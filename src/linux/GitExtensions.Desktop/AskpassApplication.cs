using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using System.Globalization;

namespace GitExtensions.Linux.Desktop;

public sealed class AskpassApplication : Application
{
    private static AskpassRequest? _request;
    private AskpassWindow? _window;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(ClassicStyles.Create());
        RequestedThemeVariant = ReadPreferredTheme();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _window = new AskpassWindow(_request ?? throw new InvalidOperationException("Missing askpass request."));
            _window.Closed += (_, _) => desktop.Shutdown(AskpassProtocol.GetExitCode(_window.Request, _window.Result));
            desktop.MainWindow = _window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static int Run(string[] arguments)
    {
        AskpassRequest? request = CreateRequest(arguments, Environment.GetEnvironmentVariable("SSH_ASKPASS_PROMPT"));
        if (request is null) { return 1; }
        _request = request;
        ApplyUiCulture();
        TextWriter output = Console.Out;
        // Framework startup and diagnostics must never enter the askpass protocol on stdout.
        Console.SetOut(TextWriter.Null);
        try
        {
            AppBuilder builder = AppBuilder.Configure<AskpassApplication>().UsePlatformDetect().WithInterFont();
            int code = builder.StartWithClassicDesktopLifetime([]);
            AskpassResult result = (builder.Instance as AskpassApplication)?._window?.Result ?? default;
            return code == 0 ? WriteResult(request, result, output) : 1;
        }
        catch
        {
            // Do not print exceptions: prompts, URLs or response data can occur in library messages.
            Console.Error.WriteLine("Unable to open the authentication dialog.");
            return 1;
        }
        finally
        {
            _request = null;
            Console.SetOut(output);
        }
    }

    internal static AskpassRequest? CreateRequest(string[] arguments, string? hint)
        => arguments.Length > 1 ? null : AskpassRequest.Create(arguments.Length == 0 ? "Enter your OpenSSH passphrase:" : arguments[0], hint);

    internal static int WriteResult(AskpassRequest request, AskpassResult result, TextWriter output)
    {
        if (!AskpassProtocol.TryGetOutput(request, result, out string response, out bool appendNewline)) { return 1; }
        output.Write(response);
        if (appendNewline) { output.Write('\n'); }
        output.Flush();
        return 0;
    }

    internal static int WriteResponse(string? response, TextWriter output)
        => WriteResult(new("", AskpassPromptKind.Secret), new(AskpassDecision.Submit, response), output);

    internal static ThemeVariant ReadPreferredTheme()
    {
        try
        {
            return new PreferencesStore().Load().Theme switch
            {
                "Dark" => ThemeVariant.Dark,
                "System" => ThemeVariant.Default,
                _ => ThemeVariant.Light
            };
        }
        catch { return ThemeVariant.Light; }
    }

    internal static void ApplyUiCulture()
    {
        string? name = Environment.GetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE");
        if (string.IsNullOrWhiteSpace(name)) { return; }
        try { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { }
    }
}

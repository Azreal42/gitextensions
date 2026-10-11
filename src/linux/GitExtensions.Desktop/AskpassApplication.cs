using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using System.Globalization;

namespace GitExtensions.Linux.Desktop;

public sealed class AskpassApplication : Application
{
    private static string _prompt = "";
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
            _window = new AskpassWindow(_prompt);
            _window.Closed += (_, _) => desktop.Shutdown(_window.Response is null ? 1 : 0);
            desktop.MainWindow = _window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static int Run(string[] arguments)
    {
        if (arguments.Length != 1)
        {
            return 1;
        }

        _prompt = arguments[0];
        ApplyUiCulture();
        TextWriter output = Console.Out;
        // Framework startup and diagnostics must never enter the askpass protocol on stdout.
        Console.SetOut(TextWriter.Null);
        try
        {
            AppBuilder builder = AppBuilder.Configure<AskpassApplication>().UsePlatformDetect().WithInterFont();
            int code = builder.StartWithClassicDesktopLifetime([]);
            string? response = (builder.Instance as AskpassApplication)?._window?.Response;
            return code == 0 ? WriteResponse(response, output) : 1;
        }
        catch
        {
            // Do not print exceptions: prompts, URLs or response data can occur in library messages.
            Console.Error.WriteLine("Unable to open the authentication dialog.");
            return 1;
        }
        finally
        {
            _prompt = "";
            Console.SetOut(output);
        }
    }

    internal static int WriteResponse(string? response, TextWriter output)
    {
        if (response is null || !AskpassWindow.IsSingleLine(response))
        {
            return 1;
        }

        output.Write(response);
        output.Write('\n');
        output.Flush();
        return 0;
    }

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

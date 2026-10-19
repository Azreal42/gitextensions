using System.Reflection;
using System.Globalization;
using Avalonia;

namespace GitExtensions.Linux.Desktop;

internal static class Program
{
    internal const string GuiHelpCommand = "help";
    internal const string ConsoleHelpOption = "--help";
    // Keep stdout's existing contract; the window projects this same public usage rather than the Windows command list.
    internal const string CommandlineUsage = "Usage: gitextensions [repository-directory] | browse [repository-directory] [--pathFilter=path] [-commit=selected[,first-selected]] | cleanup [repository-directory-or-file] | --version | --smoke-test [repository-directory]";
    internal static IReadOnlyList<string> PublicCommandlineForms { get; } = Array.AsReadOnly<string>(
        [.. CommandlineUsage["Usage: gitextensions ".Length..].Split(" | ", StringSplitOptions.None), GuiHelpCommand, ConsoleHelpOption]);
    internal static bool IsCommandlineHelpInvocation(IReadOnlyList<string> args) => args.Count == 1 && args[0] == GuiHelpCommand;
    internal static string? RepositoryPath { get; private set; }
    internal static bool SmokeTest { get; private set; }
    internal static DesktopStartupRequest StartupRequest { get; private set; } = DesktopStartupRequest.Browse;
    private static GitEnvironmentStartup? _environmentStartup;
    internal static GitEnvironmentStartup? TakeEnvironmentStartup() => Interlocked.Exchange(ref _environmentStartup, null);

    [STAThread]
    public static int Main(string[] args) => Run(args, CommandlineHelpApplication.Run);

    // The single GUI-launch seam lets dispatch be checked without starting a second application lifetime.
    internal static int Run(string[] args, Func<int> showCommandlineHelp)
    {
        ArgumentNullException.ThrowIfNull(args); ArgumentNullException.ThrowIfNull(showCommandlineHelp);
        if (args.FirstOrDefault() == InteractiveFileConsoleProtocol.Mode)
        { return InteractiveFileConsoleApplication.Run(args[1..]); }
        if (GitRebaseEditor.IsNativeEditorInvocation(args))
        {
            using LinuxClipboardProtocol? clipboardProtocol = LinuxClipboardProtocol.TryOpen();
            return NativeRebaseEditorApplication.Run(args);
        }
        if (GitRebaseEditor.TryHandle(args, out int helperCode)) { return helperCode; }
        if (args.FirstOrDefault() == "--askpass")
        {
            using LinuxClipboardProtocol? clipboardProtocol = LinuxClipboardProtocol.TryOpen();
            return AskpassApplication.Run(args[1..]);
        }
        bool cleanup = DesktopStartupArguments.IsCleanupInvocation(args);
        if (!cleanup && args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine($"Git Extensions {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
            return 0;
        }

        if (!cleanup && args.Contains(ConsoleHelpOption, StringComparer.Ordinal))
        {
            Console.WriteLine(CommandlineUsage);
            return 0;
        }

        if (IsCommandlineHelpInvocation(args))
        {
            try { return showCommandlineHelp(); }
            catch (Exception)
            { Console.Error.WriteLine("Unable to open commandline help."); return 1; }
        }

        DesktopStartupArguments startup;
        try { startup = DesktopStartupArguments.Parse(args); }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        SmokeTest = startup.SmokeTest;
        RepositoryPath = startup.Mode == DesktopStartupMode.Browse ? startup.RepositoryArgument : null;

        try
        {
            GitEnvironmentPreferencesLoadResult environment = new GitEnvironmentPreferencesStore().Load();
            GitEnvironment.Configure(GitEnvironmentSettingsSession.Options(environment.Preferences));
            DesktopLanguage.ApplyPreferredCulture();
            Exception? preparationError = null;
            try { StartupRequest = DesktopStartupDispatcher.PrepareAsync(startup, Environment.CurrentDirectory).GetAwaiter().GetResult(); }
            catch (Exception error) when (!SmokeTest && startup.Mode == DesktopStartupMode.Browse && GitEnvironmentStartup.IsExecutableUnavailable(error))
            {
                // Keep the original request for retry after the user repairs Git; never substitute an unrelated cwd repository.
                preparationError = error; RepositoryPath = null;
                StartupRequest = new(startup with { RepositoryArgument = null }, BrowseRequest: startup.BrowseRequest);
            }
            if (!SmokeTest && startup.Mode == DesktopStartupMode.Browse)
            {
                _environmentStartup = new(startup, environment.Preferences.CheckAtStartup || environment.Error is not null || preparationError is not null,
                    preparationError, Environment.CurrentDirectory);
            }
            RepositoryPath = StartupRequest.BrowseRequest?.RepositoryPath ?? RepositoryPath;
            using LinuxClipboardProtocol? clipboardProtocol = LinuxClipboardProtocol.TryOpen();
            GitRunner.AskpassFallbackPath = Path.Combine(AppContext.BaseDirectory, "gitextensions-askpass");
            GitRunner.AskpassUiCulture = CultureInfo.CurrentUICulture.Name;
            AppBuilder builder = BuildAvaloniaApp();
            try { return builder.StartWithClassicDesktopLifetime(args); }
            finally
            {
                // The UI loop has actually ended. Never create a runtime or dispatch new UI work here.
                if ((builder.Instance as App)?.ExistingAvatarRuntime is { } avatars)
                { avatars.ShutdownAsync().GetAwaiter().GetResult(); }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure(() => new App(true))
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}

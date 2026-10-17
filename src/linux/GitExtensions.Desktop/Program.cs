using System.Reflection;
using System.Globalization;
using Avalonia;

namespace GitExtensions.Linux.Desktop;

internal static class Program
{
    internal static string? RepositoryPath { get; private set; }
    internal static bool SmokeTest { get; private set; }
    internal static DesktopStartupRequest StartupRequest { get; private set; } = DesktopStartupRequest.Browse;

    [STAThread]
    public static int Main(string[] args)
    {
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

        if (!cleanup && args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("Usage: gitextensions [repository-directory] | browse [repository-directory] [--pathFilter=path] [-commit=selected[,first-selected]] | cleanup [repository-directory-or-file] | --version | --smoke-test [repository-directory]");
            return 0;
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
            DesktopLanguage.ApplyPreferredCulture();
            StartupRequest = DesktopStartupDispatcher.PrepareAsync(startup, Environment.CurrentDirectory).GetAwaiter().GetResult();
            RepositoryPath = StartupRequest.BrowseRequest?.RepositoryPath ?? RepositoryPath;
            using LinuxClipboardProtocol? clipboardProtocol = LinuxClipboardProtocol.TryOpen();
            GitRunner.AskpassFallbackPath = Path.Combine(AppContext.BaseDirectory, "gitextensions-askpass");
            GitRunner.AskpassUiCulture = CultureInfo.CurrentUICulture.Name;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}

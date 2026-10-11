using System.Reflection;
using Avalonia;

namespace GitExtensions.Linux.Desktop;

internal static class Program
{
    internal static string? RepositoryPath { get; private set; }
    internal static bool SmokeTest { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (GitRebaseEditor.TryHandle(args, out int helperCode)) { return helperCode; }
        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine($"Git Extensions {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
            return 0;
        }

        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("Usage: gitextensions [repository-directory] | --version | --smoke-test [repository-directory]");
            return 0;
        }

        SmokeTest = args.Contains("--smoke-test", StringComparer.Ordinal);
        RepositoryPath = args.FirstOrDefault(a => a != "--smoke-test");
        if (RepositoryPath?.StartsWith('-') == true)
        {
            Console.Error.WriteLine($"Unknown option: {RepositoryPath}");
            return 2;
        }

        try
        {
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

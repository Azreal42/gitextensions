namespace GitExtensions.Linux.Desktop;

internal enum DesktopStartupMode { Browse, Cleanup }

/// <summary>The public desktop argv, after the native editor and askpass protocols have been dispatched.</summary>
internal sealed record DesktopStartupArguments(DesktopStartupMode Mode, string? RepositoryArgument = null, bool SmokeTest = false)
{
    internal static bool IsCleanupInvocation(IReadOnlyList<string> arguments) => arguments.FirstOrDefault() == "cleanup";

    internal static DesktopStartupArguments Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (IsCleanupInvocation(arguments))
        {
            // GetWorkingDir takes only the command's second argv. Cleanup has no path-filter argument.
            // These values remain paths even when they resemble global flags; later argv are ignored.
            return new(DesktopStartupMode.Cleanup, arguments.Count > 1 ? arguments[1] : null);
        }

        bool smokeTest = arguments.Contains("--smoke-test", StringComparer.Ordinal);
        string? repository = arguments.FirstOrDefault(argument => argument != "--smoke-test");
        if (repository?.StartsWith('-') == true)
        {
            throw new ArgumentException($"Unknown option: {repository}");
        }
        return new(DesktopStartupMode.Browse, repository, smokeTest);
    }
}

internal sealed record DesktopStartupRequest(DesktopStartupArguments Arguments, GitRepository? Repository = null)
{
    internal static DesktopStartupRequest Browse { get; } = new(new DesktopStartupArguments(DesktopStartupMode.Browse));
}

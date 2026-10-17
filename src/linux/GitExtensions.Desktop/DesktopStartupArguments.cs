namespace GitExtensions.Linux.Desktop;

internal enum DesktopStartupMode { Browse, Cleanup }

internal enum BrowsePathKind { File, Directory }

/// <summary>A literal repository-relative path, separate from a revision expression or a saved panel preference.</summary>
internal sealed record BrowsePathFilter(string Path, BrowsePathKind? Kind = null);

/// <summary>Selection hints for an independent Browse. FirstRevision is the first selected comparison side, not a history bound.</summary>
internal sealed record BrowseRequest(string? RepositoryPath = null, BrowsePathFilter? PathFilter = null,
    GitCompareRevision? SelectedRevision = null, GitCompareRevision? FirstRevision = null);

/// <summary>The public desktop argv, after the native editor and askpass protocols have been dispatched.</summary>
internal sealed record DesktopStartupArguments(DesktopStartupMode Mode, string? RepositoryArgument = null, bool SmokeTest = false,
    BrowseRequest? BrowseRequest = null)
{
    internal static bool IsCleanupInvocation(IReadOnlyList<string> arguments) => arguments.FirstOrDefault() == "cleanup";
    internal static bool IsBrowseInvocation(IReadOnlyList<string> arguments) => arguments.FirstOrDefault() == "browse";

    internal static DesktopStartupArguments Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (IsCleanupInvocation(arguments))
        {
            // GetWorkingDir takes only the command's second argv. Cleanup has no path-filter argument.
            // These values remain paths even when they resemble global flags; later argv are ignored.
            return new(DesktopStartupMode.Cleanup, arguments.Count > 1 ? arguments[1] : null);
        }

        if (IsBrowseInvocation(arguments)) { return ParseBrowse(arguments); }

        bool smokeTest = arguments.Contains("--smoke-test", StringComparer.Ordinal);
        string? repository = arguments.FirstOrDefault(argument => argument != "--smoke-test");
        if (repository?.StartsWith('-') == true)
        {
            throw new ArgumentException($"Unknown option: {repository}");
        }
        return new(DesktopStartupMode.Browse, repository, smokeTest);
    }

    private static DesktopStartupArguments ParseBrowse(IReadOnlyList<string> arguments)
    {
        string? repository = null, path = null, commit = null;
        BrowsePathKind? kind = null;
        bool smoke = false;
        foreach (string argument in arguments.Skip(1))
        {
            if (argument.StartsWith("--pathFilter=", StringComparison.Ordinal)) { path ??= argument[13..]; }
            else if (argument.StartsWith("-pathfilter=", StringComparison.Ordinal)) { path ??= argument[12..]; }
            else if (argument.StartsWith("-commit=", StringComparison.Ordinal)) { commit ??= argument[8..]; }
            else if (argument.StartsWith("-pathfilter-kind=", StringComparison.Ordinal))
            {
                kind = argument[17..] switch
                {
                    "file" => BrowsePathKind.File,
                    "directory" => BrowsePathKind.Directory,
                    _ => throw new ArgumentException("The Browse path kind must be file or directory.")
                };
            }
            else if (argument == "--smoke-test") { smoke = true; }
            else if (argument.StartsWith('-')) { throw new ArgumentException($"Unknown Browse option: {argument}"); }
            else if (repository is null) { repository = argument; }
            else { throw new ArgumentException("Browse accepts one repository path."); }
        }
        if (kind is not null && path is null) { throw new ArgumentException("A Browse path kind requires -pathfilter."); }
        GitCompareRevision? selected = null, first = null;
        if (!string.IsNullOrEmpty(commit))
        {
            // Windows uses the first two comma-separated values and ignores any following values.
            string[] revisions = commit.Split(',', 3);
            selected = ParseBrowseRevision(revisions[0]);
            if (revisions.Length > 1) { first = ParseBrowseRevision(revisions[1]); }
        }
        BrowseRequest request = new(repository, path is null ? null : new(path, kind), selected, first);
        return new(DesktopStartupMode.Browse, repository, smoke, request);
    }

    internal static GitCompareRevision ParseBrowseRevision(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\0')) { throw new ArgumentException("Browse requires a nonempty revision."); }
        return value switch
        {
            "1111111111111111111111111111111111111111" => new("", GitCompareRevisionKind.WorkingTree),
            "2222222222222222222222222222222222222222" => new("", GitCompareRevisionKind.Index),
            _ => new(value)
        };
    }

    internal static string FormatBrowseRevision(GitCompareRevision revision) => revision.Kind switch
    {
        GitCompareRevisionKind.WorkingTree => "1111111111111111111111111111111111111111",
        GitCompareRevisionKind.Index => "2222222222222222222222222222222222222222",
        GitCompareRevisionKind.Commit when !string.IsNullOrEmpty(revision.Revision) && !revision.Revision.Contains('\0')
            && !revision.Revision.Contains(',') => revision.Revision,
        _ => throw new ArgumentException("Browse requires a valid revision kind and expression.", nameof(revision))
    };
}

internal sealed record DesktopStartupRequest(DesktopStartupArguments Arguments, GitRepository? Repository = null, BrowseRequest? BrowseRequest = null)
{
    internal static DesktopStartupRequest Browse { get; } = new(new DesktopStartupArguments(DesktopStartupMode.Browse));
}

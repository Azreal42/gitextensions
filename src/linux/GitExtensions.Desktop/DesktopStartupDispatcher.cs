using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

internal static class DesktopStartupDispatcher
{
    internal static async Task<DesktopStartupRequest> PrepareAsync(DesktopStartupArguments arguments, string currentDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        if (arguments.Mode == DesktopStartupMode.Browse)
        {
            return arguments.BrowseRequest is null ? new(arguments)
                : await PrepareBrowseAsync(arguments, currentDirectory, cancellationToken).ConfigureAwait(false);
        }
        if (arguments.Mode != DesktopStartupMode.Cleanup) { throw new ArgumentOutOfRangeException(nameof(arguments)); }

        string workingDirectory = Path.GetFullPath(currentDirectory);
        GitRepository? repository = null;
        if (!string.IsNullOrWhiteSpace(arguments.RepositoryArgument))
        {
            // Linux argv already contains the literal path: the old Windows trailing-quote workaround does not apply.
            string path = Path.GetFullPath(arguments.RepositoryArgument, workingDirectory);
            string? directory = GetPathKind(path) == PathKind.Directory ? path : Path.GetDirectoryName(path);
            if (directory is not null) { repository = await TryOpenAsync(directory, cancellationToken).ConfigureAwait(false); }
        }
        repository ??= await TryOpenAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (repository is null)
        {
            throw new InvalidOperationException("Cannot open cleanup: no Git repository was found in the supplied path or current working directory.");
        }
        return new(arguments, repository);
    }

    private static async Task<DesktopStartupRequest> PrepareBrowseAsync(DesktopStartupArguments arguments,
        string currentDirectory, CancellationToken token)
    {
        BrowseRequest request = arguments.BrowseRequest!;
        if (request.FirstRevision is not null && request.SelectedRevision is null)
        { throw new ArgumentException("The first selected Browse revision requires the last selected revision."); }
        string path = Path.GetFullPath(request.RepositoryPath ?? currentDirectory, Path.GetFullPath(currentDirectory));
        string? directory = GetPathKind(path) == PathKind.Directory ? path : Path.GetDirectoryName(path);
        GitRepository? repository = directory is null ? null : await TryOpenAsync(directory, token).ConfigureAwait(false);
        if (repository is null) { throw new InvalidOperationException("Cannot open Browse: no Git repository was found in the supplied path or current working directory."); }
        BrowsePathFilter? filter = request.PathFilter;
        if (filter is not null)
        {
            string relative = Path.IsPathRooted(filter.Path) ? Path.GetRelativePath(repository.Path, filter.Path) : filter.Path;
            if (relative.Contains('\0')) { throw new ArgumentException("A Browse path cannot contain NUL."); }
            BrowsePathKind kind = filter.Kind ?? (relative.EndsWith('/') || GetPathKind(Path.Combine(repository.Path, relative)) == PathKind.Directory
                ? BrowsePathKind.Directory : BrowsePathKind.File);
            if (!Enum.IsDefined(kind)) { throw new ArgumentException("Unknown Browse path kind."); }
            filter = new(relative, kind);
        }
        GitCompareRevision? selected = await ResolveAsync(request.SelectedRevision).ConfigureAwait(false);
        GitCompareRevision? first = await ResolveAsync(request.FirstRevision).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        BrowseRequest prepared = request with { RepositoryPath = repository.Path, PathFilter = filter, SelectedRevision = selected, FirstRevision = first };
        return new(arguments with { RepositoryArgument = repository.Path }, repository, prepared);

        async Task<GitCompareRevision?> ResolveAsync(GitCompareRevision? revision)
        {
            if (revision is null) { return null; }
            _ = DesktopStartupArguments.FormatBrowseRevision(revision);
            return revision.Kind == GitCompareRevisionKind.Commit
                ? revision with { Revision = await repository.ResolveRevisionAsync(revision.Revision, token).ConfigureAwait(false) }
                : revision;
        }
    }

    private static async Task<GitRepository?> TryOpenAsync(string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string? candidate = directory;
        // TryFindGitWorkingDir also climbs past nonexistent file parents, rather than selecting an unrelated cwd repository.
        while (candidate is not null && GetPathKind(candidate) != PathKind.Directory)
        {
            token.ThrowIfCancellationRequested();
            candidate = Path.GetDirectoryName(candidate);
        }
        if (candidate is null) { return null; }
        try { return await GitRepository.OpenAsync(candidate, token).ConfigureAwait(false); }
        catch (GitCommandException error) when (error.Result.ExitCode == 128
            && error.Result.StandardError.StartsWith("fatal: not a git repository", StringComparison.Ordinal))
        {
            // As in GetWorkingDir, a non-repository explicit path falls back to the invoking cwd.
            // Filesystem probe errors, launch failures and cancellation are not classified as a missing path.
            return null;
        }
    }

    private enum PathKind { Missing, File, Directory }

    private static PathKind GetPathKind(string path)
    {
        // Directory.Exists also returns false on access/IO failures, which must not choose a parent or the cwd repository.
        try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0 ? PathKind.Directory : PathKind.File; }
        catch (FileNotFoundException) { return PathKind.Missing; }
        catch (DirectoryNotFoundException) { return PathKind.Missing; }
    }

    internal static Window CreateMainWindow(DesktopStartupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Arguments.Mode == DesktopStartupMode.Browse)
        { return request.BrowseRequest is null ? new MainWindow() : new MainWindow(request.BrowseRequest); }
        if (request.Arguments.Mode != DesktopStartupMode.Cleanup) { throw new ArgumentOutOfRangeException(nameof(request)); }
        GitRepository repository = request.Repository ?? throw new InvalidOperationException("Cleanup repository discovery must complete before creating the window.");
        CleanupRepositoryDialog dialog = new(repository, path: null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            RequestedThemeVariant = AskpassApplication.ReadPreferredTheme()
        };
        bool initialized = false;
        dialog.Opened += async (_, _) =>
        {
            if (initialized || !dialog.IsVisible) { return; }
            initialized = true;
            await dialog.InitializeAsync();
        };
        return dialog;
    }
}

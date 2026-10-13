using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

internal static class DesktopStartupDispatcher
{
    internal static async Task<DesktopStartupRequest> PrepareAsync(DesktopStartupArguments arguments, string currentDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        if (arguments.Mode == DesktopStartupMode.Browse) { return new(arguments); }
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
        if (request.Arguments.Mode == DesktopStartupMode.Browse) { return new MainWindow(); }
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

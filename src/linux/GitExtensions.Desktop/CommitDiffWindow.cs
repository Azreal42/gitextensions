using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

internal sealed record ResolvedCommitDiffSource(GitRepository Repository, GitCommit Commit);

/// <summary>One actual commit, independently resolved from the grid's potentially filtered parent list.</summary>
internal static class CommitDiffWindow
{
    internal static async Task<MainWindow> CreateAsync(GitRepository repository, string revision, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        string oid = await repository.ResolveRevisionAsync(revision, token);
        GitCommitDetails details = await repository.GetCommitDetailsAsync(oid, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        return new(new BrowseRequest(repository.Path), MainWindowKind.CommitDiff, new(repository, ToCommit(details)));
    }

    internal static GitCommit ToCommit(GitCommitDetails details) => new(details.Hash, details.Parents,
        details.Author.Name, details.Author.Email, details.Author.Date, details.Subject, details.Body) { FullMessage = details.Message };

    internal static async Task<bool> ShowDialogAsync(Window owner, GitRepository repository, string revision, CancellationToken token)
    {
        MainWindow window = await CreateAsync(repository, revision, token);
        window.RequestedThemeVariant = owner.ActualThemeVariant;
        using CancellationTokenRegistration close = token.Register(() => Dispatcher.UIThread.Post(window.Close));
        try { token.ThrowIfCancellationRequested(); await window.ShowDialog(owner); token.ThrowIfCancellationRequested(); }
        finally { window.Close(); await window.WindowWorkCompletion; }
        return window.CommitDiffHasRepositoryChanges;
    }
}

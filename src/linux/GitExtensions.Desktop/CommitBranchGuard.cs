using Avalonia.Controls;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>FormCommit's detached-HEAD decision, shared by both commit hosts.</summary>
internal static class CommitBranchGuard
{
    internal static async Task<bool> EnsureAsync(Window owner, GitRepository repository, CommitPreferencesStore preferences,
        TranslationCatalog translations, CancellationToken token, Func<Task<bool>> create, Func<Task<bool>> checkout)
    {
        token.ThrowIfCancellationRequested();
        GitStatus status = await repository.GetStatusAsync(token);
        if (!owner.IsVisible) { return false; }
        if (preferences.Load().DontConfirmCommitIfNoBranch || !status.IsDetached
            || await IsRebaseInProgressAsync(repository, token)) { return true; }
        CommitBranchChoiceDialog prompt = new(translations) { RequestedThemeVariant = owner.ActualThemeVariant };
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(prompt.Close));
        CommitBranchChoice choice;
        try { choice = await prompt.ShowDialog<CommitBranchChoice>(owner); token.ThrowIfCancellationRequested(); }
        finally { prompt.Close(); }
        if (!owner.IsVisible) { return false; }
        return choice switch
        {
            CommitBranchChoice.Continue => true,
            CommitBranchChoice.Create => await create(),
            CommitBranchChoice.Checkout => await checkout(),
            _ => false
        };
    }

    internal static async Task<bool> IsRebaseInProgressAsync(GitRepository repository, CancellationToken token)
    {
        // Module.InTheMiddleOfRebase excludes git-am's applying marker.
        foreach (string name in new[] { "rebase-merge", "rebase-apply", "rebase" })
        {
            string directory = await GitPathAsync(repository, name, token);
            if (Directory.Exists(directory)) { return !File.Exists(Path.Combine(directory, "applying")); }
        }
        return false;
    }

    internal static async Task<bool> IsPatchOrRebaseInProgressAsync(GitRepository repository, CancellationToken token)
    {
        // The resolver offers Commit only outside both operations, including git-am.
        foreach (string name in new[] { "rebase-merge", "rebase-apply", "rebase" })
        { if (Directory.Exists(await GitPathAsync(repository, name, token))) { return true; } }
        return false;
    }

    private static async Task<string> GitPathAsync(GitRepository repository, string name, CancellationToken token)
    {
        GitResult result = await repository.RunAsync(["rev-parse", "--git-path", name], token);
        return Path.GetFullPath(result.StandardOutput.TrimEnd('\r', '\n'), repository.Path);
    }
}

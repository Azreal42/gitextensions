using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

internal sealed class RevertRevisionPicker : ChooseCommitDialog
{
    private RevertRevisionPicker(GitRepository repository, string? initial, CancellationToken token)
        : base(repository, new(initial), new("RevertChooseRevision", "ChooseRevertCommit", "CancelRevertChoice", "RevertRevisionSearch", "SearchRevertRevision"), token) { }

    internal static Task<GitCommit?> ShowAsync(Window owner, GitRepository repository, string? initial, CancellationToken token)
        => ShowPickerAsync(new RevertRevisionPicker(repository, initial, token), owner, token);
}

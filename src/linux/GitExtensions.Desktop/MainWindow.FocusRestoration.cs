using Avalonia.Controls;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private sealed record OperationFocus(GitRepository? Repository, Control? Input, string? CommitHash, bool PreserveUnselectedRow = false);
    private OperationFocus? _revisionActionFocus;

    private async Task WithRevisionActionFocusAsync(Func<Task> action)
    {
        OperationFocus origin = new(_repository, null, (_commits.SelectedItem as GitCommit)?.Hash);
        _revisionActionFocus = origin;
        try { await action(); }
        finally { if (ReferenceEquals(_revisionActionFocus, origin)) { _revisionActionFocus = null; } }
    }

    private Task WithMenuActionFocusAsync(MenuItem item, Func<Task> action)
    {
        // Membership survives popup dismissal. Shared main-menu and toolbar
        // actions keep their own input origin.
        bool Contains(IEnumerable<MenuItem> items) => items.Any(candidate => ReferenceEquals(candidate, item) || Contains(candidate.Items.OfType<MenuItem>()));
        return _revisionContext is { } menu && Contains(menu.Items.OfType<MenuItem>())
            ? WithRevisionActionFocusAsync(action) : action();
    }

    private OperationFocus? CaptureOperationFocus()
    {
        // A context action runs while focus belongs to its popup, not the grid.
        if (_revisionActionFocus is { } origin)
        {
            // One operation owns this origin. Do not lend it to a subsequent
            // operation while its asynchronous panel continuation is pending.
            _revisionActionFocus = null;
            if (origin.Repository == _repository) { return origin; }
        }
        if (FocusManager?.GetFocusedElement() is not Control input || input is Window || TopLevel.GetTopLevel(input) != this) { return null; }
        GitCommit? commit = input.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
            .FirstOrDefault(row => row.FindAncestorOfType<ListBox>() == _commits)?.DataContext as GitCommit;
        if (commit is null && _commits.IsKeyboardFocusWithin) { commit = _commits.SelectedItem as GitCommit; }
        return new(_repository, input, commit?.Hash);
    }

    private void RestoreOperationFocus(OperationFocus? previous)
    {
        if (previous is null || _repository != previous.Repository || _lifetime.IsCancellationRequested || _busy
            || !IsVisible || !IsEffectivelyEnabled || OwnedWindows.Any(window => window.IsVisible)) { return; }
        if (FocusManager?.GetFocusedElement() is Control current && current is not Window
            && current.Focusable && current.IsEffectivelyEnabled && current.IsEffectivelyVisible
            && TopLevel.GetTopLevel(current) is { IsVisible: true }) { return; }
        Control? target = previous.Input;
        if (previous.CommitHash is string hash)
        {
            // Refresh replaces commit objects and row containers. Preserve the
            // current selection. Explicit F5 also preserves an unselected caret
            // row; other operations resolve only an origin still selected.
            if (!previous.PreserveUnselectedRow && _commits.SelectedItems?.OfType<GitCommit>().Any(commit => commit.Hash == hash) != true) { return; }
            GitCommit? commit = _commits.Items.OfType<GitCommit>().FirstOrDefault(item => item.Hash == hash);
            if (commit is null || TopLevel.GetTopLevel(_commits) != this) { return; }
            _commits.ScrollIntoView(commit); _commits.UpdateLayout();
            target = _commits.ContainerFromIndex(_commits.Items.IndexOf(commit));
        }
        if (target is { Focusable: true, IsEffectivelyEnabled: true, IsEffectivelyVisible: true }
            && TopLevel.GetTopLevel(target) == this) { target.Focus(); }
    }
}

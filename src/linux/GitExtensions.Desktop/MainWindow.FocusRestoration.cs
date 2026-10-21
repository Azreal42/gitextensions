using Avalonia.Controls;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private sealed record OperationFocus(GitRepository? Repository, Control? Input, string? HistoryKey, bool PreserveUnselectedRow = false, bool PreserveHistorySelection = false);
    private OperationFocus? _revisionActionFocus;
    // Only the admitted busy operation owns this intention; asynchronous tails
    // cannot lend it to a later F5 or a different repository operation.
    private OperationFocus? _operationHistoryFocus;

    private OperationFocus CurrentHistoryOperationFocus(OperationFocus origin)
    {
        if (!origin.PreserveHistorySelection || !ReferenceEquals(origin.Repository, _repository)) { return origin; }
        // The command's captured target is immutable, but the user's latest
        // surviving selection/caret owns the eventual return from that command.
        object? current = RevisionContextItem;
        return origin with { Input = null, HistoryKey = current is null ? null : HistoryRevisionItems.Key(current) };
    }
    private object? _revisionActionItem;

    private async Task WithRevisionActionFocusAsync(Func<Task> action)
    {
        object? previous = _revisionActionItem, target = RevisionContextItem;
        OperationFocus origin = new(_repository, null, target is null ? null : HistoryRevisionItems.Key(target), PreserveHistorySelection: true);
        _revisionActionItem = target;
        _revisionActionFocus = origin;
        try { await action(); }
        finally
        {
            if (ReferenceEquals(_revisionActionFocus, origin)) { _revisionActionFocus = null; }
            if (ReferenceEquals(_revisionActionItem, target)) { _revisionActionItem = previous; }
        }
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
        object? item = input.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
            .FirstOrDefault(row => row.FindAncestorOfType<ListBox>() == _commits)?.DataContext;
        item = item is GitCommit or ArtificialHistoryRevision ? item : _commits.IsKeyboardFocusWithin ? PrimaryHistoryItem : null;
        return new(_repository, input, item is null ? null : HistoryRevisionItems.Key(item));
    }

    private void RestoreOperationFocus(OperationFocus? previous)
    {
        if (previous is null || _repository != previous.Repository || _lifetime.IsCancellationRequested || _busy
            || !IsVisible || !IsEffectivelyEnabled || OwnedWindows.Any(window => window.IsVisible)) { return; }
        if (FocusManager?.GetFocusedElement() is Control current && current is not Window
            && current.Focusable && current.IsEffectivelyEnabled && current.IsEffectivelyVisible
            && TopLevel.GetTopLevel(current) is { IsVisible: true }) { return; }
        Control? target = previous.Input;
        if (previous.HistoryKey is string hash)
        {
            // Refresh replaces commit objects and row containers. Preserve the
            // current selection. Explicit F5 also preserves an unselected caret
            // row; other operations resolve only an origin still selected.
            if (!previous.PreserveUnselectedRow && _commits.SelectedItems?.Cast<object>().Any(item => HistoryRevisionItems.Key(item) == hash) != true) { return; }
            object? commit = HistoryItem(hash);
            if (commit is null || TopLevel.GetTopLevel(_commits) != this) { return; }
            _commits.ScrollIntoView(commit); _commits.UpdateLayout();
            target = _commits.ContainerFromIndex(_commits.Items.IndexOf(commit));
        }
        if (target is { Focusable: true, IsEffectivelyEnabled: true, IsEffectivelyVisible: true }
            && TopLevel.GetTopLevel(target) == this) { target.Focus(); }
    }
}

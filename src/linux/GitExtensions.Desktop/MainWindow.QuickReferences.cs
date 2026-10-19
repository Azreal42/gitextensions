using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly BrowserRead _quickReferenceRead = new();

    private async Task ExecuteQuickReferenceActionAsync(QuickGitRefAction action)
    {
        if (_busy || _quickReferenceRead.Current is not null || _lifetime.IsCancellationRequested
            || _repository is not GitRepository repository || QuickReferenceCommit() is not GitCommit commit) { return; }
        GitReference? reference = await ChooseQuickReferenceAsync(repository, commit, action);
        if (reference is null || _repository != repository || _busy || _lifetime.IsCancellationRequested) { return; }
        try
        {
            if (action == QuickGitRefAction.Rename) { await RenameRevisionBranchAsync(repository, reference); }
            else if (action == QuickGitRefAction.Delete) { await DeleteRevisionBranchAsync(repository, reference); }
        }
        finally
        {
            if (_repository == repository && !_busy && !_lifetime.IsCancellationRequested) { RestoreQuickReferenceFocus(commit); }
        }
    }

    private GitCommit? QuickReferenceCommit()
        => (FocusManager?.GetFocusedElement() as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
            .FirstOrDefault(row => row.IsSelected)?.DataContext as GitCommit ?? _commits.SelectedItem as GitCommit;

    private void RestoreQuickReferenceFocus(GitCommit commit)
    {
        int index = _commits.Items.IndexOf(commit);
        if (index >= 0 && _commits.ContainerFromIndex(index) is Control row) { row.Focus(); }
        else { _commits.Focus(); }
    }

    private async Task<GitReference?> ChooseQuickReferenceAsync(GitRepository repository, GitCommit commit, QuickGitRefAction action)
    {
        string hash = commit.Hash;
        using CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _quickReferenceRead.Current = read;
        QuickGitRefSelectorDialog? dialog = null;
        bool Current() => !read.IsCancellationRequested && _repository == repository && !_busy
            && _commits.SelectedItems?.OfType<GitCommit>().Any(selected => selected.Hash == hash) == true;
        bool Eligible(GitReference reference) => action == QuickGitRefAction.Rename ? reference.IsBranch
            : reference.IsTag || reference.IsRemote || reference.IsBranch && !reference.IsCurrent;
        try
        {
            LastException = null;
            GitReference[] choices = (await repository.GetQuickActionReferencesAsync(hash, read.Token)).Where(Eligible).ToArray();
            if (!Current() || choices.Length == 0) { return null; }
            // RevisionGridControl bypasses the chooser for one reference. Both
            // paths still open the existing action dialog before any mutation.
            if (choices.Length == 1) { return choices[0]; }
            dialog = new(action, choices, _translations) { RequestedThemeVariant = ActualThemeVariant };
            dialog.Position = QuickReferenceLocation(dialog, commit);
            dialog.Opened += (sender, _) =>
            {
                if (sender is Window shown) { shown.Position = QuickReferenceLocation(shown, commit); }
            };
            using CancellationTokenRegistration close = read.Token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
            await dialog.ShowDialog(this);
            if (!Current() || !dialog.WasAccepted || dialog.SelectedReference is not GitReference picked) { return null; }
            GitReference? current = (await repository.GetQuickActionReferencesAsync(hash, read.Token))
                .FirstOrDefault(reference => reference.Name == picked.Name && reference.Hash == picked.Hash && Eligible(reference));
            if (!Current()) { return null; }
            if (current is null) { _state.Text = T("The selected reference changed. Try again."); }
            return current;
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { return null; }
        finally
        {
            dialog?.Close();
            if (_quickReferenceRead.Current == read) { _quickReferenceRead.Current = null; }
            if (dialog is not null && Current()) { RestoreQuickReferenceFocus(commit); }
        }
    }

    private PixelPoint QuickReferenceLocation(Window dialog, GitCommit commit)
        => FitQuickSelectorLocation(this, dialog, QuickReferenceAnchor(commit));

    private PixelPoint QuickReferenceAnchor(GitCommit commit, bool scrollIntoView = true)
    {
        if (scrollIntoView) { _commits.ScrollIntoView(commit); }
        _commits.UpdateLayout();
        Control edge = _historyGraph.IsEffectivelyVisible ? _historyGraph : _commits;
        Point bottom = _commits.ContainerFromIndex(_commits.Items.IndexOf(commit)) is Control row
            ? row.TranslatePoint(new Point(0, row.Bounds.Height), edge) ?? default : default;
        PixelPoint location = edge.PointToScreen(new Point(edge == _historyGraph ? edge.Bounds.Width : 0, bottom.Y));
        return location;
    }

    internal static PixelPoint FitQuickSelectorLocation(Window owner, Window dialog, PixelPoint location)
    {
        // Shared by reference actions and script selectors; no second screen-fitting algorithm.
        if (owner.Screens.ScreenFromPoint(location) is { WorkingArea: var area } screen)
        {
            int width = (int)Math.Ceiling(dialog.Width * screen.Scaling), height = (int)Math.Ceiling(dialog.Height * screen.Scaling);
            location = new(Math.Clamp(location.X, area.X, Math.Max(area.X, area.Right - width)),
                Math.Clamp(location.Y, area.Y, Math.Max(area.Y, area.Bottom - height)));
        }
        return location;
    }
}

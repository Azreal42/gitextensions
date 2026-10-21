using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

internal sealed record ChooseCommitSelection
{
    internal GitCompareRevision Revision { get; }
    internal GitCommit? Commit { get; }
    private ChooseCommitSelection(GitCompareRevision revision, GitCommit? commit) { Revision = revision; Commit = commit; }
    internal static ChooseCommitSelection FromCommit(GitCommit commit) => new(new(commit.Hash), commit);
    internal static ChooseCommitSelection FromArtificial(GitCompareRevisionKind kind)
        => kind is GitCompareRevisionKind.Index or GitCompareRevisionKind.WorkingTree ? new(new("", kind), null)
            : throw new ArgumentOutOfRangeException(nameof(kind));
}

/// <summary>One typed selection across the existing real-commit grid and optional artificial rows.</summary>
internal sealed class ChooseCommitSelectionPresenter : UserControl
{
    private readonly ExportRevisionList _commits;
    private readonly ArtificialRevisionRows? _artificial;
    private bool _selecting;
    private string? _head;
    internal ListBox Rows => _commits.Rows;
    internal bool IsPublishing => _commits.IsPublishing;
    internal ChooseCommitSelection? Selection { get; private set; }
    internal event EventHandler? SelectionChanged;
    internal event EventHandler? AcceptRequested;

    internal ChooseCommitSelectionPresenter(string rowsName, bool showArtificial, Func<string, string> translate, HistoryViewPreferences? view = null)
    {
        view ??= new HistoryViewPreferencesStore().Load();
        if (showArtificial)
        {
            _artificial = ArtificialRevisionRows.Create(DesktopFontMetrics.GraphRowHeight(DesktopFonts.Active), translate, "Choose",
                graphWidth: view.ShowGraph ? RevisionHistoryColumns.GraphWidth : 0);
            _artificial.View.IsVisible = false; _artificial.WorkingState.IsVisible = _artificial.IndexState.IsVisible = false;
            Configure(_artificial.Working, GitCompareRevisionKind.WorkingTree); Configure(_artificial.Index, GitCompareRevisionKind.Index);
        }
        _commits = new(rowsName, false, _artificial?.View, view); Content = _commits;
        Rows.SelectionChanged += (_, _) => { if (!_selecting && !_commits.IsPublishing) { Publish(Rows.SelectedItem is GitCommit commit ? ChooseCommitSelection.FromCommit(commit) : null); } };
        Rows.DoubleTapped += (_, args) =>
        {
            if (args.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().Any())
            { args.Handled = true; AcceptRequested?.Invoke(this, EventArgs.Empty); }
        };
        Rows.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Up && args.KeyModifiers == KeyModifiers.None && Rows.SelectedIndex == 0 && ArtificialVisible)
            { args.Handled = true; Select(new("", GitCompareRevisionKind.Index)); FocusSelected(); }
        }, RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += (_, _) => UpdateAppearance();
        Rows.GotFocus += (_, args) =>
        {
            // RunAsync restores focus programmatically after re-enabling inputs; it must not replace a typed selection.
            if (args.Source == Rows && args.NavigationMethod == NavigationMethod.Tab
                && !_selecting && Selection?.Commit is null && Rows.Items.OfType<GitCommit>().FirstOrDefault() is GitCommit first)
            { Select(new(first.Hash)); }
            else { UpdateAppearance(); }
        };
        Rows.LostFocus += (_, _) => UpdateAppearance();
    }

    private bool ArtificialVisible => _artificial?.View.IsVisible == true;
    internal IReadOnlyList<GitCompareRevision> Parents => Selection?.Revision.Kind switch
    {
        GitCompareRevisionKind.WorkingTree => [new("", GitCompareRevisionKind.Index)],
        GitCompareRevisionKind.Index => _head is null ? [] : [new(_head)],
        _ => Selection?.Commit?.Parents.Take(2).Select(hash => new GitCompareRevision(hash)).ToArray() ?? []
    };

    internal void SetData(IReadOnlyList<GitCommit> commits, IReadOnlyList<GitReference> references, string? head, bool artificialAllowed)
    {
        _head = head;
        if (_artificial is not null) { _artificial.View.IsVisible = artificialAllowed; }
        _selecting = true;
        try { _commits.SetData(commits, references, head); }
        finally { _selecting = false; }
        Publish(null);
    }

    internal void BeginIncrementalData(IReadOnlyList<GitReference> references, string? head, bool artificialAllowed, bool onlyFirstParent = false)
    {
        _head = head;
        if (_artificial is not null) { _artificial.View.IsVisible = artificialAllowed; }
        _selecting = true;
        try { _commits.BeginIncrementalData(references, head, onlyFirstParent); }
        finally { _selecting = false; }
        Publish(null);
    }

    internal IReadOnlyList<GitCommit> AppendCommits(IReadOnlyList<GitCommit> batch) => _commits.AppendCommits(batch);
    internal void CompleteCommits() => _commits.CompleteCommits();

    internal bool Select(GitCompareRevision? revision)
    {
        if (revision is null) { return false; }
        ChooseCommitSelection? selection;
        if (revision.Kind == GitCompareRevisionKind.Commit)
        {
            GitCommit? commit = Rows.Items.OfType<GitCommit>().FirstOrDefault(item => item.Hash == revision.Revision);
            if (commit is null) { return false; }
            selection = ChooseCommitSelection.FromCommit(commit);
        }
        else
        {
            if (!ArtificialVisible || revision.Kind is not (GitCompareRevisionKind.Index or GitCompareRevisionKind.WorkingTree)) { return false; }
            selection = ChooseCommitSelection.FromArtificial(revision.Kind);
        }
        _selecting = true;
        try { Rows.SelectedItem = selection.Commit; }
        finally { _selecting = false; }
        Publish(selection); if (selection.Commit is not null) { Rows.ScrollIntoView(selection.Commit); } return true;
    }

    internal void SelectFirst()
    {
        if (ArtificialVisible) { Select(new("", GitCompareRevisionKind.WorkingTree)); }
        else if (Rows.Items.OfType<GitCommit>().FirstOrDefault() is GitCommit commit) { Select(new(commit.Hash)); }
        else { Publish(null); }
    }

    internal void FocusSelected()
    {
        if (Selection?.Revision.Kind == GitCompareRevisionKind.WorkingTree && ArtificialVisible) { _artificial!.Working.Focus(); }
        else if (Selection?.Revision.Kind == GitCompareRevisionKind.Index && ArtificialVisible) { _artificial!.Index.Focus(); }
        else { Rows.Focus(); }
    }

    private void Configure(Button button, GitCompareRevisionKind kind)
    {
        button.Click += (_, _) => { Select(new("", kind)); };
        button.DoubleTapped += (_, args) => { Select(new("", kind)); args.Handled = true; AcceptRequested?.Invoke(this, EventArgs.Empty); };
        button.GotFocus += (_, args) =>
        {
            if (args.Source == button && args.NavigationMethod == NavigationMethod.Tab
                && ArtificialVisible && Selection?.Revision.Kind != kind) { Select(new("", kind)); }
            else { UpdateAppearance(); }
        };
        button.LostFocus += (_, _) => UpdateAppearance();
        button.KeyDown += (_, args) =>
        {
            if (args.Handled || args.KeyModifiers != KeyModifiers.None || args.Key is not (Key.Up or Key.Down)) { return; }
            args.Handled = true;
            if (args.Key == Key.Up)
            {
                if (kind == GitCompareRevisionKind.Index) { Select(new("", GitCompareRevisionKind.WorkingTree)); }
            }
            else if (kind == GitCompareRevisionKind.WorkingTree) { Select(new("", GitCompareRevisionKind.Index)); }
            else if (Rows.Items.OfType<GitCommit>().FirstOrDefault() is GitCommit first) { Select(new(first.Hash)); }
            FocusSelected();
        };
    }

    private void Publish(ChooseCommitSelection? selection)
    { Selection = selection; UpdateAppearance(); SelectionChanged?.Invoke(this, EventArgs.Empty); }

    private void UpdateAppearance()
    {
        if (_artificial is null) { return; }
        foreach ((Button button, GitCompareRevisionKind kind) in new[] { (_artificial.Working, GitCompareRevisionKind.WorkingTree), (_artificial.Index, GitCompareRevisionKind.Index) })
        {
            bool selected = Selection?.Revision.Kind == kind;
            if (selected)
            {
                button.Background = RevisionHistoryRow.BackgroundFor(null, true, button.IsKeyboardFocusWithin, null, 0, false, ActualThemeVariant == ThemeVariant.Dark);
                if (button.Content is TextBlock text) { text.Foreground = ActualThemeVariant == ThemeVariant.Dark ? Brush.Parse("#dedede") : button.IsKeyboardFocusWithin ? Brushes.White : Brushes.Black; }
            }
            else { button.ClearValue(BackgroundProperty); if (button.Content is TextBlock text) { text.ClearValue(TextBlock.ForegroundProperty); } }
        }
    }
}

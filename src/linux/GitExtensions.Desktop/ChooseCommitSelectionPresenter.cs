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

/// <summary>One native list and one typed selection for commits and optional comparison endpoints.</summary>
internal sealed class ChooseCommitSelectionPresenter : UserControl
{
    private readonly ExportRevisionList _commits;
    private readonly bool _showArtificial;
    private bool _selecting;
    private string? _head;
    internal ListBox Rows => _commits.Rows;
    internal bool IsPublishing => _commits.IsPublishing;
    internal bool HasPendingArtificials => _commits.HasPendingArtificials;
    internal ChooseCommitSelection? Selection { get; private set; }
    internal event EventHandler? SelectionChanged;
    internal event EventHandler? AcceptRequested;

    internal ChooseCommitSelectionPresenter(string rowsName, bool showArtificial, Func<string, string> translate,
        HistoryViewPreferences? view = null, bool showArtificialStatus = true)
    {
        view ??= new HistoryViewPreferencesStore().Load();
        _showArtificial = showArtificial && view.ShowArtificialCommits;
        _commits = new(rowsName, false, view: view, showArtificialStatus: showArtificialStatus); Content = _commits;
        KeyboardNavigation.SetTabNavigation(Rows, KeyboardNavigationMode.Once);
        Rows.SelectionChanged += (_, _) =>
        {
            if (!_selecting && !_commits.IsPublishing)
            {
                Publish(Rows.SelectedItem switch
                {
                    GitCommit commit => ChooseCommitSelection.FromCommit(commit),
                    ArtificialHistoryRevision artificial => ChooseCommitSelection.FromArtificial(artificial.Kind),
                    _ => null
                });
            }
        };
        Rows.DoubleTapped += (_, args) =>
        {
            if (args.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().Any())
            { args.Handled = true; AcceptRequested?.Invoke(this, EventArgs.Empty); }
        };
    }

    internal IReadOnlyList<GitCompareRevision> Parents => Selection?.Revision.Kind switch
    {
        GitCompareRevisionKind.WorkingTree => [new("", GitCompareRevisionKind.Index)],
        GitCompareRevisionKind.Index => _head is null ? [] : [new(_head)],
        _ => Selection?.Commit?.Parents.Take(2).Select(hash => new GitCompareRevision(hash)).ToArray() ?? []
    };

    internal void SetData(IReadOnlyList<GitCommit> commits, IReadOnlyList<GitReference> references, string? head,
        bool artificialAllowed, IReadOnlyList<string>? headAncestors = null)
    {
        _head = head;
        _selecting = true;
        try { _commits.SetData(commits, references, head, _showArtificial && artificialAllowed, headAncestors); }
        finally { _selecting = false; }
        Publish(null);
    }

    internal void BeginIncrementalData(IReadOnlyList<GitReference> references, string? head, bool artificialAllowed, bool onlyFirstParent = false)
    {
        _head = head;
        _selecting = true;
        try { _commits.BeginIncrementalData(references, head, onlyFirstParent, _showArtificial && artificialAllowed); }
        finally { _selecting = false; }
        Publish(null);
    }

    internal IReadOnlyList<GitCommit> AppendCommits(IReadOnlyList<GitCommit> batch) => _commits.AppendCommits(batch);
    internal void CompleteCommits(IReadOnlyList<string>? headAncestors = null) => _commits.CompleteCommits(headAncestors);

    internal bool Select(GitCompareRevision? revision)
    {
        if (revision is null) { return false; }
        object? item = Rows.Items.Cast<object>().FirstOrDefault(item => HistoryRevisionItems.Revision(item) == revision);
        if (item is null) { return false; }
        _selecting = true;
        try { Rows.SelectedItem = item; }
        finally { _selecting = false; }
        Publish(item is GitCommit commit ? ChooseCommitSelection.FromCommit(commit)
            : ChooseCommitSelection.FromArtificial(((ArtificialHistoryRevision)item).Kind));
        Rows.ScrollIntoView(item);
        return true;
    }

    internal void SelectFirst()
    {
        if (Rows.Items.FirstOrDefault() is object first) { Select(HistoryRevisionItems.Revision(first)); }
        else { Publish(null); }
    }

    internal void FocusSelected()
    {
        if (Rows.SelectedItem is object item) { Rows.ScrollIntoView(item); }
        Rows.Focus();
    }

    private void Publish(ChooseCommitSelection? selection)
    { Selection = selection; SelectionChanged?.Invoke(this, EventArgs.Empty); }
}

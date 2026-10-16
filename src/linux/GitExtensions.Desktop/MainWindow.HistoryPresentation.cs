using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private string? _configuredUserEmail, _authoredEmail;
    private Control? _virtualRevisions;
    private Button? _workingRevision, _indexRevision;
    private TextBlock? _workingRevisionState, _indexRevisionState;

    private void UpdateHistoryPresentation()
    {
        if (_refreshing) { return; }
        GitCommit[] selected = _commits.SelectedItems?.OfType<GitCommit>().ToArray() ?? [];
        // AuthorRevisionHighlighting: one selected author, keep it on multiselection,
        // and fall back to the configured user only when selection is empty.
        if (selected.Length == 1) { _authoredEmail = selected[0].AuthorEmail; }
        else if (selected.Length == 0) { _authoredEmail = _configuredUserEmail; }
        string? revision = selected.FirstOrDefault()?.Hash;
        if (!ReferenceEquals(_relationshipSource, _history) || _relationshipRevision != revision)
        {
            _relationshipSource = _history; _relationshipRevision = revision;
            _relatedHistory = RelatedHistory(revision, true);
        }
        _historyGraph.RelatedHashes = _highlightedHistoryBranch ?? (_historyView.DrawNonRelativesGray ? _relatedHistory : null);
        _historyGraph.IsVisible = _historyView.ShowGraph;
        _historyGraph.SelectedHashes = selected.Select(commit => commit.Hash).ToArray();
        _historyGraph.SelectionFocused = _commits.IsKeyboardFocusWithin;
        _historyGraph.AuthoredEmail = _authoredEmail;
        foreach (Control row in _commits.GetVisualDescendants().OfType<Control>().Where(control => control.Name == "RevisionHistoryRow")) { UpdateHistoryRow(row); }
    }

    private void UpdateHistoryRow(Control row)
    {
        if (row.Tag is not GitCommit commit) { return; }
        int index = 0;
        foreach (GitCommit entry in _historyGraph.OrderedCommits)
        { if (entry.Hash == commit.Hash) { break; } index++; }
        bool selected = _commits.SelectedItems?.OfType<GitCommit>().Any(entry => entry.Hash == commit.Hash) == true;
        RevisionHistoryRow.UpdateState(row, selected, _commits.IsKeyboardFocusWithin, _authoredEmail, index);
        ApplyHistoryRowOptions(row, commit);
    }

    private Control BuildVirtualRevisionRows()
    {
        Grid rows = new() { Name = "WorkingRevisions", ColumnDefinitions = new ColumnDefinitions("20,*"), RowDefinitions = new RowDefinitions("20,20") };
        // These are actual worktree/index actions, outside the commit model.
        for (int i = 0; i < 2; i++)
        {
            Canvas graph = new() { Width = 20, Height = 20 };
            graph.Children.Add(new Avalonia.Controls.Shapes.Line { StartPoint = new Point(8, 0), EndPoint = new Point(8, 20), Stroke = Brush.Parse("#d3d3d3"), StrokeThickness = 1.5 });
            Avalonia.Controls.Shapes.Ellipse node = new() { Width = 7, Height = 7, Fill = Brush.Parse("#d3d3d3") };
            Canvas.SetLeft(node, 4.5); Canvas.SetTop(node, 6.5); graph.Children.Add(node);
            Grid.SetRow(graph, i); rows.Children.Add(graph);
            Button action = new() { Name = i == 0 ? "WorkingDirectoryRevision" : "IndexRevision", Height = 20, MinHeight = 20,
                MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 0), Margin = new Thickness(2, 0, 0, 0) };
            action.Content = new TextBlock { Text = T(i == 0 ? "Working directory" : "Commit index"), VerticalAlignment = VerticalAlignment.Center };
            TextBlock state = new() { Text = "✓", Foreground = Brush.Parse("#24c221"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
            StackPanel content = new() { Orientation = Orientation.Horizontal, Height = 20, Children = { action, state } };
            Grid.SetRow(content, i); Grid.SetColumn(content, 1); rows.Children.Add(content);
            bool staged = i == 1;
            action.Click += async (_, _) =>
            {
                if (_busy || _repository is null || _repository.IsBare) { return; }
                SelectDetailsTab("ChangesTab");
                GitChange? change = _changesFilesView?.Entries.Select(entry => entry.Tag).OfType<GitChange>().FirstOrDefault(entry => staged ? entry.IsStaged : entry.IsModified || entry.IsUntracked);
                _refreshing = true;
                try
                {
                    _stagedDiff.IsChecked = staged;
                    _changesFilesView?.SelectTags(change is null ? [] : [change]);
                }
                finally { _refreshing = false; }
                if (change is not null) { await LoadSelectedDiffAsync(); }
                else { ClearWorkingDiff(); }
            };
            _actionButtons.Add(action);
            if (i == 0) { _workingRevision = action; _workingRevisionState = state; }
            else { _indexRevision = action; _indexRevisionState = state; }
        }
        rows.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ClassicWindowBrush"));
        return rows;
    }

    private void UpdateVirtualRevisionRows(GitStatus status)
    {
        if (_virtualRevisions is not null) { _virtualRevisions.IsVisible = _historyView.ShowArtificialCommits && _repository?.IsBare != true; }
        int working = status.Changes.Count(change => change.IsModified || change.IsUntracked);
        int indexed = status.Changes.Count(change => change.IsStaged);
        foreach ((Button? button, TextBlock? state, int count) in new[] { (_workingRevision, _workingRevisionState, working), (_indexRevision, _indexRevisionState, indexed) })
        {
            if (button is null || state is null) { continue; }
            ToolTip.SetTip(button, count + " " + T("changes"));
            state.Text = count == 0 ? "✓" : count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            state.Foreground = count == 0 ? Brush.Parse("#24c221") : Brushes.DarkOrange;
        }
    }
}

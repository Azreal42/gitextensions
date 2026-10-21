using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal static class ExportDialogControls
{
    internal static Grid Fields() => new() { ColumnDefinitions = new ColumnDefinitions("120,*,Auto") };

    internal static void Field(Grid grid, string caption, Control input, Control? browse = null)
        => Field(grid, new TextBlock { Text = caption }, input, browse);

    internal static void Field(Grid grid, Control label, Control input, Control? browse = null)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 3, 8, 3);
        input.Margin = new Thickness(0, 3);
        Grid.SetRow(label, row);
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(label);
        grid.Children.Add(input);
        if (browse is null) { Grid.SetColumnSpan(input, 2); }
        else
        {
            browse.Margin = new Thickness(6, 3, 0, 3);
            Grid.SetRow(browse, row);
            Grid.SetColumn(browse, 2);
            grid.Children.Add(browse);
        }
    }

    internal static Border Group(string caption, Control body)
        => ClassicChrome.Border(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = caption, FontWeight = FontWeight.SemiBold }, body } }, new Thickness(1), new Thickness(8));

    internal static Expander Expandable(string name, string caption, Control content)
        => new()
        {
            Name = name, Header = caption, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Template = new FuncControlTemplate<Expander>((host, _) =>
            {
                Button heading = new() { Height = DesktopFontMetrics.ToolbarHeight, MinHeight = DesktopFontMetrics.ToolbarHeight, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 2) };
                ContentPresenter body = new() { Content = host.Content, Padding = new Thickness(6), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                void Refresh() { heading.Content = (host.IsExpanded ? "▾ " : "▸ ") + host.Header; body.IsVisible = host.IsExpanded; }
                Refresh();
                heading.Click += (_, _) => host.IsExpanded = !host.IsExpanded;
                host.PropertyChanged += (_, change) => { if (change.Property == Expander.IsExpandedProperty || change.Property == Expander.HeaderProperty) { Refresh(); } };
                DockPanel panel = new() { LastChildFill = true };
                DockPanel.SetDock(heading, Dock.Top);
                panel.Children.Add(heading);
                panel.Children.Add(body);
                return ClassicChrome.Border(panel, new Thickness(1));
            })
        };

    internal static StackPanel Buttons(params Control[] buttons)
        => new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(10, 6) }.WithButtons(buttons);

    private static StackPanel WithButtons(this StackPanel panel, IEnumerable<Control> buttons)
    {
        foreach (Control button in buttons) { panel.Children.Add(button); }
        return panel;
    }

    internal static string DisplayPath(string path) => path.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    internal static async Task<string?> FolderAsync(Window owner, string title, string? initialPath = null)
    {
        IStorageFolder? folder = null;
        if (!string.IsNullOrEmpty(initialPath) && Path.IsPathFullyQualified(initialPath))
        {
            try { folder = await owner.StorageProvider.TryGetFolderFromPathAsync(initialPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return (await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = title, AllowMultiple = false, SuggestedStartLocation = folder })).FirstOrDefault()?.TryGetLocalPath();
    }
}

internal sealed class ExportRevisionList : UserControl
{
    internal ListBox Rows { get; }
    private readonly CommitGraph _graph = new() { Width = RevisionHistoryColumns.GraphWidth, LaneWidth = 12, ShowReferenceLabels = false };
    private readonly HistoryViewPreferences _view;
    private readonly TranslationCatalog _translations;
    private IReadOnlyList<GitReference> _references = [];
    private IReadOnlyList<GitCommit> _ordered = [];
    private Dictionary<string, int> _rowIndices = new(StringComparer.Ordinal);
    private string? _authoredEmail;

    internal ExportRevisionList(string name, bool multiple, Control? prefixRows = null, HistoryViewPreferences? view = null)
    {
        // A chooser can pass the exact snapshot used by its query. Other historical callers capture it once here.
        _view = view ?? new HistoryViewPreferencesStore().Load();
        _translations = TranslationCatalog.LoadDefault();
        DetailedGraphSettings.Bind(_graph);
        Rows = new ListBox { Name = name, Focusable = true, Padding = default,
            SelectionMode = multiple ? SelectionMode.Multiple : SelectionMode.Single };
        Rows.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters = { new Setter(HeightProperty, _graph.LineHeight), new Setter(MinHeightProperty, _graph.LineHeight),
                new Setter(PaddingProperty, new Thickness(0, 1)), new Setter(MarginProperty, new Thickness(0)),
                new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                new Setter(ListBoxItem.VerticalContentAlignmentProperty, VerticalAlignment.Center) }
        });
        Rows.ItemTemplate = new FuncDataTemplate<GitCommit>((commit, _) =>
        {
            Control row = RevisionHistoryRow.Create(commit, _references, _view.RelativeDate);
            row.Tag = commit;
            UpdateRow(row);
            // Selection can precede realization, and a virtualized row may be arranged before its host is focused.
            row.AttachedToVisualTree += (_, _) => UpdateRow(row);
            row.Loaded += (_, _) => UpdateRow(row);
            row.PropertyChanged += (_, change) => { if (change.Property == BoundsProperty) { UpdateRow(row); } };
            return row;
        });
        Rows.SelectionChanged += (_, _) => UpdatePresentation();
        Rows.ContainerPrepared += (_, _) => UpdatePresentation();
        Rows.ContainerIndexChanged += (_, _) => UpdatePresentation();
        Rows.GotFocus += (_, _) => UpdatePresentation();
        Rows.LostFocus += (_, _) => Dispatcher.UIThread.Post(UpdatePresentation);
        Rows.ActualThemeVariantChanged += (_, _) => UpdatePresentation();
        Rows.Loaded += (_, _) => UpdatePresentation();
        Rows.AddHandler(ScrollViewer.ScrollChangedEvent, (_, change) => { if (change.Source is ScrollViewer viewer) { _graph.VerticalOffset = viewer.Offset.Y; } });
        DockPanel panel = new() { LastChildFill = true };
        RevisionHistoryColumns columns = RevisionHistoryColumns.From(_view);
        Grid headings = new() { Name = "RevisionColumnHeaders", ColumnDefinitions = columns.CreateDefinitions(includeGraph: true, showGraph: _view.ShowGraph), Height = _graph.LineHeight };
        string[] labels = _view.ShowNotesColumn ? ["Graph", "Message", "Author", "Date", "Commit", "Notes"] : ["Graph", "Message", "Author", "Date", "Commit"];
        bool[] visible = [_view.ShowGraph, true, _view.ShowAuthor, _view.ShowDate, _view.ShowId, _view.ShowNotesColumn];
        for (int column = 0; column < labels.Length; column++)
        {
            TextBlock label = new() { Text = _translations.Translate(labels[column]), IsVisible = visible[column], Margin = new Thickness(4, 2), VerticalAlignment = VerticalAlignment.Center };
            DesktopFonts.Apply(label, DesktopFontRole.Application);
            Grid.SetColumn(label, column);
            headings.Children.Add(label);
        }
        Border header = ClassicChrome.Border(headings, new Thickness(0, 0, 0, 1));
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        if (prefixRows is not null) { DockPanel.SetDock(prefixRows, Dock.Top); panel.Children.Add(prefixRows); }
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("90,*") };
        content.ColumnDefinitions[0].Width = new GridLength(_view.ShowGraph ? RevisionHistoryColumns.GraphWidth : 0);
        _graph.IsVisible = _view.ShowGraph;
        content.Children.Add(_graph);
        Grid.SetColumn(Rows, 1);
        content.Children.Add(Rows);
        panel.Children.Add(content);
        Content = ClassicChrome.Border(panel, new Thickness(1));
    }

    internal IReadOnlyList<GitCommit> SetData(IReadOnlyList<GitCommit> commits, IReadOnlyList<GitReference> references, string? head)
    {
        _references = RevisionHistoryPresentation.FilterReferences(references, _view);
        _graph.Commits = commits;
        _graph.References = _references;
        _graph.CurrentHead = head;
        _ordered = _graph.OrderedCommits;
        _rowIndices = _ordered.Select((commit, index) => (commit.Hash, Index: index))
            .ToDictionary(entry => entry.Hash, entry => entry.Index, StringComparer.Ordinal);
        Rows.ItemsSource = _ordered;
        UpdatePresentation();
        return _ordered;
    }

    private void UpdatePresentation()
    {
        GitCommit[] selected = Rows.SelectedItems?.OfType<GitCommit>().ToArray() ?? [];
        if (selected.Length == 1) { _authoredEmail = selected[0].AuthorEmail; }
        else if (selected.Length == 0) { _authoredEmail = null; }
        _graph.SelectedHashes = selected.Select(commit => commit.Hash).ToArray();
        _graph.SelectionFocused = Rows.IsKeyboardFocusWithin;
        _graph.AuthoredEmail = _view.HighlightAuthoredRevisions ? _authoredEmail : null;
        _graph.DrawAlternateBackground = _view.DrawAlternateBackground;
        foreach (Control row in Rows.GetVisualDescendants().OfType<Control>().Where(control => control.Name == "RevisionHistoryRow")) { UpdateRow(row); }
    }

    private void UpdateRow(Control row)
    {
        if (row.Tag is not GitCommit commit) { return; }
        bool selected = Rows.SelectedItems?.OfType<GitCommit>().Any(entry => entry.Hash == commit.Hash) == true;
        RevisionHistoryRow.UpdateState(row, selected, Rows.IsKeyboardFocusWithin,
            _view.HighlightAuthoredRevisions ? _authoredEmail : null, _rowIndices.GetValueOrDefault(commit.Hash), _view.DrawAlternateBackground);
        // Notes and committer dates require the existing Browse metadata reader; no hidden reads are introduced here.
        RevisionHistoryPresentation.Apply(row, commit, _view, _references, translate: _translations.Translate);
    }
}

internal sealed class ExportRevisionPicker : ChooseCommitDialog
{
    private ExportRevisionPicker(GitRepository repository, string? initial, CancellationToken token, ChooseCommitRequest? request = null)
        : base(repository, request ?? new(initial), new("ChooseRevision", "Choose", "CancelChoose", "RevisionSearch", "SearchRevision"), token) { }

    internal static Task<GitCommit?> ShowAsync(Window owner, GitRepository repository, string? initial = null, CancellationToken token = default)
        => ShowPickerAsync(new ExportRevisionPicker(repository, initial, token), owner, token);

    internal static Task<GitCompareRevision?> ShowComparisonAsync(Window owner, GitRepository repository, GitCompareRevision initial, CancellationToken token = default)
        => ShowComparisonPickerAsync(new ExportRevisionPicker(repository, null, token,
            new(initial.Kind == GitCompareRevisionKind.Commit ? initial.Revision : null, ShowArtificial: true, PreselectSelection: initial)), owner, token);
}

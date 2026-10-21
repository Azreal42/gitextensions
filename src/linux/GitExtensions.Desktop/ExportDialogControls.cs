using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
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
    private readonly CommitGraph _graph = new() { Width = 90 };

    internal ExportRevisionList(string name, bool multiple, Control? prefixRows = null)
    {
        DetailedGraphSettings.Bind(_graph);
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        DesktopFontSession fonts = DesktopFonts.Active;
        double hashWidth = DesktopFontMetrics.HashWidth(fonts.For(DesktopFontRole.Monospace));
        Rows = new ListBox { Name = name, SelectionMode = multiple ? SelectionMode.Multiple : SelectionMode.Single };
        Rows.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters = { new Setter(HeightProperty, _graph.LineHeight), new Setter(MinHeightProperty, _graph.LineHeight), new Setter(PaddingProperty, new Thickness(4, 1)), new Setter(MarginProperty, new Thickness(0)) }
        });
        Rows.ItemTemplate = new FuncDataTemplate<GitCommit>((commit, _) =>
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,120,125,75"), Height = _graph.LineHeight - 2 };
            row.ColumnDefinitions[3].Width = new GridLength(hashWidth);
            string[] values = [commit?.Subject ?? "", commit?.AuthorName ?? "", commit?.AuthoredAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "", commit?.Hash[..Math.Min(9, commit.Hash.Length)] ?? ""];
            for (int column = 0; column < values.Length; column++)
            {
                TextBlock cell = new() { Text = values[column], Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                fonts.Apply(cell, column == 3 ? DesktopFontRole.Monospace : DesktopFontRole.Application);
                Grid.SetColumn(cell, column);
                row.Children.Add(cell);
            }
            return row;
        });
        Rows.AddHandler(ScrollViewer.ScrollChangedEvent, (_, change) => { if (change.Source is ScrollViewer viewer) { _graph.VerticalOffset = viewer.Offset.Y; } });
        DockPanel panel = new() { LastChildFill = true };
        Grid headings = new() { ColumnDefinitions = new ColumnDefinitions("90,*,120,125,75"), Height = _graph.LineHeight };
        headings.ColumnDefinitions[4].Width = new GridLength(hashWidth);
        string[] labels = ["Graph", "Message", "Author", "Date", "Commit"];
        for (int column = 0; column < labels.Length; column++)
        {
            TextBlock label = new() { Text = translations.Translate(labels[column]), Margin = new Thickness(4, 2), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, column);
            headings.Children.Add(label);
        }
        Border header = ClassicChrome.Border(headings, new Thickness(0, 0, 0, 1));
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        if (prefixRows is not null) { DockPanel.SetDock(prefixRows, Dock.Top); panel.Children.Add(prefixRows); }
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("90,*") };
        content.Children.Add(_graph);
        Grid.SetColumn(Rows, 1);
        content.Children.Add(Rows);
        panel.Children.Add(content);
        Content = ClassicChrome.Border(panel, new Thickness(1));
    }

    internal IReadOnlyList<GitCommit> SetData(IReadOnlyList<GitCommit> commits, IReadOnlyList<GitReference> references, string? head)
    {
        _graph.Commits = commits;
        _graph.References = references;
        _graph.CurrentHead = head;
        Rows.ItemsSource = _graph.OrderedCommits;
        return _graph.OrderedCommits;
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

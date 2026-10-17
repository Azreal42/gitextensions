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

    internal static async Task<string?> FolderAsync(Window owner, string title)
        => (await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
}

internal sealed class ExportRevisionList : UserControl
{
    internal ListBox Rows { get; }
    private readonly CommitGraph _graph = new() { Width = 90 };

    internal ExportRevisionList(string name, bool multiple)
    {
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

internal sealed class ExportRevisionPicker : RepositoryDialog
{
    private readonly ExportRevisionList _revisions = new("ChooseRevision", false);
    private readonly TextBox _search = new() { Name = "RevisionSearch", Height = DesktopFontMetrics.ToolbarHeight };

    private ExportRevisionPicker(GitRepository repository) : base(repository, "Choose revision")
    {
        Width = 780;
        Height = 420;
        MinWidth = 620;
        MinHeight = 300;
        Button select = Action("Choose", "OK", () => { if (_revisions.Rows.SelectedItem is GitCommit revision) { Close(revision); } return Task.CompletedTask; });
        StackPanel buttons = ExportDialogControls.Buttons(Action("CancelChoose", "Cancel", () => { Close(null); return Task.CompletedTask; }), select);
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        Grid search = ExportDialogControls.Fields();
        ExportDialogControls.Field(search, T("Message"), _search, Action("SearchRevision", "Search", InitializeAsync));
        search.Margin = new Thickness(10, 6);
        DockPanel.SetDock(search, Dock.Top);
        Root.Children.Add(search);
        _revisions.Margin = new Thickness(10, 0, 10, 6);
        Root.Children.Add(_revisions);
        Track(_search, _revisions);
    }

    public override Task InitializeAsync() => RunAsync("Load revisions", async token =>
    {
        IReadOnlyList<GitCommit> revisions = await Repository.GetLogAsync(new GitLogQuery(Count: 500, Message: string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text), token);
        _revisions.SetData(revisions, await Repository.GetReferencesAsync(token), (await Repository.GetStatusAsync(token)).Head);
        _revisions.Rows.SelectedIndex = revisions.Count == 0 ? -1 : 0;
        Status.Text = T("Choose revision");
    });

    internal static async Task<GitCommit?> ShowAsync(Window owner, GitRepository repository)
    {
        ExportRevisionPicker picker = new(repository);
        await picker.InitializeAsync();
        return await picker.ShowDialog<GitCommit?>(owner);
    }
}

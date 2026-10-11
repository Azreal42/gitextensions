using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Plugins;
using PieControl = GitExtensions.Desktop.Controls.StatisticsPieChart;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original four Statistics tabs: bold total, text list on the left and a projected pie on the right.</summary>
public sealed class StatisticsDialog : RepositoryDialog
{
    private readonly StatisticsPlugin _plugin = new();
    private readonly TextBox _extensions = new() { Name = "StatisticsCodeFiles" };
    private readonly TextBox _ignoredDirectories = new() { Name = "StatisticsIgnoredDirectories" };
    private readonly CheckBox _ignoreSubmodules = new() { Name = "StatisticsIgnoreSubmodules" };
    private readonly TabControl _tabs = new() { Name = "StatisticsTabs" };
    private readonly List<(TextBlock Total, TextBlock Text, PieControl Pie)> _pages = [];
    public StatisticsReport? LastReport { get; private set; }
    public StatisticsOptions CurrentOptions => new(_extensions.Text ?? "", _ignoredDirectories.Text ?? "", _ignoreSubmodules.IsChecked != false);
    public IReadOnlyDictionary<string, string?> CurrentValues => new Dictionary<string, string?>
    {
        ["codeFiles"] = CurrentOptions.CodeFiles, ["directoriesToIgnore"] = CurrentOptions.DirectoriesToIgnore,
        ["ignoreSubmodules"] = CurrentOptions.IgnoreSubmodules.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    public StatisticsDialog(GitRepository repository, StatisticsOptions? options = null) : base(repository, "Statistics")
    {
        options ??= new();
        Width = 751; Height = 560; MinWidth = 600; MinHeight = 400;
        _extensions.Text = options.CodeFiles;
        _ignoredDirectories.Text = options.DirectoriesToIgnore;
        _ignoreSubmodules.IsChecked = options.IgnoreSubmodules;
        _ignoreSubmodules.Content = T("Ignore submodules");
        Grid settings = new() { ColumnDefinitions = new ColumnDefinitions("250,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(8) };
        AddSetting(settings, 0, "Code files", _extensions);
        AddSetting(settings, 1, "Directories to ignore (EndsWith)", _ignoredDirectories);
        Grid.SetRow(_ignoreSubmodules, 2); Grid.SetColumn(_ignoreSubmodules, 1); settings.Children.Add(_ignoreSubmodules);
        Expander parameters = new() { Name = "StatisticsSettings", Header = T("Settings"), Content = settings, HorizontalAlignment = HorizontalAlignment.Stretch };
        DockPanel.SetDock(parameters, Dock.Top); Root.Children.Add(parameters);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        actions.Children.Add(Action("RefreshStatistics", "Refresh", InitializeAsync));
        actions.Children.Add(Action("CloseStatistics", "Close", () => { Close(); return Task.CompletedTask; }));
        DockPanel.SetDock(actions, Dock.Bottom); Root.Children.Add(actions);
        List<TabItem> tabs = [];
        foreach ((string name, string title) in new[]
            { ("Contributors", "Commits per contributor"), ("Languages", "Lines of code per language"), ("Types", "Lines of code per type"), ("Tests", "Lines of test code") })
        {
            TextBlock total = new() { Name = "StatisticsTotal" + name, FontWeight = FontWeight.Bold, Margin = new Thickness(5, 2) };
            TextBlock text = new() { Name = "StatisticsText" + name, Text = T("Loading..."), Margin = new Thickness(9, 4), TextWrapping = TextWrapping.NoWrap };
            PieControl pie = new() { Name = "StatisticsPie" + name, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, ToolTipFormatter = SliceText };
            Grid panes = new() { ColumnDefinitions = new ColumnDefinitions("286,4,*") };
            panes.Children.Add(new ScrollViewer { Content = text, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            GridSplitter splitter = new() { Width = 4, ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetColumn(splitter, 1); panes.Children.Add(splitter);
            Grid chartHost = new();
            chartHost.Children.Add(pie);
            chartHost.SizeChanged += (_, _) => { double size = Math.Max(0, Math.Min(chartHost.Bounds.Width, chartHost.Bounds.Height)); pie.Width = pie.Height = size; };
            Grid.SetColumn(chartHost, 2); panes.Children.Add(chartHost);
            DockPanel page = new() { Margin = new Thickness(3), LastChildFill = true };
            DockPanel.SetDock(total, Dock.Top); page.Children.Add(total); page.Children.Add(panes);
            tabs.Add(new TabItem { Header = T(title), Content = page });
            _pages.Add((total, text, pie));
        }
        _tabs.ItemsSource = tabs; _tabs.SelectedIndex = 0;
        Root.Children.Add(_tabs);
        Track(_extensions, _ignoredDirectories, _ignoreSubmodules);
    }

    public override Task InitializeAsync() => RunAsync("Statistics", async token =>
    {
        StatisticsOptions options = CurrentOptions;
        Progress<StatisticsProgress> progress = new(value =>
        {
            if (IsBusy) Status.Text = value.Phase == "files" ? $"{value.FilesAnalyzed:N0} {T("Files")} — {value.Lines.Code:N0} {T("Lines of code")}" : T("Loading...");
        });
        LastReport = await _plugin.AnalyzeAsync(Repository, options, progress, token);
        for (int i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            var chart = LastReport.Charts[i];
            page.Total.Text = i == 0 ? string.Format(T("{0:N0} Commits"), LastReport.TotalCommits)
                : i == 3 ? string.Format(T("{0:N0} Lines of test code"), LastReport.Lines.TestCode)
                : string.Format(T("{0:N0} Lines of code"), LastReport.Lines.Code);
            page.Text.Text = i == 0 ? string.Join(Environment.NewLine, LastReport.Contributors.Select(author => $"{author.Commits:N0} {author.Author}"))
                : string.Join(Environment.NewLine, chart.Slices.Select(SliceText));
            page.Pie.Chart = chart;
        }
        Status.Text = T("Completed");
    });

    private string SliceText(StatisticsPieSlice slice)
    {
        if (LastReport?.Charts[0].Slices.Contains(slice) == true) return string.Format(T("{0:N0} Commits by {1}"), slice.Value, slice.Label);
        if (slice.Key.StartsWith(".", StringComparison.Ordinal)) return string.Format(T("{0:N0} Lines of code in {1} files ({2:P1})"), slice.Value, slice.Label, slice.Fraction);
        string format = slice.Key switch
        {
            "blank" => "{0:N0} Blank lines ({1:P1})", "comments" => "{0:N0} Comment lines ({1:P1})",
            "designer" => "{0:N0} Lines in designer files ({1:P1})", "tests" => "{0:N0} Lines of test code ({1:P1})",
            "production" => "{0:N0} Lines of production code ({1:P1})", _ => "{0:N0} Lines of code ({1:P1})"
        };
        return string.Format(T(format), slice.Value, slice.Fraction);
    }
    private void AddSetting(Grid grid, int row, string label, Control field)
    {
        TextBlock text = new() { Text = T(label), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row); grid.Children.Add(text);
        field.Margin = new Thickness(0, 2); Grid.SetRow(field, row); Grid.SetColumn(field, 1); grid.Children.Add(field);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

/// <summary>Original Impact layout: author swatch and totals above a horizontally scrolling ribbon graph.</summary>
public sealed class GitImpactDialog : RepositoryDialog
{
    private readonly GitImpactPlugin _plugin = new();
    private readonly CheckBox _includeSubmodules = new() { Name = "ImpactIncludeSubmodules" };
    private readonly TextBlock _author = new() { Name = "ImpactSelectedAuthor", VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _color = new() { Name = "ImpactAuthorColor", Width = 20, Height = 20, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
    private readonly GitImpactGraph _graph = new() { Name = "ImpactGraph" };
    private readonly bool _respectMailmap;
    private readonly DayOfWeek? _firstDayOfWeek;
    public GitImpactReport? LastReport { get; private set; }
    public GitImpactOptions CurrentOptions => new(_includeSubmodules.IsChecked == true, _respectMailmap, _firstDayOfWeek);
    public IReadOnlyDictionary<string, string?> CurrentValues => new Dictionary<string, string?>
    {
        ["includeSubmodules"] = CurrentOptions.IncludeSubmodules.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["respectMailmap"] = CurrentOptions.RespectMailmap.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    public GitImpactDialog(GitRepository repository, GitImpactOptions? options = null) : base(repository, "Impact")
    {
        options ??= new();
        _respectMailmap = options.RespectMailmap;
        _firstDayOfWeek = options.FirstDayOfWeek;
        Width = 879; Height = 560; MinWidth = 500; MinHeight = 340;
        _includeSubmodules.Content = T("Including submodules");
        _includeSubmodules.IsChecked = options.IncludeSubmodules;
        DockPanel header = new() { Height = 32, Margin = new Thickness(6, 0), LastChildFill = true };
        DockPanel.SetDock(_includeSubmodules, Dock.Right); header.Children.Add(_includeSubmodules);
        StackPanel selected = new() { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _color, _author } };
        header.Children.Add(selected); DockPanel.SetDock(header, Dock.Top); Root.Children.Add(header);
        _author.IsVisible = _color.IsVisible = false;
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        actions.Children.Add(Action("RefreshImpact", "Refresh", InitializeAsync));
        actions.Children.Add(Action("CloseImpact", "Close", () => { Close(); return Task.CompletedTask; }));
        DockPanel.SetDock(actions, Dock.Bottom); Root.Children.Add(actions);
        ScrollViewer scroll = new()
        {
            Name = "ImpactScroll", Content = _graph, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        Root.Children.Add(ClassicChrome.Border(scroll, new Thickness(1)));
        _graph.SelectedAuthorChanged += (_, _) => ShowAuthor();
        _graph.ActualThemeVariantChanged += (_, _) => ShowAuthor();
        _includeSubmodules.IsCheckedChanged += async (_, _) => { if (!IsBusy) await InitializeAsync(); };
        Track(_includeSubmodules);
    }
    public override Task InitializeAsync() => RunAsync("Impact Graph", async token =>
    {
        _graph.Report = null;
        LastReport = await _plugin.AnalyzeAsync(Repository, CurrentOptions, cancellationToken: token);
        _graph.Report = LastReport;
        Status.Text = T("Completed");
    });
    private void ShowAuthor()
    {
        GitImpactAuthor? selected = LastReport?.Authors.FirstOrDefault(author => author.Name == _graph.SelectedAuthor);
        _author.IsVisible = _color.IsVisible = selected is not null;
        if (selected is null) return;
        _author.Text = string.Format(T("{0} ({1} Commits, {2} Changed Lines)"), selected.Name, selected.Data.Commits, selected.Data.ChangedLines);
        _color.Background = new SolidColorBrush(_graph.AuthorColor(selected.Name));
    }
}

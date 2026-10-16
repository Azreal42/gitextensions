using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class PullDialog
{
    private readonly RadioButton _fromRemote = new() { Name = "PullFromRemote", GroupName = "PullSource", IsChecked = true };
    private readonly RadioButton _fromUrl = new() { Name = "PullFromUrl", GroupName = "PullSource" };
    private readonly ComboBox _url = PullChoice("PullUrl");
    private readonly TextBox _localBranch = new() { Name = "PullLocalBranch", Height = 24 };
    private readonly CloneSourcesStore _sourceHistory = new();
    private readonly Border _helpHost = new() { Name = "PullHelp", Width = 305 };
    private readonly Border _footerSpacer = new() { Width = 305 };
    private readonly StackPanel _recovery = new() { Name = "PullRecoveryActions", Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Control _statusRow;
    private OriginalCommandHelp? _help;
    private GitPullAction _presentedMode = GitPullAction.None;

    private static ComboBox PullChoice(string name) => new()
    {
        Name = name, IsEditable = true, IsTextSearchEnabled = false, Height = 24,
        Padding = new Thickness(4, 2), HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };

    private void BuildOriginalLayout()
    {
        ConfigureCompactChoices();
        void UpdateLinkColor() => Resources["PullHelpLinkBrush"] = new SolidColorBrush(ClassicTextContrast.AdaptForeground(this, Color.Parse("#0000ff")));
        UpdateLinkColor(); ActualThemeVariantChanged += (_, _) => UpdateLinkColor();
        _fromRemote.Content = Label("PullFromRemote", "&Remote");
        _fromUrl.Content = Label("PullFromUrl", "&URL");
        ReferenceDialogControls.Compact(_fromRemote, _fromUrl);
        _url.ItemsSource = _sourceHistory.Load();
        _manage.Content = PullIconLabel("Remotes.png", Label("AddRemote", "&Manage remotes"));
        _browse.Content = PullIconLabel("FolderClosed.png", Label("folderBrowserButton1", "Bro&wse..."));
        _resolve.Content = Label("Mergetool", "&Solve conflicts");
        _manage.Width = 150; _browse.Width = 100; _browse.HorizontalAlignment = HorizontalAlignment.Left;
        _prune.Margin = _pruneTags.Margin = _unshallow.Margin = new Thickness(6, 0, 0, 0);
        foreach (Button button in new[] { _manage, _browse, _execute, _resolve, _stash, _continue, _abort })
        { button.Height = 24; button.MinHeight = 0; button.CornerRadius = new CornerRadius(2); }
        _execute.Width = 122; _resolve.Width = 138; _stash.Width = 130;
        _execute.Bind(BorderBrushProperty, new DynamicResourceExtension("ClassicHighlightBrush"));

        Grid source = new() { ColumnDefinitions = new("138,*,150"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 6 };
        AddRow(source, 0, _fromRemote, _remote, _manage); AddRow(source, 1, _fromUrl, _url, _browse);
        Grid branches = new() { ColumnDefinitions = new("138,*,150"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 6 };
        AddRow(branches, 0, new TextBlock { Name = "PullLocalBranchLabel", Text = C("lblLocalBranch", "&Local branch").Replace("&", "") }, _localBranch);
        AddRow(branches, 1, new TextBlock { Name = "PullRemoteBranchLabel", Text = C("lblRemoteBranch", "Rem&ote branch").Replace("&", "") }, _branch);
        ToolTip.SetTip(_localBranch, C("lblLocalBranch", "Local branch to create or reset to the remote branch selected.", "Tooltip"));
        Control mergeLabel = (Control)_merge.Content!, rebaseLabel = (Control)_rebase.Content!;
        _merge.Content = _rebase.Content = null;
        _merge.Content = PullIconLabel("PullMerge.png", mergeLabel);
        _rebase.Content = PullIconLabel("PullRebase.png", rebaseLabel);
        // Keep wrapped translations in the radio itself rather than clipping a fixed-height row.
        _merge.Height = double.NaN;
        _output.IsVisible = false;
        _output.PropertyChanged += (_, change) =>
        { if (change.Property == TextBox.TextProperty) { _output.IsVisible = !string.IsNullOrEmpty(_output.Text); } };
        _recovery.Children.Add(_continue); _recovery.Children.Add(_abort);
        StackPanel choices = new()
        {
            Name = "PullOptions", Spacing = 6, Children =
            {
                PullGroup("PullSourceGroup", C("GroupPullFrom", "Pull from"), source),
                PullGroup("PullBranchGroup", C("GroupBranch", "Branch"), branches),
                PullGroup("PullMergeGroup", C("GroupMergeOptions", "Merge options"), new StackPanel { Spacing = 3, Children = { _merge, _rebase, _fetch } }),
                PullGroup("PullTagsGroup", C("GroupTagOptions", "Tag options"), new StackPanel { Spacing = 3, Children = { _tagsFollow, _tagsNone, _tagsAll } }),
                _unshallow, _prune, _pruneTags, _conflicts, _output
            }
        };
        Grid body = new() { Name = "PullLayout", Margin = new Thickness(10), ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        body.Children.Add(_helpHost);
        ScrollViewer options = new() { Name = "PullOptionsScroll", Content = choices, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(options, 1); body.Children.Add(options);

        Grid footer = new() { Name = "PullFooter", Margin = new Thickness(10, 8), ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        footer.Children.Add(_footerSpacer);
        Grid commands = new() { ColumnDefinitions = new("Auto,Auto,Auto,*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 8 };
        Grid.SetColumnSpan(_recovery, 5); commands.Children.Add(_recovery);
        foreach (Control control in new Control[] { _resolve, _stash, _autoStash, _execute }) { Grid.SetRow(control, 1); }
        commands.Children.Add(_resolve); Grid.SetColumn(_stash, 1); commands.Children.Add(_stash);
        Grid.SetColumn(_autoStash, 2); commands.Children.Add(_autoStash);
        Grid.SetColumn(_execute, 4); commands.Children.Add(_execute);
        Grid.SetColumn(commands, 1); footer.Children.Add(commands);
        Border footerBackground = ClassicChrome.Border(footer, new Thickness(0, 1, 0, 0));
        footerBackground.Bind(BackgroundProperty, new DynamicResourceExtension("ClassicControlBrush"));
        DockPanel.SetDock(footerBackground, Dock.Bottom); Root.Children.Add(footerBackground); Root.Children.Add(body);

        foreach (RadioButton sourceMode in new[] { _fromRemote, _fromUrl })
        {
            sourceMode.IsCheckedChanged += (_, _) =>
            {
                if (_initializing) { return; }
                if (All) { _fetch.IsChecked = true; }
                UpdateSourceUrl(); UpdateBranchChoices(); UpdateActions();
            };
        }
        _url.PropertyChanged += (_, change) => { if (change.Property == ComboBox.TextProperty) { UpdateActions(); } };
        Closed += (_, _) => _help?.Dispose();

        static void AddRow(Grid grid, int row, Control label, Control input, Control? action = null)
        {
            label.VerticalAlignment = VerticalAlignment.Center;
            foreach (Control control in new[] { label, input }.Concat(action is null ? [] : new[] { action }))
            { control.Margin = new Thickness(0, 3); Grid.SetRow(control, row); grid.Children.Add(control); }
            Grid.SetColumn(input, 1);
            if (action is not null) { Grid.SetColumn(action, 2); }
        }
    }

    private static Grid PullGroup(string name, string caption, Control body)
        => ClassicChrome.Group(name, caption, body);

    private static Control PullIconLabel(string icon, Control label)
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + icon));
        return new Grid
        {
            ColumnDefinitions = new("16,*"), ColumnSpacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Children = { new Image { Width = 16, Height = 16, Source = new Bitmap(stream), Tag = icon }, InSecondColumn(label) }
        };
        static Control InSecondColumn(Control control) { Grid.SetColumn(control, 1); control.VerticalAlignment = VerticalAlignment.Center; return control; }
    }

    private void UpdateOriginalPresentation()
    {
        bool fetch = Mode == GitPullAction.Fetch;
        _remote.IsEnabled = !IsBusy && !UseUrl; _manage.IsEnabled = !IsBusy && !UseUrl;
        _url.IsEnabled = _browse.IsEnabled = !IsBusy && UseUrl;
        _fromRemote.IsEnabled = _fromUrl.IsEnabled = !IsBusy;
        _localBranch.IsEnabled = !IsBusy && fetch && !All;
        if (!fetch) { _localBranch.Text = _status?.IsDetached == true ? "" : _status?.Branch ?? ""; }
        _statusRow.IsVisible = LastError is not null;
        _recovery.IsVisible = _operationName is not null;
        Title = T(fetch ? "Fetch" : "Pull") + " (" + Repository.Path + ")";
        if (_presentedMode == Mode) { return; }
        _presentedMode = Mode;
        if (fetch) { _localBranch.Text = ""; }
        _execute.Content = PullIconLabel("ArrowDown.png", new AccessText { Text = T(fetch ? "Fetch" : "Pull") });
        _help?.Dispose(); _helpHost.Child = null;
        _help = new(fetch ? "HelpPullFetch" : Mode == GitPullAction.Rebase ? "HelpPullRebase" : "HelpPullMerge",
            Mode == GitPullAction.Merge ? "HelpPullMergeFastForward" : null, "Pull",
            Mode == GitPullAction.Merge ? "Hover to see scenario when fast forward is possible." : null)
        { UseAlternateOnHover = Mode == GitPullAction.Merge, Margin = new Thickness(6, 0, 0, 0) };
        _help.Styles.Add(new Style(selector => selector.OfType<Image>().Name("OriginalCommandHelpImage"))
        { Setters = { new Setter(MarginProperty, new Thickness(0, Mode == GitPullAction.Merge ? 0 : 26, 0, 0)) } });
        _help.Styles.Add(new Style(selector => selector.OfType<Button>().Name("OriginalCommandHelpToggle"))
        {
            Setters =
            {
                new Setter(BackgroundProperty, Brushes.Transparent), new Setter(BorderThicknessProperty, new Thickness(0)),
                new Setter(ForegroundProperty, new DynamicResourceExtension("PullHelpLinkBrush")),
                new Setter(PaddingProperty, new Thickness(0)),
                new Setter(TemplateProperty, new Avalonia.Controls.Templates.FuncControlTemplate<Button>((_, _) =>
                {
                    TextBlock text = new() { TextDecorations = TextDecorations.Underline, VerticalAlignment = VerticalAlignment.Center };
                    text.Bind(TextBlock.TextProperty, new TemplateBinding(ContentControl.ContentProperty), BindingPriority.Template); return text;
                }))
            }
        });
        _help.ExpandedChanged += (_, _) => ResizeForHelp(); _helpHost.Child = _help; ResizeForHelp();
    }

    private void ResizeForHelp()
    {
        double width = _help?.IsExpanded == true ? 305 : 40, delta = width - _helpHost.Width;
        _helpHost.Width = _footerSpacer.Width = width;
        MinWidth = _help?.IsExpanded == true ? 920 : 650;
        Width = Math.Max(MinWidth, Width + delta);
    }

    private void ConfigureCompactChoices()
    {
        Classes.Add("original-pull");
        foreach (string name in new[] { "OuterEllipse", "CheckOuterEllipse", "CheckGlyph" })
        {
            double size = name == "CheckGlyph" ? 5 : 13;
            Styles.Add(new Style(selector => selector.OfType<Window>().Class("original-pull").Descendant().OfType<RadioButton>().Template().OfType<Avalonia.Controls.Shapes.Ellipse>().Name(name))
            { Setters = { new Setter(WidthProperty, size), new Setter(HeightProperty, size), new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Center) } });
        }
        Styles.Add(new Style(selector => selector.OfType<Window>().Class("original-pull").Descendant().OfType<CheckBox>().Template().OfType<Border>().Name("NormalRectangle"))
        { Setters = { new Setter(WidthProperty, 13d), new Setter(HeightProperty, 13d), new Setter(CornerRadiusProperty, new CornerRadius(2)), new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Center) } });
        Styles.Add(new Style(selector => selector.OfType<Window>().Class("original-pull").Descendant().OfType<CheckBox>().Template().OfType<Viewbox>())
        { Setters = { new Setter(WidthProperty, 13d), new Setter(HeightProperty, 13d), new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Center) } });
    }
}

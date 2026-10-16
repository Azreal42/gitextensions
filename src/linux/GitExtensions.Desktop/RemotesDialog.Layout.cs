using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitExtensions.Linux.Desktop;

public sealed partial class RemotesDialog
{
    private readonly List<Control> _pushFields = [];
    private AccessText _urlCaption = null!;

    private void BuildLayout()
    {
        CompactList(_remotes);
        _remotes.ItemTemplate = new FuncDataTemplate<GitRemoteSettingsSnapshot>((remote, _) =>
        {
            if (remote is null) { return new TextBlock(); }
            StackPanel row = new();
            if (_values.FirstOrDefault(value => value.Disabled == remote.Disabled)?.Name == remote.Name)
            {
                TextBlock heading = new() { Name = remote.Disabled ? "InactiveRemotesHeader" : "ActiveRemotesHeader", Text = T(remote.Disabled ? "Inactive" : "Active"), Margin = new Thickness(0, 5, 0, 3) };
                void UpdateColor() => heading.Foreground = new SolidColorBrush(ClassicTextContrast.AdaptForeground(heading, Color.Parse("#0066cc")));
                heading.ActualThemeVariantChanged += (_, _) => UpdateColor(); UpdateColor();
                Border groupHeading = new() { Child = heading, Margin = new Thickness(-3, 0, -3, 0), Padding = new Thickness(3, 0) };
                groupHeading.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ClassicWindowBrush"));
                groupHeading.AddHandler(PointerPressedEvent, (_, args) => args.Handled = true, RoutingStrategies.Tunnel);
                row.Children.Add(groupHeading);
            }
            TextBlock label = new() { Text = remote.Name, Margin = new Thickness(10, 1, 4, 1), MinHeight = 18 };
            if (remote.Disabled) { label.Opacity = 0.6; }
            row.Children.Add(label); return row;
        });
        ScrollViewer.SetHorizontalScrollBarVisibility(_remotes, ScrollBarVisibility.Auto);
        DockPanel left = new() { Margin = new Thickness(8) };
        StackPanel tools = new() { Orientation = Orientation.Vertical, Spacing = 6, Margin = new Thickness(7, 3, 0, 0), Children = { _new, _delete, _toggle } };
        DockPanel.SetDock(tools, Dock.Right); left.Children.Add(tools); left.Children.Add(ClassicChrome.Border(_remotes, new Thickness(1)));

        Grid fields = new() { Name = "RemoteSettingsFields", ColumnDefinitions = new ColumnDefinitions("106,*,110"), Margin = new Thickness(10, 2, 10, 8) };
        _urlCaption = (AccessText)((Label)Field(fields, "label2", "&Url", _url, _browse)[0]).Content!;
        Field(fields, "label1", "&Name", _name);
        Field(fields, "lblRemotePrefix", "Prefix", _prefix, visible: _showAdvanced);
        StackPanel colors = new() { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _colorButton, _resetColor } };
        Field(fields, "lblRemoteColor", "Color", colors, visible: _showAdvanced);
        _separate.Content = Caption("checkBoxSepPushUrl", "Sep&arate Push Url"); _separate.Margin = new Thickness(24, 4, 0, 4); _separate.MinHeight = 20;
        int row = fields.RowDefinitions.Count; fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(_separate, row); Grid.SetColumnSpan(_separate, 2); fields.Children.Add(_separate);
        _pushFields.AddRange(Field(fields, "labelPushUrl", "&Push Url", _pushUrl, _browsePush));
        _save.HorizontalAlignment = HorizontalAlignment.Right; _save.MinWidth = 130; _save.Margin = new Thickness(10, 4, 10, 10);
        DockPanel managementBody = new() { LastChildFill = false };
        DockPanel.SetDock(fields, Dock.Top); DockPanel.SetDock(_save, Dock.Top); managementBody.Children.Add(fields); managementBody.Children.Add(_save);
        _management = ClassicChrome.Group("RemoteSettingsManagement", C("_gbMgtPanelHeaderNew", "Create New Remote"), managementBody, new Thickness(3, 12, 3, 3));
        _management.VerticalAlignment = VerticalAlignment.Top;
        _management.Margin = new Thickness(4, 8, 8, 8);
        _managementCaption = (TextBlock)((Border)_management.Children[1]).Child!;
        Grid remotePage = new() { Name = "RemoteRepositoriesPage", ColumnDefinitions = new ColumnDefinitions("350,*") };
        remotePage.Children.Add(left); Grid.SetColumn(_management, 1); remotePage.Children.Add(_management);
        _tabs.ItemsSource = new[]
        {
            new TabItem { Name = "RemoteRepositoriesTab", Header = C("tabPage1", "Remote repositories"), Content = remotePage },
            new TabItem { Name = "DefaultPullBehaviorTab", Header = C("tabPage2", "Default pull behavior (fetch & merge)"), Content = BuildTrackingLayout() }
        };
        Root.Children.Add(_tabs);
    }

    private IReadOnlyList<Control> Field(Grid grid, string item, string caption, Control input, Control? browse = null, bool visible = true)
    {
        Label label = new() { Content = Caption(item, caption), Target = input, Padding = new Thickness(0) };
        label.IsVisible = input.IsVisible = visible;
        List<Control> controls = [label, input];
        ExportDialogControls.Field(grid, label, input, browse);
        if (browse is null && grid.Name == "RemoteSettingsFields") { Grid.SetColumnSpan(input, 1); }
        if (browse is not null) { browse.IsVisible = visible; controls.Add(browse); }
        return controls;
    }

    private void UpdatePushUrlVisibility()
    {
        foreach (Control field in _pushFields) { field.IsVisible = _separate.IsChecked == true; }
        if (_urlCaption is not null)
        {
            _urlCaption.Text = Translations.TranslateAccessKeys("FormRemotes", _separate.IsChecked == true ? "_labelUrlAsFetch" : "_labelUrlAsFetchPush", "Text", _separate.IsChecked == true ? "Fetch &Url" : "&Url");
        }
    }

    private static void CompactList(ListBox list)
    {
        list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters = { new Setter(MinHeightProperty, 20d), new Setter(PaddingProperty, new Thickness(3, 0)), new Setter(MarginProperty, new Thickness(0)),
                new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) }
        });
    }
}

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>Owned font draft only; no profile publication or active-session replacement.</summary>
internal sealed class FontPickerDialog : Window
{
    private readonly FontSpec _initial;
    private readonly DesktopFontRole _role;
    private readonly ListBox _families = new() { Name = "FontFamily", Height = 210, MinWidth = 280 };
    private readonly TextBox _size;
    private readonly CheckBox _bold, _italic;
    private readonly TextBlock _preview = new() { Name = "FontPreview", Text = "AaBbYyZz 0123456789", Margin = new Thickness(8) };
    private readonly TextBlock _notice = new() { Name = "FontFallback", TextWrapping = TextWrapping.Wrap };
    private bool _refreshing;
    internal FontSpec Result { get; private set; }

    internal FontPickerDialog(FontSpec initial, DesktopFontRole role, TranslationCatalog? translations = null)
    {
        _initial = initial; _role = role; Result = initial;
        TranslationCatalog text = translations ?? TranslationCatalog.LoadDefault();
        Name = "FontPicker"; Title = text.Translate("Font"); Width = 510; SizeToContent = SizeToContent.Height;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _size = new() { Name = "FontSizeInPoints", Text = initial.SizeInPoints.ToString("G17", CultureInfo.CurrentCulture), MinWidth = 90 };
        _bold = new() { Name = "FontBold", Content = text.Translate("Bold"), IsChecked = initial.Bold };
        _italic = new() { Name = "FontItalic", Content = text.Translate("Italic"), IsChecked = initial.Italic };
        _families.ItemTemplate = new FuncDataTemplate<FontFamily>((family, _) => new TextBlock { Text = family?.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        _families.SelectionChanged += (_, _) => UpdatePreview();
        _size.TextChanged += (_, _) => UpdatePreview();
        _bold.IsCheckedChanged += (_, _) => RefreshFamilies(); _italic.IsCheckedChanged += (_, _) => RefreshFamilies();
        Grid choices = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        StackPanel family = new() { Spacing = 4, Children = { new TextBlock { Text = text.Translate("Font") }, ClassicChrome.Border(_families, new Thickness(1)) } };
        choices.Children.Add(family);
        StackPanel style = new() { Spacing = 8, Children = { new TextBlock { Text = text.Translate("Size") + " (pt)" }, _size, _bold, _italic } };
        Grid.SetColumn(style, 1); choices.Children.Add(style);
        Button accept = new() { Name = "AcceptFont", Content = text.Translate("OK"), MinWidth = 75, IsDefault = true };
        Button cancel = new() { Name = "CancelFont", Content = text.Translate("Cancel"), MinWidth = 75, IsCancel = true };
        accept.Click += (_, _) => { if (TryDraft(out FontSpec? draft, out string? error)) { Result = draft!; Close(true); } else { _notice.Text = error; } };
        cancel.Click += (_, _) => Close(false);
        StackPanel body = new() { Margin = new Thickness(12), Spacing = 10, Children = { choices,
            ClassicChrome.Border(new ScrollViewer { Height = 75, Content = _preview, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }, new Thickness(1)),
            new ScrollViewer { MaxHeight = 110, Content = _notice } } };
        StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 6), Spacing = 6, Children = { accept, cancel } };
        DockPanel root = new(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(body); Content = root;
        RefreshFamilies();
        Opened += (_, _) => { _families.Focus(); ModalWindowActivation.AfterMapping(this, () => IsVisible); };
    }

    private void RefreshFamilies()
    {
        if (_refreshing) { return; }
        _refreshing = true;
        string name = (_families.SelectedItem as FontFamily)?.Name ?? _initial.Family;
        _families.ItemsSource = DesktopFontResolver.Families(_role, _bold.IsChecked == true, _italic.IsChecked == true);
        _families.SelectedItem = _families.Items.OfType<FontFamily>().FirstOrDefault(family => string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase));
        _refreshing = false; UpdatePreview();
    }

    private bool TryDraft(out FontSpec? draft, out string? error)
    {
        draft = null;
        if (!double.TryParse(_size.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double points))
        { error = "Enter a positive, finite font size in points."; return false; }
        FontSpec value = new((_families.SelectedItem as FontFamily)?.Name ?? _initial.Family, points, _bold.IsChecked == true, _italic.IsChecked == true);
        try { value.Validate(); }
        catch (ArgumentException exception) { error = exception.Message; return false; }
        ResolvedDesktopFont resolved = DesktopFontResolver.Resolve(value, _role);
        // An unavailable existing identity can survive an unchanged OK, without pretending it is selected.
        if (value != _initial && (resolved.Diagnostic is not null || _families.SelectedItem is null))
        { error = resolved.Diagnostic ?? "Choose an installed font face."; return false; }
        if (!DesktopFontResolver.CanRender(resolved.Family, value.SizeInDips, value.Bold, value.Italic, out error)) { return false; }
        draft = value; return true;
    }

    private void UpdatePreview()
    {
        if (_refreshing) { return; }
        if (!TryDraft(out FontSpec? draft, out string? error)) { _notice.Text = error; return; }
        ResolvedDesktopFont resolved = DesktopFontResolver.Resolve(draft!, _role);
        DesktopFontResolver.Apply(_preview, resolved);
        _notice.Text = resolved.Diagnostic is null ? "" : draft!.Family + " — " + resolved.Diagnostic + " (" + resolved.EffectiveFamilyName + ")";
    }
}

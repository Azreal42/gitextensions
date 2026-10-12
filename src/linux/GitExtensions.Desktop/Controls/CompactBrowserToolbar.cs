using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;

namespace GitExtensions.Desktop.Controls;

/// <summary>One original ToolStrip row; a contiguous suffix remains available in its overflow menu.</summary>
internal sealed class CompactBrowserToolbar : Border
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 1 };
    private readonly List<Entry> _entries = [];
    private readonly ObservableCollection<Control> _popupItems = [];
    private readonly MenuFlyout _flyout = new();
    private bool _placing;
    internal Button Overflow { get; }
    internal IReadOnlyList<Control> InlineItems => _entries.Where(entry => entry.Host.IsVisible).Select(entry => entry.Control).ToArray();
    internal MenuFlyout OverflowFlyout => _flyout;

    internal CompactBrowserToolbar()
    {
        Name = "RepositoryToolbar";
        Height = 26; MinHeight = 26; MaxHeight = 26; Padding = new Thickness(3, 1);
        _flyout.ItemsSource = _popupItems;
        DiffViewerPopupLayout.Configure(_flyout);
        Overflow = new Button
        {
            Name = "RepositoryToolbarOverflow", Content = "»", Width = 24, Height = 24,
            MinWidth = 0, MinHeight = 24, Padding = default, IsVisible = false, Flyout = _flyout
        };
        _row.Children.Add(Overflow); Child = _row;
        SizeChanged += (_, _) => Place();
        _flyout.Opened += (_, _) => ApplyPalette();
        ActualThemeVariantChanged += (_, _) => ApplyPalette();
        DetachedFromVisualTree += (_, _) => _flyout.Hide();
        ApplyPalette();
    }

    internal void Add(Control control, Control popup)
    {
        Border host = new() { Child = control, Height = 24 };
        _entries.Add(new(control, host, popup));
        _row.Children.Insert(_row.Children.Count - 1, host);
        control.PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty || change.Property == WidthProperty) { Place(); }
        };
    }

    internal Control Reveal(Control control)
    {
        Entry? entry = _entries.FirstOrDefault(entry => ReferenceEquals(entry.Control, control) || control.GetVisualAncestors().Contains(entry.Control));
        if (entry is null || entry.Host.IsVisible) { return control; }
        _flyout.ShowAt(Overflow);
        entry.Popup.UpdateLayout();
        return entry.Popup.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() ?? control;
    }

    internal void Place()
    {
        if (_placing || Bounds.Width <= 0) { return; }
        _placing = true;
        try
        {
            List<Entry> available = _entries.Where(entry => entry.Control.IsVisible).ToList();
            foreach (Entry entry in available) { entry.Control.Measure(new Size(double.PositiveInfinity, 24)); }
            double width = Bounds.Width - Padding.Left - Padding.Right;
            bool overflow = available.Sum(entry => entry.Control.DesiredSize.Width + 1) > width;
            double used = 0;
            bool suffix = false;
            List<Control> popup = [];
            foreach (Entry entry in _entries)
            {
                if (!entry.Control.IsVisible) { entry.Host.IsVisible = false; continue; }
                double desired = entry.Control.DesiredSize.Width + 1;
                suffix |= used + desired > width - (overflow ? 25 : 0);
                entry.Host.IsVisible = !suffix;
                if (suffix) { popup.Add(entry.Popup); }
                else { used += desired; }
            }
            Overflow.IsVisible = popup.Count > 0;
            if (!_popupItems.SequenceEqual(popup))
            {
                _popupItems.Clear(); foreach (Control item in popup) { _popupItems.Add(item); }
            }
        }
        finally { _placing = false; }
    }

    private void ApplyPalette()
    {
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(ActualThemeVariant == ThemeVariant.Dark);
        Background = Brush.Parse(colors["Control"]);
        MenuFlyoutPresenter? presenter = _flyout.Items.OfType<Control>()
            .SelectMany(item => item.GetVisualAncestors().OfType<MenuFlyoutPresenter>()).FirstOrDefault();
        if (presenter is null) { return; }
        presenter.Background = Brush.Parse(colors["Menu"]); presenter.Foreground = Brush.Parse(colors["MenuText"]);
        presenter.BorderBrush = Brush.Parse(colors["ActiveBorder"]);
        foreach ((string key, string color) in new Dictionary<string, string>
        {
            ["MenuFlyoutItemBackgroundPointerOver"] = "MenuHighlight", ["MenuFlyoutItemBackgroundPressed"] = "MenuHighlight",
            ["MenuFlyoutItemForegroundPointerOver"] = "HighlightText", ["MenuFlyoutItemForegroundPressed"] = "HighlightText",
            ["MenuFlyoutItemKeyboardAcceleratorTextForeground"] = "MenuText",
            ["MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver"] = "HighlightText"
        }) { presenter.Resources[key] = Brush.Parse(colors[color]); }
    }
    private sealed record Entry(Control Control, Border Host, Control Popup);
}

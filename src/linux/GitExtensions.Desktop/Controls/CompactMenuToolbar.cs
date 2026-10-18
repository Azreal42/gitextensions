using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop.Controls;

/// <summary>A ToolStrip-style menu row retains its canonical commands and opens overflow menus at the chevron.</summary>
internal sealed class CompactMenuToolbar : IDisposable
{
    private static readonly ConditionalWeakTable<Menu, CompactMenuToolbar> Controllers = new();
    private readonly Menu _menu;
    private readonly MenuItem _pinned;
    private readonly IReadOnlyList<MenuItem> _canonical;
    private readonly ObservableCollection<MenuItem> _overflowItems = [];
    private readonly Dictionary<MenuItem, MenuItem> _proxies = [];
    private readonly Dictionary<MenuItem, Popup> _popups = [];
    private readonly Dictionary<MenuItem, double> _chromeWidths = [];
    private readonly Dictionary<MenuItem, EventHandler<TemplateAppliedEventArgs>> _templateHandlers = [];
    private readonly HashSet<MenuItem> _overflowed = [];
    private bool _disposed;
    internal MenuItem Overflow { get; }
    internal IReadOnlyList<MenuItem> CanonicalItems => _canonical;
    internal static CompactMenuToolbar? For(Menu menu) => Controllers.TryGetValue(menu, out var controller) ? controller : null;
    internal bool IsOverflowed(MenuItem item) => _overflowed.Contains(item);
    internal MenuItem ProxyFor(MenuItem item) => _proxies[item];
    internal event Action<MenuItem>? OpeningFromOverflow;

    internal CompactMenuToolbar(Menu menu, IEnumerable<MenuItem> items, MenuItem pinned)
    {
        _menu = menu; _canonical = Array.AsReadOnly(items.ToArray()); _pinned = pinned;
        if (!_canonical.Contains(pinned)) { throw new ArgumentException("The pinned menu must be a canonical item.", nameof(pinned)); }
        Controllers.Add(menu, this);
        Overflow = new() { Name = menu.Name + "Overflow", Header = "»", Width = 24, Height = 28, Padding = new Thickness(0), IsVisible = false, ItemsSource = _overflowItems };
        foreach (MenuItem item in _canonical)
        {
            // A ToolStrip item has compact chrome; captions and icon sizes remain complete.
            item.Padding = new Thickness(3, 1);
            EventHandler<TemplateAppliedEventArgs> handler = (_, args) =>
            {
                if (args.NameScope.Find<Popup>("PART_Popup") is Popup popup) { _popups[item] = popup; SetAnchor(item); }
            };
            _templateHandlers.Add(item, handler); item.TemplateApplied += handler;
            MenuItem proxy = CreateProxy(item); _proxies.Add(item, proxy);
        }
        menu.ItemsSource = _canonical.Append(Overflow).ToArray();
        menu.ItemsPanel = new FuncTemplate<Panel?>(() => new ToolbarPanel(this));
        menu.DetachedFromVisualTree += Detached;
    }

    internal void Reveal(MenuItem item)
    {
        if (_disposed) { return; }
        Overflow.Close();
        SetAnchor(item);
    }

    private MenuItem CreateProxy(MenuItem original)
    {
        MenuItem proxy = new() { Name = original.Name + "Overflow", Header = Caption(original.Header), Tag = original };
        if (original.Header is Control header && header.GetVisualDescendants().OfType<Image>().FirstOrDefault() is Image image)
        { proxy.Icon = new Image { Source = image.Source, Width = 16, Height = 16, Stretch = Stretch.Uniform }; }
        proxy.Bind(InputElement.IsEnabledProperty, new Binding(nameof(MenuItem.IsEnabled)) { Source = original });
        proxy.Click += (_, args) =>
        {
            args.Handled = true; _menu.Close();
            // Let the native menu finish closing its clicked leaf before opening
            // the canonical command/menu; no business handler is copied here.
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || !_menu.IsEffectivelyVisible || !_menu.IsEffectivelyEnabled || !original.IsEnabled) { return; }
                if (original.HasSubMenu)
                {
                    OpeningFromOverflow?.Invoke(original);
                    Reveal(original); original.Open(); _menu.SelectedItem = original; original.UpdateLayout();
                    MenuItem? first = original.Items.OfType<MenuItem>().FirstOrDefault(item => item.IsVisible && item.IsEnabled);
                    if (first is not null) { original.SelectedItem = first; first.Focus(NavigationMethod.Directional); }
                }
                else { original.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent) { Source = original }); }
            }, DispatcherPriority.Input);
        };
        return proxy;
    }

    private static object? Caption(object? header)
    {
        if (header is not Control control) { return header; }
        AccessText? access = control.GetSelfAndVisualDescendants().OfType<AccessText>().FirstOrDefault();
        if (access is not null) { return new AccessText { Text = access.Text }; }
        TextBlock? text = control.GetSelfAndVisualDescendants().OfType<TextBlock>().FirstOrDefault();
        return text is null ? null : new TextBlock { Text = text.Text };
    }

    private void SetAnchor(MenuItem item)
    {
        if (_popups.TryGetValue(item, out Popup? popup))
        { popup.SetCurrentValue(Popup.PlacementTargetProperty, IsOverflowed(item) ? Overflow : item); }
    }

    private double MeasureWidth(MenuItem item, double height)
    {
        if (item.IsVisible)
        {
            item.Measure(new Size(double.PositiveInfinity, height));
            if (item.Header is Control header) { _chromeWidths[item] = Math.Max(0, item.DesiredSize.Width - header.DesiredSize.Width); }
            return item.DesiredSize.Width;
        }
        // Hidden roots still own their real menu trees. Their header can be
        // measured independently, without toggling visibility during layout.
        if (item.Header is Control hiddenHeader)
        {
            hiddenHeader.Measure(new Size(double.PositiveInfinity, height));
            return hiddenHeader.DesiredSize.Width + _chromeWidths.GetValueOrDefault(item, item.Padding.Left + item.Padding.Right);
        }
        return 0;
    }

    private IReadOnlyDictionary<MenuItem, double> Place(double width, double height)
    {
        Dictionary<MenuItem, double> widths = _canonical.ToDictionary(item => item, item => MeasureWidth(item, height));
        MenuItem[] left = _canonical.Where(item => !ReferenceEquals(item, _pinned)).ToArray();
        double total = widths.Values.Sum(), available = Math.Max(0, width - widths[_pinned]);
        bool needsOverflow = total > width;
        if (needsOverflow) { available = Math.Max(0, available - Overflow.Width); }
        double used = 0; bool suffix = false; List<MenuItem> hidden = [];
        foreach (MenuItem item in left)
        {
            suffix |= used + widths[item] > available + .01;
            if (suffix) { hidden.Add(item); }
            else { used += widths[item]; }
        }
        if (!_overflowed.SetEquals(hidden))
        {
            _menu.Close();
            _overflowed.Clear(); foreach (MenuItem item in hidden) { _overflowed.Add(item); }
            _overflowItems.Clear(); foreach (MenuItem item in hidden) { _overflowItems.Add(_proxies[item]); }
        }
        foreach (MenuItem item in _canonical)
        {
            item.IsVisible = !_overflowed.Contains(item);
            SetAnchor(item);
        }
        Overflow.IsVisible = hidden.Count > 0;
        return widths;
    }

    private void Detached(object? sender, VisualTreeAttachmentEventArgs args) => _menu.Close();

    public void Dispose()
    {
        if (_disposed) { return; } _disposed = true; _menu.Close();
        _menu.DetachedFromVisualTree -= Detached;
        foreach (MenuItem item in _canonical)
        {
            item.TemplateApplied -= _templateHandlers[item]; item.IsVisible = true;
            if (_popups.TryGetValue(item, out Popup? popup)) { popup.SetCurrentValue(Popup.PlacementTargetProperty, item); }
        }
        Controllers.Remove(_menu);
    }

    private sealed class ToolbarPanel(CompactMenuToolbar toolbar) : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            double height = double.IsFinite(availableSize.Height) ? availableSize.Height : 28;
            double width = double.IsFinite(availableSize.Width) ? availableSize.Width : toolbar._menu.Bounds.Width;
            if (width <= 0) { width = double.PositiveInfinity; }
            var widths = toolbar.Place(width, height);
            foreach (Control child in Children.Where(child => child.IsVisible)) { child.Measure(new Size(double.PositiveInfinity, height)); }
            double desired = widths.Values.Sum();
            return new Size(double.IsFinite(width) ? Math.Min(width, desired) : desired, height);
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            var widths = toolbar.Place(finalSize.Width, finalSize.Height);
            double x = 0;
            foreach (MenuItem item in toolbar._canonical.Where(item => !ReferenceEquals(item, toolbar._pinned)))
            {
                if (toolbar.IsOverflowed(item)) { item.Arrange(new Rect()); }
                else { item.Arrange(new Rect(x, 0, widths[item], finalSize.Height)); x += widths[item]; }
            }
            double pinned = Math.Max(0, finalSize.Width - widths[toolbar._pinned]);
            toolbar._pinned.Arrange(new Rect(pinned, 0, widths[toolbar._pinned], finalSize.Height));
            toolbar.Overflow.Arrange(toolbar.Overflow.IsVisible
                ? new Rect(Math.Max(0, pinned - toolbar.Overflow.Width), 0, toolbar.Overflow.Width, finalSize.Height) : new Rect());
            return finalSize;
        }
    }
}

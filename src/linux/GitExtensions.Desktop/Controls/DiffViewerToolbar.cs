using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Desktop.Controls;

/// <summary>The original FileViewer toolbar floats over the text and never reserves a document row.</summary>
public sealed class DiffViewerToolbar : Border, IDisposable
{
    private readonly DiffView _view;
    private readonly TextEditor _editor;
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly List<(Image Image, Bitmap Bitmap)> _icons = [];
    private readonly Border _navigationSeparator = Separator();
    private readonly Border _contextSeparator = Separator();
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly List<Entry> _entries = [];
    private readonly List<Entry> _overflowed = [];
    private readonly ObservableCollection<Control> _popupItems = [];
    private readonly ComboBox _overflowEncoding;
    private bool _placing;
    private bool _disposed;
    public Button Overflow { get; }
    public MenuFlyout OverflowFlyout { get; } = new();
    public Button NextChange { get; }
    public Button PreviousChange { get; }
    public Button IncreaseContext { get; }
    public Button DecreaseContext { get; }
    public Button EntireFile { get; }
    public Button NonPrinting { get; }
    public Button SyntaxHighlighting { get; }
    public Button WhitespaceEol { get; }
    public Button WhitespaceChanges { get; }
    public Button AllWhitespace { get; }
    public ComboBox EncodingSelector { get; }
    public Button Settings { get; }
    public Func<Task>? NextChangeRequested { get; set; }
    public Func<Task>? PreviousChangeRequested { get; set; }
    public Task PendingSettingsOperation { get; private set; } = Task.CompletedTask;

    internal DiffViewerToolbar(DiffView view, TextEditor editor)
    {
        _view = view; _editor = editor;
        Name = "DiffViewerToolbar"; Height = 24; IsVisible = false; Child = _row;
        NextChange = IconButton("ArrowDown.png", "nextChangeButton", "Next change");
        PreviousChange = IconButton("ArrowUp.png", "previousChangeButton", "Previous change");
        NextChange.Click += async (_, _) => { if (NextChangeRequested is { } action) { await action(); } else { _view.GoToNextChange(); } };
        PreviousChange.Click += async (_, _) => { if (PreviousChangeRequested is { } action) { await action(); } else { _view.GoToPreviousChange(); } };
        IncreaseContext = view.ComparisonMenu.CreateIncreaseContextButton();
        DecreaseContext = view.ComparisonMenu.CreateDecreaseContextButton();
        EntireFile = view.ComparisonMenu.CreateWholeFileButton();
        NonPrinting = view.RenderingMenu.CreateNonPrintingButton();
        SyntaxHighlighting = view.RenderingMenu.CreateSyntaxHighlightingButton();
        WhitespaceEol = view.ComparisonMenu.CreateEolWhitespaceButton();
        WhitespaceChanges = view.ComparisonMenu.CreateWhitespaceChangesButton();
        AllWhitespace = view.ComparisonMenu.CreateAllWhitespaceButton();
        EncodingSelector = view.ReadingMenu.CreateEncodingSelector();
        Settings = IconButton("Settings.png", "settingsButton", "Settings");
        Settings.Click += (_, _) => OpenSettings();
        IReadOnlyList<object> comparison = view.ComparisonMenu.CreateContextMenuItems("DiffViewerOverflow");
        IReadOnlyList<object> rendering = view.RenderingMenu.CreateContextMenuItems("DiffViewerOverflow");
        _overflowEncoding = view.ReadingMenu.CreateEncodingSelector();
        MenuItem encoding = new() { Header = _overflowEncoding, StaysOpenOnClick = true, Height = 24, MinHeight = 24, Padding = new Thickness(7, 0) };
        MenuItem settings = PopupItem("settingsButton", "Settings", "Settings.png");
        settings.Click += (_, _) => OpenSettings();
        Add(NextChange, NavigationItem(true)); Add(PreviousChange, NavigationItem(false)); Add(_navigationSeparator, new Separator());
        Add(IncreaseContext, (Control)comparison[0]); Add(DecreaseContext, (Control)comparison[1]); Add(_contextSeparator, new Separator());
        Add(EntireFile, (Control)comparison[2]); Add(NonPrinting, (Control)rendering[0]); Add(SyntaxHighlighting, (Control)rendering[1]);
        Add(WhitespaceEol, (Control)comparison[4]); Add(WhitespaceChanges, (Control)comparison[5]); Add(AllWhitespace, (Control)comparison[6]);
        Add(EncodingSelector, encoding); Add(Settings, settings);
        // MenuFlyout.CreatePresenter captures its ItemCollection before Opening.
        // Keep a notifying source for that collection from construction onward;
        // swapping ItemsSource in Opening leaves the first presenter empty.
        OverflowFlyout.ItemsSource = _popupItems;
        Overflow = new Button
        {
            Width = 24, Height = 24, MinHeight = 24, MinWidth = 0, Padding = default, IsVisible = false,
            Content = new TextBlock { Text = "»", FontSize = 14, VerticalAlignment = VerticalAlignment.Center }, Flyout = OverflowFlyout
        };
        _row.Children.Add(Overflow);
        OverflowFlyout.Opening += (_, _) => PopulateOverflow();
        OverflowFlyout.Opened += (_, _) => { IsVisible = true; ApplyPopupPalette(); };
        OverflowFlyout.Closed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && !PopupOpen && !_view.IsPointerOver && !IsPointerOver) { IsVisible = false; }
        });
        SetNames("DiffViewer");
        _editor.PointerMoved += TextPointerMoved;
        _editor.PointerExited += TextPointerExited;
        _view.PointerExited += ViewerPointerExited;
        _view.DetachedFromVisualTree += Detached;
        _view.SizeChanged += ViewerSizeChanged;
        ActualThemeVariantChanged += ThemeChanged;
        ApplyPalette();
    }

    public void SetNames(string prefix, string? toolbarName = null)
    {
        Name = toolbarName ?? prefix + "Toolbar";
        NextChange.Name = prefix + "NextChange"; PreviousChange.Name = prefix + "PreviousChange";
        IncreaseContext.Name = prefix + "IncreaseContext"; DecreaseContext.Name = prefix + "DecreaseContext";
        EntireFile.Name = prefix + "EntireFile"; NonPrinting.Name = prefix + "NonPrinting";
        SyntaxHighlighting.Name = prefix + "SyntaxHighlighting";
        WhitespaceEol.Name = prefix + "Whitespace_Eol"; WhitespaceChanges.Name = prefix + "Whitespace_Change";
        AllWhitespace.Name = prefix + "Whitespace_AllSpace"; Settings.Name = prefix + "Settings";
        Overflow.Name = prefix + "Overflow";
        _overflowEncoding.Name = prefix + "OverflowEncoding";
        foreach (Entry entry in _entries)
        {
            if (entry.Popup is MenuItem item && entry.Control.Name is { } name)
            {
                if (entry.Control == EncodingSelector) { item.Name = prefix + "OverflowEncodingMenu"; continue; }
                item.Name = prefix + "Overflow" + name[prefix.Length..] + (entry.Control == NonPrinting || entry.Control == SyntaxHighlighting
                    || entry.Control == IncreaseContext || entry.Control == DecreaseContext || entry.Control == EntireFile ? "Menu" : "");
            }
        }
    }

    internal void Refresh(bool partial)
    {
        if (_disposed) { return; }
        NextChange.IsVisible = PreviousChange.IsVisible = _navigationSeparator.IsVisible = partial;
        NextChange.IsEnabled = PreviousChange.IsEnabled = _view.IsDocumentReady;
        _contextSeparator.IsVisible = IncreaseContext.IsVisible || DecreaseContext.IsVisible;
        Settings.IsEnabled = _view.IsDocumentReady;
        Overflow.IsEnabled = _view.IsDocumentReady;
        if (!_view.IsDocumentReady) { OverflowFlyout.Hide(); IsVisible = false; }
        if (IsVisible) { Place(); }
    }

    private void TextPointerMoved(object? sender, PointerEventArgs args)
    {
        if (_disposed || _view.RenderingMenu.IsHostBusy || !_view.IsEffectivelyEnabled) { return; }
        // A failed reload invalidates staging, but the original reader still
        // offers its codec selector so the user can recover without closing it.
        Settings.IsEnabled = Overflow.IsEnabled = true;
        if (!IsVisible) { IsVisible = true; Place(); }
    }
    private void TextPointerExited(object? sender, PointerEventArgs args)
    {
        Point point = args.GetPosition(this);
        if (!new Rect(Bounds.Size).Contains(point) && !PopupOpen) { IsVisible = false; }
    }
    private void ViewerPointerExited(object? sender, PointerEventArgs args)
    {
        if (!new Rect(_view.Bounds.Size).Contains(args.GetPosition(_view)) && !PopupOpen) { IsVisible = false; }
    }
    private bool PopupOpen => OverflowFlyout.IsOpen || EncodingSelector.IsDropDownOpen || _overflowEncoding.IsDropDownOpen;
    private void Detached(object? sender, VisualTreeAttachmentEventArgs args) { OverflowFlyout.Hide(); IsVisible = false; }
    private void ViewerSizeChanged(object? sender, SizeChangedEventArgs args) { if (IsVisible) { Place(); } }
    private void Place()
    {
        if (_placing || _disposed) { return; }
        _placing = true;
        try
        {
            double paneWidth = Math.Max(0, _view.Bounds.Width);
            double available = Math.Min(paneWidth, Math.Max(24, paneWidth - 40));
            double natural = 0;
            foreach (Entry entry in _entries)
            { entry.Control.Measure(new Size(double.PositiveInfinity, 24)); if (entry.Control.IsVisible) { natural += entry.Control.DesiredSize.Width; } }
            bool overflows = natural > available;
            double remaining = overflows ? Math.Max(0, available - 24) : available;
            bool exhausted = false;
            _overflowed.Clear();
            foreach (Entry entry in _entries)
            {
                double width = entry.Control.DesiredSize.Width;
                bool fits = !exhausted && entry.Control.IsVisible && width <= remaining;
                if (entry.Control.IsVisible && !fits) { exhausted = true; _overflowed.Add(entry); }
                entry.Host.IsVisible = fits;
                if (fits) { remaining -= width; }
            }
            // Do not leave a separator alone at the end of the retained prefix.
            Entry? last = _entries.LastOrDefault(entry => entry.Host.IsVisible);
            if (last is not null && last.Popup is Separator) { last.Host.IsVisible = false; _overflowed.Insert(0, last); }
            Overflow.IsVisible = overflows;
            Overflow.Width = Math.Min(24, paneWidth);
            Width = double.NaN;
            Measure(new Size(double.PositiveInfinity, 24));
            Width = Math.Min(paneWidth, DesiredSize.Width);
            Canvas.SetLeft(this, Math.Max(0, paneWidth - Width - 40));
            Canvas.SetTop(this, 0);
            PopulateOverflow();
        }
        finally { _placing = false; }
    }
    private void Add(Control control, Control popup)
    {
        Border host = new() { Child = control };
        _entries.Add(new(control, host, popup)); _row.Children.Add(host);
        control.PropertyChanged += ItemChanged;
    }
    private void ItemChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == IsVisibleProperty && IsVisible) { Place(); }
    }
    private void PopulateOverflow()
    {
        if (_disposed) { return; }
        List<Control> items = [];
        foreach (Entry entry in _overflowed.Where(entry => entry.Control.IsVisible))
        {
            if (entry.Popup is Separator && (items.Count == 0 || items[^1] is Separator)) { continue; }
            if (entry.Control == NextChange || entry.Control == PreviousChange || entry.Control == Settings || entry.Control == EncodingSelector)
            { entry.Popup.IsEnabled = entry.Control.IsEnabled; }
            items.Add(entry.Popup);
        }
        if (items.Count > 0 && items[^1] is Separator) { items.RemoveAt(items.Count - 1); }
        if (_popupItems.SequenceEqual(items)) { return; }
        _popupItems.Clear();
        foreach (Control item in items) { _popupItems.Add(item); }
    }
    private MenuItem NavigationItem(bool next)
    {
        MenuItem item = PopupItem(next ? "nextChangeButton" : "previousChangeButton", next ? "Next change" : "Previous change", next ? "ArrowDown.png" : "ArrowUp.png");
        item.InputGesture = new(next ? Key.Down : Key.Up, KeyModifiers.Alt);
        item.Click += async (_, _) =>
        {
            if (next) { if (NextChangeRequested is { } action) { await action(); } else { _view.GoToNextChange(); } }
            else { if (PreviousChangeRequested is { } action) { await action(); } else { _view.GoToPreviousChange(); } }
        };
        return item;
    }
    private MenuItem PopupItem(string original, string text, string asset)
        => new() { Header = new AccessText { Text = _translations.Translate("FileViewer", original, "ToolTipText", text) },
            Icon = CreateIcon(asset), Height = 24, MinHeight = 24, Padding = new Thickness(7, 2) };
    private void OpenSettings()
    {
        if (!_disposed && _view.IsEffectivelyEnabled && TopLevel.GetTopLevel(_view) is Window { IsVisible: true } owner)
        { PendingSettingsOperation = ShowSettingsAsync(owner); }
    }
    private async Task ShowSettingsAsync(Window owner)
    {
        await NativeDiffViewerSettingsDialog.ShowAsync(owner, _view.PreferencesStore);
        if (!_disposed && owner.IsVisible && _view.IsEffectivelyEnabled && TopLevel.GetTopLevel(_view) == owner)
        { owner.Activate(); _editor.TextArea.Focus(); }
    }
    private Button IconButton(string asset, string original, string tooltip)
    {
        Button button = new() { Content = CreateIcon(asset), Width = 24, Height = 24, MinHeight = 24, MinWidth = 0, Padding = default };
        ToolTip.SetTip(button, _translations.Translate("FileViewer", original, "ToolTipText", tooltip));
        return button;
    }
    private Image CreateIcon(string asset)
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + asset));
        Bitmap bitmap = new(stream); Image image = new() { Source = bitmap, Width = 16, Height = 16, Stretch = Stretch.Uniform };
        _icons.Add((image, bitmap)); return image;
    }
    private static Border Separator() => new() { Width = 7, Height = 18, Margin = new Thickness(0, 3), BorderThickness = new Thickness(1, 0, 0, 0) };
    private void ThemeChanged(object? sender, EventArgs args) => ApplyPalette();
    private void ApplyPalette()
    {
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(ActualThemeVariant == ThemeVariant.Dark);
        Background = Brush.Parse(colors["Control"]);
        _navigationSeparator.BorderBrush = _contextSeparator.BorderBrush = Brush.Parse(colors["ControlDark"]);
        Overflow.Foreground = Brush.Parse(colors["ControlText"]);
        ApplyPopupPalette();
    }
    private void ApplyPopupPalette()
    {
        MenuFlyoutPresenter? presenter = OverflowFlyout.Items.OfType<MenuItem>()
            .SelectMany(item => item.GetVisualAncestors().OfType<MenuFlyoutPresenter>()).FirstOrDefault();
        if (presenter is null) { return; }
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(ActualThemeVariant == ThemeVariant.Dark);
        presenter.Background = Brush.Parse(colors["Menu"]); presenter.BorderBrush = Brush.Parse(colors["ActiveBorder"]);
        presenter.Foreground = Brush.Parse(colors["MenuText"]);
        foreach ((string resource, string color) in new Dictionary<string, string>
        {
            ["MenuFlyoutItemBackgroundPointerOver"] = "MenuHighlight", ["MenuFlyoutItemBackgroundPressed"] = "MenuHighlight",
            ["MenuFlyoutItemForegroundPointerOver"] = "HighlightText", ["MenuFlyoutItemForegroundPressed"] = "HighlightText",
            ["MenuFlyoutItemKeyboardAcceleratorTextForeground"] = "MenuText",
            ["MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver"] = "HighlightText",
            ["MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed"] = "HighlightText"
        }) { presenter.Resources[resource] = Brush.Parse(colors[color]); }
    }
    public void Dispose()
    {
        if (_disposed) { return; } _disposed = true; IsVisible = false;
        OverflowFlyout.Hide(); Overflow.Flyout = null; _popupItems.Clear();
        foreach (Entry entry in _entries) { entry.Control.PropertyChanged -= ItemChanged; }
        _editor.PointerMoved -= TextPointerMoved; _editor.PointerExited -= TextPointerExited;
        _view.PointerExited -= ViewerPointerExited; _view.DetachedFromVisualTree -= Detached; _view.SizeChanged -= ViewerSizeChanged;
        ActualThemeVariantChanged -= ThemeChanged;
        foreach ((Image image, Bitmap bitmap) in _icons) { image.Source = null; bitmap.Dispose(); }
        _icons.Clear();
    }
    private sealed record Entry(Control Control, Border Host, Control Popup);
}

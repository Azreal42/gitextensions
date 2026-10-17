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
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Desktop.Controls;

/// <summary>Original nonprinting and partial-text syntax choices, held locally by each reader.</summary>
public sealed class DiffViewerRenderingMenu : IDisposable
{
    private readonly DiffViewerPreferencesStore _store;
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly List<(MenuItem Item, bool Syntax)> _items = [];
    private readonly List<(ToggleButton Button, bool Syntax)> _buttons = [];
    private readonly Dictionary<string, Bitmap> _originals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap> _dark = new(StringComparer.Ordinal);
    private readonly List<(Image Image, EventHandler Handler)> _images = [];
    private DiffViewerPreferences _preferences;
    private bool _supportsNonPrinting = true, _supportsSyntax = true, _busy, _hostBusy, _disposed;

    public DiffViewerRenderingMenu(DiffViewerPreferencesStore? store = null)
    {
        _store = store ?? new(); Options = _store.BeginRenderingViewerSession();
        _preferences = _store.Load(); _store.Changed += StoreChanged;
    }

    public DiffViewerRenderingOptions Options { get; private set; }
    public bool RememberNonPrintingChars => _preferences.RememberNonPrintingChars;
    public bool RememberSyntaxHighlightingInDiff => _preferences.RememberSyntaxHighlightingInDiff;
    public event EventHandler? OptionsChanged;
    internal bool IsHostBusy => _hostBusy;

    /// <summary>Syntax is a choice only for partial text. Full text always uses its automatic grammar in the host.</summary>
    public void SetContext(bool supportsNonPrinting, bool supportsSyntax, bool isBusy = false)
    { _supportsNonPrinting = supportsNonPrinting; _supportsSyntax = supportsSyntax; _busy = isBusy; Refresh(); }
    public void SetHostBusy(bool value) { _hostBusy = value; Refresh(); }

    public bool SetOptions(DiffViewerRenderingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBlocked || options == Options) { return false; }
        if (options.ShowNonPrintingChars != Options.ShowNonPrintingChars && !_supportsNonPrinting) { return false; }
        if (options.ShowSyntaxHighlightingInDiff != Options.ShowSyntaxHighlightingInDiff && !_supportsSyntax) { return false; }
        if (!_store.SaveRenderingOptions(Options, options)) { Refresh(); return false; }
        Options = options; Refresh(); OptionsChanged?.Invoke(this, EventArgs.Empty); return true;
    }
    public bool ToggleNonPrinting() => !_disposed && SetOptions(Options with { ShowNonPrintingChars = !Options.ShowNonPrintingChars });
    public bool ToggleSyntaxHighlighting() => !_disposed && SetOptions(Options with { ShowSyntaxHighlightingInDiff = !Options.ShowSyntaxHighlightingInDiff });

    public bool HandleKeyDown(KeyEventArgs args)
    {
        if (_disposed || IsBlocked || args.Handled || !_supportsSyntax || DesktopHotkeys.Match("FileViewer", args)?.Name != "ShowSyntaxHighlighting") { return false; }
        args.Handled = true; ToggleSyntaxHighlighting(); return true;
    }

    public IReadOnlyList<object> CreateContextMenuItems(string? prefix = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); string names = prefix ?? "DiffViewer";
        MenuItem nonPrinting = Item(names + "NonPrintingMenu", false, "showNonprintableCharactersToolStripMenuItem", "S&how nonprinting characters", "ShowWhitespace.png");
        MenuItem syntax = Item(names + "SyntaxHighlightingMenu", true, "showSyntaxHighlightingToolStripMenuItem", "Show synta&x highlighting", "SyntaxHighlighting.png");
        DesktopHotkeys.Bind(syntax, "FileViewer", "ShowSyntaxHighlighting"); Refresh(); return [nonPrinting, syntax];
    }
    public Button CreateNonPrintingButton(string? name = null) => Button(name ?? "DiffViewerNonPrinting", false, "ShowWhitespace.png", "showNonPrintChars", "Show nonprinting characters");
    public Button CreateSyntaxHighlightingButton(string? name = null) => Button(name ?? "DiffViewerSyntaxHighlighting", true, "SyntaxHighlighting.png", "showSyntaxHighlighting", "Show syntax highlighting");

    public void Refresh()
    {
        if (_disposed) { return; }
        foreach ((MenuItem item, bool syntax) in _items)
        { item.IsVisible = Available(syntax); item.IsEnabled = !IsBlocked && Available(syntax); item.IsChecked = Checked(syntax); }
        foreach ((ToggleButton button, bool syntax) in _buttons)
        { button.IsVisible = Available(syntax); button.IsEnabled = !IsBlocked && Available(syntax); button.IsChecked = Checked(syntax); }
    }

    private bool IsBlocked => _busy || _hostBusy;
    private bool Available(bool syntax) => syntax ? _supportsSyntax : _supportsNonPrinting;
    private bool Checked(bool syntax) => syntax ? Options.ShowSyntaxHighlightingInDiff : Options.ShowNonPrintingChars;
    private void Execute(bool syntax) { if (syntax) { ToggleSyntaxHighlighting(); } else { ToggleNonPrinting(); } Refresh(); }
    private MenuItem Item(string name, bool syntax, string original, string text, string asset)
    {
        MenuItem item = new()
        {
            Name = name, Header = new AccessText { Text = _translations.TranslateAccessKeys("FileViewer", original, "Text", text) },
            Icon = Icon(asset), ToggleType = MenuItemToggleType.CheckBox
        };
        item.Click += (_, _) => Execute(syntax); _items.Add((item, syntax)); return item;
    }
    private Button Button(string name, bool syntax, string asset, string original, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ToggleButton button = new()
        {
            Name = name, Content = Icon(asset), Width = 24, Height = 24, MinWidth = 0, MinHeight = 24, Padding = default,
            Resources = ButtonResources()
        };
        string caption = _translations.Translate("FileViewer", original, "ToolTipText", tooltip);
        if (syntax) { DesktopHotkeys.BindToolTip(button, "FileViewer", "ShowSyntaxHighlighting", caption); }
        else { ToolTip.SetTip(button, caption); }
        button.Click += (_, _) => Execute(syntax); _buttons.Add((button, syntax)); Refresh(); return button;
    }
    private Image Icon(string asset)
    {
        if (!_originals.TryGetValue(asset, out Bitmap? bitmap))
        { using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + asset)); _originals[asset] = bitmap = new(stream); }
        Bitmap original = bitmap;
        Image image = new() { Source = original, Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, UseLayoutRounding = true };
        void UpdateSource()
        {
            if (_disposed) { return; }
            if (image.ActualThemeVariant == ThemeVariant.Dark)
            {
                if (!_dark.TryGetValue(asset, out Bitmap? adapted))
                {
                    IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(dark: true);
                    _dark[asset] = adapted = DiffViewerRenderingBitmap.CreateDark(original, Color.Parse(colors["WindowText"]), Color.Parse(colors["Window"]));
                }
                image.Source = adapted;
            }
            else { image.Source = original; }
        }
        EventHandler handler = (_, _) => UpdateSource(); image.ActualThemeVariantChanged += handler;
        _images.Add((image, handler)); UpdateSource(); return image;
    }
    private static ResourceDictionary ButtonResources()
        => new() { ThemeDictionaries = { [ThemeVariant.Light] = ButtonThemeResources(false), [ThemeVariant.Dark] = ButtonThemeResources(true) } };
    private static ResourceDictionary ButtonThemeResources(bool dark)
    {
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(dark); ResourceDictionary resources = new();
        foreach (string state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed" })
        { resources["ToggleButtonBackground" + state] = Brush.Parse(colors["Highlight"]); resources["ToggleButtonBorderBrush" + state] = Brush.Parse(colors["Highlight"]); }
        return resources;
    }
    private void StoreChanged(object? sender, EventArgs args)
    {
        if (_disposed) { return; }
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => StoreChanged(sender, args)); return; }
        _preferences = _store.Load(); Refresh();
    }
    public void Dispose()
    {
        if (_disposed) { return; } _disposed = true; _store.Changed -= StoreChanged;
        foreach ((MenuItem item, _) in _items) { item.IsEnabled = false; item.IsSubMenuOpen = false; }
        foreach ((ToggleButton button, _) in _buttons) { button.IsEnabled = false; }
        foreach ((Image image, EventHandler handler) in _images) { image.Source = null; image.ActualThemeVariantChanged -= handler; }
        foreach (Bitmap bitmap in _dark.Values) { bitmap.Dispose(); } foreach (Bitmap bitmap in _originals.Values) { bitmap.Dispose(); }
        _items.Clear(); _buttons.Clear(); _images.Clear(); _dark.Clear(); _originals.Clear();
    }
}

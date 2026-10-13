using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop.Controls;

/// <summary>The original command diagrams, hover alternative and remembered Show/Hide help.</summary>
public sealed class OriginalCommandHelp : UserControl, IDisposable
{
    private readonly string _primaryAsset;
    private readonly string? _alternateAsset;
    private readonly string _settingsId;
    private readonly string? _hoverNotice;
    private readonly OriginalCommandHelpPreferencesStore _store = new();
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly Dictionary<string, Bitmap> _originals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap> _adapted = new(StringComparer.Ordinal);
    private readonly Image _image = new() { Name = "OriginalCommandHelpImage", Width = 289, Height = 373, Stretch = Stretch.None };
    private readonly TextBlock _notice = new() { Name = "OriginalCommandHelpNotice", TextWrapping = TextWrapping.Wrap, MaxWidth = 289 };
    private readonly Button _toggle = new() { Name = "OriginalCommandHelpToggle", HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 24, Padding = new Thickness(4, 0) };
    private bool _expanded = true, _alternateOnHover, _hover, _attached, _disposed;

    public OriginalCommandHelp(string primaryAsset, string? alternateAsset = null, string settingsId = "", string? hoverNotice = null)
    {
        _primaryAsset = AssetName(primaryAsset); _alternateAsset = alternateAsset is null ? null : AssetName(alternateAsset);
        _settingsId = settingsId ?? throw new ArgumentNullException(nameof(settingsId)); _hoverNotice = hoverNotice;
        Name = "OriginalCommandHelp";
        try { _expanded = _store.Load(_settingsId); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { LastError = exception.Message; }
        Content = new StackPanel { Spacing = 4, Children = { _toggle, _notice, _image } };
        _toggle.Click += (_, _) => IsExpanded = !IsExpanded;
        PointerEntered += (_, _) => { _hover = true; UpdateImage(); };
        PointerExited += (_, _) => { _hover = false; UpdateImage(); };
        ActualThemeVariantChanged += ThemeChanged;
        UpdateState();
    }

    public event EventHandler? ExpandedChanged;
    public string? LastError { get; private set; }
    public bool UseAlternateOnHover
    {
        get => _alternateOnHover;
        set { if (_alternateOnHover == value) { return; } _alternateOnHover = value; UpdateState(); UpdateImage(); }
    }
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_disposed || _expanded == value) { return; }
            _expanded = value;
            if (_attached && _settingsId.Length > 0)
            {
                try { _store.Save(_settingsId, value); LastError = null; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { LastError = exception.Message; }
            }
            UpdateState(); UpdateImage(); ExpandedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_disposed) { return; }
        _attached = true; _hover = false; UpdateState(); UpdateImage();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false; _hover = false; ReleaseImages(); base.OnDetachedFromVisualTree(e);
    }
    private void ThemeChanged(object? sender, EventArgs e) { UpdateState(); UpdateImage(); }
    private void UpdateState()
    {
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(dark);
        Background = new SolidColorBrush(Color.Parse(colors["Window"]));
        _notice.Foreground = new SolidColorBrush(Color.Parse(colors["WindowText"]));
        _image.IsVisible = _expanded;
        _toggle.Content = _expanded ? _translations.Translate("HelpImageDisplayUserControl", "linkLabelHide", "Text", "Hide help")
            : _translations.Translate("HelpImageDisplayUserControl", "linkLabelShowHelp", "Text", "Show\r\nhelp").Replace("\r\n", " ", StringComparison.Ordinal);
        _notice.Text = LastError is not null ? _translations.Translate("Unable to save settings.") + " " + LastError
            : _hoverNotice is null ? "" : _translations.Translate(_hoverNotice);
        _notice.IsVisible = LastError is not null || _expanded && _alternateOnHover && _alternateAsset is not null && !string.IsNullOrEmpty(_hoverNotice);
        Width = _expanded ? 289 : double.NaN;
    }
    private void UpdateImage()
    {
        if (!_attached || _disposed || !_expanded) { _image.Source = null; return; }
        string asset = _hover && _alternateOnHover && _alternateAsset is not null ? _alternateAsset : _primaryAsset;
        if (!_originals.TryGetValue(asset, out Bitmap? original))
        {
            using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Help/" + asset));
            _originals[asset] = original = new Bitmap(stream);
        }
        Bitmap source = original;
        if (ActualThemeVariant == ThemeVariant.Dark)
        {
            if (!_adapted.TryGetValue(asset, out Bitmap? adapted))
            {
                IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(dark: true);
                _adapted[asset] = adapted = OriginalCommandHelpBitmap.Adapt(original, Color.Parse(colors["WindowText"]), Color.Parse(colors["Window"]));
            }
            source = adapted;
        }
        _image.Tag = asset; _image.Source = source;
    }
    private void ReleaseImages()
    {
        _image.Source = null;
        foreach (Bitmap bitmap in _adapted.Values) { bitmap.Dispose(); }
        foreach (Bitmap bitmap in _originals.Values) { bitmap.Dispose(); }
        _adapted.Clear(); _originals.Clear();
    }
    public void Dispose()
    {
        if (_disposed) { return; } _disposed = true; ActualThemeVariantChanged -= ThemeChanged; _toggle.IsEnabled = false; ReleaseImages();
    }
    private static string AssetName(string asset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asset);
        if (asset != Path.GetFileName(asset) || asset.Contains('\\')) { throw new ArgumentException("Provide a Help PNG asset name.", nameof(asset)); }
        return asset.EndsWith(".png", StringComparison.Ordinal) ? asset : asset + ".png";
    }
}

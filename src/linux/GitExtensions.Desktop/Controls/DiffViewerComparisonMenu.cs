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
using GitCommands.Settings;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Desktop.Controls;

/// <summary>Original FileViewer comparison choices: local reader options and shared defaults for future readers.</summary>
public sealed class DiffViewerComparisonMenu : IDisposable
{
    private readonly DiffViewerPreferencesStore _store;
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly List<(MenuItem Item, Command Command)> _items = [];
    private readonly List<(Button Button, Command Command)> _buttons = [];
    private readonly Dictionary<string, Bitmap> _bitmaps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap> _darkMonochromeBitmaps = new(StringComparer.Ordinal);
    private readonly List<Image> _images = [];
    private readonly List<(Image Image, EventHandler Handler)> _themeHandlers = [];
    private DiffViewerPreferences _preferences;
    private GitComparisonDiffOptions _options = new();
    private bool _supportsContext = true, _supportsWhitespace = true, _supportsEolWhitespace = true, _busy, _hostBusy, _disposed;

    public DiffViewerComparisonMenu(DiffViewerPreferencesStore? store = null)
    {
        _store = store ?? new();
        Options = _store.BeginComparisonViewerSession();
        _preferences = _store.Load();
        _store.Changed += StoreChanged;
    }

    public GitComparisonDiffOptions Options
    {
        get => _store.ApplyGeneralAlgorithm(_options);
        private set => _options = value;
    }
    public bool RememberWhitespace => _preferences.RememberWhitespace;
    public bool RememberContextLines => _preferences.RememberContextLines;
    public bool RememberWholeFile => _preferences.RememberWholeFile;
    public event EventHandler? OptionsChanged;

    public void SetContext(bool supportsContext, bool supportsWhitespace, bool supportsEolWhitespace = true, bool isBusy = false)
    {
        _supportsContext = supportsContext; _supportsWhitespace = supportsWhitespace;
        _supportsEolWhitespace = supportsEolWhitespace; _busy = isBusy; Refresh();
    }
    public void SetHostBusy(bool value) { _hostBusy = value; Refresh(); }

    public bool SetOptions(GitComparisonDiffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(options.IgnoreWhitespace) || options.ContextLines is < 0 or > 100000) { throw new ArgumentOutOfRangeException(nameof(options)); }
        if (IsBlocked) { return false; }
        GitComparisonDiffOptions previous = Options;
        options = options with { UseHistogram = previous.UseHistogram };
        if (options == previous) { return false; }
        if (options.ContextLines != previous.ContextLines && (!_supportsContext || previous.ShowEntireFile || options.ShowEntireFile)) { return false; }
        if (options.ShowEntireFile != previous.ShowEntireFile && !_supportsContext) { return false; }
        if (options.IgnoreWhitespace != previous.IgnoreWhitespace && !WhitespaceAvailable(options.IgnoreWhitespace)) { return false; }
        bool changesSharedOptions = previous.IgnoreWhitespace != options.IgnoreWhitespace || previous.ContextLines != options.ContextLines || previous.ShowEntireFile != options.ShowEntireFile;
        if (changesSharedOptions && !_store.SaveComparisonOptions(previous, options)) { Refresh(); return false; }
        Options = options; Refresh(); OptionsChanged?.Invoke(this, EventArgs.Empty); return true;
    }

    public bool ToggleWholeFile() => !_disposed && !IsBlocked && _supportsContext && SetOptions(_options with { ShowEntireFile = !_options.ShowEntireFile });
    public bool IncreaseContext() => CanChangeContext && SetOptions(_options with { ContextLines = Math.Min(100000, _options.ContextLines + 1) });
    public bool DecreaseContext() => CanChangeContext && SetOptions(_options with { ContextLines = Math.Max(0, _options.ContextLines - 1) });
    public bool SelectWhitespace(IgnoreWhitespaceKind value)
    {
        if (!Enum.IsDefined(value)) { throw new ArgumentOutOfRangeException(nameof(value)); }
        if (_disposed || IsBlocked || !WhitespaceAvailable(value)) { return false; }
        return SetOptions(_options with { IgnoreWhitespace = _options.IgnoreWhitespace == value ? IgnoreWhitespaceKind.None : value });
    }
    public bool SetRememberWhitespace(bool value) => !_disposed && !IsBlocked && _store.SaveComparisonRememberFlags(whitespace: value);
    public bool SetRememberContextLines(bool value) => !_disposed && !IsBlocked && _store.SaveComparisonRememberFlags(contextLines: value);
    public bool SetRememberWholeFile(bool value) => !_disposed && !IsBlocked && _store.SaveComparisonRememberFlags(wholeFile: value);
    public bool SaveCurrentViewSettingsAsDefault() => !_disposed && !IsBlocked && _store.SaveCurrentViewSettingsAsDefault();

    public bool HandleKeyDown(KeyEventArgs args)
    {
        if (_disposed || IsBlocked || args.Handled) { return false; }
        Command? command = DesktopHotkeys.Match("FileViewer", args)?.Name switch
        {
            "ShowEntireFile" => Command.Whole,
            "IgnoreAllWhitespace" => Command.All,
            "IncreaseNumberOfVisibleLines" => Command.Increase,
            "DecreaseNumberOfVisibleLines" => Command.Decrease,
            _ => null
        };
        if (command is not { } selected || !Enabled(selected)) { return false; }
        args.Handled = true; Execute(selected); return true;
    }

    public IReadOnlyList<object> CreateContextMenuItems(string? prefix = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); string names = prefix ?? "DiffViewer";
        MenuItem eol = Item(names + "Whitespace_Eol", "FileViewer", "ignoreWhitespaceAtEolToolStripMenuItem", "Ignore whitespace changes at end of &line", Command.Eol, "WhitespaceIgnoreEol.png");
        MenuItem change = Item(names + "Whitespace_Change", "FileViewer", "ignoreWhitespaceChangesToolStripMenuItem", "Ignore changes in &amount of whitespace", Command.Change, "WhitespaceIgnore.png");
        MenuItem all = Item(names + "Whitespace_AllSpace", "FileViewer", "ignoreAllWhitespaceChangesToolStripMenuItem", "Ignore a&ll whitespace changes", Command.All, "WhitespaceIgnoreAll.png");
        MenuItem increase = Item(names + "IncreaseContextMenu", "FileViewer", "increaseNumberOfLinesToolStripMenuItem", "&Increase the number of lines of context", Command.Increase, "NumberOfLinesIncrease.png");
        MenuItem decrease = Item(names + "DecreaseContextMenu", "FileViewer", "decreaseNumberOfLinesToolStripMenuItem", "&Decrease the number of lines of context", Command.Decrease, "NumberOfLinesDecrease.png");
        MenuItem whole = Item(names + "EntireFileMenu", "FileViewer", "showEntireFileToolStripMenuItem", "Show &entire file", Command.Whole, "ShowEntireFile.png");
        DesktopHotkeys.Bind(increase, "FileViewer", "IncreaseNumberOfVisibleLines"); DesktopHotkeys.Bind(decrease, "FileViewer", "DecreaseNumberOfVisibleLines");
        DesktopHotkeys.Bind(whole, "FileViewer", "ShowEntireFile"); DesktopHotkeys.Bind(all, "FileViewer", "IgnoreAllWhitespace");
        Refresh();
        return [increase, decrease, whole, new Separator(), eol, change, all];
    }

    public Button CreateIncreaseContextButton(string? name = null) => Button(name ?? "DiffViewerIncreaseContext", Command.Increase, "NumberOfLinesIncrease.png", "increaseNumberOfLines", "Increase the number of lines of context");
    public Button CreateDecreaseContextButton(string? name = null) => Button(name ?? "DiffViewerDecreaseContext", Command.Decrease, "NumberOfLinesDecrease.png", "decreaseNumberOfLines", "Decrease the number of lines of context");
    public Button CreateWholeFileButton(string? name = null) => Button(name ?? "DiffViewerEntireFile", Command.Whole, "ShowEntireFile.png", "showEntireFileButton", "Show entire file");
    public Button CreateEolWhitespaceButton(string? name = null) => Button(name ?? "DiffViewerWhitespace_Eol", Command.Eol, "WhitespaceIgnoreEol.png", "ignoreWhitespaceAtEol", "Ignore whitespace changes at end of line");
    public Button CreateWhitespaceChangesButton(string? name = null) => Button(name ?? "DiffViewerWhitespace_Change", Command.Change, "WhitespaceIgnore.png", "ignoreWhiteSpaces", "Ignore changes in amount of whitespace");
    public Button CreateAllWhitespaceButton(string? name = null) => Button(name ?? "DiffViewerWhitespace_AllSpace", Command.All, "WhitespaceIgnoreAll.png", "ignoreAllWhitespaces", "Ignore all whitespace changes");

    public void Refresh()
    {
        if (_disposed) { return; }
        GitComparisonDiffOptions options = _options;
        foreach ((MenuItem item, Command command) in _items)
        {
            item.IsVisible = Visible(command); item.IsEnabled = Enabled(command);
            item.IsChecked = command switch
            {
                Command.Eol => (int)options.IgnoreWhitespace >= (int)IgnoreWhitespaceKind.Eol,
                Command.Change => (int)options.IgnoreWhitespace >= (int)IgnoreWhitespaceKind.Change,
                Command.All => options.IgnoreWhitespace == IgnoreWhitespaceKind.AllSpace,
                Command.Whole => options.ShowEntireFile,
                _ => false
            };
        }
        foreach ((Button button, Command command) in _buttons)
        {
            button.IsVisible = Visible(command); button.IsEnabled = Enabled(command);
            if (button is ToggleButton toggle)
            {
                toggle.IsChecked = command switch
                {
                    Command.Whole => options.ShowEntireFile,
                    Command.Eol => (int)options.IgnoreWhitespace >= (int)IgnoreWhitespaceKind.Eol,
                    Command.Change => (int)options.IgnoreWhitespace >= (int)IgnoreWhitespaceKind.Change,
                    Command.All => options.IgnoreWhitespace == IgnoreWhitespaceKind.AllSpace,
                    _ => false
                };
            }
        }
    }

    private bool IsBlocked => _busy || _hostBusy;
    private bool CanChangeContext => !_disposed && !IsBlocked && _supportsContext && !_options.ShowEntireFile;
    private bool WhitespaceAvailable(IgnoreWhitespaceKind kind) => kind is IgnoreWhitespaceKind.None or IgnoreWhitespaceKind.Eol ? _supportsEolWhitespace : _supportsWhitespace;
    private bool Visible(Command command) => command switch
    {
        Command.Eol => _supportsEolWhitespace, Command.Change or Command.All => _supportsWhitespace,
        Command.Increase or Command.Decrease or Command.Whole => _supportsContext, _ => true
    };
    private bool Enabled(Command command) => !_disposed && !IsBlocked && (command is Command.Increase or Command.Decrease ? CanChangeContext : Visible(command));
    private void Execute(Command command)
    {
        switch (command)
        {
            case Command.Eol: SelectWhitespace(IgnoreWhitespaceKind.Eol); break;
            case Command.Change: SelectWhitespace(IgnoreWhitespaceKind.Change); break;
            case Command.All: SelectWhitespace(IgnoreWhitespaceKind.AllSpace); break;
            case Command.Increase: IncreaseContext(); break; case Command.Decrease: DecreaseContext(); break;
            case Command.Whole: ToggleWholeFile(); break;
        }
    }
    private MenuItem Item(string name, string category, string original, string source, Command command, string? icon = null)
    {
        MenuItem item = new() { Name = name, Header = new AccessText { Text = _translations.TranslateAccessKeys(category, original, "Text", source) } };
        if (command is not (Command.Increase or Command.Decrease)) { item.ToggleType = MenuItemToggleType.CheckBox; }
        if (icon is not null) { item.Icon = Icon(icon); }
        item.Click += (_, _) => { Execute(command); Refresh(); }; _items.Add((item, command)); return item;
    }
    private Button Button(string name, Command command, string icon, string original, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Button button = command is Command.Whole or Command.Eol or Command.Change or Command.All ? new ToggleButton() : new Button();
        button.Name = name; button.Width = button.Height = button.MinHeight = 24; button.MinWidth = 0; button.Padding = default; button.Content = Icon(icon);
        button.Resources = new ResourceDictionary { ThemeDictionaries = { [ThemeVariant.Light] = ButtonResources(false), [ThemeVariant.Dark] = ButtonResources(true) } };
        string caption = _translations.Translate("FileViewer", original, "ToolTipText", tooltip);
        string? hotkey = command switch { Command.Increase => "IncreaseNumberOfVisibleLines", Command.Decrease => "DecreaseNumberOfVisibleLines",
            Command.Whole => "ShowEntireFile", Command.All => "IgnoreAllWhitespace", _ => null };
        if (hotkey is not null) { DesktopHotkeys.BindToolTip(button, "FileViewer", hotkey, caption); }
        else { ToolTip.SetTip(button, caption); }
        button.Click += (_, _) => { Execute(command); Refresh(); }; _buttons.Add((button, command)); Refresh(); return button;
    }
    private Image Icon(string name)
    {
        if (!_bitmaps.TryGetValue(name, out Bitmap? bitmap))
        { using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + name)); _bitmaps[name] = bitmap = new(stream); }
        Bitmap original = bitmap;
        Image image = new() { Source = original, Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, UseLayoutRounding = true };
        if (name is "WhitespaceIgnoreEol.png" or "WhitespaceIgnore.png" or "WhitespaceIgnoreAll.png")
        {
            void UpdateSource()
            {
                if (_disposed) { return; }
                if (image.ActualThemeVariant == ThemeVariant.Dark)
                {
                    if (!_darkMonochromeBitmaps.TryGetValue(name, out Bitmap? dark))
                    {
                        Color windowText = Color.Parse(ClassicStyles.OriginalColors(dark: true)["WindowText"]);
                        _darkMonochromeBitmaps[name] = dark = DiffViewerMonochromeBitmap.CreateDark(original, windowText);
                    }
                    image.Source = dark;
                }
                else { image.Source = original; }
            }
            EventHandler handler = (_, _) => UpdateSource();
            image.ActualThemeVariantChanged += handler; _themeHandlers.Add((image, handler)); UpdateSource();
        }
        _images.Add(image); return image;
    }
    private static ResourceDictionary ButtonResources(bool dark)
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
        foreach (MenuItem item in _items.Select(entry => entry.Item)) { item.IsEnabled = false; item.IsSubMenuOpen = false; }
        foreach (Button button in _buttons.Select(entry => entry.Button)) { button.IsEnabled = false; }
        foreach (Image image in _images) { image.Source = null; }
        foreach ((Image image, EventHandler handler) in _themeHandlers) { image.ActualThemeVariantChanged -= handler; }
        foreach (Bitmap bitmap in _darkMonochromeBitmaps.Values) { bitmap.Dispose(); }
        foreach (Bitmap bitmap in _bitmaps.Values) { bitmap.Dispose(); }
        _items.Clear(); _buttons.Clear(); _images.Clear(); _themeHandlers.Clear(); _darkMonochromeBitmaps.Clear(); _bitmaps.Clear();
    }
    private enum Command { Eol, Change, All, Increase, Decrease, Whole }
}

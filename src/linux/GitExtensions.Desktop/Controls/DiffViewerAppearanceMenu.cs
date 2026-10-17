using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Desktop.Controls;

/// <summary>Attachable original diff appearance menu. The host supplies repository/view capabilities.</summary>
public sealed class DiffViewerAppearanceMenu : IDisposable
{
    private readonly DiffViewerPreferencesStore _store;
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly List<MenuSet> _menus = [];
    private readonly List<MenuFlyout> _flyouts = [];
    private readonly HashSet<Control> _scopes = [];
    private readonly Dictionary<string, Bitmap> _bitmaps = new(StringComparer.Ordinal);
    private readonly List<Image> _images = [];
    private bool _supportsAppearance, _difftasticAvailable, _disposed;

    public DiffViewerAppearanceMenu(DiffViewerPreferencesStore? store = null)
    { _store = store ?? new(); Preferences = _store.BeginViewerSession(); _store.Changed += StoreChanged; }

    public DiffViewerPreferences Preferences { get; private set; }
    public event EventHandler? Changed;

    public void SetContext(bool supportsAppearance, bool difftasticAvailable)
    {
        _supportsAppearance = supportsAppearance; _difftasticAvailable = difftasticAvailable;
        UpdateMenus();
    }

    public MenuItem CreateMenuItem()
        => CreateAppearanceItem(out _);

    /// <summary>Coloring options remain available when the appearance submenu is hidden for combined/range/text views.</summary>
    public IReadOnlyList<object> CreateContextMenuItems()
    {
        MenuItem parent = CreateAppearanceItem(out MenuSet set);
        return [parent, new Separator(), set.Coloring, set.Reverse];
    }

    private MenuItem CreateAppearanceItem(out MenuSet set)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MenuItem parent = Item("Appearance", "FileViewer", "diffAppearanceToolStripMenuItem", "Diff appea&rance", "Diff.png");
        set = CreateItems(parent);
        parent.Items.Add(set.Patch); parent.Items.Add(set.Word); parent.Items.Add(set.Difftastic);
        parent.SubmenuOpened += (_, _) => Synchronize();
        UpdateMenus(); return parent;
    }

    public MenuFlyout CreateFlyout()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MenuSet set = CreateItems(null);
        MenuFlyout flyout = new() { ItemsSource = set.Items };
        flyout.Opening += (_, _) => Synchronize();
        _flyouts.Add(flyout); UpdateMenus(); return flyout;
    }

    public void Attach(Control scope)
    {
        ArgumentNullException.ThrowIfNull(scope); ObjectDisposedException.ThrowIf(_disposed, this);
        if (_scopes.Add(scope)) { scope.AddHandler(InputElement.KeyDownEvent, KeyDown, RoutingStrategies.Tunnel); }
    }

    public bool HandleKeyDown(KeyEventArgs args)
    {
        if (_disposed || args.Handled || !_supportsAppearance) { return false; }
        DiffViewerAppearance? appearance = DesktopHotkeys.Match("FileViewer", args)?.Name switch
        {
            "ShowGitWordColoring" => DiffViewerAppearance.GitWordDiff,
            "ShowDifftastic" when _difftasticAvailable => DiffViewerAppearance.Difftastic,
            _ => null
        };
        if (appearance is null) { return false; }
        SelectAppearance(appearance.Value); args.Handled = true; return true;
    }
    private void KeyDown(object? sender, KeyEventArgs args) => HandleKeyDown(args);

    private MenuSet CreateItems(MenuItem? parent)
    {
        MenuItem patch = Item("Patch", "FileViewer", "showPatchToolStripMenuItem", "&Patch", "Diff.png");
        MenuItem word = Item("GitWordDiff", "FileViewer", "showGitWordColoringToolStripMenuItem", "Git wor&d diff", "EditColor.png");
        MenuItem difftastic = Item("Difftastic", "FileViewer", "showDifftasticToolStripMenuItem", "Diff&tastic", "difftastic.ico");
        patch.ToggleType = word.ToggleType = difftastic.ToggleType = MenuItemToggleType.CheckBox;
        DesktopHotkeys.Bind(word, "FileViewer", "ShowGitWordColoring"); DesktopHotkeys.Bind(difftastic, "FileViewer", "ShowDifftastic");
        patch.Click += (_, _) => SelectAppearance(DiffViewerAppearance.Patch);
        word.Click += (_, _) => SelectAppearance(DiffViewerAppearance.GitWordDiff);
        difftastic.Click += (_, _) => SelectAppearance(DiffViewerAppearance.Difftastic);
        MenuItem coloring = Item("UseGitColoring", "DiffViewerSettingsPage", "chkUseGitColoring", "Git coloring");
        MenuItem reverse = Item("ReverseGitColoring", "DiffViewerSettingsPage", "chkUseGEThemeGitColoring", "Reverse background color");
        coloring.ToggleType = reverse.ToggleType = MenuItemToggleType.CheckBox;
        coloring.Click += (_, _) => Save(Preferences with { UseGitColoring = !Preferences.UseGitColoring });
        reverse.Click += (_, _) => { if (Preferences.UseGitColoring) { Save(Preferences with { ReverseGitColoring = !Preferences.ReverseGitColoring }); } };
        ToolTip.SetTip(coloring, _translations.Translate("DiffViewerSettingsPage", "chkUseGitColoring", "ToolTipText", "Use Git coloring engine to show moved code etc.\n"));
        ToolTip.SetTip(reverse, _translations.Translate("DiffViewerSettingsPage", "chkUseGEThemeGitColoring", "ToolTipText", "Color the background at changes (invert colors)."));
        MenuSet set = new(parent, patch, word, difftastic, coloring, reverse, [patch, word, difftastic, new Separator(), coloring, reverse]);
        _menus.Add(set); return set;
    }

    private MenuItem Item(string name, string category, string originalName, string source, string? icon = null)
    {
        MenuItem item = new() { Name = "DiffViewer" + name, Header = new AccessText { Text = _translations.TranslateAccessKeys(category, originalName, "Text", source) } };
        if (icon is not null)
        {
            if (!_bitmaps.TryGetValue(icon, out Bitmap? bitmap))
            {
                using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + icon));
                _bitmaps[icon] = bitmap = new(stream);
            }
            Image image = new() { Source = bitmap, Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, UseLayoutRounding = true };
            _images.Add(image); item.Icon = image;
        }
        return item;
    }

    private void SelectAppearance(DiffViewerAppearance appearance)
    {
        if (!_supportsAppearance || appearance == DiffViewerAppearance.Difftastic && !_difftasticAvailable) { UpdateMenus(); return; }
        Save(Preferences with { Appearance = appearance != DiffViewerAppearance.Patch && Preferences.Appearance == appearance ? DiffViewerAppearance.Patch : appearance });
    }
    private void Save(DiffViewerPreferences preferences) { if (!_store.Save(preferences)) { Synchronize(); } }
    private void Synchronize() { Preferences = _store.Load(); UpdateMenus(); }
    private void StoreChanged(object? sender, EventArgs args)
    {
        if (_disposed) { return; }
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => StoreChanged(sender, args)); return; }
        Synchronize(); Changed?.Invoke(this, EventArgs.Empty);
    }
    private void UpdateMenus()
    {
        foreach (MenuSet set in _menus)
        {
            if (set.Parent is not null) { set.Parent.IsVisible = _supportsAppearance; }
            set.Patch.IsEnabled = set.Word.IsEnabled = _supportsAppearance;
            set.Difftastic.IsEnabled = _supportsAppearance && _difftasticAvailable;
            set.Patch.IsChecked = Preferences.Appearance == DiffViewerAppearance.Patch;
            set.Word.IsChecked = Preferences.Appearance == DiffViewerAppearance.GitWordDiff;
            set.Difftastic.IsChecked = Preferences.Appearance == DiffViewerAppearance.Difftastic;
            set.Coloring.IsChecked = Preferences.UseGitColoring;
            set.Reverse.IsChecked = Preferences.ReverseGitColoring; set.Reverse.IsEnabled = Preferences.UseGitColoring;
        }
    }
    public void Dispose()
    {
        if (_disposed) { return; } _disposed = true; _store.Changed -= StoreChanged;
        foreach (Control scope in _scopes) { scope.RemoveHandler(InputElement.KeyDownEvent, KeyDown); } _scopes.Clear();
        foreach (MenuFlyout flyout in _flyouts) { flyout.Hide(); }
        foreach (MenuSet set in _menus) { if (set.Parent is not null) { set.Parent.IsSubMenuOpen = false; } }
        foreach (Image image in _images) { image.Source = null; }
        foreach (Bitmap bitmap in _bitmaps.Values) { bitmap.Dispose(); }
        _menus.Clear(); _flyouts.Clear(); _images.Clear(); _bitmaps.Clear();
    }
    private sealed record MenuSet(MenuItem? Parent, MenuItem Patch, MenuItem Word, MenuItem Difftastic, MenuItem Coloring, MenuItem Reverse, object[] Items);
}

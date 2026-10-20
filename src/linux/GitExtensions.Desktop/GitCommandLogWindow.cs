using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Styling;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class GitCommandLogWindow : Window
{
    private static GitCommandLogWindow? _instance;
    private readonly TextBox _text = new() { Name = "GitCommandLogText", IsReadOnly = true, AcceptsReturn = true,
        FontFamily = new FontFamily("DejaVu Sans Mono"), FontSize = 12, FontWeight = FontWeight.Normal, FontStyle = FontStyle.Normal,
        TextWrapping = TextWrapping.Wrap, BorderThickness = default, Margin = default };
    private readonly ListBox _items = new() { Name = "LogItems", Focusable = true, FontFamily = new FontFamily("DejaVu Sans Mono"),
        FontSize = 12, FontWeight = FontWeight.Normal, FontStyle = FontStyle.Normal, BorderThickness = default, SelectionMode = SelectionMode.Single };
    private readonly TextBox _cacheText = new() { Name = "commandCacheOutput", IsReadOnly = true, AcceptsReturn = true,
        FontFamily = new FontFamily("DejaVu Sans Mono"), FontSize = 12, FontWeight = FontWeight.Normal, FontStyle = FontStyle.Normal,
        TextWrapping = TextWrapping.Wrap, BorderThickness = default, Margin = default };
    private readonly ListBox _cacheItems = new() { Name = "CommandCacheItems", Focusable = true, FontFamily = new FontFamily("DejaVu Sans Mono"),
        FontSize = 12, FontWeight = FontWeight.Normal, FontStyle = FontStyle.Normal, BorderThickness = default, SelectionMode = SelectionMode.Single };
    private readonly TabControl _tabs = new() { Name = "CommandLogTabs" };
    private readonly ObservableCollection<CacheRow> _cacheRows = [];
    private readonly Dictionary<long, CacheRow> _cacheById = [];
    private readonly List<MenuItem> _clearCacheItems = [];
    private bool _refreshingCache;
    private readonly ObservableCollection<LogRow> _rows = [];
    private readonly Dictionary<long, LogRow> _byId = [];
    private readonly List<MenuItem> _saveItems = [], _copyItems = [], _clearItems = [];
    private readonly List<ContextMenu> _menus = [];
    private readonly TranslationCatalog _translations;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CommandLogCapturePreferences _capturePreferences = new();
    private readonly CheckBox _capture = new() { Name = "chkCaptureCallStacks", IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _captureNotice = new() { Name = "CommandLogCaptureNotice", IsVisible = false, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2) };
    private CommandLogCaptureState? _captureState;
    private bool _settingCapture;
    private bool _closed, _subscribed, _refreshing, _busy, _captureLoading;
    private int _queued, _dirty = 3;
    internal Func<FilePickerSaveOptions, Task<IStorageFile?>>? SaveFilePicker { get; set; }
    internal Task Work { get; private set; } = Task.CompletedTask;
    internal bool IsBusy => _busy || _captureLoading;

    internal static GitCommandLogWindow ShowOrActivate(Window owner, TranslationCatalog translations)
    {
        if (_instance is { IsVisible: true } current)
        {
            if (current.WindowState == WindowState.Minimized) { current.WindowState = WindowState.Normal; }
            current.Activate(); return current;
        }
        GitCommandLogWindow window = new(translations) { RequestedThemeVariant = owner.ActualThemeVariant };
        _instance = window; window.Show(owner); return window;
    }

    public GitCommandLogWindow(TranslationCatalog translations)
    {
        _translations = translations;
        Name = "GitCommandLogWindow"; Title = Original("$this", "Git Command Log");
        Width = 659; Height = 470; MinWidth = 400; MinHeight = 240; ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _items.ItemsSource = _rows;
        _cacheItems.ItemsSource = _cacheRows;
        foreach (ListBox list in new[] { _items, _cacheItems })
        {
            list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters =
            { new Setter(TemplatedControl.PaddingProperty, new Thickness(2, 0)), new Setter(Layoutable.MinHeightProperty, 16d), new Setter(Layoutable.HeightProperty, 16d) } });
        }
        foreach (TextBox text in new[] { _text, _cacheText })
        {
            // Keep TextBox's native Wrap => Disabled / NoWrap => Auto horizontal binding.
            // Classic application fonts on template controls must not replace this fixed face.
            text.Styles.Add(new Style(selector => selector.OfType<TextBox>().Template().OfType<TextPresenter>().Name("PART_TextPresenter"))
            { Setters =
            {
                new Setter(TextElement.FontFamilyProperty, text.FontFamily), new Setter(TextElement.FontSizeProperty, text.FontSize),
                new Setter(TextElement.FontWeightProperty, text.FontWeight), new Setter(TextElement.FontStyleProperty, text.FontStyle)
            } });
            ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        }
        _items.ItemTemplate = new FuncDataTemplate<LogRow>((_, _) => new TextBlock
        { FontFamily = _items.FontFamily, FontSize = 12, FontWeight = FontWeight.Normal, FontStyle = FontStyle.Normal,
            [!TextBlock.TextProperty] = new Binding(nameof(LogRow.Text)) });
        _items.SelectionChanged += (_, _) => { if (!_refreshing) { ShowDetail(); } };
        _items.ContextMenu = Menu();
        Grid content = new() { Name = "CommandLogSplit", RowDefinitions = new("264*,4,150*") };
        content.RowDefinitions[0].MinHeight = 60; content.RowDefinitions[2].MinHeight = 50;
        GridSplitter splitter = new() { Name = "CommandLogSplitter", ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(splitter, 1); Grid.SetRow(_text, 2);
        content.Children.Add(_items); content.Children.Add(splitter); content.Children.Add(_text);
        TabItem page = new() { Name = "tabPageCommandLog", Header = Original("tabPageCommandLog", "Command log"), Content = content };
        page.ContextMenu = Menu();
        _cacheItems.ItemTemplate = new FuncDataTemplate<CacheRow>((_, _) => new TextBlock
        { FontFamily = _cacheItems.FontFamily, FontSize = 12, FontWeight = FontWeight.Normal, FontStyle = FontStyle.Normal,
            [!TextBlock.TextProperty] = new Binding(nameof(CacheRow.Text)) });
        _cacheItems.SelectionChanged += (_, _) => { if (!_refreshingCache) { ShowCacheDetail(inspect: true); } };
        _cacheItems.ContextMenu = CacheMenu();
        Grid cacheContent = new() { Name = "CommandCacheSplit", RowDefinitions = new("183*,4,231*") };
        cacheContent.RowDefinitions[0].MinHeight = 60; cacheContent.RowDefinitions[2].MinHeight = 50;
        GridSplitter cacheSplitter = new() { Name = "CommandCacheSplitter", ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(cacheSplitter, 1); Grid.SetRow(_cacheText, 2);
        cacheContent.Children.Add(_cacheItems); cacheContent.Children.Add(cacheSplitter); cacheContent.Children.Add(_cacheText);
        TabItem cachePage = new() { Name = "tabPageCommandCache", Header = Original("tabPageCommandCache", "Command cache"), Content = cacheContent,
            ContextMenu = CacheMenu() };
        _tabs.ItemsSource = new[] { page, cachePage };
        _tabs.SelectionChanged += (_, args) => { if (ReferenceEquals(args.Source, _tabs)) { RefreshActive(force: true); } };
        CheckBox top = new() { Name = "chkAlwaysOnTop", Content = Original("chkAlwaysOnTop", "Always on top"), VerticalAlignment = VerticalAlignment.Center };
        CheckBox wrap = new() { Name = "chkWordWrap", Content = Original("chkWordWrap", "Word wrap"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        top.IsCheckedChanged += (_, _) => Topmost = top.IsChecked == true;
        wrap.IsCheckedChanged += (_, _) =>
        { _text.TextWrapping = _cacheText.TextWrapping = wrap.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap; };
        _capture.Content = Original("chkCaptureCallStacks", "Capture call stacks");
        _capture.IsCheckedChanged += (_, _) =>
        { if (!_settingCapture && _capture.IsEnabled) { bool desired = _capture.IsChecked == true; Start(() => SaveCaptureAsync(desired)); } };
        WrapPanel footer = new() { Name = "CommandLogOptions", Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 2,
            Margin = new Thickness(4, 3), Children = { top, wrap, _capture } };
        StackPanel options = new() { Children = { _captureNotice, footer } };
        DockPanel.SetDock(options, Dock.Bottom); Content = new DockPanel { Children = { options, _tabs } };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        KeyDown += (_, args) => { if (!args.Handled && args.Key == Key.Escape && OwnedWindows.All(window => !window.IsVisible) && _menus.All(menu => !menu.IsOpen)) { args.Handled = true; Close(); } };
        Opened += (_, _) =>
        { GitCommandLog.Changed += Changed; GitCommandCache.Shared.Changed += CacheChanged; _subscribed = true; _captureLoading = true; Refresh(); _items.Focus(); Work = LoadCaptureAsync(); };
        Closed += (_, _) =>
        {
            Volatile.Write(ref _closed, true);
            if (_subscribed) { GitCommandLog.Changed -= Changed; GitCommandCache.Shared.Changed -= CacheChanged; _subscribed = false; }
            _lifetime.Cancel(); foreach (ContextMenu menu in _menus) { menu.Close(); }
            if (ReferenceEquals(_instance, this)) { _instance = null; }
            _ = DisposeWorkAsync();
        };
    }

    private string Original(string name, string source) => _translations.Translate("FormGitCommandLog", name, "Text", source);
    private ContextMenu Menu()
    {
        MenuItem save = OriginalMenuItems.Create(_translations, "FormGitCommandLog", "mnuSaveToFile", "&Save to file", "SaveAs.png", new(Key.S, KeyModifiers.Control));
        // The recorder deliberately has no arguments: never label this a full command line.
        MenuItem copy = OriginalMenuItems.Create(_translations, "", "mnuCopyCommandLine", "&Copy", "CopyToClipboard.png", new(Key.C, KeyModifiers.Control));
        copy.Header = new AccessText { Text = _translations.Translate("Copy") };
        MenuItem clear = OriginalMenuItems.Create(_translations, "FormGitCommandLog", "mnuClear", "C&lear", "ClearLog.png", new(Key.L, KeyModifiers.Control));
        save.Click += (_, args) => { if (args.Source == save) { args.Handled = true; Start(SaveAsync); } };
        copy.Click += (_, args) => { if (args.Source == copy) { args.Handled = true; Start(CopyCommandAsync); } };
        clear.Click += (_, args) => { if (args.Source == clear) { args.Handled = true; GitCommandLog.Clear(); Refresh(); } };
        _saveItems.Add(save); _copyItems.Add(copy); _clearItems.Add(clear);
        ContextMenu menu = new() { ItemsSource = new object[] { save, copy, clear } };
        menu.Opening += (_, _) => UpdateCommands(); _menus.Add(menu); return menu;
    }

    private ContextMenu CacheMenu()
    {
        MenuItem clear = OriginalMenuItems.Create(_translations, "FormGitCommandLog", "tsmiClearCache", "C&lear", "ClearLog.png", new(Key.L, KeyModifiers.Control));
        clear.Click += (_, args) => { if (args.Source == clear) { args.Handled = true; ClearCache(); } };
        _clearCacheItems.Add(clear);
        ContextMenu menu = new() { ItemsSource = new object[] { clear } };
        menu.Opening += (_, _) => UpdateCommands(); _menus.Add(menu); return menu;
    }
    private bool CacheSelected => _tabs.SelectedIndex == 1;
    private void ClearCache() { GitCommandCache.Shared.Clear(); RefreshCache(); }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Handled || args.KeyModifiers != KeyModifiers.Control || _closed || IsBusy) { return; }
        if (CacheSelected)
        {
            if (args.Key == Key.L) { args.Handled = true; ClearCache(); }
            // The cache menu has no Save/Copy command. TextBox keeps native selection copy.
            return;
        }
        switch (args.Key)
        {
            case Key.C when !(_text.IsKeyboardFocusWithin && _text.SelectionStart != _text.SelectionEnd):
                args.Handled = true; Start(CopyCommandAsync); break;
            case Key.S:
                args.Handled = true; Start(SaveAsync); break;
            case Key.L:
                args.Handled = true; GitCommandLog.Clear(); Refresh(); break;
        }
    }

    private void Changed() => QueueRefresh(1);
    private void CacheChanged() => QueueRefresh(2);
    private void QueueRefresh(int source)
    {
        if (Volatile.Read(ref _closed)) { return; }
        Interlocked.Or(ref _dirty, source);
        if (Interlocked.Exchange(ref _queued, 1) != 0) { return; }
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _queued, 0); if (!_closed) { RefreshActive(); } }, DispatcherPriority.Background);
    }
    private void RefreshActive(bool force = false)
    {
        if (_closed) { return; }
        int source = CacheSelected ? 2 : 1;
        if (force || (Volatile.Read(ref _dirty) & source) != 0)
        { if (CacheSelected) { RefreshCache(); } else { Refresh(); } }
        UpdateCommands();
    }

    internal void Refresh()
    {
        if (_closed) { return; }
        Interlocked.And(ref _dirty, ~1);
        bool keepListFocus = _items.IsKeyboardFocusWithin && CanKeepListFocus();
        IReadOnlyList<GitCommandLogEntry> entries = GitCommandLog.Snapshot();
        LogRow? selected = _items.SelectedItem as LogRow;
        bool follow = selected is null || ReferenceEquals(selected, _rows.LastOrDefault());
        _refreshing = true;
        try
        {
            HashSet<long> present = entries.Select(entry => entry.Id).ToHashSet();
            for (int index = _rows.Count - 1; index >= 0; index--)
            { if (!present.Contains(_rows[index].Entry.Id)) { _byId.Remove(_rows[index].Entry.Id); _rows.RemoveAt(index); } }
            for (int index = 0; index < entries.Count; index++)
            {
                GitCommandLogEntry entry = entries[index];
                if (!_byId.TryGetValue(entry.Id, out LogRow? row))
                { row = new(entry, _translations); _byId.Add(entry.Id, row); _rows.Insert(index, row); }
                else { row.Update(entry); }
            }
            _items.SelectedItem = follow ? _rows.LastOrDefault() : selected is not null && _byId.TryGetValue(selected.Entry.Id, out LogRow? kept) ? kept : _rows.FirstOrDefault();
        }
        finally { _refreshing = false; }
        ShowDetail(); UpdateCommands();
        if (follow && _items.SelectedItem is { } last) { _items.ScrollIntoView(last); }
        KeepListFocus(_items, keepListFocus);
    }

    private void RefreshCache()
    {
        if (_closed) { return; }
        Interlocked.And(ref _dirty, ~2);
        bool keepListFocus = _cacheItems.IsKeyboardFocusWithin && CanKeepListFocus();
        IReadOnlyList<GitCommandCacheEntry> entries = GitCommandCache.Shared.Snapshot();
        CacheRow? selected = _cacheItems.SelectedItem as CacheRow;
        int previousIndex = _cacheItems.SelectedIndex;
        bool follow = selected is null || ReferenceEquals(selected, _cacheRows.LastOrDefault());
        _refreshingCache = true;
        try
        {
            HashSet<long> present = entries.Select(entry => entry.Id).ToHashSet();
            for (int index = _cacheRows.Count - 1; index >= 0; index--)
            { if (!present.Contains(_cacheRows[index].Entry.Id)) { _cacheById.Remove(_cacheRows[index].Entry.Id); _cacheRows.RemoveAt(index); } }
            for (int index = 0; index < entries.Count; index++)
            {
                GitCommandCacheEntry entry = entries[index];
                if (!_cacheById.TryGetValue(entry.Id, out CacheRow? row))
                { row = new(entry); _cacheById.Add(entry.Id, row); _cacheRows.Insert(index, row); }
                else { row.Update(entry); }
            }
            _cacheItems.SelectedItem = follow ? _cacheRows.LastOrDefault() : selected is not null && _cacheById.TryGetValue(selected.Entry.Id, out CacheRow? kept) ? kept
                : _cacheRows.Count == 0 ? null : _cacheRows[Math.Clamp(previousIndex, 0, _cacheRows.Count - 1)];
        }
        finally { _refreshingCache = false; }
        // Only a newly selected row is inspected. Background changes must not keep
        // an old selection artificially recent in the functional cache.
        ShowCacheDetail(inspect: !ReferenceEquals(selected, _cacheItems.SelectedItem)); UpdateCommands();
        if (follow && _cacheItems.SelectedItem is { } last) { _cacheItems.ScrollIntoView(last); }
        KeepListFocus(_cacheItems, keepListFocus);
    }
    private void ShowCacheDetail(bool inspect)
    {
        CacheRow? row = _cacheItems.SelectedItem as CacheRow;
        GitCommandCacheEntry? entry = row?.Entry;
        if (inspect && entry is not null)
        {
            entry = GitCommandCache.Shared.TryInspect(entry.Id, out GitCommandCacheEntry? current) ? current : null;
            if (entry is not null) { row!.Update(entry); }
        }
        string detail = entry is null ? "" : GitCommandCachePresentation.Detail(entry);
        if (_cacheText.Text != detail) { _cacheText.Text = detail; }
    }
    private void KeepListFocus(ListBox list, bool keepListFocus)
    {
        if (keepListFocus)
        {
            // Membership/scroll can recycle the focused row. Materialize it now,
            // then keep only a genuinely lost list focus on the stable input host.
            list.UpdateLayout();
            if (!list.IsKeyboardFocusWithin && CanKeepListFocus())
            {
                Control? current = FocusManager?.GetFocusedElement() as Control;
                if (current is null || ReferenceEquals(current, this) || !current.IsEffectivelyVisible || TopLevel.GetTopLevel(current) is null)
                { list.Focus(); }
            }
        }
    }

    private bool CanKeepListFocus() => !_closed && IsVisible && IsActive && IsEffectivelyEnabled && WindowState != WindowState.Minimized
        && !_busy && _menus.All(menu => !menu.IsOpen) && OwnedWindows.All(window => !window.IsVisible);

    private void ShowDetail()
    {
        string detail = _items.SelectedItem is LogRow row ? GitCommandLogPresentation.Detail(row.Entry, _translations) : "";
        // Unrelated notifications preserve the detail selection and caret as well as the list's focus.
        if (_text.Text != detail) { _text.Text = detail; }
    }
    private void UpdateCommands()
    {
        _capture.IsEnabled = !IsBusy && _captureState is { Snapshot: not null, Error: null };
        foreach (MenuItem item in _saveItems) { item.IsEnabled = !IsBusy && !CacheSelected && _rows.Count != 0; }
        foreach (MenuItem item in _copyItems) { item.IsEnabled = !IsBusy && !CacheSelected && _items.SelectedItem is LogRow && Clipboard is not null; }
        foreach (MenuItem item in _clearItems) { item.IsEnabled = !IsBusy && !CacheSelected && _rows.Count != 0; }
        foreach (MenuItem item in _clearCacheItems) { item.IsEnabled = !IsBusy && CacheSelected && _cacheRows.Count != 0; }
    }
    private async Task LoadCaptureAsync()
    {
        try
        {
            CommandLogCaptureState value = await _capturePreferences.LoadAsync(_lifetime.Token);
            if (!_closed) { SetCaptureState(value); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _captureLoading = false; if (!_closed) { UpdateCommands(); } }
    }
    private async Task SaveCaptureAsync(bool desired)
    {
        CommandLogCaptureState before = _captureState ?? throw new InvalidOperationException("Call stack settings have not loaded.");
        try
        {
            CommandLogCaptureState accepted = await _capturePreferences.SetAsync(before, desired, _lifetime.Token);
            if (!_closed) { SetCaptureState(accepted); }
        }
        catch (Exception error) when (CommandLogCapturePreferences.IsPreferenceError(error))
        { if (!_closed) { SetCaptureState(new(before.Enabled, null, error)); } }
    }
    private void SetCaptureState(CommandLogCaptureState value)
    {
        _captureState = value; _settingCapture = true;
        try { _capture.IsChecked = value.Enabled; }
        finally { _settingCapture = false; }
        _captureNotice.Text = value.Error is null ? "" : _translations.Translate("Unable to load or save call stack capture settings.") + " "
            + value.Error.Message + " " + _translations.Translate("Close and reopen the journal after repairing or reloading the settings.");
        _captureNotice.IsVisible = value.Error is not null;
    }
    private void Start(Func<Task> action)
    {
        if (_closed || IsBusy) { return; }
        Work = ExecuteAsync();
        async Task ExecuteAsync()
        {
            _busy = true; UpdateCommands();
            try { await action(); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!_closed && !_lifetime.IsCancellationRequested)
                { await Dialogs.ShowMessageAsync(this, _translations.Translate("Error"), error.Message, warning: false, _lifetime.Token); }
            }
            finally { _busy = false; if (!_closed) { UpdateCommands(); } }
        }
    }
    private Task CopyCommandAsync() => _items.SelectedItem is LogRow row && Clipboard is { } clipboard
        ? clipboard.SetTextAsync(row.Entry.Command) : Task.CompletedTask;

    private async Task SaveAsync()
    {
        GitCommandLogEntry[] snapshot = GitCommandLog.Snapshot().ToArray();
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (snapshot.Length == 0) { return; }
        FilePickerSaveOptions options = new() { Title = Original("mnuSaveToFile", "Save to file"), SuggestedFileName = "git-command-log.txt", DefaultExtension = "txt", ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Text files (*.txt)") { Patterns = ["*.txt"] }, new FilePickerFileType("CSV files (*.csv)") { Patterns = ["*.csv"] }, FilePickerFileTypes.All] };
        IStorageFile? chosen = SaveFilePicker is { } picker ? await picker(options) : await StorageProvider.SaveFilePickerAsync(options);
        if (chosen is null) { return; }
        using (chosen)
        {
            _lifetime.Token.ThrowIfCancellationRequested(); if (_closed || !IsVisible) { return; }
            byte[] bytes = new UTF8Encoding(false).GetBytes(GitCommandLogPresentation.Export(snapshot, chosen.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase), culture));
            await using Stream output = await chosen.OpenWriteAsync();
            _lifetime.Token.ThrowIfCancellationRequested(); if (_closed || !IsVisible) { return; }
            output.SetLength(0); await output.WriteAsync(bytes, _lifetime.Token); await output.FlushAsync(_lifetime.Token);
        }
    }
    private async Task DisposeWorkAsync()
    {
        try { await Work; } catch (OperationCanceledException) { }
        finally { _lifetime.Dispose(); }
    }

    private sealed class CacheRow(GitCommandCacheEntry entry) : INotifyPropertyChanged
    {
        internal GitCommandCacheEntry Entry { get; private set; } = entry;
        public string Text => Entry.DisplayString;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Update(GitCommandCacheEntry value)
        {
            if (ReferenceEquals(value, Entry)) { return; }
            Entry = value; PropertyChanged?.Invoke(this, new(nameof(Text)));
        }
    }

    private sealed class LogRow(GitCommandLogEntry entry, TranslationCatalog translations) : INotifyPropertyChanged
    {
        internal GitCommandLogEntry Entry { get; private set; } = entry;
        public string Text => GitCommandLogPresentation.Line(Entry, translations);
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Update(GitCommandLogEntry value)
        {
            if (ReferenceEquals(value, Entry)) { return; }
            Entry = value; PropertyChanged?.Invoke(this, new(nameof(Text)));
        }
    }
}

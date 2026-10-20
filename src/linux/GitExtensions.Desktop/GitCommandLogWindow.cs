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
    private readonly ObservableCollection<LogRow> _rows = [];
    private readonly Dictionary<long, LogRow> _byId = [];
    private readonly List<MenuItem> _saveItems = [], _copyItems = [], _clearItems = [];
    private readonly List<ContextMenu> _menus = [];
    private readonly TranslationCatalog _translations;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closed, _subscribed, _refreshing, _busy;
    private int _queued;
    internal Func<FilePickerSaveOptions, Task<IStorageFile?>>? SaveFilePicker { get; set; }
    internal Task Work { get; private set; } = Task.CompletedTask;
    internal bool IsBusy => _busy;

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
        _items.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters =
        { new Setter(TemplatedControl.PaddingProperty, new Thickness(2, 0)), new Setter(Layoutable.MinHeightProperty, 16d), new Setter(Layoutable.HeightProperty, 16d) } });
        // Keep TextBox's native Wrap => Disabled / NoWrap => Auto horizontal binding.
        // Classic application fonts on template controls must not replace this fixed face.
        _text.Styles.Add(new Style(selector => selector.OfType<TextBox>().Template().OfType<TextPresenter>().Name("PART_TextPresenter"))
        { Setters =
        {
            new Setter(TextElement.FontFamilyProperty, _text.FontFamily), new Setter(TextElement.FontSizeProperty, _text.FontSize),
            new Setter(TextElement.FontWeightProperty, _text.FontWeight), new Setter(TextElement.FontStyleProperty, _text.FontStyle)
        } });
        ScrollViewer.SetVerticalScrollBarVisibility(_text, ScrollBarVisibility.Auto);
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
        TabControl tabs = new() { Name = "CommandLogTabs", ItemsSource = new[] { page } };
        CheckBox top = new() { Name = "chkAlwaysOnTop", Content = Original("chkAlwaysOnTop", "Always on top"), VerticalAlignment = VerticalAlignment.Center };
        CheckBox wrap = new() { Name = "chkWordWrap", Content = Original("chkWordWrap", "Word wrap"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        top.IsCheckedChanged += (_, _) => Topmost = top.IsChecked == true;
        wrap.IsCheckedChanged += (_, _) => _text.TextWrapping = wrap.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        StackPanel footer = new() { Name = "CommandLogOptions", Orientation = Orientation.Horizontal, Spacing = 8,
            Margin = new Thickness(4, 3), Children = { top, wrap } };
        DockPanel.SetDock(footer, Dock.Bottom); Content = new DockPanel { Children = { footer, tabs } };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        KeyDown += (_, args) => { if (!args.Handled && args.Key == Key.Escape && OwnedWindows.All(window => !window.IsVisible) && _menus.All(menu => !menu.IsOpen)) { args.Handled = true; Close(); } };
        Opened += (_, _) => { GitCommandLog.Changed += Changed; _subscribed = true; Refresh(); _items.Focus(); };
        Closed += (_, _) =>
        {
            Volatile.Write(ref _closed, true);
            if (_subscribed) { GitCommandLog.Changed -= Changed; _subscribed = false; }
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

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Handled || args.KeyModifiers != KeyModifiers.Control || _closed || _busy) { return; }
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

    private void Changed()
    {
        if (Volatile.Read(ref _closed) || Interlocked.Exchange(ref _queued, 1) != 0) { return; }
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _queued, 0); if (!_closed) { Refresh(); } }, DispatcherPriority.Background);
    }

    internal void Refresh()
    {
        if (_closed) { return; }
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
        if (keepListFocus)
        {
            // Membership/scroll can recycle the focused row. Materialize it now,
            // then keep only a genuinely lost list focus on the stable input host.
            _items.UpdateLayout();
            if (!_items.IsKeyboardFocusWithin && CanKeepListFocus())
            {
                Control? current = FocusManager?.GetFocusedElement() as Control;
                if (current is null || ReferenceEquals(current, this) || !current.IsEffectivelyVisible || TopLevel.GetTopLevel(current) is null)
                { _items.Focus(); }
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
        foreach (MenuItem item in _saveItems) { item.IsEnabled = !_busy && _rows.Count != 0; }
        foreach (MenuItem item in _copyItems) { item.IsEnabled = !_busy && _items.SelectedItem is LogRow && Clipboard is not null; }
        foreach (MenuItem item in _clearItems) { item.IsEnabled = !_busy && _rows.Count != 0; }
    }
    private void Start(Func<Task> action)
    {
        if (_closed || _busy) { return; }
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

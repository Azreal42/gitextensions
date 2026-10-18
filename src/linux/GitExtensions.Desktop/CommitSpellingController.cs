using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Spelling;

namespace GitExtensions.Linux.Desktop;

/// <summary>Owns one editor's mutable spell engines and every raw operation until close.</summary>
internal sealed class CommitSpellingController
{
    private static event Action<string, DesktopPreferences>? SavedDesktop;
    private readonly GitRepository _repository;
    private readonly SpellingEditorView _view;
    private readonly TextBox _editor;
    private readonly PreferencesStore _preferences;
    private readonly DictionaryCatalog _catalog;
    private readonly Func<string, CancellationToken, Task<GitDictionarySettingsSnapshot>> _readSettingsAsync;
    private readonly string _userFile;
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _work = [];
    private readonly List<SpellCheckService> _services = [];
    private readonly HashSet<SpellingSpan> _ignored = [];
    private readonly HashSet<string> _completionWords = new(StringComparer.Ordinal);
    private readonly ContextMenu _menu = new() { Name = "CommitSpellingMenu" };
    private CancellationTokenSource? _debounce;
    private DesktopPreferences? _accepted;
    private GitDictionarySettingsSnapshot? _settings;
    private GitDictionarySettingsSnapshot? _menuSettings;
    private SpellCheckService? _service;
    private SpellCheckResult? _result;
    private Task? _close;
    private long _revision;
    private long _settingsRevision;
    private long _menuRevision;
    private int? _contextPosition;
    private bool _started;
    private bool _closing;
    private bool _changingDictionary;
    private string? _error;
    internal TextBlock Notice { get; } = new() { Name = "CommitSpellingStatus", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 1), IsVisible = false };
    internal Task Completion => _close ?? Task.WhenAll(_work.ToArray());
    internal event Action<DesktopPreferences>? PreferencesAccepted;
    internal IReadOnlyList<SpellingSpan> Misspellings => _result?.Misspellings.Where(item => !_ignored.Contains(item)).ToArray() ?? [];

    internal CommitSpellingController(GitRepository repository, SpellingEditorView view, PreferencesStore preferences,
        DictionaryCatalog? catalog = null,
        Func<string, CancellationToken, Task<GitDictionarySettingsSnapshot>>? readSettingsAsync = null)
    {
        _repository = repository; _view = view; _editor = view.Editor; _preferences = preferences;
        _catalog = catalog ?? DictionaryCatalog.FromApplicationDirectory(AppContext.BaseDirectory);
        _readSettingsAsync = readSettingsAsync ?? ((name, token) => repository.ReadEffectiveDictionaryAsync(name, token));
        _userFile = Path.Combine(Path.GetDirectoryName(preferences.FilePath)!, "NetSpell", "user.dic");
        _editor.ContextMenu = _menu;
        _editor.PropertyChanged += EditorChanged;
        _editor.AddHandler(InputElement.PointerPressedEvent, EditorPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _editor.AddHandler(InputElement.KeyDownEvent, EditorKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _view.CompositionChanged += CompositionChanged;
        _menu.Opening += MenuOpening;
        _menu.Closed += MenuClosed;
    }

    // Main publishes the value really written by General.Save, including partial saves.
    // This is only a profile-scoped notification, never another settings authority.
    internal static void PublishSavedDesktop(string preferencesFilePath, DesktopPreferences acceptedDesktop)
        => SavedDesktop?.Invoke(preferencesFilePath, acceptedDesktop);

    internal void Start()
    {
        if (_started || _closing) { return; }
        _started = true;
        SavedDesktop += DesktopPublished;
        DesktopPreferences accepted;
        try { accepted = _preferences.Load(); _accepted = accepted; }
        catch (Exception error) { Report(error); return; }
        PreferencesAccepted?.Invoke(accepted);
        Track(() => ReloadSettingsAsync(accepted));
    }

    private void DesktopPublished(string file, DesktopPreferences value)
    {
        if (_closing || !string.Equals(file, _preferences.FilePath, StringComparison.Ordinal)) { return; }
        InvalidateMenu();
        bool dictionaryChanged = _accepted?.Dictionary != value.Dictionary;
        _accepted = value;
        PreferencesAccepted?.Invoke(value);
        if (!_started) { return; }
        if (dictionaryChanged || _settings is null) { Track(() => ReloadSettingsAsync(value)); }
        else { Schedule(); }
    }

    private async Task ReloadSettingsAsync(DesktopPreferences accepted)
    {
        long generation = ++_settingsRevision;
        InvalidateMenu();
        _debounce?.Cancel();
        _ = _service?.BeginClose(); _service = null;
        _settings = null; _result = null; _view.SetRanges(_editor.Text ?? "", []);
        GitDictionarySettingsSnapshot snapshot;
        try { snapshot = await _readSettingsAsync(accepted.Dictionary, _lifetime.Token); }
        catch (Exception) when (_closing || generation != _settingsRevision) { return; }
        if (_closing || generation != _settingsRevision) { return; }
        _settings = snapshot;
        SetService(snapshot.EffectiveDictionary);
        Schedule();
    }

    private void SetService(string dictionary, bool force = false)
    {
        if (!force && _service?.DictionaryName == dictionary) { UpdateNotice(); return; }
        _ = _service?.BeginClose(); _service = null;
        SpellCheckService created = new(_catalog, dictionary, _userFile);
        _service = created; _services.Add(created);
        _result = null; _error = null;
        _view.SetRanges(_editor.Text ?? "", []);
        UpdateNotice();
        Track(async () =>
        {
            try
            {
                // Admission happens synchronously, before Schedule can queue another Check.
                await created.AddCompletionWordsAsync(_completionWords.ToArray(), _lifetime.Token);
                if (!_closing && ReferenceEquals(created, _service)) { Schedule(); }
            }
            catch (Exception error)
            { if (!_closing && ReferenceEquals(created, _service)) { Report(error); } }
        });
    }


    internal void AddCompletionWords(IReadOnlyList<string> words)
    {
        if (_closing) { return; }
        bool changed = false;
        foreach (string word in words) { changed |= _completionWords.Add(word); }
        if (!changed || _service is not SpellCheckService service) { return; }
        _revision++; _result = null; _debounce?.Cancel();
        _view.SetRanges(_editor.Text ?? "", []);
        string[] snapshot = _completionWords.ToArray();
        Track(async () =>
        {
            await service.AddCompletionWordsAsync(snapshot, _lifetime.Token);
            if (!_closing && ReferenceEquals(service, _service)) { Schedule(); }
        }, () => ReferenceEquals(service, _service));
    }

    private void EditorChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property != TextBox.TextProperty || _closing) { return; }
        _revision++; _menuRevision++; _ignored.Clear(); _result = null;
        _view.SetRanges(_editor.Text ?? "", []);
        Schedule();
    }
    private void CompositionChanged(object? sender, EventArgs args)
    {
        if (_closing) { return; }
        _revision++; _result = null;
        _view.SetRanges(_editor.Text ?? "", []);
        Schedule();
    }

    private void Schedule()
    {
        _debounce?.Cancel();
        if (!_started || _closing) { return; }
        string text = _editor.Text ?? "";
        _view.SetIllFormedRanges(text, _accepted?.MarkIllFormedLinesInCommitMsg == true);
        if (_view.IsComposing || _service is not { IsEnabled: true } service || text.Length >= 5000)
        { return; }
        long revision = _revision;
        CancellationTokenSource delay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _debounce = delay;
        Track(async () =>
        {
            try
            {
                await Task.Delay(250, delay.Token);
                SpellCheckResult result = await service.CheckAsync(text, delay.Token);
                if (!Current(service, revision, text) || delay.IsCancellationRequested) { return; }
                _result = result; _error = null; PaintResult(); UpdateNotice();
            }
            catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
            catch (Exception) when (!Current(service, revision, text)) { }
            finally
            {
                if (ReferenceEquals(_debounce, delay)) { _debounce = null; }
                delay.Dispose();
            }
        });
    }

    private bool Current(SpellCheckService service, long revision, string text)
        => !_closing && ReferenceEquals(service, _service) && revision == _revision
            && text == (_editor.Text ?? "") && !_view.IsComposing;
    private void PaintResult()
        => _view.SetRanges(_editor.Text ?? "", Misspellings.Select(item => new SpellingRange(item.Start, item.Length)));

    private void EditorPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        // Capture the requested word before the native TextBox handles the pointer.
        // Measuring the actual TextPresenter does not move its caret or selection.
        _contextPosition = args.GetCurrentPoint(_editor).Properties.IsRightButtonPressed
            ? _view.GetTextIndexAt(args.GetPosition(_editor)) : null;
    }
    private void EditorKeyDown(object? sender, KeyEventArgs args) => _contextPosition = null;

    private void InvalidateMenu()
    {
        _menuRevision++; _menuSettings = null;
        // A published policy invalidates the visible checks too, including flag-only changes.
        // Close through the native menu path; no editor focus/selection repair is needed.
        if (_menu.IsOpen) { _menu.Close(); }
    }
    private bool CurrentMenu(long menuRevision, long settingsRevision)
        => !_closing && menuRevision == _menuRevision && settingsRevision == _settingsRevision;

    private void MenuOpening(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        if (_closing) { args.Cancel = true; return; }
        long generation = ++_menuRevision;
        _menuSettings = null;
        int position = _contextPosition ?? Math.Min(_editor.SelectionStart, _editor.SelectionEnd);
        _contextPosition = null;
        _menu.ItemsSource = BuildItems(null, []);
        Track(() => PopulateMenuAsync(generation, position), () => generation == _menuRevision);
    }
    private void MenuClosed(object? sender, EventArgs args) => _menuRevision++;

    private async Task PopulateMenuAsync(long generation, int position)
    {
        long settingsGeneration = _settingsRevision;
        if (_accepted is not null)
        {
            GitDictionarySettingsSnapshot current = await _readSettingsAsync(_accepted.Dictionary, _lifetime.Token);
            if (!CurrentMenu(generation, settingsGeneration)) { return; }
            _menuSettings = current;
            if (_settings?.EffectiveDictionary != current.EffectiveDictionary)
            {
                // This newer observed local/distributed value also supersedes older reload replies.
                settingsGeneration = ++_settingsRevision;
                _settings = current; SetService(current.EffectiveDictionary); Schedule();
            }
        }
        SpellCheckService? service = _service;
        string text = _editor.Text ?? ""; long revision = _revision;
        SpellingSpan? word = null; string[] suggestions = [];
        if (_accepted?.ProvideAutocompletion == true && service is { IsEnabled: true } && !_view.IsComposing && text.Length < 5000)
        {
            SpellCheckResult checkedText = await service.CheckAsync(text, _lifetime.Token);
            if (!Current(service, revision, text) || !CurrentMenu(generation, settingsGeneration)) { return; }
            _result = checkedText; PaintResult();
            foreach (SpellingSpan span in Misspellings)
            { if (position >= span.Start && position <= span.End) { word = span; break; } }
            if (word is SpellingSpan misspelled)
            {
                SpellSuggestionsResult result = await service.SuggestAsync(misspelled.Word, 5, _lifetime.Token);
                if (!Current(service, revision, text) || !CurrentMenu(generation, settingsGeneration)) { return; }
                suggestions = result.Suggestions.Take(5).ToArray();
            }
        }
        if (CurrentMenu(generation, settingsGeneration)) { _menu.ItemsSource = BuildItems(word, suggestions); }
    }

    internal Func<IEnumerable<object>>? AdditionalItems { get; set; }

    private object[] BuildItems(SpellingSpan? word, string[] suggestions)
    {
        List<object> items = [];
        string text = _editor.Text ?? ""; long revision = _revision; SpellCheckService? service = _service;
        if (word is SpellingSpan target && service is not null)
        {
            foreach (string suggestion in suggestions)
            {
                MenuItem item = Item("SpellSuggestion", suggestion, () =>
                { if (Current(service, revision, text)) { Replace(target, suggestion); } return Task.CompletedTask; });
                item.FontWeight = FontWeight.Bold; items.Add(item);
            }
            items.Add(Item("AddToDictionary", "Add to dictionary", async () =>
            {
                if (!Current(service, revision, text)) { return; }
                await service.AddWordAsync(target.Word, _lifetime.Token);
                if (Current(service, revision, text)) { Schedule(); }
            }));
            items.Add(Item("IgnoreWord", "Ignore word", () =>
            { if (Current(service, revision, text)) { _ignored.Add(target); PaintResult(); } return Task.CompletedTask; }));
            items.Add(Item("RemoveWord", "Remove word", () =>
            { if (Current(service, revision, text)) { Remove(target, text); } return Task.CompletedTask; }));
            items.Add(new Separator());
        }
        items.Add(Item("SpellingCut", "Cut", () => { _editor.Cut(); return Task.CompletedTask; }, _editor.CanCut));
        items.Add(Item("SpellingCopy", "Copy", () => { _editor.Copy(); return Task.CompletedTask; }, _editor.CanCopy));
        items.Add(Item("SpellingPaste", "Paste", () => { _editor.Paste(); return Task.CompletedTask; }, !_editor.IsReadOnly));
        items.Add(Item("SpellingDelete", "Delete", () => { _editor.SelectedText = ""; return Task.CompletedTask; }, !_editor.IsReadOnly && _editor.SelectionStart != _editor.SelectionEnd));
        items.Add(Item("SpellingSelectAll", "Select all", () => { _editor.SelectAll(); return Task.CompletedTask; }));
        if (AdditionalItems is not null) { items.AddRange(AdditionalItems()); }
        items.Add(new Separator());
        MenuItem dictionary = new() { Name = "CommitDictionary", Header = T("Dictionary"), IsEnabled = !_changingDictionary && _menuSettings is not null };
        List<MenuItem> choices = [DictionaryItem(DictionaryCatalog.NoneName, T("None"))];
        try { choices.AddRange(_catalog.GetInstalled().Select(item => DictionaryItem(item.Name, item.Name))); }
        catch (Exception error) { Report(error); choices.Add(new() { Header = error.Message, IsEnabled = false }); }
        dictionary.ItemsSource = choices; items.Add(dictionary); items.Add(new Separator());
        MenuItem marks = Item("MarkIllFormedLines", "Mark ill formed lines", () => SaveFlag(false), _accepted is not null);
        marks.ToggleType = MenuItemToggleType.CheckBox; marks.IsChecked = _accepted?.MarkIllFormedLinesInCommitMsg == true; items.Add(marks);
        MenuItem completion = Item("ProvideAutocompletion", "Provide auto completion", () => SaveFlag(true), _accepted is not null);
        completion.ToggleType = MenuItemToggleType.CheckBox; completion.IsChecked = _accepted?.ProvideAutocompletion == true;
        items.Add(completion);
        return items.ToArray();
    }

    private MenuItem DictionaryItem(string name, string label)
    {
        GitDictionarySettingsSnapshot? expected = _menuSettings;
        MenuItem item = Item("CommitDictionary_" + name, label,
            () => expected is null ? Task.CompletedTask : SelectDictionaryAsync(expected, name));
        item.ToggleType = MenuItemToggleType.Radio; item.IsChecked = (_menuSettings ?? _settings)?.EffectiveDictionary == name;
        return item;
    }

    private async Task SelectDictionaryAsync(GitDictionarySettingsSnapshot before, string name)
    {
        if (_changingDictionary || _accepted is null) { return; }
        _changingDictionary = true;
        long generation = ++_settingsRevision;
        InvalidateMenu();
        try
        {
            // The menu snapshot is the CAS baseline. A peer edit since opening is a visible conflict.
            GitDictionarySettingsSnapshot saved = await _repository.WriteLocalDictionaryAsync(before, name, _lifetime.Token);
            if (_closing) { return; }
            if (generation != _settingsRevision)
            { if (_accepted is DesktopPreferences latest) { await ReloadSettingsAsync(latest); }
                return; }
            InvalidateMenu();
            _settings = saved; SetService(saved.EffectiveDictionary, force: true); Schedule();
        }
        finally { _changingDictionary = false; }
    }

    private Task SaveFlag(bool completion)
    {
        DesktopPreferences latest = _preferences.Load();
        DesktopPreferences saved = completion ? latest with { ProvideAutocompletion = !latest.ProvideAutocompletion }
            : latest with { MarkIllFormedLinesInCommitMsg = !latest.MarkIllFormedLinesInCommitMsg };
        _preferences.Save(saved);
        PublishSavedDesktop(_preferences.FilePath, saved);
        return Task.CompletedTask;
    }

    private void Remove(SpellingSpan target, string text)
    {
        // Reuse the original algorithm for adjacent spaces instead of maintaining UI copies
        // of its first/middle/punctuation/last-word rules. Text/WordIndex/DeleteWord use only
        // the linked parser's in-memory regexes; never access Dictionary or Initialize.
        using NetSpell.SpellChecker.Spelling words = new() { Text = text };
        words.WordIndex = words.GetWordIndexFromTextIndex(target.Start);
        if (words.CurrentWord != target.Word || words.TextIndex != target.Start)
        { throw new InvalidOperationException("The word selected for removal no longer matches the checked text."); }
        NetSpell.SpellChecker.SpellingEventArgs? deletion = null;
        words.DeletedWord += (_, args) => deletion = args;
        words.DeleteWord();
        if (deletion is null || deletion.TextIndex < 0 || deletion.TextIndex > text.Length - deletion.Word.Length
            || !text.AsSpan(deletion.TextIndex, deletion.Word.Length).SequenceEqual(deletion.Word.AsSpan()))
        { throw new InvalidOperationException("The spelling parser did not return a valid word removal range."); }
        Replace(new(deletion.TextIndex, deletion.Word.Length, deletion.Word), "");
    }

    private void Replace(SpellingSpan target, string replacement)
    {
        // RichTextBox.SelectionStart is the lower bound even for a reversed selection.
        int start = Math.Min(_editor.SelectionStart, _editor.SelectionEnd);
        int selectionLength = Math.Abs(_editor.SelectionEnd - _editor.SelectionStart);
        _editor.SelectionStart = target.Start; _editor.SelectionEnd = target.End;
        _editor.SelectedText = replacement;
        int length = _editor.Text?.Length ?? 0;
        start = Math.Min(start, length);
        if (selectionLength > length - start) { selectionLength = 0; }
        _editor.SelectionStart = start; _editor.SelectionEnd = start + selectionLength;
    }

    private MenuItem Item(string name, string label, Func<Task> action, bool enabled = true)
    {
        MenuItem item = new() { Name = name, Header = T(label), IsEnabled = enabled };
        item.Click += (_, _) => { if (!_closing) { Track(action); } };
        return item;
    }
    private string T(string text) => _translations.Translate(text);
    private void Report(Exception error) { _error = error.Message; UpdateNotice(); }
    private void UpdateNotice()
    {
        Notice.IsVisible = _error is not null;
        Notice.Text = _error is null ? "" : T("Spelling checker") + ": " + _error;
    }

    private void Track(Func<Task> action, Func<bool>? current = null)
    {
        if (_closing) { return; }
        Task work = ObserveAsync(action, current); _work.Add(work);
        _ = RemoveCompletedAsync(work);
    }
    private async Task ObserveAsync(Func<Task> action, Func<bool>? current)
    {
        try { await action(); }
        catch (OperationCanceledException) when (_closing || _debounce?.IsCancellationRequested == true) { }
        catch (Exception error) { if (!_closing && (current?.Invoke() ?? true)) { Report(error); } }
    }
    private async Task RemoveCompletedAsync(Task task) { await task; _work.Remove(task); }

    internal Task BeginClose()
    {
        if (_close is not null) { return _close; }
        _closing = true; _settingsRevision++; _menuRevision++; _revision++;
        SavedDesktop -= DesktopPublished;
        _editor.PropertyChanged -= EditorChanged; _view.CompositionChanged -= CompositionChanged;
        _editor.RemoveHandler(InputElement.PointerPressedEvent, EditorPointerPressed);
        _editor.RemoveHandler(InputElement.KeyDownEvent, EditorKeyDown);
        _menu.Opening -= MenuOpening; _menu.Closed -= MenuClosed; _menu.Close();
        _debounce?.Cancel(); _lifetime.Cancel();
        Task[] engines = _services.Select(service => service.BeginClose()).ToArray();
        _close = DrainAsync(engines, _work.ToArray());
        return _close;
    }
    private async Task DrainAsync(Task[] engines, Task[] work)
    {
        try { await Task.WhenAll(engines.Concat(work)); }
        finally { _view.Dispose(); _lifetime.Dispose(); }
    }
}

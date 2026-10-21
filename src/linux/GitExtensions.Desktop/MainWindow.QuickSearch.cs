using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private RevisionQuickSearch? _quickSearch;
    private GitCompareRevision? _quickSearchArtificialSelection;
    private string? _quickSearchLastCommit, _configuredUserName;
    private readonly List<string> _historySelectionOrder = [];
    private IReadOnlyList<GitReference>? _quickSearchReferenceSnapshot;
    private bool _quickSearchIncludesStashes;
    private ILookup<string, GitReference>? _quickSearchReferences;
    private CancellationTokenSource? _quickSearchClipboard;
    private readonly HashSet<Task> _quickSearchClipboardWork = [];
    private long _quickSearchGeneration;
    private bool _applyingQuickSearchSelection;
    internal Func<CancellationToken, Task<string?>>? QuickSearchClipboardReader { get; set; }
    internal Task QuickSearchCompletion => Task.WhenAll(_quickSearchClipboardWork.ToArray());

    private bool QuickSearchHasFocus => _commits.IsKeyboardFocusWithin;
    private bool CanQuickSearch => !_busy && !_refreshing && !_commitInfoClosing && _repository is not null && !_lifetime.IsCancellationRequested
        && !IsCommitDiff && !OwnedWindows.Any(window => window.IsVisible);
    private int QuickSearchArtificialCount => _historyRows.OfType<ArtificialHistoryRevision>().Count();

    private Control WithQuickSearchNotice(Control table)
    {
        string prefix = _translations.Translate("TranslatedStrings", "_searchingFor", "Text", "Searching for: ");
        _quickSearch = new(() => _commits.Items.Count, QuickSearchCurrentIndex, QuickSearchMatches, prefix,
            () => _preferencesStore.Load().RevisionGridQuickSearchTimeout);
        Grid overlay = new() { Name = "RevisionQuickSearchSurface", ClipToBounds = true };
        overlay.Children.Add(table); overlay.Children.Add(_quickSearch.Notice);
        return overlay;
    }

    private void InitializeQuickSearch()
    {
        foreach (Control surface in new Control[] { _commits })
        {
            surface.AddHandler(TextInputEvent, QuickSearchTextInput, RoutingStrategies.Tunnel);
            surface.AddHandler(KeyDownEvent, QuickSearchKeyDown, RoutingStrategies.Tunnel);
            surface.LostFocus += (_, _) => Dispatcher.UIThread.Post(() => { if (!QuickSearchHasFocus) { HideQuickSearch(); } });
            surface.GotFocus += (_, _) => { if (!_applyingQuickSearchSelection && _quickSearchClipboard is not null) { CancelQuickSearchClipboard(); } };
        }
        _commits.PropertyChanged += (_, args) => { if (args.Property == ItemsControl.ItemsSourceProperty) { HideQuickSearch(); } };
        Deactivated += (_, _) => HideQuickSearch();
        Closed += (_, _) => { HideQuickSearch(); _quickSearch?.Dispose(); };
    }

    private void ObserveQuickSearchSelection(SelectionChangedEventArgs args)
    {
        if (!_applyingQuickSearchSelection) { CancelQuickSearchClipboard(); }
        foreach (object removed in args.RemovedItems.Cast<object>()) { _historySelectionOrder.Remove(HistoryRevisionItems.Key(removed)); }
        foreach (object added in args.AddedItems.Cast<object>())
        { string key = HistoryRevisionItems.Key(added); _historySelectionOrder.Remove(key); _historySelectionOrder.Add(key); }
        _quickSearchLastCommit = _historySelectionOrder.LastOrDefault();
        if (_quickSearchLastCommit is not null) { _parentChildLatestCommit = _quickSearchLastCommit; }
    }

    private int QuickSearchCurrentIndex()
    {
        object? current = _quickSearchLastCommit is { } key && _historySelectionOrder.Contains(key) ? HistoryItem(key) : PrimaryHistoryItem;
        return current is null ? -1 : _commits.Items.IndexOf(current);
    }

    private bool QuickSearchMatches(int index, string criteria)
    {
        if (index < 0 || index >= _commits.ItemCount) { return false; }
        if (_commits.Items[index] is ArtificialHistoryRevision artificial)
        {
            // The original search provider recognizes these reserved search identities; they never enter a Git command.
            return RevisionQuickSearch.Matches(new string(artificial.Kind == GitCompareRevisionKind.WorkingTree ? '1' : '2', 40),
                _configuredUserName, T(artificial.Subject), [], criteria);
        }
        if (_commits.Items[index] is not GitCommit commit) { return false; }
        if (!ReferenceEquals(_quickSearchReferenceSnapshot, _navigationReferences) || _quickSearchIncludesStashes != _historyView.ShowStashes)
        {
            _quickSearchReferenceSnapshot = _navigationReferences; _quickSearchIncludesStashes = _historyView.ShowStashes;
            _quickSearchReferences = _navigationReferences.Where(reference => reference.RevisionHash is not null
                && (!_historyView.ShowStashes || reference.Name != "refs/stash")).ToLookup(reference => reference.RevisionHash!, StringComparer.Ordinal);
        }
        string message = commit.FullMessage ?? (commit.Subject + (commit.Body.Length == 0 ? "" : "\n\n" + commit.Body));
        return RevisionQuickSearch.Matches(commit.Hash, commit.AuthorName, message,
            _quickSearchReferences![commit.Hash].Select(reference => QuickSearchReferenceName(reference.Name)), criteria);
    }

    private static string QuickSearchReferenceName(string name)
    {
        foreach (string prefix in new[] { "refs/heads/", "refs/remotes/", "refs/tags/", "refs/" })
        { if (name.StartsWith(prefix, StringComparison.Ordinal)) { name = name[prefix.Length..]; break; } }
        return name.EndsWith("^{}", StringComparison.Ordinal) ? name[..^3] : name;
    }

    private async void QuickSearchTextInput(object? sender, TextInputEventArgs args)
    {
        if (args.Handled || !CanQuickSearch || !QuickSearchHasFocus || string.IsNullOrEmpty(args.Text) || args.Text.All(char.IsControl)) { return; }
        args.Handled = true; CancelQuickSearchClipboard();
        await ApplyQuickSearchAsync(_quickSearch!.Type(args.Text));
    }

    private async void QuickSearchKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Handled || !CanQuickSearch || !QuickSearchHasFocus) { return; }
        if (args.Key == Key.Escape && args.KeyModifiers == KeyModifiers.None) { HideQuickSearch(); return; }
        if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None) { HideQuickSearch(); return; }
        if (args.Key == Key.Back && args.KeyModifiers == KeyModifiers.None)
        {
            CancelQuickSearchClipboard(); int? found = _quickSearch!.Backspace(out bool handled); args.Handled = handled;
            await ApplyQuickSearchAsync(found); return;
        }
        if (args.Key == Key.V && args.KeyModifiers == KeyModifiers.Control)
        {
            args.Handled = true; BeginQuickSearchClipboard(); return;
        }
        if ((args.KeyModifiers & KeyModifiers.Control) != 0 && args.Key is >= Key.A and <= Key.Z) { HideQuickSearch(); }
    }

    private Task ApplyQuickSearchAsync(int? found)
        => found is int index && CanQuickSearch && index >= 0 && index < _commits.ItemCount
            ? SelectHistoryItemAsync(_commits.Items[index]!) : Task.CompletedTask;

    private void BeginQuickSearchClipboard()
    {
        CancelQuickSearchClipboard();
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _quickSearchClipboard = source;
        Task task = ReadQuickSearchClipboardAsync(source, _quickSearchGeneration, _repository!);
        _quickSearchClipboardWork.Add(task); _ = ForgetQuickSearchClipboardAsync(task);
    }

    private async Task ReadQuickSearchClipboardAsync(CancellationTokenSource source, long generation, GitRepository repository)
    {
        using WindowWork work = TrackWindowWork();
        try
        {
            Task<string?> read = QuickSearchClipboardReader is { } reader ? reader(source.Token)
                : Clipboard is { } clipboard ? ClipboardExtensions.TryGetTextAsync(clipboard) : Task.FromResult<string?>(null);
            string? text = await read.WaitAsync(source.Token);
            if (source.IsCancellationRequested || generation != _quickSearchGeneration || _repository != repository || !CanQuickSearch || !QuickSearchHasFocus) { return; }
            if (text is null) { _quickSearch?.Hide(); return; }
            await ApplyQuickSearchAsync(_quickSearch!.Paste(text));
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!source.IsCancellationRequested && generation == _quickSearchGeneration && _repository == repository && CanQuickSearch)
            { LastException = error; _state.Text = error.Message; AppendOutput(error.Message); }
        }
        finally
        {
            if (ReferenceEquals(_quickSearchClipboard, source)) { _quickSearchClipboard = null; }
            source.Dispose();
        }
    }

    private async Task ForgetQuickSearchClipboardAsync(Task task)
    { try { await task; } finally { _quickSearchClipboardWork.Remove(task); } }
    private void CancelQuickSearchClipboard()
    { _quickSearchGeneration++; _quickSearchClipboard?.Cancel(); _quickSearchClipboard = null; }
    private void HideQuickSearch()
    { CancelQuickSearchClipboard(); _quickSearch?.Hide(); }

    private async Task ShowQuickSearchHelpAsync()
    {
        HideQuickSearch();
        Window dialog = new()
        {
            Name = "RevisionQuickSearchHelp", Title = T("Information"), Width = 440,
            SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = ActualThemeVariant
        };
        Button accept = new() { Name = "CloseQuickSearchHelp", Content = T("OK"), IsDefault = true, IsCancel = true, MinWidth = 75, HorizontalAlignment = HorizontalAlignment.Right };
        accept.Click += (_, _) => dialog.Close();
        Grid message = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        Image icon = OriginalIconImage("information.png"); icon.VerticalAlignment = VerticalAlignment.Top;
        message.Children.Add(icon);
        TextBlock help = new() { Name = "RevisionQuickSearchHelpText", Text = QuickSearchHelpText, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(help, 1); message.Children.Add(help);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(12), Spacing = 12,
            Children = { message, accept }
        };
        dialog.Opened += (_, _) => { accept.Focus(); ModalWindowActivation.AfterMapping(dialog, () => dialog.IsVisible); };
        await dialog.ShowDialog(WorkflowOwner);
    }
    private string QuickSearchHelpText => _translations.Translate("RevisionGrid", "_quickSearchQuickHelp", "Text", "Start typing in revision grid to start quick search.");
}

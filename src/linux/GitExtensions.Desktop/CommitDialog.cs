using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;
using System.Text;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux.Desktop;

/// <summary>The FormCommit layout and workflows, using the existing Git backend and translation catalogue.</summary>
public sealed class CommitDialog : Window
{
    private readonly GitRepository _repository;
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Control> _interactive = [];
    private readonly TreeView _unstaged = new() { Name = "Unstaged", SelectionMode = SelectionMode.Multiple };
    private readonly TreeView _staged = new() { Name = "Staged", SelectionMode = SelectionMode.Multiple };
    private readonly FileStatusListView _unstagedView;
    private readonly FileStatusListView _stagedView;
    private readonly ComboBox _filter = new() { Name = "SelectionFilter", IsEditable = true, Width = 121, Height = 24, Padding = new Thickness(2, 0) };
    private readonly List<string> _selectionFilterHistory = [];
    private Control? _selectionFilterToolbar;
    private CancellationTokenSource? _selectionFilterOperation;
    private bool _updatingSelectionFilterHistory;
    private readonly TextBox _source = new();
    private readonly DiffView _diff;
    private readonly FileContentView _content;
    private GitComparisonDiffOptions _viewerOptions = new();
    private readonly TextBox _message = new() { Name = "Message", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _amend = new() { Name = "Amend", Height = 24, MinHeight = 24 };
    private readonly CheckBox _resetAuthor = new() { Name = "ResetAuthor", IsVisible = false, Height = 24, MinHeight = 24 };
    private readonly TextBox _author = new() { Name = "AuthorOverride", Watermark = "name <mail>", Width = 250 };
    private readonly ComboBox _sign = new() { Name = "Signing", SelectedIndex = 0, Width = 250 };
    private readonly TextBox _signingKey = new() { Name = "SigningKey", Width = 250, Watermark = "Signing key", IsEnabled = false };
    private readonly ComboBox _kind = new() { Name = "CommitKind", SelectedIndex = 0, Width = 250 };
    private readonly ComboBox _target = new() { Name = "TargetCommit", Width = 330, IsEnabled = false };
    private readonly TextBox _output = new() { Name = "CommitOutput", IsReadOnly = true, AcceptsReturn = true, IsVisible = false, Height = 72, FontFamily = new FontFamily("monospace") };
    private readonly TextBlock _state = new() { Name = "CommitStatus", TextWrapping = TextWrapping.Wrap };
    private readonly MenuItem _signOff;
    private readonly MenuItem _closeAfterCommit;
    private readonly MenuItem _closeWhenClean;
    private readonly MenuItem _selectStaged;
    private readonly MenuItem _refreshOnFocus;
    private readonly MenuItem _onlyMyMessages;
    private readonly MenuItem _noVerify;
    private readonly MenuItem _historyMenu = new() { Name = "CommitMessageHistory" };
    private MenuItem? _signingKeyMenu;
    private readonly CommitPreferencesStore _preferencesStore;
    private CommitPreferences _preferences;
    private CommitDraftStore? _drafts;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _historyOperation;
    private bool _initialized;
    private bool _closeWhenIdle;
    private bool _confirmationOpen;
    private bool _commitRequested;
    private bool _draftReset;
    private CancellationToken OperationToken => _operation?.Token ?? _lifetime.Token;
    private readonly Button _commit;
    private readonly Button _push;
    private readonly Button _stage;
    private readonly Button _unstage;
    private readonly Button _stageAll;
    private readonly Button _unstageAll;
    private readonly Button _stageHunk;
    private readonly Button _unstageHunk;
    private readonly Button _stageLines;
    private readonly Button _unstageLines;
    private Task? _initialization;
    private GitStatus? _status;
    private GitPatchFile? _patch;
    private bool _busy;
    private bool _pendingDiffAppearance;
    private bool _refreshing;
    private bool _viewStaged;
    private string? _path;
    private string _committer = "";
    private string? _loadedTemplate;
    private GitCommit? _headCommit;
    private bool _mergePending;

    public CommitDialog(GitRepository repository, CommitPreferencesStore? preferencesStore = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        _preferencesStore = preferencesStore ?? new CommitPreferencesStore();
        _preferences = _preferencesStore.Load();
        Title = C("$this", "Commit");
        Width = 1100;
        Height = 760;
        MinWidth = 860;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _diff = new DiffView(_source, new DiffViewerPreferencesStore(Path.GetDirectoryName(Path.GetDirectoryName(_preferencesStore.FilePath))!)) { Name = "SelectedDiff" };
        _viewerOptions = _diff.ComparisonMenu.Options;
        _diff.ComparisonMenu.OptionsChanged += async (_, _) =>
        {
            _viewerOptions = _diff.ComparisonMenu.Options;
            _pendingDiffAppearance = true;
            if (!_busy) { await ReloadDiffAppearanceAsync(); }
        };
        _diff.DisplayOptionsChanged += async (_, _) =>
        {
            _pendingDiffAppearance = true;
            if (!_busy) { await ReloadDiffAppearanceAsync(); }
        };
        _content = new FileContentView(_source, _diff);
        _diff.ContextMenu = new ContextMenu { ItemsSource = _diff.CreateContextMenuItems("CommitDiff") };
        FileStatusListPreferencesStore fileListPreferences = new(Path.GetDirectoryName(Path.GetDirectoryName(_preferencesStore.FilePath))!);
        _unstagedView = new FileStatusListView(_unstaged, FileHeader, fileListPreferences) { Name = "UnstagedFileStatusList" };
        _stagedView = new FileStatusListView(_staged, FileHeader, fileListPreferences) { Name = "StagedFileStatusList" };
        _amend.Content = C("Amend", "Amend commit");
        _resetAuthor.Content = C("ResetAuthor", "Reset author");
        _sign.ItemsSource = new[] { T("Git default GPG signing"), T("Do not sign commit"), T("Sign with default GPG"), T("Sign with specific GPG") };
        _kind.ItemsSource = new[] { T("Normal commit"), "Fixup", "Squash" };
        _sign.SelectedIndex = _kind.SelectedIndex = 0;
        _target.ItemTemplate = new FuncDataTemplate<GitCommit>((commit, _) => new TextBlock
        {
            Text = commit is null ? "" : $"{commit.Hash[..Math.Min(9, commit.Hash.Length)]} {commit.Subject}",
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        _signOff = Toggle("signOffToolStripMenuItem", "Sign-off commit");
        _closeAfterCommit = Toggle("closeDialogAfterEachCommitToolStripMenuItem", "Close dialog after each commit", _preferences.CloseAfterEachCommit);
        _closeWhenClean = Toggle("closeDialogAfterAllFilesCommittedToolStripMenuItem", "Close dialog when all changes are committed", _preferences.CloseWhenAllChangesCommitted);
        _selectStaged = Toggle("tsmiSelectStagedOnEnterMessage", "Select staged on entering message editor", _preferences.SelectStagedOnMessageFocus);
        _refreshOnFocus = Toggle("refreshDialogOnFormFocusToolStripMenuItem", "Refresh dialog on form focus", _preferences.RefreshOnFormFocus);
        _onlyMyMessages = Toggle("ShowOnlyMyMessagesToolStripMenuItem", "Show only my messages", _preferences.ShowOnlyMyMessages);
        _noVerify = Toggle("noVerifyToolStripMenuItem", "No verify");
        foreach (MenuItem item in new[] { _closeAfterCommit, _closeWhenClean, _selectStaged, _refreshOnFocus, _onlyMyMessages })
        {
            item.Click += (_, _) => SavePreferences();
        }
        _historyMenu.Header = IconLabel(C("commitMessageToolStripMenuItem", "Commit message"), "WorkingDirChanges.png");
        _historyMenu.ItemsSource = new MenuItem[] { new() { Name = "HistoryLoading", Header = T("Loading…"), IsEnabled = false }, _onlyMyMessages };
        _historyMenu.SubmenuOpened += async (_, args) => { if (ReferenceEquals(args.Source, _historyMenu)) { await LoadHistoryAsync(); } };
        _onlyMyMessages.Click += async (_, _) => await LoadHistoryAsync();
        _stage = Action("Stage", "toolStageItem", "Stage", "Stage.png", () => StageAsync(false));
        _unstage = Action("Unstage", "toolUnstageItem", "Unstage", "Unstage.png", () => UnstageAsync(false));
        _stageAll = Action("StageAll", "", "Stage all", "StageAll.png", () => StageAsync(true), false);
        _unstageAll = Action("UnstageAll", "", "Unstage all", "UnstageAll.png", () => UnstageAsync(true), false);
        _commit = Action("Commit", "Commit", "Commit", "RepoStateClean.png", () => CommitAsync(false));
        _push = Action("CommitAndPush", "_commitAndPush", "Commit & push", "ArrowUp.png", () => CommitAsync(true));
        _stageHunk = Action("StageHunk", "", "Stage hunk", "Stage.png", () => ApplyPatchAsync(false, false));
        _unstageHunk = Action("UnstageHunk", "", "Unstage hunk", "Unstage.png", () => ApplyPatchAsync(true, false));
        _stageLines = Action("StageLines", "", "Stage selected line(s)", "Stage.png", () => ApplyPatchAsync(false, true), false);
        _unstageLines = Action("UnstageLines", "", "Unstage selected line(s)", "Unstage.png", () => ApplyPatchAsync(true, true), false);
        ConfigureList(_unstagedView, false);
        ConfigureList(_stagedView, true);
        _interactive.AddRange([_filter, _message, _amend, _resetAuthor, _author, _sign, _signingKey, _kind, _target]);
        Content = BuildLayout();
        ToolTip.SetTip(_filter, C("_selectionFilterToolTip", "Enter a regular expression to select unstaged files."));
        _filter.PropertyChanged += async (_, change) =>
        {
            if (change.Property == ComboBox.TextProperty && !_busy && !_updatingSelectionFilterHistory)
            {
                await ScheduleSelectionFilterAsync();
            }
        };
        _filter.SelectionChanged += async (_, _) =>
        {
            if (!_updatingSelectionFilterHistory && !_busy && _filter.SelectedItem is string expression)
            {
                _selectionFilterOperation?.Cancel();
                _filter.Text = expression;
                await ApplySelectionFilterAsync(expression);
            }
        };
        _message.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty) { if (!string.IsNullOrEmpty(_message.Text)) { _draftReset = false; } UpdateAvailability(); UpdateStatus(); }
            else if (change.Property == TextBox.CaretIndexProperty) { UpdateStatus(); }
        };
        _message.GotFocus += async (_, _) =>
        {
            if (!_busy && _selectStaged.IsChecked && _stagedView.FilteredEntries.Count > 0)
            {
                bool selectFirst = !_stagedView.SelectedTags.OfType<GitChange>().Any();
                if (selectFirst)
                {
                    bool refreshing = _refreshing;
                    _refreshing = true;
                    try { _stagedView.SelectTags([_stagedView.FilteredEntries[0].Tag]); }
                    finally { _refreshing = refreshing; }
                }
                if (selectFirst || !_viewStaged)
                {
                    await SelectFileAsync(_stagedView, true);
                    // Loading the selected diff temporarily disables the editor.
                    // Keep the message as the user's keyboard destination.
                    if (IsVisible && !_lifetime.IsCancellationRequested && !_busy && !_commitRequested
                        && FocusManager?.GetFocusedElement() is null) { _message.Focus(); }
                }
            }
        };
        _amend.IsCheckedChanged += async (_, _) =>
        {
            _resetAuthor.IsVisible = _amend.IsChecked == true;
            if (_amend.IsChecked != true) { _resetAuthor.IsChecked = false; }
            if (!_busy && _amend.IsChecked == true && string.IsNullOrWhiteSpace(_message.Text))
            {
                await RunAsync(T("Commit message"), async () =>
                {
                    GitCommit? previous = (await _repository.GetLogAsync(1, all: false, cancellationToken: OperationToken)).FirstOrDefault();
                    if (previous is not null) { _message.Text = previous.Subject + (previous.Body.Length == 0 ? "" : "\n\n" + previous.Body.TrimEnd()); }
                });
            }
            UpdateAvailability();
        };
        _sign.SelectionChanged += (_, _) => UpdateAvailability();
        _kind.SelectionChanged += (_, _) => UpdateAvailability();
        _target.SelectionChanged += (_, _) => UpdateAvailability();
        _author.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateAvailability(); } };
        _diff.Editor.TextArea.SelectionChanged += (_, _) => UpdateAvailability();
        KeyDown += HandleShortcut;
        AddHandler(KeyDownEvent, (sender, args) =>
        {
            if (args.KeyModifiers == KeyModifiers.Control && (args.Key is Key.Enter or Key.S || args.Key == Key.E && _diff.IsKeyboardFocusWithin)) { HandleShortcut(sender, args); }
        }, RoutingStrategies.Tunnel);
        Opened += async (_, _) => { await InitializeAsync(); _unstaged.Focus(); };
        Activated += async (_, _) =>
        {
            if (_initialized && !_busy && !_confirmationOpen && !_closeWhenIdle && _refreshOnFocus.IsChecked) { await RefreshAsync(); }
        };
        Closing += (_, args) =>
        {
            if (_busy)
            {
                args.Cancel = true;
                _closeWhenIdle = true;
                _operation?.Cancel();
                return;
            }
            if (_initialized && _kind.SelectedIndex == 0 && (!_draftReset || !string.IsNullOrEmpty(_message.Text) || _amend.IsChecked == true))
            {
                try { _drafts?.Save(_message.Text, _amend.IsChecked == true, _preferences.RememberAmendCommitState); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EncoderFallbackException)
                {
                    args.Cancel = true;
                    _closeWhenIdle = false;
                    ShowFailure(T("Cannot save commit message"), exception);
                }
            }
        };
        Closed += (_, _) => { _lifetime.Cancel(); _selectionFilterOperation?.Cancel(); _historyOperation?.Cancel(); _unstagedView.Dispose(); _stagedView.Dispose(); _content.Dispose(); };
        UpdateAvailability();
    }

    public bool HasCommitted { get; private set; }
    public bool HasRepositoryChanges { get; private set; }
    public string? LastError { get; private set; }
    public event EventHandler? RepositoryChanged;
    internal bool IsBusy => _busy || _commitRequested;
    public Task InitializeAsync() => _initialization ??= RunAsync(T("Refresh"), async () =>
    {
        await RefreshDataAsync();
        _drafts = await CommitDraftStore.OpenAsync(_repository, OperationToken);
        (string message, bool amend) = await _drafts.LoadAsync(_preferences.RememberAmendCommitState, OperationToken);
        if (!_drafts.IsMergeCommit && amend) { _amend.IsChecked = true; }
        if (string.IsNullOrEmpty(_message.Text))
        {
            if (!string.IsNullOrEmpty(message)) { _message.Text = message; }
            else
            {
                _loadedTemplate = await _repository.GetCommitTemplateAsync(cancellationToken: OperationToken);
                _message.Text = _loadedTemplate ?? "";
            }
        }
        _drafts.RememberDisplayedMessage(_message.Text ?? "");
        _initialized = true;
    });

    public Task RefreshAsync() => RunAsync(T("Refresh"), RefreshDataAsync);
    private string T(string text) => _translations.Translate(text);
    private string C(string item, string source) => _translations.Translate("FormCommit", item, "Text", source);

    private Control BuildLayout()
    {
        Grid main = new() { ColumnDefinitions = new ColumnDefinitions("43*,5,57*"), Margin = new Thickness(6, 6, 6, 0) };
        Grid files = new() { RowDefinitions = new RowDefinitions("45*,5,55*") };
        _selectionFilterToolbar = Row(new TextBlock { Text = C("toolStripLabel1", "Selection Filter"), VerticalAlignment = VerticalAlignment.Center }, _filter);
        _selectionFilterToolbar.Name = "SelectionFilterToolbar";
        _selectionFilterToolbar.IsVisible = false; // AppSettings.CommitDialogSelectionFilter defaults to false.
        DockPanel unstaged = Pane(_unstagedView, _selectionFilterToolbar, T("Unstaged"));
        files.Children.Add(unstaged);
        files.Children.Add(Splitter(1, true));
        DockPanel staged = Pane(_stagedView, Row(_unstageAll, _unstage, _stage, _stageAll), C("commitStagedCountLabel", "Staged"));
        Grid.SetRow(staged, 2);
        files.Children.Add(staged);
        main.Children.Add(files);
        main.Children.Add(Splitter(1, false));

        Grid right = new() { RowDefinitions = new RowDefinitions("68*,5,32*") };
        right.Children.Add(Pane(_content, Row(_stageHunk, _unstageHunk, _stageLines, _unstageLines,
            _diff.ComparisonMenu.CreateIncreaseContextButton("CommitDiffIncreaseContext"),
            _diff.ComparisonMenu.CreateDecreaseContextButton("CommitDiffDecreaseContext"),
            _diff.ComparisonMenu.CreateWholeFileButton("CommitDiffEntireFile"), _diff.ReadingMenu.CreateEncodingSelector())));
        right.Children.Add(Splitter(1, true));
        Grid editor = new() { ColumnDefinitions = new ColumnDefinitions("175,5,*") };
        Button cancel = new() { Name = "Cancel", Content = C("Cancel", "Cancel"), IsCancel = true, Height = 26 };
        cancel.Click += (_, _) => Close();
        StackPanel actions = new() { Spacing = 4, Children =
        {
            _commit, _push, _amend, _resetAuthor,
            Action("ResetSoft", "ResetSoft", "Reset soft", "ResetCurrentBranchToHere.png", ResetSoftAsync),
            Action("StashStaged", "StashStaged", "Stash staged changes", "stash.png", StashStagedAsync),
            Action("ResetAll", "btnResetAllChanges", "Reset all changes", "ResetWorkingDirChanges.png", () => ResetChangesAsync(true)),
            Action("ResetUnstaged", "btnResetUnstagedChanges", "Reset unstaged changes", "ResetWorkingDirChanges.png", () => ResetChangesAsync(false)), cancel
        }};
        editor.Children.Add(actions);
        _signingKeyMenu = InputMenu(T("Signing key"), _signingKey);
        MenuItem optionsMenu = new() { Name = "Options", Header = C("tsmiOptions", "Options"), ItemsSource = new MenuItem[]
        {
            _closeAfterCommit, _closeWhenClean, _refreshOnFocus, _selectStaged,
            new() { Header = "-" }, _signOff,
            InputMenu(C("toolAuthorLabelItem", "Author: (Format: \"name <mail>\")"), _author), _noVerify,
            new() { Header = "-" }, InputMenu(T("Sign commit"), _sign), _signingKeyMenu,
            InputMenu(T("Commit kind"), _kind), InputMenu(T("Target commit"), _target)
        } };
        DockPanel.SetDock(_historyMenu, Dock.Left);
        DockPanel.SetDock(optionsMenu, Dock.Right);
        MenuItem templates = new() { Header = IconLabel(C("commitTemplatesToolStripMenuItem", "Commit templates"), "CommitTemplates.png"),
            ItemsSource = new[] { MenuAction("LoadTemplate", T("Load configured template"), LoadTemplateAsync) } };
        DockPanel.SetDock(templates, Dock.Left);
        Menu options = new() { Name = "CommitToolbar", Height = 28, HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsPanel = new FuncTemplate<Panel?>(() => new DockPanel { LastChildFill = false }), ItemsSource = new[] { _historyMenu, optionsMenu, templates } };
        _interactive.Add(options);
        DockPanel message = new() { LastChildFill = true };
        DockPanel.SetDock(options, Dock.Top);
        message.Children.Add(options);
        DockPanel.SetDock(_output, Dock.Bottom);
        message.Children.Add(_output);
        message.Children.Add(_message);
        Grid.SetColumn(message, 2);
        editor.Children.Add(message);
        Grid.SetRow(editor, 2);
        right.Children.Add(editor);
        Grid.SetColumn(right, 2);
        main.Children.Add(right);
        DockPanel root = new();
        Border status = new() { Child = _state, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(6, 3) };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        root.Children.Add(main);
        return root;
    }

    private Control FileHeader(FileStatusListEntry entry)
    {
        GitChange change = (GitChange)entry.Tag;
        string icon = FileStatusListView.IconName(entry);
        string path = ExportDialogControls.DisplayPath(entry.DisplayPath ?? entry.Path);
        Control label = IconLabel(change.OriginalPath is null ? path : $"{ExportDialogControls.DisplayPath(change.OriginalPath)} → {path}", icon);
        ToolTip.SetTip(label, change.Path + (change.IsConflict ? " — " + T("There are unresolved merge conflicts") : ""));
        return label;
    }

    private void ConfigureList(FileStatusListView view, bool staged)
    {
        TreeView list = view.Tree;
        list.Focusable = true;
        _interactive.AddRange([view, list]);
        list.SelectionChanged += async (_, args) =>
        {
            if (ReferenceEquals(args.Source, list) && !view.IsUpdating && !_refreshing && !_busy) { await SelectFileAsync(view, staged); }
        };
        list.DoubleTapped += async (_, _) => { if (!_busy) { if (staged) { await UnstageAsync(false); } else { await StageAsync(false); } } };
        view.DisplayChanged += async (_, _) =>
        {
            UpdateAllButton(_stageAll, _unstagedView, false);
            UpdateAllButton(_unstageAll, _stagedView, true);
            UpdateAvailability();
            UpdateStatus();
            if (!_refreshing && !_busy && _viewStaged == staged) { await SelectFileAsync(view, staged); }
        };
    }

    private void UpdateAllButton(Button button, FileStatusListView view, bool staged)
    {
        bool filtered = !string.IsNullOrEmpty(view.FilterText);
        button.Content = IconLabel("", staged ? filtered ? "UnstageAllFiltered.png" : "UnstageAll.png" : filtered ? "StageAllFiltered.png" : "StageAll.png");
        ToolTip.SetTip(button, staged ? filtered ? C("_unstageFiltered", "Unstage filtered") : C("_unstageAll", "Unstage all")
            : filtered ? C("_stageFiltered", "Stage filtered") : C("_stageAll", "Stage all"));
    }

    private async Task RefreshDataAsync()
    {
        _status = await _repository.GetStatusAsync(OperationToken);
        _mergePending = File.Exists(await GetGitPathAsync("MERGE_HEAD"));
        IReadOnlyList<KeyValuePair<string, string>> configuration = await _repository.GetConfigAsync("^user\\.(name|email)$", cancellationToken: OperationToken);
        _committer = $"{configuration.LastOrDefault(item => item.Key == "user.name").Value} <{configuration.LastOrDefault(item => item.Key == "user.email").Value}>";
        string? target = (_target.SelectedItem as GitCommit)?.Hash;
        IReadOnlyList<GitCommit> history = _status.IsUnborn ? Array.Empty<GitCommit>() : await _repository.GetLogAsync(100, cancellationToken: OperationToken, all: false);
        _target.ItemsSource = history;
        _headCommit = history.FirstOrDefault();
        _target.SelectedItem = history.FirstOrDefault(commit => commit.Hash == target);
        ApplyFileLists();
        await LoadDiffAsync();
    }

    private void ApplyFileLists()
    {
        if (_status is null) { return; }
        _refreshing = true;
        try
        {
            HashSet<string> unstagedSelection = _unstagedView.SelectedTags.OfType<GitChange>().Select(change => change.Path).ToHashSet(StringComparer.Ordinal);
            HashSet<string> stagedSelection = _stagedView.SelectedTags.OfType<GitChange>().Select(change => change.Path).ToHashSet(StringComparer.Ordinal);
            _unstagedView.SetGroups([new FileStatusListGroup(_unstaged, "unstaged", new TextBlock(),
                _status.Changes.Where(change => (change.IsModified || change.IsConflict) && !change.IsIgnored)
                    .Select(change => new FileStatusListEntry(change, change.Path, change.OriginalPath,
                        change.IsConflict ? 'U' : change.IsUntracked ? 'A' : change.WorkTreeStatus != '.' ? change.WorkTreeStatus : change.IndexStatus,
                        change.SimilarityPercent)).ToArray())]);
            _stagedView.SetGroups([new FileStatusListGroup(_staged, "staged", new TextBlock(),
                _status.Changes.Where(change => change.IsStaged && !change.IsConflict)
                    .Select(change => new FileStatusListEntry(change, change.Path, change.OriginalPath, change.IndexStatus, change.SimilarityPercent)).ToArray())]);
            // SetGroups chooses a first leaf for ordinary browsers. Commit has two
            // lists: preserve empty inactive selections so its first click still
            // changes the active diff from working tree to index (or vice versa).
            _unstagedView.SelectTags(_unstagedView.FilteredEntries.Where(entry => unstagedSelection.Contains(entry.Path)).Select(entry => entry.Tag));
            _stagedView.SelectTags(_stagedView.FilteredEntries.Where(entry => stagedSelection.Contains(entry.Path)).Select(entry => entry.Tag));
            FileStatusListView current = _viewStaged ? _stagedView : _unstagedView;
            if (!current.SelectedTags.OfType<GitChange>().Any())
            {
                FileStatusListEntry? entry = current.FilteredEntries.FirstOrDefault(entry => entry.Path == _path) ?? current.FilteredEntries.FirstOrDefault();
                if (entry is not null) { current.SelectTags([entry.Tag]); }
            }
            if (!current.SelectedTags.OfType<GitChange>().Any())
            {
                _viewStaged = !_viewStaged;
                current = _viewStaged ? _stagedView : _unstagedView;
                if (current.FilteredEntries.FirstOrDefault() is FileStatusListEntry entry) { current.SelectTags([entry.Tag]); }
            }
            _path = SelectedChange(current)?.Path;
        }
        finally { _refreshing = false; }
        UpdateAvailability();
        UpdateStatus();
    }

    private async Task ScheduleSelectionFilterAsync()
    {
        _selectionFilterOperation?.Cancel();
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _selectionFilterOperation = operation;
        string expression = _filter.Text ?? "";
        try
        {
            await Task.Delay(250, operation.Token);
            while (_busy) { await Task.Delay(20, operation.Token); }
            await ApplySelectionFilterAsync(expression);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_selectionFilterOperation, operation)) { _selectionFilterOperation = null; } }
    }

    private async Task ApplySelectionFilterAsync(string expression)
    {
        GitChange[] matches;
        try
        {
            Regex regex = new(expression, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            matches = _unstagedView.FilteredEntries.Select(entry => (GitChange)entry.Tag).Where(change => regex.IsMatch(change.Path)).ToArray();
            ToolTip.SetTip(_filter, C("_selectionFilterToolTip", "Enter a regular expression to select unstaged files."));
        }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        {
            ToolTip.SetTip(_filter, string.Format(C("_selectionFilterErrorToolTip", "Error {0}"), exception.Message));
            return;
        }
        _refreshing = true;
        try
        {
            _unstagedView.SelectTags(matches);
            _stagedView.ClearSelection();
            _viewStaged = false;
            _path = matches.FirstOrDefault()?.Path;
        }
        finally { _refreshing = false; }
        if (matches.Length > 0 && !_selectionFilterHistory.Contains(expression, StringComparer.Ordinal))
        {
            if (_selectionFilterHistory.Count >= 10) { _selectionFilterHistory.RemoveAt(9); }
            _selectionFilterHistory.Insert(0, expression);
            _updatingSelectionFilterHistory = true;
            try { _filter.ItemsSource = _selectionFilterHistory.ToArray(); _filter.Text = expression; }
            finally { _updatingSelectionFilterHistory = false; }
        }
        await RunAsync(T("Diff"), LoadDiffAsync);
    }

    private static GitChange? SelectedChange(FileStatusListView view)
        => view.Tree.SelectedItem is TreeViewItem { Tag: GitChange change } ? change : view.SelectedTags.OfType<GitChange>().FirstOrDefault();

    private Task SelectFileAsync(FileStatusListView view, bool staged)
    {
        GitChange? selected = SelectedChange(view);
        if (selected is null && _viewStaged != staged) { return Task.CompletedTask; }
        _viewStaged = staged;
        _path = selected?.Path;
        if (selected is not null)
        {
            _refreshing = true;
            try { (staged ? _unstagedView : _stagedView).ClearSelection(); }
            finally { _refreshing = false; }
        }
        return RunAsync(T("Diff"), LoadDiffAsync);
    }

    private async Task LoadDiffAsync()
    {
        _content.ClearImage();
        _diff.BeginDocumentLoad();
        _patch = null;
        GitChange? change = _status?.Changes.FirstOrDefault(change => change.Path == _path);
        if (change is null) { _diff.Mode = DiffViewMode.Auto; _source.Text = T("Select a file to view its changes."); _patch = null; return; }
        _diff.DocumentIdentity = string.Join('\0', _repository.Path, change.Path, change.OriginalPath, _status?.Head, _viewStaged ? "index" : "worktree");
        await _diff.ConfigureRepositoryAsync(_repository, OperationToken);
        _diff.Mode = change.IsUntracked ? DiffViewMode.PlainText : DiffViewMode.Auto;
        if (await _content.ShowChangedImageAsync(_repository, change, _viewStaged, OperationToken)) { _patch = null; return; }
        if (change.IsUntracked)
        {
            await using FileStream stream = File.OpenRead(Path.Combine(_repository.Path, change.Path));
            byte[] bytes = new byte[(int)Math.Min(2_000_000, stream.Length)];
            await stream.ReadExactlyAsync(bytes, OperationToken);
            _content.ShowFile(change.Path, bytes, await _repository.GetFileEncodingNameAsync(OperationToken), stream.Length > bytes.Length);
        }
        else
        {
            _diff.ShowDocument(await _repository.GetDiffDocumentAsync(change.Path, _viewStaged,
                options: _viewerOptions, displayOptions: _diff.DisplayOptions, originalPath: change.OriginalPath, cancellationToken: OperationToken));
        }
        _patch = _diff.CanStagePatch ? GitPatch.Parse(_source.Text ?? "").FirstOrDefault() : null;
    }

    private static string[] FilePaths(IEnumerable<GitChange> changes)
        => changes.SelectMany(change => change.OriginalPath is null ? new[] { change.Path } : new[] { change.Path, change.OriginalPath })
            .Distinct(StringComparer.Ordinal).ToArray();
    private Task StageAsync(bool all)
    {
        GitChange[] changes = (all ? _unstagedView.FilteredEntries.Select(entry => entry.Tag) : _unstagedView.SelectedTags).OfType<GitChange>().ToArray();
        return RunAsync(T("Stage"), async () =>
        {
            string[] paths = await StageablePathsAsync(changes);
            if (paths.Length > 0) { await _repository.StageAsync(paths, OperationToken); HasRepositoryChanges = true; }
            if (all) { _unstagedView.ClearFilter(); }
            await RefreshDataAsync();
            if (paths.Length > 0) { RepositoryChanged?.Invoke(this, EventArgs.Empty); }
        });
    }
    private async Task<string[]> StageablePathsAsync(IEnumerable<GitChange> changes)
    {
        HashSet<string> protectedPaths = (await _repository.GetIndexFlagsAsync(OperationToken))
            .Where(flags => flags.IsAssumeUnchanged || flags.IsSkipWorktree).Select(flags => flags.Path).ToHashSet(StringComparer.Ordinal);
        // A staged rename already removed its old name from the index. Stage only
        // current names, keeping literal paths and protected index flags intact.
        return changes.Where(change => !change.IsIgnored && !protectedPaths.Contains(change.Path))
            .Select(change => change.Path).Distinct(StringComparer.Ordinal).ToArray();
    }
    private Task UnstageAsync(bool all)
    {
        string[] paths = FilePaths((all ? _stagedView.FilteredEntries.Select(entry => entry.Tag) : _stagedView.SelectedTags).OfType<GitChange>());
        if (paths.Length == 0) { if (all) { _stagedView.ClearFilter(); } return Task.CompletedTask; }
        return MutateAsync(T("Unstage"), async () =>
        {
            GitResult result = await _repository.UnstageAsync(paths, OperationToken);
            if (all) { _stagedView.ClearFilter(); }
            return result;
        });
    }

    private Task ApplyPatchAsync(bool reverse, bool lines) => MutateAsync(reverse ? T("Unstage") : T("Stage"), async () =>
    {
        if (!_diff.CanStagePatch || _patch is null || _viewStaged != reverse) { throw new InvalidOperationException(T("Select a changed file in the correct list first.")); }
        byte[]? patch;
        if (lines)
        {
            if (_diff.SelectionLength == 0) { throw new InvalidOperationException(T("Select changed lines in the diff first.")); }
            GitChange change = _status!.Changes.Single(change => change.Path == _path);
            patch = GitEncodedPatch.SelectLines(_diff.Document!, _diff.SelectionStart, _diff.SelectionLength, staged: reverse, reverse: reverse, isNewFile: change.IndexStatus == 'A', isRenamed: change.OriginalPath is not null);
        }
        else
        {
            int offset = _patch.Header.Length;
            List<int> indices = [];
            int start = _diff.SelectionLength > 0 ? _diff.SelectionStart : _diff.CaretOffset;
            int end = start + Math.Max(1, _diff.SelectionLength);
            foreach (GitPatchHunk hunk in _patch.Hunks)
            {
                if (start < offset + hunk.Text.Length && end > offset) { indices.Add(hunk.Index); }
                offset += hunk.Text.Length;
            }
            if (indices.Count == 0) { throw new InvalidOperationException(T("Place the cursor in a diff hunk first.")); }
            patch = GitEncodedPatch.SelectHunks(_diff.Document!, indices);
        }
        if (patch is null) { throw new InvalidOperationException(T("The selected text contains no changed lines.")); }
        return await _repository.ApplyPatchAsync(patch, staged: true, reverse: reverse, cancellationToken: OperationToken);
    });

    private async Task CommitAsync(bool push)
    {
        if (_busy || _commitRequested || _confirmationOpen || _status is null) { return; }
        _commitRequested = true;
        try
        {
            // Git can start a merge or update the index while this dialog stays open.
            // Confirm the current operation state while preserving the edited message.
            await RunAsync(T("Refresh"), RefreshDataAsync);
            if (LastError is not null || !IsVisible || _lifetime.IsCancellationRequested) { return; }
            CommitEmptyChoice choice = CommitEmptyChoice.MakeEmpty;
            bool noStaged = !_status.Changes.Any(change => change.IsStaged);
            GitChange[] unstagedChanges = _unstagedView.FilteredEntries.Select(entry => entry.Tag).OfType<GitChange>().ToArray();
            _confirmationOpen = true;
            try
            {
                if (_amend.IsChecked == true)
                {
                    if (!await Dialogs.ConfirmAsync(this, C("_amendCommitCaption", "Amend commit"), C("_amendCommit", "You are about to rewrite history.\nOnly use Amend if the commit has not been published yet!\n\nDo you want to continue?"))) { return; }
                }
                else if (noStaged)
                {
                    if (_mergePending)
                    {
                        if (!await Dialogs.YesNoAsync(this, C("_noStagedChanges", "There are no staged changes"), C("_noFilesStagedAndConfirmAnEmptyMergeCommit", "There are no files staged for this commit.\nAre you sure you want to commit?"))) { return; }
                    }
                    else
                    {
                        CommitEmptyDialog dialog = new(unstagedChanges.Length > 0, filtered: !string.IsNullOrEmpty(_unstagedView.FilterText));
                        choice = await dialog.ShowDialog<CommitEmptyChoice>(this);
                        if (choice == CommitEmptyChoice.Cancel) { return; }
                    }
                }
            }
            finally { _confirmationOpen = false; }
            if (!IsVisible || _lifetime.IsCancellationRequested) { return; }
            await RunAsync(C("Commit", "Commit"), async () =>
            {
                string rawMessage = _message.Text ?? "";
                string message = CommitDraftStore.FormatMessage(rawMessage, !string.IsNullOrEmpty(_loadedTemplate), _preferences.EnsureCommitMessageSecondLineEmpty);
                if (_kind.SelectedIndex != 1)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(message);
                    if (message.Contains('\0')) { throw new ArgumentException("Commit messages cannot contain NUL."); }
                    Encoding encoding = await _repository.GetCommitMessageEncodingAsync(OperationToken);
                    _ = encoding.GetBytes(message);
                }
                _drafts = await CommitDraftStore.OpenAsync(_repository, OperationToken);
                if (noStaged && choice == CommitEmptyChoice.StageUnstaged)
                {
                    string[] paths = await StageablePathsAsync(unstagedChanges);
                    if (paths.Length == 0) { throw new InvalidOperationException(C("_noStagedChanges", "There are no staged changes")); }
                    await _repository.StageAsync(paths, OperationToken);
                    _unstagedView.ClearFilter();
                    HasRepositoryChanges = true;
                    await RefreshDataAsync();
                    RepositoryChanged?.Invoke(this, EventArgs.Empty);
                    if (!_status!.Changes.Any(change => change.IsStaged)) { throw new InvalidOperationException(C("_noStagedChanges", "There are no staged changes")); }
                    noStaged = false;
                }
                if (_kind.SelectedIndex != 1)
                {
                    _preferences = _preferencesStore.Load() with { LastCommitMessage = rawMessage };
                    _preferencesStore.Save(_preferences);
                    _drafts?.WriteForCommit(message, merge: false);
                }
                GitCommit? target = _target.SelectedItem as GitCommit;
                GitCommitOptions options = new(
                    Message: _kind.SelectedIndex == 1 ? null : message,
                    Amend: _amend.IsChecked == true,
                    SignOff: _signOff.IsChecked,
                    Sign: _sign.SelectedIndex == 0 ? null : _sign.SelectedIndex > 1,
                    SigningKey: _sign.SelectedIndex == 3 ? EmptyToNull(_signingKey.Text) : null,
                    FixupRevision: _kind.SelectedIndex == 1 ? target?.Hash : null,
                    SquashRevision: _kind.SelectedIndex == 2 ? target?.Hash : null,
                    AllowEmpty: noStaged,
                    ResetAuthor: _amend.IsChecked == true && _resetAuthor.IsChecked == true,
                    Author: EmptyToNull(_author.Text),
                    NoVerify: _noVerify.IsChecked);
                GitResult result = await _repository.CommitAsync(options, OperationToken);
                HasCommitted = true;
                HasRepositoryChanges = true;
                _output.Text = result.StandardOutput + result.StandardError;
                _output.IsVisible = false;
                _message.Text = "";
                _amend.IsChecked = false;
                _noVerify.IsChecked = false;
                _kind.SelectedIndex = 0;
                _drafts?.Reset();
                _draftReset = true;
                await RefreshDataAsync();
                RepositoryChanged?.Invoke(this, EventArgs.Empty);
                if (push) { await _repository.PushAsync(cancellationToken: OperationToken); }
                if (_closeAfterCommit.IsChecked || (_closeWhenClean.IsChecked && _status!.Changes.Count == 0)) { _closeWhenIdle = true; }
            });
        }
        finally { _commitRequested = false; UpdateAvailability(); }
    }

    private Task LoadTemplateAsync() => RunAsync(T("Commit templates"), async () =>
    {
        string? template = await _repository.GetCommitTemplateAsync(cancellationToken: OperationToken);
        if (template is null) { throw new InvalidOperationException(T("No commit template is configured.")); }
        _loadedTemplate = template;
        _message.Text = template;
    });
    private async Task LoadHistoryAsync()
    {
        if (_busy) { return; }
        _historyOperation?.Cancel();
        CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _historyOperation = operation;
        _historyMenu.ItemsSource = new MenuItem[] { new() { Name = "HistoryLoading", Header = T("Loading…"), IsEnabled = false }, _onlyMyMessages };
        try
        {
            CommitPreferences preferences = _preferencesStore.Load();
            List<string> messages = [.. await _repository.GetPreviousCommitMessagesAsync(preferences.NumberOfPreviousMessages, _onlyMyMessages.IsChecked, operation.Token)];
            if (!string.IsNullOrWhiteSpace(preferences.LastCommitMessage) && !messages.Contains(preferences.LastCommitMessage))
            {
                if (messages.Count == preferences.NumberOfPreviousMessages) { messages.RemoveAt(messages.Count - 1); }
                messages.Insert(0, preferences.LastCommitMessage);
            }
            operation.Token.ThrowIfCancellationRequested();
            List<MenuItem> items = [];
            foreach (string message in messages)
            {
                string label = message.Split('\n')[0];
                if (label.Length > 72) { label = label[..69] + "..."; }
                MenuItem item = new() { Name = "PreviousMessage", Header = label, Tag = message };
                ToolTip.SetTip(item, message);
                item.Click += (_, _) => { if (!_busy) { _message.SelectAll(); _message.SelectedText = message.Trim(); _message.Focus(); } };
                items.Add(item);
            }
            items.Add(new MenuItem { Header = "-" });
            items.Add(_onlyMyMessages);
            _historyMenu.ItemsSource = items;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ShowFailure(T("Commit message"), exception); _historyMenu.ItemsSource = new[] { _onlyMyMessages }; }
        finally { if (ReferenceEquals(_historyOperation, operation)) { _historyOperation = null; } operation.Dispose(); }
    }

    private void SavePreferences()
    {
        try
        {
            _preferences = _preferencesStore.Load() with
            {
                CloseAfterEachCommit = _closeAfterCommit.IsChecked,
                CloseWhenAllChangesCommitted = _closeWhenClean.IsChecked,
                RefreshOnFormFocus = _refreshOnFocus.IsChecked,
                SelectStagedOnMessageFocus = _selectStaged.IsChecked,
                ShowOnlyMyMessages = _onlyMyMessages.IsChecked
            };
            _preferencesStore.Save(_preferences);
        }
        catch (Exception exception) { ShowFailure(T("Options"), exception); }
    }
    private async Task ResetSoftAsync()
    {
        if (!await Dialogs.ConfirmAsync(this, C("ResetSoft", "Reset soft"), C("_amendResetSoft", "You are about to rewrite history by Soft Reset to the previous commit.\nOnly use Amend / Reset if the commit has not been published yet!\n\nDo you want to continue?"))) { return; }
        await MutateAsync(C("ResetSoft", "Reset soft"), () => _repository.ResetAsync("HEAD^", GitResetMode.Soft, OperationToken));
    }
    private Task StashStagedAsync() => MutateAsync(C("StashStaged", "Stash staged changes"), () => _repository.RunAsync(["stash", "push", "--staged"], OperationToken));
    private async Task ResetChangesAsync(bool all)
    {
        string title = all ? C("btnResetAllChanges", "Reset all changes") : C("btnResetUnstagedChanges", "Reset unstaged changes");
        if (!await Dialogs.ConfirmAsync(this, title, T("Discard tracked changes? Untracked files are preserved."))) { return; }
        await MutateAsync(title, () => _repository.RunAsync(all ? ["restore", "--staged", "--worktree", "--source=HEAD", "--", "."] : ["restore", "--worktree", "--", "."], OperationToken));
    }

    private Task MutateAsync(string label, Func<Task<GitResult>> action) => RunAsync(label, async () =>
    {
        await action();
        HasRepositoryChanges = true;
        await RefreshDataAsync();
        RepositoryChanged?.Invoke(this, EventArgs.Empty);
    });
    private async Task RunAsync(string label, Func<Task> action)
    {
        if (_busy) { return; }
        _busy = true;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        LastError = null;
        _output.IsVisible = false;
        _state.Text = label + "…";
        UpdateAvailability();
        try { await action(); }
        catch (OperationCanceledException) { _state.Text = T("Operation cancelled"); }
        catch (Exception exception) { ShowFailure(label, exception); }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _busy = false;
            UpdateAvailability();
            if (LastError is null) { UpdateStatus(); }
            if (_closeWhenIdle) { Close(); }
            else if (_pendingDiffAppearance && !_lifetime.IsCancellationRequested) { await ReloadDiffAppearanceAsync(); }
        }
    }

    private Task ReloadDiffAppearanceAsync()
    {
        if (!_pendingDiffAppearance || _busy || _lifetime.IsCancellationRequested) { return Task.CompletedTask; }
        _pendingDiffAppearance = false;
        return RunAsync(T("Diff"), LoadDiffAsync);
    }

    private void ShowFailure(string label, Exception exception)
    {
        LastError = exception is GitCommandException command
            ? !string.IsNullOrWhiteSpace(command.Result.StandardError) ? command.Result.StandardError
                : !string.IsNullOrWhiteSpace(command.Result.StandardOutput) ? command.Result.StandardOutput : exception.Message
            : exception.Message;
        _output.Text = LastError;
        _output.IsVisible = true;
        _state.Text = label + ": " + LastError.Trim();
    }

    private void UpdateAvailability()
    {
        foreach (Control control in _interactive) { control.IsEnabled = !_busy && !_commitRequested; }
        _diff.ComparisonMenu.SetHostBusy(_busy || _commitRequested);
        if (_busy || _commitRequested) { return; }
        _stage.IsEnabled = _unstagedView.SelectedTags.OfType<GitChange>().Any();
        _unstage.IsEnabled = _stagedView.SelectedTags.OfType<GitChange>().Any();
        _target.IsEnabled = _kind.SelectedIndex != 0;
        _signingKey.IsEnabled = _sign.SelectedIndex == 3;
        _signingKey.IsVisible = _sign.SelectedIndex == 3;
        if (_signingKeyMenu is not null) { _signingKeyMenu.IsVisible = _sign.SelectedIndex == 3; }
        _message.IsEnabled = _kind.SelectedIndex != 1;
        _amend.IsEnabled = _kind.SelectedIndex == 0 && _status is not null && !_status.IsUnborn;
        if (!_amend.IsEnabled && _amend.IsChecked == true) { _amend.IsChecked = false; }
        _resetAuthor.IsEnabled = _amend.IsChecked == true && string.IsNullOrWhiteSpace(_author.Text);
        if (!_resetAuthor.IsEnabled) { _resetAuthor.IsChecked = false; }
        Button? soft = _interactive.OfType<Button>().FirstOrDefault(button => button.Name == "ResetSoft");
        if (soft is not null) { soft.IsVisible = _amend.IsChecked == true; soft.IsEnabled = _headCommit?.Parents.Count > 0; }
        bool targetValid = _kind.SelectedIndex == 0 || _target.SelectedItem is GitCommit;
        string formatted = CommitDraftStore.FormatMessage(_message.Text ?? "", !string.IsNullOrEmpty(_loadedTemplate), _preferences.EnsureCommitMessageSecondLineEmpty);
        bool messageValid = _kind.SelectedIndex == 1 || (!string.IsNullOrWhiteSpace(formatted) && !formatted.Contains('\0') && _message.Text != _loadedTemplate);
        bool committable = _status is not null && !_status.Changes.Any(change => change.IsConflict);
        _commit.IsEnabled = _push.IsEnabled = targetValid && messageValid && committable;
        _stageHunk.IsEnabled = !_viewStaged && _diff.CanStagePatch && _patch?.Hunks.Count > 0;
        _unstageHunk.IsEnabled = _viewStaged && _diff.CanStagePatch && _patch?.Hunks.Count > 0;
        _stageLines.IsEnabled = _stageHunk.IsEnabled && _diff.SelectionLength > 0;
        _unstageLines.IsEnabled = _unstageHunk.IsEnabled && _diff.SelectionLength > 0;
    }
    private void UpdateStatus()
    {
        if (_busy || LastError is not null) { return; }
        string text = _message.Text ?? "";
        int caret = Math.Clamp(_message.CaretIndex, 0, text.Length);
        int line = text[..caret].Count(character => character == '\n') + 1;
        int column = caret - text[..caret].LastIndexOf('\n');
        _state.Text = $"{_committer}    ·    {_status?.Branch ?? ""}    ·    {C("commitStagedCountLabel", "Staged")}: {_status?.Changes.Count(change => change.IsStaged && !change.IsConflict) ?? 0}    ·    {C("commitCursorLineLabel", "Ln")} {line}    {C("commitCursorColumnLabel", "Col")} {column}";
    }
    private async void HandleShortcut(object? sender, KeyEventArgs args)
    {
        if (args.Handled) { return; }
        if (args.Key == Key.Escape) { Close(); args.Handled = true; return; }
        if (_busy || _commitRequested) { return; }
        if (_diff.IsKeyboardFocusWithin && (_diff.HandleReadingShortcut(args)
            || _diff.AppearanceMenu.HandleKeyDown(args))) { return; }
        if (args.KeyModifiers == KeyModifiers.Control)
        {
            switch (args.Key)
            {
                case Key.D1: _unstaged.Focus(); break;
                case Key.D2: _diff.Editor.TextArea.Focus(); break;
                case Key.D3: _staged.Focus(); break;
                case Key.D4: _message.Focus(); break;
                case Key.F:
                    if (_selectionFilterToolbar is null) { return; }
                    _selectionFilterToolbar.IsVisible = !_selectionFilterToolbar.IsVisible;
                    if (_selectionFilterToolbar.IsVisible) { _filter.Focus(); }
                    else { _unstaged.Focus(); }
                    break;
                case Key.S: args.Handled = true; await StageAsync(true); break;
                case Key.Enter:
                    if (!_commit.IsEnabled) { return; }
                    args.Handled = true;
                    await CommitAsync(false);
                    break;
                default: return;
            }
            args.Handled = true;
        }
        else if (args.Key == Key.F5) { args.Handled = true; await RefreshAsync(); }
    }

    private static string? EmptyToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
    private async Task<string> GetGitPathAsync(string name)
    {
        GitResult result = await _repository.RunAsync(["rev-parse", "--git-path", name], OperationToken);
        return Path.GetFullPath(result.StandardOutput.TrimEnd('\r', '\n'), _repository.Path);
    }
    private MenuItem Toggle(string item, string text, bool value = false) => new() { Name = item, Header = C(item, text), ToggleType = MenuItemToggleType.CheckBox, IsChecked = value, StaysOpenOnClick = true };
    private MenuItem MenuAction(string name, string text, Func<Task> action)
    {
        MenuItem item = new() { Name = name, Header = text };
        item.Click += async (_, _) => { if (!_busy) { await action(); } };
        return item;
    }
    private static MenuItem InputMenu(string text, Control input) => new() { Header = new StackPanel { Spacing = 3, Children = { new TextBlock { Text = text }, input } }, StaysOpenOnClick = true };
    private Button Action(string name, string item, string text, string icon, Func<Task> action, bool showText = true)
    {
        string label = item.Length == 0 ? T(text) : C(item, text);
        Button button = new() { Name = name, Height = 26, Content = IconLabel(showText ? label : "", icon), HorizontalContentAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(button, label);
        button.Click += async (_, _) => { if (!_busy) { await action(); } };
        _interactive.Add(button);
        return button;
    }
    private static Control IconLabel(string label, string name)
    {
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Uri uri = new($"avares://GitExtensions/Assets/Icons/{name}");
        if (AssetLoader.Exists(uri)) { using Stream stream = AssetLoader.Open(uri); content.Children.Add(new Image { Source = new Bitmap(stream), Width = 16, Height = 16 }); }
        if (label.Length > 0)
        {
            TextBlock text = new() { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            content.Children.Add(text);
        }
        return content;
    }
    private static WrapPanel Row(params Control[] controls)
    {
        WrapPanel row = new() { Orientation = Orientation.Horizontal, ItemSpacing = 3, LineSpacing = 2, Margin = new Thickness(0, 0, 0, 2) };
        foreach (Control control in controls) { row.Children.Add(control); }
        return row;
    }
    private DockPanel Pane(Control body, Control toolbar, string? title = null)
    {
        DockPanel pane = new() { LastChildFill = true };
        DockPanel.SetDock(toolbar, Dock.Top);
        pane.Children.Add(toolbar);
        if (title is not null)
        {
            TextBlock heading = new() { Text = title, Padding = new Thickness(3, 2) };
            heading.Bind(TextBlock.BackgroundProperty, new DynamicResourceExtension("ClassicMenuBrush"));
            DockPanel.SetDock(heading, Dock.Top);
            pane.Children.Add(heading);
        }
        pane.Children.Add(body);
        return pane;
    }
    private static GridSplitter Splitter(int position, bool rows)
    {
        GridSplitter splitter = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = rows ? GridResizeDirection.Rows : GridResizeDirection.Columns };
        if (rows) { splitter.Height = 5; Grid.SetRow(splitter, position); }
        else { splitter.Width = 5; Grid.SetColumn(splitter, position); }
        return splitter;
    }
}

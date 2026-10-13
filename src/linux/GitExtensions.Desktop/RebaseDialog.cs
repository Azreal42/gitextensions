using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

/// <summary>FormRebase's options and recovery actions, backed by Git's native rebase state and generated todo.</summary>
public sealed class RebaseDialog : RepositoryDialog
{
    private readonly CancellationToken _token;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<Window, GitRepository, bool, CancellationToken, Task>? _resolveConflictsAsync;
    private readonly RebasePreferencesStore _preferences = new();
    private readonly string? _initialOnto, _initialFrom, _initialTo;
    private readonly TextBlock _current = new() { Name = "RebaseCurrentBranch" };
    private readonly TextBlock _currentCommit = new() { Name = "RebaseCurrentCommit", IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _onto = Choice("RebaseOn", 270), _to = Choice("RebaseRangeTo", 184);
    private readonly TextBox _from = new() { Name = "RebaseRangeFrom", Width = 100, Height = 24, MinHeight = 24 };
    private readonly CheckBox _interactive = Toggle("RebaseInteractive"), _merges = Toggle("RebasePreserveMerges"),
        _autosquash = Toggle("RebaseAutosquash"), _autostash = Toggle("RebaseAutoStash"), _ignoreDate = Toggle("RebaseIgnoreDate"),
        _authorDate = Toggle("RebaseCommitterDateIsAuthorDate"), _updateRefs = Toggle("RebaseUpdateRefs"), _range = Toggle("RebaseSpecificRange");
    private readonly ListBox _patches = new() { Name = "RebasePatches", MinHeight = 100, SelectionMode = SelectionMode.Single, Focusable = true };
    private readonly ListBox _conflicts = new() { Name = "RebaseConflictFiles", MaxHeight = 70, IsVisible = false };
    private readonly StackPanel _options = new() { Name = "RebaseOptions", Spacing = 3 };
    private readonly Button _execute, _continue, _skip, _abort, _resolve, _addFiles, _commit, _editTodo, _stash, _chooseFrom, _cancel, _preserveJournal;
    private readonly TextBlock _conflictNotice = new() { Name = "RebaseConflictNotice", IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _encodingNotice = new() { Name = "RebaseEncodingNotice", IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _journalNotice = new() { Name = "RebaseJournalNotice", IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer _journalDetails = new() { Name = "RebaseJournalDetails", IsVisible = false, MaxHeight = 74,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private GitRebaseState? _state;
    private GitRebaseCapabilities? _capabilities;
    private bool _loaded, _closingAccepted, _dirty;
    public GitRebaseState? State => _state;
    public bool Succeeded { get; private set; }
    public bool Aborted { get; private set; }
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    internal string EditorExecutable { get; set; } = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the rebase editor application.");

    public RebaseDialog(GitRepository repository, string? initialOnto = null, string? initialFrom = null, string? initialTo = null,
        bool interactiveInitial = false, Func<Window, GitRepository, bool, CancellationToken, Task>? resolveConflictsAsync = null,
        CancellationToken cancellationToken = default) : base(repository, "Rebase")
    {
        Name = "RebaseDialog"; Title = Text("$this", "Rebase"); Width = 1050; Height = 500; MinWidth = 900; MinHeight = 460;
        _initialOnto = initialOnto; _initialFrom = initialFrom; _initialTo = initialTo; _token = cancellationToken;
        _resolveConflictsAsync = resolveConflictsAsync; _interactive.IsChecked = interactiveInitial;
        _autostash.IsChecked = _preferences.Load().AutoStash;
        Caption(_interactive, "chkInteractive", "&Interactive Rebase"); Caption(_merges, "chkPreserveMerges", "&Preserve Merges");
        Caption(_autosquash, "chkAutosquash", "Autos&quash"); Caption(_autostash, "chkStash", "A&uto stash");
        Caption(_ignoreDate, "chkIgnoreDate", "Ignore &date"); Caption(_authorDate, "chkCommitterDateIsAuthorDate", "Co&mmitter date is author date");
        Caption(_updateRefs, "checkBoxUpdateRefs", "Update dependent r&efs"); Caption(_range, "chkSpecificRange", "Specific ra&nge");
        ToolTip.SetTip(_ignoreDate, Text("chkIgnoreDate", "Sets the author date to the current date (same as\r\ncommit date), ignoring the original author date.", "ToolTip"));
        ToolTip.SetTip(_authorDate, Text("chkCommitterDateIsAuthorDate", "Sets the commit date to the original author date\r\n(instead of the current date).", "ToolTip"));
        _execute = Button("ExecuteRebase", "btnRebase", "Rebase", ExecuteRebaseAsync);
        _continue = Button("ContinueRebase", "btnContinueRebase", "&Continue rebase", () => RecoverAsync(GitOperationAction.Continue));
        _skip = Button("SkipRebase", "btnSkip", "S&kip currently applying commit", () => RecoverAsync(GitOperationAction.Skip));
        _abort = Button("AbortRebase", "btnAbort", "A&bort", () => RecoverAsync(GitOperationAction.Abort));
        _resolve = Button("ResolveRebaseConflicts", "btnSolveConflicts", "&Solve conflicts", ResolveAsync);
        _addFiles = Button("AddRebaseFiles", "btnAddFiles", "&Add files", AddFilesAsync);
        _commit = Button("CommitRebase", "btnCommit", "C&ommit...", CommitAsync);
        _editTodo = Button("EditRebaseTodo", "btnEditTodo", "&Edit todo...", EditTodoAsync);
        _preserveJournal = Action("PreserveRebaseJournal", "Preserve journal", PreserveJournalAsync);
        _preserveJournal.Height = 25;
        _stash = Action("StashBeforeRebase", "Stash", StashAsync);
        _chooseFrom = Action("ChooseRebaseFrom", "…", ChooseFromAsync); _chooseFrom.Width = 25; _chooseFrom.Height = 24;
        StackPanel current = new() { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        { new TextBlock { Text = Text("lblCurrent", "Current branch:") }, _current } };
        Grid branch = new() { ColumnDefinitions = new("90,270,*,Auto") };
        Label ontoLabel = new() { Content = Caption("label2", "&Rebase on"), Target = _onto, VerticalAlignment = VerticalAlignment.Center };
        branch.Children.Add(ontoLabel); Grid.SetColumn(_onto, 1); branch.Children.Add(_onto); Grid.SetColumn(_stash, 3); branch.Children.Add(_stash);
        WrapPanel flags = new() { Orientation = Orientation.Horizontal };
        foreach (CheckBox option in new[] { _interactive, _merges, _autosquash, _autostash, _ignoreDate, _authorDate, _updateRefs })
        { option.Margin = new Thickness(0, 0, 10, 2); flags.Children.Add(option); }
        Grid range = new() { ColumnDefinitions = new("110,Auto,100,25,Auto,*") };
        range.Children.Add(_range); Label fromLabel = new() { Content = Caption("lblRangeFrom", "&From (exc.)"), Target = _from, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(fromLabel, 1); range.Children.Add(fromLabel); Grid.SetColumn(_from, 2); range.Children.Add(_from); Grid.SetColumn(_chooseFrom, 3); range.Children.Add(_chooseFrom);
        Label toLabel = new() { Content = Caption("lblRangeTo", "&To"), Target = _to, Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(toLabel, 4); range.Children.Add(toLabel); Grid.SetColumn(_to, 5); range.Children.Add(_to);
        _options.Children.Add(flags); _options.Children.Add(range);
        StackPanel top = new() { Spacing = 7, Children = { current,
            new TextBlock { Text = Text("lblRebase", "Rebase current branch on top of another branch") }, branch, _options, _currentCommit,
            new TextBlock { Text = Text("lblCommitsToReapply", "Commits to re-apply:") } } };
        DockPanel controls = new() { LastChildFill = true, Margin = new Thickness(8) };
        DockPanel.SetDock(top, Dock.Top); controls.Children.Add(top);
        _conflictNotice.Text = Text("btnSolveMergeconflicts", "There are unresolved merge conflicts\r\n").TrimEnd();
        _cancel = new() { Name = "CancelRebase", Content = T("Cancel"), IsCancel = true, MinWidth = 75, Height = 25 };
        _cancel.Click += (_, _) => Close();
        _journalDetails.Content = new StackPanel { Spacing = 3, Children = { _encodingNotice, _journalNotice } };
        StackPanel actions = new() { Spacing = 3, Margin = new Thickness(0, 4, 0, 0), Children =
        {
            _journalDetails, _conflictNotice, _conflicts,
            new WrapPanel { Orientation = Orientation.Horizontal, Children = { _preserveJournal, _addFiles, _commit, _editTodo, _skip } },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6,
                Children = { _resolve, _continue, _abort, _execute, _cancel } }
        } };
        foreach (Button action in new[] { _addFiles, _commit, _editTodo, _skip }) { action.Margin = new Thickness(0, 0, 6, 0); }
        DockPanel.SetDock(actions, Dock.Bottom); controls.Children.Add(actions);
        controls.Children.Add(CreatePatchGrid());
        OriginalCommandHelp help = new("HelpCommandRebase", settingsId: "Rebase", hoverNotice: Text("PanelLeftImage", "Hover to see scenario when fast forward is possible.", "IsOnHoverShowImage2NoticeText"));
        Grid body = new() { ColumnDefinitions = new("Auto,*") }; body.Children.Add(help); Grid.SetColumn(controls, 1); body.Children.Add(controls); Root.Children.Add(body);
        help.ExpandedChanged += (_, _) => MinWidth = help.IsExpanded ? 900 : 620;
        Track(_onto, _to, _from, _interactive, _merges, _autosquash, _autostash, _ignoreDate, _authorDate, _updateRefs, _range, _patches);
        foreach (CheckBox flag in new[] { _interactive, _ignoreDate, _authorDate, _range }) { flag.IsCheckedChanged += (_, _) => UpdateActions(); }
        _patches.DoubleTapped += async (_, args) => { args.Handled = true; await ShowPatchAsync(); };
        _patches.AddHandler(KeyDownEvent, async (_, args) =>
        { if (args.Key == Key.Enter) { args.Handled = true; await ShowPatchAsync(); } }, RoutingStrategies.Tunnel);
        Closed += (_, _) => _lifetime.Cancel(); Opened += (_, _) => FocusAction(); UpdateActions();
    }

    private static ComboBox Choice(string name, double width) => new() { Name = name, IsEditable = true, IsTextSearchEnabled = false,
        Width = width, Height = 24, MinHeight = 24, Padding = new Thickness(4, 1), HorizontalAlignment = HorizontalAlignment.Left };
    private static CheckBox Toggle(string name) { CheckBox result = new() { Name = name }; ReferenceDialogControls.Compact(result); return result; }
    private string Text(string item, string source, string property = "Text") => Translations.Translate("FormRebase", item, property, source);
    private AccessText Caption(string item, string source) => new() { Text = Translations.TranslateAccessKeys("FormRebase", item, "Text", source) };
    private void Caption(CheckBox input, string item, string source) => input.Content = Caption(item, source);
    private Button Button(string name, string item, string source, Func<Task> action)
    { Button button = Action(name, source.Replace("&", "", StringComparison.Ordinal), action); button.Content = Caption(item, source); button.Height = 25; button.MinWidth = 75; return button; }

    private Control CreatePatchGrid()
    {
        DockPanel panel = new() { LastChildFill = true };
        Grid header = PatchColumns(); header.Height = 24;
        string[] names = ["Action", "CommitHash", "subjectDataGridViewTextBoxColumn", "authorDataGridViewTextBoxColumn", "dateDataGridViewTextBoxColumn", "Status"];
        string[] labels = ["Action", "Commit hash", "Subject", "Author", "Date", "Status"];
        for (int column = 0; column < labels.Length; column++)
        {
            TextBlock heading = new() { Name = "RebaseHeader" + column, Text = Translations.Translate("PatchGrid", names[column], "HeaderText", labels[column]), Margin = new Thickness(4, 2), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(heading, column); header.Children.Add(heading);
        }
        _patches.ItemTemplate = new FuncDataTemplate<GitRebasePatch>((patch, _) =>
        {
            Grid row = PatchColumns(); row.Height = 24;
            string hash = patch?.CommitHash ?? "";
            string[] values = [patch?.Action ?? "", hash[..Math.Min(hash.Length, 9)], patch?.Subject ?? patch?.RawLine ?? "", patch?.Author ?? "",
                patch?.CommitDate?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "", patch is null || patch.Status == GitRebaseStepStatus.Pending ? "" : T(patch.Status.ToString())];
            for (int column = 0; column < values.Length; column++)
            { TextBlock text = new() { Text = values[column], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0), TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(text, column); row.Children.Add(text); }
            ToolTip.SetTip(row, patch?.Body.Length > 0 ? patch.Body : patch?.RawLine); return row;
        });
        Border headings = ClassicChrome.Border(header, new Thickness(0, 0, 0, 1));
        DockPanel.SetDock(headings, Dock.Top); panel.Children.Add(headings); panel.Children.Add(_patches);
        return ClassicChrome.Border(panel, new Thickness(1));
    }
    private static Grid PatchColumns() => new() { ColumnDefinitions = new("65,70,*,105,110,65") };

    public override Task InitializeAsync() => RunAsync("Rebase", async token =>
    {
        if ((await Repository.GetInfoAsync(token)).IsBare) { throw new InvalidOperationException(T("Rebase requires a working tree.")); }
        _capabilities = await Repository.GetRebaseCapabilitiesAsync(token);
        IReadOnlyList<GitReference> refs = await Repository.GetReferencesAsync(token);
        _onto.ItemsSource = refs.Where(reference => reference.IsBranch || reference.IsRemote || reference.IsTag).Select(reference => reference.ShortName).ToArray();
        _to.ItemsSource = refs.Where(reference => reference.IsBranch).Select(reference => reference.ShortName).ToArray();
        if (!_loaded)
        {
            GitStatus status = await Repository.GetStatusAsync(token);
            _onto.SetCurrentValue(ComboBox.TextProperty, _initialOnto ?? ""); _from.Text = _initialFrom ?? "";
            _to.SetCurrentValue(ComboBox.TextProperty, _initialTo ?? (status.IsDetached ? "HEAD" : status.Branch) ?? "HEAD");
            _range.IsChecked = !string.IsNullOrEmpty(_initialFrom); _autosquash.IsChecked = _capabilities.AutoSquash; _updateRefs.IsChecked = _capabilities.UpdateRefs;
            _loaded = true;
        }
        await ReloadAsync(token);
    }, cancellationToken: _token);

    private async Task ReloadAsync(CancellationToken token)
    {
        _state = await Repository.GetRebaseStateAsync(token);
        GitStatus status = await Repository.GetStatusAsync(token); _dirty = status.Changes.Count != 0;
        string? headName = _state.HeadName;
        _current.Text = _state.IsActive ? (headName?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? headName[11..] : headName ?? status.Branch ?? "HEAD") : status.Branch ?? "HEAD";
        if (_state.Options is GitRebaseOptions options)
        {
            _onto.SetCurrentValue(ComboBox.TextProperty, options.OnTo ?? options.BranchName); _from.Text = options.From ?? "";
            if (options.OnTo is not null) { _to.SetCurrentValue(ComboBox.TextProperty, options.BranchName); }
            _range.IsChecked = options.From is not null; _interactive.IsChecked = options.Interactive; _merges.IsChecked = options.PreserveMerges;
            _autosquash.IsChecked = options.AutoSquash; _autostash.IsChecked = options.AutoStash; _ignoreDate.IsChecked = options.IgnoreDate;
            _authorDate.IsChecked = options.CommitterDateIsAuthorDate; _updateRefs.IsChecked = options.UpdateRefs ?? _capabilities?.UpdateRefs;
        }
        _patches.ItemsSource = _state.Steps; _patches.SelectedItem = _state.Steps.FirstOrDefault(patch => patch.Status == GitRebaseStepStatus.Applying) ?? _state.Steps.FirstOrDefault();
        _currentCommit.IsVisible = _state.IsActive && _state.Steps.Count == 0 && _state.CurrentCommit is not null;
        _currentCommit.Text = _currentCommit.IsVisible ? T("Currently applying:") + " " + _state.CurrentCommit : null;
        _conflicts.ItemsSource = _state.Conflicts.Select(conflict => conflict.Path).ToArray();
        _encodingNotice.IsVisible = _state.EncodingError is not null;
        _encodingNotice.Text = _state.EncodingError is not null
            ? T("The rebase text encoding is unavailable. Continue is unavailable; Abort is available.") : null;
        ToolTip.SetTip(_encodingNotice, _state.EncodingError);
        _journalNotice.IsVisible = _state.JournalError is not null || Warnings.Count != 0;
        _journalDetails.IsVisible = _encodingNotice.IsVisible || _journalNotice.IsVisible;
        _journalNotice.Text = _state.JournalError is not null
            ? T(_state.IsActive
                ? "The rebase journal cannot be read. Saved options are unavailable. Continue is unavailable; Abort is available."
                : "The unfinished journal has been kept. Preserve it to start a new rebase.")
            : string.Join(Environment.NewLine, Warnings);
        ToolTip.SetTip(_journalNotice, _state.JournalError ?? string.Join(Environment.NewLine, Warnings));
        Status.Text = _state.IsActive ? T(_state.HasConflicts ? "There are unresolved merge conflicts" : "Continue rebase") : Text("lblRebase", "Rebase current branch on top of another branch");
        UpdateActions();
    }

    public GitRebaseOptions ReadOptions()
    {
        string onto = _onto.Text?.Trim() ?? "";
        if (onto.Length == 0) { throw new ArgumentException(Text("_branchSelectionNeeded", "Please select a branch")); }
        bool range = _range.IsChecked == true && !string.IsNullOrWhiteSpace(_from.Text) && !string.IsNullOrWhiteSpace(_to.Text);
        return new() { BranchName = range ? _to.Text!.Trim() : onto, From = range ? _from.Text!.Trim() : null, OnTo = range ? onto : null,
            Interactive = _interactive.IsChecked == true, PreserveMerges = _merges.IsChecked == true, AutoSquash = _autosquash.IsChecked == true,
            AutoStash = _autostash.IsChecked == true, IgnoreDate = _ignoreDate.IsChecked == true, CommitterDateIsAuthorDate = _authorDate.IsChecked == true,
            UpdateRefs = _capabilities?.SupportUpdateRefs == true && _updateRefs.IsChecked != _capabilities.UpdateRefs ? _updateRefs.IsChecked == true : null };
    }

    public Task ExecuteRebaseAsync()
    {
        if (_state?.IsActive == true) { return Task.CompletedTask; }
        GitRebaseOptions options = ReadOptions(); _preferences.Save(_preferences.Load() with { AutoStash = options.AutoStash });
        return MutateAsync("Rebase", token => Repository.RebaseAsync(options, EditorExecutable, token));
    }
    private Task RecoverAsync(GitOperationAction action) => MutateAsync(action.ToString(), token => action switch
    { GitOperationAction.Continue => Repository.ContinueRebaseAsync(EditorExecutable, token), GitOperationAction.Skip => Repository.SkipRebaseAsync(EditorExecutable, token), _ => Repository.AbortRebaseAsync(token) }, operation: action);
    public Task ManageRebaseAsync(GitOperationAction action)
    {
        if (!Enum.IsDefined(action)) { throw new ArgumentOutOfRangeException(nameof(action)); }
        if (_state?.IsActive != true || action == GitOperationAction.Continue && !_state.CanContinue
            || action == GitOperationAction.Skip && !_state.CanSkip) { return Task.CompletedTask; }
        return RecoverAsync(action);
    }
    private Task EditTodoAsync() => MutateAsync("Edit todo", token => Repository.EditRebaseTodoAsync(EditorExecutable, token), closeFinished: false);
    private Task PreserveJournalAsync() => RunAsync("Preserve journal", async token =>
    {
        IReadOnlyList<GitRebasePreservedJournal> preserved = await Repository.PreserveOrphanedRebaseJournalsAsync(token);
        MarkRepositoryChanged();
        Warnings = preserved.Select(journal => T("The journal was preserved at:") + " " + journal.PreservedPath).ToArray();
        await ReloadAsync(token);
        FocusAction();
    }, cancellationToken: _token);
    private async Task MutateAsync(string label, Func<CancellationToken, Task<GitResult>> command, bool closeFinished = true, GitOperationAction? operation = null)
    {
        bool finished = false; Succeeded = Aborted = false; Warnings = [];
        await RunAsync(label, async token =>
        {
            MarkRepositoryChanged();
            try { Warnings = (await command(token)).Warnings; }
            catch (GitCommandException error) { Warnings = error.Result.Warnings; throw; }
            finally { await ReloadAsync(CancellationToken.None); }
            finished = _state?.IsActive == false;
            Aborted = finished && operation == GitOperationAction.Abort;
            Succeeded = finished && closeFinished && operation != GitOperationAction.Abort;
        }, cancellationToken: _token);
        if (finished && closeFinished && LastError is null && Warnings.Count == 0 && _state?.HasRecoverableJournal != true && !_closingAccepted) { Close(); }
        else { FocusAction(); }
    }

    protected override void UpdateActions()
    {
        bool ready = _loaded && !IsBusy, pending = _state?.IsActive == true, conflict = _state?.HasConflicts == true;
        foreach (Control input in new Control[] { _onto, _interactive, _merges, _autosquash, _autostash, _ignoreDate, _authorDate, _updateRefs, _range }) { input.IsEnabled = ready && !pending; }
        bool dates = _ignoreDate.IsChecked == true || _authorDate.IsChecked == true;
        _ignoreDate.IsEnabled &= _authorDate.IsChecked != true; _authorDate.IsEnabled &= _ignoreDate.IsChecked != true;
        _interactive.IsEnabled &= !dates; _merges.IsEnabled &= !dates; _autosquash.IsEnabled &= !dates && _interactive.IsChecked == true;
        _updateRefs.IsVisible = _capabilities?.SupportUpdateRefs == true;
        _from.IsEnabled = _to.IsEnabled = _chooseFrom.IsEnabled = ready && !pending && _range.IsChecked == true;
        _execute.IsVisible = !pending; _execute.IsEnabled = ready && !pending && _state?.HasRecoverableJournal != true;
        _continue.IsVisible = pending && !conflict; _continue.IsEnabled = ready && _state?.CanContinue == true;
        _resolve.IsVisible = pending && conflict; _resolve.IsEnabled = ready && conflict;
        foreach (Button button in new[] { _skip, _abort, _addFiles, _commit }) { button.IsVisible = pending; button.IsEnabled = ready && pending; }
        _skip.IsEnabled &= _state?.CanSkip == true; _commit.IsEnabled &= _state?.CanCommit == true;
        _preserveJournal.IsVisible = _state?.HasRecoverableJournal == true;
        _preserveJournal.IsEnabled = ready && _state?.HasRecoverableJournal == true;
        _editTodo.IsVisible = pending; _editTodo.IsEnabled = ready && _state?.CanEditTodo == true;
        _stash.IsEnabled = ready && !pending && _dirty; _conflictNotice.IsVisible = _conflicts.IsVisible = pending && conflict;
        _execute.IsDefault = !pending && _state?.HasRecoverableJournal != true; _continue.IsDefault = pending && _state?.CanContinue == true; _resolve.IsDefault = pending && conflict;
    }
    private void FocusAction()
    {
        if (!IsVisible || IsBusy || _closingAccepted) { return; }
        Control target = _state?.HasRecoverableJournal == true ? _cancel : _state?.IsActive != true ? _onto : _state.HasConflicts ? _resolve
            : _state.CanContinue ? _continue : _cancel;
        target.Focus();
    }
    protected override void OnClosing(WindowClosingEventArgs args)
    { bool previous = _closingAccepted; _closingAccepted = true; base.OnClosing(args); if (args.Cancel) { _closingAccepted = previous; } }

    private async Task ResolveAsync()
    {
        await RunAsync("Solve conflicts", async token =>
        {
            // A resolver may stage files before it closes this window or cancellation resumes us.
            // Keep the Browse refresh signal even when the lifetime guard stops a deferred reload.
            MarkRepositoryChanged();
            if (_resolveConflictsAsync is not null) { await _resolveConflictsAsync(this, Repository, false, token); }
            else { await ResolveConflictsDialog.ShowAsync(this, Repository, token); }
            if (_closingAccepted || !IsVisible || token.IsCancellationRequested) { return; }
            await ReloadAsync(token);
        }, cancellationToken: _token); FocusAction();
    }
    private async Task AddFilesAsync()
    { RebaseAddFilesDialog dialog = new(Repository); await ShowOwnedAsync(dialog); if (dialog.HasRepositoryChanges) { MarkRepositoryChanged(); } await RefreshAfterChildAsync(); }
    private async Task CommitAsync()
    { CommitDialog dialog = new(Repository); await ShowOwnedAsync(dialog); if (dialog.HasRepositoryChanges) { MarkRepositoryChanged(); } await RefreshAfterChildAsync(); }
    private async Task StashAsync()
    { StashDialog dialog = new(Repository, manage: false); dialog.Opened += async (_, _) => await dialog.InitializeAsync(); await ShowOwnedAsync(dialog); if (dialog.HasRepositoryChanges) { MarkRepositoryChanged(); } await RefreshAfterChildAsync(); }
    private async Task RefreshAfterChildAsync()
    { if (_closingAccepted || !IsVisible || _token.IsCancellationRequested) { return; } await RunAsync("Rebase", ReloadAsync, cancellationToken: _token); FocusAction(); }
    private async Task ShowOwnedAsync(Window child)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_token, _lifetime.Token);
        child.RequestedThemeVariant = ActualThemeVariant;
        using CancellationTokenRegistration close = linked.Token.Register(() => Dispatcher.UIThread.Post(child.Close));
        try { await child.ShowDialog(this); } finally { if (child.IsVisible) { child.Close(); } }
    }
    private async Task ChooseFromAsync()
    {
        GitCommit? selected = await RebaseRevisionPicker.ShowAsync(this, Repository, _from.Text, _token);
        if (selected is not null && !_closingAccepted && IsVisible && !_token.IsCancellationRequested) { _from.Text = selected.Hash; }
    }
    private async Task ShowPatchAsync()
    {
        if (IsBusy || _patches.SelectedItem is not GitRebasePatch patch) { return; }
        if (patch.Commit is GitCommit commit && commit.Parents.Count != 0)
        {
            CompareRevisionsDialog diff = new(Repository, commit.Parents[0], commit.Hash);
            diff.Opened += async (_, _) => await diff.InitializeAsync(); await ShowOwnedAsync(diff);
        }
        else if (patch.CommitHash is not null || patch.PatchPath is string)
        {
            FileHistoryContentView viewer = new() { Name = "RebasePatchContent" };
            string text = patch.CommitHash is string hash ? await Repository.GetCommitDiffAsync(hash, cancellationToken: _token)
                : GitFileEncodingCodec.DecodeFile(await File.ReadAllBytesAsync(patch.PatchPath!, _token), "utf-8").Text;
            viewer.ShowDiff(text);
            Window diff = new() { Name = "RebasePatchDiff", Title = T("Diff"), Width = 900, Height = 600,
                Content = viewer };
            diff.Closed += (_, _) => viewer.Dispose();
            await ShowOwnedAsync(diff);
        }
    }

    public static async Task<bool> ShowAsync(Window owner, GitRepository repository, string? initialOnto = null, string? initialFrom = null,
        string? initialTo = null, bool interactiveInitial = false, Func<Window, GitRepository, bool, CancellationToken, Task>? resolveConflictsAsync = null,
        CancellationToken cancellationToken = default)
    {
        RebaseDialog dialog = new(repository, initialOnto, initialFrom, initialTo, interactiveInitial, resolveConflictsAsync, cancellationToken)
        { RequestedThemeVariant = owner.ActualThemeVariant };
        dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        using CancellationTokenRegistration close = cancellationToken.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        try { await dialog.ShowDialog(owner); return dialog.HasRepositoryChanges; }
        finally { if (dialog.IsVisible) { dialog.Close(); } }
    }
}

internal sealed class RebaseRevisionPicker : RepositoryDialog
{
    private readonly ExportRevisionList _revisions = new("RebaseChooseRevision", false);
    private readonly string? _initial;
    private RebaseRevisionPicker(GitRepository repository, string? initial) : base(repository, "Choose revision")
    {
        _initial = initial; Name = "RebaseRevisionPicker"; Width = 780; Height = 420; MinWidth = 620; MinHeight = 300;
        Button select = Action("ChooseRebaseCommit", "OK", () => { if (_revisions.Rows.SelectedItem is GitCommit commit) { Close(commit); } return Task.CompletedTask; }); select.IsDefault = true;
        Button cancel = new() { Name = "CancelRebaseChoice", Content = T("Cancel"), IsCancel = true }; cancel.Click += (_, _) => Close(null);
        StackPanel buttons = ExportDialogControls.Buttons(cancel, select); DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons);
        _revisions.Margin = new Thickness(10, 6); Root.Children.Add(_revisions); Track(_revisions);
    }
    public override Task InitializeAsync() => RunAsync("Choose revision", async token =>
    {
        IReadOnlyList<GitCommit> commits = await Repository.GetLogAsync(new GitLogQuery(Count: 500, All: false, Revision: "HEAD"), token);
        _revisions.SetData(commits, await Repository.GetReferencesAsync(token), (await Repository.GetStatusAsync(token)).Head);
        string? initial = null;
        if (!string.IsNullOrWhiteSpace(_initial))
        { try { initial = await Repository.ResolveRevisionAsync(_initial, token); } catch (GitCommandException) { } }
        _revisions.Rows.SelectedItem = commits.FirstOrDefault(commit => commit.Hash == initial) ?? commits.ElementAtOrDefault(1) ?? commits.FirstOrDefault();
    });
    internal static async Task<GitCommit?> ShowAsync(Window owner, GitRepository repository, string? initial, CancellationToken token)
    {
        RebaseRevisionPicker picker = new(repository, initial) { RequestedThemeVariant = owner.ActualThemeVariant };
        picker.Opened += async (_, _) => await picker.InitializeAsync();
        using CancellationTokenRegistration close = token.Register(() => Dispatcher.UIThread.Post(picker.Close));
        try { return await picker.ShowDialog<GitCommit?>(owner); } finally { if (picker.IsVisible) { picker.Close(); } }
    }
}

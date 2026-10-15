using System.Collections.Concurrent;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Linux.Desktop;

public sealed partial class PullDialog : RepositoryDialog
{
    private readonly GitPullAction _initialAction;
    private readonly string? _initialRemote, _initialBranch;
    private readonly Func<CancellationToken, Task>? _afterSubmoduleUpdateAsync;
    private readonly Func<Window, GitRepository, bool, CancellationToken, Task>? _resolveConflictsAsync;
    private readonly PullPreferencesStore _store = new();
    private readonly SubmoduleCheckoutPreferencesStore _submodulePreferences = new();
    private readonly ComboBox _remote = PullChoice("PullRemote");
    private readonly ComboBox _branch = PullChoice("PullBranch");
    private readonly RadioButton _merge = new() { Name = "PullMerge", GroupName = "PullMode" };
    private readonly RadioButton _rebase = new() { Name = "PullRebase", GroupName = "PullMode" };
    private readonly RadioButton _fetch = new() { Name = "PullFetch", GroupName = "PullMode" };
    private readonly CheckBox _prune = new() { Name = "PullPrune" };
    private readonly CheckBox _pruneTags = new() { Name = "PullPruneTags" };
    private readonly CheckBox _unshallow = new() { Name = "PullUnshallow", IsVisible = false };
    private readonly CheckBox _autoStash = new() { Name = "PullAutoStash" };
    private readonly RadioButton _tagsFollow = new() { Name = "PullTagsFollow", GroupName = "PullTagMode", IsChecked = true };
    private readonly RadioButton _tagsNone = new() { Name = "PullTagsNone", GroupName = "PullTagMode" };
    private readonly RadioButton _tagsAll = new() { Name = "PullTagsAll", GroupName = "PullTagMode" };
    private readonly TextBox _output = new() { Name = "PullOutput", AcceptsReturn = true, IsReadOnly = true, MinHeight = 80, MaxHeight = 180, FontFamily = new("monospace") };
    private readonly ListBox _conflicts = new() { Name = "PullConflicts", Height = 72, IsVisible = false };
    private readonly Button _execute, _continue, _abort, _resolve, _stash, _manage, _browse;
    private IReadOnlyList<GitRemote> _remotes = [];
    private const string AllRemotes = "[ All ]";
    private GitStatus? _status;
    private GitRepositoryInfo? _info;
    private string? _configuredRemote, _operationName;
    private IReadOnlyList<GitReference> _references = [];
    private bool _initializing, _updatingFlags;
    private GitStash? _pendingAutoStash;
    internal bool IsReady { get; private set; }
    internal bool Succeeded { get; private set; }
    internal bool Cancelled { get; private set; }
    internal bool Attempted { get; private set; }
    internal GitProcessDialogResult? LastProcess { get; private set; }
    internal GitStash? PendingAutoStash => _pendingAutoStash;

    public PullDialog(GitRepository repository, GitPullAction action = GitPullAction.Merge, string? remote = null, string? branch = null,
        Func<CancellationToken, Task>? afterSubmoduleUpdateAsync = null,
        Func<Window, GitRepository, bool, CancellationToken, Task>? resolveConflictsAsync = null) : base(repository, "Pull")
    {
        if (!PullPreferencesStore.IsToolbarAction(action)) { throw new ArgumentOutOfRangeException(nameof(action)); }
        _initialAction = action;
        _initialRemote = remote; _initialBranch = branch;
        _afterSubmoduleUpdateAsync = afterSubmoduleUpdateAsync;
        _resolveConflictsAsync = resolveConflictsAsync;
        Name = "PullDialog"; Width = 944; Height = 525; MinWidth = 650; MinHeight = 510;
        ConstrainStatusHeight("PullStatusScroll", 90);
        _statusRow = Root.Children.Single();
        _merge.Content = Label("Merge", "&Merge remote branch into current branch");
        _rebase.Content = Label("Rebase", "R&ebase current branch on top of remote branch, creates linear history (use with caution)");
        _fetch.Content = Label("Fetch", "Do not merge, only &fetch remote changes");
        _prune.Content = Label("Prune", "&Prune remote branches");
        _pruneTags.Content = Label("PruneTags", "Prune remote branches an&d tags");
        _unshallow.Content = Label("Unshallow", "Do&wnload full history");
        _autoStash.Content = Label("AutoStash", "Auto stas&h");
        _tagsFollow.Content = Label("ReachableTags", "Follow &tagopt, if not specified, fetch tags reachable from remote HEAD");
        _tagsNone.Content = Label("NoTags", "Fetch &no tag");
        _tagsAll.Content = Label("AllTags", "Fetch &all tags");
        ReferenceDialogControls.Compact(_merge, _rebase, _fetch, _prune, _pruneTags, _unshallow, _autoStash, _tagsFollow, _tagsNone, _tagsAll);
        _rebase.Height = _tagsFollow.Height = double.NaN;
        ToolTip.SetTip(_prune, C("Prune", "Removes remote tracking branches that no longer exist on the remote (e.g. if someone else deleted them).\n\nActual command line (if checked): --prune --force\n", "Tooltip"));
        ToolTip.SetTip(_pruneTags, C("PruneTags", "Before fetching, remove any local tags that no longer exist on the remote if --prune is enabled.", "Tooltip"));
        _autoStash.IsChecked = _store.Load().AutoStash;
        _execute = Action("ExecutePull", "Pull", async () => { await ExecuteAsync(this); if (Succeeded) { Close(); } });
        _execute.IsDefault = true;
        _continue = Action("ContinuePullOperation", "Continue", () => ManageAsync(GitOperationAction.Continue));
        _abort = Action("AbortPullOperation", "Abort", () => ManageAsync(GitOperationAction.Abort));
        _resolve = Action("ResolvePullConflict", "Solve conflicts…", ResolveAsync);
        _stash = Action("PullStashChanges", "Stash changes", OpenStashAsync); _stash.Content = Label("Stash", "Stash &changes");
        _manage = Action("PullManageRemotes", "Manage remotes", ManageRemotesAsync);
        _browse = Action("PullBrowse", "Browse...", BrowseSourceAsync);
        BuildOriginalLayout();
        Track(_remote, _branch, _fromRemote, _fromUrl, _url, _localBranch, _merge, _rebase, _fetch, _prune, _pruneTags, _unshallow, _autoStash, _tagsFollow, _tagsNone, _tagsAll, _conflicts);
        foreach (RadioButton mode in new[] { _merge, _rebase, _fetch }) { mode.IsCheckedChanged += (_, _) => { if (mode.IsChecked == true) { UpdateMode(mode); } UpdateActions(); }; }
        _prune.IsCheckedChanged += (_, _) => { if (!_updatingFlags && _prune.IsChecked != true) { _pruneTags.IsChecked = false; } };
        _pruneTags.IsCheckedChanged += (_, _) =>
        {
            if (_updatingFlags || _pruneTags.IsChecked != true) { return; }
            _prune.IsChecked = true; _tagsAll.IsChecked = true;
        };
        _remote.PropertyChanged += (_, change) =>
        {
            if (change.Property == ComboBox.TextProperty && !_initializing)
            { if (All) { _fetch.IsChecked = true; } UpdateSourceUrl(); UpdateBranchChoices(); UpdateActions(); }
        };
        _conflicts.SelectionChanged += (_, _) => UpdateActions();
        Opened += (_, _) => { if (IsReady) { FocusRemote(); } };
        UpdateActions();
    }

    private string C(string item, string source, string property = "Text") => Translations.Translate("FormPull", item, property, source);
    private AccessText Label(string item, string source) => new() { Text = Translations.TranslateAccessKeys("FormPull", item, "Text", source), TextWrapping = TextWrapping.Wrap };
    private static void SetInputText(ComboBox box, string text) { box.SelectedItem = text; box.Text = text; }
    private void UpdateMode(RadioButton selectedMode)
    {
        // Avalonia raises the new radio's Checked before unchecking its peer.
        // Use the actual selection event rather than the previous Fetch value.
        if (selectedMode == _fetch || _tagsAll.IsChecked != true) { return; }
        _updatingFlags = true;
        try { _tagsFollow.IsChecked = true; }
        finally { _updatingFlags = false; }
    }

    public override Task InitializeAsync() => InitializeAsync(default);

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RunAsync("Pull", async token =>
        {
            _initializing = true;
            try
            {
                _info = await Repository.GetInfoAsync(token);
                _status = await Repository.GetStatusAsync(token);
                _references = await Repository.GetReferencesAsync(token);
                _remotes = await Repository.GetRemotesAsync(token);
                IReadOnlyList<GitRemote> remotes = _remotes;
                IReadOnlyList<KeyValuePair<string, string>> configuration = await Repository.GetConfigAsync(cancellationToken: token);
                _configuredRemote = configuration.LastOrDefault(entry => entry.Key == "branch." + _status.Branch + ".remote").Value;
                string? remote = remotes.FirstOrDefault(item => string.Equals(item.Name, _configuredRemote, StringComparison.OrdinalIgnoreCase))?.Name ?? remotes.FirstOrDefault()?.Name;
                _remote.ItemsSource = new[] { AllRemotes }.Concat(remotes.Select(item => item.Name)).ToArray();
                SetInputText(_remote, _initialAction is GitPullAction.FetchAll or GitPullAction.FetchPruneAll ? AllRemotes : _initialRemote ?? remote ?? AllRemotes);
                SetInputText(_branch, _initialBranch ?? "");
                _merge.IsChecked = _initialAction is GitPullAction.None or GitPullAction.Merge;
                _rebase.IsChecked = _initialAction == GitPullAction.Rebase;
                _fetch.IsChecked = _initialAction is GitPullAction.Fetch or GitPullAction.FetchAll or GitPullAction.FetchPruneAll;
                if (_info.IsBare) { _fetch.IsChecked = true; }
                _prune.IsChecked = _initialAction == GitPullAction.FetchPruneAll;
                _unshallow.IsVisible = await Repository.IsShallowRepositoryAsync(token);
                _unshallow.IsChecked = false;
                _autoStash.IsChecked = _store.Load().AutoStash;
                UpdateSourceUrl(); UpdateBranchChoices(); await RefreshOperationAsync(token);
                IsReady = true; Status.Text = T("Current branch:") + " " + _status.Branch;
            }
            finally { _initializing = false; }
        }, cancellationToken: cancellationToken);
        if (IsReady && IsVisible) { FocusRemote(); }
    }

    private void FocusRemote()
    {
        if (_remote.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } text) { text.Focus(); text.SelectAll(); }
        else { _remote.Focus(); }
    }

    private GitPullAction Mode => _rebase.IsChecked == true ? GitPullAction.Rebase : _fetch.IsChecked == true ? GitPullAction.Fetch : GitPullAction.Merge;
    private bool UseUrl => _fromUrl.IsChecked == true;
    private string Source => UseUrl ? _url.Text ?? "" : _remote.Text ?? "";
    private bool All => !UseUrl && _remote.Text == AllRemotes;
    private void UpdateBranchChoices()
    {
        string prefix = (_remote.Text ?? "") + "/";
        _branch.ItemsSource = UseUrl ? _references.Where(reference => reference.IsBranch).Select(reference => reference.ShortName).ToArray()
            : _references.Where(reference => reference.IsRemote && reference.SymbolicTarget is null && reference.ShortName.StartsWith(prefix, StringComparison.Ordinal))
                .Select(reference => reference.ShortName[prefix.Length..]).ToArray();
    }

    internal Task ExecuteAsync(Window owner, bool confirmPruneAll = false, CancellationToken cancellationToken = default)
        => RunAsync("Pull", async token =>
        {
            Succeeded = Cancelled = false;
            string remote = Source;
            string? branch = string.IsNullOrEmpty(_branch.Text) ? null : _branch.Text;
            GitPullAction mode = Mode;
            if (!IsReady || string.IsNullOrWhiteSpace(remote)) { throw new ArgumentException(T("Select a remote.")); }
            if (mode != GitPullAction.Fetch && All) { throw new ArgumentException(T("Select a single remote to pull.")); }
            if (mode != GitPullAction.Fetch && branch?.Contains('*') == true) { throw new ArgumentException(T("Wildcards are only allowed when fetching.")); }
            PullPreferences preferences = _store.Load();
            GitFetchTags tags = _tagsAll.IsChecked == true ? GitFetchTags.All : _tagsNone.IsChecked == true ? GitFetchTags.None : GitFetchTags.FollowTagOpt;
            GitFetchOptions fetchOptions = new(tags, _unshallow.IsChecked == true, _prune.IsChecked == true, _pruneTags.IsChecked == true);
            GitPullOptions pullOptions = new(tags, _unshallow.IsChecked == true, _autoStash.IsChecked == true, preferences.IncludeUntrackedFilesInAutoStash);
            if (mode == GitPullAction.Fetch) { await Repository.ValidateFetchOptionsAsync(fetchOptions, token); }
            else { await Repository.ValidatePullOptionsAsync(mode == GitPullAction.Rebase ? GitPullMode.Rebase : GitPullMode.Merge, remote, branch, pullOptions, token); }
            if (mode != GitPullAction.Fetch && _status?.IsDetached == true && branch is null)
            {
                int choice = await ConfirmDetachedAsync(owner, token);
                if (choice == 0) { Cancelled = true; return; }
                if (choice == 2)
                {
                    CheckoutBranchDialog checkout = new(Repository);
                    checkout.Opened += async (_, _) => await checkout.InitializeAsync(token);
                    try { await ShowOwnedAsync<object?>(owner, checkout, token); }
                    finally { if (checkout.HasRepositoryChanges) { Attempted = true; MarkRepositoryChanged(); } }
                    _status = await Repository.GetStatusAsync(token);
                    if (_status.IsDetached) { Cancelled = true; return; }
                    _configuredRemote = (await Repository.GetConfigAsync(cancellationToken: token)).LastOrDefault(entry => entry.Key == "branch." + _status.Branch + ".remote").Value;
                }
            }
            if (!UseUrl && mode != GitPullAction.Fetch && _status?.IsDetached != true && branch is null && !string.IsNullOrEmpty(_configuredRemote)
                && !string.Equals(remote, _configuredRemote, StringComparison.OrdinalIgnoreCase))
            {
                string message = string.Format(T("You asked to pull from the remote '{0}',\nbut did not specify a remote branch.\nBecause this is not the default configured remote for your local branch,\nyou must specify a remote branch."), remote)
                    + "\n\n" + string.Format(T("Pull from {0}"), remote + "/" + _status?.Branch) + "?";
                if (!await ConfirmMismatchAsync(owner, message, token))
                { Cancelled = true; return; }
                branch = _status?.Branch;
            }
            if (mode == GitPullAction.Rebase && await HasMergeToRebaseAsync(remote, branch, token))
            {
                int choice = await ConfirmRebaseAsync(owner, token);
                if (choice != 2) { Cancelled = choice == 0; return; }
            }
            if (confirmPruneAll && !await ConfirmPruneAsync(owner, token)) { Cancelled = true; return; }
            string? fetchBranch = branch;
            if (mode == GitPullAction.Fetch && !All && !string.IsNullOrWhiteSpace(_localBranch.Text))
            {
                fetchBranch = await LocalFetchRefspecAsync(owner, branch, token);
                if (fetchBranch is null) { Cancelled = true; return; }
            }
            token.ThrowIfCancellationRequested();
            _store.Save(_store.Load() with { DialogAction = mode, AutoStash = _autoStash.IsChecked == true });
            if (UseUrl) { _sourceHistory.Visit(remote, 30); }
            Attempted = true; MarkRepositoryChanged();
            Exception? operationError = null;
            try
            {
                GitResult result;
                if (mode == GitPullAction.Fetch)
                {
                    result = await RunProcessAsync(owner, "git fetch — " + (All ? "[ All ]" : remote + (branch is null ? "" : " / " + branch)),
                        (ct, output) => All ? Repository.FetchAllAsync(fetchOptions, ct, output)
                            : fetchBranch is not null ? Repository.FetchRemoteBranchAsync(remote, fetchBranch, fetchOptions, ct, output)
                            : Repository.FetchRemoteAsync(remote, fetchOptions, ct, output), token);
                }
                else
                {
                    GitPullExecutionResult? execution = null;
                    result = await RunProcessAsync(owner, "git pull " + (mode == GitPullAction.Rebase ? "--rebase" : "--no-rebase") + " — " + remote + (branch is null ? "" : " / " + branch),
                        async (ct, output) =>
                        {
                            ct.ThrowIfCancellationRequested();
                            // A retry owns its own checkpoint; earlier saved changes remain in Git's stash list.
                            _pendingAutoStash = null;
                            execution = await Repository.PullWithOptionsAsync(mode == GitPullAction.Rebase ? GitPullMode.Rebase : GitPullMode.Merge,
                                remote, branch, pullOptions, ct, output, stash => _pendingAutoStash = stash);
                            return execution.Result;
                        }, token);
                    if (execution is null) { throw new InvalidOperationException(T("No native Git result was returned.")); }
                    _pendingAutoStash = execution.AutoStash;
                    _output.Text = (execution.StashResult?.StandardOutput ?? "") + (execution.StashResult?.StandardError ?? "") + result.StandardOutput + result.StandardError;
                    await RefreshOperationAsync(token);
                    if (!result.Success && mode == GitPullAction.Rebase && _operationName == "rebase")
                    {
                        RebaseDialog recovery = await OpenRebaseRecoveryAsync(owner, null, token);
                        await RefreshOperationAsync(token);
                        if (!recovery.Succeeded) { Cancelled = recovery.Aborted; return; }
                    }
                    else if (!result.Success) { throw new GitCommandException(execution.Arguments, result); }
                    if (_operationName is null)
                    {
                        await FinishPullAsync(owner, token);
                    }
                }
                Succeeded = true; Status.Text = T("Done");
            }
            catch (Exception error) { operationError = error; throw; }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    try { await RefreshOperationAsync(token); }
                    catch when (operationError is not null) { /* Preserve the operation's native failure or interruption. */ }
                }
            }
        }, cancellationToken: cancellationToken);

    private async Task<GitResult> RunProcessAsync(Window owner, string description,
        Func<CancellationToken, Action<GitProcessOutput>, Task<GitResult>> command, CancellationToken token)
    {
        ConcurrentQueue<GitProcessOutput> chunks = new();
        LastProcess = null; _output.Text = "";
        LastProcess = await GitProcessDialog.ShowAsync(owner, Repository.Path, description, true,
            (ct, output) => command(ct, chunk => { chunks.Enqueue(chunk); output(chunk); }), token, preserveStreamedOutput: true);
        GitProcessTextDecoder stdout = new(), stderr = new(); StringBuilder text = new();
        while (chunks.TryDequeue(out GitProcessOutput? chunk))
        { text.Append((chunk.Stream == GitProcessOutputStream.StandardOutput ? stdout : stderr).Append(chunk.Bytes.Span)); }
        text.Append(stdout.Append([], flush: true)).Append(stderr.Append([], flush: true));
        _output.Text = text.Length > 0 ? text.ToString() : LastProcess.Result?.StandardOutput + LastProcess.Result?.StandardError;
        if (LastProcess.WasCancelled || LastProcess.OwnerCloseRequested || token.IsCancellationRequested || !owner.IsVisible)
        { Cancelled = true; throw new OperationCanceledException(token); }
        if (LastProcess.Error is Exception error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); }
        return LastProcess.Result ?? throw new InvalidOperationException(T("No native Git result was returned."));
    }

    private async Task<bool> HasMergeToRebaseAsync(string remote, string? branch, CancellationToken token)
    {
        string? target = branch is null ? _references.FirstOrDefault(reference => reference.IsCurrent)?.Upstream : remote + "/" + branch;
        if (target is null || !_references.Any(reference => reference.IsRemote && reference.ShortName == target)) { return false; }
        GitResult result = await Repository.RunAsync(["rev-list", "-1", "--merges", target + "..HEAD", "--"], token);
        return result.StandardOutput.Trim().Length > 0;
    }

    private Task OpenStashAsync() => RunAsync("Stash", async token =>
    {
        StashDialog dialog = new(Repository) { RequestedThemeVariant = ActualThemeVariant };
        dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        try { await ShowOwnedAsync<object?>(this, dialog, token); }
        finally { if (dialog.HasRepositoryChanges) { Attempted = true; MarkRepositoryChanged(); } }
        await RefreshOperationAsync(token);
    });

    private async Task UpdateSubmodulesAfterPullAsync(Window owner, CancellationToken token)
    {
        IReadOnlyList<GitSubmodule> submodules = await Repository.GetSubmodulesAsync(token);
        if (submodules.Count == 0) { return; }
        bool? update = _submodulePreferences.Load().Decision;
        if (update is null)
        {
            if (submodules.Any(module => !module.IsInitialized))
            {
                Window prompt = new() { Name = "ConfirmInitializePullSubmodules", Title = C("_questionInitSubmodulesCaption", "Submodules"), Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                Button yes = new() { Name = "InitializePullSubmodules", Content = T("Yes"), IsDefault = true };
                Button no = new() { Name = "SkipInitializePullSubmodules", Content = T("No"), IsCancel = true };
                yes.Click += (_, _) => prompt.Close(true); no.Click += (_, _) => prompt.Close(false);
                prompt.Content = new StackPanel { Margin = new(12), Spacing = 8, Children =
                {
                    new TextBlock { Text = C("_questionInitSubmodules", "The pulled has submodules configured.\nDo you want to initialize the submodules?\nThis will initialize and update all submodules recursive."), TextWrapping = TextWrapping.Wrap },
                    ExportDialogControls.Buttons(no, yes)
                } };
                update = await ShowOwnedAsync<bool>(owner, prompt, token);
            }
            else
            {
                SubmoduleCheckoutDecision? decision = await ShowOwnedAsync<SubmoduleCheckoutDecision?>(owner, new CheckoutSubmodulePrompt(), token);
                update = decision?.Update;
                if (decision is { Remember: true }) { _submodulePreferences.Remember(decision.Update); }
            }
        }
        if (update == true && await SubmoduleUpdateDialog.ShowAsync(owner, Repository, token))
        {
            MarkRepositoryChanged();
            if (_afterSubmoduleUpdateAsync is not null) { await _afterSubmoduleUpdateAsync(token); }
        }
    }

    private async Task OfferAutoStashPopAsync(Window owner, GitStash stash, CancellationToken token)
    {
        bool? pop = _store.Load().AutoPopStashAfterPull;
        if (pop is null)
        {
            Window prompt = new() { Name = "ConfirmPopPullAutoStash", Title = C("_applyStashedItemsAgainCaption", "Auto stash"), Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            CheckBox remember = new() { Name = "RememberPullAutoStash", Content = new AccessText { Text = Translations.TranslateAccessKeys("TranslatedStrings", "_dontShowAgain", "Text", "&Don't show me this message again") } }; ReferenceDialogControls.Compact(remember);
            Button yes = new() { Name = "ApplyPullAutoStash", Content = T("Yes"), IsDefault = true };
            Button no = new() { Name = "KeepPullAutoStash", Content = T("No") };
            yes.Click += (_, _) => prompt.Close(1); no.Click += (_, _) => prompt.Close(2);
            prompt.KeyDown += (_, args) => { if (args.Key == Avalonia.Input.Key.Escape) { args.Handled = true; prompt.Close(0); } };
            Grid information = new() { ColumnDefinitions = new("32,12,*") };
            information.Children.Add(new Panel { Width = 32, Height = 32, Children =
            {
                new Avalonia.Controls.Shapes.Ellipse { Fill = new SolidColorBrush(Color.Parse("#0078d7")), Width = 32, Height = 32 },
                new TextBlock { Text = "i", Foreground = Brushes.White, FontSize = 25, FontWeight = FontWeight.Bold, FontFamily = new("serif"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            } });
            TextBlock question = new() { Text = C("_applyStashedItemsAgain", "Apply stashed items to working directory again?"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(question, 2); information.Children.Add(question);
            prompt.Content = new StackPanel { Margin = new(12), Spacing = 8, Children =
            {
                information,
                remember, ExportDialogControls.Buttons(no, yes)
            } };
            int choice = await ShowOwnedAsync<int>(owner, prompt, token);
            pop = choice == 1;
            // Closing/Escape is not an answer; it must not become a durable No.
            if (choice != 0 && remember.IsChecked == true) { _store.Save(_store.Load() with { AutoPopStashAfterPull = pop }); }
        }
        if (pop == true)
        {
            GitResult result = await Repository.ApplyStashAsync(stash, pop: true, cancellationToken: token);
            _output.Text += result.StandardOutput + result.StandardError;
        }
    }

    private static async Task<T> ShowOwnedAsync<T>(Window owner, Window dialog, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); dialog.RequestedThemeVariant = owner.ActualThemeVariant;
        void OwnerClosed(object? sender, EventArgs args) => dialog.Close();
        owner.Closed += OwnerClosed;
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        try { T result = await dialog.ShowDialog<T>(owner); token.ThrowIfCancellationRequested(); return result; }
        finally { owner.Closed -= OwnerClosed; }
    }

    private async Task<bool> ConfirmMismatchAsync(Window owner, string message, CancellationToken token)
    {
        Window prompt = new() { Name = "ConfirmPullRemoteMismatch", Title = T("Remote branch not specified"), Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Button yes = new() { Name = "AcceptPullRemoteMismatch", Content = T("Yes"), IsDefault = true };
        Button no = new() { Name = "CancelPullRemoteMismatch", Content = T("No"), IsCancel = true };
        yes.Click += (_, _) => prompt.Close(true); no.Click += (_, _) => prompt.Close(false);
        prompt.Content = new StackPanel { Margin = new(12), Spacing = 8, Children =
        { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, ExportDialogControls.Buttons(no, yes) } };
        return await ShowOwnedAsync<bool>(owner, prompt, token);
    }

    private Task<int> ConfirmRebaseAsync(Window owner, CancellationToken token)
    {
        Window dialog = new() { Name = "ConfirmRebasePull", Title = T("Rebase merge commit?"), Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Button Choice(string caption, int value, bool isDefault = false)
        {
            Button button = new() { Content = T(caption), IsDefault = isDefault, IsCancel = value == 0 };
            button.Click += (_, _) => dialog.Close(value); return button;
        }
        dialog.Content = new StackPanel { Margin = new(12), Spacing = 10, Children =
        {
            new TextBlock { Text = T("The current commit is a merge.\nAre you sure you want to rebase this merge?"), TextWrapping = TextWrapping.Wrap },
            ExportDialogControls.Buttons(Choice("Cancel", 0), Choice("No", 1, true), Choice("Yes", 2))
        } };
        return ShowOwnedAsync<int>(owner, dialog, token);
    }

    private Task<int> ConfirmDetachedAsync(Window owner, CancellationToken token)
    {
        Window dialog = new() { Name = "ConfirmDetachedPull", Title = T("Pull"), Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Button Choice(string name, string caption, int value, bool isDefault = false)
        {
            Button button = new() { Name = name, Content = T(caption), IsDefault = isDefault, IsCancel = value == 0 };
            button.Click += (_, _) => dialog.Close(value); return button;
        }
        dialog.Content = new StackPanel { Margin = new(12), Spacing = 10, Children =
        {
            new TextBlock { Text = T("You cannot \"pull\" when git head detached.\n\nDo you want to continue?"), TextWrapping = TextWrapping.Wrap },
            ExportDialogControls.Buttons(Choice("CancelDetachedPull", "Cancel", 0, true), Choice("CheckoutBeforePull", "Checkout branch…", 2), Choice("ContinueDetachedPull", "Continue", 1))
        } };
        return ShowOwnedAsync<int>(owner, dialog, token);
    }

    private async Task<bool> ConfirmPruneAsync(Window owner, CancellationToken token)
    {
        if (_store.Load().DontConfirmFetchAndPruneAll) { return true; }
        Window dialog = new() { Name = "ConfirmFetchPruneAll", Title = T("Fetch and prune all"), Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        CheckBox suppress = new() { Name = "DontConfirmFetchPruneAll", Content = T("Don't show again") };
        Button no = new() { Name = "CancelFetchPruneAll", Content = T("No"), IsCancel = true, IsDefault = true };
        Button yes = new() { Name = "AcceptFetchPruneAll", Content = T("Yes") };
        no.Click += (_, _) => dialog.Close(false); yes.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new(12), Spacing = 10, Children =
        {
            new TextBlock { Text = T("Warning! The fetch with prune will remove all the remote-tracking references which no longer exist on remotes. Do you want to proceed?"), TextWrapping = TextWrapping.Wrap },
            suppress, ExportDialogControls.Buttons(no, yes)
        } };
        bool accepted = await ShowOwnedAsync<bool>(owner, dialog, token);
        if (accepted && suppress.IsChecked == true) { _store.Save(_store.Load() with { DontConfirmFetchAndPruneAll = true }); }
        return accepted;
    }

    private async Task RefreshOperationAsync(CancellationToken token)
    {
        _info ??= await Repository.GetInfoAsync(token);
        string directory = _info.GitDirectory;
        _operationName = Directory.Exists(Path.Combine(directory, "rebase-merge")) || Directory.Exists(Path.Combine(directory, "rebase-apply")) ? "rebase"
            : File.Exists(Path.Combine(directory, "MERGE_HEAD")) ? "merge" : null;
        _status = await Repository.GetStatusAsync(token);
        _conflicts.ItemsSource = _status.Changes.Where(change => change.IsConflict).Select(change => change.Path).ToArray();
        _conflicts.SelectedIndex = _conflicts.ItemCount > 0 ? 0 : -1;
        UpdateActions();
    }

    private async Task ResolveAsync()
    {
        if (_operationName == "rebase")
        {
            await RunAsync("Rebase", async token =>
            {
                RebaseDialog recovery = await OpenRebaseRecoveryAsync(this, null, token);
                await RefreshOperationAsync(token);
                if (recovery.Succeeded) { await FinishPullAsync(this, token); Succeeded = true; }
                else { Cancelled = recovery.Aborted; }
            });
            return;
        }
        if (_conflicts.SelectedItem is not string path)
        {
            await RunAsync("Resolve conflicts", async token =>
            {
                ResolveConflictsDialog conflicts = new(Repository);
                conflicts.Opened += async (_, _) => await conflicts.InitializeAsync();
                await ShowOwnedAsync<object?>(this, conflicts, token);
                if (conflicts.HasRepositoryChanges) { MarkRepositoryChanged(); }
                await RefreshOperationAsync(token);
            });
            return;
        }
        PatchConflictDialog dialog = new(Repository, path);
        dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        await dialog.ShowDialog(this);
        if (dialog.HasRepositoryChanges) { Attempted = true; MarkRepositoryChanged(); }
        await RefreshOperationAsync(default);
    }

    private Task ManageAsync(GitOperationAction action) => RunAsync("Pull", async token =>
    {
        if (_operationName is null) { return; }
        if (_operationName == "rebase")
        {
            RebaseDialog recovery = await OpenRebaseRecoveryAsync(this, action, token);
            await RefreshOperationAsync(token);
            if (recovery.Succeeded) { await FinishPullAsync(this, token); Succeeded = true; }
            else { Cancelled = recovery.Aborted; }
            return;
        }
        Attempted = true; MarkRepositoryChanged();
        GitResult result = await Repository.ManageOperationAsync(_operationName, action, token);
        _output.Text = result.StandardOutput + result.StandardError;
        await RefreshOperationAsync(token);
        Status.Text = T("Done");
    });

    private async Task<RebaseDialog> OpenRebaseRecoveryAsync(Window owner, GitOperationAction? action, CancellationToken token)
    {
        RebaseDialog dialog = new(Repository, resolveConflictsAsync: _resolveConflictsAsync, cancellationToken: token)
        { RequestedThemeVariant = owner.ActualThemeVariant };
        dialog.Opened += async (_, _) =>
        {
            await dialog.InitializeAsync();
            if (action is GitOperationAction requested && dialog.LastError is null && !token.IsCancellationRequested)
            { await dialog.ManageRebaseAsync(requested); }
        };
        try { await ShowOwnedAsync<object?>(owner, dialog, token); return dialog; }
        finally { if (dialog.HasRepositoryChanges) { Attempted = true; MarkRepositoryChanged(); } }
    }

    private async Task FinishPullAsync(Window owner, CancellationToken token)
    {
        await UpdateSubmodulesAfterPullAsync(owner, token);
        if (_pendingAutoStash is not null)
        {
            await OfferAutoStashPopAsync(owner, _pendingAutoStash, token);
            _pendingAutoStash = null;
        }
    }

    protected override void UpdateActions()
    {
        if (_execute is null) { return; }
        _merge.IsEnabled = _rebase.IsEnabled = !IsBusy && _info?.IsBare != true && !All;
        _prune.IsEnabled = _pruneTags.IsEnabled = _tagsAll.IsEnabled = !IsBusy && _fetch.IsChecked == true;
        _tagsFollow.IsEnabled = _tagsNone.IsEnabled = _unshallow.IsEnabled = _autoStash.IsEnabled = !IsBusy;
        _stash.IsEnabled = !IsBusy && _info?.IsBare != true;
        _branch.IsEnabled = !IsBusy && !All;
        _execute.IsEnabled = !IsBusy && IsReady && !string.IsNullOrWhiteSpace(Source) && _operationName is null && (_fetch.IsChecked == true || !All);
        UpdateOriginalPresentation();
        _continue.IsVisible = _abort.IsVisible = _operationName is not null;
        _continue.IsEnabled = !IsBusy && _operationName is not null && _conflicts.ItemCount == 0;
        _abort.IsEnabled = !IsBusy && _operationName is not null;
        _conflicts.IsVisible = _conflicts.ItemCount > 0;
        _resolve.IsEnabled = !IsBusy && _info?.IsBare != true;
    }
}

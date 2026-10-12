using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Linux.Desktop;

public sealed class PullDialog : RepositoryDialog
{
    private readonly GitPullAction _initialAction;
    private readonly string? _initialRemote, _initialBranch;
    private readonly PullPreferencesStore _store = new();
    private readonly AutoCompleteBox _remote = new() { Name = "PullRemote", Height = 24, MinimumPrefixLength = 0 };
    private readonly AutoCompleteBox _branch = new() { Name = "PullBranch", Height = 24, MinimumPrefixLength = 0 };
    private readonly RadioButton _merge = new() { Name = "PullMerge", GroupName = "PullMode" };
    private readonly RadioButton _rebase = new() { Name = "PullRebase", GroupName = "PullMode" };
    private readonly RadioButton _fetch = new() { Name = "PullFetch", GroupName = "PullMode" };
    private readonly CheckBox _prune = new() { Name = "PullPrune" };
    private readonly TextBox _output = new() { Name = "PullOutput", AcceptsReturn = true, IsReadOnly = true, MinHeight = 80, MaxHeight = 180, FontFamily = new("monospace") };
    private readonly ListBox _conflicts = new() { Name = "PullConflicts", Height = 72, IsVisible = false };
    private readonly Button _execute, _continue, _abort, _resolve;
    private const string AllRemotes = "[ All ]";
    private GitStatus? _status;
    private GitRepositoryInfo? _info;
    private string? _configuredRemote, _operationName;
    private IReadOnlyList<GitReference> _references = [];
    private bool _initializing;
    internal bool IsReady { get; private set; }
    internal bool Succeeded { get; private set; }
    internal bool Cancelled { get; private set; }
    internal bool Attempted { get; private set; }

    public PullDialog(GitRepository repository, GitPullAction action = GitPullAction.Merge, string? remote = null, string? branch = null) : base(repository, "Pull")
    {
        if (!PullPreferencesStore.IsToolbarAction(action)) { throw new ArgumentOutOfRangeException(nameof(action)); }
        _initialAction = action;
        _initialRemote = remote; _initialBranch = branch;
        Width = 600; MinWidth = 520; SizeToContent = SizeToContent.Height;
        _merge.Content = T("Merge"); _rebase.Content = T("Rebase"); _fetch.Content = T("Do not merge, only fetch");
        _prune.Content = T("Prune remote branches");
        ReferenceDialogControls.Compact(_merge, _rebase, _fetch, _prune);
        _execute = Action("ExecutePull", "Pull", async () => { await ExecuteAsync(this); if (Succeeded) { Close(); } });
        _execute.IsDefault = true;
        Button close = new() { Name = "ClosePull", Content = T("Cancel"), IsCancel = true };
        close.Click += (_, _) => Close();
        _continue = Action("ContinuePullOperation", "Continue", () => ManageAsync(GitOperationAction.Continue));
        _abort = Action("AbortPullOperation", "Abort", () => ManageAsync(GitOperationAction.Abort));
        _resolve = Action("ResolvePullConflict", "Solve conflicts…", ResolveAsync);
        StackPanel buttons = ExportDialogControls.Buttons(_resolve, _continue, _abort, close, _execute);
        DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons);
        Grid fields = ReferenceDialogControls.Fields();
        ReferenceDialogControls.Field(fields, T("Remote"), _remote);
        ReferenceDialogControls.Field(fields, T("Remote branch"), _branch);
        StackPanel body = new() { Margin = new(10), Spacing = 8, Children =
        {
            fields,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { _merge, _rebase, _fetch } },
            _prune, _conflicts, _output
        } };
        Root.Children.Add(body);
        Track(_remote, _branch, _merge, _rebase, _fetch, _prune, _conflicts);
        foreach (RadioButton mode in new[] { _merge, _rebase, _fetch }) { mode.IsCheckedChanged += (_, _) => UpdateActions(); }
        _remote.PropertyChanged += (_, change) =>
        {
            if (change.Property == AutoCompleteBox.TextProperty && !_initializing)
            { if (All) { _fetch.IsChecked = true; } UpdateBranchChoices(); UpdateActions(); }
        };
        _conflicts.SelectionChanged += (_, _) => UpdateActions();
        Opened += (_, _) => { if (IsReady) { FocusRemote(); } };
        UpdateActions();
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
                IReadOnlyList<GitRemote> remotes = await Repository.GetRemotesAsync(token);
                IReadOnlyList<KeyValuePair<string, string>> configuration = await Repository.GetConfigAsync(cancellationToken: token);
                _configuredRemote = configuration.LastOrDefault(entry => entry.Key == "branch." + _status.Branch + ".remote").Value;
                string? remote = remotes.FirstOrDefault(item => string.Equals(item.Name, _configuredRemote, StringComparison.OrdinalIgnoreCase))?.Name ?? remotes.FirstOrDefault()?.Name;
                _remote.ItemsSource = new[] { AllRemotes }.Concat(remotes.Select(item => item.Name)).ToArray();
                _remote.Text = _initialAction is GitPullAction.FetchAll or GitPullAction.FetchPruneAll ? AllRemotes : _initialRemote ?? remote ?? AllRemotes;
                _branch.Text = _initialBranch ?? "";
                _merge.IsChecked = _initialAction is GitPullAction.None or GitPullAction.Merge;
                _rebase.IsChecked = _initialAction == GitPullAction.Rebase;
                _fetch.IsChecked = _initialAction is GitPullAction.Fetch or GitPullAction.FetchAll or GitPullAction.FetchPruneAll;
                if (_info.IsBare) { _fetch.IsChecked = true; }
                _prune.IsChecked = _initialAction == GitPullAction.FetchPruneAll;
                UpdateBranchChoices(); await RefreshOperationAsync(token);
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
    private bool All => _remote.Text == AllRemotes;
    private void UpdateBranchChoices()
    {
        string prefix = (_remote.Text ?? "") + "/";
        _branch.ItemsSource = _references.Where(reference => reference.IsRemote && reference.SymbolicTarget is null && reference.ShortName.StartsWith(prefix, StringComparison.Ordinal))
            .Select(reference => reference.ShortName[prefix.Length..]).ToArray();
    }

    internal Task ExecuteAsync(Window owner, bool confirmPruneAll = false, CancellationToken cancellationToken = default)
        => RunAsync("Pull", async token =>
        {
            Succeeded = Cancelled = false;
            string remote = _remote.Text ?? "";
            string? branch = string.IsNullOrEmpty(_branch.Text) ? null : _branch.Text;
            GitPullAction mode = Mode;
            if (!IsReady || string.IsNullOrWhiteSpace(remote)) { throw new ArgumentException(T("Select a remote.")); }
            if (mode != GitPullAction.Fetch && All) { throw new ArgumentException(T("Select a single remote to pull.")); }
            if (mode != GitPullAction.Fetch && branch?.Contains('*') == true) { throw new ArgumentException(T("Wildcards are only allowed when fetching.")); }
            if (mode != GitPullAction.Fetch && _status?.IsDetached == true && branch is null)
            {
                int choice = await ConfirmDetachedAsync(owner);
                if (choice == 0) { Cancelled = true; return; }
                if (choice == 2)
                {
                    CheckoutBranchDialog checkout = new(Repository);
                    checkout.Opened += async (_, _) => await checkout.InitializeAsync();
                    await checkout.ShowDialog(owner);
                    if (checkout.HasRepositoryChanges) { Attempted = true; MarkRepositoryChanged(); }
                    _status = await Repository.GetStatusAsync(token);
                    if (_status.IsDetached) { Cancelled = true; return; }
                    _configuredRemote = (await Repository.GetConfigAsync(cancellationToken: token)).LastOrDefault(entry => entry.Key == "branch." + _status.Branch + ".remote").Value;
                }
            }
            if (mode != GitPullAction.Fetch && _status?.IsDetached != true && branch is null && !string.IsNullOrEmpty(_configuredRemote)
                && !string.Equals(remote, _configuredRemote, StringComparison.OrdinalIgnoreCase))
            {
                string message = string.Format(T("You asked to pull from the remote '{0}',\nbut did not specify a remote branch.\nBecause this is not the default configured remote for your local branch,\nyou must specify a remote branch."), remote)
                    + "\n\n" + string.Format(T("Pull from {0}"), remote + "/" + _status?.Branch) + "?";
                if (!await Dialogs.YesNoAsync(owner, T("Remote branch not specified"), message))
                { Cancelled = true; return; }
                branch = _status?.Branch;
            }
            if (mode == GitPullAction.Rebase && await HasMergeToRebaseAsync(remote, branch, token))
            {
                int choice = await ConfirmRebaseAsync(owner);
                if (choice != 2) { Cancelled = choice == 0; return; }
            }
            if (confirmPruneAll && !await ConfirmPruneAsync(owner)) { Cancelled = true; return; }
            token.ThrowIfCancellationRequested();
            _store.Save(_store.Load() with { DialogAction = mode });
            Attempted = true; MarkRepositoryChanged();
            try
            {
                GitResult result = mode switch
                {
                    GitPullAction.Rebase => await Repository.PullRebaseAsync(remote, branch, token),
                    GitPullAction.Merge => await Repository.PullMergeAsync(remote, branch, token),
                    _ when All && _prune.IsChecked == true => await Repository.FetchPruneAllAsync(token),
                    _ when All => await Repository.FetchAllAsync(token),
                    _ when branch is not null => await Repository.FetchRemoteBranchAsync(remote, branch, _prune.IsChecked == true, token),
                    _ => await Repository.FetchRemoteAsync(remote, _prune.IsChecked == true, token)
                };
                _output.Text = result.StandardOutput + result.StandardError; Succeeded = true; Status.Text = T("Done");
            }
            catch (GitCommandException ex) { _output.Text = ex.Result.StandardOutput + ex.Result.StandardError; throw; }
            finally { if (!token.IsCancellationRequested) { await RefreshOperationAsync(token); } }
        }, cancellationToken: cancellationToken);

    private async Task<bool> HasMergeToRebaseAsync(string remote, string? branch, CancellationToken token)
    {
        string? target = branch is null ? _references.FirstOrDefault(reference => reference.IsCurrent)?.Upstream : remote + "/" + branch;
        if (target is null || !_references.Any(reference => reference.IsRemote && reference.ShortName == target)) { return false; }
        GitResult result = await Repository.RunAsync(["rev-list", "-1", "--merges", target + "..HEAD", "--"], token);
        return result.StandardOutput.Trim().Length > 0;
    }

    private Task<int> ConfirmRebaseAsync(Window owner)
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
        return dialog.ShowDialog<int>(owner);
    }

    private Task<int> ConfirmDetachedAsync(Window owner)
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
        return dialog.ShowDialog<int>(owner);
    }

    private async Task<bool> ConfirmPruneAsync(Window owner)
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
        bool accepted = await dialog.ShowDialog<bool>(owner);
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
        if (_conflicts.SelectedItem is not string path) { return; }
        PatchConflictDialog dialog = new(Repository, path);
        dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        await dialog.ShowDialog(this);
        if (dialog.HasRepositoryChanges) { Attempted = true; MarkRepositoryChanged(); }
        await RefreshOperationAsync(default);
    }

    private Task ManageAsync(GitOperationAction action) => RunAsync("Pull", async token =>
    {
        if (_operationName is null) { return; }
        Attempted = true; MarkRepositoryChanged();
        GitResult result = await Repository.ManageOperationAsync(_operationName, action, token);
        _output.Text = result.StandardOutput + result.StandardError;
        await RefreshOperationAsync(token);
        Status.Text = T("Done");
    });

    protected override void UpdateActions()
    {
        if (_execute is null) { return; }
        _merge.IsEnabled = _rebase.IsEnabled = !IsBusy && _info?.IsBare != true && !All;
        _prune.IsEnabled = !IsBusy && _fetch.IsChecked == true;
        _branch.IsEnabled = !IsBusy && !All;
        _execute.IsEnabled = !IsBusy && IsReady && _operationName is null && (_fetch.IsChecked == true || !All);
        _execute.Content = T(_fetch.IsChecked == true ? "Fetch" : "Pull");
        _continue.IsVisible = _abort.IsVisible = _operationName is not null;
        _continue.IsEnabled = !IsBusy && _operationName is not null && _conflicts.ItemCount == 0;
        _abort.IsEnabled = !IsBusy && _operationName is not null;
        _resolve.IsVisible = _conflicts.IsVisible = _conflicts.ItemCount > 0;
        _resolve.IsEnabled = !IsBusy && _conflicts.SelectedItem is string;
    }
}

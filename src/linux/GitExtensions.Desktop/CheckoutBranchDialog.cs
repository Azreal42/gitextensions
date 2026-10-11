using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using GitCommands;
using GitCommands.Git;

namespace GitExtensions.Linux.Desktop;

public sealed class CheckoutBranchDialog : RepositoryDialog
{
    private readonly GitReference? _initial;
    private readonly ReferencePreferencesStore _preferences = new();
    private readonly RadioButton _local = new() { Name = "CheckoutLocalBranch", GroupName = "CheckoutKind", IsChecked = true };
    private readonly RadioButton _remote = new() { Name = "CheckoutRemoteBranch", GroupName = "CheckoutKind" };
    private readonly RadioButton _detached = new() { Name = "CheckoutRevisionMode", GroupName = "CheckoutKind" };
    private readonly ComboBox _branches = ReferenceDialogControls.Choice("CheckoutBranchSelection");
    private readonly TextBox _revision = ReferenceDialogControls.Text("CheckoutRevision", "HEAD");
    private readonly AccessText _selectionLabel = new() { Name = "CheckoutSelectionLabel" };
    private readonly TextBlock _revisionLabel = new() { Name = "CheckoutSelectionLabel" };
    private readonly Label _selectionAccess = new() { Name = "CheckoutSelectionAccess", Padding = new Thickness(0) };
    private readonly ComboBox _newMode = ReferenceDialogControls.Choice("RemoteCheckoutMode");
    private readonly TextBox _newName = ReferenceDialogControls.Text("CheckoutLocalName");
    private readonly RadioButton _keep = new() { Name = "CheckoutKeepChanges", GroupName = "CheckoutChanges", IsChecked = true };
    private readonly RadioButton _merge = new() { Name = "CheckoutMergeChanges", GroupName = "CheckoutChanges" };
    private readonly RadioButton _stash = new() { Name = "CheckoutStashChanges", GroupName = "CheckoutChanges" };
    private readonly RadioButton _discard = new() { Name = "CheckoutDiscardChanges", GroupName = "CheckoutChanges" };
    private readonly CheckBox _default = new() { Name = "CheckoutChangesDefault" };
    private readonly CheckBox _includeUntracked = new() { Name = "CheckoutStashUntracked", IsChecked = true };
    private readonly CheckBox _reapply = new() { Name = "ReapplyCheckoutStash" };
    private readonly TextBlock _current = new() { Name = "CheckoutCurrentBranch", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _saved = new() { Name = "CheckoutSavedChanges", TextWrapping = TextWrapping.Wrap };
    private readonly Border _remoteOptions;
    private readonly Border _changes;
    private readonly Button _checkout;
    private readonly bool _autoNormalize;
    private readonly string _replacement;
    private readonly Func<CancellationToken, Task>? _afterSubmoduleUpdateAsync;
    private readonly SubmoduleCheckoutPreferencesStore _submodulePreferences = new();
    private Window? _owner;
    private bool _closeOwnerWhenIdle;
    private IReadOnlyList<GitReference> _references = [];
    private bool _dirty;
    private bool _bare;
    private string? _originalStash;
    public GitCheckoutResult? LastResult { get; private set; }

    public CheckoutBranchDialog(GitRepository repository, GitReference? reference = null, bool autoNormalize = true, string replacement = "_", Func<CancellationToken, Task>? afterSubmoduleUpdateAsync = null) : base(repository, "Checkout branch")
    {
        _autoNormalize = autoNormalize;
        _replacement = replacement;
        _afterSubmoduleUpdateAsync = afterSubmoduleUpdateAsync;
        if (autoNormalize) { GitBranchNaming.Normalize("", replacement); }
        _initial = reference;
        Width = 660;
        MinWidth = 580;
        SizeToContent = SizeToContent.Height;
        _local.Content = AccessCaption("LocalBranch", "Local &branch");
        _remote.Content = AccessCaption("Remotebranch", "Remote &branch");
        _detached.Content = T("Revision (detached)");
        _selectionLabel.Text = Translations.TranslateAccessKeys("FormCheckoutBranch", "label1", "Text", "&Select branch");
        _revisionLabel.Text = T("Revision");
        _keep.Content = AccessCaption("rbDontChange", "Do&n't change");
        _merge.Content = AccessCaption("rbMerge", "&Merge");
        _stash.Content = AccessCaption("rbStash", "S&tash");
        _discard.Content = AccessCaption("rbReset", "&Reset");
        _default.Content = AccessCaption("chkSetLocalChangesActionAsDefault", "Set as &default");
        _includeUntracked.Content = T("Include untracked files");
        _reapply.Content = T("Reapply after checkout");
        ReferenceDialogControls.Compact(_local, _remote, _detached, _keep, _merge, _stash, _discard, _default, _includeUntracked, _reapply);
        _newMode.ItemsSource = new[] { T("Don't create a local branch"), T("Create local branch"), T("Reset local branch") };
        ReferencePreferences preferences = _preferences.Load();
        _newMode.SelectedIndex = (int)preferences.RemoteCheckoutMode;
        _includeUntracked.IsChecked = preferences.IncludeUntracked;
        _reapply.IsChecked = preferences.ReapplyStash;
        (_keep.IsChecked, _merge.IsChecked, _stash.IsChecked) = (preferences.CheckoutChanges == LocalChangesAction.DontChange, preferences.CheckoutChanges == LocalChangesAction.Merge, preferences.CheckoutChanges == LocalChangesAction.Stash);
        if (reference?.IsRemote == true) { _remote.IsChecked = true; }
        else if (reference?.IsTag == true) { _detached.IsChecked = true; _revision.Text = reference.Name; }
        ReferenceDialogControls.ReferenceTemplate(_branches);
        _checkout = Action("CheckoutBranch", "Checkout", CheckoutAsync);
        _checkout.Content = AccessCaption("Ok", "&Checkout");
        _checkout.IsDefault = true;
        StackPanel buttons = ExportDialogControls.Buttons(Action("CancelCheckoutBranch", "Cancel", () => { Close(); return Task.CompletedTask; }), _checkout);
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        Grid selection = ReferenceDialogControls.Fields();
        _selectionAccess.Content = _selectionLabel;
        ReferenceDialogControls.Field(selection, _selectionAccess, new Panel { Children = { _branches, _revision } });
        Grid remoteOptions = ReferenceDialogControls.Fields();
        ReferenceDialogControls.Field(remoteOptions, T("Remote checkout"), _newMode);
        ReferenceDialogControls.Field(remoteOptions, T("Local branch name"), _newName);
        _remoteOptions = ExportDialogControls.Group(T("Local branch"), remoteOptions);
        _changes = ExportDialogControls.Group(T("Local changes"), new StackPanel { Spacing = 4, Children =
        {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Children = { _keep, _merge, _stash, _discard, _default } },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Children = { _includeUntracked, _reapply } }
        } });
        Root.Children.Add(new StackPanel { Margin = new Thickness(10), Spacing = 8, Children =
        {
            _current, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Children = { _local, _remote, _detached } },
            selection, _remoteOptions, _changes, _saved
        } });
        Track(_local, _remote, _detached, _branches, _revision, _newMode, _newName, _keep, _merge, _stash, _discard, _default, _includeUntracked, _reapply);
        foreach (RadioButton radio in new[] { _local, _remote, _detached }) { radio.IsCheckedChanged += (_, _) => { PopulateBranches(); UpdateActions(); }; }
        foreach (RadioButton radio in new[] { _keep, _merge, _stash, _discard }) { radio.IsCheckedChanged += (_, _) => UpdateActions(); }
        _newMode.SelectionChanged += (_, _) => UpdateActions();
        _newName.LostFocus += (_, _) => NormalizeName();
        _branches.SelectionChanged += (_, _) =>
        {
            if (_branches.SelectedItem is GitReference branch && branch.IsRemote) { _newName.Text = branch.ShortName[(branch.ShortName.IndexOf('/') + 1)..]; }
            UpdateActions();
        };
        UpdateActions();
        Opened += (_, _) => { _owner = Owner as Window; if (_owner is not null) { _owner.Closed += OwnerClosed; } };
        Closing += (_, args) => { if (IsBusy && args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closeOwnerWhenIdle = true; } };
        Closed += (_, _) =>
        {
            if (_owner is not null)
            {
                _owner.Closed -= OwnerClosed;
                if (_closeOwnerWhenIdle) { _owner.Close(); }
            }
        };
    }

    private void OwnerClosed(object? sender, EventArgs args) => Close();

    private AccessText AccessCaption(string item, string source)
        => new() { Text = Translations.TranslateAccessKeys("FormCheckoutBranch", item, "Text", source) };

    public override async Task InitializeAsync()
    {
        await RunAsync("Checkout branch", async token =>
        {
            _bare = (await Repository.GetInfoAsync(token)).IsBare;
            GitStatus status = await Repository.GetStatusAsync(token);
            _dirty = status.Changes.Count > 0;
            _references = await Repository.GetReferencesAsync(token);
            _originalStash = (await Repository.GetStashesAsync(token)).FirstOrDefault()?.Hash;
            PopulateBranches();
            _branches.SelectedItem = _branches.ItemsSource?.Cast<GitReference>().FirstOrDefault(branch => branch.Name == _initial?.Name || _initial is null && branch.IsCurrent)
                ?? _branches.ItemsSource?.Cast<GitReference>().FirstOrDefault();
            _current.Text = T("Current branch:") + " " + status.Branch + (_dirty ? "    " + status.Changes.Count + " " + T("Local changes") : "");
            Status.Text = _bare ? T("This operation requires a working tree.") : T("Select branch");
        });
        if (IsVisible && !IsKeyboardFocusWithin)
        {
            (_remote.IsChecked == true ? _remote : _detached.IsChecked == true ? _detached : _local).Focus();
        }
    }

    private void PopulateBranches()
    {
        string? selected = (_branches.SelectedItem as GitReference)?.Name;
        GitReference[] choices = _references.Where(reference => _remote.IsChecked == true ? reference.IsRemote && reference.SymbolicTarget is null : reference.IsBranch).ToArray();
        _branches.ItemsSource = choices;
        _branches.SelectedItem = choices.FirstOrDefault(reference => reference.Name == selected) ?? choices.FirstOrDefault();
    }

    private LocalChangesAction Changes => _discard.IsChecked == true ? LocalChangesAction.Reset : _merge.IsChecked == true ? LocalChangesAction.Merge : _stash.IsChecked == true ? LocalChangesAction.Stash : LocalChangesAction.DontChange;

    public GitCheckoutOptions ReadOptions() => new(_detached.IsChecked == true ? _revision.Text ?? "HEAD" : (_branches.SelectedItem as GitReference)?.Name ?? "",
        _detached.IsChecked == true ? GitCheckoutKind.Revision : _remote.IsChecked == true ? GitCheckoutKind.RemoteBranch : GitCheckoutKind.LocalBranch,
        _remote.IsChecked == true ? (CheckoutNewBranchMode)Math.Max(0, _newMode.SelectedIndex) : CheckoutNewBranchMode.DontCreate,
        _remote.IsChecked == true && _newMode.SelectedIndex > 0 ? (_newName.Text ?? "").Trim() : null, Changes, _includeUntracked.IsChecked == true, Changes == LocalChangesAction.Stash && _reapply.IsChecked == true);

    private void NormalizeName()
    {
        if (_remote.IsChecked != true || _newMode.SelectedIndex <= 0) { return; }
        int caret = _newName.CaretIndex;
        _newName.Text = GitBranchNaming.NormalizeForInput(_newName.Text, _autoNormalize, _replacement);
        _newName.CaretIndex = Math.Min(caret, _newName.Text.Length);
    }

    private async Task CheckoutAsync()
    {
        _checkout.Focus();
        NormalizeName();
        GitCheckoutOptions options = ReadOptions();
        if (options.Changes == LocalChangesAction.Reset && _dirty && !await Dialogs.ConfirmAsync(this, T("Discard local changes"), T("Discard changes in the index and working directory while switching branches?"))) { return; }
        if (options.NewBranchMode == CheckoutNewBranchMode.Reset && !await Dialogs.ConfirmAsync(this, T("Reset branch"), T("Reset the local branch to the selected remote revision? Existing commits may no longer be referenced by this branch.") + "\n" + options.LocalBranch)) { return; }
        await RunAsync("Checkout", async token =>
        {
            string? previousHead = (await Repository.GetStatusAsync(token)).Head;
            try { LastResult = await Repository.CheckoutAsync(options, token); }
            finally
            {
                MarkRepositoryChanged();
                GitStash? stash = (await Repository.GetStashesAsync(token)).FirstOrDefault();
                if (stash is not null && stash.Hash != _originalStash) { _saved.Text = T("Saved local changes:") + " " + stash.Reference + "  " + stash.Hash; }
            }
            ReferencePreferences preferences = _preferences.Load();
            _preferences.Save(preferences with
            {
                CheckoutChanges = _default.IsChecked == true && options.Changes != LocalChangesAction.Reset ? options.Changes : preferences.CheckoutChanges,
                RemoteCheckoutMode = _remote.IsChecked == true ? options.NewBranchMode : preferences.RemoteCheckoutMode,
                IncludeUntracked = _includeUntracked.IsChecked == true, ReapplyStash = _reapply.IsChecked == true
            });
            if (previousHead != (await Repository.GetStatusAsync(token)).Head && (await Repository.GetSubmodulesAsync(token)).Count > 0)
            {
                bool? update = _submodulePreferences.Load().Decision;
                if (update is null)
                {
                    SubmoduleCheckoutDecision? decision = await CheckoutSubmodulePrompt.ShowAsync(this, token);
                    update = decision?.Update;
                    if (decision is { Remember: true }) { _submodulePreferences.Remember(decision.Update); }
                }
                if (update == true && await SubmoduleUpdateDialog.ShowAsync(this, Repository, token) && _afterSubmoduleUpdateAsync is not null)
                {
                    await _afterSubmoduleUpdateAsync(token);
                }
            }
            Close();
        });
    }

    protected override void UpdateActions()
    {
        bool revision = _detached.IsChecked == true;
        _selectionAccess.Content = revision ? _revisionLabel : _selectionLabel;
        _selectionAccess.Target = revision ? _revision : _branches;
        _branches.IsVisible = !revision;
        _revision.IsVisible = revision;
        _remoteOptions.IsVisible = _remote.IsChecked == true;
        _newName.IsEnabled = !IsBusy && _remote.IsChecked == true && _newMode.SelectedIndex > 0;
        _changes.IsVisible = _dirty;
        _includeUntracked.IsVisible = _reapply.IsVisible = _stash.IsChecked == true;
        _default.IsEnabled = !IsBusy && Changes != LocalChangesAction.Reset;
        if (Changes == LocalChangesAction.Reset) { _default.IsChecked = false; }
        _checkout.IsEnabled = !IsBusy && !_bare && (revision || _branches.SelectedItem is not null);
    }
}

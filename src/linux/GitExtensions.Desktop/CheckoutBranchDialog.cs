using GitExtensions.Linux.Scripts;
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
    private readonly PullPreferencesStore _autoStashPreferences = new();
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
    private readonly CheckBox _includeUntracked = new() { Name = "CheckoutStashUntracked" };
    private readonly CheckBox _reapply = new() { Name = "ReapplyCheckoutStash" };
    private readonly TextBlock _current = new() { Name = "CheckoutCurrentBranch", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _saved = new() { Name = "CheckoutSavedChanges", TextWrapping = TextWrapping.Wrap };
    private readonly Border _remoteOptions;
    private readonly Border _changes;
    private readonly Button _checkout;
    private readonly bool _autoNormalize;
    private readonly string _replacement;
    private readonly bool _includeUntrackedBaseline;
    private readonly Func<CancellationToken, Task>? _afterSubmoduleUpdateAsync;
    private readonly CheckoutBranchConstraint? _constraint;
    private readonly SubmoduleCheckoutPreferencesStore _submodulePreferences = new();
    private Window? _owner;
    private Window? _defaultOwner;
    private bool _closeOwnerWhenIdle;
    private IReadOnlyList<GitReference> _references = [];
    private bool? _dirty;
    private bool _initialized;
    private bool _reapplyEdited;
    private bool _bare;
    private string? _originalStash;
    public GitCheckoutResult? LastResult { get; private set; }
    public bool Succeeded { get; private set; }

    public CheckoutBranchDialog(GitRepository repository, GitReference? reference = null, bool autoNormalize = true, string replacement = "_", Func<CancellationToken, Task>? afterSubmoduleUpdateAsync = null,
        CheckoutBranchConstraint? constraint = null) : base(repository, "Checkout branch")
    {
        _autoNormalize = autoNormalize;
        _replacement = replacement;
        _afterSubmoduleUpdateAsync = afterSubmoduleUpdateAsync;
        _constraint = constraint;
        if (constraint is not null)
        {
            ArgumentNullException.ThrowIfNull(constraint.ReadReferencesAsync);
            ArgumentNullException.ThrowIfNull(constraint.CheckoutAsync);
        }
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
        _includeUntrackedBaseline = _autoStashPreferences.Load().IncludeUntrackedFilesInAutoStash;
        _includeUntracked.IsChecked = _includeUntrackedBaseline;
        _reapply.IsThreeState = true;
        _reapply.IsChecked = new ConfirmationsSettingsSession().Initial.AutoPopStashAfterCheckoutBranch;
        _reapply.IsCheckedChanged += (_, _) => _reapplyEdited = true;
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

    public override Task InitializeAsync() => InitializeAsync(CancellationToken.None);

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RunAsync("Checkout branch", async token =>
        {
            _bare = (await Repository.GetInfoAsync(token)).IsBare;
            bool checkForChanges = true;
            try { checkForChanges = new PreferencesStore().Load().CheckForUncommittedChangesInCheckoutBranch; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            GitStatus? status = checkForChanges ? await Repository.GetStatusAsync(token) : null;
            _dirty = status is null ? null : status.Changes.Count > 0;
            _references = _constraint is null ? await Repository.GetReferencesAsync(token) : await _constraint.ReadReferencesAsync(token);
            _originalStash = (await Repository.GetStashesAsync(token)).FirstOrDefault()?.Hash;
            if (_constraint is not null && !_references.Any(branch => branch.IsBranch && branch.SymbolicTarget is null)) { _remote.IsChecked = true; }
            PopulateBranches();
            if (_constraint is null)
            {
                _branches.SelectedItem = _branches.ItemsSource?.Cast<GitReference>().FirstOrDefault(branch => branch.Name == _initial?.Name || _initial is null && branch.IsCurrent)
                    ?? (_initial is null ? _branches.ItemsSource?.Cast<GitReference>().FirstOrDefault() : null);
            }
            string? branch = status?.Branch ?? _references.FirstOrDefault(reference => reference.IsCurrent)?.ShortName;
            if (branch is null)
            {
                try { branch = (await Repository.RunAsync(["symbolic-ref", "--quiet", "--short", "HEAD"], token)).StandardOutput.TrimEnd('\r', '\n'); }
                catch (GitCommandException exception) when (exception.Result.ExitCode == 1) { branch = T("detached"); }
            }
            _current.Text = T("Current branch:") + " " + branch + (_dirty == true ? "    " + status!.Changes.Count + " " + T("Local changes") : "");
            _initialized = true;
            Status.Text = _bare ? T("This operation requires a working tree.") : T("Select branch");
        }, cancellationToken: cancellationToken);
        if (IsVisible && !IsKeyboardFocusWithin)
        {
            (_remote.IsChecked == true ? _remote : _detached.IsChecked == true ? _detached : _local).Focus();
        }
    }

    private void PopulateBranches()
    {
        string? selected = (_branches.SelectedItem as GitReference)?.Name;
        GitReference[] choices = _references.Where(reference => reference.SymbolicTarget is null && (_remote.IsChecked == true ? reference.IsRemote : reference.IsBranch)).ToArray();
        _branches.ItemsSource = choices;
        _branches.SelectedItem = choices.FirstOrDefault(reference => reference.Name == selected)
            ?? (_constraint is null || choices.Length == 1 ? choices.FirstOrDefault() : null);
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

    internal bool CanExecuteDefault
    {
        get
        {
            ReferencePreferences preferences = _preferences.Load();
            GitReference? selected = _branches.SelectedItem as GitReference;
            bool selectedLocal = _constraint is { AllowDefaultAction: true }
                ? _local.IsChecked == true && selected?.IsBranch == true
                : _constraint is null && _initial?.IsBranch == true && selected?.Name == _initial.Name;
            return _initialized && LastError is null && !_bare && !preferences.AlwaysShowCheckoutDialog && selectedLocal
                && (_dirty == false || preferences.UseDefaultCheckoutChanges);
        }
    }

    internal async Task ExecuteDefaultAsync(Window owner, CancellationToken token)
    {
        _defaultOwner = owner;
        try { await CheckoutAsync(token); }
        finally { _defaultOwner = null; }
    }

    private Task CheckoutAsync() => CheckoutAsync(CancellationToken.None);

    private async Task CheckoutAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) { return; }
        Succeeded = false;
        LastResult = null;
        _checkout.Focus();
        NormalizeName();
        // The host asks only after native checkout succeeds. Core must not pop before that decision.
        GitCheckoutOptions options = ReadOptions() with { ReapplyStash = false };
        GitReference? selected = _branches.SelectedItem as GitReference;
        if (_constraint is not null && selected is null) { Status.Text = T("Select branch"); return; }
        if (_defaultOwner is not null && _dirty == false) { options = options with { Changes = LocalChangesAction.DontChange }; }
        Window operationOwner = _defaultOwner ?? this;
        if (options.Changes == LocalChangesAction.Reset && _dirty != false && !await Dialogs.ConfirmAsync(operationOwner, T("Discard local changes"), T("Discard changes in the index and working directory while switching branches?"))) { return; }
        if (options.NewBranchMode == CheckoutNewBranchMode.Reset && !await Dialogs.ConfirmAsync(operationOwner, T("Reset branch"), T("Reset the local branch to the selected remote revision? Existing commits may no longer be referenced by this branch.") + "\n" + options.LocalBranch)) { return; }
        await RunAsync("Checkout", async token =>
        {
            using ScriptWorkflowScope scripts = new(operationOwner, Repository, token);
            token = scripts.Token;
            string? previousHead = (await Repository.GetStatusAsync(token)).Head;
            Exception? checkoutError = null;
            try
            {
                GitCheckoutPreparation? preparation = _constraint is null ? await Repository.PrepareCheckoutAsync(options, token)
                    : _constraint.PrepareAsync is not null ? await _constraint.PrepareAsync(selected!, options, token) : null;
                if (preparation?.CreatedStash is not null) { MarkRepositoryChanged(); }
                if (!await RunScriptEventAsync(scripts, ScriptEvent.BeforeCheckout)) { return; }
                scripts.ThrowIfStopped();
                LastResult = _constraint is null ? await Repository.CheckoutPreparedAsync(preparation!, token)
                    : preparation is not null && _constraint.CompleteAsync is not null ? await _constraint.CompleteAsync(selected!, preparation, token)
                    : await _constraint.CheckoutAsync(selected!, options, token);
                if (!LastResult.Result.Success)
                {
                    if (options.Kind == GitCheckoutKind.Revision)
                    { scripts.ThrowIfStopped(); await RunScriptEventAsync(scripts, ScriptEvent.AfterCheckout); }
                    throw new GitCommandException(["switch"], LastResult.Result);
                }
                if (LastResult.CreatedStash is string createdStash)
                { await OfferCheckoutStashAsync(operationOwner, createdStash, token); }
            }
            catch (Exception error) { checkoutError = error; throw; }
            finally
            {
                MarkRepositoryChanged();
                if (!token.IsCancellationRequested)
                {
                    try
                    {
                        GitStash? stash = (await Repository.GetStashesAsync(token)).FirstOrDefault();
                        if (stash is not null && stash.Hash != _originalStash) { _saved.Text = T("Saved local changes:") + " " + stash.Reference + "  " + stash.Hash; }
                    }
                    catch when (checkoutError is not null) { /* Preserve the actual checkout/script failure. */ }
                }
            }
            ReferencePreferences preferences = _preferences.Load();
            _preferences.Save(preferences with
            {
                CheckoutChanges = _default.IsChecked == true && options.Changes != LocalChangesAction.Reset ? options.Changes : preferences.CheckoutChanges,
                RemoteCheckoutMode = _remote.IsChecked == true ? options.NewBranchMode : preferences.RemoteCheckoutMode,
                IncludeUntracked = _includeUntracked.IsChecked == true
            });
            if ((_includeUntracked.IsChecked == true) != _includeUntrackedBaseline)
            {
                // Checkout has already succeeded. A profile failure must not skip its
                // submodule handling or AfterCheckout scripts, as with legacy choices.
                try { _autoStashPreferences.Save(_autoStashPreferences.Load() with { IncludeUntrackedFilesInAutoStash = _includeUntracked.IsChecked == true }); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            if (previousHead != (await Repository.GetStatusAsync(token)).Head && (await Repository.GetSubmodulesAsync(token)).Count > 0)
            {
                bool? update = _submodulePreferences.Load().Decision;
                if (update is null)
                {
                    SubmoduleCheckoutDecision? decision = await CheckoutSubmodulePrompt.ShowAsync(operationOwner, token);
                    if (_constraint is not null && decision is null) { Status.Text = T("Operation cancelled"); return; }
                    update = decision?.Update;
                    if (decision is { Remember: true }) { _submodulePreferences.Remember(decision.Update); }
                }
                if (update == true)
                {
                    bool updated = await SubmoduleUpdateDialog.ShowAsync(operationOwner, Repository, token);
                    if (_constraint is not null && !updated) { Status.Text = T("Operation cancelled"); return; }
                    if (updated && _afterSubmoduleUpdateAsync is not null) { await _afterSubmoduleUpdateAsync(token); }
                }
            }
            scripts.ThrowIfStopped();
            await RunScriptEventAsync(scripts, ScriptEvent.AfterCheckout);
            scripts.ThrowIfStopped();
            Succeeded = true;
            Close();
        }, cancellationToken: cancellationToken);
    }

    private async Task OfferCheckoutStashAsync(Window owner, string createdStash, CancellationToken token)
    {
        bool? apply = _reapplyEdited ? _reapply.IsChecked : new ConfirmationsSettingsSession().Initial.AutoPopStashAfterCheckoutBranch;
        if (apply is null)
        {
            apply = await ConfirmationPrompt.ShowAsync(owner,
                Translations.Translate("FormCheckoutBranch", "_applyStashedItemsAgainCaption", "Text", "Auto stash"),
                Translations.Translate("FormCheckoutBranch", "_applyStashedItemsAgain", "Text", "Apply stashed items to working directory again?"),
                suppressed: false, remember: () => { }, token: token, warning: false,
                rememberAnswer: answer =>
                {
                    ConfirmationsSettingsSession settings = new();
                    settings.Save(settings.Initial with { AutoPopStashAfterCheckoutBranch = answer });
                });
        }
        token.ThrowIfCancellationRequested();
        if (apply == true) { await Repository.ReapplyCheckoutStashAsync(createdStash, token); }
    }

    protected override void UpdateActions()
    {
        bool revision = _detached.IsChecked == true;
        _detached.IsVisible = _constraint is null;
        _selectionAccess.Content = revision ? _revisionLabel : _selectionLabel;
        _selectionAccess.Target = revision ? _revision : _branches;
        _branches.IsVisible = !revision;
        _revision.IsVisible = revision;
        _remoteOptions.IsVisible = _remote.IsChecked == true;
        _newName.IsEnabled = !IsBusy && _remote.IsChecked == true && _newMode.SelectedIndex > 0;
        _changes.IsVisible = _initialized && _dirty != false;
        _includeUntracked.IsVisible = _reapply.IsVisible = _stash.IsChecked == true;
        _default.IsEnabled = !IsBusy && Changes != LocalChangesAction.Reset;
        if (Changes == LocalChangesAction.Reset) { _default.IsChecked = false; }
        _checkout.IsEnabled = !IsBusy && !_bare && (revision || _branches.SelectedItem is not null);
    }
}

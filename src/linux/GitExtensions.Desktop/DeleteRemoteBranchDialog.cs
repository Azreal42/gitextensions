using GitExtensions.Linux.Scripts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

/// <summary>FormDeleteRemoteBranch, sharing the process and local-branch deletion workflows.</summary>
public sealed class DeleteRemoteBranchDialog : RepositoryDialog
{
    private readonly string? _initial;
    private readonly BranchSelectionComboBox _branches = new() { Name = "DeleteRemoteBranchSelection" };
    private readonly CheckBox _remote = new() { Name = "DeleteRemoteBranches" };
    private readonly CheckBox _tracking = new() { Name = "DeleteLocalTrackingBranches" };
    private readonly TextBlock _candidates = new() { Name = "LocalTrackingBranchCandidates", TextWrapping = TextWrapping.Wrap };
    private readonly Button _delete;
    private readonly Control _statusRow;
    private IReadOnlyList<GitRemoteBranchDeletionPreview> _preview = [];
    private Window? _closingOwner;

    public DeleteRemoteBranchDialog(GitRepository repository, string? branch = null) : base(repository, "Delete branch")
    {
        _initial = branch; Name = "DeleteRemoteBranchDialog";
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        Closing += (_, args) =>
        {
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingOwner = Owner as Window; }
            foreach (GitProcessDialog process in OwnedWindows.OfType<GitProcessDialog>()) { process.RequestOwnerClose(); }
        };
        Closed += (_, _) =>
        {
            Window? owner = _closingOwner; _closingOwner = null;
            if (owner is not null) { Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        };
        Title = C("$this", "Delete branch"); Width = MinWidth = 403; SizeToContent = SizeToContent.Height; CanResize = false;
        _statusRow = (Control)Status.Parent!; _statusRow.IsVisible = false;
        ConstrainStatusHeight("DeleteRemoteBranchError", 100);
        _remote.Content = Access("DeleteRemote", "Delete branch(es) from &remote repository");
        _tracking.Content = Access("DeleteLocalTrackingBranch", "Delete &local tracking branch (if available)");
        ReferenceDialogControls.Compact(_remote, _tracking);
        _delete = Action("DeleteSelectedRemoteBranches", "Delete", DeleteAsync);
        _delete.Content = Access("Delete", "&Delete"); _delete.MinWidth = 75; _delete.Height = 25; _delete.IsDefault = true;
        StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(9, 7), Children = { _delete } };
        Border footerRow = ClassicChrome.Border(footer, new Thickness(0, 1, 0, 0), new Thickness(0));
        footerRow.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ClassicMenuBrush"));
        DockPanel.SetDock(footerRow, Dock.Bottom); Root.Children.Add(footerRow);
        Grid fields = new() { Name = "DeleteRemoteBranchFields", ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            Margin = new Thickness(9) };
        Label label = new() { Name = "RemoteBranchSelectionLabel", Content = Access("labelSelectBranches", "Select &branches"),
            Target = _branches.Editor, Padding = default, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        fields.Children.Add(label);
        Control[] rows = [_branches, _remote, _tracking, _candidates];
        for (int index = 0; index < rows.Length; index++) { Grid.SetColumn(rows[index], 1); Grid.SetRow(rows[index], index); fields.Children.Add(rows[index]); }
        Root.Children.Add(fields);
        Track(_branches, _remote, _tracking);
        _branches.SelectionChanged += (_, _) => UpdateActions();
        _remote.IsCheckedChanged += (_, _) => UpdateActions();
        UpdateActions();
    }

    private string C(string name, string source) => Translations.Translate("FormDeleteRemoteBranch", name, "Text", source);
    private AccessText Access(string name, string source) => new() { Text = Translations.TranslateAccessKeys("FormDeleteRemoteBranch", name, "Text", source) };
    internal GitRemoteBranchDeletionResult? LastDeletion { get; private set; }
    internal GitProcessDialogResult? LastProcess { get; private set; }
    internal string SelectionText { get => _branches.SelectionText; set => _branches.SelectionText = value; }
    internal GitRemoteBranchDeletionPreview[] SelectedBranches => _preview.Where(branch => _branches.SelectedBranches.Any(reference => reference.Name == branch.Reference.Name)).ToArray();

    public override async Task InitializeAsync()
    {
        await RunAsync("Select branches", async token =>
        {
            _preview = await Repository.GetRemoteBranchDeletionPreviewAsync(token);
            _branches.Choices = _preview.Select(branch => branch.Reference).ToArray();
            GitReference? initial = _preview.FirstOrDefault(branch => branch.Reference.Name == _initial || branch.Reference.ShortName == _initial)?.Reference;
            _branches.SelectionText = initial?.ShortName ?? (_initial?.StartsWith("refs/remotes/", StringComparison.Ordinal) == true ? _initial[13..] : _initial) ?? "";
            if (!string.IsNullOrEmpty(_initial) && initial is null)
            {
                ReportError(new InvalidOperationException(T("The selected reference does not map to a deletable remote branch.") + "\n" + _branches.SelectionText));
            }
        });
        _statusRow.IsVisible = LastError is not null;
        if (IsVisible) { _branches.FocusEditor(); }
    }

    protected override void UpdateActions()
    {
        string[] tracking = SelectedBranches.SelectMany(branch => branch.TrackingLocalBranches).Distinct(StringComparer.Ordinal).ToArray();
        _tracking.IsEnabled = !IsBusy && tracking.Length > 0;
        if (tracking.Length == 0) { _tracking.IsChecked = false; }
        _candidates.Text = tracking.Length == 0 ? "" : C("_toDeleteCandidates", "Local tracking branche(s) candidate to deletion:") + "\n"
            + string.Join('\n', tracking.Take(8).Select(name => " - " + name))
            + (tracking.Length > 8 ? "\n\n" + C("_andMore", "and {0} more...").Replace("{0}", (tracking.Length - 8).ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal) : "");
        _candidates.IsVisible = tracking.Length > 0;
        _delete.IsEnabled = !IsBusy && _remote.IsChecked == true && SelectedBranches.Length > 0 && _branches.UnknownNames.Length == 0;
    }

    private async Task DeleteAsync()
    {
        if (IsBusy || _remote.IsChecked != true || _branches.UnknownNames.Length > 0 || SelectedBranches.Length == 0) { return; }
        GitRemoteBranchDeletionPreview[] selected = SelectedBranches;
        bool deleteTracking = _tracking.IsChecked == true;
        await RunAsync("Delete remote branches", async token =>
        {
            LastDeletion = null; LastProcess = null;
            using CancellationTokenRegistration closeChildren = token.Register(() => Dispatcher.UIThread.Post(() =>
            {
                foreach (Window child in OwnedWindows.ToArray()) { child.Close(); }
            }));
            bool unmerged = selected.Any(branch => !branch.IsMerged);
            if (unmerged)
            {
                bool confirmed = await Dialogs.ConfirmAsync(this, C("_deleteRemoteBranchesCaption", "Delete remote branches"),
                    C("_confirmDeleteUnmergedRemoteBranchMessage", "At least one remote branch is unmerged. Are you sure you want to delete it?\nDeleting a branch can cause commits to be deleted too!"), "Yes", "No");
                token.ThrowIfCancellationRequested();
                if (!confirmed) { return; }
            }
            token.ThrowIfCancellationRequested();
            try
            {
                if (ScriptWorkflowScope.HasConfiguredEvents(ScriptEvent.BeforePush, ScriptEvent.AfterPush))
                {
                    await DeleteWithScriptEventsAsync(selected, unmerged, deleteTracking, token);
                    return;
                }
                // One owned process preserves validation of the WHOLE selection before its first push.
                // The result retains each remote's atomic transaction independently.
                LastProcess = await GitProcessDialog.ShowAsync(this, Repository.Path, T("Delete remote branches"), true, async (ct, output) =>
                {
                    LastDeletion = await Repository.DeleteRemoteBranchesAsync(new(selected, unmerged), ct, output);
                    if (LastDeletion.Cancelled) { throw new OperationCanceledException(ct); }
                    GitRemoteBranchDeletionBatchResult? failed = LastDeletion.Batches.FirstOrDefault(batch => !batch.Success);
                    if (failed is not null) { return failed.Result ?? throw new InvalidOperationException(failed.ErrorMessage); }
                    return LastDeletion.Batches.Last().Result!;
                }, token, preserveStreamedOutput: true);
            }
            finally
            {
                if (LastDeletion?.Batches.Any(batch => batch.Status != GitRemoteBranchDeletionBatchStatus.NotAttempted) == true) { MarkRepositoryChanged(); }
            }
            if (LastProcess.WorkflowStopRequested || LastProcess.OwnerCloseRequested || LastProcess.WasCancelled || token.IsCancellationRequested || !IsVisible)
            {
                // Abort stops continuation, but a still-open owner must not retry already successful remotes.
                if (!token.IsCancellationRequested && IsVisible) { await RefreshRemainingAsync(selected, token); }
                throw new OperationCanceledException(token);
            }
            if (LastProcess.CompletionError is Exception completionError) { throw new InvalidOperationException(completionError.Message, completionError); }
            if (deleteTracking && LastDeletion is not null)
            {
                GitRemoteBranchDeletionPreview[] deleted = LastDeletion.Batches.Where(batch => batch.Success).SelectMany(batch => batch.Branches).ToArray();
                IReadOnlyList<string> local = deleted.Length == 0 ? [] : await Repository.GetRemoteBranchTrackingLocalBranchesAsync(deleted, token);
                if (local.Count > 0)
                {
                    DeleteBranchDialog tracking = new(Repository, local) { RequestedThemeVariant = ActualThemeVariant };
                    tracking.Opened += async (_, _) => await tracking.InitializeAsync();
                    using CancellationTokenRegistration registration = token.Register(() => Dispatcher.UIThread.Post(tracking.Close));
                    try { await tracking.ShowDialog(this); }
                    finally { if (tracking.HasRepositoryChanges) { MarkRepositoryChanged(); } }
                    token.ThrowIfCancellationRequested();
                }
            }
            if (LastProcess.Error is not null || LastDeletion?.Success != true)
            {
                // Keep failed branches available, while dropping successful ones from the retry selection.
                await RefreshRemainingAsync(selected, token);
                if (LastProcess.Error is Exception error) { throw new InvalidOperationException(error.Message, error); }
                string failures = string.Join("\n", LastDeletion?.Batches.Where(batch => !batch.Success).Select(batch => batch.Remote + ": " + batch.ErrorMessage) ?? []);
                throw new InvalidOperationException(failures.Length > 0 ? failures : LastProcess.Result?.StandardError ?? T("Remote branch deletion failed."));
            }
            Close();
        });
        _statusRow.IsVisible = LastError is not null;
        if (LastError is not null && LastDeletion?.Cancelled == true)
        {
            Status.Text = LastError + "\n" + string.Join('\n', LastDeletion.Batches.Where(batch => batch.Status == GitRemoteBranchDeletionBatchStatus.Cancelled)
                .Select(batch => batch.Remote + ": " + T("Deletion was cancelled; refresh remote references to check the remote outcome.")));
        }
    }

    private async Task DeleteWithScriptEventsAsync(GitRemoteBranchDeletionPreview[] selected, bool allowUnmerged, bool deleteTracking, CancellationToken token)
    {
        GitRemoteBranchDeletionPlan plan = await Repository.PrepareRemoteBranchDeletionAsync(new(selected, allowUnmerged), token);
        GitRemoteBranchDeletionBatchResult[] batches = plan.Branches.GroupBy(branch => branch.Remote, StringComparer.Ordinal)
            .Select(group => new GitRemoteBranchDeletionBatchResult(group.Key, group.ToArray(), GitRemoteBranchDeletionBatchStatus.NotAttempted)).ToArray();
        LastDeletion = new(batches, false);
        using ScriptWorkflowScope scripts = new(this, Repository, token);
        for (int index = 0; index < batches.Length; index++)
        {
            scripts.ThrowIfStopped();
            if (!await RunScriptEventAsync(scripts, ScriptEvent.BeforePush))
            {
                LastDeletion = new(batches.ToArray(), true);
                await RefreshRemainingAsync(selected, scripts.Token);
                Status.Text = T("Operation cancelled");
                return;
            }
            int current = index;
            try
            {
                LastProcess = await GitProcessDialog.ShowAsync(this, Repository.Path, T("Delete remote branches") + " — " + batches[index].Remote, true,
                    async (ct, output) =>
                    {
                        batches[current] = await Repository.DeleteRemoteBranchBatchAsync(plan, batches[current].Remote, ct, output);
                        LastDeletion = new(batches.ToArray(), batches[current].Status == GitRemoteBranchDeletionBatchStatus.Cancelled);
                        GitRemoteBranchDeletionBatchResult result = batches[current];
                        if (result.Status == GitRemoteBranchDeletionBatchStatus.Cancelled) { throw new OperationCanceledException(ct); }
                        return result.Result ?? throw new InvalidOperationException(result.ErrorMessage);
                    }, scripts.Token, preserveStreamedOutput: true);
            }
            finally
            {
                if (batches.Any(batch => batch.Status != GitRemoteBranchDeletionBatchStatus.NotAttempted)) { MarkRepositoryChanged(); }
                LastDeletion = new(batches.ToArray(), scripts.Token.IsCancellationRequested || batches.Any(batch => batch.Status == GitRemoteBranchDeletionBatchStatus.Cancelled));
            }
            if (LastProcess.OwnerCloseRequested || LastProcess.WorkflowStopRequested || LastProcess.WasCancelled)
            { LastDeletion = LastDeletion with { Cancelled = true }; throw new OperationCanceledException(scripts.Token); }
            scripts.ThrowIfStopped();
            if (LastProcess.CompletionError is Exception completionError) { throw new InvalidOperationException(completionError.Message, completionError); }
            if (!batches[index].Success) { continue; }
            if (!await ScriptWorkflowScope.IsGitOperationPendingAsync(Repository, scripts.Token))
            { await RunScriptEventAsync(scripts, ScriptEvent.AfterPush); }
            scripts.ThrowIfStopped();
            if (deleteTracking)
            {
                IReadOnlyList<string> local = await Repository.GetRemoteBranchTrackingLocalBranchesAsync(batches[index].Branches, scripts.Token);
                if (local.Count > 0)
                {
                    DeleteBranchDialog tracking = new(Repository, local) { RequestedThemeVariant = ActualThemeVariant };
                    tracking.Opened += async (_, _) => await tracking.InitializeAsync();
                    using CancellationTokenRegistration registration = scripts.Token.Register(() => Dispatcher.UIThread.Post(tracking.Close));
                    try { await tracking.ShowDialog(this); }
                    finally { if (tracking.HasRepositoryChanges) { MarkRepositoryChanged(); } }
                    scripts.ThrowIfStopped();
                }
            }
        }
        LastDeletion = new(batches.ToArray(), false);
        if (!LastDeletion.Success)
        {
            await RefreshRemainingAsync(selected, scripts.Token);
            throw new InvalidOperationException(string.Join("\n", batches.Where(batch => !batch.Success).Select(batch => batch.Remote + ": " + batch.ErrorMessage)));
        }
        scripts.ThrowIfStopped();
        Close();
    }

    private async Task RefreshRemainingAsync(GitRemoteBranchDeletionPreview[] selected, CancellationToken token)
    {
        string[] remaining = LastDeletion?.Batches.Where(batch => !batch.Success).SelectMany(batch => batch.Branches).Select(branch => branch.Reference.Name).ToArray()
            ?? selected.Select(branch => branch.Reference.Name).ToArray();
        _preview = await Repository.GetRemoteBranchDeletionPreviewAsync(token);
        _branches.Choices = _preview.Select(branch => branch.Reference).ToArray();
        SelectionText = string.Join(' ', _preview.Where(branch => remaining.Contains(branch.Reference.Name, StringComparer.Ordinal)).Select(branch => branch.Reference.ShortName));
    }
}

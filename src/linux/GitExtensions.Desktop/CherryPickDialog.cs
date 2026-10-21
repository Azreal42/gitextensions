using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

/// <summary>One FormCherryPick, with its two original remembered options and a parent choice for this commit only.</summary>
public sealed class CherryPickDialog : RepositoryDialog
{
    private readonly string? _initialRevision;
    private readonly CancellationToken _token;
    private readonly Func<Window, CancellationToken, Task>? _afterSubmoduleUpdateAsync;
    private readonly CherryPickPreferencesStore _store = new();
    private readonly CheckBox _automatic = new() { Name = "CherryPickAutoCommit" };
    private readonly CheckBox _reference = new() { Name = "CherryPickAddReference" };
    private readonly TextBlock _summary = new() { Name = "CherryPickSummary", TextWrapping = TextWrapping.Wrap, MinHeight = 160 };
    private readonly ListBox _parents = new() { Name = "CherryPickParents", MinHeight = 54, MaxHeight = 180, Focusable = true };
    private readonly StackPanel _parentPanel = new() { Name = "CherryPickParentPanel", Spacing = 4, IsVisible = false };
    private readonly ListBox _conflicts = new() { Name = "CherryPickConflictFiles", Height = 80 };
    private readonly StackPanel _recovery = new() { Name = "CherryPickRecovery", Spacing = 5, IsVisible = false };
    private readonly Button _pick, _choose, _resolve, _continue, _skip, _abortOperation;
    private GitCherryPickRevision? _revision;
    private GitCherryPickSession? _session;
    private bool _recoveryInvalid;
    private bool _closingAccepted;
    private bool IsClosingOrClosed => _closingAccepted || !IsVisible;
    public string? Revision => _revision?.Commit.Hash;
    public bool Applied { get; private set; }
    public bool Skipped { get; private set; }
    public bool SequenceAccepted { get; private set; }

    public CherryPickDialog(GitRepository repository, string? revision = null, CancellationToken cancellationToken = default,
        Func<Window, CancellationToken, Task>? afterSubmoduleUpdateAsync = null) : base(repository, "Cherry pick commit")
    {
        Title = Text("$this", "Cherry pick commit"); _initialRevision = revision; _token = cancellationToken; _afterSubmoduleUpdateAsync = afterSubmoduleUpdateAsync;
        Width = 650; MinWidth = 630; SizeToContent = SizeToContent.Height; CanResize = false;
        CherryPickPreferences saved = _store.Load();
        _automatic.IsChecked = saved.CommitAutomaticallyAfterCherryPick; _reference.IsChecked = saved.AddCommitReferenceToCherryPick;
        _automatic.Content = Caption("cbxAutoCommit", "&Automatically create a commit");
        _reference.Content = Caption("cbxAddReference", "A&dd commit reference to commit message");
        ReferenceDialogControls.Compact(_automatic, _reference);
        _pick = Action("ExecuteCherryPick", "Cherry pick", ExecuteAsync); _pick.Content = Caption("btnPick", "&Cherry pick"); _pick.IsDefault = true;
        Button cancel = new() { Name = "CancelCherryPick", Content = Caption("btnAbort", "A&bort"), IsCancel = true, MinWidth = 75, Height = 25 };
        cancel.Click += (_, _) => Close(false);
        StackPanel buttons = ExportDialogControls.Buttons(cancel, _pick); DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons);
        _choose = Action("ChooseCherryPickRevision", "Choose…", ChooseAsync); _choose.Width = 26; _choose.Height = 24;
        _choose.Content = OriginalRevisionIcon();
        ToolTip.SetTip(_choose, Text("lblAnotherRev", "Choose another revision:"));
        StackPanel choose = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6,
            Children = { new Label { Content = Caption("lblAnotherRev", "C&hoose another revision:"), Target = _choose }, _choose } };
        Grid headings = ParentColumns();
        string[] columns = ["No.", "Message", "Author", "Date"];
        for (int index = 0; index < columns.Length; index++)
        { TextBlock heading = new() { Text = Text("columnHeader" + (index + 1), columns[index]), FontWeight = FontWeight.Bold }; Grid.SetColumn(heading, index); headings.Children.Add(heading); }
        _parents.ItemTemplate = new FuncDataTemplate<CherryPickParent>((row, _) =>
        {
            Grid line = ParentColumns(); line.MinHeight = 24;
            string[] values = row is null ? ["", "", "", ""] : [row.Number.ToString(), row.Commit.Subject, row.Commit.AuthorName, row.Commit.AuthoredAt.LocalDateTime.ToShortDateString()];
            for (int index = 0; index < values.Length; index++)
            { TextBlock cell = new() { Text = values[index], TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(cell, index); line.Children.Add(cell); }
            return line;
        });
        _parentPanel.Children.Add(new Label { Content = Caption("lblParents", "This commit is a merge, select &parent:"), Target = _parents });
        _parentPanel.Children.Add(headings); _parentPanel.Children.Add(ClassicChrome.Border(_parents, new Thickness(1)));
        _resolve = Action("ResolveCherryPickConflicts", "Solve conflicts…", ResolveAsync);
        _continue = Action("ContinueCherryPick", "Continue", () => RecoverAsync(GitOperationAction.Continue));
        _skip = Action("SkipCherryPick", "Skip", () => RecoverAsync(GitOperationAction.Skip));
        _abortOperation = Action("AbortCherryPickOperation", "Abort cherry-pick", AbortOperationAsync);
        _recovery.Children.Add(new TextBlock { Text = T("Resolve conflicts, then continue or abort the operation."), TextWrapping = TextWrapping.Wrap });
        _recovery.Children.Add(_conflicts);
        _recovery.Children.Add(ExportDialogControls.Buttons(_resolve, Action("RefreshCherryPickConflicts", "Refresh", RefreshConflictsAsync), _continue, _skip, _abortOperation));
        StackPanel body = new() { Margin = new Thickness(12), Spacing = 8, Children =
        {
            new TextBlock { Text = Text("lblBranchInfo", "Cherry pick this commit:") }, _summary, choose, _parentPanel, _automatic, _reference, _recovery
        } };
        Root.Children.Add(body); Track(_automatic, _reference, _parents, _conflicts);
        _conflicts.SelectionChanged += (_, _) => UpdateActions();
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Enter && !args.Handled && _pick.IsEnabled
                && (FocusManager?.GetFocusedElement() is CheckBox || _parents.IsKeyboardFocusWithin))
            {
                args.Handled = true;
                _pick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }, RoutingStrategies.Tunnel);
        Opened += (_, _) => FocusOriginalControl();
        UpdateActions();
    }

    private string Text(string item, string source) => Translations.Translate("FormCherryPick", item, "Text", source);
    private AccessText Caption(string item, string source) => new() { Text = Translations.TranslateAccessKeys("FormCherryPick", item, "Text", source) };
    private static Grid ParentColumns() => new() { ColumnDefinitions = new("42,*,140,105") };
    private static Image OriginalRevisionIcon()
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/SelectRevision.png"));
        return new() { Width = 16, Height = 16, Source = new Bitmap(stream), Tag = "SelectRevision.png" };
    }

    public override async Task InitializeAsync()
    {
        await RunAsync("Cherry pick commit", async token =>
        {
            if ((await Repository.GetInfoAsync(token)).IsBare) { throw new InvalidOperationException(T("Cherry-pick requires a working tree.")); }
            if (await AttachRecoveryAsync(token)) { return; }
            if (_initialRevision is not null) { await LoadRevisionAsync(_initialRevision, token); }
            Status.Text = T("Choose revision");
        }, cancellationToken: _token);
        FocusOriginalControl();
    }

    private async Task<bool> AttachRecoveryAsync(CancellationToken token)
    {
        GitCherryPickRecovery recovery = await Repository.GetCherryPickRecoveryAsync(token);
        _recoveryInvalid = recovery.State == GitCherryPickRecoveryState.Invalid;
        if (_recoveryInvalid)
        {
            _recovery.IsVisible = true; Status.Text = recovery.Error;
            return true;
        }
        if (recovery.Session is not { } session) { return false; }
        _session = session;
        _automatic.IsChecked = session.Options.AutoCommit; _reference.IsChecked = session.Options.AddCommitReference;
        _recovery.IsVisible = true; _conflicts.ItemsSource = recovery.ConflictedPaths; _conflicts.SelectedIndex = recovery.ConflictedPaths.Count == 0 ? -1 : 0;
        await LoadRevisionAsync(session.Options.Revision, token);
        if (session.Options.MainlineParent is int parent) { _parents.SelectedIndex = parent - 1; }
        Status.Text = session.IsRestoring ? T("Restoration was interrupted. Finish Abort or Skip before starting another cherry-pick.") : T("Resolve conflicts, then continue or abort the operation.");
        return true;
    }

    protected override void OnClosing(WindowClosingEventArgs args)
    {
        bool alreadyClosing = _closingAccepted;
        _closingAccepted = true;
        base.OnClosing(args);
        if (args.Cancel) { _closingAccepted = alreadyClosing; }
    }

    private void FocusOriginalControl()
    {
        if (!IsVisible || IsBusy) { return; }
        if (_session is { Completed: false, Aborted: false })
        { if (_conflicts.ItemCount != 0) { _conflicts.Focus(); } else { _continue.Focus(); } return; }
        if (_parentPanel.IsVisible) { _parents.Focus(); } else { _automatic.Focus(); }
    }

    public void CopyOptions(CherryPickDialog previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        _automatic.IsChecked = previous._automatic.IsChecked; _reference.IsChecked = previous._reference.IsChecked;
    }

    public GitCherryPickOptions ReadOptions()
    {
        if (_revision is null) { throw new InvalidOperationException(T("Choose revision")); }
        int? parent = _revision.Parents.Count > 1 ? (_parents.SelectedItem as CherryPickParent)?.Number : null;
        if (_revision.Parents.Count > 1 && parent is null) { throw new InvalidOperationException(Text("_noneParentSelectedText", "None parent is selected!")); }
        return new(_revision.Commit.Hash, _automatic.IsChecked == true, _reference.IsChecked == true, parent);
    }

    private async Task LoadRevisionAsync(string revision, CancellationToken token)
    {
        GitCherryPickRevision loaded = await Repository.GetCherryPickRevisionAsync(revision, token);
        _revision = loaded;
        _summary.Text = loaded.Commit.Subject + "\n" + loaded.Commit.AuthorName + " <" + loaded.Commit.AuthorEmail + ">    "
            + loaded.Commit.AuthoredAt.LocalDateTime.ToString("g") + "\n" + loaded.Commit.Hash + "\n" + loaded.Commit.Body.TrimEnd('\n');
        _parentPanel.IsVisible = loaded.Parents.Count > 1;
        _parents.ItemsSource = loaded.Parents.Select((commit, index) => new CherryPickParent(index + 1, commit)).ToArray();
        _parents.SelectedIndex = loaded.Parents.Count > 1 ? 0 : -1;
    }

    private async Task ChooseAsync()
    {
        GitCommit? commit = await CherryPickRevisionPicker.ShowAsync(this, Repository, Revision, _token);
        if (commit is null) { return; }
        await RunAsync("Revision", token => LoadRevisionAsync(commit.Hash, token), cancellationToken: _token);
        FocusOriginalControl();
    }

    private async Task ExecuteAsync()
    {
        GitCherryPickOptions options = ReadOptions();
        await RunAsync("Cherry pick", async token =>
        {
            GitCherryPickResult result;
            try { result = await Repository.CherryPickAsync(options, token); }
            catch { await AttachRecoveryAsync(CancellationToken.None); throw; }
            _session = result.Session;
            MarkRepositoryChanged(); Applied = result.Success;
            _conflicts.ItemsSource = result.ConflictedPaths; _conflicts.SelectedIndex = result.ConflictedPaths.Count > 0 ? 0 : -1;
            _recovery.IsVisible = !result.Success;
            Status.Text = result.Success ? T("Cherry pick") : result.CommandResult.StandardError;
        }, cancellationToken: _token);
        if (Applied && !IsClosingOrClosed) { await OfferSubmoduleUpdateAsync(); Accept(); }
    }

    private async Task OfferSubmoduleUpdateAsync()
    {
        // Original MergeConflictHandler offers this only when the direct pick did not leave conflicts.
        if (IsClosingOrClosed || (await Repository.GetSubmodulesAsync(_token)).Count == 0 || IsClosingOrClosed) { return; }
        SubmoduleCheckoutPreferencesStore preferences = new();
        bool? update = preferences.Load().Decision;
        if (update is null)
        {
            CheckoutSubmodulePrompt prompt = new() { RequestedThemeVariant = ActualThemeVariant };
            using CancellationTokenRegistration close = _token.Register(() => Dispatcher.UIThread.Post(() => prompt.Close(null)));
            SubmoduleCheckoutDecision? decision = await prompt.ShowDialog<SubmoduleCheckoutDecision?>(this);
            _token.ThrowIfCancellationRequested(); if (IsClosingOrClosed) { return; } update = decision?.Update;
            if (decision is { Remember: true }) { preferences.Remember(decision.Update); }
        }
        if (update != true || IsClosingOrClosed) { return; }
        SubmoduleUpdateDialog updater = new(Repository) { RequestedThemeVariant = ActualThemeVariant };
        Task? operation = null;
        updater.Opened += (_, _) => operation = updater.InitializeAsync();
        using CancellationTokenRegistration cancellation = _token.Register(() => Dispatcher.UIThread.Post(() => updater.Close()));
        await updater.ShowDialog(this);
        if (operation is not null) { await operation; }
        _token.ThrowIfCancellationRequested();
        // The original dialog accepts the pick after the optional submodule process has closed.
        if (updater.HasRepositoryChanges) { MarkRepositoryChanged(); }
        if (updater.Succeeded && !IsClosingOrClosed && _afterSubmoduleUpdateAsync is not null) { await _afterSubmoduleUpdateAsync(this, _token); }
    }

    private async Task RefreshConflictsAsync()
    {
        await RunAsync("Solve conflicts", ReadConflictsAsync, cancellationToken: _token);
    }
    private async Task ReadConflictsAsync(CancellationToken token)
    {
        IReadOnlyList<string> paths = await Repository.GetCherryPickConflictsAsync(token);
        _conflicts.ItemsSource = paths; _conflicts.SelectedIndex = paths.Count > 0 ? 0 : -1;
    }
    private async Task ResolveAsync()
    {
        if (IsBusy || IsClosingOrClosed || _conflicts.SelectedItem is not string path) { return; }
        await RunAsync("Solve conflicts", async token =>
        {
            try
            {
                if (await ConflictedPathDialog.ShowAsync(this, Repository, path, token)) { MarkRepositoryChanged(); }
                token.ThrowIfCancellationRequested();
            }
            finally
            {
                if (!token.IsCancellationRequested && !IsClosingOrClosed) { await ReadConflictsAsync(token); }
            }
        }, cancellationToken: _token);
        FocusOriginalControl();
    }
    private async Task RecoverAsync(GitOperationAction action)
    {
        if (_session is null) { return; }
        await RunAsync("Cherry pick", async token =>
        {
            GitCherryPickResult result = await Repository.ResumeCherryPickAsync(_session, action, token);
            MarkRepositoryChanged(); Applied = result.Success && action == GitOperationAction.Continue; Skipped = result.Success && action == GitOperationAction.Skip;
            _conflicts.ItemsSource = result.ConflictedPaths; _conflicts.SelectedIndex = result.ConflictedPaths.Count > 0 ? 0 : -1;
            Status.Text = result.CommandResult.StandardError;
        }, cancellationToken: _token);
        if (Applied || Skipped) { Accept(); }
    }
    private async Task AbortOperationAsync()
    {
        if (_session is null || !await Dialogs.ConfirmAsync(this, T("Abort cherry-pick"), T("Restore the working directory and index to their state before this cherry-pick?"))) { return; }
        bool aborted = false;
        await RunAsync("Abort cherry-pick", async token =>
        { await Repository.ResumeCherryPickAsync(_session, GitOperationAction.Abort, token); MarkRepositoryChanged(); aborted = true; }, cancellationToken: _token);
        if (aborted) { Close(false); }
    }
    private void Accept()
    {
        if (IsClosingOrClosed) { return; }
        SequenceAccepted = true;
        _store.Save(new(_automatic.IsChecked == true, _reference.IsChecked == true));
        Close(true);
    }
    protected override void UpdateActions()
    {
        bool pending = _session is { Completed: false, Aborted: false };
        _pick.IsEnabled = !IsBusy && _revision is not null && !pending && !_recoveryInvalid && !Applied && !Skipped;
        _choose.IsEnabled = _automatic.IsEnabled = _reference.IsEnabled = _parents.IsEnabled = !IsBusy && !pending && !_recoveryInvalid;
        _resolve.IsEnabled = !IsBusy && pending && _session?.IsRestoring != true && _conflicts.SelectedItem is not null;
        _continue.IsEnabled = !IsBusy && pending && _session?.IsRestoring != true && _conflicts.ItemCount == 0;
        _skip.IsEnabled = _abortOperation.IsEnabled = !IsBusy && pending;
    }
    private sealed record CherryPickParent(int Number, GitCommit Commit);
}

/// <summary>The original StartCherryPickDialog(IEnumerable): one modal window per revision, copying only the two checkboxes.</summary>
public static class CherryPickSequence
{
    public static async Task<bool> ShowAsync(Window owner, GitRepository repository, IEnumerable<string> revisions, CancellationToken cancellationToken = default,
        Func<Window, CancellationToken, Task>? afterSubmoduleUpdateAsync = null)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        GitCherryPickRecovery recovery = await repository.GetCherryPickRecoveryAsync(cancellationToken);
        if (recovery.State != GitCherryPickRecoveryState.Absent)
        {
            // Closing the original sequence never persists its remaining selection.
            // Reopen only the interrupted pick, even if the caller selected other commits.
            CherryPickDialog pending = new(repository, cancellationToken: cancellationToken, afterSubmoduleUpdateAsync: afterSubmoduleUpdateAsync) { RequestedThemeVariant = owner.ActualThemeVariant };
            await pending.InitializeAsync();
            using CancellationTokenRegistration closePending = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => pending.Close(false)));
            await pending.ShowDialog<bool>(owner);
            return pending.HasRepositoryChanges;
        }
        string[] selected = revisions.ToArray();
        IReadOnlyList<GitCherryPickRevision> prepared = selected.Length == 0 ? [] : await repository.PrepareCherryPickSelectionAsync(selected, cancellationToken);
        IEnumerable<string?> commits = prepared.Count == 0 ? new string?[] { null } : prepared.Select(revision => (string?)revision.Commit.Hash);
        CherryPickDialog? previous = null; bool changed = false;
        foreach (string? revision in commits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CherryPickDialog dialog = new(repository, revision, cancellationToken, afterSubmoduleUpdateAsync) { RequestedThemeVariant = owner.ActualThemeVariant };
            if (previous is not null) { dialog.CopyOptions(previous); }
            await dialog.InitializeAsync();
            using CancellationTokenRegistration close = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
            bool accepted = await dialog.ShowDialog<bool>(owner);
            changed |= dialog.HasRepositoryChanges;
            previous = dialog;
            if (!accepted || !dialog.SequenceAccepted) { break; }
        }
        return changed;
    }
}

internal sealed class CherryPickRevisionPicker : ChooseCommitDialog
{
    private CherryPickRevisionPicker(GitRepository repository, string? initial, CancellationToken token)
        : base(repository, new(initial), new("CherryPickChooseRevision", "ChooseCherryPickCommit", "CancelCherryPickChoice", "CherryPickRevisionSearch", "SearchCherryPickRevision"), token) { }

    internal static Task<GitCommit?> ShowAsync(Window owner, GitRepository repository, string? initial, CancellationToken token = default)
        => ShowPickerAsync(new CherryPickRevisionPicker(repository, initial, token), owner, token);
}

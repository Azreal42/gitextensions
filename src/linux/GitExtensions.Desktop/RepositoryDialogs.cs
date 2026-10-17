using GitExtensions.Linux.Scripts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GitCommands.Git;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

public abstract class RepositoryDialog : Window
{
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Control> _inputs = [];
    private readonly Border _statusBorder;
    private CancellationTokenSource? _operation;
    private bool _closeWhenIdle;
    private Func<Task>? _pendingDiffReload;
    protected readonly GitRepository Repository;
    protected readonly TextBlock Status = new() { Name = "OperationStatus", TextWrapping = TextWrapping.Wrap };
    protected readonly DockPanel Root = new() { LastChildFill = true };

    protected RepositoryDialog(GitRepository repository, string title)
    {
        Repository = repository;
        DesktopScriptExecutor.AttachHotkeys(this, repository, script => RunAsync(script.GetDisplayName(), async token =>
        {
            ScriptMayHaveChangedRepository = true;
            MarkRepositoryChanged();
            await DesktopScriptExecutor.ExecuteAsync(this, Repository, script, token);
        }));
        Title = T(title);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = Root;
        _statusBorder = ClassicChrome.Border(Status, new Thickness(0, 1, 0, 0), new Thickness(10, 5));
        DockPanel.SetDock(_statusBorder, Dock.Bottom);
        Root.Children.Add(_statusBorder);
        Closing += (_, args) =>
        {
            if (IsBusy) { args.Cancel = true; _closeWhenIdle = true; _operation?.Cancel(); }
        };
        Closed += (_, _) => _lifetime.Cancel();
        Opened += (_, _) => ModalWindowActivation.AfterMapping(this,
            () => !_lifetime.IsCancellationRequested && !_closeWhenIdle);
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
    }

    internal bool IsBusy { get; private set; }
    public bool HasRepositoryChanges { get; private set; }
    public bool ScriptMayHaveChangedRepository { get; private set; }
    public string? LastError { get; private set; }
    internal Exception? LastException { get; private set; }
    public abstract Task InitializeAsync();
    protected string T(string source) => _translations.Translate(source);
    protected TranslationCatalog Translations => _translations;
    protected void Track(params Control[] controls) => _inputs.AddRange(controls);
    protected void MarkRepositoryChanged() => HasRepositoryChanges = true;
    private protected async Task<bool> RunScriptEventAsync(ScriptWorkflowScope scope, ScriptEvent scriptEvent)
    {
        try { return await scope.RunAsync(scriptEvent); }
        finally
        {
            ScriptMayHaveChangedRepository |= scope.MayHaveRunScripts;
            if (ScriptMayHaveChangedRepository) { MarkRepositoryChanged(); }
        }
    }

    protected void ReportError(Exception exception) { LastException = exception; LastError = exception.Message; Status.Text = exception.Message; }
    protected void ClearError() { LastError = null; LastException = null; }

    protected void CollapseEmptyStatus()
    {
        void Update() => _statusBorder.IsVisible = !string.IsNullOrEmpty(Status.Text);
        Status.PropertyChanged += (_, change) => { if (change.Property == TextBlock.TextProperty) { Update(); } };
        Update();
    }

    /// <summary>Keep multiline command output accessible without displacing a dialog's recovery controls.</summary>
    protected void ConstrainStatusHeight(string name, double maxHeight)
    {
        _statusBorder.Child = null;
        _statusBorder.Child = new ScrollViewer
        {
            Name = name, Content = Status, MaxHeight = maxHeight,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    protected async Task RequestDiffReloadAsync(Func<Task> reload)
    {
        if (_lifetime.IsCancellationRequested) { return; }
        if (IsBusy) { _pendingDiffReload = reload; return; }
        await reload();
    }

    protected Button Action(string name, string label, Func<Task> action)
    {
        Button button = new() { Name = name, Content = T(label), HorizontalAlignment = HorizontalAlignment.Stretch };
        Track(button);
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception exception) { ReportError(exception); }
        };
        return button;
    }

    protected async Task RunAsync(string label, Func<CancellationToken, Task> action, bool changes = false, CancellationToken cancellationToken = default, bool disableInputs = true)
    {
        if (IsBusy) { return; }
        Control? previousInput = disableInputs ? FocusManager?.GetFocusedElement() as Control : null;
        if (previousInput is not null && (TopLevel.GetTopLevel(previousInput) != this
            || !_inputs.Any(input => input.IsKeyboardFocusWithin))) { previousInput = null; }
        IsBusy = true;
        LastError = null;
        LastException = null;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        if (disableInputs) { foreach (Control input in _inputs) { input.IsEnabled = false; } }
        Status.Text = T(label) + "…";
        try
        {
            await action(_operation.Token);
            HasRepositoryChanges |= changes;
        }
        catch (OperationCanceledException) { LastError = T("Operation cancelled"); Status.Text = LastError; }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            _operation.Dispose();
            _operation = null;
            IsBusy = false;
            if (disableInputs) { foreach (Control input in _inputs) { input.IsEnabled = true; } }
            UpdateActions();
            if (_closeWhenIdle) { _closeWhenIdle = false; Close(); }
            else
            {
                if (_pendingDiffReload is Func<Task> reload && !_lifetime.IsCancellationRequested)
                {
                    _pendingDiffReload = null;
                    await reload();
                }
                RestoreInputFocus(previousInput);
            }
        }
    }

    private void RestoreInputFocus(Control? input)
    {
        // Disabling the focused input clears keyboard focus. Restore it only if
        // the operation has not closed this window or focused a recovery view.
        if (input is null || _lifetime.IsCancellationRequested || _closeWhenIdle || IsBusy
            || !IsVisible || !IsEffectivelyEnabled || !input.Focusable
            || !input.IsEffectivelyEnabled || !input.IsEffectivelyVisible || TopLevel.GetTopLevel(input) != this
            || OwnedWindows.Any(child => child.IsVisible)) { return; }
        if (FocusManager?.GetFocusedElement() is Control current && current is not Window
            && current.Focusable && current.IsEffectivelyEnabled && current.IsEffectivelyVisible
            && TopLevel.GetTopLevel(current) is { IsVisible: true }) { return; }
        input.Focus();
    }

    protected virtual void UpdateActions() { }
}

public sealed class NoteDialog : RepositoryDialog
{
    private readonly string _revision;
    private readonly TextBox _note = new() { Name = "NoteText", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160 };
    private readonly Button _remove;
    private bool _hasNote;

    public NoteDialog(GitRepository repository, string revision) : base(repository, "Edit commit note")
    {
        _revision = revision;
        Width = 600;
        Height = 360;
        MinWidth = 440;
        MinHeight = 260;
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
        _remove = Action("RemoveNote", "Delete note…", RemoveAsync);
        buttons.Children.Add(_remove);
        buttons.Children.Add(Action("SaveNote", "Save", SaveAsync));
        buttons.Children.Add(Action("CloseNote", "Close", () => { Close(); return Task.CompletedTask; }));
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        StackPanel header = new() { Margin = new Thickness(10, 10, 10, 4), Children = { new TextBlock { Text = T("Commit") + ": " + revision, TextTrimming = TextTrimming.CharacterEllipsis } } };
        DockPanel.SetDock(header, Dock.Top);
        Root.Children.Add(header);
        _note.Margin = new Thickness(10, 0, 10, 10);
        Track(_note);
        Root.Children.Add(_note);
        KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.Control) { args.Handled = true; await SaveAsync(); }
        };
    }

    public override Task InitializeAsync() => RunAsync("Load note", async token =>
    {
        string? note = await Repository.GetNoteAsync(_revision, cancellationToken: token);
        _hasNote = note is not null;
        _note.Text = note;
        Status.Text = _hasNote ? T("Edit commit note") : T("This commit has no note.");
    });

    private Task SaveAsync()
    {
        string message = _note.Text ?? "";
        if (string.IsNullOrWhiteSpace(message)) { Status.Text = T("Enter a note before saving."); _note.Focus(); return Task.CompletedTask; }
        return RunAsync("Save note", async token =>
        {
            await Repository.AddNoteAsync(_revision, message, replace: _hasNote, cancellationToken: token);
            _hasNote = true;
            Status.Text = T("Note saved");
        }, changes: true);
    }

    private async Task RemoveAsync()
    {
        if (!await Dialogs.ConfirmAsync(this, T("Delete note"), T("Delete the note attached to this commit?"))) { return; }
        await RunAsync("Delete note", async token =>
        {
            await Repository.RemoveNoteAsync(_revision, cancellationToken: token);
            _hasNote = false;
            _note.Text = null;
            Status.Text = T("Note deleted");
        }, changes: true);
    }

    protected override void UpdateActions() => _remove.IsEnabled = _hasNote;
}

public sealed class BisectDialog : RepositoryDialog
{
    private readonly Button _start;
    private readonly Button _good;
    private readonly Button _bad;
    private readonly Button _skip;
    private readonly Button _stop;
    private readonly IReadOnlyList<string> _selectedRevisions;
    private readonly List<GitResult> _commandResults = [];
    private readonly CancellationTokenSource _lifetime = new();
    private GitBisectState? _state;
    private bool _sequenceRunning, _closing;

    public BisectDialog(GitRepository repository, IEnumerable<string>? selectedRevisionHashes = null) : base(repository, "Bisect")
    {
        string[] selected = selectedRevisionHashes?.ToArray() ?? [];
        foreach (string revision in selected) { ArgumentNullException.ThrowIfNull(revision); }
        _selectedRevisions = Array.AsReadOnly(selected);
        Name = "BisectDialog"; Title = Text("$this", "Bisect");
        Width = MinWidth = 248; Height = 171; SizeToContent = SizeToContent.WidthAndHeight; CanResize = false;
        // FormBisect has five buttons, without revision fields or a log panel. Native failures get a separate modal below.
        if (Status.Parent is Control statusRow) { Root.Children.Remove(statusRow); }
        StackPanel actions = new() { Name = "BisectActions", Margin = new Thickness(12, 12, 12, 14), Spacing = 4 };
        _start = BisectAction("Start", "Start bisect", StartAsync);
        _bad = BisectAction("Bad", "Mark current revision &bad", () => FinishAsync(GitBisectOption.Bad));
        _good = BisectAction("Good", "Mark current revision &good", () => FinishAsync(GitBisectOption.Good));
        _skip = BisectAction("btnSkip", "&Skip current revision", () => FinishAsync(GitBisectOption.Skip));
        _stop = BisectAction("Stop", "Stop bisect", () => FinishAsync(null));
        _skip.Margin = _stop.Margin = new Thickness(0, 2, 0, 0);
        foreach (Button button in new[] { _start, _bad, _good, _skip, _stop }) { actions.Children.Add(button); }
        Root.Children.Add(actions);
        Closing += (_, _) => { if (_closing) { return; } _closing = true; _lifetime.Cancel(); };
        Closed += (_, _) => _lifetime.Dispose();
        UpdateActions();
    }

    public IReadOnlyList<string> SelectedRevisionHashes => _selectedRevisions;
    public IReadOnlyList<GitResult> CommandResults => _commandResults.AsReadOnly();
    public GitResult? LastResult => _commandResults.LastOrDefault();
    private string Text(string item, string source) => Translations.Translate("FormBisect", item, "Text", source);
    private Button BisectAction(string name, string label, Func<Task> action)
    {
        Button button = Action(name, label, action);
        button.Content = new Avalonia.Controls.Primitives.AccessText
        { Text = Translations.TranslateAccessKeys("FormBisect", name, "Text", label), TextWrapping = TextWrapping.NoWrap };
        button.MinWidth = 224; button.Height = 25; button.MinHeight = 0;
        return button;
    }
    public override async Task InitializeAsync()
    {
        await RunAsync("Bisect status", ReadStateAsync, cancellationToken: _lifetime.Token);
        await ShowNativeErrorAsync();
    }

    private async Task StartAsync()
    {
        if (IsBusy || _sequenceRunning || _closing || !IsVisible) { return; }
        _sequenceRunning = true; UpdateActions();
        try
        {
            bool startCancelled = false;
            await RunAsync("Start bisect", async token =>
            {
                GitProcessDialogResult outcome = await RunPhaseAsync("git bisect start", true,
                    (ct, output) => Repository.StartBisectAsync(cancellationToken: ct, outputReceived: output), token);
                startCancelled = outcome.WasCancelled;
                await ReadStateAsync(token); ThrowPhaseFailure(outcome, token);
            }, cancellationToken: _lifetime.Token);
            if (_closing || !IsVisible || startCancelled || _selectedRevisions.Count < 2) { return; }
            if (!await ConfirmRangeAsync()) { return; }
            if (_closing || !IsVisible) { return; }
            await RunAsync("Bisect", async token =>
            {
                GitProcessDialogResult good = await RunPhaseAsync("git bisect good " + _selectedRevisions[0], true,
                    (ct, output) => Repository.MarkBisectAsync(GitBisectOption.Good, _selectedRevisions[0], ct, output), token);
                await ReadStateAsync(token); ThrowPhaseFailure(good, token);
                if (!good.Success) { return; }
                token.ThrowIfCancellationRequested();
                if (_closing || !IsVisible) { return; }
                GitProcessDialogResult bad = await RunPhaseAsync("git bisect bad " + _selectedRevisions[^1], true,
                    (ct, output) => Repository.MarkBisectAsync(GitBisectOption.Bad, _selectedRevisions[^1], ct, output), token);
                await ReadStateAsync(token); ThrowPhaseFailure(bad, token);
            }, cancellationToken: _lifetime.Token);
            // The original closes after accepting the range, even when Good fails (and Bad is never run), or Bad fails.
            if (!_closing && IsVisible) { Close(); }
        }
        finally { _sequenceRunning = false; UpdateActions(); }
    }

    private async Task<bool> ConfirmRangeAsync()
    {
        CancellationToken token = _lifetime.Token; token.ThrowIfCancellationRequested();
        Window question = new() { Name = "BisectRangeConfirmation", Title = Title, Width = 470, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ActualThemeVariant };
        Button yes = new() { Name = "ConfirmBisectRange", Content = T("Yes"), IsDefault = true, MinWidth = 75, Height = 25 };
        Button no = new() { Name = "CancelBisectRange", Content = T("No"), IsCancel = true, MinWidth = 75, Height = 25 };
        yes.Click += (_, _) => question.Close(true); no.Click += (_, _) => question.Close(false);
        // YesNoQuestion uses the native Question icon, rather than one of the unrelated toolbar help assets.
        Panel icon = new() { Name = "BisectQuestionIcon", Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top, Children =
        {
            new Avalonia.Controls.Shapes.Ellipse { Width = 32, Height = 32, Fill = Brushes.RoyalBlue },
            new TextBlock { Text = "?", FontSize = 27, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) }
        } };
        Grid message = new() { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        message.Children.Add(icon);
        TextBlock rangeText = new() { Name = "BisectRangeQuestion", Text = Text("_bisectStart", "Mark selected revisions as start bisect range?"), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(rangeText, 1); message.Children.Add(rangeText);
        question.Content = new StackPanel { Margin = new Thickness(12), Spacing = 12, Children =
        {
            message,
            ExportDialogControls.Buttons(yes, no)
        } };
        using CancellationTokenRegistration cancellation = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (question.IsVisible) { question.Close(false); } }));
        try { bool yesAnswer = await question.ShowDialog<bool>(this); token.ThrowIfCancellationRequested(); return yesAnswer; }
        finally { question.Close(); }
    }

    private async Task FinishAsync(GitBisectOption? verdict)
    {
        if (IsBusy || _sequenceRunning || _closing || !IsVisible) { return; }
        _sequenceRunning = true; UpdateActions();
        try
        {
            await RunAsync(verdict is null ? "Stop bisect" : "Bisect", async token =>
            {
                string description = "git bisect " + (verdict?.ToString().ToLowerInvariant() ?? "reset");
                GitProcessDialogResult outcome = await RunPhaseAsync(description, false,
                    (ct, output) => verdict is null ? Repository.ResetBisectAsync(cancellationToken: ct, outputReceived: output)
                        : Repository.MarkBisectAsync(verdict.Value, cancellationToken: ct, outputReceived: output), token);
                await ReadStateAsync(token); ThrowPhaseFailure(outcome, token);
            }, cancellationToken: _lifetime.Token);
            if (!_closing && IsVisible) { Close(); }
        }
        finally { _sequenceRunning = false; UpdateActions(); }
    }

    private async Task ShowNativeErrorAsync()
    {
        if (LastError is null || _closing || !IsVisible) { return; }
        string message = LastException is GitCommandException native ? native.Result.StandardOutput + native.Result.StandardError : LastError;
        CancellationToken token = _lifetime.Token;
        Window error = new() { Name = "BisectNativeError", Title = T("Error"), Width = 520, MaxHeight = 540, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ActualThemeVariant };
        Button accept = new() { Name = "AcceptBisectError", Content = T("OK"), IsDefault = true, IsCancel = true, MinWidth = 75, Height = 25 };
        accept.Click += (_, _) => error.Close();
        error.Content = new StackPanel { Margin = new Thickness(12), Spacing = 12, Children =
        {
            new ScrollViewer { MaxHeight = 430, Content = new TextBlock { Name = "BisectErrorMessage", Text = message, TextWrapping = TextWrapping.Wrap } },
            ExportDialogControls.Buttons(accept)
        } };
        using CancellationTokenRegistration cancellation = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (error.IsVisible) { error.Close(); } }));
        try { await error.ShowDialog(this); }
        finally { error.Close(); }
    }

    private async Task ReadStateAsync(CancellationToken token)
    {
        _state = await Repository.GetBisectStateAsync(token);
    }

    private async Task<GitProcessDialogResult> RunPhaseAsync(string description, bool useDialogSettings,
        Func<CancellationToken, Action<GitProcessOutput>, Task<GitResult>> command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        GitProcessDialogResult outcome = await GitProcessDialog.ShowAsync(this, Repository.Path, description, useDialogSettings, (ct, output) =>
        {
            ct.ThrowIfCancellationRequested(); MarkRepositoryChanged(); return command(ct, output);
        }, token);
        if (outcome.Result is GitResult result) { _commandResults.Add(result); }
        return outcome;
    }
    private static void ThrowPhaseFailure(GitProcessDialogResult outcome, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (outcome.WasCancelled) { throw new OperationCanceledException(token); }
        if (outcome.Error is Exception error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); }
    }

    protected override void UpdateActions()
    {
        bool available = !IsBusy && !_sequenceRunning && !_closing;
        _start.IsEnabled = available && _state?.IsActive != true;
        _stop.IsEnabled = _good.IsEnabled = _bad.IsEnabled = _skip.IsEnabled = available && _state?.IsActive == true;
    }
}

public sealed class SparseCheckoutDialog : RepositoryDialog
{
    private readonly CheckBox _enabled = new() { Name = "SparseEnabled" };
    private readonly CheckBox _cone = new() { Name = "SparseCone" };
    private readonly CheckBox _sparseIndex = new() { Name = "SparseIndex" };
    private readonly TextBox _rules = CreateRulesEditor();
    private static TextBox CreateRulesEditor()
    {
        TextBox editor = new() { Name = "SparseRules", AcceptsReturn = true, AcceptsTab = true };
        DesktopFonts.Apply(editor, DesktopFontRole.Code); return editor;
    }
    private readonly Button _reapply;
    private GitSparseCheckoutState? _state;

    public SparseCheckoutDialog(GitRepository repository) : base(repository, "Sparse working copy")
    {
        Width = 800;
        Height = 600;
        MinWidth = 600;
        MinHeight = 420;
        StackPanel header = new() { Margin = new Thickness(10), Spacing = 6 };
        header.Children.Add(new TextBlock { Text = T("Sparse working copy"), FontWeight = FontWeight.Bold });
        header.Children.Add(new TextBlock { Text = T("Specify the rules for including or excluding files and directories."), TextWrapping = TextWrapping.Wrap });
        _enabled.Content = T("Enable sparse checkout for this repository");
        _cone.Content = T("Cone mode: literal directories, one per line");
        _sparseIndex.Content = T("Sparse index");
        header.Children.Add(_enabled);
        header.Children.Add(_cone);
        header.Children.Add(_sparseIndex);
        _cone.IsCheckedChanged += (_, _) => _sparseIndex.IsEnabled = !IsBusy && _enabled.IsChecked == true && _cone.IsChecked == true;
        _enabled.IsCheckedChanged += (_, _) => UpdateActions();
        DockPanel.SetDock(header, Dock.Top);
        Root.Children.Add(header);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
        _reapply = Action("ReapplySparse", "Refresh working copy", () => RunAsync("Refresh working copy", async token => { await Repository.ReapplySparseCheckoutAsync(token); Status.Text = T("Working copy refreshed"); }, changes: true));
        buttons.Children.Add(_reapply);
        buttons.Children.Add(Action("SaveSparse", "Save", SaveAsync));
        buttons.Children.Add(Action("CancelSparse", "Cancel", () => { Close(); return Task.CompletedTask; }));
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        _rules.Margin = new Thickness(10, 0, 10, 10);
        Root.Children.Add(_rules);
        Track(_enabled, _cone, _sparseIndex, _rules);
        KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.Control) { args.Handled = true; await SaveAsync(); }
        };
    }

    public override Task InitializeAsync() => RunAsync("Sparse checkout status", async token =>
    {
        _state = await Repository.GetSparseCheckoutAsync(token);
        _enabled.IsChecked = _state.Enabled;
        _cone.IsChecked = !_state.Enabled || _state.ConeMode;
        _sparseIndex.IsChecked = _state.SparseIndex;
        _rules.Text = string.Join('\n', _state.Patterns);
        Status.Text = T(_state.Enabled ? "Sparse checkout is enabled." : "Sparse checkout is disabled.");
    });

    private async Task SaveAsync()
    {
        bool enabled = _enabled.IsChecked == true;
        bool cone = _cone.IsChecked == true;
        bool sparseIndex = _sparseIndex.IsChecked == true;
        string[] lines = (_rules.Text ?? "").Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToArray();
        if (_state?.Enabled == true && !enabled && !await Dialogs.ConfirmAsync(this, T("Disable sparse checkout"), T("Restore the full working copy for this repository?"))) { return; }
        await RunAsync("Save sparse checkout", async token =>
        {
            if (!enabled) { await Repository.DisableSparseCheckoutAsync(token); }
            else if (cone) { await Repository.SetSparseCheckoutAsync(lines, sparseIndex, token); }
            else { await Repository.SetSparsePatternsAsync(lines, token); }
            _state = await Repository.GetSparseCheckoutAsync(token);
            _rules.Text = string.Join('\n', _state.Patterns);
            Status.Text = T("Sparse checkout saved");
        }, changes: true);
    }

    protected override void UpdateActions()
    {
        _cone.IsEnabled = _rules.IsEnabled = !IsBusy && _enabled.IsChecked == true;
        _sparseIndex.IsEnabled = _cone.IsEnabled && _cone.IsChecked == true;
        _reapply.IsEnabled = !IsBusy && _state?.Enabled == true;
    }
}

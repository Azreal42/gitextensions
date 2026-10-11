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
    private CancellationTokenSource? _operation;
    private bool _closeWhenIdle;
    protected readonly GitRepository Repository;
    protected readonly TextBlock Status = new() { Name = "OperationStatus", TextWrapping = TextWrapping.Wrap };
    protected readonly DockPanel Root = new() { LastChildFill = true };

    protected RepositoryDialog(GitRepository repository, string title)
    {
        Repository = repository;
        Title = T(title);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = Root;
        Border status = ClassicChrome.Border(Status, new Thickness(0, 1, 0, 0), new Thickness(10, 5));
        DockPanel.SetDock(status, Dock.Bottom);
        Root.Children.Add(status);
        Closing += (_, args) =>
        {
            if (IsBusy) { args.Cancel = true; _closeWhenIdle = true; _operation?.Cancel(); }
        };
        Closed += (_, _) => _lifetime.Cancel();
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
    }

    internal bool IsBusy { get; private set; }
    public bool HasRepositoryChanges { get; private set; }
    public string? LastError { get; private set; }
    internal Exception? LastException { get; private set; }
    public abstract Task InitializeAsync();
    protected string T(string source) => _translations.Translate(source);
    protected void Track(params Control[] controls) => _inputs.AddRange(controls);
    protected void MarkRepositoryChanged() => HasRepositoryChanges = true;

    protected Button Action(string name, string label, Func<Task> action)
    {
        Button button = new() { Name = name, Content = T(label), HorizontalAlignment = HorizontalAlignment.Stretch };
        Track(button);
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception exception) { LastException = exception; LastError = exception.Message; Status.Text = exception.Message; }
        };
        return button;
    }

    protected async Task RunAsync(string label, Func<CancellationToken, Task> action, bool changes = false)
    {
        if (IsBusy) { return; }
        IsBusy = true;
        LastError = null;
        LastException = null;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        foreach (Control input in _inputs) { input.IsEnabled = false; }
        Status.Text = T(label) + "…";
        try
        {
            await action(_operation.Token);
            HasRepositoryChanges |= changes;
        }
        catch (OperationCanceledException) { LastError = T("Operation cancelled"); Status.Text = LastError; }
        catch (Exception exception) { LastException = exception; LastError = exception.Message; Status.Text = exception.Message; }
        finally
        {
            _operation.Dispose();
            _operation = null;
            IsBusy = false;
            foreach (Control input in _inputs) { input.IsEnabled = true; }
            UpdateActions();
            if (_closeWhenIdle) { Close(); }
        }
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
    private readonly TextBox _revision = new() { Name = "BisectRevision", Watermark = "Revision (blank for current)" };
    private readonly TextBox _log = new() { Name = "BisectLog", IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("monospace"), MinHeight = 100 };
    private readonly Button _start;
    private readonly Button _good;
    private readonly Button _bad;
    private readonly Button _skip;
    private readonly Button _stop;
    private GitBisectState? _state;

    public BisectDialog(GitRepository repository) : base(repository, "Bisect")
    {
        Width = 330;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        StackPanel actions = new() { Margin = new Thickness(12), Spacing = 4 };
        _start = Action("Start", "Start bisect", StartAsync);
        _bad = Action("Bad", "Mark current revision bad", () => MarkAsync(GitBisectOption.Bad));
        _good = Action("Good", "Mark current revision good", () => MarkAsync(GitBisectOption.Good));
        _skip = Action("Skip", "Skip current revision", () => MarkAsync(GitBisectOption.Skip));
        _stop = Action("Stop", "Stop bisect", () => RunAsync("Stop bisect", async token => { await Repository.ResetBisectAsync(cancellationToken: token); await ReadStateAsync(token); }, changes: true));
        foreach (Button button in new[] { _start, _bad, _good, _skip, _stop }) { button.Height = 25; actions.Children.Add(button); }
        _revision.Margin = new Thickness(0, 8);
        _revision.Watermark = T("Revision (blank for current)");
        Track(_revision);
        actions.Children.Add(_revision);
        actions.Children.Add(new Expander { Header = T("Bisect log"), Content = _log, HorizontalAlignment = HorizontalAlignment.Stretch });
        Root.Children.Add(actions);
    }

    public override Task InitializeAsync() => RunAsync("Bisect status", ReadStateAsync);

    private async Task StartAsync()
    {
        string[]? values = await Dialogs.PromptAsync(this, T("Start bisect"), (T("Bad revision (optional)"), ""), (T("Good revision (optional)"), ""));
        if (values is null) { return; }
        await RunAsync("Start bisect", async token =>
        {
            await Repository.StartBisectAsync(string.IsNullOrWhiteSpace(values[0]) ? null : values[0],
                string.IsNullOrWhiteSpace(values[1]) ? null : [values[1]], cancellationToken: token);
            await ReadStateAsync(token);
        }, changes: true);
    }

    private Task MarkAsync(GitBisectOption verdict) => RunAsync("Bisect", async token =>
    {
        await Repository.MarkBisectAsync(verdict, string.IsNullOrWhiteSpace(_revision.Text) ? null : _revision.Text, token);
        _revision.Text = null;
        await ReadStateAsync(token);
    }, changes: true);

    private async Task ReadStateAsync(CancellationToken token)
    {
        _state = await Repository.GetBisectStateAsync(token);
        _log.Text = _state.Log;
        Status.Text = _state.FirstBadRevision is not null ? T("First bad commit") + ": " + _state.FirstBadRevision
            : _state.IsInconclusive ? T("Only skipped commits remain.")
            : _state.IsActive ? T("Current revision") + ": " + _state.CurrentRevision
            : T("No bisect is active.");
    }

    protected override void UpdateActions()
    {
        _start.IsEnabled = _state?.IsActive != true;
        _stop.IsEnabled = _state?.IsActive == true;
        bool classifiable = _state?.IsActive == true && _state.FirstBadRevision is null && !_state.IsInconclusive;
        _good.IsEnabled = _bad.IsEnabled = _skip.IsEnabled = _revision.IsEnabled = classifiable;
    }
}

public sealed class SparseCheckoutDialog : RepositoryDialog
{
    private readonly CheckBox _enabled = new() { Name = "SparseEnabled" };
    private readonly CheckBox _cone = new() { Name = "SparseCone" };
    private readonly CheckBox _sparseIndex = new() { Name = "SparseIndex" };
    private readonly TextBox _rules = new() { Name = "SparseRules", AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace") };
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

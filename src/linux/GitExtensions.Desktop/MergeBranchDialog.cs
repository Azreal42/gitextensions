using GitExtensions.Linux.Scripts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

/// <summary>Compact merge window retaining the original choices, advanced options and diagrams.</summary>
public sealed class MergeBranchDialog : RepositoryDialog
{
    private readonly ComboBox _revision = new() { Name = "MergeRevision", IsEditable = true, Height = 24, Padding = new Thickness(4, 2), HorizontalAlignment = HorizontalAlignment.Stretch, IsTextSearchEnabled = false };
    private readonly TextBlock _branch = new() { Name = "MergeCurrentBranch", FontWeight = FontWeight.Bold };
    private readonly RadioButton _ff = new() { Name = "MergeFastForward", GroupName = "MergeMode" };
    private readonly RadioButton _noff = new() { Name = "MergeNoFastForward", GroupName = "MergeMode" };
    private readonly CheckBox _noCommit = new() { Name = "MergeNoCommit" }, _advanced = new() { Name = "MergeAdvanced" };
    private readonly CheckBox _strategyEnabled = new() { Name = "MergeUseStrategy" }, _squash = new() { Name = "MergeSquash" }, _unrelated = new() { Name = "MergeUnrelated" }, _log = new() { Name = "MergeAddLog" }, _customMessage = new() { Name = "MergeCustomMessage" };
    private readonly ComboBox _strategy = new() { Name = "MergeStrategy", IsEditable = true, Height = 24, Padding = new Thickness(4, 2), ItemsSource = new[] { "resolve", "recursive", "octopus", "ours", "subtree" } };
    private readonly NumericUpDown _count = new() { Name = "MergeLogCount", Minimum = 0, Maximum = 100, Value = 20, Height = 24, Width = 65 };
    private readonly TextBox _message = new() { Name = "MergeMessage", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 72 };
    private readonly StackPanel _advancedPanel;
    private readonly OriginalCommandHelp? _help;
    private readonly Grid _body;
    private readonly Button _merge, _strategyHelp;
    private readonly MergePreferencesStore _store = new();
    private readonly string? _initialRevision;
    private readonly Func<Window, GitRepository, bool, CancellationToken, Task>? _resolve;
    private readonly Func<Window, CancellationToken, Task>? _afterSubmodules;
    private readonly CancellationTokenSource _lifetime;
    private string _settingsKey = "";
    private bool _loaded, _postProcessing, _savingLog, _invalidLogCount;
    private GitDistributedSettingsSnapshot? _logSettings;
    private MergePreferences? _legacyLog;
    private (bool AddLog, int Count) _acceptedLog;
    private (bool AddLog, int Count)? _pendingLog;
    private Task _logSaveTask = Task.CompletedTask;
    internal Task LogPreferencesSettled => _logSaveTask;
    public GitMergeResult? Result { get; private set; }

    public MergeBranchDialog(GitRepository repository, string? revision = null,
        Func<Window, GitRepository, bool, CancellationToken, Task>? resolveConflictsAsync = null,
        Func<Window, CancellationToken, Task>? afterSubmoduleUpdate = null, CancellationToken cancellationToken = default) : base(repository, "Merge")
    {
        _initialRevision = revision; _resolve = resolveConflictsAsync; _afterSubmodules = afterSubmoduleUpdate;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool showHelpImages = OriginalCommandHelpVisibility.Capture();
        Name = "MergeBranchDialog"; Title = Text("$this", "Merge"); Width = showHelpImages ? 790 : 500; MinWidth = showHelpImages ? 790 : 500; Height = 530; MinHeight = 450;
        _ff.Content = Text("fastForward", "Keep a single branch line if possible (fast forward)");
        _noff.Content = Text("noFastForward", "Always create a new merge commit");
        Set(_noCommit, "noCommit", "Do not commit"); Set(_advanced, "advanced", "Show advanced options");
        Set(_strategyEnabled, "NonDefaultMergeStrategy", "Use non-default merge strategy"); Set(_squash, "squash", "Squash commits");
        Set(_unrelated, "allowUnrelatedHistories", "Allow unrelated histories"); Set(_log, "addLogMessages", "Add log messages"); Set(_customMessage, "addMergeMessage", "Specify merge message");
        ReferenceDialogControls.Compact(_ff, _noff, _noCommit, _advanced, _strategyEnabled, _squash, _unrelated, _log, _customMessage);
        Grid fields = ReferenceDialogControls.Fields(); ReferenceDialogControls.Field(fields, Text("label2", "Merge branch"), _revision); ReferenceDialogControls.Field(fields, Text("Currentbranch", "Into current branch"), _branch);
        _strategyHelp = Action("MergeStrategyHelp", "Help", () =>
        {
            LoggedExternalProcess.Start(new System.Diagnostics.ProcessStartInfo("xdg-open")
                { UseShellExecute = false, ArgumentList = { "https://git-extensions-documentation.readthedocs.io/en/latest/branches.html#advanced-merge-options" } }, GitCommandLogProcessKind.OpenResource);
            return Task.CompletedTask;
        });
        _strategyHelp.Height = 24;
        Grid strategyRow = new() { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 };
        strategyRow.Children.Add(_strategy); Grid.SetColumn(_strategyHelp, 1); strategyRow.Children.Add(_strategyHelp);
        _advancedPanel = new() { Name = "MergeAdvancedPanel", Spacing = 3, Children = { _strategyEnabled, strategyRow, _squash, _unrelated,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _log, _count } }, _customMessage, _message } };
        StackPanel choices = new() { Spacing = 3, Children = { fields, _ff, _noff, _noCommit, _advanced, _advancedPanel } };
        _help = showHelpImages ? new("HelpCommandMerge", "HelpCommandMergeFastForward", "MergeBranches", "Hover to see scenario when fast forward is possible.") : null;
        _body = new() { Margin = new Thickness(8), ColumnDefinitions = new ColumnDefinitions(showHelpImages ? "Auto,*" : "*"), ColumnSpacing = showHelpImages ? 8 : 0 };
        if (_help is not null) { _body.Children.Add(_help); }
        Border group = ExportDialogControls.Group(Text("groupBox1", "Merge"), choices); Grid.SetColumn(group, showHelpImages ? 1 : 0); _body.Children.Add(group);
        _merge = Action("ExecuteMerge", "Merge", ExecuteAsync); _merge.Content = new AccessText { Text = Translations.TranslateAccessKeys("FormMergeBranch", "Ok", "Text", "&Merge") }; _merge.IsDefault = true; _merge.MinWidth = 90; _merge.Height = 25;
        Button cancel = new() { Name = "CancelMerge", Content = T("Cancel"), IsCancel = true, MinWidth = 90, Height = 25 }; cancel.Click += (_, _) => Close();
        StackPanel buttons = ExportDialogControls.Buttons(_merge, cancel); DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons); Root.Children.Add(new ScrollViewer { Content = _body });
        Track(_revision, _ff, _noff, _noCommit, _advanced, _strategyEnabled, _strategy, _squash, _unrelated, _log, _count, _customMessage, _message);
        _ff.IsChecked = true;
        _advanced.IsChecked = AdvancedOptionsVisibility.Capture();
        _revision.PropertyChanged += (_, change) => { if (change.Property == ComboBox.TextProperty) { UpdateActions(); } };
        foreach (ToggleButton choice in new ToggleButton[] { _ff, _noff, _advanced, _strategyEnabled, _customMessage, _log })
        { choice.IsCheckedChanged += (_, _) => OptionsChanged(); }
        _count.ValueChanged += (_, _) =>
        {
            if (_loaded && _count.Value is >= 0 and <= 100)
            { _invalidLogCount = false; _count.Minimum = 0; _count.Maximum = 100; }
            SaveLogPreference(); UpdateActions();
        };
        if (_help is { } help)
        {
            MinWidth = help.IsExpanded ? 790 : 500;
            help.ExpandedChanged += (_, _) => { MinWidth = help.IsExpanded ? 790 : 500; Width = MinWidth; };
        }
        Closed += (_, _) => { _lifetime.Cancel(); _help?.Dispose(); };
        OptionsChanged();
        void Set(CheckBox box, string item, string source) => box.Content = Text(item, source);
    }
    private string Text(string item, string source) => Translations.Translate("FormMergeBranch", item, "Text", source);
    public GitMergeOptions ReadOptions() => new(_revision.Text ?? "", _ff.IsChecked == true, _squash.IsChecked == true,
        _noCommit.IsChecked == true, _strategyEnabled.IsChecked == true && !string.IsNullOrWhiteSpace(_strategy.Text) ? _strategy.Text : null,
        _unrelated.IsChecked == true, _customMessage.IsChecked == true ? _message.Text ?? "" : null, _log.IsChecked == true ? (int)(_count.Value ?? 20) : null);
    public override Task InitializeAsync() => RunAsync("Load branches", async token =>
    {
        GitRepositoryInfo info = await Repository.GetInfoAsync(token); _settingsKey = info.CommonDirectory;
        GitStatus status = await Repository.GetStatusAsync(token); IReadOnlyList<GitReference> refs = await Repository.GetReferencesAsync(token);
        _revision.ItemsSource = refs.Where(reference => reference.IsBranch || reference.IsRemote || reference.IsTag).Select(reference => reference.ShortName).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        _revision.Text = _initialRevision ?? refs.FirstOrDefault(reference => reference.IsCurrent)?.Upstream ?? ""; _branch.Text = status.Branch ?? "HEAD";
        MergePreferences settings = _store.Load(_settingsKey);
        _legacyLog = await Task.Run(() => _store.LegacyLog(_settingsKey), token);
        _logSettings = await DetailedRepositorySettings.LoadAsync(Repository, token);
        (bool log, int count) = ReadLogSettings(_logSettings);
        _noCommit.IsChecked = settings.NoCommit; _noff.IsChecked = settings.NoFastForward; _ff.IsChecked = !settings.NoFastForward;
        _log.IsChecked = log;
        // Detailed accepts every Int32. Keep an unrepresentable saved value visible;
        // require a deliberate correction here instead of clamping or rewriting it on load.
        _count.Minimum = Math.Min(0, count); _count.Maximum = Math.Max(100, count); _count.Value = count;
        _invalidLogCount = count is < 0 or > 100; _acceptedLog = (log, count);
        _loaded = !info.IsBare; OptionsChanged();
        Status.Text = _invalidLogCount ? T("The saved merge log count is outside 0–100. Choose a value in this dialog before merging. The saved setting has not been changed.") + " (" + count + ")" : "";
        Width = _help?.IsExpanded == true ? 790 : 500;
    }, cancellationToken: _lifetime.Token);
    private void OptionsChanged()
    {
        if (_advanced.IsChecked != true)
        { _strategyEnabled.IsChecked = false; _strategy.Text = ""; _squash.IsChecked = false; _unrelated.IsChecked = false; _customMessage.IsChecked = false; }
        if (_noff.IsChecked == true) { _squash.IsChecked = false; }
        _advancedPanel.IsVisible = _advanced.IsChecked == true; _strategy.IsVisible = _strategyEnabled.IsChecked == true;
        _strategyHelp.IsVisible = _strategyEnabled.IsChecked == true;
        if (_help is not null) { _help.UseAlternateOnHover = _ff.IsChecked == true; }
        SaveLogPreference(); UpdateActions();
    }
    private void SaveLogPreference()
    {
        if (!_loaded || IsBusy && !_savingLog || _invalidLogCount) { return; }
        (bool AddLog, int Count) desired = (_log.IsChecked == true, (int)(_count.Value ?? 20));
        if (desired == _acceptedLog && _pendingLog is null) { return; }
        _pendingLog = desired;
        if (_savingLog) { return; }
        _savingLog = true; _logSaveTask = SaveQueuedLogAsync();
    }
    private async Task SaveQueuedLogAsync()
    {
        try
        {
            await RunAsync("Save merge options", async token =>
            {
                while (_pendingLog is { } desired)
                { _pendingLog = null; await SaveLogAsync(desired, token); }
                Status.Text = "";
            }, cancellationToken: _lifetime.Token, disableInputs: false);
        }
        finally { _savingLog = false; }
    }
    private (bool AddLog, int Count) ReadLogSettings(GitDistributedSettingsSnapshot snapshot)
    {
        string? log = snapshot.GetValue(DetailedRepositorySettings.AddMergeLog), count = snapshot.GetValue(DetailedRepositorySettings.MergeLogCount);
        return (DetailedRepositorySettings.Boolean(log) ?? (log is null ? _legacyLog?.AddLog : null) ?? false,
            DetailedRepositorySettings.Number(count) ?? (count is null ? _legacyLog?.LogCount : null) ?? 20);
    }
    private void AcceptLogSettings(GitDistributedSettingsSnapshot accepted)
    {
        _logSettings = accepted;
        // A later file can fail. Compare subsequent edits with the values really
        // saved so reverting a control can also undo a completed earlier effect.
        _acceptedLog = ReadLogSettings(accepted);
    }
    private async Task SaveLogAsync((bool AddLog, int Count) desired, CancellationToken token)
    {
        GitDistributedSettingsSnapshot snapshot = _logSettings ?? throw new InvalidOperationException("Merge settings have not been loaded.");
        (bool AddLog, int Count) before = _acceptedLog;
        if (_legacyLog is not null)
        {
            snapshot = await DetailedRepositorySettings.ImportLegacyAsync(snapshot, desired.AddLog, desired.Count,
                AcceptLogSettings, token); _logSettings = snapshot;
        }
        Dictionary<string, string?> changes = new(StringComparer.Ordinal);
        if (desired.AddLog != before.AddLog || _legacyLog is not null && snapshot.GetValue(DetailedRepositorySettings.AddMergeLog) is null)
        { changes[DetailedRepositorySettings.AddMergeLog] = DetailedRepositorySettings.BooleanText(desired.AddLog); }
        if (desired.Count != before.Count || _legacyLog is not null && snapshot.GetValue(DetailedRepositorySettings.MergeLogCount) is null)
        { changes[DetailedRepositorySettings.MergeLogCount] = DetailedRepositorySettings.NumberText(desired.Count.ToString()); }
        if (changes.Count > 0)
        { _logSettings = await DetailedRepositorySettings.SaveEffectiveAsync(snapshot, changes, AcceptLogSettings, token); }
        if (_legacyLog is not null)
        {
            // Import only on an explicit edit or Merge, never on initialization/Cancel.
            // A marker in the old profile prevents removed XML overrides reviving old JSON values.
            await Task.Run(() => _store.MarkLogMigrated(_settingsKey), token); _legacyLog = null;
        }
        _acceptedLog = desired;
    }
    protected override void UpdateActions()
    {
        if (_merge is null) { return; }
        _merge.IsEnabled = _loaded && !_invalidLogCount && !IsBusy && !_postProcessing && !string.IsNullOrWhiteSpace(_revision.Text);
        _squash.IsEnabled = !IsBusy && !_postProcessing && _noff.IsChecked != true;
        _count.IsEnabled = (!IsBusy || _savingLog) && !_postProcessing && (_log.IsChecked == true || _invalidLogCount);
        _message.IsEnabled = !IsBusy && !_postProcessing && _customMessage.IsChecked == true;
    }
    private async Task ExecuteAsync()
    {
        await _logSaveTask;
        if (_invalidLogCount) { return; }
        if (IsBusy || _postProcessing || !_merge.IsEnabled || _lifetime.IsCancellationRequested) { return; }
        GitMergeOptions options = ReadOptions(); _postProcessing = true; UpdateActions();
        try
        {
            await RunAsync("Merge", async token =>
            {
                using ScriptWorkflowScope scripts = new(this, Repository, token);
                Result = null;
                if (!await RunScriptEventAsync(scripts, ScriptEvent.BeforeMerge)) { return; }
                await SaveLogAsync((_log.IsChecked == true, (int)(_count.Value ?? 20)), scripts.Token);
                await Task.Run(() => _store.SaveWorkflow(_settingsKey, options.NoCommit, !options.AllowFastForward), scripts.Token);
                scripts.ThrowIfStopped();
                MarkRepositoryChanged(); Result = await Repository.MergeAsync(options, scripts.Token);
                Status.Text = Result.Result.StandardOutput + Result.Result.StandardError;
                scripts.ThrowIfStopped();
                if (Result.ConflictedPaths.Count > 0 || !Result.Result.Success && Result.CanContinue)
                {
                    if (_resolve is not null) { await _resolve(this, Repository, !options.NoCommit, scripts.Token); }
                    else { await ResolveConflictsDialog.ShowAsync(this, Repository, scripts.Token); }
                }
                else if (_afterSubmodules is not null) { await _afterSubmodules(this, scripts.Token); }
                scripts.ThrowIfStopped();
                // Windows runs AfterMerge for a recognized conflict even when it was left unresolved.
                await RunScriptEventAsync(scripts, ScriptEvent.AfterMerge);
                scripts.ThrowIfStopped();
                Close();
            }, cancellationToken: _lifetime.Token);
        }
        finally { _postProcessing = false; UpdateActions(); }
    }
}

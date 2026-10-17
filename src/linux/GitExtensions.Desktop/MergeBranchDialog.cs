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
    private readonly OriginalCommandHelp _help;
    private readonly Grid _body;
    private readonly Button _merge, _strategyHelp;
    private readonly MergePreferencesStore _store = new();
    private readonly string? _initialRevision;
    private readonly Func<Window, GitRepository, bool, CancellationToken, Task>? _resolve;
    private readonly Func<Window, CancellationToken, Task>? _afterSubmodules;
    private readonly CancellationTokenSource _lifetime;
    private string _settingsKey = "";
    private bool _loaded, _postProcessing;
    public GitMergeResult? Result { get; private set; }

    public MergeBranchDialog(GitRepository repository, string? revision = null,
        Func<Window, GitRepository, bool, CancellationToken, Task>? resolveConflictsAsync = null,
        Func<Window, CancellationToken, Task>? afterSubmoduleUpdate = null, CancellationToken cancellationToken = default) : base(repository, "Merge")
    {
        _initialRevision = revision; _resolve = resolveConflictsAsync; _afterSubmodules = afterSubmoduleUpdate;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Name = "MergeBranchDialog"; Title = Text("$this", "Merge"); Width = 790; MinWidth = 500; Height = 530; MinHeight = 450;
        _ff.Content = Text("fastForward", "Keep a single branch line if possible (fast forward)");
        _noff.Content = Text("noFastForward", "Always create a new merge commit");
        Set(_noCommit, "noCommit", "Do not commit"); Set(_advanced, "advanced", "Show advanced options");
        Set(_strategyEnabled, "NonDefaultMergeStrategy", "Use non-default merge strategy"); Set(_squash, "squash", "Squash commits");
        Set(_unrelated, "allowUnrelatedHistories", "Allow unrelated histories"); Set(_log, "addLogMessages", "Add log messages"); Set(_customMessage, "addMergeMessage", "Specify merge message");
        ReferenceDialogControls.Compact(_ff, _noff, _noCommit, _advanced, _strategyEnabled, _squash, _unrelated, _log, _customMessage);
        Grid fields = ReferenceDialogControls.Fields(); ReferenceDialogControls.Field(fields, Text("label2", "Merge branch"), _revision); ReferenceDialogControls.Field(fields, Text("Currentbranch", "Into current branch"), _branch);
        _strategyHelp = Action("MergeStrategyHelp", "Help", () =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open")
                { UseShellExecute = false, ArgumentList = { "https://git-extensions-documentation.readthedocs.io/en/latest/branches.html#advanced-merge-options" } });
            return Task.CompletedTask;
        });
        _strategyHelp.Height = 24;
        Grid strategyRow = new() { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 };
        strategyRow.Children.Add(_strategy); Grid.SetColumn(_strategyHelp, 1); strategyRow.Children.Add(_strategyHelp);
        _advancedPanel = new() { Name = "MergeAdvancedPanel", Spacing = 3, Children = { _strategyEnabled, strategyRow, _squash, _unrelated,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _log, _count } }, _customMessage, _message } };
        StackPanel choices = new() { Spacing = 3, Children = { fields, _ff, _noff, _noCommit, _advanced, _advancedPanel } };
        _help = new("HelpCommandMerge", "HelpCommandMergeFastForward", "MergeBranches", "Hover to see scenario when fast forward is possible.");
        _body = new() { Margin = new Thickness(8), ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        _body.Children.Add(_help); Border group = ExportDialogControls.Group(Text("groupBox1", "Merge"), choices); Grid.SetColumn(group, 1); _body.Children.Add(group);
        _merge = Action("ExecuteMerge", "Merge", ExecuteAsync); _merge.Content = new AccessText { Text = Translations.TranslateAccessKeys("FormMergeBranch", "Ok", "Text", "&Merge") }; _merge.IsDefault = true; _merge.MinWidth = 90; _merge.Height = 25;
        Button cancel = new() { Name = "CancelMerge", Content = T("Cancel"), IsCancel = true, MinWidth = 90, Height = 25 }; cancel.Click += (_, _) => Close();
        StackPanel buttons = ExportDialogControls.Buttons(_merge, cancel); DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons); Root.Children.Add(new ScrollViewer { Content = _body });
        Track(_revision, _ff, _noff, _noCommit, _advanced, _strategyEnabled, _strategy, _squash, _unrelated, _log, _count, _customMessage, _message);
        _ff.IsChecked = true;
        _revision.PropertyChanged += (_, change) => { if (change.Property == ComboBox.TextProperty) { UpdateActions(); } };
        foreach (ToggleButton choice in new ToggleButton[] { _ff, _noff, _advanced, _strategyEnabled, _customMessage, _log })
        { choice.IsCheckedChanged += (_, _) => OptionsChanged(); }
        _count.ValueChanged += (_, _) => SaveLogPreference();
        _help.ExpandedChanged += (_, _) => Width = _help.IsExpanded ? 790 : 500;
        Closed += (_, _) => { _lifetime.Cancel(); _help.Dispose(); };
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
        MergePreferences settings = _store.Load(_settingsKey); _noCommit.IsChecked = settings.NoCommit; _noff.IsChecked = settings.NoFastForward; _ff.IsChecked = !settings.NoFastForward; _log.IsChecked = settings.AddLog; _count.Value = settings.LogCount;
        _loaded = !info.IsBare; OptionsChanged(); Status.Text = ""; Width = _help.IsExpanded ? 790 : 500;
    }, cancellationToken: _lifetime.Token);
    private void OptionsChanged()
    {
        if (_advanced.IsChecked != true)
        { _strategyEnabled.IsChecked = false; _strategy.Text = ""; _squash.IsChecked = false; _unrelated.IsChecked = false; _customMessage.IsChecked = false; }
        if (_noff.IsChecked == true) { _squash.IsChecked = false; }
        _advancedPanel.IsVisible = _advanced.IsChecked == true; _strategy.IsVisible = _strategyEnabled.IsChecked == true;
        _strategyHelp.IsVisible = _strategyEnabled.IsChecked == true;
        _help.UseAlternateOnHover = _ff.IsChecked == true;
        SaveLogPreference(); UpdateActions();
    }
    private void SaveLogPreference()
    {
        if (!_loaded || IsBusy) { return; }
        try { MergePreferences previous = _store.Load(_settingsKey); _store.Save(_settingsKey, previous with { AddLog = _log.IsChecked == true, LogCount = (int)(_count.Value ?? 20) }); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Status.Text = exception.Message; }
    }
    protected override void UpdateActions()
    {
        if (_merge is null) { return; }
        _merge.IsEnabled = _loaded && !IsBusy && !_postProcessing && !string.IsNullOrWhiteSpace(_revision.Text);
        _squash.IsEnabled = !IsBusy && !_postProcessing && _noff.IsChecked != true;
        _count.IsEnabled = !IsBusy && !_postProcessing && _log.IsChecked == true;
        _message.IsEnabled = !IsBusy && !_postProcessing && _customMessage.IsChecked == true;
    }
    private async Task ExecuteAsync()
    {
        if (IsBusy || _postProcessing || !_merge.IsEnabled || _lifetime.IsCancellationRequested) { return; }
        GitMergeOptions options = ReadOptions(); _postProcessing = true; UpdateActions();
        try
        {
            await RunAsync("Merge", async token =>
            {
                using ScriptWorkflowScope scripts = new(this, Repository, token);
                Result = null;
                if (!await RunScriptEventAsync(scripts, ScriptEvent.BeforeMerge)) { return; }
                _store.Save(_settingsKey, new(options.NoCommit, !options.AllowFastForward, _log.IsChecked == true, (int)(_count.Value ?? 20)));
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

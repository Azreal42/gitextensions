using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Scripts;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private IReadOnlyList<ScriptDefinition> _scripts = [];
    private CompactBrowserToolbar? _scriptsToolbar;
    private readonly List<Control> _scriptActions = [];
    private IReadOnlyList<object> _revisionScriptItems = [];

    private void InitializeScripts()
    {
        ScriptsPreferencesLoadResult loaded = new ScriptsPreferencesStore().Load();
        _scripts = loaded.Scripts;
        if (loaded.Error is { } error) { AppendOutput("Unable to read scripts: " + error.Message); }
        ScriptsPreferencesStore.Saved += ScriptsSaved;
        Closed += (_, _) => ScriptsPreferencesStore.Saved -= ScriptsSaved;
    }
    private void ScriptsSaved(string file)
    {
        if (file != new ScriptsPreferencesStore().FilePath) { return; }
        void Update()
        {
            if (_lifetime.IsCancellationRequested) { return; }
            _scripts = new ScriptsPreferencesStore().Load().Scripts;
            DesktopHotkeys.Reload();
            if (_scriptsToolbar is { } previous) { _repositoryToolbars?.Children.Remove(previous); }
            if (_repositoryToolbars is not null) { _repositoryToolbars.Children.Add(BuildScriptsToolbar()); }
            RefreshRevisionScriptMenu();
        }
        if (Dispatcher.UIThread.CheckAccess()) { Update(); } else { Dispatcher.UIThread.Post(Update); }
    }
    private Control BuildScriptsToolbar()
    {
        _scriptActions.Clear();
        CompactBrowserToolbar toolbar = new("ScriptsToolbar"); _scriptsToolbar = toolbar;
        foreach (ScriptDefinition script in _scripts.Where(script => script.Enabled && script.OnEvent == ScriptEvent.ShowInUserMenuBar))
        {
            StackPanel label = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
            if (ScriptPresentation.Icon(script) is { } icon) { label.Children.Add(icon); }
            label.Children.Add(ScriptPresentation.Header(script));
            Button button = new() { Name = "ScriptToolbar_" + script.HotkeyCommandIdentifier, Content = label,
                Height = DesktopFontMetrics.ToolbarHeight, MinHeight = DesktopFontMetrics.ToolbarHeight, Padding = new Avalonia.Thickness(3, 0) };
            button.Click += async (_, _) => await RunScriptAsync(script);
            DesktopHotkeys.BindScriptToolTip(button, script.HotkeyCommandIdentifier, script.GetDisplayName());
            MenuItem overflow = ScriptMenuItems.Create(script, () => RunScriptAsync(script));
            _scriptActions.Add(button); _scriptActions.Add(overflow); toolbar.Add(button, overflow);
        }
        RefreshScriptsEnabled(); return toolbar;
    }
    private void RefreshScriptsEnabled()
    {
        foreach (Control action in _scriptActions) { action.IsEnabled = !_busy && _repository is not null && !_lifetime.IsCancellationRequested; }
        if (_scriptsToolbar is not null) { _scriptsToolbar.IsVisible = _historyView.ShowScriptsToolbar && _scriptActions.Count != 0; }
    }
    private void RefreshRevisionScriptMenu()
    {
        if (_revisionContext is null) { return; }
        object[] scripts = ScriptMenuItems.Build(_scripts, script => script.AddToRevisionGridContextMenu,
            script => () => RunScriptAsync(script), !_busy && _repository is not null, _translations, "RevisionGridControl");
        _revisionScriptItems = ScriptMenuItems.ReplaceOwned(_revisionContext, _revisionScriptItems, scripts);
    }
    internal ScriptHostSelection CaptureScriptSelection(FileMenuSelection? files = null)
    {
        GitCommit[] selected = _commits.SelectedItems?.OfType<GitCommit>().ToArray() ?? [];
        GitCompareRevision? latest = CurrentRelativeRevision();
        GitCommit? latestCommit = latest?.Kind == GitCompareRevisionKind.Commit
            ? _history.FirstOrDefault(commit => commit.Hash == latest.Revision) : null;
        if (latest is { Kind: GitCompareRevisionKind.Index or GitCompareRevisionKind.WorkingTree })
        {
            latestCommit = new((latest.Kind == GitCompareRevisionKind.Index ? ObjectId.IndexId : ObjectId.WorkTreeId).ToString(),
                [], "", "", DateTimeOffset.MaxValue, latest.Kind == GitCompareRevisionKind.Index ? T("Commit index") : T("Working directory"), "");
            selected = [latestCommit];
        }
        if (files is null)
        {
            files = (_tabs.SelectedItem as TabItem)?.Name switch
            {
                "FileTreeTab" => GetFileMenuSelection(true),
                "DiffTab" => GetFileMenuSelection(false),
                _ => null
            };
        }
        var viewer = files?.IsFileTree == true ? _fileContent?.TextView : _commitContent?.TextView;
        return new(true, latestCommit, selected, files,
            files is null ? null : viewer?.CurrentFileLine,
            files is null ? null : viewer?.Editor.TextArea.Caret.Column);
    }
    internal async Task RunScriptAsync(ScriptDefinition script, FileMenuSelection? files = null, Window? owner = null)
    {
        GitRepository? repository = _repository;
        if (repository is null || _busy || files is not null && !IsCurrentFileMenuSelection(files)) { return; }
        await RunAsync(script.GetDisplayName(), async current =>
        {
            using IDisposable operation = current.BeginOperation();
            try { await DesktopScriptExecutor.ExecuteAsync(owner ?? WorkflowOwner, current, script, Token, files); }
            finally
            {
                // A script may have changed files before returning a failure.
                if (!script.RunInBackground && !Token.IsCancellationRequested && _repository == current)
                { await RefreshDataAsync(current); }
            }
        }, refresh: false);
    }
    internal void ReportScriptOutput(string text)
    {
        if (Dispatcher.UIThread.CheckAccess()) { AppendOutput(text); }
        else { Dispatcher.UIThread.Post(() => AppendOutput(text)); }
    }
    internal async Task<bool> NavigateScriptOutputAsync(GitRepository repository, string revision, CancellationToken token)
    {
        if (_repository != repository || _lifetime.IsCancellationRequested) { return false; }
        string hash;
        try { hash = await repository.ResolveRevisionAsync(revision, token); }
        catch (GitCommandException)
        {
            token.ThrowIfCancellationRequested();
            if (_repository != repository || _lifetime.IsCancellationRequested) { return false; }
            await ShowGoToCommitErrorAsync("_noRevisionFoundError", "No revision found.");
            return false;
        }
        token.ThrowIfCancellationRequested();
        if (_repository != repository) { return false; }
        if (!_history.Any(commit => commit.Hash == hash)) { await ShowFilteredRevisionErrorAsync(hash); return false; }
        SelectVisibleRevision(hash); return true;
    }
    internal async Task<bool> ExecuteScriptPluginAsync(GitRepository repository, string name, Window owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_repository != repository || _lifetime.IsCancellationRequested || _pluginDialogOpen || !owner.IsVisible) { return false; }
        var plugin = GitExtensions.Linux.Plugins.NativePluginCatalog.BuiltIn.Concat(LifecycleSettingsPlugins())
            .FirstOrDefault(plugin => string.Equals(plugin.Descriptor.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (plugin is null) { return false; }
        await ShowPluginAsync(plugin, fromScript: true, owner: owner, cancellationToken: token); return true;
    }
    private async Task<bool> HandleScriptHotkeyAsync(KeyEventArgs args, Action closePopup)
    {
        HotkeyCommand? command = DesktopHotkeys.Match("Scripts", args);
        if (command is null || _repository is null) { return false; }
        ScriptDefinition? script = _scripts.FirstOrDefault(script => script.HotkeyCommandIdentifier == command.Code);
        if (script is null) { return false; }
        // Windows dispatch by ID permits disabled scripts as explicitly assigned commands.
        args.Handled = true; closePopup(); await RunScriptAsync(script); return true;
    }
}

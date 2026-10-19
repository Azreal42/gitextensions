using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.LogicalTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Avatars;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly List<(MenuItem Item, bool Worktree)> _externalToolMenus = [];
    private GitCommandLogWindow? _commandLogWindow;
    internal Func<ProcessStartInfo, Process?> ExternalLauncher { get; set; } = Process.Start;
    private static string DesktopVersion => typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "7.2.1-linux-alpha";
    private const string ForkUrl = "https://github.com/Azreal42/gitextensions";

    private MenuItem OriginalAction(string category, string name, string label, string? icon, Func<Task> action, KeyGesture? gesture = null)
    {
        MenuItem item = OriginalMenuItems.Create(_translations, category, name, label, icon, gesture);
        if (category == "FormBrowse" && (name switch
        {
            "gitBashToolStripMenuItem" => "GitBash", "gitGUIToolStripMenuItem" => "GitGui", "kGitToolStripMenuItem" => "GitGitK",
            "settingsToolStripMenuItem" => "OpenSettings", _ => null
        }) is { } command) { DesktopHotkeys.Bind(item, "Browse", command); }
        item.Click += async (_, args) =>
        {
            if (args.Source != item || !item.IsEnabled) { return; }
            args.Handled = true;
            try { await WithMenuActionFocusAsync(item, action); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                AppendOutput(error.Message); _state.Text = error.Message;
                if (IsVisible && !_lifetime.IsCancellationRequested) { await Dialogs.ShowErrorAsync(this, error.Message); }
            }
        };
        return item;
    }

    private MenuItem BuildToolsMenu()
    {
        const string category = "FormBrowse";
        MenuItem Tool(string name, string label, string icon, Func<Task> action, bool worktree = false, KeyGesture? gesture = null)
        {
            MenuItem item = OriginalAction(category, name, label, icon, () =>
                !_busy && _repository is not null && !(worktree && _repository.IsBare) ? action() : Task.CompletedTask, gesture);
            _externalToolMenus.Add((item, worktree));
            return item;
        }
        MenuItem menu = new() { Name = "ToolsMenu", Header = T("Tools"), ItemsSource = new object[]
        {
            Tool("gitBashToolStripMenuItem", "Git &bash", "GitForWindows.png", OpenTerminalAsync, gesture: new(Key.G, KeyModifiers.Control)),
            Tool("gitGUIToolStripMenuItem", "Git &GUI", "GitGui.png", () => LaunchGitToolAsync("gui"), worktree: true),
            Tool("kGitToolStripMenuItem", "Git&K", "Gitk.png", () => LaunchGitToolAsync("gitk")),
            // The original ToolsToolStripMenuItem also hides PuTTY and its separator on non-Windows.
            new Separator(),
            OriginalAction(category, "gitcommandLogToolStripMenuItem", "Git &command log", "GitCommandLog.png", ShowCommandLogAsync, new(Key.F12)),
            new Separator(),
            OriginalAction(category, "settingsToolStripMenuItem", "&Settings...", "Settings.png", ShowSettingsAsync, new(Key.OemComma, KeyModifiers.Control))
        }};
        menu.SubmenuOpened += (_, args) => { if (args.Source == menu) { RefreshExternalToolMenus(); } };
        RefreshExternalToolMenus();
        return menu;
    }

    private void RefreshExternalToolMenus()
    {
        foreach ((MenuItem item, bool worktree) in _externalToolMenus)
        { item.IsEnabled = !_busy && _repository is not null && !(worktree && _repository.IsBare); }
    }

    private async Task LaunchGitToolAsync(string tool)
    {
        if (_repository is null || _busy || (tool == "gui" && _repository.IsBare)) { return; }
        GitExecutionEnvironment environment = _repository.ExecutionEnvironment;
        string executable = tool == "gitk" ? "gitk" : environment.Executable;
        if (!string.IsNullOrEmpty(environment.Options.ToolsDirectory) && !executable.Contains(Path.DirectorySeparatorChar))
        {
            executable = (environment.GetVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(directory => Path.GetFullPath(Path.Combine(directory.Length == 0 ? "." : directory, executable), _repository.Path))
                .FirstOrDefault(path => File.Exists(path) && (OperatingSystem.IsWindows()
                    || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
                ?? throw new FileNotFoundException("Cannot find the requested Git tool in the configured PATH: " + executable);
        }
        ProcessStartInfo info = new(executable) { WorkingDirectory = _repository.Path, UseShellExecute = false };
        environment.ApplyTo(info);
        if (tool == "gui") { info.ArgumentList.Add("gui"); }
        using Process? process = ExternalLauncher(info);
        if (process is null) { return; }
        await process.WaitForExitAsync(_lifetime.Token);
        if (process.ExitCode != 0) { throw new InvalidOperationException(T(tool == "gui" ? "Git GUI could not be started. Check that git-gui is installed." : "GitK could not be started. Check that gitk is installed.")); }
    }

    private MenuItem BuildHelpMenu()
    {
        const string category = "FormBrowse";
        MenuItem telemetry = OriginalAction(category, "tsmiTelemetryEnabled", "&Yes, I allow telemetry", null, ShowSettingsAsync);
        // No collector is shipped by this alpha. Opening Settings is the original
        // action, but a checked item would falsely imply active consent/collection.
        telemetry.IsChecked = false;
        return new MenuItem { Name = "HelpMenu", Header = T("Help"), ItemsSource = new object[]
        {
            OriginalAction(category, "userManualToolStripMenuItem", "User &manual", "GotoManual.png", () => OpenWebAsync("https://git-extensions-documentation.readthedocs.io/")),
            OriginalAction(category, "changelogToolStripMenuItem", "&Changelog", "Changelog.png", ShowChangelogAsync),
            new Separator(),
            OriginalAction(category, "translateToolStripMenuItem", "&Translate", "Translate.png", () => OpenWebAsync("https://github.com/gitextensions/gitextensions/wiki/Translations")),
            new Separator(),
            OriginalAction(category, "donateToolStripMenuItem", "&Donate", "DollarSign.png", ShowDonateAsync),
            telemetry,
            OriginalAction(category, "reportAnIssueToolStripMenuItem", "&Report an issue", "BugReport.png", ReportIssueAsync),
            OriginalAction(category, "checkForUpdatesToolStripMenuItem", "Check for &updates", "CheckForUpdates.png", () => OpenWebAsync(ForkUrl + "/releases")),
            OriginalAction(category, "aboutToolStripMenuItem", "&About", "information.png", ShowAboutAsync)
        }};
    }

    private Task OpenWebAsync(string url)
    {
        ExternalLauncher(new ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { url } });
        return Task.CompletedTask;
    }

    private async Task ReportIssueAsync()
    {
        if (Clipboard is not null)
        { await Clipboard.SetTextAsync("Git Extensions " + DesktopVersion + "\n" + RuntimeInformation.OSDescription + "\n" + RuntimeInformation.FrameworkDescription + "\n" + RuntimeInformation.ProcessArchitecture); }
        await OpenWebAsync(ForkUrl + "/issues");
    }

    private Task ShowChangelogAsync()
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/ChangeLog.md"));
        using StreamReader reader = new(stream);
        return ShowHelpTextAsync("Changelog", reader.ReadToEnd());
    }

    private Task ShowDonateAsync() => new DonateDialog(_translations, uri => _ = OpenWebAsync(uri.AbsoluteUri))
        { RequestedThemeVariant = ActualThemeVariant }.ShowDialog(this);

    private Task ShowAboutAsync() => new DesktopInformationWindow(T("About"),
        "Git Extensions " + DesktopVersion + "\n\nLinux / Avalonia\n" + ForkUrl
        + "\n\nCopyright © 2008–2026 Git Extensions Team\nGPL-3.0-or-later",
        owner => new ContributorsDialog(_translations).ShowDialog(owner)).ShowDialog(this);

    private Task ShowHelpTextAsync(string title, string text) => new DesktopInformationWindow(T(title), text).ShowDialog(this);

    private Task ShowCommandLogAsync()
    {
        if (_commandLogWindow is { IsVisible: true }) { _commandLogWindow.Activate(); return Task.CompletedTask; }
        _commandLogWindow = new GitCommandLogWindow(_translations);
        _commandLogWindow.Show(this);
        return Task.CompletedTask;
    }

    private Task ShowSettingsAsync() => ShowSettingsPageAsync(showChecklist: false);

    private async Task ShowSettingsPageAsync(bool showChecklist)
    {
        if (_busy) { return; }
        FileHistoryPreferences fileHistory = _fileHistoryPreferencesStore.Load();
        HotkeyBindings hotkeys = DesktopHotkeys.Reload();
        DesktopPreferences initial = _preferencesStore.Load();
        GeneralSettingsSession general = new(initial, fileHistory);
        AppearanceSettingsSession appearance = new(initial, _historyView);
        // Reuse the runtime already bound to this mapped Main, including explicitly bound hosts.
        // Peeking the App fallback never constructs a service or initiates an avatar read.
        AvatarService? avatars = _avatarLifetime?.Scope?.Service ?? App.PeekAvatarRuntime()?.Service;
        DesktopFontSession fonts = DesktopFonts.CreateSession();
        GitRepository? settingsRepository = _repository;
        DistributedSettingsWorkspace workspace = new(settingsRepository);
        DetailedSettingsSession detailed = new(settingsRepository, initial, workspace);
        await detailed.InitializeAsync(_lifetime.Token);
        using GitEnvironmentSettingsSession environment = new(new GitEnvironmentPreferencesStore(), _translations, () => settingsRepository, _lifetime.Token);
        DesktopSettingsDialog dialog = new(initial, _translations, _repository is not null, _historyView.Sort, fileHistory,
            hotkeys, bindings => new HotkeyPreferencesStore().Save(bindings), fonts.Preferences,
            (baseline, draft) => new FontPreferencesStore().SaveChanges(baseline, draft), fonts.LoadError, new ScriptsPreferencesStore().Load(), (baseline, draft) => new ScriptsPreferencesStore().SaveChanges(baseline, draft),
            owner => ReferenceEquals(settingsRepository, _repository) && owner.IsVisible && !_lifetime.IsCancellationRequested
                ? ShowGitToolsSettingsAsync(owner) : Task.CompletedTask,
            environment, ApplySettingsAsync, RepairEnvironmentAsync, showChecklist,
            generalSettings: general.Initial,
            cloneDestinations: _repositoryHistory.Recent.Select(entry => Path.GetDirectoryName(entry.Path))
                .Where(path => !string.IsNullOrEmpty(path)).Select(path => path!).Distinct(StringComparer.Ordinal).ToArray(),
            appearanceSettings: appearance.Initial,
            helpTranslateAsync: owner => owner.IsVisible && !_lifetime.IsCancellationRequested
                ? OpenWebAsync(AppearanceSettingsPage.TranslationHelpUrl) : Task.CompletedTask,
            clearAvatarCacheAsync: avatars is null ? null : (owner, wait) =>
            {
                wait.ThrowIfCancellationRequested();
                return owner.IsVisible && !_lifetime.IsCancellationRequested
                    ? avatars.ClearCache().WaitAsync(wait) : Task.CompletedTask;
            },
            avatarHelpAsync: (owner, url) => owner.IsVisible && !_lifetime.IsCancellationRequested
                ? OpenWebAsync(url) : Task.CompletedTask,
            appearanceCancellationToken: environment.CancellationToken, detailedSettings: detailed,
            revisionLinksSettings: new(settingsRepository, workspace),
            gitHubActionsSettings: new(settingsRepository, workspace),
            openSettingsUrlAsync: OpenWebAsync,
            configureGitAdvanced: owner => ReferenceEquals(settingsRepository, _repository) && owner.IsVisible && !_lifetime.IsCancellationRequested
                ? ShowGitToolsSettingsAsync(owner, advanced: true) : Task.CompletedTask);
        bool accepted = await dialog.ShowDialog<bool>(this);
        if (dialog.OpenGitConfiguration) { await ShowWorkflowAsync("Configuration"); return; }
        if (!accepted) { return; }

        async Task RepairEnvironmentAsync(GitEnvironmentCheckId id, Window owner)
        {
            if (!owner.IsVisible || _lifetime.IsCancellationRequested) { return; }
            switch (id)
            {
                case GitEnvironmentCheckId.GlobalIdentity:
                case GitEnvironmentCheckId.Editor:
                case GitEnvironmentCheckId.MergeTool:
                case GitEnvironmentCheckId.DiffTool:
                    await ShowGitToolsSettingsAsync(owner,
                        id == GitEnvironmentCheckId.DiffTool ? GitToolRole.Diff : id == GitEnvironmentCheckId.MergeTool ? GitToolRole.Merge : null,
                        GitToolConfigurationView.Global,
                        id == GitEnvironmentCheckId.GlobalIdentity ? GitConfigurationField.UserName : id == GitEnvironmentCheckId.Editor ? GitConfigurationField.Editor : null);
                    await environment.ScanAsync();
                    break;
                case GitEnvironmentCheckId.Translation:
                    if (owner is DesktopSettingsDialog appearanceSettings) { appearanceSettings.Navigation.SelectPage("Appearance", focusContent: true); }
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => owner.GetLogicalDescendants().OfType<ComboBox>().FirstOrDefault(control => control.Name == "Language")?.Focus());
                    break;
                default:
                    if (owner is DesktopSettingsDialog settings) { settings.SelectGitEnvironmentPage(checklist: false); }
                    string control = id == GitEnvironmentCheckId.Home ? "ChangeHomeButton" : id == GitEnvironmentCheckId.GitExecutable ? "GitPath" : "LinuxToolsDir";
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => environment.Page.GetLogicalDescendants().OfType<Control>().FirstOrDefault(input => input.Name == control)?.Focus());
                    break;
            }
        }

        async Task ApplySettingsAsync(DesktopSettingsDialog settings)
        {
            using WindowWork work = TrackWindowWork();
            CancellationToken token = environment.CancellationToken;
            bool publishing = true;
            AvatarMutation? avatarMutation = null;
            Exception? avatarMutationError = null;
            using CancellationTokenRegistration cancellation = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (publishing) { _operation?.Cancel(); CancelBrowserReads(); }
            }));
            try
            {
                token.ThrowIfCancellationRequested();
                DesktopPreferences latest = _preferencesStore.Load();
                AppearanceSettingsValues appearanceDraft = settings.AppearanceSettingsResult;
                DesktopPreferences preferences = appearance.MergeDesktop(appearanceDraft, settings.Result with
                {
                    RecentRepositories = latest.RecentRepositories, PluginSettings = latest.PluginSettings
                });
                preferences = detailed.MergeDesktop(preferences);
                bool histogramChanged = settings.GeneralSettingsResult.UseHistogramDiffAlgorithm != general.Initial.UseHistogramDiffAlgorithm;
                GeneralSettingsPublication publication = general.Save(settings.GeneralSettingsResult, preferences,
                    desktopSaved: acceptedDesktop =>
                    {
                        appearance.DesktopSaved(appearanceDraft);
                        detailed.DesktopSaved(acceptedDesktop);
                        DetailedGraphSettings.PublishSaved(_preferencesStore.FilePath, acceptedDesktop);
                        // Publish only the value actually committed to this Desktop profile,
                        // even if a later Clone/History workflow profile cannot be saved.
                        FileStatusListPreferencesStore.PublishPathPresentation(_preferencesStore.FilePath, acceptedDesktop.TruncatePathMethod);
                        CommitSpellingController.PublishSavedDesktop(_preferencesStore.FilePath, acceptedDesktop);
                        // Register from the value actually written, before another workflow profile can
                        // fail. The service owns the mutation even if Settings stops waiting or closes.
                        try { avatarMutation = avatars?.ApplySavedPolicy(AvatarPreferences.From(acceptedDesktop)); }
                        catch (Exception error) { avatarMutationError = error; }
                    });
                preferences = publication.Desktop;
                settings.AcceptGeneralSaved(publication);
                bool presentationChanged = preferences.ShowGitStatusInBrowseToolbar != _preferences.ShowGitStatusInBrowseToolbar
                    || preferences.ShowGitStatusForArtificialCommits != _preferences.ShowGitStatusForArtificialCommits
                    || preferences.ShowAheadBehindData != _preferences.ShowAheadBehindData
                    || preferences.ShowStashCount != _preferences.ShowStashCount
                    || preferences.ShowSubmoduleStatus != _preferences.ShowSubmoduleStatus;
                static bool SubmoduleStatusVisible(DesktopPreferences value) => value.ShowSubmoduleStatus
                    && (value.ShowGitStatusInBrowseToolbar || value.ShowGitStatusForArtificialCommits);
                bool submodulePresentationChanged = SubmoduleStatusVisible(preferences) != SubmoduleStatusVisible(_preferences);
                bool reload = preferences.HistoryLimit != _preferences.HistoryLimit || settings.HistorySortResult != _historyView.Sort;
                if (settings.FileHistoryResult.FollowRenames != fileHistory.FollowRenames || settings.FileHistoryResult.ExactRenames != fileHistory.ExactRenames)
                { reload |= _historyQuery.Path is not null; }
                bool referencesChanged = preferences.ReferenceSorting != _preferences.ReferenceSorting
                    || preferences.PrioritizedBranchNames != _preferences.PrioritizedBranchNames
                    || preferences.PrioritizedRemoteNames != _preferences.PrioritizedRemoteNames;
                // The active View is the Appearance baseline, but only explicit Appearance edits
                // become saved defaults. Sorting retains its existing explicit Settings publication;
                // other temporary View choices remain in this Browse.
                AppearanceSettingsPublication appearancePublication = appearance.Save(appearanceDraft,
                    preferences, _historyView, settings.HistorySortResult);
                settings.AcceptAppearanceSaved(appearancePublication);
                _historyView = appearancePublication.History;
                bool branchVisibilityChanged = preferences.ShowRepoCurrentBranch != _preferences.ShowRepoCurrentBranch;
                _preferences = preferences;
                reload |= ApplyGeneralHistoryPolicy();
                ApplyTheme(_preferences.Theme);
                ApplyHistoryViewPresentation();
                if (branchVisibilityChanged) { await ApplyRepositoryBranchVisibilityAsync(_preferences.ShowRepoCurrentBranch, token); }
                if (reload)
                {
                    _historyQuery = _historyQuery with { Count = _preferences.HistoryLimit };
                    _historyWindow = _historyQuery.Count;
                    if (_repository is not null) { await RefreshAsync(); }
                }
                else if (referencesChanged) { await ApplyReferenceSortingPreferencesAsync(); }
                if (presentationChanged && !reload)
                { await RefreshGeneralPresentationFromSettingsAsync(token, submodulePresentationChanged); }
                _pullPreferences = _pullPreferencesStore.Load(); RefreshPullMenu();
                if (histogramChanged) { await ReloadGeneralDiffAlgorithmAsync(); }
                RefreshHistoryMenuStates();
                initial = preferences; fileHistory = settings.FileHistoryResult;
                if (_repository is null && _startupArgumentsToRetry is not null)
                { await RetryStartupRepositoryAsync(token); }
                token.ThrowIfCancellationRequested();
            }
            finally { publishing = false; }
            // A profile error leaves this block by exception before these awaits, preserving its
            // original diagnostic while the already-registered service mutation remains owned.
            if (avatarMutationError is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(avatarMutationError).Throw(); }
            if (avatarMutation is not null) { await avatarMutation.WaitAsync(token); }
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly BackgroundFetchService _backgroundFetch = new();
    private readonly AutoCompileSubmodulesService _autoCompile = new();
    private bool _pluginDialogOpen;

    private IReadOnlyDictionary<string, string?> PluginSettings(PluginDescriptor descriptor)
        => _preferences.PluginSettings.GetValueOrDefault(descriptor.Id.ToString()) ?? [];

    private void SavePluginSettings(PluginDescriptor descriptor, IReadOnlyDictionary<string, string?> values)
    {
        DesktopPreferences latest = _preferencesStore.Load();
        Dictionary<string, Dictionary<string, string?>> settings = new(latest.PluginSettings)
        {
            [descriptor.Id.ToString()] = values.Where(value => !value.Key.Equals("password", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(value => value.Key, value => value.Value)
        };
        _preferences = _preferences with { PluginSettings = settings };
        _preferencesStore.Save(latest with { PluginSettings = settings });
    }

    private IEnumerable<INativePlugin> LifecycleSettingsPlugins()
        => [new LifecycleSettingsPlugin(BackgroundFetchService.Descriptor), new LifecycleSettingsPlugin(AutoCompileSubmodulesService.Descriptor)];

    private async Task ShowPluginAsync(INativePlugin plugin, bool fromScript = false, Window? owner = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GitRepository? repository = _repository;
        Window pluginOwner = owner ?? this;
        if (repository is null || _busy && !fromScript || !pluginOwner.IsVisible) { return; }
        _pluginDialogOpen = true;
        bool refreshAfterPlugin = true;
        try
        {
            IReadOnlyDictionary<string, string?> values = PluginSettings(plugin.Descriptor);
            if (plugin is StatisticsPlugin statistics)
            {
                SimplePluginSettingsSession settings = await ReadSimplePluginSettingsAsync(repository, SimplePluginSettingsCatalog.Statistics, cancellationToken);
                if (_repository != repository || !pluginOwner.IsVisible) { return; }
                StatisticsDialog dialog = new(repository, statistics.GetOptions(settings.InvocationValues()));
                dialog.Opened += async (_, _) => await dialog.InitializeAsync();
                await ShowOwnedAsync(dialog); // Invocation options are temporary; Settings owns the defaults.
            }
            else if (plugin is DeleteUnusedBranchesPlugin)
            {
                using CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                DeleteUnusedBranchesSettingsSession settings = new(repository, values);
                await settings.InitializeAsync(read.Token);
                read.Token.ThrowIfCancellationRequested();
                if (_repository != repository || !pluginOwner.IsVisible) { return; }
                bool requestedSettings = false;
                DeleteUnusedBranchesDialog dialog = new(repository, settings.EngineValues(),
                    requestSettings: () => requestedSettings = true);
                dialog.Opened += async (_, _) => await dialog.InitializeAsync();
                await ShowOwnedAsync(dialog);
                // The explicit result is latched before Close, even when ShowDialog's
                // continuation precedes the compatibility callback after Close.
                if ((requestedSettings || dialog.SettingsRequested) && _repository == repository
                    && pluginOwner.IsVisible && !_lifetime.IsCancellationRequested)
                { await ShowSettingsPageAsync(showChecklist: false, initialPage: "PluginDeleteUnusedBranches", settingsOwnerOverride: pluginOwner,
                    fromScript: fromScript, cancellationToken: cancellationToken); }
            }
            else if (plugin.Descriptor.Id == BackgroundFetchService.Descriptor.Id)
            {
                await ShowSettingsPageAsync(showChecklist: false, initialPage: "PluginBackgroundFetch", settingsOwnerOverride: pluginOwner,
                    fromScript: fromScript, cancellationToken: cancellationToken);
            }
            else if (plugin.Descriptor.Id == AutoCompileSubmodulesService.Descriptor.Id)
            {
                if (fromScript) { await ManualSubmoduleBuildAsync(repository, pluginOwner, cancellationToken); }
                else { await RunAsync(T("Auto compile submodules"), active => ManualSubmoduleBuildAsync(active, pluginOwner, Token), refresh: false); }
            }
            else if (plugin is CreateLocalBranchesPlugin)
            {
                CreateLocalBranchesDialog dialog = new(repository);
                await ShowOwnedAsync(dialog);
                await dialog.WindowWorkCompletion;
            }
            else if (plugin is FindLargeFilesPlugin)
            {
                SimplePluginSettingsSession settings = await ReadSimplePluginSettingsAsync(repository, SimplePluginSettingsCatalog.FindLargeFiles, cancellationToken);
                if (_repository != repository || !pluginOwner.IsVisible) { return; }
                IReadOnlyDictionary<string, string?> invocation;
                try { invocation = settings.InvocationValues(); }
                catch (ArgumentException error) when (error.ParamName == "minimumMegabytes")
                {
                    using WindowWork work = TrackWindowWork();
                    using CancellationTokenSource notice = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                    await Dialogs.ShowMessageAsync(pluginOwner, T("Find large files"), T(SimplePluginSettingsSession.UnsupportedThresholdMessage),
                        warning: true, cancellationToken: notice.Token, stopOnOwnerClose: true);
                    return; // No scanner and no configuration write follows this notice.
                }
                FindLargeFilesDialog dialog = new(repository, invocation);
                dialog.Opened += async (_, _) => await dialog.InitializeAsync();
                await ShowOwnedAsync(dialog); // Threshold/revision edits apply to this scan only.
            }
            else if (plugin is GitImpactPlugin impact)
            {
                GitImpactDialog dialog = new(repository, impact.GetOptions(values));
                dialog.Opened += async (_, _) => await dialog.InitializeAsync();
                await ShowOwnedAsync(dialog);
                SavePluginSettings(plugin.Descriptor, dialog.CurrentValues);
            }
            else if (plugin is ProxySwitcherPlugin)
            {
                using WindowWork work = TrackWindowWork();
                using CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                ProxySwitcherSettingsSession settings = new(repository, values);
                await settings.InitializeAsync(read.Token);
                read.Token.ThrowIfCancellationRequested();
                if (!settings.Loaded) { throw new InvalidDataException("Unable to read plugin settings.", settings.LoadError); }
                if (_repository != repository || !pluginOwner.IsVisible) { return; }
                bool requestedSettings = false;
                ProxySwitcherDialog dialog = new(repository, settings.RuntimeValues(), requestSettings: () => requestedSettings = true);
                dialog.Opened += async (_, _) => await dialog.InitializeAsync();
                await ShowOwnedAsync(dialog);
                refreshAfterPlugin = dialog.MayHaveRepositoryChanges;
                if ((requestedSettings || dialog.SettingsRequested) && _repository == repository
                    && pluginOwner.IsVisible && !_lifetime.IsCancellationRequested)
                { await ShowSettingsPageAsync(showChecklist: false, initialPage: ProxySwitcherSettingsCatalog.PageId,
                    settingsOwnerOverride: pluginOwner, fromScript: fromScript, cancellationToken: cancellationToken); }
            }
            else if (plugin is GourcePlugin)
            {
                SimplePluginSettingsSession settings = await ReadSimplePluginSettingsAsync(repository, SimplePluginSettingsCatalog.Gource, cancellationToken);
                if (_repository != repository || !pluginOwner.IsVisible) { return; }
                IReadOnlyDictionary<string, string?> defaults = settings.InvocationValues();
                GourceOptions initial = new(defaults["executable"]!, defaults["arguments"]!, repository.Path);
                GourceDialog dialog = new(repository, defaults, saveAcceptedSettings: async (accepted, token) =>
                {
                    cancellationToken.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
                    settings.StageAcceptedGource(initial, accepted);
                    await settings.SaveFilesAsync(token);
                });
                dialog.Opened += async (_, _) => await dialog.InitializeAsync();
                await ShowOwnedAsync(dialog);
            }
            else
            {
                PluginDialog dialog = new(repository, plugin, values, async (settings, token) =>
                {
                    cancellationToken.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
                    cancellationToken.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
                    SavePluginSettings(plugin.Descriptor, settings);
                });
                await ShowOwnedAsync(dialog);
            }
        }
        finally { _pluginDialogOpen = false; }
        cancellationToken.ThrowIfCancellationRequested();
        // Cancellation or a hook can leave completed mutations even when the plugin reports an error.
        if (refreshAfterPlugin && _repository == repository) { await RefreshAsync(); }

        async Task ShowOwnedAsync(Window dialog)
        {
            bool ownerCloseRequested = false;
            void Closing(object? sender, WindowClosingEventArgs args)
            {
                // A busy RepositoryDialog may veto its owner's first Close while it drains.
                // Remember that request before the parent's Closing can be raised.
                if (fromScript && args.CloseReason == WindowCloseReason.OwnerWindowClosing) { ownerCloseRequested = true; }
            }
            void Closed(object? sender, EventArgs args)
            {
                // Subscribe before ShowDialog: retain the owner stop intent before its
                // continuation could save settings or let a Before script continue.
                if (ownerCloseRequested && pluginOwner.IsVisible) { pluginOwner.Close(); }
            }
            dialog.Closing += Closing; dialog.Closed += Closed;
            using CancellationTokenRegistration cancellation = cancellationToken.Register(() =>
                Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) { dialog.Close(); } }));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await dialog.ShowDialog(pluginOwner);
            }
            finally { dialog.Closing -= Closing; dialog.Closed -= Closed; }
            // ShowDialog only completes after a deferred normal Close/drain has finished.
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task RegisterBackgroundFetchAsync(GitRepository repository, BackgroundFetchOptions? options = null)
    {
        if (options is null)
        {
            BackgroundFetchSettingsSession settings = new(repository, PluginSettings(BackgroundFetchService.Descriptor));
            await settings.InitializeAsync(_lifetime.Token);
            if (!ReferenceEquals(repository, _repository) || _lifetime.IsCancellationRequested) { return; }
            if (!settings.Loaded)
            {
                // Do not start a legacy timer under an unreadable scoped profile.
                // Browsing remains available and the real read failure is visible.
                await _backgroundFetch.UnregisterAsync();
                LastException = settings.LoadError;
                AppendOutput(T("Unable to read plugin settings."));
                return;
            }
            options = settings.EffectiveOptions();
        }
        await _backgroundFetch.RegisterAsync(new(repository,
            IsGitBusy: () => Volatile.Read(ref _busy) || Volatile.Read(ref _pluginDialogOpen),
            RequestRefreshAsync: async token =>
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    token.IsCancellationRequested || _repository != repository || _busy ? Task.CompletedTask : RefreshAsync());
            }), options, _lifetime.Token);
    }

    private async Task AfterSubmoduleUpdateAsync(GitRepository repository, Window owner, CancellationToken token)
    {
        SimplePluginSettingsSession settings = await ReadSimplePluginSettingsAsync(repository, SimplePluginSettingsCatalog.AutoCompile, token);
        AutoCompileSubmodulesResult builds = await _autoCompile.AfterSubmoduleUpdateAsync(repository, actionSucceeded: true,
            settings.AutoCompileOptions(), (request, cancellation) => ConfirmSubmoduleBuildAsync(request, cancellation, owner), token);
        ReportSubmoduleBuilds(builds);
    }

    private async Task<AutoCompileDecision> ConfirmSubmoduleBuildAsync(AutoCompileBuildRequest request, CancellationToken token, Window owner)
        => await Dispatcher.UIThread.InvokeAsync(() => ConfirmSubmoduleBuildOnUiAsync(request, token, owner));

    private async Task<AutoCompileDecision> ConfirmSubmoduleBuildOnUiAsync(AutoCompileBuildRequest request, CancellationToken token, Window owner)
    {
        Window dialog = new() { Name = "AutoCompileBuildConfirmation", Title = T("Auto compile submodules"), Width = 560, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanMinimize = false, CanMaximize = false,
            ShowInTaskbar = false, RequestedThemeVariant = owner.ActualThemeVariant };
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach ((string text, AutoCompileDecision decision) in new[] { ("Build", AutoCompileDecision.Build), ("Skip", AutoCompileDecision.Skip), ("Cancel", AutoCompileDecision.Cancel) })
        {
            Button button = new() { Name = "AutoCompile" + text, Content = T(text), IsCancel = decision == AutoCompileDecision.Cancel };
            button.Click += (_, _) => dialog.Close(decision);
            actions.Children.Add(button);
        }
        dialog.Content = new StackPanel { Margin = new Thickness(12), Spacing = 10, Children =
        {
            new TextBlock { Text = T("Build solution?") + "\n" + request.SolutionPath, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new TextBlock { Text = string.Join('\n', request.AllSolutions), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, actions
        } };
        bool ownerCloseRequested = false;
        dialog.Closing += (_, args) => { if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { ownerCloseRequested = true; } };
        // Retain normal owner-close intent, replayed only after this child has
        // actually closed. The helper posts rather than reentering owner Closing.
        _ = new DeferredOwnerCloseReplay(dialog);
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) { dialog.Close(AutoCompileDecision.Cancel); } }));
        token.ThrowIfCancellationRequested();
        AutoCompileDecision outcome = await dialog.ShowDialog<AutoCompileDecision?>(owner) ?? AutoCompileDecision.Cancel;
        token.ThrowIfCancellationRequested();
        if (ownerCloseRequested) { throw new OperationCanceledException("The build owner was closed."); }
        return outcome;
    }

    // These entries edit lifecycle settings; the services run at repository opening or after submodule update.
    private sealed class LifecycleSettingsPlugin(PluginDescriptor descriptor) : INativePlugin
    {
        public PluginDescriptor Descriptor => descriptor;
        public Task<PluginResult> ExecuteAsync(PluginContext context, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PluginResult("Settings applied."));
        }
    }
}

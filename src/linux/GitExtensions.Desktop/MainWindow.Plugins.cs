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
        try
        {
            IReadOnlyDictionary<string, string?> values = PluginSettings(plugin.Descriptor);
            if (plugin is StatisticsPlugin statistics)
            {
                StatisticsDialog dialog = new(repository, statistics.GetOptions(values));
                dialog.Opened += async (_, _) => await dialog.InitializeAsync();
                await ShowOwnedAsync(dialog);
                SavePluginSettings(plugin.Descriptor, dialog.CurrentValues);
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
            else if (plugin is FindLargeFilesPlugin)
            {
                FindLargeFilesDialog dialog = new(repository, values);
                dialog.Opened += async (_, _) => await dialog.InitializeAsync();
                await ShowOwnedAsync(dialog);
                SavePluginSettings(plugin.Descriptor, dialog.CurrentValues);
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
                ProxySwitcherDialog dialog = new(repository, values);
                dialog.Opened += async (_, _) => await dialog.InitializeAsync();
                await ShowOwnedAsync(dialog);
                SavePluginSettings(plugin.Descriptor, dialog.CurrentValues);
            }
            else if (plugin is GourcePlugin)
            {
                GourceDialog dialog = new(repository, values);
                dialog.Opened += async (_, _) => await dialog.InitializeAsync();
                await ShowOwnedAsync(dialog);
                SavePluginSettings(plugin.Descriptor, dialog.CurrentValues);
            }
            else
            {
                PluginDialog dialog = new(repository, plugin, values, async (settings, token) =>
                {
                    cancellationToken.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
                    if (plugin.Descriptor.Id == BackgroundFetchService.Descriptor.Id)
                    {
                        BackgroundFetchOptions options = BackgroundFetchOptions.FromValues(settings);
                        await RegisterBackgroundFetchAsync(repository, options);
                    }
                    else if (plugin.Descriptor.Id == AutoCompileSubmodulesService.Descriptor.Id) { _ = AutoCompileSubmodulesOptions.FromValues(settings); }
                    cancellationToken.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
                    SavePluginSettings(plugin.Descriptor, settings);
                });
                await ShowOwnedAsync(dialog);
            }
        }
        finally { _pluginDialogOpen = false; }
        cancellationToken.ThrowIfCancellationRequested();
        // Cancellation or a hook can leave completed mutations even when the plugin reports an error.
        if (_repository == repository) { await RefreshAsync(); }

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

    private Task RegisterBackgroundFetchAsync(GitRepository repository, BackgroundFetchOptions? options = null)
        => _backgroundFetch.RegisterAsync(new(repository,
            IsGitBusy: () => Volatile.Read(ref _busy) || Volatile.Read(ref _pluginDialogOpen),
            RequestRefreshAsync: async token =>
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    token.IsCancellationRequested || _repository != repository || _busy ? Task.CompletedTask : RefreshAsync());
            }), options ?? BackgroundFetchOptions.FromValues(PluginSettings(BackgroundFetchService.Descriptor)), _lifetime.Token);

    private async Task AfterSubmoduleUpdateAsync(GitRepository repository, Window owner, CancellationToken token)
    {
        AutoCompileSubmodulesResult builds = await _autoCompile.AfterSubmoduleUpdateAsync(repository, actionSucceeded: true,
            AutoCompileSubmodulesOptions.FromValues(PluginSettings(AutoCompileSubmodulesService.Descriptor)), (request, cancellation) => ConfirmSubmoduleBuildAsync(request, cancellation, owner), token);
        foreach (AutoCompileBuildResult build in builds.Builds)
        {
            if (build.Result is not null) { AppendOutput(build.SolutionPath + "\n" + build.Result.StandardOutput + build.Result.StandardError); }
            if (build.Error is not null) { AppendOutput(build.SolutionPath + "\n" + build.Error); }
        }
    }

    private async Task<AutoCompileDecision> ConfirmSubmoduleBuildAsync(AutoCompileBuildRequest request, CancellationToken token, Window owner)
        => await Dispatcher.UIThread.InvokeAsync(() => ConfirmSubmoduleBuildOnUiAsync(request, token, owner));

    private async Task<AutoCompileDecision> ConfirmSubmoduleBuildOnUiAsync(AutoCompileBuildRequest request, CancellationToken token, Window owner)
    {
        Window dialog = new() { Title = T("Auto compile submodules"), Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach ((string text, AutoCompileDecision decision) in new[] { ("Build", AutoCompileDecision.Build), ("Skip", AutoCompileDecision.Skip), ("Cancel", AutoCompileDecision.Cancel) })
        {
            Button button = new() { Content = T(text), IsCancel = decision == AutoCompileDecision.Cancel };
            button.Click += (_, _) => dialog.Close(decision);
            actions.Children.Add(button);
        }
        dialog.Content = new StackPanel { Margin = new Thickness(12), Spacing = 10, Children =
        {
            new TextBlock { Text = T("Build solution?") + "\n" + request.SolutionPath, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new TextBlock { Text = string.Join('\n', request.AllSolutions), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, actions
        } };
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(AutoCompileDecision.Cancel)));
        token.ThrowIfCancellationRequested();
        return await dialog.ShowDialog<AutoCompileDecision?>(owner) ?? AutoCompileDecision.Cancel;
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

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
        Dictionary<string, Dictionary<string, string?>> settings = new(_preferences.PluginSettings)
        {
            [descriptor.Id.ToString()] = values.Where(value => !value.Key.Equals("password", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(value => value.Key, value => value.Value)
        };
        _preferences = _preferences with { PluginSettings = settings };
        _preferencesStore.Save(_preferences);
    }

    private IEnumerable<INativePlugin> LifecycleSettingsPlugins()
        => [new LifecycleSettingsPlugin(BackgroundFetchService.Descriptor), new LifecycleSettingsPlugin(AutoCompileSubmodulesService.Descriptor)];

    private async Task ShowPluginAsync(INativePlugin plugin)
    {
        if (_repository is null || _busy) { return; }
        _pluginDialogOpen = true;
        try
        {
            IReadOnlyDictionary<string, string?> values = PluginSettings(plugin.Descriptor);
            if (plugin is StatisticsPlugin statistics)
            {
                StatisticsDialog dialog = new(_repository, statistics.GetOptions(values));
                dialog.Opened += async (_, _) => await dialog.InitializeAsync();
                await dialog.ShowDialog(this);
                SavePluginSettings(plugin.Descriptor, dialog.CurrentValues);
            }
            else if (plugin is GitImpactPlugin impact)
            {
                GitImpactDialog dialog = new(_repository, impact.GetOptions(values));
                dialog.Opened += async (_, _) => await dialog.InitializeAsync();
                await dialog.ShowDialog(this);
                SavePluginSettings(plugin.Descriptor, dialog.CurrentValues);
            }
            else
            {
                PluginDialog dialog = new(_repository, plugin, values, async (settings, token) =>
                {
                    if (plugin.Descriptor.Id == BackgroundFetchService.Descriptor.Id)
                    {
                        BackgroundFetchOptions options = BackgroundFetchOptions.FromValues(settings);
                        await RegisterBackgroundFetchAsync(_repository, options);
                    }
                    else if (plugin.Descriptor.Id == AutoCompileSubmodulesService.Descriptor.Id) { _ = AutoCompileSubmodulesOptions.FromValues(settings); }
                    token.ThrowIfCancellationRequested();
                    SavePluginSettings(plugin.Descriptor, settings);
                });
                await dialog.ShowDialog(this);
            }
        }
        finally { _pluginDialogOpen = false; }
        // Cancellation or a hook can leave completed mutations even when the plugin reports an error.
        await RefreshAsync();
    }

    private Task RegisterBackgroundFetchAsync(GitRepository repository, BackgroundFetchOptions? options = null)
        => _backgroundFetch.RegisterAsync(new(repository,
            IsGitBusy: () => Volatile.Read(ref _busy) || Volatile.Read(ref _pluginDialogOpen),
            RequestRefreshAsync: async token =>
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    token.IsCancellationRequested || _repository != repository || _busy ? Task.CompletedTask : RefreshAsync());
            }), options ?? BackgroundFetchOptions.FromValues(PluginSettings(BackgroundFetchService.Descriptor)), _lifetime.Token);

    private Task UpdateSubmodulesAndBuildAsync() => RunAsync("Update submodules", async repository =>
    {
        await repository.UpdateSubmodulesAsync(cancellationToken: Token);
        AutoCompileSubmodulesResult builds = await _autoCompile.AfterSubmoduleUpdateAsync(repository, actionSucceeded: true,
            AutoCompileSubmodulesOptions.FromValues(PluginSettings(AutoCompileSubmodulesService.Descriptor)), ConfirmSubmoduleBuildAsync, Token);
        foreach (AutoCompileBuildResult build in builds.Builds)
        {
            if (build.Result is not null) { AppendOutput(build.SolutionPath + "\n" + build.Result.StandardOutput + build.Result.StandardError); }
            if (build.Error is not null) { AppendOutput(build.SolutionPath + "\n" + build.Error); }
        }
    });

    private async Task<AutoCompileDecision> ConfirmSubmoduleBuildAsync(AutoCompileBuildRequest request, CancellationToken token)
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
        return await dialog.ShowDialog<AutoCompileDecision?>(this) ?? AutoCompileDecision.Cancel;
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

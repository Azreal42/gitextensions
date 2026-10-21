using Avalonia.Controls;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private async Task<SimplePluginSettingsSession> ReadSimplePluginSettingsAsync(GitRepository repository,
        SimplePluginSettingsEntry entry, CancellationToken token)
    {
        using WindowWork work = TrackWindowWork();
        using CancellationTokenSource reading = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        SimplePluginSettingsSession session = new(entry, repository, _preferences.PluginSettings.GetValueOrDefault(entry.Definition.Id.ToString()));
        await session.InitializeAsync(reading.Token);
        reading.Token.ThrowIfCancellationRequested();
        if (!session.Loaded) { throw new InvalidDataException("Unable to read plugin settings.", session.LoadError); }
        return session;
    }

    private async Task ManualSubmoduleBuildAsync(GitRepository repository, Window owner, CancellationToken token)
    {
        using WindowWork work = TrackWindowWork();
        using IDisposable execution = repository.BeginOperation();
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        SimplePluginSettingsSession settings = await ReadSimplePluginSettingsAsync(repository, SimplePluginSettingsCatalog.AutoCompile, operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        if (!owner.IsVisible) { return; }
        AutoCompileSubmodulesResult builds = await _autoCompile.BuildAsync(repository, settings.AutoCompileOptions(),
            (request, cancellation) => ConfirmSubmoduleBuildAsync(request, cancellation, owner), operation.Token);
        ReportSubmoduleBuilds(builds);
    }

    private void ReportSubmoduleBuilds(AutoCompileSubmodulesResult builds)
    {
        foreach (AutoCompileBuildResult build in builds.Builds)
        {
            if (build.Result is not null) { AppendOutput(build.SolutionPath + "\n" + build.Result.StandardOutput + build.Result.StandardError); }
            if (build.Error is not null) { AppendOutput(build.SolutionPath + "\n" + build.Error); }
        }
    }
}

using System.Diagnostics;
using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    internal Func<AppVeyorSettings, IReadOnlySet<string>, IAppVeyorClient> AppVeyorClientFactory { get; set; }
        = (settings, hashes) => new AppVeyorApiClient(settings, admitCommit: hashes.Contains);
    private readonly List<MenuItem> _buildServerPullRequestMenus = [];

    internal MenuItem BuildServerPullRequestPageMenu()
    {
        MenuItem item = new() { Name = "BuildServerPullRequestPage",
            Header = RevisionHeader("openPullRequestPageStripMenuItem", "Vie&w pull request in a browser"), Icon = OriginalIconImage("PullRequest.png") };
        item.Click += async (_, _) => await WithRevisionActionFocusAsync(ShowBuildPullRequestAsync);
        _buildServerPullRequestMenus.Add(item); UpdateBuildServerPullRequestCommands(); return item;
    }
    internal Task ShowBuildPullRequestAsync()
    {
        Uri? uri = BuildServerStatusFor((_commits.SelectedItem as GitCommit)?.Hash)?.Runs.FirstOrDefault()?.PullRequestUri;
        if (!_gitHubActionsEnabled || _gitHubActionsClosing || uri is null) { return Task.CompletedTask; }
        try { using Process? process = ExternalLauncher(new ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { uri.AbsoluteUri } }); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { SetGitHubActionsError("The pull request page could not be opened in the browser."); }
        return Task.CompletedTask;
    }
    private void UpdateBuildServerPullRequestCommands()
    {
        bool available = _gitHubActionsEnabled && !_gitHubActionsClosing
            && BuildServerStatusFor((_commits.SelectedItem as GitCommit)?.Hash)?.Runs.FirstOrDefault()?.PullRequestUri is not null;
        foreach (MenuItem item in _buildServerPullRequestMenus) { item.IsEnabled = item.IsVisible = available; }
    }
}

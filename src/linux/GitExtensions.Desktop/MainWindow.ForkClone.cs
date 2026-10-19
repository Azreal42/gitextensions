using Avalonia.Threading;
using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    internal Func<GitHubHostingSettings, GitHubHostingCredentials, IGitHubForkCloneClient> ForkCloneClientFactory { get; set; }
        = (settings, credentials) => new GitHubHostingClient(settings, credentials.ToDraft());

    internal async Task ShowForkCloneAsync()
    {
        if (_busy) { return; }
        ForkCloneDialog? dialog = null;
        await RunAsync("Fork/Clone repository", async _ =>
        {
            IReadOnlyDictionary<string, string?> values = new GitHubHostingPreferencesStore().Load();
            GitHubHostingSettings settings = GitHubHostingSettings.FromPluginSettings(values);
            GitHubHostingCredentials credentials = settings.CredentialsFromPluginSettings(values);
            using IGitHubForkCloneClient client = ForkCloneClientFactory(settings, credentials);
            ForkCloneDialog opened = new(client, credentials.Type != BuildServerCredentialsType.Guest, openUrl: uri => { _ = OpenWebAsync(uri.AbsoluteUri); })
            { RequestedThemeVariant = ActualThemeVariant };
            dialog = opened;
            using CancellationTokenRegistration cancellation = Token.Register(() => Dispatcher.UIThread.Post(opened.Close));
            try { Token.ThrowIfCancellationRequested(); await opened.ShowDialog(WorkflowOwner); }
            finally { if (opened.IsVisible) { opened.Close(); } await opened.Completion; }
        }, requireRepository: false, refresh: false);
        if (!_lifetime.IsCancellationRequested && dialog is { CreatedRepository: GitRepository cloned })
        {
            LoadRepositoryHistory(); UpdateRepositoryMenus(); if (_dashboard?.IsVisible == true) { _dashboard.Refresh(); }
            if (dialog.CloneWarning is string warning) { await Dialogs.ShowMessageAsync(this, T("Could not add remote"), warning, true); }
            if (!_lifetime.IsCancellationRequested) { await OpenRepositoryAsync(cloned.Path); }
        }
    }
}

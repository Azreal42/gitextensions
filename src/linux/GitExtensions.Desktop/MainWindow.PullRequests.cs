using Avalonia.Controls;
using Avalonia.Threading;
using GitExtensions.Linux.Scripts;
using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private MenuItem BuildGitHostsMenu()
    {
        MenuItem forkClone = MenuAction("Fork/Clone repository…", ShowForkCloneAsync, "CloneRepoGitHub.png"); forkClone.Name = "GitHubForkClone";
        MenuItem create = MenuAction("Create pull request…", () => ShowPullRequestsAsync(create: true), "PullRequest.png"); create.Name = "GitHubCreatePullRequest";
        MenuItem view = MenuAction("View pull requests…", () => ShowPullRequestsAsync(create: false), "PullRequest.png"); view.Name = "GitHubViewPullRequests";
        MenuItem settings = MenuAction("GitHub settings…", ShowGitHubHostingSettingsAsync, "GitHub32.png"); settings.Name = "GitHubHostingSettings";
        MenuItem menu = MenuGroup("Git hosts", MenuAction("Browse remote repository", BrowseRemoteAsync), forkClone, create, view, settings);
        menu.SubmenuOpened += (_, _) =>
        {
            create.IsEnabled = view.IsEnabled = !_busy && _repository is not null;
            forkClone.IsEnabled = settings.IsEnabled = !_busy;
        };
        return menu;
    }

    private async Task ShowGitHubHostingSettingsAsync()
    {
        await RunAsync("GitHub settings", async _ =>
        {
            GitHubHostingSettingsDialog dialog = new(new(), _translations, OpenWebAsync) { RequestedThemeVariant = ActualThemeVariant };
            using CancellationTokenRegistration cancellation = Token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
            await dialog.ShowDialog<bool>(this);
            if (!_lifetime.IsCancellationRequested) { _preferences = _preferencesStore.Load(); }
        }, refresh: false, requireRepository: false);
    }

    private async Task ShowPullRequestsAsync(bool create)
    {
        if (_busy || _repository is null) { return; }
        GitRepository repository = _repository;
        bool gitAttempted = false;
        try
        {
            await RunAsync(create ? "Create pull request" : "View pull requests", async current =>
            {
                GitHubHostingPreferencesStore store = new();
                IReadOnlyDictionary<string, string?> values = store.Load();
                GitHubHostingSettings settings = GitHubHostingSettings.FromPluginSettings(values);
                GitHubHostingCredentials credentials = settings.CredentialsFromPluginSettings(values);
                using GitHubHostingClient client = new(settings, credentials.ToDraft());
                string user = credentials.Type == BuildServerCredentialsType.Guest ? "" : await client.GetCurrentUserAsync(Token);
                List<PullRequestHostedRemote> remotes = [];
                foreach (GitRemote remote in await repository.GetRemotesAsync(Token))
                {
                    if (!settings.TryParseRemote(remote.FetchUrls.FirstOrDefault() ?? "", out GitHubRepositoryId? id)
                        || remotes.Any(r => r.Repository.Owner.Equals(id.Owner, StringComparison.OrdinalIgnoreCase) && r.Repository.Name.Equals(id.Name, StringComparison.OrdinalIgnoreCase))) { continue; }
                    try
                    {
                        GitHubHostedRepository hosted = await client.GetRepositoryAsync(id, Token);
                        remotes.Add(new(remote.Name, hosted, user.Equals(hosted.Owner, StringComparison.OrdinalIgnoreCase)));
                    }
                    catch (GitHubHostingException error) { AppendOutput("GitHub remote " + remote.Name + ": " + error.Message); }
                }
                if (remotes.Count == 0) { throw new InvalidOperationException(T("No GitHub remote matches the configured host. Configure a remote and GitHub settings first.")); }
                Token.ThrowIfCancellationRequested();
                PullRequestDialog dialog;
                if (create)
                {
                    if (credentials.Type == BuildServerCredentialsType.Guest)
                    { throw new InvalidOperationException(T("Enter GitHub authentication in Git hosts → GitHub settings before creating a pull request.")); }
                    string? branch = (await repository.GetStatusAsync(Token)).Branch;
                    dialog = new CreatePullRequestDialog(repository, remotes, client, chooseBranch: branch);
                }
                else
                {
                    ViewPullRequestsDialog? viewer = null;
                    viewer = new(repository, remotes, client, async (request, addRemote, token) =>
                    {
                        if (!ReferenceEquals(_repository, repository) || _lifetime.IsCancellationRequested || !viewer!.IsVisible) { return false; }
                        gitAttempted = true;
                        return await FetchPullRequestAsync(viewer, repository, settings, request, addRemote, token);
                    }, canWrite: credentials.Type != BuildServerCredentialsType.Guest);
                    dialog = viewer;
                }
                dialog.RequestedThemeVariant = ActualThemeVariant;
                using CancellationTokenRegistration cancellation = Token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
                try { Token.ThrowIfCancellationRequested(); await dialog.ShowDialog<bool>(this); }
                finally { await dialog.BeginClose(); await dialog.Completion; if (dialog.IsVisible) { dialog.Close(); } }
            }, refresh: false);
        }
        finally
        {
            if (gitAttempted && ReferenceEquals(_repository, repository) && !_lifetime.IsCancellationRequested) { await RefreshAsync(); }
        }
    }

    private async Task<bool> FetchPullRequestAsync(Window owner, GitRepository repository, GitHubHostingSettings settings,
        GitHubPullRequest request, bool addRemote, CancellationToken token)
    {
        if (addRemote && repository.IsBare) { throw new InvalidOperationException(T("This operation requires a working tree.")); }
        GitHubHostedRepository source = request.Head.Repository ?? throw new InvalidOperationException(T("The pull request source repository has been deleted."));
        IReadOnlyList<GitRemote> configuredRemotes = await repository.GetRemotesAsync(token);
        string url = source.CloneUrl;
        if (!settings.TryParseRemote(url, out GitHubRepositoryId? identity) || identity != source.Identity)
        { throw new InvalidOperationException(T("The pull request source URL does not match the configured GitHub host.")); }
        // Keep the clone transport chosen by the repository, as the Windows hosted-remote implementation does.
        bool ssh = configuredRemotes.Any(r => settings.TryParseRemote(r.FetchUrls.FirstOrDefault() ?? "", out GitHubRepositoryId? parsed)
            && parsed == request.Base.Repository?.Identity && ((r.FetchUrls.FirstOrDefault() ?? "").StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || (r.FetchUrls.FirstOrDefault() ?? "").StartsWith("git@", StringComparison.Ordinal)));
        if (ssh && settings.TryParseRemote(source.SshUrl, out GitHubRepositoryId? sshId) && sshId == source.Identity) { url = source.SshUrl; }
        if (addRemote && configuredRemotes.FirstOrDefault(r => r.Name == source.Owner) is { } existing
            && existing.FetchUrls.FirstOrDefault() is { } configuredUrl && settings.TryParseRemote(configuredUrl, out GitHubRepositoryId? configuredIdentity)
            && configuredIdentity.Owner.Equals(source.Owner, StringComparison.OrdinalIgnoreCase) && configuredIdentity.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase))
        { url = configuredUrl; }
        string branch = addRemote ? source.Owner + "/" + request.Head.Ref : request.FetchBranch;
        using ScriptWorkflowScope scripts = new(owner, repository, token);
        if (!await scripts.RunAsync(ScriptEvent.BeforeFetch)) { return false; }
        scripts.ThrowIfStopped();
        GitProcessDialogResult process = await GitProcessDialog.ShowAsync(owner, repository.Path, "git fetch " + branch, true,
            (operation, output) => repository.FetchPullRequestBranchAsync(url, request.Head.Ref, branch, addRemote ? source.Owner : null, operation, output), scripts.Token);
        if (process.Result is GitResult result) { AppendOutput(result.StandardOutput + result.StandardError); }
        if (process.Error is not null) { throw process.Error; }
        scripts.ThrowIfStopped();
        if (!process.Success || process.WorkflowStopRequested) { return false; }
        if (!await scripts.RunAsync(ScriptEvent.AfterFetch)) { return false; }
        scripts.ThrowIfStopped();
        if (!addRemote) { return true; }
        GitReference reference = (await repository.GetReferencesAsync(scripts.Token)).Single(r => r.Name == "refs/heads/" + branch);
        CheckoutBranchDialog? checkout = null;
        checkout = new(repository, reference, _preferences.AutoNormaliseBranchName, _preferences.AutoNormaliseSymbol,
            cancellation => AfterSubmoduleUpdateAsync(repository, checkout!.IsVisible ? checkout : owner, cancellation)) { RequestedThemeVariant = owner.ActualThemeVariant };
        using CancellationTokenRegistration closing = scripts.Token.Register(() => Dispatcher.UIThread.Post(checkout.Close));
        try
        {
            await checkout.InitializeAsync(scripts.Token); scripts.ThrowIfStopped();
            if (checkout.CanExecuteDefault)
            {
                await checkout.ExecuteDefaultAsync(owner, scripts.Token);
                if (checkout.LastError is null || scripts.Token.IsCancellationRequested) { return checkout.Succeeded; }
            }
            if (!checkout.Succeeded && !scripts.Token.IsCancellationRequested) { await checkout.ShowDialog(owner); }
            scripts.ThrowIfStopped(); return checkout.Succeeded;
        }
        finally { if (checkout.IsVisible) { checkout.Close(); } }
    }
}

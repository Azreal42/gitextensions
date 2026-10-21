using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GitLabIntegrationTests
{
    [Test]
    public async Task ActualScopedSaveRetainsUnknownXmlAndWritesOriginalGitlabKeysWithoutTeamCityCredentials()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        GitHubActionsSettingsSession abandoned = new(f.Repository); await abandoned.InitializeAsync(default);
        Dictionary<string, byte[]> material = f.Material();
        abandoned.Set(GitDistributedSettingsScope.Local, GitLabSettings.ProjectIdKey, "19");
        f.AssertMaterial(material);
        GitHubActionsSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        session.Set(GitDistributedSettingsScope.Local, GitLabSettings.ProjectIdKey, "2147483648");
        session.Set(GitDistributedSettingsScope.Local, GitLabSettings.ApiTokenKey, "local-explicit-token");
        await session.SaveFilesAsync(default);
        GitXmlSettingsSnapshot local = await f.ReadAsync(GitDistributedSettingsScope.Local);
        Assert.That(local.Values[GitLabSettings.InstanceUrlKey], Is.EqualTo("https://gitlab.example/one/"));
        Assert.That(local.Values[GitLabSettings.ApiTokenKey], Is.EqualTo("local-explicit-token"));
        Assert.That(local.Values[GitLabSettings.ProjectIdKey], Is.EqualTo("2147483648"));
        Assert.That(local.Values[GitLabSettings.PagesLimitKey], Is.EqualTo("0"));
        Assert.That(local.Values["future.λ"], Is.EqualTo("keep <literal> & \nbody"));
        Assert.That(session.GitLab().ProjectId, Is.EqualTo(2147483648L));
        Assert.That(session.HasChanges, Is.False);
        Dictionary<string, byte[]> accepted = f.Material();
        await session.SaveFilesAsync(default); f.AssertMaterial(accepted);
    }

    [TestCase("https://elsewhere.example/one/")]
    [TestCase("https://gitlab.example/two/")]
    public async Task InheritedTokenCannotFollowAChangedInstanceAndExplicitReentryBindsTheNewDestination(string destination)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f, "inherited-token");
        GitHubActionsSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        Dictionary<string, byte[]> material = f.Material();
        session.Set(GitDistributedSettingsScope.Local, GitLabSettings.InstanceUrlKey, destination);
        ArgumentException? error = Assert.ThrowsAsync<ArgumentException>(async () => await session.SaveFilesAsync(default));
        Assert.That(error!.Message, Does.Not.Contain("inherited-token").And.Not.Contain(destination)); f.AssertMaterial(material);
        session.Set(GitDistributedSettingsScope.Local, GitLabSettings.ApiTokenKey, "new-explicit-token");
        await session.SaveFilesAsync(default);
        GitXmlSettingsSnapshot saved = await f.ReadAsync(GitDistributedSettingsScope.Local);
        Assert.That(saved.Values[GitLabSettings.InstanceUrlKey], Is.EqualTo(destination));
        Assert.That(saved.Values[GitLabSettings.ApiTokenKey], Is.EqualTo("new-explicit-token"));
    }

    [Test]
    public async Task TokenWithoutItsStoredInstanceIsNotSilentlyBoundToARemoteUntilExplicitReentry()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        f.Write(GitDistributedSettingsScope.Global, (GitLabSettings.EnableKey, "true"),
            (GitLabSettings.ProviderKey, GitLabSettings.ProviderName), (GitLabSettings.ProjectIdKey, "12"),
            (GitLabSettings.ApiTokenKey, "orphan-token"));
        await f.Git.GitAsync("remote", "add", "origin", "https://gitlab.example/group/repo.git");
        GitHubActionsSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        Dictionary<string, byte[]> material = f.Material();
        session.Set(GitDistributedSettingsScope.Local, GitLabSettings.ProjectIdKey, "19");
        Assert.ThrowsAsync<ArgumentException>(async () => await session.SaveFilesAsync(default)); f.AssertMaterial(material);
        session.Set(GitDistributedSettingsScope.Global, GitLabSettings.ApiTokenKey, "explicit-token");
        await session.SaveFilesAsync(default);
        GitDistributedSettingsSnapshot snapshot = await GitDistributedSettingsSnapshot.LoadAsync(DetailedRepositorySettings.GlobalPath,
            await f.Repository!.GetInfoAsync(default), default);
        GitLabSettings accepted = GitLabSettings.Read(snapshot, session.RemoteUrls);
        Assert.That(accepted.InstanceUrl, Is.EqualTo("https://gitlab.example"));
        Assert.That(accepted.ApiToken, Is.EqualTo("explicit-token")); Assert.That(accepted.ProjectId, Is.EqualTo(19));
    }

    [Test]
    public async Task InvalidActiveProjectDoesNotPublishEitherScope()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        GitHubActionsSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        Dictionary<string, byte[]> material = f.Material();
        session.Set(GitDistributedSettingsScope.Local, GitLabSettings.ProjectIdKey, "not-an-id");
        session.Set(GitDistributedSettingsScope.Global, GitLabSettings.ApiTokenKey, "not-yet-published");
        Assert.ThrowsAsync<ArgumentException>(async () => await session.SaveFilesAsync(default));
        f.AssertMaterial(material);
    }

    [AvaloniaTest]
    public async Task BrowserPublishesLatestPipelineInPlaceAndUsesAnExplicitExternalBuildLink()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        f.Desktop.Save(new() { Language = "English", StartWithRecentWorkingDir = false });
        string sha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        FakeClient client = new(sha); int factories = 0; List<ProcessStartInfo> opened = [];
        MainWindow window = new() { Width = 1200, Height = 780, GitHubActionsPollInterval = Timeout.InfiniteTimeSpan };
        window.GitLabClientFactory = settings => { factories++; Assert.That(settings.ProjectId, Is.EqualTo(12)); return client; };
        window.ExternalLauncher = info => { opened.Add(info); return null; }; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(() => window.BuildServerStatusFor(sha) is not null); Pump(window);
            ListBox history = window.GetVisualDescendants().OfType<ListBox>().Single(c => c.Name == "RevisionHistory");
            object? source = history.ItemsSource, selected = history.SelectedItem;
            Control row = history.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "RevisionHistoryRow" && RevisionHistoryRow.CommitHash(c) == sha);
            history.Focus();
            Assert.That(window.GitHubActionsStatusFor(sha), Is.Null);
            Assert.That(window.BuildServerStatusFor(sha)!.Status, Is.EqualTo(BuildServerBuildStatus.Success));
            TabControl details = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "DetailsTabs");
            details.SelectedItem = details.Items.OfType<TabItem>().Single(tab => tab.Name == "GitHubActionsBuildReport"); Pump(window);
            HyperlinkButton reportLink = window.GetVisualDescendants().OfType<HyperlinkButton>().Single(control => control.Name == "BuildReportOpenReport");
            Assert.That(reportLink.IsEffectivelyVisible, Is.True); Assert.That(reportLink.Content, Is.EqualTo("Open report"));
            Assert.That(((BuildServerRun)reportLink.Tag!).PageUri!.AbsoluteUri, Is.EqualTo("https://gitlab.example/one/group/repo/-/pipelines/3")); history.Focus();
            Assert.That(opened, Is.Empty);
            client.Failure = true; await window.RefreshGitHubActionsNowAsync(); Pump(window);
            Assert.That(factories, Is.EqualTo(1)); Assert.That(window.BuildServerStatusFor(sha)!.Status, Is.EqualTo(BuildServerBuildStatus.Failure));
            Assert.That(window.GetVisualDescendants().OfType<HyperlinkButton>().Single(control => control.Name == "BuildReportOpenReport"), Is.SameAs(reportLink));
            Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected));
            Assert.That(history.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "RevisionHistoryRow" && RevisionHistoryRow.CommitHash(c) == sha), Is.SameAs(row));
            Assert.That(history.IsKeyboardFocusWithin, Is.True);
            await window.ShowBuildPageAsync();
            Assert.That(opened.Single().ArgumentList, Is.EqualTo(new[] { "https://gitlab.example/one/group/repo/-/pipelines/3" }));
            GitHubActionsSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
            session.Set(GitDistributedSettingsScope.Local, GitLabSettings.EnableKey, "false"); await session.SaveFilesAsync(default);
            await Until(() => !window.GitHubActionsEnabled); Pump(window);
            Assert.That(client.Disposed, Is.True); Assert.That(window.BuildServerStatusFor(sha), Is.Null);
            Assert.That(window.BuildGitHubActionsShowBuildPageMenu().IsEnabled, Is.False);
        }
        finally { await window.CloseGitHubActionsAsync(); window.Close(); }
    }

    [AvaloniaTest]
    public async Task ExternallyRetargetedInheritedTokenClearsOldStatusBeforeAnyNewClientOrLink()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f, "inherited-token");
        f.Desktop.Save(new() { Language = "English", StartWithRecentWorkingDir = false });
        string sha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        FakeClient client = new(sha); int factories = 0;
        MainWindow window = new() { GitHubActionsPollInterval = Timeout.InfiniteTimeSpan };
        window.GitLabClientFactory = _ => { factories++; return client; }; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(() => window.BuildServerStatusFor(sha) is not null);
            f.Write(GitDistributedSettingsScope.Local, (GitLabSettings.InstanceUrlKey, "https://gitlab.example/two/"));
            await window.RefreshGitHubActionsNowAsync(); Pump(window);
            Assert.That(factories, Is.EqualTo(1)); Assert.That(client.Disposed, Is.True);
            Assert.That(window.BuildServerStatusFor(sha), Is.Null); Assert.That(window.BuildGitHubActionsShowBuildPageMenu().IsEnabled, Is.False);
            Assert.That(window.GitHubActionsError, Is.Not.Null.And.Not.Contains("inherited-token"));
        }
        finally { await window.CloseGitHubActionsAsync(); window.Close(); }
    }

    [AvaloniaTest]
    public async Task SwitchingRepositoryAndClosingDrainTheActualGitlabReaderWithoutLatePublication()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        f.Desktop.Save(new() { Language = "English", StartWithRecentWorkingDir = false });
        List<FakeClient> clients = [];
        MainWindow window = new() { GitHubActionsPollInterval = Timeout.InfiniteTimeSpan };
        window.GitLabClientFactory = _ => { FakeClient client = new("") { Block = true }; clients.Add(client); return client; }; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository!.Path); await Until(() => clients.Count == 1 && clients[0].Started.Task.IsCompleted);
            string second = f.Git.Output("second"); await GitRepository.InitAsync(second, "main"); await window.OpenRepositoryAsync(second);
            await Until(() => clients.Count == 2 && clients[1].Started.Task.IsCompleted);
            Assert.That(clients[0].Finished.Task.IsCompleted, Is.True); Assert.That(clients[0].Disposed, Is.True);
            await window.CloseGitHubActionsAsync();
            Assert.That(clients[1].Finished.Task.IsCompleted, Is.True); Assert.That(clients[1].Disposed, Is.True);
            Assert.That(window.GitHubActionsCompletion.IsCompletedSuccessfully, Is.True);
            Assert.That(window.BuildServerStatusFor(new string('a', 40)), Is.Null);
        }
        finally { await window.CloseGitHubActionsAsync(); window.Close(); }
    }

    private static void Enable(DetailedSettingsTestScope f, string token = "") => f.Write(GitDistributedSettingsScope.Global,
        (GitLabSettings.EnableKey, "true"), (GitLabSettings.ShowPageKey, "true"), (GitLabSettings.ProviderKey, GitLabSettings.ProviderName),
        (GitLabSettings.InstanceUrlKey, "https://gitlab.example/one/"), (GitLabSettings.ProjectIdKey, "12"),
        (GitLabSettings.ApiTokenKey, token), (GitLabSettings.PagesLimitKey, "3"));
    private static void Pump(TopLevel window) => DetailedSettingsUiHost.Pump(window);
    private static Task Until(Func<bool> predicate) => DetailedSettingsUiHost.Until(predicate);
    private sealed class FakeClient(string sha) : IGitLabClient
    {
        internal bool Failure, Block, Disposed;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GitLabProject> GetProjectAsync(string projectNamespace, string projectName, CancellationToken cancellationToken = default)
            => Task.FromResult(new GitLabProject(12));
        public async Task<GitLabSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                if (Block) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                return new([new GitLabPipeline { Id = 3, ProjectId = 12, Sha = sha, Status = Failure ? "failed" : "success", Ref = "main", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                    WebUrl = "https://gitlab.example/one/group/repo/-/pipelines/3" }], DateTimeOffset.UtcNow);
            }
            finally { Finished.TrySetResult(); }
        }
        public Uri? BuildPageUri(GitLabPipeline pipeline) => new("https://gitlab.example/one/group/repo/-/pipelines/" + pipeline.Id);
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

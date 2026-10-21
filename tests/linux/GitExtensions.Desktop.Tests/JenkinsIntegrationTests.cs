using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitUIPluginInterfaces.BuildServerIntegration;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class JenkinsIntegrationTests
{
    private const string Endpoint = "https://jenkins.example.invalid/base/";

    [AvaloniaTest]
    public async Task RealXmlAndExplicitBasicFeedFactoryWhileMultiShaUpdatesPreserveRowsSelectionFocusAndReport()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        await f.Git.ChangeAsync("second commit\n", "Second commit"); GitCommit[] commits = (await f.Repository!.GetLogAsync(2, all: false)).ToArray();
        string sha = commits[0].Hash, second = commits[1].Hash; FakeClient client = new([sha, second]);
        BuildServerCredentialsStore store = CredentialStore(f, BuildServerCredentialsType.UsernameAndPassword); List<ProcessStartInfo> opened = []; int factories = 0;
        MainWindow window = Browser(f); window.CredentialsStoreOverride = store;
        window.JenkinsClientFactory = (settings, credentials) =>
        {
            factories++; Assert.That(settings.ServerUri.AbsoluteUri, Is.EqualTo(Endpoint));
            Assert.That(settings.ProjectName, Is.EqualTo("folder/job-one|job-two?m")); Assert.That(settings.IgnoreBuildBranch, Is.EqualTo("^archive/"));
            Assert.That(credentials.BuildServerCredentialsType, Is.EqualTo(BuildServerCredentialsType.UsernameAndPassword));
            Assert.That(credentials.Username, Is.EqualTo("explicit-user")); Assert.That(credentials.Password, Is.EqualTo("private-password")); return client;
        };
        window.ExternalLauncher = info => { opened.Add(info); return null; }; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(window, () => window.BuildServerStatusFor(sha) is not null && window.BuildServerStatusFor(second) is not null);
            Assert.That(window.GitHubActionsStatusFor(sha), Is.Null); Assert.That(window.BuildServerStatusFor(sha)!.Status, Is.EqualTo(BuildServerBuildStatus.Success));
            Assert.That(window.BuildServerStatusFor(second)!.Status, Is.EqualTo(BuildServerBuildStatus.Success));
            ListBox history = History(window); object? source = history.ItemsSource, selected = history.SelectedItem; Control row = Row(window, sha);
            Assert.That(Field<TextBlock>(row, "RevisionBuildStatus").Text, Does.Contain("#41 02:05 10 tests (1 failed, 2 skipped) Success"));
            TabControl details = Field<TabControl>(window, "DetailsTabs"); details.SelectedItem = details.Items.OfType<TabItem>().Single(tab => tab.Name == "GitHubActionsBuildReport"); Pump(window);
            ListBox runs = Field<ListBox>(window, "GitHubActionsRuns"); string stableId = ((BuildServerRun)runs.SelectedItem!).Id;
            Assert.That(stableId, Is.EqualTo("/base/job/folder/job-one/#41")); Assert.That(((BuildServerRun)runs.SelectedItem!).DurationMilliseconds, Is.EqualTo(125000));
            history.Focus(); Assert.That(history.IsKeyboardFocusWithin, Is.True); Assert.That(opened, Is.Empty);
            client.Failure = true; await window.RefreshGitHubActionsNowAsync(); Pump(window);
            Assert.That(factories, Is.EqualTo(1)); Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected)); Assert.That(Row(window, sha), Is.SameAs(row)); Assert.That(history.IsKeyboardFocusWithin, Is.True);
            Assert.That(window.BuildServerStatusFor(sha)!.Status, Is.EqualTo(BuildServerBuildStatus.Failure)); Assert.That(window.BuildServerStatusFor(second)!.Status, Is.EqualTo(BuildServerBuildStatus.Failure));
            Assert.That(((BuildServerRun)runs.SelectedItem!).Id, Is.EqualTo(stableId)); Assert.That(((BuildServerRun)runs.SelectedItem!).Description, Does.EndWith("Failure"));
            MenuItem build = window.BuildGitHubActionsShowBuildPageMenu(), pr = window.BuildServerPullRequestPageMenu();
            Assert.That(build.IsEnabled && build.IsVisible, Is.True); Assert.That((build.Icon as Image)?.Tag, Is.EqualTo("Integration.png")); Assert.That(pr.IsEnabled || pr.IsVisible, Is.False);
            await window.ShowBuildPageAsync(); Assert.That(opened.Single().ArgumentList, Is.EqualTo(new[] { FakeClient.BuildPage }));
        }
        finally { client.ReleaseAll(); await Close(window); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task JenkinsUsesTenSecondPollingWithTheExplicitInfiniteOverrideStillHonored(bool infinite)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f); string sha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        FakeClient client = new([sha]); MainWindow window = Browser(f, infinite); window.CredentialsStoreOverride = CredentialStore(f, BuildServerCredentialsType.Guest);
        window.JenkinsClientFactory = (_, credentials) => { Assert.That(credentials.BuildServerCredentialsType, Is.EqualTo(BuildServerCredentialsType.Guest)); return client; }; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(window, () => window.BuildServerStatusFor(sha) is not null);
            Assert.That(window.GitHubActionsPollInterval, Is.EqualTo(infinite ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(10))); Assert.That(client.Reads, Is.GreaterThanOrEqualTo(1));
        }
        finally { client.ReleaseAll(); await Close(window); }
    }

    [AvaloniaTest]
    public async Task MissingExplicitCredentialsNeverStartsClientOrAnAutomaticAuthenticationDialog()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f); MainWindow window = Browser(f);
        window.CredentialsStoreOverride = new(f.Git.Output("private-empty-jenkins")); int factories = 0;
        window.JenkinsClientFactory = (_, _) => { factories++; return new FakeClient([]); }; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository!.Path); await Until(window, () => window.GitHubActionsError is not null);
            Assert.That(factories, Is.Zero); Assert.That(window.OwnedWindows, Is.Empty); Assert.That(window.BuildGitHubActionsShowBuildPageMenu().IsEnabled, Is.False);
            Assert.That(window.GitHubActionsError, Does.Contain("credentials").IgnoreCase);
        }
        finally { await Close(window); }
    }

    [AvaloniaTest]
    [TestCase("retarget")] [TestCase("disabled")]
    public async Task EndpointChangeOrDisableClearsOldRowsAndMenuBeforeReplacementWithoutReusingPreviousCredentials(string change)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f); string sha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        FakeClient client = new([sha]); int factories = 0; List<ProcessStartInfo> opened = []; MainWindow window = Browser(f);
        window.CredentialsStoreOverride = CredentialStore(f, BuildServerCredentialsType.Guest); window.JenkinsClientFactory = (_, _) => { factories++; return client; };
        window.ExternalLauncher = info => { opened.Add(info); return null; }; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(window, () => window.BuildServerStatusFor(sha) is not null); MenuItem menu = window.BuildGitHubActionsShowBuildPageMenu(); Assert.That(menu.IsEnabled && menu.IsVisible, Is.True);
            GitHubActionsSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
            session.Set(GitDistributedSettingsScope.Local, change == "disabled" ? JenkinsSettings.EnableKey : JenkinsSettings.ServerUrlKey, change == "disabled" ? "false" : "https://jenkins.example.invalid/another/"); await session.SaveFilesAsync(default);
            await Until(window, () => client.Disposed && window.BuildServerStatusFor(sha) is null && (change == "disabled" ? !window.GitHubActionsEnabled : window.GitHubActionsError is not null));
            Assert.That(factories, Is.EqualTo(1)); Assert.That(menu.IsEnabled || menu.IsVisible, Is.False); await window.ShowBuildPageAsync(); Assert.That(opened, Is.Empty);
        }
        finally { client.ReleaseAll(); await Close(window); }
    }

    [AvaloniaTest]
    public async Task SwitchAndCloseDrainUncooperativeReadAndAsynchronousDisposalWithoutPublishingOldBuilds()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f); string oldSha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        string second = f.Git.Output("second-jenkins-repository"); await GitRepository.InitAsync(second, "main");
        await f.Git.GitAsync("-C", second, "-c", "user.name=Tester", "-c", "user.email=tester@example.invalid", "commit", "--allow-empty", "-m", "Second repository");
        string newSha = (await new GitRepository(second).GetLogAsync(1, all: false)).Single().Hash;
        List<FakeClient> clients = []; MainWindow window = Browser(f); window.CredentialsStoreOverride = CredentialStore(f, BuildServerCredentialsType.Guest);
        window.JenkinsClientFactory = (_, _) => { FakeClient client = new([clients.Count == 0 ? oldSha : newSha]) { Block = true, BlockDisposal = clients.Count != 0 }; clients.Add(client); return client; }; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(window, () => clients.Count == 1 && clients[0].Started.Task.IsCompleted);
            Task switching = window.OpenRepositoryAsync(second); await Until(window, () => clients[0].Token.IsCancellationRequested);
            Assert.That(clients.Count, Is.EqualTo(1)); Assert.That(clients[0].Disposed, Is.False); clients[0].ReadRelease.TrySetResult(true);
            await Until(window, () => clients.Count == 2 && clients[1].Started.Task.IsCompleted); await switching.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(clients[0].Disposed, Is.True); Assert.That(window.BuildServerStatusFor(oldSha), Is.Null);
            Task closing = window.CloseGitHubActionsAsync(); await Until(window, () => clients[1].Token.IsCancellationRequested); Assert.That(closing.IsCompleted, Is.False);
            clients[1].ReadRelease.TrySetResult(true); await Until(window, () => clients[1].DisposeStarted.Task.IsCompleted); Assert.That(closing.IsCompleted, Is.False);
            clients[1].DisposeRelease.TrySetResult(true); await Until(window, () => closing.IsCompleted); await closing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(clients[1].Disposed, Is.True); Assert.That(window.GitHubActionsCompletion.IsCompletedSuccessfully, Is.True); Assert.That(window.BuildServerStatusFor(oldSha), Is.Null); Assert.That(window.BuildServerStatusFor(newSha), Is.Null);
        }
        finally { foreach (FakeClient client in clients) { client.ReleaseAll(); } await Close(window); }
    }

    [AvaloniaTest]
    public async Task ProviderDiagnosticIsVisibleWithoutInventingACommitStatusOrEnablingPageAction()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f); string sha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        FakeClient client = new([]) { Diagnostic = "Jenkins projects returned no builds." }; MainWindow window = Browser(f);
        window.CredentialsStoreOverride = CredentialStore(f, BuildServerCredentialsType.Guest); window.JenkinsClientFactory = (_, _) => client; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(window, () => window.GitHubActionsError == client.Diagnostic);
            Assert.That(window.BuildServerStatusFor(sha), Is.Null); Assert.That(window.BuildGitHubActionsShowBuildPageMenu().IsEnabled, Is.False); Assert.That(window.OwnedWindows, Is.Empty);
        }
        finally { client.ReleaseAll(); await Close(window); }
    }

    [Test]
    public async Task ScopedCredentialDraftsArePrivateProviderAndFullEndpointSpecificAndOnlyApplyWritesThem()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f); BuildServerCredentialsStore store = new(f.Git.Output("private-jenkins"));
        GitHubActionsSettingsSession canceled = new(f.Repository) { CredentialsStore = store }; await canceled.InitializeAsync(default); Dictionary<string, byte[]> before = f.Material();
        Uri one = new(Endpoint), two = new("https://jenkins.example.invalid/another/");
        canceled.StageCredentials(JenkinsSettings.ProviderName, one, Basic()); canceled.Set(GitDistributedSettingsScope.Local, JenkinsSettings.ProjectNameKey, "discarded");
        Assert.That(File.Exists(store.FilePath), Is.False); f.AssertMaterial(before);
        GitHubActionsSettingsSession saved = new(f.Repository) { CredentialsStore = store }; await saved.InitializeAsync(default);
        saved.StageCredentials(JenkinsSettings.ProviderName, one, Basic()); saved.StageCredentials(TeamCitySettings.ProviderName, one, new() { BuildServerCredentialsType = BuildServerCredentialsType.Guest });
        saved.StageCredentials(JenkinsSettings.ProviderName, two, new() { BuildServerCredentialsType = BuildServerCredentialsType.Guest }); await saved.SaveFilesAsync(default);
        Assert.That(saved.HasChanges, Is.False); f.AssertMaterial(before);
        Assert.That(store.Load(JenkinsSettings.ProviderName, one).Credentials.Password, Is.EqualTo("private-password"));
        Assert.That(store.Load(JenkinsSettings.ProviderName, two).Credentials.BuildServerCredentialsType, Is.EqualTo(BuildServerCredentialsType.Guest));
        Assert.That(store.Load(TeamCitySettings.ProviderName, one).Credentials.BuildServerCredentialsType, Is.EqualTo(BuildServerCredentialsType.Guest));
        Assert.That(store.Load(JenkinsSettings.ProviderName, new Uri("https://jenkins.example.invalid:444/base/")).Exists, Is.False);
        BuildServerCredentials copy = saved.CredentialDraft(JenkinsSettings.ProviderName, one); copy.Password = "caller-mutated";
        Assert.That(saved.CredentialDraft(JenkinsSettings.ProviderName, one).Password, Is.EqualTo("private-password"));
        foreach (string path in f.XmlPaths) { Assert.That(File.ReadAllText(path), Does.Not.Contain("private-password")); }
    }

    private static BuildServerCredentials Basic() => new() { BuildServerCredentialsType = BuildServerCredentialsType.UsernameAndPassword, Username = "explicit-user", Password = "private-password" };
    private static void Enable(DetailedSettingsTestScope f) => f.Write(GitDistributedSettingsScope.Global,
        (JenkinsSettings.EnableKey, "true"), (JenkinsSettings.ShowPageKey, "true"), (JenkinsSettings.ProviderKey, JenkinsSettings.ProviderName),
        (JenkinsSettings.ServerUrlKey, Endpoint), (JenkinsSettings.ProjectNameKey, "folder/job-one|job-two?m"), (JenkinsSettings.IgnoreBuildBranchKey, "^archive/"), ("future.λ", "keep <literal> & \nbody"));
    private static BuildServerCredentialsStore CredentialStore(DetailedSettingsTestScope f, BuildServerCredentialsType mode)
    {
        BuildServerCredentialsStore store = new(f.Git.Output("private-ci")); store.SaveChanges(store.Load(JenkinsSettings.ProviderName, new Uri(Endpoint)), mode == BuildServerCredentialsType.Guest ? new() { BuildServerCredentialsType = mode } : Basic()); return store;
    }
    private static MainWindow Browser(DetailedSettingsTestScope f, bool infinite = true)
    {
        f.Desktop.Save(new() { Language = "English", StartWithRecentWorkingDir = false });
        new HistoryViewPreferencesStore().Save(new() { ShowArtificialCommits = false, ShowBuildStatusIcon = true, ShowBuildStatusText = true, ShowAvatar = false });
        MainWindow window = new() { Width = 1200, Height = 780 }; if (infinite) { window.GitHubActionsPollInterval = Timeout.InfiniteTimeSpan; } return window;
    }
    private static ListBox History(MainWindow window) => window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "RevisionHistory");
    private static Control Row(MainWindow window, string sha) => History(window).GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionHistoryRow" && RevisionHistoryRow.CommitHash(control) == sha);
    private static T Field<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(TopLevel window) => DetailedSettingsUiHost.Pump(window);
    private static async Task Until(MainWindow window, Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Pump(window); await Task.Delay(5); }
        Pump(window); Assert.That(condition(), Is.True, "The Jenkins browser did not reach its expected state in ten seconds.");
    }
    private static async Task Close(MainWindow window)
    {
        Task closing = window.CloseGitHubActionsAsync(); await Until(window, () => closing.IsCompleted); await closing.WaitAsync(TimeSpan.FromSeconds(10));
        if (window.IsVisible) { window.Close(); await Until(window, () => !window.IsVisible); }
    }
    private sealed class FakeClient(IReadOnlyList<string> hashes) : IJenkinsClient
    {
        internal const string BuildPage = Endpoint + "job/folder/job-one/41/";
        internal bool Failure, Block, BlockDisposal, Disposed;
        internal int Reads;
        internal string? Diagnostic;
        internal CancellationToken Token;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<JenkinsSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            Reads++; Token = cancellationToken; Started.TrySetResult(); if (Block) { await ReadRelease.Task; }
            if (Diagnostic is not null) { return new([], DateTimeOffset.UtcNow, diagnostics: [Diagnostic]); }
            return new([new JenkinsBuild
            {
                ProjectId = "/base/job/folder/job-one/", ProjectName = "folder/job-one", Number = 41, CommitHashes = hashes,
                RawResult = Failure ? "FAILURE" : "SUCCESS", Status = Failure ? JenkinsBuildStatus.Failure : JenkinsBuildStatus.Success,
                StartedAt = DateTimeOffset.Parse("2026-10-19T10:00:00Z"), DurationMilliseconds = 125000,
                TestResults = new(10, 1, 2), PageUri = new(BuildPage)
            }], DateTimeOffset.UtcNow);
        }
        public Uri? BuildPageUri(JenkinsBuild build) => build.PageUri;
        public async ValueTask DisposeAsync()
        { DisposeStarted.TrySetResult(); if (BlockDisposal) { await DisposeRelease.Task; } Disposed = true; }
        internal void ReleaseAll() { ReadRelease.TrySetResult(true); DisposeRelease.TrySetResult(true); }
    }
}

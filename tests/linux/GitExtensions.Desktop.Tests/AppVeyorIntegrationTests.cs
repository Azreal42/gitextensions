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
public sealed class AppVeyorIntegrationTests
{
    [AvaloniaTest]
    public async Task ActualXmlFactoryReceivesExactlyLoadedHashesAndMutableVersionUpdatesTheSameRowsSelectionFocusAndTooltip()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        await f.Git.ChangeAsync("second commit\n", "Second commit"); await f.Git.ChangeAsync("third commit\n", "Third commit");
        string sha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        FakeClient client = new(sha); List<HashSet<string>> admitted = []; List<AppVeyorSettings> configurations = []; List<ProcessStartInfo> opened = [];
        MainWindow window = Browser(f);
        window.AppVeyorClientFactory = (settings, hashes) => { configurations.Add(settings); admitted.Add(hashes.ToHashSet(StringComparer.OrdinalIgnoreCase)); return client; };
        window.ExternalLauncher = info => { opened.Add(info); return null; }; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(window, () => window.BuildServerStatusFor(sha) is not null);
            ListBox history = History(window); GitCommit[] visible = history.Items.OfType<GitCommit>().ToArray();
            HashSet<string> expected = visible.Select(commit => commit.Hash).Where(AppVeyorBuild.IsCommitHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.That(expected.Count, Is.GreaterThanOrEqualTo(3)); Assert.That(admitted.Single(), Is.EquivalentTo(expected));
            Assert.That(configurations.Single().AccountName, Is.EqualTo("sample-account")); Assert.That(configurations.Single().AccountToken, Is.EqualTo("private-fake-token"));
            Assert.That(configurations.Single().ProjectName, Is.EqualTo("sample-account/project")); Assert.That(configurations.Single().LoadTestsResults, Is.True);
            Assert.That(window.GitHubActionsStatusFor(sha), Is.Null); Assert.That(window.BuildServerStatusFor(sha)!.Status, Is.EqualTo(BuildServerBuildStatus.Success));
            TabControl details = Field<TabControl>(window, "DetailsTabs");
            TabItem report = details.Items.OfType<TabItem>().Single(item => item.Name == "GitHubActionsBuildReport"); details.SelectedItem = report; Pump(window);
            ListBox runs = Field<ListBox>(window, "GitHubActionsRuns");
            BuildServerRun selectedRun = (BuildServerRun)runs.SelectedItem!;
            Assert.That(selectedRun.Id, Is.EqualTo("17"), "Numerical BuildId is stable while Version is a mutable display label.");
            Assert.That(selectedRun.DurationMilliseconds, Is.EqualTo(125000)); Assert.That(selectedRun.PullRequestUri?.AbsoluteUri, Is.EqualTo(FakeClient.PullRequest));
            object? source = history.ItemsSource, selectedCommit = history.SelectedItem;
            Control row = Row(window, sha); Control cell = Field<Control>(row, "RevisionBuildStatusCell");
            Assert.That(ToolTip.GetTip(cell), Is.EqualTo(ExpectedTooltip("Success", "1.0.0")));
            Assert.That(Field<TextBlock>(row, "RevisionBuildStatus").Text, Does.Contain("10 tests ( 1 failed, 2 skipped )").And.Contain("PR#42").And.Contain("1.0.0"));
            history.Focus(); Assert.That(history.IsKeyboardFocusWithin, Is.True); Assert.That(opened, Is.Empty, "Publishing builds must not launch their pages.");
            client.Version = "2.4.8"; client.Failure = true; await window.RefreshGitHubActionsNowAsync(); Pump(window);
            Assert.That(configurations.Count, Is.EqualTo(1)); Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selectedCommit));
            Assert.That(Row(window, sha), Is.SameAs(row)); Assert.That(Field<Control>(row, "RevisionBuildStatusCell"), Is.SameAs(cell));
            Assert.That(history.IsKeyboardFocusWithin, Is.True); Assert.That(window.BuildServerStatusFor(sha)!.Status, Is.EqualTo(BuildServerBuildStatus.Failure));
            Assert.That(Field<Image>(row, "RevisionBuildStatusIcon").Tag, Is.EqualTo("StatusBadgeError.png"));
            Assert.That(((BuildServerRun)runs.SelectedItem!).Id, Is.EqualTo("17")); Assert.That(((BuildServerRun)runs.SelectedItem!).Description, Does.Contain("2.4.8").And.Not.Contain("1.0.0"));
            Assert.That(ToolTip.GetTip(cell), Is.EqualTo(ExpectedTooltip("Failure", "2.4.8")));
            MenuItem build = window.BuildGitHubActionsShowBuildPageMenu(), pullRequest = window.BuildServerPullRequestPageMenu();
            Assert.That(build.IsEnabled && build.IsVisible && pullRequest.IsEnabled && pullRequest.IsVisible, Is.True);
            Assert.That((build.Icon as Image)?.Tag, Is.EqualTo("Integration.png")); Assert.That((pullRequest.Icon as Image)?.Tag, Is.EqualTo("PullRequest.png"));
            await window.ShowBuildPageAsync(); await window.ShowBuildPullRequestAsync();
            Assert.That(opened.Select(info => info.FileName), Is.All.EqualTo("xdg-open"));
            Assert.That(opened.Select(info => info.ArgumentList.Single()), Is.EqualTo(new[] { FakeClient.BuildPage("2.4.8"), FakeClient.PullRequest }));
            client.HasPullRequest = false; await window.RefreshGitHubActionsNowAsync(); Pump(window);
            Assert.That(build.IsEnabled && build.IsVisible, Is.True); Assert.That(pullRequest.IsEnabled || pullRequest.IsVisible, Is.False); int launches = opened.Count; await window.ShowBuildPullRequestAsync(); Assert.That(opened.Count, Is.EqualTo(launches));
            Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.IsKeyboardFocusWithin, Is.True);
        }
        finally { client.ReleaseAll(); await Close(window); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task AppVeyorChoosesOriginalTenSecondPollingUnlessTheExplicitInfiniteOverrideIsPresent(bool infinite)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        string sha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash; FakeClient client = new(sha);
        MainWindow window = Browser(f, infinite: infinite); window.AppVeyorClientFactory = (_, _) => client; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(window, () => window.BuildServerStatusFor(sha) is not null);
            Assert.That(window.GitHubActionsPollInterval, Is.EqualTo(infinite ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(10)));
            Assert.That(client.Reads, Is.GreaterThanOrEqualTo(1)); if (infinite) { Assert.That(client.Reads, Is.EqualTo(1)); }
        }
        finally { client.ReleaseAll(); await Close(window); }
    }

    [AvaloniaTest]
    [TestCase("disabled")] [TestCase("retargeted")]
    public async Task ProviderDisableOrFailedRetargetClearsOldBuildAndPullRequestLinksBeforeAnyNewPublication(string change)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        string sha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash; FakeClient client = new(sha);
        int factories = 0; List<ProcessStartInfo> opened = []; MainWindow window = Browser(f);
        window.AppVeyorClientFactory = (settings, _) =>
        {
            factories++; if (factories != 1) { Assert.That(settings.ProjectName, Is.EqualTo("other-account/other-project")); throw new ArgumentException("Replacement client rejected."); }
            return client;
        };
        window.ExternalLauncher = info => { opened.Add(info); return null; }; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(window, () => window.BuildServerStatusFor(sha) is not null);
            MenuItem build = window.BuildGitHubActionsShowBuildPageMenu(), pullRequest = window.BuildServerPullRequestPageMenu();
            Assert.That(build.IsEnabled && build.IsVisible && pullRequest.IsEnabled && pullRequest.IsVisible, Is.True); Assert.That(opened, Is.Empty);
            GitHubActionsSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
            session.Set(GitDistributedSettingsScope.Local, change == "disabled" ? AppVeyorSettings.EnableKey : AppVeyorSettings.ProjectNameKey,
                change == "disabled" ? "false" : "other-account/other-project"); await session.SaveFilesAsync(default);
            await Until(window, () => client.Disposed && window.BuildServerStatusFor(sha) is null && (change == "disabled" ? !window.GitHubActionsEnabled : window.GitHubActionsError is not null));
            Assert.That(build.IsEnabled || build.IsVisible || pullRequest.IsEnabled || pullRequest.IsVisible, Is.False); Assert.That(window.BuildGitHubActionsShowBuildPageMenu().IsEnabled, Is.False);
            Assert.That(window.BuildServerPullRequestPageMenu().IsEnabled, Is.False); int before = opened.Count;
            await window.ShowBuildPageAsync(); await window.ShowBuildPullRequestAsync(); Assert.That(opened.Count, Is.EqualTo(before));
            Assert.That(factories, Is.EqualTo(change == "disabled" ? 1 : 2));
        }
        finally { client.ReleaseAll(); await Close(window); }
    }

    [AvaloniaTest]
    public async Task RepositorySwitchAndCloseCancelDrainUncooperativeReadsAndClientDisposalWithoutLateStatus()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        string oldSha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        string second = f.Git.Output("second-appveyor-repository"); await GitRepository.InitAsync(second, "main");
        await f.Git.GitAsync("-C", second, "-c", "user.name=Tester", "-c", "user.email=tester@example.invalid", "commit", "--allow-empty", "-m", "Second repository");
        string newSha = (await new GitRepository(second).GetLogAsync(1, all: false)).Single().Hash;
        List<FakeClient> clients = []; MainWindow window = Browser(f);
        window.AppVeyorClientFactory = (_, hashes) =>
        {
            string sha = clients.Count == 0 ? oldSha : newSha; Assert.That(hashes, Does.Contain(sha));
            FakeClient client = new(sha) { Block = true, BlockDisposal = clients.Count != 0 }; clients.Add(client); return client;
        };
        window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(window, () => clients.Count == 1 && clients[0].Started.Task.IsCompleted);
            Task switching = window.OpenRepositoryAsync(second); await Until(window, () => clients[0].Token.IsCancellationRequested);
            Assert.That(clients.Count, Is.EqualTo(1), "The replacement client waits for the old raw read to finish."); Assert.That(clients[0].Disposed, Is.False);
            clients[0].ReadRelease.TrySetResult(true); await Until(window, () => clients.Count == 2 && clients[1].Started.Task.IsCompleted);
            await switching.WaitAsync(TimeSpan.FromSeconds(10)); Assert.That(clients[0].Disposed, Is.True); Assert.That(window.BuildServerStatusFor(oldSha), Is.Null);
            Task closing = window.CloseGitHubActionsAsync(); await Until(window, () => clients[1].Token.IsCancellationRequested);
            Assert.That(closing.IsCompleted, Is.False); Assert.That(clients[1].Disposed, Is.False);
            clients[1].ReadRelease.TrySetResult(true); await Until(window, () => clients[1].DisposeStarted.Task.IsCompleted);
            Assert.That(closing.IsCompleted, Is.False, "The drain includes asynchronous provider disposal.");
            clients[1].DisposeRelease.TrySetResult(true); await Until(window, () => closing.IsCompleted); await closing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(clients[1].Disposed, Is.True); Assert.That(window.GitHubActionsCompletion.IsCompletedSuccessfully, Is.True);
            Assert.That(window.BuildServerStatusFor(oldSha), Is.Null); Assert.That(window.BuildServerStatusFor(newSha), Is.Null);
        }
        finally { foreach (FakeClient client in clients) { client.ReleaseAll(); } await Close(window); }
    }

    private static void Enable(DetailedSettingsTestScope f) => f.Write(GitDistributedSettingsScope.Global,
        (AppVeyorSettings.EnableKey, "true"), (AppVeyorSettings.ShowPageKey, "true"), (AppVeyorSettings.ProviderKey, AppVeyorSettings.ProviderName),
        (AppVeyorSettings.AccountNameKey, "sample-account"), (AppVeyorSettings.AccountTokenKey, "private-fake-token"),
        (AppVeyorSettings.ProjectNameKey, "sample-account/project"), (AppVeyorSettings.LoadTestsResultsKey, "true"), ("future.λ", "keep <literal> & \nbody"));
    private static MainWindow Browser(DetailedSettingsTestScope f, bool infinite = true)
    {
        f.Desktop.Save(new() { Language = "English", StartWithRecentWorkingDir = false });
        new HistoryViewPreferencesStore().Save(new() { ShowArtificialCommits = false, ShowBuildStatusIcon = true, ShowBuildStatusText = true, ShowAvatar = false });
        MainWindow window = new() { Width = 1200, Height = 780 }; if (infinite) { window.GitHubActionsPollInterval = Timeout.InfiniteTimeSpan; } return window;
    }
    private static string ExpectedTooltip(string status, string version) => string.Join(Environment.NewLine,
        status, "02:05", "10 tests ( 1 failed, 2 skipped )", "PR#42: Keep original pull request title", version);
    private static ListBox History(MainWindow window) => window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "RevisionHistory");
    private static Control Row(MainWindow window, string sha) => History(window).GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionHistoryRow" && RevisionHistoryRow.CommitHash(control) == sha);
    private static T Field<T>(Control control, string name) where T : Control => control.GetVisualDescendants().OfType<T>().Single(child => child.Name == name);
    private static void Pump(TopLevel window) => DetailedSettingsUiHost.Pump(window);
    private static async Task Until(MainWindow window, Func<bool> predicate)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Pump(window); await Task.Delay(5); }
        Pump(window); Assert.That(predicate(), Is.True, "AppVeyor browser did not reach the expected state within ten seconds.");
    }
    private static async Task Close(MainWindow window)
    {
        Task closing = window.CloseGitHubActionsAsync(); await Until(window, () => closing.IsCompleted); await closing.WaitAsync(TimeSpan.FromSeconds(10));
        if (window.IsVisible) { window.Close(); await Until(window, () => !window.IsVisible); }
    }
    private sealed class FakeClient(string sha) : IAppVeyorClient
    {
        internal const string PullRequest = "https://github.com/example/repository/pull/42";
        internal string Version = "1.0.0";
        internal bool Failure, Block, BlockDisposal, Disposed, HasPullRequest = true;
        internal int Reads;
        internal CancellationToken Token;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal static string BuildPage(string version) => "https://ci.appveyor.com/project/sample-account/project/build/" + version;
        public async Task<AppVeyorSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            Reads++; Token = cancellationToken; Started.TrySetResult(); if (Block) { await ReadRelease.Task; }
            return new([new AppVeyorBuild
            {
                BuildId = 17, Version = Version, ProjectId = "sample-account/project", CommitHash = sha,
                RawStatus = Failure ? "failed" : "success", Status = Failure ? AppVeyorBuildStatus.Failure : AppVeyorBuildStatus.Success,
                StartedAt = DateTimeOffset.Parse("2026-10-19T10:00:00Z"), UpdatedAt = DateTimeOffset.Parse("2026-10-19T10:02:05Z"),
                DurationMilliseconds = 125000, Tests = new(10, 8, 1), PullRequestText = HasPullRequest ? "PR#42" : "",
                PullRequestTitle = HasPullRequest ? "Keep original pull request title" : "", PullRequestUri = HasPullRequest ? new Uri(PullRequest) : null,
                PageUri = new Uri(BuildPage(Version))
            }], DateTimeOffset.UtcNow);
        }
        public Uri? BuildPageUri(AppVeyorBuild build) => build.PageUri;
        public async ValueTask DisposeAsync()
        { DisposeStarted.TrySetResult(); if (BlockDisposal) { await DisposeRelease.Task; } Disposed = true; }
        internal void ReleaseAll() { ReadRelease.TrySetResult(true); DisposeRelease.TrySetResult(true); }
    }
}

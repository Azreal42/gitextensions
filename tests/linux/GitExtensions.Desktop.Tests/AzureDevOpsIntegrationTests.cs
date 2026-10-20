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
public sealed class AzureDevOpsIntegrationTests
{
    [AvaloniaTest]
    public async Task ActualXmlAndMutableBuildNumberPublishInPlaceWithoutLosingReportSelectionGridFocusOrOriginalTooltip()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        string sha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        FakeClient client = new(sha); List<AzureDevOpsSettings> configurations = []; List<ProcessStartInfo> opened = [];
        MainWindow window = Browser(f); window.AzureDevOpsClientFactory = settings => { configurations.Add(settings); return client; };
        window.ExternalLauncher = info => { opened.Add(info); return null; }; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(window, () => window.BuildServerStatusFor(sha) is not null); Pump(window);
            AzureDevOpsSettings settings = configurations.Single(); Assert.That(settings.Provider, Is.EqualTo(AzureDevOpsSettings.ProviderName));
            Assert.That(settings.ProjectUrl, Is.EqualTo(AzureDevOpsSettingsUiFixture.Project)); Assert.That(settings.BuildDefinitionFilter, Is.EqualTo("pipeline.*"));
            Assert.That(settings.ApiToken, Is.EqualTo("fake-private-pat")); Assert.That(settings.RepositoryName, Is.EqualTo("repository"));
            SelectReport(window); ListBox history = History(window); HyperlinkButton reportLink = Field<HyperlinkButton>(window, "BuildReportOpenReport"); object? source = history.ItemsSource, selection = history.SelectedItem;
            Control row = Row(window, sha), cell = Field<Control>(row, "RevisionBuildStatusCell");
            Assert.That(ToolTip.GetTip(cell), Is.EqualTo("20261019.1 ✔ - 02:05 [original-pipeline]" + Environment.NewLine + "PR #42"));
            Assert.That(reportLink.IsEffectivelyVisible, Is.True); Assert.That(reportLink.Content, Is.EqualTo("Open report"));
            Assert.That(((BuildServerRun)reportLink.Tag!).Id, Is.EqualTo("17")); history.Focus(); Assert.That(history.IsKeyboardFocusWithin, Is.True); Assert.That(opened, Is.Empty);
            client.Number = "20261019.2"; client.Failure = true; await window.RefreshGitHubActionsNowAsync(); Pump(window);
            Assert.That(configurations.Count, Is.EqualTo(1)); Assert.That(window.BuildServerStatusFor(sha)!.Status, Is.EqualTo(BuildServerBuildStatus.Failure));
            Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selection)); Assert.That(Row(window, sha), Is.SameAs(row));
            Assert.That(Field<Control>(row, "RevisionBuildStatusCell"), Is.SameAs(cell)); Assert.That(history.IsKeyboardFocusWithin, Is.True);
            Assert.That(Field<HyperlinkButton>(window, "BuildReportOpenReport"), Is.SameAs(reportLink));
            Assert.That(((BuildServerRun)reportLink.Tag!).Id, Is.EqualTo("17")); Assert.That(((BuildServerRun)reportLink.Tag!).Description, Is.EqualTo("02:05 20261019.2"));
            Assert.That(ToolTip.GetTip(cell), Is.EqualTo("20261019.2 ❌ - 02:05 [original-pipeline]" + Environment.NewLine + "PR #42"));
            Assert.That(Field<Image>(row, "RevisionBuildStatusIcon").Tag, Is.EqualTo("StatusBadgeError.png"));
            MenuItem build = window.BuildGitHubActionsShowBuildPageMenu(), pullRequest = window.BuildServerPullRequestPageMenu();
            Assert.That(build.IsEnabled && build.IsVisible && pullRequest.IsEnabled && pullRequest.IsVisible, Is.True);
            await window.ShowBuildPageAsync(); await window.ShowBuildPullRequestAsync();
            Assert.That(opened.Select(info => info.FileName), Is.All.EqualTo("xdg-open")); Assert.That(opened.Select(info => info.ArgumentList.Single()), Is.EqualTo(new[] { FakeClient.BuildPage, FakeClient.PullRequest }));
            client.HasPullRequest = false; await window.RefreshGitHubActionsNowAsync(); Pump(window);
            Assert.That(build.IsEnabled && build.IsVisible, Is.True); Assert.That(pullRequest.IsEnabled || pullRequest.IsVisible, Is.False);
        }
        finally { client.ReleaseAll(); await Close(window); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task AzureUsesOriginalTenSecondPollingUnlessExplicitInfiniteOverrideIsRetained(bool infinite)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        string sha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash; FakeClient client = new(sha);
        MainWindow window = Browser(f, infinite); window.AzureDevOpsClientFactory = _ => client; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(window, () => window.BuildServerStatusFor(sha) is not null);
            Assert.That(window.GitHubActionsPollInterval, Is.EqualTo(infinite ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(10)));
            Assert.That(client.Reads, Is.GreaterThanOrEqualTo(1)); if (infinite) { Assert.That(client.Reads, Is.EqualTo(1)); }
        }
        finally { client.ReleaseAll(); await Close(window); }
    }

    [AvaloniaTest]
    [TestCase("disabled")] [TestCase("inherited-pat-retarget")]
    public async Task ProviderOffOrExternalInheritedPatRetargetInvalidatesOldStatusAndAllLinksBeforeAnyReplacementClient(string change)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        string sha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash; FakeClient client = new(sha); int factories = 0;
        MainWindow window = Browser(f); List<ProcessStartInfo> opened = []; window.ExternalLauncher = info => { opened.Add(info); return null; };
        window.AzureDevOpsClientFactory = _ => { factories++; return client; }; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(window, () => window.BuildServerStatusFor(sha) is not null);
            MenuItem build = window.BuildGitHubActionsShowBuildPageMenu(), pullRequest = window.BuildServerPullRequestPageMenu();
            if (change == "disabled")
            {
                GitHubActionsSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
                session.Set(GitDistributedSettingsScope.Local, AzureDevOpsSettings.EnableKey, "false"); await session.SaveFilesAsync(default);
            }
            else
            {
                f.Write(GitDistributedSettingsScope.Local, (AzureDevOpsSettings.ProjectUrlKey, "https://dev.azure.com/acme/Other"));
                await window.RefreshGitHubActionsNowAsync();
            }
            await Until(window, () => client.Disposed && window.BuildServerStatusFor(sha) is null); Pump(window);
            Assert.That(factories, Is.EqualTo(1)); Assert.That(build.IsEnabled || build.IsVisible || pullRequest.IsEnabled || pullRequest.IsVisible, Is.False);
            await window.ShowBuildPageAsync(); await window.ShowBuildPullRequestAsync(); Assert.That(opened, Is.Empty);
            if (change != "disabled") { Assert.That(window.GitHubActionsError, Is.Not.Null.And.Not.Contains("fake-private-pat")); }
        }
        finally { client.ReleaseAll(); await Close(window); }
    }

    [AvaloniaTest]
    public async Task NoMatchingDefinitionsPublishesTheFixedDiagnosticAndClearsOldResultsInsteadOfClaimingSuccess()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        string sha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash; FakeClient client = new(sha);
        MainWindow window = Browser(f); window.AzureDevOpsClientFactory = _ => client; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(window, () => window.BuildServerStatusFor(sha) is not null);
            MenuItem build = window.BuildGitHubActionsShowBuildPageMenu(), pull = window.BuildServerPullRequestPageMenu();
            client.NoDefinitions = true; await window.RefreshGitHubActionsNowAsync(); Pump(window);
            Assert.That(Field<TabControl>(window, "DetailsTabs").Items.OfType<TabItem>().Any(tab => tab.Name == "GitHubActionsBuildReport"), Is.False);
            Assert.That(window.GitHubActionsEnabled, Is.True); Assert.That(window.BuildServerStatusFor(sha), Is.Null);
            Assert.That(window.GitHubActionsError, Is.EqualTo("No matching Azure DevOps build definition was found."));
            TextBlock notice = Field<TextBlock>(window, "RepositoryStatus"); Assert.That(notice.IsEffectivelyVisible, Is.True); Assert.That(notice.Text, Does.EndWith("No matching Azure DevOps build definition was found."));
            Assert.That(build.IsEnabled || build.IsVisible || pull.IsEnabled || pull.IsVisible, Is.False);
        }
        finally { client.ReleaseAll(); await Close(window); }
    }

    [AvaloniaTest]
    public async Task SwitchingAndClosingDrainRawProviderReadsAndDisposalWithoutLateOldRepositoryPublication()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        string oldSha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        string other = f.Git.Output("other-azure-repository"); await GitRepository.InitAsync(other, "main");
        await f.Git.GitAsync("-C", other, "-c", "user.name=Tester", "-c", "user.email=tester@example.invalid", "commit", "--allow-empty", "-m", "Other repository");
        string newSha = (await new GitRepository(other).GetLogAsync(1, all: false)).Single().Hash;
        List<FakeClient> clients = []; MainWindow window = Browser(f);
        window.AzureDevOpsClientFactory = _ => { FakeClient client = new(clients.Count == 0 ? oldSha : newSha) { Block = true, BlockDisposal = clients.Count != 0 }; clients.Add(client); return client; };
        window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(window, () => clients.Count == 1 && clients[0].Started.Task.IsCompleted);
            Task switching = window.OpenRepositoryAsync(other); await Until(window, () => clients[0].Token.IsCancellationRequested);
            Assert.That(clients.Count, Is.EqualTo(1)); Assert.That(clients[0].Disposed, Is.False);
            clients[0].ReadRelease.TrySetResult(true); await Until(window, () => clients.Count == 2 && clients[1].Started.Task.IsCompleted); await switching.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(clients[0].Disposed, Is.True); Assert.That(window.BuildServerStatusFor(oldSha), Is.Null);
            Task closing = window.CloseGitHubActionsAsync(); await Until(window, () => clients[1].Token.IsCancellationRequested);
            Assert.That(closing.IsCompleted, Is.False); clients[1].ReadRelease.TrySetResult(true); await Until(window, () => clients[1].DisposeStarted.Task.IsCompleted);
            Assert.That(closing.IsCompleted, Is.False); clients[1].DisposeRelease.TrySetResult(true); await Until(window, () => closing.IsCompleted); await closing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(clients[1].Disposed, Is.True); Assert.That(window.BuildServerStatusFor(oldSha), Is.Null); Assert.That(window.BuildServerStatusFor(newSha), Is.Null);
            Assert.That(window.GitHubActionsCompletion.IsCompletedSuccessfully, Is.True);
        }
        finally { foreach (FakeClient client in clients) { client.ReleaseAll(); } await Close(window); }
    }

    private static void Enable(DetailedSettingsTestScope f) => f.Write(GitDistributedSettingsScope.Global,
        (AzureDevOpsSettings.EnableKey, "true"), (AzureDevOpsSettings.ShowPageKey, "true"), (AzureDevOpsSettings.ProviderKey, AzureDevOpsSettings.ProviderName),
        (AzureDevOpsSettings.ProjectUrlKey, AzureDevOpsSettingsUiFixture.Project), (AzureDevOpsSettings.ApiTokenKey, "fake-private-pat"),
        (AzureDevOpsSettings.BuildDefinitionFilterKey, "pipeline.*"), (AzureDevOpsSettings.RepositoryNameKey, "repository"));
    private static MainWindow Browser(DetailedSettingsTestScope f, bool infinite = true)
    {
        f.Desktop.Save(new() { Language = "English", StartWithRecentWorkingDir = false });
        new HistoryViewPreferencesStore().Save(new() { ShowArtificialCommits = false, ShowBuildStatusIcon = true, ShowBuildStatusText = true, ShowAvatar = false });
        MainWindow window = new() { Width = 1200, Height = 780 }; if (infinite) { window.GitHubActionsPollInterval = Timeout.InfiniteTimeSpan; } return window;
    }
    private static ListBox History(MainWindow window) => window.GetVisualDescendants().OfType<ListBox>().Single(control => control.ItemsSource is IEnumerable<GitCommit>);
    private static Control Row(MainWindow window, string sha) => History(window).GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionHistoryRow" && RevisionHistoryRow.CommitHash(control) == sha);
    private static void SelectReport(MainWindow window)
    { PushProcessTestDriver.Pointer(window.GetVisualDescendants().OfType<TabItem>().Single(tab => tab.Name == "GitHubActionsBuildReport")); Pump(window); }
    private static T Field<T>(Control parent, string name) where T : Control => parent.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(TopLevel window) => DetailedSettingsUiHost.Pump(window);
    private static async Task Until(MainWindow window, Func<bool> predicate)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Pump(window); await Task.Delay(5); }
        Pump(window); Assert.That(predicate(), Is.True, "Azure DevOps browser did not reach the expected state in ten seconds.");
    }
    private static async Task Close(MainWindow window)
    {
        Task closing = window.CloseGitHubActionsAsync(); await Until(window, () => closing.IsCompleted); await closing.WaitAsync(TimeSpan.FromSeconds(10));
        if (window.IsVisible) { window.Close(); await Until(window, () => !window.IsVisible); }
    }
    private sealed class FakeClient(string sha) : IAzureDevOpsClient
    {
        internal const string BuildPage = AzureDevOpsSettingsUiFixture.Project + "/_build/results?buildId=17";
        internal const string PullRequest = AzureDevOpsSettingsUiFixture.Project + "/_git/repository/pullrequest/42";
        internal string Number = "20261019.1";
        internal bool Failure, HasPullRequest = true, Block, BlockDisposal, Disposed, NoDefinitions;
        internal int Reads;
        internal CancellationToken Token;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> GetBuildDefinitionNameFromIdAsync(long buildId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Runtime polling must not perform a Settings clipboard lookup.");
        public async Task<AzureDevOpsSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            Reads++; Token = cancellationToken; Started.TrySetResult(); if (Block) { await ReadRelease.Task; }
            if (NoDefinitions) { return new([], DateTimeOffset.UtcNow, diagnostics: ["No matching Azure DevOps build definition was found."]); }
            return new([new AzureDevOpsBuild
            {
                BuildId = 17, BuildNumber = Number, DefinitionId = 29, DefinitionName = "original-pipeline", SourceVersion = sha, CommitHash = sha,
                RawStatus = "completed", Result = Failure ? "failed" : "succeeded", Status = Failure ? AzureDevOpsBuildStatus.Failure : AzureDevOpsBuildStatus.Success,
                StartedAt = DateTimeOffset.Parse("2026-10-19T10:00:00Z"), FinishedAt = DateTimeOffset.Parse("2026-10-19T10:02:05Z"),
                DurationMilliseconds = 125000, DurationText = "02:05", PullRequestId = HasPullRequest ? "42" : "", PullRequestUri = HasPullRequest ? new Uri(PullRequest) : null,
                PageUri = new Uri(BuildPage)
            }], DateTimeOffset.UtcNow);
        }
        public Uri? BuildPageUri(AzureDevOpsBuild build) => build.PageUri;
        public async ValueTask DisposeAsync() { DisposeStarted.TrySetResult(); if (BlockDisposal) { await DisposeRelease.Task; } Disposed = true; }
        internal void ReleaseAll() { ReadRelease.TrySetResult(true); DisposeRelease.TrySetResult(true); }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class BuildReportParityTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static DateTimeOffset At(int seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);
    private static Uri Page(long id) => new("https://github.com/octo/repo/actions/runs/" + id);
    private static GitHubActionsWorkflowRun Run(long id, long workflow, int created, int start, int updated, string conclusion = "success", string? sha = null, bool url = true)
        => new() { Id = id, WorkflowId = workflow, HeadSha = sha ?? Sha, Name = "Workflow " + workflow, RunNumber = (int)id,
            CreatedAt = At(created), RunStartedAt = At(start), UpdatedAt = At(updated), Status = "completed", Conclusion = conclusion,
            HtmlUrl = url ? Page(id).AbsoluteUri : null };
    private static BuildServerSnapshot Snapshot(params GitHubActionsWorkflowRun[] runs)
        => BuildServerSnapshot.FromGitHubActions(new(runs, At(100)), run => Uri.TryCreate(run.HtmlUrl, UriKind.Absolute, out Uri? uri) ? uri : null);

    [Test]
    [TestCase("success", "failure", BuildServerBuildStatus.Failure)]
    [TestCase("failure", "success", BuildServerBuildStatus.Success)]
    public void RevisionProjectionUsesOneLatestStartedBuildWithoutDestroyingSiblingWorkflowData(string oldConclusion, string newConclusion, BuildServerBuildStatus expected)
    {
        BuildServerSnapshot snapshot = Snapshot(Run(1, 10, 1, 1, 9, oldConclusion), Run(2, 20, 4, 4, 6, newConclusion));
        BuildServerRevisionProjection projection = new(); BuildServerCommitStatus shown = projection.Publish(snapshot)[Sha];
        Assert.That(snapshot.Runs.Count, Is.EqualTo(2)); Assert.That(snapshot.Commits[Sha].Runs.Count, Is.EqualTo(2));
        Assert.That(shown.Status, Is.EqualTo(expected)); Assert.That(shown.Runs.Single().Id, Is.EqualTo("2"));
        Assert.That(shown.Text, Is.EqualTo(shown.Runs.Single().Description)); Assert.That(shown.Tooltip, Is.EqualTo(shown.Text));
        Assert.That(shown.Runs.Single().PageUri, Is.EqualTo(Page(2))); Assert.That(shown.Runs.Single().ShowInBuildReportTab, Is.False);
        Assert.That(shown.Runs.Single().DurationMilliseconds, Is.EqualTo(2000));
        if (oldConclusion == "failure") { Assert.That(snapshot.Commits[Sha].Status, Is.EqualTo(BuildServerBuildStatus.Failure), "The data aggregate remains available; it is not the Windows display projection."); }
    }

    [Test]
    public void AnOlderCreatedRunWithANewerRerunStartIsConsideredBeforeWorkflowReduction()
    {
        BuildServerSnapshot snapshot = Snapshot(Run(1, 10, 1, 9, 12, "failure"), Run(2, 10, 6, 6, 8));
        Assert.That(snapshot.Commits[Sha].Runs.Single().Id, Is.EqualTo("2"), "Retain the existing per-workflow data grouping.");
        BuildServerCommitStatus shown = new BuildServerRevisionProjection().Publish(snapshot)[Sha];
        Assert.That(shown.Runs.Single().Id, Is.EqualTo("1")); Assert.That(shown.Status, Is.EqualTo(BuildServerBuildStatus.Failure));
        Assert.That(shown.Runs.Single().PageUri, Is.EqualTo(Page(1)));
    }

    [Test]
    public void EqualStartsReplaceOnTheLastNewObservationIncludingTheSameIdTransitionButUnchangedPollsDoNotReplay()
    {
        BuildServerRevisionProjection projection = new();
        GitHubActionsWorkflowRun first = Run(1, 10, 1, 4, 8), second = Run(2, 20, 1, 4, 8, "failure");
        Assert.That(projection.Publish(Snapshot(first, second))[Sha].Runs.Single().Id, Is.EqualTo("2"));
        Assert.That(projection.Publish(Snapshot(second, first))[Sha].Runs.Single().Id, Is.EqualTo("2"), "Reordering equal cached observations is not a new publication.");
        GitHubActionsWorkflowRun changedFirst = Run(1, 10, 1, 4, 9, "cancelled");
        Assert.That(projection.Publish(Snapshot(changedFirst, second))[Sha].Runs.Single().Id, Is.EqualTo("1"));
        Assert.That(projection.Publish(Snapshot(changedFirst, second))[Sha].Status, Is.EqualTo(BuildServerBuildStatus.Stopped));
        GitHubActionsWorkflowRun changedSecond = Run(2, 20, 1, 4, 10);
        Assert.That(projection.Publish(Snapshot(changedFirst, changedSecond))[Sha].Runs.Single().Id, Is.EqualTo("2"));
        Assert.That(projection.Publish(Snapshot(changedFirst, changedSecond))[Sha].Status, Is.EqualTo(BuildServerBuildStatus.Success));
    }

    [Test]
    public void ALatestBuildWithoutAUrlDoesNotFallBackToAnOlderLinkAndFilteredOutCommitsAreRetired()
    {
        BuildServerRevisionProjection projection = new();
        BuildServerCommitStatus shown = projection.Publish(Snapshot(Run(1, 10, 1, 1, 9), Run(2, 20, 4, 4, 6, "failure", url: false)))[Sha];
        Assert.That(shown.Runs.Single().Id, Is.EqualTo("2")); Assert.That(shown.Runs.Single().PageUri, Is.Null);
        BuildServerCommitStatus remaining = projection.Publish(Snapshot(Run(1, 10, 1, 1, 9)))[Sha];
        Assert.That(remaining.Runs.Single().Id, Is.EqualTo("1"), "An omitted winner must retire even when a sibling still supplies the same SHA.");
        Assert.That(remaining.Status, Is.EqualTo(BuildServerBuildStatus.Success)); Assert.That(remaining.Runs.Single().PageUri, Is.EqualTo(Page(1)));
        Assert.That(projection.Publish(Snapshot()), Is.Empty);
        projection.Clear(); Assert.That(projection.Publish(Snapshot(Run(1, 10, 1, 1, 9)))[Sha].Runs.Single().Id, Is.EqualTo("1"));
    }

    [Test]
    public void GitLabKeepsItsRawCreatedDatesButItsWindowsObservationsUseTheDefaultStartDate()
    {
        GitLabPipeline first = new() { Id = 1, ProjectId = 4, Sha = Sha, Status = "success", CreatedAt = At(6), UpdatedAt = At(9) };
        GitLabPipeline second = new() { Id = 2, ProjectId = 4, Sha = Sha, Status = "failed", CreatedAt = At(1), UpdatedAt = At(8) };
        BuildServerSnapshot source = BuildServerSnapshot.FromGitLab(new([first, second], At(100)), pipeline => Page(pipeline.Id));
        Assert.That(source.Runs.Select(run => run.StartedAt), Is.EqualTo(new DateTimeOffset?[] { At(6), At(1) }));
        Assert.That(source.RevisionObservations.Select(run => run.StartedAt), Is.All.Null);
        BuildServerCommitStatus shown = new BuildServerRevisionProjection().Publish(source)[Sha];
        Assert.That(shown.Runs.Single().Id, Is.EqualTo("2"), "Both default start dates tie; the last admitted source observation replaces.");
        Assert.That(shown.Status, Is.EqualTo(BuildServerBuildStatus.Failure)); Assert.That(source.Commits[Sha].Runs.Single().Id, Is.EqualTo("1"));
    }

    [Test]
    public void JenkinsKeepsItsProviderAdmissionInsteadOfLettingRawRunningHistoryReplaceACompletedBuild()
    {
        JenkinsBuild completed = new() { ProjectId = "one", Number = 1, CommitHashes = [Sha], StartedAt = At(1), Status = JenkinsBuildStatus.Success, PageUri = Page(1) };
        JenkinsBuild running = new() { ProjectId = "two", Number = 2, CommitHashes = [Sha], StartedAt = At(4), Status = JenkinsBuildStatus.InProgress, IsRunning = true, PageUri = Page(2) };
        BuildServerSnapshot source = BuildServerSnapshot.FromJenkins(new([completed, running], At(100)), build => build.PageUri);
        Assert.That(source.Runs.Count, Is.EqualTo(2)); Assert.That(source.RevisionObservations.Single().Id, Is.EqualTo(completed.BuildId));
        BuildServerCommitStatus shown = new BuildServerRevisionProjection().Publish(source)[Sha];
        Assert.That(shown.Runs.Single().Id, Is.EqualTo(completed.BuildId)); Assert.That(shown.Status, Is.EqualTo(BuildServerBuildStatus.Success));
    }

    [Test]
    public void AzurePrHeadMappingsKeepTheLatestBuildStatusPageTooltipAndPullRequestTogether()
    {
        AzureDevOpsBuild older = new() { BuildId = 1, DefinitionId = 10, SourceVersion = Sha, CommitHash = Sha, RawStatus = "completed", Status = AzureDevOpsBuildStatus.Failure,
            StartedAt = At(1), FinishedAt = At(9), BuildNumber = "old", DefinitionName = "old pipeline", PageUri = Page(1), PullRequestUri = new("https://ci.example/pr/1") };
        AzureDevOpsBuild latest = new() { BuildId = 2, DefinitionId = 20, SourceVersion = new string('b', 40), CommitHash = Sha, RawStatus = "completed", Status = AzureDevOpsBuildStatus.Success,
            StartedAt = At(4), FinishedAt = At(6), BuildNumber = "new", DefinitionName = "new pipeline", PageUri = Page(2), PullRequestUri = new("https://ci.example/pr/2") };
        BuildServerSnapshot source = BuildServerSnapshot.FromAzureDevOps(new([older, latest], At(100)), build => build.PageUri);
        Assert.That(source.Runs.Count, Is.EqualTo(2)); BuildServerCommitStatus shown = new BuildServerRevisionProjection().Publish(source)[Sha];
        Assert.That(shown.Status, Is.EqualTo(BuildServerBuildStatus.Success)); BuildServerRun run = shown.Runs.Single();
        Assert.That(run.Id, Is.EqualTo("2")); Assert.That(run.PageUri, Is.EqualTo(Page(2))); Assert.That(run.PullRequestUri!.AbsoluteUri, Is.EqualTo("https://ci.example/pr/2"));
        Assert.That(shown.Text, Is.EqualTo(latest.Description)); Assert.That(shown.Tooltip, Is.EqualTo(latest.Tooltip)); Assert.That(run.ShowInBuildReportTab, Is.False);
    }

    [AvaloniaTest]
    [TestCase("English", false)]
    [TestCase("French", true)]
    public async Task GitHubReportHasTheWindowsLinkSharedByStatusCellMenuAndKeyboardAndPollsKeepItsControlsAndFocus(string language, bool dark)
    {
        await using Runtime f = await Runtime.Open(language, dark);
        MainWindow window = f.Window; ListBox history = Field<ListBox>(window, "RevisionHistory");
        object? source = history.ItemsSource, selected = history.SelectedItem; object[] commits = history.Items.Cast<object>().ToArray();
        string before = await Witness(f); Dictionary<string, byte[]> profiles = Profiles();
        Control row = history.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionHistoryRow" && RevisionHistoryRow.CommitHash(control) == f.Sha);
        Control cell = Field<Control>(row, "RevisionBuildStatusCell");
        Assert.That(window.BuildServerStatusFor(f.Sha)!.Status, Is.EqualTo(BuildServerBuildStatus.Failure));
        Assert.That(Field<TextBlock>(row, "RevisionBuildStatus").Text, Is.EqualTo("Workflow 20 #2 (failure)"));
        Assert.That(ToolTip.GetTip(cell), Is.EqualTo("Workflow 20 #2 (failure)"));
        history.Focus(); Press(window, PhysicalKey.Digit7, RawInputModifiers.Control);
        TabControl details = Field<TabControl>(window, "DetailsTabs"); TabItem report = (TabItem)details.SelectedItem!;
        HyperlinkButton link = Field<HyperlinkButton>(window, "BuildReportOpenReport"); object? panel = report.Content;
        Assert.That(report.Name, Is.EqualTo("GitHubActionsBuildReport")); Assert.That(link.IsKeyboardFocusWithin && link.IsEffectivelyVisible, Is.True);
        Assert.That(link.Content, Is.EqualTo(language == "French" ? "Ouvrir un rapport" : "Open report"));
        Assert.That(link.FontSize, Is.EqualTo(16d * 96d / 72d)); Assert.That(link.HorizontalAlignment, Is.EqualTo(HorizontalAlignment.Center));
        Assert.That(link.VerticalAlignment, Is.EqualTo(VerticalAlignment.Center)); Assert.That(Field<ListBox>(window, "GitHubActionsRuns").IsEffectivelyVisible, Is.False);
        Assert.That(((BuildServerRun)link.Tag!).PageUri, Is.EqualTo(Page(2))); Assert.That(f.Opened, Is.Empty);
        PushProcessTestDriver.Pointer(link); Press(window, PhysicalKey.Enter); Press(window, PhysicalKey.Space);
        Assert.That(f.Opened.Select(info => info.ArgumentList.Single()), Is.EqualTo(Enumerable.Repeat(Page(2).AbsoluteUri, 3)));
        PushProcessTestDriver.Pointer(cell); Assert.That(f.Opened.Last().ArgumentList.Single(), Is.EqualTo(Page(2).AbsoluteUri));
        MenuItem menu = window.BuildGitHubActionsShowBuildPageMenu(); Assert.That(menu.IsEnabled && menu.IsVisible, Is.True);
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Assert.That(f.Opened.Count, Is.EqualTo(5));
        Assert.That(f.Opened.Last().ArgumentList.Single(), Is.EqualTo(Page(2).AbsoluteUri));
        link.Focus(); await window.RefreshGitHubActionsNowAsync(); Pump(window);
        Assert.That(Field<HyperlinkButton>(window, "BuildReportOpenReport"), Is.SameAs(link)); Assert.That(report.Content, Is.SameAs(panel));
        Assert.That(link.IsKeyboardFocusWithin, Is.True); AssertHistory(history, source, selected, commits);
        f.Runs = [Run(1, 10, 1, 1, 9, sha: f.Sha), Run(2, 20, 4, 4, 7, "failure", f.Sha, url: false)];
        await window.RefreshGitHubActionsNowAsync(); Pump(window);
        Assert.That(details.Items.OfType<TabItem>().Any(tab => tab.Name == "GitHubActionsBuildReport"), Is.False);
        Assert.That(window.BuildServerStatusFor(f.Sha)!.Runs.Single().Id, Is.EqualTo("2")); Assert.That(window.BuildServerStatusFor(f.Sha)!.Runs.Single().PageUri, Is.Null);
        Assert.That(menu.IsEnabled || menu.IsVisible, Is.False); int launched = f.Opened.Count;
        link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await window.ShowBuildPageAsync();
        Assert.That(f.Opened.Count, Is.EqualTo(launched)); AssertHistory(history, source, selected, commits);
        SameProfiles(profiles); Assert.That(await Witness(f), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task FocusBuildReportUsesTheConfiguredGestureAndNoReportDoesNotCreateATabOrStealFocus()
    {
        await using Runtime f = await Runtime.Open("English", false); MainWindow window = f.Window;
        HotkeyBindings bindings = DesktopHotkeys.Current.Clone();
        bindings.Set("Browse", HotkeyCatalog.Find("Browse", "FocusBuildServerStatus")!.Code, new(Key.F8)); new HotkeyPreferencesStore().Save(bindings); Pump(window);
        Dictionary<string, byte[]> profiles = Profiles(); ListBox history = Field<ListBox>(window, "RevisionHistory"); history.Focus();
        Press(window, PhysicalKey.Digit7, RawInputModifiers.Control); Assert.That(history.IsKeyboardFocusWithin, Is.True);
        Press(window, PhysicalKey.F8); Assert.That(Field<HyperlinkButton>(window, "BuildReportOpenReport").IsKeyboardFocusWithin, Is.True);
        GitCommit other = history.Items.OfType<GitCommit>().First(commit => commit.Hash != f.Sha); history.SelectedItem = other; await Until(() => !window.IsBusy); Pump(window);
        history.Focus(); Press(window, PhysicalKey.F8);
        Assert.That(Field<TabControl>(window, "DetailsTabs").Items.OfType<TabItem>().Any(tab => tab.Name == "GitHubActionsBuildReport"), Is.False);
        Assert.That(history.IsKeyboardFocusWithin, Is.True);
        object? source = history.ItemsSource; object[] commits = history.Items.Cast<object>().ToArray();
        Press(window, PhysicalKey.ArrowUp); await Until(() => !window.IsBusy); Pump(window);
        Assert.That(history.SelectedItem, Is.SameAs(commits[0])); Assert.That(history.IsKeyboardFocusWithin, Is.True);
        Press(window, PhysicalKey.ArrowDown); await Until(() => !window.IsBusy); Pump(window);
        AssertHistory(history, source, other, commits); Assert.That(history.IsKeyboardFocusWithin, Is.True); SameProfiles(profiles);
    }

    [AvaloniaTest]
    public async Task ReportPreferenceHidesOnlyTheOptionalPageAndReusesItsLinkWhenEnabledAgain()
    {
        await using Runtime f = await Runtime.Open("English", false); MainWindow window = f.Window;
        TabControl details = Field<TabControl>(window, "DetailsTabs");
        TabItem report = details.Items.OfType<TabItem>().Single(tab => tab.Name == "GitHubActionsBuildReport");
        details.SelectedItem = report; Pump(window);
        HyperlinkButton link = Field<HyperlinkButton>(window, "BuildReportOpenReport"); object? panel = report.Content;
        GitHubActionsSettingsSession settings = new(f.Profile.Repository); await settings.InitializeAsync(default);
        settings.Set(GitDistributedSettingsScope.Local, GitHubActionsSettings.ShowPageKey, "false"); await settings.SaveFilesAsync(default);
        await Until(() => !Field<TabControl>(window, "DetailsTabs").Items.OfType<TabItem>().Any(tab => tab.Name == "GitHubActionsBuildReport"));
        Assert.That(window.GitHubActionsEnabled, Is.True); Assert.That(window.BuildServerStatusFor(f.Sha)!.Runs.Single().Id, Is.EqualTo("2"));
        MenuItem browser = window.BuildGitHubActionsShowBuildPageMenu(); Assert.That(browser.IsEnabled && browser.IsVisible, Is.True);
        ListBox history = Field<ListBox>(window, "RevisionHistory"); history.Focus(); Press(window, PhysicalKey.Digit7, RawInputModifiers.Control);
        Assert.That(history.IsKeyboardFocusWithin, Is.True); await window.ShowBuildPageAsync(); Assert.That(f.Opened.Single().ArgumentList.Single(), Is.EqualTo(Page(2).AbsoluteUri));
        settings.Set(GitDistributedSettingsScope.Local, GitHubActionsSettings.ShowPageKey, "true"); await settings.SaveFilesAsync(default);
        await Until(() => Field<TabControl>(window, "DetailsTabs").Items.OfType<TabItem>().Any(tab => tab.Name == "GitHubActionsBuildReport"));
        Assert.That(Field<HyperlinkButton>((Control)report.Content!, "BuildReportOpenReport"), Is.SameAs(link));
        Assert.That(details.Items.OfType<TabItem>().Single(tab => tab.Name == "GitHubActionsBuildReport"), Is.SameAs(report));
        Assert.That(report.Content, Is.SameAs(panel));
        Assert.That(history.IsKeyboardFocusWithin, Is.True);
    }

    private sealed class Runtime : IAsyncDisposable
    {
        internal DetailedSettingsTestScope Profile { get; }
        internal MainWindow Window { get; }
        internal string Sha { get; }
        internal List<GitHubActionsWorkflowRun> Runs { get; set; }
        internal List<ProcessStartInfo> Opened { get; } = [];
        private Runtime(DetailedSettingsTestScope profile, string sha, string language, bool dark)
        {
            Profile = profile; Sha = sha; Runs = [Run(1, 10, 1, 1, 9, sha: sha), Run(2, 20, 4, 4, 6, "failure", sha)];
            profile.Desktop.Save(new() { Language = language, Theme = dark ? "Dark" : "Light", StartWithRecentWorkingDir = false, CheckForUpdates = false });
            new HistoryViewPreferencesStore().Save(new() { ShowBuildStatusText = true, ShowArtificialCommits = false });
            CultureInfo previousCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language == "French" ? "fr" : "en");
                Window = new() { Width = 1280, Height = 860, GitHubActionsPollInterval = Timeout.InfiniteTimeSpan, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            }
            finally { CultureInfo.CurrentUICulture = previousCulture; }
            Window.GitHubActionsClientFactory = settings => new(settings, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { workflow_runs = Runs })) })));
            Window.ExternalLauncher = info => { Opened.Add(info); return null; };
        }
        internal static async Task<Runtime> Open(string language, bool dark)
        {
            DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync(); Runtime? runtime = null;
            try
            {
                await profile.Git.ChangeAsync("second commit\n", "Second commit"); string sha = (await profile.Repository!.GetLogAsync(1, all: false)).Single().Hash;
                profile.Write(GitDistributedSettingsScope.Global, (GitHubActionsSettings.EnableKey, "true"), (GitHubActionsSettings.ShowPageKey, "true"),
                    (GitHubActionsSettings.ProviderKey, "GitHub Actions"), (GitHubActionsSettings.OwnerKey, "octo"), (GitHubActionsSettings.RepositoryKey, "repo"));
                runtime = new(profile, sha, language, dark); runtime.Window.Show(); await runtime.Window.OpenRepositoryAsync(profile.Repository.Path);
                await Until(() => runtime.Window.BuildServerStatusFor(sha)?.Runs.SingleOrDefault()?.Id == "2" && !runtime.Window.IsBusy); Pump(runtime.Window); return runtime;
            }
            catch { if (runtime is not null) { await runtime.DisposeAsync(); } else { profile.Dispose(); } throw; }
        }
        public async ValueTask DisposeAsync() { await Window.CloseGitHubActionsAsync(); Window.Close(); await Until(() => !Window.IsVisible); Profile.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private static async Task<string> Witness(Runtime f)
    {
        GitResult refs = await f.Profile.Repository!.RunAsync(["show-ref"]);
        return JsonSerializer.Serialize(new { refs = refs.StandardOutput, index = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(f.Profile.Repository.Path, ".git/index")))),
            config = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(f.Profile.Repository.Path, ".git/config")))) });
    }
    private static Dictionary<string, byte[]> Profiles()
    { string directory = Path.GetDirectoryName(new PreferencesStore().FilePath)!; return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes); }
    private static void SameProfiles(Dictionary<string, byte[]> expected)
    { Dictionary<string, byte[]> actual = Profiles(); Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys)); foreach ((string path, byte[] bytes) in expected) { Assert.That(actual[path], Is.EqualTo(bytes), path); } }
    private static void AssertHistory(ListBox history, object? source, object? selected, object[] commits)
    { Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected)); for (int index = 0; index < commits.Length; index++) { Assert.That(history.Items[index], Is.SameAs(commits[index])); } }
    private static T Field<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { root.KeyPressQwerty(key, modifiers); root.KeyReleaseQwerty(key, modifiers); Pump(root); }
    private static void Pump(TopLevel root) => DetailedSettingsUiHost.Pump(root);
    private static Task Until(Func<bool> predicate) => DetailedSettingsUiHost.Until(predicate);
}

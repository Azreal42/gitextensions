using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitCommands;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GitHubActionsDesktopTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task ProviderPageSharesOneScopePrefillsPushRemoteMasksTokenAndCancelDoesNotWrite(string culture, bool dark)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        await f.Git.GitAsync("config", "remote.origin.url", "https://github.com/fetch-owner/fetch-repo.git");
        await f.Git.GitAsync("config", "remote.origin.pushurl", "git@github.com:push-owner/push-repo.git");
        f.Write(GitDistributedSettingsScope.Global, (GitHubActionsSettings.ProviderKey, "GitHub Actions"),
            (GitHubActionsSettings.OwnerKey, " "), (GitHubActionsSettings.TokenKey, "masked-test-token"), ("future.provider.key", "retained"));
        GitHubActionsSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        Dictionary<string, byte[]> before = f.Material(); int launches = 0;
        CultureInfo previousCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        BuildServerIntegrationSettingsPage page = new(session, TranslationCatalog.LoadDefault(), () => launches++);
        Grid content = new() { ColumnDefinitions = new("190,*"), Margin = new(8) }; Grid.SetColumn(page, 1); content.Children.Add(page);
        Window host = new() { Width = 958, Height = 720, Content = content, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; host.Show();
        try
        {
            page.SetScope(GitDistributedSettingsScope.Global); Pump(host);
            Assert.That(Field<TextBox>(page, "GitHubActionsOwner").Text, Is.EqualTo("push-owner"));
            Assert.That(Field<TextBox>(page, "GitHubActionsRepository").Text, Is.EqualTo("push-repo"));
            Assert.That(Field<TextBox>(page, "GitHubActionsApiToken").PasswordChar, Is.Not.EqualTo('\0'));
            Assert.That(page.GitHubPage.IsVisible, Is.True); Assert.That(session.HasChanges, Is.False, "Prefill is a draft display, not an implicit save.");
            Assert.That(page.GetVisualDescendants().OfType<RadioButton>().Count(c => c.IsEffectivelyVisible), Is.EqualTo(4), "The embedded provider must share the parent's scope header.");
            Capture(host, "github-actions-settings-" + culture + (dark ? "-dark.png" : "-light.png"));
            PushProcessTestDriver.Pointer(Field<Button>(page, "CreatePersonalAccessToken")); Assert.That(launches, Is.EqualTo(1));
            Field<ComboBox>(page, "BuildServerProvider").SelectedItem = ""; Assert.That(page.GitHubPage.IsVisible, Is.False);
            Field<ComboBox>(page, "BuildServerProvider").SelectedItem = "GitHub Actions"; Assert.That(page.GitHubPage.IsVisible, Is.True);
            page.PrepareSave(); host.Close(); f.AssertMaterial(before);
        }
        finally { host.Close(); CultureInfo.CurrentUICulture = previousCulture; }
    }

    [Test]
    public async Task SharedWorkspaceAcknowledgesPeerPageWritesAndCiPublishesOnlyActuallySavedPaths()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        DistributedSettingsWorkspace workspace = new(f.Repository); GitHubActionsSettingsSession ci = new(f.Repository, workspace);
        await ci.InitializeAsync(default); GitDistributedSettingsSnapshot before = workspace.Snapshot!;
        GitXmlSettingsSnapshot peer = await GitXmlSettingsStore.SaveChangesAsync(before.GetSnapshot(GitDistributedSettingsScope.Local), new Dictionary<string, string?> { [DetailedRepositorySettings.AddMergeLog] = "true" });
        workspace.AcceptSaved(before.WithSaved(GitDistributedSettingsScope.Local, peer));
        List<string> paths = []; void Published(string path) => paths.Add(path); GitHubActionsSettingsPublication.Saved += Published;
        try
        {
            ci.Set(GitDistributedSettingsScope.Local, GitHubActionsSettings.ShowPageKey, "true"); await ci.SaveFilesAsync(default);
            GitXmlSettingsSnapshot saved = await f.ReadAsync(GitDistributedSettingsScope.Local);
            Assert.That(saved.Values[DetailedRepositorySettings.AddMergeLog], Is.EqualTo("true")); Assert.That(saved.Values["future.λ"], Is.EqualTo("keep <literal> & \nbody"));
            Assert.That(paths, Is.EqualTo(new[] { saved.Path })); Assert.That(ci.HasChanges, Is.False);
            await ci.SaveFilesAsync(default); Assert.That(paths.Count, Is.EqualTo(1), "A no-op must not publish or rebase XML.");
        }
        finally { GitHubActionsSettingsPublication.Saved -= Published; }
    }

    [Test]
    public async Task ExternalXmlChangeRefusesCiOverwriteAndAnUntouchedUnreadablePageDoesNotBlockOtherSettings()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); GitHubActionsSettingsSession ci = new(f.Repository); await ci.InitializeAsync(default);
        ci.Set(GitDistributedSettingsScope.Local, GitHubActionsSettings.ShowPageKey, "true");
        GitXmlSettingsSnapshot peer = await f.ReadAsync(GitDistributedSettingsScope.Local);
        await GitXmlSettingsStore.SaveChangesAsync(peer, new Dictionary<string, string?> { ["external.peer"] = "kept" });
        byte[] bytes = File.ReadAllBytes(peer.Path); Assert.CatchAsync<IOException>(async () => await ci.SaveFilesAsync(default));
        Assert.That(File.ReadAllBytes(peer.Path), Is.EqualTo(bytes)); Assert.That(ci.HasChanges, Is.True);
        File.WriteAllText(f.Path(GitDistributedSettingsScope.Global), "not XML");
        GitHubActionsSettingsSession untouched = new(f.Repository); await untouched.InitializeAsync(default);
        Assert.That(untouched.Loaded, Is.False); Assert.That(untouched.HasChanges, Is.False); await untouched.SaveFilesAsync(default);
    }

    [AvaloniaTest]
    public async Task ActualBrowserDefaultsHaveNoHttpAndApplyUpdatesStatusInPlacePreservingFocusAndExplicitLaunch()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); int requests = 0, factories = 0; string conclusion = "success";
        string sha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        f.Write(GitDistributedSettingsScope.Global, (GitHubActionsSettings.EnableKey, "false"), (GitHubActionsSettings.ProviderKey, "GitHub Actions"),
            (GitHubActionsSettings.OwnerKey, "octo"), (GitHubActionsSettings.RepositoryKey, "repo"));
        MainWindow window = Browser(f); List<ProcessStartInfo> opened = [];
        window.ExternalLauncher = info => { opened.Add(info); return null; };
        window.GitHubActionsClientFactory = settings => { factories++; return new(settings, new Handler((_, _) => { requests++; return Task.FromResult(Response(sha, conclusion)); })); };
        window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await window.GitHubActionsCompletion;
            await Until(() => !window.IsBusy); Pump(window); ListBox history = History(window);
            Assert.That(factories, Is.Zero); Assert.That(requests, Is.Zero, "CI disabled must never discover credentials or send HTTP.");
            object? source = history.ItemsSource, selected = history.SelectedItem;
            Control row = Rows(window).Single(r => RevisionHistoryRow.CommitHash(r) == ((GitCommit)selected!).Hash);
            history.Focus();
            GitHubActionsSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
            session.Set(GitDistributedSettingsScope.Local, GitHubActionsSettings.EnableKey, "true");
            session.Set(GitDistributedSettingsScope.Local, GitHubActionsSettings.ShowPageKey, "true");
            await session.SaveFilesAsync(default);
            await UntilCi(() => window.GitHubActionsStatusFor(sha)?.Status == GitHubActionsBuildStatus.Success, window,
                () => $"factories={factories}; requests={requests}; status={window.GitHubActionsStatusFor(sha)?.Status}"); Pump(window);
            Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected));
            Assert.That(Rows(window).Single(r => RevisionHistoryRow.CommitHash(r) == sha), Is.SameAs(row)); Assert.That(history.IsKeyboardFocusWithin, Is.True);
            Assert.That(row.GetVisualDescendants().OfType<Image>().Single(c => c.Name == "RevisionBuildStatusIcon").Tag, Is.EqualTo("StatusBadgeSuccess.png"));
            TabControl details = Field<TabControl>(window, "DetailsTabs");
            TabItem report = details.Items.OfType<TabItem>().Single(tab => tab.Name == "GitHubActionsBuildReport");
            Assert.That(details.IsEffectivelyVisible && report.IsVisible, Is.True);
            foreach (CommitInfoPosition position in new[] { CommitInfoPosition.RightwardFromList, CommitInfoPosition.BelowList })
            {
                MenuFlyout menu = (MenuFlyout)Field<Button>(window, "CommitInfoPositionMenu").Flyout!;
                menu.ItemsSource!.Cast<MenuItem>().Single(item => Equals(item.Tag, position)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(window);
                Assert.That(details.Items.OfType<TabItem>().Single(tab => tab.Name == "GitHubActionsBuildReport"), Is.SameAs(report));
                Assert.That(details.IsEffectivelyVisible && report.IsVisible, Is.True);
                Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected));
                Assert.That(Rows(window).Single(r => RevisionHistoryRow.CommitHash(r) == sha), Is.SameAs(row)); Assert.That(history.IsKeyboardFocusWithin, Is.True);
            }
            Capture(window, "github-actions-browser-success-light.png");
            Assert.That(opened, Is.Empty, "Receiving a run never opens a browser.");
            PushProcessTestDriver.Pointer(row.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionBuildStatusCell"));
            await Until(() => opened.Count == 1); Assert.That(opened.Single().FileName, Is.EqualTo("xdg-open"));
            Assert.That(opened.Single().ArgumentList, Is.EqualTo(new[] { "https://github.com/octo/repo/actions/runs/17" }));
            conclusion = "failure"; await window.RefreshGitHubActionsNowAsync(); Pump(window);
            Assert.That(window.GitHubActionsStatusFor(sha)?.Status, Is.EqualTo(GitHubActionsBuildStatus.Failure));
            Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected)); Assert.That(history.IsKeyboardFocusWithin, Is.True);
            int beforeDisable = requests; session.Set(GitDistributedSettingsScope.Local, GitHubActionsSettings.EnableKey, "false"); await session.SaveFilesAsync(default);
            await UntilCi(() => !window.GitHubActionsEnabled, window, () => $"factories={factories}; requests={requests}"); Pump(window);
            Assert.That(window.GitHubActionsStatusFor(sha), Is.Null); Assert.That(requests, Is.EqualTo(beforeDisable));
            Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected));
            Assert.That(window.BuildGitHubActionsShowBuildPageMenu().IsEnabled, Is.False);
            Assert.That(details.Items.OfType<TabItem>().Any(tab => tab.Name == "GitHubActionsBuildReport"), Is.False);
        }
        finally { await CloseAsync(window); }
    }

    [AvaloniaTest]
    public async Task ActualBrowserShowsAuthenticationFailureWithoutTokenAndUnrelatedXmlDoesNotRestartCi()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f); int requests = 0;
        MainWindow window = Browser(f); window.GitHubActionsClientFactory = settings => new(settings, new Handler((_, _) =>
        { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("masked-test-token must not be displayed") }); }));
        window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository!.Path); await Until(() => window.GitHubActionsError is not null); Pump(window);
            Assert.That(window.GitHubActionsError, Does.Contain("authentication failed").And.Not.Contain("masked-test-token"));
            Assert.That(window.GetVisualDescendants().OfType<TextBlock>().Single(c => c.Name == "RepositoryStatus").Text, Does.Contain("GitHub Actions"));
            int calls = requests; GitHubActionsSettingsPublication.Publish(f.Git.Output("unrelated.settings")); Pump(window);
            Assert.That(requests, Is.EqualTo(calls));
        }
        finally { await CloseAsync(window); }
    }

    [AvaloniaTest]
    public async Task ActualBrowserRepositorySwitchAndCloseCancelAndDrainBlockedHttpAndOwnedRestarts()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        MainWindow window = Browser(f); List<BlockingHandler> handlers = [];
        window.GitHubActionsClientFactory = settings => { BlockingHandler handler = new(); handlers.Add(handler); return new(settings, handler); };
        window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository!.Path);
            await Until(() => handlers.Count == 1 && handlers[0].Started.Task.IsCompleted);
            Assert.That(handlers[0].Finished.Task.IsCompleted, Is.False, "OpenRepository must not await the first HTTP response.");
            string second = f.Git.Output("another-repository"); await GitRepository.InitAsync(second, "main");
            await window.OpenRepositoryAsync(second); await Until(() => handlers.Count == 2 && handlers[1].Started.Task.IsCompleted);
            Assert.That(handlers[0].Finished.Task.IsCompleted, Is.True); Assert.That(handlers[0].Token.IsCancellationRequested, Is.True);
            await window.CloseGitHubActionsAsync();
            Assert.That(handlers[1].Token.IsCancellationRequested, Is.True); Assert.That(handlers[1].Finished.Task.IsCompleted, Is.True);
            Assert.That(window.GitHubActionsCompletion.IsCompletedSuccessfully, Is.True, "Close owns the scheduled restart, first read, polling and manual reads.");
            int count = handlers.Count; GitHubActionsSettingsPublication.Publish(DetailedRepositorySettings.GlobalPath); Pump(window);
            Assert.That(handlers.Count, Is.EqualTo(count), "A closing/detached browser no longer subscribes to settings.");
        }
        finally { await CloseAsync(window); }
    }

    private static void Enable(DetailedSettingsTestScope f) => f.Write(GitDistributedSettingsScope.Global,
        (GitHubActionsSettings.EnableKey, "true"), (GitHubActionsSettings.ProviderKey, "GitHub Actions"), (GitHubActionsSettings.OwnerKey, "octo"),
        (GitHubActionsSettings.RepositoryKey, "repo"), (GitHubActionsSettings.TokenKey, "masked-test-token"));
    private static MainWindow Browser(DetailedSettingsTestScope f)
    { f.Desktop.Save(new() { Language = "English", StartWithRecentWorkingDir = false }); return new() { Width = 1200, Height = 780, GitHubActionsPollInterval = Timeout.InfiniteTimeSpan }; }
    private static ListBox History(MainWindow window) => window.GetVisualDescendants().OfType<ListBox>().Single(c => c.ItemsSource is IEnumerable<GitCommit>);
    private static IEnumerable<Control> Rows(MainWindow window) => History(window).GetVisualDescendants().OfType<Control>().Where(c => c.Name == "RevisionHistoryRow");
    private static T Field<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Pump(TopLevel window) => DetailedSettingsUiHost.Pump(window);
    private static Task Until(Func<bool> predicate) => DetailedSettingsUiHost.Until(predicate);
    private static async Task UntilCi(Func<bool> predicate, MainWindow window, Func<string> diagnostics)
    {
        try { await Until(predicate); }
        catch (OperationCanceledException)
        { Assert.Fail($"CI publication did not reach its expected state: enabled={window.GitHubActionsEnabled}; error={window.GitHubActionsError ?? "<none>"}; {diagnostics()}"); }
    }
    private static void Capture(Window window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private static async Task CloseAsync(MainWindow window) { await window.CloseGitHubActionsAsync(); window.Close(); await Until(() => !window.IsVisible); }
    private static HttpResponseMessage Response(string sha, string conclusion) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
    { workflow_runs = new[] { new { id = 17, workflow_id = 5, head_sha = sha, name = "Build", status = "completed", conclusion, html_url = "https://github.com/octo/repo/actions/runs/17" } } })) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class BlockingHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Token = token; Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException("Unreachable"); }
            finally { Finished.TrySetResult(); }
        }
    }
}

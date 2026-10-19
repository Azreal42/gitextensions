using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using GitUIPluginInterfaces.BuildServerIntegration;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class TeamCityIntegrationTests
{
    [Test]
    public async Task ScopedDraftRetainsUnknownKeysAndCredentialsCancelNeverWritesXmlOrUserFile()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        GitHubActionsSettingsSession session = new(f.Repository) { CredentialsStore = new(f.Git.Output("private-ci")) }; await session.InitializeAsync(default);
        Dictionary<string, byte[]> material = f.Material();
        session.StageCredentials(new Uri("https://ci.example/one/"), new() { BuildServerCredentialsType = BuildServerCredentialsType.BearerToken, BearerToken = "secret" });
        session.Set(GitDistributedSettingsScope.Local, TeamCitySettings.ProjectNameKey, "Changed");
        Assert.That(File.Exists(session.CredentialsStore.FilePath), Is.False); f.AssertMaterial(material);
        GitHubActionsSettingsSession accepted = new(f.Repository) { CredentialsStore = session.CredentialsStore }; await accepted.InitializeAsync(default);
        accepted.Set(GitDistributedSettingsScope.Local, TeamCitySettings.BuildIdFilterKey, "Build.*");
        accepted.StageCredentials(new Uri("https://ci.example/one/"), new() { BuildServerCredentialsType = BuildServerCredentialsType.BearerToken, BearerToken = "explicit" });
        await accepted.SaveFilesAsync(default);
        Assert.That(accepted.HasChanges, Is.False);
        Assert.That(accepted.CredentialsStore.Load(TeamCitySettings.ProviderName, new Uri("https://ci.example/one/")).Credentials.BearerToken, Is.EqualTo("explicit"));
        GitXmlSettingsSnapshot xml = await f.ReadAsync(GitDistributedSettingsScope.Local);
        Assert.That(xml.Values["future.λ"], Is.EqualTo("keep <literal> & \nbody"));
        Assert.That(File.ReadAllText(xml.Path), Does.Not.Contain("explicit").And.Not.Contain("secret"));
    }

    [Test]
    public async Task ReportGuestCheckboxDoesNotReplaceExplicitBearerAuthenticationOrInactiveFields()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        GitHubActionsSettingsSession session = new(f.Repository) { CredentialsStore = new(f.Git.Output("private-ci")) }; await session.InitializeAsync(default);
        session.StageCredentials(new Uri("https://ci.example/one/"), new() { BuildServerCredentialsType = BuildServerCredentialsType.BearerToken,
            BearerToken = "explicit-token", Username = "inactive-user", Password = "inactive-password" });
        TeamCitySettings settings = session.TeamCity(); Assert.That(settings.LogAsGuest, Is.True);
        BuildServerCredentials credentials = session.CredentialsFor(settings)!;
        Assert.That(credentials.BuildServerCredentialsType, Is.EqualTo(BuildServerCredentialsType.BearerToken));
        Assert.That(credentials.BearerToken, Is.EqualTo("explicit-token"));
        Assert.That(credentials.Username, Is.EqualTo("inactive-user")); Assert.That(credentials.Password, Is.EqualTo("inactive-password"));
        credentials.BearerToken = "caller-edit";
        Assert.That(session.CredentialDraft(settings.ServerUri).BearerToken, Is.EqualTo("explicit-token"));
    }

    [Test]
    public async Task InvalidRegexBlocksSaveWithoutChangingEitherScopeOrCredentialFile()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        GitHubActionsSettingsSession session = new(f.Repository) { CredentialsStore = new(f.Git.Output("private-ci")) }; await session.InitializeAsync(default);
        Dictionary<string, byte[]> material = f.Material();
        session.Set(GitDistributedSettingsScope.Local, TeamCitySettings.BuildIdFilterKey, "[");
        session.StageCredentials(new Uri("https://ci.example/one/"), new() { BearerToken = "not-saved" });
        Assert.ThrowsAsync<ArgumentException>(async () => await session.SaveFilesAsync(default));
        f.AssertMaterial(material); Assert.That(File.Exists(session.CredentialsStore.FilePath), Is.False);
    }

    [AvaloniaTest]
    public async Task ProviderPageUsesOneScopeHeaderAndPasteIsExplicitBoundedAndOnlyUpdatesDraft()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        GitHubActionsSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        session.StageCredentials(new Uri("https://ci.example/one/"), new() { BuildServerCredentialsType = BuildServerCredentialsType.Guest });
        Dictionary<string, byte[]> material = f.Material();
        BuildServerIntegrationSettingsPage page = new(session, TranslationCatalog.LoadDefault());
        int calls = 0; FakeClient? client = null;
        page.TeamCityPage.ClientFactory = (settings, credentials) => { calls++; Assert.That(settings.ServerUri.AbsoluteUri, Is.EqualTo("https://ci.example/one/")); return client = new FakeClient(""); };
        page.TeamCityPage.ReadClipboard = _ => Task.FromResult<string?>("https://ci.example/one/viewLog.html?buildId=3&buildTypeId=Build");
        Window host = new() { Width = 958, Height = 646, Content = page }; host.Show();
        try
        {
            page.SetScope(GitDistributedSettingsScope.Local);
            Field<ComboBox>(page, "BuildServerProvider").SelectedItem = TeamCitySettings.ProviderName; Pump(host);
            Assert.That(page.TeamCityPage.IsVisible, Is.True); Assert.That(page.GitHubPage.IsVisible, Is.False);
            Assert.That(page.GetVisualDescendants().OfType<RadioButton>().Count(c => c.IsEffectivelyVisible), Is.EqualTo(4));
            Assert.That(calls, Is.Zero, "Typing or showing the page must never contact the server.");
            Button paste = Field<Button>(page, "lnkExtractDataFromBuildUrlCopiedInTheClipboard"); paste.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.TeamCityPage.Completion; Pump(host);
            Assert.That(calls, Is.EqualTo(1)); Assert.That(client!.Disposed, Is.True);
            Assert.That(Field<TextBox>(page, "TeamCityProjectName").Text, Is.EqualTo("Project"));
            Assert.That(Field<TextBox>(page, "TeamCityBuildIdFilter").Text, Is.EqualTo("Build"));
            Assert.That(session.HasChanges, Is.True); f.AssertMaterial(material);
            using var frame = host.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        }
        finally { await page.BeginClose(); host.Close(); }
    }

    [AvaloniaTest]
    public async Task ActualBrowserSharesReportUpdatesExistingRowPreservesFocusAndStopsOnDisable()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        f.Desktop.Save(new() { Language = "English", StartWithRecentWorkingDir = false });
        string sha = (await f.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        FakeClient client = new(sha); int factories = 0; List<ProcessStartInfo> opened = [];
        MainWindow window = new() { Width = 1200, Height = 780, GitHubActionsPollInterval = Timeout.InfiniteTimeSpan };
        window.CredentialsStoreOverride = GuestStore(f);
        window.TeamCityClientFactory = (_, credentials) => { factories++; Assert.That(credentials.BuildServerCredentialsType, Is.EqualTo(BuildServerCredentialsType.Guest)); return client; };
        window.ExternalLauncher = info => { opened.Add(info); return null; }; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository.Path); await Until(() => window.BuildServerStatusFor(sha) is not null); Pump(window);
            ListBox history = window.GetVisualDescendants().OfType<ListBox>().Single(c => c.ItemsSource is IEnumerable<GitCommit>);
            object? source = history.ItemsSource, selected = history.SelectedItem;
            Control row = history.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "RevisionHistoryRow" && RevisionHistoryRow.CommitHash(c) == sha);
            history.Focus(); Assert.That(window.GitHubActionsStatusFor(sha), Is.Null, "TeamCity must not be projected into GitHub wire DTOs.");
            Assert.That(window.BuildServerStatusFor(sha)!.Status, Is.EqualTo(BuildServerBuildStatus.Success));
            Assert.That(Field<TabControl>(window, "DetailsTabs").Items.OfType<TabItem>().Count(tab => tab.Header?.ToString() == "Build report"), Is.EqualTo(1));
            Assert.That(opened, Is.Empty);
            client.Failure = true; await window.RefreshGitHubActionsNowAsync(); Pump(window);
            Assert.That(factories, Is.EqualTo(1)); Assert.That(window.BuildServerStatusFor(sha)!.Status, Is.EqualTo(BuildServerBuildStatus.Failure));
            Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected));
            Assert.That(history.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "RevisionHistoryRow" && RevisionHistoryRow.CommitHash(c) == sha), Is.SameAs(row));
            Assert.That(history.IsKeyboardFocusWithin, Is.True);
            await window.ShowBuildPageAsync(); Assert.That(opened.Single().ArgumentList, Is.EqualTo(new[] { "https://ci.example/one/viewLog.html?buildId=3" }));
            GitHubActionsSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
            session.Set(GitDistributedSettingsScope.Local, TeamCitySettings.ServerUrlKey, "https://ci.example/two/"); await session.SaveFilesAsync(default);
            await Until(() => window.GitHubActionsError is not null && window.BuildServerStatusFor(sha) is null); Pump(window);
            Assert.That(client.Disposed, Is.True); Assert.That(factories, Is.EqualTo(1), "A new instance cannot inherit the previous instance's credentials or build links.");
            Assert.That(window.BuildGitHubActionsShowBuildPageMenu().IsEnabled, Is.False);
            int launches = opened.Count; await window.ShowBuildPageAsync(); Assert.That(opened.Count, Is.EqualTo(launches));
            session.Set(GitDistributedSettingsScope.Local, TeamCitySettings.EnableKey, "false"); await session.SaveFilesAsync(default);
            await Until(() => !window.GitHubActionsEnabled); Pump(window);
            Assert.That(window.BuildServerStatusFor(sha), Is.Null); Assert.That(client.Disposed, Is.True);
            Assert.That(window.BuildGitHubActionsShowBuildPageMenu().IsEnabled, Is.False);
        }
        finally { await window.CloseGitHubActionsAsync(); window.Close(); }
    }

    [AvaloniaTest]
    public async Task RepositorySwitchAndCloseDrainTeamCityWithoutLatePublication()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); Enable(f);
        f.Desktop.Save(new() { Language = "English", StartWithRecentWorkingDir = false });
        List<FakeClient> clients = [];
        MainWindow window = new() { GitHubActionsPollInterval = Timeout.InfiniteTimeSpan };
        window.CredentialsStoreOverride = GuestStore(f);
        window.TeamCityClientFactory = (_, _) => { FakeClient client = new("") { Block = true }; clients.Add(client); return client; }; window.Show();
        try
        {
            await window.OpenRepositoryAsync(f.Repository!.Path); await Until(() => clients.Count == 1 && clients[0].Started.Task.IsCompleted);
            string second = f.Git.Output("second"); await GitRepository.InitAsync(second, "main"); await window.OpenRepositoryAsync(second);
            await Until(() => clients.Count == 2 && clients[1].Started.Task.IsCompleted);
            Assert.That(clients[0].Finished.Task.IsCompleted, Is.True); Assert.That(clients[0].Disposed, Is.True);
            await window.CloseGitHubActionsAsync();
            Assert.That(clients[1].Finished.Task.IsCompleted, Is.True); Assert.That(clients[1].Disposed, Is.True);
            Assert.That(window.GitHubActionsCompletion.IsCompletedSuccessfully, Is.True); Assert.That(window.BuildServerStatusFor(new string('a', 40)), Is.Null);
        }
        finally { await window.CloseGitHubActionsAsync(); window.Close(); }
    }

    private static BuildServerCredentialsStore GuestStore(DetailedSettingsTestScope f)
    {
        BuildServerCredentialsStore store = new(f.Git.Output("private-ci"));
        store.SaveChanges(store.Load(TeamCitySettings.ProviderName, new Uri("https://ci.example/one/")), new BuildServerCredentials { BuildServerCredentialsType = BuildServerCredentialsType.Guest });
        return store;
    }
    private static void Enable(DetailedSettingsTestScope f) => f.Write(GitDistributedSettingsScope.Global,
        (TeamCitySettings.EnableKey, "true"), (TeamCitySettings.ShowPageKey, "true"), (TeamCitySettings.ProviderKey, TeamCitySettings.ProviderName),
        (TeamCitySettings.ServerUrlKey, "https://ci.example/one/"), (TeamCitySettings.ProjectNameKey, "Project"), (TeamCitySettings.LogAsGuestKey, "true"));
    private static T Field<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Pump(TopLevel window) => DetailedSettingsUiHost.Pump(window);
    private static Task Until(Func<bool> predicate) => DetailedSettingsUiHost.Until(predicate);
    private sealed class FakeClient(string sha) : ITeamCityClient
    {
        internal bool Failure, Block, Disposed;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TeamCityPage<TeamCityProject>> GetProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new TeamCityPage<TeamCityProject>([new("Project", "Project", null)]));
        public Task<TeamCityPage<TeamCityBuildType>> GetProjectBuildTypesAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(new TeamCityPage<TeamCityBuildType>([new("Build", "Build", projectId)]));
        public Task<TeamCityBuildType> GetBuildTypeAsync(string buildTypeId, CancellationToken cancellationToken = default) => Task.FromResult(new TeamCityBuildType(buildTypeId, "Build", "Project"));
        public async Task<TeamCityBuildSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                if (Block) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                return new([new("3", "Build", "3", "finished", Failure ? "FAILURE" : "SUCCESS", false, false, null, null, "Build #3", null, [sha])], DateTimeOffset.UtcNow);
            }
            finally { Finished.TrySetResult(); }
        }
        public Uri? BuildPageUri(TeamCityBuild build) => new("https://ci.example/one/viewLog.html?buildId=" + build.Id);
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

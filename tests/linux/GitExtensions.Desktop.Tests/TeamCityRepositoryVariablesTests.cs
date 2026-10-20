using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using Avalonia.Headless.NUnit;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitUIPluginInterfaces.BuildServerIntegration;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class TeamCityRepositoryVariablesTests
{
    private const string RawProjects = "TC_{cRepoProject}_{cRepoShortName}|Static";
    private static readonly Uri Endpoint = new("https://ci.example/one/");

    [AvaloniaTest]
    [TestCase("tracked", "TC_team_actual")]
    [TestCase("untracked", "TC_first_fallback")]
    [TestCase("detached", "TC_first_fallback")]
    public async Task BrowseExpandsConfiguredProjectsFromTheActualBranchFetchRemoteOrFallbackWithoutSavingTheExpandedValue(string context, string expected)
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        await ConfigureRemotesAsync(profile, context); Configure(profile, RawProjects);
        BuildServerCredentialsStore credentials = GuestStore(profile);
        Dictionary<string, byte[]> material = profile.Material(); byte[] savedCredentials = File.ReadAllBytes(credentials.FilePath);
        string sha = (await profile.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        ConcurrentQueue<TeamCitySettings> seen = new(); ConcurrentQueue<PrivateTeamCityTransport> transports = new();
        MainWindow window = Browser(credentials);
        window.TeamCityClientFactory = (settings, draft) =>
        {
            seen.Enqueue(settings);
            PrivateTeamCityTransport transport = new(sha, [expected, "Static"]); transports.Enqueue(transport);
            return new TeamCityApiClient(settings, draft, transport);
        };
        window.Show();
        try
        {
            await window.OpenRepositoryAsync(profile.Repository.Path); await ReadPublishedAsync(window, sha);
            Assert.That(seen.Count, Is.EqualTo(1));
            Assert.That(seen.Single().ProjectIds, Is.EqualTo(new[] { expected, "Static" }));
            AssertRequests(transports.Single(), [expected, "Static"]);
            profile.AssertMaterial(material); Assert.That(File.ReadAllBytes(credentials.FilePath), Is.EqualTo(savedCredentials));
            Assert.That((await profile.ReadAsync(GitDistributedSettingsScope.Global)).Values[TeamCitySettings.ProjectNameKey], Is.EqualTo(RawProjects));
        }
        finally { await window.CloseGitHubActionsAsync(); window.Close(); await DetailedSettingsUiHost.Until(() => !window.IsVisible); }
        Assert.That(transports.All(transport => transport.Disposed), Is.True);
        profile.AssertMaterial(material); Assert.That(File.ReadAllBytes(credentials.FilePath), Is.EqualTo(savedCredentials));
    }

    [AvaloniaTest]
    public async Task ExplicitRefreshReexpandsChangedFetchRemoteAndReplacesTheActualClientWhileKeepingRawXmlAndCredentials()
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        await ConfigureRemotesAsync(profile, "tracked"); Configure(profile, RawProjects);
        BuildServerCredentialsStore credentials = GuestStore(profile);
        Dictionary<string, byte[]> material = profile.Material(); byte[] savedCredentials = File.ReadAllBytes(credentials.FilePath);
        string sha = (await profile.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        ConcurrentQueue<TeamCitySettings> seen = new(); ConcurrentQueue<PrivateTeamCityTransport> transports = new();
        MainWindow window = Browser(credentials);
        window.TeamCityClientFactory = (settings, draft) =>
        {
            string project = seen.IsEmpty ? "TC_team_actual" : "TC_changed_new-repo";
            seen.Enqueue(settings);
            PrivateTeamCityTransport transport = new(sha, [project, "Static"]); transports.Enqueue(transport);
            return new TeamCityApiClient(settings, draft, transport);
        };
        window.Show();
        try
        {
            await window.OpenRepositoryAsync(profile.Repository.Path); await ReadPublishedAsync(window, sha);
            PrivateTeamCityTransport previous = transports.Single();
            await profile.Git.GitAsync("remote", "set-url", "upstream", "https://github.com/changed/new-repo.git");
            await window.RefreshGitHubActionsNowAsync(); await ReadPublishedAsync(window, sha);
            Assert.That(seen.Select(settings => settings.ProjectName), Is.EqualTo(new[] { "TC_team_actual|Static", "TC_changed_new-repo|Static" }));
            Assert.That(previous.Disposed, Is.True, "The old resolved project configuration must not keep polling after the fetch URL changes.");
            AssertRequests(transports.Last(), ["TC_changed_new-repo", "Static"]);
            profile.AssertMaterial(material); Assert.That(File.ReadAllBytes(credentials.FilePath), Is.EqualTo(savedCredentials));
            Assert.That((await profile.ReadAsync(GitDistributedSettingsScope.Global)).Values[TeamCitySettings.ProjectNameKey], Is.EqualTo(RawProjects));
        }
        finally { await window.CloseGitHubActionsAsync(); window.Close(); await DetailedSettingsUiHost.Until(() => !window.IsVisible); }
        Assert.That(transports.All(transport => transport.Disposed), Is.True);
        profile.AssertMaterial(material); Assert.That(File.ReadAllBytes(credentials.FilePath), Is.EqualTo(savedCredentials));
    }

    [AvaloniaTest]
    public async Task AbsentProjectSettingKeepsTheExistingFolderDefaultWithoutPersistingThatDefault()
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        await ConfigureRemotesAsync(profile, "tracked"); Configure(profile, projects: null);
        BuildServerCredentialsStore credentials = GuestStore(profile);
        Dictionary<string, byte[]> material = profile.Material(); byte[] savedCredentials = File.ReadAllBytes(credentials.FilePath);
        string sha = (await profile.Repository!.GetLogAsync(1, all: false)).Single().Hash;
        string expected = Path.GetFileName(profile.Repository.Path.TrimEnd(Path.DirectorySeparatorChar));
        ConcurrentQueue<TeamCitySettings> seen = new(); PrivateTeamCityTransport transport = new(sha, [expected]);
        MainWindow window = Browser(credentials);
        window.TeamCityClientFactory = (settings, draft) => { seen.Enqueue(settings); return new TeamCityApiClient(settings, draft, transport); };
        window.Show();
        try
        {
            await window.OpenRepositoryAsync(profile.Repository.Path); await ReadPublishedAsync(window, sha);
            Assert.That(seen.Single().ProjectIds, Is.EqualTo(new[] { expected })); AssertRequests(transport, [expected]);
            Assert.That((await profile.ReadAsync(GitDistributedSettingsScope.Global)).Values.ContainsKey(TeamCitySettings.ProjectNameKey), Is.False);
            profile.AssertMaterial(material); Assert.That(File.ReadAllBytes(credentials.FilePath), Is.EqualTo(savedCredentials));
        }
        finally { await window.CloseGitHubActionsAsync(); window.Close(); await DetailedSettingsUiHost.Until(() => !window.IsVisible); }
        Assert.That(transport.Disposed, Is.True); profile.AssertMaterial(material);
    }

    private static MainWindow Browser(BuildServerCredentialsStore store) => new()
    { Width = 1000, Height = 700, GitHubActionsPollInterval = Timeout.InfiniteTimeSpan, CredentialsStoreOverride = store };

    private static async Task ReadPublishedAsync(MainWindow window, string sha)
    {
        await DetailedSettingsUiHost.Until(() => window.BuildServerStatusFor(sha) is not null || window.GitHubActionsError is not null);
        Assert.That(window.GitHubActionsError, Is.Null, "The private TeamCity API must receive the resolved project IDs.");
        Assert.That(window.BuildServerStatusFor(sha)?.Status, Is.EqualTo(BuildServerBuildStatus.Success));
    }

    private static async Task ConfigureRemotesAsync(DetailedSettingsTestScope profile, string context)
    {
        await profile.Git.GitAsync("remote", "add", "origin", "https://github.com/other/origin.git");
        await profile.Git.GitAsync("remote", "add", "aaa", "https://github.com/first/fallback.git");
        await profile.Git.GitAsync("remote", "add", "upstream", "https://github.com/team/actual.git");
        await profile.Git.GitAsync("remote", "set-url", "--push", "upstream", "https://github.com/push/wrong.git");
        string branch = (await profile.Repository!.GetStatusAsync()).Branch!;
        await profile.Git.GitAsync("config", "branch." + branch + ".remote", "upstream");
        if (context == "untracked") { await profile.Git.GitAsync("config", "--unset", "branch." + branch + ".remote"); }
        else if (context == "detached") { await profile.Git.GitAsync("checkout", "--detach"); }
        else { Assert.That(context, Is.EqualTo("tracked")); }
    }

    private static void Configure(DetailedSettingsTestScope profile, string? projects)
    {
        profile.Desktop.Save(new() { Language = "English", StartWithRecentWorkingDir = false, CheckForUpdates = false });
        List<(string Key, string Value)> values =
        [
            (TeamCitySettings.EnableKey, "true"), (TeamCitySettings.ShowPageKey, "false"),
            (TeamCitySettings.ProviderKey, TeamCitySettings.ProviderName), (TeamCitySettings.ServerUrlKey, Endpoint.AbsoluteUri),
            (TeamCitySettings.LogAsGuestKey, "true"), ("future.λ", "keep <literal> & \nbody")
        ];
        if (projects is not null) { values.Add((TeamCitySettings.ProjectNameKey, projects)); }
        profile.Write(GitDistributedSettingsScope.Global, values.ToArray());
    }

    private static BuildServerCredentialsStore GuestStore(DetailedSettingsTestScope profile)
    {
        BuildServerCredentialsStore store = new(profile.Git.Output("private-teamcity-credentials"));
        store.SaveChanges(store.Load(TeamCitySettings.ProviderName, Endpoint), new BuildServerCredentials
            { BuildServerCredentialsType = BuildServerCredentialsType.Guest });
        return store;
    }

    private static void AssertRequests(PrivateTeamCityTransport transport, IReadOnlyList<string> expected)
    {
        Assert.That(transport.ProjectRequests, Is.EqualTo(expected)); Assert.That(transport.Requests, Is.Not.Empty);
        Assert.That(transport.Requests.All(request => request.Method == HttpMethod.Get && !request.HasAuthorization
            && request.Uri.AbsoluteUri.StartsWith(Endpoint.AbsoluteUri + "guestAuth/app/rest/", StringComparison.Ordinal)), Is.True);
    }

    private sealed class PrivateTeamCityTransport : HttpMessageHandler
    {
        private readonly string _sha;
        private readonly Dictionary<string, string> _projects;
        internal ConcurrentQueue<(HttpMethod Method, Uri Uri, bool HasAuthorization)> Requests { get; } = new();
        internal ConcurrentQueue<string> ProjectRequests { get; } = new();
        internal bool Disposed { get; private set; }
        internal PrivateTeamCityTransport(string sha, IReadOnlyList<string> expectedProjects)
        {
            _sha = sha;
            _projects = expectedProjects.Select((project, index) => (project, id: (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToDictionary(item => item.project, item => item.id, StringComparer.Ordinal);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Uri uri = request.RequestUri!;
            Requests.Enqueue((request.Method, uri, request.Headers.Authorization is not null));
            const string prefix = "/one/guestAuth/app/rest/";
            if (request.Method != HttpMethod.Get || uri.Scheme != "https" || uri.Host != "ci.example" || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)); }
            string relative = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
            if (relative.StartsWith("projects/", StringComparison.Ordinal))
            {
                string project = relative["projects/".Length..]; ProjectRequests.Enqueue(project);
                if (!_projects.TryGetValue(project, out string? id)) { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }
                return Xml(new XElement("project", new XAttribute("id", project), new XElement("buildTypes", new XAttribute("count", "1"),
                    new XElement("buildType", new XAttribute("id", "Build_" + id), new XAttribute("name", "Build"), new XAttribute("projectId", project)))));
            }
            foreach (string id in _projects.Values)
            {
                if (relative == "buildTypes/id:Build_" + id + "/builds/")
                {
                    bool running = Uri.UnescapeDataString(uri.Query).Contains("running:true", StringComparison.Ordinal);
                    return Xml(running ? new XElement("builds", new XAttribute("count", "0"))
                        : new XElement("builds", new XAttribute("count", "1"), new XElement("build", new XAttribute("id", id))));
                }
                if (relative == "builds/id:" + id)
                {
                    return Xml(new XElement("build", new XAttribute("id", id), new XAttribute("buildTypeId", "Build_" + id), new XAttribute("number", id),
                        new XAttribute("state", "finished"), new XAttribute("status", "SUCCESS"), new XElement("revisions", new XElement("revision", new XAttribute("version", _sha)))));
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        private static Task<HttpResponseMessage> Xml(XElement element) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(element.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml") });
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}

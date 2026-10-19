using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ForkCloneDialogTests
{
    [AvaloniaTest] [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalClientGeometryTwoTabsColumnsAndCompactCloneFieldsUseExistingCatalogues(string culture, bool dark)
    {
        await using Fixture f = await Fixture.Create(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        ForkCloneDialog dialog = await f.Show(theme: dark ? ThemeVariant.Dark : ThemeVariant.Light);
        Assert.That(dialog.Width, Is.EqualTo(744)); Assert.That(dialog.Height, Is.EqualTo(552));
        Assert.That(f.Field<TabControl>("tabControl").Items, Has.Count.EqualTo(2));
        foreach (string name in new[] { "destinationTB", "createDirTB", "ProtocolDropdownList", "addUpstreamRemoteAsCB", "depthUpDown", "cloneBtn", "_NO_TRANSLATE_closeBtn" })
        { Control control = f.Field<Control>(name); Assert.That(control.IsEffectivelyVisible && control.Bounds.Width > 0, Is.True, name); }
        Assert.That(f.Field<ComboBox>("ProtocolDropdownList").SelectedItem, Is.EqualTo("SSH"));
        Assert.That(f.Field<NumericUpDown>("depthUpDown").Maximum, Is.EqualTo(999));
        Assert.That(f.Field<NumericUpDown>("depthUpDown").Value, Is.EqualTo(0));
        f.Capture("fork-clone-mine-" + culture + (dark ? "-dark" : "-light"));
        f.SearchTab(); f.Pump();
        foreach (string name in new[] { "searchTB", "searchBtn", "getFromUserBtn", "forkBtn", "searchResultItemDescription", "openGitupPageBtn" })
        { Assert.That(f.Field<Control>(name).IsEffectivelyVisible, Is.True, name); }
        f.Capture("fork-clone-search-" + culture + (dark ? "-dark" : "-light"));
        Assert.That(f.Client.Searches, Is.Empty); Assert.That(f.Client.Forks, Is.Empty); Assert.That(dialog.CreatedRepository, Is.Null);
    }

    [AvaloniaTest]
    public async Task GuestPublicSearchAndGetFromUserAreExplicitAndNeverReadMyAccountOrFork()
    {
        await using Fixture f = await Fixture.Create(); ForkCloneDialog dialog = await f.Show(authenticated: false);
        Assert.That(f.Client.Owners, Is.Empty); Assert.That(f.Client.UserReads, Is.Zero);
        f.SearchTab(); f.Field<TextBox>("searchTB").Text = "  language:C# 雪 fork:true  "; f.Pump();
        Assert.That(f.Client.Searches, Is.Empty, "Editing does not query or fork."); f.Click("searchBtn"); await f.Idle();
        Assert.That(f.Client.Searches, Is.EqualTo(new[] { "  language:C# 雪 fork:true  " })); f.SelectSearch();
        Assert.That(f.Field<Button>("forkBtn").IsEnabled, Is.False); Assert.That(f.Field<Button>("cloneBtn").IsEnabled, Is.True);
        f.Field<TextBox>("searchTB").Text = "  other-user  "; f.Click("getFromUserBtn"); await f.Idle();
        Assert.That(f.Client.Owners, Is.EqualTo(new[] { "other-user" })); Assert.That(f.Client.Forks, Is.Empty); Assert.That(dialog.LastProcess, Is.Null);
    }

    [AvaloniaTest]
    public async Task EnterInSearchUsesTheLiteralQueryAndDoesNotTriggerCloneDefaultButton()
    {
        await using Fixture f = await Fixture.Create(); ForkCloneDialog dialog = await f.Show(); f.SearchTab();
        TextBox query = f.Field<TextBox>("searchTB"); query.Text = "repo:name"; query.Focus();
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); if (dialog.IsVisible) { dialog.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); }
        await f.Idle(); Assert.That(f.Client.Searches, Is.EqualTo(new[] { "repo:name" }));
        Assert.That(dialog.LastProcess, Is.Null); Assert.That(dialog.CreatedRepository, Is.Null);
    }

    [AvaloniaTest]
    public async Task ParentDetailsProtocolAndNullablePushRightsDoNotDependOnTheSelectedTab()
    {
        await using Fixture f = await Fixture.Create(); f.Client.Mine = f.Client.Fork;
        ForkCloneDialog dialog = await f.Show(); f.SelectMine(); await f.Idle();
        Assert.That(f.Field<TextBox>("createDirTB").Text, Is.EqualTo("repo"));
        Assert.That(f.Field<TextBox>("addUpstreamRemoteAsCB").Text, Is.EqualTo("owner"));
        Assert.That(f.Client.Details, Does.Contain(f.Client.Source.Identity));
        Assert.That(f.Field<TextBlock>("cloneInfoText").Text, Does.Contain("unknown"));
        f.Field<ComboBox>("ProtocolDropdownList").SelectedIndex = 1;
        f.Field<NumericUpDown>("depthUpDown").Value = 9;
        Assert.That(dialog.ReadCloneOptions(), Is.EqualTo(new GitCloneOptions(RecurseSubmodules: false, Depth: 9, SingleBranch: null)));
        f.Field<NumericUpDown>("depthUpDown").Value = 0; Assert.That(dialog.ReadCloneOptions().Depth, Is.Null);
        Assert.That(f.Client.Forks, Is.Empty); Assert.That(dialog.LastProcess, Is.Null);
    }

    [AvaloniaTest]
    public async Task ExplicitForkIsSinglePendingWriteAndSelectsAcceptedForkEvenBeforeItAppearsInMyList()
    {
        await using Fixture f = await Fixture.Create(); TaskCompletionSource<GitHubHostedRepository> pending = new();
        f.Releases.Add(() => pending.TrySetResult(f.Client.Fork)); f.Client.ForkOperation = (_, _) => pending.Task;
        ForkCloneDialog dialog = await f.Show(); await f.SearchAndSelect(); f.Click("forkBtn");
        Assert.That(dialog.IsBusy, Is.True); Assert.That(f.Client.Forks, Has.Count.EqualTo(1)); Assert.That(f.Field<Button>("forkBtn").IsEnabled, Is.False);
        pending.SetResult(f.Client.Fork); await f.Idle();
        Assert.That(dialog.ForkAccepted, Is.True); Assert.That(dialog.ForkOutcomeUnknown, Is.False);
        Assert.That(f.Field<TabControl>("tabControl").SelectedIndex, Is.Zero);
        Assert.That(f.Field<ListBox>("myReposLV").SelectedItem, Is.EqualTo(f.Client.Fork));
        Assert.That(f.Field<TextBlock>("helpTextLbl").Text, Does.Contain("preparing"));
        Assert.That(dialog.CreatedRepository, Is.Null); Assert.That(dialog.LastProcess, Is.Null); Assert.That(f.Client.Disposed, Is.False);
    }

    [AvaloniaTest]
    public async Task Accepted202ForkStaysSelectedAndClonesItsOwnRemoteWhenTheFollowingMyRepositoriesGetFails()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        await using Fixture f = await Fixture.Create(); int mineReads = 0; TaskCompletionSource refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Releases.Add(() => refresh.TrySetResult());
        using ForkHttpHandler handler = new(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/user/repos")
            {
                if (++mineReads == 1) { return WireJson(new[] { WireRepository(f.Client.Source) }); }
                await refresh.Task.WaitAsync(token); return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            if (request.Method == HttpMethod.Get && path == "/search/repositories")
            { return WireJson(new { total_count = 1, incomplete_results = false, items = new[] { WireRepository(f.Client.Source) } }); }
            if (request.Method == HttpMethod.Post && path == "/repos/owner/repo/forks")
            { return WireJson(WireRepository(f.Client.Fork), HttpStatusCode.Accepted); }
            if (request.Method == HttpMethod.Get && path == "/repos/owner/repo") { return WireJson(WireRepository(f.Client.Source)); }
            throw new AssertionException("Unexpected fixture request: " + request.Method + " " + path);
        });
        using GitHubHostingClient client = new(new(), FixtureCredentials(), handler);
        ForkCloneDialog dialog = await f.Show(client: client); await f.SearchAndSelect(); f.Click("forkBtn");
        await DetailedSettingsUiHost.Until(() => mineReads == 2);
        Assert.That(dialog.IsBusy && dialog.IsVisible, Is.True);
        Assert.That(((GitHubHostedRepository)f.Field<ListBox>("myReposLV").SelectedItem!).Identity, Is.EqualTo(f.Client.Fork.Identity), "The accepted fork is already selected before the refresh returns.");
        refresh.TrySetResult(); await f.Idle();
        Assert.That(dialog.ForkAccepted, Is.True); Assert.That(dialog.ForkOutcomeUnknown, Is.False);
        Assert.That(f.Field<TabControl>("tabControl").SelectedIndex, Is.Zero);
        Assert.That(f.Field<ListBox>("myReposLV").SelectedItem, Is.TypeOf<GitHubHostedRepository>());
        Assert.That(((GitHubHostedRepository)f.Field<ListBox>("myReposLV").SelectedItem!).Identity, Is.EqualTo(f.Client.Fork.Identity));
        Assert.That(f.Field<TextBlock>("helpTextLbl").Text, Does.Contain("accepted").And.Contain("Could not refresh My repositories.").And.Contain("503"));
        Assert.That(f.Field<Button>("cloneBtn").IsEnabled, Is.True); Assert.That(dialog.LastProcess, Is.Null);
        Assert.That(mineReads, Is.EqualTo(2)); Assert.That(handler.Requests.Count(r => r.Method == "POST"), Is.EqualTo(1));
        new ProcessDialogPreferencesStore().Save(new(true)); f.Field<ComboBox>("ProtocolDropdownList").SelectedIndex = 1;
        f.Click("cloneBtn"); await f.Idle();
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Assert.That(dialog.CreatedRepository, Is.Not.Null);
        var remotes = await dialog.CreatedRepository!.GetRemotesAsync();
        Assert.That(remotes.Single(remote => remote.Name == "origin").FetchUrls.Single(), Is.EqualTo(f.Client.Fork.CloneUrl));
        Assert.That(new CloneSourcesStore().Load().First(), Is.EqualTo(f.Client.Fork.CloneUrl));
        Assert.That(handler.Requests.Count(r => r.Method == "POST"), Is.EqualTo(1), "A failed secondary GET must never replay the accepted POST.");
    }

    [AvaloniaTest]
    public async Task ForeignForkDetailsAreRejectedBeforeSelectionOrParentPublicationAndCloneKeepsTheRequestedRepository()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        await using Fixture f = await Fixture.Create(); GitHubHostedRepository selected = f.Client.Fork with { Parent = null };
        GitHubHostedRepository foreign = f.Client.Source with { Id = 99, Owner = "other", Name = "different", Parent = null,
            CloneUrl = "https://github.com/other/different.git", SshUrl = "git@github.com:other/different.git", WebUrl = "https://github.com/other/different" };
        using ForkHttpHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/user/repos") { return WireJson(new[] { WireRepository(selected) }); }
            if (request.Method == HttpMethod.Get && path == "/repos/me/repo") { return WireJson(WireRepository(foreign)); }
            throw new AssertionException("Unexpected fixture request: " + request.Method + " " + path);
        });
        using GitHubHostingClient client = new(new(), FixtureCredentials(), handler);
        ForkCloneDialog dialog = await f.Show(client: client); f.SelectMine(); await f.Idle();
        Assert.That(dialog.LastError, Is.EqualTo("The repository details do not match the selected fork."));
        Assert.That(((GitHubHostedRepository)f.Field<ListBox>("myReposLV").SelectedItem!).Identity, Is.EqualTo(selected.Identity));
        Assert.That(f.Field<TextBox>("createDirTB").Text, Is.EqualTo(selected.Name));
        Assert.That(f.Field<TextBox>("addUpstreamRemoteAsCB").Text, Is.Empty);
        f.Field<ComboBox>("ProtocolDropdownList").SelectedIndex = 1;
        Assert.That(f.Field<TextBlock>("cloneInfoText").Text, Does.Contain(selected.Identity.ToString()).And.Not.Contain(foreign.Identity.ToString()));
        Assert.That(handler.Requests.Where(r => r.Path.StartsWith("/repos/", StringComparison.Ordinal)).Select(r => r.Path), Is.EqualTo(new[] { "/repos/me/repo" }));
        Assert.That(dialog.LastProcess, Is.Null); Assert.That(dialog.CreatedRepository, Is.Null);
        new ProcessDialogPreferencesStore().Save(new(true)); f.Click("cloneBtn"); await f.Idle();
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Assert.That(dialog.CreatedRepository, Is.Not.Null);
        var remotes = await dialog.CreatedRepository!.GetRemotesAsync();
        Assert.That(remotes.Single(remote => remote.Name == "origin").FetchUrls.Single(), Is.EqualTo(selected.CloneUrl));
        Assert.That(handler.Requests.All(r => r.Method == "GET"), Is.True);
    }

    [AvaloniaTest]
    public async Task ARefusedForkKeepsTheSearchAndDraftAndNeverReloadsOrRetriesAutomatically()
    {
        await using Fixture f = await Fixture.Create(); f.Client.ForkOperation = (_, _) => throw new GitHubHostingException("Fork refused", 403);
        ForkCloneDialog dialog = await f.Show(); await f.SearchAndSelect(); int before = f.Client.Owners.Count;
        f.Field<TextBox>("createDirTB").Text = "My clone draft"; f.Click("forkBtn"); await f.Idle();
        Assert.That(dialog.LastError, Does.Contain("Fork refused")); Assert.That(dialog.IsVisible, Is.True);
        Assert.That(f.Field<TabControl>("tabControl").SelectedIndex, Is.EqualTo(1)); Assert.That(f.Field<TextBox>("createDirTB").Text, Is.EqualTo("My clone draft"));
        Assert.That(f.Client.Forks, Has.Count.EqualTo(1)); Assert.That(f.Client.Owners, Has.Count.EqualTo(before)); Assert.That(dialog.ForkAccepted, Is.False);
    }

    [AvaloniaTest]
    public async Task AnUnknownForkOutcomeWarnsAndBlocksAnotherPostWhileMyTabCanRefreshWithGet()
    {
        await using Fixture f = await Fixture.Create(); f.Client.ForkOperation = (_, _) => throw new GitHubHostingException("The hosting request timed out.");
        ForkCloneDialog dialog = await f.Show(); await f.SearchAndSelect(); f.Click("forkBtn"); await f.Idle();
        Assert.That(dialog.ForkOutcomeUnknown, Is.True); Assert.That(f.Field<TextBlock>("SearchRepositoryNotice").Text, Does.Contain("may have reached"));
        Assert.That(f.Field<Button>("forkBtn").IsEnabled, Is.False); int reads = f.Client.Owners.Count;
        f.Field<TabControl>("tabControl").SelectedIndex = 0; await f.Idle(); Assert.That(f.Client.Owners, Has.Count.EqualTo(reads + 1));
        Assert.That(f.Client.Forks, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task CloseDuringSearchCancelsAndDrainsALateResponseBeforeTheWindowAndBorrowedClientAreReleased()
    {
        await using Fixture f = await Fixture.Create(); TaskCompletionSource<GitHubRepositorySearchResult> release = new(); CancellationToken captured = default;
        f.Releases.Add(() => release.TrySetResult(new([f.Client.Source], 1)));
        f.Client.SearchOperation = (_, token) => { captured = token; return release.Task; };
        ForkCloneDialog dialog = await f.Show(authenticated: false); f.SearchTab(); f.Field<TextBox>("searchTB").Text = "repo"; f.Click("searchBtn");
        dialog.Close(); Assert.That(captured.IsCancellationRequested, Is.True); Assert.That(dialog.IsBusy && dialog.IsVisible, Is.True);
        release.SetResult(new([f.Client.Source], 1)); await f.Idle();
        Assert.That(dialog.IsVisible, Is.False); Assert.That(f.Client.Disposed, Is.False); Assert.That(dialog.CreatedRepository, Is.Null);
        Assert.That(f.Client.Forks, Is.Empty);
    }

    [AvaloniaTest]
    public async Task HomepageIsAnExplicitSafeExternalActionAndAnAbsentHomepageReportsTheOriginalError()
    {
        await using Fixture f = await Fixture.Create(); ForkCloneDialog dialog = await f.Show(); await f.SearchAndSelect();
        Assert.That(f.Opened, Is.Empty); f.Click("openGitupPageBtn"); await f.Idle();
        Assert.That(f.Opened.Single(), Is.EqualTo(new Uri("https://project.example.invalid/docs")));
        f.Client.Source = f.Client.Source with { Homepage = null }; await f.SearchAndSelect(); f.Click("openGitupPageBtn"); await f.Idle();
        Assert.That(dialog.LastError, Is.EqualTo("No homepage defined")); Assert.That(f.Opened, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task NativeCloneWaitsForRealProcessOkThenAddsTheProtocolMatchedParentAndRemembersLiteralPath()
    {
        await using Fixture f = await Fixture.Create(); f.Client.Mine = f.Client.Fork; new ProcessDialogPreferencesStore().Save(new(false));
        ForkCloneDialog dialog = await f.Show(); f.SelectMine(); await f.Idle();
        f.Field<ComboBox>("ProtocolDropdownList").SelectedIndex = 1;
        string child = " clone '雪;$(literal)\n "; f.Field<TextBox>("createDirTB").Text = child;
        f.Field<NumericUpDown>("depthUpDown").Value = 1; f.Click("cloneBtn");
        GitProcessDialog process = await f.Process(); await DetailedSettingsUiHost.Until(() => !process.IsRunning);
        Assert.That(process.Result!.Success, Is.True, process.Error?.ToString()); Assert.That(dialog.CreatedRepository, Is.Null); Assert.That(dialog.IsBusy, Is.True);
        PushProcessTestDriver.Pointer(process.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Ok")); await f.Idle();
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Assert.That(dialog.LastProcess!.Result, Is.SameAs(process.Result));
        Assert.That(dialog.CreatedRepository!.Path, Is.EqualTo(Path.Combine(f.Destination, child))); Assert.That(dialog.CloneWarning, Is.Null);
        var remotes = await dialog.CreatedRepository.GetRemotesAsync();
        Assert.That(remotes.Single(remote => remote.Name == "owner").FetchUrls.Single(), Is.EqualTo(f.Client.Source.CloneUrl));
        Assert.That(new CloneSourcesStore().Load().First(), Is.EqualTo(f.Client.Fork.CloneUrl));
        Assert.That(new ClonePreferencesStore().Load().InitializeSubmodules, Is.True, "The hosted form does not change the general Clone submodule preference.");
        Assert.That(f.Client.Forks, Is.Empty, "Cloning an existing fork never POSTs another fork.");
    }

    [AvaloniaTest]
    public async Task UpstreamFailurePreservesTheSuccessfulCloneAndHistoryWithoutRepeatingCloneOrDeletingIt()
    {
        await using Fixture f = await Fixture.Create(); f.Client.Mine = f.Client.Fork; new ProcessDialogPreferencesStore().Save(new(true));
        ForkCloneDialog dialog = await f.Show(); f.SelectMine(); await f.Idle(); f.Field<TextBox>("addUpstreamRemoteAsCB").Text = "origin";
        f.Click("cloneBtn"); await f.Idle();
        Assert.That(dialog.CreatedRepository, Is.Not.Null); Assert.That(dialog.LastProcess!.Result!.Success, Is.True);
        Assert.That(dialog.CloneWarning, Does.Contain("cloned").And.Contain("upstream"));
        Assert.That(await dialog.CreatedRepository!.ResolveRevisionAsync("HEAD"), Is.EqualTo(await f.Git.Repository.ResolveRevisionAsync("HEAD")));
        Assert.That(new RepositoryHistoryStore().Load().Recent.First().Path, Is.EqualTo(dialog.CreatedRepository.Path));
        Assert.That((await dialog.CreatedRepository.GetRemotesAsync()).Select(remote => remote.Name), Is.EqualTo(new[] { "origin" }));
    }

    [AvaloniaTest]
    public async Task ARealCloneFailureKeepsTheFormAndOccupiedDirectoryAndNeverPublishesHistory()
    {
        await using Fixture f = await Fixture.Create(); ForkCloneDialog dialog = await f.Show(); f.SelectMine();
        string path = Path.Combine(f.Destination, "occupied"); Directory.CreateDirectory(path); await File.WriteAllTextAsync(Path.Combine(path, "keep"), "keep exactly\n");
        f.Field<TextBox>("createDirTB").Text = "occupied"; string? before = f.History(); f.Click("cloneBtn");
        GitProcessDialog process = await f.Process(); await DetailedSettingsUiHost.Until(() => !process.IsRunning);
        Assert.That(process.Result!.Success, Is.False); PushProcessTestDriver.Pointer(process.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Ok")); await f.Idle();
        Assert.That(dialog.CreatedRepository, Is.Null); Assert.That(dialog.LastError, Is.Not.Null); Assert.That(dialog.IsVisible, Is.True);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(path, "keep")), Is.EqualTo("keep exactly\n")); Assert.That(f.History(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task OneOwnerCloseAfterNativeCompletionRetainsTheCloneButCannotPublishItOrItsHistory()
    {
        await using Fixture f = await Fixture.Create(); Window owner = new() { Width = 950, Height = 730 }; owner.Show(); f.Windows.Add(owner);
        ForkCloneDialog dialog = await f.Show(owner: owner); f.SelectMine(); string? before = f.History(); f.Click("cloneBtn");
        GitProcessDialog process = await f.Process(); await DetailedSettingsUiHost.Until(() => !process.IsRunning);
        Assert.That(process.Result!.Success, Is.True); string clonePath = Path.Combine(f.Destination, "repo"); owner.Close();
        await f.Idle(); await DetailedSettingsUiHost.Until(() => !owner.IsVisible && !dialog.IsVisible);
        Assert.That(dialog.LastProcess!.WorkflowStopRequested, Is.True); Assert.That(dialog.CreatedRepository, Is.Null); Assert.That(f.History(), Is.EqualTo(before));
        Assert.That(await GitRepository.OpenAsync(clonePath), Is.Not.Null, "Completed native objects survive cancellation of the following UI workflow.");
    }

    [Test]
    public void HostedDestinationRejectsTraversalButPreservesEveryLiteralCharacter()
    {
        string root = Path.Combine(Path.GetTempPath(), "literal parent");
        foreach (string child in new[] { "../outside", "/outside", "a/../outside" })
        { Assert.Throws<ArgumentException>(() => ForkCloneDialog.CloneDestination(root, child)); }
        const string literal = " child '雪;$(literal)\n ";
        Assert.That(ForkCloneDialog.CloneDestination(root, literal), Is.EqualTo(Path.Combine(root, literal)));
    }

    [AvaloniaTest]
    public async Task OptionalDashboardRouteIsDormantUntilTheActualGitHubLinkIsClicked()
    {
        int invoked = 0; Window owner = new() { Width = 1050, Height = 780 };
        DashboardControl dashboard = new(owner, () => new(), _ => { }, _ => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
            () => { invoked++; return Task.CompletedTask; });
        owner.Content = dashboard; owner.Show();
        try
        {
            DetailedSettingsUiHost.Pump(owner); Assert.That(invoked, Is.Zero);
            TextBlock text = dashboard.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == "Clone GitHub repository");
            Button link = text.GetVisualAncestors().OfType<Button>().First(); PushProcessTestDriver.Pointer(link); Assert.That(invoked, Is.EqualTo(1));
        }
        finally { owner.Close(); dashboard.Dispose(); }
    }

    private static BuildServerCredentials FixtureCredentials() => new()
    { BuildServerCredentialsType = BuildServerCredentialsType.BearerToken, BearerToken = "synthetic-fork-clone-fixture-token" };
    private static object WireRepository(GitHubHostedRepository repository) => new
    {
        id = repository.Id, name = repository.Name, owner = new { login = repository.Owner }, description = repository.Description,
        @private = repository.Private, fork = repository.Fork, default_branch = repository.DefaultBranch,
        clone_url = repository.CloneUrl, ssh_url = repository.SshUrl, html_url = repository.WebUrl,
        parent = repository.Parent is { } parent ? new { owner = new { login = parent.Owner }, name = parent.Name } : null
    };
    private static HttpResponseMessage WireJson(object value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class ForkHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _reply;
        internal ForkHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : this((request, _) => Task.FromResult(reply(request))) { }
        internal ForkHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) => _reply = reply;
        internal List<(string Method, string Path)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath));
            return _reply(request, token);
        }
    }

    private sealed class Client : IGitHubForkCloneClient
    {
        internal GitHubHostedRepository Source = new(1, "owner", "repo", "Literal <description> & 雪", false, false, "main", "https://github.com/owner/repo.git", "git@github.com:owner/repo.git", "https://github.com/owner/repo")
        { ForksCount = 7, Homepage = "https://project.example.invalid/docs" };
        internal GitHubHostedRepository Fork => new(2, "me", "repo", "My fork", false, true, "main", "https://github.com/me/repo.git", "git@github.com:me/repo.git", "https://github.com/me/repo", Source.Identity);
        internal GitHubHostedRepository? Mine;
        internal List<string?> Owners { get; } = [];
        internal List<string> Searches { get; } = [];
        internal List<GitHubRepositoryId> Forks { get; } = [], Details { get; } = [];
        internal int UserReads;
        internal bool Disposed;
        internal Func<string, CancellationToken, Task<GitHubRepositorySearchResult>>? SearchOperation;
        internal Func<GitHubRepositoryId, CancellationToken, Task<GitHubHostedRepository>>? ForkOperation;
        public GitHubHostingSettings Settings { get; } = new();
        public Task<string> GetCurrentUserAsync(CancellationToken cancellationToken = default) { UserReads++; return Task.FromResult("me"); }
        public Task<GitHubHostedRepository> GetRepositoryAsync(GitHubRepositoryId repository, CancellationToken cancellationToken = default)
        { Details.Add(repository); return Task.FromResult(repository == Fork.Identity ? Fork : Source); }
        public Task<GitHubHostingPage<GitHubHostedRepository>> GetRepositoriesAsync(string? owner = null, CancellationToken cancellationToken = default)
        { Owners.Add(owner); return Task.FromResult(new GitHubHostingPage<GitHubHostedRepository>([Mine ?? Source])); }
        public Task<GitHubRepositorySearchResult> SearchRepositoriesAsync(string query, CancellationToken cancellationToken = default)
        { Searches.Add(query); return SearchOperation?.Invoke(query, cancellationToken) ?? Task.FromResult(new GitHubRepositorySearchResult([Source], 1)); }
        public Task<GitHubHostedRepository> ForkRepositoryAsync(GitHubRepositoryId repository, string? organization = null, CancellationToken cancellationToken = default)
        { Forks.Add(repository); Assert.That(organization, Is.Null, "The original form forks into the personal account."); return ForkOperation?.Invoke(repository, cancellationToken) ?? Task.FromResult(Fork); }
        public void Dispose() => Disposed = true;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal ProxyGourceFixture Git { get; }
        internal Client Client { get; } = new();
        internal ForkCloneDialog Window { get; private set; } = null!;
        internal List<Window> Windows { get; } = [];
        internal List<Uri> Opened { get; } = [];
        internal List<Action> Releases { get; } = [];
        internal string Destination => Path.Combine(Git.Root, "clones");
        private readonly Dictionary<string, string?> _environment = [];
        private Fixture(ProxyGourceFixture git) => Git = git;
        internal static async Task<Fixture> Create()
        {
            ProxyGourceFixture git = await ProxyGourceFixture.CreateAsync(); Fixture fixture = new(git);
            try
            {
                fixture.Set("GIT_ALLOW_PROTOCOL", "file"); fixture.Set("GIT_TERMINAL_PROMPT", "0");
                string source = new Uri(git.Repository.Path).AbsoluteUri;
                await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL")!,
                    "[url \"" + source + "\"]\n\tinsteadOf = https://github.com/owner/repo.git\n\tinsteadOf = https://github.com/me/repo.git\n\tinsteadOf = git@github.com:owner/repo.git\n\tinsteadOf = git@github.com:me/repo.git\n");
                new ProcessDialogPreferencesStore().Save(new(false)); new ClonePreferencesStore().Save(new(fixture.Destination, true)); return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        private void Set(string name, string value) { _environment[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
        internal async Task<ForkCloneDialog> Show(bool authenticated = true, ThemeVariant? theme = null, Window? owner = null, IGitHubForkCloneClient? client = null)
        {
            Window = new(client ?? Client, authenticated, Destination, Opened.Add) { RequestedThemeVariant = theme ?? ThemeVariant.Light }; Windows.Add(Window);
            if (owner is null) { Window.Show(); } else { _ = Window.ShowDialog(owner); }
            await Window.InitializeAsync(); await Idle(); return Window;
        }
        internal T Field<T>(string name) where T : Control
            => Window.GetLogicalDescendants().OfType<T>().Concat(Window.GetVisualDescendants().OfType<T>()).Distinct().Single(control => control.Name == name);
        internal void Pump() { if (Window.IsVisible) { DetailedSettingsUiHost.Pump(Window); } }
        internal void Click(string name) { Pump(); PushProcessTestDriver.Pointer(Field<Control>(name)); Pump(); }
        internal void SearchTab() { Field<TabControl>("tabControl").SelectedIndex = 1; Pump(); }
        internal void SelectMine() { Field<ListBox>("myReposLV").SelectedIndex = 0; Pump(); }
        internal void SelectSearch() { Field<ListBox>("searchResultsLV").SelectedIndex = 0; Pump(); }
        internal async Task SearchAndSelect() { SearchTab(); Field<TextBox>("searchTB").Text = "repo"; Click("searchBtn"); await Idle(); SelectSearch(); await Idle(); }
        internal async Task Idle() { await DetailedSettingsUiHost.Until(() => !Window.IsBusy); await Window.Completion; Pump(); }
        internal async Task<GitProcessDialog> Process()
        { await DetailedSettingsUiHost.Until(() => Window.OwnedWindows.OfType<GitProcessDialog>().Any()); return Window.OwnedWindows.OfType<GitProcessDialog>().Single(); }
        internal string? History() { string file = new RepositoryHistoryStore().FilePath; return File.Exists(file) ? File.ReadAllText(file) : null; }
        internal void Capture(string name)
        {
            Pump(); using var frame = Window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
            string root = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(root);
            string file = Path.Combine(root, name + ".png"); frame!.Save(file); TestContext.AddTestAttachment(file);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (Action release in Releases) { release(); }
            foreach (Window window in Windows.AsEnumerable().Reverse()) { if (window.IsVisible) { window.Close(); } }
            if (Window is not null) { await DetailedSettingsUiHost.Until(() => !Window.IsBusy); await Window.Completion; }
            Dispatcher.UIThread.RunJobs(); Client.Dispose();
            foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); }
            Git.Dispose();
        }
    }
}

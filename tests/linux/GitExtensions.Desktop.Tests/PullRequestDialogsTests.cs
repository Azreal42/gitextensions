using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class PullRequestDialogsTests
{
    [AvaloniaTest] [TestCase("en", false)] [TestCase("fr", true)]
    public async Task CreateMatchesOriginalFieldsAndReadableClientGeometry(string culture, bool dark)
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CreatePullRequestDialog dialog = new(f.Git.Repository, PullRequestUiFixture.Remotes, f.Client, templateReader: _ => Task.FromResult<string?>("## Summary\n\nTemplate body"));
        f.Window = dialog; dialog.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; dialog.Show(); await dialog.InitializeAsync(); await f.IdleAsync();
        Assert.That(dialog.Width, Is.EqualTo(546)); Assert.That(dialog.Height, Is.EqualTo(323));
        foreach (string name in new[] { "_pullReqTargetsCB", "_yourBranchesCB", "_remoteBranchesCB", "_titleTB", "_bodyTB", "_createBtn" }) { Assert.That(f.Field<Control>(name).IsEffectivelyVisible, Is.True, name); }
        AssertCaptions(dialog); Assert.That(f.Field<TextBox>("_bodyTB").Text, Is.EqualTo("## Summary\n\nTemplate body"));
        f.Capture("create-pull-request-" + culture + (dark ? "-dark.png" : "-light.png")); Assert.That(f.Client.Created, Is.Empty);
    }

    [AvaloniaTest]
    public async Task CreateUsesNativeGitTitleAndExplicitSourceTargetTitleBodyWithoutPassiveMutation()
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync();
        string subject = (await f.Git.Repository.GetLogAsync(1, all: false)).Single().Subject;
        CreatePullRequestDialog dialog = new(f.Git.Repository, PullRequestUiFixture.Remotes, f.Client); f.Window = dialog; dialog.Show(); await dialog.InitializeAsync(); await f.IdleAsync();
        Assert.That(f.Field<TextBox>("_titleTB").Text, Is.EqualTo(subject)); Assert.That(f.Client.Created, Is.Empty);
        f.Edit("_titleTB", "  My PR title  "); f.Edit("_bodyTB", "  Full literal <body> & text\nsecond line  "); f.Click("_createBtn");
        await DetailedSettingsUiHost.Until(() => !dialog.IsVisible); Assert.That(f.Client.Created.Count, Is.EqualTo(1));
        var request = f.Client.Created.Single(); Assert.That(request.Repository, Is.EqualTo(PullRequestTestClient.Target.Identity));
        Assert.That(request.Draft.Head, Is.EqualTo("tester:main")); Assert.That(request.Draft.Base, Is.EqualTo("main"));
        Assert.That(request.Draft.Title, Is.EqualTo("My PR title")); Assert.That(request.Draft.Body, Is.EqualTo("Full literal <body> & text\nsecond line")); Assert.That(f.Client.Disposed, Is.False);
    }

    [AvaloniaTest] [TestCase("oversized")] [TestCase("outside")]
    public async Task DefaultTemplateReaderReportsUnsupportedInputWithoutPuttingItInTheDraft(string kind)
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync();
        string directory = Path.Combine(f.Git.Repository.Path, ".github"), outside = f.Git.Repository.Path + "-outside-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory); string template = Path.Combine(directory, "PULL_REQUEST_TEMPLATE.md");
        try
        {
            if (kind == "oversized") { await File.WriteAllTextAsync(template, new string('x', 1024 * 1024 + 1)); }
            else { await File.WriteAllTextAsync(outside, "Outside text must not become a PR draft"); File.CreateSymbolicLink(template, outside); }
            CreatePullRequestDialog dialog = new(f.Git.Repository, PullRequestUiFixture.Remotes, f.Client); f.Window = dialog;
            dialog.Show(); await dialog.InitializeAsync(); await f.IdleAsync();
            Assert.That(f.Field<TextBox>("_bodyTB").Text, Is.Null.Or.Empty);
            Assert.That(f.Field<TextBlock>("PullRequestNotice").Text, Does.Contain(kind == "oversized" ? "supported size" : "inside the repository"));
            Assert.That(f.Field<TextBox>("_titleTB").Text, Is.Not.Null.And.Not.Empty);
            Assert.That(f.Client.Created, Is.Empty);
        }
        finally { File.Delete(outside); }
    }

    [AvaloniaTest]
    public async Task LateTemplateAndGitTitleNeverOverwriteHumanDraftOrItsFocusAndSelection()
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync(); TaskCompletionSource<string?> template = new(), title = new();
        f.ReleaseOnDispose.Add(() => { template.TrySetResult(null); title.TrySetResult(null); });
        CreatePullRequestDialog dialog = new(f.Git.Repository, PullRequestUiFixture.Remotes, f.Client, templateReader: _ => template.Task, titleReader: (_, _, _) => title.Task);
        f.Window = dialog; dialog.Show(); _ = dialog.InitializeAsync(); await DetailedSettingsUiHost.Until(() => f.Field<ComboBox>("_yourBranchesCB").ItemCount == 2);
        f.Edit("_titleTB", "Human title"); f.Edit("_bodyTB", "Human body"); TextBox body = f.Field<TextBox>("_bodyTB"); body.CaretIndex = 6; body.SelectionStart = 1; body.SelectionEnd = 6;
        template.SetResult("Remote template"); title.SetResult("Git subject"); await f.IdleAsync();
        Assert.That(f.Field<TextBox>("_titleTB").Text, Is.EqualTo("Human title")); Assert.That(body.Text, Is.EqualTo("Human body"));
        Assert.That(body.IsKeyboardFocusWithin, Is.True); Assert.That((body.SelectionStart, body.SelectionEnd), Is.EqualTo((1, 6)));
    }

    [AvaloniaTest]
    public async Task ChangingTargetCancelsOldBranchesAndCannotReplaceNewSelectionOrHumanTitle()
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync(); TaskCompletionSource entered = new(); TaskCompletionSource<GitHubHostingPage<GitHubHostedBranch>> old = new(); CancellationToken oldToken = default;
        f.ReleaseOnDispose.Add(() => old.TrySetResult(new([])));
        GitHubHostedRepository second = PullRequestTestClient.Target with { Id = 3, Owner = "other", DefaultBranch = "new-base" };
        f.Client.ReadBranches = (repository, token) =>
        {
            if (repository == PullRequestTestClient.Target.Identity) { oldToken = token; entered.TrySetResult(); return old.Task; }
            return Task.FromResult(new GitHubHostingPage<GitHubHostedBranch>([new(repository == second.Identity ? "new-base" : "main", new string('a', 40))]));
        };
        CreatePullRequestDialog dialog = new(f.Git.Repository, [.. PullRequestUiFixture.Remotes, new("other", second, false)], f.Client, titleReader: (_, _, _) => Task.FromResult<string?>("Git title")); f.Window = dialog; dialog.Show(); _ = dialog.InitializeAsync(); await entered.Task;
        f.Edit("_titleTB", "Human title"); f.Field<ComboBox>("_pullReqTargetsCB").SelectedIndex = 1; await DetailedSettingsUiHost.Until(() => f.Field<ComboBox>("_remoteBranchesCB").SelectedItem as string == "new-base");
        Assert.That(oldToken.IsCancellationRequested, Is.True); old.SetResult(new([new("stale", new string('a', 40))])); await f.IdleAsync();
        Assert.That(f.Field<ComboBox>("_remoteBranchesCB").SelectedItem, Is.EqualTo("new-base")); Assert.That(f.Field<TextBox>("_titleTB").Text, Is.EqualTo("Human title"));
    }

    [AvaloniaTest]
    public async Task FailedCreationLeavesTheEditableDraftAndNoAutomaticRetry()
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync(); f.Client.Create = (_, _, _) => throw new GitHubHostingException("Creation refused", 422);
        CreatePullRequestDialog dialog = new(f.Git.Repository, PullRequestUiFixture.Remotes, f.Client); f.Window = dialog; dialog.Show(); await dialog.InitializeAsync(); await f.IdleAsync();
        f.Edit("_titleTB", "My draft"); f.Edit("_bodyTB", "Retained body"); f.Click("_createBtn"); await f.IdleAsync();
        Assert.That(dialog.IsVisible, Is.True); Assert.That(f.Field<TextBox>("_titleTB").Text, Is.EqualTo("My draft")); Assert.That(f.Field<TextBox>("_bodyTB").Text, Is.EqualTo("Retained body"));
        Assert.That(f.Field<TextBlock>("PullRequestNotice").Text, Does.Contain("Creation refused")); Assert.That(f.Client.Created.Count, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task ClosingCreateCancelsAndActuallyDrainsAStaleTemplateRead()
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync(); TaskCompletionSource entered = new(), release = new(); CancellationToken captured = default;
        f.ReleaseOnDispose.Add(() => release.TrySetResult());
        CreatePullRequestDialog dialog = new(f.Git.Repository, PullRequestUiFixture.Remotes, f.Client, templateReader: async token => { captured = token; entered.SetResult(); await release.Task; return "late"; }); f.Window = dialog; dialog.Show(); _ = dialog.InitializeAsync(); await entered.Task;
        dialog.Close(); Assert.That(captured.IsCancellationRequested, Is.True); Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.Completion.IsCompleted, Is.False);
        release.SetResult(); await DetailedSettingsUiHost.Until(() => !dialog.IsVisible); await dialog.Completion; Assert.That(f.Client.Created, Is.Empty); Assert.That(f.Client.Disposed, Is.False);
    }

    [AvaloniaTest] [TestCase("en", false)] [TestCase("fr", true)]
    public async Task ViewUsesOriginalColumnsActionsTabsAndSharedDiffControls(string culture, bool dark)
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        ViewPullRequestsDialog dialog = View(f); dialog.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; dialog.Show(); await dialog.InitializeAsync(); await f.IdleAsync();
        Assert.That(dialog.Width, Is.EqualTo(754)); Assert.That(dialog.Height, Is.EqualTo(511)); Assert.That(dialog.FilesView, Is.TypeOf<FileStatusListView>()); Assert.That(dialog.DiffViewer, Is.TypeOf<DiffView>());
        Assert.That(dialog.FilesView.Entries.Single().Path, Is.EqualTo("literal file.txt")); Assert.That(dialog.DiffViewer.CanonicalText, Is.EqualTo(PullRequestTestClient.Patch)); AssertCaptions(dialog);
        f.Capture("view-pull-requests-" + culture + (dark ? "-dark.png" : "-light.png")); f.Click("tabPage2"); AssertCaptions(dialog);
        Assert.That(dialog.Discussion.GetVisualDescendants().OfType<SelectableTextBlock>().Select(text => text.Text), Does.Contain("PR 7 body <script>literal</script> https://example.invalid/issue/7"));
        f.Capture("pull-request-comments-" + culture + (dark ? "-dark.png" : "-light.png")); Assert.That(f.Client.Posted, Is.Empty); Assert.That(f.Client.Closed, Is.Empty);
    }

    [AvaloniaTest]
    public async Task SelectionReadsKeepListKeyboardFocusAndPerPullRequestCommentDrafts()
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync(); ViewPullRequestsDialog dialog = View(f); dialog.Show(); await dialog.InitializeAsync(); await f.IdleAsync();
        Control files = dialog.FilesView, diff = dialog.DiffViewer; f.Click("tabPage2"); f.Edit("_postCommentText", "draft seven");
        ListBox list = f.Field<ListBox>("_pullRequestsList"); PushProcessTestDriver.Pointer(list.GetVisualDescendants().OfType<ListBoxItem>().First());
        TopLevel inputOwner = TopLevel.GetTopLevel(list) ?? throw new InvalidOperationException("The list is not attached to a window.");
        inputOwner.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        if (inputOwner.IsVisible) { inputOwner.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); }
        await f.IdleAsync();
        Assert.That(dialog.SelectedPullRequest?.Number, Is.EqualTo(8)); Assert.That(list.IsKeyboardFocusWithin, Is.True); Assert.That(f.Field<TextBox>("_postCommentText").Text, Is.Empty);
        f.Edit("_postCommentText", "draft eight"); list.SelectedIndex = 0; await f.IdleAsync(); Assert.That(f.Field<TextBox>("_postCommentText").Text, Is.EqualTo("draft seven"));
        Assert.That(dialog.FilesView, Is.SameAs(files)); Assert.That(dialog.DiffViewer, Is.SameAs(diff));
    }

    [AvaloniaTest]
    public async Task StaleDiffAndDiscussionAreCancelledAndCannotPublishForTheNewPullRequest()
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync(); TaskCompletionSource entered = new(); TaskCompletionSource<string> oldDiff = new(); TaskCompletionSource<GitHubHostingPage<GitHubPullRequestDiscussionEntry>> oldDiscussion = new(); CancellationToken captured = default;
        f.ReleaseOnDispose.Add(() => { oldDiff.TrySetResult(PullRequestTestClient.Patch); oldDiscussion.TrySetResult(new([])); });
        f.Client.ReadDiff = (number, token) => { if (number == 7) { captured = token; entered.TrySetResult(); return oldDiff.Task; } return Task.FromResult(PullRequestTestClient.Patch); };
        f.Client.ReadDiscussion = (number, _) => number == 7 ? oldDiscussion.Task : Task.FromResult(new GitHubHostingPage<GitHubPullRequestDiscussionEntry>([new(GitHubDiscussionKind.Comment, "current", DateTimeOffset.Now, "CURRENT EIGHT")]));
        ViewPullRequestsDialog dialog = View(f); dialog.Show(); await dialog.InitializeAsync(); await entered.Task; f.Field<ListBox>("_pullRequestsList").SelectedIndex = 1;
        await DetailedSettingsUiHost.Until(() => dialog.DiffViewer.CanonicalText == PullRequestTestClient.Patch); Assert.That(captured.IsCancellationRequested, Is.True);
        oldDiff.SetResult(PullRequestTestClient.Patch.Replace("+new", "+STALE")); oldDiscussion.SetResult(new([new(GitHubDiscussionKind.Comment, "stale", DateTimeOffset.Now, "STALE SEVEN")])); await f.IdleAsync();
        Assert.That(dialog.SelectedPullRequest?.Number, Is.EqualTo(8)); Assert.That(dialog.DiffViewer.CanonicalText, Does.Not.Contain("STALE")); f.Click("tabPage2");
        Assert.That(dialog.Discussion.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.Text == "CURRENT EIGHT"), Is.True);
        Assert.That(dialog.Discussion.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.Text == "STALE SEVEN"), Is.False);
    }

    [AvaloniaTest]
    public async Task PostingCapturesTheSelectedPullAndPreservesANewerEditorDraftDuringTheRequest()
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync(); TaskCompletionSource<GitHubPullRequestDiscussionEntry> posted = new(); f.ReleaseOnDispose.Add(() => posted.TrySetResult(new(GitHubDiscussionKind.Comment, "cleanup", DateTimeOffset.Now, "cleanup"))); f.Client.Post = (_, _, _) => posted.Task;
        ViewPullRequestsDialog dialog = View(f); dialog.Show(); await dialog.InitializeAsync(); await f.IdleAsync(); f.Click("tabPage2"); f.Edit("_postCommentText", "first comment"); f.Click("_postComment");
        Assert.That(f.Client.Posted, Is.EqualTo(new[] { (7, "first comment") })); f.Edit("_postCommentText", "newer draft");
        posted.SetResult(new(GitHubDiscussionKind.Comment, "tester", DateTimeOffset.Now, "first comment")); await f.IdleAsync();
        Assert.That(f.Field<TextBox>("_postCommentText").Text, Is.EqualTo("newer draft")); Assert.That(f.Client.Posted.Count, Is.EqualTo(1));
        f.Click("_refreshCommentsBtn"); await f.IdleAsync(); Assert.That(f.Field<TextBox>("_postCommentText").Text, Is.EqualTo("newer draft"));
    }

    [AvaloniaTest]
    public async Task ClosePullRequestUsesTheSelectedIdentityThenRefreshesTheList()
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync(); ViewPullRequestsDialog dialog = View(f); dialog.Show(); await dialog.InitializeAsync(); await f.IdleAsync(); f.Field<ListBox>("_pullRequestsList").SelectedIndex = 1; await f.IdleAsync();
        f.Client.ReadPulls = (_, _) => Task.FromResult(new GitHubHostingPage<GitHubPullRequest>([PullRequestTestClient.Pull()])); f.Click("_closePullRequestBtn"); await f.IdleAsync();
        Assert.That(f.Client.Closed, Is.EqualTo(new[] { 8 })); Assert.That(dialog.SelectedPullRequest?.Number, Is.EqualTo(7)); Assert.That(dialog.IsVisible, Is.True);
    }

    [AvaloniaTest] [TestCase("post")] [TestCase("close")]
    public async Task FailedRemoteMutationPreservesTheSelectedPullAndDraftWithoutRetry(string operation)
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync();
        if (operation == "post") { f.Client.Post = (_, _, _) => throw new GitHubHostingException("Remote operation refused", 403); }
        else { f.Client.Close = (_, _) => throw new GitHubHostingException("Remote operation refused", 403); }
        ViewPullRequestsDialog dialog = View(f); dialog.Show(); await dialog.InitializeAsync(); await f.IdleAsync();
        f.Click("tabPage2"); f.Edit("_postCommentText", "Retain this draft"); f.Click(operation == "post" ? "_postComment" : "_closePullRequestBtn"); await f.IdleAsync();
        Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.SelectedPullRequest?.Number, Is.EqualTo(7));
        Assert.That(f.Field<TextBox>("_postCommentText").Text, Is.EqualTo("Retain this draft")); Assert.That(f.Field<TextBlock>("PullRequestNotice").Text, Does.Contain("Remote operation refused"));
        Assert.That(operation == "post" ? f.Client.Posted.Count : f.Client.Closed.Count, Is.EqualTo(1));
    }

    [AvaloniaTest] [TestCase(false)] [TestCase(true)]
    public async Task FetchAndAddRemoteFetchUseCapturedPullAndCloseOnlyOnCompleteSuccess(bool addRemote)
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync(); List<(int, bool)> calls = []; bool success = false;
        ViewPullRequestsDialog dialog = new(f.Git.Repository, PullRequestUiFixture.Remotes, f.Client, (pull, add, _) => { calls.Add((pull.Number, add)); return Task.FromResult(success); }); f.Window = dialog; dialog.Show(); await dialog.InitializeAsync(); await f.IdleAsync();
        string button = addRemote ? "_addAndFetchBtn" : "_fetchBtn"; f.Click(button); await f.IdleAsync(); Assert.That(dialog.IsVisible, Is.True); Assert.That(calls, Is.EqualTo(new[] { (7, addRemote) }));
        success = true; f.Click(button); await DetailedSettingsUiHost.Until(() => !dialog.IsVisible); Assert.That(calls.Count, Is.EqualTo(2)); Assert.That(f.Client.Closed, Is.Empty);
    }

    [AvaloniaTest]
    public async Task GuestCanReadAndFetchButPostAndCloseAreDisabledAndLinksNeedAnExplicitClick()
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync(); ViewPullRequestsDialog dialog = new(f.Git.Repository, PullRequestUiFixture.Remotes, f.Client, (_, _, _) => Task.FromResult(false), canWrite: false); f.Window = dialog; List<Uri> opened = []; dialog.Discussion.OpenLink = uri => { opened.Add(uri); return Task.CompletedTask; };
        dialog.Show(); await dialog.InitializeAsync(); await f.IdleAsync(); Assert.That(f.Field<Button>("_fetchBtn").IsEnabled, Is.True); Assert.That(f.Field<Button>("_closePullRequestBtn").IsEnabled, Is.False);
        f.Click("tabPage2"); Assert.That(f.Field<TextBox>("_postCommentText").IsEnabled, Is.False); Assert.That(f.Field<Button>("_postComment").IsEnabled, Is.False); Assert.That(opened, Is.Empty);
        Button link = dialog.Discussion.GetVisualDescendants().OfType<Button>().First(button => button.Name == "PullRequestDiscussionLink"); PushProcessTestDriver.Pointer(link); f.Pump(); Assert.That(opened.Count, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task ClosingViewCancelsAnInFlightFetchAndWaitsForItsRealCompletion()
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync(); TaskCompletionSource<bool> fetch = new(); CancellationToken captured = default; f.ReleaseOnDispose.Add(() => fetch.TrySetResult(false));
        ViewPullRequestsDialog dialog = new(f.Git.Repository, PullRequestUiFixture.Remotes, f.Client, (_, _, token) => { captured = token; return fetch.Task; }); f.Window = dialog; dialog.Show(); await dialog.InitializeAsync(); await f.IdleAsync(); f.Click("_fetchBtn"); dialog.Close();
        Assert.That(captured.IsCancellationRequested, Is.True); Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.Completion.IsCompleted, Is.False); fetch.SetResult(false);
        await DetailedSettingsUiHost.Until(() => !dialog.IsVisible); await dialog.Completion; Assert.That(f.Client.Disposed, Is.False);
    }

    [AvaloniaTest]
    public async Task RealHostingClientUsesOnlyTheInjectedHttpTransportForFilesDiffAndDiscussion()
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync();
        PullRequestHttpHandler handler = new(); using GitHubHostingClient client = new(new(), handler: handler);
        ViewPullRequestsDialog dialog = new(f.Git.Repository, PullRequestUiFixture.Remotes, client, (_, _, _) => Task.FromResult(false), canWrite: false);
        f.Window = dialog; dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await f.IdleAsync();
            Assert.That(dialog.SelectedPullRequest?.Number, Is.EqualTo(7)); Assert.That(dialog.DiffViewer.CanonicalText, Is.EqualTo(PullRequestTestClient.Patch));
            f.Click("tabPage2"); Assert.That(dialog.Discussion.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.Text == "HTTP review comment"), Is.True);
            Assert.That(handler.Calls.Any(call => call.Path.EndsWith("/pulls/7", StringComparison.Ordinal) && call.Accept == "application/vnd.github.diff"), Is.True);
            Assert.That(handler.Calls.All(call => call.Method == "GET" && call.Host == "api.github.com"), Is.True);
        }
        finally { await dialog.BeginClose(); if (dialog.IsVisible) { dialog.Close(); } }
    }

    private sealed class PullRequestHttpHandler : HttpMessageHandler
    {
        internal System.Collections.Concurrent.ConcurrentQueue<(string Method, string Host, string Path, string Accept)> Calls { get; } = new();
        private static object Repo(GitHubHostedRepository repository) => new
        {
            id = repository.Id, owner = new { login = repository.Owner }, name = repository.Name, description = repository.Description,
            @private = repository.Private, fork = repository.Fork, default_branch = repository.DefaultBranch,
            clone_url = repository.CloneUrl, ssh_url = repository.SshUrl, html_url = repository.WebUrl
        };
        private static object Pull() => new
        {
            number = 7, title = "HTTP pull request", body = "HTTP description", user = new { login = "tester" }, created_at = "2026-10-19T10:00:00Z", state = "open",
            html_url = "https://github.com/upstream/repo/pull/7", commits = 1,
            @base = new { @ref = "main", sha = new string('a', 40), repo = Repo(PullRequestTestClient.Target) },
            head = new { @ref = "feature", sha = new string('b', 40), repo = Repo(PullRequestTestClient.Mine) }
        };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Uri uri = request.RequestUri!; string accept = request.Headers.Accept.Single().MediaType!;
            Calls.Enqueue((request.Method.Method, uri.Host, uri.AbsolutePath, accept));
            string payload;
            if (uri.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal) && accept == "application/vnd.github.diff") { payload = PullRequestTestClient.Patch; }
            else if (uri.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal)) { payload = JsonSerializer.Serialize(Pull()); }
            else if (uri.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal)) { payload = JsonSerializer.Serialize(new[] { Pull() }); }
            else if (uri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
            { payload = JsonSerializer.Serialize(new[] { new { filename = "literal file.txt", status = "modified", additions = 1, deletions = 1, changes = 2, patch = "@@ -1 +1 @@\n-old\n+new\n" } }); }
            else if (uri.AbsolutePath.EndsWith("/commits", StringComparison.Ordinal))
            { payload = JsonSerializer.Serialize(new[] { new { sha = new string('c', 40), commit = new { message = "HTTP commit", author = new { name = "Author", date = "2026-10-19T11:00:00Z" } }, html_url = "https://github.com/upstream/repo/commit/" + new string('c', 40) } }); }
            else if (uri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal))
            { payload = JsonSerializer.Serialize(new[] { new { body = "HTTP review comment", user = new { login = "reviewer" }, created_at = "2026-10-19T12:00:00Z", html_url = "https://github.com/upstream/repo/pull/7#comment" } }); }
            else { throw new InvalidOperationException("Unexpected request in the private HTTP fixture: " + uri.AbsolutePath); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }

    [AvaloniaTest] [TestCase("body")] [TestCase("comment")]
    public async Task MultilinePullRequestEditorsHaveNoCommitMarkingOrMenuAndDoNotChangeCommitPreferences(string kind)
    {
        await using PullRequestUiFixture f = await PullRequestUiFixture.CreateAsync();
        PreferencesStore desktop = new(); desktop.Save(desktop.Load() with { MarkIllFormedLinesInCommitMsg = true });
        CommitPreferencesStore commit = new(); commit.Save(new() { EnsureCommitMessageSecondLineEmpty = true, LastCommitMessage = "Unrelated commit draft" });
        byte[] desktopBefore = File.ReadAllBytes(desktop.FilePath), commitBefore = File.ReadAllBytes(commit.FilePath);
        PullRequestDialog dialog;
        string editorName;
        if (kind == "body")
        {
            CreatePullRequestDialog create = new(f.Git.Repository, PullRequestUiFixture.Remotes, f.Client);
            dialog = create; f.Window = create; create.Show(); await create.InitializeAsync(); editorName = "_bodyTB";
        }
        else
        {
            ViewPullRequestsDialog view = View(f); dialog = view; view.Show(); await view.InitializeAsync();
            await f.IdleAsync(); f.Click("tabPage2"); editorName = "_postCommentText";
        }
        await f.IdleAsync();
        const string first = "            PR subject";
        string text = first + "\n            ordinary second line\n" + new string('x', 80);
        f.Edit(editorName, text); TextBox editor = f.Field<TextBox>(editorName);
        if (kind == "body") { f.Field<TextBox>("_titleTB").Focus(); }
        else { f.Field<Button>("_refreshCommentsBtn").Focus(); }
        f.Pump();
        TextPresenter presenter = editor.GetVisualDescendants().OfType<TextPresenter>().Single();
        Rect firstSpaces = presenter.TextLayout.HitTestTextRange(0, 12).First();
        Rect secondSpaces = presenter.TextLayout.HitTestTextRange(first.Length + 1, 12).First();
        using (var frame = await AvatarUiTestDriver.Frame(dialog))
        {
            Color Pixel(Rect range)
            {
                Point point = presenter.TranslatePoint(new Point(range.Left + 2, range.Top + 2), dialog)
                    ?? throw new InvalidOperationException("The native editor presenter is not attached.");
                return AvatarUiTestDriver.Pixel(frame, (int)Math.Round(point.X * dialog.RenderScaling), (int)Math.Round(point.Y * dialog.RenderScaling));
            }
            Assert.That(Pixel(secondSpaces), Is.EqualTo(Pixel(firstSpaces)),
                "An ordinary second PR line must have the same native background as the first, even when commit line marking is enabled.");
        }
        Assert.That(editor.GetVisualAncestors().OfType<SpellingEditorView>(), Is.Empty);
        Assert.That(dialog.GetVisualDescendants().OfType<Control>().Any(control => control.Name == "SpellingUnderlines"), Is.False);
        Assert.That(editor.ContextMenu?.Name, Is.Not.EqualTo("CommitSpellingMenu"));
        if (editor.ContextMenu is { } menu)
        {
            Point point = editor.TranslatePoint(new Point(20, 12), dialog) ?? throw new InvalidOperationException("The editor is not attached.");
            dialog.MouseMove(point); dialog.MouseDown(point, MouseButton.Right); dialog.MouseUp(point, MouseButton.Right); f.Pump();
            Assert.That(menu.Items.OfType<MenuItem>().Select(item => item.Name), Does.Not.Contain("MarkIllFormedLines").And.Not.Contain("CommitDictionary"));
            menu.Close();
        }
        Assert.That(editor.Text, Is.EqualTo(text)); Assert.That(f.Client.Created, Is.Empty); Assert.That(f.Client.Posted, Is.Empty);
        await dialog.BeginClose(); if (dialog.IsVisible) { dialog.Close(); }
        Assert.That(File.ReadAllBytes(desktop.FilePath), Is.EqualTo(desktopBefore));
        Assert.That(File.ReadAllBytes(commit.FilePath), Is.EqualTo(commitBefore));
    }

    private static ViewPullRequestsDialog View(PullRequestUiFixture f)
    { ViewPullRequestsDialog dialog = new(f.Git.Repository, PullRequestUiFixture.Remotes, f.Client, (_, _, _) => Task.FromResult(false)); f.Window = dialog; return dialog; }
    private static void AssertCaptions(Window window)
    {
        DetailedSettingsUiHost.Pump(window);
        foreach (Button button in window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible && button.Content is string))
        {
            foreach (TextBlock text in button.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text)))
            {
                Point origin = text.TranslatePoint(default, button)!.Value;
                Assert.That(origin.X, Is.GreaterThanOrEqualTo(-.5), text.Text);
                Assert.That(origin.X + text.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(button.Bounds.Width + .5), text.Text);
            }
        }
        foreach (TextBlock label in window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && text.Name is not null && (text.Name.StartsWith("label", StringComparison.Ordinal) || text.Name.StartsWith("columnHeader", StringComparison.Ordinal))))
        {
            Point origin = label.TranslatePoint(default, window)!.Value;
            Assert.That(origin.X + label.Bounds.Width, Is.LessThanOrEqualTo(window.ClientSize.Width + .5), label.Text);
            Assert.That(label.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False, label.Text);
        }
    }
}

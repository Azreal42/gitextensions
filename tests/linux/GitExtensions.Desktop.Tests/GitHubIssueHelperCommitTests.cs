using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitUIPluginInterfaces.BuildServerIntegration;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GitHubIssueHelperCommitTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task BackgroundIssuesKeepEditorAndOpenMenuStableThenExplicitChoiceAndUndoUseTheExistingCommand(string language, bool dark)
    {
        await using Scope f = await Scope.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        using GateHandler handler = new([Issue(7)]);
        CommitDialog commit = await f.OpenAsync(handler, dark: dark); await Until(commit, () => handler.Started.IsCompleted);
        string registered = "external-" + Guid.NewGuid().ToString("N"); CommitTemplateRegistry.Register(registered, () => "external body");
        try
        {
            Assert.That(commit.IsBusy, Is.False, "HTTP loading cannot enter the Commit busy/disable-input pipeline.");
            Edit(commit, "User draft λ"); Press(commit, PhysicalKey.ArrowLeft);
            TextBox message = Field<TextBox>(commit, "Message");
            int caret = message.CaretIndex, start = message.SelectionStart, end = message.SelectionEnd;
            MenuItem menu = OpenTemplates(commit);
            Assert.That(Issues(menu), Is.Empty);
            object? selected = menu.SelectedItem; object? focus = commit.FocusManager?.GetFocusedElement();
            Assert.That(focus, Is.Not.Null);
            string before = await Materials(f.Root);
            handler.Release(); await Until(commit, () => commit.GitHubIssueCompletion.IsCompleted); await commit.GitHubIssueCompletion;
            Assert.That(commit.IsBusy, Is.False); Assert.That(message.Text, Is.EqualTo("User draft λ"));
            Assert.That(message.CaretIndex == caret && message.SelectionStart == start && message.SelectionEnd == end, Is.True);
            Assert.That(menu.IsSubMenuOpen, Is.True); Assert.That(menu.SelectedItem, Is.SameAs(selected));
            Assert.That(commit.FocusManager?.GetFocusedElement(), Is.SameAs(focus), "The arrival of issues cannot steal editor/menu focus.");
            Assert.That(Issues(menu), Is.Empty, "A menu already open keeps its snapshot until the next opening.");
            Assert.That(await Materials(f.Root), Is.EqualTo(before));
            Press(commit, PhysicalKey.Escape); menu = OpenTemplates(commit);
            MenuItem issue = Issues(menu).Single();
            Assert.That(((CommitMessageTemplate)issue.Tag!).Name, Is.EqualTo("7: Fix λ"));
            Assert.That(issue.GetVisualDescendants().OfType<Image>().Any(image => image.Source is not null && image.Width == 16), Is.True);
            Assert.That(menu.Items.OfType<MenuItem>().Any(item => item.Name == "RegisteredCommitTemplate" && ((CommitMessageTemplate)item.Tag!).Name == registered), Is.True);
            Assert.That(menu.Items.OfType<MenuItem>().Any(item => item.Name == "UserCommitTemplate"), Is.True);
            Assert.That(menu.Items.OfType<MenuItem>().Any(item => item.Name == "ConventionalCommits"), Is.True);
            Capture(TopLevel.GetTopLevel(issue)!, "github-issue-commit-menu-" + language + (dark ? "-dark.png" : "-light.png"));
            PushProcessTestDriver.Pointer(issue); await Until(commit, () => !commit.IsBusy);
            Assert.That(message.Text, Is.EqualTo("\nFixes #7 : Fix λ\n\nliteral <body>\nsecond\n"));
            Assert.That(message.IsKeyboardFocusWithin, Is.True); Press(commit, PhysicalKey.Z, RawInputModifiers.Control);
            Assert.That(message.Text, Is.EqualTo("User draft λ")); Assert.That(await Materials(f.Root), Is.EqualTo(before));
        }
        finally { handler.Release(); CommitTemplateRegistry.Unregister(registered); }
    }

    [AvaloniaTest]
    [TestCase("disabled")] [TestCase("other-host")] [TestCase("guest")]
    public async Task DisabledOtherHostAndGuestAdmissionNeverSendAnIssueRequest(string condition)
    {
        await using Scope f = await Scope.CreateAsync(enabled: condition != "disabled", authenticated: condition != "guest",
            remote: condition == "other-host" ? "https://outside.example.invalid/octo/repo.git" : "git@github.com:octo/repo.git");
        using ImmediateHandler handler = new([]); CommitDialog commit = await f.OpenAsync(handler);
        await Until(commit, () => commit.GitHubIssueCompletion.IsCompleted); await commit.GitHubIssueCompletion;
        Edit(commit, "Keep this draft"); string before = await Materials(f.Root); MenuItem menu = OpenTemplates(commit);
        Assert.That(handler.Calls, Is.Zero); Assert.That(Field<TextBox>(commit, "Message").Text, Is.EqualTo("Keep this draft"));
        if (condition == "guest")
        {
            MenuItem notice = menu.Items.OfType<MenuItem>().Single(item => item.Name == "GitHubIssueNotice");
            Assert.That(((CommitMessageTemplate)notice.Tag!).Text, Is.Empty); Assert.That(notice.IsEnabled, Is.True);
            Assert.That(((CommitMessageTemplate)notice.Tag!).Name, Does.Contain("PAT"));
            PushProcessTestDriver.Pointer(notice); await Until(commit, () => !commit.IsBusy);
            Assert.That(Field<TextBox>(commit, "Message").Text, Is.Empty, "Only the explicit empty-template click follows Windows notice semantics.");
            Press(commit, PhysicalKey.Z, RawInputModifiers.Control); Assert.That(Field<TextBox>(commit, "Message").Text, Is.EqualTo("Keep this draft"));
        }
        else { Assert.That(Issues(menu), Is.Empty); Assert.That(menu.Items.OfType<MenuItem>().Any(item => item.Name == "GitHubIssueNotice"), Is.False); }
        Assert.That(await Materials(f.Root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task EmptyAndTruncatedFeedsAreVisibleWithoutChangingTheDraft(bool truncated)
    {
        await using Scope f = await Scope.CreateAsync(remote: "ssh://git@git.example.invalid:2222/octo/repo.git", enterprise: true);
        using ImmediateHandler handler = new(truncated ? [Issue(7)] : [], truncated);
        CommitDialog commit = await f.OpenAsync(handler, pages: 1);
        Edit(commit, "Draft remains literal"); await Until(commit, () => commit.GitHubIssueCompletion.IsCompleted); await commit.GitHubIssueCompletion;
        MenuItem menu = OpenTemplates(commit); MenuItem notice = menu.Items.OfType<MenuItem>().Single(item => item.Name == "GitHubIssueNotice");
        Assert.That(handler.Calls, Is.EqualTo(1)); Assert.That(handler.Paths.Single(), Is.EqualTo("/api/v3/issues"));
        Assert.That(((CommitMessageTemplate)notice.Tag!).Name, truncated ? Does.Contain("response limit") : Does.Contain("No assigned"));
        Assert.That(Issues(menu).Count(), Is.EqualTo(truncated ? 1 : 0));
        Assert.That(Field<TextBox>(commit, "Message").Text, Is.EqualTo("Draft remains literal"));
        PushProcessTestDriver.Pointer(notice); await Until(commit, () => !commit.IsBusy); Assert.That(Field<TextBox>(commit, "Message").Text, Is.Empty);
        Press(commit, PhysicalKey.Z, RawInputModifiers.Control); Assert.That(Field<TextBox>(commit, "Message").Text, Is.EqualTo("Draft remains literal"));
    }

    [AvaloniaTest]
    [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
    public async Task FirstDirectOrOwnerCloseCancelsAndDrainsTheActualHttpReadIncludingWhileGitIsBusy(bool busy, bool ownerClose)
    {
        if (busy && !OperatingSystem.IsLinux()) { Assert.Ignore("The real Git gate requires Linux shell/procfs."); return; }
        await using Scope f = await Scope.CreateAsync(); using GateHandler handler = new([Issue(7)]);
        NativeGate? git = busy ? await NativeGate.CreateAsync(f.Root) : null;
        CommitDialog commit = await f.OpenAsync(handler, repository: git?.Repository);
        Window owner = f.Owner(commit); await Until(owner, () => handler.Started.IsCompleted);
        Edit(commit, "Close keeps this draft"); Task? refresh = null; (int Pid, string Start)? identity = null;
        try
        {
            if (git is not null)
            {
                await Until(owner, () => commit.SpellingCloseCompletion.IsCompleted); await git.ArmAsync(); refresh = commit.RefreshAsync();
                await Until(owner, () => git.Identity is not null); identity = git.Identity;
                Assert.That(commit.IsBusy, Is.True); Assert.That(git.Arguments, Does.Contain("status"));
            }
            string profile = HashFile(f.Hosting.FilePath);
            if (ownerClose) { owner.Close(); } else { commit.Close(); }
            await Until(owner, () => handler.Canceled.IsCompleted);
            Assert.That(commit.IsVisible && owner.IsVisible, Is.True, "The actual HTTP task is still held and the modal must veto premature closure.");
            Assert.That(commit.GitHubIssueCompletion.IsCompleted, Is.False); Assert.That(handler.Disposed, Is.False);
            if (refresh is not null)
            {
                await Until(owner, () => refresh.IsCompleted); await refresh;
                Assert.That(Alive(identity!.Value), Is.False, "The real Git PID/start identity, including any zombie, has retired.");
                Assert.That(commit.IsVisible, Is.True, "The replayed close must still join the retained helper task.");
            }
            Assert.That(Field<TextBox>(commit, "Message").Text, Is.EqualTo("Close keeps this draft"));
            handler.Release();
            await Until(owner, () => !commit.IsVisible && commit.GitHubIssueCompletion.IsCompleted && (!ownerClose || !owner.IsVisible));
            await commit.GitHubIssueCompletion; await commit.SpellingCloseCompletion;
            Assert.That(handler.Finished.IsCompleted && handler.Disposed, Is.True); Assert.That(owner.IsVisible, Is.EqualTo(!ownerClose));
            Assert.That(HashFile(f.Hosting.FilePath), Is.EqualTo(profile)); Assert.That(commit.HasCommitted, Is.False);
            Assert.That(PrivateIssuesOnClosedMenu(commit), Is.Zero, "Closed clears private registrations; an ignored-cancellation result cannot publish.");
        }
        finally { handler.Release(); if (git is not null) { await git.ReleaseAsync(); } }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task SimultaneousCommitWindowsKeepTheirOwnRepositoryIssueSnapshotsAndCancelIndependently(bool sameRepository)
    {
        await using Scope first = await Scope.CreateAsync(); await using Scope second = await Scope.CreateAsync(remote: "https://github.com/octo/fork.git");
        using ImmediateHandler a = new([Issue(7)]), b = new([Issue(8, repo: sameRepository ? "repo" : "fork")]);
        CommitDialog left = await first.OpenAsync(a);
        CommitDialog right = await (sameRepository ? first : second).OpenAsync(b);
        await Until(right, () => left.GitHubIssueCompletion.IsCompleted && right.GitHubIssueCompletion.IsCompleted);
        MenuItem menuA = OpenTemplates(left); Assert.That(((CommitMessageTemplate)Issues(menuA).Single().Tag!).Name, Is.EqualTo("7: Fix λ")); Press(left, PhysicalKey.Escape);
        MenuItem menuB = OpenTemplates(right); Assert.That(((CommitMessageTemplate)Issues(menuB).Single().Tag!).Name, Is.EqualTo("8: Fix λ"));
        object? selected = menuB.SelectedItem; object? focus = right.FocusManager?.GetFocusedElement();
        left.Close(); await Until(right, () => !left.IsVisible);
        Assert.That(right.IsVisible && menuB.IsSubMenuOpen, Is.True); Assert.That(menuB.SelectedItem, Is.SameAs(selected));
        Assert.That(right.FocusManager?.GetFocusedElement(), Is.SameAs(focus));
        Assert.That(((CommitMessageTemplate)Issues(menuB).Single().Tag!).Name, Is.EqualTo("8: Fix λ"));
        Press(right, PhysicalKey.Escape); menuB = OpenTemplates(right);
        Assert.That(((CommitMessageTemplate)Issues(menuB).Single().Tag!).Name, Is.EqualTo("8: Fix λ"));
    }

    [AvaloniaTest]
    public async Task DraftSaveVetoRevokesCloseWithoutEscapingOrPublishingTheCanceledHelperThenRetryCloses()
    {
        await using Scope f = await Scope.CreateAsync(); using GateHandler handler = new([Issue(7)]);
        CommitDialog commit = await f.OpenAsync(handler); Window owner = f.Owner(commit);
        await Until(owner, () => handler.Started.IsCompleted); Edit(commit, "Unsaved veto draft");
        string blocked = f.Root.PathInRepository(".git/COMMITMESSAGE"); Directory.CreateDirectory(blocked);
        try
        {
            commit.Close(); await Until(owner, () => handler.Canceled.IsCompleted);
            Assert.That(commit.IsVisible && owner.IsVisible, Is.True); Assert.That(commit.LastError, Is.Not.Null);
            Assert.That(commit.GitHubIssueCompletion.IsCompleted, Is.False); Assert.That(handler.Disposed, Is.False);
            MenuItem menu = OpenTemplates(commit); Assert.That(Issues(menu), Is.Empty);
            object? focus = commit.FocusManager?.GetFocusedElement(); object? selected = menu.SelectedItem;
            handler.Release(); await Until(owner, () => commit.GitHubIssueCompletion.IsCompleted); await commit.GitHubIssueCompletion;
            Assert.That(handler.Finished.IsCompleted && handler.Disposed, Is.True); Assert.That(commit.IsBusy, Is.False);
            Assert.That(commit.IsVisible && menu.IsSubMenuOpen, Is.True); Assert.That(menu.SelectedItem, Is.SameAs(selected));
            Assert.That(commit.FocusManager?.GetFocusedElement(), Is.SameAs(focus)); Assert.That(Field<TextBox>(commit, "Message").Text, Is.EqualTo("Unsaved veto draft"));
            Press(commit, PhysicalKey.Escape); menu = OpenTemplates(commit); Assert.That(Issues(menu), Is.Empty);
            Assert.That(menu.Items.OfType<MenuItem>().Any(item => item.Name == "GitHubIssueNotice"), Is.False, "A canceled load is not misreported as empty/error after a failed close.");
            Press(commit, PhysicalKey.Escape); Directory.Delete(blocked);
            commit.Close(); await Until(owner, () => !commit.IsVisible); Assert.That(owner.IsVisible, Is.True);
        }
        finally { handler.Release(); if (Directory.Exists(blocked)) { Directory.Delete(blocked); } }
    }

    private static object Issue(int number, string repo = "repo") => new
    { number, title = "Fix λ", body = "literal <body>\nsecond", updated_at = "2020-01-03T00:00:00Z", repository = new { owner = new { login = "octo" }, name = repo } };
    private static IEnumerable<MenuItem> Issues(MenuItem menu) => menu.Items.OfType<MenuItem>().Where(item => item.Name == "GitHubIssueTemplate");
    private static MenuItem OpenTemplates(CommitDialog commit)
        => CommitToolbarTestDriver.OpenMenu(commit, Field<Menu>(commit, "CommitToolbar").Items.OfType<MenuItem>().Single(item => item.Name == "commitTemplatesToolStripMenuItem"));
    private static int PrivateIssuesOnClosedMenu(CommitDialog commit)
    {
        // The menu snapshot may still contain formerly rendered items; the per-window registration list must be empty.
        object list = typeof(CommitDialog).GetField("_githubIssueTemplates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(commit)!;
        return ((System.Collections.IEnumerable)list).Cast<object>().Count();
    }
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Edit(Window root, string text)
    { PushProcessTestDriver.Pointer(Field<TextBox>(root, "Message")); Press(root, PhysicalKey.A, RawInputModifiers.Control); root.KeyTextInput(text); Pump(root); Assert.That(Field<TextBox>(root, "Message").Text, Is.EqualTo(text)); }
    private static void Press(Window root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { root.KeyPressQwerty(key, modifiers); if (root.IsVisible) { root.KeyReleaseQwerty(key, modifiers); } Pump(root); }
    private static void Pump(TopLevel root)
    { Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } foreach (Window child in (root as Window)?.OwnedWindows.ToArray() ?? []) { Pump(child); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(TopLevel root, Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Pump(root); await Task.Delay(10); }
        Pump(root); if (!condition() && root.IsVisible) { Capture(root, "github-issue-timeout-" + Guid.NewGuid().ToString("N") + ".png"); }
        Assert.That(condition(), Is.True, "Actual issue-helper task/window condition timed out.");
    }
    private static void Capture(TopLevel root, string name)
    {
        using var frame = root.GetLastRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private static string HashFile(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "<absent>";
    private static async Task<string> Materials(GitFixture root)
        => (await root.GitAsync("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput
            + string.Join('\n', new[] { ".git/HEAD", ".git/index", ".git/config", "file.txt" }.Select(path => path + ":" + HashFile(root.PathInRepository(path))))
            + HashFile(new GitHubHostingPreferencesStore(root.Output("config")).FilePath);
    private static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string? StartIdentity(int pid)
    { try { string stat = File.ReadAllText("/proc/" + pid + "/stat"); return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19]; } catch (IOException) { return null; } }
    private static bool Alive((int Pid, string Start) identity) => StartIdentity(identity.Pid) == identity.Start;

    private sealed class Scope(GitFixture root) : IAsyncDisposable
    {
        private readonly List<(Window Owner, CommitDialog Dialog)> _windows = [];
        internal GitFixture Root => root;
        internal GitHubHostingPreferencesStore Hosting { get; } = new(root.Output("config"));
        internal static async Task<Scope> CreateAsync(bool enabled = true, bool authenticated = true, string remote = "git@github.com:octo/repo.git", bool enterprise = false)
        {
            GitFixture root = await GitFixture.CreateAsync();
            try
            {
                root.SetEnvironment("GIT_OPTIONAL_LOCKS", "0"); await root.GitAsync("remote", "add", "origin", remote);
                await File.WriteAllTextAsync(root.File, "staged\n"); await root.Repository.StageAllAsync(); await File.WriteAllTextAsync(root.File, "working\n");
                Scope scope = new(root); GitHubHostingSettings settings = enterprise ? new("https://git.example.invalid", "https://git.example.invalid/api/v3/") : new();
                BuildServerCredentials credentials = new() { BuildServerCredentialsType = authenticated ? BuildServerCredentialsType.BearerToken : BuildServerCredentialsType.Guest,
                    BearerToken = authenticated ? Guid.NewGuid().ToString("N") : null };
                Dictionary<string, string?> values = new(settings.WithCredentials(new Dictionary<string, string?>(), credentials), StringComparer.Ordinal)
                { [GitHubIssueHelperSettings.EnabledKey] = enabled ? "true" : "false", ["future"] = "retained" };
                scope.Hosting.SaveChanges(scope.Hosting.Load(), values);
                new CommitPreferencesStore(root.Output("config")).Save(new() { SelectStagedOnMessageFocus = false, CloseAfterEachCommit = false, CloseWhenAllChangesCommitted = false });
                CommitMessagePreferencesStore messages = new(root.Output("config")); var baseline = messages.Load(); messages.SaveChanges(baseline, baseline.WithTemplate(0, new("User template", "user body")));
                return scope;
            }
            catch { root.Dispose(); throw; }
        }
        internal async Task<CommitDialog> OpenAsync(HttpMessageHandler handler, bool dark = false, int pages = 10, GitRepository? repository = null)
        {
            Window owner = new() { Width = 1250, Height = 900 }; owner.Show();
            CommitDialog dialog = new(repository ?? root.Repository, new CommitPreferencesStore(root.Output("config"))) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            dialog.GitHubIssueClientFactory = (settings, credentials) => new GitHubHostingClient(settings, credentials.ToDraft(), handler) { MaxPages = pages };
            _windows.Add((owner, dialog)); _ = dialog.ShowDialog(owner);
            await dialog.InitializeAsync(); await Until(owner, () => !dialog.IsBusy); Assert.That(dialog.LastError, Is.Null); Pump(owner); return dialog;
        }
        internal Window Owner(CommitDialog dialog) => _windows.Single(item => ReferenceEquals(item.Dialog, dialog)).Owner;
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var (owner, dialog) in _windows.AsEnumerable().Reverse())
                {
                    foreach (Window child in dialog.OwnedWindows.ToArray()) { child.Close(); } dialog.Close();
                    await Until(owner, () => !dialog.IsVisible && dialog.GitHubIssueCompletion.IsCompleted && !dialog.IsBusy);
                    await dialog.GitHubIssueCompletion; await dialog.SpellingCloseCompletion; owner.Close(); Pump(owner);
                }
            }
            finally { root.Dispose(); }
        }
    }

    private sealed class ImmediateHandler(object[] issues, bool truncated = false) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Paths.Add(request.RequestUri!.AbsolutePath); Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
            HttpResponseMessage response = Json(issues);
            if (truncated) { response.Headers.TryAddWithoutValidation("Link", "<https://git.example.invalid/api/v3/issues?page=2>; rel=\"next\""); }
            return Task.FromResult(response);
        }
    }
    private sealed class GateHandler(object[] issues) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously), _canceled = new(TaskCreationOptions.RunContinuationsAsynchronously),
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously), _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Started => _started.Task;
        internal Task Canceled => _canceled.Task;
        internal Task Finished => _finished.Task;
        internal bool Disposed { get; private set; }
        internal void Release() => _release.TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get)); _started.TrySetResult(); using var canceled = token.Register(() => _canceled.TrySetResult());
            // Deliberately retain the actual transport task after cancellation: the modal must await real completion.
            try { await _release.Task; return Json(issues); } finally { _finished.TrySetResult(); }
        }
        protected override void Dispose(bool disposing) { Disposed = true; if (disposing) { Release(); } base.Dispose(disposing); }
    }
    private static HttpResponseMessage Json(object[] issues) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(issues), Encoding.UTF8, "application/json") };

    private sealed class NativeGate(GitRepository repository, string directory)
    {
        internal GitRepository Repository { get; } = repository;
        internal string Arguments => File.ReadAllText(Path.Combine(directory, "native.args"));
        internal (int Pid, string Start)? Identity
        { get { string file = Path.Combine(directory, "native.pid"); return File.Exists(file) && int.TryParse(File.ReadAllText(file), out int pid) && StartIdentity(pid) is string start ? (pid, start) : null; } }
        internal Task ArmAsync() => File.WriteAllTextAsync(Path.Combine(directory, "armed"), "armed\n");
        internal Task ReleaseAsync() => File.WriteAllTextAsync(Path.Combine(directory, "release"), "release\n");
        internal static async Task<NativeGate> CreateAsync(GitFixture root)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The real native gate requires Linux."); }
            string directory = root.Output("issue helper git gate " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); string path = Path.Combine(directory, "git");
            await File.WriteAllTextAsync(path, "#!/bin/sh\nblocked=no\nfor arg in \"$@\"; do [ \"$arg\" != status ] || blocked=yes; done\nif [ \"$blocked\" = yes ] && [ -f " + Quote(Path.Combine(directory, "armed"))
                + " ]; then printf '%s\\n' \"$@\" > " + Quote(Path.Combine(directory, "native.args")) + "; printf '%s' \"$$\" > " + Quote(Path.Combine(directory, "native.pid"))
                + "; while [ ! -f " + Quote(Path.Combine(directory, "release")) + " ]; do sleep .02; done; fi\nexec /usr/bin/git \"$@\"\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new(new GitRepository(root.Repository.Path, new GitRunner(path)), directory);
        }
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

/// <summary>The real MainWindow callback, real Git fetch over a private dumb-HTTP server, no GitHub writes.</summary>
[NonParallelizable]
public sealed class RootPullRequestWorkflowTests
{
    [AvaloniaTest]
    public async Task RealCallbackFetchesOnlyThePrBranchWithoutCheckoutOrRemoteCreation()
    {
        await using WorkflowFixture f = await WorkflowFixture.CreateAsync();
        string originalHead = await f.Host.Git.Repository.ResolveRevisionAsync("HEAD");
        byte[] index = File.ReadAllBytes(f.Host.Git.PathInRepository(".git/index"));
        f.Host.Click("_fetchBtn"); await f.CompletedAsync();
        Assert.That(await f.Operation!, Is.True); Assert.That(f.View.IsVisible, Is.False);
        Assert.That(await f.Host.Git.Repository.ResolveRevisionAsync("refs/heads/" + f.Pull.FetchBranch), Is.EqualTo(f.SourceTip));
        Assert.That(await f.Host.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(originalHead));
        Assert.That((await f.Host.Git.GitAsync("symbolic-ref", "--short", "HEAD")).StandardOutput.Trim(), Is.EqualTo("main"));
        Assert.That(File.ReadAllBytes(f.Host.Git.PathInRepository(".git/index")), Is.EqualTo(index));
        Assert.That((await f.Host.Git.GitAsync("remote")).StandardOutput.Trim(), Is.Empty);
        Assert.That(f.Server.Requests, Is.Not.Empty); Assert.That(f.Server.Requests.All(path => path.StartsWith("/contributor/repo.git/", StringComparison.Ordinal)), Is.True);
    }

    [AvaloniaTest]
    public async Task RealAddRemoteCallbackFetchesAndChecksOutTheOwnerBranch()
    {
        await using WorkflowFixture f = await WorkflowFixture.CreateAsync();
        f.Host.Click("_addAndFetchBtn"); await f.CompletedAsync();
        Assert.That(await f.Operation!, Is.True); Assert.That(f.View.IsVisible, Is.False);
        Assert.That((await f.Host.Git.GitAsync("symbolic-ref", "--short", "HEAD")).StandardOutput.Trim(), Is.EqualTo("contributor/feature"));
        Assert.That(await f.Host.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.SourceTip));
        Assert.That((await f.Host.Git.GitAsync("config", "--get-all", "remote.contributor.url")).StandardOutput.Trim(), Is.EqualTo(f.Source.CloneUrl));
        Assert.That(f.View.OwnedWindows.OfType<CheckoutBranchDialog>().Any(child => child.IsVisible), Is.False);
    }

    [AvaloniaTest]
    public async Task BeforeCheckoutVetoKeepsTheRealFetchButDoesNotReopenThePickerOrClaimSuccess()
    {
        await using WorkflowFixture f = await WorkflowFixture.CreateAsync();
        string marker = f.Host.Git.Output("before-checkout-veto"); string script = f.Host.Git.Output("checkout-veto.sh");
        File.WriteAllText(script, "#!/bin/sh\nprintf veto > '" + marker.Replace("'", "'\\''", StringComparison.Ordinal) + "'\nexit 9\n");
        new ScriptsPreferencesStore().Save([new() { Name = "PR checkout veto", Command = "/bin/sh", Arguments = "\"" + script + "\"", OnEvent = ScriptEvent.BeforeCheckout }]);
        string originalHead = await f.Host.Git.Repository.ResolveRevisionAsync("HEAD");
        f.Host.Click("_addAndFetchBtn");
        GitProcessDialog? failedScript = null;
        await DetailedSettingsUiHost.Until(() =>
        {
            failedScript = DescendantWindows(f.View).OfType<GitProcessDialog>().FirstOrDefault(process => process.IsVisible
                && !process.IsRunning && process.CommandDescription.StartsWith("PR checkout veto:", StringComparison.Ordinal));
            return failedScript is not null;
        });
        GitProcessDialog scriptOutput = failedScript ?? throw new InvalidOperationException("The failed checkout script output was not shown.");
        Assert.That(scriptOutput.Result?.ExitCode, Is.EqualTo(9), "Observe the real script failure before acknowledging its output.");
        Assert.That(scriptOutput.Outcome?.WasCancelled, Is.False);
        Button acknowledge = PushProcessTestDriver.Field<Button>(scriptOutput, "Ok");
        Assert.That(acknowledge.IsEnabled, Is.True);
        Assert.That(PushProcessTestDriver.Field<Button>(scriptOutput, "Abort").IsEnabled, Is.False, "The process has exited; acknowledge its result rather than aborting it.");
        PushProcessTestDriver.Pointer(acknowledge);
        await f.CompletedAsync();
        Assert.That(await f.Operation!, Is.False); Assert.That(f.View.IsVisible, Is.True); Assert.That(File.ReadAllText(marker), Is.EqualTo("veto"));
        Assert.That(await f.Host.Git.Repository.ResolveRevisionAsync("refs/heads/contributor/feature"), Is.EqualTo(f.SourceTip));
        Assert.That(await f.Host.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(originalHead));
        Assert.That(f.View.OwnedWindows.OfType<CheckoutBranchDialog>().Any(child => child.IsVisible), Is.False,
            "A direct checkout veto is a completed decision, not a reason to open another picker.");
    }

    [AvaloniaTest]
    public async Task ClosingThePrOwnerDuringCheckoutScriptCancelsAndDrainsTheActualNestedWorkflow()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("This process-drain assertion uses Linux procfs."); }
        await using WorkflowFixture f = await WorkflowFixture.CreateAsync();
        string ready = f.Host.Git.Output("checkout-script-ready"), pidFile = f.Host.Git.Output("checkout-script-pid");
        string script = f.Host.Git.Output("checkout-block.sh");
        File.WriteAllText(script, "#!/bin/sh\nprintf '%s' $$ > '" + pidFile.Replace("'", "'\\''", StringComparison.Ordinal) + "'\nprintf ready > '" + ready.Replace("'", "'\\''", StringComparison.Ordinal) + "'\nsleep 30\n");
        new ScriptsPreferencesStore().Save([new() { Name = "PR blocked checkout", Command = "/bin/sh", Arguments = "\"" + script + "\"", OnEvent = ScriptEvent.BeforeCheckout }]);
        f.Host.Click("_addAndFetchBtn"); await DetailedSettingsUiHost.Until(() => File.Exists(ready)); int pid = int.Parse(File.ReadAllText(pidFile));
        f.View.Close(); Assert.That(f.View.IsVisible, Is.True, "The owner waits for its running callback and nested checkout work.");
        await DetailedSettingsUiHost.Until(() => !f.View.IsVisible && f.View.Completion.IsCompleted); await f.View.Completion;
        Assert.That(Directory.Exists("/proc/" + pid), Is.False); Assert.That(f.View.OwnedWindows.Any(child => child.IsVisible), Is.False);
        Assert.That(await f.Host.Git.Repository.ResolveRevisionAsync("refs/heads/contributor/feature"), Is.EqualTo(f.SourceTip), "Closing does not erase a completed fetch.");
        Assert.That((await f.Host.Git.GitAsync("symbolic-ref", "--short", "HEAD")).StandardOutput.Trim(), Is.EqualTo("main"));
    }

    private static IEnumerable<Window> DescendantWindows(Window owner)
    {
        foreach (Window child in owner.OwnedWindows.ToArray())
        { yield return child; foreach (Window nested in DescendantWindows(child)) { yield return nested; } }
    }

    private sealed class WorkflowFixture : IAsyncDisposable
    {
        internal PullRequestUiFixture Host { get; }
        internal MainWindow Main { get; } = new(); // callback uses its real settings/output/submodule workflow; no startup UI needed
        internal ViewPullRequestsDialog View { get; private set; } = null!;
        internal PrivateGitHttpServer Server { get; }
        internal GitHubHostedRepository Source { get; }
        internal GitHubPullRequest Pull { get; }
        internal string SourceTip { get; }
        internal Task<bool>? Operation { get; private set; }
        private WorkflowFixture(PullRequestUiFixture host, PrivateGitHttpServer server, string tip)
        {
            Host = host; Server = server; SourceTip = tip;
            Source = PullRequestTestClient.Mine with { Owner = "contributor", CloneUrl = server.RootUrl + "contributor/repo.git", SshUrl = "", WebUrl = server.RootUrl + "contributor/repo" };
            GitHubHostedRepository target = PullRequestTestClient.Target with { CloneUrl = server.RootUrl + "upstream/repo.git", WebUrl = server.RootUrl + "upstream/repo" };
            Pull = PullRequestTestClient.Pull() with { Author = "contributor", Base = new(target, "main", PullRequestTestClient.Pull().Base.Sha), Head = new(Source, "feature", tip) };
            Host.Client.ReadPulls = (_, _) => Task.FromResult(new GitHubHostingPage<GitHubPullRequest>([Pull]));
            View = new(host.Git.Repository, [new("upstream", target, false)], host.Client, (pull, add, token) =>
            {
                GitHubHostingSettings settings = new(server.RootUrl.TrimEnd('/'), server.RootUrl + "api/v3");
                Operation = Invoke(Main, View, Host.Git.Repository, settings, pull, add, token); return Operation;
            });
            Host.Window = View;
        }
        internal static async Task<WorkflowFixture> CreateAsync()
        {
            PullRequestUiFixture host = await PullRequestUiFixture.CreateAsync(); PrivateGitHttpServer? server = null;
            try
            {
                host.Git.SetEnvironment("NO_PROXY", "127.0.0.1,localhost"); host.Git.SetEnvironment("no_proxy", "127.0.0.1,localhost");
                new ProcessDialogPreferencesStore().Save(new(true)); new ReferencePreferencesStore().Save(new());
                string bare = host.Git.Output("pr-http-source.git");
                await host.Git.GitAsync("clone", "--bare", host.Git.Repository.Path, bare);
                GitRunner git = new();
                GitResult created = await git.RunAsync(bare, ["-c", "user.name=PR fixture", "-c", "user.email=pr@example.invalid", "commit-tree", "HEAD^{tree}", "-p", "HEAD", "-m", "PR source tip"]);
                Assert.That(created.Success, Is.True, created.StandardError); string tip = created.StandardOutput.Trim();
                GitResult updated = await git.RunAsync(bare, ["update-ref", "refs/heads/feature", tip]); Assert.That(updated.Success, Is.True);
                GitResult info = await git.RunAsync(bare, ["update-server-info"]); Assert.That(info.Success, Is.True);
                server = new(bare); WorkflowFixture f = new(host, server, tip);
                f.View.Show(); await f.View.InitializeAsync(); await f.Host.IdleAsync(); return f;
            }
            catch { if (server is not null) { await server.DisposeAsync(); } await host.DisposeAsync(); throw; }
        }
        private static Task<bool> Invoke(MainWindow main, Window owner, GitRepository repository, GitHubHostingSettings settings, GitHubPullRequest pull, bool addRemote, CancellationToken token)
            => (Task<bool>)typeof(MainWindow).GetMethod("FetchPullRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(main, [owner, repository, settings, pull, addRemote, token])!;
        internal async Task CompletedAsync()
        { await DetailedSettingsUiHost.Until(() => Operation is { IsCompleted: true } && !View.IsBusy); if (Operation is not null) { await Operation; } Host.Pump(); }
        public async ValueTask DisposeAsync()
        {
            try { await Host.DisposeAsync(); Main.Close(); await Main.WindowWorkCompletion; }
            finally { await Server.DisposeAsync(); }
        }
    }

    /// <summary>Only serves this fixture's bare repository at loopback. Git uses dumb HTTP, with no external process server.</summary>
    private sealed class PrivateGitHttpServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _requests = [];
        private readonly string _repository;
        private readonly Task _loop;
        internal string RootUrl { get; }
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal PrivateGitHttpServer(string repository)
        {
            _repository = Path.GetFullPath(repository);
            using TcpListener reserve = new(IPAddress.Loopback, 0); reserve.Start(); int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
            RootUrl = "http://127.0.0.1:" + port + "/"; _listener.Prefixes.Add(RootUrl); _listener.Start(); _loop = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                { HttpListenerContext context = await _listener.GetContextAsync().WaitAsync(_stop.Token); Task work = ServeAsync(context); _requests.Add(work); }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (HttpListenerException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }
        private async Task ServeAsync(HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url!.AbsolutePath; Requests.Enqueue(path);
                const string prefix = "/contributor/repo.git/";
                if (context.Request.HttpMethod != "GET" || !path.StartsWith(prefix, StringComparison.Ordinal)) { context.Response.StatusCode = 404; return; }
                string relative = Uri.UnescapeDataString(path[prefix.Length..]);
                string file = Path.GetFullPath(Path.Combine(_repository, relative));
                if (!file.StartsWith(_repository + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file)) { context.Response.StatusCode = 404; return; }
                byte[] data = await File.ReadAllBytesAsync(file, _stop.Token); context.Response.StatusCode = 200;
                context.Response.ContentType = path.EndsWith("/info/refs", StringComparison.Ordinal) ? "text/plain" : "application/octet-stream";
                context.Response.ContentLength64 = data.Length; await context.Response.OutputStream.WriteAsync(data, _stop.Token);
            }
            catch (IOException) { }
            catch (HttpListenerException) { /* A cancelling Git client can abort a private HTTP response. */ }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { context.Response.Close(); }
        }
        public async ValueTask DisposeAsync()
        { _stop.Cancel(); _listener.Stop(); try { await _loop; await Task.WhenAll(_requests.ToArray()); } finally { _listener.Close(); _stop.Dispose(); } }
    }
}

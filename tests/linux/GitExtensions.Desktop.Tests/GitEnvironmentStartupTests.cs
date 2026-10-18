using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.ReferenceDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
[SupportedOSPlatform("linux")]
public sealed class GitEnvironmentStartupTests
{
    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task CancelRetainsTheRawBrowseRequestAndARealSettingsSaveRecoversItsOriginalSelection(bool implicitDirectory)
    {
        await using Startup f = await Startup.CreateAsync(implicitDirectory);
        DesktopSettingsDialog first = await f.SettingsAsync();
        AssertPendingRequest(f);
        f.EditGit(first, "/usr/bin/git"); Click(first, "CancelDesktopSettings");
        await Until(() => !first.IsVisible);
        Assert.That(f.Store.Load().Preferences.GitExecutable, Is.EqualTo(f.MissingGit));
        Assert.That(GitEnvironment.Options.Executable, Is.EqualTo(f.MissingGit));
        Assert.That(Read<GitRepository?>(f.Main, "_repository"), Is.Null);
        AssertPendingRequest(f);

        Task showing = (Task)typeof(MainWindow).GetMethod("ShowSettingsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Main, null)!;
        DesktopSettingsDialog repaired = await f.SettingsAsync();
        f.EditGit(repaired, "/usr/bin/git"); Click(repaired, "SaveDesktopSettings");
        await Until(() => showing.IsCompleted && !repaired.IsVisible && !f.Main.IsBusy);
        await showing;
        GitRepository repository = Read<GitRepository>(f.Main, "_repository");
        BrowseRequest request = Read<BrowseRequest>(f.Main, "_browseRequest");
        Assert.Multiple(() =>
        {
            Assert.That(repository.Path, Is.EqualTo(f.Git.Repository.Path));
            Assert.That(request.RepositoryPath, Is.EqualTo(f.Git.Repository.Path));
            Assert.That(request.PathFilter, Is.EqualTo(new BrowsePathFilter("file.txt", BrowsePathKind.File)));
            Assert.That(request.SelectedRevision, Is.EqualTo(new GitCompareRevision(f.Head)));
            Assert.That(request.FirstRevision, Is.EqualTo(new GitCompareRevision(f.First)));
            Assert.That(Read<bool>(f.Main, "_browseSelectionApplied"), Is.True);
            Assert.That(Read<DesktopStartupArguments?>(f.Main, "_startupArgumentsToRetry"), Is.Null);
            Assert.That(f.Store.Load().Preferences.GitExecutable, Is.EqualTo("/usr/bin/git"));
            Assert.That(f.Main.LastException, Is.Null);
        });
    }

    [AvaloniaTest]
    public async Task AConcurrentChoiceCannotChangeGitBetweenTheDeferredDiscoveryAndRepositoryOpening()
    {
        await using Startup f = await Startup.CreateAsync(implicitDirectory: false);
        DesktopSettingsDialog settings = await f.SettingsAsync();
        string first = f.Proxy("selected", gated: true), second = f.Proxy("concurrent", gated: false);
        f.EditGit(settings, first); Click(settings, "SaveDesktopSettings");
        await Until(() => File.Exists(f.Ready));
        GitEnvironment.Configure(new() { Executable = second });
        File.WriteAllText(f.Release, "release");
        await Until(() => !settings.IsVisible && Read<GitRepository?>(f.Main, "_repository") is not null && !f.Main.IsBusy);
        Assert.That(File.ReadAllLines(f.Log).Length, Is.GreaterThan(1), "Discovery and the real repository refresh must both have run.");
        Assert.That(File.ReadAllLines(f.Log).All(line => line == "selected"), Is.True,
            "The CLI request must resolve and open under one execution snapshot even after another window publishes a choice.");
        Assert.That(GitEnvironment.Options.Executable, Is.EqualTo(second));
        Assert.That(Read<BrowseRequest>(f.Main, "_browseRequest").SelectedRevision, Is.EqualTo(new GitCompareRevision(f.Head)));
        Assert.That(Read<DesktopStartupArguments?>(f.Main, "_startupArgumentsToRetry"), Is.Null);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ClosingSettingsOrItsMainOwnerCancelsAndDrainsARealGitBlockedDuringRetry(bool closeMain)
    {
        await using Startup f = await Startup.CreateAsync(implicitDirectory: true);
        DesktopSettingsDialog settings = await f.SettingsAsync(); string wrapper = f.Proxy("blocked", gated: true);
        byte[] index = File.ReadAllBytes(f.Git.PathInRepository(".git/index"));
        f.EditGit(settings, wrapper); Click(settings, "SaveDesktopSettings");
        await Until(() => File.Exists(f.Ready));
        int pid = int.Parse(File.ReadAllText(f.Ready), System.Globalization.CultureInfo.InvariantCulture);
        using Process git = Process.GetProcessById(pid);
        Assert.That(git.HasExited, Is.False);
        Assert.That(f.Store.Load().Preferences.GitExecutable, Is.EqualTo(wrapper), "The accepted environment publication precedes the retry.");
        Assert.That(settings.IsVisible, Is.True);
        if (closeMain) { f.Main.Close(); } else { Click(settings, "CancelDesktopSettings"); }
        await Until(() => !settings.IsVisible && (!closeMain || !f.Main.IsVisible));
        await f.Main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Multiple(() =>
        {
            Assert.That(git.HasExited, Is.True, "The window cannot finish closing while its real Git child remains alive.");
            Assert.That(File.Exists(f.Drained), Is.True, "The owned proxy must have received native cancellation before closure.");
            Assert.That(File.Exists(f.Release), Is.False, "No release or successful native completion was invented to finish the close.");
            Assert.That(f.Main.IsVisible, Is.EqualTo(!closeMain));
            Assert.That(Read<GitRepository?>(f.Main, "_repository"), Is.Null);
            Assert.That(Read<DesktopStartupArguments?>(f.Main, "_startupArgumentsToRetry"), Is.Not.Null);
            Assert.That(File.ReadAllBytes(f.Git.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(f.Store.Load().Preferences.GitExecutable, Is.EqualTo(wrapper), "Cancel after publication must not roll back a saved executable.");
        });
    }

    private static void AssertPendingRequest(Startup f)
    {
        DesktopStartupArguments pending = Read<DesktopStartupArguments>(f.Main, "_startupArgumentsToRetry");
        Assert.Multiple(() =>
        {
            Assert.That(pending.BrowseRequest!.SelectedRevision, Is.EqualTo(new GitCompareRevision("HEAD")));
            Assert.That(pending.BrowseRequest.FirstRevision, Is.EqualTo(new GitCompareRevision("HEAD~1")));
            Assert.That(pending.BrowseRequest.PathFilter, Is.EqualTo(new BrowsePathFilter("file.txt")));
            Assert.That(Read<string>(f.Main, "_startupDirectoryToRetry"), Is.EqualTo(f.InvokingDirectory));
            Assert.That(pending.BrowseRequest.RepositoryPath, Is.EqualTo(f.Arguments.RepositoryArgument ?? f.InvokingDirectory));
        });
    }

    private static T Read<T>(object target, string field)
        => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static T Field<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Window window, string name) { Pump(window); PushProcessTestDriver.Pointer(Field<Button>(window, name)); Pump(window); }
    private static void Pump(Window window)
    { Dispatcher.UIThread.RunJobs(); if (window.IsVisible) { window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(25)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "The real startup/settings operation did not settle.");
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private sealed class Startup : IAsyncDisposable
    {
        private static readonly FieldInfo Metadata = typeof(Program).GetField("_environmentStartup", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? _previousStartup = Metadata.GetValue(null);
        private readonly GitEnvironmentOptions _previousOptions = GitEnvironment.Options;
        internal ReferenceFixture Git { get; private set; } = null!;
        internal MainWindow Main { get; private set; } = null!;
        internal DesktopStartupArguments Arguments { get; private set; } = null!;
        internal GitEnvironmentPreferencesStore Store { get; private set; } = null!;
        internal string InvokingDirectory { get; private set; } = "";
        internal string Head { get; private set; } = "";
        internal string First { get; private set; } = "";
        internal string MissingGit => Git.Output("missing Git executable");
        internal string Log => Git.Output("retry-proxy.log");
        internal string Ready => Git.Output("retry-ready.pid");
        internal string Release => Git.Output("retry-release");
        internal string Drained => Git.Output("retry-drained");

        internal static async Task<Startup> CreateAsync(bool implicitDirectory)
        {
            Startup f = new(); GitEnvironment.Configure(new());
            try
            {
                f.Git = await ReferenceFixture.CreateAsync();
                f.First = await f.Git.Repository.ResolveRevisionAsync("HEAD");
                await f.Git.ChangeAsync("base\nsecond\n", "Second commit"); f.Head = await f.Git.Repository.ResolveRevisionAsync("HEAD");
                new PreferencesStore().Save(new() { Language = "English" });
                f.InvokingDirectory = implicitDirectory ? f.Git.Repository.Path : f.Git.Output("invoking cwd"); Directory.CreateDirectory(f.InvokingDirectory);
                string[] argv = implicitDirectory ? ["browse", "-commit=HEAD,HEAD~1", "-pathfilter=file.txt"]
                    : ["browse", Path.GetRelativePath(f.InvokingDirectory, f.Git.Repository.Path), "-commit=HEAD,HEAD~1", "-pathfilter=file.txt"];
                f.Arguments = DesktopStartupArguments.Parse(argv);
                f.Store = new(); GitEnvironmentPreferences missing = new() { GitExecutable = f.MissingGit, CheckAtStartup = false };
                f.Store.SaveChanges(f.Store.Load(), missing); GitEnvironment.Configure(GitEnvironmentSettingsSession.Options(missing));
                Exception? unavailable = null;
                try { await DesktopStartupDispatcher.PrepareAsync(f.Arguments, f.InvokingDirectory); }
                catch (Exception error) when (GitEnvironmentStartup.IsExecutableUnavailable(error)) { unavailable = error; }
                Assert.That(unavailable, Is.Not.Null, "Recovery metadata must originate from an actual failed native Git launch.");
                // Program's deferred branch passes this untouched Browse request and this exact invoking cwd to Main.
                Metadata.SetValue(null, new GitEnvironmentStartup(f.Arguments, true, unavailable, f.InvokingDirectory));
                f.Main = new(f.Arguments.BrowseRequest!); f.Main.Show(); Pump(f.Main);
                await f.SettingsAsync(waitForScan: true); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal async Task<DesktopSettingsDialog> SettingsAsync(bool waitForScan = false)
        {
            await Until(() => Main.OwnedWindows.OfType<DesktopSettingsDialog>().Any());
            DesktopSettingsDialog dialog = Main.OwnedWindows.OfType<DesktopSettingsDialog>().Single();
            await Until(() => dialog.GitEnvironmentSession is { IsBusy: false } session && (!waitForScan || session.LastScan is not null)); Pump(dialog);
            return dialog;
        }
        internal void EditGit(DesktopSettingsDialog dialog, string executable)
        {
            dialog.SelectGitEnvironmentPage(checklist: false); Pump(dialog);
            Field<TextBox>(dialog, "GitPath").Text = executable; Pump(dialog);
        }
        internal string Proxy(string label, bool gated)
        {
            string path = Git.Output(label + " Git λ '$()");
            string gate = gated ? "case \" $* \" in *' rev-parse '*)\n"
                + "trap " + Quote("printf drained > " + Quote(Drained) + "; exit 130") + " INT TERM\n"
                + "printf '%s\\n' \"$$\" > " + Quote(Ready + ".tmp") + "\n/bin/mv " + Quote(Ready + ".tmp") + " " + Quote(Ready) + "\n"
                + "while test ! -f " + Quote(Release) + "; do /bin/sleep 0.02; done\n;; esac\n" : "";
            File.WriteAllText(path, "#!/bin/sh\nprintf '%s\\n' " + Quote(label) + " >> " + Quote(Log) + "\n" + gate + "exec /usr/bin/git \"$@\"\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Git is not null) { File.WriteAllText(Release, "cleanup release"); }
                if (Main is not null)
                {
                    foreach (Window child in Main.OwnedWindows.ToArray()) { if (child.IsVisible) { child.Close(); } }
                    if (Main.IsVisible) { Main.Close(); }
                    await Until(() => !Main.IsVisible);
                    await Main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(25));
                }
            }
            finally { Metadata.SetValue(null, _previousStartup); GitEnvironment.Configure(_previousOptions); Git?.Dispose(); }
        }
    }
}

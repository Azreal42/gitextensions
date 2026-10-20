using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.ExternalCommandLogTestProcess;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
[SupportedOSPlatform("linux")]
public sealed class ExternalProcessCommandLogTests
{
    [SetUp] public void Setup() { GitCommandLog.Clear(); }
    [TearDown] public void TearDown() { GitCommandLog.Clear(); }

    [Test]
    public async Task CancellingTheUiWaitLeavesTheRealDetachedProcessAndItsLineRunningUntilItsActualExit()
    {
        await using ExternalCommandLogTestProcess child = new(exit: 7);
        using CancellationTokenSource cancellation = new();
        LoggedExternalProcess observation = LoggedExternalProcess.Start(child.Info("argument-secret"), GitCommandLogProcessKind.GitGui, child.Start);
        await child.ReadyAsync(); GitCommandLogEntry live = GitCommandLog.Snapshot().Single();
        Assert.That(observation.ProcessId, Is.EqualTo(child.Pid)); Assert.That(live.ProcessId, Is.EqualTo(child.Pid)); Assert.That(live.IsRunning, Is.True);
        Task<int?> ui = observation.WaitAsync(cancellation.Token); cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await ui);
        Assert.That(child.StillPresent, Is.True); Assert.That(observation.Completion.IsCompleted, Is.False);
        GitCommandLogEntry afterCancel = GitCommandLog.Snapshot().Single();
        Assert.That(afterCancel.Id, Is.EqualTo(live.Id)); Assert.That(afterCancel.IsRunning, Is.True); Assert.That(afterCancel.Cancelled, Is.False); Assert.That(afterCancel.ExitCode, Is.Null);
        child.Release(); Assert.That(await observation.Completion.WaitAsync(TimeSpan.FromSeconds(20)), Is.EqualTo(7)); await child.WaitAsync();
        GitCommandLogEntry done = GitCommandLog.Snapshot().Single();
        Assert.That(done.Id, Is.EqualTo(live.Id)); Assert.That(done.ProcessId, Is.EqualTo(child.Pid)); Assert.That(done.ExitCode, Is.EqualTo(7));
        Assert.That(done.IsRunning, Is.False); Assert.That(done.Cancelled, Is.False); Assert.That(done.IsUnobserved, Is.False);
    }

    [Test]
    public async Task FastExitAndLaunchFailureAreDifferentFromAHandoffWithNoProcessAndNoInventedSuccess()
    {
        await using ExternalCommandLogTestProcess fast = new(exit: 7, fast: true);
        LoggedExternalProcess actual = LoggedExternalProcess.Start(fast.Info(), GitCommandLogProcessKind.OpenResource, fast.Start);
        Assert.That(await actual.Completion.WaitAsync(TimeSpan.FromSeconds(20)), Is.EqualTo(7)); await fast.WaitAsync();
        Assert.That(GitCommandLog.Snapshot().Last().IsUnobserved, Is.False);
        IOException expected = new("launcher-exception-secret");
        Assert.That(Assert.Throws<IOException>(() => LoggedExternalProcess.Start(fast.Info(), GitCommandLogProcessKind.OpenResource, _ => throw expected)), Is.SameAs(expected));
        GitCommandLogEntry failed = GitCommandLog.Snapshot().Last();
        Assert.That(failed.ProcessId, Is.Null); Assert.That(failed.ExitCode, Is.Null); Assert.That(failed.IsRunning, Is.False); Assert.That(failed.IsUnobserved, Is.False);
        LoggedExternalProcess handoff = LoggedExternalProcess.Start(fast.Info("https://url-secret.invalid/token"), GitCommandLogProcessKind.OpenResource, _ => null);
        Assert.That(await handoff.Completion, Is.Null); Assert.That(handoff.ProcessId, Is.Null);
        GitCommandLogEntry unknown = GitCommandLog.Snapshot().Last();
        Assert.That(unknown.IsRunning, Is.False); Assert.That(unknown.IsUnobserved, Is.True); Assert.That(unknown.ExitCode, Is.Null); Assert.That(unknown.Cancelled, Is.False);
        TranslationCatalog translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo("en"));
        Assert.That(GitCommandLogPresentation.Detail(unknown, translations), Does.Contain("unknown").And.Not.Contain("Failed"));
        Assert.That(GitCommandLogPresentation.Detail(failed, translations), Does.Contain("Failed"));
        string metadata = JsonSerializer.Serialize(GitCommandLog.Snapshot());
        Assert.That(metadata, Does.Not.Contain("url-secret").And.Not.Contain("launcher-exception-secret"));
    }

    [Test]
    public async Task ReverseCompletionAndClearDoNotReorderOrResurrectDetachedRows()
    {
        await using ExternalCommandLogTestProcess first = new(); await using ExternalCommandLogTestProcess second = new();
        LoggedExternalProcess a = LoggedExternalProcess.Start(first.Info(), GitCommandLogProcessKind.GitGui, first.Start); await first.ReadyAsync();
        LoggedExternalProcess b = LoggedExternalProcess.Start(second.Info(), GitCommandLogProcessKind.Gitk, second.Start); await second.ReadyAsync();
        long[] order = GitCommandLog.Snapshot().Select(x => x.Id).ToArray();
        second.Release(); await b.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(GitCommandLog.Snapshot().Select(x => x.Id), Is.EqualTo(order)); Assert.That(GitCommandLog.Snapshot()[0].IsRunning, Is.True);
        GitCommandLog.Clear(); Assert.That(first.StillPresent, Is.True); first.Release(); await a.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(GitCommandLog.Snapshot(), Is.Empty); await first.WaitAsync(); await second.WaitAsync();
    }

    [AvaloniaTest]
    public async Task ClosingTheRealJournalDoesNotEndOrKillTheDetachedCommandAndCopyExportContainOnlySafeMetadata()
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        await using ExternalCommandLogTestProcess child = new();
        GitCommandLogCapturePolicy previous = GitCommandLog.CapturePolicy;
        GitCommandLog.Clear();
        GitCommandLogWindow? log = null;
        IClipboard? clipboard = null; string? previousClipboard = null;
        try
        {
            GitCommandLog.ConfigureCapture(new(true, () => Dispatcher.UIThread.CheckAccess(), typeof(ExternalProcessCommandLogTests).Assembly));
            LoggedExternalProcess observation = StartFromUi(child);
            await child.ReadyAsync();
            log = new GitCommandLogWindow(TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo("en")));
            log.Show(); await UntilAsync(() => log.GetVisualDescendants().OfType<ListBox>().Any(x => x.Name == "LogItems" && x.Items.Count == 1));
            clipboard = log.Clipboard!; previousClipboard = await ClipboardExtensions.TryGetTextAsync(clipboard);
            ListBox list = log.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "LogItems");
            list.SelectedIndex = 0; list.Focus(); log.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            log.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control); log.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.Control);
            await UntilAsync(() => log.Work.IsCompleted); await log.Work;
            Assert.That(await ClipboardExtensions.TryGetTextAsync(log.Clipboard!), Is.EqualTo("open resource"));
            string detail = log.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "GitCommandLogText").Text!;
            string metadata = string.Join('\n', detail, JsonSerializer.Serialize(GitCommandLog.Snapshot()),
                GitCommandLogPresentation.Export(GitCommandLog.Snapshot(), false, CultureInfo.GetCultureInfo("en-US")),
                GitCommandLogPresentation.Export(GitCommandLog.Snapshot(), true, CultureInfo.GetCultureInfo("fr-FR")));
            foreach (string secret in new[] { "argument-secret", "environment-secret", "stdout-secret", "stderr-secret", "url-secret", "user-script-name-secret" })
            { Assert.That(metadata.Contains(secret, StringComparison.Ordinal), Is.False, "Non-metadata external data escaped."); }
            GitCommandLogEntry live = GitCommandLog.Snapshot().Single(); Assert.That(live.IsOnMainThread, Is.True); Assert.That(live.CallStack, Does.Contain(nameof(StartFromUi)));
            log.Close(); await log.Work; Assert.That(log.IsVisible, Is.False); Assert.That(child.StillPresent, Is.True);
            Assert.That(observation.Completion.IsCompleted, Is.False); Assert.That(GitCommandLog.Snapshot().Single().IsRunning, Is.True);
            child.Release(); Assert.That(await observation.Completion.WaitAsync(TimeSpan.FromSeconds(20)), Is.Zero); await child.WaitAsync();
            Assert.That(GitCommandLog.Snapshot().Single().IsRunning, Is.False);
            Assert.That(child.Output, Is.EqualTo("stdout-secret\n")); Assert.That(child.Error, Is.EqualTo("stderr-secret\n"));
            string completed = string.Join('\n', JsonSerializer.Serialize(GitCommandLog.Snapshot()),
                GitCommandLogPresentation.Detail(GitCommandLog.Snapshot().Single(), TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo("en"))),
                GitCommandLogPresentation.Export(GitCommandLog.Snapshot(), false, CultureInfo.GetCultureInfo("en-US")),
                GitCommandLogPresentation.Export(GitCommandLog.Snapshot(), true, CultureInfo.GetCultureInfo("fr-FR")));
            foreach (string secret in new[] { "argument-secret", "environment-secret", "stdout-secret", "stderr-secret", "url-secret", "user-script-name-secret" })
            { Assert.That(completed.Contains(secret, StringComparison.Ordinal), Is.False, "Native output reached the fixture but must never enter completed public metadata."); }
        }
        finally { if (clipboard is not null) { await clipboard.SetTextAsync(previousClipboard ?? ""); } if (log is not null) { log.Close(); await log.Work; } GitCommandLog.ConfigureCapture(previous); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LoggedExternalProcess StartFromUi(ExternalCommandLogTestProcess child)
        => LoggedExternalProcess.Start(child.Info("argument-secret", "https://url-secret.invalid/token"), GitCommandLogProcessKind.OpenResource, child.Start);

    [AvaloniaTest]
    [TestCase("gitGUIToolStripMenuItem", "git gui")]
    [TestCase("kGitToolStripMenuItem", "gitk")]
    [TestCase("gitBashToolStripMenuItem", "terminal launcher")]
    [TestCase("userManualToolStripMenuItem", "open resource")]
    public async Task ActualBrowserCommandsAdmitTheirOneRealLauncherWithoutArgumentsOrDescendants(string menuName, string descriptor)
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        await using ExternalCommandLogTestProcess child = new();
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        string privateTerminal = Path.Combine(child.Root, "xterm"); File.WriteAllText(privateTerminal, "private path-resolution fixture");
        Environment.SetEnvironmentVariable("PATH", child.Root + Path.PathSeparator + previousPath);
        ConsoleStylePreferencesStore terminalStore = new(); ConsoleStylePreferences baseline = terminalStore.Load(); terminalStore.SaveChanges(baseline, baseline with { Terminal = "xterm" });
        MainWindow owner = new();
        try
        {
            owner.Show(); await owner.OpenRepositoryAsync(profile.Repository!.Path); await UntilAsync(() => !owner.IsBusy);
            owner.ExternalLauncher = child.Start; GitCommandLog.Clear();
            MenuItem menu = AllMenus(owner).Single(item => item.Name == menuName);
            menu.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await child.ReadyAsync(); GitCommandLogEntry entry = GitCommandLog.Snapshot().Single(x => x.Command == descriptor);
            Assert.That(entry.ProcessId, Is.EqualTo(child.Pid)); Assert.That(entry.IsRunning, Is.True);
            Assert.That(GitCommandLog.Snapshot().Count(x => x.ProcessId == child.Pid), Is.EqualTo(1));
            if (descriptor == "git gui") { Assert.That(child.Arguments(), Is.EqualTo(new[] { "gui" })); }
            if (descriptor == "open resource") { Assert.That(child.Arguments().Single(), Does.StartWith("https://")); }
            child.Release(); await child.WaitAsync(); await UntilAsync(() => GitCommandLog.Snapshot().Single(x => x.Id == entry.Id).IsRunning == false);
            Assert.That(GitCommandLog.Snapshot().Single(x => x.Id == entry.Id).ExitCode, Is.Zero);
        }
        finally { child.Release(); await child.WaitAsync(); owner.Close(); await UntilAsync(() => !owner.IsVisible); Environment.SetEnvironmentVariable("PATH", previousPath); }
    }

    [Test]
    public async Task IndependentBrowseLaunchUsesTheRealPrivateApphostAndOnlyItsOwnCompletion()
    {
        await using ExternalCommandLogTestProcess app = new();
        string host = Path.Combine(app.Root, "GitExtensions"); File.Copy(app.Executable, host);
        File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await BrowseApplicationLauncher.OpenAsync(app.Root, CancellationToken.None, app.Root);
        await app.ReadyAsync(); GitCommandLogEntry live = GitCommandLog.Snapshot().Single();
        Assert.That(live.Command, Is.EqualTo("browse")); Assert.That(live.ProcessId, Is.EqualTo(app.Pid)); Assert.That(live.IsRunning, Is.True);
        Assert.That(app.Arguments(), Is.EqualTo(new[] { app.Root }));
        app.Release(); await UntilAsync(() => !GitCommandLog.Snapshot().Single().IsRunning, false); await app.WaitAsync();
        Assert.That(GitCommandLog.Snapshot().Single().ExitCode, Is.Zero);
    }

    [AvaloniaTest]
    public async Task ClosingBrowseCancelsItsGitGuiWaitWithoutTerminatingTheDetachedAppOrFinalizingItsRow()
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        await using ExternalCommandLogTestProcess child = new(); MainWindow owner = new(); owner.Show();
        await owner.OpenRepositoryAsync(profile.Repository!.Path); await UntilAsync(() => !owner.IsBusy);
        owner.ExternalLauncher = child.Start; GitCommandLog.Clear();
        try
        {
            AllMenus(owner).Single(item => item.Name == "gitGUIToolStripMenuItem").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await child.ReadyAsync(); GitCommandLogEntry live = GitCommandLog.Snapshot().Single(x => x.Command == "git gui");
            owner.Close(); await UntilAsync(() => !owner.IsVisible);
            Assert.That(child.StillPresent, Is.True); Assert.That(GitCommandLog.Snapshot().Single(x => x.Id == live.Id).IsRunning, Is.True);
            Assert.That(GitCommandLog.Snapshot().Single(x => x.Id == live.Id).Cancelled, Is.False);
            child.Release(); await child.WaitAsync(); await UntilAsync(() => !GitCommandLog.Snapshot().Single(x => x.Id == live.Id).IsRunning);
            Assert.That(GitCommandLog.Snapshot().Single(x => x.Id == live.Id).ExitCode, Is.Zero);
        }
        finally { child.Release(); await child.WaitAsync(); owner.Close(); await UntilAsync(() => !owner.IsVisible); }
    }

    [AvaloniaTest]
    [TestCase("OpenWorkingFile")]
    [TestCase("OpenRevisionFile")]
    [TestCase("ShowInFolder")]
    public async Task SharedFileLaunchesKeepTheirCapturedUiContextAndLiteralPayloadOutOfMetadata(string commandName)
    {
        FileMenuCommand command = Enum.Parse<FileMenuCommand>(commandName);
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        GitRepository repository = profile.Repository!;
        string name = "resource-name-secret.txt"; await File.WriteAllTextAsync(Path.Combine(repository.Path, name), "resource-content-secret");
        Window owner = new(); owner.Show(); await using ExternalCommandLogTestProcess child = new();
        GitCommandLogCapturePolicy previous = GitCommandLog.CapturePolicy;
        try
        {
            GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(new(true, () => Dispatcher.UIThread.CheckAccess(), typeof(ExternalProcessCommandLogTests).Assembly));
            FileMenuSelection selection = new(repository, [new(new(name, 'M'), null, new("", GitCompareRevisionKind.WorkingTree))], [name]);
            FileMenuOperations operations = new(owner, async (_, run, _) => await run(CancellationToken.None), _ => true, child.Start, CancellationToken.None);
            await StartFileCommandFromUi(operations, command, selection); await child.ReadyAsync();
            GitCommandLogEntry live = GitCommandLog.Snapshot().Single(x => x.Command == "open resource");
            Assert.That(live.ProcessId, Is.EqualTo(child.Pid)); Assert.That(live.IsOnMainThread, Is.True); Assert.That(live.CallStack, Does.Contain(nameof(StartFileCommandFromUi)));
            Assert.That(child.Arguments(), Has.Length.EqualTo(1));
            if (command != FileMenuCommand.ShowInFolder) { Assert.That(child.Arguments().Single(), Does.EndWith(name)); }
            string metadata = string.Join('\n', System.Text.Json.JsonSerializer.Serialize(GitCommandLog.Snapshot()),
                GitCommandLogPresentation.Export(GitCommandLog.Snapshot(), true, CultureInfo.GetCultureInfo("en-US")));
            Assert.That(metadata, Does.Not.Contain("resource-name-secret").And.Not.Contain("resource-content-secret"));
            child.Release(); await child.WaitAsync(); await UntilAsync(() => !GitCommandLog.Snapshot().Single(x => x.Id == live.Id).IsRunning);
        }
        finally { child.Release(); await child.WaitAsync(); owner.Close(); await UntilAsync(() => !owner.IsVisible); GitCommandLog.ConfigureCapture(previous); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task StartFileCommandFromUi(FileMenuOperations operations, FileMenuCommand command, FileMenuSelection selection)
        => operations.ExecuteAsync(command, selection);

    [Test]
    public void PrecancelledOrInvalidDetachedLaunchDoesNotCaptureOrCallTheActualLauncher()
    {
        GitCommandLogCapturePolicy previous = GitCommandLog.CapturePolicy; int captures = 0, launches = 0;
        GitCommandLog.ConfigureCapture(new(false, () => { captures++; return true; }));
        Process? Launch(ProcessStartInfo _) { launches++; return null; }
        try
        {
            using CancellationTokenSource cancellation = new(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => LoggedExternalProcess.Start(new ProcessStartInfo("private"), GitCommandLogProcessKind.OpenResource, Launch, cancellationToken: cancellation.Token));
            Assert.Throws<ArgumentException>(() => LoggedExternalProcess.Start(new ProcessStartInfo("private") { ArgumentList = { "bad\0argument" } }, GitCommandLogProcessKind.OpenResource, Launch));
            Assert.Throws<ArgumentOutOfRangeException>(() => LoggedExternalProcess.Start(new ProcessStartInfo("private"), (GitCommandLogProcessKind)int.MaxValue, Launch));
            Assert.That(captures, Is.Zero); Assert.That(launches, Is.Zero); Assert.That(GitCommandLog.Snapshot(), Is.Empty);
        }
        finally { GitCommandLog.ConfigureCapture(previous); }
    }

    private static IEnumerable<MenuItem> AllMenus(Control root)
    {
        IEnumerable<MenuItem> Walk(IEnumerable<object?> values)
        {
            foreach (MenuItem item in values.OfType<MenuItem>())
            { yield return item; foreach (MenuItem child in Walk(item.Items.Cast<object?>())) { yield return child; } }
        }
        return root.GetVisualDescendants().OfType<Menu>().SelectMany(menu => Walk(menu.Items.Cast<object?>())).Distinct();
    }
}

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable, SupportedOSPlatform("linux")]
public sealed class RepositoryBranchVisibilityTests
{
    [Test]
    public async Task HiddenBranchesNeverRunABranchReadAndCanProjectARichCacheWithoutLeakingItsBranch()
    {
        await using Fixture f = await Fixture.Create(); await using RepositoryBranchMetadataService service = new(); string material = Fixture.Material(f.Git.Repository.Path);
        RepositoryBranchMetadata hidden = await service.ReadAsync(f.Git.Repository.Path, false, f.Execution);
        Assert.That(hidden.Valid, Is.True); Assert.That(hidden.Branch, Is.Empty); Assert.That(hidden.BranchLoaded, Is.False);
        Assert.That(f.Commands().Where(IsBranchRead), Is.Empty);
        RepositoryBranchMetadata visible = await service.ReadAsync(f.Git.Repository.Path, true, f.Execution);
        Assert.That(visible.Branch, Is.EqualTo("main")); Assert.That(visible.BranchLoaded, Is.True);
        Assert.That(f.Commands().Count(IsBranchRead), Is.EqualTo(1)); int reads = f.Commands().Length;
        RepositoryBranchMetadata projected = await service.ReadAsync(f.Git.Repository.Path, false, f.Execution);
        Assert.That(projected.Valid, Is.True); Assert.That(projected.Branch, Is.Empty);
        Assert.That(projected.IsDetached, Is.False); Assert.That(projected.BranchLoaded, Is.False);
        Assert.That(f.Commands(), Has.Length.EqualTo(reads), "Disabling branch labels should reuse validity without another Git invocation.");
        Assert.That(Fixture.Material(f.Git.Repository.Path), Is.EqualTo(material));
    }

    [Test]
    [TestCase("bare")]
    [TestCase("unborn")]
    [TestCase("detached")]
    [TestCase("literal")]
    public async Task RealGitMetadataDistinguishesBareUnbornDetachedAndLiteralDirectoryNames(string kind)
    {
        await using Fixture f = await Fixture.Create(); string path = await f.Repository(kind); f.ClearCommands(); string material = Fixture.Material(path);
        await using RepositoryBranchMetadataService service = new(); RepositoryBranchMetadata value = await service.ReadAsync(path, true, f.Execution);
        Assert.That(value.Valid, Is.True); Assert.That(value.IsBare, Is.EqualTo(kind == "bare"));
        Assert.That(value.IsDetached, Is.EqualTo(kind == "detached"));
        if (kind == "bare") { Assert.That(value.Branch, Is.Empty); Assert.That(f.Commands().Where(IsBranchRead), Is.Empty); }
        else if (kind == "detached") { Assert.That(value.Branch, Is.Empty); Assert.That(value.BranchLoaded, Is.True); }
        else { Assert.That(value.Branch, Is.EqualTo("main")); Assert.That(value.BranchLoaded, Is.True); }
        Assert.That(Fixture.Material(path), Is.EqualTo(material));
    }

    [Test]
    public async Task CacheIsSeparatedByCapturedGitContextAndInvalidatingAPathReloadsItsRealBranch()
    {
        await using Fixture f = await Fixture.Create(); await using RepositoryBranchMetadataService service = new();
        RepositoryBranchMetadata first = await service.ReadAsync(f.Git.Repository.Path, true, f.Execution);
        Assert.That(first.Branch, Is.EqualTo("main"));
        await f.Native(f.Git.Repository.Path, "checkout", "-b", "later-branch");
        Assert.That((await service.ReadAsync(f.Git.Repository.Path, true, f.Execution)).Branch, Is.EqualTo("main"), "A fresh read requires explicit invalidation.");
        Dictionary<string, string?> environment = new(f.Execution.EnvironmentVariables, StringComparer.Ordinal) { ["HOME"] = f.Git.Output("other immutable home") };
        GitExecutionEnvironment another = GitExecutionEnvironment.Capture(new() { Executable = f.Helper }, environment);
        Assert.That((await service.ReadAsync(f.Git.Repository.Path, true, another)).Branch, Is.EqualTo("later-branch"));
        Assert.That(f.Commands().Count(IsBranchRead), Is.EqualTo(2));
        service.Invalidate(f.Git.Repository.Path);
        Assert.That((await service.ReadAsync(f.Git.Repository.Path, true, f.Execution)).Branch, Is.EqualTo("later-branch"));
        Assert.That(f.Commands().Count(IsBranchRead), Is.EqualTo(3));
    }

    [Test]
    public async Task AnOptionalBranchFailureKeepsRealRepositoryValidityAndCanRetryWithoutPoisoningTheCache()
    {
        await using Fixture f = await Fixture.Create(); await using RepositoryBranchMetadataService service = new(); f.FailBranch(true);
        RepositoryBranchMetadata failed = await service.ReadAsync(f.Git.Repository.Path, true, f.Execution);
        Assert.That(failed.Valid, Is.True); Assert.That(failed.BranchLoaded, Is.False); Assert.That(failed.Branch, Is.Empty);
        int reads = f.Commands().Length;
        Assert.That((await service.ReadAsync(f.Git.Repository.Path, false, f.Execution)).Valid, Is.True);
        Assert.That(f.Commands(), Has.Length.EqualTo(reads));
        f.FailBranch(false); RepositoryBranchMetadata retried = await service.ReadAsync(f.Git.Repository.Path, true, f.Execution);
        Assert.That(retried.Valid, Is.True); Assert.That(retried.BranchLoaded, Is.True); Assert.That(retried.Branch, Is.EqualTo("main"));
        Assert.That(f.Commands().Count(IsBranchRead), Is.EqualTo(2));
    }

    [Test]
    public async Task CancelingOneConsumerDoesNotCancelTheSharedNativeReadNeededByAnotherConsumer()
    {
        await using Fixture f = await Fixture.Create(); await using RepositoryBranchMetadataService service = new(); f.GateBranch();
        using CancellationTokenSource caller = new();
        Task<RepositoryBranchMetadata> canceled = service.ReadAsync(f.Git.Repository.Path, true, f.Execution, caller.Token);
        Task<RepositoryBranchMetadata> retained = service.ReadAsync(f.Git.Repository.Path, true, f.Execution);
        await Until(() => File.Exists(f.Ready)); caller.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await canceled);
        Assert.That(retained.IsCompleted, Is.False); f.ReleaseBranch();
        Assert.That((await retained.WaitAsync(TimeSpan.FromSeconds(10))).Branch, Is.EqualTo("main"));
        await service.Completion; Assert.That(f.Commands().Count(IsBranchRead), Is.EqualTo(1));
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task InvalidatingOrDisposingDuringABranchReadCancelsAndDrainsItsActualNativeProcess(bool dispose)
    {
        await using Fixture f = await Fixture.Create(); await using RepositoryBranchMetadataService service = new(); f.GateBranch();
        Task<RepositoryBranchMetadata> pending = service.ReadAsync(f.Git.Repository.Path, true, f.Execution);
        await Until(() => File.Exists(f.Ready)); Actor actor = f.Actor();
        if (dispose) { await service.DisposeAsync(); } else { service.Invalidate(f.Git.Repository.Path); }
        Assert.CatchAsync<OperationCanceledException>(async () => await pending);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(10)); AssertRetired(actor);
        if (!dispose)
        {
            f.ReleaseBranch(); Assert.That((await service.ReadAsync(f.Git.Repository.Path, true, f.Execution)).Branch, Is.EqualTo("main"));
        }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task TheSavedFlagControlsBothDashboardAndTheActualRecentMenuAndDropdown(bool show)
    {
        await using Fixture f = await Fixture.Create(); string material = Fixture.Material(f.Git.Repository.Path); MainWindow main = await f.Show(show);
        AssertDashboard(f.Dashboard!, f.Git.Repository.Path, show ? "main" : "", show);
        Assert.That(f.Commands().Count(IsBranchRead), Is.EqualTo(show ? 1 : 0), "Dashboard and recent presentations share one real branch read.");
        MenuItem recent = f.RecentMenu!; MenuItem item = recent.Items.OfType<MenuItem>().Single(menu => menu.Tag as string == f.Git.Repository.Path);
        string caption = RepositoryHistoryPresentation.Select(new RepositoryHistoryStore().Load()).Recent.Single().Caption;
        Assert.That(item.Header, Is.EqualTo(caption + (show ? " (main)" : "")));
        PushProcessTestDriver.Pointer(f.StartMenu!); Pump(main); PushProcessTestDriver.Pointer(recent); Pump(main);
        Assert.That(recent.IsSubMenuOpen, Is.True); Assert.That(item.IsEffectivelyVisible, Is.True);
        Press(main, PhysicalKey.Escape); Press(main, PhysicalKey.Escape);

        await main.OpenRepositoryAsync(f.Git.Repository.Path); await f.Idle();
        ComboBox dropdown = Field<ComboBox>(main, "RecentRepositoriesDropdown"); PushProcessTestDriver.Pointer(dropdown); Pump(main);
        Assert.That(dropdown.IsDropDownOpen, Is.True);
        RepositoryHistoryRow row = dropdown.Items.OfType<RepositoryHistoryRow>().Single(value => value.Path == f.Git.Repository.Path);
        TextBlock actual = ((Control)dropdown.ContainerFromItem(row)!).GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Name == "RecentRepositoryCaption" && text.Tag as string == f.Git.Repository.Path);
        Assert.That(actual.Text, Is.EqualTo(caption + (show ? " (main)" : "")));
        Assert.That(actual.IsEffectivelyVisible, Is.True); Press(main, PhysicalKey.Escape);
        Assert.That(Fixture.Material(f.Git.Repository.Path), Is.EqualTo(material));
    }

    [AvaloniaTest]
    [TestCase("bare")]
    [TestCase("unborn")]
    [TestCase("detached")]
    [TestCase("literal")]
    [TestCase("invalid")]
    public async Task DashboardAndRecentCaptionsRepresentTheRealRepositoryKindWithoutInventingABranch(string kind)
    {
        await using Fixture f = await Fixture.Create(); string path = await f.Repository(kind); f.ClearCommands();
        await f.Show(true, path); bool valid = kind != "invalid", labeled = valid && kind != "bare";
        string branch = kind == "detached" ? "Detached HEAD" : "main";
        AssertDashboard(f.Dashboard!, path, labeled ? branch : "", visible: true);
        MenuItem item = f.RecentMenu!.Items.OfType<MenuItem>().Single(menu => menu.Tag as string == path);
        Assert.That((item.Header as string)?.EndsWith(" (" + branch + ")", StringComparison.Ordinal), Is.EqualTo(labeled));
        Control tile = Tile(f.Dashboard!, path); Image icon = tile.GetVisualDescendants().OfType<Image>().Single();
        Assert.That(AutomationProperties.GetName(icon), Is.EqualTo(valid ? "Git repository" : "Invalid repository"));
        if (kind is "bare" or "invalid") { Assert.That(f.Commands().Where(IsBranchRead), Is.Empty); }
    }

    [AvaloniaTest]
    public async Task AFailedOptionalBranchReadKeepsTheGitRepositoryIconAndDoesNotInventADetachedCaption()
    {
        await using Fixture f = await Fixture.Create(); f.FailBranch(true); await f.Show(true);
        AssertDashboard(f.Dashboard!, f.Git.Repository.Path, "", true);
        foreach (Control tile in f.Dashboard!.GetVisualDescendants().OfType<Control>()
            .Where(control => control.Tag is DashboardRepository entry && entry.Path == f.Git.Repository.Path))
        {
            Assert.That(AutomationProperties.GetName(tile.GetVisualDescendants().OfType<Image>().Single()), Is.EqualTo("Git repository"));
        }
        MenuItem item = f.RecentMenu!.Items.OfType<MenuItem>().Single(menu => menu.Tag as string == f.Git.Repository.Path);
        Assert.That(item.Header as string, Does.Not.Contain("Detached HEAD").And.Not.EndWith(" (main)"));
    }

    [AvaloniaTest]
    public async Task TurningOffAndReopeningKeepsBothConsumersHiddenAndAvoidsFurtherBranchReads()
    {
        await using Fixture f = await Fixture.Create(); MainWindow main = await f.Show(true); f.ClearCommands();
        PreferencesStore store = new(); store.Save(store.Load() with { ShowRepoCurrentBranch = false });
        await main.ApplyRepositoryBranchVisibilityAsync(false); await f.Idle();
        AssertDashboard(f.Dashboard!, f.Git.Repository.Path, "", false);
        Assert.That(f.RecentMenu!.Items.OfType<MenuItem>().All(item => !(item.Header as string ?? "").EndsWith(" (main)", StringComparison.Ordinal)), Is.True);
        Assert.That(f.Commands().Where(IsBranchRead), Is.Empty);
        await f.Close(); f.ClearCommands(); await f.Show(false);
        AssertDashboard(f.Dashboard!, f.Git.Repository.Path, "", false); Assert.That(f.Commands().Where(IsBranchRead), Is.Empty);
    }

    [AvaloniaTest]
    public async Task TurningOnLoadsTheRealBranchIntoExistingRowsWithoutReplacingTheRecentSources()
    {
        await using Fixture f = await Fixture.Create(); MainWindow main = await f.Show(false); f.ClearCommands();
        ComboBox dropdown = Field<ComboBox>(main, "RecentRepositoriesDropdown"); object? source = dropdown.ItemsSource;
        object? menuSource = f.RecentMenu!.ItemsSource;
        new PreferencesStore().Save(new PreferencesStore().Load() with { ShowRepoCurrentBranch = true });
        await main.ApplyRepositoryBranchVisibilityAsync(true); await f.Idle();
        AssertDashboard(f.Dashboard!, f.Git.Repository.Path, "main", true);
        Assert.That(f.RecentMenu.Items.OfType<MenuItem>().Single(item => item.Tag as string == f.Git.Repository.Path).Header as string, Does.EndWith(" (main)"));
        Assert.That(dropdown.ItemsSource, Is.SameAs(source)); Assert.That(f.RecentMenu.ItemsSource, Is.SameAs(menuSource));
        Assert.That(f.Commands().Count(IsBranchRead), Is.EqualTo(1));
        Assert.That(f.Commands().Any(command => command.Arguments.Contains("status", StringComparer.Ordinal)), Is.False);
    }

    [AvaloniaTest]
    public async Task ReopeningReadsTheNewRealBranchInsteadOfRetainingTheClosedWindowsCachedLabel()
    {
        await using Fixture f = await Fixture.Create(); await f.Show(true);
        AssertDashboard(f.Dashboard!, f.Git.Repository.Path, "main", true);
        await f.Native(f.Git.Repository.Path, "branch", "-m", "renamed-after-first-window");
        await f.Close(); f.ClearCommands(); await f.Show(true);
        AssertDashboard(f.Dashboard!, f.Git.Repository.Path, "renamed-after-first-window", true);
        Assert.That(f.RecentMenu!.Items.OfType<MenuItem>().Single(item => item.Tag as string == f.Git.Repository.Path).Header as string,
            Does.EndWith(" (renamed-after-first-window)"));
        Assert.That(f.Commands().Count(IsBranchRead), Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task ADelayedSharedResponseUpdatesCaptionsWithoutReplacingTheRecentItemSources()
    {
        await using Fixture f = await Fixture.Create(); f.GateBranch(); MainWindow main = await f.Show(true, waitMetadata: false);
        await Until(() => File.Exists(f.Ready));
        ComboBox dropdown = Field<ComboBox>(main, "RecentRepositoriesDropdown"); object? source = dropdown.ItemsSource;
        object? menuSource = f.RecentMenu!.ItemsSource;
        PushProcessTestDriver.Pointer(f.StartMenu!); Pump(main); PushProcessTestDriver.Pointer(f.RecentMenu); Pump(main);
        Assert.That(f.RecentMenu.IsSubMenuOpen, Is.True);
        f.ReleaseBranch(); await f.Idle();
        Assert.That(dropdown.ItemsSource, Is.SameAs(source)); Assert.That(f.RecentMenu.ItemsSource, Is.SameAs(menuSource));
        Assert.That(f.RecentMenu.IsSubMenuOpen, Is.True, "A branch response must not close the menu the user is reading.");
        AssertDashboard(f.Dashboard!, f.Git.Repository.Path, "main", true);
        Assert.That(f.RecentMenu.Items.OfType<MenuItem>().Single(item => item.Tag as string == f.Git.Repository.Path).Header as string, Does.EndWith(" (main)"));
        Press(main, PhysicalKey.Escape); Press(main, PhysicalKey.Escape);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task TurningOffOrClosingDuringTheNativeReadDrainsItAndRejectsAnyLateBranchLabel(bool close)
    {
        await using Fixture f = await Fixture.Create(); f.GateBranch(); MainWindow main = await f.Show(true, waitMetadata: false);
        await Until(() => File.Exists(f.Ready)); Actor actor = f.Actor(); DashboardControl dashboard = f.Dashboard!;
        if (close) { await f.Close(); }
        else
        {
            new PreferencesStore().Save(new PreferencesStore().Load() with { ShowRepoCurrentBranch = false });
            await main.ApplyRepositoryBranchVisibilityAsync(false); await f.Idle();
            AssertDashboard(dashboard, f.Git.Repository.Path, "", false);
        }
        AssertRetired(actor); f.ReleaseBranch(); Dispatcher.UIThread.RunJobs();
        Assert.That(dashboard.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Name == "DashboardBranch").All(text => string.IsNullOrEmpty(text.Text)), Is.True);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task AnotherRecentRepositoryCannotBlockActiveHistoryAndItsWorkerIsAlreadyRetiredWhenTheWindowActuallyCloses(bool refresh)
    {
        await using Fixture f = await Fixture.Create(); string other = await f.Repository("literal"); f.GateBranch(other);
        MainWindow main = await f.Show(true, waitMetadata: false, otherRecent: other);
        await Until(() => File.Exists(f.Ready)); Actor actor = f.Actor();
        Task open = main.OpenRepositoryAsync(f.Git.Repository.Path);
        await Until(() => open.IsCompleted && !main.IsBusy
            && Field<ListBox>(main, "RevisionHistory").ItemsSource?.OfType<GitCommit>().Any() == true);
        await open.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(main.RepositoryBranchLoading.IsCompleted, Is.False, "An unrelated recent label is still waiting for its real symbolic-ref worker.");
        if (refresh)
        {
            int logs = f.Commands().Count(command => command.Arguments.Contains("log", StringComparer.Ordinal));
            main.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.F5, RawInputModifiers.None);
            await Until(() => !main.IsBusy && f.Commands().Count(command => command.Arguments.Contains("log", StringComparer.Ordinal)) > logs);
            Assert.That(main.RepositoryBranchLoading.IsCompleted, Is.False, "F5 finishes active history without waiting for the unrelated recent label.");
        }
        bool? retiredAtClosed = null, readersDrainedAtClosed = null;
        main.Closed += (_, _) =>
        {
            retiredAtClosed = Retired(actor);
            readersDrainedAtClosed = main.RepositoryBranchLoading.IsCompleted && main.RepositoryBranches.Completion.IsCompleted;
        };
        main.Close(); await Until(() => !main.IsVisible);
        Assert.That(retiredAtClosed, Is.True, "Closed must be raised only after the exact native PID/start-time has been reaped, including zombies.");
        Assert.That(readersDrainedAtClosed, Is.True, "Awaiting readers after Closed cannot repair a prematurely hidden window.");
        AssertRetired(actor); await f.Close();
    }

    [AvaloniaTest]
    public async Task ClosingSettingsWhileItsAppearanceApplyWaitsForAnotherRecentKeepsTheSharedReaderOwnedByMain()
    {
        await using Fixture f = await Fixture.Create(); string other = await f.Repository("literal");
        GitEnvironmentPreferencesStore environment = new(); GitEnvironmentPreferencesLoadResult baseline = environment.Load();
        environment.SaveChanges(baseline, baseline.Preferences with { GitExecutable = f.Helper, CheckAtStartup = false });
        MainWindow main = await f.Show(false, otherRecent: other); f.ClearCommands(); f.GateBranch(other);
        AppearanceSettingsInput.Press(main, PhysicalKey.Comma, RawInputModifiers.Control);
        await Until(() => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any(window => window.IsVisible));
        DesktopSettingsDialog settings = main.OwnedWindows.OfType<DesktopSettingsDialog>().Single(window => window.IsVisible);
        SettingsNavigationTestDriver.Select(settings, "Appearance");
        AppearanceSettingsInput.Click(settings, "chkShowRepoCurrentBranch");
        AppearanceSettingsInput.Click(settings, "ApplyDesktopSettings");
        await Until(() => File.Exists(f.Ready) && !settings.Navigation.IsEnabled);
        Actor first = f.Actor(); GitExecutionEnvironment execution = main.RepositoryBranchExecution;
        Task<RepositoryBranchMetadata> retained = main.RepositoryBranches.ReadAsync(other, true, execution);
        Assert.That(retained.IsCompleted, Is.False); Assert.That(Retired(first), Is.False);
        bool settingsClosed = false; settings.Closed += (_, _) => settingsClosed = true;
        AppearanceSettingsInput.Click(settings, "CancelDesktopSettings");
        await Until(() => settingsClosed && !settings.IsVisible);
        Assert.That(main.IsVisible, Is.True); Assert.That(retained.IsCompleted, Is.False,
            "Canceling the Settings waiter must not cancel the same native read retained by another consumer.");
        Assert.That(Retired(first), Is.False, "The blocked shared worker is still owned by the live Browse window.");
        Assert.That(main.RepositoryBranches.Completion.IsCompleted, Is.False);
        Assert.That(new PreferencesStore().Load().ShowRepoCurrentBranch, Is.True,
            "Closing Settings after publication does not revert the saved Appearance flag.");
        Assert.That(f.Commands().Count(command => command.Cwd == other && IsBranchRead(command)), Is.EqualTo(1));
        f.ReleaseBranch();
        RepositoryBranchMetadata value = await retained.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(value.Valid && value.BranchLoaded, Is.True); Assert.That(value.Branch, Is.EqualTo("main"));
        await f.Idle(); AssertRetired(first);
        Assert.That(f.RecentMenu!.Items.OfType<MenuItem>().Single(item => item.Tag as string == other).Header as string,
            Does.EndWith(" (main)"));

        // Exercise real owner shutdown with a fresh blocked worker as well as the surviving reader above.
        f.GateBranch(other); Task publication = main.ApplyRepositoryBranchVisibilityAsync(true);
        await Until(() => File.Exists(f.Ready)); Actor closing = f.Actor();
        Task<RepositoryBranchMetadata> closingReader = main.RepositoryBranches.ReadAsync(other, true, main.RepositoryBranchExecution);
        Assert.That(closingReader.IsCompleted, Is.False);
        bool? retiredAtClosed = null, drainedAtClosed = null;
        main.Closed += (_, _) =>
        {
            retiredAtClosed = Retired(closing);
            drainedAtClosed = main.RepositoryBranchLoading.IsCompleted && main.RepositoryBranches.Completion.IsCompleted;
        };
        main.Close(); await Until(() => !main.IsVisible);
        Assert.That(retiredAtClosed, Is.True, "The actual Closed event must follow reaping the owned PID/start-time, including zombies.");
        Assert.That(drainedAtClosed, Is.True); AssertRetired(closing);
        await publication.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.CatchAsync<OperationCanceledException>(async () => await closingReader);
        await f.Close();
    }

    private static bool IsBranchRead(Command command) => command.Arguments.Contains("symbolic-ref", StringComparer.Ordinal);
    private static void AssertDashboard(DashboardControl dashboard, string path, string expected, bool visible)
    {
        Control[] tiles = dashboard.GetVisualDescendants().OfType<Control>().Where(control => control.Tag is DashboardRepository entry && entry.Path == path).ToArray();
        Assert.That(tiles, Is.Not.Empty);
        foreach (Control tile in tiles)
        {
            TextBlock branch = tile.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "DashboardBranch");
            Assert.That(branch.Text ?? "", Is.EqualTo(expected)); Assert.That(branch.IsVisible, Is.EqualTo(visible));
        }
    }
    private static Control Tile(DashboardControl dashboard, string path) => dashboard.GetVisualDescendants().OfType<Control>()
        .First(control => control.Tag is DashboardRepository entry && entry.Path == path);
    private sealed record Command(string Cwd, string[] Arguments);
    private sealed record Actor(int Pid, string StartTicks);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly GitEnvironmentOptions _originalOptions = GitEnvironment.Options;
        internal GitFixture Git { get; }
        internal string Helper => Git.Output("record repository metadata.py");
        private string Log => Git.Output("repository metadata.jsonl");
        private string Gate => Git.Output("gate branch read");
        private string Failure => Git.Output("fail branch read");
        internal string Ready => Git.Output("branch read ready.json");
        internal GitExecutionEnvironment Execution { get; }
        internal MainWindow? Main { get; private set; }
        internal DashboardControl? Dashboard { get; private set; }
        internal MenuItem? StartMenu { get; private set; }
        internal MenuItem? RecentMenu { get; private set; }
        private Fixture(GitFixture git)
        {
            Git = git; git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0"); git.SetEnvironment("GIT_TERMINAL_PROMPT", "0");
            File.WriteAllText(Helper, "#!/usr/bin/python3\nimport json, os, pathlib, sys, time\n"
                + "with open(" + JsonSerializer.Serialize(Log) + ", 'a', encoding='utf-8') as log:\n    log.write(json.dumps({'Cwd':os.getcwd(),'Arguments':sys.argv[1:]}) + '\\n')\n"
                + "gate = pathlib.Path(" + JsonSerializer.Serialize(Gate) + ")\n"
                + "if 'symbolic-ref' in sys.argv and gate.exists() and gate.read_text() in ('hold\\n', os.getcwd()):\n"
                + "    ticks = pathlib.Path('/proc/self/stat').read_text().rsplit(')', 1)[1].split()[19]\n"
                + "    ready = pathlib.Path(" + JsonSerializer.Serialize(Ready) + ")\n"
                + "    temporary = ready.with_name(ready.name + '.' + str(os.getpid()) + '.tmp')\n"
                + "    temporary.write_text(json.dumps({'Pid':os.getpid(),'StartTicks':ticks}))\n    temporary.replace(ready)\n"
                + "    while pathlib.Path(" + JsonSerializer.Serialize(Gate) + ").exists(): time.sleep(0.01)\n"
                + "if 'symbolic-ref' in sys.argv and pathlib.Path(" + JsonSerializer.Serialize(Failure) + ").exists():\n    sys.stderr.write('fixture branch read unavailable\\n')\n    sys.exit(2)\n"
                + "os.execv('/usr/bin/git', ['/usr/bin/git'] + sys.argv[1:])\n");
            File.SetUnixFileMode(Helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Execution = GitExecutionEnvironment.Capture(new() { Executable = Helper });
        }
        internal static async Task<Fixture> Create() => new(await GitFixture.CreateAsync());
        internal static string Material(string path) => JsonSerializer.Serialize(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToDictionary(file => Path.GetRelativePath(path, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), StringComparer.Ordinal));
        internal async Task Native(string path, params string[] arguments)
        { GitResult result = await new GitRunner("/usr/bin/git").RunAsync(path, arguments); Assert.That(result.Success, Is.True, result.StandardError); }
        internal async Task<string> Repository(string kind)
        {
            if (kind == "detached") { await Native(Git.Repository.Path, "checkout", "--detach", "HEAD"); return Git.Repository.Path; }
            string path = Git.Output(kind == "literal" ? "repo 'quoted';$(literal)\n雪" : kind + " repository");
            if (kind is "bare" or "literal") { await Native(Git.Repository.Path, "clone", kind == "bare" ? "--bare" : "--no-hardlinks", Git.Repository.Path, path); }
            else { Directory.CreateDirectory(path); if (kind == "unborn") { await Native(path, "init", "--initial-branch=main"); } }
            return path;
        }
        internal Command[] Commands() => File.Exists(Log) ? File.ReadAllLines(Log).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<Command>(line)!).ToArray() : [];
        internal void ClearCommands() { if (File.Exists(Log)) { File.Delete(Log); } }
        internal void GateBranch(string? path = null) { if (File.Exists(Ready)) { File.Delete(Ready); } File.WriteAllText(Gate, path ?? "hold\n"); }
        internal void ReleaseBranch() { if (File.Exists(Gate)) { File.Delete(Gate); } }
        internal void FailBranch(bool fail) { if (fail) { File.WriteAllText(Failure, "fail\n"); } else if (File.Exists(Failure)) { File.Delete(Failure); } }
        internal Actor Actor() => JsonSerializer.Deserialize<Actor>(File.ReadAllText(Ready))!;
        internal async Task<MainWindow> Show(bool show, string? repository = null, bool waitMetadata = true, string? otherRecent = null)
        {
            Assert.That(Main, Is.Null); string path = repository ?? Git.Repository.Path;
            new PreferencesStore().Save(new() { ShowRepoCurrentBranch = show, Language = "English" });
            new RepositoryHistoryStore().Save(new() { Recent = otherRecent is null ? [new(path)] : [new(path), new(otherRecent)], Favorites = [new(path)] });
            GitEnvironment.Configure(new() { Executable = Helper }); Main = new() { Width = 1440, Height = 960 }; Main.Show(); Pump(Main);
            Dashboard = Main.GetVisualDescendants().OfType<DashboardControl>().Single();
            StartMenu = Main.GetVisualDescendants().OfType<MenuItem>().Single(item => item.Header as string == "Start");
            RecentMenu = StartMenu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Recent repositories");
            if (waitMetadata) { await Idle(); } return Main;
        }
        internal async Task Idle()
        {
            await Until(() => Main is not null && !Main.IsBusy && Main.WindowWorkCompletion.IsCompleted && Main.RepositoryBranchLoading.IsCompleted);
            await Main!.RepositoryBranchLoading.WaitAsync(TimeSpan.FromSeconds(10)); await Main.RepositoryBranches.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            if (Dashboard is not null) { await Dashboard.MetadataLoading.WaitAsync(TimeSpan.FromSeconds(10)); }
            Pump(Main); Assert.That(Main.LastException, Is.Null, Main.LastException?.ToString());
        }
        internal async Task Close()
        {
            if (Main is null) { return; }
            MainWindow closing = Main; closing.Close();
            await Until(() => !closing.IsVisible && closing.WindowWorkCompletion.IsCompleted && closing.RepositoryBranches.Completion.IsCompleted);
            await closing.WindowWorkCompletion; await closing.RepositoryBranches.Completion; Main = null;
        }
        public async ValueTask DisposeAsync()
        {
            try { ReleaseBranch(); await Close(); }
            finally { GitEnvironment.Configure(_originalOptions); Git.Dispose(); }
        }
    }

    private static void AssertRetired(Actor actor) => Assert.That(Retired(actor), Is.True,
        "The actual owned Git wrapper must be reaped before completion, including zombie processes.");
    private static bool Retired(Actor actor)
    {
        string path = "/proc/" + actor.Pid + "/stat", stat;
        try { stat = File.ReadAllText(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return true; }
        // A procfs task may vanish after open; .NET Unix exposes ESRCH as raw errno 3.
        catch (IOException error) when (OperatingSystem.IsLinux() && error.HResult == 3) { return true; }
        string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return fields[19] != actor.StartTicks;
    }
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static void Press(Window window, PhysicalKey key)
    { window.KeyPressQwerty(key, RawInputModifiers.None); window.KeyReleaseQwerty(key, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(20))
        { if (Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.RunJobs(); } await Task.Delay(10); }
        Assert.That(condition(), Is.True, "Timed out while waiting for actual repository metadata/UI/native retirement.");
    }
}

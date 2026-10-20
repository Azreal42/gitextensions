using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommandLogWindowPlacementStateTests
{
    private static readonly CommandLogWindowPlacementValue First = new(-140, 75, 740.5, 490.25, 1.25, false);
    private static readonly CommandLogWindowPlacementValue Last = new(210, 115, 820, 560, 2, true);

    [Test]
    public async Task AnUninitializedProfileNeverWritesAndAnInitializedMissingProfilePublishesBeforeItsOwnedWriteDrains()
    {
        await using PlacementScope f = await PlacementScope.Create();
        CommandLogWindowPlacementState unknown = CommandLogWindowPlacementState.Open();
        Assert.That(unknown.Value, Is.Null); unknown.Save(First);
        await CommandLogWindowPlacementState.DrainAsync(); Assert.That(Directory.Exists(f.StateHome), Is.False);
        await CommandLogWindowPlacementState.InitializeRuntimeAsync();
        Assert.That(File.Exists(f.StateFile), Is.False, "Bootstrap of a missing state is a read, not a write.");
        CommandLogWindowPlacementState state = CommandLogWindowPlacementState.Open(); state.Save(First);
        Assert.That(state.Value, Is.EqualTo(First)); Assert.That(CommandLogWindowPlacementState.Open().Value, Is.EqualTo(First));
        await CommandLogWindowPlacementState.DrainAsync(); f.AssertStored(First);
        Assert.That(Directory.Exists(f.ConfigHome), Is.False, "Window state must not create editable Settings or capture XML.");
        f.AssertNoTemporaryFiles();
    }

    [Test]
    public async Task CapturedSessionsKeepTheirProfileWhenXdgChangesAndAnUnloadedSecondProfileCannotOverwriteEitherFile()
    {
        await using PlacementScope f = await PlacementScope.Create();
        string firstPath = f.StateFile; await CommandLogWindowPlacementState.InitializeRuntimeAsync();
        CommandLogWindowPlacementState first = CommandLogWindowPlacementState.Open(); first.Save(First);
        f.SelectProfile("state-b"); string secondPath = f.StateFile;
        CommandLogWindowPlacementState unloaded = CommandLogWindowPlacementState.Open();
        Assert.That(unloaded.Value, Is.Null); unloaded.Save(Last); await CommandLogWindowPlacementState.DrainAsync();
        Assert.That(File.Exists(secondPath), Is.False); f.AssertStored(First, firstPath);
        await CommandLogWindowPlacementState.InitializeRuntimeAsync(); CommandLogWindowPlacementState second = CommandLogWindowPlacementState.Open();
        first.Save(Last); second.Save(First with { X = 410 });
        await CommandLogWindowPlacementState.DrainAsync();
        f.AssertStored(Last, firstPath); f.AssertStored(First with { X = 410 }, secondPath);
        Assert.That(CommandLogWindowPlacementState.Open().Value, Is.EqualTo(First with { X = 410 }));
    }

    [Test]
    public async Task SavingMergesCurrentUnknownWindowsAndNestedFieldsWithoutTouchingTheirMeaning()
    {
        await using PlacementScope f = await PlacementScope.Create();
        JsonObject document = PlacementScope.Document(First);
        document["OtherWindow"] = JsonNode.Parse("{\"future\":[17,null,\"Ω\\nliteral\"]}");
        document["unknown-root"] = JsonNode.Parse("[true,{\"nil\":null}]");
        document[CommandLogWindowPlacementState.Key]!["future-entry"] = JsonNode.Parse("{\"zoom\":1.75,\"names\":[\"λ\"]}");
        f.Write(document.ToJsonString()); await CommandLogWindowPlacementState.InitializeRuntimeAsync();
        CommandLogWindowPlacementState state = CommandLogWindowPlacementState.Open();
        JsonObject external = f.Read(); external["external-after-load"] = "keep 🧪";
        f.Write(external.ToJsonString()); state.Save(Last); await CommandLogWindowPlacementState.DrainAsync();
        JsonObject saved = f.Read(); f.AssertStored(Last);
        foreach (string key in new[] { "OtherWindow", "unknown-root", "external-after-load" })
        { Assert.That(JsonNode.DeepEquals(saved[key], external[key]), Is.True, key); }
        Assert.That(JsonNode.DeepEquals(saved[CommandLogWindowPlacementState.Key]!["future-entry"], external[CommandLogWindowPlacementState.Key]!["future-entry"]), Is.True);
        f.AssertNoTemporaryFiles();
    }

    private static IEnumerable<TestCaseData> InvalidDocuments()
    {
        yield return new TestCaseData("").SetName("WindowPlacementEmptyStateStaysByteIdentical");
        yield return new TestCaseData("{").SetName("WindowPlacementBrokenJsonStaysByteIdentical");
        yield return new TestCaseData("[]").SetName("WindowPlacementWrongRootFormatStaysByteIdentical");
        yield return new TestCaseData("{\"FormGitCommandLog\":null}").SetName("WindowPlacementNullKnownEntryStaysByteIdentical");
        foreach ((string field, JsonNode? value) in new (string, JsonNode?)[]
        {
            ("Width", JsonValue.Create("740")), ("Height", null), ("Scaling", JsonValue.Create(0)), ("State", JsonValue.Create("Docked"))
        })
        {
            JsonObject invalid = PlacementScope.Document(First); invalid[CommandLogWindowPlacementState.Key]![field] = value;
            yield return new TestCaseData(invalid.ToJsonString()).SetName("WindowPlacementInvalid" + field + "StaysByteIdentical");
        }
        yield return new TestCaseData("{\"FormGitCommandLog\":{},\"FormGitCommandLog\":{}}").SetName("WindowPlacementDuplicateKnownKeyStaysByteIdentical");
        yield return new TestCaseData("{\"future\":{\"x\":1,\"x\":2}}").SetName("WindowPlacementDuplicateUnknownNestedKeyStaysByteIdentical");
    }

    [TestCaseSource(nameof(InvalidDocuments))]
    public async Task InvalidStateIsNotRepairedByBootstrapSaveOrReload(string invalid)
    {
        await using PlacementScope f = await PlacementScope.Create(); f.Write(invalid); byte[] before = File.ReadAllBytes(f.StateFile);
        await CommandLogWindowPlacementState.InitializeRuntimeAsync(); CommandLogWindowPlacementState state = CommandLogWindowPlacementState.Open();
        Assert.That(state.Value, Is.Null); state.Save(Last); await CommandLogWindowPlacementState.DrainAsync();
        await CommandLogWindowPlacementState.InitializeRuntimeAsync(); Assert.That(CommandLogWindowPlacementState.Open().Value, Is.Null);
        Assert.That(File.ReadAllBytes(f.StateFile), Is.EqualTo(before)); Assert.That(File.Exists(f.StateFile + ".lock"), Is.False);
        f.AssertNoTemporaryFiles();
    }

    [Test]
    public async Task OversizedOrExcessivelyNestedStateIsLeftUntouched()
    {
        await using PlacementScope f = await PlacementScope.Create();
        foreach (string invalid in new[] { new string(' ', 64 * 1024 + 1), new string('[', 34) + "0" + new string(']', 34) })
        {
            f.Write(invalid); byte[] before = File.ReadAllBytes(f.StateFile);
            await CommandLogWindowPlacementState.InitializeRuntimeAsync(); CommandLogWindowPlacementState state = CommandLogWindowPlacementState.Open();
            Assert.That(state.Value, Is.Null); state.Save(First); await CommandLogWindowPlacementState.DrainAsync();
            Assert.That(File.ReadAllBytes(f.StateFile), Is.EqualTo(before)); f.AssertNoTemporaryFiles();
        }
    }

    [Test]
    public async Task StateCorruptedAfterBootstrapIsNotOverwrittenByADeferredSave()
    {
        await using PlacementScope f = await PlacementScope.Create(); f.Write(PlacementScope.Document(First).ToJsonString());
        await CommandLogWindowPlacementState.InitializeRuntimeAsync(); CommandLogWindowPlacementState state = CommandLogWindowPlacementState.Open();
        f.Write("{\"foreign-corruption\":"); byte[] corrupt = File.ReadAllBytes(f.StateFile);
        state.Save(Last); Assert.That(state.Value, Is.EqualTo(Last), "An in-memory close/reopen snapshot is distinct from durable IO success.");
        await CommandLogWindowPlacementState.DrainAsync(); Assert.That(File.ReadAllBytes(f.StateFile), Is.EqualTo(corrupt));
        f.AssertNoTemporaryFiles();
    }

    [TestCase("Normal", false)] [TestCase("Maximized", true)] [TestCase("Minimized", false)]
    public async Task PersistedWindowStatesKeepNormalBoundsAndOnlyMaximizedSurvivesNormalization(string stateName, bool maximized)
    {
        await using PlacementScope f = await PlacementScope.Create(); JsonObject document = PlacementScope.Document(First);
        document[CommandLogWindowPlacementState.Key]!["State"] = stateName; f.Write(document.ToJsonString());
        await CommandLogWindowPlacementState.InitializeRuntimeAsync(); CommandLogWindowPlacementState state = CommandLogWindowPlacementState.Open();
        Assert.That(state.Value, Is.EqualTo(First with { Maximized = maximized }));
        state.Save(state.Value!); await CommandLogWindowPlacementState.DrainAsync();
        Assert.That(f.Read()[CommandLogWindowPlacementState.Key]!["State"]!.GetValue<string>(), Is.EqualTo(maximized ? "Maximized" : "Normal"));
        f.AssertStored(First with { Maximized = maximized });
    }

    [Test]
    public async Task AReadOnlyFileKeepsItsExactBytesAndAnActuallyUnreadableFileNeverBecomesWritableState()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Unix permission evidence requires the Linux test host."); }
        await using PlacementScope f = await PlacementScope.Create(); f.Write(PlacementScope.Document(First).ToJsonString());
        byte[] before = File.ReadAllBytes(f.StateFile); UnixFileMode original = File.GetUnixFileMode(f.StateFile);
        try
        {
            File.SetUnixFileMode(f.StateFile, UnixFileMode.UserRead);
            await CommandLogWindowPlacementState.InitializeRuntimeAsync(); CommandLogWindowPlacementState state = CommandLogWindowPlacementState.Open();
            Assert.That(state.Value, Is.EqualTo(First)); state.Save(Last); await CommandLogWindowPlacementState.DrainAsync();
            Assert.That(File.ReadAllBytes(f.StateFile), Is.EqualTo(before)); Assert.That(File.Exists(f.StateFile + ".lock"), Is.False);
            File.SetUnixFileMode(f.StateFile, UnixFileMode.None);
            Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllBytes(f.StateFile), "This test requires a real non-privileged unreadable-file precondition.");
            await CommandLogWindowPlacementState.InitializeRuntimeAsync(); CommandLogWindowPlacementState unreadable = CommandLogWindowPlacementState.Open();
            Assert.That(unreadable.Value, Is.Null); unreadable.Save(Last); await CommandLogWindowPlacementState.DrainAsync();
        }
        finally { File.SetUnixFileMode(f.StateFile, original); }
        Assert.That(File.ReadAllBytes(f.StateFile), Is.EqualTo(before)); f.AssertNoTemporaryFiles();
    }

    [Test]
    public async Task ADirectoryOrSymbolicLinkAtTheStatePathIsPreserved()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Symbolic-link evidence requires the Linux test host."); }
        await using PlacementScope f = await PlacementScope.Create(); Directory.CreateDirectory(f.StateFile);
        string child = Path.Combine(f.StateFile, "foreign.txt"); File.WriteAllText(child, "keep Ω");
        await CommandLogWindowPlacementState.InitializeRuntimeAsync(); CommandLogWindowPlacementState.Open().Save(Last);
        await CommandLogWindowPlacementState.DrainAsync(); Assert.That(Directory.Exists(f.StateFile), Is.True); Assert.That(File.ReadAllText(child), Is.EqualTo("keep Ω"));
        Directory.Delete(f.StateFile, true); string target = Path.Combine(f.Root, "linked-state.json");
        File.WriteAllText(target, PlacementScope.Document(First).ToJsonString()); byte[] before = File.ReadAllBytes(target);
        File.CreateSymbolicLink(f.StateFile, target);
        await CommandLogWindowPlacementState.InitializeRuntimeAsync(); CommandLogWindowPlacementState linked = CommandLogWindowPlacementState.Open();
        Assert.That(linked.Value, Is.Null); linked.Save(Last); await CommandLogWindowPlacementState.DrainAsync();
        Assert.That(new FileInfo(f.StateFile).LinkTarget, Is.EqualTo(target)); Assert.That(File.ReadAllBytes(target), Is.EqualTo(before));
        f.AssertNoTemporaryFiles();
    }

    [Test]
    public async Task ARealAdjacentLockPreventsReplacementAndAReleasedLockAllowsTheNextExplicitSave()
    {
        await using PlacementScope f = await PlacementScope.Create(); f.Write(PlacementScope.Document(First).ToJsonString());
        await CommandLogWindowPlacementState.InitializeRuntimeAsync(); CommandLogWindowPlacementState state = CommandLogWindowPlacementState.Open();
        byte[] before = File.ReadAllBytes(f.StateFile);
        using (FileStream claim = new(f.StateFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            state.Save(Last); await CommandLogWindowPlacementState.DrainAsync();
            Assert.That(File.ReadAllBytes(f.StateFile), Is.EqualTo(before)); f.AssertNoTemporaryFiles();
        }
        state.Save(Last); await CommandLogWindowPlacementState.DrainAsync(); f.AssertStored(Last); f.AssertNoTemporaryFiles();
    }

    [AvaloniaTest]
    public async Task TheOwnedQueueDrainIncludesEveryAcceptedSaveAndNeedsNoDispatcherContinuation()
    {
        await using PlacementScope f = await PlacementScope.Create(); f.Write(PlacementScope.Document(First).ToJsonString());
        await CommandLogWindowPlacementState.InitializeRuntimeAsync(); CommandLogWindowPlacementState state = CommandLogWindowPlacementState.Open();
        Task drain;
        // Hold the runtime's existing publication lock, not a replacement filesystem or writer.
        // This makes the real queued initialization wait before the following real SaveFile calls.
        lock (PlacementScope.RuntimeGate)
        {
            Task bootstrap = CommandLogWindowPlacementState.InitializeRuntimeAsync();
            state.Save(First with { X = 310 }); state.Save(Last); drain = CommandLogWindowPlacementState.DrainAsync();
            Assert.That(bootstrap.IsCompleted, Is.False); Assert.That(drain.IsCompleted, Is.False); f.AssertStored(First);
        }
        // Occupy the actual UI turn exactly like Program after its UI loop has ended.
        drain.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Assert.That(drain.IsCompletedSuccessfully, Is.True); f.AssertStored(Last); f.AssertNoTemporaryFiles();
        Assert.That(state.Value, Is.EqualTo(Last), "The session which accepted the writes must retain its last publication.");
        Assert.That(CommandLogWindowPlacementState.Open().Value, Is.EqualTo(Last), "The rebootstrap must not leave Open bound to its earlier disk snapshot.");
        await CommandLogWindowPlacementState.InitializeRuntimeAsync();
        Assert.That(CommandLogWindowPlacementState.Open().Value, Is.EqualTo(Last));
        GitCommandLogWindow reopened = await f.OpenLog();
        Assert.That(reopened.WindowState, Is.EqualTo(WindowState.Maximized));
        Assert.That(reopened.Width, Is.EqualTo(Last.Width)); Assert.That(reopened.Height, Is.EqualTo(Last.Height));
        Assert.That(reopened.Position, Is.EqualTo(new PixelPoint(105, 58)), "Saved DPI 2 restores the normal location on the current headless scale 1.");
    }
}

[NonParallelizable]
public sealed class CommandLogWindowPlacementTests
{
    [AvaloniaTest]
    public async Task AnEmbeddingHostWithoutBootstrapUsesTheOriginalClientSizeAndNeverBlindlyWritesState()
    {
        await using PlacementScope f = await PlacementScope.Create(); GitCommandLogWindow log = await f.OpenLog();
        Assert.That(log.ClientSize, Is.EqualTo(new Size(659, 470))); Assert.That(log.MinWidth, Is.EqualTo(400)); Assert.That(log.MinHeight, Is.EqualTo(240));
        Assert.That(log.WindowStartupLocation, Is.EqualTo(WindowStartupLocation.CenterOwner));
        await f.CloseLog(log); await CommandLogWindowPlacementState.DrainAsync(); Assert.That(Directory.Exists(f.StateHome), Is.False);
    }

    [AvaloniaTest]
    public async Task NormalBoundsPublishOnCloseAndImmediateReopenWithoutPersistingOptionsTabsOrSplitters()
    {
        await using PlacementScope f = await PlacementScope.Create(); await CommandLogWindowPlacementState.InitializeRuntimeAsync();
        GitCommandLogWindow log = await f.OpenLog(); Assert.That(File.Exists(f.StateFile), Is.False);
        log.Position = new PixelPoint(140, 95); log.Width = 800; log.Height = 520; PlacementScope.Pump(log);
        Field<CheckBox>(log, "chkAlwaysOnTop").IsChecked = true; Field<CheckBox>(log, "chkWordWrap").IsChecked = false;
        Field<TabControl>(log, "CommandLogTabs").SelectedIndex = 1; PlacementScope.Pump(log);
        Grid split = Field<Grid>(log, "CommandCacheSplit"); split.RowDefinitions[0].Height = new GridLength(80);
        Size normal = log.ClientSize; PixelPoint position = log.Position; double scaling = log.DesktopScaling;
        log.Close();
        Assert.That(CommandLogWindowPlacementState.Open().Value, Is.EqualTo(new CommandLogWindowPlacementValue(position.X, position.Y, normal.Width, normal.Height, scaling, false)));
        // No Drain between this close and reopening: publication must be immediately usable.
        GitCommandLogWindow reopened = await f.OpenLog();
        Assert.That(reopened.ClientSize, Is.EqualTo(normal)); Assert.That(reopened.Position, Is.EqualTo(position));
        Assert.That(reopened.WindowStartupLocation, Is.EqualTo(WindowStartupLocation.Manual));
        Assert.That(reopened.Topmost, Is.False); Assert.That(Field<CheckBox>(reopened, "chkWordWrap").IsChecked, Is.True);
        Assert.That(Field<TabControl>(reopened, "CommandLogTabs").SelectedIndex, Is.EqualTo(0));
        Field<TabControl>(reopened, "CommandLogTabs").SelectedIndex = 1; PlacementScope.Pump(reopened);
        Assert.That(Field<Grid>(reopened, "CommandCacheSplit").RowDefinitions[0].Height.IsStar, Is.True);
        await f.CloseLog(reopened); await CommandLogWindowPlacementState.DrainAsync();
        JsonObject entry = f.Read()[CommandLogWindowPlacementState.Key]!.AsObject();
        Assert.That(entry.Select(pair => pair.Key), Is.EquivalentTo(new[] { "X", "Y", "Width", "Height", "Scaling", "State" }));
    }

    [AvaloniaTest]
    public async Task APreviousDpiScalesPositionWhileClientDipsAreRestoredBeforeTheFirstMappingEventCompletes()
    {
        await using PlacementScope f = await PlacementScope.Create(); Screen screen = f.Screen();
        Assert.That(screen.Scaling, Is.EqualTo(1), "This is saved-DPI restoration against the fixed headless screen, not a real monitor transition.");
        Assert.That(screen.WorkingArea.Width, Is.GreaterThan(1200)); Assert.That(screen.WorkingArea.Height, Is.GreaterThan(700));
        Assert.That(screen.WorkingArea.X, Is.Zero); Assert.That(screen.WorkingArea.Y, Is.Zero);
        CommandLogWindowPlacementValue saved = new(400, 240, 700, 450, 2, false);
        f.Write(PlacementScope.Document(saved).ToJsonString()); await CommandLogWindowPlacementState.InitializeRuntimeAsync();
        GitCommandLogWindow log = f.NewLog(); (double Width, double Height, PixelPoint Position)? opening = null;
        log.AddHandler(Window.WindowOpenedEvent, (_, _) => opening = (log.Width, log.Height, log.Position));
        log.Show(); await f.Ready(log);
        Assert.That(opening, Is.EqualTo((700d, 450d, new PixelPoint(200, 120))));
        Assert.That(log.ClientSize, Is.EqualTo(new Size(700, 450))); Assert.That(log.Position, Is.EqualTo(new PixelPoint(200, 120)));
        await f.CloseLog(log); await CommandLogWindowPlacementState.DrainAsync();
        f.AssertStored(saved with { X = 200, Y = 120, Scaling = 1 });
    }

    [AvaloniaTest]
    [TestCase("offscreen")] [TestCase("title-above")] [TestCase("below-ten-percent")] [TestCase("partial-visible")] [TestCase("oversized")] [TestCase("smaller-than-minimum")]
    public async Task RestorationKeepsReachablePartialBoundsButRehomesInvisibleOrInaccessibleBounds(string kind)
    {
        await using PlacementScope f = await PlacementScope.Create(); Screen screen = f.Screen(); PixelRect area = screen.WorkingArea;
        Assert.That(screen.Scaling, Is.EqualTo(1)); Assert.That(area.Width, Is.GreaterThan(1200)); Assert.That(area.Height, Is.GreaterThan(700));
        CommandLogWindowPlacementValue saved = new(area.X + 60, area.Y + 40, 700, 450, 1, false);
        PixelPoint expected = new(area.X, area.Y);
        switch (kind)
        {
            case "offscreen": saved = saved with { X = area.Right + 10000 }; break;
            case "title-above": saved = saved with { Y = area.Y - 1 }; break;
            case "below-ten-percent": saved = saved with { X = area.Right - (int)Math.Ceiling(area.Width * .1) + 1 }; break;
            case "partial-visible": saved = saved with { X = area.X - 200 }; expected = new(saved.X, saved.Y); break;
            case "oversized": saved = saved with { Width = 100000, Height = 100000 }; expected = new(saved.X, saved.Y); break;
            case "smaller-than-minimum": saved = saved with { Width = 1, Height = 1 }; expected = new(saved.X, saved.Y); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        f.Write(PlacementScope.Document(saved).ToJsonString()); await CommandLogWindowPlacementState.InitializeRuntimeAsync();
        GitCommandLogWindow log = await f.OpenLog(); Assert.That(log.Position, Is.EqualTo(expected));
        Assert.That(log.ClientSize.Width, Is.EqualTo(kind == "oversized" ? area.Width : kind == "smaller-than-minimum" ? 400 : 700));
        Assert.That(log.ClientSize.Height, Is.EqualTo(kind == "oversized" ? area.Height : kind == "smaller-than-minimum" ? 240 : 450));
        Assert.That(log.ClientSize.Width, Is.GreaterThanOrEqualTo(log.MinWidth)); Assert.That(log.ClientSize.Height, Is.GreaterThanOrEqualTo(log.MinHeight));
    }

    [AvaloniaTest]
    [TestCase(WindowState.Maximized, "Maximized")] [TestCase(WindowState.Minimized, "Normal")] [TestCase(WindowState.FullScreen, "Normal")]
    public async Task ManagedNonNormalTransitionsSaveTheLastNormalBoundsAndOnlyMaximizedPersists(WindowState finalState, string savedState)
    {
        await using PlacementScope f = await PlacementScope.Create(); await CommandLogWindowPlacementState.InitializeRuntimeAsync();
        GitCommandLogWindow log = await f.OpenLog(); log.Width = 780; log.Height = 510; log.Position = new PixelPoint(110, 85); PlacementScope.Pump(log);
        Size normal = log.ClientSize; PixelPoint position = log.Position;
        log.WindowState = finalState; log.Position = new PixelPoint(3, 7); PlacementScope.Pump(log);
        await f.CloseLog(log); await CommandLogWindowPlacementState.DrainAsync();
        f.AssertStored(new(position.X, position.Y, normal.Width, normal.Height, 1, savedState == "Maximized"));
        Assert.That(f.Read()[CommandLogWindowPlacementState.Key]!["State"]!.GetValue<string>(), Is.EqualTo(savedState));
        GitCommandLogWindow reopened = await f.OpenLog();
        Assert.That(reopened.WindowState, Is.EqualTo(savedState == "Maximized" ? WindowState.Maximized : WindowState.Normal));
        Assert.That(reopened.Width, Is.EqualTo(normal.Width)); Assert.That(reopened.Height, Is.EqualTo(normal.Height));
    }

    [AvaloniaTest]
    public async Task ReactivatingTheLiveJournalKeepsItsCurrentBoundsAndFirstOwnerWhileClosingThatOwnerDrainsTheSnapshot()
    {
        await using PlacementScope f = await PlacementScope.Create(); await CommandLogWindowPlacementState.InitializeRuntimeAsync();
        Window first = f.Own(new Window { Width = 1000, Height = 700 }); first.Show();
        Window second = f.Own(new Window { Width = 900, Height = 650 }); second.Show();
        GitCommandLogWindow log = f.Own(GitCommandLogWindow.ShowOrActivate(first, PlacementScope.Translations)); await f.Ready(log);
        log.Width = 760; log.Height = 500; log.Position = new PixelPoint(170, 105); PlacementScope.Pump(log);
        Size normal = log.ClientSize; PixelPoint position = log.Position;
        GitCommandLogWindow same = GitCommandLogWindow.ShowOrActivate(second, PlacementScope.Translations);
        Assert.That(same, Is.SameAs(log)); Assert.That(same.Owner, Is.SameAs(first));
        Assert.That(same.ClientSize, Is.EqualTo(normal)); Assert.That(same.Position, Is.EqualTo(position));
        first.Close(); Assert.That(log.IsVisible, Is.False); Assert.That(first.OwnedWindows, Is.Empty);
        // The real owner close has queued its immutable snapshot. No Dispatcher work may be needed to drain it.
        CommandLogWindowPlacementState.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        f.AssertStored(new(position.X, position.Y, normal.Width, normal.Height, 1, false));
        GitCommandLogWindow reopened = f.Own(GitCommandLogWindow.ShowOrActivate(second, PlacementScope.Translations)); await f.Ready(reopened);
        Assert.That(reopened, Is.Not.SameAs(log)); Assert.That(reopened.Owner, Is.SameAs(second));
        Assert.That(reopened.ClientSize, Is.EqualTo(normal)); Assert.That(reopened.Position, Is.EqualTo(position));
    }

    [AvaloniaTest]
    public async Task ACancelledClosingDoesNotPublishAndTheFinalCloseCapturesTheLaterNormalBounds()
    {
        await using PlacementScope f = await PlacementScope.Create(); await CommandLogWindowPlacementState.InitializeRuntimeAsync();
        GitCommandLogWindow log = await f.OpenLog(); log.Width = 740; log.Height = 490; PlacementScope.Pump(log);
        void Cancel(object? sender, WindowClosingEventArgs args) => args.Cancel = true;
        log.Closing += Cancel;
        try
        {
            log.Close(); Assert.That(log.IsVisible, Is.True);
            await CommandLogWindowPlacementState.DrainAsync(); Assert.That(File.Exists(f.StateFile), Is.False);
        }
        finally { log.Closing -= Cancel; }
        log.Width = 820; log.Height = 550; log.Position = new PixelPoint(180, 120); PlacementScope.Pump(log);
        Size final = log.ClientSize; await f.CloseLog(log); await CommandLogWindowPlacementState.DrainAsync();
        f.AssertStored(new(180, 120, final.Width, final.Height, 1, false));
    }

    [AvaloniaTest]
    [TestCase(WindowState.Maximized)] [TestCase(WindowState.Minimized)]
    public async Task TheLastNormalMoveSurvivesAnImmediateNonNormalTransitionAndCloseWithoutPumping(WindowState finalState)
    {
        await using PlacementScope f = await PlacementScope.Create(); await CommandLogWindowPlacementState.InitializeRuntimeAsync();
        GitCommandLogWindow log = await f.OpenLog(); Size normal = log.ClientSize; double scaling = log.DesktopScaling;
        PixelPoint previous = log.Position, latest = new(310, 185); bool normalMoveNotified = false;
        log.PositionChanged += (_, args) => normalMoveNotified |= log.WindowState == WindowState.Normal && args.Point == latest;
        Assert.That(previous, Is.Not.EqualTo(latest), "The last move must differ from the initial normal snapshot.");
        log.Position = latest;
        Assert.That(normalMoveNotified, Is.True, "The real headless Move must have delivered PositionChanged while still Normal.");
        Assert.That(log.Position, Is.EqualTo(latest));
        // No Pump, dispatcher jobs, delay or await may intervene in this critical sequence.
        log.WindowState = finalState; log.Close();
        await CommandLogWindowPlacementState.DrainAsync();
        CommandLogWindowPlacementValue expected = new(latest.X, latest.Y, normal.Width, normal.Height, scaling, finalState == WindowState.Maximized);
        f.AssertStored(expected); Assert.That(CommandLogWindowPlacementState.Open().Value, Is.EqualTo(expected));
    }

    [AvaloniaTest]
    [TestCase(WindowState.Maximized)] [TestCase(WindowState.Minimized)]
    public async Task ADeliveredNormalClientResizeSurvivesImmediateNonNormalTransitionAndCloseWithoutPumping(WindowState finalState)
    {
        await using PlacementScope f = await PlacementScope.Create(); await CommandLogWindowPlacementState.InitializeRuntimeAsync();
        GitCommandLogWindow log = await f.OpenLog(); Size previous = log.ClientSize, latest = new(835, 545);
        PixelPoint position = log.Position; double scaling = log.DesktopScaling; bool normalResizeNotified = false;
        log.Resized += (_, args) => normalResizeNotified |= log.WindowState == WindowState.Normal && args.ClientSize == latest;
        Assert.That(previous, Is.Not.EqualTo(latest));
        object platform = log.PlatformImpl ?? throw new AssertionException("The real window platform must still be alive.");
        // ITopLevelImpl.Resized is not public API. HeadlessWindowImpl11.3.22 exposes
        // its real callback property; reflection only obtains the delegate already
        // wired by Window, without substituting a window implementation or handler.
        PropertyInfo? callbackProperty = platform.GetType().GetProperty("Resized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        object? callback = callbackProperty?.GetValue(platform);
        Assert.That(callback, Is.Not.Null, "The actual platform Resized callback must already be wired by Window.");
        Action<Size, WindowResizeReason> notify = (Action<Size, WindowResizeReason>)callback!;
        // Managed notification oracle; no native WM resize is claimed.
        notify(latest, WindowResizeReason.User);
        Assert.That(normalResizeNotified, Is.True); Assert.That(log.ClientSize, Is.EqualTo(latest));
        // The normal resize was already notified; no Pump is allowed before close.
        log.WindowState = finalState; log.Close();
        await CommandLogWindowPlacementState.DrainAsync();
        CommandLogWindowPlacementValue expected = new(position.X, position.Y, latest.Width, latest.Height, scaling, finalState == WindowState.Maximized);
        f.AssertStored(expected); Assert.That(CommandLogWindowPlacementState.Open().Value, Is.EqualTo(expected));
    }

    [AvaloniaTest]
    public async Task ActualF12KeepsTheLiveGeometryAndFirstOwnerThenRestoresTheSnapshotForTheNextOwner()
    {
        await using PlacementScope f = await PlacementScope.Create(); await CommandLogWindowPlacementState.InitializeRuntimeAsync();
        using MainWindowClassicTests.RepositoryFixture browser = await MainWindowClassicTests.RepositoryFixture.CreateAsync("base\n", "base\n");
        MainWindow first = browser.Window, second = f.Own(new MainWindow { Width = 1000, Height = 700 });
        second.Show(); await second.OpenRepositoryAsync(browser.Repository);
        await DetailedSettingsUiHost.Until(() => !first.IsBusy && !second.IsBusy);
        int secondF12 = 0;
        second.AddHandler(InputElement.KeyDownEvent, (_, args) => { if (args.Key == Key.F12) { secondF12++; } }, RoutingStrategies.Tunnel, handledEventsToo: true);
        try
        {
            PointAtHistory(first); PressF12(first);
            GitCommandLogWindow log = f.Own(first.OwnedWindows.OfType<GitCommandLogWindow>().Single()); await f.Ready(log);
            log.Width = 810; log.Height = 530; log.Position = new PixelPoint(160, 110); PlacementScope.Pump(log);
            Size normal = log.ClientSize; PixelPoint position = log.Position; double scaling = log.DesktopScaling;
            log.WindowState = WindowState.Minimized; second.Activate(); PointAtHistory(second); PressF12(second);
            Assert.That(secondF12, Is.EqualTo(1), "F12 must really route through the second browser.");
            Assert.That(first.OwnedWindows.OfType<GitCommandLogWindow>().Single(), Is.SameAs(log));
            Assert.That(second.OwnedWindows.OfType<GitCommandLogWindow>(), Is.Empty);
            Assert.That(log.Owner, Is.SameAs(first)); Assert.That(log.WindowState, Is.EqualTo(WindowState.Normal));
            Assert.That(log.Position, Is.EqualTo(position)); Assert.That(log.ClientSize, Is.EqualTo(normal));
            first.Close(); await DetailedSettingsUiHost.Until(() => !first.IsVisible && !log.IsVisible);
            await CommandLogWindowPlacementState.DrainAsync();
            f.AssertStored(new(position.X, position.Y, normal.Width, normal.Height, scaling, false));
            second.Activate(); PointAtHistory(second); PressF12(second);
            Assert.That(secondF12, Is.EqualTo(2));
            GitCommandLogWindow replacement = f.Own(second.OwnedWindows.OfType<GitCommandLogWindow>().Single()); await f.Ready(replacement);
            Assert.That(replacement, Is.Not.SameAs(log)); Assert.That(replacement.Owner, Is.SameAs(second));
            Assert.That(replacement.ClientSize, Is.EqualTo(normal)); Assert.That(replacement.Position, Is.EqualTo(position));
        }
        finally
        {
            second.Close(); first.Close();
            await DetailedSettingsUiHost.Until(() => !second.IsVisible && !first.IsVisible);
            await Task.WhenAll(first.CommitInformationCompletion, second.CommitInformationCompletion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        static void PointAtHistory(MainWindow window)
        {
            ListBox history = Field<ListBox>(window, "RevisionHistory"); object selected = history.SelectedItem ?? history.Items[0]!;
            history.ScrollIntoView(selected); PlacementScope.Pump(window);
            PushProcessTestDriver.Pointer((ListBoxItem)history.ContainerFromIndex(history.Items.IndexOf(selected))!);
            Assert.That(history.IsKeyboardFocusWithin, Is.True);
        }
        static void PressF12(MainWindow window)
        { window.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.F12, RawInputModifiers.None); PlacementScope.Pump(window); }
    }

    private static T Field<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
}

// A small fixture for real profile files and the real journal; it does not replace product IO or window APIs.
internal sealed class PlacementScope : IAsyncDisposable
{
    private static readonly FieldInfo Current = typeof(CommandLogWindowPlacementState).GetField("_current", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly FieldInfo? Profiles = typeof(CommandLogWindowPlacementState).GetField("Profiles", BindingFlags.NonPublic | BindingFlags.Static);
    internal static object RuntimeGate { get; } = typeof(CommandLogWindowPlacementState).GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    internal static TranslationCatalog Translations => TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo("en"));
    private readonly string? _oldState = Environment.GetEnvironmentVariable("XDG_STATE_HOME"), _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private readonly GitCommandLogCapturePolicy _oldCapture = GitCommandLog.CapturePolicy;
    private readonly object? _oldProfile;
    private readonly Dictionary<object, object?>? _oldProfiles;
    private readonly List<Window> _windows = [];
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-window-placement-test-" + Guid.NewGuid().ToString("N"));
    internal string ConfigHome => Path.Combine(Root, "config");
    internal string StateHome { get; private set; }
    internal string StateFile => Path.Combine(StateHome, "gitextensions", "window-placements.json");
    private PlacementScope()
    {
        lock (RuntimeGate)
        {
            _oldProfile = Current.GetValue(null);
            if (Profiles is null) { _oldProfiles = null; }
            else
            {
                IDictionary profiles = (IDictionary)Profiles.GetValue(null)!;
                _oldProfiles = new Dictionary<object, object?>();
                foreach (object key in profiles.Keys) { _oldProfiles.Add(key, profiles[key]); }
            }
        }
        StateHome = Path.Combine(Root, "state-a");
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", StateHome); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", ConfigHome);
    }
    internal static async Task<PlacementScope> Create()
    { await CommandLogWindowPlacementState.DrainAsync(); return new(); }
    internal void SelectProfile(string relative)
    { StateHome = Path.Combine(Root, relative); Environment.SetEnvironmentVariable("XDG_STATE_HOME", StateHome); }
    internal void Write(string text)
    { Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!); File.WriteAllText(StateFile, text, new UTF8Encoding(false)); }
    internal JsonObject Read(string? path = null) => JsonNode.Parse(File.ReadAllBytes(path ?? StateFile))!.AsObject();
    internal static JsonObject Document(CommandLogWindowPlacementValue value) => new()
    {
        [CommandLogWindowPlacementState.Key] = new JsonObject
        {
            ["X"] = value.X, ["Y"] = value.Y, ["Width"] = value.Width, ["Height"] = value.Height,
            ["Scaling"] = value.Scaling, ["State"] = value.Maximized ? "Maximized" : "Normal"
        }
    };
    internal void AssertStored(CommandLogWindowPlacementValue value, string? path = null)
    {
        JsonObject entry = Read(path)[CommandLogWindowPlacementState.Key]!.AsObject();
        Assert.Multiple(() =>
        {
            Assert.That(entry["X"]!.GetValue<int>(), Is.EqualTo(value.X)); Assert.That(entry["Y"]!.GetValue<int>(), Is.EqualTo(value.Y));
            Assert.That(entry["Width"]!.GetValue<double>(), Is.EqualTo(value.Width)); Assert.That(entry["Height"]!.GetValue<double>(), Is.EqualTo(value.Height));
            Assert.That(entry["Scaling"]!.GetValue<double>(), Is.EqualTo(value.Scaling));
            Assert.That(entry["State"]!.GetValue<string>(), Is.EqualTo(value.Maximized ? "Maximized" : "Normal"));
        });
    }
    internal void AssertNoTemporaryFiles()
    { Assert.That(Directory.Exists(Root) ? Directory.GetFiles(Root, "*.tmp", SearchOption.AllDirectories) : [], Is.Empty); }
    internal T Own<T>(T window) where T : Window { _windows.Add(window); return window; }
    internal GitCommandLogWindow NewLog() => Own(new GitCommandLogWindow(Translations));
    internal async Task<GitCommandLogWindow> OpenLog()
    { GitCommandLogWindow log = NewLog(); log.Show(); await Ready(log); return log; }
    internal async Task Ready(GitCommandLogWindow log)
    { await log.Work.WaitAsync(TimeSpan.FromSeconds(10)); Pump(log); Assert.That(log.IsVisible, Is.True); Assert.That(log.IsBusy, Is.False); }
    internal async Task CloseLog(GitCommandLogWindow log)
    { log.Close(); await log.Work.WaitAsync(TimeSpan.FromSeconds(10)); Dispatcher.UIThread.RunJobs(); Assert.That(log.IsVisible, Is.False); }
    internal Screen Screen()
    {
        Window probe = Own(new Window());
        Screen? screen = probe.Screens.Primary ?? probe.Screens.All.FirstOrDefault();
        Assert.That(screen, Is.Not.Null, "The existing Headless platform must expose a screen for restoration tests.");
        return screen!;
    }
    internal static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); if (window.IsVisible) { window.UpdateLayout(); } AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (Window window in _windows.AsEnumerable().Reverse())
            { window.Close(); if (window is GitCommandLogWindow log) { await log.Work.WaitAsync(TimeSpan.FromSeconds(10)); } }
            await CommandLogWindowPlacementState.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            lock (RuntimeGate)
            {
                Current.SetValue(null, _oldProfile);
                if (Profiles is not null && _oldProfiles is not null)
                {
                    IDictionary profiles = (IDictionary)Profiles.GetValue(null)!; profiles.Clear();
                    foreach ((object key, object? value) in _oldProfiles) { profiles.Add(key, value); }
                }
                // Product's global save sequence is monotone and deliberately not reset.
            }
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", _oldState); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
            GitCommandLog.ConfigureCapture(_oldCapture);
            if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
        }
    }
}

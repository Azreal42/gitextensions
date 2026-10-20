using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class HelpImagesPreferencesTests
{
    [Test]
    public void TheWindowsFalseDefaultAndAnUntouchedSessionDoNotCreateAnyProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), "help-image-default-" + Guid.NewGuid().ToString("N"));
        try
        {
            PreferencesStore store = new(root); AdvancedSettingsSession session = new(Path.GetDirectoryName(store.FilePath));
            Assert.That(store.Load().DontShowHelpImages, Is.False); Assert.That(session.Initial.DontShowHelpImages, Is.False);
            session.Save(session.Initial); Assert.That(Directory.Exists(root), Is.False);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [AvaloniaTest]
    [TestCase("en", false, true)] [TestCase("fr", true, false)]
    public async Task TheRealAdvancedChoiceAppliesToAllThreeCommandsAndCancelPreservesTheSavedLocalHelpStates(string language, bool dark, bool locallyExpanded)
    {
        using OptionalLocksDisabled locks = new(); using RebaseUiFixture git = await RebaseUiFixture.Create();
        await git.Git("remote", "add", "origin", git.Root);
        using PortableHelperSettingsFixture f = new(language, dark); PreferencesStore store = new();
        WriteProfile(store.FilePath, "{\"DontShowHelpImages\":false,\"CheckForUpdates\":false,\"future\":{\"literal\":[17,null,\"Ω\"]}}");
        string helpPath = LocalHelpPath(); WriteProfile(helpPath, LocalHelp(locallyExpanded, mixed: true)); byte[] localBefore = File.ReadAllBytes(helpPath);
        using StateScope state = new(Path.Combine(Path.GetDirectoryName(store.FilePath)!, "isolated-state"));
        string witness = await git.Witness(); byte[] index = File.ReadAllBytes(git.File(".git/index"));
        Dictionary<string, (double Width, double Left)> visible = [];
        foreach (string command in Commands)
        {
            RepositoryDialog dialog = await Open(git, f.Owner, command, dark);
            Assert.That(Help(dialog).IsExpanded, Is.EqualTo(command == "Merge" ? !locallyExpanded : locallyExpanded)); visible[command] = Measure(dialog, command);
            AssertActionsInside(dialog, command);
            Close(dialog);
        }
        byte[] original = File.ReadAllBytes(store.FilePath);
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Advanced", initial: store.Load());
        CheckBox choice = AdvancedSettingsUiDriver.Field<CheckBox>(settings, "chkDontSHowHelpImages");
        Assert.That(((TextBlock)choice.Content!).Text, Is.EqualTo(f.Original("AdvancedSettingsPage", "chkDontSHowHelpImages.Text")));
        Assert.That(choice.IsChecked, Is.False); AdvancedSettingsUiDriver.Set(settings, "chkDontSHowHelpImages", true);
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(original));
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await completion, Is.False);
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(original)); Assert.That(f.Publications, Is.Zero);

        (settings, completion) = f.Open("Advanced", initial: store.Load()); AdvancedSettingsUiDriver.Set(settings, "chkDontSHowHelpImages", true);
        await AdvancedSettingsUiDriver.Apply(f, settings, 1);
        Assert.That(store.Load().DontShowHelpImages, Is.True); Assert.That(settings.Result.DontShowHelpImages, Is.True);
        byte[] saved = File.ReadAllBytes(store.FilePath); AdvancedSettingsUiDriver.Set(settings, "chkDontSHowHelpImages", false);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await completion, Is.True);
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(saved));
        foreach (string command in Commands)
        {
            RepositoryDialog dialog = await Open(git, f.Owner, command, dark); AssertHidden(dialog);
            (double width, double left) = Measure(dialog, command);
            Assert.That(width, Is.LessThanOrEqualTo(visible[command].Width), command);
            Assert.That(left, Is.LessThan(visible[command].Left - .5), "No Show-help column or spacer may remain: " + command);
            AssertActionsInside(dialog, command);
            PortableHelperSettingsFixture.Capture(dialog, "help-globally-hidden-" + command + "-" + language + (dark ? "-dark.png" : "-light.png"));
            Close(dialog);
        }
        Assert.That(File.ReadAllBytes(helpPath), Is.EqualTo(localBefore)); Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(saved)); state.Unchanged();
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(saved)!["future"], JsonNode.Parse("{\"literal\":[17,null,\"Ω\"]}")), Is.True);
        (settings, completion) = f.Open("Advanced", initial: store.Load());
        Assert.That(AdvancedSettingsUiDriver.Field<CheckBox>(settings, "chkDontSHowHelpImages").IsChecked, Is.True);
        AdvancedSettingsUiDriver.Set(settings, "chkDontSHowHelpImages", false); await AdvancedSettingsUiDriver.Apply(f, settings, 2);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); await completion;
        foreach (string command in Commands)
        {
            RepositoryDialog dialog = await Open(git, f.Owner, command, dark); Assert.That(Help(dialog).IsExpanded, Is.EqualTo(command == "Merge" ? !locallyExpanded : locallyExpanded)); Close(dialog);
        }
        Assert.That(File.ReadAllBytes(helpPath), Is.EqualTo(localBefore)); state.Unchanged();
        Assert.That(await git.Witness(), Is.EqualTo(witness)); Assert.That(File.ReadAllBytes(git.File(".git/index")), Is.EqualTo(index));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task PullCapturesItsChoiceOnceAcrossRealModeChangesAndANewDialogReadsTheNewChoice(bool initiallyHidden)
    {
        using OptionalLocksDisabled locks = new(); using RebaseUiFixture git = await RebaseUiFixture.Create();
        await git.Git("remote", "add", "origin", git.Root); PreferencesStore store = new(); store.Save(new() { DontShowHelpImages = initiallyHidden });
        string helpPath = LocalHelpPath(); WriteProfile(helpPath, LocalHelp(true)); byte[] localBefore = File.ReadAllBytes(helpPath);
        string witness = await git.Witness();
        PullDialog first = (PullDialog)await Open(git, git.Owner, "Pull", false); double width = first.Width;
        store.Save(store.Load() with { DontShowHelpImages = !initiallyHidden }); byte[] preferences = File.ReadAllBytes(store.FilePath);
        foreach (string mode in new[] { "PullMerge", "PullRebase", "PullFetch", "PullRebase", "PullMerge", "PullFetch" })
        {
            PushProcessTestDriver.Pointer(Field<RadioButton>(first, mode)); PortableHelperSettingsFixture.Pump(first);
            if (initiallyHidden) { AssertHidden(first); } else { Assert.That(Help(first).IsExpanded, Is.True); }
            Assert.That(first.Width, Is.EqualTo(width).Within(.5), "Mode changes must not accumulate a help-column width delta.");
            AssertActionsInside(first, "Pull");
        }
        Close(first); PullDialog reopened = (PullDialog)await Open(git, git.Owner, "Pull", false);
        if (initiallyHidden) { Assert.That(Help(reopened).IsExpanded, Is.True); } else { AssertHidden(reopened); }
        Close(reopened); Assert.That(File.ReadAllBytes(helpPath), Is.EqualTo(localBefore)); Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(preferences));
        Assert.That(await git.Witness(), Is.EqualTo(witness));
    }

    [AvaloniaTest]
    public async Task TheRealPullRecoveryChildCapturesItsOwnCurrentPreferenceAndCancelKeepsTheNativeRebase()
    {
        using OptionalLocksDisabled locks = new(); using RebaseUiFixture git = await RebaseUiFixture.Create(conflict: true);
        PreferencesStore store = new(); store.Save(new() { DontShowHelpImages = false });
        string helpPath = LocalHelpPath(); WriteProfile(helpPath, LocalHelp(true)); byte[] localBefore = File.ReadAllBytes(helpPath);
        await git.StartConflict(); string witness = await git.Witness(); byte[] index = File.ReadAllBytes(git.File(".git/index"));
        string journalPath = git.File(".git/gitextensions-rebase-native/state.json"); byte[] journal = File.ReadAllBytes(journalPath);
        PullDialog parent = (PullDialog)await Open(git, git.Owner, "Pull", false); Assert.That(Help(parent).IsExpanded, Is.True);
        store.Save(store.Load() with { DontShowHelpImages = true }); byte[] preferences = File.ReadAllBytes(store.FilePath);
        PushProcessTestDriver.Pointer(Field<Button>(parent, "ResolvePullConflict"));
        await PushProcessTestDriver.UntilAsync(parent, () => parent.OwnedWindows.OfType<RebaseDialog>().Any());
        RebaseDialog child = parent.OwnedWindows.OfType<RebaseDialog>().Single();
        await PushProcessTestDriver.UntilAsync(child, () => !child.IsBusy && child.State?.IsActive == true); PortableHelperSettingsFixture.Pump(child);
        AssertHidden(child); Assert.That(Help(parent).IsExpanded, Is.True, "An already-open parent keeps its captured policy.");
        child.Width = child.MinWidth; child.Height = child.MinHeight; PortableHelperSettingsFixture.Pump(child); AssertActionsInside(child, "Rebase");
        Assert.That(Field<ListBox>(child, "RebasePatches").Bounds.Height, Is.GreaterThanOrEqualTo(26), "At least one patch row must remain usable without the help column.");
        PushProcessTestDriver.Pointer(Field<Button>(child, "CancelRebase"));
        await PushProcessTestDriver.UntilAsync(parent, () => !child.IsVisible && !parent.IsBusy); Close(parent);
        Assert.That(child.Succeeded || child.Aborted, Is.False); Assert.That((await git.Repository.GetRebaseStateAsync()).IsActive, Is.True);
        Assert.That(await git.Witness(), Is.EqualTo(witness)); Assert.That(File.ReadAllBytes(git.File(".git/index")), Is.EqualTo(index));
        Assert.That(File.ReadAllBytes(journalPath), Is.EqualTo(journal)); Assert.That(File.ReadAllBytes(helpPath), Is.EqualTo(localBefore));
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(preferences));
    }

    [AvaloniaTest]
    [TestCase("{broken literal")]
    [TestCase("{\"DontShowHelpImages\":\"true\",\"future\":[17,null]}")]
    public async Task AnUnreadableCosmeticProfileDoesNotBlockTheActualCommandsOrRepairAnyProfile(string raw)
    {
        using OptionalLocksDisabled locks = new(); using RebaseUiFixture git = await RebaseUiFixture.Create(); PreferencesStore store = new();
        WriteProfile(store.FilePath, raw); byte[] preferences = File.ReadAllBytes(store.FilePath);
        string helpPath = LocalHelpPath(); WriteProfile(helpPath, LocalHelp(true)); byte[] local = File.ReadAllBytes(helpPath);
        string witness = await git.Witness();
        foreach (string command in Commands)
        { RepositoryDialog dialog = await Open(git, git.Owner, command, false); Assert.That(Help(dialog).IsExpanded, Is.True); Close(dialog); }
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(preferences)); Assert.That(File.ReadAllBytes(helpPath), Is.EqualTo(local));
        Assert.That(await git.Witness(), Is.EqualTo(witness));
        AdvancedSettingsSession session = new(); Assert.That(session.LoadErrors.Keys, Does.Contain("preferences.json"));
        Assert.Throws<InvalidDataException>(() => session.Save(session.Initial with { DontShowHelpImages = true }));
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(preferences));
    }

    [AvaloniaTest]
    public async Task GeneralTypedPublicationDefersTheHelpDraftUntilItsAdvancedReceiptAndKeepsForeignData()
    {
        using PortableHelperSettingsFixture f = new(); PreferencesStore store = new();
        WriteProfile(store.FilePath, "{\"DontShowHelpImages\":false,\"CheckForUpdates\":false,\"future\":{\"literal\":[17,null,\"Ω\"]}}");
        AdvancedSettingsSession advanced = new(initialDesktop: store.Load()); int publications = 0;
        DesktopSettingsDialog settings = new(store.Load(), f.Translations, false, fileHistoryPreferences: new(), hotkeys: new(), fonts: new(), scripts: f.Scripts.Load(),
            publishSettings: dialog =>
            {
                publications++; DesktopPreferences general = advanced.MergeDesktop(dialog.Result);
                Assert.That(general.DontShowHelpImages, Is.False); store.Save(general); return Task.CompletedTask;
            }, initialPage: "General", advancedSettings: advanced);
        f.Track(settings); Task<bool> completion = settings.ShowDialog<bool>(f.Owner); PortableHelperSettingsFixture.Pump(settings);
        AdvancedSettingsUiDriver.Set(settings, "chkStartWithRecentWorkingDir", true); SettingsNavigationTestDriver.Select(settings, "Advanced");
        AdvancedSettingsUiDriver.Set(settings, "chkDontSHowHelpImages", true); PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => publications == 1 && settings.Navigation.IsEnabled);
        Assert.That(AdvancedSettingsUiDriver.Field<TextBlock>(settings, "SettingsNotice").Text, Is.Null.Or.Empty);
        Assert.That(store.Load().StartWithRecentWorkingDir && store.Load().DontShowHelpImages, Is.True); Assert.That(store.Load().CheckForUpdates, Is.False);
        Assert.That(settings.Result.DontShowHelpImages, Is.True);
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllBytes(store.FilePath))!["future"], JsonNode.Parse("{\"literal\":[17,null,\"Ω\"]}")), Is.True);
        byte[] saved = File.ReadAllBytes(store.FilePath);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await completion, Is.True);
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(saved));
    }

    private static readonly string[] Commands = ["Pull", "Merge", "Rebase"];
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static OriginalCommandHelp Help(TopLevel window) => window.GetVisualDescendants().OfType<OriginalCommandHelp>().Single();
    private static string LocalHelpPath() => Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "command-help-preferences.json");
    private static string LocalHelp(bool expanded, bool mixed = false) => new JsonObject { ["Pull"] = expanded, ["MergeBranches"] = mixed ? !expanded : expanded, ["Rebase"] = expanded, ["future"] = true }.ToJsonString() + "\n";
    private static void WriteProfile(string path, string contents) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, contents); }
    private static async Task<RepositoryDialog> Open(RebaseUiFixture f, Window owner, string command, bool dark)
    {
        RepositoryDialog dialog = command switch
        {
            "Pull" => new PullDialog(f.Repository, GitPullAction.Fetch, "origin"),
            "Merge" => new MergeBranchDialog(f.Repository, "main"),
            "Rebase" => new RebaseDialog(f.Repository, "main"),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        dialog.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        f.Windows.Add(dialog); await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
        _ = dialog.ShowDialog(owner); PortableHelperSettingsFixture.Pump(dialog); return dialog;
    }
    private static (double Width, double Left) Measure(TopLevel dialog, string command)
    {
        Control input = Field<Control>(dialog, command switch { "Pull" => "PullSourceGroup", "Merge" => "MergeRevision", _ => "RebaseCurrentBranch" });
        return (dialog.Width, input.TranslatePoint(default, dialog)!.Value.X);
    }
    private static void AssertHidden(TopLevel window)
    {
        Assert.That(window.GetVisualDescendants().OfType<OriginalCommandHelp>(), Is.Empty);
        Assert.That(window.GetVisualDescendants().OfType<Control>().Any(control => control.Name is "OriginalCommandHelpToggle" or "OriginalCommandHelpImage" or "OriginalCommandHelpNotice"), Is.False);
    }
    private static void AssertActionsInside(TopLevel window, string command)
    {
        string[] actions = command switch
        {
            "Pull" => ["ResolvePullConflict", "PullStashChanges", "PullAutoStash", "ExecutePull"],
            "Merge" => ["ExecuteMerge", "CancelMerge"],
            _ when window is RebaseDialog { State.IsActive: true, State.HasConflicts: true } => ["ResolveRebaseConflicts", "AbortRebase", "CancelRebase"],
            _ when window is RebaseDialog { State.IsActive: true } => ["ContinueRebase", "AbortRebase", "CancelRebase"],
            _ => ["ExecuteRebase", "CancelRebase"]
        };
        foreach (string name in actions)
        {
            Control action = Field<Control>(window, name); Point origin = action.TranslatePoint(default, window)!.Value;
            Assert.That(action.IsEffectivelyVisible, Is.True, name); Assert.That(origin.X, Is.GreaterThanOrEqualTo(-.5), name);
            Assert.That(origin.Y, Is.GreaterThanOrEqualTo(-.5), name);
            Assert.That(origin.X + action.Bounds.Width, Is.LessThanOrEqualTo(window.ClientSize.Width + .5), name);
            Assert.That(origin.Y + action.Bounds.Height, Is.LessThanOrEqualTo(window.ClientSize.Height + .5), name);
        }
        if (command == "Pull")
        {
            Grid body = Field<Grid>(window, "PullLayout"), footer = Field<Grid>(window, "PullFooter");
            Assert.That(body.ColumnDefinitions.Count, Is.EqualTo(footer.ColumnDefinitions.Count));
            Avalonia.Controls.Primitives.AccessText stashCaption = (Avalonia.Controls.Primitives.AccessText)Field<Button>(window, "PullStashChanges").Content!;
            Assert.That(stashCaption.TextLayout.TextLines.Count, Is.EqualTo(1), "A one-line button must show the entire translated stash caption.");
            Assert.That(stashCaption.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False);
            Assert.That(stashCaption.Bounds.Width, Is.GreaterThanOrEqualTo(stashCaption.TextLayout.Width - .5));
            Assert.That(stashCaption.Bounds.Height, Is.GreaterThanOrEqualTo(stashCaption.TextLayout.Height - .5));
            double sourceLeft = Field<Control>(window, "PullSourceGroup").TranslatePoint(default, window)!.Value.X;
            double actionLeft = Field<Button>(window, "ResolvePullConflict").TranslatePoint(default, window)!.Value.X;
            Assert.That(actionLeft, Is.EqualTo(sourceLeft).Within(1), "The footer must follow the options after removing the help spacer.");
        }
    }
    private static void Close(Window window) { window.Close(); PortableHelperSettingsFixture.Pump(window); Assert.That(window.IsVisible, Is.False); }
    private sealed class OptionalLocksDisabled : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("GIT_OPTIONAL_LOCKS");
        internal OptionalLocksDisabled() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        public void Dispose() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", _previous);
    }
    private sealed class StateScope : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        private readonly string _path;
        private readonly byte[] _bytes;
        internal StateScope(string root)
        {
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", root); _path = Path.Combine(root, "gitextensions", "update-check.json");
            WriteProfile(_path, "{ \"LastUpdateCheckUtc\": \"2026-10-20T10:00:00.0000000+00:00\", \"future\": [17,null] }\n"); _bytes = File.ReadAllBytes(_path);
        }
        internal void Unchanged() => Assert.That(File.ReadAllBytes(_path), Is.EqualTo(_bytes));
        public void Dispose() => Environment.SetEnvironmentVariable("XDG_STATE_HOME", _previous);
    }
}

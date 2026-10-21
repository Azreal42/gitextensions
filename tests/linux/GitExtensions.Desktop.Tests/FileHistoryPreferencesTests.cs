using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;
using HistoryHelpers = GitExtensions.Desktop.Tests.FileHistoryDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FileHistoryPreferencesTests
{
    [Test]
    public void OriginalDefaultsAndUnknownFieldsRoundTripSeparatelyFromGeneralAndViewPreferences()
    {
        string root = Path.Combine(Path.GetTempPath(), "gitextensions-file-history-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            FileHistoryPreferencesStore store = new(root);
            FileHistoryPreferences defaults = store.Load();
            Assert.That(defaults.FollowRenames, Is.True);
            Assert.That(defaults.ExactRenames || defaults.FullHistory || defaults.SimplifyMerges, Is.False);
            PreferencesStore general = new(root); general.Save(new() { Theme = "Dark", UseBrowseForFileHistory = false, HistoryLimit = 27 });
            byte[] generalBytes = File.ReadAllBytes(general.FilePath);
            File.WriteAllText(store.FilePath, """{"FollowRenames":false,"ExactRenames":true,"FullHistory":false,"SimplifyMerges":true,"FutureOption":{"Enabled":true}}""");
            FileHistoryPreferences retained = store.Load();
            Assert.That(retained.FollowRenames, Is.False); Assert.That(retained.ExactRenames && retained.SimplifyMerges, Is.True);
            store.Save(retained with { FullHistory = true });
            FileHistoryPreferences loaded = new FileHistoryPreferencesStore(root).Load();
            Assert.That(loaded.ExactRenames && loaded.FullHistory && loaded.SimplifyMerges, Is.True);
            Assert.That(loaded.FollowRenames, Is.False);
            Assert.That(JsonDocument.Parse(File.ReadAllText(store.FilePath)).RootElement.GetProperty("FutureOption").GetProperty("Enabled").GetBoolean(), Is.True);
            Assert.That(File.ReadAllBytes(general.FilePath), Is.EqualTo(generalBytes));
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [AvaloniaTest]
    public async Task GeneralCancelDoesNotWriteAndOkReloadsTheActualRenameFilterWithoutClearingExact()
    {
        using Fixture f = await Fixture.CreateAsync();
        (string path, string old) = await RenameAsync(f, edited: true);
        MainWindow main = new(new BrowseRequest(PathFilter: new(path, BrowsePathKind.File), SelectedRevision: new(old)));
        main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Repository.Path); await Idle(main);
            ListBox list = Field<ListBox>(main, "RevisionHistory");
            Assert.That(list.Items.OfType<GitCommit>().Select(c => c.Hash), Does.Contain(old));
            object? originalRows = list.ItemsSource;
            FileHistoryPreferencesStore store = new();
            DesktopSettingsDialog settings = await OpenSettings(main);
            CheckBox follow = Field<CheckBox>(settings, "chkFollowRenamesInFileHistory"), exact = Field<CheckBox>(settings, "chkFollowRenamesInFileHistoryExact");
            PushProcessTestDriver.Pointer(follow); PushProcessTestDriver.Pointer(exact);
            Assert.That(exact.IsEffectivelyEnabled, Is.True, "Windows General leaves Exact editable while Follow is off.");
            PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await Idle(main);
            Assert.That(File.Exists(store.FilePath), Is.False); Assert.That(list.ItemsSource, Is.SameAs(originalRows));

            settings = await OpenSettings(main);
            PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "chkFollowRenamesInFileHistory"));
            PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "chkFollowRenamesInFileHistoryExact"));
            long lastCommandIdBeforeSave = GitCommandLog.Snapshot().LastOrDefault()?.Id ?? 0;
            PushProcessTestDriver.Pointer(Field<Button>(settings, "SaveDesktopSettings"));
            await Until(() => GitCommandLog.Snapshot().Any(entry => entry.Id > lastCommandIdBeforeSave && entry.Command == "git log")
                && !main.IsBusy && main.WindowWorkCompletion.IsCompleted);
            await Idle(main);
            Assert.That(store.Load().FollowRenames, Is.False); Assert.That(store.Load().ExactRenames, Is.True);
            Assert.That(list.Items.OfType<GitCommit>().Select(c => c.Subject), Is.EquivalentTo(new[] { "After rename", "Edited rename" }));
            Assert.That(list.ItemsSource, Is.SameAs(originalRows));

            settings = await OpenSettings(main);
            Assert.That(Field<CheckBox>(settings, "chkFollowRenamesInFileHistoryExact").IsChecked, Is.True);
            PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "chkFollowRenamesInFileHistory"));
            PushProcessTestDriver.Pointer(Field<Button>(settings, "SaveDesktopSettings")); await Idle(main);
            Assert.That(list.Items.OfType<GitCommit>().Select(c => c.Hash), Does.Not.Contain(old), "The edited rename must not pass the exact100% threshold.");

            settings = await OpenSettings(main);
            PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "chkFollowRenamesInFileHistoryExact"));
            PushProcessTestDriver.Pointer(Field<Button>(settings, "SaveDesktopSettings")); await Idle(main);
            Assert.That(list.Items.OfType<GitCommit>().Select(c => c.Hash), Does.Contain(old));
            Assert.That(store.Load().FollowRenames, Is.True); Assert.That(store.Load().ExactRenames, Is.False);
        }
        finally { await Close(main); }
    }

    [AvaloniaTest]
    public async Task DedicatedOptionsUseOriginalMenusReloadImmediatelyAndReturnTheFirstArrowToTheGrid()
    {
        using Fixture f = await Fixture.CreateAsync();
        (string path, _) = await RenameAsync(f, edited: false);
        FileHistoryPreferencesStore store = new(); store.Save(new() { ExactRenames = true, SimplifyMerges = true });
        FileHistoryDialog dialog = new(f.Repository, path); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await HistoryHelpers.Idle(dialog); Pump(dialog);
            ListBox grid = Field<ListBox>(dialog, "FileHistoryRevisions");
            MenuItem follow = HistoryHelpers.Option(dialog, "followFileHistoryToolStripMenuItem"), exact = HistoryHelpers.Option(dialog, "followFileHistoryRenamesToolStripMenuItem");
            MenuItem full = HistoryHelpers.Option(dialog, "showFullHistoryToolStripMenuItem"), simplify = HistoryHelpers.Option(dialog, "simplifyMergesToolStripMenuItem");
            Assert.That(grid.ContextMenu!.Items.OfType<MenuItem>().Select(i => i.Name), Is.EqualTo(new[] { "followFileHistoryToolStripMenuItem", "followFileHistoryRenamesToolStripMenuItem" }));
            Assert.That(new[] { follow, exact, full, simplify }.All(i => i.Icon is null && i.InputGesture is null && i.ToggleType == MenuItemToggleType.CheckBox), Is.True);
            Assert.That(exact.IsChecked && exact.IsEnabled && simplify.IsChecked, Is.True); Assert.That(simplify.IsEnabled, Is.False);
            Button traversal = Field<Button>(dialog, "ShowFullHistory");
            Assert.That(traversal.GetVisualDescendants().OfType<Image>().Single().Tag, Is.EqualTo("FileHistory.png"));
            PathIcon arrow = Field<PathIcon>(dialog, "FileHistoryOptionsDropdownArrow");
            Assert.That((arrow.Width, arrow.Height), Is.EqualTo((6d, 3d)));
            string selected = dialog.SelectedEntry!.Commit.Hash;
            byte[] index = File.ReadAllBytes(f.PathInRepository(".git/index"));
            string refs = (await f.GitAsync("show-ref")).StandardOutput;

            grid.ContextMenu.Open(grid); Pump(dialog);
            PushProcessTestDriver.Pointer(follow); await HistoryHelpers.Idle(dialog); Pump(dialog);
            Assert.That(store.Load().FollowRenames, Is.False); Assert.That(store.Load().ExactRenames, Is.True);
            Assert.That(exact.IsChecked, Is.True); Assert.That(exact.IsEnabled, Is.False);
            Assert.That(dialog.History.Select(e => e.Commit.Subject), Is.EquivalentTo(new[] { "After rename", "Exact rename" }));
            Assert.That(dialog.SelectedEntry!.Commit.Hash, Is.EqualTo(selected));
            Assert.That(grid.IsKeyboardFocusWithin, Is.True);
            int before = grid.SelectedIndex; Press(dialog, PhysicalKey.ArrowDown);
            await HistoryHelpers.Idle(dialog);
            Assert.That(grid.SelectedIndex, Is.EqualTo(before + 1)); Assert.That(grid.IsKeyboardFocusWithin, Is.True);

            PushProcessTestDriver.Pointer(traversal); Pump(dialog);
            PushProcessTestDriver.Pointer(full); await HistoryHelpers.Idle(dialog); Pump(dialog);
            Assert.That(store.Load().FullHistory && store.Load().SimplifyMerges, Is.True);
            Assert.That(simplify.IsEnabled && simplify.IsChecked, Is.True);
            PushProcessTestDriver.Pointer(traversal); Pump(dialog);
            PushProcessTestDriver.Pointer(full); await HistoryHelpers.Idle(dialog); Pump(dialog);
            Assert.That(store.Load().FullHistory, Is.False); Assert.That(store.Load().SimplifyMerges, Is.True);
            Assert.That(simplify.IsChecked, Is.True); Assert.That(simplify.IsEnabled, Is.False);
            Assert.That(File.ReadAllBytes(f.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That((await f.GitAsync("show-ref")).StandardOutput, Is.EqualTo(refs));
            Assert.That((await f.Repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { await Close(dialog); }

        FileHistoryDialog reopened = new(f.Repository, path); reopened.Show();
        try
        {
            await reopened.InitializeAsync();
            Assert.That(HistoryHelpers.Option(reopened, "followFileHistoryRenamesToolStripMenuItem").IsChecked, Is.True);
            Assert.That(HistoryHelpers.Option(reopened, "followFileHistoryRenamesToolStripMenuItem").IsEnabled, Is.False);
            Assert.That(HistoryHelpers.Option(reopened, "simplifyMergesToolStripMenuItem").IsChecked, Is.True);
            Assert.That(HistoryHelpers.Option(reopened, "simplifyMergesToolStripMenuItem").IsEnabled, Is.False);
        }
        finally { await Close(reopened); }
    }

    [AvaloniaTest]
    public async Task GeneralAndOptionMenusStayReadableInBothThemesAndTheOriginalFrenchTranslations()
    {
        using Fixture f = await Fixture.CreateAsync(); CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            foreach (string culture in new[] { "en", "fr-FR" })
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                Window owner = new(); owner.Show();
                DesktopSettingsDialog settings = new(new(), TranslationCatalog.LoadDefault(), false) { RequestedThemeVariant = theme };
                Task<bool> result = settings.ShowDialog<bool>(owner);
                try
                {
                    Pump(settings); SettingsNavigationTestDriver.Select(settings, "General");
                    CheckBox follow = Field<CheckBox>(settings, "chkFollowRenamesInFileHistory"), exact = Field<CheckBox>(settings, "chkFollowRenamesInFileHistoryExact");
                    Rect[] bounds = new[] { follow, exact }.Select(c => new Rect(c.TranslatePoint(default, settings)!.Value, c.Bounds.Size)).ToArray();
                    Assert.That(bounds.All(b => new Rect(default, settings.ClientSize).Contains(b)), Is.True);
                    Assert.That(bounds[0].Right, Is.LessThanOrEqualTo(bounds[1].Left));
                    Assert.That(((TextBlock)follow.Content!).Text, Is.Not.Empty);
                    if (culture == "en") { Assert.That(((TextBlock)exact.Content!).Text, Is.EqualTo("Follow exact renames and copies only")); }
                    using var frame = HistoryHelpers.Capture(settings, $"file-history-settings-{culture}-{theme}.png");
                    PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await result, Is.False);
                }
                finally { if (settings.IsVisible) { settings.Close(false); } owner.Close(); }

                FileHistoryDialog dialog = new(f.Repository, "file.txt") { RequestedThemeVariant = theme }; dialog.Show();
                try
                {
                    await dialog.InitializeAsync(); Pump(dialog);
                    Button button = Field<Button>(dialog, "ShowFullHistory"); PushProcessTestDriver.Pointer(button); Pump(dialog);
                    ContextMenu menu = button.ContextMenu!;
                    Assert.That(menu.Items.OfType<MenuItem>().Select(i => i.Bounds.Height), Is.All.EqualTo(22));
                    using (var frame = TopLevel.GetTopLevel(menu.Items.OfType<MenuItem>().First())!.CaptureRenderedFrame())
                    {
                        string name = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"file-history-traversal-{culture}-{theme}.png");
                        frame!.Save(name); TestContext.AddTestAttachment(name);
                    }
                    menu.Close();
                    ListBox list = Field<ListBox>(dialog, "FileHistoryRevisions"); list.ContextMenu!.Open(list); Pump(dialog);
                    using (var frame = TopLevel.GetTopLevel(list.ContextMenu.Items.OfType<MenuItem>().First())!.CaptureRenderedFrame())
                    {
                        string name = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"file-history-follow-{culture}-{theme}.png");
                        frame!.Save(name); TestContext.AddTestAttachment(name);
                    }
                    list.ContextMenu.Close();
                }
                finally { await Close(dialog); }
            }
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }

    [AvaloniaTest]
    public async Task ClosingDuringAnOptionReloadDrainsItsGitAndCannotRestoreFocusAfterClose()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Linux process cancellation contract.");
            return;
        }

        using Fixture f = await Fixture.CreateAsync();
        string gate = f.Output("block-history"), marker = f.Output("history-pid"), wrapper = f.Output("history-git");
        await File.WriteAllTextAsync(wrapper, "#!/usr/bin/python3\nimport os,sys,time\nif 'log' in sys.argv and os.path.exists(" + JsonSerializer.Serialize(gate) + "):\n open(" + JsonSerializer.Serialize(marker) + ",'w').write(str(os.getpid()))\n while os.path.exists(" + JsonSerializer.Serialize(gate) + "): time.sleep(.01)\nos.execv('/usr/bin/git',['git']+sys.argv[1:])\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Button ownerInput = new() { Content = "Owner" }; Window owner = new() { Content = ownerInput }; owner.Show();
        FileHistoryDialog dialog = new(new GitRepository(f.Repository.Path, new GitRunner(wrapper)), "file.txt"); dialog.Show(owner);
        try
        {
            await dialog.InitializeAsync(); Pump(dialog); await File.WriteAllTextAsync(gate, "block");
            ListBox grid = Field<ListBox>(dialog, "FileHistoryRevisions"); grid.ContextMenu!.Open(grid); Pump(dialog);
            PushProcessTestDriver.Pointer(HistoryHelpers.Option(dialog, "followFileHistoryToolStripMenuItem"));
            await Until(() => File.Exists(marker));
            using Process reader = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker)));
            Assert.That(dialog.IsBusy, Is.True); dialog.Close();
            await Until(() => !dialog.IsVisible && !dialog.IsBusy); await dialog.InformationCompletion;
            Assert.That(reader.HasExited, Is.True);
            ownerInput.Focus(); Pump(owner); Assert.That(ownerInput.IsKeyboardFocusWithin, Is.True);
        }
        finally { File.Delete(gate); await Close(dialog); owner.Close(); }
    }

    private static async Task<(string Path, string Old)> RenameAsync(Fixture f, bool edited)
    {
        string content = string.Join('\n', Enumerable.Range(0, 40).Select(i => "line " + i)) + "\n";
        await f.ChangeAsync(content, "Original content"); string old = await f.Repository.ResolveRevisionAsync("HEAD");
        const string path = "renamed [1] λ.txt"; await f.GitAsync("mv", "file.txt", path);
        if (edited) { await File.WriteAllTextAsync(f.PathInRepository(path), content.Replace("line 0\n", "edited line zero\n", StringComparison.Ordinal)); }
        await f.GitAsync("add", "--", path); await f.GitAsync("commit", "-m", edited ? "Edited rename" : "Exact rename");
        await File.AppendAllTextAsync(f.PathInRepository(path), "after rename\n"); await f.GitAsync("add", "--", path); await f.GitAsync("commit", "-m", "After rename");
        return (path, old);
    }
    private static async Task<DesktopSettingsDialog> OpenSettings(MainWindow main)
    {
        MenuItem tools = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(i => i.Name == "ToolsMenu");
        tools.Items.OfType<MenuItem>().Single(i => i.Name == "settingsToolStripMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any());
        DesktopSettingsDialog dialog = main.OwnedWindows.OfType<DesktopSettingsDialog>().Single(); Pump(dialog); SettingsNavigationTestDriver.Select(dialog, "General"); return dialog;
    }
    private static T Field<T>(Window owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Press(Window owner, PhysicalKey key)
    { owner.KeyPressQwerty(key, RawInputModifiers.None); owner.KeyReleaseQwerty(key, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); }
    private static void Pump(Window owner)
    { for (int i = 0; i < 3; i++) { owner.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Idle(MainWindow main)
    { await Until(() => !main.IsBusy && main.WindowWorkCompletion.IsCompleted); await main.WindowWorkCompletion; Pump(main); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); }
    private static async Task Close(Window window)
    {
        window.Close();
        if (window is MainWindow main) { await main.WindowWorkCompletion; }
        if (window is FileHistoryDialog history) { await history.InformationCompletion; }
        await Until(() => !window.IsVisible); Dispatcher.UIThread.RunJobs();
    }
    private static async Task Until(Func<bool> condition)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); } }
}

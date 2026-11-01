using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ScriptsSettingsPageTests
{
    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingProfileShowsTheOriginalOrderedDisabledExamplesAndCompactControlsWithoutCreatingAFile(bool dark)
    {
        using Scope f = new(); Window owner = f.Owner(dark); (DesktopSettingsDialog dialog, Task<bool> done) = f.Open(owner);
        OpenScripts(dialog); ScriptsSettingsPage page = Field<ScriptsSettingsPage>(dialog, "ScriptsSettingsPage");
        Assert.That(dialog.ActualThemeVariant, Is.EqualTo(dark ? ThemeVariant.Dark : ThemeVariant.Light));
        Assert.That(page.ActualThemeVariant, Is.EqualTo(dialog.ActualThemeVariant));
        Assert.That(page.Draft, Is.EqualTo(ScriptConfiguration.GetDefaults())); Assert.That(page.IsDirty, Is.False);
        Assert.That(page.Draft.All(s => !s.Enabled), Is.True); Assert.That(Field<ListBox>(dialog, "lvScripts").SelectionMode, Is.EqualTo(SelectionMode.Single));
        foreach (string name in new[] { "btnAdd", "btnDelete", "btnMoveUp", "btnMoveDown", "btnArgumentsHelp" })
        { Assert.That(Field<Button>(dialog, name).IsEffectivelyVisible, Is.True); }
        Assert.That(Field<Button>(dialog, "btnMoveUp").IsEnabled, Is.False); Assert.That(Field<Button>(dialog, "btnMoveDown").IsEnabled, Is.True);
        Assert.That(dialog.GetVisualDescendants().Any(c => c is TextBox { Name: "HotkeyCommandIdentifier" }), Is.False);
        Capture(dialog, "scripts-settings-default-" + (dark ? "dark" : "light") + ".png");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await done, Is.False);
        Assert.That(File.Exists(f.Store.FilePath), Is.False); Assert.That(f.SaveCalls, Is.Zero);
    }

    [AvaloniaTest]
    public async Task AListCheckboxSelectsWithoutEnablingWhileTheEnabledPropertyActuallyChangesTheDraft()
    {
        using Scope f = new(); f.Store.Save(f.Initial); byte[] bytes = File.ReadAllBytes(f.Store.FilePath);
        (DesktopSettingsDialog dialog, Task<bool> done) = f.Open(f.Owner()); OpenScripts(dialog);
        PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "ScriptRowEnabled9002"));
        ScriptsSettingsPage page = Field<ScriptsSettingsPage>(dialog, "ScriptsSettingsPage");
        Assert.That((Field<ListBox>(dialog, "lvScripts").SelectedItem as ScriptDefinition)?.HotkeyCommandIdentifier, Is.EqualTo(9002));
        Assert.That(page.Draft[1].Enabled, Is.False); Assert.That(page.IsDirty, Is.False);
        PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "ScriptEnabled"));
        Assert.That(page.Draft[1].Enabled, Is.True); Assert.That(page.Draft[0].Enabled, Is.False); Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(bytes));
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await done, Is.False);
    }

    [AvaloniaTest]
    public async Task AllElevenVisiblePropertiesRoundTripFromActualEditorGesturesWithTheHiddenIdUnchanged()
    {
        using Scope f = new(); f.Store.Save(f.Initial); (DesktopSettingsDialog dialog, Task<bool> done) = f.Open(f.Owner()); OpenScripts(dialog);
        Edit(dialog, "ScriptName", " &Café Ω "); ClickEditor(dialog, "ScriptEnabled"); Edit(dialog, "ScriptCommand", " /literal command ");
        Edit(dialog, "ScriptArguments", " leading {UserInput:x=y}\nsecond Ω ");
        await Choose(dialog, Field<ComboBox>(dialog, "ScriptOnEvent"), (int)ScriptEvent.AfterFetch);
        await Choose(dialog, Field<ComboBox>(dialog, "ScriptIcon"), 1); string icon = ScriptPresentation.IconKeys[0];
        Edit(dialog, "ScriptIconFilePath", " /missing literal icon.ico ");
        ClickEditor(dialog, "ScriptAskConfirmation"); ClickEditor(dialog, "ScriptRunInBackground"); ClickEditor(dialog, "ScriptIsPowerShell"); ClickEditor(dialog, "ScriptAddToRevisionGridContextMenu");
        ScriptsSettingsPage page = Field<ScriptsSettingsPage>(dialog, "ScriptsSettingsPage"); ScriptDefinition first = page.Draft[0];
        Assert.That(first, Is.EqualTo(f.Initial[0] with { Name = " &Café Ω ", Enabled = true, Command = " /literal command ", Arguments = " leading {UserInput:x=y}\nsecond Ω ",
            OnEvent = ScriptEvent.AfterFetch, Icon = icon, IconFilePath = " /missing literal icon.ico ", AskConfirmation = true, RunInBackground = true, IsPowerShell = true, AddToRevisionGridContextMenu = true }));
        Assert.That(page.Draft[1], Is.EqualTo(f.Initial[1]));
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings")); Assert.That(await done, Is.True);
        Assert.That(f.Store.Load().Scripts, Is.EqualTo(new[] { first, f.Initial[1] })); Assert.That(dialog.ScriptsResult.Scripts, Is.EqualTo(f.Store.Load().Scripts)); Assert.That(f.SaveCalls, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task AddMoveDeleteUseTheOriginalOrderAndStableIdsAndClearTheLastEditor()
    {
        using Scope f = new(); f.Store.Save(f.Initial); (DesktopSettingsDialog dialog, Task<bool> done) = f.Open(f.Owner()); OpenScripts(dialog);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnAdd")); ScriptsSettingsPage page = Field<ScriptsSettingsPage>(dialog, "ScriptsSettingsPage");
        int added = page.Draft[^1].HotkeyCommandIdentifier; Assert.That(added, Is.EqualTo(9003)); Assert.That(page.Draft[^1].Name, Is.EqualTo("<New Script>")); Assert.That(page.Draft[^1].Enabled, Is.True);
        Edit(dialog, "ScriptName", " third "); PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnMoveUp"));
        Assert.That(page.Draft.Select(s => s.HotkeyCommandIdentifier), Is.EqualTo(new[] { 9001, added, 9002 }));
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnMoveDown")); Assert.That(page.Draft[^1].HotkeyCommandIdentifier, Is.EqualTo(added));
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnDelete"));
        Assert.That((Field<ListBox>(dialog, "lvScripts").SelectedItem as ScriptDefinition)?.HotkeyCommandIdentifier, Is.EqualTo(9001), "Windows selects the first remaining row after Delete.");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnDelete")); PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnDelete"));
        Assert.That(page.Draft, Is.Empty); Assert.That(Field<TextBox>(dialog, "ScriptName").Text, Is.Null.Or.Empty); Assert.That(Field<TextBox>(dialog, "ScriptName").IsEnabled, Is.False);
        Assert.That(Field<Button>(dialog, "btnDelete").IsEnabled, Is.False); Assert.That(Field<Button>(dialog, "btnMoveUp").IsEnabled, Is.False); Assert.That(Field<Button>(dialog, "btnMoveDown").IsEnabled, Is.False);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings")); Assert.That(await done, Is.True); Assert.That(f.Store.Load().Scripts, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task CancelAndEscapeDiscardActualEditsAndNeverPublishScripts(bool escape)
    {
        using Scope f = new(); f.Store.Save(f.Initial); byte[] bytes = File.ReadAllBytes(f.Store.FilePath);
        (DesktopSettingsDialog dialog, Task<bool> done) = f.Open(f.Owner()); OpenScripts(dialog);
        Edit(dialog, "ScriptName", "not saved"); PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnMoveDown"));
        if (escape) { Press(dialog, PhysicalKey.Escape); } else { PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); }
        Assert.That(await done, Is.False); Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(bytes)); Assert.That(f.SaveCalls, Is.Zero);
    }

    [AvaloniaTest]
    public async Task AnUnrelatedSettingsOkDoesNotNormalizeNullableStringsUnknownIconOrRewriteScripts()
    {
        using Scope f = new(); ScriptDefinition item = new() { HotkeyCommandIdentifier = 9001, Name = null, Command = "", Arguments = null, Icon = "UnknownExactKey", IconFilePath = null };
        f.Store.Save([item]); byte[] bytes = File.ReadAllBytes(f.Store.FilePath);
        (DesktopSettingsDialog dialog, Task<bool> done) = f.Open(f.Owner()); OpenScripts(dialog);
        Assert.That(Field<ScriptsSettingsPage>(dialog, "ScriptsSettingsPage").Draft.Single(), Is.EqualTo(item));
        SettingsNavigationTestDriver.Select(dialog, "Appearance"); await Choose(dialog, Field<ComboBox>(dialog, "SettingsTheme"), 1);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings")); Assert.That(await done, Is.True);
        Assert.That(dialog.Result.Theme, Is.EqualTo("Dark")); Assert.That(f.SaveCalls, Is.Zero); Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(bytes));
    }

    [AvaloniaTest]
    public async Task CorruptUntouchedScriptsRemainVisibleAsAnErrorButDoNotBlockOtherSettingsOrGetOverwritten()
    {
        using Scope f = new(); Directory.CreateDirectory(Path.GetDirectoryName(f.Store.FilePath)!); File.WriteAllText(f.Store.FilePath, "{keep exact corruption"); byte[] bytes = File.ReadAllBytes(f.Store.FilePath);
        (DesktopSettingsDialog dialog, Task<bool> done) = f.Open(f.Owner()); OpenScripts(dialog);
        Assert.That(Field<TextBlock>(dialog, "ScriptsNotice").Text, Is.Not.Empty); Assert.That(Field<ListBox>(dialog, "lvScripts").Items, Is.Empty);
        SettingsNavigationTestDriver.Select(dialog, "Appearance"); await Choose(dialog, Field<ComboBox>(dialog, "SettingsTheme"), 1);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings")); Assert.That(await done, Is.True);
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(bytes)); Assert.That(f.SaveCalls, Is.Zero);
    }

    [AvaloniaTest]
    public async Task ADirtyCorruptProfileKeepsSettingsOpenAndTheExactCorruptionWithoutSavingOtherStores()
    {
        using Scope f = new(); Directory.CreateDirectory(Path.GetDirectoryName(f.Store.FilePath)!); File.WriteAllText(f.Store.FilePath, "{corrupt"); byte[] bytes = File.ReadAllBytes(f.Store.FilePath);
        int hotkeySaves = 0; (DesktopSettingsDialog dialog, Task<bool> done) = f.Open(f.Owner(), () => hotkeySaves++); OpenScripts(dialog);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnAdd")); Edit(dialog, "ScriptName", "reviewed draft only");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings"));
        Assert.That(dialog.IsVisible, Is.True); Assert.That(Field<TextBlock>(dialog, "ScriptsNotice").Text, Does.Contain("unreadable")); Assert.That(hotkeySaves, Is.Zero);
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(bytes)); PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await done, Is.False);
    }

    [AvaloniaTest]
    public async Task ASecondOpenSettingsCannotOverwriteTheFirstWindowsPublishedScriptOrder()
    {
        using Scope f = new(); f.Store.Save(f.Initial); (DesktopSettingsDialog a, Task<bool> da) = f.Open(f.Owner()); (DesktopSettingsDialog b, Task<bool> db) = f.Open(f.Owner());
        OpenScripts(a); PushProcessTestDriver.Pointer(Field<Button>(a, "btnMoveDown")); PushProcessTestDriver.Pointer(Field<Button>(a, "SaveDesktopSettings")); Assert.That(await da, Is.True); byte[] saved = File.ReadAllBytes(f.Store.FilePath);
        OpenScripts(b); Edit(b, "ScriptName", "B stale draft"); PushProcessTestDriver.Pointer(Field<Button>(b, "SaveDesktopSettings"));
        Assert.That(b.IsVisible, Is.True); Assert.That(Field<TextBlock>(b, "ScriptsNotice").Text, Does.Contain("changed")); Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(saved));
        PushProcessTestDriver.Pointer(Field<Button>(b, "CancelDesktopSettings")); Assert.That(await db, Is.False);
    }

    [AvaloniaTest]
    public async Task ARealFilesystemSaveFaultKeepsTheDraftAndOwnedSettingsOpenWithoutDeletingItsSentinel()
    {
        using Scope f = new(); f.Store.Save(f.Initial); (DesktopSettingsDialog dialog, Task<bool> done) = f.Open(f.Owner()); OpenScripts(dialog); Edit(dialog, "ScriptName", "draft");
        File.Delete(f.Store.FilePath); Directory.CreateDirectory(f.Store.FilePath); string sentinel = Path.Combine(f.Store.FilePath, "keep"); File.WriteAllText(sentinel, "literal Ω");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings")); Assert.That(dialog.IsVisible, Is.True); Assert.That(Field<TextBlock>(dialog, "ScriptsNotice").Text, Is.Not.Empty);
        Assert.That(File.ReadAllText(sentinel), Is.EqualTo("literal Ω")); Assert.That(Field<ScriptsSettingsPage>(dialog, "ScriptsSettingsPage").Draft[0].Name, Is.EqualTo("draft"));
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await done, Is.False);
    }

    [AvaloniaTest]
    public async Task ArgumentsHelpReusesItsOwnedWindowAndClosesWithSettingsWithoutAnyScriptPreparation()
    {
        using Scope f = new(); (DesktopSettingsDialog dialog, Task<bool> done) = f.Open(f.Owner()); OpenScripts(dialog);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnArgumentsHelp")); Window help = dialog.OwnedWindows.Single(w => w.Name == "ScriptArgumentsHelp"); Pump(help);
        string text = Field<TextBox>(help, "ScriptArgumentsHelpText").Text!; Assert.That(text, Does.Contain("{{option}}")); Assert.That(text, Does.Contain("{UserInput:a popup label=a default value using {sLocalBranch}}")); Assert.That(text, Does.Contain("{ColumnNumber}"));
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnArgumentsHelp")); Assert.That(dialog.OwnedWindows.Single(w => w.Name == "ScriptArgumentsHelp"), Is.SameAs(help));
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await done, Is.False); Assert.That(help.IsVisible, Is.False); Assert.That(File.Exists(f.Store.FilePath), Is.False);
    }

    [AvaloniaTest]
    public async Task DraftRenamesKeepDynamicHotkeyIdsAndDisabledNamedScriptsRemainInTheCatalogue()
    {
        using Scope f = new(); f.Store.Save(f.Initial); (DesktopSettingsDialog dialog, Task<bool> done) = f.Open(f.Owner()); OpenScripts(dialog);
        Edit(dialog, "ScriptName", "&Renamed"); PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnMoveDown"));
        SettingsNavigationTestDriver.Select(dialog, "Hotkeys");
        HotkeysSettingsPage hotkeys = Field<HotkeysSettingsPage>(dialog, "HotkeysSettingsPage");
        Assert.That(hotkeys.Draft.Commands("Scripts").Select(c => c.Code), Is.EqualTo(new[] { 9002, 9001 }));
        Assert.That(hotkeys.Draft.Commands("Scripts").Single(c => c.Code == 9001).Name, Is.EqualTo("Renamed"));
        Assert.That(hotkeys.Draft.Get("Scripts", 9001), Is.Null); Assert.That(f.SaveCalls, Is.Zero);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await done, Is.False);
    }

    [AvaloniaTest]
    public void SharedPresentationKeepsLiteralNamesAndUnknownIconIntentWithoutCreatingAProcess()
    {
        ScriptDefinition script = new() { Name = "&A_&&B...", Icon = "MissingExactIcon", IconFilePath = "/a missing icon Ω.ico" };
        Assert.That(ScriptPresentation.Header(script).Text, Is.EqualTo("_A__&B...")); Assert.That(ScriptPresentation.Icon(script), Is.Null);
        Assert.That(script.Name, Is.EqualTo("&A_&&B...")); Assert.That(script.Icon, Is.EqualTo("MissingExactIcon"));
        Assert.That(ScriptPresentation.Icon(script with { Icon = "EditFile" }), Is.TypeOf<Image>());
    }

    private static void OpenScripts(Window w) { SettingsNavigationTestDriver.Select(w, "Scripts"); Pump(w); }
    private static void ClickEditor(Window w, string name)
    {
        CheckBox editor = Field<CheckBox>(w, name);
        bool expected = editor.IsChecked != true;
        Reveal(w, editor);
        PushProcessTestDriver.Pointer(editor);
        Pump(w);
        Assert.That(editor.IsChecked, Is.EqualTo(expected), name + " must toggle from the single real click.");
    }
    private static void Edit(Window w, string name, string value)
    {
        TextBox input = Field<TextBox>(w, name); Reveal(w, input); PushProcessTestDriver.Pointer(input);
        w.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control); w.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control); w.KeyTextInput(value); Pump(w); Assert.That(input.Text, Is.EqualTo(value));
    }
    private static async Task Choose(Window w, ComboBox combo, int index)
    {
        if (combo.Name?.StartsWith("Script", StringComparison.Ordinal) == true) { Reveal(w, combo); }
        PushProcessTestDriver.Pointer(combo); Assert.That(combo.IsDropDownOpen, Is.True); Press(w, PhysicalKey.Home);
        for (int i = 0; i < index; i++) { Press(w, PhysicalKey.ArrowDown); }
        Press(w, PhysicalKey.Enter); await Task.Yield(); Pump(w); Assert.That(combo.IsDropDownOpen, Is.False); Assert.That(combo.SelectedIndex, Is.EqualTo(index));
    }
    private static void Reveal(Window w, Control control)
    {
        ScrollViewer scroll = Field<ScrollViewer>(w, "ScriptsPropertiesScroll");
        ScrollContentPresenter viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>()
            .Single(presenter => ReferenceEquals(presenter.TemplatedParent, scroll));
        for (int i = 0; i < 35; i++)
        {
            // Pointer() settles render jobs too. Observe the same settled geometry
            // and expose the whole target: focusing a clipped checkbox can scroll
            // it between pointer down and release, so its center alone is insufficient.
            for (int frame = 0; frame < 10; frame++) { Pump(w); }
            Point origin = control.TranslatePoint(default, w)!.Value;
            Rect bounds = new(origin, control.Bounds.Size);
            Point center = bounds.Center;
            Point top = viewport.TranslatePoint(default, w)!.Value;
            Rect clip = new(top, viewport.Bounds.Size);
            if (clip.Inflate(.5).Contains(bounds)
                && (w.InputHitTest(center) as Visual)?.GetSelfAndVisualAncestors().Contains(control) == true) { return; }
            Point wheel = top + new Vector(6, viewport.Bounds.Height / 2);
            w.MouseMove(wheel);
            w.MouseWheel(wheel, new Vector(0, bounds.Top < clip.Top ? 1 : -1), RawInputModifiers.None);
        }
        Assert.Fail("The real wheel did not fully expose " + control.Name + "; bounds=" + control.Bounds + "; offset=" + scroll.Offset + "; viewport=" + viewport.Bounds);
    }
    private static T Field<T>(TopLevel w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Pump(TopLevel w) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Press(Window w, PhysicalKey key) { w.KeyPressQwerty(key, RawInputModifiers.None); if (w.IsVisible) { w.KeyReleaseQwerty(key, RawInputModifiers.None); Pump(w); } }
    private static void Capture(TopLevel w, string name)
    {
        Pump(w); using var frame = w.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string root = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(root); string file = Path.Combine(root, name); frame!.Save(file); TestContext.AddTestAttachment(file);
    }
    private sealed class Scope : IDisposable
    {
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CultureInfo _ui = CultureInfo.CurrentUICulture;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "scripts-page-" + Guid.NewGuid().ToString("N"));
        private readonly List<Window> _windows = [];
        internal ScriptsPreferencesStore Store { get; }
        internal int SaveCalls { get; private set; }
        internal ScriptDefinition[] Initial { get; } = [new() { HotkeyCommandIdentifier = 9001, Name = "First", Enabled = false }, new() { HotkeyCommandIdentifier = 9002, Name = "Second", Enabled = false }];
        internal Scope() { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en"); Store = new(); }
        internal Window Owner(bool dark = false) { Window w = new() { Width = 800, Height = 600, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; _windows.Add(w); w.Show(); Pump(w); return w; }
        internal (DesktopSettingsDialog, Task<bool>) Open(Window owner, Action? saveHotkeys = null)
        {
            DesktopSettingsDialog d = new(new(), TranslationCatalog.LoadDefault(), false, saveHotkeys: _ => saveHotkeys?.Invoke(), scripts: Store.Load(), saveScripts: (baseline, draft) => { SaveCalls++; return Store.SaveChanges(baseline, draft); })
                { RequestedThemeVariant = owner.ActualThemeVariant };
            _windows.Add(d); Task<bool> task = d.ShowDialog<bool>(owner); Pump(d); return (d, task);
        }
        public void Dispose()
        {
            try { foreach (Window w in _windows.AsEnumerable().Reverse()) { if (w.IsVisible) { w.Close(); Pump(w); } } }
            finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); CultureInfo.CurrentUICulture = _ui; if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); } }
        }
    }
}

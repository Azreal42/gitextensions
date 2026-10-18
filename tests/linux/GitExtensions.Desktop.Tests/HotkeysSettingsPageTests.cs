using System.Globalization;
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
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class HotkeysSettingsPageTests
{
    private static readonly KeyGesture FreeGesture = new(Key.F12, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt);
    private const RawInputModifiers FreeModifiers = RawInputModifiers.Control | RawInputModifiers.Shift | RawInputModifiers.Alt;

    [AvaloniaTest]
    public async Task AllNineOriginalContextsAreReachableAndTheCommandGridUsesSingleSelection()
    {
        await using Scope f = await Scope.OpenAsync();
        Assert.That(f.Contexts.Items.Cast<string>(), Is.EqualTo(new[]
            { "Commit", "Browse", "LeftPanel", "RevisionGrid", "FileViewer", "FormMergeConflicts", "BrowseDiff", "Stash", "Scripts" }));
        Assert.That(f.Contexts.SelectedItem, Is.Null, "ControlHotkeys does not invent an initial context or command selection.");
        Assert.That(f.Mappings.Items, Is.Empty);
        foreach (string context in f.Contexts.Items.Cast<string>())
        {
            f.SelectContext(context);
            IEnumerable<string> expected = context == "Scripts"
                ? ScriptConfiguration.GetDefaults().Select(script => script.GetDisplayName())
                : HotkeyCatalog.For(context).Select(command => command.Name);
            Assert.That(f.Mappings.Items.OfType<HotkeyCommand>().Select(command => command.Name), Is.EqualTo(expected));
            if (context == "Scripts")
            {
                Assert.That(new ScriptsPreferencesStore().Load().Scripts.All(script => !script.Enabled), Is.True,
                    "Displaying named example hotkeys must not enable any script.");
                PushProcessTestDriver.Pointer(f.Button("bApply")); PushProcessTestDriver.Pointer(f.Button("bClear"));
                Assert.That(f.Page.Capture.CapturedGesture, Is.Null); continue;
            }
            HotkeyCommand[] commands = f.Mappings.Items.OfType<HotkeyCommand>().Take(2).ToArray();
            f.SelectCommand(commands[0]);
            if (commands.Length > 1) { f.SelectCommand(commands[1], RawInputModifiers.Control); }
            Assert.That(f.Mappings.SelectedItems!.Count, Is.EqualTo(1));
            Assert.That(f.Mappings.SelectedItem, Is.EqualTo(commands[^1]));
            ListBoxItem row = (ListBoxItem)f.Mappings.ContainerFromItem(commands[^1])!;
            Assert.That(row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "HotkeyCommandName").Text, Is.EqualTo(commands[^1].Name));
        }
        Assert.That(f.Saves, Is.Zero);
    }

    [AvaloniaTest]
    [TestCase(PhysicalKey.Escape, Key.Escape)]
    [TestCase(PhysicalKey.Tab, Key.Tab)]
    [TestCase(PhysicalKey.Enter, Key.Enter)]
    [TestCase(PhysicalKey.Backspace, Key.Back)]
    public async Task CaptureSwallowsDialogAndNavigationKeysWithoutClosingSavingOrMovingFocus(PhysicalKey physical, Key logical)
    {
        await using Scope f = await Scope.OpenAsync(); f.Select("Browse", "CreateBranch");
        PushProcessTestDriver.Pointer(f.Page.Capture);
        Press(f.Dialog, physical);
        AssertGesture(f.Page.Capture.CapturedGesture, new(logical));
        Assert.That(f.Page.Capture.IsFocused, Is.True);
        Assert.That(f.Result.IsCompleted, Is.False); Assert.That(f.Dialog.IsVisible, Is.True);
        Assert.That(f.Saves, Is.Zero);
        string displayed = f.Page.Capture.Text!;
        f.Dialog.KeyTextInput("literal must not enter the capture field"); Pump(f.Dialog);
        Assert.That(f.Page.Capture.Text, Is.EqualTo(displayed));
        AssertGesture(f.Page.Draft.Get("Browse", "CreateBranch"), new(Key.B, KeyModifiers.Control));
        PushProcessTestDriver.Pointer(f.Button("CancelDesktopSettings")); Assert.That(await f.Result, Is.False);
    }

    [AvaloniaTest]
    [TestCase(PhysicalKey.ShiftLeft, RawInputModifiers.Shift)]
    [TestCase(PhysicalKey.ControlLeft, RawInputModifiers.Control)]
    [TestCase(PhysicalKey.AltLeft, RawInputModifiers.Alt)]
    [TestCase(PhysicalKey.MetaLeft, RawInputModifiers.Meta)]
    public async Task ModifierKeysAloneAreSwallowedWithoutReplacingTheCapturedGesture(PhysicalKey physical, RawInputModifiers modifier)
    {
        await using Scope f = await Scope.OpenAsync(); f.Select("Browse", "CreateBranch");
        KeyGesture? before = f.Page.Capture.CapturedGesture; string? text = f.Page.Capture.Text;
        PushProcessTestDriver.Pointer(f.Page.Capture); Press(f.Dialog, physical, modifier);
        AssertGesture(f.Page.Capture.CapturedGesture, before);
        Assert.That(f.Page.Capture.Text, Is.EqualTo(text)); Assert.That(f.Page.Capture.IsFocused, Is.True);
        Assert.That(f.Result.IsCompleted, Is.False); Assert.That(f.Saves, Is.Zero);
    }

    [AvaloniaTest]
    public async Task ApplyAndClearEditOnlyTheDraftAndClearIsAnExplicitUnassignment()
    {
        HotkeyBindings initial = new(); string[] original = Snapshot(initial);
        await using Scope f = await Scope.OpenAsync(initial); f.Select("Browse", "CreateBranch");
        Assert.That(f.Page.Draft.IsAssigned(FreeGesture), Is.False, "The fixture combination must actually be unassigned.");
        PushProcessTestDriver.Pointer(f.Page.Capture); Press(f.Dialog, PhysicalKey.F12, FreeModifiers);
        AssertGesture(f.Page.Capture.CapturedGesture, FreeGesture);
        Assert.That(f.Page.Capture.HasAssignmentWarning, Is.False);
        PushProcessTestDriver.Pointer(f.Button("bApply")); Pump(f.Dialog);
        AssertGesture(f.Page.Draft.Get("Browse", "CreateBranch"), FreeGesture);
        Assert.That(f.Mappings.SelectedItem, Is.Null); Assert.That(f.Page.Capture.CapturedGesture, Is.Null);
        Assert.That(Snapshot(initial), Is.EqualTo(original)); Assert.That(Snapshot(f.Dialog.HotkeysResult), Is.EqualTo(original));
        Assert.That(f.Saves, Is.Zero);

        f.Select("Browse", "CreateBranch");
        ListBoxItem row = (ListBoxItem)f.Mappings.ContainerFromItem(f.Mappings.SelectedItem!)!;
        Assert.That(row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "HotkeyGesture").Text, Is.EqualTo(FreeGesture.ToString()));
        PushProcessTestDriver.Pointer(f.Button("bClear")); Pump(f.Dialog);
        Assert.That(f.Page.Draft.Get("Browse", "CreateBranch"), Is.Null);
        f.Select("Browse", "CreateBranch"); Assert.That(f.Page.Capture.Text, Is.EqualTo("None"));
        Assert.That(f.Page.Capture.HasAssignmentWarning, Is.False);
        PushProcessTestDriver.Pointer(f.Button("CancelDesktopSettings")); Assert.That(await f.Result, Is.False);
        Assert.That(Snapshot(initial), Is.EqualTo(original)); Assert.That(f.Saves, Is.Zero);
    }

    [AvaloniaTest]
    public async Task AssignmentWarningIncludesTheCurrentCommandAndOtherContextsButNeverBlocksApply()
    {
        HotkeyBindings initial = new(); HotkeyCommand commit = HotkeyCatalog.Find("Commit", "Refresh")!;
        initial.Set("Browse", HotkeyCatalog.Find("Browse", "CreateBranch")!.Code, FreeGesture);
        KeyGesture crossContext = new(Key.F24, KeyModifiers.Alt); initial.Set(commit.Context, commit.Code, crossContext);
        await using Scope f = await Scope.OpenAsync(initial); f.Select("Browse", "CreateBranch");
        Assert.That(HotkeyCatalog.Contexts.SelectMany(HotkeyCatalog.For).Count(command =>
            f.Page.Draft.Get(command.Context, command.Code) is { } gesture && gesture.Key == FreeGesture.Key && gesture.KeyModifiers == FreeGesture.KeyModifiers), Is.EqualTo(1),
            "Only this command owns the captured gesture, so a global conflict elsewhere cannot explain the warning.");
        Assert.That(f.Page.Capture.HasAssignmentWarning, Is.True, "Windows also marks the selected command's own assignment red.");
        PushProcessTestDriver.Pointer(f.Page.Capture); Press(f.Dialog, PhysicalKey.F24, RawInputModifiers.Alt);
        AssertGesture(f.Page.Capture.CapturedGesture, crossContext);
        Assert.That(f.Page.Capture.HasAssignmentWarning, Is.True, "The warning considers every context, not only Browse.");
        Color warning = ((ISolidColorBrush)f.Page.Capture.Foreground!).Color;
        Assert.That(warning.R, Is.GreaterThan(warning.G)); Assert.That(warning.R, Is.GreaterThan(warning.B));
        Assert.That(f.Button("bApply").IsEffectivelyEnabled, Is.True);
        PushProcessTestDriver.Pointer(f.Button("bApply")); f.Select("Browse", "CreateBranch");
        AssertGesture(f.Page.Draft.Get("Browse", "CreateBranch"), crossContext);
        AssertGesture(f.Page.Draft.Get("Commit", "Refresh"), crossContext);
        Assert.That(f.Page.Capture.HasAssignmentWarning, Is.True);
        Assert.That(f.Saves, Is.Zero);
    }

    [AvaloniaTest]
    public async Task ResetAllRestoresEveryContextOnlyInTheDraftAndCancelLeavesTheInputUntouched()
    {
        HotkeyBindings initial = new();
        initial.Set("Browse", HotkeyCatalog.Find("Browse", "CreateBranch")!.Code, FreeGesture);
        initial.Set("Commit", HotkeyCatalog.Find("Commit", "Refresh")!.Code, null);
        string[] original = Snapshot(initial);
        await using Scope f = await Scope.OpenAsync(initial); f.Select("Browse", "CreateBranch");
        PushProcessTestDriver.Pointer(f.Button("bResetToDefaults")); Pump(f.Dialog);
        Assert.That(Snapshot(f.Page.Draft), Is.EqualTo(Snapshot(new())), "Reset all is global, including contexts which are not currently selected.");
        Assert.That(f.Contexts.SelectedItem, Is.Null); Assert.That(f.Mappings.Items, Is.Empty);
        Assert.That(f.Page.Capture.CapturedGesture, Is.Null);
        Assert.That(Snapshot(initial), Is.EqualTo(original)); Assert.That(Snapshot(f.Dialog.HotkeysResult), Is.EqualTo(original));
        PushProcessTestDriver.Pointer(f.Button("CancelDesktopSettings")); Assert.That(await f.Result, Is.False);
        Assert.That(f.Saves, Is.Zero); Assert.That(Snapshot(initial), Is.EqualTo(original));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task OkPublishesOnlyAppliedChangesAndTheSaveCallbackReceivesAnIndependentClone(bool apply)
    {
        HotkeyBindings initial = new(); string[] original = Snapshot(initial); HotkeyBindings? saved = null;
        await using Scope f = await Scope.OpenAsync(initial, save: draft =>
        {
            saved = draft.Clone(); draft.ResetToDefaults();
        });
        f.Select("Browse", "CreateBranch"); PushProcessTestDriver.Pointer(f.Page.Capture); Press(f.Dialog, PhysicalKey.F12, FreeModifiers);
        if (apply) { PushProcessTestDriver.Pointer(f.Button("bApply")); }
        PushProcessTestDriver.Pointer(f.Button("SaveDesktopSettings")); Assert.That(await f.Result, Is.True);
        KeyGesture expected = apply ? FreeGesture : new(Key.B, KeyModifiers.Control);
        Assert.That(f.Saves, Is.EqualTo(1)); Assert.That(saved, Is.Not.Null);
        AssertGesture(saved!.Get("Browse", "CreateBranch"), expected);
        AssertGesture(f.Dialog.HotkeysResult.Get("Browse", "CreateBranch"), expected);
        Assert.That(Snapshot(initial), Is.EqualTo(original));
    }

    [AvaloniaTest]
    [TestCase("io")]
    [TestCase("unauthorized")]
    [TestCase("json")]
    public async Task SaveFailureKeepsTheWindowAndAllResultsUnpublishedAndAllowsRetry(string error)
    {
        bool fail = true; HotkeyBindings initial = new(); string[] original = Snapshot(initial);
        await using Scope f = await Scope.OpenAsync(initial, save: _ =>
        {
            if (!fail) { return; }
            throw error switch { "io" => new IOException("fixture-save-error"), "unauthorized" => new UnauthorizedAccessException("fixture-save-error"), _ => new JsonException("fixture-save-error") };
        });
        DesktopPreferences initialPreferences = f.Dialog.Result; FileHistoryPreferences initialHistory = f.Dialog.FileHistoryResult;
        f.Select("Browse", "CreateBranch"); PushProcessTestDriver.Pointer(f.Page.Capture); Press(f.Dialog, PhysicalKey.F12, FreeModifiers);
        PushProcessTestDriver.Pointer(f.Button("bApply"));
        SettingsNavigationTestDriver.Select(f.Dialog, "Appearance"); Pump(f.Dialog);
        Choose(f.Dialog, Field<ComboBox>(f.Dialog, "SettingsTheme"), "Dark");
        PushProcessTestDriver.Pointer(f.Button("SaveDesktopSettings")); Pump(f.Dialog);
        Assert.That(f.Result.IsCompleted, Is.False); Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Saves, Is.EqualTo(1));
        Assert.That(Field<TextBlock>(f.Dialog, "SettingsNotice").Text, Does.Contain("fixture-save-error"));
        Assert.That(f.Dialog.Result, Is.SameAs(initialPreferences)); Assert.That(f.Dialog.HistorySortResult, Is.EqualTo(GitLogSort.GitDefault));
        Assert.That(f.Dialog.FileHistoryResult, Is.SameAs(initialHistory)); Assert.That(Snapshot(f.Dialog.HotkeysResult), Is.EqualTo(original));
        AssertGesture(f.Page.Draft.Get("Browse", "CreateBranch"), FreeGesture);
        fail = false; PushProcessTestDriver.Pointer(f.Button("SaveDesktopSettings")); Assert.That(await f.Result, Is.True);
        Assert.That(f.Saves, Is.EqualTo(2)); Assert.That(f.Dialog.Result.Theme, Is.EqualTo("Dark"));
        AssertGesture(f.Dialog.HotkeysResult.Get("Browse", "CreateBranch"), FreeGesture);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task GeneralAndSortingValidationRunBeforeAnyHotkeySave(bool invalidSorting)
    {
        await using Scope f = await Scope.OpenAsync(); DesktopPreferences initial = f.Dialog.Result;
        if (invalidSorting)
        {
            SettingsNavigationTestDriver.Select(f.Dialog, "Sorting"); Pump(f.Dialog);
            TextBox input = Field<TextBox>(f.Dialog, "txtPrioBranchNames"); Edit(f.Dialog, input, "[");
        }
        else
        {
            SettingsNavigationTestDriver.Select(f.Dialog, "Advanced"); Pump(f.Dialog);
            CheckBox normalise = Field<CheckBox>(f.Dialog, "SettingsNormaliseBranches");
            if (normalise.IsChecked != true) { PushProcessTestDriver.Pointer(normalise); }
            Edit(f.Dialog, Field<TextBox>(f.Dialog, "SettingsNormaliseSymbol"), "");
        }
        PushProcessTestDriver.Pointer(f.Button("SaveDesktopSettings")); Pump(f.Dialog);
        Assert.That(f.Result.IsCompleted, Is.False); Assert.That(f.Saves, Is.Zero); Assert.That(f.Dialog.Result, Is.SameAs(initial));
        Assert.That(Field<TextBlock>(f.Dialog, "SettingsNotice").Text, Is.Not.Null.And.Not.Empty);
    }

    [AvaloniaTest]
    public async Task LegacySettingsWithoutASaveCallbackReturnsAnIndependentDraftWithoutWritingAProfile()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        PreferencesStore store = new(); DesktopPreferences initial = new(); store.Save(initial); byte[] before = File.ReadAllBytes(store.FilePath);
        Window owner = new(); owner.Show(); DesktopSettingsDialog dialog = new(initial, TranslationCatalog.LoadDefault(), false);
        Task<bool> result = dialog.ShowDialog<bool>(owner);
        try
        {
            ShowHotkeys(dialog); HotkeysSettingsPage page = Field<HotkeysSettingsPage>(dialog, "HotkeysSettingsPage");
            Select(dialog, page, "Browse", "CreateBranch"); PushProcessTestDriver.Pointer(page.Capture); Press(dialog, PhysicalKey.F12, FreeModifiers);
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "bApply")); PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings"));
            Assert.That(await result, Is.True); AssertGesture(dialog.HotkeysResult.Get("Browse", "CreateBranch"), FreeGesture);
            Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(before));
        }
        finally { if (dialog.IsVisible) { dialog.Close(false); } owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase("en", false, false)]
    [TestCase("en", true, false)]
    [TestCase("fr-FR", false, false)]
    [TestCase("fr-FR", true, false)]
    [TestCase("en", false, true)]
    [TestCase("fr-FR", true, true)]
    public async Task OriginalControlsAndVisibleCaptureWarningRemainReadableInSixThemeLanguageVariants(string culture, bool dark, bool conflict)
    {
        await using Scope f = await Scope.OpenAsync(culture: culture, dark: dark); f.Select("Browse", "CreateBranch");
        PushProcessTestDriver.Pointer(f.Page.Capture);
        if (!conflict) { Press(f.Dialog, PhysicalKey.F12, FreeModifiers); }
        Pump(f.Dialog);
        Assert.That(f.Page.Bounds.Width, Is.GreaterThanOrEqualTo(670)); Assert.That(f.Page.Bounds.Height, Is.GreaterThanOrEqualTo(330));
        Assert.That(f.Page.Capture.HasAssignmentWarning, Is.EqualTo(conflict));
        TextPresenter presenter = f.Page.Capture.GetVisualDescendants().OfType<TextPresenter>().Single();
        Assert.That(((ISolidColorBrush)presenter.Foreground!).Color, Is.EqualTo(((ISolidColorBrush)f.Page.Capture.Foreground!).Color),
            "The actual textbox glyph surface, including focused state, must display the assignment warning.");
        Control[] controls = [f.Contexts, f.Mappings, f.Page.Capture, f.Button("bApply"), f.Button("bClear"), f.Button("bResetToDefaults")];
        foreach (Control control in controls)
        {
            Rect bounds = new(control.TranslatePoint(default, f.Dialog)!.Value, control.Bounds.Size);
            Assert.That(control.IsEffectivelyVisible && control.IsEffectivelyEnabled, Is.True);
            Assert.That(new Rect(default, f.Dialog.ClientSize).Contains(bounds), Is.True, control.Name + " must fit inside Settings.");
        }
        Assert.That(Field<TextBlock>(f.Dialog, "columnCommand").Text, Is.EqualTo(culture == "en" ? "Command" : "Commande"));
        Assert.That(Field<TextBlock>(f.Dialog, "columnKey").Text, Is.EqualTo(culture == "en" ? "Key" : "Clé"));
        Assert.That(f.Button("bApply").Content, Is.EqualTo(culture == "en" ? "Apply" : "Appliquer"));
        Assert.That(f.Button("bResetToDefaults").Content, Is.EqualTo(culture == "en" ? "Reset all Hotkeys to defaults" : "Réinitialiser tous les raccourcis clavier"));
        Capture(f.Dialog, $"hotkeys-settings-{culture}-{(dark ? "dark" : "light")}-{(conflict ? "assigned" : "free")}.png");
    }

    private sealed class Scope(Fixture fixture, Window owner, DesktopSettingsDialog dialog, Task<bool> result) : IAsyncDisposable
    {
        internal DesktopSettingsDialog Dialog => dialog;
        internal Task<bool> Result => result;
        internal HotkeysSettingsPage Page => Field<HotkeysSettingsPage>(dialog, "HotkeysSettingsPage");
        internal ListBox Contexts => Field<ListBox>(dialog, "cmbSettings");
        internal ListBox Mappings => Field<ListBox>(dialog, "listMappings");
        internal int Saves { get; private set; }
        internal Button Button(string name) => Field<Button>(dialog, name);
        internal void Select(string context, string command) => HotkeysSettingsPageTests.Select(dialog, Page, context, command);
        internal void SelectContext(string context)
        { Contexts.ScrollIntoView(context); Pump(dialog); PushProcessTestDriver.Pointer((Control)Contexts.ContainerFromItem(context)!); Pump(dialog); }
        internal void SelectCommand(HotkeyCommand command, RawInputModifiers modifiers = RawInputModifiers.None) => ClickMapping(dialog, Mappings, command, modifiers);
        internal static async Task<Scope> OpenAsync(HotkeyBindings? initial = null, Action<HotkeyBindings>? save = null, string culture = "en", bool dark = false)
        {
            Fixture fixture = await Fixture.CreateAsync(); Window? owner = null; DesktopSettingsDialog? dialog = null;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                owner = new() { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; owner.Show();
                Scope? scope = null;
                dialog = new(new(), TranslationCatalog.LoadDefault(), false, hotkeys: initial, saveHotkeys: draft => { scope!.Saves++; save?.Invoke(draft); })
                    { RequestedThemeVariant = owner.RequestedThemeVariant };
                Task<bool> result = dialog.ShowDialog<bool>(owner); scope = new(fixture, owner, dialog, result);
                ShowHotkeys(dialog); return scope;
            }
            catch { if (dialog?.IsVisible == true) { dialog.Close(false); } owner?.Close(); fixture.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            try { if (dialog.IsVisible) { dialog.Close(false); } await result.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { owner.Close(); fixture.Dispose(); }
        }
    }

    private static void ShowHotkeys(DesktopSettingsDialog dialog)
    { Pump(dialog); SettingsNavigationTestDriver.Select(dialog, "Hotkeys"); Pump(dialog); }
    private static void Select(DesktopSettingsDialog dialog, HotkeysSettingsPage page, string context, string name)
    {
        ListBox contexts = Field<ListBox>(dialog, "cmbSettings"), mappings = Field<ListBox>(dialog, "listMappings");
        if (contexts.SelectedItem as string != context)
        { contexts.ScrollIntoView(context); Pump(dialog); PushProcessTestDriver.Pointer((Control)contexts.ContainerFromItem(context)!); Pump(dialog); }
        HotkeyCommand command = mappings.Items.OfType<HotkeyCommand>().Single(command => command.Name == name);
        ClickMapping(dialog, mappings, command);
        AssertGesture(page.Capture.CapturedGesture, page.Draft.Get(context, command.Code));
    }
    private static void ClickMapping(DesktopSettingsDialog dialog, ListBox mappings, HotkeyCommand command, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        mappings.ScrollIntoView(command); Pump(dialog); Control row = (Control)mappings.ContainerFromItem(command)!;
        if (modifiers == RawInputModifiers.None) { PushProcessTestDriver.Pointer(row); }
        else
        {
            Point point = row.TranslatePoint(new(row.Bounds.Width / 2, row.Bounds.Height / 2), dialog)!.Value;
            Assert.That((dialog.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True);
            dialog.MouseMove(point); dialog.MouseDown(point, MouseButton.Left, modifiers); dialog.MouseUp(point, MouseButton.Left, modifiers);
        }
        Pump(dialog);
    }
    private static void Choose(Window window, ComboBox combo, object value)
    {
        PushProcessTestDriver.Pointer(combo); Pump(window); Assert.That(combo.IsDropDownOpen, Is.True);
        PushProcessTestDriver.Pointer(combo.ContainerFromItem(value)!); Pump(window); Assert.That(combo.SelectedItem, Is.EqualTo(value));
    }
    private static void Edit(Window window, TextBox editor, string text)
    {
        PushProcessTestDriver.Pointer(editor); Press(window, PhysicalKey.A, RawInputModifiers.Control); Press(window, PhysicalKey.Backspace);
        if (text.Length > 0) { window.KeyTextInput(text); } Pump(window);
    }
    private static void AssertGesture(KeyGesture? actual, KeyGesture? expected)
    { Assert.That(actual?.Key, Is.EqualTo(expected?.Key)); Assert.That(actual?.KeyModifiers, Is.EqualTo(expected?.KeyModifiers)); }
    private static string[] Snapshot(HotkeyBindings bindings) => HotkeyCatalog.Contexts.SelectMany(HotkeyCatalog.For)
        .Select(command => $"{command.Context}:{command.Code}:{bindings.Get(command.Context, command.Code)?.Key}:{bindings.Get(command.Context, command.Code)?.KeyModifiers}").ToArray();
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Dispatcher.UIThread.RunJobs(); }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(Window window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string folder = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
}

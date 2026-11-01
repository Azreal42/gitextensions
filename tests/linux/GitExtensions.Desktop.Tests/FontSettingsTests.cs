using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FontSettingsTests
{
    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task FourRolePageUsesOriginalTranslationsAndRowZeroEolAndCancelNeverPublishes(bool dark)
    {
        using Scope scope = new("fr"); Window owner = scope.Owner(dark); FontPreferences initial = scope.Store.Load();
        DesktopSettingsDialog dialog = scope.Own(new DesktopSettingsDialog(new(), TranslationCatalog.LoadDefault(), false,
            fonts: initial, saveFonts: scope.Store.SaveChanges) { RequestedThemeVariant = owner.ActualThemeVariant });
        Task<bool> completion = dialog.ShowDialog<bool>(owner); Pump(dialog); OpenFonts(dialog);
        Assert.That(Field<TextBlock>(dialog, "label56").Text, Is.EqualTo("Police du code source"));
        Assert.That(Field<TextBlock>(dialog, "label26").Text, Is.EqualTo("Police de l'application"));
        Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Polices (redémarrage requis)"), Is.True);
        CheckBox eol = Field<CheckBox>(dialog, "ShowEolMarkerAsGlyph"); Assert.That(Grid.GetRow(eol), Is.Zero); Assert.That(Grid.GetColumn(eol), Is.EqualTo(2));
        foreach (string name in new[] { "diffFontChangeButton", "applicationFontChangeButton", "commitFontChangeButton", "monospaceFontChangeButton" })
        { Assert.That(Field<Button>(dialog, name).Bounds.Height, Is.GreaterThanOrEqualTo(32)); }
        PushProcessTestDriver.Pointer(eol); Assert.That(scope.Store.Load(), Is.EqualTo(initial));
        Capture(dialog, "fonts-page-" + (dark ? "dark-french" : "light-french") + ".png");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await completion, Is.False);
        Assert.That(File.Exists(scope.Store.FilePath), Is.False);
    }

    [AvaloniaTest]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public async Task EachOwnedPickerDraftUsesActualFontChoicesAndPrecisePointsAndLeavesOtherRolesUnchanged(int roleValue)
    {
        DesktopFontRole role = (DesktopFontRole)roleValue;
        using Scope scope = new(); Window owner = scope.Owner(); FontPreferences initial = new();
        AppearanceFontsSettingsPage page = new(initial, TranslationCatalog.LoadDefault()); Window settings = scope.Own(new Window { Content = page, Width = 760, Height = 360 }); settings.Show(owner); Pump(settings);
        string buttonName = role switch { DesktopFontRole.Code => "diffFontChangeButton", DesktopFontRole.Application => "applicationFontChangeButton", DesktopFontRole.Commit => "commitFontChangeButton", _ => "monospaceFontChangeButton" };
        PushProcessTestDriver.Pointer(Field<Button>(settings, buttonName)); await Until(() => settings.OwnedWindows.OfType<FontPickerDialog>().Any());
        FontPickerDialog picker = scope.Own(settings.OwnedWindows.OfType<FontPickerDialog>().Single()); Pump(picker);
        FontFamily chosen = DesktopFontResolver.Families(role).First(family => new[] { (false, false), (true, false), (false, true), (true, true) }
            .All(style => DesktopFontResolver.Resolve(new(family.Name, 12.375, style.Item1, style.Item2), role).Diagnostic is null));
        ChooseFamily(picker, chosen); Edit(picker, Field<TextBox>(picker, "FontSizeInPoints"), "12.375");
        PushProcessTestDriver.Pointer(Field<CheckBox>(picker, "FontBold")); PushProcessTestDriver.Pointer(Field<CheckBox>(picker, "FontItalic"));
        Assert.That(Field<ListBox>(picker, "FontFamily").SelectedItem, Is.Not.Null, "chosen family must remain a real supported styled face");
        PushProcessTestDriver.Pointer(Field<Button>(picker, "AcceptFont")); await Until(() => !picker.IsVisible); Pump(settings);
        Assert.That(page.Draft.For(role), Is.EqualTo(new FontSpec(chosen.Name, 12.375, true, true)));
        foreach (DesktopFontRole other in Enum.GetValues<DesktopFontRole>().Where(other => other != role))
        { Assert.That(page.Draft.For(other), Is.EqualTo(initial.For(other))); }
        Assert.That(File.Exists(scope.Store.FilePath), Is.False);
        settings.Close();
    }

    [AvaloniaTest]
    public async Task PickerEscapeAndCancelDiscardActualKeyboardAndPointerEdits()
    {
        using Scope scope = new(); Window owner = scope.Owner(); FontSpec initial = new("literal unavailable family", 9.25);
        foreach (bool escape in new[] { false, true })
        {
            FontPickerDialog picker = scope.Own(new FontPickerDialog(initial, DesktopFontRole.Monospace)); Task<bool> completion = picker.ShowDialog<bool>(owner); Pump(picker);
            Assert.That(Field<ListBox>(picker, "FontFamily").SelectedItem, Is.Null); Assert.That(Field<TextBlock>(picker, "FontFallback").Text, Does.Contain(initial.Family));
            ChooseFirst(picker); Edit(picker, Field<TextBox>(picker, "FontSizeInPoints"), "17.625");
            if (escape) { Press(picker, PhysicalKey.Escape); } else { PushProcessTestDriver.Pointer(Field<Button>(picker, "CancelFont")); }
            Assert.That(await completion, Is.False); Assert.That(picker.Result, Is.EqualTo(initial));
        }
        Assert.That(File.Exists(scope.Store.FilePath), Is.False);
    }

    [AvaloniaTest]
    [TestCase("0")]
    [TestCase("not a number")]
    [TestCase("1e100")]
    public async Task InvalidOrUnrenderableSizeKeepsThePickerOpenWithAVisibleDiagnosticAndReachableFooter(string size)
    {
        using Scope scope = new(); Window owner = scope.Owner(); FontPickerDialog picker = scope.Own(new FontPickerDialog(new("Consolas", 10), DesktopFontRole.Code));
        Task<bool> completion = picker.ShowDialog<bool>(owner); Pump(picker); ChooseFirst(picker);
        Edit(picker, Field<TextBox>(picker, "FontSizeInPoints"), size); PushProcessTestDriver.Pointer(Field<Button>(picker, "AcceptFont"));
        Assert.That(picker.IsVisible, Is.True); Assert.That(Field<TextBlock>(picker, "FontFallback").Text, Is.Not.Empty);
        PushProcessTestDriver.Pointer(Field<Button>(picker, "CancelFont")); Assert.That(await completion, Is.False); Assert.That(File.Exists(scope.Store.FilePath), Is.False);
    }

    [AvaloniaTest]
    public async Task UnknownSavedIdentityHasNoFakeSelectionAndUnchangedPickerOkPreservesIt()
    {
        using Scope scope = new(); Window owner = scope.Owner(); FontSpec requested = new("Missing font identity 7741", 13.125, true, true);
        FontPickerDialog picker = scope.Own(new FontPickerDialog(requested, DesktopFontRole.Code)); Task<bool> completion = picker.ShowDialog<bool>(owner); Pump(picker);
        Assert.That(Field<ListBox>(picker, "FontFamily").SelectedItem, Is.Null); Assert.That(Field<TextBlock>(picker, "FontFallback").Text, Does.Contain(requested.Family));
        PushProcessTestDriver.Pointer(Field<Button>(picker, "AcceptFont")); Assert.That(await completion, Is.True); Assert.That(picker.Result, Is.EqualTo(requested));
    }

    [AvaloniaTest]
    public async Task LargeSavedFontButtonsMeasureTheirActualTextWithoutHidingTheSettingsFooter()
    {
        using Scope scope = new(); Window owner = scope.Owner(); string name = DesktopFontResolver.Families(DesktopFontRole.Code)[0].Name;
        FontPreferences initial = new() { Code = new(name, 35, true) };
        DesktopSettingsDialog dialog = scope.Own(new DesktopSettingsDialog(new(), TranslationCatalog.LoadDefault(), false, fonts: initial)); Task<bool> completion = dialog.ShowDialog<bool>(owner); Pump(dialog); OpenFonts(dialog);
        Button preview = Field<Button>(dialog, "diffFontChangeButton");
        double textHeight = new FormattedText("By", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(preview.FontFamily, preview.FontStyle, preview.FontWeight), preview.FontSize, Brushes.Black).Height;
        Assert.That(preview.Bounds.Height, Is.GreaterThanOrEqualTo(textHeight));
        Capture(dialog, "fonts-large-saved-preview.png"); PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await completion, Is.False);
    }

    [AvaloniaTest]
    public async Task BrowseSettingsSavesOnlyDefaultsAndKeepsTheCapturedSessionAndCurrentViewUnchanged()
    {
        using Scope scope = new(); new PreferencesStore().Save(new() { Language = "English" }); DesktopFontSession active = DesktopFonts.Active;
        MainWindow main = scope.Own(new MainWindow()); main.Show(); Pump(main);
        try
        {
            (DesktopSettingsDialog dialog, Task operation) = await OpenSettings(scope, main); OpenFonts(dialog);
            PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "ShowEolMarkerAsGlyph"));
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings")); await operation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(scope.Store.Load().ShowEolMarkerAsGlyph, Is.True); Assert.That(DesktopFonts.Active, Is.SameAs(active)); Assert.That(active.ShowEolMarkerAsGlyph, Is.False);
            Assert.That(DesktopFonts.CreateSession(scope.Store).ShowEolMarkerAsGlyph, Is.True);
        }
        finally { await CloseBrowse(main); }
    }

    [AvaloniaTest]
    public async Task TwoOpenSettingsMergeOnlyChangedFontsAndReturnTheActualLatestResult()
    {
        using Scope scope = new(); Window first = scope.Owner(), second = scope.Owner(); FontPreferences baseline = scope.Store.Load();
        DesktopSettingsDialog a = scope.Own(new DesktopSettingsDialog(new(), TranslationCatalog.LoadDefault(), false, fonts: baseline, saveFonts: scope.Store.SaveChanges));
        DesktopSettingsDialog b = scope.Own(new DesktopSettingsDialog(new(), TranslationCatalog.LoadDefault(), false, fonts: baseline, saveFonts: scope.Store.SaveChanges));
        Task<bool> aa = a.ShowDialog<bool>(first), bb = b.ShowDialog<bool>(second); Pump(a); Pump(b);
        OpenFonts(a); PushProcessTestDriver.Pointer(Field<CheckBox>(a, "ShowEolMarkerAsGlyph"));
        PushProcessTestDriver.Pointer(Field<Button>(a, "SaveDesktopSettings")); Assert.That(await aa, Is.True);
        // The other window keeps an untouched font draft; accepting another page must not undo A.
        SettingsNavigationTestDriver.Select(b, "Appearance"); ComboBox theme = Field<ComboBox>(b, "SettingsTheme"); ChooseOption(b, theme, "Dark");
        PushProcessTestDriver.Pointer(Field<Button>(b, "SaveDesktopSettings")); Assert.That(await bb, Is.True);
        Assert.That(b.FontsResult.ShowEolMarkerAsGlyph, Is.True); Assert.That(scope.Store.Load().ShowEolMarkerAsGlyph, Is.True);
    }

    [AvaloniaTest]
    public async Task BrowseSettingsOpensWithCorruptFontsAndRefusesChangedFontsWithoutReplacingTheBytes()
    {
        using Scope scope = new(); new PreferencesStore().Save(new() { Language = "English" }); Directory.CreateDirectory(Path.GetDirectoryName(scope.Store.FilePath)!);
        File.WriteAllText(scope.Store.FilePath, "{bad fonts"); byte[] before = File.ReadAllBytes(scope.Store.FilePath);
        MainWindow main = scope.Own(new MainWindow()); main.Show(); Pump(main);
        try
        {
            (DesktopSettingsDialog dialog, Task operation) = await OpenSettings(scope, main); OpenFonts(dialog);
            Assert.That(Field<TextBlock>(dialog, "FontsNotice").Text, Does.Contain("Unable to read"));
            PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "ShowEolMarkerAsGlyph")); PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings"));
            Assert.That(dialog.IsVisible, Is.True); Assert.That(File.ReadAllBytes(scope.Store.FilePath), Is.EqualTo(before));
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); await operation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(File.ReadAllBytes(scope.Store.FilePath), Is.EqualTo(before));
        }
        finally { await CloseBrowse(main); }
    }

    [AvaloniaTest]
    public async Task RealSaveFailureIsVisibleAndKeepsTheOwnedSettingsOpenWithoutChangingItsActiveSession()
    {
        using Scope scope = new(); Window owner = scope.Owner(); FontPreferences initial = scope.Store.Load();
        DesktopSettingsDialog dialog = scope.Own(new DesktopSettingsDialog(new(), TranslationCatalog.LoadDefault(), false, fonts: initial, saveFonts: scope.Store.SaveChanges));
        Task<bool> completion = dialog.ShowDialog<bool>(owner); Pump(dialog); OpenFonts(dialog); PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "ShowEolMarkerAsGlyph"));
        // A real incompatible filesystem entry replaces the missing profile after opening the draft.
        Directory.CreateDirectory(scope.Store.FilePath); PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings"));
        Assert.That(dialog.IsVisible, Is.True); Assert.That(Field<TextBlock>(dialog, "FontsNotice").Text, Does.Contain("Unable to save")); Assert.That(Directory.Exists(scope.Store.FilePath), Is.True);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await completion, Is.False);
    }

    [AvaloniaTest]
    public async Task UnavailableFamilyIntentSurvivesAnUnrelatedSettingsChangeWithoutAFalsePickerSelection()
    {
        using Scope scope = new(); Window owner = scope.Owner();
        FontPreferences initial = scope.Store.SaveChanges(new(), new() { Application = new("Unavailable retained identity 8177", 11.125, true) });
        byte[] before = File.ReadAllBytes(scope.Store.FilePath);
        DesktopSettingsDialog dialog = scope.Own(new DesktopSettingsDialog(new(), TranslationCatalog.LoadDefault(), false, fonts: initial, saveFonts: scope.Store.SaveChanges));
        Task<bool> completion = dialog.ShowDialog<bool>(owner); Pump(dialog);
        SettingsNavigationTestDriver.Select(dialog, "Appearance"); ChooseOption(dialog, Field<ComboBox>(dialog, "SettingsTheme"), "Dark");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings")); Assert.That(await completion, Is.True);
        Assert.That(dialog.FontsResult, Is.EqualTo(initial)); Assert.That(File.ReadAllBytes(scope.Store.FilePath), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task CorruptUntouchedFontsDoNotBlockOtherSettingsOrCauseAnImplicitRepair()
    {
        using Scope scope = new(); Window owner = scope.Owner(); Directory.CreateDirectory(Path.GetDirectoryName(scope.Store.FilePath)!);
        File.WriteAllText(scope.Store.FilePath, "{keep exact corruption"); byte[] before = File.ReadAllBytes(scope.Store.FilePath);
        DesktopSettingsDialog dialog = scope.Own(new DesktopSettingsDialog(new(), TranslationCatalog.LoadDefault(), false, saveFonts: scope.Store.SaveChanges));
        Task<bool> completion = dialog.ShowDialog<bool>(owner); Pump(dialog); SettingsNavigationTestDriver.Select(dialog, "Appearance"); ChooseOption(dialog, Field<ComboBox>(dialog, "SettingsTheme"), "Dark");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings")); Assert.That(await completion, Is.True);
        Assert.That(dialog.Result.Theme, Is.EqualTo("Dark")); Assert.That(File.ReadAllBytes(scope.Store.FilePath), Is.EqualTo(before));
    }

    private static void OpenFonts(Window window) { SettingsNavigationTestDriver.Select(window, "Fonts"); Pump(window); }
    private static FontFamily ChooseFirst(Window window)
    {
        ListBox list = Field<ListBox>(window, "FontFamily"); FontFamily family = list.Items.OfType<FontFamily>().First();
        ChooseFamily(window, family); return family;
    }
    private static void ChooseFamily(Window window, FontFamily family)
    {
        ListBox list = Field<ListBox>(window, "FontFamily"); int index = list.Items.OfType<FontFamily>().ToList().FindIndex(item => item.Name == family.Name);
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        // A short list can leave its viewport center below every row. Only an
        // actual row receives keyboard focus; clicking that empty space does not.
        ListBoxItem row = list.GetVisualDescendants().OfType<ListBoxItem>().First(item => item.IsEffectivelyVisible
            && item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), list) is Point center
            && new Rect(list.Bounds.Size).Contains(center));
        PushProcessTestDriver.Pointer(row); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(window, PhysicalKey.Home);
        for (int i = 0; i < index; i++) { Press(window, PhysicalKey.ArrowDown); }
        Assert.That((list.SelectedItem as FontFamily)?.Name, Is.EqualTo(family.Name));
    }
    private static void Edit(Window window, TextBox box, string text)
    { PushProcessTestDriver.Pointer(box); window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control); window.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control); window.KeyTextInput(text); Pump(window); Assert.That(box.Text, Is.EqualTo(text)); }
    private static void ChooseOption(Window window, ComboBox combo, object value)
    {
        PushProcessTestDriver.Pointer(combo); Pump(window); Assert.That(combo.IsDropDownOpen, Is.True); Press(window, PhysicalKey.Home);
        int index = combo.Items.Cast<object>().ToList().IndexOf(value); Assert.That(index, Is.GreaterThanOrEqualTo(0));
        for (int i = 0; i < index; i++) { Press(window, PhysicalKey.ArrowDown); } Press(window, PhysicalKey.Enter); Assert.That(combo.SelectedItem, Is.EqualTo(value));
    }
    private static T Field<T>(TopLevel window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Press(Window window, PhysicalKey key)
    { window.KeyPressQwerty(key, RawInputModifiers.None); if (window.IsVisible) { window.KeyReleaseQwerty(key, RawInputModifiers.None); Pump(window); } }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    { DateTime end = DateTime.UtcNow.AddSeconds(10); while (!condition() && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True); }
    private static async Task<(DesktopSettingsDialog, Task)> OpenSettings(Scope scope, MainWindow main)
    {
        Task operation = (Task)typeof(MainWindow).GetMethod("ShowSettingsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null)!;
        await Until(() => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any()); DesktopSettingsDialog dialog = scope.Own(main.OwnedWindows.OfType<DesktopSettingsDialog>().Single()); Pump(dialog);
        bool checklistOnOpen = dialog.Navigation.SelectedPage.Id == "Checklist";
        // Opening Settings reads its scoped pages before a possible Checklist
        // scan. Wait for the real save action, including that initial scan.
        await Until(() => Field<Button>(dialog, "SaveDesktopSettings").IsEffectivelyEnabled
            && dialog.GitEnvironmentSession is { IsBusy: false } session
            && (!checklistOnOpen || session.LastScan is not null));
        Pump(dialog); return (dialog, operation);
    }
    private static async Task CloseBrowse(MainWindow main)
    { main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted); await main.WindowWorkCompletion; }
    private static void Capture(TopLevel window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string file = Path.Combine(directory, name); frame!.Save(file); TestContext.AddTestAttachment(file);
    }
    private sealed class Scope : IDisposable
    {
        private readonly string? _profile = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), _override = Environment.GetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE");
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture, _ui = CultureInfo.CurrentUICulture;
        private readonly ThemeVariant? _theme = Application.Current?.RequestedThemeVariant;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-font-settings-" + Guid.NewGuid().ToString("N"));
        private readonly List<Window> _windows = [];
        internal FontPreferencesStore Store { get; }
        internal Scope(string language = "en")
        { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root); Environment.SetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE", null); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language); CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US"); Store = new(); }
        internal T Own<T>(T window) where T : Window { _windows.Add(window); return window; }
        internal Window Owner(bool dark = false) { Window owner = Own(new Window { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }); owner.Show(); Pump(owner); return owner; }
        public void Dispose()
        {
            try { foreach (Window window in _windows.AsEnumerable().Reverse()) { if (window.IsVisible) { window.Close(); } } }
            finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _profile); Environment.SetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE", _override); CultureInfo.CurrentCulture = _culture; CultureInfo.CurrentUICulture = _ui;
                if (Application.Current is { } application) { application.RequestedThemeVariant = _theme; } if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
        }
    }
}

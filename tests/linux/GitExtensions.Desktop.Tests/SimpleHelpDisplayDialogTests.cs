using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
using GitExtensions.Linux.Scripts;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class SimpleHelpDisplayDialogTests
{
    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("fr", true)]
    public void OriginalSheetIsReadonlyWrappedScrollableAndHasNoAcceptOrCancelAction(string language, bool dark)
    {
        using PortableHelperSettingsFixture f = new(language, dark);
        string literal = string.Join('\n', Enumerable.Range(0, 100).Select(index =>
            index + " literal {UserInput:a popup label=Ω} https://example.invalid/ " + new string('x', 90)));
        SimpleHelpDisplayDialog sheet = new() { DialogTitle = "Literal help Ω", ContentText = literal, RequestedThemeVariant = f.Theme };
        f.Track(sheet); sheet.Show(f.Owner); PortableHelperSettingsFixture.Pump(sheet);
        TextBox text = PortableHelperSettingsFixture.Field<TextBox>(sheet, "textBox1");
        Assert.That(sheet.Name, Is.EqualTo("SimpleHelpDisplayDialog")); Assert.That(sheet.Title, Is.EqualTo("Literal help Ω"));
        Assert.That(sheet.ClientSize, Is.EqualTo(new Size(299, 439))); Assert.That(text.Bounds.Size, Is.EqualTo(sheet.ClientSize));
        Assert.That(sheet.CanMinimize || sheet.CanMaximize || sheet.ShowInTaskbar, Is.False); Assert.That(sheet.CanResize, Is.True);
        Assert.That(sheet.Content, Is.SameAs(text));
        Assert.That(sheet.GetVisualDescendants().OfType<Button>().Where(button => button.IsDefault || button.IsCancel), Is.Empty);
        Assert.That(text.Text, Is.EqualTo(literal)); Assert.That(text.IsReadOnly && text.AcceptsReturn, Is.True);
        Assert.That(text.TextWrapping, Is.EqualTo(TextWrapping.Wrap));
        Assert.That(ScrollViewer.GetHorizontalScrollBarVisibility(text), Is.EqualTo(ScrollBarVisibility.Disabled));
        Assert.That(ScrollViewer.GetVerticalScrollBarVisibility(text), Is.EqualTo(ScrollBarVisibility.Visible));
        Assert.That((text.SelectionStart, text.SelectionEnd, text.CaretIndex), Is.EqualTo((0, 0, 0)));
        Assert.That(((ISolidColorBrush)text.Background!).Color, Is.EqualTo(Color.Parse(ClassicStyles.OriginalColors(dark)["Info"])));
        Assert.That(((ISolidColorBrush)text.Foreground!).Color, Is.EqualTo(Color.Parse(ClassicStyles.OriginalColors(dark)["InfoText"])));
        PushProcessTestDriver.Pointer(text); Assert.That(text.IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(sheet, PhysicalKey.A, RawInputModifiers.Control);
        Assert.That(Math.Abs(text.SelectionEnd - text.SelectionStart), Is.EqualTo(literal.Length));
        sheet.KeyTextInput("cannot replace the readonly help"); PortableHelperSettingsFixture.Press(sheet, PhysicalKey.Enter);
        PortableHelperSettingsFixture.Press(sheet, PhysicalKey.Escape);
        Assert.That(text.Text, Is.EqualTo(literal)); Assert.That(sheet.IsVisible, Is.True, "Windows defines neither AcceptButton nor CancelButton.");
        ScrollViewer scroll = text.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Point point = text.TranslatePoint(new Point(30, text.Bounds.Height - 30), sheet)!.Value;
        sheet.MouseMove(point); sheet.MouseWheel(point, new Vector(0, -5), RawInputModifiers.None); PortableHelperSettingsFixture.Pump(sheet);
        Assert.That(scroll.Offset.Y, Is.GreaterThan(0)); Assert.That(scroll.Extent.Height, Is.GreaterThan(scroll.Viewport.Height));
        sheet.Width = 410; sheet.Height = 310; PortableHelperSettingsFixture.Pump(sheet);
        Assert.That(text.Bounds.Size, Is.EqualTo(sheet.ClientSize)); Assert.That(scroll.Offset.X, Is.Zero);
        Assert.That(f.Owner.IsEffectivelyEnabled, Is.True); Assert.That(f.ScriptSaves, Is.Zero);
    }

    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("fr", true)]
    public async Task ScriptsUseTheSharedOwnedSheetAndReuseSelectionWhileTheSettingsDraftStaysEditable(string language, bool dark)
    {
        using PortableHelperSettingsFixture f = new(language, dark); byte[] baseline = File.ReadAllBytes(f.Scripts.FilePath);
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Scripts");
        PushProcessTestDriver.Pointer(PortableHelperSettingsFixture.Field<Button>(settings, "btnArgumentsHelp"));
        SimpleHelpDisplayDialog sheet = settings.OwnedWindows.OfType<SimpleHelpDisplayDialog>().Single(); PortableHelperSettingsFixture.Pump(sheet);
        Assert.That(sheet.Name, Is.EqualTo("ScriptArgumentsHelp")); Assert.That(sheet.Owner, Is.SameAs(settings));
        Assert.That(sheet.ActualThemeVariant, Is.EqualTo(f.Theme)); Assert.That(settings.IsEffectivelyEnabled, Is.True);
        TextBox help = PortableHelperSettingsFixture.Field<TextBox>(sheet, "ScriptArgumentsHelpText");
        Assert.That(sheet.Title, Is.EqualTo(f.Original("ScriptsSettingsPage", "_scriptSettingsPageHelpDisplayArgumentsHelp.Text")));
        Assert.That(help.Text, Is.EqualTo(f.Original("ScriptsSettingsPage", "_scriptSettingsPageHelpDisplayContent.Text")));
        Assert.That((help.CaretIndex, help.SelectionStart, help.SelectionEnd), Is.EqualTo((0, 0, 0)));
        PushProcessTestDriver.Pointer(help);
        PortableHelperSettingsFixture.Press(sheet, PhysicalKey.Home, RawInputModifiers.Control);
        for (int step = 0; step < 2; step++) { PortableHelperSettingsFixture.Press(sheet, PhysicalKey.ArrowRight); }
        for (int step = 0; step < 10; step++) { PortableHelperSettingsFixture.Press(sheet, PhysicalKey.ArrowRight, RawInputModifiers.Shift); }
        string selected = help.Text![2..12];
        Assert.That(help.IsKeyboardFocusWithin, Is.True);
        Assert.That((help.SelectionStart, help.SelectionEnd), Is.EqualTo((2, 12)), "The real keyboard must establish the selection before blur.");
        int selectedCaret = help.CaretIndex;
        Assert.That(help.SelectedText, Is.EqualTo(selected));
        f.Edit(settings, "ScriptName", "draft edited while the help stays open");
        Assert.That(PortableHelperSettingsFixture.Field<TextBox>(settings, "ScriptName").IsKeyboardFocusWithin, Is.True);
        Assert.That(help.IsKeyboardFocusWithin, Is.False, "The parent edit must really move keyboard focus out of the sheet.");
        Assert.That((help.SelectionStart, help.SelectionEnd, help.CaretIndex), Is.EqualTo((2, 12, selectedCaret)), "Hiding inactive selection must not discard the selected range.");
        Assert.That(help.SelectedText, Is.EqualTo(selected));
        PushProcessTestDriver.Pointer(PortableHelperSettingsFixture.Field<Button>(settings, "btnArgumentsHelp"));
        PortableHelperSettingsFixture.Pump(sheet);
        Assert.That(settings.OwnedWindows.OfType<SimpleHelpDisplayDialog>().Single(), Is.SameAs(sheet));
        Assert.That((help.SelectionStart, help.SelectionEnd, help.CaretIndex), Is.EqualTo((2, 12, selectedCaret)), "BringToFront must not reset an existing sheet's selection.");
        Assert.That(help.SelectedText, Is.EqualTo(selected));
        Assert.That(PortableHelperSettingsFixture.Field<ScriptsSettingsPage>(settings, "ScriptsSettingsPage").Draft[0].Name,
            Is.EqualTo("draft edited while the help stays open"));
        PortableHelperSettingsFixture.Capture(sheet, "simple-help-scripts-" + language + (dark ? "-dark.png" : "-light.png"));
        SettingsNavigationTestDriver.Select(settings, "Plugins");
        Assert.That(sheet.IsVisible, Is.False, "Windows ScriptsSettingsPage.OnParentChanged closes help when its page is removed.");
        SettingsNavigationTestDriver.Select(settings, "Scripts");
        PushProcessTestDriver.Pointer(PortableHelperSettingsFixture.Field<Button>(settings, "btnArgumentsHelp"));
        SimpleHelpDisplayDialog replacement = settings.OwnedWindows.OfType<SimpleHelpDisplayDialog>().Single();
        Assert.That(replacement, Is.Not.SameAs(sheet));
        Assert.That(PortableHelperSettingsFixture.Field<TextBox>(replacement, "ScriptArgumentsHelpText").SelectionEnd, Is.Zero);
        Assert.That(PortableHelperSettingsFixture.Field<ScriptsSettingsPage>(settings, "ScriptsSettingsPage").Draft[0].Name,
            Is.EqualTo("draft edited while the help stays open"));
        PushProcessTestDriver.Pointer(PortableHelperSettingsFixture.Field<Button>(settings, "CancelDesktopSettings"));
        Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.False); Assert.That(replacement.IsVisible, Is.False);
        Assert.That(File.ReadAllBytes(f.Scripts.FilePath), Is.EqualTo(baseline)); Assert.That(f.ScriptSaves, Is.Zero);
    }

    [AvaloniaTest]
    public async Task ClosingTheSheetLeavesSettingsOpenAndTheReplacementClosesWithItsOwner()
    {
        using PortableHelperSettingsFixture f = new(); (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Scripts");
        Button helpButton = PortableHelperSettingsFixture.Field<Button>(settings, "btnArgumentsHelp");
        PushProcessTestDriver.Pointer(helpButton); Window first = settings.OwnedWindows.OfType<SimpleHelpDisplayDialog>().Single();
        first.Close(); PortableHelperSettingsFixture.Pump(settings);
        Assert.That(settings.IsVisible && settings.IsEffectivelyEnabled, Is.True); Assert.That(completion.IsCompleted, Is.False);
        PushProcessTestDriver.Pointer(helpButton); Window replacement = settings.OwnedWindows.OfType<SimpleHelpDisplayDialog>().Single();
        Assert.That(replacement, Is.Not.SameAs(first)); Assert.That(replacement.IsVisible, Is.True);
        settings.Close(); await completion.WaitAsync(TimeSpan.FromSeconds(10)); PortableHelperSettingsFixture.Pump(f.Owner);
        Assert.That(replacement.IsVisible, Is.False); Assert.That(f.ScriptSaves, Is.Zero);
    }

    [AvaloniaTest]
    public async Task IndependentSettingsOwnersNeverShareOrCloseEachOthersHelpSheet()
    {
        using PortableHelperSettingsFixture f = new();
        (DesktopSettingsDialog a, Task<bool> aDone) = f.Open("Scripts");
        (DesktopSettingsDialog b, Task<bool> bDone) = f.Open("Scripts", f.NewOwner());
        PushProcessTestDriver.Pointer(PortableHelperSettingsFixture.Field<Button>(a, "btnArgumentsHelp"));
        PushProcessTestDriver.Pointer(PortableHelperSettingsFixture.Field<Button>(b, "btnArgumentsHelp"));
        SimpleHelpDisplayDialog first = a.OwnedWindows.OfType<SimpleHelpDisplayDialog>().Single();
        SimpleHelpDisplayDialog second = b.OwnedWindows.OfType<SimpleHelpDisplayDialog>().Single();
        Assert.That(first, Is.Not.SameAs(second)); Assert.That((first.Owner, second.Owner), Is.EqualTo((a, b)));
        PushProcessTestDriver.Pointer(PortableHelperSettingsFixture.Field<Button>(a, "CancelDesktopSettings"));
        Assert.That(await aDone.WaitAsync(TimeSpan.FromSeconds(10)), Is.False); Assert.That(first.IsVisible, Is.False);
        Assert.That(b.IsVisible && second.IsVisible, Is.True);
        PushProcessTestDriver.Pointer(PortableHelperSettingsFixture.Field<Button>(b, "CancelDesktopSettings"));
        Assert.That(await bDone.WaitAsync(TimeSpan.FromSeconds(10)), Is.False); Assert.That(second.IsVisible, Is.False);
    }
}

internal sealed class PortableHelperSettingsFixture : IDisposable
{
    private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private readonly string? _uiOverride = Environment.GetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE");
    private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "portable-helper-ui-" + Guid.NewGuid().ToString("N"));
    private readonly List<Window> _windows = [];
    private readonly string _language;
    internal ScriptsPreferencesStore Scripts { get; }
    internal TranslationCatalog Translations { get; }
    internal Window Owner { get; }
    internal ThemeVariant Theme { get; }
    internal int ScriptSaves { get; private set; }
    internal int Publications { get; private set; }

    internal PortableHelperSettingsFixture(string language = "en", bool dark = false)
    {
        _language = language; Directory.CreateDirectory(_root); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);
        Environment.SetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE", null); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        Translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
        Scripts = new(Path.Combine(_root, "scripts.json"));
        Scripts.Save([new() { HotkeyCommandIdentifier = 9001, Name = "First", Command = "must-not-execute", Enabled = false }]);
        Theme = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Owner = new() { Width = 1100, Height = 800, RequestedThemeVariant = Theme }; Track(Owner); Owner.Show(); Pump(Owner);
    }

    internal void Track(Window window) => _windows.Add(window);
    internal Window NewOwner()
    { Window owner = new() { Width = 1100, Height = 800, RequestedThemeVariant = Theme }; Track(owner); owner.Show(); Pump(owner); return owner; }
    internal (DesktopSettingsDialog, Task<bool>) Open(string initialPage, Window? owner = null)
    {
        DesktopSettingsDialog settings = new(new(), Translations, false, fileHistoryPreferences: new(), hotkeys: new(), fonts: new(),
            scripts: Scripts.Load(), saveScripts: (baseline, draft) => { ScriptSaves++; return Scripts.SaveChanges(baseline, draft); },
            publishSettings: _ => { Publications++; return Task.CompletedTask; }, initialPage: initialPage)
        { RequestedThemeVariant = Theme };
        Track(settings); Task<bool> completion = settings.ShowDialog<bool>(owner ?? Owner); Pump(settings);
        Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo(initialPage));
        return (settings, completion);
    }

    internal string Original(string category, string id)
    {
        string filename = _language == "fr" ? "French.xlf" : "English.xlf";
        XDocument xml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Translation", filename));
        XElement file = xml.Root!.Elements().Single(element => element.Name.LocalName == "file" && (string?)element.Attribute("original") == category);
        XElement unit = file.Descendants().Single(element => element.Name.LocalName == "trans-unit" && (string?)element.Attribute("id") == id);
        string field = _language == "fr" ? "target" : "source";
        return unit.Elements().Single(element => element.Name.LocalName == field).Value.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    internal void Edit(Window window, string name, string value)
    {
        TextBox input = Field<TextBox>(window, name); input.BringIntoView(); Pump(window); PushProcessTestDriver.Pointer(input);
        Press(window, PhysicalKey.A, RawInputModifiers.Control); window.KeyTextInput(value); Pump(window); Assert.That(input.Text, Is.EqualTo(value));
    }
    internal static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    internal static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    internal static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); Pump(window); } }
    internal static void Capture(TopLevel window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string root = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(root); string file = Path.Combine(root, name); frame!.Save(file); TestContext.AddTestAttachment(file);
    }
    public void Dispose()
    {
        try { foreach (Window window in _windows.AsEnumerable().Reverse()) { if (window.IsVisible) { window.Close(); Pump(window); } } }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); Environment.SetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE", _uiOverride);
            CultureInfo.CurrentUICulture = _culture; if (Directory.Exists(_root)) { Directory.Delete(_root, true); }
        }
    }
}

using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AdvancedSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalGroupsChoicesAndNonEditableSymbolsAreReadableAndUseRealKeyboardInput(string language, bool dark)
    {
        using PortableHelperSettingsFixture f = new(language, dark);
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Advanced");
        AdvancedSettingsPage page = AdvancedSettingsUiDriver.Field<AdvancedSettingsPage>(settings, "AdvancedSettingsPage");
        Assert.That(page.GetVisualDescendants().OfType<CheckBox>().Count(), Is.EqualTo(5));
        Assert.That(page.GetVisualDescendants().OfType<Control>().Any(control => control.Name is "chkCheckForUpdates" or "chkCheckForRCVersions" or "chkConsoleEmulator" or "chkDontSHowHelpImages"), Is.False);
        foreach (AdvancedSetting setting in AdvancedSettingsCatalog.Fields.Where(setting => !setting.IsSymbol))
        {
            CheckBox box = AdvancedSettingsUiDriver.Field<CheckBox>(settings, setting.Control); box.BringIntoView(); PortableHelperSettingsFixture.Pump(settings);
            TextBlock label = (TextBlock)box.Content!;
            string text = f.Original("AdvancedSettingsPage", setting.OriginalControl + ".Text").Replace("&&", "&", StringComparison.Ordinal);
            Assert.That(label.Text, Is.EqualTo(text));
            Point origin = label.TranslatePoint(default, box)!.Value;
            FormattedText glyphs = new(text, f.Translations.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize, Brushes.Black)
                { MaxTextWidth = Math.Max(1, box.Bounds.Width - origin.X - box.Padding.Right) };
            Assert.That(origin.X + glyphs.Width, Is.LessThanOrEqualTo(box.Bounds.Width + .5), setting.Control);
            Assert.That(origin.Y + glyphs.Height, Is.LessThanOrEqualTo(box.Bounds.Height + .5), setting.Control);
        }
        ComboBox symbol = AdvancedSettingsUiDriver.Field<ComboBox>(settings, "SettingsNormaliseSymbol");
        Assert.That(symbol.IsEditable, Is.False); Assert.That(symbol.Items.OfType<AdvancedSymbolChoice>().Select(choice => choice.Caption), Is.EqualTo(new[] { "_", "-", "(none)" }));
        CheckBox first = AdvancedSettingsUiDriver.Field<CheckBox>(settings, "chkAlwaysShowCheckoutDlg"); first.BringIntoView(); PortableHelperSettingsFixture.Pump(settings);
        PortableHelperSettingsFixture.Capture(settings, "advanced-local-" + language + (dark ? "-dark.png" : "-light.png"));
        PushProcessTestDriver.Pointer(first); Assert.That(page.Draft.AlwaysShowCheckoutDialog, Is.True); Assert.That(first.IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.Tab); Assert.That(AdvancedSettingsUiDriver.Field<CheckBox>(settings, "chkUseLocalChangesAction").IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.Enter); Assert.That(settings.IsVisible && !completion.IsCompleted, Is.True);
        AdvancedSettingsUiDriver.ChooseSymbol(settings, ""); Assert.That(page.Draft.AutoNormaliseSymbol, Is.Empty);
        AdvancedSettingsUiDriver.Set(settings, "SettingsNormaliseBranches", false); Assert.That(symbol.IsEnabled, Is.False);
        AdvancedSettingsUiDriver.Set(settings, "SettingsNormaliseBranches", true); Assert.That(page.Draft.AutoNormaliseSymbol, Is.Empty);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await completion, Is.False);
        Assert.That(File.Exists(new PreferencesStore().FilePath), Is.False); Assert.That(File.Exists(new CommitPreferencesStore().FilePath), Is.False);
        Assert.That(File.Exists(AdvancedSettingsUiDriver.ReferencePath), Is.False);
    }

    [AvaloniaTest]
    public async Task ApplyReopenAndCancelRetainOnlyTheSavedPageAndOriginalNoneValue()
    {
        using PortableHelperSettingsFixture f = new(); (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Advanced");
        foreach (string name in new[] { "chkAlwaysShowCheckoutDlg", "chkUseLocalChangesAction", "chkCommitAndPushForcedWhenAmend" }) { AdvancedSettingsUiDriver.Set(settings, name, true); }
        AdvancedSettingsUiDriver.ChooseSymbol(settings, ""); await AdvancedSettingsUiDriver.Apply(f, settings, 1);
        Assert.That(new ReferencePreferencesStore().Load().AlwaysShowCheckoutDialog, Is.True);
        Assert.That(new ReferencePreferencesStore().Load().UseDefaultCheckoutChanges, Is.True); Assert.That(new CommitPreferencesStore().Load().CommitAndPushForcedWhenAmend, Is.True);
        Assert.That(new PreferencesStore().Load().AutoNormaliseSymbol, Is.Empty);
        Dictionary<string, byte[]> accepted = AdvancedSettingsUiDriver.Profiles();
        AdvancedSettingsUiDriver.Set(settings, "chkUseLocalChangesAction", false); AdvancedSettingsUiDriver.ChooseSymbol(settings, "-");
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await completion, Is.True);
        AdvancedSettingsUiDriver.SameProfiles(accepted);
        (DesktopSettingsDialog reopened, Task<bool> reopenedCompletion) = f.Open("Advanced");
        Assert.That(AdvancedSettingsUiDriver.Field<AdvancedSettingsPage>(reopened, "AdvancedSettingsPage").Draft, Is.EqualTo(new AdvancedSettingsSession().Initial));
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(reopened, "CancelDesktopSettings")); await reopenedCompletion;
        AdvancedSettingsUiDriver.SameProfiles(accepted);
    }

    [AvaloniaTest]
    public async Task PartialApplyShowsTheErrorAndRetryKeepsAnAcceptedCommitFieldAndItsPeerChanges()
    {
        using PortableHelperSettingsFixture f = new(); (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Advanced");
        AdvancedSettingsUiDriver.Set(settings, "chkCommitAndPushForcedWhenAmend", true); AdvancedSettingsUiDriver.Set(settings, "chkAlwaysShowCheckoutDlg", true);
        Directory.CreateDirectory(AdvancedSettingsUiDriver.ReferencePath);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => f.Publications == 1 && settings.Navigation.IsEnabled);
        Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo("Advanced")); Assert.That(AdvancedSettingsUiDriver.Field<TextBlock>(settings, "SettingsNotice").Text, Does.Contain("Unable to save"));
        Assert.That(new CommitPreferencesStore().Load().CommitAndPushForcedWhenAmend, Is.True);
        Assert.That(AdvancedSettingsUiDriver.Field<CheckBox>(settings, "chkAlwaysShowCheckoutDlg").IsChecked, Is.True);
        CommitPreferencesStore commit = new(); JsonObject peer = JsonNode.Parse(File.ReadAllBytes(commit.FilePath))!.AsObject();
        peer["CommitAndPushForcedWhenAmend"] = false; peer["LastCommitMessage"] = "peer draft Ω"; peer["DontConfirmAmend"] = true;
        File.WriteAllText(commit.FilePath, peer.ToJsonString()); byte[] bytes = File.ReadAllBytes(commit.FilePath); Directory.Delete(AdvancedSettingsUiDriver.ReferencePath);
        await AdvancedSettingsUiDriver.Apply(f, settings, 2);
        Assert.That(File.ReadAllBytes(commit.FilePath), Is.EqualTo(bytes)); Assert.That(new ReferencePreferencesStore().Load().AlwaysShowCheckoutDialog, Is.True);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await completion, Is.True);
        Assert.That(File.ReadAllBytes(commit.FilePath), Is.EqualTo(bytes));
    }

    [AvaloniaTest]
    public async Task AForeignSymbolIsShownWithoutAnOpenOrCancelWriteAndCanBeExplicitlyReplacedByNone()
    {
        using PortableHelperSettingsFixture f = new(); string path = new PreferencesStore().FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{ \"AutoNormaliseSymbol\": \"foreign-token\", \"foreign\": [17,null] }\n"); byte[] before = File.ReadAllBytes(path);
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Advanced");
        ComboBox symbol = AdvancedSettingsUiDriver.Field<ComboBox>(settings, "SettingsNormaliseSymbol");
        Assert.That(symbol.SelectedItem, Is.Null); Assert.That(symbol.PlaceholderText, Is.EqualTo("foreign-token"));
        Assert.That(symbol.Items.OfType<AdvancedSymbolChoice>().Select(choice => choice.Value), Is.EqualTo(new[] { "_", "-", "" }));
        Assert.That(AdvancedSettingsUiDriver.Field<AdvancedSettingsPage>(settings, "AdvancedSettingsPage").Draft.AutoNormaliseSymbol, Is.EqualTo("foreign-token"));
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); await completion;
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
        (settings, completion) = f.Open("Advanced"); AdvancedSettingsUiDriver.ChooseSymbol(settings, ""); await AdvancedSettingsUiDriver.Apply(f, settings, 1);
        Assert.That(new PreferencesStore().Load().AutoNormaliseSymbol, Is.Empty);
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllBytes(path))!["foreign"], JsonNode.Parse("[17,null]")), Is.True);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); await completion;
    }
}

internal static class AdvancedSettingsUiDriver
{
    internal static string ReferencePath => Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "reference-preferences.json");
    internal static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    internal static void Set(Window window, string name, bool value)
    {
        CheckBox box = Field<CheckBox>(window, name); box.BringIntoView(); PortableHelperSettingsFixture.Pump(window);
        if (box.IsChecked != value) { PushProcessTestDriver.Pointer(box); }
        Assert.That(box.IsChecked, Is.EqualTo(value));
    }
    internal static void ChooseSymbol(Window window, string value)
    {
        ComboBox box = Field<ComboBox>(window, "SettingsNormaliseSymbol"); box.BringIntoView(); PortableHelperSettingsFixture.Pump(window);
        int index = box.Items.OfType<AdvancedSymbolChoice>().Select(choice => choice.Value).ToList().IndexOf(value); Assert.That(index, Is.GreaterThanOrEqualTo(0));
        PushProcessTestDriver.Pointer(box); Assert.That(box.IsDropDownOpen, Is.True);
        PortableHelperSettingsFixture.Press(window, PhysicalKey.Home);
        for (int step = 0; step < index; step++) { PortableHelperSettingsFixture.Press(window, PhysicalKey.ArrowDown); }
        PortableHelperSettingsFixture.Press(window, PhysicalKey.Enter);
        Assert.That(((AdvancedSymbolChoice)box.SelectedItem!).Value, Is.EqualTo(value)); Assert.That(window.IsVisible, Is.True);
    }
    internal static async Task Apply(PortableHelperSettingsFixture fixture, DesktopSettingsDialog settings, int publications)
    {
        PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => fixture.Publications == publications && settings.Navigation.IsEnabled);
        Assert.That(Field<TextBlock>(settings, "SettingsNotice").Text, Is.Null.Or.Empty);
    }
    internal static Dictionary<string, byte[]> Profiles()
    { string directory = Path.GetDirectoryName(new PreferencesStore().FilePath)!; return Directory.Exists(directory) ? Directory.GetFiles(directory).ToDictionary(path => path, File.ReadAllBytes) : new(); }
    internal static void SameProfiles(Dictionary<string, byte[]> expected)
    {
        Dictionary<string, byte[]> actual = Profiles(); Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
        foreach ((string path, byte[] bytes) in expected) { Assert.That(actual[path], Is.EqualTo(bytes)); }
    }
}

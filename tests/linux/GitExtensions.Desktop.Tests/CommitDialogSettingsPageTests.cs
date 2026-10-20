using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommitDialogSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalGlobalGroupsAndSevenControlsUseExistingCaptionsAndRealInputWithoutCancelWrites(string language, bool dark)
    {
        using PortableHelperSettingsFixture f = new(language, dark);
        Dictionary<string, byte[]> before = AdvancedSettingsUiDriver.Profiles();
        (DesktopSettingsDialog settings, Task<bool> closed) = f.Open("Commit");
        CommitDialogSettingsPage page = Field<CommitDialogSettingsPage>(settings, "CommitDialogSettingsPage");
        Assert.That(page.GetVisualDescendants().OfType<CheckBox>().Count(), Is.EqualTo(6));
        Assert.That(page.GetVisualDescendants().OfType<NumericUpDown>().Count(), Is.EqualTo(1));
        foreach (CommitDialogSetting choice in CommitDialogSettingsCatalog.Fields.Where(item => !item.IsNumber))
        {
            CheckBox box = Field<CheckBox>(settings, choice.Control); box.BringIntoView(); PortableHelperSettingsFixture.Pump(settings);
            Assert.That(((TextBlock)box.Content!).Text, Is.EqualTo(f.Original("CommitDialogSettingsPage", choice.Control + ".Text").Replace("&&", "&", StringComparison.Ordinal)));
            Assert.That(box.Bounds.Width, Is.GreaterThan(200)); Assert.That(box.Bounds.Height, Is.GreaterThanOrEqualTo(24));
        }
        Assert.That(Field<TextBlock>(settings, "lblCommitDialogNumberOfPreviousMessages").Text,
            Is.EqualTo(f.Original("CommitDialogSettingsPage", "lblCommitDialogNumberOfPreviousMessages.Text")));
        foreach (string group in new[] { "groupBoxBehaviour", "grpAdditionalButtons" })
        {
            Border caption = Field<Border>(settings, group + "Caption");
            Assert.That(caption.GetVisualDescendants().OfType<TextBlock>().Single().Text, Is.EqualTo(f.Original("CommitDialogSettingsPage", group + ".Text")));
        }
        Assert.That(page.GetVisualDescendants().OfType<Control>().Any(control => control.Name is "chkShowErrorsWhenStagingFiles" or "chkWriteCommitMessageInCommitWindow"), Is.False);
        NumericUpDown number = Field<NumericUpDown>(settings, "_NO_TRANSLATE_CommitDialogNumberOfPreviousMessages");
        Assert.That(number.Minimum, Is.EqualTo(1)); Assert.That(number.Maximum, Is.EqualTo(999));
        CheckBox autocomplete = Field<CheckBox>(settings, "chkAutocomplete"); autocomplete.BringIntoView(); PortableHelperSettingsFixture.Pump(settings);
        PortableHelperSettingsFixture.Capture(settings, $"commit-settings-{language}-{(dark ? "dark" : "light")}.png");
        PushProcessTestDriver.Pointer(autocomplete); Assert.That(page.Draft.ProvideAutocompletion, Is.False);
        Assert.That(autocomplete.IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.Tab); Assert.That(Field<CheckBox>(settings, "chkEnsureCommitMessageSecondLineEmpty").IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.Enter); Assert.That(settings.IsVisible && !closed.IsCompleted, Is.True);
        Number(settings, "19"); Assert.That(page.Draft.NumberOfPreviousMessages, Is.EqualTo(19));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await closed, Is.False);
        Assert.That(f.Publications, Is.Zero); AdvancedSettingsUiDriver.SameProfiles(before);
    }

    [AvaloniaTest]
    public async Task ApplyReopenAndCancelPersistOnlyEditedGlobalFieldsAndPreserveDictionaryAndForeignJson()
    {
        using PortableHelperSettingsFixture f = new(); PreferencesStore desktop = new(); CommitPreferencesStore commit = new();
        desktop.Save(new() { Dictionary = "none", MarkIllFormedLinesInCommitMsg = false, AutoNormaliseSymbol = "-" });
        commit.Save(new() { DontConfirmAmend = true, CommitAndPushForcedWhenAmend = true });
        JsonNode foreign = JsonNode.Parse("{\"list\":[17,null,\"Ω\"]}")!;
        foreach (string path in new[] { desktop.FilePath, commit.FilePath })
        { JsonObject raw = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); raw["future"] = foreign.DeepClone(); File.WriteAllText(path, raw.ToJsonString()); }
        (DesktopSettingsDialog settings, Task<bool> closed) = f.Open("Commit", initial: desktop.Load());
        foreach (CommitDialogSetting choice in CommitDialogSettingsCatalog.Fields.Where(item => !item.IsNumber))
        { AdvancedSettingsUiDriver.Set(settings, choice.Control, false); }
        Number(settings, "3"); await AdvancedSettingsUiDriver.Apply(f, settings, 1);
        CommitDialogSettingsValues expected = new() { ProvideAutocompletion = false, EnsureCommitMessageSecondLineEmpty = false, NumberOfPreviousMessages = 3, RememberAmendCommitState = false, ShowCommitAndPush = false, ShowResetWorkTreeChanges = false, ShowResetAllChanges = false };
        Assert.That(new CommitDialogSettingsSession().Initial, Is.EqualTo(expected));
        Assert.That(desktop.Load().Dictionary, Is.EqualTo("none")); Assert.That(desktop.Load().MarkIllFormedLinesInCommitMsg, Is.False);
        Assert.That(desktop.Load().AutoNormaliseSymbol, Is.EqualTo("-")); Assert.That(commit.Load().DontConfirmAmend && commit.Load().CommitAndPushForcedWhenAmend, Is.True);
        foreach (string path in new[] { desktop.FilePath, commit.FilePath })
        { Assert.That(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllBytes(path))!["future"], foreign), Is.True); }
        Dictionary<string, byte[]> accepted = AdvancedSettingsUiDriver.Profiles();
        AdvancedSettingsUiDriver.Set(settings, "chkShowResetAllChanges", true); Number(settings, "9");
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await closed, Is.True); AdvancedSettingsUiDriver.SameProfiles(accepted);
        (settings, closed) = f.Open("Commit", initial: desktop.Load());
        Assert.That(Field<CommitDialogSettingsPage>(settings, "CommitDialogSettingsPage").Draft, Is.EqualTo(expected));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await closed; AdvancedSettingsUiDriver.SameProfiles(accepted);
    }

    [AvaloniaTest]
    [TestCase("0")] [TestCase("1000")]
    public async Task AnEditedCountOutsideTheOriginalRangeCannotPublishAnyProfile(string value)
    {
        using PortableHelperSettingsFixture f = new(); Dictionary<string, byte[]> before = AdvancedSettingsUiDriver.Profiles();
        (DesktopSettingsDialog settings, Task<bool> closed) = f.Open("Commit"); Number(settings, value);
        PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => settings.Navigation.IsEnabled && !string.IsNullOrEmpty(Field<TextBlock>(settings, "SettingsNotice").Text));
        Assert.That(Field<TextBlock>(settings, "SettingsNotice").Text, Does.Contain("1 and 999")); Assert.That(f.Publications, Is.Zero);
        AdvancedSettingsUiDriver.SameProfiles(before); PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await closed, Is.False);
    }

    [AvaloniaTest]
    public async Task AnUnreadableCommitProfileHasAVisibleErrorAndAnEditedChoiceCannotRepairIt()
    {
        using PortableHelperSettingsFixture f = new(); string path = new CommitPreferencesStore().FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{ broken Ω"); byte[] before = File.ReadAllBytes(path);
        (DesktopSettingsDialog settings, Task<bool> closed) = f.Open("Commit");
        Assert.That(Field<TextBlock>(settings, "CommitDialogSettingsNotice").Text, Does.Contain("commit-preferences.json"));
        AdvancedSettingsUiDriver.Set(settings, "chkShowCommitAndPush", false); PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => settings.Navigation.IsEnabled && !string.IsNullOrEmpty(Field<TextBlock>(settings, "SettingsNotice").Text));
        Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo("Commit")); Assert.That(Field<TextBlock>(settings, "SettingsNotice").Text, Does.Contain("unreadable"));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before)); PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await closed;
    }

    internal static void Number(Window window, string text)
    {
        NumericUpDown number = Field<NumericUpDown>(window, "_NO_TRANSLATE_CommitDialogNumberOfPreviousMessages"); number.BringIntoView(); PortableHelperSettingsFixture.Pump(window);
        TextBox editor = number.GetVisualDescendants().OfType<TextBox>().Single(); PushProcessTestDriver.Pointer(editor);
        PortableHelperSettingsFixture.Press(window, PhysicalKey.A, RawInputModifiers.Control); PortableHelperSettingsFixture.Press(window, PhysicalKey.Backspace);
        window.KeyTextInput(text); PortableHelperSettingsFixture.Pump(window); PortableHelperSettingsFixture.Press(window, PhysicalKey.Tab);
    }
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
}

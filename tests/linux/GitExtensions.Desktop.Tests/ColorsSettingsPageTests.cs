using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ColorsSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task TheActualSettingsPageHasFiveTranslatedChoicesAndNormalKeyboardNavigation(string language, bool dark)
    {
        using PortableHelperSettingsFixture f = new(language, dark);
        (DesktopSettingsDialog settings, Task<bool> result) = f.Open("Colors");
        ColorsSettingsPage page = Field<ColorsSettingsPage>(settings, "ColorsSettingsPage");
        Assert.That(page.GetVisualDescendants().OfType<CheckBox>().Count(), Is.EqualTo(5));
        Assert.That(page.GetVisualDescendants().OfType<ComboBox>(), Is.Empty, "Theme belongs to Appearance.");
        Assert.That(page.GetVisualDescendants().OfType<Control>().Any(c => c.Name is "MulticolorBranches" or "chkColorblind" or "chkUseSystemVisualStyle"), Is.False);
        foreach (ColorsSetting field in ColorsSettingsCatalog.Fields)
        {
            CheckBox choice = Field<CheckBox>(settings, field.Control); choice.BringIntoView(); PortableHelperSettingsFixture.Pump(settings);
            TextBlock label = (TextBlock)choice.Content!;
            Assert.That(label.Text, Is.EqualTo(f.Original("ColorsSettingsPage", field.Control + ".Text")));
            Point origin = label.TranslatePoint(default, choice)!.Value;
            Assert.That(origin.X + label.Bounds.Width, Is.LessThanOrEqualTo(choice.Bounds.Width + .5));
            Assert.That(origin.Y + label.TextLayout.Height, Is.LessThanOrEqualTo(choice.Bounds.Height + .5));
            Assert.That(choice.IsThreeState, Is.False);
        }
        PortableHelperSettingsFixture.Capture(settings, "colors-" + language + (dark ? "-dark.png" : "-light.png"));
        CheckBox alternate = Field<CheckBox>(settings, "chkDrawAlternateBackColor"); PushProcessTestDriver.Pointer(alternate);
        Assert.That(page.Draft.DrawAlternateBackground, Is.False); Assert.That(alternate.IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.Space); Assert.That(page.Draft.DrawAlternateBackground, Is.True);
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.Tab); Assert.That(Field<CheckBox>(settings, "DrawNonRelativesGray").IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.Enter); Assert.That(settings.IsVisible && !result.IsCompleted, Is.True);
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await result, Is.False);
        Assert.That(File.Exists(new HistoryViewPreferencesStore().FilePath), Is.False);
    }

    [AvaloniaTest]
    public async Task ApplyReopenAndCancelRetainOnlyTheAcceptedIntentions()
    {
        using PortableHelperSettingsFixture f = new();
        (DesktopSettingsDialog settings, Task<bool> result) = f.Open("Colors");
        PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "chkFillRefLabels"));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => f.Publications == 1 && settings.Navigation.IsEnabled);
        Assert.That(new HistoryViewPreferencesStore().Load().FillRefLabels, Is.True); Assert.That(settings.IsVisible, Is.True);
        byte[] accepted = File.ReadAllBytes(new HistoryViewPreferencesStore().FilePath);
        PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "chkFillRefLabels"));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await result, Is.True);
        Assert.That(File.ReadAllBytes(new HistoryViewPreferencesStore().FilePath), Is.EqualTo(accepted));
        (DesktopSettingsDialog reopened, Task<bool> reopenedResult) = f.Open("Colors");
        Assert.That(Field<CheckBox>(reopened, "chkFillRefLabels").IsChecked, Is.True);
        Assert.That(Field<CheckBox>(reopened, "DrawNonRelativesTextGray").IsChecked, Is.False);
        PushProcessTestDriver.Pointer(Field<Button>(reopened, "CancelDesktopSettings")); await reopenedResult;
        Assert.That(File.ReadAllBytes(new HistoryViewPreferencesStore().FilePath), Is.EqualTo(accepted));
    }

    [AvaloniaTest]
    public async Task AProfileReadFailureLeavesTheActualColorsDraftForAnExplicitRetry()
    {
        using PortableHelperSettingsFixture f = new();
        (DesktopSettingsDialog settings, Task<bool> result) = f.Open("Colors");
        HistoryViewPreferencesStore store = new(); Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{broken-view");
        PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "chkFillRefLabels"));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => f.Publications == 1 && settings.Navigation.IsEnabled);
        Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo("Colors"));
        Assert.That(Field<TextBlock>(settings, "SettingsNotice").Text, Does.Contain("Unable to save"));
        Assert.That(Field<CheckBox>(settings, "chkFillRefLabels").IsChecked, Is.True); Assert.That(File.ReadAllText(store.FilePath), Is.EqualTo("{broken-view"));
        File.WriteAllText(store.FilePath, "{}"); PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => f.Publications == 2 && settings.Navigation.IsEnabled);
        Assert.That(store.Load().FillRefLabels, Is.True); Assert.That(Field<TextBlock>(settings, "SettingsNotice").Text, Is.Null.Or.Empty);
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await result, Is.True);
    }

    private static T Field<T>(TopLevel owner, string name) where T : Control => PushProcessTestDriver.Field<T>(owner, name);
}

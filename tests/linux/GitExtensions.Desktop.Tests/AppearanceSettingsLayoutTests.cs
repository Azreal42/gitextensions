using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AppearanceSettingsLayoutTests
{
    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("fr-FR", true)]
    public async Task LongCustomTemplateStaysInItsGroupAndAllHelpActionsRemainReachableAtTheNormalSettingsWidth(string culture, bool dark)
    {
        using AvatarSettingsTestProfile profile = new();
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        byte[] desktopBefore = File.ReadAllBytes(profile.Desktop.FilePath), historyBefore = File.ReadAllBytes(profile.History.FilePath);
        List<(Window Owner, string Url)> avatarHelp = []; List<Window> translationHelp = []; int clears = 0;
        AppearanceSettingsValues initial = new() { AvatarProvider = AvatarProvider.Custom, Language = "English" };
        DesktopSettingsDialog window = new(profile.Desktop.Load(), TranslationCatalog.LoadDefault(), false,
            fileHistoryPreferences: new(), fonts: new(), initialPage: "Appearance", appearanceSettings: initial,
            helpTranslateAsync: owner => { translationHelp.Add(owner); return Task.CompletedTask; },
            clearAvatarCacheAsync: (_, _) => { clears++; return Task.CompletedTask; },
            avatarHelpAsync: (owner, url) => { avatarHelp.Add((owner, url)); return Task.CompletedTask; })
            { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        try
        {
            using (await AvatarUiTestDriver.Frame(window)) { }
            Assert.That(window.ClientSize.Width, Is.EqualTo(958), "Use the real Settings width, including its navigation sidebar.");
            AppearanceSettingsPage page = Field<AppearanceSettingsPage>(window, "AppearanceSettingsPage");
            TextBox template = Field<TextBox>(window, "txtCustomAvatarTemplate");
            string[] controls = ["SettingsTheme", "AvatarProvider", "_NO_TRANSLATE_NoImageService", "txtCustomAvatarTemplate", "ClearImageCache", "Language", "avatarProviderHelp", "pictureAvatarHelp", "helpTranslate"];
            Dictionary<string, Rect> originalBounds = controls.ToDictionary(name => name, name => BoundsIn(Field<Control>(window, name), window));
            string localUrl = "http://127.0.0.1:38593/avatar/" + new string('a', 52) + ".png";
            Assert.That(localUrl.Length, Is.EqualTo(86));
            string[] templates = [localUrl, localUrl + "?literal=" + new string('x', 1024) + "&email={email};<Default>"];
            foreach (string literal in templates)
            {
                await AvatarUiTestDriver.Edit(window, template, literal);
                using (await AvatarUiTestDriver.Frame(window)) { }
                Assert.That(template.Text, Is.EqualTo(literal)); Assert.That(page.Draft.CustomAvatarTemplate, Is.EqualTo(literal));
                foreach (string name in controls)
                {
                    Control control = Field<Control>(window, name);
                    AssertReachable(window, control);
                    Rect bounds = BoundsIn(control, window);
                    Assert.That(bounds.X, Is.EqualTo(originalBounds[name].X).Within(0.5), name + " must not move when the template grows.");
                    Assert.That(bounds.Width, Is.EqualTo(originalBounds[name].Width).Within(0.5), name + " must keep its allotted viewport width.");
                }
                foreach (string name in new[] { "AvatarProvider", "_NO_TRANSLATE_NoImageService", "txtCustomAvatarTemplate", "avatarProviderHelp", "pictureAvatarHelp", "ClearImageCache" })
                { AssertInside(Field<Control>(window, name), Field<Border>(window, "gbAuthorImagesFrame")); }
                AssertInside(Field<HyperlinkButton>(window, "helpTranslate"), Field<Border>(window, "gbLanguagesFrame"));
                Assert.That(BoundsIn(Field<Grid>(window, "gbGeneral"), page).Y, Is.LessThan(BoundsIn(Field<Grid>(window, "gbAuthorImages"), page).Y));
                Assert.That(BoundsIn(Field<Grid>(window, "gbAuthorImages"), page).Y, Is.LessThan(BoundsIn(Field<Grid>(window, "gbLanguages"), page).Y));
            }
            await AvatarUiTestDriver.Click(window, Field<Button>(window, "avatarProviderHelp"));
            await AvatarUiTestDriver.Click(window, Field<Button>(window, "pictureAvatarHelp"));
            await AvatarUiTestDriver.Click(window, Field<HyperlinkButton>(window, "helpTranslate"));
            await AvatarUiTestDriver.Click(window, Field<Button>(window, "ClearImageCache"));
            Assert.That(avatarHelp.Select(call => call.Owner), Is.All.SameAs(window));
            Assert.That(avatarHelp.Select(call => call.Url), Is.EqualTo(new[] { AppearanceSettingsPage.AvatarProviderHelpUrl, AppearanceSettingsPage.AvatarFallbackHelpUrl }));
            Assert.That(translationHelp, Is.EqualTo(new[] { window })); Assert.That(clears, Is.EqualTo(1));
            Assert.That(page.Draft.CustomAvatarTemplate, Is.EqualTo(templates[^1])); Assert.That(template.Text, Is.EqualTo(templates[^1]));
            using (var frame = await AvatarUiTestDriver.Frame(window))
            {
                string folder = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
                Directory.CreateDirectory(folder); string path = Path.Combine(folder, "appearance-long-template-" + culture + (dark ? "-dark.png" : "-light.png"));
                frame.Save(path); TestContext.AddTestAttachment(path);
            }
            await AvatarUiTestDriver.Click(window, Field<Button>(window, "CancelDesktopSettings"));
            Assert.That(window.IsVisible, Is.False);
            Assert.That(File.ReadAllBytes(profile.Desktop.FilePath), Is.EqualTo(desktopBefore));
            Assert.That(File.ReadAllBytes(profile.History.FilePath), Is.EqualTo(historyBefore));
        }
        finally { if (window.IsVisible) { window.Close(); } }
    }

    private static T Field<T>(TopLevel window, string name) where T : Control => AvatarUiTestDriver.Field<T>(window, name);
    private static Rect BoundsIn(Control control, Visual ancestor)
        => new(control.TranslatePoint(default, ancestor)!.Value, control.Bounds.Size);
    private static void AssertReachable(Window window, Control control)
    {
        Rect bounds = BoundsIn(control, window);
        Assert.That(control.IsEffectivelyVisible && control.IsEffectivelyEnabled, Is.True, control.Name);
        Assert.That(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Top >= 0
            && bounds.Right <= window.ClientSize.Width && bounds.Bottom <= window.ClientSize.Height, Is.True, control.Name + " must remain fully inside the viewport without scrolling.");
        Assert.That((window.InputHitTest(bounds.Center) as Visual)?.GetSelfAndVisualAncestors().Contains(control), Is.True, control.Name + " must remain the real pointer target.");
    }
    private static void AssertInside(Control control, Control group)
    {
        Rect bounds = BoundsIn(control, group);
        Assert.That(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= group.Bounds.Width && bounds.Bottom <= group.Bounds.Height,
            Is.True, control.Name + " must remain inside its original Windows group.");
    }
}

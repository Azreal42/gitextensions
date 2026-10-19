using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ContributorsDialogTests
{
    [Test]
    public void LinkedOriginalResourceSuppliesAllFourExactListsWithoutAnIndependentCopy()
    {
        using Stream stream = typeof(ContributorsDialog).Assembly.GetManifestResourceStream(ContributorsCatalog.ResourceName)!;
        Assert.That(stream, Is.Not.Null, "Root must link the original Resources.resx as raw XML.");
        XDocument original = XDocument.Load(stream);
        string Expected(string key) => Regex.Replace(original.Root!.Elements("data").Single(item => (string?)item.Attribute("name") == key).Element("value")!.Value, "\\r\\n?|\\n", " ");
        ContributorsData data = ContributorsCatalog.Load();
        Assert.Multiple(() =>
        {
            Assert.That(data.Team, Is.EqualTo(Expected("Team")));
            Assert.That(data.Coders, Is.EqualTo(Expected("Coders")));
            Assert.That(data.Translators, Is.EqualTo(Expected("Translators")));
            Assert.That(data.Designers, Is.EqualTo(Expected("Designers")));
        });
    }

    [TestCase("<root><data name='Team'><value>one</value></data></root>")]
    [TestCase("<root><data name='Team'><value>one</value></data><data name='Team'><value>two</value></data></root>")]
    public void MissingOrDuplicateListsRemainVisibleErrorsRatherThanInventedContributors(string xml)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(xml));
        Assert.Throws<InvalidDataException>(() => ContributorsCatalog.Read(stream));
    }

    [Test]
    public void ContributorReaderRejectsExternalEntities()
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes("<!DOCTYPE root [<!ENTITY names SYSTEM 'file:///etc/passwd'>]><root/>"));
        Assert.Throws<XmlException>(() => ContributorsCatalog.Read(stream));
    }

    [AvaloniaTest]
    [TestCase("en-US", false)]
    [TestCase("fr-FR", true)]
    public async Task ThreeOriginalTabsAreSelectableReadOnlyCompactAndEscapeClosesTheOwnedWindow(string language, bool dark)
    {
        TranslationCatalog translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
        Window owner = new() { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        ContributorsDialog dialog = new(translations) { RequestedThemeVariant = owner.RequestedThemeVariant };
        try
        {
            owner.Show(); Task completion = dialog.ShowDialog(owner); Pump(dialog);
            TabControl tabs = Field<TabControl>(dialog, "ContributorsTabs");
            Assert.That(tabs.Items.Count, Is.EqualTo(3)); Assert.That(dialog.Width, Is.EqualTo(624)); Assert.That(dialog.Height, Is.EqualTo(442)); Assert.That(dialog.CanResize, Is.False);
            ContributorsData data = ContributorsCatalog.Load();
            string[] expected = [data.Coders, data.Translators, data.Designers];
            string[] names = ["Developers", "Translators", "Designers"];
            for (int index = 0; index < 3; index++)
            {
                tabs.SelectedIndex = index; Pump(dialog);
                TextBox box = Field<TextBox>(dialog, "Contributors" + names[index]); string before = box.Text!;
                Assert.That(box.IsReadOnly, Is.True); Assert.That(before, Does.Contain(expected[index]));
                box.Focus(); box.SelectionStart = 0; box.SelectionEnd = Math.Min(12, before.Length);
                Assert.That(box.SelectedText, Is.Not.Empty);
                dialog.KeyTextInput("must not alter credits"); Pump(dialog); Assert.That(box.Text, Is.EqualTo(before));
                Capture(dialog, $"contributors-{language}-{(dark ? "Dark" : "Light")}-{names[index]}.png");
            }
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); Pump(owner);
            await completion; Assert.That(dialog.IsVisible, Is.False); Assert.That(owner.IsVisible, Is.True);
        }
        finally { if (dialog.IsVisible) { dialog.Close(); } owner.Close(); }
    }
    [AvaloniaTest]
    public async Task AboutMenuOpensTheOriginalCreditsAsAnOwnedModalAndReturnsToAbout()
    {
        using MainWindowClassicTests.RepositoryFixture fixture = await MainWindowClassicTests.RepositoryFixture.CreateAsync("base\n", "base\n");
        MenuItem help = fixture.Window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Name == "HelpMenu");
        help.IsSubMenuOpen = true; Pump(fixture.Window);
        PushProcessTestDriver.Pointer(help.Items.OfType<MenuItem>().Single(item => item.Name == "aboutToolStripMenuItem"));
        await DetailedSettingsUiHost.Until(() => fixture.Window.OwnedWindows.OfType<DesktopInformationWindow>().Any());
        DesktopInformationWindow about = fixture.Window.OwnedWindows.OfType<DesktopInformationWindow>().Single(); Pump(about);
        PushProcessTestDriver.Pointer(Field<Button>(about, "ShowContributors"));
        await DetailedSettingsUiHost.Until(() => about.OwnedWindows.OfType<ContributorsDialog>().Any());
        ContributorsDialog credits = about.OwnedWindows.OfType<ContributorsDialog>().Single(); Pump(credits);
        Assert.That(credits.Owner, Is.SameAs(about)); Assert.That(credits.ShowInTaskbar, Is.False);
        Assert.That(Field<TabControl>(credits, "ContributorsTabs").Items.Count, Is.EqualTo(3));
        credits.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await DetailedSettingsUiHost.Until(() => !credits.IsVisible); Assert.That(about.IsVisible, Is.True);
        about.Close(); await DetailedSettingsUiHost.Until(() => !about.IsVisible);
    }

    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(TopLevel window, string name)
    {
        Pump(window); using var image = window.CaptureRenderedFrame(); Assert.That(image, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); image!.Save(path); TestContext.AddTestAttachment(path);
    }
}

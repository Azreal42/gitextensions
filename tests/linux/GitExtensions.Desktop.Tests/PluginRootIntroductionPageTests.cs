using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class PluginRootIntroductionPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("fr", true)]
    public async Task RealSettingsContainsTheOriginalTranslatedPluginIntroductionAsItsLastRoot(string language, bool dark)
    {
        using PortableHelperSettingsFixture f = new(language, dark); byte[] before = File.ReadAllBytes(f.Scripts.FilePath);
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("General");
        TreeView tree = PortableHelperSettingsFixture.Field<TreeView>(settings, "SettingsNavigationTree");
        TreeViewItem plugins = tree.Items.OfType<TreeViewItem>().Last();
        Assert.That(plugins.Name, Is.EqualTo("SettingsPluginsNode"));
        Assert.That(plugins.Items.Cast<TreeViewItem>().Select(node => ((SettingsNavigationPage)node.Tag!).Id),
            Is.EqualTo(new[] { "PluginDeleteUnusedBranches", "PluginBackgroundFetch", "PluginAutoCompileSubmodules",
                "PluginFindLargeFiles", "PluginStatistics", "PluginGource", "PluginsProxy" }));
        Image icon = ((Control)plugins.Header!).GetVisualDescendants().OfType<Image>().Single();
        Assert.That(icon.Tag, Is.EqualTo("plugin.png")); Assert.That(icon.Source, Is.Not.Null);
        Assert.That(((Control)plugins.Header!).GetVisualDescendants().OfType<TextBlock>().Single().Text,
            Is.EqualTo(f.Original("PluginsSettingsGroup", "$this.Title")));
        SettingsNavigationTestDriver.Select(settings, "Plugins");
        SettingsNavigationPage selected = settings.Navigation.SelectedPage;
        Assert.That(selected.Title, Is.EqualTo(f.Original("PluginRootIntroductionPage", "$this.Text")));
        Assert.That(settings.Title, Is.EqualTo(f.Translations.Translate("Settings") + " - " + selected.Title));
        PluginRootIntroductionPage page = PortableHelperSettingsFixture.Field<PluginRootIntroductionPage>(settings, "PluginRootIntroductionPage");
        Assert.That(PortableHelperSettingsFixture.Field<ContentControl>(settings, "SettingsPageContent").Content, Is.SameAs(page));
        TextBlock label = page.GetVisualDescendants().OfType<TextBlock>().Single();
        Assert.That(label.Name, Is.EqualTo("label1")); Assert.That(label.Text, Is.EqualTo(f.Original("PluginRootIntroductionPage", "label1.Text")));
        Assert.That(label.TextWrapping, Is.EqualTo(TextWrapping.Wrap)); Assert.That(label.Bounds.Size, Is.EqualTo(new Size(284, 132)));
        Assert.That(label.TranslatePoint(default, page), Is.EqualTo(new Point(12, 13)));
        Assert.That(label.TextLayout.Height, Is.LessThanOrEqualTo(label.Bounds.Height));
        Assert.That(label.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(label.Bounds.Width + .5));
        Assert.That(page.GetVisualDescendants().OfType<Button>(), Is.Empty); Assert.That(page.GetVisualDescendants().OfType<TextBox>(), Is.Empty);
        Assert.That(PortableHelperSettingsFixture.Field<TextBlock>(settings, "SettingsInstantSaveNotice").IsVisible, Is.False);
        Assert.That(tree.IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Capture(settings, "plugins-introduction-" + language + (dark ? "-dark.png" : "-light.png"));
        PushProcessTestDriver.Pointer(PortableHelperSettingsFixture.Field<Button>(settings, "CancelDesktopSettings"));
        Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.False); Assert.That(f.ScriptSaves + f.Publications, Is.Zero);
        Assert.That(File.ReadAllBytes(f.Scripts.FilePath), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("fr", true)]
    public async Task SearchingTheIntroductionAndEnterSelectsTheRootWithoutAcceptingSettings(string language, bool dark)
    {
        using PortableHelperSettingsFixture f = new(language, dark); (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("General");
        f.Edit(settings, "SettingsFind", f.Original("PluginRootIntroductionPage", "label1.Text"));
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.Enter);
        Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo("Plugins")); Assert.That(settings.IsVisible, Is.True);
        Assert.That(completion.IsCompleted, Is.False); Assert.That(f.Publications + f.ScriptSaves, Is.Zero);
        Assert.That(PortableHelperSettingsFixture.Field<TextBox>(settings, "SettingsFind").IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.Tab);
        Assert.That(PortableHelperSettingsFixture.Field<TreeView>(settings, "SettingsNavigationTree").IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.ArrowUp);
        Assert.That(settings.Navigation.SelectedPage.Id, Is.Not.EqualTo("Plugins")); Assert.That(f.Publications, Is.Zero);
        PushProcessTestDriver.Pointer(PortableHelperSettingsFixture.Field<Button>(settings, "CancelDesktopSettings"));
        Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.False);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task VisitingTheIntroductionKeepsTheScriptDraftAndOnlyExplicitApplyPublishesIt(bool apply)
    {
        using PortableHelperSettingsFixture f = new(); byte[] before = File.ReadAllBytes(f.Scripts.FilePath);
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Scripts");
        ScriptsSettingsPage scripts = PortableHelperSettingsFixture.Field<ScriptsSettingsPage>(settings, "ScriptsSettingsPage");
        f.Edit(settings, "ScriptName", "preserved draft"); SettingsNavigationTestDriver.Select(settings, "Plugins");
        SettingsNavigationTestDriver.Select(settings, "Scripts");
        Assert.That(PortableHelperSettingsFixture.Field<ScriptsSettingsPage>(settings, "ScriptsSettingsPage"), Is.SameAs(scripts));
        Assert.That(scripts.Draft.Single().Name, Is.EqualTo("preserved draft")); Assert.That(f.ScriptSaves, Is.Zero);
        SettingsNavigationTestDriver.Select(settings, "Plugins");
        if (apply)
        {
            PushProcessTestDriver.Pointer(PortableHelperSettingsFixture.Field<Button>(settings, "ApplyDesktopSettings"));
            await PushProcessTestDriver.UntilAsync(settings, () => f.Publications == 1 && PortableHelperSettingsFixture.Field<Button>(settings, "ApplyDesktopSettings").IsEnabled);
            Assert.That(settings.IsVisible, Is.True); Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo("Plugins"));
            Assert.That(f.Scripts.Load().Scripts.Single().Name, Is.EqualTo("preserved draft")); Assert.That(f.ScriptSaves, Is.EqualTo(1));
        }
        PushProcessTestDriver.Pointer(PortableHelperSettingsFixture.Field<Button>(settings, "CancelDesktopSettings"));
        Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(apply));
        if (!apply) { Assert.That(File.ReadAllBytes(f.Scripts.FilePath), Is.EqualTo(before)); Assert.That(f.ScriptSaves + f.Publications, Is.Zero); }
    }
}

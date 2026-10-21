using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ProxySwitcherSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task FourOriginalRowsUseRealCaptionsAndMaskPasswordWhileApplyStoresItsExplicitXmlValue(string language, bool dark)
    {
        CultureInfo culture = CultureInfo.CurrentCulture, ui = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            await using SimplePluginSettingsUiFixture f = await SimplePluginSettingsUiFixture.Create(language, dark);
            SettingsNavigationTestDriver.Select(f.Dialog, ProxySwitcherSettingsCatalog.PageId); f.Click("ProxyGlobalRB");
            Grid table = f.Field<Grid>("ProxySettingsTable"); Assert.That(table.RowDefinitions, Has.Count.EqualTo(4)); Assert.That(table.ColumnDefinitions, Has.Count.EqualTo(3));
            foreach (PluginScopedSetting field in ProxySwitcherSettingsCatalog.Definition.Fields)
            {
                TextBlock caption = f.Field<TextBlock>(field.Control + "Caption");
                Assert.That(caption.Text, Is.EqualTo(f.Translations.Translate("ProxySwitcherPlugin", field.Control, "Caption", field.Caption)));
                Assert.That(caption.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False);
                Assert.That(caption.TextLayout.Width, Is.LessThanOrEqualTo(caption.Bounds.Width + .5));
                Assert.That(new Rect(default, f.Dialog.ClientSize).Contains(new Rect(caption.TranslatePoint(default, f.Dialog)!.Value, caption.Bounds.Size)), Is.True);
            }
            f.Edit("Username", "alice"); f.Edit("Password", "xml-explicit-secret"); f.Edit("HttpProxy", "proxy.invalid"); f.Edit("HttpProxyPort", "65536");
            TextBox password = f.Field<TextBox>("Password"); Assert.That(password.PasswordChar, Is.EqualTo('●'));
            Assert.That(f.Dialog.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("xml-explicit-secret", StringComparison.Ordinal) == true), Is.False);
            f.Capture("proxy-settings-" + language + (dark ? "-dark.png" : "-light.png"));
            f.Click("ApplyDesktopSettings"); await f.Until(() => !f.Dialog.ProxySwitcherSettingsSession.HasChanges && f.Field<Button>("ApplyDesktopSettings").IsEnabled);
            Assert.That(f.Field<TextBlock>("SettingsNotice").Text, Is.Null.Or.Empty);
            GitXmlSettingsSnapshot xml = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global);
            Assert.That(xml.Values[ProxySwitcherSettingsCatalog.Definition.Key(ProxySwitcherSettingsCatalog.Definition.Find("Password"))], Is.EqualTo("xml-explicit-secret"));
            Assert.That(xml.Values[ProxySwitcherSettingsCatalog.Definition.Key(ProxySwitcherSettingsCatalog.Definition.Find("HttpProxyPort"))], Is.EqualTo("65536"));
            f.Edit("Password", "discarded-draft"); f.Click("CancelDesktopSettings"); await f.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.That(password.Text, Is.Null.Or.Empty); await f.Open(dark); SettingsNavigationTestDriver.Select(f.Dialog, ProxySwitcherSettingsCatalog.PageId); f.Click("ProxyGlobalRB");
            Assert.That(f.Field<TextBox>("Password").Text, Is.EqualTo("xml-explicit-secret")); Assert.That(f.Field<TextBox>("Password").PasswordChar, Is.EqualTo('●'));
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = ui; }
    }

    [AvaloniaTest]
    public async Task ScopeSwitchFlushesOnlyDraftsAndCancelCannotCreateAProfileOrImportALegacyPassword()
    {
        await using SimplePluginSettingsUiFixture f = await SimplePluginSettingsUiFixture.Create(); Dictionary<string, byte[]> before = f.Profile.Material();
        SettingsNavigationTestDriver.Select(f.Dialog, ProxySwitcherSettingsCatalog.PageId); f.Click("ProxyLocalRB");
        f.Edit("Password", "local-draft"); f.Click("ProxyGlobalRB"); f.Edit("Username", "global-draft");
        f.Click("ProxyLocalRB"); Assert.That(f.Field<TextBox>("Password").Text, Is.EqualTo("local-draft"));
        f.Click("CancelDesktopSettings"); await f.Completion.WaitAsync(TimeSpan.FromSeconds(15)); Assert.That(f.Profile.Material(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task PasswordFirstClickAndNativeTabEnterDoNotImplicitlySaveOrSetGit()
    {
        await using SimplePluginSettingsUiFixture f = await SimplePluginSettingsUiFixture.Create(); Dictionary<string, byte[]> before = f.Profile.Material();
        SettingsNavigationTestDriver.Select(f.Dialog, ProxySwitcherSettingsCatalog.PageId); f.Click("ProxyGlobalRB");
        f.Click("Username"); f.Press(PhysicalKey.Tab); Assert.That(f.Field<TextBox>("Password").IsFocused, Is.True);
        f.Edit("Password", "pending"); f.Press(PhysicalKey.Enter); Assert.That(f.Dialog.IsVisible, Is.True);
        Assert.That(f.Profile.Material(), Is.EqualTo(before)); Assert.That(f.Dialog.OwnedWindows, Is.Empty);
        f.Press(PhysicalKey.Tab); Assert.That(f.Field<TextBox>("HttpProxy").IsFocused, Is.True);
    }
}

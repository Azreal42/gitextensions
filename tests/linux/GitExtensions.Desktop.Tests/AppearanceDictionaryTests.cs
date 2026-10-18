using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Spelling;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AppearanceDictionaryTests
{
    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("en", true)]
    [TestCase("fr-FR", false)]
    [TestCase("fr-FR", true)]
    public async Task TheRealInstalledCatalogueAndExplicitNoneAreReachableWithoutPublishingAProfile(string culture, bool dark)
    {
        using AvatarSettingsTestProfile profile = new();
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        Dictionary<string, byte[]> before = Profiles(profile);
        await using PageHost host = new(new() { Language = "English", Theme = dark ? "Dark" : "Light" }, dark: dark);
        ComboBox dictionary = host.Field<ComboBox>("Dictionary");
        string[] installed = DictionaryCatalog.FromApplicationDirectory(AppContext.BaseDirectory).GetInstalled().Select(item => item.Name).ToArray();
        Assert.That(installed, Is.EquivalentTo(new[] { "de-DE", "en-AU", "en-CA", "en-GB", "en-US", "es-ES", "es-MX", "fr-FR", "it-IT", "nl-NL", "pl-PL", "ro-RO", "ru-RU" }));
        Assert.That(dictionary.Items.OfType<string>(), Is.EqualTo(new[] { DictionaryCatalog.NoneName }.Concat(installed)));
        Assert.That(dictionary.SelectedItem, Is.EqualTo("en-US"));
        await AvatarUiTestDriver.Choose(host.Window, dictionary, "fr-FR");
        Assert.That(host.Page.Draft.Dictionary, Is.EqualTo("fr-FR"));
        await AvatarUiTestDriver.Choose(host.Window, dictionary, DictionaryCatalog.NoneName);
        Assert.That(host.Page.Draft.Dictionary, Is.EqualTo("none"));
        Assert.That(host.Page.Validate(out _), Is.True);
        using (WriteableBitmap frame = await AvatarUiTestDriver.Frame(host.Window))
        {
            string png = Path.Combine(TestContext.CurrentContext.WorkDirectory, "dictionary-" + culture + (dark ? "-dark.png" : "-light.png"));
            frame.Save(png); TestContext.AddTestAttachment(png);
        }
        SameProfiles(profile, before);
    }

    [AvaloniaTest]
    [TestCase("not-installed")]
    [TestCase("../invalid")]
    [TestCase(" en-US ")]
    [TestCase("en-us")]
    public async Task AStoredUnknownIdentityRemainsVisibleAndOnlyAnExplicitNoneChoiceReplacesIt(string identity)
    {
        using AvatarSettingsTestProfile profile = new(); Dictionary<string, byte[]> before = Profiles(profile);
        await using PageHost host = new(new() { Language = "English", Dictionary = identity });
        Assert.That(host.Field<ComboBox>("Dictionary").SelectedItem, Is.Null);
        Assert.That(host.Field<TextBlock>("DictionaryNotice").IsVisible, Is.True);
        Assert.That(host.Field<TextBlock>("DictionaryNotice").Text, Does.Contain(identity));
        Assert.That(host.Page.Draft.Dictionary, Is.EqualTo(identity));
        Assert.That(host.Page.Validate(out _), Is.True, "An unchanged unavailable identity must not block unrelated settings.");
        await AvatarUiTestDriver.Choose(host.Window, host.Field<ComboBox>("Dictionary"), "none");
        Assert.That(host.Page.Draft.Dictionary, Is.EqualTo("none"));
        Assert.That(host.Field<TextBlock>("DictionaryNotice").IsVisible, Is.False); SameProfiles(profile, before);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingOrAmbiguousCatalogueIsReportedWithoutWritingOrSilentlyChangingTheIdentity(bool ambiguous)
    {
        using AvatarSettingsTestProfile profile = new(); string directory = profile.Output("catalogue");
        if (ambiguous)
        {
            Directory.CreateDirectory(directory);
            File.Copy(DictionaryCatalog.FromApplicationDirectory(AppContext.BaseDirectory).Resolve("en-US").FilePath, Path.Combine(directory, "None.dic"));
        }
        Dictionary<string, byte[]> before = Profiles(profile);
        await using PageHost host = new(new() { Language = "English", Dictionary = "en-US" }, catalog: new(directory));
        Assert.That(host.Field<TextBlock>("DictionaryNotice").IsVisible, Is.True);
        Assert.That(host.Field<TextBlock>("DictionaryNotice").Text, Is.Not.Null.And.Not.Empty);
        Assert.That(host.Page.Draft.Dictionary, Is.EqualTo("en-US"));
        Assert.That(host.Page.Validate(out _), Is.True); SameProfiles(profile, before);
        Assert.That(Directory.Exists(directory), Is.EqualTo(ambiguous));
        // Parsing a malformed .dic body belongs to SpellCheckService.Ready/Check,
        // not this catalogue-only page. DictionaryServiceTests exercises that failure.
    }

    [AvaloniaTest]
    [TestCase("../invalid")]
    [TestCase(" en-US ")]
    public async Task SavingAnotherFieldPreservesTheUnavailableDictionaryAndPublishesItsExactIdentity(string identity)
    {
        using AvatarSettingsTestProfile profile = new(); profile.Desktop.Save(profile.Desktop.Load() with { Dictionary = identity });
        await using SettingsHost host = new(profile);
        Assert.That(host.Field<TextBlock>("DictionaryNotice").IsVisible, Is.True);
        await AvatarUiTestDriver.Choose(host.Dialog, host.Field<ComboBox>("SettingsTheme"), "Dark"); await host.Apply();
        Assert.That(profile.Desktop.Load().Theme, Is.EqualTo("Dark"));
        Assert.That(profile.Desktop.Load().Dictionary, Is.EqualTo(identity));
        Assert.That(host.SavedDictionaries, Is.EqualTo(new[] { identity }));
        Assert.That(host.Page.Draft.Dictionary, Is.EqualTo(identity));
        Assert.That(host.Field<TextBlock>("DictionaryNotice").IsVisible, Is.True);
    }

    [AvaloniaTest]
    public async Task ApplyPublishesNoneWhileCancelDiscardsTheFollowingDraftAndAReopenedPageReadsTheSavedChoice()
    {
        using AvatarSettingsTestProfile profile = new();
        await using (SettingsHost host = new(profile))
        {
            await AvatarUiTestDriver.Choose(host.Dialog, host.Field<ComboBox>("Dictionary"), "none"); await host.Apply();
            Assert.That(host.Dialog.IsVisible, Is.True); Assert.That(host.Result.IsCompleted, Is.False);
            Assert.That(profile.Desktop.Load().Dictionary, Is.EqualTo("none"));
            Dictionary<string, byte[]> applied = Profiles(profile);
            await AvatarUiTestDriver.Choose(host.Dialog, host.Field<ComboBox>("Dictionary"), "fr-FR");
            await AvatarUiTestDriver.Click(host.Dialog, host.Field<Button>("CancelDesktopSettings"));
            Assert.That(await host.Result.WaitAsync(TimeSpan.FromSeconds(10)), Is.True); SameProfiles(profile, applied);
            Assert.That(host.SavedDictionaries, Is.EqualTo(new[] { "none" }));
        }
        await using SettingsHost reopened = new(profile);
        Assert.That(reopened.Page.Draft.Dictionary, Is.EqualTo("none"));
        Assert.That(reopened.Field<ComboBox>("Dictionary").SelectedItem, Is.EqualTo("none"));
    }

    [AvaloniaTest]
    public async Task TheDownloadActionUsesTheWindowsSpellingHelpUrlAndTheActualVisibleOwner()
    {
        using AvatarSettingsTestProfile profile = new(); Dictionary<string, byte[]> before = Profiles(profile);
        List<(Window Owner, string Url)> requests = [];
        await using PageHost host = new(new() { Language = "English" }, help: (owner, url) =>
        { requests.Add((owner, url)); return Task.CompletedTask; });
        HyperlinkButton link = host.Field<HyperlinkButton>("downloadDictionary");
        Assert.That(link.IsEffectivelyEnabled, Is.True);
        await AvatarUiTestDriver.Click(host.Window, link);
        Assert.That(requests, Has.Count.EqualTo(1)); Assert.That(requests[0].Owner, Is.SameAs(host.Window));
        Assert.That(requests[0].Url, Is.EqualTo("https://github.com/gitextensions/gitextensions/wiki/Spelling"));
        Assert.That(ToolTip.GetTip(link), Is.EqualTo(requests[0].Url)); SameProfiles(profile, before);
    }

    [AvaloniaTest]
    public async Task ARealPartialDesktopPublicationAcknowledgesDictionaryBeforeHistoryFailureAndRetryPreservesANewerPeer()
    {
        using AvatarSettingsTestProfile profile = new(); await using SettingsHost host = new(profile);
        byte[] history = File.ReadAllBytes(profile.History.FilePath);
        await AvatarUiTestDriver.Choose(host.Dialog, host.Field<ComboBox>("Dictionary"), "fr-FR");
        await AvatarUiTestDriver.Click(host.Dialog, host.Field<CheckBox>("chkShowRelativeDate"));
        File.WriteAllText(profile.History.FilePath, "{broken-dictionary-history-profile");
        try
        {
            await host.Apply(expectFailure: true);
            Assert.That(host.Dialog.IsVisible, Is.True); Assert.That(host.Result.IsCompleted, Is.False);
            Assert.That(profile.Desktop.Load().Dictionary, Is.EqualTo("fr-FR"));
            Assert.That(host.SavedDictionaries, Is.EqualTo(new[] { "fr-FR" }));
            Assert.That(host.Page.Draft.Dictionary, Is.EqualTo("fr-FR"));
            Assert.That(File.ReadAllText(profile.History.FilePath), Is.EqualTo("{broken-dictionary-history-profile"));
            File.WriteAllBytes(profile.History.FilePath, history);
            profile.Desktop.Save(profile.Desktop.Load() with { Dictionary = "de-DE" });
            await host.Apply();
            Assert.That(profile.Desktop.Load().Dictionary, Is.EqualTo("de-DE"));
            Assert.That(host.Page.Draft.Dictionary, Is.EqualTo("de-DE"));
            Assert.That(host.SavedDictionaries, Is.EqualTo(new[] { "fr-FR", "de-DE" }));
            Assert.That(profile.History.Load().RelativeDate, Is.False);
        }
        finally { File.WriteAllBytes(profile.History.FilePath, history); }
    }

    private static Dictionary<string, byte[]> Profiles(AvatarSettingsTestProfile profile)
        => new[] { profile.Desktop.FilePath, profile.History.FilePath, profile.Clone.FilePath }.ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
    private static void SameProfiles(AvatarSettingsTestProfile profile, Dictionary<string, byte[]> before)
    {
        Dictionary<string, byte[]> after = Profiles(profile); Assert.That(after.Keys, Is.EquivalentTo(before.Keys));
        foreach ((string path, byte[] bytes) in before) { Assert.That(after[path], Is.EqualTo(bytes), path); }
    }

    private sealed class PageHost : IAsyncDisposable
    {
        internal AppearanceSettingsPage Page { get; }
        internal Window Window { get; }
        internal PageHost(AppearanceSettingsValues initial, bool dark = false, DictionaryCatalog? catalog = null, Func<Window, string, Task>? help = null)
        {
            Page = new(initial, TranslationCatalog.LoadDefault(), avatarHelpAsync: help, dictionaryCatalog: catalog);
            Window = new() { Content = new ScrollViewer { Content = Page, Margin = new Thickness(16) }, Width = 1000, Height = 900,
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            Window.Show(); AppearanceSettingsInput.Pump(Window);
        }
        internal T Field<T>(string name) where T : Control => AvatarUiTestDriver.Field<T>(Window, name);
        public async ValueTask DisposeAsync() => await AvatarUiTestDriver.Close(Window);
    }

    private sealed class SettingsHost : IAsyncDisposable
    {
        private readonly Window _owner = new();
        internal DesktopSettingsDialog Dialog { get; }
        internal Task<bool> Result { get; }
        internal HistoryViewPreferences Active { get; private set; }
        internal List<string> SavedDictionaries { get; } = [];
        internal int Attempts { get; private set; }
        internal AppearanceSettingsPage Page => Field<AppearanceSettingsPage>("AppearanceSettingsPage");
        internal SettingsHost(AvatarSettingsTestProfile profile)
        {
            DesktopPreferences initial = profile.Desktop.Load(); Active = profile.History.Load();
            AppearanceSettingsSession session = new(initial, Active); GeneralSettingsSession general = new(initial);
            Dialog = new(initial, TranslationCatalog.LoadDefault(), false, Active.Sort, fonts: new(), initialPage: "Appearance",
                generalSettings: general.Initial, appearanceSettings: session.Initial, publishSettings: dialog =>
                {
                    Attempts++; AppearanceSettingsValues draft = dialog.AppearanceSettingsResult;
                    GeneralSettingsPublication desktop = general.Save(dialog.GeneralSettingsResult, session.MergeDesktop(draft, dialog.Result), desktopSaved: saved =>
                    {
                        Assert.That(profile.Desktop.Load().Dictionary, Is.EqualTo(saved.Dictionary), "SavedDesktop follows the actual successful desktop write.");
                        SavedDictionaries.Add(saved.Dictionary); session.DesktopSaved(draft);
                    });
                    dialog.AcceptGeneralSaved(desktop);
                    AppearanceSettingsPublication appearance = session.Save(draft, desktop.Desktop, Active, dialog.HistorySortResult);
                    dialog.AcceptAppearanceSaved(appearance); Active = appearance.History; return Task.CompletedTask;
                });
            _owner.Show(); Result = Dialog.ShowDialog<bool>(_owner); AppearanceSettingsInput.Pump(Dialog);
        }
        internal T Field<T>(string name) where T : Control => AvatarUiTestDriver.Field<T>(Dialog, name);
        internal async Task Apply(bool expectFailure = false)
        {
            int previous = Attempts;
            await AvatarUiTestDriver.Click(Dialog, Field<Button>("ApplyDesktopSettings"));
            await PushProcessTestDriver.UntilAsync(Dialog, () => Attempts == previous + 1 && Dialog.Navigation.IsEnabled);
            string? error = Field<TextBlock>("SettingsNotice").Text;
            if (expectFailure) { Assert.That(error, Is.Not.Null.And.Not.Empty); }
            else { Assert.That(error, Is.Null.Or.Empty); }
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Dialog.IsVisible) { Dialog.Close(false); }
                await Result.WaitAsync(TimeSpan.FromSeconds(10));
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            }
            finally { if (_owner.IsVisible) { await AvatarUiTestDriver.Close(_owner); } }
        }
    }
}

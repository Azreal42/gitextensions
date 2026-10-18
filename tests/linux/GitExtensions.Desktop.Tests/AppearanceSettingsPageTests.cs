using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
public sealed class AppearanceSettingsPageTests
{
    [AvaloniaTest]
    public void PointerAndKeyboardEditAnIndependentDraftWithoutWritingProfilesOrChangingTheOwnersThemeAndCulture()
    {
        AppearanceSettingsValues initial = new() { Language = "English" }; using Scope f = new(initial);
        Dictionary<string, byte[]> before = f.Profiles(); CultureInfo culture = CultureInfo.CurrentUICulture;
        f.Click("chkShowRelativeDate"); Assert.That(f.Page.Draft.RelativeDate, Is.False);
        Press(f.Window, PhysicalKey.Space); Assert.That(f.Page.Draft.RelativeDate, Is.True);
        f.Click("chkShowRepoCurrentBranch"); f.Click("ShowAuthorAvatarInCommitGraph");
        f.Choose("SettingsTheme", "Dark"); f.ChooseLanguage("French");
        Assert.That(f.Page.Draft, Is.EqualTo(initial with { ShowRepoCurrentBranch = false, ShowAvatar = false, Theme = "Dark", Language = "French" }));
        Assert.That(initial, Is.EqualTo(new AppearanceSettingsValues { Language = "English" }));
        Assert.That(f.Window.RequestedThemeVariant, Is.EqualTo(ThemeVariant.Light));
        Assert.That(CultureInfo.CurrentUICulture, Is.EqualTo(culture), "Language is a saved choice for the host, not an immediate page side effect.");
        Assert.That(f.Page.Validate(out string? error), Is.True, error); f.SameProfiles(before);
    }

    [AvaloniaTest]
    public void ClosingAnUnpublishedPageAndReopeningFromTheSameSavedValuesDiscardsItsDraft()
    {
        AppearanceSettingsValues initial = new() { Language = "English" }; using Scope f = new(initial);
        Dictionary<string, byte[]> before = f.Profiles();
        f.Click("chkShowRelativeDate"); f.Click("chkShowRepoCurrentBranch"); f.Choose("SettingsTheme", "Dark");
        f.Window.Close(); Assert.That(f.Window.IsVisible, Is.False); f.SameProfiles(before);
        using Scope reopened = new(initial);
        Assert.That(reopened.Page.Draft, Is.EqualTo(initial));
        Assert.That(reopened.Field<CheckBox>("chkShowRelativeDate").IsChecked, Is.True);
        Assert.That(reopened.Field<CheckBox>("chkShowRepoCurrentBranch").IsChecked, Is.True);
        Assert.That(reopened.Field<ComboBox>("SettingsTheme").SelectedItem, Is.EqualTo("Light"));
    }

    [AvaloniaTest]
    public void AcceptSavedAdoptsMergedChoicesAndAnotherEditDoesNotRestoreTheOldBaseline()
    {
        using Scope f = new(new() { Language = "English" }); f.Click("chkShowRelativeDate");
        AppearanceSettingsValues published = f.Page.Draft with { ShowRepoCurrentBranch = false, ShowAvatar = false, Theme = "System", Language = "French" };
        f.Page.AcceptSaved(published); Pump(f.Window);
        Assert.That(f.Page.Draft, Is.EqualTo(published));
        Assert.That(f.Field<CheckBox>("ShowAuthorAvatarInCommitGraph").IsChecked, Is.False);
        Assert.That(f.Field<ComboBox>("SettingsTheme").SelectedItem, Is.EqualTo("System"));
        Assert.That((f.Field<ComboBox>("Language").SelectedItem as LanguageChoice)?.Name, Is.EqualTo("French"));
        f.Click("chkShowRelativeDate"); Assert.That(f.Page.Draft, Is.EqualTo(published with { RelativeDate = true }));
        Assert.That(f.Page.Validate(out string? error), Is.True, error);
    }

    [AvaloniaTest]
    [TestCase(null)]
    [TestCase("")]
    [TestCase("Removed catalogue Ω")]
    public void MissingOrUnavailableLanguageIdentityIsPreservedUntilTheUserExplicitlyChoosesALanguage(string? language)
    {
        using Scope f = new(new() { Language = language });
        Assert.That(f.Page.Draft.Language, Is.EqualTo(language));
        f.Click("chkShowRelativeDate"); Assert.That(f.Page.Draft.Language, Is.EqualTo(language));
        Assert.That(f.Page.Validate(out string? error), Is.True, error);
        f.ChooseLanguage("English"); Assert.That(f.Page.Draft.Language, Is.EqualTo("English"));
    }

    [AvaloniaTest]
    public void HelpTranslateUsesTheRealOwnerAndCannotStartASecondRequestWhileItsFirstRequestIsPending()
    {
        Window? receivedOwner = null; int calls = 0; TaskCompletionSource completion = new();
        using Scope f = new(new(), translate: owner => { receivedOwner = owner; calls++; return completion.Task; });
        AppearanceSettingsValues before = f.Page.Draft; f.Click("helpTranslate");
        Assert.That(receivedOwner, Is.SameAs(f.Window)); Assert.That(calls, Is.EqualTo(1));
        Assert.That(f.Field<HyperlinkButton>("helpTranslate").IsEffectivelyEnabled, Is.False);
        Press(f.Window, PhysicalKey.Enter); Assert.That(calls, Is.EqualTo(1));
        completion.SetResult(); Pump(f.Window);
        Assert.That(f.Field<HyperlinkButton>("helpTranslate").IsEffectivelyEnabled, Is.True);
        Assert.That(f.Page.Draft, Is.EqualTo(before));
    }

    [AvaloniaTest]
    public void ACurrentTranslateFailureIsVisibleAndTheLinkCanBeRetriedWithoutPublishingTheDraft()
    {
        bool fail = true; int calls = 0;
        using Scope f = new(new(), translate: _ => { calls++; return fail ? Task.FromException(new IOException("fixture-translate-error")) : Task.CompletedTask; });
        Dictionary<string, byte[]> before = f.Profiles(); f.Click("helpTranslate");
        Assert.That(f.Field<TextBlock>("AppearanceSettingsNotice").Text, Does.Contain("fixture-translate-error"));
        Assert.That(f.Field<HyperlinkButton>("helpTranslate").IsEffectivelyEnabled, Is.True);
        fail = false; f.Click("helpTranslate"); Assert.That(calls, Is.EqualTo(2));
        Assert.That(f.Field<TextBlock>("AppearanceSettingsNotice").Text, Is.Null.Or.Empty); f.SameProfiles(before);
    }

    [AvaloniaTest]
    [TestCase("accepted")]
    [TestCase("detached")]
    [TestCase("closed")]
    public void ATranslateFailureArrivingAfterARebaseOrDetachCannotPublishAnObsoleteNotice(string change)
    {
        TaskCompletionSource completion = new(); using Scope f = new(new(), translate: _ => completion.Task);
        TextBlock notice = f.Field<TextBlock>("AppearanceSettingsNotice"); f.Click("helpTranslate");
        AppearanceSettingsValues expected = f.Page.Draft;
        if (change == "accepted") { expected = expected with { ShowRepoCurrentBranch = false, Theme = "Dark" }; f.Page.AcceptSaved(expected); }
        else if (change == "detached") { f.Window.Content = new TextBlock { Text = "The user left Appearance." }; Pump(f.Window); }
        else { f.Window.Close(); }
        completion.SetException(new IOException("obsolete-translate-error")); Dispatcher.UIThread.RunJobs();
        Assert.That(notice.Text, Is.Null.Or.Empty); Assert.That(f.Page.Draft, Is.EqualTo(expected));
    }

    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("en", true)]
    [TestCase("fr-FR", false)]
    [TestCase("fr-FR", true)]
    public void TheOriginalGroupsAndActiveChoicesRemainReachableWithHonestUnavailableControlsInBothLanguagesAndThemes(string culture, bool dark)
    {
        using Scope f = new(new() { Language = "English" }, culture: culture, dark: dark);
        string[] groups = ["gbGeneral", "gbAuthorImages", "gbLanguages"]; double previous = double.NegativeInfinity;
        foreach (string name in groups)
        {
            Grid group = f.Field<Grid>(name); double y = group.TranslatePoint(default, f.Page)!.Value.Y;
            Assert.That(y, Is.GreaterThan(previous)); previous = y;
        }
        foreach (string name in new[] { "chkShowRelativeDate", "chkShowRepoCurrentBranch", "ShowAuthorAvatarInCommitGraph", "Language", "SettingsTheme",
            "ShowAuthorAvatarInCommitInfo", "_NO_TRANSLATE_DaysToCacheImages", "AvatarProvider", "_NO_TRANSLATE_NoImageService" })
        {
            Control control = f.Field<Control>(name); f.Show(control); AssertReachable(f.Window, control);
        }
        foreach (string name in new[] { "chkShowCurrentBranchInVisualStudio", "chkEnableAutoScale", "truncatePathMethod", "ClearImageCache",
            "avatarProviderHelp", "pictureAvatarHelp", "Dictionary", "downloadDictionary" })
        {
            Control control = f.Field<Control>(name); Assert.That(control.IsEffectivelyVisible, Is.True, name);
            Assert.That(control.IsEffectivelyEnabled, Is.False, name + " must not imply a provider or desktop integration that is not available.");
            Assert.That(ToolTip.GetTip(control), Is.Not.Null, name + " needs an explanation of its unavailable behavior.");
        }
        Assert.That(f.Field<Control>("txtCustomAvatarTemplate").IsEffectivelyVisible, Is.False, "The default None provider hides its custom template.");
        Assert.That(f.Field<Control>("txtCustomAvatarTemplate").IsEffectivelyEnabled, Is.True, "The template is editable when Custom is chosen.");
        Assert.That(f.Field<TextBlock>("AppearanceUnsupportedNotice").Text, Is.Not.Null.And.Not.Empty);
        Assert.That(f.Field<HyperlinkButton>("helpTranslate").IsEffectivelyEnabled, Is.False, "A missing host callback must not leave a dead enabled link.");
        TextBlock author = f.Field<Border>("gbAuthorImagesCaption").GetVisualDescendants().OfType<TextBlock>().Single();
        Assert.That(author.Text, Is.EqualTo(culture == "en" ? "Author images" : "Avatars des auteurs"));
        f.Show(f.Field<Grid>("gbGeneral")); Capture(f.Window, $"appearance-settings-{culture}-{(dark ? "dark" : "light")}-top.png");
        f.Show(f.Field<Grid>("gbLanguages")); Capture(f.Window, $"appearance-settings-{culture}-{(dark ? "dark" : "light")}-bottom.png");
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-appearance-page-" + Guid.NewGuid().ToString("N"));
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        internal Window Window { get; }
        internal AppearanceSettingsPage Page { get; }
        internal Scope(AppearanceSettingsValues initial, string culture = "en", bool dark = false, Func<Window, Task>? translate = null)
        {
            Directory.CreateDirectory(_root); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            new PreferencesStore().Save(new() { Language = "English", Theme = "Light" });
            new HistoryViewPreferencesStore().Save(new());
            Page = new(initial, TranslationCatalog.LoadDefault(), translate);
            Window = new() { Width = 740, Height = 590, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
                Content = new ScrollViewer { Content = Page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
            Window.Show(); Pump(Window);
        }
        internal T Field<T>(string name) where T : Control => PushProcessTestDriver.Field<T>(Window, name);
        internal void Show(Control control) { control.BringIntoView(); Pump(Window); }
        internal void Click(string name) { Control control = Field<Control>(name); Show(control); PushProcessTestDriver.Pointer(control); Pump(Window); }
        internal void Choose(string name, object value)
        {
            ComboBox combo = Field<ComboBox>(name); Show(combo); PushProcessTestDriver.Pointer(combo); Pump(Window);
            Assert.That(combo.IsDropDownOpen, Is.True); int index = combo.Items.IndexOf(value); Assert.That(index, Is.GreaterThanOrEqualTo(0));
            if (combo.SelectedIndex < 0)
            {
                Press(Window, PhysicalKey.ArrowDown); Pump(Window);
                Assert.That(combo.ContainerFromIndex(0)?.IsFocused, Is.True,
                    "An initially empty ComboBox needs a real Down key to focus its first dropdown item before Home/Enter can choose it.");
            }
            Press(Window, PhysicalKey.Home);
            for (int step = 0; step < index; step++) { Press(Window, PhysicalKey.ArrowDown); }
            Press(Window, PhysicalKey.Enter); Pump(Window); Assert.That(combo.SelectedItem, Is.EqualTo(value));
        }
        internal void ChooseLanguage(string name)
        { ComboBox combo = Field<ComboBox>("Language"); Choose("Language", combo.Items.OfType<LanguageChoice>().Single(choice => choice.Name == name)); }
        internal Dictionary<string, byte[]> Profiles() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(_root, path), File.ReadAllBytes, StringComparer.Ordinal);
        internal void SameProfiles(Dictionary<string, byte[]> expected)
        {
            Dictionary<string, byte[]> actual = Profiles(); Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
            foreach ((string path, byte[] bytes) in expected) { Assert.That(actual[path], Is.EqualTo(bytes), path); }
        }
        public void Dispose()
        {
            try { Window.Close(); }
            finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); CultureInfo.CurrentUICulture = _culture; try { Directory.Delete(_root, true); } catch (IOException) { } }
        }
    }

    private static void AssertReachable(Window window, Control control)
    {
        Point center = control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        Assert.That(control.IsEffectivelyVisible && control.IsEffectivelyEnabled, Is.True, control.Name);
        Assert.That(new Rect(default, window.ClientSize).Contains(center), Is.True, control.Name);
        Assert.That((window.InputHitTest(center) as Visual)?.GetSelfAndVisualAncestors().Contains(control), Is.True, control.Name);
    }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); Dispatcher.UIThread.RunJobs(); }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(Window window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string folder = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
}

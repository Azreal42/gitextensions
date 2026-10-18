using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AppearanceSettingsSessionTests
{
    [AvaloniaTest]
    public async Task CancelDiscardsAnAppearanceDraftKeptAcrossRealSettingsNavigation()
    {
        using Fixture f = new(); Dictionary<string, byte[]> before = f.Profiles(); await using Host h = new(f);
        AppearanceSettingsInput.Click(h.Dialog, "chkShowRelativeDate");
        AppearanceSettingsInput.Click(h.Dialog, "ShowAuthorAvatarInCommitGraph");
        AppearanceSettingsInput.Choose(h.Dialog, "SettingsTheme", "Dark");
        SettingsNavigationTestDriver.Select(h.Dialog, "Sorting"); SettingsNavigationTestDriver.Select(h.Dialog, "Appearance");
        Assert.That(h.Page.Draft, Is.EqualTo(h.Session.Initial with { RelativeDate = false, ShowAvatar = false, Theme = "Dark" }));
        f.SameProfiles(before); AppearanceSettingsInput.Click(h.Dialog, "CancelDesktopSettings");
        Assert.That(await h.Result, Is.False); Assert.That(h.Attempts, Is.Zero); f.SameProfiles(before);
    }

    [AvaloniaTest]
    public async Task ApplyRemainsOpenAndCancelDiscardsOnlyTheSubsequentAppearanceDraft()
    {
        using Fixture f = new(); await using (Host h = new(f))
        {
            AppearanceSettingsInput.Click(h.Dialog, "chkShowRelativeDate");
            AppearanceSettingsInput.Choose(h.Dialog, "SettingsTheme", "Dark"); await h.Apply();
            Assert.That(h.Dialog.IsVisible, Is.True); Assert.That(h.Result.IsCompleted, Is.False);
            Assert.That(f.Desktop.Load().Theme, Is.EqualTo("Dark")); Assert.That(f.History.Load().RelativeDate, Is.False);
            Assert.That(h.Active.RelativeDate, Is.False); Dictionary<string, byte[]> applied = f.Profiles();
            AppearanceSettingsInput.Click(h.Dialog, "ShowAuthorAvatarInCommitGraph");
            AppearanceSettingsInput.Choose(h.Dialog, "SettingsTheme", "Light");
            AppearanceSettingsInput.Click(h.Dialog, "CancelDesktopSettings"); Assert.That(await h.Result, Is.True);
            Assert.That(h.Attempts, Is.EqualTo(1)); f.SameProfiles(applied);
        }
        await using Host reopened = new(f);
        Assert.That(reopened.Page.Draft.Theme, Is.EqualTo("Dark"));
        Assert.That(reopened.Page.Draft.RelativeDate, Is.False); Assert.That(reopened.Page.Draft.ShowAvatar, Is.True);
    }

    [AvaloniaTest]
    public async Task OkPublishesTheRealAppearancePageAndClosesWithAnAcceptedResult()
    {
        using Fixture f = new(); await using Host h = new(f);
        AppearanceSettingsInput.Click(h.Dialog, "ShowAuthorAvatarInCommitGraph");
        AppearanceSettingsInput.ChooseLanguage(h.Dialog, "French");
        AppearanceSettingsInput.Click(h.Dialog, "SaveDesktopSettings");
        Assert.That(await h.Result.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(h.Attempts, Is.EqualTo(1)); Assert.That(h.Dialog.IsVisible, Is.False);
        Assert.That(f.Desktop.Load().Language, Is.EqualTo("French")); Assert.That(f.History.Load().ShowAvatar, Is.False);
        Assert.That(h.Active.ShowAvatar, Is.False); f.NoTemporaryProfiles();
    }

    [Test]
    public void AnUnchangedActiveViewIsNotPublishedOrReplacedBySavedDefaultsDuringAnUnrelatedDesktopEdit()
    {
        using Fixture f = new(); HistoryViewPreferences saved = f.History.Load() with
        { RelativeDate = false, ShowAvatar = false, ShowGraph = false, HiddenToolbarItems = ["saved-only"] };
        f.History.Save(saved); byte[] before = File.ReadAllBytes(f.History.FilePath);
        HistoryViewPreferences active = new() { ShowAuthor = false, ShowId = false, HiddenToolbarItems = ["runtime-only"] };
        AppearanceSettingsSession session = new(f.Desktop.Load(), active); AppearanceSettingsValues draft = session.Initial with { Theme = "Dark" };
        GeneralSettingsSession general = new(f.Desktop.Load());
        GeneralSettingsPublication desktop = general.Save(general.Initial, session.MergeDesktop(draft, f.Desktop.Load()), () => session.DesktopSaved(draft));
        AppearanceSettingsPublication publication = session.Save(draft, desktop.Desktop, active);
        Assert.That(File.ReadAllBytes(f.History.FilePath), Is.EqualTo(before));
        Assert.That(publication.History, Is.EqualTo(active)); Assert.That(publication.Values.RelativeDate, Is.True);
        Assert.That(publication.Values.ShowAvatar, Is.True); Assert.That(f.Desktop.Load().Theme, Is.EqualTo("Dark"));
    }

    [Test]
    [TestCase(true)]
    [TestCase(false)]
    public void EditingOneAppearanceViewFieldPreservesItsPeerSavedFieldAndOtherTemporaryViewChoices(bool editRelative)
    {
        using Fixture f = new(); HistoryViewPreferences active = new() { ShowGraph = false, ShowAuthor = false, ShowDate = false, HiddenToolbarItems = ["temporary"] };
        AppearanceSettingsSession session = new(f.Desktop.Load(), active);
        HistoryViewPreferences peer = new() { RelativeDate = editRelative, ShowAvatar = !editRelative, ShowBody = false, ShowNotes = true, HiddenToolbarItems = ["peer"] };
        f.History.Save(peer);
        AppearanceSettingsValues draft = session.Initial with { RelativeDate = !editRelative, ShowAvatar = editRelative };
        AppearanceSettingsPublication publication = session.Save(draft, f.Desktop.Load(), active);
        HistoryViewPreferences persisted = f.History.Load();
        Assert.That(JsonSerializer.Serialize(persisted), Is.EqualTo(JsonSerializer.Serialize(peer with { RelativeDate = false, ShowAvatar = false })),
            "Only the chosen Appearance field becomes a saved default; the peer's other field stays saved.");
        Assert.That(publication.History.RelativeDate, Is.EqualTo(!editRelative)); Assert.That(publication.History.ShowAvatar, Is.EqualTo(editRelative));
        Assert.That(publication.History.ShowGraph, Is.False); Assert.That(publication.History.ShowAuthor, Is.False);
        Assert.That(publication.History.ShowDate, Is.False); Assert.That(publication.History.HiddenToolbarItems, Is.EqualTo(new[] { "temporary" }));
    }

    [Test]
    public void DesktopMergeRetainsPeerAppearanceAndTheDesiredGeneralChangesWithoutPublishingOnItsOwn()
    {
        using Fixture f = new(); DesktopPreferences initial = f.Desktop.Load(); AppearanceSettingsSession session = new(initial, f.History.Load());
        DesktopPreferences peer = initial with { Theme = "System", Language = "French", ShowRepoCurrentBranch = false };
        f.Desktop.Save(peer); Dictionary<string, byte[]> before = f.Profiles();
        DesktopPreferences merged = session.MergeDesktop(session.Initial, initial with { MaxRevisionGraphCommits = 321, AutoNormaliseSymbol = "-" });
        Assert.That(merged.Theme, Is.EqualTo("System")); Assert.That(merged.Language, Is.EqualTo("French"));
        Assert.That(merged.ShowRepoCurrentBranch, Is.False); Assert.That(merged.MaxRevisionGraphCommits, Is.EqualTo(321));
        Assert.That(merged.AutoNormaliseSymbol, Is.EqualTo("-")); Assert.That(session.Initial.Theme, Is.EqualTo("Light")); f.SameProfiles(before);
    }

    [AvaloniaTest]
    public async Task ConsecutiveAppliesAdoptMergedDesktopPeersWithoutPublishingUntouchedTemporaryViewFields()
    {
        using Fixture f = new(); await using Host h = new(f);
        f.Desktop.Save(f.Desktop.Load() with { Theme = "System", Language = "French", ShowRepoCurrentBranch = false });
        f.History.Save(f.History.Load() with { ShowAvatar = false, ShowGraph = false });
        AppearanceSettingsInput.Click(h.Dialog, "chkShowRelativeDate"); await h.Apply();
        Assert.That(h.Page.Draft.Theme, Is.EqualTo("System")); Assert.That(h.Page.Draft.Language, Is.EqualTo("French"));
        Assert.That(h.Page.Draft.ShowRepoCurrentBranch, Is.False); Assert.That(h.Page.Draft.ShowAvatar, Is.True);
        Assert.That(h.Active.ShowAvatar, Is.True); Assert.That(f.History.Load().ShowAvatar, Is.False);
        f.Desktop.Save(f.Desktop.Load() with { Theme = "Light", Language = "English", ShowRepoCurrentBranch = true });
        f.History.Save(f.History.Load() with { RelativeDate = true, ShowDate = false });
        AppearanceSettingsInput.Click(h.Dialog, "ShowAuthorAvatarInCommitGraph"); await h.Apply();
        Assert.That(h.Page.Draft.Theme, Is.EqualTo("Light")); Assert.That(h.Page.Draft.Language, Is.EqualTo("English"));
        Assert.That(h.Page.Draft.ShowRepoCurrentBranch, Is.True); Assert.That(h.Active.RelativeDate, Is.False);
        Assert.That(f.History.Load().RelativeDate, Is.True); Assert.That(f.History.Load().ShowDate, Is.False);
        Assert.That(h.Active.ShowDate, Is.True); Assert.That(h.Active.ShowAvatar, Is.False); Assert.That(h.Attempts, Is.EqualTo(2));
    }

    [AvaloniaTest]
    [TestCase("Clone", "corrupt")]
    [TestCase("Clone", "directory")]
    [TestCase("History", "corrupt")]
    [TestCase("History", "directory")]
    [TestCase("History", "array")]
    public async Task ARealPartialPublicationRetainsItsDraftAndRetryPreservesNewDesktopPeers(string phase, string fault)
    {
        using Fixture f = new(); await using Host h = new(f); AppearanceSettingsValues initial = h.Page.Draft;
        AppearanceSettingsInput.Choose(h.Dialog, "SettingsTheme", "Dark"); AppearanceSettingsInput.ChooseLanguage(h.Dialog, "French");
        AppearanceSettingsInput.Click(h.Dialog, "chkShowRepoCurrentBranch"); AppearanceSettingsInput.Click(h.Dialog, "chkShowRelativeDate");
        AppearanceSettingsInput.Click(h.Dialog, "ShowAuthorAvatarInCommitGraph");
        if (phase == "Clone")
        {
            SettingsNavigationTestDriver.Select(h.Dialog, "General"); AppearanceSettingsInput.EditCombo(h.Dialog, "cbDefaultCloneDestination", "/tmp/appearance retry clone");
            SettingsNavigationTestDriver.Select(h.Dialog, "Appearance");
        }
        string path = phase == "Clone" ? f.Clone.FilePath : f.History.FilePath; byte[] original = File.ReadAllBytes(path);
        File.Delete(path);
        if (fault == "directory") { Directory.CreateDirectory(path); }
        else { File.WriteAllText(path, fault == "array" ? "[]" : "{broken-appearance-profile"); }
        string? broken = File.Exists(path) ? File.ReadAllText(path) : null;
        await h.Apply(expectFailure: true);
        Assert.That(h.Dialog.IsVisible, Is.True); Assert.That(h.Result.IsCompleted, Is.False);
        Assert.That(h.Field<TextBlock>("SettingsNotice").Text, Does.Contain("Some settings may already have been saved"));
        Assert.That(f.Desktop.Load().Theme, Is.EqualTo("Dark")); Assert.That(f.Desktop.Load().Language, Is.EqualTo("French"));
        Assert.That(f.Desktop.Load().ShowRepoCurrentBranch, Is.False);
        Assert.That(h.Page.Draft.Theme, Is.EqualTo("Dark")); Assert.That(h.Page.Draft.RelativeDate, Is.False);
        Assert.That(h.Session.Initial, Is.EqualTo(initial), "The Appearance page adopts a baseline only after the whole Appearance publication succeeds.");
        Assert.That(h.Active.RelativeDate, Is.True); Assert.That(h.Active.ShowAvatar, Is.True);
        if (fault == "directory") { Assert.That(Directory.Exists(path), Is.True); Directory.Delete(path); }
        else { Assert.That(File.ReadAllText(path), Is.EqualTo(broken)); }
        File.WriteAllBytes(path, original);
        f.Desktop.Save(f.Desktop.Load() with { Theme = "System", Language = "English", ShowRepoCurrentBranch = true });
        f.History.Save(f.History.Load() with { ShowGraph = false, HiddenToolbarItems = ["peer-during-retry"] });
        await h.Apply();
        Assert.That(f.Desktop.Load().Theme, Is.EqualTo("System")); Assert.That(f.Desktop.Load().Language, Is.EqualTo("English"));
        Assert.That(f.Desktop.Load().ShowRepoCurrentBranch, Is.True); Assert.That(h.Page.Draft.Theme, Is.EqualTo("System"));
        Assert.That(h.Page.Draft.RelativeDate, Is.False); Assert.That(h.Page.Draft.ShowAvatar, Is.False);
        Assert.That(f.History.Load().RelativeDate, Is.False); Assert.That(f.History.Load().ShowAvatar, Is.False);
        Assert.That(f.History.Load().ShowGraph, Is.False); Assert.That(f.History.Load().HiddenToolbarItems, Is.EqualTo(new[] { "peer-during-retry" }));
        Assert.That(h.Active.ShowGraph, Is.True); Assert.That(h.Field<TextBlock>("SettingsNotice").Text, Is.Null.Or.Empty);
        if (phase == "Clone") { Assert.That(f.Clone.Load().DefaultDestination, Is.EqualTo("/tmp/appearance retry clone")); }
        AppearanceSettingsInput.Click(h.Dialog, "CancelDesktopSettings"); Assert.That(await h.Result, Is.True); f.NoTemporaryProfiles();
    }

    [Test]
    public void InvalidAppearanceIntentCannotWriteOrAcknowledgeAnyExistingProfile()
    {
        using Fixture f = new(); AppearanceSettingsSession session = new(f.Desktop.Load(), f.History.Load());
        Dictionary<string, byte[]> before = f.Profiles(); AppearanceSettingsValues invalid = session.Initial with { Theme = "not-a-theme", RelativeDate = false };
        Assert.Throws<ArgumentException>(() => session.MergeDesktop(invalid, f.Desktop.Load()));
        Assert.Throws<ArgumentException>(() => session.Save(invalid, f.Desktop.Load(), f.History.Load()));
        Assert.That(session.Initial.Theme, Is.EqualTo("Light")); f.SameProfiles(before);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly Window _owner = new();
        internal DesktopSettingsDialog Dialog { get; }
        internal Task<bool> Result { get; }
        internal AppearanceSettingsSession Session { get; }
        internal HistoryViewPreferences Active { get; private set; }
        internal int Attempts { get; private set; }
        internal AppearanceSettingsPage Page => Field<AppearanceSettingsPage>("AppearanceSettingsPage");
        internal Host(Fixture f)
        {
            DesktopPreferences initial = f.Desktop.Load(); Active = f.History.Load(); Session = new(initial, Active);
            GeneralSettingsSession general = new(initial);
            Dialog = new(initial, TranslationCatalog.LoadDefault(), false, Active.Sort, fonts: new(), initialPage: "Appearance",
                generalSettings: general.Initial, appearanceSettings: Session.Initial, publishSettings: dialog =>
                {
                    Attempts++; AppearanceSettingsValues draft = dialog.AppearanceSettingsResult;
                    GeneralSettingsPublication desktop = general.Save(dialog.GeneralSettingsResult, Session.MergeDesktop(draft, dialog.Result),
                        desktopSaved: () => Session.DesktopSaved(draft));
                    dialog.AcceptGeneralSaved(desktop);
                    AppearanceSettingsPublication appearance = Session.Save(draft, desktop.Desktop, Active, dialog.HistorySortResult);
                    dialog.AcceptAppearanceSaved(appearance); Active = appearance.History; return Task.CompletedTask;
                });
            _owner.Show(); Result = Dialog.ShowDialog<bool>(_owner); AppearanceSettingsInput.Pump(Dialog);
        }
        internal T Field<T>(string name) where T : Control => PushProcessTestDriver.Field<T>(Dialog, name);
        internal async Task Apply(bool expectFailure = false)
        {
            int previous = Attempts; AppearanceSettingsInput.Click(Dialog, "ApplyDesktopSettings");
            await PushProcessTestDriver.UntilAsync(Dialog, () => Attempts == previous + 1 && Dialog.Navigation.IsEnabled);
            if (expectFailure) { Assert.That(Field<TextBlock>("SettingsNotice").Text, Is.Not.Null.And.Not.Empty); }
            else { Assert.That(Field<TextBlock>("SettingsNotice").Text, Is.Null.Or.Empty); }
        }
        public async ValueTask DisposeAsync()
        {
            try { if (Dialog.IsVisible) { Dialog.Close(false); } await Result.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { _owner.Close(); }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-appearance-publication-" + Guid.NewGuid().ToString("N"));
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        internal PreferencesStore Desktop { get; }
        internal HistoryViewPreferencesStore History { get; }
        internal ClonePreferencesStore Clone { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en"); Desktop = new(); History = new(); Clone = new();
            Desktop.Save(new() { Language = "English", Theme = "Light" }); History.Save(new()); Clone.SaveStrict(new("/tmp/previous clone", false));
        }
        internal Dictionary<string, byte[]> Profiles() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(_root, path), File.ReadAllBytes, StringComparer.Ordinal);
        internal void SameProfiles(Dictionary<string, byte[]> expected)
        {
            Dictionary<string, byte[]> actual = Profiles(); Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
            foreach ((string path, byte[] bytes) in expected) { Assert.That(actual[path], Is.EqualTo(bytes), path); }
        }
        internal void NoTemporaryProfiles() => Assert.That(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories), Is.Empty);
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); CultureInfo.CurrentUICulture = _culture;
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }
}

internal static class AppearanceSettingsInput
{
    internal static void Click(Window window, string name)
    {
        Control control = PushProcessTestDriver.Field<Control>(window, name); control.BringIntoView(); Pump(window);
        PushProcessTestDriver.Pointer(control); Pump(window);
    }
    internal static void Choose(Window window, string name, object value)
    {
        ComboBox combo = PushProcessTestDriver.Field<ComboBox>(window, name); combo.BringIntoView(); Pump(window);
        PushProcessTestDriver.Pointer(combo); Pump(window); Assert.That(combo.IsDropDownOpen, Is.True);
        int index = combo.Items.IndexOf(value); Assert.That(index, Is.GreaterThanOrEqualTo(0)); Press(window, PhysicalKey.Home);
        for (int step = 0; step < index; step++) { Press(window, PhysicalKey.ArrowDown); }
        Press(window, PhysicalKey.Enter); Pump(window); Assert.That(combo.SelectedItem, Is.EqualTo(value));
    }
    internal static void ChooseLanguage(Window window, string name)
    {
        ComboBox combo = PushProcessTestDriver.Field<ComboBox>(window, "Language");
        Choose(window, "Language", combo.Items.OfType<LanguageChoice>().Single(choice => choice.Name == name));
    }
    internal static void EditCombo(Window window, string name, string text)
    {
        ComboBox combo = PushProcessTestDriver.Field<ComboBox>(window, name); combo.BringIntoView(); Pump(window);
        TextBox editor = combo.GetVisualDescendants().OfType<TextBox>().Single(); PushProcessTestDriver.Pointer(editor);
        Press(window, PhysicalKey.A, RawInputModifiers.Control); Press(window, PhysicalKey.Backspace); window.KeyTextInput(text);
        Press(window, PhysicalKey.Tab); Pump(window); Assert.That(combo.Text, Is.EqualTo(text));
    }
    internal static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); Dispatcher.UIThread.RunJobs(); }
    internal static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}

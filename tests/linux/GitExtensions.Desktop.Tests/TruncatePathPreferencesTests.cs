using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class TruncatePathPreferencesTests
{
    [TestCase(TruncatePathMethod.None, 0)]
    [TestCase(TruncatePathMethod.Compact, 1)]
    [TestCase(TruncatePathMethod.TrimStart, 2)]
    [TestCase(TruncatePathMethod.FileNameOnly, 3)]
    public void OriginalEnumIdentityRoundTripsThroughTheExistingDesktopProfile(TruncatePathMethod method, int identity)
    {
        using AvatarSettingsTestProfile profile = new();
        Assert.That((int)method, Is.EqualTo(identity));
        DesktopPreferences desired = profile.Desktop.Load() with { TruncatePathMethod = method, Theme = "System", AvatarImageCacheDays = 31 };
        profile.Desktop.Save(desired);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(profile.Desktop.FilePath));
        Assert.That(json.RootElement.GetProperty("TruncatePathMethod").GetInt32(), Is.EqualTo(identity));
        Assert.That(JsonSerializer.Serialize(profile.Desktop.Load()), Is.EqualTo(JsonSerializer.Serialize(desired)));
        Assert.That(new AppearanceSettingsSession(profile.Desktop.Load(), profile.History.Load()).Initial.TruncatePathMethod, Is.EqualTo(method));
    }

    [TestCase("{}")]
    [TestCase("{\"TruncatePathMethod\":999}")]
    [TestCase("{\"TruncatePathMethod\":-1}")]
    public void MissingOrUnknownStoredValuesUseNoneInMemoryWithoutRewritingTheProfile(string json)
    {
        using AvatarSettingsTestProfile profile = new(); File.WriteAllText(profile.Desktop.FilePath, json);
        byte[] before = File.ReadAllBytes(profile.Desktop.FilePath);
        Assert.That(profile.Desktop.Load().TruncatePathMethod, Is.EqualTo(TruncatePathMethod.None));
        Assert.That(File.ReadAllBytes(profile.Desktop.FilePath), Is.EqualTo(before));
    }

    [Test]
    public void UntouchedMethodKeepsPeerChangesWhileAnExplicitChoiceOverridesOnlyItsOwnField()
    {
        using AvatarSettingsTestProfile profile = new(); DesktopPreferences initial = profile.Desktop.Load();
        AppearanceSettingsSession session = new(initial, profile.History.Load());
        DesktopPreferences peer = initial with { TruncatePathMethod = TruncatePathMethod.FileNameOnly, Theme = "System", AvatarImageCacheDays = 31 };
        profile.Desktop.Save(peer); byte[] before = File.ReadAllBytes(profile.Desktop.FilePath);
        DesktopPreferences untouched = session.MergeDesktop(session.Initial, initial with { HistoryLimit = 777 });
        Assert.That(untouched.TruncatePathMethod, Is.EqualTo(TruncatePathMethod.FileNameOnly));
        Assert.That(untouched.Theme, Is.EqualTo("System")); Assert.That(untouched.AvatarImageCacheDays, Is.EqualTo(31));
        Assert.That(untouched.HistoryLimit, Is.EqualTo(777));
        AppearanceSettingsValues draft = session.Initial with { TruncatePathMethod = TruncatePathMethod.Compact };
        DesktopPreferences edited = session.MergeDesktop(draft, initial with { HistoryLimit = 777 });
        Assert.That(edited.TruncatePathMethod, Is.EqualTo(TruncatePathMethod.Compact));
        Assert.That(edited.Theme, Is.EqualTo("System")); Assert.That(edited.AvatarImageCacheDays, Is.EqualTo(31));
        Assert.That(edited.HistoryLimit, Is.EqualTo(777)); Assert.That(session.Initial.TruncatePathMethod, Is.EqualTo(TruncatePathMethod.None));
        Assert.That(File.ReadAllBytes(profile.Desktop.FilePath), Is.EqualTo(before), "Merging draft intentions has no publication side effect.");
    }

    [Test]
    public void AnUnchangedSecondApplyKeepsTheSavedMethodAndBothProfileByteSequences()
    {
        using AvatarSettingsTestProfile profile = new();
        profile.Desktop.Save(profile.Desktop.Load() with { TruncatePathMethod = TruncatePathMethod.TrimStart });
        DesktopPreferences initial = profile.Desktop.Load(); HistoryViewPreferences active = profile.History.Load();
        AppearanceSettingsSession session = new(initial, active); GeneralSettingsSession general = new(initial);
        byte[] desktopBefore = File.ReadAllBytes(profile.Desktop.FilePath), historyBefore = File.ReadAllBytes(profile.History.FilePath);
        for (int apply = 0; apply < 2; apply++)
        {
            AppearanceSettingsValues draft = session.Initial;
            GeneralSettingsPublication desktop = general.Save(general.Initial, session.MergeDesktop(draft, initial), _ => session.DesktopSaved(draft));
            AppearanceSettingsPublication saved = session.Save(draft, desktop.Desktop, active);
            Assert.That(saved.Values.TruncatePathMethod, Is.EqualTo(TruncatePathMethod.TrimStart));
        }
        Assert.That(File.ReadAllBytes(profile.Desktop.FilePath), Is.EqualTo(desktopBefore));
        Assert.That(File.ReadAllBytes(profile.History.FilePath), Is.EqualTo(historyBefore));
    }

    [TestCase("Clone")]
    [TestCase("History")]
    public void PartialPublicationAcknowledgesTheActualSavedMethodAndRetryPreservesANewerPeer(string failedPhase)
    {
        using AvatarSettingsTestProfile profile = new(); DesktopPreferences initial = profile.Desktop.Load();
        HistoryViewPreferences active = profile.History.Load(); AppearanceSettingsSession session = new(initial, active); GeneralSettingsSession general = new(initial);
        AppearanceSettingsValues draft = session.Initial with { TruncatePathMethod = TruncatePathMethod.Compact, RelativeDate = false };
        GeneralSettingsValues generalDraft = failedPhase == "Clone" ? general.Initial with { DefaultCloneDestinationPath = "/tmp/truncate-new-clone" } : general.Initial;
        string failedPath = failedPhase == "Clone" ? profile.Clone.FilePath : profile.History.FilePath;
        byte[] original = File.ReadAllBytes(failedPath); const string corrupt = "{broken-truncate-profile"; File.WriteAllText(failedPath, corrupt);
        List<TruncatePathMethod> notified = [];
        void ActualDesktopSaved(DesktopPreferences saved)
        {
            Assert.That(profile.Desktop.Load().TruncatePathMethod, Is.EqualTo(saved.TruncatePathMethod), "Notification follows the real desktop write.");
            notified.Add(saved.TruncatePathMethod); session.DesktopSaved(draft);
        }
        Assert.Catch<JsonException>(() =>
        {
            GeneralSettingsPublication desktop = general.Save(generalDraft, session.MergeDesktop(draft, initial), ActualDesktopSaved);
            session.Save(draft, desktop.Desktop, active);
        });
        Assert.That(notified, Is.EqualTo(new[] { TruncatePathMethod.Compact }));
        Assert.That(profile.Desktop.Load().TruncatePathMethod, Is.EqualTo(TruncatePathMethod.Compact));
        Assert.That(session.Initial.TruncatePathMethod, Is.EqualTo(TruncatePathMethod.None), "The still-visible page has not adopted a partial publication.");
        Assert.That(File.ReadAllText(failedPath), Is.EqualTo(corrupt)); File.WriteAllBytes(failedPath, original);
        profile.Desktop.Save(profile.Desktop.Load() with { TruncatePathMethod = TruncatePathMethod.FileNameOnly, Theme = "System" });
        GeneralSettingsPublication retry = general.Save(generalDraft, session.MergeDesktop(draft, initial), ActualDesktopSaved);
        AppearanceSettingsPublication accepted = session.Save(draft, retry.Desktop, active);
        Assert.That(notified, Is.EqualTo(new[] { TruncatePathMethod.Compact, TruncatePathMethod.FileNameOnly }));
        Assert.That(profile.Desktop.Load().TruncatePathMethod, Is.EqualTo(TruncatePathMethod.FileNameOnly));
        Assert.That(accepted.Values.TruncatePathMethod, Is.EqualTo(TruncatePathMethod.FileNameOnly)); Assert.That(accepted.Values.Theme, Is.EqualTo("System"));
        Assert.That(accepted.History.RelativeDate, Is.False);
    }

    [Test]
    public void AFailedDesktopWriteDoesNotNotifyOrAcknowledgeThePendingTruncationChoice()
    {
        using AvatarSettingsTestProfile profile = new(); DesktopPreferences initial = profile.Desktop.Load();
        AppearanceSettingsSession session = new(initial, profile.History.Load()); GeneralSettingsSession general = new(initial);
        AppearanceSettingsValues draft = session.Initial with { TruncatePathMethod = TruncatePathMethod.Compact };
        byte[] original = File.ReadAllBytes(profile.Desktop.FilePath); List<TruncatePathMethod> notified = [];
        File.Delete(profile.Desktop.FilePath); Directory.CreateDirectory(profile.Desktop.FilePath);
        void ActualDesktopSaved(DesktopPreferences saved) { notified.Add(saved.TruncatePathMethod); session.DesktopSaved(draft); }
        try { Assert.Catch<IOException>(() => general.Save(general.Initial, session.MergeDesktop(draft, initial), ActualDesktopSaved)); }
        finally { Directory.Delete(profile.Desktop.FilePath); File.WriteAllBytes(profile.Desktop.FilePath, original); }
        Assert.That(notified, Is.Empty); Assert.That(session.Initial.TruncatePathMethod, Is.EqualTo(TruncatePathMethod.None));
        profile.Desktop.Save(profile.Desktop.Load() with { TruncatePathMethod = TruncatePathMethod.FileNameOnly });
        GeneralSettingsPublication retry = general.Save(general.Initial, session.MergeDesktop(draft, initial), ActualDesktopSaved);
        Assert.That(retry.Desktop.TruncatePathMethod, Is.EqualTo(TruncatePathMethod.Compact));
        Assert.That(notified, Is.EqualTo(new[] { TruncatePathMethod.Compact }));
    }

    [Test]
    public void AnInvalidTypedIntentionCannotPublishOrAcknowledgeAnyProfile()
    {
        using AvatarSettingsTestProfile profile = new(); AppearanceSettingsSession session = new(profile.Desktop.Load(), profile.History.Load());
        AppearanceSettingsValues invalid = session.Initial with { TruncatePathMethod = (TruncatePathMethod)999 };
        byte[] desktopBefore = File.ReadAllBytes(profile.Desktop.FilePath), historyBefore = File.ReadAllBytes(profile.History.FilePath);
        Assert.Throws<ArgumentException>(() => session.MergeDesktop(invalid, profile.Desktop.Load()));
        Assert.Throws<ArgumentException>(() => session.Save(invalid, profile.Desktop.Load(), profile.History.Load()));
        Assert.That(session.Initial.TruncatePathMethod, Is.EqualTo(TruncatePathMethod.None));
        Assert.That(File.ReadAllBytes(profile.Desktop.FilePath), Is.EqualTo(desktopBefore)); Assert.That(File.ReadAllBytes(profile.History.FilePath), Is.EqualTo(historyBefore));
    }

    [AvaloniaTest]
    [TestCase("en")]
    [TestCase("fr-FR")]
    public async Task FourTranslatedRealChoicesKeepTheirDraftAcrossNavigationAndOnlyApplyPublishes(string culture)
    {
        using AvatarSettingsTestProfile profile = new(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        byte[] before = File.ReadAllBytes(profile.Desktop.FilePath);
        await using (Host host = new(profile))
        {
            ComboBox combo = host.Field<ComboBox>("truncatePathMethod");
            Assert.That(combo.IsEffectivelyEnabled, Is.True); Assert.That(combo.IsEditable, Is.False);
            Assert.That(combo.Items.OfType<TruncatePathMethod>(), Is.EqualTo(new[] { TruncatePathMethod.None, TruncatePathMethod.Compact, TruncatePathMethod.TrimStart, TruncatePathMethod.FileNameOnly }));
            string[] labels = culture == "en" ? ["None", "Compact", "Trim start", "Filename only"] : ["Aucun", "Compacte", "Couper le début", "Nom de fichier seulement"];
            await AvatarUiTestDriver.Click(host.Dialog, combo); using (await AvatarUiTestDriver.Frame(host.Dialog)) { }
            for (int index = 0; index < labels.Length; index++)
            {
                ComboBoxItem item = combo.ContainerFromIndex(index) as ComboBoxItem ?? throw new AssertionException("Every original path choice must have a real dropdown row.");
                Assert.That(item.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == labels[index]), Is.True, labels[index]);
            }
            AvatarUiTestDriver.Press(host.Dialog, PhysicalKey.Home); AvatarUiTestDriver.Press(host.Dialog, PhysicalKey.Enter);
            using (await AvatarUiTestDriver.Frame(host.Dialog)) { }
            foreach (TruncatePathMethod method in new[] { TruncatePathMethod.Compact, TruncatePathMethod.TrimStart, TruncatePathMethod.FileNameOnly, TruncatePathMethod.None })
            {
                await AvatarUiTestDriver.Choose(host.Dialog, combo, method); Assert.That(host.Page.Draft.TruncatePathMethod, Is.EqualTo(method));
                Assert.That(host.Dialog.IsVisible, Is.True, "Choosing with Enter must not activate the host's default OK button.");
            }
            await AvatarUiTestDriver.Choose(host.Dialog, combo, TruncatePathMethod.Compact);
            SettingsNavigationTestDriver.Select(host.Dialog, "General"); SettingsNavigationTestDriver.Select(host.Dialog, "Appearance");
            Assert.That(combo.SelectedItem, Is.EqualTo(TruncatePathMethod.Compact)); Assert.That(host.Page.Draft.TruncatePathMethod, Is.EqualTo(TruncatePathMethod.Compact));
            Assert.That(File.ReadAllBytes(profile.Desktop.FilePath), Is.EqualTo(before));
            await AvatarUiTestDriver.Click(host.Dialog, host.Field<Button>("CancelDesktopSettings")); Assert.That(await host.Result, Is.False);
            Assert.That(host.Notified, Is.Empty); Assert.That(File.ReadAllBytes(profile.Desktop.FilePath), Is.EqualTo(before));
        }
        await using (Host host = new(profile))
        {
            await AvatarUiTestDriver.Choose(host.Dialog, host.Field<ComboBox>("truncatePathMethod"), TruncatePathMethod.Compact);
            await AvatarUiTestDriver.Click(host.Dialog, host.Field<Button>("ApplyDesktopSettings"));
            Assert.That(host.Dialog.IsVisible, Is.True); Assert.That(host.Result.IsCompleted, Is.False);
            Assert.That(host.Notified, Is.EqualTo(new[] { TruncatePathMethod.Compact }));
            Assert.That(profile.Desktop.Load().TruncatePathMethod, Is.EqualTo(TruncatePathMethod.Compact));
            byte[] applied = File.ReadAllBytes(profile.Desktop.FilePath);
            await AvatarUiTestDriver.Choose(host.Dialog, host.Field<ComboBox>("truncatePathMethod"), TruncatePathMethod.FileNameOnly);
            await AvatarUiTestDriver.Click(host.Dialog, host.Field<Button>("CancelDesktopSettings")); Assert.That(await host.Result, Is.True);
            Assert.That(File.ReadAllBytes(profile.Desktop.FilePath), Is.EqualTo(applied));
        }
        await using Host reopened = new(profile);
        Assert.That(reopened.Field<ComboBox>("truncatePathMethod").SelectedItem, Is.EqualTo(TruncatePathMethod.Compact));
        Assert.That(reopened.Page.Draft.TruncatePathMethod, Is.EqualTo(TruncatePathMethod.Compact));
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly Window _owner = new();
        internal DesktopSettingsDialog Dialog { get; }
        internal Task<bool> Result { get; }
        internal List<TruncatePathMethod> Notified { get; } = [];
        internal AppearanceSettingsPage Page => Field<AppearanceSettingsPage>("AppearanceSettingsPage");
        internal Host(AvatarSettingsTestProfile profile)
        {
            DesktopPreferences initial = profile.Desktop.Load(); HistoryViewPreferences active = profile.History.Load();
            AppearanceSettingsSession session = new(initial, active); GeneralSettingsSession general = new(initial);
            Dialog = new(initial, TranslationCatalog.LoadDefault(), false, active.Sort, fonts: new(), initialPage: "Appearance",
                generalSettings: general.Initial, appearanceSettings: session.Initial, publishSettings: dialog =>
                {
                    AppearanceSettingsValues draft = dialog.AppearanceSettingsResult;
                    GeneralSettingsPublication desktop = general.Save(dialog.GeneralSettingsResult, session.MergeDesktop(draft, dialog.Result), accepted =>
                    {
                        Assert.That(profile.Desktop.Load().TruncatePathMethod, Is.EqualTo(accepted.TruncatePathMethod));
                        Notified.Add(accepted.TruncatePathMethod); session.DesktopSaved(draft);
                    });
                    dialog.AcceptGeneralSaved(desktop);
                    AppearanceSettingsPublication saved = session.Save(draft, desktop.Desktop, active, dialog.HistorySortResult);
                    dialog.AcceptAppearanceSaved(saved); active = saved.History; return Task.CompletedTask;
                });
            _owner.Show(); Result = Dialog.ShowDialog<bool>(_owner); AppearanceSettingsInput.Pump(Dialog);
        }
        internal T Field<T>(string name) where T : Control => AvatarUiTestDriver.Field<T>(Dialog, name);
        public async ValueTask DisposeAsync()
        {
            try { if (Dialog.IsVisible) { Dialog.Close(false); } await Result.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { _owner.Close(); }
        }
    }
}

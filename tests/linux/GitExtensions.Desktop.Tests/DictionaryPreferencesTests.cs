using System.Text.Json;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DictionaryPreferencesTests
{
    [Test]
    public void AnExistingProfileWithoutDictionaryUsesTheWindowsDefaultWithoutRewritingIt()
    {
        using AvatarSettingsTestProfile p = new(); File.WriteAllText(p.Desktop.FilePath, "{\"Theme\":\"Dark\"}");
        byte[] before = File.ReadAllBytes(p.Desktop.FilePath);
        DesktopPreferences current = p.Desktop.Load();
        Assert.That(current.Dictionary, Is.EqualTo("en-US")); Assert.That(current.Theme, Is.EqualTo("Dark"));
        Assert.That(new AppearanceSettingsSession(current, p.History.Load()).Initial.Dictionary, Is.EqualTo("en-US"));
        Assert.That(File.ReadAllBytes(p.Desktop.FilePath), Is.EqualTo(before));
    }

    [TestCase("None", "none")]
    [TestCase("NONE", "none")]
    [TestCase("fr-FR", "fr-FR")]
    [TestCase("My dictionary λ", "My dictionary λ")]
    [TestCase(" spaced name ", " spaced name ")]
    public void OnlyTheDisabledIdentityIsNormalizedInTheGlobalProfile(string value, string expected)
    {
        using AvatarSettingsTestProfile p = new();
        p.Desktop.Save(p.Desktop.Load() with { Dictionary = value, Theme = "System" });
        Assert.That(p.Desktop.Load().Dictionary, Is.EqualTo(expected));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(p.Desktop.FilePath));
        Assert.That(document.RootElement.GetProperty("Dictionary").GetString(), Is.EqualTo(expected));
        Assert.That(p.Desktop.Load().Theme, Is.EqualTo("System"));
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("../fr-FR")]
    [TestCase("dir/name")]
    [TestCase("dir\\name")]
    [TestCase(".")]
    [TestCase("..")]
    [TestCase("bad\0name")]
    public void InvalidNewDictionaryIntentCannotPublishAProfile(string invalid)
    {
        using AvatarSettingsTestProfile p = new(); byte[] before = File.ReadAllBytes(p.Desktop.FilePath);
        AppearanceSettingsSession session = new(p.Desktop.Load(), p.History.Load());
        Assert.Catch<ArgumentException>(() => session.MergeDesktop(session.Initial with { Dictionary = invalid }, p.Desktop.Load()));
        Assert.That(File.ReadAllBytes(p.Desktop.FilePath), Is.EqualTo(before));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("../not-a-basename")]
    [TestCase("missing-dictionary")]
    public void AnOldInvalidOrMissingIdentityRemainsDiagnosticAndDoesNotBlockUnrelatedAppearanceEdits(string? stored)
    {
        using AvatarSettingsTestProfile p = new();
        string original = JsonSerializer.Serialize(new { Dictionary = stored, Theme = "Light", AvatarImageCacheDays = 31 });
        File.WriteAllText(p.Desktop.FilePath, original);
        DesktopPreferences initial = p.Desktop.Load(); AppearanceSettingsSession session = new(initial, p.History.Load());
        string expected = stored ?? "en-US";
        Assert.That(initial.Dictionary, Is.EqualTo(expected)); Assert.That(session.Initial.Dictionary, Is.EqualTo(expected));
        Assert.That(File.ReadAllText(p.Desktop.FilePath), Is.EqualTo(original), "A read never repairs or silently disables a bad identity.");
        AppearanceSettingsValues draft = session.Initial with { Theme = "Dark" };
        DesktopPreferences merged = session.MergeDesktop(draft, initial);
        p.Desktop.Save(merged); session.DesktopSaved(draft);
        Assert.That(p.Desktop.Load().Dictionary, Is.EqualTo(expected)); Assert.That(p.Desktop.Load().Theme, Is.EqualTo("Dark"));
        Assert.That(p.Desktop.Load().AvatarImageCacheDays, Is.EqualTo(31));
        Assert.That(session.MergeDesktop(draft with { Dictionary = "none" }, merged).Dictionary, Is.EqualTo("none"), "An explicit valid selection can replace the unavailable identity.");
    }

    [Test]
    public void WindowsMessageOptionsDefaultTrueAndRoundTripWithoutJoiningTheAppearanceDraft()
    {
        using AvatarSettingsTestProfile p = new(); File.WriteAllText(p.Desktop.FilePath, "{}");
        Assert.That(p.Desktop.Load().ProvideAutocompletion, Is.True);
        Assert.That(p.Desktop.Load().MarkIllFormedLinesInCommitMsg, Is.True);
        p.Desktop.Save(p.Desktop.Load() with { ProvideAutocompletion = false, MarkIllFormedLinesInCommitMsg = false, Dictionary = "fr-FR" });
        DesktopPreferences saved = p.Desktop.Load();
        Assert.That(saved.ProvideAutocompletion, Is.False); Assert.That(saved.MarkIllFormedLinesInCommitMsg, Is.False);
        Assert.That(saved.Dictionary, Is.EqualTo("fr-FR"));
    }

    [Test]
    public void AnUntouchedDraftKeepsPeerDictionaryChangesAndAnExplicitChoiceOnlyOverridesItsOwnField()
    {
        using AvatarSettingsTestProfile p = new(); DesktopPreferences initial = p.Desktop.Load();
        AppearanceSettingsSession session = new(initial, p.History.Load());
        p.Desktop.Save(initial with { Dictionary = "fr-FR", Theme = "Dark", AvatarImageCacheDays = 31 });
        byte[] before = File.ReadAllBytes(p.Desktop.FilePath);
        DesktopPreferences untouched = session.MergeDesktop(session.Initial, initial with { HistoryLimit = 777 });
        Assert.That(untouched.Dictionary, Is.EqualTo("fr-FR")); Assert.That(untouched.Theme, Is.EqualTo("Dark"));
        DesktopPreferences edited = session.MergeDesktop(session.Initial with { Dictionary = "none" }, initial with { HistoryLimit = 777 });
        Assert.That(edited.Dictionary, Is.EqualTo("none")); Assert.That(edited.Theme, Is.EqualTo("Dark"));
        Assert.That(edited.AvatarImageCacheDays, Is.EqualTo(31)); Assert.That(edited.HistoryLimit, Is.EqualTo(777));
        Assert.That(File.ReadAllBytes(p.Desktop.FilePath), Is.EqualTo(before)); Assert.That(session.Initial.Dictionary, Is.EqualTo("en-US"));
    }

    [Test]
    public void ADictionaryOnlySaveDoesNotPublishHistoryAndAnUnchangedSecondApplyPreservesProfileBytes()
    {
        using AvatarSettingsTestProfile p = new(); DesktopPreferences initial = p.Desktop.Load(); HistoryViewPreferences active = p.History.Load();
        GeneralSettingsSession general = new(initial); AppearanceSettingsSession session = new(initial, active);
        AppearanceSettingsValues draft = session.Initial with { Dictionary = "fr-FR" }; byte[] history = File.ReadAllBytes(p.History.FilePath);
        GeneralSettingsPublication desktop = general.Save(general.Initial, session.MergeDesktop(draft, initial), _ => session.DesktopSaved(draft));
        AppearanceSettingsPublication saved = session.Save(draft, desktop.Desktop, active);
        Assert.That(saved.Values.Dictionary, Is.EqualTo("fr-FR")); Assert.That(p.Desktop.Load().Dictionary, Is.EqualTo("fr-FR"));
        byte[] accepted = File.ReadAllBytes(p.Desktop.FilePath);
        desktop = general.Save(general.Initial, session.MergeDesktop(saved.Values, desktop.Desktop), _ => session.DesktopSaved(saved.Values));
        session.Save(saved.Values, desktop.Desktop, active);
        Assert.That(File.ReadAllBytes(p.Desktop.FilePath), Is.EqualTo(accepted)); Assert.That(File.ReadAllBytes(p.History.FilePath), Is.EqualTo(history));
    }

    [Test]
    public void PartialDesktopPublicationAcknowledgesDictionaryIntentBeforeAnUnrelatedWorkflowFailure()
    {
        using AvatarSettingsTestProfile p = new(); DesktopPreferences initial = p.Desktop.Load();
        GeneralSettingsSession general = new(initial); AppearanceSettingsSession session = new(initial, p.History.Load());
        AppearanceSettingsValues draft = session.Initial with { Dictionary = "None" }; GeneralSettingsValues other = general.Initial with { DefaultCloneDestinationPath = "/tmp/dictionary-clone-draft" };
        byte[] clone = File.ReadAllBytes(p.Clone.FilePath); File.WriteAllText(p.Clone.FilePath, "{bad dictionary clone profile"); int published = 0;
        Assert.Catch<JsonException>(() => general.Save(other, session.MergeDesktop(draft, initial), accepted =>
        { Assert.That(accepted.Dictionary, Is.EqualTo("none")); published++; session.DesktopSaved(draft); }));
        Assert.That(published, Is.EqualTo(1)); Assert.That(p.Desktop.Load().Dictionary, Is.EqualTo("none"));
        Assert.That(session.Initial.Dictionary, Is.EqualTo("en-US"), "The visible draft is not rebound after only a partial save.");
        p.Desktop.Save(p.Desktop.Load() with { Dictionary = "de-DE" }); File.WriteAllBytes(p.Clone.FilePath, clone);
        DesktopPreferences retry = session.MergeDesktop(draft, initial);
        Assert.That(retry.Dictionary, Is.EqualTo("de-DE"), "Canonical None acknowledges the completed intent rather than overwriting a later peer publication.");
    }
}

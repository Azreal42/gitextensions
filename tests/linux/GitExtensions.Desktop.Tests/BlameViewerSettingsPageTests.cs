using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class BlameViewerSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("en", true)] [TestCase("fr", false)] [TestCase("fr", true)]
    public async Task TheTenOriginalChoicesGroupsWarningsAndKeyboardOrderUseTheSharedSettingsHost(string language, bool dark)
    {
        using PortableHelperSettingsFixture f = new(language, dark); Dictionary<string, byte[]> before = Profiles();
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("General"); SettingsNavigationTestDriver.Select(settings, "BlameViewer");
        BlameViewerSettingsPage page = Field<BlameViewerSettingsPage>(settings, "BlameViewerSettingsPage");
        Assert.That(page.GetVisualDescendants().OfType<CheckBox>().Select(box => box.Name),
            Is.EqualTo(BlameViewerSettingsCatalog.Fields.Select(field => field.Control)));
        Assert.That(page.GetVisualDescendants().OfType<RadioButton>().Select(radio => radio.Name), Is.EqualTo(new[] { "BlameViewerGlobalRB" }));
        Assert.That(Field<RadioButton>(settings, "BlameViewerGlobalRB").IsChecked, Is.True);
        Assert.That(page.GetVisualDescendants().OfType<Control>().Any(control => control.Name == "UseHistogramDiffAlgorithm"), Is.False);
        foreach (BlameViewerSetting field in BlameViewerSettingsCatalog.Fields)
        {
            CheckBox box = Field<CheckBox>(settings, field.Control); box.BringIntoView(); PortableHelperSettingsFixture.Pump(settings);
            TextBlock caption = (TextBlock)box.Content!;
            Assert.That(caption.Text, Is.EqualTo(f.Original("BlameViewerSettingsPage", field.Control + ".Text")));
            Assert.That(box.IsThreeState, Is.False); Assert.That(box.IsEnabled, Is.True);
            Assert.That(box.IsChecked, Is.EqualTo(BlameViewerSettingsCatalog.Get(new(), field.Name)));
            Point origin = caption.TranslatePoint(default, box)!.Value;
            Assert.That(origin.X, Is.GreaterThanOrEqualTo(0)); Assert.That(origin.X + caption.Bounds.Width, Is.LessThanOrEqualTo(box.Bounds.Width + .5));
            Assert.That(origin.Y + caption.TextLayout.Height, Is.LessThanOrEqualTo(box.Bounds.Height + .5), "Caption must fit its actual check box: " + field.Control);
            if (field.ManualAnchor is not null)
            {
                Assert.That(ToolTip.GetTip(box), Is.EqualTo(f.Original("BlameViewerSettingsPage", "_blameWarningTooltip.Text")));
                Assert.That(box.Tag, Is.EqualTo(field.ManualAnchor));
            }
        }
        foreach (string group in new[] { "groupBoxBlameSettings", "groupBoxDisplayResult" })
        {
            Border caption = Field<Border>(settings, group + "Caption");
            Assert.That(((TextBlock)caption.Child!).Text, Is.EqualTo(f.Original("BlameViewerSettingsPage", group + ".Text")));
            CheckBox firstInGroup = Field<CheckBox>(settings, group == "groupBoxBlameSettings" ? "cbIgnoreWhitespace" : "cbDisplayAuthorFirst");
            TextBlock firstLabel = (TextBlock)firstInGroup.Content!;
            Point legend = caption.TranslatePoint(default, page)!.Value, body = firstLabel.TranslatePoint(default, page)!.Value;
            Assert.That(legend.Y + caption.Bounds.Height, Is.LessThanOrEqualTo(body.Y), "The standard group caption must not cover its first label.");
            if (language == "fr" && group == "groupBoxDisplayResult")
            { Assert.That(((TextBlock)caption.Child!).Text, Is.EqualTo("Configuration de l'affichage des résultats")); }
        }
        CheckBox first = Field<CheckBox>(settings, "cbIgnoreWhitespace"); first.BringIntoView(); PortableHelperSettingsFixture.Pump(settings);
        PortableHelperSettingsFixture.Capture(settings, "blame-viewer-settings-" + language + (dark ? "-dark.png" : "-light.png"));
        PushProcessTestDriver.Pointer(first); Assert.That(page.Draft.IgnoreWhitespace, Is.False);
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.Tab); Assert.That(Field<CheckBox>(settings, "cbDetectMoveAndCopyInThisFile").IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.Enter); Assert.That(completion.IsCompleted, Is.False); Assert.That(f.Publications, Is.Zero);
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.False);
        SameProfiles(before);
    }

    [AvaloniaTest]
    public async Task ApplyThenCancelAndRestartKeepAcceptedIndependentAuthorDateChoicesAndSaveAllPersistsTheNextDraft()
    {
        using PortableHelperSettingsFixture f = new(); BlamePreferencesStore store = new(); store.Save(new() { UseHistogramDiffAlgorithm = true });
        JsonObject seeded = JsonNode.Parse(File.ReadAllBytes(store.FilePath))!.AsObject(); seeded["future"] = JsonValue.Create("keep Ω"); File.WriteAllText(store.FilePath, seeded.ToJsonString());
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("BlameViewer");
        PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "cbShowAuthor")); PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "cbShowAuthorDate"));
        Assert.That(Field<CheckBox>(settings, "cbShowAuthorTime").IsEnabled, Is.True, "Page checkboxes remain independent as in Windows.");
        PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => f.Publications == 1 && settings.Navigation.IsEnabled);
        Assert.That(store.Load().ShowAuthor, Is.False); Assert.That(store.Load().ShowAuthorDate, Is.False); Assert.That(store.Load().UseHistogramDiffAlgorithm, Is.True);
        Dictionary<string, byte[]> accepted = Profiles(); PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "cbShowAuthor"));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.True); SameProfiles(accepted);
        (DesktopSettingsDialog reopened, Task<bool> finished) = f.Open("BlameViewer");
        Assert.That(Field<CheckBox>(reopened, "cbShowAuthor").IsChecked, Is.False); Assert.That(Field<CheckBox>(reopened, "cbShowAuthorDate").IsChecked, Is.False);
        PushProcessTestDriver.Pointer(Field<CheckBox>(reopened, "cbShowAuthorTime")); PushProcessTestDriver.Pointer(Field<Button>(reopened, "SaveDesktopSettings"));
        Assert.That(await finished.WaitAsync(TimeSpan.FromSeconds(10)), Is.True); BlamePreferences restarted = new BlamePreferencesStore().Load();
        Assert.That(restarted.ShowAuthorTime, Is.False); Assert.That(restarted.ShowAuthor, Is.False); Assert.That(restarted.ShowAuthorDate, Is.False);
        Assert.That(restarted.UseHistogramDiffAlgorithm, Is.True); Assert.That(JsonNode.Parse(File.ReadAllBytes(store.FilePath))!["future"]!.GetValue<string>(), Is.EqualTo("keep Ω"));
    }

    [AvaloniaTest]
    public async Task AProfileReadErrorRemainsVisibleAndAnAttemptedApplyCannotReplaceItsUnknownBytes()
    {
        using PortableHelperSettingsFixture f = new(); BlamePreferencesStore store = new(); Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{ invalid profile Ω"); byte[] before = File.ReadAllBytes(store.FilePath);
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("BlameViewer");
        Assert.That(Field<TextBlock>(settings, "BlameViewerSettingsNotice").Text, Is.Not.Empty);
        PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "cbShowLineNumbers")); PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => f.Publications == 1 && settings.Navigation.IsEnabled);
        Assert.That(settings.IsVisible, Is.True); Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo("BlameViewer"));
        Assert.That(Field<TextBlock>(settings, "SettingsNotice").Text, Does.Contain("Blame preferences were unreadable"));
        Assert.That(Field<CheckBox>(settings, "cbShowLineNumbers").IsChecked, Is.True); Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(before));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task NewRealBlameUsesAcceptedQueryAndDisplaySettingsWhileOldViewAndGeneralEditsPreservePeers()
    {
        using Fixture git = await Fixture.CreateAsync(); string initial = await git.Repository.ResolveRevisionAsync("HEAD");
        await git.ChangeAsync("    base\n", "Whitespace only"); string latest = await git.Repository.ResolveRevisionAsync("HEAD");
        git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
        byte[] index = File.ReadAllBytes(git.PathInRepository(".git/index")), config = File.ReadAllBytes(git.PathInRepository(".git/config"));
        string refs = (await git.GitAsync("show-ref")).StandardOutput;
        using PortableHelperSettingsFixture f = new(); BlamePreferencesStore store = new(); store.Save(new() { ShowAuthorAvatar = false });
        JsonObject seeded = JsonNode.Parse(File.ReadAllBytes(store.FilePath))!.AsObject(); seeded["future"] = JsonValue.Create("peer Ω"); File.WriteAllText(store.FilePath, seeded.ToJsonString());
        GeneralSettingsSession general = new(new()); BlameDialog old = new(git.Repository, "file.txt", avatarProvider: new NoAvatars()); f.Track(old); old.Show(); await old.InitializeAsync();
        BlameView captured = Field<BlameView>(old, "BlameView"); Assert.That(old.LastError, Is.Null, old.LastException?.ToString());
        Assert.That(old.LastBlame!.Lines.Single().Line.Hash, Is.EqualTo(initial));
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("BlameViewer");
        foreach (string control in new[] { "cbIgnoreWhitespace", "cbShowAuthor", "cbShowAuthorDate", "cbShowLineNumbers" })
        { PushProcessTestDriver.Pointer(Field<CheckBox>(settings, control)); }
        PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings")); await PushProcessTestDriver.UntilAsync(settings, () => f.Publications == 1 && settings.Navigation.IsEnabled);
        Assert.That(captured.ShowLineNumbers, Is.False); Assert.That(captured.ShowAuthor, Is.True); Assert.That(captured.ShowDate, Is.True);
        Assert.That(captured.Preferences.IgnoreWhitespace, Is.True, "Windows has no global Settings publication to these existing standalone viewers.");
        general.Save(general.Initial with { UseHistogramDiffAlgorithm = true }, new());
        Assert.That(store.Load().ShowAuthorDate, Is.False); Assert.That(captured.Preferences.UseHistogramDiffAlgorithm, Is.True, "The existing eleventh option retains its late shared read.");
        captured.SetPreferences(captured.Preferences with { ShowOriginalFilePath = false });
        Assert.That(store.Load().ShowLineNumbers, Is.True); Assert.That(store.Load().IgnoreWhitespace, Is.False); Assert.That(store.Load().ShowAuthor, Is.False); Assert.That(store.Load().ShowAuthorDate, Is.False);
        BlameDialog next = new(git.Repository, "file.txt", avatarProvider: new NoAvatars()); f.Track(next); next.Show(); await next.InitializeAsync();
        BlameView restored = Field<BlameView>(next, "BlameView"); Assert.That(next.LastError, Is.Null, next.LastException?.ToString());
        Assert.That(next.LastBlame!.Lines.Single().Line.Hash, Is.EqualTo(latest), "The accepted IgnoreWhitespace checkbox must change the real Git blame attribution.");
        Assert.That(restored.ShowAuthor, Is.False); Assert.That(restored.ShowDate, Is.False); Assert.That(restored.ShowLineNumbers, Is.True);
        restored.SetPreferences(restored.Preferences with { DisplayAuthorFirst = true });
        Assert.That(store.Load().ShowAuthorDate, Is.False, "An unrelated menu intention must not normalize the independent page values.");
        Assert.That(JsonNode.Parse(File.ReadAllBytes(store.FilePath))!["future"]!.GetValue<string>(), Is.EqualTo("peer Ω"));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await completion.WaitAsync(TimeSpan.FromSeconds(10));
        next.Close(); old.Close(); await PushProcessTestDriver.UntilAsync(f.Owner, () => !next.IsVisible && !old.IsVisible && !next.IsBusy && !old.IsBusy);
        Assert.That(File.ReadAllBytes(git.PathInRepository(".git/index")), Is.EqualTo(index)); Assert.That(File.ReadAllBytes(git.PathInRepository(".git/config")), Is.EqualTo(config));
        Assert.That((await git.GitAsync("show-ref")).StandardOutput, Is.EqualTo(refs));
    }

    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static Dictionary<string, byte[]> Profiles()
    {
        string directory = Path.GetDirectoryName(new PreferencesStore().FilePath)!;
        return Directory.Exists(directory) ? Directory.GetFiles(directory).ToDictionary(path => path, File.ReadAllBytes) : new();
    }
    private static void SameProfiles(Dictionary<string, byte[]> expected)
    {
        Dictionary<string, byte[]> actual = Profiles(); Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
        foreach ((string path, byte[] bytes) in expected) { Assert.That(actual[path], Is.EqualTo(bytes), path); }
    }
    private sealed class NoAvatars : IGourceAvatarProvider
    { public Task<byte[]?> GetAvatarPngAsync(string email, string authorName, int imageSize, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null); }
}

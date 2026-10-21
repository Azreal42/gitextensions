using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DeleteUnusedBranchesSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalNineGeneratedFieldsRemainFullyReadableAtTheRealSettingsWidth(string language, bool dark)
    {
        await using DeleteUnusedBranchesSettingsUiFixture f = await DeleteUnusedBranchesSettingsUiFixture.Create(language: language, dark: dark);
        Assert.That(f.Dialog.Width, Is.EqualTo(958));
        Assert.That(f.Field<RadioButton>("DeleteUnusedBranchesEffectiveRB").IsChecked, Is.True);
        Assert.That(f.Page.GetVisualDescendants().OfType<NumericUpDown>().Count(), Is.EqualTo(1));
        Assert.That(f.Page.GetVisualDescendants().OfType<CheckBox>().Count(), Is.EqualTo(5));
        Assert.That(f.Page.GetVisualDescendants().OfType<TextBox>().Count(box => box.Name != "PART_TextBox"), Is.EqualTo(3));
        TreeViewItem pluginNode = f.Dialog.GetVisualDescendants().OfType<TreeViewItem>().Single(node => node.Name == "SettingsPluginDeleteUnusedBranchesNode");
        Image icon = ((Control)pluginNode.Header!).GetVisualDescendants().OfType<Image>().Single();
        Assert.That(icon.Source, Is.Not.Null); Assert.That(icon.Width, Is.EqualTo(16)); Assert.That(icon.Height, Is.EqualTo(16));
        foreach (DeleteUnusedBranchesSetting setting in DeleteUnusedBranchesSettingsCatalog.Fields)
        {
            Assert.That(f.Field<Control>(setting.Control).IsEffectivelyEnabled, Is.False, setting.Control);
            Assert.That(f.Field<TextBlock>(setting.Control + "Caption").Text,
                Is.EqualTo(f.Translations.Translate("DeleteUnusedBranchesPlugin", setting.Control, "Caption", setting.Caption)));
        }
        f.Select(GitDistributedSettingsScope.Global);
        Grid table = f.Field<Grid>("DeleteUnusedBranchesSettingsTable");
        Assert.That(table.ColumnDefinitions, Has.Count.EqualTo(3)); Assert.That(table.RowDefinitions, Has.Count.EqualTo(9));
        Assert.That(f.Field<NumericUpDown>("_daysOlderThan").Maximum, Is.EqualTo(int.MaxValue));
        foreach (TextBlock caption in table.Children.OfType<TextBlock>())
        {
            Assert.That(caption.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False, caption.Text);
            // Wrapped line-end whitespace has no visible glyph; readability measures the non-whitespace line width.
            Assert.That(caption.TextLayout.Width, Is.LessThanOrEqualTo(caption.Bounds.Width + .5), caption.Text);
            Rect bounds = new(caption.TranslatePoint(default, f.Dialog)!.Value, caption.Bounds.Size);
            Assert.That(new Rect(default, f.Dialog.ClientSize).Contains(bounds), Is.True, caption.Text);
        }
        f.Capture("delete-obsolete-settings-" + language + (dark ? "-dark.png" : "-light.png"));
        Assert.That(f.Session.HasChanges, Is.False); Assert.That(f.Material(), Is.EqualTo(f.Before));
    }

    [AvaloniaTest]
    [TestCase("none")] [TestCase("bare")]
    public async Task NoRepositoryShowsOnlyGlobalAndBareDisablesOnlyTheDistributedScope(string kind)
    {
        await using DeleteUnusedBranchesSettingsUiFixture f = await DeleteUnusedBranchesSettingsUiFixture.Create(kind);
        Assert.That(f.Field<RadioButton>("DeleteUnusedBranchesGlobalRB").IsVisible, Is.True);
        if (kind == "none")
        {
            foreach (string name in new[] { "Effective", "Local", "Distributed" })
            { Assert.That(f.Field<RadioButton>("DeleteUnusedBranches" + name + "RB").IsVisible, Is.False); }
            Assert.That(f.Field<TextBox>("_remoteName").IsEffectivelyEnabled, Is.True);
        }
        else
        {
            Assert.That(f.Field<RadioButton>("DeleteUnusedBranchesDistributedRB").IsEffectivelyEnabled, Is.False);
            f.Select(GitDistributedSettingsScope.Local); Assert.That(f.Field<TextBox>("_remoteName").IsEffectivelyEnabled, Is.True);
        }
    }

    [AvaloniaTest]
    public async Task ScopeDraftsSurviveNavigationAndRealCancelLeavesXmlAndLegacyProfilesUntouched()
    {
        await using DeleteUnusedBranchesSettingsUiFixture f = await DeleteUnusedBranchesSettingsUiFixture.Create();
        f.Select(GitDistributedSettingsScope.Local); f.Edit("_remoteName", "local-draft");
        f.Select(GitDistributedSettingsScope.Distributed); f.Edit("_mergedInBranch", "distributed-draft");
        f.Select(GitDistributedSettingsScope.Global); f.Edit("_regexFilter", "^global/λ$");
        SettingsNavigationTestDriver.Select(f.Dialog, "General"); SettingsNavigationTestDriver.Select(f.Dialog, "PluginDeleteUnusedBranches");
        f.Select(GitDistributedSettingsScope.Local); Assert.That(f.Field<TextBox>("_remoteName").Text, Is.EqualTo("local-draft"));
        f.Select(GitDistributedSettingsScope.Distributed); Assert.That(f.Field<TextBox>("_mergedInBranch").Text, Is.EqualTo("distributed-draft"));
        f.Select(GitDistributedSettingsScope.Global); Assert.That(f.Field<TextBox>("_regexFilter").Text, Is.EqualTo("^global/λ$"));
        f.Click("CancelDesktopSettings"); Assert.That(await f.Completion.WaitAsync(TimeSpan.FromSeconds(20)), Is.False);
        Assert.That(f.Material(), Is.EqualTo(f.Before));
    }

    [AvaloniaTest]
    public async Task UntouchedUnknownsDoNotBecomeIndeterminateWritesAndThreeStateClicksCanExplicitlyUnset()
    {
        DeleteUnusedBranchesSetting setting = DeleteUnusedBranchesSettingsCatalog.Find("_regexInvertedFlag");
        await using DeleteUnusedBranchesSettingsUiFixture f = await DeleteUnusedBranchesSettingsUiFixture.Create(prepare: profile =>
            profile.Write(GitDistributedSettingsScope.Global, (setting.Key, "future-bool"), ("foreign.key", "keep λ")));
        f.Select(GitDistributedSettingsScope.Global); CheckBox box = f.Field<CheckBox>(setting.Control);
        Assert.That(box.IsThreeState, Is.True); Assert.That(box.IsChecked, Is.Null);
        f.Click("ApplyDesktopSettings"); await f.Saved(); Assert.That(f.Material(), Is.EqualTo(f.Before));
        f.Click(setting.Control); Assert.That(box.IsChecked, Is.False);
        f.Click(setting.Control); Assert.That(box.IsChecked, Is.True);
        f.Click(setting.Control); Assert.That(box.IsChecked, Is.Null);
        f.Click("ApplyDesktopSettings"); await f.Saved();
        GitXmlSettingsSnapshot xml = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global);
        Assert.That(xml.Values.ContainsKey(setting.Key), Is.False); Assert.That(xml.Values["foreign.key"], Is.EqualTo("keep λ"));
    }

    [AvaloniaTest]
    [TestCase("", null)] [TestCase("<empty string>", "")] [TestCase("  literal λ  ", "literal λ")]
    public async Task OriginalStringSentinelDistinguishesUnsetFromARealEmptyStringOnApply(string typed, string? saved)
    {
        DeleteUnusedBranchesSetting setting = DeleteUnusedBranchesSettingsCatalog.Find("_regexFilter");
        await using DeleteUnusedBranchesSettingsUiFixture f = await DeleteUnusedBranchesSettingsUiFixture.Create(prepare: profile =>
            profile.Write(GitDistributedSettingsScope.Global, (setting.Key, "before"), ("foreign.key", "keep")));
        f.Select(GitDistributedSettingsScope.Global); f.Edit(setting.Control, typed);
        f.Click("ApplyDesktopSettings"); await f.Saved();
        GitXmlSettingsSnapshot xml = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global);
        Assert.That(xml.Values.GetValueOrDefault(setting.Key), Is.EqualTo(saved));
        Assert.That(xml.Values.ContainsKey(setting.Key), Is.EqualTo(saved is not null));
        Assert.That(f.Field<TextBox>(setting.Control).Text, Is.EqualTo(saved is { Length: 0 } ? "<empty string>" : saved ?? ""));
    }

    [AvaloniaTest]
    public async Task InvalidAgeInANonvisibleScopeSelectsAndFocusesTheRealEditorWithoutWritingOtherDrafts()
    {
        await using DeleteUnusedBranchesSettingsUiFixture f = await DeleteUnusedBranchesSettingsUiFixture.Create();
        f.Select(GitDistributedSettingsScope.Global); f.Edit("_daysOlderThan", "100001");
        f.Select(GitDistributedSettingsScope.Local); f.Edit("_remoteName", "must-not-save-yet");
        SettingsNavigationTestDriver.Select(f.Dialog, "Appearance"); f.Click("ApplyDesktopSettings");
        await f.Until(() => !string.IsNullOrEmpty(f.Field<TextBlock>("SettingsNotice").Text));
        Assert.That(f.Dialog.Navigation.SelectedPage.Id, Is.EqualTo("PluginDeleteUnusedBranches"));
        Assert.That(f.Field<RadioButton>("DeleteUnusedBranchesGlobalRB").IsChecked, Is.True);
        Assert.That(f.Field<NumericUpDown>("_daysOlderThan").IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Field<NumericUpDown>("_daysOlderThan").GetVisualDescendants().OfType<TextBox>().Single().IsFocused, Is.True);
        Assert.That(f.Material(), Is.EqualTo(f.Before));
        f.Edit("_daysOlderThan", "100000"); f.Click("ApplyDesktopSettings"); await f.Saved();
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values[DeleteUnusedBranchesSettingsCatalog.Find("_daysOlderThan").Key], Is.EqualTo("100000"));
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Local)).Values[DeleteUnusedBranchesSettingsCatalog.Find("_remoteName").Key], Is.EqualTo("must-not-save-yet"));
    }

    [AvaloniaTest]
    public async Task ARealWriteConflictKeepsTheWindowDraftAndPeerBytesAndIsReportedOnThePluginPage()
    {
        await using DeleteUnusedBranchesSettingsUiFixture f = await DeleteUnusedBranchesSettingsUiFixture.Create();
        f.Select(GitDistributedSettingsScope.Global); f.Edit("_remoteName", "mine");
        GitXmlSettingsSnapshot xml = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global);
        await GitXmlSettingsStore.SaveChangesAsync(xml, new Dictionary<string, string?> { ["peer.future"] = "peer" });
        byte[] beforeApply = File.ReadAllBytes(xml.Path);
        f.Click("ApplyDesktopSettings"); await f.Until(() => f.Field<Button>("ApplyDesktopSettings").IsEnabled && !string.IsNullOrEmpty(f.Field<TextBlock>("SettingsNotice").Text));
        Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Dialog.Navigation.SelectedPage.Id, Is.EqualTo("PluginDeleteUnusedBranches"));
        Assert.That(f.Field<TextBox>("_remoteName").Text, Is.EqualTo("mine")); Assert.That(f.Session.HasChanges, Is.True);
        Assert.That(File.ReadAllBytes(xml.Path), Is.EqualTo(beforeApply));
    }

    [AvaloniaTest]
    public async Task FirstClickOwnsTheEditorAndPhysicalTabAndEnterNeverAccidentallyAcceptSettings()
    {
        await using DeleteUnusedBranchesSettingsUiFixture f = await DeleteUnusedBranchesSettingsUiFixture.Create();
        f.Select(GitDistributedSettingsScope.Global); f.Click("_mergedInBranch");
        Assert.That(f.Field<TextBox>("_mergedInBranch").IsFocused, Is.True);
        f.Edit("_mergedInBranch", "draft"); f.Press(PhysicalKey.Enter);
        Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Material(), Is.EqualTo(f.Before));
        f.Press(PhysicalKey.Tab); Assert.That(f.Field<CheckBox>("_deleteRemoteBranchesFromFlag").IsFocused, Is.True);
        f.Press(PhysicalKey.Enter); Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Material(), Is.EqualTo(f.Before));
    }
}

internal sealed class DeleteUnusedBranchesSettingsUiFixture : IAsyncDisposable
{
    internal DetailedSettingsTestScope Profile { get; }
    internal DeleteUnusedBranchesSettingsSession Session { get; private set; } = null!;
    internal DesktopSettingsDialog Dialog { get; private set; } = null!;
    internal DeleteUnusedBranchesSettingsPage Page => Dialog.GetVisualDescendants().OfType<DeleteUnusedBranchesSettingsPage>().Single();
    internal Window Owner { get; } = new() { Width = 1080, Height = 760 };
    internal Task<bool> Completion { get; private set; } = null!;
    internal TranslationCatalog Translations { get; private set; } = null!;
    internal Dictionary<string, byte[]> Before { get; private set; } = [];
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture, _uiCulture = CultureInfo.CurrentUICulture;
    private DeleteUnusedBranchesSettingsUiFixture(DetailedSettingsTestScope profile) => Profile = profile;
    internal static async Task<DeleteUnusedBranchesSettingsUiFixture> Create(string kind = "worktree", string language = "en", bool dark = false,
        Action<DetailedSettingsTestScope>? prepare = null)
    {
        DeleteUnusedBranchesSettingsUiFixture f = new(await DetailedSettingsTestScope.CreateAsync(kind));
        try
        {
            prepare?.Invoke(f.Profile); f.Before = f.Material();
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            f.Translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
            DistributedSettingsWorkspace workspace = new(f.Profile.Repository);
            f.Session = new(f.Profile.Repository, workspace: workspace); await f.Session.InitializeAsync(default);
            f.Dialog = new(f.Profile.Desktop.Load(), f.Translations, f.Profile.Repository is not null,
                detailedSettings: new(f.Profile.Repository, f.Profile.Desktop.Load(), workspace), deleteUnusedBranchesSettings: f.Session)
                { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            f.Owner.Show(); f.Completion = f.Dialog.ShowDialog<bool>(f.Owner);
            await f.Until(() => f.Dialog.DeleteUnusedBranchesSettingsSession.Loaded && f.Field<Button>("ApplyDesktopSettings").IsEnabled);
            SettingsNavigationTestDriver.Select(f.Dialog, "PluginDeleteUnusedBranches"); f.Pump(); return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    internal Dictionary<string, byte[]> Material() => Profile.Material();
    internal T Field<T>(string name) where T : Control => Dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    internal void Select(GitDistributedSettingsScope scope) => Click("DeleteUnusedBranches" + scope + "RB");
    internal void Click(string name) { Control control = Field<Control>(name); control.BringIntoView(); Pump(); PushProcessTestDriver.Pointer(control); Pump(); }
    internal void Edit(string name, string value)
    {
        Control control = Field<Control>(name); control.BringIntoView(); Pump();
        TextBox text = control as TextBox ?? control.GetVisualDescendants().OfType<TextBox>().Single();
        PushProcessTestDriver.Pointer(text); Press(PhysicalKey.A, RawInputModifiers.Control); Press(PhysicalKey.Backspace);
        if (value.Length > 0) { Dialog.KeyTextInput(value); } Pump(); Assert.That(text.Text, Is.EqualTo(value));
    }
    internal void Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { Dialog.KeyPressQwerty(key, modifiers); Dialog.KeyReleaseQwerty(key, modifiers); Pump(); }
    internal void Pump() => DetailedSettingsUiHost.Pump(Dialog);
    internal async Task Until(Func<bool> predicate)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!predicate() && clock.Elapsed < TimeSpan.FromSeconds(15)) { Pump(); await Task.Delay(10); }
        Pump(); Assert.That(predicate(), Is.True, "The actual plugin Settings workflow did not settle within fifteen seconds.");
    }
    internal Task Saved() => Until(() => !Session.HasChanges && Field<Button>("ApplyDesktopSettings").IsEnabled
        && string.IsNullOrEmpty(Field<TextBlock>("SettingsNotice").Text));
    internal void Capture(string name)
    {
        Pump(); using var frame = Dialog.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string root = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(root); string path = Path.Combine(root, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Dialog is { IsVisible: true }) { Dialog.Close(); await Until(() => !Dialog.IsVisible); }
            Owner.Close();
        }
        finally { Profile.Dispose(); CultureInfo.CurrentCulture = _culture; CultureInfo.CurrentUICulture = _uiCulture; }
    }
}

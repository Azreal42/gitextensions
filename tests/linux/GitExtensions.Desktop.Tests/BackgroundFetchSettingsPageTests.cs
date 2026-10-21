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
public sealed class BackgroundFetchSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task FiveOriginalFieldsAndTheReadonlyWarningUseTheRealSettingsTreeAtItsNormalWidth(string language, bool dark)
    {
        await using BackgroundFetchSettingsUiFixture f = await BackgroundFetchSettingsUiFixture.Create(language: language, dark: dark);
        Assert.That(f.Dialog.Width, Is.EqualTo(958));
        Assert.That(f.Field<RadioButton>("BackgroundFetchEffectiveRB").IsChecked, Is.True);
        Grid table = f.Field<Grid>("BackgroundFetchSettingsTable");
        Assert.That(table.ColumnDefinitions, Has.Count.EqualTo(3)); Assert.That(table.RowDefinitions, Has.Count.EqualTo(6));
        Assert.That(table.Children.OfType<CheckBox>().Count(), Is.EqualTo(3));
        Assert.That(table.Children.OfType<NumericUpDown>().Count(), Is.EqualTo(1));
        Assert.That(table.Children.OfType<TextBox>().Count(), Is.EqualTo(2));
        TreeViewItem node = f.Dialog.GetVisualDescendants().OfType<TreeViewItem>().Single(item => item.Name == "SettingsPluginBackgroundFetchNode");
        Image icon = ((Control)node.Header!).GetVisualDescendants().OfType<Image>().Single();
        Assert.That(icon.Source, Is.Not.Null); Assert.That(icon.Width, Is.EqualTo(16)); Assert.That(icon.Height, Is.EqualTo(16));
        foreach (PluginScopedSetting setting in BackgroundFetchSettingsCatalog.Definition.Fields)
        {
            Assert.That(f.Field<Control>(setting.Control).IsEffectivelyEnabled, Is.False);
            TextBlock caption = f.Field<TextBlock>(setting.Control + "Caption");
            Assert.That(caption.Text, Is.EqualTo(f.Translations.Translate("BackgroundFetchPlugin", setting.Control, "Caption", setting.Caption)));
            Assert.That(caption.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False, caption.Text);
            Assert.That(caption.TextLayout.Width, Is.LessThanOrEqualTo(caption.Bounds.Width + .5), caption.Text);
            Assert.That(new Rect(default, f.Dialog.ClientSize).Contains(new Rect(caption.TranslatePoint(default, f.Dialog)!.Value, caption.Bounds.Size)), Is.True);
        }
        NumericUpDown interval = f.Field<NumericUpDown>("_fetchInterval"); Assert.That(interval.Minimum, Is.Zero); Assert.That(interval.Maximum, Is.EqualTo(int.MaxValue));
        TextBox warning = f.Field<TextBox>("_warningForceWithLease");
        Assert.That(warning.IsReadOnly && warning.IsEffectivelyEnabled, Is.True, "The original pseudo setting remains selectable even in Effective.");
        Assert.That(warning.Text, Is.EqualTo(BackgroundFetchSettingsCatalog.Warning)); Assert.That(warning.Height, Is.EqualTo(500));
        Assert.That(warning.BorderThickness, Is.EqualTo(default(Thickness)));
        f.Capture("background-fetch-settings-" + language + (dark ? "-dark.png" : "-light.png"));
        Assert.That(f.Session.HasChanges, Is.False); Assert.That(f.Material(), Is.EqualTo(f.Before));
    }

    [AvaloniaTest]
    [TestCase("none")] [TestCase("bare")]
    public async Task NoRepositoryOffersOnlyGlobalAndBareKeepsOnlyDistributedUnavailable(string kind)
    {
        await using BackgroundFetchSettingsUiFixture f = await BackgroundFetchSettingsUiFixture.Create(kind);
        Assert.That(f.Field<RadioButton>("BackgroundFetchGlobalRB").IsVisible, Is.True);
        if (kind == "none")
        {
            foreach (string scope in new[] { "Effective", "Local", "Distributed" })
            { Assert.That(f.Field<RadioButton>("BackgroundFetch" + scope + "RB").IsVisible, Is.False); }
            Assert.That(f.Field<TextBox>("_gitCommand").IsEffectivelyEnabled, Is.True);
        }
        else
        {
            Assert.That(f.Field<RadioButton>("BackgroundFetchDistributedRB").IsEffectivelyEnabled, Is.False);
            f.Select(GitDistributedSettingsScope.Local); Assert.That(f.Field<TextBox>("_gitCommand").IsEffectivelyEnabled, Is.True);
        }
    }

    [AvaloniaTest]
    public async Task ScopeDraftsSurviveNavigationButCancelCannotChangeAnyProfile()
    {
        await using BackgroundFetchSettingsUiFixture f = await BackgroundFetchSettingsUiFixture.Create();
        f.Select(GitDistributedSettingsScope.Local); f.Edit("_gitCommand", "config --local test.local draft");
        f.Select(GitDistributedSettingsScope.Distributed); f.Edit("_fetchInterval", "40000");
        f.Select(GitDistributedSettingsScope.Global); f.Click("_autoRefresh");
        SettingsNavigationTestDriver.Select(f.Dialog, "General"); SettingsNavigationTestDriver.Select(f.Dialog, "PluginBackgroundFetch");
        f.Select(GitDistributedSettingsScope.Local); Assert.That(f.Field<TextBox>("_gitCommand").Text, Is.EqualTo("config --local test.local draft"));
        f.Select(GitDistributedSettingsScope.Distributed); Assert.That(f.Field<NumericUpDown>("_fetchInterval").Text, Is.EqualTo("40000"));
        f.Click("CancelDesktopSettings"); Assert.That(await f.Completion.WaitAsync(TimeSpan.FromSeconds(20)), Is.False);
        Assert.That(f.Material(), Is.EqualTo(f.Before));
    }

    [AvaloniaTest]
    public async Task UntouchedFutureBooleanIsPreservedAndPhysicalThreeStateClicksCanUnsetIt()
    {
        PluginScopedSetting setting = BackgroundFetchSettingsCatalog.Find("_autoRefresh"); string key = BackgroundFetchSettingsCatalog.Definition.Key(setting);
        await using BackgroundFetchSettingsUiFixture f = await BackgroundFetchSettingsUiFixture.Create(prepare: profile =>
            profile.Write(GitDistributedSettingsScope.Global, (key, "future-bool"), ("peer.future", "keep")));
        f.Select(GitDistributedSettingsScope.Global); CheckBox box = f.Field<CheckBox>(setting.Control);
        Assert.That(box.IsThreeState, Is.True); Assert.That(box.IsChecked, Is.Null);
        f.Click("ApplyDesktopSettings"); await f.Saved(); Assert.That(f.Material(), Is.EqualTo(f.Before));
        f.Click(setting.Control); Assert.That(box.IsChecked, Is.False);
        f.Click(setting.Control); Assert.That(box.IsChecked, Is.True);
        f.Click(setting.Control); Assert.That(box.IsChecked, Is.Null);
        f.Click("ApplyDesktopSettings"); await f.Saved(); GitXmlSettingsSnapshot xml = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global);
        Assert.That(xml.Values.ContainsKey(key), Is.False); Assert.That(xml.Values["peer.future"], Is.EqualTo("keep"));
        Assert.That(xml.Values.Keys.Any(name => name.Contains("PseudoSetting", StringComparison.Ordinal)), Is.False);
    }

    [AvaloniaTest]
    [TestCase("", null)] [TestCase("<empty string>", "")] [TestCase("  fetch --all  ", "fetch --all")]
    public async Task OriginalStringBindingKeepsUnsetDistinctFromExplicitEmptyWithoutWritingTheWarning(string typed, string? expected)
    {
        string key = BackgroundFetchSettingsCatalog.Definition.Key(BackgroundFetchSettingsCatalog.Find("_gitCommand"));
        await using BackgroundFetchSettingsUiFixture f = await BackgroundFetchSettingsUiFixture.Create(prepare: profile =>
            profile.Write(GitDistributedSettingsScope.Global, (key, "fetch origin"), ("foreign", "keep")));
        f.Select(GitDistributedSettingsScope.Global); f.Edit("_gitCommand", typed); f.Click("ApplyDesktopSettings"); await f.Saved();
        GitXmlSettingsSnapshot xml = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global);
        Assert.That(xml.Values.GetValueOrDefault(key), Is.EqualTo(expected)); Assert.That(xml.Values.ContainsKey(key), Is.EqualTo(expected is not null));
        Assert.That(xml.Values["foreign"], Is.EqualTo("keep")); Assert.That(xml.Values.Count, Is.EqualTo(expected is null ? 1 : 2));
        Assert.That(f.Session.EffectiveOptions().GetCommandArguments(), Is.EqualTo(new[] { "fetch", "--all" }));
    }

    [AvaloniaTest]
    public async Task InvalidCommandInAnotherScopeRevealsItsRealEditorBeforeAnyOtherDraftIsSaved()
    {
        await using BackgroundFetchSettingsUiFixture f = await BackgroundFetchSettingsUiFixture.Create();
        f.Select(GitDistributedSettingsScope.Global); f.Edit("_gitCommand", "fetch 'unfinished");
        f.Select(GitDistributedSettingsScope.Local); f.Edit("_fetchInterval", "40000");
        SettingsNavigationTestDriver.Select(f.Dialog, "General"); f.Click("ApplyDesktopSettings");
        await f.Until(() => !string.IsNullOrEmpty(f.Field<TextBlock>("SettingsNotice").Text));
        Assert.That(f.Dialog.Navigation.SelectedPage.Id, Is.EqualTo("PluginBackgroundFetch"));
        Assert.That(f.Field<RadioButton>("BackgroundFetchGlobalRB").IsChecked, Is.True);
        Assert.That(f.Field<TextBox>("_gitCommand").IsFocused, Is.True); Assert.That(f.Material(), Is.EqualTo(f.Before));
        f.Edit("_gitCommand", "fetch --all"); f.Click("ApplyDesktopSettings"); await f.Saved();
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Local)).Values[BackgroundFetchSettingsCatalog.Definition.Key(BackgroundFetchSettingsCatalog.Find("_fetchInterval"))], Is.EqualTo("40000"));
    }

    [AvaloniaTest]
    public async Task FirstClickTabEnterAndReadonlyWarningUseNativeFocusWithoutAcceptingSettings()
    {
        await using BackgroundFetchSettingsUiFixture f = await BackgroundFetchSettingsUiFixture.Create();
        f.Select(GitDistributedSettingsScope.Global); f.Click("_gitCommand"); Assert.That(f.Field<TextBox>("_gitCommand").IsFocused, Is.True);
        f.Edit("_gitCommand", "draft"); f.Press(PhysicalKey.Enter); Assert.That(f.Dialog.IsVisible, Is.True);
        f.Press(PhysicalKey.Tab); Assert.That(f.Field<NumericUpDown>("_fetchInterval").IsKeyboardFocusWithin, Is.True);
        f.Press(PhysicalKey.Enter); Assert.That(f.Dialog.IsVisible, Is.True);
        TextBox warning = f.Field<TextBox>("_warningForceWithLease"); f.Click(warning.Name!); f.Press(PhysicalKey.A, RawInputModifiers.Control);
        Assert.That(warning.SelectedText, Is.EqualTo(BackgroundFetchSettingsCatalog.Warning));
        f.Dialog.KeyTextInput("must not edit warning"); f.Pump(); Assert.That(warning.Text, Is.EqualTo(BackgroundFetchSettingsCatalog.Warning));
        Assert.That(f.Material(), Is.EqualTo(f.Before));
    }

    [AvaloniaTest]
    public async Task OwnerCloseDuringAcceptedPublicationWaitsForItsDrainAndDoesNotPublishASecondTime()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int publications = 0;
        await using BackgroundFetchSettingsUiFixture f = await BackgroundFetchSettingsUiFixture.Create(publish: async _ =>
        { publications++; started.TrySetResult(); await release.Task; });
        try
        {
            f.Select(GitDistributedSettingsScope.Global); f.Click("_autoRefresh"); f.Click("ApplyDesktopSettings");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values[BackgroundFetchSettingsCatalog.Definition.Key(BackgroundFetchSettingsCatalog.Find("_autoRefresh"))], Is.EqualTo("false"));
            f.Owner.Close(); f.Pump(); Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Completion.IsCompleted, Is.False);
        }
        finally { release.TrySetResult(); }
        await f.Until(() => !f.Dialog.IsVisible); Assert.That(await f.Completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(publications, Is.EqualTo(1)); Assert.That(f.Session.HasChanges, Is.False);
    }
}

internal sealed class BackgroundFetchSettingsUiFixture : IAsyncDisposable
{
    internal DetailedSettingsTestScope Profile { get; }
    internal BackgroundFetchSettingsSession Session { get; private set; } = null!;
    internal DesktopSettingsDialog Dialog { get; private set; } = null!;
    internal BackgroundFetchSettingsPage Page => Dialog.GetVisualDescendants().OfType<BackgroundFetchSettingsPage>().Single();
    internal Window Owner { get; } = new() { Width = 1080, Height = 760 };
    internal Task<bool> Completion { get; private set; } = null!;
    internal TranslationCatalog Translations { get; private set; } = null!;
    internal Dictionary<string, byte[]> Before { get; private set; } = [];
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture, _uiCulture = CultureInfo.CurrentUICulture;
    private BackgroundFetchSettingsUiFixture(DetailedSettingsTestScope profile) => Profile = profile;
    internal static async Task<BackgroundFetchSettingsUiFixture> Create(string kind = "worktree", string language = "en", bool dark = false,
        Action<DetailedSettingsTestScope>? prepare = null,
        Func<PluginScopedSettingsSaveReceipt, Task>? publish = null)
    {
        BackgroundFetchSettingsUiFixture f = new(await DetailedSettingsTestScope.CreateAsync(kind));
        try
        {
            prepare?.Invoke(f.Profile); f.Before = f.Material();
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            f.Translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
            DistributedSettingsWorkspace workspace = new(f.Profile.Repository);
            f.Session = new(f.Profile.Repository, workspace: workspace); await f.Session.InitializeAsync(default);
            f.Dialog = new(f.Profile.Desktop.Load(), f.Translations, f.Profile.Repository is not null,
                detailedSettings: new(f.Profile.Repository, f.Profile.Desktop.Load(), workspace), backgroundFetchSettings: f.Session, publishBackgroundFetchSettings: publish)
                { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            f.Owner.Show(); f.Completion = f.Dialog.ShowDialog<bool>(f.Owner);
            await f.Until(() => f.Dialog.BackgroundFetchSettingsSession.Loaded && f.Field<Button>("ApplyDesktopSettings").IsEnabled);
            SettingsNavigationTestDriver.Select(f.Dialog, "PluginBackgroundFetch"); f.Pump(); return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    internal Dictionary<string, byte[]> Material() => Profile.Material();
    internal T Field<T>(string name) where T : Control => Dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    internal void Select(GitDistributedSettingsScope scope) => Click("BackgroundFetch" + scope + "RB");
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

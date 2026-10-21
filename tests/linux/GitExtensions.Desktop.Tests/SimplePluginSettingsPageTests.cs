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
public sealed class SimplePluginSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task FourGeneratedPagesExposeOnlyTheOriginalNineFieldsAndTheirRealXlfCaptions(string language, bool dark)
    {
        await using SimplePluginSettingsUiFixture f = await SimplePluginSettingsUiFixture.Create(language, dark);
        Dictionary<string, byte[]> before = f.Profile.Material();
        foreach (SimplePluginSettingsEntry entry in SimplePluginSettingsCatalog.All)
        {
            f.Select(entry, GitDistributedSettingsScope.Effective); Grid table = f.Field<Grid>(entry.Definition.ControlPrefix + "SettingsTable");
            Assert.That(table.ColumnDefinitions, Has.Count.EqualTo(3)); Assert.That(table.RowDefinitions, Has.Count.EqualTo(entry.Definition.Fields.Count));
            TreeViewItem node = f.Dialog.GetVisualDescendants().OfType<TreeViewItem>().Single(item => item.Name == "Settings" + entry.PageId + "Node");
            Assert.That(((Control)node.Header!).GetVisualDescendants().OfType<Image>().Single().Source, Is.Not.Null);
            foreach (PluginScopedSetting field in entry.Definition.Fields)
            {
                Control editor = f.Field<Control>(field.Control); Assert.That(editor.IsEffectivelyEnabled, Is.False);
                Assert.That(editor, field.Kind == PluginScopedSettingKind.Boolean ? Is.InstanceOf<CheckBox>() : Is.InstanceOf<TextBox>());
                TextBlock label = f.Field<TextBlock>(field.Control + "Caption");
                Assert.That(label.Text, Is.EqualTo(f.Translations.Translate(entry.Definition.Category, field.Control, "Caption", field.Caption)));
                Assert.That(label.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False, label.Text);
                Assert.That(label.TextLayout.Width, Is.LessThanOrEqualTo(label.Bounds.Width + .5), label.Text);
                Assert.That(new Rect(default, f.Dialog.ClientSize).Contains(new Rect(label.TranslatePoint(default, f.Dialog)!.Value, label.Bounds.Size)), Is.True, label.Text);
            }
            f.Capture("simple-plugin-" + entry.Definition.ControlPrefix + "-" + language + (dark ? "-dark.png" : "-light.png"));
        }
        Assert.That(f.Profile.Material(), Is.EqualTo(before)); Assert.That(f.Dialog.SimplePluginSettingsSessions.All(session => !session.HasChanges), Is.True);
    }

    [AvaloniaTest]
    public async Task FrenchSingleEditsUseTheOriginalTextBindingInvalidToUnsetAndKeepUneditedFutureValues()
    {
        CultureInfo culture = CultureInfo.CurrentCulture, uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
            SimplePluginSettingsEntry entry = SimplePluginSettingsCatalog.FindLargeFiles; PluginScopedSetting field = entry.Definition.Fields.Single();
            await using SimplePluginSettingsUiFixture f = await SimplePluginSettingsUiFixture.Create("fr", prepare: profile =>
                profile.Write(GitDistributedSettingsScope.Global, (entry.Definition.Key(field), "future-number"), ("peer.future", "keep")));
            byte[] before = File.ReadAllBytes(f.Profile.Path(GitDistributedSettingsScope.Global)); f.Select(entry, GitDistributedSettingsScope.Global);
            Assert.That(f.Field<TextBox>(field.Control).Text, Is.EqualTo("future-number"));
            f.Click("ApplyDesktopSettings"); await f.Saved(); Assert.That(File.ReadAllBytes(f.Profile.Path(GitDistributedSettingsScope.Global)), Is.EqualTo(before));
            f.Edit(field.Control, "1,5"); f.Click("ApplyDesktopSettings"); await f.Saved();
            Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values[entry.Definition.Key(field)], Is.EqualTo("1,5"));
            Assert.That(f.Session(entry).InvocationValues()["minimumMegabytes"], Is.EqualTo("1.5"));
            f.Edit(field.Control, "invalid-new-number"); Assert.That((f.Field<TextBox>(field.Control).Background as Avalonia.Media.ISolidColorBrush)?.Color, Is.EqualTo(Avalonia.Media.Colors.Red));
            f.Click("ApplyDesktopSettings"); await f.Saved(); GitXmlSettingsSnapshot xml = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global);
            Assert.That(xml.Values.ContainsKey(entry.Definition.Key(field)), Is.False); Assert.That(xml.Values["peer.future"], Is.EqualTo("keep"));
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = uiCulture; }
    }

    [AvaloniaTest]
    public async Task OneApplySharesItsXmlWorkspaceAcrossAllFourPagesAndCancelDiscardsOnlyLaterDrafts()
    {
        await using SimplePluginSettingsUiFixture f = await SimplePluginSettingsUiFixture.Create();
        foreach (SimplePluginSettingsEntry entry in SimplePluginSettingsCatalog.All)
        {
            f.Select(entry, GitDistributedSettingsScope.Global);
            foreach (PluginScopedSetting field in entry.Definition.Fields)
            {
                if (field.Kind == PluginScopedSettingKind.Boolean)
                { CheckBox box = f.Field<CheckBox>(field.Control); for (int i = 0; i < 3 && box.IsChecked != true; i++) { f.Click(field.Control); } Assert.That(box.IsChecked, Is.True); }
                else { f.Edit(field.Control, field.Kind == PluginScopedSettingKind.Single ? "1.234567" : "literal-" + field.RuntimeKey); }
            }
        }
        f.Click("ApplyDesktopSettings"); await f.Saved(); GitXmlSettingsSnapshot accepted = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global);
        foreach (SimplePluginSettingsEntry entry in SimplePluginSettingsCatalog.All)
        { foreach (PluginScopedSetting field in entry.Definition.Fields) { Assert.That(accepted.Values.ContainsKey(entry.Definition.Key(field)), Is.True, field.Control); } }
        byte[] xml = File.ReadAllBytes(accepted.Path);
        f.Select(SimplePluginSettingsCatalog.Gource, GitDistributedSettingsScope.Global); f.Edit("_gourcePath", "cancelled-draft");
        f.Click("CancelDesktopSettings"); Assert.That(await f.Completion.WaitAsync(TimeSpan.FromSeconds(20)), Is.True);
        Assert.That(File.ReadAllBytes(accepted.Path), Is.EqualTo(xml)); await f.Open();
        f.Select(SimplePluginSettingsCatalog.Gource, GitDistributedSettingsScope.Global); Assert.That(f.Field<TextBox>("_gourcePath").Text, Is.EqualTo("literal-executable"));
        f.Select(SimplePluginSettingsCatalog.FindLargeFiles, GitDistributedSettingsScope.Global); Assert.That(f.Field<TextBox>("_sizeLargeFile").Text, Is.EqualTo("1.234567"));
    }

    [AvaloniaTest]
    public async Task SingleFractionsAndLargeRepresentableThresholdsReachTheBusinessControlWithoutThreeDecimalOrHundredClamp()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
            foreach (string value in new[] { "1.234567", "1000" })
            {
                SimplePluginSettingsEntry entry = SimplePluginSettingsCatalog.FindLargeFiles; profile.Write(GitDistributedSettingsScope.Global, (entry.Definition.Key(entry.Definition.Fields.Single()), value));
                SimplePluginSettingsSession session = new(entry, profile.Repository); await session.InitializeAsync(default);
                FindLargeFilesDialog dialog = new(profile.Repository!, session.InvocationValues());
                try
                {
                    dialog.Show(); DetailedSettingsUiHost.Pump(dialog);
                    NumericUpDown minimum = dialog.GetVisualDescendants().OfType<NumericUpDown>().Single(item => item.Name == "FindLargeFilesMinimum");
                    Assert.That(minimum.Value, Is.EqualTo(decimal.Parse(value, CultureInfo.InvariantCulture)));
                    Assert.That(dialog.CurrentValues["minimumMegabytes"], Is.EqualTo(value)); Assert.That(minimum.Text, Is.EqualTo(value));
                }
                finally { dialog.Close(); }
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [AvaloniaTest]
    public async Task FirstClickTabAndEnterUseNativeControlsWithoutImplicitApplyOrCompilation()
    {
        await using SimplePluginSettingsUiFixture f = await SimplePluginSettingsUiFixture.Create();
        Dictionary<string, byte[]> before = f.Profile.Material(); f.Select(SimplePluginSettingsCatalog.AutoCompile, GitDistributedSettingsScope.Global);
        f.Click("_msBuildPath"); Assert.That(f.Field<TextBox>("_msBuildPath").IsFocused, Is.True); f.Edit("_msBuildPath", "unsaved-executable");
        f.Press(PhysicalKey.Tab); Assert.That(f.Field<TextBox>("_msBuildArguments").IsFocused, Is.True);
        f.Press(PhysicalKey.Enter); Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Profile.Material(), Is.EqualTo(before));
        Assert.That(f.Dialog.OwnedWindows, Is.Empty);
    }
}

internal sealed class SimplePluginSettingsUiFixture : IAsyncDisposable
{
    internal DetailedSettingsTestScope Profile { get; }
    internal DesktopSettingsDialog Dialog { get; private set; } = null!;
    internal Window Owner { get; } = new() { Width = 1080, Height = 760 };
    internal TranslationCatalog Translations { get; private set; } = null!;
    internal Task<bool> Completion { get; private set; } = null!;
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture, _uiCulture = CultureInfo.CurrentUICulture;
    private SimplePluginSettingsUiFixture(DetailedSettingsTestScope profile) => Profile = profile;
    internal static async Task<SimplePluginSettingsUiFixture> Create(string language = "en", bool dark = false,
        Action<DetailedSettingsTestScope>? prepare = null)
    {
        SimplePluginSettingsUiFixture f = new(await DetailedSettingsTestScope.CreateAsync());
        try
        {
            prepare?.Invoke(f.Profile); CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            f.Translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
            f.Owner.Show(); await f.Open(dark); return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    internal async Task Open(bool dark = false)
    {
        Dialog = new(Profile.Desktop.Load(), Translations, true,
            detailedSettings: new(Profile.Repository, Profile.Desktop.Load()))
            { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        Completion = Dialog.ShowDialog<bool>(Owner);
        await Until(() => Dialog.SimplePluginSettingsSessions.All(session => session.Loaded) && Field<Button>("ApplyDesktopSettings").IsEnabled);
    }
    internal SimplePluginSettingsSession Session(SimplePluginSettingsEntry entry) => Dialog.SimplePluginSettingsSessions.Single(session => ReferenceEquals(session.Entry, entry));
    internal T Field<T>(string name) where T : Control => Dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    internal void Select(SimplePluginSettingsEntry entry, GitDistributedSettingsScope scope)
    { SettingsNavigationTestDriver.Select(Dialog, entry.PageId); Click(entry.Definition.ControlPrefix + scope + "RB"); }
    internal void Click(string name) { Control control = Field<Control>(name); control.BringIntoView(); Pump(); PushProcessTestDriver.Pointer(control); Pump(); }
    internal void Edit(string name, string value)
    {
        TextBox text = Field<TextBox>(name); text.BringIntoView(); Pump(); PushProcessTestDriver.Pointer(text);
        Press(PhysicalKey.A, RawInputModifiers.Control); Press(PhysicalKey.Backspace);
        if (value.Length != 0) { Dialog.KeyTextInput(value); } Pump(); Assert.That(text.Text, Is.EqualTo(value));
    }
    internal void Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { Dialog.KeyPressQwerty(key, modifiers); Dialog.KeyReleaseQwerty(key, modifiers); Pump(); }
    internal void Pump() => DetailedSettingsUiHost.Pump(Dialog);
    internal async Task Until(Func<bool> condition)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(15)) { Pump(); await Task.Delay(10); }
        Pump(); Assert.That(condition(), Is.True, "The real simple-plugin Settings workflow did not settle within fifteen seconds. "
            + "SettingsNotice=" + (Dialog.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(control => control.Name == "SettingsNotice")?.Text ?? "<empty>")
            + "; Apply.IsEnabled=" + Dialog.GetVisualDescendants().OfType<Button>().FirstOrDefault(control => control.Name == "ApplyDesktopSettings")?.IsEnabled
            + "; session.HasChanges=" + string.Join(", ", Dialog.SimplePluginSettingsSessions.Select(session => session.Entry.PageId + "=" + session.HasChanges)));
    }
    internal Task Saved() => Until(() => Dialog.SimplePluginSettingsSessions.All(session => !session.HasChanges)
        && Field<Button>("ApplyDesktopSettings").IsEnabled && string.IsNullOrEmpty(Field<TextBlock>("SettingsNotice").Text));
    internal void Capture(string name)
    {
        Pump(); using var image = Dialog.CaptureRenderedFrame(); Assert.That(image, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); image!.Save(path); TestContext.AddTestAttachment(path);
    }
    public async ValueTask DisposeAsync()
    {
        try { if (Dialog is { IsVisible: true }) { Dialog.Close(); await Until(() => !Dialog.IsVisible); } Owner.Close(); }
        finally { Profile.Dispose(); CultureInfo.CurrentCulture = _culture; CultureInfo.CurrentUICulture = _uiCulture; }
    }
}

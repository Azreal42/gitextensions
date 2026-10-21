using System.Globalization;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
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
public sealed class GitHubIssueHelperSettingsTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalHelperOptionsValidateFitAndMergeOnlyOnExplicitSave(string language, bool dark)
    {
        using Profile f = new(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        Window owner = new() { Width = 900, Height = 650 }; owner.Show();
        GitHubHostingSettingsDialog dialog = new(f.Store) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        Task<bool> selected = dialog.ShowDialog<bool>(owner);
        try
        {
            Pump(dialog); dialog.Width = dialog.MinWidth; Pump(dialog);
            Assert.That(Field<CheckBox>(dialog, "GitHubIssueHelperEnabled").IsChecked, Is.True);
            NumericUpDown number = Field<NumericUpDown>(dialog, "GitHubIssueHelperMaxCount");
            Assert.That(number.Text, Is.EqualTo("10")); Assert.That(number.Minimum, Is.Zero); Assert.That(number.Maximum, Is.EqualTo(int.MaxValue));
            TranslationCatalog translations = TranslationCatalog.LoadDefault();
            string enabledCaption = translations.Translate("GitHub3Plugin", "_issueCommitMessageHelperEnabled", "Caption", "Enable commit message issue helper");
            string countCaption = translations.Translate("GitHub3Plugin", "_issueCommitMessageHelperMaxCount", "Caption", "Maximum number of issues retrieved");
            Assert.That(((TextBlock)Field<CheckBox>(dialog, "GitHubIssueHelperEnabled").Content!).Text, Is.EqualTo(enabledCaption));
            Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == countCaption), Is.True);
            if (language == "fr") { Assert.That(enabledCaption, Is.Not.EqualTo("Enable commit message issue helper")); Assert.That(countCaption, Is.Not.EqualTo("Maximum number of issues retrieved")); }
            foreach (string name in new[] { "GitHubIssueHelperEnabled", "GitHubIssueHelperMaxCount", "GitHubSaveSettings", "GitHubCancelSettings", "GitHubAuthentication" })
            {
                Control input = Field<Control>(dialog, name); Rect bounds = new(input.TranslatePoint(default, dialog)!.Value, input.Bounds.Size);
                Assert.That(input.IsEffectivelyVisible && new Rect(default, dialog.ClientSize).Contains(bounds), Is.True, name + ":" + bounds);
            }
            Assert.That(File.Exists(f.Store.FilePath), Is.False, "Reading defaults and rendering options must not create a profile.");
            foreach (string invalid in new[] { "invalid", "-1", "2147483648" })
            {
                EditNumber(dialog, invalid); PushProcessTestDriver.Pointer(Field<Button>(dialog, "GitHubSaveSettings")); Pump(dialog);
                Assert.That(dialog.IsVisible && !selected.IsCompleted, Is.True); Assert.That(File.Exists(f.Store.FilePath), Is.False);
                Assert.That(Field<TextBlock>(dialog, "GitHubHostingSettingsError").Text, Is.Not.Empty);
            }
            EditNumber(dialog, "0");
            PushProcessTestDriver.Pointer(number.GetVisualDescendants().OfType<RepeatButton>().Single(button => button.Name == "PART_IncreaseButton")); Pump(dialog);
            Assert.That(number.Value, Is.EqualTo(1));
            PushProcessTestDriver.Pointer(number.GetVisualDescendants().OfType<TextBox>().Single()); Press(dialog, PhysicalKey.ArrowDown);
            Assert.That(number.Value, Is.EqualTo(0));
            PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "GitHubIssueHelperEnabled"));
            IReadOnlyDictionary<string, string?> baseline = f.Store.Load();
            f.Store.SaveChanges(baseline, new Dictionary<string, string?> { ["future.helper-neighbour"] = "keep λ" });
            Capture(dialog, "github-issue-helper-settings-" + language + (dark ? "-dark.png" : "-light.png"));
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "GitHubSaveSettings")); Pump(owner);
            Assert.That(await selected.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            IReadOnlyDictionary<string, string?> saved = f.Store.Load();
            Assert.That(GitHubIssueHelperSettings.FromPluginSettings(saved), Is.EqualTo(new GitHubIssueHelperSettings(false, 0)));
            Assert.That(saved["future.helper-neighbour"], Is.EqualTo("keep λ"));
            Assert.That(saved[GitHubIssueHelperSettings.EnabledKey], Is.EqualTo("false"));
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task CancelOrEscapePreservesOldHelperAndUnknownProfileBytes(bool escape)
    {
        using Profile f = new(); Dictionary<string, string?> values = new()
        { [GitHubIssueHelperSettings.EnabledKey] = "false", [GitHubIssueHelperSettings.MaxCountKey] = "42", ["future"] = "literal λ" };
        f.Store.SaveChanges(f.Store.Load(), values); byte[] before = File.ReadAllBytes(f.Store.FilePath);
        Window owner = new() { Width = 900, Height = 650 }; owner.Show(); GitHubHostingSettingsDialog dialog = new(f.Store);
        Task<bool> selected = dialog.ShowDialog<bool>(owner);
        try
        {
            Pump(dialog); EditNumber(dialog, "0"); PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "GitHubIssueHelperEnabled"));
            if (escape) { Press(dialog, PhysicalKey.Escape); } else { PushProcessTestDriver.Pointer(Field<Button>(dialog, "GitHubCancelSettings")); }
            Pump(owner); Assert.That(await selected.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
            Assert.That(File.ReadAllBytes(f.Store.FilePath).AsSpan().SequenceEqual(before), Is.True, "No canceled helper draft may rewrite credentials or neighbour keys.");
            Assert.That(GitHubIssueHelperSettings.FromPluginSettings(f.Store.Load()), Is.EqualTo(new GitHubIssueHelperSettings(false, 42)));
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase(null)] [TestCase("future-int")] [TestCase("-5")] [TestCase("2147483648")]
    public async Task AnUntouchedRawCountOrMissingDefaultSurvivesANeighbourEdit(string? raw)
    {
        using Profile f = new(); Dictionary<string, string?> values = new() { ["future"] = "retained" };
        if (raw is not null) { values[GitHubIssueHelperSettings.MaxCountKey] = raw; }
        f.Store.SaveChanges(f.Store.Load(), values);
        Window owner = new() { Width = 900, Height = 650 }; owner.Show(); GitHubHostingSettingsDialog dialog = new(f.Store);
        Task<bool> selected = dialog.ShowDialog<bool>(owner);
        try
        {
            Pump(dialog); Assert.That(Field<NumericUpDown>(dialog, "GitHubIssueHelperMaxCount").Text, Is.EqualTo(raw ?? "10"));
            PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "GitHubIssueHelperEnabled"));
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "GitHubSaveSettings")); Pump(owner);
            Assert.That(await selected.WaitAsync(TimeSpan.FromSeconds(5)), Is.True); IReadOnlyDictionary<string, string?> saved = f.Store.Load();
            Assert.That(saved.ContainsKey(GitHubIssueHelperSettings.MaxCountKey), Is.EqualTo(raw is not null));
            if (raw is not null) { Assert.That(saved[GitHubIssueHelperSettings.MaxCountKey], Is.EqualTo(raw)); }
            Assert.That(saved["future"], Is.EqualTo("retained"));
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ClearingTheNativeCountUnsetsItsKeyAndRestoresTheDefaultAfterClickOrEnter(bool enter)
    {
        using Profile f = new();
        f.Store.SaveChanges(f.Store.Load(), new Dictionary<string, string?>
        { [GitHubIssueHelperSettings.MaxCountKey] = "42", ["future"] = "keep" });
        Window owner = new() { Width = 900, Height = 650 }; owner.Show(); GitHubHostingSettingsDialog dialog = new(f.Store);
        Task<bool> selected = dialog.ShowDialog<bool>(owner);
        try
        {
            Pump(dialog); NumericUpDown number = Field<NumericUpDown>(dialog, "GitHubIssueHelperMaxCount");
            PushProcessTestDriver.Pointer(number.GetVisualDescendants().OfType<TextBox>().Single());
            Press(dialog, PhysicalKey.A, RawInputModifiers.Control); Press(dialog, PhysicalKey.Backspace);
            Assert.That(number.GetVisualDescendants().OfType<TextBox>().Single().Text, Is.Null.Or.Empty);
            if (enter) { Press(dialog, PhysicalKey.Enter); }
            if (!selected.IsCompleted) { PushProcessTestDriver.Pointer(Field<Button>(dialog, "GitHubSaveSettings")); Pump(owner); }
            Assert.That(await selected.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            IReadOnlyDictionary<string, string?> saved = f.Store.Load();
            Assert.That(saved.ContainsKey(GitHubIssueHelperSettings.MaxCountKey), Is.False);
            Assert.That(GitHubIssueHelperSettings.FromPluginSettings(saved).MaxCount, Is.EqualTo(10));
            Assert.That(saved["future"], Is.EqualTo("keep"));
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [Test]
    public void DistinctHelperEditsMergeAndSameFieldConflictsNeverOverwriteTheWinner()
    {
        using Profile f = new(); Dictionary<string, string?> values = new()
        { [GitHubIssueHelperSettings.EnabledKey] = "true", [GitHubIssueHelperSettings.MaxCountKey] = "10", ["future"] = "retained" };
        f.Store.SaveChanges(f.Store.Load(), values); GitHubHostingPreferencesStore other = new(f.Root);
        IReadOnlyDictionary<string, string?> first = f.Store.Load(), second = other.Load();
        GitHubIssueHelperSettings initial = GitHubIssueHelperSettings.FromPluginSettings(first);
        f.Store.SaveChanges(first, new GitHubIssueHelperSettings(false, 10).WithChanges(first, initial));
        other.SaveChanges(second, new GitHubIssueHelperSettings(true, 7).WithChanges(second, initial));
        Assert.That(GitHubIssueHelperSettings.FromPluginSettings(f.Store.Load()), Is.EqualTo(new GitHubIssueHelperSettings(false, 7)));
        first = f.Store.Load(); second = other.Load(); initial = GitHubIssueHelperSettings.FromPluginSettings(first);
        f.Store.SaveChanges(first, new GitHubIssueHelperSettings(false, 11).WithChanges(first, initial));
        string winner = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.Store.FilePath)));
        Assert.That(() => other.SaveChanges(second, new GitHubIssueHelperSettings(false, 12).WithChanges(second, initial)), Throws.TypeOf<InvalidOperationException>());
        Assert.That(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.Store.FilePath))), Is.EqualTo(winner));
        Assert.That(f.Store.Load()["future"], Is.EqualTo("retained"));
    }

    [AvaloniaTest]
    public async Task OriginalTokenLinkRequiresAClickAndRetainsItsDescriptionScopesAndConfiguredHost()
    {
        using Profile f = new(); List<string> opened = []; Window owner = new() { Width = 900, Height = 650 }; owner.Show();
        GitHubHostingSettingsDialog dialog = new(f.Store, openUrl: url => { opened.Add(url); return Task.CompletedTask; });
        Task<bool> selected = dialog.ShowDialog<bool>(owner);
        try
        {
            Pump(dialog); Assert.That(opened, Is.Empty); Edit(dialog, "GitHubWebUrl", "https://enterprise.example.invalid");
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "GitHubCreateToken")); Pump(dialog);
            Assert.That(opened, Is.EqualTo(new[] { "https://enterprise.example.invalid/settings/tokens/new?description=Token%20for%20GitExtensions&scopes=repo,public_repo" }));
            Assert.That(File.Exists(f.Store.FilePath), Is.False); PushProcessTestDriver.Pointer(Field<Button>(dialog, "GitHubCancelSettings"));
            Assert.That(await selected.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        }
        finally { dialog.Close(); owner.Close(); }
    }

    private static T Field<T>(TopLevel root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(TopLevel root) { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Press(Window root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { root.KeyPressQwerty(key, modifiers); if (root.IsVisible) { root.KeyReleaseQwerty(key, modifiers); } Pump(root); }
    private static void Edit(Window root, string name, string text)
    { PushProcessTestDriver.Pointer(Field<TextBox>(root, name)); Press(root, PhysicalKey.A, RawInputModifiers.Control); root.KeyTextInput(text); Pump(root); Assert.That(Field<TextBox>(root, name).Text, Is.EqualTo(text)); }
    private static void EditNumber(Window root, string text)
    {
        NumericUpDown number = Field<NumericUpDown>(root, "GitHubIssueHelperMaxCount");
        TextBox editor = number.GetVisualDescendants().OfType<TextBox>().Single();
        PushProcessTestDriver.Pointer(editor); Press(root, PhysicalKey.A, RawInputModifiers.Control); root.KeyTextInput(text); Pump(root);
        Assert.That(editor.Text, Is.EqualTo(text));
        TextPresenter presenter = editor.GetVisualDescendants().OfType<TextPresenter>().Single();
        Assert.That(presenter.Bounds.Width, Is.GreaterThanOrEqualTo(presenter.TextLayout.Width), "The whole native numeric draft must fit beside its spinner arrows.");
    }
    private static void Capture(TopLevel root, string name)
    {
        using var frame = root.GetLastRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private sealed class Profile : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "github-issue-options-" + Guid.NewGuid().ToString("N"));
        internal GitHubHostingPreferencesStore Store { get; }
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        internal Profile() { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Root); Store = new(Root); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en"); }
        public void Dispose() { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); CultureInfo.CurrentUICulture = _culture; if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }
}

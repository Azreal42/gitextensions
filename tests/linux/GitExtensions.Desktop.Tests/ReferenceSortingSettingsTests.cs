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
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ReferenceSortingSettingsTests
{
    [Test]
    public void OldProfilesKeepTheirValuesAndNewSortingDefaultsRoundTripWithoutRenumberingHistory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gitextensions-sort-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            PreferencesStore store = new(directory); Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, """{"Theme":"Dark","HistoryLimit":128,"RecentRepositories":["literal [*] λ"],"PluginSettings":{"fixture":{"key":"keep"}}}""");
            DesktopPreferences old = store.Load();
            Assert.That(old.ReferenceSorting, Is.EqualTo(new GitReferenceSorting()));
            Assert.That(old.PrioritizedBranchNames, Is.EqualTo("main[^/]*|master[^/]*|release/.*"));
            Assert.That(old.PrioritizedRemoteNames, Is.EqualTo("origin|upstream"));
            DesktopPreferences changed = old with { ReferenceSorting = new(GitRefsSortBy.VersionRefName, GitRefsSortOrder.Ascending), PrioritizedBranchNames = " release/.* ; main ", PrioritizedRemoteNames = "Team/Upstream; origin" };
            store.Save(changed); DesktopPreferences loaded = store.Load();
            Assert.That(loaded.ReferenceSorting, Is.EqualTo(changed.ReferenceSorting));
            Assert.That(loaded.PrioritizedBranchNames, Is.EqualTo(changed.PrioritizedBranchNames)); Assert.That(loaded.PrioritizedRemoteNames, Is.EqualTo(changed.PrioritizedRemoteNames));
            Assert.That(loaded.Theme, Is.EqualTo("Dark")); Assert.That(loaded.HistoryLimit, Is.EqualTo(128)); Assert.That(loaded.RecentRepositories, Is.EqualTo(old.RecentRepositories)); Assert.That(loaded.PluginSettings["fixture"]["key"], Is.EqualTo("keep"));
            Assert.That(new[] { (int)GitLogSort.Topology, (int)GitLogSort.AuthorDate, (int)GitLogSort.Date, (int)GitLogSort.GitDefault }, Is.EqualTo(new[] { 0, 1, 2, 3 }));
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    [Test]
    public void NullOrFutureSortingFieldsDoNotInvalidateUnrelatedSavedPreferences()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gitextensions-sort-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            PreferencesStore store = new(directory); Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, """{"Theme":"Dark","ReferenceSorting":{"By":999,"Order":999},"PrioritizedBranchNames":null,"PrioritizedRemoteNames":null}""");
            DesktopPreferences loaded = store.Load(); Assert.That(loaded.Theme, Is.EqualTo("Dark")); Assert.That(loaded.ReferenceSorting, Is.EqualTo(new GitReferenceSorting()));
            Assert.That(loaded.PrioritizedBranchNames, Is.EqualTo(GitReferencePriorities.DefaultBranchPatterns)); Assert.That(loaded.PrioritizedRemoteNames, Is.EqualTo(GitReferencePriorities.DefaultRemotePatterns));
            File.WriteAllText(store.FilePath, """{"ReferenceSorting":null,"PrioritizedBranchNames":"","PrioritizedRemoteNames":""}""");
            loaded = store.Load(); Assert.That(loaded.ReferenceSorting, Is.EqualTo(new GitReferenceSorting())); Assert.That(loaded.PrioritizedBranchNames, Is.Empty); Assert.That(loaded.PrioritizedRemoteNames, Is.Empty);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    [AvaloniaTest]
    [TestCase(GitLogSort.GitDefault)]
    [TestCase(GitLogSort.Topology)]
    [TestCase(GitLogSort.Date)]
    public async Task SortingPageHasFiveOriginalInputsAndKeepsDirectionEditableUnderGitDefault(GitLogSort initialSort)
    {
        using Fixture f = await Fixture.CreateAsync(); Window owner = new(); owner.Show();
        DesktopSettingsDialog dialog = new(new(), TranslationCatalog.LoadDefault(), true, initialSort); Task<bool> result = dialog.ShowDialog<bool>(owner);
        try
        {
            ShowSorting(dialog);
            ComboBox revisions = Field<ComboBox>(dialog, "_NO_TRANSLATE_cmbRevisionsSortBy");
            Assert.That(revisions.Items.Cast<GitLogSort>(), Is.EqualTo(initialSort == GitLogSort.Date
                ? new[] { GitLogSort.GitDefault, GitLogSort.AuthorDate, GitLogSort.Topology, GitLogSort.Date }
                : new[] { GitLogSort.GitDefault, GitLogSort.AuthorDate, GitLogSort.Topology }));
            Assert.That(revisions.SelectedItem, Is.EqualTo(initialSort));
            ComboBox references = Field<ComboBox>(dialog, "_NO_TRANSLATE_cmbBranchesSortBy"), order = Field<ComboBox>(dialog, "_NO_TRANSLATE_cmbBranchesOrder");
            Assert.That(references.Items.Cast<GitRefsSortBy>(), Is.EqualTo(Enum.GetValues<GitRefsSortBy>()));
            Assert.That(references.SelectedItem, Is.EqualTo(GitRefsSortBy.GitDefault)); Assert.That(order.IsEffectivelyEnabled, Is.True);
            Assert.That(order.SelectedItem, Is.EqualTo(GitRefsSortOrder.Descending));
            Assert.That(Field<TextBox>(dialog, "txtPrioBranchNames").Text, Is.EqualTo(GitReferencePriorities.DefaultBranchPatterns));
            Assert.That(Field<TextBox>(dialog, "txtPrioRemoteNames").Text, Is.EqualTo(GitReferencePriorities.DefaultRemotePatterns));
            Press(dialog, PhysicalKey.Escape); Assert.That(await result, Is.False);
        }
        finally { if (dialog.IsVisible) { dialog.Close(false); } owner.Close(); }
    }

    [AvaloniaTest]
    public async Task InvalidPriorityIsAtomicAndTheFirstCorrectedOkReturnsBothSortingResults()
    {
        using Fixture f = await Fixture.CreateAsync(); DesktopPreferences initial = new() { Theme = "Dark", PrioritizedRemoteNames = "upstream;origin" };
        PreferencesStore store = new(); store.Save(initial); byte[] bytes = File.ReadAllBytes(store.FilePath);
        Window owner = new(); owner.Show(); DesktopSettingsDialog dialog = new(initial, TranslationCatalog.LoadDefault(), true, GitLogSort.AuthorDate); Task<bool> result = dialog.ShowDialog<bool>(owner);
        try
        {
            ShowSorting(dialog); TextBox branch = Field<TextBox>(dialog, "txtPrioBranchNames"); Edit(dialog, branch, "[");
            Field<ComboBox>(dialog, "_NO_TRANSLATE_cmbBranchesSortBy").SelectedItem = GitRefsSortBy.VersionRefName;
            Field<ComboBox>(dialog, "_NO_TRANSLATE_cmbBranchesOrder").SelectedItem = GitRefsSortOrder.Ascending;
            Field<ComboBox>(dialog, "_NO_TRANSLATE_cmbRevisionsSortBy").SelectedItem = GitLogSort.Topology;
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings")); Pump(dialog);
            Assert.That(result.IsCompleted, Is.False); Assert.That(dialog.IsVisible && branch.IsKeyboardFocusWithin, Is.True);
            Assert.That(Field<TextBlock>(dialog, "SettingsNotice").Text, Does.Contain("Invalid reference priority"));
            Assert.That(dialog.Result, Is.SameAs(initial)); Assert.That(dialog.HistorySortResult, Is.EqualTo(GitLogSort.AuthorDate)); Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(bytes));
            Edit(dialog, branch, " release/.* ; main "); PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings"));
            Assert.That(await result, Is.True); Assert.That(dialog.Result.ReferenceSorting, Is.EqualTo(new GitReferenceSorting(GitRefsSortBy.VersionRefName, GitRefsSortOrder.Ascending)));
            Assert.That(dialog.Result.PrioritizedBranchNames, Is.EqualTo(" release/.* ; main ")); Assert.That(dialog.Result.PrioritizedRemoteNames, Is.EqualTo(initial.PrioritizedRemoteNames));
            Assert.That(dialog.HistorySortResult, Is.EqualTo(GitLogSort.Topology)); Assert.That(dialog.Result.Theme, Is.EqualTo(initial.Theme));
            Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(bytes), "The shared window returns changes; its owner persists only after accepting.");
        }
        finally { if (dialog.IsVisible) { dialog.Close(false); } owner.Close(); }
    }

    [AvaloniaTest]
    public async Task CancelLeavesReferenceAndRevisionSortingAndSavedProfileUntouched()
    {
        using Fixture f = await Fixture.CreateAsync(); DesktopPreferences initial = new() { ReferenceSorting = new(GitRefsSortBy.Upstream, GitRefsSortOrder.Ascending) }; PreferencesStore store = new(); store.Save(initial); byte[] bytes = File.ReadAllBytes(store.FilePath);
        Window owner = new(); owner.Show(); DesktopSettingsDialog dialog = new(initial, TranslationCatalog.LoadDefault(), false, GitLogSort.GitDefault); Task<bool> result = dialog.ShowDialog<bool>(owner);
        try
        {
            ShowSorting(dialog); Edit(dialog, Field<TextBox>(dialog, "txtPrioRemoteNames"), "changed;origin"); Field<ComboBox>(dialog, "_NO_TRANSLATE_cmbBranchesSortBy").SelectedItem = GitRefsSortBy.RefName;
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await result, Is.False);
            Assert.That(dialog.Result, Is.SameAs(initial)); Assert.That(dialog.HistorySortResult, Is.EqualTo(GitLogSort.GitDefault)); Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(bytes));
        }
        finally { if (dialog.IsVisible) { dialog.Close(false); } owner.Close(); }
    }

    [AvaloniaTest]
    public async Task FiveRowsRemainReadableInLightDarkAndFrench()
    {
        using Fixture f = await Fixture.CreateAsync(); CultureInfo old = CultureInfo.CurrentUICulture;
        try
        {
            foreach (string culture in new[] { "en", "fr-FR" })
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture); Window owner = new(); owner.Show(); DesktopSettingsDialog dialog = new(new(), TranslationCatalog.LoadDefault(), false) { RequestedThemeVariant = theme }; Task<bool> result = dialog.ShowDialog<bool>(owner);
                try
                {
                    ShowSorting(dialog);
                    Control[] editors = [Field<ComboBox>(dialog, "_NO_TRANSLATE_cmbRevisionsSortBy"), Field<ComboBox>(dialog, "_NO_TRANSLATE_cmbBranchesSortBy"), Field<ComboBox>(dialog, "_NO_TRANSLATE_cmbBranchesOrder"), Field<TextBox>(dialog, "txtPrioBranchNames"), Field<TextBox>(dialog, "txtPrioRemoteNames")];
                    Rect[] bounds = editors.Select(editor => new Rect(editor.TranslatePoint(default, dialog)!.Value, editor.Bounds.Size)).ToArray();
                    using var bitmap = dialog.CaptureRenderedFrame(); Assert.That(bitmap, Is.Not.Null); string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"reference-sorting-settings-{culture}-{theme}.png"); bitmap!.Save(path); TestContext.AddTestAttachment(path);
                    TestContext.WriteLine($"Sorting layout {culture}/{theme}: client={dialog.ClientSize}; " + string.Join("; ", editors.Zip(bounds, (editor, rect) => $"{editor.Name}={rect}, min={editor.MinWidth}×{editor.MinHeight}, alignment={editor.HorizontalAlignment}/{editor.VerticalAlignment}")));
                    Assert.That(editors.All(editor => editor.IsEffectivelyVisible && editor.IsEffectivelyEnabled), Is.True);
                    Assert.That(bounds.All(rect => new Rect(default, dialog.ClientSize).Contains(rect) && rect.Width >= 250 && rect.Height >= 23), Is.True);
                    Assert.That(bounds.Select(rect => rect.Left).Distinct().Count(), Is.EqualTo(1));
                    Assert.That(bounds.Select(rect => rect.Right).Distinct().Count(), Is.EqualTo(1), "All five Windows editors fill the same settings column.");
                    Assert.That(bounds.Zip(bounds.Skip(1), (first, second) => second.Top >= first.Bottom), Is.All.True);
                    Press(dialog, PhysicalKey.Escape); Assert.That(await result, Is.False);
                }
                finally { if (dialog.IsVisible) { dialog.Close(false); } owner.Close(); }
            }
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task IconOnlySortingHintsLeaveTheFirstEditorClickReadyAfterRealComboChoicesAndReopening(bool dark)
    {
        using Fixture f = await Fixture.CreateAsync();
        ThemeVariant theme = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Window owner = new() { RequestedThemeVariant = theme }; owner.Show();
        DesktopPreferences initial = new();
        try
        {
            for (int opening = 1; opening <= 2; opening++)
            {
                DesktopSettingsDialog dialog = new(initial, TranslationCatalog.LoadDefault(), false) { RequestedThemeVariant = theme };
                Task<bool> result = dialog.ShowDialog<bool>(owner);
                try
                {
                    ShowSorting(dialog);
                    ComboBox references = Field<ComboBox>(dialog, "_NO_TRANSLATE_cmbBranchesSortBy");
                    ComboBox order = Field<ComboBox>(dialog, "_NO_TRANSLATE_cmbBranchesOrder");
                    Choose(references, GitRefsSortBy.VersionRefName);
                    Choose(order, GitRefsSortOrder.Ascending);
                    Grid sorting = Field<Grid>(dialog, "SortingSettingsPage");
                    Control[] editors = sorting.Children.Where(control => Grid.GetColumn(control) == 1).ToArray();
                    Control[] icons = sorting.Children.Where(control => Grid.GetColumn(control) == 2).ToArray();
                    Assert.That(editors, Has.Length.EqualTo(5));
                    Assert.That(editors.Select(ToolTip.GetTip), Is.All.Null,
                        "Windows keeps the three sorting hints on the information icons, so an editor cannot cover the next input with its hint.");
                    Assert.That(icons, Has.Length.EqualTo(3));
                    Assert.That(icons.All(control => ToolTip.GetTip(control) is string text && text.Length > 0), Is.True);
                    TextBox remotes = Field<TextBox>(dialog, "txtPrioRemoteNames");
                    PushProcessTestDriver.Pointer(remotes); Pump(dialog);
                    Assert.That(remotes.IsFocused, Is.True, "The first center click after the native combo choices must focus the real editor.");
                    Press(dialog, PhysicalKey.A, RawInputModifiers.Control);
                    Assert.That(remotes.SelectedText, Is.EqualTo(initial.PrioritizedRemoteNames));
                    dialog.KeyTextInput("upstream;origin"); Pump(dialog);
                    Assert.That(remotes.Text, Is.EqualTo("upstream;origin"));
                    Assert.That(remotes.IsFocused, Is.True);
                    Assert.That(references.SelectedItem, Is.EqualTo(GitRefsSortBy.VersionRefName));
                    Assert.That(order.SelectedItem, Is.EqualTo(GitRefsSortOrder.Ascending));
                    using var frame = dialog.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
                    string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"reference-sorting-first-editor-click-{theme}-{opening}.png");
                    frame!.Save(path); TestContext.AddTestAttachment(path);
                    Press(dialog, PhysicalKey.Escape); Assert.That(await result, Is.False);
                    Assert.That(dialog.Result, Is.SameAs(initial));
                }
                finally { if (dialog.IsVisible) { dialog.Close(false); } }
            }
        }
        finally { owner.Close(); }

        void Choose(ComboBox choice, object value)
        {
            PushProcessTestDriver.Pointer(choice); Pump((TopLevel)TopLevel.GetTopLevel(choice)!);
            Assert.That(choice.IsDropDownOpen, Is.True);
            Control item = choice.ContainerFromItem(value)!;
            Assert.That(item, Is.Not.Null, "The actual combo popup must contain the chosen value.");
            PushProcessTestDriver.Pointer(item);
            Pump((TopLevel)TopLevel.GetTopLevel(choice)!);
            Assert.That(choice.SelectedItem, Is.EqualTo(value));
            Assert.That(choice.IsDropDownOpen, Is.False);
        }
    }

    private static void ShowSorting(DesktopSettingsDialog dialog) { Pump(dialog); SettingsNavigationTestDriver.Select(dialog, "Sorting"); Pump(dialog); }
    private static T Field<T>(TopLevel root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Edit(Window window, TextBox input, string text) { PushProcessTestDriver.Pointer(input); Press(window, PhysicalKey.A, RawInputModifiers.Control); window.KeyTextInput(text); Pump(window); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}

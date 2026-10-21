using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using BrowserFixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DeleteUnusedBranchesSettingsIntegrationTests
{
    [AvaloniaTest]
    public async Task TheRealSettingsTreePersistsDefaultsUsedByTheExistingPreviewAndItsSettingsButtonReturnsToTheSamePage()
    {
        using BrowserFixture f = await BrowserFixture.CreateAsync("clean\n", "clean\n");
        GitResult tree = await f.Git.RunAsync(f.Repository, ["rev-parse", "HEAD^{tree}"]); Assert.That(tree.Success, Is.True);
        GitResult orphan = await f.Git.RunAsync(f.Repository, ["commit-tree", tree.StandardOutput.Trim(), "-m", "Unmerged plugin settings candidate"]); Assert.That(orphan.Success, Is.True);
        GitResult branch = await f.Git.RunAsync(f.Repository, ["update-ref", "refs/heads/feature/one", orphan.StandardOutput.Trim()]); Assert.That(branch.Success, Is.True);
        branch = await f.Git.RunAsync(f.Repository, ["branch", "feature/two"]); Assert.That(branch.Success, Is.True);
        PreferencesStore preferencesStore = new();
        Dictionary<string, Dictionary<string, string?>> legacyProfiles = new()
        {
            ["dc3ca904-b9a5-4fe8-bf63-5b8ee9c2ddac"] = new() { ["olderThanDays"] = "65", ["pattern"] = "^legacy/", ["future-plugin-field"] = "keep λ & <literal>" },
            ["00000000-0000-0000-0000-000000000123"] = new() { ["future-provider"] = "untouched" }
        };
        preferencesStore.Save(preferencesStore.Load() with { PluginSettings = legacyProfiles });
        f.Window.KeyPressQwerty(PhysicalKey.Comma, RawInputModifiers.Control); f.Window.KeyReleaseQwerty(PhysicalKey.Comma, RawInputModifiers.Control);
        await Until(f.Window, () => f.Window.OwnedWindows.OfType<DesktopSettingsDialog>().Any(window => window.IsVisible));
        DesktopSettingsDialog settings = f.Window.OwnedWindows.OfType<DesktopSettingsDialog>().Single(window => window.IsVisible);
        await Until(settings, () => settings.DeleteUnusedBranchesSettingsSession.Loaded && Field<Button>(settings, "ApplyDesktopSettings").IsEnabled);
        SettingsNavigationTestDriver.Select(settings, "PluginDeleteUnusedBranches");
        Pointer(Field<RadioButton>(settings, "DeleteUnusedBranchesGlobalRB"));
        Edit(settings, "_daysOlderThan", "0"); Edit(settings, "_mergedInBranch", "HEAD"); Edit(settings, "_remoteName", "origin");
        Edit(settings, "_regexFilter", "^FEATURE/ONE$");
        foreach ((string name, bool value) in new[]
        {
            ("_deleteRemoteBranchesFromFlag", false), ("_useRegexToFilterBranchesFlag", true),
            ("_regexCaseInsensitiveFlag", true), ("_regexInvertedFlag", false), ("_includeUnmergedBranchesFlag", true)
        }) { Choose(settings, name, value); }
        Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await Until(settings, () => !settings.DeleteUnusedBranchesSettingsSession.HasChanges && Field<Button>(settings, "ApplyDesktopSettings").IsEnabled
            && string.IsNullOrEmpty(Field<TextBlock>(settings, "SettingsNotice").Text));
        GitXmlSettingsSnapshot saved = await GitXmlSettingsStore.LoadAsync(DetailedRepositorySettings.GlobalPath);
        foreach (DeleteUnusedBranchesSetting setting in DeleteUnusedBranchesSettingsCatalog.Fields)
        { Assert.That(saved.Values.ContainsKey(setting.Key), Is.True, "Each explicitly edited original setting must reach the shared XML store: " + setting.Control); }
        Pointer(Field<Button>(settings, "CancelDesktopSettings")); await Until(f.Window, () => !settings.IsVisible);

        DeleteUnusedBranchesDialog preview = await OpenPlugin(f.Window);
        Assert.That(preview.VisibleBranches.Select(item => item.Reference.ShortName), Is.EqualTo(new[] { "feature/one" }),
            "Actual include-unmerged, enable-regex and case flags from Settings must affect the existing engine, not just reappear in controls.");
        Assert.That(preview.VisibleBranches.Single().Reference.Hash, Is.EqualTo(orphan.StandardOutput.Trim()));
        Assert.That(Field<NumericUpDown>(preview, "olderThanDays").Value, Is.EqualTo(0));
        Assert.That(Field<CheckBox>(preview, "useRegexFilter").IsChecked, Is.True);
        Assert.That(Field<CheckBox>(preview, "includeUnmergedBranches").IsChecked, Is.True);
        byte[] defaults = File.ReadAllBytes(saved.Path);
        Edit(preview, "regexFilter", "^feature/two$"); Pointer(Field<Button>(preview, "RefreshBtn"));
        await Until(preview, () => !preview.IsBusy && preview.LastPreview is not null);
        Assert.That(preview.VisibleBranches.Select(item => item.Reference.ShortName), Is.EqualTo(new[] { "feature/two" }));
        Assert.That(File.ReadAllBytes(saved.Path), Is.EqualTo(defaults), "Search edits this invocation only; the real Settings page owns persisted defaults.");
        Assert.That(Field<Button>(preview, "buttonSettings").IsVisible, Is.True);
        Pointer(Field<Button>(preview, "buttonSettings"));
        await Until(f.Window, () => !preview.IsVisible && f.Window.OwnedWindows.OfType<DesktopSettingsDialog>().Any(window => window.IsVisible));
        Assert.That(preview.SettingsRequested, Is.True);
        DesktopSettingsDialog reopened = f.Window.OwnedWindows.OfType<DesktopSettingsDialog>().Single(window => window.IsVisible);
        await Until(reopened, () => reopened.DeleteUnusedBranchesSettingsSession.Loaded && Field<Button>(reopened, "ApplyDesktopSettings").IsEnabled);
        Assert.That(reopened.Navigation.SelectedPage.Id, Is.EqualTo("PluginDeleteUnusedBranches"));
        Pointer(Field<RadioButton>(reopened, "DeleteUnusedBranchesGlobalRB"));
        Assert.That(Field<TextBox>(reopened, "_regexFilter").Text, Is.EqualTo("^FEATURE/ONE$"));
        Pointer(Field<Button>(reopened, "CancelDesktopSettings")); await Until(f.Window, () => !reopened.IsVisible);
        Assert.That(File.ReadAllBytes(saved.Path), Is.EqualTo(defaults));
        Assert.That(preferencesStore.Load().PluginSettings, Is.EqualTo(legacyProfiles),
            "XML Apply and preview search must preserve legacy plugin values and neighbouring provider profiles without competing JSON updates.");
        Assert.That(f.Window.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task ClosingThePluginWithoutTheExplicitSettingsGestureNeverOpensSettingsOrWritesDefaults()
    {
        using BrowserFixture f = await BrowserFixture.CreateAsync("clean\n", "clean\n");
        string global = DetailedRepositorySettings.GlobalPath;
        byte[]? before = File.Exists(global) ? File.ReadAllBytes(global) : null;
        DeleteUnusedBranchesDialog preview = await OpenPlugin(f.Window); Edit(preview, "regexFilter", "unsaved-draft");
        Pointer(Field<Button>(preview, "Cancel")); await Until(f.Window, () => !preview.IsVisible && !f.Window.IsBusy);
        Assert.That(preview.SettingsRequested, Is.False);
        Assert.That(f.Window.OwnedWindows.OfType<DesktopSettingsDialog>().Any(window => window.IsVisible), Is.False);
        Assert.That(File.Exists(global), Is.EqualTo(before is not null));
        if (before is not null) { Assert.That(File.ReadAllBytes(global), Is.EqualTo(before)); }
    }

    private static async Task<DeleteUnusedBranchesDialog> OpenPlugin(MainWindow owner)
    {
        MenuItem plugins = owner.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Plugins");
        Pointer(plugins);
        MenuItem item = plugins.Items.OfType<MenuItem>().Single(item => item.Header as string == "Delete obsolete branches");
        Pointer(item);
        await Until(owner, () => owner.OwnedWindows.OfType<DeleteUnusedBranchesDialog>().Any(window => window.IsVisible));
        DeleteUnusedBranchesDialog dialog = owner.OwnedWindows.OfType<DeleteUnusedBranchesDialog>().Single(window => window.IsVisible);
        await Until(dialog, () => !dialog.IsBusy && dialog.LastPreview is not null); return dialog;
    }
    private static void Choose(Window owner, string name, bool value)
    {
        CheckBox box = Field<CheckBox>(owner, name);
        for (int attempt = 0; box.IsChecked != value && attempt < 3; attempt++) { Pointer(box); }
        Assert.That(box.IsChecked, Is.EqualTo(value));
    }
    private static void Edit(Window owner, string name, string value)
    {
        Control control = Field<Control>(owner, name); control.BringIntoView(); Pump(owner);
        TextBox text = control as TextBox ?? control.GetVisualDescendants().OfType<TextBox>().Single(); Pointer(text);
        Press(owner, PhysicalKey.A, RawInputModifiers.Control); Press(owner, PhysicalKey.Backspace);
        if (value.Length > 0) { owner.KeyTextInput(value); } Pump(owner); Assert.That(text.Text, Is.EqualTo(value));
    }
    private static void Press(Window owner, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { owner.KeyPressQwerty(key, modifiers); owner.KeyReleaseQwerty(key, modifiers); Pump(owner); }
    private static T Field<T>(Window owner, string name) where T : Control
        => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pointer(Control control) { control.BringIntoView(); PushProcessTestDriver.Pointer(control); }
    private static void Pump(Window owner)
    { Dispatcher.UIThread.RunJobs(); if (owner.IsVisible) { owner.UpdateLayout(); } AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Window owner, Func<bool> condition)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(15)) { Pump(owner); await Task.Delay(10); }
        Pump(owner); Assert.That(condition(), Is.True, "The real plugin/Main/Settings workflow did not settle within fifteen seconds.");
    }
}

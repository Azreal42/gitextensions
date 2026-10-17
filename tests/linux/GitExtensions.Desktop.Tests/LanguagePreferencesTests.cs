using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
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
using GitExtensions.Linux.Plugins;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class LanguagePreferencesTests
{
    [Test]
    public void SavedLanguageIsIndependentOfFormattingAndSurvivesOtherPreferences()
    {
        using Scope scope = new(); CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        scope.Store.Save(new() { Language = "French", Theme = "Dark", HistoryLimit = 123, RecentRepositories = ["literal repo"] });
        DesktopLanguage.ApplyPreferredCulture();
        Assert.That(CultureInfo.CurrentUICulture.Name, Is.EqualTo("fr"));
        Assert.That(CultureInfo.CurrentCulture.Name, Is.EqualTo("de-DE"));
        Assert.That(GitRunner.AskpassUiCulture, Is.EqualTo("fr"));
        Assert.That(TranslationCatalog.LoadDefault().Translate("Cancel"), Is.EqualTo("Annuler"));
        scope.Store.Save(scope.Store.Load() with { Theme = "Light" });
        Assert.That(scope.Store.Load().Language, Is.EqualTo("French")); Assert.That(scope.Store.Load().HistoryLimit, Is.EqualTo(123));
        Assert.That(scope.Store.Load().RecentRepositories, Is.EqualTo(new[] { "literal repo" }));
    }

    [TestCase("de-DE", "de-DE")]
    [TestCase("not$$a_culture", "fr")]
    public void ExplicitChildOverrideWinsAndInvalidOverrideFallsBackToTheSavedLanguage(string value, string expected)
    {
        using Scope scope = new(); scope.Store.Save(new() { Language = "French" });
        Environment.SetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE", value);
        AskpassApplication.ApplyUiCulture();
        Assert.That(CultureInfo.CurrentUICulture.Name, Is.EqualTo(expected));
        Assert.That(scope.Store.Load().Language, Is.EqualTo("French"));
    }

    [Test]
    public void RemovedTranslationUsesEnglishWithoutSilentlyRewritingItsSavedIdentity()
    {
        using Scope scope = new(); scope.Store.Save(new() { Language = "Removed catalogue" });
        byte[] before = File.ReadAllBytes(scope.Store.FilePath); DesktopLanguage.ApplyPreferredCulture();
        Assert.That(CultureInfo.CurrentUICulture.Name, Is.EqualTo("en"));
        Assert.That(File.ReadAllBytes(scope.Store.FilePath), Is.EqualTo(before));
    }

    [Test]
    public void VersionAndHelpDoNotCreateAProfileOrOpenAChooser()
    {
        using Scope scope = new(); TextWriter old = Console.Out; using StringWriter output = new();
        try
        {
            Console.SetOut(output);
            Assert.That(GitExtensions.Linux.Desktop.Program.Main(["--version"]), Is.Zero);
            Assert.That(GitExtensions.Linux.Desktop.Program.Main(["--help"]), Is.Zero);
        }
        finally { Console.SetOut(old); }
        Assert.That(output.ToString(), Does.Contain("Git Extensions").And.Contain("Usage:"));
        Assert.That(File.Exists(scope.Store.FilePath), Is.False);
    }

    [Test]
    public void ExistingProfilesWithoutLanguageNeedTheInteractiveChoiceButSmokeDoesNot()
    {
        using Scope scope = new(); scope.Store.Save(new() { Theme = "Dark" });
        Assert.That(scope.Store.Load().Language, Is.Null);
        Assert.That(DesktopLanguage.NeedsChoice(new(DesktopStartupMode.Browse), scope.Store.Load()), Is.True);
        Assert.That(DesktopLanguage.NeedsChoice(new(DesktopStartupMode.Cleanup), scope.Store.Load()), Is.True);
        Assert.That(DesktopLanguage.NeedsChoice(new(DesktopStartupMode.Browse, SmokeTest: true), scope.Store.Load()), Is.False);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public void BootstrapBuildsTheRealConsumerOnlyAfterOnePointerActivationAndPersistsItsChoice(bool dark)
    {
        using Scope scope = new(); scope.Store.Save(new() { Theme = dark ? "Dark" : "Light", HistoryLimit = 777 });
        Window? opened = null; int created = 0;
        Window first = DesktopLanguage.CreateInitialWindow(DesktopStartupRequest.Browse, window => { opened = window; window.Show(); }, scope.Store,
            createWindow: _ => { created++; return new Window { Content = new TextBlock { Text = TranslationCatalog.LoadDefault().Translate("Cancel") } }; });
        scope.Own(first); first.Show(); Pump(first); Assert.That(first, Is.TypeOf<ChooseLanguageDialog>()); Assert.That(created, Is.Zero);
        ListBox languages = Field<ListBox>(first, "lvTranslations"); LanguageChoice french = languages.Items.OfType<LanguageChoice>().Single(choice => choice.Name == "French");
        languages.ScrollIntoView(french); Pump(first); Capture(first, "language-chooser-" + (dark ? "dark" : "light") + ".png");
        PushProcessTestDriver.Pointer((Control)languages.ContainerFromItem(french)!); Pump(first);
        Assert.That(first.IsVisible, Is.False); Assert.That(((ChooseLanguageDialog)first).WasSaved, Is.True);
        Assert.That(scope.Store.Load().Language, Is.EqualTo("French")); Assert.That(scope.Store.Load().HistoryLimit, Is.EqualTo(777));
        Assert.That(created, Is.EqualTo(1)); Assert.That(opened, Is.Not.Null); scope.Own(opened!);
        Assert.That(((TextBlock)opened!.Content!).Text, Is.EqualTo("Annuler"));
        Window next = scope.Own(DesktopLanguage.CreateInitialWindow(DesktopStartupRequest.Browse, _ => Assert.Fail("No second chooser"), scope.Store,
            createWindow: _ => new Window { Name = "DirectRestart", Content = new TextBlock { Text = TranslationCatalog.LoadDefault().Translate("Cancel") } }));
        Assert.That(next.Name, Is.EqualTo("DirectRestart")); Assert.That(((TextBlock)next.Content!).Text, Is.EqualTo("Annuler"));
    }

    [AvaloniaTest]
    public void KeyboardActivationAndClosingWithoutAChoiceBothPersistEnglishExactlyOnce()
    {
        using Scope scope = new(); ChooseLanguageDialog keyboard = scope.Own(new ChooseLanguageDialog(LanguageCatalog.LoadDefault(), scope.Store));
        keyboard.Show(); Pump(keyboard); Press(keyboard, PhysicalKey.ArrowDown); Press(keyboard, PhysicalKey.Enter);
        Assert.That(keyboard.IsVisible, Is.False); Assert.That(keyboard.SelectedChoice, Is.EqualTo(LanguageCatalog.English));
        Assert.That(scope.Store.Load().Language, Is.EqualTo("English"));
        File.Delete(scope.Store.FilePath);
        ChooseLanguageDialog closed = scope.Own(new ChooseLanguageDialog(LanguageCatalog.LoadDefault(), scope.Store)); closed.Show(); Pump(closed); closed.Close();
        Assert.That(closed.WasSaved, Is.True); Assert.That(closed.SelectedChoice, Is.Null); Assert.That(scope.Store.Load().Language, Is.EqualTo("English"));
        byte[] before = File.ReadAllBytes(scope.Store.FilePath); closed.Close(); Assert.That(File.ReadAllBytes(scope.Store.FilePath), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public void ClosingAnUnchosenChooserPreservesAPreferenceSavedByAnotherInteractiveWindow()
    {
        using Scope scope = new(); ChooseLanguageDialog chooser = scope.Own(new ChooseLanguageDialog(LanguageCatalog.LoadDefault(), scope.Store));
        chooser.Show(); Pump(chooser); scope.Store.Save(new() { Language = "French", HistoryLimit = 888 }); chooser.Close();
        Assert.That(chooser.WasSaved, Is.True); Assert.That(chooser.SelectedChoice, Is.Null);
        Assert.That(scope.Store.Load().Language, Is.EqualTo("French")); Assert.That(scope.Store.Load().HistoryLimit, Is.EqualTo(888));
    }

    [AvaloniaTest]
    public void SmokeBootstrapConstructsItsConsumerWithoutAChooserOrPreferenceWrite()
    {
        using Scope scope = new(); int created = 0;
        Window window = scope.Own(DesktopLanguage.CreateInitialWindow(new(new(DesktopStartupMode.Browse, SmokeTest: true)), _ => Assert.Fail(), scope.Store,
            createWindow: _ => { created++; return new Window(); }));
        Assert.That(window, Is.Not.TypeOf<ChooseLanguageDialog>()); Assert.That(created, Is.EqualTo(1)); Assert.That(File.Exists(scope.Store.FilePath), Is.False);
    }

    [AvaloniaTest]
    public void UnreadableProfileVetoesChooserClosingAndRetainsItsExactBytes()
    {
        using Scope scope = new();
        ChooseLanguageDialog chooser = (ChooseLanguageDialog)scope.Own(DesktopLanguage.CreateInitialWindow(DesktopStartupRequest.Browse,
            window => scope.Own(window).Show(), scope.Store, createWindow: _ => new Window()));
        chooser.Show(); Pump(chooser); Directory.CreateDirectory(Path.GetDirectoryName(scope.Store.FilePath)!);
        File.WriteAllText(scope.Store.FilePath, "{broken"); byte[] before = File.ReadAllBytes(scope.Store.FilePath);
        chooser.Close(); Pump(chooser);
        Assert.That(chooser.IsVisible, Is.True); Assert.That(chooser.WasSaved, Is.False);
        Assert.That(Field<TextBlock>(chooser, "LanguageSaveError").IsVisible, Is.True);
        Assert.That(File.ReadAllBytes(scope.Store.FilePath), Is.EqualTo(before));
        File.Delete(scope.Store.FilePath); chooser.Close(); Assert.That(chooser.IsVisible, Is.False);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task SettingsCancelDoesNotWriteAndOkReturnsTheRestartChoiceWithoutApplyingItLive(bool dark)
    {
        using Scope scope = new(); scope.Store.Save(new() { Language = "English", Theme = dark ? "Dark" : "Light", HistoryLimit = 321 }); DesktopLanguage.ApplyPreferredCulture();
        byte[] before = File.ReadAllBytes(scope.Store.FilePath); Window owner = scope.Own(new Window { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }); owner.Show();
        DesktopSettingsDialog cancelled = scope.Own(new DesktopSettingsDialog(scope.Store.Load(), TranslationCatalog.LoadDefault(), false) { RequestedThemeVariant = owner.ActualThemeVariant }); Task<bool> cancel = cancelled.ShowDialog<bool>(owner); Pump(cancelled);
        SelectLanguage(cancelled, "French"); Assert.That(File.ReadAllBytes(scope.Store.FilePath), Is.EqualTo(before));
        PushProcessTestDriver.Pointer(Field<Button>(cancelled, "CancelDesktopSettings")); Assert.That(await cancel, Is.False);
        Assert.That(File.ReadAllBytes(scope.Store.FilePath), Is.EqualTo(before));
        DesktopSettingsDialog accepted = scope.Own(new DesktopSettingsDialog(scope.Store.Load(), TranslationCatalog.LoadDefault(), false) { RequestedThemeVariant = owner.ActualThemeVariant }); Task<bool> accept = accepted.ShowDialog<bool>(owner); Pump(accepted);
        Assert.That(accepted.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Language (restart required)"), Is.True);
        SelectLanguage(accepted, "French"); Capture(accepted, "language-settings-" + (dark ? "dark" : "light") + ".png"); PushProcessTestDriver.Pointer(Field<Button>(accepted, "SaveDesktopSettings")); Assert.That(await accept, Is.True);
        Assert.That(accepted.Result.Language, Is.EqualTo("French")); Assert.That(accepted.Result.HistoryLimit, Is.EqualTo(321));
        Assert.That(CultureInfo.CurrentUICulture.Name, Is.EqualTo("en")); Assert.That(File.ReadAllBytes(scope.Store.FilePath), Is.EqualTo(before));
        scope.Store.Save(accepted.Result); DesktopLanguage.ApplyPreferredCulture();
        Assert.That(TranslationCatalog.LoadDefault().Translate("Cancel"), Is.EqualTo("Annuler"));
    }

    [AvaloniaTest]
    public async Task BrowseSettingsUsesItsActualSaveRouteAndKeepsTheExistingCultureUntilRestart()
    {
        using Scope scope = new(); scope.Store.Save(new() { Language = "English" }); DesktopLanguage.ApplyPreferredCulture();
        MainWindow main = scope.Own(new MainWindow()); main.Show(); Pump(main);
        Task workflow = (Task)typeof(MainWindow).GetMethod("ShowSettingsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null)!;
        await Until(() => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any());
        DesktopSettingsDialog settings = main.OwnedWindows.OfType<DesktopSettingsDialog>().Single(); scope.Own(settings); Pump(settings);
        SelectLanguage(settings, "French"); PushProcessTestDriver.Pointer(Field<Button>(settings, "SaveDesktopSettings"));
        await workflow.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(scope.Store.Load().Language, Is.EqualTo("French")); Assert.That(CultureInfo.CurrentUICulture.Name, Is.EqualTo("en"));
        TranslationCatalog existing = (TranslationCatalog)typeof(MainWindow).GetField("_translations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        Assert.That(existing.Translate("Cancel"), Is.EqualTo("Cancel"));
        main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted); await main.WindowWorkCompletion;
        DesktopLanguage.ApplyPreferredCulture(); AskpassWindow child = scope.Own(new AskpassWindow("Enter password")); child.Show(); Pump(child);
        Assert.That(Field<Button>(child, "CancelAskpass").Content, Is.EqualTo("Annuler"));
        DesktopSettingsDialog restarted = scope.Own(new DesktopSettingsDialog(scope.Store.Load(), TranslationCatalog.LoadDefault(), false));
        restarted.Show(); Pump(restarted);
        Assert.That(restarted.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Langue (redémarrage requis)"), Is.True);
        Assert.That((Field<ComboBox>(restarted, "Language").SelectedItem as LanguageChoice)?.Name, Is.EqualTo("French"));
        Capture(restarted, "language-settings-french.png");
    }

    [AvaloniaTest]
    public void ClosingTheBootstrapChooserUsesEnglishForTheConsumerEvenWhenTheOsUiWasFrench()
    {
        using Scope scope = new(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Window? next = null;
        Window chooser = scope.Own(DesktopLanguage.CreateInitialWindow(DesktopStartupRequest.Browse,
            window => { next = scope.Own(window); window.Show(); }, scope.Store,
            createWindow: _ => new Window { Content = new TextBlock { Text = TranslationCatalog.LoadDefault().Translate("Cancel") } }));
        chooser.Show(); Pump(chooser); chooser.Close();
        Assert.That(next, Is.Not.Null); Assert.That(((TextBlock)next!.Content!).Text, Is.EqualTo("Cancel"));
        Assert.That(CultureInfo.CurrentUICulture.Name, Is.EqualTo("en")); Assert.That(CultureInfo.CurrentCulture.Name, Is.EqualTo("fr-FR"));
        Assert.That(scope.Store.Load().Language, Is.EqualTo("English"));
    }

    [AvaloniaTest]
    public async Task StandaloneCleanupInitializesItsActualCatalogueBeforeOpeningAndCancelPreservesAllRepositoryBytes()
    {
        using Scope scope = new(); scope.Store.Save(new() { Language = "French" });
        GitRepository repository = await GitRepository.InitAsync(Path.Combine(scope.Root, "private-unborn-repository"));
        File.WriteAllText(Path.Combine(repository.Path, "literal-untracked.txt"), "untracked bytes\r\n");
        string before = RepositoryBytes(repository.Path);
        Window startup = scope.Own(App.CreateStartupWindow(new(new(DesktopStartupMode.Cleanup, repository.Path), repository)));
        Assert.That(startup, Is.TypeOf<CleanupRepositoryDialog>()); CleanupRepositoryDialog cleanup = (CleanupRepositoryDialog)startup;
        cleanup.Show(); Pump(cleanup); await Until(() => !cleanup.IsBusy);
        Assert.That(cleanup.Title, Is.EqualTo(TranslationCatalog.LoadDefault().Translate("FormCleanupRepository", "$this", "Text", "Clean working directory")));
        Assert.That(cleanup.Title, Is.Not.EqualTo("Clean working directory"));
        Assert.That(cleanup.LastResult, Is.Null, "Opening standalone must not preview or clean the repository.");
        PushProcessTestDriver.Pointer(Field<Button>(cleanup, "CloseCleanup")); await Until(() => !cleanup.IsVisible);
        Assert.That(RepositoryBytes(repository.Path), Is.EqualTo(before)); Assert.That(scope.Store.Load().Language, Is.EqualTo("French"));
    }

    [AvaloniaTest]
    public async Task NativeRebaseEditorConsumesTheSameSavedLanguageWithoutChangingTheRawInstructionsOnCancel()
    {
        using RebaseUiFixture fixture = await RebaseUiFixture.Create(conflict: true); await fixture.StartConflict();
        using Scope scope = new(); scope.Store.Save(new() { Language = "French" });
        AskpassApplication.ApplyUiCulture(); string before = await fixture.Witness();
        NativeRebaseEditorWindow editor = scope.Own(new NativeRebaseEditorWindow(fixture.Request(GitRebaseEditorKind.Todo)));
        editor.Show(); Pump(editor);
        Assert.That(Field<Button>(editor, "CancelNativeRebaseEditor").Content, Is.EqualTo("Annuler"));
        PushProcessTestDriver.Pointer(Field<Button>(editor, "CancelNativeRebaseEditor"));
        Assert.That(editor.ExitCode, Is.EqualTo(1)); Assert.That(editor.Saved, Is.False);
        Assert.That(await fixture.Witness(), Is.EqualTo(before)); Assert.That(scope.Store.Load().Language, Is.EqualTo("French"));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task BootstrapKeepsBrowseUsableForAnUnreadableProfileWithoutAChooserOrWrites(bool accessDenied)
    {
        using Scope scope = new(); scope.Store.Save(new() { Language = "French" });
        if (!accessDenied) { File.WriteAllText(scope.Store.FilePath, "{broken"); }
        byte[] before = File.ReadAllBytes(scope.Store.FilePath);
        UnixFileMode? originalMode = OperatingSystem.IsLinux() ? File.GetUnixFileMode(scope.Store.FilePath) : null;
        if (accessDenied && !OperatingSystem.IsLinux()) { Assert.Ignore("The permission prerequisite is Linux-specific."); }
        try
        {
            if (accessDenied && OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(scope.Store.FilePath, UnixFileMode.None);
                Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllBytes(scope.Store.FilePath), "The real read must be denied before checking bootstrap.");
            }
            Window initial = scope.Own(DesktopLanguage.CreateInitialWindow(DesktopStartupRequest.Browse, _ => Assert.Fail("An invalid profile must not open the chooser."), scope.Store));
            Assert.That(initial, Is.TypeOf<MainWindow>()); MainWindow main = (MainWindow)initial;
            try
            {
                main.Show(); Pump(main); Assert.That(main.IsVisible && main.IsEffectivelyEnabled, Is.True);
                TextBox output = (TextBox)typeof(MainWindow).GetField("_output", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                Assert.That(output.Text, Does.Contain("Unable to read preferences:"));
                Assert.That(main.OwnedWindows.OfType<ChooseLanguageDialog>(), Is.Empty);
            }
            finally { await CloseBrowse(main); }
        }
        finally
        {
            if (accessDenied && OperatingSystem.IsLinux()) { File.SetUnixFileMode(scope.Store.FilePath, originalMode!.Value); }
            Assert.That(File.ReadAllBytes(scope.Store.FilePath), Is.EqualTo(before));
        }
    }

    [AvaloniaTest]
    public async Task SettingsKeepsARemovedTranslationForOtherChangesUntilARealLanguageChoice()
    {
        using Scope scope = new(); scope.Store.Save(new() { Language = "Removed translation", HistoryLimit = 432 }); DesktopLanguage.ApplyPreferredCulture();
        MainWindow main = scope.Own(new MainWindow()); main.Show(); Pump(main);
        try
        {
            (DesktopSettingsDialog settings, Task saved) = await OpenSettings(scope, main);
            ComboBox language = Field<ComboBox>(settings, "Language");
            Assert.That(language.SelectedItem, Is.Null, "English fallback must not pretend to be a saved installed-language choice.");
            Assert.That(language.PlaceholderText, Does.Contain("English")); Assert.That(ToolTip.GetTip(language)?.ToString(), Does.Contain("Removed translation"));
            Capture(settings, "language-settings-unavailable.png");
            ChooseOption(settings, Field<ComboBox>(settings, "SettingsTheme"), "Dark");
            PushProcessTestDriver.Pointer(Field<Button>(settings, "SaveDesktopSettings")); await saved.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(scope.Store.Load().Language, Is.EqualTo("Removed translation")); Assert.That(scope.Store.Load().Theme, Is.EqualTo("Dark"));
            (settings, saved) = await OpenSettings(scope, main);
            ComboBox choice = Field<ComboBox>(settings, "Language"); PushProcessTestDriver.Pointer(choice); Pump(settings); Assert.That(choice.IsDropDownOpen, Is.True);
            LanguageChoice english = choice.Items.OfType<LanguageChoice>().Single(item => item.Name == "English");
            Control row = choice.ContainerFromItem(english)!; Assert.That(row, Is.Not.Null); PushProcessTestDriver.Pointer(row); Pump(settings);
            Assert.That((choice.SelectedItem as LanguageChoice)?.Name, Is.EqualTo("English"));
            PushProcessTestDriver.Pointer(Field<Button>(settings, "SaveDesktopSettings")); await saved.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(scope.Store.Load().Language, Is.EqualTo("English")); Assert.That(scope.Store.Load().HistoryLimit, Is.EqualTo(432));
        }
        finally { await CloseBrowse(main); }
    }

    [AvaloniaTest]
    public async Task TwoBrowseWindowsKeepTheLatestLanguageThroughTheOlderTreeSortAndSettings()
    {
        using ReferenceDialogTests.ReferenceFixture fixture = await ReferenceDialogTests.ReferenceFixture.CreateAsync();
        using Scope scope = new(); scope.Store.Save(new() { Language = "English" });
        MainWindow first = scope.Own(new MainWindow()), older = scope.Own(new MainWindow()); first.Show(); older.Show();
        try
        {
            await older.OpenRepositoryAsync(fixture.Repository.Path); await Until(() => !older.IsBusy); Pump(older);
            string before = RepositoryBytes(fixture.Repository.Path);
            await SaveFrench(scope, first);
            TreeView tree = Field<TreeView>(older, "RepositoryNavigation");
            IEnumerable<TreeViewItem> Nodes(IEnumerable<object> items) => items.OfType<TreeViewItem>().SelectMany(item => new[] { item }.Concat(Nodes(item.Items.Cast<object>())));
            TreeViewItem branch = Nodes(tree.Items.Cast<object>()).Single(item => item.Tag is GitReference reference && reference.Name == "refs/heads/main");
            Control label = (Control)branch.Header!; Point point = label.TranslatePoint(new Point(Math.Min(35, label.Bounds.Width / 2), 8), older)!.Value;
            Assert.That((older.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(branch), Is.True);
            older.MouseMove(point); older.MouseDown(point, MouseButton.Right); older.MouseUp(point, MouseButton.Right); Pump(older);
            await Until(() => tree.ContextMenu!.IsOpen); MenuItem by = tree.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "GitRefsSortByContextMenuItem");
            PushProcessTestDriver.Pointer(by); Pump(older);
            PushProcessTestDriver.Pointer(by.Items.OfType<MenuItem>().Single(item => item.Tag is GitRefsSortBy.RefName));
            await Until(() => !older.IsBusy && scope.Store.Load().ReferenceSorting.By == GitRefsSortBy.RefName);
            Assert.That(scope.Store.Load().Language, Is.EqualTo("French"));
            (DesktopSettingsDialog settings, Task complete) = await OpenSettings(scope, older);
            Assert.That((Field<ComboBox>(settings, "Language").SelectedItem as LanguageChoice)?.Name, Is.EqualTo("French"));
            PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await complete.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(RepositoryBytes(fixture.Repository.Path), Is.EqualTo(before));
        }
        finally { await CloseBrowse(older); await CloseBrowse(first); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task AnAlreadyOpenSettingsPreservesAnExternalLanguageUnlessTheUserChangesItsChoice(bool explicitChoice)
    {
        using Scope scope = new(); scope.Store.Save(new() { Language = "English" });
        MainWindow first = scope.Own(new MainWindow()), older = scope.Own(new MainWindow()); first.Show(); older.Show(); Pump(older);
        try
        {
            (DesktopSettingsDialog settings, Task complete) = await OpenSettings(scope, older);
            Assert.That((Field<ComboBox>(settings, "Language").SelectedItem as LanguageChoice)?.Name, Is.EqualTo("English"));
            await SaveFrench(scope, first);
            if (explicitChoice) { SelectLanguage(settings, "German"); }
            else { ChooseOption(settings, Field<ComboBox>(settings, "SettingsTheme"), "Dark"); }
            PushProcessTestDriver.Pointer(Field<Button>(settings, "SaveDesktopSettings")); await complete.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(scope.Store.Load().Language, Is.EqualTo(explicitChoice ? "German" : "French"));
        }
        finally { await CloseBrowse(older); await CloseBrowse(first); }
    }

    [AvaloniaTest]
    [TestCase("theme")]
    [TestCase("plugin")]
    public async Task OlderBrowseNonLanguageRoutesPreserveTheLatestLanguageAndOtherPluginEntries(string route)
    {
        using Scope scope = new(); scope.Store.Save(new() { Language = "English" });
        MainWindow first = scope.Own(new MainWindow()), older = scope.Own(new MainWindow()); first.Show(); older.Show(); Pump(older);
        try
        {
            await SaveFrench(scope, first);
            Invoke(first, "SavePluginSettings", BackgroundFetchService.Descriptor, new Dictionary<string, string?> { ["enabled"] = "false", ["arguments"] = "keep literal" });
            if (route == "theme") { await ((Task)Invoke(older, "ChangeThemeAsync")!).WaitAsync(TimeSpan.FromSeconds(10)); }
            else { Invoke(older, "SavePluginSettings", AutoCompileSubmodulesService.Descriptor, new Dictionary<string, string?> { ["enabled"] = "false", ["password"] = "must-not-persist" }); }
            DesktopPreferences latest = scope.Store.Load(); Assert.That(latest.Language, Is.EqualTo("French"));
            Assert.That(latest.PluginSettings[BackgroundFetchService.Descriptor.Id.ToString()]["arguments"], Is.EqualTo("keep literal"));
            if (route == "plugin") { Assert.That(latest.PluginSettings[AutoCompileSubmodulesService.Descriptor.Id.ToString()], Does.Not.ContainKey("password")); }
        }
        finally { await CloseBrowse(older); await CloseBrowse(first); }
    }

    private static object? Invoke(MainWindow window, string name, params object[] arguments)
        => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
    private static async Task<(DesktopSettingsDialog Dialog, Task Completion)> OpenSettings(Scope scope, MainWindow owner)
    {
        Task completion = (Task)Invoke(owner, "ShowSettingsAsync")!; await Until(() => owner.OwnedWindows.OfType<DesktopSettingsDialog>().Any());
        DesktopSettingsDialog settings = scope.Own(owner.OwnedWindows.OfType<DesktopSettingsDialog>().Single()); Pump(settings); return (settings, completion);
    }
    private static async Task SaveFrench(Scope scope, MainWindow window)
    {
        (DesktopSettingsDialog dialog, Task complete) = await OpenSettings(scope, window); SelectLanguage(dialog, "French");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings")); await complete.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(scope.Store.Load().Language, Is.EqualTo("French"));
    }
    private static void ChooseOption(Window window, ComboBox combo, object value)
    {
        PushProcessTestDriver.Pointer(combo); Pump(window); Assert.That(combo.IsDropDownOpen, Is.True);
        int index = combo.Items.Cast<object>().ToList().IndexOf(value); Assert.That(index, Is.GreaterThanOrEqualTo(0));
        Press(window, PhysicalKey.Home); for (int i = 0; i < index; i++) { Press(window, PhysicalKey.ArrowDown); }
        Press(window, PhysicalKey.Enter); Assert.That(combo.SelectedItem, Is.EqualTo(value));
    }
    private static async Task CloseBrowse(MainWindow window)
    {
        foreach (Window child in window.OwnedWindows.ToArray()) { child.Close(); }
        window.Close(); await Until(() => !window.IsVisible && window.WindowWorkCompletion.IsCompleted); await window.WindowWorkCompletion;
    }

    private static string RepositoryBytes(string root) => JsonSerializer.Serialize(Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private static void Capture(TopLevel window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }

    private static void SelectLanguage(Window window, string name)
    {
        ComboBox combo = Field<ComboBox>(window, "Language"); PushProcessTestDriver.Pointer(combo); Pump(window);
        Assert.That(combo.IsDropDownOpen, Is.True); Press(window, PhysicalKey.Home);
        int index = combo.Items.OfType<LanguageChoice>().Select(choice => choice.Name).ToList().IndexOf(name); Assert.That(index, Is.GreaterThanOrEqualTo(0));
        for (int i = 0; i < index; i++) { Press(window, PhysicalKey.ArrowDown); }
        Press(window, PhysicalKey.Enter); Assert.That((combo.SelectedItem as LanguageChoice)?.Name, Is.EqualTo(name));
    }
    private static T Field<T>(TopLevel root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static void Press(Window window, PhysicalKey key)
    { window.KeyPressQwerty(key, RawInputModifiers.None); if (window.IsVisible) { window.KeyReleaseQwerty(key, RawInputModifiers.None); Pump(window); } }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    { DateTime end = DateTime.UtcNow.AddSeconds(10); while (!condition() && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True); }

    private sealed class Scope : IDisposable
    {
        private readonly string? _profile = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), _override = Environment.GetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE"), _askpass = GitRunner.AskpassUiCulture;
        private readonly CultureInfo _ui = CultureInfo.CurrentUICulture, _format = CultureInfo.CurrentCulture;
        private readonly CultureInfo? _default = CultureInfo.DefaultThreadCurrentUICulture;
        private readonly ThemeVariant? _theme = Application.Current?.RequestedThemeVariant;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-language-profile-" + Guid.NewGuid().ToString("N"));
        private readonly List<Window> _windows = [];
        private readonly Dictionary<string, string?> _gitEnvironment = [];
        internal string Root => _root;
        internal PreferencesStore Store { get; }
        internal Scope()
        {
            foreach ((string key, string value) in new[] { ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_OPTIONAL_LOCKS", "0") })
            { _gitEnvironment.Add(key, Environment.GetEnvironmentVariable(key)); Environment.SetEnvironmentVariable(key, value); }
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root); Environment.SetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE", null);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en"); Store = new();
        }
        internal T Own<T>(T window) where T : Window { _windows.Add(window); return window; }
        public void Dispose()
        {
            try { foreach (Window window in _windows.AsEnumerable().Reverse()) { if (window.IsVisible) { window.Close(); } } }
            finally
            {
                foreach ((string key, string? value) in _gitEnvironment) { Environment.SetEnvironmentVariable(key, value); }
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _profile); Environment.SetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE", _override);
                if (Application.Current is { } application) { application.RequestedThemeVariant = _theme; }
                CultureInfo.CurrentUICulture = _ui; CultureInfo.CurrentCulture = _format; CultureInfo.DefaultThreadCurrentUICulture = _default; GitRunner.AskpassUiCulture = _askpass;
                if (Directory.Exists(_root)) { Directory.Delete(_root, true); }
            }
        }
    }
}

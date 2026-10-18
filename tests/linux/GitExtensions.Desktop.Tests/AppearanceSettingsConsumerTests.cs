using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AppearanceSettingsConsumerTests
{
    [AvaloniaTest]
    public async Task ApplyingDateAndAvatarUpdatesTheExistingRealCommitRowAndCancelKeepsThatPublicationOnReopen()
    {
        await using Fixture f = await Fixture.Create(); await f.Open(); MainWindow main = f.Main!;
        ListBox history = f.History; object source = history.ItemsSource!; Control row = f.Row;
        Control avatar = f.Avatar; TextBlock date = f.Date; string relative = date.Text!; string absolute = f.AbsoluteDate;
        Assert.That(avatar.IsEffectivelyVisible, Is.True); Assert.That(relative, Does.Contain("years ago"));
        DesktopSettingsDialog settings = await f.Settings(); AppearanceSettingsPage page = Field<AppearanceSettingsPage>(settings, "AppearanceSettingsPage");
        AppearanceSettingsInput.Click(settings, "chkShowRelativeDate"); AppearanceSettingsInput.Click(settings, "ShowAuthorAvatarInCommitGraph");
        GitCommandLog.Clear(); await f.Apply(settings, () => !new HistoryViewPreferencesStore().Load().RelativeDate);
        Assert.That(settings.IsVisible, Is.True); Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(f.Row, Is.SameAs(row));
        Assert.That(f.Avatar, Is.SameAs(avatar)); Assert.That(f.Date, Is.SameAs(date)); Assert.That(avatar.IsVisible, Is.False);
        Assert.That(date.Text, Is.EqualTo(absolute)); Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Command == "git log"), Is.False);
        Assert.That(page.Draft.RelativeDate, Is.False); Assert.That(page.Draft.ShowAvatar, Is.False);
        AppearanceSettingsInput.Click(settings, "chkShowRelativeDate"); AppearanceSettingsInput.Click(settings, "ShowAuthorAvatarInCommitGraph");
        AppearanceSettingsInput.Click(settings, "CancelDesktopSettings"); await f.Idle();
        Assert.That(date.Text, Is.EqualTo(absolute)); Assert.That(avatar.IsVisible, Is.False);
        Assert.That(new HistoryViewPreferencesStore().Load().RelativeDate, Is.False); Assert.That(new HistoryViewPreferencesStore().Load().ShowAvatar, Is.False);
        DesktopSettingsDialog reopened = await f.Settings();
        Assert.That(Field<AppearanceSettingsPage>(reopened, "AppearanceSettingsPage").Draft.RelativeDate, Is.False);
        Assert.That(Field<AppearanceSettingsPage>(reopened, "AppearanceSettingsPage").Draft.ShowAvatar, Is.False);
        AppearanceSettingsInput.Click(reopened, "CancelDesktopSettings"); await f.Close(); await f.Open();
        Assert.That(f.Date.Text, Is.EqualTo(absolute)); Assert.That(f.Avatar.IsVisible, Is.False);
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task AnUnrelatedAppearanceApplyKeepsViewMenuChoicesTemporaryInBothTheRealGridAndTheSavedProfile()
    {
        await using Fixture f = await Fixture.Create(); await f.Open();
        await f.View("showRelativeDateToolStripMenuItem"); await f.View("showAuthorAvatarColumnToolStripMenuItem");
        await f.View("showRevisionGraphColumnToolStripMenuItem"); await f.View("showAuthorNameColumnToolStripMenuItem");
        Assert.That(f.Date.Text, Is.EqualTo(f.AbsoluteDate)); Assert.That(f.Avatar.IsVisible, Is.False);
        Assert.That(f.Graph.IsVisible, Is.False); Assert.That(f.Author.IsVisible, Is.False);
        HistoryViewPreferences defaults = new HistoryViewPreferencesStore().Load();
        Assert.That(defaults.RelativeDate && defaults.ShowAvatar && defaults.ShowGraph && defaults.ShowAuthor, Is.True);
        DesktopSettingsDialog settings = await f.Settings(); AppearanceSettingsPage page = Field<AppearanceSettingsPage>(settings, "AppearanceSettingsPage");
        Assert.That(page.Draft.RelativeDate, Is.False); Assert.That(page.Draft.ShowAvatar, Is.False);
        object source = f.History.ItemsSource!; Control row = f.Row;
        AppearanceSettingsInput.Choose(settings, "SettingsTheme", "Dark"); GitCommandLog.Clear();
        await f.Apply(settings, () => new PreferencesStore().Load().Theme == "Dark");
        f.AssertTheme(ThemeVariant.Dark);
        Assert.That(f.History.ItemsSource, Is.SameAs(source)); Assert.That(f.Row, Is.SameAs(row));
        Assert.That(f.Date.Text, Is.EqualTo(f.AbsoluteDate)); Assert.That(f.Avatar.IsVisible, Is.False);
        Assert.That(f.Graph.IsVisible, Is.False); Assert.That(f.Author.IsVisible, Is.False);
        HistoryViewPreferences saved = new HistoryViewPreferencesStore().Load();
        Assert.That(saved.RelativeDate && saved.ShowAvatar && saved.ShowGraph && saved.ShowAuthor, Is.True,
            "Appearance must not turn an untouched temporary View into saved defaults.");
        Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Command == "git log"), Is.False);
        AppearanceSettingsInput.Click(settings, "CancelDesktopSettings"); await f.Close(); await f.Open();
        f.AssertTheme(ThemeVariant.Dark); Assert.That(f.Date.Text, Does.Contain("years ago"));
        Assert.That(f.Avatar.IsEffectivelyVisible, Is.True); Assert.That(f.Graph.IsVisible, Is.True); Assert.That(f.Author.IsVisible, Is.True);
    }

    [AvaloniaTest]
    [TestCase(true)]
    [TestCase(false)]
    public async Task EditingOneTemporaryAppearanceChoicePublishesOnlyThatChoiceAndReopenUsesItsNewDefault(bool editDate)
    {
        await using Fixture f = await Fixture.Create(); await f.Open();
        await f.View("showRelativeDateToolStripMenuItem"); await f.View("showAuthorAvatarColumnToolStripMenuItem");
        DesktopSettingsDialog settings = await f.Settings(); object source = f.History.ItemsSource!; Control row = f.Row;
        AppearanceSettingsInput.Click(settings, editDate ? "chkShowRelativeDate" : "ShowAuthorAvatarInCommitGraph");
        await f.Apply(settings, () => Field<TextBlock>(settings, "SettingsNotice").Text is null or "");
        Assert.That(f.History.ItemsSource, Is.SameAs(source)); Assert.That(f.Row, Is.SameAs(row));
        Assert.That(f.Avatar.IsVisible, Is.EqualTo(!editDate));
        if (editDate) { Assert.That(f.Date.Text, Does.Contain("years ago")); }
        else { Assert.That(f.Date.Text, Is.EqualTo(f.AbsoluteDate)); }
        HistoryViewPreferences saved = new HistoryViewPreferencesStore().Load();
        Assert.That(saved.RelativeDate, Is.True); Assert.That(saved.ShowAvatar, Is.True,
            "The other hidden temporary choice is not written by changing this Appearance control.");
        // Apply a second explicit edit to create a saved false default for just this field.
        AppearanceSettingsInput.Click(settings, editDate ? "chkShowRelativeDate" : "ShowAuthorAvatarInCommitGraph");
        await f.Apply(settings, () => editDate ? !new HistoryViewPreferencesStore().Load().RelativeDate : !new HistoryViewPreferencesStore().Load().ShowAvatar);
        AppearanceSettingsInput.Click(settings, "CancelDesktopSettings"); await f.Close(); await f.Open();
        Assert.That(f.Avatar.IsVisible, Is.EqualTo(editDate));
        if (editDate) { Assert.That(f.Date.Text, Is.EqualTo(f.AbsoluteDate)); }
        else { Assert.That(f.Date.Text, Does.Contain("years ago")); }
    }

    [AvaloniaTest]
    [TestCase("Clone")]
    [TestCase("History")]
    public async Task TheRealMainWindowRetryPreservesAPeerAfterDesktopWasSavedButALaterProfileFailed(string phase)
    {
        await using Fixture f = await Fixture.Create(); await f.Open();
        DesktopSettingsDialog settings = await f.Settings(); object source = f.History.ItemsSource!; Control row = f.Row;
        AppearanceSettingsInput.Choose(settings, "SettingsTheme", "Dark"); AppearanceSettingsInput.ChooseLanguage(settings, "French");
        AppearanceSettingsInput.Click(settings, "chkShowRepoCurrentBranch"); // Initially false: publish true, then a peer switches it back.
        AppearanceSettingsInput.Click(settings, "chkShowRelativeDate"); AppearanceSettingsInput.Click(settings, "ShowAuthorAvatarInCommitGraph");
        ClonePreferencesStore clone = new(); HistoryViewPreferencesStore history = new();
        if (phase == "Clone")
        {
            SettingsNavigationTestDriver.Select(settings, "General");
            AppearanceSettingsInput.EditCombo(settings, "cbDefaultCloneDestination", f.Git.Output("new clone destination"));
            SettingsNavigationTestDriver.Select(settings, "Appearance");
        }
        string path = phase == "Clone" ? clone.FilePath : history.FilePath; byte[] original = File.ReadAllBytes(path);
        const string broken = "{broken-real-appearance-profile"; File.WriteAllText(path, broken);
        AppearanceSettingsInput.Click(settings, "ApplyDesktopSettings");
        await PushProcessTestDriver.UntilAsync(settings, () => settings.Navigation.IsEnabled && Field<TextBlock>(settings, "SettingsNotice").Text?.Contains("Some settings", StringComparison.Ordinal) == true);
        Assert.That(File.ReadAllText(path), Is.EqualTo(broken)); Assert.That(settings.IsVisible, Is.True);
        Assert.That(new PreferencesStore().Load().Theme, Is.EqualTo("Dark")); Assert.That(new PreferencesStore().Load().Language, Is.EqualTo("French"));
        Assert.That(new PreferencesStore().Load().ShowRepoCurrentBranch, Is.True);
        f.AssertTheme(ThemeVariant.Light, "A failed publication leaves the live presentation at the last full Apply.");
        Assert.That(f.Date.Text, Does.Contain("years ago")); Assert.That(f.Avatar.IsVisible, Is.True);
        File.WriteAllBytes(path, original);
        PreferencesStore desktop = new(); desktop.Save(desktop.Load() with { Theme = "System", Language = "English", ShowRepoCurrentBranch = false });
        history.Save(history.Load() with { ShowGraph = false, HiddenToolbarItems = ["peer-hidden-item"] });
        await f.Apply(settings, () => !history.Load().RelativeDate);
        Assert.That(desktop.Load().Theme, Is.EqualTo("System")); Assert.That(desktop.Load().Language, Is.EqualTo("English"));
        Assert.That(desktop.Load().ShowRepoCurrentBranch, Is.False); Assert.That(history.Load().ShowAvatar, Is.False);
        Assert.That(history.Load().ShowGraph, Is.False); Assert.That(history.Load().HiddenToolbarItems, Is.EqualTo(new[] { "peer-hidden-item" }));
        Assert.That(f.History.ItemsSource, Is.SameAs(source)); Assert.That(f.Row, Is.SameAs(row));
        Assert.That(f.Date.Text, Is.EqualTo(f.AbsoluteDate)); Assert.That(f.Avatar.IsVisible, Is.False); Assert.That(f.Graph.IsVisible, Is.True);
        AppearanceSettingsPage page = Field<AppearanceSettingsPage>(settings, "AppearanceSettingsPage");
        Assert.That(page.Draft.Theme, Is.EqualTo("System")); Assert.That(page.Draft.Language, Is.EqualTo("English"));
        Assert.That(page.Draft.ShowRepoCurrentBranch, Is.False); Assert.That(Field<TextBlock>(settings, "SettingsNotice").Text, Is.Null.Or.Empty);
        if (phase == "Clone") { Assert.That(clone.Load().DefaultDestination, Is.EqualTo(f.Git.Output("new clone destination"))); }
        AppearanceSettingsInput.Click(settings, "CancelDesktopSettings"); await f.Idle();
    }

    [AvaloniaTest]
    public async Task TheActualAppearanceTranslateLinkUsesTheOwnedSettingsLauncherWithoutSavingItsDraft()
    {
        await using Fixture f = await Fixture.Create(); await f.Open(); List<ProcessStartInfo> launches = [];
        f.Main!.ExternalLauncher = start => { launches.Add(start); return null; };
        DesktopSettingsDialog settings = await f.Settings();
        AppearanceSettingsInput.Choose(settings, "SettingsTheme", "Dark");
        byte[] desktop = File.ReadAllBytes(new PreferencesStore().FilePath), history = File.ReadAllBytes(new HistoryViewPreferencesStore().FilePath);
        AppearanceSettingsInput.Click(settings, "helpTranslate");
        await PushProcessTestDriver.UntilAsync(settings, () => launches.Count == 1);
        Assert.That(settings.Owner, Is.SameAs(f.Main)); Assert.That(settings.IsVisible, Is.True);
        Assert.That(launches[0].FileName, Is.EqualTo("xdg-open")); Assert.That(launches[0].UseShellExecute, Is.False);
        Assert.That(launches[0].ArgumentList, Is.EqualTo(new[] { "https://github.com/gitextensions/gitextensions/wiki/Translations" }));
        Assert.That(File.ReadAllBytes(new PreferencesStore().FilePath), Is.EqualTo(desktop));
        Assert.That(File.ReadAllBytes(new HistoryViewPreferencesStore().FilePath), Is.EqualTo(history));
        Assert.That(Field<AppearanceSettingsPage>(settings, "AppearanceSettingsPage").Draft.Theme, Is.EqualTo("Dark"));
        AppearanceSettingsInput.Click(settings, "CancelDesktopSettings"); await f.Idle();
        f.AssertTheme(ThemeVariant.Light);
    }

    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly GitEnvironmentOptions _original = GitEnvironment.Options;
        internal GitFixture Git { get; }
        internal MainWindow? Main { get; private set; }
        internal ListBox History => Field<ListBox>(Main!, "RevisionHistory");
        internal Control Row => ((Control)History.ContainerFromItem(Commit)!).GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionHistoryRow");
        internal GitCommit Commit => History.ItemsSource!.OfType<GitCommit>().Single(commit => commit.Subject == "Dated Appearance consumer");
        internal Control Avatar => Row.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionAuthorAvatar");
        internal TextBlock Date => Row.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "RevisionDate");
        internal TextBlock Author => Row.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "RevisionAuthorName");
        internal CommitGraph Graph => Main!.GetVisualDescendants().OfType<CommitGraph>().Single();
        internal string AbsoluteDate => Commit.AuthoredAt.LocalDateTime.ToString("G", CultureInfo.CurrentCulture);
        private Fixture(GitFixture git) => Git = git;
        internal static async Task<Fixture> Create()
        {
            Fixture f = new(await GitFixture.CreateAsync());
            try
            {
                f.Git.SetEnvironment("GIT_AUTHOR_DATE", "2020-01-02T03:04:05+00:00");
                f.Git.SetEnvironment("GIT_COMMITTER_DATE", "2020-01-02T03:04:05+00:00");
                await f.Git.ChangeAsync("dated commit content\n", "Dated Appearance consumer");
                new PreferencesStore().Save(new() { Theme = "Light", Language = "English", ShowRepoCurrentBranch = false });
                new HistoryViewPreferencesStore().Save(new()); new ClonePreferencesStore().SaveStrict(new(f.Git.Output("last clone"), false));
                GitEnvironment.Configure(new() { Executable = "/usr/bin/git" }); return f;
            }
            catch { f.Git.Dispose(); throw; }
        }
        internal async Task Open()
        {
            Assert.That(Main, Is.Null); Main = new() { Width = 1440, Height = 960 }; Main.Show();
            await Main.OpenRepositoryAsync(Git.Repository.Path); await Idle();
            Assert.That(History.ContainerFromItem(Commit), Is.Not.Null, "The real dated commit must have a realized row.");
        }
        internal async Task Idle()
        {
            await PushProcessTestDriver.UntilAsync(Main!, () => !Main!.IsBusy && Main.WindowWorkCompletion.IsCompleted && Main.RepositoryBranchLoading.IsCompleted);
            await Main!.WindowWorkCompletion; await Main.RepositoryBranchLoading; AppearanceSettingsInput.Pump(Main);
            Assert.That(Main.LastException, Is.Null, Main.LastException?.ToString());
        }
        internal void AssertTheme(ThemeVariant expected, string? message = null)
        {
            MainWindow main = Main ?? throw new InvalidOperationException("The fixture Browse window is not open.");
            AppearanceSettingsInput.Pump(main);
            Assert.That(main.ActualThemeVariant, Is.EqualTo(expected), message);
            Border toolbar = Field<Border>(main, "RepositoryToolbar");
            Assert.That(toolbar.IsEffectivelyVisible, Is.True, "The palette oracle must inspect a real visible toolbar.");
            Assert.That(toolbar.Background, Is.InstanceOf<ISolidColorBrush>());
            Assert.That(((ISolidColorBrush)toolbar.Background!).Color,
                Is.EqualTo(Color.Parse(expected == ThemeVariant.Dark ? "#202020" : "#F0F0F0")), message);
        }
        internal async Task<DesktopSettingsDialog> Settings()
        {
            AppearanceSettingsInput.Press(Main!, PhysicalKey.Comma, RawInputModifiers.Control);
            await PushProcessTestDriver.UntilAsync(Main!, () => Main!.OwnedWindows.OfType<DesktopSettingsDialog>().Any(window => window.IsVisible));
            DesktopSettingsDialog dialog = Main!.OwnedWindows.OfType<DesktopSettingsDialog>().Single(window => window.IsVisible);
            SettingsNavigationTestDriver.Select(dialog, "Appearance"); AppearanceSettingsInput.Pump(dialog);
            Assert.That(dialog.GitEnvironmentSession, Is.Not.Null);
            GitEnvironmentSettingsSession environment = dialog.GitEnvironmentSession!;
            await environment.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(20));
            Button apply = Field<Button>(dialog, "ApplyDesktopSettings");
            await PushProcessTestDriver.UntilAsync(dialog, () => !environment.IsBusy && apply.IsEffectivelyEnabled,
                "The initial Settings environment scan must finish before the Appearance fixture can click Apply.");
            Assert.That(environment.OperationCompletion.IsCompleted, Is.True); Assert.That(environment.IsBusy, Is.False);
            Assert.That(apply.IsEnabled && apply.IsEffectivelyEnabled, Is.True);
            return dialog;
        }
        internal async Task Apply(DesktopSettingsDialog settings, Func<bool> published)
        {
            AppearanceSettingsInput.Click(settings, "ApplyDesktopSettings");
            await PushProcessTestDriver.UntilAsync(settings, () => settings.Navigation.IsEnabled && published()
                && Field<TextBlock>(settings, "SettingsNotice").Text is null or "");
            await Idle(); Assert.That(settings.IsVisible, Is.True); Assert.That(Field<TextBlock>(settings, "SettingsNotice").Text, Is.Null.Or.Empty);
        }
        internal async Task View(string command)
        {
            MainWindow main = Main ?? throw new InvalidOperationException("The fixture Browse window is not open.");
            MenuItem view = main.GetVisualDescendants().OfType<MenuItem>().Single(item => item.Name == "ViewMenu");
            PushProcessTestDriver.Pointer(view); AppearanceSettingsInput.Pump(main); Assert.That(view.IsSubMenuOpen, Is.True);
            MenuItem item = view.Items.OfType<MenuItem>().Single(menu => menu.Name == "View" + command);
            bool before = item.IsChecked; PushProcessTestDriver.Pointer(item); AppearanceSettingsInput.Pump(main);
            await PushProcessTestDriver.UntilAsync(main, () => item.IsChecked != before); await Idle();
        }
        internal async Task Close()
        {
            if (Main is not { } closing) { return; }
            closing.Close();
            await PushProcessTestDriver.UntilAsync(closing, () => !closing.IsVisible && closing.WindowWorkCompletion.IsCompleted && closing.RepositoryBranches.Completion.IsCompleted);
            await closing.WindowWorkCompletion; await closing.RepositoryBranches.Completion; Main = null;
        }
        public async ValueTask DisposeAsync()
        {
            try { await Close(); }
            finally { GitEnvironment.Configure(_original); Git.Dispose(); }
        }
    }
}

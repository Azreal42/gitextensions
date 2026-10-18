using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GeneralSettingsSessionTests
{
    [AvaloniaTest]
    public async Task CancelBeforeAnyApplyDiscardsTheRealSettingsDraftWithoutPublishingAProfile()
    {
        using Fixture f = new(); f.Seed(); Dictionary<string, byte[]> before = f.Profiles(); await using Host host = new(f);
        host.Number("_NO_TRANSLATE_MaxCommits", "111000"); host.Click("chkStashUntrackedFiles");
        SettingsNavigationTestDriver.Select(host.Dialog, "Sorting"); SettingsNavigationTestDriver.Select(host.Dialog, "General");
        Assert.That(host.Page.Draft.MaxRevisionGraphCommits, Is.EqualTo(111000));
        Assert.That(host.Page.Draft.IncludeUntrackedFilesInAutoStash, Is.True); f.SameProfiles(before);
        host.Click("CancelDesktopSettings"); Assert.That(await host.Result, Is.False);
        Assert.That(host.Publications, Is.Zero); f.SameProfiles(before);
    }

    [AvaloniaTest]
    public async Task ApplyKeepsSettingsOpenAndCancelDiscardsOnlyTheDraftMadeAfterThatPublication()
    {
        using Fixture f = new(); f.Seed(); Dictionary<string, byte[]> before = f.Profiles(); await using Host host = new(f);
        host.Number("_NO_TRANSLATE_MaxCommits", "111000"); host.Click("chkStashUntrackedFiles"); f.SameProfiles(before);
        host.Click("ApplyDesktopSettings");
        await PushProcessTestDriver.UntilAsync(host.Dialog, () => host.Publications == 1 && host.Dialog.Navigation.IsEnabled);
        Assert.That(host.Result.IsCompleted, Is.False); Assert.That(host.Dialog.IsVisible, Is.True);
        Assert.That(f.Desktop.Load().MaxRevisionGraphCommits, Is.EqualTo(111000));
        Assert.That(f.Pull.Load().IncludeUntrackedFilesInAutoStash, Is.True);
        Dictionary<string, byte[]> applied = f.Profiles();
        host.Number("_NO_TRANSLATE_MaxCommits", "222000"); host.Click("chkStashUntrackedFiles");
        host.Click("CancelDesktopSettings"); Assert.That(await host.Result, Is.True, "A prior Apply remains a successful Settings result.");
        Assert.That(host.Publications, Is.EqualTo(1)); f.SameProfiles(applied);
    }

    [AvaloniaTest]
    public async Task AFailedApplyKeepsTheDraftAvailableAndRetryPublishesItWithoutAnIntermediateClose()
    {
        using Fixture f = new(); f.Seed(); Dictionary<string, byte[]> before = f.Profiles(); await using Host host = new(f) { FailPublication = true };
        host.Number("_NO_TRANSLATE_MaxCommits", "111000"); host.Click("ApplyDesktopSettings");
        await PushProcessTestDriver.UntilAsync(host.Dialog, () => host.Publications == 1 && host.Dialog.Navigation.IsEnabled);
        Assert.That(host.Result.IsCompleted, Is.False); Assert.That(host.Dialog.IsVisible, Is.True);
        Assert.That(host.Field<TextBlock>("SettingsNotice").Text, Does.Contain("fixture-general-publication-error"));
        Assert.That(host.Page.Draft.MaxRevisionGraphCommits, Is.EqualTo(111000)); f.SameProfiles(before);
        host.FailPublication = false; host.Click("ApplyDesktopSettings");
        await PushProcessTestDriver.UntilAsync(host.Dialog, () => host.Publications == 2 && host.Dialog.Navigation.IsEnabled);
        Assert.That(host.Result.IsCompleted, Is.False); Assert.That(f.Desktop.Load().MaxRevisionGraphCommits, Is.EqualTo(111000));
        Assert.That(host.Field<TextBlock>("SettingsNotice").Text, Is.Null.Or.Empty);
        host.Click("CancelDesktopSettings"); Assert.That(await host.Result, Is.True); f.NoTemporaryProfiles();
    }

    [AvaloniaTest]
    public async Task EnterInTheHistoryNumberCommitsTheEditorWithoutInvokingTheDefaultOkButton()
    {
        using Fixture f = new(); f.Seed(); Dictionary<string, byte[]> before = f.Profiles(); await using Host host = new(f);
        host.Number("_NO_TRANSLATE_MaxCommits", "111000", leaveEditor: false); Host.Press(host.Dialog, PhysicalKey.Enter); Host.Pump(host.Dialog);
        Assert.That(host.Dialog.IsVisible, Is.True); Assert.That(host.Result.IsCompleted, Is.False); Assert.That(host.Publications, Is.Zero);
        Assert.That(host.Page.Draft.MaxRevisionGraphCommits, Is.EqualTo(111000)); f.SameProfiles(before);
        host.Click("CancelDesktopSettings"); Assert.That(await host.Result, Is.False); f.SameProfiles(before);
    }

    [Test]
    public void OpeningGeneralReadsTheExistingWorkflowProfilesWithoutCreatingAnotherAuthority()
    {
        using Fixture f = new();
        DesktopPreferences desktop = new() { HistoryLimit = 237, MaxRevisionGraphCommits = 123456, RevisionGridQuickSearchTimeout = 2400,
            StartWithRecentWorkingDir = true, ShowStashCount = true, ShowAheadBehindData = false, ShowSubmoduleStatus = true };
        f.Desktop.Save(desktop);
        f.Pull.Save(new(DefaultAction: GitPullAction.FetchPruneAll, IncludeUntrackedFilesInAutoStash: true));
        f.Process.Save(new(true)); f.Clone.SaveStrict(new("/tmp/previous clone parent", false));
        f.Submodules.SaveStrict(new(false, true)); f.History.Save(new() { FollowRenames = false, ExactRenames = true });
        f.Blame.Save(new() { UseHistogramDiffAlgorithm = true });
        Dictionary<string, byte[]> before = f.Profiles();

        GeneralSettingsSession session = new(desktop);

        Assert.That(session.Initial, Is.EqualTo(new GeneralSettingsValues
        {
            MaxRevisionGraphCommits = 123456, RevisionGridQuickSearchTimeout = 2400, StartWithRecentWorkingDir = true,
            ShowStashCount = true, ShowAheadBehindData = false, ShowSubmoduleStatus = true,
            DefaultPullAction = GitPullAction.FetchPruneAll, IncludeUntrackedFilesInAutoStash = true, CloseProcessDialog = true,
            DefaultCloneDestinationPath = "/tmp/previous clone parent", UpdateSubmodulesOnCheckout = false,
            FollowRenamesInFileHistory = false, FollowRenamesInFileHistoryExact = true, UseHistogramDiffAlgorithm = true
        }));
        f.SameProfiles(before);
        Assert.That(Directory.GetFiles(f.ProfileDirectory).Select(Path.GetFileName), Does.Not.Contain("general-settings.json"));
        Assert.That(f.Desktop.Load().HistoryLimit, Is.EqualTo(237), "The existing incremental batch size is not the Windows total-history cap.");
    }

    [Test]
    public void OpeningAnUnusedProfileDoesNotWriteDefaultsOrTouchTheHostConfiguration()
    {
        using Fixture f = new();
        GeneralSettingsSession session = new(new());
        Assert.That(session.Initial, Is.EqualTo(new GeneralSettingsValues()));
        Assert.That(Directory.Exists(f.ProfileDirectory), Is.False);
    }

    [Test]
    public void SaveMergesOnlyChangedGeneralFieldsAndPreservesOtherWorkflowChoices()
    {
        using Fixture f = new(); f.Seed(); DesktopPreferences initial = f.Desktop.Load();
        GeneralSettingsSession session = new(initial);
        f.Desktop.Save(initial with { MaxRevisionGraphCommits = 4321, ShowAheadBehindData = false });
        f.Pull.Save(f.Pull.Load() with { DefaultAction = GitPullAction.FetchAll, DialogAction = GitPullAction.Fetch,
            AutoStash = false, AutoPopStashAfterPull = true });
        f.Clone.SaveStrict(f.Clone.Load() with { Destination = "/tmp/latest clone dialog parent", InitializeSubmodules = true });
        f.Submodules.SaveStrict(new(null, false));
        f.History.Save(f.History.Load() with { FollowRenames = false, FullHistory = false, SimplifyMerges = true });
        f.Blame.Save(f.Blame.Load() with { IgnoreWhitespace = true, DetectMoves = true, DisplayAuthorFirst = true });
        GeneralSettingsValues draft = session.Initial with
        {
            ShowStashCount = true, IncludeUntrackedFilesInAutoStash = true, CloseProcessDialog = true,
            UpdateSubmodulesOnCheckout = false, DefaultCloneDestinationPath = "/tmp/explicit General destination Ω",
            FollowRenamesInFileHistoryExact = true, UseHistogramDiffAlgorithm = true
        };

        GeneralSettingsPublication published = session.Save(draft, initial with { Theme = "System" });

        Assert.That(published.Desktop.Theme, Is.EqualTo("System"), "Another Settings page can publish its deliberate draft in the same operation.");
        Assert.That(published.Desktop.MaxRevisionGraphCommits, Is.EqualTo(4321));
        Assert.That(published.Desktop.ShowAheadBehindData, Is.False); Assert.That(published.Desktop.ShowStashCount, Is.True);
        Assert.That(published.Desktop.HistoryLimit, Is.EqualTo(initial.HistoryLimit));
        Assert.That(f.Pull.Load(), Is.EqualTo(new PullPreferences(DefaultAction: GitPullAction.FetchAll, DialogAction: GitPullAction.Fetch,
            DontConfirmFetchAndPruneAll: true, AutoStash: false, AutoPopStashAfterPull: true, IncludeUntrackedFilesInAutoStash: true)));
        Assert.That(f.Process.Load().CloseProcessDialog, Is.True);
        Assert.That(f.Clone.Load(), Is.EqualTo(new ClonePreferences("/tmp/latest clone dialog parent", true, "/tmp/explicit General destination Ω")));
        Assert.That(f.Submodules.Load(), Is.EqualTo(new SubmoduleCheckoutPreferences(false, false)));
        FileHistoryPreferences history = f.History.Load();
        Assert.That(history.FollowRenames, Is.False); Assert.That(history.ExactRenames, Is.True);
        Assert.That(history.FullHistory, Is.False); Assert.That(history.SimplifyMerges, Is.True);
        Assert.That(history.AdditionalSettings!["future-history-setting"].GetString(), Is.EqualTo("preserved"));
        BlamePreferences blame = f.Blame.Load();
        Assert.That(blame.UseHistogramDiffAlgorithm, Is.True); Assert.That(blame.IgnoreWhitespace, Is.True);
        Assert.That(blame.DetectMoves, Is.True); Assert.That(blame.DetectCopies, Is.True); Assert.That(blame.DisplayAuthorFirst, Is.True);
        Assert.That(published.Values.DefaultPullAction, Is.EqualTo(GitPullAction.FetchAll));
        Assert.That(published.Values.FollowRenamesInFileHistory, Is.False);
        Assert.That(JsonSerializer.Serialize(published.FileHistory), Is.EqualTo(JsonSerializer.Serialize(history)));
        Assert.That(session.Initial, Is.EqualTo(published.Values)); f.NoTemporaryProfiles();
    }

    [Test]
    public void ASecondApplyKeepsNewerWorkflowChoicesAfterTheFirstMergedPublicationWasAccepted()
    {
        using Fixture f = new(); f.Seed(); DesktopPreferences desktop = f.Desktop.Load(); GeneralSettingsSession session = new(desktop);
        f.Pull.Save(f.Pull.Load() with { DefaultAction = GitPullAction.FetchAll });
        GeneralSettingsPublication first = session.Save(session.Initial with { FollowRenamesInFileHistoryExact = true }, desktop);
        Assert.That(first.Values.DefaultPullAction, Is.EqualTo(GitPullAction.FetchAll));
        f.Pull.Save(f.Pull.Load() with { DefaultAction = GitPullAction.Rebase, AutoStash = false });
        f.History.Save(f.History.Load() with { FullHistory = false, SimplifyMerges = true });

        GeneralSettingsPublication second = session.Save(first.Values with { ShowStashCount = true }, first.Desktop);

        Assert.That(second.Values.DefaultPullAction, Is.EqualTo(GitPullAction.Rebase));
        Assert.That(second.Values.ShowStashCount, Is.True); Assert.That(second.Values.FollowRenamesInFileHistoryExact, Is.True);
        Assert.That(f.Pull.Load().AutoStash, Is.False); Assert.That(second.FileHistory.FullHistory, Is.False);
        Assert.That(second.FileHistory.SimplifyMerges, Is.True); f.NoTemporaryProfiles();
    }

    [Test]
    public void ChoosingAskForSubmoduleCheckoutPreservesTheIndependentRememberedConfirmationFlag()
    {
        using Fixture f = new(); f.Seed(); f.Submodules.SaveStrict(new(false, true));
        DesktopPreferences desktop = f.Desktop.Load(); GeneralSettingsSession session = new(desktop);
        GeneralSettingsPublication published = session.Save(session.Initial with { UpdateSubmodulesOnCheckout = null }, desktop);
        Assert.That(f.Submodules.Load().UpdateSubmodulesOnCheckout, Is.Null);
        Assert.That(f.Submodules.Load().DontConfirmUpdateSubmodulesOnCheckout, Is.True);
        Assert.That(published.Values.UpdateSubmodulesOnCheckout, Is.Null);
    }

    [Test]
    public void AnExplicitlyEmptyCloneDefaultRemainsEmptyWhenTheCloneDialogRemembersAnotherDestination()
    {
        using Fixture f = new(); f.Seed(); DesktopPreferences desktop = f.Desktop.Load(); GeneralSettingsSession session = new(desktop);
        Assert.That(session.Initial.DefaultCloneDestinationPath, Is.EqualTo("/tmp/last clone parent"));
        GeneralSettingsPublication published = session.Save(session.Initial with { DefaultCloneDestinationPath = "" }, desktop);
        Assert.That(f.Clone.Load().DefaultDestination, Is.EqualTo(""));
        Assert.That(f.Clone.Load().Destination, Is.EqualTo("/tmp/last clone parent"));
        f.Clone.SaveStrict(f.Clone.Load() with { Destination = "/tmp/new clone dialog destination" });
        Assert.That(new GeneralSettingsSession(published.Desktop).Initial.DefaultCloneDestinationPath, Is.Empty);
        Assert.That(f.Clone.Load().EffectiveDestination, Is.Empty);
    }

    [Test]
    public void RetryAfterAPartialFailureDoesNotRepublishAlreadySavedFieldsOverNewerWorkflowChoices()
    {
        using Fixture f = new(); f.Seed(); DesktopPreferences desktop = f.Desktop.Load(); GeneralSettingsSession session = new(desktop);
        GeneralSettingsValues draft = session.Initial with
        {
            StartWithRecentWorkingDir = true, DefaultPullAction = GitPullAction.Fetch, IncludeUntrackedFilesInAutoStash = true,
            CloseProcessDialog = true, DefaultCloneDestinationPath = "/tmp/retried destination", FollowRenamesInFileHistoryExact = true,
            UseHistogramDiffAlgorithm = true
        };
        File.Delete(f.Clone.FilePath); Directory.CreateDirectory(f.Clone.FilePath);

        Assert.Throws<IOException>(() => session.Save(draft, desktop));

        Assert.That(f.Desktop.Load().StartWithRecentWorkingDir, Is.True);
        Assert.That(f.Pull.Load().DefaultAction, Is.EqualTo(GitPullAction.Fetch)); Assert.That(f.Process.Load().CloseProcessDialog, Is.True);
        Assert.That(f.History.Load().ExactRenames, Is.False, "Publication stopped before the later history profile.");
        Assert.That(f.Blame.Load().UseHistogramDiffAlgorithm, Is.False);
        f.Desktop.Save(f.Desktop.Load() with { StartWithRecentWorkingDir = false, MaxRevisionGraphCommits = 5555 });
        f.Pull.Save(f.Pull.Load() with { DefaultAction = GitPullAction.Rebase, IncludeUntrackedFilesInAutoStash = false, AutoStash = false });
        f.Process.Save(new(false)); Directory.Delete(f.Clone.FilePath);

        GeneralSettingsPublication retried = session.Save(draft, desktop);

        Assert.That(retried.Values.StartWithRecentWorkingDir, Is.False); Assert.That(retried.Values.MaxRevisionGraphCommits, Is.EqualTo(5555));
        Assert.That(retried.Values.DefaultPullAction, Is.EqualTo(GitPullAction.Rebase));
        Assert.That(retried.Values.IncludeUntrackedFilesInAutoStash, Is.False); Assert.That(retried.Values.CloseProcessDialog, Is.False);
        Assert.That(f.Pull.Load().AutoStash, Is.False);
        Assert.That(retried.Values.DefaultCloneDestinationPath, Is.EqualTo("/tmp/retried destination"));
        Assert.That(retried.Values.FollowRenamesInFileHistoryExact, Is.True); Assert.That(retried.Values.UseHistogramDiffAlgorithm, Is.True);
        f.NoTemporaryProfiles();
    }

    [Test]
    [TestCase("pull")]
    [TestCase("process")]
    [TestCase("submodules")]
    [TestCase("clone")]
    [TestCase("history")]
    [TestCase("blame")]
    public void AChangedUnreadableWorkflowProfileIsNotReplacedAndTheDraftCanBeRetriedAfterRepair(string profile)
    {
        using Fixture f = new(); f.Seed(); DesktopPreferences desktop = f.Desktop.Load(); GeneralSettingsSession session = new(desktop);
        (string path, GeneralSettingsValues draft) = profile switch
        {
            "pull" => (f.Pull.FilePath, session.Initial with { IncludeUntrackedFilesInAutoStash = true }),
            "process" => (f.Process.FilePath, session.Initial with { CloseProcessDialog = true }),
            "submodules" => (f.Submodules.FilePath, session.Initial with { UpdateSubmodulesOnCheckout = false }),
            "clone" => (f.Clone.FilePath, session.Initial with { DefaultCloneDestinationPath = "/tmp/repaired clone destination" }),
            "history" => (f.History.FilePath, session.Initial with { FollowRenamesInFileHistoryExact = true }),
            _ => (f.Blame.FilePath, session.Initial with { UseHistogramDiffAlgorithm = true })
        };
        byte[] valid = File.ReadAllBytes(path); File.WriteAllText(path, "{invalid existing workflow profile\r\n");
        byte[] invalid = File.ReadAllBytes(path);
        Assert.That(() => session.Save(draft, desktop), Throws.InstanceOf<JsonException>());
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(invalid)); f.NoTemporaryProfiles();
        File.WriteAllBytes(path, valid);
        GeneralSettingsPublication published = session.Save(draft, desktop);
        Assert.That(profile switch
        {
            "pull" => published.Values.IncludeUntrackedFilesInAutoStash,
            "process" => published.Values.CloseProcessDialog,
            "submodules" => published.Values.UpdateSubmodulesOnCheckout == false,
            "clone" => published.Values.DefaultCloneDestinationPath == "/tmp/repaired clone destination",
            "history" => published.Values.FollowRenamesInFileHistoryExact,
            _ => published.Values.UseHistogramDiffAlgorithm
        }, Is.True);
        f.NoTemporaryProfiles();
    }

    [Test]
    public void ADesktopPublicationFailurePreventsAnyWorkflowProfileFromBeingWritten()
    {
        using Fixture f = new(); f.Seed(); DesktopPreferences desktop = f.Desktop.Load(); GeneralSettingsSession session = new(desktop);
        Dictionary<string, byte[]> before = f.Profiles(); before.Remove(Path.GetFileName(f.Desktop.FilePath));
        File.Delete(f.Desktop.FilePath); Directory.CreateDirectory(f.Desktop.FilePath);
        Assert.Throws<IOException>(() => session.Save(session.Initial with { DefaultPullAction = GitPullAction.Fetch, CloseProcessDialog = true }, desktop));
        Assert.That(Directory.Exists(f.Desktop.FilePath), Is.True);
        foreach ((string name, byte[] bytes) in before) { Assert.That(File.ReadAllBytes(Path.Combine(f.ProfileDirectory, name)), Is.EqualTo(bytes), name); }
        f.NoTemporaryProfiles();
    }

    [Test]
    [TestCase("negative-cap")]
    [TestCase("oversize-cap")]
    [TestCase("short-timeout")]
    [TestCase("oversize-timeout")]
    [TestCase("unknown-pull-action")]
    public void InvalidValuesAreRejectedBeforeAnyProfileIsPublished(string invalid)
    {
        using Fixture f = new(); f.Seed(); DesktopPreferences desktop = f.Desktop.Load(); GeneralSettingsSession session = new(desktop);
        Dictionary<string, byte[]> before = f.Profiles();
        GeneralSettingsValues draft = invalid switch
        {
            "negative-cap" => session.Initial with { MaxRevisionGraphCommits = -1 },
            "oversize-cap" => session.Initial with { MaxRevisionGraphCommits = 1_000_001 },
            "short-timeout" => session.Initial with { RevisionGridQuickSearchTimeout = 99 },
            "oversize-timeout" => session.Initial with { RevisionGridQuickSearchTimeout = 1_000_001 },
            _ => session.Initial with { DefaultPullAction = (GitPullAction)12345 }
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Save(draft, desktop)); f.SameProfiles(before); f.NoTemporaryProfiles();
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly Window _owner = new();
        internal DesktopSettingsDialog Dialog { get; }
        internal Task<bool> Result { get; }
        internal int Publications { get; private set; }
        internal bool FailPublication { get; set; }
        internal GeneralSettingsPage Page => Field<GeneralSettingsPage>("GeneralSettingsPage");
        internal Host(Fixture fixture)
        {
            DesktopPreferences initial = fixture.Desktop.Load(); GeneralSettingsSession session = new(initial, fixture.History.Load());
            Dialog = new(initial, TranslationCatalog.LoadDefault(), false, fonts: new(), fileHistoryPreferences: fixture.History.Load(),
                generalSettings: session.Initial, initialPage: "General", publishSettings: dialog =>
                {
                    Publications++;
                    if (FailPublication) { throw new IOException("fixture-general-publication-error"); }
                    dialog.AcceptGeneralSaved(session.Save(dialog.GeneralSettingsResult, dialog.Result)); return Task.CompletedTask;
                });
            _owner.Show(); Result = Dialog.ShowDialog<bool>(_owner); Pump(Dialog);
        }
        internal T Field<T>(string name) where T : Control => PushProcessTestDriver.Field<T>(Dialog, name);
        internal void Click(string name)
        { Control control = Field<Control>(name); control.BringIntoView(); Pump(Dialog); PushProcessTestDriver.Pointer(control); Pump(Dialog); }
        internal void Number(string name, string text, bool leaveEditor = true)
        {
            NumericUpDown number = Field<NumericUpDown>(name); number.BringIntoView(); Pump(Dialog);
            TextBox editor = number.GetVisualDescendants().OfType<TextBox>().Single();
            PushProcessTestDriver.Pointer(editor); Press(Dialog, PhysicalKey.A, RawInputModifiers.Control); Press(Dialog, PhysicalKey.Backspace);
            Dialog.KeyTextInput(text); if (leaveEditor) { Press(Dialog, PhysicalKey.Tab); } Pump(Dialog);
        }
        internal static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
        { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); Dispatcher.UIThread.RunJobs(); }
        internal static void Pump(TopLevel window)
        { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        public async ValueTask DisposeAsync()
        { try { if (Dialog.IsVisible) { Dialog.Close(false); } await Result.WaitAsync(TimeSpan.FromSeconds(10)); } finally { _owner.Close(); } }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-general-settings-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        internal string ProfileDirectory => Path.Combine(_root, "gitextensions");
        internal PreferencesStore Desktop { get; }
        internal PullPreferencesStore Pull { get; }
        internal ProcessDialogPreferencesStore Process { get; }
        internal ClonePreferencesStore Clone { get; }
        internal SubmoduleCheckoutPreferencesStore Submodules { get; }
        internal FileHistoryPreferencesStore History { get; }
        internal BlamePreferencesStore Blame { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);
            Desktop = new(); Pull = new(); Process = new(); Clone = new(); Submodules = new(); History = new(); Blame = new();
        }
        internal void Seed()
        {
            Desktop.Save(new() { Theme = "Dark", HistoryLimit = 237, RecentRepositories = ["/tmp/existing recent repository"] });
            Pull.Save(new(DefaultAction: GitPullAction.Merge, DialogAction: GitPullAction.Rebase, DontConfirmFetchAndPruneAll: true,
                AutoStash: true, AutoPopStashAfterPull: false));
            Process.Save(new()); Clone.SaveStrict(new("/tmp/last clone parent", false)); Submodules.SaveStrict(new(null, true));
            History.Save(new() { FullHistory = true, AdditionalSettings = new() { ["future-history-setting"] = JsonSerializer.SerializeToElement("preserved") } });
            Blame.Save(new() { IgnoreWhitespace = false, DetectCopies = true });
        }
        internal Dictionary<string, byte[]> Profiles() => !Directory.Exists(ProfileDirectory) ? new() : Directory.GetFiles(ProfileDirectory)
            .ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);
        internal void SameProfiles(Dictionary<string, byte[]> expected)
        {
            Dictionary<string, byte[]> actual = Profiles(); Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
            foreach ((string path, byte[] bytes) in expected) { Assert.That(actual[path], Is.EqualTo(bytes), path); }
        }
        internal void NoTemporaryProfiles() => Assert.That(Directory.GetFiles(ProfileDirectory, "*.tmp"), Is.Empty);
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldXdg);
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }
}

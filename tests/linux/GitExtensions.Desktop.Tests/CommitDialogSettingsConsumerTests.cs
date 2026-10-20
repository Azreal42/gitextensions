using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommitDialogSettingsConsumerTests
{
    [AvaloniaTest]
    public async Task RealApplyChangesTheThreeActualButtonsInPlaceAndReopenUsesTheSavedFlagsWithoutGitSideEffects()
    {
        using GitFixture git = await GitFixture.CreateAsync(); using PortableHelperSettingsFixture f = new();
        File.WriteAllText(git.File, "kept dirty content\n"); string head = await git.Repository.ResolveRevisionAsync("HEAD");
        CommitPreferencesStore store = new(); store.Save(new() { SelectStagedOnMessageFocus = false });
        CommitDialog dialog = new(git.Repository, store); dialog.Show(); await dialog.InitializeAsync();
        try
        {
            TextBox editor = Field<TextBox>(dialog, "Message"); await AvatarUiTestDriver.Edit(dialog, editor, "kept draft Ω");
            TreeView files = Field<TreeView>(dialog, "Unstaged"); object? rows = files.ItemsSource;
            Button[] buttons = new[] { "CommitAndPush", "ResetUnstaged", "ResetAll" }.Select(name => Field<Button>(dialog, name)).ToArray();
            Assert.That(buttons.All(button => button.IsVisible), Is.True);
            (DesktopSettingsDialog settings, Task<bool> closed) = f.Open("Commit");
            foreach (string control in VisibilityControls) { AdvancedSettingsUiDriver.Set(settings, control, false); }
            await AdvancedSettingsUiDriver.Apply(f, settings, 1);
            Assert.That(buttons.All(button => !button.IsVisible && !button.IsEffectivelyVisible), Is.True);
            Assert.That(Field<TextBox>(dialog, "Message"), Is.SameAs(editor)); Assert.That(editor.Text, Is.EqualTo("kept draft Ω"));
            Assert.That(files.ItemsSource, Is.SameAs(rows)); Assert.That(Field<Button>(dialog, "Commit").IsVisible, Is.True);
            foreach (Button button in buttons) { Assert.That(Field<Button>(dialog, button.Name!), Is.SameAs(button)); }
            AdvancedSettingsUiDriver.Set(settings, "chkShowCommitAndPush", true);
            PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await closed;
            Assert.That(buttons.All(button => !button.IsVisible), Is.True, "Cancel cannot publish an unaccepted button draft.");
            await Close(dialog); dialog = new(git.Repository, store); dialog.Show(); await dialog.InitializeAsync();
            Assert.That(new[] { "CommitAndPush", "ResetUnstaged", "ResetAll" }.All(name => !Field<Button>(dialog, name).IsVisible), Is.True);
            (settings, closed) = f.Open("Commit");
            foreach (string control in VisibilityControls) { AdvancedSettingsUiDriver.Set(settings, control, true); }
            await AdvancedSettingsUiDriver.Apply(f, settings, 2);
            Assert.That(new[] { "CommitAndPush", "ResetUnstaged", "ResetAll" }.All(name => Field<Button>(dialog, name).IsVisible), Is.True);
            PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await closed;
            Assert.That(await git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head)); Assert.That(File.ReadAllText(git.File), Is.EqualTo("kept dirty content\n"));
            Assert.That(await git.Repository.GetStashesAsync(), Is.Empty); Assert.That(dialog.HasCommitted, Is.False);
        }
        finally { await Close(dialog); }
    }

    [AvaloniaTest]
    public async Task TheRealCommitPageTogglesTheExistingCompletionControllerAndRetainsDictionaryNoneAndSpellingPolicy()
    {
        using GitFixture git = await GitFixture.CreateAsync(); using PortableHelperSettingsFixture f = new();
        File.WriteAllText(git.PathInRepository("SettingsZebra.cs"), "class SettingsZebra {}\n");
        PreferencesStore preferences = new(); preferences.Save(new() { Dictionary = "none", ProvideAutocompletion = true, MarkIllFormedLinesInCommitMsg = false });
        CommitPreferencesStore commit = new(); commit.Save(new() { SelectStagedOnMessageFocus = false });
        CommitDialog dialog = new(git.Repository, commit); dialog.Show(); await dialog.InitializeAsync();
        try
        {
            CommitCompletionController completion = Private<CommitCompletionController>(dialog, "_completion");
            TextBox editor = Field<TextBox>(dialog, "Message");
            await PushProcessTestDriver.UntilAsync(dialog, () => completion.IsCorpusReady && completion.Corpus.Contains("SettingsZebra.cs"));
            await AvatarUiTestDriver.Edit(dialog, editor, "Sett"); await PushProcessTestDriver.UntilAsync(dialog, () => completion.IsOpen);
            PortableHelperSettingsFixture.Press(dialog, PhysicalKey.Escape);
            (DesktopSettingsDialog settings, Task<bool> closed) = f.Open("Commit", initial: preferences.Load());
            AdvancedSettingsUiDriver.Set(settings, "chkAutocomplete", false); await AdvancedSettingsUiDriver.Apply(f, settings, 1);
            Assert.That(preferences.Load().ProvideAutocompletion, Is.False); Assert.That(completion.IsOpen, Is.False); Assert.That(completion.Corpus, Is.Empty);
            PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await closed;
            PushProcessTestDriver.Pointer(editor); PortableHelperSettingsFixture.Press(dialog, PhysicalKey.Space, RawInputModifiers.Control);
            Assert.That(completion.IsOpen, Is.False); Assert.That(editor.Text, Is.EqualTo("Sett"));
            (settings, closed) = f.Open("Commit", initial: preferences.Load()); AdvancedSettingsUiDriver.Set(settings, "chkAutocomplete", true);
            await AdvancedSettingsUiDriver.Apply(f, settings, 2); PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await closed;
            await PushProcessTestDriver.UntilAsync(dialog, () => completion.IsCorpusReady && completion.Corpus.Contains("SettingsZebra.cs"));
            PushProcessTestDriver.Pointer(editor); PortableHelperSettingsFixture.Press(dialog, PhysicalKey.Space, RawInputModifiers.Control);
            Assert.That(completion.IsOpen, Is.True); Assert.That(Field<TextBox>(dialog, "Message"), Is.SameAs(editor)); Assert.That(dialog.HasCommitted, Is.False);
            Assert.That(preferences.Load().Dictionary, Is.EqualTo("none")); Assert.That(preferences.Load().MarkIllFormedLinesInCommitMsg, Is.False);
            Assert.That(Private<CommitSpellingController>(dialog, "_spelling").Misspellings, Is.Empty);
            PortableHelperSettingsFixture.Press(dialog, PhysicalKey.Escape);
        }
        finally { await Close(dialog); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task SavedPageChoicesReachRealHistoryAmendDraftAndCommittedMessageConsumers(bool enabled)
    {
        using GitFixture git = await GitFixture.CreateAsync(); using PortableHelperSettingsFixture f = new();
        await git.ChangeAsync("second\n", "Second previous message"); await git.ChangeAsync("third\n", "Third previous message");
        (DesktopSettingsDialog settings, Task<bool> settingsClosed) = f.Open("Commit");
        AdvancedSettingsUiDriver.Set(settings, "chkEnsureCommitMessageSecondLineEmpty", enabled);
        AdvancedSettingsUiDriver.Set(settings, "cbRememberAmendCommitState", enabled);
        CommitDialogSettingsPageTests.Number(settings, "2"); await AdvancedSettingsUiDriver.Apply(f, settings, 1);
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await settingsClosed;
        CommitPreferencesStore store = new(); store.Save(store.Load() with { SelectStagedOnMessageFocus = false, CloseAfterEachCommit = false, CloseWhenAllChangesCommitted = false });
        File.WriteAllText(git.File, "new commit payload\n"); await git.Repository.StageAllAsync();
        CommitDialog dialog = new(git.Repository, store); dialog.Show(); await dialog.InitializeAsync();
        try
        {
            await AvatarUiTestDriver.Edit(dialog, Field<TextBox>(dialog, "Message"), "Saved subject\nSaved body");
            PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "Amend")); await PushProcessTestDriver.UntilAsync(dialog, () => !dialog.IsBusy);
            await Close(dialog);
            string amendPath = Path.GetFullPath((await git.GitAsync("rev-parse", "--git-path", "GitExtensions.amend")).StandardOutput.Trim(), git.Repository.Path);
            Assert.That(File.Exists(amendPath), Is.EqualTo(enabled));
            dialog = new(git.Repository, store); dialog.Show(); await dialog.InitializeAsync();
            Assert.That(Field<CheckBox>(dialog, "Amend").IsChecked, Is.EqualTo(enabled));
            Assert.That(Field<TextBox>(dialog, "Message").Text, Is.EqualTo("Saved subject\nSaved body"));
            if (enabled) { PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "Amend")); }
            MenuItem history = Field<Menu>(dialog, "CommitToolbar").Items.OfType<MenuItem>().Single(item => item.Name == "CommitMessageHistory");
            PushProcessTestDriver.Pointer(history);
            await PushProcessTestDriver.UntilAsync(dialog, () => history.IsSubMenuOpen && !history.Items.OfType<MenuItem>().Any(item => item.Name == "HistoryLoading"));
            Assert.That(history.Items.OfType<MenuItem>().Count(item => item.Name == "PreviousMessage"), Is.EqualTo(2));
            PortableHelperSettingsFixture.Press(dialog, PhysicalKey.Escape);
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "Commit")); await PushProcessTestDriver.UntilAsync(dialog, () => !dialog.IsBusy && dialog.HasCommitted);
            Assert.That(dialog.LastError, Is.Null);
            string message = (await git.GitAsync("log", "-1", "--pretty=%B")).StandardOutput;
            Assert.That(message, Does.StartWith(enabled ? "Saved subject\n\nSaved body" : "Saved subject\nSaved body"));
            Assert.That((await git.GitAsync("show", "HEAD:file.txt")).StandardOutput, Is.EqualTo("new commit payload\n"));
        }
        finally { await Close(dialog); }
    }

    [AvaloniaTest]
    public async Task ActualMainApplyCombinesGeneralAndCommitWithoutLosingForeignDictionaryOrVisibilityValues()
    {
        using GitFixture git = await GitFixture.CreateAsync(); string path = new PreferencesStore().FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"Dictionary\":\"none\",\"ProvideAutocompletion\":true,\"MarkIllFormedLinesInCommitMsg\":false,\"future\":{\"literal\":[17,null,\"Ω\"]}}");
        MainWindow main = new(); main.Show();
        try
        {
            await main.OpenRepositoryAsync(git.Repository.Path); await PushProcessTestDriver.UntilAsync(main, () => !main.IsBusy && main.WindowWorkCompletion.IsCompleted);
            PortableHelperSettingsFixture.Press(main, PhysicalKey.Comma, RawInputModifiers.Control);
            await PushProcessTestDriver.UntilAsync(main, () => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any(window => window.IsVisible));
            DesktopSettingsDialog settings = main.OwnedWindows.OfType<DesktopSettingsDialog>().Single(window => window.IsVisible);
            if (settings.GitEnvironmentSession is { } environment) { await environment.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(20)); }
            await PushProcessTestDriver.UntilAsync(settings, () => Field<Button>(settings, "ApplyDesktopSettings").IsEffectivelyEnabled);
            SettingsNavigationTestDriver.Select(settings, "General"); AdvancedSettingsUiDriver.Set(settings, "chkStartWithRecentWorkingDir", true);
            SettingsNavigationTestDriver.Select(settings, "Commit"); AdvancedSettingsUiDriver.Set(settings, "chkAutocomplete", false);
            AdvancedSettingsUiDriver.Set(settings, "chkShowResetAllChanges", false);
            PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
            await PushProcessTestDriver.UntilAsync(settings, () => settings.Navigation.IsEnabled && !new PreferencesStore().Load().ProvideAutocompletion && !new CommitPreferencesStore().Load().ShowResetAllChanges);
            Assert.That(Field<TextBlock>(settings, "SettingsNotice").Text, Is.Null.Or.Empty);
            DesktopPreferences saved = new PreferencesStore().Load(), active = Private<DesktopPreferences>(main, "_preferences");
            Assert.That(saved.StartWithRecentWorkingDir && active.StartWithRecentWorkingDir, Is.True); Assert.That(active.ProvideAutocompletion, Is.False);
            Assert.That(saved.Dictionary, Is.EqualTo("none")); Assert.That(saved.MarkIllFormedLinesInCommitMsg, Is.False);
            Assert.That(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllBytes(path))!["future"], JsonNode.Parse("{\"literal\":[17,null,\"Ω\"]}")), Is.True);
            byte[] accepted = File.ReadAllBytes(path); PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings"));
            await PushProcessTestDriver.UntilAsync(main, () => !settings.IsVisible);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(accepted));
        }
        finally
        {
            if (main.IsVisible) { main.Close(); }
            await PushProcessTestDriver.UntilAsync(main, () => !main.IsVisible && main.WindowWorkCompletion.IsCompleted);
            await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task AnOlderMainCommitSettingsApplyKeepsTheOtherMainCommitEditorsActuallySavedSpellingChoice(bool peerMarks)
    {
        using GitFixture git = await GitFixture.CreateAsync(); git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
        File.WriteAllText(git.File, "kept peer working content\n");
        PreferencesStore preferences = new(); Directory.CreateDirectory(Path.GetDirectoryName(preferences.FilePath)!);
        JsonObject initial = new()
        {
            ["Dictionary"] = "none", ["ProvideAutocompletion"] = true, ["MarkIllFormedLinesInCommitMsg"] = !peerMarks,
            ["CheckForUpdates"] = false, ["future"] = JsonNode.Parse("{\"literal\":[17,null,\"Ω\"]}")
        };
        File.WriteAllText(preferences.FilePath, initial.ToJsonString());
        MainWindow first = new() { Width = 1440, Height = 960 }, peer = new() { Width = 1440, Height = 960 };
        DesktopSettingsDialog? settings = null; CommitDialog? commit = null;
        first.Show(); peer.Show();
        try
        {
            await first.OpenRepositoryAsync(git.Repository.Path); await peer.OpenRepositoryAsync(git.Repository.Path);
            await MainIdle(first); await MainIdle(peer);
            string head = await git.Repository.ResolveRevisionAsync("HEAD"), refs = (await git.GitAsync("show-ref")).StandardOutput;
            byte[] index = File.ReadAllBytes(git.PathInRepository(".git/index"));
            MenuItem tools = Field<MenuItem>(first, "ToolsMenu"); PushProcessTestDriver.Pointer(tools);
            PushProcessTestDriver.Pointer(tools.Items.OfType<MenuItem>().Single(item => item.Name == "settingsToolStripMenuItem"));
            await PushProcessTestDriver.UntilAsync(first, () => first.OwnedWindows.OfType<DesktopSettingsDialog>().Any(window => window.IsVisible));
            settings = first.OwnedWindows.OfType<DesktopSettingsDialog>().Single(window => window.IsVisible);
            if (settings.GitEnvironmentSession is { } environment) { await environment.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(20)); }
            await PushProcessTestDriver.UntilAsync(settings, () => Field<Button>(settings, "ApplyDesktopSettings").IsEffectivelyEnabled);
            SettingsNavigationTestDriver.Select(settings, "Commit");
            AdvancedSettingsUiDriver.Set(settings, "chkAutocomplete", false);
            AdvancedSettingsUiDriver.Set(settings, "chkShowResetAllChanges", false);

            PushProcessTestDriver.Pointer(Field<Button>(peer, "BrowseCommit"));
            await PushProcessTestDriver.UntilAsync(peer, () => peer.OwnedWindows.OfType<CommitDialog>().Any(window => window.IsVisible));
            commit = peer.OwnedWindows.OfType<CommitDialog>().Single(window => window.IsVisible);
            await commit.InitializeAsync(); await PushProcessTestDriver.UntilAsync(commit, () => !commit.IsBusy);
            TextBox editor = Field<TextBox>(commit, "Message"); await AvatarUiTestDriver.Edit(commit, editor, "kept peer draft Ω");
            ContextMenu menu = await SpellingMenu(commit, editor);
            MenuItem marks = menu.Items.OfType<MenuItem>().Single(item => item.Name == "MarkIllFormedLines");
            Assert.That(marks.IsChecked, Is.EqualTo(!peerMarks));
            PushProcessTestDriver.Pointer(marks);
            await PushProcessTestDriver.UntilAsync(commit, () => preferences.Load().MarkIllFormedLinesInCommitMsg == peerMarks);
            Assert.That(Private<DesktopPreferences>(first, "_preferences").MarkIllFormedLinesInCommitMsg, Is.EqualTo(!peerMarks),
                "The older Main really still has its old snapshot before its unrelated Commit-page Apply.");
            Assert.That(settings.IsVisible && commit.IsVisible, Is.True);
            PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
            await PushProcessTestDriver.UntilAsync(settings, () => settings.Navigation.IsEnabled
                && !preferences.Load().ProvideAutocompletion && !new CommitPreferencesStore().Load().ShowResetAllChanges);
            Assert.That(Field<TextBlock>(settings, "SettingsNotice").Text, Is.Null.Or.Empty);
            Assert.That(preferences.Load().MarkIllFormedLinesInCommitMsg, Is.EqualTo(peerMarks));
            Assert.That(Private<DesktopPreferences>(first, "_preferences").MarkIllFormedLinesInCommitMsg, Is.EqualTo(peerMarks));
            Assert.That(Private<CommitCompletionController>(commit, "_completion").IsOpen, Is.False);
            Assert.That(Private<CommitCompletionController>(commit, "_completion").Corpus, Is.Empty);
            Assert.That(Field<TextBox>(commit, "Message"), Is.SameAs(editor)); Assert.That(editor.Text, Is.EqualTo("kept peer draft Ω"));
            Assert.That(Field<Button>(commit, "ResetAll").IsVisible, Is.False);
            menu = await SpellingMenu(commit, editor);
            Assert.That(menu.Items.OfType<MenuItem>().Single(item => item.Name == "MarkIllFormedLines").IsChecked, Is.EqualTo(peerMarks),
                "The actual editor consumer must retain the peer choice after both Settings publications.");
            menu.Close();
            Assert.That(preferences.Load().Dictionary, Is.EqualTo("none"));
            Assert.That(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllBytes(preferences.FilePath))!["future"], initial["future"]), Is.True);
            byte[] saved = File.ReadAllBytes(preferences.FilePath);
            PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings"));
            await PushProcessTestDriver.UntilAsync(first, () => !settings.IsVisible); await MainIdle(first);
            Assert.That(File.ReadAllBytes(preferences.FilePath), Is.EqualTo(saved));
            Assert.That(await git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
            Assert.That((await git.GitAsync("show-ref")).StandardOutput, Is.EqualTo(refs));
            Assert.That(File.ReadAllBytes(git.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(File.ReadAllText(git.File), Is.EqualTo("kept peer working content\n")); Assert.That(commit.HasCommitted, Is.False);
        }
        finally
        {
            if (settings is { IsVisible: true }) { settings.Close(); await PushProcessTestDriver.UntilAsync(first, () => !settings.IsVisible); }
            if (commit is not null) { await Close(commit); }
            foreach (MainWindow main in new[] { peer, first })
            {
                if (main.IsVisible) { main.Close(); }
                await PushProcessTestDriver.UntilAsync(main, () => !main.IsVisible && main.WindowWorkCompletion.IsCompleted);
                await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(20));
            }
        }
    }

    private static async Task MainIdle(MainWindow main)
    {
        await PushProcessTestDriver.UntilAsync(main, () => !main.IsBusy && main.WindowWorkCompletion.IsCompleted);
        await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }
    private static async Task<ContextMenu> SpellingMenu(CommitDialog commit, TextBox editor)
    {
        for (int frame = 0; frame < 10; frame++) { PortableHelperSettingsFixture.Pump(commit); }
        Point point = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), commit)!.Value;
        Assert.That(editor.IsEffectivelyVisible && editor.IsEffectivelyEnabled && new Rect(default, commit.ClientSize).Contains(point)
            && (commit.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(editor) == true,
            Is.True, "The actual peer spelling gesture must hit the native message editor.");
        commit.MouseMove(point); commit.MouseDown(point, MouseButton.Right); commit.MouseUp(point, MouseButton.Right);
        await PushProcessTestDriver.UntilAsync(commit, () => editor.ContextMenu is { IsOpen: true } menu
            && menu.Items.OfType<MenuItem>().Any(item => item.Name == "CommitDictionary" && item.IsEnabled)
            && menu.Items.OfType<MenuItem>().Any(item => item.Name == "MarkIllFormedLines" && item.IsEnabled));
        return editor.ContextMenu!;
    }

    private static readonly string[] VisibilityControls = ["chkShowCommitAndPush", "chkShowResetWorkTreeChanges", "chkShowResetAllChanges"];
    private static T Field<T>(Avalonia.Controls.TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static T Private<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static async Task Close(CommitDialog dialog)
    {
        if (dialog.IsVisible) { dialog.Close(); }
        await PushProcessTestDriver.UntilAsync(dialog, () => !dialog.IsVisible && !dialog.IsBusy);
        await dialog.SpellingCloseCompletion.WaitAsync(TimeSpan.FromSeconds(20));
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AdvancedOptionsVisibilityTests
{
    [Test]
    public void TheWindowsFalseDefaultDoesNotCreateAProfileOrCacheALaterChoice()
    {
        string root = Path.Combine(Path.GetTempPath(), "advanced-visibility-default-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", root);
            Assert.That(AdvancedOptionsVisibility.Capture(), Is.False); Assert.That(Directory.Exists(root), Is.False);
            PreferencesStore store = new(); store.Save(new() { AlwaysShowAdvancedOptions = true }); byte[] bytes = File.ReadAllBytes(store.FilePath);
            Assert.That(AdvancedOptionsVisibility.Capture(), Is.True); Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(bytes));
            store.Save(store.Load() with { AlwaysShowAdvancedOptions = false }); Assert.That(AdvancedOptionsVisibility.Capture(), Is.False);
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous); if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [Test]
    public void ASharingFailureUsesTheCosmeticDefaultWithoutRepairingTheUnreadableProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), "advanced-visibility-locked-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", root); PreferencesStore store = new();
            Write(store.FilePath, "{\"AlwaysShowAdvancedOptions\":true,\"future\":[17,null]}\n"); byte[] bytes = File.ReadAllBytes(store.FilePath);
            using (FileStream exclusive = new(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<IOException>(() => store.Load());
                Assert.That(AdvancedOptionsVisibility.Capture(), Is.False);
            }
            Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(bytes)); Assert.That(AdvancedOptionsVisibility.Capture(), Is.True);
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous); if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task RealAdvancedApplyCancelAndReopenControlTheActualCommandWindowsWithoutChangingOtherSettings(string language, bool dark)
    {
        using OptionalLocksDisabled locks = new(); using RebaseUiFixture git = await RebaseUiFixture.Create();
        await git.Git("remote", "add", "origin", git.Root); using PortableHelperSettingsFixture f = new(language, dark);
        PreferencesStore store = SeedProfiles(); using StateWitness state = new(); string witness = await git.Witness(); byte[] index = File.ReadAllBytes(git.File(".git/index"));
        Dictionary<string, byte[]> original = AdvancedSettingsUiDriver.Profiles();
        await AssertConsumers(git, f.Owner, dark, expanded: false); AdvancedSettingsUiDriver.SameProfiles(original);
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Advanced", initial: store.Load());
        CheckBox choice = Field<CheckBox>(settings, "SettingsAdvancedOptions");
        Assert.That(((TextBlock)choice.Content!).Text, Is.EqualTo(f.Original("AdvancedSettingsPage", "chkAlwaysShowAdvOpt.Text")));
        AdvancedSettingsUiDriver.Set(settings, "SettingsAdvancedOptions", true); AdvancedSettingsUiDriver.SameProfiles(original);
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await completion, Is.False);
        Assert.That(f.Publications, Is.Zero); AdvancedSettingsUiDriver.SameProfiles(original);
        (settings, completion) = f.Open("Advanced", initial: store.Load()); AdvancedSettingsUiDriver.Set(settings, "SettingsAdvancedOptions", true);
        await AdvancedSettingsUiDriver.Apply(f, settings, 1); Assert.That(store.Load().AlwaysShowAdvancedOptions, Is.True);
        Assert.That(settings.Result.AlwaysShowAdvancedOptions, Is.True); Dictionary<string, byte[]> applied = AdvancedSettingsUiDriver.Profiles();
        AdvancedSettingsUiDriver.Set(settings, "SettingsAdvancedOptions", false); PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings"));
        Assert.That(await completion, Is.True); AdvancedSettingsUiDriver.SameProfiles(applied);
        await AssertConsumers(git, f.Owner, dark, expanded: true); AdvancedSettingsUiDriver.SameProfiles(applied);
        (settings, completion) = f.Open("Advanced", initial: store.Load()); Assert.That(Field<CheckBox>(settings, "SettingsAdvancedOptions").IsChecked, Is.True);
        AdvancedSettingsUiDriver.Set(settings, "SettingsAdvancedOptions", false); await AdvancedSettingsUiDriver.Apply(f, settings, 2);
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await completion, Is.True);
        Dictionary<string, byte[]> compact = AdvancedSettingsUiDriver.Profiles(); await AssertConsumers(git, f.Owner, dark, expanded: false);
        AdvancedSettingsUiDriver.SameProfiles(compact); state.Unchanged();
        Assert.That(store.Load().CheckForUpdates, Is.False); Assert.That(store.Load().CheckForReleaseCandidates, Is.True); Assert.That(store.Load().DontShowHelpImages, Is.False);
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllBytes(store.FilePath))!["future"], JsonNode.Parse("{\"literal\":[17,null,\"Ω\"]}")), Is.True);
        foreach ((string path, byte[] bytes) in original.Where(pair => pair.Key != store.FilePath)) { Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), path); }
        Assert.That(await git.Witness(), Is.EqualTo(witness)); Assert.That(File.ReadAllBytes(git.File(".git/index")), Is.EqualTo(index));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task EachWindowCapturesOnceAndLocalExpansionIsIndependentOfExternalChangesAndReloads(bool initiallyExpanded)
    {
        using OptionalLocksDisabled locks = new(); using RebaseUiFixture git = await RebaseUiFixture.Create(); await git.Git("remote", "add", "origin", git.Root);
        PreferencesStore store = SeedProfiles(); store.Save(store.Load() with { AlwaysShowAdvancedOptions = initiallyExpanded }); using StateWitness state = new();
        string witness = await git.Witness();
        foreach (string command in new[] { "Push", "Merge", "Remotes" })
        {
            store.Save(store.Load() with { AlwaysShowAdvancedOptions = initiallyExpanded });
            RepositoryDialog dialog = Create(git, command); git.Windows.Add(dialog);
            store.Save(store.Load() with { AlwaysShowAdvancedOptions = !initiallyExpanded }); Dictionary<string, byte[]> external = AdvancedSettingsUiDriver.Profiles();
            await InitializeAndShow(dialog, git.Owner); AssertExpansion(dialog, command, initiallyExpanded);
            if (dialog is PushDialog push)
            {
                GitPushRequest before = push.ReadOptions();
                if (!initiallyExpanded) { PushProcessTestDriver.Pointer(Field<Button>(push, "PushShowOptions")); }
                AssertExpansion(push, command, true); await push.InitializeAsync(); PortableHelperSettingsFixture.Pump(push); AssertExpansion(push, command, true);
                Assert.That(push.ReadOptions(), Is.EqualTo(before)); Assert.That(push.Attempted, Is.False); Assert.That(push.LastProcess, Is.Null);
            }
            else if (dialog is MergeBranchDialog merge)
            {
                AdvancedSettingsUiDriver.Set(merge, "MergeAdvanced", !initiallyExpanded);
                await merge.InitializeAsync(); PortableHelperSettingsFixture.Pump(merge); AssertExpansion(merge, command, !initiallyExpanded);
                AdvancedSettingsUiDriver.Set(merge, "MergeAdvanced", true);
                foreach (string name in new[] { "MergeUseStrategy", "MergeSquash", "MergeUnrelated", "MergeCustomMessage" }) { AdvancedSettingsUiDriver.Set(merge, name, true); }
                Field<ComboBox>(merge, "MergeStrategy").Text = "ours"; Field<TextBox>(merge, "MergeMessage").Text = "temporary";
                AdvancedSettingsUiDriver.Set(merge, "MergeAdvanced", false); AssertExpansion(merge, command, false);
                Assert.That(merge.ReadOptions(), Is.EqualTo(new GitMergeOptions("main")), "Closing Advanced keeps the existing disarming rules.");
                Assert.That(Field<ComboBox>(merge, "MergeStrategy").Text, Is.Empty);
            }
            else { await dialog.InitializeAsync(); AssertExpansion(dialog, command, initiallyExpanded); }
            Cancel(dialog, command); AdvancedSettingsUiDriver.SameProfiles(external);
            RepositoryDialog reopened = Create(git, command); git.Windows.Add(reopened); await InitializeAndShow(reopened, git.Owner);
            AssertExpansion(reopened, command, !initiallyExpanded); Cancel(reopened, command); AdvancedSettingsUiDriver.SameProfiles(external);
        }
        state.Unchanged(); Assert.That(await git.Witness(), Is.EqualTo(witness));
    }

    [AvaloniaTest]
    [TestCase("{broken literal")]
    [TestCase("{\"AlwaysShowAdvancedOptions\":\"true\",\"future\":[17,null]}")]
    public async Task CorruptCosmeticPreferencesRemainUnrepairedWhileCommandsAndTheExplicitRemotesOverrideStillOpen(string raw)
    {
        using OptionalLocksDisabled locks = new(); using RebaseUiFixture git = await RebaseUiFixture.Create(); await git.Git("remote", "add", "origin", git.Root);
        using PortableHelperSettingsFixture f = new(); PreferencesStore store = SeedProfiles(); Write(store.FilePath, raw);
        Dictionary<string, byte[]> original = AdvancedSettingsUiDriver.Profiles(); using StateWitness state = new(); string witness = await git.Witness();
        await AssertConsumers(git, f.Owner, false, expanded: false);
        RemotesDialog explicitOverride = new(git.Repository, showAdvanced: true); git.Windows.Add(explicitOverride);
        await InitializeAndShow(explicitOverride, f.Owner); AssertExpansion(explicitOverride, "Remotes", true); Cancel(explicitOverride, "Remotes");
        AdvancedSettingsUiDriver.SameProfiles(original);
        AdvancedSettingsSession session = new(); Assert.That(session.LoadErrors.Keys, Does.Contain("preferences.json"));
        Assert.Throws<InvalidDataException>(() => session.Save(session.Initial with { AlwaysShowAdvancedOptions = true }));
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Advanced", initial: new());
        AdvancedSettingsUiDriver.Set(settings, "SettingsAdvancedOptions", true);
        foreach (string action in new[] { "ApplyDesktopSettings", "SaveDesktopSettings" })
        {
            PushProcessTestDriver.Pointer(Field<Button>(settings, action));
            await PushProcessTestDriver.UntilAsync(settings, () => settings.Navigation.IsEnabled && Field<TextBlock>(settings, "SettingsNotice").Text?.Contains("Unable to save", StringComparison.Ordinal) == true);
            Assert.That(settings.IsVisible, Is.True, "A rejected Apply or OK must keep the unreadable-profile draft open.");
            Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo("Advanced")); Assert.That(Field<CheckBox>(settings, "SettingsAdvancedOptions").IsChecked, Is.True);
            Assert.That(Field<Button>(settings, "ApplyDesktopSettings").IsEffectivelyEnabled, Is.True); Assert.That(Field<Button>(settings, "SaveDesktopSettings").IsEffectivelyEnabled, Is.True);
            Assert.That(Field<TextBlock>(settings, "SettingsNotice").Text, Does.Contain("Advanced preferences were unreadable"));
            Assert.That(completion.IsCompleted, Is.False); AdvancedSettingsUiDriver.SameProfiles(original); state.Unchanged();
        }
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); Assert.That(await completion, Is.False);
        AdvancedSettingsUiDriver.SameProfiles(original); state.Unchanged(); Assert.That(await git.Witness(), Is.EqualTo(witness));
    }

    [AvaloniaTest]
    [TestCase("tracking", "main-upstream")]
    [TestCase("mapping", "mapped-main")]
    [TestCase("prefix", "prefix-main")]
    public async Task PushBranchPreselectsTheClickedNonCurrentSourceAndItsOwnRemoteWhileCancelPreservesEveryGitAndProfileByte(string mode, string expectedDestination)
    {
        using OptionalLocksDisabled locks = new(); using RebaseUiFixture git = await RebaseUiFixture.Create();
        string remotesRoot = git.File(".git/advanced-push-remotes");
        foreach (string remote in new[] { "checkout", "origin", "release" })
        {
            string path = Path.Combine(remotesRoot, remote + ".git"); Directory.CreateDirectory(path);
            GitResult result = await new GitRunner().RunAsync(path, ["init", "--bare", "-b", "main"]); Assert.That(result.Success, Is.True, result.StandardError);
            await git.Git("remote", "add", remote, path);
        }
        await git.Git("config", "branch.topic.remote", "checkout"); await git.Git("config", "branch.topic.merge", "refs/heads/topic-upstream");
        await git.Git("config", "branch.main.remote", "release");
        if (mode != "prefix") { await git.Git("config", "branch.main.merge", "refs/heads/main-upstream"); }
        if (mode == "mapping") { await git.Git("config", "remote.release.push", "refs/heads/main:refs/heads/mapped-main"); }
        await git.Git("config", "remote.release.prefix", "prefix-");
        PreferencesStore store = SeedProfiles(); store.Save(store.Load() with { AlwaysShowAdvancedOptions = true }); using StateWitness state = new();
        string witness = await git.Witness(), bareWitness = DirectoryWitness(remotesRoot); byte[] index = File.ReadAllBytes(git.File(".git/index"));
        Dictionary<string, byte[]> profiles = AdvancedSettingsUiDriver.Profiles(); int callbacks = 0;
        PushDialog selected = new(git.Repository, (_, _) => { callbacks++; return Task.CompletedTask; }, initialBranch: "main"); git.Windows.Add(selected);
        await InitializeAndShow(selected, git.Owner); GitPushRequest request = selected.ReadOptions();
        Assert.That(Field<AutoCompleteBox>(selected, "PushBranch").Text, Is.EqualTo("main")); Assert.That(Field<ComboBox>(selected, "PushRemote").SelectedItem, Is.EqualTo("release"));
        Assert.That(request.Source, Is.EqualTo("main")); Assert.That(request.Destination, Is.EqualTo("release")); Assert.That(request.DestinationRef, Is.EqualTo(expectedDestination));
        Assert.That(Field<TextBlock>(selected, "OperationStatus").Text, Does.EndWith(" topic"));
        Assert.That(request.Force, Is.EqualTo(GitPushForce.None)); Assert.That(request.SetUpstream, Is.False); Assert.That(selected.Attempted, Is.False);
        Assert.That(selected.LastProcess, Is.Null); Assert.That(selected.LastRemoteHeadsProcess, Is.Null); Cancel(selected, "Push");
        PushDialog checkout = new(git.Repository); git.Windows.Add(checkout); await InitializeAndShow(checkout, git.Owner);
        Assert.That(checkout.ReadOptions().Source, Is.EqualTo("topic")); Assert.That(checkout.ReadOptions().Destination, Is.EqualTo("checkout"));
        Assert.That(checkout.ReadOptions().DestinationRef, Is.EqualTo("topic-upstream")); Cancel(checkout, "Push");
        Assert.That(callbacks, Is.Zero); AdvancedSettingsUiDriver.SameProfiles(profiles); state.Unchanged();
        Assert.That(await git.Witness(), Is.EqualTo(witness)); Assert.That(File.ReadAllBytes(git.File(".git/index")), Is.EqualTo(index));
        Assert.That(DirectoryWitness(remotesRoot), Is.EqualTo(bareWitness));
    }

    [AvaloniaTest]
    [TestCase("en", false, false)] [TestCase("en", false, true)]
    [TestCase("fr", true, false)] [TestCase("fr", true, true)]
    public async Task ExpandedOptionsRemainRenderedAndReachableAtTheCommandMinimaWithAndWithoutHelp(string language, bool dark, bool hideHelp)
    {
        using OptionalLocksDisabled locks = new(); using RebaseUiFixture git = await RebaseUiFixture.Create(); await git.Git("remote", "add", "origin", git.Root);
        using PortableHelperSettingsFixture f = new(language, dark); PreferencesStore store = SeedProfiles();
        store.Save(store.Load() with { AlwaysShowAdvancedOptions = true, DontShowHelpImages = hideHelp });
        Dictionary<string, byte[]> profiles = AdvancedSettingsUiDriver.Profiles(); string witness = await git.Witness();
        foreach (string command in new[] { "Push", "Merge", "Remotes" })
        {
            RepositoryDialog dialog = Create(git, command); dialog.RequestedThemeVariant = f.Theme; git.Windows.Add(dialog); await InitializeAndShow(dialog, f.Owner);
            dialog.Width = dialog.MinWidth; if (dialog.MinHeight > 0) { dialog.Height = dialog.MinHeight; } PortableHelperSettingsFixture.Pump(dialog);
            AssertExpansion(dialog, command, true);
            if (command == "Merge") { Assert.That(dialog.GetVisualDescendants().OfType<OriginalCommandHelp>().Any(), Is.EqualTo(!hideHelp)); }
            if (dialog is PushDialog push)
            {
                GitPushRequest original = push.ReadOptions();
                foreach (string name in new[] { "PushBranch", "PushRemoteBranch", "PushDestination" }) { AssertEditableLineInside(Field<AutoCompleteBox>(push, name)); }
                foreach (string name in new[] { "PushBranch", "PushRemoteBranch" }) { ExerciseEditor(push, Field<AutoCompleteBox>(push, name)); }
                PushProcessTestDriver.Pointer(Field<RadioButton>(push, "PushToUrl")); PortableHelperSettingsFixture.Pump(push);
                ExerciseEditor(push, Field<AutoCompleteBox>(push, "PushDestination"));
                PushProcessTestDriver.Pointer(Field<RadioButton>(push, "PushToRemote"));
                PushProcessTestDriver.Pointer(Field<TabItem>(push, "PushTagsTab")); PortableHelperSettingsFixture.Pump(push);
                ExerciseEditor(push, Field<AutoCompleteBox>(push, "PushTag"));
                PushProcessTestDriver.Pointer(Field<TabItem>(push, "PushBranchesTab")); PortableHelperSettingsFixture.Pump(push);
                Assert.That(push.ReadOptions(), Is.EqualTo(original)); Assert.That(push.Attempted, Is.False); Assert.That(push.LastProcess, Is.Null);
            }
            string[] names = command switch
            {
                "Push" => ["PushForceWithLease", "ForcePushBranches", "PushReplaceTracking", "PushRecursiveSubmodules"],
                "Merge" => ["MergeAdvanced", "MergeUseStrategy", "MergeSquash", "MergeUnrelated", "MergeAddLog", "MergeCustomMessage"],
                _ => ["RemoteSettingsPrefix", "RemoteSettingsColor"]
            };
            foreach (string name in names)
            {
                Control control = Field<Control>(dialog, name); control.BringIntoView(); PortableHelperSettingsFixture.Pump(dialog); AssertInside(dialog, control);
                foreach (TextBlock caption in control.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(text.Text)))
                {
                    Assert.That(caption.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False, name);
                    Assert.That(caption.Bounds.Height, Is.GreaterThanOrEqualTo(caption.TextLayout.Height - .5), name);
                    Assert.That(caption.Bounds.Width, Is.GreaterThanOrEqualTo(caption.TextLayout.Width - .5), name);
                }
            }
            if (command == "Merge")
            {
                AdvancedSettingsUiDriver.Set(dialog, "MergeCustomMessage", true); Control message = Field<TextBox>(dialog, "MergeMessage");
                message.BringIntoView(); PortableHelperSettingsFixture.Pump(dialog); AssertInside(dialog, message);
                AdvancedSettingsUiDriver.Set(dialog, "MergeAdvanced", false); AdvancedSettingsUiDriver.Set(dialog, "MergeAdvanced", true);
                Field<Control>(dialog, "MergeAdvanced").BringIntoView(); PortableHelperSettingsFixture.Pump(dialog);
            }
            if (command != "Remotes") { AssertInside(dialog, Field<Button>(dialog, command == "Push" ? "CancelPush" : "CancelMerge")); }
            PortableHelperSettingsFixture.Capture(dialog, "advanced-options-" + command + "-" + language + (dark ? "-dark" : "-light") + (hideHelp ? "-without-help.png" : "-with-help.png"));
            Cancel(dialog, command);
        }
        AdvancedSettingsUiDriver.SameProfiles(profiles); Assert.That(await git.Witness(), Is.EqualTo(witness));
    }

    private static async Task AssertConsumers(RebaseUiFixture git, Window owner, bool dark, bool expanded)
    {
        foreach (string command in new[] { "Push", "Merge", "Remotes", "Rebase" })
        {
            RepositoryDialog dialog = Create(git, command); dialog.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; git.Windows.Add(dialog);
            await InitializeAndShow(dialog, owner); AssertExpansion(dialog, command, expanded);
            if (dialog is PushDialog push)
            {
                Assert.That(push.ReadOptions().Force, Is.EqualTo(GitPushForce.None)); Assert.That(push.ReadOptions().SetUpstream, Is.False);
                Assert.That(push.ReadOptions().Recursion, Is.EqualTo(GitPushRecursion.OnDemand)); Assert.That(push.Attempted, Is.False); Assert.That(push.LastProcess, Is.Null);
            }
            if (dialog is MergeBranchDialog merge) { Assert.That(merge.ReadOptions(), Is.EqualTo(new GitMergeOptions("main"))); Assert.That(merge.Result, Is.Null); }
            Assert.That(dialog.HasRepositoryChanges || dialog.ScriptMayHaveChangedRepository, Is.False); Cancel(dialog, command);
        }
    }
    private static RepositoryDialog Create(RebaseUiFixture git, string command) => command switch
    {
        "Push" => new PushDialog(git.Repository), "Merge" => new MergeBranchDialog(git.Repository, "main"),
        "Remotes" => new RemotesDialog(git.Repository), "Rebase" => new RebaseDialog(git.Repository, "main"),
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };
    private static async Task InitializeAndShow(RepositoryDialog dialog, Window owner)
    {
        await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
        _ = dialog.ShowDialog(owner); PortableHelperSettingsFixture.Pump(dialog);
    }
    private static void AssertExpansion(TopLevel dialog, string command, bool expanded)
    {
        switch (command)
        {
            case "Push":
                Assert.That(Field<StackPanel>(dialog, "PushOptionsPanel").IsVisible, Is.EqualTo(expanded));
                Assert.That(Field<Button>(dialog, "PushShowOptions").IsVisible, Is.EqualTo(!expanded)); break;
            case "Merge":
                Assert.That(Field<CheckBox>(dialog, "MergeAdvanced").IsChecked, Is.EqualTo(expanded));
                Assert.That(Field<StackPanel>(dialog, "MergeAdvancedPanel").IsVisible, Is.EqualTo(expanded)); break;
            case "Remotes":
                Assert.That(Field<TextBox>(dialog, "RemoteSettingsPrefix").IsEffectivelyVisible, Is.EqualTo(expanded));
                Assert.That(Field<Button>(dialog, "RemoteSettingsColor").IsEffectivelyVisible, Is.EqualTo(expanded)); break;
            case "Rebase": Assert.That(Field<StackPanel>(dialog, "RebaseOptions").IsEffectivelyVisible, Is.True, "Normal Windows OnShown opens Rebase options with either global value."); break;
        }
    }
    private static void Cancel(Window dialog, string command)
    {
        if (command == "Remotes") { PortableHelperSettingsFixture.Press(dialog, PhysicalKey.Escape); }
        else { PushProcessTestDriver.Pointer(Field<Button>(dialog, command switch { "Push" => "CancelPush", "Merge" => "CancelMerge", _ => "CancelRebase" })); }
        PortableHelperSettingsFixture.Pump(dialog); Assert.That(dialog.IsVisible, Is.False);
    }
    private static T Field<T>(TopLevel dialog, string name) where T : Control => PushProcessTestDriver.Field<T>(dialog, name);
    private static PreferencesStore SeedProfiles()
    {
        PreferencesStore store = new();
        Write(store.FilePath, "{\"AlwaysShowAdvancedOptions\":false,\"CheckForUpdates\":false,\"CheckForReleaseCandidates\":true,\"DontShowHelpImages\":false,\"future\":{\"literal\":[17,null,\"Ω\"]}}\n");
        string directory = Path.GetDirectoryName(store.FilePath)!;
        Write(Path.Combine(directory, "command-help-preferences.json"), "{ \"Pull\": true, \"MergeBranches\": true, \"Rebase\": false, \"future\": true }\n");
        Write(Path.Combine(directory, "push-preferences.json"), "{\"Recursion\":\"OnDemand\",\"DontConfirmNewBranch\":false,\"future\":[17,null]}\n");
        Write(Path.Combine(directory, "merge-preferences.json"), "{\"NoCommit\":false,\"Repositories\":{},\"future\":[17,null]}\n");
        Write(Path.Combine(directory, "rebase-preferences.json"), "{\"AutoStash\":false,\"future\":[17,null]}\n");
        Write(Path.Combine(directory, "unrelated-preferences.json"), "{ \"foreign\": [17,null,\"Ω\"] }\n"); return store;
    }
    private static void Write(string path, string contents) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, contents); }
    private static string DirectoryWitness(string root) => JsonSerializer.Serialize(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private static void ExerciseEditor(Window dialog, AutoCompleteBox input)
    {
        TextBox editor = input.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "PART_TextBox");
        string text = editor.Text ?? ""; Assert.That(text, Is.Not.Empty, input.Name); PushProcessTestDriver.Pointer(editor);
        PortableHelperSettingsFixture.Press(dialog, PhysicalKey.A, RawInputModifiers.Control); AssertEditableLineInside(input);
        dialog.KeyTextInput(text); PortableHelperSettingsFixture.Pump(dialog); Assert.That(editor.Text, Is.EqualTo(text)); AssertEditableLineInside(input);
        PortableHelperSettingsFixture.Press(dialog, PhysicalKey.Tab); Assert.That(dialog.IsVisible, Is.True); AssertEditableLineInside(input);
    }
    private static void AssertEditableLineInside(AutoCompleteBox input)
    {
        TextBox editor = input.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "PART_TextBox");
        TextPresenter presenter = editor.GetVisualDescendants().OfType<TextPresenter>().Single(control => control.Name == "PART_TextPresenter");
        ScrollContentPresenter viewport = editor.GetVisualDescendants().OfType<ScrollContentPresenter>().Single();
        Assert.That(editor.Text, Is.Not.Null.And.Not.Empty, input.Name); Assert.That(input.Bounds.Height, Is.EqualTo(24), input.Name);
        Point withinViewport = presenter.TranslatePoint(default, viewport)!.Value, withinInput = presenter.TranslatePoint(default, input)!.Value;
        Rect first = presenter.TextLayout.HitTestTextPosition(0), last = presenter.TextLayout.HitTestTextPosition(editor.Text!.Length - 1);
        TestContext.WriteLine($"{input.Name}: input={input.Bounds.Size}, editor={editor.Bounds.Size}, padding={editor.Padding}, viewport={viewport.Bounds.Size}, presenter={presenter.Bounds.Size}, origin={withinViewport}, first={first}, last={last}");
        Assert.That(presenter.TextLayout.TextLines.Count, Is.EqualTo(1), input.Name);
        Assert.That(withinViewport.Y + first.Top, Is.GreaterThanOrEqualTo(-.5), input.Name + " first line inside editor viewport");
        Assert.That(withinViewport.Y + last.Bottom, Is.LessThanOrEqualTo(viewport.Bounds.Height + .5), input.Name + " last line inside editor viewport");
        Assert.That(withinInput.Y + first.Top, Is.GreaterThanOrEqualTo(-.5), input.Name + " first line inside the 24 px input");
        Assert.That(withinInput.Y + last.Bottom, Is.LessThanOrEqualTo(input.Bounds.Height + .5), input.Name + " last line inside the 24 px input");
    }
    private static void AssertInside(TopLevel dialog, Control control)
    {
        Assert.That(control.IsEffectivelyVisible && control.IsEffectivelyEnabled, Is.True, control.Name);
        Point origin = control.TranslatePoint(default, dialog)!.Value;
        Assert.That(origin.X, Is.GreaterThanOrEqualTo(-.5), control.Name); Assert.That(origin.Y, Is.GreaterThanOrEqualTo(-.5), control.Name);
        Assert.That(origin.X + control.Bounds.Width, Is.LessThanOrEqualTo(dialog.ClientSize.Width + .5), control.Name);
        Assert.That(origin.Y + control.Bounds.Height, Is.LessThanOrEqualTo(dialog.ClientSize.Height + .5), control.Name);
    }
    private sealed class OptionalLocksDisabled : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("GIT_OPTIONAL_LOCKS");
        internal OptionalLocksDisabled() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        public void Dispose() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", _previous);
    }
    private sealed class StateWitness : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "advanced-options-state-" + Guid.NewGuid().ToString("N"));
        private readonly string _file;
        private readonly byte[] _bytes;
        internal StateWitness()
        {
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", _root); _file = Path.Combine(_root, "gitextensions", "update-check.json");
            Write(_file, "{ \"LastUpdateCheckUtc\": \"2026-10-20T10:00:00.0000000+00:00\", \"future\": [17,null] }\n"); _bytes = File.ReadAllBytes(_file);
        }
        internal void Unchanged() => Assert.That(File.ReadAllBytes(_file), Is.EqualTo(_bytes));
        public void Dispose() { Environment.SetEnvironmentVariable("XDG_STATE_HOME", _previous); if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
    }
}

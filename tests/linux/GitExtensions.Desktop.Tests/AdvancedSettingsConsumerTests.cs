using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AdvancedSettingsConsumerTests
{
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ActualSettingsControlDeterminesWhetherACleanExplicitCheckoutUsesTheExistingDefault(bool alwaysShow)
    {
        using GitFixture git = await GitFixture.CreateAsync(); using PortableHelperSettingsFixture f = new();
        await git.Repository.CreateBranchAsync("target", checkout: false);
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Advanced");
        AdvancedSettingsUiDriver.Set(settings, "chkAlwaysShowCheckoutDlg", alwaysShow); await AdvancedSettingsUiDriver.Apply(f, settings, 1);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); await completion;
        GitReference target = (await git.Repository.GetReferencesAsync()).Single(reference => reference.ShortName == "target");
        CheckoutBranchDialog checkout = new(git.Repository, target); await checkout.InitializeAsync();
        try
        {
            Assert.That(checkout.CanExecuteDefault, Is.EqualTo(!alwaysShow));
            if (!alwaysShow)
            {
                await checkout.ExecuteDefaultAsync(f.Owner, CancellationToken.None);
                Assert.That(checkout.Succeeded, Is.True, checkout.LastError); Assert.That((await git.Repository.GetStatusAsync()).Branch, Is.EqualTo("target"));
            }
            else
            {
                Task result = checkout.ShowDialog(f.Owner); PortableHelperSettingsFixture.Pump(checkout);
                Assert.That(checkout.IsVisible, Is.True); Assert.That((await git.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
                PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(checkout, "CancelCheckoutBranch")); await result;
            }
        }
        finally { if (checkout.IsVisible) { checkout.Close(); } }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ActualSettingsUsesTheRememberedDirtyActionOnlyWhenTheExplicitDefaultFlagIsEnabled(bool useDefault)
    {
        using GitFixture git = await GitFixture.CreateAsync(); using PortableHelperSettingsFixture f = new();
        await git.Repository.CreateBranchAsync("target", checkout: false); new ReferencePreferencesStore().Save(new(CheckoutChanges: LocalChangesAction.Stash));
        File.WriteAllText(git.File, "dirty preserved by advanced setting\n");
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Advanced");
        AdvancedSettingsUiDriver.Set(settings, "chkUseLocalChangesAction", useDefault); await AdvancedSettingsUiDriver.Apply(f, settings, 1);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); await completion;
        Assert.That(new ReferencePreferencesStore().Load().CheckoutChanges, Is.EqualTo(LocalChangesAction.Stash));
        GitReference target = (await git.Repository.GetReferencesAsync()).Single(reference => reference.ShortName == "target");
        CheckoutBranchDialog checkout = new(git.Repository, target); await checkout.InitializeAsync();
        try
        {
            Assert.That(checkout.CanExecuteDefault, Is.EqualTo(useDefault));
            if (useDefault)
            {
                Task execution = checkout.ExecuteDefaultAsync(f.Owner, CancellationToken.None);
                await PushProcessTestDriver.UntilAsync(f.Owner, () => f.Owner.OwnedWindows.Any(window => window.IsVisible && window.Name == "SuppressibleConfirmation"));
                Window question = f.Owner.OwnedWindows.Single(window => window.IsVisible && window.Name == "SuppressibleConfirmation");
                Assert.That(question.Owner, Is.SameAs(f.Owner));
                PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(question, "ConfirmationNo"));
                await execution;
                Assert.That(checkout.Succeeded, Is.True, checkout.LastError); Assert.That((await git.Repository.GetStatusAsync()).Branch, Is.EqualTo("target"));
                Assert.That((await git.Repository.GetStashesAsync()).Count, Is.EqualTo(1));
                Assert.That((await git.GitAsync("stash", "show", "-p", "stash@{0}")).StandardOutput, Does.Contain("dirty preserved by advanced setting"));
            }
            else
            {
                Assert.That((await git.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
                Assert.That(File.ReadAllText(git.File), Is.EqualTo("dirty preserved by advanced setting\n")); Assert.That(await git.Repository.GetStashesAsync(), Is.Empty);
            }
        }
        finally { if (checkout.IsVisible) { checkout.Close(); } }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ActualAdvancedApplyControlsTheExistingAmendAndPushPipelineWithoutAnExternalRemote(bool force)
    {
        using GitFixture git = await GitFixture.CreateAsync(); using PortableHelperSettingsFixture f = new();
        string remote = await git.CreateRemoteAsync(); await git.GitAsync("branch", "--set-upstream-to=origin/main", "main");
        string published = await git.Repository.ResolveRevisionAsync("HEAD");
        CommitPreferencesStore store = new(); store.Save(new() { CloseAfterEachCommit = false, CloseWhenAllChangesCommitted = false, DontConfirmAmend = true });
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Advanced");
        AdvancedSettingsUiDriver.Set(settings, "chkCommitAndPushForcedWhenAmend", force); await AdvancedSettingsUiDriver.Apply(f, settings, 1);
        PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings")); await completion;
        Assert.That(store.Load().DontConfirmAmend, Is.True); Assert.That(store.Load().CommitAndPushForcedWhenAmend, Is.EqualTo(force));
        File.WriteAllText(git.File, "amended content\n"); await git.Repository.StageAllAsync();
        CommitDialog commit = new(git.Repository, store); commit.Show(); await commit.InitializeAsync();
        try
        {
            await PushProcessTestDriver.UntilAsync(commit, () => !commit.IsBusy);
            AdvancedSettingsUiDriver.Set(commit, "Amend", true); await PushProcessTestDriver.UntilAsync(commit, () => !commit.IsBusy);
            f.Edit(commit, "Message", "Replacement commit from Advanced settings");
            PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(commit, "CommitAndPush"));
            if (!force)
            {
                await PushProcessTestDriver.UntilAsync(commit, () => PushProcessTestDriver.Windows(commit).Any(window => window.Name == "PushRejectedConfirmation"));
                Window rejected = PushProcessTestDriver.Windows(commit).Single(window => window.Name == "PushRejectedConfirmation");
                PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(rejected, "PushChoice0"));
                await PushProcessTestDriver.UntilAsync(commit, () => commit.OwnedWindows.OfType<PushDialog>().Any(window => window.IsReady && !window.IsBusy));
                PushDialog push = commit.OwnedWindows.OfType<PushDialog>().Single();
                Assert.That(AdvancedSettingsUiDriver.Field<CheckBox>(push, "PushForceWithLease").IsChecked, Is.False);
                PortableHelperSettingsFixture.Press(push, PhysicalKey.Escape);
            }
            await PushProcessTestDriver.UntilAsync(commit, () => !commit.IsBusy);
            string head = await git.Repository.ResolveRevisionAsync("HEAD");
            string remoteHead = (await git.GitAsync("--git-dir", remote, "rev-parse", "refs/heads/main")).StandardOutput.Trim();
            Assert.That(commit.HasCommitted, Is.True); Assert.That(commit.PushCompleted, Is.EqualTo(force)); Assert.That(head, Is.Not.EqualTo(published));
            Assert.That(remoteHead, Is.EqualTo(force ? head : published));
            Assert.That((await git.GitAsync("rev-list", "--count", "HEAD")).StandardOutput.Trim(), Is.EqualTo("1"), "The changed root commit is a non-fast-forward amend.");
        }
        finally
        {
            if (commit.IsVisible) { commit.Close(); }
            await PushProcessTestDriver.UntilAsync(commit, () => !commit.IsVisible);
            await commit.SpellingCloseCompletion.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    [AvaloniaTest]
    public async Task RealMainApplyCombinesGeneralAndAdvancedWithoutLosingForeignDesktopKeysOrPublishingTheOldSymbol()
    {
        using GitFixture git = await GitFixture.CreateAsync(); string path = new PreferencesStore().FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"AutoNormaliseSymbol\":\"_\",\"future\":{\"literal\":[17,null,\"Ω\"]}}");
        MainWindow main = new(); main.Show();
        try
        {
            await main.OpenRepositoryAsync(git.Repository.Path); await PushProcessTestDriver.UntilAsync(main, () => !main.IsBusy);
            PortableHelperSettingsFixture.Press(main, PhysicalKey.Comma, RawInputModifiers.Control);
            await PushProcessTestDriver.UntilAsync(main, () => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any());
            DesktopSettingsDialog settings = main.OwnedWindows.OfType<DesktopSettingsDialog>().Single();
            if (settings.GitEnvironmentSession is { } environment) { await environment.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(20)); }
            SettingsNavigationTestDriver.Select(settings, "General");
            // Use a real General control as well, so its typed desktop write runs before Advanced's CAS receipt.
            CheckBox startRecent = settings.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "chkStartWithRecentWorkingDir");
            startRecent.BringIntoView(); PortableHelperSettingsFixture.Pump(settings); PushProcessTestDriver.Pointer(startRecent);
            SettingsNavigationTestDriver.Select(settings, "Advanced"); AdvancedSettingsUiDriver.ChooseSymbol(settings, "");
            AdvancedSettingsUiDriver.Set(settings, "chkAlwaysShowCheckoutDlg", true);
            PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "ApplyDesktopSettings"));
            await PushProcessTestDriver.UntilAsync(settings, () => settings.Navigation.IsEnabled && new PreferencesStore().Load().AutoNormaliseSymbol == "");
            Assert.That(AdvancedSettingsUiDriver.Field<TextBlock>(settings, "SettingsNotice").Text, Is.Null.Or.Empty);
            Assert.That(new PreferencesStore().Load().StartWithRecentWorkingDir, Is.True); Assert.That(new ReferencePreferencesStore().Load().AlwaysShowCheckoutDialog, Is.True);
            Assert.That(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllBytes(path))!["future"], JsonNode.Parse("{\"literal\":[17,null,\"Ω\"]}")), Is.True);
            DesktopPreferences active = (DesktopPreferences)typeof(MainWindow).GetField("_preferences", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            Assert.That(active.AutoNormaliseSymbol, Is.Empty, "The accepted Advanced receipt must publish into this Main after General.");
            Assert.That(GitBranchNaming.Normalize("one two", active.AutoNormaliseSymbol), Is.EqualTo("onetwo"));
            byte[] saved = File.ReadAllBytes(path);
            PushProcessTestDriver.Pointer(AdvancedSettingsUiDriver.Field<Button>(settings, "CancelDesktopSettings"));
            await PushProcessTestDriver.UntilAsync(main, () => !settings.IsVisible);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(saved));
        }
        finally
        {
            if (main.IsVisible) { main.Close(); }
            await PushProcessTestDriver.UntilAsync(main, () => !main.IsVisible);
            await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }
}

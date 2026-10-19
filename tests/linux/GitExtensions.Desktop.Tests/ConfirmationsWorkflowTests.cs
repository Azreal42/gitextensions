using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ConfirmationsWorkflowTests
{
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ChangesTabUsesTheSharedDetachedGuardAndCancelPreservesTheActualDraftIndexAndContext(bool createBranch)
    {
        await using Scope scope = await Scope.CreateAsync();
        await scope.Git.GitAsync("switch", "--detach", "HEAD");
        await File.WriteAllTextAsync(scope.Git.File, "staged detached change\n"); await scope.Git.Repository.StageAllAsync();
        MainWindow main = await scope.MainAsync();
        TextBox message = ChangesMessage(main); const string draft = "Changes draft Ω\n\nKeep every line"; Edit(main, message, draft);
        string head = await scope.Head(); string index = (await scope.Git.GitAsync("ls-files", "--stage", "-z")).StandardOutput;
        ClickChangesCommit(message);
        Window prompt = await Child(main, "CommitBranchChoice");
        Assert.That(prompt.Owner, Is.SameAs(main));
        Click(prompt, "DetachedCancel"); await Idle(main);
        Assert.That(message.Text, Is.EqualTo(draft)); Assert.That(await scope.Head(), Is.EqualTo(head));
        Assert.That((await scope.Git.GitAsync("ls-files", "--stage", "-z")).StandardOutput, Is.EqualTo(index));
        Assert.That((await scope.Git.Repository.GetStatusAsync()).IsDetached, Is.True);
        Assert.That(main.LastException, Is.Null);
        ClickChangesCommit(message); prompt = await Child(main, "CommitBranchChoice");
        if (createBranch)
        {
            Click(prompt, "DetachedCreate");
            await PushProcessTestDriver.UntilAsync(main, () => main.OwnedWindows.OfType<CreateBranchDialog>().Any(dialog => !dialog.IsBusy));
            CreateBranchDialog create = main.OwnedWindows.OfType<CreateBranchDialog>().Single();
            Edit(create, Field<TextBox>(create, "CreateBranchName"), "draft-branch");
            Assert.That(Field<CheckBox>(create, "CheckoutAfterCreate").IsChecked, Is.True);
            Click(create, "CreateBranch");
        }
        else { Click(prompt, "DetachedContinue"); }
        await Idle(main);
        Assert.That(main.LastException, Is.Null); Assert.That(await scope.Head(), Is.Not.EqualTo(head));
        Assert.That((await scope.Git.GitAsync("log", "-1", "--format=%B")).StandardOutput.TrimEnd(), Is.EqualTo(draft));
        Assert.That(message.Text, Is.Empty);
        GitStatus status = await scope.Git.Repository.GetStatusAsync();
        Assert.That(status.IsDetached, Is.EqualTo(!createBranch));
        if (createBranch) { Assert.That(status.Branch, Is.EqualTo("draft-branch")); }
    }

    [AvaloniaTest]
    public async Task ChangesTabReadsNewlyAppliedDetachedPreferenceBeforeBeforeCommitAndLeavesTheCheckoutDetached()
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.Git.GitAsync("switch", "--detach", "HEAD");
        await File.WriteAllTextAsync(scope.Git.File, "staged detached change\n"); await scope.Git.Repository.StageAllAsync();
        MainWindow main = await scope.MainAsync(); TextBox message = ChangesMessage(main); Edit(main, message, "Explicit detached commit");
        ConfirmationsSettingsSession settings = new(); settings.Save(settings.Initial with { DontConfirmCommitIfNoBranch = true });
        string before = await scope.Head(); ClickChangesCommit(message); await Idle(main);
        Assert.That(main.LastException, Is.Null); Assert.That(await scope.Head(), Is.Not.EqualTo(before));
        Assert.That((await scope.Git.Repository.GetStatusAsync()).IsDetached, Is.True);
        Assert.That(main.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase("Commit")] [TestCase("ResetSoft")]
    public async Task BothAmendActionsReadTheSharedPreferenceAfterTheCommitWindowHasOpened(string action)
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.Git.ChangeAsync("second\n", "Second commit");
        string second = await scope.Head(); string first = (await scope.Git.GitAsync("rev-parse", "HEAD^")).StandardOutput.Trim();
        await File.WriteAllTextAsync(scope.Git.File, "third staged\n"); await scope.Git.Repository.StageAllAsync();
        new CommitPreferencesStore().Save(new() { CloseAfterEachCommit = false, CloseWhenAllChangesCommitted = false });
        CommitDialog commit = new(scope.Git.Repository); scope.Track(commit); commit.Show(); await commit.InitializeAsync();
        PushProcessTestDriver.Pointer(Field<CheckBox>(commit, "Amend"));
        await PushProcessTestDriver.UntilAsync(commit, () => !commit.IsBusy);
        if (action == "Commit") { Edit(commit, Field<TextBox>(commit, "Message"), "Amended via shared flag"); }
        ConfirmationsSettingsSession settings = new(); settings.Save(settings.Initial with { DontConfirmAmend = true });
        Click(commit, action); await PushProcessTestDriver.UntilAsync(commit, () => !commit.IsBusy);
        Assert.That(commit.LastError, Is.Null); Assert.That(commit.OwnedWindows, Is.Empty);
        if (action == "ResetSoft")
        {
            Assert.That(await scope.Head(), Is.EqualTo(first));
            Assert.That((await scope.Git.Repository.GetStatusAsync()).Changes.Any(change => change.IsStaged), Is.True);
        }
        else
        {
            Assert.That(await scope.Head(), Is.Not.EqualTo(second));
            Assert.That((await scope.Git.GitAsync("rev-list", "--count", "HEAD")).StandardOutput.Trim(), Is.EqualTo("2"));
            Assert.That((await scope.Git.GitAsync("log", "-1", "--format=%s")).StandardOutput.Trim(), Is.EqualTo("Amended via shared flag"));
        }
        Assert.That(new CommitPreferencesStore().Load().DontConfirmAmend, Is.True);
    }

    [AvaloniaTest]
    public async Task DecliningAmendWithDontShowAgainRemembersTheBoolWithoutConsumingTheDraftOrRewritingHistory()
    {
        await using Scope scope = await Scope.CreateAsync();
        new CommitPreferencesStore().Save(new() { CloseAfterEachCommit = false, CloseWhenAllChangesCommitted = false });
        CommitDialog commit = new(scope.Git.Repository); scope.Track(commit); commit.Show(); await commit.InitializeAsync();
        PushProcessTestDriver.Pointer(Field<CheckBox>(commit, "Amend")); await PushProcessTestDriver.UntilAsync(commit, () => !commit.IsBusy);
        Edit(commit, Field<TextBox>(commit, "Message"), "Keep this amend draft"); string head = await scope.Head();
        Click(commit, "Commit"); Window question = await Child(commit, "SuppressibleConfirmation");
        PushProcessTestDriver.Pointer(Field<CheckBox>(question, "ConfirmationDontShowAgain")); Click(question, "ConfirmationNo");
        await PushProcessTestDriver.UntilAsync(commit, () => !commit.IsBusy);
        Assert.That(await scope.Head(), Is.EqualTo(head)); Assert.That(commit.HasCommitted, Is.False);
        Assert.That(Field<TextBox>(commit, "Message").Text, Is.EqualTo("Keep this amend draft"));
        Assert.That(new CommitPreferencesStore().Load().DontConfirmAmend, Is.True);
    }

    [AvaloniaTest]
    [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
    public async Task ConflictChoicesControlOnlyResolverEntryAndCommitOfferWhileFileReplacementStaysProtected(bool suppressResolve, bool suppressCommit)
    {
        await using Scope scope = await Scope.CreateAsync();
        await scope.Git.GitAsync("switch", "--create", "incoming"); await scope.Git.ChangeAsync("incoming\n", "Incoming");
        await scope.Git.GitAsync("switch", "main"); await scope.Git.ChangeAsync("local\n", "Local");
        GitResult merge = await new GitRunner().RunAsync(scope.Git.Repository.Path, ["merge", "--no-edit", "incoming"]);
        Assert.That(merge.ExitCode, Is.EqualTo(1)); Assert.That(await scope.Git.Repository.GetConflictsAsync(), Has.Count.EqualTo(1));
        MainWindow main = await scope.MainAsync(); string head = await scope.Head();
        ConfirmationsSettingsSession settings = new(); settings.Save(settings.Initial with
        { DontConfirmResolveConflicts = suppressResolve, DontConfirmCommitAfterConflictsResolved = suppressCommit });
        Task workflow = (Task)typeof(MainWindow).GetMethod("ResolveWorkflowConflictsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(main, [main, scope.Git.Repository, true, CancellationToken.None])!;
        if (!suppressResolve) { Window question = await Child(main, "SuppressibleConfirmation"); Click(question, "ConfirmationYes"); }
        await PushProcessTestDriver.UntilAsync(main, () => main.OwnedWindows.OfType<ResolveConflictsDialog>().Any(dialog => !dialog.IsBusy));
        ResolveConflictsDialog resolver = main.OwnedWindows.OfType<ResolveConflictsDialog>().Single();
        Assert.That(resolver.Owner, Is.SameAs(main));
        string unmerged = (await scope.Git.GitAsync("ls-files", "--unmerged", "-z")).StandardOutput;
        Click(resolver, "ResolveOurs");
        await PushProcessTestDriver.UntilAsync(main, () => resolver.OwnedWindows.Any());
        Window replacement = resolver.OwnedWindows.Single();
        Assert.That(replacement.Name, Is.Not.EqualTo("SuppressibleConfirmation"), "The file replacement safety question is a different action.");
        PushProcessTestDriver.Pointer(replacement.GetVisualDescendants().OfType<Button>().Single(button => button.IsCancel));
        await PushProcessTestDriver.UntilAsync(main, () => !resolver.IsBusy);
        Assert.That((await scope.Git.GitAsync("ls-files", "--unmerged", "-z")).StandardOutput, Is.EqualTo(unmerged));
        await File.WriteAllTextAsync(scope.Git.File, "resolved deliberately\n"); Click(resolver, "ResolveManual");
        await PushProcessTestDriver.UntilAsync(main, () => !resolver.IsBusy && resolver.Conflicts.Count == 0);
        Click(resolver, "CloseConflicts");
        if (suppressCommit)
        {
            await PushProcessTestDriver.UntilAsync(main, () => main.OwnedWindows.OfType<CommitDialog>().Any(dialog => !dialog.IsBusy));
            CommitDialog commit = main.OwnedWindows.OfType<CommitDialog>().Single();
            Assert.That(commit.HasCommitted, Is.False); Assert.That(await scope.Head(), Is.EqualTo(head)); commit.Close();
        }
        else
        {
            Window offer = await Child(main, "SuppressibleConfirmation");
            Assert.That(Field<TextBlock>(offer, "ConfirmationMessage").Text, Does.Contain("All merge conflicts are resolved"));
            Click(offer, "ConfirmationNo");
        }
        await PushProcessTestDriver.CompleteAsync(workflow, main);
        Assert.That(await scope.Head(), Is.EqualTo(head));
        Assert.That((await scope.Git.GitAsync("rev-parse", "MERGE_HEAD")).Success, Is.True);
        Assert.That(await scope.Git.Repository.GetConflictsAsync(), Is.Empty);
        Assert.That((await scope.Git.Repository.GetStatusAsync()).Changes.Any(change => change.IsStaged), Is.True);
    }

    [AvaloniaTest]
    public async Task AnAlreadyOpenPushReadsBothAppliedFlagsAndItsRecursionWritePreservesTheNewerPreferences()
    {
        await using Scope scope = await Scope.CreateAsync(); string remote = await scope.Git.CreateRemoteAsync();
        await scope.Git.GitAsync("switch", "--create", "new-topic");
        PushDialog push = new(scope.Git.Repository); scope.Track(push); push.Show(); await push.InitializeAsync();
        Assert.That(push.IsReady, Is.True);
        ConfirmationsSettingsSession settings = new(); settings.Save(settings.Initial with
        { DontConfirmPushNewBranch = true, DontConfirmAddTrackingRef = true });
        PushPreferencesStore store = new(); store.Save(store.Load() with { RejectedAction = GitPullAction.None });
        Click(push, "ExecutePush");
        await PushProcessTestDriver.UntilAsync(push, () => !push.IsBusy);
        Assert.That(push.Succeeded, Is.True, push.LastError);
        Assert.That(PushProcessTestDriver.Windows(push).Any(window => window.Name is "PushNewBranchConfirmation" or "PushTrackingConfirmation"), Is.False);
        Assert.That((await scope.Git.GitAsync("config", "branch.new-topic.remote")).StandardOutput.Trim(), Is.EqualTo("origin"));
        Assert.That((await scope.Git.GitAsync("config", "branch.new-topic.merge")).StandardOutput.Trim(), Is.EqualTo("refs/heads/new-topic"));
        GitResult remoteHead = await new GitRunner().RunAsync(remote, ["rev-parse", "refs/heads/new-topic"]);
        Assert.That(remoteHead.StandardOutput.Trim(), Is.EqualTo(await scope.Head()));
        Assert.That(store.Load().DontConfirmNewBranch && store.Load().DontConfirmAddTracking, Is.True);
        Assert.That(store.Load().RejectedAction, Is.EqualTo(GitPullAction.None));
    }

    [AvaloniaTest]
    public async Task ChangesTabSkipsTheDetachedWarningDuringAnActualPausedRebase()
    {
        await using Scope scope = await Scope.CreateAsync(); await scope.Git.ChangeAsync("second\n", "Second commit");
        GitResult pause = await new GitRunner().RunAsync(scope.Git.Repository.Path, ["rebase", "--exec", "false", "HEAD~1"]);
        Assert.That(pause.ExitCode, Is.Not.Zero, "The actual exec command must pause Git rather than merely create a directory.");
        Assert.That((await scope.Git.Repository.GetRebaseStateAsync()).IsActive, Is.True);
        Assert.That((await scope.Git.Repository.GetStatusAsync()).IsDetached, Is.True);
        await File.WriteAllTextAsync(scope.Git.File, "staged during rebase\n"); await scope.Git.Repository.StageAllAsync();
        MainWindow main = await scope.MainAsync(); TextBox message = ChangesMessage(main); Edit(main, message, "Rebase checkpoint commit");
        string before = await scope.Head(); ClickChangesCommit(message); await Idle(main);
        Assert.That(main.LastException, Is.Null); Assert.That(await scope.Head(), Is.Not.EqualTo(before));
        Assert.That(main.OwnedWindows, Is.Empty); Assert.That((await scope.Git.Repository.GetRebaseStateAsync()).IsActive, Is.True);
    }

    [AvaloniaTest]
    public async Task ClosingTheSuppressibleQuestionOwnerDrainsWithoutRememberingOrAccepting()
    {
        using PortableHelperSettingsFixture f = new(); int remembered = 0;
        Task<bool> decision = ConfirmationPrompt.ShowAsync(f.Owner, "Owned decision", "Continue?", false, () => remembered++, CancellationToken.None);
        Window prompt = await Child(f.Owner, "SuppressibleConfirmation");
        PushProcessTestDriver.Pointer(Field<CheckBox>(prompt, "ConfirmationDontShowAgain"));
        f.Owner.Close(); PortableHelperSettingsFixture.Pump(prompt);
        Assert.That(await decision.WaitAsync(TimeSpan.FromSeconds(10)), Is.False);
        Assert.That(prompt.IsVisible, Is.False); Assert.That(remembered, Is.Zero);
    }

    [AvaloniaTest]
    public async Task StagedChangesWithoutAnyConflictsDoNotInventACommitAfterResolutionOffer()
    {
        await using Scope scope = await Scope.CreateAsync();
        await File.WriteAllTextAsync(scope.Git.File, "ordinary staged change\n"); await scope.Git.Repository.StageAllAsync();
        MainWindow main = await scope.MainAsync(); string before = await scope.Head();
        Task workflow = (Task)typeof(MainWindow).GetMethod("ResolveWorkflowConflictsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(main, [main, scope.Git.Repository, true, CancellationToken.None])!;
        await workflow.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(main.OwnedWindows, Is.Empty); Assert.That(await scope.Head(), Is.EqualTo(before));
    }

    private static TextBox ChangesMessage(MainWindow main)
    {
        TabControl tabs = Private<TabControl>(main, "_tabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "ChangesTab");
        PortableHelperSettingsFixture.Pump(main);
        TextBox message = Private<TextBox>(main, "_commitMessage"); Assert.That(message.IsEffectivelyVisible, Is.True); return message;
    }
    private static void ClickChangesCommit(TextBox message)
    {
        StackPanel panel = message.GetVisualAncestors().OfType<StackPanel>().First(parent => parent.Children.Contains(message));
        PushProcessTestDriver.Pointer(panel.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Commit"));
    }
    private static T Private<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static void Click(Window window, string name) => PushProcessTestDriver.Pointer(Field<Button>(window, name));
    private static async Task<Window> Child(Window owner, string name)
    { await PushProcessTestDriver.UntilAsync(owner, () => owner.OwnedWindows.Any(window => window.Name == name)); return owner.OwnedWindows.Single(window => window.Name == name); }
    private static Task Idle(MainWindow main) => PushProcessTestDriver.UntilAsync(main, () => !main.IsBusy);
    private static void Edit(Window window, TextBox text, string value)
    {
        text.BringIntoView(); PortableHelperSettingsFixture.Pump(window); PushProcessTestDriver.Pointer(text);
        PortableHelperSettingsFixture.Press(window, PhysicalKey.A, RawInputModifiers.Control); window.KeyTextInput(value); PortableHelperSettingsFixture.Pump(window);
        Assert.That(text.Text, Is.EqualTo(value));
    }

    private sealed class Scope(GitFixture git) : IAsyncDisposable
    {
        internal GitFixture Git { get; } = git;
        private readonly List<Window> _windows = [];
        internal static async Task<Scope> CreateAsync() => new(await GitFixture.CreateAsync());
        internal void Track(Window window) => _windows.Add(window);
        internal Task<string> Head() => Git.Repository.ResolveRevisionAsync("HEAD");
        internal async Task<MainWindow> MainAsync()
        { MainWindow main = new() { Width = 1280, Height = 800 }; Track(main); main.Show(); await main.OpenRepositoryAsync(Git.Repository.Path); return main; }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window window in _windows.AsEnumerable().Reverse())
                {
                    if (window.IsVisible) { window.Close(); }
                    await PushProcessTestDriver.UntilAsync(window, () => !window.IsVisible);
                    if (window is MainWindow main) { await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(20)); }
                    if (window is CommitDialog commit) { await commit.SpellingCloseCompletion.WaitAsync(TimeSpan.FromSeconds(20)); }
                }
            }
            finally { Git.Dispose(); }
        }
    }
}

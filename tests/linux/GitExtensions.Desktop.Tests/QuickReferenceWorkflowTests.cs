using System.Diagnostics;
using System.Reflection;
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
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class QuickReferenceWorkflowTests
{
    [AvaloniaTest]
    public async Task ZeroCandidatesDoNothingAndOneCurrentBranchRenamesDirectlyWithoutAChooser()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        string before = await Snapshot(f); History(f).Focus();
        Press(f.Window, PhysicalKey.Delete); await Idle(f.Window);
        Assert.That(f.Window.OwnedWindows, Is.Empty, "The only local branch is current and cannot be deleted.");
        Press(f.Window, PhysicalKey.F2);
        RenameBranchDialog rename = await Child<RenameBranchDialog>(f.Window);
        Assert.That(f.Window.OwnedWindows.OfType<QuickGitRefSelectorDialog>(), Is.Empty);
        Assert.That(Field<TextBox>(rename, "RenameBranchName").Text, Is.EqualTo("main"));
        rename.Close(); await Idle(f.Window);
        Assert.That(History(f).IsKeyboardFocusWithin, Is.True);
        Assert.That(await Snapshot(f), Is.EqualTo(before));

        await Git(f, "commit", "--allow-empty", "-m", "Unreferenced older commit");
        await f.Window.OpenRepositoryAsync(f.Repository);
        Select(f, "Initial commit");
        Press(f.Window, PhysicalKey.F2); await Idle(f.Window);
        Press(f.Window, PhysicalKey.Delete); await Idle(f.Window);
        Assert.That(f.Window.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task MultipleLocalBranchesUseThePickerThenRenameOnlyTheChosenLiteralBranch()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        await Git(f, "branch", "alpha"); await Git(f, "branch", "zeta");
        string before = await Snapshot(f); History(f).Focus();
        Press(f.Window, PhysicalKey.F2); QuickGitRefSelectorDialog picker = await Child<QuickGitRefSelectorDialog>(f.Window);
        Assert.That(await Snapshot(f), Is.EqualTo(before));
        Choose(picker, "refs/heads/zeta");
        RenameBranchDialog rename = await Child<RenameBranchDialog>(f.Window);
        Assert.That(Field<TextBox>(rename, "RenameBranchName").Text, Is.EqualTo("zeta"));
        Field<TextBox>(rename, "RenameBranchName").Text = "renamed/été";
        PushProcessTestDriver.Pointer(Field<Button>(rename, "RenameBranch"));
        await Until(() => !rename.IsVisible); await Idle(f.Window);
        string refs = (await Git(f, "for-each-ref", "--format=%(refname)")).StandardOutput;
        Assert.That(refs, Does.Contain("refs/heads/renamed/été\n").And.Contain("refs/heads/alpha\n").And.Contain("refs/heads/main\n"));
        Assert.That(refs, Does.Not.Contain("refs/heads/zeta\n"));
        Assert.That((await Git(f, "symbolic-ref", "--short", "HEAD")).StandardOutput.Trim(), Is.EqualTo("main"));
        Assert.That((await Git(f, "status", "--porcelain")).StandardOutput, Is.Empty);
        Assert.That(History(f).IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    public async Task MixedReferencesRouteToTheirExistingTypedDialogsAndKeepGitUnchangedUntilConfirmed()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        await Git(f, "branch", "topic"); await Git(f, "remote", "add", "origin", f.Repository);
        await Git(f, "update-ref", "refs/remotes/origin/topic", "HEAD");
        await Git(f, "tag", "topic"); await Git(f, "tag", "-a", "annotated", "-m", "Annotated tag");
        string before = await Snapshot(f);
        foreach ((string name, Type expected) in new[]
        {
            ("refs/heads/topic", typeof(DeleteBranchDialog)), ("refs/remotes/origin/topic", typeof(DeleteRemoteBranchDialog)),
            ("refs/tags/topic", typeof(DeleteTagDialog)), ("refs/tags/annotated", typeof(DeleteTagDialog))
        })
        {
            History(f).Focus(); Press(f.Window, PhysicalKey.Delete);
            QuickGitRefSelectorDialog picker = await Child<QuickGitRefSelectorDialog>(f.Window);
            Assert.That(References(picker).Select(reference => reference.Name), Is.EquivalentTo(new[]
                { "refs/heads/topic", "refs/remotes/origin/topic", "refs/tags/topic", "refs/tags/annotated" }));
            Assert.That(await Snapshot(f), Is.EqualTo(before)); Choose(picker, name);
            await Until(() => f.Window.OwnedWindows.Any(window => window.GetType() == expected));
            RepositoryDialog target = (RepositoryDialog)f.Window.OwnedWindows.Single(window => window.GetType() == expected);
            await Until(() => !target.IsBusy); Pump(target);
            Assert.That(target.LastError, Is.Null, target.LastError);
            if (target is DeleteBranchDialog branch) { Assert.That(branch.ReadOptions().Names, Is.EqualTo(new[] { "topic" })); }
            if (target is DeleteRemoteBranchDialog remote) { Assert.That(remote.SelectionText, Is.EqualTo("origin/topic")); }
            if (target is DeleteTagDialog)
            { Assert.That((Field<ComboBox>(target, "DeleteTagSelection").SelectedItem as GitReference)?.Name, Is.EqualTo(name)); }
            target.Close(); await Idle(f.Window);
            Assert.That(await Snapshot(f), Is.EqualTo(before));
            Assert.That(History(f).IsKeyboardFocusWithin, Is.True);
        }
    }

    [AvaloniaTest]
    public async Task DeletingAnAnnotatedTagFromThePickerPreservesItsHomonymousBranchAndCommit()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        await Git(f, "branch", "release"); await Git(f, "tag", "-a", "release", "-m", "Release");
        string head = (await Git(f, "rev-parse", "HEAD")).StandardOutput.Trim();
        History(f).Focus(); Press(f.Window, PhysicalKey.Delete);
        QuickGitRefSelectorDialog picker = await Child<QuickGitRefSelectorDialog>(f.Window);
        Assert.That(References(picker).Select(reference => reference.ShortName), Is.EqualTo(new[] { "release", "release" }));
        Choose(picker, "refs/tags/release"); DeleteTagDialog dialog = await Child<DeleteTagDialog>(f.Window);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "DeleteTag"));
        await Until(() => !dialog.IsVisible); await Idle(f.Window);
        Assert.That((await Git(f, "rev-parse", "refs/heads/release")).StandardOutput.Trim(), Is.EqualTo(head));
        GitResult missing = await f.Git.RunAsync(f.Repository, ["show-ref", "--verify", "refs/tags/release"]);
        Assert.That(missing.Success, Is.False);
        Assert.That((await Git(f, "rev-parse", "HEAD")).StandardOutput.Trim(), Is.EqualTo(head));
        Assert.That((await Git(f, "status", "--porcelain")).StandardOutput, Is.Empty);
    }

    [AvaloniaTest]
    public async Task EscapeWindowCloseAndGroupHeaderAcceptanceDoNotOpenAnActionAndRestoreTheGrid()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n"); await Git(f, "branch", "topic");
        string before = await Snapshot(f); object source = History(f).ItemsSource!;
        for (int mode = 0; mode < 3; mode++)
        {
            History(f).Focus(); Press(f.Window, PhysicalKey.F2); QuickGitRefSelectorDialog picker = await Child<QuickGitRefSelectorDialog>(f.Window);
            if (mode == 0) { Press(picker, PhysicalKey.Escape); }
            else if (mode == 1) { picker.Close(); }
            else { Field<ListBox>(picker, "lbxRefs").SelectedIndex = 0; Press(picker, PhysicalKey.Enter); }
            await Until(() => !picker.IsVisible); await Idle(f.Window);
            Assert.That(f.Window.OwnedWindows, Is.Empty);
            Assert.That(History(f).IsKeyboardFocusWithin, Is.True);
            Assert.That(History(f).ItemsSource, Is.SameAs(source), "Cancelling a read-only chooser must not refresh history.");
            Assert.That(await Snapshot(f), Is.EqualTo(before));
        }
    }

    [AvaloniaTest]
    public async Task RepositoryReopenCancelsThePickerEvenAtTheSamePathAndAChangedReferenceCannotOpenItsOldAction()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        await Git(f, "commit", "--allow-empty", "-m", "Tip"); await Git(f, "branch", "topic");
        await f.Window.OpenRepositoryAsync(f.Repository); Select(f, "Tip");
        Press(f.Window, PhysicalKey.F2); QuickGitRefSelectorDialog picker = await Child<QuickGitRefSelectorDialog>(f.Window);
        await f.Window.OpenRepositoryAsync(f.Repository); await Until(() => !picker.IsVisible); await Idle(f.Window);
        Assert.That(f.Window.OwnedWindows, Is.Empty);
        History(f).Focus(); Press(f.Window, PhysicalKey.F2); picker = await Child<QuickGitRefSelectorDialog>(f.Window);
        await Git(f, "update-ref", "refs/heads/topic", "HEAD^"); string changed = await Snapshot(f);
        Choose(picker, "refs/heads/topic"); await Idle(f.Window);
        Assert.That(f.Window.OwnedWindows, Is.Empty);
        Assert.That(await Snapshot(f), Is.EqualTo(changed));
        Assert.That(Private<TextBlock>(f.Window, "_state").Text, Does.Contain("reference changed"));
    }

    [AvaloniaTest]
    public async Task ARemoteHeadThatCannotBeDeletedKeepsItsRequestedNameAndExplainsTheDisabledAction()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        await Git(f, "symbolic-ref", "refs/remotes/isolated/HEAD", "refs/heads/main");
        string before = await Snapshot(f); History(f).Focus(); Press(f.Window, PhysicalKey.Delete);
        DeleteRemoteBranchDialog dialog = await Child<DeleteRemoteBranchDialog>(f.Window);
        Assert.That(f.Window.OwnedWindows.OfType<QuickGitRefSelectorDialog>(), Is.Empty);
        Assert.That(dialog.SelectionText, Is.EqualTo("isolated/HEAD"));
        Assert.That(dialog.LastError, Does.Contain("does not map to a deletable remote branch").And.Contain("isolated/HEAD"));
        Assert.That(Field<Button>(dialog, "DeleteSelectedRemoteBranches").IsEnabled, Is.False);
        Assert.That(dialog.HasRepositoryChanges, Is.False);
        dialog.Close(); await Idle(f.Window); Assert.That(await Snapshot(f), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ShortcutsStayInTheGridAndMultiSelectionActsOnTheFocusedSelectedCommit()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        await Git(f, "branch", "older"); await Git(f, "commit", "--allow-empty", "-m", "Tip");
        await f.Window.OpenRepositoryAsync(f.Repository);
        TextBox path = Private<TextBox>(f.Window, "_repositoryPath"); path.Focus();
        Press(f.Window, PhysicalKey.F2); await Idle(f.Window); Assert.That(f.Window.OwnedWindows, Is.Empty);
        ListBox history = History(f); GitCommit tip = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Tip");
        GitCommit older = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Initial commit");
        history.SelectedItem = tip; history.SelectedItems!.Add(older); Pump(f.Window);
        history.ContainerFromIndex(history.Items.IndexOf(older))!.Focus();
        Assert.That(history.SelectedItem, Is.SameAs(tip));
        Press(f.Window, PhysicalKey.F2); RenameBranchDialog dialog = await Child<RenameBranchDialog>(f.Window);
        Assert.That(Field<TextBox>(dialog, "RenameBranchName").Text, Is.EqualTo("older"));
        dialog.Close(); await Idle(f.Window);
    }

    private static ListBox History(Fixture f) => Field<ListBox>(f.Window, "RevisionHistory");
    private static void Select(Fixture f, string subject)
    { ListBox history = History(f); history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == subject); history.Focus(); Pump(f.Window); }
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static T Private<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static bool Reading(MainWindow main)
    { object read = Private<object>(main, "_quickReferenceRead"); return read.GetType().GetField("Current", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(read) is not null; }
    private static GitReference[] References(QuickGitRefSelectorDialog picker)
        => Field<ListBox>(picker, "lbxRefs").Items.Cast<object>().Select(item => item.GetType().GetProperty("Reference")!.GetValue(item)).OfType<GitReference>().ToArray();
    private static void Choose(QuickGitRefSelectorDialog picker, string name)
    {
        ListBox list = Field<ListBox>(picker, "lbxRefs");
        list.SelectedItem = list.Items.Cast<object>().Single(item => (item.GetType().GetProperty("Reference")!.GetValue(item) as GitReference)?.Name == name);
        list.Focus(); Press(picker, PhysicalKey.Enter);
    }
    private static void Press(TopLevel root, PhysicalKey key)
    {
        root.KeyPressQwerty(key, RawInputModifiers.None);
        if (root is not Window { IsVisible: false }) { root.KeyReleaseQwerty(key, RawInputModifiers.None); }
        Pump(root);
    }
    private static void Pump(TopLevel root)
    { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Idle(MainWindow main) { await Until(() => !main.IsBusy && !Reading(main)); Pump(main); }
    private static async Task<T> Child<T>(MainWindow main) where T : Window
    {
        await Until(() => main.OwnedWindows.OfType<T>().Any(window => window.IsVisible));
        T child = main.OwnedWindows.OfType<T>().Single(window => window.IsVisible);
        if (child is RepositoryDialog repository) { await Until(() => !repository.IsBusy); }
        Pump(child); return child;
    }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "The quick reference workflow did not settle.");
    }
    private static async Task<GitResult> Git(Fixture f, params string[] args)
    { GitResult result = await f.Git.RunAsync(f.Repository, args); Assert.That(result.Success, Is.True, result.StandardError); return result; }
    private static async Task<string> Snapshot(Fixture f)
        => (await Git(f, "show-ref", "--head")).StandardOutput + (await Git(f, "ls-files", "--stage")).StandardOutput
            + (await Git(f, "status", "--porcelain")).StandardOutput;
}

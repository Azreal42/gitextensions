using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class RenameCompareMenuTests
{
    [AvaloniaTest]
    public async Task RenameHubRemapsTheSelectedReferenceAndNavigationFilterWithoutLosingOtherCriteria()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string selectedHash = await repository.ResolveRevisionAsync("HEAD");
        await repository.CreateBranchAsync(new GitCreateBranchOptions("old-topic", Checkout: false));
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "later\n");
        await repository.StageAllAsync(); await repository.CommitAsync("Later branch commit");
        MainWindow main = fixture.Window;
        await main.OpenRepositoryAsync(fixture.Repository);
        HistoryFilterDialog filter = await OpenFilterAsync(main);
        Field<CheckBox>(filter, "AuthorCheck").IsChecked = true;
        Field<TextBox>(filter, "Author").Text = "Desktop Tester";
        Field<CheckBox>(filter, "MessageCheck").IsChecked = true;
        Field<TextBox>(filter, "Message").Text = "Initial commit";
        Field<CheckBox>(filter, "OnlyFirstParent").IsChecked = true;
        ClickLabel(filter, "OK"); await ClosedAsync(main, main, filter);
        TreeView navigation = Field<TreeView>(main, "RepositoryNavigation");
        navigation.SelectedItem = NavigationNodes(main).Single(node => node.Tag is GitReference reference && reference.Name == "refs/heads/old-topic");
        await MainIdleAsync(main);
        MenuItem commands = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Commands");
        Invoke(commands.Items.OfType<MenuItem>().Single(item => item.Header as string == "Branches and tags…"));
        await Until(() => main.OwnedWindows.Any());
        Window workflow = main.OwnedWindows.Single();
        try
        {
            workflow.UpdateLayout();
            ListBox references = workflow.GetVisualDescendants().OfType<ListBox>().Single(list => list.Items.OfType<GitReference>().Any());
            Assert.That((references.SelectedItem as GitReference)?.Name, Is.EqualTo("refs/heads/old-topic"));
            ClickLabel(workflow, "Rename…");
            RenameBranchDialog rename = await OwnedAsync<RenameBranchDialog>(workflow);
            Assert.That(rename.Owner, Is.EqualTo(workflow));
            Assert.That(main.IsBusy, Is.True, "The browser must remain locked while its repository dialog is open.");
            Assert.That(Field<TextBox>(rename, "RenameBranchName").Text, Is.EqualTo("old-topic"));
            Field<TextBox>(rename, "RenameBranchName").Text = "new topic";
            ClickNamed(rename, "RenameBranch"); await ClosedAsync(main, workflow, rename);
            Assert.That(rename.RenamedBranch, Is.EqualTo("new_topic"));
            Assert.That(await repository.ResolveRevisionAsync("new_topic"), Is.EqualTo(selectedHash));
            Assert.That((await repository.GetReferencesAsync()).Any(reference => reference.ShortName == "old-topic"), Is.False);
            Assert.That((references.SelectedItem as GitReference)?.Name, Is.EqualTo("refs/heads/new_topic"));
            Assert.That((navigation.SelectedItem as TreeViewItem)?.Tag, Is.TypeOf<GitReference>());
            Assert.That(((GitReference)((TreeViewItem)navigation.SelectedItem!).Tag!).Name, Is.EqualTo("refs/heads/new_topic"));
            Assert.That(NavigationNodes(main), Does.Contain(navigation.SelectedItem), "The selection must belong to the visible, refreshed navigation tree.");
            TextBox expression = main.GetVisualDescendants().OfType<TextBox>().Single(box => box.Watermark == "Revision (default: all refs)");
            Assert.That(expression.Text, Is.EqualTo("refs/heads/new_topic"));
            Assert.That((Field<ListBox>(main, "RevisionHistory").SelectedItem as GitCommit)?.Hash, Is.EqualTo(selectedHash));
            ClickLabel(workflow, "Rename…");
            rename = await OwnedAsync<RenameBranchDialog>(workflow);
            Assert.That(Field<TextBox>(rename, "RenameBranchName").Text, Is.EqualTo("new_topic"));
            Press(rename, PhysicalKey.Escape); await ClosedAsync(main, workflow, rename);
        }
        finally { workflow.Close(); }
        await Until(() => !main.OwnedWindows.Contains(workflow));
        filter = await OpenFilterAsync(main);
        GitLogQuery query = filter.ReadQuery();
        Assert.Multiple(() =>
        {
            Assert.That(query.Revision, Is.EqualTo("refs/heads/new_topic"));
            Assert.That(query.Author, Is.EqualTo("Desktop Tester"));
            Assert.That(query.Message, Is.EqualTo("Initial commit"));
            Assert.That(query.FirstParent, Is.True);
        });
        ClickLabel(filter, "Cancel"); await ClosedAsync(main, main, filter);
    }

    [AvaloniaTest]
    public async Task ControlRComparesRootParentAndMultipleSelectionsInHistoryOrderAndPreservesSelectionOnRefresh()
    {
        using Fixture fixture = await Fixture.CreateAsync("oldest\n", "oldest\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string oldest = await repository.ResolveRevisionAsync("HEAD");
        await CommitAsync(fixture, repository, "middle\n", "Middle commit");
        string middle = await repository.ResolveRevisionAsync("HEAD");
        await CommitAsync(fixture, repository, "latest\n", "Latest commit");
        string latest = await repository.ResolveRevisionAsync("HEAD");
        MainWindow main = fixture.Window;
        await main.OpenRepositoryAsync(fixture.Repository);
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        await SelectAsync(main, history, oldest);
        history.Focus(); Press(main, PhysicalKey.R, RawInputModifiers.Control);
        CompareRevisionsDialog compare = await OwnedAsync<CompareRevisionsDialog>(main);
        Assert.That(compare.Comparison!.Base.Revision, Is.EqualTo(oldest));
        Assert.That(compare.Comparison.Compare.Revision, Is.EqualTo(oldest));
        Assert.That(compare.Comparison.Files, Is.Empty, "A root commit has no parent, so Ctrl+R must use its valid hash on both sides.");
        Assert.That(main.IsBusy, Is.True);
        ClickNamed(compare, "CloseComparison"); await ClosedAsync(main, main, compare);

        await SelectAsync(main, history, middle);
        history.Focus(); Press(main, PhysicalKey.R, RawInputModifiers.Control);
        compare = await OwnedAsync<CompareRevisionsDialog>(main);
        Assert.That(compare.Comparison!.Base.Revision, Is.EqualTo(oldest));
        Assert.That(compare.Comparison.Compare.Revision, Is.EqualTo(middle));
        Assert.That(compare.Comparison.Files.Single().Path, Is.EqualTo("file.txt"));
        ClickNamed(compare, "CloseComparison"); await ClosedAsync(main, main, compare);

        await SelectAsync(main, history, oldest);
        history.SelectedItems!.Add(history.Items.OfType<GitCommit>().Single(commit => commit.Hash == latest));
        await MainIdleAsync(main);
        history.Focus(); Press(main, PhysicalKey.R, RawInputModifiers.Control);
        compare = await OwnedAsync<CompareRevisionsDialog>(main);
        Assert.That(compare.Comparison!.Base.Revision, Is.EqualTo(oldest), "Selection insertion order must not reverse the BASE side.");
        Assert.That(compare.Comparison.Compare.Revision, Is.EqualTo(latest));
        Assert.That(Field<GitExtensions.Desktop.Controls.FileHistoryContentView>(compare, "ComparisonDiff").TextView.Text, Does.Contain("-oldest").And.Contain("+latest"));
        ClickNamed(compare, "CloseComparison"); await ClosedAsync(main, main, compare);
        history.Focus(); Press(main, PhysicalKey.F5); await MainIdleAsync(main);
        Assert.That(history.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EquivalentTo(new[] { oldest, latest }));
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }

    [AvaloniaTest]
    public async Task RenameHubUsesTheCurrentUnbornBranchWhenThereAreNoReferencesYet()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        string empty = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "unborn repository");
        Directory.CreateDirectory(empty);
        GitResult initialized = await fixture.Git.RunAsync(empty, ["init", "-b", "main"]);
        Assert.That(initialized.Success, Is.True, initialized.StandardError);
        GitRepository repository = await GitRepository.OpenAsync(empty);
        MainWindow main = fixture.Window;
        await main.OpenRepositoryAsync(empty);
        MenuItem commands = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Commands");
        Invoke(commands.Items.OfType<MenuItem>().Single(item => item.Header as string == "Branches and tags…"));
        await Until(() => main.OwnedWindows.Any());
        Window workflow = main.OwnedWindows.Single();
        try
        {
            ClickLabel(workflow, "Rename…");
            RenameBranchDialog rename = await OwnedAsync<RenameBranchDialog>(workflow);
            Assert.That(Field<TextBox>(rename, "RenameBranchName").Text, Is.EqualTo("main"));
            Field<TextBox>(rename, "RenameBranchName").Text = "before-first-commit";
            ClickNamed(rename, "RenameBranch"); await ClosedAsync(main, workflow, rename);
            Assert.That(rename.HasRepositoryChanges, Is.True);
            Assert.That((await repository.GetStatusAsync()).Branch, Is.EqualTo("before-first-commit"));
            Assert.That((await repository.GetReferencesAsync()), Is.Empty);
            Assert.That(Field<TextBlock>(main, "RepositoryStatus").Text, Does.StartWith("before-first-commit"));
        }
        finally { workflow.Close(); }
    }

    [AvaloniaTest]
    public async Task OriginalCompareContextActionsUseSelectedCommitsBaseBranchWorktreeAndConfiguredDiffTool()
    {
        using Fixture fixture = await Fixture.CreateAsync("old\n", "old\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string old = await repository.ResolveRevisionAsync("HEAD");
        await CommitAsync(fixture, repository, "new\n", "Context target commit");
        string latest = await repository.ResolveRevisionAsync("HEAD");
        MainWindow main = fixture.Window;
        await main.OpenRepositoryAsync(fixture.Repository);
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        MenuItem group = history.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Header as string == "Compare");
        Assert.That(group.Items.OfType<MenuItem>().Select(item => item.Header as string), Is.EqualTo(new[]
        {
            "Open selected commits with difftool", "Compare to branch…", "Compare with current branch", "Select as BASE to compare",
            "Compare to BASE", "Compare to working directory", "Compare selected commits"
        }));
        Assert.That(ContextAction(group, "Compare to BASE").IsEnabled, Is.False);
        await SelectAsync(main, history, latest);
        Invoke(ContextAction(group, "Select as BASE to compare"));
        Assert.That(ContextAction(group, "Compare to BASE").IsEnabled, Is.True);
        await SelectAsync(main, history, old);
        Invoke(ContextAction(group, "Compare to BASE"));
        CompareRevisionsDialog compare = await OwnedAsync<CompareRevisionsDialog>(main);
        Assert.That(compare.Comparison!.Base.Revision, Is.EqualTo(latest));
        Assert.That(compare.Comparison.Compare.Revision, Is.EqualTo(old));
        Assert.That(Field<GitExtensions.Desktop.Controls.FileHistoryContentView>(compare, "ComparisonDiff").TextView.Text, Does.Contain("-new").And.Contain("+old"));
        ClickNamed(compare, "CloseComparison"); await ClosedAsync(main, main, compare);

        Invoke(ContextAction(group, "Compare with current branch"));
        compare = await OwnedAsync<CompareRevisionsDialog>(main);
        Assert.That(compare.Comparison!.Base.Revision, Is.EqualTo(old));
        Assert.That(compare.Comparison.Compare.Revision, Is.EqualTo(latest));
        ClickNamed(compare, "CloseComparison"); await ClosedAsync(main, main, compare);

        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "working copy\n");
        const string untracked = ":context[*]\nλ.txt";
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, untracked), "literal untracked\n");
        Invoke(ContextAction(group, "Compare to working directory"));
        compare = await OwnedAsync<CompareRevisionsDialog>(main);
        Assert.That(compare.Comparison!.Base.Revision, Is.EqualTo(old));
        Assert.That(compare.Comparison.Compare.Kind, Is.EqualTo(GitCompareRevisionKind.WorkingTree));
        Assert.That(compare.Comparison.UntrackedPaths, Does.Contain(untracked));
        ClickNamed(compare, "CloseComparison"); await ClosedAsync(main, main, compare);

        Invoke(ContextAction(group, "Compare to branch…"));
        CompareBranchPicker picker = await OwnedAsync<CompareBranchPicker>(main);
        Assert.That(main.IsBusy, Is.True);
        Field<RadioButton>(picker, "ComparePickLocal").IsChecked = true; await IdleAsync(picker);
        ComboBox branches = Field<ComboBox>(picker, "ComparePickBranch");
        branches.SelectedItem = branches.Items.OfType<GitReference>().Single(reference => reference.ShortName == "main");
        await IdleAsync(picker); ClickNamed(picker, "ComparePickAccept");
        compare = await OwnedAsync<CompareRevisionsDialog>(main);
        Assert.That(compare.Comparison!.Base.Revision, Is.EqualTo(latest));
        Assert.That(compare.Comparison.Compare.Revision, Is.EqualTo(old));
        ClickNamed(compare, "CloseComparison"); await ClosedAsync(main, main, compare);

        history.SelectedItems!.Add(history.Items.OfType<GitCommit>().Single(commit => commit.Hash == latest));
        await MainIdleAsync(main);
        Invoke(ContextAction(group, "Compare selected commits"));
        compare = await OwnedAsync<CompareRevisionsDialog>(main);
        Assert.That(compare.Comparison!.Base.Revision, Is.EqualTo(old));
        Assert.That(compare.Comparison.Compare.Revision, Is.EqualTo(latest));
        ClickNamed(compare, "CloseComparison"); await ClosedAsync(main, main, compare);

        if (!OperatingSystem.IsWindows())
        {
            string helper = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "context-diff-tool.py");
            string marker = helper + ".json";
            await File.WriteAllTextAsync(helper, "#!/usr/bin/python3\nimport json, pathlib, sys\n" +
                "pathlib.Path(" + JsonSerializer.Serialize(marker) + ").write_text(json.dumps([str(pathlib.Path(p).joinpath('file.txt').read_text()) for p in sys.argv[1:]]))\n");
            File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await GitAsync(fixture, "config", "diff.guitool", "menu-fixture");
            await GitAsync(fixture, "config", "difftool.menu-fixture.cmd", "'" + helper.Replace("'", "'\\''", StringComparison.Ordinal) + "' \"$LOCAL\" \"$REMOTE\"");
            Invoke(ContextAction(group, "Open selected commits with difftool"));
            await Until(() => File.Exists(marker) && !main.IsBusy);
            Assert.That(JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(marker)), Is.EqualTo(new[] { "old\n", "new\n" }));
            Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        }
        Assert.That((await repository.GetStatusAsync()).Changes.Select(change => change.Path), Is.EquivalentTo(new[] { "file.txt", untracked }));
    }

    [AvaloniaTest]
    public async Task ContextDiffToolRefreshesTheBrowserAfterItsWorkingDirectoryEdit()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Linux native diff-tool executable scenario."); return; }
        using Fixture fixture = await Fixture.CreateAsync("old\n", "old\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await CommitAsync(fixture, repository, "new\n", "Editable diff-tool target");
        MainWindow main = fixture.Window;
        await main.OpenRepositoryAsync(fixture.Repository);
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        await SelectAsync(main, history, await repository.ResolveRevisionAsync("HEAD"));
        string helper = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "editing-context-diff-tool.py");
        string marker = helper + ".done";
        await File.WriteAllTextAsync(helper, "#!/usr/bin/python3\nimport pathlib, sys\n" +
            "pathlib.Path(sys.argv[2]).joinpath('file.txt').write_text('edited by native tool\\n')\n" +
            "pathlib.Path(" + JsonSerializer.Serialize(marker) + ").write_text('done')\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await GitAsync(fixture, "config", "diff.guitool", "editing-menu-fixture");
        await GitAsync(fixture, "config", "difftool.editing-menu-fixture.cmd", "'" + helper.Replace("'", "'\\''", StringComparison.Ordinal) + "' \"$LOCAL\" \"$REMOTE\"");
        MenuItem group = history.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Header as string == "Compare");
        Invoke(ContextAction(group, "Open selected commits with difftool"));
        await Until(() => File.Exists(marker) && !main.IsBusy);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "file.txt")), Is.EqualTo("edited by native tool\n"));
        Assert.That((await repository.GetStatusAsync()).Changes.Single().Path, Is.EqualTo("file.txt"));
        Assert.That(Field<TextBlock>(main, "RepositoryStatus").Text, Does.Contain("1 changes"), "After the native tool closes, the browser must refresh the working-directory state.");
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }

    private static async Task CommitAsync(Fixture fixture, GitRepository repository, string text, string message)
    { await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), text); await repository.StageAllAsync(); await repository.CommitAsync(message); }
    private static async Task GitAsync(Fixture fixture, params string[] arguments)
    { GitResult result = await fixture.Git.RunAsync(fixture.Repository, arguments); Assert.That(result.Success, Is.True, result.StandardError); }
    private static MenuItem ContextAction(MenuItem group, string title) => group.Items.OfType<MenuItem>().Single(item => item.Header as string == title);
    private static void Invoke(MenuItem action)
    { Assert.That(action.IsEnabled, Is.True, action.Header?.ToString()); action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
    private static T Field<T>(Window owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static IEnumerable<TreeViewItem> NavigationNodes(MainWindow main)
        => Field<TreeView>(main, "RepositoryNavigation").Items.OfType<TreeViewItem>().SelectMany(group => group.Items.OfType<TreeViewItem>());
    private static void ClickNamed(Window owner, string name) => Click(Field<Button>(owner, name));
    private static void ClickLabel(Window owner, string label) => Click(owner.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == label));
    private static void Click(Button button)
    { Assert.That(button.IsEnabled && button.IsEffectivelyVisible, Is.True, button.Name ?? button.Content?.ToString()); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static void Press(Window owner, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { owner.KeyPressQwerty(key, modifiers); if (owner.IsVisible) { owner.KeyReleaseQwerty(key, modifiers); } }
    private static async Task<T> OwnedAsync<T>(Window owner) where T : RepositoryDialog
    { await Until(() => owner.OwnedWindows.OfType<T>().Any()); T dialog = owner.OwnedWindows.OfType<T>().Single(); await IdleAsync(dialog); return dialog; }
    private static async Task<HistoryFilterDialog> OpenFilterAsync(MainWindow main)
    { ClickLabel(main, "Filter"); await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any()); return main.OwnedWindows.OfType<HistoryFilterDialog>().Single(); }
    private static async Task SelectAsync(MainWindow main, ListBox history, string hash)
    { history.SelectedItems!.Clear(); history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == hash); await MainIdleAsync(main); }
    private static async Task IdleAsync(RepositoryDialog dialog)
    { await Until(() => !dialog.IsBusy); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); }
    private static async Task MainIdleAsync(MainWindow main)
    { await Until(() => !main.IsBusy); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); }
    private static async Task ClosedAsync(MainWindow main, Window owner, Window dialog)
    { await Until(() => !dialog.IsVisible && !owner.OwnedWindows.Contains(dialog) && !main.IsBusy); if (dialog is RepositoryDialog repositoryDialog) { Assert.That(repositoryDialog.LastError, Is.Null, repositoryDialog.LastException?.ToString()); } Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); }
    private static async Task Until(Func<bool> predicate)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); while (!predicate()) { await Task.Delay(20, timeout.Token); } }
}

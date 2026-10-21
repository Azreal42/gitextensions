using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class MainFileStatusListTests
{
    [AvaloniaTest]
    public async Task BrowseUsesOriginalBranchMarkersFiltersAndRangeNodeWithoutChangingTheRepository()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await WriteAsync(fixture, "nested/same.txt", "base same\n");
        await WriteAsync(fixture, "nested/different.txt", "base different\n");
        await CommitAsync(repository, "Base files");
        string branchPoint = await repository.ResolveRevisionAsync("HEAD");
        await GitAsync(fixture, "checkout", "-b", "series-a");
        await WriteAsync(fixture, "nested/same.txt", "same change\n");
        await WriteAsync(fixture, "nested/different.txt", "change in A\n");
        await WriteAsync(fixture, "nested/only A λ[1].txt", "only A\n");
        await CommitAsync(repository, "Series A");
        string a = await repository.ResolveRevisionAsync("HEAD");
        await GitAsync(fixture, "checkout", "-b", "series-b", branchPoint);
        await WriteAsync(fixture, "nested/same.txt", "same change\n");
        await WriteAsync(fixture, "nested/different.txt", "change in B\n");
        await WriteAsync(fixture, "nested/only B.txt", "only B\n");
        await CommitAsync(repository, "Series B");
        string b = await repository.ResolveRevisionAsync("HEAD");
        await fixture.Window.OpenRepositoryAsync(fixture.Repository);
        await IdleAsync(fixture.Window);
        ListBox history = Find<ListBox>(fixture.Window, "RevisionHistory");
        history.SelectedItems!.Clear(); await IdleAsync(fixture.Window);
        // Windows SelectedRows puts the last newly selected row first: B is the comparison target.
        history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == a); await IdleAsync(fixture.Window);
        history.SelectedItems.Add(history.Items.OfType<GitCommit>().Single(commit => commit.Hash == b)); await IdleAsync(fixture.Window);
        SelectTab(fixture.Window, "DiffTab");
        FileStatusListView view = Find<FileStatusListView>(fixture.Window, "RevisionFileStatusList");
        RevisionDiffFileSelection onlyA = view.Entries.Select(entry => entry.Tag).OfType<RevisionDiffFileSelection>()
            .Single(item => item.Group.Kind == GitRevisionDiffGroupKind.Comparison && item.File.Path == "nested/only A λ[1].txt");
        Assert.That(onlyA.Group.BaseRevision, Is.EqualTo(a)); Assert.That(onlyA.Group.Revision, Is.EqualTo(b));
        Assert.That(onlyA.File.Status, Is.EqualTo('D'));
        Assert.That(onlyA.File.DiffStatus, Is.EqualTo(DiffBranchStatus.OnlyAChange));
        Assert.That(FileStatusListView.IconName(view.Entries.Single(entry => Equals(entry.Tag, onlyA))), Is.EqualTo("FileStatusRemovedOnlyA.png"));
        Assert.That(view.Entries.Where(entry => entry.Path == "nested/same.txt").Select(entry => entry.DiffStatus),
            Is.All.EqualTo(DiffBranchStatus.SameChange));
        string before = await SnapshotAsync(fixture);
        view.SelectTags([onlyA]); await IdleAsync(fixture.Window);
        Assert.That(Viewer(fixture.Window).Text, Does.Contain("-only A"));
        Assert.That(Viewer(fixture.Window).CanStagePatch, Is.True, "This is an ordinary A-to-B patch even though the branch marker is A.");

        ToggleButton aButton = Find<ToggleButton>(fixture.Window, "RevisionFilesOnlyA");
        Assert.That(aButton.IsChecked, Is.True);
        aButton.IsChecked = false; aButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !fixture.Window.IsBusy && view.FilteredEntries.All(entry => entry.DiffStatus != DiffBranchStatus.OnlyAChange));
        Assert.That(view.Tree.Items.OfType<TreeViewItem>().Any(item => item.Tag is GitRevisionDiffGroup { Kind: GitRevisionDiffGroupKind.RangeDiff }), Is.True);
        view.FilterText = "no matching files";
        await UntilAsync(() => !fixture.Window.IsBusy && view.FilteredEntries.Count == 0);
        TreeViewItem range = view.Tree.Items.OfType<TreeViewItem>().Single(item => item.Tag is GitRevisionDiffGroup { Kind: GitRevisionDiffGroupKind.RangeDiff });
        view.Tree.SelectedItem = range; await IdleAsync(fixture.Window);
        Assert.That(Viewer(fixture.Window).EffectiveMode, Is.EqualTo(DiffViewMode.RangeDiff));
        Assert.That(Viewer(fixture.Window).Text, Is.Not.Empty);
        view.ClearFilter();
        aButton.IsChecked = true; aButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !fixture.Window.IsBusy && view.FilteredEntries.Any(entry => entry.DiffStatus == DiffBranchStatus.OnlyAChange));
        view.SelectTags([onlyA]); await IdleAsync(fixture.Window);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            fixture.Window.RequestedThemeVariant = theme;
            await CaptureAsync(fixture.Window, "classic-file-status-branches-" + theme + ".png");
        }
        Assert.That(await SnapshotAsync(fixture), Is.EqualTo(before), "Display and branch filters must not change refs, index or worktree.");
    }

    [AvaloniaTest]
    public async Task WorkingDirectoryStagesOnlyFilteredLogicalLeavesAndUnstagesRenamesByBothNames()
    {
        using Fixture fixture = await Fixture.CreateAsync("anchor\n", "anchor\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        const string oldPath = "deep/old λ\t[1].txt";
        const string newPath = "deep/renamed λ\n[1].txt";
        await WriteAsync(fixture, oldPath, string.Join('\n', Enumerable.Range(1, 40).Select(index => "line " + index)) + "\n");
        await WriteAsync(fixture, "deep/hidden.txt", "hidden base\n");
        await CommitAsync(repository, "Nested files");
        await GitAsync(fixture, "mv", "--", oldPath, newPath);
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository, newPath), "new line\n");
        await WriteAsync(fixture, "deep/hidden.txt", "hidden work\n");
        await WriteAsync(fixture, "deep/new λ[2].txt", "new work\n");
        await fixture.Window.OpenRepositoryAsync(fixture.Repository); await IdleAsync(fixture.Window);
        SelectTab(fixture.Window, "ChangesTab");
        FileStatusListView view = Find<FileStatusListView>(fixture.Window, "WorkingFileStatusList");
        view.FilterText = "renamed|new λ";
        await UntilAsync(() => view.FilteredEntries.Count == 2 && !fixture.Window.IsBusy);
        foreach (TreeViewItem item in FileStatusTestHelpers.Rows(view.Tree)) { item.IsExpanded = false; }
        Assert.That(view.FilteredEntries.Select(entry => entry.Path), Is.EquivalentTo(new[] { newPath, "deep/new λ[2].txt" }));
        Click(fixture.Window, "Stage all"); await IdleAsync(fixture.Window);
        Assert.That(view.FilterText, Is.Empty);
        string staged = (await GitAsync(fixture, "diff", "--cached", "--name-only", "-z")).StandardOutput;
        Assert.That(staged.Split('\0', StringSplitOptions.RemoveEmptyEntries), Does.Contain(newPath).And.Contain("deep/new λ[2].txt").And.Not.Contain("deep/hidden.txt"));
        Assert.That((await GitAsync(fixture, "show", ":" + newPath)).StandardOutput, Does.EndWith("new line\n"));

        view.FilterText = "old λ";
        await UntilAsync(() => view.FilteredEntries.Count == 1 && !fixture.Window.IsBusy);
        Assert.That(view.FilteredEntries[0].OriginalPath, Is.EqualTo(oldPath), "Display regex must also match the old rename path.");
        Click(fixture.Window, "Unstage all"); await IdleAsync(fixture.Window);
        Assert.That(view.FilterText, Is.Empty);
        staged = (await GitAsync(fixture, "diff", "--cached", "--name-only", "-z")).StandardOutput;
        Assert.That(staged.Split('\0', StringSplitOptions.RemoveEmptyEntries), Is.EqualTo(new[] { "deep/new λ[2].txt" }));
        Assert.That(File.Exists(Path.Combine(fixture.Repository, oldPath)), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(fixture.Repository, newPath)), Does.EndWith("new line\n"));
        Assert.That(File.ReadAllText(Path.Combine(fixture.Repository, "deep/hidden.txt")), Is.EqualTo("hidden work\n"));
    }

    [AvaloniaTest]
    public async Task EmptyDisplayFilterCannotStageAllAndInvalidRegexKeepsTheLastValidFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync("anchor\n", "changed\n");
        await WriteAsync(fixture, "folder/other λ.txt", "other\n");
        await fixture.Window.OpenRepositoryAsync(fixture.Repository); await IdleAsync(fixture.Window);
        SelectTab(fixture.Window, "ChangesTab");
        FileStatusListView view = Find<FileStatusListView>(fixture.Window, "WorkingFileStatusList");
        view.FilterText = "folder/other";
        await UntilAsync(() => view.FilteredEntries.Count == 1 && !fixture.Window.IsBusy);
        string[] retained = view.FilteredEntries.Select(entry => entry.Path).ToArray();
        view.FilterText = "[";
        await UntilAsync(() => view.FilterError is not null);
        Assert.That(view.FilteredEntries.Select(entry => entry.Path), Is.EqualTo(retained));
        Assert.That(Find<ComboBox>(fixture.Window, "WorkingFilesFilter").Text, Is.EqualTo("["));
        view.FilterText = "no such file";
        await UntilAsync(() => view.FilterError is null && view.FilteredEntries.Count == 0 && !fixture.Window.IsBusy);
        Click(fixture.Window, "Stage all"); await IdleAsync(fixture.Window);
        Assert.That((await GitAsync(fixture, "diff", "--cached")).StandardOutput, Is.Empty,
            "git add -A with an empty path list would silently stage everything; the host must guard the empty filtered list.");
        Assert.That(view.FilterText, Is.Empty);
        Assert.That(File.ReadAllText(Path.Combine(fixture.Repository, "file.txt")), Is.EqualTo("changed\n"));
    }

    [AvaloniaTest]
    public async Task GlobalListModeUpdatesBrowseWorkingDirectoryAndCommitButFiltersStayLocal()
    {
        using Fixture fixture = await Fixture.CreateAsync("anchor\n", "changed\n");
        await WriteAsync(fixture, "one/deep/a.cs", "source\n");
        await fixture.Window.OpenRepositoryAsync(fixture.Repository); await IdleAsync(fixture.Window);
        SelectTab(fixture.Window, "ChangesTab");
        FileStatusListView working = Find<FileStatusListView>(fixture.Window, "WorkingFileStatusList");
        working.SortMode = FileStatusListSortMode.FileExtensionFlat;
        working.FilterText = "\\.cs$";
        await UntilAsync(() => working.FilteredEntries.Count == 1);
        CommitDialog dialog = new(await GitRepository.OpenAsync(fixture.Repository));
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            FileStatusListView commit = dialog.GetVisualDescendants().OfType<FileStatusListView>().Single(view => view.Tree.Name == "Unstaged");
            Assert.That(commit.SortMode, Is.EqualTo(FileStatusListSortMode.FileExtensionFlat));
            Assert.That(commit.FilterText, Is.Empty);
            Assert.That(commit.FilteredEntries.Count, Is.EqualTo(2));
            commit.SortMode = FileStatusListSortMode.FilePath;
            Assert.That(working.SortMode, Is.EqualTo(FileStatusListSortMode.FilePath));
            Assert.That(working.FilterText, Is.EqualTo("\\.cs$"));
            SelectTab(fixture.Window, "DiffTab");
            FileStatusListView browse = Find<FileStatusListView>(fixture.Window, "RevisionFileStatusList");
            Assert.That(browse.SortMode, Is.EqualTo(FileStatusListSortMode.FilePath));
            Assert.That(browse.FilterText, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task SelectingAFolderClearsThePreviousPatchBeforeHunkActions()
    {
        using Fixture fixture = await Fixture.CreateAsync("anchor\n", "anchor\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string original = string.Join('\n', Enumerable.Range(1, 40).Select(index => "original " + index)) + "\n";
        await WriteAsync(fixture, "folder/file.txt", original);
        await CommitAsync(repository, "Nested content");
        await WriteAsync(fixture, "folder/file.txt", original.Replace("original 20\n", "changed line\n", StringComparison.Ordinal));
        await fixture.Window.OpenRepositoryAsync(fixture.Repository); await IdleAsync(fixture.Window);
        SelectTab(fixture.Window, "ChangesTab");
        FileStatusListView view = Find<FileStatusListView>(fixture.Window, "WorkingFileStatusList");
        FileStatusTestHelpers.Select(view.Tree, "folder/file.txt"); await IdleAsync(fixture.Window);
        ListBox hunks = fixture.Window.GetVisualDescendants().OfType<ListBox>().Single(list => list.Items.OfType<GitPatchHunk>().Any());
        hunks.SelectedIndex = 0;
        view.Tree.SelectedItem = FileStatusTestHelpers.Rows(view.Tree).Single(item => item.Tag is FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder });
        await IdleAsync(fixture.Window);
        Assert.That(hunks.Items, Is.Empty);
        Click(fixture.Window, "Stage selected hunks"); await IdleAsync(fixture.Window);
        Assert.That((await GitAsync(fixture, "diff", "--cached")).StandardOutput, Is.Empty);
        Assert.That(File.ReadAllText(Path.Combine(fixture.Repository, "folder/file.txt")), Does.Contain("changed line\n"));
    }

    private static T Find<T>(Control owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static DiffView Viewer(MainWindow main) => main.GetVisualDescendants().OfType<DiffView>().Single(view => view.Name == "CommitDiff");
    private static void SelectTab(MainWindow main, string name)
    {
        TabControl tabs = Find<TabControl>(main, "DetailsTabs");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == name);
        main.UpdateLayout();
    }
    private static void Click(MainWindow main, string text)
        => main.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task IdleAsync(MainWindow main)
    {
        await Task.Delay(30);
        await UntilAsync(() => !main.IsBusy);
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate() && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        Assert.That(predicate(), Is.True, "File-list workflow did not settle within 15 seconds.");
    }
    private static async Task WriteAsync(Fixture fixture, string path, string text)
    {
        string full = Path.Combine(fixture.Repository, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, text);
    }
    private static async Task CommitAsync(GitRepository repository, string message)
    { await repository.StageAllAsync(); await repository.CommitAsync(message); }
    private static async Task<GitResult> GitAsync(Fixture fixture, params string[] arguments)
    {
        GitResult result = await fixture.Git.RunAsync(fixture.Repository, arguments);
        Assert.That(result.Success, Is.True, result.StandardError);
        return result;
    }
    private static async Task<string> SnapshotAsync(Fixture fixture)
        => (await GitAsync(fixture, "show-ref")).StandardOutput + "\0" + (await GitAsync(fixture, "status", "--porcelain=v1", "-z")).StandardOutput;
    private static async Task CaptureAsync(MainWindow main, string name)
    {
        main.UpdateLayout(); Dispatcher.UIThread.RunJobs(); await Task.Delay(60);
        using var bitmap = main.CaptureRenderedFrame();
        Assert.That(bitmap, Is.Not.Null);
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts/linux/screenshots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        bitmap!.Save(path);
        TestContext.AddTestAttachment(path);
    }
}

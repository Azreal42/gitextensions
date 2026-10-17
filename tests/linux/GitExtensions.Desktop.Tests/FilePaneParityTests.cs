using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FilePaneParityTests
{
    [AvaloniaTest]
    public async Task DiffKeepsItsSingleComparisonHeaderAndCompactFlatPathsBesideTheRealSelectedPatch()
    {
        using Fixture f = await CreateAsync(); MainWindow main = f.Window;
        SelectTab(main, "DiffTab"); await Idle(main);
        FileStatusListView view = Find<FileStatusListView>(main, "RevisionFileStatusList");
        view.SortMode = FileStatusListSortMode.FilePathFlat; await Idle(main);
        TreeViewItem group = view.Tree.Items.OfType<TreeViewItem>().Single();
        Assert.That(group.Tag, Is.TypeOf<GitRevisionDiffGroup>());
        string caption = ((StackPanel)group.Header!).Children.OfType<TextBlock>().Single().Text!;
        Assert.That(caption, Does.StartWith("(3) Diff with A ").And.Contain("Nested base"));
        TreeViewItem file = FileStatusTestHelpers.Rows(view.Tree).Single(item => item.Tag is RevisionDiffFileSelection { File.Path: "src/deep/file_name.txt" });
        view.Tree.SelectedItem = file; await Idle(main);
        DiffView viewer = Find<DiffView>(main, "CommitDiff");
        Assert.That(viewer.Text, Does.Contain("-historical source").And.Contain("+committed change").And.Not.Contain("dirty worktree"));
        TextBlock label = ((Grid)file.Header!).Children.OfType<TextBlock>().Single();
        Assert.That(label.Inlines!.OfType<Run>().Select(run => run.Text), Is.EqualTo(new[] { "src/deep/", "file_name.txt" }));
        Assert.That(((Image)((Grid)file.Header!).Children[0]).Tag, Is.EqualTo("FileStatusModified.png"));
        Assert.That(view.Toolbar.Children.IndexOf(Find<Button>(main, "RevisionDiffRefresh")), Is.EqualTo(1));
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme; Pump(main);
            Assert.That(FileStatusTestHelpers.Rows(view.Tree).Where(item => item.IsEffectivelyVisible && item.GetVisualDescendants().OfType<Grid>().Any(grid => grid.Name == "PART_Header" && grid.TemplatedParent == item)).Select(item => Header(item).Bounds.Height), Is.All.EqualTo(18));
            Assert.That(((ISolidColorBrush)label.Inlines!.OfType<Run>().First().Foreground!).Color, Is.Not.EqualTo(((ISolidColorBrush)label.Foreground!).Color));
            Capture(main, "file-panes-diff-" + theme + ".png");
        }
    }

    [AvaloniaTest]
    public async Task FileTreeHasNativeFoldersTheOriginalFilterAndImmutableFileContentWithoutGenericActionRows()
    {
        using Fixture f = await CreateAsync(); MainWindow main = f.Window; SelectTab(main, "FileTreeTab"); await Idle(main);
        FileStatusListView view = Find<FileStatusListView>(main, "CommitFileStatusList");
        TreeViewItem folder = FileStatusTestHelpers.Rows(view.Tree).Single(item => item.Tag is FileStatusListNodeTag { Key: "src/deep", Kind: FileStatusListNodeKind.Folder });
        Assert.That(((Grid)folder.Header!).Children.OfType<Image>().Single().Tag, Is.EqualTo("FolderClosed.png"));
        Assert.That(view.Toolbar.IsEffectivelyVisible, Is.False);
        Assert.That(main.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible && button.Content as string is "Blame" or "File history"), Is.False);
        Assert.That(Find<ComboBox>(main, "CommitFileTreeFilter").IsEditable, Is.True);
        view.SelectTags(["src/deep/file_name.txt"]); await Idle(main);
        FileContentView content = Find<FileContentView>(main, "CommitFileContent");
        Assert.That(content.TextView.Text, Is.EqualTo("committed change\n"));
        Assert.That(content.TextView.SyntaxPath, Is.EqualTo("src/deep/file_name.txt"));
        string before = await Snapshot(f);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme; Pump(main);
            Assert.That(FileStatusTestHelpers.Rows(view.Tree).Where(item => item.IsEffectivelyVisible && item.GetVisualDescendants().OfType<Grid>().Any(grid => grid.Name == "PART_Header" && grid.TemplatedParent == item)).Select(item => Header(item).Bounds.Height), Is.All.EqualTo(18));
            Capture(main, "file-panes-tree-" + theme + ".png");
        }
        view.FilterText = "other"; await Until(() => view.FilteredEntries.Count == 1 && !main.IsBusy);
        Assert.That(view.FilteredEntries.Single().Path, Is.EqualTo("src/deep/other.txt"));
        Assert.That(content.TextView.Text, Is.Empty.Or.Null, "Filtering away the selection must clear its old content.");
        view.ClearFilter(); await Idle(main);
        Assert.That(await Snapshot(f), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task FileMenuUsesOriginalOrderIconsShortcutsAndNativePathSubmenuAndCopiesLiteralFolderOrFileNames()
    {
        using Fixture f = await CreateAsync(); MainWindow main = f.Window; SelectTab(main, "FileTreeTab"); await Idle(main);
        FileStatusListView view = Find<FileStatusListView>(main, "CommitFileStatusList"); view.SelectTags(["src/deep/file_name.txt"]); await Idle(main);
        ContextMenu menu = Open(view.Tree);
        Assert.That(menu.Items.OfType<MenuItem>().Select(item => item.Name), Is.EqualTo(new[]
        {
            "tsmiResetFileTo", "tsmiOpenWithDifftool", "tsmiOpenWorkingDirectoryFile", "tsmiOpenWorkingDirectoryFileWith", "tsmiOpenRevisionFile", "tsmiOpenRevisionFileWith",
            "tsmiEditWorkingDirectoryFile", "tsmiSaveAs", "tsmiMove", "CopyPathsToolStripMenuItem", "tsmiShowInFolder", "tsmiFilterFileInGrid", "tsmiFileHistory", "tsmiBlame",
            "tsmiFindFile", "tsmiOpenFindInCommitFilesGitGrepDialog", "tsmiShowFindInCommitFilesGitGrep", "tsmiStopTracking", "FileTreeCollapseRootFolders"
        }));
        Assert.That(Item(menu, "tsmiOpenRevisionFile").InputGesture, Is.EqualTo(new KeyGesture(Key.F3, KeyModifiers.Control)));
        Assert.That(((Image)Item(menu, "tsmiOpenWithDifftool").Icon!).Tag, Is.EqualTo("Diff.png"));
        MenuItem copy = Item(menu, "CopyPathsToolStripMenuItem");
        Assert.That(copy.Items.OfType<MenuItem>().Select(item => ((AccessText)item.Header!).Text), Is.EqualTo(new[] { "Copy relative path(s) - _POSIX", "Copy relative path(s) - _native", "Copy _full path(s) - native" }));
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        { main.RequestedThemeVariant = theme; Pump(main); Capture(main, "file-panes-menu-" + theme + ".png"); }
        Click(copy.Items.OfType<MenuItem>().First());
        Assert.That(await main.Clipboard!.TryGetTextAsync(), Is.EqualTo("src/deep/file_name.txt"));
        Click(copy.Items.OfType<MenuItem>().Last());
        Assert.That(await main.Clipboard!.TryGetTextAsync(), Is.EqualTo(Path.Combine(f.Repository, "src/deep/file_name.txt")));
        menu.Close(); await Idle(main);
        TreeViewItem folder = FileStatusTestHelpers.Rows(view.Tree).Single(item => item.Tag is FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder, Key: "src/deep" });
        view.Tree.SelectedItem = folder; await Idle(main); menu = Open(view.Tree);
        Click(Item(menu, "CopyPathsToolStripMenuItem").Items.OfType<MenuItem>().First());
        Assert.That(await main.Clipboard!.TryGetTextAsync(), Is.EqualTo("src/deep"));
        Assert.That(Find<FileContentView>(main, "CommitFileContent").TextView.Text,
            Is.EqualTo("(2) src/deep/\n\nfile_name.txt\n\nother.txt\n"));
        Click(Item(menu, "tsmiMove")); menu.Close();
        await Until(() => main.OwnedWindows.Any(window => window.Title == "Rename / move"));
        Window prompt = main.OwnedWindows.Single(window => window.Title == "Rename / move");
        TextBox name = prompt.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.That(name.Text, Is.EqualTo("src/deep")); name.Text = "src/renamed";
        Click(prompt.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Rename / move")); await Idle(main);
        Assert.That(Directory.Exists(Path.Combine(f.Repository, "src/deep")), Is.False);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Repository, "src/renamed/file_name.txt")), Is.EqualTo("dirty worktree\n"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Repository, "src/renamed/other.txt")), Is.EqualTo("other dirty\n"));
    }

    [AvaloniaTest]
    public async Task TempFileShortcutOpensTheSelectedHistoricalBytesWithALiteralArgumentAndCleansUpAtClose()
    {
        using Fixture f = await CreateAsync(); MainWindow main = f.Window;
        PreferencesStore preferences = new(); preferences.Save(preferences.Load() with { UseBrowseForFileHistory = false });
        ListBox history = Find<ListBox>(main, "RevisionHistory");
        history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Nested base"); await Idle(main);
        SelectTab(main, "FileTreeTab"); await Idle(main);
        FileStatusListView view = Find<FileStatusListView>(main, "CommitFileStatusList"); view.SelectTags(["src/deep/file_name.txt"]); await Idle(main);
        ProcessStartInfo? launch = null; main.ExternalLauncher = info => { launch = info; return null; };
        string before = await Snapshot(f); ((TreeViewItem)view.Tree.SelectedItem!).Focus();
        main.KeyPressQwerty(PhysicalKey.F3, RawInputModifiers.Control); main.KeyReleaseQwerty(PhysicalKey.F3, RawInputModifiers.Control);
        await Until(() => launch is not null && !main.IsBusy);
        Assert.That(launch!.FileName, Is.EqualTo("xdg-open")); Assert.That(launch.UseShellExecute, Is.False);
        string file = launch.ArgumentList.Single(); Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("historical source\n"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Repository, "src/deep/file_name.txt")), Is.EqualTo("dirty worktree\n"));
        Assert.That(await Snapshot(f), Is.EqualTo(before));
        TreeViewItem selected = (TreeViewItem)view.Tree.SelectedItem!;
        Point position = Header(selected).TranslatePoint(new Point(38, 9), main)!.Value;
        main.MouseDown(position, MouseButton.Left); main.MouseUp(position, MouseButton.Left);
        main.MouseDown(position, MouseButton.Left); main.MouseUp(position, MouseButton.Left);
        await Until(() => main.OwnedWindows.OfType<FileHistoryDialog>().Any());
        FileHistoryDialog dialog = main.OwnedWindows.OfType<FileHistoryDialog>().Single();
        await Until(() => !dialog.IsBusy);
        Assert.That(Find<TextBox>(dialog, "FileHistoryRevision").Text, Is.EqualTo(((GitCommit)history.SelectedItem!).Hash));
        dialog.Close(); await Idle(main);
        main.Close(); Assert.That(File.Exists(file), Is.False);
    }

    [AvaloniaTest]
    public async Task ResetMenuCancelIsInertAndConfirmedResetChangesOnlyTheSelectedLiteralFile()
    {
        using Fixture f = await CreateAsync(); MainWindow main = f.Window; SelectTab(main, "FileTreeTab"); await Idle(main);
        FileStatusListView view = Find<FileStatusListView>(main, "CommitFileStatusList"); view.SelectTags(["src/deep/file_name.txt"]); await Idle(main);
        string before = await Snapshot(f); ContextMenu menu = Open(view.Tree);
        Click(Item(menu, "tsmiResetFileTo").Items.OfType<MenuItem>().First()); menu.Close();
        await Until(() => main.OwnedWindows.OfType<ResetChangesDialog>().Any());
        ResetChangesDialog dialog = main.OwnedWindows.OfType<ResetChangesDialog>().Single(); Click(Find<Button>(dialog, "CancelResetChanges")); await Idle(main);
        Assert.That(await Snapshot(f), Is.EqualTo(before));
        menu = Open(view.Tree); Click(Item(menu, "tsmiResetFileTo").Items.OfType<MenuItem>().First()); menu.Close();
        await Until(() => main.OwnedWindows.OfType<ResetChangesDialog>().Any()); dialog = main.OwnedWindows.OfType<ResetChangesDialog>().Single(); Click(Find<Button>(dialog, "ConfirmResetChanges")); await Idle(main);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Repository, "src/deep/file_name.txt")), Is.EqualTo("committed change\n"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Repository, "src/deep/other.txt")), Is.EqualTo("other dirty\n"));
    }

    [AvaloniaTest]
    public async Task ChangingRevisionsWhileFileTreeIsVisibleReloadsTheSamePathAndRightClickSelectsItsActualTarget()
    {
        using Fixture f = await CreateAsync(); MainWindow main = f.Window; SelectTab(main, "FileTreeTab"); await Idle(main);
        FileStatusListView view = Find<FileStatusListView>(main, "CommitFileStatusList"); view.SelectTags(["src/deep/file_name.txt"]); await Idle(main);
        ListBox history = Find<ListBox>(main, "RevisionHistory");
        history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Nested base"); await Idle(main);
        FileContentView content = Find<FileContentView>(main, "CommitFileContent");
        Assert.That(content.TextView.Text, Is.EqualTo("historical source\n"));
        TreeViewItem target = FileStatusTestHelpers.Rows(view.Tree).Single(item => item.Tag as string == "src/deep/other.txt");
        Point position = Header(target).TranslatePoint(new Point(38, 9), main)!.Value;
        main.MouseDown(position, MouseButton.Right); main.MouseUp(position, MouseButton.Right); Pump(main);
        Assert.That(view.Tree.ContextMenu!.IsOpen, Is.True);
        Assert.That((view.Tree.SelectedItem as TreeViewItem)?.Tag, Is.EqualTo("src/deep/other.txt"));
        Assert.That(Item(view.Tree.ContextMenu, "tsmiOpenRevisionFile").IsEnabled, Is.True);
        view.Tree.ContextMenu.Close(); await Idle(main);
        Assert.That(content.TextView.Text, Is.EqualTo("other base\n"));
        await main.OpenRepositoryAsync(f.Repository); await Idle(main);
        Assert.That(content.TextView.Text, Is.EqualTo("other base\n"));
    }

    [AvaloniaTest]
    public async Task FileActionsKeepGlobMetacharactersLiteralAndRefuseUntrackedCollisionsBeforeChangingTheIndex()
    {
        using Fixture f = await Fixture.CreateAsync("anchor\n", "anchor\n"); GitRepository repository = await GitRepository.OpenAsync(f.Repository);
        const string literal = ":name[1]* λ\n.txt"; await Write(f, literal, "original\n"); await Write(f, "name1-other.txt", "other\n");
        await repository.StageAllAsync(); await repository.CommitAsync("Literal files"); string source = await repository.ResolveRevisionAsync("HEAD");
        await Write(f, literal, "changed\n"); await Write(f, "name1-other.txt", "untouched dirty\n");
        await repository.RestoreRevisionFilesAsync(source, [literal]);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Repository, literal)), Is.EqualTo("original\n"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Repository, "name1-other.txt")), Is.EqualTo("untouched dirty\n"));
        await repository.MoveTrackedFileAsync(literal, "renamed[2] λ.txt");
        Assert.That(File.Exists(Path.Combine(f.Repository, literal)), Is.False);
        await repository.StopTrackingFileAsync("renamed[2] λ.txt"); Assert.That(File.Exists(Path.Combine(f.Repository, "renamed[2] λ.txt")), Is.True);
        await Write(f, literal, "untracked collision\n"); string before = await Snapshot(f);
        Assert.ThrowsAsync<InvalidOperationException>(() => repository.RestoreRevisionFilesAsync(source, [literal, "name1-other.txt"]));
        Assert.That(await Snapshot(f), Is.EqualTo(before));
        Assert.Throws<ArgumentException>(() => repository.MoveTrackedFileAsync("name1-other.txt", "../outside.txt"));
    }

    private static async Task<Fixture> CreateAsync()
    {
        Fixture f = await Fixture.CreateAsync("anchor\n", "anchor\n", commitInfoPosition: null);
        GitRepository repository = await GitRepository.OpenAsync(f.Repository);
        await Write(f, "src/deep/file_name.txt", "historical source\n"); await Write(f, "src/deep/other.txt", "other base\n");
        await Write(f, "documents/readme.txt", "documentation\n"); await repository.StageAllAsync(); await repository.CommitAsync("Nested base");
        await Write(f, "src/deep/file_name.txt", "committed change\n"); await Write(f, "src/deep/other.txt", "other change\n"); await Write(f, "src/new_file.txt", "added file\n");
        await repository.StageAllAsync(); await repository.CommitAsync("Three file change");
        await Write(f, "src/deep/file_name.txt", "dirty worktree\n"); await Write(f, "src/deep/other.txt", "other dirty\n");
        await f.Window.OpenRepositoryAsync(f.Repository); await Idle(f.Window);
        ListBox history = Find<ListBox>(f.Window, "RevisionHistory");
        history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Three file change");
        await Idle(f.Window); return f;
    }
    private static async Task Write(Fixture f, string path, string text)
    { string file = Path.Combine(f.Repository, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllTextAsync(file, text); }
    private static async Task<string> Snapshot(Fixture f) => (await f.Git.RunAsync(f.Repository, ["status", "--porcelain=v1", "-z"])).StandardOutput + "\0" + (await f.Git.RunAsync(f.Repository, ["diff", "--cached", "--binary"])).StandardOutput;
    private static void SelectTab(MainWindow main, string name) { TabControl tabs = Find<TabControl>(main, "DetailsTabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == name); Pump(main); }
    private static T Find<T>(Control owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static Grid Header(TreeViewItem item) => item.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "PART_Header" && grid.TemplatedParent == item);
    private static ContextMenu Open(TreeView tree)
    {
        tree.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = tree });
        Dispatcher.UIThread.RunJobs(); return tree.ContextMenu!;
    }
    private static MenuItem Item(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(TopLevel window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Idle(MainWindow main) { await Task.Delay(40); await Until(() => !main.IsBusy); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); Pump(main); }
    private static async Task Until(Func<bool> condition) { DateTime end = DateTime.UtcNow.AddSeconds(15); while (!condition() && DateTime.UtcNow < end) { await Task.Delay(20); } Assert.That(condition(), Is.True); }
    private static void Capture(MainWindow main, string name)
    {
        Pump(main); string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts/linux/screenshots"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name); using var frame = main.CaptureRenderedFrame()!; frame.Save(path); TestContext.AddTestAttachment(path);
    }
}

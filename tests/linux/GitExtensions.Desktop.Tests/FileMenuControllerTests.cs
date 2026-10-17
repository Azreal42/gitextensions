using System.Diagnostics;
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
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FileMenuControllerTests
{
    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task SharedCompareMenuUsesOriginalCommandsButDoesNotInventBrowseCallbacks(bool dark)
    {
        using Fixture git = await Fixture.CreateAsync();
        GitComparison pair = await git.Repository.GetComparisonAsync(new("HEAD"), new(Kind: GitCompareRevisionKind.WorkingTree));
        GitRevisionFile file = new("file.txt", 'M');
        FileMenuLeaf leaf = new(file, new("HEAD"), new("HEAD"), pair);
        using Harness f = new(git, [leaf], dark);
        f.OpenMenu();
        Assert.That(f.Menu.Items.OfType<MenuItem>().Select(item => item.Name), Is.EqualTo(new[]
        {
            "tsmiResetFileTo", "tsmiOpenWithDifftool", "tsmiOpenWorkingDirectoryFile", "tsmiOpenWorkingDirectoryFileWith", "tsmiOpenRevisionFile", "tsmiOpenRevisionFileWith",
            "tsmiEditWorkingDirectoryFile", "tsmiSaveAs", "tsmiMove", "CopyPathsToolStripMenuItem", "tsmiShowInFolder", "tsmiFileHistory", "tsmiBlame", "tsmiFindFile", "tsmiStopTracking", "sortListByContextMenuItem"
        }));
        Assert.That(((Image)f.Item("tsmiOpenWithDifftool").Icon!).Tag, Is.EqualTo("Diff.png"));
        Assert.That(f.Item("tsmiOpenWithDifftool").InputGesture, Is.EqualTo(new KeyGesture(Key.F3)));
        Assert.That(f.Item("tsmiOpenRevisionFile").InputGesture, Is.EqualTo(new KeyGesture(Key.F3, KeyModifiers.Control)));
        foreach (string name in new[] { "tsmiShowInFileTree", "tsmiFilterFileInGrid", "tsmiOpenFindInCommitFilesGitGrepDialog", "tsmiShowFindInCommitFilesGitGrep" })
        { Assert.That(f.Menu.Items.OfType<MenuItem>().Any(item => item.Name == name), Is.False, name); }
        Assert.That(f.Item("tsmiFileHistory").FontWeight, Is.Not.EqualTo(Avalonia.Media.FontWeight.Bold));
        Capture(f.Owner, "shared-file-menu-compare-" + (dark ? "Dark" : "Light") + ".png");
    }

    [AvaloniaTest]
    public async Task TheFirstStageKeyStagesOnlyItsLiteralFileAndTypingInTheRealFilterEditorRemainsText()
    {
        using Fixture git = await Fixture.CreateAsync();
        const string literal = ":name[1]* λ\tline\n.txt";
        await File.WriteAllTextAsync(git.PathInRepository(literal), "base\n");
        await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Literal");
        await File.WriteAllTextAsync(git.PathInRepository(literal), "selected worktree\n");
        await File.WriteAllTextAsync(git.File, "other unstaged\n");
        GitComparison pair = await git.Repository.GetComparisonAsync(new(Kind: GitCompareRevisionKind.Index), new(Kind: GitCompareRevisionKind.WorkingTree));
        GitComparisonFileMetadata native = (await git.Repository.GetComparisonFileMetadataAsync(pair, null)).Single(item => item.File.Path == literal);
        Assert.That(native.Staged, Is.EqualTo(GitFileStagedStatus.WorkTree));
        FileMenuLeaf leaf = new(native.File, pair.Base, pair.Compare, pair, FileMenuStage.WorkingTree, native.IsTracked, native.IsSubmodule);
        using Harness f = new(git, [leaf]);
        f.Execute = (command, snapshot) => command == FileMenuCommand.Stage ? git.Repository.StageAsync(snapshot.Paths) : throw new AssertionException("Unexpected command " + command);
        PushProcessTestDriver.Pointer(RowTarget((TreeViewItem)f.Tree.SelectedItem!));
        Press(f.Owner, PhysicalKey.S);
        await Until(() => f.Operation is { IsCompleted: true }); await f.Operation!;
        Assert.That(f.Commands, Is.EqualTo(new[] { FileMenuCommand.Stage }));
        Assert.That(System.Text.Encoding.UTF8.GetString(await git.Repository.GetIndexFileBytesAsync(literal)), Is.EqualTo("selected worktree\n"));
        Assert.That(System.Text.Encoding.UTF8.GetString(await git.Repository.GetIndexFileBytesAsync("file.txt")), Is.EqualTo("base\n"));
        TextBox editor = FilterEditor(f.View); PushProcessTestDriver.Pointer(editor); f.Owner.KeyTextInput("s");
        await Until(() => f.View.FilterText == "s");
        Assert.That(f.Owner.FocusManager!.GetFocusedElement(), Is.SameAs(editor));
        Assert.That(f.Commands, Is.EqualTo(new[] { FileMenuCommand.Stage }), "A plain letter in the combo's real editor is never a menu command.");
    }

    [AvaloniaTest]
    public async Task NativeStageStateNotTheArtificialEndpointControlsTheVisibleStageAndUnstageCommands()
    {
        using Fixture git = await Fixture.CreateAsync();
        GitRevisionFile file = new("file.txt", 'M');
        FileMenuLeaf leaf = new(file, new("HEAD"), new(Kind: GitCompareRevisionKind.Index), Stage: FileMenuStage.Unknown);
        using Harness f = new(git, [leaf]); f.OpenMenu();
        Assert.That(f.Menu.Items.OfType<MenuItem>().Any(item => item.Name is "tsmiStageFile" or "tsmiUnstageFile"), Is.False);
        f.Menu.Close(); f.Selection = f.Selection with { Leaves = [leaf with { Stage = FileMenuStage.Index }] }; f.OpenMenu();
        Assert.That(f.Item("tsmiUnstageFile").IsEnabled, Is.True);
        Assert.That(f.Menu.Items.OfType<MenuItem>().Any(item => item.Name == "tsmiStageFile"), Is.False);
        f.Menu.Close(); f.Selection = f.Selection with { IsDisplayOnly = true }; f.OpenMenu();
        Assert.That(f.Menu.Items.OfType<MenuItem>().Any(item => item.Name is "tsmiStageFile" or "tsmiUnstageFile"), Is.False);
        Assert.That(f.Item("tsmiResetFileTo").IsEnabled, Is.False);
        Assert.That(f.Item("tsmiOpenWithDifftool").IsEnabled, Is.False);
    }

    [AvaloniaTest]
    public async Task ARealMenuClickCannotExecuteTheCapturedActionAfterItsHostSnapshotBecameStale()
    {
        using Fixture git = await Fixture.CreateAsync();
        FileMenuLeaf leaf = new(new("file.txt", 'M'), new(Kind: GitCompareRevisionKind.Index), new(Kind: GitCompareRevisionKind.WorkingTree), Stage: FileMenuStage.WorkingTree);
        using Harness f = new(git, [leaf]); f.OpenMenu();
        MenuItem stage = f.Item("tsmiStageFile"); Assert.That(stage.IsEnabled, Is.True);
        f.Current = false; PushProcessTestDriver.Pointer(stage); Pump(f.Owner);
        Assert.That(f.Commands, Is.Empty);
        Assert.That((await git.GitAsync("status", "--porcelain=v1")).StandardOutput, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task FindFileSearchesTheCapturedOldAndNewNamesAndSelectsTheRealResultWithoutChangingTheRegex(bool dark)
    {
        using Fixture git = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(git.PathInRepository("old-name.txt"), "rename\n");
        await File.WriteAllTextAsync(git.PathInRepository("z-next.txt"), "next\n");
        await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Before rename");
        await git.GitAsync("mv", "old-name.txt", "new-name.txt");
        await File.WriteAllTextAsync(git.PathInRepository("z-next.txt"), "next change\n");
        await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("After rename");
        GitComparison pair = await git.Repository.GetComparisonAsync(new("HEAD~1"), new("HEAD"));
        FileMenuLeaf[] leaves = pair.Files.Select(file => new FileMenuLeaf(file, pair.Base, pair.Compare, pair)).ToArray();
        using Harness f = new(git, leaves, dark); f.OpenMenu(); PushProcessTestDriver.Pointer(f.Item("tsmiFindFile"));
        await Until(() => f.Owner.OwnedWindows.OfType<FileListFindDialog>().Any());
        FileListFindDialog picker = f.Owner.OwnedWindows.OfType<FileListFindDialog>().Single(); Pump(picker);
        TextBox input = picker.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "txtSearchBox");
        Assert.That(picker.FocusManager!.GetFocusedElement(), Is.SameAs(input));
        picker.KeyTextInput("old-name");
        await Until(() => picker.GetVisualDescendants().OfType<ListBox>().Single().ItemCount == 1);
        Assert.That(((FileStatusListEntry)picker.GetVisualDescendants().OfType<ListBox>().Single().Items[0]!).Path, Is.EqualTo("new-name.txt"));
        Capture(picker, "file-search-window-" + (dark ? "dark" : "light") + ".png");
        Press(picker, PhysicalKey.Enter, f.Owner);
        await Until(() => !picker.IsVisible && f.Tree.SelectedItem is TreeViewItem { Tag: FileMenuLeaf selected } && selected.File.Path == "new-name.txt");
        Assert.That(f.View.FilterText, Is.Empty);
        Assert.That(f.Owner.FocusManager!.GetFocusedElement(), Is.SameAs(f.Tree.SelectedItem));
        Assert.That((await git.GitAsync("status", "--porcelain=v1")).StandardOutput, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.Index)]
    [TestCase(GitCompareRevisionKind.WorkingTree)]
    public async Task ArtificialBlameExplicitlyUsesHeadHistoryAndNeverPretendsToReadTheIndexOrWorktree(GitCompareRevisionKind kind)
    {
        using Fixture git = await Fixture.CreateAsync();
        PreferencesStore preferences = new(); preferences.Save(preferences.Load() with { UseBrowseForFileHistory = false });
        string head = await git.Repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(git.File, "different index\n"); await git.Repository.StageAllAsync();
        await File.WriteAllTextAsync(git.File, "different worktree\n");
        FileMenuLeaf leaf = new(new("file.txt", 'M'), new("HEAD"), new(Kind: kind), Stage: kind == GitCompareRevisionKind.Index ? FileMenuStage.Index : FileMenuStage.WorkingTree);
        using Harness f = new(git, [leaf]);
        FileMenuOperations operations = new(f.Owner, async (_, action, _) => await action(CancellationToken.None), _ => f.Current, _ => throw new AssertionException("History must not launch an external application."), CancellationToken.None);
        f.Execute = operations.ExecuteAsync; f.OpenMenu(); PushProcessTestDriver.Pointer(f.Item("tsmiBlame"));
        await Until(() => f.Owner.OwnedWindows.OfType<FileHistoryDialog>().Any());
        FileHistoryDialog history = f.Owner.OwnedWindows.OfType<FileHistoryDialog>().Single();
        await Until(() => !history.IsBusy && history.History.Count > 0); Pump(history);
        Assert.That(history.InitialSource.Kind, Is.EqualTo(kind));
        Assert.That(PushProcessTestDriver.Field<TextBox>(history, "FileHistoryRevision").Text, Is.EqualTo("HEAD"));
        Assert.That(PushProcessTestDriver.Field<TextBlock>(history, "FileHistorySourceNotice").Text, Does.Contain(kind == GitCompareRevisionKind.Index ? "index" : "working directory"));
        Assert.That(PushProcessTestDriver.Field<TextBlock>(history, "FileHistorySourceNotice").IsEffectivelyVisible, Is.True);
        Assert.That(PushProcessTestDriver.Field<TabControl>(history, "FileHistoryTabs").SelectedIndex, Is.EqualTo(3));
        BlameView blame = PushProcessTestDriver.Field<BlameView>(history, "FileHistoryBlame");
        Assert.That(blame.TextView.Text, Is.EqualTo("base\n")); Assert.That(blame.Lines.Select(line => line.Line.Hash).Distinct(), Is.EqualTo(new[] { head }));
        Assert.That(history.LastError, Is.Null); history.Close(); await f.Operation!;
        Assert.That(System.Text.Encoding.UTF8.GetString(await git.Repository.GetIndexFileBytesAsync("file.txt")), Is.EqualTo("different index\n"));
        Assert.That(await File.ReadAllTextAsync(git.File), Is.EqualTo("different worktree\n"));
    }

    [AvaloniaTest]
    public async Task ArtificialBlameForANewFileUsesTheNoCommitsStateWithoutInventingAHeadBlob()
    {
        using Fixture git = await Fixture.CreateAsync(); string path = "new[1] λ\tfile\n.txt";
        PreferencesStore preferences = new(); preferences.Save(preferences.Load() with { UseBrowseForFileHistory = false });
        await File.WriteAllTextAsync(git.PathInRepository(path), "new file\n"); await git.Repository.StageAsync([path]);
        FileMenuLeaf leaf = new(new(path, 'A'), new("HEAD"), new(Kind: GitCompareRevisionKind.Index), Stage: FileMenuStage.Index);
        using Harness f = new(git, [leaf]);
        FileMenuOperations operations = new(f.Owner, async (_, action, _) => await action(CancellationToken.None), _ => f.Current, _ => throw new AssertionException("No external blame."), CancellationToken.None);
        f.Execute = operations.ExecuteAsync; f.OpenMenu(); PushProcessTestDriver.Pointer(f.Item("tsmiBlame"));
        await Until(() => f.Owner.OwnedWindows.OfType<FileHistoryDialog>().Any());
        FileHistoryDialog history = f.Owner.OwnedWindows.OfType<FileHistoryDialog>().Single(); await Until(() => !history.IsBusy); Pump(history);
        Assert.That(history.History, Is.Empty); Assert.That(history.LastError, Is.Null);
        Assert.That(PushProcessTestDriver.Field<TabControl>(history, "FileHistoryTabs").SelectedIndex, Is.EqualTo(1));
        Assert.That(PushProcessTestDriver.Field<FileHistoryContentView>(history, "FileHistoryDiff").TextView.Text, Is.EqualTo("No commits found."));
        Assert.That(PushProcessTestDriver.Field<TabItem>(history, "FileHistoryBlameTab").IsEnabled, Is.False);
        history.Close(); await f.Operation!;
        Assert.That(System.Text.Encoding.UTF8.GetString(await git.Repository.GetIndexFileBytesAsync(path)), Is.EqualTo("new file\n"));
    }

    [AvaloniaTest]
    public async Task ConflictingResetSourcesForOnePathAreRefusedBeforeConfirmationOrAnyEffect()
    {
        using Fixture git = await Fixture.CreateAsync(); string first = await git.Repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(git.File, "second source\n"); await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Second");
        string second = await git.Repository.ResolveRevisionAsync("HEAD"); await File.WriteAllTextAsync(git.File, "preserve worktree\n");
        FileMenuLeaf[] leaves = [new(new("file.txt", 'M'), new(first), new(second)), new(new("file.txt", 'M'), new(second), new(first))];
        using Harness f = new(git, leaves);
        FileMenuOperations operations = new(f.Owner, async (_, action, _) => await action(CancellationToken.None), _ => f.Current, _ => throw new AssertionException("No process."), CancellationToken.None);
        string before = (await git.GitAsync("status", "--porcelain=v1")).StandardOutput;
        Exception? error = null;
        try { await operations.ExecuteAsync(FileMenuCommand.ResetToFirst, f.Selection); } catch (InvalidOperationException caught) { error = caught; }
        Assert.That(error?.Message, Does.Contain("different reset sources")); Assert.That(f.Owner.OwnedWindows, Is.Empty);
        Assert.That(await File.ReadAllTextAsync(git.File), Is.EqualTo("preserve worktree\n"));
        Assert.That(System.Text.Encoding.UTF8.GetString(await git.Repository.GetIndexFileBytesAsync("file.txt")), Is.EqualTo("second source\n"));
        Assert.That((await git.GitAsync("status", "--porcelain=v1")).StandardOutput, Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task TheRealContextSortUsesTheSharedPreferenceAndReturnsTheFirstArrowToTheSelectedLeaf()
    {
        using Fixture git = await Fixture.CreateAsync(); await File.WriteAllTextAsync(git.PathInRepository("other.cs"), "other\n"); await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Other");
        FileMenuLeaf[] leaves = [new(new("file.txt", 'M'), new("HEAD~1"), new("HEAD")), new(new("other.cs", 'A'), new("HEAD~1"), new("HEAD"))];
        using Harness f = new(git, leaves); PushProcessTestDriver.Pointer(RowTarget((TreeViewItem)f.Tree.SelectedItem!)); f.OpenMenu();
        MenuItem sortRoot = f.Item("sortListByContextMenuItem"); Assert.That(((Image)sortRoot.Icon!).Tag, Is.EqualTo("SortBy.png"));
        Assert.That(sortRoot.Items.OfType<MenuItem>().Select(item => ((AccessText)item.Header!).Text), Is.EqualTo(new[] { "File _path - tree", "_File path - flat", "File _extension - tree", "File e_xtension - flat", "File _status - tree", "File s_tatus - flat" }));
        TopLevel popup = TopLevel.GetTopLevel(sortRoot)!; Point hover = sortRoot.TranslatePoint(new Point(sortRoot.Bounds.Width * .7, sortRoot.Bounds.Height / 2), popup)!.Value;
        popup.MouseMove(hover); await Until(() => sortRoot.IsSubMenuOpen && sortRoot.Items.OfType<MenuItem>().First().Bounds.Height > 0);
        MenuItem sort = sortRoot.Items.OfType<MenuItem>().Single(item => item.Name == "SharedFileMenuTreeContextSort_FileExtensionFlat"); PushProcessTestDriver.Pointer(sort); Pump(f.Owner);
        Assert.That(f.View.SortMode, Is.EqualTo(FileStatusListSortMode.FileExtensionFlat));
        Assert.That(f.Owner.FocusManager!.GetFocusedElement(), Is.SameAs(f.Tree.SelectedItem));
        object? selected = ((TreeViewItem)f.Tree.SelectedItem!).Tag;
        Press(f.Owner, PhysicalKey.ArrowUp); Pump(f.Owner);
        Assert.That(((TreeViewItem)f.Tree.SelectedItem!).Tag, Is.Not.EqualTo(selected), "The first arrow after the actual context-menu action navigates a newly built row.");
    }

    [AvaloniaTest]
    public async Task ARealGitlinkOffersOpenWithGitExtensionsButNeverStopTrackingOrFileBlame()
    {
        using Fixture git = await Fixture.CreateAsync(); string head = await git.Repository.ResolveRevisionAsync("HEAD");
        await git.GitAsync("update-index", "--add", "--cacheinfo", "160000," + head + ",module"); await git.Repository.CommitAsync("Gitlink");
        GitComparison pair = await git.Repository.GetComparisonAsync(new(head), new("HEAD"));
        GitComparisonFileMetadata facts = (await git.Repository.GetComparisonFileMetadataAsync(pair, null)).Single(item => item.File.Path == "module");
        Assert.That(facts.IsSubmodule, Is.True);
        using Harness f = new(git, [new(facts.File, pair.Base, pair.Compare, pair, IsSubmodule: facts.IsSubmodule)]); f.OpenMenu();
        Assert.That(((AccessText)f.Item("OpenSubmodule").Header!).Text, Is.EqualTo("Open with Git Extensions"));
        Assert.That(f.Menu.Items.OfType<MenuItem>().Any(item => item.Name == "tsmiStopTracking"), Is.False);
        Assert.That(f.Item("tsmiBlame").IsEnabled, Is.False);
    }

    [AvaloniaTest]
    public async Task ParentTreeCommandsPrecedeBusinessAndSelectOnlyTheirDescendantFiles()
    {
        using Fixture git = await Fixture.CreateAsync();
        FileMenuLeaf[] leaves = [new(new("src/a.cs", 'M'), new("HEAD"), new("HEAD")), new(new("src/b.cs", 'M'), new("HEAD"), new("HEAD")), new(new("outside.txt", 'M'), new("HEAD"), new("HEAD"))];
        using Harness f = new(git, leaves); f.View.SortMode = FileStatusListSortMode.FilePath;
        TreeViewItem folder = f.Tree.Items.OfType<TreeViewItem>().Single(item => item.Tag is FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder, Key: "src" });
        f.Tree.SelectedItem = folder; f.Selection = f.Selection with { Folder = "src", Leaves = leaves.Take(2).ToArray(), CopyPaths = ["src"] }; Pump(f.Owner); PushProcessTestDriver.Pointer(RowTarget(folder)); f.OpenMenu();
        Assert.That(f.Menu.Items.OfType<MenuItem>().Take(4).Select(item => item.Name), Is.EqualTo(new[] { "FileTreeSelectAll", "FileTreeCollapseAll", "FileTreeExpandAll", "tsmiResetFileTo" }));
        PushProcessTestDriver.Pointer(f.Item("FileTreeSelectAll")); Pump(f.Owner);
        Assert.That(f.Tree.SelectedItems!.OfType<TreeViewItem>().Select(item => ((FileMenuLeaf)item.Tag!).File.Path), Is.EquivalentTo(new[] { "src/a.cs", "src/b.cs" }));
    }

    [AvaloniaTest]
    [TestCase(true, PhysicalKey.Escape)]
    [TestCase(true, PhysicalKey.Enter)]
    [TestCase(false, PhysicalKey.Escape)]
    public async Task TheFirstResetKeyOnlyOffersFirstAndEscapeLeavesGitUntouched(bool hasFirst, PhysicalKey cancellationKey)
    {
        using Fixture git = await Fixture.CreateAsync(); string first = await git.Repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(git.File, "second revision\n"); await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Second revision");
        string second = await git.Repository.ResolveRevisionAsync("HEAD"); await File.WriteAllTextAsync(git.File, "keep working changes\n");
        FileMenuLeaf leaf = new(new("file.txt", 'M'), hasFirst ? new(first) : null, new(second));
        using Harness f = new(git, [leaf]);
        FileMenuOperations operations = new(f.Owner, async (_, action, _) => await action(CancellationToken.None), _ => f.Current,
            _ => throw new AssertionException("Reset must not launch an external application."), CancellationToken.None);
        f.Execute = operations.ExecuteAsync;
        string before = (await git.GitAsync("status", "--porcelain=v1")).StandardOutput;
        PushProcessTestDriver.Pointer(RowTarget((TreeViewItem)f.Tree.SelectedItem!));
        Assert.That(f.Owner.FocusManager!.GetFocusedElement(), Is.SameAs(f.Tree.SelectedItem));
        Press(f.Owner, PhysicalKey.R); Pump(f.Owner);
        if (hasFirst)
        {
            await Until(() => f.Owner.OwnedWindows.OfType<ResetChangesDialog>().Any());
            ResetChangesDialog dialog = f.Owner.OwnedWindows.OfType<ResetChangesDialog>().Single(); Pump(dialog);
            Assert.That(f.Commands, Is.EqualTo(new[] { FileMenuCommand.ResetToFirst }));
            Assert.That(PushProcessTestDriver.Field<TextBox>(dialog, "ResetChangesMessage").Text, Does.Contain("First: A").And.Contain("file.txt"));
            Button cancel = PushProcessTestDriver.Field<Button>(dialog, "CancelResetChanges");
            Assert.That(dialog.FocusManager!.GetFocusedElement(), Is.SameAs(cancel), "The confirmation itself must give the first key to its safe default button.");
            Assert.That(cancel.IsDefault, Is.True);
            Press(dialog, cancellationKey, f.Owner); await Until(() => f.Operation is { IsCompleted: true }); await f.Operation!;
            Assert.That(dialog.IsVisible, Is.False);
        }
        else
        {
            Assert.That(f.Owner.OwnedWindows, Is.Empty, "R must not fall back to Second when First is absent.");
            Assert.That(f.Commands, Is.Empty); Assert.That(f.Operation, Is.Null);
        }
        MenuItem reset = f.Controller.BuildItems(f.Selection).OfType<MenuItem>().Single(item => item.Name == "tsmiResetFileTo");
        Assert.That(reset.InputGesture, Is.Null);
        Assert.That(reset.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiResetFileToSelected").InputGesture, Is.Null);
        MenuItem? firstItem = reset.Items.OfType<MenuItem>().SingleOrDefault(item => item.Name == "tsmiResetFileToParent");
        if (hasFirst) { Assert.That(firstItem!.InputGesture, Is.EqualTo(new KeyGesture(Key.R))); }
        else { Assert.That(firstItem, Is.Null); }
        Assert.That(await File.ReadAllTextAsync(git.File), Is.EqualTo("keep working changes\n"));
        Assert.That(System.Text.Encoding.UTF8.GetString(await git.Repository.GetIndexFileBytesAsync("file.txt")), Is.EqualTo("second revision\n"));
        Assert.That(await git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(second));
        Assert.That((await git.GitAsync("status", "--porcelain=v1")).StandardOutput, Is.EqualTo(before));
    }

    private sealed class Harness : IDisposable
    {
        internal readonly Window Owner;
        internal readonly TreeView Tree = new() { Name = "SharedFileMenuTree", SelectionMode = SelectionMode.Multiple };
        internal readonly FileStatusListView View;
        internal readonly ContextMenu Menu = new() { Name = "SharedFileMenu" };
        internal readonly List<FileMenuCommand> Commands = [];
        internal readonly FileStatusListContextMenuController Controller;
        internal FileMenuSelection Selection;
        internal bool Current = true;
        internal Func<FileMenuCommand, FileMenuSelection, Task> Execute = (_, _) => Task.CompletedTask;
        internal Task? Operation;
        internal Harness(Fixture git, IReadOnlyList<FileMenuLeaf> leaves, bool dark = false)
        {
            Owner = new Window { Width = 850, Height = 750, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            View = new(Tree, preferencesStore: new FileStatusListPreferencesStore(git.Output("controller-preferences"))) { SortMode = FileStatusListSortMode.FilePathFlat };
            Selection = new(git.Repository, leaves, leaves.Select(leaf => leaf.File.Path).ToArray(), HostIdentity: this);
            TranslationCatalog translations = TranslationCatalog.LoadDefault();
            FileMenuHost host = new()
            {
                CaptureSelection = () => Selection, IsCurrent = snapshot => Current && ReferenceEquals(snapshot.Repository, git.Repository), IsBusy = () => false,
                CreateAction = (category, name, label, icon, action, gesture) =>
                {
                    MenuItem item = OriginalMenuItems.Create(translations, category, name, label, icon, gesture);
                    item.Click += async (_, args) => { if (args.Source == item && item.IsEnabled) { args.Handled = true; await action(); } }; return item;
                },
                ExecuteAsync = (command, snapshot) => { Commands.Add(command); return Operation = Execute(command, snapshot); }
            };
            Controller = new(View, translations, host); Menu.Opening += (_, _) => Menu.ItemsSource = Controller.BuildItems(Selection); Tree.ContextMenu = Menu;
            Owner.AddHandler(InputElement.KeyDownEvent, async (_, args) => await Controller.HandleShortcutAsync(args), RoutingStrategies.Tunnel);
            Owner.Content = View; Owner.Show();
            View.SetGroups([new FileStatusListGroup("files", "files", new TextBlock { Text = "Files" }, leaves.Select(leaf => new FileStatusListEntry(leaf, leaf.File.Path, leaf.File.OriginalPath, leaf.File.Status)).ToArray())]);
            View.SelectTags([leaves[0]]); Pump(Owner);
        }
        internal void OpenMenu()
        {
            Control row = RowTarget((TreeViewItem)Tree.SelectedItem!); Pump(Owner);
            Point point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), Owner)!.Value;
            Assert.That((Owner.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True);
            Owner.MouseMove(point); Owner.MouseDown(point, MouseButton.Right); Owner.MouseUp(point, MouseButton.Right); Pump(Owner);
            Assert.That(Menu.IsOpen, Is.True);
        }
        internal MenuItem Item(string name) => Menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
        public void Dispose() { foreach (Window child in Owner.OwnedWindows.ToArray()) { child.Close(); } Menu.Close(); Owner.Close(); View.Dispose(); }
    }
    private static Control RowTarget(TreeViewItem row) => row.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header" && control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() == row);
    private static TextBox FilterEditor(FileStatusListView view) => view.GetVisualDescendants().OfType<ComboBox>().Single().GetVisualDescendants().OfType<TextBox>().Single();
    private static void Pump(TopLevel top) { Dispatcher.UIThread.RunJobs(); top.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Press(Window window, PhysicalKey key, Window? release = null)
    { window.KeyPressQwerty(key, RawInputModifiers.None); if ((release ?? window).IsVisible) { (release ?? window).KeyReleaseQwerty(key, RawInputModifiers.None); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True); }
    private static void Capture(Window owner, string name)
    { Pump(owner); using var frame = owner.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string folder = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(folder); string file = Path.Combine(folder, name); frame!.Save(file); TestContext.AddTestAttachment(file); }
}

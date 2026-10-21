using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class BrowseDiffHotkeysTests
{
    [AvaloniaTest]
    public async Task RemappedStageUsesTheCapturedLiteralFileAndNoneRetiresBothOldAndNewKeys()
    {
        using Profile profile = new(); using GitFixture git = await GitFixture.CreateAsync();
        const string literal = ":name[1]* λ.txt";
        await File.WriteAllTextAsync(git.PathInRepository(literal), "base literal\n");
        await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Literal base");
        await File.WriteAllTextAsync(git.PathInRepository(literal), "selected literal\n");
        await File.WriteAllTextAsync(git.File, "other unstaged\n");
        GitComparison pair = await git.Repository.GetComparisonAsync(new(Kind: GitCompareRevisionKind.Index), new(Kind: GitCompareRevisionKind.WorkingTree));
        GitComparisonFileMetadata native = (await git.Repository.GetComparisonFileMetadataAsync(pair, null)).Single(item => item.File.Path == literal);
        FileMenuLeaf leaf = new(native.File, pair.Base, pair.Compare, pair, FileMenuStage.WorkingTree);
        using Harness f = new(git, [leaf]);
        f.Execute = (command, selection) => command == FileMenuCommand.Stage ? git.Repository.StageAsync(selection.Paths) : throw new AssertionException("Unexpected " + command);
        profile.Set("StageSelectedFile", new(Key.F8));
        MenuItem menu = f.Controller.BuildItems(f.Capture()).OfType<MenuItem>().Single(item => item.Name == "tsmiStageFile");
        Assert.That(menu.InputGesture, Is.EqualTo(new KeyGesture(Key.F8))); Assert.That(menu.Tag, Is.EqualTo(FileMenuCommand.Stage));
        f.FocusFirst(); Press(f.Owner, PhysicalKey.S); Assert.That(await f.Pending, Is.False); Assert.That(f.Calls, Is.Empty);
        Press(f.Owner, PhysicalKey.F8); Assert.That(await f.Pending.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(System.Text.Encoding.UTF8.GetString(await git.Repository.GetIndexFileBytesAsync(literal)), Is.EqualTo("selected literal\n"));
        Assert.That(System.Text.Encoding.UTF8.GetString(await git.Repository.GetIndexFileBytesAsync("file.txt")), Is.EqualTo("base\n"));
        Assert.That(f.Calls.Single().Selection.Paths, Is.EqualTo(new[] { literal }));
        profile.Set("StageSelectedFile", null); Assert.That(menu.InputGesture, Is.Null, "Already built menu follows actual Save.");
        f.FocusFirst(); Press(f.Owner, PhysicalKey.F8); Assert.That(await f.Pending, Is.False);
        Assert.That(f.Calls, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task FirstUnavailableConfiguredIdentityDoesNotFallThroughToAnAvailableSecondBinding()
    {
        using Profile profile = new(); using GitFixture git = await GitFixture.CreateAsync();
        FileMenuLeaf leaf = new(new("file.txt", 'M'), new("HEAD"), new("HEAD"), Stage: FileMenuStage.WorkingTree);
        using Harness f = new(git, [leaf]);
        profile.Set("DeleteSelectedFiles", new(Key.F8)); profile.Set("StageSelectedFile", new(Key.F8));
        object[] menu = f.Controller.BuildItems(f.Capture());
        Assert.That(menu.OfType<MenuItem>().Any(item => item.Tag is FileMenuCommand.DeleteFile), Is.False);
        Assert.That(menu.OfType<MenuItem>().Single(item => item.Tag is FileMenuCommand.Stage).IsEnabled, Is.True);
        f.FocusFirst(); Press(f.Owner, PhysicalKey.F8); Assert.That(await f.Pending, Is.False); Assert.That(f.Calls, Is.Empty);
        profile.Set("DeleteSelectedFiles", null);
        Press(f.Owner, PhysicalKey.F8); Assert.That(await f.Pending, Is.True);
        Assert.That(f.Calls.Single().Command, Is.EqualTo(FileMenuCommand.Stage));
    }

    [AvaloniaTest]
    public async Task ConfiguredArrowRunsItsCurrentLeafWhileOrdinaryArrowsAndFixedCopySaveKeepTheirRoles()
    {
        using Profile profile = new(); using GitFixture git = await GitFixture.CreateAsync();
        await File.WriteAllTextAsync(git.PathInRepository("z-next.txt"), "next\n");
        FileMenuLeaf first = new(new("file.txt", 'M'), new("HEAD"), new("HEAD"));
        FileMenuLeaf second = first with { File = new("z-next.txt", 'M') };
        using Harness f = new(git, [first, second]);
        profile.Set("ShowHistory", new(Key.Down, KeyModifiers.Control));
        f.FocusFirst(); Press(f.Owner, PhysicalKey.ArrowDown); Assert.That(await f.Pending, Is.False);
        Assert.That(f.View.SelectedTags, Is.EqualTo(new[] { second }));
        GitCommandLog.Clear(); Press(f.Owner, PhysicalKey.ArrowDown, RawInputModifiers.Control); Assert.That(await f.Pending, Is.True);
        Assert.That(f.Calls.Single().Selection.Paths, Is.EqualTo(new[] { "z-next.txt" })); Assert.That(GitCommandLog.Snapshot(), Is.Empty);
        Press(f.Owner, PhysicalKey.H); Assert.That(await f.Pending, Is.False);
        Press(f.Owner, PhysicalKey.C, RawInputModifiers.Control); Assert.That(await f.Pending, Is.True);
        Assert.That(f.Calls.Last().Command, Is.EqualTo(FileMenuCommand.CopyFullNative));
        Press(f.Owner, PhysicalKey.S, RawInputModifiers.Control); Assert.That(await f.Pending, Is.True);
        Assert.That(f.Calls.Last().Command, Is.EqualTo(FileMenuCommand.SaveAs));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task DiffAndTreeGrepBindingsAreIndependentAndFindFileUsesTheRealLocalPicker(bool tree)
    {
        using Profile profile = new(); using GitFixture git = await GitFixture.CreateAsync();
        FileMenuLeaf leaf = new(new("file.txt", 'M'), new("HEAD"), new("HEAD"));
        using Harness f = new(git, [leaf], new(Grep: true)); f.IsFileTree = tree;
        profile.Set("FindInCommitFilesUsingGitGrep_DiffTab", new(Key.F8));
        profile.Set("FindInCommitFilesUsingGitGrep_FileTreeTab", new(Key.F9));
        profile.Set("FindFile", new(Key.F10));
        MenuItem grep = f.Controller.BuildItems(f.Capture()).OfType<MenuItem>().Single(item => item.Name == "tsmiOpenFindInCommitFilesGitGrepDialog");
        Assert.That(grep.InputGesture, Is.EqualTo(new KeyGesture(tree ? Key.F9 : Key.F8)));
        f.FocusFirst(); Press(f.Owner, tree ? PhysicalKey.F8 : PhysicalKey.F9); Assert.That(await f.Pending, Is.False); Assert.That(f.Calls, Is.Empty);
        Press(f.Owner, tree ? PhysicalKey.F9 : PhysicalKey.F8); Assert.That(await f.Pending, Is.True);
        Assert.That(f.Calls.Single().Command, Is.EqualTo(FileMenuCommand.FindInCommitFiles));
        int calls = f.Calls.Count; string originalFilter = f.View.FilterText; GitCommandLog.Clear();
        Press(f.Owner, PhysicalKey.F10);
        await Until(() => f.Owner.OwnedWindows.OfType<FileListFindDialog>().Any(window => window.IsVisible), f.Owner);
        FileListFindDialog picker = f.Owner.OwnedWindows.OfType<FileListFindDialog>().Single(window => window.IsVisible);
        TextBox input = Node<TextBox>(picker, "txtSearchBox"); Assert.That(input.IsKeyboardFocusWithin, Is.True);
        picker.KeyTextInput("file"); Pump(picker);
        Assert.That(Node<ListBox>(picker, "listBoxSearchResult").ItemCount, Is.EqualTo(1));
        Press(picker, PhysicalKey.Escape); Assert.That(await f.Pending, Is.True);
        Assert.That(f.Calls, Has.Count.EqualTo(calls)); Assert.That(f.View.FilterText, Is.EqualTo(originalFilter));
        Assert.That(GitCommandLog.Snapshot(), Is.Empty, "Local file picking never becomes a grep or a Git discovery query.");
    }

    [AvaloniaTest]
    public async Task ProgrammaticCommandsRecheckCurrentBusyPolicyAndDoNotInventUnsupportedOperations()
    {
        using Profile profile = new(); using GitFixture git = await GitFixture.CreateAsync();
        FileMenuLeaf leaf = new(new("file.txt", 'M'), new(Kind: GitCompareRevisionKind.Index), new(Kind: GitCompareRevisionKind.WorkingTree), Stage: FileMenuStage.WorkingTree);
        using Harness f = new(git, [leaf]); GitCommandLog.Clear();
        f.Current = false; Assert.That(await f.Controller.ExecuteCommandAsync(FileMenuCommand.Stage), Is.False);
        f.Current = true; f.Busy = true; Assert.That(await f.Controller.ExecuteCommandAsync(FileMenuCommand.Stage), Is.False);
        f.Busy = false; f.DisplayOnly = true; Assert.That(await f.Controller.ExecuteCommandAsync(FileMenuCommand.Stage), Is.False);
        Assert.That(await f.Controller.ExecuteCommandAsync(FileMenuCommand.ShowInFileTree), Is.False);
        Assert.That(await f.Controller.ExecuteCommandAsync(FileMenuCommand.FindInCommitFiles), Is.False);
        f.DisplayOnly = false;
        profile.Set("OpenInVisualStudio", new(Key.F8)); profile.Set("GoToFirstParent", new(Key.F9));
        f.FocusFirst(); Press(f.Owner, PhysicalKey.F8); Assert.That(await f.Pending, Is.False);
        Press(f.Owner, PhysicalKey.F9); Assert.That(await f.Pending, Is.False);
        Assert.That(f.Calls, Is.Empty); Assert.That(GitCommandLog.Snapshot(), Is.Empty);
        Assert.That(await f.Controller.ExecuteCommandAsync(FileMenuCommand.Stage), Is.True);
        Assert.That(f.Calls.Single().Command, Is.EqualTo(FileMenuCommand.Stage));
        Assert.That(f.ClickScopes, Is.EqualTo(1), "The public API must keep the same host menu/error/focus wrapper.");
    }

    [AvaloniaTest]
    public async Task CustomDifftoolSubmenuKeepsItsSpecificToolAndCurrentCapturedEndpoints()
    {
        using Profile profile = new(); using GitFixture git = await GitFixture.CreateAsync();
        FileMenuLeaf leaf = new(new("file.txt", 'M'), new("HEAD"), new("HEAD"));
        using Harness f = new(git, [leaf]); f.CustomTools = ["one", "two"];
        ContextMenu menu = new(); f.Tree.ContextMenu = menu;
        menu.Opening += (_, _) => menu.ItemsSource = f.Controller.BuildItems(f.Capture());
        Pointer(Header((TreeViewItem)f.Tree.SelectedItem!), MouseButton.Right); Pump(f.Owner);
        MenuItem diff = menu.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiOpenWithDifftool");
        PushProcessTestDriver.Pointer(diff); Pump(f.Owner);
        MenuItem local = diff.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiDiffFirstToLocal");
        PushProcessTestDriver.Pointer(local); Pump(f.Owner);
        MenuItem tool = local.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiDiffFirstToLocalTool1");
        Assert.That(tool.Tag, Is.EqualTo(FileMenuCommand.DiffFirstToWorkingTree));
        PushProcessTestDriver.Pointer(tool); Pump(f.Owner);
        await Until(() => f.Calls.Count == 1, f.Owner);
        Assert.That(f.Calls.Single().Selection.CustomDiffTool, Is.EqualTo("two"));
        Assert.That(f.Calls.Single().Selection.Leaves.Single().First, Is.EqualTo(leaf.First));
        Assert.That(f.Calls.Single().Selection.Leaves.Single().Second, Is.EqualTo(leaf.Second));
        Assert.That(f.ClickScopes, Is.EqualTo(1)); menu.Close();
    }

    [AvaloniaTest]
    public Task RealBrowseFileTreeFindFileRemapOpensLocalPickerAndLeavesRepositoryUntouched()
        => VerifyRealBrowseFindFileAsync("tree");

    [AvaloniaTest]
    [TestCase("popup")]
    [TestCase("viewer")]
    public Task RealBrowseFilePickerPrecedesBrowseFromItsOwnPopupAndViewer(string source)
        => VerifyRealBrowseFindFileAsync(source);

    private static async Task VerifyRealBrowseFindFileAsync(string source)
    {
        using Profile profile = new(); using GitFixture git = await GitFixture.CreateAsync();
        profile.Set("FindFile", new(Key.F8));
        MainWindow main = new() { Width = 1200, Height = 800 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(git.Repository.Path); await Idle(main);
            Press(main, PhysicalKey.Digit4, RawInputModifiers.Control);
            await Until(() => main.GetVisualDescendants().OfType<FileStatusListView>().Any(view => view.Name == "CommitFileStatusList" && view.IsEffectivelyVisible && view.Entries.Count > 0), main);
            FileStatusListView view = Node<FileStatusListView>(main, "CommitFileStatusList");
            TreeViewItem row = FileStatusTestHelpers.Rows(view.Tree).Single(item => item.Tag is string path && path == "file.txt");
            await Until(() => row.GetVisualDescendants().OfType<Control>().SingleOrDefault(control => control.Name == "PART_Header"
                && control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() == row)
                is { IsEffectivelyVisible: true, Bounds.Width: > 0, Bounds.Height: > 0 }, main);
            PushProcessTestDriver.Pointer(Header(row)); await Idle(main);
            ContextMenu menu = view.Tree.ContextMenu!;
            Control returnFocus = view.Tree;
            if (source == "popup")
            {
                Pointer(Header(row), MouseButton.Right);
                await Until(() => menu.IsOpen && menu.Items.OfType<MenuItem>().Any(item => item.Name == "tsmiFindFile"), main);
                MenuItem find = menu.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiFindFile");
                await Until(() => TopLevel.GetTopLevel(find) is { IsVisible: true }, main);
                Assert.That(find.InputGesture, Is.EqualTo(new KeyGesture(Key.F8)));
            }
            else if (source == "viewer")
            {
                FileContentView content = Node<FileContentView>(main, "CommitFileContent");
                await Until(() => content.TextView.IsDocumentReady, main);
                PushProcessTestDriver.Pointer(content.TextView.Editor.TextArea);
                Assert.That(content.TextView.IsKeyboardFocusWithin, Is.True);
                returnFocus = content.TextView;
            }
            string bytes = await File.ReadAllTextAsync(git.File); GitCommandLog.Clear();
            TopLevel input = source == "popup" ? TopLevel.GetTopLevel(menu.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiFindFile"))! : main;
            Press(input, PhysicalKey.F8);
            await Until(() => main.OwnedWindows.OfType<FileListFindDialog>().Any(window => window.IsVisible), main);
            FileListFindDialog picker = main.OwnedWindows.OfType<FileListFindDialog>().Single(window => window.IsVisible);
            Assert.That(Node<TextBox>(picker, "txtSearchBox").IsKeyboardFocusWithin, Is.True);
            Assert.That(menu.IsOpen, Is.False, "An accepted file command closes only its owning popup before opening the picker.");
            Press(picker, PhysicalKey.Escape); await Idle(main);
            await Until(() => returnFocus.IsKeyboardFocusWithin && !main.OwnedWindows.Any(window => window.IsVisible), main);
            Assert.That(GitCommandLog.Snapshot(), Is.Empty);
            Assert.That(await File.ReadAllTextAsync(git.File), Is.EqualTo(bytes)); Assert.That(main.LastException, Is.Null);
        }
        finally { main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted, main); await main.WindowWorkCompletion; }
    }

    [AvaloniaTest]
    public async Task UnavailableRootParentBindingsKeepTheFileMenuAndItsSelection()
    {
        using Profile profile = new(); using GitFixture git = await GitFixture.CreateAsync();
        profile.Set("GoToFirstParent", new(Key.F9));
        profile.Set("GoToLastParent", new(Key.F9, KeyModifiers.Shift));
        MainWindow main = new() { Width = 1200, Height = 800 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(git.Repository.Path); await Idle(main);
            ListBox history = Node<ListBox>(main, "RevisionHistory");
            GitCommit root = history.Items.OfType<GitCommit>().Single();
            Assert.That(root.Parents, Is.Empty);
            PushProcessTestDriver.Pointer((Control)history.ContainerFromItem(root)!); await Idle(main);
            Press(main, PhysicalKey.Digit3, RawInputModifiers.Control);
            FileStatusListView view = Node<FileStatusListView>(main, "RevisionFileStatusList");
            await Until(() => view.IsEffectivelyVisible && view.Entries.Count > 0, main);
            // Groups become visible before their already-owned selected-file document read finishes.
            await main.RepositoryMutationCompletion; Pump(main);
            TreeViewItem row = FileStatusTestHelpers.Rows(view.Tree).Single(item => item.Tag is RevisionDiffFileSelection { File.Path: "file.txt" });
            Pointer(Header(row), MouseButton.Right);
            ContextMenu menu = view.Tree.ContextMenu!;
            await Until(() => menu.IsOpen && menu.Items.OfType<MenuItem>().Any(item => TopLevel.GetTopLevel(item) is { IsVisible: true }), main);
            object? selectedFile = view.Tree.SelectedItem; GitCommandLog.Clear();
            foreach (RawInputModifiers modifiers in new[] { RawInputModifiers.None, RawInputModifiers.Shift })
            {
                TopLevel input = TopLevel.GetTopLevel(menu.Items.OfType<MenuItem>().First())!;
                Press(input, PhysicalKey.F9, modifiers);
                Assert.That(menu.IsOpen, Is.True, "An unavailable parent command must not close its file popup.");
                Assert.That(history.SelectedItem, Is.SameAs(root));
                Assert.That(view.Tree.SelectedItem, Is.SameAs(selectedFile));
                Assert.That(main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
            }
            Assert.That(GitCommandLog.Snapshot(), Is.Empty); Assert.That(main.LastException, Is.Null);
            menu.Close();
        }
        finally { main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted, main); await main.WindowWorkCompletion; }
    }

    private static T Node<T>(Window root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static Control Header(TreeViewItem row) => row.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header" && control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() == row);
    private static void Pointer(Control control, MouseButton button)
    {
        TopLevel root = TopLevel.GetTopLevel(control) ?? throw new AssertionException("No rendered root."); Pump(root);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point); root.MouseDown(point, button); root.MouseUp(point, button); Pump(root);
    }
    private static void Pump(TopLevel root) { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { root.KeyPressQwerty(key, modifiers); if (root.IsVisible) { root.KeyReleaseQwerty(key, modifiers); Pump(root); } }
    private static async Task Until(Func<bool> condition, TopLevel root)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) { Pump(root); await Task.Delay(10); }
        Assert.That(condition(), Is.True);
    }
    private static async Task Idle(MainWindow main)
    { await Until(() => !main.IsBusy && main.WindowWorkCompletion.IsCompleted, main); await main.WindowWorkCompletion; Pump(main); }

    private sealed class Profile : IDisposable
    {
        private readonly string? _old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly string _path = Path.Combine(Path.GetTempPath(), "gitextensions-browsediff-hotkeys-" + Guid.NewGuid().ToString("N"));
        private readonly HotkeyBindings _bindings = new();
        internal Profile() { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _path); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en"); new HotkeyPreferencesStore().Save(_bindings); }
        internal void Set(string name, KeyGesture? value)
        { _bindings.Set("BrowseDiff", HotkeyCatalog.Find("BrowseDiff", name)!.Code, value); new HotkeyPreferencesStore().Save(_bindings); Dispatcher.UIThread.RunJobs(); }
        public void Dispose() { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _old); CultureInfo.CurrentUICulture = _culture; Directory.Delete(_path, recursive: true); }
    }

    private sealed class Harness : IDisposable
    {
        private readonly GitFixture _git;
        internal Window Owner { get; }
        internal TreeView Tree { get; } = new() { SelectionMode = SelectionMode.Multiple };
        internal FileStatusListView View { get; }
        internal FileStatusListContextMenuController Controller { get; }
        internal readonly List<(FileMenuCommand Command, FileMenuSelection Selection)> Calls = [];
        internal Func<FileMenuCommand, FileMenuSelection, Task> Execute = (_, _) => Task.CompletedTask;
        internal Task<bool> Pending = Task.FromResult(false);
        internal bool Current = true, Busy, DisplayOnly, IsFileTree;
        internal int ClickScopes;
        internal string[] CustomTools = [];
        internal Harness(GitFixture git, IReadOnlyList<FileMenuLeaf> leaves, FileMenuCapabilities? capabilities = null)
        {
            _git = git; View = new(Tree, preferencesStore: new FileStatusListPreferencesStore(git.Output("hotkey-view"))) { SortMode = FileStatusListSortMode.FilePathFlat };
            TranslationCatalog translations = TranslationCatalog.LoadDefault();
            FileMenuHost host = new()
            {
                CaptureSelection = Capture, IsCurrent = selection => Current && ReferenceEquals(selection.Repository, git.Repository), IsBusy = () => Busy,
                Capabilities = capabilities ?? new(), GetCustomDiffTools = () => CustomTools,
                ExecuteAsync = (command, selection) => { Calls.Add((command, selection)); return Execute(command, selection); },
                CreateAction = (category, name, label, icon, action, gesture) =>
                {
                    MenuItem item = OriginalMenuItems.Create(translations, category, name, label, icon, gesture);
                    item.Click += async (_, args) => { if (args.Source != item || !item.IsEnabled) { return; } args.Handled = true; ClickScopes++; await action(); };
                    return item;
                }
            };
            Controller = new(View, translations, host);
            Owner = new() { Width = 900, Height = 700, Content = View };
            Owner.AddHandler(InputElement.KeyDownEvent, (_, args) => Pending = Controller.HandleShortcutAsync(args), RoutingStrategies.Tunnel);
            Owner.Show();
            View.SetGroups([new FileStatusListGroup("files", "files", new TextBlock { Text = "Files" }, leaves.Select(leaf => new FileStatusListEntry(leaf, leaf.File.Path, leaf.File.OriginalPath, leaf.File.Status)).ToArray())]);
            View.SelectTags([leaves[0]]); Pump(Owner);
        }
        internal FileMenuSelection Capture()
        {
            FileMenuLeaf[] leaves = View.SelectedTags.OfType<FileMenuLeaf>().ToArray();
            return new(_git.Repository, leaves, leaves.Select(leaf => leaf.File.Path).ToArray(), IsFileTree: IsFileTree, IsDisplayOnly: DisplayOnly, HostIdentity: this);
        }
        internal void FocusFirst() { PushProcessTestDriver.Pointer(Header((TreeViewItem)Tree.SelectedItem!)); Pump(Owner); }
        public void Dispose() { foreach (Window child in Owner.OwnedWindows.ToArray()) { child.Close(); } Tree.ContextMenu?.Close(); Owner.Close(); View.Dispose(); Pump(Owner); }
    }
}

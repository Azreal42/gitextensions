using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FilePathPresentationIntegrationTests
{
    [AvaloniaTest]
    [TestCase(FileStatusListSortMode.FilePath)]
    [TestCase(FileStatusListSortMode.FilePathFlat)]
    [TestCase(FileStatusListSortMode.FileExtension)]
    [TestCase(FileStatusListSortMode.FileExtensionFlat)]
    [TestCase(FileStatusListSortMode.FileStatus)]
    [TestCase(FileStatusListSortMode.FileStatusFlat)]
    public async Task AllFourPresentationsKeepRowsHeadersRawRenameSelectionFocusAndMenusInEachOriginalSort(FileStatusListSortMode sort)
    {
        using ViewFixture f = new(); f.View.SortMode = sort; f.View.DenseTree = true; f.View.ShowGroupNodesInFlatList = true;
        object tag = new(), other = new(), range = new();
        FileStatusListEntry rename = new(tag, "long/current/directory/current\\literal.txt", "archive/previous/directory/old\\literal.txt", 'R', 100);
        f.View.GroupByRevision = true;
        f.View.SetGroups([new("files", "files", new TextBlock { Text = "Diff with A full group caption" }, [rename, new(other, "other/keep.md")]),
            new(range, "range", new TextBlock { Text = "Range diff full caption" }, [], AlwaysVisible: true)]);
        f.Window.Show(); f.View.SelectTags([tag]); await Frame(f.Window);
        TreeViewItem row = f.View.FindItemByTag(tag)!; await ClickRow(f.Window, row);
        object source = f.View.Tree.ItemsSource!, header = row.Header!, focus = f.Window.FocusManager!.GetFocusedElement()!;
        Dictionary<TreeViewItem, object?> nonFiles = FileStatusTestHelpers.Rows(f.View.Tree).Where(item => !ReferenceEquals(item.Tag, tag) && !ReferenceEquals(item.Tag, other)).ToDictionary(item => item, item => item.Header);
        string tooltip = (string)ToolTip.GetTip((Control)header)!;
        int selected = 0, display = 0;
        f.View.Tree.SelectionChanged += (_, _) => selected++;
        f.View.DisplayChanged += (_, _) => display++;
        byte[] localOptions = File.ReadAllBytes(f.Store.FilePath);
        foreach (TruncatePathMethod method in Enum.GetValues<TruncatePathMethod>())
        {
            f.Publish(method); await Frame(f.Window);
            Assert.That(f.View.Tree.ItemsSource, Is.SameAs(source)); Assert.That(f.View.FindItemByTag(tag), Is.SameAs(row)); Assert.That(row.Header, Is.SameAs(header));
            Assert.That(f.Window.FocusManager.GetFocusedElement(), Is.SameAs(focus)); Assert.That(f.View.Tree.IsKeyboardFocusWithin, Is.True);
            Assert.That(f.View.SelectedTags, Is.EqualTo(new[] { tag })); Assert.That(f.View.Entries.Single(entry => ReferenceEquals(entry.Tag, tag)), Is.SameAs(rename));
            Assert.That(rename.Path, Is.EqualTo("long/current/directory/current\\literal.txt")); Assert.That(rename.OriginalPath, Is.EqualTo("archive/previous/directory/old\\literal.txt"));
            Assert.That(ToolTip.GetTip((Control)header), Is.EqualTo(tooltip)); Assert.That(Text(row).Contains('→'), Is.False);
            Assert.That(((Grid)header).Children.OfType<TextBlock>().Single().TextTrimming, Is.EqualTo(TextTrimming.None));
            foreach (var pair in nonFiles) { Assert.That(pair.Key.Header, Is.SameAs(pair.Value)); }
            if (method == TruncatePathMethod.FileNameOnly) { Assert.That(Text(row), Is.EqualTo("current\\literal.txt (old\\literal.txt)")); }
            Assert.That(f.View.SortMode, Is.EqualTo(sort)); Assert.That(f.View.DenseTree && f.View.ShowGroupNodesInFlatList, Is.True);
        }
        Assert.That(selected, Is.Zero); Assert.That(display, Is.Zero); Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(localOptions));
        Assert.That(f.View.SettingsMenu, Is.Not.Null); Assert.That(f.View.FindItemByTag(range)!.Header, Is.SameAs(nonFiles[f.View.FindItemByTag(range)!]));
    }

    [AvaloniaTest]
    [TestCase(12)]
    [TestCase(24)]
    public async Task TrimmingUsesTheActualScrollableViewportAndEffectiveFontWhileNoneKeepsTheFullText(double fontSize)
    {
        using ViewFixture f = new(); f.View.SortMode = FileStatusListSortMode.FilePathFlat;
        object tag = new(); string path = string.Join('/', Enumerable.Repeat("a-very-long-directory", 9)) + "/current-filename.txt";
        f.View.SetGroups([new("files", "files", new TextBlock(), [new(tag, path, "old/previous-filename.txt", 'R')])]);
        f.Window.Width = 360; f.Window.Show(); f.View.SelectTags([tag]); await Frame(f.Window);
        TreeViewItem row = f.View.FindItemByTag(tag)!; TextBlock text = ((Grid)row.Header!).Children.OfType<TextBlock>().Single();
        text.FontSize = fontSize; await Frame(f.Window);
        string none = Text(row); Assert.That(none, Does.StartWith(path).And.Contain("old/previous-filename.txt"));
        f.Publish(TruncatePathMethod.TrimStart); await Frame(f.Window);
        string narrow = Text(row); Assert.That(narrow, Does.Contain("...").And.Not.EqualTo(none)); AssertFitsViewport(text);
        object header = row.Header!; f.Window.Width = 850; await Frame(f.Window);
        Assert.That(row.Header, Is.SameAs(header)); Assert.That(Text(row), Is.Not.EqualTo(narrow)); AssertFitsViewport(text);
        f.Publish(TruncatePathMethod.Compact); await Frame(f.Window);
        Assert.That(Text(row), Does.Contain("...").And.Not.EqualTo(none)); AssertFitsViewport(text);
        f.Publish(TruncatePathMethod.None); await Frame(f.Window); Assert.That(Text(row), Is.EqualTo(none));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task FilenameFilteringMatchesCurrentOrOldPosixBasenameAndPreservesAnExplicitLocalOverride(bool localOverride)
    {
        using ViewFixture f = new(); f.View.FilterFileNameOnly = localOverride;
        object renamed = new(), literal = new(), range = new();
        FileStatusListEntry entry = new(renamed, "new/deep/current.txt", "archive/deep/old.txt", 'R');
        f.View.GroupByRevision = true;
        f.View.SetGroups([new("files", "files", new TextBlock(), [entry, new(literal, "nested/one\\two.txt")]),
            new(range, "range", new TextBlock { Text = "Range remains visible" }, [], AlwaysVisible: true)]);
        f.Window.Show(); await Frame(f.Window); await Filter(f, "^old\\.txt$");
        Assert.That(f.View.FilteredEntries.Count, Is.EqualTo(localOverride ? 1 : 0));
        f.Publish(TruncatePathMethod.FileNameOnly); await Frame(f.Window);
        Assert.That(f.View.FilteredEntries.Single().Tag, Is.SameAs(renamed)); Assert.That(f.View.FindItemByTag(range), Is.Not.Null);
        Assert.That(f.View.FilteredEntries.Single().OriginalPath, Is.EqualTo(entry.OriginalPath));
        f.Publish(TruncatePathMethod.None); await Frame(f.Window);
        Assert.That(f.View.FilteredEntries.Count, Is.EqualTo(localOverride ? 1 : 0)); Assert.That(f.Store.Load().FilterFileNameOnly, Is.EqualTo(localOverride));
        f.Publish(TruncatePathMethod.FileNameOnly); await Filter(f, "^" + System.Text.RegularExpressions.Regex.Escape("one\\two.txt") + "$");
        Assert.That(f.View.FilteredEntries.Single().Tag, Is.SameAs(literal), "A backslash belongs to the POSIX basename.");
    }

    [AvaloniaTest]
    public async Task AnActiveRegexWhoseMembershipDoesNotChangeStillKeepsItsRowFocusAndAllSelectionEventsSilent()
    {
        using ViewFixture f = new(); f.View.SortMode = FileStatusListSortMode.FilePathFlat;
        object tag = new(); f.View.SetGroups([new("files", "files", new TextBlock(), [new(tag, "directory/one.txt"), new(new object(), "directory/two.txt")])]);
        f.Window.Show(); await Frame(f.Window); await Filter(f, ".*"); f.View.SelectTags([tag]); await ClickRow(f.Window, f.View.FindItemByTag(tag)!);
        TreeViewItem row = f.View.FindItemByTag(tag)!; object source = f.View.Tree.ItemsSource!, header = row.Header!, focus = f.Window.FocusManager!.GetFocusedElement()!;
        int selected = 0, displayed = 0; f.View.Tree.SelectionChanged += (_, _) => selected++; f.View.DisplayChanged += (_, _) => displayed++;
        f.Publish(TruncatePathMethod.FileNameOnly); await Frame(f.Window);
        Assert.That(Text(row), Is.EqualTo("one.txt")); Assert.That(f.View.Tree.ItemsSource, Is.SameAs(source)); Assert.That(row.Header, Is.SameAs(header));
        Assert.That(f.Window.FocusManager.GetFocusedElement(), Is.SameAs(focus)); Assert.That(selected, Is.Zero); Assert.That(displayed, Is.Zero);
    }

    [AvaloniaTest]
    public async Task QueuedSavedPoliciesCannotLoseTheFinalReturnToTheCurrentlyDisplayedMethod()
    {
        using ViewFixture f = new(); f.View.SortMode = FileStatusListSortMode.FilePathFlat;
        object tag = new(); f.View.SetGroups([new("files", "files", new TextBlock(), [new(tag, "directory/current.txt", "old/previous.txt", 'R')])]);
        f.Window.Show(); await Frame(f.Window); TreeViewItem row = f.View.FindItemByTag(tag)!; object header = row.Header!;
        // Both actual saved publications are queued while this UI stack is held.
        // A producer returning to None must not be discarded against stale UI state.
        Task producer = Task.Run(() => { f.Publish(TruncatePathMethod.FileNameOnly); f.Publish(TruncatePathMethod.None); });
        producer.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        await Frame(f.Window);
        Assert.That(f.Desktop.Load().TruncatePathMethod, Is.EqualTo(TruncatePathMethod.None));
        Assert.That(Text(row), Is.EqualTo("directory/current.txt (old/previous.txt)")); Assert.That(row.Header, Is.SameAs(header));
    }

    [AvaloniaTest]
    public async Task ExistingAndFileTreePeersUseTheCapturedDesktopProfileWithoutChangingAnotherProfile()
    {
        using ViewFixture first = new(); using ViewFixture isolated = new();
        first.Publish(TruncatePathMethod.FileNameOnly);
        using FileStatusListView peer = new(new TreeView(), preferencesStore: new(first.Directory), fileTreeMode: true);
        object tag = new(); FileStatusListEntry rename = new(tag, "current.txt", "old/full/previous.txt", 'R');
        FileStatusListGroup group = new("files", "files", new TextBlock(), [rename]);
        first.View.SetGroups([group]); peer.SetGroups([group]); isolated.View.SetGroups([group]);
        Assert.That(Text(peer.FindItemByTag(tag)!), Is.EqualTo("current.txt (previous.txt)"), "File-tree mode also loads its captured global Desktop profile.");
        Assert.That(Text(isolated.View.FindItemByTag(tag)!), Does.Contain("old/full/previous.txt"));
        first.Publish(TruncatePathMethod.None);
        Assert.That(Text(peer.FindItemByTag(tag)!), Does.Contain("old/full/previous.txt"));
        Assert.That(isolated.Desktop.Load().TruncatePathMethod, Is.EqualTo(TruncatePathMethod.None));
        first.Publish(TruncatePathMethod.FileNameOnly);
        Assert.That(Text(first.View.FindItemByTag(tag)!), Is.EqualTo("current.txt (previous.txt)"));
        Assert.That(Text(isolated.View.FindItemByTag(tag)!), Does.Contain("old/full/previous.txt"));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public void DisposingAViewReleasesTheProfilePresentationSubscription(bool fileTreeMode)
    {
        using ViewFixture f = new(); WeakReference released = CreateDisposedView(f.Directory, fileTreeMode);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Assert.That(released.IsAlive, Is.False, "The existing profile state must not retain a disposed view through the new typed event.");
        f.Publish(TruncatePathMethod.FileNameOnly);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ActualMainApplyPublishesTheSavedMethodWithoutReloadingGitEvenWhenTheFollowingCloneProfileFails(bool failClone)
    {
        await using AvatarSettingsMainFixture f = await AvatarSettingsMainFixture.Create();
        const string path = "long/nested/path/file-name.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(f.Git.PathInRepository(path))!); await File.WriteAllTextAsync(f.Git.PathInRepository(path), "new file\n");
        await f.Git.GitAsync("add", "--", path); await f.Git.GitAsync("commit", "-m", "Nested file");
        await f.Main.OpenRepositoryAsync(f.Git.Repository.Path); await f.Idle();
        ListBox history = AvatarUiTestDriver.Field<ListBox>(f.Main, "RevisionHistory"); history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Nested file"); await f.Idle();
        await AvatarUiTestDriver.Click(f.Main, AvatarUiTestDriver.Field<TabItem>(f.Main, "DiffTab")); await f.Idle();
        FileStatusListView view = AvatarUiTestDriver.Field<FileStatusListView>(f.Main, "RevisionFileStatusList"); view.SortMode = FileStatusListSortMode.FilePathFlat; await f.Idle();
        object tag = view.Entries.Single(entry => entry.Path == path).Tag; view.SelectTags([tag]); await f.Idle(); await ClickRow(f.Main, view.FindItemByTag(tag)!);
        TreeViewItem row = view.FindItemByTag(tag)!; object items = view.Tree.ItemsSource!, header = row.Header!;
        int selections = 0, displays = 0; view.Tree.SelectionChanged += (_, _) => selections++; view.DisplayChanged += (_, _) => displays++;
        DesktopSettingsDialog settings = await f.Settings();
        await AvatarUiTestDriver.Choose(settings, AvatarUiTestDriver.Field<ComboBox>(settings, "truncatePathMethod"), TruncatePathMethod.FileNameOnly);
        ClonePreferencesStore clone = new(); byte[]? cloneBefore = File.Exists(clone.FilePath) ? File.ReadAllBytes(clone.FilePath) : null;
        try
        {
            if (failClone)
            {
                SettingsNavigationTestDriver.Select(settings, "General"); await Frame(settings);
                ComboBox destination = AvatarUiTestDriver.Field<ComboBox>(settings, "cbDefaultCloneDestination");
                await AvatarUiTestDriver.Edit(settings, destination.GetVisualDescendants().OfType<TextBox>().Single(), "/tmp/truncate-new-clone");
                Directory.CreateDirectory(Path.GetDirectoryName(clone.FilePath)!); File.WriteAllText(clone.FilePath, "{broken-clone-after-desktop");
            }
            GitCommandLog.Clear(); await f.Apply(settings, expectFailure: failClone);
            Assert.That(f.Desktop.Load().TruncatePathMethod, Is.EqualTo(TruncatePathMethod.FileNameOnly)); Assert.That(Text(row), Is.EqualTo("file-name.txt"));
            Assert.That(view.Tree.ItemsSource, Is.SameAs(items)); Assert.That(view.FindItemByTag(tag), Is.SameAs(row)); Assert.That(row.Header, Is.SameAs(header));
            Assert.That(view.SelectedTags, Is.EqualTo(new[] { tag })); Assert.That(selections, Is.Zero); Assert.That(displays, Is.Zero);
            Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Command is "git log" or "git diff" or "git ls-tree" or "git show" or "git status"), Is.False);
        }
        finally
        {
            if (cloneBefore is null) { File.Delete(clone.FilePath); } else { File.WriteAllBytes(clone.FilePath, cloneBefore); }
        }
        await f.Cancel(settings); Assert.That(view.Tree.IsKeyboardFocusWithin, Is.True); Assert.That(view.SelectedTags, Is.EqualTo(new[] { tag }));
        await AvatarUiTestDriver.Click(f.Main, AvatarUiTestDriver.Field<TabItem>(f.Main, "FileTreeTab")); await f.Idle();
        FileStatusListView fileTree = AvatarUiTestDriver.Field<FileStatusListView>(f.Main, "CommitFileStatusList");
        await Filter(fileTree, f.Main, "^file-name\\.txt$"); await f.Idle();
        Assert.That(fileTree.FilteredEntries.Single().Path, Is.EqualTo(path), "The already-open real commit-file tree also uses the saved filename filter.");
    }

    [AvaloniaTest]
    public async Task ARealComparisonKeepsItsSelectedPatchAndRawFilenameWhileAnOpenHeaderChangesPresentation()
    {
        using GitFixture f = await GitFixture.CreateAsync(); const string path = "nested/long/comparison/file-name.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(f.PathInRepository(path))!); await File.WriteAllTextAsync(f.PathInRepository(path), "base content\n");
        await f.GitAsync("add", "--", path); await f.GitAsync("commit", "-m", "Comparison base");
        await File.AppendAllTextAsync(f.PathInRepository(path), "comparison content\n"); await f.GitAsync("add", "--", path); await f.GitAsync("commit", "-m", "Comparison change");
        PreferencesStore desktop = new(); desktop.Save(new() { Language = "English", TruncatePathMethod = TruncatePathMethod.FileNameOnly });
        new FileStatusListPreferencesStore().Save(new(FileStatusListSortMode.FilePathFlat));
        CompareRevisionsDialog dialog = new(f.Repository); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Until(() => !dialog.IsBusy && dialog.SelectionCompletion.IsCompleted); await Frame(dialog);
            FileStatusListView view = AvatarUiTestDriver.Field<FileStatusListView>(dialog, "ComparisonFileStatusList");
            object tag = view.Entries.Single(entry => entry.Path == path).Tag; view.SelectTags([tag]);
            await Until(() => !dialog.IsBusy && dialog.SelectionCompletion.IsCompleted); await Frame(dialog);
            TreeViewItem row = view.FindItemByTag(tag)!; object header = row.Header!, source = view.Tree.ItemsSource!;
            Assert.That(Text(row), Is.EqualTo("file-name.txt")); Assert.That(dialog.SelectedFile!.Path, Is.EqualTo(path));
            FileHistoryContentView diff = AvatarUiTestDriver.Field<FileHistoryContentView>(dialog, "ComparisonDiff"); string patch = diff.TextView.Text;
            Assert.That(patch, Does.Contain("+comparison content")); GitCommandLog.Clear();
            DesktopPreferences accepted = desktop.Load() with { TruncatePathMethod = TruncatePathMethod.None }; desktop.Save(accepted);
            FileStatusListPreferencesStore.PublishPathPresentation(desktop.FilePath, accepted.TruncatePathMethod); await Frame(dialog);
            Assert.That(Text(row), Is.EqualTo(path)); Assert.That(view.Tree.ItemsSource, Is.SameAs(source)); Assert.That(row.Header, Is.SameAs(header));
            Assert.That(dialog.SelectedFile!.Path, Is.EqualTo(path)); Assert.That(diff.TextView.Text, Is.EqualTo(patch)); Assert.That(GitCommandLog.Snapshot(), Is.Empty);
        }
        finally { await AvatarUiTestDriver.Close(dialog); }
    }

    [AvaloniaTest]
    public async Task BothRealCommitListsUseTheSharedRenameHeaderAndStagingStillUsesTheLiteralCurrentPath()
    {
        using GitFixture f = await GitFixture.CreateAsync(); const string old = "old/deep/old\\literal.txt", current = "new/deep/current\\literal.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(f.PathInRepository(old))!); await File.WriteAllTextAsync(f.PathInRepository(old), "base file\n");
        await f.GitAsync("add", "--", old); await f.GitAsync("commit", "-m", "Rename source");
        Directory.CreateDirectory(Path.GetDirectoryName(f.PathInRepository(current))!); await f.GitAsync("mv", "--", old, current);
        await File.AppendAllTextAsync(f.PathInRepository(current), "unstaged literal content\n");
        PreferencesStore desktop = new(); desktop.Save(new() { Language = "English", TruncatePathMethod = TruncatePathMethod.FileNameOnly });
        new FileStatusListPreferencesStore().Save(new(FileStatusListSortMode.FilePathFlat));
        CommitDialog dialog = new(f.Repository); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Until(() => !dialog.IsBusy); await Frame(dialog);
            FileStatusListView unstaged = AvatarUiTestDriver.Field<FileStatusListView>(dialog, "UnstagedFileStatusList"), staged = AvatarUiTestDriver.Field<FileStatusListView>(dialog, "StagedFileStatusList");
            FileStatusListEntry work = unstaged.Entries.Single(entry => entry.Path == current), index = staged.Entries.Single(entry => entry.Path == current);
            TreeViewItem a = unstaged.FindItemByTag(work.Tag)!, b = staged.FindItemByTag(index.Tag)!;
            Assert.That(Text(a), Is.EqualTo("current\\literal.txt (old\\literal.txt)")); Assert.That(Text(b), Is.EqualTo("current\\literal.txt (old\\literal.txt)"));
            Assert.That(ToolTip.GetTip((Control)a.Header!), Is.EqualTo(current)); Assert.That(work.OriginalPath, Is.EqualTo(old)); Assert.That(index.OriginalPath, Is.EqualTo(old));
            object ah = a.Header!, bh = b.Header!; byte[] before = File.ReadAllBytes(Path.Combine(f.Repository.Path, ".git/index"));
            GitCommandLog.Clear(); DesktopPreferences saved = desktop.Load() with { TruncatePathMethod = TruncatePathMethod.None }; desktop.Save(saved);
            FileStatusListPreferencesStore.PublishPathPresentation(desktop.FilePath, saved.TruncatePathMethod); await Frame(dialog);
            Assert.That(a.Header, Is.SameAs(ah)); Assert.That(b.Header, Is.SameAs(bh)); Assert.That(Text(a), Does.Contain(current).And.Contain(old));
            Assert.That(GitCommandLog.Snapshot(), Is.Empty); Assert.That(File.ReadAllBytes(Path.Combine(f.Repository.Path, ".git/index")), Is.EqualTo(before));
            unstaged.SelectTags([work.Tag]); await Until(() => !dialog.IsBusy); await Frame(dialog);
            await AvatarUiTestDriver.Click(dialog, AvatarUiTestDriver.Field<Button>(dialog, "Stage"));
            await Until(() => !dialog.IsBusy && !unstaged.Entries.Any(entry => entry.Path == current));
            Assert.That((await f.GitAsync("show", ":" + current)).StandardOutput, Is.EqualTo("base file\nunstaged literal content\n"));
            Assert.That(File.Exists(f.PathInRepository(old)), Is.False); Assert.That(dialog.LastError, Is.Null);
        }
        finally { await AvatarUiTestDriver.Close(dialog); }
    }

    private static string Text(TreeViewItem row) => string.Concat(((Grid)row.Header!).Children.OfType<TextBlock>().Single().Inlines!.OfType<Run>().Select(run => run.Text));
    private static async Task Frame(Window window) { using (await AvatarUiTestDriver.Frame(window)) { } }
    private static async Task ClickRow(Window window, TreeViewItem row)
    {
        await Frame(window); Control header = (Control)row.Header!;
        Point point = header.TranslatePoint(new Point(20, header.Bounds.Height / 2), window)!.Value;
        Assert.That((window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True, "The first visible header hit must belong to the intended real row.");
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }
    private static Task Filter(ViewFixture f, string expression) => Filter(f.View, f.Window, expression);
    private static async Task Filter(FileStatusListView view, Window window, string expression)
    {
        TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Updated(object? _, EventArgs __) => changed.TrySetResult();
        view.DisplayChanged += Updated;
        try { view.FilterText = expression; await changed.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Frame(window); }
        finally { view.DisplayChanged -= Updated; }
    }
    private static void AssertFitsViewport(TextBlock text)
    {
        ScrollContentPresenter viewport = text.GetVisualAncestors().OfType<ScrollContentPresenter>().First();
        double start = text.TranslatePoint(default, viewport)!.Value.X;
        string caption = string.Concat(text.Inlines!.OfType<Run>().Select(run => run.Text));
        double width = new FormattedText(caption, CultureInfo.CurrentCulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black).WidthIncludingTrailingWhitespace;
        Assert.That(width, Is.LessThanOrEqualTo(viewport.Bounds.Width - start + .5));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedView(string directory, bool fileTreeMode)
    {
        FileStatusListView view = new(new TreeView(), preferencesStore: new(directory), fileTreeMode: fileTreeMode);
        WeakReference weak = new(view); view.Dispose(); return weak;
    }
    private static async Task Until(Func<bool> complete)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!complete() && timer.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(10); }
        Assert.That(complete(), Is.True);
    }
    private sealed class ViewFixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-truncate-consumer-" + Guid.NewGuid().ToString("N"));
        internal PreferencesStore Desktop { get; } internal FileStatusListPreferencesStore Store { get; }
        internal FileStatusListView View { get; } internal Window Window { get; }
        internal ViewFixture()
        {
            Desktop = new(Directory); Desktop.Save(new() { Language = "English" }); Store = new(Directory);
            View = new(new TreeView { Name = "TruncateFiles", SelectionMode = SelectionMode.Multiple }, preferencesStore: Store);
            Window = new() { Width = 520, Height = 340, Content = View };
        }
        internal void Publish(TruncatePathMethod method)
        {
            DesktopPreferences accepted = Desktop.Load() with { TruncatePathMethod = method }; Desktop.Save(accepted);
            FileStatusListPreferencesStore.PublishPathPresentation(Desktop.FilePath, accepted.TruncatePathMethod);
        }
        public void Dispose() { Window.Close(); View.Dispose(); if (System.IO.Directory.Exists(Directory)) { System.IO.Directory.Delete(Directory, true); } }
    }
}

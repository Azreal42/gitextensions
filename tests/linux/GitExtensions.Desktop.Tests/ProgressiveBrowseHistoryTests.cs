using System.Collections.Specialized;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.ProgressiveBrowseTestScope;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable, Platform("Linux")]
[SupportedOSPlatform("linux")]
public sealed class ProgressiveBrowseHistoryTests
{
    [AvaloniaTest]
    public async Task A_FirstHundredAreUsableAndLaterHeadMixedSelectionAndEofKeepTheSameNativeSurface()
    {
        await using ProgressiveBrowseTestScope f = await ProgressiveBrowseTestScope.CreateAsync();
        using var presentation = f.EnterPresentation();
        NativeHistoryGate.Slot gate = f.Gate.Arm(false, 100, 200); string material = await f.Materials();
        await f.OpenAsync(); await Ready(f.Main, gate); await DiffTab(f.Main);
        ListBox rows = f.Rows; object source = rows.ItemsSource!; object[] prefix = rows.Items.Cast<object>().ToArray();
        Assert.That(Hashes(f.Main), Is.EqualTo(f.Native.Take(100))); Assert.That(rows.Items.OfType<ArtificialHistoryRevision>(), Is.Empty);
        await Click(f.Main, f.Native[8]); Press(f.Main, PhysicalKey.ArrowDown); await Panel(f.Main);
        Assert.That(Key(rows.SelectedItem!), Is.EqualTo(f.Native[9])); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
        await FileRow(f.Main, "file.txt");
        Assert.That(f.Diff.CanonicalText, Does.Contain("+NEWER 120").And.Contain("-NEWER 119"));
        ToggleArtificial(f.Main); await Panel(f.Main); ToggleArtificial(f.Main); await Panel(f.Main);
        Assert.That(rows.Items.OfType<ArtificialHistoryRevision>(), Is.Empty, "A pending tail must not be completed just to choose an artificial-row fallback.");
        Assert.That(f.Gate.Commands.Count(NativeHistoryGate.IsGraph), Is.EqualTo(1)); Capture(f.Main, "A-en-light-first100");
        gate.Release(100); await Ready(f.Main, gate, 200);
        Assert.That(rows.ItemsSource, Is.SameAs(source)); Assert.That(prefix.All(item => rows.Items.Cast<object>().Any(current => ReferenceEquals(current, item))), Is.True);
        Assert.That(rows.Items.OfType<ArtificialHistoryRevision>().Select(a => a.Kind), Is.EqualTo(new[] { GitCompareRevisionKind.WorkingTree, GitCompareRevisionKind.Index }));
        await Click(f.Main, f.Native[131]); await Click(f.Main, ":index", RawInputModifiers.Control); await Click(f.Main, ":working-tree", RawInputModifiers.Control);
        Assert.That(Selected(f.Main), Is.EquivalentTo(new[] { f.Native[131], ":index", ":working-tree" }));
        GitRevisionDiffGroup[] groups = Groups(f.Main); Assert.That(groups, Has.Length.EqualTo(2));
        Assert.That(groups[0].BaseRevision, Is.EqualTo(f.Native[131])); Assert.That(groups[0].Revision, Is.EqualTo("WorkingTree"));
        Assert.That(groups[1].BaseRevision, Is.EqualTo("Index")); Assert.That(groups[1].Revision, Is.EqualTo("WorkingTree"));
        Assert.That(f.Diff.CanonicalText, Does.Contain("-MAIN 158").And.Contain("+WORKTREE FILE"));
        object[] selected = rows.SelectedItems!.Cast<object>().ToArray(); object? caret = Caret(f.Main)?.Content; int anchor = rows.Selection.AnchorIndex;
        Vector offset = Scroller(f.Main).Offset; object[] existing = rows.Items.Cast<object>().ToArray(); int resets = 0;
        ((INotifyCollectionChanged)source).CollectionChanged += (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) { resets++; } };
        gate.Release(200); await Complete(f.Main);
        Assert.Multiple(() =>
        {
            Assert.That(Hashes(f.Main), Is.EqualTo(f.Native)); Assert.That(rows.ItemsSource, Is.SameAs(source)); Assert.That(resets, Is.Zero);
            Assert.That(existing.All(item => rows.Items.Cast<object>().Any(current => ReferenceEquals(current, item))), Is.True);
            Assert.That(rows.SelectedItems!.Cast<object>(), Is.EquivalentTo(selected)); Assert.That(Caret(f.Main)?.Content, Is.SameAs(caret));
            Assert.That(rows.Selection.AnchorIndex, Is.EqualTo(anchor)); Assert.That(Scroller(f.Main).Offset, Is.EqualTo(offset)); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
            Assert.That(Groups(f.Main).Select(g => (g.BaseRevision, g.Revision)), Is.EqualTo(groups.Select(g => (g.BaseRevision, g.Revision))));
        });
        Press(f.Main, PhysicalKey.ArrowDown); await Panel(f.Main); Assert.That(Key(rows.SelectedItem!), Is.EqualTo(":index")); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
        gate.AssertRetired(); Assert.That(f.Gate.Commands.Count(NativeHistoryGate.IsGraph), Is.EqualTo(1)); Assert.That(await f.Materials(), Is.EqualTo(material));
        Capture(f.Main, "A-en-light-eof");
    }

    [AvaloniaTest]
    public async Task B_CompactOwnedLogReadsItsPartialDiffAndDirectCloseDrainsBeforeReadOnlyReturn()
    {
        await using ProgressiveBrowseTestScope f = await ProgressiveBrowseTestScope.CreateAsync(frenchDark: true);
        using var presentation = f.EnterPresentation();
        await f.OpenAsync(); await Complete(f.Main); await DiffTab(f.Main); await Click(f.Main, f.Native[3]);
        object browseSource = f.Rows.ItemsSource!; object[] selected = f.Rows.SelectedItems!.Cast<object>().ToArray(); string material = await f.Materials();
        NativeHistoryGate.Slot gate = f.Gate.Arm(); Task modal = f.Main.ShowRevisionLogAsync();
        await Until(f.Main, () => f.Main.OwnedWindows.OfType<MainWindow>().Any(window => window.WindowKind == MainWindowKind.RevisionLog));
        MainWindow log = f.Main.OwnedWindows.OfType<MainWindow>().Single(window => window.WindowKind == MainWindowKind.RevisionLog); f.Track(log);
        await Ready(log, gate); f.AssertPresentation(log); Assert.That(log.GetVisualDescendants().OfType<Menu>(), Is.Empty);
        Assert.That(log.Owner, Is.SameAs(f.Main)); Assert.That(Field<ListBox>(log, "RevisionHistory").ItemsSource, Is.Not.Null);
        await Click(log, f.Native[5]); Press(log, PhysicalKey.ArrowDown); await Panel(log);
        Assert.That(Key(Field<ListBox>(log, "RevisionHistory").SelectedItem!), Is.EqualTo(f.Native[6]));
        Assert.That(Field<ListBox>(log, "RevisionHistory").IsKeyboardFocusWithin, Is.True); await FileRow(log, "file.txt");
        Assert.That(Field<DiffView>(log, "CommitDiff").CanonicalText, Does.Contain("-NEWER 122").And.Contain("+NEWER 123"));
        await Click(log, f.Native[7], RawInputModifiers.Control);
        Assert.That(Groups(log).Single().BaseRevision, Is.EqualTo(f.Native[6])); Assert.That(Groups(log).Single().Revision, Is.EqualTo(f.Native[7]));
        Assert.That(Field<DiffView>(log, "CommitDiff").CanonicalText, Does.Contain("-NEWER 123").And.Contain("+NEWER 122"));
        Capture(log, "B-fr-dark-partial-log"); int graphCount = f.Gate.Commands.Count(NativeHistoryGate.IsGraph);
        Task history = HistoryWork(log); log.Close();
        await Until(f.Main, () => !log.IsVisible && history.IsCompleted && log.WindowWorkCompletion.IsCompleted && modal.IsCompleted);
        await modal; await history; await log.WindowWorkCompletion;
        gate.AssertRetired(); Assert.That(gate.Paused(), Is.True, "No test release was needed to drain a real close.");
        Assert.That(f.Rows.ItemsSource, Is.SameAs(browseSource)); Assert.That(f.Rows.SelectedItems!.Cast<object>(), Is.EquivalentTo(selected));
        Assert.That(f.Gate.Commands.Count(NativeHistoryGate.IsGraph), Is.EqualTo(graphCount), "Returning from a read-only Log must not notify/refresh Browse.");
        Assert.That(f.Main.IsVisible, Is.True); Assert.That(await f.Materials(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    public async Task C_RenamedLiteralFileCarriesItsCompletedSidecarIntoEveryBatchAndDeepSelectionUsesTheOldBlob()
    {
        await using ProgressiveBrowseTestScope f = await ProgressiveBrowseTestScope.CreateAsync();
        using var presentation = f.EnterPresentation();
        NativeHistoryGate.Slot gate = f.Gate.Arm(); string material = await f.Materials();
        string[] expected = (await f.Git("--literal-pathspecs", "log", "--parents", "--format=%H %P", "--all", "--", NewName, OldName)).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await f.OpenAsync(new(PathFilter: new(NewName, BrowsePathKind.File), SelectedRevision: new(f.Deep), FirstRevision: new(f.DeepFirst)));
        await Ready(f.Main, gate); object source = f.Rows.ItemsSource!; object[] prefix = f.Rows.Items.Cast<object>().ToArray();
        GitFileHistoryGraph sidecar = (GitFileHistoryGraph)typeof(MainWindow).GetField("_browseFileHistory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(f.Main)!;
        Assert.That(sidecar.PathByCommit[f.Deep], Is.EqualTo(OldName)); Assert.That(sidecar.PathByCommit[f.Head], Is.EqualTo(NewName));
        Assert.That(sidecar.PathByCommit.ContainsKey(f.Native[0]), Is.False, "HEAD-followed names must not be invented for the unrelated branch.");
        Assert.That(Hashes(f.Main), Is.EqualTo(expected.Take(100).Select(line => line.Split(' ')[0])));
        Assert.That(f.Gate.Commands.Count(argv => argv.Contains("--follow")), Is.EqualTo(1), "HEAD discovery must complete before the first graph callback.");
        Assert.That(f.Gate.Commands.Count(NativeHistoryGate.IsGraph), Is.EqualTo(1));
        Assert.That(f.Gate.Commands.Single(NativeHistoryGate.IsGraph), Does.Contain(NewName).And.Contain(OldName).And.Not.Contain(f.Deep));
        gate.Release(); await Complete(f.Main);
        GitFileHistoryGraph completedSidecar = (GitFileHistoryGraph)typeof(MainWindow).GetField("_browseFileHistory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(f.Main)!;
        Assert.That(completedSidecar.Paths, Is.SameAs(sidecar.Paths)); Assert.That(completedSidecar.PathByCommit, Is.SameAs(sidecar.PathByCommit));
        AssertGraph(f.Main, expected); Assert.That(f.Rows.ItemsSource, Is.SameAs(source)); Assert.That(prefix.All(item => f.Rows.Items.Cast<object>().Any(current => ReferenceEquals(current, item))), Is.True);
        Assert.That(Key(f.Rows.SelectedItem!), Is.EqualTo(f.Deep)); Assert.That(Selected(f.Main), Is.EquivalentTo(new[] { f.DeepFirst, f.Deep }));
        Assert.That(Groups(f.Main).Single().BaseRevision, Is.EqualTo(f.DeepFirst)); Assert.That(Groups(f.Main).Single().Revision, Is.EqualTo(f.Deep));
        Assert.That(f.Diff.CanonicalText, Does.Contain("-followed main 49").And.Contain("+followed main 50"));
        RevisionDiffFileSelection[] files = FileStatusTestHelpers.Rows(f.Files).Select(item => item.Tag).OfType<RevisionDiffFileSelection>().ToArray();
        Assert.That(files.Select(file => file.File.Path), Does.Contain(OldName).And.Contain("a-other.txt").And.Contain("file.txt"));
        PushProcessTestDriver.Pointer(Field<TabItem>(f.Main, "FileTreeTab")); await Panel(f.Main);
        Assert.That(((TreeViewItem)Field<TreeView>(f.Main, "CommitFileTree").SelectedItem!).Tag, Is.EqualTo(OldName));
        CheckBox blame = Field<CheckBox>(f.Main, "FileHistoryInlineBlameToggle"); if (blame.IsChecked == true) { PushProcessTestDriver.Pointer(blame); await Panel(f.Main); }
        Assert.That(Field<FileContentView>(f.Main, "CommitFileContent").TextView.Text, Is.EqualTo("followed main 50\n"));
        Assert.That(f.Gate.Commands.Count(argv => argv.Contains("--follow")), Is.EqualTo(1)); Assert.That(f.Gate.Commands.Count(NativeHistoryGate.IsGraph), Is.EqualTo(1));
        gate.AssertRetired(); Assert.That(await f.Materials(), Is.EqualTo(material)); Capture(f.Main, "C-en-light-old-literal-blob");
    }

    [AvaloniaTest]
    public async Task D_DirectoryAndAcceptedAuthorMessageBranchFiltersKeepTheNativePathParentsWithoutFollowingOrWidening()
    {
        await using ProgressiveBrowseTestScope f = await ProgressiveBrowseTestScope.CreateAsync(frenchDark: true);
        using var presentation = f.EnterPresentation();
        await f.OpenAsync(new(PathFilter: new(DirectoryName, BrowsePathKind.Directory))); await Complete(f.Main);
        HistoryFilterDialog filter = await OpenFilter(f.Main); FilterText(filter, "Author", "Stream Tester"); FilterText(filter, "Message", "Keep"); FilterText(filter, "BranchFilter", "main");
        NativeHistoryGate.Slot gate = f.Gate.Arm(); AcceptFilter(filter); await Ready(f.Main, gate);
        string[] expected = (await f.Git("--literal-pathspecs", "log", "--parents", "--format=%H %P", "--author=Stream Tester", "--grep=Keep", "main", "--", DirectoryName)).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(expected.Length, Is.GreaterThan(100)); Assert.That(Hashes(f.Main), Is.EqualTo(expected.Take(100).Select(line => line.Split(' ')[0])));
        object source = f.Rows.ItemsSource!; int resets = 0; ((INotifyCollectionChanged)source).CollectionChanged += (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) { resets++; } };
        await Click(f.Main, Hashes(f.Main)[5]); Press(f.Main, PhysicalKey.ArrowDown); await Panel(f.Main); object selected = f.Rows.SelectedItem!;
        await FileRow(f.Main, DirectoryName + "/inside.txt");
        Assert.That(f.Diff.CanonicalText, Does.Contain("inside main")); Assert.That(Loading(f.Main), Is.True);
        gate.Release(); await Complete(f.Main); AssertGraph(f.Main, expected);
        Assert.That(f.Rows.ItemsSource, Is.SameAs(source)); Assert.That(resets, Is.Zero); Assert.That(f.Rows.SelectedItem, Is.SameAs(selected));
        Assert.That(f.Gate.Commands.Any(argv => argv.Contains("--follow")), Is.False);
        string[] command = f.Gate.Commands.Where(NativeHistoryGate.IsGraph).Last();
        Assert.That(command, Does.Contain("main").And.Contain(DirectoryName).And.Not.Contain("--all"));
        Assert.That(Hashes(f.Main).Intersect(f.Native.Take(130)), Is.Empty, "The newer unrelated branch is excluded by the actual accepted branch expression.");
        Assert.That(f.Rows.Items.OfType<GitCommit>().Any(commit => commit.Subject.StartsWith("Outside", StringComparison.Ordinal)), Is.False);
        gate.AssertRetired(); Capture(f.Main, "D-fr-dark-directory-filter");
    }

    [AvaloniaTest]
    public async Task E_SameRowClickRevokesDeferredSelectionAndUnacceptedBranchDraftKeepsItsCaretAcrossEof()
    {
        await using ProgressiveBrowseTestScope f = await ProgressiveBrowseTestScope.CreateAsync();
        using var presentation = f.EnterPresentation();
        NativeHistoryGate.Slot gate = f.Gate.Arm(); await f.OpenAsync(new(SelectedRevision: new(f.Deep))); await Ready(f.Main, gate);
        object source = f.Rows.ItemsSource!; string chosen = Key(f.Rows.SelectedItem!); Assert.That(chosen, Is.Not.EqualTo(f.Deep));
        int changed = 0; f.Rows.SelectionChanged += (_, _) => changed++;
        await Click(f.Main, chosen); Assert.That(changed, Is.Zero, "This is specifically the real click without a SelectionChanged event.");
        object selected = f.Rows.SelectedItem!; object? caret = Caret(f.Main)?.Content;
        TextBox editor = Field<BranchHistoryFilterComboBox>(f.Main, "BranchHistoryFilter").GetVisualDescendants().OfType<TextBox>().Single();
        Edit(f.Main, editor, "newer draft"); Press(f.Main, PhysicalKey.ArrowLeft); Press(f.Main, PhysicalKey.ArrowLeft, RawInputModifiers.Shift);
        int caretIndex = editor.CaretIndex, start = editor.SelectionStart, end = editor.SelectionEnd;
        Assert.That(editor.IsKeyboardFocusWithin, Is.True); gate.Release(); await Complete(f.Main);
        Assert.Multiple(() =>
        {
            Assert.That(Hashes(f.Main), Is.EqualTo(f.Native)); Assert.That(f.Rows.ItemsSource, Is.SameAs(source));
            Assert.That(f.Rows.SelectedItem, Is.SameAs(selected)); Assert.That(Key(f.Rows.SelectedItem!), Is.EqualTo(chosen).And.Not.EqualTo(f.Deep));
            Assert.That(editor.Text, Is.EqualTo("newer draft")); Assert.That(editor.CaretIndex, Is.EqualTo(caretIndex));
            Assert.That(editor.SelectionStart, Is.EqualTo(start)); Assert.That(editor.SelectionEnd, Is.EqualTo(end)); Assert.That(editor.IsKeyboardFocusWithin, Is.True);
            Assert.That(caret, Is.SameAs(selected));
        });
        Assert.That(f.Gate.Commands.Count(NativeHistoryGate.IsGraph), Is.EqualTo(1)); gate.AssertRetired(); Capture(f.Main, "E-en-light-editor-intent");
        // An explicit but path-invisible target is a distinct EOF decision, never an early empty-history fallback.
        string missingHash = (await f.Git("rev-parse", "HEAD~111")).StandardOutput.Trim(); // main48 changes only the outside directory.
        MainWindow absent = new(new BrowseRequest(PathFilter: new(DirectoryName, BrowsePathKind.Directory), SelectedRevision: new(missingHash))) { Width = 1440, Height = 960 };
        f.Track(absent); NativeHistoryGate.Slot missing = f.Gate.Arm(); absent.Show(); await absent.OpenRepositoryAsync(f.Root.Repository.Path); await Ready(absent, missing); f.AssertPresentation(absent);
        Assert.That(Field<DiffView>(absent, "CommitDiff").Text, Does.Not.Contain("not visible"));
        Assert.That(Loading(absent), Is.True); missing.Release(); await Complete(absent);
        Assert.That(Hashes(absent), Does.Not.Contain(missingHash)); Assert.That(Field<ListBox>(absent, "RevisionHistory").SelectedItem, Is.Null);
        Assert.That(Field<DiffView>(absent, "CommitDiff").Text, Does.Contain("not visible").And.Contain(missingHash).And.Not.Contain("no commits yet")); missing.AssertRetired();
    }

    [AvaloniaTest]
    public async Task F_AcceptedFilterThenRepositoryReplacementDrainEachOldNativeGenerationBeforeAdmittingTheNext()
    {
        await using ProgressiveBrowseTestScope f = await ProgressiveBrowseTestScope.CreateAsync(frenchDark: true);
        using var presentation = f.EnterPresentation();
        NativeHistoryGate.Slot old = f.Gate.Arm(); await f.OpenAsync(); await Ready(f.Main, old);
        Task oldWork = HistoryWork(f.Main);
        HistoryFilterDialog filter = await OpenFilter(f.Main); FilterText(filter, "Message", "Keep main");
        NativeHistoryGate.Slot filtered = f.Gate.Arm(); AcceptFilter(filter); await Ready(f.Main, filtered);
        Assert.That(oldWork.IsCompleted, Is.True); old.AssertRetired(); Assert.That(filtered.PreviousRetiredAtAdmission, Is.True);
        Assert.That(f.Rows.Items.OfType<GitCommit>().All(c => c.Subject.StartsWith("Keep main", StringComparison.Ordinal)), Is.True);
        string other = f.Root.Output("replacement"); Directory.CreateDirectory(other); await NativeGit(other, "init", "--initial-branch=other");
        await f.ImportAsync(other, "other", 160, "other-newer", 130);
        string[] expected = (await NativeGit(other, "log", "--format=%H", "--all", "--")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Task filteredWork = HistoryWork(f.Main); NativeHistoryGate.Slot replacement = f.Gate.Arm(); await f.Main.OpenRepositoryAsync(other); await Ready(f.Main, replacement);
        filtered.AssertRetired(); Assert.That(filteredWork.IsCompleted, Is.True); Assert.That(replacement.PreviousRetiredAtAdmission, Is.True);
        Assert.That(Hashes(f.Main), Is.EqualTo(expected.Take(100))); Assert.That(Hashes(f.Main).Intersect(f.Native), Is.Empty);
        object[] prefix = f.Rows.Items.Cast<object>().ToArray(); object source = f.Rows.ItemsSource!; await Click(f.Main, expected[2]); object selected = f.Rows.SelectedItem!;
        RepositoryHistoryStore historyStore = new(); byte[] historyBytes = File.ReadAllBytes(historyStore.FilePath); DateTime historyWritten = File.GetLastWriteTimeUtc(historyStore.FilePath);
        Assert.That(historyStore.Load().Recent.Select(entry => entry.Path), Is.EqualTo(new[] { other, f.Root.Repository.Path })); Assert.That(historyStore.Load().LastVisitedRepositoryPath, Is.EqualTo(other));
        old.ReleaseAll(); filtered.ReleaseAll(); Pump(f.Main);
        Assert.That(Hashes(f.Main), Is.EqualTo(expected.Take(100))); Assert.That(f.Rows.ItemsSource, Is.SameAs(source));
        replacement.Release(); await Complete(f.Main);
        Assert.That(Hashes(f.Main), Is.EqualTo(expected)); Assert.That(f.Rows.ItemsSource, Is.SameAs(source)); Assert.That(f.Rows.SelectedItem, Is.SameAs(selected));
        Assert.That(prefix.All(item => f.Rows.Items.Cast<object>().Any(current => ReferenceEquals(current, item))), Is.True);
        Assert.That(Field<TextBox>(f.Main, "RepositoryPath").Text, Is.EqualTo(other));
        Assert.That(f.Main.Title, Is.EqualTo("replacement (other) — Git Extensions")); Assert.That(f.Main.LastException, Is.Null); Assert.That(f.Gate.Commands.Count(NativeHistoryGate.IsGraph), Is.EqualTo(3));
        Assert.That(File.ReadAllBytes(historyStore.FilePath), Is.EqualTo(historyBytes)); Assert.That(File.GetLastWriteTimeUtc(historyStore.FilePath), Is.EqualTo(historyWritten), "EOF and stale generations must not record another visit.");
        replacement.AssertRetired(); Capture(f.Main, "F-fr-dark-replacement");
    }

    [AvaloniaTest]
    public async Task G_LateNativeFailureKeepsThePrefixProvisionalAndAnExplicitF5RetryAlonePublishesSuccessfulEof()
    {
        await using ProgressiveBrowseTestScope f = await ProgressiveBrowseTestScope.CreateAsync();
        using var presentation = f.EnterPresentation();
        NativeHistoryGate.Slot failed = f.Gate.Arm(fail: true); await f.OpenAsync(); await Ready(f.Main, failed);
        object source = f.Rows.ItemsSource!; object[] prefix = f.Rows.Items.Cast<object>().ToArray(); await DiffTab(f.Main); await Click(f.Main, f.Native[4]);
        failed.Release(); await Complete(f.Main, error: true);
        Assert.That(f.Main.LastException, Is.TypeOf<GitCommandException>()); GitCommandException error = (GitCommandException)f.Main.LastException!;
        Assert.That(error.Result.ExitCode, Is.EqualTo(17)); Assert.That(error.Result.StandardErrorBytes.Length, Is.LessThanOrEqualTo(65536));
        Assert.That(error.Result.Warnings.Any(warning => warning.Contains("truncated", StringComparison.Ordinal)), Is.True);
        Assert.That(error.Result.StandardError, Does.Contain("private late graph failure"));
        Assert.That(Field<TextBlock>(f.Main, "RepositoryStatus").Text, Does.Contain("Load revisions failed"));
        Assert.That(Field<Button>(f.Main, "ViewErrorDetails").IsVisible, Is.True);
        Assert.That(Hashes(f.Main), Is.EqualTo(f.Native.Take(100))); Assert.That(f.Rows.ItemsSource, Is.SameAs(source));
        Assert.That(f.Rows.Items.OfType<ArtificialHistoryRevision>(), Is.Empty, "CompleteCommits would invent an EOF fallback before the absent captured HEAD; failure may not complete that prefix.");
        Assert.That(prefix.All(item => f.Rows.Items.Cast<object>().Any(current => ReferenceEquals(current, item))), Is.True);
        failed.AssertRetired(); Capture(f.Main, "G-en-light-late-error");
        await Click(f.Main, f.Native[5]); await FileRow(f.Main, "file.txt");
        Assert.That(Key(f.Rows.SelectedItem!), Is.EqualTo(f.Native[5]));
        Assert.That(f.Diff.DocumentIdentity, Does.Contain(f.Native[5]));
        Assert.That(f.Main.LastException, Is.SameAs(error), "Selecting another loaded commit/file cannot recover the failed history producer.");
        Assert.That(f.Main.HistoryLoadingException, Is.SameAs(error));
        Assert.That(Field<TextBlock>(f.Main, "RepositoryStatus").Text, Does.Contain("Load revisions failed"));
        Assert.That(Field<Button>(f.Main, "ViewErrorDetails").IsVisible, Is.True);
        Assert.That(Hashes(f.Main), Is.EqualTo(f.Native.Take(100))); Assert.That(f.Rows.ItemsSource, Is.SameAs(source));
        Assert.That(f.Rows.Items.OfType<ArtificialHistoryRevision>(), Is.Empty);
        Assert.That(prefix.All(item => f.Rows.Items.Cast<object>().Any(current => ReferenceEquals(current, item))), Is.True);
        Assert.That(f.Gate.Commands.Count(NativeHistoryGate.IsGraph), Is.EqualTo(1));
        NativeHistoryGate.Slot retry = f.Gate.Arm(); Press(f.Main, PhysicalKey.F5); await Ready(f.Main, retry);
        Assert.That(f.Main.LastException, Is.Null); retry.Release(); await Complete(f.Main);
        Assert.That(Hashes(f.Main), Is.EqualTo(f.Native)); Assert.That(Field<Button>(f.Main, "ViewErrorDetails").IsVisible, Is.False);
        Assert.That(f.Gate.Commands.Count(NativeHistoryGate.IsGraph), Is.EqualTo(2)); retry.AssertRetired();
    }

    [AvaloniaTest]
    public async Task H_ActualOwnerCloseDrainsAllPartialHistoryActorsAndCannotPublishAgainAfterTheOwnerIsGone()
    {
        await using ProgressiveBrowseTestScope f = await ProgressiveBrowseTestScope.CreateAsync(frenchDark: true);
        using var presentation = f.EnterPresentation();
        Window owner = new() { Width = 1500, Height = 1000 }; owner.Show(); NativeHistoryGate.Slot gate = f.Gate.Arm();
        try
        {
            await f.OpenAsync(owner: owner); await Ready(f.Main, gate); await DiffTab(f.Main); await Click(f.Main, f.Native[12]);
            object source = f.Rows.ItemsSource!; object[] prefix = f.Rows.Items.Cast<object>().ToArray(); string[] selection = Selected(f.Main); string material = await f.Materials();
            Task admitted = HistoryWork(f.Main); Capture(f.Main, "H-fr-dark-before-owner-close"); owner.Close();
            await Until(f.Main, () => !owner.IsVisible && !f.Main.IsVisible && admitted.IsCompleted && f.Main.WindowWorkCompletion.IsCompleted);
            await admitted; await f.Main.WindowWorkCompletion; gate.AssertRetired();
            string closedProfile = f.ProfileMaterial();
            gate.Release(); Pump(f.Main); Pump(owner);
            Assert.Multiple(() =>
            {
                Assert.That(owner.IsVisible || f.Main.IsVisible, Is.False); Assert.That(Loading(f.Main), Is.False);
                Assert.That(f.Rows.ItemsSource, Is.SameAs(source)); Assert.That(f.Rows.Items.Cast<object>(), Is.EqualTo(prefix)); Assert.That(Selected(f.Main), Is.EqualTo(selection));
                Assert.That(f.Gate.Commands.Count(NativeHistoryGate.IsGraph), Is.EqualTo(1));
            });
            Assert.That(await f.Materials(), Is.EqualTo(material));
            Assert.That(f.ProfileMaterial(), Is.EqualTo(closedProfile), "No callback may write the profile after the real close has drained.");
        }
        finally { owner.Close(); }
    }

    private static void AssertGraph(MainWindow window, string[] native)
    {
        Assert.That(Hashes(window), Is.EqualTo(native.Select(line => line.Split(' ')[0])));
        Dictionary<string, string[]> parents = native.Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToDictionary(fields => fields[0], fields => fields[1..]);
        foreach (GitCommit commit in Field<ListBox>(window, "RevisionHistory").Items.OfType<GitCommit>())
        { Assert.That(commit.Parents, Is.EqualTo(parents[commit.Hash]), "Actual native rewritten parents for " + commit.Hash); }
    }
}

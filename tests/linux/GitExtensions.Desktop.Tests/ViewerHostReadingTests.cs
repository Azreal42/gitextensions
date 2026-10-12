using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitCommands.Settings;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class ViewerHostReadingTests
{
    private const string LiteralPath = ":café[1]* λ.txt";
    private const string NeighborPath = ":café1Z λ.txt";
    private const string HighChange = "edited high λ";

    [AvaloniaTest]
    public async Task MainFocusedControlGOpensFileLineDialogAndWholeFileKeepsSelection()
    {
        string initial = HighLines();
        await using Fixture fixture = await Fixture.CreateAsync(initial, initial.Replace("line 180\n", HighChange + "\n", StringComparison.Ordinal), commitChanges: true);
        MainWindow main = await fixture.MainAsync();
        DiffView view = await SelectRevisionFile(main, LiteralPath);
        Assert.That(view.MaxFileLine, Is.GreaterThan(180));
        Assert.That(view.Editor.Document.LineCount, Is.LessThan(30), "The actual revision diff is partial, not a 240-row source document.");
        fixture.InstallTerminalSentinel();
        view.Editor.TextArea.Focus();
        Assert.That(view.IsKeyboardFocusWithin, Is.True);
        Press(main, PhysicalKey.G, RawInputModifiers.Control);
        await Until(() => main.OwnedWindows.Any(window => window.Name == "DiffViewerGoToLineDialog") || File.Exists(fixture.TerminalMarker));
        Assert.That(File.Exists(fixture.TerminalMarker), Is.False, "The focused viewer must consume CtrlG before Browse's terminal command.");
        Window goTo = main.OwnedWindows.Single(window => window.Name == "DiffViewerGoToLineDialog");
        NumericUpDown number = Find<NumericUpDown>(goTo, "DiffViewerLineNumber");
        Assert.That(number.Maximum, Is.EqualTo(view.MaxFileLine).And.GreaterThan(view.Editor.Document.LineCount));
        number.Value = 180;
        Click(goTo, "DiffViewerGoToAccept");
        await view.ReadingMenu.PendingOperation.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(view.CurrentFileLine, Is.EqualTo(180));
        Assert.That(main.LastException, Is.Null);
        Assert.That(Find<TabControl>(main, "DetailsTabs").SelectedItem, Has.Property("Name").EqualTo("DiffTab"));

        SelectToken(view, HighChange);
        string identity = view.DocumentIdentity!;
        view.Editor.TextArea.Focus();
        Press(main, PhysicalKey.E, RawInputModifiers.Control);
        await Until(() => !main.IsBusy && view.Text.Contains(" line 001\n", StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(view.CurrentFileLine, Is.EqualTo(180), "Whole-file reload must remap by file line, not retain a patch-row offset.");
            Assert.That(Selected(view), Is.EqualTo(HighChange));
            Assert.That(view.DocumentIdentity, Is.EqualTo(identity));
            Assert.That(view.Editor.Document.LineCount, Is.GreaterThan(240));
            Assert.That(File.Exists(fixture.TerminalMarker), Is.False);
            Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        });
    }

    [AvaloniaTest]
    public async Task MainReadingIdentityResetsForOtherFileAndRevisionButNotContextOptions()
    {
        string initial = HighLines();
        await using Fixture fixture = await Fixture.CreateAsync(initial, initial.Replace("line 180\n", HighChange + "\n", StringComparison.Ordinal), commitChanges: true);
        MainWindow main = await fixture.MainAsync();
        DiffView view = await SelectRevisionFile(main, LiteralPath);
        SelectToken(view, HighChange);
        string identity = view.DocumentIdentity!;
        GitDiffDocument document = view.Document!;
        Click(main, "RevisionDiffIncreaseContext");
        await Until(() => !main.IsBusy && !ReferenceEquals(view.Document, document));
        Assert.That(view.DocumentIdentity, Is.EqualTo(identity));
        Assert.That(view.CurrentFileLine, Is.EqualTo(180));
        Assert.That(Selected(view), Is.EqualTo(HighChange));

        await SelectRevisionFile(main, NeighborPath);
        Assert.That(view.DocumentIdentity, Is.Not.EqualTo(identity));
        Assert.That(view.SelectionStart, Is.Zero);
        Assert.That(view.SelectionLength, Is.Zero);
        await SelectRevisionFile(main, LiteralPath);
        SelectToken(view, HighChange);
        ListBox history = Find<ListBox>(main, "RevisionHistory");
        history.SelectedItem = history.ItemsSource!.OfType<GitCommit>().Single(commit => commit.Hash == fixture.BaseRevision);
        await Until(() => !main.IsBusy && view.DocumentIdentity?.Contains(fixture.BaseRevision, StringComparison.Ordinal) == true
            && view.Text.Contains("+line 001", StringComparison.Ordinal));
        Assert.That(view.SelectionStart, Is.Zero, "Opening the same path at another commit establishes a new reading identity.");
        Assert.That(view.SelectionLength, Is.Zero);
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }

    [AvaloniaTest]
    public async Task CompareHighFileLineAndWholeFileReloadPreserveActualSelectedCode()
    {
        string initial = HighLines();
        await using Fixture fixture = await Fixture.CreateAsync(initial, initial.Replace("line 180\n", HighChange + "\n", StringComparison.Ordinal), commitChanges: true);
        CompareRevisionsDialog dialog = fixture.Track(new CompareRevisionsDialog(fixture.Repository, fixture.BaseRevision, "HEAD"));
        dialog.Show();
        await dialog.InitializeAsync();
        Find<ListBox>(dialog, "ComparisonFiles").SelectedItem = dialog.Comparison!.Files.Single(file => file.Path == LiteralPath);
        DiffView view = Find<FileHistoryContentView>(dialog, "ComparisonDiff").TextView;
        await Until(() => !dialog.IsBusy && view.Text.Contains(HighChange, StringComparison.Ordinal));
        Assert.That(view.Editor.Document.LineCount, Is.LessThan(30));
        view.Editor.TextArea.Focus();
        Press(dialog, PhysicalKey.G, RawInputModifiers.Control);
        await Until(() => dialog.OwnedWindows.Any(window => window.Name == "DiffViewerGoToLineDialog"));
        Window goTo = dialog.OwnedWindows.Single(window => window.Name == "DiffViewerGoToLineDialog");
        NumericUpDown number = Find<NumericUpDown>(goTo, "DiffViewerLineNumber");
        Assert.That(number.Maximum, Is.GreaterThan(view.Editor.Document.LineCount));
        number.Value = 180; Click(goTo, "DiffViewerGoToAccept");
        await view.ReadingMenu.PendingOperation.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(view.CurrentFileLine, Is.EqualTo(180));
        SelectToken(view, HighChange);
        string identity = view.DocumentIdentity!;
        view.Editor.TextArea.Focus(); Press(dialog, PhysicalKey.E, RawInputModifiers.Control);
        await Until(() => !dialog.IsBusy && view.Text.Contains(" line 001\n", StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(dialog.ReadDiffOptions().ShowEntireFile, Is.True);
            Assert.That(view.CurrentFileLine, Is.EqualTo(180));
            Assert.That(Selected(view), Is.EqualTo(HighChange));
            Assert.That(view.DocumentIdentity, Is.EqualTo(identity));
            Assert.That(dialog.LastException, Is.Null, dialog.LastError);
            Assert.That(view.Editor.Document.LineCount, Is.GreaterThan(240));
        });
    }

    [AvaloniaTest]
    public async Task MainLatin1SelectedLinesAndHunksRoundTripExactIndexBytesAndLiteralPath()
    {
        const string selected = "sélection été", remaining = "reste à faire";
        string initial = EncodedLines();
        string changed = Additions(initial, selected, remaining);
        await using Fixture fixture = await Fixture.CreateAsync(initial, changed, "iso-8859-1");
        MainWindow main = await fixture.MainAsync();
        SelectTab(main, "ChangesTab");
        TreeView changes = Find<TreeView>(main, "WorkingFiles");
        FileStatusTestHelpers.Select(changes, LiteralPath);
        DiffView view = Find<DiffView>(main, "WorktreeDiff");
        await Until(() => !main.IsBusy && view.Text.Contains("+" + selected, StringComparison.Ordinal));
        GitDiffDocument previous = view.Document!;
        ChooseEncoding(FindVisibleEncoding(main), "iso-8859-1");
        await Until(() => !main.IsBusy && !ReferenceEquals(view.Document, previous));
        AssertLossless(view, "iso-8859-1");

        foreach (bool lines in new[] { true, false })
        {
            FileStatusTestHelpers.Select(changes, LiteralPath);
            await Until(() => !main.IsBusy && view.Text.Contains("+" + selected, StringComparison.Ordinal));
            if (lines) { SelectToken(view, "+" + selected); }
            else { HunkList(main).SelectedIndex = 0; }
            ClickContent(main, lines ? "Stage selected lines" : "Stage selected hunks");
            await Until(() => !main.IsBusy && FileStatusTestHelpers.Changes(changes).Single(change => change.Path == LiteralPath).IsStaged);
            Assert.That(await fixture.IndexAsync(LiteralPath), Is.EqualTo(fixture.Encoding.GetBytes(AddFirst(initial, selected))));
            await fixture.AssertNeighborAndWorktreeAsync();

            FindStagedOption(main).IsChecked = true;
            await Until(() => !main.IsBusy && view.Text.Contains("+" + selected, StringComparison.Ordinal) && view.DocumentIdentity!.EndsWith("index", StringComparison.Ordinal));
            AssertLossless(view, "iso-8859-1");
            if (lines) { SelectToken(view, "+" + selected); }
            else { HunkList(main).SelectedIndex = 0; }
            ClickContent(main, lines ? "Unstage selected lines" : "Unstage selected hunks");
            await Until(() => !main.IsBusy && !FileStatusTestHelpers.Changes(changes).Single(change => change.Path == LiteralPath).IsStaged);
            Assert.That(await fixture.IndexAsync(LiteralPath), Is.EqualTo(fixture.InitialBytes));
            await fixture.AssertNeighborAndWorktreeAsync();
            FindStagedOption(main).IsChecked = false;
            await Until(() => !main.IsBusy && view.Text.Contains("+" + remaining, StringComparison.Ordinal));
        }
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        Assert.That(view.FileEncodingName, Is.EqualTo("iso-8859-1"), "The session codec survives index/worktree identity changes.");
    }

    [AvaloniaTest]
    public async Task CommitWindows1252SelectedLinesAndHunksKeepEuroSmartQuotesAndOtherFileBytes()
    {
        const string selected = "sélection coût € “smart”", remaining = "reste € ‘intact’";
        string initial = EncodedLines();
        string changed = Additions(initial, selected, remaining);
        await using Fixture fixture = await Fixture.CreateAsync(initial, changed, "windows-1252");
        CommitDialog dialog = fixture.Track(new CommitDialog(fixture.Repository));
        dialog.Show(); await dialog.InitializeAsync();
        TreeView unstaged = Find<TreeView>(dialog, "Unstaged"), staged = Find<TreeView>(dialog, "Staged");
        FileStatusTestHelpers.Select(unstaged, LiteralPath);
        DiffView view = Find<DiffView>(dialog, "SelectedDiff");
        await Until(() => !dialog.IsBusy && view.Text.Contains("+" + selected, StringComparison.Ordinal));
        GitDiffDocument previous = view.Document!;
        ChooseEncoding(FindVisibleEncoding(dialog), "windows-1252");
        await Until(() => !dialog.IsBusy && !ReferenceEquals(view.Document, previous));
        AssertLossless(view, "windows-1252");
        Assert.That(view.Text, Does.Contain("€ “smart”").And.Not.Contain("\uFFFD"));
        foreach (bool lines in new[] { true, false })
        {
            FileStatusTestHelpers.Select(unstaged, LiteralPath);
            await Until(() => !dialog.IsBusy && view.Text.Contains("+" + selected, StringComparison.Ordinal));
            SelectToken(view, "+" + selected);
            Click(dialog, lines ? "StageLines" : "StageHunk");
            await Until(() => !dialog.IsBusy && FileStatusTestHelpers.Changes(staged).Any(change => change.Path == LiteralPath));
            Assert.That(await fixture.IndexAsync(LiteralPath), Is.EqualTo(fixture.Encoding.GetBytes(AddFirst(initial, selected))));
            await fixture.AssertNeighborAndWorktreeAsync();
            FileStatusTestHelpers.Select(staged, LiteralPath);
            await Until(() => !dialog.IsBusy && view.DocumentIdentity!.EndsWith("index", StringComparison.Ordinal) && view.Text.Contains("+" + selected, StringComparison.Ordinal));
            AssertLossless(view, "windows-1252");
            SelectToken(view, "+" + selected);
            Click(dialog, lines ? "UnstageLines" : "UnstageHunk");
            await Until(() => !dialog.IsBusy && !FileStatusTestHelpers.Changes(staged).Any(change => change.Path == LiteralPath));
            Assert.That(await fixture.IndexAsync(LiteralPath), Is.EqualTo(fixture.InitialBytes));
            await fixture.AssertNeighborAndWorktreeAsync();
        }
        Assert.That(dialog.LastError, Is.Null, dialog.LastError);
        Assert.That(dialog.HasRepositoryChanges, Is.True);
        Assert.That(dialog.HasCommitted, Is.False);
    }

    [AvaloniaTest]
    public async Task MainUntrackedInvalidUtf8ChoosesCodecFromCachedBytesWithoutReadingChangedDiskAgain()
    {
        await using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        const string path = ":untracked[2]* λ.txt", decoded = "café € cache\n";
        byte[] bytes = GitFileEncodingCodec.GetEncoding("windows-1252").GetBytes(decoded);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Repository.Path, path), bytes);
        MainWindow main = await fixture.MainAsync();
        SelectTab(main, "ChangesTab");
        FileStatusTestHelpers.Select(Find<TreeView>(main, "WorkingFiles"), path);
        DiffView view = Find<DiffView>(main, "WorktreeDiff");
        await Until(() => !main.IsBusy && view.DocumentIdentity?.Contains(path, StringComparison.Ordinal) == true);
        Assert.That(view.DefaultFileEncodingName, Is.EqualTo("utf-8"));
        Assert.That(view.Text, Is.Not.EqualTo(decoded));
        Assert.That(view.Text, Does.Not.Contain("\uFFFD"));
        Assert.That(view.CanStagePatch, Is.False);
        Assert.That(view.Document, Is.Null);
        const string later = "later on disk; not the loaded preview\n";
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, path), later);
        ChooseEncoding(FindVisibleEncoding(main), "windows-1252");
        await Until(() => view.Text == decoded);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, path)), Is.EqualTo(later));
        Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.PlainText));
        Assert.That(view.CanStagePatch, Is.False);
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        Assert.That((await fixture.Repository.GetStatusAsync()).Changes.Single(change => change.Path == path).IsUntracked, Is.True);
    }

    [AvaloniaTest]
    public async Task BrowseCommitAndCompareKeepLocalOptionsAndGridSavesTheLatestSharedSession()
    {
        string initial = HighLines(), changed = initial.Replace("line 180\n", HighChange + "\n", StringComparison.Ordinal);
        await using Fixture fixture = await Fixture.CreateAsync(initial, changed);
        MainWindow main = await fixture.MainAsync();
        DiffView browse = await SelectRevisionFile(main, LiteralPath);
        CommitDialog commit = fixture.Track(new CommitDialog(fixture.Repository));
        commit.Show(); await commit.InitializeAsync();
        FileStatusTestHelpers.Select(Find<TreeView>(commit, "Unstaged"), LiteralPath);
        DiffView worktree = Find<DiffView>(commit, "SelectedDiff");
        await Until(() => !commit.IsBusy && worktree.Text.Contains(HighChange, StringComparison.Ordinal));
        byte[] index = await fixture.IndexAsync(LiteralPath);
        Assert.That(browse.ComparisonMenu.SelectWhitespace(IgnoreWhitespaceKind.Change), Is.True);
        await Until(() => !main.IsBusy);
        Assert.That(worktree.ComparisonMenu.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.None), "Already-open readers retain their comparison options.");
        Assert.That(browse.ComparisonMenu.ToggleWholeFile(), Is.True); await Until(() => !main.IsBusy);
        Assert.That(worktree.ComparisonMenu.Options.ShowEntireFile, Is.False);
        Assert.That(worktree.ComparisonMenu.IncreaseContext(), Is.True); await Until(() => !commit.IsBusy);
        Assert.That(worktree.ComparisonMenu.Options.ContextLines, Is.EqualTo(4));
        Assert.That(browse.ComparisonMenu.Options.ContextLines, Is.EqualTo(3));

        CompareRevisionsDialog compare = fixture.Track(new CompareRevisionsDialog(fixture.Repository,
            new GitCompareRevision("HEAD"), new GitCompareRevision("", GitCompareRevisionKind.WorkingTree)));
        compare.Show(); await compare.InitializeAsync();
        Find<ListBox>(compare, "ComparisonFiles").SelectedItem = compare.Comparison!.Files.Single(file => file.Path == LiteralPath);
        DiffView next = Find<FileHistoryContentView>(compare, "ComparisonDiff").TextView;
        await Until(() => !compare.IsBusy && next.Text.Contains(HighChange, StringComparison.Ordinal));
        Assert.That(next.ComparisonMenu.Options, Is.EqualTo(new GitComparisonDiffOptions(IgnoreWhitespaceKind.Change)), "Remember whitespace is enabled by default; whole-file and context are reset for a new reader.");
        Assert.That(next.ComparisonMenu.SelectWhitespace(IgnoreWhitespaceKind.Eol), Is.True); await Until(() => !compare.IsBusy);
        MenuItem save = Find<ListBox>(main, "RevisionHistory").ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "SaveCurrentViewSettingsAsDefault");
        save.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        DiffViewerPreferencesStore store = new();
        using JsonDocument defaults = JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath));
        Assert.That(defaults.RootElement.GetProperty("IgnoreWhitespace").GetInt32(), Is.EqualTo((int)IgnoreWhitespaceKind.Eol), "The old Browse reader saves the latest shared session, not its old local Change choice.");
        Assert.That(browse.ComparisonMenu.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.Change));
        Assert.That(worktree.ComparisonMenu.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.None));
        Assert.That(await fixture.IndexAsync(LiteralPath), Is.EqualTo(index));
        await fixture.AssertNeighborAndWorktreeAsync();
        Assert.That(main.LastException ?? compare.LastException, Is.Null);
        Assert.That(commit.LastError, Is.Null);
    }

    [AvaloniaTest]
    public async Task CommitFocusedFindOpensTheOriginalModelessDialogBeforeTheSelectionFilter()
    {
        string initial = HighLines(), changed = initial.Replace("line 180\n", HighChange + "\n", StringComparison.Ordinal);
        await using Fixture fixture = await Fixture.CreateAsync(initial, changed);
        CommitDialog commit = fixture.Track(new CommitDialog(fixture.Repository));
        commit.Show(); await commit.InitializeAsync();
        FileStatusTestHelpers.Select(Find<TreeView>(commit, "Unstaged"), LiteralPath);
        DiffView view = Find<DiffView>(commit, "SelectedDiff");
        await Until(() => !commit.IsBusy && view.Text.Contains(HighChange, StringComparison.Ordinal));
        byte[] index = await fixture.IndexAsync(LiteralPath);
        SelectToken(view, HighChange); view.Editor.TextArea.Focus();
        Press(commit, PhysicalKey.F, RawInputModifiers.Control);
        await Until(() => commit.OwnedWindows.Any(window => window.Name == "DiffViewerFindDialog"));
        Window find = commit.OwnedWindows.Single(window => window.Name == "DiffViewerFindDialog");
        Assert.That(find.Owner, Is.SameAs(commit));
        Assert.That(Find<TextBox>(find, "DiffViewerFindQuery").Text, Is.EqualTo(HighChange));
        Assert.That(Find<TextBox>(find, "DiffViewerFindQuery").IsFocused, Is.True);
        Assert.That(Find<ComboBox>(commit, "SelectionFilter").IsKeyboardFocusWithin, Is.False);
        Click(find, "DiffViewerFindNext");
        Assert.That(Selected(view), Is.EqualTo(HighChange));
        Click(find, "DiffViewerFindCancel");
        Assert.That(view.Editor.TextArea.IsKeyboardFocusWithin, Is.True);
        Press(commit, PhysicalKey.F, RawInputModifiers.Control);
        Assert.That(commit.OwnedWindows.Single(window => window.Name == "DiffViewerFindDialog"), Is.SameAs(find), "Each reader reuses its modeless Find dialog.");
        Click(find, "DiffViewerFindCancel");
        Assert.That(await fixture.IndexAsync(LiteralPath), Is.EqualTo(index));
        await fixture.AssertNeighborAndWorktreeAsync();
        Assert.That(commit.LastError, Is.Null);
    }

    [AvaloniaTest]
    public async Task FileHistoryOptionsReloadTheRealDiffAndFindKeepsItsReadingIdentity()
    {
        string initial = HighLines(), changed = initial.Replace("line 180\n", HighChange + "\n", StringComparison.Ordinal);
        await using Fixture fixture = await Fixture.CreateAsync(initial, changed, commitChanges: true);
        FileHistoryDialog history = fixture.Track(new FileHistoryDialog(fixture.Repository, LiteralPath));
        history.Show(); await history.InitializeAsync();
        Find<TabControl>(history, "FileHistoryTabs").SelectedIndex = 1;
        DiffView view = Find<FileHistoryContentView>(history, "FileHistoryDiff").TextView;
        await Until(() => !history.IsBusy && view.Text.Contains(HighChange, StringComparison.Ordinal));
        string identity = view.DocumentIdentity!;
        SelectToken(view, HighChange);
        Click(history, "FileHistoryDiffEntireFile");
        await Until(() => !history.IsBusy && view.Text.Contains(" line 001\n", StringComparison.Ordinal));
        Assert.That(view.DocumentIdentity, Is.EqualTo(identity));
        Assert.That(Selected(view), Is.EqualTo(HighChange));
        Assert.That(view.ComparisonMenu.Options.ContextLines, Is.EqualTo(3));
        Assert.That(Find<Button>(history, "FileHistoryDiffIncreaseContext").IsEnabled, Is.False);
        view.Editor.TextArea.Focus(); Press(history, PhysicalKey.F, RawInputModifiers.Control);
        await Until(() => history.OwnedWindows.Any(window => window.Name == "DiffViewerFindDialog"));
        Window find = history.OwnedWindows.Single(window => window.Name == "DiffViewerFindDialog");
        Assert.That(Find<TextBox>(find, "DiffViewerFindQuery").Text, Is.EqualTo(HighChange));
        Click(find, "DiffViewerFindCancel");
        Assert.That(history.LastException, Is.Null);
        Assert.That((await fixture.Repository.GetStatusAsync()).Changes, Is.Empty);
    }

    private static string HighLines() => string.Concat(Enumerable.Range(1, 240).Select(number => $"line {number:000}\n"));
    private static string EncodedLines() => string.Concat(Enumerable.Range(1, 30).Select(number => $"ligne {number:00} café\n"));
    private static string AddFirst(string initial, string selected) => initial.Replace("ligne 02 café\n", selected + "\nligne 02 café\n", StringComparison.Ordinal);
    private static string Additions(string initial, string selected, string remaining)
        => AddFirst(initial, selected).Replace("ligne 26 café\n", remaining + "\nligne 26 café\n", StringComparison.Ordinal);
    private static string Selected(DiffView view) => view.Text.Substring(view.SelectionStart, view.SelectionLength);
    private static void SelectToken(DiffView view, string token)
    { int offset = view.Text.IndexOf(token, StringComparison.Ordinal); Assert.That(offset, Is.GreaterThanOrEqualTo(0)); view.Select(offset, token.Length); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static ComboBox FindVisibleEncoding(Window window) => window.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "DiffViewerEncoding" && control.IsEffectivelyVisible);
    private static ListBox HunkList(Window window) => window.GetVisualDescendants().OfType<ListBox>().Single(list => list.ItemsSource?.OfType<GitPatchHunk>().Any() == true);
    private static CheckBox FindStagedOption(Window window) => window.GetVisualDescendants().OfType<CheckBox>().Single(option => option.Content as string == "Show staged changes");
    private static void ChooseEncoding(ComboBox selector, string name)
    {
        int index = GitFileEncodingCodec.AvailableEncodings.ToList().FindIndex(choice => choice.Name == name);
        Assert.That(index, Is.GreaterThanOrEqualTo(0), "The real viewer must offer the requested codec.");
        Assert.That(selector.IsEnabled, Is.True); selector.SelectedIndex = index + 1;
    }
    private static void AssertLossless(DiffView view, string encoding)
    {
        Assert.That(view.Document, Is.Not.Null);
        Assert.That(view.Document!.FileEncodingName, Is.EqualTo(encoding));
        Assert.That(view.Document.IsLosslessPatch, Is.True);
        Assert.That(view.Document.PatchBytes.IsEmpty, Is.False);
        Assert.That(view.CanStagePatch, Is.True);
        Assert.That(view.Text, Does.Not.Contain("\uFFFD"));
    }
    private static void SelectTab(MainWindow main, string name)
    { TabControl tabs = Find<TabControl>(main, "DetailsTabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == name); main.UpdateLayout(); }
    private static async Task<DiffView> SelectRevisionFile(MainWindow main, string path)
    {
        await Until(() => !main.IsBusy);
        SelectTab(main, "DiffTab");
        TreeView files = Find<TreeView>(main, "RevisionFiles");
        TreeViewItem row = FileStatusTestHelpers.Rows(files).Single(item => item.Tag is RevisionDiffFileSelection selection && selection.File.Path == path);
        foreach (TreeViewItem parent in files.Items.OfType<TreeViewItem>()) { parent.IsExpanded = true; }
        files.SelectedItem = row;
        DiffView view = Find<DiffView>(main, "CommitDiff");
        await Until(() => !main.IsBusy && view.DocumentIdentity?.Contains(path, StringComparison.Ordinal) == true && view.Document is not null);
        return view;
    }
    private static void Click(Window window, string name)
    { Button button = Find<Button>(window, name); Assert.That(button.IsEnabled && button.IsEffectivelyVisible, Is.True, name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static void ClickContent(Window window, string content)
    {
        Button button = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == content);
        Assert.That(button.IsEnabled && button.IsEffectivelyVisible, Is.True, content); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Press(Window owner, PhysicalKey key, RawInputModifiers modifiers)
    { owner.KeyPressQwerty(key, modifiers); if (owner.IsVisible) { owner.KeyReleaseQwerty(key, modifiers); } }
    private static async Task Until(Func<bool> predicate)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20)); while (!predicate()) { await Task.Delay(20, timeout.Token); } }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly List<Window> _windows = [];
        public string Temporary { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-viewer-host-" + Guid.NewGuid().ToString("N"));
        public GitRepository Repository { get; private set; } = null!;
        public Encoding Encoding { get; private set; } = null!;
        public byte[] InitialBytes { get; private set; } = [];
        private byte[] ChangedBytes { get; set; } = [];
        private byte[] NeighborInitial { get; set; } = [];
        private byte[] NeighborChanged { get; set; } = [];
        public string BaseRevision { get; private set; } = "";
        public string TerminalMarker => Path.Combine(Temporary, "terminal-was-invoked");
        public T Track<T>(T window) where T : Window { _windows.Add(window); return window; }
        public static async Task<Fixture> CreateAsync(string initial, string changed, string? encoding = null, bool commitChanges = false)
        {
            Fixture fixture = new();
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(fixture.Temporary, "configuration"));
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
                fixture.Encoding = GitFileEncodingCodec.GetEncoding(encoding);
                fixture.InitialBytes = fixture.Encoding.GetBytes(initial); fixture.ChangedBytes = fixture.Encoding.GetBytes(changed);
                fixture.NeighborInitial = fixture.Encoding.GetBytes("neighbor café\n"); fixture.NeighborChanged = fixture.Encoding.GetBytes("neighbor café\ndo not stage\n");
                fixture.Repository = await GitRepository.InitAsync(Path.Combine(fixture.Temporary, "repository"), new GitInitOptions("main"));
                await fixture.Repository.SetConfigAsync("user.name", "Viewer Host Tester");
                await fixture.Repository.SetConfigAsync("user.email", "viewer-host@example.invalid");
                await fixture.Repository.SetConfigAsync("commit.gpgsign", "false");
                await fixture.Repository.SetConfigAsync("core.autocrlf", "false");
                await fixture.Repository.SetConfigAsync("core.hooksPath", Path.Combine(fixture.Temporary, "no-hooks"));
                await fixture.Repository.SetConfigAsync("diff.context", "3");
                if (encoding is not null) { await fixture.Repository.SetConfigAsync("i18n.filesencoding", encoding); }
                await File.WriteAllBytesAsync(Path.Combine(fixture.Repository.Path, LiteralPath), fixture.InitialBytes);
                await File.WriteAllBytesAsync(Path.Combine(fixture.Repository.Path, NeighborPath), fixture.NeighborInitial);
                await fixture.Repository.StageAllAsync(); await fixture.Repository.CommitAsync("Initial host content");
                fixture.BaseRevision = await fixture.Repository.ResolveRevisionAsync("HEAD");
                await File.WriteAllBytesAsync(Path.Combine(fixture.Repository.Path, LiteralPath), fixture.ChangedBytes);
                await File.WriteAllBytesAsync(Path.Combine(fixture.Repository.Path, NeighborPath), fixture.NeighborChanged);
                if (commitChanges) { await fixture.Repository.StageAllAsync(); await fixture.Repository.CommitAsync("Changed host content"); }
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async Task<MainWindow> MainAsync()
        {
            MainWindow main = Track(new MainWindow { Width = 1440, Height = 900 });
            main.Show(); await main.OpenRepositoryAsync(Repository.Path); await Until(() => !main.IsBusy);
            main.UpdateLayout(); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); return main;
        }
        public async Task<byte[]> IndexAsync(string path)
        {
            GitBinaryResult result = await new GitRunner().RunBinaryAsync(Repository.Path, ["show", ":" + path]);
            Assert.That(result.Success, Is.True, result.StandardError); return result.StandardOutput;
        }
        public async Task AssertNeighborAndWorktreeAsync()
        {
            Assert.That(await IndexAsync(NeighborPath), Is.EqualTo(NeighborInitial), "Literal partial staging must leave the adjacent glob-like path's index bytes alone.");
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(Repository.Path, LiteralPath)), Is.EqualTo(ChangedBytes));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(Repository.Path, NeighborPath)), Is.EqualTo(NeighborChanged));
        }
        public void InstallTerminalSentinel()
        {
            if (OperatingSystem.IsWindows()) { return; }
            string binaryDirectory = Path.Combine(Temporary, "sentinel-bin"); Directory.CreateDirectory(binaryDirectory);
            string git = (_path ?? "").Split(Path.PathSeparator).Select(directory => Path.Combine(directory, "git")).First(File.Exists);
            File.CreateSymbolicLink(Path.Combine(binaryDirectory, "git"), git);
            string terminal = Path.Combine(binaryDirectory, "x-terminal-emulator");
            File.WriteAllText(terminal, "#!/bin/sh\nprintf invoked > '" + TerminalMarker.Replace("'", "'\\''", StringComparison.Ordinal) + "'\n");
            File.SetUnixFileMode(terminal, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("PATH", binaryDirectory);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window window in _windows.AsEnumerable().Reverse())
                {
                    foreach (Window child in window.OwnedWindows.ToArray()) { child.Close(); }
                    window.Close();
                }
                await Until(() => _windows.All(window => !window.IsVisible));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", _path);
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); CultureInfo.CurrentUICulture = _culture;
                if (Directory.Exists(Temporary)) { Directory.Delete(Temporary, recursive: true); }
            }
        }
    }
}

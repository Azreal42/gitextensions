using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands.Settings;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class RenameCompareDialogTests
{
    [AvaloniaTest]
    public async Task RenameEnterUsesTheOriginalNormalizerAndPreservesCurrentBranchConfiguration()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string head = await repository.ResolveRevisionAsync("HEAD");
        await Git(fixture, "config", "branch.main.remote", "origin");
        await Git(fixture, "config", "branch.main.merge", "refs/heads/main");
        RenameBranchDialog dialog = new(repository, "main"); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Field<TextBox>(dialog, "RenameBranchName").Text = "feat name/.hidden.lock";
            Capture(dialog, "classic-rename-branch.png");
            Assert.That(Field<TextBox>(dialog, "RenameBranchName").Bounds.Height, Is.EqualTo(24));
            Field<TextBox>(dialog, "RenameBranchName").Focus();
            Press(dialog, PhysicalKey.Enter);
            await Until(() => !dialog.IsVisible);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.RenamedBranch, Is.EqualTo("feat_name/_hidden_lock"));
            Assert.That(dialog.HasRepositoryChanges, Is.True);
            Assert.That((await repository.GetStatusAsync()).Branch, Is.EqualTo(dialog.RenamedBranch));
            Assert.That(await repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
            Assert.That((await Git(fixture, "config", "--get", "branch.feat_name/_hidden_lock.remote")).StandardOutput.Trim(), Is.EqualTo("origin"));
            Assert.That((await Git(fixture, "config", "--get", "branch.feat_name/_hidden_lock.merge")).StandardOutput.Trim(), Is.EqualTo("refs/heads/main"));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task RenameNoOpAndEscapeDoNotChangeReferencesAndInvalidNamesRemainReviewable()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        RenameBranchDialog unchanged = new(repository, "main"); unchanged.Show();
        await unchanged.InitializeAsync(); Click(unchanged, "RenameBranch");
        await Until(() => !unchanged.IsVisible);
        Assert.That(unchanged.HasRepositoryChanges, Is.False);
        RenameBranchDialog invalid = new(repository, "main", autoNormalize: false); invalid.Show();
        try
        {
            await invalid.InitializeAsync();
            Field<TextBox>(invalid, "RenameBranchName").Text = "invalid:name";
            Click(invalid, "RenameBranch"); await Idle(invalid, expectSuccess: false);
            Assert.That(invalid.LastError, Is.Not.Null);
            Assert.That(invalid.IsVisible, Is.True);
            Assert.That(invalid.HasRepositoryChanges, Is.False);
            Press(invalid, PhysicalKey.Escape); await Until(() => !invalid.IsVisible);
            Assert.That((await repository.GetReferencesAsync()).Select(reference => reference.Name), Is.EqualTo(new[] { "refs/heads/main" }));
            using CancellationTokenSource cancelled = new(); cancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => repository.RenameBranchAsync("main", "cancelled", cancelled.Token));
            Assert.That((await repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
        }
        finally { invalid.Close(); }
    }

    [AvaloniaTest]
    public async Task RenamingALinkedWorktreeBranchUpdatesItsHeadAndNeverOverwritesAnotherBranch()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string worktree = Output(fixture, "linked worktree λ");
        await repository.AddWorktreeAsync(worktree, "HEAD", "linked");
        RenameBranchDialog dialog = new(repository, "linked", autoNormalize: false); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Field<TextBox>(dialog, "RenameBranchName").Text = "main";
            Click(dialog, "RenameBranch"); await Idle(dialog, expectSuccess: false);
            Assert.That(dialog.LastError, Is.Not.Null);
            Assert.That((await repository.GetReferencesAsync()).Where(reference => reference.IsBranch).Select(reference => reference.ShortName), Is.EquivalentTo(new[] { "main", "linked" }));
            Field<TextBox>(dialog, "RenameBranchName").Text = "renamed/λ;$()'literal";
            Click(dialog, "RenameBranch"); await Idle(dialog);
            GitRepository linked = await GitRepository.OpenAsync(worktree);
            Assert.That((await linked.GetStatusAsync()).Branch, Is.EqualTo(dialog.RenamedBranch));
            Assert.That((await repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
            Assert.That((await repository.GetWorktreesAsync()).Single(tree => tree.Path == worktree).Branch, Is.EqualTo("refs/heads/" + dialog.RenamedBranch));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ComparisonShowsLiteralRenamesAdditionsDeletionsAndBothBlobVersions()
    {
        string initial = string.Join('\n', Enumerable.Range(1, 60).Select(index => "line " + index)) + "\n";
        using Fixture fixture = await Fixture.CreateAsync(initial, initial);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "deleted.txt"), "old deleted file\n");
        await repository.StageAllAsync(); await repository.CommitAsync("Comparison base");
        string first = await repository.ResolveRevisionAsync("HEAD");
        const string renamed = "[renamed]\nλ.cs";
        const string added = ":added[*].txt";
        await Git(fixture, "mv", "--", "file.txt", renamed);
        string changed = initial.Replace("line 30\n", "changed 30\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, renamed), changed);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, added), "added literal file\n");
        await Git(fixture, "rm", "--", "deleted.txt");
        await repository.StageAllAsync(); await repository.CommitAsync("Comparison target");
        string second = await repository.ResolveRevisionAsync("HEAD");
        CompareRevisionsDialog dialog = new(repository, first, second); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Idle(dialog);
            Assert.That(dialog.Comparison!.Files.Select(file => file.Status), Is.EquivalentTo(new[] { 'R', 'A', 'D' }));
            ListBox files = Field<ListBox>(dialog, "ComparisonFiles");
            GitRevisionFile rename = dialog.Comparison.Files.Single(file => file.Path == renamed);
            Assert.That(rename.OriginalPath, Is.EqualTo("file.txt"));
            files.SelectedItem = rename; await Idle(dialog);
            Assert.That(Field<FileHistoryContentView>(dialog, "ComparisonDiff").TextView.Text, Does.Contain("-line 30").And.Contain("+changed 30").And.Not.Contain("old deleted file"));
            Capture(dialog, "classic-compare-revisions.png");
            Assert.That(files.GetVisualDescendants().OfType<ListBoxItem>().Select(row => row.Bounds.Height), Is.All.EqualTo(24));
            foreach (string name in new[] { "ComparisonBaseBranch", "ComparisonBaseCommit", "ComparisonCompareBranch", "ComparisonCompareCommit", "ComparisonSwap" })
            { Assert.That(((Image)Field<Button>(dialog, name).Content!).Width, Is.EqualTo(16)); }
            TabControl tabs = Field<TabControl>(dialog, "ComparisonTabs");
            tabs.SelectedIndex = 1; await Idle(dialog);
            Assert.That(Field<FileHistoryContentView>(dialog, "ComparisonBaseView").TextView.Text, Is.EqualTo(initial));
            tabs.SelectedIndex = 2; await Idle(dialog);
            Assert.That(Field<FileHistoryContentView>(dialog, "ComparisonCompareView").TextView.Text, Is.EqualTo(changed));
            string saved = Output(fixture, "saved comparison.cs");
            await dialog.SaveSelectedFileAsync(saved);
            Assert.That(await File.ReadAllBytesAsync(saved), Is.EqualTo(Encoding.UTF8.GetBytes(changed)));
            await ExpectAsync<IOException>(() => dialog.SaveSelectedFileAsync(saved));
            string savedBase = Output(fixture, "saved base.cs");
            await dialog.SaveSelectedFileAsync(savedBase, baseSide: true);
            Assert.That(await File.ReadAllTextAsync(savedBase), Is.EqualTo(initial));
            Field<TextBox>(dialog, "ComparisonFileFilter").Text = "λ"; await Idle(dialog);
            Assert.That(files.Items.OfType<GitRevisionFile>().Single().Path, Is.EqualTo(renamed));
            Field<TextBox>(dialog, "ComparisonFileFilter").Text = "deleted"; await Idle(dialog);
            Assert.That(Field<TabItem>(dialog, "ComparisonCompareTab").IsEnabled, Is.False);
            Assert.That(Field<TabItem>(dialog, "ComparisonBaseTab").IsEnabled, Is.True);
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task SwapAndMergeBaseCompareTheCorrectSideAndKeepTheOriginalAncestor()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string ancestor = await repository.ResolveRevisionAsync("HEAD");
        await repository.CreateBranchAsync(new GitCreateBranchOptions("side", Checkout: false));
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "main\n");
        await repository.StageAllAsync(); await repository.CommitAsync("Main change");
        await repository.CheckoutAsync("side");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "side.txt"), "side\n");
        await repository.StageAllAsync(); await repository.CommitAsync("Side change");
        await repository.CheckoutAsync("main");
        Assert.That(await repository.GetComparisonCommitCountsAsync(new("main"), new("side")), Is.EqualTo(new GitComparisonCommitCounts(1, 1)));
        CompareRevisionsDialog dialog = new(repository, "main", "side"); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Idle(dialog);
            Assert.That(dialog.MergeBaseRevision, Is.EqualTo(ancestor));
            Assert.That(dialog.Comparison!.Files.Select(file => file.Path), Is.EquivalentTo(new[] { "file.txt", "side.txt" }));
            Field<CheckBox>(dialog, "CompareToMergeBase").IsChecked = true; await Idle(dialog);
            Assert.That(dialog.Comparison!.Base.Revision, Is.EqualTo(ancestor));
            Assert.That(dialog.Comparison.Files.Single().Path, Is.EqualTo("side.txt"));
            Click(dialog, "ComparisonSwap"); await Idle(dialog);
            Assert.That(dialog.Comparison!.Files.Single().Path, Is.EqualTo("file.txt"));
            Assert.That(Field<FileHistoryContentView>(dialog, "ComparisonDiff").TextView.Text, Does.Contain("-base").And.Contain("+main"));
            Assert.That(dialog.MergeBaseRevision, Is.EqualTo(ancestor));
            Field<CheckBox>(dialog, "CompareToMergeBase").IsChecked = false; await Idle(dialog);
            Assert.That(dialog.Comparison!.Files.Single(file => file.Path == "side.txt").IsDeleted, Is.True);
            Assert.That(Field<TextBox>(dialog, "ComparisonBaseRevision").Text, Is.EqualTo("side"));
            Assert.That(Field<TextBox>(dialog, "ComparisonCompareRevision").Text, Is.EqualTo("main"));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task BranchAndCommitPickersUseRealLocalRemoteReferencesAndCommitCounts()
    {
        using Fixture fixture = await Fixture.CreateAsync("old\n", "old\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string initial = await repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "new\n"); await repository.StageAllAsync(); await repository.CommitAsync("Later picker commit");
        string latest = await repository.ResolveRevisionAsync("HEAD");
        await Git(fixture, "update-ref", "refs/remotes/origin/topic", initial);
        await Git(fixture, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/topic");
        CompareRevisionsDialog dialog = new(repository, initial, latest); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Click(dialog, "ComparisonBaseBranch");
            await Until(() => dialog.OwnedWindows.OfType<CompareBranchPicker>().Any());
            CompareBranchPicker picker = dialog.OwnedWindows.OfType<CompareBranchPicker>().Single();
            await Idle(picker);
            Assert.That(Field<RadioButton>(picker, "ComparePickRemote").IsChecked, Is.True);
            ComboBox branches = Field<ComboBox>(picker, "ComparePickBranch");
            Assert.That(branches.Items.OfType<GitReference>().Select(reference => reference.Name), Is.EqualTo(new[] { "refs/remotes/origin/topic" }));
            Field<RadioButton>(picker, "ComparePickLocal").IsChecked = true; await Idle(picker);
            branches.SelectedItem = branches.Items.OfType<GitReference>().Single(reference => reference.ShortName == "main"); await Idle(picker);
            Assert.That(Field<TextBlock>(picker, "ComparePickCounts").Text, Does.Contain("Compare: 1"));
            Click(picker, "ComparePickAccept"); await Until(() => !dialog.OwnedWindows.Contains(picker)); await Idle(dialog);
            Assert.That(dialog.Comparison!.Files, Is.Empty);
            Assert.That(dialog.Comparison.Base.Revision, Is.EqualTo(latest));
            Click(dialog, "ComparisonCompareCommit");
            await Until(() => dialog.OwnedWindows.OfType<ExportRevisionPicker>().Any());
            ExportRevisionPicker commits = dialog.OwnedWindows.OfType<ExportRevisionPicker>().Single();
            ListBox rows = Field<ListBox>(commits, "ChooseRevision");
            rows.SelectedItem = rows.Items.OfType<GitCommit>().Single(commit => commit.Hash == initial);
            Click(commits, "Choose"); await Until(() => !dialog.OwnedWindows.Contains(commits)); await Idle(dialog);
            Assert.That(dialog.Comparison!.Compare.Revision, Is.EqualTo(initial));
            Assert.That(Field<FileHistoryContentView>(dialog, "ComparisonDiff").TextView.Text, Does.Contain("-new").And.Contain("+old"));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task IndexAndWorkingDirectoryComparisonsPreserveDirectionAndIncludeLiteralUntrackedFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "staged\n"); await repository.StageAllAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "working\n");
        const string untracked = "untracked[*]\nλ.txt";
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, untracked), "literal untracked\n");
        GitCompareRevision head = new("HEAD");
        GitCompareRevision index = new(Kind: GitCompareRevisionKind.Index);
        GitCompareRevision worktree = new(Kind: GitCompareRevisionKind.WorkingTree);
        foreach ((GitCompareRevision from, GitCompareRevision to, string removed, string added) in new[]
        { (head, index, "base", "staged"), (index, head, "staged", "base"), (head, worktree, "base", "working"),
          (worktree, head, "working", "base"), (index, worktree, "staged", "working"), (worktree, index, "working", "staged") })
        {
            GitComparison comparison = await repository.GetComparisonAsync(from, to);
            string diff = await repository.GetComparisonFileDiffAsync(comparison, comparison.Files.Single(file => file.Path == "file.txt"));
            Assert.That(diff, Does.Contain("-" + removed).And.Contain("+" + added), from.Kind + " → " + to.Kind);
            Assert.That(comparison.UntrackedPaths.Contains(untracked), Is.EqualTo(from.Kind == GitCompareRevisionKind.WorkingTree || to.Kind == GitCompareRevisionKind.WorkingTree));
        }
        Assert.That((await repository.GetComparisonAsync(index, index)).Files, Is.Empty);
        Assert.That((await repository.GetComparisonAsync(worktree, worktree)).Files, Is.Empty);
        CompareRevisionsDialog dialog = new(repository, index, worktree); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            await Idle(dialog);
            Assert.That(dialog.Comparison!.Base.Kind, Is.EqualTo(GitCompareRevisionKind.Index));
            Field<ListBox>(dialog, "ComparisonFiles").SelectedItem = dialog.Comparison.Files.Single(file => file.Path == untracked); await Idle(dialog);
            Assert.That(Field<FileHistoryContentView>(dialog, "ComparisonDiff").TextView.Text, Does.Contain("+literal untracked"));
            Assert.That(Field<TabItem>(dialog, "ComparisonBaseTab").IsEnabled, Is.False);
            Click(dialog, "ComparisonSwap"); await Idle(dialog);
            Assert.That(Field<Button>(dialog, "ComparisonDirectoryTool").IsEnabled, Is.False);
            Field<ListBox>(dialog, "ComparisonFiles").SelectedItem = dialog.Comparison!.Files.Single(file => file.Path == untracked); await Idle(dialog);
            Assert.That(Field<FileHistoryContentView>(dialog, "ComparisonDiff").TextView.Text, Does.Contain("-literal untracked"));
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { dialog.Close(); }
        Assert.That(Encoding.UTF8.GetString(await repository.GetIndexFileBytesAsync("file.txt")), Is.EqualTo("staged\n"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "file.txt")), Is.EqualTo("working\n"));
    }

    [AvaloniaTest]
    public async Task WhitespaceContextEntireFileAndTreatAsTextUseTheOriginalDiffOptions()
    {
        string firstText = "a b\n" + string.Join('\n', Enumerable.Range(1, 40).Select(index => "context " + index)) + "\n";
        using Fixture fixture = await Fixture.CreateAsync(firstText, firstText);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string first = await repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), firstText.Replace("a b", "a  b  ", StringComparison.Ordinal));
        await repository.StageAllAsync(); await repository.CommitAsync("Whitespace change");
        CompareRevisionsDialog dialog = new(repository, first, "HEAD"); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            for (int i = 0; i < 3; i++) { Click(dialog, "ComparisonDecreaseContext"); await Idle(dialog); }
            Assert.That(Field<FileHistoryContentView>(dialog, "ComparisonDiff").TextView.Text, Does.Not.Contain("context 40"));
            Click(dialog, "ComparisonEntireFile"); await Idle(dialog);
            Assert.That(Field<Button>(dialog, "ComparisonIncreaseContext").IsEnabled, Is.False);
            Assert.That(Field<Button>(dialog, "ComparisonDecreaseContext").IsEnabled, Is.False);
            Assert.That(dialog.ReadDiffOptions().ContextLines, Is.Zero, "Whole-file mode preserves the selected context count.");
            Assert.That(Field<FileHistoryContentView>(dialog, "ComparisonDiff").TextView.Text, Does.Contain("context 40"));
            ChooseDiffMenu(dialog, "ComparisonWhitespace_Eol"); await Idle(dialog);
            Assert.That(Field<FileHistoryContentView>(dialog, "ComparisonDiff").TextView.Text, Does.Contain("a  b"));
            ChooseDiffMenu(dialog, "ComparisonWhitespace_Change"); await Idle(dialog);
            Assert.That(Field<FileHistoryContentView>(dialog, "ComparisonDiff").TextView.Text, Is.Empty);
            Assert.That(dialog.ReadDiffOptions().IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.Change));
            Assert.That(DiffMenu(dialog, "ComparisonWhitespace_Eol").IsChecked, Is.True);
            Assert.That(DiffMenu(dialog, "ComparisonWhitespace_Change").IsChecked, Is.True);
            Assert.That(DiffMenu(dialog, "ComparisonWhitespace_AllSpace").IsChecked, Is.False);
            dialog.RequestedThemeVariant = ThemeVariant.Dark;
            Capture(dialog, "classic-compare-revisions-dark.png");
            Assert.That(Field<Button>(dialog, "ComparisonEntireFile").Bounds.Height, Is.EqualTo(24));
            Assert.That(Field<Button>(dialog, "ComparisonEntireFile").Content, Is.TypeOf<Image>());
        }
        finally { dialog.Close(); }
        await File.WriteAllBytesAsync(Path.Combine(fixture.Repository, "binary.dat"), new byte[] { 1, 0, 2 });
        await repository.StageAllAsync(); await repository.CommitAsync("Binary base"); string binaryBase = await repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Repository, "binary.dat"), new byte[] { 1, 0, 3 });
        await repository.StageAllAsync(); await repository.CommitAsync("Binary compare");
        GitComparison binary = await repository.GetComparisonAsync(new(binaryBase), new("HEAD"));
        GitRevisionFile file = binary.Files.Single();
        Assert.That(await repository.GetComparisonFileDiffAsync(binary, file), Does.Contain("Binary files"));
        Assert.That(await repository.GetComparisonFileDiffAsync(binary, file, new(TreatAllFilesAsText: true)), Does.Contain("@@"));
        await ExpectAsync<ArgumentOutOfRangeException>(() => repository.GetComparisonFileDiffAsync(binary, file, new(ContextLines: -1)));
    }

    [AvaloniaTest]
    public async Task ComparisonViewsDecodeEachRevisionImageAndSaveItsOriginalBytes()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        const string path = "image λ[*].png";
        byte[] firstBytes = FileHistoryDialogTests.ImageBytes(2, 3, 0x19);
        byte[] secondBytes = FileHistoryDialogTests.ImageBytes(3, 2, 0xa1);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Repository, path), firstBytes); await repository.StageAllAsync(); await repository.CommitAsync("Image base");
        string first = await repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Repository, path), secondBytes); await repository.StageAllAsync(); await repository.CommitAsync("Image compare");
        CompareRevisionsDialog dialog = new(repository, first, "HEAD"); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            TabControl tabs = Field<TabControl>(dialog, "ComparisonTabs"); tabs.SelectedIndex = 1; await Idle(dialog);
            FileHistoryContentView baseView = Field<FileHistoryContentView>(dialog, "ComparisonBaseView");
            Assert.That(baseView.IsImage, Is.True);
            Assert.That(((Bitmap)baseView.GetVisualDescendants().OfType<Image>().Single().Source!).PixelSize, Is.EqualTo(new PixelSize(2, 3)));
            tabs.SelectedIndex = 2; await Idle(dialog);
            FileHistoryContentView compareView = Field<FileHistoryContentView>(dialog, "ComparisonCompareView");
            Assert.That(compareView.IsImage, Is.True);
            Assert.That(((Bitmap)compareView.GetVisualDescendants().OfType<Image>().Single().Source!).PixelSize, Is.EqualTo(new PixelSize(3, 2)));
            string saved = Output(fixture, "saved image.png"); await dialog.SaveSelectedFileAsync(saved);
            Assert.That(await File.ReadAllBytesAsync(saved), Is.EqualTo(secondBytes));
            string savedBase = Output(fixture, "saved base image.png"); await dialog.SaveSelectedFileAsync(savedBase, baseSide: true);
            Assert.That(await File.ReadAllBytesAsync(savedBase), Is.EqualTo(firstBytes));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task FindAndHunkNavigationUseTheOriginalKeyboardGesturesAndCrossFiles()
    {
        string first = string.Join('\n', Enumerable.Range(1, 45).Select(index => "line " + index)) + "\n";
        using Fixture fixture = await Fixture.CreateAsync(first, first);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string baseRevision = await repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), first.Replace("line 2\n", "change first\n", StringComparison.Ordinal).Replace("line 40\n", "change second\n", StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "z-next.txt"), "change in next file\n");
        await repository.StageAllAsync(); await repository.CommitAsync("Two hunks and another file");
        CompareRevisionsDialog dialog = new(repository, baseRevision, "HEAD"); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Field<ListBox>(dialog, "ComparisonFiles").SelectedItem = dialog.Comparison!.Files.Single(file => file.Path == "file.txt"); await Idle(dialog);
            DiffView view = Field<FileHistoryContentView>(dialog, "ComparisonDiff").TextView;
            view.Editor.Focus(); Press(dialog, PhysicalKey.F, RawInputModifiers.Control);
            Window find = dialog.OwnedWindows.Single(window => window.Name == "DiffViewerFindDialog");
            Assert.That(Field<TextBox>(find, "DiffViewerFindQuery").IsFocused, Is.True);
            Field<TextBox>(find, "DiffViewerFindQuery").Text = "change";
            Press(find, PhysicalKey.F3); Assert.That(view.Text.Substring(view.SelectionStart, view.SelectionLength), Is.EqualTo("change"));
            Click(find, "DiffViewerFindCancel");
            Assert.That(find.IsVisible, Is.False);
            Assert.That(view.Editor.TextArea.IsKeyboardFocusWithin, Is.True);
            int firstFound = view.SelectionStart; Press(dialog, PhysicalKey.F3); Assert.That(view.SelectionStart, Is.GreaterThan(firstFound));
            Press(dialog, PhysicalKey.F3, RawInputModifiers.Shift); Assert.That(view.SelectionStart, Is.EqualTo(firstFound));
            view.Select(0, 0); Press(dialog, PhysicalKey.ArrowDown, RawInputModifiers.Alt);
            Assert.That(view.Editor.Document.GetText(view.Editor.Document.GetLineByOffset(view.CaretOffset)), Does.StartWith("@@"));
            Press(dialog, PhysicalKey.ArrowDown, RawInputModifiers.Alt);
            int lastHunk = view.CaretOffset; Assert.That(lastHunk, Is.GreaterThan(0));
            Press(dialog, PhysicalKey.ArrowDown, RawInputModifiers.Alt); await Idle(dialog);
            Assert.That(dialog.SelectedFile!.Path, Is.EqualTo("z-next.txt"));
            Press(dialog, PhysicalKey.ArrowUp, RawInputModifiers.Alt); await Idle(dialog);
            Assert.That(dialog.SelectedFile!.Path, Is.EqualTo("file.txt"));
            Assert.That(view.CaretOffset, Is.EqualTo(lastHunk));
            Press(dialog, PhysicalKey.E, RawInputModifiers.Control); await Idle(dialog);
            Assert.That(dialog.ReadDiffOptions().ShowEntireFile, Is.True);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ConfiguredNativeDiffToolsReceiveOnlyTheSelectedRevisionsAndLiteralPaths()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("This integration scenario uses a local Linux test executable."); return; }
        using Fixture fixture = await Fixture.CreateAsync("old\n", "old\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        const string literal = ":tool[*]\nλ.txt";
        await Git(fixture, "mv", "--", "file.txt", literal); await repository.CommitAsync("Literal diff tool base");
        string first = await repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, literal), "new\n"); await repository.StageAllAsync(); await repository.CommitAsync("Diff tool target");
        string helper = Output(fixture, "fake native diff tool.py"); string marker = Output(fixture, "diff-tool.json");
        await File.WriteAllTextAsync(helper, "#!/usr/bin/python3\nimport json, pathlib, sys\n" +
            "left, right = map(pathlib.Path, sys.argv[1:])\n" +
            "def read(p):\n    return {str(f.relative_to(p)): f.read_text() for f in p.rglob('*') if f.is_file()} if p.is_dir() else {'file': p.read_text()}\n" +
            "pathlib.Path(" + JsonSerializer.Serialize(marker) + ").write_text(json.dumps({'left': read(left), 'right': read(right)}))\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Git(fixture, "config", "diff.guitool", "native-fixture");
        await Git(fixture, "config", "difftool.native-fixture.cmd", ShellQuote(helper) + " \"$LOCAL\" \"$REMOTE\"");
        CompareRevisionsDialog dialog = new(repository, first, "HEAD"); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Click(dialog, "ComparisonFileTool"); await Idle(dialog);
            using (JsonDocument output = JsonDocument.Parse(await File.ReadAllTextAsync(marker)))
            { Assert.That(output.RootElement.GetProperty("left").GetProperty("file").GetString(), Is.EqualTo("old\n")); Assert.That(output.RootElement.GetProperty("right").GetProperty("file").GetString(), Is.EqualTo("new\n")); }
            File.Delete(marker); Click(dialog, "ComparisonDirectoryTool"); await Idle(dialog);
            using (JsonDocument output = JsonDocument.Parse(await File.ReadAllTextAsync(marker)))
            { Assert.That(output.RootElement.GetProperty("left").GetProperty(literal).GetString(), Is.EqualTo("old\n")); Assert.That(output.RootElement.GetProperty("right").GetProperty(literal).GetString(), Is.EqualTo("new\n")); }
            Assert.That(dialog.HasRepositoryChanges, Is.True, "An external tool may edit local files, so the browser must refresh on close.");
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task InvalidRevisionsRemainEditableAndClosingCancelsARealGitProcess()
    {
        using Fixture fixture = await Fixture.CreateAsync("old\n", "old\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string first = await repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "new\n"); await repository.StageAllAsync(); await repository.CommitAsync("Cancellation target");
        CompareRevisionsDialog invalid = new(repository, "missing-revision", "HEAD"); invalid.Show();
        try
        {
            await invalid.InitializeAsync(); await Idle(invalid, expectSuccess: false);
            Assert.That(invalid.LastError, Is.Not.Null);
            Assert.That(invalid.IsVisible, Is.True);
            Field<TextBox>(invalid, "ComparisonBaseRevision").Text = first;
            Click(invalid, "CompareRevisions"); await Idle(invalid);
            Assert.That(invalid.Comparison!.Files.Single().Path, Is.EqualTo("file.txt"));
        }
        finally { invalid.Close(); }
        if (OperatingSystem.IsWindows()) { return; }
        (GitRepository slow, string started) = FileHistoryDialogTests.SlowRepository(fixture, "diff");
        CompareRevisionsDialog dialog = new(slow, first, "HEAD"); dialog.Show();
        Task load = dialog.InitializeAsync();
        await Until(() => File.Exists(started));
        Assert.That(Field<TextBox>(dialog, "ComparisonBaseRevision").IsEffectivelyEnabled, Is.False);
        Click(dialog, "CloseComparison");
        await load.WaitAsync(TimeSpan.FromSeconds(10)); await Until(() => !dialog.IsVisible);
        Assert.That(dialog.LastError, Is.EqualTo("Operation cancelled"));
        Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
    }

    [AvaloniaTest]
    public async Task UnrelatedAndBareRevisionsRemainReadableWithoutWorktreeActions()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string first = await repository.ResolveRevisionAsync("HEAD");
        await repository.CreateBranchAsync(new GitCreateBranchOptions("orphan", Orphan: true));
        await repository.CommitAsync("Unrelated root"); string second = await repository.ResolveRevisionAsync("HEAD");
        CompareRevisionsDialog unrelated = new(repository, first, second); unrelated.Show();
        try
        {
            await unrelated.InitializeAsync(); await Idle(unrelated);
            Assert.That(unrelated.MergeBaseRevision, Is.Null);
            Assert.That(Field<CheckBox>(unrelated, "CompareToMergeBase").IsEnabled, Is.False);
            Assert.That(unrelated.Comparison!.Files, Is.Empty);
        }
        finally { unrelated.Close(); }
        string bare = Output(fixture, "compare bare.git"); await Git(fixture, "clone", "--bare", fixture.Repository, bare);
        CompareRevisionsDialog dialog = new(await GitRepository.OpenAsync(bare), first, second); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Idle(dialog);
            Assert.That(Field<ComboBox>(dialog, "ComparisonBaseKind").Items.OfType<ComboBoxItem>().Skip(1).Select(item => item.IsEnabled), Is.All.False);
            Assert.That(Field<Button>(dialog, "ComparisonDirectoryTool").IsEnabled, Is.False);
            Assert.That(Field<Button>(dialog, "ComparisonFileTool").IsEnabled, Is.False);
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingTheBrowserCancelsBranchPickerLoadingAndItsGitProcess()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Linux process cancellation scenario."); return; }
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        (GitRepository repository, string started) = FileHistoryDialogTests.SlowRepository(fixture, "for-each-ref");
        Task<GitReference?> result = CompareBranchPicker.ShowAsync(fixture.Window, repository, new("HEAD"), null);
        await Until(() => File.Exists(started) && new FileInfo(started).Length > 0);
        CompareBranchPicker picker = fixture.Window.OwnedWindows.OfType<CompareBranchPicker>().Single();
        Assert.That(picker.Owner, Is.EqualTo(fixture.Window), "Git reads must begin with the modal owner already established.");
        Assert.That(picker.IsBusy, Is.True);
        int pid = int.Parse(await File.ReadAllTextAsync(started), System.Globalization.CultureInfo.InvariantCulture);
        fixture.Window.Close();
        Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(10)), Is.Null);
        Assert.That(picker.IsVisible, Is.False);
        Assert.That(picker.LastError, Is.EqualTo("Operation cancelled"));
        try { using Process process = Process.GetProcessById(pid); Assert.That(process.HasExited, Is.True, "Closing left the Git helper running."); }
        catch (ArgumentException) { }
    }

    private static T Field<T>(Window owner, string name) where T : Control
        => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static MenuItem DiffMenu(Window owner, string name)
        => Field<FileHistoryContentView>(owner, "ComparisonDiff").TextView.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static void ChooseDiffMenu(Window owner, string name)
    {
        MenuItem item = DiffMenu(owner, name);
        Assert.That(item.IsEnabled && item.IsVisible, Is.True, name + " must be available.");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }
    private static void Click(Window owner, string name)
    {
        Button button = Field<Button>(owner, name);
        Assert.That(button.IsEnabled && button.IsEffectivelyVisible, Is.True, name + " must be available.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Press(Window owner, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { owner.KeyPressQwerty(key, modifiers); if (owner.IsVisible) { owner.KeyReleaseQwerty(key, modifiers); } }
    private static async Task Idle(RepositoryDialog dialog, bool expectSuccess = true)
    { await Until(() => !dialog.IsBusy); if (expectSuccess) { Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString() ?? dialog.LastError); } }
    private static async Task Until(Func<bool> predicate)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); while (!predicate()) { await Task.Delay(20, timeout.Token); } }
    private static async Task<GitResult> Git(Fixture fixture, params string[] arguments)
    { GitResult result = await fixture.Git.RunAsync(fixture.Repository, ["--literal-pathspecs", .. arguments]); Assert.That(result.Success, Is.True, result.StandardError); return result; }
    private static string Output(Fixture fixture, string name) => Path.Combine(Path.GetDirectoryName(fixture.Repository)!, name);
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (T) { return; }
        Assert.Fail("Expected " + typeof(T).Name);
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using WriteableBitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string output = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame!.Save(output); TestContext.AddTestAttachment(output);
    }
}

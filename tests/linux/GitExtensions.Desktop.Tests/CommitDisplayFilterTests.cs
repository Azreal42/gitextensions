using System.Globalization;
using System.Text.RegularExpressions;
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
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class CommitDisplayFilterTests
{
    [AvaloniaTest]
    public async Task StageAllAndControlSUseEveryFilteredLogicalLeafIncludingCollapsedFolders()
    {
        const string first = "folder/first λ.cs", literal = "folder/nested/λ[1]; $(touch nope)\n?.cs", hidden = "folder/hidden.txt", indexed = "indexed.txt";
        using Fixture fixture = await Fixture.CreateAsync([first, literal, hidden, indexed]);
        await fixture.Repository.StageAsync([indexed]);
        string preserved = (await fixture.Git("diff", "--cached", "--", indexed)).StandardOutput;
        CommitDialog dialog = await fixture.OpenAsync();
        FileStatusListView unstaged = View(dialog, false);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await FilterAsync(dialog, false, @"^folder/[\s\S]*\.cs$");
            Assert.That(unstaged.FilteredEntries.Select(entry => entry.Path), Is.EquivalentTo(new[] { first, literal }));
            unstaged.SelectTags([unstaged.FilteredEntries.Single(entry => entry.Path == first).Tag]);
            Collapse(unstaged.Tree);
            await Idle(dialog);
            Assert.That(unstaged.FilteredEntries.Count, Is.EqualTo(2), "A collapsed folder still contains both logical leaves.");
            Assert.That(ToolTip.GetTip(Field<Button>(dialog, "StageAll")), Is.EqualTo("Stage filtered"));
            if (attempt == 0) { Click(dialog, "StageAll"); }
            else { Press(dialog, PhysicalKey.S, RawInputModifiers.Control); }
            await Idle(dialog);
            Assert.That(await fixture.IndexText(first), Is.EqualTo(Fixture.Changed));
            Assert.That(await fixture.IndexText(literal), Is.EqualTo(Fixture.Changed));
            Assert.That(await fixture.IndexText(hidden), Is.EqualTo(Fixture.Initial));
            Assert.That((await fixture.Git("diff", "--cached", "--", indexed)).StandardOutput, Is.EqualTo(preserved));
            Assert.That(unstaged.FilterText, Is.Empty);
            Assert.That(ToolTip.GetTip(Field<Button>(dialog, "StageAll")), Is.EqualTo("Stage all"));
            await fixture.Repository.UnstageAsync([first, literal]);
            await dialog.RefreshAsync();
        }
        Assert.That(File.Exists(Path.Combine(fixture.Repository.Path, "nope")), Is.False);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, hidden)), Is.EqualTo(Fixture.Changed));
    }

    [AvaloniaTest]
    public async Task OriginalNameFilterStagesCurrentRenameAndUnstagesBothNamesWithCollapsedAddedFiles()
    {
        const string old = "archive/old λ[1]\n.txt", renamed = "bundle/current λ; $(touch nope).txt", keep = "keep.txt", one = "bundle/added one.txt", two = "bundle/nested/added two.txt";
        using Fixture fixture = await Fixture.CreateAsync([old, keep]);
        Directory.CreateDirectory(Path.Combine(fixture.Repository.Path, "bundle"));
        await fixture.Git("mv", "--", old, renamed);
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository.Path, renamed), "renamed current content\n");
        await fixture.Repository.StageAsync([keep]);
        await fixture.Git("config", "color.ui", "always");
        string preserved = (await fixture.Git("diff", "--cached", "--", keep)).StandardOutput;
        CommitDialog dialog = await fixture.OpenAsync();
        await FilterAsync(dialog, false, "^" + Regex.Escape(old) + "$");
        FileStatusListEntry rename = View(dialog, false).FilteredEntries.Single();
        Assert.That(rename.Path, Is.EqualTo(renamed));
        Assert.That(rename.OriginalPath, Is.EqualTo(old));
        Assert.That(rename.SimilarityPercent, Is.EqualTo(((GitChange)rename.Tag).SimilarityPercent).And.Not.Null);
        Assert.That(Field<DiffView>(dialog, "SelectedDiff").Text, Does.Not.Contain('\u001b'));
        Assert.That(File.Exists(Path.Combine(fixture.Repository.Path, old)), Is.False);
        Click(dialog, "StageAll"); await Idle(dialog);
        FileStatusListEntry stagedRename = View(dialog, true).Entries.Single(entry => entry.Path == renamed);
        Assert.That(stagedRename.Status, Is.EqualTo('R'));
        Assert.That(stagedRename.SimilarityPercent, Is.InRange(1, 99), "The modified rename must retain Git's actual porcelain-v2 similarity score.");
        Assert.That(FileStatusListView.IconName(stagedRename), Is.EqualTo("FileStatusModified.png"));
        Assert.That(await fixture.IndexText(renamed), Is.EqualTo(Fixture.Changed + "renamed current content\n"));
        Assert.That(dialog.LastError, Is.Null, "Staging the absent old name would make Git reject this operation.");
        Assert.That(Field<DiffView>(dialog, "SelectedDiff").Text, Does.Not.Contain('\u001b'));
        await fixture.Write(one, "independent new file one\n");
        await fixture.Write(two, "independent new file two\n");
        await fixture.Repository.StageAsync([one, two]);
        await dialog.RefreshAsync();
        await FilterAsync(dialog, true, Regex.Escape(old) + "|^bundle/");
        FileStatusListView staged = View(dialog, true);
        Assert.That(staged.FilteredEntries.Select(entry => entry.Path), Is.EquivalentTo(new[] { renamed, one, two }));
        staged.SelectTags([staged.FilteredEntries.Single(entry => entry.Path == renamed).Tag]);
        Collapse(staged.Tree);
        await Idle(dialog);
        Click(dialog, "UnstageAll"); await Idle(dialog);
        Assert.That((await fixture.Git("diff", "--cached", "--", keep)).StandardOutput, Is.EqualTo(preserved));
        Assert.That((await fixture.Git("diff", "--cached", "--name-only", "-z")).StandardOutput, Is.EqualTo(keep + '\0'));
        Assert.That(await fixture.IndexText(old), Is.EqualTo(Fixture.Initial));
        GitResult missing = await new GitRunner().RunAsync(fixture.Repository.Path, ["show", ":" + renamed]);
        Assert.That(missing.Success, Is.False);
        Assert.That(File.Exists(Path.Combine(fixture.Repository.Path, old)), Is.False);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, renamed)), Is.EqualTo(Fixture.Changed + "renamed current content\n"));
        Assert.That(File.Exists(Path.Combine(fixture.Repository.Path, one)) && File.Exists(Path.Combine(fixture.Repository.Path, two)), Is.True);
        Assert.That(staged.FilterText, Is.Empty);
        Assert.That(ToolTip.GetTip(Field<Button>(dialog, "UnstageAll")), Is.EqualTo("Unstage all"));
        Assert.That(File.Exists(Path.Combine(fixture.Repository.Path, "nope")), Is.False);
    }

    [AvaloniaTest]
    public async Task DisplayAndSelectionFiltersRemainSeparateWhenSortModesRebuildTheRealTree()
    {
        const string selected = "src/deep/λ[1]\n.cs", other = "src/other.txt", hidden = "hidden.cs";
        using Fixture fixture = await Fixture.CreateAsync([selected, other, hidden]);
        CommitDialog dialog = await fixture.OpenAsync();
        await FilterAsync(dialog, false, "^src/");
        FileStatusListView list = View(dialog, false);
        ComboBox selection = Field<ComboBox>(dialog, "SelectionFilter");
        selection.Text = "^" + Regex.Escape(selected) + "$";
        await Until(() => selection.Items.OfType<string>().Contains(selection.Text!)); await Idle(dialog);
        Assert.That(list.Entries.Count, Is.EqualTo(3));
        Assert.That(list.FilteredEntries.Count, Is.EqualTo(2));
        Assert.That(list.SelectedTags.OfType<GitChange>().Single().Path, Is.EqualTo(selected));
        foreach (FileStatusListSortMode sort in Enum.GetValues<FileStatusListSortMode>())
        {
            list.SortMode = sort; await Idle(dialog);
            Assert.That(list.FilteredEntries.Select(entry => entry.Path), Is.EquivalentTo(new[] { selected, other }));
            Assert.That(list.SelectedTags.OfType<GitChange>().Single().Path, Is.EqualTo(selected));
            Assert.That(FileStatusTestHelpers.Rows(list.Tree).Where(item => item.Tag is GitChange).Count(), Is.EqualTo(2));
            Assert.That(list.FindItemByTag(list.SelectedTags.Single())!.Tag, Is.TypeOf<GitChange>());
        }
        selection.Text = "not-present";
        await Until(() => list.SelectedTags.Count == 0); await Idle(dialog);
        Assert.That(list.FilteredEntries.Count, Is.EqualTo(2));
        Assert.That(Field<Button>(dialog, "StageAll").IsEnabled, Is.True);
        Assert.That((await fixture.Git("diff", "--cached")).StandardOutput, Is.Empty);
    }

    [AvaloniaTest]
    public async Task FilteredEmptyConfirmationCancelPreservesStateThenStagesAndCommitsOnlyItsLogicalLeaves()
    {
        const string matching = "src/λ[1]\n.cs", hidden = "src/other.txt";
        using Fixture fixture = await Fixture.CreateAsync([matching, hidden]);
        CommitDialog dialog = await fixture.OpenAsync();
        await FilterAsync(dialog, false, "^" + Regex.Escape(matching) + "$");
        View(dialog, false).ClearSelection();
        Field<TextBox>(dialog, "Message").Text = "Only the displayed literal file";
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        Click(dialog, "Commit"); CommitEmptyDialog prompt = await EmptyPrompt(dialog);
        Assert.That(((TextBlock)Field<Button>(prompt, "StageUnstaged").Content!).Text, Is.EqualTo("Stage and commit the unstaged files that match your filter"));
        Assert.That(Field<Button>(prompt, "StageUnstaged").IsEnabled, Is.True);
        Click(prompt, "Cancel"); await Idle(dialog);
        Assert.That(View(dialog, false).FilterText, Is.EqualTo("^" + Regex.Escape(matching) + "$"));
        Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That((await fixture.Git("diff", "--cached")).StandardOutput, Is.Empty);
        Assert.That(Field<TextBox>(dialog, "Message").Text, Is.EqualTo("Only the displayed literal file"));
        Click(dialog, "Commit"); prompt = await EmptyPrompt(dialog); Click(prompt, "StageUnstaged"); await Idle(dialog);
        Assert.That(dialog.HasCommitted, Is.True);
        Assert.That((await fixture.Git("diff-tree", "--no-commit-id", "--name-only", "-z", "-r", "HEAD")).StandardOutput, Is.EqualTo(matching + '\0'));
        Assert.That(await fixture.IndexText(hidden), Is.EqualTo(Fixture.Initial));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, hidden)), Is.EqualTo(Fixture.Changed));
        Assert.That(View(dialog, false).FilterText, Is.Empty);
    }

    [AvaloniaTest]
    public async Task EmptyDisplayFilterDisablesItsStageAndCommitChoiceWithoutIndexingHiddenFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync(["one.txt", "two.txt"]);
        CommitDialog dialog = await fixture.OpenAsync();
        await FilterAsync(dialog, false, "not-present");
        Assert.That(View(dialog, false).FilteredEntries, Is.Empty);
        Field<TextBox>(dialog, "Message").Text = "No matching files";
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        Click(dialog, "Commit"); CommitEmptyDialog prompt = await EmptyPrompt(dialog);
        Assert.That(Field<Button>(prompt, "StageUnstaged").IsEnabled, Is.False);
        Assert.That(((TextBlock)Field<Button>(prompt, "StageUnstaged").Content!).Text, Does.Contain("match your filter"));
        Click(prompt, "Cancel"); await Idle(dialog);
        Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That((await fixture.Git("diff", "--cached")).StandardOutput, Is.Empty);
        Click(dialog, "StageAll"); await Idle(dialog);
        Assert.That((await fixture.Git("diff", "--cached")).StandardOutput, Is.Empty);
        Assert.That(View(dialog, false).FilterText, Is.Empty, "The original StageAll operation also clears a no-match filter.");
    }

    [AvaloniaTest]
    public async Task FreshIndexFlagsProtectStaleSelectedAndFilteredLeavesForBothButtonsAndControlS()
    {
        const string assumed = "protected/λ-assume\n.txt", skipped = "protected/skip[1].txt", allowed = "protected/allowed.txt";
        foreach (string action in new[] { "Stage", "StageAll", "CtrlS" })
        {
            using Fixture fixture = await Fixture.CreateAsync([assumed, skipped, allowed]);
            CommitDialog dialog = await fixture.OpenAsync();
            await FilterAsync(dialog, false, "^protected/");
            FileStatusListView list = View(dialog, false);
            list.SelectTags(list.FilteredEntries.Select(entry => entry.Tag)); await Idle(dialog);
            await fixture.Git("update-index", "--assume-unchanged", "--", assumed);
            await fixture.Git("update-index", "--skip-worktree", "--", skipped);
            string flags = (await fixture.Git("ls-files", "-v", "-z")).StandardOutput;
            Assert.That(list.FilteredEntries.Count, Is.EqualTo(3), "The flags changed after the actual list was loaded.");
            if (action == "CtrlS") { Press(dialog, PhysicalKey.S, RawInputModifiers.Control); }
            else { Click(dialog, action); }
            await Idle(dialog);
            Assert.That((await fixture.Git("diff", "--cached", "--name-only", "-z")).StandardOutput, Is.EqualTo(allowed + '\0'));
            Assert.That(await fixture.IndexText(assumed), Is.EqualTo(Fixture.Initial));
            Assert.That(await fixture.IndexText(skipped), Is.EqualTo(Fixture.Initial));
            Assert.That((await fixture.Git("ls-files", "-v", "-z")).StandardOutput, Is.EqualTo(flags));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, assumed)), Is.EqualTo(Fixture.Changed));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, skipped)), Is.EqualTo(Fixture.Changed));
            Assert.That(list.FilterText, Is.EqualTo(action == "Stage" ? "^protected/" : ""));
        }
    }

    [AvaloniaTest]
    public async Task FilteredUnstagingOnAnUnbornBranchKeepsEveryLiteralWorktreeFile()
    {
        const string first = "folder/λ[1]\n.txt", second = "folder/nested/second.txt", hidden = "hidden.txt";
        using Fixture fixture = await Fixture.CreateAsync([first, second, hidden], unborn: true);
        CommitDialog dialog = await fixture.OpenAsync();
        await FilterAsync(dialog, false, "^folder/");
        Collapse(View(dialog, false).Tree);
        Click(dialog, "StageAll"); await Idle(dialog);
        Assert.That((await fixture.Git("ls-files", "-z")).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries), Is.EquivalentTo(new[] { first, second }));
        await FilterAsync(dialog, true, "^folder/"); Collapse(View(dialog, true).Tree);
        Click(dialog, "UnstageAll"); await Idle(dialog);
        Assert.That((await fixture.Git("ls-files", "-z")).StandardOutput, Is.Empty);
        Assert.That((await fixture.Repository.GetStatusAsync()).IsUnborn, Is.True);
        foreach (string path in new[] { first, second, hidden }) { Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, path)), Is.EqualTo(Fixture.Changed)); }
        Assert.That(View(dialog, true).FilterText, Is.Empty);
    }

    [AvaloniaTest]
    public async Task RealPerfectRenameAndUnmergedEntriesUseTheOriginalSemanticIconsAndScores()
    {
        const string old = "old λ[1]\n.txt", renamed = "folder/renamed λ.txt", conflict = "conflict λ[1]\n.txt";
        using Fixture fixture = await Fixture.CreateAsync([old, conflict]);
        await fixture.Git("restore", "--worktree", "--", old, conflict);
        Directory.CreateDirectory(Path.Combine(fixture.Repository.Path, "folder"));
        await fixture.Git("mv", "--", old, renamed);
        CommitDialog dialog = await fixture.OpenAsync();
        FileStatusListEntry perfectRename = View(dialog, true).Entries.Single();
        Assert.That(perfectRename.Path, Is.EqualTo(renamed));
        Assert.That(perfectRename.OriginalPath, Is.EqualTo(old));
        Assert.That(perfectRename.Status, Is.EqualTo('R'));
        Assert.That(perfectRename.SimilarityPercent, Is.EqualTo(100));
        Assert.That(FileStatusListView.IconName(perfectRename), Is.EqualTo("FileStatusRenamed.png"));
        await fixture.Repository.CommitAsync(new GitCommitOptions("Perfect rename"));
        await fixture.Git("checkout", "-b", "topic");
        await fixture.Write(conflict, "topic conflict\n"); await fixture.Repository.StageAsync([conflict]);
        await fixture.Repository.CommitAsync(new GitCommitOptions("Topic"));
        await fixture.Git("checkout", "main");
        await fixture.Write(conflict, "main conflict\n"); await fixture.Repository.StageAsync([conflict]);
        await fixture.Repository.CommitAsync(new GitCommitOptions("Main"));
        GitResult merge = await new GitRunner().RunAsync(fixture.Repository.Path, ["merge", "--no-edit", "topic"]);
        Assert.That(merge.Success, Is.False);
        await dialog.RefreshAsync(); await Idle(dialog);
        FileStatusListEntry unmerged = View(dialog, false).Entries.Single();
        Assert.That(unmerged.Path, Is.EqualTo(conflict));
        Assert.That(((GitChange)unmerged.Tag).IsConflict, Is.True);
        Assert.That(unmerged.Status, Is.EqualTo('U'));
        Assert.That(unmerged.SimilarityPercent, Is.Null);
        Assert.That(FileStatusListView.IconName(unmerged), Is.EqualTo("Unmerged.png"));
        Assert.That(View(dialog, true).Entries, Is.Empty);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            dialog.RequestedThemeVariant = theme; dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            TreeViewItem leaf = View(dialog, false).FindItemByTag(unmerged.Tag)!;
            Image icon = ((Grid)leaf.Header!).Children.OfType<Image>().Single();
            Assert.That(icon.Bounds.Size, Is.EqualTo(new Size(16, 16)));
            Assert.That(icon.Source, Is.Not.Null);
        }
        Assert.That((await fixture.Git("ls-files", "--unmerged", "-z")).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length, Is.EqualTo(3));
        Assert.That(dialog.HasCommitted, Is.False);
    }

    [AvaloniaTest]
    public async Task FilteredOriginalIconsAndCompactTreesRenderInBothThemesWithoutBreakingLiteralCaptions()
    {
        const string source = "src/λ[1]\n.cs", other = "src/nested/second.cs", staged = "indexed/ready.txt";
        using Fixture fixture = await Fixture.CreateAsync([source, other, staged]);
        await fixture.Repository.StageAsync([staged]);
        CommitDialog dialog = await fixture.OpenAsync();
        await FilterAsync(dialog, false, "^src/"); await FilterAsync(dialog, true, "^indexed/");
        FileStatusTestHelpers.Select(View(dialog, false).Tree, source); await Idle(dialog);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            dialog.RequestedThemeVariant = theme; dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            foreach (bool isStaged in new[] { false, true })
            {
                TreeView tree = View(dialog, isStaged).Tree;
                foreach (TreeViewItem leaf in FileStatusTestHelpers.Rows(tree).Where(item => item.Tag is GitChange && item.IsEffectivelyVisible))
                {
                    Assert.That(leaf.Bounds.Height, Is.EqualTo(24));
                    Assert.That(leaf.Header, Is.TypeOf<Grid>());
                    Assert.That(((Grid)leaf.Header!).Children.OfType<Image>().Single().Width, Is.EqualTo(16));
                }
                Assert.That(Field<ComboBox>(dialog, isStaged ? "StagedFilter" : "UnstagedFilter").Bounds.Height, Is.EqualTo(24));
            }
            foreach (string name in new[] { "StageAll", "UnstageAll" })
            {
                Image icon = Field<Button>(dialog, name).GetVisualDescendants().OfType<Image>().Single();
                Assert.That(icon.Bounds.Size, Is.EqualTo(new Size(16, 16)));
                Assert.That(icon.Source, Is.Not.Null);
            }
            Assert.That(View(dialog, false).FilteredEntries.Single(entry => entry.Path == source).Tag, Is.TypeOf<GitChange>());
            using WriteableBitmap frame = Capture(dialog, "classic-commit-display-filter-" + theme + ".png");
            Assert.That(frame.PixelSize.Width, Is.EqualTo(1100));
        }
        Assert.That(await fixture.IndexText(source), Is.EqualTo(Fixture.Initial));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, source)), Is.EqualTo(Fixture.Changed));
    }

    private static FileStatusListView View(CommitDialog dialog, bool staged) => Field<FileStatusListView>(dialog, staged ? "StagedFileStatusList" : "UnstagedFileStatusList");
    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Collapse(TreeView tree) { foreach (TreeViewItem node in FileStatusTestHelpers.Rows(tree).Where(item => item.Items.Count > 0)) { node.IsExpanded = false; } }
    private static void Click(Window window, string name)
    {
        Button button = Field<Button>(window, name); Assert.That(button.IsEnabled, Is.True, name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); }
    private static async Task FilterAsync(CommitDialog dialog, bool staged, string expression)
    {
        FileStatusListView list = View(dialog, staged);
        TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Applied(object? sender, EventArgs args) { if (list.FilterText == expression) { changed.TrySetResult(); } }
        list.DisplayChanged += Applied;
        try
        {
            Field<ComboBox>(dialog, staged ? "StagedFilter" : "UnstagedFilter").Text = expression;
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { list.DisplayChanged -= Applied; }
        Assert.That(list.FilterError, Is.Null); await Idle(dialog);
    }
    private static async Task Idle(CommitDialog dialog)
    {
        await Task.Delay(30); Dispatcher.UIThread.RunJobs(); await Until(() => !dialog.IsBusy && dialog.OwnedWindows.Count == 0);
        Assert.That(dialog.LastError, Is.Null); dialog.UpdateLayout();
    }
    private static async Task Until(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate() && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        Assert.That(predicate(), Is.True, "The real commit workflow did not settle within 15 seconds.");
    }
    private static async Task<CommitEmptyDialog> EmptyPrompt(CommitDialog dialog)
    { await Until(() => dialog.OwnedWindows.OfType<CommitEmptyDialog>().Any()); return dialog.OwnedWindows.OfType<CommitEmptyDialog>().Single(); }
    private static WriteableBitmap Capture(Window window, string name)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new AssertionException("No commit frame.");
        string directory = Path.Combine(AppContext.BaseDirectory, "artifacts/linux/screenshots"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name); frame.Save(path); TestContext.AddTestAttachment(path); return frame;
    }

    private sealed class Fixture : IDisposable
    {
        public static readonly string Initial = string.Concat(Enumerable.Range(1, 40).Select(number => "stable line " + number + "\n"));
        public static readonly string Changed = Initial.Replace("stable line 20", "changed option λ🧪", StringComparison.Ordinal);
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly List<CommitDialog> _dialogs = [];
        public string Temporary { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-commit-display-" + Guid.NewGuid().ToString("N"));
        public GitRepository Repository { get; private set; } = null!;
        public CommitPreferencesStore Store { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync(string[] names, bool unborn = false)
        {
            Fixture fixture = new();
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(fixture.Temporary, "config"));
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
                string path = Path.Combine(fixture.Temporary, "repository"); Directory.CreateDirectory(path);
                GitResult init = await new GitRunner().RunAsync(path, ["init", "-b", "main"]); Assert.That(init.Success, Is.True, init.StandardError);
                fixture.Repository = new(path); fixture.Store = new();
                fixture.Store.Save(new CommitPreferences { CloseAfterEachCommit = false, CloseWhenAllChangesCommitted = false, SelectStagedOnMessageFocus = false });
                foreach (var (key, value) in new[] { ("user.name", "Display Filter Tester"), ("user.email", "display-filter@example.invalid"), ("commit.gpgsign", "false"), ("core.autocrlf", "false"), ("core.hooksPath", Path.Combine(fixture.Temporary, "no-hooks")) }) { await fixture.Repository.SetConfigAsync(key, value); }
                foreach (string name in names) { await fixture.Write(name, Initial); }
                if (!unborn) { await fixture.Repository.StageAsync(names); await fixture.Repository.CommitAsync(new GitCommitOptions("Initial commit")); }
                foreach (string name in names) { await fixture.Write(name, Changed); }
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        public async Task Write(string path, string content)
        {
            string file = Path.Combine(Repository.Path, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllTextAsync(file, content);
        }
        public Task<GitResult> Git(params string[] arguments) => Repository.RunAsync(arguments);
        public async Task<string> IndexText(string path) => (await Git("show", ":" + path)).StandardOutput;
        public async Task<CommitDialog> OpenAsync()
        {
            CommitDialog dialog = new(Repository, Store); _dialogs.Add(dialog); dialog.Show(); await dialog.InitializeAsync(); await Idle(dialog); return dialog;
        }
        public void Dispose()
        {
            foreach (CommitDialog dialog in _dialogs) { foreach (Window child in dialog.OwnedWindows.ToArray()) { child.Close(); } dialog.Close(); }
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); CultureInfo.CurrentUICulture = _culture;
            if (Directory.Exists(Temporary)) { Directory.Delete(Temporary, true); }
        }
    }
}

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ArtificialRevisionMenuTests
{
    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.WorkingTree)] [TestCase(GitCompareRevisionKind.Index)]
    public async Task ContextUsesOriginalActionsIconsAndReturnsToTheActualRowForItsFirstArrow(GitCompareRevisionKind kind)
    {
        await using Scope f = await Scope.OpenAsync(kind);
        ContextMenu menu = f.OpenContext();
        MenuItem[] visible = menu.Items.OfType<MenuItem>().Where(item => item.IsVisible).ToArray();
        Assert.That(visible.Take(2).Select(item => item.Name), Is.EqualTo(new[] { "RevisionResetChanges", "RevisionCommit" }));
        Assert.That(visible.Take(2).Select(item => (item.Icon as Image)?.Tag), Is.EqualTo(new[] { "ResetWorkingDirChanges.png", "RepoStateDirty.png" }));
        Assert.That(f.Command("RevisionCommit").InputGesture, Is.EqualTo(new KeyGesture(Key.Space, KeyModifiers.Control)));
        Assert.That(f.Command("RevisionCopy").IsVisible, Is.False);
        int index = f.Rows.Items.IndexOf(f.Row);
        menu.Close(); await f.IdleAsync();
        Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True);
        f.Main.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); f.Main.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        await f.IdleAsync(); Assert.That(f.Rows.SelectedItem, Is.SameAs(f.Rows.Items[index + 1]));
        Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.WorkingTree)] [TestCase(GitCompareRevisionKind.Index)]
    public async Task CommitUsesTheNormalDialogAndOpeningOrCancelingNeverStagesFiles(GitCompareRevisionKind kind)
    {
        await using Scope f = await Scope.OpenAsync(kind); string before = await f.SnapshotAsync();
        f.OpenContext(); PushProcessTestDriver.Pointer(f.Command("RevisionCommit"));
        await Until(() => f.Main.OwnedWindows.OfType<CommitDialog>().Any(window => window.IsVisible));
        CommitDialog dialog = f.Main.OwnedWindows.OfType<CommitDialog>().Single();
        Assert.That(dialog.GetVisualDescendants().OfType<TreeView>().Select(tree => tree.Name), Does.Contain("Staged").And.Contain("Unstaged"));
        Assert.That(await f.SnapshotAsync(), Is.EqualTo(before));
        dialog.Close(); await Until(() => !dialog.IsVisible && dialog.SpellingCloseCompletion.IsCompleted && dialog.GitHubIssueCompletion.IsCompleted);
        await f.IdleAsync(); Assert.That(await f.SnapshotAsync(), Is.EqualTo(before));
        Assert.That(f.Rows.SelectedItem, Is.SameAs(f.Row));
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.WorkingTree, false, false)] [TestCase(GitCompareRevisionKind.Index, false, false)]
    [TestCase(GitCompareRevisionKind.WorkingTree, false, true)] [TestCase(GitCompareRevisionKind.Index, false, true)]
    [TestCase(GitCompareRevisionKind.WorkingTree, true, false)] [TestCase(GitCompareRevisionKind.Index, true, false)]
    [TestCase(GitCompareRevisionKind.WorkingTree, true, true)] [TestCase(GitCompareRevisionKind.Index, true, true)]
    public async Task ResetConfirmationUsesFreshWholeWorktreeStatusIncludingIntentToAddAndCancelDoesNotMutate(
        GitCompareRevisionKind kind, bool existing, bool added)
    {
        await using Scope f = await Scope.OpenAsync(kind);
        // Change the repository after the grid snapshot: the confirmation must read fresh status.
        await f.Git.GitAsync("reset", "--hard", "--quiet", "--"); await f.Git.GitAsync("clean", "-d", "-f");
        if (existing) { await File.WriteAllTextAsync(f.Git.File, "LATEST WT\n"); }
        if (added)
        {
            await File.WriteAllTextAsync(f.Git.PathInRepository("intent.txt"), "INTENT\n");
            await f.Git.GitAsync("add", "-N", "--", "intent.txt");
            Assert.That((await f.Git.Repository.GetStatusAsync()).Changes.Single(change => change.Path == "intent.txt").WorkTreeStatus, Is.EqualTo('A'));
        }
        string before = await f.SnapshotAsync(); GitCommandLog.Clear();
        ResetChangesDialog dialog = await f.ResetDialogAsync();
        CheckBox delete = Field<CheckBox>(dialog, "ResetDeleteNewFiles");
        Assert.That(delete.IsChecked, Is.EqualTo(!existing)); Assert.That(delete.IsEnabled, Is.EqualTo(existing && added));
        Button cancel = Field<Button>(dialog, "CancelResetChanges"); Assert.That(cancel.IsDefault, Is.True);
        PushProcessTestDriver.Pointer(cancel); await f.IdleAsync();
        Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Command.StartsWith("git reset", StringComparison.Ordinal)
            || entry.Command.StartsWith("git checkout", StringComparison.Ordinal) || entry.Command.StartsWith("git clean", StringComparison.Ordinal)), Is.False);
        Assert.That(await f.SnapshotAsync(), Is.EqualTo(before)); Assert.That(f.Rows.SelectedItem, Is.SameAs(f.Row));
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.WorkingTree, false)] [TestCase(GitCompareRevisionKind.WorkingTree, true)]
    [TestCase(GitCompareRevisionKind.Index, false)] [TestCase(GitCompareRevisionKind.Index, true)]
    public async Task ConfirmedResetUsesTheWholeRepositoryAndTheChosenScopeAndKeepsIgnoredFiles(GitCompareRevisionKind kind, bool deleteNew)
    {
        await using Scope f = await Scope.OpenAsync(kind); object source = f.Rows.ItemsSource!;
        string head = await f.Git.Repository.ResolveRevisionAsync("HEAD");
        ResetChangesDialog dialog = await f.ResetDialogAsync(); CheckBox delete = Field<CheckBox>(dialog, "ResetDeleteNewFiles");
        Assert.That(delete.IsChecked, Is.False); Assert.That(delete.IsEnabled, Is.True);
        if (deleteNew) { PushProcessTestDriver.Pointer(delete); }
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "ConfirmResetChanges")); await f.IdleAsync();
        bool worktree = kind == GitCompareRevisionKind.WorkingTree;
        Assert.That(await File.ReadAllTextAsync(f.Git.File), Is.EqualTo(worktree ? "INDEX FILE\n" : "base\n"));
        Assert.That(await File.ReadAllTextAsync(f.Git.PathInRepository("other.txt")), Is.EqualTo(worktree ? "INDEX OTHER\n" : "BASE OTHER\n"));
        Assert.That((await f.Git.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo(worktree ? "INDEX FILE\n" : "base\n"));
        Assert.That(File.Exists(f.Git.PathInRepository("new-dir/new.txt")), Is.EqualTo(!deleteNew));
        Assert.That(await File.ReadAllTextAsync(f.Git.PathInRepository("ignored.txt")), Is.EqualTo("KEEP IGNORED\n"));
        Assert.That(await f.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That(f.Rows.ItemsSource, Is.SameAs(source)); Assert.That(f.Rows.SelectedItem, Is.SameAs(f.Row));
        Assert.That(f.Main.LastException, Is.Null, f.Main.LastException?.ToString());
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.WorkingTree)] [TestCase(GitCompareRevisionKind.Index)]
    public async Task AContextOnTheLatestShiftCaretKeepsComparisonOrderWhileItsResetTargetsTheTypedRow(GitCompareRevisionKind kind)
    {
        await using Scope f = await Scope.OpenAsync(kind);
        ListBoxItem head = (ListBoxItem)f.Rows.ContainerFromItem(f.Rows.Items.OfType<GitCommit>().First())!;
        PushProcessTestDriver.Pointer(head); await f.IdleAsync();
        f.SelectRow(RawInputModifiers.Shift); await f.IdleAsync();
        object[] selected = f.Rows.SelectedItems!.Cast<object>().ToArray();
        string patch = Field<DiffView>(f.Main, "CommitDiff").CanonicalText; string before = await f.SnapshotAsync();
        Assert.That(selected.OfType<GitCommit>().Count(), Is.EqualTo(1));
        Assert.That(patch, Does.Contain(kind == GitCompareRevisionKind.Index ? "-INDEX FILE" : "-WT FILE"));
        GitCommandLog.Clear(); f.OpenContext();
        Assert.That(f.Command("RevisionResetChanges").IsVisible, Is.True); Assert.That(f.Command("RevisionCommit").IsVisible, Is.True);
        Assert.That(f.Rows.SelectedItems!.Cast<object>(), Is.EquivalentTo(selected));
        Assert.That(Field<DiffView>(f.Main, "CommitDiff").CanonicalText, Is.EqualTo(patch));
        f.Rows.ContextMenu!.Close(); await f.IdleAsync();
        Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Command == "git diff"), Is.False, "Opening the menu on the latest selected caret must not reverse/reload its comparison.");
        ResetChangesDialog dialog = await f.ResetDialogAsync();
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelResetChanges")); await f.IdleAsync();
        Assert.That(await f.SnapshotAsync(), Is.EqualTo(before));
        Assert.That(Field<DiffView>(f.Main, "CommitDiff").CanonicalText, Is.EqualTo(patch));
        Assert.That(f.Rows.SelectedItems!.Cast<object>(), Is.EquivalentTo(selected));
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.WorkingTree)] [TestCase(GitCompareRevisionKind.Index)]
    public async Task AContextOnANonLatestSelectedMemberCollapsesToThatNativeTarget(GitCompareRevisionKind kind)
    {
        await using Scope f = await Scope.OpenAsync(kind);
        ListBoxItem real = (ListBoxItem)f.Rows.ContainerFromItem(f.Rows.Items.OfType<GitCommit>().First())!;
        Point point = real.TranslatePoint(new Point(100, real.Bounds.Height / 2), f.Main)!.Value;
        f.Main.MouseDown(point, MouseButton.Left, RawInputModifiers.Control); f.Main.MouseUp(point, MouseButton.Left, RawInputModifiers.Control);
        await f.IdleAsync(); Assert.That(f.Rows.SelectedItems, Has.Count.EqualTo(2));
        f.OpenContext(); Assert.That(f.Rows.SelectedItems, Has.Count.EqualTo(1)); Assert.That(f.Rows.SelectedItem, Is.SameAs(f.Row));
        Assert.That(f.Command("RevisionResetChanges").IsVisible, Is.True);
        f.Rows.ContextMenu!.Close();
        await Until(() => Field<DiffView>(f.Main, "CommitDiff").CanonicalText.Contains(kind == GitCompareRevisionKind.Index ? "+INDEX FILE" : "+WT FILE", StringComparison.Ordinal));
        await f.IdleAsync(); Assert.That(f.Rows.IsKeyboardFocusWithin, Is.True);
    }

    private sealed class Scope(GitFixture git, MainWindow main, ArtificialHistoryRevision row) : IAsyncDisposable
    {
        internal GitFixture Git => git;
        internal MainWindow Main => main;
        internal ListBox Rows => Field<ListBox>(main, "RevisionHistory");
        internal ArtificialHistoryRevision Row => row;
        internal static async Task<Scope> OpenAsync(GitCompareRevisionKind kind)
        {
            GitFixture git = await GitFixture.CreateAsync(); MainWindow? main = null;
            try
            {
                await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "BASE OTHER\n");
                await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Other file");
                await File.WriteAllTextAsync(git.File, "INDEX FILE\n"); await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "INDEX OTHER\n");
                await git.Repository.StageAllAsync();
                await File.WriteAllTextAsync(git.File, "WT FILE\n"); await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "WT OTHER\n");
                Directory.CreateDirectory(git.PathInRepository("new-dir")); await File.WriteAllTextAsync(git.PathInRepository("new-dir/new.txt"), "NEW\n");
                await File.WriteAllTextAsync(git.PathInRepository(".git/info/exclude"), "ignored.txt\n");
                await File.WriteAllTextAsync(git.PathInRepository("ignored.txt"), "KEEP IGNORED\n");
                main = new MainWindow(new BrowseRequest(git.Repository.Path, new("file.txt", BrowsePathKind.File), new("", kind))) { Width = 1440, Height = 900 };
                main.Show(); await Until(() => !main.IsBusy && main.WindowWorkCompletion.IsCompleted); Pump(main);
                ListBox rows = Field<ListBox>(main, "RevisionHistory"); ArtificialHistoryRevision row = rows.Items.OfType<ArtificialHistoryRevision>().Single(item => item.Kind == kind);
                return new(git, main, row);
            }
            catch { if (main is not null) { main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted); } git.Dispose(); throw; }
        }
        internal void SelectRow(RawInputModifiers modifiers)
        {
            Rows.ScrollIntoView(row); Pump(main); ListBoxItem target = (ListBoxItem)Rows.ContainerFromItem(row)!;
            PushProcessTestDriver.Pointer(target, MouseButton.Left, modifiers, new Point(100, target.Bounds.Height / 2)); Pump(main);
        }
        internal ContextMenu OpenContext()
        {
            Rows.ScrollIntoView(row); Pump(main); ListBoxItem target = (ListBoxItem)Rows.ContainerFromItem(row)!;
            PushProcessTestDriver.Pointer(target, MouseButton.Right, localPoint: new Point(100, target.Bounds.Height / 2)); Pump(main);
            Assert.That(Rows.ContextMenu!.IsOpen, Is.True); return Rows.ContextMenu;
        }
        internal MenuItem Command(string name) => Rows.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == name);
        internal async Task<ResetChangesDialog> ResetDialogAsync()
        {
            OpenContext(); PushProcessTestDriver.Pointer(Command("RevisionResetChanges"));
            await Until(() => main.OwnedWindows.OfType<ResetChangesDialog>().Any(dialog => dialog.IsVisible));
            ResetChangesDialog dialog = main.OwnedWindows.OfType<ResetChangesDialog>().Single(); Pump(dialog); return dialog;
        }
        internal async Task<string> SnapshotAsync() => (await git.GitAsync("status", "--porcelain=v2", "--untracked-files=all")).StandardOutput
            + (await git.GitAsync("ls-files", "--stage")).StandardOutput + await File.ReadAllTextAsync(git.File)
            + (File.Exists(git.PathInRepository("other.txt")) ? await File.ReadAllTextAsync(git.PathInRepository("other.txt")) : "")
            + (await git.GitAsync("show-ref", "--head")).StandardOutput;
        internal async Task IdleAsync() { await Until(() => !main.IsBusy && main.WindowWorkCompletion.IsCompleted); Pump(main); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); }
        public async ValueTask DisposeAsync()
        {
            foreach (Window child in main.OwnedWindows.ToArray()) { child.Close(); }
            main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted); await main.WindowWorkCompletion; git.Dispose();
        }
    }
    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(30)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs(); Assert.That(condition(), Is.True, "The actual artificial context workflow did not drain.");
    }
}

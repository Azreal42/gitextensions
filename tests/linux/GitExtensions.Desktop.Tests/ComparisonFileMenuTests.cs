using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;
using RepoFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ComparisonFileMenuTests
{
    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ARealStageClickUsesTheLiteralIndexWorktreePairAndKeepsRegexLettersAsInput(bool dark)
    {
        using RepoFixture git = await RepoFixture.CreateAsync(); const string path = ":stage[1]* λ\tname\n.txt";
        await File.WriteAllTextAsync(git.PathInRepository(path), "old\n"); await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Add literal");
        await File.WriteAllTextAsync(git.PathInRepository(path), "selected staged value\n"); await File.WriteAllTextAsync(git.File, "other stays unstaged\n");
        await using Host f = await Host.ShowAsync(git, new(Kind: GitCompareRevisionKind.Index), new(Kind: GitCompareRevisionKind.WorkingTree), dark);
        Select(f, path); await Idle(f.Dialog); ContextMenu menu = OpenMenu(f);
        Assert.That(Item(menu, "tsmiStageFile").IsEnabled, Is.True);
        Assert.That(menu.Items.OfType<MenuItem>().Any(item => item.Name is "tsmiFilterFileInGrid" or "tsmiShowInFileTree" or "tsmiOpenFindInCommitFilesGitGrepDialog"), Is.False);
        Capture(f.Dialog, "comparison-file-menu-index-worktree-" + (dark ? "dark" : "light") + ".png");
        PushProcessTestDriver.Pointer(Item(menu, "tsmiStageFile"));
        await Until(() => !f.Dialog.IsBusy && f.Dialog.HasRepositoryChanges && f.Dialog.Comparison!.Files.All(file => file.Path != path)); await Idle(f.Dialog);
        Assert.That(System.Text.Encoding.UTF8.GetString(await git.Repository.GetIndexFileBytesAsync(path)), Is.EqualTo("selected staged value\n"));
        Assert.That(System.Text.Encoding.UTF8.GetString(await git.Repository.GetIndexFileBytesAsync("file.txt")), Is.EqualTo("base\n"));
        TextBox editor = Field<ComboBox>(f.Dialog, "ComparisonFilesFilter").GetVisualDescendants().OfType<TextBox>().Single();
        PushProcessTestDriver.Pointer(editor); f.Dialog.KeyTextInput("s"); await Until(() => f.View.FilterText == "s");
        Assert.That(f.Dialog.FocusManager!.GetFocusedElement(), Is.SameAs(editor)); Assert.That(f.Dialog.LastError, Is.Null);
        Assert.That((await git.GitAsync("diff", "--", "file.txt")).StandardOutput, Does.Contain("other stays unstaged"));
    }

    [AvaloniaTest]
    public async Task ResetSecondUsesTheSelectedBaseToAGroupAndCancelLeavesAllGitStateUnchanged()
    {
        using RepoFixture git = await RepoFixture.CreateAsync();
        await File.WriteAllTextAsync(git.PathInRepository("shared.txt"), "BASE\n"); await File.WriteAllTextAsync(git.PathInRepository("untouched.txt"), "keep\n");
        await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Common"); string common = await git.Repository.ResolveRevisionAsync("HEAD");
        await git.GitAsync("checkout", "-b", "side-a"); await File.WriteAllTextAsync(git.PathInRepository("shared.txt"), "value A\n"); await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("A"); string a = await git.Repository.ResolveRevisionAsync("HEAD");
        await git.GitAsync("checkout", "-b", "side-b", common); await File.WriteAllTextAsync(git.PathInRepository("shared.txt"), "value B\n"); await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("B"); string b = await git.Repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(git.PathInRepository("untouched.txt"), "dirty unrelated\n");
        await using Host f = await Host.ShowAsync(git, new(a), new(b));
        ComparisonFileSelection leaf = f.View.Entries.Select(entry => entry.Tag).OfType<ComparisonFileSelection>().Single(item => item.Group.Kind == GitRevisionDiffGroupKind.BaseToFirst && item.File.Path == "shared.txt");
        f.View.SelectTags([leaf]); await Idle(f.Dialog); PushProcessTestDriver.Pointer(Row(f.View.FindItemByTag(leaf)!));
        string before = (await git.GitAsync("status", "--porcelain=v1")).StandardOutput;
        await ResetSecond(f); ResetChangesDialog cancel = f.Dialog.OwnedWindows.OfType<ResetChangesDialog>().Single();
        PushProcessTestDriver.Pointer(Field<Button>(cancel, "CancelResetChanges")); await Idle(f.Dialog);
        Assert.That(await File.ReadAllTextAsync(git.PathInRepository("shared.txt")), Is.EqualTo("value B\n"));
        Assert.That((await git.GitAsync("status", "--porcelain=v1")).StandardOutput, Is.EqualTo(before));
        leaf = f.View.Entries.Select(entry => entry.Tag).OfType<ComparisonFileSelection>().Single(item => item.Group.Kind == GitRevisionDiffGroupKind.BaseToFirst && item.File.Path == "shared.txt");
        f.View.SelectTags([leaf]); await Idle(f.Dialog); PushProcessTestDriver.Pointer(Row(f.View.FindItemByTag(leaf)!));
        await ResetSecond(f); ResetChangesDialog confirm = f.Dialog.OwnedWindows.OfType<ResetChangesDialog>().Single();
        Assert.That(Field<TextBox>(confirm, "ResetChangesMessage").Text, Does.Contain("Second: B"));
        PushProcessTestDriver.Pointer(Field<Button>(confirm, "ConfirmResetChanges")); await Idle(f.Dialog);
        Assert.That(await File.ReadAllTextAsync(git.PathInRepository("shared.txt")), Is.EqualTo("value A\n"), "The menu must reset to the selected BASE→A group's A, not the form's B.");
        Assert.That(System.Text.Encoding.UTF8.GetString(await git.Repository.GetIndexFileBytesAsync("shared.txt")), Is.EqualTo("value A\n"));
        Assert.That(await File.ReadAllTextAsync(git.PathInRepository("untouched.txt")), Is.EqualTo("dirty unrelated\n"));
        Assert.That(f.Dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    public async Task FirstCtrlASelectsOnlyTheFirstGroupWhileCtrlAInTheRegexEditorSelectsItsText()
    {
        using RepoFixture git = await RepoFixture.CreateAsync();
        await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "old\n"); await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Before"); string before = await git.Repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(git.PathInRepository("other.txt"), "new\n"); await File.WriteAllTextAsync(git.File, "new file\n"); await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("After");
        await using Host f = await Host.ShowAsync(git, new(before), new("HEAD")); Select(f, "file.txt"); await Idle(f.Dialog);
        Press(f.Dialog, PhysicalKey.A, RawInputModifiers.Control);
        Assert.That(f.View.Tree.SelectedItems!.Count, Is.EqualTo(2));
        Assert.That(f.View.Tree.SelectedItems.OfType<TreeViewItem>().All(item => item.Tag is ComparisonFileSelection), Is.True);
        TextBox editor = Field<ComboBox>(f.Dialog, "ComparisonFilesFilter").GetVisualDescendants().OfType<TextBox>().Single();
        PushProcessTestDriver.Pointer(editor); f.Dialog.KeyTextInput("txt"); Press(f.Dialog, PhysicalKey.A, RawInputModifiers.Control);
        Assert.That(editor.SelectedText, Is.EqualTo("txt")); Assert.That(f.Dialog.FocusManager!.GetFocusedElement(), Is.SameAs(editor));
    }

    private static async Task ResetSecond(Host f)
    {
        ContextMenu menu = OpenMenu(f); MenuItem root = Item(menu, "tsmiResetFileTo");
        Point point = root.TranslatePoint(new Point(root.Bounds.Width * .7, root.Bounds.Height / 2), TopLevel.GetTopLevel(root)!)!.Value;
        TopLevel.GetTopLevel(root)!.MouseMove(point);
        await Until(() => root.IsSubMenuOpen && root.Items.OfType<MenuItem>().First().Bounds.Height > 0);
        PushProcessTestDriver.Pointer(root.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiResetFileToSelected"));
        await Until(() => f.Dialog.OwnedWindows.OfType<ResetChangesDialog>().Any());
    }
    private static void Select(Host f, string path)
    { object tag = f.View.Entries.Single(entry => entry.Path == path).Tag; f.View.SelectTags([tag]); Pump(f.Dialog); PushProcessTestDriver.Pointer(Row(f.View.FindItemByTag(tag)!)); }
    private static ContextMenu OpenMenu(Host f)
    {
        Control row = Row((TreeViewItem)f.View.Tree.SelectedItem!); Pump(f.Dialog);
        Point point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), f.Dialog)!.Value;
        Assert.That((f.Dialog.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True);
        f.Dialog.MouseMove(point); f.Dialog.MouseDown(point, MouseButton.Right); f.Dialog.MouseUp(point, MouseButton.Right); Pump(f.Dialog);
        ContextMenu menu = f.View.Tree.ContextMenu!; Assert.That(menu.IsOpen, Is.True); return menu;
    }
    private static MenuItem Item(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static Control Row(TreeViewItem row) => row.GetVisualDescendants().OfType<Control>().Single(item => item.Name == "PART_Header" && item.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() == row);
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static async Task Idle(CompareRevisionsDialog dialog) { await Until(() => !dialog.IsBusy && dialog.SelectionCompletion.IsCompleted); Pump(dialog); }
    private static async Task Until(Func<bool> condition) { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True); }
    private static void Pump(TopLevel root) { Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) { root.KeyPressQwerty(key, modifiers); if (root.IsVisible) { root.KeyReleaseQwerty(key, modifiers); } Pump(root); }
    private static void Capture(Window owner, string name) { Pump(owner); using var frame = owner.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string folder = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(folder); string file = Path.Combine(folder, name); frame!.Save(file); TestContext.AddTestAttachment(file); }
    private sealed class Host : IAsyncDisposable
    {
        private readonly Window _owner = new() { Width = 1300, Height = 850 };
        internal CompareRevisionsDialog Dialog { get; }
        internal FileStatusListView View => Field<FileStatusListView>(Dialog, "ComparisonFileStatusList");
        private readonly Task _shown;
        private Host(RepoFixture git, GitCompareRevision first, GitCompareRevision second, bool dark)
        { _owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; _owner.Show(); Dialog = new(git.Repository, first, second, new FileStatusListPreferencesStore(git.Output("menu-prefs"))) { RequestedThemeVariant = _owner.ActualThemeVariant }; _shown = Dialog.ShowDialog(_owner); }
        internal static async Task<Host> ShowAsync(RepoFixture git, GitCompareRevision first, GitCompareRevision second, bool dark = false)
        { Host f = new(git, first, second, dark); await f.Dialog.InitializeAsync(); await Idle(f.Dialog); Assert.That(f.Dialog.LastError, Is.Null); return f; }
        public async ValueTask DisposeAsync() { foreach (Window child in Dialog.OwnedWindows.ToArray()) { child.Close(); } Dialog.Close(); await Dialog.SelectionCompletion; await _shown; _owner.Close(); }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
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
public sealed class RevisionContextMenuTests
{
    [AvaloniaTest]
    public async Task WindowsReferenceMenuRendersCompactRowsIconsShortcutsAndSubmenusInBothThemes()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        Assert.That((await fixture.Git.RunAsync(fixture.Repository, ["branch", "context-target", "HEAD"])).Success, Is.True); await fixture.Window.OpenRepositoryAsync(fixture.Repository);
        MainWindow main = fixture.Window; ListBox history = History(main); ContextMenu menu = history.ContextMenu!;
        string[] expected = ["Copy to clipboard", "|", "Checkout branch...", "Rebase current branch on", "Reset current branch to here...", "|",
            "Create new branch here...", "Rename branch...", "Delete branch...", "Reset another branch to here...", "|", "Create new tag here...", "|",
            "Checkout this commit...", "Revert this commit...", "Cherry pick this commit...", "Archive this commit...", "Advanced", "|", "Compare", "|", "Navigate", "View"];
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme; Pump(main); menu.Open(history); Pump(main);
            Assert.That(menu.Items.Cast<object>().Select(item => item is Separator ? "|" : Caption((MenuItem)item)), Is.EqualTo(expected));
            MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.That(items.All(item => item.Icon is Image { Source: not null, Width: 16, Height: 16 }), Is.True);
            Assert.That(items.All(item => Math.Abs(item.Bounds.Height - 22) < .1), Is.True, "Original rows are 22px, including groups with shortcuts.");
            Assert.That(Item(menu, "RevisionCreateBranch").InputGesture, Is.EqualTo(new KeyGesture(Key.B, KeyModifiers.Control)));
            Assert.That(Item(menu, "RevisionCreateTag").InputGesture, Is.EqualTo(new KeyGesture(Key.T, KeyModifiers.Control)));
            Assert.That(items.Where(item => item.Items.Count > 0).Select(item => item.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                .Single(path => path.Name == "PART_ChevronPath").TranslatePoint(default, menu)!.Value.X).Distinct().Count(), Is.EqualTo(1));
            Capture(TopLevel.GetTopLevel(items[0])!, "revision-context-" + theme + ".png");
            using (RenderTargetBitmap renderedMenu = new(new PixelSize((int)Math.Ceiling(menu.Bounds.Width), (int)Math.Ceiling(menu.Bounds.Height))))
            {
                renderedMenu.Render(menu);
                string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "revision-context-menu-only-" + theme + ".png");
                renderedMenu.Save(path); TestContext.AddTestAttachment(path);
            }
            MenuItem rebase = Item(menu, "HistoryRebaseMenu"); rebase.IsSubMenuOpen = true; Pump(main);
            Assert.That(rebase.Items.OfType<MenuItem>().All(item => Math.Abs(item.Bounds.Height - 22) < .1), Is.True);
            Capture(TopLevel.GetTopLevel(rebase.Items.OfType<MenuItem>().First())!, "revision-context-rebase-" + theme + ".png");
            menu.Close(); await Idle(main);
        }
    }

    [AvaloniaTest]
    public async Task BranchActionsTargetTheClickedCommitRatherThanTheSidebarAndCancellingDoesNotMutateGit()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = fixture.Window; ListBox history = History(main);
        GitCommit target = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Feature branch change");
        history.SelectedItem = target; await Idle(main);
        string head = (await fixture.Git.RunAsync(fixture.Repository, ["rev-parse", "HEAD"])).StandardOutput;
        ContextMenu menu = history.ContextMenu!; menu.Open(history); Pump(main);
        MenuItem rename = Item(menu, "RevisionRenameBranch");
        Assert.That(rename.Items.OfType<MenuItem>().Select(item => ((GitReference)item.Tag!).ShortName), Is.EqualTo(new[] { "feature" }));
        Assert.That(Item(menu, "RevisionCheckoutBranch").Items.OfType<MenuItem>().Select(item => ((GitReference)item.Tag!).ShortName), Is.EqualTo(new[] { "feature" }));
        Click(rename.Items.OfType<MenuItem>().Single());
        await Until(() => main.OwnedWindows.OfType<RenameBranchDialog>().Any(dialog => !dialog.IsBusy));
        RenameBranchDialog dialog = main.OwnedWindows.OfType<RenameBranchDialog>().Single();
        Assert.That(dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "RenameBranchName").Text, Is.EqualTo("feature"));
        dialog.Close(); menu.Close(); await Idle(main);
        Assert.That((await fixture.Git.RunAsync(fixture.Repository, ["rev-parse", "HEAD"])).StandardOutput, Is.EqualTo(head));
        Assert.That((await fixture.Git.RunAsync(fixture.Repository, ["branch", "--list", "feature"])).StandardOutput, Does.Contain("feature"));
    }

    [AvaloniaTest]
    public async Task ClipboardUsesActualSelectedHashesAuthorsAndFullMessageAndPreservesMultiSelection()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = fixture.Window; ListBox history = History(main); GitCommit[] rows = history.Items.OfType<GitCommit>().ToArray();
        history.SelectedItems!.Clear(); history.SelectedItem = rows[0]; await Idle(main); history.SelectedItems.Add(rows[1]); await Idle(main);
        Pump(main);
        ListBoxItem clicked = history.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.DataContext is GitCommit commit && commit.Hash == rows[1].Hash);
        Point point = clicked.TranslatePoint(new Point(100, clicked.Bounds.Height / 2), main)!.Value;
        main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right); Pump(main);
        Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(rows[1].Hash), "A right click changes the context target while retaining both selected commits.");
        Assert.That(history.SelectedItems.OfType<GitCommit>().Count(), Is.EqualTo(2));
        ContextMenu menu = history.ContextMenu!; menu.Open(history); Pump(main);
        MenuItem copy = Item(menu, "RevisionCopy");
        MenuItem hashes = copy.Items.OfType<MenuItem>().Single(item => item.Icon is Image { Tag: "CommitId.png" });
        Click(hashes); Assert.That(await ClipboardExtensions.TryGetTextAsync(main.Clipboard!), Is.EqualTo(rows[0].Hash + "\n" + rows[1].Hash));
        Assert.That(history.SelectedItems.OfType<GitCommit>().Count(), Is.EqualTo(2));
        MenuItem authors = copy.Items.OfType<MenuItem>().Single(item => item.Icon is Image { Tag: "Author.png" });
        Click(authors); Assert.That(await ClipboardExtensions.TryGetTextAsync(main.Clipboard!), Is.EqualTo("Desktop Tester <desktop@example.com>"));
        menu.Close(); await Idle(main);
    }

    [AvaloniaTest]
    public async Task MessageCopyReadsTheVerbatimCommitMessageInsteadOfRebuildingItFromTheGridSubject()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        const string message = "First subject\ncontinued subject\n\nBody λ\n\n\nTail\n";
        string input = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "message-input.txt");
        await File.WriteAllTextAsync(input, message);
        Assert.That((await fixture.Git.RunAsync(fixture.Repository, ["commit", "--allow-empty", "--cleanup=verbatim", "-F", input])).Success, Is.True);
        await fixture.Window.OpenRepositoryAsync(fixture.Repository);
        string hash = (await fixture.Git.RunAsync(fixture.Repository, ["rev-parse", "HEAD"])).StandardOutput.Trim();
        History(fixture.Window).SelectedItem = History(fixture.Window).Items.OfType<GitCommit>().Single(commit => commit.Hash == hash);
        await Idle(fixture.Window);
        MainWindow main = fixture.Window; ContextMenu menu = History(main).ContextMenu!;
        string? previous = await ClipboardExtensions.TryGetTextAsync(main.Clipboard!);
        try
        {
            await main.Clipboard!.SetTextAsync("before copy"); menu.Open(History(main)); Pump(main);
            MenuItem copy = Item(menu, "RevisionCopy");
            Click(copy.Items.OfType<MenuItem>().Single(item => item.Icon is Image { Tag: "Message.png" }));
            DateTime end = DateTime.UtcNow.AddSeconds(10);
            while (await ClipboardExtensions.TryGetTextAsync(main.Clipboard!) == "before copy" && DateTime.UtcNow < end) { await Task.Delay(20); }
            Assert.That(await ClipboardExtensions.TryGetTextAsync(main.Clipboard!), Is.EqualTo(message));
        }
        finally { menu.Close(); await main.Clipboard!.SetTextAsync(previous ?? ""); await Idle(main); }
    }

    [AvaloniaTest]
    public async Task RightClickOnAnotherRowSelectsThatCommitAndKeepsTheContextMenuUsable()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = fixture.Window; ListBox history = History(main); Pump(main);
        ListBoxItem row = history.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.DataContext is GitCommit commit && commit.Subject == "Feature branch change");
        Point point = row.TranslatePoint(new Point(100, row.Bounds.Height / 2), main)!.Value;
        main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right); Pump(main);
        Assert.That(((GitCommit)history.SelectedItem!).Subject, Is.EqualTo("Feature branch change"));
        Assert.That(history.ContextMenu!.IsOpen, Is.True); Assert.That(history.ContextMenu.IsEnabled, Is.True);
        Assert.That(Item(history.ContextMenu, "RevisionCreateBranch").IsEnabled, Is.True);
        history.ContextMenu.Close(); await Idle(main);
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task CreationShortcutsCloseAnOpenSubmenuAndKeepTheSelectedCommitAsTheirTarget()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = fixture.Window; ListBox history = History(main);
        GitCommit target = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Feature branch change");
        history.SelectedItem = target; await Idle(main);
        foreach (PhysicalKey key in new[] { PhysicalKey.B, PhysicalKey.T })
        {
            ContextMenu menu = history.ContextMenu!; menu.Open(history); Pump(main);
            MenuItem rebase = Item(menu, "HistoryRebaseMenu"); rebase.IsSubMenuOpen = true; Pump(main);
            TopLevel popup = TopLevel.GetTopLevel(rebase.Items.OfType<MenuItem>().First())!;
            popup.KeyPressQwerty(key, RawInputModifiers.Control); popup.KeyReleaseQwerty(key, RawInputModifiers.Control); Pump(main);
            await Until(() => main.OwnedWindows.OfType<RepositoryDialog>().Any(dialog => !dialog.IsBusy));
            Assert.That(menu.IsOpen, Is.False);
            Window dialog = main.OwnedWindows.Single();
            Assert.That(key == PhysicalKey.B ? dialog is CreateBranchDialog : dialog is CreateTagDialog, Is.True);
            if (dialog is CreateBranchDialog branch) { Assert.That(branch.ReadOptions().Revision, Is.EqualTo(target.Hash)); }
            dialog.Close(); await Idle(main);
        }
    }

    [AvaloniaTest]
    public async Task CancellingADialogAfterRightClickLoadsTheNewlySelectedCommitDiff()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = fixture.Window; ListBox history = History(main); Pump(main);
        GitCommit target = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Initial commit");
        DiffView diff = main.GetVisualDescendants().OfType<DiffView>().Single(view => view.Name == "CommitDiff");
        Assert.That(diff.DocumentIdentity, Does.Not.Contain(target.Hash));
        ListBoxItem row = history.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.DataContext is GitCommit commit && commit.Hash == target.Hash);
        Point point = row.TranslatePoint(new Point(100, row.Bounds.Height / 2), main)!.Value;
        main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right); Pump(main);
        Assert.That(history.ContextMenu!.IsOpen, Is.True);
        TopLevel popup = TopLevel.GetTopLevel(history.ContextMenu.Items.OfType<MenuItem>().First())!;
        popup.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.Control); popup.KeyReleaseQwerty(PhysicalKey.B, RawInputModifiers.Control); Pump(main);
        await Until(() => main.OwnedWindows.OfType<CreateBranchDialog>().Any(dialog => !dialog.IsBusy));
        CreateBranchDialog dialog = main.OwnedWindows.OfType<CreateBranchDialog>().Single();
        Assert.That(dialog.ReadOptions().Revision, Is.EqualTo(target.Hash));
        dialog.Close(); Pump(main);
        await Until(() => !main.IsBusy && diff.DocumentIdentity?.Contains(target.Hash, StringComparison.Ordinal) == true);
        Assert.That(main.LastException, Is.Null);
        Assert.That((await fixture.Git.RunAsync(fixture.Repository, ["branch", "--list"])).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(2));
    }

    [AvaloniaTest]
    public async Task BareRepositoryKeepsCopyArchiveAndBranchCreationButDisablesWorktreeMutations()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        string bare = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "bare-context.git");
        Assert.That((await fixture.Git.RunAsync(fixture.Repository, ["clone", "--bare", fixture.Repository, bare])).Success, Is.True);
        await fixture.Window.OpenRepositoryAsync(bare); ListBox history = History(fixture.Window); ContextMenu menu = history.ContextMenu!;
        menu.Open(history); Pump(fixture.Window);
        foreach (string name in new[] { "RevisionCheckout", "RevisionResetCurrent", "RevisionResetAnother", "RevisionRevert", "RevisionCherryPick", "HistoryAdvancedMenu" })
        { Assert.That(Item(menu, name).IsEnabled, Is.False, name); }
        foreach (string name in new[] { "RevisionCopy", "RevisionCreateBranch", "RevisionCreateTag", "RevisionArchive" })
        { Assert.That(Item(menu, name).IsEnabled, Is.True, name); }
        menu.Close(); await Idle(fixture.Window);
    }

    private static string Caption(MenuItem item) => (item.Header is AccessText text ? text.Text!.Replace("_", "") : item.Header?.ToString() ?? "").Replace("(&x)", "(x)").Replace("…", "...");
    private static ListBox History(MainWindow main) => main.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "RevisionHistory");
    private static IEnumerable<MenuItem> Flatten(IEnumerable<MenuItem> items) => items.SelectMany(item => new[] { item }.Concat(Flatten(item.Items.OfType<MenuItem>())));
    private static MenuItem Item(ContextMenu menu, string name) => Flatten(menu.Items.OfType<MenuItem>()).Single(item => item.Name == name);
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void Pump(TopLevel root) { root.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Capture(TopLevel root, string name)
    { Pump(root); using WriteableBitmap frame = root.CaptureRenderedFrame()!; string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame.Save(path); TestContext.AddTestAttachment(path); }
    private static Task Idle(MainWindow main) => Until(() => !main.IsBusy);
    private static async Task Until(Func<bool> condition)
    { DateTime end = DateTime.UtcNow.AddSeconds(15); while (!condition() && DateTime.UtcNow < end) { await Task.Delay(20); } Assert.That(condition(), Is.True); }
}

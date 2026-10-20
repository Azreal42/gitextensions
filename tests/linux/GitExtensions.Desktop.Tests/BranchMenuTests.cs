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
using GitCommands;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class BranchMenuTests
{
    [AvaloniaTest]
    public async Task LabelAndArrowOpenTheOriginalLocalBranchListInBothThemesWithoutCheckout()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        await Git(f, "branch", "topic_with_underscore"); await Git(f, "tag", "a-tag");
        await Git(f, "update-ref", "refs/remotes/origin/remote-only", "HEAD");
        string before = (await Git(f, "for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput;
        MainWindow main = f.Window;
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme;
            Button button = Field<Button>(main, "BrowseCurrentBranch");
            foreach (bool arrow in new[] { false, true })
            {
                Click(button, arrow ? new Point(button.Bounds.Width - 5, 12) : new Point(40, 12));
                MenuFlyout menu = (MenuFlyout)button.Flyout!; await Ready(menu.Items);
                Assert.That(menu.IsOpen, Is.True); Assert.That(main.OwnedWindows, Is.Empty);
                Assert.That(Branches(menu.Items).Select(branch => branch.ShortName), Is.EqualTo(new[] { "main", "topic_with_underscore" }));
                MenuItem checkout = menu.Items.OfType<MenuItem>().First();
                MenuFlyoutPresenter presenter = checkout.GetVisualAncestors().OfType<MenuFlyoutPresenter>().First();
                Assert.That(presenter.PointToScreen(default).X, Is.EqualTo(button.PointToScreen(default).X).Within(2));
                Assert.That(checkout.InputGesture, Is.EqualTo(new KeyGesture(Key.OemPeriod, KeyModifiers.Control)));
                Assert.That(checkout.Icon, Is.TypeOf<Image>());
                Assert.That(menu.Items.OfType<Separator>().Count(), Is.EqualTo(1));
                Capture(TopLevel.GetTopLevel(checkout)!, "branch-menu-" + theme + (arrow ? "-arrow" : "-label") + ".png");
                menu.Hide();
            }
        }
        Assert.That((await Git(f, "for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput, Is.EqualTo(before));
        Assert.That((await Git(f, "symbolic-ref", "--short", "HEAD")).StandardOutput.Trim(), Is.EqualTo("main"));
    }

    [AvaloniaTest]
    public async Task SelectingACleanLocalBranchChecksOutItAndRefreshesTheToolbar()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        await Git(f, "checkout", "-b", "topic"); await File.WriteAllTextAsync(Path.Combine(f.Repository, "topic.txt"), "topic content\n");
        await Git(f, "add", "topic.txt"); await Git(f, "commit", "-m", "Topic"); await Git(f, "checkout", "main");
        MenuFlyout menu = await Open(f.Window); Click(Local(menu.Items, "topic")); await Idle(f.Window);
        Assert.That((await Git(f, "symbolic-ref", "--short", "HEAD")).StandardOutput.Trim(), Is.EqualTo("topic"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Repository, "topic.txt")), Is.EqualTo("topic content\n"));
        Assert.That(Field<Button>(f.Window, "BrowseCurrentBranch").GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "topic"), Is.True);
        Assert.That(f.Window.OwnedWindows, Is.Empty); Assert.That(f.Window.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task DirtyTreeOpensPreselectedCheckoutAndCancellingPreservesIndexAndFiles()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "local change\n");
        await Git(f, "branch", "topic"); await Git(f, "add", "file.txt");
        string index = (await Git(f, "write-tree")).StandardOutput;
        MenuFlyout menu = await Open(f.Window); Click(Local(menu.Items, "topic"));
        CheckoutBranchDialog dialog = await Dialog(f.Window);
        Assert.That(dialog.ReadOptions().Revision, Is.EqualTo("refs/heads/topic"));
        Assert.That(Field<RadioButton>(dialog, "CheckoutLocalBranch").IsChecked, Is.True);
        Click(Field<Button>(dialog, "CancelCheckoutBranch")); await Idle(f.Window);
        Assert.That((await Git(f, "write-tree")).StandardOutput, Is.EqualTo(index));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Repository, "file.txt")), Is.EqualTo("local change\n"));
        Assert.That((await Git(f, "symbolic-ref", "--short", "HEAD")).StandardOutput.Trim(), Is.EqualTo("main"));
    }

    [AvaloniaTest]
    public async Task ExplicitDefaultStashesDirtyChangesDuringQuickCheckout()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "local change\n");
        await Git(f, "branch", "topic");
        new ReferencePreferencesStore().Save(new(CheckoutChanges: LocalChangesAction.Stash, UseDefaultCheckoutChanges: true));
        MenuFlyout menu = await Open(f.Window); Click(Local(menu.Items, "topic"));
        // The original nullable checkout policy now asks after the successful switch.
        // Answer No without remembering; all Git/stash/focus oracles below are retained.
        await Until(() => f.Window.OwnedWindows.Any(window => window.Name == "SuppressibleConfirmation"));
        Window decision = f.Window.OwnedWindows.Single(window => window.Name == "SuppressibleConfirmation");
        Click(Field<Button>(decision, "ConfirmationNo")); await Idle(f.Window);
        Assert.That((await Git(f, "symbolic-ref", "--short", "HEAD")).StandardOutput.Trim(), Is.EqualTo("topic"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Repository, "file.txt")), Is.EqualTo("base\n"));
        Assert.That((await Git(f, "show", "stash@{0}:file.txt")).StandardOutput, Is.EqualTo("local change\n"));
        Assert.That(f.Window.OwnedWindows, Is.Empty); Assert.That(f.Window.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task DeletedSelectionCannotSilentlyCheckoutTheFirstRemainingBranch()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        await Git(f, "branch", "topic"); MenuFlyout menu = await Open(f.Window);
        MenuItem stale = Local(menu.Items, "topic"); await Git(f, "branch", "-D", "topic"); Click(stale);
        CheckoutBranchDialog dialog = await Dialog(f.Window);
        Assert.That(Field<ComboBox>(dialog, "CheckoutBranchSelection").SelectedItem, Is.Null);
        Assert.That(Field<Button>(dialog, "CheckoutBranch").IsEnabled, Is.False);
        Click(Field<Button>(dialog, "CancelCheckoutBranch")); await Idle(f.Window);
        Assert.That((await Git(f, "symbolic-ref", "--short", "HEAD")).StandardOutput.Trim(), Is.EqualTo("main"));
    }

    [AvaloniaTest]
    public async Task ReopeningRefreshesExternalBranchesAndCapsTheShortcutListAtOneHundred()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        MenuFlyout menu = await Open(f.Window); menu.Hide();
        for (int i = 0; i < 101; i++) { await Git(f, "branch", "external-" + i.ToString("D3")); }
        menu = await Open(f.Window);
        Assert.That(Branches(menu.Items), Has.Length.EqualTo(100));
        Assert.That(Branches(menu.Items).First().ShortName, Is.EqualTo("external-000")); menu.Hide();
        await Git(f, "branch", "-D", "external-000"); menu = await Open(f.Window);
        Assert.That(Branches(menu.Items).First().ShortName, Is.EqualTo("external-001")); menu.Hide();
    }

    [AvaloniaTest]
    public async Task BranchesOutsideTheDisplayedGraphRemainSelectableWithTheOriginalMutedColour()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        string tree = (await Git(f, "rev-parse", "HEAD^{tree}")).StandardOutput.Trim();
        string hidden = (await Git(f, "commit-tree", tree, "-m", "Outside the displayed graph")).StandardOutput.Trim();
        await Git(f, "update-ref", "refs/heads/hidden", hidden);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            f.Window.RequestedThemeVariant = theme; MenuFlyout menu = await Open(f.Window); MenuItem item = Local(menu.Items, "hidden");
            Assert.That(item.IsEnabled, Is.True);
            Assert.That(item.Icon, Is.TypeOf<Image>());
            Assert.That(((Avalonia.Media.ISolidColorBrush)item.Foreground!).Color, Is.EqualTo(Avalonia.Media.Color.Parse(theme == ThemeVariant.Dark ? ClassicStyles.OriginalColors(true)["GrayText"] : "#c0c0c0")));
            Capture(TopLevel.GetTopLevel(item)!, "branch-menu-hidden-" + theme + ".png"); menu.Hide();
        }
        Click(Local((await Open(f.Window)).Items, "hidden")); await Idle(f.Window);
        Assert.That((await Git(f, "rev-parse", "HEAD")).StandardOutput.Trim(), Is.EqualTo(hidden));
    }

    [AvaloniaTest]
    public async Task AFailedQuickCheckoutShowsTheSameDialogWithGitErrorAndPreservesTheCurrentBranch()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n"); await Git(f, "branch", "topic");
        await Git(f, "worktree", "add", Path.Combine(Path.GetDirectoryName(f.Repository)!, "linked"), "topic");
        Click(Local((await Open(f.Window)).Items, "topic")); CheckoutBranchDialog dialog = await Dialog(f.Window);
        Assert.That(dialog.LastError, Is.Not.Null.And.Not.Empty);
        Assert.That(dialog.ReadOptions().Revision, Is.EqualTo("refs/heads/topic"));
        Assert.That((await Git(f, "symbolic-ref", "--short", "HEAD")).StandardOutput.Trim(), Is.EqualTo("main"));
        Capture(dialog, "branch-checkout-error.png"); Click(Field<Button>(dialog, "CancelCheckoutBranch")); await Idle(f.Window);
        Assert.That((await Git(f, "status", "--porcelain")).StandardOutput, Is.Empty);
    }

    [AvaloniaTest]
    public async Task CheckoutEntryRightClickAndCtrlPeriodAlwaysOpenTheFullDialog()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        MainWindow main = f.Window;
        for (int mode = 0; mode < 4; mode++)
        {
            if (mode == 0) { MenuFlyout menu = await Open(main); Click(menu.Items.OfType<MenuItem>().First()); }
            else if (mode == 1)
            {
                Button button = Field<Button>(main, "BrowseCurrentBranch"); Point point = button.TranslatePoint(new Point(40, 12), main)!.Value;
                main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right); Pump(main);
            }
            else
            {
                TopLevel root = mode == 2 ? main : TopLevel.GetTopLevel((await Open(main)).Items.OfType<MenuItem>().First())!;
                root.KeyPressQwerty(PhysicalKey.Period, RawInputModifiers.Control); root.KeyReleaseQwerty(PhysicalKey.Period, RawInputModifiers.Control); Pump(root);
            }
            CheckoutBranchDialog dialog = await Dialog(main); Assert.That(((MenuFlyout)Field<Button>(main, "BrowseCurrentBranch").Flyout!).IsOpen, Is.False);
            Click(Field<Button>(dialog, "CancelCheckoutBranch")); await Idle(main);
        }
        Assert.That((await Git(f, "symbolic-ref", "--short", "HEAD")).StandardOutput.Trim(), Is.EqualTo("main"));
    }

    [AvaloniaTest]
    public async Task OverflowHasTheSameLocalChoicesAndAlwaysShowPreferenceKeepsTheDialog()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n"); await Git(f, "branch", "topic");
        new ReferencePreferencesStore().Save(new(AlwaysShowCheckoutDialog: true));
        Field<CompactBrowserToolbar>(f.Window, "RepositoryToolbar").Width = 320; Pump(f.Window);
        Button overflow = Field<Button>(f.Window, "RepositoryToolbarOverflow"); Click(overflow);
        MenuFlyout menu = (MenuFlyout)overflow.Flyout!;
        MenuItem branches = menu.Items.OfType<MenuItem>().Single(item => item.Name == "BranchOverflow"); branches.IsSubMenuOpen = true;
        await Ready(branches.Items); Assert.That(Branches(branches.Items).Select(branch => branch.ShortName), Is.EqualTo(new[] { "main", "topic" }));
        Click(Local(branches.Items, "topic")); CheckoutBranchDialog dialog = await Dialog(f.Window);
        Assert.That(dialog.ReadOptions().Revision, Is.EqualTo("refs/heads/topic")); Assert.That(menu.IsOpen, Is.False);
        Click(Field<Button>(dialog, "CancelCheckoutBranch")); await Idle(f.Window);
    }

    [AvaloniaTest]
    public async Task DetachedAndUnbornRepositoriesKeepTheListWhileBareRepositoriesDisableCheckout()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        await Git(f, "checkout", "--detach", "HEAD"); await f.Window.OpenRepositoryAsync(f.Repository);
        MenuFlyout menu = await Open(f.Window); Assert.That(Branches(menu.Items).Single().ShortName, Is.EqualTo("main")); menu.Hide();
        string bare = Path.Combine(Path.GetDirectoryName(f.Repository)!, "bare.git");
        GitResult clone = await f.Git.RunAsync(Path.GetDirectoryName(f.Repository)!, ["clone", "--bare", f.Repository, bare]); Assert.That(clone.Success, Is.True);
        await f.Window.OpenRepositoryAsync(bare); menu = await Open(f.Window);
        Assert.That(menu.Items.OfType<MenuItem>().All(item => !item.IsEnabled), Is.True); menu.Hide();
        string unborn = Path.Combine(Path.GetDirectoryName(f.Repository)!, "unborn"); Directory.CreateDirectory(unborn);
        Assert.That((await f.Git.RunAsync(unborn, ["init", "-b", "empty"])).Success, Is.True);
        await f.Window.OpenRepositoryAsync(unborn); menu = await Open(f.Window);
        Assert.That(Branches(menu.Items), Is.Empty); Assert.That(menu.Items.OfType<MenuItem>().First().IsEnabled, Is.True); menu.Hide();
    }

    private static GitReference[] Branches(System.Collections.IEnumerable items) => items.Cast<object>().OfType<MenuItem>().Select(item => item.Tag).OfType<GitReference>().ToArray();
    private static MenuItem Local(System.Collections.IEnumerable items, string name) => items.Cast<object>().OfType<MenuItem>().Single(item => item.Tag is GitReference branch && branch.ShortName == name);
    private static T Field<T>(Window main, string name) where T : Control => main.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static async Task<MenuFlyout> Open(MainWindow main)
    { Button button = Field<Button>(main, "BrowseCurrentBranch"); Click(button); MenuFlyout menu = (MenuFlyout)button.Flyout!; await Ready(menu.Items); return menu; }
    private static Task Ready(System.Collections.IEnumerable items) => Until(() => items.Cast<object>().OfType<MenuItem>().All(item => item.Name?.EndsWith("Loading", StringComparison.Ordinal) != true));
    private static async Task<CheckoutBranchDialog> Dialog(MainWindow main)
    { await Until(() => main.OwnedWindows.OfType<CheckoutBranchDialog>().Any()); CheckoutBranchDialog dialog = main.OwnedWindows.OfType<CheckoutBranchDialog>().Single(); await Until(() => !dialog.IsBusy); Pump(dialog); return dialog; }
    private static async Task<GitResult> Git(Fixture f, params string[] args)
    { GitResult result = await f.Git.RunAsync(f.Repository, args); Assert.That(result.Success, Is.True, result.StandardError); return result; }
    private static void Click(Control control, Point? position = null)
    { TopLevel root = TopLevel.GetTopLevel(control)!; Pump(root); Point point = control.TranslatePoint(position ?? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value; root.MouseMove(point); root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump(root); }
    private static void Pump(TopLevel root) { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static async Task Idle(MainWindow main) { await Task.Delay(20); await Until(() => !main.IsBusy); Pump(main); }
    private static async Task Until(Func<bool> condition)
    { DateTime deadline = DateTime.UtcNow.AddSeconds(20); while (!condition() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); } Assert.That(condition(), Is.True, "Branch selector did not reach the expected state."); }
    private static void Capture(TopLevel root, string name)
    { Pump(root); using var frame = root.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string file = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame!.Save(file); TestContext.AddTestAttachment(file); }
}

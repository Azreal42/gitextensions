using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RevisionContextFocusTests
{
    [AvaloniaTest]
    [TestCase(0, 0)]
    [TestCase(0, 1)]
    [TestCase(0, 2)]
    [TestCase(1, 0)]
    [TestCase(1, 1)]
    [TestCase(1, 2)]
    [TestCase(2, 0)]
    [TestCase(2, 1)]
    [TestCase(2, 2)]
    public async Task DismissingTheRevisionMenuReturnsToTheClickedRowForImmediateArrowNavigation(int focusMode, int copyMode)
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = f.Window; ListBox history = PushProcessTestDriver.Field<ListBox>(main, "RevisionHistory");
        GitCommit[] commits = history.Items.OfType<GitCommit>().ToArray(); Assert.That(commits.Length, Is.GreaterThanOrEqualTo(4));
        int index = 2; GitCommit selected = commits[index];
        ListBoxItem row = (ListBoxItem)history.ContainerFromIndex(index)!;
        if (focusMode > 0) { PushProcessTestDriver.Pointer(row); Assert.That(history.IsKeyboardFocusWithin, Is.True); }
        if (focusMode == 2)
        {
            GitResult result = await f.Git.RunAsync(f.Repository, ["tag", "-a", "focus-tag", "-m", "Before refresh", selected.Hash]);
            Assert.That(result.Success, Is.True, result.StandardError);
            result = await f.Git.RunAsync(f.Repository, ["tag", "-f", "-a", "focus-tag", "-m", "Moved externally", commits[1].Hash]);
            Assert.That(result.Success, Is.True, result.StandardError);
            object? source = history.ItemsSource; string selectedHash = selected.Hash;
            Press(main, PhysicalKey.F5); await Until(() => !main.IsBusy && !ReferenceEquals(history.ItemsSource, source)); Pump(main);
            commits = history.Items.OfType<GitCommit>().ToArray(); selected = commits.Single(commit => commit.Hash == selectedHash);
            index = Array.IndexOf(commits, selected); row = (ListBoxItem)history.ContainerFromIndex(index)!;
            Assert.That(history.SelectedItem, Is.SameAs(selected), "F5 preserves the selected commit; this test does not refocus it.");
            Log(main, "after real F5 and external retag");
        }
        if (focusMode == 0) { PushProcessTestDriver.Pointer(PushProcessTestDriver.Field<TextBox>(main, "RepositoryNavigationSearch")); Assert.That(history.IsKeyboardFocusWithin, Is.False); }
        Log(main, "before right click");
        RightClick(main, row); await Until(() => history.ContextMenu!.IsOpen); Pump(main);
        Assert.That(history.SelectedItem, Is.SameAs(selected)); ContextMenu menu = history.ContextMenu!;
        Log(main, "menu open");
        if (copyMode != 0)
        {
            MenuItem copy = menu.Items.OfType<MenuItem>().Single(item => item.Name == "RevisionCopy");
            object? oldItem = copy.Items.FirstOrDefault();
            if (copyMode == 1) { PushProcessTestDriver.Pointer(copy); }
            else { Hover(copy); }
            await Until(() => copy.IsSubMenuOpen && !ReferenceEquals(copy.Items.FirstOrDefault(), oldItem));
            if (copyMode == 1) { Press(TopLevel.GetTopLevel(copy)!, PhysicalKey.ArrowRight); }
            Pump(main);
            Log(main, "copy submenu selected");
            Press(TopLevel.GetTopLevel(copy)!, PhysicalKey.Escape); Pump(main);
            Assert.That(copy.IsSubMenuOpen, Is.False);
            if (copyMode == 1) { Assert.That(menu.IsOpen, Is.True); }
        }
        Press(TopLevel.GetTopLevel(menu) ?? main, PhysicalKey.Escape); await Until(() => !menu.IsOpen); Pump(main);
        Log(main, "menu closed");
        // No explicit Focus(), no synthetic selection and no RaiseEvent after dismissal.
        Press(main, PhysicalKey.ArrowUp); Pump(main); Log(main, "first Up");
        Assert.That(history.SelectedItem, Is.SameAs(commits[index - 1]), "The first Up after Escape must navigate from the right-clicked row.");
        Assert.That(history.IsKeyboardFocusWithin, Is.True);
        Press(main, PhysicalKey.ArrowUp); Pump(main); Log(main, "second Up");
        Assert.That(history.SelectedItem, Is.SameAs(commits[index - 2]), "The second Up must continue commit navigation.");
        Press(main, PhysicalKey.ArrowDown); Pump(main); Log(main, "first Down");
        Assert.That(history.SelectedItem, Is.SameAs(commits[index - 1]));
        Press(main, PhysicalKey.ArrowDown); Pump(main); Log(main, "second Down");
        Assert.That(history.SelectedItem, Is.SameAs(commits[index]));
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task RightClickOnAMemberOfTheMultipleSelectionPreservesAllCommitsAndFocusesThatRow()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = f.Window; ListBox history = PushProcessTestDriver.Field<ListBox>(main, "RevisionHistory");
        GitCommit[] commits = history.Items.OfType<GitCommit>().ToArray();
        ListBoxItem first = (ListBoxItem)history.ContainerFromIndex(1)!, clicked = (ListBoxItem)history.ContainerFromIndex(2)!;
        PushProcessTestDriver.Pointer(first); LeftClick(main, clicked, RawInputModifiers.Control);
        Assert.That(history.SelectedItems!.Cast<GitCommit>(), Is.EquivalentTo(new[] { commits[1], commits[2] }));
        RightClick(main, clicked); await Until(() => history.ContextMenu!.IsOpen);
        Assert.That(history.SelectedItems!.Cast<GitCommit>(), Is.EquivalentTo(new[] { commits[1], commits[2] }));
        Assert.That(history.SelectedItem, Is.SameAs(commits[2]));
        Press(TopLevel.GetTopLevel(history.ContextMenu!)!, PhysicalKey.Escape); Pump(main);
        Assert.That(main.FocusManager!.GetFocusedElement(), Is.SameAs(clicked));
        Assert.That(history.SelectedItems!.Cast<GitCommit>(), Is.EquivalentTo(new[] { commits[1], commits[2] }));
    }

    [AvaloniaTest]
    public async Task AContextActionOpeningADialogKeepsTheDialogsEditingFocus()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = f.Window; ListBox history = PushProcessTestDriver.Field<ListBox>(main, "RevisionHistory");
        GitCommit selected = history.Items.OfType<GitCommit>().ElementAt(2);
        RightClick(main, (ListBoxItem)history.ContainerFromIndex(2)!); await Until(() => history.ContextMenu!.IsOpen);
        MenuItem create = history.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "RevisionCreateBranch");
        PushProcessTestDriver.Pointer(create);
        await Until(() => main.OwnedWindows.OfType<CreateBranchDialog>().Any());
        CreateBranchDialog dialog = main.OwnedWindows.OfType<CreateBranchDialog>().Single(); Pump(dialog);
        TextBox name = PushProcessTestDriver.Field<TextBox>(dialog, "CreateBranchName");
        TextBlock summary = PushProcessTestDriver.Field<TextBlock>(dialog, "CreateBranchSummary");
        await Until(() => !dialog.IsBusy && name.IsEnabled && !string.IsNullOrEmpty(summary.Text));
        Pump(dialog);
        Assert.That(history.ContextMenu.IsOpen, Is.False);
        Assert.That(dialog.ReadOptions().Revision, Is.EqualTo(selected.Hash));
        Assert.That(dialog.FocusManager!.GetFocusedElement(), Is.SameAs(name), "Closing the context menu must leave the action dialog's focus intact.");
        dialog.KeyTextInput("c"); Pump(dialog); Assert.That(name.Text, Is.EqualTo("c"), "The first key after completed initialization must reach Branch name.");
        dialog.KeyTextInput("ontext-action-focus"); Pump(dialog); Assert.That(name.Text, Is.EqualTo("context-action-focus"));
        PushProcessTestDriver.Pointer(PushProcessTestDriver.Field<Button>(dialog, "CancelCreateBranch"));
        await Until(() => main.OwnedWindows.Count == 0 && !main.IsBusy);
        Assert.That((await f.Git.RunAsync(f.Repository, ["branch", "--list", "context-action-focus"])).StandardOutput, Is.Empty);
    }

    private static void LeftClick(Window window, Control control, RawInputModifiers modifiers)
    {
        Pump(window); Point point = control.TranslatePoint(new Point(Math.Min(100, control.Bounds.Width / 2), control.Bounds.Height / 2), window)!.Value;
        Assert.That((window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(control), Is.True);
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left, modifiers); window.MouseUp(point, MouseButton.Left, modifiers); Pump(window);
    }
    private static void Hover(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; Pump(top);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        Assert.That((top.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(control), Is.True);
        top.MouseMove(point); Pump(top);
    }
    private static void RightClick(Window window, Control control)
    {
        Pump(window); Point point = control.TranslatePoint(new Point(Math.Min(100, control.Bounds.Width / 2), control.Bounds.Height / 2), window)!.Value;
        Assert.That((window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(control), Is.True, "Right click must hit the real revision row.");
        window.MouseMove(point); window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right); Pump(window);
    }
    private static void Press(TopLevel root, PhysicalKey key)
    { root.KeyPressQwerty(key, RawInputModifiers.None); root.KeyReleaseQwerty(key, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); }
    private static void Pump(TopLevel root)
    { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> ready)
    { Stopwatch watch = Stopwatch.StartNew(); while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(ready(), Is.True); }
    private static void Log(MainWindow main, string phase)
    {
        ListBox history = PushProcessTestDriver.Field<ListBox>(main, "RevisionHistory");
        Control? focus = main.FocusManager?.GetFocusedElement() as Control;
        ListBoxItem? row = focus?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        TestContext.WriteLine($"{phase}: focused={focus?.GetType().Name}/{focus?.Name} rowIndex={(row is null ? -1 : history.IndexFromContainer(row))}/{(row?.DataContext as GitCommit)?.Subject} historyFocus={history.IsKeyboardFocusWithin} selected={history.SelectedIndex}/{(history.SelectedItem as GitCommit)?.Subject}");
    }
}

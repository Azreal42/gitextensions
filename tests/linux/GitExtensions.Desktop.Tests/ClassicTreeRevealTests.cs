using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ClassicTreeRevealTests
{
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public void ShortLocalRemoteAndTagRowsDoNotRevealTheBlankWidthOfALongSibling(bool dark)
    {
        using Scope f = new(dark: dark);
        object[] roots = f.Tree.Items.Cast<object>().ToArray();
        double rootX = f.Header(f.Branches).TranslatePoint(default, f.Presenter)!.Value.X;
        Assert.That(f.Scroll.Extent.Width, Is.GreaterThan(f.Scroll.Viewport.Width));
        foreach (TreeViewItem node in new[] { f.First, f.Remote, f.Tag })
        {
            Assert.That(f.Header(node).Bounds.Width, Is.GreaterThan(f.Scroll.Viewport.Width), "Reproduce the stretched native header, not a label-sized substitute.");
            PushProcessTestDriver.Pointer(f.Label(node)); f.Pump();
            Assert.That(f.Tree.SelectedItem, Is.SameAs(node));
            Assert.That(node.IsKeyboardFocusWithin, Is.True);
            Assert.That(f.Scroll.Offset.X, Is.EqualTo(0).Within(.01));
            Assert.That(f.Header(f.Branches).TranslatePoint(default, f.Presenter)!.Value.X, Is.EqualTo(rootX).Within(.01));
            Assert.That(f.Tree.Items.Cast<object>().Zip(roots).All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True);
        }
        f.Capture(dark ? "classic-tree-short-dark.png" : "classic-tree-short-light.png");
    }

    [AvaloniaTest]
    public void PhysicalArrowNavigationRetainsItsNativeCursorAndDoesNotShiftShortRows()
    {
        using Scope f = new();
        PushProcessTestDriver.Pointer(f.Label(f.First)); f.Pump();
        f.Press(PhysicalKey.ArrowDown);
        Assert.That(f.Tree.SelectedItem, Is.SameAs(f.Second)); Assert.That(f.Second.IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Scroll.Offset.X, Is.EqualTo(0).Within(.01));
        f.Press(PhysicalKey.ArrowUp);
        Assert.That(f.Tree.SelectedItem, Is.SameAs(f.First)); Assert.That(f.First.IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Scroll.Offset.X, Is.EqualTo(0).Within(.01));
    }

    [AvaloniaTest]
    [TestCase(true)] [TestCase(false)]
    public void ExplicitBringIntoViewKeepsVerticalRevealAndAnAlreadyVisibleHorizontalBandWithoutStealingFocus(bool rootLines)
    {
        using Scope f = new(rootLines: rootLines, fillerCount: 30);
        f.Outside.Focus(); f.Scroll.Offset = new Vector(20, 0); f.Pump();
        Assert.That(f.Header(f.Remote).TranslatePoint(default, f.Presenter)!.Value.Y, Is.GreaterThan(f.Presenter.Viewport.Height));
        Assert.That(f.Scroll.Offset.X, Is.EqualTo(20).Within(.01));
        f.Remote.BringIntoView(); f.Pump();
        Assert.That(f.Scroll.Offset.Y, Is.GreaterThan(0));
        Assert.That(f.Scroll.Offset.X, Is.EqualTo(20).Within(.01));
        f.AssertVerticallyVisible(f.Remote);
        Assert.That(f.Outside.IsFocused, Is.True, "An explicit reveal must not select or focus an unrelated row.");
        Assert.That(f.Tree.SelectedItem, Is.Null);
    }

    [AvaloniaTest]
    public void ADeepShortNodeStillRevealsItsActualGutterAndLabelHorizontallyAndVertically()
    {
        using Scope f = new(deep: true);
        TreeViewItem target = f.Deep!;
        Assert.That(f.Header(target).TranslatePoint(default, f.Presenter)!.Value.X, Is.GreaterThan(f.Presenter.Viewport.Width));
        f.Tree.SelectedItem = target; target.Focus(); target.BringIntoView(); f.Pump();
        Assert.That(f.Tree.SelectedItem, Is.SameAs(target)); Assert.That(target.IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Scroll.Offset.X, Is.GreaterThan(0)); Assert.That(f.Scroll.Offset.Y, Is.GreaterThan(0));
        f.AssertVerticallyVisible(target);
        Point start = f.Header(target).TranslatePoint(default, f.Presenter)!.Value;
        TextBlock label = f.Label(target);
        Point end = label.TranslatePoint(new Point(label.Bounds.Width, 0), f.Presenter)!.Value;
        Assert.That(start.X, Is.GreaterThanOrEqualTo(-.5));
        Assert.That(end.X, Is.LessThanOrEqualTo(f.Presenter.Viewport.Width + .5));
    }

    [AvaloniaTest]
    public void ALongHeaderStillUsesNativeHorizontalRevealInsteadOfFreezingTheCurrentOffset()
    {
        using Scope f = new();
        Assert.That(f.Label(f.Long).DesiredSize.Width, Is.GreaterThan(f.Presenter.Viewport.Width));
        f.Long.BringIntoView(); f.Pump();
        Assert.That(f.Scroll.Offset.X, Is.GreaterThan(0));
        Assert.That(f.Header(f.Long).TranslatePoint(default, f.Presenter)!.Value.X, Is.EqualTo(0).Within(.5));
        f.AssertVerticallyVisible(f.Long);
    }

    [AvaloniaTest]
    public void RealHorizontalWheelScrollingRemainsAvailableAndSelectingVisibleContentDoesNotResetIt()
    {
        using Scope f = new(); f.Outside.Focus();
        Point point = f.Presenter.TranslatePoint(new Point(f.Presenter.Bounds.Width / 2, 12), f.Window)!.Value;
        f.Window.MouseMove(point); f.Window.MouseWheel(point, new Vector(-1, 0), RawInputModifiers.None); f.Pump();
        Assert.That(f.Scroll.Offset.X, Is.GreaterThan(0)); Assert.That(f.Outside.IsFocused, Is.True);
        Assert.That(f.Tree.SelectedItem, Is.Null);
        f.Scroll.Offset = new Vector(20, 0); f.Pump();
        PushProcessTestDriver.Pointer(f.Label(f.First)); f.Pump();
        Assert.That(f.Tree.SelectedItem, Is.SameAs(f.First)); Assert.That(f.First.IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Scroll.Offset.X, Is.EqualTo(20).Within(.01));
    }

    [AvaloniaTest]
    public void ChevronAndNativeContextMenuStillReceiveTheirActualPointerAndKeyboardGestures()
    {
        using Scope f = new(); int clicks = 0;
        ToggleButton arrow = f.Branches.GetVisualDescendants().OfType<ToggleButton>()
            .Single(control => control.Name == "PART_ExpandCollapseChevron" && control.TemplatedParent == f.Branches);
        arrow.Click += (_, _) => clicks++;
        PushProcessTestDriver.Pointer(arrow); f.Pump();
        Assert.That(clicks, Is.EqualTo(1)); Assert.That(f.Branches.IsExpanded, Is.False);
        f.Tree.SelectedItem = f.Branches; f.Branches.Focus(); f.Press(PhysicalKey.ArrowRight);
        Assert.That(f.Branches.IsExpanded, Is.True); Assert.That(f.Branches.IsKeyboardFocusWithin, Is.True);
        ContextMenu menu = new() { Items = { new MenuItem { Header = "Copy name" } } }; f.First.ContextMenu = menu;
        TextBlock label = f.Label(f.First);
        Point point = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), f.Window)!.Value;
        f.Window.MouseMove(point); f.Window.MouseDown(point, MouseButton.Right); f.Window.MouseUp(point, MouseButton.Right); f.Pump();
        Assert.That(menu.IsOpen, Is.True); Assert.That(f.Tree.SelectedItem, Is.SameAs(f.First));
        f.Press(PhysicalKey.Escape); Assert.That(menu.IsOpen, Is.False);
        Assert.That(f.Tree.IsKeyboardFocusWithin, Is.True); Assert.That(f.Scroll.Offset.X, Is.EqualTo(0).Within(.01));
    }

    [AvaloniaTest]
    public void ATreeThatDidNotOptIntoClassicAppearanceRetainsTheNativeRevealPolicy()
    {
        _ = ClassicTreeViewAppearance.RowHeight; // Initialize the shared policy even when this case runs by itself.
        using Scope f = new(classic: false);
        Assert.That(f.Header(f.First).Bounds.Width, Is.GreaterThan(f.Scroll.Viewport.Width));
        f.Tree.SelectedItem = f.First; f.Pump();
        Assert.That(f.Scroll.Offset.X, Is.GreaterThan(0), "The shared handler must not silently change ordinary TreeViews.");
    }

    [AvaloniaTest]
    public void RepeatedDeepRevealsBeforeAndAfterSettledLayoutKeepTheSameHorizontalBand()
    {
        using Scope f = new(deep: true); TreeViewItem target = f.Deep!;
        f.Tree.SelectedItem = target;
        double first = f.Scroll.Offset.X; Assert.That(first, Is.GreaterThan(0));
        target.Focus(); target.BringIntoView(); target.BringIntoView();
        Assert.That(f.Scroll.Offset.X, Is.EqualTo(first).Within(.01), "Repeated requests before layout must use the same content coordinates.");
        f.Pump(); AssertHorizontallyVisible(f, target); f.AssertVerticallyVisible(target);
        double settled = f.Scroll.Offset.X;
        for (int repeat = 0; repeat < 3; repeat++)
        {
            target.BringIntoView(); f.Pump();
            Assert.That(f.Scroll.Offset.X, Is.EqualTo(settled).Within(.01));
            AssertHorizontallyVisible(f, target); f.AssertVerticallyVisible(target);
        }
        Assert.That(f.Tree.SelectedItem, Is.SameAs(target)); Assert.That(target.IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    public void DifferentTargetsBeforeLayoutUseTheirOwnCoordinatesInsteadOfAccumulatingThePreviousOffset()
    {
        using Scope f = new(deep: true); TreeViewItem target = f.Deep!; f.Outside.Focus();
        target.BringIntoView(); double deep = f.Scroll.Offset.X; Assert.That(deep, Is.GreaterThan(0));
        f.First.BringIntoView(); double first = f.Scroll.Offset.X;
        Assert.That(first, Is.LessThan(deep), "The short first node lies left of the current horizontal band and must be revealed.");
        f.Remote.BringIntoView();
        Assert.That(f.Scroll.Offset.X, Is.EqualTo(first).Within(.01), "The remote is already within that newly requested band.");
        target.BringIntoView(); f.Pump();
        AssertHorizontallyVisible(f, target); f.AssertVerticallyVisible(target);
        Assert.That(f.Outside.IsFocused, Is.True); Assert.That(f.Tree.SelectedItem, Is.Null);
    }

    [AvaloniaTest]
    public void RealRevealOrderSelectionThenExplicitRevealThenFocusRequiresNoInterveningLayout()
    {
        using Scope f = new(deep: true); TreeViewItem target = f.Deep!;
        // MainWindow.FocusedRevisionMenu uses this exact synchronous ordering,
        // then posts a guarded focus restoration at Loaded priority.
        f.Tree.SelectedItem = target; target.BringIntoView(); target.Focus();
        Dispatcher.UIThread.Post(() => target.Focus(), DispatcherPriority.Loaded); f.Pump();
        Assert.That(f.Tree.SelectedItem, Is.SameAs(target)); Assert.That(target.IsKeyboardFocusWithin, Is.True);
        AssertHorizontallyVisible(f, target); f.AssertVerticallyVisible(target);
    }

    [AvaloniaTest]
    public void PendingHorizontalDisplacementIsConvertedThroughAnAxisAlignedItemScale()
    {
        using Scope f = new(deep: true); TreeViewItem target = f.Deep!;
        target.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
        target.RenderTransform = new ScaleTransform(1.25, 1); f.Pump();
        Assert.That(f.Header(target).TranslatePoint(default, f.Presenter)!.Value.X, Is.GreaterThan(f.Presenter.Viewport.Width));
        f.Tree.SelectedItem = target; target.Focus(); target.BringIntoView(); f.Pump();
        Assert.That(f.Tree.SelectedItem, Is.SameAs(target)); Assert.That(target.IsKeyboardFocusWithin, Is.True);
        AssertHorizontallyVisible(f, target); f.AssertVerticallyVisible(target);
    }

    private static void AssertHorizontallyVisible(Scope f, TreeViewItem target)
    {
        Point start = f.Header(target).TranslatePoint(default, f.Presenter)!.Value;
        TextBlock label = f.Label(target);
        Point end = label.TranslatePoint(new Point(label.Bounds.Width, 0), f.Presenter)!.Value;
        Assert.That(start.X, Is.GreaterThanOrEqualTo(-.5));
        Assert.That(end.X, Is.LessThanOrEqualTo(f.Presenter.Viewport.Width + .5));
    }

    private sealed class Scope : IDisposable
    {
        internal Window Window { get; }
        internal TreeView Tree { get; } = new() { Width = 220, Height = 230, HorizontalAlignment = HorizontalAlignment.Left };
        internal TextBox Outside { get; } = new() { Text = "outside focus", Height = 26 };
        internal TreeViewItem Branches { get; } = Node("Branches");
        internal TreeViewItem First { get; } = Node("a-first");
        internal TreeViewItem Second { get; } = Node("b-next");
        internal TreeViewItem Remote { get; } = Node("a-first");
        internal TreeViewItem Tag { get; } = Node("annotated-a");
        internal TreeViewItem Long { get; } = Node("worktree-with-a-long-description-" + new string('w', 90));
        internal TreeViewItem? Deep { get; }
        internal ScrollViewer Scroll => Tree.GetVisualDescendants().OfType<ScrollViewer>().Single(viewer => viewer.TemplatedParent == Tree);
        internal ScrollContentPresenter Presenter => Scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().Single(value => value.TemplatedParent == Scroll);

        internal Scope(bool dark = false, bool rootLines = true, int fillerCount = 0, bool deep = false, bool classic = true)
        {
            Branches.Items.Add(First); Branches.Items.Add(Second);
            for (int index = 0; index < fillerCount; index++) { Branches.Items.Add(Node("filler-" + index)); }
            TreeViewItem origin = Node("origin"), remotes = Node("Remotes"), tags = Node("Tags"), worktrees = Node("Worktrees");
            origin.Items.Add(Remote); remotes.Items.Add(origin); tags.Items.Add(Tag); worktrees.Items.Add(Long);
            Tree.Items.Add(Branches); Tree.Items.Add(remotes); Tree.Items.Add(tags); Tree.Items.Add(worktrees);
            if (deep)
            {
                TreeViewItem root = Node("Deep"), parent = root; Tree.Items.Add(root);
                for (int index = 0; index < 14; index++) { TreeViewItem child = Node("f" + index); parent.Items.Add(child); parent = child; }
                Deep = Node("target"); parent.Items.Add(Deep);
            }
            ScrollViewer.SetHorizontalScrollBarVisibility(Tree, ScrollBarVisibility.Auto);
            Control surface = classic ? ClassicTreeViewAppearance.Wrap(Tree, showRootLines: rootLines) : Tree;
            Window = new() { Width = 300, Height = 300, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
                Content = new StackPanel { Children = { Outside, surface } } };
            Window.Show(); Pump();
        }

        private static TreeViewItem Node(string text) => new()
        {
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
                Children = { OriginalMenuItems.Icon("BranchLocal.png"), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } }
            },
            IsExpanded = true
        };
        internal TextBlock Label(TreeViewItem item) => ((StackPanel)item.Header!).Children.OfType<TextBlock>().Single();
        internal Control Header(TreeViewItem item) => item.GetVisualDescendants().OfType<Control>()
            .Single(control => control.Name == "PART_Header" && control.TemplatedParent == item);
        internal void Pump()
        {
            for (int pass = 0; pass < 4; pass++) { Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
            Dispatcher.UIThread.RunJobs();
        }
        internal void Press(PhysicalKey key)
        { Window.KeyPressQwerty(key, RawInputModifiers.None); Window.KeyReleaseQwerty(key, RawInputModifiers.None); Pump(); }
        internal void AssertVerticallyVisible(TreeViewItem item)
        {
            Control header = Header(item); Point point = header.TranslatePoint(default, Presenter)!.Value;
            Assert.That(point.Y, Is.GreaterThanOrEqualTo(-.5));
            Assert.That(point.Y + header.Bounds.Height, Is.LessThanOrEqualTo(Presenter.Viewport.Height + .5));
        }
        internal void Capture(string name)
        {
            string root = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
            Directory.CreateDirectory(root); string path = Path.Combine(root, name);
            using var frame = Window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); frame!.Save(path); TestContext.AddTestAttachment(path);
        }
        public void Dispose() => Window.Close();
    }
}

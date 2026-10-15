using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CompactNavigationTreeTests
{
    [AvaloniaTest]
    public async Task WindowsDensityUsesEighteenPixelHeadersNineteenPixelIndentAndOriginalRootAndFolderIcons()
    {
        using Fixture f = await CreateAsync();
        TreeView tree = Tree(f.Window);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            f.Window.RequestedThemeVariant = theme; Pump(f.Window);
            TreeViewItem[] nodes = Visible(tree);
            Assert.That(nodes.Select(item => Header(item).Bounds.Height), Is.All.EqualTo(18));
            double[] tops = nodes.Select(item => Header(item).TranslatePoint(default, tree)!.Value.Y).Order().ToArray();
            Assert.That(tops.Zip(tops.Skip(1), (a, b) => b - a), Is.All.EqualTo(18));
            TreeViewItem root = tree.Items.OfType<TreeViewItem>().First();
            TreeViewItem child = root.Items.OfType<TreeViewItem>().First();
            Assert.That(X(Label(child), tree) - X(Label(root), tree), Is.EqualTo(19));
            Assert.That(X(Label(root).Children.OfType<TextBlock>().Single(), tree), Is.EqualTo(45));
            Assert.That(nodes.All(item => Label(item).Children.OfType<Image>().Single().Bounds.Size == new Size(16, 16)), Is.True);
            Assert.That(Label(root).Children.OfType<Image>().Single().Tag, Is.EqualTo("LocalBranchRoot.png"));
            TreeViewItem current = nodes.Single(item => item.Tag is GitReference { IsCurrent: true });
            Assert.That(Label(current).Children.OfType<TextBlock>().Single().FontWeight, Is.EqualTo(FontWeight.Bold));
            ToggleButton arrow = root.GetVisualDescendants().OfType<ToggleButton>().Single(control => control.Name == "PART_ExpandCollapseChevron" && control.TemplatedParent == root);
            Assert.That(arrow.Bounds.Size, Is.EqualTo(new Size(7, 7)));
            Assert.That(tree.GetVisualDescendants().OfType<TreeViewItem>().Any(item => Header(item).Bounds.Height > 18), Is.False);
            // Assert real alternating pixels, not just a DashStyle declaration.
            // Fractional dash endpoints otherwise look like a continuous gray line.
            using (var frame = f.Window.CaptureRenderedFrame()!)
            using (ILockedFramebuffer buffer = frame.Lock())
            {
                Assert.That(f.Window.TryFindResource("ClassicDisabledBrush", theme, out object? color), Is.True);
                byte ink = ((ISolidColorBrush)color!).Color.R;
                byte[] pixels = new byte[buffer.RowBytes * buffer.Size.Height]; Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);
                int x = (int)Math.Floor(arrow.TranslatePoint(new Point(3.5, 3.5), f.Window)!.Value.X);
                int from = (int)Header(root).TranslatePoint(default, f.Window)!.Value.Y + 20;
                int to = (int)Header(tree.Items.OfType<TreeViewItem>().Skip(1).First()).TranslatePoint(default, f.Window)!.Value.Y - 2;
                int dots = 0, gaps = 0;
                byte background = ((ISolidColorBrush)tree.Background!).Color.R;
                for (int y = from; y < to; y++)
                {
                    int offset = y * buffer.RowBytes + x * 4;
                    if (pixels[offset] == ink && pixels[offset + 1] == ink && pixels[offset + 2] == ink) { dots++; }
                    if (pixels[offset] == background && pixels[offset + 1] == background && pixels[offset + 2] == background) { gaps++; }
                }
                Assert.That(dots, Is.GreaterThan(10)); Assert.That(gaps, Is.GreaterThan(10));
            }
            Capture(f.Window, tree, "compact-tree-" + theme);
        }
    }

    [AvaloniaTest]
    public async Task SmallChevronsStillExpandWithPointerAndKeyboardAndRefreshPreservesCollapsedFolders()
    {
        using Fixture f = await CreateAsync(); TreeView tree = Tree(f.Window);
        TreeViewItem branches = tree.Items.OfType<TreeViewItem>().First();
        ToggleButton arrow = branches.GetVisualDescendants().OfType<ToggleButton>().Single(control => control.Name == "PART_ExpandCollapseChevron" && control.TemplatedParent == branches);
        Assert.That(arrow.IsEffectivelyEnabled, Is.True, "Navigation must finish loading before the click.");
        int clicks = 0; arrow.Click += (_, _) => clicks++;
        Point point = arrow.TranslatePoint(new Point(3.5, 3.5), f.Window)!.Value;
        f.Window.MouseMove(point);
        Visual? hit = f.Window.InputHitTest(point) as Visual;
        Assert.That(hit?.GetSelfAndVisualAncestors().Contains(arrow), Is.True,
            $"Arrow at {point}, enabled {arrow.IsEffectivelyEnabled}, bounds {arrow.Bounds}; hit: " + string.Join(" > ", hit?.GetSelfAndVisualAncestors().Select(control => control.GetType().Name + "#" + (control as Control)?.Name) ?? []));
        f.Window.MouseDown(point, MouseButton.Left); f.Window.MouseUp(point, MouseButton.Left); Pump(f.Window);
        Assert.That(clicks, Is.EqualTo(1));
        Assert.That(branches.IsExpanded, Is.False);
        Assert.That(Visible(tree).First(), Is.SameAs(branches));
        tree.SelectedItem = branches; branches.Focus();
        f.Window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); f.Window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); Pump(f.Window);
        Assert.That(branches.IsExpanded, Is.True);
        TreeViewItem folder = BrowseTestHelpers.NavigationNodes(tree).Single(item => Label(item).Children.OfType<TextBlock>().Single().Text == "codex");
        folder.IsExpanded = false; Pump(f.Window);
        await f.Window.OpenRepositoryAsync(f.Repository); Pump(f.Window);
        folder = BrowseTestHelpers.NavigationNodes(tree).Single(item => Label(item).Children.OfType<TextBlock>().Single().Text == "codex");
        Assert.That(folder.IsExpanded, Is.False);
        Assert.That(((Grid)tree.Parent!).Children.Single(control => control.Name == "ClassicTreeConnectors").IsHitTestVisible, Is.False);
        Assert.That((await new GitRepository(f.Repository).GetStatusAsync()).Changes, Is.Empty);
    }

    [AvaloniaTest]
    public async Task NestedReferenceSelectionAndSearchKeepLiteralNamesAndActualHistory()
    {
        using Fixture f = await CreateAsync(); TreeView tree = Tree(f.Window);
        TreeViewItem target = BrowseTestHelpers.NavigationNodes(tree).Single(item => item.Tag is GitReference { Name: "refs/heads/codex/api/bug_fix" });
        Point point = Label(target).TranslatePoint(new Point(35, 8), f.Window)!.Value;
        f.Window.MouseMove(point); f.Window.MouseDown(point, MouseButton.Left); f.Window.MouseUp(point, MouseButton.Left);
        await Until(() => !f.Window.IsBusy && f.Window.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "BranchHistoryFilter").Text == "refs/heads/codex/api/bug_fix");
        TextBox filter = f.Window.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "RepositoryNavigationSearch");
        filter.Text = "bug_fix"; Pump(f.Window);
        Assert.That(Visible(tree).Where(item => item.Tag is GitReference).Select(item => Label(item).Children.OfType<TextBlock>().Single().Text), Is.EqualTo(new[] { "bug_fix" }));
        filter.Text = ""; Pump(f.Window);
        Assert.That(((GitReference)((TreeViewItem)tree.SelectedItem!).Tag!).Name, Is.EqualTo("refs/heads/codex/api/bug_fix"));
        Assert.That(f.Window.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task FrenchAndLongBranchNamesKeepCompactRowsAndNativeHorizontalScrolling()
    {
        using Fixture f = await CreateAsync();
        string branch = "codex/une_branche_avec_un_nom_très_long_à_afficher_sans_le_tronquer";
        Assert.That((await f.Git.RunAsync(f.Repository, ["branch", branch])).Success, Is.True);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
        MainWindow french = new() { Width = 1280, Height = 752 }; french.Show();
        try
        {
            await Until(() => !french.IsBusy);
            await french.OpenRepositoryAsync(f.Repository); Pump(french); await Until(() => !french.IsBusy);
            TreeView tree = Tree(french);
            TreeViewItem leaf = BrowseTestHelpers.NavigationNodes(tree).Single(item => item.Tag is GitReference reference && reference.ShortName == branch);
            Assert.That(Label(leaf).Children.OfType<TextBlock>().Single().Text, Is.EqualTo(branch.Split('/')[1]));
            ScrollViewer scroll = tree.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.That(scroll.Extent.Width, Is.GreaterThan(scroll.Viewport.Width));
            scroll.Offset = new Vector(30, 0); Pump(french);
            Assert.That(scroll.Offset.X, Is.GreaterThan(0));
            Assert.That(Visible(tree).Select(item => Header(item).Bounds.Height), Is.All.EqualTo(18));
            Capture(french, tree, "compact-tree-fr-scrolled");
        }
        finally { french.Close(); }
    }

    private static async Task<Fixture> CreateAsync()
    {
        Fixture fixture = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        try
        {
            foreach (string branch in new[] { "codex/api/bug_fix", "codex/ui/layout", "fix/typo" })
            {
                GitResult result = await fixture.Git.RunAsync(fixture.Repository, ["branch", branch]);
                Assert.That(result.Success, Is.True, result.StandardError);
            }
            await fixture.Window.OpenRepositoryAsync(fixture.Repository); Pump(fixture.Window); await Until(() => !fixture.Window.IsBusy); return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }
    private static TreeView Tree(Window window) => window.GetVisualDescendants().OfType<TreeView>().Single(tree => tree.Name == "RepositoryNavigation");
    private static TreeViewItem[] Visible(TreeView tree) => BrowseTestHelpers.NavigationNodes(tree).Where(item => item.IsEffectivelyVisible && item.GetVisualDescendants().OfType<Border>().Any(control => control.Name == "PART_LayoutRoot" && control.TemplatedParent == item)).ToArray();
    private static StackPanel Label(TreeViewItem item) => (StackPanel)item.Header!;
    private static Border Header(TreeViewItem item) => item.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_LayoutRoot" && control.TemplatedParent == item);
    private static double X(Control control, Visual relative) => control.TranslatePoint(default, relative)!.Value.X;
    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); window.CaptureRenderedFrame()?.Dispose(); }
    private static void Capture(Window window, TreeView tree, string name)
    {
        using var bitmap = window.CaptureRenderedFrame(); Assert.That(bitmap, Is.Not.Null);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name + ".png"); bitmap!.Save(path); TestContext.AddTestAttachment(path);
        Control panel = window.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RepositoryNavigationPanel");
        Point top = panel.TranslatePoint(default, window)!.Value;
        string metadata = Path.Combine(TestContext.CurrentContext.WorkDirectory, name + ".json");
        File.WriteAllText(metadata, System.Text.Json.JsonSerializer.Serialize(new { X = top.X, Y = top.Y, Width = panel.Bounds.Width, Height = Header(Visible(tree)[^1]).TranslatePoint(default, panel)!.Value.Y + 18 }));
        TestContext.AddTestAttachment(metadata);
    }
    private static async Task Until(Func<bool> predicate) { for (int i = 0; i < 500 && !predicate(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); } Assert.That(predicate(), Is.True); }
}

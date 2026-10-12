using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class DiffViewerAppearanceMenuTests
{
    [AvaloniaTest]
    public void PointerChoicesAndSharedOptionsUpdateExistingMenusAndANewViewerResetsOnlyUnrememberedAppearance()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new();
        DiffViewerPreferencesStore store = new(directory.Path);
        using DiffViewerAppearanceMenu first = new(store), second = new(new DiffViewerPreferencesStore(directory.Path));
        first.SetContext(true, false); second.SetContext(true, false);
        MenuFlyout flyout = first.CreateFlyout(); MenuItem other = second.CreateMenuItem();
        TextBlock displayed = new(); first.Changed += (_, _) => displayed.Text = first.Preferences.Appearance.ToString();
        Button open = new() { Content = "Appearance", Flyout = flyout };
        Window window = new() { Width = 720, Height = 300, Content = new StackPanel { Children = { open, displayed } } };
        try
        {
            window.Show(); Open(flyout, open);
            Assert.That(Item(flyout, "Difftastic").IsEnabled, Is.False);
            ClickRendered(Item(flyout, "GitWordDiff"));
            Assert.That(displayed.Text, Is.EqualTo("GitWordDiff"));
            Assert.That(other.Items.OfType<MenuItem>().Single(item => item.Name == "DiffViewerGitWordDiff").IsChecked, Is.True);
            Open(flyout, open); ClickRendered(Item(flyout, "GitWordDiff"));
            Assert.That(first.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Patch), "Original word-diff choices toggle back to Patch on a second click.");
            Open(flyout, open); ClickRendered(Item(flyout, "UseGitColoring"));
            Assert.That(second.Preferences.UseGitColoring, Is.False); Assert.That(Item(flyout, "ReverseGitColoring").IsEnabled, Is.False);
            Assert.That(first.Preferences.ReverseGitColoring, Is.True, "Disabling Git coloring must not erase the inversion preference.");
            Open(flyout, open); ClickRendered(Item(flyout, "UseGitColoring"));
            Open(flyout, open); ClickRendered(Item(flyout, "ReverseGitColoring"));
            Assert.That(second.Preferences.ReverseGitColoring, Is.False);
            Open(flyout, open); ClickRendered(Item(flyout, "GitWordDiff"));
            using (DiffViewerAppearanceMenu reopened = new(new DiffViewerPreferencesStore(directory.Path)))
            { Assert.That(reopened.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Patch)); }
            Assert.That(first.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Patch), "The original runtime preference is global, so resetting a newly opened viewer also synchronizes existing menus.");
            Assert.That(first.Preferences.ReverseGitColoring, Is.False);
            Open(flyout, open); ClickRendered(Item(flyout, "RememberAppearance"));
            Open(flyout, open); ClickRendered(Item(flyout, "GitWordDiff"));
            using (DiffViewerAppearanceMenu remembered = new(new DiffViewerPreferencesStore(directory.Path)))
            { Assert.That(remembered.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.GitWordDiff)); }
            first.SetContext(true, true); Open(flyout, open); ClickRendered(Item(flyout, "Difftastic"));
            Assert.That(second.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Difftastic));
            first.SetContext(true, false); Open(flyout, open);
            Assert.That(Item(flyout, "Difftastic").IsEnabled, Is.False, "Availability is supplied for the current repository, not inferred from a remembered choice.");
        }
        finally { flyout.Hide(); window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaTest]
    public void OriginalKeyboardTogglesAreScopedAndRespectCombinedRangePlainAndUnavailableDifftasticContexts()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new();
        using DiffViewerAppearanceMenu controller = new(new DiffViewerPreferencesStore(directory.Path));
        TextBox viewer = new() { Text = "Read-only viewer", IsReadOnly = true }, outside = new() { Text = "Other editor", IsReadOnly = true };
        StackPanel scope = new() { Children = { viewer } };
        controller.Attach(scope); controller.Attach(scope); controller.SetContext(true, false);
        Window window = new() { Width = 720, Height = 220, Content = new StackPanel { Children = { scope, outside } } };
        int changed = 0; controller.Changed += (_, _) => changed++;
        try
        {
            window.Show(); viewer.Focus(); Press(window, PhysicalKey.D);
            Assert.That(controller.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.GitWordDiff));
            Press(window, PhysicalKey.D); Assert.That(controller.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Patch));
            Assert.That(changed, Is.EqualTo(2), "Attaching twice must not register the shortcut twice.");
            Press(window, PhysicalKey.T); Assert.That(changed, Is.EqualTo(2));
            controller.SetContext(true, true); Press(window, PhysicalKey.T);
            Assert.That(controller.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Difftastic));
            Press(window, PhysicalKey.T); Assert.That(controller.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Patch));
            controller.SetContext(false, true); Press(window, PhysicalKey.D); Press(window, PhysicalKey.T);
            Assert.That(controller.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Patch));
            controller.SetContext(true, true); outside.Focus(); Press(window, PhysicalKey.D);
            Assert.That(controller.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Patch));
            controller.Dispose(); viewer.Focus(); Press(window, PhysicalKey.D);
            Assert.That(controller.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Patch));
            Assert.That(viewer.Text, Is.EqualTo("Read-only viewer"));
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaTest]
    public void OriginalFrenchAppearanceSubmenuAndIconsRenderInBothThemesAndKeyboardActivatesTheRealMenu()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new();
        using DiffViewerAppearanceMenu controller = new(new DiffViewerPreferencesStore(directory.Path));
        controller.SetContext(true, false);
        IReadOnlyList<object> items = controller.CreateContextMenuItems();
        MenuItem appearance = items.OfType<MenuItem>().Single(item => item.Name == "DiffViewerAppearance");
        ContextMenu menu = new() { ItemsSource = items };
        TextBox viewer = new() { Text = "Diff", IsReadOnly = true, ContextMenu = menu };
        Window window = new() { Width = 1100, Height = 300, Content = viewer };
        try
        {
            Assert.That(((AccessText)appearance.Header!).Text, Is.EqualTo("_Apparence du diff"));
            Assert.That(((AccessText)Child("GitWordDiff").Header!).Text, Is.EqualTo("_Diff de mots Git"));
            Assert.That(((AccessText)Child("ReverseGitColoring").Header!).Text, Is.EqualTo("Inverser la couleur d'arrière-plan"));
            window.Show();
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme; OpenAppearance();
                MenuItem patch = Child("Patch"), word = Child("GitWordDiff");
                Assert.That(patch.IsEffectivelyVisible && patch.Bounds.Height == 24, Is.True);
                foreach (MenuItem item in appearance.Items.OfType<MenuItem>().Concat(items.OfType<MenuItem>().Where(item => item != appearance)))
                {
                    Assert.That(((AccessText)item.Header!).Text, Does.Not.Contain("&"));
                    Assert.That(item.Bounds.Height, Is.EqualTo(24));
                    if (item.Icon is Image icon) { Assert.That(icon.Bounds.Size, Is.EqualTo(new Size(16, 16))); Assert.That(icon.Source, Is.Not.Null); }
                }
                TopLevel root = (TopLevel)word.GetVisualRoot()!;
                Rect parentBounds = new(appearance.TranslatePoint(default, root)!.Value, appearance.Bounds.Size);
                Assert.That(patch.TranslatePoint(default, root)!.Value.X, Is.GreaterThanOrEqualTo(parentBounds.Right - 6),
                    "The submenu must be positioned after the context menu's current layout, rather than overlap its previous position.");
                Point selectedPoint = patch.TranslatePoint(new Point(patch.Bounds.Width - 20, patch.Bounds.Height / 2), root)!.Value;
                root.MouseMove(selectedPoint); root.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.That(patch.IsSelected, Is.True);
                using WriteableBitmap frame = Capture(root, "diff-viewer-appearance-fr-" + theme + ".png");
                AssertActiveCheck(frame, root, patch, theme);
                Assert.That(Child("Difftastic").IsEnabled, Is.False);
                word.Focus(); root.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); root.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Assert.That(controller.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.GitWordDiff));
                menu.Close();
                OpenAppearance();
                ClickRendered(patch); Assert.That(controller.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Patch));
                menu.Close();
            }
            controller.SetContext(false, false); Assert.That(appearance.IsVisible, Is.False);
        }
        finally { menu.Close(); window.Close(); CultureInfo.CurrentUICulture = previous; Dispatcher.UIThread.RunJobs(); }
        MenuItem Child(string suffix) => appearance.Items.OfType<MenuItem>().Concat(items.OfType<MenuItem>()).Single(item => item.Name == "DiffViewer" + suffix);
        void OpenAppearance()
        {
            // ContextMenu defaults to the pointer position. Mount and lay out the
            // parent before opening its submenu, especially after a theme change.
            window.MouseMove(new Point(8, 8)); menu.Open(viewer);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            appearance.IsSubMenuOpen = true; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTest]
    public void CombinedAndRangeViewersKeepGlobalColorSettingsAccessibleOutsideTheAppearanceSubmenu()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new();
        foreach (DiffViewMode mode in new[] { DiffViewMode.Combined, DiffViewMode.RangeDiff })
        {
            TextBox source = new() { Text = mode == DiffViewMode.Combined ? "diff --cc file\n@@@ -1 -1 +1 @@@\n++new\n" : "1: aaaaaaa ! 1: bbbbbbb Subject\n" };
            using DiffView viewer = new(source, new DiffViewerPreferencesStore(directory.Path)) { Mode = mode };
            ContextMenu menu = viewer.ContextMenu!;
            Window window = new() { Width = 800, Height = 300, Content = viewer };
            try
            {
                window.Show(); window.UpdateLayout(); menu.Open(viewer); Dispatcher.UIThread.RunJobs();
                Assert.That(viewer.EffectiveMode, Is.EqualTo(mode));
                Assert.That(Item("Appearance").IsVisible, Is.False, "These view modes cannot offer ordinary patch appearances.");
                Assert.That(Item("UseGitColoring").IsEffectivelyVisible && Item("ReverseGitColoring").IsEffectivelyVisible, Is.True);
                Assert.That(Item("UseGitColoring").IsChecked, Is.True);
                ClickRendered(Item("UseGitColoring"));
                Assert.That(viewer.DisplayOptions.UseGitColoring, Is.False);
                menu.Open(viewer); Dispatcher.UIThread.RunJobs();
                Assert.That(Item("ReverseGitColoring").IsEnabled, Is.False);
                ClickRendered(Item("UseGitColoring"));
                menu.Open(viewer); Dispatcher.UIThread.RunJobs(); ClickRendered(Item("ReverseGitColoring"));
                Assert.That(viewer.DisplayOptions.UseGitColoring, Is.True);
                Assert.That(viewer.DisplayOptions.ReverseGitColoring, Is.False);
                Assert.That(source.Text, Does.Contain(mode == DiffViewMode.Combined ? "@@@" : "aaaaaaa"));
                // Restore the global inversion preference for the next real viewer.
                menu.Open(viewer); Dispatcher.UIThread.RunJobs(); ClickRendered(Item("ReverseGitColoring"));
            }
            finally { menu.Close(); window.Close(); Dispatcher.UIThread.RunJobs(); }
            MenuItem Item(string suffix) => menu.ItemsSource!.OfType<MenuItem>().Single(item => item.Name == "DiffViewer" + suffix);
        }
    }

    private static void AssertActiveCheck(WriteableBitmap frame, TopLevel root, MenuItem patch, ThemeVariant theme)
    {
        IReadOnlyDictionary<string, string> source = ClassicStyles.OriginalColors(theme == ThemeVariant.Dark);
        Color background = Color.Parse(source["MenuHighlight"]), foreground = Color.Parse(source["HighlightText"]);
        Border line = patch.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_LayoutRoot");
        ContentControl icon = patch.GetVisualDescendants().OfType<ContentControl>().Single(control => control.Name == "PART_ToggleIconPresenter");
        Avalonia.Controls.Shapes.Path check = icon.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
        Assert.That(((ISolidColorBrush)line.Background!).Color, Is.EqualTo(background), "The selected template must actually draw the original highlight behind its check.");
        Assert.That(((ISolidColorBrush)check.Fill!).Color, Is.EqualTo(foreground));
        Assert.That(Math.Round(Contrast(foreground, background), 2), Is.GreaterThanOrEqualTo(4.5));
        Rect checkBounds = new(check.TranslatePoint(default, root)!.Value, check.Bounds.Size);
        using ILockedFramebuffer pixels = frame.Lock(); byte[] data = new byte[pixels.RowBytes * pixels.Size.Height];
        Marshal.Copy(pixels.Address, data, 0, data.Length); bool bgra = pixels.Format == PixelFormat.Bgra8888;
        double dr = foreground.R - background.R, dg = foreground.G - background.G, db = foreground.B - background.B, magnitude = dr * dr + dg * dg + db * db;
        int coverageCount = 0, backgroundCount = 0;
        for (int y = Math.Max(0, (int)Math.Floor(checkBounds.Y)); y < Math.Min(pixels.Size.Height, (int)Math.Ceiling(checkBounds.Bottom)); y++)
        {
            for (int x = Math.Max(0, (int)Math.Floor(checkBounds.X)); x < Math.Min(pixels.Size.Width, (int)Math.Ceiling(checkBounds.Right)); x++)
            {
                int offset = y * pixels.RowBytes + x * 4; double r = data[offset + (bgra ? 2 : 0)], g = data[offset + 1], b = data[offset + (bgra ? 0 : 2)];
                if (r == background.R && g == background.G && b == background.B) { backgroundCount++; }
                double coverage = ((r - background.R) * dr + (g - background.G) * dg + (b - background.B) * db) / magnitude;
                if (coverage >= .25 && coverage <= 1.05 && Math.Abs(r - (background.R + coverage * dr)) <= 3
                    && Math.Abs(g - (background.G + coverage * dg)) <= 3 && Math.Abs(b - (background.B + coverage * db)) <= 3) { coverageCount++; }
            }
        }
        Assert.That(backgroundCount, Is.GreaterThan(30), "The source background must reach the actual check-mark region.");
        Assert.That(coverageCount, Is.GreaterThan(8), "The active check mark must be drawn visibly, including antialiased edge pixels.");
    }

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        double a = Luminance(first), b = Luminance(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static MenuItem Item(MenuFlyout flyout, string name) => flyout.ItemsSource!.OfType<MenuItem>().Single(item => item.Name == "DiffViewer" + name);
    private static void Open(MenuFlyout flyout, Control target)
    { flyout.ShowAt(target); TopLevel.GetTopLevel(target)!.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void ClickRendered(Control control)
    {
        TopLevel root = (TopLevel)control.GetVisualRoot()!; root.UpdateLayout();
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point); root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static void Press(Window window, PhysicalKey key)
    { window.KeyPressQwerty(key, RawInputModifiers.Control); window.KeyReleaseQwerty(key, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs(); }
    private static WriteableBitmap Capture(TopLevel host, string name)
    {
        host.UpdateLayout(); Dispatcher.UIThread.RunJobs(); WriteableBitmap frame = host.CaptureRenderedFrame() ?? throw new AssertionException("No appearance-menu frame.");
        DirectoryInfo? root = new(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/linux/GitExtensions.Desktop/GitExtensions.Desktop.csproj"))) { root = root.Parent; }
        string directory = Path.Combine(root?.FullName ?? TestContext.CurrentContext.WorkDirectory, "artifacts/linux/screenshots"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name); frame.Save(path); TestContext.AddTestAttachment(path); return frame;
    }
}

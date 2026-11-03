using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

/// <summary>Exercise the visible Settings tree, including expansion, before editing a page.</summary>
internal static class SettingsNavigationTestDriver
{
    internal static async Task ReadyAsync(Window window, string controlName = "SaveDesktopSettings")
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        Control control = PushProcessTestDriver.Field<Control>(window, controlName);
        bool Ready() => control.IsEffectivelyVisible && control.IsEffectivelyEnabled;
        Pump(window);
        while (!Ready() && clock.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(10); Pump(window); }
        Pump(window);
        Assert.That(Ready(), Is.True, "The real Settings control did not become available: " + controlName);
    }

    internal static void Select(Window window, string id)
    {
        Pump(window);
        TreeView tree = PushProcessTestDriver.Field<TreeView>(window, "SettingsNavigationTree");
        TreeViewItem[] path = FindPath(tree.Items.OfType<TreeViewItem>(), "Settings" + id + "Node")
            ?? throw new AssertionException("Settings page not found in the navigation tree: " + id);
        foreach (TreeViewItem parent in path.SkipLast(1))
        {
            if (parent.IsExpanded) { continue; }
            PointerHeader(parent); Pump(window);
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); Pump(window);
            Assert.That(parent.IsExpanded, Is.True, "The actual Settings tree must expand with Right.");
        }
        TreeViewItem node = path[^1]; node.BringIntoView(); Pump(window); PointerHeader(node); Pump(window);
        Assert.That(tree.SelectedItem, Is.SameAs(node));
    }

    internal static void PointerHeader(TreeViewItem node)
    {
        TopLevel window = TopLevel.GetTopLevel(node)!;
        for (int frame = 0; frame < 10; frame++) { Pump(window); }
        Control header = node.Header as Control ?? node;
        Rect visible = VisibleHeaderRect(window, header);
        Point point = visible.Center;
        bool ready = visible.Width > 0 && visible.Height > 0 && header.IsEffectivelyVisible && node.IsEffectivelyEnabled
            && new Rect(default, window.ClientSize).Contains(point)
            && (window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(node) == true;
        string details = ready ? "" : CaptureHeaderFailure(window, node, header, point, visible);
        Assert.That(ready, Is.True, "Real Settings tree header pointer target: " + node.Name + details);
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump(window);
    }

    private static Rect VisibleHeaderRect(TopLevel window, Control header)
    {
        Rect visible = new(default, window.ClientSize);
        foreach (Visual visual in header.GetSelfAndVisualAncestors())
        {
            bool boundsClip = ReferenceEquals(visual, header) || visual.ClipToBounds || visual is ScrollViewer or ScrollContentPresenter;
            if (!boundsClip && visual.Clip is null) { continue; }
            if (visual.TransformToVisual(window) is not Matrix transform) { return default; }
            if (boundsClip)
            { visible = visible.Intersect(new Rect(default, visual.Bounds.Size).TransformToAABB(transform)); }
            if (visual.Clip is { } clip)
            { visible = visible.Intersect(clip.Bounds.TransformToAABB(transform)); }
            if (!(visible.Width > 0 && visible.Height > 0)) { return default; }
        }
        return double.IsFinite(visible.X) && double.IsFinite(visible.Y) && double.IsFinite(visible.Width) && double.IsFinite(visible.Height)
            ? visible : default;
    }

    private static string CaptureHeaderFailure(TopLevel window, TreeViewItem node, Control header, Point point, Rect visible)
    {
        try
        {
            // Passive snapshot of the failed guard. Do not pump/layout/reveal/focus or send any input here.
            Visual? hit = window.InputHitTest(point) as Visual;
            string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
            Directory.CreateDirectory(directory);
            string stem = "settings-header-failure-" + (node.Name ?? "unnamed") + "-" + Guid.NewGuid().ToString("N");
            string jsonPath = Path.Combine(directory, stem + ".json"), imagePath = Path.Combine(directory, stem + ".png");
            object snapshot = new
            {
                Test = TestContext.CurrentContext.Test.FullName,
                Culture = CultureInfo.CurrentCulture.Name,
                UiCulture = CultureInfo.CurrentUICulture.Name,
                ClientSize = SizeData(window.ClientSize),
                Point = PointData(point),
                VisibleHeaderRect = RectData(visible),
                Guard = new
                {
                    VisibleHeaderNonempty = visible.Width > 0 && visible.Height > 0,
                    HeaderEffectivelyVisible = header.IsEffectivelyVisible,
                    NodeEffectivelyEnabled = node.IsEffectivelyEnabled,
                    PointInsideClient = new Rect(default, window.ClientSize).Contains(point),
                    HitHasNodeAncestor = hit?.GetSelfAndVisualAncestors().Contains(node) == true
                },
                Header = Geometry(header, window, point),
                Node = Geometry(node, window, point),
                Hit = hit is null ? null : Geometry(hit, window, point),
                HeaderDescendants = header.GetVisualDescendants().Select(value => Geometry(value, window, point)).ToArray(),
                HeaderAncestors = header.GetVisualAncestors().Select(value => Geometry(value, window, point)).ToArray(),
                NodeAncestors = node.GetVisualAncestors().Select(value => Geometry(value, window, point)).ToArray(),
                HitAncestors = hit?.GetVisualAncestors().Select(value => Geometry(value, window, point)).ToArray(),
                ScrollViewers = header.GetSelfAndVisualAncestors().OfType<ScrollViewer>().Select(scroll => new
                {
                    Visual = Geometry(scroll, window, point),
                    Offset = PointData(new Point(scroll.Offset.X, scroll.Offset.Y)),
                    Viewport = SizeData(scroll.Viewport),
                    Extent = SizeData(scroll.Extent),
                    HorizontalVisibility = scroll.HorizontalScrollBarVisibility.ToString(),
                    VerticalVisibility = scroll.VerticalScrollBarVisibility.ToString()
                }).ToArray()
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.AddTestAttachment(jsonPath);
            string capture;
            try
            {
                // Read the already rendered frame from the existing ten pumps; no extra frame or wait is introduced.
                using var frame = window.CaptureRenderedFrame();
                if (frame is null) { capture = "no rendered frame"; }
                else { frame.Save(imagePath); TestContext.AddTestAttachment(imagePath); capture = imagePath; }
            }
            catch (Exception error) { capture = error.GetType().Name + ": " + error.Message; }
            string details = "\nPassive geometry: " + jsonPath + "\nExisting rendered frame: " + capture
                + "\npoint=" + FormattableString.Invariant($"{point.X:R},{point.Y:R}") + "; hit=" + hit?.GetType().Name + "#" + (hit as Control)?.Name;
            TestContext.Out.WriteLine(details); return details;
        }
        catch (Exception error)
        { return "\nPassive diagnostic failed: " + error.GetType().Name + ": " + error.Message; }
    }

    private static object Geometry(Visual value, TopLevel window, Point point)
    {
        Point? origin = value.TranslatePoint(default, window), localPoint = window.TranslatePoint(point, value);
        Rect? rect = origin is Point start ? new Rect(start, value.Bounds.Size) : null;
        return new
        {
            Type = value.GetType().FullName,
            Name = (value as Control)?.Name,
            Bounds = RectData(value.Bounds),
            WindowRect = rect is Rect actual ? RectData(actual) : null,
            LocalPoint = localPoint is Point localValue ? PointData(localValue) : null,
            PointInsideLocalBounds = localPoint is Point local && new Rect(default, value.Bounds.Size).Contains(local),
            value.IsVisible,
            value.IsEffectivelyVisible,
            IsEffectivelyEnabled = (value as InputElement)?.IsEffectivelyEnabled,
            IsHitTestVisible = (value as InputElement)?.IsHitTestVisible,
            value.ClipToBounds,
            ClipType = value.Clip?.GetType().FullName,
            ClipBounds = value.Clip is { } clip ? RectData(clip.Bounds) : null,
            RenderTransform = value.RenderTransform is { } transform ? new[] { transform.Value.M11, transform.Value.M12,
                transform.Value.M21, transform.Value.M22, transform.Value.M31, transform.Value.M32 } : null,
            TextLength = (value as TextBlock)?.Text?.Length,
            TextWrapping = (value as TextBlock)?.TextWrapping.ToString(),
            RenderTransformOrigin = value.RenderTransformOrigin.ToString()
        };
    }

    private static object PointData(Point point) => new { point.X, point.Y };
    private static object SizeData(Size size) => new { size.Width, size.Height };
    private static object RectData(Rect rect) => new { rect.X, rect.Y, rect.Width, rect.Height };

    private static TreeViewItem[]? FindPath(IEnumerable<TreeViewItem> nodes, string name)
    {
        foreach (TreeViewItem node in nodes)
        {
            if (node.Name == name) { return [node]; }
            if (FindPath(node.Items.OfType<TreeViewItem>(), name) is { } children) { return [node, .. children]; }
        }
        return null;
    }

    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}

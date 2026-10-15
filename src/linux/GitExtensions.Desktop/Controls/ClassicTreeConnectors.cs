using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop.Controls;

/// <summary>Non-interactive dotted hierarchy lines, measured from the native headers and clipped to their viewport.</summary>
internal sealed class ClassicTreeConnectors(TreeView tree, double indent = ClassicTreeViewAppearance.Indent, bool showRootLines = true) : Control
{
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        tree.LayoutUpdated += LayoutChanged;
        tree.AddHandler(ScrollViewer.ScrollChangedEvent, Scrolled);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        tree.LayoutUpdated -= LayoutChanged;
        tree.RemoveHandler(ScrollViewer.ScrollChangedEvent, Scrolled);
        base.OnDetachedFromVisualTree(e);
    }

    private void LayoutChanged(object? sender, EventArgs e) => InvalidateVisual();
    private void Scrolled(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!tree.TryFindResource("ClassicDisabledBrush", tree.ActualThemeVariant, out object? resource) || resource is not IBrush brush) { return; }
        // Never paint over scrollbars or outside the native content viewport.
        ScrollContentPresenter? viewport = tree.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault();
        if (viewport is null || viewport.TranslatePoint(default, this) is not Point origin) { return; }
        using (context.PushClip(new Rect(origin, viewport.Bounds.Size)))
        {
            Pen pen = new(brush, 1, new DashStyle([1d, 1d], 0));
            DrawSiblings(tree.Items.OfType<TreeViewItem>().ToArray(), null);

            void DrawSiblings(TreeViewItem[] siblings, Rect? parent)
            {
                var rows = siblings.Select(item => (Item: item, Header: Header(item))).Where(row => row.Header is not null).ToArray();
                if (rows.Length == 0) { return; }
                Rect first = rows[0].Header!.Value;
                double x = Math.Floor(first.X + indent / 2) + .5;
                double top = parent?.Bottom ?? first.Center.Y;
                foreach ((TreeViewItem item, Rect? bounds) in rows)
                {
                    Rect header = bounds!.Value; double y = Math.Floor(header.Center.Y) + .5;
                    bool expandable = item.Items.Count != 0;
                    double end = expandable ? y - 5 : y;
                    // Place dash boundaries on whole pixels: half-pixel endpoints
                    // blend adjacent 1px dashes into an apparently solid line.
                    bool draw = parent is not null || showRootLines;
                    if (draw && Math.Floor(end) > Math.Ceiling(top)) { context.DrawLine(pen, new Point(x, Math.Ceiling(top)), new Point(x, Math.Floor(end))); }
                    double start = expandable ? x + 5 : x;
                    if (draw) { context.DrawLine(pen, new Point(Math.Ceiling(start), y), new Point(Math.Floor(header.X + indent - 2), y)); }
                    top = expandable ? y + 5 : y;
                    if (item.IsExpanded) { DrawSiblings(item.Items.OfType<TreeViewItem>().ToArray(), header); }
                }
            }
        }
    }

    private Rect? Header(TreeViewItem item)
    {
        if (!item.IsEffectivelyVisible) { return null; }
        Grid? header = item.GetVisualDescendants().OfType<Grid>().FirstOrDefault(control => control.Name == "PART_Header" && control.TemplatedParent == item);
        return header is not null && header.Bounds.Height > 0 && header.TranslatePoint(default, this) is Point position ? new Rect(position, header.Bounds.Size) : null;
    }
}

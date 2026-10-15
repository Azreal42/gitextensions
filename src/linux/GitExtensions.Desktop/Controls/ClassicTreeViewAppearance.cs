using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Styling;

namespace GitExtensions.Linux.Desktop.Controls;

/// <summary>Explorer-like density on native TreeView templates; opt in without changing other trees.</summary>
internal static class ClassicTreeViewAppearance
{
    internal const double RowHeight = 18, Indent = 19, ChevronSize = 7;
    private const string TreeClass = "classic-tree";

    public static Control Wrap(TreeView tree, double indent = Indent, double inset = 6, bool showRootLines = true)
    {
        FuncValueConverter<int, Thickness> levelIndent = new(level => new Thickness(inset + Math.Max(0, level - (showRootLines ? 0 : 1)) * indent, 0, 0, 0));
        FuncValueConverter<int, double> gutterWidth = new(level => !showRootLines && level == 0 ? 0 : indent);
        tree.Classes.Add(TreeClass);
        // Keep the inset inside each header. ScrollViewer padding would make
        // focus/BringIntoView shift a narrow chevron underneath the pointer.
        tree.Padding = new Thickness(0);
        tree.BorderThickness = default;
        tree.UseLayoutRounding = true;
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Auto);
        tree.Styles.Add(new Styles
        {
            new Style(selector => Item(selector))
            {
                Setters =
                {
                    new Setter(Layoutable.MinHeightProperty, RowHeight),
                    new Setter(Layoutable.MarginProperty, new Thickness(0)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(0)),
                    new Setter(TemplatedControl.FontSizeProperty, 12d)
                }
            },
            // Height belongs to the header, not the whole TreeViewItem (which also contains its children).
            new Style(selector => Item(selector).Template().OfType<Border>().Name("PART_LayoutRoot"))
            {
                Setters = { new Setter(Layoutable.HeightProperty, RowHeight), new Setter(Layoutable.MinHeightProperty, RowHeight) }
            },
            new Style(selector => Item(selector).Template().OfType<Grid>().Name("PART_Header"))
            {
                Setters = { new Setter(Layoutable.MarginProperty, new Binding(nameof(TreeViewItem.Level))
                    { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent), Converter = levelIndent }) }
            },
            new Style(selector => Item(selector).Template().OfType<Panel>().Name("PART_ExpandCollapseChevronContainer"))
            {
                Setters =
                {
                    new Setter(Layoutable.WidthProperty, new Binding(nameof(TreeViewItem.Level))
                        { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent), Converter = gutterWidth }),
                    new Setter(Visual.ClipToBoundsProperty, true),
                    new Setter(Layoutable.MarginProperty, new Thickness(0)),
                    new Setter(Layoutable.VerticalAlignmentProperty, VerticalAlignment.Center)
                }
            },
            new Style(selector => Item(selector).Template().OfType<ToggleButton>().Name("PART_ExpandCollapseChevron"))
            {
                Setters =
                {
                    new Setter(Layoutable.WidthProperty, ChevronSize), new Setter(Layoutable.HeightProperty, ChevronSize),
                    new Setter(Layoutable.MinWidthProperty, 0d), new Setter(Layoutable.MinHeightProperty, 0d),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                    new Setter(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Center),
                    new Setter(Layoutable.VerticalAlignmentProperty, VerticalAlignment.Center)
                }
            }
        });
        return new Grid { ClipToBounds = true, Children = { tree, new ClassicTreeConnectors(tree, indent, showRootLines) { Name = "ClassicTreeConnectors", IsHitTestVisible = false } } };
    }

    private static Selector Item(Selector? selector) => selector.OfType<TreeView>().Class(TreeClass).Descendant().OfType<TreeViewItem>();
}

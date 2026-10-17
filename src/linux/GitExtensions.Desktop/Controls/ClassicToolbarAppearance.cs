using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop.Controls;

internal static class ClassicToolbarAppearance
{
    internal static Geometry DropdownGeometry { get; } = Geometry.Parse("M 0,0 L 6,0 L 3,3 Z");
    internal static PathIcon DropdownArrow(TemplatedControl owner, string name)
    {
        PathIcon arrow = new()
        {
            Name = name, Data = DropdownGeometry, Width = 6, Height = 3,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        arrow.Bind(PathIcon.ForegroundProperty, new Binding(nameof(TemplatedControl.Foreground)) { Source = owner });
        return arrow;
    }
}

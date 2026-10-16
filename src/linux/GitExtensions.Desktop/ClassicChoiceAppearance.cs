using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Styling;

namespace GitExtensions.Linux.Desktop;

internal static class ClassicChoiceAppearance
{
    internal static void Apply(Window window, string scope)
    {
        window.Classes.Add(scope);
        foreach (string name in new[] { "OuterEllipse", "CheckOuterEllipse", "CheckGlyph" })
        {
            double size = name == "CheckGlyph" ? 5 : 13;
            window.Styles.Add(new Style(selector => selector.OfType<Window>().Class(scope).Descendant().OfType<RadioButton>().Template().OfType<Ellipse>().Name(name))
            { Setters = { new Setter(Control.WidthProperty, size), new Setter(Control.HeightProperty, size), new Setter(Control.HorizontalAlignmentProperty, HorizontalAlignment.Center) } });
        }
        window.Styles.Add(new Style(selector => selector.OfType<Window>().Class(scope).Descendant().OfType<CheckBox>().Template().OfType<Border>().Name("NormalRectangle"))
        { Setters = { new Setter(Control.WidthProperty, 13d), new Setter(Control.HeightProperty, 13d), new Setter(Border.CornerRadiusProperty, new CornerRadius(2)), new Setter(Control.HorizontalAlignmentProperty, HorizontalAlignment.Center) } });
        window.Styles.Add(new Style(selector => selector.OfType<Window>().Class(scope).Descendant().OfType<CheckBox>().Template().OfType<Viewbox>())
        { Setters = { new Setter(Control.WidthProperty, 13d), new Setter(Control.HeightProperty, 13d), new Setter(Control.HorizontalAlignmentProperty, HorizontalAlignment.Center) } });
    }
}

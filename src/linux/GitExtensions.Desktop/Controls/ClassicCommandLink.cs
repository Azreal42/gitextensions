using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitExtensions.Linux.Desktop.Controls;

/// <summary>Shared native-task-dialog command links for the original commit and push decisions.</summary>
internal static class ClassicCommandLink
{
    internal static void Configure(Window dialog, string className, double minHeight, bool disabledForeground = false)
    {
        dialog.Styles.Add(new Style(selector => selector.OfType<Button>().Class(className))
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, Resource("Window")), new Setter(Button.ForegroundProperty, Resource("Text")),
                new Setter(Button.BorderBrushProperty, Brushes.Transparent), new Setter(Button.BorderThicknessProperty, new Thickness(1)),
                new Setter(Button.PaddingProperty, new Thickness(10)), new Setter(Button.FontSizeProperty, DesktopFonts.For(DesktopFontRole.Application).SizeInDips + 2),
                new Setter(Button.MinHeightProperty, minHeight), new Setter(Button.HorizontalAlignmentProperty, HorizontalAlignment.Stretch)
            }
        });
        dialog.Styles.Add(new Style(selector => selector.OfType<Button>().Class(className).Class(":pointerover"))
        { Setters = { new Setter(Button.BackgroundProperty, Resource("Hover")), new Setter(Button.BorderBrushProperty, Resource("ButtonBorder")) } });
        dialog.Styles.Add(new Style(selector => selector.OfType<Button>().Class(className).Class(":pressed"))
        { Setters = { new Setter(Button.BackgroundProperty, Resource("Pressed")) } });
        dialog.Styles.Add(new Style(selector => selector.OfType<Button>().Class(className).Class(":focus-visible"))
        { Setters = { new Setter(Button.BorderBrushProperty, Resource("Highlight")) } });
        if (disabledForeground)
        {
            dialog.Styles.Add(new Style(selector => selector.OfType<Button>().Class(className).Class(":disabled"))
            { Setters = { new Setter(Button.ForegroundProperty, Resource("Disabled")) } });
        }
        // Fluent's content presenter must not repaint its ordinary-button chrome.
        dialog.Styles.Add(new Style(selector => selector.OfType<Button>().Class(className).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent), new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent),
                new Setter(ContentPresenter.BorderThicknessProperty, new Thickness(0)), new Setter(ContentPresenter.PaddingProperty, new Thickness(0)) }
        });
    }

    internal static void Apply(Button button, string className)
    {
        button.Classes.Add(className);
        button.Template = Template();
    }

    internal static Panel ErrorIcon(VerticalAlignment verticalAlignment = VerticalAlignment.Stretch) => new()
    {
        Width = 32, Height = 32, VerticalAlignment = verticalAlignment, Children =
        {
            new Ellipse { Fill = Brushes.Red, Width = 32, Height = 32 },
            new PathIcon { Data = Geometry.Parse("M 1,3 L 3,1 L 9,7 L 15,1 L 17,3 L 11,9 L 17,15 L 15,17 L 9,11 L 3,17 L 1,15 L 7,9 Z"),
                Foreground = Brushes.White, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        }
    };

    private static DynamicResourceExtension Resource(string name) => new("Classic" + name + "Brush");

    private static FuncControlTemplate<Button> Template() => new((button, scope) =>
    {
        PathIcon arrow = new() { Data = Geometry.Parse("M 1,6 L 11,6 L 7,2 L 10,2 L 17,9 L 10,16 L 7,16 L 11,12 L 1,12 Z"), Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        arrow.Bind(PathIcon.ForegroundProperty, new TemplateBinding(Button.ForegroundProperty), BindingPriority.Template);
        ContentPresenter label = new() { Name = "PART_ContentPresenter", Content = button.Content, HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center };
        label.Bind(ContentPresenter.ForegroundProperty, new TemplateBinding(Button.ForegroundProperty), BindingPriority.Template); scope.Register(label.Name!, label);
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("18,12,*") }; content.Children.Add(arrow); Grid.SetColumn(label, 2); content.Children.Add(label);
        Border chrome = new() { Child = content, CornerRadius = new CornerRadius(3) };
        chrome.Bind(Border.BackgroundProperty, new TemplateBinding(Button.BackgroundProperty), BindingPriority.Template);
        chrome.Bind(Border.BorderBrushProperty, new TemplateBinding(Button.BorderBrushProperty), BindingPriority.Template);
        chrome.Bind(Border.BorderThicknessProperty, new TemplateBinding(Button.BorderThicknessProperty), BindingPriority.Template);
        chrome.Bind(Border.PaddingProperty, new TemplateBinding(Button.PaddingProperty), BindingPriority.Template); return chrome;
    });
}

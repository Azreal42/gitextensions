using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using GitExtensions.Linux.Desktop;
using Path = Avalonia.Controls.Shapes.Path;

namespace GitExtensions.Desktop.Controls;

/// <summary>Applies the original compact revision menu appearance to a native Avalonia menu.</summary>
public static class RevisionContextMenuAppearance
{
    private const string MenuClass = "revision-context-menu";
    private static readonly FontFamily MenuFont = new("Segoe UI, Noto Sans, DejaVu Sans");

    public static void Configure(ContextMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (menu.Classes.Contains(MenuClass)) { return; }
        menu.Classes.Add(MenuClass);

        // These are the dimensions consumed inside Fluent's native menu and
        // submenu templates. Resources on the menu also reach later additions
        // to MenuItem.Items, including items hosted in a submenu's Popup.
        menu.Resources["OverlayCornerRadius"] = new CornerRadius(0);
        menu.Resources["MenuFlyoutPresenterBorderThemeThickness"] = new Thickness(1);
        menu.Resources["MenuFlyoutPresenterThemePadding"] = new Thickness(0, 2);
        menu.Resources["MenuFlyoutScrollerMargin"] = new Thickness(0);
        menu.Resources["MenuFlyoutThemeMinHeight"] = 0d;
        menu.Resources["FlyoutThemeMaxWidth"] = double.PositiveInfinity;
        menu.Resources["MenuFlyoutItemThemePaddingNarrow"] = new Thickness(4, 0, 8, 0);
        menu.Resources["MenuIconPresenterMargin"] = new Thickness(0, 0, 6, 0);
        menu.Resources["MenuInputGestureTextMargin"] = new Thickness(24, 0, 0, 0);
        menu.Resources["MenuFlyoutItemChevronMargin"] = new Thickness(10, 0, 0, 0);
        menu.Resources["MenuFlyoutSubItemPopupHorizontalOffset"] = -1d;
        menu.Resources["MenuFlyoutSeparatorThemeHeight"] = 6d;
        menu.Resources["MenuFlyoutSeparatorThemePadding"] = new Thickness(26, 0, 6, 0);
        menu.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            ThemeDictionaries =
            {
                [ThemeVariant.Light] = Palette(dark: false),
                [ThemeVariant.Dark] = Palette(dark: true)
            }
        });

        // Keep the native menu templates, shared shortcut/chevron columns, popup
        // interaction, access keys and automation peers. The ancestor class
        // limits every override to this menu while giving template-part
        // setters precedence over Fluent's own template defaults.
        menu.Styles.Add(new Styles
        {
            new Style(selector => Root(selector))
            {
                Setters =
                {
                    new Setter(TemplatedControl.FontFamilyProperty, MenuFont),
                    new Setter(TemplatedControl.FontSizeProperty, 12d),
                    new Setter(TemplatedControl.BackgroundProperty, Resource("MenuFlyoutPresenterBackground")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("MenuFlyoutItemForeground")),
                    new Setter(TemplatedControl.BorderBrushProperty, Resource("MenuFlyoutPresenterBorderBrush")),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(0)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(0, 2)),
                    new Setter(Layoutable.MinHeightProperty, 0d),
                    new Setter(Layoutable.MaxWidthProperty, double.PositiveInfinity),
                    new Setter(Layoutable.UseLayoutRoundingProperty, true)
                }
            },
            new Style(selector => Item(selector))
            {
                Setters =
                {
                    new Setter(TemplatedControl.FontFamilyProperty, MenuFont),
                    new Setter(TemplatedControl.FontSizeProperty, 12d),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 0, 8, 0)),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(0)),
                    new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                    new Setter(TemplatedControl.BackgroundProperty, Resource("MenuFlyoutItemBackground")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("MenuFlyoutItemForeground")),
                    new Setter(Layoutable.HeightProperty, 22d),
                    new Setter(Layoutable.MinHeightProperty, 22d),
                    new Setter(Layoutable.MarginProperty, new Thickness(0)),
                    new Setter(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Stretch)
                }
            },
            new Style(selector => Item(selector).Class(":selected"))
            {
                Setters = { new Setter(TemplatedControl.ForegroundProperty, Resource("MenuFlyoutItemForegroundPointerOver")) }
            },
            new Style(selector => Item(selector).Class(":disabled"))
            {
                Setters = { new Setter(TemplatedControl.ForegroundProperty, Resource("MenuFlyoutItemForegroundDisabled")) }
            },
            // Always reserve the 16 px icon plus 6 px gap. Fluent normally
            // collapses this presenter when an entire submenu has no icons.
            new Style(selector => Item(selector).Template().OfType<ContentControl>().Name("PART_IconPresenter"))
            {
                Setters =
                {
                    new Setter(Visual.IsVisibleProperty, true),
                    new Setter(Layoutable.WidthProperty, 16d),
                    new Setter(Layoutable.HeightProperty, 16d),
                    new Setter(Layoutable.MarginProperty, new Thickness(0, 0, 6, 0)),
                    new Setter(Layoutable.VerticalAlignmentProperty, VerticalAlignment.Center)
                }
            },
            // Checks and radio dots share the icon gutter instead of adding a
            // second column that shifts the labels of the whole submenu.
            new Style(selector => Item(selector).Template().OfType<ContentControl>().Name("PART_ToggleIconPresenter"))
            {
                Setters =
                {
                    new Setter(Grid.ColumnProperty, 1),
                    new Setter(Layoutable.WidthProperty, 16d),
                    new Setter(Layoutable.HeightProperty, 16d),
                    new Setter(Layoutable.MarginProperty, new Thickness(0, 0, 6, 0))
                }
            },
            new Style(selector => Item(selector).Class(":checked").Class(":toggle").Template().OfType<ContentControl>().Name("PART_IconPresenter"))
            {
                Setters = { new Setter(Visual.IsVisibleProperty, false) }
            },
            new Style(selector => Item(selector).Class(":checked").Class(":radio").Template().OfType<ContentControl>().Name("PART_IconPresenter"))
            {
                Setters = { new Setter(Visual.IsVisibleProperty, false) }
            },
            new Style(selector => Item(selector).Class(":disabled").Template().OfType<ContentControl>().Name("PART_IconPresenter"))
            {
                Setters = { new Setter(Visual.OpacityProperty, .5d) }
            },
            new Style(selector => Item(selector).Template().OfType<TextBlock>().Name("PART_InputGestureText"))
            {
                Setters =
                {
                    new Setter(TextBlock.FontFamilyProperty, MenuFont),
                    new Setter(TextBlock.FontSizeProperty, 12d),
                    new Setter(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Right),
                    new Setter(Layoutable.MarginProperty, new Thickness(24, 0, 0, 0))
                }
            },
            new Style(selector => Item(selector).Template().OfType<Path>().Name("PART_ChevronPath"))
            {
                Setters =
                {
                    new Setter(Layoutable.WidthProperty, 5d),
                    new Setter(Layoutable.HeightProperty, 9d),
                    new Setter(Layoutable.MarginProperty, new Thickness(10, 0, 0, 0)),
                    new Setter(Layoutable.VerticalAlignmentProperty, VerticalAlignment.Center)
                }
            },
            new Style(selector => Root(selector).Descendant().OfType<Separator>())
            {
                Setters =
                {
                    new Setter(Layoutable.HeightProperty, 6d),
                    new Setter(Layoutable.MinHeightProperty, 6d),
                    new Setter(Layoutable.MarginProperty, new Thickness(0)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                    new Setter(TemplatedControl.BackgroundProperty, Resource("RevisionMenuSeparatorBrush")),
                    // Only the separator's single line needs a small template;
                    // it has no menu input or accessibility behavior to replace.
                    new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<Separator>((_, _) =>
                    {
                        Border line = new()
                        {
                            Height = 1,
                            Margin = new Thickness(26, 0, 6, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        line.Bind(Border.BackgroundProperty, new TemplateBinding(TemplatedControl.BackgroundProperty), BindingPriority.Template);
                        return line;
                    }))
                }
            },
            new Style(selector => Item(selector).Class(":separator"))
            {
                Setters =
                {
                    new Setter(Layoutable.HeightProperty, 6d),
                    new Setter(Layoutable.MinHeightProperty, 6d),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(0))
                }
            }
        });
    }

    private static Selector Root(Selector? selector) => selector.OfType<ContextMenu>().Class(MenuClass);
    private static Selector Item(Selector? selector) => Root(selector).Descendant().OfType<MenuItem>();
    private static DynamicResourceExtension Resource(string name) => new(name);

    private static ResourceDictionary Palette(bool dark)
    {
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(dark);
        ResourceDictionary resources = new();
        foreach ((string resource, string color) in new Dictionary<string, string>
        {
            ["MenuFlyoutPresenterBackground"] = "Menu",
            ["MenuFlyoutPresenterBorderBrush"] = "ActiveBorder",
            ["MenuFlyoutItemBackground"] = "Menu",
            ["MenuFlyoutItemBackgroundPointerOver"] = "MenuHighlight",
            ["MenuFlyoutItemBackgroundPressed"] = "MenuHighlight",
            ["MenuFlyoutItemBackgroundDisabled"] = "Menu",
            ["MenuFlyoutItemForeground"] = "MenuText",
            ["MenuFlyoutItemForegroundPointerOver"] = "HighlightText",
            ["MenuFlyoutItemForegroundPressed"] = "HighlightText",
            ["MenuFlyoutItemForegroundDisabled"] = "GrayText",
            ["MenuFlyoutItemKeyboardAcceleratorTextForeground"] = "MenuText",
            ["MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver"] = "HighlightText",
            ["MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed"] = "HighlightText",
            ["MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled"] = "GrayText",
            ["MenuFlyoutSubItemChevron"] = "MenuText",
            ["MenuFlyoutSubItemChevronPointerOver"] = "HighlightText",
            ["MenuFlyoutSubItemChevronPressed"] = "HighlightText",
            ["MenuFlyoutSubItemChevronSubMenuOpened"] = "HighlightText",
            ["MenuFlyoutSubItemChevronDisabled"] = "GrayText",
            ["RevisionMenuSeparatorBrush"] = "ControlDark"
        }) { resources[resource] = Brush.Parse(colors[color]); }
        return resources;
    }
}

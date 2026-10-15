using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Path = Avalonia.Controls.Shapes.Path;

namespace GitExtensions.Linux.Desktop.Controls;

/// <summary>Shared Windows menu metrics and colors for native menu bars, context menus and flyouts.</summary>
internal static class ClassicMenuAppearance
{
    private static readonly FontFamily MenuFont = new("Segoe UI, Noto Sans, DejaVu Sans");

    public static Styles Create()
    {
        Styles styles = new();
        // These are the dimensions consumed inside Fluent's native menu and
        // submenu templates. Application resources also reach later additions
        // to MenuItem.Items, including items hosted in a submenu's Popup.
        styles.Resources["OverlayCornerRadius"] = new CornerRadius(0);
        styles.Resources["MenuFlyoutPresenterBorderThemeThickness"] = new Thickness(1);
        styles.Resources["MenuFlyoutPresenterThemePadding"] = new Thickness(0, 2);
        styles.Resources["MenuFlyoutScrollerMargin"] = new Thickness(0);
        styles.Resources["MenuFlyoutThemeMinHeight"] = 0d;
        styles.Resources["FlyoutThemeMaxWidth"] = double.PositiveInfinity;
        styles.Resources["MenuFlyoutItemThemePaddingNarrow"] = new Thickness(4, 0, 8, 0);
        styles.Resources["MenuIconPresenterMargin"] = new Thickness(0, 0, 6, 0);
        styles.Resources["MenuInputGestureTextMargin"] = new Thickness(24, 0, 0, 0);
        styles.Resources["MenuFlyoutItemChevronMargin"] = new Thickness(10, 0, 0, 0);
        styles.Resources["MenuFlyoutSubItemPopupHorizontalOffset"] = -1d;
        styles.Resources["MenuFlyoutSeparatorThemeHeight"] = 6d;
        styles.Resources["MenuFlyoutSeparatorThemePadding"] = new Thickness(26, 0, 6, 0);
        styles.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            ThemeDictionaries =
            {
                [ThemeVariant.Light] = Palette(dark: false),
                [ThemeVariant.Dark] = Palette(dark: true)
            }
        });

        // Keep the native menu templates, shared shortcut/chevron columns, popup
        // interaction, access keys and automation peers. Ancestor selectors give
        // template-part setters precedence over Fluent's own defaults, including
        // dynamically populated items hosted in nested popups.
        styles.Add(new Styles
        {
            new Style(selector => selector.OfType<Menu>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Resource("MenuFlyoutPresenterBackground")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("MenuFlyoutItemForeground"))
                }
            },
            new Style(selector => selector.OfType<ContextMenu>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Resource("MenuFlyoutPresenterBackground")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("MenuFlyoutItemForeground")),
                    new Setter(TemplatedControl.BorderBrushProperty, Resource("MenuFlyoutPresenterBorderBrush"))
                }
            },
            new Style(selector => selector.OfType<MenuItem>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.FontSizeProperty, 12d),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(7, 3)),
                    new Setter(TemplatedControl.BackgroundProperty, Resource("MenuFlyoutPresenterBackground")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("MenuFlyoutItemForeground"))
                }
            },
            new Style(selector => selector.OfType<MenuItem>().Class(":selected"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Resource("MenuFlyoutItemBackgroundPointerOver")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("MenuFlyoutItemForegroundPointerOver"))
                }
            },
            new Style(selector => selector.OfType<MenuItem>().Class(":disabled"))
            {
                Setters = { new Setter(TemplatedControl.ForegroundProperty, Resource("MenuFlyoutItemForegroundDisabled")) }
            },

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
            // An item carrying both a check and an original bitmap needs both.
            // Keep Fluent's shared check column in that case, so selected diff
            // options never replace their glyph with a check mark.
            new Style(selector => Item(selector).Not(item => item.PropertyEquals(MenuItem.IconProperty, null))
                .Template().OfType<ContentControl>().Name("PART_ToggleIconPresenter"))
            {
                Setters = { new Setter(Grid.ColumnProperty, 0) }
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
            new Style(selector => selector.Is<MenuBase>().Descendant().OfType<Separator>())
            {
                Setters =
                {
                    new Setter(Layoutable.HeightProperty, 6d),
                    new Setter(Layoutable.MinHeightProperty, 6d),
                    new Setter(Layoutable.MarginProperty, new Thickness(0)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                    new Setter(TemplatedControl.BackgroundProperty, Resource("ClassicMenuSeparatorBrush")),
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
        return styles;
    }

    private static Selector Root(Selector? selector) => Selectors.Or(selector.OfType<ContextMenu>(), selector.OfType<MenuFlyoutPresenter>());
    // MenuBase also covers the horizontal menu bar. Its top-level buttons keep
    // the native horizontal layout; only popup rows receive an icon gutter.
    private static Selector Item(Selector? selector) => selector.Is<MenuBase>().Descendant().OfType<MenuItem>().Not(item => item.Class(":top-level"));
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
            ["ClassicMenuSeparatorBrush"] = "ControlDark"
        }) { resources[resource] = Brush.Parse(colors[color]); }
        return resources;
    }
}

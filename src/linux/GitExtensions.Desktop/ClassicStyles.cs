using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux.Desktop;

internal static class ClassicStyles
{
    public static Styles Create()
    {
        Styles styles =
        [
            new Style(selector => selector.OfType<Window>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.FontFamilyProperty, new FontFamily("Segoe UI, Noto Sans, DejaVu Sans")),
                    new Setter(TemplatedControl.FontSizeProperty, 12d),
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Window")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("Text"))
                }
            },
            new Style(selector => selector.OfType<Button>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.FontSizeProperty, 12d),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(5, 2)),
                    new Setter(Layoutable.MinHeightProperty, 24d),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(0)),
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Button")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("ControlText")),
                    new Setter(TemplatedControl.BorderBrushProperty, Resource("ButtonBorder"))
                }
            },
            new Style(selector => selector.OfType<Button>().Class(":pointerover"))
            {
                Setters = { new Setter(TemplatedControl.BackgroundProperty, Resource("Hover")) }
            },
            new Style(selector => selector.OfType<Button>().Class(":pressed"))
            {
                Setters = { new Setter(TemplatedControl.BackgroundProperty, Resource("Pressed")) }
            },
            new Style(selector => selector.OfType<Button>().Class(":disabled"))
            {
                Setters = { new Setter(TemplatedControl.ForegroundProperty, Resource("Disabled")) }
            },
            new Style(selector => selector.OfType<TextBox>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.FontSizeProperty, 12d),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 2)),
                    new Setter(Layoutable.MinHeightProperty, 24d),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(0)),
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Editor")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("Text")),
                    new Setter(TemplatedControl.BorderBrushProperty, Resource("Border")),
                    new Setter(TextBox.SelectionBrushProperty, Resource("Selection")),
                    new Setter(TextBox.SelectionForegroundBrushProperty, Resource("Text"))
                }
            },
            new Style(selector => selector.OfType<ListBox>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Window")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("Text")),
                    new Setter(TemplatedControl.BorderBrushProperty, Resource("Border"))
                }
            },
            new Style(selector => selector.OfType<TreeView>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Window")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("Text")),
                    new Setter(TemplatedControl.BorderBrushProperty, Resource("Border"))
                }
            },
            new Style(selector => selector.OfType<ComboBox>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Editor")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("Text")),
                    new Setter(TemplatedControl.BorderBrushProperty, Resource("Border")),
                    new Setter(Layoutable.MinHeightProperty, 24d),
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(0))
                }
            },
            new Style(selector => selector.OfType<ComboBox>().Class("classic-arrow-only").Template().OfType<PathIcon>().Name("DropDownGlyph"))
            {
                Setters =
                {
                    new Setter(Layoutable.MarginProperty, new Thickness(0)),
                    new Setter(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Center)
                }
            },
            new Style(selector => selector.OfType<TabControl>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Window")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("Text"))
                }
            },
            new Style(selector => selector.OfType<TabItem>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.FontSizeProperty, 12d),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(7, 3)),
                    new Setter(Layoutable.MinHeightProperty, 25d),
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Control")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("ControlText"))
                }
            },
            new Style(selector => selector.OfType<TabItem>().Class(":selected"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Window")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("Text"))
                }
            },
            new Style(selector => selector.OfType<Menu>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Menu")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("MenuText"))
                }
            },
            new Style(selector => selector.OfType<ContextMenu>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Menu")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("MenuText")),
                    new Setter(TemplatedControl.BorderBrushProperty, Resource("Border"))
                }
            },
            new Style(selector => selector.OfType<MenuItem>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.FontSizeProperty, 12d),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(7, 3)),
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Menu")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("MenuText"))
                }
            },
            new Style(selector => selector.OfType<MenuItem>().Class(":selected"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Resource("MenuHighlight")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("MenuHighlightText"))
                }
            },
            new Style(selector => selector.OfType<MenuItem>().Class(":disabled"))
            {
                Setters = { new Setter(TemplatedControl.ForegroundProperty, Resource("Disabled")) }
            },
            new Style(selector => selector.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 1)),
                    new Setter(Layoutable.MinHeightProperty, 24d),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("Text"))
                }
            },
            new Style(selector => selector.OfType<ListBoxItem>().Class(":selected"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Highlight")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("HighlightText"))
                }
            },
            // Fluent sets selected text on the inner presenter independently of
            // ListBoxItem.Foreground. Keep that actual template surface readable.
            new Style(selector => selector.OfType<ListBoxItem>().Class(":selected").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Setter(ContentPresenter.ForegroundProperty, Resource("HighlightText")) }
            },
            new Style(selector => selector.OfType<TreeViewItem>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 1)),
                    new Setter(Layoutable.MinHeightProperty, 24d),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("Text"))
                }
            },
            new Style(selector => selector.OfType<TreeViewItem>().Class(":selected"))
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Highlight")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("HighlightText"))
                }
            },
            new Style(selector => selector.OfType<CheckBox>())
            {
                Setters =
                {
                    new Setter(Layoutable.MinHeightProperty, 24d),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("ControlText"))
                }
            },
            new Style(selector => selector.OfType<RadioButton>())
            {
                Setters =
                {
                    new Setter(Layoutable.MinHeightProperty, 24d),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("ControlText"))
                }
            },
            new Style(selector => selector.OfType<Border>())
            {
                Setters = { new Setter(Border.BorderBrushProperty, Resource("Border")) }
            },
            new Style(selector => selector.OfType<ToolTip>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.BackgroundProperty, Resource("Tooltip")),
                    new Setter(TemplatedControl.ForegroundProperty, Resource("TooltipText")),
                    new Setter(TemplatedControl.BorderBrushProperty, Resource("Border"))
                }
            },
            new Style(selector => selector.OfType<ToolTip>().Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Setter(ContentPresenter.ForegroundProperty, Resource("TooltipText")) }
            }
        ];
        IReadOnlyDictionary<string, string> light = OriginalColors(dark: false);
        IReadOnlyDictionary<string, string> dark = OriginalColors(dark: true);
        styles.Resources = new ResourceDictionary
        {
            ThemeDictionaries =
            {
                [ThemeVariant.Light] = Resources(light, dark: false),
                [ThemeVariant.Dark] = Resources(dark, dark: true)
            }
        };
        return styles;
    }

    private static DynamicResourceExtension Resource(string name) => new("Classic" + name + "Brush");

    internal static IReadOnlyDictionary<string, string> OriginalColors(bool dark)
    {
        Dictionary<string, string> colors = ReadColors(ReadCss("invariant.css"), includeSystemTable: false);
        if (dark)
        {
            foreach ((string name, string value) in ReadColors(ReadCss("dark.css"), includeSystemTable: true)) { colors[name] = value; }
        }
        return colors;
    }

    private static ResourceDictionary Resources(IReadOnlyDictionary<string, string> colors, bool dark)
    {
        // The application colors come from CSS; native system colors come from the
        // explicit light CSS and SystemColorMode.Dark table documented in dark.css.
        Dictionary<string, string> names = new(StringComparer.Ordinal)
        {
            ["Window"] = "PanelBackground", ["Editor"] = "EditorBackground", ["Text"] = "WindowText",
            ["Control"] = "Control", ["ControlText"] = "ControlText", ["Menu"] = "Menu",
            ["MenuText"] = "MenuText", ["Border"] = "ActiveBorder", ["Button"] = "ButtonFace",
            ["ButtonBorder"] = "ButtonShadow", ["Hover"] = "ControlLight", ["Pressed"] = "ControlDark",
            ["Disabled"] = "GrayText", ["Highlight"] = "Highlight", ["HighlightText"] = dark ? "WindowText" : "HighlightText",
            ["MenuHighlightText"] = "HighlightText",
            ["MenuHighlight"] = "MenuHighlight", ["Selection"] = "Selection",
            ["Tooltip"] = "Info", ["TooltipText"] = "InfoText"
        };
        ResourceDictionary resources = new();
        // Fluent also uses these dimensions inside its templates. A 24 px
        // control otherwise clips glyphs still laid out in a 32 px container.
        resources["CheckBoxMinHeight"] = 24d;
        resources["RadioButtonMinHeight"] = 24d;
        foreach ((string resource, string color) in names)
        {
            resources["Classic" + resource + "Brush"] = Brush.Parse(colors[color]);
        }

        // The Fluent 11 templates resolve these resources on their own presenters,
        // selection pipes and focus borders; control-level setters cannot override
        // them. Bind those rendered states to the original palette, not the OS accent.
        foreach (string suffix in new[] { "", "Light1", "Light2", "Light3", "Dark1", "Dark2", "Dark3" })
        {
            resources["SystemAccentColor" + suffix] = Color.Parse(colors["Highlight"]);
        }
        Dictionary<string, string> templateBrushes = new(StringComparer.Ordinal)
        {
            ["ToolTipBackground"] = "Tooltip",
            ["ToolTipForeground"] = "TooltipText",
            ["ToolTipBorderBrush"] = "Border",
            ["SystemControlHighlightListAccentLowBrush"] = "Highlight",
            ["SystemControlHighlightListAccentMediumBrush"] = "Highlight",
            ["SystemControlHighlightListAccentHighBrush"] = "Highlight",
            ["SystemControlHighlightAltListAccentLowBrush"] = "Highlight",
            ["SystemControlHighlightAltListAccentMediumBrush"] = "Highlight",
            ["SystemControlHighlightAltListAccentHighBrush"] = "Highlight",
            ["SystemControlHighlightAccentBrush"] = "Highlight",
            ["SystemControlHighlightAltAccentBrush"] = "Highlight",
            ["TreeViewItemBackgroundSelected"] = "Highlight",
            ["TreeViewItemBackgroundSelectedPointerOver"] = "Highlight",
            ["TreeViewItemBackgroundSelectedPressed"] = "Highlight",
            ["TreeViewItemForegroundSelected"] = "HighlightText",
            ["TreeViewItemForegroundSelectedPointerOver"] = "HighlightText",
            ["TreeViewItemForegroundSelectedPressed"] = "HighlightText",
            ["TabItemHeaderSelectedPipeFill"] = "Highlight",
            ["TextControlBorderBrushFocused"] = "Highlight",
            ["TextControlBackgroundFocused"] = "Editor",
            ["TextControlForegroundFocused"] = "Text",
            ["CheckBoxCheckBackgroundFillChecked"] = "Highlight",
            ["CheckBoxCheckBackgroundFillCheckedPointerOver"] = "Highlight",
            ["CheckBoxCheckBackgroundFillCheckedPressed"] = "Highlight",
            ["CheckBoxCheckBackgroundStrokeCheckedPointerOver"] = "Highlight",
            ["CheckBoxCheckBackgroundStrokeCheckedPressed"] = "Highlight",
            ["CheckBoxCheckBackgroundFillIndeterminate"] = "Highlight",
            ["CheckBoxCheckBackgroundFillIndeterminatePointerOver"] = "Highlight",
            ["CheckBoxCheckBackgroundFillIndeterminatePressed"] = "Highlight",
            ["CheckBoxCheckBackgroundStrokeIndeterminate"] = "Highlight",
            ["CheckBoxCheckBackgroundStrokeIndeterminatePointerOver"] = "Highlight",
            ["CheckBoxCheckBackgroundStrokeIndeterminatePressed"] = "Highlight"
        };
        foreach ((string resource, string classic) in templateBrushes)
        {
            resources[resource] = resources["Classic" + classic + "Brush"];
        }
        foreach (string state in new[] { "", "PointerOver", "Pressed" })
        {
            resources["RadioButtonOuterEllipseCheckedFill" + state] = resources["ClassicHighlightBrush"];
            resources["RadioButtonOuterEllipseCheckedStroke" + state] = resources["ClassicHighlightBrush"];
            resources["RadioButtonCheckGlyphFill" + state] = resources["ClassicHighlightTextBrush"];
            resources["RadioButtonCheckGlyphStroke" + state] = resources["ClassicHighlightTextBrush"];
        }
        return resources;
    }

    private static string ReadCss(string file)
    {
        using StreamReader reader = new(AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Themes/" + file)));
        return reader.ReadToEnd();
    }

    private static Dictionary<string, string> ReadColors(string css, bool includeSystemTable)
    {
        Dictionary<string, string> colors = new(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(css, @"^\.(?<name>\w+)\s*\{\s*color:\s*(?<color>#[a-fA-F0-9]{6});\s*\}", RegexOptions.Multiline | RegexOptions.CultureInvariant))
        {
            colors[match.Groups["name"].Value] = match.Groups["color"].Value;
        }
        if (includeSystemTable)
        {
            foreach (Match match in Regex.Matches(css, @"^(?<name>\w+): ff(?<color>[a-fA-F0-9]{6})\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant))
            {
                colors[match.Groups["name"].Value] = "#" + match.Groups["color"].Value;
            }
        }
        return colors;
    }
}

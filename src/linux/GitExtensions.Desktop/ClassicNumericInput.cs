using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace GitExtensions.Linux.Desktop;

internal static class ClassicNumericInput
{
    internal static NumericUpDown Create(string name, int minimum, int maximum, int increment = 1,
        double width = 61, string format = "0", bool clipValueToMinMax = true)
        => new()
        {
            Name = name, Minimum = minimum, Maximum = maximum, Increment = increment, Width = width, Height = 24, MinHeight = 24,
            FormatString = format, ClipValueToMinMax = clipValueToMinMax,
            Template = new FuncControlTemplate<NumericUpDown>((owner, scope) =>
            {
                TextBox text = new()
                {
                    Name = "PART_TextBox", Height = 22, MinHeight = 0, MinWidth = 0, Padding = new Thickness(2, 0),
                    BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center,
                    TextAlignment = owner.TextAlignment
                };
                // Single-line TextBox consumes Up/Down before the spinner. Keep
                // Windows' keyboard increment while preserving invalid drafts.
                text.AddHandler(InputElement.KeyDownEvent, (_, args) =>
                {
                    if (args.KeyModifiers != KeyModifiers.None || args.Key is not (Key.Up or Key.Down)
                        || !owner.AllowSpin || owner.IsReadOnly) { return; }
                    if (decimal.TryParse(text.Text, owner.ParsingNumberStyle, owner.NumberFormat, out decimal value)
                        && value >= owner.Minimum && value <= owner.Maximum)
                    { owner.Value = Math.Clamp(value + (args.Key == Key.Up ? owner.Increment : -owner.Increment), owner.Minimum, owner.Maximum); }
                    args.Handled = true;
                }, RoutingStrategies.Tunnel);
                ButtonSpinner spinner = new()
                {
                    Name = "PART_Spinner", Content = text, AllowSpin = true, MinWidth = 0, MinHeight = 0, Padding = default, Margin = default,
                    Template = new FuncControlTemplate<ButtonSpinner>((host, spinnerScope) =>
                    {
                        RepeatButton increase = Arrow("PART_IncreaseButton", "▴"), decrease = Arrow("PART_DecreaseButton", "▾");
                        spinnerScope.Register(increase.Name!, increase); spinnerScope.Register(decrease.Name!, decrease);
                        StackPanel arrows = new() { Width = 14, Children = { increase, decrease } };
                        Grid panel = new() { ColumnDefinitions = new("*,14") };
                        panel.Children.Add(new ContentPresenter { Content = host.Content, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                        Grid.SetColumn(arrows, 1); panel.Children.Add(arrows); return panel;
                    })
                };
                scope.Register(text.Name!, text); scope.Register(spinner.Name!, spinner);
                Border border = ClassicChrome.Border(spinner, new Thickness(1));
                border.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ClassicWindowBrush"));
                return border;
                static RepeatButton Arrow(string part, string glyph) => new()
                {
                    Name = part, Content = glyph, Height = 11, MinHeight = 0, MinWidth = 0, FontSize = 8,
                    Padding = default, Margin = default, Focusable = false,
                    HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
                };
            })
        };
}

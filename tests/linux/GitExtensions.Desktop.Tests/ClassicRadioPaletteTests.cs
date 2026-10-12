using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class ClassicRadioPaletteTests
{
    [AvaloniaTest]
    public void CheckedRadioButtonsKeepTheirHistoricalPaletteAcrossHoverPressDisableAndThemeChanges()
    {
        FluentTheme fluent = Application.Current!.Styles.OfType<FluentTheme>().Single();
        fluent.Palettes.TryGetValue(ThemeVariant.Light, out ColorPaletteResources? previousLight);
        fluent.Palettes.TryGetValue(ThemeVariant.Dark, out ColorPaletteResources? previousDark);
        Color orange = Color.Parse("#e95420");
        fluent.Palettes[ThemeVariant.Light] = new() { Accent = orange };
        fluent.Palettes[ThemeVariant.Dark] = new() { Accent = orange };
        RadioButton first = new() { Content = "Local branch", GroupName = "Choice", IsChecked = true, Height = 24 };
        RadioButton second = new() { Content = "Remote branch", GroupName = "Choice", Height = 24 };
        Window window = new() { Width = 320, Height = 160, Content = new StackPanel { Margin = new(10), Children = { first, second } } };
        try
        {
            window.Show();
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = theme; Pump(window);
                Assert.That(((IResourceNode)fluent).TryGetResource("SystemAccentColor", theme, out object? accent), Is.True);
                Assert.That(accent, Is.EqualTo(orange));
                IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(theme == ThemeVariant.Dark);
                Color highlight = Color.Parse(colors["Highlight"]);
                foreach (RadioButton radio in new[] { first, second })
                {
                    radio.IsChecked = true; Pump(window); AssertChecked();
                    Point point = radio.TranslatePoint(new Point(10, 10), window)!.Value;
                    window.MouseMove(point); Pump(window); AssertChecked();
                    window.MouseDown(point, MouseButton.Left); Pump(window); AssertChecked();
                    window.MouseUp(point, MouseButton.Left); window.MouseMove(new Point(300, 140)); Pump(window);
                    radio.IsEnabled = false; Pump(window);
                    Assert.That(Outer(radio).Fill, Is.Not.Null);
                    Assert.That(((ISolidColorBrush)Outer(radio).Fill!).Color, Is.Not.EqualTo(orange));
                    radio.IsEnabled = true; Pump(window); AssertChecked();
                    void AssertChecked()
                    {
                        Assert.That(((ISolidColorBrush)Outer(radio).Fill!).Color, Is.EqualTo(highlight), "Every dialog must draw the original checked/hover/pressed color.");
                        Assert.That(((ISolidColorBrush)Outer(radio).Stroke!).Color, Is.EqualTo(highlight));
                    }
                }
                Capture(window, "classic-radio-" + theme + ".png");
            }
        }
        finally
        {
            window.Close();
            if (previousLight is null) { fluent.Palettes.Remove(ThemeVariant.Light); } else { fluent.Palettes[ThemeVariant.Light] = previousLight; }
            if (previousDark is null) { fluent.Palettes.Remove(ThemeVariant.Dark); } else { fluent.Palettes[ThemeVariant.Dark] = previousDark; }
        }
    }

    private static Avalonia.Controls.Shapes.Ellipse Outer(RadioButton radio)
        => radio.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single(ellipse => ellipse.Name == "CheckOuterEllipse");
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Capture(Window window, string name)
    {
        using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
}

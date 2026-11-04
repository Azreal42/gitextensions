using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public class ClassicStylesTests
{
    [AvaloniaTest, NonParallelizable]
    [TestCase(false)]
    [TestCase(true)]
    public void ChoiceLabelsKeepTheirGapAndRemainClickableWithStandardAndCompactGlyphs(bool compact)
    {
        CheckBox plain = new() { Content = "Enable option", HorizontalAlignment = HorizontalAlignment.Left };
        CheckBox wrapped = new() { Content = new TextBlock { Text = "A longer translated option that wraps over multiple lines", TextWrapping = TextWrapping.Wrap, MaxWidth = 180 } };
        CheckBox threeState = new() { Content = "Inherit from global settings", IsThreeState = true, IsChecked = null };
        CheckBox disabled = new() { Content = "Unavailable option", IsEnabled = false };
        RadioButton radio = new() { Content = new AccessText { Text = "_Remote branch" }, GroupName = "ChoiceSpacing" };
        CheckBox tableCell = new() { Padding = default, HorizontalAlignment = HorizontalAlignment.Left };
        CheckBox compactTableCell = new() { HorizontalAlignment = HorizontalAlignment.Left };
        ReferenceDialogControls.Compact(compactTableCell);
        ToggleButton[] labelled = [plain, wrapped, threeState, disabled, radio];
        ReferenceDialogControls.Compact(plain, threeState, radio);
        Window window = new()
        {
            Width = 400, Height = 340,
            Content = new StackPanel { Spacing = 8, Margin = new Thickness(8), Children = { plain, wrapped, threeState, disabled, radio, tableCell, compactTableCell } }
        };
        if (compact) { ClassicChoiceAppearance.Apply(window, "choice-spacing-preview"); }
        window.Show();
        try
        {
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                foreach (bool? state in new bool?[] { false, true, null })
                {
                    threeState.IsChecked = state; window.UpdateLayout();
                    foreach (ToggleButton choice in labelled)
                    {
                        Control glyph = choice.GetVisualDescendants().OfType<Control>().Single(control => control.Name == (choice is RadioButton ? "OuterEllipse" : "NormalRectangle"));
                        ContentPresenter label = choice.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "PART_ContentPresenter");
                        Assert.That(Bounds(label).Left - Bounds(glyph).Right, Is.GreaterThanOrEqualTo(5.9), choice.Content?.ToString());
                        Assert.That(label.Bounds.Width, Is.GreaterThan(0));
                    }
                }
                Assert.That(tableCell.Bounds.Width, Is.LessThanOrEqualTo(24), "Unlabelled table choices must retain their compact width.");
                Assert.That(compactTableCell.Bounds.Width, Is.EqualTo(tableCell.Bounds.Width), "The shared compact helper must not widen an unlabelled table choice.");
                plain.IsChecked = false;
                ContentPresenter text = plain.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "PART_ContentPresenter");
                Point point = Bounds(text).Center;
                window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                Assert.That(plain.IsChecked, Is.True, "The label remains part of the checkbox hit target.");
                using WriteableBitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
                string folder = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "choice-spacing-" + (compact ? "compact-" : "standard-") + theme + ".png");
                frame!.Save(path); TestContext.AddTestAttachment(path);
            }
        }
        finally { window.Close(); }
        return;

        Rect Bounds(Control control) => new(control.TranslatePoint(default, window)
            ?? throw new InvalidOperationException("Detached choice control."), control.Bounds.Size);
    }

    [AvaloniaTest, NonParallelizable]
    public void OrangePlatformAccentCannotReplaceRenderedHistoricalSelectionAndFocusColors()
    {
        FluentTheme fluent = Application.Current!.Styles.OfType<FluentTheme>().Single();
        fluent.Palettes.TryGetValue(ThemeVariant.Light, out ColorPaletteResources? previousLight);
        fluent.Palettes.TryGetValue(ThemeVariant.Dark, out ColorPaletteResources? previousDark);
        // Fluent's palette exposes the same SystemAccentColor consumed by templates
        // when Linux platform settings provide Ubuntu's orange accent.
        Color orange = Color.Parse("#e95420");
        fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = orange };
        fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = orange };
        ListBox files = new() { ItemsSource = new[] { "Other commit", "Selected commit" }, SelectedIndex = 1, Height = 70 };
        TreeViewItem branch = new() { Header = "Selected branch", IsSelected = true };
        TreeView references = new() { ItemsSource = new[] { branch }, SelectedItem = branch, Height = 50 };
        TabItem diff = new() { Header = "Diff", Content = new TextBlock { Text = "Changes" } };
        TabControl tabs = new() { ItemsSource = new[] { diff, new TabItem { Header = "File tree" } }, SelectedIndex = 0, Height = 90 };
        TextBox editor = new() { Text = "Focused message editor", Height = 40 };
        CheckBox amend = new() { Content = "Amend commit", IsChecked = true };
        Window window = new()
        {
            Width = 560, Height = 350,
            Content = new StackPanel { Spacing = 6, Children = { files, references, tabs, editor, amend } }
        };
        try
        {
            window.Show();
            foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Assert.That(((IResourceNode)fluent).TryGetResource("SystemAccentColor", variant, out object? accent), Is.True);
                Assert.That(accent, Is.EqualTo(orange), "The underlying Fluent accent must really be orange for this regression test.");
                window.RequestedThemeVariant = variant;
                window.UpdateLayout();
                references.SelectedItem = branch;
                branch.IsSelected = true;
                window.UpdateLayout();
                Assert.That(branch.IsSelected, Is.True);
                ListBoxItem selected = (ListBoxItem)files.ContainerFromIndex(1)!;
                selected.Focus();
                window.UpdateLayout();
                ContentPresenter presenter = selected.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "PART_ContentPresenter");
                Border treeBackground = branch.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_LayoutRoot");
                ContentPresenter treeText = branch.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "PART_HeaderPresenter");
                Border pipe = diff.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_SelectedPipe");
                Color highlight = Color.Parse(variant == ThemeVariant.Dark ? "#2864b4" : "#0078d7");
                Color text = Color.Parse(variant == ThemeVariant.Dark ? "#f0f0f0" : "#ffffff");
                Assert.Multiple(() =>
                {
                    Assert.That(ColorOf(presenter.Background), Is.EqualTo(highlight), "The inner presenter, not only ListBoxItem.Background, draws the selected row.");
                    Assert.That(presenter.Background!.Opacity, Is.EqualTo(1));
                    Assert.That(ColorOf(presenter.Foreground), Is.EqualTo(text));
                    Assert.That(ColorOf(treeBackground.Background), Is.EqualTo(highlight));
                    Assert.That(ColorOf(treeText.Foreground), Is.EqualTo(text));
                    Assert.That(ColorOf(pipe.Background), Is.EqualTo(highlight));
                    Assert.That(Math.Round(Contrast(text, highlight), 2), Is.GreaterThanOrEqualTo(4.5));
                });
                Point rowPoint = presenter.TranslatePoint(new Point(presenter.Bounds.Width - 20, presenter.Bounds.Height / 2), window)!.Value;
                window.MouseMove(rowPoint);
                window.MouseDown(rowPoint, MouseButton.Left);
                window.UpdateLayout();
                Assert.That(ColorOf(presenter.Background), Is.EqualTo(highlight), "Selected and pressed rows must also retain the original highlight.");
                Assert.That(ColorOf(presenter.Foreground), Is.EqualTo(text));
                window.MouseUp(rowPoint, MouseButton.Left);
                window.MouseMove(new Point(window.Width - 5, window.Height - 5));
                editor.Focus();
                window.UpdateLayout();
                Border editorBorder = editor.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_BorderElement");
                Assert.That(ColorOf(editorBorder.BorderBrush), Is.EqualTo(highlight), "The actual focused template border must ignore the OS accent.");
                Assert.That(ColorOf(editorBorder.Background), Is.EqualTo(Color.Parse(variant == ThemeVariant.Dark ? "#323232" : "#ffffff")));
                Border check = amend.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "NormalRectangle");
                Assert.That(ColorOf(check.Background), Is.EqualTo(highlight), "Amend's checked marker must not retain the orange accent either.");
                using WriteableBitmap? frame = window.CaptureRenderedFrame();
                Assert.That(frame, Is.Not.Null);
                Rect rowBounds = new(presenter.TranslatePoint(default, window)!.Value, presenter.Bounds.Size);
                Rect pipeBounds = new(pipe.TranslatePoint(default, window)!.Value, pipe.Bounds.Size);
                Assert.That(PixelAt(frame!, (int)(rowBounds.Right - 20), (int)rowBounds.Center.Y), Is.EqualTo(highlight));
                Assert.That(PixelAt(frame!, (int)pipeBounds.Center.X, (int)pipeBounds.Center.Y), Is.EqualTo(highlight));
                Assert.That(CountPixels(frame!, rowBounds, text), Is.GreaterThan(0), "Selected text must actually render with the readable historical foreground.");
                Rect checkBounds = new(check.TranslatePoint(default, window)!.Value, check.Bounds.Size);
                Assert.That(CountPixels(frame!, checkBounds, highlight), Is.GreaterThan(50));
                Assert.That(CountPixels(frame!, new Rect(0, 0, window.Bounds.Width, window.Bounds.Height), orange), Is.Zero);
                string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "classic-orange-accent-" + variant + ".png");
                frame!.Save(path);
                TestContext.AddTestAttachment(path, "Historical colors with an orange Fluent platform accent");
            }
        }
        finally
        {
            window.Close();
            if (previousLight is null) { fluent.Palettes.Remove(ThemeVariant.Light); }
            else { fluent.Palettes[ThemeVariant.Light] = previousLight; }
            if (previousDark is null) { fluent.Palettes.Remove(ThemeVariant.Dark); }
            else { fluent.Palettes[ThemeVariant.Dark] = previousDark; }
        }
    }

    [AvaloniaTest]
    public void OriginalPaletteUpdatesAllCommonSurfacesAndKeepsTextReadableInBothThemes()
    {
        Menu menu = new() { ItemsSource = new[] { new MenuItem { Header = "Repository" }, new MenuItem { Header = "Commands" } } };
        TextBox editor = new() { Text = "Editor text stays readable", Height = 64 };
        ListBox files = new() { ItemsSource = new[] { "File list" }, Height = 60 };
        TreeView references = new() { ItemsSource = new[] { new TreeViewItem { Header = "Branches" } }, Height = 60 };
        Button button = new() { Content = "Commit", Height = 28 };
        Window window = new()
        {
            Width = 560, Height = 350, RequestedThemeVariant = ThemeVariant.Light,
            Content = new StackPanel { Spacing = 6, Children = { menu, editor, files, references, button } }
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            AssertPalette("#ffffff", "#f0f0f0", "#000000", "#b4b4b4", "#f0f0f0");
            SaveFrame("classic-surfaces-light.png", "#ffffff", "#f0f0f0");
            window.RequestedThemeVariant = ThemeVariant.Dark;
            window.UpdateLayout();
            AssertPalette("#323232", "#373737", "#f0f0f0", "#464646", "#202020");
            SaveFrame("classic-surfaces-dark.png", "#323232", "#373737", "#202020");
            window.RequestedThemeVariant = ThemeVariant.Light;
            window.UpdateLayout();
            AssertPalette("#ffffff", "#f0f0f0", "#000000", "#b4b4b4", "#f0f0f0");
        }
        finally { window.Close(); }

        void AssertPalette(string surface, string toolbar, string text, string border, string face)
        {
            Assert.Multiple(() =>
            {
                Assert.That(ColorOf(window.Background), Is.EqualTo(Color.Parse(surface)));
                Assert.That(ColorOf(editor.Background), Is.EqualTo(Color.Parse(surface)));
                Assert.That(ColorOf(files.Background), Is.EqualTo(Color.Parse(surface)));
                Assert.That(ColorOf(references.Background), Is.EqualTo(Color.Parse(surface)));
                Assert.That(ColorOf(menu.Background), Is.EqualTo(Color.Parse(toolbar)));
                Assert.That(ColorOf(editor.Foreground), Is.EqualTo(Color.Parse(text)));
                Assert.That(ColorOf(editor.BorderBrush), Is.EqualTo(Color.Parse(border)));
                Assert.That(ColorOf(button.Background), Is.EqualTo(Color.Parse(face)));
                Assert.That(Contrast(ColorOf(editor.Foreground), ColorOf(editor.Background)), Is.GreaterThanOrEqualTo(4.5));
                Assert.That(Contrast(ColorOf(menu.Foreground), ColorOf(menu.Background)), Is.GreaterThanOrEqualTo(4.5));
            });
        }

        void SaveFrame(string file, params string[] expected)
        {
            using WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.That(frame, Is.Not.Null);
            using ILockedFramebuffer pixels = frame!.Lock();
            Assert.That(pixels.Format, Is.EqualTo(PixelFormat.Bgra8888).Or.EqualTo(PixelFormat.Rgba8888));
            byte[] data = new byte[pixels.RowBytes * pixels.Size.Height];
            Marshal.Copy(pixels.Address, data, 0, data.Length);
            bool bgra = pixels.Format == PixelFormat.Bgra8888;
            foreach (string hex in expected)
            {
                Color color = Color.Parse(hex);
                int count = 0;
                for (int y = 0; y < pixels.Size.Height; y++)
                {
                    for (int x = 0; x < pixels.Size.Width; x++)
                    {
                        int offset = y * pixels.RowBytes + x * 4;
                        if (data[offset] == (bgra ? color.B : color.R) && data[offset + 1] == color.G && data[offset + 2] == (bgra ? color.R : color.B) && data[offset + 3] == 255) { count++; }
                    }
                }
                Assert.That(count, Is.GreaterThan(100), "The rendered UI must use original color " + hex);
            }
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, file);
            frame.Save(path);
            TestContext.AddTestAttachment(path, "Common surfaces from the original CSS palette");
        }
    }

    private static Color PixelAt(WriteableBitmap bitmap, int x, int y)
    {
        using ILockedFramebuffer pixels = bitmap.Lock();
        byte[] data = new byte[4];
        Marshal.Copy(pixels.Address + y * pixels.RowBytes + x * 4, data, 0, data.Length);
        return pixels.Format == PixelFormat.Bgra8888 ? Color.FromArgb(data[3], data[2], data[1], data[0]) : Color.FromArgb(data[3], data[0], data[1], data[2]);
    }
    private static int CountPixels(WriteableBitmap bitmap, Rect bounds, Color color)
    {
        using ILockedFramebuffer pixels = bitmap.Lock();
        byte[] data = new byte[pixels.RowBytes * pixels.Size.Height];
        Marshal.Copy(pixels.Address, data, 0, data.Length);
        bool bgra = pixels.Format == PixelFormat.Bgra8888;
        int count = 0;
        for (int y = (int)bounds.Y; y < Math.Min(pixels.Size.Height, bounds.Bottom); y++)
        {
            for (int x = (int)bounds.X; x < Math.Min(pixels.Size.Width, bounds.Right); x++)
            {
                int offset = y * pixels.RowBytes + x * 4;
                if (data[offset] == (bgra ? color.B : color.R) && data[offset + 1] == color.G && data[offset + 2] == (bgra ? color.R : color.B) && data[offset + 3] == 255) { count++; }
            }
        }
        return count;
    }
    private static Color ColorOf(IBrush? brush) => ((ISolidColorBrush)brush!).Color;
    private static double Contrast(Color first, Color second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
}

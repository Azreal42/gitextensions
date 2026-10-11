using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public class ClassicStylesTests
{
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

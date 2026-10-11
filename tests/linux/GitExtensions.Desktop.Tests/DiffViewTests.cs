using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using GitExtensions.Desktop.Controls;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class DiffViewTests
{
    private const string Patch = "diff --git a/file.txt b/file.txt\n--- a/file.txt\n+++ b/file.txt\n@@ -41,3 +71,3 @@\n context\n-removed\n+added\n trailing\n";

    [AvaloniaTest]
    public void SelectionOffsetsTrackUnicodeAndCrLfAndResetWhenDiffChanges()
    {
        TextBox source = new() { Text = Patch.Replace("\n", "\r\n", StringComparison.Ordinal).Replace("+added", "+α🙂", StringComparison.Ordinal) };
        DiffView view = new(source);
        Window window = new() { Width = 520, Height = 230, Content = view };
        try
        {
            window.Show();
            int start = source.Text!.IndexOf("+α🙂", StringComparison.Ordinal);
            int length = "+α🙂\r\n".Length;
            view.Select(start, length);
            Assert.That(view.Text, Is.EqualTo(source.Text));
            Assert.That(view.SelectionStart, Is.EqualTo(start));
            Assert.That(view.SelectionLength, Is.EqualTo(length));
            Assert.That(view.CaretOffset, Is.EqualTo(start + length));
            Assert.That(source.SelectionStart, Is.EqualTo(start));
            Assert.That(source.SelectionEnd, Is.EqualTo(start + length));
            Assert.That(source.CaretIndex, Is.EqualTo(start + length));
            Assert.That(view.GetLineNumbers(5), Is.EqualTo(((int?)41, (int?)71)));
            Assert.That(view.GetLineNumbers(6), Is.EqualTo(((int?)42, (int?)null)));
            Assert.That(view.GetLineNumbers(7), Is.EqualTo(((int?)null, (int?)72)));
            Assert.That(view.GetLineNumbers(8), Is.EqualTo(((int?)43, (int?)73)));

            source.Text = "plain preview\n+not a patch\n";
            Assert.That(view.Text, Is.EqualTo(source.Text));
            Assert.That(view.SelectionLength, Is.Zero);
            Assert.That(view.CaretOffset, Is.Zero);
            Assert.That(source.SelectionStart, Is.Zero);
            Assert.That(source.SelectionEnd, Is.Zero);
            Assert.That(view.GetLineNumbers(2), Is.EqualTo(((int?)null, (int?)2)));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void KeyboardInputCannotChangeTheDiffWhileSelectionRemainsAvailable()
    {
        TextBox source = new() { Text = Patch };
        DiffView view = new(source);
        Window window = new() { Width = 520, Height = 230, Content = view };
        try
        {
            window.Show();
            view.Editor.TextArea.Focus();
            view.Select(Patch.IndexOf("+added", StringComparison.Ordinal), "+added".Length);
            window.KeyTextInput("replacement");
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Assert.That(view.Text, Is.EqualTo(Patch));
            Assert.That(source.Text, Is.EqualTo(Patch));
            Assert.That(view.SelectionLength, Is.EqualTo("+added".Length));
            Assert.That(view.Editor.IsReadOnly, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void LightAndDarkDiffRenderWithTheExistingThemeColors()
    {
        DiffView view = new(new TextBox { Text = Patch });
        Window window = new() { Width = 520, Height = 230, Content = view, RequestedThemeVariant = ThemeVariant.Light };
        try
        {
            window.Show();
            using WriteableBitmap? light = window.CaptureRenderedFrame();
            Assert.That(light, Is.Not.Null);
            AssertColors(light!, "#c8ffc8", "#ffc8c8", "#e6e6e6");
            light!.Save(Path.Combine(TestContext.CurrentContext.WorkDirectory, "diff-view-light.png"));

            window.RequestedThemeVariant = ThemeVariant.Dark;
            using WriteableBitmap? dark = window.CaptureRenderedFrame();
            Assert.That(dark, Is.Not.Null);
            AssertColors(dark!, "#076641", "#6e1919", "#5f5e5e");
            dark!.Save(Path.Combine(TestContext.CurrentContext.WorkDirectory, "diff-view-dark.png"));
        }
        finally { window.Close(); }
    }

    private static void AssertColors(WriteableBitmap bitmap, params string[] colors)
    {
        using ILockedFramebuffer framebuffer = bitmap.Lock();
        Assert.That(framebuffer.Format, Is.EqualTo(PixelFormat.Bgra8888).Or.EqualTo(PixelFormat.Rgba8888));
        byte[] bytes = new byte[framebuffer.RowBytes * framebuffer.Size.Height];
        Marshal.Copy(framebuffer.Address, bytes, 0, bytes.Length);
        bool bgra = framebuffer.Format == PixelFormat.Bgra8888;
        foreach (string hex in colors)
        {
            Color color = Color.Parse(hex);
            int matching = 0;
            for (int y = 0; y < framebuffer.Size.Height; y++)
            {
                for (int x = 0; x < framebuffer.Size.Width; x++)
                {
                    int offset = y * framebuffer.RowBytes + x * 4;
                    if (bytes[offset] == (bgra ? color.B : color.R) && bytes[offset + 1] == color.G
                        && bytes[offset + 2] == (bgra ? color.R : color.B) && bytes[offset + 3] == 255)
                    {
                        matching++;
                    }
                }
            }
            Assert.That(matching, Is.GreaterThan(100), $"The diff must render opaque upstream color {hex} across the line.");
        }
    }
}

using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace GitExtensions.Desktop.Controls;

internal static class DiffViewerMonochromeBitmap
{
    internal static Bitmap CreateDark(Bitmap original, Color windowText)
    {
        // Original LightnessCorrection maps black to SystemColors.WindowText.
        // The three whitespace assets contain only black and transparent pixels;
        // do not apply this special case to the other, colored viewer icons.
        WriteableBitmap adapted = new(original.PixelSize, original.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        try
        {
            using ILockedFramebuffer buffer = adapted.Lock(); original.CopyPixels(buffer, AlphaFormat.Unpremul);
            byte[] pixels = new byte[buffer.RowBytes * buffer.Size.Height]; Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);
            for (int y = 0; y < buffer.Size.Height; y++)
            {
                for (int x = 0; x < buffer.Size.Width; x++)
                {
                    int offset = y * buffer.RowBytes + x * 4;
                    if (pixels[offset] == 0 && pixels[offset + 1] == 0 && pixels[offset + 2] == 0)
                    { pixels[offset] = windowText.B; pixels[offset + 1] = windowText.G; pixels[offset + 2] = windowText.R; }
                    // Preserve the source alpha byte, including transparent and antialiased pixels.
                }
            }
            Marshal.Copy(pixels, 0, buffer.Address, pixels.Length); return adapted;
        }
        catch { adapted.Dispose(); throw; }
    }
}

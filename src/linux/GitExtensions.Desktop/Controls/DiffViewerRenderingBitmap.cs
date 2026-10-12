using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace GitExtensions.Desktop.Controls;

internal static class DiffViewerRenderingBitmap
{
    internal static Bitmap CreateDark(Bitmap original, Color windowText, Color window)
    {
        // FileViewer adapts both images using LightnessCorrection. These two
        // assets are achromatic (including RGB 1 and fractional alpha), so their
        // HSL saturation is zero and ColorHelper.Gamma is exactly one. Preserve
        // the original TransformL and HslColor.ToColor rounding for that branch.
        double textLightness = (Math.Max(windowText.R, Math.Max(windowText.G, windowText.B)) + Math.Min(windowText.R, Math.Min(windowText.G, windowText.B))) / 510d;
        double backgroundLightness = (Math.Max(window.R, Math.Max(window.G, window.B)) + Math.Min(window.R, Math.Min(window.G, window.B))) / 510d;
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
                    if (pixels[offset] != pixels[offset + 1] || pixels[offset] != pixels[offset + 2])
                    { throw new InvalidDataException("The rendering glyph must be monochrome for this original lightness correction."); }
                    double lightness = textLightness + pixels[offset] / 255d * (backgroundLightness - textLightness);
                    byte channel = (byte)Math.Clamp(Math.Floor(lightness * 256), 0, 255);
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = channel;
                    // Alpha is never written, including transparent and antialiased source pixels.
                }
            }
            Marshal.Copy(pixels, 0, buffer.Address, pixels.Length); return adapted;
        }
        catch { adapted.Dispose(); throw; }
    }
}

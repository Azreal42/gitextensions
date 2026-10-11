using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace GitExtensions.Desktop.Controls;

public sealed class FileHistoryContentView : UserControl, IDisposable
{
    private readonly TextBox _source = new();
    private readonly FileContentView _content;
    private string? _syntaxPath;
    public DiffView TextView => _content.TextView;
    public bool IsImage => _content.IsImage;
    public FileHistoryContentView()
    {
        _content = new(_source);
        Content = _content;
        ActualThemeVariantChanged += (_, _) => ApplySyntax();
    }
    public void ShowFile(string path, byte[] bytes)
    {
        _syntaxPath = path;
        if (!_content.TryShowImage(path, bytes)) { _source.Text = DecodeBytes(bytes); }
        ApplySyntax();
    }
    public void ShowDiff(string text)
    {
        _syntaxPath = null;
        _content.ClearImage();
        _source.Text = text;
        ApplySyntax();
    }
    private void ApplySyntax()
        => TextView.Editor.SyntaxHighlighting = _syntaxPath is null || IsImage ? null
            : FileHistorySyntax.Load(_syntaxPath, ActualThemeVariant == ThemeVariant.Dark);
    public void Dispose() => _content.Dispose();

    internal static string DecodeBytes(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4); }
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { return new UTF32Encoding(true, false).GetString(bytes, 4, bytes.Length - 4); }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2); }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2); }
        int skip = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        try { if (!bytes.Contains((byte)0)) { return new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip); } }
        catch (DecoderFallbackException) { }
        return "Binary file\n" + Convert.ToHexString(bytes.AsSpan(0, Math.Min(4096, bytes.Length)));
    }
}

internal static class FileHistorySyntax
{
    private static readonly Dictionary<(string Extension, bool Dark), IHighlightingDefinition?> Cache = [];
    internal static IHighlightingDefinition? Load(string path, bool dark)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (Cache.TryGetValue((extension, dark), out IHighlightingDefinition? cached)) { return cached; }
        IHighlightingDefinition? original = HighlightingManager.Instance.GetDefinitionByExtension(extension);
        if (original is null) { Cache[(extension, dark)] = null; return null; }
        // Reload the existing embedded grammar into an independent definition;
        // changing a globally shared definition would recolor other windows.
        foreach (string name in typeof(HighlightingManager).Assembly.GetManifestResourceNames().Where(name => name.EndsWith(".xshd", StringComparison.Ordinal)))
        {
            using Stream stream = typeof(HighlightingManager).Assembly.GetManifestResourceStream(name)!;
            XDocument xml = XDocument.Load(stream);
            if (xml.Root?.Attribute("name")?.Value != original.Name) { continue; }
            Color background = Color.Parse(dark ? "#323232" : "#ffffff");
            foreach (XAttribute attribute in xml.Descendants().Attributes("foreground"))
            {
                if (Color.TryParse(attribute.Value, out Color color)) { attribute.Value = Contrast(background, color).ToString(); }
            }
            using XmlReader reader = xml.CreateReader();
            IHighlightingDefinition definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            Cache[(extension, dark)] = definition;
            return definition;
        }
        Cache[(extension, dark)] = original;
        return original;
    }

    // Same RGB stepping and WCAG threshold as the original ColorHelper's
    // EnsureContrast, used by ThemeBasedHighlighting.Transform.
    private static Color Contrast(Color background, Color color)
    {
        if (Ratio(background, color) >= 4.5) { return color; }
        bool reduce = Luminance(color) < Luminance(background);
        Color first = Step(reduce);
        if (Ratio(background, first) >= 4.5) { return first; }
        Color second = Step(!reduce);
        return Ratio(background, first) >= Ratio(background, second) ? first : second;
        Color Step(bool darker)
        {
            int r = color.R, g = color.G, b = color.B;
            Color current = color;
            while (Ratio(background, current) < 4.5 && (darker ? r > 0 || g > 0 || b > 0 : r < 255 || g < 255 || b < 255))
            {
                r = darker ? r - (int)Math.Ceiling(r * 0.1) : Math.Min(255, r + (int)Math.Ceiling((255 - r) * 0.1));
                g = darker ? g - (int)Math.Ceiling(g * 0.1) : Math.Min(255, g + (int)Math.Ceiling((255 - g) * 0.1));
                b = darker ? b - (int)Math.Ceiling(b * 0.1) : Math.Min(255, b + (int)Math.Ceiling((255 - b) * 0.1));
                current = Color.FromArgb(color.A, (byte)r, (byte)g, (byte)b);
            }
            return current;
        }
    }
    internal static double Ratio(Color one, Color two)
    { double a = Luminance(one), b = Luminance(two); return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05); }
    private static double Luminance(Color color)
    {
        static double Linear(byte channel) { double value = channel / 255d; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}

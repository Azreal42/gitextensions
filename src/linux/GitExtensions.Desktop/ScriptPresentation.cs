using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

/// <summary>Presentation only. Never executes a file to discover an associated icon.</summary>
internal static class ScriptPresentation
{
    internal static IReadOnlyList<string> IconKeys => Array.AsReadOnly(AssetLoader.GetAssets(new Uri("avares://GitExtensions/Assets/Icons/"), null)
        .Where(uri => uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        .Select(uri => Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(uri.AbsolutePath)))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    internal static AccessText Header(ScriptDefinition script)
    {
        string name = script.Name ?? "";
        StringBuilder text = new(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '_') { text.Append("__"); }
            else if (c == '&' && i + 1 < name.Length && !char.IsWhiteSpace(name[i + 1]))
            {
                if (name[i + 1] == '&') { text.Append('&'); i++; }
                else { text.Append('_'); }
            }
            else { text.Append(c); }
        }
        return new AccessText { Text = text.ToString() };
    }

    internal static Control? Icon(ScriptDefinition script)
    {
        if (!string.IsNullOrEmpty(script.IconFilePath))
        {
            try
            {
                Bitmap bitmap = new(script.IconFilePath);
                return new Image { Source = bitmap, Width = 16, Height = 16, Stretch = Stretch.Uniform,
                    Tag = script.IconFilePath, UseLayoutRounding = true };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            { /* A missing/unrenderable path falls through without altering either identity. */ }
        }
        return script.Icon is string key && IconKeys.Contains(key, StringComparer.Ordinal)
            ? OriginalMenuItems.Icon(key + ".png") : null;
    }
}

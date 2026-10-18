using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Media;
using Avalonia.Styling;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop.Controls;

/// <summary>One source for original menu translations, access keys, icons and gestures.</summary>
internal static class OriginalMenuItems
{
    public static MenuItem Create(TranslationCatalog translations, string category, string name, string text,
        string? icon = null, KeyGesture? gesture = null) => new()
    {
        Name = name,
        Header = new AccessText { Text = translations.TranslateAccessKeys(category, name, "Text", text) },
        Icon = icon is null ? null : Icon(icon),
        InputGesture = gesture
    };

    public static Image Icon(string file)
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + file));
        Bitmap original = new(stream);
        Image image = new() { Source = original, Width = 16, Height = 16, Tag = file, Stretch = Stretch.Uniform, UseLayoutRounding = true };
        if (file is "Translate.png" or "FileStatusRenamed.png" or "Font.png")
        {
            Bitmap? dark = null;
            void UpdateSource()
            {
                image.Source = image.ActualThemeVariant == ThemeVariant.Dark
                    ? dark ??= GitExtensions.Desktop.Controls.DiffViewerRenderingBitmap.CreateDark(original, Color.Parse(ClassicStyles.OriginalColors(dark: true)["MenuText"]), Color.Parse(ClassicStyles.OriginalColors(dark: true)["Menu"]))
                    : original;
            }
            image.ActualThemeVariantChanged += (_, _) => UpdateSource();
            UpdateSource();
        }
        return image;
    }
}

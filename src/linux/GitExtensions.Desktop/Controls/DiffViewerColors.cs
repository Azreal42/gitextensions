using Avalonia.Media;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;

namespace GitExtensions.Desktop.Controls;

internal static class DiffViewerColors
{
    private static readonly Lazy<GitDiffColorPalette> Light = new(() => Load(false));
    private static readonly Lazy<GitDiffColorPalette> Dark = new(() => Load(true));
    internal static GitDiffColorPalette ForTheme(bool dark) => (dark ? Dark : Light).Value;
    internal static IBrush Brush(GitDiffColor color) => new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));

    private static GitDiffColorPalette Load(bool dark)
    {
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(dark);
        string[] names = ["Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White"];
        return new(Enumerable.Range(0, 16).Select(index => Read("AnsiTerminal" + names[index % 8] + "Fore" + (index < 8 ? "Normal" : "Bold"))).ToArray(),
            Enumerable.Range(0, 16).Select(index => Read("AnsiTerminal" + names[index % 8] + "Back" + (index < 8 ? "Normal" : "Bold"))).ToArray(),
            Read("EditorBackground"), Read("WindowText"));
        GitDiffColor Read(string name)
        {
            Color color = Color.Parse(colors[name]);
            return new(color.R, color.G, color.B);
        }
    }
}

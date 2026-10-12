namespace GitExtensions.Linux;

public readonly record struct GitDiffColor(byte R, byte G, byte B);

/// <summary>ANSI slots 0..7 are normal and 8..15 bright; palettes are supplied by the host theme.</summary>
public sealed record GitDiffColorPalette(IReadOnlyList<GitDiffColor> Foregrounds,
    IReadOnlyList<GitDiffColor> Backgrounds, GitDiffColor EditorBackground, GitDiffColor EditorForeground)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Foregrounds); ArgumentNullException.ThrowIfNull(Backgrounds);
        if (Foregrounds.Count != 16 || Backgrounds.Count != 16)
        { throw new ArgumentException("An ANSI diff palette requires sixteen foregrounds and sixteen backgrounds."); }
    }
}

public sealed record GitDiffColorSpan(int Offset, int Length, GitDiffColor? Background = null,
    GitDiffColor? Foreground = null, bool IsAnchor = false);
public sealed record GitColoredText(string Text, IReadOnlyList<GitDiffColorSpan> Spans);
public enum GitDiffLineKind { Added, Removed, Context, Header }
public sealed record GitDiffLineDescriptor(int Offset, int Length, GitDiffLineKind Kind,
    int ContentOffset = 1, bool IsMoved = false);

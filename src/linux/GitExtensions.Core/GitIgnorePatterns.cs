using System.Text;

namespace GitExtensions.Linux;

/// <summary>Literal selections are escaped for gitignore, independently of the editable raw pattern syntax.</summary>
public static class GitIgnorePatterns
{
    public static string ForPath(string path, bool directory = false, bool anchored = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ValidateLine(path);
        if (System.IO.Path.IsPathRooted(path) || path.Split('/').Any(part => part is "" or "." or ".."))
        { throw new ArgumentException("Choose a path relative to the ignore file's directory.", nameof(path)); }
        return (anchored ? "/" : "") + Literal(path) + (directory ? "/" : "");
    }
    public static string ForExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrEmpty(extension); ValidateLine(extension);
        if (extension.StartsWith('.')) { extension = extension[1..]; }
        if (extension.Length == 0 || extension.Contains('/') || extension.Contains('\\')) { throw new ArgumentException("Enter one file extension.", nameof(extension)); }
        return "*." + Literal(extension);
    }
    internal static void ValidateLine(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.IndexOfAny(['\0', '\r', '\n']) >= 0) { throw new ArgumentException("An ignore pattern is one line and cannot represent NUL, CR or LF in a file name.", nameof(pattern)); }
    }
    private static string Literal(string value)
    {
        StringBuilder escaped = new();
        foreach (char character in value)
        {
            if (character is '\\' or '*' or '?' or '[' or ']' or '!' or '#' or ' ') { escaped.Append('\\'); }
            escaped.Append(character);
        }
        return escaped.ToString();
    }
}

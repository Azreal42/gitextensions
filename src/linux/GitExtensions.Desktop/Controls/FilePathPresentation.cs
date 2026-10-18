using System.Globalization;
using GitCommands;

namespace GitExtensions.Linux.Desktop.Controls;

/// <summary>Presentation strings only; callers retain literal Git paths, selection identities and full tooltips.</summary>
internal static class FilePathPresentation
{
    internal static (string? prefix, string? text, string? suffix) Format(TruncatePathMethod method, double maxWidth,
        string name, string? oldName, Func<string, double> measure)
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(measure);
        if (!Enum.IsDefined(method)) { throw new ArgumentOutOfRangeException(nameof(method)); }
        if (double.IsNaN(maxWidth)) { throw new ArgumentOutOfRangeException(nameof(maxWidth)); }
        if (method == TruncatePathMethod.FileNameOnly)
        {
            string filename = BaseName(name.TrimEnd('/'));
            string? previous = oldName is null ? null : BaseName(oldName);
            return (null, filename, filename == previous ? null : OldName(previous));
        }
        (string? prefix, string? text, string? suffix) full = AtStep(0);
        if (method == TruncatePathMethod.None || Fits(full)) { return full; }

        long oldLength = oldName?.Length ?? 0;
        if (oldLength > 0)
        {
            var minimalSuffix = AtStep(oldLength);
            // Search the suffix separately. Starting to add an ellipsis to the current
            // name can briefly widen it, so one binary search across both phases could
            // skip a suffix-only solution with variable glyph widths.
            if (Fits(minimalSuffix)) { return Search(1, oldLength, minimalSuffix); }
        }
        if (name.Length > 0)
        {
            var minimalName = AtStep(oldLength + name.Length);
            if (Fits(minimalName)) { return Search(oldLength + 1, oldLength + name.Length, minimalName); }
        }
        return (null, name.Length == 0 ? "" : "...", null);

        (string? prefix, string? text, string? suffix) Search(long low, long high,
            (string? prefix, string? text, string? suffix) result)
        {
            while (low <= high)
            {
                long middle = low + (high - low) / 2;
                var candidate = AtStep(middle);
                if (Fits(candidate)) { result = candidate; high = middle - 1; }
                else { low = middle + 1; }
            }
            return result;
        }
        bool Fits((string? prefix, string? text, string? suffix) value)
        {
            double width = measure(string.Concat(value.prefix, value.text, value.suffix));
            if (double.IsNaN(width) || width < 0) { throw new ArgumentException("The path measurer must return a nonnegative width.", nameof(measure)); }
            return width <= maxWidth;
        }
        (string? prefix, string? text, string? suffix) AtStep(long step)
        {
            int oldRemoved = (int)Math.Min(step, oldName?.Length ?? 0);
            int nameRemoved = (int)(step - oldRemoved);
            (string? prefix, string? text) = Split(Truncate(name, name.Length - nameRemoved));
            return (prefix, text, oldName is null ? null : OldName(Truncate(oldName, oldName.Length - oldRemoved)));
        }
        string Truncate(string value, int length)
        {
            if (length == value.Length) { return value; }
            if (length <= 0) { return "..."; }
            return method == TruncatePathMethod.Compact ? Compact(value, length) : "..." + SafeTail(value, length);
        }
    }

    private static (string? prefix, string text) Split(string value)
    {
        int slash = value.TrimEnd('/').LastIndexOf('/');
        return slash < 0 ? (null, value) : (value[..(slash + 1)], value[(slash + 1)..]);
    }
    private static string BaseName(string value) => value[(value.LastIndexOf('/') + 1)..];
    private static string? OldName(string? value) => string.IsNullOrEmpty(value) ? null : " (" + value + ")";

    /// <summary>
    /// Portable adaptation of the documented PathCompactPathEx contract: the character
    /// budget includes NUL, directory components become "...", and filename-only
    /// overflow retains a prefix followed by "...". The Windows API is not invoked.
    /// </summary>
    /// <remarks>
    /// Only POSIX '/' is a separator. Prefer the whole final component and the longest
    /// complete leading directory prefix. Native tie-breaking, MAX_PATH and arbitrary
    /// UTF-16 cuts are not reproduced; text-element boundaries are retained instead.
    /// https://learn.microsoft.com/en-us/windows/win32/api/shlwapi/nf-shlwapi-pathcompactpathexw
    /// </remarks>
    private static string Compact(string value, int characterBudget)
    {
        int length = characterBudget - 1; // PathCompactPathEx includes its terminating NUL.
        if (length <= 3) { return "..."; }
        int slash = value.TrimEnd('/').LastIndexOf('/');
        if (slash < 0) { return CompactFilename(value, length); }
        string filename = value[(slash + 1)..];
        string root = value.StartsWith('/') ? "/" : "";
        string tail = ".../" + filename;
        if (root.Length + tail.Length <= length)
        {
            string leading = root;
            for (int separator = value.IndexOf('/'); separator >= 0 && separator < slash;
                separator = value.IndexOf('/', separator + 1))
            {
                if (separator + 1 + tail.Length > length) { break; }
                leading = value[..(separator + 1)];
            }
            return leading + tail;
        }
        string marker = root + ".../";
        if (length - marker.Length >= 3) { return marker + CompactFilename(filename, length - marker.Length); }
        // Below the directory marker plus minimal filename budget, preserve an
        // overflow indication rather than silently dropping all directory context.
        return CompactFilename(filename, length, forceEllipsis: true);
    }
    private static string CompactFilename(string value, int length, bool forceEllipsis = false)
        => !forceEllipsis && value.Length <= length ? value : SafePrefix(value, Math.Max(0, length - 3)) + "...";

    private static string SafePrefix(string value, int maximumLength)
    {
        if (maximumLength >= value.Length) { return value; }
        int end = 0;
        foreach (int boundary in StringInfo.ParseCombiningCharacters(value))
        { if (boundary > maximumLength) { break; } end = boundary; }
        return value[..end];
    }
    private static string SafeTail(string value, int maximumLength)
    {
        int minimumStart = Math.Max(0, value.Length - maximumLength);
        foreach (int boundary in StringInfo.ParseCombiningCharacters(value))
        { if (boundary >= minimumStart) { return value[boundary..]; } }
        return "";
    }
}

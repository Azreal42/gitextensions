using System.Globalization;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux.Desktop;

/// <summary>Release tags use SemVer. Build metadata identifies a build, never its ordering.</summary>
internal sealed partial class LinuxReleaseVersion : IComparable<LinuxReleaseVersion>
{
    private readonly int[] _numbers;
    private readonly string[] _prerelease;
    internal string Text { get; }
    internal bool IsPrerelease => _prerelease.Length != 0;
    internal string DebianVersion
    {
        get { int index = Text.IndexOf('-'); return index < 0 ? Text : Text[..index] + "~" + Text[(index + 1)..]; }
    }

    private LinuxReleaseVersion(string text, int[] numbers, string[] prerelease)
    { Text = text; _numbers = numbers; _prerelease = prerelease; }

    internal static LinuxReleaseVersion? Parse(string? tag)
    {
        if (tag is null || tag.Length > 128) { return null; }
        Match match = VersionPattern().Match(tag);
        if (!match.Success) { return null; }
        int[] numbers = new int[3];
        for (int i = 0; i < 3; i++)
        { if (!int.TryParse(match.Groups[i + 1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) { return null; } }
        string[] prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(part => part.All(char.IsAsciiDigit) && part.Length > 1 && part[0] == '0')) { return null; }
        return new(tag.TrimStart('v'), numbers, prerelease);
    }

    public int CompareTo(LinuxReleaseVersion? other)
    {
        if (other is null) { return 1; }
        for (int i = 0; i < 3; i++) { int result = _numbers[i].CompareTo(other._numbers[i]); if (result != 0) { return result; } }
        if (_prerelease.Length == 0 || other._prerelease.Length == 0)
        { return (_prerelease.Length == 0).CompareTo(other._prerelease.Length == 0); }
        for (int i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            string a = _prerelease[i], b = other._prerelease[i];
            bool numericA = a.All(char.IsAsciiDigit), numericB = b.All(char.IsAsciiDigit);
            int result = numericA && numericB ? a.Length.CompareTo(b.Length) : numericA == numericB ? 0 : numericA ? -1 : 1;
            if (result == 0) { result = string.CompareOrdinal(a, b); }
            if (result != 0) { return result; }
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    [GeneratedRegex(@"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

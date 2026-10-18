using System.Globalization;
using System.Text;

namespace GitExtensions.Linux.Desktop;

/// <summary>Presentation choices only. Strict file decoding and patch safety remain in GitFileEncodingCodec.</summary>
public sealed class AvailableEncodingsCatalog
{
    private readonly IReadOnlyDictionary<string, GitFileEncodingChoice> _byName;
    private readonly int _defaultCodePage = Encoding.Default.CodePage;
    public IReadOnlyList<GitFileEncodingChoice> All { get; }
    public IReadOnlyList<string> Defaults { get; }

    public AvailableEncodingsCatalog(IEnumerable<Encoding>? encodings = null, CultureInfo? culture = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        IEnumerable<Encoding> supported = encodings ?? SupportedEncodings();
        List<GitFileEncodingChoice> choices = [];
        foreach (Encoding encoding in supported)
        {
            if (encoding.CodePage == 65000) { continue; }
            // Resolve through the same strict codec as the viewer, including BOM-free UTF-8.
            Encoding strict = GitFileEncodingCodec.GetEncoding(encoding.WebName);
            choices.Add(new(strict.WebName, strict.EncodingName));
        }
        All = Array.AsReadOnly(choices.DistinctBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(choice => choice.DisplayName, StringComparer.CurrentCulture).ToArray());
        _byName = All.ToDictionary(choice => choice.Name, StringComparer.OrdinalIgnoreCase);
        List<string> defaults = [];
        Add(Encoding.Default); Add(Encoding.ASCII); Add(Encoding.Unicode); Add(new UTF8Encoding(false, true));
        try { Add(Encoding.GetEncoding((culture ?? CultureInfo.CurrentCulture).TextInfo.OEMCodePage)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { /* Some cultures have no OEM code page. */ }
        try { Add(Encoding.GetEncoding(0)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { /* A platform may not expose an active code page. */ }
        Defaults = Array.AsReadOnly(defaults.ToArray());
        void Add(Encoding encoding)
        {
            if (_byName.ContainsKey(encoding.WebName) && !defaults.Contains(encoding.WebName, StringComparer.OrdinalIgnoreCase))
            { defaults.Add(encoding.WebName); }
        }
    }

    private static IEnumerable<Encoding> SupportedEncodings()
    {
        foreach (EncodingInfo info in Encoding.GetEncodings()) { yield return info.GetEncoding(); }
        // .NET 10's CodePages provider resolves these pages on Linux but omits their metadata
        // from both GetEncodings APIs. Query this bounded set; unavailable pages remain absent.
        int[] additionalPages = [10003, 10008, 38598, 50220, 50221, 50225, 50227, 51936, 51949, 52936, 54936,
            57002, 57003, 57004, 57005, 57006, 57007, 57008, 57009, 57010, 57011];
        foreach (int codePage in additionalPages)
        {
            Encoding? encoding;
            try { encoding = CodePagesEncodingProvider.Instance.GetEncoding(codePage); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException) { continue; }
            if (encoding is not null) { yield return encoding; }
        }
    }

    public GitFileEncodingChoice? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { return null; }
        if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)) { name = Encoding.Default.WebName; }
        if (_byName.TryGetValue(name, out GitFileEncodingChoice? choice)) { return choice; }
        try
        {
            string canonical = GitFileEncodingCodec.GetEncoding(name).WebName;
            return _byName.GetValueOrDefault(canonical);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return null; }
    }

    public bool IsProtected(string name)
    {
        GitFileEncodingChoice? choice = Resolve(name);
        if (choice is null) { return false; }
        int codePage = GitFileEncodingCodec.GetEncoding(choice.Name).CodePage;
        return codePage is 20127 or 1200 or 1201 or 65001 || codePage == _defaultCodePage;
    }

    /// <summary>Removing a preferred choice never changes an already selected file encoding.</summary>
    public IReadOnlyList<GitFileEncodingChoice> Choices(IEnumerable<string> names, string? current = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        List<GitFileEncodingChoice> choices = [];
        foreach (string name in names)
        {
            GitFileEncodingChoice choice = Resolve(name) ?? throw new ArgumentException("Unsupported preferred encoding: " + name, nameof(names));
            if (!choices.Contains(choice)) { choices.Add(choice); }
        }
        if (Resolve(current) is { } selected && !choices.Contains(selected)) { choices.Add(selected); }
        return Array.AsReadOnly(choices.ToArray());
    }
}

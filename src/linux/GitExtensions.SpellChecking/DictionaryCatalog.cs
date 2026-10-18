#nullable enable
using System.Collections.Immutable;

namespace GitExtensions.Linux.Spelling;

public sealed record SpellDictionary(string Name, string FilePath);

/// <summary>The installed NetSpell directory is read-only to this component.</summary>
public sealed class DictionaryCatalog
{
    public const string NoneName = "none";
    public const string DefaultName = "en-US";
    public string InstallationDirectory { get; }

    public DictionaryCatalog(string installationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationDirectory);
        InstallationDirectory = Path.GetFullPath(installationDirectory);
    }

    public static DictionaryCatalog FromApplicationDirectory(string applicationDirectory)
        => new(Path.Combine(applicationDirectory, "Dictionaries"));

    public static string NormalizeName(string? value)
    {
        return value is null ? DefaultName
            : string.Equals(value, NoneName, StringComparison.OrdinalIgnoreCase) ? NoneName : value;
    }

    /// <summary>Enumerates actual files only. The UI may prepend the canonical None choice.</summary>
    public ImmutableArray<SpellDictionary> GetInstalled()
    {
        // Do not turn a missing/unreadable installation into an empty catalogue.
        var result = ImmutableArray.CreateBuilder<SpellDictionary>();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(InstallationDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(Path.GetExtension(path), ".dic", StringComparison.OrdinalIgnoreCase)) { continue; }
            string name = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(name, NoneName, StringComparison.OrdinalIgnoreCase) || !names.Add(name))
            { throw new InvalidDataException($"Ambiguous dictionary identity '{name}' in '{InstallationDirectory}'."); }
            result.Add(new(name, Path.GetFullPath(path)));
        }
        return result.OrderBy(item => item.Name, StringComparer.Ordinal).ToImmutableArray();
    }

    public SpellDictionary Resolve(string? name)
    {
        string normalized = NormalizeName(name);
        if (normalized == NoneName) { throw new ArgumentException("None has no dictionary file.", nameof(name)); }
        if (string.IsNullOrWhiteSpace(normalized) || normalized is "." or ".." || normalized.IndexOfAny(['/', '\\', '\0']) >= 0)
        { throw new ArgumentException("A dictionary must be an installed dictionary basename.", nameof(name)); }
        return GetInstalled().FirstOrDefault(item => string.Equals(item.Name, normalized, StringComparison.Ordinal))
            ?? throw new FileNotFoundException($"Dictionary '{normalized}' is not installed in '{InstallationDirectory}'.");
    }
}

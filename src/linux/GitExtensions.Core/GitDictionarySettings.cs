namespace GitExtensions.Linux;

public enum GitDictionarySettingSource { Global, Distributed, Local }

/// <summary>Dictionary identities are names, not paths. Only the disabled identity is canonicalized.</summary>
public static class GitDictionaryName
{
    public const string Default = "en-US";
    public const string None = "none";

    /// <summary>Keep stored unknown/invalid identities available for diagnostics; a read is not a new edit.</summary>
    public static string NormalizeDisabledIdentity(string? value)
        => value is null ? Default : string.Equals(value, None, StringComparison.OrdinalIgnoreCase) ? None : value;

    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value is "." or ".." || value.IndexOfAny(['/', '\\', '\0']) >= 0)
        { throw new ArgumentException("Choose a dictionary basename, not a path.", nameof(value)); }
        return value.Equals(None, StringComparison.OrdinalIgnoreCase) ? None : value;
    }
}

/// <summary>Owned immutable baseline for a local settings edit; bytes and filesystem identities remain private.</summary>
public sealed class GitDictionarySettingsSnapshot
{
    internal GitDictionarySettingsSnapshot(GitRepositoryInfo info, string globalDictionary,
        GitDictionarySettingsFile local, GitDictionarySettingsFile? distributed)
    {
        RootPath = info.RootPath; CommonDirectory = info.CommonDirectory;
        GlobalDictionary = globalDictionary; Local = local; Distributed = distributed;
        LocalDictionary = local.Dictionary; DistributedDictionary = distributed?.Dictionary;
        EffectiveDictionary = GitDictionaryName.Normalize(LocalDictionary ?? DistributedDictionary ?? globalDictionary);
        Source = LocalDictionary is not null ? GitDictionarySettingSource.Local
            : DistributedDictionary is not null ? GitDictionarySettingSource.Distributed : GitDictionarySettingSource.Global;
    }

    public string RootPath { get; }
    public string CommonDirectory { get; }
    public string LocalPath => Local.Path;
    public string? DistributedPath => Distributed?.Path;
    public bool LocalExists => Local.Exists;
    public bool DistributedExists => Distributed?.Exists == true;
    public string GlobalDictionary { get; }
    public string? LocalDictionary { get; }
    public string? DistributedDictionary { get; }
    public string EffectiveDictionary { get; }
    public GitDictionarySettingSource Source { get; }
    internal GitDictionarySettingsFile Local { get; }
    internal GitDictionarySettingsFile? Distributed { get; }
}

internal sealed record GitDictionarySettingsFile(string Path, GitRulesLocation Location, bool Exists,
    string Hash, GitRulesIdentity? Identity, UnixFileMode? Mode, FileAttributes Attributes,
    IReadOnlyDictionary<string, string> Values, string? Dictionary);

namespace GitExtensions.Linux;

public sealed record GitRemoteSettingsEdit(string Name, string Url, string? PushUrl = null, string? Prefix = null, string? Color = null);
public sealed record GitRemoteSetting(string Key, string Value, string Scope, string Origin);

/// <summary>A guarded, immutable view of a remote, retaining ordered multivalues and their configuration origins.</summary>
public sealed class GitRemoteSettingsSnapshot
{
    internal GitRemoteSettingsSnapshot(RemoteSettingsState state, string name, bool disabled, GitRemoteSetting[] settings,
        string[] fetch, string[] push, string[] effectiveFetch, string[] effectivePush, string? resolutionError)
    {
        State = state; Name = name; Disabled = disabled; Settings = Array.AsReadOnly(settings);
        RawFetchUrls = Array.AsReadOnly(settings.Where(s => s.Key == Key("url")).Select(s => s.Value).ToArray());
        RawPushUrls = Array.AsReadOnly(settings.Where(s => s.Key == Key("pushurl")).Select(s => s.Value).ToArray());
        FetchUrls = Array.AsReadOnly(fetch); PushUrls = Array.AsReadOnly(push);
        EffectiveFetchUrls = Array.AsReadOnly(effectiveFetch); EffectivePushUrls = Array.AsReadOnly(effectivePush);
        UrlResolutionError = resolutionError;
        CanModifyStructure = settings.All(s => s.Scope == "local" && s.Origin == state.Config.Path);
        ModificationRestriction = CanModifyStructure ? null : "This remote includes settings inherited from another configuration file. Rename, remove and activation changes require editing that source; changing it here would affect other repositories.";
    }
    public string Name { get; }
    public bool Disabled { get; }
    public string Url => FetchUrls.FirstOrDefault() ?? "";
    public string? PushUrl => PushUrls.LastOrDefault();
    public bool HasSeparatePushUrl => PushUrls.Count != 0;
    /// <summary>Unexpanded URLs after Git's empty-value list resets.</summary>
    public IReadOnlyList<string> FetchUrls { get; }
    public IReadOnlyList<string> PushUrls { get; }
    public IReadOnlyList<string> RawFetchUrls { get; }
    public IReadOnlyList<string> RawPushUrls { get; }
    public IReadOnlyList<string> EffectiveFetchUrls { get; }
    public IReadOnlyList<string> EffectivePushUrls { get; }
    public IReadOnlyList<GitRemoteSetting> Settings { get; }
    public string? UrlResolutionError { get; }
    public string? Prefix => Last("prefix");
    public string? Color => Last("color");
    public string? PuttySshKey => Last("puttykeyfile");
    public bool CanModifyStructure { get; }
    public string? ModificationRestriction { get; }
    public string ConfigurationRevision => State.Fingerprint;
    internal RemoteSettingsState State { get; }
    internal string Key(string variable) => (Disabled ? "-remote." : "remote.") + Name + "." + variable;
    private string? Last(string variable) => Settings.LastOrDefault(s => s.Key == Key(variable))?.Value;
}

public sealed class GitBranchTrackingSnapshot
{
    internal GitBranchTrackingSnapshot(RemoteSettingsState state, string branch, string hash)
    { State = state; LocalBranch = branch; Hash = hash; }
    public string LocalBranch { get; }
    public string? Remote => State.Effective.LastOrDefault(s => s.Key == "branch." + LocalBranch + ".remote")?.Value;
    public string? MergeRef => State.Effective.LastOrDefault(s => s.Key == "branch." + LocalBranch + ".merge")?.Value;
    public string? MergeBranch => MergeRef is string value && value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value[11..] : MergeRef;
    public IReadOnlyList<string> MergeRefs => Array.AsReadOnly(State.Effective.Where(s => s.Key == "branch." + LocalBranch + ".merge").Select(s => s.Value).ToArray());
    public string ConfigurationRevision => State.Fingerprint;
    internal RemoteSettingsState State { get; }
    internal string Hash { get; }
}

/// <summary>Native multi-step operations may already have changed refs/configuration when a later step fails.</summary>
public sealed class GitRemoteSettingsException : InvalidOperationException
{
    internal GitRemoteSettingsException(string message, bool changed, string? effectiveName, Exception? inner = null)
        : base(message, inner) { HasRepositoryChanges = changed; EffectiveName = effectiveName; }
    public bool HasRepositoryChanges { get; }
    public string? EffectiveName { get; }
}

internal sealed record RemoteSettingsFile(string LogicalPath, string Path, GitRulesLocation Location, GitRulesIdentity? Identity, byte[] Bytes,
    UnixFileMode? Mode, FileAttributes Attributes)
{
    internal string Hash => GitRulesFiles.Hash(Bytes);
}

internal sealed record RemoteSettingsState(string CommonDirectory, RemoteSettingsFile Config,
    IReadOnlyList<GitRemoteSetting> Effective, IReadOnlyList<KeyValuePair<string, string>> Local,
    IReadOnlyList<RemoteSettingsFile> Sources, string Fingerprint);

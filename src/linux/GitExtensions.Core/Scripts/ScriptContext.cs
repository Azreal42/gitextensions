using System.Globalization;

namespace GitExtensions.Linux.Scripts;

public enum ScriptReferenceKind { LocalBranch, RemoteBranch, Tag }
// Only the original file-provider escaping policy. This does not parse Arguments into a process argv.
public enum ScriptArgumentPlatform { Posix, Windows }

/// <param name="Name">The original short display/ref name, e.g. origin/topic; not refs/remotes/origin/topic.</param>
public sealed record ScriptReference(string Name, ScriptReferenceKind Kind, string Remote = "");

public sealed record ScriptRevision(string Hash, string Subject = "", string? Body = null, string? Author = null, string? Committer = null,
    DateTime AuthorDate = default, DateTime CommitDate = default)
{
    // GitRevision exposes local DateTime, independent of the offset stored in the raw commit.
    public static DateTime FromUnixTimeSeconds(long unixTime)
        => unixTime == 0 ? DateTime.MaxValue : DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;
}
public sealed record ScriptOptionValues(string Name, IReadOnlyList<string> Values);

/// <summary>Materialized host facts only. Constructing/snapshotting this context never queries Git or UI.</summary>
public sealed record ScriptContext
{
    public string WorkingDir { get; init; } = "";
    public string RepoName { get; init; } = "";
    public bool HasRevisionGrid { get; init; }
    public ScriptRevision? SelectedRevision { get; init; }
    public IReadOnlyList<ScriptRevision> SelectedRevisions { get; init; } = [];
    public IReadOnlyList<ScriptReference> SelectedReferences { get; init; } = [];
    public ScriptRevision? CurrentRevision { get; init; }
    public IReadOnlyList<ScriptReference> CurrentReferences { get; init; } = [];
    public string CurrentBranch { get; init; } = "";
    public string CurrentRemote { get; init; } = "";
    public IReadOnlyDictionary<string, string> BranchRemotes { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> RemoteUrls { get; init; } = new Dictionary<string, string>();
    public string? SelectedFolder { get; init; }
    public IReadOnlyList<string> SelectedRelativePaths { get; init; } = [];
    public int? LineNumber { get; init; }
    public int? ColumnNumber { get; init; }
    /// <summary>Exact provider enumeration order, after global options. Null uses file options then AdditionalOptions; empty declares no provider options.</summary>
    public IReadOnlyList<string>? ProviderOptions { get; init; }
    /// <summary>Values already supplied by a provider, without another escaping pass. Global options retain their original precedence.</summary>
    public IReadOnlyList<ScriptOptionValues> AdditionalOptions { get; init; } = [];
    public ScriptArgumentPlatform ArgumentPlatform { get; init; } = ScriptArgumentPlatform.Posix;
    public CultureInfo Culture { get; init; } = CultureInfo.CurrentCulture;
    public string GitExecutable { get; init; } = "git";
    public string ApplicationExecutable { get; init; } = "gitextensions";
    public string OpenUrlExecutable { get; init; } = "xdg-open";

    public ScriptContext Snapshot() => this with
    {
        SelectedRevisions = Array.AsReadOnly(SelectedRevisions.ToArray()),
        SelectedReferences = Array.AsReadOnly(SelectedReferences.ToArray()),
        CurrentReferences = Array.AsReadOnly(CurrentReferences.ToArray()),
        SelectedRelativePaths = Array.AsReadOnly(SelectedRelativePaths.ToArray()),
        ProviderOptions = ProviderOptions is null ? null : Array.AsReadOnly(ProviderOptions.ToArray()),
        AdditionalOptions = Array.AsReadOnly(AdditionalOptions.Select(option => option with { Values = Array.AsReadOnly(option.Values.ToArray()) }).ToArray()),
        BranchRemotes = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(BranchRemotes, StringComparer.Ordinal)),
        RemoteUrls = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(RemoteUrls, StringComparer.Ordinal)),
        Culture = CultureInfo.ReadOnly((CultureInfo)Culture.Clone())
    };
}

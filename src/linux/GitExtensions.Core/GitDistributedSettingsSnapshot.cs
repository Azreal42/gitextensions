using System.Collections.ObjectModel;
using IOPath = System.IO.Path;

namespace GitExtensions.Linux;

public enum GitDistributedSettingsScope { Effective, Local, Distributed, Global }

/// <summary>One destination and only its intended keys. Applying a plan is explicitly sequential, not a cross-file transaction.</summary>
public sealed record GitDistributedSettingsDestination(GitDistributedSettingsScope Scope, GitXmlSettingsSnapshot Snapshot,
    IReadOnlyDictionary<string, string?> Intentions);

/// <summary>Immutable raw resolution and effective-write routing, shared by settings UI and workflow consumers.</summary>
public sealed class GitDistributedSettingsSnapshot
{
    public const string FileName = "GitExtensions.settings";

    public GitDistributedSettingsSnapshot(GitXmlSettingsSnapshot global, GitXmlSettingsSnapshot? local = null,
        GitXmlSettingsSnapshot? distributed = null)
    {
        Global = global ?? throw new ArgumentNullException(nameof(global)); Local = local; Distributed = distributed;
    }

    public GitXmlSettingsSnapshot Global { get; }
    public GitXmlSettingsSnapshot? Local { get; }
    public GitXmlSettingsSnapshot? Distributed { get; }

    /// <summary>The global file alone works outside a repository; bare repositories have no distributed working-tree file.</summary>
    public static async Task<GitDistributedSettingsSnapshot> LoadAsync(string globalPath, GitRepositoryInfo? repository = null,
        CancellationToken cancellationToken = default)
    {
        GitXmlSettingsSnapshot global = await GitXmlSettingsStore.LoadAsync(globalPath, cancellationToken).ConfigureAwait(false);
        if (repository is null) { return new(global); }
        GitXmlSettingsSnapshot local = await GitXmlSettingsStore.LoadAsync(IOPath.Combine(repository.CommonDirectory, FileName), cancellationToken).ConfigureAwait(false);
        GitXmlSettingsSnapshot? distributed = repository.IsBare ? null
            : await GitXmlSettingsStore.LoadAsync(IOPath.Combine(repository.RootPath, FileName), cancellationToken).ConfigureAwait(false);
        return new(global, local, distributed);
    }

    public bool IsAvailable(GitDistributedSettingsScope scope) => scope switch
    {
        GitDistributedSettingsScope.Effective or GitDistributedSettingsScope.Global => true,
        GitDistributedSettingsScope.Local => Local is not null,
        GitDistributedSettingsScope.Distributed => Distributed is not null,
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    public GitXmlSettingsSnapshot GetSnapshot(GitDistributedSettingsScope scope) => scope switch
    {
        GitDistributedSettingsScope.Global => Global,
        GitDistributedSettingsScope.Local => Local ?? throw new InvalidOperationException("Local settings require a repository."),
        GitDistributedSettingsScope.Distributed => Distributed ?? throw new InvalidOperationException("Distributed settings require a non-bare working tree."),
        GitDistributedSettingsScope.Effective => throw new ArgumentException("Effective settings are resolved across files, not stored in one file.", nameof(scope)),
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    /// <summary>Resolve the first non-null raw value before any business parsing. Empty/unknown values mask lower layers.</summary>
    public string? GetValue(string key, GitDistributedSettingsScope scope = GitDistributedSettingsScope.Effective)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (scope != GitDistributedSettingsScope.Effective) { return Raw(GetSnapshot(scope), key); }
        return Raw(Local, key) ?? Raw(Distributed, key) ?? Raw(Global, key);
    }

    public GitDistributedSettingsScope? GetSource(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Raw(Local, key) is not null ? GitDistributedSettingsScope.Local
            : Raw(Distributed, key) is not null ? GitDistributedSettingsScope.Distributed
            : Raw(Global, key) is not null ? GitDistributedSettingsScope.Global : null;
    }

    /// <summary>Only compute destinations. Effective never writes distributed settings; changed explicit views retain their own destination.</summary>
    public IReadOnlyList<GitDistributedSettingsDestination> PlanChanges(GitDistributedSettingsScope scope,
        IReadOnlyDictionary<string, string?> intentions)
    {
        ArgumentNullException.ThrowIfNull(intentions);
        if (!IsAvailable(scope)) { _ = GetSnapshot(scope); }
        Dictionary<GitDistributedSettingsScope, Dictionary<string, string?>> plans = [];
        foreach ((string key, string? value) in intentions)
        {
            ArgumentNullException.ThrowIfNull(key);
            GitDistributedSettingsScope destination = scope;
            if (scope == GitDistributedSettingsScope.Effective)
            {
                if (GetValue(key) == value) { continue; }
                destination = Raw(Local, key) is not null || Raw(Distributed, key) is not null
                    ? GitDistributedSettingsScope.Local : GitDistributedSettingsScope.Global;
            }
            GitXmlSettingsSnapshot target = GetSnapshot(destination);
            if (value is null ? !target.Values.ContainsKey(key) : Raw(target, key) == value) { continue; }
            if (!plans.TryGetValue(destination, out Dictionary<string, string?>? edits)) { plans.Add(destination, edits = new(StringComparer.Ordinal)); }
            edits.Add(key, value);
        }
        // Match the existing explicit scope order; each completed destination can be acknowledged independently.
        return Array.AsReadOnly(new[] { GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global }
            .Where(plans.ContainsKey).Select(destination => new GitDistributedSettingsDestination(destination, GetSnapshot(destination),
                new ReadOnlyDictionary<string, string?>(plans[destination]))).ToArray());
    }

    /// <summary>Rebase only an actually completed file, retaining the other baselines after partial saves.</summary>
    public GitDistributedSettingsSnapshot WithSaved(GitDistributedSettingsScope scope, GitXmlSettingsSnapshot saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (GetSnapshot(scope).Path != saved.Path) { throw new ArgumentException("The saved settings belong to another destination.", nameof(saved)); }
        return scope switch
        {
            GitDistributedSettingsScope.Global => new(saved, Local, Distributed),
            GitDistributedSettingsScope.Local => new(Global, saved, Distributed),
            GitDistributedSettingsScope.Distributed => new(Global, Local, saved),
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };
    }

    private static string? Raw(GitXmlSettingsSnapshot? source, string key)
        => source is not null && source.Values.TryGetValue(key, out string? value) ? value : null;
}

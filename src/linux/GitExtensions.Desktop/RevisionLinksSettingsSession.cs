using GitCommands.ExternalLinks;

namespace GitExtensions.Linux.Desktop;

/// <summary>Independent additive collection drafts on the Settings window's accepted XML workspace.</summary>
internal sealed class RevisionLinksSettingsSession
{
    private static readonly GitDistributedSettingsScope[] Order = [GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global];
    private readonly DistributedSettingsWorkspace _workspace;
    private readonly Dictionary<GitDistributedSettingsScope, ExternalLinkDefinition[]> _drafts = [];
    internal GitRepository? Repository { get; }
    internal bool Loaded => _workspace.Loaded;
    internal Exception? LoadError => _workspace.LoadError;
    internal bool HasRepository => Repository is not null;
    internal bool HasDistributed => _workspace.Snapshot?.Distributed is not null;
    internal GitDistributedSettingsScope InitialScope => HasRepository ? GitDistributedSettingsScope.Effective : GitDistributedSettingsScope.Global;
    internal RevisionLinksSettingsSession(GitRepository? repository, DistributedSettingsWorkspace? workspace = null)
    { Repository = repository; _workspace = workspace ?? new(repository); }
    internal Task InitializeAsync(CancellationToken token) => _workspace.InitializeAsync(token);
    internal string? ScopePath(GitDistributedSettingsScope scope) => scope != GitDistributedSettingsScope.Effective && _workspace.Snapshot?.IsAvailable(scope) == true
        ? _workspace.Snapshot.GetSnapshot(scope).Path : null;
    internal IReadOnlyList<ExternalLinkDefinition> Read(GitDistributedSettingsScope scope)
    {
        if (!Loaded) { return []; }
        if (scope == GitDistributedSettingsScope.Effective)
        { return Order.Where(_workspace.Snapshot!.IsAvailable).SelectMany(Read).ToArray(); }
        return _drafts.TryGetValue(scope, out ExternalLinkDefinition[]? draft) ? draft.Select(GitRevisionLinkCodec.Clone).ToArray()
            : GitRevisionLinkSettings.From(_workspace.Snapshot!).GetDefinitions(scope);
    }
    internal IReadOnlyList<GitRevisionLinkDiagnostic> Diagnostics(GitDistributedSettingsScope scope)
        => Loaded ? GitRevisionLinkSettings.From(_workspace.Snapshot!).GetDiagnostics(scope) : [];
    internal void Set(GitDistributedSettingsScope scope, IReadOnlyList<ExternalLinkDefinition> definitions)
    {
        if (scope == GitDistributedSettingsScope.Effective) { throw new InvalidOperationException("Effective revision links are read-only."); }
        GitDistributedSettingsSnapshot snapshot = _workspace.Snapshot ?? throw new InvalidOperationException("Revision links have not been loaded.");
        _ = snapshot.GetSnapshot(scope);
        // Compare semantic drafts to semantic originals, but never rewrite an untouched raw/unknown document.
        string? original = GitRevisionLinkCodec.Encode(GitRevisionLinkSettings.From(snapshot).GetDefinitions(scope));
        if (GitRevisionLinkCodec.Encode(definitions) == original) { _drafts.Remove(scope); }
        else { _drafts[scope] = definitions.Select(GitRevisionLinkCodec.Clone).ToArray(); }
    }
    internal async Task SaveFilesAsync(CancellationToken token)
    {
        if (_drafts.Count == 0) { return; }
        if (!Loaded) { throw new InvalidOperationException("Revision link settings could not be loaded."); }
        foreach (GitDistributedSettingsScope scope in Order)
        {
            if (!_drafts.TryGetValue(scope, out ExternalLinkDefinition[]? draft)) { continue; }
            GitDistributedSettingsSnapshot before = _workspace.Snapshot!;
            IReadOnlyList<GitDistributedSettingsDestination> plan = GitRevisionLinkSettings.From(before).PlanChanges(scope, draft);
            foreach (GitDistributedSettingsDestination destination in plan)
            {
                GitXmlSettingsSnapshot saved = await GitXmlSettingsStore.SaveChangesAsync(destination.Snapshot, destination.Intentions, token);
                _workspace.AcceptSaved(before.WithSaved(destination.Scope, saved));
                _drafts.Remove(scope);
                RevisionLinksSettingsPublication.Publish(saved.Path);
            }
            if (plan.Count == 0) { _drafts.Remove(scope); }
        }
    }
}

internal static class RevisionLinksSettingsPublication
{
    internal static event Action<string>? Saved;
    internal static void Publish(string path) => Saved?.Invoke(path);
}

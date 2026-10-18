using System.Globalization;
using System.Xml;
using GitExtensions.Desktop.Controls.Graph;

namespace GitExtensions.Linux.Desktop;

internal sealed record DetailedScopeValues(bool? RemoteBranches, bool? AddMergeLog, string LogCount);

/// <summary>Shared key identities and typed reads match DetailedSettings/SettingsSource on Windows.</summary>
internal static class DetailedRepositorySettings
{
    internal const string RemoteBranches = "Detailed.GetRemoteBranchesDirectlyFromRemote";
    internal const string AddMergeLog = "Detailed.AddMergeLogMessages";
    internal const string MergeLogCount = "Detailed.MergeLogMessagesCount";
    internal static string GlobalPath => Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "GitExtensions.settings");
    internal static async Task<GitDistributedSettingsSnapshot> LoadAsync(GitRepository? repository, CancellationToken token)
        => await GitDistributedSettingsSnapshot.LoadAsync(GlobalPath,
            repository is null ? null : await repository.GetInfoAsync(token), token);
    internal static bool? Boolean(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ? true
        : string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ? false : null;
    internal static int? Number(string? value) => int.TryParse(value, out int result) ? result : null;
    internal static string? BooleanText(bool? value) => value is null ? null : value.Value ? "true" : "false";
    internal static string? NumberText(string? value) => Number(value)?.ToString(CultureInfo.CurrentCulture);

    internal static GitDistributedSettingsSnapshot Replace(GitDistributedSettingsSnapshot previous,
        GitDistributedSettingsScope scope, GitXmlSettingsSnapshot saved) => scope switch
    {
        GitDistributedSettingsScope.Local => new(previous.Global, saved, previous.Distributed),
        GitDistributedSettingsScope.Distributed => new(previous.Global, previous.Local, saved),
        GitDistributedSettingsScope.Global => new(saved, previous.Local, previous.Distributed),
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    // Consumers save Effective intents. Each completed destination is acknowledged before
    // another write, so failures retain their actual effects and never imply a rollback.
    internal static async Task<GitDistributedSettingsSnapshot> SaveEffectiveAsync(GitDistributedSettingsSnapshot snapshot,
        IReadOnlyDictionary<string, string?> intentions, Action<GitDistributedSettingsSnapshot>? saved, CancellationToken token)
    {
        GitDistributedSettingsSnapshot current = snapshot;
        IReadOnlyList<GitDistributedSettingsDestination> plan = snapshot.PlanChanges(GitDistributedSettingsScope.Effective, intentions);
        if (plan.Count > 0) { await ValidateContextAsync(snapshot, token); }
        foreach (GitDistributedSettingsDestination destination in plan)
        {
            GitXmlSettingsSnapshot file = await GitXmlSettingsStore.SaveChangesAsync(destination.Snapshot, destination.Intentions, token);
            current = Replace(current, destination.Scope, file); saved?.Invoke(current);
        }
        return current;
    }

    internal static async Task ValidateContextAsync(GitDistributedSettingsSnapshot snapshot, CancellationToken token)
    {
        IReadOnlyDictionary<string, string?> noChanges = new Dictionary<string, string?>();
        foreach (GitXmlSettingsSnapshot? file in new[] { snapshot.Local, snapshot.Distributed, snapshot.Global })
        { if (file is not null) { _ = await GitXmlSettingsStore.SaveChangesAsync(file, noChanges, token); } }
    }

    internal static async Task<GitDistributedSettingsSnapshot> ImportLegacyAsync(GitDistributedSettingsSnapshot snapshot,
        bool addLog, int count, Action<GitDistributedSettingsSnapshot>? saved, CancellationToken token, IReadOnlySet<string>? excluded = null)
    {
        Dictionary<string, string?> missing = new(StringComparer.Ordinal);
        if (excluded?.Contains(AddMergeLog) != true && snapshot.GetValue(AddMergeLog) is null) { missing[AddMergeLog] = BooleanText(addLog); }
        if (excluded?.Contains(MergeLogCount) != true && snapshot.GetValue(MergeLogCount) is null) { missing[MergeLogCount] = NumberText(count.ToString()); }
        if (missing.Count == 0) { return snapshot; }
        await ValidateContextAsync(snapshot, token);
        // Historical JSON values were per repository. Import unresolved values locally,
        // so their first explicit migration cannot change another repository's defaults.
        GitDistributedSettingsScope destination = snapshot.Local is null ? GitDistributedSettingsScope.Global : GitDistributedSettingsScope.Local;
        GitXmlSettingsSnapshot file = await GitXmlSettingsStore.SaveChangesAsync(snapshot.GetSnapshot(destination), missing, token);
        GitDistributedSettingsSnapshot current = snapshot.WithSaved(destination, file); saved?.Invoke(current); return current;
    }
}

/// <summary>The page owns drafts; the host owns publication of existing Desktop preferences.</summary>
internal sealed class DetailedSettingsSession
{
    private readonly GitRepository? _repository;
    private readonly Dictionary<GitDistributedSettingsScope, Dictionary<string, string?>> _intentions = [];
    private GitDistributedSettingsSnapshot? _snapshot;
    private MergePreferences? _legacyLog;
    private string? _legacyRepository;
    private bool _legacyMigrationRequested;
    private readonly HashSet<string> _legacyExcludedKeys = new(StringComparer.Ordinal);
    private RevisionGraphOptions _graphBaseline, _acceptedGraph;
    internal RevisionGraphOptions Graph { get; set; }
    internal Exception? LoadError { get; private set; }
    internal bool Loaded => _snapshot is not null && LoadError is null;
    internal bool HasRepository => _repository is not null;
    internal bool HasDistributed => _snapshot?.Distributed is not null;
    internal GitDistributedSettingsScope InitialScope => HasRepository ? GitDistributedSettingsScope.Effective : GitDistributedSettingsScope.Global;
    internal DetailedSettingsSession(GitRepository? repository, DesktopPreferences desktop)
    {
        _repository = repository; Graph = _graphBaseline = _acceptedGraph = DetailedGraphSettings.From(desktop);
    }

    internal async Task InitializeAsync(CancellationToken token)
    {
        try
        {
            _snapshot = await DetailedRepositorySettings.LoadAsync(_repository, token);
            if (_repository is not null)
            {
                _legacyRepository = (await _repository.GetInfoAsync(token)).CommonDirectory;
                _legacyLog = await Task.Run(() => new MergePreferencesStore().LegacyLog(_legacyRepository), token);
            }
            LoadError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException or InvalidDataException
            or GitCommandException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { LoadError = error; }
    }

    private GitXmlSettingsSnapshot? File(GitDistributedSettingsScope scope) => scope switch
    {
        GitDistributedSettingsScope.Local => _snapshot?.Local,
        GitDistributedSettingsScope.Distributed => _snapshot?.Distributed,
        GitDistributedSettingsScope.Global => _snapshot?.Global,
        _ => null
    };
    private string? Raw(GitDistributedSettingsScope scope, string key)
    {
        if (scope == GitDistributedSettingsScope.Effective)
        { return Raw(GitDistributedSettingsScope.Local, key) ?? Raw(GitDistributedSettingsScope.Distributed, key) ?? Raw(GitDistributedSettingsScope.Global, key); }
        return _intentions.TryGetValue(scope, out Dictionary<string, string?>? values) && values.TryGetValue(key, out string? value)
            ? value : File(scope)?.Values.GetValueOrDefault(key);
    }
    internal DetailedScopeValues Read(GitDistributedSettingsScope scope)
    {
        bool effective = scope == GitDistributedSettingsScope.Effective;
        bool? remote = DetailedRepositorySettings.Boolean(Raw(scope, DetailedRepositorySettings.RemoteBranches));
        bool? log = DetailedRepositorySettings.Boolean(Raw(scope, DetailedRepositorySettings.AddMergeLog));
        int? count = DetailedRepositorySettings.Number(Raw(scope, DetailedRepositorySettings.MergeLogCount));
        if (effective && _legacyLog is not null)
        {
            if (Raw(scope, DetailedRepositorySettings.AddMergeLog) is null) { log = _legacyLog.AddLog; }
            if (Raw(scope, DetailedRepositorySettings.MergeLogCount) is null) { count = _legacyLog.LogCount; }
        }
        return new(effective ? remote ?? false : remote, effective ? log ?? false : log,
            (effective ? count ?? 20 : count)?.ToString(CultureInfo.CurrentCulture) ?? "");
    }
    internal void Set(GitDistributedSettingsScope scope, string key, string? value)
    {
        if (scope == GitDistributedSettingsScope.Effective) { throw new InvalidOperationException("The Effective page is read-only."); }
        GitXmlSettingsSnapshot file = File(scope) ?? throw new InvalidOperationException("This settings scope is unavailable.");
        if (!_intentions.TryGetValue(scope, out Dictionary<string, string?>? changes)) { _intentions.Add(scope, changes = new(StringComparer.Ordinal)); }
        if (value is null ? !file.Values.ContainsKey(key) : file.Values.GetValueOrDefault(key) == value) { changes.Remove(key); }
        else { changes[key] = value; }
    }
    internal string? ScopePath(GitDistributedSettingsScope scope) => File(scope)?.Path;

    internal DesktopPreferences MergeDesktop(DesktopPreferences desired)
    {
        DesktopPreferences latest = new PreferencesStore().Load();
        return desired with
        {
            MergeGraphLanesHavingCommonParent = Pick(_graphBaseline.MergeGraphLanesHavingCommonParent, Graph.MergeGraphLanesHavingCommonParent, latest.MergeGraphLanesHavingCommonParent),
            RenderGraphWithDiagonals = Pick(_graphBaseline.RenderGraphWithDiagonals, Graph.RenderGraphWithDiagonals, latest.RenderGraphWithDiagonals),
            StraightenGraphDiagonals = Pick(_graphBaseline.StraightenGraphDiagonals, Graph.StraightenGraphDiagonals, latest.StraightenGraphDiagonals)
        };
    }
    internal void DesktopSaved(DesktopPreferences accepted)
    { _graphBaseline = Graph; _acceptedGraph = DetailedGraphSettings.From(accepted); }

    internal async Task SaveFilesAsync(CancellationToken token)
    {
        // Only final, actual edits trigger legacy migration. Remember explicit removals
        // across partial failures so retry can never resurrect a removed JSON value.
        if (_legacyLog is not null)
        {
            foreach (string key in _intentions.Values.SelectMany(changes => changes.Keys)
                .Where(key => key is DetailedRepositorySettings.AddMergeLog or DetailedRepositorySettings.MergeLogCount))
            { _legacyMigrationRequested = true; _legacyExcludedKeys.Add(key); }
        }
        foreach (GitDistributedSettingsScope scope in new[] { GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global })
        {
            if (!_intentions.TryGetValue(scope, out Dictionary<string, string?>? changes) || changes.Count == 0) { continue; }
            GitXmlSettingsSnapshot expected = File(scope) ?? throw new InvalidOperationException("This settings scope is unavailable.");
            GitXmlSettingsSnapshot accepted = await GitXmlSettingsStore.SaveChangesAsync(expected, new Dictionary<string, string?>(changes), token);
            _snapshot = DetailedRepositorySettings.Replace(_snapshot!, scope, accepted); changes.Clear();
        }
        if (_legacyMigrationRequested && _legacyLog is not null && _snapshot is not null)
        {
            DetailedScopeValues effective = Read(GitDistributedSettingsScope.Effective);
            _snapshot = await DetailedRepositorySettings.ImportLegacyAsync(_snapshot, effective.AddMergeLog ?? false,
                DetailedRepositorySettings.Number(effective.LogCount) ?? 20, accepted => _snapshot = accepted, token, _legacyExcludedKeys);
            if (_legacyRepository is not null) { await Task.Run(() => new MergePreferencesStore().MarkLogMigrated(_legacyRepository), token); }
            _legacyLog = null;
        }
        _legacyMigrationRequested = false; _legacyExcludedKeys.Clear();
        Graph = _graphBaseline = _acceptedGraph;
    }
    private static T Pick<T>(T before, T draft, T latest) => EqualityComparer<T>.Default.Equals(before, draft) ? latest : draft;
}

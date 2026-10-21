using System.Collections.ObjectModel;
using System.Globalization;

namespace GitExtensions.Linux.Desktop;

internal enum DeleteUnusedBranchesSettingKind { Number, Text, Boolean }

internal sealed record DeleteUnusedBranchesSetting(string Control, string Caption, string EngineKey,
    DeleteUnusedBranchesSettingKind Kind, string DefaultValue)
{
    internal string Key => DeleteUnusedBranchesSettingsCatalog.Prefix + Caption;
    internal string OldKey => DeleteUnusedBranchesSettingsCatalog.OldPrefix + Caption;
}

/// <summary>GetSettings order, names, defaults and legacy prefix from the Windows plugin.</summary>
internal static class DeleteUnusedBranchesSettingsCatalog
{
    internal const string Prefix = "dc3ca904-b9a5-4fe8-bf63-5b8ee9c2ddac.";
    internal const string OldPrefix = "Delete obsolete branches";
    internal static IReadOnlyList<DeleteUnusedBranchesSetting> Fields { get; } = Array.AsReadOnly<DeleteUnusedBranchesSetting>(
    [
        new("_daysOlderThan", "Delete obsolete branches older than (days)", "olderThanDays", DeleteUnusedBranchesSettingKind.Number, "30"),
        new("_mergedInBranch", "Branch where all branches should be merged in", "mergedInto", DeleteUnusedBranchesSettingKind.Text, "HEAD"),
        new("_deleteRemoteBranchesFromFlag", "Delete obsolete branches from remote", "remoteBranches", DeleteUnusedBranchesSettingKind.Boolean, "false"),
        new("_remoteName", "Remote name obsoleted branches should be deleted from", "remote", DeleteUnusedBranchesSettingKind.Text, "origin"),
        new("_useRegexToFilterBranchesFlag", "Use regex to filter branches to delete", "useRegex", DeleteUnusedBranchesSettingKind.Boolean, "false"),
        new("_regexFilter", "Regex to filter branches to delete", "pattern", DeleteUnusedBranchesSettingKind.Text, "/(feature|develop)/"),
        new("_regexCaseInsensitiveFlag", "Is regex filter case insensitive?", "ignoreCase", DeleteUnusedBranchesSettingKind.Boolean, "false"),
        new("_regexInvertedFlag", "Search branches that does not match regex", "invertPattern", DeleteUnusedBranchesSettingKind.Boolean, "false"),
        new("_includeUnmergedBranchesFlag", "Delete unmerged branches", "includeUnmerged", DeleteUnusedBranchesSettingKind.Boolean, "false")
    ]);
    internal static DeleteUnusedBranchesSetting Find(string control) => Fields.Single(item => item.Control == control);
    internal static bool? Boolean(string? value) => bool.TryParse(value, out bool result) ? result : null;
    internal static int? Number(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out int result) ? result : null;
}

/// <summary>Only explicit field intentions use the existing shared XML workspace and CAS writer.</summary>
internal sealed class DeleteUnusedBranchesSettingsSession
{
    private static readonly GitDistributedSettingsScope[] Order =
        [GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global];
    private readonly DistributedSettingsWorkspace _workspace;
    private readonly Dictionary<GitDistributedSettingsScope, Dictionary<string, string?>> _intentions = [];
    private readonly IReadOnlyDictionary<string, string?> _legacy;
    internal GitRepository? Repository { get; }
    internal bool Loaded => _workspace.Loaded;
    internal Exception? LoadError => _workspace.LoadError;
    internal bool HasRepository => Repository is not null;
    internal bool HasDistributed => _workspace.Snapshot?.Distributed is not null;
    internal bool HasChanges => _intentions.Values.Any(values => values.Count > 0);
    internal GitDistributedSettingsScope InitialScope => HasRepository ? GitDistributedSettingsScope.Effective : GitDistributedSettingsScope.Global;

    internal DeleteUnusedBranchesSettingsSession(GitRepository? repository,
        IReadOnlyDictionary<string, string?>? legacyValues = null, DistributedSettingsWorkspace? workspace = null)
    {
        Repository = repository; _workspace = workspace ?? new(repository);
        _legacy = new ReadOnlyDictionary<string, string?>(legacyValues is null ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(legacyValues, StringComparer.Ordinal));
    }
    internal Task InitializeAsync(CancellationToken token) => _workspace.InitializeAsync(token);
    internal string? ScopePath(GitDistributedSettingsScope scope)
        => scope != GitDistributedSettingsScope.Effective && _workspace.Snapshot?.IsAvailable(scope) == true
            ? _workspace.Snapshot.GetSnapshot(scope).Path : null;

    private string? Raw(GitDistributedSettingsScope scope, string key)
    {
        if (scope == GitDistributedSettingsScope.Effective)
        { return Raw(GitDistributedSettingsScope.Local, key) ?? Raw(GitDistributedSettingsScope.Distributed, key) ?? Raw(GitDistributedSettingsScope.Global, key); }
        if (_intentions.TryGetValue(scope, out Dictionary<string, string?>? changes) && changes.TryGetValue(key, out string? value)) { return value; }
        return _workspace.Snapshot?.IsAvailable(scope) == true ? _workspace.Snapshot.GetSnapshot(scope).Values.GetValueOrDefault(key) : null;
    }

    internal string? Read(GitDistributedSettingsScope scope, DeleteUnusedBranchesSetting setting)
    {
        // GitPluginSettingsContainer asks the entire source for the GUID key first,
        // then the old Description key. An old local key cannot mask a new global one.
        string? value = Raw(scope, setting.Key) ?? Raw(scope, setting.OldKey);
        if (scope != GitDistributedSettingsScope.Effective) { return value; }
        if (value is null && _legacy.TryGetValue(setting.EngineKey, out string? old)) { value = old; }
        if (value is null && setting.EngineKey == "useRegex" && !_legacy.ContainsKey("useRegex")
            && !string.IsNullOrEmpty(_legacy.GetValueOrDefault("pattern"))) { value = "true"; }
        return setting.Kind switch
        {
            DeleteUnusedBranchesSettingKind.Boolean => (DeleteUnusedBranchesSettingsCatalog.Boolean(value)
                ?? bool.Parse(setting.DefaultValue)) ? "true" : "false",
            DeleteUnusedBranchesSettingKind.Number => (DeleteUnusedBranchesSettingsCatalog.Number(value)
                ?? int.Parse(setting.DefaultValue, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
            _ => value ?? setting.DefaultValue
        };
    }

    internal void Set(GitDistributedSettingsScope scope, DeleteUnusedBranchesSetting setting, string? value)
    {
        if (scope == GitDistributedSettingsScope.Effective) { throw new InvalidOperationException("Effective plugin settings are read-only."); }
        if (!DeleteUnusedBranchesSettingsCatalog.Fields.Contains(setting)) { throw new ArgumentException("Unknown plugin setting.", nameof(setting)); }
        GitDistributedSettingsSnapshot snapshot = _workspace.Snapshot ?? throw new InvalidOperationException("Plugin settings have not been loaded.");
        GitXmlSettingsSnapshot file = snapshot.GetSnapshot(scope);
        if (!_intentions.TryGetValue(scope, out Dictionary<string, string?>? changes)) { _intentions.Add(scope, changes = new(StringComparer.Ordinal)); }
        if (value is null ? !file.Values.ContainsKey(setting.Key) : file.Values.GetValueOrDefault(setting.Key) == value) { changes.Remove(setting.Key); }
        else { changes[setting.Key] = value; }
    }

    internal IReadOnlyDictionary<string, string?> EngineValues()
    {
        if (!Loaded) { throw new InvalidOperationException("Plugin settings could not be loaded.", LoadError); }
        Dictionary<string, string?> values = new(_legacy, StringComparer.Ordinal) { ["operation"] = "preview", ["branches"] = "" };
        foreach (DeleteUnusedBranchesSetting setting in DeleteUnusedBranchesSettingsCatalog.Fields)
        { values[setting.EngineKey] = Read(GitDistributedSettingsScope.Effective, setting); }
        return new ReadOnlyDictionary<string, string?>(values);
    }

    internal bool ValidateEdits(out GitDistributedSettingsScope scope)
    {
        DeleteUnusedBranchesSetting days = DeleteUnusedBranchesSettingsCatalog.Find("_daysOlderThan");
        foreach (GitDistributedSettingsScope candidate in Order)
        {
            if (_intentions.TryGetValue(candidate, out Dictionary<string, string?>? edits)
                && edits.TryGetValue(days.Key, out string? text) && text is not null
                && (DeleteUnusedBranchesSettingsCatalog.Number(text) is not { } value || value is < 0 or > 100000))
            { scope = candidate; return false; }
        }
        scope = InitialScope; return true;
    }

    internal async Task SaveFilesAsync(CancellationToken token)
    {
        if (!HasChanges) { return; }
        if (!Loaded) { throw new InvalidOperationException("Plugin settings could not be loaded.", LoadError); }
        if (!ValidateEdits(out _)) { throw new ArgumentException("Choose an age between 0 and 100000 days."); }
        foreach (GitDistributedSettingsScope scope in Order)
        {
            if (!_intentions.TryGetValue(scope, out Dictionary<string, string?>? changes) || changes.Count == 0) { continue; }
            GitDistributedSettingsSnapshot before = _workspace.Snapshot!;
            foreach (GitDistributedSettingsDestination destination in before.PlanChanges(scope, changes))
            {
                GitXmlSettingsSnapshot saved = await GitXmlSettingsStore.SaveChangesAsync(destination.Snapshot, destination.Intentions, token);
                _workspace.AcceptSaved(before.WithSaved(destination.Scope, saved));
                // Acknowledge before any later scope can fail; retry never replays this file.
                changes.Clear();
            }
            changes.Clear();
        }
    }
}

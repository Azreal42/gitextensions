using System.Collections.ObjectModel;
using System.Globalization;

namespace GitExtensions.Linux.Desktop;

internal enum PluginScopedSettingKind { Text, Integer, Boolean }

internal sealed record PluginScopedSetting(string Control, string Name, string Caption, string RuntimeKey,
    PluginScopedSettingKind Kind, string DefaultValue);

internal sealed record PluginScopedWarning(string Control, string Text, double Height);

internal sealed class PluginScopedSettingsDefinition
{
    internal Guid Id { get; }
    internal string Description { get; }
    internal string Category { get; }
    internal string ControlPrefix { get; }
    internal IReadOnlyList<PluginScopedSetting> Fields { get; }
    internal PluginScopedWarning? Warning { get; }
    internal PluginScopedSettingsDefinition(Guid id, string description, string category, string controlPrefix,
        IEnumerable<PluginScopedSetting> fields, PluginScopedWarning? warning = null)
    {
        Id = id; Description = description; Category = category; ControlPrefix = controlPrefix;
        Fields = Array.AsReadOnly(fields.ToArray()); Warning = warning;
        if (Fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != Fields.Count
            || Fields.Select(field => field.Control).Distinct(StringComparer.Ordinal).Count() != Fields.Count
            || Fields.Select(field => field.RuntimeKey).Distinct(StringComparer.Ordinal).Count() != Fields.Count)
        { throw new ArgumentException("Plugin setting names must be unique.", nameof(fields)); }
    }
    internal string Key(PluginScopedSetting setting) => Id + "." + setting.Name;
    internal string OldKey(PluginScopedSetting setting) => Description + setting.Name;
    internal PluginScopedSetting Find(string control) => Fields.Single(field => field.Control == control);
    internal static bool? Boolean(string? value) => bool.TryParse(value, out bool result) ? result : null;
    internal static int? Integer(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out int result) ? result : null;
}

internal sealed record PluginScopedSettingsSaveReceipt(GitDistributedSettingsScope Scope,
    IReadOnlyDictionary<string, string?> EffectiveValues, bool EffectiveChanged);

/// <summary>Explicit plugin field intentions over the Settings window's accepted XML baselines.</summary>
internal class PluginScopedSettingsSession
{
    private static readonly GitDistributedSettingsScope[] Order =
        [GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global];
    private readonly DistributedSettingsWorkspace _workspace;
    private readonly Dictionary<GitDistributedSettingsScope, Dictionary<string, string?>> _intentions = [];
    private readonly IReadOnlyDictionary<string, string?> _legacy;
    private PluginScopedSettingsSaveReceipt? _pendingPublication;
    internal PluginScopedSettingsDefinition Definition { get; }
    internal GitRepository? Repository { get; }
    internal bool Loaded => _workspace.Loaded;
    internal Exception? LoadError => _workspace.LoadError;
    internal bool HasRepository => Repository is not null;
    internal bool HasDistributed => _workspace.Snapshot?.Distributed is not null;
    internal bool HasChanges => _pendingPublication is not null || _intentions.Values.Any(changes => changes.Count > 0);
    internal bool HasPendingPublication => _pendingPublication is not null;
    internal GitDistributedSettingsScope InitialScope => HasRepository ? GitDistributedSettingsScope.Effective : GitDistributedSettingsScope.Global;

    internal PluginScopedSettingsSession(PluginScopedSettingsDefinition definition, GitRepository? repository,
        IReadOnlyDictionary<string, string?>? legacyValues = null, DistributedSettingsWorkspace? workspace = null)
    {
        Definition = definition; Repository = repository; _workspace = workspace ?? new(repository);
        _legacy = new ReadOnlyDictionary<string, string?>(legacyValues is null ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(legacyValues, StringComparer.Ordinal));
    }
    internal Task InitializeAsync(CancellationToken token) => _workspace.InitializeAsync(token);
    internal string? ScopePath(GitDistributedSettingsScope scope)
        => scope != GitDistributedSettingsScope.Effective && _workspace.Snapshot?.IsAvailable(scope) == true
            ? _workspace.Snapshot.GetSnapshot(scope).Path : null;

    private string? Raw(GitDistributedSettingsScope scope, string key, bool includeDrafts)
    {
        if (scope == GitDistributedSettingsScope.Effective)
        { return Raw(GitDistributedSettingsScope.Local, key, includeDrafts) ?? Raw(GitDistributedSettingsScope.Distributed, key, includeDrafts)
            ?? Raw(GitDistributedSettingsScope.Global, key, includeDrafts); }
        if (includeDrafts && _intentions.TryGetValue(scope, out Dictionary<string, string?>? edits)
            && edits.TryGetValue(key, out string? value)) { return value; }
        return _workspace.Snapshot?.IsAvailable(scope) == true ? _workspace.Snapshot.GetSnapshot(scope).Values.GetValueOrDefault(key) : null;
    }

    internal string? Read(GitDistributedSettingsScope scope, PluginScopedSetting setting) => Read(scope, setting, includeDrafts: true);
    private string? Read(GitDistributedSettingsScope scope, PluginScopedSetting setting, bool includeDrafts)
    {
        // Windows resolves a GUID key over the entire scoped source before trying
        // the legacy Description prefix. It does not choose a prefix per file.
        string? value = Raw(scope, Definition.Key(setting), includeDrafts) ?? Raw(scope, Definition.OldKey(setting), includeDrafts);
        if (scope != GitDistributedSettingsScope.Effective) { return value; }
        value ??= _legacy.GetValueOrDefault(setting.RuntimeKey);
        return setting.Kind switch
        {
            PluginScopedSettingKind.Boolean => (PluginScopedSettingsDefinition.Boolean(value) ?? bool.Parse(setting.DefaultValue)) ? "true" : "false",
            PluginScopedSettingKind.Integer => (PluginScopedSettingsDefinition.Integer(value)
                ?? int.Parse(setting.DefaultValue, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
            _ => value ?? setting.DefaultValue
        };
    }

    internal void Set(GitDistributedSettingsScope scope, PluginScopedSetting setting, string? value)
    {
        if (scope == GitDistributedSettingsScope.Effective) { throw new InvalidOperationException("Effective plugin settings are read-only."); }
        if (!Definition.Fields.Contains(setting)) { throw new ArgumentException("Unknown plugin setting.", nameof(setting)); }
        GitXmlSettingsSnapshot file = (_workspace.Snapshot ?? throw new InvalidOperationException("Plugin settings have not been loaded.")).GetSnapshot(scope);
        if (!_intentions.TryGetValue(scope, out Dictionary<string, string?>? edits)) { _intentions.Add(scope, edits = new(StringComparer.Ordinal)); }
        string key = Definition.Key(setting);
        if (value is null ? !file.Values.ContainsKey(key) : file.Values.GetValueOrDefault(key) == value) { edits.Remove(key); }
        else { edits[key] = value; }
    }

    // Runtime publication must never observe intentions belonging to a file that
    // has not been accepted. The page's Effective view may separately preview them.
    internal IReadOnlyDictionary<string, string?> EffectiveValues(bool includeDrafts = false)
    {
        if (!Loaded) { throw new InvalidOperationException("Plugin settings could not be loaded.", LoadError); }
        Dictionary<string, string?> result = new(_legacy, StringComparer.Ordinal);
        foreach (PluginScopedSetting setting in Definition.Fields)
        { result[setting.RuntimeKey] = Read(GitDistributedSettingsScope.Effective, setting, includeDrafts); }
        return new ReadOnlyDictionary<string, string?>(result);
    }

    internal bool ValidateEdits(out GitDistributedSettingsScope scope, out string? control, out string? error)
    {
        foreach (GitDistributedSettingsScope candidate in Order)
        {
            if (!_intentions.TryGetValue(candidate, out Dictionary<string, string?>? edits)) { continue; }
            foreach (PluginScopedSetting setting in Definition.Fields)
            {
                if (!edits.TryGetValue(Definition.Key(setting), out string? value) || value is null) { continue; }
                error = value.Contains('\0') ? "A plugin setting cannot contain a null character."
                    : setting.Kind == PluginScopedSettingKind.Integer && (PluginScopedSettingsDefinition.Integer(value) is not { } count || count < 0)
                        ? "Choose an integer between 0 and 2147483647."
                    : setting.Kind == PluginScopedSettingKind.Boolean && PluginScopedSettingsDefinition.Boolean(value) is null
                        ? "Choose true, false or an unset value." : ValidateValue(setting, value);
                if (error is not null) { scope = candidate; control = setting.Control; return false; }
            }
        }
        scope = InitialScope; control = error = null; return true;
    }
    protected virtual string? ValidateValue(PluginScopedSetting setting, string value) => null;

    internal async Task SaveFilesAsync(CancellationToken token, Func<PluginScopedSettingsSaveReceipt, Task>? publish = null)
    {
        if (!HasChanges) { return; } // An unreadable, untouched page cannot block other Settings pages.
        if (!Loaded) { throw new InvalidOperationException("Plugin settings could not be loaded.", LoadError); }
        if (!ValidateEdits(out _, out _, out string? error)) { throw new ArgumentException(error); }
        foreach (GitDistributedSettingsScope scope in Order)
        {
            if (!_intentions.TryGetValue(scope, out Dictionary<string, string?>? edits) || edits.Count == 0) { continue; }
            token.ThrowIfCancellationRequested();
            GitDistributedSettingsSnapshot before = _workspace.Snapshot!;
            foreach (GitDistributedSettingsDestination destination in before.PlanChanges(scope, edits))
            {
                IReadOnlyDictionary<string, string?> previous = EffectiveValues();
                GitXmlSettingsSnapshot accepted = await GitXmlSettingsStore.SaveChangesAsync(destination.Snapshot, destination.Intentions, token);
                _workspace.AcceptSaved(before.WithSaved(destination.Scope, accepted));
                // Acknowledge the file before awaiting host work or a later file.
                // Retry can publish again, but can never replay this accepted write.
                edits.Clear();
                IReadOnlyDictionary<string, string?> current = EffectiveValues();
                bool changed = Definition.Fields.Any(field => previous.GetValueOrDefault(field.RuntimeKey) != current.GetValueOrDefault(field.RuntimeKey));
                _pendingPublication = new(scope, current, changed || _pendingPublication?.EffectiveChanged == true);
                await PublishPendingAsync(publish);
            }
            // Another page may already have accepted this exact key/value in the
            // shared workspace; a zero-destination plan has no remaining intent.
            edits.Clear();
        }
        await PublishPendingAsync(publish);
    }

    private async Task PublishPendingAsync(Func<PluginScopedSettingsSaveReceipt, Task>? publish)
    {
        PluginScopedSettingsSaveReceipt? pending = _pendingPublication;
        if (pending is null) { return; }
        if (publish is not null) { await publish(pending); }
        _pendingPublication = null;
    }
}

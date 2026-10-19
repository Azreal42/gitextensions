namespace GitExtensions.Linux.Desktop;

/// <summary>Independent scope drafts. Read-only views always come from the published snapshot.</summary>
internal sealed class GitToolsDraftSession
{
    internal sealed record ValidationTarget(GitToolConfigurationView View, GitToolRole? Role = null,
        GitConfigurationField? Field = null, string? ToolPart = null);
    private readonly Dictionary<GitToolConfigurationView, ScopeDraft> _scopes = [];
    internal GitToolConfigurationSnapshot Snapshot { get; }

    internal GitToolsDraftSession(GitToolConfigurationSnapshot snapshot)
    {
        Snapshot = snapshot;
        foreach (GitToolConfigurationView view in Enum.GetValues<GitToolConfigurationView>())
        { _scopes.Add(view, new(snapshot.GetScope(view), snapshot.SupportsGuiMergeTool)); }
    }

    internal ScopeDraft Scope(GitToolConfigurationView view) => _scopes[view];
    internal bool IsDirty => BuildEdits().Count > 0;

    internal IReadOnlyList<GitToolConfigurationScopeEdit> BuildEdits()
    {
        List<GitToolConfigurationScopeEdit> edits = [];
        foreach (GitToolConfigurationView view in new[] { GitToolConfigurationView.Local, GitToolConfigurationView.Global })
        {
            ScopeDraft scope = Scope(view);
            List<GitToolRoleEdit> roles = [];
            foreach (GitToolRole role in Enum.GetValues<GitToolRole>())
            {
                RoleDraft draft = scope.Role(role);
                Fields fields = draft.Current;
                bool hasName = !string.IsNullOrWhiteSpace(draft.Name);
                GitToolValueEdit? path = hasName && fields.PathDirty ? new(fields.Path) : null;
                GitToolValueEdit? command = hasName && fields.CommandDirty ? new(fields.Command) : null;
                if (draft.NameDirty || path is not null || command is not null)
                { roles.Add(new(role, hasName ? draft.Name : "", draft.NameDirty, path, command)); }
            }
            List<GitConfigurationFieldEdit> settings = Enum.GetValues<GitConfigurationField>()
                .Where(field => scope.Setting(field).IsDirty).Select(field => new GitConfigurationFieldEdit(field, scope.Setting(field).Value)).ToList();
            if (roles.Count > 0 || settings.Count > 0)
            { edits.Add(new(view == GitToolConfigurationView.Local ? GitConfigScope.Local : GitConfigScope.Global, roles, settings)); }
        }
        return edits;
    }

    internal GitToolsDraftSession Rebase(GitToolConfigurationSnapshot after)
    {
        GitToolsDraftSession next = new(after);
        foreach (GitToolConfigurationView view in new[] { GitToolConfigurationView.Local, GitToolConfigurationView.Global })
        {
            foreach (GitConfigurationField field in Enum.GetValues<GitConfigurationField>())
            {
                SettingDraft old = Scope(view).Setting(field);
                if (old.IsDirty) { next.Scope(view).Setting(field).SetValue(old.Value); }
            }
            foreach (GitToolRole role in Enum.GetValues<GitToolRole>())
            {
                RoleDraft old = Scope(view).Role(role), current = next.Scope(view).Role(role);
                // Keep pending intent; actual completed fields become clean by comparison with readback.
                bool selectionPublished = string.Equals(string.IsNullOrWhiteSpace(old.Name) ? "" : old.Name,
                    LastValue(current.Selection), StringComparison.Ordinal);
                if (old.NameDirty ? !selectionPublished : old.Current.PathDirty || old.Current.CommandDirty) { current.Name = old.Name; }
                foreach ((string name, Fields fields) in old.Visited)
                {
                    Fields target = current.ForName(name);
                    if (fields.PathDirty) { target.Path = fields.Path; }
                    if (fields.CommandDirty) { target.SetCommand(fields.Command, fields.AutoSuggest); }
                }
            }
        }
        return next;
    }

    internal bool Validate(out string? error, out ValidationTarget target)
    {
        target = new(GitToolConfigurationView.Local);
        foreach (GitToolConfigurationScopeEdit edit in BuildEdits())
        {
            GitToolConfigurationView view = edit.Scope == GitConfigScope.Local ? GitToolConfigurationView.Local : GitToolConfigurationView.Global;
            ScopeDraft scope = Scope(view);
            foreach (GitConfigurationFieldEdit value in edit.Settings ?? [])
            {
                target = new(view, Field: value.Field);
                SettingDraft setting = scope.Setting(value.Field);
                if (!scope.CanEdit(value.Field))
                { error = setting.Published.Restriction ?? scope.Snapshot.Diagnostic ?? "This configuration scope is read only."; return false; }
                if (value.Value?.Contains('\0') == true) { error = "Git configuration values cannot contain NUL."; return false; }
                if (value.Field == GitConfigurationField.AutoCrlf && value.Value is not (null or "true" or "input" or "false"))
                { error = "Choose true, input, false or Not set for line endings."; return false; }
                if (GitConfigurationFields.IsAdvancedBoolean(value.Field) && value.Value is not (null or "true" or "false"))
                { error = "Choose true, false or Not set for advanced Git settings."; return false; }
            }
            foreach (GitToolRoleEdit value in edit.Roles)
            {
                GitToolRole role = value.Role; RoleDraft draft = scope.Role(role); Fields fields = draft.Current;
                target = new(view, role);
                if (!scope.CanEdit(role)) { error = scope.Snapshot.Diagnostic ?? "This configuration scope is read only."; return false; }
                if (draft.Name.IndexOfAny(['\0', '\n', '\r']) >= 0) { error = "Tool names cannot contain NUL or a line break."; return false; }
                if (value.Path?.Value?.Contains('\0') == true)
                { target = new(view, role, ToolPart: "path"); error = "Tool paths cannot contain NUL."; return false; }
                if (value.Command?.Value?.Contains('\0') == true)
                { target = new(view, role, ToolPart: "command"); error = "Tool commands cannot contain NUL."; return false; }
                if (value.ChangeName && !draft.Selection.CanChange) { error = draft.Selection.Restriction; return false; }
                if (value.Path is not null && !fields.CanChangePath) { target = new(view, role, ToolPart: "path"); error = fields.PathRestriction; return false; }
                if (value.Command is not null && !fields.CanChangeCommand) { target = new(view, role, ToolPart: "command"); error = fields.CommandRestriction; return false; }
            }
        }
        error = null; return true;
    }

    internal sealed class ScopeDraft
    {
        private readonly Dictionary<GitToolRole, RoleDraft> _roles = [];
        private readonly Dictionary<GitConfigurationField, SettingDraft> _settings = [];
        private readonly bool _guiMerge;
        internal GitToolConfigurationScopeSnapshot Snapshot { get; }
        internal ScopeDraft(GitToolConfigurationScopeSnapshot snapshot, bool guiMerge)
        {
            Snapshot = snapshot; _guiMerge = guiMerge;
            foreach (GitToolRole role in Enum.GetValues<GitToolRole>())
            { _roles.Add(role, new(snapshot, role, guiMerge)); }
            foreach (GitConfigurationField field in Enum.GetValues<GitConfigurationField>())
            { _settings.Add(field, new(snapshot.GetSetting(field))); }
        }
        internal RoleDraft Role(GitToolRole role) => _roles[role];
        internal SettingDraft Setting(GitConfigurationField field) => _settings[field];
        internal bool CanEdit(GitConfigurationField field) => Snapshot.IsAvailable && Snapshot.CanWrite
            && Snapshot.View is GitToolConfigurationView.Local or GitToolConfigurationView.Global
            && Setting(field).Published.CanChange;
        internal bool CanEdit(GitToolRole role) => Snapshot.IsAvailable && Snapshot.CanWrite
            && Snapshot.View is GitToolConfigurationView.Local or GitToolConfigurationView.Global
            && (role != GitToolRole.Merge || _guiMerge);
    }

    internal sealed class SettingDraft
    {
        private bool _hasIntent;
        internal GitToolConfigurationValue Published { get; }
        internal string? Value { get; private set; }
        internal bool IsPresent => _hasIntent ? Value is not null : Published.Occurrences.Count > 0;
        internal bool IsDirty => _hasIntent && (Value is null ? Published.Occurrences.Count > 0
            : Published.Occurrences.Count == 0 || Value != Published.Occurrences.Last().Value);
        internal string DisplayValue => Published.Key == "credential.helper" && Published.Occurrences.Count > 1 && !_hasIntent
            ? string.Join(", ", Published.Occurrences.Select(entry => entry.Value)) : Value ?? "";

        internal SettingDraft(GitToolConfigurationValue published)
        { Published = published; Value = published.Occurrences.LastOrDefault()?.Value; }
        internal void SetValue(string? value) { Value = value; _hasIntent = true; }
        internal void RestorePublished() { Value = Published.Occurrences.LastOrDefault()?.Value; _hasIntent = false; }
    }

    internal sealed class RoleDraft
    {
        private readonly GitToolConfigurationScopeSnapshot _scope;
        private readonly GitToolRole _role;
        private readonly Dictionary<string, Fields> _fields = new(StringComparer.Ordinal);
        private readonly string _originalName;
        internal string Name { get; set; }
        internal bool NameDirty => !string.Equals(Name, _originalName, StringComparison.Ordinal);
        internal Fields Current => ForName(Name);
        internal IEnumerable<KeyValuePair<string, Fields>> Visited => _fields;
        internal GitToolConfigurationValue Selection { get; }
        internal bool UsesMergeFallback { get; }

        internal RoleDraft(GitToolConfigurationScopeSnapshot scope, GitToolRole role, bool guiMerge)
        {
            _scope = scope; _role = role;
            Selection = role == GitToolRole.Diff ? scope.SelectedDiffGui : scope.SelectedMergeGui;
            string gui = LastValue(Selection);
            UsesMergeFallback = role == GitToolRole.Merge && (!guiMerge || gui.Length == 0)
                && LastValue(scope.MergeToolFallback).Length > 0;
            _originalName = UsesMergeFallback ? LastValue(scope.MergeToolFallback) : gui;
            Name = _originalName;
        }

        internal Fields ForName(string name)
        {
            if (_fields.TryGetValue(name, out Fields? fields)) { return fields; }
            GitToolConfigurationDefinition? definition = null;
            if (!string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['\0', '\n', '\r']) < 0 && _scope.IsAvailable)
            { definition = _scope.GetTool(_role, name); }
            fields = new(definition); _fields.Add(name, fields); return fields;
        }

        internal IEnumerable<string> Names()
        {
            string prefix = _role == GitToolRole.Diff ? "difftool." : "mergetool.";
            return GitToolsCatalog.Names(_role).Concat(_scope.Occurrences.Select(value => value.Key)
                .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && (key.EndsWith(".path", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)))
                .Select(key => key[prefix.Length..key.LastIndexOf('.')]))
                .Append(Name).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        }
    }

    internal sealed class Fields
    {
        private readonly string _originalPath, _originalCommand;
        internal string Path { get; set; }
        internal string Command { get; private set; }
        internal bool AutoSuggest { get; private set; }
        internal bool PathDirty => EmptyToNull(Path) != EmptyToNull(_originalPath);
        internal bool CommandDirty => EmptyToNull(Command) != EmptyToNull(_originalCommand);
        internal bool CanChangePath { get; }
        internal bool CanChangeCommand { get; }
        internal string? PathRestriction { get; }
        internal string? CommandRestriction { get; }
        internal string PublishedPath => _originalPath;
        internal string PublishedCommand => _originalCommand;

        internal Fields(GitToolConfigurationDefinition? definition)
        {
            Path = _originalPath = definition is null ? "" : LastValue(definition.Path);
            Command = _originalCommand = definition is null ? "" : LastValue(definition.Command);
            AutoSuggest = definition is null || definition.Command.Occurrences.Count == 0;
            CanChangePath = definition?.Path.CanChange ?? false;
            CanChangeCommand = definition?.Command.CanChange ?? false;
            PathRestriction = definition?.Path.Restriction; CommandRestriction = definition?.Command.Restriction;
        }
        internal void SetCommand(string command, bool generated) { Command = command; AutoSuggest = generated; }
        private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;
    }

    private static string LastValue(GitToolConfigurationValue value) => value.Occurrences.LastOrDefault()?.Value ?? "";
}

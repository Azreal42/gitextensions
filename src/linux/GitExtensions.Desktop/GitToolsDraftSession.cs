namespace GitExtensions.Linux.Desktop;

/// <summary>Independent scope drafts. Read-only views always come from the published snapshot.</summary>
internal sealed class GitToolsDraftSession
{
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
            if (roles.Count > 0) { edits.Add(new(view == GitToolConfigurationView.Local ? GitConfigScope.Local : GitConfigScope.Global, roles)); }
        }
        return edits;
    }

    internal GitToolsDraftSession Rebase(GitToolConfigurationSnapshot after)
    {
        GitToolsDraftSession next = new(after);
        foreach (GitToolConfigurationView view in new[] { GitToolConfigurationView.Local, GitToolConfigurationView.Global })
        {
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

    internal bool Validate(out string? error, out GitToolConfigurationView view, out GitToolRole role)
    {
        view = GitToolConfigurationView.Local; role = GitToolRole.Diff;
        foreach (GitToolConfigurationScopeEdit edit in BuildEdits())
        {
            view = edit.Scope == GitConfigScope.Local ? GitToolConfigurationView.Local : GitToolConfigurationView.Global;
            ScopeDraft scope = Scope(view);
            foreach (GitToolRoleEdit value in edit.Roles)
            {
                role = value.Role; RoleDraft draft = scope.Role(role); Fields fields = draft.Current;
                if (!scope.CanEdit(role)) { error = scope.Snapshot.Diagnostic ?? "This configuration scope is read only."; return false; }
                if (draft.Name.IndexOfAny(['\0', '\n', '\r']) >= 0) { error = "Tool names cannot contain NUL or a line break."; return false; }
                if ((value.Path?.Value?.Contains('\0') ?? false) || (value.Command?.Value?.Contains('\0') ?? false))
                { error = "Tool paths and commands cannot contain NUL."; return false; }
                if (value.ChangeName && !draft.Selection.CanChange) { error = draft.Selection.Restriction; return false; }
                if (value.Path is not null && !fields.CanChangePath) { error = fields.PathRestriction; return false; }
                if (value.Command is not null && !fields.CanChangeCommand) { error = fields.CommandRestriction; return false; }
            }
        }
        error = null; return true;
    }

    internal sealed class ScopeDraft
    {
        private readonly Dictionary<GitToolRole, RoleDraft> _roles = [];
        private readonly bool _guiMerge;
        internal GitToolConfigurationScopeSnapshot Snapshot { get; }
        internal ScopeDraft(GitToolConfigurationScopeSnapshot snapshot, bool guiMerge)
        {
            Snapshot = snapshot; _guiMerge = guiMerge;
            foreach (GitToolRole role in Enum.GetValues<GitToolRole>())
            { _roles.Add(role, new(snapshot, role, guiMerge)); }
        }
        internal RoleDraft Role(GitToolRole role) => _roles[role];
        internal bool CanEdit(GitToolRole role) => Snapshot.IsAvailable && Snapshot.CanWrite
            && Snapshot.View is GitToolConfigurationView.Local or GitToolConfigurationView.Global
            && (role != GitToolRole.Merge || _guiMerge);
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

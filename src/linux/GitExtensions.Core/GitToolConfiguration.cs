namespace GitExtensions.Linux;

public enum GitToolRole { Diff, Merge }
public enum GitToolConfigurationView { Effective, System, Global, Local }
public enum GitConfigurationField { UserName, UserEmail, CredentialHelper, Editor, CommitTemplate, AutoCrlf, FilesEncoding,
    PullRebase, FetchPrune, MergeAutoStash, RebaseAutoStash, RebaseAutoSquash, RebaseUpdateRefs, RerereEnabled, RerereAutoUpdate }

public static class GitConfigurationFields
{
    public static string Key(GitConfigurationField field) => field switch
    {
        GitConfigurationField.UserName => "user.name",
        GitConfigurationField.UserEmail => "user.email",
        GitConfigurationField.CredentialHelper => "credential.helper",
        GitConfigurationField.Editor => "core.editor",
        GitConfigurationField.CommitTemplate => "commit.template",
        GitConfigurationField.AutoCrlf => "core.autocrlf",
        GitConfigurationField.FilesEncoding => "i18n.filesencoding",
        GitConfigurationField.PullRebase => "pull.rebase",
        GitConfigurationField.FetchPrune => "fetch.prune",
        GitConfigurationField.MergeAutoStash => "merge.autostash",
        GitConfigurationField.RebaseAutoStash => "rebase.autostash",
        GitConfigurationField.RebaseAutoSquash => "rebase.autosquash",
        GitConfigurationField.RebaseUpdateRefs => "rebase.updaterefs",
        GitConfigurationField.RerereEnabled => "rerere.enabled",
        GitConfigurationField.RerereAutoUpdate => "rerere.autoupdate",
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    public static bool IsAdvancedBoolean(GitConfigurationField field) => field is
        GitConfigurationField.PullRebase or GitConfigurationField.FetchPrune or GitConfigurationField.MergeAutoStash
        or GitConfigurationField.RebaseAutoStash or GitConfigurationField.RebaseAutoSquash or GitConfigurationField.RebaseUpdateRefs
        or GitConfigurationField.RerereEnabled or GitConfigurationField.RerereAutoUpdate;

    /// <summary>The Windows advanced page's exact, case-sensitive tri-state decoding. Unknown raw values stay untouched.</summary>
    public static bool? AdvancedBooleanState(string? value) => value switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" or "" => false,
        _ => null
    };
}

/// <summary>A real ordered Git entry. A null value denotes an existing valueless entry.</summary>
public sealed record GitToolConfigurationOccurrence(string Key, string? Value, string Scope, string Origin, int Order,
    string? ResolvedOriginPath, bool IsNativeWriteTarget);
public sealed record GitToolConfigurationValue(string Key, IReadOnlyList<GitToolConfigurationOccurrence> Occurrences,
    bool CanChange, string? Restriction);
public sealed record GitToolConfigurationDefinition(GitToolRole Role, string Name,
    GitToolConfigurationValue Path, GitToolConfigurationValue Command);

/// <summary>All lookups are immutable memory reads, including unknown tool names.</summary>
public sealed class GitToolConfigurationScopeSnapshot
{
    private readonly bool _mergeSupported, _updateRefsSupported;
    private readonly IReadOnlyList<GitToolConfigurationOccurrence> _settings;
    internal GitToolConfigurationScopeSnapshot(GitToolConfigurationView view, bool available, bool writable, string? diagnostic,
        bool mergeSupported, IEnumerable<GitToolConfigurationOccurrence> occurrences, bool updateRefsSupported = true)
    {
        View = view; IsAvailable = available; CanWrite = writable; Diagnostic = diagnostic; _mergeSupported = mergeSupported; _updateRefsSupported = updateRefsSupported;
        GitToolConfigurationOccurrence[] entries = occurrences.ToArray();
        Occurrences = Array.AsReadOnly(entries.Where(entry => GitToolConfigurationKeys.IsToolKey(entry.Key)).ToArray());
        _settings = Array.AsReadOnly(entries.Where(entry => GitToolConfigurationKeys.IsSettingKey(entry.Key)).ToArray());
        SelectedDiffGui = Value("diff.guitool", GitToolRole.Diff);
        SelectedMergeGui = Value("merge.guitool", GitToolRole.Merge);
        MergeToolFallback = Value("merge.tool", GitToolRole.Merge, fallback: true);
    }
    public GitToolConfigurationView View { get; }
    public bool IsAvailable { get; }
    public bool CanWrite { get; }
    public string? Diagnostic { get; }
    public GitToolConfigurationValue SelectedDiffGui { get; }
    public GitToolConfigurationValue SelectedMergeGui { get; }
    public GitToolConfigurationValue MergeToolFallback { get; }
    public IReadOnlyList<GitToolConfigurationOccurrence> Occurrences { get; }
    public GitToolConfigurationValue GetSetting(GitConfigurationField field)
        => Value(GitConfigurationFields.Key(field), null);
    public GitToolConfigurationDefinition GetTool(GitToolRole role, string name)
    {
        GitToolConfigurationKeys.ValidateName(name);
        if (!Enum.IsDefined(role)) { throw new ArgumentOutOfRangeException(nameof(role)); }
        string prefix = GitToolConfigurationKeys.Prefix(role) + name + ".";
        return new(role, name, Value(prefix + "path", role), Value(prefix + "cmd", role));
    }
    private GitToolConfigurationValue Value(string key, GitToolRole? role, bool fallback = false)
    {
        GitToolConfigurationOccurrence[] entries = (role is null ? _settings : Occurrences).Where(entry => entry.Key == key).ToArray();
        string? restriction = !IsAvailable ? Diagnostic : fallback ? "merge.tool is a read-only fallback."
            : !CanWrite ? "This scope is read-only."
            : role == GitToolRole.Merge && !_mergeSupported ? "GUI merge tools require Git 2.20 or newer."
            : key == "rebase.updaterefs" && !_updateRefsSupported ? "Rebase updateRefs requires Git 2.38 or newer."
            : entries.Length > 1 ? "This scope contains multiple values, including included files. Only an unchanged value can be kept." : null;
        return new(key, Array.AsReadOnly(entries), restriction is null, restriction);
    }
}

public sealed class GitToolConfigurationSnapshot
{
    internal GitToolConfigurationSnapshot(GitRepository owner, ToolConfigurationState state)
    {
        Owner = owner; State = state; GitVersion = state.Version; ConfigurationRevision = state.Revision;
        SupportsGuiMergeTool = state.Version >= new Version(2, 20);
        SupportsRebaseUpdateRefs = state.Version >= new Version(2, 38);
        Scopes = Array.AsReadOnly(Enum.GetValues<GitToolConfigurationView>().Select(view =>
        {
            bool available = state.Diagnostic is null && (view != GitToolConfigurationView.Local || state.LocalTarget is not null);
            string? diagnostic = state.Diagnostic ?? (available ? null : "Local configuration requires a Git repository.");
            bool writable = available && (view == GitToolConfigurationView.Local || view == GitToolConfigurationView.Global && state.GlobalTarget is not null);
            if (state.Diagnostic is null && view == GitToolConfigurationView.Global && state.GlobalTarget is null) { diagnostic = "The native global configuration destination could not be established. This scope is read-only."; }
            IEnumerable<GitToolConfigurationOccurrence> entries = state.Entries.Where(entry => (GitToolConfigurationKeys.IsToolKey(entry.Key) || GitToolConfigurationKeys.IsSettingKey(entry.Key))
                && (view == GitToolConfigurationView.Effective || entry.Scope == view.ToString().ToLowerInvariant()));
            return new GitToolConfigurationScopeSnapshot(view, available, writable, diagnostic, SupportsGuiMergeTool, entries, SupportsRebaseUpdateRefs);
        }).ToArray());
    }
    internal GitRepository Owner { get; }
    internal ToolConfigurationState State { get; }
    public Version GitVersion { get; }
    public bool SupportsGuiMergeTool { get; }
    public bool SupportsRebaseUpdateRefs { get; }
    public string ConfigurationRevision { get; }
    public IReadOnlyList<GitToolConfigurationScopeSnapshot> Scopes { get; }
    public GitToolConfigurationScopeSnapshot GetScope(GitToolConfigurationView view)
        => Scopes.Single(scope => scope.View == view);
}

public sealed record GitToolValueEdit(string? Value);
public sealed record GitToolRoleEdit(GitToolRole Role, string? Name, bool ChangeName,
    GitToolValueEdit? Path = null, GitToolValueEdit? Command = null);
/// <summary>A field edit preserves an empty value; null requests a native scoped unset.</summary>
public sealed record GitConfigurationFieldEdit(GitConfigurationField Field, string? Value);
public sealed record GitToolConfigurationScopeEdit(GitConfigScope Scope, IReadOnlyList<GitToolRoleEdit> Roles,
    IReadOnlyList<GitConfigurationFieldEdit>? Settings = null)
{
    public void Deconstruct(out GitConfigScope scope, out IReadOnlyList<GitToolRoleEdit> roles)
    { scope = Scope; roles = Roles; }
}
public enum GitToolConfigurationWriteOutcome { NoOp, NativeCompleted, NativeFailed, Unknown }
public sealed record GitToolConfigurationWriteReceipt(GitConfigScope Scope, string Key, string? RequestedValue,
    IReadOnlyList<string> Arguments, GitResult? NativeResult, GitToolConfigurationWriteOutcome Outcome, string? Diagnostic);
public sealed record GitToolConfigurationSaveResult(bool Success, bool Cancelled, bool? MutationStarted, bool MayHaveChanged,
    GitToolConfigurationSnapshot? After, IReadOnlyList<GitToolConfigurationWriteReceipt> Writes, string? Diagnostic);
public sealed class GitToolConfigurationSaveException : InvalidOperationException
{
    internal GitToolConfigurationSaveException(string message, GitToolConfigurationSaveResult result, Exception? inner = null) : base(message, inner) { Result = result; }
    public GitToolConfigurationSaveResult Result { get; }
}
public sealed class GitToolConfigurationSaveCanceledException : OperationCanceledException
{
    internal GitToolConfigurationSaveCanceledException(string message, GitToolConfigurationSaveResult result, Exception inner, CancellationToken token)
        : base(message, inner, token) { Result = result; }
    public GitToolConfigurationSaveResult Result { get; }
}

internal static class GitToolConfigurationKeys
{
    internal static bool IsSettingKey(string key) => key is "user.name" or "user.email" or "credential.helper" or "core.editor"
        or "commit.template" or "core.autocrlf" or "i18n.filesencoding"
        or "pull.rebase" or "fetch.prune" or "merge.autostash" or "rebase.autostash"
        or "rebase.autosquash" or "rebase.updaterefs" or "rerere.enabled" or "rerere.autoupdate";
    internal static string Prefix(GitToolRole role) => role == GitToolRole.Diff ? "difftool." : "mergetool.";
    internal static string Selector(GitToolRole role) => role == GitToolRole.Diff ? "diff.guitool" : "merge.guitool";
    internal static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Contains('\0') || name.Contains('\n')) { throw new ArgumentException("A tool subsection name must be nonempty and cannot contain NUL or LF.", nameof(name)); }
    }
    internal static bool IsToolKey(string key) => key is "diff.guitool" or "merge.guitool" or "merge.tool"
        || ((key.StartsWith("difftool.", StringComparison.Ordinal) || key.StartsWith("mergetool.", StringComparison.Ordinal))
            && (key.EndsWith(".path", StringComparison.Ordinal) || key.EndsWith(".cmd", StringComparison.Ordinal)));
}

internal sealed record ToolConfigurationFile(string LogicalPath, GitRulesLocation Location, GitRulesIdentity? Identity,
    string Hash, UnixFileMode? Mode, FileAttributes Attributes);
internal sealed record ToolConfigurationState(Version Version, string Context, string Revision, string? LocalTarget, string? GlobalTarget,
    IReadOnlyList<GitToolConfigurationOccurrence> Entries, IReadOnlyList<ToolConfigurationFile> Files, string? Diagnostic);

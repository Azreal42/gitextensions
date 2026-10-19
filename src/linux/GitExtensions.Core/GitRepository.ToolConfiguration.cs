using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IOPath = System.IO.Path;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ToolConfigurationLocks = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding ToolConfigurationUtf8 = new(false, true);

    public Task<GitToolConfigurationSnapshot> GetToolConfigurationAsync(CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            try { return new GitToolConfigurationSnapshot(this, await ReadToolConfigurationAsync(token).ConfigureAwait(false)); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // An unavailable reader must never look like an empty, editable scope.
                return new GitToolConfigurationSnapshot(this, new(new Version(0, 0), "", "", null, null, [], [], exception.Message));
            }
        }, cancellationToken);

    public async Task<GitToolConfigurationSaveResult> SaveToolConfigurationAsync(GitToolConfigurationSnapshot expected,
        IReadOnlyList<GitToolConfigurationScopeEdit> edits, CancellationToken cancellationToken = default)
    {
        using IDisposable operation = _runner.BeginOperation();
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(edits);
        List<GitToolConfigurationWriteReceipt> receipts = [];
        List<SemaphoreSlim> acquired = [];
        GitToolConfigurationSnapshot? after = null;
        bool mutationLock = false, attempted = false;
        bool? started = false;
        try
        {
            if (!ReferenceEquals(expected.Owner, this)) { throw new ArgumentException("The tool configuration snapshot belongs to another repository instance.", nameof(expected)); }
            List<ToolConfigurationEdit> changes = BuildToolConfigurationEdits(expected, edits);
            await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false); mutationLock = true;
            foreach (string path in changes.Select(change => ToolTarget(expected.State, change.Scope)!).Distinct(StringComparer.Ordinal)
                .Select(path => GitRulesFiles.Resolve(path).Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                SemaphoreSlim shared = ToolConfigurationLocks.GetOrAdd(path, _ => new(1, 1));
                await shared.WaitAsync(cancellationToken).ConfigureAwait(false); acquired.Add(shared);
            }
            ToolConfigurationState current = await ReadToolConfigurationAsync(cancellationToken).ConfigureAwait(false);
            CheckToolConfigurationState(expected.State, current);
            after = new(this, current);
            // Preflight the complete batch, including no-ops, before the first native write.
            foreach (ToolConfigurationEdit change in changes) { ValidateToolConfigurationEdit(current, change); }
            foreach (ToolConfigurationEdit change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ToolConfigurationState observed = await ReadToolConfigurationAsync(cancellationToken).ConfigureAwait(false);
                CheckToolConfigurationState(current, observed); current = observed;
                if (ToolEditIsNoOp(current, change))
                {
                    receipts.Add(new(change.Scope, change.Key, change.Value, [], null, GitToolConfigurationWriteOutcome.NoOp, null)); continue;
                }
                string[] arguments = [.. GlobalOptions, "config", "--" + change.Scope.ToString().ToLowerInvariant(),
                    .. (change.Value is null ? new[] { "--unset", "--", change.Key } : new[] { "--", change.Key, change.Value! })];
                attempted = true; started = started == true ? true : null; after = null;
                GitResult native;
                try { native = await _runner.RunAsync(Path, arguments, cancellationToken).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    receipts.Add(new(change.Scope, change.Key, change.Value, Array.AsReadOnly(arguments), null,
                        GitToolConfigurationWriteOutcome.Unknown, exception.Message)); throw;
                }
                started = true;
                receipts.Add(new(change.Scope, change.Key, change.Value, Array.AsReadOnly(arguments), native,
                    native.Success ? GitToolConfigurationWriteOutcome.NativeCompleted : GitToolConfigurationWriteOutcome.NativeFailed,
                    native.Success ? null : "The native Git command failed; no rollback was attempted."));
                // This read uses the owner token. Cancellation never starts a replacement read after drain.
                ToolConfigurationState next = await ReadToolConfigurationAsync(cancellationToken).ConfigureAwait(false);
                after = new(this, next);
                if (!native.Success) { throw new GitCommandException(arguments, native); }
                CheckToolConfigurationEffect(current, next, change);
                current = next;
                GitToolConfigurationOccurrence? winning = current.Entries.LastOrDefault(entry => entry.Key == change.Key);
                if (change.Value is not null && winning?.Value != change.Value)
                { receipts[^1] = receipts[^1] with { Diagnostic = "Git wrote the native scope file, but another effective value still wins. Review the actual scopes and origins." }; }
            }
            return Result(success: true, cancelled: false, diagnostic: null);
        }
        catch (OperationCanceledException exception)
        {
            throw new GitToolConfigurationSaveCanceledException("Saving tool configuration was cancelled after the owned Git operation drained.",
                Result(false, true, exception.Message), exception, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new GitToolConfigurationSaveException(exception.Message, Result(false, false, exception.Message), exception);
        }
        finally
        {
            for (int index = acquired.Count - 1; index >= 0; index--) { acquired[index].Release(); }
            if (mutationLock) { _mutationLock.Release(); }
        }
        GitToolConfigurationSaveResult Result(bool success, bool cancelled, string? diagnostic)
            => new(success, cancelled, started, attempted, after, Array.AsReadOnly(receipts.ToArray()), diagnostic);
    }

    private static List<ToolConfigurationEdit> BuildToolConfigurationEdits(GitToolConfigurationSnapshot expected,
        IReadOnlyList<GitToolConfigurationScopeEdit> edits)
    {
        List<ToolConfigurationEdit> result = [];
        HashSet<GitConfigScope> scopes = [];
        foreach (GitToolConfigurationScopeEdit scopeEdit in edits)
        {
            ArgumentNullException.ThrowIfNull(scopeEdit); ArgumentNullException.ThrowIfNull(scopeEdit.Roles);
            if (!Enum.IsDefined(scopeEdit.Scope) || !scopes.Add(scopeEdit.Scope)) { throw new ArgumentException("Each writable Local/Global scope may occur only once.", nameof(edits)); }
            GitToolConfigurationScopeSnapshot scope = expected.GetScope(scopeEdit.Scope == GitConfigScope.Local ? GitToolConfigurationView.Local : GitToolConfigurationView.Global);
            if (!scope.CanWrite) { throw new InvalidOperationException(scope.Diagnostic ?? "This configuration scope is read-only."); }
            HashSet<GitToolRole> roles = [];
            foreach (GitToolRoleEdit role in scopeEdit.Roles)
            {
                ArgumentNullException.ThrowIfNull(role);
                if (!Enum.IsDefined(role.Role) || !roles.Add(role.Role)) { throw new ArgumentException("Each Diff/Merge role may occur only once per scope.", nameof(edits)); }
                if (role.Role == GitToolRole.Merge && !expected.SupportsGuiMergeTool) { throw new NotSupportedException("GUI merge tools require Git 2.20 or newer."); }
                string? selected = LastValue(role.Role == GitToolRole.Diff ? scope.SelectedDiffGui : scope.SelectedMergeGui);
                if (role.Role == GitToolRole.Merge && string.IsNullOrEmpty(selected)) { selected = LastValue(scope.MergeToolFallback); }
                string? name = string.IsNullOrWhiteSpace(role.Name) ? null : role.Name;
                if (name is not null) { GitToolConfigurationKeys.ValidateName(name); }
                if (!role.ChangeName && name != (string.IsNullOrWhiteSpace(selected) ? null : selected))
                { throw new ArgumentException("An unchanged selector must retain the snapshot's displayed tool name.", nameof(edits)); }
                if (role.Path is not null || role.Command is not null)
                {
                    if (name is null) { throw new ArgumentException("Path/command changes require the final nonempty tool name.", nameof(edits)); }
                    string prefix = GitToolConfigurationKeys.Prefix(role.Role) + name + ".";
                    if (role.Path is not null) { Add(prefix + "path", role.Path.Value, 0); }
                    if (role.Command is not null) { Add(prefix + "cmd", role.Command.Value, 1); }
                }
                if (role.ChangeName) { Add(GitToolConfigurationKeys.Selector(role.Role), name, 2); }
                void Add(string key, string? value, int order)
                {
                    if (value?.Contains('\0') == true) { throw new ArgumentException("Git configuration values cannot contain NUL.", nameof(edits)); }
                    result.Add(new(scopeEdit.Scope, role.Role, key, value is "" ? null : value, order));
                }
            }
            HashSet<GitConfigurationField> fields = [];
            foreach (GitConfigurationFieldEdit setting in scopeEdit.Settings ?? [])
            {
                ArgumentNullException.ThrowIfNull(setting);
                if (!Enum.IsDefined(setting.Field) || !fields.Add(setting.Field))
                { throw new ArgumentException("Each supported Git configuration field may occur only once per scope.", nameof(edits)); }
                if (setting.Value?.Contains('\0') == true) { throw new ArgumentException("Git configuration values cannot contain NUL.", nameof(edits)); }
                result.Add(new(scopeEdit.Scope, null, GitConfigurationFields.Key(setting.Field), setting.Value, (int)setting.Field, setting.Field));
            }
        }
        return result.OrderBy(edit => edit.Scope == GitConfigScope.Local ? 0 : 1)
            .ThenBy(edit => edit.Role is GitToolRole role ? (int)role : 2).ThenBy(edit => edit.Order).ToList();
        static string? LastValue(GitToolConfigurationValue value) => value.Occurrences.LastOrDefault()?.Value;
    }

    private static IEnumerable<GitToolConfigurationOccurrence> EntriesFor(ToolConfigurationState state, ToolConfigurationEdit edit)
        => state.Entries.Where(entry => entry.Scope == edit.Scope.ToString().ToLowerInvariant() && entry.Key == edit.Key);
    private static bool ToolEditIsNoOp(ToolConfigurationState state, ToolConfigurationEdit edit)
    {
        GitToolConfigurationOccurrence[] entries = EntriesFor(state, edit).ToArray();
        return edit.Value is null ? entries.Length == 0 : entries.Length != 0 && entries[^1].Value == edit.Value;
    }
    private static void ValidateToolConfigurationEdit(ToolConfigurationState state, ToolConfigurationEdit edit)
    {
        if (ToolEditIsNoOp(state, edit)) { return; }
        if (edit.Field == GitConfigurationField.AutoCrlf && edit.Value is not (null or "false" or "input" or "true"))
        { throw new ArgumentException("core.autocrlf must be false, input, true, or unset."); }
        if (edit.Field is { } field && GitConfigurationFields.IsAdvancedBoolean(field) && edit.Value is not (null or "false" or "true"))
        { throw new ArgumentException("Advanced Git boolean settings must be false, true, or unset."); }
        if (edit.Field == GitConfigurationField.RebaseUpdateRefs && state.Version < new Version(2, 38))
        { throw new InvalidOperationException("Rebase updateRefs requires Git 2.38 or newer."); }
        string target = ToolTarget(state, edit.Scope) ?? throw new InvalidOperationException("The native configuration destination is unavailable.");
        ToolConfigurationFile file = state.Files.Single(source => source.LogicalPath == target);
        if (file.Identity is not null && !file.Identity.Value.IsRegular) { throw new IOException("The native configuration destination is not a regular file."); }
        GitToolConfigurationOccurrence[] entries = EntriesFor(state, edit).ToArray();
        if (entries.Length > 1) { throw new InvalidOperationException("Cannot change a multi-valued Git setting, including duplicate/include values: " + edit.Key); }
        if (edit.Value is null && entries.Length == 1 && !entries[0].IsNativeWriteTarget)
        { throw new InvalidOperationException("This value comes only from another file/include. Remove it in its source; a scoped unset cannot remove it: " + edit.Key); }
    }
    private static string? ToolTarget(ToolConfigurationState state, GitConfigScope scope)
        => scope == GitConfigScope.Local ? state.LocalTarget : state.GlobalTarget;

    private async Task<ToolConfigurationState> ReadToolConfigurationAsync(CancellationToken token)
    {
        string environment = ToolConfigurationEnvironment();
        if (_runner.ExecutionEnvironment.GetVariable("GIT_CONFIG") is not null)
        { throw new NotSupportedException("An explicit GIT_CONFIG file override is outside this scoped editor. No write destination was inferred."); }
        Version version = await GetToolConfigurationGitVersionAsync(token).ConfigureAwait(false);
        if (version < new Version(2, 26))
        { return new(version, environment, "", null, null, [], [], "Tool configuration needs Git 2.26 or newer for actual scope and origin metadata. No editable values were inferred."); }
        GitResult local = await _runner.RunAsync(Path, ["rev-parse", "--git-path", "config"], token).ConfigureAwait(false);
        string? localTarget = local.Success ? IOPath.GetFullPath(ReadOutputLine(local.StandardOutput), Path) : null;
        if (!local.Success && local.ExitCode != 128) { throw new GitCommandException(["rev-parse", "--git-path", "config"], local); }
        (string? globalTarget, string[] globalCandidates) = ToolGlobalTargets();
        if (globalTarget is not null && GitRulesIdentity.ReadPath(GitRulesFiles.Resolve(globalTarget).Path) is GitRulesIdentity globalIdentity && !globalIdentity.IsRegular)
        { globalTarget = null; }
        string[] arguments = [.. GlobalOptions, "config", "--includes", "--null", "--list", "--show-origin", "--show-scope"];
        GitBinaryResult raw = await _runner.RunBinaryAsync(Path, arguments, token).ConfigureAwait(false);
        GitToolConfigurationOccurrence[] entries = ParseToolConfiguration(raw, arguments, localTarget, globalTarget);
        string? diagnostic = localTarget is null && !local.Success && entries.Any(entry => entry.Scope == "local")
            ? new GitCommandException(["rev-parse", "--git-path", "config"], local).Message : null;
        HashSet<string> paths = new(globalCandidates, StringComparer.Ordinal);
        if (localTarget is not null) { paths.Add(localTarget); }
        foreach (GitToolConfigurationOccurrence entry in entries)
        {
            if (entry.ResolvedOriginPath is not null) { paths.Add(IOPath.GetFullPath(entry.Origin[5..], Path)); }
            if (entry.ResolvedOriginPath is not null && (entry.Key == "include.path" || entry.Key.StartsWith("includeif.", StringComparison.Ordinal) && entry.Key.EndsWith(".path", StringComparison.Ordinal))
                && entry.Value is { Length: > 0 } include)
            {
                string basePath = IOPath.GetDirectoryName(IOPath.GetFullPath(entry.Origin[5..], Path))!;
                if (include.StartsWith("~/", StringComparison.Ordinal))
                {
                    string home = _runner.ExecutionEnvironment.GetVariable("HOME") ?? throw new IOException("Cannot guard a home-relative Git include without HOME.");
                    include = IOPath.Combine(home, include[2..]);
                }
                else if (include.StartsWith('~')) { throw new NotSupportedException("This configuration includes a user-home path whose physical origin cannot be guarded by this editor."); }
                paths.Add(IOPath.GetFullPath(include, basePath));
            }
        }
        List<ToolConfigurationFile> files = [];
        foreach (string path in paths.Order(StringComparer.Ordinal)) { files.Add(await ReadToolConfigurationFileAsync(path, token).ConfigureAwait(false)); }
        GitBinaryResult check = await _runner.RunBinaryAsync(Path, arguments, token).ConfigureAwait(false);
        _ = ParseToolConfiguration(check, arguments, localTarget, globalTarget);
        if (!raw.StandardOutput.AsSpan().SequenceEqual(check.StandardOutput)) { throw ToolConfigurationChanged(); }
        foreach (ToolConfigurationFile file in files)
        { CheckToolConfigurationFile(file, await ReadToolConfigurationFileAsync(file.LogicalPath, token).ConfigureAwait(false)); }
        if (environment != ToolConfigurationEnvironment()) { throw ToolConfigurationChanged(); }
        string context = GitRulesFiles.Hash(ToolConfigurationUtf8.GetBytes(environment + "\0" + Path + "\0" + _runner.Executable + "\0" + version));
        string revision = GitRulesFiles.Hash(ToolConfigurationUtf8.GetBytes(GitRulesFiles.Hash(raw.StandardOutput)
            + string.Concat(files.Select(file => file.LogicalPath + "\0" + file.Location + "\0" + file.Identity + "\0" + file.Hash + "\0" + file.Mode + "\0" + file.Attributes))));
        return new(version, context, revision, localTarget, globalTarget, Array.AsReadOnly(entries), files.AsReadOnly(), diagnostic);
    }

    private async Task<Version> GetToolConfigurationGitVersionAsync(CancellationToken token)
    {
        GitResult native = await _runner.RunAsync(Path, ["--version"], token).ConfigureAwait(false);
        if (!native.Success) { throw new GitCommandException(["--version"], native); }
        Match match = Regex.Match(native.StandardOutput, @"\Agit version (\d+)\.(\d+)(?:\.(\d+))?");
        if (!match.Success) { throw new FormatException("Cannot determine Git version for GUI tool configuration."); }
        return new(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0);
    }

    private (string? Target, string[] Candidates) ToolGlobalTargets()
    {
        string? configured = _runner.ExecutionEnvironment.GetVariable("GIT_CONFIG_GLOBAL");
        if (configured is not null)
        { return configured.Length == 0 ? (null, []) : (IOPath.GetFullPath(configured, Path), [IOPath.GetFullPath(configured, Path)]); }
        string? home = _runner.ExecutionEnvironment.GetVariable("HOME");
        if (string.IsNullOrEmpty(home) || !IOPath.IsPathRooted(home)) { return (null, []); }
        string normal = IOPath.Combine(home, ".gitconfig");
        string xdg = IOPath.Combine(_runner.ExecutionEnvironment.GetVariable("XDG_CONFIG_HOME") is { Length: > 0 } directory ? IOPath.GetFullPath(directory, Path) : IOPath.Combine(home, ".config"), "git", "config");
        return (File.Exists(normal) || !File.Exists(xdg) ? normal : xdg, [normal, xdg]);
    }
    private string ToolConfigurationEnvironment()
    {
        GitExecutionEnvironment environment = _runner.ExecutionEnvironment;
        IEnumerable<string> names = environment.EnvironmentVariables.Keys.Where(name => name.StartsWith("GIT_CONFIG", StringComparison.Ordinal)
            || name is "HOME" or "XDG_CONFIG_HOME" or "GIT_DIR" or "GIT_WORK_TREE" or "GIT_COMMON_DIR" or "GIT_CEILING_DIRECTORIES" or "PATH" or "GIT_EXEC_PATH");
        // Context values can contain secrets; retain only their digest, never expose them in diagnostics or public DTOs.
        return GitRulesFiles.Hash(ToolConfigurationUtf8.GetBytes(string.Concat(names.Order(StringComparer.Ordinal).Select(name => name + "\0" + environment.GetVariable(name) + "\0"))));
    }
    private GitToolConfigurationOccurrence[] ParseToolConfiguration(GitBinaryResult raw, IReadOnlyList<string> arguments, string? localTarget, string? globalTarget)
    {
        if (!raw.Success) { throw new GitCommandException(arguments, new(raw.ExitCode, "", raw.StandardError)); }
        string decoded = ToolConfigurationUtf8.GetString(raw.StandardOutput);
        string[] fields = decoded.Split('\0');
        if (fields[^1].Length != 0 || (fields.Length - 1) % 3 != 0) { throw new FormatException("Git returned incomplete scope/origin/configuration framing."); }
        List<GitToolConfigurationOccurrence> entries = [];
        for (int index = 0; index < fields.Length - 1; index += 3)
        {
            string scope = fields[index], origin = fields[index + 1], pair = fields[index + 2];
            if (scope is not ("system" or "global" or "local" or "worktree" or "command")) { throw new FormatException("Git returned an unknown configuration scope."); }
            int newline = pair.IndexOf('\n');
            string key = newline < 0 ? pair : pair[..newline];
            if (key.Length == 0) { throw new FormatException("Git returned an empty configuration key."); }
            string? resolved = origin.StartsWith("file:", StringComparison.Ordinal) ? GitRulesFiles.Resolve(IOPath.GetFullPath(origin[5..], Path)).Path : null;
            string? target = scope == "local" ? localTarget : scope == "global" ? globalTarget : null;
            bool direct = resolved is not null && target is not null && resolved == GitRulesFiles.Resolve(target).Path;
            entries.Add(new(key, newline < 0 ? null : pair[(newline + 1)..], scope, origin, index / 3, resolved, direct));
        }
        return entries.ToArray();
    }
    private static async Task<ToolConfigurationFile> ReadToolConfigurationFileAsync(string path, CancellationToken token)
    {
        GitRulesLocation location = GitRulesFiles.Resolve(path);
        GitRulesIdentity? identity = GitRulesIdentity.ReadPath(location.Path);
        byte[] bytes = [];
        UnixFileMode? mode = null;
        FileAttributes attributes = FileAttributes.Normal;
        if (identity is not null)
        {
            // /dev/null is a common explicit read-only config override; never offer it as a write target.
            if (!identity.Value.IsRegular && location.Path != "/dev/null") { throw new IOException("A Git configuration source is not a regular file: " + path); }
            await using FileStream file = new(location.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (GitRulesIdentity.ReadHandle(file.SafeFileHandle) != identity) { throw ToolConfigurationChanged(); }
            using MemoryStream buffer = new(); await file.CopyToAsync(buffer, token).ConfigureAwait(false); bytes = buffer.ToArray();
            mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(file.SafeFileHandle);
            attributes = File.GetAttributes(file.SafeFileHandle);
            if (GitRulesIdentity.ReadHandle(file.SafeFileHandle) != identity) { throw ToolConfigurationChanged(); }
        }
        GitRulesFiles.CheckLocation(location, GitRulesFiles.Resolve(path));
        return new(path, location, identity, GitRulesFiles.Hash(bytes), mode, attributes);
    }
    private static void CheckToolConfigurationFile(ToolConfigurationFile expected, ToolConfigurationFile actual)
    {
        GitRulesFiles.CheckLocation(expected.Location, actual.Location);
        if (expected.Identity != actual.Identity || expected.Hash != actual.Hash || expected.Mode != actual.Mode || expected.Attributes != actual.Attributes)
        { throw ToolConfigurationChanged(); }
    }
    private static void CheckToolConfigurationState(ToolConfigurationState expected, ToolConfigurationState actual)
    {
        if (expected.Diagnostic is not null) { throw new InvalidOperationException(expected.Diagnostic); }
        if (actual.Diagnostic is not null) { throw new InvalidOperationException(actual.Diagnostic); }
        if (expected.Context != actual.Context || expected.Revision != actual.Revision || expected.LocalTarget != actual.LocalTarget || expected.GlobalTarget != actual.GlobalTarget)
        { throw ToolConfigurationChanged(); }
        foreach (ToolConfigurationFile source in expected.Files)
        { CheckToolConfigurationFile(source, actual.Files.Single(file => file.LogicalPath == source.LogicalPath)); }
    }
    private static void CheckToolConfigurationEffect(ToolConfigurationState before, ToolConfigurationState after, ToolConfigurationEdit edit)
    {
        if (before.Context != after.Context || before.LocalTarget != after.LocalTarget || before.GlobalTarget != after.GlobalTarget || after.Diagnostic is not null)
        { throw ToolConfigurationChanged(); }
        string scope = edit.Scope.ToString().ToLowerInvariant();
        static string Material(GitToolConfigurationOccurrence entry) => entry.Scope + "\0" + entry.Origin + "\0" + entry.Key + "\0" + (entry.Value is null ? "N" : "V" + entry.Value);
        bool Other(GitToolConfigurationOccurrence entry) => entry.Scope != scope || entry.Key != edit.Key;
        if (!before.Entries.Where(Other).Select(Material).SequenceEqual(after.Entries.Where(Other).Select(Material))) { throw ToolConfigurationChanged(); }
        string target = ToolTarget(before, edit.Scope)!;
        string resolved = before.Files.Single(file => file.LogicalPath == target).Location.Path;
        foreach (ToolConfigurationFile source in before.Files)
        {
            ToolConfigurationFile current = after.Files.Single(file => file.LogicalPath == source.LogicalPath);
            if (source.Location.Path != resolved) { CheckToolConfigurationFile(source, current); continue; }
            // Native Git replaces its target atomically. Only that final regular-file identity may change.
            if (source.Location.Path != current.Location.Path || source.Location.Parts.Count != current.Location.Parts.Count
                || !source.Location.Parts.Where(part => part.Path != resolved).SequenceEqual(current.Location.Parts.Where(part => part.Path != resolved))
                || current.Identity?.IsRegular != true || source.Identity is not null && source.Mode != current.Mode)
            { throw ToolConfigurationChanged(); }
        }
        GitToolConfigurationOccurrence[] direct = after.Entries.Where(entry => entry.Scope == scope && entry.Key == edit.Key && entry.IsNativeWriteTarget).ToArray();
        if (edit.Value is null ? direct.Length != 0 : direct.Length != 1 || direct[0].Value != edit.Value) { throw ToolConfigurationChanged(); }
        if (!before.Entries.Where(entry => entry.Scope == scope && entry.Key == edit.Key && !entry.IsNativeWriteTarget).Select(Material)
            .SequenceEqual(after.Entries.Where(entry => entry.Scope == scope && entry.Key == edit.Key && !entry.IsNativeWriteTarget).Select(Material)))
        { throw ToolConfigurationChanged(); }
    }
    private static IOException ToolConfigurationChanged() => new("Git configuration or its physical sources changed outside this editor. Reload before saving. No rollback was attempted.");
    private sealed record ToolConfigurationEdit(GitConfigScope Scope, GitToolRole? Role, string Key, string? Value, int Order,
        GitConfigurationField? Field = null);
}

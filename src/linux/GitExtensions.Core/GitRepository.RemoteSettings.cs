namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public Task<IReadOnlyList<GitRemoteSettingsSnapshot>> GetRemoteSettingsAsync(CancellationToken cancellationToken = default)
        => WithRemoteSettingsAsync<IReadOnlyList<GitRemoteSettingsSnapshot>>(ReadRemoteSettingsAsync, cancellationToken);

    private async Task<IReadOnlyList<GitRemoteSettingsSnapshot>> ReadRemoteSettingsAsync(RemoteSettingsState state, CancellationToken token)
    {
        List<GitRemoteSettingsSnapshot> snapshots = [];
        IEnumerable<(string Name, bool Disabled)> names = state.Effective
            .Select(s => TryRemoteSetting(s.Key, out string name, out bool disabled) ? (name, disabled) : default)
            .Where(pair => pair.name is not null).Distinct().OrderBy(pair => pair.name, StringComparer.Ordinal).ThenBy(pair => pair.disabled);
        foreach ((string name, bool disabled) in names)
        {
            string prefix = (disabled ? "-remote." : "remote.") + name + ".";
            GitRemoteSetting[] settings = state.Effective.Where(s => IsRemoteSettingsSection(s.Key, name, disabled)).ToArray();
            string[] fetch = RemoteSettingsList(settings.Where(s => s.Key == prefix + "url").Select(s => s.Value)).ToArray();
            string[] push = RemoteSettingsList(settings.Where(s => s.Key == prefix + "pushurl").Select(s => s.Value)).ToArray();
            string[] effectiveFetch = [], effectivePush = [];
            string? error = null;
            if (!disabled)
            {
                try
                {
                    effectiveFetch = NativeUrlLines((await ExecuteAsync(["remote", "get-url", "--all", "--", name], token).ConfigureAwait(false)).StandardOutput);
                    effectivePush = NativeUrlLines((await ExecuteAsync(["remote", "get-url", "--push", "--all", "--", name], token).ConfigureAwait(false)).StandardOutput);
                }
                catch (GitCommandException exception) { error = exception.Message; }
            }
            snapshots.Add(new(state, name, disabled, settings, fetch, push, effectiveFetch, effectivePush, error));
        }
        await CheckRemoteSettingsUnchangedAsync(state, token).ConfigureAwait(false);
        return snapshots.AsReadOnly();
    }

    public Task<GitRemoteSettingsSnapshot> SaveRemoteSettingsAsync(GitRemoteSettingsSnapshot? expected, GitRemoteSettingsEdit edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        string name = edit.Name?.Trim() ?? "";
        string url = edit.Url?.Trim() ?? "";
        string? pushUrl = edit.PushUrl?.Trim();
        if (string.IsNullOrEmpty(pushUrl) || string.Equals(pushUrl, url, StringComparison.Ordinal)) { pushUrl = null; }
        edit = edit with { Name = name, Url = url, PushUrl = pushUrl };
        ValidateRemoteSettingsText(name, "remote name", required: true);
        ValidateRemoteSettingsText(edit.Url, "remote URL", required: true);
        ValidateRemoteSettingsText(edit.PushUrl, "push URL", required: edit.PushUrl is not null);
        ValidateRemoteSettingsText(edit.Prefix, "branch prefix"); ValidateRemoteSettingsText(edit.Color, "remote color");
        return WithRemoteSettingsAsync(async (state, token) =>
        {
            if (expected is not null) { CheckRemoteSettingsState(expected.State, state); }
            if (expected?.Disabled == true) { throw new GitRemoteSettingsException("Activate this remote before editing it.", false, expected.Name); }
            await ValidateRemoteSettingsNameAsync(name, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(edit.Prefix))
            { await ExecuteAsync(["check-ref-format", "refs/heads/" + edit.Prefix.TrimEnd('/') + "/branch"], token).ConfigureAwait(false); }
            IReadOnlyList<GitRemoteSettingsSnapshot> all = await ReadRemoteSettingsAsync(state, token).ConfigureAwait(false);
            bool rename = expected is not null && expected.Name != name;
            if (all.Any(s => s.Name == name && (expected is null || s.Name != expected.Name)))
            { throw new GitRemoteSettingsException("A remote with this name already exists, including inactive remotes: " + name, false, expected?.Name); }
            if (rename) { RequireRemoteStructure(expected!); }
            IReadOnlyList<RemoteConfigEdit> edits = RemoteSettingsEdits(state, expected, edit with { Name = name });
            if (edits.Any(e => (e.Key.EndsWith(".url", StringComparison.Ordinal) || e.Key.EndsWith(".pushurl", StringComparison.Ordinal)) && e.Values.FirstOrDefault() == ""))
            { await EnsureRemoteSettingsUrlResetAsync(token).ConfigureAwait(false); }
            // Exercise every requested config edit on copies before native rename/add can affect refs or configuration.
            await PrepareRemoteSettingsConfigAsync(state, edits, token).ConfigureAwait(false);
            bool attempted = false, changed = false;
            string effectiveName = expected?.Name ?? name;
            string refs = await RemoteSettingsReferencesAsync(token).ConfigureAwait(false);
            try
            {
                if (expected is null || rename)
                {
                    await CheckRemoteSettingsUnchangedAsync(state, token).ConfigureAwait(false);
                    attempted = true;
                    await ExecuteAsync(expected is null ? ["remote", "add", "--", name, edit.Url]
                        : ["remote", "rename", "--", expected.Name, name], token).ConfigureAwait(false);
                    changed = true; effectiveName = name;
                    RemoteSettingsState native = await ReadRemoteSettingsStateAsync(state.CommonDirectory, token).ConfigureAwait(false);
                    await CheckRemoteNativeTransitionAsync(state, native, expected?.Name, name, edit.Url, token).ConfigureAwait(false);
                    state = native;
                    // The native operation retained its own config changes, including tracking/refspec rewrites.
                    GitRemoteSettingsSnapshot current = (await ReadRemoteSettingsAsync(state, token).ConfigureAwait(false)).Single(s => !s.Disabled && s.Name == name);
                    edits = RemoteSettingsEdits(state, current, edit with { Name = name });
                }
                byte[] bytes = await PrepareRemoteSettingsConfigAsync(state, edits, token).ConfigureAwait(false);
                await PublishRemoteSettingsConfigAsync(state, bytes, token).ConfigureAwait(false);
                changed |= !bytes.AsSpan().SequenceEqual(state.Config.Bytes);
                RemoteSettingsState saved = await ReadRemoteSettingsStateAsync(state.CommonDirectory, CancellationToken.None).ConfigureAwait(false);
                GitRemoteSettingsSnapshot result = (await ReadRemoteSettingsAsync(saved, CancellationToken.None).ConfigureAwait(false)).Single(s => !s.Disabled && s.Name == name);
                if (!result.FetchUrls.SequenceEqual(DesiredRemoteSettingsUrls(expected, edit, push: false))
                    || !result.PushUrls.SequenceEqual(DesiredRemoteSettingsUrls(expected, edit, push: true)))
                { throw new GitRemoteSettingsException("Saving this remote changed conditional configuration and its inherited URL destinations. Reload the remote before editing those lists again.", true, name); }
                return result;
            }
            catch (Exception exception)
            {
                if (attempted) { changed |= await RemoteSettingsHasChangesAsync(state, refs).ConfigureAwait(false); }
                if (changed && (expected is null || rename))
                {
                    try
                    {
                        RemoteSettingsState current = await ReadRemoteSettingsStateAsync(state.CommonDirectory, CancellationToken.None).ConfigureAwait(false);
                        if (current.Effective.Any(s => IsRemoteSettingsSection(s.Key, name, false))) { effectiveName = name; }
                    }
                    catch { /* Retain the last proven native name when the configuration itself cannot be read. */ }
                }
                if (!changed && exception is OperationCanceledException) { throw; }
                if (!changed && exception is GitRemoteSettingsException) { throw; }
                throw new GitRemoteSettingsException("Remote settings could not be saved" + (changed ? "; some native changes already took effect. Reload the repository. " : ": ") + exception.Message,
                    changed, effectiveName, exception);
            }
        }, cancellationToken);
    }

    public async Task RemoveRemoteSettingsAsync(GitRemoteSettingsSnapshot expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        await WithRemoteSettingsAsync(async (state, token) =>
        {
            CheckRemoteSettingsState(expected.State, state); RequireRemoteStructure(expected);
            IReadOnlyList<RemoteConfigEdit> edits = state.Local.Where(s => IsRemoteSettingsSection(s.Key, expected.Name, expected.Disabled))
                .Select(s => new RemoteConfigEdit(s.Key, [])).DistinctBy(e => e.Key).ToArray();
            if (expected.Disabled)
            {
                byte[] bytes = await PrepareRemoteSettingsConfigAsync(state, edits, token).ConfigureAwait(false);
                await PublishRemoteSettingsConfigAsync(state, bytes, token).ConfigureAwait(false);
            }
            else
            {
                string refs = await RemoteSettingsReferencesAsync(token).ConfigureAwait(false);
                await CheckRemoteSettingsUnchangedAsync(state, token).ConfigureAwait(false);
                try { await ExecuteAsync(["remote", "remove", "--", expected.Name], token).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    bool changed = await RemoteSettingsHasChangesAsync(state, refs).ConfigureAwait(false);
                    if (!changed && exception is OperationCanceledException) { throw; }
                    throw new GitRemoteSettingsException("Remote removal failed; reload its current state. " + exception.Message, changed, expected.Name, exception);
                }
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<GitRemoteSettingsSnapshot> SetRemoteEnabledAsync(GitRemoteSettingsSnapshot expected, bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return WithRemoteSettingsAsync(async (state, token) =>
        {
            CheckRemoteSettingsState(expected.State, state); RequireRemoteStructure(expected);
            if (enabled == !expected.Disabled) { return expected; }
            IReadOnlyList<GitRemoteSettingsSnapshot> all = await ReadRemoteSettingsAsync(state, token).ConfigureAwait(false);
            if (all.Any(s => s.Name == expected.Name && s.Disabled != expected.Disabled))
            { throw new GitRemoteSettingsException("An active/inactive remote with this name already exists. Resolve that collision before changing its state.", false, expected.Name); }
            string source = (expected.Disabled ? "-remote." : "remote.") + expected.Name + ".";
            string destination = (enabled ? "remote." : "-remote.") + expected.Name + ".";
            KeyValuePair<string, string>[] entries = state.Local.Where(s => IsRemoteSettingsSection(s.Key, expected.Name, expected.Disabled)).ToArray();
            List<RemoteConfigEdit> edits = entries.Select(s => new RemoteConfigEdit(s.Key, [])).DistinctBy(s => s.Key).ToList();
            edits.AddRange(entries.GroupBy(s => s.Key, StringComparer.Ordinal).Select(group => new RemoteConfigEdit(destination + group.Key[source.Length..], group.Select(s => s.Value).ToArray())));
            await PrepareRemoteSettingsConfigAsync(state, edits, token).ConfigureAwait(false);
            bool attempted = false, changed = false;
            string refs = await RemoteSettingsReferencesAsync(token).ConfigureAwait(false);
            try
            {
                if (!enabled)
                {
                    await CheckRemoteSettingsUnchangedAsync(state, token).ConfigureAwait(false);
                    attempted = true;
                    await ExecuteAsync(["remote", "remove", "--", expected.Name], token).ConfigureAwait(false); changed = true;
                    RemoteSettingsState after = await ReadRemoteSettingsStateAsync(state.CommonDirectory, token).ConfigureAwait(false);
                    await CheckRemoteNativeRemovalTransitionAsync(state, after, expected.Name, token).ConfigureAwait(false);
                    state = after;
                }
                byte[] bytes = await PrepareRemoteSettingsConfigAsync(state, edits, token).ConfigureAwait(false);
                await PublishRemoteSettingsConfigAsync(state, bytes, token).ConfigureAwait(false);
                changed = true;
                RemoteSettingsState saved = await ReadRemoteSettingsStateAsync(state.CommonDirectory, CancellationToken.None).ConfigureAwait(false);
                return (await ReadRemoteSettingsAsync(saved, CancellationToken.None).ConfigureAwait(false)).Single(s => s.Name == expected.Name && s.Disabled == !enabled);
            }
            catch (Exception exception)
            {
                if (attempted) { changed |= await RemoteSettingsHasChangesAsync(state, refs).ConfigureAwait(false); }
                if (!changed && exception is OperationCanceledException) { throw; }
                throw new GitRemoteSettingsException("Changing remote activation failed" + (changed ? "; native changes already took effect. " : ": ") + exception.Message,
                    changed, expected.Name, exception);
            }
        }, cancellationToken);
    }

    private static IReadOnlyList<RemoteConfigEdit> RemoteSettingsEdits(RemoteSettingsState state, GitRemoteSettingsSnapshot? original, GitRemoteSettingsEdit edit)
    {
        List<RemoteConfigEdit> edits = [];
        string prefix = "remote." + edit.Name + ".";
        if (original is null || original.Url != edit.Url)
        {
            List<string> urls = original?.FetchUrls.ToList() ?? [];
            if (urls.Count == 0) { urls.Add(edit.Url); } else { urls[0] = edit.Url; }
            AddList("url", urls);
        }
        if (original?.PushUrl != edit.PushUrl)
        {
            List<string> urls = original?.PushUrls.ToList() ?? [];
            if (edit.PushUrl is null) { urls.Clear(); }
            else if (urls.Count == 0) { urls.Add(edit.PushUrl); } else { urls[^1] = edit.PushUrl; }
            AddList("pushurl", urls);
        }
        AddScalar("prefix", original?.Prefix, edit.Prefix); AddScalar("color", original?.Color, edit.Color);
        return edits;
        bool HasInherited(string variable) => state.Effective.Any(s => s.Key == prefix + variable && (s.Scope != "local" || s.Origin != state.Config.Path));
        void AddList(string variable, IReadOnlyList<string> values)
        { edits.Add(new(prefix + variable, HasInherited(variable) ? ["", .. values] : values)); }
        void AddScalar(string variable, string? before, string? after)
        {
            if ((before ?? "") == (after ?? "")) { return; }
            edits.Add(new(prefix + variable, string.IsNullOrEmpty(after) ? HasInherited(variable) ? [""] : [] : [after]));
        }
    }
    private static IReadOnlyList<string> DesiredRemoteSettingsUrls(GitRemoteSettingsSnapshot? original, GitRemoteSettingsEdit edit, bool push)
    {
        List<string> values = (push ? original?.PushUrls : original?.FetchUrls)?.ToList() ?? [];
        if (push && edit.PushUrl is null) { return []; }
        string value = push ? edit.PushUrl! : edit.Url;
        if (values.Count == 0) { values.Add(value); } else { values[push ? values.Count - 1 : 0] = value; }
        return values;
    }

    private static void RequireRemoteStructure(GitRemoteSettingsSnapshot snapshot)
    { if (!snapshot.CanModifyStructure) { throw new GitRemoteSettingsException(snapshot.ModificationRestriction!, false, snapshot.Name); } }
    private static void ValidateRemoteSettingsText(string? text, string field, bool required = false)
    {
        if (required && string.IsNullOrWhiteSpace(text)) { throw new ArgumentException("Enter a " + field + "."); }
        if (text?.Contains('\0') == true) { throw new ArgumentException("The " + field + " cannot contain NUL."); }
    }
    private async Task ValidateRemoteSettingsNameAsync(string name, CancellationToken token)
        => await ExecuteAsync(["check-ref-format", "refs/remotes/" + name + "/branch"], token).ConfigureAwait(false);
    private static string[] NativeUrlLines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    private async Task<string> RemoteSettingsReferencesAsync(CancellationToken token)
        => (await ExecuteAsync(["for-each-ref", "--format=%(refname)%00%(objectname)%00%(symref)"], token).ConfigureAwait(false)).StandardOutput;
    private async Task<bool> RemoteSettingsHasChangesAsync(RemoteSettingsState before, string references)
    {
        try
        {
            RemoteSettingsFile current = await ReadRemoteSettingsFileAsync(before.Config.LogicalPath, CancellationToken.None).ConfigureAwait(false);
            return current.Hash != before.Config.Hash || current.Identity != before.Config.Identity || references != await RemoteSettingsReferencesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch { return true; }
    }

    private static async Task CheckRemoteNativeTransitionAsync(RemoteSettingsState before, RemoteSettingsState after, string? oldName, string newName, string newUrl, CancellationToken token)
    {
        if (oldName is null)
        {
            // Native add creates URL/fetch; every pre-existing setting must remain intact.
            IEnumerable<KeyValuePair<string, string>> expected = after.Local.Where(s => !IsRemoteSettingsSection(s.Key, newName, false));
            if (!RemoteSettingsPairsEqual(expected, before.Local)) { throw RemoteSettingsChanged(); }
            IEnumerable<KeyValuePair<string, string>> section = after.Local.Where(s => IsRemoteSettingsSection(s.Key, newName, false));
            if (!RemoteSettingsPairsEqual(section, [new("remote." + newName + ".url", newUrl),
                new("remote." + newName + ".fetch", "+refs/heads/*:refs/remotes/" + newName + "/*")])) { throw RemoteSettingsChanged(); }
        }
        else
        {
            string oldPrefix = "remote." + oldName + ".", newPrefix = "remote." + newName + ".";
            List<KeyValuePair<string, string>> expected = before.Local.Select(s => IsRemoteSettingsSection(s.Key, oldName, false)
                ? new KeyValuePair<string, string>(newPrefix + s.Key[oldPrefix.Length..], s.Key == oldPrefix + "fetch"
                    ? s.Value.Replace(":refs/remotes/" + oldName + "/", ":refs/remotes/" + newName + "/", StringComparison.Ordinal) : s.Value)
                : s).ToList();
            foreach (GitRemoteSetting scalar in RemoteNativeTrackingScalars(before).Where(s => s.Value == oldName))
            {
                // Git cannot rewrite a scalar with multiple local values; it warns and retains them.
                // Inherited branch remotes get a local override. A global pushDefault is deliberately retained by Git.
                if (scalar.Key == "remote.pushdefault" && scalar.Scope != "local" || expected.Count(s => s.Key == scalar.Key) > 1) { continue; }
                expected.RemoveAll(s => s.Key == scalar.Key); expected.Add(new(scalar.Key, newName));
            }
            if (!RemoteSettingsPairsEqual(expected, after.Local)) { throw RemoteSettingsChanged(); }
        }
        await CheckRemoteSettingsForeignSourcesAsync(before, after, token).ConfigureAwait(false);
    }
    private static async Task CheckRemoteNativeRemovalTransitionAsync(RemoteSettingsState before, RemoteSettingsState after, string name, CancellationToken token)
    {
        List<KeyValuePair<string, string>> expected = before.Local.Where(s => !IsRemoteSettingsSection(s.Key, name, false)).ToList();
        foreach (GitRemoteSetting scalar in RemoteNativeTrackingScalars(before).Where(s => s.Value == name))
        {
            if (scalar.Key == "remote.pushdefault" && scalar.Scope != "local") { continue; }
            UnsetScalar(scalar.Key);
            if (scalar.Key.StartsWith("branch.", StringComparison.Ordinal) && scalar.Key.EndsWith(".remote", StringComparison.Ordinal))
            { UnsetScalar(scalar.Key[..^7] + ".merge"); }
        }
        if (!RemoteSettingsPairsEqual(expected, after.Local)) { throw RemoteSettingsChanged(); }
        await CheckRemoteSettingsForeignSourcesAsync(before, after, token).ConfigureAwait(false);
        void UnsetScalar(string key) { if (expected.Count(s => s.Key == key) <= 1) { expected.RemoveAll(s => s.Key == key); } }
    }
    private static IEnumerable<GitRemoteSetting> RemoteNativeTrackingScalars(RemoteSettingsState state)
        => state.Effective.Where(s => s.Key == "remote.pushdefault" || s.Key.StartsWith("branch.", StringComparison.Ordinal)
            && (s.Key.EndsWith(".remote", StringComparison.Ordinal) || s.Key.EndsWith(".pushremote", StringComparison.Ordinal)))
            .GroupBy(s => s.Key, StringComparer.Ordinal).Select(g => g.Last());
    private static bool RemoteSettingsPairsEqual(IEnumerable<KeyValuePair<string, string>> first, IEnumerable<KeyValuePair<string, string>> second)
    {
        var a = first.GroupBy(s => s.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(s => s.Value).ToArray(), StringComparer.Ordinal);
        var b = second.GroupBy(s => s.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(s => s.Value).ToArray(), StringComparer.Ordinal);
        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out string[]? values) && values.SequenceEqual(pair.Value, StringComparer.Ordinal));
    }
    private static async Task CheckRemoteSettingsForeignSourcesAsync(RemoteSettingsState before, RemoteSettingsState after, CancellationToken token)
    {
        foreach (RemoteSettingsFile file in before.Sources.Where(s => s.Path != before.Config.Path))
        {
            // A URL-dependent include can legitimately stop being active after native remote removal.
            RemoteSettingsFile current = after.Sources.SingleOrDefault(s => s.Path == file.Path)
                ?? await ReadRemoteSettingsFileAsync(file.LogicalPath, token).ConfigureAwait(false);
            CheckRemoteSettingsFile(file, current);
        }
    }
}

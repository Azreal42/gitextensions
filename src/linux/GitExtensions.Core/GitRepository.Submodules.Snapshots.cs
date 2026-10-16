namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private async Task<IReadOnlyList<GitSubmoduleSnapshot>> ReadSubmoduleSnapshotsAsync(GitRepositoryInfo info, CancellationToken token)
    {
        string modulesPath = System.IO.Path.Combine(info.RootPath, ".gitmodules");
        WorktreeNode modules = await ReadWorktreeNodeAsync(modulesPath, token).ConfigureAwait(false);
        if (modules.Identity is not null) { ValidateRemovalFile(modulesPath); }
        List<WorktreeNode> source = [];
        foreach (string path in new[] { info.RootPath, info.CommonDirectory, info.GitDirectory, System.IO.Path.Combine(info.RootPath, ".git"),
            System.IO.Path.Combine(info.GitDirectory, "HEAD"), modulesPath }.Distinct(StringComparer.Ordinal))
        { source.Add(await ReadWorktreeNodeAsync(path, token).ConfigureAwait(false)); }
        RemoteSettingsState configuration = await ReadRemoteSettingsStateAsync(info.CommonDirectory, token).ConfigureAwait(false);
        RemovalIndexEntry[] index = await ReadRemovalIndexAsync(token).ConfigureAwait(false);
        Dictionary<string, List<KeyValuePair<string, string>>> registrations = new(StringComparer.Ordinal);
        if (modules.Identity is not null)
        {
            GitBinaryResult values = await RemovalCommandAsync(["config", "--no-includes", "--file", modulesPath, "--null", "--get-regexp", "^submodule\\..*\\.(path|url)$"], token, allowNoMatches: true).ConfigureAwait(false);
            foreach (string entry in RemovalNullStrings(values.StandardOutput))
            {
                int separator = entry.IndexOf('\n');
                if (separator < 0) { throw new InvalidOperationException("Git returned an invalid submodule configuration entry."); }
                string key = entry[..separator]; int dot = key.LastIndexOf('.'); string name = key[10..dot];
                if (!registrations.TryGetValue(name, out List<KeyValuePair<string, string>>? settings)) { registrations.Add(name, settings = []); }
                settings.Add(new(key[(dot + 1)..], entry[(separator + 1)..]));
            }
        }
        (string Name, string Path, string? Url, bool Unique)[] configured = registrations.Select(pair =>
        {
            string[] paths = pair.Value.Where(setting => setting.Key == "path").Select(setting => setting.Value).ToArray();
            string[] urls = pair.Value.Where(setting => setting.Key == "url").Select(setting => setting.Value).ToArray();
            return (pair.Key, paths.LastOrDefault() ?? "", urls.LastOrDefault(), paths.Length == 1 && urls.Length <= 1);
        }).Where(item => item.Item2.Length > 0).ToArray();
        List<GitSubmoduleSnapshot> snapshots = [];
        foreach (IGrouping<string, RemovalIndexEntry> group in index.Where(entry => entry.Mode == "160000").GroupBy(entry => entry.Path, StringComparer.Ordinal))
        {
            string path = group.Key;
            RemovalIndexEntry[] entries = index.Where(entry => entry.Path == path).ToArray();
            var matching = configured.Where(item => item.Path == path).ToArray();
            string name = matching.Length == 1 ? matching[0].Name : path;
            string? url = matching.Length == 1 ? matching[0].Url : null;
            bool unmerged = entries.Length != 1 || entries[0].Stage != "0";
            string? indexed = unmerged ? null : entries[0].Hash;
            string? error = matching.Length != 1 ? "The indexed gitlink has no unique .gitmodules registration." : null;
            List<WorktreeNode> nodes = [];
            GitRepositoryInfo? moduleInfo = null; string? head = null; string branch = ""; char code = unmerged ? 'U' : '-'; string? commit = indexed;
            try
            {
                ValidateRemovalLocation(path);
                string full = System.IO.Path.Combine(info.RootPath, path);
                nodes.Add(await ReadWorktreeNodeAsync(full, token).ConfigureAwait(false));
                string markerPath = System.IO.Path.Combine(full, ".git");
                WorktreeNode? marker = nodes[0].Identity?.IsDirectory == true ? await ReadWorktreeNodeAsync(markerPath, token).ConfigureAwait(false) : null;
                if (marker is not null)
                {
                    nodes.Add(marker);
                    if (marker.Identity is not null)
                    {
                        moduleInfo = await DiscoverRepositoryAsync(full, _runner, token).ConfigureAwait(false);
                        WorktreeNode actualRoot = await ReadWorktreeNodeAsync(moduleInfo.RootPath, token).ConfigureAwait(false);
                        WorktreeNode common = await ReadWorktreeNodeAsync(moduleInfo.CommonDirectory, token).ConfigureAwait(false);
                        if (moduleInfo.IsBare || !SameWorktreeNode(nodes[0], actualRoot)
                            || SameWorktreeNode(common, source.Single(item => item.LogicalPath == info.CommonDirectory)))
                        { throw new InvalidOperationException("The submodule path does not contain its own distinct Git working tree."); }
                        foreach (string location in new[] { moduleInfo.GitDirectory, moduleInfo.CommonDirectory,
                            System.IO.Path.Combine(moduleInfo.GitDirectory, "HEAD"), System.IO.Path.Combine(moduleInfo.CommonDirectory, "config") }.Distinct(StringComparer.Ordinal))
                        { nodes.Add(await ReadWorktreeNodeAsync(location, token).ConfigureAwait(false)); }
                        GitResult resolved = await _runner.RunAsync(full, [.. GlobalOptions, "rev-parse", "--verify", "HEAD"], token).ConfigureAwait(false);
                        if (resolved.Success) { head = ReadOutputLine(resolved.StandardOutput); }
                    }
                }
                if (error is null)
                {
                    GitResult status = await _runner.RunAsync(info.RootPath, [.. GlobalOptions, "--literal-pathspecs", "submodule", "status", "--", path], token).ConfigureAwait(false);
                    if (!status.Success) { throw new GitCommandException(["submodule", "status", "--", path], status); }
                    (code, commit, branch) = ReadSelectedSubmoduleStatus(status.StandardOutput, path);
                    if (unmerged) { code = 'U'; commit = null; branch = ""; }
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or GitCommandException)
            { error = exception.Message; moduleInfo = null; }
            GitSubmoduleRemovalSelection? removal = error is null && !unmerged && matching[0].Unique
                ? new(info.RootPath, name, path, url, indexed!, modules.ContentHash!, File.Exists(System.IO.Path.Combine(info.RootPath, path, ".git"))
                    || Directory.Exists(System.IO.Path.Combine(info.RootPath, path, ".git"))) : null;
            snapshots.Add(new(name, path, url, indexed, head, code == 'U' ? null : code == '-' ? indexed : commit, branch, code, error, removal,
                new(info.RootPath, SubmoduleIndexSignature(entries), configuration.Fingerprint, source.ToArray(), nodes.ToArray(), moduleInfo)));
        }
        CheckSubmoduleNodes(source, await ReadSubmoduleNodesAsync(source.Select(node => node.LogicalPath), token).ConfigureAwait(false));
        RemoteSettingsState finalConfiguration = await ReadRemoteSettingsStateAsync(info.CommonDirectory, token).ConfigureAwait(false);
        if (configuration.Fingerprint != finalConfiguration.Fingerprint
            || SubmoduleIndexSignature(index) != SubmoduleIndexSignature(await ReadRemovalIndexAsync(token).ConfigureAwait(false)))
        { throw new InvalidOperationException("Submodule registrations, Git configuration or index changed while loading. Refresh the list."); }
        return snapshots.AsReadOnly();
    }

    private static (char Code, string? Commit, string Branch) ReadSelectedSubmoduleStatus(string output, string path)
    {
        int space = output.IndexOf(' ', 1);
        if (space < 2 || output[0] is not (' ' or '+' or '-' or 'U')) { throw new InvalidOperationException("Git returned an invalid submodule status."); }
        string hash = output[1..space]; string remainder = ReadOutputLine(output[(space + 1)..]);
        if (hash.Length is not (40 or 64) || !hash.All(Uri.IsHexDigit) || !remainder.StartsWith(path, StringComparison.Ordinal))
        { throw new InvalidOperationException("Git returned a status for a different submodule."); }
        string suffix = remainder[path.Length..];
        if (suffix.Length > 0 && !(suffix.StartsWith(" (", StringComparison.Ordinal) && suffix.EndsWith(')')))
        { throw new InvalidOperationException("Git returned an invalid submodule description."); }
        string branch = suffix.Length > 0 ? suffix[2..^1] : "";
        return (output[0], hash.All(character => character == '0') ? null : hash, branch);
    }
    private static string SubmoduleIndexSignature(IEnumerable<RemovalIndexEntry> entries)
        => string.Join('\0', entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ThenBy(entry => entry.Stage, StringComparer.Ordinal)
            .Select(entry => entry.Mode + " " + entry.Hash + " " + entry.Stage + "\t" + entry.Path));
    private static async Task<IReadOnlyList<WorktreeNode>> ReadSubmoduleNodesAsync(IEnumerable<string> paths, CancellationToken token)
    {
        List<WorktreeNode> nodes = [];
        foreach (string path in paths) { nodes.Add(await ReadWorktreeNodeAsync(path, token).ConfigureAwait(false)); }
        return nodes;
    }
}

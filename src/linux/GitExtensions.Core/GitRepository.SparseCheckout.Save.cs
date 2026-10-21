using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Saves sparse settings and rules without updating the index or working files.
    /// The saved settings can deliberately differ from the checkout until Reapply or Disable is requested.</summary>
    public Task SaveSparseCheckoutSettingsAsync(bool enabled, bool coneMode, bool sparseIndex,
        IEnumerable<string> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        string[] values = rules.ToArray();
        foreach (string value in values) { ArgumentNullException.ThrowIfNull(value); ValidateSparseLine(value); }
        string[] directories = coneMode && values.Length > 0 ? ValidateSparseDirectories(values) : [];
        string text = coneMode ? SparseConePatterns(directories) : string.Join('\n', values) + (values.Length > 0 ? "\n" : "");
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            GitRepositoryInfo info = await GetInfoAsync(token).ConfigureAwait(false);
            string path = await GetGitPathAsync("info/sparse-checkout", token).ConfigureAwait(false);
            GitRulesLocation location = GitRulesFiles.Resolve(path);
            if (new FileInfo(path).LinkTarget is not null || GitRulesIdentity.ReadPath(location.Path) is { IsRegular: false })
            { throw new IOException("The sparse rules path must be a regular file."); }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(location.Path)!);
            GitRulesFiles.CheckLocation(location, GitRulesFiles.Resolve(path), createdParents: true);
            string lockPath = location.Path + ".lock";
            // The native sparse writer uses this same lock. CreateNew never steals an existing writer's lock.
            await using FileStream file = new(lockPath, new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous });
            bool committed = false;
            try
            {
                token.ThrowIfCancellationRequested();
                // After admission, drain every write even if the editor is being closed.
                await EnableSparseWorktreeConfigAsync(info, CancellationToken.None).ConfigureAwait(false);
                await SetSparseWorktreeValueAsync("core.sparseCheckout", enabled, CancellationToken.None).ConfigureAwait(false);
                await SetSparseWorktreeValueAsync("core.sparseCheckoutCone", coneMode, CancellationToken.None).ConfigureAwait(false);
                await SetSparseWorktreeValueAsync("index.sparse", coneMode && sparseIndex, CancellationToken.None).ConfigureAwait(false);
                await file.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                await file.DisposeAsync().ConfigureAwait(false);
                GitRulesFiles.CheckLocation(location, GitRulesFiles.Resolve(path), createdParents: true);
                File.Move(lockPath, location.Path, overwrite: true); committed = true;
                return true;
            }
            finally
            {
                await file.DisposeAsync().ConfigureAwait(false);
                if (!committed) { File.Delete(lockPath); }
            }
        }, cancellationToken);
    }

    private async Task SetSparseWorktreeValueAsync(string key, bool value, CancellationToken token)
    { await ExecuteAsync(["config", "--worktree", "--replace-all", key, value ? "true" : "false"], token).ConfigureAwait(false); }

    private async Task EnableSparseWorktreeConfigAsync(GitRepositoryInfo info, CancellationToken token)
    {
        GitResult extension = await ExecuteAsync(["config", "--local", "--type=bool", "--get", "extensions.worktreeConfig"], token, allowedExitCodes: [1]).ConfigureAwait(false);
        if (extension.Success && extension.StandardOutput.TrimEnd('\r', '\n') == "true") { return; }
        string common = System.IO.Path.Combine(info.CommonDirectory, "config");
        string main = System.IO.Path.Combine(info.CommonDirectory, "config.worktree");
        // Match Git's init_worktree_config: core.bare=true and core.worktree belong to the main worktree.
        // core.bare=false remains shared, since it can override an inherited true value.
        GitResult bare = await ExecuteAsync(["config", "--file", common, "--type=bool", "--get", "core.bare"], token, allowedExitCodes: [1]).ConfigureAwait(false);
        GitResult worktree = await ExecuteAsync(["config", "--file", common, "--get", "core.worktree"], token, allowedExitCodes: [1]).ConfigureAwait(false);
        await ExecuteAsync(["config", "--local", "--replace-all", "extensions.worktreeConfig", "true"], token).ConfigureAwait(false);
        if (bare.Success && bare.StandardOutput.TrimEnd('\r', '\n') == "true")
        { await MoveSparseConfigAsync("core.bare", "true", common, main, token).ConfigureAwait(false); }
        if (worktree.Success)
        { await MoveSparseConfigAsync("core.worktree", worktree.StandardOutput.EndsWith('\n') ? worktree.StandardOutput[..^1] : worktree.StandardOutput, common, main, token).ConfigureAwait(false); }
    }

    private async Task MoveSparseConfigAsync(string key, string value, string common, string main, CancellationToken token)
    {
        await ExecuteAsync(["config", "--file", main, "--replace-all", key, value], token).ConfigureAwait(false);
        await ExecuteAsync(["config", "--file", common, "--unset-all", key], token, allowedExitCodes: [5]).ConfigureAwait(false);
    }

    private static string SparseConePatterns(IEnumerable<string> paths)
    {
        // Git's cone writer emits shallow parent pairs before recursively selected directories.
        string[] selected = paths.Select(path => path.Trim(' ', '\t', '\v', '\f').TrimEnd('/'))
            .Where(path => path.Length > 0 && path != ".").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        bool HasSelectedParent(string path) => selected.Any(parent => path.StartsWith(parent + "/", StringComparison.Ordinal));
        string[] recursive = selected.Where(path => !HasSelectedParent(path)).ToArray();
        HashSet<string> parents = new(StringComparer.Ordinal);
        foreach (string path in recursive)
        {
            for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            { parents.Add(path[..slash]); }
        }
        StringBuilder result = new("/*\n!/*/\n");
        foreach (string parent in parents.Order(StringComparer.Ordinal))
        { string pattern = EscapeSparseCone(parent); result.Append('/').Append(pattern).Append("/\n!/").Append(pattern).Append("/*/\n"); }
        foreach (string path in recursive) { result.Append('/').Append(EscapeSparseCone(path)).Append("/\n"); }
        return result.ToString();
    }

    private static string EscapeSparseCone(string path)
    {
        StringBuilder result = new(path.Length);
        foreach (char character in path)
        { if (character is '*' or '?' or '[' or '\\') { result.Append('\\'); } result.Append(character); }
        return result.ToString();
    }
}

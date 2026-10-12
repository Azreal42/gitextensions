using System.Security.Cryptography;
using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private static readonly UTF8Encoding SubmoduleRemovalUtf8 = new(false, true);

    public Task<IReadOnlyList<GitSubmoduleRemovalSelection>> GetSubmoduleRemovalSelectionsAsync(CancellationToken cancellationToken = default)
        => WithMutationAsync(ReadSubmoduleRemovalSelectionsAsync, cancellationToken);

    /// <summary>Windows FormSubmodules Remove: uncache the gitlink, update registrations, retain all module data.</summary>
    public Task<GitSubmoduleRemovalResult> RemoveSubmoduleAsync(GitSubmoduleRemovalSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return WithMutationAsync(async token =>
        {
            if (selection.RepositoryPath != Path) { throw new ArgumentException("The selected submodule belongs to another repository.", nameof(selection)); }
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            await CheckSubmoduleRemovalOperationAsync(token).ConfigureAwait(false);
            string index = await GetGitPathAsync("index", token).ConfigureAwait(false);
            string config = await GetGitPathAsync("config", token).ConfigureAwait(false);
            string modules = System.IO.Path.Combine(Path, ".gitmodules");
            ValidateRemovalFile(index); ValidateRemovalFile(config); ValidateRemovalFile(modules);
            List<(string Path, FileStream Stream)> locks = [];
            string temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-submodule-remove-" + Guid.NewGuid().ToString("N"));
            // Config may be protected by its original parent directory. Never expose
            // its preparation copy through the process umask in a shared temp directory.
            if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(temporary); }
            else { Directory.CreateDirectory(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            try
            {
                // Git's conventional locks protect both ordinary and linked-worktree repositories.
                foreach (string file in new[] { index, config, modules })
                {
                    string lockPath = file + ".lock";
                    locks.Add((lockPath, new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)));
                }
                GitSubmoduleRemovalSelection current = (await ReadSubmoduleRemovalSelectionsAsync(token).ConfigureAwait(false))
                    .SingleOrDefault(item => item.Name == selection.Name && item.Path == selection.Path)
                    ?? throw new InvalidOperationException("The submodule registration changed. Refresh and select it again.");
                if (current != selection) { throw new InvalidOperationException("The submodule registration or indexed commit changed. Refresh and select it again."); }
                ValidateRemovalLocation(selection.Path);
                RemovalIndexEntry[] moduleEntries = (await ReadRemovalIndexAsync(token).ConfigureAwait(false)).Where(entry => entry.Path == ".gitmodules").ToArray();
                if (moduleEntries.Length != 1 || moduleEntries[0].Stage != "0" || moduleEntries[0].Mode is not ("100644" or "100755"))
                { throw new InvalidOperationException("Submodule removal requires a regular, unconflicted indexed .gitmodules file."); }
                Dictionary<string, byte[]> before = new(StringComparer.Ordinal);
                foreach (string file in new[] { index, config, modules }) { before.Add(file, await File.ReadAllBytesAsync(file, token).ConfigureAwait(false)); }
                string temporaryIndex = System.IO.Path.Combine(temporary, "index");
                string temporaryConfig = System.IO.Path.Combine(temporary, "config");
                string temporaryModules = System.IO.Path.Combine(temporary, "gitmodules");
                await File.WriteAllBytesAsync(temporaryIndex, before[index], token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(temporaryConfig, before[config], token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(temporaryModules, before[modules], token).ConfigureAwait(false);
                Dictionary<string, string?> environment = new() { ["GIT_INDEX_FILE"] = temporaryIndex, ["GIT_OPTIONAL_LOCKS"] = "0", ["LC_ALL"] = "C" };
                // A prepared index must be self-contained when published from its temporary directory.
                await RemovalCommandAsync(["update-index", "--no-split-index"], token, environment).ConfigureAwait(false);
                // All native rejection paths run on copies. No --force: retain Git's index safety checks.
                await RemovalCommandAsync(["config", "--no-includes", "--file", temporaryConfig, "--list"], token, environment).ConfigureAwait(false);
                await RemovalCommandAsync(["config", "--no-includes", "--file", temporaryModules, "--remove-section", "--", "submodule." + selection.Name], token, environment).ConfigureAwait(false);
                GitBinaryResult local = await RemovalCommandAsync(["config", "--no-includes", "--file", temporaryConfig, "--remove-section", "--", "submodule." + selection.Name], token, environment, allowMissingSection: true).ConfigureAwait(false);
                bool lastSection = !RemovalHasConfigSection(await File.ReadAllBytesAsync(temporaryModules, token).ConfigureAwait(false));
                if (!lastSection)
                {
                    // Modern Git also insists that .gitmodules match the index before rm.
                    // Prepare Windows' Save+Stage on the copied index, retaining the native
                    // gitlink HEAD/index/module checks and never forcing a cached removal.
                    await StageRemovalModulesAsync(before[modules], moduleEntries[0].Mode, token, environment).ConfigureAwait(false);
                }
                await RemovalCommandAsync(["--literal-pathspecs", "rm", "--cached", "--", selection.Path], token, environment).ConfigureAwait(false);
                if (lastSection)
                {
                    await RemovalCommandAsync(["--literal-pathspecs", "rm", "--cached", "--", ".gitmodules"], token, environment).ConfigureAwait(false);
                }
                byte[] updatedModules = await File.ReadAllBytesAsync(temporaryModules, token).ConfigureAwait(false);
                if (!lastSection)
                {
                    // Stage the exact rewritten bytes, using the repository's native clean conversion.
                    await StageRemovalModulesAsync(updatedModules, moduleEntries[0].Mode, token, environment).ConfigureAwait(false);
                }
                Dictionary<string, byte[]> after = new(StringComparer.Ordinal)
                {
                    [index] = await File.ReadAllBytesAsync(temporaryIndex, token).ConfigureAwait(false),
                    [config] = local.Success ? await File.ReadAllBytesAsync(temporaryConfig, token).ConfigureAwait(false) : before[config]
                };
                if (!lastSection) { after.Add(modules, updatedModules); }
                ValidateRemovalLocation(selection.Path);
                foreach ((string file, byte[] bytes) in before)
                {
                    ValidateRemovalFile(file);
                    if (!(await File.ReadAllBytesAsync(file, token).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes))
                    { throw new InvalidOperationException("Repository files changed during removal. Refresh and select the submodule again."); }
                }
                await CheckSubmoduleRemovalOperationAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                // Once publication begins, complete the short transaction without cancellation.
                // IO failure restores every published file while the Git locks remain held.
                List<string> published = [];
                try
                {
                    foreach ((string file, byte[] bytes) in after)
                    {
                        PublishRemovalFile(file, bytes); published.Add(file);
                    }
                }
                catch (Exception failure)
                {
                    List<Exception> failures = [failure];
                    foreach (string file in published.AsEnumerable().Reverse())
                    {
                        try { PublishRemovalFile(file, before[file]); } catch (Exception rollback) { failures.Add(rollback); }
                    }
                    if (failures.Count > 1) { throw new AggregateException("Submodule removal could not restore repository files after an IO failure.", failures); }
                    throw;
                }
                return new GitSubmoduleRemovalResult(selection.Path, lastSection);
            }
            finally
            {
                foreach ((string lockPath, FileStream stream) in locks) { stream.Dispose(); File.Delete(lockPath); }
                Directory.Delete(temporary, recursive: true);
            }
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<GitSubmoduleRemovalSelection>> ReadSubmoduleRemovalSelectionsAsync(CancellationToken token)
    {
        await EnsureWorktreeAsync(token).ConfigureAwait(false);
        string modules = System.IO.Path.Combine(Path, ".gitmodules");
        if (!File.Exists(modules) && new FileInfo(modules).LinkTarget is null) { return []; }
        ValidateRemovalFile(modules);
        byte[] bytes = await File.ReadAllBytesAsync(modules, token).ConfigureAwait(false);
        string fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        GitBinaryResult keys = await RemovalCommandAsync(["config", "--no-includes", "--file", modules, "--null", "--name-only", "--get-regexp", "^submodule\\..*\\.path$"], token, allowNoMatches: true).ConfigureAwait(false);
        RemovalIndexEntry[] index = await ReadRemovalIndexAsync(token).ConfigureAwait(false);
        List<GitSubmoduleRemovalSelection> selections = [];
        foreach (string key in RemovalNullStrings(keys.StandardOutput).Distinct(StringComparer.Ordinal))
        {
            string name = key[10..^5];
            string[] paths = RemovalNullStrings((await RemovalCommandAsync(["config", "--no-includes", "--file", modules, "--null", "--get-all", key], token).ConfigureAwait(false)).StandardOutput);
            if (paths.Length != 1) { throw new InvalidOperationException("A submodule registration contains multiple paths: " + name); }
            string path = paths[0]; ValidateRemovalLocation(path);
            RemovalIndexEntry[] matching = index.Where(entry => entry.Path == path).ToArray();
            if (matching.Length == 0) { continue; } // The last removal deliberately leaves the worktree .gitmodules file.
            if (matching.Length != 1 || matching[0].Mode != "160000" || matching[0].Stage != "0")
            { throw new InvalidOperationException("The selected submodule is not an unconflicted indexed gitlink: " + path); }
            string urlKey = "submodule." + name + ".url";
            string[] urls = RemovalNullStrings((await RemovalCommandAsync(["config", "--no-includes", "--file", modules, "--null", "--get-all", urlKey], token, allowNoMatches: true).ConfigureAwait(false)).StandardOutput);
            if (urls.Length > 1) { throw new InvalidOperationException("A submodule registration contains multiple URLs: " + name); }
            string marker = System.IO.Path.Combine(Path, path, ".git");
            selections.Add(new(Path, name, path, urls.SingleOrDefault(), matching[0].Hash, fingerprint, File.Exists(marker) || Directory.Exists(marker)));
        }
        if (selections.GroupBy(item => item.Path, StringComparer.Ordinal).Any(group => group.Count() > 1))
        { throw new InvalidOperationException("Multiple submodule registrations refer to the same path."); }
        if (!(await File.ReadAllBytesAsync(modules, token).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes))
        { throw new InvalidOperationException("Submodule registrations changed while loading. Refresh the list."); }
        return selections;
    }

    private sealed record RemovalIndexEntry(string Mode, string Hash, string Stage, string Path);
    private async Task StageRemovalModulesAsync(byte[] bytes, string mode, CancellationToken token, IReadOnlyDictionary<string, string?> environment)
    {
        GitBinaryResult blob = await RemovalCommandAsync(["hash-object", "-w", "--path=.gitmodules", "--stdin"], token, environment, bytes).ConfigureAwait(false);
        string objectName = SubmoduleRemovalUtf8.GetString(blob.StandardOutput).TrimEnd('\n');
        await RemovalCommandAsync(["update-index", "--add", "--cacheinfo", mode + "," + objectName + ",.gitmodules"], token, environment).ConfigureAwait(false);
    }
    private async Task<RemovalIndexEntry[]> ReadRemovalIndexAsync(CancellationToken token)
    {
        GitBinaryResult result = await RemovalCommandAsync(["--literal-pathspecs", "ls-files", "--stage", "-z"], token).ConfigureAwait(false);
        return RemovalNullStrings(result.StandardOutput).Select(record =>
        {
            int tab = record.IndexOf('\t');
            if (tab < 0) { throw new InvalidOperationException("Git returned an invalid index record."); }
            string[] metadata = record[..tab].Split(' ');
            if (metadata.Length != 3) { throw new InvalidOperationException("Git returned an invalid index record."); }
            return new RemovalIndexEntry(metadata[0], metadata[1], metadata[2], record[(tab + 1)..]);
        }).ToArray();
    }

    private async Task<GitBinaryResult> RemovalCommandAsync(IReadOnlyList<string> arguments, CancellationToken token,
        IReadOnlyDictionary<string, string?>? environment = null, byte[]? input = null, bool allowNoMatches = false, bool allowMissingSection = false)
    {
        GitBinaryResult result = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. arguments], token, input, environment).ConfigureAwait(false);
        if (!result.Success && !(allowNoMatches && result.ExitCode == 1)
            && !(allowMissingSection && result.ExitCode == 128 && result.StandardError.Contains("no such section:", StringComparison.Ordinal)))
        { throw new GitCommandException(arguments, new GitResult(result.ExitCode, Encoding.UTF8.GetString(result.StandardOutput), result.StandardError)); }
        return result;
    }

    private static string[] RemovalNullStrings(byte[] bytes)
    {
        try { return SubmoduleRemovalUtf8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries); }
        catch (DecoderFallbackException exception) { throw new InvalidOperationException("Submodule removal requires losslessly readable UTF-8 names and paths.", exception); }
    }

    private void ValidateRemovalLocation(string path)
    {
        if (path != ValidatePath(path) || path == "." || path == ".gitmodules" || path.Split('/').Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        { throw new ArgumentException("The submodule path must be an exact relative worktree path.", nameof(path)); }
        string location = Path;
        foreach (string part in path.Split('/'))
        {
            location = System.IO.Path.Combine(location, part);
            if (new DirectoryInfo(location).LinkTarget is not null || new FileInfo(location).LinkTarget is not null)
            { throw new InvalidOperationException("A submodule path cannot traverse a symbolic link: " + path); }
        }
    }

    private static void ValidateRemovalFile(string file)
    {
        if (new FileInfo(file).LinkTarget is not null || !File.Exists(file))
        { throw new InvalidOperationException("Submodule removal requires a regular repository file: " + file); }
    }

    private async Task CheckSubmoduleRemovalOperationAsync(CancellationToken token)
    {
        foreach (string name in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "sequencer" })
        {
            string location = await GetGitPathAsync(name, token).ConfigureAwait(false);
            if (File.Exists(location) || Directory.Exists(location))
            { throw new InvalidOperationException("Finish or abort the current Git operation before removing a submodule."); }
        }
        if ((await ReadRemovalIndexAsync(token).ConfigureAwait(false)).Any(entry => entry.Stage != "0"))
        { throw new InvalidOperationException("Resolve index conflicts before removing a submodule."); }
    }

    private static bool RemovalHasConfigSection(byte[] bytes)
    {
        // Git has already validated this file. Empty section headers count in the Windows ConfigFile too.
        bool continuation = false;
        foreach (string line in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            string text = line.TrimStart(' ', '\t', '\r');
            if (!continuation && text.StartsWith('[')) { return true; }
            if (!continuation && (text.StartsWith('#') || text.StartsWith(';'))) { continue; }
            int escapes = 0;
            for (int i = line.TrimEnd('\r').Length - 1; i >= 0 && line[i] == '\\'; i--) { escapes++; }
            continuation = escapes % 2 != 0;
        }
        return false;
    }

    private static void PublishRemovalFile(string file, byte[] bytes)
    {
        string replacement = file + ".gitextensions-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(replacement, bytes);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(replacement, File.GetUnixFileMode(file)); }
            File.Move(replacement, file, overwrite: true);
        }
        finally { File.Delete(replacement); }
    }
}

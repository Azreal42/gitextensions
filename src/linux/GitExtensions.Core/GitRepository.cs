using System.Globalization;

namespace GitExtensions.Linux;

/// <summary>Platform-neutral repository workflows backed by the user's Git installation.</summary>
public sealed partial class GitRepository
{
    private readonly GitRunner _runner;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private static readonly string[] GlobalOptions = ["--no-pager", "-c", "color.ui=false", "-c", "core.quotepath=false"];

    public GitRepository(string path, GitRunner? runner = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        _runner = runner ?? new GitRunner();
    }

    public string Path { get; }
    /// <summary>Discovery metadata; populated by OpenAsync or GetInfoAsync.</summary>
    public GitRepositoryInfo? Info { get; private set; }
    public bool IsBare => Info?.IsBare == true;

    public async Task<GitResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        if (!await IsRepositoryAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The selected directory is not a Git repository.");
        }

        return await MutateAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if ((await GetInfoAsync(cancellationToken).ConfigureAwait(false)).IsBare)
        {
            GitResult branch = await ExecuteAsync(["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken, allowedExitCodes: [1]).ConfigureAwait(false);
            string? head = await HasHeadAsync(cancellationToken).ConfigureAwait(false)
                ? await ResolveRevisionAsync("HEAD", cancellationToken).ConfigureAwait(false) : null;
            return new GitStatus(branch.Success ? ReadOutputLine(branch.StandardOutput) : "(detached)", head, 0, 0, []);
        }

        return GitOutputParser.ParseStatus((await ExecuteAsync(["status", "--porcelain=2", "--branch", "-z", "--untracked-files=all"], cancellationToken).ConfigureAwait(false)).StandardOutput);
    }

    public Task<IReadOnlyList<GitCommit>> GetLogAsync(int count = 100, string? revision = null, CancellationToken cancellationToken = default,
        bool all = true, string? path = null, int skip = 0, string? author = null, string? search = null)
        => GetLogAsync(new GitLogQuery(Count: count, Skip: skip, Revision: revision, All: all, Path: path, Author: author, Message: search), cancellationToken);

    public async Task<IReadOnlyList<GitReference>> GetReferencesAsync(CancellationToken cancellationToken = default)
    {
        GitReference[] references = (await ReadReferenceTargetsAsync([], cancellationToken).ConfigureAwait(false))
            .Select(target => target.Reference).ToArray();
        if (!references.Any(reference => reference.IsRemote)) { return references; }
        IReadOnlyList<KeyValuePair<string, string>> configuration = await GetConfigAsync("^remote\\.", cancellationToken: cancellationToken).ConfigureAwait(false);
        Dictionary<string, string?> colors = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> setting in configuration)
        {
            int separator = setting.Key.LastIndexOf('.');
            if (separator <= 7) { continue; }
            string remote = setting.Key[7..separator];
            colors.TryAdd(remote, null);
            if (setting.Key[(separator + 1)..] == "color") { colors[remote] = EmptyToNull(setting.Value); }
        }
        // A remote name can itself contain '/'. An uncolored nested remote still
        // owns its refs and must not inherit a shorter remote's custom color.
        (string Prefix, string? Color)[] remotes = colors.OrderByDescending(pair => pair.Key.Length)
            .Select(pair => ("refs/remotes/" + pair.Key + "/", pair.Value)).ToArray();
        return references.Select(reference => reference.IsRemote
            ? reference with { RemoteColor = remotes.FirstOrDefault(remote => reference.Name.StartsWith(remote.Prefix, StringComparison.Ordinal)).Color }
            : reference).ToArray();
    }

    public async Task<string> GetDiffAsync(string? path = null, bool staged = false, string? revision = null, CancellationToken cancellationToken = default)
        => await ExecuteDiffReadAsync(await BuildWorktreeDiffArgumentsAsync(path, staged, revision, null, null, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private async Task<GitDiffReadCommand> BuildWorktreeDiffArgumentsAsync(string? path, bool staged, string? revision,
        GitComparisonDiffOptions? options, string? originalPath, CancellationToken cancellationToken)
    {
        await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
        List<string> arguments = ["--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--no-color"];
        if (options is not null) { AddComparisonOptions(arguments, options); }
        if (originalPath is not null) { arguments.AddRange(["--find-renames", "--find-copies"]); }
        if (staged) { arguments.Add("--cached"); }
        if (revision is not null) { arguments.Add(await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false)); }
        arguments.Add("--");
        if (originalPath is not null) { arguments.Add(ValidatePath(originalPath)); }
        if (path is not null) { arguments.Add(ValidatePath(path)); }
        return new(arguments);
    }

    public async Task<string> GetCommitDiffAsync(string revision, string? path = null, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["show", "--encoding=UTF-8", "--format=fuller", "--no-ext-diff", "--no-textconv", await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false), "--"];
        if (path is not null)
        {
            arguments.Add(ValidatePath(path));
        }

        return (await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)).StandardOutput;
    }

    public async Task<IReadOnlyList<GitTreeEntry>> GetTreeAsync(string revision = "HEAD", string? path = null, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["ls-tree", "-r", "-z", "--full-tree", await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false), "--"];
        if (path is not null)
        {
            arguments.Add(ValidatePath(path));
        }

        GitResult result = await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(entry =>
        {
            int tab = entry.IndexOf('\t');
            string[] metadata = entry[..tab].Split(' ');
            return new GitTreeEntry(metadata[0], metadata[1], metadata[2], entry[(tab + 1)..]);
        }).ToArray();
    }

    public async Task<byte[]> GetFileBytesAsync(string path, string revision = "HEAD", CancellationToken cancellationToken = default)
    {
        string commit = await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        string[] arguments = ["cat-file", "blob", commit + ":" + ValidatePath(path)];
        GitBinaryResult result = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. arguments], cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new GitCommandException(arguments, new GitResult(result.ExitCode, "", result.StandardError));
        }

        return result.StandardOutput;
    }

    /// <summary>Applies an exact diff selection to the index or working tree. Reverse unstages a staged selection.</summary>
    public Task<GitResult> ApplyPatchAsync(string patch, bool staged = true, bool reverse = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patch);
        return ApplyFileEncodedPatchAsync(patch, staged, reverse, cancellationToken);
    }

    public Task<GitResult> StageAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
        => MutateAsync(["add", "-A", "--", .. ValidatePaths(paths)], cancellationToken);

    public Task<GitResult> StageAllAsync(CancellationToken cancellationToken = default)
        => MutateAsync(["add", "-A", "--", "."], cancellationToken);

    public async Task<GitResult> UnstageAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        string[] validated = ValidatePaths(paths);
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteAsync(await HasHeadAsync(cancellationToken).ConfigureAwait(false)
                ? ["reset", "-q", "HEAD", "--", .. validated]
                : ["rm", "--cached", "--ignore-unmatch", "--", .. validated], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public Task<GitResult> CommitAsync(string message, bool amend = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Contains('\0'))
        {
            throw new ArgumentException("Commit messages cannot contain NUL.", nameof(message));
        }

        return CommitAsync(new GitCommitOptions(message, Amend: amend), cancellationToken);
    }

    public async Task<string> ResolveRevisionAsync(string revision, CancellationToken cancellationToken = default)
        => (await ExecuteAsync(["rev-parse", "--verify", "--end-of-options", ValidateValue(revision) + "^{commit}"], cancellationToken).ConfigureAwait(false)).StandardOutput.TrimEnd('\r', '\n');

    public async Task<bool> IsRepositoryAsync(CancellationToken cancellationToken = default)
    {
        GitResult result = await ExecuteAsync(["rev-parse", "--git-dir"], cancellationToken, allowedExitCodes: [128]).ConfigureAwait(false);
        return result.Success;
    }

    public static Task<GitRepository> InitAsync(string path, string initialBranch = "main", bool bare = false, CancellationToken cancellationToken = default)
        => InitAsync(path, new GitInitOptions(initialBranch, bare), cancellationToken);

    public static Task<GitRepository> CloneAsync(string source, string destination, bool recursive = true, CancellationToken cancellationToken = default)
        => CloneAsync(source, destination, new GitCloneOptions(RecurseSubmodules: recursive), cancellationToken);

    public Task<GitResult> CreateBranchAsync(string name, string? startPoint = null, bool checkout = true, CancellationToken cancellationToken = default)
    {
        List<string> arguments = checkout ? ["switch", "-c", ValidateValue(name)] : ["branch", "--", ValidateValue(name)];
        if (startPoint is not null)
        {
            arguments.Add(ValidateValue(startPoint));
        }

        return MutateAsync(arguments, cancellationToken);
    }

    public Task<GitResult> CheckoutAsync(string revision, bool detached = false, CancellationToken cancellationToken = default)
        => MutateAsync(detached ? ["switch", "--detach", "--", ValidateValue(revision)] : ["switch", "--", ValidateValue(revision)], cancellationToken);

    public Task<GitResult> DeleteBranchAsync(string name, bool force = false, CancellationToken cancellationToken = default)
        => MutateAsync(["branch", force ? "-D" : "-d", "--", ValidateValue(name)], cancellationToken);

    public Task<GitResult> RenameBranchAsync(string oldName, string newName, CancellationToken cancellationToken = default)
        => MutateAsync(["branch", "-m", "--", ValidateValue(oldName), ValidateValue(newName)], cancellationToken);

    public async Task<IReadOnlyList<GitStash>> GetStashesAsync(CancellationToken cancellationToken = default)
    {
        GitResult result;
        if ((await GetInfoAsync(cancellationToken).ConfigureAwait(false)).IsBare)
        {
            GitResult reference = await ExecuteAsync(["show-ref", "--verify", "--quiet", "refs/stash"], cancellationToken, allowedExitCodes: [1]).ConfigureAwait(false);
            if (!reference.Success) { return []; }
            result = await ExecuteAsync(["log", "--encoding=UTF-8", "--walk-reflogs", "-z", "--format=%gd%x00%H%x00%s%x00%cI", "refs/stash", "--"], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await ExecuteAsync(["stash", "list", "--encoding=UTF-8", "-z", "--format=%gd%x00%H%x00%s%x00%cI"], cancellationToken).ConfigureAwait(false);
        }
        string[] fields = result.StandardOutput.Split('\0');
        List<GitStash> stashes = [];
        for (int i = 0; i + 3 < fields.Length; i += 4)
        {
            stashes.Add(new GitStash(fields[i], fields[i + 1], fields[i + 2], DateTimeOffset.Parse(fields[i + 3], CultureInfo.InvariantCulture)));
        }

        return stashes;
    }

    public Task<GitResult> StashAsync(string? message = null, bool includeUntracked = true, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["stash", "push"];
        if (includeUntracked)
        {
            arguments.Add("--include-untracked");
        }

        if (message is not null)
        {
            arguments.AddRange(["--message", message]);
        }

        return MutateAsync(arguments, cancellationToken);
    }

    public Task<GitResult> ApplyStashAsync(string reference, bool pop = false, bool restoreIndex = true, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["stash", pop ? "pop" : "apply"];
        if (restoreIndex)
        {
            arguments.Add("--index");
        }

        arguments.Add(ValidateValue(reference));
        return MutateAsync(arguments, cancellationToken);
    }

    public Task<GitResult> DropStashAsync(string reference, CancellationToken cancellationToken = default)
        => MutateAsync(["stash", "drop", ValidateValue(reference)], cancellationToken);

    public async Task<IReadOnlyList<GitRemote>> GetRemotesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<KeyValuePair<string, string>> configured = await GetConfigAsync("^remote\\..*\\.(url|pushurl)$", cancellationToken: cancellationToken).ConfigureAwait(false);
        Dictionary<string, (List<string> Fetch, List<string> Push)> remotes = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> item in configured)
        {
            bool push = item.Key.EndsWith(".pushurl", StringComparison.Ordinal);
            string name = item.Key[7..^(push ? 8 : 4)];
            if (!remotes.TryGetValue(name, out (List<string> Fetch, List<string> Push) urls))
            {
                urls = ([], []);
                remotes.Add(name, urls);
            }

            (push ? urls.Push : urls.Fetch).Add(item.Value);
        }

        return remotes.Select(item =>
        {
            IReadOnlyList<string> fetch = RemoteSettingsList(item.Value.Fetch);
            IReadOnlyList<string> push = RemoteSettingsList(item.Value.Push);
            return new GitRemote(item.Key, fetch, push.Count > 0 ? push : fetch);
        }).ToArray();
    }

    public Task<GitResult> AddRemoteAsync(string name, string url, CancellationToken cancellationToken = default)
        => MutateAsync(["remote", "add", "--", ValidateValue(name), ValidateValue(url)], cancellationToken);

    public Task<GitResult> RemoveRemoteAsync(string name, CancellationToken cancellationToken = default)
        => MutateAsync(["remote", "remove", ValidateValue(name)], cancellationToken);

    public Task<GitResult> SetRemoteUrlAsync(string name, string url, bool push = false, CancellationToken cancellationToken = default)
        => MutateAsync(push ? ["remote", "set-url", "--push", "--", ValidateValue(name), ValidateValue(url)] : ["remote", "set-url", "--", ValidateValue(name), ValidateValue(url)], cancellationToken);

    public Task<GitResult> FetchAsync(string? remote = null, bool prune = true, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["fetch"];
        if (prune)
        {
            arguments.Add("--prune");
        }

        if (remote is null)
        {
            arguments.Add("--all");
        }
        else
        {
            arguments.AddRange(["--", ValidateValue(remote)]);
        }

        return MutateAsync(arguments, cancellationToken);
    }

    public Task<GitResult> PullAsync(string? remote = null, string? branch = null, bool rebase = false, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["pull", rebase ? "--rebase" : "--ff-only"];
        if (remote is not null)
        {
            arguments.AddRange(["--", ValidateValue(remote)]);
            if (branch is not null)
            {
                arguments.Add(ValidateValue(branch));
            }
        }
        else if (branch is not null)
        {
            throw new ArgumentException("A branch requires a remote.", nameof(branch));
        }

        return MutateAsync(arguments, cancellationToken);
    }

    public Task<GitResult> PushAsync(string? remote = null, string? branch = null, bool setUpstream = false, bool forceWithLease = false, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["push"];
        if (setUpstream)
        {
            arguments.Add("--set-upstream");
        }

        if (forceWithLease)
        {
            arguments.Add("--force-with-lease");
        }

        if (remote is not null)
        {
            arguments.AddRange(["--", ValidateValue(remote)]);
            if (branch is not null)
            {
                arguments.Add(ValidateValue(branch));
            }
        }
        else if (branch is not null)
        {
            throw new ArgumentException("A branch requires a remote.", nameof(branch));
        }

        return MutateAsync(arguments, cancellationToken);
    }

    public Task<GitResult> CreateTagAsync(string name, string? revision = null, string? message = null, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["tag"];
        if (message is not null)
        {
            arguments.AddRange(["--annotate", "--message", message]);
        }

        arguments.AddRange(["--", ValidateValue(name)]);
        if (revision is not null)
        {
            arguments.Add(ValidateValue(revision));
        }

        return MutateAsync(arguments, cancellationToken);
    }

    public Task<GitResult> DeleteTagAsync(string name, CancellationToken cancellationToken = default)
        => MutateAsync(["tag", "--delete", "--", ValidateValue(name)], cancellationToken);

    public async Task<IReadOnlyList<GitWorktree>> GetWorktreesAsync(CancellationToken cancellationToken = default)
        => GitOutputParser.ParseWorktrees((await ExecuteAsync(["worktree", "list", "--porcelain", "-z"], cancellationToken).ConfigureAwait(false)).StandardOutput);

    public async Task<GitResult> AddWorktreeAsync(string path, string revision, string? newBranch = null, CancellationToken cancellationToken = default)
    {
        GitResult result = (await CreateWorktreeAsync(new(path, revision, newBranch), cancellationToken).ConfigureAwait(false)).Result;
        if (!result.Success) { throw new GitCommandException(["worktree", "add"], result); }
        return result;
    }

    public async Task<GitResult> RemoveWorktreeAsync(string path, bool force = false, CancellationToken cancellationToken = default)
    {
        string full = System.IO.Path.GetFullPath(path, Path);
        GitRulesIdentity? identity = GitRulesIdentity.ReadPath(GitRulesFiles.Resolve(full).Path);
        WorktreeSnapshot selected = (await GetWorktreeSnapshotsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(tree => tree.Path == full || identity is not null && tree.Binding.Root.Identity == identity)
            ?? throw new ArgumentException("Select a registered worktree.", nameof(path));
        GitResult result = (await DeleteWorktreeAsync(selected, discardChanges: force, cancellationToken: cancellationToken).ConfigureAwait(false)).Result;
        if (!result.Success) { throw new GitCommandException(["worktree", "remove"], result); }
        return result;
    }

    public async Task<GitResult> PruneWorktreesAsync(CancellationToken cancellationToken = default)
    {
        GitResult result = await PruneWorktreesAsync(cancellationToken, outputReceived: null).ConfigureAwait(false);
        if (!result.Success) { throw new GitCommandException(["worktree", "prune"], result); }
        return result;
    }

    public async Task<IReadOnlyList<GitSubmodule>> GetSubmodulesAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(System.IO.Path.Combine(Path, ".gitmodules")))
        {
            return [];
        }

        GitResult moduleConfig = await ExecuteAsync(["config", "--file", ".gitmodules", "--null", "--get-regexp", "^submodule\\..*\\.(path|url)$"], cancellationToken, allowedExitCodes: [1]).ConfigureAwait(false);
        Dictionary<string, Dictionary<string, string>> modules = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> setting in ParseConfig(moduleConfig.StandardOutput))
        {
            int separator = setting.Key.LastIndexOf('.');
            string name = setting.Key[10..separator];
            if (!modules.TryGetValue(name, out Dictionary<string, string>? values))
            {
                values = new(StringComparer.Ordinal);
                modules.Add(name, values);
            }

            values[setting.Key[(separator + 1)..]] = setting.Value;
        }

        GitResult files = await ExecuteAsync(["ls-files", "--stage", "-z"], cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> expected = new(StringComparer.Ordinal);
        foreach (string entry in files.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = entry.IndexOf('\t');
            if (entry.StartsWith("160000 ", StringComparison.Ordinal) && tab >= 0)
            {
                expected[entry[(tab + 1)..]] = entry.Split(' ', 3)[1];
            }
        }

        List<GitSubmodule> result = [];
        foreach (KeyValuePair<string, Dictionary<string, string>> module in modules)
        {
            if (!module.Value.TryGetValue("path", out string? modulePath))
            {
                continue;
            }

            // The Windows list follows git submodule status, which is driven
            // by indexed gitlinks. Remove retains the last worktree .gitmodules.
            if (!expected.ContainsKey(modulePath)) { continue; }

            string fullPath = System.IO.Path.Combine(Path, ValidatePath(modulePath));
            bool initialized = File.Exists(System.IO.Path.Combine(fullPath, ".git")) || Directory.Exists(System.IO.Path.Combine(fullPath, ".git"));
            string? head = null;
            if (initialized)
            {
                head = await new GitRepository(fullPath, _runner).ResolveRevisionAsync("HEAD", cancellationToken).ConfigureAwait(false);
            }

            result.Add(new GitSubmodule(module.Key, modulePath, module.Value.GetValueOrDefault("url"), expected.GetValueOrDefault(modulePath), head, initialized));
        }

        return result;
    }

    public async Task<GitResult> AddSubmoduleAsync(string source, string path, CancellationToken cancellationToken = default)
    {
        GitResult result = await AddSubmoduleAsync(new GitSubmoduleAddOptions(source, path), cancellationToken).ConfigureAwait(false);
        if (!result.Success) { throw new GitCommandException(["submodule", "add", "--", source, path], result); }
        return result;
    }

    public Task<GitResult> UpdateSubmodulesAsync(bool recursive = true, CancellationToken cancellationToken = default)
        => MutateAsync(recursive ? ["submodule", "update", "--init", "--recursive"] : ["submodule", "update", "--init"], cancellationToken);

    public Task<GitResult> SyncSubmodulesAsync(CancellationToken cancellationToken = default)
        => MutateAsync(["submodule", "sync", "--recursive"], cancellationToken);

    public Task<GitResult> MergeAsync(string revision, bool noFastForward = false, CancellationToken cancellationToken = default)
        => MutateAsync(noFastForward ? ["merge", "--no-edit", "--no-ff", "--", ValidateValue(revision)] : ["merge", "--no-edit", "--", ValidateValue(revision)], cancellationToken);

    public Task<GitResult> CherryPickAsync(string revision, CancellationToken cancellationToken = default)
        => CherryPickCommittedAsync(revision, cancellationToken);

    public Task<GitResult> RebaseAsync(string revision, CancellationToken cancellationToken = default)
        => StartSimpleRebaseAsync(ValidateValue(revision), cancellationToken);

    public Task<GitResult> ManageOperationAsync(string operation, GitOperationAction action, CancellationToken cancellationToken = default)
    {
        if (operation is not ("merge" or "rebase" or "cherry-pick" or "revert"))
        {
            throw new ArgumentException("Unsupported sequenced operation.", nameof(operation));
        }

        if (operation == "merge" && action == GitOperationAction.Skip)
        {
            throw new ArgumentException("Merge does not support skip.", nameof(action));
        }

        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        if (operation == "rebase")
        {
            return ManageRebaseOperationAsync(action, cancellationToken);
        }

        return MutateAsync([operation, "--" + action.ToString().ToLowerInvariant()], cancellationToken);
    }

    public Task<GitResult> RevertAsync(string revision, CancellationToken cancellationToken = default)
        => RevertCommittedAsync(revision, cancellationToken);

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> GetConfigAsync(string? pattern = null, GitConfigScope? scope = null, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["config", "--null"];
        if (scope is not null)
        {
            arguments.Add("--" + scope.ToString()!.ToLowerInvariant());
        }

        arguments.AddRange(pattern is null ? ["--list"] : ["--get-regexp", pattern]);
        return ParseConfig((await ExecuteAsync(arguments, cancellationToken, allowedExitCodes: [1]).ConfigureAwait(false)).StandardOutput);
    }

    public Task<GitResult> SetConfigAsync(string key, string value, GitConfigScope scope = GitConfigScope.Local, CancellationToken cancellationToken = default)
        => MutateAsync(["config", "--" + scope.ToString().ToLowerInvariant(), "--replace-all", ValidateValue(key), value], cancellationToken);

    public Task<GitResult> UnsetConfigAsync(string key, GitConfigScope scope = GitConfigScope.Local, CancellationToken cancellationToken = default)
        => MutateAsync(["config", "--" + scope.ToString().ToLowerInvariant(), "--unset-all", ValidateValue(key)], cancellationToken, allowedExitCodes: [5]);

    private async Task<bool> HasHeadAsync(CancellationToken cancellationToken)
        => (await ExecuteAsync(["rev-parse", "--verify", "HEAD"], cancellationToken, allowedExitCodes: [1, 128]).ConfigureAwait(false)).Success;

    private async Task<GitResult> MutateAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? standardInput = null, int[]? allowedExitCodes = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteAsync(arguments, cancellationToken, standardInput, allowedExitCodes, environment).ConfigureAwait(false);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private async Task<GitResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? standardInput = null, int[]? allowedExitCodes = null,
        IReadOnlyDictionary<string, string?>? environment = null, Func<GitBinaryResult, GitOutputDecoding>? outputDecoder = null)
    {
        string? command = arguments.FirstOrDefault();
        bool worktreeRequired = command is "status" or "add" or "rm" or "restore" or "checkout" or "switch" or "commit" or "stash"
            or "merge" or "rebase" or "cherry-pick" or "revert" or "am" or "apply" or "clean" or "sparse-checkout" or "submodule"
            or "pull" or "mergetool" || command == "difftool" && !arguments.Contains("--no-index")
            || command == "reset" && !arguments.Contains("--soft");
        if (worktreeRequired)
        {
            await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
        }

        // Git stash builds internal magic pathspecs while removing untracked files. A global
        // literal-pathspec setting breaks that cleanup (notably for names containing newlines).
        // Enable literal handling only on commands that receive user pathspecs.
        string[] pathOptions = arguments.FirstOrDefault() is "add" or "reset" or "rm" or "diff" or "show" or "blame" or "restore" or "checkout" or "ls-files" or "ls-tree" or "log" or "grep" or "bisect"
            ? ["--literal-pathspecs"] : [];
        GitResult result = await _runner.RunAsync(Path, [.. GlobalOptions, .. pathOptions, .. arguments], cancellationToken, standardInput, environment, outputDecoder).ConfigureAwait(false);
        if (!result.Success && !(allowedExitCodes?.Contains(result.ExitCode) ?? false))
        {
            throw new GitCommandException(arguments, result);
        }

        return result;
    }

    private async Task<T> WithMutationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private string[] ValidatePaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] validated = paths.Select(ValidatePath).Distinct(StringComparer.Ordinal).ToArray();
        if (validated.Length == 0)
        {
            throw new ArgumentException("Select at least one pathname.", nameof(paths));
        }

        return validated;
    }

    private string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.Contains('\0'))
        {
            throw new ArgumentException("Pathnames cannot contain NUL.", nameof(path));
        }

        string fullPath = System.IO.Path.GetFullPath(path, Path);
        string relative = System.IO.Path.GetRelativePath(Path, fullPath);
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative))
        {
            throw new ArgumentException("Pathname is outside the repository.", nameof(path));
        }

        return relative;
    }

    private static string ValidateValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.StartsWith('-') || value.Contains('\0'))
        {
            throw new ArgumentException("Git names and revision expressions cannot begin with '-' or contain NUL.", nameof(value));
        }

        return value;
    }

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private static IReadOnlyList<KeyValuePair<string, string>> ParseConfig(string output)
        => output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(entry =>
        {
            int newline = entry.IndexOf('\n');
            return new KeyValuePair<string, string>(newline < 0 ? entry : entry[..newline], newline < 0 ? "" : entry[(newline + 1)..]);
        }).ToArray();
}

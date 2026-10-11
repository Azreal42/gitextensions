namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Opens a repository from its root, a descendant directory, a .git file/directory, or linked-worktree metadata.</summary>
    public static async Task<GitRepository> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        GitRunner runner = new();
        GitRepositoryInfo info = await DiscoverRepositoryAsync(path, runner, cancellationToken).ConfigureAwait(false);
        return new GitRepository(info.RootPath, runner) { Info = info };
    }

    /// <summary>Loads repository metadata for callers that construct a repository from an already known root.</summary>
    public async Task<GitRepositoryInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Info ??= await DiscoverRepositoryAsync(Path, _runner, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureWorktreeAsync(CancellationToken cancellationToken)
    {
        if ((await GetInfoAsync(cancellationToken).ConfigureAwait(false)).IsBare)
        {
            throw new InvalidOperationException("This operation requires a working tree. The selected repository is bare; create or open a worktree to edit files.");
        }
    }

    private static async Task<GitRepositoryInfo> DiscoverRepositoryAsync(string path, GitRunner runner, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        string directory = System.IO.Path.GetFullPath(path);
        if (File.Exists(directory))
        {
            if (System.IO.Path.GetFileName(directory) != ".git")
            {
                throw new ArgumentException("Select a repository directory or its .git file.", nameof(path));
            }

            directory = System.IO.Path.GetDirectoryName(directory)!;
        }

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Repository directory does not exist: {directory}");
        }

        string gitDirectory = ReadOutputLine((await RunAsync(directory, ["rev-parse", "--absolute-git-dir"]).ConfigureAwait(false)).StandardOutput);
        bool bare = ReadOutputLine((await RunAsync(directory, ["rev-parse", "--is-bare-repository"]).ConfigureAwait(false)).StandardOutput) == "true";
        string root = gitDirectory;
        if (!bare)
        {
            GitResult topLevel = await RunAsync(directory, ["rev-parse", "--show-toplevel"], [128]).ConfigureAwait(false);
            if (topLevel.Success)
            {
                root = ReadOutputLine(topLevel.StandardOutput);
            }
            else
            {
                List<string> candidates = [];
                // Linked-worktree administrative directories record the associated .git file.
                string backlink = System.IO.Path.Combine(gitDirectory, "gitdir");
                if (File.Exists(backlink))
                {
                    string gitFile = ReadOutputLine(await File.ReadAllTextAsync(backlink, cancellationToken).ConfigureAwait(false));
                    string? candidate = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(gitFile, gitDirectory));
                    if (candidate is not null)
                    {
                        candidates.Add(candidate);
                    }
                }

                GitResult worktree = await RunAsync(directory, ["config", "--path", "--null", "--get", "core.worktree"], [1]).ConfigureAwait(false);
                if (worktree.Success)
                {
                    string configured = worktree.StandardOutput.EndsWith('\0') ? worktree.StandardOutput[..^1] : worktree.StandardOutput;
                    candidates.Add(System.IO.Path.GetFullPath(configured, gitDirectory));
                }

                if (System.IO.Path.GetFileName(gitDirectory) == ".git" && System.IO.Path.GetDirectoryName(gitDirectory) is string parent)
                {
                    candidates.Add(parent);
                }

                root = "";
                foreach (string candidate in candidates.Distinct(StringComparer.Ordinal))
                {
                    if (!Directory.Exists(candidate))
                    {
                        continue;
                    }

                    GitResult matching = await RunAsync(candidate, ["rev-parse", "--absolute-git-dir"], [128]).ConfigureAwait(false);
                    if (!matching.Success || ReadOutputLine(matching.StandardOutput) != gitDirectory)
                    {
                        continue;
                    }

                    GitResult candidateRoot = await RunAsync(candidate, ["rev-parse", "--show-toplevel"], [128]).ConfigureAwait(false);
                    if (candidateRoot.Success)
                    {
                        root = ReadOutputLine(candidateRoot.StandardOutput);
                        break;
                    }
                }

                if (root.Length == 0)
                {
                    throw new InvalidOperationException("The Git metadata belongs to a repository with a working tree, but its root could not be found. Open the working tree directory.");
                }
            }
        }

        string commonDirectory = ReadOutputLine((await RunAsync(root, ["rev-parse", "--path-format=absolute", "--git-common-dir"]).ConfigureAwait(false)).StandardOutput);
        return new GitRepositoryInfo(System.IO.Path.GetFullPath(root), System.IO.Path.GetFullPath(gitDirectory), System.IO.Path.GetFullPath(commonDirectory), bare);

        async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, int[]? allowedExitCodes = null)
        {
            GitResult result = await runner.RunAsync(workingDirectory, [.. GlobalOptions, .. arguments], cancellationToken).ConfigureAwait(false);
            if (!result.Success && !(allowedExitCodes?.Contains(result.ExitCode) ?? false))
            {
                throw new GitCommandException(arguments, result);
            }

            return result;
        }
    }

    // Paths can themselves end in a newline. Remove precisely Git's one line terminator.
    private static string ReadOutputLine(string output) => output.EndsWith('\n') ? output[..^1] : output;
}

using System.Diagnostics;
using GitExtensions.Linux;

internal static class QuickReferenceScenarios
{
    private const string Literal = "topic/été;$()'quoted";
    private const string WindowsFormat = "--format=%(if)%(authordate)%(then)%(objectname) %(refname)%(else)%(*objectname) %(*refname)%(end)";
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("quick references match the native Windows format for branches annotated nested and non-commit tags while retaining raw identities", NativeTargetsAsync),
        ("quick references retain isolated remote HEAD and apply exactly the Windows duplicate HEAD rule", RemoteHeadsAsync),
        ("quick references have no item limit and work in bare and SHA256 repositories without changing index or configuration", BareAndUnboundedAsync),
        ("quick references validate identities cancel and drain their native read without taking a mutation lock", CancellationAsync),
    ];

    private static async Task NativeTargetsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository repository = f.Repository;
        string commit = await repository.ResolveRevisionAsync("HEAD");
        await Git(repository, "branch", Literal, commit); await Git(repository, "tag", "light", commit);
        await Git(repository, "tag", "-a", "annotated", "-m", "First annotated tag", commit);
        await Git(repository, "tag", "-a", "nested", "-m", "Nested annotated tag", "annotated");
        await Git(repository, "tag", "-a", "nested-three", "-m", "Third nested tag", "nested");
        string blob = (await Git(repository, "rev-parse", "HEAD:seed.txt")).StandardOutput.Trim();
        string tree = (await Git(repository, "rev-parse", "HEAD^{tree}")).StandardOutput.Trim();
        await Git(repository, "tag", "-a", "blob", "-m", "Blob tag", blob); await Git(repository, "tag", "-a", "tree", "-m", "Tree tag", tree);
        await Git(repository, "update-ref", "refs/notes/not-an-action", commit);
        await Git(repository, "update-ref", "refs/remotes/origin/topic", commit);
        f.Write("seed.txt", "Second\n"); await repository.StageAllAsync(); await repository.CommitAsync("Second");
        await Git(repository, "tag", "other", "HEAD");
        string before = await FileDigest(f.File(".git/index"));
        IReadOnlyList<GitReference> actual = await repository.GetQuickActionReferencesAsync(commit);
        GitResult native = await Git(repository, "--no-optional-locks", "for-each-ref", WindowsFormat, "refs/heads/", "refs/remotes/", "refs/tags/");
        string[] expected = native.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(commit + " ", StringComparison.Ordinal)).Select(line => line[(commit.Length + 1)..])
            .Select(name => name.EndsWith("^{}", StringComparison.Ordinal) ? name[..^3] : name).Order(StringComparer.Ordinal).ToArray();
        Check(actual.Select(reference => reference.Name).Order(StringComparer.Ordinal).SequenceEqual(expected), "The complete native Windows format, including the installed Git's nested-tag semantics, is the oracle.");
        foreach (GitReference reference in actual)
        {
            string raw = (await Git(repository, "rev-parse", "--verify", "--end-of-options", reference.Name)).StandardOutput.Trim();
            Check(reference.Hash == raw, "Each reference retains its direct object hash instead of the peeled commit.");
        }
        Check(actual.Single(reference => reference.Name == "refs/tags/annotated").Hash != commit, "Annotated tags retain their own identity.");
        Check(actual.All(reference => reference.RevisionHash == commit), "Quick references carry the commit attachment separately from native ref identities.");
        Check(actual.Any(reference => reference.Name == "refs/heads/" + Literal) && actual.Any(reference => reference.Name == "refs/remotes/origin/topic"), "Local, remote, Unicode and shell-metacharacter reference names remain literal.");
        Check(actual.All(reference => reference.Name is not ("refs/tags/blob" or "refs/tags/tree" or "refs/tags/other" or "refs/notes/not-an-action")), "Only references attached to the selected commit and the three action namespaces are returned.");
        Check((await repository.GetQuickActionReferencesAsync(blob)).Count == 0 && (await repository.GetQuickActionReferencesAsync(tree)).Count == 0, "Non-commit targets never become commit actions.");
        Check(await FileDigest(f.File(".git/index")) == before && (await repository.GetStatusAsync()).Changes.Count == 0, "Reading actions does not stage or change files.");

        await Git(repository, "tag", "tags/release", commit);
        await Git(repository, "tag", "light-blob", blob);
        await repository.SetConfigAsync("remote.origin.url", repository.Path);
        await repository.SetConfigAsync("remote.origin.color", "#123456");
        IReadOnlyList<GitReference> all = await repository.GetReferencesAsync();
        const string targetsFormat = "--format=%(refname)%00%(if)%(authordate)%(then)%(objectname)%(else)%(*objectname)%(end)";
        Dictionary<string, string?> targets = (await Git(repository, "for-each-ref", targetsFormat)).StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\0'))
            .ToDictionary(fields => fields[0], fields => fields[1].Length == 0 ? null : fields[1], StringComparer.Ordinal);
        Check(all.Count == targets.Count, "General references retain every namespace, including notes and non-commit tags.");
        foreach (GitReference reference in all)
        {
            string raw = (await Git(repository, "rev-parse", "--verify", "--end-of-options", reference.Name)).StandardOutput.Trim();
            Check(reference.Hash == raw && reference.Sha1Id?.ToString() == raw, "General references preserve the raw identity and SHA1 projection.");
            Check(reference.RevisionHash == targets[reference.Name], "Presentation targets match the native Windows expression, including nested/non-commit cases.");
        }
        Check(all.Single(reference => reference.Name == "refs/tags/annotated").RevisionHash == commit, "An annotated tag is attached to its commit.");
        Check(all.Single(reference => reference.Name == "refs/tags/tags/release").ShortName == "tags/release", "A literal tags/ prefix is not stripped from a name.");
        Check(all.Single(reference => reference.Name == "refs/remotes/origin/topic") is { RemoteColor: "#123456", RevisionHash: not null }, "Remote color enrichment retains target metadata.");
        GitReference snapshot = all.Single(reference => reference.Name == "refs/tags/annotated");
        await Git(repository, "tag", "-f", "-a", "annotated", "-m", "Changed metadata, same commit", commit);
        GitReference replaced = (await repository.GetQuickActionReferencesAsync(commit)).Single(reference => reference.Name == snapshot.Name);
        Check(snapshot.RevisionHash == replaced.RevisionHash && snapshot.Hash != replaced.Hash, "A tag rewritten at the same commit still changes the guarded native identity.");
    }

    private static async Task RemoteHeadsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository repository = f.Repository;
        string commit = await repository.ResolveRevisionAsync("HEAD");
        await Git(repository, "update-ref", "refs/remotes/origin/topic", commit);
        await Git(repository, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/topic");
        await Git(repository, "symbolic-ref", "refs/remotes/isolated/HEAD", "refs/heads/main");
        await Git(repository, "update-ref", "refs/remotes/ori/gin/topic", commit);
        await Git(repository, "symbolic-ref", "refs/remotes/ori/gin/HEAD", "refs/remotes/ori/gin/topic");
        await Git(repository, "update-ref", "refs/remotes/HEAD", commit);
        await repository.SetConfigAsync("branch.main.remote", "."); await repository.SetConfigAsync("branch.main.merge", "refs/heads/main");
        IReadOnlyList<GitReference> references = await repository.GetQuickActionReferencesAsync(commit);
        Check(references.All(reference => reference.Name != "refs/remotes/origin/HEAD"), "A default remote HEAD is omitted only when that remote already has a reference on the same commit.");
        Check(references.Single(reference => reference.Name == "refs/remotes/isolated/HEAD").SymbolicTarget == "refs/heads/main", "An isolated symbolic HEAD remains selectable with its native target.");
        Check(references.Any(reference => reference.Name == "refs/remotes/ori/gin/HEAD") && references.Any(reference => reference.Name == "refs/remotes/HEAD"), "Slash-named and non-default HEAD references are not mistaken for the one-segment default HEAD.");
        GitReference main = references.Single(reference => reference.Name == "refs/heads/main");
        Check(main.IsCurrent && main.Upstream == "main", "The read retains current branch and upstream metadata; action-specific filtering belongs to the UI.");
        f.Write("seed.txt", "Later\n"); await repository.StageAllAsync(); await repository.CommitAsync("Later");
        await Git(repository, "update-ref", "--no-deref", "refs/remotes/origin/HEAD", "HEAD");
        IReadOnlyList<GitReference> later = await repository.GetQuickActionReferencesAsync(await repository.ResolveRevisionAsync("HEAD"));
        Check(later.Any(reference => reference.Name == "refs/remotes/origin/HEAD"), "A default HEAD with no same-target remote branch is retained even if other branches of the remote exist.");
    }

    private static async Task BareAndUnboundedAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository repository = f.Repository;
        string commit = await repository.ResolveRevisionAsync("HEAD");
        string commands = string.Concat(Enumerable.Range(0, 1101).Select(index => $"create refs/tags/bulk-{index:0000} {commit}\n"));
        GitResult update = await new GitRunner().RunAsync(repository.Path, ["update-ref", "--stdin"], standardInput: commands);
        Check(update.Success, update.StandardError);
        string config = await FileDigest(f.File(".git/config")), indexBefore = await FileDigest(f.File(".git/index"));
        IReadOnlyList<GitReference> references = await repository.GetQuickActionReferencesAsync(commit);
        Check(references.Count == 1102, "The selector reads every matching reference without the GoToCommit list's 1000-item limit.");
        Check(await FileDigest(f.File(".git/config")) == config && await FileDigest(f.File(".git/index")) == indexBefore, "Action discovery preserves configuration and index bytes.");
        string barePath = Path.Combine(f.Root, "bare repository.git"); await Git(repository, "clone", "--bare", repository.Path, barePath);
        Check((await new GitRepository(barePath).GetQuickActionReferencesAsync(commit)).Count == references.Count, "Action discovery works without a worktree.");
        Check((await new GitRepository(barePath).GetReferencesAsync()).All(reference => reference.RevisionHash == commit), "Bare display references retain their commit attachments.");
        string orphan = (await Git(repository, "commit-tree", "HEAD^{tree}", "-p", "HEAD", "-m", "Unreferenced")).StandardOutput.Trim();
        Check((await repository.GetQuickActionReferencesAsync(orphan)).Count == 0, "An existing commit with no reference has no action candidates.");
        string shaPath = Path.Combine(f.Root, "sha256"); Directory.CreateDirectory(shaPath); GitRepository sha = new(shaPath);
        await Git(sha, "init", "--object-format=sha256", "--initial-branch=main"); await RepositoryFixture.ConfigureAsync(sha);
        await Git(sha, "commit", "--allow-empty", "-m", "SHA256"); string shaCommit = await sha.ResolveRevisionAsync("HEAD");
        await Git(sha, "tag", "-a", "sha-tag", "-m", "SHA256 tag");
        IReadOnlyList<GitReference> shaReferences = await sha.GetQuickActionReferencesAsync(shaCommit.ToUpperInvariant());
        Check(shaCommit.Length == 64 && shaReferences.Count == 2 && shaReferences.All(reference => reference.Hash.Length == 64), "Full SHA256 and uppercase input identities preserve native reference identities.");
        GitReference shaTag = (await sha.GetReferencesAsync()).Single(reference => reference.IsTag);
        Check(shaTag.RevisionHash == shaCommit && shaTag.Hash != shaCommit && shaTag.Sha1Id is null, "SHA256 tags keep separate raw and display identities without a false SHA1 projection.");
    }

    private static async Task CancellationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string commit = await f.Repository.ResolveRevisionAsync("HEAD");
        foreach (string invalid in new[] { "HEAD", commit[..12], "-option", new string('z', 40), commit + "\n" })
        { await Expect<ArgumentException>(() => f.Repository.GetQuickActionReferencesAsync(invalid)); }
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Expect<OperationCanceledException>(() => f.Repository.GetQuickActionReferencesAsync(commit, cancelled.Token));
        Check((await f.Repository.GetQuickActionReferencesAsync(new string('0', 40))).Count == 0, "A complete but absent commit identity has no matching actions.");
        if (!OperatingSystem.IsLinux()) { return; }
        string wrapper = Path.Combine(f.Root, "blocked git"), pidFile = Path.Combine(f.Root, "reader.pid");
        string quoted = "'" + pidFile.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nfor arg in \"$@\"; do if [ \"$arg\" = for-each-ref ]; then printf '%s' \"$$\" > " + quoted + "; sleep 60; fi; done\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository guarded = new(f.Repository.Path, new GitRunner(wrapper)); using CancellationTokenSource lifetime = new();
        Task<IReadOnlyList<GitReference>> pending = guarded.GetQuickActionReferencesAsync(commit, lifetime.Token);
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            while ((!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0) && watch.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(10); }
            Check(File.Exists(pidFile), "The cancellable native reference read is genuinely waiting.");
            int pid = int.Parse(await File.ReadAllTextAsync(pidFile));
            // A normal repository mutation can finish while this read is blocked: the helper owns no mutation lock.
            await guarded.SetConfigAsync("quickrefs.probe", "available").WaitAsync(TimeSpan.FromSeconds(3));
            lifetime.Cancel(); await Expect<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Check(!Alive(pid), "Cancelling discovery drains and terminates its native reader.");
            Check((await f.Repository.GetQuickActionReferencesAsync(commit)).Single().IsCurrent, "The repository remains usable after cancellation.");
        }
        finally { lifetime.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
    }

    private static async Task<GitResult> Git(GitRepository repository, params string[] arguments)
    {
        GitResult result = await new GitRunner().RunAsync(repository.Path, arguments);
        Check(result.Success, string.Join(' ', arguments) + "\n" + result.StandardError); return result;
    }
    private static async Task<string> FileDigest(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static bool Alive(int pid)
    { try { string stat = File.ReadAllText($"/proc/{pid}/stat"); return stat[(stat.LastIndexOf(')') + 2)..].First() is not ('Z' or 'X'); } catch (IOException) { return false; } }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}

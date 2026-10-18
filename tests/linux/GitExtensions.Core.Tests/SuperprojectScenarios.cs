using System.Text;
using System.Text.Json;
using GitExtensions.Linux;

internal static class SuperprojectScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("superproject discovery never promotes an unregistered containing repository or bare repository", NoAssociationAsync),
        ("superproject discovery and native staging preserve literal Unicode TAB LF and option-like module paths", LiteralAsync),
        ("superproject staging reads the final child commit after commit amend and detached checkout", FinalHeadAsync),
        ("superproject staging uses the real final HEAD after rebase and successful native push", RebasePushAsync),
        ("superproject staging permits unrelated parent index and registration edits and preserves split indexes", UnrelatedAsync),
        ("superproject staging refuses stale selected registration and selected gitlink without reverting child commits", StaleAsync),
        ("superproject discovery rejects duplicate registrations and ordinary indexed files at the module path", InvalidAssociationAsync),
        ("superproject discovery and staging support old-form internal git directories", OldFormAsync),
        ("superproject discovery and staging use the linked parent worktree index rather than the main index", LinkedAsync),
        ("superproject discovery binds a registered linked child worktree and stages its own final HEAD", LinkedChildAsync),
        ("superproject nested staging touches only the immediate parent gitlink", NestedAsync),
        ("superproject update-index adds a genuinely registered gitlink absent from the parent index", AbsentIndexAsync),
        ("superproject unmerged gitlinks retain native update-index behavior and diagnostic readback", UnmergedAsync),
        ("superproject native index lock refusal is visible and never removes another actor lock", LockAsync),
        ("superproject native failure after actual staging retains partial effects and raw diagnostics", PartialAsync),
        ("superproject success without the requested gitlink readback is never reported verified", ReadbackAsync),
        ("superproject readback command failure retains the successful native staging effect and diagnostic", ReadbackFailureAsync),
        ("superproject staging retains the snapshot injected executable and captured environment", ContextAsync),
        ("superproject precancellation and in-flight cancellation drain the witnessed native writer and retain actual effects", CancellationAsync),
        ("superproject staging rejects another child and a replaced child root before writing", ReplacedAsync)
    ];

    private static async Task NoAssociationAsync()
    {
        using RepositoryFixture standalone = await RepositoryFixture.CreateAsync();
        Check(await standalone.Repository.GetSuperprojectSnapshotAsync() is null, "An ordinary repository has no fabricated parent.");
        GitRepository nested = await GitRepository.InitAsync(standalone.File("unregistered"));
        await RepositoryFixture.ConfigureAsync(nested); await nested.CommitAsync(new GitCommitOptions("Independent", AllowEmpty: true));
        standalone.Write(".gitmodules", "[submodule \"other\"]\n path = elsewhere\n url = nowhere\n");
        Check(await nested.GetSuperprojectSnapshotAsync() is null, "A containing Git root and unrelated registration do not make this repository a submodule.");
        GitRepository bare = await GitRepository.InitAsync(Path.Combine(standalone.Root, "bare.git"), bare: true);
        Check(await bare.GetSuperprojectSnapshotAsync() is null, "A bare repository has no parent gitlink working tree.");

        string fakeMarker = Path.Combine(standalone.Root, ".git"), fakeModules = Path.Combine(standalone.Root, ".gitmodules");
        Directory.CreateDirectory(fakeMarker);
        await File.WriteAllTextAsync(fakeModules, "[submodule \"other\"]\n path = elsewhere\n url = nowhere\n");
        GitResult invalidParent = await Native(standalone.Root, "rev-parse", "--absolute-git-dir");
        Check(!invalidParent.Success && !string.IsNullOrWhiteSpace(invalidParent.StandardError), "The ancestor fixture really has invalid Git metadata, independent of the child API.");
        string childHead = await standalone.Repository.ResolveRevisionAsync("HEAD");
        byte[] childIndex = await File.ReadAllBytesAsync(standalone.File(".git/index"));
        Check(await standalone.Repository.GetSuperprojectSnapshotAsync() is null,
            "An unrelated registration beneath invalid ancestor Git metadata does not block an actual independent child repository.");
        await File.WriteAllTextAsync(fakeModules, "[submodule \"declared\"]\n path = repository\n url = nowhere\n");
        GitCommandException declared = await Expect<GitCommandException>(() => standalone.Repository.GetSuperprojectSnapshotAsync());
        Check(declared.Arguments.SequenceEqual(new[] { "rev-parse", "--absolute-git-dir" })
            && declared.Result.ExitCode == invalidParent.ExitCode && declared.Result.StandardError == invalidParent.StandardError,
            "A declared association to corrupt parent metadata retains the original native discovery failure and stderr.");
        await File.WriteAllTextAsync(fakeModules, "[submodule broken\n");
        GitCommandException malformed = await Expect<GitCommandException>(() => standalone.Repository.GetSuperprojectSnapshotAsync());
        Check(malformed.Arguments[0] == "config" && !malformed.Result.Success && !string.IsNullOrWhiteSpace(malformed.Result.StandardError),
            "An invalid registration config is reported, rather than hidden as an unrelated parent.");
        Check(await standalone.Repository.ResolveRevisionAsync("HEAD") == childHead
            && Bytes(childIndex, await File.ReadAllBytesAsync(standalone.File(".git/index"))),
            "Independent-child discovery and visible association errors never stage or rewrite the child repository.");
    }

    private static async Task LiteralAsync()
    {
        foreach (string path in new[] { "-module [*] Ω", "mods/été [a]*\tand\nline" })
        {
            using Fixture f = await Fixture.CreateAsync(path); Directory.CreateDirectory(f.ChildFile("descendant"));
            GitRepository descendant = new(f.ChildFile("descendant"));
            GitSuperprojectSnapshot snapshot = await Required(descendant);
            Check(snapshot.ChildRoot == f.Child.Path && snapshot.SuperprojectRoot == f.Parent.Path && snapshot.SubmodulePath == path
                && snapshot.IndexHead == await f.Child.ResolveRevisionAsync("HEAD"), "Only the exact registration and distinct root are exposed from a descendant.");
            await f.AdvanceAsync(); GitSuperprojectStageResult result = await descendant.StageInSuperprojectAsync(snapshot);
            Check(result.Result.Success && result.Verified && result.Error is null && result.StagedHead == await f.Child.ResolveRevisionAsync("HEAD"),
                "Literal wildcard, leading dash, Unicode, TAB and LF survive native update-index and NUL index readback.");
            Check((await f.IndexEntries()).Length == 1 && (await f.IndexEntries())[0].Contains(" 0\t" + path, StringComparison.Ordinal), "No pathspec expansion or sibling staging occurs.");
        }
    }

    private static async Task FinalHeadAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSuperprojectSnapshot before = await Required(f.Child);
        string parentHead = await f.Parent.ResolveRevisionAsync("HEAD");
        await f.AdvanceAsync(); string committed = await f.Child.ResolveRevisionAsync("HEAD");
        await f.Child.CommitAsync(new GitCommitOptions("Rewritten final child", Amend: true, AllowEmpty: true)); string final = await f.Child.ResolveRevisionAsync("HEAD");
        Check(committed != final, "A real amend supplies a different post-commit/recovery HEAD.");
        await f.Child.CheckoutAsync(final, detached: true);
        GitSuperprojectStageResult result = await f.Child.StageInSuperprojectAsync(before);
        Check(result.Verified && result.ChildHead == final && result.StagedHead == final && before.IndexHead != final
            && await f.Parent.ResolveRevisionAsync("HEAD") == parentHead, "The old child HEAD is not a stale guard, detached final commits are valid, and no parent commit is made.");
    }

    private static async Task UnrelatedAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSuperprojectSnapshot selected = await Required(f.Child);
        await f.AdvanceAsync(); f.Root.Write("other.txt", "unrelated staged content\n"); await f.Parent.StageAsync(["other.txt"]);
        await f.Parent.RunAsync(["update-index", "--split-index"]);
        await File.AppendAllTextAsync(f.Root.File(".gitmodules"), "\n[submodule \"unrelated\"]\n path = unused\n url = untouched\n");
        byte[] modules = await File.ReadAllBytesAsync(f.Root.File(".gitmodules")); string other = await f.Text("rev-parse", ":other.txt");
        GitSuperprojectStageResult result = await f.Child.StageInSuperprojectAsync(selected);
        Check(result.Verified && await f.Text("rev-parse", ":other.txt") == other && File.ReadAllText(f.Root.File("other.txt")) == "unrelated staged content\n"
            && Bytes(modules, await File.ReadAllBytesAsync(f.Root.File(".gitmodules"))), "Unrelated peer index/config/worktree changes are retained without an index-wide stale refusal.");
    }

    private static async Task RebasePushAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSuperprojectSnapshot snapshot = await Required(f.Child); await f.AdvanceAsync();
        string local = await f.Child.ResolveRevisionAsync("HEAD"), parent = await f.Parent.ResolveRevisionAsync("HEAD");
        f.Source.Write("upstream.txt", "independent upstream change\n"); await f.Source.Repository.StageAllAsync(); await f.Source.Repository.CommitAsync("Upstream advance");
        GitResult fetch = await Native(f.Child.Path, "-c", "protocol.file.allow=always", "fetch", "--no-tags", "origin");
        GitResult rebase = await Native(f.Child.Path, "rebase", "origin/main"); string final = await f.Child.ResolveRevisionAsync("HEAD");
        Check(fetch.Success && rebase.Success && final != local, "A real nonconflicting native rebase changes the successfully committed child HEAD.");
        GitRepository remote = await GitRepository.InitAsync(f.Output("published.git"), bare: true);
        GitResult push = await Native(f.Child.Path, "push", "--", remote.Path, "HEAD:refs/heads/published");
        Check(push.Success && await remote.ResolveRevisionAsync("refs/heads/published") == final, "The actual final rebased child commit has been pushed before parent staging.");
        GitSuperprojectStageResult result = await f.Child.StageInSuperprojectAsync(snapshot);
        Check(result.Verified && result.ChildHead == final && result.StagedHead == final && await f.Parent.ResolveRevisionAsync("HEAD") == parent,
            "The association captured before commit survives real rebase/push, stages their final HEAD exactly once, and never commits the parent.");
    }

    private static async Task StaleAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSuperprojectSnapshot selected = await Required(f.Child); await f.AdvanceAsync();
        string childHead = await f.Child.ResolveRevisionAsync("HEAD");
        await f.Native("config", "--file", ".gitmodules", "submodule.module.path", "elsewhere"); byte[] index = await f.IndexBytes();
        await Expect<InvalidOperationException>(() => f.Child.StageInSuperprojectAsync(selected));
        Check(Bytes(index, await f.IndexBytes()) && await f.Child.ResolveRevisionAsync("HEAD") == childHead, "Stale registration writes nothing and does not roll back the successful child commit.");
        await f.Native("config", "--file", ".gitmodules", "submodule.module.path", f.Path);
        selected = await Required(f.Child); await f.Parent.StageAsync([f.Path]); index = await f.IndexBytes();
        await Expect<InvalidOperationException>(() => f.Child.StageInSuperprojectAsync(selected));
        Check(Bytes(index, await f.IndexBytes()), "A concurrently restaged selected gitlink is refused even if it now points at the desired child.");
    }

    private static async Task InvalidAssociationAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Native("config", "--file", ".gitmodules", "submodule.duplicate.path", f.Path);
        await Expect<InvalidOperationException>(() => f.Child.GetSuperprojectSnapshotAsync());
        await f.Native("config", "--file", ".gitmodules", "--remove-section", "submodule.duplicate");
        await f.Native("config", "--file", ".gitmodules", "--add", "submodule.module.path", f.Path);
        await Expect<InvalidOperationException>(() => f.Child.GetSuperprojectSnapshotAsync());
        await f.Native("config", "--file", ".gitmodules", "--replace-all", "submodule.module.path", f.Path);
        string blob = await f.Text("rev-parse", "HEAD:seed.txt"); await f.Native("update-index", "--add", "--cacheinfo", "100644," + blob + "," + f.Path);
        byte[] index = await f.IndexBytes(); await Expect<InvalidOperationException>(() => f.Child.GetSuperprojectSnapshotAsync());
        Check(Bytes(index, await f.IndexBytes()), "A registered directory cannot replace ordinary indexed file data under the guise of gitlink staging.");
    }

    private static async Task OldFormAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitRepositoryInfo info = await f.Child.GetInfoAsync();
        await f.Child.RunAsync(["config", "--unset", "core.worktree"]); File.Delete(f.ChildFile(".git")); Directory.Move(info.GitDirectory, f.ChildFile(".git"));
        GitRepository old = new(f.Child.Path); GitSuperprojectSnapshot snapshot = await Required(old);
        await f.AdvanceAsync(old); GitSuperprojectStageResult result = await old.StageInSuperprojectAsync(snapshot);
        Check(result.Verified && snapshot.SuperprojectRoot == f.Parent.Path && Directory.Exists(f.ChildFile(".git")), "Ancestor registration supports the real old-form internal Git directory without relying on an absorbed-module hint.");
    }

    private static async Task LinkedAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string linked = Path.Combine(f.Root.Root, "linked parent Ω");
        await f.Parent.AddWorktreeAsync(linked, "HEAD", "linked-parent");
        await Native(linked, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", f.Path);
        GitRepository child = new(Path.Combine(linked, f.Path)); await RepositoryFixture.ConfigureAsync(child);
        GitSuperprojectSnapshot snapshot = await Required(child); byte[] mainIndex = await f.IndexBytes();
        await f.AdvanceAsync(child); GitSuperprojectStageResult result = await child.StageInSuperprojectAsync(snapshot);
        Check(snapshot.SuperprojectRoot == linked && result.Verified && Bytes(mainIndex, await f.IndexBytes())
            && Line((await Native(linked, "rev-parse", ":" + f.Path)).StandardOutput) == result.ChildHead,
            "The linked parent's own index is staged; shared common metadata never selects the main worktree index.");
    }

    private static async Task NestedAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); const string inner = "nested/inner Ω";
        await Native(f.Child.Path, "-c", "protocol.file.allow=always", "submodule", "add", "--name", "inner", "--", f.Source.Repository.Path, inner);
        await f.Child.CommitAsync("Register inner"); GitRepository child = new(f.ChildFile(inner)); await RepositoryFixture.ConfigureAsync(child);
        GitSuperprojectSnapshot snapshot = await Required(child); byte[] outerIndex = await f.IndexBytes(); string parentHead = await f.Child.ResolveRevisionAsync("HEAD");
        await f.AdvanceAsync(child); GitSuperprojectStageResult result = await child.StageInSuperprojectAsync(snapshot);
        Check(result.Verified && snapshot.SuperprojectRoot == f.Child.Path && snapshot.SubmodulePath == inner
            && Bytes(outerIndex, await f.IndexBytes()) && await f.Child.ResolveRevisionAsync("HEAD") == parentHead,
            "Only the immediate registered superproject gitlink changes; neither ancestor index nor parent HEAD is committed.");
    }

    private static async Task LinkedChildAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); const string path = "linked-child Ω";
        // Isolate this API from the existing managed worktree source-registration refusal for an absorbed module.
        // The native worktree and its actual roots/common directory are verified before the API under test.
        string target = f.Root.File(path);
        GitResult before = await Native(f.Child.Path, "worktree", "list", "--porcelain", "-z");
        GitResult added = await Native(f.Child.Path, "worktree", "add", "-b", "linked-child", "--", target, "HEAD");
        Check(added.Success, "Native linked-child worktree fixture failed: " + added.StandardError);
        GitRepository child = await GitRepository.OpenAsync(target); await RepositoryFixture.ConfigureAsync(child);
        GitRepositoryInfo childInfo = await child.GetInfoAsync(), sourceInfo = await f.Child.GetInfoAsync();
        GitResult listed = await Native(f.Child.Path, "worktree", "list", "--porcelain", "-z");
        GitResult nativeRoot = await Native(target, "rev-parse", "--show-toplevel");
        Check(before.Success && listed.Success && nativeRoot.Success && Line(nativeRoot.StandardOutput) == target
            && listed.StandardOutput.Split('\0').Contains("worktree " + target, StringComparer.Ordinal)
            && childInfo.RootPath == target && childInfo.CommonDirectory == sourceInfo.CommonDirectory
            && childInfo.GitDirectory != childInfo.CommonDirectory && File.Exists(System.IO.Path.Combine(target, ".git")),
            "Native Git must establish a distinct real linked child worktree sharing only its source common directory.");
        Console.WriteLine("FIXTURE linked-child native " + JsonSerializer.Serialize(new { source = sourceInfo.RootPath, sourceGit = sourceInfo.GitDirectory,
            target = childInfo.RootPath, git = childInfo.GitDirectory, common = childInfo.CommonDirectory,
            before = before.StandardOutput, after = listed.StandardOutput, actual_native_add_exit = added.ExitCode }));
        await f.Native("config", "--file", ".gitmodules", "submodule.linked.path", path);
        await f.Native("config", "--file", ".gitmodules", "submodule.linked.url", f.Source.Repository.Path);
        await f.Native("update-index", "--add", "--", path); await f.Parent.StageAsync([".gitmodules"]);
        GitSuperprojectSnapshot snapshot = await Required(child); string mainHead = await f.Child.ResolveRevisionAsync("HEAD");
        await f.AdvanceAsync(child); GitSuperprojectStageResult result = await child.StageInSuperprojectAsync(snapshot);
        Check(result.Verified && snapshot.SubmodulePath == path && result.ChildHead != mainHead
            && await f.Text("rev-parse", ":" + f.Path) == mainHead && await f.Child.ResolveRevisionAsync("HEAD") == mainHead,
            "A registered linked child uses its actual worktree HEAD and leaves its main checkout and other parent gitlink intact.");
    }

    private static async Task AbsentIndexAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.Native("update-index", "--force-remove", "--", f.Path);
        GitSuperprojectSnapshot snapshot = await Required(f.Child); Check(snapshot.IndexHead is null, "The genuine registration remains while its indexed gitlink is removed.");
        await f.AdvanceAsync(); GitSuperprojectStageResult result = await f.Child.StageInSuperprojectAsync(snapshot);
        Check(result.Verified && result.StagedHead == await f.Child.ResolveRevisionAsync("HEAD"), "Windows update-index --add can re-add the registered current child without staging .gitmodules or any directory contents.");
    }

    private static async Task UnmergedAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string old = await f.Child.ResolveRevisionAsync("HEAD"); await f.AdvanceAsync(); string current = await f.Child.ResolveRevisionAsync("HEAD");
        await f.Native("update-index", "--force-remove", "--", f.Path);
        GitResult setup = await new GitRunner().RunAsync(f.Parent.Path, ["update-index", "--index-info"], standardInput:
            "160000 " + old + " 1\t" + f.Path + "\n160000 " + current + " 2\t" + f.Path + "\n160000 " + old + " 3\t" + f.Path + "\n");
        Check(setup.Success, "Prepare actual unmerged gitlink stages."); byte[] before = await f.IndexBytes();
        GitSuperprojectSnapshot snapshot = await Required(f.Child); GitResult oracle = await f.Native("update-index", "--add", "--", f.Path);
        string[] expected = await f.IndexEntries(); await File.WriteAllBytesAsync(f.Root.File(".git/index"), before);
        GitSuperprojectStageResult actual = await f.Child.StageInSuperprojectAsync(snapshot);
        Check(actual.Result.ExitCode == oracle.ExitCode && (await f.IndexEntries()).SequenceEqual(expected)
            && actual.Verified == (oracle.Success && expected.Length == 1 && expected[0].StartsWith("160000 " + current + " 0\t", StringComparison.Ordinal)),
            "The normal staging API uses the real Windows native command for an unmerged gitlink, with honest verification and no conflict-helper shortcut.");
    }

    private static async Task LockAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSuperprojectSnapshot snapshot = await Required(f.Child); await f.AdvanceAsync(); byte[] before = await f.IndexBytes();
        string file = f.Root.File(".git/index.lock"); await File.WriteAllTextAsync(file, "another actor\n");
        try
        {
            GitSuperprojectStageResult result = await f.Child.StageInSuperprojectAsync(snapshot);
            Check(!result.Result.Success && !result.Verified && result.Error?.Contains("index.lock", StringComparison.Ordinal) == true
                && File.ReadAllText(file) == "another actor\n" && Bytes(before, await f.IndexBytes()), "Native lock diagnostics and the other actor's lock/data survive unchanged.");
        }
        finally { File.Delete(file); }
    }

    private static async Task PartialAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); string journal = f.Output("partial.jsonl");
        GitRepository child = await Wrapped(f, journal, "if 'update-index' in a and '--add' in a:\n r=subprocess.run(['/usr/bin/git']+a)\n if r.returncode:sys.exit(r.returncode)\n sys.stderr.write('owned partial staging failure\\n');sys.exit(17)\n");
        GitSuperprojectSnapshot snapshot = await Required(child); await f.AdvanceAsync(); Pipes pipes = new();
        GitSuperprojectStageResult result = await child.StageInSuperprojectAsync(snapshot, outputReceived: pipes.Receive);
        Check(result.Result.ExitCode == 17 && !result.Verified && result.StagedHead == result.ChildHead && result.Error?.Contains("owned partial", StringComparison.Ordinal) == true
            && Line((await f.Native("rev-parse", ":" + f.Path)).StandardOutput) == result.ChildHead && Encoding.UTF8.GetString(pipes.Error).Contains("owned partial", StringComparison.Ordinal),
            "A real updated index is retained when its owned native writer later returns failure; raw diagnostics remain visible and success is not fabricated.");
        string[] args = Arguments(journal).Single(row => row.Contains("update-index"));
        Check(args.TakeLast(4).SequenceEqual(["update-index", "--add", "--", f.Path]) && !Arguments(journal).Any(row => row.Contains("add") || row.Contains("commit") || row.Contains("push")),
            "The only writer is one targeted Windows update-index --add, without StageAll, commit or push.");
    }

    private static async Task ReadbackAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); string old = await f.Child.ResolveRevisionAsync("HEAD");
        GitRepository child = await Wrapped(f, f.Output("mismatch.jsonl"), "if 'update-index' in a and '--add' in a:\n r=subprocess.run(['/usr/bin/git']+a)\n if r.returncode:sys.exit(r.returncode)\n sys.exit(subprocess.run(['/usr/bin/git','update-index','--cacheinfo'," + JsonSerializer.Serialize("160000," + old + "," + f.Path) + "]).returncode)\n");
        GitSuperprojectSnapshot snapshot = await Required(child); await f.AdvanceAsync(); GitSuperprojectStageResult result = await child.StageInSuperprojectAsync(snapshot);
        Check(result.Result.Success && !result.Verified && result.StagedHead == old && result.ChildHead != old && result.Error is not null,
            "A real exit0 with a mismatched parent gitlink is not verified or silently corrected by a second writer.");
    }

    private static async Task ContextAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); string journal = f.Output("context.jsonl"), marker = "GE_SUPERPROJECT_PRIVATE_CONTEXT";
        string? previous = Environment.GetEnvironmentVariable(marker); Environment.SetEnvironmentVariable(marker, "captured");
        try
        {
            GitRepository child = await Wrapped(f, journal); GitSuperprojectSnapshot snapshot = await Required(child); await f.AdvanceAsync();
            Environment.SetEnvironmentVariable(marker, "later"); int before = File.ReadAllLines(journal).Length;
            GitSuperprojectStageResult result = await child.StageInSuperprojectAsync(snapshot);
            Check(result.Verified && File.ReadAllLines(journal).Skip(before).All(row =>
                { using JsonDocument record = JsonDocument.Parse(row); return record.RootElement.GetProperty("context").GetString() == "captured"; }),
                "All staging probes/native update/readback retain the snapshot's injected Git executable and environment.");
        }
        finally { Environment.SetEnvironmentVariable(marker, previous); }
    }

    private static async Task ReadbackFailureAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); string marker = f.Output("staged"), journal = f.Output("readback-failure.jsonl");
        GitRepository child = await Wrapped(f, journal, "if 'update-index' in a and '--add' in a:\n r=subprocess.run(['/usr/bin/git']+a)\n if r.returncode:sys.exit(r.returncode)\n open(" + JsonSerializer.Serialize(marker)
            + ",'w').close();sys.exit(0)\nif 'ls-files' in a and os.path.exists(" + JsonSerializer.Serialize(marker) + "):\n sys.stderr.write('owned readback failure\\n');sys.exit(55)\n");
        GitSuperprojectSnapshot snapshot = await Required(child); await f.AdvanceAsync(); GitSuperprojectStageResult result = await child.StageInSuperprojectAsync(snapshot);
        Check(result.Result.Success && !result.Verified && result.Error?.Contains("owned readback failure", StringComparison.Ordinal) == true
            && await f.Text("rev-parse", ":" + f.Path) == result.ChildHead && Arguments(journal).Count(args => args.Contains("update-index")) == 1,
            "A failed real Git readback is visible while the successful index effect remains intact, without a compensating writer.");
    }

    private static async Task CancellationAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSuperprojectSnapshot snapshot = await Required(f.Child); await f.AdvanceAsync(); byte[] before = await f.IndexBytes();
        using CancellationTokenSource pre = new(); pre.Cancel();
        await Expect<OperationCanceledException>(() => f.Child.GetSuperprojectSnapshotAsync(pre.Token));
        await Expect<OperationCanceledException>(() => f.Child.StageInSuperprojectAsync(snapshot, pre.Token));
        Check(Bytes(before, await f.IndexBytes()), "Pre-cancellation cannot write the index."); if (!OperatingSystem.IsLinux()) { return; }
        string witness = f.Output("writer.json"), journal = f.Output("cancel.jsonl");
        GitRepository child = await Wrapped(f, journal, "if 'update-index' in a and '--add' in a:\n r=subprocess.run(['/usr/bin/git']+a)\n m=" + JsonSerializer.Serialize(witness)
            + ";t=m+'.tmp'\n with open(t,'w') as f:json.dump({'pid':os.getpid(),'exit':r.returncode},f)\n os.replace(t,m)\n while True:time.sleep(.05)\n");
        snapshot = await Required(child); using CancellationTokenSource cancel = new(); Task<GitSuperprojectStageResult> operation = child.StageInSuperprojectAsync(snapshot, cancel.Token);
        int pid = 0; Exception? observation = null;
        try
        {
            DateTime until = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(witness)) { if (operation.IsCompleted) { await operation; } if (DateTime.UtcNow > until) { throw new TimeoutException("No owned native staging witness."); } await Task.Delay(15); }
            using JsonDocument record = JsonDocument.Parse(await File.ReadAllTextAsync(witness)); pid = record.RootElement.GetProperty("pid").GetInt32();
            Check(record.RootElement.GetProperty("exit").GetInt32() == 0 && Directory.Exists("/proc/" + pid), "The real native update completed while the actual owned writer is witnessed live.");
        }
        catch (Exception error) { observation = error; }
        finally { cancel.Cancel(); }
        await Expect<OperationCanceledException>(async () => await operation);
        if (observation is not null) { throw new InvalidOperationException("Observation failed after draining the owned writer.", observation); }
        Check(!Directory.Exists("/proc/" + pid) && await f.Text("rev-parse", ":" + f.Path) == await f.Child.ResolveRevisionAsync("HEAD")
            && Arguments(journal).Count(args => args.Contains("update-index")) == 1, "Cancellation drains the real actor, retains its actual index effect, and never starts a compensating writer.");
    }

    private static async Task ReplacedAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); using Fixture other = await Fixture.CreateAsync(); GitSuperprojectSnapshot snapshot = await Required(f.Child);
        await Expect<ArgumentException>(() => other.Child.StageInSuperprojectAsync(snapshot)); byte[] before = await f.IndexBytes();
        Directory.Move(f.Child.Path, f.Output("saved-child")); Directory.CreateDirectory(f.Child.Path);
        await Expect<ArgumentException>(() => new GitRepository(f.Child.Path).StageInSuperprojectAsync(snapshot));
        GitRepository replacement = await GitRepository.InitAsync(f.Child.Path);
        await Expect<InvalidOperationException>(() => replacement.StageInSuperprojectAsync(snapshot));
        Check(Bytes(before, await f.IndexBytes()), "Replacing a registered pathname with a different real child cannot reuse the old association or alter its index.");
    }

    private static async Task<GitSuperprojectSnapshot> Required(GitRepository child)
        => await child.GetSuperprojectSnapshotAsync() ?? throw new InvalidOperationException("Expected actual registered superproject association.");
    private static async Task<GitResult> Native(string cwd, params string[] args)
    { GitResult result = await new GitRunner().RunAsync(cwd, args); return result; }
    private static async Task<GitRepository> Wrapped(Fixture f, string journal, string body = "")
    {
        string file = f.Output("git-superproject-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(file, "#!/usr/bin/python3\nimport json,os,subprocess,sys,time\na=sys.argv[1:]\nwith open(" + JsonSerializer.Serialize(journal)
            + ",'a') as f:f.write(json.dumps({'argv':a,'cwd':os.getcwd(),'context':os.environ.get('GE_SUPERPROJECT_PRIVATE_CONTEXT')})+'\\n')\n" + body + "os.execv('/usr/bin/git',['git']+a)\n");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        return new(f.Child.Path, new GitRunner(file));
    }
    private static string[][] Arguments(string journal) => File.ReadAllLines(journal).Select(line =>
    { using JsonDocument row = JsonDocument.Parse(line); return row.RootElement.GetProperty("argv").EnumerateArray().Select(value => value.GetString()!).ToArray(); }).ToArray();
    private static string Line(string value) => value.EndsWith('\n') ? value[..^1] : value;
    private static bool Bytes(byte[] first, byte[] second) => first.SequenceEqual(second);
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Pipes
    {
        private readonly MemoryStream _error = new(); private readonly object _gate = new();
        public byte[] Error { get { lock (_gate) { return _error.ToArray(); } } }
        public void Receive(GitProcessOutput output) { if (output.Stream == GitProcessOutputStream.StandardError) { lock (_gate) { _error.Write(output.Bytes.Span); } } }
    }
    private sealed class Fixture : IDisposable
    {
        private Fixture(RepositoryFixture root, RepositoryFixture source, string path)
        { Root = root; Source = source; Path = path; Child = new(root.File(path)); }
        public RepositoryFixture Root { get; }
        public RepositoryFixture Source { get; }
        public GitRepository Parent => Root.Repository;
        public GitRepository Child { get; }
        public string Path { get; }
        public string ChildFile(string name) => System.IO.Path.Combine(Child.Path, name);
        public string Output(string name) => System.IO.Path.Combine(Root.Root, name);
        public Task<GitResult> Native(params string[] args) => SuperprojectScenarios.Native(Parent.Path, args);
        public async Task<string> Text(params string[] args) => Line((await Native(args)).StandardOutput);
        public Task<byte[]> IndexBytes() => File.ReadAllBytesAsync(Root.File(".git/index"));
        public async Task<string[]> IndexEntries() => (await Native("--literal-pathspecs", "ls-files", "--stage", "-z", "--", Path)).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        public async Task AdvanceAsync(GitRepository? child = null)
        {
            child ??= Child; await File.AppendAllTextAsync(System.IO.Path.Combine(child.Path, "seed.txt"), "new child " + Guid.NewGuid().ToString("N") + "\n");
            await child.StageAsync(["seed.txt"]); await child.CommitAsync("Child advancement");
        }
        public static async Task<Fixture> CreateAsync(string path = "module")
        {
            RepositoryFixture root = await RepositoryFixture.CreateAsync(), source = await RepositoryFixture.CreateAsync();
            Fixture f = new(root, source, path);
            try
            {
                // Native submodule add forwards an option-like destination to an internal git add without -- on this Git version.
                // Create at a neutral path, then native git mv keeps the genuine leading-dash destination/registration for every oracle below.
                string setupPath = path.StartsWith('-') ? "fixture-safe-module" : path;
                GitResult added = await f.Native("-c", "protocol.file.allow=always", "submodule", "add", "--name", "module", "--", source.Repository.Path, setupPath);
                if (!added.Success) { throw new GitCommandException(["submodule", "add"], added); }
                if (setupPath != path)
                {
                    GitResult moved = await f.Native("--literal-pathspecs", "mv", "--", setupPath, path);
                    if (!moved.Success) { throw new GitCommandException(["mv"], moved); }
                }
                GitResult configured = await f.Native("config", "--file", ".gitmodules", "--get", "submodule.module.path");
                GitResult nativeRoot = await SuperprojectScenarios.Native(f.Child.Path, "rev-parse", "--show-toplevel");
                string[] indexed = await f.IndexEntries();
                Check(configured.Success && Line(configured.StandardOutput) == path && nativeRoot.Success && Line(nativeRoot.StandardOutput) == f.Child.Path
                    && indexed.Length == 1 && indexed[0].StartsWith("160000 ", StringComparison.Ordinal) && indexed[0].EndsWith(" 0\t" + path, StringComparison.Ordinal),
                    "Fixture setup retains the exact literal registration, distinct child root and stage-0 gitlink before testing the API.");
                await root.Repository.CommitAsync("Register child"); await RepositoryFixture.ConfigureAsync(f.Child); return f;
            }
            catch { f.Dispose(); throw; }
        }
        public void Dispose() { Root.Dispose(); Source.Dispose(); }
    }
}

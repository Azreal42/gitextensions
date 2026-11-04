using System.Text.Json;
using GitExtensions.Linux;

internal static class ToolConfigurationScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("tool configuration snapshots retain actual scopes origins valueless entries and literal subsection case", SnapshotAsync),
        ("tool configuration saves only final literal names and exact path command values", LiteralAsync),
        ("tool configuration keeps identical duplicate noops and refuses changed duplicates before any write", DuplicatesAsync),
        ("tool configuration refuses included-only unset and exposes real override origins", IncludesAsync),
        ("tool configuration separates private Local Global and read-only effective views", ScopesAsync),
        ("tool configuration respects both global files and native XDG destination selection", XdgAsync),
        ("tool configuration preflights complete batches and never deletes old definitions", ValidationAsync),
        ("tool configuration refuses changed includes contexts and retargeted symlinks", GuardsAsync),
        ("tool configuration serializes competing shared global snapshots across repository instances", SharedAsync),
        ("tool configuration reports real partial writes when the later global config lock fails", PartialAsync),
        ("tool configuration cancellation drains the actual native writer and reports uncertain effects", CancellationAsync),
        ("tool configuration distinguishes old capability metadata without issuing unsupported scope reads", VersionAsync),
        ("tool configuration supports bare local settings and global settings outside a repository", BareOutsideAsync),
        ("merge tools consume selected GUI then native merge.tool fallback on real conflicts", MergeConsumerAsync)
    ];

    private static async Task SnapshotAsync()
    {
        using Fixture f = await Fixture.Create();
        await f.Git("config", "--file", f.Global, "diff.guitool", "global");
        await f.Git("config", "diff.guitool", "local");
        await f.Git("config", "difftool.Case.With.dot.path", "  /literal\\path '$() λ  ");
        await File.AppendAllTextAsync(f.Config, "\n[difftool \"Case.With.dot\"]\n cmd\n[private]\n secret = do-not-expose\n");
        GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
        GitToolConfigurationScopeSnapshot local = snapshot.GetScope(GitToolConfigurationView.Local);
        Check(local.CanWrite && snapshot.SupportsGuiMergeTool && snapshot.GitVersion >= new Version(2, 26), "Qualified Git exposes writable Local and its actual version.");
        Check(local.SelectedDiffGui.Occurrences.Single().Value == "local" && snapshot.GetScope(GitToolConfigurationView.Global).SelectedDiffGui.Occurrences.Single().Value == "global",
            "The same key remains separate in its actual scopes.");
        Check(snapshot.GetScope(GitToolConfigurationView.Effective).SelectedDiffGui.Occurrences.Select(entry => entry.Value).SequenceEqual(["global", "local"])
            && !snapshot.GetScope(GitToolConfigurationView.System).CanWrite && !snapshot.GetScope(GitToolConfigurationView.Effective).CanWrite, "Effective precedence is ordered and read-only.");
        GitToolConfigurationDefinition tool = local.GetTool(GitToolRole.Diff, "Case.With.dot");
        Check(tool.Command.Occurrences.Single().Value is null && tool.Path.Occurrences.Single().Value == "  /literal\\path '$() λ  "
            && local.GetTool(GitToolRole.Diff, "case.With.dot").Path.Occurrences.Count == 0, "Valueless entries differ from missing values and subsection case is literal.");
        Check(local.Occurrences.All(entry => entry.Key != "private.secret") && local.Occurrences.All(entry => entry.Scope == "local" && entry.ResolvedOriginPath == f.Config && entry.IsNativeWriteTarget),
            "Public DTOs expose only tool keys, with real file origins and native-target qualification.");
        await Expect<NotSupportedException>(() => { ((IList<GitToolConfigurationOccurrence>)local.Occurrences)[0] = local.Occurrences[0]; return Task.CompletedTask; });
        GitToolConfigurationSaveResult cleared = await f.Repository.SaveToolConfigurationAsync(snapshot, [Local(new GitToolRoleEdit(GitToolRole.Diff, "Case.With.dot", true, Command: new(null)))]);
        Check(cleared.Success && cleared.After!.GetScope(GitToolConfigurationView.Local).GetTool(GitToolRole.Diff, "Case.With.dot").Command.Occurrences.Count == 0,
            "Clearing an existing valueless variable is a real unset, not a missing-key noop.");
    }

    private static async Task LiteralAsync()
    {
        using Fixture f = await Fixture.Create();
        string name = "Final.Case λ '$()\\\"", path = "  /private path/λ\\'\"$()  ", command = "printf '%s\\n' \"$LOCAL\" \"$REMOTE\"\nprintf 'λ'\n";
        await f.Git("config", "difftool.old.path", "old-path"); await f.Git("config", "diff.guitool", "old");
        await f.Git("config", "--add", "unrelated.value", "first"); await f.Git("config", "--add", "unrelated.value", "second");
        string head = await f.Repository.ResolveRevisionAsync("HEAD"); byte[] index = await File.ReadAllBytesAsync(f.File(".git/index"));
        GitToolConfigurationSaveResult result = await f.Repository.SaveToolConfigurationAsync(await f.Repository.GetToolConfigurationAsync(),
            [Local(new GitToolRoleEdit(GitToolRole.Diff, name, true, new(path), new(command)))]);
        GitToolConfigurationScopeSnapshot local = result.After!.GetScope(GitToolConfigurationView.Local);
        Check(result.Success && result.MutationStarted == true && result.Writes.Count == 3 && result.Writes.All(write => write.NativeResult?.Success == true
            && write.Outcome == GitToolConfigurationWriteOutcome.NativeCompleted && !write.Arguments.Contains("--replace-all") && !write.Arguments.Contains("--unset-all")), "Every changed field retains actual scalar Git argv and successful native result.");
        Check(result.Writes.Select(write => write.Key).SequenceEqual(["difftool." + name + ".path", "difftool." + name + ".cmd", "diff.guitool"])
            && local.GetTool(GitToolRole.Diff, name).Path.Occurrences.Single().Value == path && local.GetTool(GitToolRole.Diff, name).Command.Occurrences.Single().Value == command,
            "Definitions precede selection and preserve exact argv data including quotes/newlines.");
        Check(local.GetTool(GitToolRole.Diff, "old").Path.Occurrences.Single().Value == "old-path"
            && (await f.Repository.GetConfigAsync("^unrelated\\.value$")).Select(pair => pair.Value).SequenceEqual(["first", "second"]), "Unvisited definitions and unrelated multivalues remain intact.");
        byte[] afterIndex = await File.ReadAllBytesAsync(f.File(".git/index"));
        Check(await f.Repository.ResolveRevisionAsync("HEAD") == head && index.SequenceEqual(afterIndex), "Editing tools does not mutate revisions or index.");
    }

    private static async Task DuplicatesAsync()
    {
        using Fixture f = await Fixture.Create();
        await f.Git("config", "--add", "diff.guitool", "first"); await f.Git("config", "--add", "diff.guitool", "last");
        byte[] before = await File.ReadAllBytesAsync(f.Config);
        GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
        Check(!snapshot.GetScope(GitToolConfigurationView.Local).SelectedDiffGui.CanChange, "Duplicate values carry an explicit per-key restriction.");
        GitToolConfigurationSaveResult noop = await f.Repository.SaveToolConfigurationAsync(snapshot, [Local(new GitToolRoleEdit(GitToolRole.Diff, "last", true))]);
        byte[] afterNoop = await File.ReadAllBytesAsync(f.Config);
        Check(noop.Success && noop.MutationStarted == false && !noop.MayHaveChanged && noop.Writes.Single().Outcome == GitToolConfigurationWriteOutcome.NoOp
            && before.SequenceEqual(afterNoop), "Same-value noop keeps all duplicate bytes rather than flattening them.");
        GitToolConfigurationSaveException refused = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(noop.After!,
            [Local(new GitToolRoleEdit(GitToolRole.Merge, "would-write", true, Command: new("true")), new(GitToolRole.Diff, "changed", true))]));
        byte[] afterRefusal = await File.ReadAllBytesAsync(f.Config);
        Check(refused.Result.MutationStarted == false && !refused.Result.MayHaveChanged && refused.Result.Writes.Count == 0
            && before.SequenceEqual(afterRefusal), "A late invalid key rejects the entire batch before earlier valid definitions publish.");
    }

    private static async Task IncludesAsync()
    {
        using Fixture f = await Fixture.Create(); string include = Path.Combine(f.Root, "included config");
        await f.Git("config", "--file", include, "diff.guitool", "included"); await f.Git("config", "include.path", include);
        byte[] bytes = await File.ReadAllBytesAsync(include); GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
        Check(!snapshot.GetScope(GitToolConfigurationView.Local).SelectedDiffGui.Occurrences.Single().IsNativeWriteTarget, "Included values retain their distinct physical origin.");
        GitToolConfigurationSaveException unset = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(snapshot, [Local(new GitToolRoleEdit(GitToolRole.Diff, null, true))]));
        byte[] afterUnset = await File.ReadAllBytesAsync(include);
        Check(unset.Result.MutationStarted == false && unset.Result.Writes.Count == 0 && bytes.SequenceEqual(afterUnset), "Included-only unset is refused before any native writer.");
        GitToolConfigurationSaveResult saved = await f.Repository.SaveToolConfigurationAsync(snapshot, [Local(new GitToolRoleEdit(GitToolRole.Diff, "override", true))]);
        IReadOnlyList<GitToolConfigurationOccurrence> occurrences = saved.After!.GetScope(GitToolConfigurationView.Local).SelectedDiffGui.Occurrences;
        byte[] afterOverride = await File.ReadAllBytesAsync(include);
        Check(occurrences.Count == 2 && occurrences.Any(entry => entry.Value == "override" && entry.IsNativeWriteTarget)
            && occurrences.Any(entry => entry.Value == "included" && !entry.IsNativeWriteTarget) && bytes.SequenceEqual(afterOverride),
            "A real native override preserves the included file and exposes both actual origins, including shadowing.");
        await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(saved.After!, [Local(new GitToolRoleEdit(GitToolRole.Diff, "third", true))]));
    }

    private static async Task ScopesAsync()
    {
        using Fixture f = await Fixture.Create();
        await f.Git("config", "--file", f.Global, "merge.tool", "fallback");
        GitToolConfigurationSnapshot initial = await f.Repository.GetToolConfigurationAsync();
        byte[] before = await File.ReadAllBytesAsync(f.Config), global = await File.ReadAllBytesAsync(f.Global);
        GitToolConfigurationSaveResult noop = await f.Repository.SaveToolConfigurationAsync(initial,
            [new(GitConfigScope.Global, [new(GitToolRole.Merge, "fallback", false)])]);
        byte[] localAfterNoop = await File.ReadAllBytesAsync(f.Config), globalAfterNoop = await File.ReadAllBytesAsync(f.Global);
        Check(noop.Writes.Count == 0 && before.SequenceEqual(localAfterNoop) && global.SequenceEqual(globalAfterNoop), "Displaying merge.tool never copies it into merge.guitool on untouched OK.");
        GitToolConfigurationSaveResult saved = await f.Repository.SaveToolConfigurationAsync(noop.After!,
            [new(GitConfigScope.Global, [new(GitToolRole.Merge, "global-gui", true)]), Local(new GitToolRoleEdit(GitToolRole.Diff, "local-gui", true))]);
        Check(saved.Writes.Select(write => write.Scope).SequenceEqual([GitConfigScope.Local, GitConfigScope.Global])
            && saved.After!.GetScope(GitToolConfigurationView.Local).SelectedDiffGui.Occurrences.Single().Value == "local-gui"
            && saved.After!.GetScope(GitToolConfigurationView.Global).SelectedMergeGui.Occurrences.Single().Value == "global-gui"
            && saved.After!.GetScope(GitToolConfigurationView.Global).MergeToolFallback.Occurrences.Single().Value == "fallback", "Explicit drafts publish only their own scope selectors, leaving merge.tool unchanged.");
        saved = await f.Repository.SaveToolConfigurationAsync(saved.After!, [new(GitConfigScope.Global, [new(GitToolRole.Merge, " ", true)])]);
        Check(saved.After!.GetScope(GitToolConfigurationView.Global).SelectedMergeGui.Occurrences.Count == 0
            && saved.After!.GetScope(GitToolConfigurationView.Global).MergeToolFallback.Occurrences.Single().Value == "fallback", "Whitespace selector clears only the GUI choice, revealing the existing native fallback.");
    }

    private static async Task XdgAsync()
    {
        using Fixture f = await Fixture.Create(); Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", null);
        string xdg = Path.Combine(f.Xdg, "git", "config"); Directory.CreateDirectory(Path.GetDirectoryName(xdg)!);
        await f.Git("config", "--file", xdg, "diff.guitool", "xdg");
        GitToolConfigurationSaveResult first = await f.Repository.SaveToolConfigurationAsync(await f.Repository.GetToolConfigurationAsync(),
            [new(GitConfigScope.Global, [new(GitToolRole.Diff, "xdg-updated", true)])]);
        string physicalXdg = xdg;
        if (!OperatingSystem.IsWindows())
        {
            // macOS temporary directories can contain symlinked parents; compare the actual physical origin.
            GitResult directory = await new GitRunner("/bin/pwd").RunAsync(Path.GetDirectoryName(xdg)!, ["-P"]);
            Check(directory.Success, "The native working directory must be available for the origin assertion.");
            physicalXdg = Path.Combine(directory.StandardOutput.TrimEnd('\r', '\n'), Path.GetFileName(xdg));
        }
        Check(!File.Exists(f.Global) && first.After!.GetScope(GitToolConfigurationView.Global).SelectedDiffGui.Occurrences.Single().ResolvedOriginPath == physicalXdg,
            "With HOME config absent and XDG present, native Git writes the existing XDG file.");
        await f.Git("config", "--file", f.Global, "diff.guitool", "home");
        GitToolConfigurationSnapshot both = await f.Repository.GetToolConfigurationAsync();
        Check(both.GetScope(GitToolConfigurationView.Global).SelectedDiffGui.Occurrences.Select(entry => entry.Value).SequenceEqual(["xdg-updated", "home"]), "Both global files are read in native precedence order, not merged into an invented single origin.");
        byte[] home = await File.ReadAllBytesAsync(f.Global), xdgBytes = await File.ReadAllBytesAsync(xdg);
        await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(both, [new(GitConfigScope.Global, [new(GitToolRole.Diff, "would-flatten", true)])]));
        byte[] homeAfter = await File.ReadAllBytesAsync(f.Global), xdgAfter = await File.ReadAllBytesAsync(xdg);
        Check(home.SequenceEqual(homeAfter) && xdgBytes.SequenceEqual(xdgAfter), "A cross-file duplicate in one scope is refused without flattening either file.");
    }

    private static async Task ValidationAsync()
    {
        using Fixture f = await Fixture.Create(); await f.Git("config", "diff.guitool", "old"); await f.Git("config", "difftool.old.cmd", "old-command");
        GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync(); byte[] before = await File.ReadAllBytesAsync(f.Config);
        GitToolConfigurationScopeEdit[][] invalid =
        [
            [Local(new GitToolRoleEdit(GitToolRole.Diff, "bad\nname", true))],
            [Local(new GitToolRoleEdit(GitToolRole.Diff, "old", false, Command: new("bad\0value")))],
            [Local(new GitToolRoleEdit((GitToolRole)99, "bad", true))],
            [Local(new GitToolRoleEdit(GitToolRole.Diff, "one", true), new(GitToolRole.Diff, "two", true))],
            [Local(new GitToolRoleEdit(GitToolRole.Diff, "old", false)), Local(new GitToolRoleEdit(GitToolRole.Merge, "other", true))],
            [Local(new GitToolRoleEdit(GitToolRole.Diff, "different", false, Command: new("true")))],
            [Local(new GitToolRoleEdit(GitToolRole.Diff, null, true, Path: new("path")))],
            [new((GitConfigScope)99, [new(GitToolRole.Diff, "wrong-scope", true)])]
        ];
        foreach (GitToolConfigurationScopeEdit[] edits in invalid)
        {
            GitToolConfigurationSaveException error = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(snapshot, edits));
            byte[] afterInvalid = await File.ReadAllBytesAsync(f.Config);
            Check(error.Result.MutationStarted == false && !error.Result.MayHaveChanged && before.SequenceEqual(afterInvalid), "Invalid batch does not publish any valid prefix.");
        }
        await Expect<GitToolConfigurationSaveException>(() => new GitRepository(f.Repository.Path).SaveToolConfigurationAsync(snapshot, []));
        GitToolConfigurationSaveResult cleared = await f.Repository.SaveToolConfigurationAsync(snapshot, [Local(new GitToolRoleEdit(GitToolRole.Diff, null, true))]);
        Check(cleared.After!.GetScope(GitToolConfigurationView.Local).GetTool(GitToolRole.Diff, "old").Command.Occurrences.Single().Value == "old-command", "Clearing selection never removes the old subsection.");
    }

    private static async Task GuardsAsync()
    {
        using Fixture f = await Fixture.Create(); string included = Path.Combine(f.Root, "guard.conf"); await File.WriteAllTextAsync(included, "# originally empty\n");
        await f.Git("config", "include.path", included); GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
        await File.AppendAllTextAsync(included, "# external comment only\n");
        GitToolConfigurationSaveException stale = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(snapshot, [Local(new GitToolRoleEdit(GitToolRole.Diff, "new", true))]));
        Check(stale.Result.MutationStarted == false, "Even a comment-only empty included source is guarded physically.");
        snapshot = await f.Repository.GetToolConfigurationAsync(); Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "never");
        await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(snapshot, [Local(new GitToolRoleEdit(GitToolRole.Diff, "new", true))]));
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
        if (!OperatingSystem.IsWindows())
        {
            string copy = Path.Combine(f.Root, "copy-config"), first = Path.Combine(f.Root, "first-config"); File.Copy(f.Config, first); File.Copy(f.Config, copy); File.Delete(f.Config); File.CreateSymbolicLink(f.Config, first);
            snapshot = await f.Repository.GetToolConfigurationAsync(); File.Delete(f.Config); File.CreateSymbolicLink(f.Config, copy);
            await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(snapshot, [Local(new GitToolRoleEdit(GitToolRole.Diff, "new", true))]));
            Check(!(await f.Repository.GetConfigAsync("^diff\\.guitool$")).Any(), "An identical-byte symlink retarget is refused without writing either target.");
        }
    }

    private static async Task SharedAsync()
    {
        using Fixture f = await Fixture.Create(); GitRepository other = new(f.Repository.Path);
        GitToolConfigurationSnapshot first = await f.Repository.GetToolConfigurationAsync(), second = await other.GetToolConfigurationAsync();
        Task<bool> one = Save(f.Repository, first, "one"), two = Save(other, second, "two");
        bool[] successes = await Task.WhenAll(one, two);
        Check(successes.Count(success => success) == 1 && (await f.Repository.GetToolConfigurationAsync()).GetScope(GitToolConfigurationView.Global).SelectedDiffGui.Occurrences.Count == 1,
            "A shared native-target semaphore plus revalidation permits one snapshot and refuses its stale competitor.");
        static async Task<bool> Save(GitRepository repository, GitToolConfigurationSnapshot snapshot, string name)
        {
            try { return (await repository.SaveToolConfigurationAsync(snapshot, [new(GitConfigScope.Global, [new(GitToolRole.Diff, name, true)])])).Success; }
            catch (GitToolConfigurationSaveException error) { Check(error.Result.MutationStarted == false && !error.Result.MayHaveChanged, "The losing save never starts a native writer."); return false; }
        }
    }

    private static async Task PartialAsync()
    {
        using Fixture f = await Fixture.Create(); GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
        await File.WriteAllTextAsync(f.Global + ".lock", "held-by-test\n");
        GitToolConfigurationSaveException error = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(snapshot,
            [Local(new GitToolRoleEdit(GitToolRole.Diff, "published-first", true)), new(GitConfigScope.Global, [new(GitToolRole.Merge, "blocked-second", true)])]));
        Check(error.Result.MutationStarted == true && error.Result.MayHaveChanged && !error.Result.Success && error.Result.Writes.Count == 2
            && error.Result.Writes[0].NativeResult?.Success == true && error.Result.Writes[1].NativeResult is { Success: false }, "A real later Git lock failure retains both native results and truthful partial state.");
        Check(error.Result.After!.GetScope(GitToolConfigurationView.Local).SelectedDiffGui.Occurrences.Single().Value == "published-first"
            && await File.ReadAllTextAsync(f.Global + ".lock") == "held-by-test\n" && !File.Exists(f.Global), "The first effect persists; the existing foreign lock is neither removed nor rolled back.");
    }

    private static async Task CancellationAsync()
    {
        using Fixture f = await Fixture.Create(); GitToolConfigurationSnapshot baseline = await f.Repository.GetToolConfigurationAsync();
        using (CancellationTokenSource pre = new())
        {
            pre.Cancel(); GitToolConfigurationSaveCanceledException error = await Expect<GitToolConfigurationSaveCanceledException>(() => f.Repository.SaveToolConfigurationAsync(baseline, [Local(new GitToolRoleEdit(GitToolRole.Diff, "never", true))], pre.Token));
            Check(error.Result.MutationStarted == false && !error.Result.MayHaveChanged, "Pre-cancellation cannot publish a config change.");
        }
        if (!OperatingSystem.IsLinux()) { return; }
        string marker = Path.Combine(f.Root, "writer-started.json"), journal = Path.Combine(f.Root, "writer-argv.jsonl");
        GitRepository repository = await f.Wrapped("import subprocess,time\n"
            + "if 'config' in a and '--list' not in a and '--' in a:\n"
            + " r=subprocess.run(['/usr/bin/git']+a)\n"
            + " m=" + JsonSerializer.Serialize(marker) + ";t=m+'.tmp'\n"
            + " with open(t,'w') as f:json.dump({'pid':os.getpid(),'gitExit':r.returncode},f)\n"
            + " os.replace(t,m)\n"
            + " while True:time.sleep(.05)\n", journal);
        baseline = await repository.GetToolConfigurationAsync(); using CancellationTokenSource cancel = new();
        Task<GitToolConfigurationSaveResult> operation = repository.SaveToolConfigurationAsync(baseline, [Local(new GitToolRoleEdit(GitToolRole.Diff, "native-before-cancel", true))], cancel.Token);
        int pid = 0, lines = 0; Exception? observationFailure = null;
        try
        {
            DateTime until = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(marker))
            {
                if (operation.IsCompleted) { await operation; }
                if (DateTime.UtcNow > until) { throw new TimeoutException("The owned native config writer did not publish its marker."); }
                await Task.Delay(15);
            }
            using JsonDocument record = JsonDocument.Parse(await File.ReadAllTextAsync(marker)); pid = record.RootElement.GetProperty("pid").GetInt32();
            Check(record.RootElement.GetProperty("gitExit").GetInt32() == 0 && Directory.Exists("/proc/" + pid), "The actual Git command has exited zero while its witnessed owned wrapper is still live.");
            lines = File.ReadAllLines(journal).Length;
        }
        catch (Exception exception) { observationFailure = exception; }
        finally { cancel.Cancel(); }
        GitToolConfigurationSaveCanceledException errorAfter = await Expect<GitToolConfigurationSaveCanceledException>(async () => await operation);
        if (observationFailure is not null) { throw new InvalidOperationException("Native writer observation failed after its owned operation drained.", observationFailure); }
        Check(errorAfter.Result.Cancelled && errorAfter.Result.MutationStarted is null && errorAfter.Result.MayHaveChanged && errorAfter.Result.After is null
            && errorAfter.Result.Writes.Single().Outcome == GitToolConfigurationWriteOutcome.Unknown && !Directory.Exists("/proc/" + pid)
            && File.ReadAllLines(journal).Length == lines, "Cancellation drains the witnessed process, retains uncertain native result, and starts no post-cancel readback query.");
        Check((await f.Repository.GetConfigAsync("^diff\\.guitool$")).Single().Value == "native-before-cancel", "A separate later observer sees the already published native effect; cancellation did not roll it back.");
    }

    private static async Task VersionAsync()
    {
        using Fixture f = await Fixture.Create(); string journal = Path.Combine(f.Root, "old-version-argv.jsonl");
        GitRepository old = await f.Wrapped("if a==['--version']:\n print('git version 2.25.0');sys.exit(0)\n", journal);
        GitToolConfigurationSnapshot snapshot = await old.GetToolConfigurationAsync();
        Check(snapshot.GitVersion == new Version(2, 25, 0) && snapshot.Scopes.All(scope => !scope.IsAvailable && !scope.CanWrite && scope.Occurrences.Count == 0)
            && !File.ReadAllText(journal).Contains("--show-scope", StringComparison.Ordinal), "A controlled old-version capability response never triggers unsupported scope reads or fabricated origins.");
        await Expect<GitToolConfigurationSaveException>(() => old.SaveToolConfigurationAsync(snapshot, [Local(new GitToolRoleEdit(GitToolRole.Diff, "no", true))]));
    }

    private static async Task BareOutsideAsync()
    {
        using Fixture f = await Fixture.Create(); GitRepository bare = await GitRepository.InitAsync(Path.Combine(f.Root, "bare.git"), bare: true);
        GitToolConfigurationSaveResult saved = await bare.SaveToolConfigurationAsync(await bare.GetToolConfigurationAsync(), [Local(new GitToolRoleEdit(GitToolRole.Diff, "bare-gui", true))]);
        Check(saved.After!.GetScope(GitToolConfigurationView.Local).SelectedDiffGui.Occurrences.Single().Value == "bare-gui", "Bare configuration uses its actual config destination without requiring an index or worktree.");
        string directory = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(directory); GitRepository outside = new(directory);
        GitToolConfigurationSnapshot snapshot = await outside.GetToolConfigurationAsync();
        Check(!snapshot.GetScope(GitToolConfigurationView.Local).IsAvailable && snapshot.GetScope(GitToolConfigurationView.Global).CanWrite, "Outside a repository only Local is unavailable.");
        saved = await outside.SaveToolConfigurationAsync(snapshot, [new(GitConfigScope.Global, [new(GitToolRole.Diff, "outside-global", true)])]);
        Check(saved.Success && saved.After!.GetScope(GitToolConfigurationView.Global).SelectedDiffGui.Occurrences.Single().Value == "outside-global", "Private Global remains writable with a valid non-repository cwd.");
    }

    private static async Task MergeConsumerAsync()
    {
        foreach (bool gui in new[] { true, false })
        {
            using Fixture f = await Fixture.Create(); const string path = "literal [*] λ conflict.txt";
            await File.WriteAllTextAsync(f.File(path), "base\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Base");
            await f.Git("checkout", "-b", "topic"); await File.WriteAllTextAsync(f.File(path), "theirs\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Theirs");
            await f.Git("checkout", "main"); await File.WriteAllTextAsync(f.File(path), "ours\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Ours");
            await f.Repository.MergeAsync(new GitMergeOptions("topic", NoCommit: true)); string head = await f.Repository.ResolveRevisionAsync("HEAD");
            string output = Path.Combine(f.Root, "chosen-tool.txt");
            await f.Git("config", "merge.tool", "fallback-tool");
            foreach (string name in new[] { "gui-tool", "fallback-tool" })
            {
                await f.Git("config", "mergetool." + name + ".cmd", "printf '%s' " + Quote(name) + " > " + Quote(output) + "; test -f \"$BASE\" && test -f \"$LOCAL\" && test -f \"$REMOTE\" && cp \"$REMOTE\" \"$MERGED\"");
                await f.Git("config", "mergetool." + name + ".trustExitCode", "true");
            }
            await f.Git("config", "mergetool.keepBackup", "false");
            GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
            GitToolConfigurationSaveResult saved = await f.Repository.SaveToolConfigurationAsync(snapshot, [Local(new GitToolRoleEdit(GitToolRole.Merge, gui ? "gui-tool" : null, true))]);
            await f.Repository.RunMergeToolAsync(path);
            Check(await File.ReadAllTextAsync(output) == (gui ? "gui-tool" : "fallback-tool") && await File.ReadAllTextAsync(f.File(path)) == "theirs\n"
                && (await f.Repository.GetConflictsAsync()).Count == 0 && await f.Repository.ResolveRevisionAsync("HEAD") == head && File.Exists(f.File(".git/MERGE_HEAD")),
                "The actual Git merge helper selects GUI/fallback, receives literal real stages, stages its result, and leaves commit/continuation to the owner.");
            Check(saved.After!.GetScope(GitToolConfigurationView.Local).MergeToolFallback.Occurrences.Single().Value == "fallback-tool", "Neither GUI consumption nor unset rewrites merge.tool.");
        }
    }

    private static GitToolConfigurationScopeEdit Local(params GitToolRoleEdit[] roles) => new(GitConfigScope.Local, roles);
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T exception) { return exception; } throw new InvalidOperationException("Expected " + typeof(T).Name + "."); }

    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<string, string?> _environment;
        private Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "gitextensions-tool-config-" + Guid.NewGuid().ToString("N"));
            Home = Path.Combine(Root, "home"); Xdg = Path.Combine(Root, "xdg"); Directory.CreateDirectory(Home); Directory.CreateDirectory(Xdg);
            _environment = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(IsPrivateVariable).ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
            foreach (string name in _environment.Keys) { Environment.SetEnvironmentVariable(name, null); }
            Environment.SetEnvironmentVariable("HOME", Home); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Xdg);
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Global); Environment.SetEnvironmentVariable("GIT_CONFIG_SYSTEM", Path.Combine(Root, "system-config"));
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1"); Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow"); Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
        }
        public string Root { get; }
        public string Home { get; }
        public string Xdg { get; }
        public string Global => Path.Combine(Home, ".gitconfig");
        public GitRepository Repository { get; private set; } = null!;
        public string Config => File(".git/config");
        public string File(string relative) => Path.Combine(Repository.Path, relative);
        public static async Task<Fixture> Create()
        {
            Fixture fixture = new();
            try
            {
                fixture.Repository = await GitRepository.InitAsync(Path.Combine(fixture.Root, "repository"));
                await RepositoryFixture.ConfigureAsync(fixture.Repository);
                await System.IO.File.WriteAllTextAsync(fixture.File("seed.txt"), "seed\n"); await fixture.Repository.StageAllAsync(); await fixture.Repository.CommitAsync("Initial");
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        public async Task<GitResult> Git(params string[] arguments)
        {
            GitResult result = await new GitRunner().RunAsync(Repository.Path, arguments);
            if (!result.Success) { throw new GitCommandException(arguments, result); } return result;
        }
        public async Task<GitRepository> Wrapped(string body, string journal)
        {
            string wrapper = Path.Combine(Root, "git-wrapper-" + Guid.NewGuid().ToString("N"));
            await System.IO.File.WriteAllTextAsync(wrapper, "#!/usr/bin/python3\nimport json,os,sys\na=sys.argv[1:]\nwith open(" + JsonSerializer.Serialize(journal) + ",'a') as f:f.write(json.dumps(a)+'\\n')\n" + body + "os.execv('/usr/bin/git',['git']+a)\n");
            if (!OperatingSystem.IsWindows()) { System.IO.File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            return new(Repository.Path, new GitRunner(wrapper));
        }
        private static bool IsPrivateVariable(string name) => name.StartsWith("GIT_CONFIG", StringComparison.Ordinal) || name is "HOME" or "XDG_CONFIG_HOME";
        public void Dispose()
        {
            foreach (string name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(IsPrivateVariable).ToArray()) { Environment.SetEnvironmentVariable(name, null); }
            foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); }
            if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
        }
    }
}

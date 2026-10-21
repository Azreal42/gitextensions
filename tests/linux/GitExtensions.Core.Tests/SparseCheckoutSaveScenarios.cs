using GitExtensions.Linux;

internal static class SparseCheckoutSaveScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("sparse save-only cone rules preserve literal names index bytes and working files until reapply", ConeAsync),
        ("sparse save-only non-cone rules defer checkout changes and retain dirty files", PatternsAsync),
        ("sparse save-only disable retains rules and index until explicit full checkout restoration", DisableAsync),
        ("sparse save-only linked worktree settings do not enable or rewrite the main checkout", LinkedAsync),
        ("sparse save-only linked checkout from bare migrates core.bare true only to the main config", BareLinkedAsync),
        ("sparse save-only validates before writes and respects another native rules lock", ValidationAsync),
        ("sparse save-only migrates a displaced main core.worktree without changing checkout bytes", MainWorktreeConfigAsync)
    ];

    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static async Task<GitResult> Git(GitRepository repository, params string[] args)
    {
        GitResult result = await new GitRunner().RunAsync(repository.Path, ["--no-optional-locks", .. args]);
        if (!result.Success) { throw new GitCommandException(args, result); } return result;
    }
    private static async Task<string> GitPath(GitRepository repository, string name)
        => (await Git(repository, "rev-parse", "--path-format=absolute", "--git-path", name)).StandardOutput.TrimEnd('\r', '\n');
    private static async Task<RepositoryFixture> CreateAsync()
    {
        RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        fixture.Write("included/public/file.txt", "public\n"); fixture.Write("included/private/file.txt", "private\n"); fixture.Write("excluded/file.txt", "excluded\n");
        await fixture.Repository.StageAllAsync(); await fixture.Repository.CommitAsync("Sparse save-only fixture"); return fixture;
    }
    private static async Task ConeAsync()
    {
        using RepositoryFixture f = await CreateAsync();
        string[] selected = ["nested/a", "nested/b", "[literal]*?", "quoted \"directory", "étoile", "has\ttab", "has\\backslash", "--option"];
        foreach (string directory in selected) { f.Write(directory + "/file.txt", directory + "\n"); }
        f.Write("nested/root.txt", "parent file\n");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Literal sparse directories");
        f.Write("seed.txt", "dirty staged\n"); await f.Repository.StageAllAsync(); f.Write("seed.txt", "dirty unstaged\n");
        string indexPath = await GitPath(f.Repository, "index"); byte[] before = await File.ReadAllBytesAsync(indexPath);
        string bits = (await Git(f.Repository, "ls-files", "-t")).StandardOutput;
        await f.Repository.SaveSparseCheckoutSettingsAsync(true, true, true, selected);
        GitSparseCheckoutState saved = await f.Repository.GetSparseCheckoutAsync();
        Check(saved.Enabled && saved.ConeMode && saved.SparseIndex && saved.Patterns.ToHashSet().SetEquals(selected), "Saved cone settings and literal names must round-trip through native sparse-checkout list.");
        Check(before.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(indexPath)) && bits == (await Git(f.Repository, "ls-files", "-t")).StandardOutput,
            "Save-only must retain exact index bytes and SKIP_WORKTREE flags.");
        Check(File.Exists(f.File("excluded/file.txt")) && File.Exists(f.File("included/public/file.txt")) && File.ReadAllText(f.File("seed.txt")) == "dirty unstaged\n",
            "Save-only must not remove files or discard staged/unstaged work.");
        await f.Repository.ReapplySparseCheckoutAsync();
        Check(selected.All(directory => File.Exists(f.File(directory + "/file.txt"))) && File.Exists(f.File("nested/root.txt"))
            && !File.Exists(f.File("excluded/file.txt")) && !File.Exists(f.File("included/public/file.txt")), "Reapply must actually apply the saved cone, including shallow parent files.");
        Check(File.ReadAllText(f.File("seed.txt")) == "dirty unstaged\n" && (await Git(f.Repository, "show", ":seed.txt")).StandardOutput == "dirty staged\n",
            "Applying the saved cone must retain unrelated staged and unstaged bytes.");
    }
    private static async Task PatternsAsync()
    {
        using RepositoryFixture f = await CreateAsync(); await f.Repository.SetSparseCheckoutAsync(["included"], sparseIndex: true);
        f.Write("included/public/file.txt", "dirty public\n");
        string index = await GitPath(f.Repository, "index"); byte[] before = await File.ReadAllBytesAsync(index);
        string[] rules = ["/*", "!/included/private/", "!/included/public/"];
        await f.Repository.SaveSparseCheckoutSettingsAsync(true, false, true, rules);
        GitSparseCheckoutState saved = await f.Repository.GetSparseCheckoutAsync();
        Check(saved.Enabled && !saved.ConeMode && !saved.SparseIndex && saved.Patterns.SequenceEqual(rules), "Non-cone settings force a full index preference and preserve explicit rules.");
        Check(before.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(index)) && !File.Exists(f.File("excluded/file.txt"))
            && File.Exists(f.File("included/private/file.txt")), "Mode and rules save alone must not convert an existing sparse index or change its checkout.");
        await f.Repository.ReapplySparseCheckoutAsync();
        Check(File.Exists(f.File("excluded/file.txt")) && !File.Exists(f.File("included/private/file.txt"))
            && File.ReadAllText(f.File("included/public/file.txt")) == "dirty public\n", "Reapply must follow saved non-cone rules while preserving a dirty excluded file.");
    }
    private static async Task DisableAsync()
    {
        using RepositoryFixture f = await CreateAsync(); await f.Repository.SetSparseCheckoutAsync(["included"], sparseIndex: true);
        string index = await GitPath(f.Repository, "index"); byte[] before = await File.ReadAllBytesAsync(index);
        await f.Repository.SaveSparseCheckoutSettingsAsync(false, true, true, ["included"]);
        GitSparseCheckoutState saved = await f.Repository.GetSparseCheckoutAsync();
        Check(!saved.Enabled && saved.ConeMode && saved.Patterns.SequenceEqual(["included"]), "Disabling save-only retains rules for a later editor.");
        Check(before.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(index)) && !File.Exists(f.File("excluded/file.txt")),
            "Saved disabled state is not proof of an already restored working copy.");
        await f.Repository.DisableSparseCheckoutAsync();
        Check(File.Exists(f.File("excluded/file.txt")) && (await f.Repository.GetStatusAsync()).Changes.Count == 0, "Explicit refresh of disabled settings restores the full clean checkout.");
    }
    private static async Task LinkedAsync()
    {
        using RepositoryFixture f = await CreateAsync();
        string linkedPath = Path.Combine(f.Root, "linked sparse"); await Git(f.Repository, "worktree", "add", "--detach", linkedPath, "HEAD");
        GitRepository linked = new(linkedPath);
        byte[] mainIndex = await File.ReadAllBytesAsync(await GitPath(f.Repository, "index"));
        byte[] linkedIndex = await File.ReadAllBytesAsync(await GitPath(linked, "index"));
        await linked.SaveSparseCheckoutSettingsAsync(true, true, false, ["included"]);
        Check(!(await f.Repository.GetSparseCheckoutAsync()).Enabled && (await linked.GetSparseCheckoutAsync()).Enabled,
            "Enabling a linked worktree must not enable the main worktree.");
        Check((await GitPath(linked, "info/sparse-checkout")) != (await GitPath(f.Repository, "info/sparse-checkout"))
            && !File.Exists(await GitPath(f.Repository, "info/sparse-checkout")), "The rules destination must be resolved with native --git-path for the exact linked worktree.");
        Check(mainIndex.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(await GitPath(f.Repository, "index")))
            && linkedIndex.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(await GitPath(linked, "index"))), "Neither checkout's index may change while saving linked settings.");
        await linked.ReapplySparseCheckoutAsync();
        Check(!File.Exists(Path.Combine(linkedPath, "excluded/file.txt")) && File.Exists(f.File("excluded/file.txt")), "Only the exact saved linked checkout is refreshed.");
    }
    private static async Task BareLinkedAsync()
    {
        using RepositoryFixture f = await CreateAsync();
        string barePath = Path.Combine(f.Root, "sparse bare.git"), linkedPath = Path.Combine(f.Root, "bare linked checkout");
        await Git(f.Repository, "clone", "--bare", f.Repository.Path, barePath);
        GitRepository bare = new(barePath);
        Check((await Git(bare, "config", "--local", "--type=bool", "--get", "core.bare")).StandardOutput == "true\n", "The real bare clone must start with core.bare=true in shared config.");
        await Git(bare, "worktree", "add", "--detach", linkedPath, "HEAD");
        GitRepository linked = new(linkedPath);
        byte[] linkedIndex = await File.ReadAllBytesAsync(await GitPath(linked, "index"));
        string refs = (await Git(bare, "show-ref")).StandardOutput;
        await linked.SaveSparseCheckoutSettingsAsync(true, true, false, ["included"]);
        Check((await Git(bare, "rev-parse", "--is-bare-repository")).StandardOutput == "true\n"
            && (await Git(linked, "rev-parse", "--is-bare-repository")).StandardOutput == "false\n", "The migration must preserve a bare main repository and a usable linked worktree.");
        Check((await Git(bare, "config", "--file", Path.Combine(barePath, "config.worktree"), "--type=bool", "--get", "core.bare")).StandardOutput == "true\n",
            "core.bare=true must move to the main config.worktree, not the linked config.");
        GitResult commonBare = await new GitRunner().RunAsync(barePath, ["config", "--local", "--get", "core.bare"]);
        Check(commonBare.ExitCode == 1 && !(await bare.GetSparseCheckoutAsync()).Enabled && (await linked.GetSparseCheckoutAsync()).Enabled,
            "The shared core.bare entry must be removed while sparse settings stay local to the linked checkout.");
        Check(linkedIndex.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(await GitPath(linked, "index")))
            && File.Exists(Path.Combine(linkedPath, "excluded/file.txt")) && !File.Exists(Path.Combine(barePath, "index")),
            "Saving metadata from a bare-backed linked checkout must not apply or invent an index.");
        await linked.ReapplySparseCheckoutAsync();
        Check(!File.Exists(Path.Combine(linkedPath, "excluded/file.txt")) && File.Exists(Path.Combine(linkedPath, "included/public/file.txt"))
            && (await Git(bare, "show-ref")).StandardOutput == refs && (await Git(bare, "rev-parse", "--is-bare-repository")).StandardOutput == "true\n",
            "The later native refresh must affect only the real linked checkout and preserve the bare repository and refs.");
    }

    private static async Task ValidationAsync()
    {
        using RepositoryFixture f = await CreateAsync();
        byte[] config = await File.ReadAllBytesAsync(f.File(".git/config"));
        await Expect<ArgumentException>(() => f.Repository.SaveSparseCheckoutSettingsAsync(true, true, false, ["../outside"]));
        await Expect<ArgumentException>(() => f.Repository.SaveSparseCheckoutSettingsAsync(true, false, false, ["bad\0rule"]));
        Check(config.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.File(".git/config"))), "Invalid edits must fail before config writes.");
        string rules = await GitPath(f.Repository, "info/sparse-checkout"); Directory.CreateDirectory(Path.GetDirectoryName(rules)!);
        await File.WriteAllTextAsync(rules + ".lock", "another writer\n");
        await Expect<IOException>(() => f.Repository.SaveSparseCheckoutSettingsAsync(true, true, false, ["included"]));
        Check(File.ReadAllText(rules + ".lock") == "another writer\n" && !File.Exists(rules)
            && config.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.File(".git/config"))), "An existing native sparse lock must survive and reject the edit before all config writes.");
        File.Delete(rules + ".lock");
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Expect<OperationCanceledException>(() => f.Repository.SaveSparseCheckoutSettingsAsync(true, true, false, ["included"], cancelled.Token));
        Check(!File.Exists(rules) && config.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.File(".git/config"))), "Cancellation before admission must not save settings or rules.");
    }
    private static async Task MainWorktreeConfigAsync()
    {
        using RepositoryFixture f = await CreateAsync();
        await Git(f.Repository, "config", "core.worktree", f.Repository.Path);
        byte[] index = await File.ReadAllBytesAsync(await GitPath(f.Repository, "index"));
        await f.Repository.SaveSparseCheckoutSettingsAsync(true, true, false, ["included"]);
        Check((await Git(f.Repository, "config", "--worktree", "--get", "core.worktree")).StandardOutput.TrimEnd('\r', '\n') == f.Repository.Path
            && File.ReadAllText(f.File(".git/config.worktree")).Contains("worktree", StringComparison.Ordinal), "The main worktree location remains valid after enabling worktree-local settings.");
        Check(index.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(await GitPath(f.Repository, "index"))) && File.Exists(f.File("excluded/file.txt")), "Config migration alone must leave index and working files untouched.");
        string linkedPath = Path.Combine(f.Root, "linked after migration"); await Git(f.Repository, "worktree", "add", "--detach", linkedPath, "HEAD");
        Check((await new GitRepository(linkedPath).GetInfoAsync()).RootPath == linkedPath, "A later linked worktree must not inherit the displaced main worktree path.");
    }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}

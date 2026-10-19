using System.Text.Json;
using GitExtensions.Linux;

internal static class GitConfigAdvancedScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("advanced Git booleans retain exact Windows aliases and unknown tristates", DecodeAsync),
        ("advanced Git fields expose actual scopes origins raw empty valueless and missing entries", SnapshotAsync),
        ("advanced Git fields publish eight canonical booleans without touching repository data", PublishAsync),
        ("advanced Git raw unknown noops keep bytes and reject noncanonical changed values", RawNoopAsync),
        ("advanced Git duplicates and included-only unset reject the whole mixed batch", RestrictedAsync),
        ("advanced Git stale source bytes reject writes and preserve external settings", StaleAsync),
        ("advanced Git updateRefs capability is enforced by snapshot and native writer", UpdateRefsVersionAsync),
        ("advanced Git partial Apply retains a successful local prefix and global native failure", PartialAsync),
        ("advanced Git pre-cancellation starts no writer", CancelAsync),
        ("advanced fetch prune is consumed by a real native fetch without extra flags", NativePruneAsync)
    ];
    private static readonly GitConfigurationField[] Fields = [GitConfigurationField.PullRebase, GitConfigurationField.FetchPrune,
        GitConfigurationField.MergeAutoStash, GitConfigurationField.RebaseAutoStash, GitConfigurationField.RebaseAutoSquash,
        GitConfigurationField.RebaseUpdateRefs, GitConfigurationField.RerereEnabled, GitConfigurationField.RerereAutoUpdate];
    private static readonly string[] Keys = ["pull.rebase", "fetch.prune", "merge.autostash", "rebase.autostash", "rebase.autosquash", "rebase.updaterefs", "rerere.enabled", "rerere.autoupdate"];

    private static Task DecodeAsync()
    {
        Check(Fields.Select(GitConfigurationFields.Key).SequenceEqual(Keys), "Only the eight original keys are exposed in the original order.");
        foreach (string raw in new[] { "true", "yes", "on", "1" }) { Check(GitConfigurationFields.AdvancedBooleanState(raw) == true, "The original true aliases remain checked."); }
        foreach (string raw in new[] { "false", "no", "off", "0", "" }) { Check(GitConfigurationFields.AdvancedBooleanState(raw) == false, "The original false aliases, including empty, remain unchecked."); }
        foreach (string? raw in new[] { null, "TRUE", "False", "merges", "interactive", " true ", "unknown" })
        { Check(GitConfigurationFields.AdvancedBooleanState(raw) is null, "Unknown and valueless inputs stay indeterminate without normalization."); }
        Check(Enum.GetValues<GitConfigurationField>().Where(GitConfigurationFields.IsAdvancedBoolean).SequenceEqual(Fields), "The advanced whitelist and enum expose exactly these eight fields.");
        return Task.CompletedTask;
    }

    private static async Task SnapshotAsync()
    {
        using Fixture f = await Fixture.Create();
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", null);
        foreach (string key in Keys)
        { await f.Git("config", "--file", Path.Combine(f.Root, "system-config"), key, "on"); await f.Git("config", "--file", f.Global, key, "yes"); await f.Git("config", key, "off"); }
        await f.Git("config", "--unset", "rerere.autoupdate");
        await File.AppendAllTextAsync(f.Config, "\n[rerere]\n autoupdate\n[rebase]\n autosquash = \"\"\n");
        GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
        foreach (GitConfigurationField field in Fields)
        {
            GitToolConfigurationValue value = snapshot.GetScope(GitToolConfigurationView.Local).GetSetting(field);
            Check(value.Occurrences.Count >= 1 && value.Occurrences.All(entry => entry.Scope == "local" && entry.ResolvedOriginPath == f.Config), "Native origins remain attached to each raw entry.");
            Check(!snapshot.GetScope(GitToolConfigurationView.Effective).GetSetting(field).CanChange
                && !snapshot.GetScope(GitToolConfigurationView.System).GetSetting(field).CanChange, "Effective and System are explicitly read-only.");
            Check(snapshot.GetScope(GitToolConfigurationView.Effective).GetSetting(field).Occurrences.Select(entry => entry.Scope).Take(2).SequenceEqual(["system", "global"]), "Effective retains System then Global precedence.");
        }
        Check(snapshot.GetScope(GitToolConfigurationView.Local).GetSetting(GitConfigurationField.RerereAutoUpdate).Occurrences.Single().Value is null,
            "Present valueless is distinct from a missing entry.");
        Check(snapshot.GetScope(GitToolConfigurationView.Local).GetSetting(GitConfigurationField.RebaseAutoSquash).Occurrences.Last().Value == "", "Explicit empty remains distinct from valueless and missing.");
        await f.Git("config", "--unset-all", "rerere.autoupdate");
        Check((await f.Repository.GetToolConfigurationAsync()).GetScope(GitToolConfigurationView.Local).GetSetting(GitConfigurationField.RerereAutoUpdate).Occurrences.Count == 0,
            "A later snapshot observes native absence without changing the captured snapshot.");
    }

    private static async Task PublishAsync()
    {
        using Fixture f = await Fixture.Create();
        const string unknown = "\n# byte-exact comment λ\n[unrelated]\n value = first\n value = second\n";
        await File.AppendAllTextAsync(f.Config, unknown); string head = await f.Repository.ResolveRevisionAsync("HEAD");
        byte[] index = await File.ReadAllBytesAsync(f.File(".git/index"));
        GitToolConfigurationSaveResult saved = await f.Repository.SaveToolConfigurationAsync(await f.Repository.GetToolConfigurationAsync(),
            [Local(Fields.Select((field, i) => new GitConfigurationFieldEdit(field, i % 2 == 0 ? "true" : "false")).ToArray())]);
        Check(saved.Success && saved.Writes.Count == 8 && saved.Writes.All(write => write.NativeResult?.Success == true && write.Outcome == GitToolConfigurationWriteOutcome.NativeCompleted), "Each boolean has a real successful native receipt.");
        foreach ((GitConfigurationField field, int indexOfField) in Fields.Select((field, i) => (field, i)))
        {
            string expected = indexOfField % 2 == 0 ? "true" : "false";
            Check((await f.Git("config", "--type=bool", "--get", GitConfigurationFields.Key(field))).StandardOutput.TrimEnd('\n') == expected, "Git's native boolean parser consumes the saved value.");
        }
        Check((await File.ReadAllTextAsync(f.Config)).Contains(unknown, StringComparison.Ordinal)
            && index.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.File(".git/index"))) && head == await f.Repository.ResolveRevisionAsync("HEAD"),
            "Unknown bytes, HEAD and index survive the native setting writes.");
        GitToolConfigurationSaveResult unset = await f.Repository.SaveToolConfigurationAsync(saved.After!, [Local(Fields.Select(field => new GitConfigurationFieldEdit(field, null)).ToArray())]);
        Check(unset.Success && unset.Writes.Count == 8 && unset.Writes.All(write => write.Arguments.Contains("--unset"))
            && Fields.All(field => unset.After!.GetScope(GitToolConfigurationView.Local).GetSetting(field).Occurrences.Count == 0), "Each of the eight original keys also has a real scoped native unset receipt.");
    }

    private static async Task RawNoopAsync()
    {
        using Fixture f = await Fixture.Create();
        string[] raw = ["merges", "TRUE", "future-mode", "yes", "on", "1", "", "off"];
        for (int i = 0; i < Fields.Length; i++) { await f.Git("config", Keys[i], raw[i]); }
        byte[] before = await File.ReadAllBytesAsync(f.Config);
        GitToolConfigurationSaveResult noop = await f.Repository.SaveToolConfigurationAsync(await f.Repository.GetToolConfigurationAsync(),
            [Local(Fields.Select((field, i) => new GitConfigurationFieldEdit(field, raw[i])).ToArray())]);
        Check(noop.MutationStarted == false && noop.Writes.All(write => write.Outcome == GitToolConfigurationWriteOutcome.NoOp)
            && before.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.Config)), "Untouched aliases and unknown raw values are never canonicalized.");
        foreach (GitConfigurationField field in Fields)
        {
            GitToolConfigurationSaveException rejected = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(noop.After!, [Local(new GitConfigurationFieldEdit(field, "unexpected-new-value"))]));
            Check(rejected.Result.MutationStarted == false && rejected.Result.Writes.Count == 0, "New noncanonical values fail before any writer.");
        }
        GitToolConfigurationSaveResult unset = await f.Repository.SaveToolConfigurationAsync(noop.After!, [Local(new GitConfigurationFieldEdit(GitConfigurationField.PullRebase, null))]);
        Check(unset.After!.GetScope(GitToolConfigurationView.Local).GetSetting(GitConfigurationField.PullRebase).Occurrences.Count == 0
            && unset.Writes.Single().Arguments.Contains("--unset"), "Explicit unset removes only this native scoped entry.");
    }

    private static async Task RestrictedAsync()
    {
        using Fixture f = await Fixture.Create(); string included = Path.Combine(f.Root, "included.conf");
        await File.WriteAllTextAsync(included, "[fetch]\n prune = true\n"); await f.Git("config", "include.path", included);
        await f.Git("config", "--add", "rerere.enabled", "true"); await f.Git("config", "--add", "rerere.enabled", "false");
        byte[] before = await File.ReadAllBytesAsync(f.Config), external = await File.ReadAllBytesAsync(included);
        GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
        Check(!snapshot.GetScope(GitToolConfigurationView.Local).GetSetting(GitConfigurationField.RerereEnabled).CanChange, "Duplicates are explicitly restricted.");
        foreach (GitConfigurationFieldEdit edit in new GitConfigurationFieldEdit[] { new(GitConfigurationField.FetchPrune, null), new(GitConfigurationField.RerereEnabled, "true") })
        {
            GitToolConfigurationSaveException error = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(snapshot,
                [new(GitConfigScope.Local, [], [new(GitConfigurationField.UserName, "must-not-write"), edit])]));
            Check(error.Result.MutationStarted == false && error.Result.Writes.Count == 0, "An unsafe advanced change rejects even an earlier valid common-field write.");
        }
        Check(before.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.Config)) && external.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(included)), "Restrictions preserve native and included files.");
    }

    private static async Task StaleAsync()
    {
        using Fixture f = await Fixture.Create(); GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
        await File.AppendAllTextAsync(f.Config, "\n# concurrent external edit\n"); byte[] external = await File.ReadAllBytesAsync(f.Config);
        GitToolConfigurationSaveException error = await Expect<GitToolConfigurationSaveException>(() => f.Repository.SaveToolConfigurationAsync(snapshot, [Local(new GitConfigurationFieldEdit(GitConfigurationField.PullRebase, "true"))]));
        Check(error.Result.MutationStarted == false && external.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.Config)), "The shared CAS rejects even a concurrent comment change.");
    }

    private static async Task UpdateRefsVersionAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.Create(); await f.Git("config", "rebase.updaterefs", "true");
        GitRepository old = await f.Wrapped("if a==['--version']:\n print('git version 2.37.3')\n sys.exit(0)\n", Path.Combine(f.Root, "versions.jsonl"));
        GitToolConfigurationSnapshot snapshot = await old.GetToolConfigurationAsync();
        Check(!snapshot.SupportsRebaseUpdateRefs && !snapshot.GetScope(GitToolConfigurationView.Local).GetSetting(GitConfigurationField.RebaseUpdateRefs).CanChange, "Git 2.37 exposes a read-only existing updateRefs value.");
        GitToolConfigurationSaveResult noop = await old.SaveToolConfigurationAsync(snapshot, [Local(new GitConfigurationFieldEdit(GitConfigurationField.RebaseUpdateRefs, "true"))]);
        Check(noop.Writes.Single().Outcome == GitToolConfigurationWriteOutcome.NoOp, "The unsupported existing raw value may remain untouched.");
        foreach (string? value in new[] { "false", null })
        {
            GitToolConfigurationSaveException error = await Expect<GitToolConfigurationSaveException>(() => old.SaveToolConfigurationAsync(noop.After!, [Local(new GitConfigurationFieldEdit(GitConfigurationField.RebaseUpdateRefs, value))]));
            Check(error.Result.MutationStarted == false && error.Result.Writes.Count == 0, "The writer also rejects changing or unsetting updateRefs before Git 2.38.");
        }
        GitRepository current = await f.Wrapped("if a==['--version']:\n print('git version 2.38.0')\n sys.exit(0)\n", Path.Combine(f.Root, "supported.jsonl"));
        GitToolConfigurationSnapshot supported = await current.GetToolConfigurationAsync();
        Check(supported.SupportsRebaseUpdateRefs && supported.GetScope(GitToolConfigurationView.Local).GetSetting(GitConfigurationField.RebaseUpdateRefs).CanChange, "The exact Git 2.38 boundary enables the setting.");
    }

    private static async Task PartialAsync()
    {
        using Fixture f = await Fixture.Create(); await File.WriteAllTextAsync(f.Global + ".lock", "foreign-lock\n");
        GitToolConfigurationSaveException error = await Expect<GitToolConfigurationSaveException>(async () => await f.Repository.SaveToolConfigurationAsync(await f.Repository.GetToolConfigurationAsync(),
            [Local(new GitConfigurationFieldEdit(GitConfigurationField.PullRebase, "true")), new(GitConfigScope.Global, [], [new(GitConfigurationField.FetchPrune, "true")])]));
        Check(error.Result.Writes.Count == 2 && error.Result.Writes[0].Outcome == GitToolConfigurationWriteOutcome.NativeCompleted
            && error.Result.Writes[1].Outcome == GitToolConfigurationWriteOutcome.NativeFailed && error.Result.MayHaveChanged,
            "A real foreign Git lock preserves the local success and later global failure receipts.");
        Check(error.Result.After!.GetScope(GitToolConfigurationView.Local).GetSetting(GitConfigurationField.PullRebase).Occurrences.Single().Value == "true"
            && !File.Exists(f.Global) && await File.ReadAllTextAsync(f.Global + ".lock") == "foreign-lock\n", "Partial readback does not rollback or remove a foreign lock.");
    }

    private static async Task CancelAsync()
    {
        using Fixture f = await Fixture.Create(); GitToolConfigurationSnapshot snapshot = await f.Repository.GetToolConfigurationAsync();
        byte[] before = await File.ReadAllBytesAsync(f.Config); using CancellationTokenSource cancel = new(); cancel.Cancel();
        GitToolConfigurationSaveCanceledException error = await Expect<GitToolConfigurationSaveCanceledException>(() => f.Repository.SaveToolConfigurationAsync(snapshot, [Local(new GitConfigurationFieldEdit(GitConfigurationField.FetchPrune, "true"))], cancel.Token));
        Check(error.Result.MutationStarted == false && !error.Result.MayHaveChanged && before.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(f.Config)), "Pre-cancellation leaves the native files unchanged.");
    }

    private static async Task NativePruneAsync()
    {
        using Fixture f = await Fixture.Create(); string bare = Path.Combine(f.Root, "remote.git");
        await f.Git("init", "--bare", bare); await f.Git("remote", "add", "origin", bare);
        await f.Git("push", "origin", "HEAD:refs/heads/topic"); await f.Git("fetch", "origin");
        await f.Git("--git-dir=" + bare, "update-ref", "-d", "refs/heads/topic");
        await f.Repository.SaveToolConfigurationAsync(await f.Repository.GetToolConfigurationAsync(), [Local(new GitConfigurationFieldEdit(GitConfigurationField.FetchPrune, "true"))]);
        await f.Git("fetch", "origin"); GitResult gone = await new GitRunner().RunAsync(f.Repository.Path, ["show-ref", "--verify", "--quiet", "refs/remotes/origin/topic"]);
        Check(gone.ExitCode == 1, "Native fetch consumes fetch.prune from the saved config without a --prune override.");
    }

    private static GitToolConfigurationScopeEdit Local(params GitConfigurationFieldEdit[] settings) => new(GitConfigScope.Local, [], settings);
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<string, string?> _environment;
        private Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "gitextensions-config-fields-" + Guid.NewGuid().ToString("N"));
            string home = Path.Combine(Root, "home"), xdg = Path.Combine(Root, "xdg"); Directory.CreateDirectory(home); Directory.CreateDirectory(xdg);
            Global = Path.Combine(home, ".gitconfig");
            _environment = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(IsPrivateVariable).ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
            foreach (string name in _environment.Keys) { Environment.SetEnvironmentVariable(name, null); }
            Environment.SetEnvironmentVariable("HOME", home); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdg);
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Global); Environment.SetEnvironmentVariable("GIT_CONFIG_SYSTEM", Path.Combine(Root, "system-config"));
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1"); Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow"); Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
        }
        public string Root { get; }
        public string Global { get; }
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

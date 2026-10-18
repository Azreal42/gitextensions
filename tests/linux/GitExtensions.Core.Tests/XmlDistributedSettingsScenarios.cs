using System.Text;
using System.Xml;
using GitCommands;
using GitExtensions.Linux;

internal static class XmlDistributedSettingsScenarios
{
    private const string Remote = "Detailed.GetRemoteBranchesDirectlyFromRemote";
    private const string AddLog = "Detailed.AddMergeLogMessages";
    private const string Count = "Detailed.MergeLogMessagesCount";
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("XML distributed resolution uses the highest non-null raw value including empty and invalid data", ResolutionAsync),
        ("XML explicit scopes save independently and unsetting reveals inheritance without staging Git files", ExplicitAsync),
        ("XML Effective routing matches Windows and never writes Distributed", EffectiveAsync),
        ("XML snapshots plans and captured save intentions are immutable", ImmutableAsync),
        ("XML no-op saves preserve exact bytes modes mtimes and missing files", NoopAsync),
        ("XML shared Dictionary and generic actors serialize and reject a stale peer without losing unknown keys", PeersAsync),
        ("XML baseline guards reject creation deletion same-byte replacement and mode changes", StaleAsync),
        ("XML followed symlinks hardlinks and readonly modes retain their real endpoints", LinksAsync),
        ("XML malformed duplicate and DTD data remain visible and untouched", InvalidAsync),
        ("XML nonregular blocked and unreadable paths report IO without publishing defaults", IoAsync),
        ("XML precancellation writes nothing and cancellation after witnessed writing drains readback", CancellationAsync),
        ("XML real linked worktrees bare repositories and no-repository profiles expose only their actual scopes", EndpointsAsync),
        ("XML generic storage preserves nil empty Unicode and unknown Dictionary business values opaquely", OpaqueAsync),
        ("XML sequential destination saves acknowledge partial effects and retry only the remaining intent", PartialAsync)
    ];

    private static async Task ResolutionAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string global = Global(f);
        GitRepositoryInfo info = await f.Repository.GetInfoAsync();
        Write(global, (Remote, "true"), (Count, "7"), ("global", "kept"));
        Write(Path.Combine(info.RootPath, GitDistributedSettingsSnapshot.FileName), (Remote, "false"), (Count, "-900"));
        Write(Path.Combine(info.CommonDirectory, GitDistributedSettingsSnapshot.FileName), (Remote, ""), (Count, "invalid"), ("nil", null));
        GitDistributedSettingsSnapshot state = await GitDistributedSettingsSnapshot.LoadAsync(global, info);
        Check(state.GetValue(Remote) == "" && state.GetValue(Count) == "invalid" && state.GetSource(Remote) == GitDistributedSettingsScope.Local,
            "Highest raw empty/invalid values mask valid lower values; the generic resolver does not parse business values.");
        Check(state.GetValue(Remote, GitDistributedSettingsScope.Distributed) == "false" && state.GetValue(Count, GitDistributedSettingsScope.Global) == "7"
            && state.GetValue("absent") is null && state.GetSource("absent") is null && state.Local!.Values.ContainsKey("nil"), "Explicit views remain independent and nil is retained opaquely.");
        state = await Apply(state, GitDistributedSettingsScope.Local, Edits((Remote, null), (Count, null)));
        Check(state.GetValue(Remote) == "false" && state.GetValue(Count) == "-900" && state.GetSource(Count) == GitDistributedSettingsScope.Distributed,
            "Only null removal reveals the lower raw value, with no automatic coercion or numeric clamp.");
    }

    private static async Task ExplicitAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepositoryInfo info = await f.Repository.GetInfoAsync();
        byte[] index = await File.ReadAllBytesAsync(f.File(".git/index")); string head = await f.Repository.ResolveRevisionAsync("HEAD");
        GitDistributedSettingsSnapshot state = await GitDistributedSettingsSnapshot.LoadAsync(Global(f), info);
        state = await Apply(state, GitDistributedSettingsScope.Global, Edits((Remote, "false"), (Count, "20")));
        state = await Apply(state, GitDistributedSettingsScope.Distributed, Edits((Remote, "true"), (Count, "4")));
        state = await Apply(state, GitDistributedSettingsScope.Local, Edits((Remote, "false"), (Count, "2")));
        Check(state.GetValue(Count) == "2" && state.GetValue(Count, GitDistributedSettingsScope.Distributed) == "4" && state.GetValue(Count, GitDistributedSettingsScope.Global) == "20",
            "Every explicit scope writes only its native XML endpoint.");
        state = await Apply(state, GitDistributedSettingsScope.Local, Edits((Count, null)));
        Check(state.GetValue(Count) == "4" && !WindowsRead(state.Local!.Path).ContainsKey(Count), "Unset removes the actual XML key, not an empty value.");
        Check(Enumerable.SequenceEqual(index, await File.ReadAllBytesAsync(f.File(".git/index"))) && head == await f.Repository.ResolveRevisionAsync("HEAD"),
            "Saving distributed XML does not stage it, touch another index entry or commit anything.");
    }

    private static async Task EffectiveAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepositoryInfo info = await f.Repository.GetInfoAsync();
        (string? Global, string? Distributed, string? Local, string? Value, GitDistributedSettingsScope? Target)[] cases =
        [
            (null, null, null, "true", GitDistributedSettingsScope.Global),
            ("false", null, null, "true", GitDistributedSettingsScope.Global),
            ("false", null, "false", "true", GitDistributedSettingsScope.Local),
            ("false", "false", null, "true", GitDistributedSettingsScope.Local),
            ("false", "true", null, "true", null),
            ("true", "false", "", "false", GitDistributedSettingsScope.Local),
            ("true", null, null, null, GitDistributedSettingsScope.Global),
            ("false", "true", null, null, null),
            ("false", "true", "false", null, GitDistributedSettingsScope.Local)
        ];
        foreach (var row in cases)
        {
            // Null in this routing table means an absent key, not an explicitly
            // serialized nil item (whose deliberate removal is covered separately).
            Write(Global(f), row.Global is null ? [] : [(Remote, row.Global)]);
            Write(Path.Combine(info.RootPath, GitDistributedSettingsSnapshot.FileName), row.Distributed is null ? [] : [(Remote, row.Distributed)]);
            Write(Path.Combine(info.CommonDirectory, GitDistributedSettingsSnapshot.FileName), row.Local is null ? [] : [(Remote, row.Local)]);
            GitDistributedSettingsSnapshot state = await GitDistributedSettingsSnapshot.LoadAsync(Global(f), info);
            byte[] distributed = await File.ReadAllBytesAsync(state.Distributed!.Path);
            IReadOnlyList<GitDistributedSettingsDestination> plan = state.PlanChanges(GitDistributedSettingsScope.Effective, Edits((Remote, row.Value)));
            Check(row.Target is null ? plan.Count == 0 : plan.Count == 1 && plan[0].Scope == row.Target, "Effective raw routing must match the real Windows source rules.");
            Check(plan.All(part => part.Scope != GitDistributedSettingsScope.Distributed), "Effective never edits shared repository policy.");
            foreach (GitDistributedSettingsDestination part in plan) { _ = await GitXmlSettingsStore.SaveChangesAsync(part.Snapshot, part.Intentions); }
            Check(Enumerable.SequenceEqual(distributed, await File.ReadAllBytesAsync(state.Distributed!.Path)), "Every effective scenario retains the exact distributed bytes.");
        }
    }

    private static async Task ImmutableAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); Write(Global(f), (Remote, "false"));
        GitDistributedSettingsSnapshot state = await GitDistributedSettingsSnapshot.LoadAsync(Global(f));
        Expect<NotSupportedException>(() => ((IDictionary<string, string?>)state.Global.Values).Add("injected", "no"));
        Dictionary<string, string?> input = Edits((Remote, "true"));
        IReadOnlyList<GitDistributedSettingsDestination> plan = state.PlanChanges(GitDistributedSettingsScope.Global, input);
        input[Remote] = "later caller mutation";
        Check(plan.Single().Intentions[Remote] == "true", "The plan owns its intents rather than exposing the caller dictionary.");
        Expect<NotSupportedException>(() => ((IDictionary<string, string?>)plan.Single().Intentions).Add("injected", "no"));
        Dictionary<string, string?> writes = Edits((Remote, "true"));
        Task<GitXmlSettingsSnapshot> save = GitXmlSettingsStore.SaveChangesAsync(state.Global, writes); writes[Remote] = "mutated after invocation";
        GitXmlSettingsSnapshot saved = await save;
        Check(saved.Values[Remote] == "true" && state.Global.Values[Remote] == "false", "Saving captures supplied intents and never mutates its baseline snapshot.");
    }

    private static async Task NoopAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string path = Global(f);
        GitXmlSettingsSnapshot missing = await GitXmlSettingsStore.LoadAsync(path);
        _ = await GitXmlSettingsStore.SaveChangesAsync(missing, Edits(("absent", null)));
        Check(!File.Exists(path) && !Directory.Exists(Path.GetDirectoryName(path)), "A read and no-op unset do not create a profile directory.");
        Write(path, (Remote, "false"), ("empty", ""));
        string original = (await File.ReadAllTextAsync(path)).Replace("<dictionary>", "<dictionary><!-- retain original layout -->", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, original, new UTF8Encoding(false));
        DateTime timestamp = new(2003, 2, 1, 1, 2, 3, DateTimeKind.Utc); File.SetLastWriteTimeUtc(path, timestamp);
        byte[] bytes = await File.ReadAllBytesAsync(path); GitXmlSettingsSnapshot before = await GitXmlSettingsStore.LoadAsync(path);
        _ = await GitXmlSettingsStore.SaveChangesAsync(before, Edits((Remote, "false"), ("empty", ""), ("absent", null)));
        Check(Enumerable.SequenceEqual(bytes, await File.ReadAllBytesAsync(path)) && File.GetLastWriteTimeUtc(path) == timestamp,
            "A no-op retains exact comments, layout, encoding and mtime without serialization.");
    }

    private static async Task PeersAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitDictionarySettingsSnapshot dictionary = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        Write(dictionary.LocalPath, ("dictionary", "en-US"), ("peer", "retained"));
        dictionary = await f.Repository.ReadEffectiveDictionaryAsync("en-US"); GitXmlSettingsSnapshot xml = await GitXmlSettingsStore.LoadAsync(dictionary.LocalPath);
        Task<bool> a = Outcome(() => f.Repository.WriteLocalDictionaryAsync(dictionary, "fr-FR"));
        Task<bool> b = Outcome(() => GitXmlSettingsStore.SaveChangesAsync(xml, Edits((Count, "3"))));
        bool[] results = await Task.WhenAll(a, b);
        Check(results.Count(result => result) == 1, "Shared file locks and strict baselines permit one actor and refuse the stale peer.");
        GitDictionarySettingsSnapshot fresh = await f.Repository.ReadEffectiveDictionaryAsync("en-US");
        _ = await f.Repository.WriteLocalDictionaryAsync(fresh, "fr-FR");
        GitXmlSettingsSnapshot current = await GitXmlSettingsStore.LoadAsync(dictionary.LocalPath);
        GitXmlSettingsSnapshot saved = await GitXmlSettingsStore.SaveChangesAsync(current, Edits((Count, "3")));
        Check(saved.Values["dictionary"] == "fr-FR" && saved.Values[Count] == "3" && saved.Values["peer"] == "retained", "Retrying with fresh snapshots retains both real actor effects and unknown values.");
    }

    private static async Task StaleAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string path = Global(f);
        GitXmlSettingsSnapshot expected = await GitXmlSettingsStore.LoadAsync(path); Write(path, (Remote, "false"));
        await ExpectAsync<IOException>(() => GitXmlSettingsStore.SaveChangesAsync(expected, Edits((Remote, "true"))));
        expected = await GitXmlSettingsStore.LoadAsync(path); File.Delete(path);
        await ExpectAsync<IOException>(() => GitXmlSettingsStore.SaveChangesAsync(expected, Edits((Remote, "true"))));
        Write(path, (Remote, "false")); expected = await GitXmlSettingsStore.LoadAsync(path);
        string replacement = path + ".replacement"; File.Copy(path, replacement); File.Move(replacement, path, true);
        await ExpectAsync<IOException>(() => GitXmlSettingsStore.SaveChangesAsync(expected, Edits((Remote, "true"))));
        expected = await GitXmlSettingsStore.LoadAsync(path); Write(path, (Remote, "false"), ("peer", "changed"));
        await ExpectAsync<IOException>(() => GitXmlSettingsStore.SaveChangesAsync(expected, Edits((Remote, "false"))));
        if (!OperatingSystem.IsWindows())
        {
            expected = await GitXmlSettingsStore.LoadAsync(path); UnixFileMode mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode ^ UnixFileMode.GroupRead);
            await ExpectAsync<IOException>(() => GitXmlSettingsStore.SaveChangesAsync(expected, Edits((Remote, "true"))));
            File.SetUnixFileMode(path, mode);
        }
        Check(WindowsRead(path)["peer"] == "changed", "A rejected stale or even no-op request does not discard another actor's data.");
    }

    private static async Task LinksAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string path = f.File("settings-link"), target = Path.Combine(f.Root, "target Ω"), hard = Path.Combine(f.Root, "hard Ω");
        Write(target, (Remote, "false"), ("peer", "retained"));
        Check((await new GitRunner("ln").RunAsync(f.Repository.Path, ["--", target, hard])).Success, "A real hardlink supplies a distinct pathname to the same inode.");
        File.CreateSymbolicLink(path, target); const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.GroupRead; File.SetUnixFileMode(target, mode);
        GitXmlSettingsSnapshot one = await GitXmlSettingsStore.LoadAsync(path), two = await GitXmlSettingsStore.LoadAsync(hard);
        bool[] results = await Task.WhenAll(Outcome(() => GitXmlSettingsStore.SaveChangesAsync(one, Edits((Remote, "true")))),
            Outcome(() => GitXmlSettingsStore.SaveChangesAsync(two, Edits((Count, "3")))));
        Check(results.Count(result => result) == 1 && new FileInfo(path).LinkTarget == target
            && File.ReadAllBytes(hard).SequenceEqual(File.ReadAllBytes(target)) && File.GetUnixFileMode(target) == mode,
            "Hardlink aliases share the writer gate, a stale peer fails, followed links and readonly permissions survive.");
        GitXmlSettingsSnapshot before = await GitXmlSettingsStore.LoadAsync(path); string other = target + ".other"; File.Copy(target, other);
        File.Delete(path); File.CreateSymbolicLink(path, other);
        await ExpectAsync<IOException>(() => GitXmlSettingsStore.SaveChangesAsync(before, Edits((Remote, "false"))));
    }

    private static async Task InvalidAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string path = Global(f); Write(path, (Remote, "true"));
        GitXmlSettingsSnapshot good = await GitXmlSettingsStore.LoadAsync(path);
        foreach (string bad in new[] { "", "<dictionary>", "<wrong />", "<dictionary><unknown /></dictionary>",
            "<dictionary><item><key><string>x</string></key></item></dictionary>",
            "<!DOCTYPE dictionary [<!ENTITY injected 'no'>]><dictionary>&injected;</dictionary>",
            "<dictionary><item><key><string>x</string></key><value><string>a</string></value></item><item><key><string>x</string></key><value><string>b</string></value></item></dictionary>" })
        {
            await File.WriteAllTextAsync(path, bad); byte[] bytes = await File.ReadAllBytesAsync(path);
            await ExpectAsync<XmlException>(() => GitXmlSettingsStore.LoadAsync(path));
            await ExpectAsync<XmlException>(() => GitXmlSettingsStore.SaveChangesAsync(good, Edits((Remote, "false"))));
            Check(Enumerable.SequenceEqual(bytes, await File.ReadAllBytesAsync(path)), "A malformed source is not replaced by defaults or partial parse results.");
        }
    }

    private static async Task IoAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string directory = f.File("nonregular"); Directory.CreateDirectory(directory);
        await ExpectAsync<IOException>(() => GitXmlSettingsStore.LoadAsync(directory));
        string blocker = f.File("blocker"); await File.WriteAllTextAsync(blocker, "a file");
        await ExpectAsync<IOException>(() => GitXmlSettingsStore.LoadAsync(Path.Combine(blocker, "settings")));
        if (!OperatingSystem.IsWindows())
        {
            string link = f.File("cycle"); File.CreateSymbolicLink(link, link);
            await ExpectAsync<IOException>(() => GitXmlSettingsStore.LoadAsync(link));
        }
        Check(await File.ReadAllTextAsync(blocker) == "a file" && Directory.GetFileSystemEntries(directory).Length == 0,
            "IO/path diagnostics do not create a replacement profile or change its parent.");
    }

    private static async Task CancellationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string path = Global(f); Write(path, (Remote, "false"));
        GitXmlSettingsSnapshot expected = await GitXmlSettingsStore.LoadAsync(path); byte[] original = await File.ReadAllBytesAsync(path);
        using CancellationTokenSource before = new(); before.Cancel();
        await ExpectAsync<OperationCanceledException>(() => GitXmlSettingsStore.LoadAsync(path, before.Token));
        await ExpectAsync<OperationCanceledException>(() => GitXmlSettingsStore.SaveChangesAsync(expected, Edits((Remote, "true")), before.Token));
        Check(Enumerable.SequenceEqual(original, await File.ReadAllBytesAsync(path)), "Precancelled read/write performs no publication.");
        using CancellationTokenSource during = new(); Task<GitXmlSettingsSnapshot>? save = null;
        TaskCompletionSource<bool> witnessed = new(TaskCreationOptions.RunContinuationsAsynchronously); int observed = 0;
        using FileSystemWatcher watcher = new(Path.GetDirectoryName(path)!, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite };
        watcher.Changed += (_, _) =>
        {
            if (Interlocked.CompareExchange(ref observed, 1, 0) != 0) { return; }
            try { bool pending = save?.IsCompleted == false; during.Cancel(); witnessed.TrySetResult(pending); }
            catch (Exception error) { witnessed.TrySetException(error); }
        };
        watcher.EnableRaisingEvents = true;
        string body = new('λ', 4 * 1024 * 1024);
        save = GitXmlSettingsStore.SaveChangesAsync(expected, Edits((Remote, "true"), ("large", body)), during.Token);
        GitXmlSettingsSnapshot saved = await save;
        Check(await witnessed.Task.WaitAsync(TimeSpan.FromSeconds(10)) && during.IsCancellationRequested
            && saved.Values[Remote] == "true" && saved.Values["large"] == body && WindowsRead(path)["large"] == body,
            "Cancellation after the actual write event occurs while owned work is pending and full serialization/readback drains before success.");
    }

    private static async Task EndpointsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepositoryInfo main = await f.Repository.GetInfoAsync(); string global = Global(f);
        string linkedPath = Path.Combine(f.Root, "linked Ω");
        // Native fixture isolates endpoint semantics from the separately owned worktree dialog workflow.
        _ = await f.Repository.RunAsync(["worktree", "add", "--detach", "--", linkedPath, "HEAD"]);
        GitRepository linkedRepository = new(linkedPath); GitRepositoryInfo linked = await linkedRepository.GetInfoAsync();
        GitDistributedSettingsSnapshot a = await GitDistributedSettingsSnapshot.LoadAsync(global, main), b = await GitDistributedSettingsSnapshot.LoadAsync(global, linked);
        Check(a.Local!.Path == b.Local!.Path && a.Distributed!.Path != b.Distributed!.Path, "Linked worktrees share CommonDirectory local settings, with distinct actual working-tree settings.");
        a = await Apply(a, GitDistributedSettingsScope.Local, Edits((Remote, "true")));
        a = await Apply(a, GitDistributedSettingsScope.Distributed, Edits((Count, "3")));
        b = await GitDistributedSettingsSnapshot.LoadAsync(global, linked); b = await Apply(b, GitDistributedSettingsScope.Distributed, Edits((Count, "4")));
        Check(b.GetValue(Remote) == "true" && b.GetValue(Count) == "4" && (await GitDistributedSettingsSnapshot.LoadAsync(global, main)).GetValue(Count) == "3",
            "Real XML publication keeps linked distributed policies isolated and local overrides shared.");
        GitRepository bare = await GitRepository.InitAsync(Path.Combine(f.Root, "bare.git"), bare: true);
        GitDistributedSettingsSnapshot bareSettings = await GitDistributedSettingsSnapshot.LoadAsync(global, await bare.GetInfoAsync());
        Check(bareSettings.IsAvailable(GitDistributedSettingsScope.Local) && !bareSettings.IsAvailable(GitDistributedSettingsScope.Distributed)
            && bareSettings.Local!.Path == Path.Combine(bare.Path, GitDistributedSettingsSnapshot.FileName), "Bare exposes its actual local Git endpoint and excludes an aliased distributed file.");
        Expect<InvalidOperationException>(() => bareSettings.PlanChanges(GitDistributedSettingsScope.Distributed, Edits((Remote, "true"))));
        GitDistributedSettingsSnapshot outside = await GitDistributedSettingsSnapshot.LoadAsync(global);
        Check(outside.Local is null && outside.Distributed is null && outside.PlanChanges(GitDistributedSettingsScope.Effective, Edits((Remote, "true"))).Single().Scope == GitDistributedSettingsScope.Global,
            "No-repository profiles retain Global without discovering or fabricating a parent repository.");
    }

    private static async Task OpaqueAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string path = Global(f);
        Write(path, ("dictionary", "../unknown and invalid dictionary"), ("nil", null), ("empty", ""), ("未知<&", "λ\n& <value>"));
        GitXmlSettingsSnapshot original = await GitXmlSettingsStore.LoadAsync(path);
        Check(original.Values.ContainsKey("nil") && original.Values["nil"] is null && original.Values["dictionary"] == "../unknown and invalid dictionary",
            "Generic XML never interprets dictionary identities or discards nil/empty business data.");
        GitXmlSettingsSnapshot saved = await GitXmlSettingsStore.SaveChangesAsync(original, Edits((Count, "2147483647")));
        XmlSerializableDictionary<string, string?> windows = WindowsRead(path);
        Check(windows["nil"] is null && windows["empty"] == "" && windows["未知<&"] == "λ\n& <value>" && windows[Count] == "2147483647"
            && saved.Values["dictionary"] == original.Values["dictionary"], "Original Windows serialization preserves opaque values and full Int32 data when another key changes.");
        saved = await GitXmlSettingsStore.SaveChangesAsync(saved, Edits(("nil", null)));
        Check(!saved.Values.ContainsKey("nil"), "An explicit null intention removes even an existing nil key.");
    }

    private static async Task PartialAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepositoryInfo info = await f.Repository.GetInfoAsync();
        Write(Global(f), (Count, "3")); Write(Path.Combine(info.CommonDirectory, GitDistributedSettingsSnapshot.FileName), (Remote, "false"));
        GitDistributedSettingsSnapshot state = await GitDistributedSettingsSnapshot.LoadAsync(Global(f), info);
        Dictionary<string, string?> intentions = Edits((Remote, "true"), (Count, "5"));
        IReadOnlyList<GitDistributedSettingsDestination> plan = state.PlanChanges(GitDistributedSettingsScope.Effective, intentions);
        Check(plan.Count == 2 && plan[0].Scope == GitDistributedSettingsScope.Local && plan[1].Scope == GitDistributedSettingsScope.Global, "The resolver exposes independent ordered destinations, not a pretend transaction.");
        GitXmlSettingsSnapshot local = await GitXmlSettingsStore.SaveChangesAsync(plan[0].Snapshot, plan[0].Intentions);
        state = state.WithSaved(plan[0].Scope, local); Write(Global(f), (Count, "7"), ("peer", "keep"));
        await ExpectAsync<IOException>(() => GitXmlSettingsStore.SaveChangesAsync(plan[1].Snapshot, plan[1].Intentions));
        Check(WindowsRead(local.Path)[Remote] == "true" && WindowsRead(Global(f))[Count] == "7", "The completed first effect and the second actor's data survive a later stale refusal.");
        state = new(await GitXmlSettingsStore.LoadAsync(Global(f)), state.Local, state.Distributed);
        IReadOnlyList<GitDistributedSettingsDestination> retry = state.PlanChanges(GitDistributedSettingsScope.Effective, intentions);
        Check(retry.Count == 1 && retry[0].Scope == GitDistributedSettingsScope.Global, "Acknowledging the completed snapshot prevents replaying its old intention.");
        state = await Apply(state, GitDistributedSettingsScope.Effective, intentions);
        Check(state.GetValue(Count) == "5" && state.GetValue(Remote) == "true" && state.Global.Values["peer"] == "keep", "Refreshing only the failed destination preserves peer keys and completes the remaining save.");
        Expect<ArgumentException>(() => state.WithSaved(GitDistributedSettingsScope.Global, local));
    }

    private static string Global(RepositoryFixture f) => Path.Combine(f.Root, "profile", GitDistributedSettingsSnapshot.FileName);
    private static Dictionary<string, string?> Edits(params (string Key, string? Value)[] pairs) => pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    private static void Write(string path, params (string Key, string? Value)[] pairs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); XmlSerializableDictionary<string, string?> values = [];
        foreach (var pair in pairs) { values.Add(pair.Key, pair.Value); }
        using XmlTextWriter writer = new(path, Encoding.UTF8) { Formatting = Formatting.Indented };
        writer.WriteStartDocument(); writer.WriteStartElement("dictionary"); values.WriteXml(writer); writer.WriteEndElement();
    }
    private static XmlSerializableDictionary<string, string?> WindowsRead(string path)
    {
        XmlSerializableDictionary<string, string?> result = [];
        using XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { IgnoreWhitespace = true }); result.ReadXml(reader); return result;
    }
    private static async Task<GitDistributedSettingsSnapshot> Apply(GitDistributedSettingsSnapshot state, GitDistributedSettingsScope scope, IReadOnlyDictionary<string, string?> changes)
    {
        foreach (GitDistributedSettingsDestination part in state.PlanChanges(scope, changes))
        { state = state.WithSaved(part.Scope, await GitXmlSettingsStore.SaveChangesAsync(part.Snapshot, part.Intentions)); }
        return state;
    }
    private static async Task<bool> Outcome(Func<Task> operation)
    { try { await operation(); return true; } catch (IOException) { return false; } }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}

using System.Text;
using GitExtensions.Linux;

internal static class RemoteSettingsScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("remote settings snapshots retain ordered literal URLs origins disabled remotes and immutable lists", ReadAsync),
        ("remote settings edits preserve extra URLs unknown multivalues refspecs prune tags and Putty data", MultivaluesAsync),
        ("remote settings add and native rename retain refs tracking and current checkout", AddRenameAsync),
        ("remote native transitions preserve inherited tracking and retain Git's duplicate scalar behavior", NativeTrackingTransitionsAsync),
        ("remote activation preserves every setting while applying native removal and retaining local branches", ActivationAsync),
        ("remote settings distinguish dotted neighboring names during snapshot rename activation and removal", DottedNamesAsync),
        ("remote settings refuse name collisions inactive collisions invalid edits and stale snapshots", ValidationAsync),
        ("remote settings override inherited fetch and push URL lists using actual native reset semantics", InheritedAsync),
        ("remote settings preserve global configuration and reject destructive inherited operations", GlobalAsync),
        ("remote settings detect external included changes even when effective values are unchanged", IncludeConflictAsync),
        ("remote settings retain native config bytes BOM comments EOL and Unix modes", BytesModesAsync),
        ("remote settings refuse undecodable configuration values without replacing their original bytes", InvalidEncodingAsync),
        ("remote settings preserve existing Git locks and leave failed publications unchanged", LocksAsync),
        ("remote settings preserve legitimate config symlinks and refuse retargeted symlinks", SymlinkAsync),
        ("remote settings serialize optimistic saves across instances and linked worktrees", SharedAsync),
        ("branch tracking validates immediately supports dot and future branches and clears fields independently", TrackingAsync),
        ("branch tracking preserves inherited scalars and refuses unreplaceable inherited merge lists", InheritedTrackingAsync),
        ("remote settings work in bare repositories and pre-cancellation never mutates configuration", BareCancelledAsync),
        ("remote settings cancellation joins native preparation processes and leaves repository bytes intact", LiveCancellationAsync),
        ("native remote rename reports actual partial config changes when a ref lock blocks completion", PartialRenameAsync),
    ];

    private static async Task ReadAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await Add(f.Repository, "remote.origin.url", "second path/été '$();");
        await Add(f.Repository, "remote.origin.pushurl", "push-one"); await Add(f.Repository, "remote.origin.pushurl", "push-last");
        await Add(f.Repository, "-remote.Sleep.url", "inactive"); await Add(f.Repository, "-remote.Sleep.custom", "one"); await Add(f.Repository, "-remote.Sleep.custom", "two");
        IReadOnlyList<GitRemoteSettingsSnapshot> all = await f.Repository.GetRemoteSettingsAsync();
        GitRemoteSettingsSnapshot origin = all.Single(s => s.Name == "origin");
        Check(origin.Url == f.Server.Path && origin.PushUrl == "push-last" && origin.HasSeparatePushUrl && origin.FetchUrls.Count == 2,
            "Windows' first fetch/last explicit push fields retain their complete ordered lists.");
        Check(origin.EffectivePushUrls.SequenceEqual(["push-one", "push-last"]) && origin.Settings.All(s => s.Scope == "local" && Path.IsPathRooted(s.Origin)),
            "Native effective destinations and normalized configuration origins are retained.");
        GitRemoteSettingsSnapshot inactive = all.Single(s => s.Name == "Sleep");
        Check(inactive.Disabled && inactive.Settings.Count == 3 && !inactive.HasSeparatePushUrl, "Inactive sections are visible with unknown duplicate values intact.");
        await Expect<NotSupportedException>(() => { ((IList<string>)origin.FetchUrls)[0] = "forged"; return Task.CompletedTask; });
    }

    private static async Task MultivaluesAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await Add(f.Repository, "remote.origin.url", "fetch-two"); await Add(f.Repository, "remote.origin.pushurl", "push-one"); await Add(f.Repository, "remote.origin.pushurl", "push-two");
        foreach ((string key, string value) in new[] { ("custom", "first"), ("custom", "second"), ("fetch", "^refs/heads/private/*"), ("push", "refs/heads/a:refs/heads/b"),
            ("prune", "true"), ("prunetags", "false"), ("tagopt", "--no-tags"), ("puttykeyfile", "legacy key.ppk") })
        { await Add(f.Repository, "remote.origin." + key, value); }
        GitRemoteSettingsSnapshot before = await Origin(f.Repository);
        GitRemoteSettingsSnapshot saved = await f.Repository.SaveRemoteSettingsAsync(before, new("origin", "  fetch-new  ", "  push-new  ", "team/", "#123456"));
        Check(saved.FetchUrls.SequenceEqual(["fetch-new", "fetch-two"]) && saved.PushUrls.SequenceEqual(["push-one", "push-new"]),
            "Editing only the first fetch and last push preserves every additional URL.");
        Check(saved.Prefix == "team/" && saved.Color == "#123456" && saved.PuttySshKey == "legacy key.ppk", "Advanced values persist without replacing PuTTY data.");
        foreach (string variable in new[] { "custom", "fetch", "push", "prune", "prunetags", "tagopt", "puttykeyfile" })
        { Check(Values(before, variable).SequenceEqual(Values(saved, variable)), "Untouched variable changed: " + variable); }
        saved = await f.Repository.SaveRemoteSettingsAsync(saved, new("origin", saved.Url, "  " + saved.Url + "  ", saved.Prefix, saved.Color));
        Check(!saved.HasSeparatePushUrl && saved.EffectivePushUrls.SequenceEqual(saved.FetchUrls), "A push URL equal to fetch restores the complete native fallback list.");
    }

    private static async Task AddRenameAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        GitRemoteSettingsSnapshot added = await f.Repository.SaveRemoteSettingsAsync(null, new("new/été", f.Server.Path, Prefix: "team/", Color: "#010203"));
        Check(Values(added, "fetch").Single() == "+refs/heads/*:refs/remotes/new/été/*", "Creation uses Git's standard native fetch mapping.");
        GitRemoteSettingsSnapshot origin = await Origin(f.Repository);
        await f.Repository.SetConfigAsync("branch.main.pushRemote", "origin"); await f.Repository.SetConfigAsync("remote.pushDefault", "origin");
        origin = await Origin(f.Repository);
        string head = await f.Repository.ResolveRevisionAsync("HEAD");
        GitRemoteSettingsSnapshot renamed = await f.Repository.SaveRemoteSettingsAsync(origin, new("renamed/été", origin.Url, Prefix: "prefix/", Color: "#ff0000"));
        Check(renamed.Name == "renamed/été" && (await f.Repository.GetReferencesAsync()).Any(r => r.Name == "refs/remotes/renamed/été/main"), "Native rename moves fetched refs.");
        IReadOnlyList<KeyValuePair<string, string>> config = await f.Repository.GetConfigAsync();
        Check(config.Single(s => s.Key == "branch.main.remote").Value == renamed.Name && config.Single(s => s.Key == "branch.main.pushremote").Value == renamed.Name
            && config.Single(s => s.Key == "remote.pushdefault").Value == renamed.Name, "Native rename updates pull, branch push and default push remote settings.");
        Check(await f.Repository.ResolveRevisionAsync("HEAD") == head && (await f.Repository.GetStatusAsync()).Branch == "main", "Rename never changes checkout or HEAD.");
        await f.Repository.RemoveRemoteSettingsAsync(added = (await f.Repository.GetRemoteSettingsAsync()).Single(s => s.Name == added.Name));
        Check((await f.Repository.GetRemoteSettingsAsync()).All(s => s.Name != added.Name), "Native removal removes the selected configuration.");
    }

    private static async Task ActivationAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await Add(f.Repository, "remote.origin.custom", "one"); await Add(f.Repository, "remote.origin.custom", "two");
        await Add(f.Repository, "remote.origin.url", "extra"); await Add(f.Repository, "remote.origin.push", "a:b");
        GitRemoteSettingsSnapshot before = await Origin(f.Repository);
        GitRemoteSettingsSnapshot disabled = await f.Repository.SetRemoteEnabledAsync(before, false);
        Check(disabled.Disabled && disabled.Settings.Count == before.Settings.Count && Values(disabled, "custom").SequenceEqual(["one", "two"]),
            "All remote entries move to -remote, including duplicate unknown values.");
        Check((await f.Repository.GetReferencesAsync()).All(r => r.Name != "refs/remotes/origin/main")
            && (await f.Repository.GetBranchTrackingSettingsAsync()).Single(s => s.LocalBranch == "main").Remote is null
            && await f.Repository.ResolveRevisionAsync("main") == f.Initial, "Disabling applies native cleanup but keeps local branches and their commits.");
        await Expect<GitRemoteSettingsException>(() => f.Repository.SaveRemoteSettingsAsync(disabled, new("origin", disabled.Url)));
        GitRemoteSettingsSnapshot active = await f.Repository.SetRemoteEnabledAsync(disabled, true);
        Check(!active.Disabled && active.FetchUrls.SequenceEqual(before.FetchUrls) && Values(active, "push").SequenceEqual(Values(before, "push")), "Activation restores all settings exactly.");
        Check((await f.Repository.GetBranchTrackingSettingsAsync()).Single(s => s.LocalBranch == "main").Remote is null, "Activation does not invent tracking removed by native Git.");
        disabled = await f.Repository.SetRemoteEnabledAsync(active, false);
        await f.Repository.RemoveRemoteSettingsAsync(disabled);
        Check((await f.Repository.GetRemoteSettingsAsync()).Count == 0, "Removing an inactive remote only removes its inactive section.");
    }

    private static async Task NativeTrackingTransitionsAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Repository.UnsetConfigAsync("branch.main.remote"); await f.Repository.UnsetConfigAsync("branch.main.merge");
        string include = Path.Combine(f.Root.Root, "native-tracking.conf");
        await ConfigFile(f.Repository, include, "branch.main.remote", "origin"); await ConfigFile(f.Repository, include, "branch.main.merge", "refs/heads/main");
        await ConfigFile(f.Repository, include, "remote.pushDefault", "origin"); await f.Repository.SetConfigAsync("include.path", include);
        await Add(f.Repository, "branch.unrelated.remote", "origin"); await Add(f.Repository, "branch.unrelated.remote", "other");
        await Add(f.Repository, "branch.unrelated.merge", "refs/heads/keep");
        byte[] included = await File.ReadAllBytesAsync(include);
        GitRemoteSettingsSnapshot before = await Origin(f.Repository);
        GitRemoteSettingsSnapshot renamed = await f.Repository.SaveRemoteSettingsAsync(before, new("renamed", before.Url));
        Check((await f.Repository.GetBranchTrackingSettingsAsync()).Single(s => s.LocalBranch == "main").Remote == "renamed"
            && (await f.Repository.GetConfigAsync("^remote\\.pushdefault$")).Last().Value == "renamed", "Native rename creates the actual local overrides for inherited tracking settings.");
        Check((await f.Repository.GetConfigAsync("^branch\\.unrelated\\.remote$")).Select(s => s.Value).SequenceEqual(["origin", "other"]),
            "Scalars with duplicate values that native Git retains do not create a false optimistic-concurrency failure.");
        await f.Repository.SetRemoteEnabledAsync(renamed, false);
        Check(BytesEqual(included, await File.ReadAllBytesAsync(include)) && (await f.Repository.GetBranchTrackingSettingsAsync()).Single(s => s.LocalBranch == "main").Remote == "origin",
            "Native removal retains the inherited source and faithfully exposes any inherited tracking value that reappears.");
    }

    private static async Task ValidationAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await Add(f.Repository, "-remote.inactive.url", "held");
        GitRemoteSettingsSnapshot origin = await Origin(f.Repository);
        byte[] bytes = await File.ReadAllBytesAsync(f.Config);
        await Expect<GitRemoteSettingsException>(() => f.Repository.SaveRemoteSettingsAsync(null, new("origin", "other")));
        await Expect<GitRemoteSettingsException>(() => f.Repository.SaveRemoteSettingsAsync(origin, new("inactive", origin.Url)));
        await Expect<GitCommandException>(() => f.Repository.SaveRemoteSettingsAsync(origin, new("bad..name", origin.Url)));
        await Expect<ArgumentException>(() => f.Repository.SaveRemoteSettingsAsync(origin, new("origin", " \t")));
        Check(BytesEqual(bytes, await File.ReadAllBytesAsync(f.Config)), "Validation failures leave all configuration bytes unchanged.");
        await f.Repository.SetConfigAsync("remote.origin.prefix", "external/");
        GitRemoteSettingsException stale = await Expect<GitRemoteSettingsException>(() => f.Repository.SaveRemoteSettingsAsync(origin, new("origin", "new")));
        Check(!stale.HasRepositoryChanges && (await Origin(f.Repository)).Prefix == "external/", "Stale snapshots refuse rather than overwriting external edits.");
        await Add(f.Repository, "-remote.origin.url", "collision");
        origin = await Origin(f.Repository);
        await Expect<GitRemoteSettingsException>(() => f.Repository.SetRemoteEnabledAsync(origin, false));
        Check((await f.Repository.GetRemoteSettingsAsync()).Count(s => s.Name == "origin") == 2, "Activation collision preserves both existing sections.");
    }

    private static async Task DottedNamesAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Repository.SaveRemoteSettingsAsync(null, new("team", f.Server.Path));
        await f.Repository.SaveRemoteSettingsAsync(null, new("team.foo", f.Server.Path));
        await Add(f.Repository, "remote.team.foo.custom", "neighbor-one"); await Add(f.Repository, "remote.team.foo.custom", "neighbor-two");
        IReadOnlyList<GitRemoteSettingsSnapshot> all = await f.Repository.GetRemoteSettingsAsync();
        GitRemoteSettingsSnapshot team = all.Single(s => s.Name == "team"), neighbor = all.Single(s => s.Name == "team.foo");
        string[] original = neighbor.Settings.Select(s => s.Key + "=" + s.Value).ToArray();
        Check(team.Settings.All(s => !s.Key.Contains(".foo.", StringComparison.Ordinal)), "The shorter remote cannot absorb a dotted neighbor's configuration.");
        GitRemoteSettingsSnapshot disabled = await f.Repository.SetRemoteEnabledAsync(team, false);
        Check(original.SequenceEqual((await f.Repository.GetRemoteSettingsAsync()).Single(s => s.Name == "team.foo").Settings.Select(s => s.Key + "=" + s.Value)), "Disabling the shorter name retains the neighbor intact.");
        team = await f.Repository.SetRemoteEnabledAsync(disabled, true);
        team = await f.Repository.SaveRemoteSettingsAsync(team, new("renamed.team", team.Url));
        disabled = await f.Repository.SetRemoteEnabledAsync(team, false);
        await f.Repository.RemoveRemoteSettingsAsync(disabled);
        Check(original.SequenceEqual((await f.Repository.GetRemoteSettingsAsync()).Single(s => s.Name == "team.foo").Settings.Select(s => s.Key + "=" + s.Value)), "Native rename and inactive removal never rename, disable or remove the neighboring section.");
    }

    private static async Task InheritedAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string include = Path.Combine(f.Root.Root, "included config");
        foreach ((string key, string value) in new[] { ("url", "fetch-one"), ("url", "fetch-two"), ("pushurl", "push-one"), ("pushurl", "push-two"), ("prefix", "inherited/"), ("color", "#abcdef"), ("custom", "keep") })
        { await ConfigFile(f.Repository, include, "remote.inherited." + key, value); }
        await f.Repository.SetConfigAsync("include.path", include);
        byte[] original = await File.ReadAllBytesAsync(include);
        GitRemoteSettingsSnapshot inherited = (await f.Repository.GetRemoteSettingsAsync()).Single(s => s.Name == "inherited");
        Check(!inherited.CanModifyStructure, "Inherited remote structure is explicitly restricted.");
        GitRemoteSettingsSnapshot saved = await f.Repository.SaveRemoteSettingsAsync(inherited, new("inherited", "fetch-replaced", "push-replaced"));
        Check(saved.FetchUrls.SequenceEqual(["fetch-replaced", "fetch-two"]) && saved.PushUrls.SequenceEqual(["push-one", "push-replaced"]), "Untouched inherited URL entries retain their order.");
        Check((await NativeUrls(f.Repository, "inherited", false)).SequenceEqual(saved.FetchUrls)
            && (await NativeUrls(f.Repository, "inherited", true)).SequenceEqual(saved.PushUrls), "Native Git sees exactly the intended fetch and push lists after empty resets.");
        Check(saved.Prefix == "" && saved.Color == "" && Values(saved, "custom").Single() == "keep", "Clearing inherited scalars overrides them locally without deleting unrelated inherited entries.");
        saved = await f.Repository.SaveRemoteSettingsAsync(saved, new("inherited", saved.Url));
        Check(!saved.HasSeparatePushUrl && (await NativeUrls(f.Repository, "inherited", true)).SequenceEqual(saved.FetchUrls), "Resetting inherited push URLs restores only the intended fetch fallback.");
        Check(BytesEqual(original, await File.ReadAllBytesAsync(include)), "The included file is never modified.");
        GitRemoteSettingsException rejected = await Expect<GitRemoteSettingsException>(() => f.Repository.RemoveRemoteSettingsAsync(saved));
        Check(!rejected.HasRepositoryChanges, "A local editor does not remove a section that would reappear from its include.");
    }

    private static async Task GlobalAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string global = Path.Combine(f.Root.Root, "global config");
        await ConfigFile(f.Repository, global, "remote.global.url", "global-first"); await ConfigFile(f.Repository, global, "remote.global.url", "global-second");
        await ConfigFile(f.Repository, global, "remote.global.pushurl", "global-push"); await ConfigFile(f.Repository, global, "remote.global.color", "#010101");
        string? previous = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global);
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(global);
            GitRemoteSettingsSnapshot snapshot = (await f.Repository.GetRemoteSettingsAsync()).Single(s => s.Name == "global");
            GitRemoteSettingsSnapshot saved = await f.Repository.SaveRemoteSettingsAsync(snapshot, new("global", "local-first", "local-push"));
            Check((await NativeUrls(f.Repository, "global", false)).SequenceEqual(["local-first", "global-second"])
                && (await NativeUrls(f.Repository, "global", true)).SequenceEqual(["local-push"]), "Global lists are overridden with Git-native reset semantics.");
            await Expect<GitRemoteSettingsException>(() => f.Repository.SaveRemoteSettingsAsync(saved, new("other", saved.Url, saved.PushUrl)));
            await Expect<GitRemoteSettingsException>(() => f.Repository.SetRemoteEnabledAsync(saved, false));
            Check(BytesEqual(bytes, await File.ReadAllBytesAsync(global)) && saved.Color == "", "Global file bytes stay unchanged, and null scalar clears override locally.");
        }
        finally { Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previous); }
    }

    private static async Task IncludeConflictAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string included = Path.Combine(f.Root.Root, "guard.conf"); await File.WriteAllTextAsync(included, "# first\n[custom]\n value = same\n");
        await f.Repository.SetConfigAsync("include.path", included);
        GitRemoteSettingsSnapshot before = await Origin(f.Repository);
        await File.AppendAllTextAsync(included, "# externally changed comment\n");
        byte[] config = await File.ReadAllBytesAsync(f.Config);
        await Expect<GitRemoteSettingsException>(() => f.Repository.SaveRemoteSettingsAsync(before, new("origin", "new")));
        Check(BytesEqual(config, await File.ReadAllBytesAsync(f.Config)), "Even a source-file byte change with identical effective settings invalidates the editing snapshot.");
    }

    private static async Task BytesModesAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        byte[] comment = [0x23, 0x20, 0xe9, 0x20, 0x0d, 0x0a];
        byte[] original = await File.ReadAllBytesAsync(f.Config);
        byte[] bytes = [0xef, 0xbb, 0xbf, .. comment, .. original];
        await File.WriteAllBytesAsync(f.Config, bytes);
        UnixFileMode? mode = null;
        if (!OperatingSystem.IsWindows()) { mode = UnixFileMode.UserRead | UnixFileMode.GroupRead; File.SetUnixFileMode(f.Config, mode.Value); }
        GitRemoteSettingsSnapshot before = await Origin(f.Repository);
        GitRemoteSettingsSnapshot saved = await f.Repository.SaveRemoteSettingsAsync(before, new("origin", before.Url, Prefix: "new/"));
        byte[] after = await File.ReadAllBytesAsync(f.Config);
        Check(after.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }) && Contains(after, comment), "Native rewriting preserves the UTF8 BOM and untouched legacy-encoded comment/EOL bytes.");
        if (!OperatingSystem.IsWindows()) { Check(File.GetUnixFileMode(f.Config) == mode, "Read-only Unix mode is preserved by guarded atomic publication."); }
        Check(saved.Prefix == "new/", "A read-only original can be updated using the writable Git directory without chmod of the old inode.");
    }

    private static async Task LocksAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        GitRemoteSettingsSnapshot before = await Origin(f.Repository);
        string existing = f.Config + ".lock"; await File.WriteAllTextAsync(existing, "other process");
        byte[] bytes = await File.ReadAllBytesAsync(f.Config);
        GitRemoteSettingsException error = await Expect<GitRemoteSettingsException>(() => f.Repository.SaveRemoteSettingsAsync(before, new("origin", "changed")));
        Check(!error.HasRepositoryChanges && await File.ReadAllTextAsync(existing) == "other process" && BytesEqual(bytes, await File.ReadAllBytesAsync(f.Config)),
            "Existing Git locks are neither removed nor bypassed and no partial configuration is published.");
    }

    private static async Task InvalidEncodingAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        byte[] before = await File.ReadAllBytesAsync(f.Config);
        byte[] invalid = [.. before, .. Encoding.ASCII.GetBytes("\n[remote \"legacy\"]\n url = "), 0xe9, 0x0a];
        await File.WriteAllBytesAsync(f.Config, invalid);
        await Expect<DecoderFallbackException>(() => f.Repository.GetRemoteSettingsAsync());
        Check(BytesEqual(invalid, await File.ReadAllBytesAsync(f.Config)), "An invalid text value cannot be silently decoded and rewritten with replacement characters.");
    }

    private static async Task SymlinkAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using Fixture f = await Fixture.CreateAsync();
        string target = Path.Combine(f.Root.Root, "external config"); File.Move(f.Config, target); File.CreateSymbolicLink(f.Config, target);
        GitRemoteSettingsSnapshot snapshot = await Origin(f.Repository);
        snapshot = await f.Repository.SaveRemoteSettingsAsync(snapshot, new("origin", snapshot.Url, Prefix: "through-link/"));
        Check(new FileInfo(f.Config).LinkTarget == target && (await File.ReadAllTextAsync(target)).Contains("through-link/", StringComparison.Ordinal), "Saving retains a legitimate symlink and updates its actual target.");
        string other = Path.Combine(f.Root.Root, "replacement config"); File.Copy(target, other);
        File.Delete(f.Config); File.CreateSymbolicLink(f.Config, other);
        byte[] priorTarget = await File.ReadAllBytesAsync(target), replacement = await File.ReadAllBytesAsync(other);
        await Expect<GitRemoteSettingsException>(() => f.Repository.SaveRemoteSettingsAsync(snapshot, new("origin", "must-not-write")));
        Check(BytesEqual(priorTarget, await File.ReadAllBytesAsync(target)) && BytesEqual(replacement, await File.ReadAllBytesAsync(other)), "Retargeting refuses both paths without overwriting either target.");
    }

    private static async Task SharedAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string linkedPath = Path.Combine(f.Root.Root, "linked worktree"); await f.Repository.AddWorktreeAsync(linkedPath, "HEAD", newBranch: "linked");
        GitRepository linked = await GitRepository.OpenAsync(linkedPath), second = await GitRepository.OpenAsync(f.Repository.Path);
        GitRemoteSettingsSnapshot baseline = await Origin(f.Repository);
        async Task<bool> Save(GitRepository repository, string url)
        { try { await repository.SaveRemoteSettingsAsync(baseline, new("origin", url)); return true; } catch (GitRemoteSettingsException e) when (!e.HasRepositoryChanges) { return false; } }
        bool[] outcomes = await Task.WhenAll(Save(linked, "linked-winner"), Save(second, "main-winner"));
        Check(outcomes.Count(value => value) == 1 && (await Origin(f.Repository)).Url is "linked-winner" or "main-winner", "Instances sharing a common Git directory serialize, and only one stale-baseline save wins.");
        string[] linkedUrls = await NativeUrls(linked, "origin", false), mainUrls = await NativeUrls(second, "origin", false);
        Check(linkedUrls.SequenceEqual(mainUrls), "Linked worktrees read the same common remote configuration.");
    }

    private static async Task TrackingAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string refs = (await f.Repository.RunAsync(["show-ref"])).StandardOutput;
        GitBranchTrackingSnapshot tracking = (await f.Repository.GetBranchTrackingSettingsAsync()).Single(s => s.LocalBranch == "main");
        tracking = await f.Repository.SetBranchTrackingMergeAsync(tracking, null);
        tracking = await f.Repository.SetBranchTrackingRemoteAsync(tracking, ".");
        Check(tracking.Remote == "." && tracking.MergeRef == "refs/heads/main", "Selecting a remote fills a missing merge destination with the local name.");
        tracking = await f.Repository.SetBranchTrackingMergeAsync(tracking, "future/été");
        Check(tracking.MergeBranch == "future/été" && tracking.MergeRef == "refs/heads/future/été", "Future remote branches do not require an existing fetched ref.");
        tracking = await f.Repository.SetBranchTrackingRemoteAsync(tracking, null);
        Check(tracking.Remote is null && tracking.MergeBranch == "future/été", "Clearing only remote retains the merge destination.");
        tracking = await f.Repository.SetBranchTrackingMergeAsync(tracking, "");
        Check(tracking.MergeRef is null && refs == (await f.Repository.RunAsync(["show-ref"])).StandardOutput, "Independent tracking edits never checkout, create or delete refs.");
        await f.Repository.SetConfigAsync("branch.main.remote", "outside");
        await Expect<GitRemoteSettingsException>(() => f.Repository.SetBranchTrackingRemoteAsync(tracking, "origin"));
    }

    private static async Task InheritedTrackingAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Repository.UnsetConfigAsync("branch.main.remote"); await f.Repository.UnsetConfigAsync("branch.main.merge");
        string include = Path.Combine(f.Root.Root, "tracking.conf"); await ConfigFile(f.Repository, include, "branch.main.remote", "origin");
        await ConfigFile(f.Repository, include, "branch.main.merge", "refs/heads/inherited"); await f.Repository.SetConfigAsync("include.path", include);
        GitBranchTrackingSnapshot before = (await f.Repository.GetBranchTrackingSettingsAsync()).Single(s => s.LocalBranch == "main");
        byte[] config = await File.ReadAllBytesAsync(f.Config);
        await Expect<GitRemoteSettingsException>(() => f.Repository.SetBranchTrackingMergeAsync(before, "replacement"));
        Check(BytesEqual(config, await File.ReadAllBytesAsync(f.Config)), "An inherited multivalued merge destination is refused before a misleading local append.");
        await Expect<GitRemoteSettingsException>(() => f.Repository.SetBranchTrackingRemoteAsync(before, null));
        Check(BytesEqual(config, await File.ReadAllBytesAsync(f.Config)), "An inherited remote cannot be safely cleared by writing an empty Git remote.");
        GitBranchTrackingSnapshot replaced = await f.Repository.SetBranchTrackingRemoteAsync(before, ".");
        string nativeUpstream = (await f.Repository.RunAsync(["for-each-ref", "--format=%(upstream:short)", "refs/heads/main"])).StandardOutput.TrimEnd('\r', '\n');
        string? actualUpstream = (await f.Repository.GetReferencesAsync()).Single(r => r.IsCurrent).Upstream;
        Check(replaced.Remote == "." && replaced.MergeBranch == "inherited" && (actualUpstream ?? "") == nativeUpstream,
            "A valid local scalar replaces the inherited remote without changing its inherited merge destination.");
    }

    private static async Task BareCancelledAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        GitRemoteSettingsSnapshot added = await f.Server.SaveRemoteSettingsAsync(null, new("bare/été", f.Repository.Path));
        Check(added.Name == "bare/été" && (await f.Server.GetBranchTrackingSettingsAsync()).Single().LocalBranch == "main", "Remote management and tracking discovery work in a bare repository.");
        byte[] bytes = await File.ReadAllBytesAsync(f.Config);
        GitRemoteSettingsSnapshot before = await Origin(f.Repository);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Expect<OperationCanceledException>(() => f.Repository.SaveRemoteSettingsAsync(before, new("origin", "cancelled"), cancelled.Token));
        await Expect<OperationCanceledException>(() => f.Repository.SetRemoteEnabledAsync(before, false, cancelled.Token));
        await Expect<OperationCanceledException>(() => f.Repository.RemoveRemoteSettingsAsync(before, cancelled.Token));
        Check(BytesEqual(bytes, await File.ReadAllBytesAsync(f.Config)), "Pre-cancelled operations cannot start config or native mutations.");
    }

    private static async Task PartialRenameAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        GitRemoteSettingsSnapshot before = await Origin(f.Repository);
        string path = f.Root.File(".git/refs/remotes/blocked/main.lock"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, "lock owner");
        GitRemoteSettingsException error = await Expect<GitRemoteSettingsException>(() => f.Repository.SaveRemoteSettingsAsync(before, new("blocked", before.Url)));
        Check(error.HasRepositoryChanges && error.EffectiveName == "blocked" && await File.ReadAllTextAsync(path) == "lock owner",
            "Native partial rename reports its changed configuration/name and never deletes an unrelated ref lock.");
        Check((await f.Repository.GetRemoteSettingsAsync()).Any(s => s.Name == "blocked") && await f.Repository.ResolveRevisionAsync("main") == f.Initial,
            "The actual native partial state remains reviewable without blind rollback or loss of local commits.");
    }

    private static async Task LiveCancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync();
        string marker = Path.Combine(f.Root.Root, "preparation-pid"), wrapper = Path.Combine(f.Root.Root, "native-git-wrapper");
        string quoted = "'" + marker.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\ncase \"$*\" in\n *.gitextensions-remotes-*--add*)\n echo $$ > " + quoted + "\n sleep 60\n ;;\nesac\nexec git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository observed = new(f.Repository.Path, new GitRunner(wrapper));
        GitRemoteSettingsSnapshot before = await Origin(observed);
        byte[] bytes = await File.ReadAllBytesAsync(f.Config);
        using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(20));
        Task<GitRemoteSettingsSnapshot> pending = observed.SaveRemoteSettingsAsync(before, new("origin", "never-published"), cancel.Token);
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (!File.Exists(marker))
            {
                if (pending.IsCompleted) { await pending; throw new InvalidOperationException("Preparation completed before its native process witness."); }
                if (DateTime.UtcNow > deadline) { throw new TimeoutException("No live native preparation witness."); }
                await Task.Delay(20);
            }
            int pid = int.Parse(await File.ReadAllTextAsync(marker), System.Globalization.CultureInfo.InvariantCulture);
            Check(LinuxProcessState.IsRunning(pid), "The fixture really owns a live native preparation process before cancellation.");
            cancel.Cancel(); await Expect<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Check(!LinuxProcessState.IsRunning(pid) && BytesEqual(bytes, await File.ReadAllBytesAsync(f.Config))
                && !Directory.EnumerateFiles(Path.GetDirectoryName(f.Config)!, ".gitextensions-remotes-*").Any(),
                "Cancellation joins native descendants, removes private preparation files and never publishes any repository config edit.");
            await f.Repository.SaveRemoteSettingsAsync(before, new("origin", before.Url, Prefix: "after-cancel/"));
        }
        finally { cancel.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
    }

    private static Task<GitResult> Add(GitRepository repository, string key, string value) => repository.RunAsync(["config", "--add", "--", key, value]);
    private static Task<GitResult> ConfigFile(GitRepository repository, string file, string key, string value)
        => repository.RunAsync(["config", "--file", file, "--add", "--", key, value]);
    private static async Task<string[]> NativeUrls(GitRepository repository, string name, bool push)
        => (await repository.RunAsync(push ? ["remote", "get-url", "--push", "--all", "--", name] : ["remote", "get-url", "--all", "--", name])).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    private static async Task<GitRemoteSettingsSnapshot> Origin(GitRepository repository) => (await repository.GetRemoteSettingsAsync()).Single(s => s.Name == "origin" && !s.Disabled);
    private static IEnumerable<string> Values(GitRemoteSettingsSnapshot snapshot, string variable)
        => snapshot.Settings.Where(s => s.Key == (snapshot.Disabled ? "-remote." : "remote.") + snapshot.Name + "." + variable).Select(s => s.Value);
    private static bool BytesEqual(byte[] first, byte[] second) => first.SequenceEqual(second);
    private static bool Contains(byte[] bytes, byte[] value) => Enumerable.Range(0, Math.Max(0, bytes.Length - value.Length + 1)).Any(i => bytes.Skip(i).Take(value.Length).SequenceEqual(value));
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T exception) { return exception; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Fixture : IDisposable
    {
        private Fixture(RepositoryFixture root, GitRepository server, string initial) { Root = root; Server = server; Initial = initial; }
        public RepositoryFixture Root { get; }
        public GitRepository Repository => Root.Repository;
        public GitRepository Server { get; }
        public string Initial { get; }
        public string Config => Root.File(".git/config");
        public static async Task<Fixture> CreateAsync()
        {
            RepositoryFixture root = await RepositoryFixture.CreateAsync();
            try
            {
                GitRepository server = await GitRepository.InitAsync(Path.Combine(root.Root, "server bare.git"), bare: true);
                await RepositoryFixture.ConfigureAsync(server); await root.Repository.AddRemoteAsync("origin", server.Path);
                await root.Repository.PushAsync("origin", "main", setUpstream: true);
                return new(root, server, await root.Repository.ResolveRevisionAsync("HEAD"));
            }
            catch { root.Dispose(); throw; }
        }
        public void Dispose() => Root.Dispose();
    }
}

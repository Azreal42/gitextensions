using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GitExtensions.Linux;

internal static class CommitInformationScenarios
{
    private const string Literal = "topic/été;$()'quoted";
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("commit information preserves separate mailmapped identities, dates, exact body and graph reader", DetailsAsync),
        ("commit information decodes legacy message without corrupting UTF8 author and committer", LegacyAsync),
        ("commit information notes follow display refs and refresh independently from immutable commit", NotesAsync),
        ("commit containment is typed, complete, symbolic and distinct from exact decorations for every scope", ContainmentAsync),
        ("commit containment tagger-date order ignores panel preferences and has no thousand-item bound", TagOrderAndUnboundedAsync),
        ("commit containment distinguishes current, detached, divergent and unborn HEAD without fake branches", HeadStatesAsync),
        ("commit information reads work in bare, linked and SHA256 repositories without touching their files", RepositoryKindsAsync),
        ("commit annotation messages bind exact captured raw tags through replacement, nesting and name collisions", AnnotationsAsync),
        ("commit description follows first parent, full identities and literal exact tag names", DescribeAsync),
        ("commit information rejects invalid identities, noncommits and cancelled requests instead of empty success", ValidationAsync),
        ("all commit information subreads disable optional locks and use no mutation workflow", NoOptionalLocksAsync),
        ("commit containment retries when a real concurrent HEAD change invalidates its snapshot", HeadChangesAsync),
        .. Enumerable.Range(0, 4).Select(kind => ($"commit information cancellation drains native reader and descendants for API{kind}",
            (Func<Task>)(() => CancellationAsync(kind)))),
    ];

    private static async Task DetailsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(false);
        f.Write(".mailmap", "Mapped Äuthor <mapped-author@example.invalid> Raw Äuthor <raw-author@example.invalid>\nMapped Committer <mapped-committer@example.invalid> Raw Committer <raw-committer@example.invalid>\n");
        f.Write("seed.txt", "before\n"); await f.Repository.StageAllAsync();
        string body = "Subject €\ncontinued subject\n\n\nBody with <markup> & literal $() 'quotes'\n\nLast line\n";
        await CommitWithIdentities(f.Repository, body);
        string hash = await f.Repository.ResolveRevisionAsync("HEAD");
        GitCommit graphBefore = (await f.Repository.GetLogAsync(1, hash)).Single();
        f.Write("seed.txt", "dirty tracked\n"); f.Write("untracked literal.txt", "untracked\n"); string before = Snapshot(f.Repository.Path);
        GitCommitDetails d = await f.Repository.GetCommitDetailsAsync(hash.ToUpperInvariant());
        Check(d.Hash == hash && d.TreeHash == await Text(f.Repository, "rev-parse", "HEAD^{tree}") && d.Parents.Count == 0, "A real root retains its complete commit/tree identity and no invented parent.");
        Check(d.Author.Name == "Mapped Äuthor" && d.Author.Email == "mapped-author@example.invalid" && d.Committer.Name == "Mapped Committer"
            && d.Committer.Email == "mapped-committer@example.invalid", "The actual repository mailmap maps author and committer separately.");
        Check(d.Author.Date == DateTimeOffset.Parse("2001-02-03T04:05:06+05:30", CultureInfo.InvariantCulture)
            && d.Author.Date.Offset == TimeSpan.FromMinutes(330) && d.Committer.Date.Offset == TimeSpan.FromHours(-4)
            && d.Author.Date != d.Committer.Date, "Both native timestamp instants and distinct offsets are retained.");
        Check(d.Subject == "Subject € continued subject" && d.Message == body && d.Body == "Body with <markup> & literal $() 'quotes'\n\nLast line\n" && d.Notes == "",
            "The exact full message is separate from the subject/body projections and absent notes.");
        Check((await f.Repository.GetCommitDetailsAsync(hash, includeNotes: false)).Notes is null, "Not loaded notes differ from loaded-empty notes.");
        GitCommit graphAfter = (await f.Repository.GetLogAsync(1, hash)).Single();
        Check(graphBefore.Hash == graphAfter.Hash && graphBefore.Body == graphAfter.Body && graphBefore.AuthorName == graphAfter.AuthorName,
            "The existing graph reader remains independent of detailed metadata.");
        Check(Snapshot(f.Repository.Path) == before, "Details do not refresh index bytes or alter dirty/untracked files, metadata or refs.");
    }

    private static async Task LegacyAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(false);
        f.Write(".mailmap", "Mapped Äuthor <mapped-author@example.invalid> Raw Äuthor <raw-author@example.invalid>\n");
        f.Write("seed.txt", "legacy\n"); await f.Repository.StageAllAsync();
        string tree = await Text(f.Repository, "write-tree");
        string prefix = $"tree {tree}\nauthor Raw Äuthor <raw-author@example.invalid> 981173106 +0530\ncommitter Raw Cömmit <raw-committer@example.invalid> 981199999 -0400\nencoding windows-1252\n\n";
        string body = "Legacy € “message”\n\nExact body £\n";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] commitBytes = [.. Encoding.UTF8.GetBytes(prefix), .. Encoding.GetEncoding(1252).GetBytes(body)];
        GitBinaryResult written = await new GitRunner().RunBinaryAsync(f.Repository.Path, ["hash-object", "-t", "commit", "-w", "--stdin"], standardInput: commitBytes);
        Check(written.Success, written.StandardError); string hash = Encoding.ASCII.GetString(written.StandardOutput).Trim();
        string before = Snapshot(f.Repository.Path); GitCommitDetails d = await f.Repository.GetCommitDetailsAsync(hash);
        Check(d.Message == body && d.Body == "Exact body £\n" && d.Subject == "Legacy € “message”" && d.Author.Name == "Mapped Äuthor" && d.Committer.Name == "Raw Cömmit",
            "A real CP1252 commit body is re-encoded once while native UTF8 contacts and mailmap stay intact.");
        Check(Snapshot(f.Repository.Path) == before, "A dangling legacy commit can be inspected without creating a ref or changing the index.");
    }

    private static async Task NotesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string hash = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.Repository.AddNoteAsync(hash, "First default note\n"); await f.Repository.AddNoteAsync(hash, "Review note €\n", notesRef: "review");
        await f.Repository.SetConfigAsync("notes.displayRef", "refs/notes/review"); string before = Snapshot(f.Repository.Path);
        GitCommitDetails first = await f.Repository.GetCommitDetailsAsync(hash);
        string native = (await Git(f.Repository, ["show", "--no-patch", "--format=format:%N", hash, "--"])).StandardOutput;
        Check(first.Notes == native && first.Notes.Contains("First default note", StringComparison.Ordinal) && first.Notes.Contains("Review note €", StringComparison.Ordinal),
            "%N follows the default and configured display namespaces, including their native headings.");
        Check(!first.Body.Contains("note", StringComparison.Ordinal) && Snapshot(f.Repository.Path) == before, "Notes stay outside the immutable message without optional index updates.");
        await f.Repository.AddNoteAsync(hash, "Changed without commit\n", replace: true); before = Snapshot(f.Repository.Path);
        GitCommitDetails second = await f.Repository.GetCommitDetailsAsync(hash);
        Check(second.Hash == first.Hash && second.Body == first.Body && second.Notes!.Contains("Changed without commit", StringComparison.Ordinal)
            && !second.Notes.Contains("First default note", StringComparison.Ordinal), "Commit-keyed caching never hides a changed note.");
        Check(Snapshot(f.Repository.Path) == before, "Refreshing notes is read-only.");
    }

    private static async Task ContainmentAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string root = await f.Repository.ResolveRevisionAsync("HEAD");
        await Git(f.Repository, ["branch", Literal, root]);
        await Git(f.Repository, ["branch", "remotes/origin/same", root]);
        f.Write("seed.txt", "later\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Later"); string tip = await f.Repository.ResolveRevisionAsync("HEAD");
        await Git(f.Repository, ["update-ref", "refs/remotes/origin/same", tip]);
        await Git(f.Repository, ["symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/same"]);
        await Git(f.Repository, ["update-ref", "refs/remotes/ori/gin/topic", tip]);
        await Git(f.Repository, ["tag", Literal, root]); await Git(f.Repository, ["tag", "descendant", tip]);
        await Git(f.Repository, ["tag", "-a", "annotated", "-m", "Root annotation", root]);
        await Git(f.Repository, ["tag", "-a", "nested", "-m", "Nested", "annotated"]);
        string blob = await Text(f.Repository, "rev-parse", "HEAD:seed.txt");
        await Git(f.Repository, ["tag", "-a", "blob-tag", "-m", "Not a commit", blob]);
        string before = Snapshot(f.Repository.Path);
        for (int mask = 0; mask <= 7; mask++)
        {
            GitCommitReferenceScope scope = (GitCommitReferenceScope)mask;
            GitCommitContainment snapshot = await f.Repository.GetCommitContainingReferencesAsync(root, scope);
            List<string> args = ["for-each-ref", "--contains=" + root, "--sort=-taggerdate", "--format=%(refname)"];
            if (scope.HasFlag(GitCommitReferenceScope.LocalBranches)) { args.Add("refs/heads/"); }
            if (scope.HasFlag(GitCommitReferenceScope.RemoteBranches)) { args.Add("refs/remotes/"); }
            if (scope.HasFlag(GitCommitReferenceScope.Tags)) { args.Add("refs/tags/"); }
            string[] expected = mask == 0 ? [] : (await Git(f.Repository, args)).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Check(snapshot.References.Select(r => r.FullName).SequenceEqual(expected), "Typed native contains matches the selected namespaces for mask" + mask);
            Check(snapshot.DetachedHead is null, "An attached HEAD is never represented as a detached pseudo-entry.");
        }
        GitCommitContainment all = await f.Repository.GetCommitContainingReferencesAsync(root);
        Check(all.CurrentBranchFullName == "refs/heads/main" && all.References.Single(r => r.IsCurrent).FullName == "refs/heads/main", "Current full branch identity is captured independently of display text.");
        Check(all.References.Single(r => r.FullName == "refs/heads/remotes/origin/same").Kind == GitCommitReferenceKind.LocalBranch
            && all.References.Single(r => r.FullName == "refs/remotes/origin/same").Kind == GitCommitReferenceKind.RemoteBranch, "A local remotes/ name cannot masquerade as a remote.");
        Check(all.References.Count(r => r.ShortName == Literal) == 2 && all.References.Where(r => r.ShortName == Literal).Select(r => r.Kind).Distinct().Count() == 2,
            "Identical literal local/tag display names retain distinct kinds and full names.");
        Check(all.References.Single(r => r.FullName == "refs/remotes/origin/HEAD").SymbolicTarget == "refs/remotes/origin/same", "Containing symbolic remote HEAD is retained, unlike QuickRef action suppression.");
        Check(all.References.Single(r => r.FullName == "refs/tags/descendant").RevisionHash == tip && !all.References.Any(r => r.FullName == "refs/tags/blob-tag"),
            "Contains includes tagged descendants but never a blob tag.");
        GitCommitReference annotated = all.References.Single(r => r.FullName == "refs/tags/annotated");
        Check(annotated.ObjectHash != root && annotated.RevisionHash == root && annotated.TaggerDate is not null, "Raw annotation and peeled commit identities remain separate.");
        Check(Snapshot(f.Repository.Path) == before, "All scope queries preserve every repository file including raw index bytes.");
    }

    private static async Task TagOrderAndUnboundedAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string hash = await f.Repository.ResolveRevisionAsync("HEAD");
        await Git(f.Repository, ["tag", "-a", "v2", "-m", "Older"], environment: new Dictionary<string, string?> { ["GIT_COMMITTER_DATE"] = "2001-01-01T00:00:00+0000" });
        await Git(f.Repository, ["tag", "-a", "v10", "-m", "Newer"], environment: new Dictionary<string, string?> { ["GIT_COMMITTER_DATE"] = "2002-01-01T00:00:00+0000" });
        await Git(f.Repository, ["tag", "light", hash]);
        string updates = string.Concat(Enumerable.Range(0, 1005).Select(i => $"create refs/tags/bulk-{i:0000} {hash}\n"));
        await Git(f.Repository, ["update-ref", "--stdin"], updates);
        string before = Snapshot(f.Repository.Path);
        foreach (GitReferenceSorting sorting in new[] { new GitReferenceSorting(GitRefsSortBy.VersionRefName, GitRefsSortOrder.Ascending), new GitReferenceSorting(GitRefsSortBy.RefName, GitRefsSortOrder.Descending) })
        {
            f.Repository.ReferenceSorting = sorting;
            GitCommitContainment result = await f.Repository.GetCommitContainingReferencesAsync(hash, GitCommitReferenceScope.Tags);
            Check(result.References.Count == 1008 && result.References[0].ShortName == "v10" && result.References[1].ShortName == "v2"
                && result.References.Skip(2).All(r => r.TaggerDate is null), "Tagger date, not panel Version/direction, orders every containing tag before display truncation.");
            Check(f.Repository.ReferenceSorting == sorting, "Commit information does not rewrite the panel preference snapshot.");
        }
        Check(Snapshot(f.Repository.Path) == before, "The unbounded reference read never packs or modifies refs or index.");
    }

    private static async Task HeadStatesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string root = await f.Repository.ResolveRevisionAsync("HEAD");
        f.Write("seed.txt", "tip\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Tip"); string tip = await f.Repository.ResolveRevisionAsync("HEAD");
        string divergent = await Text(f.Repository, "commit-tree", "HEAD^{tree}", "-m", "Unrelated root");
        await Git(f.Repository, ["checkout", "--detach", tip]); string before = Snapshot(f.Repository.Path);
        GitCommitContainment ancestor = await f.Repository.GetCommitContainingReferencesAsync(root);
        GitCommitContainment unrelated = await f.Repository.GetCommitContainingReferencesAsync(divergent);
        Check(ancestor.CurrentBranchFullName is null && ancestor.DetachedHead is { ContainsSelectedCommit: true } && ancestor.DetachedHead.Hash == tip,
            "A detached HEAD at a descendant contains the selected ancestor.");
        Check(unrelated.DetachedHead is { ContainsSelectedCommit: false } && !unrelated.References.Any(), "Unrelated existing commits are not misreported as missing or contained.");
        Check((await f.Repository.GetCommitContainingReferencesAsync(root, GitCommitReferenceScope.RemoteBranches)).DetachedHead is null
            && ancestor.References.All(r => r.FullName.StartsWith("refs/", StringComparison.Ordinal)), "Detached pseudo-entry is separate and belongs only to local-query scope.");
        Check(Snapshot(f.Repository.Path) == before, "Detached containment does not checkout or modify reflogs/index.");
        using RepositoryFixture unborn = await RepositoryFixture.CreateAsync(false);
        string emptyTree = await Text(unborn.Repository, ["hash-object", "-t", "tree", "--stdin"], "");
        string unreferenced = await Text(unborn.Repository, "commit-tree", emptyTree, "-m", "Not HEAD"); before = Snapshot(unborn.Repository.Path);
        GitCommitContainment missingHead = await unborn.Repository.GetCommitContainingReferencesAsync(unreferenced);
        Check(missingHead.CurrentBranchFullName == "refs/heads/main" && missingHead.DetachedHead is null && missingHead.References.Count == 0,
            "Unborn symbolic HEAD is not detached and an existing dangling commit has valid empty containment.");
        Check(Snapshot(unborn.Repository.Path) == before, "Unborn read leaves its index absent and no refs created.");
    }

    private static async Task RepositoryKindsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string hash = await f.Repository.ResolveRevisionAsync("HEAD");
        await Git(f.Repository, ["tag", "-a", "release", "-m", "Visible message"]);
        string barePath = Path.Combine(f.Root, "bare.git"), linkedPath = Path.Combine(f.Root, "linked");
        await Git(f.Repository, ["clone", "--bare", f.Repository.Path, barePath]);
        await Git(f.Repository, ["worktree", "add", "-b", "linked", linkedPath, hash]);
        foreach (GitRepository repository in new[] { new GitRepository(barePath), new GitRepository(linkedPath) })
        {
            string before = Snapshot(repository.Path), mainBefore = Snapshot(f.Repository.Path);
            GitCommitDetails details = await repository.GetCommitDetailsAsync(hash);
            GitCommitContainment refs = await repository.GetCommitContainingReferencesAsync(hash);
            GitReference[] tags = refs.References.Where(r => r.Kind == GitCommitReferenceKind.Tag).Select(AsReference).ToArray();
            Check(details.Hash == hash && refs.References.Any(r => r.ShortName == "release")
                && (await repository.GetAnnotatedTagMessagesAsync(hash, tags)).Single().Message == "Visible message"
                && (await repository.GetCommitDescribeAsync(hash)) is { TagName: "release", CommitDistance: null }, "All four dedicated APIs work in native bare and linked repositories.");
            Check(Snapshot(repository.Path) == before && Snapshot(f.Repository.Path) == mainBefore, "Neither linked common metadata nor bare files are changed by details reads.");
        }
        string shaPath = Path.Combine(f.Root, "sha256"); Directory.CreateDirectory(shaPath); GitRepository sha = new(shaPath);
        await Git(sha, ["init", "--object-format=sha256", "--initial-branch=main"]); await RepositoryFixture.ConfigureAsync(sha);
        await Git(sha, ["commit", "--allow-empty", "-m", "SHA256"]); string first = await sha.ResolveRevisionAsync("HEAD");
        await Git(sha, ["tag", "-a", "sha-release", "-m", "SHA256 annotation"]); await Git(sha, ["commit", "--allow-empty", "-m", "Later"]);
        string second = await sha.ResolveRevisionAsync("HEAD"), shaBefore = Snapshot(sha.Path);
        GitCommitDescription? desc = await sha.GetCommitDescribeAsync(second);
        Check(desc is { TagName: "sha-release", CommitDistance: 1 } && desc.Raw.EndsWith("-g" + second, StringComparison.Ordinal) && second.Length == 64,
            "The SHA256 description never truncates its identity to40 or mistakes the suffix for a literal tag name.");
        Check((await sha.GetCommitDetailsAsync(second)).Parents.Single() == first && (await sha.GetCommitContainingReferencesAsync(first)).References.Any(r => r.ObjectHash.Length == 64),
            "SHA256 metadata and raw refs remain algorithm-neutral.");
        Check(Snapshot(sha.Path) == shaBefore, "SHA256 reads leave every repository byte unchanged.");
    }

    private static async Task AnnotationsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string root = await f.Repository.ResolveRevisionAsync("HEAD");
        await Git(f.Repository, ["tag", "-a", "same", "-m", "Old annotation"]); await Git(f.Repository, ["branch", "same"]);
        await Git(f.Repository, ["tag", "light"]); await Git(f.Repository, ["tag", "-a", "nested", "-m", "Outer nested message", "same"]);
        IReadOnlyList<GitReference> captured = await f.Repository.GetReferencesAsync();
        await Git(f.Repository, ["tag", "-f", "-a", "same", "-m", "Replacement at same target"]);
        f.Write("seed.txt", "descendant\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Descendant");
        await Git(f.Repository, ["tag", "-a", "future", "-m", "Do not show"]);
        string tagData = $"object {root}\ntype commit\ntag extra\ntagger Someone <tag@example.invalid> 981173106 +0000\nextra preserved header\n\nOnly the actual body\n\n";
        string extra = await Text(f.Repository, ["hash-object", "-t", "tag", "-w", "--stdin"], tagData);
        GitReference extraRef = new("refs/tags/extra", extra, null, false) { RevisionHash = root };
        string before = Snapshot(f.Repository.Path);
        IReadOnlyList<GitAnnotatedTagMessage> old = await f.Repository.GetAnnotatedTagMessagesAsync(root, [.. captured, extraRef]);
        Check(old.Single(t => t.FullName == "refs/tags/same").Message == "Old annotation" && old.Single(t => t.FullName == "refs/tags/same").ObjectHash == captured.Single(t => t.Name == "refs/tags/same").Hash,
            "Retagging a ref does not substitute another raw annotation into an earlier captured snapshot.");
        Check(old.Single(t => t.FullName == "refs/tags/nested").Message == "Outer nested message" && old.Single(t => t.FullName == "refs/tags/extra").Message == "Only the actual body",
            "Nested outer messages and real header separators are preserved.");
        Check(old.Count == 3 && old.All(t => t.RevisionHash == root), "Local homonyms and lightweight tags have no fabricated annotations.");
        GitCommitContainment current = await f.Repository.GetCommitContainingReferencesAsync(root, GitCommitReferenceScope.Tags);
        IReadOnlyList<GitAnnotatedTagMessage> now = await f.Repository.GetAnnotatedTagMessagesAsync(root, current.References.Select(AsReference).ToArray());
        Check(now.Single(t => t.FullName == "refs/tags/same").Message == "Replacement at same target" && !now.Any(t => t.FullName == "refs/tags/future"),
            "A new snapshot sees the replacement, but a containing descendant tag still is not an exact annotation.");
        Check(Snapshot(f.Repository.Path) == before, "Raw tag message reads never update refs or the index.");
    }

    private static async Task DescribeAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string root = await f.Repository.ResolveRevisionAsync("HEAD");
        Check(await f.Repository.GetCommitDescribeAsync(root) is null, "A valid commit without a reachable tag has no fabricated fallback description.");
        await Git(f.Repository, ["tag", "release-with-hyphens", root]);
        string one = await Text(f.Repository, "commit-tree", "HEAD^{tree}", "-p", root, "-m", "First parent1");
        string two = await Text(f.Repository, "commit-tree", "HEAD^{tree}", "-p", one, "-m", "First parent2");
        string side = await Text(f.Repository, "commit-tree", "HEAD^{tree}", "-p", root, "-m", "Other side");
        await Git(f.Repository, ["tag", "-a", "side-nearest", "-m", "Second parent", side]);
        string merge = await Text(f.Repository, "commit-tree", "HEAD^{tree}", "-p", two, "-p", side, "-m", "Merge");
        string before = Snapshot(f.Repository.Path);
        GitCommitDescription? d = await f.Repository.GetCommitDescribeAsync(merge);
        Check(d is { TagName: "release-with-hyphens", CommitDistance: 3 } && d.Raw == await Text(f.Repository, "describe", "--tags", "--first-parent", "--abbrev=40", merge),
            "The complete real first-parent series wins over a closer tag reachable through the second parent.");
        Check(Snapshot(f.Repository.Path) == before, "Description never creates a tag, checkout or commit.");
        string literal = "looks-12-g" + merge; await Git(f.Repository, ["tag", literal, merge]); before = Snapshot(f.Repository.Path);
        Check((await f.Repository.GetCommitDescribeAsync(merge)) is { CommitDistance: null } exact && exact.TagName == literal,
            "An exact native tag with a describe-shaped name remains a literal complete tag name.");
        Check(Snapshot(f.Repository.Path) == before, "Disambiguating the exact tag is read-only.");
    }

    private static async Task ValidationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string hash = await f.Repository.ResolveRevisionAsync("HEAD");
        string tree = await Text(f.Repository, "rev-parse", "HEAD^{tree}"), blob = await Text(f.Repository, "rev-parse", "HEAD:seed.txt");
        await Git(f.Repository, ["tag", "-a", "tag-object", "-m", "Not the raw commit"]); string tag = await Text(f.Repository, "rev-parse", "refs/tags/tag-object");
        string before = Snapshot(f.Repository.Path);
        foreach (string invalid in new[] { "HEAD", hash[..12], "--option", new string('z', 40), hash + "\n" })
        {
            await Expect<ArgumentException>(() => f.Repository.GetCommitDetailsAsync(invalid));
            await Expect<ArgumentException>(() => f.Repository.GetCommitContainingReferencesAsync(invalid));
            await Expect<ArgumentException>(() => f.Repository.GetAnnotatedTagMessagesAsync(invalid, []));
            await Expect<ArgumentException>(() => f.Repository.GetCommitDescribeAsync(invalid));
        }
        foreach (string noncommit in new[] { tree, blob, tag })
        {
            await Expect<ArgumentException>(() => f.Repository.GetCommitDetailsAsync(noncommit));
            await Expect<ArgumentException>(() => f.Repository.GetCommitContainingReferencesAsync(noncommit));
            await Expect<ArgumentException>(() => f.Repository.GetAnnotatedTagMessagesAsync(noncommit, []));
            await Expect<ArgumentException>(() => f.Repository.GetCommitDescribeAsync(noncommit));
        }
        await Expect<FileNotFoundException>(() => f.Repository.GetCommitDetailsAsync(new string('0', 40)));
        await Expect<GitCommandException>(() => f.Repository.GetCommitContainingReferencesAsync(new string('0', 40)));
        await Expect<ArgumentOutOfRangeException>(() => f.Repository.GetCommitContainingReferencesAsync(hash, (GitCommitReferenceScope)128));
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Expect<OperationCanceledException>(() => f.Repository.GetCommitDetailsAsync(hash, cancellationToken: cancelled.Token));
        await Expect<OperationCanceledException>(() => f.Repository.GetCommitContainingReferencesAsync(hash, cancellationToken: cancelled.Token));
        await Expect<OperationCanceledException>(() => f.Repository.GetAnnotatedTagMessagesAsync(hash, [], cancelled.Token));
        await Expect<OperationCanceledException>(() => f.Repository.GetCommitDescribeAsync(hash, cancelled.Token));
        Check(Snapshot(f.Repository.Path) == before, "Invalid, missing, noncommit and cancelled reads are distinct from valid-empty without mutation.");
    }

    private static async Task NoOptionalLocksAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string hash = await f.Repository.ResolveRevisionAsync("HEAD");
        await Git(f.Repository, ["tag", "-a", "ann", "-m", "Annotation"]); await Git(f.Repository, ["checkout", "--detach"]);
        IReadOnlyList<GitReference> tags = await f.Repository.GetReferencesAsync(); string before = Snapshot(f.Repository.Path);
        string wrapper = Path.Combine(f.Root, "strict-read-git"), log = Path.Combine(f.Root, "argv.log");
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nfound=0\nfor arg in \"$@\"; do [ \"$arg\" != --no-optional-locks ] || found=1; done\n[ \"$found\" = 1 ] || exit 86\nprintf '%s\\000' \"$@\" >> " + Quote(log) + "\nprintf '\\n' >> " + Quote(log) + "\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository guarded = new(f.Repository.Path, new GitRunner(wrapper));
        await guarded.GetCommitDetailsAsync(hash); await guarded.GetCommitContainingReferencesAsync(hash);
        await guarded.GetAnnotatedTagMessagesAsync(hash, tags); await guarded.GetCommitDescribeAsync(hash);
        string[] invocations = (await File.ReadAllTextAsync(log)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check(invocations.Length >= 12 && invocations.All(line => line.Split('\0').Contains("--no-optional-locks", StringComparer.Ordinal)),
            "Every actual validation/head/metadata/mailmap/notes/refs/tag/describe command carries the native no-optional-locks flag.");
        Check(invocations.Any(line => line.Contains("merge-base\0", StringComparison.Ordinal)) && Snapshot(f.Repository.Path) == before,
            "The detached ancestor subread is covered and all repository bytes remain intact.");
    }

    private static async Task HeadChangesAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string hash = await f.Repository.ResolveRevisionAsync("HEAD"); await Git(f.Repository, ["branch", "next"]);
        string wrapper = Path.Combine(f.Root, "changing-head-git"), once = Path.Combine(f.Root, "changed");
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nfor arg in \"$@\"; do\n if [ \"$arg\" = for-each-ref ] && [ ! -f " + Quote(once) + " ]; then\n  touch " + Quote(once)
            + "\n  /usr/bin/git --no-optional-locks symbolic-ref HEAD refs/heads/next || exit\n fi\ndone\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitCommitContainment result = await new GitRepository(f.Repository.Path, new GitRunner(wrapper)).GetCommitContainingReferencesAsync(hash);
        Check(File.Exists(once) && result.CurrentBranchFullName == "refs/heads/next" && result.References.Single(r => r.IsCurrent).FullName == "refs/heads/next",
            "A real concurrent native HEAD switch cannot publish stale current-branch metadata.");
        Check(await f.Repository.ResolveRevisionAsync("HEAD") == hash && (await f.Repository.GetStatusAsync()).Changes.Count == 0,
            "The injected external symbolic change did not checkout or modify branch targets, index or working files.");
    }

    private static async Task CancellationAsync(int kind)
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string hash = await f.Repository.ResolveRevisionAsync("HEAD");
        await Git(f.Repository, ["tag", "-a", "ann", "-m", "Annotation"]); GitReference[] tags = (await f.Repository.GetReferencesAsync()).Where(r => r.IsTag).ToArray();
        string wrapper = Path.Combine(f.Root, "gated-git"), readerPid = Path.Combine(f.Root, "reader.pid"), childPid = Path.Combine(f.Root, "child.pid"), release = Path.Combine(f.Root, "release");
        string command = kind switch { 0 => "show", 1 => "for-each-ref", 2 => "--batch", _ => "describe" };
        string childScript = "printf '%s' \"$$\" > " + Quote(childPid) + "; while [ ! -f " + Quote(release) + " ]; do sleep 0.05; done";
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nfor arg in \"$@\"; do\n if [ \"$arg\" = " + Quote(command) + " ] && [ ! -f " + Quote(release) + " ]; then\n  printf '%s' \"$$\" > " + Quote(readerPid)
            + "\n  /bin/sh -c " + Quote(childScript) + " &\n  wait\n  break\n fi\ndone\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository guarded = new(f.Repository.Path, new GitRunner(wrapper)); using CancellationTokenSource lifetime = new();
        Task pending = kind switch
        {
            0 => guarded.GetCommitDetailsAsync(hash, cancellationToken: lifetime.Token),
            1 => guarded.GetCommitContainingReferencesAsync(hash, cancellationToken: lifetime.Token),
            2 => guarded.GetAnnotatedTagMessagesAsync(hash, tags, lifetime.Token),
            _ => guarded.GetCommitDescribeAsync(hash, lifetime.Token)
        };
        try
        {
            await WaitFor(() => File.Exists(readerPid) && File.Exists(childPid) && new FileInfo(readerPid).Length > 0 && new FileInfo(childPid).Length > 0);
            int reader = int.Parse(await File.ReadAllTextAsync(readerPid), CultureInfo.InvariantCulture), child = int.Parse(await File.ReadAllTextAsync(childPid), CultureInfo.InvariantCulture);
            Check(Alive(reader) && Alive(child), "The actual native workflow and its descendant reached a real blocking barrier.");
            await guarded.SetConfigAsync("commitinfo.read-does-not-lock-writes", "yes").WaitAsync(TimeSpan.FromSeconds(5));
            string before = Snapshot(f.Repository.Path); lifetime.Cancel();
            await Expect<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Check(!Alive(reader) && !Alive(child), "Cancelling the details API awaited native reader and descendant drainage.");
            await File.WriteAllTextAsync(release, "release");
            Check((await guarded.GetCommitDetailsAsync(hash)).Hash == hash && Snapshot(f.Repository.Path) == before,
                "The same runner can read again after cancellation without ref/index/worktree changes.");
        }
        finally
        {
            lifetime.Cancel(); await File.WriteAllTextAsync(release, "release");
            try { await pending; } catch (OperationCanceledException) { }
        }
    }

    private static GitReference AsReference(GitCommitReference reference)
        => new(reference.FullName, reference.ObjectHash, null, reference.IsCurrent, reference.SymbolicTarget) { RevisionHash = reference.RevisionHash };
    private static async Task CommitWithIdentities(GitRepository repository, string body)
        => await Git(repository, ["commit", "--cleanup=verbatim", "--file=-"], body, new Dictionary<string, string?>
        {
            ["GIT_AUTHOR_NAME"] = "Raw Äuthor", ["GIT_AUTHOR_EMAIL"] = "raw-author@example.invalid", ["GIT_AUTHOR_DATE"] = "2001-02-03T04:05:06+0530",
            ["GIT_COMMITTER_NAME"] = "Raw Committer", ["GIT_COMMITTER_EMAIL"] = "raw-committer@example.invalid", ["GIT_COMMITTER_DATE"] = "2002-03-04T05:06:07-0400"
        });
    private static async Task<GitResult> Git(GitRepository repository, IReadOnlyList<string> arguments, string? input = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        GitResult result = await new GitRunner().RunAsync(repository.Path, ["--no-optional-locks", .. arguments], standardInput: input, environment: environment);
        Check(result.Success, string.Join(' ', arguments) + "\n" + result.StandardError); return result;
    }
    private static Task<string> Text(GitRepository repository, params string[] arguments) => Text(repository, arguments, null);
    private static async Task<string> Text(GitRepository repository, IReadOnlyList<string> arguments, string? input)
        => (await Git(repository, arguments, input)).StandardOutput.TrimEnd('\r', '\n');
    private static string Snapshot(string root)
        => string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path) + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static async Task WaitFor(Func<bool> ready)
    { Stopwatch watch = Stopwatch.StartNew(); while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(10); } Check(ready(), "The real Git barrier was not reached."); }
    private static bool Alive(int pid)
    { try { string text = File.ReadAllText($"/proc/{pid}/stat"); return text[(text.LastIndexOf(')') + 2)..].First() is not ('Z' or 'X'); } catch (IOException) { return false; } }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitExtensions.Linux;

internal static class ObjectRecoveryScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("object recovery scan preserves native flags diagnostics missing rows and readonly materials", ScanAsync),
        ("lost commit and tag metadata preserve CP1252 UTF8 identities and committer dates", MetadataAsync),
        ("object readers preserve exact blob tree commit and unpeeled tag payloads", RawObjectsAsync),
        ("lost object previews preserve file codecs raw binary bytes and readonly patches", PreviewAsync),
        ("blob export follows true input false autocrlf and native byte CRLF rules", ExportEolAsync),
        ("blob export uses attributes before extensions and retains one five six NUL bytes", ExportAttributesAsync),
        ("checked recovery creates exact mixed object tags and original display order numbering", RecoverMixedAsync),
        ("empty invalid selections and cancelled recovery reject before deleting existing tags", ValidationAsync),
        ("recovery reports real partial failures and duplicate annotated tag name effects", PartialRecoveryAsync),
        ("recovery tag cleanup deletes only the original LOST_FOUND prefix", CleanupAsync),
        ("save lost-found writes the actual native blob and commit files without inventing refs", LostFoundAsync),
        ("plain prune matches native Git retention and leaves protected objects and refs", PruneAsync),
        ("object recovery works without HEAD in bare and linked-worktree Git directories", RepositoryKindsAsync),
        ("SHA256 recovery preserves full identities and annotated tag objects", Sha256Async),
        ("unchecked Full scan reads real packed alternates like native fsck", AlternatesAsync),
        ("cancelled recovery observes committed ref effects and stops native descendants", CancellationAsync),
        ("recovery retains confirmed deletions and unknown effects when Git stops being executable", UnlaunchableAfterDeletionAsync),
    ];

    private static async Task ScanAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        OrphanObjects objects = await Orphans(fixture.Repository);
        await Git(fixture.Repository, "update-ref", "--create-reflog", "refs/heads/temporary", objects.Commit);
        await Git(fixture.Repository, "update-ref", "-d", "refs/heads/temporary");
        string before = Materials(fixture.Repository.Path);
        foreach (GitObjectRecoveryOptions options in new[] { new GitObjectRecoveryOptions(), new GitObjectRecoveryOptions(true, true, false), new GitObjectRecoveryOptions(false, true, true) })
        {
            GitObjectRecoveryReport report = await fixture.Repository.GetLostObjectsAsync(options);
            List<string> args = ["fsck"];
            if (options.FullCheck) { args.Add("--full"); }
            if (options.IncludeUnreachable) { args.Add("--unreachable"); }
            if (options.NoReflogs) { args.Add("--no-reflogs"); }
            GitResult oracle = await Native(fixture.Repository, args);
            Check(report.ExitCode == oracle.ExitCode && report.StandardOutput == oracle.StandardOutput && report.StandardError == oracle.StandardError,
                "The report retains the exact command's diagnostics, including its native nonzero exit.");
            Check(report.Objects.Select(row => row.Hash).Order(StringComparer.Ordinal).SequenceEqual(ParseDiagnosticIds(oracle).Order(StringComparer.Ordinal)),
                "Every supported native diagnostic has a row; unrelated progress/error lines are retained in the raw report.");
        }
        Check(Materials(fixture.Repository.Path) == before, "Scan and metadata reads create no lost-found, refs, index or object changes.");

        string missing = await Blob(fixture.Repository, "lost contents\n"u8.ToArray());
        string tree = await Tree(fixture.Repository, missing, "missing.txt");
        await Git(fixture.Repository, "update-ref", "refs/tags/protect-broken-tree", tree);
        File.Delete(ObjectPath(fixture.Repository, missing));
        GitObjectRecoveryReport damaged = await fixture.Repository.GetLostObjectsAsync(new(IncludeUnreachable: true));
        Check(!damaged.Success && damaged.Objects.Any(row => row.Hash == missing && row.ReadError is not null)
            && damaged.Objects.Any(row => row.Hash == objects.Tag && row.ReadError is null), "A missing object does not erase readable lost objects or fsck failure evidence.");
    }

    private static async Task MetadataAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        GitRepository repo = fixture.Repository;
        await repo.SetConfigAsync("i18n.commitEncoding", "windows-1252");
        await repo.SetConfigAsync("user.name", "Åuteur Ω");
        fixture.Write(".mailmap", "Mapped Ω <linux-test@example.invalid> Åuteur Ω <linux-test@example.invalid>\n");
        string tree = await Tree(repo, await Blob(repo, "file\n"u8.ToArray()), "café.txt");
        Encoding cp = GitFileEncodingCodec.GetEncoding("windows-1252");
        GitBinaryResult commit = await Binary(repo, ["commit-tree", tree, "-p", await repo.ResolveRevisionAsync("HEAD")], cp.GetBytes("Café € ‘message’\nSuite du sujet\n\nCorps Â©\n"),
            new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = "2000-01-01T00:00:00+00:00", ["GIT_COMMITTER_DATE"] = "2020-02-03T04:05:06+00:00" });
        Check(commit.Success, commit.StandardError);
        string oid = Encoding.ASCII.GetString(commit.StandardOutput).Trim();
        GitObjectData raw = await repo.ReadObjectAsync(oid);
        int headerEnd = raw.Bytes.AsSpan().IndexOf("\n\n"u8);
        Check(new UTF8Encoding(false, true).GetString(raw.Bytes.AsSpan(0, headerEnd)).Contains("author Åuteur Ω <", StringComparison.Ordinal),
            "The CP1252 commit really stores its independent author identity as UTF8 bytes.");
        string tag = await Tag(repo, oid, "étiquette", "Annotation Ω\n");
        await repo.SetConfigAsync("i18n.commitEncoding", "unknown-current-codec");
        string before = Materials(repo.Path);
        GitObjectRecoveryReport report = await repo.GetLostObjectsAsync(new(IncludeUnreachable: true));
        GitLostObject row = report.Objects.First(value => value.Hash == oid);
        Check(row.Subject == "Café € ‘message’ Suite du sujet" && row.Author == "Mapped Ω" && row.Parent == await repo.ResolveRevisionAsync("HEAD")
            && row.Date == DateTimeOffset.Parse("2020-02-03T04:05:06+00:00", CultureInfo.InvariantCulture), "Object-declared CP1252 and the actual committer timestamp are independent of the current config.");
        GitLostObject annotated = report.Objects.First(value => value.Hash == tag);
        Check(annotated.TagName == "étiquette" && annotated.Parent == oid && annotated.Subject == "étiquette:Annotation Ω" && annotated.ReadError is null,
            "Tag metadata retains its outer identity and UTF8 annotation.");
        Check(Materials(repo.Path) == before, "Metadata lookup does not rewrite a legacy commit or current configuration.");
    }

    private static async Task RawObjectsAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        OrphanObjects objects = await Orphans(fixture.Repository);
        foreach (string oid in new[] { objects.Blob, objects.Tree, objects.Commit, objects.Tag })
        {
            GitObjectData data = await fixture.Repository.ReadObjectAsync(oid.ToUpperInvariant());
            GitBinaryResult raw = await Binary(fixture.Repository, ["cat-file", data.RawType, oid]);
            Check(raw.Success && data.Hash == oid && data.Bytes.SequenceEqual(raw.StandardOutput), "The reader returns the exact raw object payload for every type.");
        }
        string substitute = await Blob(fixture.Repository, "replacement\n"u8.ToArray());
        await Git(fixture.Repository, "replace", objects.Blob, substitute);
        GitObjectData original = await fixture.Repository.ReadObjectAsync(objects.Blob);
        Check(original.Bytes.SequenceEqual(objects.BlobBytes), "A replace ref does not silently change the requested object contents or OID.");
        await Expect<ArgumentException>(() => fixture.Repository.ReadObjectAsync("HEAD"));
        await Expect<ArgumentException>(() => fixture.Repository.ReadObjectAsync(objects.Blob[..12]));
    }

    private static async Task PreviewAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        GitRepository repo = fixture.Repository;
        await repo.SetConfigAsync("i18n.filesEncoding", "windows-1252");
        byte[] text = GitFileEncodingCodec.GetEncoding("windows-1252").GetBytes("Café €\n");
        string blob = await Blob(repo, text), tree = await Tree(repo, blob, "café.txt");
        string commit = await CommitTree(repo, tree, "UTF8 annotation Ω\n");
        string tag = await Tag(repo, commit, "preview", "Tag Ω\n");
        byte[] binary = [137, 80, 78, 71, 0, 255, 10];
        string image = await Blob(repo, binary);
        string before = Materials(repo.Path);
        GitObjectPreview preview = await repo.GetObjectPreviewAsync(blob);
        Check(preview.Text == "Café €\n" && preview.Object!.Bytes.SequenceEqual(text) && preview.FileEncodingName == "windows-1252", "Blob preview uses the effective file codec and retains every cached byte.");
        GitObjectPreview imagePreview = await repo.GetObjectPreviewAsync(image);
        Check(imagePreview.Text is null && imagePreview.Object!.Bytes.SequenceEqual(binary) && imagePreview.SuggestedFileName.EndsWith(".png", StringComparison.Ordinal), "Binary preview remains raw and signature guessing does not decode corrupt text.");
        foreach (string oid in new[] { commit, tag })
        {
            GitObjectPreview patch = await repo.GetObjectPreviewAsync(oid, displayOptions: new(UseGitColoring: false));
            Check(patch.ReadError is null && patch.DiffDocument is { CanStageEquivalent: false, IsLosslessPatch: true }
                && patch.DiffDocument.Patch.Contains("+Café €", StringComparison.Ordinal) && !patch.DiffDocument.Patch.Contains('\ufffd'), "Readonly show combines UTF8 metadata and CP1252 file lines without staging capability.");
        }
        Check((await repo.GetObjectPreviewAsync(tree)).Text!.Contains("café.txt", StringComparison.Ordinal), "Tree preview exposes the actual native pretty tree.");
        GitObjectPreview missing = await repo.GetObjectPreviewAsync(new string('1', 40));
        Check(missing.Hash == new string('1', 40) && missing.Object is null && missing.ReadError is not null && missing.DiffDocument is null, "An unreadable identity has no stale or invented preview.");
        Check(Materials(repo.Path) == before, "All previews are repository-readonly.");
    }

    private static async Task ExportEolAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        byte[] lf = "one\ntwo\n"u8.ToArray(), mixed = "one\r\ntwo\n"u8.ToArray(), loneCr = "one\rtwo\n"u8.ToArray();
        string blob = await Blob(fixture.Repository, lf);
        foreach (string value in new[] { "false", "input", "true" })
        {
            await fixture.Repository.SetConfigAsync("core.autocrlf", value);
            string before = Materials(fixture.Repository.Path), destination = System.IO.Path.Combine(fixture.Root, "export-" + value + ".txt");
            GitObjectExportResult result = await fixture.Repository.ExportBlobAsync(blob, destination);
            byte[] expected = value == "true" ? "one\r\ntwo\r\n"u8.ToArray() : lf;
            Check(File.ReadAllBytes(destination).SequenceEqual(expected) && result.ConvertedLineEndings == (value == "true") && result.BytesWritten == expected.Length,
                "Only autocrlf=true converts native blob LF to literal CRLF, including on Linux.");
            Check(Materials(fixture.Repository.Path) == before, "Save as affects only the chosen destination.");
        }
        foreach ((byte[] bytes, byte[] expected, string name) in new[] { (mixed, "one\r\ntwo\r\n"u8.ToArray(), "mixed"), (loneCr, loneCr, "lonecr") })
        {
            string destination = System.IO.Path.Combine(fixture.Root, name + ".txt");
            await fixture.Repository.ExportBlobAsync(await Blob(fixture.Repository, bytes), destination);
            Check(File.ReadAllBytes(destination).SequenceEqual(expected), "Existing CRLF is retained; a lone CR prevents GitConvert conversion.");
        }
        string protectedOutput = System.IO.Path.Combine(fixture.Root, "existing.txt");
        File.WriteAllText(protectedOutput, "existing");
        await Expect<IOException>(() => fixture.Repository.ExportBlobAsync(blob, protectedOutput));
        Check(File.ReadAllText(protectedOutput) == "existing", "A refused overwrite preserves the original file.");
        await fixture.Repository.ExportBlobAsync(blob, protectedOutput, overwrite: true);
        Check(File.ReadAllBytes(protectedOutput).SequenceEqual("one\r\ntwo\r\n"u8.ToArray()), "Explicit overwrite succeeds atomically with the same EOL rule.");
    }

    private static async Task ExportAttributesAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        await fixture.Repository.SetConfigAsync("core.autocrlf", "true");
        fixture.Write(".gitattributes", "binary.txt -diff\nforced.png diff=text\nplain.png text\n");
        string blob = await Blob(fixture.Repository, "text\n"u8.ToArray());
        string before = Materials(fixture.Repository.Path);
        foreach ((string name, bool convert) in new[] { ("binary.txt", false), ("fallback.png", false), ("plain.png", true), ("forced.png", false), ("fallback.txt", true) })
        {
            string destination = fixture.File(name);
            await fixture.Repository.ExportBlobAsync(blob, destination);
            Check(File.ReadAllBytes(destination).SequenceEqual(convert ? "text\r\n"u8.ToArray() : "text\n"u8.ToArray()), "Attributes precede binary extensions; an unknown diff driver alone does not force text.");
            File.Delete(destination);
        }
        Check(Materials(fixture.Repository.Path) == before, "Name/attribute export tests preserve the complete repository after the chosen output is removed.");
        // Preview regards a single NUL as binary, but export uses >5 and the subsequent byte converter rejects any NUL.
        foreach (int count in new[] { 1, 5, 6 })
        {
            byte[] bytes = [.. new byte[count], .. "text\n"u8.ToArray()];
            string oid = await Blob(fixture.Repository, bytes), destination = System.IO.Path.Combine(fixture.Root, "nul-" + count + ".txt");
            await fixture.Repository.ExportBlobAsync(oid, destination);
            Check(File.ReadAllBytes(destination).SequenceEqual(bytes), "One, five and six NUL payloads all remain exact under the independent original guards.");
        }
        Check(!Directory.EnumerateFiles(fixture.Root, ".gitextensions-export-*", SearchOption.AllDirectories).Any(), "Atomic Save as leaves no temporary export files.");
        // The extra test blobs are the only database additions; attributes/index/config were not changed by export.
        Check(File.ReadAllText(fixture.File(".gitattributes")) == "binary.txt -diff\nforced.png diff=text\nplain.png text\n", "Export does not edit attribute configuration.");
    }

    private static async Task RecoverMixedAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        OrphanObjects objects = await Orphans(fixture.Repository);
        await Git(fixture.Repository, "tag", "LOST_FOUND_old", "HEAD");
        await Git(fixture.Repository, "tag", "keep", "HEAD");
        await fixture.Repository.SetConfigAsync("tag.gpgSign", "true");
        byte[] index = File.ReadAllBytes(fixture.File(".git/index"));
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        GitObjectRecoveryResult result = await fixture.Repository.RecoverObjectsAsync([objects.Commit, objects.Tag, objects.Blob, objects.Tree]);
        Check(result.Success && result.EffectsVerified && result.CreatedTags.Count == 4 && result.DeletedTags.SequenceEqual(["LOST_FOUND_old"]), "Counts reflect real effects, including deletion of old recovery tags.");
        string[] names = ["LOST_FOUND_1", "LOST_FOUND_annotation", "LOST_FOUND_3", "LOST_FOUND_4"];
        string[] ids = [objects.Commit, objects.Tag, objects.Blob, objects.Tree];
        for (int i = 0; i < names.Length; i++)
        {
            Check((await Git(fixture.Repository, "show-ref", "--verify", "--hash", "refs/tags/" + names[i])).StandardOutput.Trim() == ids[i], "Each lightweight tag points at the exact object rather than an annotated tag's peeled commit.");
            Check(result.CreatedTags[i] == new GitRecoveredObjectTag(names[i], ids[i]), "Return order is the requested display order, including the tag's named slot.");
        }
        Check(await fixture.Repository.ResolveRevisionAsync("HEAD") == head && File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(index)
            && (await Git(fixture.Repository, "show-ref", "--verify", "--hash", "refs/tags/keep")).StandardOutput.Trim() == head, "Recovery changes only intended tag refs, never checkout/index or unrelated refs.");
    }

    private static async Task ValidationAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        await Git(fixture.Repository, "tag", "LOST_FOUND_old", "HEAD");
        string before = Materials(fixture.Repository.Path), head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        await Expect<ArgumentException>(() => fixture.Repository.RecoverObjectsAsync([]));
        await Expect<ArgumentException>(() => fixture.Repository.RecoverObjectsAsync([head, "HEAD;touch"]));
        using CancellationTokenSource source = new(); source.Cancel();
        await Expect<OperationCanceledException>(() => fixture.Repository.RecoverObjectsAsync([head], source.Token));
        await Expect<ArgumentException>(() => fixture.Repository.ExportBlobAsync(head, System.IO.Path.Combine(fixture.Root, "not-a-blob.txt")));
        string blob = (await Git(fixture.Repository, "rev-parse", "HEAD:seed.txt")).StandardOutput.Trim();
        await Expect<ArgumentException>(() => fixture.Repository.ExportBlobAsync(blob, fixture.File(".git/index"), overwrite: true));
        if (!OperatingSystem.IsWindows())
        {
            string alias = System.IO.Path.Combine(fixture.Root, "metadata-alias");
            Directory.CreateSymbolicLink(alias, fixture.File(".git"));
            await Expect<ArgumentException>(() => fixture.Repository.ExportBlobAsync(blob, System.IO.Path.Combine(alias, "index"), overwrite: true));
        }
        Check(Materials(fixture.Repository.Path) == before, "Empty, invalid or pre-cancelled selection preserves all old recovery tags and materials.");
    }

    private static async Task PartialRecoveryAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        OrphanObjects objects = await Orphans(fixture.Repository);
        string missing = new string('1', 40), duplicate = await Tag(fixture.Repository, objects.Commit, "annotation", "Another annotation\n");
        GitObjectRecoveryResult result = await fixture.Repository.RecoverObjectsAsync([objects.Blob, missing, objects.Tag, duplicate, objects.Tree]);
        Check(!result.Success && !result.Cancelled && result.EffectsVerified && result.CreatedTags.Count == 3 && result.Errors.Count == 2,
            "Missing/colliding objects retain earlier and later native successes without fictional success counts or rollback.");
        Check(result.CreatedTags.Select(tag => tag.Name).SequenceEqual(["LOST_FOUND_1", "LOST_FOUND_annotation", "LOST_FOUND_5"]), "The counter advances per requested row, even after a failed slot.");
        Check(result.CreatedTags.Single(tag => tag.Name == "LOST_FOUND_annotation").ObjectId == objects.Tag, "A later name collision does not hide the first successful exact tag object.");
        if (OperatingSystem.IsLinux())
        {
            string wrapper = System.IO.Path.Combine(fixture.Root, "ref-change-wrapper");
            File.WriteAllText(wrapper, "#!/bin/sh\ncase \"$*\" in\n*'tag --no-sign -- LOST_FOUND_1 '*) git \"$@\" || exit $?; git update-ref refs/tags/LOST_FOUND_1 " + objects.Tree + ";;\n*) exec git \"$@\";;\nesac\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            GitRepository changed = new(fixture.Repository.Path, new GitRunner(wrapper));
            GitObjectRecoveryResult divergent = await changed.RecoverObjectsAsync([objects.Blob]);
            Check(!divergent.Success && divergent.CreatedTags.Count == 0 && divergent.DivergentTags.Single() == new GitRecoveredObjectTag("LOST_FOUND_1", objects.Tree)
                && divergent.HasRepositoryChanges && divergent.Errors.Any(error => error.Operation == "Observe LOST_FOUND_1"),
                "A ref changed before observation is a visible divergent effect, never an exact-target recovery success.");
        }
        string corrupt = await Blob(fixture.Repository, "corrupt\n"u8.ToArray());
        string corruptPath = ObjectPath(fixture.Repository, corrupt);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(corruptPath, File.GetUnixFileMode(corruptPath) | UnixFileMode.UserWrite); }
        File.WriteAllBytes(corruptPath, [1, 2, 3]);
        GitObjectRecoveryReport report = await fixture.Repository.GetLostObjectsAsync(new(IncludeUnreachable: true));
        Check(!report.Success && !string.IsNullOrEmpty(report.StandardError), "A corrupt loose object keeps native fsck's actual failure report.");
        GitObjectPreview preview = await fixture.Repository.GetObjectPreviewAsync(corrupt);
        Check(preview.ReadError is not null && preview.Object is null, "Corruption does not produce guessed object bytes or stale preview.");
    }

    private static async Task CleanupAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        foreach (string name in new[] { "LOST_FOUND_1", "LOST_FOUND_é/two", "LOST_AND_FOUND_keep", "prefix_LOST_FOUND_keep", "ordinary" })
        { await Git(fixture.Repository, "tag", name, "HEAD"); }
        byte[] index = File.ReadAllBytes(fixture.File(".git/index"));
        GitObjectRecoveryResult result = await fixture.Repository.DeleteLostFoundTagsAsync();
        Check(result.Success && result.DeletedTags.Count == 2 && result.CreatedTags.Count == 0, "Cleanup uses the real original prefix, not the inconsistent displayed LOST_AND_FOUND label.");
        IReadOnlyList<GitReference> refs = await fixture.Repository.GetReferencesAsync();
        Check(refs.Where(reference => reference.Name.StartsWith("refs/tags/", StringComparison.Ordinal)).Select(reference => reference.ShortName).Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { "LOST_AND_FOUND_keep", "ordinary", "prefix_LOST_FOUND_keep" }.Order(StringComparer.Ordinal)), "All other tags survive.");
        Check(File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(index), "Deleting recovery tags does not alter the index.");
    }

    private static async Task LostFoundAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        byte[] raw = [0, 255, 10, 13, 0];
        string blob = await Blob(fixture.Repository, raw), tree = await Tree(fixture.Repository, await Blob(fixture.Repository, "commit body\n"u8.ToArray()), "lost.txt");
        string commit = await CommitTree(fixture.Repository, tree, "Lost\n");
        string refs = await RefState(fixture.Repository);
        byte[] index = File.ReadAllBytes(fixture.File(".git/index"));
        GitObjectRecoveryReport report = await fixture.Repository.SaveLostFoundAsync();
        Check(File.ReadAllBytes(System.IO.Path.Combine(report.LostFoundDirectory, "other", blob)).SequenceEqual(raw), "Native lost-found stores the blob's actual bytes, including NUL/ff/EOL.");
        string saved = File.ReadAllText(System.IO.Path.Combine(report.LostFoundDirectory, "commit", commit)).Trim();
        Check(saved == commit, "Native lost-found stores the real dangling commit identity.");
        Check(await RefState(fixture.Repository) == refs && File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(index), "Save lost-found creates files rather than recovery tags or checkout changes.");
    }

    private static async Task PruneAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string protectedBlob = await Blob(fixture.Repository, "protected\n"u8.ToArray()), loose = await Blob(fixture.Repository, "dangling\n"u8.ToArray());
        await Git(fixture.Repository, "tag", "protect-object", protectedBlob);
        string twin = System.IO.Path.Combine(fixture.Root, "oracle"); CopyDirectory(fixture.Repository.Path, twin);
        GitRepository oracle = new(twin);
        string refs = await RefState(fixture.Repository);
        byte[] index = File.ReadAllBytes(fixture.File(".git/index"));
        GitResult actual = await fixture.Repository.PruneLostObjectsAsync(), expected = await Native(oracle, ["prune"]);
        Check(actual.ExitCode == expected.ExitCode && actual.StandardOutput == expected.StandardOutput && actual.StandardError == expected.StandardError,
            "Remove invokes plain prune with native expiry/config behavior, not gc or invented expire flags.");
        Check(ObjectFiles(fixture.Repository).SequenceEqual(ObjectFiles(oracle)), "Object set exactly matches a native prune oracle from the same repository preimage.");
        Check((await fixture.Repository.ReadObjectAsync(protectedBlob)).Hash == protectedBlob && await RefState(fixture.Repository) == refs
            && File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(index), "Protected objects, refs and index survive plain prune.");
        Check(File.Exists(ObjectPath(fixture.Repository, loose)) == File.Exists(ObjectPath(oracle, loose)), "Dangling retention is measured, never assumed from age.");
    }

    private static async Task RepositoryKindsAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(seed: false);
        string blob = await Blob(fixture.Repository, "unborn\n"u8.ToArray()), tree = await Tree(fixture.Repository, blob, "unborn.txt"), commit = await CommitTree(fixture.Repository, tree, "First orphan\n", parent: false);
        Check((await fixture.Repository.GetLostObjectsAsync()).Objects.Any(row => row.Hash == commit), "Unborn HEAD does not prevent lost-object discovery.");
        Check((await fixture.Repository.RecoverObjectsAsync([blob, tree, commit])).Success, "All object types can be recovered before a current branch exists.");
        GitRepository bare = await GitRepository.InitAsync(System.IO.Path.Combine(fixture.Root, "bare.git"), bare: true);
        await RepositoryFixture.ConfigureAsync(bare);
        string bareBlob = await Blob(bare, "bare\n"u8.ToArray());
        Check((await bare.GetLostObjectsAsync()).Objects.Any(row => row.Hash == bareBlob) && (await bare.RecoverObjectsAsync([bareBlob])).Success,
            "Bare object recovery requires no worktree or status index.");
        string another = await Blob(bare, "bare other\n"u8.ToArray());
        GitObjectRecoveryReport saved = await bare.SaveLostFoundAsync();
        Check(File.Exists(System.IO.Path.Combine(saved.LostFoundDirectory, "other", another)) && !Directory.Exists(System.IO.Path.Combine(bare.Path, ".git")), "Bare lost-found resolves the actual Git directory.");
        await Git(fixture.Repository, "branch", "main", commit);
        await Git(fixture.Repository, "symbolic-ref", "HEAD", "refs/heads/main");
        string worktree = System.IO.Path.Combine(fixture.Root, "linked");
        await Git(fixture.Repository, "worktree", "add", "--detach", worktree, commit);
        GitRepository linked = new(worktree);
        string linkedBlob = await Blob(linked, "linked dangling\n"u8.ToArray());
        GitObjectRecoveryReport linkedReport = await linked.SaveLostFoundAsync();
        string nativePath = (await Git(linked, "rev-parse", "--path-format=absolute", "--git-path", "lost-found")).StandardOutput.Trim();
        Check(linkedReport.LostFoundDirectory == nativePath && File.Exists(System.IO.Path.Combine(nativePath, "other", linkedBlob)), "Linked-worktree recovery uses Git's path rules rather than Path/.git.");
    }

    private static async Task Sha256Async()
    {
        using RepositoryFixture owner = await RepositoryFixture.CreateAsync();
        string path = System.IO.Path.Combine(owner.Root, "sha256"); Directory.CreateDirectory(path);
        GitResult init = await new GitRunner().RunAsync(path, ["init", "--object-format=sha256", "--initial-branch=main"]);
        Check(init.Success, init.StandardError);
        GitRepository repo = new(path); await RepositoryFixture.ConfigureAsync(repo);
        OrphanObjects objects = await Orphans(repo, parent: false);
        GitObjectRecoveryReport report = await repo.GetLostObjectsAsync(new(IncludeUnreachable: true));
        Check(report.Objects.Any(row => row.Hash == objects.Tag && row.Hash.Length == 64) && report.Objects.All(row => row.Hash.Length == 64), "The diagnostic reader does not truncate SHA256 identities to SHA1.");
        GitObjectRecoveryResult recovery = await repo.RecoverObjectsAsync([objects.Blob, objects.Tree, objects.Commit, objects.Tag]);
        Check(recovery.Success && recovery.CreatedTags.Count == 4 && recovery.CreatedTags.All(tag => tag.ObjectId.Length == 64), "SHA256 recovered refs retain the complete original IDs.");
        GitObjectData tagObject = await repo.ReadObjectAsync(objects.Tag);
        GitBinaryResult nativeTag = await Binary(repo, ["cat-file", "tag", objects.Tag]);
        Check(tagObject.Bytes.SequenceEqual(nativeTag.StandardOutput), "SHA256 tag objects stay unpeeled and byte-exact.");
    }

    private static async Task AlternatesAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        GitRepository source = await GitRepository.InitAsync(System.IO.Path.Combine(fixture.Root, "alternate"));
        await RepositoryFixture.ConfigureAsync(source);
        OrphanObjects objects = await Orphans(source, parent: false);
        await Git(source, "tag", "keep", objects.Tag);
        await Git(source, "repack", "-a", "-d");
        string alternatePath = System.IO.Path.Combine(source.Path, ".git", "objects");
        Directory.CreateDirectory(fixture.File(".git/objects/info")); File.WriteAllText(fixture.File(".git/objects/info/alternates"), alternatePath + "\n");
        await Git(fixture.Repository, "update-ref", "refs/tags/alternate-tag", objects.Tag);
        string before = Materials(fixture.Repository.Path, source.Path);
        GitObjectRecoveryReport report = await fixture.Repository.GetLostObjectsAsync(new(FullCheck: false, IncludeUnreachable: true));
        GitResult native = await Native(fixture.Repository, ["fsck", "--unreachable", "--no-reflogs"]);
        Check(report.ExitCode == native.ExitCode && report.StandardOutput == native.StandardOutput && report.StandardError == native.StandardError,
            "Unchecked Full omits --no-full and reads packed alternates according to native default behavior.");
        Check((await fixture.Repository.ReadObjectAsync(objects.Blob)).Bytes.SequenceEqual(objects.BlobBytes), "Object-by-OID reads alternate packs without requiring a loose local file.");
        Check(Materials(fixture.Repository.Path, source.Path) == before, "Scan/raw reads do not copy or unpack alternate objects into either repository.");
    }

    private static async Task CancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The cancellation fixture requires Unix executable wrappers."); }
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string first = await Blob(fixture.Repository, "first\n"u8.ToArray()), second = await Blob(fixture.Repository, "second\n"u8.ToArray());
        string wrapper = System.IO.Path.Combine(fixture.Root, "git-wrapper"), ready = System.IO.Path.Combine(fixture.Root, "ready"), child = System.IO.Path.Combine(fixture.Root, "child");
        File.WriteAllText(wrapper, "#!/bin/sh\ncase \"$*\" in\n*'tag --no-sign -- LOST_FOUND_2 '*) git \"$@\"; result=$?; [ $result -eq 0 ] || exit $result; sleep 60 & echo $! > '" + child + "'; echo ready > '" + ready + "'; wait;;\n*) exec git \"$@\";;\nesac\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository repo = new(fixture.Repository.Path, new GitRunner(wrapper));
        using CancellationTokenSource cancel = new();
        Task<GitObjectRecoveryResult> task = repo.RecoverObjectsAsync([first, second], cancel.Token);
        await WaitFor(() => File.Exists(ready), TimeSpan.FromSeconds(10));
        int pid = int.Parse(File.ReadAllText(child).Trim(), CultureInfo.InvariantCulture);
        cancel.Cancel();
        GitObjectRecoveryResult result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(result.Cancelled && result.EffectsVerified && result.CreatedTags.Count == 2 && result.CreatedTags[1].ObjectId == second,
            "Cancellation after native tag creation reports both actual ref effects, rather than losing the last committed tag.");
        await WaitFor(() => !Running(pid), TimeSpan.FromSeconds(5));
        Check(!Running(pid), "Cancellation terminates the real wrapper descendant.");
        byte[] index = File.ReadAllBytes(fixture.File(".git/index"));
        Check((await repo.GetLostObjectsAsync()).Objects.Count == 0 && File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(index), "Recovery tags protect the blobs and cancellation does not change the index.");
    }

    private sealed record OrphanObjects(string Blob, string Tree, string Commit, string Tag, byte[] BlobBytes);
    private static async Task UnlaunchableAfterDeletionAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The executable-permission fixture requires Linux."); }
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        GitRepository normal = fixture.Repository;
        string head = await normal.ResolveRevisionAsync("HEAD");
        await Git(normal, "tag", "LOST_FOUND_1", head);
        await Git(normal, "tag", "LOST_FOUND_2", head);
        byte[] index = File.ReadAllBytes(fixture.File(".git/index")), contents = File.ReadAllBytes(fixture.File("seed.txt")), config = File.ReadAllBytes(fixture.File(".git/config"));
        string wrapper = System.IO.Path.Combine(fixture.Root, "git-becomes-unlaunchable");
        File.WriteAllText(wrapper, "#!/bin/sh\ncase \"$*\" in\n*'tag --delete -- LOST_FOUND_1') git \"$@\"; result=$?; [ $result -eq 0 ] || exit $result; chmod 000 \"$0\";;\n*) exec git \"$@\";;\nesac\n");
        UnixFileMode executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(wrapper, executable);
        try
        {
            GitRepository interrupted = new(normal.Path, new GitRunner(wrapper));
            GitObjectRecoveryResult result = await interrupted.RecoverObjectsAsync([head]);
            Check(!result.Success && !result.Cancelled && !result.EffectsVerified && result.HasRepositoryChanges
                && result.DeletedTags.SequenceEqual(["LOST_FOUND_1"]) && result.CreatedTags.Count == 0
                && result.Errors.Any(error => error.Operation == "Recover batch") && result.Errors.Any(error => error.Operation == "Observe effects"),
                "A launch failure after mutation returns the confirmed deletion and explicit unknown observation; it cannot report no effects or success.");
            Check(File.GetUnixFileMode(wrapper) == (UnixFileMode)0, "The wrapper really lost execute permission after its first native deletion.");
            IReadOnlyList<GitReference> refs = await normal.GetReferencesAsync();
            Check(!refs.Any(reference => reference.Name == "refs/tags/LOST_FOUND_1")
                && refs.Any(reference => reference.Name == "refs/tags/LOST_FOUND_2" && reference.Hash == head),
                "The independent normal Git reader confirms precisely the first deletion and the second untouched ref.");
            Check(await normal.ResolveRevisionAsync("HEAD") == head && File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(index)
                && File.ReadAllBytes(fixture.File("seed.txt")).SequenceEqual(contents) && File.ReadAllBytes(fixture.File(".git/config")).SequenceEqual(config),
                "An unlaunchable Git does not hide or corrupt checkout, index, worktree or configuration effects.");
        }
        finally { if (File.Exists(wrapper)) { File.SetUnixFileMode(wrapper, executable); } }
    }
    private static async Task<OrphanObjects> Orphans(GitRepository repo, bool parent = true)
    {
        byte[] bytes = "lost text\n"u8.ToArray();
        string blob = await Blob(repo, bytes), tree = await Tree(repo, blob, "lost.txt"), commit = await CommitTree(repo, tree, "Lost subject\n\nLost body\n", parent);
        return new(blob, tree, commit, await Tag(repo, commit, "annotation", "Annotated Ω\n"), bytes);
    }
    private static async Task<string> Blob(GitRepository repo, byte[] bytes)
        => ReadOid(await Binary(repo, ["hash-object", "-w", "--stdin"], bytes));
    private static async Task<string> Tree(GitRepository repo, string blob, string name)
        => ReadOid(await Binary(repo, ["mktree", "-z"], Encoding.UTF8.GetBytes("100644 blob " + blob + "\t" + name + "\0")));
    private static async Task<string> CommitTree(GitRepository repo, string tree, string message, bool parent = true)
    {
        List<string> args = ["commit-tree", tree];
        if (parent) { args.AddRange(["-p", await repo.ResolveRevisionAsync("HEAD")]); }
        return ReadOid(await Binary(repo, args, Encoding.UTF8.GetBytes(message)));
    }
    private static async Task<string> Tag(GitRepository repo, string target, string name, string message)
    {
        string type = (await Git(repo, "cat-file", "-t", target)).StandardOutput.Trim();
        string raw = "object " + target + "\ntype " + type + "\ntag " + name + "\ntagger Åuteur Ω <author@example.invalid> 1600000000 +0000\n\n" + message;
        return ReadOid(await Binary(repo, ["mktag"], Encoding.UTF8.GetBytes(raw)));
    }
    private static string ReadOid(GitBinaryResult result) { Check(result.Success, result.StandardError); return Encoding.ASCII.GetString(result.StandardOutput).Trim(); }
    private static Task<GitBinaryResult> Binary(GitRepository repo, IReadOnlyList<string> args, byte[]? input = null, IReadOnlyDictionary<string, string?>? extra = null)
        => new GitRunner().RunBinaryAsync(repo.Path, ["--no-pager", "-c", "color.ui=false", "-c", "core.quotepath=false", "--no-replace-objects", .. args], standardInput: input,
            environment: Environment(extra));
    private static Task<GitResult> Native(GitRepository repo, IReadOnlyList<string> args)
        => new GitRunner().RunAsync(repo.Path, ["--no-pager", "-c", "color.ui=false", "-c", "core.quotepath=false", "--no-replace-objects", .. args], environment: Environment());
    private static async Task<GitResult> Git(GitRepository repo, params string[] args)
    { GitResult result = await Native(repo, args); Check(result.Success, result.StandardError); return result; }
    private static Dictionary<string, string?> Environment(IReadOnlyDictionary<string, string?>? extra = null)
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal) { ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = "/dev/null" };
        if (extra is not null) { foreach ((string key, string? value) in extra) { values[key] = value; } } return values;
    }
    private static IEnumerable<string> ParseDiagnosticIds(GitResult result) => (result.StandardOutput + "\n" + result.StandardError).Split('\n')
        .Select(line => Regex.Match(line, @"^(?:(?:dangling|unreachable|missing) (?:commit|blob|tree|tag)|warning in tree) ([a-f0-9]{64}|[a-f0-9]{40})(?=$|[^a-f0-9])"))
        .Where(match => match.Success).Select(match => match.Groups[1].Value);
    private static string ObjectPath(GitRepository repo, string oid) => System.IO.Path.Combine(repo.Path, ".git", "objects", oid[..2], oid[2..]);
    private static Task<string> RefState(GitRepository repo) => RefStateCore(repo);
    private static async Task<string> RefStateCore(GitRepository repo) => (await Git(repo, "for-each-ref", "--format=%(refname)%00%(objectname)")).StandardOutput;
    private static string[] ObjectFiles(GitRepository repo) => Directory.EnumerateFiles(System.IO.Path.Combine(repo.Path, ".git", "objects"), "*", SearchOption.AllDirectories)
        .Select(file => System.IO.Path.GetRelativePath(System.IO.Path.Combine(repo.Path, ".git", "objects"), file)).Order(StringComparer.Ordinal).ToArray();
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) { Directory.CreateDirectory(System.IO.Path.Combine(destination, System.IO.Path.GetRelativePath(source, dir))); }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) { File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetRelativePath(source, file))); }
    }
    private static string Materials(params string[] roots)
    {
        List<string> entries = [];
        foreach (string root in roots)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                FileInfo info = new(path);
                string kind = info.LinkTarget is string target ? "link:" + target : Directory.Exists(path) ? "directory" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                string mode = OperatingSystem.IsWindows() ? "" : ((int)File.GetUnixFileMode(path)).ToString(CultureInfo.InvariantCulture);
                entries.Add(System.IO.Path.GetRelativePath(root, path) + ":" + kind + ":" + mode);
            }
        }
        return JsonSerializer.Serialize(entries);
    }
    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < timeout) { await Task.Delay(20); }
        Check(condition(), "Bounded condition did not become true.");
    }
    private static bool Running(int pid)
    {
        try
        {
            string stat = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
            int end = stat.LastIndexOf(')'); return end >= 0 && stat.Length > end + 2 && stat[end + 2] is not ('Z' or 'X' or 'x');
        }
        catch (IOException) { return false; }
    }
    private static async Task Expect<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
}

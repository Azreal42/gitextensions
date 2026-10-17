using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using GitCommands.Git;
using GitExtensions.Linux;

if (args.Length == 3 && args[0] == "--unix-socket-directory-worker")
{
    return await UnixSocketDirectoryScenarios.RunWorkerAsync(args[1], args[2]);
}

if (args.FirstOrDefault() is "--fake-askpass" or "--fake-user-askpass")
{
    if (args.Length != 2) { return 1; }
    if (Environment.GetEnvironmentVariable("GITEXTENSIONS_FAKE_EXPECT_CULTURE") is string expectedCulture
        && Environment.GetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE") != expectedCulture) { return 1; }
    bool username = args[1].StartsWith("Username", StringComparison.Ordinal);
    Console.WriteLine(args[0] == "--fake-user-askpass"
        ? username ? "explicit-user" : "fictitious-explicit-password"
        : username ? "fallback-user" : "fictitious-password λ '$() &");
    return 0;
}
if (args.FirstOrDefault() == "--fake-slow-askpass")
{
    // Publish readiness only after the complete PID has been written and the file closed.
    string pidFile = Environment.GetEnvironmentVariable("GITEXTENSIONS_FAKE_ASKPASS_PID")!;
    string temporaryPidFile = pidFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
    await File.WriteAllTextAsync(temporaryPidFile, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    File.Move(temporaryPidFile, pidFile, overwrite: false);
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 1;
}

if (args.Length == 2 && args[0] == "--cherry-pick-recovery-abort")
{
    return await CherryPickRecoveryScenarios.RunRecoveryWorkerAsync(args[1]);
}

if (RebaseAdvancedScenarios.TryHandleEditor(args, out int nativeRebaseEditorExitCode))
{
    return nativeRebaseEditorExitCode;
}

if (GitRebaseEditor.TryHandle(args, out int rebaseEditorExitCode))
{
    return rebaseEditorExitCode;
}

// This dependency-free integration executable intentionally exercises the installed Git against real repositories.
// A failed assertion or unexpected Git failure makes the process exit nonzero.
Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", "/dev/null");
Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow");
Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");

(string Name, Func<Task> Run)[] tests =
[
    .. UnixSocketDirectoryScenarios.All,
    .. FileHistoryGraphScenarios.All,
    .. QuickSearchMessageScenarios.All,
    .. ComparisonEndpointScenarios.All,
    .. ComparisonGroupScenarios.All,
    .. ComparisonFileMenuScenarios.All,
    .. FileMenuDiffToolScenarios.All,
    ("unborn status and unstage", UnbornAsync),
    ("repository discovery normalizes subdirectories and metadata to root", DiscoveryAsync),
    ("repository discovery resolves linked worktree files and metadata", LinkedDiscoveryAsync),
    ("bare repositories expose history and reject worktree mutations clearly", BareAsync),
    ("unborn bare repository and newline root discovery", BareUnbornAsync),
    ("literal paths with whitespace, quotes, metacharacters and newlines", LiteralPathsAsync),
    ("rename and mixed index/worktree status", RenameAsync),
    ("history, parents, multiline messages and SHA1 shared identities", HistoryAsync),
    ("history all branches, paging, authors, messages and literal path filters", FilteredHistoryAsync),
    ("literal author filters alone and combined with message", LiteralAuthorAsync),
    ("typed history headers, dates, literal paths and explicit diff regex", LogQueryAsync),
    ("typed history first-parent, merge hiding and history simplification", LogTraversalAsync),
    ("typed history reflogs recover unreachable commits", LogReflogAsync),
    ("branches, tags, detached HEAD and references", ReferencesAsync),
    ("stash include untracked, apply index, pop and drop", StashAsync),
    ("local remotes, push, fetch, pull and clone", RemotesAsync),
    ("NUL worktree paths and cleanup", WorktreesAsync),
    ("submodule paths, init/update/sync and checkout identities", SubmodulesAsync),
    ("sparse cone directories are literal and disabling restores checkout", SparseCheckoutAsync),
    ("explicit non-cone sparse patterns and dirty file preservation", SparsePatternsAsync),
    ("bisect identifies first bad commit and reset restores branch", BisectAsync),
    ("bisect no-checkout, first-parent, skips and inconclusive candidates", BisectSkipAsync),
    ("bisect path filters preserve literal metacharacters", BisectPathsAsync),
    ("notes preserve exact text, namespaces, commit identity and serialized writes", NotesAsync),
    ("blame authors and line contents", BlameAsync),
    ("tree paths and byte-exact binary blobs", TreeAsync),
    ("partial hunks stage and unstage without touching other changes", HunksAsync),
    ("upstream line selection stages, unstages and resets selected lines", LinesAsync),
    ("search no-match, tracked content, historical content and newline paths", SearchAsync),
    ("reflog and soft/mixed/hard reset semantics", ReflogResetAsync),
    ("commit signoff, authors, fixup, squash, templates and signing options", CommitOptionsAsync),
    ("SSH commit signature is verified by Git", SigningAsync),
    ("interactive rebase reorder, reword, drop and plan validation", InteractiveRebaseAsync),
    ("interactive rebase squash/fixup", InteractiveSquashAsync),
    ("interactive rebase edited squash message survives trailing fixup", InteractiveSquashMessageAsync),
    ("interactive rebase edit pause survives a new repository instance", InteractiveEditAsync),
    ("merge conflict, stage resolution and continue", MergeConflictAsync),
    ("merge conflict and abort", MergeAbortAsync),
    ("cherry-pick, rebase and revert", SequencerAsync),
    ("cherry-pick abort and rebase abort/skip", SequencerRecoveryAsync),
    ("config duplicates, multiline values and missing settings", ConfigAsync),
    ("command failures and invalid revision/argument handling", ErrorsAsync),
    ("SHA256 repository status/history", Sha256Async),
    ("cancellation terminates Git and descendants", CancellationAsync),
    ("askpass isolates response streams and preserves explicit helpers", AskpassAsync),
    .. AskpassInformationScenarios.All,
    .. AskpassModesScenarios.All,
    ("ZIP and TAR exports preserve committed bytes, literal paths and revision filters", ArchiveExportsAsync),
    ("mailbox exports retain root, inclusive range and individual selection semantics", FormatPatchExportsAsync),
    ("raw patch options apply, check, reverse, stage and fix whitespace", DiffPatchFilesAsync),
    ("mailbox application preserves authors, signoffs and directory order", MailboxApplyAsync),
    ("mailbox conflict state survives resolution, abort and skip", MailboxConflictAsync),
    ("object verification, byte statistics and normal garbage collection", MaintenanceAsync),
    ("revision file lists preserve renames and literal paths", DiffScenarios.RevisionFilesAsync),
    ("merge diffs expose each parent", DiffScenarios.MergeParentsAsync),
    ("index previews preserve exact bytes", DiffScenarios.IndexBytesAsync),
    ("combined diffs retain NUL literal paths and actual merge parents", MergeRangeDiffScenarios.CombinedPathsAsync),
    ("combined default -c retains picked-parent hunks while --cc explicitly omits them", MergeRangeDiffScenarios.OmittedHunksAsync),
    ("root and clean merge groups omit empty combined content", MergeRangeDiffScenarios.CleanAndRootGroupsAsync),
    ("real three-parent combined merges preserve all columns", MergeRangeDiffScenarios.ThreeParentCombinedAsync),
    ("range-diff two, three and four selections preserve all explicit bounds", MergeRangeDiffScenarios.RangeSelectionsAsync),
    ("range-diff context, whitespace, histogram, colors and literal paths", MergeRangeDiffScenarios.RangeOptionsAndPathsAsync),
    ("invalid range series retain ordinary comparisons and reject injection", MergeRangeDiffScenarios.InvalidRangeSelectionsAsync),
    ("combined and range-diff work without a worktree in bare repositories", MergeRangeDiffScenarios.BareDiffsAsync),
    ("ordinary revision diff overload retains display options and plain output", MergeRangeDiffScenarios.OrdinaryDiffOptionsAsync),
    ("combined and range-diff cancellation terminates child processes", MergeRangeDiffScenarios.CancellationAsync),
    ("A/B groups classify identical, unequal and one-sided real changes", ABClassificationScenarios.ProvenanceAsync),
    ("A/B rename scores distinguish exact moves from changed content", ABClassificationScenarios.RenameScoresAsync),
    ("A/B copy detection retains exact and partial similarity without harder search", ABClassificationScenarios.CopyScoresAsync),
    ("classification BASE stays distinct from explicit range bounds", ABClassificationScenarios.ExplicitBasesAsync),
    ("pairwise rename overlap does not form a transitive file identity", ABClassificationScenarios.PairwiseNamesAsync),
    ("NUL name-status records reject truncation, invalid scores and empty names", ABClassificationScenarios.MalformedRecordsAsync),
    ("bare classification preserves patches and ineligible groups remain unclassified", ABClassificationScenarios.BareAndEligibilityAsync),
    ("index flags retain literal names, mixed flags and unique conflict paths", ABClassificationScenarios.IndexFlagsAsync),
    ("porcelain rename status retains exact and partial scores with literal paths", StatusSimilarityScenarios.RealRenamesAsync),
    ("porcelain copy scores and malformed records preserve status contracts", StatusSimilarityScenarios.MalformedAndCopyScoresAsync),
    ("ANSI port retains the original terminal and malformed-text fixtures", DiffColorScenarios.OriginalAnsiFixturesAsync),
    ("ANSI injected light/dark palettes preserve attributes, 256 colors and RGB", DiffColorScenarios.AnsiPaletteAndAttributesAsync),
    ("ANSI CRLF merging and malformed SGR preserve UTF16 canonical offsets", DiffColorScenarios.AnsiMergingAndMalformedAsync),
    ("theme adaptation and double-dim intraline segments match original RGB", DiffColorScenarios.ThemeAdaptationAndDoubleDimAsync),
    ("intraline anchors retain Unicode combined and nested range prefixes", DiffColorScenarios.InlineAnchorsAndPrefixOffsetsAsync),
    ("line/word matches remain ordered and pairwise across original cases", DiffColorScenarios.OrderedLineMatchingAsync),
    ("intraline bounds, five-line context blocks and moved exclusions", DiffColorScenarios.InlineBoundsAndBlocksAsync),
    ("original gaps patch retains content-only markers and empty-line anchors", DiffColorScenarios.OriginalGapFixtureAsync),
    .. DiffDisplayScenarios.All,
    .. FileEncodingScenarios.All,
    .. FilePreviewScenarios.All,
    .. BlameEncodingScenarios.All,
    .. ReflogScenarios.All,
    .. ObjectRecoveryScenarios.All,
    .. CleanScenarios.All,
    .. HistoryRemovalScenarios.All,
    .. GitProcessStreamingScenarios.All,
    .. PullFetchScenarios.All,
    .. PullAdvancedScenarios.All,
    .. BranchFilterScenarios.All,
    .. StashScenarios.All,
    .. SubmoduleRemovalScenarios.All,
    .. SubmoduleAddScenarios.All,
    .. SubmoduleManagementScenarios.All,
    .. SubmoduleConflictScenarios.All,
    .. CherryPickScenarios.All,
    .. MergeConflictScenarios.All,
    .. ResetRevertScenarios.All,
    .. ResetSelectedPatchScenarios.All,
    .. CherryPickRecoveryScenarios.All,
    .. RebaseAdvancedScenarios.All,
    .. PushScenarios.All,
    .. RemoteBranchDeletionScenarios.All,
    .. QuickReferenceScenarios.All,
    .. CommitInformationScenarios.All,
    .. ReferenceSortingScenarios.All,
    .. RemoteSettingsScenarios.All,
    .. WorktreeScenarios.All,
    .. RulesFileScenarios.All,
    .. PushStreamingScenarios.All,
    .. CloneStreamingScenarios.All,
    .. ScriptScenarios.All,
    .. ScriptRuntimeScenarios.All,
    .. GitProcessRelayScenarios.All,
    .. GrepScenarios.All,
    ("Linux cancellation treats zombie and dead procfs tasks as terminated", LinuxProcessState.VerifyStatesAsync),
    ("difftastic formatter retains original sample gutters and right column", DifftasticScenarios.OriginalFixtureAsync),
    ("difftastic strips number colors and aligns growing column prefixes", DifftasticScenarios.ColumnsAndColorsAsync),
    ("difftastic one-sided and zero-context Unicode lines retain correct gutters", DifftasticScenarios.OneSidedAndUnicodeAsync),
    ("difftastic unexpected output and malformed bounds remain deterministic", DifftasticScenarios.UnexpectedOutputAndBoundsAsync),
    ("official difftastic compact width200 retains both gutters and actual colors", DifftasticScenarios.RealCompactAsync),
    ("official difftastic color-never preserves exact emitted text without guessing gutters", DifftasticScenarios.RealNoColor177Async),
    ("official difftastic unilateral Unicode and numeric content survives compact columns", DifftasticScenarios.RealUnicodeUnilateralAsync),
    ("official difftastic multiple compact files align at one content ruler", DifftasticScenarios.RealMultipleFilesAsync),
    ("copy patch preserves exact selected or complete UTF16 display text", DiffClipboardScenarios.PatchVerbatimAsync),
    ("whole patch version copies preserve original header and prefix semantics", DiffClipboardScenarios.WholeVersionsAsync),
    ("selected hunk version copies retain only displayed old or new fragments", DiffClipboardScenarios.HunkSelectionAsync),
    ("mid-line version selections protect literal signs and original header spacing", DiffClipboardScenarios.MidLineSelectionAsync),
    ("version copies preserve triple-prefix and no-newline-marker rules", DiffClipboardScenarios.TriplesAndMarkerAsync),
    ("clipboard versions reuse original autocrlf conversion and CRLF semantics", DiffClipboardScenarios.LineEndingsAsync),
    ("plain clipboard text and invalid UTF16 selections remain explicit", DiffClipboardScenarios.PlainAndValidationAsync),
    ("real partial Git patch version copies never reconstruct omitted file content", DiffClipboardScenarios.RealPartialPatchAsync),
];

if (args.Contains("--unix-socket-directory-only", StringComparer.Ordinal)) { tests = UnixSocketDirectoryScenarios.All.ToArray(); }
if (args.Contains("--file-history-graph-only", StringComparer.Ordinal)) { tests = FileHistoryGraphScenarios.All.ToArray(); }
if (args.Contains("--quick-search-message-only", StringComparer.Ordinal)) { tests = QuickSearchMessageScenarios.All.ToArray(); }
if (args.Contains("--reset-selected-patch-only", StringComparer.Ordinal)) { tests = ResetSelectedPatchScenarios.All.ToArray(); }
if (args.Contains("--comparison-endpoints-only", StringComparer.Ordinal)) { tests = ComparisonEndpointScenarios.All.ToArray(); }
if (args.Contains("--remote-branch-deletion-only", StringComparer.Ordinal)) { tests = RemoteBranchDeletionScenarios.All.ToArray(); }
if (args.Contains("--quick-references-only", StringComparer.Ordinal)) { tests = QuickReferenceScenarios.All.ToArray(); }
if (args.Contains("--commit-information-only", StringComparer.Ordinal)) { tests = CommitInformationScenarios.All.ToArray(); }
if (args.Contains("--reference-sorting-only", StringComparer.Ordinal)) { tests = ReferenceSortingScenarios.All.ToArray(); }
if (args.Contains("--remote-settings-only", StringComparer.Ordinal)) { tests = RemoteSettingsScenarios.All.ToArray(); }
if (args.Contains("--worktrees-only", StringComparer.Ordinal)) { tests = WorktreeScenarios.All.ToArray(); }
if (args.Contains("--submodule-add-only", StringComparer.Ordinal)) { tests = SubmoduleAddScenarios.All.ToArray(); }
if (args.Contains("--submodule-management-only", StringComparer.Ordinal)) { tests = SubmoduleManagementScenarios.All.ToArray(); }
if (args.Contains("--submodule-conflict-only", StringComparer.Ordinal)) { tests = SubmoduleConflictScenarios.All.ToArray(); }
if (args.Contains("--askpass-modes-only", StringComparer.Ordinal))
{ tests = [("askpass isolates response streams and preserves explicit helpers", AskpassAsync), .. AskpassInformationScenarios.All, .. AskpassModesScenarios.All]; }
if (args.Contains("--rules-files-only", StringComparer.Ordinal)) { tests = RulesFileScenarios.All.ToArray(); }
if (args.Contains("--scripts-only", StringComparer.Ordinal)) { tests = ScriptScenarios.All.Concat(ScriptRuntimeScenarios.All).ToArray(); }
if (args.Contains("--comparison-groups-only", StringComparer.Ordinal)) { tests = ComparisonGroupScenarios.All.ToArray(); }
if (args.Contains("--comparison-file-menus-only", StringComparer.Ordinal)) { tests = ComparisonFileMenuScenarios.All.ToArray(); }
if (args.Contains("--file-menu-difftools-only", StringComparer.Ordinal)) { tests = FileMenuDiffToolScenarios.All.ToArray(); }

int failures = 0;
foreach ((string name, Func<Task> run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} integration scenarios passed.");
return failures == 0 ? 0 : 1;

static async Task UnbornAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(seed: false);
    GitStatus initial = await fixture.Repository.GetStatusAsync();
    Assert(initial.Branch == "main" && initial.IsUnborn && initial.Changes.Count == 0, "Unborn branch information");
    Assert((await fixture.Repository.GetLogAsync()).Count == 0, "Empty history");
    fixture.Write("new.txt", "new\n");
    await fixture.Repository.StageAsync(["new.txt"]);
    Assert((await fixture.Repository.GetStatusAsync()).Changes.Single().IndexStatus == 'A', "Staged addition");
    await fixture.Repository.UnstageAsync(["new.txt"]);
    Assert(File.Exists(fixture.File("new.txt")) && (await fixture.Repository.GetStatusAsync()).Changes.Single().IsUntracked, "Unstage preserves file");
}

static async Task DiscoveryAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string nested = fixture.File("nested/dir");
    Directory.CreateDirectory(nested);
    GitRepository root = await GitRepository.OpenAsync(nested);
    Assert(root.Path == fixture.Repository.Path && root.Info is not null && root.Info.RootPath == root.Path && !root.IsBare, "Subdirectory resolves root");
    Assert(root.Info!.GitDirectory == fixture.File(".git") && root.Info.CommonDirectory == fixture.File(".git"), "Normal metadata locations");
    GitRepository metadata = await GitRepository.OpenAsync(fixture.File(".git"));
    Assert(metadata.Path == root.Path && metadata.Info == root.Info, ".git directory resolves worktree root");
    string literal = ":[root]*?.txt";
    fixture.Write(literal, "root file\n");
    fixture.Write("nested/dir/:unrelated*.txt", "nested unrelated\n");
    await root.StageAsync([literal]);
    GitStatus status = await root.GetStatusAsync();
    Assert(status.Changes.Single(change => change.IsStaged).Path == literal && status.Changes.Single(change => change.IsUntracked).Path == "nested/dir/:unrelated*.txt", "Pathspecs use discovered root regardless of caller subdirectory");
    await root.CommitAsync("Root path commit");
    string previousDirectory = Environment.CurrentDirectory;
    try
    {
        Environment.CurrentDirectory = fixture.Root;
        GitRepository relative = await GitRepository.OpenAsync("repository/nested/dir");
        Assert(relative.Path == root.Path, "Relative opening resolves from unrelated caller cwd");
        Assert(System.Text.Encoding.UTF8.GetString(await relative.GetFileBytesAsync(literal)) == "root file\n", "Preview reads discovered root regardless of cwd");
        fixture.Write(literal, "root file\nmodified\n");
        await relative.StageAsync([literal]);
        Assert((await relative.GetStatusAsync()).Changes.Single(change => change.IsStaged).Path == literal, "Literal staging does not use ambient cwd");
        await relative.UnstageAsync([literal]);
    }
    finally
    {
        Environment.CurrentDirectory = previousDirectory;
    }
    await ExpectAsync<GitCommandException>(() => GitRepository.OpenAsync(fixture.Root));
    await ExpectAsync<DirectoryNotFoundException>(() => GitRepository.OpenAsync(System.IO.Path.Combine(fixture.Root, "missing")));
    using CancellationTokenSource cancelled = new();
    cancelled.Cancel();
    await ExpectAsync<OperationCanceledException>(() => GitRepository.OpenAsync(nested, cancelled.Token));
}

static async Task LinkedDiscoveryAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string linked = System.IO.Path.Combine(fixture.Root, "linked worktree\nwith space");
    await fixture.Repository.AddWorktreeAsync(linked, "HEAD", "linked/test");
    Directory.CreateDirectory(System.IO.Path.Combine(linked, "nested"));
    GitRepository repository = await GitRepository.OpenAsync(System.IO.Path.Combine(linked, "nested"));
    Assert(repository.Path == linked && repository.Info is not null && !repository.IsBare && repository.Info.CommonDirectory == fixture.File(".git"), "Linked root and common directory");
    Assert(repository.Info!.GitDirectory.StartsWith(fixture.File(".git/worktrees/"), StringComparison.Ordinal), "Linked administrative directory");
    GitRepository gitFile = await GitRepository.OpenAsync(System.IO.Path.Combine(linked, ".git"));
    GitRepository metadata = await GitRepository.OpenAsync(repository.Info.GitDirectory);
    Assert(gitFile.Path == linked && metadata.Path == linked && gitFile.Info == repository.Info && metadata.Info == repository.Info, ".git file and linked administrative directory resolve same root");
    Assert((await repository.GetStatusAsync()).Branch == "linked/test", "Linked branch state");
    await fixture.Repository.RemoveWorktreeAsync(linked);
}

static async Task BareAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string barePath = System.IO.Path.Combine(fixture.Root, "bare clone");
    GitResult cloned = await new GitRunner().RunAsync(fixture.Root, ["clone", "--bare", "--", fixture.Repository.Path, barePath]);
    Assert(cloned.Success, "Bare clone");
    GitRepository bare = await GitRepository.OpenAsync(barePath);
    Assert(bare.IsBare && bare.Info!.RootPath == barePath && bare.Info.GitDirectory == barePath && bare.Info.CommonDirectory == barePath, "Bare discovery metadata");
    GitStatus status = await bare.GetStatusAsync();
    Assert(status.Branch == "main" && status.Head == await fixture.Repository.ResolveRevisionAsync("HEAD") && status.Changes.Count == 0, "Bare status exposes branch and HEAD without index");
    Assert((await bare.GetLogAsync()).Single().Subject == "Initial" && (await bare.GetTreeAsync()).Single().Path == "seed.txt", "Bare history and tree");
    Assert(System.Text.Encoding.UTF8.GetString(await bare.GetFileBytesAsync("seed.txt")) == "seed\n", "Bare preview uses object database");
    Assert((await bare.GetCommitDiffAsync("HEAD")).Contains("+seed", StringComparison.Ordinal), "Bare commit diff");
    Assert((await bare.GetStashesAsync()).Count == 0, "Bare stash list is a read-only operation");
    fixture.Write("seed.txt", "stashed from source\n");
    await fixture.Repository.StashAsync("Read stash in bare");
    GitStash sourceStash = (await fixture.Repository.GetStashesAsync()).Single();
    await bare.RunAsync(["fetch", fixture.Repository.Path, "refs/stash"]);
    await bare.RunAsync(["update-ref", "--create-reflog", "-m", "Copied stash", "refs/stash", sourceStash.Hash]);
    GitStash bareStash = (await bare.GetStashesAsync()).Single();
    Assert(bareStash.Hash == sourceStash.Hash && bareStash.Reference == "stash@{0}" && bareStash.Subject == sourceStash.Subject, "Bare stash list reads copied reflog with matching selector, hash and subject");
    InvalidOperationException stage = await ExpectAsync<InvalidOperationException>(() => bare.StageAsync(["seed.txt"]));
    Assert(stage.Message.Contains("bare", StringComparison.Ordinal) && stage.Message.Contains("working tree", StringComparison.Ordinal), "Bare mutation error is actionable");
    await ExpectAsync<InvalidOperationException>(() => bare.UnstageAsync(["seed.txt"]));
    await ExpectAsync<InvalidOperationException>(() => bare.CommitAsync("Cannot commit worktree"));
    await ExpectAsync<InvalidOperationException>(() => bare.CheckoutAsync("main"));
    await ExpectAsync<InvalidOperationException>(() => bare.StashAsync());
    await ExpectAsync<InvalidOperationException>(() => bare.GetDiffAsync("seed.txt"));
    await ExpectAsync<InvalidOperationException>(() => bare.SetSparseCheckoutAsync(["some-dir"]));
    await ExpectAsync<InvalidOperationException>(() => bare.RunAsync(["add", "--", "seed.txt"]));
    await bare.RunAsync(["update-ref", "--no-deref", "HEAD", status.Head!]);
    Assert((await bare.GetStatusAsync()).IsDetached && (await bare.GetStatusAsync()).Head == status.Head, "Detached bare HEAD state");
    await bare.RunAsync(["symbolic-ref", "HEAD", "refs/heads/main"]);
    await bare.ResetAsync("HEAD", GitResetMode.Soft);
    await ExpectAsync<InvalidOperationException>(() => bare.ResetAsync("HEAD", GitResetMode.Hard));
    await bare.CreateBranchAsync("metadata-branch", checkout: false);
    await RepositoryFixture.ConfigureAsync(bare);
    await bare.CreateTagAsync("bare-tag", "HEAD");
    await bare.AddNoteAsync("HEAD", "Bare business note");
    Assert(await bare.GetNoteAsync() == "Bare business note" && (await bare.GetReferencesAsync()).Any(reference => reference.IsTag && reference.ShortName == "bare-tag"), "Metadata mutations remain available in bare repositories");
    GitRepository constructed = new(barePath);
    Assert((await constructed.GetStatusAsync()).Changes.Count == 0 && constructed.IsBare, "Existing root constructor discovers bare status");
    string linked = System.IO.Path.Combine(fixture.Root, "worktree from bare");
    await bare.AddWorktreeAsync(linked, "HEAD", "bare-worktree");
    GitRepository worktree = await GitRepository.OpenAsync(linked);
    Assert(!worktree.IsBare && worktree.Info!.CommonDirectory == barePath && File.Exists(System.IO.Path.Combine(linked, "seed.txt")), "Bare repositories can create usable working trees");
    await bare.RemoveWorktreeAsync(linked);
}

static async Task BareUnbornAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string barePath = System.IO.Path.Combine(fixture.Root, "unborn bare\n");
    GitRepository initialized = await GitRepository.InitAsync(barePath, initialBranch: "new/main", bare: true);
    Assert(initialized.IsBare && initialized.Info is not null, "Init returns discovered metadata");
    GitRepository bare = await GitRepository.OpenAsync(barePath);
    Assert(bare.Path == barePath && bare.Info!.GitDirectory == barePath && bare.Info.CommonDirectory == barePath, "Root's trailing newline is preserved");
    GitStatus status = await bare.GetStatusAsync();
    Assert(status.Branch == "new/main" && status.IsUnborn && status.Changes.Count == 0 && (await bare.GetLogAsync()).Count == 0, "Unborn bare history state");
}

static async Task LiteralPathsAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string[] names = ["two words.txt", "quote'\".txt", "line\nbreak.txt", "tab\tfile.txt", "$(touch injected).txt", "--option.txt", ":[literal]*?.txt", "étoile🐈.txt"];
    foreach (string name in names)
    {
        fixture.Write(name, "first\n");
    }

    Assert((await fixture.Repository.GetStatusAsync()).Changes.Select(change => change.Path).ToHashSet().SetEquals(names), "Raw NUL paths");
    await fixture.Repository.StageAsync([names[6]]);
    GitStatus status = await fixture.Repository.GetStatusAsync();
    Assert(status.Changes.Count(change => change.IsStaged) == 1 && status.Changes.Single(change => change.IsStaged).Path == names[6], "Literal pathspec");
    await fixture.Repository.UnstageAsync([names[6]]);
    await fixture.Repository.StageAsync(names);
    await fixture.Repository.CommitAsync("Names remain literal");
    Assert((await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "All odd names committed");
    Assert(!File.Exists(fixture.File("injected")), "No shell interpolation");
    fixture.Write(names[6], "first\nsecond\n");
    string diff = await fixture.Repository.GetDiffAsync(names[6]);
    Assert(diff.Contains("+second", StringComparison.Ordinal), "Literal diff");
    await fixture.Repository.StageAsync([names[6]]);
    Assert((await fixture.Repository.GetDiffAsync(names[6], staged: true)).Contains("+second", StringComparison.Ordinal), "Staged diff");
}

static async Task RenameAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string destination = "renamed\nfile.txt";
    File.Move(fixture.File("seed.txt"), fixture.File(destination));
    await fixture.Repository.StageAsync(["seed.txt", destination]);
    GitChange renamed = (await fixture.Repository.GetStatusAsync()).Changes.Single();
    Assert(renamed.IndexStatus == 'R' && renamed.Path == destination && renamed.OriginalPath == "seed.txt", "NUL rename pair");
    fixture.Write(destination, "seed\nunstaged\n");
    GitChange mixed = (await fixture.Repository.GetStatusAsync()).Changes.Single();
    Assert(mixed.IndexStatus == 'R' && mixed.WorkTreeStatus == 'M', "Index and worktree both retained");
}

static async Task HistoryAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    fixture.Write("seed.txt", "seed\nsecond\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Unicode été\n\nFirst body line\nSecond body line\n");
    IReadOnlyList<GitCommit> history = await fixture.Repository.GetLogAsync();
    Assert(history.Count == 2 && history[0].Parents.Single() == history[1].Hash, "Parents and order");
    Assert(history[0].Subject == "Unicode été" && history[0].Body == "First body line\nSecond body line\n", "Multiline message");
    Assert(history[0].Sha1Id?.ToString() == history[0].Hash, "Reused ObjectId source");
    Assert(history[0].AuthorName == "Linux Test" && history[0].AuthorEmail == "linux-test@example.invalid", "Author metadata");
    Assert((await fixture.Repository.GetLogAsync(1)).Count == 1, "History bound");
    Assert((await fixture.Repository.GetCommitDiffAsync(history[0].Hash)).Contains("+second", StringComparison.Ordinal), "Commit diff");
    await fixture.Repository.CommitAsync("Amended\n\nBody", amend: true);
    Assert((await fixture.Repository.GetLogAsync()).Count == 2 && (await fixture.Repository.GetLogAsync())[0].Subject == "Amended", "Amend");
}

static async Task FilteredHistoryAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    await fixture.Repository.CreateBranchAsync("other");
    fixture.Write(":branch*.txt", "other branch\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Hidden from main");
    await fixture.Repository.CheckoutAsync("main");
    fixture.Write("main.txt", "main\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Main branch");
    Assert((await fixture.Repository.GetLogAsync()).Count == 3, "Default history includes every branch");
    Assert((await fixture.Repository.GetLogAsync(all: false)).Count == 2, "HEAD-only history");
    Assert((await fixture.Repository.GetLogAsync(path: ":branch*.txt")).Single().Subject == "Hidden from main", "Literal file filter across refs");
    Assert((await fixture.Repository.GetLogAsync(search: "Main branch")).Single().Subject == "Main branch", "Commit message search");
    Assert((await fixture.Repository.GetLogAsync(author: "Linux Test")).Count == 3, "Author filter");
    IReadOnlyList<GitCommit> full = await fixture.Repository.GetLogAsync();
    Assert((await fixture.Repository.GetLogAsync(1, skip: 1)).Single().Hash == full[1].Hash, "Paged history");
}

static async Task LiteralAuthorAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    fixture.Write("first.txt", "first\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync(new GitCommitOptions("Business[1]", Author: "name[1] <literal@example.invalid>"));
    fixture.Write("second.txt", "second\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync(new GitCommitOptions("Business[1]", Author: "name1 <regex@example.invalid>"));
    Assert((await fixture.Repository.GetLogAsync(author: "name[1]")).Single().AuthorEmail == "literal@example.invalid", "Author alone stays literal");
    Assert((await fixture.Repository.GetLogAsync(author: "name[1]", search: "Business[1]")).Single().AuthorEmail == "literal@example.invalid", "Author and message both literal");
}

static async Task LogQueryAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string path = ":[literal]*?.txt";
    await DatedCommitAsync(fixture, "normal.txt", "Needle0\n", "Release[1]", "author1", "committer1", "2024-01-05T12:00:00+02:00");
    string expected = await DatedCommitAsync(fixture, path, "Needle42\n", "Release[1]\n\nBusiness reason", "author[1]", "committer[1]", "2024-02-10T12:00:00+02:00");
    await DatedCommitAsync(fixture, path, "Needle43\n", "Other message", "author[1]", "committer[1]", "2024-03-10T12:00:00+02:00");
    GitLogQuery query = new(Path: path, Author: "AUTHOR[1]", Committer: "COMMITTER[1]", Message: "RELEASE[1]", IgnoreCase: true,
        Since: DateTimeOffset.Parse("2024-02-01T00:00:00+02:00"), Until: DateTimeOffset.Parse("2024-02-29T23:59:59+02:00"));
    Assert((await fixture.Repository.GetLogAsync(query)).Single().Hash == expected, "Date/header/message/path filters combine");
    Assert((await fixture.Repository.GetLogAsync(query with { IgnoreCase = false })).Count == 0, "Case matching is explicit");
    Assert((await fixture.Repository.GetLogAsync(new GitLogQuery(Author: "author[1]", DiffContent: "Needle[0-9]+", Path: path))).Count == 2, "Diff regex stays a regex beside literal author");
    Assert((await fixture.Repository.GetLogAsync(new GitLogQuery(Committer: "committer[1]"))).Count == 2, "Literal committer filter");
    await ExpectAsync<GitCommandException>(() => fixture.Repository.GetLogAsync(new GitLogQuery(DiffContent: "[")));
    await ExpectAsync<ArgumentException>(() => fixture.Repository.GetLogAsync(query with { Since = query.Until, Until = query.Since }));
    await ExpectAsync<ArgumentOutOfRangeException>(() => fixture.Repository.GetLogAsync(new GitLogQuery(Count: 0)));
    await ExpectAsync<ArgumentOutOfRangeException>(() => fixture.Repository.GetLogAsync(new GitLogQuery(Skip: -1)));
}

static async Task<string> DatedCommitAsync(RepositoryFixture fixture, string path, string contents, string message, string author, string committer, string date)
{
    fixture.Write(path, contents);
    await fixture.Repository.StageAllAsync();
    GitResult committed = await new GitRunner().RunAsync(fixture.Repository.Path, ["commit", "--file=-"], standardInput: message,
        environment: new Dictionary<string, string?>
        {
            ["GIT_AUTHOR_NAME"] = author, ["GIT_AUTHOR_EMAIL"] = "author@example.invalid", ["GIT_AUTHOR_DATE"] = date,
            ["GIT_COMMITTER_NAME"] = committer, ["GIT_COMMITTER_EMAIL"] = "committer@example.invalid", ["GIT_COMMITTER_DATE"] = date,
        });
    Assert(committed.Success, committed.StandardError);
    return await fixture.Repository.ResolveRevisionAsync("HEAD");
}

static async Task LogTraversalAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    await fixture.Repository.CreateBranchAsync("topic");
    fixture.Write("topic.txt", "topic\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Topic change");
    string topic = await fixture.Repository.ResolveRevisionAsync("HEAD");
    await fixture.Repository.CheckoutAsync("main");
    fixture.Write("main.txt", "main\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Main change");
    string main = await fixture.Repository.ResolveRevisionAsync("HEAD");
    await fixture.Repository.MergeAsync("topic", noFastForward: true);
    string merge = await fixture.Repository.ResolveRevisionAsync("HEAD");
    GitLogQuery query = new(Revision: "HEAD", All: false);
    IReadOnlyList<GitCommit> firstParent = await fixture.Repository.GetLogAsync(query with { FirstParent = true });
    Assert(firstParent.Count == 3 && firstParent.Any(commit => commit.Hash == merge) && firstParent.All(commit => commit.Hash != topic), "First-parent traversal");
    IReadOnlyList<GitCommit> withoutMerges = await fixture.Repository.GetLogAsync(query with { HideMergeCommits = true });
    Assert(withoutMerges.Count == 3 && withoutMerges.All(commit => commit.Parents.Count < 2) && withoutMerges.Any(commit => commit.Hash == topic), "Hide merges retains side history");
    Assert((await fixture.Repository.GetLogAsync(query with { FirstParent = true, HideMergeCommits = true })).Count == 2, "Traversal flags compose");
    Assert((await fixture.Repository.GetLogAsync(query with { Path = "main.txt", FullHistory = true, SimplifyMerges = true })).Single().Hash == main, "Full file history with simplified merges");
    await fixture.Repository.DeleteBranchAsync("topic");
    await fixture.Repository.CreateTagAsync("kept", merge);
    fixture.Write("main.txt", "main\nintermediate\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Undecorated middle");
    string hidden = await fixture.Repository.ResolveRevisionAsync("HEAD");
    fixture.Write("main.txt", "main\nlast\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Last");
    IReadOnlyList<GitCommit> decorated = await fixture.Repository.GetLogAsync(new GitLogQuery(SimplifyByDecoration: true));
    Assert(decorated.All(commit => commit.Hash != hidden) && decorated.Any(commit => commit.Hash == merge), "Decoration simplification retains tagged commits");
}

static async Task LogReflogAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    fixture.Write("lost.txt", "recover me\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Unreachable after reset");
    string lost = await fixture.Repository.ResolveRevisionAsync("HEAD");
    await fixture.Repository.ResetAsync("HEAD~1", GitResetMode.Hard);
    Assert((await fixture.Repository.GetLogAsync()).Count == 1, "Unreachable excluded from normal refs");
    Assert((await fixture.Repository.GetLogAsync(new GitLogQuery(Reflog: true))).Any(commit => commit.Hash == lost), "Reflog objects included in history");
}

static async Task ReferencesAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    await fixture.Repository.CreateBranchAsync("feature/nested");
    Assert((await fixture.Repository.GetStatusAsync()).Branch == "feature/nested", "Branch checkout");
    await fixture.Repository.RenameBranchAsync("feature/nested", "feature/renamed");
    await fixture.Repository.CreateTagAsync("v1", message: "Annotated version");
    await fixture.Repository.CreateTagAsync("light", "HEAD");
    IReadOnlyList<GitReference> references = await fixture.Repository.GetReferencesAsync();
    Assert(references.Single(reference => reference.IsCurrent).ShortName == "feature/renamed", "Current reference");
    Assert(references.Count(reference => reference.IsTag) == 2, "Tags listed");
    await fixture.Repository.CheckoutAsync("v1", detached: true);
    Assert((await fixture.Repository.GetStatusAsync()).IsDetached, "Detached HEAD");
    await fixture.Repository.CheckoutAsync("main");
    await fixture.Repository.DeleteBranchAsync("feature/renamed");
    await fixture.Repository.DeleteTagAsync("light");
    Assert((await fixture.Repository.GetReferencesAsync()).Count == 2, "Deleted branch and tag");
    await fixture.Repository.CreateBranchAsync("unselected", "HEAD", checkout: false);
    Assert((await fixture.Repository.GetStatusAsync()).Branch == "main", "Create without checkout");
}

static async Task StashAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    fixture.Write("seed.txt", "seed\nstaged\n");
    fixture.Write("untracked\nfile.txt", "untracked\n");
    await fixture.Repository.StageAsync(["seed.txt"]);
    await fixture.Repository.StashAsync("save staged and untracked");
    Assert((await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "Stash clears worktree");
    GitStash stash = (await fixture.Repository.GetStashesAsync()).Single();
    Assert(stash.Reference == "stash@{0}" && stash.Subject.Contains("save staged", StringComparison.Ordinal), "Stash list");
    await fixture.Repository.ApplyStashAsync(stash.Reference);
    GitStatus applied = await fixture.Repository.GetStatusAsync();
    Assert(applied.Changes.Single(change => change.Path == "seed.txt").IsStaged && applied.Changes.Single(change => change.Path == "untracked\nfile.txt").IsUntracked, "Index and untracked restored");
    await fixture.Repository.DropStashAsync(stash.Reference);
    Assert((await fixture.Repository.GetStashesAsync()).Count == 0, "Stash drop");
    await fixture.Repository.StashAsync("again");
    await fixture.Repository.ApplyStashAsync("stash@{0}", pop: true);
    Assert((await fixture.Repository.GetStashesAsync()).Count == 0, "Stash pop");
}

static async Task RemotesAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string remotePath = System.IO.Path.Combine(fixture.Root, "bare remote");
    await GitRepository.InitAsync(remotePath, bare: true);
    await fixture.Repository.AddRemoteAsync("origin", remotePath);
    await fixture.Repository.PushAsync("origin", "main", setUpstream: true);
    GitRemote remote = (await fixture.Repository.GetRemotesAsync()).Single();
    Assert(remote.Name == "origin" && remote.FetchUrls.Single() == remotePath && remote.PushUrls.Single() == remotePath, "Remote URLs");
    Assert((await fixture.Repository.GetReferencesAsync()).Single(reference => reference.IsCurrent).Upstream == "origin/main", "Tracking branch");
    GitRepository clone = await GitRepository.CloneAsync(remotePath, System.IO.Path.Combine(fixture.Root, "clone"));
    await RepositoryFixture.ConfigureAsync(clone);
    await File.WriteAllTextAsync(System.IO.Path.Combine(clone.Path, "from clone.txt"), "remote change\n");
    await clone.StageAllAsync();
    await clone.CommitAsync("Clone change");
    await clone.PushAsync();
    await fixture.Repository.FetchAsync("origin");
    Assert((await fixture.Repository.GetStatusAsync()).Behind == 1, "Fetch behind information");
    await fixture.Repository.PullAsync();
    Assert(File.Exists(fixture.File("from clone.txt")) && (await fixture.Repository.GetStatusAsync()).Behind == 0, "Pull fast forward");
    await fixture.Repository.SetRemoteUrlAsync("origin", remotePath, push: true);
    await fixture.Repository.SetRemoteUrlAsync("origin", remotePath);
    await fixture.Repository.FetchAsync();
    await fixture.Repository.RemoveRemoteAsync("origin");
    Assert((await fixture.Repository.GetRemotesAsync()).Count == 0, "Remote removal");
    GitRemote hosted = new("origin", ["git@github.com:Azreal42/gitextensions.git"], []);
    Assert(hosted.BrowseUri?.AbsoluteUri == "https://github.com/Azreal42/gitextensions", "Reused hosting parser");
}

static async Task WorktreesAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string other = System.IO.Path.Combine(fixture.Root, "work tree\nwith newline");
    await fixture.Repository.AddWorktreeAsync(other, "HEAD", "worktree/test");
    IReadOnlyList<GitWorktree> worktrees = await fixture.Repository.GetWorktreesAsync();
    Assert(worktrees.Count == 2 && worktrees.Single(worktree => worktree.Path == other).Branch == "refs/heads/worktree/test", "NUL worktree parsing");
    await fixture.Repository.RemoveWorktreeAsync(other);
    await fixture.Repository.PruneWorktreesAsync();
    Assert((await fixture.Repository.GetWorktreesAsync()).Count == 1 && !Directory.Exists(other), "Worktree removed");
}

static async Task SubmodulesAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string source = System.IO.Path.Combine(fixture.Root, "submodule source");
    GitRepository sourceRepository = await GitRepository.InitAsync(source);
    await RepositoryFixture.ConfigureAsync(sourceRepository);
    await File.WriteAllTextAsync(System.IO.Path.Combine(source, "child.txt"), "child\n");
    await sourceRepository.StageAllAsync();
    await sourceRepository.CommitAsync("Child commit");
    await fixture.Repository.AddSubmoduleAsync(source, "modules/child module");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Add submodule");
    GitSubmodule module = (await fixture.Repository.GetSubmodulesAsync()).Single();
    Assert(module.Path == "modules/child module" && module.IsInitialized && module.ExpectedHead == module.Head && module.Url == source, "Submodule metadata");
    GitRepository clone = await GitRepository.CloneAsync(fixture.Repository.Path, System.IO.Path.Combine(fixture.Root, "clone without modules"), recursive: false);
    Assert(!(await clone.GetSubmodulesAsync()).Single().IsInitialized, "Uninitialized module");
    await clone.UpdateSubmodulesAsync();
    await clone.SyncSubmodulesAsync();
    Assert((await clone.GetSubmodulesAsync()).Single().IsInitialized, "Module init/update/sync");
}

static async Task SparseCheckoutAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string[] directories = ["selected space", "--option", "[literal]*?", "quoted \"directory", "étoile", "has\ttab", "has\\backslash"];
    foreach (string directory in directories.Append("outside"))
    {
        fixture.Write(directory + "/file.txt", directory + "\n");
    }

    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Sparse directories");
    Assert(!(await fixture.Repository.GetSparseCheckoutAsync()).Enabled, "Sparse checkout initially disabled");
    await fixture.Repository.SetSparseCheckoutAsync(directories, sparseIndex: true);
    GitSparseCheckoutState sparse = await fixture.Repository.GetSparseCheckoutAsync();
    Assert(sparse.Enabled && sparse.ConeMode && sparse.SparseIndex, "Cone and sparse index state");
    Assert(sparse.Patterns.ToHashSet().SetEquals(directories), "Literal directory names listed");
    await fixture.Repository.SetSparseCheckoutAsync(sparse.Patterns, sparseIndex: sparse.SparseIndex);
    Assert((await fixture.Repository.GetSparseCheckoutAsync()).Patterns.ToHashSet().SetEquals(directories), "Quoted cone names round-trip through state/save");
    Assert(GitQuotedPath.Decode("\"\\303\\251toile\"") == "étoile", "Octal bytes decode as UTF8");
    Assert(directories.All(directory => File.Exists(fixture.File(directory + "/file.txt"))) && !File.Exists(fixture.File("outside/file.txt")), "Selected checkout only");
    Assert(File.Exists(fixture.File("seed.txt")), "Cone retains root files");
    await fixture.Repository.AddSparseCheckoutAsync(["outside"]);
    Assert(File.Exists(fixture.File("outside/file.txt")) && (await fixture.Repository.GetSparseCheckoutAsync()).Patterns.Contains("outside"), "Adding directory restores it");
    await fixture.Repository.ReapplySparseCheckoutAsync();
    await fixture.Repository.DisableSparseCheckoutAsync();
    Assert(!(await fixture.Repository.GetSparseCheckoutAsync()).Enabled && directories.Append("outside").All(directory => File.Exists(fixture.File(directory + "/file.txt"))), "Disable restores checkout");
    Assert((await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "Sparse operations do not modify tracked contents");
    await ExpectAsync<ArgumentException>(() => fixture.Repository.SetSparseCheckoutAsync(["../outside"]));
    await ExpectAsync<ArgumentException>(() => fixture.Repository.SetSparseCheckoutAsync(["line\nname"]));
    await ExpectAsync<ArgumentException>(() => fixture.Repository.SetSparseCheckoutAsync([]));
    await ExpectAsync<InvalidOperationException>(() => fixture.Repository.AddSparseCheckoutAsync(["outside"]));
}

static async Task SparsePatternsAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    fixture.Write("included/public/file.txt", "public\n");
    fixture.Write("included/private/file.txt", "private\n");
    fixture.Write("excluded/file.txt", "excluded\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Pattern checkout");
    await fixture.Repository.SetSparsePatternsAsync(["/included/**", "!/included/private/**"]);
    GitSparseCheckoutState sparse = await fixture.Repository.GetSparseCheckoutAsync();
    Assert(sparse.Enabled && !sparse.ConeMode && !sparse.SparseIndex && sparse.Patterns.SequenceEqual(["/included/**", "!/included/private/**"]), "Non-cone pattern state");
    Assert(File.Exists(fixture.File("included/public/file.txt")) && !File.Exists(fixture.File("included/private/file.txt")) && !File.Exists(fixture.File("excluded/file.txt")), "Git pattern selection");
    await ExpectAsync<InvalidOperationException>(() => fixture.Repository.AddSparseCheckoutAsync(["excluded"]));
    await fixture.Repository.DisableSparseCheckoutAsync();
    fixture.Write("excluded/file.txt", "dirty contents\n");
    await fixture.Repository.SetSparseCheckoutAsync(["included"]);
    Assert(File.ReadAllText(fixture.File("excluded/file.txt")) == "dirty contents\n", "Sparse checkout preserves dirty excluded file");
    await fixture.Repository.DisableSparseCheckoutAsync();
    Assert((await fixture.Repository.GetStatusAsync()).Changes.Single().Path == "excluded/file.txt", "Dirty worktree remains explicit");
    await ExpectAsync<ArgumentException>(() => fixture.Repository.SetSparsePatternsAsync(["pattern\nsecond"]));
}

static async Task BisectAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    List<string> commits = [await fixture.Repository.ResolveRevisionAsync("HEAD")];
    for (int version = 1; version <= 8; version++)
    {
        fixture.Write("version.txt", version.ToString());
        await fixture.Repository.StageAllAsync();
        await fixture.Repository.CommitAsync("Version " + version);
        commits.Add(await fixture.Repository.ResolveRevisionAsync("HEAD"));
    }

    GitBisectState inactive = await fixture.Repository.GetBisectStateAsync();
    Assert(!inactive.IsActive && inactive.CurrentRevision == commits[8], "Inactive bisect state");
    await ExpectAsync<InvalidOperationException>(() => fixture.Repository.MarkBisectAsync(GitBisectOption.Good));
    await fixture.Repository.StartBisectAsync(commits[8], [commits[0]]);
    await ExpectAsync<InvalidOperationException>(() => fixture.Repository.StartBisectAsync());
    GitBisectState state = await fixture.Repository.GetBisectStateAsync();
    Assert(state.IsActive && state.StartingRevision == "main" && state.BadRevision == commits[8] && state.GoodRevisions.Contains(commits[0]), "Active bisect endpoints");
    for (int step = 0; state.FirstBadRevision is null && step < 12; step++)
    {
        int version = commits.IndexOf(state.CurrentRevision!);
        Assert(version > 0, "Bisect selects known intermediate commit");
        await fixture.Repository.MarkBisectAsync(version >= 5 ? GitBisectOption.Bad : GitBisectOption.Good);
        state = await fixture.Repository.GetBisectStateAsync();
    }

    Assert(state.FirstBadRevision == commits[5] && state.BadRevision == commits[5] && state.Log.Contains("# first bad commit:", StringComparison.Ordinal), "Exact first bad commit");
    await fixture.Repository.ResetBisectAsync();
    Assert(!(await fixture.Repository.GetBisectStateAsync()).IsActive && (await fixture.Repository.GetStatusAsync()).Branch == "main"
        && await fixture.Repository.ResolveRevisionAsync("HEAD") == commits[8] && File.ReadAllText(fixture.File("version.txt")) == "8", "Reset restores original branch and tree");
    await fixture.Repository.ResetBisectAsync();
    await ExpectAsync<ArgumentOutOfRangeException>(() => fixture.Repository.MarkBisectAsync((GitBisectOption)999));
}

static async Task BisectSkipAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string good = await fixture.Repository.ResolveRevisionAsync("HEAD");
    fixture.Write("version.txt", "1");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Middle");
    string middle = await fixture.Repository.ResolveRevisionAsync("HEAD");
    fixture.Write("version.txt", "2");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Bad");
    string bad = await fixture.Repository.ResolveRevisionAsync("HEAD");
    await fixture.Repository.StartBisectAsync(bad, [good], noCheckout: true, firstParent: true);
    GitBisectState started = await fixture.Repository.GetBisectStateAsync();
    Assert(started.NoCheckout && started.FirstParent && started.CurrentRevision == middle && await fixture.Repository.ResolveRevisionAsync("HEAD") == bad, "No-checkout uses BISECT_HEAD and preserves branch");
    GitResult skipped = await fixture.Repository.MarkBisectAsync(GitBisectOption.Skip);
    GitBisectState state = await fixture.Repository.GetBisectStateAsync();
    Assert(skipped.ExitCode == 2 && state.IsInconclusive && state.SkippedRevisions.Contains(middle), "Skipped-only exit is an explicit inconclusive result");
    Assert(state.PossibleFirstBadRevisions.ToHashSet().SetEquals([middle, bad]), "Inconclusive candidates preserved");
    await fixture.Repository.ResetBisectAsync("main");
    Assert(!File.Exists(fixture.File(".git/BISECT_START")) && (await fixture.Repository.GetStatusAsync()).Branch == "main", "Reset clears session");
}

static async Task BisectPathsAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string path = ":[literal]*?.txt";
    fixture.Write(path, "0");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Good selected path");
    string good = await fixture.Repository.ResolveRevisionAsync("HEAD");
    fixture.Write("other.txt", "other1");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Unrelated first");
    fixture.Write(path, "1");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("First bad selected path");
    string firstBad = await fixture.Repository.ResolveRevisionAsync("HEAD");
    fixture.Write("other.txt", "other2");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Unrelated second");
    fixture.Write(path, "2");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Bad selected path");
    await fixture.Repository.StartBisectAsync("HEAD", [good], [path], noCheckout: true);
    Assert((await fixture.Repository.GetBisectStateAsync()).CurrentRevision == firstBad, "Literal file filter skips unrelated commits");
    await fixture.Repository.MarkBisectAsync(GitBisectOption.Bad, firstBad);
    Assert((await fixture.Repository.GetBisectStateAsync()).FirstBadRevision == firstBad, "Path-limited culprit");
    await fixture.Repository.ResetBisectAsync();
    Assert(File.ReadAllText(fixture.File(path)) == "2", "No-checkout bisect preserves worktree");
}

static async Task NotesAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string initial = await fixture.Repository.ResolveRevisionAsync("HEAD");
    Assert((await fixture.Repository.GetNotesAsync()).Count == 0 && await fixture.Repository.GetNoteAsync() is null, "Missing note is empty state");
    string note = "\nBusiness intent $(touch injected)\n\nKeep trailing spaces  ";
    await fixture.Repository.AddNoteAsync("HEAD", note);
    Assert(await fixture.Repository.GetNoteAsync() == note && !File.Exists(fixture.File("injected")), "Exact note bytes and shell text stays data");
    GitNote entry = (await fixture.Repository.GetNotesAsync()).Single();
    Assert(entry.ObjectHash == initial && entry.NotesRef == "refs/notes/commits" && entry.NoteHash.Length == 40, "Notes object identity");
    await ExpectAsync<GitCommandException>(() => fixture.Repository.AddNoteAsync("HEAD", "duplicate"));
    Assert(await fixture.Repository.GetNoteAsync() == note, "Duplicate does not replace note");
    await fixture.Repository.AddNoteAsync("HEAD", "Replacement", replace: true);
    await fixture.Repository.AddNoteAsync("HEAD", "Review namespace", notesRef: "business/review");
    Assert(await fixture.Repository.GetNoteAsync(notesRef: "business/review") == "Review namespace" && await fixture.Repository.GetNoteAsync() == "Replacement", "Notes namespaces independent");
    await fixture.Repository.RemoveNoteAsync("HEAD", "business/review");
    await fixture.Repository.RemoveNoteAsync("HEAD", "business/review");
    Assert(await fixture.Repository.GetNoteAsync(notesRef: "business/review") is null && (await fixture.Repository.GetNotesAsync("business/review")).Count == 0, "Remove is idempotent");
    await ExpectAsync<ArgumentException>(() => fixture.Repository.AddNoteAsync("HEAD", "bad", notesRef: "refs/heads/main"));
    await ExpectAsync<ArgumentException>(() => fixture.Repository.GetNotesAsync("bad namespace"));
    await fixture.Repository.SetConfigAsync("core.notesRef", "refs/notes/team");
    await fixture.Repository.AddNoteAsync("HEAD", "Team note");
    Assert(await fixture.Repository.GetNoteAsync() == "Team note" && await fixture.Repository.GetNoteAsync(notesRef: "commits") == "Replacement", "Configured default notes reference");
    await fixture.Repository.UnsetConfigAsync("core.notesRef");
    fixture.Write("second.txt", "second\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Second");
    string second = await fixture.Repository.ResolveRevisionAsync("HEAD");
    await Task.WhenAll(fixture.Repository.AddNoteAsync(initial, "First concurrent", notesRef: "concurrent"), fixture.Repository.AddNoteAsync(second, "Second concurrent", notesRef: "concurrent"));
    Assert((await fixture.Repository.GetNotesAsync("concurrent")).Count == 2, "Serialized notes writes do not lose entries");
    Assert(await fixture.Repository.ResolveRevisionAsync("HEAD") == second && (await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "Notes do not rewrite the user branch or worktree");
    using CancellationTokenSource cancelled = new();
    cancelled.Cancel();
    await ExpectAsync<OperationCanceledException>(() => fixture.Repository.AddNoteAsync("HEAD", "Cancelled", notesRef: "cancelled", cancellationToken: cancelled.Token));
    Assert((await fixture.Repository.GetNotesAsync("cancelled")).Count == 0, "Cancelled note does not mutate state");
}

static async Task BlameAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    fixture.Write("seed.txt", "seed\nwith\ttab\nété\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Lines");
    IReadOnlyList<GitBlameLine> blame = await fixture.Repository.GetBlameAsync("seed.txt");
    Assert(blame.Count == 3 && blame[0].Summary == "Initial" && blame[1].Text == "with\ttab" && blame[2].Text == "été", "Blame per line");
    Assert(blame.Select(line => line.LineNumber).SequenceEqual([1, 2, 3]) && blame.All(line => line.AuthorEmail == "linux-test@example.invalid"), "Line numbers/authors");
    Assert((await fixture.Repository.GetBlameAsync("seed.txt", "HEAD~1")).Count == 1, "Historical blame");
}

static async Task TreeAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string name = "nested/path\nwith\ttabs.bin";
    byte[] bytes = [0, 1, 2, 255, 128, 13, 10, 0];
    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fixture.File(name))!);
    await File.WriteAllBytesAsync(fixture.File(name), bytes);
    fixture.Write(":[literal]*.txt", "literal contents\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Tree contents");
    IReadOnlyList<GitTreeEntry> tree = await fixture.Repository.GetTreeAsync();
    Assert(tree.Count == 3 && tree.Single(entry => entry.Path == name).Type == "blob", "NUL tree paths");
    Assert((await fixture.Repository.GetFileBytesAsync(name)).SequenceEqual(bytes), "Binary bytes preserved");
    Assert((await fixture.Repository.GetTreeAsync(path: ":[literal]*.txt")).Single().Path == ":[literal]*.txt", "Literal tree path filter");
    Assert(System.Text.Encoding.UTF8.GetString(await fixture.Repository.GetFileBytesAsync(":[literal]*.txt")) == "literal contents\n", "Colon in blob path");
}

static async Task HunksAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string[] original = Enumerable.Range(1, 40).Select(number => $"line {number}").ToArray();
    fixture.Write("wide.txt", string.Join('\n', original) + "\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Wide file");
    string[] modified = original.ToArray();
    modified[1] = "changed 2";
    modified[36] = "changed 37";
    fixture.Write("wide.txt", string.Join('\n', modified) + "\n");
    GitPatchFile patch = GitPatch.Parse(await fixture.Repository.GetDiffAsync("wide.txt")).Single();
    Assert(patch.Hunks.Count == 2 && patch.Hunks[1].OldStart > patch.Hunks[0].OldStart, "Distinct hunks");
    await fixture.Repository.ApplyPatchAsync(patch.SelectHunks([1]));
    string staged = await fixture.Repository.GetDiffAsync("wide.txt", staged: true);
    string unstaged = await fixture.Repository.GetDiffAsync("wide.txt");
    Assert(staged.Contains("+changed 37", StringComparison.Ordinal) && !staged.Contains("+changed 2", StringComparison.Ordinal), "Only selected hunk staged");
    Assert(unstaged.Contains("+changed 2", StringComparison.Ordinal) && !unstaged.Contains("+changed 37", StringComparison.Ordinal), "Remaining hunk unstaged");
    await fixture.Repository.ApplyPatchAsync(GitPatch.Parse(staged).Single().SelectHunks([0]), reverse: true);
    Assert((await fixture.Repository.GetDiffAsync("wide.txt", staged: true)).Length == 0 && File.ReadAllText(fixture.File("wide.txt")).Contains("changed 37", StringComparison.Ordinal), "Reverse unstages and preserves worktree");
    fixture.Write("no-newline.txt", "original");
    await fixture.Repository.StageAsync(["no-newline.txt"]);
    await fixture.Repository.CommitAsync("Without newline");
    fixture.Write("no-newline.txt", "modified");
    GitPatchFile newlinePatch = GitPatch.Parse(await fixture.Repository.GetDiffAsync("no-newline.txt")).Single();
    Assert(newlinePatch.Hunks.Single().Text.Contains("\\ No newline at end of file", StringComparison.Ordinal), "Newline marker retained");
    await fixture.Repository.ApplyPatchAsync(newlinePatch.SelectHunks([0]));
    Assert(System.Text.Encoding.UTF8.GetString(await fixture.Repository.GetFileBytesAsync("no-newline.txt")) == "original", "HEAD blob unaffected by partial staging");
    await ExpectAsync<GitCommandException>(() => fixture.Repository.ApplyPatchAsync("invalid patch\n"));
}

static async Task LinesAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    fixture.Write("lines.txt", "one\ntwo\nthree\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Lines base");
    fixture.Write("lines.txt", "ONE\nTWO\nthree\n");
    string diff = await fixture.Repository.GetDiffAsync("lines.txt");
    int position = diff.IndexOf("+ONE", StringComparison.Ordinal);
    string patch = GitPatch.SelectLines(diff, position, "+ONE".Length - 1) ?? throw new InvalidOperationException("Selected line patch");
    await fixture.Repository.ApplyPatchAsync(patch);
    string staged = await fixture.Repository.GetDiffAsync("lines.txt", staged: true);
    Assert(staged.Contains("+ONE", StringComparison.Ordinal) && !staged.Contains("+TWO", StringComparison.Ordinal) && !staged.Contains("-one", StringComparison.Ordinal), "Only selected addition staged");
    string unstage = GitPatch.SelectLines(staged, staged.IndexOf("+ONE", StringComparison.Ordinal), "+ONE".Length - 1, staged: true, reverse: true)!;
    await fixture.Repository.ApplyPatchAsync(unstage, reverse: true);
    Assert((await fixture.Repository.GetDiffAsync("lines.txt", staged: true)).Length == 0, "Selected line unstaged");
    string discard = GitPatch.SelectResetLines(diff, diff.IndexOf("+TWO", StringComparison.Ordinal), "+TWO".Length - 1)!;
    await fixture.Repository.ApplyPatchAsync(discard, staged: false);
    string content = await File.ReadAllTextAsync(fixture.File("lines.txt"));
    Assert(content.Contains("ONE", StringComparison.Ordinal) && !content.Contains("TWO", StringComparison.Ordinal), "Only selected addition discarded");
}

static async Task SearchAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string name = ":search\nfile*.txt";
    fixture.Write(name, "first\nTARGET: text\nthird\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Search source");
    Assert((await fixture.Repository.SearchAsync("absent")).Count == 0, "No match is successful");
    GitSearchMatch match = (await fixture.Repository.SearchAsync("target", path: name, ignoreCase: true)).Single();
    Assert(match.Path == name && match.LineNumber == 2 && match.Text == "TARGET: text", "NUL filename/line number parsing");
    fixture.Write(name, "modified\n");
    Assert((await fixture.Repository.SearchAsync("TARGET", path: name)).Count == 0, "Search reads current worktree");
    Assert((await fixture.Repository.SearchAsync("TARGET", path: name, revision: "HEAD")).Single().Text == "TARGET: text", "Historical search");
    await ExpectAsync<GitCommandException>(() => fixture.Repository.SearchAsync("[", regex: true));
}

static async Task ReflogResetAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    fixture.Write("seed.txt", "seed\nsecond\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Second");
    IReadOnlyList<GitReflogEntry> reflog = await fixture.Repository.GetReflogAsync();
    Assert(reflog.Count == 2 && reflog[0].Selector == "HEAD@{0}" && reflog[0].Description.Contains("Second", StringComparison.Ordinal), "Reflog metadata");
    string original = reflog[1].Hash;
    await fixture.Repository.ResetAsync("HEAD~1", GitResetMode.Soft);
    Assert(await fixture.Repository.ResolveRevisionAsync("HEAD") == original && (await fixture.Repository.GetStatusAsync()).Changes.Single().IsStaged, "Soft reset keeps index");
    await fixture.Repository.ResetAsync("HEAD", GitResetMode.Mixed);
    GitChange mixed = (await fixture.Repository.GetStatusAsync()).Changes.Single();
    Assert(!mixed.IsStaged && mixed.WorkTreeStatus == 'M', "Mixed reset keeps worktree");
    await fixture.Repository.ResetAsync("HEAD", GitResetMode.Hard);
    Assert(File.ReadAllText(fixture.File("seed.txt")) == "seed\n" && (await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "Hard reset restores worktree");
    await ExpectAsync<ArgumentOutOfRangeException>(() => fixture.Repository.ResetAsync("HEAD", (GitResetMode)999));
    Assert((await fixture.Repository.GetReflogAsync(1)).Count == 1, "Reflog limit");
}

static async Task CommitOptionsAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    fixture.Write("seed.txt", "seed\noptions\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync(new GitCommitOptions("Options", SignOff: true, Sign: false, Author: "Other Author <other@example.invalid>"));
    GitCommit commit = (await fixture.Repository.GetLogAsync(1, "HEAD")).Single();
    Assert(commit.AuthorName == "Other Author" && commit.Body.Contains("Signed-off-by: Linux Test <linux-test@example.invalid>", StringComparison.Ordinal), "Author and signoff");
    await fixture.Repository.CommitAsync(new GitCommitOptions("Reset author", Amend: true, ResetAuthor: true));
    Assert((await fixture.Repository.GetLogAsync(1, "HEAD")).Single().AuthorName == "Linux Test", "Amend resets author");
    fixture.Write("seed.txt", "seed\nfixup\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync(new GitCommitOptions(FixupRevision: "HEAD", Sign: false));
    Assert((await fixture.Repository.GetLogAsync(1, "HEAD")).Single().Subject == "fixup! Reset author", "Fixup subject");
    fixture.Write("seed.txt", "seed\nsquash\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync(new GitCommitOptions(SquashRevision: "HEAD~1", Sign: false));
    Assert((await fixture.Repository.GetLogAsync(1, "HEAD")).Single().Subject == "squash! Reset author", "Squash subject");
    fixture.Write("message-template.txt", "Template subject\n\nBusiness reason\n");
    await fixture.Repository.SetConfigAsync("commit.template", "message-template.txt");
    Assert(await fixture.Repository.GetCommitTemplateAsync() == "Template subject\n\nBusiness reason\n", "Configured template read");
    await fixture.Repository.CommitAsync(new GitCommitOptions(TemplatePath: "message-template.txt", AllowEmpty: true));
    Assert((await fixture.Repository.GetLogAsync(1, "HEAD")).Single().Subject == "Template subject", "Template commit");
    await ExpectAsync<ArgumentException>(() => fixture.Repository.CommitAsync(new GitCommitOptions("Bad", FixupRevision: "HEAD", SquashRevision: "HEAD")));
    await ExpectAsync<ArgumentException>(() => fixture.Repository.CommitAsync(new GitCommitOptions("Bad", Sign: false, SigningKey: "key")));
    await ExpectAsync<GitCommandException>(() => fixture.Repository.CommitAsync(new GitCommitOptions("Invalid signing key", Sign: true, SigningKey: "missing-linux-test-key", AllowEmpty: true)));
}

static async Task SigningAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string key = System.IO.Path.Combine(fixture.Root, "signing key");
    GitResult generated = await new GitRunner("ssh-keygen").RunAsync(fixture.Root, ["-q", "-t", "ed25519", "-N", "", "-f", key]);
    Assert(generated.Success, "SSH signing key generated");
    await fixture.Repository.SetConfigAsync("gpg.format", "ssh");
    await fixture.Repository.CommitAsync(new GitCommitOptions("Signed SSH commit", Sign: true, SigningKey: key, AllowEmpty: true));
    string raw = (await fixture.Repository.RunAsync(["cat-file", "commit", "HEAD"])).StandardOutput;
    Assert(raw.Contains("gpgsig -----BEGIN SSH SIGNATURE-----", StringComparison.Ordinal), "Signature stored in commit");
    string signers = System.IO.Path.Combine(fixture.Root, "allowed signers");
    await File.WriteAllTextAsync(signers, "linux-test@example.invalid " + await File.ReadAllTextAsync(key + ".pub"));
    await fixture.Repository.SetConfigAsync("gpg.ssh.allowedSignersFile", signers);
    Assert((await fixture.Repository.RunAsync(["verify-commit", "HEAD"])).Success, "Git verifies SSH signature");
}

static string RebaseExecutable()
{
    string directory = System.IO.Path.GetDirectoryName(typeof(RepositoryFixture).Assembly.Location)!;
    return System.IO.Path.Combine(directory, "GitExtensions.Core.Tests");
}

static async Task InteractiveRebaseAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string upstream = await fixture.Repository.ResolveRevisionAsync("HEAD");
    foreach (string name in new[] { "a", "b", "c" })
    {
        fixture.Write(name + ".txt", name + "\n");
        await fixture.Repository.StageAllAsync();
        await fixture.Repository.CommitAsync("Add " + name);
    }

    GitRebasePlan plan = await fixture.Repository.GetRebasePlanAsync(upstream);
    Assert(plan.Steps.Select(step => step.Subject).SequenceEqual(["Add a", "Add b", "Add c"]), "Oldest-first editable plan");
    GitRebaseStep[] edited = [plan.Steps[1], plan.Steps[0] with { Action = GitRebaseAction.Reword, Message = "New subject $(touch injected)\n\nBusiness message" }, plan.Steps[2] with { Action = GitRebaseAction.Drop }];
    await fixture.Repository.InteractiveRebaseAsync(plan with { Steps = edited }, RebaseExecutable());
    IReadOnlyList<GitCommit> history = await fixture.Repository.GetLogAsync(revision: "HEAD");
    Assert(history.Count == 3 && history[0].Subject == "New subject $(touch injected)" && history[1].Subject == "Add b", "Reordered/reworded history");
    Assert(!File.Exists(fixture.File("c.txt")) && !File.Exists(fixture.File("injected")), "Drop and no command interpolation");
    Assert(!Directory.Exists(fixture.File(".git/gitextensions-rebase")), "Completed rebase sidecar cleaned");
    await ExpectAsync<InvalidOperationException>(() => fixture.Repository.InteractiveRebaseAsync(plan, RebaseExecutable()));
    GitRebasePlan refreshed = await fixture.Repository.GetRebasePlanAsync(upstream);
    await ExpectAsync<ArgumentException>(() => fixture.Repository.InteractiveRebaseAsync(refreshed with { Steps = [refreshed.Steps[0], refreshed.Steps[0]] }, RebaseExecutable()));
    await ExpectAsync<ArgumentOutOfRangeException>(() => fixture.Repository.InteractiveRebaseAsync(refreshed with { Steps = [refreshed.Steps[0] with { Action = (GitRebaseAction)999 }, refreshed.Steps[1]] }, RebaseExecutable()));
}

static async Task InteractiveSquashAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string upstream = await fixture.Repository.ResolveRevisionAsync("HEAD");
    foreach (string name in new[] { "a", "b", "c" })
    {
        fixture.Write(name + ".txt", name + "\n");
        await fixture.Repository.StageAllAsync();
        await fixture.Repository.CommitAsync("Add " + name);
    }

    GitRebasePlan plan = await fixture.Repository.GetRebasePlanAsync(upstream);
    await fixture.Repository.InteractiveRebaseAsync(plan with { Steps = [plan.Steps[0], plan.Steps[1] with { Action = GitRebaseAction.Squash }, plan.Steps[2] with { Action = GitRebaseAction.Fixup }] }, RebaseExecutable());
    IReadOnlyList<GitCommit> history = await fixture.Repository.GetLogAsync(revision: "HEAD");
    Assert(history.Count == 2 && history[0].Subject == "Add a" && history[0].Body.Contains("Add b", StringComparison.Ordinal), "Squash and fixup combined history");
    Assert(File.Exists(fixture.File("a.txt")) && File.Exists(fixture.File("b.txt")) && File.Exists(fixture.File("c.txt")), "Squash keeps tree changes");
}

static async Task InteractiveSquashMessageAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string upstream = await fixture.Repository.ResolveRevisionAsync("HEAD");
    foreach (string name in new[] { "a", "b", "c", "d" })
    {
        fixture.Write(name + ".txt", name + "\n");
        await fixture.Repository.StageAllAsync();
        await fixture.Repository.CommitAsync("Add " + name);
    }

    GitRebasePlan plan = await fixture.Repository.GetRebasePlanAsync(upstream);
    GitRebaseStep[] steps =
    [
        plan.Steps[0],
        plan.Steps[1] with { Action = GitRebaseAction.Squash, Message = "Combined subject $(touch injected)\n\nChosen business reason" },
        plan.Steps[2] with { Action = GitRebaseAction.Fixup },
        plan.Steps[3] with { Action = GitRebaseAction.Reword, Message = "Final reword" },
    ];
    await fixture.Repository.InteractiveRebaseAsync(plan with { Steps = steps }, RebaseExecutable());
    IReadOnlyList<GitCommit> history = await fixture.Repository.GetLogAsync(revision: "HEAD");
    Assert(history.Count == 3 && history[0].Subject == "Final reword" && history[1].Subject == "Combined subject $(touch injected)", "Edited squash and later reword");
    Assert(history[1].Body == "Chosen business reason\n" && !File.Exists(fixture.File("injected")), "Custom squash message stays data");
}

static async Task InteractiveEditAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string upstream = await fixture.Repository.ResolveRevisionAsync("HEAD");
    foreach (string name in new[] { "a", "b" })
    {
        fixture.Write(name + ".txt", name + "\n");
        await fixture.Repository.StageAllAsync();
        await fixture.Repository.CommitAsync("Add " + name);
    }

    GitRebasePlan plan = await fixture.Repository.GetRebasePlanAsync(upstream);
    await fixture.Repository.InteractiveRebaseAsync(plan with { Steps = [plan.Steps[0] with { Action = GitRebaseAction.Edit }, plan.Steps[1] with { Action = GitRebaseAction.Reword, Message = "Edited second" }] }, RebaseExecutable());
    Assert((await fixture.Repository.GetStatusAsync()).IsDetached && Directory.Exists(fixture.File(".git/gitextensions-rebase")), "Edit pauses and persists plan");
    GitRepository reopened = new(fixture.Repository.Path);
    await reopened.CommitAsync("Edited first", amend: true);
    await reopened.ManageOperationAsync("rebase", GitOperationAction.Continue);
    IReadOnlyList<GitCommit> history = await reopened.GetLogAsync(revision: "HEAD");
    Assert(history[0].Subject == "Edited second" && history[1].Subject == "Edited first", "New repository instance continues with saved messages");
    Assert((await reopened.GetStatusAsync()).Branch == "main" && !Directory.Exists(fixture.File(".git/gitextensions-rebase")), "Continue restores branch and cleans plan");
}

static async Task MergeConflictAsync()
{
    using RepositoryFixture fixture = await ConflictingBranchesAsync();
    string[] parents = [await fixture.Repository.ResolveRevisionAsync("HEAD"), await fixture.Repository.ResolveRevisionAsync("feature")];
    GitCommandException failure = await ExpectAsync<GitCommandException>(() => fixture.Repository.MergeAsync("feature"));
    Assert(failure.Result.ExitCode == 1, "Merge reports failure");
    Assert((await fixture.Repository.GetStatusAsync()).Changes.Single().IsConflict, "Unmerged porcelain");
    fixture.Write("seed.txt", "resolved\n");
    await fixture.Repository.StageAsync(["seed.txt"]);
    await fixture.Repository.ManageOperationAsync("merge", GitOperationAction.Continue);
    GitCommit merge = (await fixture.Repository.GetLogAsync(1, "HEAD")).Single();
    Assert(merge.Parents.Count == 2 && merge.Parents.SequenceEqual(parents) && (await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "Merge continue");
    Assert(System.Text.Encoding.UTF8.GetString(await fixture.Repository.GetFileBytesAsync("seed.txt", merge.Hash)) == "resolved\n", "Merge commits the staged resolution");
}

static async Task MergeAbortAsync()
{
    using RepositoryFixture fixture = await ConflictingBranchesAsync();
    await ExpectAsync<GitCommandException>(() => fixture.Repository.MergeAsync("feature"));
    await fixture.Repository.ManageOperationAsync("merge", GitOperationAction.Abort);
    Assert((await fixture.Repository.GetStatusAsync()).Changes.Count == 0 && File.ReadAllText(fixture.File("seed.txt")) == "main\n", "Merge abort");
}

static async Task SequencerAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    await fixture.Repository.CreateBranchAsync("feature");
    fixture.Write("feature.txt", "feature\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Feature change");
    string feature = await fixture.Repository.ResolveRevisionAsync("HEAD");
    await fixture.Repository.CheckoutAsync("main");
    fixture.Write("main.txt", "main\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Main change");
    await fixture.Repository.CherryPickAsync(feature);
    Assert(File.Exists(fixture.File("feature.txt")), "Cherry-pick");
    await fixture.Repository.RevertAsync("HEAD");
    Assert(!File.Exists(fixture.File("feature.txt")), "Revert");
    await fixture.Repository.CheckoutAsync("feature");
    await fixture.Repository.RebaseAsync("main");
    Assert(File.Exists(fixture.File("main.txt")) && (await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "Rebase");
    fixture.Write("after-rebase.txt", "after rebase\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("After rebase");
    string rebasedFeature = await fixture.Repository.ResolveRevisionAsync("HEAD");
    await fixture.Repository.CheckoutAsync("main");
    string main = await fixture.Repository.ResolveRevisionAsync("HEAD");
    await fixture.Repository.MergeAsync("feature", noFastForward: true);
    GitCommit merge = (await fixture.Repository.GetLogAsync(1, "HEAD")).Single();
    Assert(merge.Parents.Count == 2 && merge.Parents.SequenceEqual([main, rebasedFeature]), "Non fast-forward merge");
}

static async Task SequencerRecoveryAsync()
{
    using RepositoryFixture fixture = await ConflictingBranchesAsync();
    await ExpectAsync<GitCommandException>(() => fixture.Repository.CherryPickAsync("feature"));
    await fixture.Repository.ManageOperationAsync("cherry-pick", GitOperationAction.Abort);
    Assert((await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "Cherry-pick abort");
    await fixture.Repository.CheckoutAsync("feature");
    await ExpectAsync<GitCommandException>(() => fixture.Repository.RebaseAsync("main"));
    await fixture.Repository.ManageOperationAsync("rebase", GitOperationAction.Abort);
    Assert((await fixture.Repository.GetStatusAsync()).Branch == "feature" && File.ReadAllText(fixture.File("seed.txt")) == "feature\n", "Rebase abort");
    await ExpectAsync<GitCommandException>(() => fixture.Repository.RebaseAsync("main"));
    await fixture.Repository.ManageOperationAsync("rebase", GitOperationAction.Skip);
    Assert((await fixture.Repository.GetStatusAsync()).Branch == "feature" && File.ReadAllText(fixture.File("seed.txt")) == "main\n", "Rebase skip");
}

static async Task ConfigAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    await fixture.Repository.SetConfigAsync("test.message", "First line\nSecond line");
    Assert((await fixture.Repository.GetConfigAsync("^test\\.message$")).Single().Value == "First line\nSecond line", "Multiline config");
    await fixture.Repository.RunAsync(["config", "--add", "test.message", "Another"]);
    Assert((await fixture.Repository.GetConfigAsync("^test\\.message$")).Count == 2, "Duplicate config");
    await fixture.Repository.SetConfigAsync("test.message", "Replaced");
    Assert((await fixture.Repository.GetConfigAsync("^test\\.message$")).Single().Value == "Replaced", "Replace all");
    await fixture.Repository.UnsetConfigAsync("test.message");
    await fixture.Repository.UnsetConfigAsync("test.message");
    Assert((await fixture.Repository.GetConfigAsync("^test\\.message$")).Count == 0, "Missing config");
}

static async Task ErrorsAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    GitCommandException error = await ExpectAsync<GitCommandException>(() => fixture.Repository.RunAsync(["not-a-command"]));
    Assert(error.Result.ExitCode != 0 && error.Result.StandardError.Length > 0 && error.Arguments[0] == "not-a-command", "Error result retained");
    GitResult rejectedInput = await new GitRunner().RunAsync(fixture.Repository.Path, ["not-a-command"], standardInput: new string('X', 1_000_000));
    Assert(rejectedInput.ExitCode != 0 && rejectedInput.StandardError.Contains("not-a-command", StringComparison.Ordinal), "Early pipe closure keeps Git error");
    await ExpectAsync<GitCommandException>(() => fixture.Repository.GetLogAsync(revision: "nonexistent"));
    await ExpectAsync<ArgumentException>(() => fixture.Repository.StageAsync(["../outside"]));
    await ExpectAsync<ArgumentException>(() => fixture.Repository.CreateBranchAsync("--help"));
    await ExpectAsync<ArgumentException>(() => fixture.Repository.ResolveRevisionAsync("--help"));
    await ExpectAsync<ArgumentException>(() => fixture.Repository.StageAsync([]));
    await ExpectAsync<ArgumentException>(() => new GitRunner().RunAsync(fixture.Repository.Path, ["status\0oops"]));
    GitRepository outside = new(fixture.Root);
    Assert(!await outside.IsRepositoryAsync(), "Invalid repository");
    await ExpectAsync<InvalidOperationException>(() => outside.RunAsync(["status"]));
}

static async Task Sha256Async()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(seed: false);
    string path = System.IO.Path.Combine(fixture.Root, "sha256 repository");
    Directory.CreateDirectory(path);
    GitResult initialized = await new GitRunner().RunAsync(path, ["init", "--object-format=sha256", "--initial-branch=main"]);
    Assert(initialized.Success, "SHA256 init");
    GitRepository repository = new(path);
    await RepositoryFixture.ConfigureAsync(repository);
    string file = System.IO.Path.Combine(path, "sha256.txt");
    await File.WriteAllTextAsync(file, "SHA256 contents\n");
    await repository.StageAllAsync();
    await repository.CommitAsync("SHA256 commit");
    await File.WriteAllTextAsync(file, "SHA256 contents\nchanged\n");
    GitStatus status = await repository.GetStatusAsync();
    Assert(status.Head?.Length == 64 && status.Changes.Single().Path == "sha256.txt", "Field count instead of SHA1 byte offsets");
    GitCommit commit = (await repository.GetLogAsync()).Single();
    Assert(commit.Hash.Length == 64 && commit.Sha1Id is null, "SHA256 identities preserved");
}

static async Task ArchiveExportsAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string literal = ":[archive]*? λ\n.bin";
    byte[] firstBytes = [0, 255, 10, 128, 42];
    await File.WriteAllBytesAsync(fixture.File(literal), firstBytes);
    fixture.Write("old-name.txt", "rename content\n");
    fixture.Write("ignored.txt", "export ignored\n");
    fixture.Write(".gitattributes", "ignored.txt export-ignore\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Archive baseline");
    string baseline = await fixture.Repository.ResolveRevisionAsync("HEAD");
    string output = System.IO.Path.Combine(fixture.Root, "exports λ\n");
    string zipPath = System.IO.Path.Combine(output, "literal '$().zip");
    GitArchiveResult zip = await fixture.Repository.ExportArchiveAsync(new(zipPath, Prefix: "release λ", Paths: [literal], CompressionLevel: 0));
    Assert(zip.OutputPath == zipPath && zip.Revision == baseline && zip.BytesWritten == new FileInfo(zipPath).Length && zip.Paths.Single() == literal, "Archive result records resolved revision and file metadata");
    using (ZipArchive archive = ZipFile.OpenRead(zipPath))
    {
        ZipArchiveEntry file = archive.Entries.Single(entry => !entry.FullName.EndsWith('/'));
        Assert(file.FullName == "release λ/" + literal, "ZIP literal path and normalized prefix");
        using MemoryStream bytes = new();
        await file.Open().CopyToAsync(bytes);
        Assert(bytes.ToArray().SequenceEqual(firstBytes), "ZIP bytes are exact, including NUL and invalid text bytes");
    }

    byte[] sentinel = await File.ReadAllBytesAsync(zipPath);
    await ExpectAsync<IOException>(() => fixture.Repository.ExportArchiveAsync(new(zipPath)));
    Assert((await File.ReadAllBytesAsync(zipPath)).SequenceEqual(sentinel), "Existing export remains byte-identical");
    string allZip = System.IO.Path.Combine(output, "all.zip");
    await fixture.Repository.ExportArchiveAsync(new(allZip));
    using (ZipArchive archive = ZipFile.OpenRead(allZip))
    {
        Assert(archive.Entries.Any(entry => entry.FullName == literal) && archive.Entries.All(entry => entry.FullName != "ignored.txt"), "Git archive keeps committed export-ignore attributes");
    }

    byte[] changedBytes = [42, 0, 10, 255];
    await File.WriteAllBytesAsync(fixture.File(literal), changedBytes);
    File.Delete(fixture.File("seed.txt"));
    const string renamed = "renamed [a]*?\n.txt";
    File.Move(fixture.File("old-name.txt"), fixture.File(renamed));
    fixture.Write("added/file.txt", "added\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Archive changes");
    string tarPath = System.IO.Path.Combine(output, "changed.tar");
    GitArchiveResult tar = await fixture.Repository.ExportArchiveAsync(new(tarPath, Format: GitArchiveFormat.Tar, Prefix: "changes/", DiffRevision: baseline));
    Assert(tar.Paths.ToHashSet(StringComparer.Ordinal).SetEquals([literal, renamed, "added/file.txt"]), "Comparison archives changed target files and omits deleted paths");
    Dictionary<string, byte[]> entries = [];
    await using (FileStream stream = File.OpenRead(tarPath))
    using (TarReader reader = new(stream))
    {
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.DataStream is null) { continue; }
            using MemoryStream bytes = new();
            await entry.DataStream.CopyToAsync(bytes);
            entries.Add(entry.Name, bytes.ToArray());
        }
    }
    Assert(entries.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(tar.Paths.Select(path => "changes/" + path)) && entries["changes/" + literal].SequenceEqual(changedBytes), "TAR archive has the exact filtered names and binary contents");
    string missing = System.IO.Path.Combine(output, "missing.zip");
    await ExpectAsync<GitCommandException>(() => fixture.Repository.ExportArchiveAsync(new(missing, Paths: ["missing path"])));
    Assert(!File.Exists(missing) && !Directory.GetFiles(output, ".gitextensions-archive-*").Any(), "Failed Git archive leaves no final or temporary artifact");
    await ExpectAsync<InvalidOperationException>(() => fixture.Repository.ExportArchiveAsync(new(missing, DiffRevision: "HEAD")));
    await ExpectAsync<ArgumentException>(() => fixture.Repository.ExportArchiveAsync(new(missing, Prefix: "../outside")));
    await ExpectAsync<ArgumentException>(() => fixture.Repository.ExportArchiveAsync(new(missing, Paths: [literal], DiffRevision: baseline)));
    await ExpectAsync<ArgumentException>(() => fixture.Repository.ExportArchiveAsync(new(missing, Format: GitArchiveFormat.Tar, CompressionLevel: 5)));
    using CancellationTokenSource cancelled = new();
    cancelled.Cancel();
    await ExpectAsync<OperationCanceledException>(() => fixture.Repository.ExportArchiveAsync(new(missing), cancelled.Token));
    Assert(!File.Exists(missing) && (await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "Cancelled exports leave no artifacts and exports do not dirty the repository");

    string barePath = System.IO.Path.Combine(fixture.Root, "bare archive.git");
    GitResult clone = await new GitRunner().RunAsync(fixture.Root, ["clone", "--bare", "--", fixture.Repository.Path, barePath]);
    Assert(clone.Success, "Bare archive source");
    GitRepository bare = await GitRepository.OpenAsync(barePath);
    await bare.ExportArchiveAsync(new(System.IO.Path.Combine(output, "bare.zip"), Paths: [literal]));
    Assert(bare.IsBare && (await bare.GetStatusAsync()).Changes.Count == 0, "Archives work from a bare object database");
}

static async Task FormatPatchExportsAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string root = await fixture.Repository.ResolveRevisionAsync("HEAD");
    List<string> revisions = [];
    for (int index = 1; index <= 4; index++)
    {
        fixture.Write($"feature-{index}.txt", $"feature {index}\n");
        if (index == 1) { await File.WriteAllBytesAsync(fixture.File("binary λ\n.bin"), [0, 255, 128, 10]); }
        await fixture.Repository.StageAllAsync();
        await fixture.Repository.CommitAsync(new GitCommitOptions(Message: "Feature " + index, Author: "Patch Author <patch-author@example.invalid>"));
        revisions.Add(await fixture.Repository.ResolveRevisionAsync("HEAD"));
    }
    string exports = System.IO.Path.Combine(fixture.Root, "patch exports λ\n");
    GitFormatPatchResult initial = await fixture.Repository.FormatPatchAsync(new(System.IO.Path.Combine(exports, "root"), SelectedRevisions: [root]));
    Assert(initial.Files.Count == 1 && (await File.ReadAllTextAsync(initial.Files[0])).Contains("Subject: [PATCH] Initial", StringComparison.Ordinal), "Selecting root exports the root commit");
    GitFormatPatchResult single = await fixture.Repository.FormatPatchAsync(new(System.IO.Path.Combine(exports, "single"), SelectedRevisions: [revisions[1]]));
    Assert(single.Files.Count == 1 && (await File.ReadAllTextAsync(single.Files[0])).Contains("Subject: [PATCH] Feature 2", StringComparison.Ordinal), "One selected revision excludes its parent");
    GitFormatPatchResult range = await fixture.Repository.FormatPatchAsync(new(System.IO.Path.Combine(exports, "range"), SelectedRevisions: [revisions[0], revisions[2]]));
    Assert(range.Files.Count == 3 && (await File.ReadAllTextAsync(range.Files[1])).Contains("Feature 2", StringComparison.Ordinal), "Two endpoints include both selected commits and the commit between them");
    GitFormatPatchResult individual = await fixture.Repository.FormatPatchAsync(new(System.IO.Path.Combine(exports, "individual"), SelectedRevisions: [revisions[0], revisions[2], revisions[3]], StartNumber: 7));
    Assert(individual.Files.Select(file => System.IO.Path.GetFileName(file)[..4]).SequenceEqual(["0007", "0008", "0009"]), "Individual selection advances historical patch file numbering");
    string[] individualText = await Task.WhenAll(individual.Files.Select(file => File.ReadAllTextAsync(file)));
    Assert(individualText.Length == 3 && individualText.All(text => !text.Contains("Subject: [PATCH] Feature 2", StringComparison.Ordinal)), "More than two selections export only individual commits");
    GitFormatPatchOptions options = new(System.IO.Path.Combine(exports, "full series"), FromRevision: root, CoverLetter: true, SignOff: true, SubjectPrefix: "REVIEW", Numbered: true);
    GitFormatPatchResult series = await fixture.Repository.FormatPatchAsync(options);
    Assert(series.Files.Count == 5 && System.IO.Path.GetFileName(series.Files[0]).Contains("cover-letter", StringComparison.Ordinal), "Contiguous range includes a cover letter");
    string text = await File.ReadAllTextAsync(series.Files[1]);
    Assert(text.Contains("Subject: [REVIEW 1/4] Feature 1", StringComparison.Ordinal) && text.Contains("Signed-off-by: Linux Test <linux-test@example.invalid>", StringComparison.Ordinal) && text.Contains("GIT binary patch", StringComparison.Ordinal), "Business options retain numbered subject, signoff and binary data");
    Dictionary<string, byte[]> contents = [];
    foreach (string file in series.Files) { contents[file] = await File.ReadAllBytesAsync(file); }
    await ExpectAsync<IOException>(() => fixture.Repository.FormatPatchAsync(options));
    foreach ((string file, byte[] bytes) in contents) { Assert((await File.ReadAllBytesAsync(file)).SequenceEqual(bytes), "Existing mailbox file is not overwritten"); }
    Assert(!Directory.GetDirectories(options.OutputDirectory, ".gitextensions-patches-*").Any(), "Temporary patch directories are removed on a destination collision");
    await ExpectAsync<ArgumentException>(() => fixture.Repository.FormatPatchAsync(new(System.IO.Path.Combine(exports, "duplicate"), SelectedRevisions: [root, root])));
    await ExpectAsync<InvalidOperationException>(() => fixture.Repository.FormatPatchAsync(new(System.IO.Path.Combine(exports, "empty"), FromRevision: "HEAD")));
    Assert((await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "Patch exports leave the source worktree and index unchanged");

    string recipientPath = System.IO.Path.Combine(fixture.Root, "patch recipient");
    GitRepository recipient = await GitRepository.CloneAsync(fixture.Repository.Path, recipientPath, recursive: false);
    await RepositoryFixture.ConfigureAsync(recipient);
    await recipient.CreateBranchAsync("recipient", root);
    await recipient.ApplyMailboxPatchesAsync(range.Files);
    Assert((await recipient.GetLogAsync(3, all: false)).Select(commit => commit.Subject).SequenceEqual(["Feature 3", "Feature 2", "Feature 1"]), "Inclusive exported series reapplies as commits in chronological order");
    Assert((await recipient.GetFileBytesAsync("binary λ\n.bin")).SequenceEqual(new byte[] { 0, 255, 128, 10 }), "Mailbox patches restore exact binary content");
}

static async Task DiffPatchFilesAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    fixture.Write("seed.txt", "seed\nnew line\n");
    string patchFile = System.IO.Path.Combine(fixture.Root, "raw patch λ\n.patch");
    await File.WriteAllTextAsync(patchFile, await fixture.Repository.GetDiffAsync());
    await fixture.Repository.ResetAsync("HEAD", GitResetMode.Hard);
    Assert(await GitRepository.GetPatchFileKindAsync(patchFile) == GitPatchFileKind.Diff, "Raw diff detection retains the original dialog's prefix rule");
    await fixture.Repository.ApplyDiffPatchAsync(patchFile, new(CheckOnly: true));
    Assert((await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "Check-only application does not mutate files or index");
    await fixture.Repository.ApplyDiffPatchAsync(patchFile);
    Assert((await fixture.Repository.GetStatusAsync()).Changes.Single() is { IsStaged: false, WorkTreeStatus: 'M' }, "Default raw application affects the worktree only");
    await fixture.Repository.ApplyDiffPatchAsync(patchFile, new(Reverse: true));
    await fixture.Repository.ApplyDiffPatchAsync(patchFile, new(Target: GitPatchTarget.Index));
    Assert(await File.ReadAllTextAsync(fixture.File("seed.txt")) == "seed\n" && (await fixture.Repository.GetStatusAsync()).Changes.Single().IsStaged, "Cached application changes the index without editing the file");
    await fixture.Repository.ApplyDiffPatchAsync(patchFile, new(Target: GitPatchTarget.Index, Reverse: true));
    // Refresh stat data after worktree-only edits; git apply --index deliberately requires an up-to-date index.
    await fixture.Repository.RunAsync(["update-index", "--refresh"]);
    await fixture.Repository.ApplyDiffPatchAsync(patchFile, new(Target: GitPatchTarget.IndexAndWorkingTree));
    Assert(await File.ReadAllTextAsync(fixture.File("seed.txt")) == "seed\nnew line\n" && (await fixture.Repository.GetStatusAsync()).Changes.Single().IsStaged, "Index-and-worktree application updates both");
    await fixture.Repository.ApplyDiffPatchAsync(patchFile, new(Target: GitPatchTarget.IndexAndWorkingTree, Reverse: true));

    fixture.Write("nested/seed.txt", "seed\n");
    await fixture.Repository.ApplyDiffPatchAsync(patchFile, new(Directory: "nested"));
    Assert(await File.ReadAllTextAsync(fixture.File("nested/seed.txt")) == "seed\nnew line\n" && await File.ReadAllTextAsync(fixture.File("seed.txt")) == "seed\n", "Directory-prefix application stays inside the selected repository path");
    await ExpectAsync<ArgumentException>(() => fixture.Repository.ApplyDiffPatchAsync(patchFile, new(Directory: "../outside")));
    File.Delete(fixture.File("nested/seed.txt"));
    fixture.Write("seed.txt", "anchor\tvalue\nold\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Whitespace baseline");
    fixture.Write("seed.txt", "anchor\tvalue\nnew\n");
    string whitespacePatch = (await fixture.Repository.GetDiffAsync()).Replace(" anchor\tvalue", " anchor value", StringComparison.Ordinal);
    await File.WriteAllTextAsync(patchFile, whitespacePatch);
    await fixture.Repository.ResetAsync("HEAD", GitResetMode.Hard);
    await ExpectAsync<GitCommandException>(() => fixture.Repository.ApplyDiffPatchAsync(patchFile));
    await fixture.Repository.ApplyDiffPatchAsync(patchFile, new(IgnoreWhitespace: true));
    Assert(await File.ReadAllTextAsync(fixture.File("seed.txt")) == "anchor\tvalue\nnew\n", "Ignore-whitespace matches differing context while retaining original context bytes");
    await fixture.Repository.ResetAsync("HEAD", GitResetMode.Hard);
    fixture.Write("seed.txt", "anchor\tvalue\nnew trailing  \n");
    await File.WriteAllTextAsync(patchFile, await fixture.Repository.GetDiffAsync());
    await fixture.Repository.ResetAsync("HEAD", GitResetMode.Hard);
    await ExpectAsync<GitCommandException>(() => fixture.Repository.ApplyDiffPatchAsync(patchFile, new(Whitespace: GitPatchWhitespace.ErrorAll)));
    await fixture.Repository.ApplyDiffPatchAsync(patchFile, new(Whitespace: GitPatchWhitespace.Fix));
    Assert(await File.ReadAllTextAsync(fixture.File("seed.txt")) == "anchor\tvalue\nnew trailing\n", "Whitespace fix removes trailing added whitespace");
    string indexPatch = System.IO.Path.Combine(fixture.Root, "svn.patch");
    await File.WriteAllTextAsync(indexPatch, "Index: seed.txt\n");
    Assert(await GitRepository.GetPatchFileKindAsync(indexPatch) == GitPatchFileKind.Diff, "Original Index: diff prefix remains recognized");
}

static async Task MailboxApplyAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string initial = await fixture.Repository.ResolveRevisionAsync("HEAD");
    for (int index = 1; index <= 2; index++)
    {
        fixture.Write("seed.txt", "seed\n" + string.Concat(Enumerable.Range(1, index).Select(value => $"change {value}\n")));
        await fixture.Repository.StageAllAsync();
        await fixture.Repository.CommitAsync(new GitCommitOptions(Message: "Mailbox change " + index, Author: "Patch Author <patch-author@example.invalid>"));
    }
    GitFormatPatchResult patches = await fixture.Repository.FormatPatchAsync(new(System.IO.Path.Combine(fixture.Root, "mailbox files"), FromRevision: initial));
    Assert(await GitRepository.GetPatchFileKindAsync(patches.Files[0]) == GitPatchFileKind.Mailbox, "Mailbox detection recognizes Git format-patch output");
    string folder = System.IO.Path.Combine(fixture.Root, "directory mailbox λ\n");
    Directory.CreateDirectory(folder);
    File.Copy(patches.Files[1], System.IO.Path.Combine(folder, "0002 λ\n.patch"));
    File.Copy(patches.Files[0], System.IO.Path.Combine(folder, "0001 λ\n.patch"));
    GitRepository target = await GitRepository.CloneAsync(fixture.Repository.Path, System.IO.Path.Combine(fixture.Root, "am recipient"), recursive: false);
    await RepositoryFixture.ConfigureAsync(target);
    await target.CreateBranchAsync("recipient", initial);
    await target.ApplyPatchDirectoryAsync(folder, new(SignOff: true, CommitterDateIsAuthorDate: true));
    IReadOnlyList<GitCommit> commits = await target.GetLogAsync(2, all: false);
    Assert(commits.Select(commit => commit.Subject).SequenceEqual(["Mailbox change 2", "Mailbox change 1"]) && commits.All(commit => commit.AuthorName == "Patch Author" && commit.Body.Contains("Signed-off-by: Linux Test <linux-test@example.invalid>", StringComparison.Ordinal)), "Directory mailbox ordering preserves authors and applies signoff");
    GitResult dates = await target.RunAsync(["show", "--no-patch", "--format=%aI%x00%cI", "HEAD"]);
    string[] date = dates.StandardOutput.TrimEnd('\n').Split('\0');
    Assert(date.Length == 2 && date[0] == date[1], "Mailbox committer-date option adopts the author timestamp");
    Assert(!(await target.GetAmStateAsync()).IsActive && (await target.GetStatusAsync()).Changes.Count == 0, "Successful mailbox application clears state and leaves a clean index");
    await ExpectAsync<InvalidOperationException>(() => target.ManageAmAsync(GitOperationAction.Continue));
    await ExpectAsync<ArgumentOutOfRangeException>(() => target.ManageAmAsync((GitOperationAction)999));
    GitFormatPatchResult rootPatch = await fixture.Repository.FormatPatchAsync(new(System.IO.Path.Combine(fixture.Root, "root mail"), SelectedRevisions: [initial]));
    GitRepository unborn = await GitRepository.InitAsync(System.IO.Path.Combine(fixture.Root, "unborn am"));
    await RepositoryFixture.ConfigureAsync(unborn);
    await unborn.ApplyMailboxPatchesAsync(rootPatch.Files);
    Assert((await unborn.GetLogAsync()).Single().Subject == "Initial" && await File.ReadAllTextAsync(System.IO.Path.Combine(unborn.Path, "seed.txt")) == "seed\n", "Mailbox application imports a root commit into an unborn repository");
}

static async Task MailboxConflictAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string initial = await fixture.Repository.ResolveRevisionAsync("HEAD");
    fixture.Write("seed.txt", "source conflicting change\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Conflict patch");
    fixture.Write("later.txt", "later independent patch\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Later patch");
    GitFormatPatchResult patches = await fixture.Repository.FormatPatchAsync(new(System.IO.Path.Combine(fixture.Root, "conflict mailbox"), FromRevision: initial));
    foreach (GitOperationAction action in new[] { GitOperationAction.Continue, GitOperationAction.Abort, GitOperationAction.Skip })
    {
        GitRepository target = await GitRepository.CloneAsync(fixture.Repository.Path, System.IO.Path.Combine(fixture.Root, action.ToString()), recursive: false);
        await RepositoryFixture.ConfigureAsync(target);
        await target.CreateBranchAsync("recipient", initial);
        await File.WriteAllTextAsync(System.IO.Path.Combine(target.Path, "seed.txt"), "target conflicting change\n");
        await target.StageAllAsync();
        await target.CommitAsync("Target change");
        string original = await target.ResolveRevisionAsync("HEAD");
        await ExpectAsync<GitCommandException>(() => target.ApplyMailboxPatchesAsync(patches.Files));
        GitRepository reopened = await GitRepository.OpenAsync(target.Path);
        GitAmState state = await reopened.GetAmStateAsync();
        Assert(state.IsActive && state.CurrentPatch == 1 && state.PatchCount == 2 && state.Subject == "Conflict patch" && state.OriginalHead == original && state.ConflictedPaths.SequenceEqual(["seed.txt"]) && state.CurrentPatchDiff?.Contains("+source conflicting change", StringComparison.Ordinal) == true, "Mailbox conflict state survives reopening with current patch and conflicted paths");
        await ExpectAsync<InvalidOperationException>(() => reopened.ApplyMailboxPatchesAsync(patches.Files));
        if (action == GitOperationAction.Continue)
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(target.Path, "seed.txt"), "resolved source and target\n");
            await reopened.StageAsync(["seed.txt"]);
        }
        await reopened.ManageAmAsync(action);
        Assert(!(await reopened.GetAmStateAsync()).IsActive && (await reopened.GetStatusAsync()).Changes.Count == 0, "Mailbox continuation action clears active state and leaves index clean");
        string seed = await File.ReadAllTextAsync(System.IO.Path.Combine(target.Path, "seed.txt"));
        if (action == GitOperationAction.Abort)
        {
            Assert(await reopened.ResolveRevisionAsync("HEAD") == original && seed == "target conflicting change\n" && !File.Exists(System.IO.Path.Combine(target.Path, "later.txt")), "Abort restores the original branch, worktree and commit");
        }
        else if (action == GitOperationAction.Skip)
        {
            Assert(seed == "target conflicting change\n" && File.Exists(System.IO.Path.Combine(target.Path, "later.txt")) && (await reopened.GetLogAsync(1, all: false)).Single().Subject == "Later patch", "Skip omits only the conflicted patch and applies the following patch");
        }
        else
        {
            Assert(seed == "resolved source and target\n" && (await reopened.GetLogAsync(2, all: false)).Select(commit => commit.Subject).SequenceEqual(["Later patch", "Conflict patch"]), "Continue commits the staged resolution and finishes the mailbox series");
        }
    }
}

static async Task MaintenanceAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
    GitRepositoryStatistics loose = await fixture.Repository.GetStatisticsAsync();
    Assert(loose.LooseObjects >= 3 && loose.LooseSizeBytes > 0 && loose.PackedObjects == 0 && loose.PackFiles == 0, "Fresh repository statistics expose loose object counts and bytes");
    GitResult dangling = await new GitRunner().RunAsync(fixture.Repository.Path, ["hash-object", "-w", "--stdin"], standardInput: "recent unreferenced bytes\n");
    Assert(dangling.Success, "Temporary unreachable object");
    string objectHash = dangling.StandardOutput.TrimEnd('\n');
    GitFsckResult verified = await fixture.Repository.VerifyObjectsAsync(new(IncludeUnreachable: true, NoReflogs: true, Strict: true));
    Assert(verified.IsValid && verified.Issues.Any(issue => issue.Hash == objectHash && issue.ObjectType == "blob" && issue.Kind == GitFsckIssueKind.Unreachable), "Typed integrity report includes unreachable objects without deleting them");
    await fixture.Repository.GarbageCollectAsync();
    GitRepositoryStatistics packed = await fixture.Repository.GetStatisticsAsync();
    Assert(packed.PackFiles >= 1 && packed.PackedObjects >= 3 && packed.PackedSizeBytes > 0 && packed.GarbageObjects == 0, "Normal garbage collection packs reachable objects with byte-based statistics");
    GitResult retained = await new GitRunner().RunAsync(fixture.Repository.Path, ["cat-file", "blob", objectHash]);
    Assert(retained.Success && retained.StandardOutput == "recent unreferenced bytes\n" && await fixture.Repository.ResolveRevisionAsync("HEAD") == head && (await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "Normal collection keeps recent unreachable objects, branch identity and worktree contents");
    Assert((await fixture.Repository.VerifyObjectsAsync()).IsValid, "Packed object verification remains healthy");
    string barePath = System.IO.Path.Combine(fixture.Root, "maintenance bare.git");
    Assert((await new GitRunner().RunAsync(fixture.Root, ["clone", "--bare", "--", fixture.Repository.Path, barePath])).Success, "Bare maintenance fixture");
    GitRepository bare = await GitRepository.OpenAsync(barePath);
    await bare.GarbageCollectAsync();
    Assert((await bare.VerifyObjectsAsync()).IsValid && (await bare.GetStatisticsAsync()).PackedObjects >= 3, "Maintenance operates on bare repositories");

    using RepositoryFixture corrupt = await RepositoryFixture.CreateAsync();
    GitResult blob = await new GitRunner().RunAsync(corrupt.Repository.Path, ["rev-parse", "HEAD:seed.txt"]);
    string missing = blob.StandardOutput.TrimEnd('\n');
    File.Delete(corrupt.File(".git/objects/" + missing[..2] + "/" + missing[2..]));
    GitFsckResult failed = await corrupt.Repository.VerifyObjectsAsync();
    Assert(!failed.IsValid && failed.ExitCode != 0 && (failed.StandardOutput + failed.StandardError).Contains(missing, StringComparison.Ordinal), "Corrupt objects return an actionable nonzero report instead of losing diagnostics in an exception");
}

static async Task AskpassAsync()
{
    if (OperatingSystem.IsWindows()) { return; }
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string fallback = System.IO.Path.Combine(fixture.Root, "fallback askpass");
    string explicitHelper = System.IO.Path.Combine(fixture.Root, "user askpass");
    foreach ((string path, string mode) in new[] { (fallback, "--fake-askpass"), (explicitHelper, "--fake-user-askpass") })
    {
        await File.WriteAllTextAsync(path, $"#!/bin/sh\nexec \"$GITEXTENSIONS_FAKE_ASKPASS_PROCESS\" {mode} \"$@\"\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    string? originalFallback = GitRunner.AskpassFallbackPath;
    string? originalCulture = GitRunner.AskpassUiCulture;
    string[] keys = ["GIT_ASKPASS", "SSH_ASKPASS", "SSH_ASKPASS_REQUIRE", "DISPLAY", "WAYLAND_DISPLAY"];
    Dictionary<string, string?> previous = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
    try
    {
        foreach (string key in keys) { Environment.SetEnvironmentVariable(key, null); }
        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "fake-wayland-display");
        GitRunner.AskpassFallbackPath = fallback;
        GitRunner.AskpassUiCulture = "fr-FR";
        GitRunner runner = new();
        Dictionary<string, string?> environment = new() { ["GITEXTENSIONS_FAKE_ASKPASS_PROCESS"] = Environment.ProcessPath! };
        environment["GITEXTENSIONS_FAKE_EXPECT_CULTURE"] = "fr-FR";
        const string request = "protocol=https\nhost=authentication.example.invalid\n\n";
        string[] command = ["-c", "credential.helper=", "credential", "fill"];
        GitResult result = await runner.RunAsync(fixture.Repository.Path, command, standardInput: request, environment: environment);
        Assert(result.Success && result.StandardError.Length == 0, "Git obtains fake credentials via askpass without diagnostic pollution");
        Assert(result.StandardOutput == "protocol=https\nhost=authentication.example.invalid\nusername=fallback-user\npassword=fictitious-password λ '$() &\n", "Only credential protocol fields enter Git stdout, with Unicode and shell characters unchanged");
        Assert(Environment.GetEnvironmentVariable("GIT_ASKPASS") is null && Environment.GetEnvironmentVariable("SSH_ASKPASS") is null, "Askpass configuration is isolated to child processes");
        environment.Remove("GITEXTENSIONS_FAKE_EXPECT_CULTURE");

        string nativeDirectory = System.IO.Path.Combine(fixture.Root, "native helper λ");
        Directory.CreateDirectory(nativeDirectory);
        string nativeHelper = System.IO.Path.Combine(nativeDirectory, "gitextensions-askpass");
        string sourceHelper = System.IO.Path.GetFullPath("../../../../../../src/linux/GitExtensions.Desktop/gitextensions-askpass", AppContext.BaseDirectory);
        File.Copy(sourceHelper, nativeHelper);
        string sibling = System.IO.Path.Combine(nativeDirectory, "GitExtensions");
        await File.WriteAllTextAsync(sibling, "#!/bin/sh\n[ \"$1\" = --askpass ] || exit 1\nshift\nexec \"$GITEXTENSIONS_FAKE_ASKPASS_PROCESS\" --fake-askpass \"$@\"\n");
        File.SetUnixFileMode(nativeHelper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(sibling, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRunner.AskpassFallbackPath = nativeHelper;
        result = await runner.RunAsync(fixture.Repository.Path, command, standardInput: request, environment: environment);
        Assert(result.Success && result.StandardOutput.Contains("username=fallback-user\n", StringComparison.Ordinal) && result.StandardError.Length == 0, "Published helper dispatches to sibling apphost with the askpass mode, preserving spaced Unicode paths");
        string injection = System.IO.Path.Combine(fixture.Root, "prompt-was-interpolated");
        result = await runner.RunAsync(fixture.Repository.Path, command, standardInput: $"protocol=https\nhost=authentication.example.invalid/$(touch '{injection}')\n\n", environment: environment);
        Assert(result.Success && !File.Exists(injection), "Prompt shell characters are forwarded as data by the actual helper script");
        GitRunner.AskpassFallbackPath = fallback;

        environment["GIT_ASKPASS"] = explicitHelper;
        result = await runner.RunAsync(fixture.Repository.Path, command, standardInput: request, environment: environment);
        Assert(result.Success && result.StandardOutput.Contains("username=explicit-user\npassword=fictitious-explicit-password\n", StringComparison.Ordinal), "Explicit GIT_ASKPASS is preserved");
        environment.Remove("GIT_ASKPASS");
        environment["SSH_ASKPASS"] = explicitHelper;
        result = await runner.RunAsync(fixture.Repository.Path, command, standardInput: request, environment: environment);
        Assert(result.Success && result.StandardOutput.Contains("username=explicit-user\n", StringComparison.Ordinal), "Explicit SSH_ASKPASS is preserved without being superseded by a Git fallback");
        environment.Remove("SSH_ASKPASS");
        await fixture.Repository.SetConfigAsync("core.askpass", explicitHelper);
        result = await runner.RunAsync(fixture.Repository.Path, command, standardInput: request, environment: environment);
        Assert(result.Success && result.StandardOutput.Contains("username=explicit-user\n", StringComparison.Ordinal), "Repository core.askpass retains its precedence");
        await fixture.Repository.UnsetConfigAsync("core.askpass");
        result = await runner.RunAsync(fixture.Repository.Path, ["-c", "credential.helper=", "-c", "core.askpass=" + explicitHelper, "credential", "fill"], standardInput: request, environment: environment);
        Assert(result.Success && result.StandardOutput.Contains("username=explicit-user\n", StringComparison.Ordinal), "Command-local core.askpass is preserved");

        string credentials = System.IO.Path.Combine(fixture.Root, "credential-provider");
        await File.WriteAllTextAsync(credentials, "#!/bin/sh\nprintf 'username=credential-provider\\npassword=fictitious-provider-password\\n'\n");
        File.SetUnixFileMode(credentials, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        result = await runner.RunAsync(fixture.Repository.Path, ["-c", "credential.helper=", "-c", "credential.helper=" + credentials, "credential", "fill"], standardInput: request, environment: environment);
        Assert(result.Success && result.StandardOutput.Contains("username=credential-provider\n", StringComparison.Ordinal), "Credential providers continue to run before askpass");

        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
        result = await runner.RunAsync(fixture.Repository.Path, command, standardInput: request, environment: environment);
        Assert(!result.Success && result.StandardOutput.Length == 0 && result.StandardError.Contains("terminal prompts disabled", StringComparison.Ordinal), "Without a display authentication fails promptly without fallback UI");
        Environment.SetEnvironmentVariable("DISPLAY", ":fake-display");
        GitRunner.AskpassFallbackPath = System.IO.Path.Combine(fixture.Root, "missing-helper");
        result = await runner.RunAsync(fixture.Repository.Path, command, standardInput: request, environment: environment);
        Assert(!result.Success && result.StandardOutput.Length == 0, "Missing packaged helper leaves no unusable override");

        string pidFile = System.IO.Path.Combine(fixture.Root, "waiting-askpass-pid");
        await File.WriteAllTextAsync(fallback, "#!/bin/sh\nexec \"$GITEXTENSIONS_FAKE_ASKPASS_PROCESS\" --fake-slow-askpass \"$@\"\n");
        environment["GITEXTENSIONS_FAKE_ASKPASS_PID"] = pidFile;
        GitRunner.AskpassFallbackPath = fallback;
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(8));
        Task<GitResult> waiting = runner.RunAsync(fixture.Repository.Path, command, cancellation.Token, request, environment);
        try
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (!File.Exists(pidFile) && timeout.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(25); }
            Assert(File.Exists(pidFile), "Waiting fake askpass was started");
            int helperPid = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
            cancellation.Cancel();
            await ExpectAsync<OperationCanceledException>(() => waiting);
            await VerifyProcessTerminatedAsync(helperPid, "Cancelling Git also terminates its waiting askpass descendant");
        }
        finally
        {
            cancellation.Cancel();
            try { await waiting; } catch (OperationCanceledException) { }
        }
    }
    finally
    {
        GitRunner.AskpassFallbackPath = originalFallback;
        GitRunner.AskpassUiCulture = originalCulture;
        foreach ((string key, string? value) in previous) { Environment.SetEnvironmentVariable(key, value); }
    }
}

static async Task CancellationAsync()
{
    using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    string childPidPath = fixture.File("child.pid");
    string alias = $"alias.slow=!sleep 20 & child=$!; printf '%s' \"$child\" > '{childPidPath}'; wait";
    using CancellationTokenSource source = new();
    Stopwatch duration = Stopwatch.StartNew();
    Task<GitResult> operation = new GitRunner().RunAsync(fixture.Repository.Path, ["-c", alias, "slow"], source.Token);
    while (!File.Exists(childPidPath) && duration.Elapsed < TimeSpan.FromSeconds(3))
    {
        await Task.Delay(20);
    }

    Assert(File.Exists(childPidPath), "Slow descendant started");
    int childPid = int.Parse(await File.ReadAllTextAsync(childPidPath));
    source.Cancel();
    await ExpectAsync<OperationCanceledException>(() => operation);
    Assert(duration.Elapsed < TimeSpan.FromSeconds(5), "Cancellation bounded");
    await VerifyProcessTerminatedAsync(childPid, "Descendant terminated");
    Assert(await fixture.Repository.IsRepositoryAsync(), "Later commands work");
}

static async Task VerifyProcessTerminatedAsync(int pid, string message)
{
    bool Terminated() => !LinuxProcessState.IsRunning(pid);
    Stopwatch deadline = Stopwatch.StartNew();
    while (!Terminated() && deadline.Elapsed < TimeSpan.FromSeconds(2)) { await Task.Delay(20); }
    Assert(Terminated(), message);
}

static async Task<RepositoryFixture> ConflictingBranchesAsync()
{
    RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
    await fixture.Repository.CreateBranchAsync("feature");
    fixture.Write("seed.txt", "feature\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Feature conflict");
    await fixture.Repository.CheckoutAsync("main");
    fixture.Write("seed.txt", "main\n");
    await fixture.Repository.StageAllAsync();
    await fixture.Repository.CommitAsync("Main conflict");
    return fixture;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task<T> ExpectAsync<T>(Func<Task> operation) where T : Exception
{
    try
    {
        await operation();
    }
    catch (T exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class RepositoryFixture : IDisposable
{
    private RepositoryFixture(string root, GitRepository repository)
    {
        Root = root;
        Repository = repository;
    }

    public string Root { get; }
    public GitRepository Repository { get; }
    public string File(string name) => System.IO.Path.Combine(Repository.Path, name);
    public void Write(string name, string contents)
    {
        string path = File(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, contents);
    }

    public static async Task<RepositoryFixture> CreateAsync(bool seed = true)
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-linux-test-" + Guid.NewGuid().ToString("N"));
        GitRepository repository = await GitRepository.InitAsync(System.IO.Path.Combine(root, "repository"));
        await ConfigureAsync(repository);
        RepositoryFixture fixture = new(root, repository);
        if (seed)
        {
            fixture.Write("seed.txt", "seed\n");
            await repository.StageAllAsync();
            await repository.CommitAsync("Initial");
        }

        return fixture;
    }

    public static async Task ConfigureAsync(GitRepository repository)
    {
        await repository.SetConfigAsync("user.name", "Linux Test");
        await repository.SetConfigAsync("user.email", "linux-test@example.invalid");
        await repository.SetConfigAsync("commit.gpgsign", "false");
        await repository.SetConfigAsync("tag.gpgsign", "false");
        await repository.SetConfigAsync("core.hooksPath", "/dev/null");
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

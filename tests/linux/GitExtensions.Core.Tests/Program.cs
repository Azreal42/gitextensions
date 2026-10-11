using System.Diagnostics;
using GitExtensions.Linux;

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
    ("unborn status and unstage", UnbornAsync),
    ("literal paths with whitespace, quotes, metacharacters and newlines", LiteralPathsAsync),
    ("rename and mixed index/worktree status", RenameAsync),
    ("history, parents, multiline messages and SHA1 shared identities", HistoryAsync),
    ("history all branches, paging, authors, messages and literal path filters", FilteredHistoryAsync),
    ("branches, tags, detached HEAD and references", ReferencesAsync),
    ("stash include untracked, apply index, pop and drop", StashAsync),
    ("local remotes, push, fetch, pull and clone", RemotesAsync),
    ("NUL worktree paths and cleanup", WorktreesAsync),
    ("submodule paths, init/update/sync and checkout identities", SubmodulesAsync),
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
];

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
    GitCommandException failure = await ExpectAsync<GitCommandException>(() => fixture.Repository.MergeAsync("feature"));
    Assert(failure.Result.ExitCode == 1, "Merge reports failure");
    Assert((await fixture.Repository.GetStatusAsync()).Changes.Single().IsConflict, "Unmerged porcelain");
    fixture.Write("seed.txt", "resolved\n");
    await fixture.Repository.StageAsync(["seed.txt"]);
    await fixture.Repository.ManageOperationAsync("merge", GitOperationAction.Continue);
    Assert((await fixture.Repository.GetLogAsync())[0].Parents.Count == 2 && (await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "Merge continue");
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
    await fixture.Repository.CheckoutAsync("main");
    await fixture.Repository.MergeAsync("feature", noFastForward: true);
    Assert((await fixture.Repository.GetLogAsync())[0].Parents.Count == 2, "Non fast-forward merge");
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
    string procStatus = $"/proc/{childPid}/status";
    Assert(!File.Exists(procStatus) || File.ReadLines(procStatus).Any(line => line.StartsWith("State:", StringComparison.Ordinal) && line.Contains('Z')), "Descendant terminated");
    Assert(await fixture.Repository.IsRepositoryAsync(), "Later commands work");
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

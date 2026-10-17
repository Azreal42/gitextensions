using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FileMenuAdditionalOperationsTests
{
    [AvaloniaTest]
    public async Task SharedRememberedFileRetainsItsOwnCommitAndOldRenamePathWithoutMutatingTheSelection()
    {
        using Fixture f = await Fixture.CreateAsync();
        string first = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.Repository.MoveTrackedFileAsync("file.txt", "renamed.txt");
        await f.GitAsync("commit", "-m", "Rename");
        await File.WriteAllTextAsync(f.PathInRepository("second.txt"), "second file\n");
        await f.Repository.StageAllAsync(); await f.GitAsync("commit", "-m", "Second file");
        string second = await f.Repository.ResolveRevisionAsync("HEAD");
        string output = await ConfigureToolAsync(f);
        byte[] index = await File.ReadAllBytesAsync(f.PathInRepository(".git/index"));
        Window owner = new(); owner.Show();
        try
        {
            FileMenuAdditionalOperations browse = Service(owner), compare = Service(owner);
            await browse.RefreshAsync(f.Repository, default); await compare.RefreshAsync(f.Repository, default);
            FileMenuLeaf renamed = new(new("renamed.txt", 'R', "file.txt"), new(first), new(second));
            FileMenuSelection selected = new(f.Repository, [renamed], ["renamed.txt"]);
            await browse.ExecuteAsync(FileMenuCommand.RememberFirst, selected);
            Assert.That(compare.RememberedLeaf!.File.Path, Is.EqualTo("file.txt"));
            Assert.That(compare.RememberedLeaf.Second.Revision, Is.EqualTo(first));
            Assert.That(renamed.File.Path, Is.EqualTo("renamed.txt"));
            Assert.That(renamed.File.OriginalPath, Is.EqualTo("file.txt"));
            FileMenuLeaf other = new(new("second.txt", 'A'), new(first), new(second));
            await compare.ExecuteAsync(FileMenuCommand.DiffWithRemembered, new(f.Repository, [other], ["second.txt"]) { CustomDiffTool = "test-record" });
            using JsonDocument result = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            Assert.That(result.RootElement.GetProperty("left").GetString(), Is.EqualTo("base\n"));
            Assert.That(result.RootElement.GetProperty("right").GetString(), Is.EqualTo("second file\n"));
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/index")), Is.EqualTo(index));
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    public async Task TwoSelectedFilesUseTheFocusedFileAsSecondAndDisableToolsAppliesAcrossHosts()
    {
        using Fixture f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.PathInRepository("other.txt"), "other\n");
        string output = await ConfigureToolAsync(f); await f.Repository.SetConfigAsync("difftool.z-other.cmd", "true");
        Window owner = new(); owner.Show();
        try
        {
            FileMenuAdditionalOperations a = Service(owner), b = Service(owner);
            await a.RefreshAsync(f.Repository, default); await b.RefreshAsync(f.Repository, default);
            FileMenuLeaf focused = new(new("file.txt", 'M'), new("HEAD"), new(Kind: GitCompareRevisionKind.WorkingTree));
            FileMenuLeaf other = new(new("other.txt", '?'), new("HEAD"), new(Kind: GitCompareRevisionKind.WorkingTree));
            FileMenuSelection selected = new(f.Repository, [focused, other], ["file.txt", "other.txt"], FocusedLeaf: focused) { CustomDiffTool = "test-record" };
            await a.ExecuteAsync(FileMenuCommand.DiffTwoSelected, selected);
            using JsonDocument result = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            Assert.That(result.RootElement.GetProperty("left").GetString(), Is.EqualTo("other\n"));
            Assert.That(result.RootElement.GetProperty("right").GetString(), Is.EqualTo("base\n"));
            Assert.That(a.CustomDiffTools.Count, Is.EqualTo(2));
            await a.ExecuteAsync(FileMenuCommand.DisableCustomDiffTools, selected);
            Assert.That(a.CustomDiffTools, Is.Empty); Assert.That(b.CustomDiffTools, Is.Empty);
            Assert.That(Service(owner).CustomDiffTools, Is.Empty);
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    public async Task FlagsToggleOnlyTrackedSelectedPathsAndStaleDispatchHasNoEffect()
    {
        using Fixture f = await Fixture.CreateAsync();
        const string literal = ":[a]* λ\t\n.txt";
        await File.WriteAllTextAsync(f.PathInRepository(literal), "literal\n");
        await f.Repository.StageAllAsync(); await f.GitAsync("commit", "-m", "Literal path");
        Window owner = new(); owner.Show();
        bool current = true;
        try
        {
            FileMenuAdditionalOperations service = Service(owner, () => current);
            FileMenuLeaf leaf = new(new(literal, 'M'), new(Kind: GitCompareRevisionKind.Index), new(Kind: GitCompareRevisionKind.WorkingTree), Stage: FileMenuStage.WorkingTree);
            FileMenuSelection selection = new(f.Repository, [leaf], [literal]);
            await service.ExecuteAsync(FileMenuCommand.ToggleSkipWorktree, selection);
            IReadOnlyList<GitIndexFileFlags> flags = await f.Repository.GetIndexFlagsAsync();
            Assert.That(flags.Single(flag => flag.Path == literal).IsSkipWorktree, Is.True);
            Assert.That(flags.Single(flag => flag.Path == "file.txt").IsSkipWorktree, Is.False);
            await service.ExecuteAsync(FileMenuCommand.ToggleSkipWorktree, selection with { Leaves = [leaf with { IsSkipWorktree = true }] });
            await service.ExecuteAsync(FileMenuCommand.ToggleAssumeUnchanged, selection);
            Assert.That((await f.Repository.GetIndexFlagsAsync()).Single(flag => flag.Path == literal).IsAssumeUnchanged, Is.True);
            current = false;
            Assert.ThrowsAsync<InvalidOperationException>(async () => await service.ExecuteAsync(FileMenuCommand.ToggleAssumeUnchanged,
                selection with { Leaves = [leaf with { IsAssumeUnchanged = true }] }));
            Assert.That((await f.Repository.GetIndexFlagsAsync()).Single(flag => flag.Path == literal).IsAssumeUnchanged, Is.True);
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    public async Task RepositoryChangesClearRememberedFilesAndCachedToolsBeforeAFileTreeMenuCanUseThem()
    {
        using Fixture f = await Fixture.CreateAsync(); await ConfigureToolAsync(f);
        Window owner = new(); owner.Show();
        try
        {
            FileMenuAdditionalOperations first = Service(owner), second = Service(owner);
            await first.RefreshAsync(f.Repository, default);
            FileMenuLeaf leaf = new(new("file.txt", 'M'), new("HEAD"), new("HEAD"));
            await first.ExecuteAsync(FileMenuCommand.RememberSecond, new(f.Repository, [leaf], ["file.txt"]));
            Assert.That(first.RememberedLeaf, Is.SameAs(leaf)); Assert.That(first.CustomDiffTools, Is.Not.Empty);
            string otherPath = f.Output("other-repo"); Directory.CreateDirectory(otherPath);
            GitRepository other = new(otherPath);
            await f.GitAsync("-C", otherPath, "init");
            // A repository switch with File tree already active need not load the Diff pane.
            Assert.That(first.GetCustomDiffTools(other), Is.Empty);
            Assert.That(first.GetRemembered(other), Is.Null);
            await second.RefreshAsync(f.Repository, default);
            Assert.That(second.RememberedLeaf, Is.Null);
            await second.ExecuteAsync(FileMenuCommand.RememberSecond, new(f.Repository, [leaf], ["file.txt"]));
            Assert.That(first.GetRemembered(other), Is.Null, "An older window cannot expose another repository's remembered file.");
            Assert.That(second.GetRemembered(f.Repository), Is.SameAs(leaf), "Reading an unchanged older window does not clear a different window's new remembered file.");
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    public async Task SubmoduleStashAndBrowseTargetTheValidatedChildAndLeaveTheSuperprojectUncommitted()
    {
        using Fixture f = await Fixture.CreateAsync();
        string source = f.Output("module-source");
        await f.GitAsync("clone", "--", f.Repository.Path, source);
        await f.GitAsync("-c", "protocol.file.allow=always", "submodule", "add", "--", source, "nested/module");
        await f.GitAsync("commit", "-m", "Add module");
        string parentHead = await f.Repository.ResolveRevisionAsync("HEAD");
        byte[] parentIndex = await File.ReadAllBytesAsync(f.PathInRepository(".git/index"));
        string childRoot = f.PathInRepository("nested/module");
        GitRepository child = await GitRepository.OpenAsync(childRoot);
        await child.SetConfigAsync("user.name", "Child Tester"); await child.SetConfigAsync("user.email", "child@example.com");
        await child.SetConfigAsync("commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(childRoot, "file.txt"), "child dirty\n");
        await File.WriteAllTextAsync(Path.Combine(childRoot, "untracked.txt"), "included\n");
        await File.WriteAllTextAsync(f.File, "parent stays dirty\n");
        new StashPreferencesStore().Save(new(IncludeUntracked: true));
        Window owner = new(); owner.Show(); ProcessStartInfo? launch = null;
        try
        {
            FileMenuAdditionalOperations service = new(owner, (_, action, _) => action(default), _ => true,
                info => { launch = info; return Process.GetCurrentProcess(); }, default);
            FileMenuLeaf leaf = new(new("nested/module", 'M'), new("HEAD"), new(Kind: GitCompareRevisionKind.WorkingTree), IsSubmodule: true);
            FileMenuSelection selected = new(f.Repository, [leaf], [leaf.File.Path]);
            await service.ExecuteAsync(FileMenuCommand.OpenSubmodule, selected);
            Assert.That(launch!.WorkingDirectory, Is.EqualTo(childRoot)); Assert.That(launch.ArgumentList.Last(), Is.EqualTo(childRoot));
            await service.ExecuteAsync(FileMenuCommand.StashSubmodule, selected);
            Assert.That((await child.GetStashesAsync()).Count, Is.EqualTo(1));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(childRoot, "file.txt")), Is.EqualTo("base\n"));
            Assert.That(File.Exists(Path.Combine(childRoot, "untracked.txt")), Is.False);
            Assert.That(await File.ReadAllTextAsync(f.File), Is.EqualTo("parent stays dirty\n"));
            Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(parentHead));
            Assert.That(await f.Repository.GetStashesAsync(), Is.Empty);
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/index")), Is.EqualTo(parentIndex));
        }
        finally { owner.Close(); }
    }

    private static FileMenuAdditionalOperations Service(Window owner, Func<bool>? current = null)
        => new(owner, (_, action, _) => action(default), _ => current?.Invoke() ?? true, _ => throw new InvalidOperationException("Unexpected external launch."), default);
    private static async Task<string> ConfigureToolAsync(Fixture f)
    {
        string helper = f.Output("record-tool"), output = f.Output("tool.json");
        await File.WriteAllTextAsync(helper, "#!/usr/bin/python3\nimport json,sys\nwith open(" + JsonSerializer.Serialize(output)
            + ",'w') as f:json.dump({'left':open(sys.argv[1]).read(),'right':open(sys.argv[2]).read()},f)\n");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        await f.Repository.SetConfigAsync("difftool.test-record.cmd", "'" + helper.Replace("'", "'\\''", StringComparison.Ordinal) + "' \"$LOCAL\" \"$REMOTE\"");
        return output;
    }
}

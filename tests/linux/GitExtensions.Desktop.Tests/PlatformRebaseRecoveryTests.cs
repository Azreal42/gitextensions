using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, Category("CrossPlatform"), NonParallelizable]
public sealed class PlatformRebaseRecoveryTests
{
    [TestCase("file")] [TestCase("directory")]
    [TestCase("file-link")] [TestCase("directory-link")]
    [TestCase("dangling-file-link")] [TestCase("dangling-directory-link")]
    public async Task PreservingAnOrphanMovesTheExactEntryAndKeepsTheCheckoutAndLinkTarget(string kind)
    {
        using PlatformRepositoryTests.Fixture f = await PlatformRepositoryTests.Fixture.CreateAsync();
        string tracked = Path.Combine(f.Repository.Path, "tracked λ.txt");
        await File.WriteAllTextAsync(tracked, "checkout\n");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("before orphan recovery");
        string head = await f.Repository.ResolveRevisionAsync("HEAD");
        string journal = Path.Combine(f.Repository.Path, ".git", "gitextensions-rebase-native");
        string outside = Path.GetFullPath(Path.Combine(f.Repository.Path, "..", "outside ' λ & journal"));
        byte[] witness = [0, 255, 10, 13, 42];
        bool directory = kind.Contains("directory", StringComparison.Ordinal);
        bool link = kind.EndsWith("link", StringComparison.Ordinal);
        bool dangling = kind.StartsWith("dangling", StringComparison.Ordinal);
        if (!dangling)
        {
            if (directory) { Directory.CreateDirectory(outside); }
            await File.WriteAllBytesAsync(directory ? Path.Combine(outside, "state.json") : outside, witness);
        }
        if (link)
        {
            if (directory) { Directory.CreateSymbolicLink(journal, outside); }
            else { File.CreateSymbolicLink(journal, outside); }
        }
        else if (directory)
        { Directory.CreateDirectory(journal); await File.WriteAllBytesAsync(Path.Combine(journal, "state.json"), witness); }
        else { await File.WriteAllBytesAsync(journal, witness); }

        GitRebaseState before = await f.Repository.GetRebaseStateAsync();
        Assert.That(before.HasRecoverableJournal, Is.True);
        Assert.That(before.IsActive, Is.False);
        GitRebasePreservedJournal saved = (await f.Repository.PreserveOrphanedRebaseJournalsAsync()).Single();
        Assert.That(saved.OriginalPath, Is.EqualTo(journal));
        Assert.That(Path.GetDirectoryName(saved.PreservedPath), Is.EqualTo(Path.GetDirectoryName(journal)));
        Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(journal)!), Does.Not.Contain(journal));
        if (link)
        {
            FileSystemInfo entry = directory ? new DirectoryInfo(saved.PreservedPath) : new FileInfo(saved.PreservedPath);
            Assert.That(entry.LinkTarget, Is.EqualTo(outside));
            if (dangling) { Assert.That(File.Exists(outside) || Directory.Exists(outside), Is.False); }
        }
        else
        { Assert.That(await File.ReadAllBytesAsync(directory ? Path.Combine(saved.PreservedPath, "state.json") : saved.PreservedPath), Is.EqualTo(witness)); }
        if (!dangling)
        { Assert.That(await File.ReadAllBytesAsync(directory ? Path.Combine(outside, "state.json") : outside), Is.EqualTo(witness)); }
        Assert.That(await File.ReadAllTextAsync(tracked), Is.EqualTo("checkout\n"));
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That((await f.Repository.GetRebaseStateAsync()).HasRecoverableJournal, Is.False);
        Assert.That(await f.Repository.PreserveOrphanedRebaseJournalsAsync(), Is.Empty);
    }
}

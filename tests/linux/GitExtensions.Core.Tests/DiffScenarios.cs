using GitExtensions.Linux;

internal static class DiffScenarios
{
    public static async Task RevisionFilesAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(seed: false);
        const string literal = ":[image]* λ\n[1].png";
        const string adjacent = ":[image]* λ\n1.png";
        fixture.Write(literal, "original literal\n");
        fixture.Write(adjacent, "adjacent content\n");
        await fixture.Repository.StageAllAsync();
        await fixture.Repository.CommitAsync("Root files");
        GitRevisionChanges root = await fixture.Repository.GetRevisionChangesAsync("HEAD");
        Check(root.BaseRevision is null && root.Files.Count == 2 && root.Files.All(file => file.Status == 'A'), "Root additions");
        string diff = await fixture.Repository.GetRevisionFileDiffAsync(root, root.Files.Single(file => file.Path == literal));
        Check(diff.Contains("+original literal", StringComparison.Ordinal) && !diff.Contains("adjacent content", StringComparison.Ordinal), "Root pathspec is literal");

        const string renamed = "renamed λ\n[2].png";
        await fixture.Repository.RunAsync(["mv", "--", literal, renamed]);
        await fixture.Repository.RunAsync(["rm", "--", adjacent]);
        await fixture.Repository.CommitAsync("Rename and delete");
        GitRevisionChanges changes = await fixture.Repository.GetRevisionChangesAsync("HEAD");
        GitRevisionFile rename = changes.Files.Single(file => file.Status == 'R');
        Check(rename.Path == renamed && rename.OriginalPath == literal, "NUL rename records keep both names");
        Check(changes.Files.Single(file => file.Path == adjacent).IsDeleted, "Deletion status");
        diff = await fixture.Repository.GetRevisionFileDiffAsync(changes, rename);
        Check(diff.Contains("similarity index 100%", StringComparison.Ordinal) && diff.Contains("rename from", StringComparison.Ordinal), "Selected rename retains metadata");
        Check(System.Text.Encoding.UTF8.GetString(await fixture.Repository.GetFileBytesAsync(adjacent, changes.BaseRevision!)) == "adjacent content\n", "Deleted preview reads parent bytes");
    }

    public static async Task MergeParentsAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string main = (await fixture.Repository.GetStatusAsync()).Branch!;
        await fixture.Repository.RunAsync(["checkout", "-b", "feature"]);
        fixture.Write("feature.txt", "feature\n");
        await fixture.Repository.StageAllAsync();
        await fixture.Repository.CommitAsync("Feature");
        string featureHead = await fixture.Repository.ResolveRevisionAsync("HEAD");
        await fixture.Repository.RunAsync(["checkout", main]);
        fixture.Write("main.txt", "main\n");
        await fixture.Repository.StageAllAsync();
        await fixture.Repository.CommitAsync("Main");
        string mainHead = await fixture.Repository.ResolveRevisionAsync("HEAD");
        await fixture.Repository.RunAsync(["merge", "--no-ff", "feature", "-m", "Merge"]);
        GitCommit commit = (await fixture.Repository.GetLogAsync(1, "HEAD")).Single();
        Check(commit.Parents.Count == 2 && commit.Parents.SequenceEqual([mainHead, featureHead]), "Real merge parents");
        GitRevisionChanges first = await fixture.Repository.GetRevisionChangesAsync(commit.Hash);
        GitRevisionChanges second = await fixture.Repository.GetRevisionChangesAsync(commit.Hash, commit.Parents[1]);
        Check(first.BaseRevision == commit.Parents[0] && first.Files.Single().Path == "feature.txt", "Default first-parent files");
        Check(second.BaseRevision == commit.Parents[1] && second.Files.Single().Path == "main.txt", "Explicit second-parent files");
        Check((await fixture.Repository.GetRevisionFileDiffAsync(second, second.Files.Single())).Contains("+main", StringComparison.Ordinal), "Second-parent patch");
    }

    public static async Task IndexBytesAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        const string path = ":[literal]*\nλ.png";
        byte[] staged = [0, 1, 128, 255, 10, 13];
        await File.WriteAllBytesAsync(fixture.File(path), staged);
        await fixture.Repository.StageAsync([path]);
        await File.WriteAllBytesAsync(fixture.File(path), [99, 0, 12]);
        Check((await fixture.Repository.GetIndexFileBytesAsync(path)).SequenceEqual(staged), "Stage-0 bytes are independent from working directory");
        Check((await File.ReadAllBytesAsync(fixture.File(path))).SequenceEqual(new byte[] { 99, 0, 12 }), "Preview preserves working bytes");
        await fixture.Repository.CommitAsync("Image bytes");
        Check((await fixture.Repository.GetFileBytesAsync(path)).SequenceEqual(staged), "Historical bytes retain binary identity");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}

using System.Globalization;
using GitExtensions.Linux;

internal static class StatusSimilarityScenarios
{
    private const string Original = "literal dir/original λ\t\n:[1]*?.txt";
    private const string Renamed = "literal dir/renamed λ\t\n:[1]*?.txt";

    public static async Task RealRenamesAsync()
    {
        using RepositoryFixture fixture = await CreateAsync();
        GitRepository repository = fixture.Repository;
        File.Move(fixture.File(Original), fixture.File(Renamed));
        await repository.StageAllAsync();
        GitChange exact = (await repository.GetStatusAsync()).Changes.Single();
        Check(exact is { IndexStatus: 'R', WorkTreeStatus: '.', SimilarityPercent: 100 }
            && exact.Path == Renamed && exact.OriginalPath == Original, "A real staged R100 keeps both literal names and the exact score.");
        string[] lines = File.ReadAllLines(fixture.File(Renamed));
        lines[20] = "changed middle line";
        fixture.Write(Renamed, string.Join('\n', lines) + "\n");
        await repository.StageAsync([Renamed]);
        GitChange changed = (await repository.GetStatusAsync()).Changes.Single();
        Check(changed is { IndexStatus: 'R', WorkTreeStatus: '.', SimilarityPercent: >= 50 and < 100 }
            && changed.Path == Renamed && changed.OriginalPath == Original, "A real edited rename retains its lower similarity rather than assuming 100.");
        string raw = (await repository.RunAsync(["status", "--porcelain=v2", "-z"])).StandardOutput;
        string score = raw.Split('\0').Single(entry => entry.StartsWith("2 ", StringComparison.Ordinal)).Split(' ', 10)[8];
        Check(score[0] == 'R' && int.Parse(score.AsSpan(1), CultureInfo.InvariantCulture) == changed.SimilarityPercent,
            "The returned percentage equals Git's actual record2 score.");
        fixture.Write(Renamed, string.Join('\n', lines) + "\nworktree-only line\n");
        fixture.Write("ordinary.txt", "ordinary modified\n");
        fixture.Write("untracked λ\t\n.txt", "untracked\n");
        GitStatus mixed = await repository.GetStatusAsync();
        Check(mixed.Changes.Single(change => change.Path == Renamed) is { IndexStatus: 'R', WorkTreeStatus: 'M' } current
            && current.SimilarityPercent == changed.SimilarityPercent, "Mixed RM status retains the index rename's real percentage.");
        Check(mixed.Changes.Where(change => change.Path != Renamed).All(change => change.SimilarityPercent is null),
            "Ordinary modifications and untracked files have no rename/copy score.");
        (string path, string? previous, char index, char worktree, bool conflict, bool submodule) = changed;
        Check(path == Renamed && previous == Original && index == 'R' && worktree == '.' && !conflict && !submodule,
            "GitChange keeps its existing six-field constructor and deconstruction contract.");
        Check(exact.SimilarityPercent == 100, "A previously returned status snapshot remains unchanged after later reads.");
    }

    public static async Task MalformedAndCopyScoresAsync()
    {
        using RepositoryFixture fixture = await CreateAsync();
        GitRepository repository = fixture.Repository;
        File.Move(fixture.File(Original), fixture.File(Renamed)); await repository.StageAllAsync();
        string raw = (await repository.RunAsync(["status", "--porcelain=v2", "-z"])).StandardOutput;
        string record = raw.Split('\0').Single(entry => entry.StartsWith("2 ", StringComparison.Ordinal));
        string[] fields = record.Split(' ', 10);
        fields[1] = "C."; fields[8] = "C095";
        GitChange copy = GitOutputParser.ParseStatus(string.Join(' ', fields) + "\0" + Original + "\0").Changes.Single();
        Check(copy is { IndexStatus: 'C', WorkTreeStatus: '.', SimilarityPercent: 95 } && copy.OriginalPath == Original && copy.Path == Renamed,
            "Record2 copy scores use the same literal-name grammar and retain leading-zero numeric percentages.");
        foreach (string score in new[] { "R", "C", "R101", "C-1", "Rinvalid", "M100", "R١٠٠", "R99999999999999999999" })
        {
            fields[8] = score;
            ExpectFormat(() => GitOutputParser.ParseStatus(string.Join(' ', fields) + "\0" + Original + "\0"));
        }
        fields[1] = "R."; fields[8] = "R100";
        ExpectFormat(() => GitOutputParser.ParseStatus(string.Join(' ', fields) + "\0"));
        fields[1] = "R";
        ExpectFormat(() => GitOutputParser.ParseStatus(string.Join(' ', fields) + "\0" + Original + "\0"));
        GitChange ordinary = new("ordinary.txt", null, 'M', '.', false, false);
        Check(ordinary.SimilarityPercent is null && GitOutputParser.ParseStatus("? untracked\0! ignored\0").Changes.All(change => change.SimilarityPercent is null),
            "Existing ordinary constructors and simple status records retain null metadata.");
    }

    private static async Task<RepositoryFixture> CreateAsync()
    {
        RepositoryFixture fixture = await RepositoryFixture.CreateAsync(seed: false);
        try
        {
            fixture.Write(Original, string.Join('\n', Enumerable.Range(1, 40).Select(index => "original common line " + index.ToString("D2", CultureInfo.InvariantCulture))) + "\n");
            fixture.Write("ordinary.txt", "ordinary original\n");
            await fixture.Repository.StageAllAsync(); await fixture.Repository.CommitAsync("Rename sources"); return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }
    private static void ExpectFormat(Action action)
    {
        try { action(); throw new InvalidOperationException("Malformed porcelain record was accepted."); }
        catch (FormatException) { }
    }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}

using GitExtensions.Linux;

internal static class BisectLogScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("bisect reads legacy terminal log records", () => TerminalAsync("bad")),
        ("bisect reads quoted terminal log records", () => TerminalAsync("'bad'")),
        ("bisect reads legacy inconclusive log candidates", () => CandidatesAsync("bad")),
        ("bisect reads quoted inconclusive log candidates", () => CandidatesAsync("'bad'")),
        ("bisect ignores incomplete and malformed log records", MalformedAsync),
    ];

    private static async Task TerminalAsync(string term)
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        await fixture.Repository.StartBisectAsync();
        foreach (int length in new[] { 40, 64 })
        {
            string first = new('a', length), last = new('b', length);
            string log = $"# first {term} commit: [{first}] Earlier result\r\n"
                + $"# first {term} commit: [{last}] Final result\r\n";
            await File.WriteAllTextAsync(fixture.File(".git/BISECT_LOG"), log);
            GitBisectState state = await fixture.Repository.GetBisectStateAsync();
            Check(state.IsActive && state.FirstBadRevision == last && state.Log == log,
                "The latest complete terminal record and the original log must be preserved.");
        }
    }

    private static async Task CandidatesAsync(string term)
    {
        string first = new('a', 40), second = new('b', 64);
        string log = "# only skipped commits left to test\r\n"
            + $"# possible first {term} commit: [{first}] Subject\r\n"
            + $"# possible first {term} commit: [{second}] Other subject\r\n"
            + $"# possible first {term} commit: [{first}] Repeated result\r\n";
        GitBisectState state = new(true, "main", null, null, [], [], null, log, true, false);
        Check(state.IsInconclusive && state.PossibleFirstBadRevisions.SequenceEqual(new[] { first, second }),
            "Skipped candidates retain complete object IDs and order, with duplicates removed.");
        await Task.CompletedTask;
    }

    private static async Task MalformedAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        await fixture.Repository.StartBisectAsync();
        string[] invalid =
        [
            "[", "[abc]", "[" + new string('a', 40), "[" + new string('g', 40) + "]",
            "[" + new string('a', 41) + "]", "[]", "[" + new string('a', 40) + "]suffix",
        ];
        string log = string.Join('\n', invalid.SelectMany(value => new[]
        {
            "# first bad commit: " + value, "# first 'bad' commit: " + value,
            "# possible first bad commit: " + value, "# possible first 'bad' commit: " + value,
        })) + "\n# first good commit: [" + new string('a', 40) + "] Other term\n";
        await File.WriteAllTextAsync(fixture.File(".git/BISECT_LOG"), log);
        GitBisectState state = await fixture.Repository.GetBisectStateAsync();
        Check(state.FirstBadRevision is null && state.PossibleFirstBadRevisions.Count == 0 && state.Log == log,
            "A partial or malformed log must neither invent a result nor throw or alter its text.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) { throw new InvalidOperationException(message); }
    }
}

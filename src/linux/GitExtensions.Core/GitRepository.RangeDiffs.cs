using System.Globalization;
using System.Text.RegularExpressions;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Returns null for a selection that upstream would display as ordinary pair comparisons.</summary>
    public async Task<GitRangeDiffComparison?> GetRangeDiffComparisonAsync(IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        cancellationToken.ThrowIfCancellationRequested();
        if (revisions.Count is < 2 or > 4) { return null; }
        string[] resolved = new string[revisions.Count];
        for (int index = 0; index < resolved.Length; index++) { resolved[index] = await ResolveRangeRevisionAsync(revisions[index], cancellationToken).ConfigureAwait(false); }
        if (resolved.Distinct(StringComparer.Ordinal).Count() != resolved.Length) { return null; }
        string first = resolved[resolved.Length == 4 ? 2 : resolved.Length - 1], second = resolved[0];
        string? common = await GetDiffMergeBaseAsync(first, second, cancellationToken).ConfigureAwait(false);
        if (common is null) { return null; }
        string baseA = common, baseB = common;
        if (resolved.Length == 2)
        {
            if (common == first || common == second) { return null; }
        }
        else if (resolved.Length == 3)
        {
            baseA = baseB = common = resolved[1];
            if (!await IsDiffAncestorAsync(common, first, cancellationToken).ConfigureAwait(false)
                || !await IsDiffAncestorAsync(common, second, cancellationToken).ConfigureAwait(false)) { return null; }
        }
        else
        {
            baseA = resolved[3]; baseB = resolved[1];
            if (!await IsDiffAncestorAsync(baseA, first, cancellationToken).ConfigureAwait(false)
                || !await IsDiffAncestorAsync(baseB, second, cancellationToken).ConfigureAwait(false)) { return null; }
        }
        return await BuildRangeDiffComparisonAsync(baseA, first, baseB, second, common, resolved.Length, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitRangeDiffComparison> CreateRangeDiffComparisonAsync(string firstTip, string secondTip,
        string? firstBase = null, string? secondBase = null, CancellationToken cancellationToken = default)
    {
        if ((firstBase is null) != (secondBase is null)) { throw new ArgumentException("Both range bases must be supplied together."); }
        string first = await ResolveRangeRevisionAsync(firstTip, cancellationToken).ConfigureAwait(false);
        string second = await ResolveRangeRevisionAsync(secondTip, cancellationToken).ConfigureAwait(false);
        if (first == second) { throw new ArgumentException("Range-diff requires two distinct tips."); }
        string common = await GetDiffMergeBaseAsync(first, second, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException("Range-diff requires a common ancestor.");
        string baseA = firstBase is null ? common : await ResolveRangeRevisionAsync(firstBase, cancellationToken).ConfigureAwait(false);
        string baseB = secondBase is null ? common : await ResolveRangeRevisionAsync(secondBase, cancellationToken).ConfigureAwait(false);
        if (baseA == first || baseB == second || !await IsDiffAncestorAsync(baseA, first, cancellationToken).ConfigureAwait(false)
            || !await IsDiffAncestorAsync(baseB, second, cancellationToken).ConfigureAwait(false))
        { throw new ArgumentException("Each series requires a distinct ancestor base and a later tip."); }
        if (baseA == baseB) { common = baseA; }
        return await BuildRangeDiffComparisonAsync(baseA, first, baseB, second, common, firstBase is null ? 2 : baseA == baseB ? 3 : 4, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetRangeDiffAsync(GitRangeDiffComparison comparison, GitRangeDiffOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(comparison.First);
        ArgumentNullException.ThrowIfNull(comparison.Second);
        options ??= new();
        GitComparisonDiffOptions display = new(options.IgnoreWhitespace, options.ContextLines, options.ShowEntireFile, options.TreatAllFilesAsText);
        ValidateComparisonOptions(display);
        if (options.CreationFactor is < 0 or > 10000) { throw new ArgumentOutOfRangeException(nameof(options), "Creation factor must be between zero and 10000."); }
        string[] paths = options.Paths is null || options.Paths.Count == 0 ? [] : ValidatePaths(options.Paths);
        GitRangeDiffComparison resolved = await CreateRangeDiffComparisonAsync(comparison.First.TipRevision, comparison.Second.TipRevision,
            comparison.First.BaseRevision, comparison.Second.BaseRevision, cancellationToken).ConfigureAwait(false);
        Version version = await GetRangeDiffGitVersionAsync(cancellationToken).ConfigureAwait(false);
        if (version < new Version(2, 19)) { throw new NotSupportedException("Range-diff requires Git 2.19 or newer."); }
        if (paths.Length > 0 && version < new Version(2, 38)) { throw new NotSupportedException("Literal range-diff path filters require Git 2.38 or newer."); }
        List<string> arguments = ["--literal-pathspecs", "range-diff", "--find-renames", "--find-copies", "--no-ext-diff", "--no-textconv",
            options.UseGitColoring ? "--color=always" : "--no-color"];
        if (options.UseHistogram) { arguments.Add("--histogram"); }
        AddComparisonOptions(arguments, display);
        // FileViewer.GetExtraDiffArguments: zero context means summary only for range-diff.
        if (options.ContextLines == 0) { arguments.Add("--no-patch"); }
        if (options.CreationFactor is int factor) { arguments.Add("--creation-factor=" + factor.ToString(CultureInfo.InvariantCulture)); }
        // Keep both explicit bounds. Upstream calculated these for four selections but lost them before executing Git.
        arguments.AddRange([resolved.First.BaseRevision + ".." + resolved.First.TipRevision, resolved.Second.BaseRevision + ".." + resolved.Second.TipRevision]);
        if (paths.Length > 0) { arguments.AddRange(["--", .. paths]); }
        return (await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)).StandardOutput;
    }

    private Task<string> ResolveRangeRevisionAsync(string revision, CancellationToken cancellationToken)
    {
        if (ObjectId.TryParse(revision, out ObjectId id) && id.IsZeroOrArtificial)
        { throw new ArgumentException("Range-diff requires real commit tips and bases; zero and artificial revisions are unavailable.", nameof(revision)); }
        return ResolveRevisionAsync(revision, cancellationToken);
    }

    private async Task<GitRangeDiffComparison> BuildRangeDiffComparisonAsync(string baseA, string first, string baseB, string second,
        string common, int selectionCount, CancellationToken cancellationToken)
    {
        int countA = int.Parse((await ExecuteAsync(["rev-list", "--count", baseA + ".." + first, "--"], cancellationToken).ConfigureAwait(false)).StandardOutput.Trim(), CultureInfo.InvariantCulture);
        int countB = int.Parse((await ExecuteAsync(["rev-list", "--count", baseB + ".." + second, "--"], cancellationToken).ConfigureAwait(false)).StandardOutput.Trim(), CultureInfo.InvariantCulture);
        return new(new(baseA, first, countA), new(baseB, second, countB), common, selectionCount);
    }

    private async Task<string?> GetDiffMergeBaseAsync(string first, string second, CancellationToken cancellationToken)
    {
        GitResult result = await ExecuteAsync(["merge-base", first, second], cancellationToken, allowedExitCodes: [1]).ConfigureAwait(false);
        return result.Success ? ReadOutputLine(result.StandardOutput) : null;
    }

    private async Task<bool> IsDiffAncestorAsync(string ancestor, string descendant, CancellationToken cancellationToken)
        => (await ExecuteAsync(["merge-base", "--is-ancestor", ancestor, descendant], cancellationToken, allowedExitCodes: [1]).ConfigureAwait(false)).Success;

    private async Task<Version> GetRangeDiffGitVersionAsync(CancellationToken cancellationToken)
    {
        string output = (await ExecuteAsync(["--version"], cancellationToken).ConfigureAwait(false)).StandardOutput;
        Match match = Regex.Match(output, @"git version (\d+)\.(\d+)");
        if (!match.Success) { throw new FormatException("Cannot determine Git version for range-diff support."); }
        return new(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }
}

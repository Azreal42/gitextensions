using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

/// <summary>Windows left-panel priority buckets, preserving native order inside each bucket.</summary>
public static class GitReferencePriorities
{
    public const string DefaultBranchPatterns = "main[^/]*|master[^/]*|release/.*";
    public const string DefaultRemotePatterns = "origin|upstream";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>Validates every semicolon-separated expression before a preference is saved.</summary>
    public static void Validate(string patterns) => _ = CreateMatchers(patterns);

    public static IReadOnlyList<T> OrderByPriority<T>(IEnumerable<T> references, Func<T, string> keySelector, string patterns)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(keySelector);
        Regex[] matchers = CreateMatchers(patterns);
        try
        {
            return references.Select(value => (Value: value, Priority: Priority(keySelector(value))))
                .OrderBy(entry => entry.Priority).Select(entry => entry.Value).ToArray();
        }
        catch (RegexMatchTimeoutException error)
        {
            throw new ArgumentException("A reference priority expression exceeded its matching time limit.", nameof(patterns), error);
        }

        int Priority(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            for (int index = 0; index < matchers.Length; index++)
            {
                if (matchers[index].IsMatch(key)) { return index; }
            }
            return int.MaxValue;
        }
    }

    private static Regex[] CreateMatchers(string patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        try
        {
            return patterns.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(pattern => new Regex($"^({pattern})$", RegexOptions.ExplicitCapture, MatchTimeout)).ToArray();
        }
        catch (ArgumentException error)
        {
            throw new ArgumentException("Invalid reference priority expression: " + error.Message, nameof(patterns), error);
        }
    }
}

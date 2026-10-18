using System.Text.RegularExpressions;
using GitUI.AutoCompletion;

namespace GitExtensions.Linux;

internal sealed class GitCommitAutoCompleteRules
{
    // Windows supplies no timeout. Bound an extraction so cancellation/drain cannot wait forever
    // on a configured backtracking expression; preserve its Compiled-only extraction options.
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private readonly Dictionary<string, Regex> _regexes = new(StringComparer.Ordinal);

    internal GitCommitAutoCompleteRules(string? profileDirectory)
    {
        string? path = profileDirectory is null ? null : Path.Combine(profileDirectory, "AutoCompleteRegexes.txt");
        IEnumerable<string> lines = path is not null && File.Exists(path) ? File.ReadLines(path)
            : AutoCompleteRegexes.Default.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        int number = 0;
        foreach (string line in lines)
        {
            ++number;
            try
            {
                int separator = line.IndexOf('=');
                if (separator < 0) { throw new ArgumentException("An extension list and expression must be separated by '='."); }
                string[] extensions = line[..separator].Split(',').Select(extension => extension.Trim()).Distinct(StringComparer.Ordinal).ToArray();
                Regex regex = new(line[(separator + 1)..].Trim(), RegexOptions.Compiled, MatchTimeout);
                foreach (string extension in extensions) { _regexes.Add(extension, regex); }
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException($"Invalid commit completion rule in {path ?? "the Windows default rules"}, line {number}: {error.Message}", error);
            }
        }
    }

    internal Regex? ForPath(string path) => _regexes.GetValueOrDefault(Path.GetExtension(path));
}

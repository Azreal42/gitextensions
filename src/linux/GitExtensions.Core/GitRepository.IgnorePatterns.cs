using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>The original FormAddToGitIgnore preview: native -x patterns, including tracked and modified files, without editing rules.</summary>
    public async Task<IReadOnlyList<string>> GetIgnoredFilesAsync(IReadOnlyList<string> patterns, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        string[] selected = patterns.ToArray();
        foreach (string pattern in selected) { GitIgnorePatterns.ValidateLine(pattern); }
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
        selected = selected.Where(pattern => !string.IsNullOrWhiteSpace(pattern)).ToArray();
        if (selected.Length == 0) { return []; }
        List<string> arguments = ["ls-files", "-z", "-o", "-m", "-c", "-i"];
        foreach (string pattern in selected) { arguments.AddRange(["-x", pattern]); }
        arguments.Add("--");
        return (await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Append raw patterns in their supplied order and case. Duplicate rules and negations are meaningful and are retained.</summary>
    public Task<GitRulesFileSnapshot> AppendIgnorePatternsAsync(GitRulesFileSnapshot snapshot, IReadOnlyList<string> patterns, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(patterns);
        if (snapshot.Kind is not (GitRulesFileKind.Ignore or GitRulesFileKind.LocalExclude)) { throw new ArgumentException("Choose .gitignore or local exclusions.", nameof(snapshot)); }
        string[] selected = patterns.ToArray();
        foreach (string pattern in selected) { GitIgnorePatterns.ValidateLine(pattern); }
        StringBuilder text = new(snapshot.Text);
        foreach (string pattern in selected.Where(pattern => pattern.Length != 0))
        {
            if (text.Length > 0 && text[^1] is not ('\n' or '\r')) { text.Append(snapshot.NewLine); }
            text.Append(pattern).Append(snapshot.NewLine);
        }
        return SaveRulesFileAsync(snapshot, text.ToString(), cancellationToken);
    }
}

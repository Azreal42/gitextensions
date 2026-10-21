using System.Collections.ObjectModel;
using System.Globalization;

namespace GitExtensions.Linux;

/// <summary>The two yielded GitHub3 issue-helper settings, in the original hosting plugin group.</summary>
public sealed record GitHubIssueHelperSettings(bool Enabled = true, int MaxCount = 10)
{
    public const string EnabledKey = "IssueCommitMessageHelperEnabled";
    public const string MaxCountKey = "IssueCommitMessageHelperMaxCount";

    public static GitHubIssueHelperSettings FromPluginSettings(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        // BoolSetting rejects unknown text; NumberSetting<int> falls back on an absent or invalid Int32.
        bool enabled = !string.Equals(values.GetValueOrDefault(EnabledKey), "false", StringComparison.OrdinalIgnoreCase);
        int count = int.TryParse(values.GetValueOrDefault(MaxCountKey), NumberStyles.Integer, CultureInfo.CurrentCulture, out int parsed) ? parsed : 10;
        return new(enabled, count);
    }

    public IReadOnlyDictionary<string, string?> WithChanges(IReadOnlyDictionary<string, string?> values, GitHubIssueHelperSettings baseline)
    {
        ArgumentNullException.ThrowIfNull(values); ArgumentNullException.ThrowIfNull(baseline);
        Dictionary<string, string?> changed = new(values, StringComparer.Ordinal);
        // An untouched default (or an existing unknown value) is never materialized by merely opening the editor.
        if (Enabled != baseline.Enabled) { changed[EnabledKey] = Enabled ? "true" : "false"; }
        if (MaxCount != baseline.MaxCount) { changed[MaxCountKey] = MaxCount.ToString(CultureInfo.CurrentCulture); }
        return new ReadOnlyDictionary<string, string?>(changed);
    }
}

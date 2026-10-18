using System.Globalization;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

public sealed record GitBranchTrackingStatus(string Branch, string RemoteRef, string AheadCount, string BehindCount)
{
    public bool IsGone => AheadCount == "gone";
    public string Display => new GitCommands.Git.AheadBehindData(Branch, RemoteRef, AheadCount, BehindCount).ToDisplay();
}

public enum GitSubmoduleBrowseState { Unknown, SameCommit, Modified, FastForward, Rewind, NewerTime, OlderTime, NotInitialized, Conflicted }
public sealed record GitSubmoduleBrowseStatus(string Path, GitSubmoduleBrowseState State, bool IsDirty, string? Error = null,
    long? AddedCommits = null, long? RemovedCommits = null)
{
    public string CountSuffix => AddedCommits is null || RemovedCommits is null || AddedCommits == 0 && RemovedCommits == 0
        ? "" : $" (+{AddedCommits}-{RemovedCommits})";
}

public sealed partial class GitRepository
{
    public async Task<GitBranchTrackingStatus?> GetBranchTrackingStatusAsync(string branch, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(branch) || branch == "(detached)") { return null; }
        using IDisposable operation = BeginOperation();
        const string format = "--format=%(push:track,nobracket)%00%(upstream:track,nobracket)%00%(push)%00%(upstream)%00%(refname:short)";
        GitResult result = await ExecuteAsync(["for-each-ref", format, "--", "refs/heads/" + ValidateValue(branch)], cancellationToken).ConfigureAwait(false);
        foreach (string row in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = row.TrimEnd('\r').Split('\0');
            if (fields.Length != 5 || fields[4] != branch) { continue; }
            string remote = fields[2].Length > 0 && fields[0] != "gone" ? fields[2] : fields[3];
            if (remote.Length == 0) { return null; }
            static string Count(string value, string kind) => Regex.Match(value, "(?:^|, )" + kind + " ([0-9]+)(?:,|$)", RegexOptions.CultureInvariant).Groups[1].Value;
            string pushAhead = Count(fields[0], "ahead"), pushBehind = Count(fields[0], "behind");
            string upstreamAhead = Count(fields[1], "ahead"), upstreamBehind = Count(fields[1], "behind");
            bool unknown = Enumerable.Range(0, 2).Any(index => fields[index].Length > 0 && fields[index] != "gone"
                && Count(fields[index], "ahead").Length == 0 && Count(fields[index], "behind").Length == 0);
            // Preserve the original provider's push-first, gone and unknown rules.
            string ahead = pushAhead.Length > 0 ? pushAhead : pushBehind.Length > 0 ? "" : upstreamAhead.Length > 0 ? upstreamAhead
                : fields[0] == "gone" || fields[1] == "gone" ? "gone" : unknown ? "" : "0";
            string behind = pushBehind.Length > 0 ? pushBehind : pushAhead.Length == 0 ? upstreamBehind : "";
            return new(branch, remote, ahead, behind);
        }
        return null;
    }

    public async Task<IReadOnlyList<GitSubmoduleBrowseStatus>> GetSubmoduleBrowseStatusesAsync(CancellationToken cancellationToken = default)
    {
        using IDisposable operation = BeginOperation();
        IReadOnlyList<GitSubmoduleSnapshot> modules = await GetSubmoduleSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        List<GitSubmoduleBrowseStatus> results = [];
        foreach (GitSubmoduleSnapshot module in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!module.CanOpen || module.IsConflicted)
            {
                results.Add(new(module.Path, module.IsConflicted ? GitSubmoduleBrowseState.Conflicted : module.IsInitialized ? GitSubmoduleBrowseState.Unknown : GitSubmoduleBrowseState.NotInitialized,
                    false, module.ValidationError));
                continue;
            }
            try
            {
                GitRepository child = await OpenSubmoduleRepositoryAsync(module, cancellationToken).ConfigureAwait(false);
                GitStatus status = await child.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                bool dirty = status.Changes.Count > 0;
                long? addedCount = null, removedCount = null;
                GitSubmoduleBrowseState state = GitSubmoduleBrowseState.Modified;
                if (module.IndexHead == status.Head) { state = GitSubmoduleBrowseState.SameCommit; addedCount = removedCount = 0; }
                else if (module.IndexHead is not null && status.Head is not null)
                {
                    GitResult countResult = await child.ExecuteAsync(["rev-list", "--left-right", "--count", module.IndexHead + "..." + status.Head, "--"], cancellationToken).ConfigureAwait(false);
                    string[] counts = countResult.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (counts.Length == 2 && long.TryParse(counts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long removed)
                        && long.TryParse(counts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long added))
                    {
                        addedCount = added; removedCount = removed;
                        if (added > 0 && removed == 0) { state = GitSubmoduleBrowseState.FastForward; }
                        else if (added == 0 && removed > 0) { state = GitSubmoduleBrowseState.Rewind; }
                        else
                        {
                            GitResult dates = await child.ExecuteAsync(["show", "-s", "--format=%ct", module.IndexHead, status.Head, "--"], cancellationToken).ConfigureAwait(false);
                            string[] values = dates.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                            if (values.Length == 2 && long.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long previous)
                                && long.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long current))
                            { state = current > previous ? GitSubmoduleBrowseState.NewerTime : current < previous ? GitSubmoduleBrowseState.OlderTime : GitSubmoduleBrowseState.Modified; }
                        }
                    }
                }
                results.Add(new(module.Path, state, dirty, AddedCommits: addedCount, RemovedCommits: removedCount));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or GitCommandException)
            { results.Add(new(module.Path, GitSubmoduleBrowseState.Unknown, false, error.Message)); }
        }
        return results;
    }
}

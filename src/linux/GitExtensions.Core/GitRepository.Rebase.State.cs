using System.Globalization;
using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Read the sequencer's actual files. Structural todo commands are retained, not flattened into commits.</summary>
    public async Task<GitRebaseState> GetRebaseStateAsync(CancellationToken cancellationToken = default)
    {
        string merge = await GetGitPathAsync("rebase-merge", cancellationToken).ConfigureAwait(false);
        string apply = await GetGitPathAsync("rebase-apply", cancellationToken).ConfigureAwait(false);
        GitRebaseBackend backend = Directory.Exists(merge) ? GitRebaseBackend.Merge : Directory.Exists(apply) ? GitRebaseBackend.Apply : GitRebaseBackend.None;
        if (backend == GitRebaseBackend.None) { return new(backend, null, null, null, null, [], [], null, null); }
        string root = backend == GitRebaseBackend.Merge ? merge : apply;
        string journal = await GetGitPathAsync(GitRebaseEditor.NativeStateDirectoryName, cancellationToken).ConfigureAwait(false);
        NativeRebaseEditorState? session = Directory.Exists(journal) ? GitRebaseEditor.ReadNativeState(journal) : null;
        IReadOnlyList<string> skipped = session?.SkippedCommits ?? [];
        string legacy = await GetGitPathAsync("gitextensions-rebase", cancellationToken).ConfigureAwait(false);
        RebaseEditorState? legacySession = Directory.Exists(legacy) ? GitRebaseEditor.ReadState(legacy) : null;
        Encoding? encoding;
        string? encodingError = null;
        if (legacySession is not null) { encoding = GitRebaseEditor.GetStrictEncoding(legacySession.EncodingName); }
        else if (session is not null) { encoding = GitRebaseEditor.GetStrictEncoding(session.EncodingName); }
        else
        {
            try { encoding = await GetCommitMessageEncodingAsync(cancellationToken).ConfigureAwait(false); }
            catch (NotSupportedException error) { encoding = null; encodingError = error.Message; }
        }
        async Task<string?> Read(string name, Encoding? codec = null, bool trim = true)
        {
            string file = System.IO.Path.Combine(root, name);
            if (!File.Exists(file)) { return null; }
            string value = (codec ?? new UTF8Encoding(false, true)).GetString(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false));
            return trim ? value.TrimEnd('\r', '\n') : value;
        }
        string? current = await Read("stopped-sha").ConfigureAwait(false)
            ?? EmptyToNull((await ExecuteAsync(["rev-parse", "--verify", "REBASE_HEAD"], cancellationToken, allowedExitCodes: [1, 128]).ConfigureAwait(false)).StandardOutput.Trim());
        if (encoding is null)
        {
            // Administrative identities and conflicts are codec-independent.
            // Preserve visibility/Abort without guessing how to decode a native todo.
            return new(backend, await Read("orig-head").ConfigureAwait(false), await Read("onto").ConfigureAwait(false),
                await Read("head-name").ConfigureAwait(false), current, [],
                await GetConflictsAsync(cancellationToken).ConfigureAwait(false), null, null) { EncodingError = encodingError };
        }
        List<GitRebasePatch> patches = [];
        Dictionary<string, GitCommit?> metadata = new(StringComparer.Ordinal);
        async Task<GitCommit?> Commit(string? hash)
        {
            if (hash is null) { return null; }
            if (metadata.TryGetValue(hash, out GitCommit? cached)) { return cached; }
            try { cached = (await GetLogAsync(1, hash, cancellationToken, all: false).ConfigureAwait(false)).SingleOrDefault(); }
            catch (GitCommandException) { cached = null; }
            metadata.Add(hash, cached); return cached;
        }
        Dictionary<string, DateTimeOffset?> commitDates = new(StringComparer.Ordinal);
        async Task<DateTimeOffset?> CommitDate(GitCommit? commit)
        {
            if (commit is null) { return null; }
            if (commitDates.TryGetValue(commit.Hash, out DateTimeOffset? cached)) { return cached; }
            string text = (await ExecuteAsync(["show", "--no-patch", "--no-show-signature", "--no-decorate", "--encoding=UTF-8",
                "--format=%cI", commit.Hash, "--"], cancellationToken).ConfigureAwait(false)).StandardOutput.TrimEnd('\r', '\n');
            cached = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
            commitDates.Add(commit.Hash, cached); return cached;
        }
        string? todoPath = null, todoText = null;
        if (backend == GitRebaseBackend.Merge)
        {
            todoPath = System.IO.Path.Combine(merge, "git-rebase-todo");
            todoText = await Read("git-rebase-todo", encoding, trim: false).ConfigureAwait(false) ?? "";
            string done = await Read("done", encoding, trim: false).ConfigureAwait(false) ?? "";
            foreach ((string text, bool applied) in new[] { (done, true), (todoText, false) })
            {
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) { continue; }
                    string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    string action = parts[0]; string? hash = RebaseTodoCommitHash(parts);
                    GitCommit? commit = await Commit(hash).ConfigureAwait(false);
                    string? canonical = commit?.Hash ?? hash;
                    bool isCurrent = applied && canonical is not null && current is not null
                        && (canonical.StartsWith(current, StringComparison.Ordinal) || current.StartsWith(canonical, StringComparison.Ordinal));
                    bool isSkipped = canonical is not null && skipped.Contains(canonical, StringComparer.Ordinal)
                        || applied && action is "drop" or "d";
                    GitRebaseStepStatus status = isSkipped ? GitRebaseStepStatus.Skipped : isCurrent ? GitRebaseStepStatus.Applying
                        : applied ? GitRebaseStepStatus.Applied : GitRebaseStepStatus.Pending;
                    patches.Add(new(action, raw, canonical, commit, status) { CommitDate = await CommitDate(commit).ConfigureAwait(false) });
                }
            }
        }
        else
        {
            int next = int.TryParse(await Read("next").ConfigureAwait(false), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 1;
            int last = int.TryParse(await Read("last").ConfigureAwait(false), NumberStyles.Integer, CultureInfo.InvariantCulture, out int l) ? l : 0;
            for (int index = 1; index <= last; index++)
            {
                string file = System.IO.Path.Combine(apply, index.ToString("D4", CultureInfo.InvariantCulture));
                if (!File.Exists(file)) { continue; }
                byte[] bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
                int end = Array.IndexOf(bytes, (byte)'\n');
                string first = Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end);
                string[] fields = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string? hash = fields.Length > 1 && fields[0] == "From" && IsRebaseHash(fields[1]) ? fields[1] : null;
                GitCommit? commit = await Commit(hash).ConfigureAwait(false); hash = commit?.Hash ?? hash;
                GitRebaseStepStatus status = hash is not null && skipped.Contains(hash, StringComparer.Ordinal) ? GitRebaseStepStatus.Skipped
                    : index < next ? GitRebaseStepStatus.Applied : index == next ? GitRebaseStepStatus.Applying : GitRebaseStepStatus.Pending;
                patches.Add(new("pick", first, hash, commit, status, file) { CommitDate = await CommitDate(commit).ConfigureAwait(false) });
                if (index == next && current is null) { current = hash; }
            }
        }
        return new(backend, await Read("orig-head").ConfigureAwait(false), await Read("onto").ConfigureAwait(false),
            await Read("head-name").ConfigureAwait(false), current, patches,
            await GetConflictsAsync(cancellationToken).ConfigureAwait(false), todoPath, todoText)
            { Options = legacySession is null ? session?.Options : null, UsesLegacyPlan = legacySession is not null };
    }

    private static string? RebaseTodoCommitHash(string[] parts)
    {
        string action = parts[0];
        if (action is "pick" or "p" or "reword" or "r" or "edit" or "e" or "squash" or "s" or "drop" or "d")
        { return parts.Length > 1 && IsRebaseHash(parts[1]) ? parts[1] : null; }
        if (action is "fixup" or "f" or "merge" or "m")
        {
            int index = parts.Length > 1 && parts[1] is "-C" or "-c" ? 2 : 1;
            return parts.Length > index && IsRebaseHash(parts[index]) ? parts[index] : null;
        }
        return null;
    }
    private static bool IsRebaseHash(string text) => text.Length is >= 4 and <= 64 && text.All(Uri.IsHexDigit);
}

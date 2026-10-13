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
        GitRepositoryInfo info = await GetInfoAsync(cancellationToken).ConfigureAwait(false);
        RebaseJournalInspection journals = InspectRebaseJournals(info.GitDirectory);
        if (backend == GitRebaseBackend.None)
        { return new(backend, null, null, null, null, [], [], null, null) { JournalError = journals.Error, JournalPaths = journals.Paths }; }
        string root = backend == GitRebaseBackend.Merge ? merge : apply;
        NativeRebaseEditorState? session = journals.Error is null ? journals.Native : null;
        IReadOnlyList<string> skipped = session?.SkippedCommits ?? [];
        RebaseEditorState? legacySession = journals.Error is null ? journals.Legacy : null;
        Encoding? encoding;
        string? encodingError = null;
        if (journals.Error is not null) { encoding = null; }
        else if (legacySession is not null) { encoding = GitRebaseEditor.GetStrictEncoding(legacySession.EncodingName); }
        else if (session is not null) { encoding = GitRebaseEditor.GetStrictEncoding(session.EncodingName); }
        else
        {
            try { encoding = await GetCommitMessageEncodingAsync(cancellationToken).ConfigureAwait(false); }
            catch (NotSupportedException error) { encoding = null; encodingError = error.Message; }
        }
        Encoding? todoEncoding = encoding;
        if (backend == GitRebaseBackend.Merge && encoding is not null && legacySession is null)
        {
            // Saved UTF-8 journals are compatible with the earlier generator. External
            // UTF-8 sessions retain the existing current-config convention, with strict reads.
            // A non-UTF8 old/native or external script may contain mixed/recoded subjects;
            // neither valid-UTF8 guessing nor converting its existing bytes can establish intent.
            string? todoName = session is not null ? GitRebaseEditor.NativeTodoEncodingName(session)
                : encoding.CodePage == Encoding.UTF8.CodePage ? "utf-8" : null;
            if (todoName is null)
            {
                encodingError = "This native rebase has no reliable declared todo codec. Its instruction subjects may be mixed or altered. Abort remains available; no existing bytes have been converted.";
                encoding = todoEncoding = null;
            }
            else { todoEncoding = GitRebaseEditor.GetStrictEncoding(todoName); }
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
        async Task<GitRebaseState> Minimal(string? instructionError = null)
            => new(backend, await Read("orig-head").ConfigureAwait(false), await Read("onto").ConfigureAwait(false),
                await Read("head-name").ConfigureAwait(false), current, [],
                await GetConflictsAsync(cancellationToken).ConfigureAwait(false), null, null)
                {
                    Options = legacySession is null ? session?.Options : null, EncodingName = encoding?.WebName,
                    EncodingError = encodingError, InstructionError = instructionError, JournalError = journals.Error,
                    JournalPaths = journals.Paths, UsesLegacyPlan = legacySession is not null,
                    TodoEncodingName = backend == GitRebaseBackend.Merge && encoding is not null ? todoEncoding?.WebName : null
                };
        if (encoding is null)
        {
            // Administrative identities and conflicts are codec-independent.
            // Preserve visibility/Abort without guessing how to decode a native todo.
            return await Minimal().ConfigureAwait(false);
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
            string instructionPath = todoPath;
            string done;
            try
            {
                todoText = await ReadRebaseInstructionsAsync(instructionPath, todoEncoding!, cancellationToken).ConfigureAwait(false) ?? "";
                instructionPath = System.IO.Path.Combine(merge, "done");
                done = await ReadRebaseInstructionsAsync(instructionPath, todoEncoding!, cancellationToken).ConfigureAwait(false) ?? "";
            }
            catch (Exception error) when (error is DecoderFallbackException or IOException or UnauthorizedAccessException)
            {
                // Do not expose a partial plan or try the commit codec on Git's instruction bytes.
                // The real administrative identities/index still permit an independent Abort.
                cancellationToken.ThrowIfCancellationRequested();
                return await Minimal("The rebase instruction text cannot be read: " + instructionPath + ". " + error.Message).ConfigureAwait(false);
            }
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
            { Options = legacySession is null ? session?.Options : null, EncodingName = encoding.WebName,
                UsesLegacyPlan = legacySession is not null, JournalPaths = journals.Paths,
                TodoEncodingName = backend == GitRebaseBackend.Merge ? todoEncoding?.WebName : null };
    }

    private static async Task<string?> ReadRebaseInstructionsAsync(string path, Encoding encoding, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Git can legitimately have no done/todo file yet. File.Exists would also hide
        // access errors or a directory replacing the file, which are not an empty plan.
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            { throw new IOException("A directory replaced the rebase instruction file."); }
        }
        catch (FileNotFoundException)
        {
            if (new FileInfo(path).LinkTarget is not null)
            { throw new IOException("The existing rebase instruction link has no readable target."); }
            token.ThrowIfCancellationRequested(); return null;
        }
        catch (DirectoryNotFoundException) { token.ThrowIfCancellationRequested(); return null; }
        return encoding.GetString(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
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

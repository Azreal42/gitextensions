using System.Text;
using System.Text.RegularExpressions;
using GitUI.AutoCompletion;

namespace GitExtensions.Linux;

/// <summary>
/// Completion for one immutable successful status snapshot, captured at the owning Commit refresh.
/// Unlike Windows' per-provider status query/retry, admission never re-queries status while typing.
/// The caller owns this raw task, including cancellation/drain and visible rule/extraction errors.
/// </summary>
public sealed class GitCommitAutoCompleteProvider : IAutoCompleteProvider
{
    private readonly GitRepository _repository;
    private readonly bool _useHistogram;
    private readonly Lazy<GitCommitAutoCompleteRules> _rules;
    public IReadOnlyList<GitChange> Changes { get; }

    public GitCommitAutoCompleteProvider(GitRepository repository, IReadOnlyList<GitChange> changes,
        string? profileDirectory = null, bool useHistogram = false)
    {
        ArgumentNullException.ThrowIfNull(repository); ArgumentNullException.ThrowIfNull(changes);
        _repository = new(repository.Path, repository.ExecutionEnvironment.CreateRunner());
        Changes = Array.AsReadOnly(changes.ToArray()); _useHistogram = useHistogram;
        string? directory = profileDirectory is null ? null : Path.GetFullPath(profileDirectory);
        _rules = new(() => new(directory));
        foreach (GitChange change in Changes)
        {
            ArgumentNullException.ThrowIfNull(change); _ = WorkingPath(change.Path);
            if (OldPath(change) is { } old) { _ = WorkingPath(old); }
        }
    }

    public async Task<IEnumerable<AutoCompleteWord>> GetAutoCompleteWordsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Regex compilation/extraction and profile file IO stay off the editor thread. The returned
        // task owns the entire worker; no waiter-only cancellation hides unfinished native work.
        return await Task.Run(async () =>
        {
            GitCommitAutoCompleteRules rules = _rules.Value;
            HashSet<string> words = new(StringComparer.Ordinal);
            foreach (GitChange change in Changes.Where(change => !change.IsIgnored))
            {
                cancellationToken.ThrowIfCancellationRequested();
                words.Add(Path.GetFileName(change.Path));
                if (change.IsSubmodule) { continue; }
                if (rules.ForPath(change.Path) is Regex regex)
                {
                    if (change.IsUntracked)
                    { Extract(regex, await ReadWorkingTextAsync(change.Path, cancellationToken).ConfigureAwait(false)); }
                    else
                    {
                        // A porcelain MM row represents two Windows status items and two real patches.
                        if (change.IsStaged) { Extract(regex, await ReadChangedTextAsync(change, true, cancellationToken).ConfigureAwait(false)); }
                        if (change.IsModified) { Extract(regex, await ReadChangedTextAsync(change, false, cancellationToken).ConfigureAwait(false)); }
                    }
                }
                words.Add(Path.GetFileNameWithoutExtension(change.Path));
                if (OldPath(change) is { } old && !string.IsNullOrWhiteSpace(old))
                { words.Add(Path.GetFileNameWithoutExtension(old)); words.Add(Path.GetFileName(old)); }
            }
            foreach (AutoCompleteWord word in await new CommitMessageMetadataProvider().GetAutoCompleteWordsAsync(cancellationToken).ConfigureAwait(false))
            { words.Add(word.Word); }
            cancellationToken.ThrowIfCancellationRequested();
            return Array.AsReadOnly(words.Select(word => new AutoCompleteWord(word)).ToArray());

            void Extract(Regex regex, string text)
            {
                try
                {
                    foreach (Match match in regex.Matches(text))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        foreach (Group group in match.Groups.Cast<Group>().Skip(1))
                        {
                            foreach (Capture capture in group.Captures)
                            { cancellationToken.ThrowIfCancellationRequested(); words.Add(capture.Value); }
                        }
                    }
                }
                catch (RegexMatchTimeoutException) when (cancellationToken.IsCancellationRequested)
                { cancellationToken.ThrowIfCancellationRequested(); throw; }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadChangedTextAsync(GitChange change, bool staged, CancellationToken token)
    {
        string? old = staged ? OldPath(change) : null;
        try
        {
            string? patch = await _repository.ReadCommitCompletionPatchAsync(change.Path, old, staged, _useHistogram, token).ConfigureAwait(false);
            if (patch is not null) { return patch; }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Windows silently tries HEAD after a file/patch read failure. */ }
        if (!(staged && change.IndexStatus == 'A'))
        {
            StringBuilder contents = new();
            await AppendHead(change.Path).ConfigureAwait(false);
            if (old is not null) { await AppendHead(old).ConfigureAwait(false); }
            if (contents.Length > 0) { return contents.ToString(); }
            async Task AppendHead(string path)
            {
                try
                {
                    byte[] bytes = await _repository.GetFileBytesAsync(path, cancellationToken: token).ConfigureAwait(false);
                    string encodingName = await _repository.GetFileEncodingNameAsync(token).ConfigureAwait(false);
                    GitDecodedFile decoded = GitFileEncodingCodec.DecodeFile(bytes, encodingName);
                    if (!decoded.IsBinary) { contents.Append(decoded.Text); }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Keep the other HEAD name, then try the working file as on Windows. */ }
            }
        }
        return await ReadWorkingTextAsync(change.Path, token).ConfigureAwait(false);
    }

    private async Task<string> ReadWorkingTextAsync(string path, CancellationToken token)
    {
        try
        {
            using StreamReader reader = File.OpenText(WorkingPath(path));
            return await reader.ReadToEndAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return ""; } // Windows silently skips unreadable/disappeared working files.
    }

    private static string? OldPath(GitChange change) => change.IndexStatus is 'R' or 'C' ? change.OriginalPath : null;
    private string WorkingPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.Contains('\0')) { throw new ArgumentException("Completion paths cannot contain NUL.", nameof(path)); }
        string full = Path.GetFullPath(path, _repository.Path), relative = Path.GetRelativePath(_repository.Path, full);
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        { throw new ArgumentException("Completion paths must remain inside the captured repository.", nameof(path)); }
        return full;
    }
}

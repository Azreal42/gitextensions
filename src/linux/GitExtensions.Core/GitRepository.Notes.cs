namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public async Task<IReadOnlyList<GitNote>> GetNotesAsync(string? notesRef = null, CancellationToken cancellationToken = default)
    {
        string reference = await ResolveNotesRefAsync(notesRef, cancellationToken).ConfigureAwait(false);
        GitResult result = await ExecuteAsync(["notes", "--ref=" + reference, "list"], cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            string[] fields = line.Split(' ', 2);
            return new GitNote(fields[1], fields[0], reference);
        }).ToArray();
    }

    public async Task<string?> GetNoteAsync(string revision = "HEAD", string? notesRef = null, CancellationToken cancellationToken = default)
    {
        string commit = await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        string reference = await ResolveNotesRefAsync(notesRef, cancellationToken).ConfigureAwait(false);
        GitResult note = await ExecuteAsync(["notes", "--ref=" + reference, "list", commit], cancellationToken, allowedExitCodes: [1]).ConfigureAwait(false);
        if (!note.Success)
        {
            return null;
        }

        return (await ExecuteAsync(["cat-file", "blob", note.StandardOutput.TrimEnd('\r', '\n')], cancellationToken).ConfigureAwait(false)).StandardOutput;
    }

    public Task<GitResult> AddNoteAsync(string revision, string message, bool replace = false, string? notesRef = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Contains('\0'))
        {
            throw new ArgumentException("Note text cannot contain NUL.", nameof(message));
        }

        return WithMutationAsync(async token =>
        {
            string commit = await ResolveRevisionAsync(revision, token).ConfigureAwait(false);
            string reference = await ResolveNotesRefAsync(notesRef, token).ConfigureAwait(false);
            // Reuse a blob to preserve the exact text on the minimum supported Git versions.
            // The newer notes --no-stripspace flag is unavailable on Git 2.38.
            string blob = (await ExecuteAsync(["hash-object", "-w", "--stdin"], token, message).ConfigureAwait(false)).StandardOutput.TrimEnd('\r', '\n');
            List<string> arguments = ["notes", "--ref=" + reference, "add", "-C", blob];
            if (replace)
            {
                arguments.Add("--force");
            }

            arguments.Add(commit);
            return await ExecuteAsync(arguments, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<GitResult> RemoveNoteAsync(string revision, string? notesRef = null, CancellationToken cancellationToken = default)
        => WithMutationAsync(async token =>
        {
            string commit = await ResolveRevisionAsync(revision, token).ConfigureAwait(false);
            string reference = await ResolveNotesRefAsync(notesRef, token).ConfigureAwait(false);
            return await ExecuteAsync(["notes", "--ref=" + reference, "remove", "--ignore-missing", commit], token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<string> ResolveNotesRefAsync(string? notesRef, CancellationToken cancellationToken)
    {
        string reference = notesRef is null
            ? (await ExecuteAsync(["notes", "get-ref"], cancellationToken).ConfigureAwait(false)).StandardOutput.TrimEnd('\r', '\n')
            : ValidateValue(notesRef);
        if (!reference.StartsWith("refs/", StringComparison.Ordinal))
        {
            reference = "refs/notes/" + reference;
        }

        if (!reference.StartsWith("refs/notes/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Notes must use a namespace under refs/notes/.", nameof(notesRef));
        }

        GitResult checkedReference = await ExecuteAsync(["check-ref-format", reference], cancellationToken, allowedExitCodes: [1]).ConfigureAwait(false);
        if (!checkedReference.Success)
        {
            throw new ArgumentException("Invalid Git notes namespace.", nameof(notesRef));
        }

        return reference;
    }
}

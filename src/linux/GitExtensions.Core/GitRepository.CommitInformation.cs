using System.Globalization;
using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Reads complete metadata without changing the graph reader, index or reference sorting preferences.</summary>
    public async Task<GitCommitDetails> GetCommitDetailsAsync(string completeCommitHash, bool includeNotes = true,
        CancellationToken cancellationToken = default)
    {
        string commit = ValidateObjectId(completeCommitHash);
        (string type, byte[] data) = await ReadCommitInformationObjectAsync(commit, cancellationToken).ConfigureAwait(false);
        if (type != "commit") { throw new ArgumentException("Select a commit object.", nameof(completeCommitHash)); }
        int separator = data.AsSpan().IndexOf("\n\n"u8);
        if (separator < 0) { throw new InvalidDataException("The commit has no metadata separator."); }
        string[] headers = ObjectUtf8.GetString(data.AsSpan(0, separator)).Split('\n');
        string Header(string name) => headers.FirstOrDefault(line => line.StartsWith(name + " ", StringComparison.Ordinal))?[(name.Length + 1)..]
            ?? throw new InvalidDataException("The commit has no " + name + " header.");
        string tree = ValidateObjectId(Header("tree"));
        string[] parents = headers.Where(line => line.StartsWith("parent ", StringComparison.Ordinal)).Select(line => ValidateObjectId(line[7..])).ToArray();
        (string authorContact, DateTimeOffset authorDate) = ReadCommitInformationIdentity(Header("author"));
        (string committerContact, DateTimeOffset committerDate) = ReadCommitInformationIdentity(Header("committer"));
        // Commit headers are UTF8 even when the message declares a legacy encoding. Mapping their raw
        // contacts avoids Git re-encoding those identities together with a CP1252 commit body.
        GitResult mapped = await ExecuteAsync(["--no-optional-locks", "check-mailmap", "--stdin"], cancellationToken,
            standardInput: authorContact + "\n" + committerContact + "\n").ConfigureAwait(false);
        string[] contacts = mapped.StandardOutput.TrimEnd('\n').Split('\n');
        if (contacts.Length != 2) { throw new InvalidDataException("Git omitted a mapped commit identity."); }
        GitCommitIdentity author = CommitInformationIdentity(contacts[0], authorDate);
        GitCommitIdentity committer = CommitInformationIdentity(contacts[1], committerDate);
        GitResult message = await ExecuteAsync(["--no-optional-locks", "show", "--no-patch", "--no-show-signature", "--no-notes",
            "--no-ext-diff", "--no-textconv", "--encoding=UTF-8", "--format=format:%s%x00%b%x00%B", commit, "--"], cancellationToken).ConfigureAwait(false);
        string[] text = message.StandardOutput.Split('\0', 3);
        if (text.Length != 3) { throw new InvalidDataException("Git omitted the commit message separator."); }
        string? notes = null;
        if (includeNotes)
        {
            // %N observes Git's display refs, unlike notes --ref=<one namespace> show.
            notes = (await ExecuteAsync(["--no-optional-locks", "show", "--no-patch", "--no-show-signature", "--encoding=UTF-8",
                "--format=format:%N", commit, "--"], cancellationToken).ConfigureAwait(false)).StandardOutput;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new GitCommitDetails(commit, tree, parents, author, committer, text[0], text[1], notes) { Message = text[2] };
    }

    /// <summary>Reads every containing ref. Tags retain native descending tagger-date order, independently of the reference panel.</summary>
    public async Task<GitCommitContainment> GetCommitContainingReferencesAsync(string completeCommitHash,
        GitCommitReferenceScope scope = GitCommitReferenceScope.All, CancellationToken cancellationToken = default)
    {
        string commit = ValidateObjectId(completeCommitHash);
        if ((scope & ~GitCommitReferenceScope.All) != 0) { throw new ArgumentOutOfRangeException(nameof(scope)); }
        await ValidateCommitInformationCommitAsync(commit, cancellationToken).ConfigureAwait(false);
        if (scope == GitCommitReferenceScope.None) { return new(commit, null, null, []); }
        bool readHead = (scope & (GitCommitReferenceScope.LocalBranches | GitCommitReferenceScope.RemoteBranches)) != 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            (string? Branch, string? Hash) head = readHead ? await ReadCommitInformationHeadAsync(cancellationToken).ConfigureAwait(false) : default;
            List<string> arguments = ["--no-optional-locks", "for-each-ref", "--contains=" + commit, "--sort=-taggerdate",
                "--format=%(refname)%00%(objectname)%00%(if)%(authordate)%(then)%(objectname)%(else)%(*objectname)%(end)%00%(symref)%00%(taggerdate:iso-strict)"];
            if (scope.HasFlag(GitCommitReferenceScope.LocalBranches)) { arguments.Add("refs/heads/"); }
            if (scope.HasFlag(GitCommitReferenceScope.RemoteBranches)) { arguments.Add("refs/remotes/"); }
            if (scope.HasFlag(GitCommitReferenceScope.Tags)) { arguments.Add("refs/tags/"); }
            GitResult result = await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
            List<GitCommitReference> references = [];
            foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] fields = line.Split('\0');
                if (fields.Length != 5) { throw new InvalidDataException("Git omitted containing-reference metadata."); }
                (GitCommitReferenceKind kind, int prefix) = fields[0].StartsWith("refs/heads/", StringComparison.Ordinal) ? (GitCommitReferenceKind.LocalBranch, 11)
                    : fields[0].StartsWith("refs/remotes/", StringComparison.Ordinal) ? (GitCommitReferenceKind.RemoteBranch, 13)
                    : fields[0].StartsWith("refs/tags/", StringComparison.Ordinal) ? (GitCommitReferenceKind.Tag, 10)
                    : throw new InvalidDataException("Git returned a reference outside the requested namespaces.");
                DateTimeOffset? taggerDate = fields[4].Length == 0 ? null : DateTimeOffset.Parse(fields[4], CultureInfo.InvariantCulture);
                references.Add(new(kind, fields[0], fields[0][prefix..], ValidateObjectId(fields[1]), EmptyToNull(fields[2]),
                    EmptyToNull(fields[3]), fields[0] == head.Branch, taggerDate));
            }
            GitDetachedHeadInfo? detached = null;
            if (scope.HasFlag(GitCommitReferenceScope.LocalBranches) && head is { Branch: null, Hash: not null })
            {
                GitResult contains = await ExecuteAsync(["--no-optional-locks", "merge-base", "--is-ancestor", commit, head.Hash], cancellationToken,
                    allowedExitCodes: [1]).ConfigureAwait(false);
                detached = new(head.Hash, contains.Success);
            }
            if (readHead && head != await ReadCommitInformationHeadAsync(cancellationToken).ConfigureAwait(false)) { continue; }
            cancellationToken.ThrowIfCancellationRequested();
            return new(commit, head.Branch, detached, references.AsReadOnly());
        }
        throw new InvalidOperationException("HEAD changed while the containing references were being read. Reload commit information.");
    }

    /// <summary>Reads only exact attached annotations from captured raw tag objects, even if their ref is subsequently replaced.</summary>
    public async Task<IReadOnlyList<GitAnnotatedTagMessage>> GetAnnotatedTagMessagesAsync(string commitHash,
        IReadOnlyList<GitReference> attachedTags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachedTags);
        string commit = ValidateObjectId(commitHash);
        await ValidateCommitInformationCommitAsync(commit, cancellationToken).ConfigureAwait(false);
        List<GitAnnotatedTagMessage> messages = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (GitReference reference in attachedTags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reference.IsTag || reference.RevisionHash is null || ValidateObjectId(reference.RevisionHash) != commit || !names.Add(reference.Name)) { continue; }
            string oid = ValidateObjectId(reference.Hash);
            if (oid == commit) { continue; } // A lightweight tag has no annotation.
            (string type, byte[] data) = await ReadCommitInformationObjectAsync(oid, cancellationToken).ConfigureAwait(false);
            if (type != "tag") { continue; }
            GitResult target = await ExecuteAsync(["--no-optional-locks", "rev-parse", "--verify", "--end-of-options", oid + "^{commit}"], cancellationToken).ConfigureAwait(false);
            if (ReadOutputLine(target.StandardOutput) != commit) { continue; }
            int separator = data.AsSpan().IndexOf("\n\n"u8);
            if (separator < 0) { throw new InvalidDataException("The annotated tag has no message separator."); }
            List<string> warnings = [];
            string message = GitOutputText.Utf8(data.AsSpan(separator + 2), "tag message", warnings).TrimEnd();
            messages.Add(new(reference.Name, oid, commit, message));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return messages.AsReadOnly();
    }

    /// <summary>Windows' first-parent description, retaining full SHA1/SHA256 identities and literal tag names.</summary>
    public async Task<GitCommitDescription?> GetCommitDescribeAsync(string completeCommitHash, CancellationToken cancellationToken = default)
    {
        string commit = ValidateObjectId(completeCommitHash);
        await ValidateCommitInformationCommitAsync(commit, cancellationToken).ConfigureAwait(false);
        GitResult result = await ExecuteAsync(["--no-optional-locks", "describe", "--tags", "--first-parent",
            "--abbrev=" + commit.Length.ToString(CultureInfo.InvariantCulture), commit], cancellationToken, allowedExitCodes: [128]).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Success) { return null; }
        string raw = result.StandardOutput.TrimEnd('\r', '\n');
        string tag = raw;
        int? distance = null;
        string suffix = "-g" + commit;
        if (raw.EndsWith(suffix, StringComparison.Ordinal))
        {
            // An exact tag may itself be named "name-12-g<hash>". Prefer that real full ref
            // instead of interpreting its literal name as a describe-generated suffix.
            GitResult exact = await ExecuteAsync(["--no-optional-locks", "show-ref", "--verify", "--hash", "refs/tags/" + raw],
                cancellationToken, allowedExitCodes: [1, 128]).ConfigureAwait(false);
            string? exactCommit = exact.Success
                ? ReadOutputLine((await ExecuteAsync(["--no-optional-locks", "rev-parse", "--verify", "--end-of-options",
                    ValidateObjectId(ReadOutputLine(exact.StandardOutput)) + "^{commit}"], cancellationToken).ConfigureAwait(false)).StandardOutput) : null;
            if (exactCommit != commit)
            {
                string withoutHash = raw[..^suffix.Length];
                int dash = withoutHash.LastIndexOf('-');
                if (dash > 0 && int.TryParse(withoutHash.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                { tag = withoutHash[..dash]; distance = count; }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(commit, tag, distance, raw);
    }

    private async Task ValidateCommitInformationCommitAsync(string commit, CancellationToken token)
    {
        string type = ReadOutputLine((await ExecuteAsync(["--no-optional-locks", "cat-file", "-t", commit], token).ConfigureAwait(false)).StandardOutput);
        if (type != "commit") { throw new ArgumentException("Select a commit object.", nameof(commit)); }
    }

    private async Task<(string? Branch, string? Hash)> ReadCommitInformationHeadAsync(CancellationToken token)
    {
        GitResult branch = await ExecuteAsync(["--no-optional-locks", "symbolic-ref", "--quiet", "HEAD"], token, allowedExitCodes: [1]).ConfigureAwait(false);
        GitResult head = await ExecuteAsync(["--no-optional-locks", "rev-parse", "--verify", "--quiet", "--end-of-options", "HEAD^{commit}"], token,
            allowedExitCodes: [1, 128]).ConfigureAwait(false);
        return (branch.Success ? ReadOutputLine(branch.StandardOutput) : null, head.Success ? ReadOutputLine(head.StandardOutput) : null);
    }

    private async Task<(string Type, byte[] Data)> ReadCommitInformationObjectAsync(string oid, CancellationToken token)
    {
        GitResult result = await ExecuteAsync(["--no-optional-locks", "cat-file", "--batch"], token, standardInput: oid + "\n").ConfigureAwait(false);
        byte[] bytes = result.StandardOutputBytes;
        int newline = Array.IndexOf(bytes, (byte)'\n');
        if (newline < 0) { throw new InvalidDataException("Git omitted the object record header."); }
        string[] header = Encoding.ASCII.GetString(bytes, 0, newline).Split(' ');
        if (header.Length == 2 && header[1] == "missing") { throw new FileNotFoundException("Git object could not be read: " + oid); }
        if (header.Length != 3 || header[0] != oid || !int.TryParse(header[2], NumberStyles.None, CultureInfo.InvariantCulture, out int size)
            || size < 0 || bytes.LongLength != (long)newline + size + 2 || bytes[^1] != (byte)'\n')
        { throw new InvalidDataException("Git returned an incomplete commit information object."); }
        token.ThrowIfCancellationRequested();
        return (header[1], bytes.AsSpan(newline + 1, size).ToArray());
    }

    private static (string Contact, DateTimeOffset Date) ReadCommitInformationIdentity(string identity)
    {
        int emailEnd = identity.LastIndexOf('>');
        if (emailEnd < 0) { throw new InvalidDataException("The commit identity has no email terminator."); }
        string[] date = identity[(emailEnd + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (date.Length != 2 || !long.TryParse(date[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
            || date[1].Length != 5 || date[1][0] is not ('+' or '-') || !int.TryParse(date[1].AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(date[1].AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || minutes >= 60)
        { throw new InvalidDataException("The commit identity timestamp is incomplete."); }
        TimeSpan offset = TimeSpan.FromMinutes((date[1][0] == '-' ? -1 : 1) * (hours * 60 + minutes));
        return (identity[..(emailEnd + 1)], DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(offset));
    }

    private static GitCommitIdentity CommitInformationIdentity(string contact, DateTimeOffset date)
    {
        int email = contact.LastIndexOf(" <", StringComparison.Ordinal);
        if (email < 0 || !contact.EndsWith('>')) { throw new InvalidDataException("Git returned an incomplete mapped identity."); }
        return new(contact[..email], contact[(email + 2)..^1], date);
    }
}

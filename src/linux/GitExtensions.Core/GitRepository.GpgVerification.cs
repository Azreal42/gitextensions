using GitCommands.Git.Gpg;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public async Task<GitGpgVerification> ReadGpgVerificationAsync(string revision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable operation = _runner.BeginOperation();
        string commit = await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        const string format = "--format=%G?%x00%GG%x00Signature status: %G?%nSigner: %GS%nKey: %GK%nFingerprint: %GF";
        GitResult commitResult = await ExecuteAsync(["show", "--no-patch", "--no-notes", "--no-decorate", format, commit], cancellationToken).ConfigureAwait(false);
        string[] fields = commitResult.StandardOutput.Split('\0');
        if (fields.Length != 3) { throw new InvalidDataException("Git returned incomplete commit signature information."); }
        CommitStatus commitStatus = GpgSignatureStatusParser.Commit(fields[0]);
        // Use the same peeled targets as Windows GetRefs. Git also peels nested annotated tags; lightweight refs are excluded.
        IReadOnlyList<ReferenceTarget> targets = await ReadReferenceTargetsAsync(["refs/tags"], cancellationToken, noOptionalLocks: true).ConfigureAwait(false);
        GitReference[] annotated = targets.Where(target => target.Reference.IsTag && target.ObjectType == "commit"
            && target.Reference.RevisionHash == commit && target.Reference.Hash != commit).Select(target => target.Reference).ToArray();
        List<GitAnnotatedTagVerification> tags = [];
        foreach (GitReference reference in annotated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Verify the captured tag object, never a mutable ref name which could now point at a different revision.
            string objectId = ValidateValue(reference.Hash);
            GitResult raw = await _runner.RunAsync(Path, [.. GlobalOptions, "verify-tag", "--raw", "--", objectId], cancellationToken).ConfigureAwait(false);
            GitResult message = await _runner.RunAsync(Path, [.. GlobalOptions, "verify-tag", "--", objectId], cancellationToken).ConfigureAwait(false);
            tags.Add(new(reference.ShortName, objectId, GpgSignatureStatusParser.Tag(1, raw.StandardError),
                raw.ExitCode, message.ExitCode, raw.StandardError, message.StandardError));
        }
        cancellationToken.ThrowIfCancellationRequested();
        TagStatus tagStatus = tags.Count == 1 ? tags[0].Status : GpgSignatureStatusParser.Tag(tags.Count, null);
        string tagMessage = tags.Count == 1 ? tags[0].VerificationMessage : string.Join("\n\n",
            tags.Select(tag => tag.Name + "\n" + tag.VerificationMessage));
        GpgInfo information = new(commitStatus, fields[1] + commitResult.StandardError, tagStatus, tagMessage);
        return new(commit, information, fields[2] + commitResult.StandardError, tags.AsReadOnly());
    }
}

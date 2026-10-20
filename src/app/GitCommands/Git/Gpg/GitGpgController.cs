using GitExtensions.Extensibility.Git;
using GitExtUtils;
using GitUIPluginInterfaces;

namespace GitCommands.Git.Gpg;

public interface IGitGpgController
{
    /// <summary>
    /// Obtain the commit signature status on current revision.
    /// </summary>
    /// <returns>Enum value that indicate the gpg status for current git revision.</returns>
    Task<CommitStatus> GetRevisionCommitSignatureStatusAsync(GitRevision revision);

    /// <summary>
    /// Obtain the commit verification message, coming from <c>--pretty="format:%GG"</c>
    /// </summary>
    /// <returns>Full string coming from GPG analysis on current revision.</returns>
    string GetCommitVerificationMessage(GitRevision revision);

    /// <summary>
    /// Obtain the tag status on current revision.
    /// </summary>
    /// <returns>Enum value that indicate if current git revision has one tag with good signature, one tag with bad signature or more than one tag.</returns>
    Task<TagStatus> GetRevisionTagSignatureStatusAsync(GitRevision revision);

    /// <summary>
    /// Obtain the tag verification message for all the tag in current git revision
    /// </summary>
    /// <returns>Full concatenated string coming from GPG analysis on all tags on current git revision.</returns>
    string? GetTagVerifyMessage(GitRevision revision);
}

public partial class GitGpgController : IGitGpgController
{
    private readonly Func<IGitModule> _getModule;

    /// <summary>
    /// Obtain the tag verification message for all the tag in current git revision
    /// </summary>
    /// <returns>Full concatenated string coming from GPG analysis on all tags on current git revision.</returns>
    public GitGpgController(Func<IGitModule> getModule)
    {
        _getModule = getModule;
    }

    /// <summary>
    /// Obtain the commit signature status on current revision.
    /// </summary>
    /// <returns>Enum value that indicate the gpg status for current git revision.</returns>
    public async Task<CommitStatus> GetRevisionCommitSignatureStatusAsync(GitRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        IGitModule module = GetModule();

        return await Task.Run(() =>
        {
            GitArgumentBuilder args = new("log")
            {
                "--pretty=\"format:%G?\"",
                "-1",
                revision.Guid
            };
            string gpg = module.GitExecutable.GetOutput(args);

            return GpgSignatureStatusParser.Commit(gpg);
        });
    }

    /// <summary>
    /// Obtain the tag status on current revision.
    /// </summary>
    /// <returns>Enum value that indicate if current git revision has one tag with good signature, one tag with bad signature or more than one tag.</returns>
    public async Task<TagStatus> GetRevisionTagSignatureStatusAsync(GitRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        /* No Tag present, exit */
        List<IGitRef> usefulTagRefs = [.. revision.Refs.Where(x => x.IsTag && x.IsDereference)];

        if (usefulTagRefs.Count == 0)
        {
            return TagStatus.NoTag;
        }

        return await Task.Run(() =>
        {
            // Many is a classification of the attached tag set, independent of individual signatures.
            if (usefulTagRefs.Count > 1)
            {
                return GpgSignatureStatusParser.Tag(usefulTagRefs.Count, null);
            }

            string? rawGpgMessage = GetTagVerificationMessage(usefulTagRefs[0], true);
            return GpgSignatureStatusParser.Tag(1, rawGpgMessage);
        });
    }

    /// <summary>
    /// Obtain the commit verification message, coming from --pretty="format:%GG"
    /// </summary>
    /// <returns>Full string coming from GPG analysis on current revision.</returns>
    public string GetCommitVerificationMessage(GitRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        IGitModule module = GetModule();
        GitArgumentBuilder args = new("log")
        {
            "--pretty=\"format:%GG\"",
            "-1",
            revision.Guid
        };
        return module.GitExecutable.GetOutput(args);
    }

    /// <summary>
    /// Obtain the tag verification message for all the tag on the revision
    /// </summary>
    /// <returns>Full string coming from GPG analysis on current revision.</returns>
    public string? GetTagVerifyMessage(GitRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        List<IGitRef> usefulTagRefs = [.. revision.Refs.Where(x => x.IsTag && x.IsDereference)];
        return EvaluateTagVerifyMessage(usefulTagRefs);
    }

    private string? GetTagVerificationMessage(IGitRef tagRef, bool raw = true)
    {
        string tagName = tagRef.LocalName;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        GitArgumentBuilder args = new("verify-tag")
        {
            { raw, "--raw" },
            tagName
        };

        // 'verify-tag' returns info in stderr
        return GetModule().GitExecutable.Execute(args, throwOnErrorExit: false).StandardError;
    }

    private string? EvaluateTagVerifyMessage(IReadOnlyList<IGitRef> usefulTagRefs)
    {
        if (usefulTagRefs.Count == 0)
        {
            return "";
        }

        if (usefulTagRefs.Count == 1)
        {
            return GetTagVerificationMessage(usefulTagRefs[0], false);
        }

        /* When _usefulTagRefs.Count > 1 */
        string tagVerifyMessage = "";

        /* Only to populate TagVerifyMessage */
        foreach (IGitRef tagRef in usefulTagRefs)
        {
            /* String printed in dialog box */
            tagVerifyMessage = $"{tagVerifyMessage}{tagRef.LocalName}\r\n{GetTagVerificationMessage(tagRef, false)}\r\n\r\n";
        }

        return tagVerifyMessage;
    }

    private IGitModule GetModule()
        => _getModule() ?? throw new ArgumentException($"Require a valid instance of {nameof(IGitModule)}");
}

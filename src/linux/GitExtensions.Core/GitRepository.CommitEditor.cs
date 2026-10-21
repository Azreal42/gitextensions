namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Git owns the message/editor. Core keeps its mutation lease until the terminal callback is drained.</summary>
    public async Task<GitResult> CommitWithGitEditorAsync(GitCommitOptions options,
        Func<IReadOnlyList<string>, CancellationToken, Task<GitResult>> launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(launch);
        // Commands.Commit(false) has no message-derived non-HEAD kind. The UI disables those choices.
        GitCommitOptions native = options with
        {
            Message = null, TemplatePath = null, FixupRevision = null, SquashRevision = null, AmendRevision = null,
            ResetAuthor = options.Amend && options.ResetAuthor,
            Author = string.IsNullOrEmpty(options.Author) ? null : options.Author.Trim().Trim('"'),
            SigningKey = options.Sign is true && !string.IsNullOrWhiteSpace(options.SigningKey) ? options.SigningKey : null
        };
        List<string> arguments = CreateCommitArguments(native);
        AddCommitTailArguments(arguments, native);
        IReadOnlyList<string> actual = Array.AsReadOnly<string>([.. GlobalOptions, .. arguments]);
        return await WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            GitResult result = await launch(actual, token).ConfigureAwait(false);
            if (!result.Success) { throw new GitCommandException(actual, result); }
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static List<string> CreateCommitArguments(GitCommitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        int targetedKinds = (options.FixupRevision is null ? 0 : 1)
            + (options.SquashRevision is null ? 0 : 1) + (options.AmendRevision is null ? 0 : 1);
        if (targetedKinds > 1 || options.Amend && targetedKinds != 0)
        {
            throw new ArgumentException("Choose one targeted commit kind; amending HEAD is a separate operation.", nameof(options));
        }

        if (options.Sign == false && options.SigningKey is not null)
        {
            throw new ArgumentException("A signing key requires signing.", nameof(options));
        }

        List<string> arguments = ["commit"];
        if (options.NoVerify) { arguments.Add("--no-verify"); }
        if (options.Amend)
        {
            arguments.Add("--amend");
        }

        if (options.SignOff)
        {
            arguments.Add("--signoff");
        }

        if (options.Sign == true || options.SigningKey is not null)
        {
            arguments.Add(options.SigningKey is null ? "--gpg-sign" : "--gpg-sign=" + options.SigningKey);
        }
        else if (options.Sign == false)
        {
            arguments.Add("--no-gpg-sign");
        }

        return arguments;
    }

    private static void AddCommitTailArguments(List<string> arguments, GitCommitOptions options)
    {
        if (options.AllowEmpty)
        {
            arguments.Add("--allow-empty");
        }

        if (options.ResetAuthor)
        {
            arguments.Add("--reset-author");
        }

        if (options.Author is not null)
        {
            arguments.Add("--author=" + options.Author);
        }
    }
}

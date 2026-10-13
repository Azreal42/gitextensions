using System.Globalization;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public async Task<IReadOnlyList<GitSearchMatch>> SearchAsync(string query, string? path = null, string? revision = null,
        bool ignoreCase = false, bool regex = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        List<string> arguments = ["grep", "--no-color", "-I", "-n", "-z"];
        arguments.Add(regex ? "--extended-regexp" : "--fixed-strings");
        if (ignoreCase)
        {
            arguments.Add("--ignore-case");
        }

        arguments.AddRange(["-e", query]);
        string? commit = null;
        if (revision is not null)
        {
            commit = await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
            arguments.Add(commit);
        }

        arguments.Add("--");
        if (path is not null)
        {
            arguments.Add(ValidatePath(path));
        }

        GitResult result = await ExecuteAsync(arguments, cancellationToken, allowedExitCodes: [1]).ConfigureAwait(false);
        List<GitSearchMatch> matches = [];
        int start = 0;
        while (start < result.StandardOutput.Length)
        {
            int pathEnd = result.StandardOutput.IndexOf('\0', start);
            int numberEnd = pathEnd < 0 ? -1 : result.StandardOutput.IndexOf('\0', pathEnd + 1);
            if (pathEnd < 0 || numberEnd < 0)
            {
                throw new FormatException("Incomplete Git grep record.");
            }

            int textEnd = result.StandardOutput.IndexOf('\n', numberEnd + 1);
            if (textEnd < 0)
            {
                textEnd = result.StandardOutput.Length;
            }

            string filePath = result.StandardOutput[start..pathEnd];
            if (commit is not null && filePath.StartsWith(commit + ":", StringComparison.Ordinal))
            {
                filePath = filePath[(commit.Length + 1)..];
            }

            matches.Add(new GitSearchMatch(filePath, int.Parse(result.StandardOutput.AsSpan(pathEnd + 1, numberEnd - pathEnd - 1), CultureInfo.InvariantCulture),
                result.StandardOutput[(numberEnd + 1)..textEnd]));
            start = textEnd + 1;
        }

        return matches;
    }

    public async Task<IReadOnlyList<GitReflogEntry>> GetReflogAsync(int count = 100, string reference = "HEAD", CancellationToken cancellationToken = default)
        => await GetReflogAsync((int?)count, reference, cancellationToken).ConfigureAwait(false);

    /// <summary>Reads the selected reflog; a null count returns all native entries.</summary>
    public async Task<IReadOnlyList<GitReflogEntry>> GetReflogAsync(int? count, string reference = "HEAD", CancellationToken cancellationToken = default)
    {
        if (count is int limit) { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit, nameof(count)); }
        if (reference == "HEAD" && !await HasHeadAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        return await ReadReflogEntriesAsync(count, reference, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitResult> ResetAsync(string revision, GitResetMode mode = GitResetMode.Mixed, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        string commit = await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        return await MutateAsync(["reset", "--" + mode.ToString().ToLowerInvariant(), commit, "--"], cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetCommitTemplateAsync(string? templatePath = null, CancellationToken cancellationToken = default)
    {
        if (templatePath is null)
        {
            templatePath = (await GetConfigAsync("^commit\\.template$", cancellationToken: cancellationToken).ConfigureAwait(false)).LastOrDefault().Value;
        }

        if (string.IsNullOrEmpty(templatePath))
        {
            return null;
        }

        if (templatePath.StartsWith("~/", StringComparison.Ordinal))
        {
            templatePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), templatePath[2..]);
        }

        System.Text.Encoding encoding = await GetCommitMessageEncodingAsync(cancellationToken).ConfigureAwait(false);
        byte[] bytes = await File.ReadAllBytesAsync(System.IO.Path.GetFullPath(templatePath, Path), cancellationToken).ConfigureAwait(false);
        int offset = encoding.CodePage == System.Text.Encoding.UTF8.CodePage && bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return encoding.GetString(bytes, offset, bytes.Length - offset);
    }

    public async Task<GitResult> CommitAsync(GitCommitOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.FixupRevision is not null && options.SquashRevision is not null)
        {
            throw new ArgumentException("A commit cannot be both fixup and squash.", nameof(options));
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

        if (options.FixupRevision is not null)
        {
            arguments.Add("--fixup=" + await ResolveRevisionAsync(options.FixupRevision, cancellationToken).ConfigureAwait(false));
        }

        if (options.SquashRevision is not null)
        {
            arguments.Add("--squash=" + await ResolveRevisionAsync(options.SquashRevision, cancellationToken).ConfigureAwait(false));
        }

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

        string? message = options.Message;
        if (message is null && options.TemplatePath is not null)
        {
            message = await GetCommitTemplateAsync(options.TemplatePath, cancellationToken).ConfigureAwait(false);
        }

        if (message is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            if (message.Contains('\0'))
            {
                throw new ArgumentException("Commit messages cannot contain NUL.", nameof(options));
            }

            arguments.Add("--file=-");
        }
        else if (options.FixupRevision is null && options.SquashRevision is null)
        {
            throw new ArgumentException("Provide an edited commit message or a template.", nameof(options));
        }

        return await CommitEncodedAsync(arguments, message, cancellationToken).ConfigureAwait(false);
    }
}

using System.Text;

namespace GitExtensions.Linux.Desktop;

/// <summary>The COMMITMESSAGE/MERGE_MSG and GitExtensions.amend files used by CommitMessageManager.</summary>
public sealed class CommitDraftStore
{
    private readonly string _messagePath;
    private readonly string _mergePath;
    private readonly string _amendPath;
    private readonly Encoding _encoding;
    private string? _loadedPath;
    private string? _loadedMessage;
    private CommitDraftStore(string messagePath, string mergePath, string amendPath, Encoding encoding)
        => (_messagePath, _mergePath, _amendPath, _encoding) = (messagePath, mergePath, amendPath, encoding);

    public static async Task<CommitDraftStore> OpenAsync(GitRepository repository, CancellationToken cancellationToken = default)
    {
        async Task<string> GitPath(string name)
        {
            GitResult result = await repository.RunAsync(["rev-parse", "--git-path", name], cancellationToken).ConfigureAwait(false);
            return Path.GetFullPath(result.StandardOutput.TrimEnd('\r', '\n'), repository.Path);
        }
        string message = await GitPath("COMMITMESSAGE").ConfigureAwait(false);
        string merge = await GitPath("MERGE_MSG").ConfigureAwait(false);
        string amend = await GitPath("GitExtensions.amend").ConfigureAwait(false);
        Encoding encoding = await repository.GetCommitMessageEncodingAsync(cancellationToken).ConfigureAwait(false);
        return new CommitDraftStore(message, merge, amend, encoding);
    }

    public async Task<(string Message, bool Amend)> LoadAsync(bool rememberAmend, CancellationToken cancellationToken = default)
    {
        string path = MessagePath;
        byte[] bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : [];
        int offset = _encoding.CodePage == Encoding.UTF8.CodePage && bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        string message = _encoding.GetString(bytes, offset, bytes.Length - offset);
        _loadedPath = path;
        _loadedMessage = message;
        bool amend = await LoadAmendAsync(rememberAmend, cancellationToken).ConfigureAwait(false);
        return (message, amend);
    }

    public async Task<bool> LoadAmendAsync(bool rememberAmend, CancellationToken cancellationToken = default)
        => rememberAmend && File.Exists(_amendPath) && bool.TryParse(await File.ReadAllTextAsync(_amendPath, cancellationToken).ConfigureAwait(false), out bool saved) && saved;

    // Closing is synchronous so a repository/window cannot disappear before its draft is durable.
    public void Save(string? message, bool amend, bool rememberAmend)
    {
        if (!Directory.Exists(Path.GetDirectoryName(_messagePath))) { throw new DirectoryNotFoundException(Path.GetDirectoryName(_messagePath)); }
        string path = MessagePath;
        // A merge can start while this window is open. Its newly generated message
        // belongs to Git until the user has edited this window's old draft.
        if (_loadedPath != path && _loadedMessage == (message ?? "")) { return; }
        WriteAtomic(path, _encoding.GetBytes(message ?? ""));
        SaveAmend(amend, rememberAmend);
    }

    // The Git-editor mode never rewrites the disabled form's unrelated message draft.
    public void SaveAmend(bool amend, bool rememberAmend)
    {
        if (rememberAmend && amend) { WriteAtomic(_amendPath, Encoding.ASCII.GetBytes(bool.TrueString)); }
        else { File.Delete(_amendPath); }
    }

    public void WriteForCommit(string message, bool merge) => WriteAtomic(merge ? _mergePath : _messagePath, _encoding.GetBytes(message));
    public void RememberDisplayedMessage(string message) => _loadedMessage = message;
    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite: true); }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }
    public void Reset()
    {
        File.Delete(_messagePath);
        File.Delete(_amendPath);
    }
    public bool IsMergeCommit => File.Exists(_mergePath);
    private string MessagePath => IsMergeCommit ? _mergePath : _messagePath;

    /// <summary>Port of CommitMessageManager.FormatCommitMessage; issue-number '#' lines remain unless using a template.</summary>
    public static string FormatMessage(string message, bool usingTemplate, bool ensureBlankSecondLine)
    {
        if (message.Length == 0) { return ""; }
        StringBuilder formatted = new();
        int lineNumber = 1;
        foreach (string line in message.Split('\n'))
        {
            if (usingTemplate && line.StartsWith('#')) { continue; }
            if (ensureBlankSecondLine && lineNumber == 2 && line.Length > 0) { formatted.AppendLine(); }
            formatted.AppendLine(line);
            lineNumber++;
        }
        return formatted.ToString();
    }
}

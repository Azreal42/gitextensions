using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitExtensions.Linux;

public enum GitRebaseEditorKind { Todo, Message }

/// <summary>A snapshot read from Git's editor invocation. Only the reader can create a writable request.</summary>
public sealed class GitRebaseEditorRequest
{
    internal GitRebaseEditorRequest(string directory, string stateHash, string path, string repository,
        GitRebaseEditorKind kind, Encoding encoding, byte[] before)
    {
        Directory = directory; StateHash = stateHash; FilePath = path; RepositoryPath = repository;
        Kind = kind; EncodingName = encoding.WebName; Text = encoding.GetString(before);
        PreimageSha256 = Convert.ToHexString(SHA256.HashData(before));
    }
    internal string Directory { get; }
    internal string StateHash { get; }
    public string FilePath { get; }
    public string RepositoryPath { get; }
    public GitRebaseEditorKind Kind { get; }
    public string EncodingName { get; }
    public string Text { get; }
    public string PreimageSha256 { get; }
}

internal sealed record NativeRebaseEditorState(string RepositoryPath, string GitDirectory, string EditorExecutable,
    string EncodingName, IReadOnlyList<string> SkippedCommits, GitRebaseOptions? Options = null)
{
    // Older journals did not distinguish Git's generated instruction text from commit messages.
    public string? TodoEncodingName { get; init; }
}

public static partial class GitRebaseEditor
{
    internal const string NativeStateDirectoryName = "gitextensions-rebase-native";
    public static bool IsNativeEditorInvocation(string[] args)
        => args.FirstOrDefault() is "--edit-rebase-todo" or "--edit-rebase-message";

    public static GitRebaseEditorRequest ReadNativeEditorRequest(string[] args)
    {
        if (!IsNativeEditorInvocation(args) || args.Length != 2 || !System.IO.Path.IsPathFullyQualified(args[1]))
        { throw new ArgumentException("Git must supply one absolute rebase editor filename.", nameof(args)); }
        string directory = Environment.GetEnvironmentVariable(DirectoryVariable)
            ?? throw new InvalidOperationException("Missing native rebase editor state.");
        NativeRebaseEditorState state = ReadNativeState(directory);
        GitRebaseEditorKind kind = args[0] == "--edit-rebase-todo" ? GitRebaseEditorKind.Todo : GitRebaseEditorKind.Message;
        string path = ValidateNativeEditorPath(directory, state, args[1], kind);
        _ = ValidateCurrentEncoding(state);
        Encoding encoding = NativeEditorEncoding(state, kind);
        return new(directory, Hash(File.ReadAllBytes(System.IO.Path.Combine(directory, "state.json"))), path,
            state.RepositoryPath, kind, encoding, File.ReadAllBytes(path));
    }

    /// <summary>Validate the codec and preimage before any write; never replace Git's generated todo implicitly.</summary>
    public static void WriteNativeEditorRequest(GitRebaseEditorRequest request, string text)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0')) { throw new ArgumentException("Rebase editor text cannot contain NUL.", nameof(text)); }
        NativeRebaseEditorState state = ReadNativeState(request.Directory);
        string path = ValidateNativeEditorPath(request.Directory, state, request.FilePath, request.Kind);
        _ = ValidateCurrentEncoding(state);
        byte[] bytes = NativeEditorEncoding(state, request.Kind).GetBytes(text);
        if (Hash(File.ReadAllBytes(System.IO.Path.Combine(request.Directory, "state.json"))) != request.StateHash)
        { throw new InvalidOperationException("The rebase editor session changed. Reopen the editor before saving."); }
        // Keep Git's file locked across the compare and write. Git waits for this editor process,
        // so it cannot truncate or replace its own file while the snapshot is being saved.
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using MemoryStream existing = new(); stream.CopyTo(existing);
        if (Hash(existing.ToArray()) != request.PreimageSha256)
        { throw new InvalidOperationException("Git's editor file changed. Reopen it before saving."); }
        string temporary = path + ".gitextensions-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(temporary, File.GetUnixFileMode(path)); }
            // Re-check the directory entry too: another editor might have replaced the path
            // rather than writing the inode locked above. Git itself is waiting for us.
            RejectLink(path);
            if (Hash(File.ReadAllBytes(path)) != request.PreimageSha256)
            { throw new InvalidOperationException("Git's editor file changed. Reopen it before saving."); }
            stream.Dispose();
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static NativeRebaseEditorState ReadNativeState(string directory)
    {
        if (!System.IO.Path.IsPathFullyQualified(directory)) { throw new ArgumentException("The rebase editor state directory must be absolute.", nameof(directory)); }
        string fullDirectory = System.IO.Path.GetFullPath(directory);
        RejectLink(fullDirectory);
        string statePath = System.IO.Path.Combine(fullDirectory, "state.json"); RejectLink(statePath);
        NativeRebaseEditorState state = JsonSerializer.Deserialize<NativeRebaseEditorState>(File.ReadAllBytes(statePath))
            ?? throw new FormatException("Invalid native rebase editor state.");
        if (!System.IO.Path.IsPathFullyQualified(state.RepositoryPath) || !System.IO.Path.IsPathFullyQualified(state.GitDirectory)
            || fullDirectory != System.IO.Path.Combine(state.GitDirectory, NativeStateDirectoryName))
        { throw new InvalidOperationException("Native rebase editor state is outside its Git directory."); }
        GitRepositoryInfo info = new GitRepository(state.RepositoryPath).GetInfoAsync().GetAwaiter().GetResult();
        if (info.RootPath != state.RepositoryPath || info.GitDirectory != state.GitDirectory || info.IsBare)
        { throw new InvalidOperationException("Native rebase editor state belongs to a different repository."); }
        _ = GetStrictEncoding(state.EncodingName);
        ValidateNativeTodoEncoding(state);
        return state;
    }

    internal static void WriteNativeState(string directory, NativeRebaseEditorState state)
    {
        _ = GetStrictEncoding(state.EncodingName);
        ValidateNativeTodoEncoding(state);
        string temporary = System.IO.Path.Combine(directory, "state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state));
            File.Move(temporary, System.IO.Path.Combine(directory, "state.json"), overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static IReadOnlyDictionary<string, string?> NativeEnvironmentFor(string directory, NativeRebaseEditorState state)
    {
        Dictionary<string, string?> environment = new() { [DirectoryVariable] = directory };
        if (!string.IsNullOrEmpty(state.EditorExecutable))
        {
            ValidateExecutable(state.EditorExecutable);
            environment["GIT_SEQUENCE_EDITOR"] = state.EditorExecutable + " --edit-rebase-todo";
            environment["GIT_EDITOR"] = state.EditorExecutable + " --edit-rebase-message";
        }
        return environment;
    }

    internal static Encoding GetStrictEncoding(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        const string ascii = "0123456789AZaz !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~\r\n";
        if (encoding.CodePage != Encoding.UTF8.CodePage && !encoding.IsSingleByte
            || !encoding.GetBytes(ascii).SequenceEqual(Encoding.ASCII.GetBytes(ascii)))
        { throw new NotSupportedException("Rebase editor encoding must be UTF-8 or an ASCII-compatible single-byte encoding."); }
        return encoding.CodePage == Encoding.UTF8.CodePage ? new UTF8Encoding(false, true) : encoding;
    }

    internal static string? NativeTodoEncodingName(NativeRebaseEditorState state)
        => state.TodoEncodingName ?? (GetStrictEncoding(state.EncodingName).CodePage == Encoding.UTF8.CodePage ? "utf-8" : null);

    private static void ValidateNativeTodoEncoding(NativeRebaseEditorState state)
    {
        if (state.TodoEncodingName is not null && GetStrictEncoding(state.TodoEncodingName).CodePage != Encoding.UTF8.CodePage)
        { throw new NotSupportedException("Native rebase journals must declare UTF-8 instruction text separately from the commit codec."); }
    }

    private static Encoding NativeEditorEncoding(NativeRebaseEditorState state, GitRebaseEditorKind kind)
        => GetStrictEncoding(kind == GitRebaseEditorKind.Message ? state.EncodingName
            : NativeTodoEncodingName(state) ?? throw new NotSupportedException("This older native journal does not identify a reliable todo codec. Abort remains available; no todo bytes have been converted."));

    private static Encoding ValidateCurrentEncoding(NativeRebaseEditorState state)
    {
        Encoding current = new GitRepository(state.RepositoryPath).GetCommitMessageEncodingAsync().GetAwaiter().GetResult();
        if (current.WebName != state.EncodingName)
        { throw new InvalidOperationException("The commit encoding changed during this editor session. Restore its original encoding before saving."); }
        return current;
    }

    private static string ValidateNativeEditorPath(string directory, NativeRebaseEditorState state, string supplied, GitRebaseEditorKind kind)
    {
        string path = System.IO.Path.GetFullPath(supplied);
        string merge = System.IO.Path.Combine(state.GitDirectory, "rebase-merge");
        string[] allowed = kind == GitRebaseEditorKind.Todo ? [System.IO.Path.Combine(merge, "git-rebase-todo")]
            : [System.IO.Path.Combine(state.GitDirectory, "COMMIT_EDITMSG"), System.IO.Path.Combine(merge, "message"),
               System.IO.Path.Combine(merge, "message-squash"), System.IO.Path.Combine(merge, "message-fixup")];
        if (!allowed.Contains(path, StringComparer.Ordinal) || !Directory.Exists(merge))
        { throw new InvalidOperationException("Git supplied an unexpected file, or this rebase is no longer active."); }
        RejectLink(merge); RejectLink(path); RejectLink(directory);
        return path;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        { throw new IOException("Rebase editor paths must not be symbolic links."); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

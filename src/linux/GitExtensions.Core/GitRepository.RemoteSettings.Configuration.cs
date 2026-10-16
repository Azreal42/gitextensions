using System.Collections.Concurrent;
using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RemoteSettingsLocks = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding RemoteSettingsUtf8 = new(false, true);
    private bool? _remoteSettingsUrlResetSupported;

    private Task<T> WithRemoteSettingsAsync<T>(Func<RemoteSettingsState, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        => WithMutationAsync(async token =>
        {
            GitRepositoryInfo info = await GetInfoAsync(token).ConfigureAwait(false);
            string common = GitRulesFiles.Resolve(info.CommonDirectory).Path;
            SemaphoreSlim shared = RemoteSettingsLocks.GetOrAdd(common, _ => new(1, 1));
            await shared.WaitAsync(token).ConfigureAwait(false);
            try { return await operation(await ReadRemoteSettingsStateAsync(common, token).ConfigureAwait(false), token).ConfigureAwait(false); }
            finally { shared.Release(); }
        }, cancellationToken);

    private async Task<RemoteSettingsState> ReadRemoteSettingsStateAsync(string common, CancellationToken token)
    {
        string path = await GetGitPathAsync("config", token).ConfigureAwait(false);
        RemoteSettingsFile config = await ReadRemoteSettingsFileAsync(path, token).ConfigureAwait(false);
        GitBinaryResult result = await _runner.RunBinaryAsync(Path,
            [.. GlobalOptions, "config", "--null", "--list", "--show-origin", "--show-scope"], token).ConfigureAwait(false);
        if (!result.Success) { throw new GitCommandException(["config", "--list"], new(result.ExitCode, RemoteSettingsUtf8.GetString(result.StandardOutput), result.StandardError)); }
        string[] fields = RemoteSettingsUtf8.GetString(result.StandardOutput).Split('\0');
        List<GitRemoteSetting> settings = [];
        for (int i = 0; i + 2 < fields.Length; i += 3)
        {
            string origin = fields[i + 1];
            if (origin.StartsWith("file:", StringComparison.Ordinal))
            { origin = GitRulesFiles.Resolve(System.IO.Path.GetFullPath(origin[5..], Path)).Path; }
            int newline = fields[i + 2].IndexOf('\n');
            settings.Add(new(newline < 0 ? fields[i + 2] : fields[i + 2][..newline],
                newline < 0 ? "" : fields[i + 2][(newline + 1)..], fields[i], origin));
        }
        GitBinaryResult local = await _runner.RunBinaryAsync(Path,
            [.. GlobalOptions, "config", "--no-includes", "--file", path, "--null", "--list"], token).ConfigureAwait(false);
        if (!local.Success) { throw new GitCommandException(["config", "--local", "--list"], new(local.ExitCode, RemoteSettingsUtf8.GetString(local.StandardOutput), local.StandardError)); }
        IReadOnlyList<KeyValuePair<string, string>> pairs = ParseConfig(RemoteSettingsUtf8.GetString(local.StandardOutput));
        List<RemoteSettingsFile> sources = [config];
        foreach (string source in settings.Where(s => System.IO.Path.IsPathRooted(s.Origin)).Select(s => s.Origin).Distinct(StringComparer.Ordinal))
        {
            if (source != config.Path) { sources.Add(await ReadRemoteSettingsFileAsync(source, token).ConfigureAwait(false)); }
        }
        CheckRemoteSettingsFile(config, await ReadRemoteSettingsFileAsync(path, token).ConfigureAwait(false));
        string material = string.Concat(settings.Select(s => Frame(s.Key) + Frame(s.Value) + Frame(s.Scope) + Frame(s.Origin)))
            + string.Concat(sources.OrderBy(s => s.Path, StringComparer.Ordinal).Select(s => Frame(s.Path) + Frame(s.Hash) + s.Identity + s.Mode + s.Attributes));
        return new(common, config, settings.AsReadOnly(), pairs, sources.AsReadOnly(), GitRulesFiles.Hash(RemoteSettingsUtf8.GetBytes(material)));
        static string Frame(string value) => value.Length + ":" + value;
    }

    private static async Task<RemoteSettingsFile> ReadRemoteSettingsFileAsync(string path, CancellationToken token)
    {
        GitRulesLocation location = GitRulesFiles.Resolve(path);
        GitRulesIdentity? identity = GitRulesIdentity.ReadPath(location.Path);
        byte[] bytes = [];
        UnixFileMode? mode = null;
        FileAttributes attributes = FileAttributes.Normal;
        if (identity is not null)
        {
            if (!identity.Value.IsRegular) { throw new IOException("The Git configuration is not a regular file: " + path); }
            await using FileStream file = new(location.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (GitRulesIdentity.ReadHandle(file.SafeFileHandle) != identity) { throw RemoteSettingsChanged(); }
            using MemoryStream contents = new(); await file.CopyToAsync(contents, token).ConfigureAwait(false); bytes = contents.ToArray();
            mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(file.SafeFileHandle);
            attributes = File.GetAttributes(file.SafeFileHandle);
        }
        GitRulesFiles.CheckLocation(location, GitRulesFiles.Resolve(path));
        return new(path, location.Path, location, identity, bytes, mode, attributes);
    }

    private static GitRemoteSettingsException RemoteSettingsChanged()
        => new("Git configuration changed outside this editor. Reload the remote settings before applying this change.", false, null);

    private static void CheckRemoteSettingsFile(RemoteSettingsFile expected, RemoteSettingsFile actual)
    {
        try { GitRulesFiles.CheckLocation(expected.Location, actual.Location); }
        catch (IOException exception) { throw new GitRemoteSettingsException(exception.Message, false, null, exception); }
        if (expected.Identity != actual.Identity || expected.Hash != actual.Hash || expected.Mode != actual.Mode || expected.Attributes != actual.Attributes)
        { throw RemoteSettingsChanged(); }
    }

    private static void CheckRemoteSettingsState(RemoteSettingsState expected, RemoteSettingsState actual)
    {
        if (expected.CommonDirectory != actual.CommonDirectory) { throw new ArgumentException("The settings snapshot belongs to another repository."); }
        CheckRemoteSettingsFile(expected.Config, actual.Config);
        if (expected.Fingerprint != actual.Fingerprint) { throw RemoteSettingsChanged(); }
        foreach (RemoteSettingsFile source in expected.Sources)
        {
            RemoteSettingsFile? current = actual.Sources.SingleOrDefault(s => s.Path == source.Path);
            if (current is null) { throw RemoteSettingsChanged(); }
            CheckRemoteSettingsFile(source, current);
        }
    }

    private async Task CheckRemoteSettingsUnchangedAsync(RemoteSettingsState expected, CancellationToken token)
        => CheckRemoteSettingsState(expected, await ReadRemoteSettingsStateAsync(expected.CommonDirectory, token).ConfigureAwait(false));

    private sealed record RemoteConfigEdit(string Key, IReadOnlyList<string> Values);

    private async Task<byte[]> PrepareRemoteSettingsConfigAsync(RemoteSettingsState state, IReadOnlyList<RemoteConfigEdit> edits, CancellationToken token)
    {
        if (edits.Count == 0) { return state.Config.Bytes; }
        string temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(state.Config.Path)!, ".gitextensions-remotes-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (FileStream prepared = CreateRemoteSettingsPrivateFile(temporary))
            {
                await prepared.WriteAsync(state.Config.Bytes, token).ConfigureAwait(false);
                await prepared.FlushAsync(token).ConfigureAwait(false);
            }
            foreach (RemoteConfigEdit edit in edits)
            {
                await ExecuteAsync(["config", "--no-includes", "--file", temporary, "--unset-all", "--", edit.Key], token, allowedExitCodes: [5]).ConfigureAwait(false);
                foreach (string value in edit.Values)
                { await ExecuteAsync(["config", "--no-includes", "--file", temporary, "--add", "--", edit.Key, value], token).ConfigureAwait(false); }
            }
            await ExecuteAsync(["config", "--no-includes", "--file", temporary, "--list"], token).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(temporary, token).ConfigureAwait(false);
        }
        finally { File.Delete(temporary); File.Delete(temporary + ".lock"); }
    }

    private async Task PublishRemoteSettingsConfigAsync(RemoteSettingsState expected, byte[] bytes, CancellationToken token)
    {
        if (bytes.AsSpan().SequenceEqual(expected.Config.Bytes)) { await CheckRemoteSettingsUnchangedAsync(expected, token).ConfigureAwait(false); return; }
        List<(string Path, FileStream Stream)> locks = [];
        string targetLock = expected.Config.Path + ".lock";
        bool published = false;
        try
        {
            foreach (string path in new[] { targetLock, expected.Config.LogicalPath + ".lock" }.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            { locks.Add((path, CreateRemoteSettingsPrivateFile(path))); }
            await CheckRemoteSettingsUnchangedAsync(expected, token).ConfigureAwait(false);
            FileStream output = locks.Single(pair => pair.Path == targetLock).Stream;
            await output.WriteAsync(bytes, token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows() && expected.Config.Mode is UnixFileMode mode) { File.SetUnixFileMode(output.SafeFileHandle, mode); }
            if (OperatingSystem.IsWindows()) { File.SetAttributes(output.SafeFileHandle, expected.Config.Attributes); }
            await CheckRemoteSettingsUnchangedAsync(expected, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // The prepared bytes become visible together. Following the resolved target preserves a legitimate config symlink.
            output.Dispose();
            File.Move(targetLock, expected.Config.Path, overwrite: true); published = true;
        }
        catch (Exception exception) when (exception is not GitRemoteSettingsException && exception is not OperationCanceledException)
        { throw new GitRemoteSettingsException("Cannot publish Git configuration: " + exception.Message, published, null, exception); }
        finally
        {
            foreach ((string path, FileStream stream) in locks) { stream.Dispose(); if (path != targetLock || !published) { File.Delete(path); } }
        }
    }

    private static IReadOnlyList<string> RemoteSettingsList(IEnumerable<string> values)
    {
        List<string> result = [];
        foreach (string value in values) { if (value.Length == 0) { result.Clear(); } else { result.Add(value); } }
        return result;
    }
    private static FileStream CreateRemoteSettingsPrivateFile(string path)
    {
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        return new(path, options);
    }

    private async Task EnsureRemoteSettingsUrlResetAsync(CancellationToken token)
    {
        if (_remoteSettingsUrlResetSupported is null)
        {
            string temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-url-reset-" + Guid.NewGuid().ToString("N"));
            try
            {
                await ExecuteAsync(["init", "--bare", "--quiet", temporary], token).ConfigureAwait(false);
                string config = System.IO.Path.Combine(temporary, "config");
                foreach ((string key, string value) in new[]
                {
                    ("url", "inherited-fetch"), ("url", ""), ("url", "replacement-fetch"),
                    ("pushurl", "inherited-push"), ("pushurl", ""), ("pushurl", "replacement-push")
                })
                { await ExecuteAsync(["config", "--file", config, "--add", "remote.probe." + key, value], token).ConfigureAwait(false); }
                GitResult fetch = await ExecuteAsync(["--git-dir=" + temporary, "remote", "get-url", "--all", "probe"], token).ConfigureAwait(false);
                GitResult push = await ExecuteAsync(["--git-dir=" + temporary, "remote", "get-url", "--push", "--all", "probe"], token).ConfigureAwait(false);
                _remoteSettingsUrlResetSupported = NativeUrlLines(fetch.StandardOutput).SequenceEqual(["replacement-fetch"])
                    && NativeUrlLines(push.StandardOutput).SequenceEqual(["replacement-push"]);
            }
            finally { if (Directory.Exists(temporary)) { Directory.Delete(temporary, recursive: true); } }
        }
        if (_remoteSettingsUrlResetSupported != true)
        { throw new GitRemoteSettingsException("This Git version cannot replace inherited URL lists without keeping their old destinations. Edit the source configuration or update Git before saving this remote.", false, null); }
    }
    private static bool TryRemoteSetting(string key, out string name, out bool disabled)
    {
        disabled = key.StartsWith("-remote.", StringComparison.Ordinal);
        int prefix = disabled ? 8 : key.StartsWith("remote.", StringComparison.Ordinal) ? 7 : 0;
        int end = key.LastIndexOf('.'); name = prefix != 0 && end > prefix ? key[prefix..end] : "";
        return name.Length != 0;
    }
    private static bool IsRemoteSettingsSection(string key, string expectedName, bool expectedDisabled)
        => TryRemoteSetting(key, out string name, out bool disabled) && name == expectedName && disabled == expectedDisabled;
}

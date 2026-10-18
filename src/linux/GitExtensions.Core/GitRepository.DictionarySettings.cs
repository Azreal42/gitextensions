using System.Collections.Concurrent;
using IOPath = System.IO.Path;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private const string DictionarySettingsFileName = "GitExtensions.settings";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DictionarySettingsLocks = new(StringComparer.Ordinal);

    /// <summary>Reads explicit repository XML overrides over the supplied XDG global value, without any write.</summary>
    public Task<GitDictionarySettingsSnapshot> ReadEffectiveDictionaryAsync(string globalDictionary,
        CancellationToken cancellationToken = default)
    {
        string global = GitDictionaryName.NormalizeDisabledIdentity(globalDictionary);
        return DictionarySettingsOperationAsync(info => ReadDictionarySettingsAsync(info, global, cancellationToken), cancellationToken);
    }

    /// <summary>Writes only the local dictionary key after checking the captured local and distributed files.</summary>
    public Task<GitDictionarySettingsSnapshot> WriteLocalDictionaryAsync(GitDictionarySettingsSnapshot expected,
        string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        string dictionary = GitDictionaryName.Normalize(value);
        return DictionarySettingsOperationAsync(async info =>
        {
            if (info.RootPath != expected.RootPath || info.CommonDirectory != expected.CommonDirectory)
            { throw new ArgumentException("The dictionary settings snapshot belongs to another repository or worktree.", nameof(expected)); }
            GitDictionarySettingsSnapshot current = await ReadDictionarySettingsAsync(info, expected.GlobalDictionary, cancellationToken).ConfigureAwait(false);
            CheckDictionarySettingsFile(expected.Local, current.Local);
            if ((expected.Distributed is null) != (current.Distributed is null)) { throw DictionarySettingsChanged(); }
            if (expected.Distributed is not null) { CheckDictionarySettingsFile(expected.Distributed, current.Distributed!); }
            if (current.LocalDictionary == dictionary) { return current; }

            GitXmlSettingsSnapshot savedFile = await GitXmlSettingsStore.SaveChangesAsync(current.Local.File,
                new Dictionary<string, string?> { ["dictionary"] = dictionary }, cancellationToken).ConfigureAwait(false);
            GitDictionarySettingsFile saved = new(savedFile);
            return new(info, expected.GlobalDictionary, saved, current.Distributed);
        }, cancellationToken);
    }

    private Task<GitDictionarySettingsSnapshot> DictionarySettingsOperationAsync(
        Func<GitRepositoryInfo, Task<GitDictionarySettingsSnapshot>> action, CancellationToken token)
        // XML, path/permission inspection and final serialization never run synchronously on the UI thread.
        => Task.Run(() => WithMutationAsync(async cancellationToken =>
        {
            GitRepositoryInfo info = await GetInfoAsync(cancellationToken).ConfigureAwait(false);
            string common = GitRulesFiles.Resolve(info.CommonDirectory).Path;
            SemaphoreSlim gate = DictionarySettingsLocks.GetOrAdd(common, static _ => new(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await action(info).ConfigureAwait(false); }
            finally { gate.Release(); }
        }, token), CancellationToken.None);

    private static async Task<GitDictionarySettingsSnapshot> ReadDictionarySettingsAsync(GitRepositoryInfo info,
        string global, CancellationToken token)
    {
        GitDictionarySettingsFile local = await ReadDictionarySettingsFileAsync(IOPath.Combine(info.CommonDirectory, DictionarySettingsFileName), token).ConfigureAwait(false);
        GitDictionarySettingsFile? distributed = info.IsBare ? null
            : await ReadDictionarySettingsFileAsync(IOPath.Combine(info.RootPath, DictionarySettingsFileName), token).ConfigureAwait(false);
        return new(info, global, local, distributed);
    }

    private static async Task<GitDictionarySettingsFile> ReadDictionarySettingsFileAsync(string path, CancellationToken token)
        => new(await GitXmlSettingsStore.LoadAsync(path, token).ConfigureAwait(false));

    private static void CheckDictionarySettingsFile(GitDictionarySettingsFile expected, GitDictionarySettingsFile current)
        => GitXmlSettingsStore.CheckSnapshot(expected.File, current.File);

    private static IOException DictionarySettingsChanged()
        => new("The repository dictionary settings, permissions or symbolic-link destination changed. Reload before saving.");
}

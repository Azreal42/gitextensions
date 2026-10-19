namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Windows config fields, including a missing pushurl; no fetch fallback or URL rewriting.</summary>
    public async Task<IReadOnlyList<GitRevisionLinkRemote>> GetRevisionLinkRemotesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<KeyValuePair<string, string>> settings = await GetConfigAsync("^remote\\.", cancellationToken: cancellationToken).ConfigureAwait(false);
        Dictionary<string, (string? Url, string? Push)> remotes = new(StringComparer.Ordinal);
        foreach ((string key, string value) in settings)
        {
            int dot = key.LastIndexOf('.');
            if (dot <= 7) { continue; }
            string name = key[7..dot];
            remotes.TryGetValue(name, out var fields);
            if (key[(dot + 1)..] == "url") { fields.Url = value; }
            if (key[(dot + 1)..] == "pushurl") { fields.Push = value; }
            remotes[name] = fields;
        }
        return remotes.OrderBy(pair => pair.Key).Select(pair => new GitRevisionLinkRemote(pair.Key, pair.Value.Url, pair.Value.Push)).ToArray();
    }

    public async Task<GitRevisionLinkContext> GetRevisionLinkContextAsync(string revision, CancellationToken cancellationToken = default)
    {
        string hash = await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        Task<IReadOnlyList<GitReference>> references = GetReferencesAsync(cancellationToken);
        Task<IReadOnlyList<GitRevisionLinkRemote>> remotes = GetRevisionLinkRemotesAsync(cancellationToken);
        Task<GitCommitDetails> details = GetCommitDetailsAsync(hash, includeNotes: false, cancellationToken);
        await Task.WhenAll(references, remotes, details).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        string[] prefixes = remotes.Result.Select(remote => remote.Name + "/").OrderByDescending(name => name.Length).ToArray();
        GitRevisionLinkReference Project(GitReference reference)
        {
            string name = reference.ShortName;
            if (reference.IsRemote && prefixes.FirstOrDefault(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) is { } prefix)
            { name = name[prefix.Length..]; }
            return new(reference.IsRemote, name);
        }
        return new(hash, details.Result.Message, references.Result.Where(reference => reference.RevisionHash == hash).Select(Project).ToArray(), remotes.Result);
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitUIPluginInterfaces.BuildServerIntegration;

namespace GitExtensions.Linux.Desktop;

/// <summary>An immutable ownership boundary; every model handed to a caller is a fresh draft.</summary>
internal sealed class BuildServerCredentialsSnapshot
{
    private readonly BuildServerCredentials _credentials;
    internal string FilePath { get; }
    internal string Provider { get; }
    internal string Endpoint { get; }
    internal bool Exists { get; }
    internal BuildServerCredentials Credentials => ToDraft();

    internal BuildServerCredentialsSnapshot(string filePath, string provider, string endpoint, bool exists, IBuildServerCredentials? credentials)
    { FilePath = filePath; Provider = provider; Endpoint = endpoint; Exists = exists; _credentials = Copy(credentials); }
    internal BuildServerCredentials ToDraft() => Copy(_credentials);
    internal static BuildServerCredentials Copy(IBuildServerCredentials? value) => new()
    {
        BuildServerCredentialsType = value?.BuildServerCredentialsType ?? BuildServerCredentialsType.Guest,
        Username = value?.Username, Password = value?.Password, BearerToken = value?.BearerToken
    };
    public override string ToString() => "Build server credentials (redacted)";
}

/// <summary>User-only CI credentials, separate from repository/distributed XML and hosting preferences.</summary>
internal sealed class BuildServerCredentialsStore
{
    private const string ProvidersKey = "Providers";
    private const string TypeKey = nameof(BuildServerCredentials.BuildServerCredentialsType);
    private const string ReadError = "Cannot read the local build-server credentials file. Repair it before saving.";
    private const string ConflictError = "Build-server credentials changed in another window. Reopen authentication before saving.";
    private const string WriteError = "Cannot save the local build-server credentials file. Check its permissions and retry.";
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);
    internal string FilePath { get; }

    internal BuildServerCredentialsStore(string? directory = null)
        => FilePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(new PreferencesStore(directory).FilePath)!, "build-server-credentials.json"));

    internal BuildServerCredentialsSnapshot Load(string provider, string endpoint)
    {
        string name = ProviderName(provider), address = NormalizeEndpoint(endpoint);
        return Read(name, address).Snapshot;
    }

    internal BuildServerCredentialsSnapshot Load(string provider, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri) { throw new ArgumentException("Enter an absolute build-server endpoint.", nameof(endpoint)); }
        return Load(provider, endpoint.AbsoluteUri);
    }

    /// <summary>Null is Cancel. An explicit first Guest save remembers that choice; later equal drafts do not rewrite.</summary>
    internal BuildServerCredentialsSnapshot SaveChanges(BuildServerCredentialsSnapshot baseline, IBuildServerCredentials? draft)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (baseline.FilePath != FilePath) { throw new ArgumentException("The authentication draft belongs to another profile.", nameof(baseline)); }
        if (draft is null) { return baseline; }
        BuildServerCredentials captured = BuildServerCredentialsSnapshot.Copy(draft);
        if (!Enum.IsDefined(captured.BuildServerCredentialsType)) { throw new ArgumentException("Choose a supported build-server credential type.", nameof(draft)); }
        lock (Gates.GetOrAdd(FilePath, static _ => new()))
        {
            Document current = Read(baseline.Provider, baseline.Endpoint);
            Merge merge = Prepare(baseline, captured, current.Snapshot);
            if (!merge.Create && merge.Changes.Count == 0) { return current.Snapshot; }
            string directory = Path.GetDirectoryName(FilePath)!;
            string lockPath = FilePath + ".lock", temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            FileStream? held = null;
            try
            {
                if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
                else { Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                // Cooperating processes cannot publish between our compare and rename.
                held = PrivateFile(lockPath);
                current = Read(baseline.Provider, baseline.Endpoint);
                merge = Prepare(baseline, captured, current.Snapshot);
                if (!merge.Create && merge.Changes.Count == 0) { return current.Snapshot; }
                JsonObject providers = Object(current.Root, ProvidersKey), entries = Object(providers, baseline.Provider);
                JsonObject entry = Object(entries, baseline.Endpoint);
                foreach ((string key, string? value) in merge.Create ? Fields(merge.Credentials) : merge.Changes) { entry[key] = value; }
                using (FileStream stream = PrivateFile(temporary))
                {
                    JsonSerializer.Serialize(stream, current.Root, new JsonSerializerOptions { WriteIndented = true });
                    stream.Flush(flushToDisk: true);
                }
                byte[]? latest = ReadBytes();
                if (!Equal(current.Bytes, latest)) { throw new InvalidOperationException(ConflictError); }
                // The external-writer check is optimistic; repository XML and this user file are not a transaction.
                File.Move(temporary, FilePath, overwrite: true);
                return new(FilePath, baseline.Provider, baseline.Endpoint, true, merge.Credentials);
            }
            catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException(WriteError); }
            catch (IOException) { throw new IOException(WriteError); }
            finally
            {
                if (held is not null) { held.Dispose(); File.Delete(lockPath); }
                File.Delete(temporary);
            }
        }
    }

    /// <summary>The identity includes transport, normalized host/port, and the complete instance base path.</summary>
    internal static string NormalizeEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        { throw new ArgumentException("Enter an explicit HTTP or HTTPS build-server endpoint without credentials, query or fragment.", nameof(endpoint)); }
        string address = uri.AbsoluteUri;
        return address.EndsWith('/') ? address : address + "/";
    }

    private Document Read(string provider, string endpoint)
    {
        byte[]? bytes = ReadBytes();
        if (bytes is null) { return new(new(), null, new(FilePath, provider, endpoint, false, null)); }
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes); Unique(json.RootElement);
            JsonElement root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw new JsonException(); }
            JsonElement? found = null;
            if (root.TryGetProperty(ProvidersKey, out JsonElement providers))
            {
                if (providers.ValueKind != JsonValueKind.Object) { throw new JsonException(); }
                if (providers.TryGetProperty(provider, out JsonElement entries))
                {
                    if (entries.ValueKind != JsonValueKind.Object) { throw new JsonException(); }
                    if (entries.TryGetProperty(endpoint, out JsonElement entry))
                    { if (entry.ValueKind != JsonValueKind.Object) { throw new JsonException(); } found = entry; }
                }
            }
            BuildServerCredentials credentials = new();
            if (found is JsonElement value)
            {
                if (value.TryGetProperty(TypeKey, out JsonElement type))
                {
                    if (type.ValueKind != JsonValueKind.String || !Enum.TryParse(type.GetString(), ignoreCase: false, out BuildServerCredentialsType mode)
                        || !Enum.IsDefined(mode)) { throw new JsonException(); }
                    credentials.BuildServerCredentialsType = mode;
                }
                credentials.Username = Text(value, nameof(BuildServerCredentials.Username));
                credentials.Password = Text(value, nameof(BuildServerCredentials.Password));
                credentials.BearerToken = Text(value, nameof(BuildServerCredentials.BearerToken));
            }
            return new(JsonNode.Parse(bytes)!.AsObject(), bytes, new(FilePath, provider, endpoint, found is not null, credentials));
        }
        // Never retain the parser's payload/path-bearing exception, including as an InnerException.
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw new JsonException(ReadError); }
    }

    private byte[]? ReadBytes()
    {
        try { return File.ReadAllBytes(FilePath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException(ReadError); }
        catch (IOException) { throw new IOException(ReadError); }
    }
    private static Merge Prepare(BuildServerCredentialsSnapshot baseline, BuildServerCredentials draft, BuildServerCredentialsSnapshot current)
    {
        Dictionary<string, string?> before = Fields(baseline.Credentials), desired = Fields(draft), actual = Fields(current.Credentials);
        string[] edited = desired.Keys.Where(key => desired[key] != before[key] || !baseline.Exists && key == TypeKey).ToArray();
        if (edited.Length > 0 && baseline.Exists && !current.Exists) { throw new InvalidOperationException(ConflictError); }
        Dictionary<string, string?> changes = new(StringComparer.Ordinal);
        foreach (string key in edited)
        {
            if (actual[key] != before[key] && actual[key] != desired[key]) { throw new InvalidOperationException(ConflictError); }
            if (actual[key] != desired[key]) { changes[key] = desired[key]; actual[key] = desired[key]; }
        }
        BuildServerCredentials merged = new()
        {
            BuildServerCredentialsType = Enum.Parse<BuildServerCredentialsType>(actual[TypeKey]!),
            Username = actual[nameof(BuildServerCredentials.Username)], Password = actual[nameof(BuildServerCredentials.Password)],
            BearerToken = actual[nameof(BuildServerCredentials.BearerToken)]
        };
        return new(!baseline.Exists && !current.Exists, changes, merged);
    }
    private static Dictionary<string, string?> Fields(IBuildServerCredentials credentials) => new(StringComparer.Ordinal)
    {
        [TypeKey] = credentials.BuildServerCredentialsType.ToString(), [nameof(BuildServerCredentials.Username)] = credentials.Username,
        [nameof(BuildServerCredentials.Password)] = credentials.Password, [nameof(BuildServerCredentials.BearerToken)] = credentials.BearerToken
    };
    private static string? Text(JsonElement entry, string key)
    {
        if (!entry.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null) { return null; }
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException();
    }
    private static string ProviderName(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider) || provider.Any(char.IsControl))
        { throw new ArgumentException("Choose an explicit build-server provider.", nameof(provider)); }
        return provider.Trim();
    }
    private static JsonObject Object(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject value) { return value; }
        JsonObject created = new(); parent.Add(key, created); return created;
    }
    private static FileStream PrivateFile(string path)
    {
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        return new(path, options);
    }
    private static bool Equal(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty pair in value.EnumerateObject())
            { if (!names.Add(pair.Name)) { throw new JsonException(); } Unique(pair.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) { foreach (JsonElement item in value.EnumerateArray()) { Unique(item); } }
    }
    public override string ToString() => "Local build-server credentials store";
    private sealed record Document(JsonObject Root, byte[]? Bytes, BuildServerCredentialsSnapshot Snapshot)
    { public override string ToString() => "Credentials document (redacted)"; }
    private sealed record Merge(bool Create, Dictionary<string, string?> Changes, BuildServerCredentials Credentials)
    { public override string ToString() => "Credentials merge (redacted)"; }
}

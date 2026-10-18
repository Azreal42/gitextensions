namespace GitExtensions.Linux.Desktop.Avatars;

internal sealed record AvatarDiskEntry(AvatarPng Image, DateTime LastWriteTime);
internal interface IAvatarCacheWrite : IDisposable { void Commit(); }
internal interface IAvatarDiskCache
{
    Task<AvatarDiskEntry?> ReadAsync(string key, CancellationToken cancellationToken);
    Task<IAvatarCacheWrite> StageAsync(string key, AvatarPng image, CancellationToken cancellationToken);
    void DeleteExpired(string key);
    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>One production namespace, separate from legacy Gource. Construction performs no IO.</summary>
internal sealed class AvatarDiskCache : IAvatarDiskCache
{
    private readonly string _directory;
    internal AvatarDiskCache(string? directory = null)
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        string home = Path.IsPathFullyQualified(xdg ?? "") ? xdg!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        _directory = Path.GetFullPath(directory ?? Path.Combine(home, "gitextensions", "shared-avatars-v1"));
    }

    private string FilePath(string key)
    {
        if (key.Length != 64 || key.Any(character => !char.IsAsciiHexDigit(character)))
        { throw new ArgumentException("Expected a SHA256 avatar key.", nameof(key)); }
        return Path.Combine(_directory, key + ".png");
    }

    public async Task<AvatarDiskEntry?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string path = FilePath(key); FileInfo file = new(path);
            if (!file.Exists || file.Length > AvatarImageTransport.MaximumResponseBytes || file.LinkTarget is not null) { return null; }
            DateTime written = file.LastWriteTime;
            using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
            using MemoryStream output = new(); byte[] buffer = new byte[8192]; int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > AvatarImageTransport.MaximumResponseBytes) { return null; }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            byte[] bytes = output.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return new(new AvatarPng(bytes), written);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public void DeleteExpired(string key) => File.Delete(FilePath(key));

    public async Task<IAvatarCacheWrite> StageAsync(string key, AvatarPng image, CancellationToken cancellationToken)
    {
        string destination = FilePath(key);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        StagedWrite staged = new(temporary, destination);
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(temporary, image.CopyBytes(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); return staged;
        }
        catch { staged.Dispose(); throw; }
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_directory)) { return Task.CompletedTask; }
        List<Exception> errors = [];
        foreach (string path in Directory.EnumerateFiles(_directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Delete(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add(error); }
        }
        if (errors.Count > 0) { throw new AggregateException("Could not clear the shared avatar cache.", errors); }
        return Task.CompletedTask;
    }

    private sealed class StagedWrite(string temporary, string destination) : IAvatarCacheWrite
    {
        public void Commit() => File.Move(temporary, destination, overwrite: true);
        public void Dispose()
        { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
}

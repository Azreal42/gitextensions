using System.Security.Cryptography;
using System.Text;

namespace GitExtensions.Linux.Desktop;

internal sealed record RepositoryBranchMetadata(bool Valid, bool IsBare, string Branch, bool IsDetached, bool BranchLoaded);

/// <summary>Shared repository discovery and optional branch reads, pinned to the caller's Git environment.</summary>
internal sealed class RepositoryBranchMetadataService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _limit = new(4);
    private readonly Dictionary<(string Path, string Context), RepositoryBranchMetadata> _cache = [];
    private readonly Dictionary<(string Path, string Context, bool Branch), Pending> _pending = [];
    private readonly HashSet<Pending> _active = [];
    private bool _disposed;
    private Task? _disposal;

    private sealed class Pending((string Path, string Context, bool Branch) key)
    {
        internal readonly (string Path, string Context, bool Branch) Key = key;
        internal readonly CancellationTokenSource Cancellation = new();
        internal Task<RepositoryBranchMetadata> Task { get; set; } = null!;
    }

    internal static string ContextIdentity(GitExecutionEnvironment execution)
    {
        StringBuilder text = new(execution.Executable);
        foreach ((string name, string? value) in execution.EnvironmentVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        { text.Append('\0').Append(name).Append('\0').Append(value); }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    internal async Task<RepositoryBranchMetadata> ReadAsync(string path, bool includeBranch,
        GitExecutionEnvironment execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(path);
        var key = (Path: fullPath, Context: ContextIdentity(execution), Branch: includeBranch);
        Task<RepositoryBranchMetadata> task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue((key.Path, key.Context), out RepositoryBranchMetadata? cached)
                && (!includeBranch || cached.BranchLoaded || cached.IsBare || !cached.Valid))
            { return Present(cached, includeBranch); }
            if (!_pending.TryGetValue(key, out Pending? pending))
            {
                pending = new(key);
                _pending.Add(key, pending); _active.Add(pending);
                Pending owned = pending;
                // Start under the lock so Task is assigned before a completing worker can retire.
                pending.Task = Task.Run(() => ReadOwnedAsync(owned, execution));
            }
            task = pending.Task;
        }
        return Present(await task.WaitAsync(cancellationToken).ConfigureAwait(false), includeBranch);
    }

    private static RepositoryBranchMetadata Present(RepositoryBranchMetadata metadata, bool includeBranch)
        => includeBranch ? metadata : metadata with { Branch = "", IsDetached = false, BranchLoaded = false };

    private async Task<RepositoryBranchMetadata> ReadOwnedAsync(Pending pending, GitExecutionEnvironment execution)
    {
        CancellationToken token = pending.Cancellation.Token;
        bool entered = false;
        try
        {
            await _limit.WaitAsync(token).ConfigureAwait(false); entered = true;
            RepositoryBranchMetadata metadata = await DiscoverAsync(pending.Key.Path, pending.Key.Branch, execution, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_disposed || !_pending.TryGetValue(pending.Key, out Pending? current) || !ReferenceEquals(current, pending))
                { throw new OperationCanceledException(token); }
                var key = (pending.Key.Path, pending.Key.Context);
                // A validity-only request must not replace a richer branch result completed in parallel.
                if (!_cache.TryGetValue(key, out RepositoryBranchMetadata? previous) || !previous.BranchLoaded || metadata.BranchLoaded)
                { _cache[key] = metadata; }
            }
            return metadata;
        }
        finally
        {
            if (entered) { _limit.Release(); }
            lock (_gate)
            {
                if (_pending.TryGetValue(pending.Key, out Pending? current) && ReferenceEquals(current, pending)) { _pending.Remove(pending.Key); }
                _active.Remove(pending);
            }
            pending.Cancellation.Dispose();
        }
    }

    private static async Task<RepositoryBranchMetadata> DiscoverAsync(string path, bool includeBranch,
        GitExecutionEnvironment execution, CancellationToken token)
    {
        GitRunner runner = execution.CreateRunner();
        GitRepository repository;
        try { repository = await GitRepository.OpenAsync(path, token, runner).ConfigureAwait(false); }
        catch (Exception error) when (error is GitCommandException or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new(false, false, "", false, false); }
        if (!includeBranch || repository.IsBare) { return new(true, repository.IsBare, "", false, false); }
        try
        {
            GitResult result = await runner.RunAsync(repository.Path, ["symbolic-ref", "--quiet", "--short", "HEAD"], token).ConfigureAwait(false);
            if (result.Success) { return new(true, false, result.StandardOutput.TrimEnd('\r', '\n'), false, true); }
            if (result.ExitCode == 1) { return new(true, false, "", true, true); }
        }
        catch (Exception error) when (error is GitCommandException or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { /* Failure of optional branch decoration must not mark a valid repository as missing. */ }
        return new(true, false, "", false, false);
    }

    internal void Invalidate(string? path = null)
    {
        string? fullPath = path is null ? null : Path.GetFullPath(path);
        lock (_gate)
        {
            if (_disposed) { return; }
            foreach (var key in _cache.Keys.Where(key => fullPath is null || key.Path == fullPath).ToArray()) { _cache.Remove(key); }
            foreach (Pending pending in _active.Where(read => fullPath is null || read.Key.Path == fullPath).ToArray())
            { pending.Cancellation.Cancel(); _pending.Remove(pending.Key); }
        }
    }

    internal Task Completion
    {
        get { lock (_gate) { return _disposal ?? DrainAsync(_active.Select(read => read.Task).ToArray()); } }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) { return new(_disposal); }
            _disposed = true;
            foreach (Pending pending in _active) { pending.Cancellation.Cancel(); }
            _pending.Clear(); _cache.Clear();
            _disposal = FinishDisposalAsync(_active.Select(read => read.Task).ToArray());
            return new(_disposal);
        }
    }

    private static async Task DrainAsync(Task[] pending)
    {
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task FinishDisposalAsync(Task[] pending)
    {
        try { await DrainAsync(pending).ConfigureAwait(false); }
        finally { _limit.Dispose(); }
    }
}

using System.Text.RegularExpressions;
using Avalonia.Threading;
using GitCommands.ExternalLinks;

namespace GitExtensions.Linux.Desktop;

internal sealed record RevisionLinksResult(IReadOnlyList<ExternalLink> Links, IReadOnlyList<GitRevisionLinkDiagnostic> Diagnostics, string? Error = null);

/// <summary>Independent related-links read: bounded regex runs off the UI thread; all generations are drained.</summary>
internal sealed class RevisionLinksController : IDisposable
{
    private readonly Func<GitRepository, string, CancellationToken, Task<RevisionLinksResult>> _reader;
    private readonly HashSet<Task> _reads = [];
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private CancellationTokenSource? _current;
    private GitRepository? _repository;
    private string? _hash;
    private long _generation, _attachment;
    private bool _attached, _closing;
    private Task _pending = Task.CompletedTask;
    internal event Action<RevisionLinksResult>? Updated;
    internal Task Completion => Task.WhenAll(_reads.ToArray());
    internal bool IsLoading => _reads.Any(task => !task.IsCompleted);
    internal RevisionLinksController(Func<GitRepository, string, CancellationToken, Task<RevisionLinksResult>>? reader = null)
        => _reader = reader ?? ReadAsync;
    internal void SetRevision(GitRepository? repository, string? hash)
    { Cancel(); _repository = repository; _hash = hash; _paths.Clear(); }
    internal void Cancel() { _generation++; _current?.Cancel(); _current = null; }
    internal void Attach()
    {
        if (_attached || _closing) { return; }
        _attached = true; _attachment++; RevisionLinksSettingsPublication.Saved += Saved;
    }
    internal void Detach()
    {
        if (!_attached) { return; }
        _attached = false; _attachment++; RevisionLinksSettingsPublication.Saved -= Saved; Cancel();
    }
    private void Saved(string path)
    {
        long attachment = _attachment;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing || !_attached || attachment != _attachment || !_paths.Contains(path)) { return; }
            Cancel(); _ = LoadAsync();
        });
    }
    internal Task LoadAsync(CancellationToken token = default)
    {
        if (_closing || _repository is null || _hash is null) { return Task.CompletedTask; }
        if (_current is not null) { return _pending; }
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(token);
        _current = source;
        Task request = LoadCoreAsync(_repository, _hash, _generation, source);
        _pending = request; _reads.Add(request); _ = DrainAsync(request); return request;
    }
    private async Task DrainAsync(Task request)
    { try { await request; } catch (OperationCanceledException) { } finally { _reads.Remove(request); } }
    private async Task LoadCoreAsync(GitRepository repository, string hash, long generation, CancellationTokenSource source)
    {
        try
        {
            GitRepositoryInfo info = await repository.GetInfoAsync(source.Token);
            string[] paths = new[] { DetailedRepositorySettings.GlobalPath, Path.Combine(info.CommonDirectory, GitDistributedSettingsSnapshot.FileName) }
                .Concat(info.IsBare ? [] : new[] { Path.Combine(info.RootPath, GitDistributedSettingsSnapshot.FileName) }).ToArray();
            if (generation == _generation) { _paths.UnionWith(paths); }
            RevisionLinksResult result = await _reader(repository, hash, source.Token);
            if (_closing || generation != _generation || source.IsCancellationRequested) { return; }
            Updated?.Invoke(result);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or GitCommandException or System.Xml.XmlException or ArgumentException)
        { if (!_closing && generation == _generation) { Updated?.Invoke(new([], [], error.Message)); } }
        finally { if (_current == source) { _current = null; } source.Dispose(); }
    }
    private static async Task<RevisionLinksResult> ReadAsync(GitRepository repository, string hash, CancellationToken token)
    {
        GitDistributedSettingsSnapshot snapshot = await DetailedRepositorySettings.LoadAsync(repository, token).ConfigureAwait(false);
        GitRevisionLinkSettings links = await Task.Run(() => GitRevisionLinkSettings.From(snapshot), token).ConfigureAwait(false);
        IReadOnlyList<ExternalLinkDefinition> definitions = links.GetDefinitions();
        if (!definitions.Any(definition => definition.Enabled)) { return new([], links.GetDiagnostics()); }
        GitRevisionLinkContext context = await repository.GetRevisionLinkContextAsync(hash, token).ConfigureAwait(false);
        try
        {
            GitRevisionLinkParseResult result = await Task.Run(() => new GitRevisionLinkParser(TimeSpan.FromMilliseconds(250)).Parse(context, definitions, token), token).ConfigureAwait(false);
            return new(result.DistinctLinks, links.GetDiagnostics().Concat(result.Diagnostics).ToArray());
        }
        catch (RegexMatchTimeoutException error) { return new([], links.GetDiagnostics(), error.Message); }
    }
    internal Task BeginClose()
    { _closing = true; Detach(); Cancel(); return Completion; }
    public void Dispose() { _ = BeginClose(); }
}

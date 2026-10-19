using System.Xml;

namespace GitExtensions.Linux.Desktop;

/// <summary>One accepted XML baseline for all scoped pages in a Settings window.</summary>
internal sealed class DistributedSettingsWorkspace
{
    private readonly GitRepository? _repository;
    private readonly object _gate = new();
    private Task? _initialization;
    internal GitDistributedSettingsSnapshot? Snapshot { get; private set; }
    internal Exception? LoadError { get; private set; }
    internal bool Loaded => Snapshot is not null && LoadError is null;

    internal DistributedSettingsWorkspace(GitRepository? repository) => _repository = repository;

    internal Task InitializeAsync(CancellationToken token)
    {
        lock (_gate) { return _initialization ??= LoadAsync(token); }
    }

    private async Task LoadAsync(CancellationToken token)
    {
        try
        {
            Snapshot = await DetailedRepositorySettings.LoadAsync(_repository, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException
            or InvalidDataException or GitCommandException or ArgumentException or InvalidOperationException)
        { LoadError = error; }
    }

    // Called only after a page's real CAS write/readback, never to merge an external
    // reread into a pending draft. SaveAll invokes pages sequentially on this baseline.
    internal void AcceptSaved(GitDistributedSettingsSnapshot accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        GitDistributedSettingsSnapshot before = Snapshot ?? throw new InvalidOperationException("Settings have not been loaded.");
        static bool SamePath(GitXmlSettingsSnapshot? left, GitXmlSettingsSnapshot? right)
            => left is null ? right is null : right is not null && string.Equals(left.Path, right.Path, StringComparison.Ordinal);
        if (!SamePath(before.Local, accepted.Local) || !SamePath(before.Distributed, accepted.Distributed)
            || !SamePath(before.Global, accepted.Global))
        { throw new InvalidOperationException("The accepted settings belong to another scope or repository."); }
        Snapshot = accepted;
    }
}

namespace GitExtensions.Linux.Desktop;

internal enum MainWindowKind { Browse, RevisionLog, CommitDiff }

/// <summary>FormLog is a dedicated host of the existing history/files/diff controllers, not FormDiff.</summary>
internal static class RevisionLogWindow
{
    internal static MainWindow Create(BrowseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PathFilter is not null || request.SelectedRevision is not null || request.FirstRevision is not null)
        { throw new ArgumentException("The revision Log starts with its own history selection.", nameof(request)); }
        return new(request, MainWindowKind.RevisionLog);
    }
}

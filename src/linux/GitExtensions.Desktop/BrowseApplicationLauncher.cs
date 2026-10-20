using System.Diagnostics;

namespace GitExtensions.Linux.Desktop;

/// <summary>Launches an independent Browse process using the current installation, without shell quoting.</summary>
internal static class BrowseApplicationLauncher
{
    internal static Task OpenAsync(string repositoryPath, CancellationToken token)
        => OpenAsync(repositoryPath, token, AppContext.BaseDirectory);

    internal static Task OpenAsync(string repositoryPath, CancellationToken token, string applicationDirectory)
    {
        token.ThrowIfCancellationRequested();
        if (!LoggedExternalProcess.Start(CreateStartInfo(repositoryPath, applicationDirectory), GitCommandLogProcessKind.Browse, cancellationToken: token).HasProcess)
        { throw new InvalidOperationException("The Git Extensions browser process could not be started."); }
        return Task.CompletedTask;
    }

    internal static Task OpenAsync(BrowseRequest request, CancellationToken token, string? applicationDirectory = null)
    {
        token.ThrowIfCancellationRequested();
        if (!LoggedExternalProcess.Start(CreateStartInfo(request, applicationDirectory), GitCommandLogProcessKind.Browse, cancellationToken: token).HasProcess)
        { throw new InvalidOperationException("The Git Extensions browser process could not be started."); }
        return Task.CompletedTask;
    }

    internal static ProcessStartInfo CreateStartInfo(BrowseRequest request, string? applicationDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        string repositoryPath = request.RepositoryPath ?? throw new ArgumentException("Browse requires an absolute repository path.", nameof(request));
        ProcessStartInfo info = CreateStartInfo(repositoryPath, applicationDirectory);
        // The legacy overload retains its one literal repository argument for existing independent Browse callers.
        info.ArgumentList.RemoveAt(info.ArgumentList.Count - 1); info.ArgumentList.Add("browse"); info.ArgumentList.Add(repositoryPath);
        if (request.PathFilter is { } filter)
        {
            if (filter.Path.Contains('\0')) { throw new ArgumentException("A Browse path cannot contain NUL.", nameof(request)); }
            info.ArgumentList.Add("--pathFilter=" + filter.Path);
            if (filter.Kind is { } kind)
            {
                if (!Enum.IsDefined(kind)) { throw new ArgumentException("Unknown Browse path kind.", nameof(request)); }
                info.ArgumentList.Add("-pathfilter-kind=" + (kind == BrowsePathKind.Directory ? "directory" : "file"));
            }
        }
        if (request.FirstRevision is not null && request.SelectedRevision is null)
        { throw new ArgumentException("The first selected Browse revision requires the last selected revision.", nameof(request)); }
        if (request.SelectedRevision is { } selected)
        {
            string commit = DesktopStartupArguments.FormatBrowseRevision(selected);
            if (request.FirstRevision is { } first) { commit += "," + DesktopStartupArguments.FormatBrowseRevision(first); }
            info.ArgumentList.Add("-commit=" + commit);
        }
        return info;
    }

    internal static ProcessStartInfo CreateStartInfo(string repositoryPath, string? applicationDirectory = null)
    {
        if (!Path.IsPathFullyQualified(repositoryPath)) { throw new ArgumentException("Browse requires an absolute repository path.", nameof(repositoryPath)); }
        string apphost = Path.Combine(applicationDirectory ?? AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "GitExtensions.exe" : "GitExtensions");
        ProcessStartInfo info = new() { UseShellExecute = false, WorkingDirectory = repositoryPath };
        if (File.Exists(apphost)) { info.FileName = apphost; }
        else
        {
            string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            string candidate = string.IsNullOrEmpty(dotnetRoot) ? "" : Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            info.FileName = File.Exists(candidate) ? candidate : "dotnet";
            info.ArgumentList.Add(typeof(Program).Assembly.Location);
        }
        info.ArgumentList.Add(repositoryPath); return info;
    }
}

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
        using Process process = Process.Start(CreateStartInfo(repositoryPath, applicationDirectory))
            ?? throw new InvalidOperationException("The Git Extensions browser process could not be started.");
        return Task.CompletedTask;
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

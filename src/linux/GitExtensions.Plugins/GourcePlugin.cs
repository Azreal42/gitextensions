using System.Diagnostics;

namespace GitExtensions.Linux.Plugins;

public interface IGourceAvatarProvider
{
    Task<byte[]?> GetAvatarPngAsync(string email, string authorName, int imageSize, CancellationToken cancellationToken);
}

public sealed record GourceOptions(string Executable = "gource", string Arguments = "--hide filenames --user-image-dir \"$(AVATARS)\"", string? WorkingDirectory = null);
public sealed record GourceLaunch(int ProcessId, string Executable, string WorkingDirectory, IReadOnlyList<string> Arguments, string? AvatarDirectory);

public sealed class GourcePlugin(IGourceAvatarProvider? avatars = null, string? avatarRoot = null) : INativePlugin
{
    public PluginDescriptor Descriptor { get; } = new(new Guid("F0A6A769-6DCC-4452-9A43-343347015EEC"),
        "Gource", "Launch the configured Linux Gource executable to visualize repository activity.", "IconGource",
        [new("executable", "Path to Gource", PluginParameterKind.Text, "gource", Required: true),
         new("arguments", "Arguments", PluginParameterKind.Text, "--hide filenames --user-image-dir \"$(AVATARS)\""),
         new("workingDirectory", "Working directory (blank for repository)", PluginParameterKind.Text)]);

    public async Task<PluginResult> ExecuteAsync(PluginContext context, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        GourceOptions options = new(PluginValues.Text(Descriptor, values, "executable"), PluginValues.Text(Descriptor, values, "arguments"),
            PluginValues.Text(Descriptor, values, "workingDirectory"));
        GourceLaunch launch = await LaunchAsync(context.Repository, options, cancellationToken).ConfigureAwait(false);
        return new($"Gource started (process {launch.ProcessId}).");
    }

    public async Task<GourceLaunch> LaunchAsync(GitRepository repository, GourceOptions options, CancellationToken cancellationToken = default)
    {
        if (!await repository.IsRepositoryAsync(cancellationToken).ConfigureAwait(false))
        { throw new InvalidOperationException("Gource can only be started from a valid Git repository."); }
        string directory = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? repository.Path : Path.GetFullPath(options.WorkingDirectory);
        string[] arguments = GourceProcess.ParseArguments(options.Arguments);
        string? avatarDirectory = null;
        if (arguments.Any(argument => argument.Contains("$(AVATARS)", StringComparison.Ordinal)))
        {
            avatarDirectory = await ExportAvatarsAsync(repository, cancellationToken).ConfigureAwait(false);
            arguments = arguments.Select(argument => argument.Replace("$(AVATARS)", avatarDirectory, StringComparison.Ordinal)).ToArray();
        }
        Process? process = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessStartInfo info = GourceProcess.StartInfo(options.Executable, directory, arguments);
            Process launched = Process.Start(info) ?? throw new InvalidOperationException("Gource did not start.");
            process = launched;
            int processId = process.Id;
            string? ownedAvatars = avatarDirectory;
            process.Exited += (_, _) =>
            {
                if (ownedAvatars is not null)
                {
                    try { Directory.Delete(ownedAvatars, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                launched.Dispose();
            };
            process.EnableRaisingEvents = true;
            return new(processId, options.Executable, directory, arguments, avatarDirectory);
        }
        catch
        {
            process?.Dispose();
            if (avatarDirectory is not null) { Directory.Delete(avatarDirectory, recursive: true); }
            throw;
        }
    }

    private async Task<string> ExportAvatarsAsync(GitRepository repository, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(avatarRoot ?? Path.Combine(Path.GetTempPath(), "GitExtensions-Gource"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (avatars is null) { return directory; }
            string log = (await repository.RunAsync(["log", "--format=%aE%x00%aN", "--"], cancellationToken).ConfigureAwait(false)).StandardOutput;
            IEnumerable<string[]> authors = log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\0', 2))
                .Where(fields => fields.Length == 2 && !string.IsNullOrWhiteSpace(fields[0]) && !string.IsNullOrWhiteSpace(fields[1]))
                .GroupBy(fields => fields[1], StringComparer.Ordinal).Select(group => group.First());
            using SemaphoreSlim downloads = new(10);
            await Task.WhenAll(authors.Select(ExportAuthorAsync)).ConfigureAwait(false);
            return directory;

            async Task ExportAuthorAsync(string[] author)
            {
                string name = author[1];
                if (name.Contains('/') || name.Contains('\\') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { return; }
                await downloads.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    byte[]? png = await avatars.GetAvatarPngAsync(author[0], name, 90, cancellationToken).ConfigureAwait(false);
                    if (png is not null) { await File.WriteAllBytesAsync(Path.Combine(directory, name + ".png"), png, cancellationToken).ConfigureAwait(false); }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { /* A missing avatar must not prevent Gource from starting. */ }
                finally { downloads.Release(); }
            }
        }
        catch { Directory.Delete(directory, recursive: true); throw; }
    }
}

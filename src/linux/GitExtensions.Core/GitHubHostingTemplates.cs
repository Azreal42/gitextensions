using System.Text;

namespace GitExtensions.Linux;

public static class GitHubHostingTemplates
{
    /// <summary>The original .github/PULL_REQUEST_TEMPLATE.md, literal text, only inside this repository.</summary>
    public static async Task<string> ReadAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = PhysicalPath(repositoryPath);
        DirectoryInfo directory = new(Path.Combine(root, ".github"));
        if (!directory.Exists) { return ""; }
        string actualDirectory = PhysicalPath(directory.FullName);
        if (!Inside(root, actualDirectory)) { throw new IOException("The pull request template must stay inside the repository."); }
        FileInfo file = new(Path.Combine(actualDirectory, "PULL_REQUEST_TEMPLATE.md"));
        if (!file.Exists) { return ""; }
        string path = PhysicalPath(file.FullName);
        if (!Inside(root, path)) { throw new IOException("The pull request template must stay inside the repository."); }
        await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        using MemoryStream buffer = new(); byte[] bytes = new byte[8192]; int count;
        while ((count = await input.ReadAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        { if (buffer.Length + count > 1024 * 1024) { throw new IOException("The pull request template exceeds the supported size."); } buffer.Write(bytes, 0, count); }
        cancellationToken.ThrowIfCancellationRequested();
        buffer.Position = 0; using StreamReader reader = new(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
    private static bool Inside(string root, string path)
    { string relative = Path.GetRelativePath(root, path); return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal); }
    private static string PhysicalPath(string path, int depth = 0)
    {
        if (depth > 40) { throw new IOException("The pull request template contains too many symbolic links."); }
        string full = Path.GetFullPath(path), current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null)
            { current = PhysicalPath((entry.ResolveLinkTarget(returnFinalTarget: true) ?? throw new IOException("The pull request template link could not be resolved.")).FullName, depth + 1); }
        }
        return current;
    }
}

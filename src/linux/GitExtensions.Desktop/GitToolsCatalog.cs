using GitCommands.DiffMergeTools;

namespace GitExtensions.Linux.Desktop;

/// <summary>Upstream command data only. Git remains responsible for running the selected tool.</summary>
internal static class GitToolsCatalog
{
    private static readonly DiffMergeTool[] Tools =
        [new BeyondCompare4(), new DiffMerge(), new Kdiff3(), new Meld(), new P4Merge(), new Smerge(), new VsCode()];

    internal static IEnumerable<string> Names(GitToolRole role)
        => Tools.Where(tool => role == GitToolRole.Diff ? tool.IsDiffTool : tool.IsMergeTool).Select(tool => tool.Name);

    internal static bool IsKnown(GitToolRole role, string name) => Find(role, name) is not null;

    internal static string? Suggest(GitToolRole role, string name, string path)
    {
        DiffMergeTool? tool = Find(role, name);
        if (tool is null) { return null; }
        string executable = path.Length == 0 ? tool.ExeFileName : path;
        string arguments = role == GitToolRole.Diff ? tool.DiffCommand : tool.MergeCommand;
        return QuoteExecutable(executable) + " " + arguments;
    }

    internal static string? PreferredExecutable(GitToolRole role, string name)
    {
        string? executable = Find(role, name)?.ExeFileName;
        if (executable is null) { return null; }
        // This is a picker hint, not a claim that the program is installed or usable.
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(directory => directory.Length > 0)
            .Select(directory => Path.Combine(directory, executable)).FirstOrDefault(File.Exists);
    }

    private static DiffMergeTool? Find(GitToolRole role, string name)
        => Tools.FirstOrDefault(tool => tool.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && (role == GitToolRole.Diff ? tool.IsDiffTool : tool.IsMergeTool));

    private static string QuoteExecutable(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

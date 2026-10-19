namespace GitExtensions.Linux;

public sealed record JenkinsProject(string Id, Uri ProjectUri, bool IsMultiBranch, string? CustomTree);

public static class JenkinsProjects
{
    public const string BuildTree = "number,result,timestamp,url,actions[lastBuiltRevision[SHA1,branch[name]],totalCount,failCount,skipCount],building,duration";
    public static IReadOnlyList<JenkinsProject> Resolve(string value, Uri server, Func<string, string>? expandVariables = null, bool allowVariables = false)
    {
        string text = expandVariables is null ? value : expandVariables(value); List<JenkinsProject> projects = [];
        foreach (string part in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int question = part.IndexOf('?'); string path = question < 0 ? part : part[..question];
            string? selector = question < 0 ? null : part[question..];
            if (selector == "?") { throw Invalid(); }
            if (selector is { Length: > 2048 } || selector?.Any(char.IsControl) == true) { throw Invalid(); }
            if (!allowVariables && (path.Contains('{') || path.Contains('}'))) { throw new ArgumentException("Expand the Jenkins project variables before starting the reader."); }
            string checkedPath = allowVariables ? path.Replace("{cRepoProject}", "project", StringComparison.Ordinal).Replace("{cRepoShortName}", "repo", StringComparison.Ordinal) : path;
            if (checkedPath.Length == 0 || checkedPath.Contains('{') || checkedPath.Contains('}') || checkedPath.Contains('\\') || checkedPath.Contains('#')
                || checkedPath.Split('/').Any(s => s.Length == 0 || s is "." or ".." || s.Any(char.IsControl))) { throw Invalid(); }
            string encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            Uri target = new(server, "job/" + encoded + "/");
            bool multi = selector == "?m"; string? custom = selector is null || multi ? null : selector;
            string id = target.AbsolutePath + (selector ?? "");
            if (!projects.Any(p => p.Id == id)) { projects.Add(new(id, target, multi, custom)); }
        }
        return Array.AsReadOnly(projects.ToArray());
    }
    public static Uri RequestUri(JenkinsProject project, bool full)
    {
        string tree = "lastBuild[timestamp]"; int depth = 1;
        if (project.CustomTree is not null) { tree = project.CustomTree; }
        else if (project.IsMultiBranch) { tree = "jobs[" + tree + (full ? ",builds[" + BuildTree + "]" : "") + "]"; if (full) { depth = 2; } }
        else if (full) { tree += ",builds[" + BuildTree + "]"; }
        return new(project.ProjectUri, "api/json?depth=" + depth + "&tree=" + Uri.EscapeDataString(tree));
    }
    internal static bool SafePath(string value)
    {
        string path = Uri.UnescapeDataString(value);
        return !path.Contains('\\') && !path.Any(char.IsControl) && !path.Split('/').Any(s => s is "." or "..");
    }
    public static bool InServer(Uri server, Uri uri) => uri.IsAbsoluteUri && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.Scheme == server.Scheme && uri.IdnHost == server.IdnHost && uri.Port == server.Port && SafePath(uri.AbsolutePath)
        && Uri.UnescapeDataString(uri.AbsolutePath).StartsWith(Uri.UnescapeDataString(server.AbsolutePath), StringComparison.Ordinal);
    private static ArgumentException Invalid() => new("The Jenkins project path or selector is invalid.");
}

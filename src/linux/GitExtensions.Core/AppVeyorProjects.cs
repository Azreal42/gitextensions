namespace GitExtensions.Linux;

/// <summary>Windows pipe-separated project resolution, independent of persistence and HTTP.</summary>
public static class AppVeyorProjects
{
    public static IReadOnlyList<string> Resolve(string projectNames, string accountName, Func<string, string>? expandVariables = null)
    {
        ArgumentNullException.ThrowIfNull(projectNames); ArgumentNullException.ThrowIfNull(accountName);
        string expanded = expandVariables is null ? projectNames : expandVariables(projectNames);
        List<string> result = [];
        foreach (string project in expanded.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!project.Contains('/') && accountName.Length == 0) { ValidatePart(project); continue; }
            string id = project.Contains('/') ? project : accountName + "/" + project;
            ValidateId(id);
            if (!result.Contains(id, StringComparer.Ordinal)) { result.Add(id); }
        }
        return Array.AsReadOnly(result.ToArray());
    }
    public static void ValidateId(string id)
    {
        string[] parts = id.Split('/');
        if (parts.Length != 2) { throw new ArgumentException("Enter an AppVeyor project as account/project, or a project name with an account."); }
        foreach (string part in parts) { ValidatePart(part); }
    }
    internal static void ValidatePart(string part)
    {
        if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.Any(c => char.IsControl(c) || "/\\?#|%{}".Contains(c)))
        { throw new ArgumentException("The AppVeyor account or project name contains an invalid path component."); }
    }
    internal static string Escaped(string id)
    { ValidateId(id); return string.Join('/', id.Split('/').Select(Uri.EscapeDataString)); }
}

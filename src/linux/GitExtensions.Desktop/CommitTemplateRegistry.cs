namespace GitExtensions.Linux.Desktop;

/// <summary>Application-side template registration. It does not load or execute a Windows plugin.</summary>
public static class CommitTemplateRegistry
{
    private sealed record Registration(string Name, Func<string> Text, string? Icon, bool IsRegex);
    private static readonly object Gate = new();
    private static readonly List<Registration> Items = [];
    public static void Register(string name, Func<string> text, string? iconName = null, bool isRegex = false)
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(text);
        lock (Gate) { if (Items.All(item => item.Name != name)) { Items.Add(new(name, text, iconName, isRegex)); } }
    }
    public static void Unregister(string name) { lock (Gate) { Items.RemoveAll(item => item.Name == name); } }
    internal static IReadOnlyList<(CommitMessageTemplate Template, string? Icon)> Snapshot()
    {
        Registration[] entries; lock (Gate) { entries = Items.ToArray(); }
        // User delegates run outside the registry lock; menu snapshots are private copies.
        return entries.Select(item => (new CommitMessageTemplate(item.Name, item.Text(), item.IsRegex), item.Icon)).ToArray();
    }
}

using System.Globalization;
using System.Xml.Linq;

namespace GitExtensions.Linux;

internal static class TeamCityXml
{
    internal static string Required(XElement e, string name)
    {
        string? value = (string?)e.Attribute(name);
        return string.IsNullOrEmpty(value) ? throw new TeamCityException("The TeamCity XML response is missing a required field.") : value;
    }
    internal static XElement Root(XDocument doc, string name)
    {
        XElement? root = doc.Root;
        if (root?.Name.LocalName.Equals("html", StringComparison.OrdinalIgnoreCase) == true)
        { throw new TeamCityException("TeamCity returned a login page. Configure guest access or explicit credentials; OS sign-in/NTLM is not supported."); }
        if (root?.Name != name) { throw new TeamCityException("The TeamCity API returned an unexpected XML entity."); }
        return root;
    }
    internal static bool Boolean(XElement e, string name)
    {
        string? v = (string?)e.Attribute(name);
        if (v is null) { return false; }
        return bool.TryParse(v, out bool b) ? b : throw new TeamCityException("The TeamCity XML response contains an invalid boolean.");
    }
    internal static TeamCityProject Project(XElement e) => new(Required(e, "id"), Required(e, "name"), (string?)e.Attribute("parentProjectId"), Boolean(e, "archived"));
    internal static TeamCityBuildType BuildType(XElement e) => new(Required(e, "id"), Required(e, "name"), Required(e, "projectId"));
    internal static DateTimeOffset? Date(string? value)
    {
        if (value is null) { return null; }
        // TeamCity offsets are +HHmm; DateTimeOffset's zzz format expects +HH:mm.
        if (value.Length == 20 && value[15] is '+' or '-') { value = value.Insert(18, ":"); }
        if (DateTimeOffset.TryParseExact(value, ["yyyyMMdd'T'HHmmsszzz", "yyyyMMdd'T'HHmmss'Z'"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset date)) { return date; }
        throw new TeamCityException("The TeamCity XML response contains an invalid build date.");
    }
    internal static TeamCityBuild Build(XDocument doc)
    {
        XElement e = Root(doc, "build"); string id = Required(e, "id");
        if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) || number == 0)
        { throw new TeamCityException("The TeamCity XML response contains an invalid build ID."); }
        string type = (string?)e.Attribute("buildTypeId") ?? (string?)e.Element("buildType")?.Attribute("id")
            ?? throw new TeamCityException("The TeamCity XML response has no build configuration ID.");
        bool running = Boolean(e, "running"); string state = (string?)e.Attribute("state") ?? (running ? "running" : "finished");
        string description = (running || state == "running" ? (string?)e.Element("running-info")?.Attribute("currentStageText") : null)
            ?? (string?)e.Element("statusText") ?? (string?)e.Attribute("statusText") ?? (string?)e.Attribute("status") ?? "Unknown build state";
        string[] hashes = (e.Element("revisions")?.Elements("revision") ?? []).Select(x => (string?)x.Attribute("version"))
            .Where(TeamCityBuildSnapshot.IsCommitHash).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(id, type, (string?)e.Attribute("number") ?? "", state, (string?)e.Attribute("status") ?? "", running,
            e.Element("canceledInfo") is not null || Boolean(e, "canceled"), Date((string?)e.Element("startDate")), Date((string?)e.Element("finishDate")),
            description, (string?)e.Attribute("webUrl"), Array.AsReadOnly(hashes));
    }
}

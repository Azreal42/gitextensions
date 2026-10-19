using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace GitCommands.ExternalLinks;

/// <summary>Windows ExternalLinkFormat, with the GitRevision dependency reduced to its already-captured hash.</summary>
[XmlType("GitExtLinkFormat")]
public class ExternalLinkFormat
{
    public string? Caption { get; set; }
    public string? Format { get; set; }
    [XmlIgnore]
    public bool IsValid { get; private set; }

    public ExternalLink Apply(Match? remoteMatch, Match? revisionMatch, string commitHash)
    {
        List<string> groups = [];
        AddGroupsFromMatches(remoteMatch);
        AddGroupsFromMatches(revisionMatch);
        object[] groupsArray = [.. groups];
        string? caption = null;
        string uri;
        try
        {
            caption = string.Format(Caption!, groupsArray);
            ArgumentNullException.ThrowIfNull(Format);
            uri = Format.Replace("%COMMIT_HASH%", commitHash);
            uri = string.Format(uri, groupsArray);
            IsValid = true;
        }
        catch (Exception error)
        {
            // Retain the Windows error-link behavior; the parser also reports a separate diagnostic.
            uri = error.Message + ": " + Format + " " + groupsArray;
            IsValid = false;
        }

        return new ExternalLink(caption, uri);

        void AddGroupsFromMatches(Match? match)
        {
            if (match is not null)
            {
                for (int i = match.Groups.Count > 1 ? 1 : 0; i < match.Groups.Count; i++)
                { groups.Add(match.Groups[i].Value); }
            }
        }
    }
}

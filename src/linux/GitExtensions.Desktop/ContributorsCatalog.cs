using System.Xml;
using System.Xml.Linq;

namespace GitExtensions.Linux.Desktop;

internal sealed record ContributorsData(string Team, string Coders, string Translators, string Designers);

/// <summary>The original inline strings remain owned by GitUI's resource file, linked as raw XML.</summary>
internal static class ContributorsCatalog
{
    internal const string ResourceName = "GitExtensions.Contributors.Windows.xml";
    private static readonly Lazy<ContributorsData> Original = new(() =>
    {
        using Stream stream = typeof(ContributorsCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException("The original Windows contributors resource is unavailable.", ResourceName);
        return Read(stream);
    });

    internal static ContributorsData Load() => Original.Value;

    internal static ContributorsData Read(Stream stream)
    {
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = false });
        HashSet<string> wanted = ["Team", "Coders", "Translators", "Designers"];
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        // Stream past bitmap/file-reference resources; materialize only the four original string nodes.
        while (reader.ReadToFollowing("data"))
        {
            string? name = reader.GetAttribute("name");
            if (name is null || !wanted.Contains(name)) { continue; }
            using XmlReader item = reader.ReadSubtree();
            string value = XElement.Load(item).Element("value")?.Value
                ?? throw new InvalidDataException("Missing contributors resource value: " + name);
            if (!values.TryAdd(name, Flatten(value))) { throw new InvalidDataException("Duplicate contributors resource: " + name); }
        }
        if (values.Count != wanted.Count) { throw new InvalidDataException("The original contributors resource is incomplete."); }
        return new(values["Team"], values["Coders"], values["Translators"], values["Designers"]);
    }

    private static string Flatten(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\n', ' ');
}

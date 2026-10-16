using System.Xml;
using System.Xml.Linq;
using Avalonia.Platform;

namespace GitExtensions.Linux.Desktop;

internal static class OriginalRulesFileContent
{
    internal static string Help(string category)
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/RulesHelp/" + category + ".resx"));
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return XDocument.Load(reader).Root!.Elements("data").Single(element => (string?)element.Attribute("name") == "label1.Text").Element("value")!.Value;
    }

    internal static string[] DefaultIgnorePatterns => Help("FormGitIgnore").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
        .SkipWhile(line => line != "#Ignore thumbnails created by Windows").ToArray();
}

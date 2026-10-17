using System.Globalization;
using System.Xml;

namespace GitExtensions.Linux.Desktop.Localization;

internal sealed record LanguageChoice(string Name, string CultureName);

/// <summary>Windows translation names from primary installed XLF files, paired with their real target culture.</summary>
internal sealed class LanguageCatalog
{
    internal static readonly LanguageChoice English = new("English", "en");
    internal IReadOnlyList<LanguageChoice> Choices { get; }
    internal IReadOnlyList<string> Failures { get; }
    internal string DirectoryPath { get; }
    private LanguageCatalog(string directory, IReadOnlyList<LanguageChoice> choices, IReadOnlyList<string> failures)
    { DirectoryPath = directory; Choices = choices; Failures = failures; }
    internal static LanguageCatalog LoadDefault() => Load(Path.Combine(AppContext.BaseDirectory, "Translation"));
    internal static LanguageCatalog Load(string directory)
    {
        List<LanguageChoice> choices = []; List<string> failures = [];
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.xlf"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains('.') || name.Equals("English", StringComparison.OrdinalIgnoreCase)) { continue; }
                try
                {
                    string? target = null;
                    using XmlReader reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    while (reader.Read())
                    {
                        if (target is null && reader.NodeType == XmlNodeType.Element && reader.LocalName == "file")
                        { target = reader.GetAttribute("target-language"); }
                    }
                    if (string.IsNullOrWhiteSpace(target)) { throw new InvalidDataException("The translation has no target language."); }
                    string culture = CultureInfo.GetCultureInfo(target.Replace('_', '-')).Name;
                    if (culture.Length == 0) { throw new InvalidDataException("The translation target must be a specific language."); }
                    choices.Add(new(name, culture));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or CultureNotFoundException)
                { failures.Add(Path.GetFileName(file) + ": " + error.Message); }
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failures.Add(error.Message); }
        choices.Sort((left, right) => StringComparer.CurrentCulture.Compare(left.Name, right.Name));
        choices.Insert(0, English);
        return new(directory, Array.AsReadOnly(choices.ToArray()), Array.AsReadOnly(failures.ToArray()));
    }
    internal LanguageChoice? Find(string? name) => Choices.FirstOrDefault(choice => choice.Name.Equals(name, StringComparison.Ordinal));
}

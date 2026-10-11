using System.Globalization;
using System.Text;
using System.Xml;
using GitExtensions.Extensibility.Translations.Xliff;

namespace GitExtensions.Linux.Desktop.Localization;

public sealed class TranslationCatalog
{
    private readonly Dictionary<string, string?> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _normalized = new(StringComparer.Ordinal);
    private readonly List<TranslationFile> _catalogues = [];
    private readonly List<string> _loadedFiles = [];
    private readonly List<string> _loadFailures = [];

    private TranslationCatalog(CultureInfo culture)
    {
        CurrentCulture = culture;
    }

    public CultureInfo CurrentCulture { get; }
    public IReadOnlyList<string> LoadedFiles => _loadedFiles;
    public IReadOnlyList<string> LoadFailures => _loadFailures;

    public static TranslationCatalog LoadDefault()
        => Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.CurrentUICulture);

    public static TranslationCatalog Load(string directory, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        TranslationCatalog catalog = new(culture);
        if (!Directory.Exists(directory) || culture.TwoLetterISOLanguageName == "en")
        {
            return catalog;
        }

        List<CultureInfo> cultures = [];
        for (CultureInfo current = culture; current.Name.Length > 0; current = current.Parent)
        {
            cultures.Add(current);
        }

        cultures.Reverse();
        List<(string Path, string Language)> files = [];
        foreach (string file in Directory.EnumerateFiles(directory, "*.xlf").Order(StringComparer.Ordinal))
        {
            try
            {
                string? language = ReadTargetLanguage(file);
                if (!string.IsNullOrEmpty(language))
                {
                    files.Add((file, language.Replace('_', '-')));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
            {
                catalog._loadFailures.Add($"{Path.GetFileName(file)}: {exception.Message}");
            }
        }

        foreach (CultureInfo candidate in cultures)
        {
            foreach ((string file, _) in files.Where(file => file.Language.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    if (TranslationSerializer.Deserialize(file) is TranslationFile translation)
                    {
                        catalog._catalogues.Add(translation);
                        catalog._loadedFiles.Add(file);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or XmlException)
                {
                    catalog._loadFailures.Add($"{Path.GetFileName(file)}: {exception.Message}");
                }
            }
        }

        // Prefer the requested regional language to its parent, while refusing ambiguous contextual translations.
        foreach (IGrouping<string?, TranslationFile> language in catalog._catalogues.GroupBy(file => file.TranslationCategories.FirstOrDefault()?.TargetLanguage))
        {
            Dictionary<string, string?> exact = new(StringComparer.Ordinal);
            Dictionary<string, string?> normalized = new(StringComparer.Ordinal);
            foreach (TranslationItem item in language.SelectMany(file => file.TranslationCategories).SelectMany(category => category.Body.TranslationItems))
            {
                if (string.IsNullOrWhiteSpace(item.Source) || string.IsNullOrWhiteSpace(item.Value))
                {
                    continue;
                }

                string target = Normalize(item.Value);
                AddTranslation(exact, item.Source, target);
                AddTranslation(normalized, Normalize(item.Source), target);
            }

            foreach ((string key, string? value) in exact) { catalog._exact[key] = value; }
            foreach ((string key, string? value) in normalized) { catalog._normalized[key] = value; }
        }

        return catalog;
    }

    public string Translate(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_exact.TryGetValue(source, out string? exact))
        {
            return exact ?? source;
        }

        return _normalized.TryGetValue(Normalize(source), out string? normalized) && normalized is not null
            ? normalized : source;
    }

    public string Translate(string category, string item, string property, string source)
    {
        for (int index = _catalogues.Count - 1; index >= 0; index--)
        {
            TranslationItem? translation = _catalogues[index].GetTranslationCategory(category)?.Body.GetTranslationItem(item, property);
            if (!string.IsNullOrWhiteSpace(translation?.Value))
            {
                return Normalize(translation.Value);
            }
        }

        return source;
    }

    private static void AddTranslation(Dictionary<string, string?> dictionary, string source, string target)
    {
        if (!dictionary.TryGetValue(source, out string? previous))
        {
            dictionary.Add(source, target);
        }
        else if (previous != target)
        {
            dictionary[source] = null;
        }
    }

    private static string? ReadTargetLanguage(string path)
    {
        using XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "file")
            {
                return reader.GetAttribute("target-language");
            }
        }

        return null;
    }

    private static string Normalize(string text)
    {
        StringBuilder result = new(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character != '&' || index + 1 == text.Length || char.IsWhiteSpace(text[index + 1]))
            {
                result.Append(character);
            }
            else if (text[index + 1] == '&')
            {
                result.Append('&');
                index++;
            }
        }

        string normalized = result.ToString();
        return normalized.EndsWith("...", StringComparison.Ordinal) ? normalized[..^3] + "…" : normalized;
    }
}

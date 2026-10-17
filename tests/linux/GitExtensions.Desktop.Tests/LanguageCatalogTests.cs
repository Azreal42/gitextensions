using System.Globalization;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class LanguageCatalogTests
{
    [Test]
    public void InstalledWindowsNamesAreAllDiscoverableWithEnglishFirstAndRealTargets()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Translation");
        LanguageCatalog catalog = LanguageCatalog.Load(directory);
        string[] names = Directory.GetFiles(directory, "*.xlf").Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null && !name.Contains('.') && !name.Equals("English", StringComparison.OrdinalIgnoreCase))
            .Select(name => name!).Order(StringComparer.CurrentCulture).ToArray();
        Assert.That(catalog.Failures, Is.Empty);
        Assert.That(catalog.Choices.Select(choice => choice.Name), Is.EqualTo(new[] { "English" }.Concat(names)));
        Assert.That(catalog.Find("French")?.CultureName, Is.EqualTo("fr"));
        foreach (LanguageChoice choice in catalog.Choices)
        { Assert.That(CultureInfo.GetCultureInfo(choice.CultureName).Name, Is.Not.Empty); }
        TranslationCatalog french = TranslationCatalog.Load(directory, CultureInfo.GetCultureInfo(catalog.Find("French")!.CultureName));
        Assert.That(french.Translate("Cancel"), Is.EqualTo("Annuler"));
        Assert.That(french.LoadedFiles.Select(Path.GetFileName), Does.Contain("French.xlf").And.Contain("French.Plugins.xlf"));
    }

    [TestCase("Portuguese (Brazil)", "pt-BR")]
    [TestCase("Spanish (Argentina)", "es-AR")]
    [TestCase("Simplified Chinese", "zh-Hans")]
    [TestCase("Traditional Chinese", "zh-Hant")]
    public void RegionalAndScriptNamesComeFromTheirInstalledXlfRatherThanAHandwrittenLanguageList(string name, string culture)
    {
        LanguageChoice? choice = LanguageCatalog.LoadDefault().Find(name);
        Assert.That(choice, Is.Not.Null); Assert.That(choice!.CultureName, Is.EqualTo(culture));
        Assert.That(TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(choice.CultureName))
            .LoadedFiles.Select(Path.GetFileName), Does.Contain(name + ".xlf"));
    }

    [Test]
    public void PrimaryFilesAndTargetUnderscoresAreUsedWithoutOfferingSatellitesOrBrokenChoices()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gitextensions-languages-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Literal name.xlf"), Xml("es_AR"));
            File.WriteAllText(Path.Combine(directory, "Literal name.Plugins.xlf"), Xml("es_AR"));
            File.WriteAllText(Path.Combine(directory, "English.xlf"), Xml("en"));
            File.WriteAllText(Path.Combine(directory, "Broken.xlf"), "<xliff><file target-language='fr'><body>");
            File.WriteAllText(Path.Combine(directory, "Unknown.xlf"), Xml("not$$a_culture"));
            File.WriteAllText(Path.Combine(directory, "Missing.xlf"), "<xliff><file><body /></file></xliff>");
            LanguageCatalog catalog = LanguageCatalog.Load(directory);
            Assert.That(catalog.Choices.Select(choice => choice.Name), Is.EqualTo(new[] { "English", "Literal name" }));
            Assert.That(catalog.Find("Literal name")!.CultureName, Is.EqualTo("es-AR"));
            Assert.That(catalog.Failures, Has.Count.EqualTo(3));
            Assert.That(catalog.Find("Literal name.Plugins"), Is.Null);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public void MissingTranslationDirectoryStillOffersTheLiteralEnglishFallback()
    {
        LanguageCatalog catalog = LanguageCatalog.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.That(catalog.Choices, Is.EqualTo(new[] { LanguageCatalog.English })); Assert.That(catalog.Failures, Is.Empty);
    }
    private static string Xml(string target) => "<xliff version='1.2'><file original='Fixture' source-language='en' target-language='" + target + "'><body /></file></xliff>";
}

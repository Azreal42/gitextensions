using System.Globalization;
using GitExtensions.Extensibility.Translations.Xliff;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public class TranslationCatalogTests
{
    private static string CatalogueDirectory => Path.Combine(AppContext.BaseDirectory, "Translation");

    [Test]
    public void DefaultLoaderUsesCurrentSystemUiCulture()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            TranslationCatalog catalogue = TranslationCatalog.LoadDefault();
            Assert.That(catalogue.CurrentCulture.Name, Is.EqualTo("fr-FR"));
            Assert.That(catalogue.Translate("Cancel"), Is.EqualTo("Annuler"));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Test]
    public void FrenchCatalogueAndPluginResourcesAreLoadedFromTheExistingXliffFiles()
    {
        Assert.That(Directory.GetFiles(CatalogueDirectory, "*.xlf"), Has.Length.EqualTo(72));
        TranslationCatalog catalogue = TranslationCatalog.Load(CatalogueDirectory, CultureInfo.GetCultureInfo("fr-FR"));
        Assert.That(catalogue.LoadFailures, Is.Empty);
        Assert.That(catalogue.CurrentCulture.Name, Is.EqualTo("fr-FR"));
        Assert.That(catalogue.LoadedFiles.Select(Path.GetFileName), Is.EquivalentTo(new[] { "French.xlf", "French.Plugins.xlf" }));
        Assert.That(catalogue.Translate("Cancel"), Is.EqualTo("Annuler"));
        Assert.That(catalogue.Translate("Stage selected"), Is.EqualTo("Indexer la sélection"));
        Assert.That(catalogue.Translate("Open…"), Is.EqualTo("Ouvrir…"));
        Assert.That(catalogue.Translate("FormCommit", "Commit", "Text", "Commit"), Is.EqualTo("Commiter"));
    }

    [TestCase("de-DE", "German.xlf")]
    [TestCase("pt-BR", "Portuguese (Brazil).xlf")]
    [TestCase("es-AR", "Spanish (Argentina).xlf")]
    [TestCase("zh-CN", "Simplified Chinese.xlf")]
    [TestCase("zh-TW", "Traditional Chinese.xlf")]
    public void SystemRegionalCultureFindsItsExistingCatalogue(string culture, string expectedFile)
    {
        TranslationCatalog catalogue = TranslationCatalog.Load(CatalogueDirectory, CultureInfo.GetCultureInfo(culture));
        Assert.That(catalogue.LoadedFiles.Select(Path.GetFileName), Does.Contain(expectedFile));
        Assert.That(catalogue.LoadFailures, Is.Empty);
    }

    [Test]
    public void MissingOrEnglishLanguageReturnsTheOriginalEnglishLabel()
    {
        foreach (string culture in new[] { "en-US", "is-IS" })
        {
            TranslationCatalog catalogue = TranslationCatalog.Load(CatalogueDirectory, CultureInfo.GetCultureInfo(culture));
            Assert.That(catalogue.Translate("An unknown Linux operation…"), Is.EqualTo("An unknown Linux operation…"));
            Assert.That(catalogue.Translate("Cancel"), Is.EqualTo("Cancel"));
        }

        TranslationCatalog missing = TranslationCatalog.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), CultureInfo.GetCultureInfo("fr"));
        Assert.That(missing.Translate("Cancel"), Is.EqualTo("Cancel"));
    }

    [Test]
    public void ContextualAccessKeysKeepTheOriginalTranslatedMnemonicAndLeavePlainTranslationsUnchanged()
    {
        TranslationCatalog french = TranslationCatalog.Load(CatalogueDirectory, CultureInfo.GetCultureInfo("fr-FR"));
        Assert.That(french.TranslateAccessKeys("FormCheckoutBranch", "rbMerge", "Text", "&Merge"), Is.EqualTo("_Fusionner"));
        Assert.That(french.TranslateAccessKeys("FormClone", "brachLabel", "Text", "Br&anch:"), Is.EqualTo("Br_anche :"));
        Assert.That(french.Translate("FormCheckoutBranch", "rbMerge", "Text", "&Merge"), Is.EqualTo("Fusionner"));
        TranslationCatalog english = TranslationCatalog.Load(CatalogueDirectory, CultureInfo.GetCultureInfo("en"));
        Assert.That(english.TranslateAccessKeys("FormClone", "repositoryLabel", "Text", "Repository to &clone:"), Is.EqualTo("Repository to _clone:"));
    }

    [Test]
    public void AccessKeyConversionPreservesLiteralAmpersandsUnderscoresAndFallbackText()
    {
        TranslationCatalog missing = TranslationCatalog.Load("/nonexistent-catalogue", CultureInfo.GetCultureInfo("en"));
        Assert.That(missing.TranslateAccessKeys("Missing", "item", "Text", "&Open_file && directory..."), Is.EqualTo("_Open__file & directory…"));
        Assert.That(missing.TranslateAccessKeys("Missing", "item", "Text", "A & B &"), Is.EqualTo("A & B &"));
    }

    [Test]
    public void AmbiguousEmptyAndBrokenTranslationsFallBackToEnglish()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gitextensions-localization-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TranslationFile translation = new("en", "fr");
            TranslationCategory category = new("One", "en", "fr");
            category.Body.TranslationItems.Add(new("empty", "Text", "Missing translation", ""));
            category.Body.TranslationItems.Add(new("first", "Text", "Ambiguous", "Un sens"));
            category.Body.TranslationItems.Add(new("second", "Text", "Ambiguous", "Un autre sens"));
            category.Body.TranslationItems.Add(new("escaped", "Text", "Commit && Push...", "Commiter && Pousser..."));
            translation.AddTranslationCategory(category);
            TranslationSerializer.Serialize(translation, Path.Combine(directory, "French.xlf"));
            File.WriteAllText(Path.Combine(directory, "Broken.xlf"), "<xliff><file target-language=\"fr\"><body>");
            TranslationCatalog catalogue = TranslationCatalog.Load(directory, CultureInfo.GetCultureInfo("fr"));

            Assert.That(catalogue.Translate("Missing translation"), Is.EqualTo("Missing translation"));
            Assert.That(catalogue.Translate("Ambiguous"), Is.EqualTo("Ambiguous"));
            Assert.That(catalogue.Translate("One", "empty", "Text", "Missing translation"), Is.EqualTo("Missing translation"));
            Assert.That(catalogue.Translate("Commit & Push…"), Is.EqualTo("Commiter & Pousser…"));
            Assert.That(catalogue.LoadFailures, Has.Count.EqualTo(1));
            Assert.That(catalogue.LoadFailures[0], Does.StartWith("Broken.xlf:"));
        }
        finally { Directory.Delete(directory, true); }
    }
}

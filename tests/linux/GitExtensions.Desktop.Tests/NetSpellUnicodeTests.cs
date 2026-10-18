using GitExtensions.Linux.Spelling;
using NetSpell.SpellChecker.Dictionary;
using NetSpell.SpellChecker.Dictionary.Affix;
using NetSpell.SpellChecker.Dictionary.Phonetic;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class NetSpellUnicodeTests
{
    // These invoke the actual linked NetSpell code. Before the proposed portable
    // bounds guards they fail with IndexOutOfRangeException, without a copied engine.
    [TestCase("add-prefix")]
    [TestCase("add-suffix")]
    [TestCase("remove-prefix")]
    [TestCase("remove-suffix")]
    public void ACharacterOutsideTheAffixTableDoesNotMatchTheRule(string operation)
    {
        AffixEntry entry = new() { AddCharacters = operation.EndsWith("prefix", StringComparison.Ordinal) ? "pre" : "s" };
        AffixUtility.EncodeConditions("a", entry);
        AffixRule rule = new(); rule.AffixEntries.Add(entry);
        string result = operation switch
        {
            "add-prefix" => AffixUtility.AddPrefix("漢", rule),
            "add-suffix" => AffixUtility.AddSuffix("漢", rule),
            "remove-prefix" => AffixUtility.RemovePrefix("pre漢", entry),
            _ => AffixUtility.RemoveSuffix("漢s", entry)
        };
        Assert.That(result, Is.EqualTo(operation.StartsWith("add", StringComparison.Ordinal) ? "漢" : operation == "remove-prefix" ? "pre漢" : "漢s"));
        Assert.That(AffixUtility.AddSuffix("a", new() { AffixEntries = { new() { AddCharacters = "s" } } }), Is.EqualTo("as"));
    }

    [Test]
    public void OutsidePhoneticTableSkipsOnlyThatRuleAndStillProcessesTheFollowingAsciiCharacter()
    {
        using WordDictionary dictionary = new();
        PhoneticRule rule = new() { ReplaceString = "T" };
        PhoneticUtility.EncodeRule("A", ref rule); dictionary.PhoneticRules.Add(rule);
        Assert.That(dictionary.PhoneticCode("ła"), Is.EqualTo("T"));
        Assert.That(dictionary.PhoneticCode("ł"), Is.Empty);
    }

    [Test]
    public async Task MixedUnicodeTextRemainsCheckedAndUnknownCharactersAreNotMarkedCorrect()
    {
        string root = Path.Combine(Path.GetTempPath(), "ge-spelling-unicode-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using SpellCheckService service = new(DictionaryCatalog.FromApplicationDirectory(AppContext.BaseDirectory), "en-US", Path.Combine(root, "user.dic"));
            SpellCheckResult result = await service.CheckAsync("hello 漢s wrld ł");
            Assert.That(result.Misspellings.Select(span => span.Word), Is.EqualTo(new[] { "漢s", "wrld", "ł" }));
            SpellSuggestionsResult suggestions = await service.SuggestAsync("ł");
            Assert.That(suggestions.Word, Is.EqualTo("ł"));
            Assert.That(suggestions.Suggestions.Length, Is.LessThanOrEqualTo(5));
            Assert.That((await service.CheckAsync("wrld")).Misspellings.Single().Word, Is.EqualTo("wrld"));
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }
}

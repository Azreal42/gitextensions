#nullable enable
using System.Collections.Immutable;
using System.Text;
using NetSpell.SpellChecker;
using NetSpell.SpellChecker.Dictionary;

namespace GitExtensions.Linux.Spelling;

/// <summary>One original NetSpell engine; called only by its owner's serialized worker.</summary>
internal sealed class NetSpellEngine : IDisposable
{
    private readonly WordDictionary _dictionary;
    private readonly NetSpell.SpellChecker.Spelling _spelling;
    internal string Name { get; }

    private NetSpellEngine(string name, WordDictionary dictionary)
    {
        Name = name;
        _dictionary = dictionary;
        _spelling = new() { Dictionary = dictionary, ShowDialog = false, IgnoreAllCapsWords = true, IgnoreWordsWithDigits = true, MaxSuggestions = 5 };
    }

    internal static NetSpellEngine Load(SpellDictionary resource, CancellationToken token)
    {
        // Validate encoding/format without maintaining a second parser. NetSpell's
        // original Initialize below remains the authority for affixes/phonetics.
        bool wordsSection = false;
        using (FileStream input = new(resource.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (StreamReader reader = new(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true))
        {
            while (reader.ReadLine() is { } line)
            { token.ThrowIfCancellationRequested(); wordsSection |= line.Trim() == "[Words]"; }
        }
        if (!wordsSection) { throw new InvalidDataException($"'{resource.FilePath}' is not a NetSpell dictionary: missing [Words]."); }
        token.ThrowIfCancellationRequested();
        WordDictionary dictionary = new()
        {
            DictionaryFolder = Path.GetDirectoryName(resource.FilePath)!,
            DictionaryFile = Path.GetFileName(resource.FilePath), EnableUserFile = false
        };
        try
        {
            // This original synchronous parser is a real owned worker. A canceled
            // consumer may return earlier, but CloseCompletion must drain it.
            dictionary.Initialize();
            token.ThrowIfCancellationRequested();
            if (dictionary.BaseWords.Count == 0) { throw new InvalidDataException($"'{resource.FilePath}' has no dictionary words."); }
            return new(resource.Name, dictionary);
        }
        catch (Exception error)
        {
            dictionary.Dispose();
            if (error is ArgumentException or IndexOutOfRangeException or NullReferenceException or FormatException)
            { throw new InvalidDataException($"Invalid NetSpell data in '{resource.FilePath}'.", error); }
            throw;
        }
    }

    internal void SetUserWords(ImmutableArray<string> words)
    {
        _dictionary.UserWords.Clear();
        foreach (string word in words) { _dictionary.UserWords.Add(word, word); }
    }

    internal SpellCheckResult Check(string text, CancellationToken token)
    {
        var spans = ImmutableArray.CreateBuilder<SpellingSpan>();
        void Misspelled(object? sender, SpellingEventArgs args)
        { token.ThrowIfCancellationRequested(); spans.Add(new(args.TextIndex, args.Word.Length, args.Word)); }
        _spelling.MisspelledWord += Misspelled;
        try
        {
            _spelling.Text = text;
            token.ThrowIfCancellationRequested();
            _spelling.SpellCheck();
            token.ThrowIfCancellationRequested();
            return new(Name, text, spans.ToImmutable());
        }
        finally { _spelling.MisspelledWord -= Misspelled; _spelling.Text = ""; }
    }

    internal SpellSuggestionsResult Suggest(string word, int maximum, CancellationToken token)
    {
        try
        {
            _spelling.MaxSuggestions = maximum;
            token.ThrowIfCancellationRequested();
            _spelling.Suggest(word);
            token.ThrowIfCancellationRequested();
            return new(Name, word, _spelling.Suggestions.ToImmutableArray());
        }
        finally { _spelling.Text = ""; _spelling.MaxSuggestions = 5; }
    }

    public void Dispose() { _spelling.Dispose(); _dictionary.Dispose(); }
}

#nullable enable
using System.Collections.Immutable;

namespace GitExtensions.Linux.Spelling;

/// <summary>UTF-16 positions in the exact immutable Text of the check result.</summary>
public readonly record struct SpellingSpan(int Start, int Length, string Word)
{
    public int End => checked(Start + Length);
}

public sealed record SpellCheckResult(string DictionaryName, string Text, ImmutableArray<SpellingSpan> Misspellings);
public sealed record SpellSuggestionsResult(string DictionaryName, string Word, ImmutableArray<string> Suggestions);

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace GitCommands.Patches;

// Only the encoding boundary used by the unchanged Windows PatchProcessor.
// Keep the Latin1 byte round-trip and Git's UTF-8 path encoding separate from FilesEncoding.
internal static partial class GitModule
{
    [GeneratedRegex(@"(\\(?<octal>[0-7]{3}))+", RegexOptions.ExplicitCapture)]
    private static partial Regex EscapedOctalCodePointRegex { get; }

    [return: NotNullIfNotNull(nameof(value))]
    internal static string? ReEncodeStringFromLossless(string? value, Encoding? encoding)
        => value is null || encoding is null || encoding.WebName == Encoding.Latin1.WebName
            ? value : encoding.GetString(Encoding.Latin1.GetBytes(value));

    [return: NotNullIfNotNull(nameof(value))]
    internal static string? ReEncodeFileNameFromLossless(string? value)
        => UnescapeOctalCodePoints(ReEncodeStringFromLossless(value, SystemEncoding));

    [return: NotNullIfNotNull(nameof(value))]
    internal static string? UnescapeOctalCodePoints(string? value)
        => value is null ? null : EscapedOctalCodePointRegex.Replace(value, match =>
        {
            try
            {
                return SystemEncoding.GetString([.. match.Groups["octal"].Captures.Cast<Capture>()
                    .Select(capture => Convert.ToByte(capture.Value, 8))]);
            }
            catch (OverflowException) { return match.Value; }
        });
}

// Debug-only dependency of the linked parser, without Windows debugger/settings side effects.
internal static class DebugHelpers
{
    [Conditional("DEBUG")]
    internal static void Assert([DoesNotReturnIf(false)] bool condition, string message) => Debug.Assert(condition, message);
}

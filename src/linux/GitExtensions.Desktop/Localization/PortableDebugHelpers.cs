using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace GitExtensions.Extensibility;

// TranslationBody only needs this assertion; the Windows helper also starts a debugger and reads Application.ExecutablePath.
internal static class DebugHelpers
{
    [Conditional("DEBUG")]
    public static void Assert([DoesNotReturnIf(false)] bool condition, string message)
        => Debug.Assert(condition, message);
}

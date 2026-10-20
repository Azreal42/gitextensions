using System.Text.Json;

namespace GitExtensions.Linux.Desktop.Controls;

/// <summary>Each command window captures the global choice without altering its local Show/Hide state.</summary>
internal static class OriginalCommandHelpVisibility
{
    internal static bool Capture()
    {
        try { return !new PreferencesStore().Load().DontShowHelpImages; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // A cosmetic preference must not prevent opening a Git command. Leave an unreadable
            // profile untouched; Advanced already reports its read error before an explicit edit.
            return true;
        }
    }
}

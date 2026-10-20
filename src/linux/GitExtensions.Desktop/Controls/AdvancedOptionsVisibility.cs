using System.Text.Json;

namespace GitExtensions.Linux.Desktop.Controls;

/// <summary>Each new window or popup captures the current presentation preference once.</summary>
internal static class AdvancedOptionsVisibility
{
    internal static bool Capture()
    {
        try { return new PreferencesStore().Load().AlwaysShowAdvancedOptions; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Opening a Git command must remain possible. Preserve the unreadable profile;
            // Advanced reports the error and prevents an edit from silently replacing it.
            return false;
        }
    }
}

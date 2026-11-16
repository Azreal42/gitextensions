using GitExtensions.Extensibility.Translations;

namespace GitExtensions.Linux.Desktop.Localization;

/// <summary>
///  Declares Avalonia-only strings for the shared English catalogue generator.
/// </summary>
internal static class AvaloniaTranslationItems
{
    internal const string ReleaseCandidateToolTip = "Linux alpha and beta packages are also pre-releases.";

    /// <summary>
    ///  Adds strings that cannot be discovered by reflecting over WinForms controls.
    /// </summary>
    internal static void AddTranslationItems(ITranslation translation)
    {
        translation.AddTranslationItem("AdvancedSettingsPage", "chkCheckForRCVersions", "LinuxToolTipText", ReleaseCandidateToolTip);
    }
}

using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class BackgroundFetchSettingsPage(BackgroundFetchSettingsSession session, TranslationCatalog translations)
    : PluginScopedSettingsPage(session, translations)
{
}


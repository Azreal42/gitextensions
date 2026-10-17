using System.Globalization;
using Avalonia.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>UI language only: never changes the OS formatting culture or starts Git to resolve a language.</summary>
internal static class DesktopLanguage
{
    internal static void ApplyPreferredCulture(PreferencesStore? store = null, LanguageCatalog? catalog = null)
    {
        string? explicitCulture = Environment.GetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE");
        CultureInfo? culture = null;
        if (!string.IsNullOrWhiteSpace(explicitCulture))
        {
            try { culture = CultureInfo.GetCultureInfo(explicitCulture); }
            catch (CultureNotFoundException) { }
        }
        if (culture is null)
        {
            string? preference = ReadPreferences(store ?? new())?.Language;
            if (!string.IsNullOrEmpty(preference))
            {
                // A removed translation keeps its saved identity and uses English until the user chooses again.
                culture = CultureInfo.GetCultureInfo((catalog ?? LanguageCatalog.LoadDefault()).Find(preference)?.CultureName ?? "en");
            }
        }
        if (culture is not null)
        {
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        GitRunner.AskpassUiCulture = CultureInfo.CurrentUICulture.Name;
    }

    private static DesktopPreferences? ReadPreferences(PreferencesStore store)
    {
        try { return store.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return null; }
    }

    internal static bool NeedsChoice(DesktopStartupArguments arguments, DesktopPreferences preferences)
        => !arguments.SmokeTest && string.IsNullOrEmpty(preferences.Language);

    /// <summary>The only interactive language gate. Protocol helpers and informational CLI modes never call it.</summary>
    internal static Window CreateInitialWindow(DesktopStartupRequest request, Action<Window> continueStartup,
        PreferencesStore? store = null, LanguageCatalog? catalog = null, Func<DesktopStartupRequest, Window>? createWindow = null)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(continueStartup);
        store ??= new(); catalog ??= LanguageCatalog.LoadDefault(); createWindow ??= App.CreateStartupWindow;
        ApplyPreferredCulture(store, catalog);
        DesktopPreferences? preferences = ReadPreferences(store);
        // MainWindow already presents a preference diagnostic. Do not turn that recoverable
        // read failure into a fatal pre-Avalonia exit, or overwrite it through a first-run chooser.
        if (preferences is null || !NeedsChoice(request.Arguments, preferences)) { return createWindow(request); }
        ChooseLanguageDialog chooser = new(catalog, store) { RequestedThemeVariant = AskpassApplication.ReadPreferredTheme() };
        chooser.Closed += (_, _) =>
        {
            if (!chooser.WasSaved) { return; }
            ApplyPreferredCulture(store, catalog);
            continueStartup(createWindow(request));
        };
        return chooser;
    }
}

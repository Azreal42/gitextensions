using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using GitExtensions.Linux.Desktop.Avatars;

namespace GitExtensions.Linux.Desktop;

public sealed class App : Application
{
    private readonly bool _productionStartup;
    internal AvatarApplicationLifetime? ExistingAvatarRuntime { get; private set; }

    // The headless test builder also constructs App. Only Program opts into production startup.
    public App() : this(false) { }
    internal App(bool productionStartup) => _productionStartup = productionStartup;

    internal static AvatarApplicationLifetime? PeekAvatarRuntime()
        => (Current as App)?.ExistingAvatarRuntime;

    internal static void BindExistingAvatarRuntime(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        PeekAvatarRuntime()?.BindWindow(window);
    }

    public override void Initialize()
    {
        DesktopFonts.Initialize();
        Styles.Add(new FluentTheme());
        Styles.Add(ClassicStyles.Create());
        Styles.Add(Controls.ClassicMenuAppearance.Create());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shutdown = Program.StartupRequest.Arguments.Mode == DesktopStartupMode.Cleanup
                ? Avalonia.Controls.ShutdownMode.OnMainWindowClose : desktop.ShutdownMode;
            // The chooser can close before the real main window is constructed.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = DesktopLanguage.CreateInitialWindow(Program.StartupRequest, window =>
            {
                desktop.MainWindow = window; window.Show(); desktop.ShutdownMode = shutdown;
            }, createWindow: CreateDesktopStartupWindow);
            if (desktop.MainWindow is not ChooseLanguageDialog) { desktop.ShutdownMode = shutdown; }
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal Window CreateDesktopStartupWindow(DesktopStartupRequest request)
    {
        Window window = CreateStartupWindow(request);
        if (!_productionStartup || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime) { return window; }
        if (ExistingAvatarRuntime is null)
        {
            DesktopPreferences saved;
            try { saved = new PreferencesStore().Load(); }
            catch (Exception error) when (error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                // Keep Main's existing preference diagnostic and recoverable startup behavior.
                // An unreadable profile is never repaired or overwritten by the avatar runtime.
                saved = new();
            }
            ExistingAvatarRuntime = new(new AvatarService(AvatarPreferences.From(saved)));
        }
        ExistingAvatarRuntime.BindWindow(window);
        if (window is MainWindow browse && !request.Arguments.SmokeTest) { browse.EnableAutomaticUpdates(); }
        return window;
    }

    internal static Window CreateStartupWindow(DesktopStartupRequest request)
    {
        DesktopLanguage.ApplyPreferredCulture();
        return DesktopStartupDispatcher.CreateMainWindow(request);
    }
}

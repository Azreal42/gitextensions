using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace GitExtensions.Linux.Desktop;

public sealed class App : Application
{
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
            });
            if (desktop.MainWindow is not ChooseLanguageDialog) { desktop.ShutdownMode = shutdown; }
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static Avalonia.Controls.Window CreateStartupWindow(DesktopStartupRequest request)
    {
        DesktopLanguage.ApplyPreferredCulture();
        return DesktopStartupDispatcher.CreateMainWindow(request);
    }
}

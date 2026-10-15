using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace GitExtensions.Linux.Desktop;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(ClassicStyles.Create());
        Styles.Add(Controls.ClassicMenuAppearance.Create());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Program.StartupRequest.Arguments.Mode == DesktopStartupMode.Cleanup)
            {
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            }
            desktop.MainWindow = CreateStartupWindow(Program.StartupRequest);
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static Avalonia.Controls.Window CreateStartupWindow(DesktopStartupRequest request)
        => DesktopStartupDispatcher.CreateMainWindow(request);
}

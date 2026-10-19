using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original commandline information window, with the public Linux dispatcher's real usage.</summary>
internal sealed class CommandlineHelpDialog : Window
{
    internal CommandlineHelpDialog(TranslationCatalog? translations = null)
    {
        TranslationCatalog text = translations ?? TranslationCatalog.LoadDefault();
        Name = "CommandlineHelpDialog"; Title = text.Translate("FormCommandlineHelp", "$this", "Text", "Commandline usage");
        Width = MinWidth = 394; Height = MinHeight = 662; CanMaximize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        using Stream icon = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/git-extensions-logo.ico")); Icon = new WindowIcon(icon);
        TextBlock header = new()
        {
            Name = "CommandlineHelpHeader", Text = text.Translate("Supported commandline arguments for") + "\ngitextensions:", TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(13, 10, 13, 10)
        };
        TextBox commands = new()
        {
            Name = "CommandlineHelpCommands", Text = string.Join('\n', Program.PublicCommandlineForms),
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(13, 0, 13, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(commands, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(commands, ScrollBarVisibility.Auto);
        DockPanel panel = new() { LastChildFill = true }; DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header); panel.Children.Add(commands); Content = panel;
        Opened += (_, _) =>
        {
            commands.Focus(); commands.SelectionStart = commands.SelectionEnd = 0;
            ModalWindowActivation.AfterMapping(this, () => IsVisible);
        };
        AddHandler(KeyDownEvent, (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } }, RoutingStrategies.Tunnel);
    }

    internal static async Task ShowAsync(Window owner, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(owner); token.ThrowIfCancellationRequested();
        if (!owner.IsVisible) { return; }
        CommandlineHelpDialog dialog = new() { RequestedThemeVariant = owner.ActualThemeVariant, ShowInTaskbar = false };
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) { dialog.Close(); } }));
        try { token.ThrowIfCancellationRequested(); await dialog.ShowDialog(owner); token.ThrowIfCancellationRequested(); }
        finally { dialog.Close(); }
    }
}

/// <summary>Informational startup only: no repository discovery, environment checklist, chooser or avatar runtime.</summary>
internal sealed class CommandlineHelpApplication : Application
{
    public override void Initialize()
    {
        DesktopFonts.Initialize(); Styles.Add(new FluentTheme()); Styles.Add(ClassicStyles.Create());
        Styles.Add(Controls.ClassicMenuAppearance.Create()); RequestedThemeVariant = AskpassApplication.ReadPreferredTheme();
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new CommandlineHelpDialog { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        }
        base.OnFrameworkInitializationCompleted();
    }
    internal static int Run()
    {
        DesktopLanguage.ApplyPreferredCulture();
        using LinuxClipboardProtocol? clipboard = LinuxClipboardProtocol.TryOpen();
        return AppBuilder.Configure(() => new CommandlineHelpApplication()).UsePlatformDetect().WithInterFont()
            .LogToTrace().StartWithClassicDesktopLifetime([]);
    }
}

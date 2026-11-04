using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The child editor Git invokes for its generated todo or commit message. Cancellation returns failure to Git.</summary>
public sealed class NativeRebaseEditorApplication : Application
{
    private static GitRebaseEditorRequest? _request;
    private NativeRebaseEditorWindow? _window;
    public override void Initialize()
    {
        ModalWindowActivation.Initialize();
        DesktopFonts.Initialize();
        Styles.Add(new FluentTheme()); Styles.Add(ClassicStyles.Create());
        RequestedThemeVariant = AskpassApplication.ReadPreferredTheme();
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && _request is not null)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _window = new(_request);
            _window.Closed += (_, _) => desktop.Shutdown(_window.Saved ? 0 : 1);
            desktop.MainWindow = _window;
        }
        base.OnFrameworkInitializationCompleted();
    }
    internal static int Run(string[] arguments)
    {
        try
        {
            _request = GitRebaseEditor.ReadNativeEditorRequest(arguments);
            AskpassApplication.ApplyUiCulture();
            return AppBuilder.Configure<NativeRebaseEditorApplication>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([]);
        }
        catch (Exception error)
        { Console.Error.WriteLine("Unable to open the rebase editor: " + error.Message); return 1; }
        finally { _request = null; }
    }
}

/// <summary>A raw editor: every Git command, argument and comment is kept until the user explicitly changes it.</summary>
internal sealed class NativeRebaseEditorWindow : Window
{
    private readonly GitRebaseEditorRequest _request;
    private readonly TextBox _text = new() { Name = "NativeRebaseEditorText", AcceptsReturn = true, AcceptsTab = true,
        TextWrapping = TextWrapping.NoWrap, MinHeight = 260,
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock _error = new() { Name = "NativeRebaseEditorError", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    internal bool Saved { get; private set; }
    internal int ExitCode => Saved ? 0 : 1;

    internal NativeRebaseEditorWindow(GitRebaseEditorRequest request)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        Name = "NativeRebaseEditor"; Title = translations.Translate(request.Kind == GitRebaseEditorKind.Todo ? "Edit rebase todo" : "Edit commit message");
        Width = 900; Height = 640; MinWidth = 600; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        DesktopFonts.Apply(_text, request.Kind == GitRebaseEditorKind.Todo ? DesktopFontRole.Code : DesktopFontRole.Commit);
        _text.SetCurrentValue(TextBox.TextProperty, request.Text);
        Button save = new() { Name = "SaveNativeRebaseEditor", Content = translations.Translate("Save"), IsDefault = true, MinWidth = 75 };
        save.Click += (_, _) => Save();
        Button cancel = new() { Name = "CancelNativeRebaseEditor", Content = translations.Translate("Cancel"), IsCancel = true, MinWidth = 75 };
        cancel.Click += (_, _) => Close();
        DockPanel panel = new() { Margin = new Thickness(10), LastChildFill = true };
        StackPanel header = new() { Spacing = 4, Margin = new Thickness(0, 0, 0, 8), Children =
        {
            new TextBlock { Text = request.RepositoryPath, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = Path.GetFileName(request.FilePath) + " · " + request.EncodingName },
            new TextBlock { Text = request.Kind == GitRebaseEditorKind.Todo
                ? translations.Translate("Edit Git's generated rebase commands. Save to continue; Cancel stops the editor without saving.")
                : translations.Translate("Edit the complete commit message. Save to continue; Cancel stops the editor without saving."), TextWrapping = TextWrapping.Wrap }
        } };
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        StackPanel footer = new() { Spacing = 5, Children = { _error, ExportDialogControls.Buttons(cancel, save) } };
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer); panel.Children.Add(_text); Content = panel;
        Opened += (_, _) => { _text.Focus(); _text.SelectionStart = _text.SelectionEnd = 0; };
    }
    private void Save()
    {
        try
        {
            GitRebaseEditor.WriteNativeEditorRequest(_request, _text.Text ?? "");
            Saved = true; Close();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or FormatException or System.Text.Json.JsonException)
        { _error.Text = error.Message; _error.IsVisible = true; _text.Focus(); }
    }
}

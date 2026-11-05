using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaTerminal;
using GitExtensions.Linux.Desktop.Controls;
using Porta.Pty;

namespace GitExtensions.Linux.Desktop;

/// <summary>The session belongs to the repository/window, not to a tab's visual attachment.</summary>
internal sealed class EmbeddedTerminalPane : Grid, IAsyncDisposable
{
    private readonly EmbeddedTerminalControl _terminal = new() { Name = "EmbeddedTerminal" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly MenuItem _restart, _external, _showOutput;
    private readonly TextBox _output;
    private readonly Border _frame, _notice;
    private readonly Button _noticeAction;
    private readonly SolidColorBrush[] _palette = Enumerable.Range(0, 16).Select(_ => new SolidColorBrush()).ToArray();
    private readonly Func<string, string> _translate;
    private ConsoleStylePreferences _appearance = new();
    private string? _noticeText;
    private bool _offerRestart;
    private TerminalControlModel _model = null!;
    private EmbeddedTerminalSession? _session;
    private string? _directory;
    private GitExecutionEnvironment? _environment;
    private CancellationTokenSource? _cancellation;
    private Task _completion = Task.CompletedTask, _retirement = Task.CompletedTask;
    private TaskCompletionSource? _ready;
    private int _generation;
    private bool _disposed;

    internal Func<PtyOptions, CancellationToken, Task<EmbeddedTerminalSession>> StartSessionAsync { get; set; } = EmbeddedTerminalSession.StartAsync;
    internal TerminalControl Terminal => _terminal;
    internal Task Completion => _completion;
    internal int? ProcessId => _session?.ProcessId;

    internal EmbeddedTerminalPane(TextBox commandOutput, Func<Task> openExternal, Func<string, string> translate)
    {
        _translate = translate;
        _output = commandOutput;
        _terminal.InputError += message => SetNotice(message);
        RowDefinitions = new RowDefinitions("*,Auto");
        _output.IsVisible = false;
        _output.BorderThickness = new Thickness(0);
        _output.Padding = new Thickness(6);
        _frame = ClassicChrome.Border(_terminal, new Thickness(1), new Thickness(6, 4));
        _frame.Name = "ConsoleTerminalFrame";
        Children.Add(new Grid { Children = { _frame, _output } });

        _noticeAction = new Button { Name = "ConsoleNoticeAction", Margin = new Thickness(12, 0, 0, 0) };
        _noticeAction.Click += async (_, _) =>
        {
            if (_output.IsVisible) { ShowOutput(false); }
            else { await RestartAsync(); }
        };
        DockPanel notice = new() { LastChildFill = true };
        DockPanel.SetDock(_noticeAction, Dock.Right); notice.Children.Add(_noticeAction); notice.Children.Add(_status);
        _notice = ClassicChrome.Border(notice, new Thickness(0, 1, 0, 0), new Thickness(6, 3));
        _notice.Name = "ConsoleNotice";
        Grid.SetRow(_notice, 1); Children.Add(_notice);

        KeyModifiers clipboard = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control | KeyModifiers.Shift;
        MenuItem copy = Action("CopyTerminalSelection", "Copy", "CopyToClipboard.png", async () =>
        {
            if (_output.IsVisible && TopLevel.GetTopLevel(this)?.Clipboard is { } target) { await target.SetTextAsync(_output.SelectedText); }
            else { await _terminal.CopySelectionAsync(); }
        });
        copy.InputGesture = new KeyGesture(Key.C, clipboard);
        MenuItem paste = Action("PasteIntoTerminal", "Paste", null, _terminal.PasteClipboardTextAsync);
        paste.InputGesture = new KeyGesture(Key.V, clipboard);
        MenuItem selectAll = Action("SelectTerminalOutput", "Select all", null, () =>
        {
            if (_output.IsVisible) { _output.SelectAll(); } else { _terminal.SelectAll(); }
            return Task.CompletedTask;
        });
        _restart = Action("NewTerminalSession", "New session", "ReloadRevisions.png", RestartAsync);
        _external = Action("OpenExternalTerminal", "Open terminal", "Console.png", openExternal);
        _restart.IsEnabled = _external.IsEnabled = false;
        _showOutput = new MenuItem { Name = "ShowCommandOutput", Header = translate("Command output"), ToggleType = MenuItemToggleType.CheckBox };
        _showOutput.Click += (_, _) => ShowOutput(_showOutput.IsChecked);
        ContextMenu menu = new() { ItemsSource = new Control[] { copy, paste, selectAll, new Separator(), _restart, _external, new Separator(), _showOutput } };
        // Opened also runs for the renderer's explicit Open call; Opening only runs for routed context requests.
        menu.Opened += (_, _) =>
        {
            copy.IsEnabled = _output.IsVisible ? !string.IsNullOrEmpty(_output.SelectedText) : _terminal.HasSelection;
            copy.InputGesture = new KeyGesture(Key.C, _output.IsVisible && !OperatingSystem.IsMacOS() ? KeyModifiers.Control : clipboard);
            paste.IsEnabled = !_output.IsVisible && _session is not null;
        };
        ContextMenu = menu;
        _terminal.ContextRequested += (_, _) => menu.Open(this);
        _terminal.MenuRequested += () => menu.Open(this);

        for (int i = 0; i < _palette.Length; i++) { _terminal.Resources["AvaloniaTerminalColor" + i] = _palette[i]; }
        ActualThemeVariantChanged += (_, _) => ApplyAppearance();
        ResetModel();
        ApplyAppearance();
        ConsoleStylePresentation.Bind(this, appearance => { _appearance = appearance; ApplyAppearance(); });
        SetNotice(translate("Open a repository to begin"));

        MenuItem Action(string name, string text, string? icon, Func<Task> action)
        {
            MenuItem item = new() { Name = name, Header = translate(text), Icon = icon is null ? null : OriginalMenuItems.Icon(icon) };
            item.Click += async (_, _) =>
            {
                try { await action(); }
                catch (Exception error) { SetNotice(error.Message); }
            };
            return item;
        }
    }

    private async Task RestartAsync()
    {
        ShowOutput(false); ResetSession(); await EnsureStartedAsync(); _terminal.Focus();
    }

    private void ShowOutput(bool show)
    {
        _showOutput.IsChecked = show; _output.IsVisible = show; _frame.IsVisible = !show;
        UpdateNotice();
        if (show) { _output.Focus(); } else { _terminal.Focus(); }
    }

    private void SetNotice(string? message, bool offerRestart = false)
    {
        _noticeText = message; _offerRestart = offerRestart;
        UpdateNotice();
    }

    private void UpdateNotice()
    {
        _notice.IsVisible = _output.IsVisible || !string.IsNullOrEmpty(_noticeText);
        _status.Text = _output.IsVisible ? _translate("Command output") : _noticeText;
        _noticeAction.IsVisible = _output.IsVisible || _offerRestart;
        _noticeAction.Content = _translate(_output.IsVisible ? "Back to terminal" : "New session");
    }

    internal void SetRepository(GitRepository? repository)
    {
        _environment = repository?.ExecutionEnvironment;
        string? directory = repository?.Path;
        if (_directory == directory) { return; }
        _directory = directory;
        ResetSession();
        _restart.IsEnabled = _external.IsEnabled = directory is not null;
        SetNotice(directory is null ? _translate("Open a repository to begin") : null);
    }

    internal Task EnsureStartedAsync()
    {
        if (_disposed || _directory is null || _environment is null) { return Task.CompletedTask; }
        if (_ready is not null) { return _ready.Task; }
        _cancellation = new(); _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = RunSessionAsync(_generation, _directory, _environment, _model, _ready, _cancellation.Token);
        return _ready.Task;
    }

    internal void FocusTerminal() => ShowOutput(false);

    private async Task RunSessionAsync(int generation, string directory, GitExecutionEnvironment environment,
        TerminalControlModel model, TaskCompletionSource ready, CancellationToken cancellation)
    {
        bool Current() => !_disposed && generation == _generation && !cancellation.IsCancellationRequested;
        try
        {
            await _retirement;
            cancellation.ThrowIfCancellationRequested();
            SetNotice(_translate("Starting terminal…"));
            PtyOptions options = EmbeddedTerminalSession.CreateOptions(directory, environment, model.Terminal.Cols, model.Terminal.Rows);
            await using EmbeddedTerminalSession session = await StartSessionAsync(options, cancellation);
            cancellation.ThrowIfCancellationRequested();
            _session = session;
            session.Resize(model.Terminal.Cols, model.Terminal.Rows);
            SetNotice(null);
            ready.TrySetResult();
            int exit = await session.RunAsync(async (text, token) =>
            {
                await Dispatcher.UIThread.InvokeAsync(() => { if (Current()) { model.Feed(text); } }, DispatcherPriority.Background, token);
            }, cancellation);
            if (Current())
            {
                SetNotice(_translate("Session ended") + " (" + exit + ")", offerRestart: true);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (Current()) { SetNotice(_translate("Unable to start or continue the terminal") + ": " + error.Message, offerRestart: true); }
        }
        finally
        {
            if (generation == _generation) { _session = null; }
            ready.TrySetResult();
        }
    }

    private void ResetSession()
    {
        _generation++;
        _session = null;
        CancellationTokenSource? previous = _cancellation;
        previous?.Cancel(); _cancellation = null;
        _retirement = RetireAsync(Task.WhenAll(_retirement, _completion), previous);
        _completion = Task.CompletedTask; _ready = null;
        ResetModel();
    }

    private static async Task RetireAsync(Task completion, CancellationTokenSource? source)
    {
        try { await completion; }
        finally { source?.Dispose(); }
    }

    private void ResetModel()
    {
        _model = new(new TerminalOptions { Scrollback = 5000, Cols = 100, Rows = 24, TermName = "xterm-256color" });
        // macOS Option is used to type characters on many keyboard layouts.
        _model.OptionAsMetaKey = !OperatingSystem.IsMacOS();
        _model.UserInput += bytes => _session?.Send(bytes);
        _model.Terminal.Engine.DataReceived += (_, args) => _session?.Send(Encoding.UTF8.GetBytes(args.Data));
        _model.SizeChanged += (columns, rows, _, _) =>
        {
            try { _session?.Resize(columns, rows); }
            catch (Exception error) { SetNotice(error.Message); }
        };
        _terminal.Model = _model;
    }

    private void ApplyAppearance()
    {
        ConsoleStylePreferences style = _appearance;
        ResolvedDesktopFont font = style.Font is null ? DesktopFonts.For(DesktopFontRole.Monospace)
            : DesktopFontResolver.Resolve(style.Font, DesktopFontRole.Monospace);
        _terminal.FontFamily = font.Family.ToString(); _terminal.FontSize = font.SizeInDips;
        bool light = style.Style == "Light" || style.Style == "Default" && ActualThemeVariant != ThemeVariant.Dark;
        string[] colors = light
            ? ["#ffffff", "#a31515", "#397300", "#795e00", "#0451a5", "#af00db", "#007b80", "#555555",
               "#767676", "#c72e2e", "#3b7a00", "#9b6500", "#0066bf", "#a34b91", "#007a7a", "#1e1e1e"]
            : ["#0c0c0c", "#cd3131", "#0dbc79", "#e5e510", "#3b8eea", "#bc3fbc", "#11a8cd", "#cccccc",
               "#767676", "#f14c4c", "#23d18b", "#f5f543", "#3b8eea", "#d670d6", "#29b8db", "#cccccc"];
        // Keep the brushes themselves: the renderer caches formatted text using these brush instances.
        for (int i = 0; i < colors.Length; i++) { _palette[i].Color = Color.Parse(colors[i]); }
        _frame.Background = _palette[0];
        _terminal.CaretBrush = _palette[15];
        _terminal.SelectionBrush = new SolidColorBrush(Color.Parse(light ? "#663399ff" : "#6680bfff"));
        _model.UpdateDisplay();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true; ResetSession();
        await _retirement;
    }
}

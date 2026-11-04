using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaTerminal;
using Porta.Pty;

namespace GitExtensions.Linux.Desktop;

/// <summary>The session belongs to the repository/window, not to a tab's visual attachment.</summary>
internal sealed class EmbeddedTerminalPane : Grid, IAsyncDisposable
{
    private readonly EmbeddedTerminalControl _terminal = new() { Name = "EmbeddedTerminal", Margin = new Thickness(4) };
    private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _restart;
    private readonly ToggleButton _showOutput;
    private readonly Func<string, string> _translate;
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
        _terminal.InputError += message => _status.Text = message;
        RowDefinitions = new RowDefinitions("Auto,*");
        _restart = new Button { Content = translate("New session"), Name = "NewTerminalSession", IsEnabled = false };
        _restart.Click += async (_, _) => { ResetSession(); await EnsureStartedAsync(); _terminal.Focus(); };
        Button external = new() { Content = translate("Open terminal"), Name = "OpenExternalTerminal" };
        external.Click += async (_, _) =>
        {
            try { await openExternal(); }
            catch (Exception error) { _status.Text = error.Message; }
        };
        ToggleButton log = _showOutput = new() { Content = translate("Command output"), Name = "ShowCommandOutput" };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _restart, external, log } };
        DockPanel header = new() { Margin = new Thickness(8, 6), LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Left); header.Children.Add(buttons);
        _status.Margin = new Thickness(12, 0, 0, 0); header.Children.Add(_status); Children.Add(header);
        commandOutput.IsVisible = false;
        Grid body = new() { Children = { _terminal, commandOutput } };
        Grid.SetRow(body, 1); Children.Add(body);
        log.IsCheckedChanged += (_, _) =>
        {
            commandOutput.IsVisible = log.IsChecked == true;
            _terminal.IsVisible = !commandOutput.IsVisible;
            if (_terminal.IsVisible) { _terminal.Focus(); }
        };
        AttachedToVisualTree += (_, _) => ApplyAppearance();
        ActualThemeVariantChanged += (_, _) => ApplyAppearance();
        ResetModel();
        _status.Text = translate("Open a repository to begin");
    }

    internal void SetRepository(GitRepository? repository)
    {
        _environment = repository?.ExecutionEnvironment;
        string? directory = repository?.Path;
        if (_directory == directory) { return; }
        _directory = directory;
        ResetSession();
        _restart.IsEnabled = directory is not null;
        _status.Text = directory ?? _translate("Open a repository to begin");
    }

    internal Task EnsureStartedAsync()
    {
        if (_disposed || _directory is null || _environment is null) { return Task.CompletedTask; }
        if (_ready is not null) { return _ready.Task; }
        _cancellation = new(); _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = RunSessionAsync(_generation, _directory, _environment, _model, _ready, _cancellation.Token);
        return _ready.Task;
    }

    internal void FocusTerminal() { _showOutput.IsChecked = false; _terminal.Focus(); }

    private async Task RunSessionAsync(int generation, string directory, GitExecutionEnvironment environment,
        TerminalControlModel model, TaskCompletionSource ready, CancellationToken cancellation)
    {
        bool Current() => !_disposed && generation == _generation && !cancellation.IsCancellationRequested;
        try
        {
            await _retirement;
            cancellation.ThrowIfCancellationRequested();
            _status.Text = _translate("Starting terminal…");
            PtyOptions options = EmbeddedTerminalSession.CreateOptions(directory, environment, model.Terminal.Cols, model.Terminal.Rows);
            await using EmbeddedTerminalSession session = await StartSessionAsync(options, cancellation);
            cancellation.ThrowIfCancellationRequested();
            _session = session;
            session.Resize(model.Terminal.Cols, model.Terminal.Rows);
            _status.Text = Path.GetFileName(options.App) + " — " + directory;
            ready.TrySetResult();
            int exit = await session.RunAsync(async (text, token) =>
            {
                await Dispatcher.UIThread.InvokeAsync(() => { if (Current()) { model.Feed(text); } }, DispatcherPriority.Background, token);
            }, cancellation);
            if (Current())
            {
                _status.Text = _translate("Session ended") + " (" + exit + ")";
                model.Feed("\r\n[" + _translate("Session ended. Use New session to start again.") + "]\r\n");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (Current()) { _status.Text = _translate("Unable to start or continue the terminal") + ": " + error.Message; }
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
            catch (Exception error) { _status.Text = error.Message; }
        };
        _terminal.Model = _model;
    }

    private void ApplyAppearance()
    {
        ConsoleStylePreferences style;
        try { style = new ConsoleStylePreferencesStore().Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { style = new(); }
        ResolvedDesktopFont font = style.Font is null ? DesktopFonts.For(DesktopFontRole.Monospace)
            : DesktopFontResolver.Resolve(style.Font, DesktopFontRole.Monospace);
        _terminal.FontFamily = font.Family.ToString(); _terminal.FontSize = font.SizeInDips;
        bool light = style.Style == "Light" || style.Style == "Default" && ActualThemeVariant != ThemeVariant.Dark;
        _terminal.Resources["AvaloniaTerminalColor0"] = light ? Brushes.White : new SolidColorBrush(Color.Parse("#0c0c0c"));
        _terminal.Resources["AvaloniaTerminalColor15"] = light ? Brushes.Black : new SolidColorBrush(Color.Parse("#cccccc"));
        _terminal.CaretBrush = light ? Brushes.Black : Brushes.White;
        _model.UpdateDisplay();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true; ResetSession();
        await _retirement;
    }
}

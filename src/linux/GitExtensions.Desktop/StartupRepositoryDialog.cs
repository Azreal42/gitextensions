using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

public abstract class StartupRepositoryDialog : Window
{
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Control> _inputs = [];
    private CancellationTokenSource? _operation;
    private bool _closeWhenIdle;
    private readonly Border _statusBorder;
    protected readonly DockPanel Root = new() { LastChildFill = true };
    protected readonly TextBlock Status = new() { Name = "StartupStatus", TextWrapping = TextWrapping.Wrap };
    public GitRepository? CreatedRepository { get; private set; }
    public string? LastError { get; private set; }
    internal Exception? LastException { get; private set; }
    internal bool IsBusy { get; private set; }

    protected StartupRepositoryDialog(string title)
    {
        Title = T(title);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        Content = Root;
        _statusBorder = ClassicChrome.Border(Status, new Thickness(0, 1, 0, 0), new Thickness(10, 5));
        _statusBorder.IsVisible = false;
        DockPanel.SetDock(_statusBorder, Dock.Bottom);
        Root.Children.Add(_statusBorder);
        Closing += (_, args) =>
        {
            if (IsBusy) { args.Cancel = true; _closeWhenIdle = true; _operation?.Cancel(); }
        };
        Closed += (_, _) => _lifetime.Cancel();
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
    }

    protected string T(string text) => _translations.Translate(text);
    protected void Track(params Control[] inputs) => _inputs.AddRange(inputs);
    protected Button Action(string name, string text, Func<Task> action)
    {
        bool cancel = name.StartsWith("Cancel", StringComparison.Ordinal);
        Button button = new() { Name = name, Content = T(text), MinWidth = 75, Height = 25, IsCancel = cancel, IsDefault = name is "CloneRepository" or "CreateRepository" };
        if (!cancel) { Track(button); }
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception exception) { LastException = exception; LastError = exception.Message; Status.Text = exception.Message; _statusBorder.IsVisible = true; }
        };
        return button;
    }

    protected async Task RunAsync(string label, Func<CancellationToken, Task> action)
    {
        if (IsBusy) { return; }
        IsBusy = true;
        LastError = null;
        LastException = null;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        foreach (Control input in _inputs) { input.IsEnabled = false; }
        Status.Text = T(label) + "…";
        _statusBorder.IsVisible = true;
        try { await action(_operation.Token); }
        catch (OperationCanceledException) { LastError = T("Operation cancelled"); Status.Text = LastError; }
        catch (Exception exception) { LastException = exception; LastError = exception.Message; Status.Text = exception.Message; }
        finally
        {
            _operation.Dispose();
            _operation = null;
            IsBusy = false;
            _statusBorder.IsVisible = LastError is not null;
            foreach (Control input in _inputs) { input.IsEnabled = true; }
            UpdateActions();
            if (_closeWhenIdle) { Close(); }
        }
    }

    protected void Complete(GitRepository repository) { CreatedRepository = repository; _closeWhenIdle = true; }
    protected virtual void UpdateActions() { }
    protected static string AbsoluteDirectory(string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) { throw new ArgumentException("Destination folder must be an absolute path."); }
        if (File.Exists(path)) { throw new ArgumentException("Cannot initialize a new repository on a file. Please choose a directory."); }
        return Path.GetFullPath(path);
    }
}

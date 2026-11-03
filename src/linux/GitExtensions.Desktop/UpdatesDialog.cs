using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The compact Windows Updates form; downloads a verified native package or portable archive.</summary>
internal sealed class UpdatesDialog : Window
{
    private readonly ILinuxUpdateClient _client;
    private readonly TranslationCatalog _text;
    private readonly LinuxReleaseVersion _current;
    private readonly Func<string, Task> _open;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _label;
    private readonly ProgressBar _progress;
    private readonly CheckBox _prereleases;
    private readonly Button _download, _direct, _log;
    private LinuxUpdateRelease? _release;
    private string? _package;
    private Func<Task>? _retryOperation;
    private Task _pending = Task.CompletedTask;
    private bool _closed, _busy, _failed;
    internal Task Retirement { get; private set; } = Task.CompletedTask;
    internal Task PendingOperation => _pending;

    internal UpdatesDialog(ILinuxUpdateClient client, string currentVersion, Func<string, Task> open, TranslationCatalog? translations = null, bool? includePrereleases = null, LinuxUpdateSearch? initialSearch = null)
    {
        _client = client; _text = translations ?? TranslationCatalog.LoadDefault(); _open = open;
        _current = LinuxReleaseVersion.Parse(currentVersion) ?? throw new ArgumentException("The installed release version is invalid.", nameof(currentVersion));
        Name = "UpdatesDialog"; Title = T("$this", "Check for update");
        Width = MinWidth = 462; MinHeight = 135; SizeToContent = SizeToContent.Height;
        CanResize = CanMaximize = CanMinimize = false; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _label = new() { Name = "UpdateLabel", Text = T("UpdateLabel", "Searching for updates"), TextWrapping = TextWrapping.Wrap };
        _progress = new() { Name = "progressBar1", Height = 20, MinHeight = 20, IsIndeterminate = true, Margin = new Thickness(0, 3, 0, 0) };
        _prereleases = new() { Name = "IncludePrereleases", Content = Access("_includePrereleases", "Include pre-releases"), IsChecked = includePrereleases ?? _current.IsPrerelease, Margin = new Thickness(0, 5, 0, 0) };
        _prereleases.IsCheckedChanged += (_, _) => { if (IsVisible && !_busy && !_closed) { Start(SearchAsync); } };
        _log = Link("linkChangeLog", "Show release notes and change &log");
        _direct = Link("linkDirectDownload", "&Direct Download");
        _download = new() { Name = "btnUpdateNow", Content = Access("_downloadPackage", "Download &update..."), MinWidth = 100, MinHeight = 25, IsVisible = false, IsDefault = true };
        _download.Click += (_, _) => Start(_failed && _retryOperation is not null ? _retryOperation : DownloadOrOpenAsync);
        _log.Click += (_, _) => { if (_release is { } release) { Start(() => OpenReleaseAsync(release.ReleaseUri.AbsoluteUri)); } };
        _direct.Click += (_, _) => { if (_release is { } release) { Start(() => OpenReleaseAsync(release.DownloadUri.AbsoluteUri)); } };
        StackPanel body = new() { Name = "tlpnlContent", Margin = new Thickness(12), MinHeight = 79, Children = { _label, _progress, _prereleases, _log } };
        StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { _download, _direct } };
        Border footer = ClassicChrome.Border(actions, new Thickness(0, 1, 0, 0), new Thickness(12, 4));
        footer.Name = "ControlsPanel"; footer.MinHeight = 32; footer.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ClassicButtonBrush"));
        Grid layout = new() { RowDefinitions = new("*,Auto") }; layout.Children.Add(body); Grid.SetRow(footer, 1); layout.Children.Add(footer); Content = layout;
        Opened += (_, _) =>
        {
            ModalWindowActivation.AfterMapping(this, () => IsVisible && !_closed);
            if (initialSearch is null) { Start(SearchAsync); }
            else { PresentSearch(initialSearch); Refresh(); }
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); Retirement = RetireAsync(); };
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; Close(); }
            else if (args.KeyModifiers == KeyModifiers.Alt && !_busy)
            {
                if (args.Key == Key.L && _log.IsVisible) { args.Handled = true; _log.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                else if (args.Key == Key.D && _direct.IsVisible) { args.Handled = true; _direct.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            }
        }, RoutingStrategies.Tunnel);
    }

    private void Start(Func<Task> operation)
    { if (_closed || _busy) { return; } _pending = RunAsync(operation); }

    private async Task RunAsync(Func<Task> operation)
    {
        _busy = true; _failed = false; _retryOperation = null; Refresh();
        try { await operation(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closed)
            {
                _failed = true; _retryOperation = operation; _label.Text = T("_checkFailed", "The update operation failed. You can retry.");
                await Dialogs.ShowErrorAsync(this, error.Message);
            }
        }
        finally { _busy = false; if (!_closed) { Refresh(); } }
    }

    private async Task SearchAsync()
    {
        _release = null; _package = null; _failed = false;
        _label.Text = T("UpdateLabel", "Searching for updates"); _progress.IsIndeterminate = true; Refresh();
        LinuxUpdateSearch result = await _client.FindAsync(_current, _prereleases.IsChecked == true, _lifetime.Token);
        if (_closed || _lifetime.IsCancellationRequested) { return; }
        PresentSearch(result);
    }

    private void PresentSearch(LinuxUpdateSearch result)
    {
        _release = result.Release;
        if (_release is not null) { RestoreReleaseLabel(); }
        else { _label.Text = result.HasCompatiblePublications ? T("_noUpdatesFound", "No updates found")
            : OperatingSystem.IsLinux() ? T("_noLinuxRelease", "No compatible Linux package has been published on the fork releases.")
            : T("_noNativeRelease", "No compatible package for this system has been published on the fork releases."); }
    }

    private async Task DownloadOrOpenAsync()
    {
        if (_package is not null) { await OpenReleaseAsync(_package); return; }
        if (_release is not { Sha256: not null } release) { return; }
        _failed = false; _label.Text = T("_downloadingUpdate", "Downloading update...");
        _progress.IsIndeterminate = false; _progress.Value = 0; Refresh();
        // Post only a progress value. A completed/closed dialog cannot be updated by a queued callback.
        Progress<int> progress = new(value => Dispatcher.UIThread.Post(() =>
        { if (!_closed && _busy && _release == release && _package is null) { _progress.Value = value; } }));
        string package = await _client.DownloadAsync(release, progress, _lifetime.Token);
        if (_closed || _lifetime.IsCancellationRequested) { return; }
        _package = package; RestoreReleaseLabel();
    }

    private async Task OpenReleaseAsync(string target)
    { await _open(target); if (!_closed) { RestoreReleaseLabel(); } }

    private void RestoreReleaseLabel()
    {
        if (_package is not null)
        { _label.Text = string.Format(_text.CurrentCulture, OperatingSystem.IsLinux()
            ? T("_downloaded", "Package downloaded to {0}. Open it with your desktop package installer to install the update.")
            : T("_archiveDownloaded", "Archive downloaded to {0}. Extract it and replace the application to install the update."), _package); }
        else if (_release is { } release)
        { _label.Text = string.Format(_text.CurrentCulture, T("_newVersionAvailable", "There is a new version {0} of Git Extensions available"), release.Version.Text)
            + (release.Sha256 is null ? "\n" + T("_unverified", "Verification information is unavailable. Use Direct Download.") : ""); }
    }

    private void Refresh()
    {
        _progress.IsVisible = _busy && (_release is null || _package is null);
        _prereleases.IsEnabled = !_busy;
        _log.IsVisible = _direct.IsVisible = _release is not null; _log.IsEnabled = _direct.IsEnabled = !_busy;
        _download.IsVisible = _release?.Sha256 is not null || _failed;
        _download.IsEnabled = !_busy;
        _download.Content = _failed ? Access("_retry", "&Retry") : _package is not null ? Access("_openPackage", "&Open package...") : Access("_downloadPackage", "Download &update...");
    }

    private async Task RetireAsync()
    { try { await _pending; } finally { _lifetime.Dispose(); } }
    private string T(string name, string fallback) => _text.Translate("FormUpdates", name, "Text", fallback);
    private AccessText Access(string name, string fallback) => new() { Text = _text.TranslateAccessKeys("FormUpdates", name, "Text", fallback) };
    private Button Link(string name, string caption)
    {
        Button button = new HyperlinkButton { Name = name, Content = Access(name, caption), IsVisible = false, MinHeight = 23, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        return button;
    }
}

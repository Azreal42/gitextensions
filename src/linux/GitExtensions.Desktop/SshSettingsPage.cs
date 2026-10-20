using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>Windows SSH choice, using the same application profile and captured child context as Paths.</summary>
internal sealed class SshSettingsPage : UserControl
{
    private readonly TranslationCatalog _translations;
    private readonly RadioButton _native = new() { Name = "OpenSSH", GroupName = "SshClient" };
    private readonly RadioButton _other = new() { Name = "Other", GroupName = "SshClient" };
    private readonly TextBox _executable = new() { Name = "OtherSsh" };
    private readonly Button _browse = new() { Name = "OtherSshBrowse" };
    private readonly TextBlock _error = new() { Name = "SshSettingsError", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _information = new() { Name = "SshEnvironmentInformation", TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _body;
    private readonly CancellationToken _ownerToken;
    private string _baseline;
    private bool _loading, _busy, _invalidated;
    private int _revision;
    private TaskCompletionSource? _completion;
    internal string Draft => _other.IsChecked == true ? _executable.Text ?? "" : "";
    internal bool IsDirty => Draft != _baseline || (_other.IsChecked == true && _baseline.Length == 0);
    internal bool IsBusy { get; private set; }
    internal Task OperationCompletion => _completion?.Task ?? Task.CompletedTask;
    internal Func<CancellationToken, Task<string?>>? PickExecutable { get; set; }
    internal event Action? BusyChanged;
    internal event Action? Settled;

    internal SshSettingsPage(string initial, TranslationCatalog translations, CancellationToken ownerToken = default)
    {
        Name = "SshSettingsPage"; _translations = translations; _baseline = initial; _ownerToken = ownerToken;
        // The native mode preserves login/session configuration, including an inherited GIT_SSH.
        _native.Content = translations.Translate("Use Git SSH settings (OpenSSH by default)");
        _other.Content = Text("Other", "Other ssh client");
        _browse.Content = Text("OtherSshBrowse", "Browse");
        Grid choices = new() { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto"), RowSpacing = 6, ColumnSpacing = 8 };
        Grid.SetColumnSpan(_native, 3); choices.Children.Add(_native);
        Grid.SetRow(_other, 1); choices.Children.Add(_other);
        Grid.SetRow(_executable, 1); Grid.SetColumn(_executable, 1); choices.Children.Add(_executable);
        Grid.SetRow(_browse, 1); Grid.SetColumn(_browse, 2); choices.Children.Add(_browse);
        _body = new() { Spacing = 10, Children =
        {
            ClassicChrome.Group("groupBox1", Text("groupBox1", "Specify which ssh client to use"), choices),
            new TextBlock { Name = "SshHelp", TextWrapping = TextWrapping.Wrap, Text = translations.Translate("Choose an executable, without arguments or shell quoting. Git Extensions applies it as GIT_SSH to child processes. GIT_SSH_COMMAND and core.sshCommand take precedence. SSH agents, keys and host verification follow your native SSH configuration.") },
            _information
        } };
        Content = new StackPanel { Spacing = 8, Children = { _body, _error } };
        _native.IsCheckedChanged += (_, _) => Changed();
        _other.IsCheckedChanged += (_, _) => Changed();
        _executable.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) { Changed(); } };
        _browse.Click += async (_, _) => await BrowseAsync(_ownerToken);
        AcceptSaved(initial);
    }

    internal bool Validate(out string? error)
    {
        error = _other.IsChecked == true && Draft.Length == 0 ? _translations.Translate("Choose an SSH executable or use Git SSH settings.")
            : Draft.Contains('\0') ? _translations.Translate("Paths cannot contain NUL.") : null;
        ShowError(error); return error is null;
    }
    internal void AcceptSaved(string published)
    {
        if (_invalidated) { return; }
        _loading = true;
        try { _baseline = published; _executable.Text = published; _other.IsChecked = published.Length > 0; _native.IsChecked = published.Length == 0; }
        finally { _loading = false; }
        _revision++; _information.Text = ""; ShowError(null); UpdateEnabled();
    }
    internal void RebasePublished(string published)
    {
        if (_invalidated) { return; }
        if (!IsDirty) { AcceptSaved(published); }
        else { _baseline = published; _revision++; _information.Text = ""; }
    }
    internal void SetDiagnostic(GitEnvironmentDiagnostic diagnostic)
    {
        if (_invalidated) { return; }
        _information.Text = GitEnvironmentChecklist.DescribeSshCommand(diagnostic, _translations)
            + "\n" + _translations.Translate("SSH executable version check:") + " " + diagnostic.Ssh.Executable
            + "\n" + _translations.Translate("SSH authentication has not been tested.");
    }
    internal void ShowError(string? message) { _error.Text = message; _error.IsVisible = !string.IsNullOrEmpty(message); }
    internal void SetBusy(bool busy) { _busy = busy; UpdateEnabled(); }
    internal void Invalidate() { _invalidated = true; _revision++; UpdateEnabled(); }
    internal void FocusClient() { if (_other.IsChecked == true) { _executable.Focus(); } else { _native.Focus(); } }
    private string Text(string name, string fallback) => _translations.Translate("SshSettingsPage", name, "Text", fallback);
    private void Changed() { if (!_loading && !_invalidated) { _revision++; _information.Text = ""; UpdateEnabled(); ShowError(null); } }
    private void UpdateEnabled()
    {
        _body.IsEnabled = !_invalidated && !_busy && !IsBusy;
        _executable.IsEnabled = _browse.IsEnabled = _other.IsChecked == true;
    }
    internal async Task BrowseAsync(CancellationToken cancellationToken = default)
    {
        if (_invalidated || _busy || IsBusy || cancellationToken.IsCancellationRequested) { return; }
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _completion = completion;
        int revision = _revision; IsBusy = true; UpdateEnabled(); BusyChanged?.Invoke();
        try
        {
            string? path;
            if (PickExecutable is { } pick) { path = await pick(cancellationToken); }
            else if (TopLevel.GetTopLevel(this) is { } owner)
            {
                // StorageProvider has no cancellation API. The Settings owner waits for the
                // native picker to return; closing/invalidation discards its eventual result.
                IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new()
                { Title = Text("OtherSshBrowse", "Browse"), AllowMultiple = false });
                path = files.FirstOrDefault()?.TryGetLocalPath();
            }
            else { path = null; }
            if (!_invalidated && !cancellationToken.IsCancellationRequested && revision == _revision && path is not null)
            { _other.IsChecked = true; _executable.Text = path; }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_invalidated && !cancellationToken.IsCancellationRequested) { ShowError(error.Message); } }
        finally
        {
            IsBusy = false; UpdateEnabled();
            try { BusyChanged?.Invoke(); }
            finally { completion.TrySetResult(); Settled?.Invoke(); }
        }
    }
}

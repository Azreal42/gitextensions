using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>Chooses a child-process HOME override without changing the operating system environment.</summary>
internal sealed class GitHomeDirectoryDialog : Window
{
    private readonly RadioButton _inherited, _custom;
    private readonly TextBox _directory;
    private readonly TextBlock _error;
    private readonly GitEnvironmentPreferences _initial;
    private readonly string _inheritedHome;
    private bool _closed, _picking;
    internal GitEnvironmentPreferences Result { get; private set; }
    internal Func<Task<string?>>? PickHomeDirectory { get; set; }

    internal GitHomeDirectoryDialog(GitEnvironmentPreferences initial, TranslationCatalog translations, string? inheritedHome = null)
    {
        _initial = Result = initial; _inheritedHome = inheritedHome ?? Environment.GetEnvironmentVariable("HOME") ?? "";
        Name = "FormFixHome"; Title = "HOME"; Width = 650; SizeToContent = SizeToContent.Height; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _inherited = new() { Name = "defaultHome", GroupName = "HomeDirectory", Content = translations.Translate("Use inherited HOME") + " (" + _inheritedHome + ")", IsChecked = initial.CustomHomeDirectory is null };
        _custom = new() { Name = "otherHome", GroupName = "HomeDirectory", Content = translations.Translate("FormFixHome", "otherHome", "Text", "Other"), IsChecked = initial.CustomHomeDirectory is not null };
        _directory = new() { Name = "otherHomeDir", Text = initial.CustomHomeDirectory ?? "", MinWidth = 220 };
        Button browse = new() { Name = "otherHomeBrowse", Content = translations.Translate("Browse") };
        Grid custom = new() { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8, Children = { _custom, _directory, browse } }; Grid.SetColumn(_directory, 1); Grid.SetColumn(browse, 2);
        _error = new() { Name = "HomeError", TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
        Button ok = new() { Name = "ok", Content = translations.Translate("OK"), MinWidth = 75, IsDefault = true };
        Button cancel = new() { Name = "CancelHome", Content = translations.Translate("Cancel"), MinWidth = 75, IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (_picking) { return; }
            string? message = ValidateDirectory(_custom.IsChecked == true ? _directory.Text ?? "" : _inheritedHome);
            _error.Text = message; _error.IsVisible = message is not null;
            if (message is not null) { _directory.Focus(); return; }
            Result = _initial with { CustomHomeDirectory = _custom.IsChecked == true ? _directory.Text : null }; Close(true);
        };
        cancel.Click += (_, _) => Close(false);
        browse.Click += async (_, _) =>
        {
            if (_picking || _closed) { return; }
            _picking = true; browse.IsEnabled = false; ok.IsEnabled = false; string? before = _directory.Text;
            try
            {
                string? selected;
                if (PickHomeDirectory is { } pick) { selected = await pick(); }
                else
                {
                    IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new() { AllowMultiple = false, Title = translations.Translate("Browse") });
                    selected = folders.FirstOrDefault()?.TryGetLocalPath();
                }
                if (!_closed && before == _directory.Text && selected is not null) { _custom.IsChecked = true; _directory.Text = selected; }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!_closed) { _error.Text = error.Message; _error.IsVisible = true; } }
            finally { _picking = false; if (!_closed) { browse.IsEnabled = true; ok.IsEnabled = true; } }
        };
        _inherited.IsCheckedChanged += (_, _) => _directory.IsEnabled = _custom.IsChecked == true;
        _custom.IsCheckedChanged += (_, _) => _directory.IsEnabled = _custom.IsChecked == true;
        _directory.IsEnabled = _custom.IsChecked == true;
        Content = new StackPanel { Margin = new Thickness(12), Spacing = 10, Children =
        {
            ClassicChrome.Group("groupBox8", translations.Translate("FormFixHome", "groupBox8", "Text", "Environment"), new StackPanel { Spacing = 8, Children =
            {
                new TextBlock { Name = "label51", Text = translations.Translate("Choose HOME for Git Extensions child processes. $GIT_CONFIG_GLOBAL keeps priority when explicitly set. This does not change your desktop session."), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, _inherited, custom
            } }), _error,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { ok, cancel } }
        } };
        Closed += (_, _) => _closed = true;
    }

    internal static string? ValidateDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return "Please enter a HOME directory."; }
        if (path.Contains('\0')) { return "HOME cannot contain NUL."; }
        if (!Path.IsPathFullyQualified(path)) { return "HOME must be an absolute directory path."; }
        try
        {
            if (!Directory.Exists(path)) { return "HOME directory does not exist or is not accessible: " + path; }
            using IEnumerator<string> contents = Directory.EnumerateFileSystemEntries(path).GetEnumerator(); contents.MoveNext();
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return error.Message; }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>Editable application environment. Publishing belongs to the Settings host.</summary>
internal sealed class GitEnvironmentSettingsPage : UserControl
{
    private readonly TranslationCatalog _translations;
    private readonly TextBox _git = new() { Name = "GitPath" };
    private readonly TextBox _tools = new() { Name = "LinuxToolsDir" };
    private readonly TextBlock _environment = new() { Name = "homeIsSetToLabel", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Name = "GitEnvironmentError", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _body;
    private GitEnvironmentPreferences _baseline;
    private GitEnvironmentPreferences _draft;
    private bool _loading, _busy, _actionBusy, _invalidated;
    private int _revision;
    private string? _environmentInformation;

    internal GitEnvironmentPreferences Draft => _draft;
    internal bool IsDirty => _draft != _baseline;
    internal bool IsInvalidated => _invalidated;
    internal event Action? DraftChanged;
    internal Func<Task<string?>>? PickGitExecutable { get; set; }
    internal Func<Task<string?>>? PickToolsDirectory { get; set; }
    internal Func<GitEnvironmentPreferences, Task<GitEnvironmentPreferences?>>? ChangeHome { get; set; }
    internal Func<Task>? OpenGitHelp { get; set; }

    internal GitEnvironmentSettingsPage(GitEnvironmentPreferences initial, TranslationCatalog translations)
    {
        Name = "GitSettingsPage"; _translations = translations; _baseline = _draft = initial;
        Grid paths = new() { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 6 };
        TextBlock intro = Label("label50", translations.Translate("Set the correct paths to Git and Linux tools."));
        Grid.SetColumnSpan(intro, 3); paths.Children.Add(intro);
        AddPath(1, "lblGitCommand", translations.Translate("Command used to run git (empty uses PATH)"), _git, "BrowseGitPath", async () =>
        {
            string? path = PickGitExecutable is { } picker ? await picker() : await PickFileAsync();
            return () => { if (path is not null) { _git.Text = path; } };
        });
        AddPath(2, "lblShPath", Text("lblShPath", "Path to linux tools (sh). Leave empty when it is in the path."), _tools, "BrowseLinuxToolsDir", async () =>
        {
            string? path = PickToolsDirectory is { } picker ? await picker() : await PickDirectoryAsync(_tools.Text);
            return () => { if (path is not null) { _tools.Text = path; } };
        });
        Button help = new()
        {
            Name = "downloadGitForWindows", Content = new TextBlock { Text = Text("downloadGitForWindows", "Download Git"), TextDecorations = TextDecorations.Underline },
            Background = Brushes.Transparent, BorderThickness = default, Padding = default, MinHeight = 18,
            HorizontalAlignment = HorizontalAlignment.Left, Cursor = new(StandardCursorType.Hand)
        };
        void HelpColor() => help.Foreground = ActualThemeVariant == ThemeVariant.Dark ? Brush.Parse("#78b7ff") : Brushes.Blue;
        ActualThemeVariantChanged += (_, _) => HelpColor(); HelpColor();
        help.Click += async (_, _) => await RunActionAsync(async () =>
        {
            if (OpenGitHelp is { } open) { await open(); }
            else if (TopLevel.GetTopLevel(this) is { } top && !await top.Launcher.LaunchUriAsync(new Uri("https://git-scm.com/download/linux")))
            { throw new IOException(_translations.Translate("Unable to open Git installation help.")); }
            return null;
        });
        Grid.SetRow(help, 3); Grid.SetColumnSpan(help, 3); paths.Children.Add(help);
        Button home = new() { Name = "ChangeHomeButton", Content = Text("ChangeHomeButton", "Change HOME"), HorizontalAlignment = HorizontalAlignment.Left };
        home.Click += async (_, _) => await RunActionAsync(async () =>
        {
            GitEnvironmentPreferences? chosen;
            if (ChangeHome is { } change) { chosen = await change(_draft); }
            else if (TopLevel.GetTopLevel(this) is Window owner)
            {
                GitHomeDirectoryDialog dialog = new(_draft, _translations) { RequestedThemeVariant = owner.ActualThemeVariant };
                chosen = await dialog.ShowDialog<bool>(owner) ? dialog.Result : null;
            }
            else { chosen = null; }
            return () => { if (chosen is not null) { _draft = _draft with { CustomHomeDirectory = chosen.CustomHomeDirectory }; UpdateDraftChanged(); } };
        });
        StackPanel environment = new() { Spacing = 8, Children =
        {
            Label("lblGlobalConfigPath", translations.Translate("Git uses $GIT_CONFIG_GLOBAL when it is set. Otherwise global configuration is read from $XDG_CONFIG_HOME/git/config and $HOME/.gitconfig. A custom HOME applies only to Git Extensions child processes.")),
            _environment, home
        } };
        _body = new() { Spacing = 10, Children = { ClassicChrome.Group("gbPaths", Text("gbPaths", "Paths"), paths), ClassicChrome.Group("gbEnvironment", Text("gbEnvironment", "Environment"), environment) } };
        Content = new StackPanel { Spacing = 8, Children = { _body, _error } };
        // TextChanged is deferred by Avalonia. Capture the value synchronously so
        // Save/Rebase cannot read the previous draft in the same input turn.
        _git.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateEditors(); } };
        _tools.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateEditors(); } };
        AcceptSaved(initial);

        void AddPath(int row, string labelName, string caption, TextBox editor, string buttonName, Func<Task<Action?>> pick)
        {
            TextBlock label = Label(labelName, caption); label.MaxWidth = 280; label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(label, row); paths.Children.Add(label); Grid.SetRow(editor, row); Grid.SetColumn(editor, 1); paths.Children.Add(editor);
            Button browse = new() { Name = buttonName, Content = Text(buttonName, "Browse"), HorizontalAlignment = HorizontalAlignment.Stretch };
            browse.Click += async (_, _) => await RunActionAsync(pick);
            Grid.SetRow(browse, row); Grid.SetColumn(browse, 2); paths.Children.Add(browse);
        }
    }

    internal bool Validate(out string? error)
    {
        error = null;
        if (_draft.GitExecutable.Contains('\0') || _draft.ToolsDirectory.Contains('\0')) { error = _translations.Translate("Paths cannot contain NUL."); }
        else if (_draft.CustomHomeDirectory is { } home) { error = GitHomeDirectoryDialog.ValidateDirectory(home); }
        ShowError(error); return error is null;
    }

    internal void AcceptSaved(GitEnvironmentPreferences published)
    {
        if (_invalidated) { return; }
        _loading = true;
        try { _baseline = _draft = published; _git.Text = published.GitExecutable; _tools.Text = published.ToolsDirectory; }
        finally { _loading = false; }
        _revision++; ShowError(null); UpdateEnvironment(); DraftChanged?.Invoke();
    }

    internal void RebasePublished(GitEnvironmentPreferences published)
    {
        if (_invalidated) { return; }
        GitEnvironmentPreferences draft = published with
        {
            GitExecutable = _draft.GitExecutable != _baseline.GitExecutable ? _draft.GitExecutable : published.GitExecutable,
            ToolsDirectory = _draft.ToolsDirectory != _baseline.ToolsDirectory ? _draft.ToolsDirectory : published.ToolsDirectory,
            CustomHomeDirectory = _draft.CustomHomeDirectory != _baseline.CustomHomeDirectory ? _draft.CustomHomeDirectory : published.CustomHomeDirectory,
            CheckAtStartup = _draft.CheckAtStartup != _baseline.CheckAtStartup ? _draft.CheckAtStartup : published.CheckAtStartup
        };
        _loading = true;
        try { _baseline = published; _draft = draft; _git.Text = draft.GitExecutable; _tools.Text = draft.ToolsDirectory; }
        finally { _loading = false; }
        UpdateDraftChanged();
    }

    internal void SetEnvironmentInformation(string text)
    {
        if (_invalidated) { return; }
        _environmentInformation = text; UpdateEnvironment();
    }

    internal void SetCheckAtStartup(bool value)
    {
        if (_invalidated || _draft.CheckAtStartup == value) { return; }
        _draft = _draft with { CheckAtStartup = value }; UpdateDraftChanged();
    }
    internal void SetBusy(bool busy) { _busy = busy; UpdateEnabled(); }
    internal void Invalidate() { _invalidated = true; _revision++; UpdateEnabled(); }
    internal void ShowError(string? message) { _error.Text = message; _error.IsVisible = !string.IsNullOrEmpty(message); }
    private string Text(string name, string source) => _translations.Translate("GitSettingsPage", name, "Text", source);
    private static TextBlock Label(string name, string text) => new() { Name = name, Text = text, TextWrapping = TextWrapping.Wrap };
    private void UpdateEditors()
    {
        if (_loading || _invalidated) { return; }
        _draft = _draft with { GitExecutable = _git.Text ?? "", ToolsDirectory = _tools.Text ?? "" }; UpdateDraftChanged();
    }
    private void UpdateDraftChanged() { _revision++; UpdateEnvironment(); DraftChanged?.Invoke(); }
    private void UpdateEnvironment()
    {
        if (_environmentInformation is not null) { _environment.Text = _environmentInformation; return; }
        string inherited = Environment.GetEnvironmentVariable("HOME") ?? "";
        string home = _draft.CustomHomeDirectory ?? inherited;
        string? global = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        _environment.Text = "$HOME = " + home + (_draft.CustomHomeDirectory is null ? " (" + _translations.Translate("inherited") + ")" : " (" + _translations.Translate("custom, child processes only") + ")")
            + "\n" + (global is null ? "$GIT_CONFIG_GLOBAL: " + _translations.Translate("Not set") : "$GIT_CONFIG_GLOBAL = " + global)
            + "\n$XDG_CONFIG_HOME = " + (xdg ?? Path.Combine(home, ".config"));
    }
    private void UpdateEnabled() => _body.IsEnabled = !_invalidated && !_busy && !_actionBusy;
    private async Task RunActionAsync(Func<Task<Action?>> action)
    {
        if (_invalidated || _busy || _actionBusy) { return; }
        _actionBusy = true; int revision = _revision; UpdateEnabled();
        try { Action? accept = await action(); if (!_invalidated && revision == _revision) { accept?.Invoke(); ShowError(null); } }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_invalidated) { ShowError(error.Message); } }
        finally { _actionBusy = false; UpdateEnabled(); }
    }
    private async Task<string?> PickFileAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } owner) { return null; }
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new() { Title = Text("BrowseGitPath", "Browse"), AllowMultiple = false });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    private async Task<string?> PickDirectoryAsync(string? initial)
    {
        if (TopLevel.GetTopLevel(this) is not { } owner) { return null; }
        IStorageFolder? folder = Path.IsPathFullyQualified(initial ?? "") ? await owner.StorageProvider.TryGetFolderFromPathAsync(initial!) : null;
        IReadOnlyList<IStorageFolder> folders = await owner.StorageProvider.OpenFolderPickerAsync(new() { Title = Text("BrowseLinuxToolsDir", "Browse"), AllowMultiple = false, SuggestedStartLocation = folder });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}

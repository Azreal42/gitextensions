using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitCommands;

namespace GitExtensions.Linux.Desktop;

/// <summary>FormCleanupRepository's worktree cleanup; independent of the selected revision.</summary>
public sealed class CleanupRepositoryDialog : RepositoryDialog
{
    private readonly RadioButton _all = Mode("CleanupAll", "RemoveAll", "Remove all untracked files", true);
    private readonly RadioButton _nonIgnored = Mode("CleanupNonIgnored", "RemoveNonIgnored", "Remove only non-ignored untracked files");
    private readonly RadioButton _ignored = Mode("CleanupIgnored", "RemoveIgnored", "Remove only ignored untracked files");
    private readonly CheckBox _directories = new() { Name = "CleanupDirectories", IsChecked = true };
    private readonly CheckBox _submodules = new() { Name = "CleanupSubmodules" };
    private readonly CheckBox _include = new() { Name = "CleanupIncludeFilter" }, _exclude = new() { Name = "CleanupExcludeFilter" };
    private readonly TextBox _includes = Paths("CleanupIncludePaths"), _excludes = Paths("CleanupExcludePaths");
    private readonly TextBlock _includeHint = Hint("CleanupIncludeHint"), _excludeHint = Hint("CleanupExcludeHint");
    private readonly TextBox _log = new() { Name = "CleanupLog", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 64 };
    private readonly Button _preview, _cleanup, _addInclude, _addExclude;
    private readonly List<Bitmap> _icons = [];
    private readonly CancellationTokenSource _lifetime = new();
    private GitRepositoryInfo? _info;
    private bool _closing;
    public GitCleanResult? LastResult { get; private set; }
    // Only picker selection is replaceable. The default remains the native StorageProvider.
    internal Func<FolderPickerOpenOptions, Task<IStorageFolder?>>? FolderPicker { get; set; }
    internal Func<FilePickerOpenOptions, Task<IStorageFile?>>? FilePicker { get; set; }

    public CleanupRepositoryDialog(GitRepository repository, string? path = null) : base(repository, "Clean working directory")
    {
        Name = "CleanupRepositoryDialog"; Title = Text("$this", "Clean working directory");
        // Designer minimum 558x738 at 120 dpi, scaled to the classic 96 dpi layout.
        Width = MinWidth = 446; Height = MinHeight = 591; ConstrainStatusHeight("CleanupStatus", 28);
        foreach (RadioButton radio in new[] { _all, _nonIgnored, _ignored }) { (string key, string source) = ((string, string))radio.Tag!; radio.Content = Caption(key, source); }
        _directories.Content = Caption("RemoveDirectories", "Remove untracked directories");
        _submodules.Content = Caption("CleanSubmodules", "Clean submodules recursively");
        _include.Content = Caption("checkBoxIncludePathFilter", "Affect the following directory path(s) only:");
        _exclude.Content = Caption("checkBoxExcludePathFilter", "Exclude the following file path(s):");
        _includeHint.Text = Text("labelPathHintInclude", "(one path per line)"); _excludeHint.Text = Text("labelPathHintExclude", "(one path per line)");
        foreach (CheckBox box in new[] { _directories, _submodules, _include, _exclude }) { box.MinHeight = 24; box.Padding = new Thickness(0); }
        _preview = Action("PreviewCleanup", "Preview", PreviewAsync); _preview.Content = IconCaption("Preview", "Preview", "Preview");
        _cleanup = Action("ExecuteCleanup", "Cleanup", CleanupAsync); _cleanup.Content = IconCaption("Cleanup", "Cleanup", "CleanupRepo");
        _addInclude = Action("AddCleanupInclude", "Add a path…", () => ChoosePathAsync(true)); _addInclude.Content = Caption("AddInclusivePath", "Add a path...");
        _addExclude = Action("AddCleanupExclude", "Add a path…", () => ChoosePathAsync(false)); _addExclude.Content = Caption("AddExclusivePath", "Add a path...");
        Button close = new() { Name = "CloseCleanup", Content = T("Close"), IsCancel = true }; close.Click += (_, _) => Close();
        foreach (Button button in new[] { _preview, _cleanup, close, _addInclude, _addExclude }) { button.Height = 25; button.MinHeight = 0; button.MinWidth = 0; button.HorizontalAlignment = HorizontalAlignment.Stretch; }
        StackPanel settings = new() { Name = "CleanupSettings", Margin = new Thickness(12, 10, 12, 0), Spacing = 4 };
        settings.Children.Add(ExportDialogControls.Group(Text("groupBox1", "Remove untracked files from working directory"),
            new StackPanel { Children = { _all, _nonIgnored, _ignored } }));
        settings.Children.Add(_directories); settings.Children.Add(_submodules);
        settings.Children.Add(Filter("CleanupIncludeBlock", _include, _addInclude, _includes, _includeHint));
        settings.Children.Add(Filter("CleanupExcludeBlock", _exclude, _addExclude, _excludes, _excludeHint));
        DockPanel.SetDock(settings, Dock.Top); Root.Children.Add(settings);
        Grid actions = new() { Name = "CleanupActions", ColumnDefinitions = new("*,6,*,6,*"), Margin = new Thickness(48, 10, 12, 8) };
        actions.Children.Add(_preview); Grid.SetColumn(_cleanup, 2); actions.Children.Add(_cleanup); Grid.SetColumn(close, 4); actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Top); Root.Children.Add(actions);
        Grid output = new() { Name = "CleanupOutput", RowDefinitions = new("Auto,*"), Margin = new Thickness(12, 0, 12, 8) };
        output.Children.Add(new TextBlock { Name = "CleanupLogLabel", Text = Text("label1", "Log:"), Margin = new Thickness(0, 0, 0, 3) });
        Grid.SetRow(_log, 1); output.Children.Add(_log); Root.Children.Add(output);
        ScrollViewer.SetHorizontalScrollBarVisibility(_log, ScrollBarVisibility.Auto); ScrollViewer.SetVerticalScrollBarVisibility(_log, ScrollBarVisibility.Auto);
        _include.IsCheckedChanged += (_, _) => UpdateActions(); _exclude.IsCheckedChanged += (_, _) => UpdateActions();
        Track(_all, _nonIgnored, _ignored, _directories, _submodules, _include, _exclude, _includes, _excludes);
        AddHandler(KeyDownEvent, (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } }, RoutingStrategies.Tunnel);
        Closing += (_, _) => { _closing = true; _lifetime.Cancel(); };
        Closed += (_, _) => { foreach (Bitmap icon in _icons) { icon.Dispose(); } };
        Opened += (_, _) => { if (_info is not null) { _all.Focus(); } };
        SetPathArgument(path); UpdateActions();
    }

    private string Text(string item, string source) => Translations.Translate("FormCleanupRepository", item, "Text", source);
    private AccessText Caption(string item, string source) => new() { Text = Translations.TranslateAccessKeys("FormCleanupRepository", item, "Text", source), TextWrapping = TextWrapping.Wrap };
    private static RadioButton Mode(string name, string key, string source, bool selected = false)
        => new() { Name = name, Tag = (key, source), GroupName = "CleanupMode", IsChecked = selected, MinHeight = 24, Padding = new Thickness(0) };
    private static TextBox Paths(string name) => new() { Name = name, AcceptsReturn = true, Height = 62, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30, 0, 0, 0) };
    private static TextBlock Hint(string name) => new() { Name = name, Margin = new Thickness(30, 0, 0, 3) };
    private static Grid Filter(string name, CheckBox enabled, Button browse, TextBox paths, TextBlock hint)
    {
        Grid grid = new() { Name = name, ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        browse.MinWidth = 120; browse.Padding = new Thickness(2, 1);
        if (browse.Content is AccessText caption) { caption.TextWrapping = TextWrapping.NoWrap; }
        enabled.Margin = new Thickness(0, 0, 6, 0); grid.Children.Add(enabled); Grid.SetColumn(browse, 1); browse.VerticalAlignment = VerticalAlignment.Top; grid.Children.Add(browse);
        Grid.SetRow(paths, 1); Grid.SetColumnSpan(paths, 2); paths.Margin = new Thickness(30, 3, 0, 0); grid.Children.Add(paths);
        Grid.SetRow(hint, 2); Grid.SetColumnSpan(hint, 2); grid.Children.Add(hint); return grid;
    }
    private Control IconCaption(string item, string caption, string icon)
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + icon + ".png")); Bitmap bitmap = new(stream); _icons.Add(bitmap);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children =
        { new Image { Source = bitmap, Width = 16, Height = 16, Tag = icon + ".png" }, Caption(item, caption) } };
    }
    public void SetPathArgument(string? path)
    {
        _include.IsChecked = _exclude.IsChecked = !string.IsNullOrEmpty(path); _includes.Text = _excludes.Text = path ?? "";
    }
    public GitCleanOptions ReadOptions() => new(_all.IsChecked == true ? CleanMode.All : _ignored.IsChecked == true ? CleanMode.OnlyIgnored : CleanMode.OnlyNonIgnored,
        _directories.IsChecked == true, _submodules.IsChecked == true)
    {
        IncludePaths = _include.IsChecked == true ? Lines(_includes.Text) : [], ExcludePaths = _exclude.IsChecked == true ? Lines(_excludes.Text) : []
    };
    private static string[] Lines(string? value) => (value ?? "").Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
    public override Task InitializeAsync() => RunAsync("Clean working directory", async token =>
    {
        _info = await Repository.GetInfoAsync(token); token.ThrowIfCancellationRequested();
        if (_info.IsBare) { throw new InvalidOperationException(T("This operation requires a working tree.")); }
        Status.Text = ""; if (IsVisible && !_closing) { Dispatcher.UIThread.Post(() => { if (!IsBusy && !_closing) { _all.Focus(); } }); }
    }, cancellationToken: _lifetime.Token);
    private bool CanAct => _info is { IsBare: false } && !IsBusy && !_closing && IsVisible;
    private Task PreviewAsync() => !CanAct ? Task.CompletedTask : RunAsync("Preview", token => ExecuteAsync(true, token), cancellationToken: _lifetime.Token);
    private Task CleanupAsync()
        => !CanAct ? Task.CompletedTask : RunAsync("Cleanup", async token =>
        {
            if (!await ConfirmAsync(token)) { return; } token.ThrowIfCancellationRequested(); if (_closing || !IsVisible) { return; }
            await ExecuteAsync(false, token);
        }, cancellationToken: _lifetime.Token);
    private async Task ExecuteAsync(bool preview, CancellationToken token)
    {
        GitCleanOptions options = ReadOptions();
        LastResult = preview ? await Repository.PreviewCleanAsync(options, token) : await Repository.CleanWorkingDirectoryAsync(options, token);
        if (LastResult.MutationStarted) { MarkRepositoryChanged(); }
        StringBuilder output = new();
        foreach (GitCleanCommandResult command in LastResult.Commands)
        {
            output.Append(command.StandardOutput); output.Append(command.StandardError);
            // A cancelled runner without a result has no output to invent; keep its diagnostic distinct.
            if (command.Result is null && command.Error is { } error) { if (output.Length > 0 && output[^1] != '\n') { output.Append('\n'); } output.Append(error).Append('\n'); }
        }
        _log.Text = output.ToString();
        Status.Text = LastResult.Cancelled ? T("Operation cancelled") : LastResult.Success ? ""
            : string.Join('\n', LastResult.Commands.Where(command => !command.Success).Select(command => command.Error ?? command.StandardError ?? T("Cleanup failed")));
    }
    private async Task<bool> ConfirmAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (_closing || !IsVisible) { return false; }
        Window question = new() { Name = "CleanupConfirmation", Title = Text("_reallyCleanupQuestionCaption", "Cleanup"), Width = 460,
            SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ActualThemeVariant };
        Button yes = new() { Name = "ConfirmCleanup", Content = T("Yes"), IsDefault = true, MinWidth = 75, Height = 25 }, no = new() { Name = "CancelCleanup", Content = T("No"), IsCancel = true, MinWidth = 75, Height = 25 };
        yes.Click += (_, _) => question.Close(true); no.Click += (_, _) => question.Close(false);
        question.Content = new StackPanel { Margin = new Thickness(12), Spacing = 12, Children =
        {
            new TextBlock { Name = "CleanupQuestion", Text = Text("_reallyCleanupQuestion", "Are you sure you want to cleanup the repository?"), TextWrapping = TextWrapping.Wrap },
            ExportDialogControls.Buttons(yes, no)
        } };
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => question.Close(false)));
        try { bool answer = await question.ShowDialog<bool>(this); token.ThrowIfCancellationRequested(); return answer; }
        finally { question.Close(); }
    }
    private Task ChoosePathAsync(bool include)
        => !CanAct ? Task.CompletedTask : RunAsync("Add a path…", async token =>
        {
            using IStorageFolder? start = await StorageProvider.TryGetFolderFromPathAsync(new Uri(_info!.RootPath)); token.ThrowIfCancellationRequested(); if (_closing || !IsVisible) { return; }
            IStorageItem? chosen;
            if (include)
            {
                FolderPickerOpenOptions options = new() { Title = Text("AddInclusivePath", "Add a path..."), AllowMultiple = false, SuggestedStartLocation = start };
                chosen = FolderPicker is { } picker ? await picker(options) : (await StorageProvider.OpenFolderPickerAsync(options)).FirstOrDefault();
            }
            else
            {
                FilePickerOpenOptions options = new() { Title = Text("AddExclusivePath", "Add a path..."), AllowMultiple = false, SuggestedStartLocation = start };
                chosen = FilePicker is { } picker ? await picker(options) : (await StorageProvider.OpenFilePickerAsync(options)).FirstOrDefault();
            }
            using (chosen)
            {
                token.ThrowIfCancellationRequested(); if (_closing || !IsVisible || chosen is null) { return; }
                string local = chosen.TryGetLocalPath() ?? throw new InvalidOperationException(T("Select a local path inside the working directory."));
                string selected = PhysicalPath(local), root = PhysicalPath(_info!.RootPath), git = PhysicalPath(_info.GitDirectory), common = PhysicalPath(_info.CommonDirectory);
                if (!Inside(selected, root) || Inside(selected, git) || Inside(selected, common) || selected == Path.Combine(root, ".git")
                    || (include ? !Directory.Exists(selected) : !File.Exists(selected)))
                { throw new InvalidOperationException(T("Select a path inside the working directory, outside Git metadata.")); }
                TextBox text = include ? _includes : _excludes; string addition = include ? selected : Path.GetRelativePath(root, selected);
                text.Text = (text.Text ?? "") + (string.IsNullOrEmpty(text.Text) ? "" : "\n") + addition;
                (include ? _include : _exclude).IsChecked = true; Status.Text = "";
            }
        }, cancellationToken: _lifetime.Token);
    private static bool Inside(string path, string directory) => path == directory || path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static string PhysicalPath(string path)
    {
        string full = Path.GetFullPath(path), resolved = Path.GetPathRoot(full)!;
        foreach (string component in full[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(resolved, component); FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            resolved = info.LinkTarget is null ? next : info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("The selected symbolic link cannot be resolved.");
        }
        return resolved;
    }
    protected override void UpdateActions()
    {
        if (_preview is null) { return; } bool ready = _info is { IsBare: false } && !IsBusy && !_closing;
        _preview.IsEnabled = _cleanup.IsEnabled = _addInclude.IsEnabled = _addExclude.IsEnabled = ready;
        _includes.IsEnabled = ready && _include.IsChecked == true; _excludes.IsEnabled = ready && _exclude.IsChecked == true;
        _includeHint.IsVisible = _include.IsChecked == true; _excludeHint.IsVisible = _exclude.IsChecked == true;
    }
}

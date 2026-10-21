using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop;

/// <summary>The compact FormOpenDirectory, with local discovery and the shared startup lifetime.</summary>
internal sealed class OpenRepositoryDialog : StartupRepositoryDialog
{
    private readonly ComboBox _directory = RepositorySourceControls.EditableChoice("_NO_TRANSLATE_Directory");
    private readonly Grid _body = new() { ColumnDefinitions = new("Auto,*,26,Auto"), RowDefinitions = new("24,25"),
        ColumnSpacing = 3, RowSpacing = 7, Margin = new Thickness(13, 7, 3, 17) };
    private readonly Button _up, _browse, _open;
    private readonly Func<Window, string?, CancellationToken, Task<string?>> _pickFolder;
    private readonly List<CloneDirectoryCompletion> _completions = [];
    private CloneDirectoryCompletion? _completion;
    private TextBox? _editor;
    private Task _operationCompletion = Task.CompletedTask;
    private bool _closing, _closingAfterCompletion;

    internal string? ValidationError { get; private set; }
    internal Exception? ValidationException { get; private set; }
    internal Task WindowWorkCompletion => Task.WhenAll(_completions.Select(item => item.Idle).Append(_operationCompletion));
    // Hold an actual filesystem result before publication, as in the shared Clone consumer.
    internal Func<string, IReadOnlyList<string>, Task>? DirectoryCompletionBeforeApply
    { get => _completion?.BeforeApply; set { if (_completion is not null) { _completion.BeforeApply = value; } } }

    internal OpenRepositoryDialog(string? defaultCloneDestination, string? currentRepository,
        RepositoryHistoryState history, string? inheritedHome = null,
        Func<Window, string?, CancellationToken, Task<string?>>? pickFolder = null) : base("Open local repository", showOperationStatus: false)
    {
        // The storage provider has no cancellation overload. Keep its real Task in
        // our operation until it returns; a cancelled/closing dialog rejects its result.
        _pickFolder = pickFolder ?? ((owner, path, _) => ExportDialogControls.FolderAsync(owner,
            Translations.Translate("FormOpenDirectory", "folderBrowserButton", "Text", "Browse..."), path));
        Name = "FormOpenDirectory";
        Title = Translations.Translate("FormOpenDirectory", "$this", "Text", "Open local repository");
        Width = 615; MinWidth = 450; MaxWidth = 800;
        Height = MinHeight = MaxHeight = 81; SizeToContent = SizeToContent.Manual; CanResize = true;
        CanMinimize = CanMaximize = false;
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        _directory.ItemsSource = SuggestedDirectories(defaultCloneDestination, currentRepository, history,
            inheritedHome ?? GitEnvironment.Current.HomeDirectory);
        _directory.SelectedIndex = _directory.ItemCount > 0 ? 0 : -1;
        _directory.Text = _directory.SelectedItem as string ?? "";
        Label label = new() { Name = "label1", Padding = default, VerticalAlignment = VerticalAlignment.Center,
            Target = _directory, Content = Caption("label1", "&Directory:") };
        _up = Action("folderGoUpButton", "", () => { GoUp(); return Task.CompletedTask; });
        _up.MinWidth = _up.Width = 26; _up.Padding = default; _up.Content = ResetDialogControls.Icon("NavigateUp");
        ToolTip.SetTip(_up, Translations.Translate("FormOpenDirectory", "folderGoUpButton", "toolTip1", "Go to parent directory..."));
        _browse = Action("folderBrowserButton", "Browse", BrowseAsync);
        _browse.MinWidth = 135; _browse.Content = ButtonCaption("BrowseFileExplorer", "folderBrowserButton", "&Browse...");
        _open = Action("Load", "Open", OpenAsync);
        _open.MinWidth = 164; _open.IsDefault = true; _open.Content = ButtonCaption("RepoOpen", "Load", "Open");
        _open.HorizontalAlignment = HorizontalAlignment.Right;
        _body.Children.Add(label); Grid.SetColumn(_directory, 1); _body.Children.Add(_directory);
        Grid.SetColumn(_up, 2); _body.Children.Add(_up); Grid.SetColumn(_browse, 3); _body.Children.Add(_browse);
        Grid.SetRow(_open, 1); Grid.SetColumn(_open, 2); Grid.SetColumnSpan(_open, 2); _body.Children.Add(_open);
        Root.Children.Add(_body); Track(_directory);
        _directory.PropertyChanged += (_, change) => { if (change.Property == ComboBox.TextProperty) { UpdateActions(); } };
        _directory.TemplateApplied += (_, _) => AttachCompletion();
        _directory.AddHandler(KeyDownEvent, async (_, args) =>
        {
            // WinForms explicitly opens on DirectoryKeyPress. Avalonia's editable
            // ComboBox consumes Enter before the window's default button sees it.
            // An open MRU or filesystem popup still owns Enter for acceptance.
            if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None || _closing || IsBusy
                || _directory.IsDropDownOpen || _completion?.CanAcceptSuggestion == true) { return; }
            args.Handled = true;
            // An empty/error filesystem popup has nothing for Enter to accept.
            // Dismiss its presentation, then validate the trimmed path in this same gesture.
            if (_completion?.IsOpen == true) { _completion.Dismiss(); }
            await OpenAsync();
        }, RoutingStrategies.Tunnel);
        _directory.DropDownOpened += (_, _) =>
        {
            // The MRU arrow and filesystem suggestions are separate Windows sources.
            // A deliberate MRU opening dismisses the transient filesystem popup.
            _completion?.Dismiss();
        };
        Opened += (_, _) =>
        {
            AttachCompletion(); UpdateActions();
            ModalWindowActivation.AfterMapping(this, () => !_closing && IsVisible);
            Dispatcher.UIThread.Post(() => { if (!_closing && IsVisible) { _editor?.Focus(); _editor?.SelectAll(); } });
        };
        Closing += (_, args) =>
        {
            _closing = true;
            foreach (CloneDirectoryCompletion completion in _completions) { completion.Dispose(); }
            if (_completions.Any(item => !item.Idle.IsCompleted))
            {
                args.Cancel = true;
                if (!_closingAfterCompletion) { _closingAfterCompletion = true; _ = CloseAfterCompletionAsync(); }
            }
        };
        Closed += (_, _) => { foreach (CloneDirectoryCompletion completion in _completions) { completion.Dispose(); } };
        UpdateActions();
    }

    internal static IReadOnlyList<string> SuggestedDirectories(string? defaultCloneDestination, string? currentRepository,
        RepositoryHistoryState history, string? inheritedHome)
    {
        List<string> paths = [];
        static string WithSeparator(string path) => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
        if (!string.IsNullOrWhiteSpace(defaultCloneDestination)) { paths.Add(WithSeparator(defaultCloneDestination)); }
        if (!string.IsNullOrWhiteSpace(currentRepository) && new DirectoryInfo(currentRepository).Parent is { } parent)
        { paths.Add(WithSeparator(parent.FullName)); }
        paths.AddRange(history.Recent.Select(item => item.Path));
        if (paths.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(history.LastVisitedRepositoryPath)) { paths.Add(WithSeparator(history.LastVisitedRepositoryPath)); }
            if (!string.IsNullOrWhiteSpace(inheritedHome)) { paths.Add(WithSeparator(inheritedHome)); }
        }
        return paths.Distinct(StringComparer.Ordinal).ToArray();
    }

    private AccessText Caption(string item, string source) => new()
    { Text = Translations.TranslateAccessKeys("FormOpenDirectory", item, "Text", source), TextWrapping = TextWrapping.NoWrap };
    private StackPanel ButtonCaption(string icon, string item, string source) => new()
    { Orientation = Orientation.Horizontal, Spacing = 5, Children = { ResetDialogControls.Icon(icon), Caption(item, source) } };

    private void AttachCompletion()
    {
        TextBox? editor = _directory.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        if (_closing || editor is null || ReferenceEquals(_editor, editor)) { return; }
        _completion?.Dispose(); _editor = editor;
        _completion = new(editor, _body, "OpenRepository"); _completions.Add(_completion);
    }
    private async Task CloseAfterCompletionAsync()
    {
        await Task.WhenAll(_completions.Select(item => item.Idle));
        if (IsVisible) { Close(); }
    }
    private void GoUp()
    {
        try
        {
            string path = _directory.Text ?? "";
            if (!Directory.Exists(path) || Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))) is not { } parent) { return; }
            _directory.Text = Path.EndsInDirectorySeparator(parent.FullName) ? parent.FullName : parent.FullName + Path.DirectorySeparatorChar;
            _editor?.Focus();
            if (_editor is not null) { _editor.SelectionStart = _editor.SelectionEnd = _editor.CaretIndex = (_directory.Text ?? "").Length; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
    private Task OpenAsync() => StartReadAsync(pick: false);
    private Task BrowseAsync() => StartReadAsync(pick: true);
    private Task StartReadAsync(bool pick)
    {
        if (_closing || IsBusy) { return Task.CompletedTask; }
        _operationCompletion = ReadAsync(pick); return _operationCompletion;
    }
    private Task ReadAsync(bool pick) => RunAsync("Open repository", async token =>
    {
        ValidationError = null; ValidationException = null;
        try
        {
            if (pick)
            {
                string? selected = await _pickFolder(this, _directory.Text, token);
                token.ThrowIfCancellationRequested();
                if (_closing || !IsVisible || string.IsNullOrEmpty(selected)) { return; }
                _directory.Text = selected;
            }
            string path = (_directory.Text ?? "").Trim(); _directory.Text = path;
            using IDisposable execution = GitEnvironment.BeginOperation();
            GitRepository candidate = await GitRepository.OpenAsync(path, token, GitEnvironment.Current.CreateRunner());
            token.ThrowIfCancellationRequested();
            if (!_closing && IsVisible) { Complete(candidate); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or GitCommandException or System.ComponentModel.Win32Exception)
        {
            ValidationException = error;
            ValidationError = Translations.Translate("FormOpenDirectory", "_warningOpenFailed", "Text", "The selected directory is not a valid git repository.");
            if (!_closing && IsVisible)
            {
                await Dialogs.ShowMessageAsync(this, T("Error"), ValidationError, warning: false, cancellationToken: token);
                if (!_closing && IsVisible) { Dispatcher.UIThread.Post(() => { _editor?.Focus(); _editor?.SelectAll(); }); }
            }
        }
    });
    protected override void UpdateActions()
    {
        bool parent = false;
        try
        {
            string path = _directory.Text ?? "";
            parent = Directory.Exists(path) && Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))) is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        _up.IsEnabled = !_closing && !IsBusy && parent;
    }
}

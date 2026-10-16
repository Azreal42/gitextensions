using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

/// <summary>FormFilePrompt UI infrastructure; no script dispatch or process launch.</summary>
internal sealed class FilePromptDialog : Window
{
    private readonly TextBox _path = ScriptInputDialogControls.Text("txtFilePath");
    private readonly Button _browse;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>> _browseFiles;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private bool _closed;
    internal bool WasAccepted { get; private set; }
    internal string UserInput { get; private set; } = "";
    internal ScriptReply Reply => WasAccepted ? new(ScriptReplyKind.Accepted, UserInput) : new(ScriptReplyKind.Cancelled);
    internal bool IsBrowsing { get; private set; }
    internal Exception? LastBrowseError { get; private set; }
    internal Task BrowseCompletion { get; private set; } = Task.CompletedTask;

    internal FilePromptDialog(TranslationCatalog? translations = null,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? browseFiles = null)
    {
        _token = _lifetime.Token;
        translations ??= TranslationCatalog.LoadDefault();
        string Text(string key, string fallback) => translations.Translate("FormFilePrompt", key, "Text", fallback);
        Name = "FilePromptDialog"; Title = Text("$this", "Select script files");
        Width = 549; Height = MinHeight = MaxHeight = 78; MinWidth = 450; MaxWidth = 800;
        CanMinimize = CanMaximize = false;
        _browseFiles = browseFiles ?? BrowseNativeAsync;
        _path.Height = _path.MinHeight = 21; _path.TabIndex = 1; _path.Margin = new Thickness(3, 0);
        TextBlock label = new() { Name = "lblSelectFiles", Text = Text("lblSelectFiles", "Select file(s)"), Margin = new Thickness(3, 0), VerticalAlignment = VerticalAlignment.Center };
        _browse = new Button { Name = "btnBrowse", Width = 115, MinWidth = 100, Height = 25, MinHeight = 25, Margin = new Thickness(3), Padding = new Thickness(5, 0), TabIndex = 2 };
        using (Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/BrowseFileExplorer.png")))
        {
            _browse.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children =
            {
                new Image { Source = new Bitmap(stream), Width = 16, Height = 16 },
                new AccessText { Text = translations.TranslateAccessKeys("FormFilePrompt", "btnBrowse", "Text", "Browse...") }
            } };
        }
        _browse.Click += (_, _) => { if (!IsBrowsing && !_closed) { BrowseCompletion = BrowseAsync(); } };
        Button accept = ScriptInputDialogControls.Accept("btnOk", translations, "FormFilePrompt", 100, 25);
        accept.Margin = new Thickness(3); accept.TabIndex = 3; accept.HorizontalAlignment = HorizontalAlignment.Stretch;
        accept.Click += (_, _) =>
        {
            if (_closed) { return; }
            string value = _path.Text ?? "";
            if (value.Length > 0) { UserInput = value; WasAccepted = true; }
            Close();
        };
        Grid body = new() { Name = "tableLayoutPanel1", ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("31,31"), Margin = new Thickness(8) };
        body.Children.Add(label); Grid.SetColumn(_path, 1); body.Children.Add(_path);
        Grid.SetColumn(_browse, 2); body.Children.Add(_browse); Grid.SetRow(accept, 1); Grid.SetColumn(accept, 2); body.Children.Add(accept); Content = body;
        ScriptInputDialogControls.Configure(this, _path, () => !_closed);
        KeyDown += (_, args) => { if (!args.Handled && args.Key == Key.Escape) { args.Handled = true; Close(); } };
        Closed += async (_, _) =>
        {
            _closed = true; _lifetime.Cancel();
            try { await BrowseCompletion; } finally { _lifetime.Dispose(); }
        };
    }

    private async Task BrowseAsync()
    {
        IsBrowsing = true; LastBrowseError = null; _browse.IsEnabled = false;
        try
        {
            IReadOnlyList<string> chosen = await _browseFiles(".", _token);
            _token.ThrowIfCancellationRequested();
            if (_closed || chosen.Count == 0) { return; }
            if (chosen.Any(path => !File.Exists(path))) { throw new IOException("Select existing local files."); }
            _path.Text = string.Join(' ', chosen.Select(ScriptInputDialogControls.QuoteFilePath));
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            LastBrowseError = exception;
            if (!_closed) { await Dialogs.ShowErrorAsync(this, exception.Message); }
        }
        finally { IsBrowsing = false; if (!_closed) { _browse.IsEnabled = true; _path.Focus(); } }
    }

    private async Task<IReadOnlyList<string>> BrowseNativeAsync(string initialDirectory, CancellationToken token)
    {
        IStorageFolder? start = await StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(initialDirectory)));
        token.ThrowIfCancellationRequested();
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = Title, AllowMultiple = true, SuggestedStartLocation = start });
        token.ThrowIfCancellationRequested();
        return files.Select(file => file.TryGetLocalPath() ?? throw new IOException("Select existing local files.")).ToArray();
    }
}

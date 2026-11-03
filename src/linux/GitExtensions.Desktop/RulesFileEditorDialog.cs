using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

/// <summary>The three original rule-file editors share FileViewer, save and dirty-close behavior.</summary>
public sealed class RulesFileEditorDialog : RepositoryDialog
{
    private readonly GitRulesFileKind _kind;
    private readonly string _category;
    private readonly DiffView _view;
    private readonly Button _save;
    private readonly Control _statusRow;
    private readonly Action<Uri> _openLink;
    private GitRulesFileSnapshot? _snapshot;
    private bool _allowClose, _askingClose, _changingEncoding;
    private Window? _closingOwner;

    public RulesFileEditorDialog(GitRepository repository, GitRulesFileKind kind, Action<Uri>? openLink = null) : base(repository, "Edit " + FileName(kind))
    {
        _kind = kind;
        _category = kind switch { GitRulesFileKind.Attributes => "FormGitAttributes", GitRulesFileKind.MailMap => "FormMailMap", _ => "FormGitIgnore" };
        _openLink = openLink ?? (uri => LoggedExternalProcess.Start(DesktopResourceLauncher.CreateStartInfo(uri.AbsoluteUri), GitCommandLogProcessKind.OpenResource));
        Name = "RulesFileEditorDialog"; Title = T("Edit " + FileName(kind));
        bool ignore = IsIgnore;
        Width = 634; Height = ignore ? 623 : 474; MinWidth = ignore ? 634 : 500; MinHeight = ignore ? 460 : 320;
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        _statusRow = (Control)Status.Parent!; _statusRow.IsVisible = false; ConstrainStatusHeight("RulesFileError", 100);
        _view = new DiffView(new TextBox()) { Name = "RulesFileViewer", Mode = DiffViewMode.PlainText, SyntaxPath = FileName(kind) };
        _save = Action("SaveRulesFile", "Save", async () => { if (await SaveBufferAsync()) { _allowClose = true; Close(); } });
        _save.Height = ignore ? 27 : 25; _save.MinWidth = ignore ? 160 : 75; _save.Content = Caption("Save", "Save");
        if (ignore) { _save.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { OriginalMenuItems.Icon("Save.png"), Caption("Save", "Save") } }; }
        Grid split = new() { Name = "RulesFileSplit", ColumnDefinitions = new ColumnDefinitions(ignore ? "*,4,270" : "*,4,249"), Margin = new Thickness(ignore ? 4 : 0) };
        DockPanel left = new();
        if (ignore)
        {
            Button defaults = Action("AddDefaultIgnores", "Add default ignores", AddDefaultsAsync);
            defaults.Content = Caption("AddDefault", "Add default ignores"); defaults.MinWidth = 160; defaults.Height = 27;
            Button pattern = Action("AddIgnorePattern", "Add pattern", AddPatternAsync);
            pattern.Content = Caption("AddPattern", "Add pattern"); pattern.MinWidth = 160; pattern.Height = 27;
            StackPanel additions = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(3), Children = { defaults, pattern } };
            DockPanel.SetDock(additions, Dock.Bottom); left.Children.Add(additions);
        }
        left.Children.Add(_view); split.Children.Add(left);
        GridSplitter splitter = new() { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1); split.Children.Add(splitter);
        DockPanel right = new();
        if (ignore)
        {
            StackPanel links = new() { Margin = new Thickness(5, 2, 8, 4), Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right };
            links.Children.Add(Link("lnkGitIgnoreGenerate", "Generate a custom ignore file for git", "https://www.gitignore.io/"));
            links.Children.Add(Link("lnkGitIgnorePatterns", "Example ignore patterns", "https://github.com/github/gitignore"));
            DockPanel.SetDock(links, Dock.Bottom); right.Children.Add(links);
            Button cancel = Action("CancelRulesFile", "Cancel", () => { Close(); return Task.CompletedTask; }); cancel.MinWidth = 75; cancel.Height = 27; cancel.Content = Caption("btnCancel", "Cancel");
            StackPanel footer = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(7, 3), Children = { cancel, _save } };
            Border row = ClassicChrome.Border(footer, default); row.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ClassicMenuBrush"));
            DockPanel.SetDock(row, Dock.Bottom); Root.Children.Add(row);
        }
        else
        {
            StackPanel buttons = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 8, 12, 10), Children = { _save } };
            DockPanel.SetDock(buttons, Dock.Bottom); right.Children.Add(buttons);
        }
        right.Children.Add(new ScrollViewer { Name = "RulesFileHelpScroll", Content = new TextBlock
            { Text = C("label1", OriginalRulesFileContent.Help(_category)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 9, 8, 3) } });
        Grid.SetColumn(right, 2); split.Children.Add(right); Root.Children.Add(split);
        Track(_view);
        _view.ModifiedChanged += (_, _) => UpdateActions();
        _view.FileEncodingChanged += async (_, _) => await EncodingChangedAsync();
        AddHandler(KeyDownEvent, async (_, args) =>
        {
            if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.S)
            { args.Handled = true; if (!IsBusy) { await SaveBufferAsync(); } }
        }, RoutingStrategies.Tunnel);
        Closing += (_, args) =>
        {
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingOwner = Owner as Window; }
            if (_allowClose || IsBusy || !_view.IsModified) { return; }
            args.Cancel = true;
            if (!_askingClose) { _askingClose = true; Dispatcher.UIThread.Post(async () => await ConfirmCloseAsync()); }
        };
        Closed += (_, _) =>
        {
            _view.Dispose(); Window? owner = _closingOwner; _closingOwner = null;
            if (owner is not null) { Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        };
        UpdateActions();
    }

    private bool IsIgnore => _kind is GitRulesFileKind.Ignore or GitRulesFileKind.LocalExclude;
    private static string FileName(GitRulesFileKind kind) => kind switch { GitRulesFileKind.Ignore => ".gitignore", GitRulesFileKind.LocalExclude => ".git/info/exclude", GitRulesFileKind.Attributes => ".gitattributes", GitRulesFileKind.MailMap => ".mailmap", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    private string C(string name, string text) => Translations.Translate(_category, name, "Text", text);
    private AccessText Caption(string name, string text) => new() { Text = Translations.TranslateAccessKeys(_category, name, "Text", text) };
    internal GitRulesFileSnapshot? Snapshot => _snapshot;
    internal DiffView Viewer => _view;

    public override async Task InitializeAsync()
    {
        await RunAsync("Load file", async token =>
        {
            _changingEncoding = true;
            try { await _view.ConfigureRepositoryAsync(Repository, token); await LoadBufferAsync(token); }
            finally { _changingEncoding = false; }
        });
        ShowError();
        if (IsVisible) { _view.FocusEditor(); }
    }

    private async Task LoadBufferAsync(CancellationToken token)
    {
        GitRulesFileSnapshot snapshot = await Repository.GetRulesFileSnapshotAsync(_kind, encodingName: _view.FileEncodingName, cancellationToken: token);
        _snapshot = snapshot; _view.LoadText(snapshot.Text); _view.IsReadOnly = false;
    }

    private async Task<bool> SaveBufferAsync()
    {
        if (IsBusy || _snapshot is null) { return false; }
        if (IsIgnore && !_view.IsModified) { return true; }
        bool saved = false;
        await RunAsync("Save", async token =>
        {
            try
            {
                GitRulesFileSnapshot snapshot = await Repository.SaveRulesFileAsync(_snapshot, _view.Text, token);
                if (_view.Text != snapshot.Text) { _view.Editor.Document.Replace(0, _view.Editor.Document.TextLength, snapshot.Text); }
                _snapshot = snapshot; _view.AcceptChanges(); saved = true;
            }
            finally { MarkRepositoryChanged(); }
        });
        ShowError(); return saved;
    }

    private async Task AddDefaultsAsync()
    {
        if (_snapshot is null || IsBusy) { return; }
        await RunAsync("Add default ignores", async token =>
        {
            string path = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "DefaultIgnorePatterns.txt");
            string[] defaults = File.Exists(path) ? await File.ReadAllLinesAsync(path, GitFileEncodingCodec.GetEncoding(), token) : OriginalRulesFileContent.DefaultIgnorePatterns;
            string[] existing = _view.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            string[] added = defaults.Except(existing, StringComparer.Ordinal).ToArray();
            if (added.Length > 0) { _view.Editor.Document.Insert(_view.Editor.Document.TextLength, _snapshot.NewLine + string.Join(_snapshot.NewLine, added) + _snapshot.NewLine); }
        });
        ShowError();
    }

    private async Task AddPatternAsync()
    {
        if (!await SaveBufferAsync()) { return; }
        await RunAsync("Add pattern", async token =>
        {
            AddToGitIgnoreDialog dialog = new(Repository, _kind == GitRulesFileKind.LocalExclude, ["*.dll"]) { RequestedThemeVariant = ActualThemeVariant };
            dialog.Opened += async (_, _) => await dialog.InitializeAsync();
            using CancellationTokenRegistration registration = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
            try { await dialog.ShowDialog(this); }
            finally { if (dialog.HasRepositoryChanges) { MarkRepositoryChanged(); } }
            token.ThrowIfCancellationRequested(); await LoadBufferAsync(token);
        });
        ShowError();
    }

    private async Task EncodingChangedAsync()
    {
        if (_changingEncoding || IsBusy || !IsVisible) { return; }
        bool reload = !_view.IsModified;
        if (!reload)
        {
            int answer = await AskSaveAsync();
            reload = answer == 2 || answer == 1 && await SaveBufferAsync();
        }
        _changingEncoding = true;
        try
        {
            if (reload)
            {
                await RunAsync("Load file", LoadBufferAsync);
                if (LastError is not null) { _view.FileEncodingName = _snapshot?.EncodingName; }
                ShowError();
            }
            else { _view.FileEncodingName = _snapshot?.EncodingName; }
        }
        finally { _changingEncoding = false; }
    }

    private async Task ConfirmCloseAsync()
    {
        try
        {
            int answer = await AskSaveAsync();
            if (answer == 2 || answer == 1 && await SaveBufferAsync()) { _allowClose = true; Close(); }
            else { _closingOwner = null; }
        }
        finally { _askingClose = false; }
    }

    private async Task<int> AskSaveAsync()
    {
        Window dialog = new() { Name = "SaveRulesFileQuestion", Title = T("Save changes?"), Width = 420,
            SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = ActualThemeVariant };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6 };
        foreach ((string text, int value) in new[] { ("Yes", 1), ("No", 2), ("Cancel", 0) })
        {
            Button button = new() { Name = "RulesSave" + text, Content = T(text), MinWidth = 75, Height = 25, IsDefault = value == 1, IsCancel = value == 0 };
            button.Click += (_, _) => dialog.Close(value); buttons.Children.Add(button);
        }
        dialog.Content = new StackPanel { Margin = new Thickness(12), Spacing = 12, Children =
        { new TextBlock { Text = T("Save changes to " + FileName(_kind) + "?"), TextWrapping = TextWrapping.Wrap }, buttons } };
        return await dialog.ShowDialog<int>(this);
    }

    private Button Link(string name, string label, string url)
    {
        Button link = new() { Name = name, Content = C(name, label), Padding = default, MinHeight = 18, Background = Brushes.Transparent, BorderThickness = default };
        link.Click += (_, _) => _openLink(new Uri(url)); return link;
    }

    private void ShowError() => _statusRow.IsVisible = LastError is not null;
    protected override void UpdateActions()
    {
        _save.IsEnabled = !IsBusy && _snapshot is not null;
        _view.IsReadOnly = _snapshot is null;
    }
}

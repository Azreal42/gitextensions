using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original disk FileViewer editor; its only publication is an explicit verified Save.</summary>
public sealed class FileEditorDialog : Window, IDisposable
{
    internal const long DeferredLength = 5 * 1024 * 1024;
    private readonly string _path, _defaultEncoding;
    private readonly bool _readOnly;
    private readonly int? _initialLine;
    private readonly TranslationCatalog _translations;
    private readonly IEditableFileStore _store;
    private readonly DiffView _view;
    private readonly FileContentView _content;
    private readonly Button _save, _load;
    private readonly TextBlock _error = new() { Name = "FileEditorError", TextWrapping = TextWrapping.Wrap };
    private readonly Border _errorRow;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private bool _started, _allowClose, _closeWhenIdle, _askingClose, _changingEncoding, _askingEncoding, _closed, _deferred, _disposed, _closeAfterEncoding;
    private Window? _closingOwner;
    internal EditableFileSnapshot? Snapshot { get; private set; }
    internal DiffView Viewer => _view;
    internal bool IsBusy { get; private set; }
    public bool HasFileChanges { get; private set; }
    public bool CloseAccepted { get; private set; } = true;
    public string? LastError { get; private set; }
    internal Exception? LastException { get; private set; }
    public Task Initialization { get; private set; } = Task.CompletedTask;
    internal event Action? FileChanged;

    public FileEditorDialog(string fullPath, string defaultEncodingName = "utf-8", bool showWarning = false,
        bool readOnly = false, int? lineNumber = null, TranslationCatalog? translations = null)
        : this(fullPath, defaultEncodingName, showWarning, readOnly, lineNumber, translations, new EditableFileStore())
    {
    }

    internal FileEditorDialog(string fullPath, string defaultEncodingName, bool showWarning, bool readOnly, int? lineNumber,
        TranslationCatalog? translations, IEditableFileStore store)
    {
        _path = Path.GetFullPath(fullPath); _defaultEncoding = GitFileEncodingCodec.GetEncoding(defaultEncodingName).WebName;
        _readOnly = readOnly; _initialLine = lineNumber; _translations = translations ?? TranslationCatalog.LoadDefault(); _store = store;
        Name = "FormEditor"; Title = _path; Width = 659; Height = 543;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        TextBox source = new(); _view = new(source) { Name = "fileViewer", Mode = DiffViewMode.PlainText, SyntaxPath = _path };
        _content = new(source, _view);
        _save = new() { Name = "toolStripSaveButton", Content = OriginalMenuItems.Icon("Save.png"), Width = 24,
            Height = 23, MinWidth = 0, MinHeight = 0, Padding = new Thickness(3), BorderThickness = default };
        ToolTip.SetTip(_save, C("toolStripSaveButton", "ToolTipText", "Save"));
        _save.Click += async (_, _) => await SaveAsync();
        Border toolbar = new() { Name = "toolStrip1", Height = 25, Child = new StackPanel
            { Orientation = Orientation.Horizontal, Children = { _save } } };
        toolbar.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ClassicMenuBrush"));
        Color warningColor = Color.FromRgb(230, 99, 99);
        Border warning = new() { Name = "panelMessage", IsVisible = showWarning, Padding = new Thickness(5), Margin = new Thickness(3),
            Background = new SolidColorBrush(warningColor), Child = new Grid { ColumnDefinitions = new("25,*"), Children =
            { ResetDialogControls.Icon("Warning"), WarningText(warningColor) } } };
        _load = new() { Name = "_NO_TRANSLATE_lblShowPreview", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Content = "", MaxWidth = 580 };
        _load.Click += async (_, _) => await LoadAsync(allowLarge: true, lineNumber);
        _errorRow = ClassicChrome.Border(new ScrollViewer { Content = _error, MaxHeight = 100 }, default, new Thickness(8, 4));
        _errorRow.IsVisible = false;
        Grid body = new() { RowDefinitions = new("25,Auto,*,Auto") };
        body.Children.Add(toolbar); Grid.SetRow(warning, 1); body.Children.Add(warning);
        Grid page = new() { Children = { _content, _load } }; Grid.SetRow(page, 2); body.Children.Add(page);
        Grid.SetRow(_errorRow, 3); body.Children.Add(_errorRow); Content = body;
        _view.ModifiedChanged += (_, _) => UpdateControls();
        _view.FileEncodingChanged += async (_, _) => await ChangeEncodingAsync();
        AddHandler(KeyDownEvent, async (_, args) =>
        {
            if (args.Key == Key.S && args.KeyModifiers == KeyModifiers.Control)
            { args.Handled = true; if (!IsBusy && !_askingClose && !_askingEncoding) { await SaveAsync(); } }
        }, RoutingStrategies.Tunnel);
        _view.Editor.TextArea.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Handled || args.Key != Key.Escape || args.KeyModifiers != KeyModifiers.None
                || !ReferenceEquals(args.Source, _view.Editor.TextArea) || !_view.Editor.TextArea.IsFocused
                || _view.ContextMenu?.IsOpen == true) { return; }
            args.Handled = true;
            if (!_view.Editor.TextArea.Selection.IsEmpty) { _view.Editor.TextArea.ClearSelection(); }
            else { Close(); }
        }, RoutingStrategies.Tunnel);
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && args.KeyModifiers == KeyModifiers.None && _view.ContextMenu?.IsOpen != true)
            { args.Handled = true; Close(); }
        };
        Opened += (_, _) =>
        {
            if (Owner is Window owner) { RequestedThemeVariant = owner.ActualThemeVariant; }
            ModalWindowActivation.AfterMapping(this, () => !_closed && !_closeWhenIdle);
            if (!_started) { Initialization = InitializeAsync(lineNumber); }
            else if (Snapshot is not null) { _view.FocusEditor(); }
        };
        Closing += (_, args) =>
        {
            if (_allowClose) { return; }
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingOwner = Owner as Window; }
            if (IsBusy) { args.Cancel = true; _closeWhenIdle = true; _operation?.Cancel(); return; }
            if (_askingClose) { args.Cancel = true; return; }
            if (_askingEncoding) { args.Cancel = true; _closeAfterEncoding = true; return; }
            if (_view.IsModified && Snapshot?.CanEdit == true && !_readOnly)
            {
                args.Cancel = true; _askingClose = true;
                Dispatcher.UIThread.Post(async () => await ConfirmCloseAsync());
            }
        };
        Closed += (_, _) =>
        {
            Dispose();
            Window? owner = _closingOwner; _closingOwner = null;
            if (owner is not null) { Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        };
        UpdateControls();
    }
    private TextBlock WarningText(Color background)
    {
        TextBlock text = new() { Name = "labelWarning", TextWrapping = TextWrapping.Wrap,
            Text = C("labelWarning", "Text", "Here be dragons!\nChanging this file by hand can be harmful and might break something.\nIf you are not sure just close this window."),
            Foreground = new SolidColorBrush(ClassicTextContrast.AdaptForeground(background, Colors.Black)) };
        Grid.SetColumn(text, 1); return text;
    }
    private string C(string name, string property, string fallback) => _translations.Translate("FormEditor", name, property, fallback);
    public Task InitializeAsync(int? lineNumber = null)
    {
        if (_started) { return Initialization; }
        _started = true; return Initialization = LoadAsync(allowLarge: false, lineNumber ?? _initialLine);
    }
    private async Task LoadAsync(bool allowLarge, int? lineNumber)
    {
        if (IsBusy || _closed) { return; }
        bool loaded = await RunAsync(async token =>
        {
            long length = new FileInfo(_path).Length;
            if (!allowLarge && length > DeferredLength)
            {
                _deferred = true;
                _load.Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = string.Format(CultureInfo.CurrentCulture,
                    _translations.Translate("FileViewer", "_largeFileSizeWarning", "Text", "This file is {0:N1} MB. Showing large files can be slow. Click to show anyway."), length / (1024d * 1024)) };
                return;
            }
            EditableFileSnapshot snapshot = await _store.ReadAsync(_path, _defaultEncoding, token);
            token.ThrowIfCancellationRequested(); ApplySnapshot(snapshot, clearUndo: true);
            if (lineNumber is int line) { _view.GoToFileLine(line); }
        }, C("_cannotOpenFile", "Text", "Cannot open file:"));
        if (!loaded && !_closed && !_closeWhenIdle && LastException is not OperationCanceledException)
        {
            CloseAccepted = false;
            if (IsVisible) { await Dialogs.ShowErrorAsync(this, LastError ?? ""); }
            if (!_closed) { _allowClose = true; Close(); }
        }
        else if (IsVisible && Snapshot is not null) { _view.FocusEditor(); }
    }
    private void ApplySnapshot(EditableFileSnapshot snapshot, bool clearUndo)
    {
        _view.IsReadOnly = true; _deferred = false; _content.ClearImage();
        Snapshot = snapshot; SetEncoding(snapshot.EncodingName);
        _view.DocumentIdentity = _path;
        if (!_content.TryShowImage(_path, snapshot.GetBytes()))
        {
            _view.Mode = DiffViewMode.PlainText; _view.SyntaxPath = snapshot.IsBinary ? null : _path;
            if (clearUndo) { _view.LoadText(snapshot.Text); }
            else { _view.AcceptChanges(); }
        }
        UpdateControls();
    }
    internal async Task<bool> SaveAsync()
    {
        if (IsBusy || _closed || _readOnly || Snapshot?.CanEdit != true || _content.IsImage) { return false; }
        if (!_view.IsModified) { return true; }
        EditableFileSnapshot baseline = Snapshot; string text = _view.Text;
        bool saved = await RunAsync(async token =>
        {
            EditableFileSnapshot next = await _store.SaveAsync(baseline, text, token);
            // Save never replaces the live document or clears undo.
            Snapshot = next; _view.AcceptChanges(); MarkFileChanged();
        }, C("_cannotSaveFile", "Text", "Cannot save file:"));
        if (!saved && LastException is EditableFileWriteException) { MarkFileChanged(); }
        if (!saved && LastException is not OperationCanceledException && IsVisible && !_closeWhenIdle)
        { await Dialogs.ShowErrorAsync(this, LastError ?? ""); }
        return saved;
    }
    private void MarkFileChanged()
    { if (!HasFileChanges) { HasFileChanges = true; FileChanged?.Invoke(); } }
    private void SetEncoding(string? value)
    {
        _changingEncoding = true; try { _view.FileEncodingName = value; } finally { _changingEncoding = false; }
    }
    private async Task ChangeEncodingAsync()
    {
        if (_changingEncoding || _closed || Snapshot is not { } previous) { return; }
        string requested = _view.FileEncodingName ?? _defaultEncoding;
        SetEncoding(previous.EncodingName);
        if (IsBusy || _askingClose || _askingEncoding || !IsVisible) { return; }
        _askingEncoding = true; UpdateControls();
        try
        {
            if (_view.IsModified)
            {
                int answer = await AskSaveAsync();
                if (answer == 0) { _closeAfterEncoding = false; _closingOwner = null; return; }
                if (answer == 1 && !await SaveAsync()) { return; }
            }
            bool loaded = await RunAsync(async token =>
            {
                EditableFileSnapshot next = await _store.ReadAsync(_path, requested, token);
                if (!next.CanEdit) { throw new DecoderFallbackException("The file cannot be decoded without losing bytes. Choose its actual file encoding."); }
                token.ThrowIfCancellationRequested(); ApplySnapshot(next, clearUndo: true);
            }, C("_cannotOpenFile", "Text", "Cannot open file:"));
            if (!loaded && LastException is not OperationCanceledException && IsVisible)
            { await Dialogs.ShowErrorAsync(this, LastError ?? ""); }
        }
        finally
        {
            _askingEncoding = false; UpdateControls();
            if (_closeAfterEncoding) { _closeAfterEncoding = false; Dispatcher.UIThread.Post(Close); }
            else if (IsVisible) { _view.FocusEditor(); }
        }
    }
    private async Task ConfirmCloseAsync()
    {
        try
        {
            int answer = await AskSaveAsync();
            if (answer == 2 || answer == 1 && await SaveAsync())
            { CloseAccepted = answer == 1; _allowClose = true; Close(); }
            else { _closingOwner = null; }
        }
        finally { _askingClose = false; if (!_closed) { UpdateControls(); if (IsVisible) { _view.FocusEditor(); } } }
    }
    private async Task<int> AskSaveAsync()
    {
        Window question = new() { Name = "FileEditorSaveQuestion", Title = C("_saveChangesCaption", "Text", "Save changes"), Width = 420,
            SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = ActualThemeVariant };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6 };
        foreach ((string text, int value) in new[] { ("Yes", 1), ("No", 2), ("Cancel", 0) })
        {
            Button button = new() { Name = "FileEditorSave" + text, Content = _translations.Translate(text), IsDefault = value == 1,
                IsCancel = value == 0, Height = 25, MinWidth = 75 };
            button.Click += (_, _) => question.Close(value); buttons.Children.Add(button);
        }
        question.Content = new StackPanel { Margin = new Thickness(12), Spacing = 12, Children =
            { new TextBlock { Text = C("_saveChanges", "Text", "Do you want to save changes?"), TextWrapping = TextWrapping.Wrap }, buttons } };
        question.Opened += (_, _) =>
        {
            buttons.Children.OfType<Button>().First(button => button.IsDefault).Focus();
            ModalWindowActivation.AfterMapping(question, () => !_closed);
        };
        return await question.ShowDialog<int>(this);
    }
    private async Task<bool> RunAsync(Func<CancellationToken, Task> action, string errorCaption)
    {
        if (IsBusy || _closed) { return false; }
        IsBusy = true; LastException = null; LastError = null; _errorRow.IsVisible = false;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); UpdateControls();
        bool success = false;
        try { await action(_operation.Token); success = true; }
        catch (Exception error)
        {
            LastException = error; LastError = errorCaption + "\n" + error.Message; _error.Text = LastError;
            _errorRow.IsVisible = error is not OperationCanceledException;
        }
        finally
        {
            _operation.Dispose(); _operation = null; IsBusy = false; UpdateControls();
            if (_closeWhenIdle) { Dispatcher.UIThread.Post(() => { _closeWhenIdle = false; Close(); }); }
        }
        return success;
    }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true; _closed = true; _lifetime.Cancel(); _content.Dispose();
    }
    internal void RequestClose()
    { if (!_closed) { _operation?.Cancel(); Close(); } }
    private void UpdateControls()
    {
        bool editable = Snapshot?.CanEdit == true && !_readOnly && !_content.IsImage && !IsBusy;
        _view.IsReadOnly = !editable;
        _save.IsEnabled = editable && _view.IsModified && !_askingEncoding && !_askingClose;
        _load.IsVisible = _deferred; _load.IsEnabled = !IsBusy;
        _content.IsVisible = !_deferred;
        _view.Toolbar.EncodingSelector.IsEnabled = Snapshot is not null && !IsBusy && !_askingClose && !_askingEncoding && !_content.IsImage;
    }
}

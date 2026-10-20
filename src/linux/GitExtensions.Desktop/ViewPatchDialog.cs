using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>Autonomous, read-only FormViewPatch. Owns and drains picker/stream/parser work before closing.</summary>
public sealed class ViewPatchDialog : Window, IDisposable
{
    private readonly TranslationCatalog _translations;
    private readonly string _encoding;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<CancellationTokenSource> _operations = [];
    private readonly TaskCompletionSource _closedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBox _path = new() { Name = "PatchFileNameEdit", MinWidth = 0 };
    private readonly Button _browse = new() { Name = "BrowsePatch", MinWidth = 100 };
    private readonly Button _cancel = new() { Name = "ViewPatchCancelRead", Content = "Cancel reading", IsVisible = false };
    private readonly TextBlock _message = new() { Name = "ViewPatchMessage", TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _files = new() { Name = "GridChangedFiles", SelectionMode = SelectionMode.Single,
        BorderThickness = default };
    private readonly TextBox _source = new() { IsReadOnly = true };
    private readonly DiffView _view;
    private IReadOnlyList<Patch> _patches = [];
    private readonly ObservableCollection<Patch> _rows = [];
    private int _generation;
    private bool _publishing, _closing, _allowClose, _disposed;
    private Window? _closingOwner;

    internal Func<FilePickerOpenOptions, Task<IReadOnlyList<IStorageFile>>> OpenFilePicker { get; set; }
    internal Func<IStorageFile, CancellationToken, Task<Stream>> OpenRead { get; set; }
        = async (file, token) => { token.ThrowIfCancellationRequested(); return await file.OpenReadAsync(); };
    internal DiffView Viewer => _view;
    internal IReadOnlyList<Patch> Patches => _patches;
    public string? LoadedPath { get; private set; }
    public Exception? LastException { get; private set; }
    public bool IsBusy => _operations.Count != 0;
    public Task OperationCompletion { get; private set; } = Task.CompletedTask;
    public Task CloseCompletion => _closedCompletion.Task;

    public ViewPatchDialog(string fileEncodingName = "utf-8", TranslationCatalog? translations = null)
    {
        _encoding = GitFileEncodingCodec.GetEncoding(fileEncodingName).WebName;
        _translations = translations ?? TranslationCatalog.LoadDefault();
        Name = "FormViewPatch"; Title = C("$this", "Text", "View patch file");
        Width = 689; Height = 501; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        DesktopFonts.Apply(this, DesktopFontRole.Application);
        OpenFilePicker = options => StorageProvider.OpenFilePickerAsync(options);
        _view = new(_source) { Name = "ChangesList", IsReadOnly = true, IsFixedPatch = true, FileEncodingName = _encoding, Mode = DiffViewMode.Auto };
        _files.ItemsSource = _rows;
        _files.ItemTemplate = new FuncDataTemplate<Patch>((patch, _) => BuildRow(patch));
        _files.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters =
        { new Setter(TemplatedControl.PaddingProperty, new Thickness(2, 0)), new Setter(Layoutable.MinHeightProperty, 18d),
          new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _files.SelectionChanged += (_, _) => { if (!_publishing) { ShowSelection(); } };
        Grid headers = new() { ColumnDefinitions = new("*,70,50") };
        Header("FileNameA", C("FileNameA", "HeaderText", "Filename"), 0, patch => patch.FileNameA);
        Header("typeDataGridViewTextBoxColumn", C("typeDataGridViewTextBoxColumn", "HeaderText", "Change"), 1, patch => patch.ChangeType.ToString());
        Header("File", C("File", "HeaderText", "Type"), 2, patch => patch.FileType.ToString());
        Grid list = new() { RowDefinitions = new("25,*") }; list.Children.Add(headers);
        Grid.SetRow(_files, 1); list.Children.Add(_files);
        Grid panes = new() { Name = "splitContainer1", RowDefinitions = new("270,4,*") };
        panes.RowDefinitions[0].MinHeight = 45; panes.RowDefinitions[2].MinHeight = 45;
        GridSplitter splitter = new() { Name = "ViewPatchSplitter", ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        panes.Children.Add(ClassicChrome.Border(list, new Thickness(1))); Grid.SetRow(splitter, 1); panes.Children.Add(splitter); Grid.SetRow(_view, 2); panes.Children.Add(_view);
        _browse.Content = C("BrowsePatch", "Text", "Browse");
        _browse.Click += (_, _) => OperationCompletion = BeginReadAsync(async token =>
        {
            using IStorageFolder? directory = await StorageProvider.TryGetFolderFromPathAsync(new Uri(Environment.CurrentDirectory));
            token.ThrowIfCancellationRequested();
            IReadOnlyList<IStorageFile> selected = await OpenFilePicker(new FilePickerOpenOptions
            {
                SuggestedStartLocation = directory,
                Title = C("_patchFileFilterTitle", "Text", "Select patch file"), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(C("_patchFileFilterString", "Text", "Patch file (*.Patch)")) { Patterns = ["*.patch"] }, FilePickerFileTypes.All]
            });
            try
            {
                token.ThrowIfCancellationRequested();
                return selected.Count == 0 ? null : await ReadDocumentAsync(selected[0], token);
            }
            finally { foreach (IStorageFile file in selected) { file.Dispose(); } }
        });
        _path.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None || IsBusy) { return; }
            args.Handled = true; OperationCompletion = LoadPathAsync(_path.Text ?? "");
        };
        _cancel.Click += (_, _) => { foreach (CancellationTokenSource operation in _operations.ToArray()) { operation.Cancel(); } };
        Grid heading = new() { ColumnDefinitions = new("Auto,*,100"), Margin = new Thickness(6, 5) };
        heading.Children.Add(new TextBlock { Name = "labelPatch", Text = C("labelPatch", "Text", "Patch"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
        Grid.SetColumn(_path, 1); heading.Children.Add(_path); Grid.SetColumn(_browse, 2); heading.Children.Add(_browse);
        Grid status = new() { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(6, 2) };
        status.Children.Add(_message); Grid.SetColumn(_cancel, 1); status.Children.Add(_cancel);
        Grid body = new() { RowDefinitions = new("Auto,*,Auto") };
        body.Children.Add(heading); Grid.SetRow(panes, 1); body.Children.Add(panes); Grid.SetRow(status, 2); body.Children.Add(status); Content = body;
        KeyDown += (_, args) =>
        {
            if (args.Handled || args.Key != Key.Escape || args.KeyModifiers != KeyModifiers.None || _view.ContextMenu?.IsOpen == true) { return; }
            args.Handled = true; Close();
        };
        Opened += (_, _) =>
        {
            if (Owner is Window owner) { RequestedThemeVariant = owner.ActualThemeVariant; }
            ModalWindowActivation.AfterMapping(this, () => !_closing && !_disposed);
            _path.Focus();
        };
        Closing += (_, args) =>
        {
            if (_allowClose || _disposed) { return; }
            _closing = true; ++_generation; _lifetime.Cancel();
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingOwner = Owner as Window; }
            if (IsBusy) { args.Cancel = true; UpdateControls(); }
        };
        Closed += (_, _) =>
        {
            Dispose(); _closedCompletion.TrySetResult();
            Window? owner = _closingOwner; _closingOwner = null;
            if (owner is not null) { Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        };

        void Header(string name, string text, int column, Func<Patch, string> key)
        {
            Button button = new() { Name = name, Content = text, Focusable = false, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(3, 0) };
            button.Click += (_, _) => Sort(key, button);
            Grid.SetColumn(button, column); headers.Children.Add(button);
        }
    }

    private string C(string name, string property, string fallback) => _translations.Translate("FormViewPatch", name, property, fallback);
    private static Grid BuildRow(Patch? patch)
    {
        Grid row = new() { ColumnDefinitions = new("*,70,50") };
        string[] values = [patch?.FileNameA ?? "", patch?.ChangeType.ToString() ?? "", patch?.FileType.ToString() ?? ""];
        for (int index = 0; index < values.Length; ++index)
        {
            TextBlock cell = new() { Text = values[index], TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0) };
            Grid.SetColumn(cell, index); row.Children.Add(cell);
        }
        return row;
    }

    // Files are owned until the read has drained, including cancellation and late picker responses.
    internal Task LoadAsync(IStorageFile file)
    {
        if (_closing || _disposed) { file.Dispose(); return Task.CompletedTask; }
        return OperationCompletion = BeginReadAsync(async token =>
        {
            using (file) { return await ReadDocumentAsync(file, token); }
        });
    }

    public Task LoadPathAsync(string path) => OperationCompletion = BeginReadAsync(async token =>
    {
        string fullPath = Path.GetFullPath(path);
        using IStorageFile file = await StorageProvider.TryGetFileFromPathAsync(new Uri(fullPath))
            ?? throw new FileNotFoundException("Cannot open patch file.", fullPath);
        return await ReadDocumentAsync(file, token);
    });

    private async Task<Document> ReadDocumentAsync(IStorageFile file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using Stream stream = await OpenRead(file, token);
        token.ThrowIfCancellationRequested();
        // Same initial decoding contract as File.ReadAllText(path, LosslessEncoding): Latin1,
        // with StreamReader BOM detection, then the linked parser's FilesEncoding conversion.
        using StreamReader reader = new(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string text = await reader.ReadToEndAsync(token);
        IReadOnlyList<Patch> patches = await Task.Run(() => GitPatchPreview.Parse(text, _encoding, token), token);
        token.ThrowIfCancellationRequested();
        return new(file.TryGetLocalPath() ?? file.Path.ToString(), patches);
    }

    private async Task BeginReadAsync(Func<CancellationToken, Task<Document?>> read)
    {
        if (_closing || _disposed) { return; }
        int generation = ++_generation;
        foreach (CancellationTokenSource previous in _operations.ToArray()) { previous.Cancel(); }
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operations.Add(operation); LastException = null; _message.Text = "Reading patch…"; UpdateControls();
        try
        {
            Document? next = await read(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_closing || generation != _generation) { return; }
            if (next is null) { _message.Text = ""; return; }
            _publishing = true;
            try
            {
                _patches = next.Patches; LoadedPath = next.Path; _path.Text = next.Path;
                _rows.Clear(); foreach (Patch patch in _patches) { _rows.Add(patch); }
                _files.SelectedIndex = _patches.Count == 0 ? -1 : 0;
            }
            finally { _publishing = false; }
            ShowSelection(); _message.Text = _patches.Count == 0 ? "The patch file is empty." : "";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { if (!_closing && generation == _generation) { _message.Text = "Reading cancelled."; } }
        catch (Exception error)
        {
            if (!_closing && generation == _generation) { LastException = error; _message.Text = error.Message + (LoadedPath is null ? "" : "\nPrevious preview retained: " + LoadedPath); }
        }
        finally
        {
            _operations.Remove(operation); UpdateControls();
            if (_closing && !IsBusy && IsVisible) { _allowClose = true; Close(); }
        }
    }

    private void Sort(Func<Patch, string> key, Button header)
    {
        if (_closing || _patches.Count == 0) { return; }
        Patch? selected = _files.SelectedItem as Patch;
        bool descending = Equals(header.Tag, true); header.Tag = !descending;
        Patch[] ordered = descending ? _patches.OrderByDescending(key, StringComparer.Ordinal).ToArray()
            : _patches.OrderBy(key, StringComparer.Ordinal).ToArray();
        _publishing = true;
        try
        {
            for (int target = 0; target < ordered.Length; ++target)
            {
                if (ReferenceEquals(ordered[target], selected))
                {
                    // Avalonia 11.3.22 treats Move of the selected item as remove/add.
                    // Move only its neighbours, so the native selection never loses this Patch.
                    while (_rows.IndexOf(selected!) > target) { _rows.Move(target, _rows.IndexOf(selected!)); }
                }
                else
                {
                    int current = _rows.IndexOf(ordered[target]);
                    if (current != target) { _rows.Move(current, target); }
                }
            }
        }
        finally { _publishing = false; }
        // A header belongs to this grid's interaction. Restore its selected native row,
        // without reloading the preview or requiring a user click before the next arrow.
        if (selected is not null && ReferenceEquals(_files.SelectedItem, selected))
        {
            _files.ScrollIntoView(selected); _files.UpdateLayout();
            if (_files.ContainerFromIndex(_files.SelectedIndex) is Control { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } row
                && ReferenceEquals(row.DataContext, selected))
            { row.Focus(); }
        }
    }

    private void ShowSelection()
    {
        Patch? selected = _files.SelectedItem as Patch;
        _view.DocumentIdentity = selected is null ? LoadedPath : LoadedPath + "\n" + _patches.ToList().IndexOf(selected);
        _view.SyntaxPath = selected?.FileNameB ?? selected?.FileNameA;
        _view.Mode = DiffViewMode.Auto; _source.Text = selected?.Text ?? ""; _view.AcceptChanges();
    }
    private void UpdateControls()
    {
        _path.IsEnabled = _browse.IsEnabled = !_closing && !IsBusy;
        _cancel.IsVisible = IsBusy; _cancel.IsEnabled = !_closing && IsBusy;
    }
    public void Dispose()
    {
        if (_disposed) { return; }
        if (IsBusy) { Close(); return; }
        _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); _view.Dispose();
    }
    private sealed record Document(string Path, IReadOnlyList<Patch> Patches);
}

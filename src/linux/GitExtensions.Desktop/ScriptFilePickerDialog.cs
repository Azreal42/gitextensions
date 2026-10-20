using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Dialogs;
using Avalonia.Dialogs.Internal;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>A script-owned picker: cancellation closes the actual child before preparation can finish.</summary>
internal sealed class ScriptFilePickerDialog : Window
{
    // These public view/control types belong to the pinned Avalonia.Dialogs package.
    // Keep their use here: StorageProvider cannot accept a cancellation token.
    private readonly ManagedFileChooserViewModel _model;
    private string[] _files = [];
    private TextBox? _fileName;
    private Button? _open;
    private bool _accepting;
    private Task _acceptance = Task.CompletedTask;
    private readonly string _invalidFiles;

    private ScriptFilePickerDialog(Window owner, string title, string initialDirectory, TranslationCatalog translations)
    {
        Name = "ScriptFilePickerDialog";
        _invalidFiles = translations.Translate("ScriptFilePickerDialog", "InvalidFiles", "Text", "Select existing local files.");
        Title = title;
        Width = 800; Height = 480; MinWidth = 600; MinHeight = 360;
        ShowInTaskbar = false; CanMinimize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RequestedThemeVariant = owner.ActualThemeVariant;
        _model = new ManagedFileChooserViewModel(new FilePickerOpenOptions { Title = title, AllowMultiple = true },
            new ManagedFileDialogOptions { AllowDirectorySelection = false });
        try { _model.Navigate(Path.GetFullPath(initialDirectory)); }
        catch { _model.Cancel(); throw; }
        foreach ((string resource, string fallback) in new[]
        {
            ("FileNameWatermark", "File name"), ("ShowHiddenFilesText", "Show hidden files"),
            ("OkText", "Open"), ("CancelText", "Cancel"), ("NameColumn", "Name"),
            ("DateModifiedColumn", "Date modified"), ("TypeColumn", "Type"), ("SizeColumn", "Size")
        })
        {
            Resources["StringManagedFileChooser" + resource] = translations.Translate("ScriptFilePickerDialog", resource, "Text", fallback);
        }
        DataContext = _model;
        ManagedFileChooser chooser = new() { Name = "ScriptFileChooser" };
        chooser.TemplateApplied += (_, args) =>
        {
            // Avalonia's open-mode command ignores edits in FileName and returns
            // SelectedItems instead. Bind acceptance to the actual field here.
            _fileName = chooser.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name != "Location");
            _open = args.NameScope.Get<UniformGrid>("Finalize").Children.OfType<Button>().First();
            _open.Command = null;
            _open.IsDefault = true;
            _open.Click += async (_, _) => await (_acceptance = OpenSelectionAsync());
            _fileName.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateOpen(); } };
            _fileName.KeyDown += async (_, key) =>
            {
                if (key.Key == Key.Enter && !key.Handled) { key.Handled = true; await (_acceptance = OpenSelectionAsync()); }
            };
            UpdateOpen();
        };
        Content = chooser;
        _model.CompleteRequested += paths => { _files = paths; Close(); };
        _model.CancelRequested += Close;
        Closed += (_, _) => _model.Cancel();
        KeyDown += (_, args) => { if (!args.Handled && args.Key == Key.Escape) { args.Handled = true; Close(); } };
        Opened += (_, _) => ModalWindowActivation.AfterMapping(this, () => IsVisible);
    }

    private void UpdateOpen()
    { if (_open is not null) { _open.IsEnabled = !_accepting && !string.IsNullOrEmpty(_fileName?.Text); } }

    private async Task OpenSelectionAsync()
    {
        if (_accepting || !IsVisible) { return; }
        _accepting = true; UpdateOpen();
        try
        {
            string text = _fileName?.Text ?? "";
            if (text.Length == 0) { return; }
            string location = Path.GetFullPath(_model.Location ?? ".");
            string whole = Path.GetFullPath(text, location);
            if (Directory.Exists(whole)) { _model.Navigate(whole); _model.FileName = ""; return; }
            string selectedText = _model.SelectedItems.Count == 1 ? _model.SelectedItems[0].DisplayName ?? ""
                : string.Join(' ', _model.SelectedItems.Select(item => '"' + item.DisplayName + '"'));
            string[] paths = text == selectedText && _model.SelectedItems.Count > 0
                ? _model.SelectedItems.Select(item => item.Path!).ToArray()
                : File.Exists(whole) ? [whole] : ParseFileNames(text).Select(name => Path.GetFullPath(name, location)).ToArray();
            if (paths.Length == 0 || paths.Any(path => !File.Exists(path))) { throw new IOException(_invalidFiles); }
            _files = paths; Close();
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { if (IsVisible) { await Dialogs.ShowErrorAsync(this, _invalidFiles); } }
        finally { _accepting = false; if (IsVisible) { UpdateOpen(); } }
    }

    private static IEnumerable<string> ParseFileNames(string text)
    {
        // No shell syntax, expansion or globbing. An existing literal whole name
        // wins before this Windows-style list of quoted names is interpreted.
        List<string> names = []; StringBuilder name = new(); bool quoted = false;
        for (int index = 0; index < text.Length; index++)
        {
            char ch = text[index];
            if (ch == '\\' && index + 1 < text.Length && text[index + 1] == '"') { name.Append('"'); index++; }
            else if (ch == '"') { quoted = !quoted; }
            else if (char.IsWhiteSpace(ch) && !quoted)
            { if (name.Length > 0) { names.Add(name.ToString()); name.Clear(); } }
            else { name.Append(ch); }
        }
        if (quoted) { throw new IOException("Unclosed file name quote."); }
        if (name.Length > 0) { names.Add(name.ToString()); }
        return names;
    }

    internal static async Task<IReadOnlyList<string>> PickAsync(Window owner, string title, string initialDirectory, CancellationToken token, TranslationCatalog? translations = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        token.ThrowIfCancellationRequested();
        ScriptFilePickerDialog picker = new(owner, title, initialDirectory, translations ?? TranslationCatalog.LoadDefault());
        try
        {
            using CancellationTokenRegistration close = token.Register(() => Dispatcher.UIThread.Post(picker.Close));
            token.ThrowIfCancellationRequested();
            await picker.ShowDialog(owner);
            token.ThrowIfCancellationRequested();
            return picker._files;
        }
        finally
        {
            picker.Close();
            picker._model.Cancel();
            await picker._acceptance;
        }
    }
}

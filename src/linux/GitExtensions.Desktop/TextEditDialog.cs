using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original FormEdit text viewer: in-memory editing, with no returned value or save workflow.</summary>
public sealed class TextEditDialog : Window, IDisposable
{
    private bool _disposed;
    internal FileHistoryContentView Viewer { get; }

    public bool IsReadOnly
    {
        get => Viewer.TextView.IsReadOnly;
        set => Viewer.TextView.IsReadOnly = value;
    }

    public TextEditDialog(string text, string filename = "", TranslationCatalog? translations = null)
        : this(text, filename, translations, "Viewer")
    {
    }

    internal TextEditDialog(string text, string filename, TranslationCatalog? translations, string viewerName)
    {
        // Name is immutable once the viewer is styled through this window's Content.
        Viewer = new() { Name = viewerName };
        TranslationCatalog catalog = translations ?? TranslationCatalog.LoadDefault();
        Name = "FormEdit"; Title = catalog.Translate("FormEdit", "$this", "Text", "View");
        Width = 733; Height = 571; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Viewer.ShowDiff(text, filename); Viewer.TextView.Mode = DiffViewMode.PlainText;
        Viewer.TextView.LoadText(text); IsReadOnly = false;
        Content = Viewer;
        Opened += (_, _) =>
        {
            if (Owner is Window owner) { RequestedThemeVariant = owner.ActualThemeVariant; }
            ModalWindowActivation.AfterMapping(this, () => !_disposed);
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed && IsVisible && IsEffectivelyEnabled) { Viewer.TextView.FocusEditor(); }
            }, DispatcherPriority.Loaded);
        };
        // FileViewer consumes Escape before the form: clear its selection first.
        // AvaloniaEdit handles this key internally, so relay it at the focused text area.
        Viewer.TextView.Editor.TextArea.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Handled || args.Key != Key.Escape || args.KeyModifiers != KeyModifiers.None
                || !ReferenceEquals(args.Source, Viewer.TextView.Editor.TextArea) || !Viewer.TextView.Editor.TextArea.IsFocused
                || Viewer.TextView.ContextMenu?.IsOpen == true) { return; }
            args.Handled = true;
            if (!Viewer.TextView.Editor.TextArea.Selection.IsEmpty) { Viewer.TextView.Editor.TextArea.ClearSelection(); }
            else { Close(); }
        }, RoutingStrategies.Tunnel);
        KeyDown += (_, args) =>
        {
            if (!args.Handled && args.Key == Key.Escape && args.KeyModifiers == KeyModifiers.None)
            { args.Handled = true; Close(); }
        };
        Closed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true; Viewer.Dispose();
    }
}

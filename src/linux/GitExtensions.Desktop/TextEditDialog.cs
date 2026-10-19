using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original FormEdit text viewer: in-memory editing, with no returned value or save workflow.</summary>
public sealed class TextEditDialog : Window, IDisposable
{
    private bool _disposed;
    internal FileHistoryContentView Viewer { get; } = new() { Name = "Viewer" };

    public bool IsReadOnly
    {
        get => Viewer.TextView.IsReadOnly;
        set => Viewer.TextView.IsReadOnly = value;
    }

    public TextEditDialog(string text, string filename = "", TranslationCatalog? translations = null)
    {
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
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; Close(); }
        };
        Closed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true; Viewer.Dispose();
    }
}

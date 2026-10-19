using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

/// <summary>The selectable, nonmodal information sheet used by the Windows Settings pages.</summary>
internal sealed class SimpleHelpDisplayDialog : Window
{
    private readonly TextBox _text = new()
    {
        Name = "textBox1", IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, TabIndex = 0,
        // Windows HideSelection hides the inactive highlight without discarding the range.
        ClearSelectionOnLostFocus = false, IsInactiveSelectionHighlightEnabled = false
    };

    internal SimpleHelpDisplayDialog(string textControlName = "textBox1")
    {
        Name = "SimpleHelpDisplayDialog"; _text.Name = textControlName;
        Width = 299; Height = 439;
        CanMinimize = CanMaximize = false; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _text.Bind(TextBox.BackgroundProperty, new DynamicResourceExtension("ClassicTooltipBrush"));
        _text.Bind(TextBox.ForegroundProperty, new DynamicResourceExtension("ClassicTooltipTextBrush"));
        ScrollViewer.SetHorizontalScrollBarVisibility(_text, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_text, ScrollBarVisibility.Visible);
        Content = _text;
        Opened += (_, _) =>
        {
            Title = DialogTitle ?? "";
            _text.Text = ContentText;
            _text.Focus();
            _text.CaretIndex = _text.SelectionStart = _text.SelectionEnd = 0;
            ModalWindowActivation.AfterMapping(this, () => IsVisible);
        };
    }

    internal string? DialogTitle { get; set; }
    internal string? ContentText { get; set; }
}

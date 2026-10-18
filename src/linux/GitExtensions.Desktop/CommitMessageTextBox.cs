using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GitExtensions.Linux.Desktop;

/// <summary>The native editor still owns IME, clipboard and undo. Formatting must not run inside native undo/redo.</summary>
internal sealed class CommitMessageTextBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);
    internal long InteractionVersion { get; private set; }
    protected override void OnTextInput(TextInputEventArgs args) { InteractionVersion++; base.OnTextInput(args); }
    protected override void OnPointerPressed(PointerPressedEventArgs args) { InteractionVersion++; base.OnPointerPressed(args); }
    protected override void OnLostFocus(RoutedEventArgs args) { InteractionVersion++; base.OnLostFocus(args); }
    internal bool IsReplaying { get; private set; }
    protected override void OnKeyDown(KeyEventArgs args)
    {
        InteractionVersion++;
        bool replay = args.KeyModifiers == KeyModifiers.Control && args.Key is Key.Z or Key.Y
            || args.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && args.Key == Key.Z;
        if (replay) { IsReplaying = true; }
        try { base.OnKeyDown(args); }
        finally { if (replay) { IsReplaying = false; } }
    }
}

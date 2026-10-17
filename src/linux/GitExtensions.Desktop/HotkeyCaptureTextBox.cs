using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

/// <summary>TextboxHotkey captures logical keys, including dialog/navigation keys.</summary>
internal sealed class HotkeyCaptureTextBox : TextBox
{
    private readonly Func<KeyGesture?, bool> _isAssigned;
    private readonly string _noneText;
    private KeyGesture? _capturedGesture;

    internal HotkeyCaptureTextBox(Func<KeyGesture?, bool>? isAssigned = null, string noneText = "None")
    {
        _isAssigned = isAssigned ?? (_ => false); _noneText = noneText;
        IsReadOnly = true; AcceptsReturn = false; AcceptsTab = false;
        AddHandler(KeyDownEvent, CaptureKey, RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += (_, _) => RefreshAssignmentWarning();
        CapturedGesture = null;
    }

    protected override Type StyleKeyOverride => typeof(TextBox);
    internal event Action? CapturedGestureChanged;
    internal bool HasAssignmentWarning { get; private set; }
    internal KeyGesture? CapturedGesture
    {
        get => _capturedGesture;
        set
        {
            bool changed = !Equals(_capturedGesture, value);
            _capturedGesture = value;
            Text = value?.ToString() ?? _noneText;
            RefreshAssignmentWarning();
            if (changed) { CapturedGestureChanged?.Invoke(); }
        }
    }

    internal void RefreshAssignmentWarning()
    {
        HasAssignmentWarning = _isAssigned(_capturedGesture);
        if (HasAssignmentWarning)
        {
            Color background = Background is ISolidColorBrush brush ? brush.Color : Colors.White;
            Foreground = new SolidColorBrush(ClassicTextContrast.AdaptForeground(background, Colors.Red));
        }
        else { ClearValue(ForegroundProperty); }
    }

    private void CaptureKey(object? sender, KeyEventArgs args)
    {
        // Windows ProcessCmdKey swallows every key, but does not store a modifier
        // on its own. In particular Escape and Tab must never reach the dialog.
        args.Handled = true;
        if (args.Key is Key.None or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) { return; }
        CapturedGesture = new(args.Key, args.KeyModifiers);
    }

    protected override void OnKeyDown(KeyEventArgs args) { args.Handled = true; }
    protected override void OnKeyUp(KeyEventArgs args) { args.Handled = true; }
    protected override void OnTextInput(TextInputEventArgs args) { args.Handled = true; }
}

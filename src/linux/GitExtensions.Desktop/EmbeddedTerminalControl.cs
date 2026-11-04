using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaTerminal;
using TerminalKey = XTerm.Input.Key;
using TerminalModifiers = XTerm.Input.KeyModifiers;

namespace GitExtensions.Linux.Desktop;

/// <summary>
/// Adapt the pinned Avalonia 11 renderer's key-up-only input to Avalonia text input
/// (keyboard layouts/composition) and key-down (control keys and key repeat).
/// </summary>
internal sealed class EmbeddedTerminalControl : TerminalControl
{
    private bool _suppressTextInput;
    internal event Action<string>? InputError;

    protected override void OnTextInput(TextInputEventArgs args)
    {
        if (!_suppressTextInput && args.Text is { Length: > 0 }) { Send(args.Text); }
        args.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs args)
    {
        // The upstream handler emits printable characters again on KeyUp, including after Ctrl+C.
        _suppressTextInput = false;
        args.Handled = true;
    }

    protected override async void OnKeyDown(KeyEventArgs args)
    {
        _suppressTextInput = false;
        if (Model is null) { return; }
        KeyModifiers modifiers = args.KeyModifiers;
        bool clipboard = OperatingSystem.IsMacOS() ? modifiers == KeyModifiers.Meta
            : modifiers == (KeyModifiers.Control | KeyModifiers.Shift);
        if (clipboard && args.Key is Key.C or Key.V || args.Key == Key.Insert && modifiers == KeyModifiers.Shift)
        {
            args.Handled = _suppressTextInput = true;
            try
            {
                if (args.Key == Key.C) { await CopySelectionAsync(); }
                else if (TopLevel.GetTopLevel(this)?.Clipboard is { } source)
                {
#pragma warning disable CS0618 // Avalonia 11 clipboard text API.
                    string? text = await source.GetTextAsync();
#pragma warning restore CS0618
                    if (text is not null) { PasteText(text); }
                }
            }
            catch (Exception error) { InputError?.Invoke(error.Message); }
            return;
        }
        if (modifiers == KeyModifiers.Shift && args.Key is Key.PageUp or Key.PageDown)
        {
            if (args.Key == Key.PageUp) { Model.PageUp(); } else { Model.PageDown(); }
            args.Handled = _suppressTextInput = true;
            return;
        }
        if ((modifiers & KeyModifiers.Control) != 0 && (modifiers & (KeyModifiers.Alt | KeyModifiers.Meta)) == 0)
        {
            int code = args.Key switch
            {
                >= Key.A and <= Key.Z => args.Key - Key.A + 1,
                Key.Space or Key.D2 => 0, Key.OemOpenBrackets => 27, Key.OemBackslash => 28,
                Key.OemCloseBrackets => 29, Key.D6 => 30, Key.OemMinus => 31, _ => -1
            };
            if (code >= 0)
            {
                Send(((char)code).ToString()); args.Handled = _suppressTextInput = true; return;
            }
        }
        if (args.Key == Key.Enter)
        {
            Send("\r"); args.Handled = _suppressTextInput = true; return;
        }
        string? keyName = args.Key switch
        {
            Key.Back => "Backspace", Key.Up => "UpArrow", Key.Down => "DownArrow", Key.Left => "LeftArrow", Key.Right => "RightArrow",
            Key.Escape or Key.Tab or Key.Home or Key.End or Key.Insert or Key.Delete or Key.PageUp or Key.PageDown => args.Key.ToString(),
            >= Key.F1 and <= Key.F24 => args.Key.ToString(), _ => null
        };
        if (keyName is not null && Enum.TryParse(keyName, out TerminalKey key))
        {
            TerminalModifiers mapped = TerminalModifiers.None;
            if (modifiers.HasFlag(KeyModifiers.Shift)) { mapped |= TerminalModifiers.Shift; }
            if (modifiers.HasFlag(KeyModifiers.Control)) { mapped |= TerminalModifiers.Control; }
            if (modifiers.HasFlag(KeyModifiers.Alt)) { mapped |= TerminalModifiers.Alt; }
            Send(Model.Terminal.Engine.GenerateKeyInput(key, mapped));
            args.Handled = _suppressTextInput = true;
            return;
        }
        if (modifiers == KeyModifiers.Alt && Model.OptionAsMetaKey && args.KeySymbol is { Length: > 0 } symbol)
        {
            Send("\u001b" + symbol); args.Handled = _suppressTextInput = true;
        }
        // Printable text, including macOS Option combinations and IME commits, arrives in TextInput.
    }

    internal void PasteText(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        Send(Model?.Terminal.Engine.BracketedPasteMode == true
            ? "\u001b[200~" + text.Replace("\u001b", "", StringComparison.Ordinal) + "\u001b[201~"
            : text.Replace('\n', '\r'));
    }

    private void Send(string? text)
    {
        if (string.IsNullOrEmpty(text) || Model is null) { return; }
        Model.ClearSelection(); Model.Send(text);
    }
}

using Avalonia.Input;

namespace GitExtensions.Linux.Desktop;

/// <summary>An editable draft. Null is an explicit unassignment, not a missing default.</summary>
internal sealed class HotkeyBindings
{
    private const KeyModifiers AllowedModifiers = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta;
    private readonly Dictionary<(string Context, int Code), KeyGesture?> _gestures = [];

    public HotkeyBindings() => ResetToDefaults();

    public HotkeyBindings Clone()
    {
        HotkeyBindings result = new();
        foreach (KeyValuePair<(string Context, int Code), KeyGesture?> entry in _gestures)
        {
            result._gestures[entry.Key] = entry.Value;
        }

        return result;
    }

    public KeyGesture? Get(string context, string name)
    {
        HotkeyCommand? command = HotkeyCatalog.Find(context, name);
        return command is null ? null : Get(context, command.Code);
    }

    public KeyGesture? Get(string context, int code)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _gestures.GetValueOrDefault((context, code));
    }

    public void Set(string context, int code, KeyGesture? gesture)
    {
        if (HotkeyCatalog.Find(context, code) is null)
        {
            throw new ArgumentException($"Unknown hotkey command '{context}:{code}'.", nameof(code));
        }

        ValidateGesture(gesture);
        _gestures[(context, code)] = gesture;
    }

    public void ResetToDefaults()
    {
        _gestures.Clear();
        foreach (string context in HotkeyCatalog.Contexts)
        {
            foreach (HotkeyCommand command in HotkeyCatalog.For(context))
            {
                _gestures.Add((context, command.Code), command.DefaultGesture);
            }
        }
    }

    /// <summary>Only resolves a binding; the caller owns focus, capability and handled-event policy.</summary>
    public HotkeyCommand? Match(string context, KeyEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (HotkeyCommand command in HotkeyCatalog.For(context))
        {
            KeyGesture? gesture = Get(context, command.Code);
            if (gesture is not null && gesture.Key == args.Key && gesture.KeyModifiers == args.KeyModifiers)
            {
                return command;
            }
        }

        return null;
    }

    /// <summary>Global advisory warning, including the command's own current assignment.</summary>
    public bool IsAssigned(KeyGesture? gesture)
        => gesture is not null && _gestures.Values.Any(value => value is not null && value.Key == gesture.Key && value.KeyModifiers == gesture.KeyModifiers);

    private static void ValidateGesture(KeyGesture? gesture)
    {
        if (gesture is null)
        {
            return;
        }

        if (!Enum.IsDefined(gesture.Key) || gesture.Key == Key.None || (gesture.KeyModifiers & ~AllowedModifiers) != 0)
        {
            throw new ArgumentException("A hotkey requires a logical key and only Control, Shift, Alt or Meta modifiers. Use null to clear it.", nameof(gesture));
        }
    }
}

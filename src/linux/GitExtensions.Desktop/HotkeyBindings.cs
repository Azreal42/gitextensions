using Avalonia.Input;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

/// <summary>An editable draft. Null is an explicit unassignment, not a missing default.</summary>
internal sealed class HotkeyBindings
{
    private const KeyModifiers AllowedModifiers = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta;
    private readonly Dictionary<(string Context, int Code), KeyGesture?> _gestures = [];

    private IReadOnlyList<HotkeyCommand> _scripts = [];

    public HotkeyBindings(IEnumerable<ScriptDefinition>? scripts = null)
    {
        UpdateScripts(scripts ?? []);
        ResetToDefaults();
    }

    // Names are editable, command identities are not. Keep bindings for temporarily
    // absent script IDs so another Browse cannot erase them during an unrelated save.
    internal void UpdateScripts(IEnumerable<ScriptDefinition> scripts)
    {
        _scripts = Array.AsReadOnly(scripts.Where(script => !string.IsNullOrEmpty(script.Name))
            .Select(script => new HotkeyCommand("Scripts", script.HotkeyCommandIdentifier, script.GetDisplayName(), null)).ToArray());
        foreach (HotkeyCommand command in _scripts) { _gestures.TryAdd(("Scripts", command.Code), null); }
    }

    internal IReadOnlyList<HotkeyCommand> Commands(string context) => context == "Scripts" ? _scripts : HotkeyCatalog.For(context);
    internal IEnumerable<(string Context, int Code, KeyGesture? Gesture)> StoredBindings
        => _gestures.Select(pair => (pair.Key.Context, pair.Key.Code, pair.Value));

    internal void SetStoredScript(int code, KeyGesture? gesture)
    {
        if (code <= ScriptConfiguration.MinimumUserScriptId && !_scripts.Any(command => command.Code == code)) { return; }
        ValidateGesture(gesture); _gestures[("Scripts", code)] = gesture;
    }

    public HotkeyBindings Clone()
    {
        HotkeyBindings result = new() { _scripts = _scripts };
        foreach (KeyValuePair<(string Context, int Code), KeyGesture?> entry in _gestures)
        {
            result._gestures[entry.Key] = entry.Value;
        }

        return result;
    }

    public KeyGesture? Get(string context, string name)
    {
        HotkeyCommand? command = Commands(context).FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.Ordinal));
        return command is null ? null : Get(context, command.Code);
    }

    public KeyGesture? Get(string context, int code)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _gestures.GetValueOrDefault((context, code));
    }

    public void Set(string context, int code, KeyGesture? gesture)
    {
        if (!Commands(context).Any(command => command.Code == code))
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
            foreach (HotkeyCommand command in Commands(context))
            {
                _gestures.Add((context, command.Code), command.DefaultGesture);
            }
        }
    }

    /// <summary>Only resolves a binding; the caller owns focus, capability and handled-event policy.</summary>
    public HotkeyCommand? Match(string context, KeyEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (HotkeyCommand command in Commands(context))
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

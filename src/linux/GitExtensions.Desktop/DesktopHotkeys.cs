using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

/// <summary>One configuration snapshot supplies both dispatch and displayed gestures.</summary>
internal static class DesktopHotkeys
{
    private sealed record Snapshot(HotkeyBindings Bindings, Exception? Error);
    private sealed record DisplayBinding(WeakReference<Control> Control, string FilePath, string Context, string Command, string? Caption);
    private static readonly Dictionary<string, Snapshot> Snapshots = new(StringComparer.Ordinal);
    private static readonly List<DisplayBinding> Displays = [];

    static DesktopHotkeys() => HotkeyPreferencesStore.Saved += Saved;

    internal static HotkeyBindings Current => Read(new HotkeyPreferencesStore()).Bindings;
    internal static Exception? LoadError => Read(new HotkeyPreferencesStore()).Error;
    internal static KeyGesture? Get(string context, string command) => Current.Get(context, command);
    internal static HotkeyCommand? Match(string context, KeyEventArgs args) => Current.Match(context, args);

    internal static HotkeyBindings Reload()
    {
        HotkeyPreferencesStore store = new();
        Snapshot next;
        lock (Snapshots)
        {
            next = Load(store, Snapshots.GetValueOrDefault(store.FilePath));
            Snapshots[store.FilePath] = next;
        }
        UpdateDisplays(store.FilePath, next.Bindings);
        return next.Bindings;
    }

    internal static void Bind(MenuItem item, string context, string command)
        => BindDisplay(item, context, command, null);

    internal static void BindToolTip(Control control, string context, string command, string caption)
        => BindDisplay(control, context, command, caption);

    private static void BindDisplay(Control control, string context, string command, string? caption)
    {
        HotkeyPreferencesStore store = new();
        DisplayBinding binding = new(new(control), store.FilePath, context, command, caption);
        lock (Displays)
        {
            Displays.RemoveAll(value => !value.Control.TryGetTarget(out Control? target)
                || ReferenceEquals(target, control) && (value.Caption is null) == (caption is null));
            Displays.Add(binding);
        }
        Apply(binding, control, Read(store).Bindings);
    }

    private static Snapshot Read(HotkeyPreferencesStore store)
    {
        lock (Snapshots)
        {
            if (!Snapshots.TryGetValue(store.FilePath, out Snapshot? snapshot))
            { snapshot = Load(store, null); Snapshots.Add(store.FilePath, snapshot); }
            return snapshot;
        }
    }

    private static Snapshot Load(HotkeyPreferencesStore store, Snapshot? previous)
    {
        try { return new(store.Load(), null); }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable file never becomes a silent overwrite. Keep the active
            // snapshot, expose the error to Settings, and let its explicit save fail.
            return new(previous?.Bindings ?? new HotkeyBindings(), error);
        }
    }

    private static void Saved(string filePath)
    {
        Snapshot next;
        lock (Snapshots)
        {
            // Save has already validated and atomically replaced this exact file.
            string directory = Path.GetDirectoryName(Path.GetDirectoryName(filePath)!)!;
            next = Load(new HotkeyPreferencesStore(directory), Snapshots.GetValueOrDefault(filePath));
            Snapshots[filePath] = next;
        }
        if (Dispatcher.UIThread.CheckAccess()) { UpdateDisplays(filePath, next.Bindings); }
        else { Dispatcher.UIThread.Post(() => UpdateDisplays(filePath, next.Bindings)); }
    }

    private static void UpdateDisplays(string filePath, HotkeyBindings bindings)
    {
        lock (Displays)
        {
            for (int index = Displays.Count - 1; index >= 0; index--)
            {
                DisplayBinding binding = Displays[index];
                if (!binding.Control.TryGetTarget(out Control? target)) { Displays.RemoveAt(index); continue; }
                if (binding.FilePath == filePath) { Apply(binding, target, bindings); }
            }
        }
    }

    private static void Apply(DisplayBinding binding, Control target, HotkeyBindings bindings)
    {
        KeyGesture? gesture = bindings.Get(binding.Context, binding.Command);
        if (binding.Caption is { } caption)
        { ToolTip.SetTip(target, gesture is null ? caption : caption + " (" + gesture + ")"); }
        else if (target is MenuItem item) { item.InputGesture = gesture; }
    }
}

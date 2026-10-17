using System.Text;
using System.Text.Json;
using Avalonia.Input;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

/// <summary>Linux profile bindings, keyed by stable context and command IDs. Never imports Windows settings.</summary>
internal sealed class HotkeyPreferencesStore
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly KeyModifiers[] ModifierOrder = [KeyModifiers.Control, KeyModifiers.Shift, KeyModifiers.Alt, KeyModifiers.Meta];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static event Action<string>? Saved;

    public string FilePath { get; }

    public HotkeyPreferencesStore(string? directory = null)
        => FilePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(new PreferencesStore(directory).FilePath)!, "hotkeys.json"));

    public HotkeyBindings Load()
    {
        ReadExisting(out HotkeyBindings bindings);
        return bindings;
    }

    public void Save(HotkeyBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        HotkeyBindings snapshot = bindings.Clone();
        BindingEntry[] entries = [.. snapshot.StoredBindings.Select(entry =>
        {
            KeyGesture? gesture = entry.Gesture;
            return new BindingEntry(entry.Context, entry.Code, gesture?.Key.ToString(),
                [.. ModifierOrder.Where(modifier => gesture is not null && gesture.KeyModifiers.HasFlag(modifier)).Select(modifier => modifier.ToString())]);
        })];
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new PreferencesDocument(1, entries), JsonOptions);

        // A damaged profile must not silently become defaults, even if the caller loaded an earlier good version.
        byte[]? previous = ReadExisting(out _);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            // Revalidate immediately before replacement. Detect changes observed while preparing the new file;
            // this is not a claim of a cross-process compare-and-swap primitive.
            byte[]? current = ReadExisting(out _);
            if (previous is null ? current is not null : current is null || !previous.AsSpan().SequenceEqual(current))
            {
                throw new IOException("The hotkey preferences changed while saving. Reload them before saving again.");
            }

            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        Saved?.Invoke(FilePath);
    }

    private byte[]? ReadExisting(out HotkeyBindings bindings)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(FilePath); }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        {
            bindings = new(LoadScripts());
            return null;
        }

        bindings = Parse(bytes);
        return bytes;
    }

    private HotkeyBindings Parse(byte[] bytes)
    {
        string text;
        try { text = Utf8.GetString(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("The hotkey preferences are not valid UTF-8.", error); }
        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The hotkey preferences document must be an object.");
        }

        JsonElement versionValue = Required(root, "Version");
        if (versionValue.ValueKind != JsonValueKind.Number || !versionValue.TryGetInt32(out int version) || version != 1)
        {
            throw new JsonException("Unsupported hotkey preferences document. Expected Version 1.");
        }

        JsonElement entries = Required(root, "Bindings");
        if (entries.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Hotkey Bindings must be an array.");
        }

        HotkeyBindings result = new(LoadScripts());
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Each hotkey binding must be an object.");
            }

            JsonElement contextValue = Required(entry, "Context");
            if (contextValue.ValueKind != JsonValueKind.String || contextValue.GetString() is not string context)
            {
                throw new JsonException("A hotkey Context must be a string.");
            }

            if (!HotkeyCatalog.Contexts.Contains(context, StringComparer.Ordinal))
            {
                continue;
            }

            JsonElement codeValue = Required(entry, "Code");
            if (codeValue.ValueKind != JsonValueKind.Number || !codeValue.TryGetInt32(out int code))
            {
                throw new JsonException($"Invalid command ID in hotkey context '{context}'.");
            }

            if (HotkeyCatalog.Find(context, code) is null && !(context == "Scripts" && (code > ScriptConfiguration.MinimumUserScriptId || result.Commands(context).Any(command => command.Code == code))))
            {
                continue;
            }

            JsonElement keyValue = Required(entry, "Key");
            JsonElement modifiersValue = Required(entry, "Modifiers");
            if (modifiersValue.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException($"Modifiers for '{context}:{code}' must be an array of names.");
            }

            KeyModifiers modifiers = KeyModifiers.None;
            foreach (JsonElement modifierValue in modifiersValue.EnumerateArray())
            {
                if (modifierValue.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException($"Invalid modifier for '{context}:{code}'.");
                }

                KeyModifiers modifier = modifierValue.GetString() switch
                {
                    "Control" => KeyModifiers.Control,
                    "Shift" => KeyModifiers.Shift,
                    "Alt" => KeyModifiers.Alt,
                    "Meta" => KeyModifiers.Meta,
                    _ => throw new JsonException($"Unknown modifier for '{context}:{code}'.")
                };
                if ((modifiers & modifier) != 0)
                {
                    throw new JsonException($"Repeated modifier for '{context}:{code}'.");
                }

                modifiers |= modifier;
            }

            KeyGesture? gesture = null;
            if (keyValue.ValueKind != JsonValueKind.Null)
            {
                if (keyValue.ValueKind != JsonValueKind.String || keyValue.GetString() is not string keyName
                    || !Enum.IsDefined(typeof(Key), keyName) || !Enum.TryParse(keyName, out Key key) || key == Key.None)
                {
                    throw new JsonException($"Invalid logical key for '{context}:{code}'. Use null to clear a binding.");
                }

                gesture = new(key, modifiers);
            }
            else if (modifiers != KeyModifiers.None)
            {
                throw new JsonException($"An unassigned hotkey '{context}:{code}' cannot have modifiers.");
            }

            if (context == "Scripts") { result.SetStoredScript(code, gesture); }
            else { result.Set(context, code, gesture); }
        }

        return result;
    }

    private IReadOnlyList<ScriptDefinition> LoadScripts()
        => new ScriptsPreferencesStore(Path.Combine(Path.GetDirectoryName(FilePath)!, "scripts.json")).Load().Scripts;

    private static JsonElement Required(JsonElement value, string name)
        => value.TryGetProperty(name, out JsonElement property) ? property : throw new JsonException($"Missing hotkey property '{name}'.");

    private sealed record PreferencesDocument(int Version, BindingEntry[] Bindings);
    private sealed record BindingEntry(string Context, int Code, string? Key, string[] Modifiers);
}

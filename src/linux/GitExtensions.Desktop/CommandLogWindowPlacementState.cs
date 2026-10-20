using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitExtensions.Linux.Desktop;

// Client size is in DIPs; the normal top-left is in desktop pixels. Keep the
// original desktop scale so a later DPI can scale the normal location as on Windows.
internal sealed record CommandLogWindowPlacementValue(int X, int Y, double Width, double Height, double Scaling, bool Maximized)
{
    internal bool IsValid => X is >= -1_000_000 and <= 1_000_000 && Y is >= -1_000_000 and <= 1_000_000
        && double.IsFinite(Width) && Width is > 0 and <= 100_000
        && double.IsFinite(Height) && Height is > 0 and <= 100_000
        && double.IsFinite(Scaling) && Scaling is >= 0.25 and <= 8;
}

/// <summary>One geometry key in XDG state, independent of editable settings and journal data.</summary>
internal sealed class CommandLogWindowPlacementState
{
    internal const string Key = "FormGitCommandLog";
    private const int MaximumFileBytes = 64 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, WeakReference<Profile>> Profiles = new(StringComparer.Ordinal);
    private static Task _pending = Task.CompletedTask;
    private static RuntimeProfile? _current;
    private static long _saveVersion;
    private readonly Profile? _profile;
    private CommandLogWindowPlacementState(Profile? profile) => _profile = profile;

    // Capture only environment strings here. Path resolution, reads and JSON
    // parsing belong to the owned worker, even when an embedding host reboots it.
    internal static Task InitializeRuntimeAsync()
    {
        PathInputs inputs = CurrentInputs();
        lock (Gate)
        {
            long acceptedBefore = _saveVersion;
            return EnqueueLocked(() => Initialize(inputs, acceptedBefore));
        }
    }

    private static void Initialize(PathInputs inputs, long acceptedBefore)
    {
        string? path = ResolvePath(inputs);
        Profile? profile = null;
        CommandLogWindowPlacementValue? value = null;
        bool canSave = false;
        if (path is not null)
        {
            lock (Gate)
            {
                // A captured session keeps its profile alive. Rebootstrap and
                // lexical aliases of that path must share its accepted saves.
                foreach (string expired in Profiles.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToArray())
                { Profiles.Remove(expired); }
                if (!Profiles.TryGetValue(path, out WeakReference<Profile>? reference) || !reference.TryGetTarget(out profile))
                {
                    profile = new(path);
                    Profiles[path] = new(profile);
                }
            }
            try
            {
                value = ReadValue(Read(path).Document);
                canSave = true;
            }
            catch (Exception error) when (IsStateError(error)) { }
        }
        lock (Gate)
        {
            if (profile is not null)
            {
                // Saves accepted after this bootstrap was queued already own
                // the newer in-memory snapshot, even if their writes follow us.
                if (profile.SaveVersion <= acceptedBefore) { profile.Value = value; }
                profile.CanSave = canSave;
            }
            _current = new(inputs, profile);
        }
    }

    internal static CommandLogWindowPlacementState Open()
    {
        PathInputs inputs = CurrentInputs();
        lock (Gate)
        {
            // No path resolution or filesystem access on the window's UI path.
            // A host without a matching bootstrap gets defaults and cannot save.
            return new(_current is { } current && current.Inputs == inputs ? current.Profile : null);
        }
    }

    internal CommandLogWindowPlacementValue? Value { get { lock (Gate) { return _profile?.Value; } } }

    internal void Save(CommandLogWindowPlacementValue value)
    {
        if (!value.IsValid) { return; }
        lock (Gate)
        {
            if (_profile is not { CanSave: true } profile) { return; }
            profile.Value = value;
            profile.SaveVersion = ++_saveVersion;
            // Publication is ordered with queuing: close/reopen sees this exact
            // immutable snapshot while its owned worker write is still draining.
            _ = EnqueueLocked(() =>
            {
                try { SaveFile(profile.Path, value); }
                catch (Exception error) when (IsStateError(error)) { }
            });
        }
    }

    // No Dispatcher continuations are needed by queued writes. Program calls this
    // only after the real UI loop ends, including an owner-driven journal close.
    internal static Task DrainAsync() { lock (Gate) { return _pending; } }
    private static Task EnqueueLocked(Action action)
    {
        Task previous = _pending;
        return _pending = Task.Run(async () => { await previous.ConfigureAwait(false); action(); });
    }

    private static PathInputs CurrentInputs() => new(
        Environment.GetEnvironmentVariable("XDG_STATE_HOME"), Environment.GetEnvironmentVariable("HOME"));

    private static string? ResolvePath(PathInputs inputs)
    {
        try
        {
            string? root = Path.IsPathFullyQualified(inputs.StateHome ?? "") ? inputs.StateHome
                : Path.IsPathFullyQualified(inputs.Home ?? "") ? Path.Combine(inputs.Home!, ".local", "state") : null;
            // An absent/relative HOME must never turn this state into a file in
            // the current repository. A nonexistent absolute HOME is still valid.
            return root is null ? null : Path.GetFullPath(Path.Combine(root, "gitextensions", "window-placements.json"));
        }
        catch (Exception error) when (IsStateError(error)) { return null; }
    }

    private static (JsonObject Document, byte[]? Bytes) Read(string path)
    {
        byte[]? bytes;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            { throw new IOException("Window state must be a regular file."); }
            if (new FileInfo(path).Length is <= 0 or > MaximumFileBytes) { throw new InvalidDataException("Invalid window state size."); }
            using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > MaximumFileBytes) { throw new InvalidDataException("Invalid window state size."); }
            bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            if (input.ReadByte() != -1) { throw new InvalidDataException("Window state changed during reading."); }
        }
        catch (FileNotFoundException) { bytes = null; }
        catch (DirectoryNotFoundException) { bytes = null; }
        if (bytes is null) { return (new JsonObject(), null); }
        using JsonDocument parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        ValidateUniqueProperties(parsed.RootElement);
        JsonObject document = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject
            ?? throw new InvalidDataException("Window state must be an object.");
        _ = ReadValue(document); // A malformed known entry must never be overwritten.
        return (document, bytes);
    }

    private static void ValidateUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) { throw new InvalidDataException("Duplicate window state property."); }
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { foreach (JsonElement item in element.EnumerateArray()) { ValidateUniqueProperties(item); } }
    }

    private static CommandLogWindowPlacementValue? ReadValue(JsonObject document)
    {
        if (!document.TryGetPropertyValue(Key, out JsonNode? node)) { return null; }
        if (node is not JsonObject entry || !Integer(entry, "X", out int x) || !Integer(entry, "Y", out int y)
            || !Number(entry, "Width", out double width) || !Number(entry, "Height", out double height)
            || !Number(entry, "Scaling", out double scaling)
            || entry["State"] is not JsonValue scalar || !scalar.TryGetValue(out string? state)
            || state is not ("Normal" or "Maximized" or "Minimized"))
        { throw new InvalidDataException("Invalid journal window placement."); }
        CommandLogWindowPlacementValue value = new(x, y, width, height, scaling, state == "Maximized");
        return value.IsValid ? value : throw new InvalidDataException("Invalid journal window bounds.");
    }
    private static bool Integer(JsonObject entry, string name, out int value)
    { value = default; return entry[name] is JsonValue scalar && scalar.TryGetValue(out value); }
    private static bool Number(JsonObject entry, string name, out double value)
    { value = default; return entry[name] is JsonValue scalar && scalar.TryGetValue(out value); }

    private static void SaveFile(string path, CommandLogWindowPlacementValue value)
    {
        // Validate before even creating a lock beside an invalid/unreadable file.
        _ = Read(path);
        if (!CanReplace(path)) { return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream claim = new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        (JsonObject document, byte[]? baseline) = Read(path);
        if (!CanReplace(path)) { return; }
        JsonObject entry = document[Key] as JsonObject ?? new();
        entry["X"] = value.X; entry["Y"] = value.Y;
        entry["Width"] = value.Width; entry["Height"] = value.Height;
        entry["Scaling"] = value.Scaling; entry["State"] = value.Maximized ? "Maximized" : "Normal";
        if (entry.Parent is null) { document[Key] = entry; }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > MaximumFileBytes) { return; }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            byte[]? current = Read(path).Bytes;
            if ((baseline is null) != (current is null) || baseline is not null && !baseline.AsSpan().SequenceEqual(current)) { return; }
            if (CanReplace(path)) { File.Move(temporary, path, overwrite: true); }
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    private static bool CanReplace(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) { return false; }
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                UnixFileMode mode = File.GetUnixFileMode(path);
                if ((mode & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0) { return false; }
            }
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }
    private static bool IsStateError(Exception error)
        => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException;

    private sealed record PathInputs(string? StateHome, string? Home);
    private sealed record RuntimeProfile(PathInputs Inputs, Profile? Profile);

    private sealed class Profile(string path)
    {
        internal string Path { get; } = path;
        internal CommandLogWindowPlacementValue? Value { get; set; }
        internal bool CanSave { get; set; }
        internal long SaveVersion { get; set; }
    }
}

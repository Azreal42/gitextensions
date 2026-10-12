using System.Collections.Concurrent;
using System.Text.Json;
using GitCommands.Settings;

namespace GitExtensions.Linux.Desktop;

public enum DiffViewerAppearance { Patch, GitWordDiff, Difftastic }

public sealed record DiffViewerRenderingOptions(bool ShowNonPrintingChars = false, bool ShowSyntaxHighlightingInDiff = true);

public sealed record DiffViewerPreferences(bool UseGitColoring = true, bool ReverseGitColoring = true,
    DiffViewerAppearance Appearance = DiffViewerAppearance.Patch, bool RememberAppearance = false)
{
    public IgnoreWhitespaceKind IgnoreWhitespace { get; init; } = IgnoreWhitespaceKind.None;
    public bool RememberWhitespace { get; init; } = true;
    public int ContextLines { get; init; } = 3;
    public bool RememberContextLines { get; init; }
    public bool ShowEntireFile { get; init; }
    public bool RememberWholeFile { get; init; }
    public bool ShowNonPrintingChars { get; init; }
    public bool RememberNonPrintingChars { get; init; }
    public bool ShowSyntaxHighlightingInDiff { get; init; } = true;
    public bool RememberSyntaxHighlightingInDiff { get; init; } = true;
}

/// <summary>Original diff appearance defaults, with one shared runtime appearance per configuration path.</summary>
public sealed class DiffViewerPreferencesStore
{
    private static readonly ConcurrentDictionary<string, SharedState> States = new(StringComparer.Ordinal);
    private readonly SharedState _state;
    public string FilePath { get; }
    public event EventHandler? Changed { add => _state.Changed += value; remove => _state.Changed -= value; }

    public DiffViewerPreferencesStore(string? directory = null)
    {
        FilePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(new PreferencesStore(directory).FilePath)!, "diff-viewer-preferences.json"));
        _state = States.GetOrAdd(FilePath, _ => new());
    }

    public DiffViewerPreferences Load()
    {
        lock (_state)
        {
            (DiffViewerPreferences persisted, bool valid) = Read();
            if (_state.Current is null || _state.Persisted != persisted || !valid)
            {
                // RuntimeSetting.Reload starts from the saved baseline even when
                // Remember is false. Remember controls later viewer construction,
                // not whether a previously saved default is allowed to load.
                _state.Current = persisted with { ContextLines = persisted.RememberContextLines ? persisted.ContextLines : 3 };
                _state.Persisted = persisted;
            }
            return _state.Current;
        }
    }

    /// <summary>Snapshot the original comparison defaults for one new reader. Existing readers keep their own options.</summary>
    public GitComparisonDiffOptions BeginComparisonViewerSession()
    {
        lock (_state)
        {
            DiffViewerPreferences current = Load(), persisted = _state.Persisted!;
            DiffViewerPreferences snapshot = current with
            {
                IgnoreWhitespace = current.RememberWhitespace ? current.IgnoreWhitespace : persisted.IgnoreWhitespace,
                ShowEntireFile = current.RememberWholeFile ? current.ShowEntireFile : persisted.ShowEntireFile,
                ContextLines = current.RememberContextLines ? persisted.ContextLines : 3
            };
            _state.Current = snapshot;
            return new(snapshot.IgnoreWhitespace, snapshot.ContextLines, snapshot.ShowEntireFile);
        }
    }

    public DiffViewerPreferences BeginViewerSession()
    {
        DiffViewerPreferences value;
        bool changed;
        lock (_state)
        {
            DiffViewerPreferences current = Load();
            value = current.RememberAppearance ? current : current with { Appearance = _state.Persisted!.Appearance };
            changed = value != current; _state.Current = value;
        }
        if (changed) { _state.Notify(this); }
        return value;
    }

    /// <summary>Reload unremembered RuntimeSettings for a new reader, without replacing options in already open readers.</summary>
    public DiffViewerRenderingOptions BeginRenderingViewerSession()
    {
        lock (_state)
        {
            DiffViewerPreferences current = Load(), persisted = _state.Persisted!;
            DiffViewerPreferences snapshot = current with
            {
                ShowNonPrintingChars = current.RememberNonPrintingChars ? current.ShowNonPrintingChars : persisted.ShowNonPrintingChars,
                ShowSyntaxHighlightingInDiff = current.RememberSyntaxHighlightingInDiff ? current.ShowSyntaxHighlightingInDiff : persisted.ShowSyntaxHighlightingInDiff
            };
            _state.Current = snapshot;
            return new(snapshot.ShowNonPrintingChars, snapshot.ShowSyntaxHighlightingInDiff);
        }
    }

    public bool Save(DiffViewerPreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Validate(value);
        // Existing appearance controllers hold a snapshot record. Treat their
        // saves as a patch of the original four fields, preserving newer reader
        // session values and remember flags written by another controller.
        return Write(current => current with
        {
            UseGitColoring = value.UseGitColoring, ReverseGitColoring = value.ReverseGitColoring,
            Appearance = value.Appearance, RememberAppearance = value.RememberAppearance
        });
    }

    internal bool SaveComparisonRememberFlags(bool? whitespace = null, bool? contextLines = null, bool? wholeFile = null)
        => Write(current => current with
        {
            RememberWhitespace = whitespace ?? current.RememberWhitespace,
            RememberContextLines = contextLines ?? current.RememberContextLines,
            RememberWholeFile = wholeFile ?? current.RememberWholeFile
        });

    /// <summary>Apply the original settings page atomically without replacing current reader values or saved defaults.</summary>
    public bool SaveSettings(DiffViewerPreferences settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Write(current => current with
        {
            UseGitColoring = settings.UseGitColoring, ReverseGitColoring = settings.ReverseGitColoring,
            RememberAppearance = settings.RememberAppearance, RememberWhitespace = settings.RememberWhitespace,
            RememberContextLines = settings.RememberContextLines, RememberWholeFile = settings.RememberWholeFile,
            RememberNonPrintingChars = settings.RememberNonPrintingChars,
            RememberSyntaxHighlightingInDiff = settings.RememberSyntaxHighlightingInDiff
        });
    }

    /// <summary>Context differs from RuntimeSetting: its setter persists only while RememberContextLines is enabled.</summary>
    public bool SaveContextLines(int value)
    {
        if (value is < 0 or > 100000) { throw new ArgumentOutOfRangeException(nameof(value)); }
        return Write(current => current with { ContextLines = value }, saveContext: true);
    }

    internal bool SaveComparisonOptions(GitComparisonDiffOptions previous, GitComparisonDiffOptions next)
        => Write(current => current with
        {
            IgnoreWhitespace = previous.IgnoreWhitespace != next.IgnoreWhitespace ? next.IgnoreWhitespace : current.IgnoreWhitespace,
            ContextLines = previous.ContextLines != next.ContextLines ? next.ContextLines : current.ContextLines,
            ShowEntireFile = previous.ShowEntireFile != next.ShowEntireFile ? next.ShowEntireFile : current.ShowEntireFile
        }, saveContext: previous.ContextLines != next.ContextLines);

    internal bool SaveRenderingOptions(DiffViewerRenderingOptions previous, DiffViewerRenderingOptions next)
        => Write(current => current with
        {
            ShowNonPrintingChars = previous.ShowNonPrintingChars != next.ShowNonPrintingChars ? next.ShowNonPrintingChars : current.ShowNonPrintingChars,
            ShowSyntaxHighlightingInDiff = previous.ShowSyntaxHighlightingInDiff != next.ShowSyntaxHighlightingInDiff ? next.ShowSyntaxHighlightingInDiff : current.ShowSyntaxHighlightingInDiff
        });

    /// <summary>Save the latest shared runtime values, as the original revision-grid command does.</summary>
    public bool SaveCurrentViewSettingsAsDefault() => Write(current => current, saveRuntimeDefaults: true);

    private bool Write(Func<DiffViewerPreferences, DiffViewerPreferences> update, bool saveContext = false, bool saveRuntimeDefaults = false)
    {
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool changed;
        try
        {
            lock (_state)
            {
                DiffViewerPreferences current = Load(), value = update(current);
                Validate(value);
                DiffViewerPreferences baseline = _state.Persisted!;
                DiffViewerPreferences persisted = value with
                {
                    Appearance = saveRuntimeDefaults ? value.Appearance : baseline.Appearance,
                    IgnoreWhitespace = saveRuntimeDefaults ? value.IgnoreWhitespace : baseline.IgnoreWhitespace,
                    ShowEntireFile = saveRuntimeDefaults ? value.ShowEntireFile : baseline.ShowEntireFile,
                    ShowNonPrintingChars = saveRuntimeDefaults ? value.ShowNonPrintingChars : baseline.ShowNonPrintingChars,
                    ShowSyntaxHighlightingInDiff = saveRuntimeDefaults ? value.ShowSyntaxHighlightingInDiff : baseline.ShowSyntaxHighlightingInDiff,
                    ContextLines = saveContext && value.RememberContextLines ? value.ContextLines : baseline.ContextLines
                };
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(persisted));
                File.Move(temporary, FilePath, overwrite: true);
                changed = current != value;
                _state.Current = value; _state.Persisted = persisted;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
        if (changed) { _state.Notify(this); }
        return true;
    }

    private (DiffViewerPreferences Preferences, bool Valid) Read()
    {
        try
        {
            if (!File.Exists(FilePath)) { return (new(), true); }
            DiffViewerPreferences? value = JsonSerializer.Deserialize<DiffViewerPreferences>(File.ReadAllText(FilePath));
            if (value is null) { return (new(), false); }
            DiffViewerPreferences normalized = value with
            {
                Appearance = Enum.IsDefined(value.Appearance) ? value.Appearance : DiffViewerAppearance.Patch,
                IgnoreWhitespace = Enum.IsDefined(value.IgnoreWhitespace) ? value.IgnoreWhitespace : IgnoreWhitespaceKind.None,
                ContextLines = value.ContextLines is >= 0 and <= 100000 ? value.ContextLines : 3
            };
            return (normalized, normalized == value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return (new(), false); }
    }

    private static void Validate(DiffViewerPreferences value)
    {
        if (!Enum.IsDefined(value.Appearance) || !Enum.IsDefined(value.IgnoreWhitespace) || value.ContextLines is < 0 or > 100000)
        { throw new ArgumentOutOfRangeException(nameof(value)); }
    }

    private sealed class SharedState
    {
        internal DiffViewerPreferences? Current, Persisted;
        internal event EventHandler? Changed;
        internal void Notify(object sender) => Changed?.Invoke(sender, EventArgs.Empty);
    }
}

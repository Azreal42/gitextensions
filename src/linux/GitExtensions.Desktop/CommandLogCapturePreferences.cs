using System.Xml;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

internal sealed record CommandLogCaptureState(bool Enabled, GitXmlSettingsSnapshot? Snapshot, Exception? Error);

/// <summary>The one Windows XML key; an accepted write and its runtime publication share the same gate.</summary>
internal sealed class CommandLogCapturePreferences
{
    internal const string Key = "Log.CaptureCallStacks";
    // Runtime policy is process-global. One gate also orders aliases of the same profile.
    private static readonly SemaphoreSlim PublicationGate = new(1, 1);
    private static readonly object RuntimeGate = new();
    internal string FilePath { get; }
    internal CommandLogCapturePreferences(string? path = null) => FilePath = Path.GetFullPath(path ?? DetailedRepositorySettings.GlobalPath);

    internal Task<CommandLogCaptureState> LoadAsync(CancellationToken token = default) => ReadAndPublishAsync(false, token);
    private async Task<CommandLogCaptureState> ReadAndPublishAsync(bool initializeRuntime, CancellationToken token)
    {
        await PublicationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (initializeRuntime)
            { lock (RuntimeGate) { GitCommandLog.ConfigureCapture(new(false, approvedHostAssembly: typeof(Program).Assembly)); } }
            CommandLogCaptureState value;
            try
            {
                GitXmlSettingsSnapshot snapshot = await GitXmlSettingsStore.LoadAsync(FilePath, token).ConfigureAwait(false);
                value = new(Parse(snapshot), snapshot, null);
            }
            catch (Exception error) when (IsPreferenceError(error)) { value = new(false, null, error); }
            token.ThrowIfCancellationRequested(); Publish(value.Enabled); return value;
        }
        finally { PublicationGate.Release(); }
    }

    internal async Task<CommandLogCaptureState> SetAsync(CommandLogCaptureState expected, bool desired, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Error is not null || expected.Snapshot is not { } baseline || baseline.Path != FilePath)
        { throw new InvalidOperationException("Reload the call stack capture settings before saving."); }
        await PublicationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Even an unchanged choice validates the exact XML CAS baseline. An absent
            // false default stays absent; no file/key is created by simply opening.
            IReadOnlyDictionary<string, string?> edits = desired == expected.Enabled
                ? new Dictionary<string, string?>() : new Dictionary<string, string?> { [Key] = desired ? "true" : "false" };
            GitXmlSettingsSnapshot accepted = await GitXmlSettingsStore.SaveChangesAsync(baseline, edits, token).ConfigureAwait(false);
            CommandLogCaptureState value = new(Parse(accepted), accepted, null);
            // The shared writer drains an acquired effect despite later cancellation.
            // Publish that real effect even if its initiating window has since closed.
            Publish(value.Enabled); return value;
        }
        finally { PublicationGate.Release(); }
    }

    internal static Task<CommandLogCaptureState> InitializeRuntimeAsync(CancellationToken token = default)
        => new CommandLogCapturePreferences().ReadAndPublishAsync(true, token);

    // Closed cancels unacquired UI requests. An acquired XML write and its publication
    // run entirely off Dispatcher, so Program can drain this gate after the UI loop ends.
    internal static async Task DrainAsync()
    {
        await PublicationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        PublicationGate.Release();
    }

    // Only Program's AfterSetup callback installs this, after real platform/Dispatcher setup.
    internal static void InstallDispatcherProbe()
    {
        Dispatcher.UIThread.VerifyAccess();
        lock (RuntimeGate)
        { GitCommandLog.ConfigureCapture(new(GitCommandLog.CapturePolicy.CaptureCallStacks, static () => Dispatcher.UIThread.CheckAccess(), typeof(Program).Assembly)); }
    }

    private static void Publish(bool enabled)
    {
        lock (RuntimeGate)
        { GitCommandLog.ConfigureCapture(new(enabled, GitCommandLog.CapturePolicy.UiThreadProbe, typeof(Program).Assembly)); }
    }
    private static bool Parse(GitXmlSettingsSnapshot value)
        => !value.Values.TryGetValue(Key, out string? raw) ? false : DetailedRepositorySettings.Boolean(raw)
            ?? throw new InvalidDataException("Log.CaptureCallStacks must be true or false. Repair and reopen the journal before saving.");
    internal static bool IsPreferenceError(Exception error)
        => error is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or ArgumentException or InvalidOperationException;
}

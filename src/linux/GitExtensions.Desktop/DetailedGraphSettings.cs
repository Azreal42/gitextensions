using System.Text.Json;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitExtensions.Desktop.Controls.Graph;

namespace GitExtensions.Linux.Desktop;

/// <summary>One saved global graph policy for existing Browse, History and revision-picker controls.</summary>
internal static class DetailedGraphSettings
{
    private static event Action<string, RevisionGraphOptions>? Saved;

    internal static RevisionGraphOptions From(DesktopPreferences preferences) => new(
        preferences.MergeGraphLanesHavingCommonParent, preferences.RenderGraphWithDiagonals, preferences.StraightenGraphDiagonals);

    internal static void PublishSaved(string profile, DesktopPreferences preferences)
        => Saved?.Invoke(Path.GetFullPath(profile), From(preferences));

    internal static void Bind(CommitGraph graph)
    {
        PreferencesStore store = new();
        string profile = Path.GetFullPath(store.FilePath);
        bool attached = false;
        long attachment = 0;
        void ReadSaved()
        {
            // Unreadable profiles are reported by the Settings host. A graph never repairs or writes them.
            try { graph.Options = From(store.Load()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        void Changed(string path, RevisionGraphOptions options)
        {
            long version = Interlocked.Read(ref attachment);
            if (!Volatile.Read(ref attached) || path != profile) { return; }
            if (Dispatcher.UIThread.CheckAccess()) { graph.Options = options; }
            else { Dispatcher.UIThread.Post(() => { if (Volatile.Read(ref attached) && Interlocked.Read(ref attachment) == version) { graph.Options = options; } }); }
        }
        ReadSaved();
        graph.AttachedToVisualTree += (_, _) =>
        {
            if (attached) { return; }
            Interlocked.Increment(ref attachment); attached = true; Saved += Changed; ReadSaved();
        };
        graph.DetachedFromVisualTree += (_, _) => { Interlocked.Increment(ref attachment); attached = false; Saved -= Changed; };
    }
}

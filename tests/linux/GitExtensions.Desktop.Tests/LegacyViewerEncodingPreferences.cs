using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;

namespace GitExtensions.Desktop.Tests;

/// <summary>These legacy reader scenarios explicitly retain the fixed encoding menu they exercised before preferences existed.</summary>
internal static class LegacyViewerEncodingPreferences
{
    internal static AvailableEncodingsPreferencesStore Seed(string directory)
    {
        AvailableEncodingsPreferencesStore store = new(directory);
        store.SaveChanges(store.Load(), GitFileEncodingCodec.AvailableEncodings.Select(choice => choice.Name));
        return store;
    }

    internal sealed class TemporaryProfile : IDisposable
    {
        private readonly string? _originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "gitextensions-legacy-encoding-profile-" + Guid.NewGuid().ToString("N"));
        internal TemporaryProfile()
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
            try { Seed(_directory); }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalXdg);
            if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); }
        }
    }
}

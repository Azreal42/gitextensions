using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitExtensions.Linux.Desktop;

/// <summary>Durable attempt time, independent of editable settings. Claim before HTTP, as on Windows.</summary>
internal sealed class UpdateCheckStateStore
{
    internal string FilePath { get; }
    internal UpdateCheckStateStore(string? stateHome = null)
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        string root = stateHome ?? (Path.IsPathFullyQualified(xdg ?? "") ? xdg! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"));
        FilePath = Path.Combine(root, "gitextensions", "update-check.json");
    }

    internal bool TryClaim(DateTimeOffset now, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); now = now.ToUniversalTime();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        FileStream claim;
        try { claim = new(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33) { return false; }
        using (claim)
        {
            byte[]? bytes;
            try { bytes = File.ReadAllBytes(FilePath); }
            catch (FileNotFoundException) { bytes = null; }
            JsonObject document = bytes is null ? new() : JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException("Update check state must contain an object.");
            if (document.TryGetPropertyValue("LastUpdateCheckUtc", out JsonNode? value))
            {
                if (value is not JsonValue scalar || !scalar.TryGetValue(out string? text)
                    || !DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset last))
                { throw new JsonException("The last update check time is invalid."); }
                // Future timestamps remain throttled if the system clock moves backwards.
                if (now <= last || now - last <= TimeSpan.FromDays(7)) { return false; }
            }
            document["LastUpdateCheckUtc"] = now.ToString("O", CultureInfo.InvariantCulture);
            string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                token.ThrowIfCancellationRequested();
                File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                token.ThrowIfCancellationRequested(); File.Move(temporary, FilePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
            return true;
        }
    }
}

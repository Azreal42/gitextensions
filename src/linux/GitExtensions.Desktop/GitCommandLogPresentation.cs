using System.Globalization;
using System.Text;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>Formats only the recorder's allowlisted metadata; never reconstructs arguments or process output.</summary>
internal static class GitCommandLogPresentation
{
    internal static string Line(GitCommandLogEntry entry, TranslationCatalog translations)
        => $"{entry.Started:HH:mm:ss.fff} {(entry.IsRunning ? translations.Translate("running") : entry.Duration.TotalMilliseconds.ToString("N0", CultureInfo.CurrentCulture) + " ms"),9} {entry.ProcessId,7} {Exit(entry, translations),3} {entry.Command}";

    internal static string Detail(GitCommandLogEntry entry, TranslationCatalog translations)
        => $"{translations.Translate("Command")}:     {entry.Command}\n"
            + $"{translations.Translate("Working dir")}: {entry.Directory}\n"
            + $"{translations.Translate("Process ID")}:  {entry.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? translations.Translate("unknown")}\n"
            + $"{translations.Translate("Started at")}:  {entry.Started:O}\n"
            + $"{translations.Translate("Duration")}:    {(entry.IsRunning ? translations.Translate("still running") : entry.Duration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) + " ms")}\n"
            + $"{translations.Translate("Exit code")}:   {Exit(entry, translations)}";

    private static string Exit(GitCommandLogEntry entry, TranslationCatalog translations)
        => entry.IsRunning ? "" : entry.Cancelled ? translations.Translate("Cancelled")
            : entry.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? translations.Translate("Failed");

    internal static string Export(IReadOnlyList<GitCommandLogEntry> snapshot, bool csv, CultureInfo culture)
    {
        string separator = csv ? culture.TextInfo.ListSeparator : "\t";
        StringBuilder text = new();
        foreach (GitCommandLogEntry entry in snapshot)
        {
            string[] fields = [entry.Started.ToString("O", CultureInfo.InvariantCulture),
                entry.IsRunning ? "" : entry.Duration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                entry.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "", entry.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "",
                entry.Cancelled ? "true" : "false", entry.IsRunning ? "true" : "false", entry.Command, entry.Directory];
            // CSV quotes preserve embedded delimiters/newlines. TXT escapes control
            // characters so each command remains exactly one physical tabbed line.
            text.AppendJoin(separator, fields.Select(field => csv ? Quote(field, separator) : Escape(field))).Append('\n');
        }
        return text.ToString();
    }
    private static string Quote(string field, string separator)
        => field.Contains(separator, StringComparison.Ordinal) || field.IndexOfAny(['"', '\r', '\n']) >= 0 ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;
    private static string Escape(string field) => field.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}

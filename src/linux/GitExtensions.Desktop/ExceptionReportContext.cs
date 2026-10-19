using System.Runtime.InteropServices;
using System.Text;

namespace GitExtensions.Linux.Desktop;

internal enum BugReportDecision { Dismiss, Ignore, Quit }

/// <summary>Already-known facts at the failed operation boundary; never probes Git or the environment.</summary>
internal sealed record ExceptionReportContext(string Operation, string? WorkingDirectory, string ApplicationVersion,
    DateTimeOffset OccurredAt, string OperatingSystem, string Runtime, string Architecture,
    string? GitVersion = null, bool CanIgnore = true, bool ShowIgnore = false, bool FocusDetails = false)
{
    internal static ExceptionReportContext Capture(string operation, string? directory, string version, Exception exception)
        => new(operation, directory, version, DateTimeOffset.Now, RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            ShowIgnore: exception is GitCommandException or IOException or System.ComponentModel.Win32Exception,
            FocusDetails: exception is GitCommandException);

    internal string EnvironmentText => $"- Git Extensions {ApplicationVersion}\n- Git {GitVersion ?? "???"}\n- {OperatingSystem}\n- {Runtime}\n- {Architecture}\n";
    internal string OperationText => Operation + (WorkingDirectory is null ? "" : "\nDirectory: " + WorkingDirectory);

    internal string CopyText(ExceptionReportSnapshot snapshot, string? description)
    {
        // ErrorReportUrlBuilder's ordering/fenced exception and description, with a stable local snapshot.
        return EnvironmentText + ExceptionText(snapshot, description);
    }

    internal string ExceptionText(ExceptionReportSnapshot snapshot, string? description)
    {
        StringBuilder text = new();
        if (!string.IsNullOrWhiteSpace(OperationText)) { text.Append('\n').Append(OperationText.Trim()).Append("\n\n"); }
        text.Append("```\n").Append(snapshot.FullText.Trim()).Append("\n```\n\n");
        if (!string.IsNullOrWhiteSpace(description)) { text.Append(description.Trim()).Append('\n'); }
        return text.ToString();
    }
}

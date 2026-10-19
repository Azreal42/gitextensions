using Avalonia.Controls;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal static class DesktopErrorReporter
{
    internal static Uri IssueTarget { get; } = new("https://github.com/Azreal42/gitextensions/issues/new");

    internal static async Task<BugReportDecision> ShowAsync(Window owner, ExceptionReportSnapshot snapshot,
        ExceptionReportContext context, TranslationCatalog translations, CancellationToken token,
        Func<string, CancellationToken, Task>? copyText = null, Func<Uri, CancellationToken, Task>? openIssue = null)
    {
        if (token.IsCancellationRequested || !owner.IsVisible) { return BugReportDecision.Dismiss; }
        BugReportDialog dialog = new(snapshot, context, translations, copyText, openIssue, token) { RequestedThemeVariant = owner.ActualThemeVariant };
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => { dialog.BeginClose(); dialog.Close(); }));
        try { return await dialog.ShowDialog<BugReportDecision>(owner); }
        finally
        {
            await dialog.BeginClose();
            dialog.Close(); dialog.DisposeWork();
        }
    }

    internal static Uri IssueUri(ExceptionReportSnapshot snapshot, ExceptionReportContext context, string description)
    {
        // GitHubUrlBuilder's title limit and template/labels; no HTTP submission, only a user-opened URL.
        string title = "[NBug] " + snapshot.Root.Message;
        if (title.Length > 69) { title = title[..66] + "..."; }
        string details = context.ExceptionText(snapshot, description);
        return new(IssueTarget.AbsoluteUri + "?title=" + Uri.EscapeDataString(title)
            + "&template=bug_report.yml&labels=" + Uri.EscapeDataString("type: NBug")
            + "&about=" + Uri.EscapeDataString(context.EnvironmentText)
            + "&description=" + Uri.EscapeDataString(details));
    }
}

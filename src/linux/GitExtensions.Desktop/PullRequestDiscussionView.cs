using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

/// <summary>Native counterpart of DiscussionHtmlCreator: author/date/commit headings and complete selectable bodies.</summary>
internal sealed class PullRequestDiscussionView : ScrollViewer
{
    private readonly StackPanel _entries = new() { Spacing = 12, Margin = new(3) };
    private static readonly Regex BodyUrls = new("https?://[^\\s<>\"'\\)\\]]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    internal Func<Uri, Task> OpenLink { get; set; } = uri =>
    {
        ProcessStartInfo start = new("xdg-open") { UseShellExecute = false }; start.ArgumentList.Add(uri.AbsoluteUri);
        using Process? process = Process.Start(start); return Task.CompletedTask;
    };
    internal event Action<string>? LinkError;
    internal PullRequestDiscussionView()
    { Name = "_discussionWB"; Content = _entries; HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled; }
    internal void SetEntries(IReadOnlyList<GitHubPullRequestDiscussionEntry> entries)
    {
        _entries.Children.Clear();
        foreach (GitHubPullRequestDiscussionEntry entry in entries.OrderBy(item => item.CreatedAt))
        {
            Grid heading = new() { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 7 };
            TextBlock author = new() { Text = entry.Author, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
            if (entry.Kind == GitHubDiscussionKind.Commit) { author.Foreground = Brushes.Red; }
            else { author.Bind(ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundAccentBrush")); }
            TextBlock created = new() { Text = entry.CreatedAt.LocalDateTime.ToString("G"), VerticalAlignment = VerticalAlignment.Top };
            heading.Children.Add(author); Grid.SetColumn(created, 1); heading.Children.Add(created);
            StackPanel body = new() { Spacing = 2 };
            Border header = ClassicChrome.Border(heading, new(0, 0, 0, 1)); header.Bind(BackgroundProperty, new DynamicResourceExtension("ClassicMenuBrush")); body.Children.Add(header);
            if (entry.Kind == GitHubDiscussionKind.Commit && entry.Sha is not null)
            { body.Children.Add(new SelectableTextBlock { Text = "Commit: " + entry.Sha, FontSize = 10, TextWrapping = TextWrapping.Wrap }); }
            // Windows inserts the supplied Markdown body into its HTML document. Native
            // text preserves all content literally and never executes embedded HTML/scripts.
            body.Children.Add(new SelectableTextBlock { Name = "PullRequestDiscussionBody", Text = entry.Body, TextWrapping = TextWrapping.Wrap, Focusable = true });
            IEnumerable<string> links = BodyUrls.Matches(entry.Body).Select(match => match.Value);
            if (!string.IsNullOrEmpty(entry.WebUrl)) { links = links.Append(entry.WebUrl); }
            foreach (string address in links.Distinct(StringComparer.Ordinal))
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http")) { continue; }
                Button link = new() { Name = "PullRequestDiscussionLink", Content = new TextBlock { Text = address, TextWrapping = TextWrapping.Wrap, TextDecorations = TextDecorations.Underline },
                    Tag = address, Padding = new(0), MinHeight = 0, BorderThickness = new(0), Background = Brushes.Transparent, Cursor = new(StandardCursorType.Hand), HorizontalAlignment = HorizontalAlignment.Left };
                link.Bind(ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundAccentBrush"));
                link.Click += async (_, _) => { try { await OpenLink(uri); } catch (Exception error) { LinkError?.Invoke(error.Message); } };
                body.Children.Add(link);
            }
            _entries.Children.Add(body);
        }
        ScrollToEnd();
    }
}

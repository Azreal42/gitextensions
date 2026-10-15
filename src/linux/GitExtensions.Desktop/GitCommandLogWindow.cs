using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class GitCommandLogWindow : Window
{
    private readonly TextBox _text = new() { Name = "GitCommandLogText", IsReadOnly = true, AcceptsReturn = true, FontFamily = new Avalonia.Media.FontFamily("DejaVu Sans Mono"), FontSize = 12, Margin = new Thickness(6) };
    private readonly TranslationCatalog _translations;
    private readonly DispatcherTimer _timer;
    private GitCommandLogEntry? _last;

    public GitCommandLogWindow(TranslationCatalog translations)
    {
        _translations = translations;
        Name = "GitCommandLogWindow"; Title = translations.Translate("Git command log");
        Width = 880; Height = 460; MinWidth = 400; MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Button clear = new() { Name = "ClearGitCommandLog", Content = translations.Translate("Clear"), MinWidth = 75 };
        Button close = new() { Name = "CloseGitCommandLog", Content = translations.Translate("Close"), MinWidth = 75, IsCancel = true };
        clear.Click += (_, _) => { GitCommandLog.Clear(); Refresh(); };
        close.Click += (_, _) => Close();
        StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(6), Children = { clear, close } };
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel { Children = { footer, _text } };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Refresh());
        Opened += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    internal void Refresh()
    {
        IReadOnlyList<GitCommandLogEntry> entries = GitCommandLog.Snapshot();
        GitCommandLogEntry? last = entries.LastOrDefault();
        if (ReferenceEquals(last, _last) && _text.Text is not null) { return; }
        _last = last;
        _text.Text = string.Join('\n', entries.Select(entry =>
            $"{entry.Started:HH:mm:ss.fff}  {entry.Duration.TotalMilliseconds,8:0} ms  {(entry.Cancelled ? _translations.Translate("Cancelled") : entry.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? _translations.Translate("Failed")),8}  {entry.Command,-20}  {entry.Directory}"));
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The actual failed staging output, shown only when the global Windows option requests it.</summary>
internal sealed class CommitStagingReportDialog : Window
{
    private CommitStagingReportDialog(int files, GitResult result, TranslationCatalog translations)
    {
        string Text(string name, string fallback) => translations.Translate("FormCommit", name, "Text", fallback);
        Name = "StageDetails"; Title = Text("_stageDetails", "Stage Details"); Width = 720; Height = 420;
        MinWidth = 480; MinHeight = 280; CanMinimize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        TextBox output = new() { Name = "StageDetailsOutput", Text = result.StandardOutput + result.StandardError,
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap };
        ConsoleStylePresentation.Bind(output);
        Button close = new() { Name = "StageDetailsClose", Content = translations.Translate("Close"), IsDefault = true, IsCancel = true, MinWidth = 85 };
        close.Click += (_, _) => Close();
        Grid panel = new() { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(10) };
        panel.Children.Add(new TextBlock { Name = "StageDetailsSummary", Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
            Text("_stageFiles", "Stage {0} files"), files), TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(output, 1); panel.Children.Add(output); Grid.SetRow(close, 2); close.HorizontalAlignment = HorizontalAlignment.Right; panel.Children.Add(close);
        Content = panel; Opened += (_, _) => close.Focus();
    }

    internal static async Task ShowAsync(Window owner, int files, GitResult result, TranslationCatalog translations, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CommitStagingReportDialog report = new(files, result, translations) { RequestedThemeVariant = owner.ActualThemeVariant };
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => { if (report.IsVisible) { report.Close(); } }));
        try { await report.ShowDialog(owner); token.ThrowIfCancellationRequested(); }
        finally { report.Close(); }
    }
}

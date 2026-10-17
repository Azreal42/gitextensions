using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

public enum MaintenanceView { Verify, Statistics }

public sealed class MaintenanceDialog : RepositoryDialog
{
    private readonly MaintenanceView _initialView;
    private readonly CheckBox _full = new() { Name = "VerifyFull", IsChecked = true };
    private readonly CheckBox _unreachable = new() { Name = "VerifyUnreachable" };
    private readonly CheckBox _noReflogs = new() { Name = "VerifyNoReflogs" };
    private readonly CheckBox _strict = new() { Name = "VerifyStrict" };
    private readonly ListBox _objects = new() { Name = "VerifiedObjects", Height = 140 };
    private readonly TextBox _report = new() { Name = "VerificationReport", AcceptsReturn = true, IsReadOnly = true, };
    private readonly TextBox _statistics = new() { Name = "RepositoryStatistics", AcceptsReturn = true, IsReadOnly = true, };
    private readonly TabControl _tabs = new() { Name = "MaintenanceTabs" };
    public GitFsckResult? LastVerification { get; private set; }
    public GitRepositoryStatistics? LastStatistics { get; private set; }

    public MaintenanceDialog(GitRepository repository, MaintenanceView initialView = MaintenanceView.Verify) : base(repository, "Repository maintenance")
    {
        DesktopFonts.Apply(_report, DesktopFontRole.Code);
        DesktopFonts.Apply(_statistics, DesktopFontRole.Monospace);
        _initialView = initialView;
        Width = 700;
        Height = 480;
        MinWidth = 560;
        MinHeight = 350;
        _full.Content = T("Full check");
        _unreachable.Content = T("Unreachable objects");
        _noReflogs.Content = T("Ignore reflogs");
        _strict.Content = T("Strict");
        StackPanel buttons = ExportDialogControls.Buttons(Action("VerifyObjects", "Verify objects", VerifyAsync), Action("ReadStatistics", "Repository statistics", StatisticsAsync),
            Action("CollectGarbage", "Garbage collection…", CollectAsync), Action("CloseMaintenance", "Close", () => { Close(); return Task.CompletedTask; }));
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        StackPanel options = new() { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(10, 8), Children = { _full, _unreachable, _noReflogs, _strict } };
        DockPanel.SetDock(options, Dock.Top);
        Root.Children.Add(options);
        _objects.ItemTemplate = new FuncDataTemplate<GitFsckIssue>((item, _) => new TextBlock
        {
            Text = item is null ? "" : T(item.Kind.ToString()) + "    " + item.ObjectType + "    " + item.Hash,
            Margin = new Thickness(4, 2), TextTrimming = TextTrimming.CharacterEllipsis
        });
        DockPanel verify = new() { LastChildFill = true };
        DockPanel.SetDock(_objects, Dock.Top);
        verify.Children.Add(_objects);
        verify.Children.Add(_report);
        _tabs.ItemsSource = new[] { new TabItem { Header = T("Verify"), Content = verify }, new TabItem { Header = T("Statistics"), Content = _statistics } };
        _tabs.Margin = new Thickness(10, 0, 10, 6);
        _tabs.SelectedIndex = initialView == MaintenanceView.Statistics ? 1 : 0;
        Root.Children.Add(_tabs);
        Track(_full, _unreachable, _noReflogs, _strict);
    }

    public override Task InitializeAsync() => _initialView == MaintenanceView.Statistics ? StatisticsAsync() : VerifyAsync();

    private Task VerifyAsync() => RunAsync("Verify objects", async token =>
    {
        LastVerification = await Repository.VerifyObjectsAsync(new(_full.IsChecked == true, _unreachable.IsChecked == true, _noReflogs.IsChecked == true, _strict.IsChecked == true), token);
        _objects.ItemsSource = LastVerification.Issues;
        _report.Text = LastVerification.StandardOutput + LastVerification.StandardError;
        if (string.IsNullOrWhiteSpace(_report.Text)) { _report.Text = T("No problems found."); }
        _tabs.SelectedIndex = 0;
        Status.Text = LastVerification.IsValid ? T("Verification completed") : T("Verification failed");
    });

    private Task StatisticsAsync() => RunAsync("Repository statistics", async token =>
    {
        await ReadStatisticsAsync(token);
        _tabs.SelectedIndex = 1;
        Status.Text = T("Repository statistics");
    });

    private async Task ReadStatisticsAsync(CancellationToken token)
    {
        LastStatistics = await Repository.GetStatisticsAsync(token);
        _statistics.Text = string.Join('\n', new[]
        {
            T("Loose objects") + ": " + LastStatistics.LooseObjects,
            T("Loose object size") + ": " + Size(LastStatistics.LooseSizeBytes),
            T("Packed objects") + ": " + LastStatistics.PackedObjects,
            T("Pack files") + ": " + LastStatistics.PackFiles,
            T("Pack size") + ": " + Size(LastStatistics.PackedSizeBytes),
            T("Prunable packed objects") + ": " + LastStatistics.PrunablePackedObjects,
            T("Garbage objects") + ": " + LastStatistics.GarbageObjects,
            T("Garbage size") + ": " + Size(LastStatistics.GarbageSizeBytes),
            string.Join('\n', LastStatistics.Alternates.Select(path => T("Alternate object database") + ": " + ExportDialogControls.DisplayPath(path))), LastStatistics.Diagnostics
        }.Where(line => line.Length != 0));
    }

    private async Task CollectAsync()
    {
        if (!await Dialogs.ConfirmAsync(this, T("Garbage collection"), T("Run Git garbage collection with its normal retention policy?"))) { return; }
        await RunAsync("Garbage collection", async token =>
        {
            await Repository.GarbageCollectAsync(token);
            await ReadStatisticsAsync(token);
            _tabs.SelectedIndex = 1;
            Status.Text = T("Garbage collection completed");
        }, changes: true);
    }

    private static string Size(long bytes) => bytes < 1024 ? bytes + " B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:N1} KiB" : $"{bytes / (1024d * 1024):N1} MiB";
}

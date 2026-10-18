using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal enum GitEnvironmentCheckId { GitExecutable, GlobalIdentity, Editor, MergeTool, DiffTool, Shell, Ssh, Translation, Home }
internal enum GitEnvironmentCheckStatus { Good, Warning, Error, Unavailable }
internal sealed record GitEnvironmentCheck(GitEnvironmentCheckId Id, GitEnvironmentCheckStatus Status, string Message, bool CanRepair = true);

/// <summary>Presentation of observed diagnostics; the host owns scans, repairs and publication.</summary>
internal sealed class ChecklistSettingsPage : UserControl
{
    private readonly Dictionary<GitEnvironmentCheckId, (Button Status, Button Repair)> _rows = [];
    private readonly Dictionary<GitEnvironmentCheckId, GitEnvironmentCheck> _checks = [];
    private readonly TranslationCatalog _translations;
    private readonly CheckBox _startup;
    private readonly Button _rescan;
    private readonly TextBlock _error = new() { Name = "ChecklistError", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private bool _busy, _actionBusy, _rendering, _invalidated;
    private Func<GitEnvironmentCheckId, Task>? _repairRequested;
    private Func<Task>? _saveAndRescanRequested;
    internal bool CheckAtStartup => _startup.IsChecked == true;
    internal bool IsInvalidated => _invalidated;
    internal Func<GitEnvironmentCheckId, Task>? RepairRequested { get => _repairRequested; set { _repairRequested = value; UpdateEnabled(); } }
    internal Func<Task>? SaveAndRescanRequested { get => _saveAndRescanRequested; set { _saveAndRescanRequested = value; UpdateEnabled(); } }
    internal Action<bool>? CheckAtStartupChanged { get; set; }

    internal ChecklistSettingsPage(TranslationCatalog translations)
    {
        _translations = translations; Name = "ChecklistSettingsPage";
        Grid grid = new() { Name = "tableLayoutPanel1", ColumnDefinitions = new("*,Auto"), RowDefinitions = new(string.Join(",", Enumerable.Repeat("Auto", 11))), ColumnSpacing = 6, RowSpacing = 5 };
        TextBlock intro = new() { Name = "label11", Text = Text("label11", "The checklist below validates the basic settings needed for Git Extensions to work properly."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) };
        Grid.SetColumnSpan(intro, 2); grid.Children.Add(intro);
        (GitEnvironmentCheckId Id, string Name)[] ordered =
        [
            (GitEnvironmentCheckId.GitExecutable, "GitFound"), (GitEnvironmentCheckId.GlobalIdentity, "UserNameSet"),
            (GitEnvironmentCheckId.Editor, "Editor"), (GitEnvironmentCheckId.MergeTool, "MergeTool"),
            (GitEnvironmentCheckId.DiffTool, "DiffTool"), (GitEnvironmentCheckId.Shell, "GitBinFound"),
            (GitEnvironmentCheckId.Ssh, "SshConfig"), (GitEnvironmentCheckId.Translation, "translationConfig"),
            (GitEnvironmentCheckId.Home, "HomeDirectory")
        ];
        for (int index = 0; index < ordered.Length; index++)
        {
            (GitEnvironmentCheckId id, string name) = ordered[index];
            Button status = new() { Name = name, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new TextBlock { Name = name + "Message", TextWrapping = TextWrapping.Wrap } };
            Button repair = new() { Name = name + "_Fix", Content = Text(name + "_Fix", "Repair"), HorizontalAlignment = HorizontalAlignment.Stretch };
            status.Click += async (_, _) => await RepairAsync(id); repair.Click += async (_, _) => await RepairAsync(id);
            Grid.SetRow(status, index + 1); Grid.SetRow(repair, index + 1); Grid.SetColumn(repair, 1);
            grid.Children.Add(status); grid.Children.Add(repair); _rows.Add(id, (status, repair));
        }
        _startup = new() { Name = "CheckAtStartup", Content = new TextBlock { Text = Text("CheckAtStartup", "Check settings at startup (disables automatically if all settings are correct)"), TextWrapping = TextWrapping.Wrap } };
        _startup.IsCheckedChanged += (_, _) => { if (!_rendering && !_invalidated) { CheckAtStartupChanged?.Invoke(CheckAtStartup); } };
        Grid.SetRow(_startup, 10); Grid.SetColumnSpan(_startup, 2); grid.Children.Add(_startup);
        _rescan = new() { Name = "Rescan", Content = Text("Rescan", "Save and rescan"), HorizontalAlignment = HorizontalAlignment.Left };
        _rescan.Click += async (_, _) => { if (SaveAndRescanRequested is { } scan) { await RunAsync(scan); } };
        Content = new StackPanel { Spacing = 8, Children = { grid, _error, _rescan } };
        ActualThemeVariantChanged += (_, _) => UpdateRows();
        Render([], true);
    }

    internal void Render(IReadOnlyList<GitEnvironmentCheck> checks, bool checkAtStartup)
    {
        if (_invalidated) { return; }
        _checks.Clear(); foreach (GitEnvironmentCheck check in checks) { _checks[check.Id] = check; }
        _rendering = true;
        try { _startup.IsChecked = checkAtStartup; }
        finally { _rendering = false; }
        UpdateRows();
    }
    internal void SetBusy(bool busy) { _busy = busy; UpdateEnabled(); }
    internal void Invalidate() { _invalidated = true; UpdateEnabled(); }
    internal void ShowError(string? message) { _error.Text = message; _error.IsVisible = !string.IsNullOrEmpty(message); }
    private string Text(string name, string source) => _translations.Translate("ChecklistSettingsPage", name, "Text", source);
    private Task RepairAsync(GitEnvironmentCheckId id) => _checks.TryGetValue(id, out GitEnvironmentCheck? check) && check.CanRepair && RepairRequested is { } repair ? RunAsync(() => repair(id)) : Task.CompletedTask;
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _actionBusy || _invalidated) { return; }
        _actionBusy = true; UpdateEnabled();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_invalidated) { ShowError(error.Message); } }
        finally { _actionBusy = false; UpdateEnabled(); }
    }
    private void UpdateEnabled()
    {
        bool enabled = !_busy && !_actionBusy && !_invalidated;
        _startup.IsEnabled = enabled;
        _rescan.IsEnabled = enabled && SaveAndRescanRequested is not null;
        foreach ((GitEnvironmentCheckId id, (Button status, Button repair)) in _rows)
        {
            bool canRepair = enabled && RepairRequested is not null && _checks.TryGetValue(id, out GitEnvironmentCheck? check) && check.CanRepair;
            status.IsEnabled = canRepair; repair.IsEnabled = canRepair;
        }
    }
    private void UpdateRows()
    {
        foreach ((GitEnvironmentCheckId id, (Button status, Button repair)) in _rows)
        {
            GitEnvironmentCheck? check = _checks.GetValueOrDefault(id);
            ((TextBlock)status.Content!).Text = check?.Message ?? _translations.Translate("Not checked.");
            repair.IsVisible = check is { CanRepair: true } && check.Status != GitEnvironmentCheckStatus.Good;
            Color source = (check?.Status ?? GitEnvironmentCheckStatus.Unavailable) switch
            {
                GitEnvironmentCheckStatus.Good => Color.FromRgb(128, 255, 128), GitEnvironmentCheckStatus.Warning => Color.FromRgb(255, 255, 128),
                GitEnvironmentCheckStatus.Error => Color.FromRgb(255, 128, 128), _ => Color.FromRgb(192, 192, 192)
            };
            bool dark = ActualThemeVariant == ThemeVariant.Dark;
            Color editor = this.TryFindResource("ClassicEditorBrush", ActualThemeVariant, out object? resource) && resource is ISolidColorBrush brush ? brush.Color : Color.Parse("#323232");
            Color background = dark ? Dim(source, editor) : source;
            status.Background = new SolidColorBrush(background);
            status.Foreground = new SolidColorBrush(ClassicTextContrast.AdaptForeground(background, dark ? Colors.White : Colors.Black));
            Color hover = dark ? Dim(Color.FromRgb(192, 192, 255), editor) : Color.FromRgb(192, 192, 255);
            status.Styles.Clear();
            foreach (string state in new[] { ":pointerover", ":pressed" })
            {
                status.Styles.Add(new Style(selector => selector.OfType<Button>().Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter")) { Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(hover)),
                    new Setter(ContentPresenter.ForegroundProperty, new SolidColorBrush(ClassicTextContrast.AdaptForeground(hover, dark ? Colors.White : Colors.Black)))
                } });
            }
        }
        UpdateEnabled();
    }
    // OtherColors.DimColor blends status colors with the editor background in linear sRGB.
    private static Color Dim(Color first, Color second)
    {
        static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        static byte Channel(byte a, byte b)
        { double value = (Linear(a) + Linear(b)) / 2; return (byte)Math.Round(255 * (value <= .0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - .055)); }
        return Color.FromRgb(Channel(first.R, second.R), Channel(first.G, second.G), Channel(first.B, second.B));
    }
}

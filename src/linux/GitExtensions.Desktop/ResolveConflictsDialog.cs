using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

/// <summary>A reopenable view of the actual unmerged index, shared by merge and revert.</summary>
public sealed class ResolveConflictsDialog : RepositoryDialog
{
    private readonly ListBox _files = new() { Name = "ConflictFiles", MinHeight = 140 };
    private readonly TextBlock _stages = new() { Name = "ConflictStages", TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<GitConflictResolution, Button> _choices = [];
    private readonly Button _tool;
    public IReadOnlyList<GitConflictPath> Conflicts { get; private set; } = [];

    public ResolveConflictsDialog(GitRepository repository) : base(repository, "Resolve conflicts")
    {
        Name = "ResolveConflictsDialog"; Width = 690; Height = 430; MinWidth = 590; MinHeight = 340;
        _files.ItemTemplate = new FuncDataTemplate<GitConflictPath>((item, _) => new TextBlock { Text = item is null ? "" : ExportDialogControls.DisplayPath(item.Path), Margin = new Thickness(3), TextTrimming = TextTrimming.CharacterEllipsis });
        _files.SelectionChanged += (_, _) => UpdateActions();
        Track(_files);
        StackPanel actions = new() { Spacing = 4, Margin = new Thickness(6, 0, 0, 0) };
        Add(GitConflictResolution.Base, "Use base version"); Add(GitConflictResolution.Ours, "Use local version (ours)");
        Add(GitConflictResolution.Theirs, "Use remote version (theirs)"); Add(GitConflictResolution.Delete, "Delete file");
        Add(GitConflictResolution.Manual, "Mark as resolved");
        _tool = Action("ConflictMergeTool", "Open merge tool", MergeToolAsync); _tool.Height = 25; actions.Children.Add(_tool);
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("*,210"), Margin = new Thickness(10) };
        content.Children.Add(ClassicChrome.Border(_files, new Thickness(1))); Grid.SetColumn(actions, 1); content.Children.Add(actions);
        StackPanel footer = ExportDialogControls.Buttons(Action("RefreshConflicts", "Refresh", InitializeAsync), Action("CloseConflicts", "Close", () => { Close(); return Task.CompletedTask; }));
        DockPanel.SetDock(footer, Dock.Bottom); Root.Children.Add(footer);
        _stages.Margin = new Thickness(10, 2); DockPanel.SetDock(_stages, Dock.Bottom); Root.Children.Add(_stages); Root.Children.Add(content);
        UpdateActions();
        void Add(GitConflictResolution choice, string label)
        {
            Button button = Action("Resolve" + choice, label, () => ResolveAsync(choice)); button.Height = 25;
            _choices[choice] = button; actions.Children.Add(button);
        }
    }
    public override Task InitializeAsync() => RunAsync("Load conflicts", ReloadAsync);
    private async Task ReloadAsync(CancellationToken token)
    {
        string? selected = (_files.SelectedItem as GitConflictPath)?.Path;
        Conflicts = await Repository.GetConflictsAsync(token);
        _files.ItemsSource = Conflicts;
        _files.SelectedItem = Conflicts.FirstOrDefault(item => item.Path == selected) ?? Conflicts.FirstOrDefault();
        Status.Text = Conflicts.Count == 0 ? T("All conflicts have been resolved.") : T("Resolve the files, then continue the interrupted operation.");
    }
    private async Task ResolveAsync(GitConflictResolution choice)
    {
        if (IsBusy || _files.SelectedItem is not GitConflictPath selected) { return; }
        if (choice != GitConflictResolution.Manual
            && !await Dialogs.ConfirmAsync(this, T("Resolve conflicts"), T("Replace the working file with the selected version?") + "\n" + ExportDialogControls.DisplayPath(selected.Path))) { return; }
        if (!IsVisible) { return; }
        await RunAsync("Resolve conflicts", async token =>
        {
            MarkRepositoryChanged();
            await Repository.ResolveConflictAsync(selected.Path, choice, token);
            await ReloadAsync(token);
        });
    }
    private Task MergeToolAsync()
    {
        if (IsBusy || _files.SelectedItem is not GitConflictPath selected) { return Task.CompletedTask; }
        return RunAsync("Open merge tool", async token => { MarkRepositoryChanged(); await Repository.RunMergeToolAsync(selected.Path, token); await ReloadAsync(token); });
    }
    protected override void UpdateActions()
    {
        GitConflictPath? selected = _files.SelectedItem as GitConflictPath;
        foreach ((GitConflictResolution choice, Button button) in _choices)
        {
            int stage = choice switch { GitConflictResolution.Base => 1, GitConflictResolution.Ours => 2, GitConflictResolution.Theirs => 3, _ => 0 };
            button.IsEnabled = !IsBusy && selected is not null && (stage == 0 || selected.Stages.Any(item => item.Stage == stage && item.Mode != "160000"));
        }
        if (_tool is not null) { _tool.IsEnabled = !IsBusy && selected is not null; }
        _stages.Text = selected is null ? "" : string.Join("\n", selected.Stages.Select(stage => $"{stage.Stage}: {stage.Mode}  {stage.Hash}"));
    }
    public static async Task<bool> ShowAsync(Window owner, GitRepository repository, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ResolveConflictsDialog dialog = new(repository) { RequestedThemeVariant = owner.ActualThemeVariant };
        dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        using CancellationTokenRegistration registration = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        try { await dialog.ShowDialog(owner); token.ThrowIfCancellationRequested(); return (await repository.GetConflictsAsync(token)).Count == 0; }
        finally { dialog.Close(); }
    }
}

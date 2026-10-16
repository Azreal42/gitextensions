using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop;

/// <summary>FormManageWorktree, with the same actions shared by navigation and toolbar entry points.</summary>
public sealed class WorktreeDialog : RepositoryDialog
{
    private readonly WorktreeTable _table;
    private readonly Button _open, _delete, _prune, _create;
    private readonly bool _autoNormalize;
    private readonly string _replacement;
    private readonly string? _selectedPath;
    private IReadOnlyList<WorktreeSnapshot> _worktrees = [];
    public string? RequestedWorktreePath { get; private set; }

    public WorktreeDialog(GitRepository repository, string? selectedPath = null, bool autoNormalize = true, string replacement = "_")
        : base(repository, "Existing worktrees")
    {
        Name = "WorktreeDialog"; Title = Text("$this", "Existing worktrees");
        Width = 710; Height = 361; MinWidth = 710; MinHeight = 200;
        _selectedPath = selectedPath; _autoNormalize = autoNormalize; _replacement = replacement;
        ConstrainStatusHeight("WorktreeStatusScroll", 40);
        CollapseEmptyStatus();
        _table = new WorktreeTable(header => Translations.Translate("FormManageWorktree", header == "SHA-1" ? "Sha1" : header, "HeaderText", header));
        _open = Button("OpenSelectedWorktree", "buttonOpenSelectedWorktree", "&Open selected", OpenAsync, "BrowseFileExplorer"); _open.IsDefault = true;
        _delete = Button("DeleteSelectedWorktree", "buttonDeleteSelectedWorktree", "&Delete selected", DeleteAsync, "DeleteFile");
        _prune = Button("PruneWorktrees", "buttonPruneWorktrees", "&Prune deleted worktrees", PruneAsync);
        _create = Button("CreateWorktree", "buttonCreateNewWorktree", "&Create...", CreateAsync, "FileStatusAdded");
        Grid footer = new() { ColumnDefinitions = new("Auto,*,Auto"), Margin = new Thickness(12, 8) };
        footer.Children.Add(_prune); Grid.SetColumn(_create, 2); footer.Children.Add(_create); DockPanel.SetDock(footer, Dock.Bottom); Root.Children.Add(footer);
        StackPanel selected = ExportDialogControls.Buttons(_delete, _open); selected.Margin = new Thickness(0, 6, 0, 0);
        DockPanel content = new() { Margin = new Thickness(12, 9, 12, 0) }; DockPanel.SetDock(selected, Dock.Bottom); content.Children.Add(selected); content.Children.Add(_table); Root.Children.Add(content);
        Track(_table.Rows);
        _table.Rows.SelectionChanged += (_, _) => UpdateActions();
        _table.Rows.DoubleTapped += async (_, args) =>
        {
            if ((args.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
                .Any(item => ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), _table.Rows)) == true) { await OpenAsync(); }
        };
        _table.Rows.AddHandler(KeyDownEvent, async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await OpenAsync(); } }, RoutingStrategies.Tunnel);
        Opened += (_, _) => { if (_worktrees.Count > 0) { _table.Rows.Focus(); } };
        UpdateActions();
    }

    private string Text(string item, string source) => Translations.Translate("FormManageWorktree", item, "Text", source);
    private Button Button(string name, string item, string caption, Func<Task> action, string? icon = null)
    {
        Button button = Action(name, caption.Replace("&", "", StringComparison.Ordinal), action);
        button.Content = WorktreeDialogControls.ButtonContent(new AccessText { Text = Translations.TranslateAccessKeys("FormManageWorktree", item, "Text", caption) }, icon);
        button.Height = 25; button.MinWidth = 100; return button;
    }

    public override async Task InitializeAsync()
    {
        await RunAsync("Load worktrees", token => ReloadAsync(_selectedPath, token));
        if (IsVisible) { _table.Rows.Focus(); }
    }

    private async Task ReloadAsync(string? selected, CancellationToken token)
    {
        _worktrees = await Repository.GetWorktreeSnapshotsAsync(token);
        _table.SetItems(_worktrees);
        _table.Rows.SelectedItem = _worktrees.FirstOrDefault(worktree => worktree.Path == selected) ?? _worktrees.FirstOrDefault();
        Status.Text = "";
    }

    private async Task OpenAsync()
    {
        if (IsBusy || !IsVisible || _table.Rows.SelectedItem is not WorktreeSnapshot { CanOpen: true } selected) { return; }
        await RunAsync("Open selected", async token =>
        {
            if (await WorktreeDialogControls.ConfirmOpenAsync(this, Repository, selected.Path, token)) { RequestedWorktreePath = selected.Path; Close(); }
        });
        if (IsVisible) { _table.Rows.Focus(); }
    }

    private async Task DeleteAsync()
    {
        if (IsBusy || !IsVisible || _table.Rows.SelectedItem is not WorktreeSnapshot { CanRemove: true } selected) { return; }
        await RunAsync("Delete selected", async token =>
        {
            try { if (await WorktreeDialogControls.DeleteAsync(this, Repository, selected.Path, token)) { MarkRepositoryChanged(); } }
            finally { await ReloadAsync(selected.Path, CancellationToken.None); }
        });
        if (IsVisible) { _table.Rows.Focus(); }
    }

    private async Task PruneAsync()
    {
        if (IsBusy || !IsVisible || !_prune.IsEnabled) { return; }
        string? selected = (_table.Rows.SelectedItem as WorktreeSnapshot)?.Path;
        await RunAsync("Prune deleted worktrees", async token =>
        {
            try { if (await WorktreeDialogControls.PruneAsync(this, Repository, token)) { MarkRepositoryChanged(); } }
            finally { await ReloadAsync(selected, CancellationToken.None); }
        });
        if (IsVisible) { _table.Rows.Focus(); }
    }

    private async Task CreateAsync()
    {
        if (IsBusy || !IsVisible) { return; }
        CreateWorktreeDialog dialog = new(Repository, _worktrees.FirstOrDefault(worktree => worktree.IsMain)?.Path, _autoNormalize, _replacement)
            { RequestedThemeVariant = ActualThemeVariant };
        dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        await RunAsync("Create a new worktree", async token =>
        {
            using CancellationTokenRegistration registration = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
            try
            {
                await dialog.ShowDialog(this);
                if (dialog.HasRepositoryChanges) { MarkRepositoryChanged(); }
                token.ThrowIfCancellationRequested();
                await ReloadAsync(dialog.CreatedWorktreePath, token);
                if (dialog.CreatedWorktreePath is string created && await WorktreeDialogControls.ConfirmOpenAsync(this, Repository, created, token))
                { RequestedWorktreePath = created; Close(); }
            }
            finally
            {
                dialog.Close(); if (dialog.HasRepositoryChanges) { MarkRepositoryChanged(); }
            }
        });
        if (IsVisible) { _table.Rows.Focus(); }
    }

    protected override void UpdateActions()
    {
        WorktreeSnapshot? selected = _table.Rows.SelectedItem as WorktreeSnapshot;
        _open.IsEnabled = !IsBusy && _worktrees.Count > 1 && selected?.CanOpen == true;
        _delete.IsEnabled = !IsBusy && _worktrees.Count > 1 && selected?.CanRemove == true;
        _prune.IsEnabled = !IsBusy && _worktrees.Any(worktree => !worktree.IsMain && !worktree.Exists);
        _create.IsEnabled = !IsBusy;
    }
}

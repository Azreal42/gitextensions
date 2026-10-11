using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

public sealed class DeleteBranchDialog : RepositoryDialog
{
    private readonly string[] _initial;
    private readonly ListBox _branches = new() { Name = "DeleteBranchSelection", SelectionMode = SelectionMode.Multiple, Height = 145 };
    private readonly CheckBox _force = new() { Name = "ForceDeleteBranches" };
    private readonly CheckBox _removeWorktrees = new() { Name = "RemoveBranchWorktrees" };
    private readonly CheckBox _forceWorktrees = new() { Name = "ForceRemoveBranchWorktrees" };
    private readonly TextBlock _selection = new() { Name = "DeleteBranchSummary", TextWrapping = TextWrapping.Wrap };
    private readonly Button _delete;
    private string? _current;

    public DeleteBranchDialog(GitRepository repository, IEnumerable<string>? branches = null) : base(repository, "Delete Branches")
    {
        _initial = branches?.ToArray() ?? [];
        Width = 700;
        MinWidth = 540;
        SizeToContent = SizeToContent.Height;
        _force.Content = T("Force deletion of unmerged branches");
        _removeWorktrees.Content = T("Remove linked worktrees with the selected branches");
        _forceWorktrees.Content = T("Remove worktrees even if they contain changes");
        ReferenceDialogControls.Compact(_force, _removeWorktrees, _forceWorktrees);
        _delete = Action("DeleteBranches", "Delete", DeleteAsync);
        StackPanel buttons = ExportDialogControls.Buttons(Action("CancelDeleteBranches", "Cancel", () => { Close(); return Task.CompletedTask; }), _delete);
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        _branches.ItemTemplate = new FuncDataTemplate<GitBranchDeletionPreview>((branch, _) =>
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("180,90,85,*"), Height = 24 };
            string[] values = branch is null ? ["", "", "", ""] : [branch.Reference.ShortName, T(branch.IsMerged ? "Merged" : "Unmerged"), branch.Reference.Hash[..Math.Min(9, branch.Reference.Hash.Length)], branch.Worktree is null ? "" : ExportDialogControls.DisplayPath(branch.Worktree.Path)];
            for (int column = 0; column < values.Length; column++)
            {
                TextBlock cell = new() { Text = values[column], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(cell, column);
                row.Children.Add(cell);
            }
            return row;
        });
        Root.Children.Add(new StackPanel { Margin = new Thickness(10), Spacing = 6, Children =
        {
            new TextBlock { Text = T("Select branches") }, _branches, _selection, _force, _removeWorktrees, _forceWorktrees,
            new TextBlock { Text = T("Did you know you can use reflog to restore deleted branches?"), TextWrapping = TextWrapping.Wrap }
        } });
        Track(_branches, _force, _removeWorktrees, _forceWorktrees);
        _branches.SelectionChanged += (_, _) => UpdateActions();
        _removeWorktrees.IsCheckedChanged += (_, _) => { if (_removeWorktrees.IsChecked != true) { _forceWorktrees.IsChecked = false; } UpdateActions(); };
        UpdateActions();
    }

    public override Task InitializeAsync() => RunAsync("Delete Branches", async token =>
    {
        _current = (await Repository.GetStatusAsync(token)).Branch;
        IReadOnlyList<GitBranchDeletionPreview> branches = await Repository.GetBranchDeletionPreviewAsync(token);
        _branches.ItemsSource = branches;
        _branches.SelectedItems?.Clear();
        foreach (GitBranchDeletionPreview branch in branches.Where(branch => _initial.Contains(branch.Reference.ShortName, StringComparer.Ordinal))) { _branches.SelectedItems?.Add(branch); }
        Status.Text = T("Select branches");
    });

    public GitDeleteBranchesOptions ReadOptions() => new(Selected().Select(branch => branch.Reference.ShortName).ToArray(), _force.IsChecked == true,
        _removeWorktrees.IsChecked == true, _removeWorktrees.IsChecked == true && _forceWorktrees.IsChecked == true);

    private GitBranchDeletionPreview[] Selected() => _branches.SelectedItems?.Cast<GitBranchDeletionPreview>().ToArray() ?? [];

    private async Task DeleteAsync()
    {
        GitDeleteBranchesOptions options = ReadOptions();
        if (options.Names.Count == 0) { return; }
        string message = string.Join('\n', Selected().Select(branch => branch.Reference.ShortName + "  " + branch.Reference.Hash));
        if (options.Force && Selected().Any(branch => !branch.IsMerged)) { message += "\n\n" + T("The selected branch(es) have not been merged into HEAD. Proceed?"); }
        if (options.RemoveLinkedWorktrees) { message += "\n\n" + T("Remove selected linked worktrees:") + "\n" + string.Join('\n', Selected().Where(branch => branch.Worktree is not null).Select(branch => ExportDialogControls.DisplayPath(branch.Worktree!.Path))); }
        if (options.ForceWorktreeRemoval) { message += "\n" + T("Changes in these worktrees will be discarded."); }
        if (!await Dialogs.ConfirmAsync(this, T("Delete Confirmation"), message)) { return; }
        await RunAsync("Delete Branches", async token =>
        {
            try { await Repository.DeleteBranchesAsync(options, token); }
            finally { MarkRepositoryChanged(); _branches.ItemsSource = await Repository.GetBranchDeletionPreviewAsync(token); }
            Close();
        });
    }

    protected override void UpdateActions()
    {
        GitBranchDeletionPreview[] selected = Selected();
        bool blocked = selected.Any(branch => branch.Reference.ShortName == _current || branch.IsMainWorktree || branch.Worktree?.LockedReason is not null);
        _removeWorktrees.IsEnabled = !IsBusy && selected.Any(branch => branch.Worktree is not null && !branch.IsMainWorktree);
        if (!selected.Any(branch => branch.Worktree is not null && !branch.IsMainWorktree)) { _removeWorktrees.IsChecked = false; }
        _forceWorktrees.IsEnabled = !IsBusy && _removeWorktrees.IsChecked == true;
        _delete.IsEnabled = !IsBusy && selected.Length > 0 && !blocked;
        _selection.Text = blocked ? T("A selected branch is current, locked, or checked out in the main worktree.") : selected.Length + " " + T("Selected branches") + "    " + selected.Count(branch => !branch.IsMerged) + " " + T("Unmerged");
    }
}

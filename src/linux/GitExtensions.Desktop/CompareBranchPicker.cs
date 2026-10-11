using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace GitExtensions.Linux.Desktop;

internal sealed class CompareBranchPicker : RepositoryDialog
{
    private readonly GitCompareRevision _from;
    private readonly string? _initial;
    private readonly RadioButton _local = new() { Name = "ComparePickLocal", GroupName = "ComparePickBranchKind" };
    private readonly RadioButton _remote = new() { Name = "ComparePickRemote", GroupName = "ComparePickBranchKind", IsChecked = true };
    private readonly ComboBox _branches = ReferenceDialogControls.Choice("ComparePickBranch");
    private readonly TextBlock _counts = new() { Name = "ComparePickCounts" };
    private readonly Button _accept;
    private IReadOnlyList<GitReference> _references = [];
    private bool _loading;

    private CompareBranchPicker(GitRepository repository, GitCompareRevision from, string? initial) : base(repository, "Compare to branch")
    {
        _from = from; _initial = initial;
        Width = 460; MinWidth = 400; SizeToContent = SizeToContent.Height;
        _local.Content = T("Local branch"); _remote.Content = T("Remote branch");
        ReferenceDialogControls.Compact(_local, _remote);
        ReferenceDialogControls.ReferenceTemplate(_branches);
        _accept = Action("ComparePickAccept", "Compare", () => { if (_branches.SelectedItem is GitReference branch) { Close(branch); } return Task.CompletedTask; });
        _accept.IsDefault = true;
        StackPanel buttons = ExportDialogControls.Buttons(Action("ComparePickCancel", "Cancel", () => { Close(); return Task.CompletedTask; }), _accept);
        DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons);
        Root.Children.Add(new StackPanel { Margin = new Thickness(8), Spacing = 6, Children =
        { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Children = { _local, _remote } }, _branches, _counts } });
        Track(_local, _remote, _branches);
        _remote.IsCheckedChanged += async (_, _) => { if (!_loading) { Populate(); await LoadCountsAsync(); } };
        _branches.SelectionChanged += async (_, _) => { if (!_loading && !IsBusy) { await LoadCountsAsync(); } };
        UpdateActions();
    }

    internal static async Task<GitReference?> ShowAsync(Window owner, GitRepository repository, GitCompareRevision from, string? initial)
    {
        CompareBranchPicker picker = new(repository, from, initial);
        picker.Opened += async (_, _) => await picker.InitializeAsync();
        EventHandler closed = (_, _) => picker.Close();
        owner.Closed += closed;
        try { return await picker.ShowDialog<GitReference?>(owner); }
        finally { owner.Closed -= closed; }
    }
    public override Task InitializeAsync() => RunAsync("Select branch", async token =>
    {
        _loading = true;
        try
        {
            _references = await Repository.GetReferencesAsync(token);
            if (_initial?.StartsWith("refs/heads/", StringComparison.Ordinal) == true) { _local.IsChecked = true; }
            Populate();
            _branches.SelectedItem = _branches.Items.OfType<GitReference>().FirstOrDefault(reference => reference.Name == _initial);
            await ReadCountsAsync(token);
        }
        finally { _loading = false; }
    });
    private void Populate()
    {
        _loading = true;
        try { _branches.ItemsSource = _references.Where(reference => _remote.IsChecked == true ? reference.IsRemote && reference.SymbolicTarget is null : reference.IsBranch).ToArray(); }
        finally { _loading = false; }
        UpdateActions();
    }
    private Task LoadCountsAsync() => RunAsync("Compare branch", ReadCountsAsync);
    private async Task ReadCountsAsync(CancellationToken token)
    {
        _counts.Text = "";
        if (_branches.SelectedItem is GitReference selected)
        {
            GitComparisonCommitCounts? counts = await Repository.GetComparisonCommitCountsAsync(_from, new(selected.Name), token);
            if (counts is not null) { _counts.Text = T("BASE") + ": " + counts.BaseOnly + "    " + T("Compare") + ": " + counts.CompareOnly + " " + T("commits"); }
        }
        Status.Text = T("Select branch");
    }
    protected override void UpdateActions() => _accept.IsEnabled = !IsBusy && _branches.SelectedItem is GitReference;
}

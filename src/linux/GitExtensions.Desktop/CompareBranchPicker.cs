using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

internal sealed class CompareBranchPicker : RepositoryDialog
{
    private readonly GitCompareRevision _from;
    private readonly string? _initial;
    private readonly CancellationToken _token;
    private readonly RadioButton _local = new() { Name = "ComparePickLocal", GroupName = "ComparePickBranchKind" };
    private readonly RadioButton _remote = new() { Name = "ComparePickRemote", GroupName = "ComparePickBranchKind", IsChecked = true };
    private readonly ComboBox _branches = ReferenceDialogControls.Choice("ComparePickBranch");
    private readonly TextBlock _counts = new() { Name = "ComparePickCounts" };
    private readonly Button _accept;
    private IReadOnlyList<GitReference> _references = [];
    private bool _loading, _closing;
    private Task _work = Task.CompletedTask;
    internal Task Completion => _work;

    private CompareBranchPicker(GitRepository repository, GitCompareRevision from, string? initial, CancellationToken token) : base(repository, "Compare to branch")
    {
        _from = from; _initial = initial; _token = token;
        Width = 460; MinWidth = 400; SizeToContent = SizeToContent.Height;
        _local.Content = T("Local branch"); _remote.Content = T("Remote branch");
        ReferenceDialogControls.Compact(_local, _remote);
        ReferenceDialogControls.ReferenceTemplate(_branches);
        _accept = Action("ComparePickAccept", "Compare", () => { if (!_closing && !IsBusy && _branches.SelectedItem is GitReference branch) { Close(branch); } return Task.CompletedTask; });
        _accept.IsDefault = true;
        StackPanel buttons = ExportDialogControls.Buttons(Action("ComparePickCancel", "Cancel", () => { Close(); return Task.CompletedTask; }), _accept);
        DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons);
        Root.Children.Add(new StackPanel { Margin = new Thickness(8), Spacing = 6, Children =
        { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Children = { _local, _remote } }, _branches, _counts } });
        Track(_local, _remote, _branches);
        _remote.IsCheckedChanged += async (_, _) => { if (!_loading && !_closing && !IsBusy) { Populate(); await LoadCountsAsync(); } };
        _branches.SelectionChanged += async (_, _) => { if (!_loading && !_closing && !IsBusy) { await LoadCountsAsync(); } };
        Closing += (_, _) => _closing = true;
        UpdateActions();
    }

    internal static async Task<GitReference?> ShowAsync(Window owner, GitRepository repository, GitCompareRevision from, string? initial,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); if (!owner.IsVisible) { return null; }
        CompareBranchPicker picker = new(repository, from, initial, token) { RequestedThemeVariant = owner.ActualThemeVariant };
        picker.Opened += async (_, _) => await picker.InitializeAsync();
        EventHandler closed = (_, _) => picker.Close();
        owner.Closed += closed;
        using CancellationTokenRegistration close = token.Register(() => Dispatcher.UIThread.Post(() => { if (picker.IsVisible) { picker.Close(); } }));
        try
        {
            GitReference? selected = await picker.ShowDialog<GitReference?>(owner);
            await picker.Completion; token.ThrowIfCancellationRequested(); return owner.IsVisible ? selected : null;
        }
        finally { owner.Closed -= closed; picker.Close(); await picker.Completion; }
    }

    public override Task InitializeAsync()
    {
        if (_closing || IsBusy) { return _work; }
        return _work = RunAsync("Select branch", async token =>
        {
            using IDisposable environment = Repository.BeginOperation();
            _loading = true;
            try
            {
                IReadOnlyList<GitReference> references = await Repository.GetReferencesAsync(token);
                token.ThrowIfCancellationRequested(); if (_closing) { return; }
                _references = references;
                if (_initial?.StartsWith("refs/heads/", StringComparison.Ordinal) == true) { _local.IsChecked = true; }
                Populate();
                _branches.SelectedItem = _branches.Items.OfType<GitReference>().FirstOrDefault(reference => reference.Name == _initial);
                await ReadCountsAsync(token);
            }
            finally { _loading = false; }
        }, cancellationToken: _token);
    }
    private void Populate()
    {
        _loading = true;
        try { _branches.ItemsSource = _references.Where(reference => _remote.IsChecked == true ? reference.IsRemote && reference.SymbolicTarget is null : reference.IsBranch).ToArray(); }
        finally { _loading = false; }
        UpdateActions();
    }
    private Task LoadCountsAsync()
    {
        if (_closing || IsBusy) { return _work; }
        return _work = RunAsync("Compare branch", async token =>
        {
            using IDisposable environment = Repository.BeginOperation();
            await ReadCountsAsync(token);
        }, cancellationToken: _token);
    }
    private async Task ReadCountsAsync(CancellationToken token)
    {
        _counts.Text = "";
        if (_branches.SelectedItem is GitReference selected)
        {
            GitComparisonCommitCounts? counts = await Repository.GetComparisonCommitCountsAsync(_from, new(selected.Name), token);
            token.ThrowIfCancellationRequested(); if (_closing) { return; }
            if (counts is not null) { _counts.Text = T("BASE") + ": " + counts.BaseOnly + "    " + T("Compare") + ": " + counts.CompareOnly + " " + T("commits"); }
        }
        if (!_closing) { Status.Text = T("Select branch"); }
    }
    protected override void UpdateActions() => _accept.IsEnabled = !_closing && !IsBusy && _branches.SelectedItem is GitReference;
}

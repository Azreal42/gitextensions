using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

public sealed partial class RemotesDialog
{
    private readonly ListBox _trackingList = new() { Name = "BranchTrackingList", SelectionMode = SelectionMode.Single, Focusable = true };
    private readonly TextBox _trackingLocal = new() { Name = "TrackingLocalBranch", IsReadOnly = true, Height = 24 };
    private readonly ComboBox _trackingRemote = ReferenceDialogControls.Choice("TrackingRemote");
    private readonly ComboBox _trackingMerge = EditableChoice("TrackingMergeBranch");
    private readonly Button _trackingSave;
    private IReadOnlyList<GitBranchTrackingSnapshot> _tracking = [];
    private GitBranchTrackingSnapshot? _trackingSelected;
    private Task _trackingWrites = Task.CompletedTask;
    private readonly Dictionary<(string Branch, bool Remote), Exception> _trackingErrors = [];
    private int _trackingSort;
    private bool _trackingDescending, _remoteDirty, _mergeDirty, _closingTracking, _trackingOperation;

    private Control BuildTrackingLayout()
    {
        CompactList(_trackingList);
        _trackingList.ItemTemplate = new FuncDataTemplate<GitBranchTrackingSnapshot>((branch, _) =>
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
            string[] values = [branch?.LocalBranch ?? "", branch?.Remote ?? "", branch?.MergeBranch ?? ""];
            for (int i = 0; i < values.Length; i++)
            {
                TextBlock value = new() { Text = values[i], Margin = new Thickness(4, 1), MinHeight = 18, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
                Grid.SetColumn(value, i); row.Children.Add(value);
            }
            return row;
        });
        ScrollViewer.SetHorizontalScrollBarVisibility(_trackingList, ScrollBarVisibility.Disabled);
        Grid headers = new() { Name = "BranchTrackingHeaders", ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        string[] captions = [C("BranchName", "Local branch name", "HeaderText"), C("RemoteCombo", "Remote repository", "HeaderText"), C("MergeWith", "Default merge with", "HeaderText")];
        for (int i = 0; i < captions.Length; i++)
        {
            int column = i;
            Button heading = new() { Name = "BranchTrackingSort" + i, Content = captions[i], Height = 23, Padding = new Thickness(4, 1), HorizontalContentAlignment = HorizontalAlignment.Left };
            heading.Click += (_, _) =>
            {
                _trackingDescending = _trackingSort == column && !_trackingDescending; _trackingSort = column;
                BindTracking(_trackingSelected?.LocalBranch);
            };
            Grid.SetColumn(heading, i); headers.Children.Add(heading); Track(heading);
        }
        DockPanel table = new(); DockPanel.SetDock(headers, Dock.Top); table.Children.Add(headers); table.Children.Add(_trackingList);
        Grid fields = new() { Name = "BranchTrackingFields", ColumnDefinitions = new ColumnDefinitions("150,*,Auto"), Margin = new Thickness(32, 5, 50, 5) };
        Field(fields, "label4", "&Local branch name", _trackingLocal);
        Field(fields, "label5", "&Remote repository", _trackingRemote);
        Field(fields, "label6", "&Default merge with", _trackingMerge);
        _trackingSave.MinWidth = 130; _trackingSave.HorizontalAlignment = HorizontalAlignment.Right;
        int r = fields.RowDefinitions.Count; fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(_trackingSave, r); Grid.SetColumn(_trackingSave, 1); Grid.SetColumnSpan(_trackingSave, 2); fields.Children.Add(_trackingSave);
        Grid content = new() { Name = "DefaultPullBehaviorPage", RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(6) };
        content.Children.Add(table); Grid.SetRow(fields, 1); content.Children.Add(fields); return content;
    }

    private void ConfigureTracking()
    {
        _trackingList.SelectionChanged += (_, _) =>
        {
            if (_loading) { return; }
            _ = FlushTrackingAsync();
            LoadTrackingSelection();
        };
        _trackingRemote.SelectionChanged += (_, _) => { if (!_loading) { _remoteDirty = true; UpdateMergeChoices(); } };
        _trackingMerge.PropertyChanged += (_, change) => { if (!_loading && change.Property == ComboBox.TextProperty) { _mergeDirty = true; } };
        _trackingRemote.LostFocus += async (_, _) => { if (!_loading) { await QueueTrackingFieldAsync(remote: true); } };
        _trackingMerge.LostFocus += async (_, _) => { if (!_loading) { await QueueTrackingFieldAsync(remote: false); } };
        Closing += async (_, args) =>
        {
            if (_closingTracking || !_remoteDirty && !_mergeDirty && _trackingWrites.IsCompleted) { return; }
            args.Cancel = true; _closingTracking = true;
            try { await FlushTrackingAsync(); Close(); }
            finally { _closingTracking = false; }
        };
    }

    private void BindTracking(string? localBranch)
    {
        bool previous = _loading; _loading = true;
        try
        {
            Func<GitBranchTrackingSnapshot, string> selector = _trackingSort switch
            { 1 => item => item.Remote ?? "", 2 => item => item.MergeBranch ?? "", _ => item => item.LocalBranch };
            IOrderedEnumerable<GitBranchTrackingSnapshot> values = _trackingDescending ? _tracking.OrderByDescending(selector, StringComparer.Ordinal) : _tracking.OrderBy(selector, StringComparer.Ordinal);
            _trackingList.ItemsSource = values.ThenBy(item => item.LocalBranch, StringComparer.Ordinal).ToArray();
            _trackingList.SelectedItem = _tracking.FirstOrDefault(item => item.LocalBranch == localBranch) ?? _tracking.FirstOrDefault();
            _trackingRemote.ItemsSource = new[] { "", "." }.Concat(_values.Where(value => !value.Disabled).Select(value => value.Name)).Distinct(StringComparer.Ordinal).ToArray();
            LoadTrackingSelection();
        }
        finally { _loading = previous; }
    }

    private void LoadTrackingSelection()
    {
        bool previous = _loading; _loading = true;
        try
        {
            _trackingSelected = _trackingList.SelectedItem as GitBranchTrackingSnapshot;
            _trackingLocal.Text = _trackingSelected?.LocalBranch ?? "";
            _trackingRemote.SelectedItem = _trackingSelected?.Remote ?? "";
            SetText(_trackingMerge, _trackingSelected?.MergeBranch ?? "");
            _remoteDirty = _mergeDirty = false; UpdateMergeChoices();
        }
        finally { _loading = previous; }
        UpdateActions();
    }

    private void UpdateMergeChoices()
    {
        string remote = _trackingRemote.SelectedItem as string ?? "";
        string prefix = "refs/remotes/" + remote + "/";
        _trackingMerge.ItemsSource = new[] { "" }.Concat(_references.Where(reference => reference.SymbolicTarget is null
                && (remote == "." ? reference.IsBranch : reference.IsRemote && reference.Name.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(reference => remote == "." ? reference.ShortName : reference.Name[prefix.Length..])).Distinct(StringComparer.Ordinal).ToArray();
    }

    private Task QueueTrackingFieldAsync(bool remote)
    {
        if (_trackingSelected is not { } selected || (remote ? !_remoteDirty : !_mergeDirty)) { return _trackingWrites; }
        string branch = selected.LocalBranch;
        string value = remote ? _trackingRemote.SelectedItem as string ?? "" : _trackingMerge.Text ?? "";
        if (remote) { _remoteDirty = false; } else { _mergeDirty = false; }
        Task previous = _trackingWrites;
        return _trackingWrites = CommitAsync();

        async Task CommitAsync()
        {
            // Validated writes must not overtake each other when focus moves quickly.
            await previous;
            _trackingOperation = true;
            try { await RunAsync("Save changes", async token =>
            {
                // Validated must retain the editing focus and a pending Save click.
                UpdateActions();
                GitBranchTrackingSnapshot expected = _tracking.First(item => item.LocalBranch == branch);
                GitBranchTrackingSnapshot saved;
                try
                {
                    saved = remote ? await Repository.SetBranchTrackingRemoteAsync(expected, value, token)
                        : await Repository.SetBranchTrackingMergeAsync(expected, value, token);
                }
                catch (GitRemoteSettingsException exception) { if (exception.HasRepositoryChanges) { MarkRepositoryChanged(); } throw; }
                MarkRepositoryChanged();
                IReadOnlyList<GitBranchTrackingSnapshot> tracking = await Repository.GetBranchTrackingSettingsAsync(token);
                IReadOnlyList<GitRemoteSettingsSnapshot> remotes = await Repository.GetRemoteSettingsAsync(token);
                if (tracking.Any(item => item.ConfigurationRevision != saved.ConfigurationRevision)
                    || remotes.Any(item => item.ConfigurationRevision != saved.ConfigurationRevision))
                { throw new InvalidOperationException(T("Git configuration changed outside this editor. Reopen the remote settings before applying another change.")); }
                _tracking = tracking;
                _values = remotes.OrderBy(item => item.Disabled).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
                bool loading = _loading; _loading = true;
                try
                {
                    string? selectedRemote = _selected?.Name;
                    bool? disabled = _selected?.Disabled;
                    _remotes.ItemsSource = _values;
                    _remotes.SelectedItem = _values.FirstOrDefault(item => item.Name == selectedRemote && item.Disabled == disabled);
                    _selected = _remotes.SelectedItem as GitRemoteSettingsSnapshot;
                }
                finally { _loading = loading; }
                string? displayed = _trackingSelected?.LocalBranch;
                // Keep a deliberately edited merge value when the remote write initializes its default.
                string? remoteDraft = _remoteDirty ? _trackingRemote.SelectedItem as string ?? "" : null;
                string? mergeDraft = _mergeDirty ? _trackingMerge.Text ?? "" : null;
                BindTracking(displayed);
                _loading = true;
                try
                {
                    if (remoteDraft is not null) { _trackingRemote.SelectedItem = remoteDraft; _remoteDirty = true; UpdateMergeChoices(); }
                    if (mergeDraft is not null) { SetText(_trackingMerge, mergeDraft); _mergeDirty = true; }
                }
                finally { _loading = false; }
            }, disableInputs: false); }
            finally { _trackingOperation = false; UpdateActions(); }
            if (LastError is not null && _trackingSelected?.LocalBranch == branch)
            {
                _trackingErrors[(branch, remote)] = LastException ?? new InvalidOperationException(LastError);
                if (remote) { _remoteDirty = true; } else { _mergeDirty = true; }
            }
            else { _trackingErrors.Remove((branch, remote)); }
            if (_trackingErrors.Values.LastOrDefault() is Exception error) { ReportError(error); UpdateActions(); }
        }
    }

    private async Task FlushTrackingAsync()
    {
        // Capture both fields before the first asynchronous write can replace the selected row.
        Task remote = QueueTrackingFieldAsync(remote: true);
        Task merge = QueueTrackingFieldAsync(remote: false);
        await remote; await merge;
    }

    private async Task SaveTrackingAsync()
    {
        await FlushTrackingAsync();
        // Reserve the reload before it can move focus and queue another validation.
        Task previous = _trackingWrites;
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _trackingWrites = completed.Task;
        try
        {
            await previous;
            if (LastError is not null) { return; }
            await RunAsync("Remote repositories", token => ReloadAsync(_selected?.Name, _trackingSelected?.LocalBranch, token));
        }
        finally { completed.TrySetResult(); }
    }
}

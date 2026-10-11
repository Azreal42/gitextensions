using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

public sealed class FormatPatchDialog : RepositoryDialog
{
    private readonly string _initialRevision;
    private readonly ExportPreferencesStore _preferences = new();
    private readonly ExportRevisionList _revisions = new("FormatRevisions", true);
    private readonly List<GitCommit> _loaded = [];
    private readonly TextBox _output = new() { Name = "PatchOutputDirectory", Height = 24 };
    private readonly TextBlock _branch = new() { Name = "PatchCurrentBranch", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _selection = new() { Name = "PatchSelectionSummary", TextWrapping = TextWrapping.Wrap };
    private readonly RadioButton _selectedMode = new() { Name = "PatchSelectedMode", GroupName = "PatchSelection", IsChecked = true };
    private readonly RadioButton _rangeMode = new() { Name = "PatchRangeMode", GroupName = "PatchSelection" };
    private readonly TextBox _from = new() { Name = "PatchFromRevision", Height = 24 };
    private readonly TextBox _to = new() { Name = "PatchToRevision", Height = 24, Text = "HEAD" };
    private readonly NumericUpDown _start = new() { Name = "PatchStartNumber", Minimum = 1, Maximum = 100000, Value = 1, FormatString = "0", Width = 100, Height = 24, Padding = new Thickness(4, 2) };
    private readonly CheckBox _cover = new() { Name = "PatchCoverLetter" };
    private readonly CheckBox _signoff = new() { Name = "PatchSignOff" };
    private readonly CheckBox _renames = new() { Name = "PatchFindRenames", IsChecked = true };
    private readonly CheckBox _copies = new() { Name = "PatchFindCopies", IsChecked = true };
    private readonly CheckBox _rewrites = new() { Name = "PatchBreakRewrites", IsChecked = true };
    private readonly CheckBox _binary = new() { Name = "PatchBinary", IsChecked = true };
    private readonly TextBox _prefix = new() { Name = "PatchSubjectPrefix", Height = 24, Watermark = "PATCH" };
    private readonly ComboBox _numbered = new() { Name = "PatchNumbered", Height = 24, SelectedIndex = 0 };
    private readonly TextBox _created = new() { Name = "PatchCreatedFiles", AcceptsReturn = true, IsReadOnly = true, MinHeight = 70, FontFamily = new FontFamily("monospace") };
    private readonly Expander _result;
    private readonly Button _create;
    private readonly Button _more;
    private bool _hasMore;
    private int _readCount;
    public GitFormatPatchResult? LastResult { get; private set; }

    public FormatPatchDialog(GitRepository repository, string initialRevision = "HEAD") : base(repository, "Create patches")
    {
        _initialRevision = initialRevision;
        _output.Text = _preferences.Load().LastFormatPatchDirectory;
        Width = 860;
        Height = 640;
        MinWidth = 700;
        MinHeight = 560;
        _selectedMode.Content = T("Selected revisions");
        _rangeMode.Content = T("Revision range");
        _cover.Content = T("Cover letter");
        _signoff.Content = T("Sign-off");
        _renames.Content = T("Find renames");
        _copies.Content = T("Find copies");
        _rewrites.Content = T("Break rewrites");
        _binary.Content = T("Include binary changes");
        _numbered.ItemsSource = new[] { T("Git default"), T("Number patches"), T("Do not number patches") };
        _numbered.SelectedIndex = 0;
        _create = Action("CreatePatches", "Create patch(es)", CreateAsync);
        _create.IsDefault = true;
        _more = Action("MorePatchRevisions", "Load more", () => RunAsync("Load revisions", LoadMoreAsync));
        Grid footer = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10, 6) };
        footer.Children.Add(_branch);
        StackPanel buttons = ExportDialogControls.Buttons(Action("CloseFormatPatch", "Close", () => { Close(); return Task.CompletedTask; }), _create);
        buttons.Margin = new Thickness(0);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        Root.Children.Add(footer);
        _result = ExportDialogControls.Expandable("PatchResult", T("Patch result"), _created);
        _result.IsVisible = false;
        _result.Margin = new Thickness(10, 0, 10, 6);
        DockPanel.SetDock(_result, Dock.Bottom);
        Root.Children.Add(_result);
        StackPanel header = new() { Margin = new Thickness(10, 6), Spacing = 4 };
        Grid output = ExportDialogControls.Fields();
        ExportDialogControls.Field(output, T("Save patches to"), _output, Action("BrowsePatchOutput", "Browse…", BrowseAsync));
        header.Children.Add(output);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _selectedMode, _rangeMode } });
        Grid bounds = new() { ColumnDefinitions = new ColumnDefinitions("120,*,120,*") };
        AddBound(bounds, 0, T("After revision"), _from);
        AddBound(bounds, 2, T("Through revision"), _to);
        header.Children.Add(bounds);
        Grid options = new() { ColumnDefinitions = new ColumnDefinitions("100,100,100,*,90,190") };
        AddBound(options, 0, T("Start number"), _start);
        AddBound(options, 2, T("Subject prefix"), _prefix);
        AddBound(options, 4, T("Numbering"), _numbered);
        StackPanel flags = new() { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _cover, _signoff, _binary } };
        StackPanel detection = new() { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _renames, _copies, _rewrites } };
        header.Children.Add(ExportDialogControls.Expandable("FormatPatchOptions", T("Options"), new StackPanel { Spacing = 4, Children = { options, flags, detection } }));
        header.Children.Add(_selection);
        DockPanel.SetDock(header, Dock.Top);
        Root.Children.Add(header);
        StackPanel paging = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 4), Children = { _more } };
        DockPanel.SetDock(paging, Dock.Bottom);
        Root.Children.Add(paging);
        _revisions.Margin = new Thickness(10, 0, 10, 0);
        Root.Children.Add(_revisions);
        Track(_output, _selectedMode, _rangeMode, _from, _to, _start, _cover, _signoff, _renames, _copies, _rewrites, _binary, _prefix, _numbered, _revisions);
        _revisions.Rows.SelectionChanged += (_, _) => UpdateActions();
        _selectedMode.IsCheckedChanged += (_, _) => UpdateActions();
        _rangeMode.IsCheckedChanged += (_, _) => UpdateActions();
        _output.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty && Directory.Exists(_output.Text))
            {
                _preferences.Save(_preferences.Load() with { LastFormatPatchDirectory = _output.Text });
            }
        };
        UpdateActions();
    }

    public override Task InitializeAsync() => RunAsync("Load revisions", async token =>
    {
        if (_loaded.Count == 0) { await LoadMoreAsync(token); }
        GitStatus status = await Repository.GetStatusAsync(token);
        _branch.Text = T("Current branch:") + " " + status.Branch;
        string? selected = status.IsUnborn ? null : await Repository.ResolveRevisionAsync(_initialRevision, token);
        if (selected is not null && _loaded.All(commit => commit.Hash != selected))
        {
            _loaded.AddRange(await Repository.GetLogAsync(1, selected, token));
            _revisions.SetData(_loaded, await Repository.GetReferencesAsync(token), status.Head);
        }
        _revisions.Rows.SelectedItem = _revisions.Rows.ItemsSource?.Cast<GitCommit>().FirstOrDefault(commit => commit.Hash == selected);
        Status.Text = T("Select revisions to export");
    });

    private async Task LoadMoreAsync(CancellationToken token)
    {
        string[] selected = _revisions.Rows.SelectedItems?.Cast<GitCommit>().Select(commit => commit.Hash).ToArray() ?? [];
        IReadOnlyList<GitCommit> next = await Repository.GetLogAsync(new GitLogQuery(Count: 300, Skip: _readCount), token);
        _readCount += next.Count;
        _hasMore = next.Count == 300;
        _loaded.AddRange(next.Where(commit => _loaded.All(existing => existing.Hash != commit.Hash)));
        IReadOnlyList<GitCommit> ordered = _revisions.SetData(_loaded, await Repository.GetReferencesAsync(token), (await Repository.GetStatusAsync(token)).Head);
        _revisions.Rows.SelectedItems?.Clear();
        foreach (GitCommit commit in ordered.Where(commit => selected.Contains(commit.Hash, StringComparer.Ordinal))) { _revisions.Rows.SelectedItems?.Add(commit); }
        Status.Text = T("Select revisions to export");
    }

    public GitFormatPatchOptions ReadOptions()
    {
        string[]? selected = _rangeMode.IsChecked == true ? null : (_revisions.Rows.ItemsSource?.Cast<GitCommit>() ?? [])
            .Where(commit => _revisions.Rows.SelectedItems?.Contains(commit) == true).Reverse().Select(commit => commit.Hash).ToArray();
        return new(_output.Text ?? "", _rangeMode.IsChecked == true && !string.IsNullOrWhiteSpace(_from.Text) ? _from.Text : null,
            _rangeMode.IsChecked == true ? _to.Text ?? "HEAD" : "HEAD", selected, (int)(_start.Value ?? 1), _cover.IsChecked == true, _signoff.IsChecked == true,
            string.IsNullOrEmpty(_prefix.Text) ? null : _prefix.Text, _numbered.SelectedIndex switch { 1 => true, 2 => false, _ => null },
            _renames.IsChecked == true, _copies.IsChecked == true, _rewrites.IsChecked == true, _binary.IsChecked == true);
    }

    private async Task BrowseAsync()
    {
        string? directory = await ExportDialogControls.FolderAsync(this, T("Save patches to"));
        if (directory is not null) { _output.Text = directory; }
    }

    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(_output.Text)) { await BrowseAsync(); }
        if (string.IsNullOrWhiteSpace(_output.Text)) { return; }
        await RunAsync("Create patches", async token =>
        {
            LastResult = await Repository.FormatPatchAsync(ReadOptions(), token);
            _created.Text = string.Join('\n', LastResult.Files.Select(ExportDialogControls.DisplayPath));
            _result.IsVisible = true;
            _result.IsExpanded = true;
            Status.Text = T("Patch result") + ": " + LastResult.Files.Count;
        });
    }

    protected override void UpdateActions()
    {
        int count = _revisions.Rows.SelectedItems?.Count ?? 0;
        bool range = _rangeMode.IsChecked == true;
        _from.IsEnabled = _to.IsEnabled = !IsBusy && range;
        _revisions.IsEnabled = !IsBusy && !range;
        _more.IsEnabled = !IsBusy && !range && _hasMore;
        _create.IsEnabled = !IsBusy && (range || count > 0);
        _cover.IsEnabled = !IsBusy && (range || count <= 2);
        if (!range && count > 2) { _cover.IsChecked = false; }
        _selection.Text = range ? T("The lower revision is excluded; the upper revision is included.")
            : count == 2 ? T("Both selected revisions and the commits between them will be exported.")
            : count > 2 ? T("Only the selected commits will be exported.") : T("Select at least one revision");
    }

    private static void AddBound(Grid grid, int column, string label, Control input)
    {
        TextBlock caption = new() { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(caption, column);
        grid.Children.Add(caption);
        Grid.SetColumn(input, column + 1);
        input.Margin = new Thickness(0, 2, column == 0 ? 10 : 0, 2);
        grid.Children.Add(input);
    }
}

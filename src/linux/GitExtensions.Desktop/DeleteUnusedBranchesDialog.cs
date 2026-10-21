using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

/// <summary>The Windows checkbox preview and two remote decisions, over the existing portable plugin engine.</summary>
public sealed class DeleteUnusedBranchesDialog : RepositoryDialog
{
    private const string Columns = "50,180,175,91,*";
    private readonly DeleteUnusedBranchesPlugin _plugin = new();
    private readonly IReadOnlyDictionary<string, string?> _savedValues;
    private readonly Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task>? _applySettings;
    private readonly NumericUpDown _days = new() { Name = "olderThanDays", Minimum = 0, Maximum = 100000, Increment = 1, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _merged = new() { Name = "mergedIntoBranch" };
    private readonly CheckBox _remoteOnly = new() { Name = "IncludeRemoteBranches" };
    private readonly TextBox _remote = new() { Name = "_NO_TRANSLATE_Remote" };
    private readonly CheckBox _useRegex = new() { Name = "useRegexFilter" };
    private readonly TextBox _pattern = new() { Name = "regexFilter" };
    private readonly CheckBox _ignoreCase = new() { Name = "useRegexCaseInsensitive" };
    private readonly CheckBox _invert = new() { Name = "regexDoesNotMatch" };
    private readonly CheckBox _unmerged = new() { Name = "includeUnmergedBranches" };
    private readonly CheckBox _all = new() { Name = "checkBoxHeaderCell", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBox _branches = new() { Name = "BranchesGrid", SelectionMode = SelectionMode.Single };
    private readonly Button _search, _delete;
    private readonly TextBlock _instructions = new() { Name = "instructionLabel", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 3) };
    private readonly TextBox _results = new() { Name = "DeleteUnusedBranchesResults", IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap, MaxHeight = 72, IsVisible = false };
    private readonly List<Button> _sortHeaders = [];
    private List<BranchRow> _rows = [], _displayed = [];
    private CancellationTokenSource? _searchCancellation;
    private Task _work = Task.CompletedTask;
    private long _generation;
    private int _sortColumn = 1;
    private bool _descending, _updating;

    public bool SettingsRequested { get; private set; }
    public DeleteUnusedBranchesPreview? LastPreview { get; private set; }
    public DeleteUnusedBranchesDeletionResult? LastDeletion { get; private set; }
    public IReadOnlyList<DeleteUnusedBranchCandidate> VisibleBranches => _displayed.Select(row => row.Branch).ToArray();
    public IReadOnlyList<string> SelectedBranches => _rows.Where(row => row.Checked).Select(row => row.Branch.Reference.Name).ToArray();
    internal Task Completion => _work;
    public IReadOnlyDictionary<string, string?> CurrentValues => new Dictionary<string, string?>(_savedValues)
    {
        ["operation"] = "preview", ["mergedInto"] = _merged.Text, ["olderThanDays"] = (_days.Value ?? 30).ToString(CultureInfo.InvariantCulture),
        ["remoteBranches"] = (_remoteOnly.IsChecked == true).ToString(), ["remote"] = _remote.Text,
        ["includeUnmerged"] = (_unmerged.IsChecked == true).ToString(), ["useRegex"] = (_useRegex.IsChecked == true).ToString(),
        ["pattern"] = _pattern.Text, ["ignoreCase"] = (_ignoreCase.IsChecked == true).ToString(), ["invertPattern"] = (_invert.IsChecked == true).ToString(),
        ["branches"] = ""
    };

    public DeleteUnusedBranchesDialog(GitRepository repository, IReadOnlyDictionary<string, string?>? savedValues = null,
        Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task>? applySettings = null, Action? requestSettings = null)
        : base(repository, "Delete obsolete branches")
    {
        _applySettings = applySettings;
        _savedValues = savedValues is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(savedValues);
        Name = "DeleteUnusedBranchesForm"; Title = Caption("$this", "Delete obsolete branches");
        Width = 760; Height = 500; MinWidth = 600; MinHeight = 400; CanMinimize = false; ShowInTaskbar = false;
        ClassicChoiceAppearance.Apply(this, "delete-unused-branches"); Resources["CheckBoxMinHeight"] = 22d;
        _all.MinHeight = 22; _all.Padding = new Thickness(0);
        using (Stream icon = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/git-extensions-logo.ico"))) { Icon = new WindowIcon(icon); }
        ConstrainStatusHeight("DeleteUnusedBranchesStatus", 45);
        ScrollViewer.SetVerticalScrollBarVisibility(_results, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_results, ScrollBarVisibility.Auto);
        _days.Value = int.TryParse(savedValues?.GetValueOrDefault("olderThanDays"), NumberStyles.None, CultureInfo.InvariantCulture, out int days)
            && days <= 100000 ? days : 30;
        _merged.Text = savedValues?.GetValueOrDefault("mergedInto") ?? "HEAD";
        _remoteOnly.IsChecked = Bool("remoteBranches"); _remote.Text = savedValues?.GetValueOrDefault("remote") ?? "origin";
        _pattern.Text = savedValues?.GetValueOrDefault("pattern") ?? "/(feature|develop)/";
        // Preserve the former generic plugin's saved nonempty filter; an untouched fresh form uses Windows' unchecked default.
        _useRegex.IsChecked = savedValues?.ContainsKey("useRegex") == true ? Bool("useRegex") : !string.IsNullOrEmpty(savedValues?.GetValueOrDefault("pattern"));
        _ignoreCase.IsChecked = Bool("ignoreCase"); _invert.IsChecked = Bool("invertPattern"); _unmerged.IsChecked = Bool("includeUnmerged");
        _remoteOnly.Content = Caption("IncludeRemoteBranches", "Delete remote branches from");
        _useRegex.Content = Caption("useRegexFilter", "Use regex to filter branches");
        _ignoreCase.Content = Caption("useRegexCaseInsensitive", "Case insensitive"); _invert.Content = Caption("regexDoesNotMatch", "Does not match");
        _unmerged.Content = Caption("includeUnmergedBranches", "Include unmerged branches");
        _search = new() { Name = "RefreshBtn", Content = Caption("RefreshBtn", "Search branches"), MinWidth = 100, Height = 25,
            HorizontalAlignment = HorizontalAlignment.Left };
        _search.Click += (_, _) =>
        {
            if (_searchCancellation is not null) { _searchCancellation.Cancel(); return; }
            if (!IsBusy) { _work = SearchAsync(saveSettings: true); }
        };
        Grid options = new() { Name = "tableLayoutPanel3", ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"), Margin = new Thickness(6, 4) };
        AddOption(Label("label1", "Delete branches older than x days"), 0, 0); AddOption(_days, 0, 1);
        AddOption(Label("label2", "Delete branches fully merged into branch"), 1, 0); AddOption(_merged, 1, 1);
        AddOption(_remoteOnly, 2, 0); AddOption(_remote, 2, 1); AddOption(_useRegex, 3, 0); AddOption(_pattern, 3, 1);
        AddOption(_ignoreCase, 4, 1); AddOption(_invert, 5, 1); AddOption(_unmerged, 6, 0); AddOption(_search, 7, 1);
        StackPanel top = new() { Children = { options, _instructions } }; DockPanel.SetDock(top, Dock.Top); Root.Children.Add(top);

        _delete = Action("Delete", Caption("Delete", "Delete"), DeleteAsync); _delete.MinWidth = 75; _delete.Height = 25;
        Button close = new() { Name = "Cancel", Content = Caption("Cancel", "Close"), MinWidth = 75, Height = 25 }; close.Click += (_, _) => Close();
        // The host supplies the real page route and awaits this modal's drain before opening it.
        Button settings = new() { Name = "buttonSettings", Content = Caption("buttonSettings", "Settings"), MinWidth = 75, Height = 25, IsVisible = requestSettings is not null };
        settings.Click += (_, _) => { if (!IsBusy) { SettingsRequested = true; Close(); requestSettings?.Invoke(); } }; Track(settings);
        Grid buttons = new() { Name = "tableLayoutPanel2", ColumnDefinitions = new("Auto,*,Auto,Auto"), Margin = new Thickness(6) };
        buttons.Children.Add(settings); Grid.SetColumn(_delete, 2); buttons.Children.Add(_delete);
        close.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(close, 3); buttons.Children.Add(close);
        StackPanel bottom = new() { Children = { _results, buttons } }; DockPanel.SetDock(bottom, Dock.Bottom); Root.Children.Add(bottom);

        Grid headers = new() { Name = "DeleteUnusedBranchesHeaders", ColumnDefinitions = new(Columns), Height = 25 };
        ToolTip.SetTip(_all, T("Select all")); headers.Children.Add(_all);
        string[] names = ["", "nameDataGridViewTextBoxColumn", "dateDataGridViewTextBoxColumn", "Author", "Message"];
        string[] labels = ["", "Name", "Last activity", "Last author", "Last message"];
        for (int index = 1; index < labels.Length; index++)
        {
            int column = index;
            if (column == 4)
            {
                TextBlock heading = new() { Name = names[index], Text = Header(names[index], labels[index]), Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(heading, column); headers.Children.Add(heading); continue;
            }
            Button headingButton = new() { Name = names[index], Content = Header(names[index], labels[index]), Height = 25, MinWidth = 0,
                Padding = new Thickness(4, 0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            headingButton.Click += (_, _) =>
            {
                if (IsBusy) { return; }
                _descending = _sortColumn == column && !_descending; _sortColumn = column; PresentRows();
            };
            Grid.SetColumn(headingButton, column); headers.Children.Add(headingButton); _sortHeaders.Add(headingButton); Track(headingButton);
        }
        _branches.ItemTemplate = new FuncDataTemplate<BranchRow>((row, _) => CreateRow(row));
        _branches.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(HeightProperty, 22d), new Setter(MinHeightProperty, 22d), new Setter(PaddingProperty, new Thickness(0)),
            new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        Grid table = new() { Name = "DeleteUnusedBranchesTable", MinWidth = 700, RowDefinitions = new("Auto,*") };
        table.Children.Add(ClassicChrome.Border(headers, new Thickness(0, 0, 0, 1))); Grid.SetRow(_branches, 1); table.Children.Add(_branches);
        Root.Children.Add(ClassicChrome.Border(new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, new Thickness(1)));
        Track(_days, _merged, _remoteOnly, _remote, _useRegex, _pattern, _ignoreCase, _invert, _unmerged, _branches, _all);
        _all.IsCheckedChanged += (_, _) =>
        {
            if (_updating || IsBusy) { return; }
            foreach (BranchRow row in _rows) { row.Checked = _all.IsChecked == true; }
            PresentRows();
        };
        // TextChanged is posted after rendering, including constructor assignments.
        // Observe the actual property synchronously so an old notification cannot erase a newer preview.
        foreach (TextBox editor in new[] { _merged, _remote, _pattern })
            editor.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { CriteriaChanged(); } };
        _days.ValueChanged += (_, _) => CriteriaChanged();
        foreach (CheckBox choice in new[] { _remoteOnly, _useRegex, _ignoreCase, _invert }) choice.IsCheckedChanged += (_, _) => CriteriaChanged();
        _unmerged.IsCheckedChanged += (_, _) =>
        {
            CriteriaChanged();
            if (_unmerged.IsChecked == true)
            {
                Task pending = _work;
                long generation = _generation;
                _work = WarnUnmergedAsync(pending, generation);
            }
        };
        CriteriaChanged();

        bool Bool(string key) => bool.TryParse(savedValues?.GetValueOrDefault(key), out bool value) && value;
        void AddOption(Control control, int row, int column)
        { control.Margin = new Thickness(0, 1, 4, 1); control.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(control, row); Grid.SetColumn(control, column); options.Children.Add(control); }
    }

    public override Task InitializeAsync()
    {
        if (IsBusy) { return _work; }
        return _work = SearchAsync(saveSettings: false);
    }
    private string Caption(string item, string source) => Translations.Translate("DeleteUnusedBranchesForm", item, "Text", source);
    private string Header(string item, string source) => Translations.Translate("DeleteUnusedBranchesForm", item, "HeaderText", source);
    private TextBlock Label(string name, string text) => new() { Name = name, Text = Caption(name, text), TextWrapping = TextWrapping.Wrap };

    private void CriteriaChanged()
    {
        _generation++; _searchCancellation?.Cancel(); LastPreview = null; _rows.Clear(); _displayed.Clear(); _branches.ItemsSource = null;
        _instructions.Text = string.Format(CultureInfo.CurrentCulture, Caption("_chooseBranchesToDelete", "Choose branches to delete. Only branches that are fully merged in '{0}' will be deleted."), _merged.Text);
        Status.Text = string.Format(CultureInfo.CurrentCulture, Caption("_pressToSearch", "Press '{0}' to search for branches to delete."), Caption("_searchBranches", "Search branches"));
        UpdateSelectionStatus(updateStatus: false); UpdateActions();
    }
    private Task SearchAsync(bool saveSettings)
    {
        if (IsBusy) { return Task.CompletedTask; }
        IReadOnlyDictionary<string, string?> values = CurrentValues;
        long generation = ++_generation;
        return RunAsync("Loading", async token =>
        {
            using CancellationTokenSource search = CancellationTokenSource.CreateLinkedTokenSource(token);
            _searchCancellation = search; _search.Content = Caption("_cancel", "Cancel"); UpdateActions();
            Status.Text = Caption("_loading", "Loading...");
            try
            {
                DeleteUnusedBranchesPreview preview = await _plugin.PreviewAsync(new(Repository), values, search.Token);
                search.Token.ThrowIfCancellationRequested();
                if (generation != _generation) { return; }
                ApplyPreview(preview);
                if (saveSettings && _applySettings is not null) { await _applySettings(values, search.Token); }
            }
            finally { _searchCancellation = null; _search.Content = Caption("_searchBranches", "Search branches"); }
        }, disableInputs: false);
    }
    private async Task WarnUnmergedAsync(Task pending, long generation)
    {
        // An editable search can be cancelled by this checkbox. Wait for that read before owning the notice.
        await pending;
        if (!IsVisible || generation != _generation || _unmerged.IsChecked != true || IsBusy) { return; }
        await RunAsync("Delete", token =>
        {
            UpdateActions();
            return Dialogs.ShowMessageAsync(this, Caption("_deleteCaption", "Delete"),
                Caption("_deletingUnmergedBranches", "Deleting unmerged branches will result in dangling commits. Use with caution!"), true, token);
        });
    }
    private void ApplyPreview(DeleteUnusedBranchesPreview preview)
    {
        LastPreview = preview;
        _rows = preview.Candidates.Where(item => item.Eligible).Select(item => new BranchRow(item)).ToList();
        PresentRows();
    }
    private Control CreateRow(BranchRow? row)
    {
        Grid line = new() { Name = "DeleteUnusedBranchRow", Tag = row?.Branch.Reference.Name, ColumnDefinitions = new(Columns), Height = 22 };
        if (row is null) { return line; }
        CheckBox check = new() { Name = "DeleteUnusedBranchChecked", Tag = row.Branch.Reference.Name, IsChecked = row.Checked,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MinHeight = 22, Padding = new Thickness(0) };
        check.IsCheckedChanged += (_, _) => { row.Checked = check.IsChecked == true; UpdateSelectionStatus(); };
        line.Children.Add(check);
        string[] values = [row.Branch.Reference.ShortName, row.Branch.Date.LocalDateTime.ToString(CultureInfo.CurrentCulture), row.Branch.Author, row.Branch.Subject];
        for (int index = 0; index < values.Length; index++)
        {
            TextBlock text = new() { Name = "DeleteUnusedBranchCell" + (index + 1), Text = values[index], Margin = new Thickness(4, 0),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(text, values[index]); Grid.SetColumn(text, index + 1); line.Children.Add(text);
        }
        return line;
    }
    private void PresentRows()
    {
        string? current = (_branches.SelectedItem as BranchRow)?.Branch.Reference.Name;
        IEnumerable<BranchRow> ordered = _sortColumn switch
        {
            2 => _descending ? _rows.OrderByDescending(row => row.Branch.Date) : _rows.OrderBy(row => row.Branch.Date),
            3 => _descending ? _rows.OrderByDescending(row => row.Branch.Author, StringComparer.CurrentCulture) : _rows.OrderBy(row => row.Branch.Author, StringComparer.CurrentCulture),
            _ => _descending ? _rows.OrderByDescending(row => row.Branch.Reference.ShortName, StringComparer.CurrentCulture) : _rows.OrderBy(row => row.Branch.Reference.ShortName, StringComparer.CurrentCulture)
        };
        _displayed = ordered.ToList(); _branches.ItemsSource = _displayed;
        if (current is not null) { _branches.SelectedItem = _displayed.FirstOrDefault(row => row.Branch.Reference.Name == current); }
        UpdateSelectionStatus();
    }
    private void UpdateSelectionStatus(bool updateStatus = true)
    {
        _updating = true;
        try { _all.IsChecked = _rows.All(row => row.Checked); }
        finally { _updating = false; }
        if (updateStatus) Status.Text = string.Format(CultureInfo.CurrentCulture, Caption("_branchesSelected", "{0}/{1} branches selected."), _rows.Count(row => row.Checked), _rows.Count);
    }
    private Task DeleteAsync()
    {
        if (IsBusy) { return Task.CompletedTask; }
        DeleteUnusedBranchesPreview? reviewed = LastPreview;
        string[] selected = SelectedBranches.ToArray();
        Dictionary<string, string?> values = new(CurrentValues); values["branches"] = string.Join('\n', selected);
        return _work = RunAsync("Delete", async token =>
        {
            UpdateActions();
            if (reviewed is null || selected.Length == 0)
            {
                await Dialogs.ShowMessageAsync(this, Caption("_deleteCaption", "Delete"),
                    string.Format(CultureInfo.CurrentCulture, Caption("_selectBranchesToDelete", "Select branches to delete using checkboxes in '{0}' column."), ""), false, token);
                return;
            }
            if (await Dialogs.ConfirmChoiceAsync(this, Caption("_deleteCaption", "Delete"),
                string.Format(CultureInfo.CurrentCulture, Caption("_areYouSureToDelete", "Are you sure to delete {0} selected branches?"), selected.Length),
                "Yes", "No", acceptIsDefault: true, cancellationToken: token, translations: Translations, requestedThemeVariant: ActualThemeVariant, warning: true) != true) { UpdateSelectionStatus(); return; }
            if (reviewed.Candidates.Any(item => selected.Contains(item.Reference.Name, StringComparer.Ordinal) && item.Reference.IsRemote)
                && await Dialogs.ConfirmChoiceAsync(this, Caption("_deleteCaption", "Delete"),
                    string.Format(CultureInfo.CurrentCulture, Caption("_dangerousAction", "DANGEROUS ACTION!\nBranches will be deleted on the remote '{0}'. This can not be undone.\nAre you sure you want to continue?"), reviewed.Remote),
                    "Yes", "No", acceptIsDefault: true, cancellationToken: token, translations: Translations, requestedThemeVariant: ActualThemeVariant, warning: true) != true) { UpdateSelectionStatus(); return; }
            token.ThrowIfCancellationRequested(); Status.Text = Caption("_deletingBranches", "Deleting branches...");
            LastDeletion = await _plugin.DeleteAsync(new(Repository, Confirmed: true), values, reviewed, token);
            if (LastDeletion.MayHaveRepositoryChanges) { MarkRepositoryMutationAttempted(); }
            if (LastDeletion.RepositoryChanged) { MarkRepositoryChanged(); }
            _results.Text = ResultText(LastDeletion); _results.IsVisible = true;
            Status.Text = T(LastDeletion.Success ? "Completed" : LastDeletion.Cancelled ? "Operation cancelled" : "Some operations failed. See results.");
            if (LastDeletion.Success)
            {
                string summary = Status.Text ?? "";
                try
                {
                    DeleteUnusedBranchesPreview refreshed = await _plugin.PreviewAsync(new(Repository), values, token);
                    token.ThrowIfCancellationRequested(); ApplyPreview(refreshed); Status.Text = summary;
                    if (_applySettings is not null) { await _applySettings(CurrentValues, token); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { Status.Text = summary; }
                catch (Exception error) { ReportError(error); }
            }
        });
    }
    private string ResultText(DeleteUnusedBranchesDeletionResult result)
    {
        StringBuilder text = new();
        foreach (DeleteUnusedBranchOutcome outcome in result.Outcomes)
        {
            text.Append(outcome.Branch.Reference.ShortName).Append(": ").Append(T(outcome.Status.ToString())).AppendLine();
            text.AppendLine(outcome.Message);
        }
        return text.ToString().TrimEnd();
    }
    protected override void UpdateActions()
    {
        _search.IsEnabled = !IsBusy || _searchCancellation is not null;
        _delete.IsEnabled = !IsBusy;
        _all.IsEnabled = !IsBusy && _rows.Count > 0;
        foreach (Button header in _sortHeaders) { header.IsEnabled = !IsBusy; }
    }
    private sealed class BranchRow(DeleteUnusedBranchCandidate branch)
    {
        internal DeleteUnusedBranchCandidate Branch { get; } = branch;
        internal bool Checked { get; set; } = branch.Suggested;
    }
}

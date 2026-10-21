using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original blob grid and path removal workflow, with native results retained after failure or interruption.</summary>
public sealed class FindLargeFilesDialog : RepositoryDialog
{
    private const string Columns = "60,*,75,112,94,126,60";
    private readonly FindLargeFilesPlugin _plugin = new();
    private readonly NumericUpDown _minimum = new() { Name = "FindLargeFilesMinimum", Minimum = 0, Maximum = long.MaxValue / 1048576m,
        Increment = 0.1m, FormatString = "G29", ParsingNumberStyle = NumberStyles.Float | NumberStyles.AllowThousands, Width = 100 };
    private readonly TextBox _revision = new() { Name = "FindLargeFilesRevision", Width = 150 };
    private readonly ListBox _objects = new() { Name = "FindLargeFilesObjects", SelectionMode = SelectionMode.Single };
    private readonly Grid _headers = new() { Name = "FindLargeFilesHeaders", ColumnDefinitions = new(Columns), Height = 25 };
    private readonly ProgressBar _progress = new() { Name = "FindLargeFilesProgress", Height = 18, Minimum = 0, Maximum = 100, IsVisible = false };
    private readonly Button _delete;
    private readonly List<Button> _sortHeaders = [];
    private List<BlobRow> _rows = [], _displayed = [];
    private int _sortColumn = 2;
    private bool _descending = true, _initialized;
    public PluginResult? LastAnalysis { get; private set; }
    public GitHistoryRemovalResult? LastRemoval { get; private set; }
    public GitProcessDialogResult? LastProcess { get; private set; }
    public IReadOnlyList<PluginRow> VisibleObjects => _displayed.Select(row => row.Object).ToArray();
    public IReadOnlyList<string> SelectedPaths => _rows.Where(row => row.Checked).Select(row => row.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public IReadOnlyDictionary<string, string?> CurrentValues => new Dictionary<string, string?>
    {
        ["minimumMegabytes"] = _minimum.Value?.ToString(CultureInfo.InvariantCulture), ["revision"] = _revision.Text
    };

    public FindLargeFilesDialog(GitRepository repository, IReadOnlyDictionary<string, string?>? savedValues = null) : base(repository, "Find large files")
    {
        Name = "FindLargeFilesDialog"; Width = 820; Height = 460; MinWidth = 760; MinHeight = 350;
        ConstrainStatusHeight("FindLargeFilesStatus", 72);
        _minimum.Value = decimal.TryParse(savedValues?.GetValueOrDefault("minimumMegabytes"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out decimal minimum) && minimum >= 0 && minimum <= _minimum.Maximum ? minimum : 1;
        _revision.Text = savedValues?.GetValueOrDefault("revision") ?? "HEAD";
        Button analyze = Action("AnalyzeLargeFiles", "Find large files", InitializeAsync); analyze.Height = 25;
        StackPanel settings = new() { Name = "FindLargeFilesSettings", Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 4), Children =
        {
            new TextBlock { Text = T("Minimum size (MiB)"), VerticalAlignment = VerticalAlignment.Center }, _minimum,
            new TextBlock { Text = T("Revision"), VerticalAlignment = VerticalAlignment.Center }, _revision, analyze
        } };
        TextBlock instructions = new() { Name = "FindLargeFilesInstructions", Margin = new Thickness(8, 4), TextWrapping = TextWrapping.Wrap,
            Text = T("Reset local changes before deleting files. Choose files to delete. Force push for replacing data on remote repository.") };
        StackPanel top = new() { Children = { settings, instructions } }; DockPanel.SetDock(top, Dock.Top); Root.Children.Add(top);

        _delete = Action("DeleteLargeFiles", "Delete", DeleteAsync); _delete.MinWidth = 75; _delete.Height = 25;
        // Keep Close available during work; RepositoryDialog cancels and waits for native completion before closing.
        Button close = new() { Name = "CloseLargeFiles", Content = T("Close"), MinWidth = 75, Height = 25 }; close.Click += (_, _) => Close();
        Grid footer = new() { ColumnDefinitions = new("*,Auto,Auto"), Margin = new Thickness(8, 6) };
        _progress.Margin = new Thickness(0, 0, 8, 0); _progress.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(_progress);
        Grid.SetColumn(_delete, 1); footer.Children.Add(_delete); close.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(close, 2); footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); Root.Children.Add(footer);

        string[] labels = ["SHA", "Path", "Size", "Compressed size", "Commit count", "Last commit date", "Delete"];
        for (int i = 0; i < labels.Length; i++)
        {
            int column = i;
            Button header = new() { Name = "FindLargeFilesSort" + i, Content = T(labels[i]), Padding = new Thickness(4, 0), HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0, Height = 25 };
            header.Click += (_, _) => { if (IsBusy) { return; } _descending = _sortColumn == column && !_descending; _sortColumn = column; PresentRows(); };
            Grid.SetColumn(header, i); _headers.Children.Add(header); _sortHeaders.Add(header); Track(header);
        }
        Grid table = new() { Name = "FindLargeFilesTable", RowDefinitions = new("Auto,*"), MinWidth = 735 };
        table.Children.Add(ClassicChrome.Border(_headers, new Thickness(0, 0, 0, 1)));
        _objects.ItemTemplate = new FuncDataTemplate<BlobRow>((row, _) => CreateRow(row));
        _objects.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(HeightProperty, 24d), new Setter(MinHeightProperty, 24d), new Setter(PaddingProperty, new Thickness(0)) } });
        Grid.SetRow(_objects, 1); table.Children.Add(_objects);
        ScrollViewer viewport = new() { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Root.Children.Add(ClassicChrome.Border(viewport, new Thickness(1)));
        Track(_minimum, _revision, _objects); UpdateActions();
    }

    public override Task InitializeAsync() => RunAsync("Find large files", async token =>
    {
        _progress.IsVisible = true; _progress.IsIndeterminate = true;
        try { await AnalyzeAsync(token); }
        finally { _progress.IsIndeterminate = false; _progress.IsVisible = false; }
    });

    private async Task AnalyzeAsync(CancellationToken token)
    {
        PluginResult analysis = await _plugin.ExecuteAsync(new(Repository), CurrentValues, token);
        token.ThrowIfCancellationRequested();
        HashSet<string> checkedObjects = _rows.Where(row => row.Checked).Select(row => row.Object.Key).ToHashSet(StringComparer.Ordinal);
        _rows = analysis.Table?.Rows.Select(row => new BlobRow(row) { Checked = checkedObjects.Contains(row.Key) }).ToList() ?? [];
        LastAnalysis = analysis; _initialized = true; _progress.Value = 100; PresentRows(); Status.Text = analysis.Text;
    }

    private Grid CreateRow(BlobRow? row)
    {
        Grid grid = new() { Name = "FindLargeFilesRow", ColumnDefinitions = new(Columns), Height = 24, Tag = row?.Object.Key };
        if (row is null) { return grid; }
        string[] cells = [row.Hash[..Math.Min(7, row.Hash.Length)], row.Path, Size(row.Size), row.CompressedSize is long compressed ? Size(compressed) : "<" + T("Unknown") + ">",
            row.CommitCount.ToString(CultureInfo.CurrentCulture), row.LastDate.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)];
        for (int i = 0; i < cells.Length; i++)
        {
            TextBlock text = new() { Name = "FindLargeFilesCell" + i, Text = cells[i], Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            if (i is 2 or 3 or 4) { text.TextAlignment = TextAlignment.Right; }
            ToolTip.SetTip(text, i == 0 ? row.Hash : row.Object.Cells[i]); Grid.SetColumn(text, i); grid.Children.Add(text);
        }
        CheckBox check = new() { Name = "FindLargeFilesDeleteChecked", IsChecked = row.Checked, Padding = new Thickness(0), MinHeight = 24,
            HorizontalAlignment = HorizontalAlignment.Center, Tag = row.Object.Key };
        ToolTip.SetTip(check, T("Delete") + ": " + row.Path);
        check.IsCheckedChanged += (_, _) => { row.Checked = check.IsChecked == true; UpdateActions(); };
        Grid.SetColumn(check, 6); grid.Children.Add(check); return grid;
    }
    private static string Size(long bytes) => (bytes / 1048576m).ToString("F2", CultureInfo.CurrentCulture) + " Mb";

    private void PresentRows()
    {
        string? selected = (_objects.SelectedItem as BlobRow)?.Object.Key;
        Comparison<BlobRow> compare = (left, right) => _sortColumn switch
        {
            0 => StringComparer.Ordinal.Compare(left.Hash, right.Hash), 1 => StringComparer.Ordinal.Compare(left.Path, right.Path),
            2 => left.Size.CompareTo(right.Size), 3 => Nullable.Compare(left.CompressedSize, right.CompressedSize),
            4 => left.CommitCount.CompareTo(right.CommitCount), 5 => left.LastDate.CompareTo(right.LastDate), 6 => left.Checked.CompareTo(right.Checked), _ => 0
        };
        _displayed = _rows.Order(Comparer<BlobRow>.Create((left, right) =>
        {
            int result = compare(left, right); if (_descending) { result = -result; }
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.Object.Key, right.Object.Key);
        })).ToList();
        _objects.ItemsSource = _displayed; _objects.SelectedItem = _displayed.FirstOrDefault(row => row.Object.Key == selected);
        for (int i = 0; i < _sortHeaders.Count; i++)
        {
            string label = T(new[] { "SHA", "Path", "Size", "Compressed size", "Commit count", "Last commit date", "Delete" }[i]);
            _sortHeaders[i].Content = label + (i == _sortColumn ? _descending ? " ▼" : " ▲" : "");
        }
        UpdateActions();
    }

    private Task DeleteAsync()
    {
        string[] paths = SelectedPaths.ToArray();
        if (IsBusy || !_initialized || paths.Length == 0 || !IsVisible) { return Task.CompletedTask; }
        return RunAsync("Delete", async token =>
        {
            if (!await ConfirmDeleteAsync(paths, token)) { Status.Text = LastAnalysis?.Text; return; }
            token.ThrowIfCancellationRequested();
            LastRemoval = null; LastProcess = null; _progress.IsVisible = true; _progress.IsIndeterminate = true;
            try
            {
                LastProcess = await GitProcessDialog.ShowAsync(this, Repository.Path, "git filter-branch — " + T("Delete selected paths from branch history"), true,
                    async (ct, output) =>
                    {
                        GitHistoryRemovalResult removal = await Repository.RemovePathsFromHistoryAsync(paths, ct, output);
                        LastRemoval = removal; if (removal.MutationStarted) { MarkRepositoryChanged(); }
                        if (removal.Cancelled) { throw new OperationCanceledException(ct); }
                        if (!removal.Success)
                        {
                            GitHistoryRemovalStepResult? failed = removal.Steps.LastOrDefault(step => !step.Success);
                            if (failed?.Result is GitResult native && !native.Success) { throw new GitCommandException(failed.Arguments, native); }
                            throw new InvalidOperationException(removal.Error ?? string.Join(Environment.NewLine, removal.Errors));
                        }
                        return removal.Steps.LastOrDefault(step => step.Result is not null)?.Result
                            ?? throw new InvalidOperationException(T("No native Git result was returned."));
                    }, token, preserveStreamedOutput: true);
                // Completed commands can have changed history even if the remaining sequence was interrupted.
                if (LastRemoval is not null) { Status.Text = RemovalText(LastRemoval); }
                else { Status.Text = LastProcess.Error?.Message ?? T("Operation cancelled"); }
                if (LastProcess.Success && LastRemoval?.Success == true)
                {
                    // Reanalyse only a fully completed removal. On failure keep the selected rows and diagnostics for review.
                    string summary = RemovalText(LastRemoval);
                    try { await AnalyzeAsync(token); Status.Text = summary + Environment.NewLine + LastAnalysis?.Text; }
                    catch (Exception error) { Status.Text = summary + Environment.NewLine + T("Reload failed") + ": " + error.Message; }
                }
            }
            finally { _progress.IsIndeterminate = false; _progress.IsVisible = false; }
        });
    }

    private async Task<bool> ConfirmDeleteAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        Window confirmation = new() { Name = "FindLargeFilesDeleteConfirmation", Title = T("Delete"), Width = 550, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ActualThemeVariant };
        Button no = new() { Name = "CancelLargeFilesDelete", Content = T("No"), IsCancel = true, IsDefault = true, MinWidth = 75, Height = 25 };
        Button yes = new() { Name = "ConfirmLargeFilesDelete", Content = T("Yes"), MinWidth = 75, Height = 25 };
        no.Click += (_, _) => confirmation.Close(false); yes.Click += (_, _) => confirmation.Close(true);
        confirmation.Content = new StackPanel { Margin = new Thickness(12), Spacing = 10, Children =
        {
            new TextBlock { Text = T("Are you sure to delete the selected files?") + Environment.NewLine
                + T("Selected paths will be removed from branch history. Annotated tags keep their original commits."), TextWrapping = TextWrapping.Wrap },
            new ScrollViewer { MaxHeight = 160, Content = new TextBlock { Text = string.Join(Environment.NewLine, paths), TextWrapping = TextWrapping.Wrap } },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { no, yes } }
        } };
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => confirmation.Close(false)));
        token.ThrowIfCancellationRequested(); return await confirmation.ShowDialog<bool>(this);
    }

    private string RemovalText(GitHistoryRemovalResult removal)
    {
        StringBuilder text = new();
        text.AppendLine(T(removal.Success ? "Completed" : removal.Cancelled ? "Operation cancelled" : "Some operations failed. See results."));
        if (!removal.Success && removal.MutationStarted) { text.AppendLine(T("History may be partially changed. Review the completed steps before continuing.")); }
        foreach (GitHistoryRemovalStepResult step in removal.Steps)
        {
            text.Append(StepName(step.Step)).Append(": ").AppendLine(step.ExitCode is int exit ? string.Format(CultureInfo.CurrentCulture, T("Exit code: {0}"), exit)
                : T(step.Cancelled ? "Operation cancelled" : "No native Git exit result"));
            if (step.StandardOutput.Length > 0) { text.AppendLine(step.StandardOutput); }
            if (step.StandardError.Length > 0) { text.AppendLine(step.StandardError); }
            if (!string.IsNullOrEmpty(step.Error)) { text.AppendLine(step.Error); }
        }
        if (!string.IsNullOrEmpty(removal.Error)) { text.AppendLine(removal.Error); }
        return text.ToString().TrimEnd();
    }
    private string StepName(GitHistoryRemovalStep step) => T(step switch
    {
        GitHistoryRemovalStep.RewriteHistory => "Rewrite history", GitHistoryRemovalStep.EnumerateBackupReferences => "Find backup references",
        GitHistoryRemovalStep.DeleteBackupReference => "Remove backup reference", GitHistoryRemovalStep.ExpireReflogs => "Expire reflogs",
        GitHistoryRemovalStep.GarbageCollect => "Clean up repository objects", _ => "Git operation"
    });
    protected override void UpdateActions() => _delete.IsEnabled = !IsBusy && _initialized && _rows.Any(row => row.Checked);

    private sealed class BlobRow
    {
        internal BlobRow(PluginRow row)
        {
            Object = row; Hash = row.Cells[0]; Path = row.Cells[1]; Size = long.Parse(row.Cells[2], CultureInfo.InvariantCulture);
            CompressedSize = long.TryParse(row.Cells[3], NumberStyles.None, CultureInfo.InvariantCulture, out long compressed) ? compressed : null;
            CommitCount = int.Parse(row.Cells[4], CultureInfo.InvariantCulture); LastDate = DateTimeOffset.Parse(row.Cells[5], CultureInfo.InvariantCulture);
        }
        internal PluginRow Object { get; }
        internal string Hash { get; }
        internal string Path { get; }
        internal long Size { get; }
        internal long? CompressedSize { get; }
        internal int CommitCount { get; }
        internal DateTimeOffset LastDate { get; }
        internal bool Checked { get; set; }
    }
}

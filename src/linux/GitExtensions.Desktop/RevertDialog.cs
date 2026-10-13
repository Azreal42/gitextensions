using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitExtensions.Linux.Desktop;

/// <summary>FormRevertCommit: independent automatic-commit choice, and one-based merge parent choice.</summary>
public sealed class RevertDialog : RepositoryDialog
{
    private readonly string _initialRevision;
    private readonly CancellationTokenSource _lifetime;
    private readonly TextBlock _summary = ResetDialogControls.Summary("RevertRevisionSummary", 160);
    private readonly ListBox _parents = new() { Name = "RevertParents", Focusable = true, MaxHeight = 180 };
    private readonly StackPanel _parentPanel = new() { Name = "RevertParentPanel", Spacing = 3, IsVisible = false };
    private readonly CheckBox _automatic = new() { Name = "RevertAutoCommit" };
    private readonly Button _revert, _choose;
    private GitRevertRevision? _revision;
    private bool _choosing;
    public string? Revision => _revision?.Commit.Hash;
    public bool AutoCommit => _automatic.IsChecked == true;
    public bool Applied { get; private set; }
    public bool Attempted { get; private set; }
    public IReadOnlyList<string> Conflicts { get; private set; } = [];

    public RevertDialog(GitRepository repository, string revision, CancellationToken cancellationToken = default) : base(repository, "Revert commit")
    {
        _initialRevision = revision; _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Name = "RevertDialog"; Title = Text("$this", "Revert commit"); Width = 630; MinWidth = 614; CanResize = false; SizeToContent = SizeToContent.Height;
        _automatic.Content = Caption("AutoCommit", "&Automatically create a commit"); ReferenceDialogControls.Compact(_automatic);
        _revert = Action("ExecuteRevert", "Revert this commit", ExecuteAsync); _revert.Content = Caption("Revert", "&Revert this commit"); _revert.IsDefault = true; _revert.MinWidth = 117; _revert.Height = 25;
        Button cancel = new() { Name = "CancelRevert", Content = Caption("btnAbort", "A&bort"), IsCancel = true, MinWidth = 75, Height = 25 };
        cancel.Click += (_, _) => Close(false);
        StackPanel buttons = ExportDialogControls.Buttons(_revert, cancel); DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons);
        _choose = Action("ChooseRevertRevision", "Choose…", ChooseAsync); _choose.Content = ResetDialogControls.Icon("SelectRevision"); _choose.Width = 26; _choose.Height = 24;
        ToolTip.SetTip(_choose, T("Choose another revision:"));
        _parents.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters =
            { new Setter(Layoutable.HeightProperty, 18d), new Setter(Layoutable.MinHeightProperty, 18d), new Setter(TemplatedControl.PaddingProperty, new Thickness(2, 0)) } });
        _parents.ItemTemplate = new FuncDataTemplate<RevertParent>((row, _) =>
        {
            Grid line = ParentColumns(); line.MinHeight = 18;
            string[] cells = row is null ? ["", "", "", ""] : [row.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Commit.Subject, row.Commit.AuthorName, row.Commit.AuthoredAt.LocalDateTime.ToShortDateString()];
            for (int column = 0; column < cells.Length; column++)
            { TextBlock text = new() { Text = cells[column], TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(text, column); line.Children.Add(text); }
            return line;
        });
        Grid headings = ParentColumns(); string[] columns = ["No.", "Message", "Author", "Date"];
        for (int column = 0; column < columns.Length; column++)
        { TextBlock heading = new() { Text = Text("columnHeader" + (column + 1), columns[column]), FontWeight = FontWeight.Bold }; Grid.SetColumn(heading, column); headings.Children.Add(heading); }
        _parentPanel.Children.Add(new Label { Content = Caption("ParentsLabel", "This commit is a merge, select &parent:"), Target = _parents, Padding = default });
        _parentPanel.Children.Add(headings); _parentPanel.Children.Add(ClassicChrome.Border(_parents, new Thickness(1)));
        Grid header = new() { ColumnDefinitions = new("*,26") };
        header.Children.Add(new TextBlock { Text = Text("BranchInfo", "Revert this commit:"), VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(_choose, 1); header.Children.Add(_choose);
        Root.Children.Add(new StackPanel { Margin = new Thickness(8), Spacing = 6, Children = { header, _summary, _parentPanel, _automatic } });
        Track(_parents, _automatic); _parents.SelectionChanged += (_, _) => UpdateActions();
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); }; Opened += (_, _) => FocusOriginalControl(); UpdateActions();
    }
    private string Text(string item, string source) => Translations.Translate("FormRevertCommit", item, "Text", source);
    private AccessText Caption(string item, string source) => new() { Text = Translations.TranslateAccessKeys("FormRevertCommit", item, "Text", source) };
    private static Grid ParentColumns() => new() { ColumnDefinitions = new("43,*,120,80") };
    private void FocusOriginalControl() { if (IsVisible && !IsBusy) { if (_parentPanel.IsVisible) { _parents.Focus(); } else { _automatic.Focus(); } } }
    public override async Task InitializeAsync()
    {
        await RunAsync("Revert commit", token => LoadAsync(_initialRevision, token), cancellationToken: _lifetime.Token); FocusOriginalControl();
    }
    private async Task LoadAsync(string revision, CancellationToken token)
    {
        if ((await Repository.GetInfoAsync(token)).IsBare) { throw new InvalidOperationException(T("Revert requires a working tree.")); }
        GitRevertRevision selected = await Repository.GetRevertRevisionAsync(revision, token);
        _revision = selected; ResetDialogControls.SetSummary(_summary, selected.Commit);
        _parentPanel.IsVisible = selected.Parents.Count > 1;
        _parents.ItemsSource = selected.Parents.Select((commit, index) => new RevertParent(index + 1, commit)).ToArray();
        _parents.Height = Math.Min(180, Math.Max(18, selected.Parents.Count * 18 + 4));
        _parents.SelectedIndex = _parentPanel.IsVisible ? 0 : -1; Status.Text = "";
    }
    private async Task ChooseAsync()
    {
        if (_choosing || IsBusy || Attempted || _lifetime.IsCancellationRequested) { return; }
        _choosing = true; UpdateActions();
        try
        {
            GitCommit? selected = await RevertRevisionPicker.ShowAsync(this, Repository, Revision, _lifetime.Token);
            if (selected is null || !IsVisible || _lifetime.IsCancellationRequested) { return; }
            await RunAsync("Revision", token => LoadAsync(selected.Hash, token), cancellationToken: _lifetime.Token);
        }
        finally { _choosing = false; UpdateActions(); FocusOriginalControl(); }
    }
    private async Task ExecuteAsync()
    {
        if (IsBusy || _choosing || Attempted || _revision is null || _lifetime.IsCancellationRequested || !_revert.IsEnabled) { return; }
        GitRevertOptions options = new(AutoCommit, _parentPanel.IsVisible ? (_parents.SelectedItem as RevertParent)?.Number : null);
        if (_parentPanel.IsVisible && options.MainlineParent is null) { Status.Text = Text("_noneParentSelectedText", "None parent is selected!"); return; }
        await RunAsync("Revert commit", async token =>
        {
            try
            {
                GitRevertResult result = await Repository.RevertAsync(_revision.Commit.Hash, options, token);
                Attempted = true; Applied = result.Success; Conflicts = result.ConflictedPaths; MarkRepositoryChanged();
                Status.Text = result.CommandResult.StandardError;
            }
            catch (Exception error) when (error is GitCommandException or OperationCanceledException)
            { MarkRepositoryChanged(); throw; }
        }, cancellationToken: _lifetime.Token);
        // The original conflict handler belongs to the surrounding command/sequence.
        if (Attempted && IsVisible && !_lifetime.IsCancellationRequested) { Close(true); }
    }
    protected override void UpdateActions()
    {
        if (_revert is null || _choose is null) { return; }
        bool available = !IsBusy && !_choosing && !Attempted && !_lifetime.IsCancellationRequested;
        _choose.IsEnabled = available; _automatic.IsEnabled = available; _parents.IsEnabled = available;
        _revert.IsEnabled = available && _revision is not null && (!_parentPanel.IsVisible || _parents.SelectedItem is RevertParent);
    }
    private sealed record RevertParent(int Number, GitCommit Commit);
}

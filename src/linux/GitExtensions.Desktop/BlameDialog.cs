using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed class BlameDialog : RepositoryDialog
{
    private readonly TextBox _file = new() { Name = "BlameFilePath" };
    private readonly TextBox _revision = new() { Name = "BlameRevision" };
    private readonly CheckBox _whitespace = new() { Name = "BlameIgnoreWhitespace" };
    private readonly CheckBox _moves = new() { Name = "BlameDetectMoves" };
    private readonly CheckBox _copies = new() { Name = "BlameDetectCopies" };
    private readonly BlameView _view = new() { Name = "BlameView" };
    private readonly TextBox _commit = new() { Name = "BlameCommitInfo", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly FileHistoryContentView _changes = new() { Name = "BlameChanges" };
    private readonly TabControl _tabs = new() { Name = "BlameTabs" };
    private readonly int _initialLine;
    public GitFileBlame? LastBlame { get; private set; }

    public BlameDialog(GitRepository repository, string path, string? revision = "HEAD", int initialLine = 1) : base(repository, "Blame")
    {
        Width = 1100; Height = 760; MinWidth = 740; MinHeight = 480;
        _file.Text = path; _revision.Text = revision ?? ""; _initialLine = initialLine;
        Title = T("Blame") + " (" + path + ")";
        Grid paths = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,240,Auto"), Margin = new Avalonia.Thickness(6, 4) };
        Control[] fields = [new TextBlock { Text = T("File") + ":", VerticalAlignment = VerticalAlignment.Center }, _file,
            new TextBlock { Text = T("Revision") + ":", VerticalAlignment = VerticalAlignment.Center }, _revision, Action("LoadBlame", "Load", InitializeAsync)];
        for (int index = 0; index < fields.Length; index++) { Grid.SetColumn(fields[index], index); fields[index].Margin = new Avalonia.Thickness(3, 0); paths.Children.Add(fields[index]); }
        ToolTip.SetTip(_revision, T("Leave empty to blame the working directory."));
        _whitespace.Content = T("Ignore whitespace"); _moves.Content = T("Detect move and copy in this file"); _copies.Content = T("Detect move and copy in all files");
        StackPanel options = new() { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Avalonia.Thickness(9, 2), Children = { _whitespace, _moves, _copies } };
        StackPanel top = new() { Children = { paths, options } };
        DockPanel.SetDock(top, Dock.Top); Root.Children.Add(top);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(6, 4), Children =
        {
            Action("BlameThisRevision", "Blame this revision", () => NavigateAsync(previous: false)),
            Action("BlamePreviousRevision", "Blame previous revision", () => NavigateAsync(previous: true)),
            Action("BlameShowChanges", "Show changes", ShowChangesAsync),
            new Button { Name = "CancelBlame", Content = T("Close"), IsCancel = true }
        } };
        ((Button)actions.Children[^1]).Click += (_, _) => Close();
        DockPanel.SetDock(actions, Dock.Bottom); Root.Children.Add(actions);
        Grid content = new() { RowDefinitions = new RowDefinitions("150,4,*") };
        content.Children.Add(_commit);
        GridSplitter splitter = new() { Height = 4, ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(splitter, 1); content.Children.Add(splitter);
        _tabs.ItemsSource = new[] { new TabItem { Name = "BlameSourceTab", Header = T("Blame"), Content = _view }, new TabItem { Name = "BlameChangesTab", Header = T("Changes"), Content = _changes } };
        _tabs.SelectedIndex = 0; Grid.SetRow(_tabs, 2); content.Children.Add(_tabs); Root.Children.Add(content);
        _view.SelectedLineChanged += async (_, _) => { if (!IsBusy) { await UpdateInfoAsync(); } };
        Track(_file, _revision, _whitespace, _moves, _copies, _view, _tabs);
        Closed += (_, _) => { _view.Dispose(); _changes.Dispose(); };
    }

    public override Task InitializeAsync() => RunAsync("Blame", async token =>
    {
        string path = _file.Text ?? "";
        string? revision = string.IsNullOrWhiteSpace(_revision.Text) ? null : _revision.Text;
        LastBlame = await Repository.GetFileBlameAsync(path, revision, new(_whitespace.IsChecked == true, _moves.IsChecked == true, _copies.IsChecked == true), token);
        _view.SetBlame(LastBlame, _initialLine);
        _tabs.SelectedIndex = 0;
        await SetCommitInfoAsync(_view.SelectedLine, token);
        Title = T("Blame") + " (" + path + ")";
        Status.Text = LastBlame.Lines.Count + " " + T("lines");
    });

    private Task UpdateInfoAsync() => RunAsync("Commit information", token => SetCommitInfoAsync(_view.SelectedLine, token));
    private async Task SetCommitInfoAsync(GitFileBlameLine? selected, CancellationToken token)
    {
        if (selected is null) { _commit.Text = T("This file has no lines."); return; }
        GitBlameLine line = selected.Line;
        string body = line.Hash.All(character => character == '0') ? T("Not committed yet")
            : (await Repository.GetLogAsync(1, line.Hash, token, all: false)).Single().Body;
        _commit.Text = $"{T("Commit")}: {line.Hash}\n{T("Author")}: {line.AuthorName} <{line.AuthorEmail}>\n{T("Date")}: {line.AuthoredAt:O}\n{T("File")}: {selected.OriginalPath}:{line.OriginalLine}\n\n{line.Summary}\n{body}";
    }

    private Task NavigateAsync(bool previous) => RunAsync("Blame", async token =>
    {
        GitFileBlameLine selected = _view.SelectedLine ?? throw new InvalidOperationException(T("Select a line first."));
        string hash = previous ? selected.PreviousHash ?? throw new InvalidOperationException(T("This line has no previous revision.")) : selected.Line.Hash;
        if (hash.All(character => character == '0')) { throw new InvalidOperationException(T("This line has not been committed yet.")); }
        string path = previous ? selected.PreviousPath ?? selected.OriginalPath : selected.OriginalPath;
        LastBlame = await Repository.GetFileBlameAsync(path, hash, new(_whitespace.IsChecked == true, _moves.IsChecked == true, _copies.IsChecked == true), token);
        _file.Text = path; _revision.Text = hash;
        _view.SetBlame(LastBlame, Math.Clamp(selected.Line.OriginalLine, 1, Math.Max(1, LastBlame.Lines.Count)));
        _tabs.SelectedIndex = 0;
        await SetCommitInfoAsync(_view.SelectedLine, token);
        Title = T("Blame") + " (" + path + ")";
    });

    private Task ShowChangesAsync() => RunAsync("Show changes", async token =>
    {
        GitFileBlameLine selected = _view.SelectedLine ?? throw new InvalidOperationException(T("Select a line first."));
        if (selected.Line.Hash.All(character => character == '0')) { throw new InvalidOperationException(T("This line has not been committed yet.")); }
        GitRevisionChanges revisions = await Repository.GetRevisionChangesAsync(selected.Line.Hash, cancellationToken: token);
        GitRevisionFile file = revisions.Files.FirstOrDefault(file => file.Path == selected.OriginalPath) ?? new(selected.OriginalPath, 'M');
        _changes.ShowDiff(await Repository.GetRevisionFileDiffAsync(revisions, file, token));
        _tabs.SelectedIndex = 1;
    });
}

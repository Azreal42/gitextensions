using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Globalization;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Plugins;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

public sealed class BlameDialog : RepositoryDialog
{
    private readonly TextBox _file = new() { Name = "BlameFilePath" };
    private readonly TextBox _revision = new() { Name = "BlameRevision" };
    private readonly CheckBox _whitespace = new() { Name = "BlameIgnoreWhitespace" };
    private readonly CheckBox _moves = new() { Name = "BlameDetectMoves" };
    private readonly CheckBox _copies = new() { Name = "BlameDetectCopies" };
    private readonly BlameView _view;
    private readonly TextBox _commit = new() { Name = "BlameCommitInfo", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly FileHistoryContentView _changes = new() { Name = "BlameChanges" };
    private readonly TabControl _tabs = new() { Name = "BlameTabs" };
    private readonly int _initialLine;
    private bool _synchronizingPreferences;
    private bool _preferencesFromCheckboxes;
    public GitFileBlame? LastBlame { get; private set; }

    public BlameDialog(GitRepository repository, string path, string? revision = "HEAD", int initialLine = 1, IGourceAvatarProvider? avatarProvider = null) : base(repository, "Blame")
    {
        _view = new(avatarProvider) { Name = "BlameView" };
        _view.CommitInformationProvider = (line, token) => ReadCommitInformationAsync(repository, line, token);
        _view.InteractionError += error => Status.Text = error;
        Width = 1100; Height = 760; MinWidth = 740; MinHeight = 480;
        _file.Text = path; _revision.Text = revision ?? ""; _initialLine = initialLine;
        Title = T("Blame") + " (" + path + ")";
        Grid paths = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,240,Auto"), Margin = new Avalonia.Thickness(6, 4) };
        Control[] fields = [new TextBlock { Text = T("File") + ":", VerticalAlignment = VerticalAlignment.Center }, _file,
            new TextBlock { Text = T("Revision") + ":", VerticalAlignment = VerticalAlignment.Center }, _revision, Action("LoadBlame", "Load", InitializeAsync)];
        for (int index = 0; index < fields.Length; index++) { Grid.SetColumn(fields[index], index); fields[index].Margin = new Avalonia.Thickness(3, 0); paths.Children.Add(fields[index]); }
        ToolTip.SetTip(_revision, T("Leave empty to blame the working directory."));
        _whitespace.Content = T("Ignore whitespace"); _moves.Content = T("Detect move and copy in this file"); _copies.Content = T("Detect move and copy in all files");
        Button optionsButton = _view.CreateOptionsButton();
        StackPanel options = new() { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Avalonia.Thickness(9, 2), Children = { optionsButton, _whitespace, _moves, _copies } };
        StackPanel top = new() { Children = { paths, options } };
        DockPanel.SetDock(top, Dock.Top); Root.Children.Add(top);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(6, 4), Children =
        {
            Action("BlameThisRevision", "Blame this revision", () => NavigateAsync(previous: false)),
            Action("BlamePreviousRevision", "Blame previous revision", () => NavigateAsync(previous: true)),
            Action("BlameShowChanges", "Show changes", () => ShowChangesAsync()),
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
        _view.CommandRequested += async (_, args) =>
        {
            if (IsBusy) { return; }
            if (args.Command == BlameCommand.ShowChanges) { await ShowChangesAsync(args.Line); }
            else { await NavigateAsync(args.Command == BlameCommand.PreviousRevision, args.Line); }
        };
        BlamePreferences previousPreferences = _view.Preferences;
        _view.PreferencesChanged += async preferences =>
        {
            bool reload = previousPreferences.GitOptions != preferences.GitOptions;
            previousPreferences = preferences;
            SynchronizePreferences();
            if (_view.PreferenceError is string error) { Status.Text = error; }
            if (reload && !_preferencesFromCheckboxes && LastBlame is not null && !IsBusy) { await InitializeAsync(); }
        };
        SynchronizePreferences();
        _whitespace.IsCheckedChanged += (_, _) => SaveCheckboxPreferences();
        _moves.IsCheckedChanged += (_, _) => SaveCheckboxPreferences();
        _copies.IsCheckedChanged += (_, _) => SaveCheckboxPreferences();
        Track(_file, _revision, _whitespace, _moves, _copies, optionsButton, _view, _tabs);
        _changes.TextView.DisplayOptionsChanged += async (_, _) =>
        {
            if (_tabs.SelectedIndex == 1 && _view.SelectedLine is not null) { await RequestDiffReloadAsync(() => ShowChangesAsync()); }
        };
        Closing += (_, _) => _view.CancelAvatarLoading();
        Closed += (_, _) => { _view.Dispose(); _changes.Dispose(); };
    }

    public override Task InitializeAsync() => RunAsync("Blame", async token =>
    {
        string path = _file.Text ?? "";
        string? revision = string.IsNullOrWhiteSpace(_revision.Text) ? null : _revision.Text;
        int selectedLine = _view.SelectedLine?.Line.LineNumber ?? _initialLine;
        LastBlame = await Repository.GetFileBlameAsync(path, revision, _view.Preferences.GitOptions, token);
        _view.SetBlame(LastBlame, selectedLine);
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

    private Task NavigateAsync(bool previous, GitFileBlameLine? contextLine = null) => RunAsync("Blame", async token =>
    {
        GitFileBlameLine selected = contextLine ?? _view.SelectedLine ?? throw new InvalidOperationException(T("Select a line first."));
        string hash = selected.Line.Hash;
        if (hash.All(character => character == '0')) { throw new InvalidOperationException(T("This line has not been committed yet.")); }
        string path = selected.OriginalPath;
        int position = selected.Line.OriginalLine;
        GitFileBlameOptions options = _view.Preferences.GitOptions;
        if (previous)
        {
            GitBlamePreviousRevision target = await Repository.GetBlamePreviousRevisionAsync(selected, options, _view.Preferences.UseHistogramDiffAlgorithm, token)
                ?? throw new InvalidOperationException(T("This line has no previous revision."));
            hash = target.Revision; path = target.Path; position = target.Line;
        }
        LastBlame = await Repository.GetFileBlameAsync(path, hash, options, token);
        _file.Text = path; _revision.Text = hash;
        _view.SetBlame(LastBlame, Math.Clamp(position, 1, Math.Max(1, LastBlame.Lines.Count)));
        _tabs.SelectedIndex = 0;
        await SetCommitInfoAsync(_view.SelectedLine, token);
        Title = T("Blame") + " (" + path + ")";
    });

    public Task ShowChangesAsync(GitFileBlameLine? contextLine = null) => RunAsync("Show changes", async token =>
    {
        GitFileBlameLine selected = contextLine ?? _view.SelectedLine ?? throw new InvalidOperationException(T("Select a line first."));
        if (selected.Line.Hash.All(character => character == '0')) { throw new InvalidOperationException(T("This line has not been committed yet.")); }
        GitRevisionChanges revisions = await Repository.GetRevisionChangesAsync(selected.Line.Hash, cancellationToken: token);
        GitRevisionFile file = revisions.Files.FirstOrDefault(file => file.Path == selected.OriginalPath) ?? new(selected.OriginalPath, 'M');
        await _changes.TextView.ConfigureRepositoryAsync(Repository, token);
        _changes.ShowDiff(await Repository.GetRevisionFileDiffDocumentAsync(revisions, file,
            displayOptions: _changes.TextView.DisplayOptions, cancellationToken: token));
        _tabs.SelectedIndex = 1;
    });

    private void SynchronizePreferences()
    {
        _synchronizingPreferences = true;
        _whitespace.IsChecked = _view.Preferences.IgnoreWhitespace;
        _moves.IsChecked = _view.Preferences.DetectMoves;
        _copies.IsChecked = _view.Preferences.DetectCopies;
        _synchronizingPreferences = false;
    }
    private void SaveCheckboxPreferences()
    {
        if (_synchronizingPreferences) { return; }
        _preferencesFromCheckboxes = true;
        try
        {
            _view.SetPreferences(_view.Preferences with { IgnoreWhitespace = _whitespace.IsChecked == true,
                DetectMoves = _moves.IsChecked == true, DetectCopies = _copies.IsChecked == true });
        }
        finally { _preferencesFromCheckboxes = false; }
    }

    internal static async Task<string> ReadCommitInformationAsync(GitRepository repository, GitFileBlameLine selected, CancellationToken token)
    {
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        GitBlameLine line = selected.Line;
        string author = translations.Translate("Author") + ": " + line.AuthorName;
        string date = translations.Translate("Author date") + ": " + line.AuthoredAt.ToString("G", CultureInfo.CurrentCulture);
        string committer = "";
        if (!line.Hash.All(character => character == '0'))
        {
            GitResult result = await repository.RunAsync(["show", "--encoding=UTF-8", "--no-patch", "--format=%cn%x00%ce%x00%cI", line.Hash, "--"], token);
            string[] fields = result.StandardOutput.TrimEnd('\r', '\n').Split('\0');
            DateTimeOffset committed = DateTimeOffset.Parse(fields[2], CultureInfo.InvariantCulture);
            if (fields[0] != line.AuthorName || committed != line.AuthoredAt)
            {
                committer = "\n" + translations.Translate("Committer") + ": " + fields[0] + "\n" + translations.Translate("Commit date") + ": " + committed.ToString("G", CultureInfo.CurrentCulture);
            }
        }
        return author + "\n" + date + committer + "\n" + translations.Translate("Commit hash") + ": " + line.Hash[..Math.Min(9, line.Hash.Length)] +
            "\n" + translations.Translate("Summary") + ": " + line.Summary + "\n\n" + translations.Translate("FileName") + ": " + ExportDialogControls.DisplayPath(selected.OriginalPath);
    }
}

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
    private readonly CommitInformationView _commit = new() { Name = "BlameCommitInfo", ShowRevisionLinks = false };
    private readonly FileHistoryContentView _changes = new() { Name = "BlameChanges" };
    private readonly TabControl _tabs = new() { Name = "BlameTabs" };
    private readonly int _initialLine;
    private readonly string _initialPath;
    private readonly string? _initialFileEncodingName;
    private bool _initialFileEncodingApplied;
    private bool _synchronizingPreferences;
    private bool _preferencesFromCheckboxes;
    private readonly CancellationTokenSource _informationLifetime = new();
    private bool _informationClosing, _informationCloseReady;
    private long _informationGeneration;
    private Exception? _informationError;
    private Window? _closingInformationOwner;
    internal Task InformationCompletion => _commit.Completion;
    public GitFileBlame? LastBlame { get; private set; }

    public BlameDialog(GitRepository repository, string path, string? revision = "HEAD", int initialLine = 1,
        IGourceAvatarProvider? avatarProvider = null, string? fileEncodingName = null) : base(repository, "Blame")
    {
        _changes.TextView.ContextMenu = new ContextMenu { ItemsSource = _changes.TextView.CreateContextMenuItems("BlameDiff") };
        _changes.Toolbar.SetNames("BlameDiff");
        _changes.TextView.ComparisonMenu.OptionsChanged += async (_, _) =>
        {
            if (_tabs.SelectedIndex == 1) { await RequestDiffReloadAsync(() => ShowChangesAsync()); }
        };
        _view = new(avatarProvider) { Name = "BlameView" };
        _view.CommitInformationProvider = (line, token) => ReadCommitInformationAsync(repository, line, token);
        _view.InteractionError += error => Status.Text = error;
        _commit.InteractionError += error => { if (!_informationClosing) { Status.Text = error; } };
        _commit.CommandRequested += async (command, argument) =>
        {
            if (command != CommitInformationCommand.AddNotes || argument is null || IsBusy || _informationClosing) { return; }
            NoteDialog dialog = new(Repository, argument) { RequestedThemeVariant = ActualThemeVariant };
            dialog.Opened += async (_, _) => await dialog.InitializeAsync();
            try
            {
                await dialog.ShowDialog(this);
                if (_informationClosing) { return; }
                if (dialog.HasRepositoryChanges) { MarkRepositoryChanged(); }
                await UpdateInfoAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { if (!_informationClosing) { ReportError(exception); } }
        };
        Width = 1100; Height = 760; MinWidth = 740; MinHeight = 480;
        _file.Text = path; _revision.Text = revision ?? ""; _initialLine = initialLine; _initialPath = path;
        _initialFileEncodingName = fileEncodingName;
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
        _view.TextView.FileEncodingChanged += async (_, _) =>
        {
            if (LastBlame is not null && !IsBusy) { await InitializeAsync(); }
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
        Closing += (_, args) =>
        {
            _view.CancelAvatarLoading();
            if (_informationCloseReady) { return; }
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingInformationOwner = Owner as Window; }
            _informationClosing = true; _informationLifetime.Cancel(); _commit.BeginClose();
            Task pending = _commit.Completion;
            if (!pending.IsCompleted) { args.Cancel = true; _ = CloseAfterInformationAsync(pending); }
        };
        Closed += (_, _) =>
        {
            _informationCloseReady = true;
            _commit.Dispose(); _informationLifetime.Dispose(); _view.Dispose(); _changes.Dispose();
            Window? owner = _closingInformationOwner; _closingInformationOwner = null;
            if (owner is not null) { Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        };
    }

    public override Task InitializeAsync() => RunAsync("Blame", async token =>
    {
        string path = _file.Text ?? "";
        string? revision = string.IsNullOrWhiteSpace(_revision.Text) ? null : _revision.Text;
        int? selectedLine = LastBlame is null && path == _initialPath ? _initialLine : null;
        await _view.ConfigureRepositoryAsync(Repository, token);
        if (!_initialFileEncodingApplied)
        {
            _view.TextView.FileEncodingName = _initialFileEncodingName;
            _initialFileEncodingApplied = true;
        }
        _view.TextView.BeginDocumentLoad();
        LastBlame = await Repository.GetFileBlameAsync(path, revision, _view.Preferences.GitOptions, token, _view.TextView.FileEncodingName);
        _view.SetBlame(LastBlame, selectedLine);
        _tabs.SelectedIndex = 0;
        await SetCommitInfoAsync(_view.SelectedLine, token);
        Title = T("Blame") + " (" + path + ")";
        if (LastError is null) { Status.Text = LastBlame.Lines.Count + " " + T("lines"); }
    });

    private Task UpdateInfoAsync() => SetCommitInfoAsync(_view.SelectedLine, _informationLifetime.Token);
    private Task SetCommitInfoAsync(GitFileBlameLine? selected, CancellationToken token)
    {
        if (_informationClosing) { return Task.CompletedTask; }
        _informationGeneration++;
        if (selected is null) { _commit.SetMessage(T("This file has no lines.")); return Task.CompletedTask; }
        GitBlameLine line = selected.Line;
        string context = selected.OriginalPath + ":" + line.OriginalLine;
        if (line.Hash.All(character => character == '0'))
        {
            _commit.SetMessage($"{T("Not committed yet")}\n{T("Author")}: {line.AuthorName} <{line.AuthorEmail}>\n{T("File")}: {context}\n\n{line.Summary}");
            return Task.CompletedTask;
        }
        _commit.SetRevision(Repository, new(line.Hash, [], line.AuthorName, line.AuthorEmail, line.AuthoredAt, line.Summary, ""), context: context);
        return LoadInformationAsync(_informationGeneration, token);
    }

    private async Task LoadInformationAsync(long generation, CancellationToken token)
    {
        if (_informationError is not null && ReferenceEquals(LastException, _informationError)) { ClearError(); }
        _informationError = null;
        try { await _commit.LoadAsync(token); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_informationClosing && generation == _informationGeneration)
            { _informationError = exception; ReportError(exception); }
        }
    }

    private async Task CloseAfterInformationAsync(Task pending)
    {
        try { await pending; }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The current request reports its own error; closing still drains it. */ }
        finally
        {
            _informationCloseReady = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (IsVisible) { Close(); } });
        }
    }

    private Task NavigateAsync(bool previous, GitFileBlameLine? contextLine = null) => RunAsync("Blame", async token =>
    {
        GitFileBlameLine selected = contextLine ?? _view.SelectedLine ?? throw new InvalidOperationException(T("Select a line first."));
        string hash = selected.Line.Hash;
        if (hash.All(character => character == '0')) { throw new InvalidOperationException(T("This line has not been committed yet.")); }
        _view.TextView.BeginDocumentLoad();
        string path = selected.OriginalPath;
        int position = selected.Line.OriginalLine;
        GitFileBlameOptions options = _view.Preferences.GitOptions;
        if (previous)
        {
            GitBlamePreviousRevision target = await Repository.GetBlamePreviousRevisionAsync(selected, options, _view.Preferences.UseHistogramDiffAlgorithm, token)
                ?? throw new InvalidOperationException(T("This line has no previous revision."));
            hash = target.Revision; path = target.Path; position = target.Line;
        }
        await _view.ConfigureRepositoryAsync(Repository, token);
        LastBlame = await Repository.GetFileBlameAsync(path, hash, options, token, _view.TextView.FileEncodingName);
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
        _changes.TextView.BeginDocumentLoad();
        await _changes.TextView.ConfigureRepositoryAsync(Repository, token);
        _changes.TextView.DocumentIdentity = string.Join('\0', Repository.Path, file.Path, revisions.Revision, revisions.BaseRevision, "diff");
        _changes.ShowDiff(await Repository.GetRevisionFileDiffDocumentAsync(revisions, file,
            options: _changes.TextView.ComparisonMenu.Options, displayOptions: _changes.TextView.DisplayOptions, cancellationToken: token), file.Path);
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
            GitCommitDetails details = await repository.GetCommitDetailsAsync(line.Hash, false, token);
            DateTimeOffset committed = details.Committer.Date;
            if (details.Committer.Name != line.AuthorName || committed != line.AuthoredAt)
            {
                committer = "\n" + translations.Translate("Committer") + ": " + details.Committer.Name + "\n" + translations.Translate("Commit date") + ": " + committed.ToString("G", CultureInfo.CurrentCulture);
            }
        }
        return author + "\n" + date + committer + "\n" + translations.Translate("Commit hash") + ": " + line.Hash[..Math.Min(9, line.Hash.Length)] +
            "\n" + translations.Translate("Summary") + ": " + line.Summary + "\n\n" + translations.Translate("FileName") + ": " + ExportDialogControls.DisplayPath(selected.OriginalPath);
    }
}

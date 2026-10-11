using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed class ApplyPatchDialog : RepositoryDialog
{
    private readonly ExportPreferencesStore _preferences = new();
    private readonly RadioButton _fileMode = new() { Name = "PatchFileMode", GroupName = "ApplyPatchInput", IsChecked = true };
    private readonly RadioButton _directoryMode = new() { Name = "PatchDirectoryMode", GroupName = "ApplyPatchInput" };
    private readonly TextBox _file = new() { Name = "ApplyPatchFile", Height = 24 };
    private readonly TextBox _directory = new() { Name = "ApplyPatchDirectory", Height = 24 };
    private readonly ComboBox _kind = new() { Name = "PatchInputKind", Height = 24 };
    private readonly CheckBox _ignore = new() { Name = "ApplyIgnoreWhitespace" };
    private readonly CheckBox _signoff = new() { Name = "ApplySignOff" };
    private readonly CheckBox _threeWay = new() { Name = "AmThreeWay", IsChecked = true };
    private readonly CheckBox _reverse = new() { Name = "ApplyReverse" };
    private readonly CheckBox _check = new() { Name = "ApplyCheckOnly" };
    private readonly CheckBox _rawThreeWay = new() { Name = "ApplyThreeWay" };
    private readonly ComboBox _target = new() { Name = "ApplyTarget", Height = 24 };
    private readonly ComboBox _whitespace = new() { Name = "ApplyWhitespace", Height = 24 };
    private readonly NumericUpDown _strip = new() { Name = "ApplyStrip", Minimum = 0, Maximum = 255, Value = 1, Width = 100, Height = 24, FormatString = "0", Padding = new Thickness(4, 2) };
    private readonly TextBox _prefix = new() { Name = "ApplyDirectoryPrefix", Height = 24 };
    private readonly Control _rawOptions;
    private readonly ListBox _patches = new() { Name = "PatchGrid", Height = 76 };
    private readonly ListBox _files = new() { Name = "PatchChangedFiles", SelectionMode = SelectionMode.Multiple };
    private readonly TextBox _diffSource = new();
    private readonly DiffView _diff;
    private readonly TextBlock _conflict = new() { Name = "PatchConflictWarning", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly Button _apply;
    private readonly Button _stage;
    private readonly Button _resolve;
    private readonly Button _continue;
    private readonly Button _skip;
    private readonly Button _abort;
    private readonly Button _browseFile;
    private readonly Button _browseDirectory;
    private GitAmState? _am;
    private GitPatchFileKind? _detected;
    private string[] _submittedFiles = [];
    private readonly HashSet<int> _skipped = [];
    private bool _completed;
    private bool _aborted;
    private bool _bare;
    public GitAmState? AmState => _am;

    public ApplyPatchDialog(GitRepository repository, string? patchPath = null) : base(repository, "Apply patch")
    {
        _diff = new DiffView(_diffSource) { Name = "PatchPreview" };
        ExportPreferences preferences = _preferences.Load();
        _ignore.IsChecked = preferences.ApplyIgnoreWhitespace;
        _signoff.IsChecked = preferences.ApplySignOff;
        Width = 880;
        Height = 650;
        MinWidth = 700;
        MinHeight = 500;
        _fileMode.Content = T("Patch file");
        _directoryMode.Content = T("Patch directory");
        if (patchPath is not null && Directory.Exists(patchPath)) { _directory.Text = patchPath; _directoryMode.IsChecked = true; }
        else { _file.Text = patchPath; }
        _kind.ItemsSource = new[] { T("Automatic"), T("Raw diff"), T("Mailbox patches") };
        _kind.SelectedIndex = 0;
        _target.ItemsSource = new[] { T("Working copy"), T("Index only"), T("Index and working copy") };
        _target.SelectedIndex = 0;
        _whitespace.ItemsSource = new[] { T("Warn"), T("No warnings"), T("Fix whitespace"), T("Reject whitespace errors"), T("Reject all whitespace errors") };
        _whitespace.SelectedIndex = 0;
        _ignore.Content = T("Ignore whitespace");
        _signoff.Content = T("Sign-off");
        _threeWay.Content = T("Three-way merge");
        _reverse.Content = T("Reverse patch");
        _check.Content = T("Check only");
        _rawThreeWay.Content = T("Three-way apply");
        _apply = Action("ApplyPatch", "Apply patch", ApplyAsync);
        _apply.IsDefault = true;
        _stage = Action("StagePatchFiles", "Add files", StageAsync);
        _resolve = Action("ResolvePatchConflicts", "Solve conflicts…", ResolveAsync);
        _continue = Action("ContinuePatch", "Conflicts resolved", () => ManageAsync(GitOperationAction.Continue));
        _skip = Action("SkipPatch", "Skip patch", () => ManageAsync(GitOperationAction.Skip));
        _abort = Action("AbortPatch", "Abort patch", () => ManageAsync(GitOperationAction.Abort));
        StackPanel buttons = ExportDialogControls.Buttons(_stage, _resolve, _continue, _skip, _abort, _apply, Action("CloseApplyPatch", "Close", () => { Close(); return Task.CompletedTask; }));
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        StackPanel header = new() { Margin = new Thickness(10, 6), Spacing = 4 };
        Grid inputs = ExportDialogControls.Fields();
        _browseFile = Action("BrowseApplyPatch", "Browse…", BrowseFileAsync);
        _browseDirectory = Action("BrowseApplyDirectory", "Browse…", BrowseDirectoryAsync);
        ExportDialogControls.Field(inputs, _fileMode, _file, _browseFile);
        ExportDialogControls.Field(inputs, _directoryMode, _directory, _browseDirectory);
        ExportDialogControls.Field(inputs, T("Patch type"), _kind, Action("RefreshPatch", "Refresh", RefreshAsync));
        header.Children.Add(inputs);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { _ignore, _signoff, _threeWay } });
        Grid raw = ExportDialogControls.Fields();
        ExportDialogControls.Field(raw, T("Apply to"), _target);
        ExportDialogControls.Field(raw, T("Whitespace"), _whitespace);
        ExportDialogControls.Field(raw, T("Strip directories"), _strip);
        ExportDialogControls.Field(raw, T("Directory prefix"), _prefix);
        _rawOptions = ExportDialogControls.Expandable("RawPatchOptions", T("Raw diff options"), new StackPanel { Spacing = 4, Children = { raw, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { _reverse, _check, _rawThreeWay } } } });
        header.Children.Add(_rawOptions);
        header.Children.Add(_conflict);
        DockPanel.SetDock(header, Dock.Top);
        Root.Children.Add(header);
        _patches.ItemTemplate = new FuncDataTemplate<PatchApplicationRow>((row, _) =>
        {
            Grid cells = new() { ColumnDefinitions = new ColumnDefinitions("40,*,100"), Height = 22 };
            string[] values = [row?.Number.ToString() ?? "", row is null ? "" : ExportDialogControls.DisplayPath(row.Name), row?.Status ?? ""];
            for (int column = 0; column < values.Length; column++)
            {
                TextBlock cell = new() { Text = values[column], Margin = new Thickness(4, 2), TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(cell, column);
                cells.Children.Add(cell);
            }
            return cells;
        });
        _patches.Margin = new Thickness(10, 0, 10, 4);
        DockPanel.SetDock(_patches, Dock.Top);
        Root.Children.Add(_patches);
        _files.ItemTemplate = new FuncDataTemplate<GitChange>((change, _) => new TextBlock
        {
            Text = change is null ? "" : (change.IsConflict ? T("Conflict") : change.IsStaged ? T("Staged") : T("Modified")) + "    " + ExportDialogControls.DisplayPath(change.Path),
            Margin = new Thickness(4, 2), TextTrimming = TextTrimming.CharacterEllipsis
        });
        TabControl body = new() { Name = "ApplyPatchTabs", Margin = new Thickness(10, 0, 10, 6), ItemsSource = new[]
        {
            new TabItem { Header = T("Patch"), Content = _diff }, new TabItem { Header = T("Files"), Content = _files }
        } };
        Root.Children.Add(body);
        Track(_fileMode, _directoryMode, _file, _directory, _kind, _ignore, _signoff, _threeWay, _reverse, _check, _rawThreeWay, _target, _whitespace, _strip, _prefix);
        _fileMode.IsCheckedChanged += (_, _) => UpdateActions();
        _directoryMode.IsCheckedChanged += (_, _) => UpdateActions();
        _kind.SelectionChanged += (_, _) => UpdateActions();
        _ignore.IsCheckedChanged += (_, _) => SavePreferences();
        _signoff.IsCheckedChanged += (_, _) => SavePreferences();
        _files.SelectionChanged += (_, _) => UpdateActions();
        _file.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { _detected = null; UpdateActions(); } };
        UpdateActions();
    }

    private void SavePreferences() => _preferences.Save(_preferences.Load() with { ApplyIgnoreWhitespace = _ignore.IsChecked == true, ApplySignOff = _signoff.IsChecked == true });

    public override Task InitializeAsync() => RunAsync("Apply patch", async token =>
    {
        _bare = (await Repository.GetInfoAsync(token)).IsBare;
        await ReadStateAsync(token);
        if (_am?.IsActive != true && (_directoryMode.IsChecked == true ? Directory.Exists(_directory.Text) : File.Exists(_file.Text))) { await ReadInputAsync(token); }
        if (_bare) { Status.Text = T("Apply patches in a working tree."); }
    });

    private Task RefreshAsync() => RunAsync("Refresh", async token =>
    {
        await ReadStateAsync(token);
        if (_am?.IsActive != true) { await ReadInputAsync(token); }
    });

    private async Task ReadInputAsync(CancellationToken token)
    {
        _submittedFiles = _directoryMode.IsChecked == true
            ? Directory.GetFiles(_directory.Text ?? "").OrderBy(file => Path.GetFileName(file), StringComparer.Ordinal).ToArray()
            : [Path.GetFullPath(_file.Text ?? "")];
        if (_submittedFiles.Length == 0) { throw new ArgumentException(T("Please select a patch to apply")); }
        _detected = _directoryMode.IsChecked == true ? GitPatchFileKind.Mailbox : await GitRepository.GetPatchFileKindAsync(_submittedFiles[0], token);
        using StreamReader reader = new(_submittedFiles[0]);
        char[] preview = new char[1024 * 1024];
        int count = await reader.ReadBlockAsync(preview.AsMemory(), token);
        _diffSource.Text = new string(preview, 0, count) + (count == preview.Length ? "\n" + T("Preview is truncated.") : "");
        _completed = _aborted = false;
        UpdatePatchRows();
        Status.Text = T("Apply patch");
    }

    private async Task ReadStateAsync(CancellationToken token)
    {
        _am = await Repository.GetAmStateAsync(token);
        GitStatus status = await Repository.GetStatusAsync(token);
        string[] selected = _files.SelectedItems?.Cast<GitChange>().Select(change => change.Path).ToArray() ?? [];
        _files.ItemsSource = status.Changes;
        _files.SelectedItems?.Clear();
        foreach (GitChange change in status.Changes.Where(change => selected.Contains(change.Path, StringComparer.Ordinal))) { _files.SelectedItems?.Add(change); }
        _conflict.IsVisible = _am.IsActive && _am.ConflictedPaths.Count > 0;
        _conflict.Text = T("There are unresolved merge conflicts") + ": " + _am.ConflictedPaths.Count;
        if (_am.IsActive)
        {
            _diffSource.Text = _am.CurrentPatchDiff ?? "";
            Status.Text = T("Patch") + " " + _am.CurrentPatch + "/" + _am.PatchCount + ": " + _am.Subject;
        }
        UpdatePatchRows();
    }

    private void UpdatePatchRows()
    {
        int count = _am?.IsActive == true ? _am.PatchCount ?? _submittedFiles.Length : _submittedFiles.Length;
        _patches.Height = Math.Clamp(count * 24, 24, 96);
        _patches.ItemsSource = Enumerable.Range(1, count).Select(index => new PatchApplicationRow(index,
            index <= _submittedFiles.Length ? Path.GetFileName(_submittedFiles[index - 1]) : index == _am?.CurrentPatch ? _am?.Subject ?? T("Patch") : T("Patch") + " " + index,
            _aborted ? T("Aborted") : _skipped.Contains(index) ? T("Skipped") : _completed || index < _am?.CurrentPatch ? T("Applied") : index == _am?.CurrentPatch ? T("Current") : T("Pending"))).ToArray();
    }

    private GitPatchFileKind? SelectedKind => _directoryMode.IsChecked == true ? GitPatchFileKind.Mailbox : _kind.SelectedIndex switch { 1 => GitPatchFileKind.Diff, 2 => GitPatchFileKind.Mailbox, _ => _detected };

    private Task ApplyAsync() => RunAsync("Apply patch", async token =>
    {
        await ReadInputAsync(token);
        bool changes = SelectedKind != GitPatchFileKind.Diff || _check.IsChecked != true;
        try
        {
            if (SelectedKind == GitPatchFileKind.Diff)
            {
                await Repository.ApplyDiffPatchAsync(_submittedFiles[0], new(_ignore.IsChecked == true, _reverse.IsChecked == true, _check.IsChecked == true,
                    (GitPatchTarget)Math.Max(0, _target.SelectedIndex), _rawThreeWay.IsChecked == true, (GitPatchWhitespace)Math.Max(0, _whitespace.SelectedIndex),
                    (int)(_strip.Value ?? 1), string.IsNullOrEmpty(_prefix.Text) ? null : _prefix.Text), token);
            }
            else
            {
                _skipped.Clear();
                await Repository.ApplyMailboxPatchesAsync(_submittedFiles, new(_threeWay.IsChecked == true, _signoff.IsChecked == true, _ignore.IsChecked == true), token);
            }
            _completed = true;
        }
        finally
        {
            if (changes) { MarkRepositoryChanged(); }
            await ReadStateAsync(token);
        }
        Status.Text = _check.IsChecked == true && SelectedKind == GitPatchFileKind.Diff ? T("Patch check completed") : T("Patch applied");
    });

    private Task StageAsync() => RunAsync("Add files", async token =>
    {
        string[] selected = _files.SelectedItems?.Cast<GitChange>().Select(change => change.Path).ToArray() ?? [];
        if (selected.Length == 0) { throw new InvalidOperationException(T("Select files first")); }
        await Repository.StageAsync(selected, token);
        MarkRepositoryChanged();
        await ReadStateAsync(token);
    });

    private async Task ResolveAsync()
    {
        GitChange? selected = _files.SelectedItems?.Cast<GitChange>().FirstOrDefault(change => change.IsConflict)
            ?? _files.ItemsSource?.Cast<GitChange>().FirstOrDefault(change => change.IsConflict);
        if (selected is null) { return; }
        PatchConflictDialog dialog = new(Repository, selected.Path);
        await dialog.InitializeAsync();
        await dialog.ShowDialog(this);
        if (dialog.HasRepositoryChanges) { MarkRepositoryChanged(); }
        await RunAsync("Refresh", ReadStateAsync);
    }

    private async Task ManageAsync(GitOperationAction action)
    {
        if (action == GitOperationAction.Abort && !await Dialogs.ConfirmAsync(this, T("Abort patch"), T("Abort the mailbox application and restore its original branch and files?"))) { return; }
        await RunAsync(action.ToString(), async token =>
        {
            int? current = _am?.CurrentPatch;
            try
            {
                await Repository.ManageAmAsync(action, token);
                _aborted = action == GitOperationAction.Abort;
                _completed = !_aborted;
            }
            finally
            {
                MarkRepositoryChanged();
                await ReadStateAsync(token);
                // Skip can succeed for this patch while Git reports a conflict in the next one.
                if (action == GitOperationAction.Skip && current is int skipped && (_am?.IsActive == false || _am?.CurrentPatch is int next && next > skipped))
                {
                    _skipped.Add(skipped);
                    UpdatePatchRows();
                }
            }
            Status.Text = T(action == GitOperationAction.Abort ? "Patch application aborted" : "Patch applied");
        });
    }

    private async Task BrowseFileAsync()
    {
        IStorageFile? file = (await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Select patch file"), AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(T("Patch files")) { Patterns = ["*.patch", "*.diff", "*.mbox"] }, new FilePickerFileType(T("All files")) { Patterns = ["*"] }]
        })).FirstOrDefault();
        if (file?.TryGetLocalPath() is string path) { _file.Text = path; _fileMode.IsChecked = true; await RefreshAsync(); }
    }

    private async Task BrowseDirectoryAsync()
    {
        string? directory = await ExportDialogControls.FolderAsync(this, T("Select patch directory"));
        if (directory is not null) { _directory.Text = directory; _directoryMode.IsChecked = true; await RefreshAsync(); }
    }

    protected override void UpdateActions()
    {
        bool active = _am?.IsActive == true;
        bool enabled = !IsBusy && !active && !_bare;
        bool directory = _directoryMode.IsChecked == true;
        bool mailbox = SelectedKind == GitPatchFileKind.Mailbox;
        _fileMode.IsEnabled = _directoryMode.IsEnabled = enabled;
        _file.IsEnabled = _browseFile.IsEnabled = enabled && !directory;
        _directory.IsEnabled = _browseDirectory.IsEnabled = enabled && directory;
        _kind.IsEnabled = enabled && !directory;
        _ignore.IsEnabled = enabled;
        _signoff.IsEnabled = _threeWay.IsEnabled = enabled && mailbox;
        _rawOptions.IsEnabled = enabled && SelectedKind == GitPatchFileKind.Diff;
        _apply.IsEnabled = enabled;
        _stage.IsEnabled = !IsBusy && active && _files.SelectedItems?.Count > 0;
        _resolve.IsEnabled = !IsBusy && active && _am!.ConflictedPaths.Count > 0;
        _continue.IsEnabled = !IsBusy && active && _am!.ConflictedPaths.Count == 0;
        _skip.IsEnabled = _abort.IsEnabled = !IsBusy && active;
    }
}

internal sealed record PatchApplicationRow(int Number, string Name, string Status);

internal sealed class PatchConflictDialog : RepositoryDialog
{
    private readonly string _path;
    private readonly TextBox _text = new() { Name = "PatchConflictText", AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("monospace") };
    private readonly Button _save;
    private byte[] _original = [];
    private bool _editable;

    internal PatchConflictDialog(GitRepository repository, string path) : base(repository, "Solve conflicts")
    {
        _path = path;
        Width = 780;
        Height = 520;
        MinWidth = 600;
        MinHeight = 360;
        _save = Action("SavePatchResolution", "Save and add file", SaveAsync);
        StackPanel buttons = ExportDialogControls.Buttons(Action("PatchOurs", "Use ours…", () => ChooseAsync("ours")), Action("PatchTheirs", "Use theirs…", () => ChooseAsync("theirs")),
            Action("PatchMergetool", "Merge tool…", MergetoolAsync), _save, Action("ClosePatchResolution", "Close", () => { Close(); return Task.CompletedTask; }));
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        TextBlock heading = new() { Text = ExportDialogControls.DisplayPath(path), Margin = new Thickness(10, 8), TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(heading, Dock.Top);
        Root.Children.Add(heading);
        _text.Margin = new Thickness(10, 0, 10, 6);
        Root.Children.Add(_text);
        Track(_text);
    }

    public override Task InitializeAsync() => RunAsync("Solve conflicts", LoadAsync);

    private async Task LoadAsync(CancellationToken token)
    {
        string file = Path.Combine(Repository.Path, _path);
        _editable = false;
        if (!File.Exists(file) || new FileInfo(file).LinkTarget is not null)
        {
            _text.Text = T("Choose a version or use the configured merge tool.");
        }
        else
        {
            _original = await File.ReadAllBytesAsync(file, token);
            try { _text.Text = new UTF8Encoding(false, true).GetString(_original); _editable = true; }
            catch (DecoderFallbackException) { _text.Text = T("This file is binary. Choose a version or use the configured merge tool."); }
        }
        _text.IsReadOnly = !_editable;
        Status.Text = T("Resolve conflicts, then save and add the file.");
    }

    private Task SaveAsync() => RunAsync("Save resolution", async token =>
    {
        string file = Path.Combine(Repository.Path, _path);
        if (!(await File.ReadAllBytesAsync(file, token)).SequenceEqual(_original)) { throw new IOException(T("The file changed outside this editor. Close and reopen it before saving.")); }
        string temporary = file + ".gitextensions-resolution-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { await stream.WriteAsync(Encoding.UTF8.GetBytes(_text.Text ?? ""), token); }
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(temporary, File.GetUnixFileMode(file)); }
            File.Move(temporary, file, overwrite: true);
            MarkRepositoryChanged();
            await Repository.StageAsync([_path], token);
            _original = await File.ReadAllBytesAsync(file, token);
            Status.Text = T("Conflicts resolved");
            Close();
        }
        finally { File.Delete(temporary); }
    });

    private async Task ChooseAsync(string version)
    {
        if (!await Dialogs.ConfirmAsync(this, T(version == "ours" ? "Use ours" : "Use theirs"), T("Replace the conflicted file with this version and add it to the index?"))) { return; }
        await RunAsync("Solve conflicts", async token =>
        {
            await Repository.RunAsync(["checkout", "--" + version, "--", _path], token);
            MarkRepositoryChanged();
            await Repository.StageAsync([_path], token);
            Close();
        });
    }

    private Task MergetoolAsync() => RunAsync("Merge tool", async token =>
    {
        await Repository.RunAsync(["mergetool", "--no-prompt", "--", _path], token);
        MarkRepositoryChanged();
        await LoadAsync(token);
    });

    protected override void UpdateActions() => _save.IsEnabled = !IsBusy && _editable;
}

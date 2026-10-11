using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The FormCommit layout and workflows, using the existing Git backend and translation catalogue.</summary>
public sealed class CommitDialog : Window
{
    private readonly GitRepository _repository;
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Control> _interactive = [];
    private readonly ListBox _unstaged = new() { Name = "Unstaged", SelectionMode = SelectionMode.Multiple };
    private readonly ListBox _staged = new() { Name = "Staged", SelectionMode = SelectionMode.Multiple };
    private readonly TextBox _filter = new() { Name = "SelectionFilter", Width = 180, Height = 24 };
    private readonly TextBox _source = new();
    private readonly DiffView _diff;
    private readonly TextBox _message = new() { Name = "Message", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _amend = new() { Name = "Amend", Height = 24, MinHeight = 24 };
    private readonly CheckBox _resetAuthor = new() { Name = "ResetAuthor", IsVisible = false, Height = 24, MinHeight = 24 };
    private readonly TextBox _author = new() { Name = "AuthorOverride", Watermark = "name <mail>", Width = 250 };
    private readonly ComboBox _sign = new() { Name = "Signing", SelectedIndex = 0, Width = 250 };
    private readonly TextBox _signingKey = new() { Name = "SigningKey", Width = 250, Watermark = "Signing key", IsEnabled = false };
    private readonly ComboBox _kind = new() { Name = "CommitKind", SelectedIndex = 0, Width = 250 };
    private readonly ComboBox _target = new() { Name = "TargetCommit", Width = 330, IsEnabled = false };
    private readonly TextBox _output = new() { Name = "CommitOutput", IsReadOnly = true, AcceptsReturn = true, IsVisible = false, Height = 72, FontFamily = new FontFamily("monospace") };
    private readonly TextBlock _state = new() { Name = "CommitStatus", TextWrapping = TextWrapping.Wrap };
    private readonly MenuItem _signOff;
    private readonly MenuItem _closeAfterCommit;
    private readonly MenuItem _closeWhenClean;
    private readonly MenuItem _selectStaged;
    private readonly Button _commit;
    private readonly Button _push;
    private readonly Button _stage;
    private readonly Button _unstage;
    private readonly Button _stageHunk;
    private readonly Button _unstageHunk;
    private readonly Button _stageLines;
    private readonly Button _unstageLines;
    private Task? _initialization;
    private GitStatus? _status;
    private GitPatchFile? _patch;
    private bool _busy;
    private bool _refreshing;
    private bool _viewStaged;
    private string? _path;
    private string _committer = "";
    private string? _loadedTemplate;
    private GitCommit? _headCommit;
    private bool _mergePending;

    public CommitDialog(GitRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        Title = C("$this", "Commit");
        Width = 1100;
        Height = 760;
        MinWidth = 860;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _diff = new DiffView(_source) { Name = "SelectedDiff" };
        _amend.Content = C("Amend", "Amend commit");
        _resetAuthor.Content = C("ResetAuthor", "Reset author");
        _sign.ItemsSource = new[] { T("Use Git configuration"), T("Do not sign"), T("Sign commit") };
        _kind.ItemsSource = new[] { T("Normal commit"), "Fixup", "Squash" };
        _sign.SelectedIndex = _kind.SelectedIndex = 0;
        _target.ItemTemplate = new FuncDataTemplate<GitCommit>((commit, _) => new TextBlock
        {
            Text = commit is null ? "" : $"{commit.Hash[..Math.Min(9, commit.Hash.Length)]} {commit.Subject}",
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        _signOff = Toggle("signOffToolStripMenuItem", "Sign-off commit");
        _closeAfterCommit = Toggle("closeDialogAfterEachCommitToolStripMenuItem", "Close dialog after each commit", true);
        _closeWhenClean = Toggle("closeDialogAfterAllFilesCommittedToolStripMenuItem", "Close dialog when all changes are committed", true);
        _selectStaged = Toggle("tsmiSelectStagedOnEnterMessage", "Select staged on entering message editor", true);
        _stage = Action("Stage", "toolStageItem", "Stage", "Stage.png", () => StageAsync(false));
        _unstage = Action("Unstage", "toolUnstageItem", "Unstage", "Unstage.png", () => UnstageAsync(false));
        _commit = Action("Commit", "Commit", "Commit", "RepoStateClean.png", () => CommitAsync(false));
        _push = Action("CommitAndPush", "_commitAndPush", "Commit & push", "ArrowUp.png", () => CommitAsync(true));
        _stageHunk = Action("StageHunk", "", "Stage hunk", "Stage.png", () => ApplyPatchAsync(false, false));
        _unstageHunk = Action("UnstageHunk", "", "Unstage hunk", "Unstage.png", () => ApplyPatchAsync(true, false));
        _stageLines = Action("StageLines", "", "Stage selected line(s)", "Stage.png", () => ApplyPatchAsync(false, true), false);
        _unstageLines = Action("UnstageLines", "", "Unstage selected line(s)", "Unstage.png", () => ApplyPatchAsync(true, true), false);
        ConfigureList(_unstaged, false);
        ConfigureList(_staged, true);
        _interactive.AddRange([_filter, _message, _amend, _resetAuthor, _author, _sign, _signingKey, _kind, _target]);
        Content = BuildLayout();
        _filter.PropertyChanged += async (_, change) =>
        {
            if (change.Property == TextBox.TextProperty && !_busy)
            {
                ApplyFileLists();
                await RunAsync(T("Diff"), LoadDiffAsync);
            }
        };
        _message.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty) { UpdateAvailability(); UpdateStatus(); }
            else if (change.Property == TextBox.CaretIndexProperty) { UpdateStatus(); }
        };
        _message.GotFocus += async (_, _) =>
        {
            if (!_busy && _selectStaged.IsChecked && _staged.ItemCount > 0)
            {
                _staged.SelectedIndex = Math.Max(0, _staged.SelectedIndex);
                if (!_viewStaged) { await SelectFileAsync(_staged, true); }
            }
        };
        _amend.IsCheckedChanged += async (_, _) =>
        {
            _resetAuthor.IsVisible = _amend.IsChecked == true;
            if (_amend.IsChecked != true) { _resetAuthor.IsChecked = false; }
            if (!_busy && _amend.IsChecked == true && string.IsNullOrWhiteSpace(_message.Text))
            {
                await RunAsync(T("Commit message"), async () =>
                {
                    GitCommit? previous = (await _repository.GetLogAsync(1, all: false, cancellationToken: _lifetime.Token)).FirstOrDefault();
                    if (previous is not null) { _message.Text = previous.Subject + (previous.Body.Length == 0 ? "" : "\n\n" + previous.Body.TrimEnd()); }
                });
            }
            UpdateAvailability();
        };
        _sign.SelectionChanged += (_, _) => UpdateAvailability();
        _kind.SelectionChanged += (_, _) => UpdateAvailability();
        _target.SelectionChanged += (_, _) => UpdateAvailability();
        _author.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateAvailability(); } };
        _diff.Editor.TextArea.SelectionChanged += (_, _) => UpdateAvailability();
        KeyDown += HandleShortcut;
        Opened += async (_, _) => { await InitializeAsync(); _unstaged.Focus(); };
        Closed += (_, _) => _lifetime.Cancel();
        UpdateAvailability();
    }

    public bool HasCommitted { get; private set; }
    public bool HasRepositoryChanges { get; private set; }
    public string? LastError { get; private set; }
    public event EventHandler? RepositoryChanged;
    internal bool IsBusy => _busy;
    public Task InitializeAsync() => _initialization ??= RunAsync(T("Refresh"), async () =>
    {
        await RefreshDataAsync();
        if (!string.IsNullOrEmpty(_message.Text)) { return; }
        foreach (string name in new[] { "MERGE_MSG", "SQUASH_MSG" })
        {
            string path = await GetGitPathAsync(name);
            if (File.Exists(path)) { _message.Text = await File.ReadAllTextAsync(path, _lifetime.Token); return; }
        }
        _loadedTemplate = await _repository.GetCommitTemplateAsync(cancellationToken: _lifetime.Token);
        _message.Text = _loadedTemplate ?? "";
    });

    public Task RefreshAsync() => RunAsync(T("Refresh"), RefreshDataAsync);
    private string T(string text) => _translations.Translate(text);
    private string C(string item, string source) => _translations.Translate("FormCommit", item, "Text", source);

    private Control BuildLayout()
    {
        Grid main = new() { ColumnDefinitions = new ColumnDefinitions("43*,5,57*"), Margin = new Thickness(6, 6, 6, 0) };
        Grid files = new() { RowDefinitions = new RowDefinitions("45*,5,55*") };
        DockPanel unstaged = Pane(_unstaged, Row(new TextBlock { Text = C("toolStripLabel1", "Selection Filter"), VerticalAlignment = VerticalAlignment.Center }, _filter,
            Action("Refresh", "", "Refresh", "ReloadRevisions.png", RefreshAsync, false)), T("Unstaged"));
        files.Children.Add(unstaged);
        files.Children.Add(Splitter(1, true));
        DockPanel staged = Pane(_staged, Row(Action("UnstageAll", "", "Unstage all", "UnstageAll.png", () => UnstageAsync(true), false), _unstage,
            _stage, Action("StageAll", "", "Stage all", "StageAll.png", () => StageAsync(true), false)), C("commitStagedCountLabel", "Staged"));
        Grid.SetRow(staged, 2);
        files.Children.Add(staged);
        main.Children.Add(files);
        main.Children.Add(Splitter(1, false));

        Grid right = new() { RowDefinitions = new RowDefinitions("68*,5,32*") };
        right.Children.Add(Pane(_diff, Row(_stageHunk, _unstageHunk, _stageLines, _unstageLines)));
        right.Children.Add(Splitter(1, true));
        Grid editor = new() { ColumnDefinitions = new ColumnDefinitions("175,5,*") };
        Button cancel = new() { Name = "Cancel", Content = C("Cancel", "Cancel"), IsCancel = true, Height = 26 };
        cancel.Click += (_, _) => Close();
        StackPanel actions = new() { Spacing = 4, Children =
        {
            _commit, _push, _amend, _resetAuthor,
            Action("ResetSoft", "ResetSoft", "Reset soft", "ResetCurrentBranchToHere.png", ResetSoftAsync),
            Action("StashStaged", "StashStaged", "Stash staged changes", "stash.png", StashStagedAsync),
            Action("ResetAll", "btnResetAllChanges", "Reset all changes", "ResetWorkingDirChanges.png", () => ResetChangesAsync(true)),
            Action("ResetUnstaged", "btnResetUnstagedChanges", "Reset unstaged changes", "ResetWorkingDirChanges.png", () => ResetChangesAsync(false)), cancel
        }};
        editor.Children.Add(actions);
        Menu options = new() { Name = "CommitToolbar", ItemsSource = new[]
        {
            new MenuItem { Header = C("commitMessageToolStripMenuItem", "Commit message"), ItemsSource = new[] { MenuAction("PreviousMessage", T("Previous commit message"), LoadPreviousMessageAsync) } },
            new MenuItem { Name = "Options", Header = C("tsmiOptions", "Options"), ItemsSource = new MenuItem[]
            {
                _closeAfterCommit, _closeWhenClean, _selectStaged, _signOff,
                InputMenu(C("toolAuthorLabelItem", "Author: (Format: \"name <mail>\")"), _author),
                InputMenu(T("Sign commit"), _sign), InputMenu(T("Signing key"), _signingKey),
                InputMenu(T("Commit kind"), _kind), InputMenu(T("Target commit"), _target)
            } },
            new MenuItem { Header = C("commitTemplatesToolStripMenuItem", "Commit templates"), ItemsSource = new[] { MenuAction("LoadTemplate", T("Load configured template"), LoadTemplateAsync) } }
        }};
        _interactive.Add(options);
        DockPanel message = new() { LastChildFill = true };
        DockPanel.SetDock(options, Dock.Top);
        message.Children.Add(options);
        DockPanel.SetDock(_output, Dock.Bottom);
        message.Children.Add(_output);
        message.Children.Add(_message);
        Grid.SetColumn(message, 2);
        editor.Children.Add(message);
        Grid.SetRow(editor, 2);
        right.Children.Add(editor);
        Grid.SetColumn(right, 2);
        main.Children.Add(right);
        DockPanel root = new();
        Border status = new() { Child = _state, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(6, 3) };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        root.Children.Add(main);
        return root;
    }

    private void ConfigureList(ListBox list, bool staged)
    {
        list.Focusable = true;
        _interactive.Add(list);
        list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(HeightProperty, 24d), new Setter(MinHeightProperty, 24d), new Setter(PaddingProperty, new Thickness(3, 0)),
                new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
            }
        });
        list.ItemTemplate = new FuncDataTemplate<GitChange>((change, _) =>
        {
            if (change is null) { return new TextBlock(); }
            char status = staged ? change.IndexStatus : change.WorkTreeStatus;
            string icon = change.IsConflict ? "FileStatusUnknown.png" : change.IsUntracked || status == 'A' ? "FileStatusAdded.png" : status == 'D' ? "FileStatusRemoved.png" : status == 'R' ? "FileStatusRenamed.png" : "FileStatusModified.png";
            Control label = IconLabel(change.OriginalPath is null ? change.Path : $"{change.OriginalPath} → {change.Path}", icon);
            ToolTip.SetTip(label, change.Path + (change.IsConflict ? " — " + T("There are unresolved merge conflicts") : ""));
            return label;
        });
        list.SelectionChanged += async (_, _) => { if (!_refreshing && !_busy) { await SelectFileAsync(list, staged); } };
        list.DoubleTapped += async (_, _) => { if (!_busy) { if (staged) { await UnstageAsync(false); } else { await StageAsync(false); } } };
    }

    private async Task RefreshDataAsync()
    {
        _status = await _repository.GetStatusAsync(_lifetime.Token);
        _mergePending = File.Exists(await GetGitPathAsync("MERGE_HEAD"));
        IReadOnlyList<KeyValuePair<string, string>> configuration = await _repository.GetConfigAsync("^user\\.(name|email)$", cancellationToken: _lifetime.Token);
        _committer = $"{configuration.LastOrDefault(item => item.Key == "user.name").Value} <{configuration.LastOrDefault(item => item.Key == "user.email").Value}>";
        string? target = (_target.SelectedItem as GitCommit)?.Hash;
        IReadOnlyList<GitCommit> history = _status.IsUnborn ? Array.Empty<GitCommit>() : await _repository.GetLogAsync(100, cancellationToken: _lifetime.Token, all: false);
        _target.ItemsSource = history;
        _headCommit = history.FirstOrDefault();
        _target.SelectedItem = history.FirstOrDefault(commit => commit.Hash == target);
        ApplyFileLists();
        await LoadDiffAsync();
    }

    private void ApplyFileLists()
    {
        if (_status is null) { return; }
        _refreshing = true;
        try
        {
            bool Matches(GitChange change) => string.IsNullOrEmpty(_filter.Text) || change.Path.Contains(_filter.Text, StringComparison.OrdinalIgnoreCase) || change.OriginalPath?.Contains(_filter.Text, StringComparison.OrdinalIgnoreCase) is true;
            _unstaged.ItemsSource = _status.Changes.Where(change => (change.IsModified || change.IsConflict) && !change.IsIgnored && Matches(change)).ToArray();
            _staged.ItemsSource = _status.Changes.Where(change => change.IsStaged && !change.IsConflict && Matches(change)).ToArray();
            ListBox current = _viewStaged ? _staged : _unstaged;
            current.SelectedItem = current.Items.OfType<GitChange>().FirstOrDefault(change => change.Path == _path) ?? current.Items.OfType<GitChange>().FirstOrDefault();
            if (current.SelectedItem is null)
            {
                _viewStaged = !_viewStaged;
                current = _viewStaged ? _staged : _unstaged;
                current.SelectedItem = current.Items.OfType<GitChange>().FirstOrDefault();
            }
            _path = (current.SelectedItem as GitChange)?.Path;
        }
        finally { _refreshing = false; }
        UpdateAvailability();
        UpdateStatus();
    }

    private Task SelectFileAsync(ListBox list, bool staged)
    {
        if (list.SelectedItem is not GitChange selected) { return Task.CompletedTask; }
        _viewStaged = staged;
        _path = selected.Path;
        return RunAsync(T("Diff"), LoadDiffAsync);
    }

    private async Task LoadDiffAsync()
    {
        GitChange? change = _status?.Changes.FirstOrDefault(change => change.Path == _path);
        if (change is null) { _source.Text = T("Select a file to view its changes."); _patch = null; return; }
        if (change.IsUntracked)
        {
            await using FileStream stream = File.OpenRead(Path.Combine(_repository.Path, change.Path));
            byte[] bytes = new byte[(int)Math.Min(2_000_000, stream.Length)];
            await stream.ReadExactlyAsync(bytes, _lifetime.Token);
            _source.Text = bytes.Take(8192).Contains((byte)0) ? T("Binary file") + $" ({stream.Length:N0} bytes)" : System.Text.Encoding.UTF8.GetString(bytes) + (stream.Length > bytes.Length ? "\n[Preview limited to 2 MB]" : "");
        }
        else if (change.OriginalPath is not null)
        {
            List<string> arguments = ["diff", "--no-ext-diff", "--no-textconv"];
            if (_viewStaged) { arguments.Add("--cached"); }
            arguments.AddRange(["--", change.OriginalPath, change.Path]);
            _source.Text = (await _repository.RunAsync(arguments, _lifetime.Token)).StandardOutput;
        }
        else { _source.Text = await _repository.GetDiffAsync(change.Path, _viewStaged, cancellationToken: _lifetime.Token); }
        _patch = GitPatch.Parse(_source.Text ?? "").FirstOrDefault();
    }

    private static string[] FilePaths(IEnumerable<GitChange> changes)
        => changes.SelectMany(change => change.OriginalPath is null ? new[] { change.Path } : new[] { change.Path, change.OriginalPath })
            .Distinct(StringComparer.Ordinal).ToArray();
    private static string[] SelectedPaths(ListBox list) => FilePaths(list.SelectedItems?.OfType<GitChange>() ?? []);
    private Task StageAsync(bool all)
    {
        IEnumerable<GitChange> changes = all ? _unstaged.Items.OfType<GitChange>() : _unstaged.SelectedItems?.OfType<GitChange>() ?? [];
        // A staged rename has already removed its old path from the index.
        // Stage the current name, as FormCommit.StageFiles does.
        string[] paths = changes.Select(change => change.Path).Distinct(StringComparer.Ordinal).ToArray();
        return paths.Length == 0 ? Task.CompletedTask : MutateAsync(T("Stage"), () => _repository.StageAsync(paths, _lifetime.Token));
    }
    private Task UnstageAsync(bool all)
    {
        string[] paths = all ? FilePaths(_staged.Items.OfType<GitChange>()) : SelectedPaths(_staged);
        return paths.Length == 0 ? Task.CompletedTask : MutateAsync(T("Unstage"), () => _repository.UnstageAsync(paths, _lifetime.Token));
    }

    private Task ApplyPatchAsync(bool reverse, bool lines) => MutateAsync(reverse ? T("Unstage") : T("Stage"), async () =>
    {
        if (_patch is null || _viewStaged != reverse) { throw new InvalidOperationException(T("Select a changed file in the correct list first.")); }
        string? patch;
        if (lines)
        {
            if (_diff.SelectionLength == 0) { throw new InvalidOperationException(T("Select changed lines in the diff first.")); }
            GitChange change = _status!.Changes.Single(change => change.Path == _path);
            patch = GitPatch.SelectLines(_diff.Text, _diff.SelectionStart, _diff.SelectionLength, staged: reverse, reverse: reverse, isNewFile: change.IndexStatus == 'A', isRenamed: change.OriginalPath is not null);
        }
        else
        {
            int offset = _patch.Header.Length;
            List<int> indices = [];
            int start = _diff.SelectionLength > 0 ? _diff.SelectionStart : _diff.CaretOffset;
            int end = start + Math.Max(1, _diff.SelectionLength);
            foreach (GitPatchHunk hunk in _patch.Hunks)
            {
                if (start < offset + hunk.Text.Length && end > offset) { indices.Add(hunk.Index); }
                offset += hunk.Text.Length;
            }
            if (indices.Count == 0) { throw new InvalidOperationException(T("Place the cursor in a diff hunk first.")); }
            patch = _patch.SelectHunks(indices);
        }
        if (patch is null) { throw new InvalidOperationException(T("The selected text contains no changed lines.")); }
        return await _repository.ApplyPatchAsync(patch, staged: true, reverse: reverse, cancellationToken: _lifetime.Token);
    });

    private async Task CommitAsync(bool push)
    {
        if (_busy) { return; }
        if (_amend.IsChecked == true && !await Dialogs.ConfirmAsync(this, C("_amendCommitCaption", "Amend commit"), C("_amendCommit", "You are about to rewrite history.\nOnly use Amend if the commit has not been published yet!\n\nDo you want to continue?"))) { return; }
        await RunAsync(C("Commit", "Commit"), async () =>
        {
            GitCommit? target = _target.SelectedItem as GitCommit;
            GitCommitOptions options = new(
                Message: _kind.SelectedIndex == 1 ? null : _message.Text,
                Amend: _amend.IsChecked == true,
                SignOff: _signOff.IsChecked,
                Sign: _sign.SelectedIndex == 0 ? null : _sign.SelectedIndex == 2,
                SigningKey: _sign.SelectedIndex == 2 ? EmptyToNull(_signingKey.Text) : null,
                FixupRevision: _kind.SelectedIndex == 1 ? target?.Hash : null,
                SquashRevision: _kind.SelectedIndex == 2 ? target?.Hash : null,
                ResetAuthor: _amend.IsChecked == true && _resetAuthor.IsChecked == true,
                Author: EmptyToNull(_author.Text));
            GitResult result = await _repository.CommitAsync(options, _lifetime.Token);
            HasCommitted = true;
            HasRepositoryChanges = true;
            _output.Text = result.StandardOutput + result.StandardError;
            _output.IsVisible = false;
            _message.Text = "";
            _amend.IsChecked = false;
            _kind.SelectedIndex = 0;
            await RefreshDataAsync();
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
            if (push) { await _repository.PushAsync(cancellationToken: _lifetime.Token); }
            if (_closeAfterCommit.IsChecked || (_closeWhenClean.IsChecked && _status!.Changes.Count == 0)) { Close(); }
        });
    }

    private Task LoadTemplateAsync() => RunAsync(T("Commit templates"), async () =>
    {
        string? template = await _repository.GetCommitTemplateAsync(cancellationToken: _lifetime.Token);
        if (template is null) { throw new InvalidOperationException(T("No commit template is configured.")); }
        _loadedTemplate = template;
        _message.Text = template;
    });
    private Task LoadPreviousMessageAsync() => RunAsync(T("Commit message"), async () =>
    {
        GitCommit? commit = (await _repository.GetLogAsync(1, cancellationToken: _lifetime.Token, all: false)).FirstOrDefault();
        if (commit is not null) { _message.Text = commit.Subject + (commit.Body.Length == 0 ? "" : "\n\n" + commit.Body.TrimEnd()); }
    });
    private async Task ResetSoftAsync()
    {
        if (!await Dialogs.ConfirmAsync(this, C("ResetSoft", "Reset soft"), C("_amendResetSoft", "You are about to rewrite history by Soft Reset to the previous commit.\nOnly use Amend / Reset if the commit has not been published yet!\n\nDo you want to continue?"))) { return; }
        await MutateAsync(C("ResetSoft", "Reset soft"), () => _repository.ResetAsync("HEAD^", GitResetMode.Soft, _lifetime.Token));
    }
    private Task StashStagedAsync() => MutateAsync(C("StashStaged", "Stash staged changes"), () => _repository.RunAsync(["stash", "push", "--staged"], _lifetime.Token));
    private async Task ResetChangesAsync(bool all)
    {
        string title = all ? C("btnResetAllChanges", "Reset all changes") : C("btnResetUnstagedChanges", "Reset unstaged changes");
        if (!await Dialogs.ConfirmAsync(this, title, T("Discard tracked changes? Untracked files are preserved."))) { return; }
        await MutateAsync(title, () => _repository.RunAsync(all ? ["restore", "--staged", "--worktree", "--source=HEAD", "--", "."] : ["restore", "--worktree", "--", "."], _lifetime.Token));
    }

    private Task MutateAsync(string label, Func<Task<GitResult>> action) => RunAsync(label, async () =>
    {
        await action();
        HasRepositoryChanges = true;
        await RefreshDataAsync();
        RepositoryChanged?.Invoke(this, EventArgs.Empty);
    });
    private async Task RunAsync(string label, Func<Task> action)
    {
        if (_busy) { return; }
        _busy = true;
        LastError = null;
        _output.IsVisible = false;
        _state.Text = label + "…";
        UpdateAvailability();
        try { await action(); }
        catch (OperationCanceledException) { _state.Text = T("Operation cancelled"); }
        catch (Exception exception)
        {
            LastError = exception is GitCommandException command
                ? !string.IsNullOrWhiteSpace(command.Result.StandardError) ? command.Result.StandardError
                    : !string.IsNullOrWhiteSpace(command.Result.StandardOutput) ? command.Result.StandardOutput : exception.Message
                : exception.Message;
            _output.Text = LastError;
            _output.IsVisible = true;
            _state.Text = label + ": " + LastError.Trim();
        }
        finally { _busy = false; UpdateAvailability(); if (LastError is null) { UpdateStatus(); } }
    }

    private void UpdateAvailability()
    {
        foreach (Control control in _interactive) { control.IsEnabled = !_busy; }
        if (_busy) { return; }
        _stage.IsEnabled = _unstaged.SelectedItems?.Count > 0;
        _unstage.IsEnabled = _staged.SelectedItems?.Count > 0;
        _target.IsEnabled = _kind.SelectedIndex != 0;
        _signingKey.IsEnabled = _sign.SelectedIndex == 2;
        _message.IsEnabled = _kind.SelectedIndex != 1;
        _amend.IsEnabled = _kind.SelectedIndex == 0 && _status is not null && !_status.IsUnborn;
        if (!_amend.IsEnabled && _amend.IsChecked == true) { _amend.IsChecked = false; }
        _resetAuthor.IsEnabled = _amend.IsChecked == true && string.IsNullOrWhiteSpace(_author.Text);
        if (!_resetAuthor.IsEnabled) { _resetAuthor.IsChecked = false; }
        Button? soft = _interactive.OfType<Button>().FirstOrDefault(button => button.Name == "ResetSoft");
        if (soft is not null) { soft.IsVisible = _amend.IsChecked == true; soft.IsEnabled = _headCommit?.Parents.Count > 0; }
        bool targetValid = _kind.SelectedIndex == 0 || _target.SelectedItem is GitCommit;
        bool messageValid = _kind.SelectedIndex == 1 || (!string.IsNullOrWhiteSpace(_message.Text) && _message.Text != _loadedTemplate);
        bool committable = _status is not null && !_status.Changes.Any(change => change.IsConflict) && (_status.Changes.Any(change => change.IsStaged) || _amend.IsChecked == true || _mergePending);
        _commit.IsEnabled = _push.IsEnabled = targetValid && messageValid && committable;
        _stageHunk.IsEnabled = !_viewStaged && _patch?.Hunks.Count > 0;
        _unstageHunk.IsEnabled = _viewStaged && _patch?.Hunks.Count > 0;
        _stageLines.IsEnabled = _stageHunk.IsEnabled && _diff.SelectionLength > 0;
        _unstageLines.IsEnabled = _unstageHunk.IsEnabled && _diff.SelectionLength > 0;
    }
    private void UpdateStatus()
    {
        if (_busy || LastError is not null) { return; }
        string text = _message.Text ?? "";
        int caret = Math.Clamp(_message.CaretIndex, 0, text.Length);
        int line = text[..caret].Count(character => character == '\n') + 1;
        int column = caret - text[..caret].LastIndexOf('\n');
        _state.Text = $"{_committer}    ·    {_status?.Branch ?? ""}    ·    {C("commitStagedCountLabel", "Staged")}: {_status?.Changes.Count(change => change.IsStaged && !change.IsConflict) ?? 0}    ·    {C("commitCursorLineLabel", "Ln")} {line}    {C("commitCursorColumnLabel", "Col")} {column}";
    }
    private async void HandleShortcut(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape) { Close(); args.Handled = true; return; }
        if (_busy) { return; }
        if (args.KeyModifiers == KeyModifiers.Control)
        {
            switch (args.Key)
            {
                case Key.D1: _unstaged.Focus(); break;
                case Key.D2: _diff.Editor.TextArea.Focus(); break;
                case Key.D3: _staged.Focus(); break;
                case Key.D4: _message.Focus(); break;
                case Key.S: await StageAsync(true); break;
                case Key.Enter: if (_commit.IsEnabled) { await CommitAsync(false); } break;
                default: return;
            }
            args.Handled = true;
        }
        else if (args.Key == Key.F5) { args.Handled = true; await RefreshAsync(); }
    }

    private static string? EmptyToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
    private async Task<string> GetGitPathAsync(string name)
    {
        GitResult result = await _repository.RunAsync(["rev-parse", "--git-path", name], _lifetime.Token);
        return Path.GetFullPath(result.StandardOutput.TrimEnd('\r', '\n'), _repository.Path);
    }
    private MenuItem Toggle(string item, string text, bool value = false) => new() { Name = item, Header = C(item, text), ToggleType = MenuItemToggleType.CheckBox, IsChecked = value, StaysOpenOnClick = true };
    private MenuItem MenuAction(string name, string text, Func<Task> action)
    {
        MenuItem item = new() { Name = name, Header = text };
        item.Click += async (_, _) => { if (!_busy) { await action(); } };
        return item;
    }
    private static MenuItem InputMenu(string text, Control input) => new() { Header = new StackPanel { Spacing = 3, Children = { new TextBlock { Text = text }, input } }, StaysOpenOnClick = true };
    private Button Action(string name, string item, string text, string icon, Func<Task> action, bool showText = true)
    {
        string label = item.Length == 0 ? T(text) : C(item, text);
        Button button = new() { Name = name, Height = 26, Content = IconLabel(showText ? label : "", icon), HorizontalContentAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(button, label);
        button.Click += async (_, _) => { if (!_busy) { await action(); } };
        _interactive.Add(button);
        return button;
    }
    private static Control IconLabel(string label, string name)
    {
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Uri uri = new($"avares://GitExtensions/Assets/Icons/{name}");
        if (AssetLoader.Exists(uri)) { using Stream stream = AssetLoader.Open(uri); content.Children.Add(new Image { Source = new Bitmap(stream), Width = 16, Height = 16 }); }
        if (label.Length > 0)
        {
            TextBlock text = new() { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            content.Children.Add(text);
        }
        return content;
    }
    private static WrapPanel Row(params Control[] controls)
    {
        WrapPanel row = new() { Orientation = Orientation.Horizontal, ItemSpacing = 3, LineSpacing = 2, Margin = new Thickness(0, 0, 0, 2) };
        foreach (Control control in controls) { row.Children.Add(control); }
        return row;
    }
    private DockPanel Pane(Control body, Control toolbar, string? title = null)
    {
        DockPanel pane = new() { LastChildFill = true };
        DockPanel.SetDock(toolbar, Dock.Top);
        pane.Children.Add(toolbar);
        if (title is not null)
        {
            TextBlock heading = new() { Text = title, Padding = new Thickness(3, 2) };
            heading.Bind(TextBlock.BackgroundProperty, new DynamicResourceExtension("ClassicMenuBrush"));
            DockPanel.SetDock(heading, Dock.Top);
            pane.Children.Add(heading);
        }
        pane.Children.Add(body);
        return pane;
    }
    private static GridSplitter Splitter(int position, bool rows)
    {
        GridSplitter splitter = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = rows ? GridResizeDirection.Rows : GridResizeDirection.Columns };
        if (rows) { splitter.Height = 5; Grid.SetRow(splitter, position); }
        else { splitter.Width = 5; Grid.SetColumn(splitter, position); }
        return splitter;
    }
}

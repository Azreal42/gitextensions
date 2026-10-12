using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original FormStash: worktree/stash selector and files on the left, viewer on the right.</summary>
public sealed class StashDialog : RepositoryDialog
{
    private sealed record Choice(GitStash? Stash, string Caption);
    private sealed record FileChoice(string Path, bool Staged = false, GitChange? Change = null,
        GitRevisionChanges? Revisions = null, GitRevisionFile? File = null);
    private readonly bool _manage;
    private readonly GitStash? _initial;
    private readonly ComboBox _selector = ReferenceDialogControls.Choice("StashSelection");
    private readonly TreeView _files = new() { Name = "StashFiles", SelectionMode = SelectionMode.Multiple };
    private readonly TextBox _message = new() { Name = "StashMessage", AcceptsReturn = true, Height = 80, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _keepIndex = new() { Name = "StashKeepIndex" };
    private readonly CheckBox _untracked = new() { Name = "StashIncludeUntracked" };
    private readonly TextBox _diffSource = new();
    private readonly FileStatusListView _fileList;
    private readonly FileContentView _content;
    private readonly StashPreferencesStore _store = new();
    private readonly Button _saveAll, _saveSelected, _drop, _apply, _resolve;
    private readonly MenuItem _pickChanges = new() { Name = "StashCherryPickChanges" };
    private readonly MenuItem _resetFiles = new() { Name = "StashResetFiles" };
    private readonly MenuItem _resetToStash = new() { Name = "StashResetToSelected" };
    private readonly MenuItem _resetToParent = new() { Name = "StashResetToParent", InputGesture = new(Key.R) };
    private GitStatus? _status;
    private string? _stashParent;
    private bool _updating;
    private bool _initialized;

    public StashDialog(GitRepository repository, bool manage = true, GitStash? initialStash = null) : base(repository, "Stash")
    {
        _manage = manage; _initial = initialStash;
        Width = 980; Height = 600; MinWidth = 720; MinHeight = 480;
        _selector.ItemTemplate = new FuncDataTemplate<Choice>((choice, _) => new TextBlock { Text = choice?.Caption, TextTrimming = TextTrimming.CharacterEllipsis });
        _keepIndex.Content = Caption("StashKeepIndex", "&Keep index");
        _untracked.Content = Caption("chkIncludeUntrackedFiles", "&Include untracked files");
        Tip(_keepIndex, "StashKeepIndex", "All changes already added to the index are left intact");
        Tip(_untracked, "chkIncludeUntrackedFiles", "All untracked files are also stashed and then cleaned");
        Tip(_selector, "Stashes", "Select a stash", "ToolTipText");
        _message.BorderThickness = default;
        _message.Bind(TextBox.BackgroundProperty, new DynamicResourceExtension("ClassicTooltipBrush"));
        _message.Bind(TextBox.ForegroundProperty, new DynamicResourceExtension("ClassicTooltipTextBrush"));
        ReferenceDialogControls.Compact(_keepIndex, _untracked);
        StashPreferences preferences = _store.Load(); _keepIndex.IsChecked = preferences.KeepIndex; _untracked.IsChecked = preferences.IncludeUntracked;
        _saveAll = StashAction("StashAll", "Stash", "S&tash all changes", () => SaveAsync(false));
        _saveSelected = StashAction("StashSelected", "StashSelectedFiles", "Stash &selected changes", () => SaveAsync(true));
        _drop = StashAction("DropStash", "Clear", "&Drop Selected Stash", DropAsync);
        _apply = StashAction("ApplyStash", "Apply", "&Apply Selected Stash", ApplyAsync);
        Tip(_saveAll, "Stash", "Save local changes to a new stash, then revert local changes");
        Tip(_saveSelected, "StashSelectedFiles", "Stash changes for the selected files, then revert them to the original state");
        Tip(_drop, "Clear", "Remove the selected stash from the list");
        Tip(_apply, "Apply", "Apply the selected stash on top of the current working directory state");
        _resolve = Action("ResolveStashConflict", "Solve conflicts…", ResolveAsync); _resolve.Height = _resolve.MinHeight = 25;
        _fileList = new(_files); _content = new(_diffSource); _content.Name = "StashDiff";
        ConfigureFileMenu();
        _content.TextView.ComparisonOptionsChanged += (_, _) => ReloadDiff();
        _content.TextView.DisplayOptionsChanged += (_, _) => ReloadDiff();
        _content.TextView.FileEncodingChanged += (_, _) => ReloadDiff();
        _content.TextView.Editor.TextArea.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Handled || args.Key != Key.Escape || args.KeyModifiers != KeyModifiers.None
                || !ReferenceEquals(args.Source, _content.TextView.Editor.TextArea) || !_content.TextView.Editor.TextArea.IsFocused
                || _content.TextView.ContextMenu?.IsOpen == true) { return; }
            args.Handled = true;
            if (!_content.TextView.Editor.TextArea.Selection.IsEmpty) { _content.TextView.Editor.TextArea.ClearSelection(); }
            else { Close(); }
        }, RoutingStrategies.Tunnel);
        Grid show = new() { ColumnDefinitions = new("Auto,*"), Height = 28, Margin = new Thickness(4, 0) };
        show.Children.Add(new AccessText { Text = Translations.TranslateAccessKeys("FormStash", "showToolStripLabel", "Text", "S&how:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
        Grid.SetColumn(_selector, 1); show.Children.Add(_selector);
        Grid options = new() { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(4, 0) };
        _keepIndex.Margin = new Thickness(0, 0, 12, 0); _untracked.HorizontalAlignment = HorizontalAlignment.Right;
        options.Children.Add(_keepIndex); Grid.SetColumn(_untracked, 1); options.Children.Add(_untracked);
        StackPanel actions = new() { Spacing = 5, Margin = new Thickness(5), Children = { options, _saveAll, _saveSelected, _drop, _apply, _resolve } };
        DockPanel left = new() { LastChildFill = true };
        DockPanel.SetDock(show, Dock.Top); left.Children.Add(show);
        DockPanel.SetDock(actions, Dock.Bottom); left.Children.Add(actions);
        StackPanel message = new() { Children = { new AccessText { Text = Translations.TranslateAccessKeys("FormStash", "messageLabel", "Text", "&Message:"), Margin = new Thickness(4, 0) }, _message } };
        DockPanel.SetDock(message, Dock.Bottom); left.Children.Add(message); left.Children.Add(_fileList);
        Grid split = new() { Name = "StashSplit", ColumnDefinitions = new("330,5,*") };
        split.Children.Add(ClassicChrome.Border(left, new Thickness(0, 0, 1, 0)));
        GridSplitter divider = new() { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetColumn(divider, 1); split.Children.Add(divider);
        Grid.SetColumn(_content, 2); split.Children.Add(_content); Root.Children.Add(split);
        // Keep the immutable file list enabled while reading its diff. Disabling
        // it inside PointerPressed removes the clicked item's keyboard focus.
        // IsBusy still guards every command; changed selections queue a reload.
        Track(_selector, _message, _keepIndex, _untracked);
        _selector.SelectionChanged += async (_, _) => { if (!_updating) { await LoadSelectionAsync(); } };
        _files.SelectionChanged += (_, _) => { if (!_updating && !_fileList.IsUpdating) { ReloadDiff(); } UpdateActions(); };
        _fileList.DisplayChanged += (_, _) => { if (!_updating) { ReloadDiff(); } UpdateActions(); };
        Closed += (_, _) => { _fileList.Dispose(); _content.Dispose(); };
        Closing += (_, _) =>
        {
            try { _store.Save(_store.Load() with { KeepIndex = _keepIndex.IsChecked == true, IncludeUntracked = _untracked.IsChecked == true }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = ex.Message; }
        };
        AddHandler(KeyDownEvent, async (_, args) =>
        {
            if (args.Handled) { return; }
            if (args.Key == Key.Escape && args.KeyModifiers == KeyModifiers.None)
            {
                if (_selector.IsDropDownOpen) { _selector.IsDropDownOpen = false; args.Handled = true; return; }
                if (args.Source is TextBox text && text.SelectionStart != text.SelectionEnd) { text.SelectionEnd = text.SelectionStart; args.Handled = true; return; }
            }
            if (IsBusy) { return; }
            if (args.Key == Key.R && args.KeyModifiers == KeyModifiers.None && _files.IsKeyboardFocusWithin && Selected is not null)
            { args.Handled = true; await ResetSelectedFilesAsync(true); return; }
            if (_content.TextView.HandleReadingShortcut(args)) { return; }
            if (args.Key == Key.F5 && args.KeyModifiers == KeyModifiers.None) { args.Handled = true; await InitializeAsync(); }
            else if (args.KeyModifiers == KeyModifiers.Control && args.Key is Key.N or Key.P)
            {
                args.Handled = true;
                int index = _selector.SelectedIndex + (args.Key == Key.N ? -1 : 1);
                if (index >= 0 && index < _selector.ItemCount) { _selector.SelectedIndex = index; }
            }
        }, RoutingStrategies.Tunnel);
        UpdateActions();
    }

    private AccessText Caption(string key, string text) => new() { Text = Translations.TranslateAccessKeys("FormStash", key, "Text", text) };
    private void Tip(Control control, string key, string text, string property = "toolTip")
        => ToolTip.SetTip(control, Translations.Translate("FormStash", key, property, text));
    private Button StashAction(string name, string key, string label, Func<Task> action)
    {
        Button button = Action(name, label, action); button.Content = Caption(key, label); button.Height = button.MinHeight = 25; return button;
    }
    private GitStash? Selected => (_selector.SelectedItem as Choice)?.Stash;
    private bool IsWorktree => _selector.SelectedIndex == 0;
    private void ReloadDiff() => _ = RequestDiffReloadAsync(() => RunAsync("Diff", LoadDiffAsync));

    public override Task InitializeAsync() => RunAsync("Stash", async token =>
    {
        if (!_initialized) { await _content.TextView.ConfigureRepositoryAsync(Repository, token); }
        await RefreshAsync(token, !_initialized && _manage, !_initialized ? _initial?.Hash : Selected?.Hash);
        _initialized = true;
    });

    private async Task RefreshAsync(CancellationToken token, bool manage = false, string? selectHash = null, int? selectIndex = null)
    {
        _status = await Repository.GetStatusAsync(token);
        IReadOnlyList<GitStash> stashes = await Repository.GetStashesAsync(token);
        Choice[] choices = [new(null, T("Current working directory changes")), ..stashes.Select(stash => new Choice(stash, stash.Reference + "  " + stash.Subject))];
        _updating = true;
        try
        {
            _selector.ItemsSource = choices;
            _selector.SelectedIndex = selectHash is not null ? Math.Max(0, Array.FindIndex(choices, choice => choice.Stash?.Hash == selectHash))
                : selectIndex is int index ? Math.Clamp(index, 0, choices.Length - 1) : manage && stashes.Count > 0 ? 1 : 0;
        }
        finally { _updating = false; }
        await ReadSelectionAsync(token);
        Status.Text = stashes.Count == 0 ? T("There are no stashes.") : T("Stash");
    }

    private Task LoadSelectionAsync() => RunAsync("Stash", ReadSelectionAsync);

    private async Task ReadSelectionAsync(CancellationToken token)
    {
        _updating = true;
        try
        {
            List<FileStatusListGroup> groups = [];
            if (IsWorktree)
            {
                _stashParent = null;
                _status = await Repository.GetStatusAsync(token); _message.Text = "";
                foreach (bool staged in new[] { true, false })
                {
                    string key = staged ? "Index" : "Workspace";
                    groups.Add(new(key, key, new TextBlock { Text = T(key) }, _status.Changes
                        .Where(change => staged ? change.IsStaged : change.IsModified || change.IsConflict)
                        .Select(change => new FileStatusListEntry(new FileChoice(change.Path, staged, change), change.Path, change.OriginalPath,
                            change.IsConflict ? 'U' : change.IsUntracked ? 'A' : staged ? change.IndexStatus : change.WorkTreeStatus, change.SimilarityPercent)).ToArray()));
                }
            }
            else if (Selected is GitStash stash)
            {
                GitStashChanges changes = await Repository.GetStashChangesAsync(stash, token); _message.Text = changes.Message.TrimEnd('\n');
                _stashParent = changes.Tracked.BaseRevision;
                void Add(GitRevisionChanges revision, string key)
                    => groups.Add(new(key, key, new TextBlock { Text = T(key) }, revision.Files.Select(file => new FileStatusListEntry(
                        new FileChoice(file.Path, Revisions: revision, File: file), file.Path, file.OriginalPath, file.Status, file.SimilarityPercent)).ToArray()));
                Add(changes.Tracked, stash.Reference);
                if (changes.Untracked is not null) { Add(changes.Untracked, "Untracked files"); }
            }
            _fileList.SetGroups(groups, preserveSelection: false);
        }
        finally { _updating = false; }
        await LoadDiffAsync(token);
    }

    private async Task LoadDiffAsync(CancellationToken token)
    {
        _content.ClearImage();
        FileChoice? file = _fileList.SelectedTags.OfType<FileChoice>().FirstOrDefault();
        if (file is null) { _diffSource.Text = ""; return; }
        DiffView viewer = _content.TextView;
        viewer.Mode = DiffViewMode.Auto;
        viewer.DocumentIdentity = Repository.Path + "\0" + (file.Revisions?.Revision ?? "worktree") + "\0" + file.Staged + "\0" + file.Path;
        viewer.SyntaxPath = file.Path; viewer.BeginDocumentLoad();
        if (file.Change is { IsUntracked: true })
        {
            await using FileStream stream = File.OpenRead(Path.Combine(Repository.Path, file.Path));
            byte[] bytes = new byte[(int)Math.Min(2_000_000, stream.Length)]; await stream.ReadExactlyAsync(bytes, token);
            _content.ShowFile(file.Path, bytes, await Repository.GetFileEncodingNameAsync(token), stream.Length > bytes.Length); return;
        }
        if (file.Change is GitChange change && await _content.ShowChangedImageAsync(Repository, change, file.Staged, token)) { return; }
        if (file.Revisions is not null && file.File is not null && !file.File.IsDeleted && FileContentView.IsImagePath(file.Path)
            && _content.TryShowImage(file.Path, await Repository.GetFileBytesAsync(file.Path, file.Revisions.Revision, token))) { return; }
        GitDiffDocument document = file.Revisions is not null && file.File is not null
            ? await Repository.GetRevisionFileDiffDocumentAsync(file.Revisions, file.File, viewer.ComparisonMenu.Options, viewer.DisplayOptions, token)
            : await Repository.GetDiffDocumentAsync(file.Path, file.Staged, options: viewer.ComparisonMenu.Options, displayOptions: viewer.DisplayOptions, originalPath: file.Change?.OriginalPath, cancellationToken: token);
        viewer.ShowDocument(document);
    }

    private async Task SaveAsync(bool selected)
    {
        if (!IsWorktree) { return; }
        string[]? paths = selected ? _fileList.SelectedTags.OfType<FileChoice>().Select(file => file.Path).Distinct(StringComparer.Ordinal).ToArray() : null;
        if (paths is { Length: 0 }) { return; }
        GitStashSaveOptions options = new(_message.Text, _untracked.IsChecked == true, _keepIndex.IsChecked == true, paths);
        await MutateAsync("Stash", token => Repository.StashAsync(options, token));
    }

    private Task ApplyAsync() => Selected is GitStash stash ? MutateAsync("Apply stash", token => Repository.ApplyStashAsync(stash, cancellationToken: token)) : Task.CompletedTask;

    private GitRevisionFile[] SelectedStashFiles => _fileList.SelectedTags.OfType<FileChoice>()
        .Where(choice => choice.File is not null).Select(choice => choice.File!).Distinct().ToArray();

    private void ConfigureFileMenu()
    {
        AccessText Caption(string key, string source) => new() { Text = Translations.TranslateAccessKeys("FileStatusList", key, "Text", source) };
        _pickChanges.Header = Caption("tsmiCherryPickChanges", "Cherr&y pick changes");
        _pickChanges.Icon = _fileList.CreateIcon("CherryPick.png");
        _resetFiles.Header = Caption("tsmiResetFileTo", "&Reset file(s) to");
        _resetFiles.Icon = _fileList.CreateIcon("ResetWorkingDirChanges.png");
        _resetFiles.ItemsSource = new[] { _resetToStash, _resetToParent };
        _resetToParent.FontWeight = Avalonia.Media.FontWeight.Bold;
        _pickChanges.Click += async (_, _) => await PickDisplayedChangesAsync();
        _resetToStash.Click += async (_, _) => await ResetSelectedFilesAsync(false);
        _resetToParent.Click += async (_, _) => await ResetSelectedFilesAsync(true);
        ContextMenu menu = new() { ItemsSource = new[] { _resetFiles, _pickChanges } };
        DiffViewerPopupLayout.Configure(menu);
        menu.Opening += (_, _) => RefreshFileMenu();
        foreach (MenuItem item in new[] { _pickChanges, _resetFiles, _resetToStash, _resetToParent })
        {
            item.AddHandler(KeyDownEvent, async (_, e) =>
            {
                if (e.Handled || IsBusy || e.Key != Key.R || e.KeyModifiers != KeyModifiers.None) { return; }
                e.Handled = true; menu.Close(); await ResetSelectedFilesAsync(true);
            }, RoutingStrategies.Tunnel);
        }
        _files.ContextMenu = menu;
    }

    private void RefreshFileMenu()
    {
        bool available = !IsBusy && !Repository.IsBare && Selected is not null && SelectedStashFiles.Length > 0;
        _resetFiles.IsVisible = _pickChanges.IsVisible = Selected is not null;
        _resetFiles.IsEnabled = _resetToStash.IsEnabled = _resetToParent.IsEnabled = available;
        // Windows applies the displayed file's entire patch, not all files in a
        // multi-selection. Binary/image and source-only previews cannot patch.
        _pickChanges.IsEnabled = available && _content.TextView.CanStagePatch && !_content.IsImage;
        _resetToStash.Header = T("Second: B ") + (Selected?.Hash[..8] ?? "");
        _resetToParent.Header = T("First: A ") + (_stashParent?[..8] ?? "");
    }

    private async Task PickDisplayedChangesAsync()
    {
        _files.ContextMenu?.Close();
        if (IsBusy || Repository.IsBare || Selected is not GitStash stash || !_content.TextView.CanStagePatch || _content.IsImage) { return; }
        GitRevisionFile? file = _fileList.SelectedTags.OfType<FileChoice>().FirstOrDefault()?.File;
        if (file is not null) { await MutateAsync("Cherry pick changes", token => Repository.ApplyStashChangesAsync(stash, [file], token), preserveStashView: true); }
    }

    private async Task ResetSelectedFilesAsync(bool parent)
    {
        _files.ContextMenu?.Close();
        if (IsBusy || Repository.IsBare || Selected is not GitStash stash) { return; }
        GitRevisionFile[] files = SelectedStashFiles;
        if (files.Length == 0) { return; }
        bool hasExisting = files.Any(file => file.Status != 'A');
        bool hasNew = files.Any(file => file.Status is not ('M' or 'T') && new[] { file.Path, file.OriginalPath }
            .Where(path => path is not null).Any(path => File.Exists(Path.Combine(Repository.Path, path!)) || Directory.Exists(Path.Combine(Repository.Path, path!))));
        string description = parent ? T("First: A ") + _stashParent : T("Second: B ") + stash.Hash;
        ResetChangesDialog confirmation = new(hasExisting, hasNew, string.Format(T("Are you sure you want to reset all selected files to {0}?"), description))
        { RequestedThemeVariant = ActualThemeVariant };
        ResetChangesDialog.Decision decision = await confirmation.ShowDialog<ResetChangesDialog.Decision>(this);
        if (decision == ResetChangesDialog.Decision.Cancel) { return; }
        await MutateAsync("Reset changes", token => Repository.RestoreStashFilesAsync(stash, files, parent,
            decision == ResetChangesDialog.Decision.ResetAndDelete, token), preserveStashView: true);
    }

    private async Task DropAsync()
    {
        if (Selected is not GitStash stash) { return; }
        StashPreferences preferences = _store.Load();
        if (!preferences.DontConfirmDrop)
        {
            StashDropConfirmation confirmation = new(stash, Translations);
            bool accepted = await confirmation.ShowDialog<bool>(this);
            if (confirmation.DontShowAgain) { _store.Save(_store.Load() with { DontConfirmDrop = true }); }
            if (!accepted) { return; }
        }
        int index = _selector.SelectedIndex;
        await MutateAsync("Drop stash", token => Repository.DropStashAsync(stash, token), index);
    }

    private async Task MutateAsync(string label, Func<CancellationToken, Task<GitResult>> action, int? index = null, bool preserveStashView = false)
    {
        string? selectedHash = preserveStashView ? Selected?.Hash : null;
        await RunAsync(label, async token =>
        {
            string? output = null;
            try { MarkRepositoryChanged(); GitResult result = await action(token); output = result.StandardOutput + result.StandardError; }
            finally
            {
                // Git can modify index/worktree even when apply/pop fails with conflicts.
                if (!token.IsCancellationRequested)
                {
                    // FormStash keeps the immutable stash and its file selection
                    // after a context action. Only the worktree state has changed.
                    if (selectedHash is not null && Selected?.Hash == selectedHash)
                    { _status = await Repository.GetStatusAsync(token); }
                    else { await RefreshAsync(token, selectIndex: index); }
                }
            }
            if (output is not null) { Status.Text = output.Trim(); }
        });
    }

    private async Task ResolveAsync()
    {
        string? path = _fileList.SelectedTags.OfType<FileChoice>().FirstOrDefault(file => file.Change?.IsConflict == true)?.Path
            ?? _status?.Changes.FirstOrDefault(change => change.IsConflict)?.Path;
        if (path is null) { return; }
        PatchConflictDialog dialog = new(Repository, path); dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        await dialog.ShowDialog(this); if (dialog.HasRepositoryChanges) { MarkRepositoryChanged(); } await InitializeAsync();
    }

    protected override void UpdateActions()
    {
        bool worktree = IsWorktree && !Repository.IsBare;
        _message.IsReadOnly = !worktree;
        _keepIndex.IsEnabled = _untracked.IsEnabled = !IsBusy && worktree;
        _saveAll.IsEnabled = !IsBusy && worktree && _status?.IsUnborn == false && _status.Changes.Any(change => !change.IsConflict && !change.IsIgnored);
        _saveSelected.IsEnabled = _saveAll.IsEnabled && _fileList.SelectedTags.OfType<FileChoice>().Any();
        _drop.IsEnabled = _apply.IsEnabled = !IsBusy && Selected is not null && !Repository.IsBare;
        _resolve.IsVisible = _status?.Changes.Any(change => change.IsConflict) == true;
        _resolve.IsEnabled = !IsBusy && _resolve.IsVisible;
        RefreshFileMenu();
    }
}

internal sealed class StashDropConfirmation : Window
{
    private readonly CheckBox _remember = new() { Name = "StashDropDontShowAgain" };
    internal bool DontShowAgain => _remember.IsChecked == true;
    internal StashDropConfirmation(GitStash stash, Localization.TranslationCatalog translations)
    {
        string T(string text) => translations.Translate(text);
        Title = T("Drop stash"); Width = 520; SizeToContent = SizeToContent.Height; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _remember.Content = T("Don't show this message again"); ReferenceDialogControls.Compact(_remember);
        Button yes = new() { Name = "ConfirmStashDrop", Content = T("Yes"), MinWidth = 75, Height = 25 };
        Button no = new() { Name = "CancelStashDrop", Content = T("No"), MinWidth = 75, Height = 25, IsDefault = true, IsCancel = true };
        yes.Click += (_, _) => Close(true); no.Click += (_, _) => Close(false);
        Content = new StackPanel { Margin = new Thickness(12), Spacing = 10, Children =
        {
            new TextBlock { Text = T("This action cannot be undone."), FontWeight = FontWeight.Bold },
            new TextBlock { Text = T("Are you sure?") + "\n" + stash.Reference + "  " + stash.Subject, TextWrapping = TextWrapping.Wrap }, _remember,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { yes, no } }
        } };
    }
}

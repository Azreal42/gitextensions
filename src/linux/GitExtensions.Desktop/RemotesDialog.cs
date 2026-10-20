using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original remote settings and default pull behavior share one repository snapshot.</summary>
public sealed partial class RemotesDialog : RepositoryDialog
{
    private readonly string? _preselectRemote, _preselectLocalBranch;
    private readonly bool _showAdvanced;
    private readonly CloneSourcesStore _history = new();
    private readonly TabControl _tabs = new() { Name = "RemotesTabs" };
    private readonly ListBox _remotes = new() { Name = "RemoteSettingsList", SelectionMode = SelectionMode.Single, Focusable = true };
    private readonly ComboBox _url = EditableChoice("RemoteSettingsUrl");
    private readonly ComboBox _pushUrl = EditableChoice("RemoteSettingsPushUrl");
    private readonly TextBox _name = ReferenceDialogControls.Text("RemoteSettingsName");
    private readonly TextBox _prefix = ReferenceDialogControls.Text("RemoteSettingsPrefix");
    private readonly CheckBox _separate = new() { Name = "SeparateRemotePushUrl" };
    private readonly Button _new, _delete, _toggle, _save, _colorButton, _resetColor, _browse, _browsePush;
    private readonly Control _statusRow;
    private Grid _management = null!;
    private TextBlock _managementCaption = null!;
    private IReadOnlyList<GitRemoteSettingsSnapshot> _values = [];
    private IReadOnlyList<GitReference> _references = [];
    private GitRemoteSettingsSnapshot? _selected;
    private string? _color;
    private bool _loading, _ready;

    public RemotesDialog(GitRepository repository, string? preselectRemote = null, string? preselectLocalBranch = null, bool showAdvanced = false)
        : base(repository, "Remote repositories")
    {
        if (preselectRemote is not null && preselectLocalBranch is not null) { throw new ArgumentException("Select either a remote or a local branch, not both."); }
        _preselectRemote = preselectRemote; _preselectLocalBranch = preselectLocalBranch;
        _showAdvanced = showAdvanced || AdvancedOptionsVisibility.Capture();
        Name = "RemotesDialog"; Width = 950; Height = 400; MinWidth = 950; MinHeight = 360;
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        ClassicChoiceAppearance.Apply(this, "original-remotes");
        Title = C("$this", "Remote repositories");
        _statusRow = Root.Children.Single(); _statusRow.IsVisible = false;
        ConstrainStatusHeight("RemoteSettingsError", 80);
        _new = IconButton("NewRemote", "RemoteAdd.png", "Create a new remote", () => { NewRemote(); return Task.CompletedTask; });
        _delete = IconButton("DeleteRemote", "RemoteDelete.png", "Delete the selected remote", DeleteAsync);
        _toggle = IconButton("ToggleRemoteState", "EyeClosed.png", "Deactivate the selected remote", ToggleAsync);
        _browse = DialogButton("BrowseRemoteSettingsUrl", "folderBrowserButtonUrl", "Browse...", () => BrowseAsync(_url), "FolderClosed.png");
        _browsePush = DialogButton("BrowseRemoteSettingsPushUrl", "folderBrowserButtonPushUrl", "Browse...", () => BrowseAsync(_pushUrl), "FolderClosed.png");
        _save = DialogButton("SaveRemoteSettings", "Save", "&Save changes", SaveAsync, "Save.png");
        _colorButton = DialogButton("RemoteSettingsColor", "btnRemoteColor", "Set &color", ChooseColorAsync);
        _resetColor = DialogButton("ResetRemoteSettingsColor", "btnRemoteColorReset", "Default color", () => { SetColor(null); return Task.CompletedTask; });
        _trackingSave = DialogButton("SaveBranchTracking", "SaveDefaultPushPull", "&Save changes", SaveTrackingAsync, "Save.png");
        BuildLayout();
        Track(_tabs, _remotes, _management, _trackingList, _trackingRemote, _trackingMerge);
        _remotes.SelectionChanged += (_, _) => { if (!_loading) { LoadSelectedRemote(); } };
        _name.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateActions(); } };
        _separate.IsCheckedChanged += (_, _) => UpdatePushUrlVisibility();
        _prefix.LostFocus += (_, _) => NormalizePrefix();
        _name.GotFocus += (_, _) => SuggestName();
        _url.GotFocus += (_, _) => FillUrlSuggestions(_url, push: false);
        _pushUrl.GotFocus += (_, _) => FillUrlSuggestions(_pushUrl, push: true);
        ConfigureTracking();
        IDisposable urlCompletion = RepositorySourceControls.CompleteHistory(_url, Root), pushCompletion = RepositorySourceControls.CompleteHistory(_pushUrl, Root);
        Closed += (_, _) => { urlCompletion.Dispose(); pushCompletion.Dispose(); };
        _tabs.SelectionChanged += (_, args) =>
        {
            if (!ReferenceEquals(args.Source, _tabs) || !_ready) { return; }
            Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible && !IsBusy && (FocusManager?.GetFocusedElement() is not Control focused
                    || !focused.IsEffectivelyVisible || TopLevel.GetTopLevel(focused) != this)) { FocusInitial(); }
            }, DispatcherPriority.Loaded);
        };
        Opened += (_, _) => { if (_ready) { FocusInitial(); } };
        UpdateActions();
    }

    private string C(string item, string source, string property = "Text") => Translations.Translate("FormRemotes", item, property, source);
    private AccessText Caption(string item, string source) => new() { Text = Translations.TranslateAccessKeys("FormRemotes", item, "Text", source) };
    private static ComboBox EditableChoice(string name) => RepositorySourceControls.EditableChoice(name);

    private Button DialogButton(string name, string originalName, string caption, Func<Task> action, string? icon = null)
    {
        Button button = Action(name, caption, action); button.Height = 25; button.MinWidth = 90;
        button.Content = icon is null ? Caption(originalName, caption) : new StackPanel
        { Orientation = Orientation.Horizontal, Spacing = 5, Children = { OriginalMenuItems.Icon(icon), Caption(originalName, caption) } };
        return button;
    }

    private Button IconButton(string name, string icon, string tip, Func<Task> action)
    {
        Button button = Action(name, tip, action); button.Content = OriginalMenuItems.Icon(icon);
        button.Width = button.Height = 26; button.Padding = new Thickness(3);
        ToolTip.SetTip(button, T(tip)); Avalonia.Automation.AutomationProperties.SetName(button, T(tip));
        return button;
    }

    public override Task InitializeAsync() => InitializeAsync(default);
    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RunAsync("Remote repositories", async token =>
        {
            await ReloadAsync(_preselectRemote, _preselectLocalBranch, token);
            if (_preselectLocalBranch is not null && _values.Count > 0) { _tabs.SelectedIndex = 1; }
            _ready = true;
        }, cancellationToken: cancellationToken);
        if (_ready && IsVisible) { FocusInitial(); }
    }

    private void FocusInitial()
    {
        if (_tabs.SelectedIndex == 1) { _trackingList.Focus(); }
        else if (_selected is null) { _name.Focus(); }
        else { _remotes.Focus(); }
    }

    private async Task ReloadAsync(string? remote, string? localBranch, CancellationToken token)
    {
        IReadOnlyList<GitRemoteSettingsSnapshot> values = await Repository.GetRemoteSettingsAsync(token);
        IReadOnlyList<GitBranchTrackingSnapshot> tracking = await Repository.GetBranchTrackingSettingsAsync(token);
        _references = await Repository.GetReferencesAsync(token);
        _loading = true;
        try
        {
            _values = values.OrderBy(value => value.Disabled).ThenBy(value => value.Name, StringComparer.Ordinal).ToArray();
            _remotes.ItemsSource = _values;
            _remotes.SelectedItem = _values.FirstOrDefault(value => value.Name == remote) ?? _values.FirstOrDefault();
            _tracking = tracking;
            BindTracking(localBranch);
            string[] suggestions = _history.Load().Concat(_values.SelectMany(value => value.FetchUrls.Concat(value.PushUrls))).Distinct(StringComparer.Ordinal).ToArray();
            _url.ItemsSource = _pushUrl.ItemsSource = suggestions;
        }
        finally { _loading = false; }
        LoadSelectedRemote();
    }

    private void NewRemote()
    {
        _remotes.SelectedItem = null; LoadSelectedRemote(); _name.Focus();
    }

    internal void StartNewRemote() { if (_ready && !IsBusy) { NewRemote(); } }

    private void LoadSelectedRemote()
    {
        _selected = _remotes.SelectedItem as GitRemoteSettingsSnapshot;
        _name.Text = _selected?.Name ?? "";
        SetText(_url, _selected?.Url ?? ""); SetText(_pushUrl, _selected?.PushUrl ?? "");
        _prefix.Text = _selected?.Prefix ?? "";
        _separate.IsChecked = _selected?.HasSeparatePushUrl == true;
        SetColor(_selected?.Color);
        _managementCaption.Text = C(_selected is null ? "_gbMgtPanelHeaderNew" : "_gbMgtPanelHeaderEdit", _selected is null ? "Create New Remote" : "Edit Remote Details");
        UpdatePushUrlVisibility(); UpdateActions();
    }

    private static void SetText(ComboBox box, string text) { box.SelectedItem = text; box.Text = text; }

    private async Task BrowseAsync(ComboBox target)
    {
        string? folder = await ExportDialogControls.FolderAsync(this, T("Please select a source directory"));
        if (folder is not null) { SetText(target, folder); }
    }

    private void NormalizePrefix()
        => _prefix.Text = GitBranchNaming.Normalize(_prefix.Text, new PreferencesStore().Load().AutoNormaliseSymbol, allowTrailingSlash: true);

    private void SuggestName()
    {
        if (_selected is not null || !string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_url.Text)) { return; }
        if (new GitCommands.Remotes.GitHostingRemoteParser().TryExtractGitHostingDataFromRemoteUrl(_url.Text, out _, out string? owner, out _))
        { _name.Text = owner; _name.SelectAll(); }
    }

    private void FillUrlSuggestions(ComboBox box, bool push)
    {
        if (_loading) { return; }
        string name = _name.Text ?? "";
        bool generic = string.IsNullOrWhiteSpace(name) || name is "origin" or "upstream" or "fork";
        string ownerName = generic ? "TO_REPLACE" : name;
        GitCommands.Remotes.GitHostingRemoteParser parser = new();
        HashSet<string> generated = new(StringComparer.Ordinal);
        foreach (GitRemoteSettingsSnapshot remote in _values)
        {
            string? url = push ? remote.PushUrl ?? remote.Url : remote.Url;
            if (string.IsNullOrEmpty(url)) { continue; }
            if (url.Contains(remote.Name + "/", StringComparison.Ordinal)) { generated.Add(url.Replace(remote.Name + "/", ownerName + "/", StringComparison.Ordinal)); }
            if (parser.TryExtractGitHostingDataFromRemoteUrl(url, out _, out string? owner, out _))
            { generated.Add(url.Replace(owner + "/", ownerName + "/", StringComparison.Ordinal)); }
        }
        string text = box.Text ?? "";
        _loading = true;
        try
        {
            box.ItemsSource = generated.Concat(_history.Load()).Concat(_values.SelectMany(remote => remote.FetchUrls.Concat(remote.PushUrls))).Distinct(StringComparer.Ordinal).ToArray();
            SetText(box, string.IsNullOrEmpty(text) && !generic && generated.Count == 1 ? generated.Single() : text);
        }
        finally { _loading = false; }
    }

    private Task SaveAsync() => RunAsync("Save changes", async token =>
    {
        NormalizePrefix();
        GitRemoteSettingsSnapshot saved;
        try
        {
            saved = await Repository.SaveRemoteSettingsAsync(_selected, new GitRemoteSettingsEdit(_name.Text ?? "", _url.Text ?? "",
                _separate.IsChecked == true ? _pushUrl.Text : null, _prefix.Text, _color), token);
        }
        catch (GitRemoteSettingsException exception)
        {
            if (exception.HasRepositoryChanges) { MarkRepositoryChanged(); }
            throw;
        }
        MarkRepositoryChanged();
        _history.Visit(saved.Url, 30);
        if (saved.PushUrl is { Length: > 0 } push) { _history.Visit(push, 30); }
        try { await FetchSavedAsync(saved.Name, token); }
        finally
        {
            // Fetch may have introduced the very branches offered by the tracking tab.
            if (!token.IsCancellationRequested) { await ReloadAsync(saved.Name, _trackingSelected?.LocalBranch, token); }
        }
    });

    private async Task FetchSavedAsync(string remote, CancellationToken token)
    {
        PullDialog fetch = new(Repository, GitPullAction.Fetch, remote) { RequestedThemeVariant = ActualThemeVariant };
        try
        {
            await fetch.InitializeAsync(token);
            if (fetch.LastException is Exception initializationError) { throw initializationError; }
            await fetch.ExecuteAsync(this, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (!fetch.Succeeded && !fetch.Cancelled)
            {
                using CancellationTokenRegistration registration = token.Register(() => Dispatcher.UIThread.Post(fetch.Close));
                await fetch.ShowDialog(this);
                token.ThrowIfCancellationRequested();
            }
            if (fetch.LastException is Exception error) { throw error; }
        }
        finally { fetch.Close(); }
    }

    private Task ToggleAsync() => RunAsync("Remote repositories", async token =>
    {
        if (_selected is not { } selected) { return; }
        try
        {
            GitRemoteSettingsSnapshot changed = await Repository.SetRemoteEnabledAsync(selected, selected.Disabled, token);
            MarkRepositoryChanged(); await ReloadAsync(changed.Name, _trackingSelected?.LocalBranch, token);
        }
        catch (GitRemoteSettingsException exception) { if (exception.HasRepositoryChanges) { MarkRepositoryChanged(); } throw; }
    });

    private Task DeleteAsync() => RunAsync("Delete remote", async token =>
    {
        if (_selected is not { } selected || !await ConfirmDeleteAsync(selected.Name, token)) { return; }
        try { await Repository.RemoveRemoteSettingsAsync(selected, token); }
        catch (GitRemoteSettingsException exception) { if (exception.HasRepositoryChanges) { MarkRepositoryChanged(); } throw; }
        MarkRepositoryChanged(); await ReloadAsync(null, _trackingSelected?.LocalBranch, token);
    });

    private async Task<bool> ConfirmDeleteAsync(string name, CancellationToken token)
    {
        Window prompt = new() { Name = "DeleteRemoteConfirmation", Title = T("Delete remote"), Width = 460, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ActualThemeVariant };
        Button yes = new() { Name = "ConfirmDeleteRemote", Content = T("Yes") };
        Button no = new() { Name = "CancelDeleteRemote", Content = T("No"), IsDefault = true, IsCancel = true };
        yes.Click += (_, _) => prompt.Close(true); no.Click += (_, _) => prompt.Close(false);
        prompt.Content = new StackPanel { Margin = new Thickness(12), Spacing = 10, Children =
        {
            new TextBlock { Text = string.Format(T("Delete remote '{0}' and its remote-tracking branches?"), name), TextWrapping = TextWrapping.Wrap },
            ExportDialogControls.Buttons(yes, no)
        } };
        using CancellationTokenRegistration registration = token.Register(() => Dispatcher.UIThread.Post(prompt.Close));
        bool result = await prompt.ShowDialog<bool>(this); token.ThrowIfCancellationRequested(); return result;
    }

    protected override void UpdateActions()
    {
        bool idle = _ready && !IsBusy;
        if (_management is not null) { _management.IsEnabled = idle && _selected?.Disabled != true; }
        _new.IsEnabled = idle;
        _save.IsEnabled = idle && _selected?.Disabled != true && !string.IsNullOrWhiteSpace(_name.Text);
        _delete.IsEnabled = _toggle.IsEnabled = idle && _selected?.CanModifyStructure == true;
        bool disabled = _selected?.Disabled == true;
        _toggle.Content = OriginalMenuItems.Icon(disabled ? "EyeOpened.png" : "EyeClosed.png");
        string tip = _selected?.ModificationRestriction ?? T(disabled ? "Activate the selected remote" : "Deactivate the selected remote.\nInactive remote is completely invisible to git.")
            + (disabled ? "" : "\n" + T("Its remote-tracking references and branch upstream settings will be removed."));
        ToolTip.SetTip(_toggle, tip); ToolTip.SetTip(_delete, _selected?.ModificationRestriction ?? T("Delete the selected remote"));
        bool trackingReady = _ready && (!IsBusy || _trackingOperation) && _trackingSelected is not null;
        _trackingRemote.IsEnabled = _trackingMerge.IsEnabled = _trackingSave.IsEnabled = trackingReady;
        _statusRow.IsVisible = LastError is not null;
    }
}

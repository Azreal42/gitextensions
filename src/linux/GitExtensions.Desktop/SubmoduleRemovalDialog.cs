using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The shared FormSubmodules manager, including its data-preserving Remove workflow.</summary>
public sealed partial class SubmoduleRemovalDialog : RepositoryDialog
{
    private readonly ListBox _modules = new() { Name = "RemovalSubmodules", SelectionMode = SelectionMode.Single, Focusable = true };
    private readonly TextBox _name = Detail("RemovalSubmoduleName");
    private readonly TextBox _remote = Detail("RemovalSubmoduleRemotePath");
    private readonly TextBox _path = Detail("RemovalSubmoduleLocalPath");
    private readonly TextBox _commit = Detail("RemovalSubmoduleCommit");
    private readonly TextBox _branch = Detail("RemovalSubmoduleBranch");
    private readonly TextBox _state = Detail("RemovalSubmoduleStatus");
    private readonly Button _remove, _update, _sync, _pull, _add;
    private readonly GitSubmodule? _initialSelection;
    private readonly Func<Window, GitRepository, CancellationToken, Task>? _afterSubmoduleUpdateAsync;
    private readonly Func<Window, GitRepository, bool, CancellationToken, Task>? _resolveConflictsAsync;
    private readonly SubmoduleDialogPreferencesStore _preferences;
    private bool _confirming, _ready, _loaded;
    private Window? _closingOwner;

    public GitProcessDialogResult? LastProcess { get; private set; }

    public SubmoduleRemovalDialog(GitRepository repository, GitSubmodule? selected = null,
        Func<Window, GitRepository, CancellationToken, Task>? afterSubmoduleUpdateAsync = null,
        Func<Window, GitRepository, bool, CancellationToken, Task>? resolveConflictsAsync = null,
        string? preferencesPath = null) : base(repository, "Submodules")
    {
        _initialSelection = selected; _afterSubmoduleUpdateAsync = afterSubmoduleUpdateAsync;
        _resolveConflictsAsync = resolveConflictsAsync; _preferences = new(preferencesPath);
        Name = "SubmoduleRemoval"; Title = Original("$this", "Submodules");
        Width = 782; Height = 372; MinWidth = 710; MinHeight = 350;
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        CollapseEmptyStatus(); ConstrainStatusHeight("SubmodulesStatusScroll", 70);
        _remove = Action("RemoveSubmodule", Original("RemoveSubmodule", "Remove"), RemoveAsync);
        _update = OriginalButton("UpdateSubmodule", "Update", () => ProcessAsync(sync: false), "SubmodulesUpdate");
        _sync = OriginalButton("SynchronizeSubmodule", "Synchronize", () => ProcessAsync(sync: true), "SubmodulesSync");
        _add = OriginalButton("AddSubmodule", "Add submodule", AddAsync);
        _pull = OriginalButton("PullSubmodule", "", PullAsync);
        _pull.Content = ResetDialogControls.Icon("Pull");
        _pull.Width = _pull.Height = _pull.MinWidth = _pull.MinHeight = 24; _pull.Padding = new Thickness(2);
        ToolTip.SetTip(_pull, T("Pull"));
        foreach (Button button in new[] { _remove, _update, _sync }) { button.MinWidth = 115; button.Height = 27; }
        _add.Width = _add.MaxWidth = 202; _add.Height = 27; _add.HorizontalAlignment = HorizontalAlignment.Left;
        _modules.TabIndex = _add.TabIndex = 0; _sync.TabIndex = 5; _update.TabIndex = 3; _remove.TabIndex = 6; _pull.TabIndex = 22;
        _name.TabIndex = 8; _remote.TabIndex = 9; _path.TabIndex = 10; _commit.TabIndex = 17; _branch.TabIndex = _state.TabIndex = 13;
        BuildOriginalLayout();
        Track(_modules);
        _modules.SelectionChanged += (_, _) => { ShowSelection(); UpdateActions(); };
        Closing += (_, args) =>
        {
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingOwner = Owner as Window; }
            if (_confirming) { foreach (Window child in OwnedWindows.ToArray()) { child.Close(); } }
        };
        Closed += (_, _) =>
        {
            _preferences.Save(new(_layout.ColumnDefinitions[0].Width.Value));
            Window? owner = _closingOwner; _closingOwner = null;
            if (owner is not null) { Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        };
        UpdateActions();
    }

    private string Original(string key, string source) => Translations.Translate("FormSubmodules", key, "Text", source);
    private static TextBox Detail(string name) => new() { Name = name, IsReadOnly = true, Height = 25, MinWidth = 0 };
    private GitSubmoduleSnapshot? Selected => _modules.SelectedItem as GitSubmoduleSnapshot;

    public override async Task InitializeAsync()
    {
        GitSubmoduleSnapshot? selected = Selected;
        await RunAsync("Submodules", async token =>
        {
            IReadOnlyList<GitSubmoduleSnapshot> entries = await Repository.GetSubmoduleSnapshotsAsync(token);
            _modules.ItemsSource = entries;
            _modules.SelectedItem = !_loaded
                ? _initialSelection is null ? entries.FirstOrDefault() : entries.SingleOrDefault(item =>
                    item.Name == _initialSelection.Name && item.Path == _initialSelection.Path && item.IndexHead == _initialSelection.ExpectedHead)
                : entries.SingleOrDefault(item => item.Name == selected?.Name && item.Path == selected?.Path);
            _loaded = _ready = true; ShowSelection(); Status.Text = "";
        });
        if (IsVisible && _ready) { _modules.Focus(); }
    }

    private async Task ReloadAsync(GitSubmoduleSnapshot? selected, CancellationToken token, string? addedPath = null)
    {
        IReadOnlyList<GitSubmoduleSnapshot> entries = await Repository.GetSubmoduleSnapshotsAsync(token);
        _modules.ItemsSource = entries;
        _modules.SelectedItem = addedPath is not null ? entries.SingleOrDefault(item => item.Path == addedPath)
            : selected is null ? null : entries.SingleOrDefault(item => item.Name == selected.Name && item.Path == selected.Path);
        _ready = true; ShowSelection(); Status.Text = "";
    }

    private void ShowSelection()
    {
        GitSubmoduleSnapshot? item = Selected;
        _name.Text = item?.Name; _remote.Text = item?.Url; _path.Text = item?.Path;
        _commit.Text = item?.Commit; _branch.Text = item?.Branch; _state.Text = item?.Status;
        ToolTip.SetTip(_state, item?.ValidationError);
    }

    protected override void UpdateActions()
    {
        bool enabled = _ready && !IsBusy && !_confirming;
        _remove.IsEnabled = enabled && Selected?.CanRemove == true;
        _update.IsEnabled = enabled && Selected?.CanUpdate == true;
        _sync.IsEnabled = enabled && Selected?.CanSync == true;
        _pull.IsEnabled = enabled && Selected?.CanOpen == true;
        _add.IsEnabled = enabled && !Repository.IsBare;
        _modules.IsEnabled = enabled;
    }
}

internal sealed class SubmoduleRemovalConfirmation : Window
{
    internal SubmoduleRemovalConfirmation(TranslationCatalog translations, GitSubmoduleRemovalSelection selection)
    {
        Name = "ConfirmSubmoduleRemoval";
        Title = translations.Translate("FormSubmodules", "_removeSelectedSubmoduleCaption", "Text", "Remove");
        Width = 520; CanResize = false; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        StackPanel content = new() { Margin = new Thickness(12), Spacing = 8 };
        content.Children.Add(new TextBlock { Name = "SubmoduleRemovalQuestion", TextWrapping = TextWrapping.Wrap,
            Text = translations.Translate("FormSubmodules", "_removeSelectedSubmodule", "Text", "Are you sure you want remove the selected submodule?") });
        content.Children.Add(new TextBox { Name = "ConfirmedSubmodulePath", Text = selection.Path, IsReadOnly = true });
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button yes = new() { Name = "ConfirmRemoveSubmodule", Content = translations.Translate("Yes"), MinWidth = 75, Height = 25, IsDefault = true };
        Button no = new() { Name = "KeepSubmodule", Content = translations.Translate("No"), MinWidth = 75, Height = 25, IsCancel = true };
        yes.Click += (_, _) => Close(true); no.Click += (_, _) => Close(false);
        buttons.Children.Add(yes); buttons.Children.Add(no); content.Children.Add(buttons); Content = content;
    }
}

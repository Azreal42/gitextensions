using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

/// <summary>FormCreateWorktree: branch selection and destination, with native Git doing the checkout.</summary>
public sealed class CreateWorktreeDialog : RepositoryDialog
{
    private readonly RadioButton _existing = new() { Name = "CheckoutExistingWorktreeBranch", GroupName = "WorktreeBranchMode" };
    private readonly RadioButton _new = new() { Name = "CreateNewWorktreeBranch", GroupName = "WorktreeBranchMode" };
    private readonly ComboBox _branches = ReferenceDialogControls.Choice("WorktreeExistingBranches");
    private readonly TextBox _name = ReferenceDialogControls.Text("WorktreeNewBranchName");
    private readonly TextBox _directory = ReferenceDialogControls.Text("WorktreeDirectory");
    private readonly Button _create, _browse;
    private readonly CloneDirectoryCompletion _completion;
    private readonly bool _autoNormalize;
    private readonly string _replacement;
    private readonly string? _initialMainPath;
    private string _mainPath;
    private IReadOnlyList<GitReference> _allBranches = [];
    private bool _ready, _loading;

    public string? CreatedWorktreePath { get; private set; }
    public GitProcessDialogResult? LastProcess { get; private set; }

    public CreateWorktreeDialog(GitRepository repository, string? mainPath = null, bool autoNormalize = true, string replacement = "_")
        : base(repository, "Create a new worktree")
    {
        Name = "CreateWorktreeDialog";
        Title = Text("$this", "Create a new worktree");
        _autoNormalize = autoNormalize; _replacement = replacement; _initialMainPath = mainPath; _mainPath = repository.Path;
        if (autoNormalize) { GitBranchNaming.Normalize("", replacement); }
        Width = MinWidth = 608; Height = 208; MinHeight = 208; SizeToContent = SizeToContent.Height;
        ConstrainStatusHeight("WorktreeCreateStatusScroll", 64);
        CollapseEmptyStatus();
        ClassicChoiceAppearance.Apply(this, "createWorktree");
        ReferenceDialogControls.ReferenceTemplate(_branches);
        _existing.Content = Caption("rbCheckoutExistingBranch", "Checkout an &existing branch:");
        _new.Content = Caption("rbCreateNewBranch", "Create a &new branch:\n(from current commit)");
        _existing.MinHeight = 24; _new.MinHeight = 40; _existing.Padding = _new.Padding = new Thickness(0);
        _create = Action("CreateTheNewWorktree", "Create the new worktree", CreateAsync);
        _create.Content = Caption("btnCreateWorktree", "&Create the new worktree"); _create.IsDefault = true; _create.Height = 25;
        _browse = Action("BrowseWorktreeDirectory", "Browse...", BrowseAsync);
        _browse.Content = WorktreeDialogControls.ButtonContent(Text("btnBrowseWorktreeDir", "Browse..."), "BrowseFileExplorer"); _browse.Height = 25;
        Grid choices = new() { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 3 };
        choices.Children.Add(_existing); Grid.SetColumn(_branches, 1); choices.Children.Add(_branches);
        Grid.SetRow(_new, 1); choices.Children.Add(_new); Grid.SetRow(_name, 1); Grid.SetColumn(_name, 1); choices.Children.Add(_name);
        _branches.VerticalAlignment = _name.VerticalAlignment = VerticalAlignment.Center;
        Grid group = ClassicChrome.Group("WorktreeCheckoutGroup", Text("gbxWhatToCheckout", "What to checkout:"), choices);
        Grid fields = new() { ColumnDefinitions = new("Auto,*,110"), ColumnSpacing = 8, Margin = new Thickness(0, 8, 0, 10) };
        Label label = new() { Content = Caption("lblNewWorktreeFolder", "New worktree &directory:"), Target = _directory, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        fields.Children.Add(label); Grid.SetColumn(_directory, 1); fields.Children.Add(_directory); Grid.SetColumn(_browse, 2); fields.Children.Add(_browse);
        StackPanel body = new() { Margin = new Thickness(9), Spacing = 4, Children = { group, fields } };
        StackPanel buttons = ExportDialogControls.Buttons(_create); DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons);
        Root.Children.Add(body);
        _completion = new CloneDirectoryCompletion(_directory, body, "Worktree");
        Track(_existing, _new, _branches, _name, _directory, _browse);
        _existing.IsCheckedChanged += (_, _) => RegeneratePath(); _new.IsCheckedChanged += (_, _) => RegeneratePath();
        _branches.SelectionChanged += (_, _) => RegeneratePath();
        _name.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { RegeneratePath(); } };
        _directory.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateActions(); } };
        _name.LostFocus += (_, _) => NormalizeName();
        Opened += (_, _) => { if (_ready) { FocusInitial(); } };
        Closed += (_, _) => _completion.Dispose();
        UpdateActions();
    }

    private string Text(string item, string source) => Translations.Translate("FormCreateWorktree", item, "Text", source);
    private AccessText Caption(string item, string source) => new() { Text = Translations.TranslateAccessKeys("FormCreateWorktree", item, "Text", source) };

    public override async Task InitializeAsync()
    {
        await RunAsync("Load branches", async token =>
        {
            IReadOnlyList<WorktreeSnapshot> worktrees = await Repository.GetWorktreeSnapshotsAsync(token);
            _mainPath = worktrees.FirstOrDefault(worktree => worktree.IsMain)?.Path ?? _initialMainPath ?? Repository.Path;
            _allBranches = (await Repository.GetReferencesAsync(token)).Where(branch => branch.IsBranch && !branch.IsRemote).ToArray();
            GitReference[] available = _allBranches.Where(branch => !branch.IsCurrent).ToArray();
            _loading = true;
            try
            {
                _branches.ItemsSource = available; _branches.SelectedIndex = available.Length == 0 ? -1 : 0;
                _existing.IsChecked = available.Length != 0; _new.IsChecked = available.Length == 0; _ready = true;
            }
            finally { _loading = false; }
            RegeneratePath(); Status.Text = "";
        });
        if (_ready && IsVisible) { FocusInitial(); }
    }

    private void FocusInitial() { if (_new.IsChecked == true) { _name.Focus(); } else { _branches.Focus(); } }

    private void NormalizeName()
    {
        int caret = _name.CaretIndex;
        _name.Text = GitBranchNaming.NormalizeForInput(_name.Text, _autoNormalize, _replacement);
        _name.CaretIndex = Math.Min(caret, _name.Text?.Length ?? 0);
    }

    private void RegeneratePath()
    {
        if (_loading || !_ready) { return; }
        string branch = _existing.IsChecked == true ? (_branches.SelectedItem as GitReference)?.ShortName ?? "" : _name.Text ?? "";
        // A Linux branch may contain Windows-invalid filename characters. They still need
        // flattening to keep the original, portable sibling-directory suggestion.
        char[] invalid = ['/', '\\', '<', '>', ':', '"', '|', '?', '*', '\0'];
        string flattened = string.Join('_', branch.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        _directory.Text = _mainPath.TrimEnd(Path.DirectorySeparatorChar) + "_" + flattened;
        UpdateActions();
    }

    private async Task BrowseAsync()
    {
        if (IsBusy) { return; }
        string? path = await ExportDialogControls.FolderAsync(this, T("Choose directory"));
        if (path is not null && IsVisible) { _directory.Text = path; _directory.Focus(); }
    }

    private async Task CreateAsync()
    {
        if (IsBusy || !_ready || !IsVisible) { return; }
        if (_new.IsChecked == true) { NormalizeName(); }
        UpdateActions(); if (!_create.IsEnabled) { return; }
        string path = Path.GetFullPath(_directory.Text!, Repository.Path);
        WorktreeCreateOptions options = new(path, _existing.IsChecked == true ? ((GitReference)_branches.SelectedItem!).ShortName : "HEAD",
            _new.IsChecked == true ? _name.Text : null);
        WorktreeOperationResult? created = null;
        await RunAsync("Create the new worktree", async token =>
        {
            LastProcess = await GitProcessDialog.ShowAsync(this, Repository.Path, "git worktree add " + ExportDialogControls.DisplayPath(path), true,
                async (ct, output) => { created = await Repository.CreateWorktreeAsync(options, ct, output); return created.Result; }, token);
            if (LastProcess.CommandStarted) { MarkRepositoryChanged(); }
            if (LastProcess.Success && !LastProcess.WorkflowStopRequested && !LastProcess.OwnerCloseRequested)
            {
                CreatedWorktreePath = created?.Worktree?.Path ?? path;
                // RepositoryDialog defers closing until the running operation has drained.
                Close();
            }
            else if (LastProcess.Error is Exception error) { ReportError(error); }
            else if (LastProcess.WasCancelled) { Status.Text = T("Operation cancelled") + ". " + T("The worktree list will be refreshed to show any completed changes."); }
            else if (LastProcess.Result is { Success: false } failed) { ReportError(new GitCommandException(["worktree", "add"], failed)); }
            else { Status.Text = T("The worktree list will be refreshed to show any completed changes."); }
        });
        if (IsVisible) { _directory.Focus(); }
    }

    private bool TargetIsAvailable()
    {
        if (string.IsNullOrWhiteSpace(_directory.Text)) { return false; }
        try
        {
            string path = Path.GetFullPath(_directory.Text, Repository.Path);
            return !File.Exists(path) && (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    protected override void UpdateActions()
    {
        bool enabled = _ready && !IsBusy;
        _existing.IsEnabled = enabled && _branches.ItemCount > 0; _new.IsEnabled = enabled;
        _branches.IsEnabled = enabled && _existing.IsChecked == true; _name.IsEnabled = enabled && _new.IsChecked == true;
        bool branchValid = _existing.IsChecked == true ? _branches.SelectedItem is GitReference
            : !string.IsNullOrWhiteSpace(_name.Text) && !_allBranches.Any(branch => branch.ShortName == _name.Text);
        _create.IsEnabled = enabled && branchValid && TargetIsAvailable();
    }
}

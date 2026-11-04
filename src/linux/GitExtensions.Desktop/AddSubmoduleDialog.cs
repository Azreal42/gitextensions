using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop;

/// <summary>FormAddSubmodule: the native add command owns cloning, registration and index changes.</summary>
public sealed partial class AddSubmoduleDialog : RepositoryDialog
{
    private readonly ComboBox _source = RepositorySourceControls.EditableChoice("AddSubmoduleSource");
    private readonly TextBox _local = ReferenceDialogControls.Text("AddSubmoduleLocalPath");
    private readonly ComboBox _branch = RepositorySourceControls.EditableChoice("AddSubmoduleBranch");
    private readonly CheckBox _force = new() { Name = "AddSubmoduleForce", Height = 19, MinHeight = 19, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _browse, _add;
    private readonly IDisposable _historyCompletion;
    private bool _ready, _bare;
    private string _rootPath;

    public GitProcessDialogResult? LastProcess { get; private set; }
    public string? AddedSubmodulePath { get; private set; }

    public AddSubmoduleDialog(GitRepository repository) : base(repository, "Add submodule")
    {
        Name = "AddSubmoduleDialog"; Title = Text("$this", "Add submodule");
        Width = MinWidth = 492; Height = MinHeight = 131; SizeToContent = SizeToContent.Height;
        _rootPath = repository.Path;
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        CollapseEmptyStatus(); ConstrainStatusHeight("AddSubmoduleStatusScroll", 80);
        ClassicChoiceAppearance.Apply(this, "addSubmodule");
        _browse = Action("BrowseSubmoduleSource", "Browse", BrowseAsync); _browse.Content = Text("Browse", "Browse");
        _add = Action("AddSubmodule", "Add", AddAsync); _add.Content = Text("Add", "Add");
        _source.TabIndex = 1; _browse.TabIndex = 2; _local.TabIndex = 4; _branch.TabIndex = 6; _force.TabIndex = 7; _add.TabIndex = 8;
        _browse.Width = _add.Width = 102; _browse.Height = _add.Height = 25;
        _force.Content = Text("chkForce", "Force");
        Grid fields = new() { Name = "AddSubmoduleFields", ColumnDefinitions = new("130,*,102"), ColumnSpacing = 6, RowDefinitions = new("27,27,27") };
        Field(fields, 0, "label1", "Path to submodule", _source);
        Field(fields, 1, "label2", "Local path", _local);
        Field(fields, 2, "label3", "Branch", _branch);
        Grid.SetColumn(_browse, 2); fields.Children.Add(_browse);
        Grid footer = new() { ColumnDefinitions = new("*,Auto"), Height = 25, Margin = new Thickness(0, 3, 0, 0) };
        footer.Children.Add(_force); Grid.SetColumn(_add, 1); footer.Children.Add(_add);
        Root.Children.Add(new StackPanel { Margin = new Thickness(8, 12, 12, 10), Children = { fields, footer } });
        Track(_source, _local, _branch, _force, _browse);
        _historyCompletion = RepositorySourceControls.CompleteHistory(_source, Root);
        _source.PropertyChanged += (_, change) => { if (change.Property == ComboBox.TextProperty) { SourceChanged(); } };
        _local.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateActions(); } };
        _branch.DropDownOpened += async (_, _) => await LoadBranchesAsync();
        ConfigureBranchLifetime();
        Opened += (_, _) => { if (_ready) { FocusSource(); } };
        Closed += (_, _) => _historyCompletion.Dispose();
        UpdateActions();
    }

    private string Text(string item, string source) => Translations.Translate("FormAddSubmodule", item, "Text", source);
    private void Field(Grid fields, int row, string item, string caption, Control input)
    {
        Label label = new() { Content = Text(item, caption), Target = input, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(label, row); fields.Children.Add(label);
        input.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(input, row); Grid.SetColumn(input, 1); fields.Children.Add(input);
    }

    public override async Task InitializeAsync()
    {
        await RunAsync("Load repository", async token =>
        {
            GitRepositoryInfo info = await Repository.GetInfoAsync(token); _bare = info.IsBare; _rootPath = info.RootPath;
            _source.ItemsSource = new CloneSourcesStore().Load(); _source.SelectedIndex = -1; _source.Text = "";
            _local.Text = ""; _branch.Text = ""; _force.IsChecked = false; _ready = true;
            Status.Text = _bare ? T("Submodules require a working tree.") : "";
        });
        if (_ready && IsVisible) { FocusSource(); }
    }

    private void FocusSource()
    {
        if (_source.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is TextBox editor) { editor.Focus(); }
        else { _source.Focus(); }
    }

    private void SourceChanged()
    {
        _sourceVersion++; _branchVersion++; CancelBranchRequests();
        string? oldError = BranchLoadError?.Message; BranchLoadError = null;
        if (Status.Text == oldError) { Status.Text = ""; }
        string text = _branch.Text ?? "";
        _branch.ItemsSource = Array.Empty<string>(); _branch.SelectedItem = null; _branch.Text = text;
        string name = RepositorySourceControls.RepositoryName(_source.Text ?? "");
        if (name.Length > 0 && name is not ("." or "..")) { _local.Text = name; }
        UpdateActions();
    }

    private async Task BrowseAsync()
    {
        if (IsBusy || _branchClosing) { return; }
        string? source = await ExportDialogControls.FolderAsync(this, T("Please select a source directory"));
        if (source is not null && IsVisible && !_branchClosing) { _source.SelectedItem = source; _source.Text = source; FocusSource(); }
    }

    public GitSubmoduleAddOptions ReadOptions() => new(_source.Text ?? "", _local.Text ?? "", string.IsNullOrWhiteSpace(_branch.Text) ? null : _branch.Text.Trim(), _force.IsChecked == true);

    private async Task AddAsync()
    {
        if (IsBusy || !_ready || _bare || _branchClosing || !IsVisible) { return; }
        GitSubmoduleAddOptions options = ReadOptions();
        if (options.Source.Length == 0 || options.Path.Length == 0)
        {
            Status.Text = Text("_remoteAndLocalPathRequired", "A remote path and local path are required");
            return;
        }
        await RunAsync("Add submodule", async token =>
        {
            CancelBranchRequests(); await DrainBranchRequestsAsync(); token.ThrowIfCancellationRequested();
            LastProcess = await GitProcessDialog.ShowAsync(this, Repository.Path, "git submodule add — " + ExportDialogControls.DisplayPath(options.Source) + " → " + ExportDialogControls.DisplayPath(options.Path), true,
                (ct, output) => Repository.AddSubmoduleAsync(options, ct, output), token);
            if (LastProcess.CommandStarted) { MarkRepositoryChanged(); }
            if (LastProcess.Success && !LastProcess.WorkflowStopRequested && !LastProcess.OwnerCloseRequested)
            {
                AddedSubmodulePath = Path.GetRelativePath(_rootPath, Path.GetFullPath(options.Path, _rootPath));
                Close();
            }
            else if ((LastProcess.Error ?? LastProcess.CompletionError) is Exception error) { ReportError(error); }
            else if (LastProcess.WasCancelled) { Status.Text = T("Operation cancelled") + ". " + T("Git may already have changed the index, configuration or submodule directory. The parent will reread its state."); }
            else if (LastProcess.Result is { Success: false } failed) { ReportError(new GitCommandException(["submodule", "add"], failed)); }
            else if (LastProcess.Success) { Status.Text = T("Submodule added. Close this dialog to refresh the repository."); }
        });
        if (IsVisible && !_branchClosing) { _local.Focus(); }
    }

    protected override void UpdateActions()
    {
        bool enabled = _ready && !IsBusy && !_branchClosing;
        _add.IsEnabled = enabled && !_bare && !string.IsNullOrEmpty(_source.Text) && !string.IsNullOrEmpty(_local.Text);
        _source.IsEnabled = _local.IsEnabled = _branch.IsEnabled = _force.IsEnabled = _browse.IsEnabled = enabled && !_bare;
    }
}

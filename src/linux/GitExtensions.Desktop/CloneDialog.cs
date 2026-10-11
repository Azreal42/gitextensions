using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace GitExtensions.Linux.Desktop;

public sealed class CloneDialog : StartupRepositoryDialog
{
    private readonly ClonePreferencesStore _preferences = new();
    private readonly TextBox _source = new() { Name = "CloneSource", Height = 24 };
    private readonly TextBox _destination = new() { Name = "CloneDestination", Height = 24 };
    private readonly TextBox _subdirectory = new() { Name = "CloneSubdirectory", Height = 24 };
    private readonly TextBox _branch = new() { Name = "CloneBranch", Height = 24 };
    private readonly ComboBox _branches = new() { Name = "CloneBranches", MinWidth = 36, Width = 36, Height = 24 };
    private readonly TextBlock _location = new() { Name = "CloneLocation", TextWrapping = TextWrapping.Wrap };
    private readonly RadioButton _personal = new() { Name = "ClonePersonal", GroupName = "CloneType", IsChecked = true, Height = 24, Padding = new Thickness(0) };
    private readonly RadioButton _bare = new() { Name = "CloneBare", GroupName = "CloneType", Height = 24, Padding = new Thickness(0) };
    private readonly CheckBox _recursive = new() { Name = "CloneSubmodules", IsChecked = true, Height = 24, Padding = new Thickness(0) };
    private readonly CheckBox _fullHistory = new() { Name = "CloneFullHistory", IsChecked = true, Height = 24, Padding = new Thickness(0) };
    private readonly string _defaultBranch;
    private readonly string _noCheckout;

    public CloneDialog(string destination = "", string source = "") : base("Clone repository")
    {
        Width = 655;
        _defaultBranch = T("(default: remote HEAD)");
        _noCheckout = T("(none: don't checkout after clone)");
        _branch.Text = _defaultBranch;
        _personal.Content = T("Personal repository");
        _bare.Content = T("Public repository, no working directory  (--bare)");
        _recursive.Content = T("Initialize all submodules");
        _fullHistory.Content = T("Download full history");
        _branches.ItemsSource = new[] { _defaultBranch, _noCheckout };
        _branches.SelectionChanged += (_, _) => { if (_branches.SelectedItem is string branch) { _branch.Text = branch; } };
        _branches.DropDownOpened += async (_, _) => await LoadBranchesAsync();
        _source.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty) { return; }
            string name = RepositoryName(_source.Text ?? "");
            if (name.Length > 0) { _subdirectory.Text = name; }
            _branch.Text = _defaultBranch;
            _branches.ItemsSource = new[] { _defaultBranch, _noCheckout };
            UpdateLocation();
        };
        _destination.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateLocation(); } };
        _subdirectory.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateLocation(); } };
        _bare.IsCheckedChanged += (_, _) => UpdateActions();
        _branch.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateActions(); } };
        ClonePreferences preferences = _preferences.Load();
        _recursive.IsChecked = preferences.InitializeSubmodules;
        _destination.Text = string.IsNullOrEmpty(destination) ? preferences.Destination : destination;
        _source.Text = source;
        Grid fields = ExportDialogControls.Fields();
        fields.ColumnDefinitions = new ColumnDefinitions("150,*,Auto");
        ExportDialogControls.Field(fields, T("Repository to clone:"), _source, Action("BrowseCloneSource", "Browse", () => BrowseAsync(_source)));
        ExportDialogControls.Field(fields, T("Destination:"), _destination, Action("BrowseCloneDestination", "Browse", () => BrowseAsync(_destination)));
        ExportDialogControls.Field(fields, T("Subdirectory to create:"), _subdirectory);
        Grid branch = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        branch.Children.Add(_branch);
        Grid.SetColumn(_branches, 1);
        branch.Children.Add(_branches);
        ExportDialogControls.Field(fields, T("Branch:"), branch, Action("RefreshCloneBranches", "Refresh", LoadBranchesAsync));
        StackPanel body = new() { Margin = new Thickness(12), Spacing = 8 };
        body.Children.Add(fields);
        body.Children.Add(_location);
        body.Children.Add(ExportDialogControls.Group(T("Repository type"), new StackPanel { Spacing = 4, Children = { _personal, _bare } }));
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Children = { _recursive, _fullHistory } });
        StackPanel buttons = ExportDialogControls.Buttons(Action("CancelClone", "Cancel", () => { Close(); return Task.CompletedTask; }), Action("CloneRepository", "Clone", CloneAsync));
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        Root.Children.Add(body);
        Track(_source, _destination, _subdirectory, _branch, _branches, _personal, _bare, _recursive, _fullHistory);
        UpdateLocation();
        UpdateActions();
    }

    public GitCloneOptions ReadOptions()
    {
        string? branch = _branch.Text;
        bool noCheckout = branch == _noCheckout;
        if (noCheckout || branch == _defaultBranch || string.IsNullOrWhiteSpace(branch)) { branch = null; }
        return new(_bare.IsChecked == true, _recursive.IsChecked == true, branch, noCheckout, _fullHistory.IsChecked == true ? null : 1);
    }

    private async Task BrowseAsync(TextBox field)
    {
        string? path = await ExportDialogControls.FolderAsync(this, T("Choose directory"));
        if (path is not null) { field.Text = path; }
    }

    private Task LoadBranchesAsync() => RunAsync("Load branches", async token =>
    {
        string? selected = _branch.Text;
        IReadOnlyList<string> branches = await GitRepository.GetRemoteBranchesAsync(_source.Text ?? "", token);
        _branches.ItemsSource = new[] { _defaultBranch, _noCheckout }.Concat(branches).ToArray();
        _branch.Text = selected;
        Status.Text = T("Branch:") + " " + branches.Count;
    });

    private Task CloneAsync() => RunAsync("Clone", async token =>
    {
        string directory = AbsoluteDirectory(_destination.Text);
        string subdirectory = _subdirectory.Text ?? "";
        if (Path.IsPathRooted(subdirectory) || subdirectory.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
        { throw new ArgumentException("Subdirectory must remain inside the destination folder."); }
        string path = Path.GetFullPath(Path.Combine(directory, subdirectory));
        GitRepository cloned = await GitRepository.CloneAsync(_source.Text ?? "", path, ReadOptions(), token);
        _preferences.Save(new(directory, _recursive.IsChecked == true));
        Complete(cloned);
    });

    private void UpdateLocation()
    {
        string destination = _destination.Text ?? "";
        string subdirectory = _subdirectory.Text ?? "";
        try
        {
            string path = Path.Combine(destination, subdirectory);
            bool exists = Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
            _location.Text = T("The repository will be cloned to a new directory located here:") + "\n" + ExportDialogControls.DisplayPath(path) + "\n" + T(exists ? "(Directory already exists)" : "(New directory)");
            if (exists) { _location.Foreground = new SolidColorBrush(Color.Parse("#d95040")); }
            else { _location.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ClassicTextBrush")); }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        { _location.Text = exception.Message; }
    }

    protected override void UpdateActions() => _recursive.IsEnabled = !IsBusy && _bare.IsChecked != true && _branch.Text != _noCheckout;

    private static string RepositoryName(string source)
    {
        string path = source.TrimEnd('/');
        if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && uri.Scheme != "file") { path = uri.AbsolutePath.TrimEnd('/'); }
        int split = Math.Max(path.LastIndexOf('/'), path.LastIndexOf(':'));
        string name = path[(split + 1)..];
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}

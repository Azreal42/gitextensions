using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace GitExtensions.Linux.Desktop;

public sealed class ArchiveDialog : RepositoryDialog
{
    private readonly TextBox _revision = new() { Name = "ArchiveRevision", Height = 24 };
    private readonly TextBlock _summary = new() { Name = "ArchiveSummary", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _output = new() { Name = "ArchiveOutput", Height = 24 };
    private readonly TextBox _prefix = new() { Name = "ArchivePrefix", Height = 24 };
    private readonly RadioButton _zip = new() { Name = "ArchiveZip", Content = "zip", GroupName = "ArchiveFormat", IsChecked = true };
    private readonly RadioButton _tar = new() { Name = "ArchiveTar", Content = "tar", GroupName = "ArchiveFormat" };
    private readonly CheckBox _pathsEnabled = new() { Name = "ArchivePathsEnabled" };
    private readonly TextBox _paths = new() { Name = "ArchivePaths", AcceptsReturn = true, Height = 66, FontFamily = new FontFamily("monospace") };
    private readonly ListBox _exactPaths = new() { Name = "ArchiveExactPaths", SelectionMode = SelectionMode.Multiple, Height = 75, IsVisible = false };
    private readonly CheckBox _comparisonEnabled = new() { Name = "ArchiveComparisonEnabled" };
    private readonly TextBox _comparison = new() { Name = "ArchiveComparison", Height = 24 };
    private readonly NumericUpDown _compression = new() { Name = "ArchiveCompression", Minimum = 0, Maximum = 9, Value = 6, FormatString = "0", Width = 100, Height = 24, Padding = new Thickness(4, 2) };
    private readonly Button _save;
    private readonly Button _selectPaths;
    private bool _hasCommits;
    public GitArchiveResult? LastResult { get; private set; }

    public ArchiveDialog(GitRepository repository, string revision = "HEAD", string? path = null) : base(repository, "Archive")
    {
        Width = 630;
        MinWidth = 540;
        SizeToContent = SizeToContent.Height;
        _revision.Text = revision;
        _comparison.Text = "HEAD~1";
        if (path is not null)
        {
            _exactPaths.ItemsSource = new[] { path };
            _exactPaths.SelectedItems?.Add(path);
            _exactPaths.IsVisible = true;
        }
        _pathsEnabled.Content = T("Archive specific paths only");
        _pathsEnabled.IsChecked = path is not null;
        _comparisonEnabled.Content = T("Archive only changed files");
        _exactPaths.ItemTemplate = new FuncDataTemplate<string>((item, _) => new TextBlock { Text = ExportDialogControls.DisplayPath(item ?? ""), Margin = new Thickness(4, 2) });
        _save = Action("SaveArchive", "Save as…", SaveAsync);
        _save.IsDefault = true;
        StackPanel buttons = ExportDialogControls.Buttons(Action("CloseArchive", "Close", () => { Close(); return Task.CompletedTask; }), _save);
        DockPanel.SetDock(buttons, Dock.Bottom);
        Root.Children.Add(buttons);
        StackPanel body = new() { Margin = new Thickness(10), Spacing = 8 };
        Grid revisionFields = ExportDialogControls.Fields();
        ExportDialogControls.Field(revisionFields, T("Revision"), _revision, Action("ChooseArchiveRevision", "Choose…", ChooseRevisionAsync));
        body.Children.Add(ExportDialogControls.Group(T("This revision will be archived:"), new StackPanel { Spacing = 4, Children = { revisionFields, _summary } }));
        Grid outputFields = ExportDialogControls.Fields();
        ExportDialogControls.Field(outputFields, T("Archive format"), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _zip, _tar } });
        ExportDialogControls.Field(outputFields, T("Output file"), _output, Action("BrowseArchiveOutput", "Browse…", BrowseOutputAsync));
        ExportDialogControls.Field(outputFields, T("Directory prefix"), _prefix);
        ExportDialogControls.Field(outputFields, T("Compression"), _compression);
        body.Children.Add(outputFields);
        _selectPaths = Action("SelectArchivePaths", "Choose files…", SelectPathsAsync);
        Grid pathsHeader = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        pathsHeader.Children.Add(_pathsEnabled);
        StackPanel pathButtons = new() { Orientation = Orientation.Horizontal, Spacing = 6, Children =
        {
            Action("EnterArchivePaths", "Enter paths", () => { _exactPaths.IsVisible = false; UpdateActions(); return Task.CompletedTask; }), _selectPaths
        } };
        Grid.SetColumn(pathButtons, 1);
        pathsHeader.Children.Add(pathButtons);
        Grid comparisonFields = ExportDialogControls.Fields();
        ExportDialogControls.Field(comparisonFields, T("Compare with"), _comparison, Action("ChooseArchiveComparison", "Choose…", ChooseComparisonAsync));
        body.Children.Add(ExportDialogControls.Group(T("Filter files"), new StackPanel
        {
            Spacing = 4,
            Children = { pathsHeader, _paths, _exactPaths, new TextBlock { Text = T("separate each new path by new line") }, _comparisonEnabled, comparisonFields }
        }));
        Root.Children.Add(body);
        Track(_revision, _output, _prefix, _zip, _tar, _pathsEnabled, _paths, _exactPaths, _comparisonEnabled, _comparison, _compression);
        _pathsEnabled.IsCheckedChanged += (_, _) => { if (_pathsEnabled.IsChecked == true) { _comparisonEnabled.IsChecked = false; } UpdateActions(); };
        _comparisonEnabled.IsCheckedChanged += (_, _) => { if (_comparisonEnabled.IsChecked == true) { _pathsEnabled.IsChecked = false; } UpdateActions(); };
        _zip.IsCheckedChanged += (_, _) => UpdateActions();
        _tar.IsCheckedChanged += (_, _) => UpdateActions();
        _revision.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty && _exactPaths.ItemsSource is not null)
            {
                _exactPaths.ItemsSource = null;
                _exactPaths.IsVisible = false;
                UpdateActions();
            }
        };
        UpdateActions();
    }

    public override Task InitializeAsync() => RunAsync("Archive", ReadRevisionAsync);

    private async Task ReadRevisionAsync(CancellationToken token)
    {
        _hasCommits = !(await Repository.GetStatusAsync(token)).IsUnborn;
        if (!_hasCommits) { _summary.Text = T("This repository has no commits."); return; }
        GitCommit revision = (await Repository.GetLogAsync(1, _revision.Text ?? "HEAD", token)).Single();
        _summary.Text = revision.Subject + "\n" + T("Author") + ": " + revision.AuthorName + "    " + T("Date") + ": " + revision.AuthoredAt.LocalDateTime.ToString("g") + "\n" + revision.Hash;
        Status.Text = T("Save archive as");
    }

    private async Task ChooseRevisionAsync()
    {
        GitCommit? revision = await ExportRevisionPicker.ShowAsync(this, Repository);
        if (revision is null) { return; }
        _revision.Text = revision.Hash;
        _exactPaths.IsVisible = false;
        _exactPaths.ItemsSource = null;
        await RunAsync("Revision", ReadRevisionAsync);
    }

    private async Task ChooseComparisonAsync()
    {
        GitCommit? revision = await ExportRevisionPicker.ShowAsync(this, Repository);
        if (revision is not null) { _comparison.Text = revision.Hash; _comparisonEnabled.IsChecked = true; }
    }

    private async Task BrowseOutputAsync()
    {
        string extension = _tar.IsChecked == true ? "tar" : "zip";
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Save archive as"), DefaultExtension = extension,
            SuggestedFileName = Path.GetFileName(Repository.Path) + "-" + (_revision.Text ?? "HEAD")[..Math.Min(9, (_revision.Text ?? "HEAD").Length)] + "." + extension,
            FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = ["*." + extension] }], ShowOverwritePrompt = false
        });
        if (file?.TryGetLocalPath() is string path) { _output.Text = path; }
    }

    private Task SelectPathsAsync() => RunAsync("Choose files", async token =>
    {
        string[] selected = _exactPaths.SelectedItems?.Cast<string>().ToArray() ?? [];
        _exactPaths.ItemsSource = (await Repository.GetTreeAsync(_revision.Text ?? "HEAD", cancellationToken: token)).Select(entry => entry.Path).ToArray();
        _exactPaths.SelectedItems?.Clear();
        foreach (string item in selected) { _exactPaths.SelectedItems?.Add(item); }
        _exactPaths.IsVisible = true;
        _pathsEnabled.IsChecked = true;
        Status.Text = T("Select files to archive");
    });

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_output.Text)) { await BrowseOutputAsync(); }
        if (string.IsNullOrWhiteSpace(_output.Text)) { return; }
        await RunAsync("Archive", async token =>
        {
            await ReadRevisionAsync(token);
            IReadOnlyList<string>? paths = null;
            if (_pathsEnabled.IsChecked == true)
            {
                paths = _exactPaths.IsVisible
                    ? _exactPaths.SelectedItems?.Cast<string>().ToArray() ?? []
                    : (_paths.Text ?? "").Split('\n').Select(path => path.TrimEnd('\r')).Where(path => path.Length > 0).ToArray();
                if (paths.Count == 0) { throw new ArgumentException(T("Select files to archive")); }
            }
            LastResult = await Repository.ExportArchiveAsync(new(_output.Text!, _revision.Text ?? "HEAD", _tar.IsChecked == true ? GitArchiveFormat.Tar : GitArchiveFormat.Zip,
                _prefix.Text ?? "", paths, _comparisonEnabled.IsChecked == true ? _comparison.Text : null, _tar.IsChecked == true ? null : (int?)_compression.Value), token);
            Status.Text = T("Archive created") + ": " + ExportDialogControls.DisplayPath(LastResult.OutputPath);
        });
    }

    protected override void UpdateActions()
    {
        _save.IsEnabled = !IsBusy && _hasCommits;
        _selectPaths.IsEnabled = !IsBusy && _hasCommits;
        _paths.IsEnabled = !IsBusy && _pathsEnabled.IsChecked == true && !_exactPaths.IsVisible;
        _paths.IsVisible = !_exactPaths.IsVisible;
        _exactPaths.IsEnabled = !IsBusy && _pathsEnabled.IsChecked == true;
        _comparison.IsEnabled = !IsBusy && _comparisonEnabled.IsChecked == true;
        _compression.IsEnabled = !IsBusy && _tar.IsChecked != true;
    }
}

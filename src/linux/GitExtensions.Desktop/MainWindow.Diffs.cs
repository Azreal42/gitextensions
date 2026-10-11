using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly ListBox _revisionFiles = new() { Name = "RevisionFiles" };
    private readonly ComboBox _diffParent = new() { Name = "RevisionDiffParent", MinWidth = 220, IsVisible = false };
    private GitRevisionChanges? _revisionChanges;
    private FileContentView? _commitContent;
    private FileContentView? _changesContent;
    private FileContentView? _fileContent;
    private bool _loadingCommitFiles;

    private Control BuildCommitFilesView()
    {
        _commitContent = new FileContentView(_diff, new DiffView(_diff) { Name = "CommitDiff" });
        _revisionFiles.ItemTemplate = new FuncDataTemplate<GitRevisionFile>((file, _) => file is null ? new TextBlock() : IconLabel(
            file.Path + (file.OriginalPath is null ? "" : " ← " + file.OriginalPath),
            file.Status switch { 'A' => "DiffA.png", 'D' => "DiffR.png", 'R' => "DiffC.png", _ => "Edit.png" }));
        _revisionFiles.Styles.Add(new Avalonia.Styling.Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters = { new Avalonia.Styling.Setter(HeightProperty, 24d), new Avalonia.Styling.Setter(PaddingProperty, new Thickness(0)) }
        });
        _revisionFiles.SelectionChanged += async (_, _) =>
        {
            if (!_loadingCommitFiles && !_refreshing && !_busy && _revisionFiles.SelectedItem is GitRevisionFile file)
            {
                await RunAsync("File diff", repository => ShowRevisionFileAsync(repository, file), refresh: false);
            }
        };
        _diffParent.SelectionChanged += async (_, _) =>
        {
            if (!_loadingCommitFiles && !_refreshing && !_busy && _commits.SelectedItem is GitCommit commit)
            {
                await RunAsync("Parent diff", repository => LoadCommitFilesAsync(repository, commit.Hash, preserveParent: true), refresh: false);
            }
        };
        Grid split = new() { ColumnDefinitions = new ColumnDefinitions("230,4,*") };
        split.Children.Add(ClassicChrome.Border(_revisionFiles, new Thickness(0, 0, 1, 0)));
        GridSplitter splitter = new() { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1);
        split.Children.Add(splitter);
        Grid.SetColumn(_commitContent, 2);
        split.Children.Add(_commitContent);
        DockPanel view = new();
        DockPanel.SetDock(_diffParent, Dock.Top);
        view.Children.Add(_diffParent);
        view.Children.Add(split);
        return view;
    }

    private async Task LoadCommitFilesAsync(GitRepository repository, string revision, bool preserveParent = false)
    {
        string? selected = (_revisionFiles.SelectedItem as GitRevisionFile)?.Path;
        _loadingCommitFiles = true;
        try
        {
            if (!preserveParent)
            {
                IReadOnlyList<string> parents = (_commits.SelectedItem as GitCommit)?.Parents ?? [];
                _diffParent.ItemsSource = parents;
                _diffParent.SelectedIndex = parents.Count > 0 ? 0 : -1;
                _diffParent.IsVisible = parents.Count > 1;
            }
            _revisionChanges = await repository.GetRevisionChangesAsync(revision, _diffParent.SelectedItem as string, Token);
            _revisionFiles.ItemsSource = _revisionChanges.Files;
            _revisionFiles.SelectedItem = _revisionChanges.Files.FirstOrDefault(file => file.Path == selected) ?? _revisionChanges.Files.FirstOrDefault();
            if (_revisionFiles.SelectedItem is GitRevisionFile file) { await ShowRevisionFileAsync(repository, file); }
            else { _diff.Text = await repository.GetCommitDiffAsync(revision, cancellationToken: Token); }
        }
        finally { _loadingCommitFiles = false; }
    }

    private async Task ShowRevisionFileAsync(GitRepository repository, GitRevisionFile file)
    {
        if (_revisionChanges is null) { return; }
        _commitContent?.ClearImage();
        if (FileContentView.IsImagePath(file.Path))
        {
            string? revision = file.IsDeleted ? _revisionChanges.BaseRevision : _revisionChanges.Revision;
            if (revision is not null && _commitContent is not null
                && (await repository.GetTreeAsync(revision, file.Path, Token)).FirstOrDefault(entry => entry.Path == file.Path)?.IsSubmodule is not true)
            {
                _commitContent.ShowImageOrHex(file.Path, await repository.GetFileBytesAsync(file.Path, revision, Token));
                return;
            }
        }
        _diff.Text = await repository.GetRevisionFileDiffAsync(_revisionChanges, file, Token);
    }

    private async Task LoadChangedFileContentAsync(GitRepository repository, GitChange change, bool staged)
    {
        _changesContent?.ClearImage();
        if (_changesContent is not null && await _changesContent.ShowChangedImageAsync(repository, change, staged, Token)) { return; }
        if (change.IsUntracked)
        {
            await using FileStream stream = File.OpenRead(Path.Combine(repository.Path, change.Path));
            byte[] bytes = new byte[(int)Math.Min(stream.Length, 2_000_000)];
            await stream.ReadExactlyAsync(bytes, Token);
            _changesDiff.Text = DisplayBytes(bytes, stream.Length > bytes.Length);
        }
        else { _changesDiff.Text = await repository.GetDiffAsync(change.Path, staged: staged, cancellationToken: Token); }
    }
}

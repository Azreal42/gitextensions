using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace GitExtensions.Linux.Desktop;

/// <summary>File operations shared by Browse and comparison hosts; every source is taken from its captured leaf.</summary>
internal sealed class FileMenuOperations
{
    private readonly Window _owner;
    private readonly Func<Window>? _workflowOwner;
    private Window Owner => _workflowOwner?.Invoke() ?? _owner;
    private readonly Func<string, Func<CancellationToken, Task>, bool, Task> _run;
    private readonly Func<FileMenuSelection, bool> _isCurrent;
    private readonly Func<ProcessStartInfo, Process?> _launch;
    private readonly CancellationToken _lifetime;
    private readonly Func<string, string> _translate;
    private readonly List<string> _temporaryDirectories = [];
    internal Func<FileMenuLeaf, (int Line, string? Encoding)> ReadingPosition { get; init; } = _ => (1, null);

    internal FileMenuOperations(Window owner, Func<string, Func<CancellationToken, Task>, bool, Task> run, Func<FileMenuSelection, bool> isCurrent,
        Func<ProcessStartInfo, Process?> launch, CancellationToken lifetime, Func<string, string>? translate = null, Func<Window>? workflowOwner = null)
    {
        _owner = owner; _workflowOwner = workflowOwner; _run = run; _isCurrent = isCurrent; _launch = launch; _lifetime = lifetime; _translate = translate ?? (text => text);
        owner.Closed += (_, _) =>
        {
            foreach (string directory in _temporaryDirectories)
            { try { Directory.Delete(directory, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
        };
    }
    internal static bool Handles(FileMenuCommand command) => command is FileMenuCommand.OpenWorkingFile or FileMenuCommand.OpenWorkingFileWith
        or FileMenuCommand.EditWorkingFile or FileMenuCommand.OpenRevisionFile or FileMenuCommand.OpenRevisionFileWith or FileMenuCommand.SaveAs
        or FileMenuCommand.ResetToFirst or FileMenuCommand.ResetToSecond or FileMenuCommand.RenameMove or FileMenuCommand.StopTracking
        or FileMenuCommand.CopyRelativePosix or FileMenuCommand.CopyRelativeNative or FileMenuCommand.CopyFullNative or FileMenuCommand.ShowInFolder
        or FileMenuCommand.FileHistory or FileMenuCommand.Blame or FileMenuCommand.DiffFirstToSecond or FileMenuCommand.DiffFirstToWorkingTree or FileMenuCommand.DiffSecondToWorkingTree;

    internal async Task ExecuteAsync(FileMenuCommand command, FileMenuSelection selection)
    {
        EnsureCurrent(selection);
        GitCommandLogProcessContext? journalContext = command is FileMenuCommand.OpenWorkingFile or FileMenuCommand.OpenWorkingFileWith
            or FileMenuCommand.OpenRevisionFile or FileMenuCommand.OpenRevisionFileWith or FileMenuCommand.ShowInFolder
            or FileMenuCommand.FileHistory or FileMenuCommand.Blame ? GitCommandLog.CaptureProcessContext() : null;
        switch (command)
        {
            case FileMenuCommand.CopyRelativePosix:
            case FileMenuCommand.CopyRelativeNative:
            case FileMenuCommand.CopyFullNative:
                if (Owner.Clipboard is { } clipboard)
                { await clipboard.SetTextAsync(string.Join(Environment.NewLine, selection.CopyPaths.Select(path => command == FileMenuCommand.CopyFullNative ? Path.Combine(selection.Repository.Path, path) : path))); }
                return;
            case FileMenuCommand.EditWorkingFile: await EditWorkingFileAsync(selection); return;
            case FileMenuCommand.OpenWorkingFile:
            case FileMenuCommand.OpenWorkingFileWith:
                string? app = command == FileMenuCommand.OpenWorkingFileWith ? await ChooseApplicationAsync(selection) : "xdg-open";
                if (app is not null) { Launch(Path.Combine(selection.Repository.Path, selection.Paths.Single()), app, selection, journalContext); }
                return;
            case FileMenuCommand.OpenRevisionFile:
            case FileMenuCommand.OpenRevisionFileWith:
                await OpenRevisionAsync(selection, command == FileMenuCommand.OpenRevisionFileWith, journalContext); return;
            case FileMenuCommand.SaveAs: await SaveAsync(selection); return;
            case FileMenuCommand.ResetToFirst:
            case FileMenuCommand.ResetToSecond: await ResetAsync(selection, command == FileMenuCommand.ResetToFirst); return;
            case FileMenuCommand.RenameMove: await RenameMoveAsync(selection); return;
            case FileMenuCommand.StopTracking:
                if (await Dialogs.ConfirmAsync(Owner, _translate("Stop tracking this file"), _translate("Stop tracking this file") + "?\n\n" + selection.Paths.Single()))
                { EnsureCurrent(selection); await RunAsync("Stop tracking this file", selection, token => selection.Repository.StopTrackingFileAsync(selection.Paths.Single(), token), true); }
                return;
            case FileMenuCommand.ShowInFolder:
                string full = Path.Combine(selection.Repository.Path, selection.CopyPaths.FirstOrDefault() ?? selection.Paths[0]);
                string directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
                while (!Directory.Exists(directory) && Path.GetDirectoryName(directory) is string parent) { directory = parent; }
                Launch(directory, "xdg-open", selection, journalContext); return;
            case FileMenuCommand.FileHistory:
                FileMenuLeaf history = selection.Leaves.First();
                if (selection.Leaves.Select(leaf => leaf.Second).Distinct().Count() != 1) { throw new InvalidOperationException("Select files from one revision for file history."); }
                await RunAsync("File history", selection, async token =>
                {
                    await FileHistoryWorkflow.OpenAsync(Owner, selection.Repository, selection.Folder ?? history.File.Path,
                        selection.Folder is not null, history.Second, token, _launch, journalContext: journalContext);
                }, false); return;
            case FileMenuCommand.Blame:
                FileMenuLeaf blame = selection.Leaves.Single();
                await RunAsync("Blame", selection, async token =>
                {
                    if (blame.Second.Kind != GitCompareRevisionKind.Commit)
                    {
                        await FileHistoryWorkflow.OpenAsync(Owner, selection.Repository, blame.File.Path, false,
                            blame.Second, token, _launch, initialBlame: true, journalContext: journalContext);
                        return;
                    }
                    var reading = ReadingPosition(blame);
                    BlameDialog dialog = new(selection.Repository, blame.File.Path, blame.Second.Kind == GitCompareRevisionKind.WorkingTree ? null : blame.Second.Revision,
                        reading.Line, fileEncodingName: reading.Encoding) { RequestedThemeVariant = Owner.ActualThemeVariant };
                    dialog.Opened += async (_, _) => await dialog.InitializeAsync(); await dialog.ShowDialog(Owner); token.ThrowIfCancellationRequested();
                }, false); return;
            case FileMenuCommand.DiffFirstToSecond:
            case FileMenuCommand.DiffFirstToWorkingTree:
            case FileMenuCommand.DiffSecondToWorkingTree:
                await RunAsync("Open with difftool", selection, async token =>
                {
                    foreach (FileMenuLeaf leaf in selection.Leaves)
                    {
                        GitCompareRevision first = command == FileMenuCommand.DiffSecondToWorkingTree ? leaf.Second
                            : leaf.First ?? throw new InvalidOperationException("The selected file has no first revision.");
                        GitCompareRevision second = command == FileMenuCommand.DiffFirstToSecond ? leaf.Second : new(Kind: GitCompareRevisionKind.WorkingTree);
                        GitComparison pair = command == FileMenuCommand.DiffFirstToSecond && leaf.Comparison is not null ? leaf.Comparison : new(first, second, [leaf.File], []);
                        await selection.Repository.OpenComparisonDiffToolAsync(pair, leaf.File, directory: false, customTool: selection.CustomDiffTool, cancellationToken: token);
                    }
                }, true); return;
            default: throw new NotSupportedException("No shared file operation for " + command);
        }
    }
    private Task RunAsync(string label, FileMenuSelection selection, Func<CancellationToken, Task> operation, bool changes)
        => _run(label, async token => { EnsureCurrent(selection); token.ThrowIfCancellationRequested(); await operation(token); }, changes);
    private void EnsureCurrent(FileMenuSelection selection)
    { _lifetime.ThrowIfCancellationRequested(); if (!_isCurrent(selection) || !_owner.IsVisible) { throw new InvalidOperationException(_translate("The file selection has changed.")); } }
    private void Launch(string path, string application, FileMenuSelection selection, GitCommandLogProcessContext? journalContext)
    { EnsureCurrent(selection); LoggedExternalProcess.Start(new ProcessStartInfo(application) { UseShellExecute = false, WorkingDirectory = selection.Repository.Path, ArgumentList = { path } },
        GitCommandLogProcessKind.OpenResource, _launch, journalContext, cancellationToken: _lifetime); }
    private async Task<string?> ChooseApplicationAsync(FileMenuSelection selection)
    {
        string[]? values = await Dialogs.PromptAsync(Owner, _translate("Open with"), (_translate("Application executable"), "/usr/bin/"));
        if (values is null) { return null; } EnsureCurrent(selection);
        string executable = values[0];
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) { throw new ArgumentException(_translate("Select an existing application executable.")); }
        return executable;
    }
    private async Task RenameMoveAsync(FileMenuSelection selection)
    {
        string path = selection.Folder ?? selection.Paths.Single();
        if (path.Length == 0) { throw new InvalidOperationException(_translate("Select a file or folder first.")); }
        string draft = path; bool attempted = false;
        try
        {
            // Keep the captured selection stable across retries; refresh after the whole interaction drains.
            await RunAsync("Rename / move", selection, async token =>
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested(); EnsureCurrent(selection);
                    Window owner = Owner;
                    SimplePromptDialog prompt = new(_translate("Rename / move"), _translate("New name"), draft)
                        { RequestedThemeVariant = owner.ActualThemeVariant };
                    using CancellationTokenRegistration close = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(prompt.Close));
                    try
                    {
                        token.ThrowIfCancellationRequested(); await prompt.ShowDialog(owner); token.ThrowIfCancellationRequested();
                        if (!prompt.WasAccepted) { return; }
                        EnsureCurrent(selection); draft = prompt.UserInput;
                    }
                    finally { prompt.Close(); }
                    if (string.IsNullOrWhiteSpace(draft) || draft == path) { continue; }
                    try
                    {
                        attempted = true;
                        await selection.Repository.MoveTrackedFileAsync(path, draft, token);
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        token.ThrowIfCancellationRequested(); EnsureCurrent(selection);
                        await Dialogs.ShowMessageAsync(owner, _translate("Error"), error.Message, false, token);
                        token.ThrowIfCancellationRequested(); EnsureCurrent(selection);
                    }
                }
            }, false);
        }
        finally
        {
            if (attempted && !_lifetime.IsCancellationRequested && _owner.IsVisible && _isCurrent(selection))
            { await _run("Refresh after rename / move", _ => Task.CompletedTask, true); }
        }
    }
    private async Task EditWorkingFileAsync(FileMenuSelection selection)
    {
        FileMenuLeaf leaf = selection.Leaves.Single();
        string path = Path.GetFullPath(selection.Paths.Single(), selection.Repository.Path);
        var reading = ReadingPosition(leaf); bool changed = false;
        try
        {
            await RunAsync("Edit file", selection, async token =>
            {
                await FileEditorWorkflow.OpenAsync(Owner, selection.Repository, path, reading.Line, reading.Encoding,
                    cancellationToken: token, fileChanged: () => changed = true);
            }, false);
        }
        finally
        {
            if (changed && !_lifetime.IsCancellationRequested && _owner.IsVisible && _isCurrent(selection))
            { await _run("Refresh after file edit", _ => Task.CompletedTask, true); }
        }
    }
    private async Task OpenRevisionAsync(FileMenuSelection selection, bool withApplication, GitCommandLogProcessContext? journalContext)
    {
        string? application = withApplication ? await ChooseApplicationAsync(selection) : "xdg-open";
        if (application is null) { return; } EnsureCurrent(selection);
        FileMenuLeaf leaf = selection.Leaves.Single();
        await RunAsync("Open this revision", selection, async token =>
        {
            byte[] bytes = await selection.Repository.GetComparisonFileBytesAsync(leaf.Second, leaf.File.Path, token);
            EnsureCurrent(selection); token.ThrowIfCancellationRequested();
            string directory = Path.Combine(Path.GetTempPath(), "gitextensions-file-" + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
            else { Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            _temporaryDirectories.Add(directory);
            string file = Path.Combine(directory, Path.GetFileName(leaf.File.Path));
            await File.WriteAllBytesAsync(file, bytes, token);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            Launch(file, application, selection, journalContext);
        }, false);
    }
    private async Task SaveAsync(FileMenuSelection selection)
    {
        if (selection.Leaves.Count == 1)
        {
            FileMenuLeaf leaf = selection.Leaves[0];
            IStorageFile? target = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = _translate("Save selected as..."), SuggestedFileName = Path.GetFileName(leaf.File.Path), ShowOverwritePrompt = true });
            if (target is null) { return; } EnsureCurrent(selection);
            await RunAsync("Save selected as", selection, async token =>
            {
                byte[] bytes = await selection.Repository.GetComparisonFileBytesAsync(leaf.Second, leaf.File.Path, token);
                EnsureCurrent(selection); token.ThrowIfCancellationRequested();
                await using Stream output = await target.OpenWriteAsync(); output.SetLength(0); await output.WriteAsync(bytes, token);
            }, false);
        }
        else
        {
            IReadOnlyList<IStorageFolder> folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = _translate("Save selected as..."), AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is not string directory) { return; } EnsureCurrent(selection);
            await RunAsync("Save selected as", selection, async token =>
            {
                HashSet<string> destinations = new(StringComparer.Ordinal);
                foreach (FileMenuLeaf leaf in selection.Leaves)
                {
                    string destination = Path.Combine(directory, leaf.File.Path);
                    if (!destinations.Add(destination) || File.Exists(destination) || Directory.Exists(destination)) { throw new IOException("Destination already exists: " + destination); }
                    for (string? parent = Path.GetDirectoryName(destination); parent is not null && parent != directory; parent = Path.GetDirectoryName(parent))
                    { if (new DirectoryInfo(parent).LinkTarget is not null) { throw new IOException("Destination is beneath a symbolic link: " + destination); } }
                }
                foreach (FileMenuLeaf leaf in selection.Leaves)
                {
                    byte[] bytes = await selection.Repository.GetComparisonFileBytesAsync(leaf.Second, leaf.File.Path, token);
                    EnsureCurrent(selection); token.ThrowIfCancellationRequested();
                    string destination = Path.Combine(directory, leaf.File.Path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using FileStream output = new(destination, FileMode.CreateNew); await output.WriteAsync(bytes, token);
                }
            }, false);
        }
    }
    private static GitRevisionFile Invert(GitRevisionFile file) => file with
    {
        Status = file.Status switch { 'A' => 'D', 'D' => 'A', _ => file.Status },
        Path = file.Status == 'R' && file.OriginalPath is not null ? file.OriginalPath : file.Path,
        OriginalPath = file.Status == 'R' ? file.Path : file.OriginalPath
    };
    private async Task ResetAsync(FileMenuSelection selection, bool first)
    {
        Dictionary<string, GitCompareRevision> sources = new(StringComparer.Ordinal);
        foreach (FileMenuLeaf leaf in selection.Leaves)
        {
            GitCompareRevision? source = first ? leaf.First : leaf.Second;
            if (source is null) { continue; }
            foreach (string path in leaf.File.OriginalPath is string original ? new[] { leaf.File.Path, original } : [leaf.File.Path])
            {
                if (sources.TryGetValue(path, out GitCompareRevision? previous) && previous != source)
                { throw new InvalidOperationException(_translate("The same path is selected from different reset sources:") + " " + path); }
                sources[path] = source;
            }
        }
        // Partition by endpoint: applying every selected path to each group would reproduce the Windows cross-product bug.
        var groups = selection.Leaves.GroupBy(leaf => first ? leaf.First : leaf.Second)
            .Where(group => group.Key?.Kind == GitCompareRevisionKind.Commit
                || first && group.Key?.Kind == GitCompareRevisionKind.Index && selection.Leaves.All(leaf => leaf.Second.Kind == GitCompareRevisionKind.WorkingTree)).ToArray();
        if (groups.Length == 0) { throw new InvalidOperationException(_translate("The selected files have no reset source.")); }
        await RunAsync("Reset files", selection, async token =>
        {
            bool present = false, missing = false;
            foreach (var group in groups)
            {
                HashSet<string> paths = group.Key!.Kind == GitCompareRevisionKind.Index
                    ? (await selection.Repository.GetIndexFilePathsAsync(token)).ToHashSet(StringComparer.Ordinal)
                    : (await selection.Repository.GetTreeAsync(group.Key.Revision, cancellationToken: token)).Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
                string[] selected = group.SelectMany(leaf => leaf.File.OriginalPath is string original ? new[] { leaf.File.Path, original } : [leaf.File.Path]).Distinct(StringComparer.Ordinal).ToArray();
                present |= selected.Any(paths.Contains); missing |= selected.Any(path => !paths.Contains(path));
            }
            EnsureCurrent(selection); token.ThrowIfCancellationRequested();
            string sourceDescription = FileMenuRevisionDescription.Describe(groups.Select(group => group.Key!), _translate);
            ResetChangesDialog dialog = new(present, missing, _translate("Are you sure you want to reset all selected files to") + " " + (first ? _translate("First: A ") : _translate("Second: B ")) + sourceDescription + "?\n\n" + string.Join('\n', selection.ResetPaths)) { RequestedThemeVariant = Owner.ActualThemeVariant };
            ResetChangesDialog.Decision decision = await dialog.ShowDialog<ResetChangesDialog.Decision>(Owner);
            if (decision == ResetChangesDialog.Decision.Cancel) { return; }
            EnsureCurrent(selection); token.ThrowIfCancellationRequested();
            foreach (var group in groups)
            {
                GitRevisionFile[] files = group.Select(leaf => first ? leaf.File : Invert(leaf.File)).ToArray();
                await selection.Repository.RestoreComparisonFilesAsync(group.Key!, files, decision == ResetChangesDialog.Decision.ResetAndDelete, token);
            }
        }, true);
    }
}

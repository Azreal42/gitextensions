using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

/// <summary>Repository effects shared by the Browse and Compare file lists.</summary>
internal sealed class FileMenuAdditionalOperations
{
    private readonly Window _owner;
    private readonly Func<Window>? _workflowOwner;
    private Window Owner => _workflowOwner?.Invoke() ?? _owner;
    private readonly Func<string, Func<CancellationToken, Task>, bool, Task> _run;
    private readonly Func<FileMenuSelection, bool> _isCurrent;
    private readonly Func<ProcessStartInfo, Process?> _launch;
    private readonly CancellationToken _lifetime;
    private readonly string _preferences = Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "file-menu-preferences.json");
    private string? _repositoryPath;
    private IReadOnlyList<string> _tools = [];
    // Windows has one remembered file shared between forms, cleared on repository change.
    private static string? _rememberedRepository;
    private static FileMenuLeaf? _remembered;
    internal FileMenuLeaf? RememberedLeaf => _rememberedRepository == _repositoryPath ? _remembered : null;
    internal IReadOnlyList<string> CustomDiffTools => ShowAvailableDiffTools() ? _tools : [];
    internal FileMenuLeaf? GetRemembered(GitRepository repository) { ObserveRepository(repository); return RememberedLeaf; }
    internal IReadOnlyList<string> GetCustomDiffTools(GitRepository repository) { ObserveRepository(repository); return CustomDiffTools; }

    private void ObserveRepository(GitRepository repository)
    {
        if (_repositoryPath == repository.Path) { return; }
        _repositoryPath = repository.Path; _tools = [];
        if (_rememberedRepository != repository.Path) { _remembered = null; _rememberedRepository = repository.Path; }
    }

    internal FileMenuAdditionalOperations(Window owner,
        Func<string, Func<CancellationToken, Task>, bool, Task> run, Func<FileMenuSelection, bool> isCurrent,
        Func<ProcessStartInfo, Process?> launch, CancellationToken lifetime, Func<Window>? workflowOwner = null)
    { _owner = owner; _run = run; _isCurrent = isCurrent; _launch = launch; _lifetime = lifetime; _workflowOwner = workflowOwner; }

    internal async Task RefreshAsync(GitRepository repository, CancellationToken token)
    {
        IReadOnlyList<string> tools = await repository.GetCustomDiffToolsAsync(token);
        token.ThrowIfCancellationRequested();
        ObserveRepository(repository); _tools = tools;
    }

    internal static bool Handles(FileMenuCommand command) => command is FileMenuCommand.Stage or FileMenuCommand.Unstage
        or FileMenuCommand.DeleteFile or FileMenuCommand.AddToIgnore or FileMenuCommand.AddToLocalExclude
        or FileMenuCommand.ToggleSkipWorktree or FileMenuCommand.ToggleAssumeUnchanged
        or FileMenuCommand.OpenSubmodule or FileMenuCommand.UpdateSubmodule or FileMenuCommand.ResetSubmodule
        or FileMenuCommand.StashSubmodule or FileMenuCommand.CommitSubmodule
        or FileMenuCommand.InteractiveAdd or FileMenuCommand.ResetChunk
        or FileMenuCommand.DiffTwoSelected or FileMenuCommand.DiffWithRemembered
        or FileMenuCommand.RememberFirst or FileMenuCommand.RememberSecond or FileMenuCommand.DisableCustomDiffTools;

    internal async Task ExecuteAsync(FileMenuCommand command, FileMenuSelection selection)
    {
        EnsureCurrent(selection);
        ObserveRepository(selection.Repository);
        if (selection.IsDisplayOnly) { throw new InvalidOperationException("This comparison is for display only."); }
        switch (command)
        {
            case FileMenuCommand.Stage:
            case FileMenuCommand.Unstage:
                FileMenuStage stage = command == FileMenuCommand.Stage ? FileMenuStage.WorkingTree : FileMenuStage.Index;
                string[] paths = selection.Leaves.Where(leaf => leaf.Stage == stage).SelectMany(leaf => Paths(leaf.File)).Distinct(StringComparer.Ordinal).ToArray();
                if (paths.Length == 0) { throw new InvalidOperationException("The selected files have no matching staged state."); }
                await RunAsync(command == FileMenuCommand.Stage ? "Stage selected" : "Unstage selected", selection,
                    token => command == FileMenuCommand.Stage ? selection.Repository.StageAsync(paths, token) : selection.Repository.UnstageAsync(paths, token), true);
                return;
            case FileMenuCommand.DeleteFile:
                if (await Dialogs.ConfirmAsync(Owner, "Delete file", "Delete these working directory files?\n\n" + string.Join('\n', selection.Paths)))
                { EnsureCurrent(selection); await RunAsync("Delete file", selection, token => selection.Repository.DeleteWorkingTreeFilesAsync(selection.Paths, token), true); }
                return;
            case FileMenuCommand.AddToIgnore:
            case FileMenuCommand.AddToLocalExclude:
                string[] patterns = selection.Folder is { Length: > 0 } folder ? [GitIgnorePatterns.ForPath(folder, directory: true)]
                    : selection.Paths.Select(path => GitIgnorePatterns.ForPath(path)).ToArray();
                await RunAsync("Add to .gitignore", selection, async token =>
                {
                    AddToGitIgnoreDialog dialog = new(selection.Repository, command == FileMenuCommand.AddToLocalExclude, patterns)
                        { RequestedThemeVariant = Owner.ActualThemeVariant };
                    dialog.Opened += async (_, _) => await dialog.InitializeAsync();
                    await dialog.ShowDialog(Owner); token.ThrowIfCancellationRequested();
                }, true);
                return;
            case FileMenuCommand.ToggleSkipWorktree:
            case FileMenuCommand.ToggleAssumeUnchanged:
                string[] tracked = selection.Leaves.Where(leaf => leaf.IsTracked).Select(leaf => leaf.File.Path).Distinct(StringComparer.Ordinal).ToArray();
                if (tracked.Length == 0) { throw new InvalidOperationException("Select tracked files first."); }
                await RunAsync("Update index flags", selection, token => command == FileMenuCommand.ToggleSkipWorktree
                    ? selection.Repository.SetSkipWorktreeAsync(tracked, !selection.Leaves.Any(leaf => leaf.IsSkipWorktree), token)
                    : selection.Repository.SetAssumeUnchangedAsync(tracked, !selection.Leaves.Any(leaf => leaf.IsAssumeUnchanged), token), true);
                return;
            case FileMenuCommand.OpenSubmodule:
            case FileMenuCommand.UpdateSubmodule:
            case FileMenuCommand.ResetSubmodule:
            case FileMenuCommand.StashSubmodule:
            case FileMenuCommand.CommitSubmodule:
                await SubmodulesAsync(command, selection); return;
            case FileMenuCommand.InteractiveAdd:
            case FileMenuCommand.ResetChunk:
                await RunAsync(command == FileMenuCommand.InteractiveAdd ? "Interactive add" : "Reset chunk of file", selection, async token =>
                {
                    IReadOnlyList<string> launchedArguments = [];
                    GitResult result = await selection.Repository.RunInteractiveFileActionAsync(command == FileMenuCommand.InteractiveAdd
                        ? GitInteractiveFileAction.AddPatch : GitInteractiveFileAction.ResetPatch, selection.Paths,
                        (arguments, cancellation) =>
                        {
                            launchedArguments = arguments;
                            return InteractiveFileConsoleLauncher.RunAsync(Owner, selection.Repository.Path,
                                selection.Repository.GitExecutable, arguments, cancellation);
                        }, token);
                    if (!result.Success) { throw new GitCommandException(launchedArguments, result); }
                }, true); return;
            case FileMenuCommand.RememberFirst:
            case FileMenuCommand.RememberSecond:
                FileMenuLeaf leaf = selection.Leaves.Single();
                if (leaf.IsSubmodule) { throw new InvalidOperationException("Select a file first."); }
                if (command == FileMenuCommand.RememberFirst)
                {
                    GitCompareRevision first = leaf.First ?? throw new InvalidOperationException("The selected file has no first revision.");
                    if (first.Kind == GitCompareRevisionKind.WorkingTree || leaf.File.Status == 'A')
                    { throw new InvalidOperationException("The first revision has no file to remember."); }
                    leaf = leaf with { File = leaf.File with { Path = leaf.File.OriginalPath ?? leaf.File.Path, OriginalPath = null }, Second = first, First = null, Comparison = null };
                }
                _rememberedRepository = selection.Repository.Path; _repositoryPath = selection.Repository.Path; _remembered = leaf;
                return;
            case FileMenuCommand.DiffTwoSelected:
            case FileMenuCommand.DiffWithRemembered:
                FileMenuLeaf firstLeaf, secondLeaf;
                if (command == FileMenuCommand.DiffTwoSelected)
                {
                    if (selection.Leaves.Count != 2) { throw new InvalidOperationException("Select exactly two files."); }
                    int firstIndex = selection.FocusedLeaf == selection.Leaves[0] ? 1 : 0;
                    firstLeaf = selection.Leaves[firstIndex]; secondLeaf = selection.Leaves[1 - firstIndex];
                }
                else
                {
                    firstLeaf = RememberedLeaf ?? throw new InvalidOperationException("Remember a file from this repository first.");
                    secondLeaf = selection.Leaves.Single();
                }
                if (firstLeaf.IsSubmodule || secondLeaf.IsSubmodule) { throw new InvalidOperationException("Select files, not submodules."); }
                await RunAsync("Open with difftool", selection, token => selection.Repository.OpenFileDiffToolAsync(
                    new(firstLeaf.Second, firstLeaf.File.Path), new(secondLeaf.Second, secondLeaf.File.Path), selection.CustomDiffTool, token), true);
                return;
            case FileMenuCommand.DisableCustomDiffTools:
                Directory.CreateDirectory(Path.GetDirectoryName(_preferences)!);
                string temporary = _preferences + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllTextAsync(temporary, "{\"ShowAvailableDiffTools\":false}", _lifetime); File.Move(temporary, _preferences, overwrite: true); }
                finally { File.Delete(temporary); }
                return;
            default: throw new NotSupportedException("No shared additional file operation for " + command);
        }
    }

    private bool ShowAvailableDiffTools()
    {
        try
        { return !File.Exists(_preferences) || (JsonSerializer.Deserialize<FileMenuPreferences>(File.ReadAllText(_preferences)) ?? new()).ShowAvailableDiffTools; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return true; }
    }
    private sealed record FileMenuPreferences(bool ShowAvailableDiffTools = true);
    private static IEnumerable<string> Paths(GitRevisionFile file) => file.Status == 'R' && file.OriginalPath is string old ? [old, file.Path] : [file.Path];
    private void EnsureCurrent(FileMenuSelection selection)
    { _lifetime.ThrowIfCancellationRequested(); if (!_isCurrent(selection) || !_owner.IsVisible) { throw new InvalidOperationException("The file selection has changed."); } }
    private Task RunAsync(string label, FileMenuSelection selection, Func<CancellationToken, Task> action, bool changes)
        => _run(label, async token => { EnsureCurrent(selection); token.ThrowIfCancellationRequested(); await action(token); }, changes);

    private async Task SubmodulesAsync(FileMenuCommand command, FileMenuSelection selection)
    {
        string[] modulePaths = selection.Leaves.Where(leaf => leaf.IsSubmodule).Select(leaf => leaf.File.Path).Distinct(StringComparer.Ordinal).ToArray();
        if (modulePaths.Length == 0) { throw new InvalidOperationException("Select submodules first."); }
        if (command == FileMenuCommand.OpenSubmodule && (selection.Leaves.Count != 1 || modulePaths.Length != 1)) { throw new InvalidOperationException("Select one submodule."); }
        await RunAsync("Submodules", selection, async token =>
        {
            IReadOnlyList<GitSubmoduleSnapshot> snapshots = await selection.Repository.GetSubmoduleSnapshotsAsync(token);
            GitSubmoduleSnapshot[] selected = modulePaths.Select(path => snapshots.SingleOrDefault(item => item.Path == path)
                ?? throw new InvalidOperationException("The submodule registration changed. Refresh the file list.")).ToArray();
            ResetChangesDialog.Decision reset = ResetChangesDialog.Decision.Cancel;
            if (command == FileMenuCommand.ResetSubmodule)
            {
                ResetChangesDialog confirmation = new(true, true, "Reset changes in the selected submodules?\n\n" + string.Join('\n', modulePaths))
                    { RequestedThemeVariant = Owner.ActualThemeVariant };
                reset = await confirmation.ShowDialog<ResetChangesDialog.Decision>(Owner);
                if (reset == ResetChangesDialog.Decision.Cancel) { return; }
                EnsureCurrent(selection);
            }
            // Validate every child before the first effect. Never let Git discover the superproject as a fallback.
            Dictionary<string, GitRepository> children = [];
            if (command != FileMenuCommand.UpdateSubmodule)
            { foreach (GitSubmoduleSnapshot item in selected) { children[item.Path] = await selection.Repository.OpenSubmoduleRepositoryAsync(item, token); } }
            foreach (GitSubmoduleSnapshot item in selected)
            {
                token.ThrowIfCancellationRequested(); EnsureCurrent(selection);
                if (command == FileMenuCommand.UpdateSubmodule)
                {
                    GitProcessDialogResult outcome = await GitProcessDialog.ShowAsync(Owner, selection.Repository.Path,
                        "git submodule update --init --recursive -- " + item.Path, true,
                        (cancellation, output) => selection.Repository.UpdateSubmoduleAsync(item, cancellation, output), token);
                    if (outcome.Error is not null) { throw outcome.Error; }
                    if (outcome.WasCancelled) { throw new OperationCanceledException(token); }
                    if (outcome.Result is { Success: false } failed) { throw new GitCommandException(["submodule", "update", "--init", "--recursive", "--", item.Path], failed); }
                    if (!outcome.Success) { throw new InvalidOperationException("The submodule update did not complete."); }
                    continue;
                }
                GitRepository child = children[item.Path];
                switch (command)
                {
                    case FileMenuCommand.OpenSubmodule:
                        using (_launch(BrowseApplicationLauncher.CreateStartInfo(child.Path))
                            ?? throw new InvalidOperationException("The Git Extensions browser could not be started.")) { }
                        break;
                    case FileMenuCommand.ResetSubmodule:
                        await child.ResetAsync("HEAD", GitResetMode.Hard, token);
                        if (reset == ResetChangesDialog.Decision.ResetAndDelete)
                        {
                            GitCleanResult cleaned = await child.CleanWorkingDirectoryAsync(cancellationToken: token);
                            if (!cleaned.Success) { throw new InvalidOperationException("Cleaning the submodule did not complete."); }
                        }
                        break;
                    case FileMenuCommand.StashSubmodule:
                        StashPreferences preferences = new StashPreferencesStore().Load();
                        await child.StashAsync(new GitStashSaveOptions(IncludeUntracked: preferences.IncludeUntracked), token); break;
                    case FileMenuCommand.CommitSubmodule:
                        CommitDialog commit = new(child) { RequestedThemeVariant = Owner.ActualThemeVariant };
                        commit.Opened += async (_, _) => await commit.InitializeAsync(); await commit.ShowDialog(Owner); break;
                }
            }
        }, command != FileMenuCommand.OpenSubmodule);
    }
}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private sealed record RevisionPointerContext(GitRepository Repository, string CommitHash, GitReference Reference, KeyModifiers Modifiers);
    private RevisionPointerContext? _pendingRevisionPointer, _revisionPointerContext;
    private object[] _revisionMenuCommands = [];
    private readonly HashSet<object> _revisionLayoutSeparators = new(ReferenceEqualityComparer.Instance);
    private MenuItem? _revisionOtherActions, _revisionPushBranches, _revisionMergeBranches, _revisionDeleteTags, _revisionSelectInLeftPanel;
    private Func<bool, bool, Task>? _contextRebaseCommand, _closedContextRebaseCommand;
    private OperationFocus? _revisionMenuFocus;
    private long _revisionMenuFocusVersion;
    private bool _focusedRevisionMenu;
    private Func<Task>? _selectRevisionInLeftPanel;

    private void InitializeFocusedRevisionMenu(object[] commands)
    {
        _revisionMenuCommands = commands;
        InitializeKeyboardContextGesture();
        _revisionOtherActions = new() { Name = "RevisionOtherActions", Header = RevisionHeader("tsmiOtherActions", "&Other actions") };
        _commits.AddHandler(KeyDownEvent, (_, _) => _pendingRevisionPointer = null, RoutingStrategies.Tunnel);
        _commits.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (!args.GetCurrentPoint(_commits).Properties.IsRightButtonPressed) { _pendingRevisionPointer = null; }
        }, RoutingStrategies.Tunnel);
        _commits.DetachedFromVisualTree += (_, _) => ClearRevisionPointerContext();
        Closed += (_, _) => ClearRevisionPointerContext();
    }

    private void CaptureRevisionPointerContext(PointerPressedEventArgs args, GitCommit? commit, Control source)
    {
        _pendingRevisionPointer = null;
        if (commit is null || _repository is not GitRepository repository) { return; }
        GitReference? hit = source.GetSelfAndVisualAncestors().OfType<Control>()
            .FirstOrDefault(control => control.Name == "RevisionReference")?.Tag as GitReference;
        if (hit is not null && _navigationReferences.FirstOrDefault(reference => reference.Name == hit.Name
            && reference.RevisionHash == commit.Hash && reference.SymbolicTarget is null) is { } current)
        { _pendingRevisionPointer = new(repository, commit.Hash, current, args.KeyModifiers); }
    }

    private GitReference? CurrentRevisionContextReference()
    {
        if (_revisionPointerContext is not { } context || context.Repository != _repository
            || (_commits.SelectedItem as GitCommit)?.Hash != context.CommitHash) { return null; }
        return _navigationReferences.FirstOrDefault(reference => reference.Name == context.Reference.Name
            && reference.RevisionHash == context.CommitHash && reference.SymbolicTarget is null);
    }

    private void BeginRevisionContextMenu()
    {
        _closedContextRebaseCommand = null;
        _revisionMenuFocusVersion++;
        _revisionPointerContext = _pendingRevisionPointer; _pendingRevisionPointer = null;
        GitReference? reference = CurrentRevisionContextReference();
        if (reference is null) { _revisionPointerContext = null; }
        KeyModifiers modifiers = _revisionPointerContext?.Modifiers ?? KeyModifiers.None;
        bool focused = reference is not null && (modifiers.HasFlag(KeyModifiers.Control)
            || !(modifiers.HasFlag(KeyModifiers.Shift) || AdvancedOptionsVisibility.Capture()));
        _revisionMenuFocus = new(_repository, null, (_commits.SelectedItem as GitCommit)?.Hash);
        _focusedRevisionMenu = focused;
    }

    private void EndRevisionContextMenu()
    {
        OperationFocus? focus = _revisionMenuFocus; _revisionMenuFocus = null;
        long focusVersion = _revisionMenuFocusVersion;
        _closedContextRebaseCommand = _contextRebaseCommand;
        ClearRevisionPointerContext();
        QueuePendingRevisionDetails();
        Dispatcher.UIThread.Post(() =>
        {
            if (_revisionContext?.IsOpen != true && focusVersion == _revisionMenuFocusVersion) { RestoreOperationFocus(focus); }
        }, DispatcherPriority.Loaded);
    }

    private void ClearRevisionPointerContext()
    { _pendingRevisionPointer = _revisionPointerContext = null; }

    private void EnsureRevisionContextRepository()
    {
        if (_revisionPointerContext is { } context && (context.Repository != _repository
            || (_commits.SelectedItem as GitCommit)?.Hash != context.CommitHash))
        { ClearRevisionPointerContext(); _revisionContext?.Close(); }
        if (_pendingRevisionPointer is { } pending && pending.Repository != _repository) { _pendingRevisionPointer = null; }
    }

    private void ComposeRevisionContextMenu(bool focused)
    {
        if (_revisionContext is null || _revisionOtherActions is null) { return; }
        HashSet<object> owned = new(_revisionMenuCommands, ReferenceEqualityComparer.Instance) { _revisionOtherActions };
        // Scripts and other contributors retain their controls and order across reshaping.
        object[] additions = _revisionContext.Items.Cast<object>().Where(item => !owned.Contains(item) && !_revisionLayoutSeparators.Contains(item)).ToArray();
        _revisionLayoutSeparators.Clear();
        _revisionContext.ItemsSource = Array.Empty<object>();
        _revisionOtherActions.ItemsSource = Array.Empty<object>();
        if (!focused)
        {
            _revisionContext.ItemsSource = NormalizeSeparators(_revisionMenuCommands).Concat(additions).ToArray();
            ArrangeRevisionContributions(); return;
        }
        bool Advanced(object item) => item is MenuItem menu && menu.Name is "RevisionResetAnother" or "RevisionCreateTag"
            or "RevisionCheckout" or "RevisionRevert" or "RevisionCherryPick" or "RevisionArchive" or "HistoryAdvancedMenu"
            or "RevisionNavigate" or "RevisionView" or "ShowBuildPage" or "BuildServerPullRequestPage";
        List<object> primary = [], advanced = [];
        foreach (object item in _revisionMenuCommands)
        {
            if (item is Separator)
            {
                Separator separator = new();
                _revisionLayoutSeparators.Add(separator);
                // Windows classifies separators as well as commands. Only the
                // commit and Navigate boundaries belong in Other actions.
                (item is Control { Name: "RevisionCommitSeparator" or "RevisionNavigateSeparator" } ? advanced : primary).Add(separator);
            }
            else { (Advanced(item) ? advanced : primary).Add(item); }
        }
        _revisionOtherActions.ItemsSource = NormalizeSeparators(advanced);
        _revisionContext.ItemsSource = NormalizeSeparators(primary).Concat(additions).Append(_revisionOtherActions).ToArray();
    }


    private void ArrangeRevisionContributions()
    {
        if (_revisionContext is null || _revisionOtherActions is null) { return; }
        object[] current = _revisionContext.Items.Cast<object>().ToArray();
        object[] arranged;
        if (current.Contains(_revisionOtherActions))
        { arranged = current.Where(item => !ReferenceEquals(item, _revisionOtherActions)).Append(_revisionOtherActions).ToArray(); }
        else
        {
            HashSet<object> scripts = new(_revisionScriptItems, ReferenceEqualityComparer.Instance);
            object[] contribution = current.Where(scripts.Contains).ToArray();
            List<object> commands = current.Where(item => !scripts.Contains(item)).ToList();
            int ci = commands.FindIndex(item => item is MenuItem { Name: "ShowBuildPage" or "BuildServerPullRequestPage" });
            commands.InsertRange(ci < 0 ? commands.Count : ci, contribution);
            arranged = commands.ToArray();
        }
        if (!current.SequenceEqual(arranged, ReferenceEqualityComparer.Instance)) { _revisionContext.ItemsSource = arranged; }
    }

    private static object[] NormalizeSeparators(IEnumerable<object> source)
    {
        List<object> result = [];
        Separator? pending = null;
        bool visible = false;
        foreach (object item in source)
        {
            if (item is Separator separator) { pending ??= separator; continue; }
            if (item is Control { IsVisible: false }) { result.Add(item); continue; }
            if (visible && pending is not null) { result.Add(pending); }
            result.Add(item); visible = true; pending = null;
        }
        return result.ToArray();
    }

    private void BindContextRebaseCommand()
    {
        GitRepository? repository = _repository;
        GitReference? reference = CurrentRevisionContextReference();
        string? hash = (_commits.SelectedItem as GitCommit)?.Hash;
        // Menu dismissal may precede Click. The command owns its immutable target;
        // clearing the next opening's hit-test context must not change this action.
        _contextRebaseCommand = (interactive, immediate) =>
        {
            if (repository != _repository || hash != (_commits.SelectedItem as GitCommit)?.Hash || reference?.IsCurrent == true) { return Task.CompletedTask; }
            return OpenHistoryRebaseAsync(interactive, immediate, reference?.Name);
        };
    }

    private Task ContextHistoryRebaseAsync(bool interactive, bool immediate = false)
    {
        // Closing can run before Click, and an intervening async refresh rebinds
        // the open-menu delegate. Keep the just-closed invocation until consumed
        // or a new opening replaces it; it still guards repository and commit.
        Func<bool, bool, Task>? command = _closedContextRebaseCommand ?? _contextRebaseCommand;
        _closedContextRebaseCommand = null;
        return command is not null ? command(interactive, immediate) : OpenHistoryRebaseAsync(interactive, immediate);
    }

    private Task MergeRevisionReferenceAsync(GitRepository repository, string revision)
        => ShowRepositoryDialogAsync(new MergeBranchDialog(repository, revision, ResolveWorkflowConflictsAsync,
            (owner, token) => AfterSubmoduleUpdateAsync(repository, owner, token)));

    private Task PushRevisionReferenceAsync(GitRepository repository, GitReference reference)
        => ShowRepositoryDialogAsync(new PushDialog(repository,
            (owner, token) => AfterSubmoduleUpdateAsync(repository, owner, token), initialBranch: reference.ShortName));

    private Task SelectRevisionReferenceInLeftPanelAsync(GitReference reference)
    {
        GitRepository? repository = _repository;
        // Also invalidate a return-to-grid callback that Closed already queued.
        long focusVersion = ++_revisionMenuFocusVersion;
        _revisionMenuFocus = null;
        string group = reference.IsTag ? "Tags" : reference.IsRemote ? "Remotes" : "Branches";
        if (_navigationPanel?.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .FirstOrDefault(toggle => toggle.Name == "NavigationShow" + group) is { } show) { show.IsChecked = true; }
        _navigationGroups[group] = true;
        _navigationSearch.Text = "";
        RebuildNavigation();
        static TreeViewItem[]? Path(IEnumerable<TreeViewItem> nodes, string name)
        {
            foreach (TreeViewItem node in nodes)
            {
                if (node.Tag is GitReference candidate && candidate.Name == name) { return [node]; }
                if (Path(node.Items.OfType<TreeViewItem>(), name) is { } child) { return [node, .. child]; }
            }
            return null;
        }
        if (Path(_navigation.Items.OfType<TreeViewItem>(), reference.Name) is { Length: > 0 } path)
        {
            foreach (TreeViewItem ancestor in path[..^1]) { ancestor.IsExpanded = true; }
            _navigation.UpdateLayout();
            TreeViewItem target = path[^1];
            // This command reveals a node; it does not apply the tree's usual branch
            // filter or replace the commit grid/selection as a normal tree click does.
            bool updating = _updatingNavigation; _updatingNavigation = true;
            try { _navigation.SelectedItem = target; _navigationSelectionKey = "ref:" + reference.Name; }
            finally { _updatingNavigation = updating; }
            target.BringIntoView(); target.Focus();
            Dispatcher.UIThread.Post(() =>
            {
                if (focusVersion == _revisionMenuFocusVersion && repository == _repository && !_busy && IsVisible && !OwnedWindows.Any(window => window.IsVisible)
                    && target.IsEffectivelyVisible) { target.Focus(); }
            }, DispatcherPriority.Loaded);
        }
        return Task.CompletedTask;
    }
}

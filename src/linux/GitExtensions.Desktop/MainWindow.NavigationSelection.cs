using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    // RepoObjectsTree/NodeBase keep underlined nodes independently of the
    // native tree's active row. Arrow navigation must not modify this set.
    private readonly HashSet<string> _navigationMarkedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _navigationSelectionChildren = new(StringComparer.Ordinal);
    private GitRepository? _navigationSelectionRepository;
    private long _navigationMarkedVersion;

    private void ConfigureNavigationSelection()
    {
        // Run on the ancestor before TreeView's native Ctrl-single toggle can
        // clear the cursor; a same-node Ctrl click must change only its mark.
        AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (_busy || _refreshing || _updatingNavigation || _repository is null || _lifetime.IsCancellationRequested || IsBranchFilterOwnerClosing
                || args.Source is not Avalonia.Visual source || !source.GetSelfAndVisualAncestors().Contains(_navigation)) { return; }
            // Capture before this handler changes SelectedItem or focus. A
            // separate handler can run later on the same routed-event surface.
            _leftPanelReferenceModifiers = args.KeyModifiers;
            TreeViewItem? node = source.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
            if (node is null || !_navigationKeys.TryGetValue(node, out string? key)
                || source.GetSelfAndVisualAncestors().TakeWhile(ancestor => ancestor != node).Any(ancestor => ancestor is ToggleButton)) { return; }
            var point = args.GetCurrentPoint(_navigation);
            bool right = point.Properties.IsRightButtonPressed;
            if (!right && !point.Properties.IsLeftButtonPressed) { return; }
            if (!right || !_navigationMarkedKeys.Contains(key))
            {
                SelectNavigationNodes(node, args.KeyModifiers.HasFlag(KeyModifiers.Control), args.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }
            if (right)
            {
                // Choose a context without GoToRef; an already underlined row
                // preserves every other marked node, exactly as Windows does.
                bool updating = _updatingNavigation; _updatingNavigation = true;
                try { _navigation.SelectedItem = node; node.Focus(NavigationMethod.Pointer); }
                finally { _updatingNavigation = updating; }
            }
            else if (args.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                // Avalonia's Single mode otherwise Ctrl-toggles the active row
                // to null. Ctrl belongs to the underline set, not this cursor.
                _navigation.SelectedItem = node;
                node.Focus(NavigationMethod.Pointer);
                args.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    private void SelectNavigationNodes(TreeViewItem node, bool multiple, bool includingDescendants)
    {
        if (!_navigationKeys.TryGetValue(node, out string? key)) { return; }
        HashSet<string> previous = new(_navigationMarkedKeys, StringComparer.Ordinal);
        if (multiple)
        {
            bool select = !_navigationMarkedKeys.Contains(key);
            void Set(string selectedKey)
            {
                if (select) { _navigationMarkedKeys.Add(selectedKey); } else { _navigationMarkedKeys.Remove(selectedKey); }
                if (includingDescendants && _navigationSelectionChildren.TryGetValue(selectedKey, out string[]? children))
                { foreach (string child in children) { Set(child); } }
            }
            Set(key);
        }
        else
        {
            _navigationMarkedKeys.ExceptWith(NavigationSelectionKeys()); _navigationMarkedKeys.Add(key);
        }
        if (!previous.SetEquals(_navigationMarkedKeys)) { _navigationMarkedVersion++; }
        ApplyNavigationMarks();
    }

    private void ApplyNavigationMarks()
    {
        foreach ((TreeViewItem node, string key) in _navigationKeys)
        {
            if (node.Header is StackPanel header)
            { header.Children.OfType<TextBlock>().Single().TextDecorations = _navigationMarkedKeys.Contains(key) ? TextDecorations.Underline : null; }
        }
    }

    private IEnumerable<string> NavigationSelectionKeys()
    {
        IEnumerable<string> Descendants(string key)
        {
            if (!_navigationSelectionChildren.TryGetValue(key, out string[]? children)) { yield break; }
            yield return key;
            foreach (string child in children)
            { foreach (string descendant in Descendants(child)) { yield return descendant; } }
        }
        return _navigationGroups.Where(group => group.Value).SelectMany(group => Descendants("group:" + group.Key));
    }

    private int MarkedNavigationNodeCount => NavigationSelectionKeys().Count(_navigationMarkedKeys.Contains);

    private string[] MarkedNavigationReferences()
    {
        HashSet<string> active = new(NavigationSelectionKeys(), StringComparer.Ordinal);
        return _navigationKeys.Where(pair => pair.Key.Tag is GitReference && active.Contains(pair.Value) && _navigationMarkedKeys.Contains(pair.Value))
            .Select(pair => ((GitReference)pair.Key.Tag!).Name).ToArray();
    }

    private Task FilterMarkedNavigationReferencesAsync(GitRepository? repository, long version, string[] references)
    {
        if (repository is null || _repository != repository || _navigationSelectionRepository != repository || _busy
            || _lifetime.IsCancellationRequested || IsBranchFilterOwnerClosing || _navigationMarkedVersion != version || references.Length == 0
            || !references.SequenceEqual(MarkedNavigationReferences(), StringComparer.Ordinal)) { return Task.CompletedTask; }
        string filter = string.Join(" ", references);
        return ApplyBranchFilterAsync(_historyQuery with { BranchFilter = filter, Revision = null, All = true }, filter, branchFilterEnabled: true);
    }
}

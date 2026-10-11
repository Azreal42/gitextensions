using Avalonia.Controls;
using GitExtensions.Linux;

namespace GitExtensions.Desktop.Tests;

internal static class FileStatusTestHelpers
{
    public static IEnumerable<TreeViewItem> Rows(TreeView tree) => Descendants(tree.Items.OfType<TreeViewItem>());
    public static IEnumerable<GitChange> Changes(TreeView tree) => Rows(tree).Select(item => item.Tag).OfType<GitChange>();

    public static void Select(TreeView tree, string path)
    {
        TreeViewItem selected = Rows(tree).Single(item => item.Tag is GitChange change && change.Path == path);
        foreach (TreeViewItem parent in Rows(tree).Where(item => Descendants(item.Items.OfType<TreeViewItem>()).Contains(selected)))
        { parent.IsExpanded = true; }
        tree.SelectedItem = selected;
    }

    private static IEnumerable<TreeViewItem> Descendants(IEnumerable<TreeViewItem> items)
    {
        foreach (TreeViewItem item in items)
        {
            yield return item;
            foreach (TreeViewItem child in Descendants(item.Items.OfType<TreeViewItem>())) { yield return child; }
        }
    }
}

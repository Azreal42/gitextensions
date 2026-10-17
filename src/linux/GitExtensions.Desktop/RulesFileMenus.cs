using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal static class RulesFileMenus
{
    internal static void AttachEditors(FileStatusListView view, Func<bool> available, Func<GitRulesFileKind, Task> open)
    {
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        MenuItem ignore = OriginalMenuItems.Create(translations, "FileStatusList", "tsmiEditGitIgnore", "&Edit ignored files", "EditGitIgnore.png");
        MenuItem local = OriginalMenuItems.Create(translations, "FileStatusList", "tmsiEditLocallyIgnoredFiles", "Edit &locally ignored files", "EditGitIgnore.png");
        ignore.Click += async (_, _) => { if (available()) { await open(GitRulesFileKind.Ignore); } };
        local.Click += async (_, _) => { if (available()) { await open(GitRulesFileKind.LocalExclude); } };
        view.SettingsMenu.Items.Insert(0, ignore); view.SettingsMenu.Items.Insert(1, local); view.SettingsMenu.Items.Insert(2, new Separator());
        view.SettingsMenu.Opening += (_, _) => ignore.IsEnabled = local.IsEnabled = available();
    }

    internal static Action AttachIgnoreContext(FileStatusListView view, Func<bool> available, Func<bool, string[], Task> open)
    {
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        ContextMenu menu = view.Tree.ContextMenu ??= new ContextMenu { Name = "WorkingFilesIgnoreMenu" };
        view.Tree.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            if (!args.GetCurrentPoint(view.Tree).Properties.IsRightButtonPressed) { return; }
            TreeViewItem? row = (args.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
            if (row is not null && view.Tree.SelectedItems?.Contains(row) != true) { view.Tree.SelectedItem = row; row.Focus(); }
        }, RoutingStrategies.Tunnel);
        FileStatusListEntry[] selected = []; string? folder = null;
        bool enabled = false;
        MenuItem Item(string name, string label, bool local)
        {
            MenuItem item = OriginalMenuItems.Create(translations, "FileStatusList", name, label, "AddToGitIgnore.png");
            item.Click += async (_, _) =>
            {
                if (!enabled || !available()) { return; }
                try { await open(local, folder is null ? selected.Select(entry => GitIgnorePatterns.ForPath(entry.Path)).ToArray() : [GitIgnorePatterns.ForPath(folder, directory: true)]); }
                catch (Exception error) { if (TopLevel.GetTopLevel(view) is Window owner) { await Dialogs.ShowErrorAsync(owner, error.Message); } }
            };
            return item;
        }
        MenuItem[] items = [Item("tsmiAddFileToGitIgnore", "Add file to .gitignore", false), Item("tsmiAddFileToGitInfoExclude", "Add file to .git/info/exclude", true)];
        menu.ItemsSource = items;
        void UpdateItems()
        {
            selected = view.SelectedEntries.ToArray(); folder = view.SelectedFolderPath;
            enabled = available() && selected.Length > 0 && !(selected.Length == 1 && selected[0].Tag is GitChange { IsSubmodule: true });
            foreach (MenuItem item in items) { item.IsEnabled = enabled; }
        }
        menu.Opening += (_, _) => UpdateItems();
        menu.Opened += (_, _) => UpdateItems();
        return UpdateItems;
    }
}

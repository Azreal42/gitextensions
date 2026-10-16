using Avalonia.Controls;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private MenuItem BuildRepositoryMenu()
    {
        MenuItem Rule(string name, string text, GitRulesFileKind kind, string? icon = null)
        {
            MenuItem item = OriginalMenuItems.Create(_translations, "FormBrowse", name, text, icon); item.Tag = true; _actionMenus.Add(item);
            item.Click += async (_, _) => { if (_repository is not null && !_repository.IsBare && !_busy) { await OpenRulesFileAsync(kind); } };
            return item;
        }
        MenuItem remotes = OriginalMenuItems.Create(_translations, "FormBrowse", "manageRemoteRepositoriesToolStripMenuItem1", "Remo&te repositories...", "Remotes.png");
        remotes.Tag = false; _actionMenus.Add(remotes);
        remotes.Click += async (_, _) => await ManageRemotesAsync();
        return new MenuItem { Header = T("Repository"), ItemsSource = new object[]
        {
            remotes,
            MenuAction("Commit…", () => ShowWorkflowAsync("Changes")), MenuAction("Stash…", () => ShowWorkflowAsync("Stashes")),
            MenuAction("Worktrees…", () => ShowWorkflowAsync("Worktrees")), MenuAction("Submodules…", () => ShowWorkflowAsync("Submodules")), new Separator(),
            Rule("editgitignoreToolStripMenuItem1", "Edit .git&ignore", GitRulesFileKind.Ignore, "EditGitIgnore.png"),
            Rule("editgitinfoexcludeToolStripMenuItem", "Edit .git/info/&exclude", GitRulesFileKind.LocalExclude),
            Rule("editGitAttributesToolStripMenuItem", "Edit .git&attributes", GitRulesFileKind.Attributes),
            Rule("editmailmapToolStripMenuItem", "Edit .&mailmap", GitRulesFileKind.MailMap), new Separator(),
            BuildGitMaintenanceMenu(), MenuAction("Maintenance…", () => ShowWorkflowAsync("Repository"))
        } };
    }

    private Task OpenRulesFileAsync(GitRulesFileKind kind)
        => _repository is not null && !_repository.IsBare && !_busy ? ShowRepositoryDialogAsync(new RulesFileEditorDialog(_repository, kind)) : Task.CompletedTask;

    private void ConfigureRulesFileList(FileStatusListView view, bool worktreeContext)
    {
        bool Available() => _repository is not null && !_repository.IsBare && !_busy;
        RulesFileMenus.AttachEditors(view, Available, OpenRulesFileAsync);
        if (worktreeContext) { RulesFileMenus.AttachIgnoreContext(view, Available, (local, patterns) => _repository is not null ? ShowRepositoryDialogAsync(new AddToGitIgnoreDialog(_repository, local, patterns)) : Task.CompletedTask); }
    }
}

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class MainWindowDiffShortcutTests
{
    [AvaloniaTest]
    public async Task FocusedRevisionAndWorktreeViewersTakeDifftasticBeforeTheBrowserTagShortcut()
    {
        using Fixture fixture = await Fixture.CreateAsync("before λ🧪\n", "after λ🧪\n");
        MainWindow main = fixture.Window;
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string worktree = await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "file.txt"));
        string tool = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "controlled-difftastic");
        await File.WriteAllTextAsync(tool, "#!/bin/sh\nprintf 'file.txt --- Text\\n\\033[31m1 before λ🧪\\033[m\\n\\033[32m2 after λ🧪\\033[m\\n'\n");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        await RunGit(fixture, "config", "difftool.difftastic.cmd", "'" + tool + "' \"$LOCAL\" \"$REMOTE\"");
        try
        {
            foreach ((string tabName, string viewerName) in new[] { ("DiffTab", "CommitDiff"), ("ChangesTab", "WorktreeDiff") })
            {
                TabControl tabs = Find<TabControl>(main, "DetailsTabs");
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == tabName);
                main.UpdateLayout();
                if (tabName == "ChangesTab")
                {
                    TreeView files = Find<TreeView>(main, "WorkingFiles");
                    FileStatusTestHelpers.Select(files, "file.txt");
                }
                DiffView diff = Find<DiffView>(main, viewerName);
                await Until(() => !main.IsBusy && diff.CanonicalText.Contains("+", StringComparison.Ordinal));
                await diff.ConfigureRepositoryAsync(repository);
                string patch = diff.CanonicalText;
                Assert.That(diff.Editor.TextArea.Focus(), Is.True);
                Press(main, PhysicalKey.T);
                await Until(() => main.OwnedWindows.Any() || !main.IsBusy && diff.EffectiveMode == DiffViewMode.Difftastic);
                Assert.That(main.OwnedWindows, Is.Empty, "A focused diff must take Ctrl+T before the browser's Create tag action.");
                Assert.That(diff.AppearanceMenu.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Difftastic));
                Assert.That(diff.CanonicalText, Is.EqualTo(patch));
                Assert.That(diff.CanStagePatch, Is.False);
                Assert.That(diff.Text, Does.Contain("before λ🧪").And.Contain("after λ🧪"));
                Assert.That(diff.GetColumnNumbers(2), Is.EqualTo(new int?[] { 1, null }));
                Assert.That(diff.GetColumnNumbers(3), Is.EqualTo(new int?[] { null, 2 }));
                Assert.That(diff.Editor.TextArea.Focus(), Is.True);
                Press(main, PhysicalKey.T);
                await Until(() => !main.IsBusy && diff.EffectiveMode == DiffViewMode.Unified);
                Assert.That(diff.AppearanceMenu.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Patch));
                Assert.That(diff.CanonicalText, Is.EqualTo(patch));
            }

            main.UpdateLayout();
            ListBox history = Find<ListBox>(main, "RevisionHistory");
            Assert.That(history.GetVisualDescendants().OfType<ListBoxItem>().Single().Focus(), Is.True);
            Press(main, PhysicalKey.T);
            await Until(() => main.OwnedWindows.OfType<CreateTagDialog>().Any());
            CreateTagDialog tag = main.OwnedWindows.OfType<CreateTagDialog>().Single();
            Assert.That(tag.ReadOptions().Revision, Is.EqualTo(await repository.ResolveRevisionAsync("HEAD")));
            tag.Close();
            await Until(() => !main.IsBusy && !main.OwnedWindows.Any());
            Assert.That((await RunGit(fixture, "diff", "--cached")).StandardOutput, Is.Empty);
            Assert.That((await RunGit(fixture, "tag", "--list")).StandardOutput, Is.Empty);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "file.txt")), Is.EqualTo(worktree));
        }
        finally { foreach (Window child in main.OwnedWindows.ToArray()) { child.Close(); } }
    }

    [AvaloniaTest]
    public async Task UnavailableDifftasticLeavesTheOriginalCreateTagShortcutAvailable()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        MainWindow main = fixture.Window;
        DiffView diff = Find<DiffView>(main, "CommitDiff");
        await Until(() => !main.IsBusy && diff.CanonicalText.Contains("+initial", StringComparison.Ordinal));
        await RunGit(fixture, "config", "difftool.difftastic.cmd", "");
        await diff.ConfigureRepositoryAsync(await GitRepository.OpenAsync(fixture.Repository));
        Assert.That(diff.Editor.TextArea.Focus(), Is.True);
        try
        {
            Press(main, PhysicalKey.T);
            await Until(() => main.OwnedWindows.OfType<CreateTagDialog>().Any());
            Assert.That(diff.AppearanceMenu.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Patch));
        }
        finally
        {
            Window[] children = main.OwnedWindows.ToArray();
            foreach (Window child in children) { child.Close(); }
            await Until(() => children.All(child => !child.IsVisible) && !main.OwnedWindows.Any() && !main.IsBusy);
        }
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Press(Window window, PhysicalKey key)
    { window.KeyPressQwerty(key, RawInputModifiers.Control); window.KeyReleaseQwerty(key, RawInputModifiers.Control); }
    private static async Task<GitResult> RunGit(Fixture fixture, params string[] arguments)
    {
        GitResult result = await fixture.Git.RunAsync(fixture.Repository, arguments);
        Assert.That(result.Success, Is.True, result.StandardError); return result;
    }
    private static async Task Until(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate() && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        Assert.That(predicate(), Is.True, "The keyboard workflow did not complete within 15 seconds.");
    }
}

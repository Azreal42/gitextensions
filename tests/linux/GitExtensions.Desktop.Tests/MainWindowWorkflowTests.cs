using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class MainWindowWorkflowTests
{
    [AvaloniaTest]
    public async Task RepositoryLoadsCommitAndWorktreeDiffAndCommitsStagedChanges()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "gitextensions-desktop-tests-" + Guid.NewGuid().ToString("N"));
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(temporary, "configuration"));
        string repository = Path.Combine(temporary, "repository");
        Directory.CreateDirectory(repository);
        GitRunner git = new();
        MainWindow? window = null;
        try
        {
            await RunGit(git, repository, "init", "-b", "main");
            await RunGit(git, repository, "config", "user.name", "Desktop Tester");
            await RunGit(git, repository, "config", "user.email", "desktop@example.com");
            await RunGit(git, repository, "config", "commit.gpgsign", "false");
            await RunGit(git, repository, "config", "core.hooksPath", Path.Combine(temporary, "no-hooks"));
            await File.WriteAllTextAsync(Path.Combine(repository, "file.txt"), "first line\n");
            await RunGit(git, repository, "add", "--", "file.txt");
            await RunGit(git, repository, "commit", "-m", "Initial commit");
            await File.WriteAllTextAsync(Path.Combine(repository, "file.txt"), "first line\nsecond line\n");

            window = new MainWindow();
            window.Show();
            await window.OpenRepositoryAsync(repository);
            window.UpdateLayout();
            Assert.That(window.Title, Does.Contain(repository));
            ListBox history = window.GetVisualDescendants().OfType<ListBox>().Single(list => list.ItemsSource?.OfType<GitCommit>().Any() is true);
            Assert.That(history.ItemsSource!.OfType<GitCommit>().Single().Subject, Is.EqualTo("Initial commit"));
            Assert.That(history.GetVisualDescendants().OfType<ListBoxItem>().Single().Bounds.Height, Is.EqualTo(24), "History rows must retain the original compact density.");
            history.SelectedIndex = 0;
            // A fresh profile now uses the original Commit tab below the graph.
            TabControl tabs = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "DetailsTabs");
            tabs.SelectedItem = tabs.ItemsSource!.OfType<TabItem>().Single(tab => tab.Name == "DiffTab");
            window.UpdateLayout();
            await Until(() => VisibleDiffContains(window, "+first line"));
            await Until(() => !window.IsBusy);

            tabs.SelectedItem = tabs.ItemsSource!.OfType<TabItem>().Single(tab => tab.Name == "ChangesTab");
            window.UpdateLayout();
            TreeView changes = window.GetVisualDescendants().OfType<TreeView>().Single(tree => tree.Name == "WorkingFiles");
            FileStatusTestHelpers.Select(changes, FileStatusTestHelpers.Changes(changes).First().Path);
            await Until(() => VisibleDiffContains(window, "+second line"));
            await Until(() => !window.IsBusy);
            Click(window, "Stage selected");
            await Until(() => FileStatusTestHelpers.Changes(changes).Single().IsStaged);
            await Until(() => !window.IsBusy);

            TextBox message = window.GetVisualDescendants().OfType<TextBox>().Single(text => text.Watermark == "Commit message");
            message.Text = "Commit from Linux desktop";
            Click(window, "Commit");
            await Until(() => !FileStatusTestHelpers.Changes(changes).Any());
            await Until(() => !window.IsBusy);
            GitResult latest = await git.RunAsync(repository, ["log", "-1", "--format=%s"]);
            Assert.That(latest.StandardOutput.Trim(), Is.EqualTo("Commit from Linux desktop"));
            Assert.That(new RepositoryHistoryStore().Load().Recent.Select(entry => entry.Path), Does.Contain(repository));
        }
        finally
        {
            window?.Close();
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
            Directory.Delete(temporary, true);
        }
    }

    private static async Task RunGit(GitRunner git, string directory, params string[] arguments)
    {
        GitResult result = await git.RunAsync(directory, arguments);
        Assert.That(result.Success, Is.True, result.StandardError);
    }

    private static void Click(Window window, string content)
    {
        Button button = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == content);
        Assert.That(button.IsEffectivelyVisible && button.IsEnabled, Is.True, $"{content} must be available in the visible workflow.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static bool VisibleDiffContains(Window window, string text)
        => window.GetVisualDescendants().OfType<DiffView>().Any(view => view.IsEffectivelyVisible && view.Text.Contains(text, StringComparison.Ordinal));

    private static async Task Until(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.That(predicate(), Is.True, "Desktop workflow did not complete within 15 seconds.");
    }
}

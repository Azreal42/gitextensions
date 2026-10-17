using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using GitExtensions.Desktop.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class HistorySortDefaultTests
{
    [TestCase(0, GitLogSort.Topology)]
    [TestCase(1, GitLogSort.AuthorDate)]
    [TestCase(2, GitLogSort.Date)]
    [TestCase(3, GitLogSort.GitDefault)]
    public void ExistingNumericPreferencesKeepTheirMeaning(int number, GitLogSort expected)
    {
        HistoryViewPreferences value = JsonSerializer.Deserialize<HistoryViewPreferences>("{\"Sort\":" + number + "}")!;
        Assert.That(value.Sort, Is.EqualTo(expected));
        Assert.That(JsonSerializer.Deserialize<HistoryViewPreferences>(JsonSerializer.Serialize(value))!.Sort, Is.EqualTo(expected));
    }

    [Test]
    public void MissingAndInvalidSavedOrderUseTheOriginalGitDefault()
    {
        string? old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string directory = Path.Combine(Path.GetTempPath(), "gitextensions-sort-prefs-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            HistoryViewPreferencesStore store = new();
            Assert.That(store.Load().Sort, Is.EqualTo(GitLogSort.GitDefault));
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            foreach (string json in new[] { "{}", "{\"Sort\":999}" })
            {
                File.WriteAllText(store.FilePath, json);
                Assert.That(store.Load().Sort, Is.EqualTo(GitLogSort.GitDefault));
            }
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old); if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    [AvaloniaTest]
    public async Task BothOriginalTogglesReturnToNativeGitOrderingAndSaveOnlyExplicitly()
    {
        using Fixture f = await Fixture.CreateAsync("same tree\n", "same tree\n");
        string tree = (await Git(f, "rev-parse", "HEAD^{tree}")).Trim();
        Dictionary<string, string> ids = new(StringComparer.Ordinal);
        foreach ((string name, string[] parents, int year) in new[]
        {
            ("root", Array.Empty<string>(), 2009), ("a1", new[] { "root" }, 2008),
            ("a2", new[] { "a1" }, 2007), ("b1", new[] { "root" }, 2006),
            ("merge", new[] { "a2", "b1" }, 2010)
        })
        { ids[name] = await Commit(f, tree, parents.Select(parent => ids[parent]), name, year); }
        await Git(f, "update-ref", "refs/heads/main", ids["merge"]);
        string[] native = await Order(f, ids["merge"]);
        Assert.That(native, Is.Not.EqualTo(await Order(f, ids["merge"], "--date-order")), "The clock-skewed DAG must distinguish native default from date-order.");
        Assert.That(native, Is.Not.EqualTo(await Order(f, ids["merge"], "--topo-order")));
        GitRepository repository = await GitRepository.OpenAsync(f.Repository);
        Assert.That((await repository.GetLogAsync(new GitLogQuery(Revision: ids["merge"]))).Select(commit => commit.Hash), Is.EqualTo(native));
        foreach (string option in new[] { "--date-order", "--author-date-order", "--topo-order" })
        {
            Assert.That((await repository.GetLogAsync(new GitLogQuery { BranchFilter = option + " main", IncludeStashes = false })).Select(commit => commit.Hash),
                Is.EqualTo(await Order(f, ids["merge"], option)), "An explicit filter option remains effective in GitDefault.");
        }
        await f.Window.OpenRepositoryAsync(f.Repository); await Idle(f.Window);
        ListBox history = f.Window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "RevisionHistory");
        MenuItem view = f.Window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Name == "ViewMenu");
        MenuItem Item(string name) => view.Items.OfType<MenuItem>().Single(item => item.Name == name);
        byte[] index = await File.ReadAllBytesAsync(Path.Combine(f.Repository, ".git", "index"));
        string refs = await Git(f, "show-ref");
        foreach ((string name, string option) in new[] { ("ViewAuthorDateSort", "--author-date-order"), ("ViewTopoOrder", "--topo-order") })
        {
            GitCommit selected = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == ids["a1"]);
            history.SelectedItem = selected; await Idle(f.Window);
            history.ScrollIntoView(selected); f.Window.UpdateLayout();
            PushProcessTestDriver.Pointer(history.ContainerFromItem(selected)!); await Idle(f.Window);
            Assert.That(history.IsKeyboardFocusWithin, Is.True);
            Click(Item(name)); await Idle(f.Window);
            Assert.That(Item(name).IsChecked, Is.True);
            Assert.That(history.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(await Order(f, ids["merge"], option)));
            Click(Item(name)); await Idle(f.Window);
            Assert.That(Item("ViewAuthorDateSort").IsChecked, Is.False);
            Assert.That(Item("ViewTopoOrder").IsChecked, Is.False);
            CommitGraph graph = f.Window.GetVisualDescendants().OfType<CommitGraph>().Single();
            Assert.That(graph.Commits.Select(commit => commit.Hash), Is.EqualTo(native), "The reader must receive GitDefault without a sort flag.");
            // Windows RevisionDataGridView.GetRevision uses RevisionGraph scores:
            // Add moves an already-seen parent behind its late child. Keep that
            // upstream layout contract distinct from the reader's native order.
            Assert.That(history.Items.OfType<GitCommit>().Select(commit => commit.Hash),
                Is.EqualTo(new[] { "merge", "a2", "a1", "b1", "root" }.Select(name => ids[name])));
            Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(selected.Hash));
            Assert.That(history.IsKeyboardFocusWithin, Is.True);
            Assert.That(new HistoryViewPreferencesStore().Load().Sort, Is.EqualTo(GitLogSort.GitDefault));
        }
        Click(Item("ViewTopoOrder")); await Idle(f.Window);
        Assert.That(new HistoryViewPreferencesStore().Load().Sort, Is.EqualTo(GitLogSort.GitDefault));
        Click(Item("ViewSaveAsDefault"));
        Assert.That(new HistoryViewPreferencesStore().Load().Sort, Is.EqualTo(GitLogSort.Topology));
        Click(Item("ViewTopoOrder")); await Idle(f.Window); Click(Item("ViewSaveAsDefault"));
        Assert.That(new HistoryViewPreferencesStore().Load().Sort, Is.EqualTo(GitLogSort.GitDefault));
        Assert.That(await Git(f, "show-ref"), Is.EqualTo(refs));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(f.Repository, ".git", "index")), Is.EqualTo(index));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Repository, "file.txt")), Is.EqualTo("same tree\n"));
    }

    [AvaloniaTest]
    public async Task GeneralSettingsDoNotRereadOrRebuildReferencesWhenSortingIsUnchanged()
    {
        using Fixture f = await Fixture.CreateAsync("same tree\n", "same tree\n"); await Idle(f.Window);
        ListBox history = f.Window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "RevisionHistory");
        TreeView tree = f.Window.GetVisualDescendants().OfType<TreeView>().Single(control => control.Name == "RepositoryNavigation");
        object rows = history.ItemsSource!, nodes = tree.ItemsSource!;
        PushProcessTestDriver.Pointer(history.ContainerFromItem(history.SelectedItem!)!); await Idle(f.Window);
        f.Window.KeyPressQwerty(PhysicalKey.Comma, RawInputModifiers.Control);
        f.Window.KeyReleaseQwerty(PhysicalKey.Comma, RawInputModifiers.Control);
        for (int i = 0; i < 500 && !f.Window.OwnedWindows.OfType<DesktopSettingsDialog>().Any(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        DesktopSettingsDialog settings = f.Window.OwnedWindows.OfType<DesktopSettingsDialog>().Single();
        settings.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        settings.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "SettingsTheme").SelectedItem = "Dark";
        GitCommandLog.Clear();
        PushProcessTestDriver.Pointer(settings.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "SaveDesktopSettings"));
        await Idle(f.Window);
        Assert.That(settings.IsVisible, Is.False);
        Assert.That(new PreferencesStore().Load().Theme, Is.EqualTo("Dark"));
        Assert.That(history.ItemsSource, Is.SameAs(rows)); Assert.That(tree.ItemsSource, Is.SameAs(nodes));
        Assert.That(GitCommandLog.Snapshot(), Is.Empty, "Changing only the theme must not restart reference, history or diff reads.");
    }

    private static async Task<string> Git(Fixture f, params string[] args)
    {
        GitResult result = await f.Git.RunAsync(f.Repository, args); Assert.That(result.Success, Is.True, result.StandardError); return result.StandardOutput;
    }
    private static async Task<string[]> Order(Fixture f, string revision, params string[] options)
        => (await Git(f, ["log", .. options, "--format=%H", revision, "--"])).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    private static async Task<string> Commit(Fixture f, string tree, IEnumerable<string> parents, string name, int year)
    {
        ProcessStartInfo start = new("git") { WorkingDirectory = f.Repository, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "commit-tree", tree }.Concat(parents.SelectMany(parent => new[] { "-p", parent })).Concat(["-m", name])) { start.ArgumentList.Add(arg); }
        start.Environment["GIT_AUTHOR_DATE"] = start.Environment["GIT_COMMITTER_DATE"] = $"{year}-01-01T00:00:00+00:00";
        using Process process = Process.Start(start)!; Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.That(process.ExitCode, Is.Zero, await error); return (await output).Trim();
    }
    private static void Click(MenuItem item) { item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    private static async Task Idle(MainWindow window)
    {
        for (int i = 0; i < 1000 && window.IsBusy; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(window.IsBusy, Is.False); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
    }
}

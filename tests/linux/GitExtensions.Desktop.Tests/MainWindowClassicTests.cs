using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitCommands;
using NUnit.Framework;
using System.Diagnostics;
using System.Globalization;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class MainWindowClassicTests
{
    [AvaloniaTest]
    public async Task BrowserKeepsClassicMenusNavigationAndCompactRevisionTableAboveDetails()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync("first line\n", "first line\nsecond line\n", createMerge: true);
        MainWindow window = fixture.Window;
        ListBox history = History(window);
        history.SelectedItem = history.ItemsSource!.OfType<GitCommit>().Single(commit => commit.Subject == "Main branch change");
        await Until(() => !window.IsBusy && VisibleDiff(window).Text.Contains("+Main branch content", StringComparison.Ordinal));
        window.UpdateLayout();

        Menu menu = window.GetVisualDescendants().OfType<Menu>().Single();
        TreeView navigation = window.GetVisualDescendants().OfType<TreeView>().Single(tree => tree.Name == "RepositoryNavigation");
        TabControl details = Details(window);
        CommitGraph graph = window.GetVisualDescendants().OfType<CommitGraph>().Single();
        GitCommit selected = (GitCommit)history.SelectedItem!;
        TextBox information = window.GetVisualDescendants().OfType<TextBox>().Single(text => text.IsEffectivelyVisible && text.Text?.Contains(selected.Hash, StringComparison.Ordinal) is true && text.Text.Contains("Author:", StringComparison.Ordinal));

        Assert.That(menu.Items.OfType<MenuItem>().Select(item => item.Header), Is.EqualTo(new[] { "Start", "Repository", "Navigate", "View", "Commands", "Git hosts", "Plugins", "Tools", "Help" }));
        Assert.That(navigation.Items.OfType<TreeViewItem>().Select(item => ((StackPanel)item.Header!).Children.OfType<TextBlock>().Single().Text), Is.EqualTo(new[] { "Branches", "Remotes", "Tags", "Submodules", "Stashes", "Worktrees" }));
        Assert.That(graph.Commits.Any(commit => commit.Parents.Count == 2), Is.True, "The real repository must render a merge graph.");
        Assert.That(graph.Bounds.Width, Is.InRange(16, 52), "The graph must reserve its visible lanes; reference labels belong in the message column.");
        Assert.That(history.GetVisualDescendants().OfType<ListBoxItem>().Select(item => item.Bounds.Height), Is.All.EqualTo(20));
        Rect menuBounds = InWindow(window, menu);
        Rect navigationBounds = InWindow(window, navigation);
        Rect historyBounds = InWindow(window, history);
        Rect graphBounds = InWindow(window, graph);
        Rect informationBounds = InWindow(window, information);
        Rect detailsBounds = InWindow(window, details);
        Assert.Multiple(() =>
        {
            Assert.That(menuBounds.Y, Is.LessThan(navigationBounds.Y), "Menus must remain above the browser.");
            Assert.That(navigationBounds.Right, Is.LessThanOrEqualTo(graphBounds.X), "Repository references must remain in the left sidebar.");
            Assert.That(graphBounds.Right, Is.EqualTo(historyBounds.X).Within(1), "Graph lanes must remain alongside the revision table.");
            Assert.That(historyBounds.Right, Is.LessThanOrEqualTo(informationBounds.X), "Commit details must remain to the right of history.");
            Assert.That(informationBounds.Y, Is.LessThan(detailsBounds.Y));
            Assert.That(historyBounds.Bottom, Is.LessThanOrEqualTo(detailsBounds.Y), "Diff tabs must remain below history.");
            Assert.That(detailsBounds.X, Is.EqualTo(graphBounds.X).Within(1));
            Assert.That(detailsBounds.Right, Is.EqualTo(informationBounds.Right).Within(1));
            Assert.That(navigationBounds.Bottom, Is.GreaterThan(detailsBounds.Y), "The sidebar must span both history and details.");
            Assert.That(VisibleDiff(window).Bounds.Width, Is.GreaterThan(400), "The visible native diff must fill the lower pane.");
        });

        using Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null);
        string screenshot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "classic-browser.png");
        frame!.Save(screenshot);
        TestContext.AddTestAttachment(screenshot, "Classic Linux browser with real merge history and native diff");

        Border toolbar = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "RepositoryToolbar");
        Assert.That(window.GetVisualDescendants().Any(control => control is Control { Name: "RevisionHeader" }), Is.False, "The original revision grid hides its column headers.");
        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.UpdateLayout();
        Assert.That(((ISolidColorBrush)toolbar.Background!).Color, Is.EqualTo(Color.Parse("#202020")), "The compact toolbar follows the original dark palette.");
        using (Avalonia.Media.Imaging.WriteableBitmap? darkFrame = window.CaptureRenderedFrame())
        {
            Assert.That(darkFrame, Is.Not.Null);
            string darkScreenshot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "classic-browser-dark.png");
            darkFrame!.Save(darkScreenshot);
            TestContext.AddTestAttachment(darkScreenshot, "Classic Linux browser in the dark theme");
        }
        window.RequestedThemeVariant = ThemeVariant.Light;
        window.UpdateLayout();
        Assert.That(((ISolidColorBrush)toolbar.Background!).Color, Is.EqualTo(Color.Parse("#F0F0F0")));
    }

    [AvaloniaTest]
    public async Task OpeningFromASubdirectoryNormalizesRootAndBareRepositoryKeepsHistoryReadOnly()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync("anchor\n", "modified\n", createMerge: true);
        string subdirectory = Path.Combine(fixture.Repository, "nested");
        Directory.CreateDirectory(subdirectory);
        await fixture.Window.OpenRepositoryAsync(subdirectory);
        Assert.That(fixture.Window.Title, Is.EqualTo("repository (main) — Git Extensions"));
        Assert.That(new RepositoryHistoryStore().Load().Recent.First().Path, Is.EqualTo(fixture.Repository));
        SelectChanges(fixture.Window);
        TreeView changes = Changes(fixture.Window);
        FileStatusTestHelpers.Select(changes, FileStatusTestHelpers.Changes(changes).First().Path);
        await Until(() => !fixture.Window.IsBusy && VisibleDiff(fixture.Window).Text.Contains("+modified", StringComparison.Ordinal));
        Click(fixture.Window, "Stage selected");
        await Until(() => !fixture.Window.IsBusy && FileStatusTestHelpers.Changes(changes).Single().IsStaged);
        Assert.That((await fixture.Git.RunAsync(fixture.Repository, ["diff", "--cached"])).StandardOutput, Does.Contain("+modified"));

        string barePath = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "bare.git");
        GitResult clone = await fixture.Git.RunAsync(fixture.Repository, ["clone", "--bare", fixture.Repository, barePath]);
        Assert.That(clone.Success, Is.True, clone.StandardError);
        await fixture.Window.OpenRepositoryAsync(barePath);
        Assert.That(fixture.Window.Title, Is.EqualTo("bare.git (main) — Git Extensions"));
        Assert.That(History(fixture.Window).ItemsSource!.OfType<GitCommit>().Count(), Is.EqualTo(4));
        Assert.That((Details(fixture.Window).SelectedItem as TabItem)?.Name, Is.EqualTo("DiffTab"));
        Assert.That(Details(fixture.Window).Items.OfType<TabItem>().Single(tab => tab.Name == "ChangesTab").IsEnabled, Is.False);
        Menu menu = fixture.Window.GetVisualDescendants().OfType<Menu>().Single();
        MenuItem repositoryMenu = menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Repository");
        Assert.That(repositoryMenu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Commit…").IsEnabled, Is.False);
        Assert.That(fixture.Window.LastException, Is.Null, fixture.Window.LastException?.ToString());
        Assert.That(fixture.Window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "RepositoryStatus").Text, Does.StartWith("Bare repository"));
    }

    [AvaloniaTest]
    public async Task WorkingDirectoryStagesModifiedRenameAndUnstagesBothNamesWithoutChangingFiles()
    {
        string initial = string.Join('\n', Enumerable.Range(1, 30).Select(index => $"original line {index:00}")) + "\n";
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(initial, initial);
        const string renamed = "renamed λ[1].txt";
        const string addition = "renamed change\n";
        GitResult move = await fixture.Git.RunAsync(fixture.Repository, ["mv", "--", "file.txt", renamed]);
        Assert.That(move.Success, Is.True, move.StandardError);
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository, renamed), addition);
        await fixture.Window.OpenRepositoryAsync(fixture.Repository);
        SelectChanges(fixture.Window);
        TreeView changes = Changes(fixture.Window);
        GitChange change = FileStatusTestHelpers.Changes(changes).Single();
        Assert.That(change.OriginalPath, Is.EqualTo("file.txt"));
        FileStatusTestHelpers.Select(changes, change.Path);
        await Until(() => !fixture.Window.IsBusy);

        Click(fixture.Window, "Stage selected");
        await Until(() => !fixture.Window.IsBusy);
        Assert.That(fixture.Window.LastException, Is.Null, fixture.Window.LastException?.ToString());
        GitResult indexed = await fixture.Git.RunAsync(fixture.Repository, ["show", ":" + renamed]);
        Assert.That(indexed.Success, Is.True, indexed.StandardError);
        Assert.That(indexed.StandardOutput, Is.EqualTo(initial + addition));
        Assert.That(VisibleDiff(fixture.Window).Text, Does.Contain("rename from file.txt").And.Contain("rename to " + renamed),
            "The viewer must include both paths to preserve Git's actual rename patch, rather than displaying only the destination as an addition.");

        FileStatusTestHelpers.Select(changes, FileStatusTestHelpers.Changes(changes).Single().Path);
        await Until(() => !fixture.Window.IsBusy);
        Click(fixture.Window, "Unstage selected");
        await Until(() => !fixture.Window.IsBusy);
        Assert.That(fixture.Window.LastException, Is.Null, fixture.Window.LastException?.ToString());
        Assert.That((await fixture.Git.RunAsync(fixture.Repository, ["diff", "--cached"])).StandardOutput, Is.Empty);
        Assert.That(File.Exists(Path.Combine(fixture.Repository, "file.txt")), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(fixture.Repository, renamed)), Is.EqualTo(initial + addition));
    }

    [AvaloniaTest]
    public async Task OriginalShortcutsNavigateHistoryAndFocusDetailTabs()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync("first line\n", "first line\nsecond line\n", createMerge: true);
        MainWindow window = fixture.Window;
        ListBox history = History(window);
        history.SelectedItem = history.ItemsSource!.OfType<GitCommit>().Single(commit => commit.Subject == "Main branch change");
        await Until(() => !window.IsBusy && VisibleDiff(window).Text.Contains("+Main branch content", StringComparison.Ordinal));
        history.Focus();
        Press(window, Key.P, RawInputModifiers.Control);
        await Until(() => !window.IsBusy && (history.SelectedItem as GitCommit)?.Subject == "Initial commit");
        Assert.That(VisibleDiff(window).Text, Does.Contain("+first line"));
        Press(window, Key.N, RawInputModifiers.Control);
        await Until(() => !window.IsBusy && (history.SelectedItem as GitCommit)?.Parents.Contains(fixtureInitialHash()) == true);
        Press(window, Key.D3, RawInputModifiers.Control);
        Assert.That((Details(window).SelectedItem as TabItem)?.Name, Is.EqualTo("DiffTab"));
        Assert.That(VisibleDiff(window).Editor.TextArea.IsFocused, Is.True);
        Press(window, Key.D4, RawInputModifiers.Control);
        Assert.That((Details(window).SelectedItem as TabItem)?.Name, Is.EqualTo("FileTreeTab"));
        await Until(() => !window.IsBusy); // The newly visible tree now loads its selected historical blob.
        Press(window, Key.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.That((Details(window).SelectedItem as TabItem)?.Name, Is.EqualTo("DiffTab"));
        Press(window, Key.Tab, RawInputModifiers.Control);
        Assert.That((Details(window).SelectedItem as TabItem)?.Name, Is.EqualTo("FileTreeTab"));
        await Until(() => !window.IsBusy);

        SelectChanges(window);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "new.txt"), "F5 refresh\n");
        Press(window, Key.F5, RawInputModifiers.None);
        await Until(() => !window.IsBusy && FileStatusTestHelpers.Changes(Changes(window)).Any(change => change.Path == "new.txt"));

        string fixtureInitialHash() => history.ItemsSource!.OfType<GitCommit>().Single(commit => commit.Subject == "Initial commit").Hash;
    }

    [AvaloniaTest]
    public async Task StagesAndUnstagesOnlySelectedHunkWithoutLosingOtherWorktreeChanges()
    {
        string initial = string.Join('\n', Enumerable.Range(1, 30).Select(index => $"original line {index:00}")) + "\n";
        string changed = initial.Replace("original line 02", "selected change", StringComparison.Ordinal)
            .Replace("original line 26", "remaining change", StringComparison.Ordinal);
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(initial, changed);
        MainWindow window = fixture.Window;
        SelectChanges(window);
        TreeView changes = Changes(window);
        FileStatusTestHelpers.Select(changes, FileStatusTestHelpers.Changes(changes).First().Path);
        await Until(() => !window.IsBusy && VisibleDiff(window).Text.Contains("+remaining change", StringComparison.Ordinal));
        ListBox hunks = window.GetVisualDescendants().OfType<ListBox>().Single(list => list.ItemsSource?.OfType<GitPatchHunk>().Count() == 2);
        hunks.SelectedIndex = 0;
        Click(window, "Stage selected hunks");
        await Until(() => !window.IsBusy && FileStatusTestHelpers.Changes(changes).Single().IsStaged);
        GitResult staged = await fixture.Git.RunAsync(fixture.Repository, ["diff", "--cached", "--", "file.txt"]);
        GitResult unstaged = await fixture.Git.RunAsync(fixture.Repository, ["diff", "--", "file.txt"]);
        Assert.Multiple(() =>
        {
            Assert.That(staged.StandardOutput, Does.Contain("+selected change").And.Not.Contain("+remaining change"));
            Assert.That(unstaged.StandardOutput, Does.Contain("+remaining change").And.Not.Contain("+selected change"));
            Assert.That(File.ReadAllText(Path.Combine(fixture.Repository, "file.txt")), Is.EqualTo(changed));
        });

        CheckBox stagedDiff = window.GetVisualDescendants().OfType<CheckBox>().Single(check => check.Content as string == "Show staged changes");
        stagedDiff.IsChecked = true;
        await Until(() => !window.IsBusy && VisibleDiff(window).Text.Contains("+selected change", StringComparison.Ordinal));
        hunks.SelectedIndex = 0;
        Click(window, "Unstage selected hunks");
        await Until(() => !window.IsBusy && !FileStatusTestHelpers.Changes(changes).Single().IsStaged);
        staged = await fixture.Git.RunAsync(fixture.Repository, ["diff", "--cached", "--", "file.txt"]);
        Assert.That(staged.StandardOutput, Is.Empty);
        stagedDiff.IsChecked = false;
        await Until(() => !window.IsBusy && VisibleDiff(window).Text.Contains("+remaining change", StringComparison.Ordinal));
        Assert.That(VisibleDiff(window).Text, Does.Contain("+selected change"));
    }

    [AvaloniaTest]
    public async Task StagesAndUnstagesOneUnicodeLineFromTheVisibleDiff()
    {
        const string initial = "anchor\nend\n";
        const string changed = "anchor\npremière ligne λ🧪\nremaining addition\nend\n";
        const string selectedLine = "+première ligne λ🧪";
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(initial, changed);
        MainWindow window = fixture.Window;
        SelectChanges(window);
        TreeView changes = Changes(window);
        FileStatusTestHelpers.Select(changes, FileStatusTestHelpers.Changes(changes).First().Path);
        await Until(() => !window.IsBusy && VisibleDiff(window).Text.Contains("+remaining addition", StringComparison.Ordinal));
        DiffView diff = VisibleDiff(window);
        diff.Select(diff.Text.IndexOf(selectedLine, StringComparison.Ordinal), selectedLine.Length);
        Click(window, "Stage selected lines");
        await Until(() => !window.IsBusy && FileStatusTestHelpers.Changes(changes).Single().IsStaged);
        GitResult staged = await fixture.Git.RunAsync(fixture.Repository, ["diff", "--cached", "--", "file.txt"]);
        GitResult unstaged = await fixture.Git.RunAsync(fixture.Repository, ["diff", "--", "file.txt"]);
        Assert.Multiple(() =>
        {
            Assert.That(staged.StandardOutput, Does.Contain(selectedLine).And.Not.Contain("+remaining addition"));
            Assert.That(unstaged.StandardOutput, Does.Contain("+remaining addition").And.Not.Contain(selectedLine));
            Assert.That(File.ReadAllText(Path.Combine(fixture.Repository, "file.txt")), Is.EqualTo(changed));
        });

        CheckBox stagedDiff = window.GetVisualDescendants().OfType<CheckBox>().Single(check => check.Content as string == "Show staged changes");
        stagedDiff.IsChecked = true;
        await Until(() => !window.IsBusy && VisibleDiff(window).Text.Contains(selectedLine, StringComparison.Ordinal));
        diff.Select(diff.Text.IndexOf(selectedLine, StringComparison.Ordinal), selectedLine.Length);
        Click(window, "Unstage selected lines");
        await Until(() => !window.IsBusy && !FileStatusTestHelpers.Changes(changes).Single().IsStaged);
        staged = await fixture.Git.RunAsync(fixture.Repository, ["diff", "--cached", "--", "file.txt"]);
        Assert.That(staged.StandardOutput, Is.Empty);
        Assert.That(File.ReadAllText(Path.Combine(fixture.Repository, "file.txt")), Is.EqualTo(changed));
    }

    private static ListBox History(Window window) => window.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "RevisionHistory");
    private static TreeView Changes(Window window) => window.GetVisualDescendants().OfType<TreeView>().Single(tree => tree.Name == "WorkingFiles");
    private static TabControl Details(Window window) => window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "DetailsTabs");
    private static DiffView VisibleDiff(Window window) => window.GetVisualDescendants().OfType<DiffView>().Single(view => view.IsEffectivelyVisible);

    private static void SelectChanges(Window window)
    {
        TabControl tabs = Details(window);
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "ChangesTab");
        window.UpdateLayout();
    }

    private static Rect InWindow(Window window, Control control)
    {
        Point? origin = control.TranslatePoint(default, window);
        Assert.That(origin, Is.Not.Null);
        return new Rect(origin!.Value, control.Bounds.Size);
    }

    private static void Click(Window window, string content)
    {
        Button button = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == content);
        Assert.That(button.IsEffectivelyVisible && button.IsEnabled, Is.True, $"{content} must be available in the visible workflow.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers)
    {
        PhysicalKey physical = key switch
        {
            Key.P => PhysicalKey.P,
            Key.N => PhysicalKey.N,
            Key.D3 => PhysicalKey.Digit3,
            Key.D4 => PhysicalKey.Digit4,
            Key.Tab => PhysicalKey.Tab,
            Key.F5 => PhysicalKey.F5,
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        window.KeyPressQwerty(physical, modifiers);
        window.KeyReleaseQwerty(physical, modifiers);
    }

    private static async Task Until(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate() && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        Assert.That(predicate(), Is.True, "Desktop workflow did not complete within 15 seconds.");
    }

    internal sealed class RepositoryFixture : IDisposable
    {
        private readonly string _temporary = Path.Combine(Path.GetTempPath(), "gitextensions-desktop-tests-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CultureInfo _oldCulture = CultureInfo.CurrentUICulture;
        public GitRunner Git { get; } = new();
        public string Repository => Path.Combine(_temporary, "repository");
        public MainWindow Window { get; private set; } = null!;

        public static async Task<RepositoryFixture> CreateAsync(string initialContent, string changedContent, bool createMerge = false,
            CommitInfoPosition? commitInfoPosition = CommitInfoPosition.RightwardFromList)
        {
            RepositoryFixture fixture = new();
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(fixture._temporary, "configuration"));
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
                Directory.CreateDirectory(fixture.Repository);
                await fixture.RunGit("init", "-b", "main");
                await fixture.RunGit("config", "user.name", "Desktop Tester");
                await fixture.RunGit("config", "user.email", "desktop@example.com");
                await fixture.RunGit("config", "commit.gpgsign", "false");
                await fixture.RunGit("config", "core.hooksPath", Path.Combine(fixture._temporary, "no-hooks"));
                await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), initialContent);
                await fixture.RunGit("add", "--", "file.txt");
                await fixture.RunGit("commit", "-m", "Initial commit");
                if (createMerge)
                {
                    await fixture.RunGit("tag", "v1.0");
                    await fixture.RunGit("checkout", "-b", "feature");
                    await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "feature.txt"), "Feature branch content\n");
                    await fixture.RunGit("add", "--", "feature.txt");
                    await fixture.RunGit("commit", "-m", "Feature branch change");
                    await fixture.RunGit("checkout", "main");
                    await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "README.md"), "Main branch content\n");
                    await fixture.RunGit("add", "--", "README.md");
                    await fixture.RunGit("commit", "-m", "Main branch change");
                    await fixture.RunGit("merge", "--no-ff", "feature", "-m", "Merge feature branch");
                    await fixture.RunGit("update-ref", "refs/remotes/origin/main", "HEAD");
                }

                await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), changedContent);
                // These existing scenarios exercise the right-hand information variant explicitly.
                // A null position represents a new profile with the original BelowList default.
                if (commitInfoPosition is not null) { new BrowserLayoutPreferencesStore().Save(new(commitInfoPosition.Value)); }
                fixture.Window = new MainWindow { Width = 1440, Height = 900 };
                fixture.Window.Show();
                await fixture.Window.OpenRepositoryAsync(fixture.Repository);
                fixture.Window.UpdateLayout();
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        private async Task RunGit(params string[] arguments)
        {
            GitResult result = await Git.RunAsync(Repository, arguments);
            Assert.That(result.Success, Is.True, result.StandardError);
        }

        public void Dispose()
        {
            if (Window is { } window)
            {
                window.Close();
                Task pending = window.CommitInformationCompletion;
                Stopwatch timer = Stopwatch.StartNew();
                while ((window.IsVisible || !pending.IsCompleted) && timer.Elapsed < TimeSpan.FromSeconds(15))
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    if (window.IsVisible) { window.UpdateLayout(); }
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Yield();
                }
                Assert.That(window.IsVisible, Is.False, "Fixture cleanup must close the window before its headless application scope ends.");
                Assert.That(pending.IsCompleted, Is.True, "Fixture cleanup must drain CommitInfo before restoring its profile or deleting its repository.");
                try { pending.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
            CultureInfo.CurrentUICulture = _oldCulture;
            if (Directory.Exists(_temporary)) { Directory.Delete(_temporary, true); }
        }
    }
}

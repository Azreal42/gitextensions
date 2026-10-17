using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class MainWindowSelectionTests
{
    [AvaloniaTest]
    public async Task CancellingTheTargetRepositoryLogPreservesTheOldRepositoryItsSelectionsAndHistory()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Linux Git process cancellation scenario."); return; }
        using Fixture fixture = await Fixture.CreateAsync("old repository\n", "old repository\n");
        GitRepository original = await GitRepository.OpenAsync(fixture.Repository);
        string originalHash = await original.ResolveRevisionAsync("HEAD");
        string targetPath = await CreateRepositoryAsync(fixture, "target [literal]", "target-only", "independent target\n");
        GitRepository target = await GitRepository.OpenAsync(targetPath);
        string targetHash = await target.ResolveRevisionAsync("HEAD");
        Assert.That(targetHash, Is.Not.EqualTo(originalHash), "The repositories must have different objects so an accidental backend switch is observable.");

        MainWindow main = fixture.Window;
        TreeView navigation = Field<TreeView>(main, "RepositoryNavigation");
        await ClickBranchAsync(main, "refs/heads/main");
        Invoke(CompareAction(main, "Select as BASE to compare"));
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        string? oldTitle = main.Title, oldPath = RepositoryPath(main).Text, oldFilter = RevisionFilter(main).Text;
        string[] oldHashes = HistoryHashes(main), oldSelection = SelectedHashes(main), oldReferences = NavigationReferences(main);
        string oldHistoryJson = JsonSerializer.Serialize(new RepositoryHistoryStore().Load());
        string? oldSelectedReference = ((navigation.SelectedItem as TreeViewItem)?.Tag as GitReference)?.Name;
        Task? opening = null;
        using (BlockingGitLog block = new(fixture, targetPath))
        {
            try
            {
                opening = main.OpenRepositoryAsync(targetPath);
                await UntilAsync(() => File.Exists(block.Marker) && new FileInfo(block.Marker).Length > 0);
                int pid = int.Parse(await File.ReadAllTextAsync(block.Marker), System.Globalization.CultureInfo.InvariantCulture);
                Assert.That(main.IsBusy, Is.True);
                Assert.That(history.IsEnabled, Is.False);
                ClickLabel(main, "Cancel operation");
                await opening.WaitAsync(TimeSpan.FromSeconds(5));
                await UntilAsync(() => !main.IsBusy && !Directory.Exists("/proc/" + pid));
                Assert.Multiple(() =>
                {
                    Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
                    Assert.That(main.Title, Is.EqualTo(oldTitle));
                    Assert.That(RepositoryPath(main).Text, Is.EqualTo(oldPath));
                    Assert.That(RevisionFilter(main).Text, Is.EqualTo(oldFilter));
                    Assert.That(HistoryHashes(main), Is.EqualTo(oldHashes));
                    Assert.That(SelectedHashes(main), Is.EqualTo(oldSelection));
                    Assert.That(NavigationReferences(main), Is.EqualTo(oldReferences));
                    Assert.That(((navigation.SelectedItem as TreeViewItem)?.Tag as GitReference)?.Name, Is.EqualTo(oldSelectedReference));
                    Assert.That(CompareAction(main, "Compare to BASE").IsEnabled, Is.True);
                    Assert.That(JsonSerializer.Serialize(new RepositoryHistoryStore().Load()), Is.EqualTo(oldHistoryJson));
                    Assert.That(history.IsEnabled, Is.True);
                });
            }
            finally
            {
                if (main.IsBusy) { ClickLabel(main, "Cancel operation"); }
                if (opening is not null) { await opening.WaitAsync(TimeSpan.FromSeconds(5)); }
            }
        }

        // Refresh is a user-visible proof of the retained backend, without reading private fields.
        history.Focus(); Press(main, PhysicalKey.F5); await MainIdleAsync(main);
        Assert.That(HistoryHashes(main), Is.EqualTo(new[] { originalHash }));
        Assert.That(NavigationReferences(main), Is.EqualTo(oldReferences));
        Assert.That(RepositoryPath(main).Text, Is.EqualTo(fixture.Repository));
        Assert.That(new RepositoryHistoryStore().Load().Recent.Any(entry => entry.Path == targetPath), Is.False);
        Assert.That((await original.GetStatusAsync()).Changes, Is.Empty);
        Assert.That((await target.GetStatusAsync()).Changes, Is.Empty);
    }

    [AvaloniaTest]
    public async Task OpeningAnotherRepositoryClearsTheHomonymousSidebarSelectionSoItsBranchCanBeClicked()
    {
        using Fixture fixture = await Fixture.CreateAsync("old repository\n", "old repository\n");
        MainWindow main = fixture.Window;
        await ClickBranchAsync(main, "refs/heads/main");
        Assert.That(RevisionFilter(main).Text, Is.EqualTo("refs/heads/main"));

        string targetPath = await CreateRepositoryAsync(fixture, "homonymous repository", "main", "new main\n");
        GitRepository target = await GitRepository.OpenAsync(targetPath);
        string mainHash = await target.ResolveRevisionAsync("HEAD");
        await target.CreateBranchAsync(new GitCreateBranchOptions("feature", Checkout: true));
        await CommitAsync(target, "feature-only\n", "Feature-only commit");
        string featureHash = await target.ResolveRevisionAsync("HEAD");
        await target.CheckoutAsync("main");
        await main.OpenRepositoryAsync(targetPath); await MainIdleAsync(main);
        Assert.That(Field<TreeView>(main, "RepositoryNavigation").SelectedItem, Is.Null);
        Assert.That(RevisionFilter(main).Text, Is.Null.Or.Empty);
        Assert.That(HistoryHashes(main), Is.EquivalentTo(new[] { mainHash, featureHash }));

        await ClickBranchAsync(main, "refs/heads/main");
        Assert.That(RevisionFilter(main).Text, Is.EqualTo("refs/heads/main"));
        Assert.That(HistoryHashes(main), Is.EqualTo(new[] { mainHash }), "Clicking the homonymous branch must apply its filter, rather than leaving the all-branches graph displayed.");
        Assert.That(((Field<TreeView>(main, "RepositoryNavigation").SelectedItem as TreeViewItem)?.Tag as GitReference)?.Name, Is.EqualTo("refs/heads/main"));
        Assert.That((await target.GetStatusAsync()).Changes, Is.Empty);
    }

    [AvaloniaTest]
    public async Task FilteringOutThePrimaryCommitKeepsOnlyTheSurvivingSecondarySelection()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string parentHash = await repository.ResolveRevisionAsync("HEAD");
        await CommitAsync(repository, "older selected\n", "match older selected");
        string surviving = await repository.ResolveRevisionAsync("HEAD");
        await CommitAsync(repository, "newer unselected\n", "match newer unselected");
        string firstResult = await repository.ResolveRevisionAsync("HEAD");
        await CommitAsync(repository, "excluded primary\n", "excluded primary");
        string excluded = await repository.ResolveRevisionAsync("HEAD");
        MainWindow main = fixture.Window;
        await main.OpenRepositoryAsync(fixture.Repository);
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        history.SelectedItems!.Clear(); history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == excluded);
        await MainIdleAsync(main);
        history.SelectedItems.Add(history.Items.OfType<GitCommit>().Single(commit => commit.Hash == surviving));
        await MainIdleAsync(main);
        Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(excluded));
        Assert.That(SelectedHashes(main), Is.EquivalentTo(new[] { excluded, surviving }));

        ClickLabel(main, "Filter");
        await UntilAsync(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        HistoryFilterDialog filter = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        Field<CheckBox>(filter, "MessageCheck").IsChecked = true;
        Field<TextBox>(filter, "Message").Text = "match";
        ClickLabel(filter, "OK"); await UntilAsync(() => !main.OwnedWindows.Contains(filter)); await MainIdleAsync(main);
        Assert.That(HistoryHashes(main), Is.EqualTo(new[] { firstResult, surviving }));
        Assert.That(SelectedHashes(main), Is.EqualTo(new[] { surviving }), "The first filtered result was never selected and must not be added to the comparison.");
        Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(surviving));
        Invoke(CompareAction(main, "Compare selected commits"));
        CompareRevisionsDialog comparison = await OwnedComparisonAsync(main);
        try
        {
            Assert.That(comparison.Comparison!.Base.Revision, Is.EqualTo(parentHash));
            Assert.That(comparison.Comparison.Compare.Revision, Is.EqualTo(surviving));
            Assert.That(ComparisonDiff(comparison), Does.Contain("+older selected").And.Not.Contain("newer unselected"));
        }
        finally { comparison.Close(); }
        await UntilAsync(() => !main.IsBusy && !main.OwnedWindows.Contains(comparison));
        Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
    }

    [AvaloniaTest]
    public async Task TheContextComparisonOfOneCommitMatchesControlRAndStillOrdersMultipleSelections()
    {
        using Fixture fixture = await Fixture.CreateAsync("parent\n", "parent\n");
        // Ctrl+R is BASE by default in Windows. This regression exercises an
        // explicit user binding for the existing selected-commits comparison.
        HotkeyBindings hotkeys = DesktopHotkeys.Current.Clone();
        hotkeys.Set("RevisionGrid", HotkeyCatalog.Find("RevisionGrid", "CompareToBase")!.Code, null);
        hotkeys.Set("RevisionGrid", HotkeyCatalog.Find("RevisionGrid", "CompareSelectedCommits")!.Code, new(Key.R, KeyModifiers.Control));
        new HotkeyPreferencesStore().Save(hotkeys);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string parent = await repository.ResolveRevisionAsync("HEAD");
        await CommitAsync(repository, "selected\n", "Single selected commit");
        string selected = await repository.ResolveRevisionAsync("HEAD");
        MainWindow main = fixture.Window;
        await main.OpenRepositoryAsync(fixture.Repository);
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == selected); await MainIdleAsync(main);
        Assert.That(SelectedHashes(main), Is.EqualTo(new[] { selected }));

        history.Focus(); Press(main, PhysicalKey.R, RawInputModifiers.Control);
        CompareRevisionsDialog shortcut = await OwnedComparisonAsync(main);
        string expectedDiff;
        try
        {
            Assert.That(shortcut.Comparison!.Base.Revision, Is.EqualTo(parent));
            Assert.That(shortcut.Comparison.Compare.Revision, Is.EqualTo(selected));
            expectedDiff = ComparisonDiff(shortcut);
            Assert.That(expectedDiff, Does.Contain("-parent").And.Contain("+selected"));
        }
        finally { shortcut.Close(); }
        await UntilAsync(() => !main.IsBusy && !main.OwnedWindows.Contains(shortcut));
        Invoke(CompareAction(main, "Compare selected commits"));
        CompareRevisionsDialog context = await OwnedComparisonAsync(main);
        try
        {
            Assert.That(context.Comparison!.Base.Revision, Is.EqualTo(parent));
            Assert.That(context.Comparison.Compare.Revision, Is.EqualTo(selected));
            Assert.That(ComparisonDiff(context), Is.EqualTo(expectedDiff));
        }
        finally { context.Close(); }
        await UntilAsync(() => !main.IsBusy && !main.OwnedWindows.Contains(context));

        history.SelectedItems!.Clear(); history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == parent); await MainIdleAsync(main);
        history.SelectedItems.Add(history.Items.OfType<GitCommit>().Single(commit => commit.Hash == selected)); await MainIdleAsync(main);
        Invoke(CompareAction(main, "Compare selected commits"));
        CompareRevisionsDialog multiple = await OwnedComparisonAsync(main);
        try
        {
            Assert.That(multiple.Comparison!.Base.Revision, Is.EqualTo(parent));
            Assert.That(multiple.Comparison.Compare.Revision, Is.EqualTo(selected));
            Assert.That(ComparisonDiff(multiple), Is.EqualTo(expectedDiff));
        }
        finally { multiple.Close(); }
        await UntilAsync(() => !main.IsBusy && !main.OwnedWindows.Contains(multiple));
        Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
    }

    private static async Task<string> CreateRepositoryAsync(Fixture fixture, string name, string branch, string content)
    {
        string path = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, name); Directory.CreateDirectory(path);
        foreach (string[] arguments in new[]
        {
            new[] { "init", "-b", branch }, new[] { "config", "user.name", "Target Tester" }, new[] { "config", "user.email", "target@example.invalid" },
            new[] { "config", "commit.gpgsign", "false" }, new[] { "config", "core.hooksPath", Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "no-hooks") }
        })
        { GitResult result = await fixture.Git.RunAsync(path, arguments); Assert.That(result.Success, Is.True, result.StandardError); }
        GitRepository repository = await GitRepository.OpenAsync(path); await CommitAsync(repository, content, "Independent target commit"); return path;
    }
    private static async Task CommitAsync(GitRepository repository, string content, string message)
    { await File.WriteAllTextAsync(Path.Combine(repository.Path, "file.txt"), content); await repository.StageAllAsync(); await repository.CommitAsync(message); }
    private static T Field<T>(Window owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static TextBox RepositoryPath(MainWindow main) => main.GetVisualDescendants().OfType<TextBox>().Single(box => box.Watermark == "Repository directory");
    private static ComboBox RevisionFilter(MainWindow main) => main.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "BranchHistoryFilter");
    private static string[] HistoryHashes(MainWindow main) => Field<ListBox>(main, "RevisionHistory").Items.OfType<GitCommit>().Select(commit => commit.Hash).ToArray();
    private static string[] SelectedHashes(MainWindow main) => Field<ListBox>(main, "RevisionHistory").SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash).ToArray();
    private static IEnumerable<TreeViewItem> NavigationNodes(MainWindow main) => BrowseTestHelpers.NavigationNodes(Field<TreeView>(main, "RepositoryNavigation"));
    private static string[] NavigationReferences(MainWindow main) => NavigationNodes(main).Select(node => node.Tag).OfType<GitReference>().Select(reference => reference.Name + ":" + reference.Hash).ToArray();
    private static MenuItem CompareAction(MainWindow main, string label) => Field<ListBox>(main, "RevisionHistory").ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Header as string == "Compare").Items.OfType<MenuItem>().Single(item => item.Header as string == label);
    private static string ComparisonDiff(CompareRevisionsDialog dialog) => Field<FileHistoryContentView>(dialog, "ComparisonDiff").TextView.Text;
    private static void Invoke(MenuItem item) { Assert.That(item.IsEnabled, Is.True, item.Header?.ToString()); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
    private static void ClickLabel(Window owner, string label)
    {
        Button button = owner.GetVisualDescendants().OfType<Button>().Single(button => (button.Content as string == label || label == "Filter" && button.Name == "BrowseAdvancedFilter" || label == "Cancel operation" && button.Name == "BrowseCancelOperation"));
        Assert.That(button.IsEnabled && button.IsEffectivelyVisible, Is.True, label); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static async Task ClickBranchAsync(MainWindow main, string name)
    {
        main.UpdateLayout(); TreeViewItem branch = NavigationNodes(main).Single(node => node.Tag is GitReference reference && reference.Name == name);
        Assert.That(branch.IsEffectivelyVisible, Is.True);
        Point point = branch.TranslatePoint(new Point(48, 10), main)!.Value;
        main.MouseMove(point); main.MouseDown(point, MouseButton.Left); main.MouseUp(point, MouseButton.Left);
        await MainIdleAsync(main);
        Assert.That(((Field<TreeView>(main, "RepositoryNavigation").SelectedItem as TreeViewItem)?.Tag as GitReference)?.Name, Is.EqualTo(name));
    }
    private static void Press(Window owner, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { owner.KeyPressQwerty(key, modifiers); if (owner.IsVisible) { owner.KeyReleaseQwerty(key, modifiers); } }
    private static async Task<CompareRevisionsDialog> OwnedComparisonAsync(MainWindow main)
    {
        await UntilAsync(() => main.OwnedWindows.OfType<CompareRevisionsDialog>().Any());
        CompareRevisionsDialog dialog = main.OwnedWindows.OfType<CompareRevisionsDialog>().Single();
        await UntilAsync(() => !dialog.IsBusy); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); return dialog;
    }
    private static async Task MainIdleAsync(MainWindow main)
    { await UntilAsync(() => !main.IsBusy); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); }
    private static async Task UntilAsync(Func<bool> predicate)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); while (!predicate()) { await Task.Delay(20, timeout.Token); } }

    private sealed class BlockingGitLog : IDisposable
    {
        private readonly string? _oldPath = Environment.GetEnvironmentVariable("PATH");
        public string Marker { get; }
        public BlockingGitLog(Fixture fixture, string target)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
            string executable = (_oldPath ?? "").Split(Path.PathSeparator).Where(directory => !string.IsNullOrEmpty(directory))
                .Select(directory => Path.Combine(directory, "git")).First(File.Exists);
            string directory = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "blocking-git-bin"); Directory.CreateDirectory(directory);
            Marker = Path.Combine(directory, "target-log.pid"); string script = Path.Combine(directory, "git");
            File.WriteAllText(script, "#!/usr/bin/python3\nimport os,pathlib,sys,time\n" +
                "if os.path.realpath(os.getcwd()) == os.path.realpath(" + JsonSerializer.Serialize(target) + ") and 'log' in sys.argv[1:]:\n" +
                "    pathlib.Path(" + JsonSerializer.Serialize(Marker) + ").write_text(str(os.getpid()))\n    time.sleep(30)\n" +
                "os.execv(" + JsonSerializer.Serialize(executable) + ", [" + JsonSerializer.Serialize(executable) + "] + sys.argv[1:])\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + _oldPath);
        }
        public void Dispose() => Environment.SetEnvironmentVariable("PATH", _oldPath);
    }
}

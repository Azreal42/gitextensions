using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FileMenuFocusedSelectionTests
{
    [AvaloniaTest]
    [TestCase(0, false)]
    [TestCase(0, true)]
    [TestCase(1, false)]
    [TestCase(1, true)]
    [TestCase(2, false)]
    [TestCase(2, true)]
    public async Task RealMultiSelectionAndRightClickPutThatLeafOnTheRightOfTheNativeDiff(int hostKind, bool rightClickFirst)
    {
        using Fixture f = await Fixture.CreateAsync();
        foreach (string path in new[] { "first.txt", "second.txt" }) { await File.WriteAllTextAsync(f.PathInRepository(path), path + " before\n"); }
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Before files"); string before = await f.Repository.ResolveRevisionAsync("HEAD");
        foreach (string path in new[] { "first.txt", "second.txt" }) { await File.WriteAllTextAsync(f.PathInRepository(path), path + " after\n"); }
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("After files"); string after = await f.Repository.ResolveRevisionAsync("HEAD");
        string output = f.Output("focused-native-diff.json"), helper = f.Output("focused-native-diff.py");
        await File.WriteAllTextAsync(helper, "#!/usr/bin/python3\nimport json,sys\nwith open(" + JsonSerializer.Serialize(output)
            + ", 'w') as f: json.dump({'left':open(sys.argv[1]).read(),'right':open(sys.argv[2]).read()},f)\n");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        await f.Repository.SetConfigAsync("difftool.test-focused.cmd", "'" + helper.Replace("'", "'\\''", StringComparison.Ordinal) + "' \"$LOCAL\" \"$REMOTE\"");
        await f.Repository.SetConfigAsync("difftool.unused.cmd", "true");
        Window owner = new() { Width = 1440, Height = 900 }; MainWindow? browse = null; CompareRevisionsDialog? compare = null; Task? shown = null;
        try
        {
            Window window; FileStatusListView view;
            if (hostKind == 1)
            {
                owner.Show(); compare = new(f.Repository, new GitCompareRevision(before), new GitCompareRevision(after), new FileStatusListPreferencesStore(f.Output("view-prefs")));
                shown = compare.ShowDialog(owner); await compare.InitializeAsync(); await Until(() => !compare.IsBusy && compare.SelectionCompletion.IsCompleted);
                window = compare; view = Field<FileStatusListView>(compare, "ComparisonFileStatusList");
            }
            else
            {
                browse = new() { Width = 1440, Height = 900 }; browse.Show(); await browse.OpenRepositoryAsync(f.Repository.Path); await Until(() => !browse.IsBusy);
                TabControl tabs = Field<TabControl>(browse, "DetailsTabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == (hostKind == 2 ? "FileTreeTab" : "DiffTab"));
                await Until(() => !browse.IsBusy); window = browse; view = Field<FileStatusListView>(browse, hostKind == 2 ? "CommitFileStatusList" : "RevisionFileStatusList");
            }
            await Until(() => view.Entries.Count(entry => entry.Path is "first.txt" or "second.txt") == 2); Pump(window);
            TreeViewItem first = view.FindItemByTag(view.Entries.Single(entry => entry.Path == "first.txt").Tag)!;
            TreeViewItem second = view.FindItemByTag(view.Entries.Single(entry => entry.Path == "second.txt").Tag)!;
            Pointer(window, Header(first)); await Until(() => browse?.IsBusy != true && compare?.SelectionCompletion.IsCompleted != false);
            Pointer(window, Header(second), MouseButton.Left, RawInputModifiers.Control);
            Assert.That(view.Tree.SelectedItems!.OfType<TreeViewItem>().ToArray(), Is.EquivalentTo(new[] { first, second }));
            byte[] index = await File.ReadAllBytesAsync(f.PathInRepository(".git/index"));
            TreeViewItem target = rightClickFirst ? first : second;
            Pointer(window, Header(target), MouseButton.Right); ContextMenu menu = view.Tree.ContextMenu!;
            Assert.That(menu.IsOpen, Is.True); Assert.That(view.Tree.SelectedItems!.OfType<TreeViewItem>().ToArray(), Is.EquivalentTo(new[] { first, second }), "Right-click must retain the other explicit selected leaf.");
            MenuItem difftool = menu.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiOpenWithDifftool");
            PushProcessTestDriver.Pointer(difftool); await Until(() => difftool.IsSubMenuOpen && difftool.Items.OfType<MenuItem>().Any(item => item.Bounds.Height > 0));
            MenuItem two = difftool.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiDiffTwoSelected");
            Assert.That(two.IsEnabled, Is.True); PushProcessTestDriver.Pointer(two);
            await Until(() => two.IsSubMenuOpen && two.Items.OfType<MenuItem>().Any(item => item.Bounds.Height > 0));
            MenuItem record = two.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiDiffTwoSelectedTool0"); PushProcessTestDriver.Pointer(record);
            await Until(() => File.Exists(output) && browse?.IsBusy != true && compare?.IsBusy != true && compare?.SelectionCompletion.IsCompleted != false);
            using JsonDocument result = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            Assert.Multiple(() =>
            {
                Assert.That(result.RootElement.GetProperty("left").GetString(), Is.EqualTo((rightClickFirst ? "second.txt" : "first.txt") + " after\n"), "The nonfocused selected leaf is the first side.");
                Assert.That(result.RootElement.GetProperty("right").GetString(), Is.EqualTo((rightClickFirst ? "first.txt" : "second.txt") + " after\n"), "The actual right-click target is the second side, independently of SelectedItem.");
            });
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(after));
            foreach (string path in new[] { "first.txt", "second.txt" }) { Assert.That(await File.ReadAllTextAsync(f.PathInRepository(path)), Is.EqualTo(path + " after\n")); }
            Assert.That(view.SelectedEntries.Select(entry => entry.Path), Is.EquivalentTo(new[] { "first.txt", "second.txt" }));
            Pump(window);
            PhysicalKey arrow = rightClickFirst ? PhysicalKey.ArrowDown : PhysicalKey.ArrowUp;
            window.KeyPressQwerty(arrow, RawInputModifiers.None); window.KeyReleaseQwerty(arrow, RawInputModifiers.None);
            await Until(() => browse?.IsBusy != true && compare?.SelectionCompletion.IsCompleted != false
                && view.SelectedEntries.Count == 1 && view.SelectedEntries[0].Path == (rightClickFirst ? "second.txt" : "first.txt"));
            Assert.That(browse?.LastException, Is.Null); Assert.That(compare?.LastError, Is.Null);
        }
        finally
        {
            compare?.Close(); if (shown is not null) { await shown; } browse?.Close(); owner.Close();
        }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task BrowseRightClickRendersTheNewSingletonWhileItsRealMenuStaysOpen(bool fileTree)
    {
        using Fixture f = await Fixture.CreateAsync();
        foreach (string path in new[] { "first.txt", "second.txt" }) { await File.WriteAllTextAsync(f.PathInRepository(path), path + " before\n"); }
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Before files");
        foreach (string path in new[] { "first.txt", "second.txt" }) { await File.WriteAllTextAsync(f.PathInRepository(path), path + " after\n"); }
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("After files");
        MainWindow main = new() { Width = 1440, Height = 900 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Repository.Path); await Until(() => !main.IsBusy);
            TabControl tabs = Field<TabControl>(main, "DetailsTabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == (fileTree ? "FileTreeTab" : "DiffTab"));
            await Until(() => !main.IsBusy); FileStatusListView view = Field<FileStatusListView>(main, fileTree ? "CommitFileStatusList" : "RevisionFileStatusList");
            await Until(() => view.Entries.Count(entry => entry.Path is "first.txt" or "second.txt") == 2); Pump(main);
            TreeViewItem first = view.FindItemByTag(view.Entries.Single(entry => entry.Path == "first.txt").Tag)!;
            TreeViewItem second = view.FindItemByTag(view.Entries.Single(entry => entry.Path == "second.txt").Tag)!;
            string Content() => fileTree ? Field<FileContentView>(main, "CommitFileContent").TextView.Text : Field<DiffView>(main, "CommitDiff").Text;
            Pointer(main, Header(first)); await Until(() => !main.IsBusy && Content().Contains("first.txt after", StringComparison.Ordinal));
            byte[] index = await File.ReadAllBytesAsync(f.PathInRepository(".git/index"));
            Pointer(main, Header(second), MouseButton.Right); ContextMenu menu = view.Tree.ContextMenu!;
            Assert.That(menu.IsOpen, Is.True);
            await Until(() => !main.IsBusy && Content().Contains("second.txt after", StringComparison.Ordinal));
            Assert.That(menu.IsOpen, Is.True, "Scoped detail loading must leave the actual menu open.");
            Assert.That(view.FindItemByTag(second.Tag!), Is.SameAs(second), "A detail read cannot rebuild the tree.");
            Assert.That(view.SelectedEntries.Select(entry => entry.Path), Is.EqualTo(new[] { "second.txt" }));
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/index")), Is.EqualTo(index));
            TopLevel popup = TopLevel.GetTopLevel(menu)!; popup.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            if (menu.IsOpen) { popup.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); }
            await Until(() => !menu.IsOpen); main.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            await Until(() => !main.IsBusy && view.SelectedEntries.Count == 1 && view.SelectedEntries[0].Path == "first.txt");
            Assert.That(main.LastException, Is.Null);
        }
        finally { main.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task BrowseShowInFileTreeHandsTheFirstArrowToTheSelectedTargetRow(bool nested)
    {
        using Fixture f = await Fixture.CreateAsync();
        string prefix = nested ? "nested/" : "";
        if (nested) { Directory.CreateDirectory(f.PathInRepository("nested")); }
        foreach (string name in new[] { "first.txt", "second.txt" }) { await File.WriteAllTextAsync(f.PathInRepository(prefix + name), name + " before\n"); }
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Before handoff");
        foreach (string name in new[] { "first.txt", "second.txt" }) { await File.WriteAllTextAsync(f.PathInRepository(prefix + name), name + " after\n"); }
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("After handoff");
        MainWindow main = new() { Width = 1440, Height = 900 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Repository.Path); await Until(() => !main.IsBusy);
            TabControl tabs = Field<TabControl>(main, "DetailsTabs");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "DiffTab");
            FileStatusListView diff = Field<FileStatusListView>(main, "RevisionFileStatusList");
            await Until(() => !main.IsBusy && diff.Entries.Count(entry => entry.Path == prefix + "second.txt") == 1); Pump(main);
            TreeViewItem second = diff.FindItemByTag(diff.Entries.Single(entry => entry.Path == prefix + "second.txt").Tag)!;
            Pointer(main, Header(second)); await Until(() => !main.IsBusy);
            byte[] index = await File.ReadAllBytesAsync(f.PathInRepository(".git/index"));
            Pointer(main, Header(second), MouseButton.Right); ContextMenu menu = diff.Tree.ContextMenu!;
            Assert.That(menu.IsOpen, Is.True);
            MenuItem show = menu.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiShowInFileTree");
            PushProcessTestDriver.Pointer(show);
            FileStatusListView tree = Field<FileStatusListView>(main, "CommitFileStatusList");
            await Until(() => !main.IsBusy && !menu.IsOpen && (tabs.SelectedItem as TabItem)?.Name == "FileTreeTab"
                && tree.SelectedEntries.Count == 1 && tree.SelectedEntries[0].Path == prefix + "second.txt");
            Pump(main);
            main.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            await Until(() => !main.IsBusy && tree.SelectedEntries.Count == 1 && tree.SelectedEntries[0].Path == prefix + "first.txt");
            Assert.That(Field<FileContentView>(main, "CommitFileContent").TextView.Text, Does.Contain("first.txt after"));
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(main.LastException, Is.Null);
        }
        finally { main.Close(); }
    }

    private static Control Header(TreeViewItem item) => item.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header" && control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() == item);
    private static void Pointer(Window window, Control row, MouseButton button = MouseButton.Left, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Pump(window); Point point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        Assert.That((window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True, "Actual hit-tested file row required.");
        window.MouseMove(point); window.MouseDown(point, button, modifiers); window.MouseUp(point, button, modifiers); Pump(window);
    }
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Pump(TopLevel root) { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    { Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True); }
}

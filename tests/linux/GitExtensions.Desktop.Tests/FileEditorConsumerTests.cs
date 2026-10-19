using System.Diagnostics;
using System.Security.Cryptography;
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
public sealed class FileEditorConsumerTests
{
    [AvaloniaTest, TestCase(false, true), TestCase(true, true), TestCase(false, false), TestCase(true, false)]
    public async Task RealBrowseAndCompareEditMenusUseOnlyTheReadyViewerPositionAndRefreshWithoutStaging(bool browse, bool documentReady)
    {
        using Fixture git = await Fixture.CreateAsync(); string first = (await git.GitAsync("rev-parse", "HEAD")).StandardOutput.Trim();
        await git.ChangeAsync("committed first\ncommitted second\n", "Editor historical source");
        await File.WriteAllTextAsync(git.File, "working first\nworking second\n");
        byte[] index = await File.ReadAllBytesAsync(git.PathInRepository(".git/index")); string head = (await git.GitAsync("rev-parse", "HEAD")).StandardOutput;
        MainWindow? main = null; CompareRevisionsDialog? compare = null; Window owner = new() { Width = 1300, Height = 850 }; owner.Show();
        try
        {
            FileStatusListView view; Window host;
            if (browse)
            {
                main = new() { Width = 1400, Height = 900, ExternalLauncher = _ => throw new AssertionException("Edit must not launch an external application.") }; main.Show();
                await main.OpenRepositoryAsync(git.Repository.Path); await Until(() => !main.IsBusy);
                TabControl tabs = Field<TabControl>(main, "DetailsTabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Name == "DiffTab");
                view = Field<FileStatusListView>(main, "RevisionFileStatusList"); host = main;
            }
            else
            {
                compare = new(git.Repository, new GitCompareRevision(first), new GitCompareRevision("HEAD"), new FileStatusListPreferencesStore(git.Output("editor-menu-prefs")))
                    { Width = 1300, Height = 800, FileMenuExternalLauncher = _ => throw new AssertionException("Edit must not launch an external application.") };
                _ = compare.ShowDialog(owner); await compare.InitializeAsync(); await Until(() => !compare.IsBusy && compare.SelectionCompletion.IsCompleted);
                view = Field<FileStatusListView>(compare, "ComparisonFileStatusList"); host = compare;
            }
            await Until(() => view.Entries.Any(e => e.Path == "file.txt"));
            object tag = view.Entries.First(e => e.Path == "file.txt").Tag; view.SelectTags([tag]); Pump(host);
            if (compare is not null) { await Until(() => compare.SelectionCompletion.IsCompleted && !compare.IsBusy); }
            else { await main!.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15)); await Until(() => !main.IsBusy); }
            DiffView viewer = browse ? Field<DiffView>(host, "CommitDiff") : Field<FileHistoryContentView>(host, "ComparisonDiff").TextView;
            await Until(() => viewer.IsDocumentReady && viewer.MaxFileLine >= 2);
            viewer.FileEncodingName = "windows-1252"; Pump(host);
            if (main is not null) { await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15)); }
            await Until(() => viewer.IsDocumentReady && (compare is null || compare.SelectionCompletion.IsCompleted));
            Assert.That(viewer.GoToFileLine(2), Is.True); Assert.That(viewer.CurrentFileLine, Is.EqualTo(2));
            if (!documentReady) { viewer.BeginDocumentLoad(); Assert.That(viewer.Text, Is.Not.Empty, "The old pixels are deliberately retained while their document is invalidated."); }
            TreeViewItem row = view.FindItemByTag(tag)!; Pointer(host, Header(row), MouseButton.Right);
            ContextMenu menu = view.Tree.ContextMenu!; await Until(() => menu.IsOpen);
            MenuItem edit = menu.Items.OfType<MenuItem>().Single(i => i.Name == "tsmiEditWorkingDirectoryFile"); Assert.That(edit.IsEnabled, Is.True);
            PushProcessTestDriver.Pointer(edit);
            await Until(() => host.OwnedWindows.OfType<FileEditorDialog>().Any(d => d.Snapshot is not null && !d.IsBusy));
            FileEditorDialog editor = host.OwnedWindows.OfType<FileEditorDialog>().Single();
            Assert.That(editor.Owner, Is.SameAs(host)); Assert.That(editor.Snapshot!.Path, Is.EqualTo(git.File));
            Assert.That(editor.Viewer.CurrentFileLine, Is.EqualTo(documentReady ? 2 : 1), "Only a ready document belonging to these endpoints supplies its position.");
            Assert.That(editor.Snapshot.EncodingName, Is.EqualTo(documentReady ? "windows-1252" : "utf-8"), "An invalidated viewer cannot leak its codec override to the working file.");
            Assert.That(editor.Viewer.Text, Is.EqualTo("working first\nworking second\n"), "A historical row opens its live working file rather than publishing the historical blob.");
            Edit(editor, "saved by internal editor"); Press(editor, PhysicalKey.S, RawInputModifiers.Control); await Until(() => editor.HasFileChanges && !editor.IsBusy);
            Assert.That(editor.LastError, Is.Null, editor.LastException?.ToString()); editor.Close();
            await Until(() => !editor.IsVisible && (main is null || !main.IsBusy) && (compare is null || !compare.IsBusy && compare.SelectionCompletion.IsCompleted));
            Assert.That(File.ReadAllText(git.File), Is.EqualTo("saved by internal editor")); Assert.That(await File.ReadAllBytesAsync(git.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That((await git.GitAsync("rev-parse", "HEAD")).StandardOutput, Is.EqualTo(head));
            Assert.That(main?.LastException, Is.Null); Assert.That(compare?.LastError, Is.Null);
        }
        finally { if (compare is not null) { await CloseTree(compare); } if (main is not null) { await CloseTree(main); } owner.Close(); }
    }

    [AvaloniaTest, TestCase("normal"), TestCase("linked"), TestCase("bare"), TestCase("absorbed")]
    public async Task RealMaintenanceMenuEditsTheGitCommonConfigWithWarningAndNoIndexOrXmlMutation(string topology)
    {
        using Fixture git = await Fixture.CreateAsync(); using Fixture child = await Fixture.CreateAsync(); string path = git.Repository.Path;
        if (topology == "linked") { path = git.Output("linked"); await git.GitAsync("worktree", "add", "-b", "editor-linked", path); }
        else if (topology == "bare") { path = git.Output("bare.git"); await git.GitAsync("clone", "--bare", git.Repository.Path, path); }
        else if (topology == "absorbed")
        {
            await git.GitAsync("-c", "protocol.file.allow=always", "submodule", "add", "--", child.Repository.Path, "nested-child");
            await git.GitAsync("submodule", "absorbgitdirs", "--", "nested-child"); path = git.PathInRepository("nested-child");
        }
        string common = (await git.GitAsync("-C", path, "rev-parse", "--path-format=absolute", "--git-common-dir")).StandardOutput.TrimEnd('\r', '\n');
        string config = System.IO.Path.Combine(common, "config"); string original = await File.ReadAllTextAsync(config);
        string distributed = System.IO.Path.Combine(common, "GitExtensions.settings"); bool distributedExisted = File.Exists(distributed);
        byte[]? xml = distributedExisted ? File.ReadAllBytes(distributed) : null;
        string index = (await git.GitAsync("-C", path, "rev-parse", "--path-format=absolute", "--git-path", "index")).StandardOutput.TrimEnd('\r', '\n');
        string? indexHash = File.Exists(index) ? Hash(index) : null;
        MainWindow main = new() { Width = 1350, Height = 850, ExternalLauncher = _ => throw new AssertionException("Raw config edits must remain internal.") }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(path); await Until(() => !main.IsBusy);
            MenuItem repository = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(i => i.Header as string == "Repository");
            MenuItem maintenance = repository.Items.OfType<MenuItem>().Single(i => i.Name == "BrowseGitMaintenance");
            MenuItem edit = maintenance.Items.OfType<MenuItem>().Single(i => i.Name == "editLocalGitConfigToolStripMenuItem");
            repository.IsSubMenuOpen = true; maintenance.IsSubMenuOpen = true; Pump(main); Assert.That(edit.IsEnabled, Is.True); PushProcessTestDriver.Pointer(edit);
            await Until(() => main.OwnedWindows.OfType<FileEditorDialog>().Any(d => d.Snapshot is not null && !d.IsBusy)); FileEditorDialog editor = main.OwnedWindows.OfType<FileEditorDialog>().Single();
            Assert.That(editor.Snapshot!.Path, Is.EqualTo(config)); Assert.That(Field<Border>(editor, "panelMessage").IsVisible, Is.True);
            string expected = original + "\n# explicit raw editor save"; Edit(editor, expected); Press(editor, PhysicalKey.S, RawInputModifiers.Control);
            await Until(() => !editor.IsBusy && editor.HasFileChanges); Assert.That(editor.LastError, Is.Null, editor.LastException?.ToString()); editor.Close();
            await Until(() => !main.IsBusy && !editor.IsVisible); Assert.That(File.ReadAllText(config), Is.EqualTo(expected)); Assert.That(main.LastException, Is.Null);
            Assert.That(File.Exists(index) ? Hash(index) : null, Is.EqualTo(indexHash));
            Assert.That(File.Exists(distributed), Is.EqualTo(distributedExisted)); if (xml is not null) { Assert.That(File.ReadAllBytes(distributed), Is.EqualTo(xml)); }
        }
        finally { await CloseTree(main); }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Edit(FileEditorDialog d, string text) { d.Viewer.FocusEditor(); Press(d, PhysicalKey.A, RawInputModifiers.Control); d.KeyTextInput(text); Pump(d); }
    private static Control Header(TreeViewItem row) => row.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_Header" && c.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() == row);
    private static void Pointer(Window window, Control target, MouseButton button)
    {
        Pump(window); Point point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        Assert.That((window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(target), Is.True);
        window.MouseMove(point); window.MouseDown(point, button); window.MouseUp(point, button); Pump(window);
    }
    private static T Field<T>(TopLevel owner, string name) where T : Control => PushProcessTestDriver.Field<T>(owner, name);
    private static void Pump(TopLevel owner) { Dispatcher.UIThread.RunJobs(); if (owner.IsVisible) { owner.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static void Press(TopLevel owner, PhysicalKey key, RawInputModifiers modifiers) { owner.KeyPressQwerty(key, modifiers); if (owner.IsVisible) { owner.KeyReleaseQwerty(key, modifiers); } Pump(owner); }
    private static async Task Until(Func<bool> complete) { Stopwatch s = Stopwatch.StartNew(); while (!complete() && s.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(complete(), Is.True, "Editor consumer did not settle."); }
    private static async Task CloseTree(Window window)
    {
        for (int i = 0; window.IsVisible && i < 100; i++)
        {
            foreach (Window child in window.OwnedWindows.ToArray())
            {
                if (child is FileEditorDialog editor)
                {
                    foreach (Window modal in editor.OwnedWindows.ToArray())
                    {
                        Button? choice = modal.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name is "FileEditorSaveNo" or "MessageDialogAccept");
                        if (choice is not null) { PushProcessTestDriver.Pointer(choice); } else { modal.Close(); }
                    }
                    editor.Close();
                }
                else { child.Close(); }
            }
            window.Close(); Dispatcher.UIThread.RunJobs(); await Task.Delay(10);
        }
        Assert.That(window.IsVisible, Is.False, "Consumer and editor must drain before fixture teardown.");
    }
}

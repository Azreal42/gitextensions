using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using ReferenceFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ComparisonGroupsDialogTests
{
    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task DivergentRevisionsShowTheFourOriginalGroupsInOrder(bool dark)
    {
        await using Fixture f = await Fixture.CreateAsync(dark: dark);
        Assert.That(f.Tree.Items.OfType<TreeViewItem>().Select(item => item.Tag).OfType<GitComparisonGroup>().Select(group => group.Kind),
            Is.EqualTo(new[] { GitRevisionDiffGroupKind.Comparison, GitRevisionDiffGroupKind.RangeDiff, GitRevisionDiffGroupKind.BaseToSecond, GitRevisionDiffGroupKind.BaseToFirst }));
        Assert.That(f.Root(GitRevisionDiffGroupKind.Comparison).IsExpanded, Is.True);
        Assert.That(f.Root(GitRevisionDiffGroupKind.BaseToSecond).IsExpanded, Is.False);
        Assert.That(f.Root(GitRevisionDiffGroupKind.BaseToFirst).IsExpanded, Is.False);
        Assert.That(f.Root(GitRevisionDiffGroupKind.RangeDiff).Items, Is.Empty);
        foreach (string name in new[] { "Unequal", "OnlyB", "OnlyA", "Same" })
        { ToggleButton button = Field<ToggleButton>(f.Dialog, "ComparisonFiles" + name); Assert.That(button.IsVisible, Is.True); Assert.That(button.IsChecked, Is.True); }
        Capture(f.Dialog, "comparison-groups-" + (dark ? "dark" : "light") + ".png");
    }

    [AvaloniaTest]
    [TestCase(GitRevisionDiffGroupKind.Comparison)]
    [TestCase(GitRevisionDiffGroupKind.BaseToFirst)]
    [TestCase(GitRevisionDiffGroupKind.BaseToSecond)]
    public async Task FileDiffSourcesSaveAndNativeFileToolUseTheSelectedGroupsPair(GitRevisionDiffGroupKind kind)
    {
        await using Fixture f = await Fixture.CreateAsync();
        await Select(f, kind, "shared.txt");
        string before = kind == GitRevisionDiffGroupKind.Comparison ? "value A\n" : "base shared\n";
        string after = kind == GitRevisionDiffGroupKind.BaseToFirst ? "value A\n" : "value B\n";
        GitComparison selected = f.Dialog.SelectedComparison!;
        Assert.That(selected.Base.Revision, Is.EqualTo(kind == GitRevisionDiffGroupKind.Comparison ? f.A : f.Base));
        Assert.That(selected.Compare.Revision, Is.EqualTo(kind == GitRevisionDiffGroupKind.BaseToFirst ? f.A : f.B));
        Assert.That(Diff(f).Text, Does.Contain("-" + before.TrimEnd()).And.Contain("+" + after.TrimEnd()));
        string identity = Diff(f).DocumentIdentity!;
        for (int tab = 1; tab <= 2; tab++)
        {
            Field<TabControl>(f.Dialog, "ComparisonTabs").SelectedIndex = tab; await Idle(f.Dialog);
            Assert.That(Field<FileHistoryContentView>(f.Dialog, tab == 1 ? "ComparisonBaseView" : "ComparisonCompareView").TextView.Text, Is.EqualTo(tab == 1 ? before : after));
        }
        string savedBase = f.Git.Output("saved-base"), savedCompare = f.Git.Output("saved-compare");
        await f.Dialog.SaveSelectedFileAsync(savedBase, true); await f.Dialog.SaveSelectedFileAsync(savedCompare);
        Assert.That(await File.ReadAllTextAsync(savedBase), Is.EqualTo(before)); Assert.That(await File.ReadAllTextAsync(savedCompare), Is.EqualTo(after));
        await f.Git.GitAsync("config", "diff.guitool", "comparison-fixture"); await f.Git.GitAsync("config", "difftool.comparison-fixture.cmd", "true");
        PushProcessTestDriver.Pointer(Field<Button>(f.Dialog, "ComparisonFileTool")); await Idle(f.Dialog);
        string[] tool = f.Gate.Commands.Last(entry => entry.GetProperty("args").EnumerateArray().Any(arg => arg.GetString() == "difftool")).GetProperty("args").EnumerateArray().Select(arg => arg.GetString()!).ToArray();
        Assert.That(tool, Does.Contain(selected.Base.Revision).And.Contain(selected.Compare.Revision).And.Contain("shared.txt"));
        Assert.That((await f.Git.GitAsync("status", "--porcelain=v1")).StandardOutput, Is.Empty);
        Assert.That(identity, Does.Contain(kind.ToString()));
    }

    [AvaloniaTest]
    public async Task RangeIsARealReadAndRemainsSelectableWhenRegexAndMarkersHideEveryFile()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await Select(f, GitRevisionDiffGroupKind.RangeDiff);
        Assert.That(Diff(f).EffectiveMode, Is.EqualTo(DiffViewMode.RangeDiff)); Assert.That(Diff(f).Text, Is.Not.Empty);
        Assert.That(f.Gate.Commands.Any(entry => entry.GetProperty("args").EnumerateArray().Any(arg => arg.GetString() == "range-diff")), Is.True);
        Assert.That(f.Dialog.SelectedFile, Is.Null);
        foreach (string name in new[] { "ComparisonFileTool", "ComparisonSaveBase", "ComparisonSaveCompare" }) { Assert.That(Field<Button>(f.Dialog, name).IsEnabled, Is.False); }
        Assert.That(Field<Button>(f.Dialog, "ComparisonDirectoryTool").IsEnabled, Is.True);
        Assert.That(Field<TabControl>(f.Dialog, "ComparisonTabs").Items.OfType<TabItem>().Skip(1).All(tab => !tab.IsEnabled), Is.True);
        TextBox editor = await TypeFilter(f, "^nothing-matches$"); await Until(() => f.View.FilteredEntries.Count == 0); await Idle(f.Dialog);
        Assert.That(f.Tree.Items.OfType<TreeViewItem>().Any(item => item.Tag is GitComparisonGroup { Kind: GitRevisionDiffGroupKind.RangeDiff }), Is.True);
        Assert.That(Diff(f).EffectiveMode, Is.EqualTo(DiffViewMode.RangeDiff)); Assert.That(Diff(f).Text, Is.Not.Empty);
        Assert.That(f.Dialog.FocusManager!.GetFocusedElement(), Is.SameAs(editor));
        foreach (string name in new[] { "Unequal", "OnlyB", "OnlyA", "Same" }) { PushProcessTestDriver.Pointer(Field<ToggleButton>(f.Dialog, "ComparisonFiles" + name)); }
        await Select(f, GitRevisionDiffGroupKind.RangeDiff); Assert.That(Diff(f).Text, Is.Not.Empty);
        await Select(f, GitRevisionDiffGroupKind.Comparison); Assert.That(Diff(f).Text, Is.Empty); Assert.That(Diff(f).DocumentIdentity, Is.Null);
    }

    [AvaloniaTest]
    public async Task ParentFolderAndExplicitCrossGroupMultiSelectionStaySeparateFromOneFile()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await Select(f, GitRevisionDiffGroupKind.BaseToFirst); Assert.That(f.Dialog.SelectedFile, Is.Null); Assert.That(Diff(f).Text, Is.Empty);
        TreeViewItem parent = f.Root(GitRevisionDiffGroupKind.BaseToFirst); parent.IsExpanded = true; Pump(f.Dialog);
        TreeViewItem folder = parent.Items.OfType<TreeViewItem>().Single(item => item.Tag is FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder, Key: "nested" });
        PushProcessTestDriver.Pointer(RowTarget(folder)); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile, Is.Null); Assert.That(f.Dialog.SelectedFiles.Select(file => file.Path), Is.EqualTo(new[] { "nested/one.txt" })); Assert.That(Diff(f).Text, Is.Empty);
        await Select(f, GitRevisionDiffGroupKind.BaseToFirst, "shared.txt");
        TreeViewItem second = f.Leaf(GitRevisionDiffGroupKind.BaseToSecond, "shared.txt"); Expand(f.Tree, second); Pump(f.Dialog);
        for (int frame = 0; frame < 10; frame++) { Pump(f.Dialog); }
        Control target = RowTarget(second); Point point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), f.Dialog)!.Value;
        Assert.That((f.Dialog.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(target), Is.True);
        f.Dialog.MouseMove(point); f.Dialog.MouseDown(point, MouseButton.Left, RawInputModifiers.Control); f.Dialog.MouseUp(point, MouseButton.Left, RawInputModifiers.Control); await Idle(f.Dialog);
        Assert.That(f.Dialog.SelectedFile, Is.Null); Assert.That(f.Dialog.SelectedFiles.Count, Is.EqualTo(2));
        Assert.That(f.Dialog.SelectedFileSelections.Select(selection => selection.Group.Kind), Is.EquivalentTo(new[] { GitRevisionDiffGroupKind.BaseToFirst, GitRevisionDiffGroupKind.BaseToSecond }));
        Assert.That(Diff(f).Text, Is.Empty); Assert.That(Diff(f).DocumentIdentity, Is.Null);
    }

    [AvaloniaTest]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task SamePathInDifferentGroupsCancelsTheFormerReadAndKeepsTheFilterEditable(int tab)
    {
        await using Fixture f = await Fixture.CreateAsync();
        await Select(f, GitRevisionDiffGroupKind.Comparison, "shared.txt");
        Field<TabControl>(f.Dialog, "ComparisonTabs").SelectedIndex = tab; await Idle(f.Dialog);
        string[] match = tab == 0 ? new[] { "diff", f.Base, f.A, "shared.txt" } : new[] { "ls-tree", tab == 1 ? f.Base : f.A, "shared.txt" };
        f.Gate.Arm(match); await Select(f, GitRevisionDiffGroupKind.BaseToFirst, "shared.txt", wait: false); await Until(() => f.Gate.Started); int old = f.Gate.BlockedPid;
        TextBox editor = await TypeFilter(f, "shared"); await Until(() => f.View.FilteredEntries.Count == 3);
        Assert.That(f.Dialog.FocusManager!.GetFocusedElement(), Is.SameAs(editor)); Assert.That(f.Dialog.IsBusy, Is.False);
        f.Gate.Disarm(); await Select(f, GitRevisionDiffGroupKind.BaseToSecond, "shared.txt"); await Until(() => !Alive(old)); f.Gate.Release(); await Idle(f.Dialog);
        string text = Field<FileHistoryContentView>(f.Dialog, tab == 0 ? "ComparisonDiff" : tab == 1 ? "ComparisonBaseView" : "ComparisonCompareView").TextView.Text;
        Assert.That(text, tab == 1 ? Is.EqualTo("base shared\n") : Does.Contain("value B").And.Not.Contain("value A"));
        Assert.That(f.Dialog.SelectedGroup!.Kind, Is.EqualTo(GitRevisionDiffGroupKind.BaseToSecond));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task PendingRangeReadIsCancelledByALeafOrClosingTheDialog(bool close)
    {
        await using Fixture f = await Fixture.CreateAsync();
        f.Gate.Arm(["range-diff"]); await Select(f, GitRevisionDiffGroupKind.RangeDiff, wait: false); await Until(() => f.Gate.Started); int pid = f.Gate.BlockedPid;
        if (close) { f.Dialog.Close(); await Until(() => !f.Dialog.IsVisible && f.Dialog.SelectionCompletion.IsCompleted); }
        else { await Select(f, GitRevisionDiffGroupKind.BaseToSecond, "shared.txt"); Assert.That(Diff(f).EffectiveMode, Is.Not.EqualTo(DiffViewMode.RangeDiff)); Assert.That(Diff(f).Text, Does.Contain("value B")); }
        await Until(() => !Alive(pid));
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.Index)]
    [TestCase(GitCompareRevisionKind.WorkingTree)]
    public async Task ArtificialGroupEndpointsRemainLiveWhileTheRangeHeadAndOpeningMergeBaseStayCaptured(GitCompareRevisionKind kind)
    {
        await using Fixture f = await Fixture.CreateAsync(compareKind: kind);
        GitRangeDiffComparison range = f.Dialog.ComparisonGroups.Single(group => group.Kind == GitRevisionDiffGroupKind.RangeDiff).RangeDiff!;
        string? merge = f.Dialog.MergeBaseRevision;
        await f.Git.GitAsync("checkout", "-b", "new-head", f.Base);
        await File.WriteAllTextAsync(f.Git.PathInRepository("shared.txt"), "value C\n"); await f.Git.GitAsync("add", "."); await f.Git.GitAsync("commit", "-m", "Later HEAD");
        await f.Dialog.InitializeAsync(); await Idle(f.Dialog);
        GitRangeDiffComparison after = f.Dialog.ComparisonGroups.Single(group => group.Kind == GitRevisionDiffGroupKind.RangeDiff).RangeDiff!;
        Assert.That(after.First.TipRevision, Is.EqualTo(range.First.TipRevision)); Assert.That(after.Second.TipRevision, Is.EqualTo(range.Second.TipRevision)); Assert.That(f.Dialog.MergeBaseRevision, Is.EqualTo(merge));
        await Select(f, GitRevisionDiffGroupKind.BaseToSecond, "shared.txt"); Assert.That(f.Dialog.SelectedComparison!.Compare.Kind, Is.EqualTo(kind));
        Field<TabControl>(f.Dialog, "ComparisonTabs").SelectedIndex = 2; await Idle(f.Dialog);
        Assert.That(Field<FileHistoryContentView>(f.Dialog, "ComparisonCompareView").TextView.Text, Is.EqualTo("value C\n"));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task SettingsShowAllUsesTheSharedPreferenceAndKeepsItsInputOrigin(bool editorOrigin)
    {
        await using Fixture f = await Fixture.CreateAsync(); new RevisionDiffPreferencesStore().Save(new(true, true));
        TextBox? editor = editorOrigin ? await TypeFilter(f, "shared") : null;
        if (editor is null) { await Select(f, GitRevisionDiffGroupKind.Comparison, "shared.txt"); }
        else
        {
            await Until(() => f.View.FilteredEntries.Count == 3);
            Press(f.Dialog, PhysicalKey.Home); Press(f.Dialog, PhysicalKey.ArrowRight); Press(f.Dialog, PhysicalKey.ArrowRight);
            Press(f.Dialog, PhysicalKey.ArrowRight, RawInputModifiers.Shift); Press(f.Dialog, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
        }
        int caret = editor?.CaretIndex ?? 0, selectionStart = editor?.SelectionStart ?? 0, selectionEnd = editor?.SelectionEnd ?? 0;
        MenuFlyout menu = await OpenSettings(f); MenuItem option = menu.Items.OfType<MenuItem>().Single(item => item.Name == "ComparisonDiffAllParents");
        PushProcessTestDriver.Pointer(option); await Idle(f.Dialog);
        Assert.That(f.Dialog.ComparisonGroups.Count, Is.EqualTo(1)); Assert.That(new RevisionDiffPreferencesStore().Load(), Is.EqualTo(new RevisionDiffPreferences(false, true)));
        if (editor is not null)
        {
            Assert.That(f.Dialog.FocusManager!.GetFocusedElement(), Is.SameAs(editor)); Assert.That(editor.Text, Is.EqualTo("shared"));
            Assert.That(editor.CaretIndex, Is.EqualTo(caret)); Assert.That(editor.SelectionStart, Is.EqualTo(selectionStart)); Assert.That(editor.SelectionEnd, Is.EqualTo(selectionEnd));
            string expected = editor.Text!.Remove(Math.Min(selectionStart, selectionEnd), Math.Abs(selectionEnd - selectionStart)).Insert(Math.Min(selectionStart, selectionEnd), "x");
            f.Dialog.KeyTextInput("x"); Pump(f.Dialog);
            Assert.That(editor.Text, Is.EqualTo(expected), "The first real character after Settings must reach the preserved editor without a repair click.");
            Assert.That(f.View.FilterText, Is.EqualTo(expected)); Assert.That(f.Dialog.FocusManager.GetFocusedElement(), Is.SameAs(editor));
        }
        else
        {
            Assert.That(f.Tree.IsKeyboardFocusWithin, Is.True);
            object? previous = f.Tree.SelectedItem; Press(f.Dialog, PhysicalKey.ArrowUp); await Idle(f.Dialog);
            Assert.That(f.Tree.SelectedItem, Is.Not.SameAs(previous), "Up from the last leaf must navigate on its first press.");
            Press(f.Dialog, PhysicalKey.ArrowDown); await Idle(f.Dialog); Assert.That(f.Tree.SelectedItem, Is.SameAs(previous));
        }
        // Browse's next real refresh must observe the same setting and preserve Omit.
        MainWindow browse = new(); browse.Show();
        try
        {
            await Until(() => !browse.IsBusy); await browse.OpenRepositoryAsync(f.Git.Repository.Path); await Until(() => !browse.IsBusy);
            ListBox history = Field<ListBox>(browse, "RevisionHistory"); history.SelectedItems!.Clear();
            history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.B); await Until(() => !browse.IsBusy);
            history.SelectedItems!.Add(history.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.A)); await Until(() => !browse.IsBusy);
            Field<TabControl>(browse, "DetailsTabs").SelectedItem = Field<TabControl>(browse, "DetailsTabs").Items.OfType<TabItem>().Single(tab => tab.Name == "DiffTab"); await Until(() => !browse.IsBusy);
            Assert.That(Field<TreeView>(browse, "RevisionFiles").Items.OfType<TreeViewItem>().Select(item => item.Tag).OfType<GitRevisionDiffGroup>().Select(group => group.Kind), Is.EqualTo(new[] { GitRevisionDiffGroupKind.Comparison }));
        }
        finally { browse.Close(); }
    }

    private static async Task<MenuFlyout> OpenSettings(Fixture f)
    {
        PushProcessTestDriver.Pointer(Field<Button>(f.Dialog, "ComparisonFilesSettings"));
        await Until(() => f.View.SettingsMenu.IsOpen); return f.View.SettingsMenu;
    }
    private static async Task<TextBox> TypeFilter(Fixture f, string text)
    {
        TextBox editor = Field<ComboBox>(f.Dialog, "ComparisonFilesFilter").GetVisualDescendants().OfType<TextBox>().Single();
        PushProcessTestDriver.Pointer(editor); Press(f.Dialog, PhysicalKey.A, RawInputModifiers.Control); Press(f.Dialog, PhysicalKey.Backspace);
        foreach (char c in text) { f.Dialog.KeyTextInput(c.ToString()); } Pump(f.Dialog); Assert.That(editor.Text, Is.EqualTo(text)); return await Task.FromResult(editor);
    }
    private static async Task Select(Fixture f, GitRevisionDiffGroupKind kind, string? path = null, bool wait = true)
    { TreeViewItem item = path is null ? f.Root(kind) : f.Leaf(kind, path); Expand(f.Tree, item); Pump(f.Dialog); PushProcessTestDriver.Pointer(RowTarget(item)); if (wait) { await Idle(f.Dialog); } }
    private static void Expand(TreeView tree, TreeViewItem wanted)
    {
        bool Visit(TreeViewItem item) { bool contains = ReferenceEquals(item, wanted); foreach (TreeViewItem child in item.Items.OfType<TreeViewItem>()) { contains |= Visit(child); } if (contains && !ReferenceEquals(item, wanted)) { item.IsExpanded = true; } return contains; }
        foreach (TreeViewItem root in tree.Items.OfType<TreeViewItem>()) { Visit(root); } wanted.BringIntoView();
    }
    private static Control RowTarget(TreeViewItem item) => item.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header" && ReferenceEquals(control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), item));
    private static DiffView Diff(Fixture f) => Field<FileHistoryContentView>(f.Dialog, "ComparisonDiff").TextView;
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Pump(TopLevel root) { Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) { root.KeyPressQwerty(key, modifiers); if (root.IsVisible) { root.KeyReleaseQwerty(key, modifiers); } Pump(root); }
    private static async Task Until(Func<bool> ready) { Stopwatch timer = Stopwatch.StartNew(); while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(25)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(ready(), Is.True); }
    private static async Task Idle(CompareRevisionsDialog dialog) { await Until(() => !dialog.IsBusy && dialog.SelectionCompletion.IsCompleted); Pump(dialog); Assert.That(dialog.LastError, Is.Null, dialog.LastError?.ToString()); }
    private static bool Alive(int pid) { try { string s = File.ReadAllText($"/proc/{pid}/stat"); return s[(s.LastIndexOf(')') + 2)..][0] is not ('Z' or 'X'); } catch (IOException) { return false; } }
    private static void Capture(TopLevel root, string name) { Pump(root); using var image = root.CaptureRenderedFrame(); Assert.That(image, Is.Not.Null); string dir = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(dir); string path = Path.Combine(dir, name); image!.Save(path); TestContext.AddTestAttachment(path); }

    private sealed class Fixture : IAsyncDisposable
    {
        internal ReferenceFixture Git { get; }
        internal Gate Gate { get; }
        internal CompareRevisionsDialog Dialog { get; }
        internal TreeView Tree => Field<TreeView>(Dialog, "ComparisonFiles");
        internal FileStatusListView View => Field<FileStatusListView>(Dialog, "ComparisonFileStatusList");
        internal string Base { get; }
        internal string A { get; }
        internal string B { get; }
        internal TreeViewItem Root(GitRevisionDiffGroupKind kind) => Tree.Items.OfType<TreeViewItem>().Single(item => item.Tag is GitComparisonGroup group && group.Kind == kind);
        internal TreeViewItem Leaf(GitRevisionDiffGroupKind kind, string path) => View.FindItemByTag(View.Entries.Select(entry => entry.Tag).OfType<ComparisonFileSelection>().Single(selection => selection.Group.Kind == kind && selection.File.Path == path))!;
        private Fixture(ReferenceFixture git, string common, string a, string b, bool dark, GitCompareRevisionKind compareKind)
        {
            Git = git; Base = common; A = a; B = b; Gate = new(git);
            Dialog = new(new GitRepository(git.Repository.Path, new GitRunner(Gate.Executable)), new GitCompareRevision(a), new GitCompareRevision(b, compareKind), new FileStatusListPreferencesStore(git.Output("file-list-preferences"))) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            Dialog.Show();
        }
        internal static async Task<Fixture> CreateAsync(bool dark = false, GitCompareRevisionKind compareKind = GitCompareRevisionKind.Commit)
        {
            ReferenceFixture git = await ReferenceFixture.CreateAsync(); Fixture? f = null;
            try
            {
                Directory.CreateDirectory(git.PathInRepository("nested"));
                await File.WriteAllTextAsync(git.PathInRepository("shared.txt"), "base shared\n"); await File.WriteAllTextAsync(git.PathInRepository("nested/one.txt"), "base nested\n");
                await git.GitAsync("add", "."); await git.GitAsync("commit", "-m", "Common base"); string common = await git.Repository.ResolveRevisionAsync("HEAD");
                await git.GitAsync("checkout", "-b", "group-a");
                await File.WriteAllTextAsync(git.PathInRepository("shared.txt"), "value A\n"); await File.WriteAllTextAsync(git.PathInRepository("nested/one.txt"), "nested A\n");
                await File.WriteAllTextAsync(git.PathInRepository("only-a.txt"), "only A\n");
                await git.GitAsync("add", "."); await git.GitAsync("commit", "-m", "Series A"); string a = await git.Repository.ResolveRevisionAsync("HEAD");
                await git.GitAsync("checkout", "-b", "group-b", common);
                await File.WriteAllTextAsync(git.PathInRepository("shared.txt"), "value B\n"); await File.WriteAllTextAsync(git.PathInRepository("nested/one.txt"), "nested B\n");
                await File.WriteAllTextAsync(git.PathInRepository("only-b.txt"), "only B\n");
                await git.GitAsync("add", "."); await git.GitAsync("commit", "-m", "Series B"); string b = await git.Repository.ResolveRevisionAsync("HEAD");
                f = new(git, common, a, b, dark, compareKind); await f.Dialog.InitializeAsync(); await Idle(f.Dialog); return f;
            }
            catch { if (f is null) { git.Dispose(); } else { await f.DisposeAsync(); } throw; }
        }
        public async ValueTask DisposeAsync() { Gate.Release(); Dialog.Close(); await Dialog.SelectionCompletion; Git.Dispose(); }
    }
    private sealed class Gate
    {
        private readonly string _control, _release, _log;
        internal string Executable { get; }
        internal IReadOnlyList<JsonElement> Commands
        {
            get { if (!File.Exists(_log)) { return []; } List<JsonElement> result = []; foreach (string line in File.ReadAllLines(_log)) { try { using JsonDocument json = JsonDocument.Parse(line); result.Add(json.RootElement.Clone()); } catch (JsonException) { } } return result; }
        }
        internal bool Started => Commands.Any(row => row.GetProperty("blocked").GetBoolean());
        internal int BlockedPid => Commands.Last(row => row.GetProperty("blocked").GetBoolean()).GetProperty("pid").GetInt32();
        internal Gate(ReferenceFixture fixture)
        {
            string dir = fixture.Output("comparison-group-gate"); Directory.CreateDirectory(dir); _control = Path.Combine(dir, "match.json"); _release = Path.Combine(dir, "release"); _log = Path.Combine(dir, "commands.jsonl"); Executable = Path.Combine(dir, "git");
            File.WriteAllText(Executable, $$"""
                #!/usr/bin/python3
                import os,sys,json,time
                from pathlib import Path
                args=sys.argv[1:]
                match=json.loads(Path({{JsonSerializer.Serialize(_control)}}).read_text()) if Path({{JsonSerializer.Serialize(_control)}}).exists() else []
                blocked=bool(match) and all(p in args for p in match)
                with Path({{JsonSerializer.Serialize(_log)}}).open('a') as f:f.write(json.dumps(dict(pid=os.getpid(),args=args,blocked=blocked))+'\n')
                if blocked:
                    for _ in range(2500):
                        if Path({{JsonSerializer.Serialize(_release)}}).exists():break
                        time.sleep(.01)
                    else:sys.exit(78)
                os.execv('/usr/bin/git',['/usr/bin/git',*args])
                """);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }
        internal void Arm(string[] match) { File.Delete(_release); File.WriteAllText(_control, JsonSerializer.Serialize(match)); }
        internal void Disarm() => File.Delete(_control);
        internal void Release() { Disarm(); File.WriteAllText(_release, "release"); }
    }
}

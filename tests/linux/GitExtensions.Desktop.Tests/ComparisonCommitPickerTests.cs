using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
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
using RootFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ComparisonCommitPickerTests
{
    [AvaloniaTest]
    [TestCase(true, GitCompareRevisionKind.Index)] [TestCase(true, GitCompareRevisionKind.WorkingTree)]
    [TestCase(false, GitCompareRevisionKind.Index)] [TestCase(false, GitCompareRevisionKind.WorkingTree)]
    public async Task BothComparisonSelectorsReturnTypedEndpointsAndReadTheActualIndexOrWorktree(bool baseSide, GitCompareRevisionKind kind)
    {
        using Fixture f = await Fixture.CreateAsync(dirty: true); string before = await Materials(f.Root);
        await using ComparisonLease s = await ComparisonLease.OpenAsync(f.Repository, new(f.First), new(f.Head));
        GitComparison original = s.Dialog.Comparison!;
        WindowClosingBehavior previousClosingBehavior = s.Dialog.ClosingBehavior;
        Assert.That(previousClosingBehavior, Is.EqualTo(WindowClosingBehavior.OwnerAndChildWindows));
        Pointer(Field<Button>(s.Dialog, baseSide ? "ComparisonBaseCommit" : "ComparisonCompareCommit"));
        ExportRevisionPicker picker = await Picker(s.Dialog); ChooseCommitSelectionPresenter presenter = Presenter(picker);
        Assert.That(s.Dialog.ClosingBehavior, Is.EqualTo(WindowClosingBehavior.OwnerWindowOnly));
        Assert.That(presenter.Selection!.Revision, Is.EqualTo(new GitCompareRevision(baseSide ? f.First : f.Head)));
        Assert.That(presenter.Selection.Commit!.Hash, Is.EqualTo(baseSide ? f.First : f.Head));
        Pointer(Field<Button>(picker, kind == GitCompareRevisionKind.Index ? "ChooseIndexRevision" : "ChooseWorkingDirectoryRevision").FindAncestorOfType<ListBoxItem>()!);
        AssertArtificial(picker, kind); Assert.That(s.Dialog.Comparison, Is.SameAs(original), "Selecting inside a child cannot publish before OK.");
        Pointer(Field<Button>(picker, "Choose"));
        await Until(s.Owner, () => !picker.IsVisible && !s.Dialog.IsBusy && !s.Dialog.IsSelectionLoading
            && (baseSide ? s.Dialog.Comparison?.Base.Kind : s.Dialog.Comparison?.Compare.Kind) == kind);
        await s.Dialog.SelectionCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(s.Dialog.ClosingBehavior, Is.EqualTo(previousClosingBehavior));
        GitCompareRevision selected = baseSide ? s.Dialog.Comparison!.Base : s.Dialog.Comparison!.Compare;
        Assert.That(selected.Kind, Is.EqualTo(kind)); Assert.That(selected.Revision, Is.Empty);
        string content = kind == GitCompareRevisionKind.Index ? "INDEX CONTENT" : "WORKTREE CONTENT";
        string diff = Field<FileHistoryContentView>(s.Dialog, "ComparisonDiff").TextView.Text;
        Assert.That(diff, Does.Contain(baseSide ? "-" + content : "+" + content));
        Assert.That(diff, Does.Contain(baseSide ? "+HEAD CONTENT" : "-base"));
        Assert.That(s.Dialog.LastError, Is.Null); Assert.That(await Materials(f.Root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task ArtificialPreselectionAndFirstArrowsUseOneSelectionAndFitTheSharedMinimum(string language, bool dark)
    {
        using Fixture f = await Fixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        string before = await Materials(f.Root);
        await using PickerLease s = await PickerLease.OpenAsync(f.Repository, new("", GitCompareRevisionKind.WorkingTree), dark: dark);
        ExportRevisionPicker picker = s.Dialog; ListBox rows = Field<ListBox>(picker, "ChooseRevision");
        picker.Width = picker.MinWidth; picker.Height = picker.MinHeight; PumpTree(s.Owner);
        foreach (string name in new[] { "ChooseWorkingDirectoryRevision", "ChooseIndexRevision", "Choose", "CancelChoose", "buttonGotoCommit", "linkLabelParent" })
        {
            Control control = Field<Control>(picker, name); Rect bounds = new(control.TranslatePoint(default, picker)!.Value, control.Bounds.Size);
            Assert.That(control.IsEffectivelyVisible && new Rect(default, picker.ClientSize).Contains(bounds), Is.True, name + ":" + bounds);
        }
        AssertArtificial(picker, GitCompareRevisionKind.WorkingTree); Capture(picker, "comparison-picker-" + language + (dark ? "-dark.png" : "-light.png"));
        Press(picker, PhysicalKey.ArrowDown); AssertArtificial(picker, GitCompareRevisionKind.Index);
        Press(picker, PhysicalKey.ArrowDown); Assert.That(rows.SelectedIndex, Is.EqualTo(2)); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
        Assert.That(Presenter(picker).Selection!.Commit, Is.SameAs(rows.SelectedItem));
        Press(picker, PhysicalKey.ArrowUp); AssertArtificial(picker, GitCompareRevisionKind.Index);
        Press(picker, PhysicalKey.ArrowUp); AssertArtificial(picker, GitCompareRevisionKind.WorkingTree);
        Assert.That(rows.Items.OfType<ArtificialHistoryRevision>().Select(item => item.Kind),
            Is.EqualTo(new[] { GitCompareRevisionKind.WorkingTree, GitCompareRevisionKind.Index }));
        Assert.That(rows.Items.Cast<object>().All(item => item is GitCommit or ArtificialHistoryRevision), Is.True);
        string[] actualIds = (await f.Root.GitAsync("rev-list", "--all")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(picker.GetVisualDescendants().OfType<CommitGraph>().Single().Commits.Select(commit => commit.Hash), Is.EquivalentTo(actualIds));
        object selected = rows.SelectedItem!;
        Assert.That(Field<Button>(picker, "ChooseWorkingDirectoryRevision").Focusable, Is.False);
        Assert.That(Field<Button>(picker, "ChooseIndexRevision").Focusable, Is.False);
        Press(picker, PhysicalKey.Tab);
        Assert.That(rows.IsKeyboardFocusWithin, Is.False, "The mixed grid is one Tab stop, like the Windows revision grid.");
        Assert.That(rows.SelectedItem, Is.SameAs(selected));
        Assert.That(Presenter(picker).Selection!.Revision.Kind, Is.EqualTo(GitCompareRevisionKind.WorkingTree));
        Press(picker, PhysicalKey.Tab, RawInputModifiers.Shift);
        AssertArtificial(picker, GitCompareRevisionKind.WorkingTree); Assert.That(rows.SelectedItem, Is.SameAs(selected));
        Press(picker, PhysicalKey.Enter); Assert.That(await s.Selection, Is.EqualTo(new GitCompareRevision("", GitCompareRevisionKind.WorkingTree)));
        Assert.That(await Materials(f.Root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ArtificialParentsGoToAndFilteredHeadKeepTheTypedSelectionAndNeverExpandTheFilter()
    {
        using Fixture f = await Fixture.CreateAsync(); string before = await Materials(f.Root);
        await using PickerLease s = await PickerLease.OpenAsync(f.Repository, new("", GitCompareRevisionKind.WorkingTree));
        Pointer(Field<Button>(s.Dialog, "linkLabelParent")); await s.Dialog.Completion; AssertArtificial(s.Dialog, GitCompareRevisionKind.Index);
        Assert.That(Field<Button>(s.Dialog, "linkLabelParent").Tag, Is.EqualTo(f.Head));
        Edit(s.Dialog, Field<TextBox>(s.Dialog, "RevisionSearch"), "Initial commit"); Pointer(Field<Button>(s.Dialog, "SearchRevision")); await s.Dialog.Completion;
        AssertArtificial(s.Dialog, GitCompareRevisionKind.Index); ListBox rows = Field<ListBox>(s.Dialog, "ChooseRevision"); object source = rows.ItemsSource!;
        Assert.That(rows.Items.OfType<GitCommit>().Single().Hash, Is.EqualTo(f.First));
        Pointer(Field<Button>(s.Dialog, "linkLabelParent")); Window error = await Message(s.Dialog);
        Assert.That(Field<TextBlock>(error, "MessageDialogText").Text, Does.Contain(f.Head[..8]).And.Contain("not visible"));
        Pointer(Field<Button>(error, "MessageDialogAccept")); await s.Dialog.Completion;
        AssertArtificial(s.Dialog, GitCompareRevisionKind.Index); Assert.That(rows.ItemsSource, Is.SameAs(source));
        Assert.That(Field<TextBox>(s.Dialog, "RevisionSearch").Text, Is.EqualTo("Initial commit"));
        Pointer(Field<Button>(s.Dialog, "buttonGotoCommit")); GoToCommitDialog go = await Child<GoToCommitDialog>(s.Dialog); await go.InitializeAsync();
        Edit(go, Field<TextBox>(go, "textboxCommitExpression"), "HEAD^"); Press(go, PhysicalKey.Enter); await s.Dialog.Completion;
        Assert.That(Presenter(s.Dialog).Selection!.Revision, Is.EqualTo(new GitCompareRevision(f.First)));
        Assert.That(rows.SelectedItem, Is.SameAs(Presenter(s.Dialog).Selection!.Commit)); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
        Assert.That(rows.ItemsSource, Is.SameAs(source)); Assert.That(Field<TextBlock>(s.Dialog, "OperationStatus").IsEffectivelyVisible, Is.False);
        Pointer(Field<Button>(s.Dialog, "ChooseWorkingDirectoryRevision").FindAncestorOfType<ListBoxItem>()!);
        Pointer(Field<Button>(s.Dialog, "buttonGotoCommit")); go = await Child<GoToCommitDialog>(s.Dialog); await go.InitializeAsync();
        Press(go, PhysicalKey.Escape); await s.Dialog.Completion; AssertArtificial(s.Dialog, GitCompareRevisionKind.WorkingTree);
        Assert.That(await Materials(f.Root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task TheUnbornIndexHasNoParentAndTheWorkingTreeParentIsStillTheIndex()
    {
        using RootFixture root = await RootFixture.CreateAsync(unborn: true);
        await File.WriteAllTextAsync(root.File, "INDEX CONTENT\n"); await root.Repository.StageAllAsync(); await File.WriteAllTextAsync(root.File, "WORKTREE CONTENT\n");
        string before = await Materials(root);
        await using PickerLease s = await PickerLease.OpenAsync(root.Repository, new("", GitCompareRevisionKind.Index));
        AssertArtificial(s.Dialog, GitCompareRevisionKind.Index); Assert.That(Presenter(s.Dialog).Parents, Is.Empty);
        Assert.That(Field<StackPanel>(s.Dialog, "flowLayoutPanelParents").IsVisible, Is.False);
        Assert.That(Field<ListBox>(s.Dialog, "ChooseRevision").Items.OfType<GitCommit>(), Is.Empty);
        Assert.That(Field<ListBox>(s.Dialog, "ChooseRevision").Items.OfType<ArtificialHistoryRevision>().Select(item => item.Kind),
            Is.EqualTo(new[] { GitCompareRevisionKind.WorkingTree, GitCompareRevisionKind.Index }));
        Pointer(Field<Button>(s.Dialog, "ChooseWorkingDirectoryRevision").FindAncestorOfType<ListBoxItem>()!); Assert.That(Presenter(s.Dialog).Parents.Single(), Is.EqualTo(new GitCompareRevision("", GitCompareRevisionKind.Index)));
        Pointer(Field<Button>(s.Dialog, "linkLabelParent")); await s.Dialog.Completion; AssertArtificial(s.Dialog, GitCompareRevisionKind.Index);
        Press(s.Dialog, PhysicalKey.Enter); Assert.That(await s.Selection, Is.EqualTo(new GitCompareRevision("", GitCompareRevisionKind.Index)));
        Assert.That(await Materials(root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase("clean")] [TestCase("hidden")] [TestCase("bare")]
    public async Task CleanPreferencesAndUncachedBareDiscoveryDetermineAvailabilityWithoutWriting(string mode)
    {
        using Fixture f = await Fixture.CreateAsync(); GitRepository repository = f.Repository;
        HistoryViewPreferencesStore store = new();
        if (mode == "hidden") { store.Save(store.Load() with { ShowArtificialCommits = false }); }
        if (mode == "bare")
        {
            string path = f.Root.Output("bare.git"); await f.Root.GitAsync("clone", "--bare", repository.Path, path);
            repository = new GitRepository(path); Assert.That(repository.Info, Is.Null, "Exercise discovery, not a pre-populated OpenAsync cache.");
        }
        string before = await Materials(f.Root);
        await using PickerLease s = await PickerLease.OpenAsync(repository, new("", GitCompareRevisionKind.Index));
        Assert.That(repository.IsBare, Is.EqualTo(mode == "bare")); Assert.That(s.Dialog.LastError, Is.Null);
        if (mode == "clean")
        {
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty); AssertArtificial(s.Dialog, GitCompareRevisionKind.Index);
            Assert.That(Field<Button>(s.Dialog, "ChooseWorkingDirectoryRevision").IsEffectivelyVisible, Is.True);
        }
        else
        {
            Assert.That(s.Dialog.GetVisualDescendants().OfType<Button>().Any(button => (button.Name is "ChooseIndexRevision" or "ChooseWorkingDirectoryRevision") && button.IsEffectivelyVisible), Is.False);
            Assert.That(Presenter(s.Dialog).Selection!.Commit, Is.Not.Null); Assert.That(Presenter(s.Dialog).Selection!.Revision.Kind, Is.EqualTo(GitCompareRevisionKind.Commit));
        }
        Pointer(Field<Button>(s.Dialog, "CancelChoose")); Assert.That(await s.Selection, Is.Null);
        Assert.That(await Materials(f.Root), Is.EqualTo(before)); Assert.That(store.Load().ShowArtificialCommits, Is.EqualTo(mode != "hidden"));
    }

    [AvaloniaTest]
    public async Task TheArtificialPreferenceIsCapturedOnceAndAnExternalChangeIsNeverOverwritten()
    {
        using Fixture f = await Fixture.CreateAsync(); HistoryViewPreferencesStore store = new();
        await using PickerLease s = await PickerLease.OpenAsync(f.Repository, new("", GitCompareRevisionKind.Index));
        store.Save(store.Load() with { ShowArtificialCommits = false }); byte[] changed = File.ReadAllBytes(store.FilePath);
        Pointer(Field<Button>(s.Dialog, "SearchRevision")); await s.Dialog.Completion; AssertArtificial(s.Dialog, GitCompareRevisionKind.Index);
        Pointer(Field<Button>(s.Dialog, "CancelChoose")); await s.Selection;
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(changed));
        await using PickerLease next = await PickerLease.OpenAsync(f.Repository, new("", GitCompareRevisionKind.Index));
        Assert.That(next.Dialog.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "ChooseIndexRevision" && button.IsEffectivelyVisible), Is.False);
        Assert.That(Presenter(next.Dialog).Selection!.Revision.Kind, Is.EqualTo(GitCompareRevisionKind.Commit));
        Pointer(Field<Button>(next.Dialog, "CancelChoose")); await next.Selection; Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(changed));
    }

    [AvaloniaTest]
    public async Task CancelingAnArtificialDraftPreservesTheHostEndpointsAndTheExistingComparisonObject()
    {
        using Fixture f = await Fixture.CreateAsync(dirty: true); string before = await Materials(f.Root);
        await using ComparisonLease s = await ComparisonLease.OpenAsync(f.Repository, new(f.First), new(f.Head));
        GitComparison original = s.Dialog.Comparison!; string initialText = Field<TextBox>(s.Dialog, "ComparisonCompareRevision").Text!;
        WindowClosingBehavior previousClosingBehavior = s.Dialog.ClosingBehavior;
        Assert.That(previousClosingBehavior, Is.EqualTo(WindowClosingBehavior.OwnerAndChildWindows));
        Pointer(Field<Button>(s.Dialog, "ComparisonCompareCommit")); ExportRevisionPicker picker = await Picker(s.Dialog);
        Assert.That(s.Dialog.ClosingBehavior, Is.EqualTo(WindowClosingBehavior.OwnerWindowOnly));
        Pointer(Field<Button>(picker, "ChooseWorkingDirectoryRevision").FindAncestorOfType<ListBoxItem>()!); AssertArtificial(picker, GitCompareRevisionKind.WorkingTree);
        Pointer(Field<Button>(picker, "CancelChoose")); await Until(s.Owner, () => !picker.IsVisible && s.Dialog.SelectionCompletion.IsCompleted);
        Assert.That(s.Dialog.ClosingBehavior, Is.EqualTo(previousClosingBehavior));
        Assert.That(s.Dialog.Comparison, Is.SameAs(original)); Assert.That(Field<TextBox>(s.Dialog, "ComparisonCompareRevision").Text, Is.EqualTo(initialText));
        Assert.That(Field<ComboBox>(s.Dialog, "ComparisonCompareKind").SelectedIndex, Is.EqualTo((int)GitCompareRevisionKind.Commit));
        Assert.That(await Materials(f.Root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ClosingTheComparisonOrItsOwnerDrainsTheActualPickerReadAndSelectionCompletion(bool ownerClose)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private native gates require Linux shell/procfs."); return; }
        using Fixture f = await Fixture.CreateAsync(dirty: true); Gate gate = await Gate.CreateAsync(f.Root, armed: false); string before = await Materials(f.Root);
        await using ComparisonLease s = await ComparisonLease.OpenAsync(gate.Repository, new(f.First), new(f.Head));
        GitComparison original = s.Dialog.Comparison!;
        WindowClosingBehavior previousClosingBehavior = s.Dialog.ClosingBehavior;
        Assert.That(previousClosingBehavior, Is.EqualTo(WindowClosingBehavior.OwnerAndChildWindows));
        await gate.ArmAsync();
        try
        {
            Pointer(Field<Button>(s.Dialog, "ComparisonCompareCommit")); ExportRevisionPicker picker = await Child<ExportRevisionPicker>(s.Dialog);
            await Until(s.Owner, () => gate.Identity is not null); var identity = gate.Identity!.Value;
            Assert.That(picker.IsBusy && s.Dialog.IsSelectionLoading, Is.True); Assert.That(s.Dialog.SelectionCompletion.IsCompleted, Is.False);
            Assert.That(s.Dialog.ClosingBehavior, Is.EqualTo(WindowClosingBehavior.OwnerWindowOnly));
            if (ownerClose) { s.Owner.Close(); } else { s.Dialog.Close(); }
            await Until(s.Owner, () => !s.Dialog.IsVisible && !picker.IsVisible && s.Dialog.SelectionCompletion.IsCompleted
                && (!ownerClose || !s.Owner.IsVisible));
            await picker.Completion.WaitAsync(TimeSpan.FromSeconds(10)); await s.Dialog.SelectionCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(s.Dialog.ClosingBehavior, Is.EqualTo(previousClosingBehavior));
            Assert.That(Alive(identity), Is.False); Assert.That(s.Dialog.Comparison, Is.SameAs(original)); Assert.That(s.Owner.IsVisible, Is.EqualTo(!ownerClose));
            Assert.That(await Materials(f.Root), Is.EqualTo(before));
        }
        finally { await gate.ReleaseAsync(); }
    }

    [AvaloniaTest]
    public async Task AnExternalPickerTokenDrainsItsActualPidWithoutReturningAnArtificialOrRealEndpoint()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private native gates require Linux shell/procfs."); return; }
        using Fixture f = await Fixture.CreateAsync(); Gate gate = await Gate.CreateAsync(f.Root); using CancellationTokenSource token = new();
        await using PickerLease s = PickerLease.Begin(gate.Repository, new("", GitCompareRevisionKind.WorkingTree), token.Token);
        try
        {
            await Until(s.Owner, () => gate.Identity is not null); var identity = gate.Identity!.Value;
            token.Cancel(); await Until(s.Owner, () => s.Selection.IsCompleted && !s.Dialog.IsVisible);
            Assert.CatchAsync<OperationCanceledException>(async () => await s.Selection); await s.Dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(Alive(identity), Is.False); Assert.That(s.Owner.IsVisible, Is.True);
        }
        finally { await gate.ReleaseAsync(); }
    }

    private static void AssertArtificial(ExportRevisionPicker picker, GitCompareRevisionKind kind)
    {
        ChooseCommitSelectionPresenter presenter = Presenter(picker);
        Assert.That(presenter.Selection!.Revision, Is.EqualTo(new GitCompareRevision("", kind))); Assert.That(presenter.Selection.Commit, Is.Null);
        ListBox rows = Field<ListBox>(picker, "ChooseRevision");
        Assert.That(rows.SelectedItems, Has.Count.EqualTo(1), "Real commits and artificial endpoints share one native selection.");
        Assert.That(rows.SelectedItem, Is.TypeOf<ArtificialHistoryRevision>());
        Assert.That(((ArtificialHistoryRevision)rows.SelectedItem!).Revision, Is.EqualTo(presenter.Selection.Revision));
        Assert.That(rows.IsKeyboardFocusWithin, Is.True);
    }
    private static ChooseCommitSelectionPresenter Presenter(TopLevel root) => root.GetVisualDescendants().OfType<ChooseCommitSelectionPresenter>().Single();
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Pointer(Control control) => PushProcessTestDriver.Pointer(control);
    private static void Edit(Window window, TextBox editor, string text)
    { Pointer(editor); Press(window, PhysicalKey.A, RawInputModifiers.Control); window.KeyTextInput(text); PumpTree(window); Assert.That(editor.Text, Is.EqualTo(text)); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } PumpTree(window); }
    private static void PumpTree(Window root)
    { Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } foreach (Window child in root.OwnedWindows.ToArray()) { PumpTree(child); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Window root, Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { PumpTree(root); await Task.Delay(10); }
        PumpTree(root); if (!condition() && root.IsVisible) { Capture(root, "comparison-picker-timeout-" + Guid.NewGuid().ToString("N") + ".png"); }
        Assert.That(condition(), Is.True, "Actual comparison picker timed out; visible=" + root.IsVisible + " children=" + string.Join(',', root.OwnedWindows.Select(window => window.Name + ":" + window.IsVisible)));
    }
    private static async Task<T> Child<T>(Window owner) where T : Window
    { await Until(owner, () => owner.OwnedWindows.OfType<T>().Any(window => window.IsVisible)); return owner.OwnedWindows.OfType<T>().Single(window => window.IsVisible); }
    private static async Task<ExportRevisionPicker> Picker(Window owner)
    { ExportRevisionPicker picker = await Child<ExportRevisionPicker>(owner); await Until(owner, () => !picker.IsBusy && picker.Completion.IsCompleted); await picker.Completion; Assert.That(picker.LastError, Is.Null); return picker; }
    private static async Task<Window> Message(Window owner)
    { await Until(owner, () => owner.OwnedWindows.Any(window => window.Name == "MessageDialog" && window.IsVisible)); return owner.OwnedWindows.Single(window => window.Name == "MessageDialog" && window.IsVisible); }
    private static void Capture(Window window, string name)
    {
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); using var frame = window.GetLastRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private static async Task<string> Materials(RootFixture root)
    {
        string profile = root.Output("config");
        return (await root.GitAsync("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput + "\n"
            + string.Join('\n', new[] { ".git/HEAD", ".git/index", ".git/config", "file.txt" }.Select(path => path + ":" + (File.Exists(root.PathInRepository(path)) ? Convert.ToHexString(File.ReadAllBytes(root.PathInRepository(path))) : "<absent>")))
            + "\nprofile:" + (Directory.Exists(profile) ? string.Join('\n', Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => path + ":" + Convert.ToHexString(File.ReadAllBytes(path)))) : "");
    }
    private static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string? StartIdentity(int pid)
    { try { string stat = File.ReadAllText("/proc/" + pid + "/stat"); return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19]; } catch (IOException) { return null; } }
    private static bool Alive((int Pid, string Start) identity) => StartIdentity(identity.Pid) == identity.Start;

    private sealed class ComparisonLease : IAsyncDisposable
    {
        internal Window Owner { get; } = new() { Width = 1300, Height = 850 };
        internal CompareRevisionsDialog Dialog { get; }
        private readonly Task _modal;
        private ComparisonLease(GitRepository repository, GitCompareRevision first, GitCompareRevision second)
        { Owner.Show(); Dialog = new(repository, first, second); _modal = Dialog.ShowDialog(Owner); }
        internal static async Task<ComparisonLease> OpenAsync(GitRepository repository, GitCompareRevision first, GitCompareRevision second)
        {
            ComparisonLease s = new(repository, first, second);
            try { await s.Dialog.InitializeAsync(); await Until(s.Owner, () => !s.Dialog.IsBusy && !s.Dialog.IsSelectionLoading); Assert.That(s.Dialog.LastError, Is.Null); return s; }
            catch { await s.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            foreach (Window child in Dialog.OwnedWindows.ToArray()) { child.Close(); } Dialog.Close();
            await Until(Owner, () => !Dialog.IsVisible && !Dialog.IsBusy && Dialog.SelectionCompletion.IsCompleted);
            await Dialog.SelectionCompletion.WaitAsync(TimeSpan.FromSeconds(10)); await _modal.WaitAsync(TimeSpan.FromSeconds(10)); Owner.Close(); PumpTree(Owner);
        }
    }

    private sealed class PickerLease : IAsyncDisposable
    {
        internal Window Owner { get; }
        internal ExportRevisionPicker Dialog => _dialog;
        internal Task<GitCompareRevision?> Selection { get; }
        private readonly ExportRevisionPicker _dialog;
        private readonly CancellationToken _token;
        private PickerLease(GitRepository repository, GitCompareRevision initial, CancellationToken token, bool dark)
        {
            _token = token; Owner = new() { Width = 1000, Height = 700, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; Owner.Show();
            Selection = ExportRevisionPicker.ShowComparisonAsync(Owner, repository, initial, token); _dialog = Owner.OwnedWindows.OfType<ExportRevisionPicker>().Single(); PumpTree(Owner);
        }
        internal static PickerLease Begin(GitRepository repository, GitCompareRevision initial, CancellationToken token = default, bool dark = false) => new(repository, initial, token, dark);
        internal static async Task<PickerLease> OpenAsync(GitRepository repository, GitCompareRevision initial, bool dark = false)
        {
            PickerLease s = new(repository, initial, default, dark);
            try { await Until(s.Owner, () => !s.Dialog.IsBusy && s.Dialog.Completion.IsCompleted); await s.Dialog.Completion; Assert.That(s.Dialog.LastError, Is.Null); PumpTree(s.Owner); return s; }
            catch { await s.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            foreach (Window child in _dialog.OwnedWindows.ToArray()) { child.Close(); } _dialog.Close();
            await Until(Owner, () => !_dialog.IsVisible && !_dialog.IsBusy && Selection.IsCompleted); await _dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            try { await Selection.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            Owner.Close(); PumpTree(Owner);
        }
    }

    private sealed class Gate(GitRepository repository, string directory)
    {
        internal GitRepository Repository { get; } = repository;
        internal (int Pid, string Start)? Identity
        {
            get { string path = Path.Combine(directory, "native.pid"); return File.Exists(path) && int.TryParse(File.ReadAllText(path), out int pid) && StartIdentity(pid) is string start ? (pid, start) : null; }
        }
        internal Task ReleaseAsync() => File.WriteAllTextAsync(Path.Combine(directory, "release"), "release\n");
        internal Task ArmAsync() => File.WriteAllTextAsync(Path.Combine(directory, "armed"), "armed\n");
        internal static async Task<Gate> CreateAsync(RootFixture root, bool armed = true)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Native gates require Linux."); }
            string directory = root.Output("comparison chooser gate " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); string path = Path.Combine(directory, "git");
            await File.WriteAllTextAsync(path, "#!/bin/sh\nblocked=no\nfor arg in \"$@\"; do [ \"$arg\" != log ] || blocked=yes; done\nif [ \"$blocked\" = yes ] && [ -f " + Quote(Path.Combine(directory, "armed")) + " ]; then printf '%s' \"$$\" > "
                + Quote(Path.Combine(directory, "native.pid")) + "; while [ ! -f " + Quote(Path.Combine(directory, "release")) + " ]; do sleep .02; done; fi\nexec /usr/bin/git \"$@\"\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Gate gate = new(new GitRepository(root.Repository.Path, new GitRunner(path)), directory);
            if (armed) { await gate.ArmAsync(); } return gate;
        }
    }

    private sealed class Fixture(RootFixture root, string first, string head) : IDisposable
    {
        internal RootFixture Root { get; } = root;
        internal GitRepository Repository => Root.Repository;
        internal string First { get; } = first;
        internal string Head { get; } = head;
        internal static async Task<Fixture> CreateAsync(bool dirty = false)
        {
            RootFixture root = await RootFixture.CreateAsync();
            try
            {
                string first = await root.Repository.ResolveRevisionAsync("HEAD"); await root.ChangeAsync("HEAD CONTENT\n", "Second commit");
                string head = await root.Repository.ResolveRevisionAsync("HEAD");
                if (dirty) { await File.WriteAllTextAsync(root.File, "INDEX CONTENT\n"); await root.Repository.StageAllAsync(); await File.WriteAllTextAsync(root.File, "WORKTREE CONTENT\n"); }
                return new(root, first, head);
            }
            catch { root.Dispose(); throw; }
        }
        public void Dispose() => Root.Dispose();
    }
}

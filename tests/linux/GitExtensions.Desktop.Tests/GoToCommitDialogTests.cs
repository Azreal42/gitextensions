using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using RootFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GoToCommitDialogTests
{
    [AvaloniaTest]
    public async Task NativeExpressionsAbbreviationsAnnotatedTagsRemoteRefsAndLiteralBranchNamesResolveWithoutRepositoryMutation()
    {
        using Fixture f = await Fixture.CreateAsync(); string before = await f.Index();
        foreach ((string expression, string expected) in new[]
        {
            ("  HEAD^  ", f.First), (f.First[..12], f.First), ("Tag/Été", f.First), ("refs/remotes/origin/remote", f.First),
            (Fixture.LocalName, f.First), ("HEAD", f.Last)
        })
        {
            GoToCommitDialog dialog = await f.ShowAsync(); Field<TextBox>(dialog, "textboxCommitExpression").Text = expression;
            Press(dialog, PhysicalKey.Enter); await Until(() => !dialog.IsVisible); await dialog.Completion;
            Assert.That(dialog.WasAccepted, Is.True); Assert.That(dialog.ResolutionError, Is.Null); Assert.That(dialog.SelectedCommitHash, Is.EqualTo(expected));
        }
        string blob = (await f.Root.GitAsync("rev-parse", "HEAD:file.txt")).StandardOutput.Trim();
        await f.Root.GitAsync("tag", "-a", "Tag/blob", "-m", "Non-commit annotated tag", blob);
        GoToCommitDialog nonCommit = await f.ShowAsync(); ComboBox tags = Field<ComboBox>(nonCommit, "comboBoxTags");
        Editor(tags).Focus(); tags.Text = "Tag/blob"; Pointer(Field<Button>(nonCommit, "goButton"));
        await Until(() => !nonCommit.IsVisible); await nonCommit.Completion;
        Assert.That(nonCommit.WasAccepted, Is.True); Assert.That(nonCommit.SelectedCommitHash, Is.Null); Assert.That(nonCommit.ResolutionError, Is.TypeOf<GitCommandException>());
        Assert.That(await f.Index(), Is.EqualTo(before)); Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Last));
    }

    [AvaloniaTest]
    public async Task TheLastFocusedSourceWinsAndProgrammaticTextOrListPopulationNeverAccepts()
    {
        using Fixture f = await Fixture.CreateAsync();
        foreach (string field in new[] { "comboBoxTags", "comboBoxBranches" })
        {
            GoToCommitDialog dialog = await f.ShowAsync(); ComboBox combo = Field<ComboBox>(dialog, field);
            Field<TextBox>(dialog, "textboxCommitExpression").Text = "HEAD";
            Editor(combo).Focus(); combo.Text = field == "comboBoxTags" ? "Tag/Été" : Fixture.LocalName;
            Assert.That(dialog.IsVisible && !dialog.WasAccepted, Is.True);
            Field<TextBox>(dialog, "textboxCommitExpression").Text = "HEAD~0";
            Pointer(Field<Button>(dialog, "goButton")); await Until(() => !dialog.IsVisible); await dialog.Completion;
            Assert.That(dialog.SelectedCommitHash, Is.EqualTo(f.First), "Go uses the focused combo's captured reference, not a later expression update or its own button focus.");
        }
    }

    [AvaloniaTest]
    public async Task ValidClipboardPrefillsAndSelectsButInvalidBlobAndUnavailableClipboardStaySilent()
    {
        using Fixture f = await Fixture.CreateAsync(); string blob = (await f.Root.GitAsync("rev-parse", "HEAD:file.txt")).StandardOutput.Trim();
        foreach ((string? value, string expected) in new[] { ("  HEAD^  ", "HEAD^"), ("not-a-revision", ""), (blob, ""), ((string?)null, "") })
        {
            GoToCommitDialog dialog = await f.ShowAsync(clipboard: _ => Task.FromResult(value));
            TextBox expression = Field<TextBox>(dialog, "textboxCommitExpression");
            Assert.That(expression.Text ?? "", Is.EqualTo(expected)); Assert.That(expression.SelectedText, Is.EqualTo(expected));
            Assert.That(expression.IsFocused, Is.True); Assert.That(dialog.ResolutionError, Is.Null); Assert.That(dialog.WasAccepted, Is.False);
            dialog.Close(); await dialog.Completion;
        }
        GoToCommitDialog unavailable = await f.ShowAsync(clipboard: _ => Task.FromException<string?>(new IOException("Private clipboard unavailable")));
        Assert.That(Field<TextBox>(unavailable, "textboxCommitExpression").Text, Is.Null.Or.Empty); unavailable.Close(); await unavailable.Completion;
    }

    [AvaloniaTest]
    public async Task IndependentNativeLoadsAndLateClipboardNeverDisableTheExpressionOverwriteTypingOrStealFocus()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private native process gates use a Linux shell."); return; }
        using Fixture f = await Fixture.CreateAsync(); Gates gates = await f.GatesAsync("tags", "heads"); TaskCompletionSource<string?> clipboard = new();
        GoToCommitDialog dialog = new(gates.Repository, readClipboard: token => clipboard.Task.WaitAsync(token)); dialog.Show(); Task loading = dialog.InitializeAsync();
        try
        {
            await Until(() => gates.HasPid("tags") && gates.HasPid("heads")); TextBox expression = Field<TextBox>(dialog, "textboxCommitExpression");
            Assert.That(expression.IsEnabled && expression.IsFocused && dialog.IsLoadingTags && dialog.IsLoadingBranches, Is.True);
            Edit(dialog, expression, "HEAD^ "); await gates.Release("tags"); await Until(() => !dialog.IsLoadingTags);
            Assert.That(dialog.IsLoadingBranches && expression.IsFocused, Is.True);
            ComboBox tags = Field<ComboBox>(dialog, "comboBoxTags"); Editor(tags).Focus(); tags.Text = "Tag/Été";
            clipboard.SetResult("HEAD"); await gates.Release("heads"); await loading;
            Assert.That(expression.Text, Is.EqualTo("HEAD^ ")); Assert.That(tags.IsKeyboardFocusWithin, Is.True);
            Pointer(Field<Button>(dialog, "goButton")); await Until(() => !dialog.IsVisible); await dialog.Completion;
            Assert.That(dialog.SelectedCommitHash, Is.EqualTo(f.First));
        }
        finally { dialog.Close(); await dialog.Completion; }
    }

    [AvaloniaTest]
    public async Task ClosingCancelsAndDrainsAllThreeBlockedNativeReadsAndNoLateResultReopensTheDialog()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private native process gates use Linux procfs."); return; }
        using Fixture f = await Fixture.CreateAsync(); Gates gates = await f.GatesAsync("tags", "heads", "clipboard");
        GoToCommitDialog dialog = new(gates.Repository, readClipboard: _ => Task.FromResult<string?>("HEAD")); dialog.Show(); Task loading = dialog.InitializeAsync();
        try
        {
            await Until(() => new[] { "tags", "heads", "clipboard" }.All(gates.HasPid)); int[] pids = new[] { "tags", "heads", "clipboard" }.Select(gates.Pid).ToArray();
            Press(dialog, PhysicalKey.Escape); await loading.WaitAsync(TimeSpan.FromSeconds(10)); await dialog.Completion;
            Assert.That(dialog.IsVisible || dialog.WasAccepted, Is.False); Assert.That(pids.Any(Alive), Is.False);
            Assert.That(Field<TextBox>(dialog, "textboxCommitExpression").Text, Is.Null.Or.Empty);
        }
        finally { dialog.Close(); await dialog.Completion; }
    }

    [AvaloniaTest]
    public async Task TheThousandItemLimitDoesNotRestrictExpressionsAndRemoteRefsNeverEnterTheLocalBranchCombo()
    {
        using Fixture f = await Fixture.CreateAsync();
        string commands = string.Concat(Enumerable.Range(0, 1005).Select(i => $"create refs/heads/bulk-{i:0000} {f.Last}\ncreate refs/tags/bulk-{i:0000} {f.Last}\n"));
        GitResult update = await new GitRunner().RunAsync(f.Repository.Path, ["update-ref", "--stdin"], standardInput: commands);
        Assert.That(update.Success, Is.True, update.StandardError); GoToCommitDialog dialog = await f.ShowAsync();
        Assert.That(Field<ComboBox>(dialog, "comboBoxTags").Items.Count, Is.EqualTo(1000)); Assert.That(Field<ComboBox>(dialog, "comboBoxBranches").Items.Count, Is.EqualTo(1000));
        Assert.That(Field<ComboBox>(dialog, "comboBoxBranches").Items.Cast<string>(), Does.Not.Contain("bulk-1004").And.Not.Contain("origin/remote"));
        Field<TextBox>(dialog, "textboxCommitExpression").Text = "refs/heads/bulk-1004"; Press(dialog, PhysicalKey.Enter);
        await Until(() => !dialog.IsVisible); await dialog.Completion; Assert.That(dialog.SelectedCommitHash, Is.EqualTo(f.Last));
    }

    [AvaloniaTest]
    public async Task InvalidAndFilteredCommitsProduceDistinctErrorsWithoutChangingFiltersRowsOrSelection()
    {
        using Fixture f = await Fixture.CreateAsync(); MainWindow window = await f.Browse(); ListBox history = History(window);
        await Invoke(window, "ApplyBranchFilterAsync", new GitLogQuery(Count: 100, Revision: "HEAD", All: false, Message: "Second"), "", false);
        object? source = history.ItemsSource; object query = Read<object>(window, "_historyQuery"); string selected = ((GitCommit)history.SelectedItem!).Hash;
        foreach ((string expression, string expected) in new[] { ("not-a-real-revision", "No revision found."), (f.First, "not visible in the revision grid") })
        {
            Task navigation = Invoke(window, "GoToRevisionAsync"); GoToCommitDialog dialog = await Child<GoToCommitDialog>(window); await dialog.InitializeAsync();
            TextBox input = Field<TextBox>(dialog, "textboxCommitExpression"); input.Focus(); input.Text = expression; Press(dialog, PhysicalKey.Enter);
            Window error = await Error(window); Assert.That(Field<TextBlock>(error, "GoToCommitErrorMessage").Text, Does.Contain(expected));
            Pointer(Field<Button>(error, "CloseGoToCommitError")); await navigation;
            Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(Read<object>(window, "_historyQuery"), Is.SameAs(query));
            Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(selected)); Assert.That(history.IsKeyboardFocusWithin, Is.True);
        }
    }

    [AvaloniaTest]
    public async Task SuccessfulNavigationKeepsTheHistorySourceAndKeyboardFocusAndParticipatesInBackForward()
    {
        using Fixture f = await Fixture.CreateAsync(); MainWindow window = await f.Browse(); ListBox history = History(window); object? source = history.ItemsSource;
        Task navigation = Invoke(window, "GoToRevisionAsync"); GoToCommitDialog dialog = await Child<GoToCommitDialog>(window); await dialog.InitializeAsync();
        Field<TextBox>(dialog, "textboxCommitExpression").Text = "HEAD^"; Press(dialog, PhysicalKey.Enter); await navigation;
        Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(f.First)); Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.IsKeyboardFocusWithin, Is.True);
        await Invoke(window, "NavigateRevisionHistoryAsync", -1); Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(f.Last));
        await Invoke(window, "NavigateRevisionHistoryAsync", 1); Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(f.First));
        Press(window, PhysicalKey.ArrowDown); await Until(() => !Read<bool>(window, "_busy")); Assert.That(history.IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    public Task EscapeCancellationKeepsTheGridSelectionNavigationAndFocusForRealArrowKeysAndKeyboardReopening()
        => AssertMainWindowCancellationAsync(closeWindow: false);

    [AvaloniaTest]
    public Task WindowCloseCancellationKeepsTheGridSelectionNavigationAndFocusForRealArrowKeysAndKeyboardReopening()
        => AssertMainWindowCancellationAsync(closeWindow: true);

    private static async Task AssertMainWindowCancellationAsync(bool closeWindow)
    {
        using Fixture f = await Fixture.CreateAsync(); MainWindow window = await f.Browse(); ListBox history = History(window);
        Pointer((Control)history.ContainerFromIndex(0)!);
        await Until(() => history.IsKeyboardFocusWithin && ((GitCommit)history.SelectedItem!).Hash == f.Last);
        object? source = history.ItemsSource; object query = Read<object>(window, "_historyQuery");
        for (int cycle = 0; cycle < 2; cycle++)
        {
            string[] navigation = Read<List<string>>(window, "_revisionNavigation").ToArray();
            int navigationIndex = Read<int>(window, "_revisionNavigationIndex");
            Press(window, PhysicalKey.G, RawInputModifiers.Control | RawInputModifiers.Shift);
            GoToCommitDialog dialog = await Child<GoToCommitDialog>(window);
            try
            {
                await dialog.InitializeAsync(); Assert.That(Field<TextBox>(dialog, "textboxCommitExpression").IsFocused, Is.True);
                Edit(dialog, Field<TextBox>(dialog, "textboxCommitExpression"), "HEAD^");
                if (closeWindow) { dialog.Close(); }
                else { Press(dialog, PhysicalKey.Escape); }
                await dialog.Completion;
                await Until(() => !IsGoToCommitReadPending(window)); Pump(window);
                Assert.That(dialog.IsVisible || dialog.WasAccepted, Is.False);
                Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(Read<object>(window, "_historyQuery"), Is.SameAs(query));
                Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(f.Last));
                Assert.That(Read<List<string>>(window, "_revisionNavigation"), Is.EqualTo(navigation));
                Assert.That(Read<int>(window, "_revisionNavigationIndex"), Is.EqualTo(navigationIndex));
                Assert.That(history.IsKeyboardFocusWithin, Is.True, "Cancel must return keyboard focus to the grid without another click or Focus().");
                Assert.That(window.LastException, Is.Null);
                Press(window, PhysicalKey.ArrowDown);
                await Until(() => ((GitCommit)history.SelectedItem!).Hash == f.First);
                Assert.That(history.IsKeyboardFocusWithin, Is.True);
                Press(window, PhysicalKey.ArrowUp);
                await Until(() => ((GitCommit)history.SelectedItem!).Hash == f.Last);
                Assert.That(history.IsKeyboardFocusWithin, Is.True);
            }
            finally { dialog.Close(); await dialog.Completion; }
            // The next iteration reopens with Ctrl+Shift+G without any intervening pointer/focus action.
        }
        Assert.That(window.OwnedWindows.Any(child => child.IsVisible), Is.False);
    }

    private static bool IsGoToCommitReadPending(MainWindow window)
    {
        object read = Read<object>(window, "_goToCommitRead");
        return read.GetType().GetField("Current", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(read) is not null;
    }

    [AvaloniaTest]
    public async Task RepositoryReplacementClosesAndCancelsTheOldDialogBeforeItCanApplyAnySelection()
    {
        using Fixture f = await Fixture.CreateAsync(); using RootFixture other = await RootFixture.CreateAsync(); MainWindow window = await f.Browse();
        Gates gates = await f.GatesAsync("tags", "heads"); Write(window, "_repository", gates.Repository);
        Task navigation = Invoke(window, "GoToRevisionAsync"); GoToCommitDialog dialog = await Child<GoToCommitDialog>(window);
        await Until(() => gates.HasPid("tags") && gates.HasPid("heads")); int[] pids = [gates.Pid("tags"), gates.Pid("heads")];
        await window.OpenRepositoryAsync(other.Repository.Path); await navigation.WaitAsync(TimeSpan.FromSeconds(10)); await dialog.Completion;
        Assert.That(dialog.IsVisible || dialog.WasAccepted, Is.False); Assert.That(pids.Any(Alive), Is.False);
        Assert.That(Read<GitRepository>(window, "_repository").Path, Is.EqualTo(other.Repository.Path));
        Assert.That(History(window).Items.OfType<GitCommit>().Single().Hash, Is.EqualTo(await other.Repository.ResolveRevisionAsync("HEAD")));
    }

    [AvaloniaTest]
    public async Task FirstGoFreezesAllSourcesAndRejectsLateClipboardAndFurtherChoiceUntilItsNativeResolutionFinishes()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private native process gates use Linux procfs."); return; }
        using Fixture f = await Fixture.CreateAsync();
        foreach (bool cancel in new[] { false, true })
        {
            Gates gates = await f.GatesAsync("clipboard"); TaskCompletionSource<string?> clipboard = new();
            GoToCommitDialog dialog = new(gates.Repository, readClipboard: token => clipboard.Task.WaitAsync(token));
            dialog.Show(); TextBox expression = Field<TextBox>(dialog, "textboxCommitExpression"); expression.Text = "HEAD";
            Task loading = dialog.InitializeAsync();
            try
            {
                await Until(() => !dialog.IsLoadingTags && !dialog.IsLoadingBranches);
                Press(dialog, PhysicalKey.Enter); await Until(() => gates.HasPid("clipboard")); int pid = gates.Pid("clipboard");
                Assert.That(dialog.IsValidating, Is.True);
                ComboBox tags = Field<ComboBox>(dialog, "comboBoxTags"), branches = Field<ComboBox>(dialog, "comboBoxBranches");
                Assert.That(new Control[] { expression, tags, branches, Field<Button>(dialog, "goButton") }.All(control => !control.IsEnabled), Is.True);
                Point point = tags.TranslatePoint(new Point(tags.Bounds.Width / 2, tags.Bounds.Height / 2), dialog)!.Value;
                dialog.MouseMove(point); dialog.MouseDown(point, MouseButton.Left); dialog.MouseUp(point, MouseButton.Left);
                dialog.KeyTextInput("Tag/Été"); Press(dialog, PhysicalKey.Enter);
                clipboard.SetResult("HEAD^"); await loading;
                Assert.That(expression.Text, Is.EqualTo("HEAD")); Assert.That(tags.IsDropDownOpen, Is.False);
                Assert.That(dialog.IsValidating && !dialog.WasAccepted, Is.True);
                if (cancel) { Press(dialog, PhysicalKey.Escape); }
                else { await gates.Release("clipboard"); }
                await dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(dialog.IsVisible, Is.False); Assert.That(Alive(pid), Is.False);
                Assert.That(dialog.WasAccepted, Is.EqualTo(!cancel));
                Assert.That(dialog.SelectedExpression, Is.EqualTo("HEAD"));
                Assert.That(dialog.SelectedCommitHash, cancel ? Is.Null : Is.EqualTo(f.Last));
            }
            finally { dialog.Close(); await dialog.Completion; }
        }
    }

    [AvaloniaTest]
    public async Task ReopeningTheSamePathCreatesANewContextAndCancelsAndDrainsTheOldDialog()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private native process gates use Linux procfs."); return; }
        using Fixture f = await Fixture.CreateAsync(); MainWindow window = await f.Browse();
        Gates gates = await f.GatesAsync("tags", "heads"); Write(window, "_repository", gates.Repository);
        Task navigation = Invoke(window, "GoToRevisionAsync"); GoToCommitDialog dialog = await Child<GoToCommitDialog>(window);
        try
        {
            await Until(() => gates.HasPid("tags") && gates.HasPid("heads")); int[] pids = [gates.Pid("tags"), gates.Pid("heads")];
            await window.OpenRepositoryAsync(gates.Repository.Path); await navigation.WaitAsync(TimeSpan.FromSeconds(10)); await dialog.Completion;
            Assert.That(dialog.IsVisible || dialog.WasAccepted, Is.False); Assert.That(pids.Any(Alive), Is.False);
            GitRepository reopened = Read<GitRepository>(window, "_repository"); Assert.That(reopened, Is.Not.SameAs(gates.Repository));
            Assert.That(reopened.Path, Is.EqualTo(gates.Repository.Path)); Assert.That(History(window).Items.OfType<GitCommit>().Count(), Is.EqualTo(2));
        }
        finally { dialog.Close(); await dialog.Completion; }
    }

    [AvaloniaTest]
    public async Task RepositoryOperationCancellationAtAcceptanceNeverAppliesTheLateSelectionEvenWhenTheRepositoryInstanceIsUnchanged()
    {
        using Fixture f = await Fixture.CreateAsync(); MainWindow window = await f.Browse(); GitRepository repository = Read<GitRepository>(window, "_repository");
        Task navigation = Invoke(window, "GoToRevisionAsync"); GoToCommitDialog dialog = await Child<GoToCommitDialog>(window); await dialog.InitializeAsync();
        dialog.Closing += (_, _) => typeof(MainWindow).GetMethod("CancelBrowserReads", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Field<TextBox>(dialog, "textboxCommitExpression").Text = "HEAD^"; Press(dialog, PhysicalKey.Enter); await navigation;
        Assert.That(dialog.WasAccepted, Is.True); Assert.That(dialog.SelectedCommitHash, Is.EqualTo(f.First));
        Assert.That(Read<GitRepository>(window, "_repository"), Is.SameAs(repository));
        Assert.That(((GitCommit)History(window).SelectedItem!).Hash, Is.EqualTo(f.Last));
        Assert.That(window.OwnedWindows.Any(child => child.IsVisible), Is.False);
    }

    [AvaloniaTest]
    public async Task BackAndForwardToFilteredCommitsUseTheSameModalAndAdvanceTheNavigationIndexWithoutChangingTheFilter()
    {
        using Fixture f = await Fixture.CreateAsync(); MainWindow window = await f.Browse(); ListBox history = History(window);
        Task navigation = Invoke(window, "GoToRevisionAsync"); GoToCommitDialog dialog = await Child<GoToCommitDialog>(window); await dialog.InitializeAsync();
        Field<TextBox>(dialog, "textboxCommitExpression").Text = "HEAD^"; Press(dialog, PhysicalKey.Enter); await navigation;
        await Invoke(window, "ApplyBranchFilterAsync", new GitLogQuery(Count: 100, Revision: "HEAD", All: false, Message: "Second"), "", false);
        object? source = history.ItemsSource, query = Read<object>(window, "_historyQuery"); int initialIndex = Read<int>(window, "_revisionNavigationIndex");
        Task back = Invoke(window, "NavigateRevisionHistoryAsync", -1); Window error = await Error(window);
        Assert.That(Field<TextBlock>(error, "GoToCommitErrorMessage").Text, Does.Contain(f.First[..7]).And.Contain("not visible in the revision grid"));
        Pointer(Field<Button>(error, "CloseGoToCommitError")); await back;
        Assert.That(Read<int>(window, "_revisionNavigationIndex"), Is.EqualTo(initialIndex - 1));
        Assert.That(((GitCommit)history.SelectedItem!).Hash, Is.EqualTo(f.Last)); Assert.That(history.ItemsSource, Is.SameAs(source));
        await Invoke(window, "NavigateRevisionHistoryAsync", -1);
        Task forward = Invoke(window, "NavigateRevisionHistoryAsync", 1); error = await Error(window); Pointer(Field<Button>(error, "CloseGoToCommitError")); await forward;
        Assert.That(Read<int>(window, "_revisionNavigationIndex"), Is.EqualTo(initialIndex - 1));
        Assert.That(Read<object>(window, "_historyQuery"), Is.SameAs(query)); Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.IsKeyboardFocusWithin, Is.True);
    }

    private static async Task<T> Child<T>(Window owner) where T : Window
    { await Until(() => owner.OwnedWindows.OfType<T>().Any(w => w.IsVisible)); return owner.OwnedWindows.OfType<T>().Single(w => w.IsVisible); }
    private static async Task<Window> Error(Window owner)
    { await Until(() => owner.OwnedWindows.Any(w => w.Name == "GoToCommitError" && w.IsVisible)); return owner.OwnedWindows.Single(w => w.Name == "GoToCommitError" && w.IsVisible); }
    private static ListBox History(MainWindow window) => Field<ListBox>(window, "RevisionHistory");
    private static T Field<T>(TopLevel root, string name) where T : Control => root.GetVisualDescendants().OfType<Control>()
        .Concat(root.GetLogicalDescendants().OfType<Control>()).Distinct().OfType<T>().Single(control => control.Name == name);
    private static Popup Popup(GoToCommitDialog dialog, ComboBox choice) => Field<Popup>(dialog, choice.Name + "HistoryCompletionPopup");
    private static TextBox Editor(ComboBox choice) => choice.GetVisualDescendants().OfType<TextBox>().Single();
    private static Rect Bounds(Window window, Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void Pointer(Control control) => PushProcessTestDriver.Pointer(control);
    private static void Edit(Window window, TextBox editor, string value) { Pointer(editor); Press(window, PhysicalKey.A, RawInputModifiers.Control); window.KeyTextInput(value); Pump(window); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); if (window is Window { IsVisible: false }) { return; } window.UpdateLayout(); window.CaptureRenderedFrame()?.Dispose(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True); }
    private static Task Invoke(MainWindow window, string name, params object[] args)
        => (Task)typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args)!;
    private static T Read<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Write(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static bool Alive(int pid)
    { try { string stat = File.ReadAllText($"/proc/{pid}/stat"); return stat[(stat.LastIndexOf(')') + 2)..].First() is not ('Z' or 'X'); } catch (IOException) { return false; } }

    private sealed class Gates(GitRepository repository, string directory)
    {
        public GitRepository Repository { get; } = repository;
        public bool HasPid(string name) => File.Exists(Path.Combine(directory, name + ".pid")) && new FileInfo(Path.Combine(directory, name + ".pid")).Length > 0;
        public int Pid(string name) => int.Parse(File.ReadAllText(Path.Combine(directory, name + ".pid")));
        public Task Release(string name) => File.WriteAllTextAsync(Path.Combine(directory, name + ".release"), "release");
    }
    private sealed class Fixture : IDisposable
    {
        public const string LocalName = "local/λ;$()'quoted";
        public RootFixture Root { get; }
        public GitRepository Repository => Root.Repository;
        public string First { get; }
        public string Last { get; }
        private readonly List<Window> _windows = [];
        private Fixture(RootFixture root, string first, string last) { Root = root; First = first; Last = last; }
        public static async Task<Fixture> CreateAsync()
        {
            RootFixture root = await RootFixture.CreateAsync(); string first = await root.Repository.ResolveRevisionAsync("HEAD");
            await root.GitAsync("config", "tag.gpgsign", "false"); await root.GitAsync("tag", "-a", "Tag/Été", "-m", "Annotated", first);
            await root.GitAsync("branch", LocalName, first); await root.GitAsync("update-ref", "refs/remotes/origin/remote", first);
            await root.ChangeAsync("second\n", "Second commit"); return new(root, first, await root.Repository.ResolveRevisionAsync("HEAD"));
        }
        public async Task<GoToCommitDialog> ShowAsync(Func<CancellationToken, Task<string?>>? clipboard = null)
        {
            GoToCommitDialog dialog = new(Repository, readClipboard: clipboard ?? (_ => Task.FromResult<string?>(null)));
            _windows.Add(dialog); dialog.Show(); await dialog.InitializeAsync(); Pump(dialog); return dialog;
        }
        public async Task<MainWindow> Browse()
        { MainWindow window = new() { Width = 1400, Height = 900 }; _windows.Add(window); window.Show(); await window.OpenRepositoryAsync(Repository.Path); Pump(window); return window; }
        public async Task<string> Index() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(Repository.Path, ".git/index"))));
        public async Task<Gates> GatesAsync(params string[] blocked)
        {
            string directory = Root.Output("native gates " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); string wrapper = Path.Combine(directory, "git");
            string quote = "'" + directory.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            string script = "#!/bin/sh\nfield=''\nfor arg in \"$@\"; do case \"$arg\" in refs/tags/) field=tags;; refs/heads/) field=heads;; 'HEAD^{commit}') field=clipboard;; esac; done\n";
            foreach (string name in blocked) { script += "if [ \"$field\" = " + name + " ]; then printf '%s' \"$$\" > " + quote + "/" + name + ".pid; while [ ! -f " + quote + "/" + name + ".release ]; do sleep .02; done; fi\n"; }
            script += "exec /usr/bin/git \"$@\"\n"; await File.WriteAllTextAsync(wrapper, script);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            return new(new GitRepository(Repository.Path, new GitRunner(wrapper)), directory);
        }
        public void Dispose() { foreach (Window window in _windows) { window.Close(); } Root.Dispose(); }
    }
}

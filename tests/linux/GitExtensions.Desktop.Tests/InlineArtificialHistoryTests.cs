using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable, Platform("Linux")]
[SupportedOSPlatform("linux")]
public sealed class InlineArtificialHistoryTests
{
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ActualHeadAfterANewerBranchGetsItsTwoInlineEndpointsAndTheDefaultSelectionEvenWhenDetached(bool detached)
    {
        using Fixture f = await Fixture.CreateAsync(detached: detached); string before = await Material(f.Git);
        await using Session s = Session.Start(f.Repository); await Complete(s);
        Assert.That(f.Native[0], Is.Not.EqualTo(f.Head), "The newer independent branch must precede the actual checkout.");
        AssertOrder(s.Rows, f.Native, f.Head);
        Assert.That(s.Rows.SelectedItem, Is.TypeOf<GitCommit>()); Assert.That(((GitCommit)s.Rows.SelectedItem!).Hash, Is.EqualTo(f.Head));
        Assert.That(s.Rows.IsKeyboardFocusWithin, Is.True);
        Press(s.Dialog, PhysicalKey.ArrowUp); AssertEndpoint(s, GitCompareRevisionKind.Index, [new(f.Head)]);
        Press(s.Dialog, PhysicalKey.ArrowUp); AssertEndpoint(s, GitCompareRevisionKind.WorkingTree, [new("", GitCompareRevisionKind.Index)]);
        Press(s.Dialog, PhysicalKey.ArrowUp);
        Assert.That(((GitCommit)s.Rows.SelectedItem!).Hash, Is.EqualTo(f.Native[0])); Assert.That(s.Rows.IsKeyboardFocusWithin, Is.True);
        Assert.That(await Material(f.Git), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(true)] [TestCase(false)]
    public async Task FilteredOutHeadWaitsForEofAndInsertsBeforeItsFirstLoadedAncestorOrAtTheTopWithoutRewritingItsParent(bool ancestor)
    {
        using Fixture f = await Fixture.CreateAsync(newerCount: 8, maximum: 3); string before = await Material(f.Git);
        await using Session s = Session.Start(f.Repository); await Complete(s);
        Edit(s.Dialog, Field<TextBox>(s.Dialog, "InlineSearch"), ancestor ? "Visible ancestor" : "Visible unrelated");
        Pointer(Field<Button>(s.Dialog, "InlineSearchAction")); await Complete(s);
        string[] expected = (await f.Git.GitAsync("log", "--format=%H", "--all", "--max-count=3", "--fixed-strings",
            "--grep=" + (ancestor ? "Visible ancestor" : "Visible unrelated"), "--")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(expected.Length, Is.EqualTo(ancestor ? 1 : 3)); Assert.That(expected, Does.Not.Contain(f.Head));
        Assert.That(s.Rows.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(expected));
        object[] items = s.Rows.Items.Cast<object>().ToArray(); Assert.That(items.Take(2).All(item => item is ArtificialHistoryRevision), Is.True);
        Assert.That(((GitCommit)items[2]).Hash, Is.EqualTo(expected[0])); Assert.That(items.Length, Is.EqualTo(expected.Length + 2), "The two endpoints do not consume the native maximum.");
        ArtificialHistoryRevision index = s.Rows.Items.OfType<ArtificialHistoryRevision>().Single(item => item.Kind == GitCompareRevisionKind.Index);
        Assert.That(index.ParentIds, Is.EqualTo(new[] { f.Head }), "Placement before an ancestor never changes the actual Index parent to that visible ancestor.");
        if (ancestor) { Assert.That(expected[0], Is.EqualTo(f.Ancestor)); }
        AssertActualTypes(s.Rows); Assert.That(await Material(f.Git), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase("unborn")] [TestCase("bare")] [TestCase("setting-off")] [TestCase("request-off")]
    public async Task UnbornBareAndBothIndependentVisibilityPermissionsKeepTheOriginalEndpointContract(string mode)
    {
        using Fixture f = await Fixture.CreateAsync(unborn: mode == "unborn", showArtificial: mode != "setting-off");
        GitRepository repository = f.Repository;
        if (mode == "bare") { string path = f.Git.Output("inline-bare.git"); await f.Git.GitAsync("clone", "--bare", repository.Path, path); repository = new(path); }
        string before = await Material(f.Git);
        await using Session s = Session.Start(repository, new(ShowArtificial: mode != "request-off")); await Complete(s);
        int expected = mode == "unborn" ? 2 : 0;
        Assert.That(s.Rows.Items.OfType<ArtificialHistoryRevision>().Count(), Is.EqualTo(expected)); AssertActualTypes(s.Rows);
        if (mode == "unborn")
        {
            Assert.That(s.Rows.Items.OfType<GitCommit>(), Is.Empty); Assert.That(s.Rows.ItemCount, Is.EqualTo(2));
            Press(s.Dialog, PhysicalKey.Home); AssertEndpoint(s, GitCompareRevisionKind.WorkingTree, [new("", GitCompareRevisionKind.Index)]);
            Press(s.Dialog, PhysicalKey.ArrowDown); AssertEndpoint(s, GitCompareRevisionKind.Index, []);
            Press(s.Dialog, PhysicalKey.Enter); Assert.That(await s.Selection, Is.EqualTo(new GitCompareRevision("", GitCompareRevisionKind.Index)));
        }
        else
        {
            Assert.That(s.Rows.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(f.Native));
            Assert.That(s.Dialog.GetVisualDescendants().OfType<Button>().Any(button => button.Name is "ChooseWorkingDirectoryRevision" or "ChooseIndexRevision"), Is.False);
        }
        Assert.That(await Material(f.Git), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task NativeHomeEndPagesTabAndFirstArrowsUseOneListSelectionAndReturnTypedEndpointsWithEmptyObjectNames()
    {
        using Fixture f = await Fixture.CreateAsync(newerCount: 0); string before = await Material(f.Git);
        await using (Session s = Session.Start(f.Repository))
        {
            await Complete(s); object source = s.Rows.ItemsSource!;
            Press(s.Dialog, PhysicalKey.ArrowUp); AssertEndpoint(s, GitCompareRevisionKind.Index, [new(f.Head)]);
            Button[] labels = s.Rows.GetVisualDescendants().OfType<Button>().Where(button => button.Name is "ChooseWorkingDirectoryRevision" or "ChooseIndexRevision").ToArray();
            Assert.That(labels, Has.Length.EqualTo(2));
            foreach (Button label in labels) { Assert.That(label.Focusable, Is.False); Assert.That(label.FindAncestorOfType<ListBoxItem>(), Is.Not.Null); }
            object selected = s.Rows.SelectedItem!;
            Press(s.Dialog, PhysicalKey.Tab); Assert.That(s.Rows.IsKeyboardFocusWithin, Is.False, "Artificial labels cannot create another tab stop inside the list.");
            Press(s.Dialog, PhysicalKey.Tab, RawInputModifiers.Shift); Assert.That(s.Rows.IsKeyboardFocusWithin, Is.True); Assert.That(s.Rows.SelectedItem, Is.SameAs(selected));
            Press(s.Dialog, PhysicalKey.Home); AssertEndpoint(s, GitCompareRevisionKind.WorkingTree, [new("", GitCompareRevisionKind.Index)]);
            Press(s.Dialog, PhysicalKey.ArrowDown); AssertEndpoint(s, GitCompareRevisionKind.Index, [new(f.Head)]);
            Press(s.Dialog, PhysicalKey.ArrowDown); Assert.That(((GitCommit)s.Rows.SelectedItem!).Hash, Is.EqualTo(f.Head));
            Press(s.Dialog, PhysicalKey.End); Assert.That(s.Rows.SelectedItem, Is.SameAs(s.Rows.Items[s.Rows.ItemCount - 1])); Assert.That(s.Rows.IsKeyboardFocusWithin, Is.True);
            int end = s.Rows.SelectedIndex; Press(s.Dialog, PhysicalKey.PageUp); Assert.That(s.Rows.SelectedIndex, Is.LessThan(end - 1));
            int page = s.Rows.SelectedIndex;
            Press(s.Dialog, PhysicalKey.ArrowDown); Assert.That(s.Rows.SelectedIndex, Is.EqualTo(page + 1));
            Press(s.Dialog, PhysicalKey.ArrowUp); Assert.That(s.Rows.SelectedIndex, Is.EqualTo(page));
            Press(s.Dialog, PhysicalKey.PageDown); Assert.That(s.Rows.SelectedIndex, Is.GreaterThan(page));
            Assert.That(s.Rows.IsKeyboardFocusWithin, Is.True); Press(s.Dialog, PhysicalKey.Home); Press(s.Dialog, PhysicalKey.ArrowDown);
            AssertEndpoint(s, GitCompareRevisionKind.Index, [new(f.Head)]); Assert.That(s.Rows.ItemsSource, Is.SameAs(source));
            Press(s.Dialog, PhysicalKey.Enter); GitCompareRevision? chosen = await s.Selection;
            Assert.That(chosen, Is.EqualTo(new GitCompareRevision("", GitCompareRevisionKind.Index))); Assert.That(chosen!.Revision, Is.Empty);
        }
        await using (Session s = Session.Start(f.Repository, new(ShowArtificial: true, PreselectSelection: new("", GitCompareRevisionKind.WorkingTree))))
        {
            await Complete(s); AssertEndpoint(s, GitCompareRevisionKind.WorkingTree, [new("", GitCompareRevisionKind.Index)]);
            Press(s.Dialog, PhysicalKey.Enter); Assert.That(await s.Selection, Is.EqualTo(new GitCompareRevision("", GitCompareRevisionKind.WorkingTree)));
        }
        Assert.That(await Material(f.Git), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ActualNativeStreamKeepsTheScrolledSourceObjectsAndFirstArrowWhenHeadAndEndpointsArriveAfterSixHundredOtherBranchCommits()
    {
        using Fixture f = await Fixture.CreateAsync(newerCount: 600); Gate gate = await Gate.CreateAsync(f.Git);
        string before = await Material(f.Git); await using Session s = Session.Start(gate.Repository);
        try
        {
            await Until(s.Owner, () => gate.IsPaused && s.Rows.ItemCount == 100 && !s.Dialog.IsBusy);
            Assert.That(s.Dialog.IsHistoryLoading, Is.True); Assert.That(s.Dialog.Completion.IsCompleted, Is.False);
            Assert.That(s.Rows.Items.OfType<ArtificialHistoryRevision>(), Is.Empty, "No fixed prefix may be published before HEAD or successful EOF.");
            Assert.That(s.Rows.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(f.Native.Take(100)));
            var identities = gate.Identities; Assert.That(identities, Has.Length.EqualTo(2)); Assert.That(identities.All(Alive), Is.True);
            object source = s.Rows.ItemsSource!; List<NotifyCollectionChangedAction> notifications = [];
            ((INotifyCollectionChanged)source).CollectionChanged += (_, change) => notifications.Add(change.Action);
            s.Dialog.Height = s.Dialog.MinHeight; Pump(s.Owner);
            Assert.That(s.Rows.ContainerFromIndex(0), Is.InstanceOf<ListBoxItem>());
            Pointer((Control)s.Rows.ContainerFromIndex(0)!); Assert.That(s.Rows.SelectedIndex, Is.Zero);
            Assert.That(s.Rows.IsKeyboardFocusWithin, Is.True);
            for (int index = 1; index <= 35; index++) { Press(s.Dialog, PhysicalKey.ArrowDown); Assert.That(s.Rows.SelectedIndex, Is.EqualTo(index)); }
            object selected = s.Rows.SelectedItem!; Control? focus = s.Dialog.FocusManager?.GetFocusedElement() as Control;
            ScrollViewer scroll = s.Rows.GetVisualDescendants().OfType<ScrollViewer>().First(); Vector offset = scroll.Offset;
            Assert.That(offset.Y, Is.GreaterThan(0)); Assert.That(((GitCommit)selected).Hash, Is.EqualTo(f.Native[35]));
            await gate.ReleaseAsync(); await Complete(s);
            AssertOrder(s.Rows, f.Native, f.Head); Assert.That(s.Rows.ItemsSource, Is.SameAs(source)); Assert.That(s.Rows.SelectedItem, Is.SameAs(selected));
            Assert.That(s.Dialog.FocusManager?.GetFocusedElement(), Is.SameAs(focus)); Assert.That(scroll.Offset, Is.EqualTo(offset));
            Assert.That(notifications, Does.Not.Contain(NotifyCollectionChangedAction.Reset)); Assert.That(s.Rows.Selection.AnchorIndex, Is.EqualTo(35));
            Press(s.Dialog, PhysicalKey.ArrowDown); Assert.That(((GitCommit)s.Rows.SelectedItem!).Hash, Is.EqualTo(f.Native[36])); Assert.That(s.Rows.IsKeyboardFocusWithin, Is.True);
            Assert.That(gate.Invocations, Is.EqualTo(1)); foreach (var identity in identities) { Assert.That(Alive(identity), Is.False, "The exact actor, including zombie state, must be retired: " + identity); }
            Assert.That(await Material(f.Git), Is.EqualTo(before));
        }
        finally { await gate.ReleaseAsync(); }
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task InlineEndpointsRenderWithinTheMinimumInTheSameScrollSurfaceAndKeepTheirHistoricalLabels(string language, bool dark)
    {
        using Fixture f = await Fixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        await using Session s = Session.Start(f.Repository, dark: dark); await Complete(s);
        s.Dialog.Width = s.Dialog.MinWidth; s.Dialog.Height = s.Dialog.MinHeight; Pump(s.Owner);
        AssertOrder(s.Rows, f.Native, f.Head); Assert.That(s.Rows.SelectionMode, Is.EqualTo(Avalonia.Controls.SelectionMode.Single));
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        foreach ((string id, GitCompareRevisionKind kind, string title) in new[]
        {
            ("ChooseWorkingDirectoryRevision", GitCompareRevisionKind.WorkingTree, "Working directory"),
            ("ChooseIndexRevision", GitCompareRevisionKind.Index, "Commit index")
        })
        {
            Button label = Field<Button>(s.Dialog, id); ListBoxItem row = label.FindAncestorOfType<ListBoxItem>()!;
            Assert.That(row, Is.Not.Null); Assert.That(row.FindAncestorOfType<ListBox>(), Is.SameAs(s.Rows));
            Assert.That(row.DataContext, Is.TypeOf<ArtificialHistoryRevision>()); Assert.That(((ArtificialHistoryRevision)row.DataContext!).Kind, Is.EqualTo(kind));
            Assert.That(label.Focusable, Is.False);
            string expected = translations.Translate(title);
            Assert.That(label.Content as string == expected || label.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == expected), Is.True);
            Rect bounds = new(label.TranslatePoint(default, s.Dialog)!.Value, label.Bounds.Size);
            Assert.That(label.IsEffectivelyVisible && bounds.Width > 0 && bounds.Height > 0 && new Rect(default, s.Dialog.ClientSize).Contains(bounds), Is.True, id + " " + bounds);
        }
        Press(s.Dialog, PhysicalKey.ArrowUp); AssertEndpoint(s, GitCompareRevisionKind.Index, [new(f.Head)]);
        Capture(s.Dialog, "inline-artificial-minimum-" + language + (dark ? "-dark.png" : "-light.png"));
    }

    private static void AssertOrder(ListBox rows, string[] native, string head)
    {
        object[] expected = native.Cast<object>().ToArray(); int position = Array.IndexOf(native, head); Assert.That(position, Is.GreaterThanOrEqualTo(0));
        object[] actual = rows.Items.Cast<object>().ToArray(); Assert.That(actual.Length, Is.EqualTo(expected.Length + 2));
        Assert.That(actual.Take(position).OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(native.Take(position)));
        Assert.That(actual[position], Is.TypeOf<ArtificialHistoryRevision>()); Assert.That(((ArtificialHistoryRevision)actual[position]).Kind, Is.EqualTo(GitCompareRevisionKind.WorkingTree));
        Assert.That(actual[position + 1], Is.TypeOf<ArtificialHistoryRevision>()); Assert.That(((ArtificialHistoryRevision)actual[position + 1]).Kind, Is.EqualTo(GitCompareRevisionKind.Index));
        Assert.That(((GitCommit)actual[position + 2]).Hash, Is.EqualTo(head)); Assert.That(rows.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(native)); AssertActualTypes(rows);
    }
    private static void AssertActualTypes(ListBox rows)
    {
        Assert.That(rows.Items.Cast<object>().All(item => item is GitCommit or ArtificialHistoryRevision), Is.True);
        foreach (ArtificialHistoryRevision item in rows.Items.OfType<ArtificialHistoryRevision>())
        { Assert.That(item.Revision.Revision, Is.Empty); Assert.That(item.GraphId, Does.StartWith(":")); Assert.That(item.GraphId.All(Uri.IsHexDigit), Is.False); }
        Assert.That(rows.Items.OfType<GitCommit>().All(commit => (commit.Hash.Length is 40 or 64) && commit.Hash.All(Uri.IsHexDigit)), Is.True, "No fabricated GitCommit/SHA may carry an endpoint.");
    }
    private static void AssertEndpoint(Session s, GitCompareRevisionKind kind, GitCompareRevision[] parents)
    {
        Assert.That(s.Rows.SelectedItem, Is.TypeOf<ArtificialHistoryRevision>()); Assert.That(((ArtificialHistoryRevision)s.Rows.SelectedItem!).Kind, Is.EqualTo(kind));
        ChooseCommitSelectionPresenter presenter = s.Dialog.GetVisualDescendants().OfType<ChooseCommitSelectionPresenter>().Single();
        Assert.That(presenter.Selection!.Commit, Is.Null); Assert.That(presenter.Selection.Revision, Is.EqualTo(new GitCompareRevision("", kind)));
        Assert.That(presenter.Parents, Is.EqualTo(parents)); Assert.That(s.Rows.IsKeyboardFocusWithin, Is.True);
        Assert.That(s.Rows.SelectedItems, Has.Count.EqualTo(1)); AssertActualTypes(s.Rows);
    }
    private static async Task Complete(Session s)
    { await Until(s.Owner, () => !s.Dialog.IsBusy && !s.Dialog.IsHistoryLoading && s.Dialog.Completion.IsCompleted); await s.Dialog.Completion; Pump(s.Owner); Assert.That(s.Dialog.LastError, Is.Null, s.Dialog.LastException?.ToString()); }
    private static T Field<T>(TopLevel root, string id) where T : Control => PushProcessTestDriver.Field<T>(root, id);
    private static void Pointer(Control control) => PushProcessTestDriver.Pointer(control);
    private static void Edit(Window window, TextBox editor, string value)
    { Pointer(editor); Press(window, PhysicalKey.A, RawInputModifiers.Control); Press(window, PhysicalKey.Backspace); window.KeyTextInput(value); Pump(window); Assert.That(editor.Text, Is.EqualTo(value)); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Pump(Window root)
    { Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } foreach (Window child in root.OwnedWindows.ToArray()) { Pump(child); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Window owner, Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Pump(owner); await Task.Delay(10); }
        Pump(owner); if (!condition() && owner.IsVisible) { Capture(owner, "inline-artificial-timeout-" + Guid.NewGuid().ToString("N") + ".png"); }
        Assert.That(condition(), Is.True, string.Join("; ", owner.OwnedWindows.Select(child => child.Name + " visible=" + child.IsVisible
            + (child is ChooseCommitDialog chooser ? " busy=" + chooser.IsBusy + " history=" + chooser.IsHistoryLoading + " error=" + chooser.LastError : ""))));
    }
    private static void Capture(Window window, string name)
    { string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory); using var frame = window.GetLastRenderedFrame(); Assert.That(frame, Is.Not.Null); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }
    private static async Task<string> Material(GitFixture f)
    {
        string profile = f.Output("config");
        return (await f.GitAsync("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput + "\n"
            + string.Join('\n', new[] { ".git/HEAD", ".git/index", ".git/config", "file.txt" }.Where(path => File.Exists(f.PathInRepository(path)))
                .Select(path => path + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.PathInRepository(path))))))
            + "\nprofile:" + (Directory.Exists(profile) ? string.Join('\n', Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(path => path + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))) : "");
    }
    private sealed class TestChooser : ChooseCommitDialog
    {
        internal TestChooser(GitRepository repository, ChooseCommitRequest request) : base(repository, request,
            new("InlineRows", "InlineAccept", "InlineCancel", "InlineSearch", "InlineSearchAction"), default) { }
        internal Task<GitCompareRevision?> OpenAsync(Window owner) => ShowComparisonPickerAsync(this, owner, default);
    }
    private sealed class Session : IAsyncDisposable
    {
        internal Window Owner { get; } internal TestChooser Dialog { get; } internal Task<GitCompareRevision?> Selection { get; }
        internal ListBox Rows => Field<ListBox>(Dialog, "InlineRows");
        private Session(GitRepository repository, ChooseCommitRequest request, bool dark)
        { Owner = new() { Width = 1200, Height = 800, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; Owner.Show(); Dialog = new(repository, request); Selection = Dialog.OpenAsync(Owner); Pump(Owner); }
        internal static Session Start(GitRepository repository, ChooseCommitRequest? request = null, bool dark = false) => new(repository, request ?? new(ShowArtificial: true), dark);
        public async ValueTask DisposeAsync()
        { foreach (Window child in Dialog.OwnedWindows.ToArray()) { child.Close(); } Dialog.Close(); await Until(Owner, () => !Dialog.IsVisible && Selection.IsCompleted); await Dialog.Completion; await Selection; Owner.Close(); Pump(Owner); }
    }
    private sealed record Fixture(GitFixture Git, string[] Native, string Head, string Ancestor) : IDisposable
    {
        internal GitRepository Repository => Git.Repository;
        internal static async Task<Fixture> CreateAsync(int newerCount = 1, bool detached = false, bool unborn = false, bool showArtificial = true, int maximum = 0)
        {
            GitFixture git = await GitFixture.CreateAsync(unborn: true);
            try
            {
                git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
                if (!unborn)
                {
                    StringBuilder input = new();
                    void Branch(string name, int count, int firstMark, long date)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            string message = name == "main" && i == 40 ? "Visible ancestor\n" : name == "newer" ? "Visible unrelated " + i + "\n" : "Main revision " + i + "\n";
                            input.Append("commit refs/heads/").Append(name).Append("\nmark :").Append(firstMark + i).Append("\ncommitter Inline Tester <inline@example.invalid> ")
                                .Append(date + i).Append(" +0000\ndata ").Append(Encoding.UTF8.GetByteCount(message)).Append('\n').Append(message);
                            if (i > 0) { input.Append("from :").Append(firstMark + i - 1).Append('\n'); }
                            if (i == 0) { input.Append("M 100644 inline file.txt\ndata 5\nseed\n"); } input.Append('\n');
                        }
                    }
                    Branch("main", 60, 1, 1700000000L); Branch("newer", newerCount, 1000, 1800000000L); input.Append("done\n");
                    GitResult imported = await new GitRunner("/usr/bin/git").RunAsync(git.Repository.Path, ["fast-import", "--quiet"], standardInput: input.ToString());
                    Assert.That(imported.Success, Is.True, imported.StandardError); await git.GitAsync("reset", "--hard", "HEAD");
                    if (detached) { await git.GitAsync("checkout", "--detach", "HEAD"); }
                }
                new PreferencesStore().Save(new DesktopPreferences { MaxRevisionGraphCommits = maximum, HistoryLimit = 128 });
                new HistoryViewPreferencesStore().Save(new HistoryViewPreferences { ShowArtificialCommits = showArtificial, ShowAvatar = false, ShowStashes = false, ShowSessionCheckpoints = false, Sort = GitLogSort.GitDefault });
                new FileHistoryPreferencesStore().Save(new());
                string[] native = unborn ? [] : (await git.GitAsync("log", "--format=%H", "--all", "--")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                string head = unborn ? "" : await git.Repository.ResolveRevisionAsync("HEAD"), ancestor = unborn ? "" : await git.Repository.ResolveRevisionAsync("HEAD~19");
                return new(git, native, head, ancestor);
            }
            catch { git.Dispose(); throw; }
        }
        public void Dispose() => Git.Dispose();
    }
    private static bool Alive((int Pid, string Start) identity)
    { try { string stat = File.ReadAllText("/proc/" + identity.Pid + "/stat"); return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19] == identity.Start; } catch (IOException) { return false; } }
    private sealed class Gate(string directory, GitRepository repository)
    {
        internal GitRepository Repository { get; } = repository;
        internal bool IsPaused => File.Exists(Path.Combine(directory, "paused"));
        internal int Invocations => File.Exists(Path.Combine(directory, "invocations")) ? File.ReadAllLines(Path.Combine(directory, "invocations")).Length : 0;
        internal (int Pid, string Start)[] Identities
        { get { try { using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "identities.json"))); return json.RootElement.EnumerateArray().Select(item => (item.GetProperty("pid").GetInt32(), item.GetProperty("start").GetString()!)).ToArray(); } catch (Exception error) when (error is IOException or JsonException) { return []; } } }
        internal Task ReleaseAsync() => File.WriteAllTextAsync(Path.Combine(directory, "release"), "release\n");
        internal static async Task<Gate> CreateAsync(GitFixture fixture)
        {
            string directory = fixture.Output("inline-native-gate"); Directory.CreateDirectory(directory); string executable = Path.Combine(directory, "git");
            await File.WriteAllTextAsync(executable, $$"""
                #!/usr/bin/python3
                import os,sys,json,time,subprocess,shutil,threading
                from pathlib import Path
                args=sys.argv[1:]
                if 'log' not in args: os.execv('/usr/bin/git',['/usr/bin/git',*args])
                deadline=threading.Timer(40,lambda:os._exit(124)); deadline.daemon=True; deadline.start()
                directory=Path({{JsonSerializer.Serialize(directory)}})
                with (directory/'invocations').open('a') as out: out.write(str(os.getpid())+'\n')
                child=subprocess.Popen(['/usr/bin/git',*args],stdout=subprocess.PIPE,bufsize=0)
                def identity(pid):
                    fields=Path('/proc/'+str(pid)+'/stat').read_text().rsplit(')',1)[1].strip().split()
                    return dict(pid=pid,start=fields[19])
                (directory/'identities.pending').write_text(json.dumps([identity(os.getpid()),identity(child.pid)]))
                os.replace(directory/'identities.pending',directory/'identities.json')
                prefix=bytearray(); separators=0
                while separators<8*100:
                    value=child.stdout.read(1)
                    if not value:
                        child.wait(); sys.stderr.write('fixture ended before100 records\n'); sys.exit(79)
                    prefix.extend(value)
                    if value==b'\0': separators+=1
                sys.stdout.buffer.write(prefix); sys.stdout.buffer.flush(); (directory/'paused').write_text('100')
                while not (directory/'release').exists(): time.sleep(.01)
                shutil.copyfileobj(child.stdout,sys.stdout.buffer,length=32768); sys.stdout.buffer.flush(); sys.exit(child.wait())
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new(directory, new GitRepository(fixture.Repository.Path, new GitRunner(executable)));
        }
    }
}

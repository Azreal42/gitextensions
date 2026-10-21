using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RemotesTrackingGridTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task RealGripsKeepFillAlignmentRowsAndFirstArrowNavigationWithoutRebinding(string language, bool dark)
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync();
        await f.GitAsync("branch", "alpha"); await f.GitAsync("branch", "omega");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        await using Lease s = await Lease.Open(f.Repository, "main", dark);
        ListBox list = Field<ListBox>(s.Dialog, "BranchTrackingList"); Grid header = Field<Grid>(s.Dialog, "BranchTrackingHeaders");
        object source = list.ItemsSource!; object selected = list.SelectedItem!;
        ListBoxItem container = (ListBoxItem)list.ContainerFromIndex(list.SelectedIndex)!;
        Grid row = Row(container); string before = await Materials(f);
        double[] initial = Widths(header); double total = initial.Sum();
        Assert.That(list.SelectionMode, Is.EqualTo(SelectionMode.Single));
        AssertAligned(s.Dialog); Drag(s.Dialog, Field<Thumb>(s.Dialog, "ResizeBranchTracking0"), 40);
        double[] first = Widths(header);
        Assert.That(first[0], Is.GreaterThan(initial[0] + 20));
        Assert.That(first[1], Is.LessThan(initial[1])); Assert.That(first[2], Is.LessThan(initial[2]));
        Assert.That(first[1] / first[2], Is.EqualTo(initial[1] / initial[2]).Within(.02));
        Drag(s.Dialog, Field<Thumb>(s.Dialog, "ResizeBranchTracking1"), 30); double[] second = Widths(header);
        Assert.That(second[0], Is.EqualTo(first[0]).Within(1)); Assert.That(second[1], Is.GreaterThan(first[1] + 15));
        Assert.That(second[2], Is.LessThan(first[2]));
        Drag(s.Dialog, Field<Thumb>(s.Dialog, "ResizeBranchTracking2"), 20); double[] third = Widths(header);
        Assert.That(third[2], Is.GreaterThan(second[2] + 10)); Assert.That(third[0], Is.LessThan(second[0])); Assert.That(third[1], Is.LessThan(second[1]));
        Assert.That(third.Sum(), Is.EqualTo(total).Within(1)); AssertAligned(s.Dialog);
        Assert.That(list.ItemsSource, Is.SameAs(source)); Assert.That(list.SelectedItem, Is.SameAs(selected));
        Assert.That(list.ContainerFromIndex(list.SelectedIndex), Is.SameAs(container)); Assert.That(Row(container), Is.SameAs(row));
        Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Press(s.Dialog, PhysicalKey.ArrowDown); Assert.That(((GitBranchTrackingSnapshot)list.SelectedItem!).LocalBranch, Is.EqualTo("omega"));
        Press(s.Dialog, PhysicalKey.ArrowUp); Assert.That(list.SelectedItem, Is.SameAs(selected));
        double[] ratio = third.Select(width => width / third.Sum()).ToArray();
        s.Dialog.Width = 1150; Pump(s.Dialog); AssertAligned(s.Dialog);
        double[] expanded = Widths(header);
        for (int i = 0; i < 3; i++) { Assert.That(expanded[i] / expanded.Sum(), Is.EqualTo(ratio[i]).Within(.003)); }
        Pointer(Field<Button>(s.Dialog, "BranchTrackingSort0"));
        Assert.That(list.SelectedItem, Is.SameAs(selected)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
        Assert.That(list.Items.OfType<GitBranchTrackingSnapshot>().Select(item => item.LocalBranch), Is.EqualTo(new[] { "omega", "main", "alpha" }));
        Press(s.Dialog, PhysicalKey.ArrowDown); Assert.That(((GitBranchTrackingSnapshot)list.SelectedItem!).LocalBranch, Is.EqualTo("alpha"));
        Press(s.Dialog, PhysicalKey.ArrowUp); Assert.That(list.SelectedItem, Is.SameAs(selected));
        AssertAligned(s.Dialog); Capture(s.Dialog, "remotes-tracking-grid-" + language + (dark ? "-dark.png" : "-light.png"));
        Drag(s.Dialog, Field<Thumb>(s.Dialog, "ResizeBranchTracking0"), 1200);
        Assert.That(Widths(header), Is.All.GreaterThanOrEqualTo(4.5)); Assert.That(Widths(header).Sum(), Is.EqualTo(expanded.Sum()).Within(1));
        Drag(s.Dialog, Field<Thumb>(s.Dialog, "ResizeBranchTracking0"), -1200);
        Assert.That(Widths(header)[0], Is.EqualTo(5).Within(.5)); AssertAligned(s.Dialog);
        Assert.That(await Materials(f), Is.EqualTo(before)); Assert.That(s.Dialog.HasRepositoryChanges, Is.False); Assert.That(s.Dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    [TestCase("en", false, 1)] [TestCase("fr", true, 1)]
    [TestCase("en", false, 3)] [TestCase("fr", true, 3)]
    public async Task NativeCtrlCCopiesTheCompleteSelectedRowAndAutoHeaderOnlyForASingleDisplayedBranch(string language, bool dark, int count)
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync();
        string selected = count == 1 ? "main" : "topic/été;$()'literal-" + new string('x', 150);
        if (count > 1) { await f.GitAsync("branch", selected); await f.GitAsync("branch", "unselected"); }
        await f.GitAsync("config", "branch." + selected + ".remote", "origin");
        await f.GitAsync("config", "branch." + selected + ".merge", "refs/heads/target");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        await using Lease s = await Lease.Open(f.Repository, selected, dark);
        ListBox rows = Field<ListBox>(s.Dialog, "BranchTrackingList"); var clipboard = s.Dialog.Clipboard!;
        string? prior = await ClipboardExtensions.TryGetTextAsync(clipboard); string before = await Materials(f);
        try
        {
            Assert.That(rows.ItemCount, Is.EqualTo(count)); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
            if (count > 1) { Assert.That(Row((ListBoxItem)rows.ContainerFromIndex(rows.SelectedIndex)!).Children.OfType<TextBlock>().Any(text => text.TextLayout.TextLines.Any(line => line.HasCollapsed)), Is.True); }
            string expected = selected + "\torigin\ttarget";
            if (count == 1)
            {
                TranslationCatalog t = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.CurrentUICulture);
                expected = t.Translate("FormRemotes", "BranchName", "HeaderText", "Local branch name") + "\t"
                    + t.Translate("FormRemotes", "RemoteCombo", "HeaderText", "Remote repository") + "\t"
                    + t.Translate("FormRemotes", "MergeWith", "HeaderText", "Default merge with") + "\r\n" + expected;
            }
            Press(s.Dialog, PhysicalKey.C, RawInputModifiers.Control); await s.Dialog.TrackingPresentationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo(expected));
            Assert.That(rows.IsKeyboardFocusWithin, Is.True); Assert.That(((GitBranchTrackingSnapshot)rows.SelectedItem!).LocalBranch, Is.EqualTo(selected));
            Pointer(Field<Button>(s.Dialog, "BranchTrackingSort1")); Press(s.Dialog, PhysicalKey.C, RawInputModifiers.Control);
            await s.Dialog.TrackingPresentationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo(expected));
            Assert.That(await Materials(f), Is.EqualTo(before)); Assert.That(s.Dialog.HasRepositoryChanges, Is.False);
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, prior ?? ""); }
    }

    [AvaloniaTest]
    public async Task UnicodeTextKeepsQuotesAndNewlinesReplacesEmbeddedTabsAndAddsNoTrailingRowSeparator()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync(); await f.GitAsync("branch", "other");
        await f.GitAsync("config", "branch.main.remote", "origin\tquoted\" λ\r\nline");
        await f.GitAsync("config", "branch.main.merge", "refs/heads/topic\t\rsecond\nthird");
        await using Lease s = await Lease.Open(f.Repository); var clipboard = s.Dialog.Clipboard!;
        string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard); string before = await Materials(f);
        try
        {
            Press(s.Dialog, PhysicalKey.C, RawInputModifiers.Control); await s.Dialog.TrackingPresentationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("main\torigin quoted\" λ\r\nline\ttopic \rsecond\nthird"));
            Assert.That(await Materials(f), Is.EqualTo(before)); Assert.That(s.Dialog.HasRepositoryChanges, Is.False);
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task CopyInsideTheReadOnlyFieldOrEditableMergeKeepsTheNativeEditorSelection(bool editable)
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync();
        await f.GitAsync("config", "branch.main.remote", "origin"); await f.GitAsync("config", "branch.main.merge", "refs/heads/main");
        await using Lease s = await Lease.Open(f.Repository); var clipboard = s.Dialog.Clipboard!;
        string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard); string before = await Materials(f);
        try
        {
            TextBox editor = editable ? Field<ComboBox>(s.Dialog, "TrackingMergeBranch").GetVisualDescendants().OfType<TextBox>().Single()
                : Field<TextBox>(s.Dialog, "TrackingLocalBranch");
            Pointer(editor); Press(s.Dialog, PhysicalKey.A, RawInputModifiers.Control); Press(s.Dialog, PhysicalKey.C, RawInputModifiers.Control);
            Assert.That(editor.SelectedText, Is.EqualTo("main")); Assert.That(editor.IsKeyboardFocusWithin, Is.True);
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("main"));
            Assert.That(await Materials(f), Is.EqualTo(before)); Assert.That(s.Dialog.HasRepositoryChanges, Is.False);
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""); }
    }

    [AvaloniaTest]
    public async Task CopyWithNoBranchSelectionPreservesTheClipboardAndDoesNotCreateConfiguration()
    {
        using Fixture f = await Fixture.CreateAsync(unborn: true); await f.GitAsync("remote", "add", "origin", f.Output("not-fetched.git"));
        await using Lease s = await Lease.Open(f.Repository); var clipboard = s.Dialog.Clipboard!;
        string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard); string before = await Materials(f);
        try
        {
            Assert.That(Field<ListBox>(s.Dialog, "BranchTrackingList").ItemCount, Is.Zero);
            await ClipboardExtensions.SetTextAsync(clipboard, "unchanged clipboard λ");
            Press(s.Dialog, PhysicalKey.C, RawInputModifiers.Control); await s.Dialog.TrackingPresentationCompletion;
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("unchanged clipboard λ"));
            Assert.That(await Materials(f), Is.EqualTo(before)); Assert.That(s.Dialog.HasRepositoryChanges, Is.False);
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""); }
    }

    [AvaloniaTest]
    public async Task RealGripsValidateOnBlurAndKeepASecondMergeQueuedBehindTheFirstNativeWrite()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("This real Git gate uses a POSIX wrapper and Unix executable mode."); return; }
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync(); await f.GitAsync("branch", "other");
        await f.GitAsync("config", "branch.main.remote", "."); await f.GitAsync("config", "branch.main.merge", "refs/heads/main");
        await f.GitAsync("config", "branch.main.description", "preserve description\nand line two");
        await f.GitAsync("config", "--add", "remote.origin.review", "opaque one"); await f.GitAsync("config", "--add", "remote.origin.review", "opaque two");
        string configBefore = (await f.GitAsync("config", "--local", "--null", "--list")).StandardOutput;
        string refsBefore = (await f.GitAsync("show-ref", "--head")).StandardOutput;
        string wrapper = f.Output("tracking-grid-git"), marker = wrapper + ".entered", release = wrapper + ".release", calls = wrapper + ".calls";
        string script = "#!/bin/sh\nprintf '%s\\t' \"$@\" >> " + Quote(calls) + "\nprintf '\\n' >> " + Quote(calls)
            + "\ncase \"$*\" in *'check-ref-format refs/heads/first-edit'*) : > " + Quote(marker)
            + "; while test ! -f " + Quote(release) + "; do sleep 0.01; done ;; esac\nexec /usr/bin/git \"$@\"\n";
        await File.WriteAllTextAsync(wrapper, script); File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using Lease s = await Lease.Open(new GitRepository(f.Repository.Path, new GitRunner(wrapper)));
        var clipboard = s.Dialog.Clipboard!; string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard);
        try
        {
            TextBox editor = Field<ComboBox>(s.Dialog, "TrackingMergeBranch").GetVisualDescendants().OfType<TextBox>().Single();
            Type(s.Dialog, editor, "first-edit"); Drag(s.Dialog, Field<Thumb>(s.Dialog, "ResizeBranchTracking0"), 25);
            await Until(s.Dialog, () => File.Exists(marker)); Assert.That(Writes(s.Dialog).IsCompleted, Is.False);
            Type(s.Dialog, editor, "second-edit"); Drag(s.Dialog, Field<Thumb>(s.Dialog, "ResizeBranchTracking1"), 20);
            Assert.That(Writes(s.Dialog).IsCompleted, Is.False);
            ListBox list = Field<ListBox>(s.Dialog, "BranchTrackingList"); Assert.That(list.IsKeyboardFocusWithin, Is.True);
            Assert.That(((GitBranchTrackingSnapshot)list.SelectedItem!).LocalBranch, Is.EqualTo("main"));
            Press(s.Dialog, PhysicalKey.C, RawInputModifiers.Control); await s.Dialog.TrackingPresentationCompletion;
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("main\t.\tmain"), "An uncommitted editor draft is not a grid snapshot.");
            await File.WriteAllTextAsync(release, "release\n");
            await Until(s.Dialog, () => !s.Dialog.IsBusy && Writes(s.Dialog).IsCompleted && editor.Text == "second-edit");
            Assert.That(s.Dialog.LastError, Is.Null, s.Dialog.LastException?.ToString()); AssertAligned(s.Dialog);
            Assert.That((await f.GitAsync("config", "--get", "branch.main.merge")).StandardOutput.Trim(), Is.EqualTo("refs/heads/second-edit"));
            Assert.That(OtherConfig((await f.GitAsync("config", "--local", "--null", "--list")).StandardOutput), Is.EqualTo(OtherConfig(configBefore)));
            Assert.That((await f.GitAsync("show-ref", "--head")).StandardOutput, Is.EqualTo(refsBefore));
            string[] nativeCalls = File.ReadAllLines(calls);
            Assert.That(nativeCalls.Count(line => line.Contains("\tcheck-ref-format\trefs/heads/second-edit\t", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(nativeCalls.Count(line => line.Contains("\t--add\t--\tbranch.main.merge\trefs/heads/second-edit\t", StringComparison.Ordinal)), Is.EqualTo(1));
            Press(s.Dialog, PhysicalKey.C, RawInputModifiers.Control); await s.Dialog.TrackingPresentationCompletion;
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("main\t.\tsecond-edit"));
        }
        finally { await File.WriteAllTextAsync(release, "cleanup\n"); await ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""); }
    }

    private static string[] OtherConfig(string text) => text.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(record => !record.StartsWith("branch.main.merge\n", StringComparison.Ordinal)).ToArray();
    private static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static Task Writes(RemotesDialog dialog) => (Task)typeof(RemotesDialog).GetField("_trackingWrites", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static Grid Row(ListBoxItem container) => container.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "BranchTrackingRow");
    private static double[] Widths(Grid header) => header.ColumnDefinitions.Select(column => column.ActualWidth).ToArray();
    private static void AssertAligned(RemotesDialog dialog)
    {
        Pump(dialog); Grid header = Field<Grid>(dialog, "BranchTrackingHeaders"); ListBox list = Field<ListBox>(dialog, "BranchTrackingList");
        double[] widths = Widths(header);
        for (int column = 0; column < 3; column++)
        {
            Button heading = Field<Button>(dialog, "BranchTrackingSort" + column);
            Assert.That(heading.Bounds.Width, Is.EqualTo(widths[column]).Within(1), "The native-style header fills its complete column.");
            Assert.That(heading.TranslatePoint(default, header)!.Value.X, Is.EqualTo(widths.Take(column).Sum()).Within(1));
        }
        foreach (Grid row in list.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Name == "BranchTrackingRow" && grid.IsEffectivelyVisible))
        {
            Assert.That(row.TranslatePoint(default, dialog)!.Value.X, Is.EqualTo(header.TranslatePoint(default, dialog)!.Value.X).Within(1));
            for (int i = 0; i < 3; i++) { Assert.That(row.ColumnDefinitions[i].ActualWidth, Is.EqualTo(widths[i]).Within(1)); }
            Assert.That(row.Bounds.Width, Is.EqualTo(widths.Sum()).Within(1));
        }
    }
    private static void Pointer(Control control) => PushProcessTestDriver.Pointer(control);
    private static void Drag(Window owner, Thumb grip, double delta)
    {
        for (int i = 0; i < 10; i++) { Pump(owner); }
        Point start = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), owner)!.Value;
        Assert.That(grip.IsEffectivelyVisible && grip.IsEffectivelyEnabled && new Rect(default, owner.ClientSize).Contains(start)
            && (owner.InputHitTest(start) as Visual)?.GetSelfAndVisualAncestors().Contains(grip) == true, Is.True, "A real resize grip must be the actual pointer target: " + grip.Name);
        owner.MouseMove(start); owner.MouseDown(start, MouseButton.Left); owner.MouseMove(start + new Vector(delta, 0)); owner.MouseUp(start + new Vector(delta, 0), MouseButton.Left); Pump(owner);
    }
    private static void Press(Window owner, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { owner.KeyPressQwerty(key, modifiers); owner.KeyReleaseQwerty(key, modifiers); Pump(owner); }
    private static void Type(Window owner, TextBox editor, string value)
    { Pointer(editor); Press(owner, PhysicalKey.A, RawInputModifiers.Control); owner.KeyTextInput(value); Pump(owner); Assert.That(editor.Text, Is.EqualTo(value)); }
    private static void Pump(TopLevel owner)
    { Dispatcher.UIThread.RunJobs(); owner.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(TopLevel owner, Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline) { Pump(owner); await Task.Delay(10); }
        Pump(owner); Assert.That(condition(), Is.True, owner is RemotesDialog dialog ? "Tracking timeout: busy=" + dialog.IsBusy + " error=" + dialog.LastError : "Tracking timeout");
    }
    private static async Task<string> Materials(Fixture f)
        => (await f.GitAsync("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput + "\n" + string.Join('\n',
            new[] { ".git/HEAD", ".git/index", ".git/config", "file.txt" }.Select(path => path + ":" + (File.Exists(f.PathInRepository(path)) ? Convert.ToHexString(File.ReadAllBytes(f.PathInRepository(path))) : "<missing>")));
    private static void Capture(Window owner, string name)
    {
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); using var frame = owner.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private sealed class Lease : IAsyncDisposable
    {
        internal Window Owner { get; } = new() { Width = 1250, Height = 850 };
        internal RemotesDialog Dialog { get; private set; } = null!;
        private Task _modal = Task.CompletedTask;
        internal static async Task<Lease> Open(GitRepository repository, string selected = "main", bool dark = false)
        {
            Lease s = new(); s.Owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; s.Owner.Show();
            s.Dialog = new(repository, preselectLocalBranch: selected) { RequestedThemeVariant = s.Owner.ActualThemeVariant };
            s._modal = s.Dialog.ShowDialog(s.Owner); await s.Dialog.InitializeAsync(); Pump(s.Dialog);
            Assert.That(s.Dialog.LastError, Is.Null, s.Dialog.LastException?.ToString()); return s;
        }
        public async ValueTask DisposeAsync()
        {
            await Until(Dialog, () => !Dialog.IsBusy && Writes(Dialog).IsCompleted);
            Dialog.Close(); await _modal.WaitAsync(TimeSpan.FromSeconds(10)); await Dialog.TrackingPresentationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Owner.Close(); Pump(Owner);
        }
    }
}

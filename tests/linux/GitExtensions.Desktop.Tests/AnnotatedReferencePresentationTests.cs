using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
using BrowseFixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AnnotatedReferencePresentationTests
{
    [AvaloniaTest]
    public async Task AnnotatedAndLightTagsAttachOnCreationAndRefreshWithoutReplacingRowsOrRawIdentities()
    {
        using RootFixture f = await RootFixture.CreateAsync(); string first = await f.Repository.ResolveRevisionAsync("HEAD");
        await f.GitAsync("tag", "light", first); await f.GitAsync("tag", "-a", "annotated", "-m", "Annotated", first);
        await f.ChangeAsync("second\n", "Second");
        IReadOnlyList<GitCommit> commits = await f.Repository.GetLogAsync(10, "HEAD"); GitCommit old = commits.Single(c => c.Hash == first), head = commits.First();
        IReadOnlyList<GitReference> references = await f.Repository.GetReferencesAsync();
        Control oldRow = RevisionHistoryRow.Create(old, references), headRow = RevisionHistoryRow.Create(head, references);
        StackPanel panel = new() { Children = { headRow, oldRow } }; Window window = new() { Width = 950, Height = 100, Content = panel }; window.Show();
        try
        {
            Pump(window); Assert.That(BadgeNames(oldRow), Is.EquivalentTo(new[] { "refs/tags/light", "refs/tags/annotated" }));
            AssertMessageTooltip(oldRow, old, "annotated", "light");
            GitReference annotated = references.Single(r => r.ShortName == "annotated");
            Assert.That(annotated.Hash, Is.Not.EqualTo(first)); Assert.That(annotated.RevisionHash, Is.EqualTo(first));
            Assert.That(Badges(oldRow).Single(b => ((GitReference)b.Tag!).IsTag && ((GitReference)b.Tag!).ShortName == "annotated").Tag, Is.SameAs(annotated));
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            { window.RequestedThemeVariant = theme; Capture(window, "annotated-reference-initial-" + theme + ".png"); }

            await f.GitAsync("tag", "-f", "-a", "annotated", "-m", "Moved", head.Hash);
            references = await f.Repository.GetReferencesAsync();
            Present(oldRow, old, references); Present(headRow, head, references); Pump(window);
            Assert.That(BadgeNames(oldRow), Is.EqualTo(new[] { "refs/tags/light" }));
            Assert.That(BadgeNames(headRow), Does.Contain("refs/tags/annotated")); Assert.That(panel.Children[1], Is.SameAs(oldRow));
            AssertMessageTooltip(oldRow, old, "light");
            Assert.That(MessageTooltip(headRow).Split('\n'), Does.Contain("annotated"));
            await f.GitAsync("tag", "--delete", "annotated"); references = await f.Repository.GetReferencesAsync();
            Present(headRow, head, references); Pump(window); Assert.That(BadgeNames(headRow), Does.Not.Contain("refs/tags/annotated"));
            Assert.That(MessageTooltip(headRow).Split('\n'), Does.Not.Contain("annotated"));
            await f.GitAsync("tag", "--delete", "light"); references = await f.Repository.GetReferencesAsync();
            Present(oldRow, old, references); Pump(window); Assert.That(BadgeNames(oldRow), Is.Empty);
            AssertMessageTooltip(oldRow, old);
            Assert.That((await f.Repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task LegacyAndCompactGraphsRenderTheTagAtItsCommitAndIncludeItsNameInTheActualTooltip(bool labels)
    {
        using RootFixture f = await RootFixture.CreateAsync(); await f.GitAsync("tag", "-a", "graph-tag", "-m", "Graph tag");
        GitCommit commit = (await f.Repository.GetLogAsync(1)).Single();
        GitReference tag = (await f.Repository.GetReferencesAsync()).Single(r => r.IsTag);
        Assert.That(tag.Hash, Is.Not.EqualTo(commit.Hash));
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            CommitGraph graph = new() { Commits = [commit], References = [tag], ShowReferenceLabels = labels };
            Window window = new() { Width = 360, Height = 80, Content = graph, RequestedThemeVariant = theme }; window.Show();
            try
            {
                Pump(window); Point point = graph.TranslatePoint(new Point(labels ? 20 : 10, 12), window)!.Value;
                Assert.That((window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(graph), Is.True);
                window.MouseMove(point); Pump(window); Assert.That(ToolTip.GetTip(graph)?.ToString(), Does.Contain("graph-tag").And.Contain(commit.Hash));
                using var before = window.CaptureRenderedFrame(); Assert.That(before, Is.Not.Null); byte[] withTag = Pixels(before!);
                Capture(window, $"annotated-reference-graph-{labels}-{theme}.png");
                graph.References = []; Pump(window); window.MouseMove(point + new Vector(1, 0)); Pump(window);
                Assert.That(ToolTip.GetTip(graph)?.ToString(), Does.Not.Contain("graph-tag"));
                using var after = window.CaptureRenderedFrame(); Assert.That(after, Is.Not.Null);
                Assert.That(Pixels(after!), Is.Not.EqualTo(withTag), "Even the compact graph changes its reference marker when the tag is removed.");
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTest]
    public async Task TheRealBrowseRowAndCopyTagsMenuIncludeAnnotatedTagsAndRespectTagVisibilityAfterRefresh()
    {
        using BrowseFixture f = await BrowseFixture.CreateAsync("base\n", "base\n");
        await Git(f, "tag", "-a", "annotated-copy", "-m", "Copy tag"); await f.Window.OpenRepositoryAsync(f.Repository); await Idle(f.Window);
        ListBox history = Field<ListBox>(f.Window, "RevisionHistory"); GitCommit commit = history.Items.OfType<GitCommit>().First(); history.SelectedItem = commit; Pump(f.Window);
        Control row = history.ContainerFromIndex(history.Items.IndexOf(commit))!;
        Assert.That(BadgeNames(row), Does.Contain("refs/tags/annotated-copy"));
        ContextMenu menu = history.ContextMenu!; menu.Open(history); Pump(f.Window);
        MenuItem copy = menu.Items.OfType<MenuItem>().Single(item => item.Name == "RevisionCopy");
        Assert.That(copy.Items.OfType<MenuItem>().Select(item => item.Tag as string), Does.Contain("annotated-copy")); menu.Close();
        object source = history.ItemsSource!; object view = Private<object>(f.Window, "_historyView");
        PropertyInfo showTags = view.GetType().GetProperty("ShowTags")!; bool previous = (bool)showTags.GetValue(view)!;
        try
        {
            showTags.SetValue(view, false); Invoke(f.Window, "ApplyHistoryViewPresentation"); Pump(f.Window);
            Assert.That(BadgeNames(row), Does.Not.Contain("refs/tags/annotated-copy")); Assert.That(history.ItemsSource, Is.SameAs(source));
            showTags.SetValue(view, true); Invoke(f.Window, "ApplyHistoryViewPresentation"); Pump(f.Window);
            Assert.That(BadgeNames(row), Does.Contain("refs/tags/annotated-copy")); Assert.That(history.ItemsSource, Is.SameAs(source));
        }
        finally { showTags.SetValue(view, previous); Invoke(f.Window, "ApplyHistoryViewPresentation"); }
        await Git(f, "tag", "--delete", "annotated-copy"); await f.Window.OpenRepositoryAsync(f.Repository); await Idle(f.Window);
        menu = history.ContextMenu!; menu.Open(history); Pump(f.Window);
        MenuItem refreshedCopy = menu.Items.OfType<MenuItem>().Single(item => item.Name == "RevisionCopy");
        Assert.That(refreshedCopy.Items.OfType<MenuItem>().Select(item => item.Tag as string), Does.Not.Contain("annotated-copy")); menu.Close();
    }

    [AvaloniaTest]
    public async Task ReannotatingATagAtTheSameCommitStillInvalidatesTheOpenQuickReferenceSnapshot()
    {
        using BrowseFixture f = await BrowseFixture.CreateAsync("base\n", "base\n");
        await Git(f, "tag", "light"); await Git(f, "tag", "-a", "annotated", "-m", "Original metadata");
        await f.Window.OpenRepositoryAsync(f.Repository); await Idle(f.Window);
        ListBox history = Field<ListBox>(f.Window, "RevisionHistory"); history.Focus(); Press(f.Window, PhysicalKey.Delete);
        await Until(() => f.Window.OwnedWindows.OfType<QuickGitRefSelectorDialog>().Any());
        QuickGitRefSelectorDialog picker = f.Window.OwnedWindows.OfType<QuickGitRefSelectorDialog>().Single(); Pump(picker);
        ListBox choices = Field<ListBox>(picker, "lbxRefs");
        int index = choices.Items.Cast<object>().ToList().FindIndex(item =>
            (item.GetType().GetProperty("Reference")!.GetValue(item) as GitReference)?.Name == "refs/tags/annotated");
        PushProcessTestDriver.Pointer((Control)choices.ContainerFromIndex(index)!);
        GitReference snapshot = picker.SelectedReference!;
        await Git(f, "tag", "-f", "-a", "annotated", "-m", "Different metadata, same commit");
        GitReference current = (await new GitRepository(f.Repository).GetReferencesAsync()).Single(r => r.Name == snapshot.Name);
        Assert.That(current.RevisionHash, Is.EqualTo(snapshot.RevisionHash)); Assert.That(current.Hash, Is.Not.EqualTo(snapshot.Hash));
        Press(picker, PhysicalKey.Enter); await Idle(f.Window);
        Assert.That(f.Window.OwnedWindows, Is.Empty); Assert.That(f.Window.LastException, Is.Null);
        Assert.That(Private<TextBlock>(f.Window, "_state").Text, Does.Contain("selected reference changed"));
        Assert.That((await Git(f, "rev-parse", "refs/tags/annotated")).StandardOutput.Trim(), Is.EqualTo(current.Hash));
    }

    private static string MessageTooltip(Control row)
        => ToolTip.GetTip(row.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionMessage")) as string ?? "";
    private static void AssertMessageTooltip(Control row, GitCommit commit, params string[] referenceNames)
        => Assert.That(MessageTooltip(row), Is.EqualTo(string.Join('\n', referenceNames.Append(commit.Subject).Append(commit.Body).Where(text => text.Length > 0))));
    private static Border[] Badges(Control row) => row.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "RevisionReference").ToArray();
    private static string[] BadgeNames(Control row) => Badges(row).Select(b => ((GitReference)b.Tag!).Name).ToArray();
    private static void Present(Control row, GitCommit commit, IReadOnlyList<GitReference> references)
        => RevisionHistoryRow.ApplyPresentation(row, new(true, true, true, true, true, false, commit.AuthoredAt, "", "", false, references));
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static T Private<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Invoke(MainWindow window, string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    private static void Press(Window window, PhysicalKey key)
    { window.KeyPressQwerty(key, RawInputModifiers.None); if (window.IsVisible) { window.KeyReleaseQwerty(key, RawInputModifiers.None); } Dispatcher.UIThread.RunJobs(); }
    private static void Pump(TopLevel root)
    { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    { Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True); }
    private static async Task Idle(MainWindow window)
    {
        await Until(() => !window.IsBusy && Private<object>(window, "_quickReferenceRead").GetType().GetField("Current", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(Private<object>(window, "_quickReferenceRead")) is null);
        Pump(window);
    }
    private static async Task<GitResult> Git(BrowseFixture fixture, params string[] args)
    { GitResult result = await fixture.Git.RunAsync(fixture.Repository, args); Assert.That(result.Success, Is.True, result.StandardError); return result; }
    private static byte[] Pixels(Avalonia.Media.Imaging.WriteableBitmap bitmap)
    { using var frame = bitmap.Lock(); byte[] bytes = new byte[frame.RowBytes * frame.Size.Height]; Marshal.Copy(frame.Address, bytes, 0, bytes.Length); return bytes; }
    private static void Capture(TopLevel window, string name)
    {
        Pump(window); using var bitmap = window.CaptureRenderedFrame(); Assert.That(bitmap, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); bitmap!.Save(path); TestContext.AddTestAttachment(path);
    }
}

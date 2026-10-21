using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ExportRevisionPresentationTests
{
    [AvaloniaTest]
    [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
    public async Task NativeSingleAndMultipleListsShareInlineRefsColumnsColorsAndKeepObjectsAndFocus(bool multiple, bool dark)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(kind: "none");
        GitCommit[] commits = Commits(4); GitReference[] references = References(commits[0]);
        HistoryViewPreferences view = new() { RelativeDate = false, FillRefLabels = true, ShowAvatar = false };
        ExportRevisionList list = new("HistoricalRowsId", multiple, view: view);
        Button other = new() { Content = "Other focus target", Height = 30 };
        DockPanel root = new() { LastChildFill = true }; DockPanel.SetDock(other, Dock.Bottom); root.Children.Add(other); root.Children.Add(list);
        Window host = new() { Width = 1100, Height = 360, Content = root, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        IReadOnlyList<GitCommit> ordered = list.SetData(commits, references, commits[0].Hash);
        object source = list.Rows.ItemsSource!;
        list.Rows.SelectedItem = commits[0];
        Dictionary<string, byte[]> material = f.Material();
        try
        {
            host.Show(); Pump(host);
            CommitGraph graph = list.GetVisualDescendants().OfType<CommitGraph>().Single();
            IReadOnlyList<GitCommit> graphInput = graph.Commits;
            Assert.That(list.Rows.Name, Is.EqualTo("HistoricalRowsId"));
            Assert.That(list.Rows.SelectionMode, Is.EqualTo(multiple ? SelectionMode.Multiple : SelectionMode.Single));
            Assert.That(ordered, Is.SameAs(source)); Assert.That(ordered, Is.EqualTo(commits));
            Grid[] originalRows = commits.Select(commit => Row(list.Rows, commit)).ToArray();
            AssertState(originalRows[0], commits[0], true, list.Rows.IsKeyboardFocusWithin, 0, view, dark);
            Border[] badges = originalRows[0].GetVisualDescendants().OfType<Border>().Where(control => control.Name == "RevisionReference").ToArray();
            Assert.That(badges.Select(badge => ((GitReference)badge.Tag!).Name), Is.EqualTo(references.Take(3).Select(reference => reference.Name)));
            Assert.That(originalRows[1].GetVisualDescendants().OfType<Border>().Any(control => control.Name == "RevisionReference"), Is.False,
                "Refs must attach to an exact RevisionHash, including a peeled annotated tag, never by prefix or contains.");
            TextBlock subject = Cell(originalRows[0], "RevisionSubject");
            Assert.That(subject.Text, Is.EqualTo(commits[0].Subject));
            Assert.That(badges.All(badge => badge.TranslatePoint(default, originalRows[0])!.Value.X + badge.Bounds.Width
                <= subject.TranslatePoint(default, originalRows[0])!.Value.X + .5), Is.True);
            Grid headers = list.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "RevisionColumnHeaders");
            Assert.That(headers.ColumnDefinitions.Skip(2).Select(column => column.ActualWidth),
                Is.EqualTo(originalRows[0].ColumnDefinitions.Skip(1).Select(column => column.ActualWidth)));
            Assert.That(graph.ShowReferenceLabels, Is.False, "References have one inline renderer, not a second clipped label in the graph gutter.");
            Pointer(host, subject); Assert.That(list.Rows.IsKeyboardFocusWithin, Is.True);
            AssertState(originalRows[0], commits[0], true, true, 0, view, dark);
            Pointer(host, Cell(originalRows[1], "RevisionSubject"), multiple ? RawInputModifiers.Control : RawInputModifiers.None);
            if (multiple) { Assert.That(list.Rows.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EquivalentTo(new[] { commits[0].Hash, commits[1].Hash })); }
            else { Assert.That(list.Rows.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(new[] { commits[1].Hash })); }
            Assert.That(list.Rows.IsKeyboardFocusWithin, Is.True);
            Assert.That(graph.SelectedHashes, Is.EquivalentTo(list.Rows.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash)));
            if (!multiple)
            {
                Press(host, PhysicalKey.ArrowDown);
                Assert.That(list.Rows.SelectedItem, Is.SameAs(commits[2]), "The first physical arrow after a click navigates the native list.");
                Assert.That(list.Rows.IsKeyboardFocusWithin, Is.True);
            }
            Pointer(host, other);
            Assert.That(list.Rows.IsKeyboardFocusWithin, Is.False); Assert.That(graph.SelectionFocused, Is.False);
            Assert.That(list.Rows.ItemsSource, Is.SameAs(source)); Assert.That(graph.Commits, Is.SameAs(graphInput));
            for (int i = 0; i < commits.Length; i++)
            {
                Assert.That(list.Rows.Items[i], Is.SameAs(commits[i])); Assert.That(Row(list.Rows, commits[i]), Is.SameAs(originalRows[i]));
            }
            f.AssertMaterial(material);
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task CapturedColumnVisibilityUsesTheSameWidthsForRowsAndHeaders(bool avatarOnly)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(kind: "none");
        HistoryViewPreferences view = new() { ShowGraph = false, ShowAuthor = false, ShowAvatar = avatarOnly,
            ShowDate = false, ShowId = false, ShowNotesColumn = true, ShowBody = false };
        ExportRevisionList list = new("FormatRevisions", true, view: view); GitCommit[] commits = Commits(2); list.SetData(commits, [], commits[0].Hash);
        Window host = new() { Width = 900, Height = 220, Content = list };
        try
        {
            host.Show(); Pump(host); Grid row = Row(list.Rows, commits[0]);
            Grid headers = list.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "RevisionColumnHeaders");
            Assert.That(row.ColumnDefinitions.Skip(1).Select(column => column.ActualWidth), Is.EqualTo(new[] { avatarOnly ? 20d : 0d, 0d, 0d, 220d }));
            Assert.That(headers.ColumnDefinitions.Skip(2).Select(column => column.ActualWidth), Is.EqualTo(row.ColumnDefinitions.Skip(1).Select(column => column.ActualWidth)));
            Assert.That(headers.ColumnDefinitions[0].ActualWidth, Is.Zero);
            Assert.That(list.GetVisualDescendants().OfType<CommitGraph>().Single().IsVisible, Is.False);
            Assert.That(Cell(row, "RevisionMessageDetails").Text, Is.Empty);
            Assert.That(Cell(row, "RevisionNotes").Text, Is.Empty, "The renderer cannot invent notes that its owner has not loaded.");
            Assert.That(list.Rows.SelectionMode, Is.EqualTo(SelectionMode.Multiple));
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ArtificialAndRealRowsShareTheGraphGutterAndFirstArrowsWhenTheGraphIsHiddenOrVisible(bool showGraph)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(kind: "none");
        HistoryViewPreferences view = new() { ShowGraph = showGraph, ShowAvatar = false };
        GitCommit[] commits = Commits(2);
        ChooseCommitSelectionPresenter presenter = new("ChooseRows", true, text => text, view);
        presenter.SetData(commits, [], commits[0].Hash, artificialAllowed: true);
        object source = presenter.Rows.ItemsSource!;
        Window host = new() { Width = 1000, Height = 280, Content = presenter };
        try
        {
            host.Show(); Pump(host);
            Grid prefix = presenter.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "ChooseWorkingRevisions");
            Grid headers = presenter.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "RevisionColumnHeaders");
            Assert.That(prefix.IsEffectivelyVisible, Is.True);
            Assert.That(prefix.ColumnDefinitions[0].ActualWidth, Is.EqualTo(headers.ColumnDefinitions[0].ActualWidth));
            Assert.That(prefix.ColumnDefinitions[0].ActualWidth, Is.EqualTo(showGraph ? 90 : 0));
            Assert.That(prefix.Children.OfType<Canvas>().Select(canvas => canvas.IsVisible), Is.EqualTo(new[] { showGraph, showGraph }));
            Button working = prefix.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ChooseWorkingDirectoryRevision");
            Pointer(host, working);
            Assert.That(presenter.Selection!.Revision.Kind, Is.EqualTo(GitCompareRevisionKind.WorkingTree));
            Press(host, PhysicalKey.ArrowDown);
            Assert.That(presenter.Selection!.Revision.Kind, Is.EqualTo(GitCompareRevisionKind.Index));
            Press(host, PhysicalKey.ArrowDown);
            Assert.That(presenter.Selection!.Commit, Is.SameAs(commits[0]));
            Assert.That(presenter.Rows.IsKeyboardFocusWithin, Is.True);
            Assert.That(presenter.Rows.ItemsSource, Is.SameAs(source));
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public async Task PresenterUsesTheSuppliedSnapshotAndFiltersOnlyItsInlineReferencePresentation()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(kind: "none");
        GitCommit[] commits = Commits(2); GitReference[] refs = References(commits[0]);
        HistoryViewPreferences view = new() { ShowTags = false, ShowRemoteBranches = false, ShowAvatar = false };
        ChooseCommitSelectionPresenter presenter = new("ChooseRevision", false, text => text, view);
        presenter.SetData(commits, refs, commits[0].Hash, artificialAllowed: false);
        Window host = new() { Width = 1000, Height = 240, Content = presenter };
        HistoryViewPreferencesStore store = new(); store.Save(view with { ShowTags = true, ShowRemoteBranches = true });
        byte[] saved = File.ReadAllBytes(store.FilePath);
        try
        {
            host.Show(); presenter.Select(new(commits[0].Hash)); Pump(host);
            Border[] badges = Row(presenter.Rows, commits[0]).GetVisualDescendants().OfType<Border>().Where(control => control.Name == "RevisionReference").ToArray();
            Assert.That(badges.Select(badge => ((GitReference)badge.Tag!).Name), Is.EqualTo(new[] { refs[0].Name }));
            Assert.That(presenter.Selection!.Commit, Is.SameAs(commits[0]));
            Assert.That(presenter.Rows.Items.Cast<GitCommit>(), Is.EqualTo(commits));
            Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(saved), "Opening/selecting must not save a local View or migrate the profile.");
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public void PureSharedPresentationUsesProvidedMetadataAndPreservesTheExistingBrowseRules()
    {
        GitCommit commit = Commits(1)[0]; DateTimeOffset committerDate = commit.AuthoredAt.AddDays(3);
        HistoryViewPreferences view = new() { ShowAuthorDate = false, RelativeDate = false, ShowNotes = true, ShowBody = true };
        Grid row = (Grid)RevisionHistoryRow.Create(commit, []);
        RevisionHistoryPresentation.Apply(row, commit, view, [], (committerDate, "first note\nsecond note"), translate: value => value == "Notes" ? "Notes FR" : value);
        Assert.That(Cell(row, "RevisionDate").Text, Is.EqualTo(committerDate.LocalDateTime.ToString("G", CultureInfo.CurrentCulture)));
        Assert.That(Cell(row, "RevisionMessageDetails").Text, Is.EqualTo("  body 0 continuation  [Notes FR] first note second note"));
        view = view with { ShowNotesColumn = true };
        RevisionHistoryPresentation.Apply(row, commit, view, [], (committerDate, "first note\nsecond note"));
        Assert.That(Cell(row, "RevisionMessageDetails").Text, Is.EqualTo("  body 0 continuation"));
        Assert.That(Cell(row, "RevisionNotes").Text, Is.EqualTo("first note second note"));
        Assert.That(row.ColumnDefinitions[4].Width.Value, Is.EqualTo(220));
    }

    [AvaloniaTest]
    public async Task AnOffscreenSelectionReceivesTheSameColorsOnFirstRealizationAndTheFirstArrowKeepsTheSource()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(kind: "none");
        HistoryViewPreferences view = new() { ShowAvatar = false }; GitCommit[] commits = Commits(80);
        ExportRevisionList list = new("ChooseRows", false, view: view); list.SetData(commits, [], commits[0].Hash);
        object source = list.Rows.ItemsSource!; list.Rows.SelectedItem = commits[^1];
        Window host = new() { Width = 1000, Height = 200, Content = list, RequestedThemeVariant = ThemeVariant.Light };
        try
        {
            host.Show(); list.Rows.ScrollIntoView(commits[^1]); Pump(host);
            Grid row = Row(list.Rows, commits[^1]); AssertState(row, commits[^1], true, list.Rows.IsKeyboardFocusWithin, 79, view, false);
            Pointer(host, Cell(row, "RevisionSubject")); Press(host, PhysicalKey.ArrowUp);
            Assert.That(list.Rows.SelectedItem, Is.SameAs(commits[^2])); Assert.That(list.Rows.IsKeyboardFocusWithin, Is.True);
            Assert.That(list.Rows.ItemsSource, Is.SameAs(source));
        }
        finally { host.Close(); }
    }

    private static GitCommit[] Commits(int count) => Enumerable.Range(0, count).Select(index => new GitCommit((index + 1).ToString("x40", CultureInfo.InvariantCulture),
        index + 1 < count ? [(index + 2).ToString("x40", CultureInfo.InvariantCulture)] : [],
        index % 2 == 0 ? "One author" : "Other author", index % 2 == 0 ? "one@example.invalid" : "other@example.invalid",
        DateTimeOffset.UnixEpoch.AddDays(count - index), "subject " + index, "body " + index + "\ncontinuation")).ToArray();
    private static GitReference[] References(GitCommit commit) =>
    [new("refs/heads/main", commit.Hash, null, true), new("refs/remotes/origin/main", commit.Hash, null, false),
     new("refs/tags/annotated", "tag-object-identity", null, false) { RevisionHash = commit.Hash },
     new("refs/heads/prefix-collision", commit.Hash + "f", null, false)];
    private static Grid Row(ListBox rows, GitCommit commit) => rows.GetVisualDescendants().OfType<ListBoxItem>()
        .Single(item => ReferenceEquals(item.DataContext, commit)).GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "RevisionHistoryRow");
    private static TextBlock Cell(Control row, string name) => row.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == name);
    private static void AssertState(Grid row, GitCommit commit, bool selected, bool focused, int index, HistoryViewPreferences view, bool dark)
        => Assert.That(row.Background!.ToString(), Is.EqualTo(RevisionHistoryRow.BackgroundFor(commit, selected, focused,
            view.HighlightAuthoredRevisions ? commit.AuthorEmail : null, index, view.DrawAlternateBackground, dark).ToString()));
    private static void Pump(Window host) { host.UpdateLayout(); Dispatcher.UIThread.RunJobs(); host.UpdateLayout(); }
    private static void Pointer(Window host, Control control, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Pump(host); Point point = control.TranslatePoint(default, host)!.Value + new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        Assert.That(new Rect(default, host.ClientSize).Contains(point), Is.True, control.Name ?? control.GetType().Name);
        host.MouseMove(point); host.MouseDown(point, MouseButton.Left, modifiers); host.MouseUp(point, MouseButton.Left, modifiers); Pump(host);
    }
    private static void Press(Window host, PhysicalKey key)
    { host.KeyPressQwerty(key, RawInputModifiers.None); host.KeyReleaseQwerty(key, RawInputModifiers.None); Pump(host); }
}

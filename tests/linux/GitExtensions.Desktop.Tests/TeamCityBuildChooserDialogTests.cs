using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class TeamCityBuildChooserDialogTests
{
    [AvaloniaTest]
    [TestCase("en-US", false)] [TestCase("fr-FR", true)]
    public async Task OriginalCompactChromeKeepsDesignerGeometryReadableButtonsAndNativeLinkTint(string language, bool dark)
    {
        await using TeamCityChooserTestFixture f = new(language, dark);
        f.Show(new("App", "A_build")); await f.ReadyAsync();
        Assert.That(f.Dialog.Width, Is.EqualTo(451)); Assert.That(f.Dialog.Height, Is.EqualTo(379));
        Assert.That(f.Dialog.Owner, Is.SameAs(f.Owner)); Assert.That(f.Dialog.ShowInTaskbar || f.Dialog.CanMaximize || f.Dialog.CanMinimize, Is.False);
        Assert.That(f.Bounds(f.Field<Border>("TeamCityProjectsFrame")), Is.EqualTo(new Rect(9, 10, 434, 323)));
        Assert.That(f.Bounds(f.Field<Button>("buttonCancel")), Is.EqualTo(new Rect(121, 346, 56, 19)));
        Assert.That(f.Bounds(f.Field<Button>("buttonOK")), Is.EqualTo(new Rect(274, 346, 56, 19)));
        Assert.That(f.Field<Button>("buttonOK").IsDefault, Is.False, "The source has no AcceptButton.");
        Assert.That(f.Field<Button>("buttonCancel").IsCancel, Is.True);
        Assert.That(f.Dialog.Title, Is.EqualTo(f.Translations.Translate("Choose the TeamCity build...")));
        Assert.That(f.Field<Button>("buttonCancel").Content, Is.EqualTo(f.Translations.Translate("Cancel")));
        if (language == "fr-FR") { Assert.That(f.Field<Button>("buttonCancel").Content, Is.EqualTo("Annuler")); }
        foreach (string name in new[] { "buttonCancel", "buttonOK" })
        {
            Button button = f.Field<Button>(name);
            foreach (TextBlock text in button.GetVisualDescendants().OfType<TextBlock>())
            {
                Point origin = text.TranslatePoint(default, button)!.Value;
                Assert.That(origin.X, Is.GreaterThanOrEqualTo(-.5), name);
                Assert.That(origin.X + text.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(button.Bounds.Width + .5), name + " full caption");
                Assert.That(origin.Y + text.TextLayout.Height, Is.LessThanOrEqualTo(button.Bounds.Height + .5), name + " full caption height");
            }
        }
        Assert.That(f.Tree.GetVisualDescendants().OfType<Image>(), Is.Empty, "Windows has no ImageList in this chooser.");
        Assert.That(((Control)f.Tree.Parent!).GetVisualDescendants().OfType<ClassicTreeConnectors>().Any(), Is.True);
        Assert.That(f.Header(f.Build("A_build")).Bounds.Height, Is.EqualTo(ClassicTreeViewAppearance.RowHeight).Within(.5));
        AssertBrush(f, f.Build("Z_build"), "ClassicHighlightBrush"); AssertBrush(f, f.Build("A_build"), "ClassicHighlightTextBrush");
        Assert.That((((TextBlock)f.Project("App").Header!).Foreground as ISolidColorBrush)?.Color,
            Is.EqualTo(Color.Parse(ClassicStyles.OriginalColors(dark)["WindowText"])), "Project labels stay readable in both palettes.");
        f.Capture("teamcity-build-chooser-" + language + (dark ? "-dark.png" : "-light.png"));
        f.Dialog.Width = 560; f.Dialog.Height = 459; f.Pump();
        Assert.That(f.Bounds(f.Field<Button>("buttonCancel")).X, Is.EqualTo(121));
        Assert.That(f.Bounds(f.Field<Button>("buttonOK")).Right, Is.EqualTo(560 - 121));
        Assert.That(f.Bounds(f.Field<Button>("buttonOK")).Y, Is.EqualTo(426));
        Assert.That(f.Bounds(f.Field<Border>("TeamCityProjectsFrame")).Size, Is.EqualTo(new Size(543, 403)));
        Assert.That(f.Client.RuntimeReads + f.Client.SingleBuildReads + f.Client.DisposeCalls, Is.Zero);
    }

    [AvaloniaTest]
    public async Task RootOpensButOtherBuildsStayLazyAndSubprojectsPrecedeBuildsSortedByIdRatherThanDisplayName()
    {
        await using TeamCityChooserTestFixture f = new(); f.Show(); await f.ReadyAsync();
        Assert.That(f.Client.ProjectReads, Is.EqualTo(1)); Assert.That(f.Client.BuildReads.Select(read => read.ProjectId), Is.EqualTo(new[] { "_Root" }));
        Assert.That(f.Project("_Root").IsExpanded, Is.True);
        Assert.That(f.Project("_Root").Items.OfType<TreeViewItem>().Select(TeamCityChooserTestFixture.Label), Is.EqualTo(new[] { "Application", "Zulu" }));
        Assert.That(TeamCityChooserTestFixture.Nodes(f.Tree.Items.OfType<TreeViewItem>()).Any(node => node.Tag is TeamCityProject { Archived: true }), Is.False);
        Assert.That(f.Project("Zulu").Items.OfType<TreeViewItem>().Single().Tag, Is.Null, "A lazy leaf must retain its expansion chevron.");
        f.Expand("App"); await f.ReadyAsync();
        Assert.That(f.Project("App").Items.OfType<TreeViewItem>().Select(TeamCityChooserTestFixture.Label), Is.EqualTo(new[]
        { "Alpha child", "Zeta child", "Zulu display name (A_build)", "Alpha display name (Z_build)" }));
        Assert.That(f.Client.BuildReads.Select(read => read.ProjectId), Is.EqualTo(new[] { "_Root", "App" }));
        f.Collapse("App"); f.Expand("App"); await f.ReadyAsync();
        Assert.That(f.Client.BuildReads.Count(read => read.ProjectId == "App"), Is.EqualTo(1), "Successful build reads are cached like Project.Builds.");
        f.Expand("Alpha"); await f.ReadyAsync(); Assert.That(f.Build("Nested_build").Tag, Is.EqualTo(new TeamCityBuildType("Nested_build", "Nested", "Alpha")));
        Assert.That(f.Client.RuntimeReads + f.Client.SingleBuildReads, Is.Zero);
    }

    [AvaloniaTest]
    [TestCase("A_build", true)] [TestCase("missing", false)]
    public async Task RestoringProjectExpandsAncestorsAndSelectsExactBuildOrProjectWithoutAccepting(string buildId, bool exists)
    {
        await using TeamCityChooserTestFixture f = new(); TeamCityBuildSelection initial = new("App", buildId);
        f.Show(initial); await f.ReadyAsync();
        Assert.That(f.Project("_Root").IsExpanded && f.Project("App").IsExpanded, Is.True);
        Assert.That(f.Tree.SelectedItem, Is.SameAs(exists ? f.Build(buildId) : f.Project("App")));
        Assert.That(f.Field<Button>("buttonOK").IsEnabled, Is.EqualTo(exists));
        Assert.That(f.Dialog.Result, Is.Null); Assert.That(initial, Is.EqualTo(new TeamCityBuildSelection("App", buildId)));
        Assert.That(f.Tree.IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Client.SingleBuildReads, Is.Zero, "A vanished build must not be restored from another project.");
    }

    [AvaloniaTest]
    public async Task UnknownSavedProjectDoesNotInventASelectedBuild()
    {
        await using TeamCityChooserTestFixture f = new(); f.Show(new("gone", "A_build")); await f.ReadyAsync();
        Assert.That(f.Field<Button>("buttonOK").IsEnabled, Is.False); Assert.That(f.Dialog.Result, Is.Null);
        Assert.That(f.Client.BuildReads.Select(read => read.ProjectId), Is.EqualTo(new[] { "_Root" }));
    }

    [AvaloniaTest]
    public async Task OneRealBuildClickEnablesOkTreeEnterDoesNotSubmitAndTabEnterOnOkDoes()
    {
        await using TeamCityChooserTestFixture f = new(); f.Show(new("App", "missing")); await f.ReadyAsync();
        f.RowClick(f.Build("Z_build")); Assert.That(f.Tree.SelectedItem, Is.SameAs(f.Build("Z_build")));
        Assert.That(f.Field<Button>("buttonOK").IsEnabled, Is.True);
        f.Press(PhysicalKey.Enter); Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Dialog.Result, Is.Null);
        for (int tab = 0; tab < 2 && !f.Field<Button>("buttonOK").IsKeyboardFocusWithin; tab++) { f.Press(PhysicalKey.Tab); }
        Assert.That(f.Field<Button>("buttonOK").IsKeyboardFocusWithin, Is.True);
        f.Press(PhysicalKey.Enter);
        Assert.That(await f.ClosedAsync(), Is.EqualTo(new TeamCityBuildSelection("App", "Z_build")));
        Assert.That(f.Dialog.Result, Is.EqualTo(new TeamCityBuildSelection("App", "Z_build")));
        Assert.That(f.Client.DisposeCalls, Is.Zero, "Only the owner disposes the borrowed client after draining.");
    }

    [AvaloniaTest]
    public async Task DoubleClickingAProjectOnlyExpandsAndDoubleClickingARealBuildAcceptsItsParentIdentity()
    {
        await using TeamCityChooserTestFixture f = new(); f.Show(); await f.ReadyAsync();
        f.DoubleClick(f.Project("App")); await f.ReadyAsync();
        Assert.That(f.Project("App").IsExpanded, Is.True); Assert.That(f.Dialog.IsVisible, Is.True);
        Assert.That(f.Field<Button>("buttonOK").IsEnabled, Is.False); Assert.That(f.Dialog.Result, Is.Null);
        f.DoubleClick(f.Build("A_build"));
        Assert.That(await f.ClosedAsync(), Is.EqualTo(new TeamCityBuildSelection("App", "A_build")));
    }

    [AvaloniaTest]
    public async Task ArrowsKeepNativeTreeFocusAndSelectingAProjectDisablesOkAgain()
    {
        await using TeamCityChooserTestFixture f = new(); f.Show(new("App", "A_build")); await f.ReadyAsync();
        f.RowClick(f.Build("A_build")); f.Press(PhysicalKey.ArrowDown);
        Assert.That(f.Tree.SelectedItem, Is.SameAs(f.Build("Z_build"))); Assert.That(f.Tree.IsKeyboardFocusWithin, Is.True);
        f.Press(PhysicalKey.ArrowUp); Assert.That(f.Tree.SelectedItem, Is.SameAs(f.Build("A_build"))); Assert.That(f.Tree.IsKeyboardFocusWithin, Is.True);
        f.RowClick(f.Project("App")); Assert.That(f.Field<Button>("buttonOK").IsEnabled, Is.False);
        f.Press(PhysicalKey.Enter); Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Dialog.Result, Is.Null);
    }

    [AvaloniaTest]
    [TestCase("Cancel")] [TestCase("Escape")] [TestCase("WindowClose")]
    public async Task CancelEscapeAndWindowCloseDiscardSelectionWithoutChangingTheSuppliedIds(string method)
    {
        await using TeamCityChooserTestFixture f = new(); TeamCityBuildSelection initial = new("App", "A_build");
        f.Show(initial); await f.ReadyAsync(); f.RowClick(f.Build("Z_build"));
        if (method == "Cancel") { f.Click("buttonCancel"); }
        else if (method == "Escape") { f.Press(PhysicalKey.Escape); }
        else { f.Dialog.Close(); }
        Assert.That(await f.ClosedAsync(), Is.Null); Assert.That(f.Dialog.Result, Is.Null);
        Assert.That(initial, Is.EqualTo(new TeamCityBuildSelection("App", "A_build")));
        Assert.That(f.Client.DisposeCalls + f.Client.RuntimeReads, Is.Zero);
    }

    [AvaloniaTest]
    public async Task ADelayedRestorationNeverOverridesANewerSelectionOrFocusOnCancel()
    {
        await using TeamCityChooserTestFixture f = new(); var gate = f.Gate<TeamCityPage<TeamCityBuildType>>();
        f.Client.BuildsRead = (id, _) => id == "App" ? gate.Task : Task.FromResult(new TeamCityPage<TeamCityBuildType>(TeamCityChooserTestClient.Builds(id)));
        f.Show(new("App", "A_build")); await f.UntilAsync(() => f.Client.BuildReads.Any(read => read.ProjectId == "App"));
        f.RowClick(f.Project("Zulu")); f.Press(PhysicalKey.Tab);
        Assert.That(f.Field<Button>("buttonCancel").IsKeyboardFocusWithin, Is.True);
        gate.SetResult(new(TeamCityChooserTestClient.Builds("App"))); await f.ReadyAsync();
        Assert.That(f.Tree.SelectedItem, Is.SameAs(f.Project("Zulu")));
        Assert.That(f.Field<Button>("buttonCancel").IsKeyboardFocusWithin, Is.True); Assert.That(f.Dialog.Result, Is.Null);
    }

    [AvaloniaTest]
    public async Task CollapseAndReexpandCancelOldReadAndOnlyTheCurrentGenerationAddsBuildsWithoutMovingFocus()
    {
        await using TeamCityChooserTestFixture f = new(); var first = f.Gate<TeamCityPage<TeamCityBuildType>>(); var second = f.Gate<TeamCityPage<TeamCityBuildType>>();
        int requests = 0;
        f.Client.BuildsRead = (id, _) => id == "App" ? ++requests == 1 ? first.Task : second.Task
            : Task.FromResult(new TeamCityPage<TeamCityBuildType>([]));
        f.Show(); await f.ReadyAsync(); f.Expand("App"); await f.UntilAsync(() => requests == 1);
        CancellationToken old = f.Client.BuildReads.Single(read => read.ProjectId == "App").Token;
        f.Collapse("App"); Assert.That(old.IsCancellationRequested, Is.True); f.Expand("App"); await f.UntilAsync(() => requests == 2);
        first.SetResult(new([new("stale", "Old ignored build", "App")])); await f.UntilAsync(() => first.Task.IsCompleted);
        Assert.That(f.Project("App").Items.OfType<TreeViewItem>().Any(node => node.Tag is TeamCityBuildType), Is.False);
        second.SetResult(new([new("current", "Current build", "App")])); await f.ReadyAsync();
        Assert.That(f.Project("App").Items.OfType<TreeViewItem>().Where(node => node.Tag is TeamCityBuildType).Select(node => ((TeamCityBuildType)node.Tag!).Id), Is.EqualTo(new[] { "current" }));
        Assert.That(f.Tree.SelectedItem, Is.SameAs(f.Project("App"))); Assert.That(f.Project("App").IsKeyboardFocusWithin, Is.True);
        f.Click("buttonCancel"); Assert.That(await f.ClosedAsync(), Is.Null);
    }

    [AvaloniaTest]
    [TestCase("Cancel")] [TestCase("WindowClose")] [TestCase("OwnerClose")]
    public async Task ClosingDuringAnUncooperativeInitialReadCancelsButWaitsForItsRealCompletion(string method)
    {
        await using TeamCityChooserTestFixture f = new(); var gate = f.Gate<TeamCityPage<TeamCityProject>>(); CancellationToken request = default;
        f.Client.ProjectsRead = token => { request = token; return gate.Task; }; f.Show();
        await f.UntilAsync(() => f.Client.ProjectReads == 1);
        if (method == "Cancel") { f.Click("buttonCancel"); } else if (method == "OwnerClose") { f.Owner.Close(); } else { f.Dialog.Close(); }
        f.Pump(); Assert.That(request.IsCancellationRequested, Is.True); Assert.That(f.Modal.IsCompleted, Is.False);
        Assert.That(f.Dialog.Completion.IsCompleted, Is.False); Assert.That(f.Dialog.IsVisible, Is.True);
        gate.SetResult(new(TeamCityChooserTestClient.Projects)); Assert.That(await f.ClosedAsync(), Is.Null);
        Assert.That(f.Tree.Items.Count, Is.Zero, "Closing must suppress a late initial publication.");
        Assert.That(f.Client.DisposeCalls, Is.Zero);
        if (method == "OwnerClose") { await f.UntilAsync(() => !f.Owner.IsVisible); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ClosingDrainsBothCurrentAndAlreadyCancelledBuildReads(bool collapseFirst)
    {
        await using TeamCityChooserTestFixture f = new(); var gate = f.Gate<TeamCityPage<TeamCityBuildType>>();
        f.Client.BuildsRead = (id, _) => id == "App" ? gate.Task : Task.FromResult(new TeamCityPage<TeamCityBuildType>([]));
        f.Show(); await f.ReadyAsync(); f.Expand("App"); await f.UntilAsync(() => f.Client.BuildReads.Any(read => read.ProjectId == "App"));
        CancellationToken token = f.Client.BuildReads.Single(read => read.ProjectId == "App").Token;
        if (collapseFirst) { f.Collapse("App"); }
        f.Click("buttonCancel"); Assert.That(token.IsCancellationRequested, Is.True); Assert.That(f.Modal.IsCompleted, Is.False);
        Assert.That(f.Dialog.Completion.IsCompleted, Is.False);
        gate.SetResult(new(TeamCityChooserTestClient.Builds("App"))); Assert.That(await f.ClosedAsync(), Is.Null);
        Assert.That(f.Project("App").Items.OfType<TreeViewItem>().Any(node => node.Tag is TeamCityBuildType), Is.False);
    }

    [AvaloniaTest]
    public async Task AcceptingABuildAlsoDrainsAnUnrelatedReadAndDoesNotDisposeTheBorrowedClient()
    {
        await using TeamCityChooserTestFixture f = new(); var gate = f.Gate<TeamCityPage<TeamCityBuildType>>();
        f.Client.BuildsRead = (id, _) => id == "Zulu" ? gate.Task : Task.FromResult(new TeamCityPage<TeamCityBuildType>(TeamCityChooserTestClient.Builds(id)));
        f.Show(new("App", "A_build")); await f.ReadyAsync(); f.Expand("Zulu");
        await f.UntilAsync(() => f.Client.BuildReads.Any(read => read.ProjectId == "Zulu"));
        f.RowClick(f.Build("Z_build")); f.Click("buttonOK");
        Assert.That(f.Dialog.Result, Is.EqualTo(new TeamCityBuildSelection("App", "Z_build"))); Assert.That(f.Modal.IsCompleted, Is.False);
        Assert.That(f.Client.BuildReads.Single(read => read.ProjectId == "Zulu").Token.IsCancellationRequested, Is.True);
        gate.SetResult(new(TeamCityChooserTestClient.Builds("Zulu")));
        Assert.That(await f.ClosedAsync(), Is.EqualTo(new TeamCityBuildSelection("App", "Z_build"))); Assert.That(f.Client.DisposeCalls, Is.Zero);
    }

    [AvaloniaTest]
    public async Task BuildReadFailureIsVisibleAndCollapseReexpandRetriesWithoutLosingTheSelectedProject()
    {
        await using TeamCityChooserTestFixture f = new(); int calls = 0;
        f.Client.BuildsRead = (id, _) => id == "App" && ++calls == 1
            ? Task.FromException<TeamCityPage<TeamCityBuildType>>(new IOException("TeamCity test failure"))
            : Task.FromResult(new TeamCityPage<TeamCityBuildType>(TeamCityChooserTestClient.Builds(id)));
        f.Show(); await f.ReadyAsync(); f.Expand("App"); await f.ReadyAsync();
        Assert.That(f.Dialog.LastError, Is.TypeOf<IOException>()); Assert.That(f.Field<TextBlock>("TeamCityChooserNotice").Text, Does.Contain("TeamCity test failure"));
        Assert.That(f.Tree.SelectedItem, Is.SameAs(f.Project("App"))); Assert.That(f.Field<Button>("buttonOK").IsEnabled, Is.False);
        f.Collapse("App"); f.Expand("App"); await f.ReadyAsync();
        Assert.That(f.Build("A_build").Tag, Is.TypeOf<TeamCityBuildType>()); Assert.That(f.Field<TextBlock>("TeamCityChooserNotice").IsVisible, Is.False);
        Assert.That(f.Tree.SelectedItem, Is.SameAs(f.Project("App"))); Assert.That(f.Tree.IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    [TestCase("duplicate")] [TestCase("cycle")] [TestCase("orphan")]
    public async Task MalformedProjectHierarchyIsAnObservableFailureRatherThanAnIncompleteOrRecursiveTree(string kind)
    {
        await using TeamCityChooserTestFixture f = new();
        TeamCityProject[] projects = kind switch
        {
            "duplicate" => [new("p", "One", null), new("p", "Two", null)],
            "cycle" => [new("p", "One", "q"), new("q", "Two", "p")], _ => [new("p", "One", "missing")]
        };
        f.Client.ProjectsRead = _ => Task.FromResult(new TeamCityPage<TeamCityProject>(projects)); f.Show(); await f.ReadyAsync();
        Assert.That(f.Dialog.LastError, Is.TypeOf<InvalidDataException>()); Assert.That(f.Field<TextBlock>("TeamCityChooserNotice").IsVisible, Is.True);
        Assert.That(f.Tree.Items.Count, Is.Zero); Assert.That(f.Field<Button>("buttonOK").IsEnabled, Is.False); Assert.That(f.Client.BuildReads, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ForeignOrDuplicateBuildIdentityIsNeverSelectable(bool duplicate)
    {
        await using TeamCityChooserTestFixture f = new();
        f.Client.BuildsRead = (id, _) => Task.FromResult(new TeamCityPage<TeamCityBuildType>(id != "App" ? [] : duplicate
            ? [new("same", "One", "App"), new("same", "Two", "App")] : [new("foreign", "One", "Zulu")]));
        f.Show(new("App", "foreign")); await f.ReadyAsync();
        Assert.That(f.Dialog.LastError, Is.TypeOf<InvalidDataException>()); Assert.That(f.Field<TextBlock>("TeamCityChooserNotice").IsVisible, Is.True);
        Assert.That(f.Project("App").Items.OfType<TreeViewItem>().Any(node => node.Tag is TeamCityBuildType), Is.False);
        Assert.That(f.Field<Button>("buttonOK").IsEnabled, Is.False); Assert.That(f.Dialog.Result, Is.Null);
    }

    [AvaloniaTest]
    public async Task PaginationLimitsAndDiagnosticsAreVisibleWithoutInventingOrBlockingReturnedBuilds()
    {
        await using TeamCityChooserTestFixture f = new();
        f.Client.ProjectsRead = _ => Task.FromResult(new TeamCityPage<TeamCityProject>(TeamCityChooserTestClient.Projects, true, ["Projects were limited."]));
        f.Client.BuildsRead = (id, _) => Task.FromResult(new TeamCityPage<TeamCityBuildType>(TeamCityChooserTestClient.Builds(id), id == "App", id == "App" ? ["Builds were limited."] : []));
        f.Show(new("App", "A_build")); await f.ReadyAsync();
        string? notice = f.Field<TextBlock>("TeamCityChooserNotice").Text;
        Assert.That(notice, Does.Contain("incomplete").And.Contain("Projects were limited.").And.Contain("Builds were limited."));
        Assert.That(f.Field<Button>("buttonOK").IsEnabled, Is.True); f.Click("buttonOK");
        Assert.That(await f.ClosedAsync(), Is.EqualTo(new TeamCityBuildSelection("App", "A_build")));
    }

    [AvaloniaTest]
    public async Task InitialRequestFailureKeepsTheOwnedWindowCancelableAndDoesNotFallBackToRuntimePolling()
    {
        await using TeamCityChooserTestFixture f = new();
        f.Client.ProjectsRead = _ => Task.FromException<TeamCityPage<TeamCityProject>>(new IOException("Projects unavailable"));
        f.Show(); await f.ReadyAsync(); Assert.That(f.Dialog.LastError, Is.TypeOf<IOException>());
        Assert.That(f.Field<TextBlock>("TeamCityChooserNotice").Text, Is.EqualTo("Projects unavailable"));
        Assert.That(f.Field<Button>("buttonCancel").IsEnabled, Is.True); f.Click("buttonCancel");
        Assert.That(await f.ClosedAsync(), Is.Null); Assert.That(f.Client.RuntimeReads + f.Client.SingleBuildReads, Is.Zero);
    }

    private static void AssertBrush(TeamCityChooserTestFixture f, TreeViewItem item, string name)
    {
        bool dark = item.ActualThemeVariant == ThemeVariant.Dark;
        string color = name == "ClassicHighlightBrush" ? "Highlight" : dark ? "WindowText" : "HighlightText";
        object resource = Brush.Parse(ClassicStyles.OriginalColors(dark)[color]);
        Assert.That(((TextBlock)item.Header!).Foreground, Is.InstanceOf<ISolidColorBrush>());
        Assert.That((((TextBlock)item.Header!).Foreground as ISolidColorBrush)?.Color, Is.EqualTo((resource as ISolidColorBrush)?.Color));
    }
}

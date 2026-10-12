using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class BranchHistoryFilterTests
{
    private string? _optionalLocks;
    [SetUp] public void ReadOnlyGit() { _optionalLocks = Environment.GetEnvironmentVariable("GIT_OPTIONAL_LOCKS"); Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0"); }
    [TearDown] public void RestoreGit() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", _optionalLocks);

    [AvaloniaTest]
    public async Task LazyEditableSuggestionsUseOriginalNamesScopesContainsAndNoHundredItemLimit()
    {
        using Fixture f = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
        for (int i = 0; i < 103; i++) { await Git(f, "update-ref", "refs/heads/prefix-middle-" + i, "HEAD"); }
        await Git(f, "update-ref", "refs/tags/middle-tag", "HEAD");
        await Git(f, "update-ref", "refs/remotes/origin/middle-remote", "HEAD");
        await Git(f, "update-ref", "refs/notes/review", "HEAD"); await Git(f, "update-ref", "refs/stash", "HEAD");
        MainWindow main = f.Window; await main.OpenRepositoryAsync(f.Repository); await Layout(main);
        ComboBox combo = Branch(main); TextBox editor = Editor(combo);
        string before = await State(f);
        Assert.Multiple(() =>
        {
            Assert.That(combo.IsEditable, Is.True);
            Assert.That(combo.Bounds.Size, Is.EqualTo(new Size(100, 23)));
            Assert.That(combo.MinHeight, Is.EqualTo(23));
            Assert.That(combo.Items, Is.Empty, "Refreshing cached refs must not eagerly populate the branch combo.");
        });
        Click(editor); await Layout(main);
        Assert.That(combo.IsDropDownOpen, Is.True);
        Assert.That(combo.Items.Count, Is.GreaterThan(100));
        Assert.That(combo.Items.OfType<string>(), Does.Contain("feature").And.Not.Contain("origin/main").And.Not.Contain("v1.0"));
        Popup popup = combo.GetVisualDescendants().OfType<Popup>().Single(); Assert.That(popup.MinWidth, Is.GreaterThanOrEqualTo(300));
        Press(main, PhysicalKey.Escape); await Layout(main);
        editor.Focus(); editor.SelectAll(); main.KeyTextInput("MiDdLe"); await Layout(main);
        Assert.That(combo.Text, Is.EqualTo("MiDdLe"));
        Assert.That(combo.Items.OfType<string>().ToArray(), Has.Length.EqualTo(103));
        Assert.That(combo.Items.OfType<string>().All(name => name.Contains("middle", StringComparison.Ordinal)), Is.True);
        Press(main, PhysicalKey.Escape); await SetScopes(main, local: false, remote: true, tag: true);
        editor.Focus(); editor.SelectAll(); main.KeyTextInput("middle"); await Layout(main);
        Assert.That(combo.Items.OfType<string>(), Is.EquivalentTo(new[] { "origin/middle-remote", "middle-tag" }));
        Press(main, PhysicalKey.Escape); await SetScopes(main, local: false, remote: false, tag: false);
        editor.Focus(); editor.SelectAll(); main.KeyTextInput(""); combo.Text = ""; Click(editor); await Layout(main);
        Assert.That(combo.Items.OfType<string>(), Does.Contain("stash").And.Contain("notes/review").And.Not.Contain("refs/stash"));
        Assert.That(await State(f), Is.EqualTo(before), "Typing, opening or limiting suggestions must not write Git/config/index/worktree.");
    }

    [AvaloniaTest]
    public async Task TypingEscapeRefreshAndLoadMorePreserveDraftCaretWithoutApplyingIt()
    {
        using Fixture f = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
        MainWindow main = f.Window; await Accept(main, "feature");
        string[] accepted = Hashes(main); string before = await State(f);
        ComboBox combo = Branch(main); TextBox editor = Editor(combo);
        editor.Focus(); editor.SelectAll(); main.KeyTextInput("main"); await Layout(main);
        Assert.That(combo.IsDropDownOpen, Is.True);
        Assert.That(Hashes(main), Is.EqualTo(accepted));
        editor.SelectionStart = editor.SelectionEnd = 2;
        Press(main, PhysicalKey.Escape); await Layout(main);
        Assert.That(combo.IsDropDownOpen, Is.False); Assert.That(combo.Text, Is.EqualTo("main"));
        Assert.That(editor.SelectionStart, Is.EqualTo(2));
        Press(main, PhysicalKey.F5); await Idle(main); await Layout(main);
        Assert.That(Hashes(main), Is.EqualTo(accepted), "Refresh must read the accepted expression, not the draft.");
        Assert.That(combo.Text, Is.EqualTo("main")); Assert.That(editor.SelectionStart, Is.EqualTo(2));
        editor.Focus(); Click(editor); await Layout(main); Assert.That(combo.Items.OfType<string>(), Is.EqualTo(new[] { "main" }));
        Press(main, PhysicalKey.Escape);
        Assert.That(await State(f), Is.EqualTo(before));
        // The advanced dialog is another consumer of the accepted state.
        Click(Field<Button>(main, "BrowseAdvancedFilter")); await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        HistoryFilterDialog advanced = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        Assert.That(Field<TextBox>(advanced, "BranchFilter").Text, Is.EqualTo("feature"));
        advanced.Close(); await Idle(main); Assert.That(combo.Text, Is.EqualTo("main"));
        Click(Field<Button>(main, "BrowseAdvancedFilter")); await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        advanced = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        Field<TextBox>(advanced, "BranchFilter").Text = "main"; Field<NumericUpDown>(advanced, "CommitsLimit").Value = 1;
        Click(advanced.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "OK")); await Idle(main); await Layout(main);
        Assert.That(Hashes(main).Length, Is.EqualTo(1));
        combo.Text = "feature"; editor.SelectionStart = editor.SelectionEnd = 3;
        Button more = Field<Button>(main, "BrowseLoadMore");
        if (more.IsEffectivelyVisible) { Click(more); }
        else
        {
            CompactBrowserToolbar toolbar = Field<CompactBrowserToolbar>(main, "RepositoryToolbar"); Click(toolbar.Overflow); await Layout(main);
            Click(toolbar.OverflowFlyout.Items.OfType<MenuItem>().Single(item => item.Header as string == "Load more"));
        }
        await Idle(main); await Layout(main);
        Assert.That(Hashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery(Count: 2) { BranchFilter = "main" })).Select(commit => commit.Hash)));
        Assert.That(combo.Text, Is.EqualTo("feature")); Assert.That(editor.SelectionStart, Is.EqualTo(3));
    }

    [AvaloniaTest]
    public async Task RealPointerSuggestionAndKeyboardChoiceOnlyApplyOnEnterWithCompactLightDarkPopups()
    {
        using Fixture f = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
        MainWindow main = f.Window; string before = await State(f);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme; await Layout(main);
            await Scope(main, "All");
            ComboBox combo = Branch(main); combo.Text = ""; TextBox editor = Editor(combo);
            string[] all = Hashes(main);
            PathIcon glyph = combo.GetVisualDescendants().OfType<PathIcon>().Single(icon => icon.Name == "DropDownGlyph");
            Assert.That(glyph.Bounds.Size, Is.EqualTo(new Size(6, 3)));
            Click(glyph); await Layout(main);
            Popup popup = combo.GetVisualDescendants().OfType<Popup>().Single();
            ComboBoxItem feature = popup.Child!.GetVisualDescendants().OfType<ComboBoxItem>().Single(item => item.Content as string == "feature");
            Capture(main, "branch-filter-inline-" + theme); Capture((TopLevel)feature.GetVisualRoot()!, "branch-filter-popup-" + theme);
            Assert.That(((TopLevel)feature.GetVisualRoot()!).Bounds.Width, Is.GreaterThanOrEqualTo(300));
            Click(feature); await Layout(main);
            Assert.That(combo.Text, Is.EqualTo("feature")); Assert.That(Hashes(main), Is.EqualTo(all));
            Editor(combo).Focus(); Press(main, PhysicalKey.Enter); await Idle(main);
            Assert.That(Subjects(main), Is.EqualTo(new[] { "Feature branch change", "Initial commit" }));
            Assert.That(Editor(combo).IsFocused, Is.True, "An accepted inline expression must remain editable for consecutive keyboard actions.");
            combo.Text = ""; Editor(combo).Focus(); Press(main, PhysicalKey.ArrowDown); await Layout(main);
            Assert.That(combo.IsDropDownOpen, Is.True);
            Press(main, PhysicalKey.ArrowDown); await Layout(main);
            Assert.That(combo.SelectedItem, Is.Not.Null);
            string chosen = (string)combo.SelectedItem!;
            Assert.That(Hashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = "feature" })).Select(commit => commit.Hash)));
            Press(main, PhysicalKey.Escape); await Layout(main); Assert.That(combo.IsDropDownOpen, Is.False);
            Assert.That(Hashes(main).Length, Is.EqualTo(2));
            Editor(combo).Focus(); Press(main, PhysicalKey.Enter); await Idle(main);
            Assert.That(combo.Text, Is.EqualTo(chosen));
            Assert.That(Hashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = chosen })).Select(commit => commit.Hash)));
        }
        Assert.That(await State(f), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ScopePresetsRetainCustomTextFilteredReactivatesAcceptedAndEmptyEnterReturnsAll()
    {
        using Fixture f = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
        MainWindow main = f.Window; await Accept(main, "feature"); string[] feature = Hashes(main);
        await Scope(main, "Current"); Assert.That(Branch(main).Text, Is.EqualTo("feature"));
        AssertScopeCaptionFits(main);
        Assert.That(Hashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = "HEAD", All = false })).Select(commit => commit.Hash)));
        await Scope(main, "All"); Assert.That(Branch(main).Text, Is.EqualTo("feature")); Assert.That(Hashes(main).Length, Is.GreaterThan(feature.Length));
        // Author-only OK while the custom expression is inactive must not erase it.
        Click(Field<Button>(main, "BrowseAdvancedFilter")); await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        HistoryFilterDialog dialog = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        Assert.That(Field<TextBox>(dialog, "BranchFilter").Text, Is.EqualTo("feature"));
        Assert.That(Field<CheckBox>(dialog, "BranchFilterCheck").IsChecked, Is.False);
        Field<CheckBox>(dialog, "AuthorCheck").IsChecked = true; Field<TextBox>(dialog, "Author").Text = "Desktop Tester";
        Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "OK")); await Idle(main);
        Assert.That(Branch(main).Text, Is.EqualTo("feature"));
        Branch(main).Text = "unaccepted draft"; await Scope(main, "Filtered");
        Assert.That(Branch(main).Text, Is.EqualTo("feature")); Assert.That(Hashes(main), Is.EqualTo(feature));
        AssertScopeCaptionFits(main);
        await Scope(main, "Current"); await Accept(main, "  \t  ");
        Assert.That(Branch(main).Text, Is.Empty); Assert.That(ScopeLabel(main), Is.EqualTo("All branches"));
        await Scope(main, "Filtered"); Assert.That(ScopeLabel(main), Is.EqualTo("Filtered branches"));
        Assert.That(Hashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = "" })).Select(commit => commit.Hash)));
        Click(Field<Button>(main, "BrowseAdvancedFilter")); await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        dialog = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        Assert.That(Field<CheckBox>(dialog, "BranchFilterCheck").IsChecked, Is.True);
        Assert.That(dialog.ReadQuery().BranchFilter, Is.Empty);
        Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "OK")); await Idle(main);
        Assert.That(ScopeLabel(main), Is.EqualTo("Filtered branches"), "Advanced OK preserves enabled-but-empty; only toolbar Enter empty returns All.");
    }

    [AvaloniaTest]
    public async Task OverflowHasTheSameEditableComboAndControlEAlternatesQuickThenBranchEditors()
    {
        using Fixture f = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
        MainWindow main = f.Window; await Accept(main, "feature"); string[] before = Hashes(main);
        Branch(main).Text = "main"; main.Width = 860; await Layout(main);
        CompactBrowserToolbar toolbar = Field<CompactBrowserToolbar>(main, "RepositoryToolbar");
        Field<ListBox>(main, "RevisionHistory").Focus(); Press(main, PhysicalKey.E, RawInputModifiers.Control); await Layout(main);
        TextBox quick = OverflowControls(toolbar).OfType<TextBox>().Single(control => control.Name == "RevisionQuickFilterOverflow");
        Assert.That(quick.IsFocused, Is.True, "The original first Ctrl+E focuses the revision quick filter.");
        Press(main, PhysicalKey.E, RawInputModifiers.Control); await Layout(main);
        ComboBox branch = OverflowControls(toolbar).OfType<ComboBox>().Single(control => control.Name == "BranchHistoryFilterOverflow");
        Assert.That(branch.IsEditable && Editor(branch).IsFocused, Is.True);
        Press(main, PhysicalKey.E, RawInputModifiers.Control); await Layout(main);
        Assert.That(quick.IsFocused, Is.True, "Ctrl+E must also return from the branch popup's input route to the quick filter.");
        Press(main, PhysicalKey.E, RawInputModifiers.Control); await Layout(main);
        Assert.That(Editor(branch).IsFocused, Is.True);
        Assert.That(branch.Bounds.Size, Is.EqualTo(new Size(100, 23)));
        Assert.That(branch.Text, Is.EqualTo("main"));
        Editor(branch).SelectAll(); main.KeyTextInput("FeAt"); await Layout(main);
        Assert.That(branch.Items.OfType<string>(), Is.EqualTo(new[] { "feature" })); Assert.That(Branch(main).Text, Is.EqualTo("FeAt"));
        Assert.That(Hashes(main), Is.EqualTo(before)); Press(main, PhysicalKey.Escape); await Layout(main);
        Capture((TopLevel)branch.GetVisualRoot()!, "branch-filter-overflow");
        Editor(branch).SelectAll(); main.KeyTextInput("feature"); await Layout(main);
        Press(main, PhysicalKey.Enter); await Idle(main);
        Assert.That(toolbar.OverflowFlyout.IsOpen, Is.False); Assert.That(Hashes(main), Is.EqualTo(before));
        main.Width = 1440; await Layout(main); Assert.That(Branch(main).Text, Is.EqualTo("feature"));
    }

    [AvaloniaTest]
    public async Task NoResultsPlaceholderAndInvalidSimpleTokensWarnIndividuallyAndKeepEveryValidToken()
    {
        using Fixture f = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
        MainWindow main = f.Window; string before = await State(f);
        ComboBox combo = Branch(main); TextBox editor = Editor(combo);
        editor.Focus(); editor.SelectAll(); main.KeyTextInput("no-such-ref"); await Layout(main);
        Assert.That(combo.Items.OfType<string>(), Is.EqualTo(new[] { "<No results found>" }));
        combo.SelectedItem = combo.Items[0]; Editor(combo).Focus(); Press(main, PhysicalKey.Enter); await Idle(main);
        Assert.That(combo.Text, Is.Empty); Assert.That(ScopeLabel(main), Is.EqualTo("All branches"));
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme; combo.Text = "  feature  absent-first\tmain ^absent-second  "; Editor(combo).Focus(); Press(main, PhysicalKey.Enter);
            foreach (string token in new[] { "absent-first", "^absent-second" })
            {
                await Until(() => main.OwnedWindows.Any(window => window.Name == "BranchFilterInvalidReference"));
                Window warning = main.OwnedWindows.Single(window => window.Name == "BranchFilterInvalidReference"); await Layout(warning);
                Assert.That(warning.Owner, Is.SameAs(main)); Assert.That(warning.ActualThemeVariant, Is.EqualTo(theme));
                Assert.That(Field<TextBlock>(warning, "BranchFilterWarningMessage").Text, Does.Contain("\"" + token + "\"").And.Contain("will be ignored"));
                Capture(warning, "branch-filter-invalid-" + token.TrimStart('^') + "-" + theme);
                Click(Field<Button>(warning, "BranchFilterWarningOK")); await Until(() => !warning.IsVisible);
            }
            await Idle(main); Assert.That(combo.Text, Is.EqualTo("feature main"));
            Assert.That(Hashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = "feature main" })).Select(commit => commit.Hash)));
        }
        Assert.That(await State(f), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task AdvancedExpressionsRangesAndNativeErrorsUseAcceptedStateAndPreserveTheLastGoodGrid()
    {
        using Fixture f = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
        MainWindow main = f.Window; await Accept(main, "feature");
        Branch(main).Text = "draft never applied";
        await Layout(main);
        Assert.That(Branch(main).IsDropDownOpen, Is.True, "The first-click test starts after the draft's suggestion popup has mapped.");
        Assert.That(Branch(main).GetVisualDescendants().OfType<Popup>().Single().OverlayDismissEventPassThrough, Is.True);
        Capture(main, "branch-filter-before-first-advanced-click");
        Button advancedButton = Field<Button>(main, "BrowseAdvancedFilter");
        Popup suggestions = Branch(main).GetVisualDescendants().OfType<Popup>().Single();
        List<string> clickTrace = [];
        void Trace(string label) => clickTrace.Add(label + " | " + JsonSerializer.Serialize(new
        {
            Focus = main.FocusManager?.GetFocusedElement() is Control focus ? focus.GetType().Name + ":" + focus.Name : null,
            ButtonPressed = advancedButton.IsPressed, ButtonHover = advancedButton.IsPointerOver,
            ButtonEnabled = advancedButton.IsEffectivelyEnabled, ButtonVisible = advancedButton.IsEffectivelyVisible,
            ComboFocus = Branch(main).IsKeyboardFocusWithin, EditorStart = Editor(Branch(main)).SelectionStart,
            PopupOpen = suggestions.IsOpen, BranchOpen = Branch(main).IsDropDownOpen,
            main.IsActive, main.IsVisible, main.IsBusy, Owned = main.OwnedWindows.Select(window => window.GetType().Name + ":" + window.IsVisible).ToArray()
        }));
        void PointerTrace(object? sender, PointerEventArgs args)
            => Trace((sender is Control control ? control.Name ?? control.GetType().Name : sender?.GetType().Name) + ":" + args.RoutedEvent?.Name
                + ":" + args.Route + ":handled=" + args.Handled + ":source=" + (args.Source as Control)?.GetType().Name);
        main.AddHandler(Control.PointerPressedEvent, PointerTrace, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        main.AddHandler(Control.PointerReleasedEvent, PointerTrace, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        advancedButton.AddHandler(Control.PointerPressedEvent, PointerTrace, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        advancedButton.AddHandler(Control.PointerReleasedEvent, PointerTrace, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        advancedButton.Click += (_, _) => Trace("Advanced Click");
        suggestions.Opened += (_, _) => Trace("Popup Opened"); suggestions.Closed += (_, _) => Trace("Popup Closed");
        Point clickPoint = advancedButton.TranslatePoint(new Point(Math.Min(12, advancedButton.Bounds.Width / 2), advancedButton.Bounds.Height / 2), main)!.Value;
        Trace("Before single click at " + clickPoint + " button " + advancedButton.Bounds);
        main.MouseMove(clickPoint); Trace("After MouseMove");
        main.MouseDown(clickPoint, MouseButton.Left); Trace("After MouseDown");
        main.MouseUp(clickPoint, MouseButton.Left); Trace("After MouseUp");
        await Layout(main); Trace("After Layout"); Capture(main, "branch-filter-after-first-advanced-click");
        string traceDirectory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "gitextensions-branch-filter-screenshots");
        string tracePath = Path.Combine(traceDirectory, "advanced-first-click-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(tracePath, JsonSerializer.Serialize(clickTrace, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(tracePath, "Real single pointer-click routes and popup/focus state, before asserting the advanced dialog.");
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, clickTrace));
        Assert.That(clickTrace.Count(line => line.StartsWith("Advanced Click |", StringComparison.Ordinal)), Is.EqualTo(1), "The real first pointer press/release must produce exactly one button Click.");
        await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        HistoryFilterDialog dialog = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        Assert.That(Field<TextBox>(dialog, "BranchFilter").Text, Is.EqualTo("feature"));
        Field<TextBox>(dialog, "BranchFilter").Text = "feature..main";
        Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "OK")); await Idle(main);
        Assert.That(Branch(main).Text, Is.EqualTo("feature..main"));
        Assert.That(Hashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = "feature..main" })).Select(commit => commit.Hash)));
        string[] good = Hashes(main); Branch(main).Text = "absent..main"; Editor(Branch(main)).Focus(); Press(main, PhysicalKey.Enter);
        await Until(() => !main.IsBusy); Assert.That(main.OwnedWindows, Is.Empty, "Ranges bypass simple-reference warnings in the Windows original.");
        Assert.That(main.LastException, Is.TypeOf<GitCommandException>()); Assert.That(Hashes(main), Is.EqualTo(good));
        Assert.That(Branch(main).Text, Is.EqualTo("absent..main"));
        Branch(main).Text = "main"; Field<ListBox>(main, "RevisionHistory").Focus(); Press(main, PhysicalKey.F5);
        await Until(() => !main.IsBusy); Assert.That(main.LastException, Is.TypeOf<GitCommandException>());
        Assert.That(Hashes(main), Is.EqualTo(good), "Refresh keeps reporting the accepted invalid range; a new draft must not replace it.");
        await Accept(main, "main ^feature");
        Assert.That(Hashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = "main ^feature" })).Select(commit => commit.Hash)));
        good = Hashes(main);
        Click(Field<Button>(main, "BrowseAdvancedFilter")); await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        dialog = main.OwnedWindows.OfType<HistoryFilterDialog>().Single(); Field<TextBox>(dialog, "BranchFilter").Text = "absent-simple-ref";
        Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "OK")); await Until(() => !main.IsBusy);
        Assert.That(main.OwnedWindows, Is.Empty, "Advanced OK does not show toolbar's reference-omission warnings.");
        Assert.That(main.LastException, Is.TypeOf<GitCommandException>());
        Assert.That(Branch(main).Text, Is.EqualTo("absent-simple-ref")); Assert.That(Hashes(main), Is.EqualTo(good));
    }

    [AvaloniaTest]
    public async Task AdvancedResetRetainsCurrentReflogAndRememberedFilterForLaterFilteredScope()
    {
        using Fixture f = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
        MainWindow main = f.Window; await Accept(main, "feature");
        await Scope(main, "Current");
        Click(Field<Button>(main, "BrowseAdvancedFilter")); await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        HistoryFilterDialog dialog = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        Assert.That(Field<TextBox>(dialog, "BranchFilter").Text, Is.EqualTo("feature"));
        Assert.That(Field<CheckBox>(dialog, "CurrentBranchOnly").IsChecked, Is.True);
        Field<CheckBox>(dialog, "Reflog").IsChecked = true; Field<CheckBox>(dialog, "FullHistory").IsChecked = true;
        Field<CheckBox>(dialog, "SimplifyMerges").IsChecked = true;
        Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Reset")); await Idle(main);
        Assert.That(ScopeLabel(main), Is.EqualTo("Current branch")); Assert.That(Branch(main).Text, Is.EqualTo("feature"));
        // Wait for the refreshed AutoSize toolbar and the closing modal to
        // finish their layout before measuring the next real pointer click.
        await Layout(main);
        Button reopen = Field<Button>(main, "BrowseAdvancedFilter"); Popup suggestions = Branch(main).GetVisualDescendants().OfType<Popup>().Single();
        List<string> reopenTrace = []; int reopenClicks = 0;
        void TraceReopen(string label) => reopenTrace.Add(label + " | " + JsonSerializer.Serialize(new
        {
            Focus = main.FocusManager?.GetFocusedElement() is Control focus ? focus.GetType().Name + ":" + focus.Name : null,
            reopen.IsPressed, PopupOpen = suggestions.IsOpen, ComboOpen = Branch(main).IsDropDownOpen,
            main.IsBusy, Owned = main.OwnedWindows.Select(window => window.GetType().Name).ToArray(),
            ButtonPoint = reopen.TranslatePoint(new Point(12, reopen.Bounds.Height / 2), main)?.ToString()
        }));
        reopen.AddHandler(Control.PointerPressedEvent, (_, args) => TraceReopen("Pressed " + args.Route + " handled=" + args.Handled), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        reopen.AddHandler(Control.PointerReleasedEvent, (_, args) => TraceReopen("Released " + args.Route + " handled=" + args.Handled), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        reopen.Click += (_, _) => { reopenClicks++; TraceReopen("Click"); };
        suggestions.Closed += (_, _) => TraceReopen("Popup Closed");
        TraceReopen("Before single reopen click"); Capture(main, "branch-filter-reset-before-reopen");
        Click(reopen); await Layout(main); TraceReopen("After single reopen click");
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "gitextensions-branch-filter-screenshots");
        string trace = Path.Combine(directory, "advanced-reset-reopen-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(trace, JsonSerializer.Serialize(reopenTrace, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(trace, "Single real reopen click after Reset and a rendered AutoSize toolbar.");
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, reopenTrace));
        Assert.That(reopenClicks, Is.EqualTo(1), "The first real pointer press/release after Reset must reopen Advanced exactly once.");
        await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        dialog = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        GitLogQuery query = dialog.ReadQuery();
        Assert.That(query.Reflog && query.FullHistory && query.SimplifyMerges, Is.True);
        Assert.That(query.Revision, Is.EqualTo("HEAD")); Assert.That(Field<TextBox>(dialog, "BranchFilter").Text, Is.EqualTo("feature"));
        dialog.Close(); await Idle(main); await Scope(main, "Filtered");
        Assert.That(Branch(main).Text, Is.EqualTo("feature"));
        Assert.That(Hashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(query with { Revision = null, BranchFilter = "feature", All = true })).Select(commit => commit.Hash)));
    }

    [AvaloniaTest]
    public async Task CurrentPresetAndAdvancedCurrentInAnUnbornRepositoryNeverWarnAboutHead()
    {
        using Fixture f = await Fixture.CreateAsync("initial\n", "initial\n");
        string unborn = Path.Combine(Path.GetDirectoryName(f.Repository)!, "unborn"); Directory.CreateDirectory(unborn);
        GitResult init = await f.Git.RunAsync(unborn, ["init", "-b", "unborn-main"]); Assert.That(init.Success, Is.True, init.StandardError);
        MainWindow main = f.Window; await main.OpenRepositoryAsync(unborn); await Idle(main); await Scope(main, "Current");
        Assert.That(Hashes(main), Is.Empty); Assert.That(main.OwnedWindows, Is.Empty);
        Click(Field<Button>(main, "BrowseAdvancedFilter")); await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        HistoryFilterDialog dialog = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        Assert.That(Field<CheckBox>(dialog, "CurrentBranchOnly").IsChecked, Is.True);
        Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "OK")); await Idle(main);
        Assert.That(Hashes(main), Is.Empty); Assert.That(main.OwnedWindows, Is.Empty);
        Assert.That(ScopeLabel(main), Is.EqualTo("Current branch")); Assert.That(Branch(main).Text, Is.Null.Or.Empty);
    }

    [AvaloniaTest]
    public async Task CancellationDuringValidationRetainsAcceptedStateAndOpeningAnotherRepositoryClearsOldSuggestions()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Linux process cancellation fixture."); return; }
        using Fixture f = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
        MainWindow main = f.Window; await Accept(main, "feature"); string[] accepted = Hashes(main); string before = await State(f);
        using (BlockingValidation block = new(f))
        {
            Branch(main).Text = "main block-ref"; Editor(Branch(main)).Focus(); Press(main, PhysicalKey.Enter);
            try
            {
                await Until(() => File.Exists(block.Marker)); Assert.That(main.IsBusy, Is.True);
                Click(Field<Button>(main, "BrowseCancelOperation")); await Until(() => !main.IsBusy);
                Assert.That(main.LastException, Is.Null); Assert.That(Hashes(main), Is.EqualTo(accepted));
                Assert.That(Branch(main).Text, Is.EqualTo("main block-ref"));
            }
            finally { if (main.IsBusy) { Click(Field<Button>(main, "BrowseCancelOperation")); await Until(() => !main.IsBusy); } }
        }
        Assert.That(await State(f), Is.EqualTo(before));
        string target = Path.Combine(Path.GetDirectoryName(f.Repository)!, "another-repository"); Directory.CreateDirectory(target);
        foreach (string[] args in new[] { new[] { "init", "-b", "other-main" }, new[] { "config", "user.name", "Other" }, new[] { "config", "user.email", "other@example.invalid" }, new[] { "commit", "--allow-empty", "-m", "Independent history" } })
        { GitResult result = await f.Git.RunAsync(target, args); Assert.That(result.Success, Is.True, result.StandardError); }
        await main.OpenRepositoryAsync(target); await Idle(main);
        Assert.That(Branch(main).Text, Is.Null.Or.Empty); Assert.That(Branch(main).Items, Is.Empty);
        Click(Editor(Branch(main))); await Layout(main); Assert.That(Branch(main).Items.OfType<string>(), Is.EqualTo(new[] { "other-main" }));
        Assert.That(Subjects(main), Is.EqualTo(new[] { "Independent history" }));
    }

    [AvaloniaTest]
    public async Task ClosingTheOwnerDuringAReferenceWarningCancelsWithoutApplyingTheOldExpression()
    {
        using Fixture f = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
        MainWindow main = f.Window; await Accept(main, "feature"); string before = await State(f);
        string[] accepted = Hashes(main);
        Branch(main).Text = "main absent-ref"; Editor(Branch(main)).Focus(); Press(main, PhysicalKey.Enter);
        await Until(() => main.OwnedWindows.Any(window => window.Name == "BranchFilterInvalidReference"));
        Window warning = main.OwnedWindows.Single(window => window.Name == "BranchFilterInvalidReference");
        main.Close(); await Until(() => !main.IsBusy && !warning.IsVisible);
        Assert.That(main.LastException, Is.Null); Assert.That(await State(f), Is.EqualTo(before));
        Assert.That(Branch(main).Text, Is.EqualTo("main absent-ref"), "The owned warning can close before the parent's IsVisible becomes false: its valid token must not be accepted during that interval.");
        Assert.That(Hashes(main), Is.EqualTo(accepted));
    }

    private static ComboBox Branch(MainWindow main) => Field<ComboBox>(main, "BranchHistoryFilter");
    private static TextBox Editor(ComboBox combo) => combo.GetVisualDescendants().OfType<TextBox>().Single();
    private static T Field<T>(TopLevel owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static string[] Hashes(MainWindow main) => Field<ListBox>(main, "RevisionHistory").Items.OfType<GitCommit>().Select(commit => commit.Hash).ToArray();
    private static string[] Subjects(MainWindow main) => Field<ListBox>(main, "RevisionHistory").Items.OfType<GitCommit>().Select(commit => commit.Subject).ToArray();
    private static string? ScopeLabel(MainWindow main) => Field<Button>(main, "BrowseHistoryScope").GetVisualDescendants().OfType<TextBlock>().Single().Text;
    private static void AssertScopeCaptionFits(MainWindow main)
    {
        TextBlock caption = Field<Button>(main, "BrowseHistoryScope").GetVisualDescendants().OfType<TextBlock>().Single();
        double textWidth = new FormattedText(caption.Text ?? "", System.Globalization.CultureInfo.CurrentCulture, caption.FlowDirection,
            new Typeface(caption.FontFamily, caption.FontStyle, caption.FontWeight), caption.FontSize, caption.Foreground ?? Brushes.Black).Width;
        Assert.That(caption.Bounds.Width, Is.GreaterThanOrEqualTo(textWidth - 1), "The current scope label must remain fully visible, including its translated text.");
    }
    private static IEnumerable<Control> OverflowControls(CompactBrowserToolbar toolbar)
    {
        foreach (MenuItem item in toolbar.OverflowFlyout.ItemsSource!.Cast<MenuItem>())
        {
            foreach (Control control in item.GetVisualDescendants().OfType<Control>()) { yield return control; }
            foreach (MenuItem child in item.Items.OfType<MenuItem>())
            { if (child.Header is Control header) { foreach (Control control in header.GetVisualDescendants().OfType<Control>()) { yield return control; } } }
        }
    }
    private static async Task Accept(MainWindow main, string text)
    { Branch(main).Text = text; Editor(Branch(main)).Focus(); Press(main, PhysicalKey.Enter); await Idle(main); await Layout(main); }
    private static async Task Scope(MainWindow main, string name)
    {
        Button button = Field<Button>(main, "BrowseHistoryScope"); Click(button); await Layout(main);
        MenuItem choice = ((MenuFlyout)button.Flyout!).Items.OfType<MenuItem>().Single(item => item.Name == "HistoryScope" + name);
        Click(choice); await Idle(main); await Layout(main);
    }
    private static async Task SetScopes(MainWindow main, bool local, bool remote, bool tag)
    {
        Button button = Field<Button>(main, "BranchHistoryFilterType"); Click(button); await Layout(main);
        foreach ((string name, bool value) in new[] { ("Local", local), ("Remote", remote), ("Tag", tag) })
        {
            MenuItem choice = ((MenuFlyout)button.Flyout!).Items.OfType<MenuItem>().Single(item => item.Name == "BranchSuggestion" + name);
            if (choice.IsChecked != value) { Click(choice); await Layout(main); }
            Assert.That(choice.IsChecked, Is.EqualTo(value));
        }
        ((MenuFlyout)button.Flyout!).Hide(); await Layout(main);
    }
    private static void Click(Control control)
    {
        Assert.That(control.IsEffectivelyEnabled && control.IsEffectivelyVisible, Is.True, control.Name ?? control.GetType().Name);
        TopLevel root = (TopLevel)control.GetVisualRoot()!;
        Point point = control.TranslatePoint(new Point(Math.Min(12, control.Bounds.Width / 2), control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point); root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left);
    }
    private static void Press(TopLevel owner, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { owner.KeyPressQwerty(key, modifiers); if (owner.IsVisible) { owner.KeyReleaseQwerty(key, modifiers); } }
    private static async Task Layout(TopLevel owner) { owner.UpdateLayout(); Dispatcher.UIThread.RunJobs(); await Task.Delay(30); owner.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Idle(MainWindow main) { await Until(() => !main.IsBusy); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); }
    private static async Task Until(Func<bool> predicate)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); while (!predicate()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20, timeout.Token); } }
    private static async Task Git(Fixture f, params string[] args)
    { GitResult result = await f.Git.RunAsync(f.Repository, args); Assert.That(result.Success, Is.True, result.StandardError); }
    private static async Task<string> State(Fixture f)
    {
        List<string> state = [];
        foreach (string[] args in new[] { new[] { "--no-optional-locks", "rev-parse", "HEAD" }, new[] { "--no-optional-locks", "for-each-ref", "--format=%(refname)%00%(objectname)" }, new[] { "--no-optional-locks", "ls-files", "--stage", "-z" } })
        { GitResult result = await f.Git.RunAsync(f.Repository, args); Assert.That(result.Success, Is.True, result.StandardError); state.Add(result.StandardOutput); }
        foreach (string file in Directory.EnumerateFiles(f.Repository, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(f.Repository, file);
            if (relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && relative is not ".git/config" and not ".git/index") { continue; }
            state.Add(relative + "=" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))));
        }
        return JsonSerializer.Serialize(state);
    }
    private static void Capture(TopLevel owner, string name)
    {
        using var image = owner.CaptureRenderedFrame(); Assert.That(image, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "gitextensions-branch-filter-screenshots");
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N") + ".png");
        image!.Save(path); TestContext.AddTestAttachment(path, "Unmodified rendered headless frame, actual Git repository");
    }
    private sealed class BlockingValidation : IDisposable
    {
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        internal string Marker { get; }
        internal BlockingValidation(Fixture f)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
            string git = (_path ?? "").Split(Path.PathSeparator).Select(directory => Path.Combine(directory, "git")).First(File.Exists);
            string directory = Path.Combine(Path.GetDirectoryName(f.Repository)!, "blocking-filter-bin"); Directory.CreateDirectory(directory);
            Marker = Path.Combine(directory, "validation.pid"); string executable = Path.Combine(directory, "git");
            File.WriteAllText(executable, "#!/usr/bin/python3\nimport os,pathlib,sys,time\n" +
                "if 'rev-parse' in sys.argv and any('block-ref' in arg for arg in sys.argv):\n    pathlib.Path(" + JsonSerializer.Serialize(Marker) + ").write_text(str(os.getpid()))\n    time.sleep(30)\n" +
                "os.execv(" + JsonSerializer.Serialize(git) + ", [" + JsonSerializer.Serialize(git) + "] + sys.argv[1:])\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + _path);
        }
        public void Dispose() => Environment.SetEnvironmentVariable("PATH", _path);
    }
}

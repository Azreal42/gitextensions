using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
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
        MainWindow main = f.Window; await Accept(main, "feature"); await CompleteHistory();
        int savedMaximum = new PreferencesStore().Load().MaxRevisionGraphCommits;
        string[] accepted = Hashes(main); string before = await State(f);
        ComboBox combo = Branch(main); TextBox editor = Editor(combo);
        editor.Focus(); editor.SelectAll(); main.KeyTextInput("main"); await Layout(main);
        Assert.That(combo.IsDropDownOpen, Is.True);
        Assert.That(Hashes(main), Is.EqualTo(accepted));
        editor.SelectionStart = editor.SelectionEnd = 2;
        Press(main, PhysicalKey.Escape); await Layout(main);
        Assert.That(combo.IsDropDownOpen, Is.False); Assert.That(combo.Text, Is.EqualTo("main"));
        Assert.That(editor.SelectionStart, Is.EqualTo(2));
        Press(main, PhysicalKey.F5); await Idle(main); await CompleteHistory(); await Layout(main);
        Assert.That(Hashes(main), Is.EqualTo(accepted), "Refresh must read the accepted expression, not the draft.");
        Assert.That(combo.Text, Is.EqualTo("main")); Assert.That(editor.SelectionStart, Is.EqualTo(2));
        editor.Focus(); Click(editor); await Layout(main); Assert.That(combo.Items.OfType<string>(), Is.EqualTo(new[] { "main" }));
        Press(main, PhysicalKey.Escape);
        Assert.That(await State(f), Is.EqualTo(before));
        // The advanced dialog is another consumer of the accepted state.
        Click(Field<Button>(main, "BrowseAdvancedFilter")); await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        HistoryFilterDialog advanced = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        Assert.That(Field<TextBox>(advanced, "BranchFilter").Text, Is.EqualTo("feature"));
        advanced.Close(); await Idle(main); await CompleteHistory(); Assert.That(combo.Text, Is.EqualTo("main"));
        Click(Field<Button>(main, "BrowseAdvancedFilter")); await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        advanced = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
        Field<TextBox>(advanced, "BranchFilter").Text = "main";
        CheckBox privateMaximum = Field<CheckBox>(advanced, "CommitsLimitCheck");
        Assert.That(privateMaximum.IsChecked, Is.False); Click(privateMaximum); await Layout(advanced);
        Assert.That(privateMaximum.IsChecked, Is.True); Assert.That(Field<NumericUpDown>(advanced, "CommitsLimit").IsEnabled, Is.True);
        Field<NumericUpDown>(advanced, "CommitsLimit").Value = 1;
        Click(advanced.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "OK")); await Idle(main); await CompleteHistory(); await Layout(main);
        Assert.That(Hashes(main).Length, Is.EqualTo(1));
        combo.Text = "feature"; editor.SelectionStart = editor.SelectionEnd = 3;
        // F5 continues to use accepted main and its private ceiling, never the
        // feature draft. A delivery batch size no longer creates a second page.
        Press(main, PhysicalKey.F5); await Idle(main); await CompleteHistory(); await Layout(main);
        Assert.That(ReaderHashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery(Count: 1) { BranchFilter = "main" })).Select(commit => commit.Hash)));
        Assert.That(combo.Text, Is.EqualTo("feature")); Assert.That(editor.SelectionStart, Is.EqualTo(3));
        Assert.That(new PreferencesStore().Load().MaxRevisionGraphCommits, Is.EqualTo(savedMaximum), "A private filter ceiling never changes the saved global maximum.");

        async Task CompleteHistory()
        {
            await Until(() => !main.IsBusy && main.WindowWorkCompletion.IsCompleted);
            await main.WindowWorkCompletion;
            Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        }
    }

    [AvaloniaTest]
    public async Task RealPointerSuggestionAndKeyboardChoiceOnlyApplyOnEnterWithCompactLightDarkPopups()
    {
        using OrangePlatformAccent accent = new();
        using Fixture f = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
        MainWindow main = f.Window; string before = await State(f);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme; await Layout(main);
            accent.AssertActive(theme);
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
            await ClickRowWithHistoricalPalette(feature, main, theme, "inline");
            Assert.That(combo.Text, Is.EqualTo("feature")); Assert.That(Hashes(main), Is.EqualTo(all));
            Assert.That(editor.Text, Is.EqualTo("feature"), "Pointer choice must update the real editor through its template binding before Enter.");
            Assert.That(editor.IsFocused, Is.True, "Pointer choice must return focus to the real editor without a corrective Focus call.");
            Press(main, PhysicalKey.Enter); await Idle(main);
            Assert.That(Subjects(main), Is.EqualTo(new[] { "Feature branch change", "Initial commit" }));
            Assert.That(Editor(combo).IsFocused, Is.True, "An accepted inline expression must remain editable for consecutive keyboard actions.");
            combo.Text = ""; Editor(combo).Focus(); Press(main, PhysicalKey.ArrowDown); await Layout(main);
            Assert.That(combo.IsDropDownOpen, Is.True);
            Press(main, PhysicalKey.ArrowDown); await Layout(main);
            Assert.That(combo.SelectedItem, Is.Not.Null);
            string chosen = (string)combo.SelectedItem!;
            ComboBoxItem selected = (ComboBoxItem)combo.ContainerFromIndex(combo.SelectedIndex)!;
            await AssertSelectedRowWithHistoricalPalette(combo, selected, theme, "inline");
            Assert.That(ReaderHashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = "feature" })).Select(commit => commit.Hash)));
            Press(main, PhysicalKey.Escape); await Layout(main); Assert.That(combo.IsDropDownOpen, Is.False);
            Assert.That(Hashes(main).Length, Is.EqualTo(2));
            Editor(combo).Focus(); Press(main, PhysicalKey.Enter); await Idle(main);
            Assert.That(combo.Text, Is.EqualTo(chosen));
            Assert.That(ReaderHashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = chosen })).Select(commit => commit.Hash)));
            // Exercise the same item theme when the editable combo itself is
            // hosted by the toolbar's PopupRoot, where the native regression occurred.
            main.Width = 860; await Layout(main);
            Field<ListBox>(main, "RevisionHistory").Focus();
            Press(main, PhysicalKey.E, RawInputModifiers.Control); await Layout(main);
            Press(main, PhysicalKey.E, RawInputModifiers.Control); await Layout(main);
            CompactBrowserToolbar toolbar = Field<CompactBrowserToolbar>(main, "RepositoryToolbar");
            ComboBox overflow = OverflowControls(toolbar).OfType<ComboBox>().Single(control => control.Name == "BranchHistoryFilterOverflow");
            overflow.Text = ""; Editor(overflow).Focus(); Press(main, PhysicalKey.ArrowDown); await Layout(main);
            if (overflow.SelectedIndex < 0) { Press(main, PhysicalKey.ArrowDown); await Layout(main); }
            selected = (ComboBoxItem)overflow.ContainerFromIndex(overflow.SelectedIndex)!;
            await AssertSelectedRowWithHistoricalPalette(overflow, selected, theme, "overflow");
            await ClickRowWithHistoricalPalette(selected, main, theme, "overflow");
            Assert.That(Editor(overflow).Text, Is.EqualTo(overflow.Text));
            Assert.That(Editor(overflow).IsFocused, Is.True, "An overflow pointer choice must leave its real editor ready for Enter.");
            Assert.That(ReaderHashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = chosen })).Select(commit => commit.Hash)), "Palette/pointer selection must still only edit the draft.");
            toolbar.OverflowFlyout.Hide(); main.Width = 1440; await Layout(main);
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
        Assert.That(ReaderHashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = "HEAD", All = false })).Select(commit => commit.Hash)));
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
        Assert.That(ReaderHashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = "" })).Select(commit => commit.Hash)));
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
        MainWindow main = f.Window; string state = await State(f);
        IClipboard clipboard = main.Clipboard!; string? previousClipboard = await ClipboardExtensions.TryGetTextAsync(clipboard);
        try
        {
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                main.RequestedThemeVariant = theme; main.Width = 1440; await Layout(main);
                await Accept(main, "feature"); string[] before = Hashes(main);
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
                // Start the native regression route afresh, after preserving the existing
                // quick/branch alternation and Contains-suggestion assertions above.
                toolbar.OverflowFlyout.Hide(); await Layout(main);
                Branch(main).Text = "feature"; Field<ListBox>(main, "RevisionHistory").Focus();
                Press(main, PhysicalKey.E, RawInputModifiers.Control); await Layout(main);
                Press(main, PhysicalKey.E, RawInputModifiers.Control); await Layout(main);
                Assert.That(Editor(branch).IsFocused, Is.True);
                Press(main, PhysicalKey.A, RawInputModifiers.Control);
                await TypeBranchExpression(main, branch, "main");
                Press(main, PhysicalKey.Escape); await Layout(main);
                Assert.That(branch.IsDropDownOpen, Is.False);
                Press(main, PhysicalKey.A, RawInputModifiers.Control); Press(main, PhysicalKey.C, RawInputModifiers.Control);
                Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("main"), "The actual overflow editor must copy the unaccepted draft.");
                using EditorPointerTrace trace = new(main, branch, "overflow-editor-click-" + theme);
                trace.Record("Before editor click");
                Click(Editor(branch)); await Layout(main); trace.Record("After editor click");
                Capture((TopLevel)branch.GetVisualRoot()!, "branch-filter-overflow-editor-click-" + theme);
                trace.Save();
                Assert.That(trace.MenuClicks, Is.Zero, "Releasing the embedded editor must not activate its enclosing overflow menu command.");
                Assert.That(Editor(branch).Text, Is.EqualTo("main"));
                Assert.That(Editor(branch).IsFocused, Is.True, "Clicking the real overflow editor must retain its focus while suggestions open; no refocusing is permitted before Enter.");
                TopLevel editorRoot = (TopLevel)branch.GetVisualRoot()!;
                Point editorPoint = Editor(branch).TranslatePoint(new Point(Editor(branch).Bounds.Width / 2, Editor(branch).Bounds.Height / 2), editorRoot)!.Value;
                editorRoot.MouseMove(new Point(-10, editorPoint.Y)); await Layout(editorRoot);
                editorRoot.MouseMove(editorPoint); await Layout(editorRoot);
                trace.Record("After pointer re-entered mapped editor"); trace.Save();
                Assert.That(Editor(branch).IsFocused, Is.True, "A real pointer re-entry after mapping suggestions must not let the menu's hover selection steal editor focus.");
                Assert.That(Hashes(main), Is.EqualTo(before), "Clicking the draft must not apply it.");
                Press(main, PhysicalKey.Enter); await Idle(main);
                trace.Record("After Enter"); trace.Save();
                Assert.That(toolbar.OverflowFlyout.IsOpen, Is.False);
                Assert.That(ReaderHashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = "main" })).Select(commit => commit.Hash)));
                main.Width = 1440; await Layout(main); Assert.That(Branch(main).Text, Is.EqualTo("main"));
            }
            Assert.That(await State(f), Is.EqualTo(state));
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, previousClipboard ?? ""); }
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
            main.RequestedThemeVariant = theme;
            foreach (bool overflow in new[] { false, true })
            {
                main.Width = overflow ? 860 : 1440; await Layout(main);
                ComboBox editing = combo;
                if (overflow)
                {
                    Field<ListBox>(main, "RevisionHistory").Focus();
                    Press(main, PhysicalKey.E, RawInputModifiers.Control); await Layout(main);
                    Press(main, PhysicalKey.E, RawInputModifiers.Control); await Layout(main);
                    editing = OverflowControls(Field<CompactBrowserToolbar>(main, "RepositoryToolbar")).OfType<ComboBox>().Single(control => control.Name == "BranchHistoryFilterOverflow");
                }
                Editor(editing).Focus(); Press(main, PhysicalKey.A, RawInputModifiers.Control);
                string[] accepted = Hashes(main);
                await TypeBranchExpression(main, editing, "feature missing-native-branch main ^absent-second");
                Assert.That(combo.Text, Is.EqualTo(editing.Text), "Inline and overflow must preserve the entire multi-token draft.");
                Assert.That(Hashes(main), Is.EqualTo(accepted), "Spaces/text entry must not apply a history filter.");
                Capture((TopLevel)editing.GetVisualRoot()!, "branch-filter-typed-expression-" + (overflow ? "overflow-" : "inline-") + theme);
                Press(main, PhysicalKey.Enter);
                foreach (string token in new[] { "missing-native-branch", "^absent-second" })
                {
                    await Until(() => main.OwnedWindows.Any(window => window.Name == "BranchFilterInvalidReference"));
                    Window warning = main.OwnedWindows.Single(window => window.Name == "BranchFilterInvalidReference"); await Layout(warning);
                    Assert.That(warning.Owner, Is.SameAs(main)); Assert.That(warning.ActualThemeVariant, Is.EqualTo(theme));
                    Assert.That(Field<TextBlock>(warning, "BranchFilterWarningMessage").Text, Does.Contain("\"" + token + "\"").And.Contain("will be ignored"));
                    Capture(warning, "branch-filter-invalid-" + token.TrimStart('^') + "-" + (overflow ? "overflow-" : "inline-") + theme);
                    Click(Field<Button>(warning, "BranchFilterWarningOK")); await Until(() => !warning.IsVisible);
                }
                await Idle(main); Assert.That(combo.Text, Is.EqualTo("feature main"));
                Assert.That(ReaderHashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = "feature main" })).Select(commit => commit.Hash)));
            }
            main.Width = 1440; await Layout(main);
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
        Assert.That(ReaderHashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = "feature..main" })).Select(commit => commit.Hash)));
        string[] good = Hashes(main); Branch(main).Text = "absent..main"; Editor(Branch(main)).Focus(); Press(main, PhysicalKey.Enter);
        await Until(() => !main.IsBusy); Assert.That(main.OwnedWindows, Is.Empty, "Ranges bypass simple-reference warnings in the Windows original.");
        Assert.That(main.LastException, Is.TypeOf<GitCommandException>()); Assert.That(Hashes(main), Is.EqualTo(good));
        Assert.That(Branch(main).Text, Is.EqualTo("absent..main"));
        Branch(main).Text = "main"; Field<ListBox>(main, "RevisionHistory").Focus(); Press(main, PhysicalKey.F5);
        await Until(() => !main.IsBusy); Assert.That(main.LastException, Is.TypeOf<GitCommandException>());
        Assert.That(Hashes(main), Is.EqualTo(good), "Refresh keeps reporting the accepted invalid range; a new draft must not replace it.");
        await Accept(main, "main ^feature");
        Assert.That(ReaderHashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(new GitLogQuery { BranchFilter = "main ^feature" })).Select(commit => commit.Hash)));
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
        // A branch/reflog-only filter retains these preferences but Windows does
        // not emit their traversal flags without FilterInfo.HasRevisionFilter.
        Assert.That(ReaderHashes(main), Is.EqualTo((await new GitRepository(f.Repository).GetLogAsync(query with
        { Revision = null, BranchFilter = "feature", All = true, FullHistory = false, SimplifyMerges = false })).Select(commit => commit.Hash)));
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
    private sealed class EditorPointerTrace : IDisposable
    {
        private readonly MainWindow _main;
        private readonly ComboBox _combo;
        private readonly string _name;
        private readonly Control[] _controls;
        private readonly List<string> _events = [];
        internal int MenuClicks { get; private set; }
        internal EditorPointerTrace(MainWindow main, ComboBox combo, string name)
        {
            _main = main; _combo = combo; _name = name;
            _controls = new Control[] { main, combo, Editor(combo), (Control)combo.GetVisualRoot()! }
                .Concat(combo.GetVisualAncestors().OfType<MenuItem>()).Distinct().ToArray();
            foreach (Control control in _controls)
            {
                control.AddHandler(Control.PointerPressedEvent, Observe, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                control.AddHandler(Control.PointerReleasedEvent, Observe, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                control.AddHandler(InputElement.GotFocusEvent, Observe, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                control.AddHandler(InputElement.LostFocusEvent, Observe, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                control.AddHandler(InputElement.KeyDownEvent, Observe, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                if (control is MenuItem item) { item.Click += MenuClick; }
            }
        }
        private void Observe(object? sender, RoutedEventArgs args)
            => Record((sender as Control)?.GetType().Name + ":" + (sender as Control)?.Name + ":" + args.RoutedEvent?.Name
                + ":" + args.Route + ":handled=" + args.Handled + ":source=" + (args.Source as Control)?.GetType().Name);
        private void MenuClick(object? sender, RoutedEventArgs args) { MenuClicks++; Observe(sender, args); }
        internal void Record(string label) => _events.Add(label + " | " + JsonSerializer.Serialize(new
        {
            ComboText = _combo.Text, EditorText = Editor(_combo).Text, EditorFocus = Editor(_combo).IsFocused,
            EditorSelectionStart = Editor(_combo).SelectionStart, EditorSelectionEnd = Editor(_combo).SelectionEnd,
            Focus = _main.FocusManager?.GetFocusedElement() is Control focus ? focus.GetType().Name + ":" + focus.Name : null,
            _combo.IsDropDownOpen, _combo.SelectedItem,
            MenuSelected = _controls.OfType<MenuItem>().Select(item => new { item.IsSelected, item.IsFocused }).ToArray()
        }));
        internal void Save()
        {
            string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "gitextensions-branch-filter-screenshots");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, _name + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(_events, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.AddTestAttachment(path, "Real editor pointer/focus routes, with no corrective refocus.");
        }
        public void Dispose()
        {
            foreach (Control control in _controls)
            {
                control.RemoveHandler(Control.PointerPressedEvent, Observe); control.RemoveHandler(Control.PointerReleasedEvent, Observe);
                control.RemoveHandler(InputElement.GotFocusEvent, Observe); control.RemoveHandler(InputElement.LostFocusEvent, Observe);
                control.RemoveHandler(InputElement.KeyDownEvent, Observe);
                if (control is MenuItem item) { item.Click -= MenuClick; }
            }
        }
    }
    private static async Task TypeBranchExpression(MainWindow main, ComboBox combo, string expression)
    {
        string typed = ""; bool? spaceHandled = null;
        void ObserveSpace(object? sender, KeyEventArgs args)
        { if (args.Key == Key.Space) { spaceHandled = args.Handled; } }
        combo.AddHandler(Control.KeyDownEvent, ObserveSpace, RoutingStrategies.Bubble, handledEventsToo: true);
        try
        {
            foreach (char character in expression)
            {
                PhysicalKey key = character switch
                { ' ' => PhysicalKey.Space, '-' => PhysicalKey.Minus, '^' => PhysicalKey.Digit6, _ => Enum.Parse<PhysicalKey>(char.ToUpperInvariant(character).ToString()) };
                RawInputModifiers modifiers = character == '^' ? RawInputModifiers.Shift : RawInputModifiers.None;
                bool popupWasOpen = combo.IsDropDownOpen; spaceHandled = null;
                main.KeyPressQwerty(key, modifiers);
                if (character == ' ')
                {
                    Assert.That(spaceHandled, Is.False, "Space must remain available to native TextInput after the editor's KeyDown.");
                    Assert.That(combo.IsDropDownOpen, Is.EqualTo(popupWasOpen), "Space must not select/close the suggestion list like Enter.");
                    Assert.That(combo.Text, Is.EqualTo(typed), "Space KeyDown must not discard/select-all any previously typed tokens.");
                }
                main.KeyTextInput(character.ToString()); main.KeyReleaseQwerty(key, modifiers);
                typed += character; await Layout(main);
                Assert.That(combo.Text, Is.EqualTo(typed), "Every real key/TextInput pair must preserve the exact expression prefix.");
                Assert.That(Editor(combo).Text, Is.EqualTo(typed), "The real editor and combo draft must remain synchronized after every native TextInput.");
                Assert.That(Editor(combo).IsFocused, Is.True);
                Assert.That(Editor(combo).SelectionStart, Is.EqualTo(typed.Length));
                Assert.That(Editor(combo).SelectionEnd, Is.EqualTo(typed.Length));
            }
        }
        finally { combo.RemoveHandler(Control.KeyDownEvent, ObserveSpace); }
    }
    private static async Task AssertSelectedRowWithHistoricalPalette(ComboBox combo, ComboBoxItem item, ThemeVariant theme, string scope)
    {
        Assert.That(item.IsSelected, Is.True);
        Assert.That(Editor(combo).IsFocused, Is.True, "Keyboard navigation of an editable combo keeps focus in its real editor.");
        TopLevel root = (TopLevel)item.GetVisualRoot()!;
        root.MouseMove(new Point(root.Bounds.Width - 2, root.Bounds.Height - 2)); await Layout(root);
        Assert.That(item.IsPointerOver, Is.False, "The selected color must be verified without a hover tint.");
        AssertRowPalette(item, theme);
        Assert.That(Editor(combo).IsFocused, Is.True, "Moving away from the selected popup row must keep the editor available for typing/Enter.");
        AssertRowPalette(item, theme); Capture(root, "branch-filter-selected-focus-orange-" + scope + "-" + theme);
    }

    private static async Task ClickRowWithHistoricalPalette(ComboBoxItem item, MainWindow main, ThemeVariant theme, string scope)
    {
        TopLevel root = (TopLevel)item.GetVisualRoot()!;
        Point point = item.TranslatePoint(new Point(12, item.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point); await Layout(root); Assert.That(item.IsPointerOver, Is.True);
        AssertRowPalette(item, theme); Capture(root, "branch-filter-hover-orange-" + scope + "-" + theme);
        root.MouseDown(point, MouseButton.Left); await Layout(root); AssertRowPalette(item, theme);
        if (root.IsVisible) { Capture(root, "branch-filter-pointer-down-orange-" + scope + "-" + theme); }
        root.MouseUp(point, MouseButton.Left); await Layout(main);
    }

    private static void AssertRowPalette(ComboBoxItem item, ThemeVariant theme)
    {
        ContentPresenter presenter = item.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "PART_ContentPresenter");
        TextBlock label = presenter.GetVisualDescendants().OfType<TextBlock>().Single();
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(theme == ThemeVariant.Dark);
        Color background = Color.Parse(colors["Highlight"]), foreground = Color.Parse(colors[theme == ThemeVariant.Dark ? "WindowText" : "HighlightText"]);
        Assert.That(((ISolidColorBrush)presenter.Background!).Color, Is.EqualTo(background));
        Assert.That(((ISolidColorBrush)presenter.Foreground!).Color, Is.EqualTo(foreground));
        Assert.That(((ISolidColorBrush)label.Foreground!).Color, Is.EqualTo(foreground));
        TopLevel? root = item.GetVisualRoot() as TopLevel;
        if (root?.IsVisible != true) { return; }
        using var frame = root.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        using var pixels = frame!.Lock();
        Point sample = presenter.TranslatePoint(new Point(presenter.Bounds.Width - 12, presenter.Bounds.Height / 2), root)!.Value;
        byte[] pixel = new byte[4]; Marshal.Copy(pixels.Address + (int)sample.Y * pixels.RowBytes + (int)sample.X * 4, pixel, 0, 4);
        bool bgra = pixels.Format == PixelFormat.Bgra8888;
        Assert.That(Color.FromArgb(pixel[3], pixel[bgra ? 2 : 0], pixel[1], pixel[bgra ? 0 : 2]), Is.EqualTo(background), "The actual popup pixel must use the opaque historical highlight, including inside overflow.");
    }

    private sealed class OrangePlatformAccent : IDisposable
    {
        private readonly FluentTheme _theme = Application.Current!.Styles.OfType<FluentTheme>().Single();
        private readonly ColorPaletteResources? _light, _dark;
        private readonly Color _orange = Color.Parse("#e95420");
        internal OrangePlatformAccent()
        {
            _theme.Palettes.TryGetValue(ThemeVariant.Light, out _light); _theme.Palettes.TryGetValue(ThemeVariant.Dark, out _dark);
            _theme.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = _orange };
            _theme.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = _orange };
        }
        internal void AssertActive(ThemeVariant variant)
        {
            Assert.That(((IResourceNode)_theme).TryGetResource("SystemAccentColor", variant, out object? accent), Is.True);
            Assert.That(accent, Is.EqualTo(_orange));
        }
        public void Dispose()
        {
            if (_light is null) { _theme.Palettes.Remove(ThemeVariant.Light); } else { _theme.Palettes[ThemeVariant.Light] = _light; }
            if (_dark is null) { _theme.Palettes.Remove(ThemeVariant.Dark); } else { _theme.Palettes[ThemeVariant.Dark] = _dark; }
        }
    }
    private static TextBox Editor(ComboBox combo) => combo.GetVisualDescendants().OfType<TextBox>().Single();
    private static T Field<T>(TopLevel owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static string[] Hashes(MainWindow main) => Field<ListBox>(main, "RevisionHistory").Items.OfType<GitCommit>().Select(commit => commit.Hash).ToArray();
    // Windows rows follow RevisionGraph scores; GetLogAsync still supplies its native reader order.
    private static string[] ReaderHashes(MainWindow main)
    {
        CommitGraph graph = main.GetVisualDescendants().OfType<CommitGraph>().Single();
        string[] reader = graph.Commits.Select(commit => commit.Hash).ToArray();
        string[] visible = Hashes(main);
        Assert.That(visible, Is.EquivalentTo(reader), "The graph may reorder rows but must keep every commit returned by the reader.");
        Assert.That(visible, Is.EqualTo(graph.OrderedCommits.Select(commit => commit.Hash)), "Visible rows must retain the exact Windows graph-score order.");
        return reader;
    }
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

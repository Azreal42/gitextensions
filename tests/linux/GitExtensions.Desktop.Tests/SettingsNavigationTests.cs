using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class SettingsNavigationTests
{
    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("en", true)]
    [TestCase("fr", false)]
    [TestCase("fr", true)]
    public async Task RealPagesUseTheOriginalHierarchyIconsAndTheWindowTitle(string culture, bool dark)
    {
        await using Scope f = new(culture); (DesktopSettingsDialog dialog, _) = f.Open(environment: true, dark: dark);
        TreeView tree = Field<TreeView>(dialog, "SettingsNavigationTree");
        Assert.That(tree.Items.OfType<TreeViewItem>().Select(Id), Is.EqualTo(new[] { "Checklist", "Git", "Plugins" }));
        TreeViewItem extensions = Nodes(tree).Single(node => Id(node) == "Checklist"), git = Nodes(tree).Single(node => Id(node) == "Git");
        Assert.That(extensions.Items.OfType<TreeViewItem>().Select(Id), Is.EqualTo(new[] { "General", "Appearance", "RevisionLinks", "BuildServerIntegration", "Scripts", "Hotkeys", "Advanced", "Detailed", "GitSsh" }));
        Assert.That(Nodes(tree).Single(node => Id(node) == "Appearance").Items.OfType<TreeViewItem>().Select(Id), Is.EqualTo(new[] { "Sorting", "Colors", "Fonts", "ConsoleStyle" }));
        Assert.That(Nodes(tree).Single(node => Id(node) == "Detailed").Items.OfType<TreeViewItem>().Select(Id), Is.EqualTo(new[] { "Commit", "BlameViewer" }));
        Assert.That(git.Items.OfType<TreeViewItem>().Select(Id), Is.EqualTo(new[] { "GitPaths", "Config" }));
        Assert.That(((Control)extensions.Header!).GetVisualDescendants().OfType<TextBlock>().Single().Text, Is.EqualTo("Git Extensions"));
        Assert.That(dialog.GetVisualDescendants().OfType<TabControl>(), Is.Empty);
        Dictionary<string, string> icons = new()
        {
            ["Checklist"] = "git-extensions-logo-16px.png", ["General"] = "GeneralSettings.png", ["Appearance"] = "Appearance.png",
            ["Sorting"] = "SortBy.png", ["Colors"] = "Colors.png", ["Fonts"] = "Font.png", ["ConsoleStyle"] = "Console.png",
            ["RevisionLinks"] = "Link.png", ["BuildServerIntegration"] = "Integration.png", ["Scripts"] = "Console.png", ["Hotkeys"] = "Hotkey.png",
            ["Detailed"] = "Settings.png", ["Commit"] = "CommitSummary.png", ["BlameViewer"] = "Blame.png", ["Advanced"] = "AdvancedSettings.png", ["Confirmations"] = "BisectGood.png", ["Git"] = "GitLogo16.png", ["GitPaths"] = "FolderOpen.png", ["GitSsh"] = "Key.png", ["Config"] = "GeneralSettings.png", ["Plugins"] = "plugin.png"
        };
        foreach (TreeViewItem node in Nodes(tree))
        {
            Image icon = ((Control)node.Header!).GetVisualDescendants().OfType<Image>().Single();
            Assert.That(icon.Tag, Is.EqualTo(icons[Id(node)])); Assert.That(icon.Source, Is.Not.Null);
            SettingsNavigationTestDriver.Select(dialog, Id(node));
            SettingsNavigationPage page = (SettingsNavigationPage)node.Tag!;
            Assert.That(dialog.Title, Is.EqualTo(TranslationCatalog.LoadDefault().Translate("Settings") + " - " + page.Title));
            Assert.That(Field<ContentControl>(dialog, "SettingsPageContent").Content, Is.SameAs(page.Content));
            if (Id(node) == "Checklist") { await f.WaitForScanAsync(); }
        }
        SettingsNavigationTestDriver.Select(dialog, "Appearance");
        Assert.That(Field<ComboBox>(dialog, "SettingsTheme").IsEffectivelyVisible, Is.True);
        Assert.That(Field<ComboBox>(dialog, "Language").IsEffectivelyVisible, Is.True);
        Capture(dialog, $"settings-navigation-{culture}-{(dark ? "dark" : "light")}.png");
    }

    [AvaloniaTest]
    public void OneClickChangesThePageAndArrowKeysKeepNavigatingTheActualTree()
    {
        using NavigationHost f = new();
        TreeView tree = Field<TreeView>(f.Window, "SettingsNavigationTree");
        SettingsNavigationTestDriver.Select(f.Window, "Sorting");
        Assert.That(f.Navigation.SelectedPage.Id, Is.EqualTo("Sorting")); Assert.That(tree.IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Editors["Sorting"].IsKeyboardFocusWithin, Is.False, "A normal tree click must leave arrow navigation in the tree.");
        Press(f.Window, PhysicalKey.ArrowDown);
        Assert.That(f.Navigation.SelectedPage.Id, Is.EqualTo("Fonts")); Assert.That(tree.IsKeyboardFocusWithin, Is.True);
        Press(f.Window, PhysicalKey.ArrowUp);
        Assert.That(f.Navigation.SelectedPage.Id, Is.EqualTo("Sorting")); Assert.That(tree.IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Accepted, Is.Zero);
    }

    [AvaloniaTest]
    public void ExplicitGotoFocusesTheNewPageAndANormalTreeSelectionDoesNotTakeContentFocus()
    {
        using NavigationHost f = new(); SettingsNavigationTestDriver.Select(f.Window, "General");
        Assert.That(f.Navigation.SelectPage("Fonts", focusContent: true), Is.True); Pump(f.Window);
        Assert.That(f.Editors["Fonts"].IsKeyboardFocusWithin, Is.True); Assert.That(f.Navigation.SelectedPage.Id, Is.EqualTo("Fonts"));
        SettingsNavigationTestDriver.Select(f.Window, "Sorting");
        Assert.That(Field<TreeView>(f.Window, "SettingsNavigationTree").IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Editors["Sorting"].IsKeyboardFocusWithin, Is.False);
        Assert.That(f.Navigation.SelectPage("NotAPortedPage", focusContent: true), Is.False); Pump(f.Window);
        Assert.That(f.Navigation.SelectedPage.Id, Is.EqualTo("Sorting"));
    }

    [AvaloniaTest]
    public void SearchHighlightsAndMatchesEveryKeywordWithoutFilteringAndEnterCyclesWithoutAccepting()
    {
        using NavigationHost f = new(); TreeView tree = Field<TreeView>(f.Window, "SettingsNavigationTree");
        TreeViewItem[] before = Nodes(tree).ToArray(); SettingsNavigationTestDriver.Select(f.Window, "General");
        TextBox find = Field<TextBox>(f.Window, "SettingsFind"); Edit(f.Window, find, "SOURCE code");
        Assert.That(Matches(tree), Is.EqualTo(new[] { "Fonts", "Tools" }));
        Assert.That(Nodes(tree), Is.EqualTo(before), "Search keeps the full navigation tree and its node instances.");
        Assert.That(before.Where(node => Id(node) is "Fonts" or "Tools").All(node => node.IsEffectivelyVisible), Is.True);
        foreach (string expected in new[] { "Fonts", "Tools", "Fonts" })
        {
            Press(f.Window, PhysicalKey.Enter); Pump(f.Window);
            Assert.That(f.Navigation.SelectedPage.Id, Is.EqualTo(expected)); Assert.That(find.IsKeyboardFocusWithin, Is.True);
            Assert.That(f.Accepted, Is.Zero); Assert.That(f.Window.IsVisible, Is.True);
        }
        Edit(f.Window, find, "sort order"); Assert.That(Matches(tree), Is.EqualTo(new[] { "Sorting" }), "Full page-title matching is case insensitive.");
        Press(f.Window, PhysicalKey.Enter); Assert.That(f.Navigation.SelectedPage.Id, Is.EqualTo("Sorting"));
        Edit(f.Window, find, "source absent"); Assert.That(Matches(tree), Is.Empty);
        Press(f.Window, PhysicalKey.Enter); Assert.That(f.Accepted, Is.Zero); Assert.That(find.IsKeyboardFocusWithin, Is.True);
        Edit(f.Window, find, ""); Assert.That(Matches(tree), Is.Empty); Assert.That(Nodes(tree), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public void SearchUsesTheSelectedComboTemplateLabelAndExcludesOtherChoicesHiddenControlsAndDisabledAncestors()
    {
        ComboBox choice = new()
        {
            ItemsSource = new[] { GitLogSort.GitDefault, GitLogSort.AuthorDate }, SelectedItem = GitLogSort.GitDefault,
            ItemTemplate = new FuncDataTemplate<GitLogSort>((value, _) => new TextBlock { Text = value == GitLogSort.GitDefault ? "friendly active label" : "unselected wording" })
        };
        StackPanel content = new() { Children =
        {
            choice,
            new Border { IsVisible = false, Child = new TextBlock { Text = "invisible wording" } },
            new Border { IsEnabled = false, Child = new TextBlock { Text = "disabled wording" } },
            new NumericUpDown { Value = 987 }
        } };
        SettingsNavigationView navigation = new([new("Options", "Options", content, "GeneralSettings.png")], TranslationCatalog.LoadDefault());
        Window window = new() { Width = 850, Height = 500, Content = navigation }; window.Show(); Pump(window);
        try
        {
            TreeView tree = Field<TreeView>(window, "SettingsNavigationTree"); TextBox find = Field<TextBox>(window, "SettingsFind");
            Edit(window, find, "friendly active"); Assert.That(Matches(tree), Is.EqualTo(new[] { "Options" }));
            foreach (string query in new[] { "GitDefault", "AuthorDate", "unselected wording", "invisible wording", "disabled wording", "987" })
            { Edit(window, find, query); Assert.That(Matches(tree), Is.Empty, query); }
            Assert.That(choice.SelectedItem, Is.EqualTo(GitLogSort.GitDefault)); Assert.That(choice.IsDropDownOpen, Is.False);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaTest]
    public async Task NavigationRetainsDraftControlsAndCancelDoesNotPublishThem()
    {
        await using Scope f = new(); (DesktopSettingsDialog dialog, Task<bool> result) = f.Open();
        SettingsNavigationTestDriver.Select(dialog, "Advanced"); ComboBox symbol = Field<ComboBox>(dialog, "SettingsNormaliseSymbol"); AdvancedSettingsUiDriver.ChooseSymbol(dialog, "-");
        SettingsNavigationTestDriver.Select(dialog, "Scripts");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnAdd")); Edit(dialog, Field<TextBox>(dialog, "ScriptName"), "Unsaved navigation script");
        Edit(dialog, Field<TextBox>(dialog, "ScriptCommand"), "/bin/true"); ScriptsSettingsPage scripts = Field<ScriptsSettingsPage>(dialog, "ScriptsSettingsPage");
        SettingsNavigationTestDriver.Select(dialog, "Fonts"); AppearanceFontsSettingsPage fonts = Field<AppearanceFontsSettingsPage>(dialog, "AppearanceFontsSettingsPage");
        PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "ShowEolMarkerAsGlyph"));
        SettingsNavigationTestDriver.Select(dialog, "General"); SettingsNavigationTestDriver.Select(dialog, "Advanced");
        Assert.That(Field<ComboBox>(dialog, "SettingsNormaliseSymbol"), Is.SameAs(symbol)); Assert.That(((AdvancedSymbolChoice)symbol.SelectedItem!).Value, Is.EqualTo("-"));
        SettingsNavigationTestDriver.Select(dialog, "Scripts");
        Assert.That(Field<ScriptsSettingsPage>(dialog, "ScriptsSettingsPage"), Is.SameAs(scripts)); Assert.That(scripts.Draft.Single().Name, Is.EqualTo("Unsaved navigation script"));
        SettingsNavigationTestDriver.Select(dialog, "Fonts");
        Assert.That(Field<AppearanceFontsSettingsPage>(dialog, "AppearanceFontsSettingsPage"), Is.SameAs(fonts)); Assert.That(fonts.Draft.ShowEolMarkerAsGlyph, Is.True);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await result, Is.False);
        Assert.That(f.Publications, Is.Empty); Assert.That(f.FontSaves, Is.Zero); Assert.That(f.ScriptSaves, Is.Zero); Assert.That(f.HotkeySaves, Is.Zero);
    }

    [AvaloniaTest]
    public async Task ApplyPublishesWithoutClosingAndCancelRetainsThatPublicationButDiscardsLaterDrafts()
    {
        await using Scope f = new(); (DesktopSettingsDialog dialog, Task<bool> result) = f.Open();
        EditHistory(dialog, "321"); PushProcessTestDriver.Pointer(Field<Button>(dialog, "ApplyDesktopSettings")); Pump(dialog);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(result.IsCompleted, Is.False); Assert.That(f.Publications.Select(value => value.MaxRevisionGraphCommits), Is.EqualTo(new[] { 321 }));
        Assert.That(dialog.Result.MaxRevisionGraphCommits, Is.EqualTo(321));
        Assert.That(dialog.Result.HistoryLimit, Is.EqualTo(300), "The General ceiling does not change the internal batch size.");
        EditHistory(dialog, "654"); SettingsNavigationTestDriver.Select(dialog, "Advanced");
        AdvancedSettingsUiDriver.ChooseSymbol(dialog, "-");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings"));
        Assert.That(await result, Is.True, "Windows FormSettings reports OK on closing after a successful Apply.");
        Assert.That(f.Publications.Count, Is.EqualTo(1)); Assert.That(dialog.Result.MaxRevisionGraphCommits, Is.EqualTo(321));
        Assert.That(dialog.Result.AutoNormaliseSymbol, Is.EqualTo("_"));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ACorrectedApplyClearsTheEarlierFailureAndPublishesExactlyOnce(bool publicationFailure)
    {
        await using Scope f = new(); (DesktopSettingsDialog dialog, Task<bool> result) = f.Open(); EditHistory(dialog, "321");
        if (publicationFailure) { f.PublishFailure = new IOException("fixture publication failed"); }
        else
        {
            EditQuickSearch(dialog, "-1");
        }
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "ApplyDesktopSettings")); Pump(dialog);
        Assert.That(Field<TextBlock>(dialog, "SettingsNotice").Text, Is.Not.Empty); Assert.That(f.Publications, Is.Empty);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(result.IsCompleted, Is.False);
        if (publicationFailure) { f.PublishFailure = null; }
        else { EditQuickSearch(dialog, "4000"); }
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "ApplyDesktopSettings")); Pump(dialog);
        Assert.That(Field<TextBlock>(dialog, "SettingsNotice").Text, Is.Null.Or.Empty);
        Assert.That(f.Publications.Select(value => value.MaxRevisionGraphCommits), Is.EqualTo(new[] { 321 }));
        Assert.That(dialog.IsVisible, Is.True); PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await result, Is.True);
    }

    [AvaloniaTest]
    public async Task SuccessfulFontApplyAcceptsTheMergedSavedValueAndTheNextApplyDoesNotOverwriteIt()
    {
        await using Scope f = new(); (DesktopSettingsDialog dialog, Task<bool> result) = f.Open();
        SettingsNavigationTestDriver.Select(dialog, "Fonts"); AppearanceFontsSettingsPage fonts = Field<AppearanceFontsSettingsPage>(dialog, "AppearanceFontsSettingsPage");
        PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "ShowEolMarkerAsGlyph")); FontPreferences draft = fonts.Draft;
        FontPreferences merged = draft with { Code = new("Merged external family", 13.25) };
        List<(FontPreferences Baseline, FontPreferences Draft)> attempts = [];
        f.SaveFontsOverride = (baseline, value) =>
        {
            attempts.Add((baseline, value));
            if (attempts.Count == 1) { throw new IOException("first font save failed"); }
            return attempts.Count == 2 ? merged : value;
        };
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "ApplyDesktopSettings")); Pump(dialog);
        Assert.That(Field<TextBlock>(dialog, "FontsNotice").Text, Does.Contain("first font save failed")); Assert.That(f.Publications, Is.Empty);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "ApplyDesktopSettings")); Pump(dialog);
        Assert.That(fonts.Draft, Is.EqualTo(merged)); Assert.That(dialog.FontsResult, Is.EqualTo(merged));
        Assert.That(Field<CheckBox>(dialog, "ShowEolMarkerAsGlyph").IsChecked, Is.True);
        // The merged requested family remains unavailable; saving clears its old write error, not the valid fallback diagnostic.
        Assert.That(Field<TextBlock>(dialog, "FontsNotice").Text,
            Does.Not.Contain("first font save failed").And.Not.Contain("Unable to save")
                .And.Contain("Code font:").And.Contain("fallback"));
        Assert.That(Field<TextBlock>(dialog, "SettingsNotice").Text, Is.Null.Or.Empty);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "ApplyDesktopSettings")); Pump(dialog);
        Assert.That(attempts.Count, Is.EqualTo(3)); Assert.That(attempts[1].Draft, Is.EqualTo(draft));
        Assert.That(attempts[2], Is.EqualTo((merged, merged)), "The second successful Apply must use the merged saved baseline and draft.");
        Assert.That(dialog.FontsResult, Is.EqualTo(merged)); Assert.That(f.Publications.Count, Is.EqualTo(2));
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await result, Is.True);
    }

    [AvaloniaTest]
    public async Task TheHostRemembersTheLastPageButExplicitGotoAndChecklistRequestsTakePrecedence()
    {
        await using Scope f = new(); (DesktopSettingsDialog first, Task<bool> firstDone) = f.Open();
        SettingsNavigationTestDriver.Select(first, "Fonts"); PushProcessTestDriver.Pointer(Field<Button>(first, "CancelDesktopSettings")); Assert.That(await firstDone, Is.False);
        (DesktopSettingsDialog remembered, Task<bool> rememberedDone) = f.Open(initialPage: null);
        Assert.That(remembered.Navigation.SelectedPage.Id, Is.EqualTo("Fonts"));
        PushProcessTestDriver.Pointer(Field<Button>(remembered, "CancelDesktopSettings")); Assert.That(await rememberedDone, Is.False);
        (DesktopSettingsDialog explicitPage, Task<bool> explicitDone) = f.Open(initialPage: "Sorting");
        Assert.That(explicitPage.Navigation.SelectedPage.Id, Is.EqualTo("Sorting"));
        Assert.That(Field<TextBox>(explicitPage, "txtPrioBranchNames").IsEffectivelyVisible, Is.True);
        PushProcessTestDriver.Pointer(Field<Button>(explicitPage, "CancelDesktopSettings")); Assert.That(await explicitDone, Is.False);
        (DesktopSettingsDialog checklist, Task<bool> checklistDone) = f.Open(environment: true, initialPage: "Sorting", showChecklist: true);
        Assert.That(checklist.Navigation.SelectedPage.Id, Is.EqualTo("Checklist")); await f.WaitForScanAsync();
        PushProcessTestDriver.Pointer(Field<Button>(checklist, "CancelDesktopSettings")); Assert.That(await checklistDone, Is.False);
    }

    [AvaloniaTest]
    public async Task TheInstantSaveNoticeIsVisibleOnlyForTheChecklistAndItsStartupToggleDoesNotSavePathDrafts()
    {
        await using Scope f = new(); (DesktopSettingsDialog dialog, Task<bool> result) = f.Open(environment: true);
        TextBlock notice = Field<TextBlock>(dialog, "SettingsInstantSaveNotice");
        Assert.That(notice.IsVisible, Is.False);
        SettingsNavigationTestDriver.Select(dialog, "GitPaths"); Edit(dialog, Field<TextBox>(dialog, "GitPath"), "/literal/pending/git");
        SettingsNavigationTestDriver.Select(dialog, "Checklist"); await f.WaitForScanAsync();
        Assert.That(notice.IsVisible, Is.True); Assert.That(notice.Text, Is.Not.Empty);
        bool before = f.EnvironmentStore.Load().Preferences.CheckAtStartup; PushProcessTestDriver.Pointer(Field<CheckBox>(dialog, "CheckAtStartup"));
        Assert.That(f.EnvironmentStore.Load().Preferences.CheckAtStartup, Is.EqualTo(!before));
        Assert.That(f.EnvironmentStore.Load().Preferences.GitExecutable, Is.EqualTo("/usr/bin/git"));
        foreach (string id in new[] { "General", "Appearance", "Sorting", "Fonts", "Scripts", "Hotkeys", "Advanced", "Git", "GitPaths", "Config" })
        { SettingsNavigationTestDriver.Select(dialog, id); Assert.That(notice.IsVisible, Is.False, id); }
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await result, Is.False);
        Assert.That(f.EnvironmentStore.Load().Preferences.GitExecutable, Is.EqualTo("/usr/bin/git")); Assert.That(f.Publications, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase("General", "RevisionGridQuickSearchTimeout", "-1")]
    [TestCase("Sorting", "txtPrioBranchNames", "[")]
    public async Task FailedValidationSelectsAndFocusesThePageThatNeedsCorrection(string id, string field, string value)
    {
        await using Scope f = new(); (DesktopSettingsDialog dialog, Task<bool> result) = f.Open();
        SettingsNavigationTestDriver.Select(dialog, id);
        Control invalid = Field<Control>(dialog, field);
        TextBox input = invalid as TextBox ?? invalid.GetVisualDescendants().OfType<TextBox>().Single();
        Edit(dialog, input, value);
        SettingsNavigationTestDriver.Select(dialog, "Appearance"); PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveDesktopSettings")); Pump(dialog);
        Assert.That(dialog.Navigation.SelectedPage.Id, Is.EqualTo(id)); Assert.That(input.IsKeyboardFocusWithin, Is.True);
        Assert.That(Field<TextBlock>(dialog, "SettingsNotice").Text, Is.Not.Empty); Assert.That(result.IsCompleted, Is.False); Assert.That(f.Publications, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase("Scripts", "btnAdd")]
    [TestCase("Hotkeys", "bClear")]
    public async Task NarrowSettingsCanScrollToAndOperateTheRightHandPageControls(string id, string buttonName)
    {
        await using Scope f = new(); (DesktopSettingsDialog dialog, Task<bool> result) = f.Open();
        dialog.Width = 740; Pump(dialog); SettingsNavigationTestDriver.Select(dialog, id);
        ContentControl page = Field<ContentControl>(dialog, "SettingsPageContent");
        ScrollViewer scroll = page.GetVisualAncestors().OfType<ScrollViewer>().First();
        Assert.That(scroll.Extent.Width, Is.GreaterThan(scroll.Viewport.Width));
        Point wheel = scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height - 24), dialog)!.Value;
        for (int step = 0; step < 8; step++)
        { dialog.MouseMove(wheel); dialog.MouseWheel(wheel, new Vector(-4, 0), RawInputModifiers.None); Pump(dialog); }
        Assert.That(scroll.Offset.X, Is.GreaterThan(0), "The real horizontal wheel must move the Settings page viewport.");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, buttonName));
        if (id == "Scripts") { Assert.That(Field<ScriptsSettingsPage>(dialog, "ScriptsSettingsPage").Draft.Count, Is.EqualTo(1)); }
        Assert.That(dialog.Navigation.SelectedPage.Id, Is.EqualTo(id));
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await result, Is.False); Assert.That(f.Publications, Is.Empty);
    }

    [AvaloniaTest]
    public async Task NormalWidthKeepsTheDefaultScriptExamplesAndRightHandActionsInsideThePageWithoutScrolling()
    {
        await using Scope f = new(); (DesktopSettingsDialog dialog, Task<bool> result) = f.Open(environment: true, defaultScripts: true);
        Assert.That(dialog.Width, Is.EqualTo(958)); SettingsNavigationTestDriver.Select(dialog, "Scripts");
        ScriptsSettingsPage scripts = Field<ScriptsSettingsPage>(dialog, "ScriptsSettingsPage");
        Assert.That(scripts.Draft, Is.EqualTo(ScriptConfiguration.GetDefaults())); int initialCount = scripts.Draft.Count;
        ContentControl page = Field<ContentControl>(dialog, "SettingsPageContent"); ScrollViewer scroll = page.GetVisualAncestors().OfType<ScrollViewer>().First();
        Assert.That(scroll.Offset.X, Is.Zero); Assert.That(scroll.Extent.Width, Is.LessThanOrEqualTo(scroll.Viewport.Width + 1));
        double addTop = Field<Button>(dialog, "btnAdd").TranslatePoint(default, scroll)!.Value.Y;
        Assert.That(addTop, Is.InRange(0, 50), "The script actions start at the top of the page viewport, without vertical centering or scrolling.");
        Capture(dialog, "settings-navigation-default-scripts-normal-top.png");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnArgumentsHelp"));
        Window help = dialog.OwnedWindows.Single(window => window.Name == "ScriptArgumentsHelp"); Assert.That(help.IsVisible, Is.True); help.Close(); Pump(dialog);
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnAdd")); Assert.That(scripts.Draft.Count, Is.EqualTo(initialCount + 1));
        Edit(dialog, Field<TextBox>(dialog, "ScriptName"), "Open the selected repository files in the configured external source editor");
        SettingsNavigationTestDriver.Select(dialog, "Fonts"); SettingsNavigationTestDriver.Select(dialog, "Scripts");
        Assert.That(Field<ScriptsSettingsPage>(dialog, "ScriptsSettingsPage"), Is.SameAs(scripts)); Assert.That(scroll.Offset.X, Is.Zero);
        Assert.That(scroll.Extent.Width, Is.LessThanOrEqualTo(scroll.Viewport.Width + 1));
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnAdd")); Assert.That(scripts.Draft.Count, Is.EqualTo(initialCount + 2));
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelDesktopSettings")); Assert.That(await result, Is.False);
        Assert.That(f.ScriptSaves, Is.Zero); Assert.That(f.Publications, Is.Empty);
    }

    private static void EditQuickSearch(DesktopSettingsDialog dialog, string value)
    {
        SettingsNavigationTestDriver.Select(dialog, "General");
        Edit(dialog, Field<NumericUpDown>(dialog, "RevisionGridQuickSearchTimeout").GetVisualDescendants().OfType<TextBox>().Single(), value);
    }

    private static void EditHistory(DesktopSettingsDialog dialog, string value)
    {
        SettingsNavigationTestDriver.Select(dialog, "General"); NumericUpDown history = Field<NumericUpDown>(dialog, "_NO_TRANSLATE_MaxCommits");
        Edit(dialog, history.GetVisualDescendants().OfType<TextBox>().Single(), value);
    }
    private static string Id(TreeViewItem node) => ((SettingsNavigationPage)node.Tag!).Id;
    private static IEnumerable<TreeViewItem> Nodes(TreeView tree) => Descendants(tree.Items.OfType<TreeViewItem>());
    private static IEnumerable<TreeViewItem> Descendants(IEnumerable<TreeViewItem> nodes)
        => nodes.SelectMany(node => new[] { node }.Concat(Descendants(node.Items.OfType<TreeViewItem>())));
    private static string[] Matches(TreeView tree) => Nodes(tree).Where(node => ((Control)node.Header!).Classes.Contains("settings-search-match")).Select(Id).ToArray();
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static void Edit(Window window, TextBox input, string value)
    {
        input.BringIntoView(); Pump(window); PushProcessTestDriver.Pointer(input); Press(window, PhysicalKey.A, RawInputModifiers.Control);
        Press(window, PhysicalKey.Backspace); if (value.Length > 0) { window.KeyTextInput(value); } Pump(window); Assert.That(input.Text, Is.EqualTo(value));
    }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(TopLevel window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string file = Path.Combine(directory, name); frame!.Save(file); TestContext.AddTestAttachment(file);
    }

    private sealed class NavigationHost : IDisposable
    {
        internal Window Window { get; }
        internal SettingsNavigationView Navigation { get; }
        internal Dictionary<string, TextBox> Editors { get; } = new[] { "General", "Appearance", "Sorting", "Fonts", "Tools" }.ToDictionary(id => id, id => new TextBox { Name = id + "Editor", Text = id });
        internal int Accepted { get; private set; }
        internal NavigationHost()
        {
            SettingsNavigationPage general = new("General", "General", Editors["General"], "GeneralSettings.png");
            SettingsNavigationPage appearance = new("Appearance", "Appearance", Editors["Appearance"], "Appearance.png", Children:
            [
                new("Sorting", "Sort order", Editors["Sorting"], "SortBy.png", SearchKeywords: ["branch order", "remote"]),
                new("Fonts", "Formatting", Editors["Fonts"], "Font.png", SearchKeywords: ["source code", "font family"])
            ]);
            SettingsNavigationPage tools = new("Tools", "Tools", Editors["Tools"], "GitLogo16.png", SearchKeywords: ["source", "code"]);
            Navigation = new([general, appearance, tools], TranslationCatalog.LoadDefault());
            Button accept = new() { Content = "OK", IsDefault = true }; accept.Click += (_, _) => Accepted++;
            DockPanel root = new(); DockPanel.SetDock(accept, Dock.Bottom); root.Children.Add(accept); root.Children.Add(Navigation);
            Window = new() { Width = 850, Height = 500, Content = root }; Window.Show(); Pump(Window);
        }
        public void Dispose() { Window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    private sealed class Scope : IAsyncDisposable
    {
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly string? _uiOverride = Environment.GetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE");
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly GitEnvironmentOptions _options = GitEnvironment.Options;
        private readonly List<DesktopSettingsDialog> _dialogs = [];
        private readonly List<GitEnvironmentSettingsSession> _sessions = [];
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-settings-tree-" + Guid.NewGuid().ToString("N"));
        private readonly TranslationCatalog _translations;
        private readonly Window _owner = new() { Width = 1100, Height = 800 };
        internal GitEnvironmentPreferencesStore EnvironmentStore { get; }
        internal List<DesktopPreferences> Publications { get; } = [];
        internal int FontSaves { get; private set; }
        internal int ScriptSaves { get; private set; }
        internal int HotkeySaves { get; private set; }
        internal Exception? PublishFailure { get; set; }
        internal Func<FontPreferences, FontPreferences, FontPreferences>? SaveFontsOverride { get; set; }
        internal Scope(string culture = "en")
        {
            Directory.CreateDirectory(_root); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);
            Environment.SetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE", null); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            _translations = TranslationCatalog.LoadDefault(); EnvironmentStore = new(Path.Combine(_root, "environment"));
            EnvironmentStore.SaveChanges(EnvironmentStore.Load(), new() { GitExecutable = "/usr/bin/git", CheckAtStartup = false });
            _owner.Show(); Pump(_owner);
        }
        internal (DesktopSettingsDialog Dialog, Task<bool> Completion) Open(bool environment = false, bool dark = false, string? initialPage = "General", bool showChecklist = false, bool defaultScripts = false)
        {
            GitEnvironmentSettingsSession? session = null;
            if (environment) { session = new(EnvironmentStore, _translations, () => null); _sessions.Add(session); }
            DesktopSettingsDialog dialog = new(new() { Language = "English" }, _translations, false,
                fileHistoryPreferences: new(), fonts: new(), hotkeys: new(), scripts: new(defaultScripts ? ScriptConfiguration.Read(null) : new([], ScriptConfigurationSource.Xml), false, ReadOnlyMemory<byte>.Empty),
                saveHotkeys: _ => HotkeySaves++, saveFonts: (baseline, draft) => { FontSaves++; return SaveFontsOverride?.Invoke(baseline, draft) ?? draft; },
                saveScripts: (baseline, draft) => { ScriptSaves++; return new(new(draft, ScriptConfigurationSource.Xml), baseline.Existed, baseline.OriginalBytes); },
                configureGitTools: _ => Task.CompletedTask, gitEnvironment: session,
                publishSettings: settings => { if (PublishFailure is { } error) { throw error; } Publications.Add(settings.Result); return Task.CompletedTask; }, initialPage: initialPage, showChecklist: showChecklist)
            { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            _dialogs.Add(dialog); Task<bool> completion = dialog.ShowDialog<bool>(_owner); Pump(dialog); return (dialog, completion);
        }
        internal async Task WaitForScanAsync()
        {
            foreach (GitEnvironmentSettingsSession session in _sessions) { await session.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(15)); }
            Dispatcher.UIThread.RunJobs();
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (GitEnvironmentSettingsSession session in _sessions.Where(session => !session.Page.IsInvalidated)) { session.Cancel(); }
                foreach (DesktopSettingsDialog dialog in _dialogs.Where(dialog => dialog.IsVisible)) { dialog.Close(false); }
                await WaitForScanAsync(); Dispatcher.UIThread.RunJobs();
                foreach (DesktopSettingsDialog dialog in _dialogs.Where(dialog => dialog.IsVisible)) { dialog.Close(false); }
                _owner.Close(); foreach (GitEnvironmentSettingsSession session in _sessions) { session.Dispose(); }
                Dispatcher.UIThread.RunJobs();
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); Environment.SetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE", _uiOverride);
                CultureInfo.CurrentUICulture = _culture; GitEnvironment.Configure(_options); Directory.Delete(_root, true);
            }
        }
    }
}

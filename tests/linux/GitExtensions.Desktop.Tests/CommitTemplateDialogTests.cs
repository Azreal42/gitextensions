using System.Globalization;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
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
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommitTemplateDialogTests
{
    [AvaloniaTest]
    [TestCase(false, "en")]
    [TestCase(true, "fr-FR")]
    public async Task OwnedTwoTabsEditTheTenthSlotAndAllSixOptionsThenReopenFromTheRealMenu(bool dark, string culture)
    {
        await using Scope f = await Scope.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CommitDialog commit = await f.OpenAsync(dark); string material = await f.MaterialAsync();
        CommitMessageSettingsDialog settings = await OpenSettings(commit);
        Assert.That(settings.Owner, Is.SameAs(commit)); Assert.That(settings.ActualThemeVariant, Is.EqualTo(commit.ActualThemeVariant));
        Assert.That(Field<ComboBox>(settings, "_NO_TRANSLATE_comboBoxCommitTemplates").Items, Has.Count.EqualTo(10));
        await Choose(settings, Field<ComboBox>(settings, "_NO_TRANSLATE_comboBoxCommitTemplates"), 9);
        Edit(settings, "_NO_TRANSLATE_textBoxCommitTemplateName", "duplicate name"); Edit(settings, "_NO_TRANSLATE_textCommitTemplateText", "Issue {{(main)}}\n\nLiteral body λ");
        PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "checkBoxRegexEnabled"));
        Assert.That(ToolTip.GetTip(Field<CheckBox>(settings, "checkBoxRegexEnabled")), Does.Contain("{{regex}}"));
        Capture(settings, "commit-template-settings-" + (dark ? "Dark" : "Light") + ".png");
        OpenTab(settings, "tabPage2"); EditNumber(settings, "_NO_TRANSLATE_numericMaxFirstLineLength", "12"); EditNumber(settings, "_NO_TRANSLATE_numericMaxLineLength", "18");
        PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "checkBoxAutoWrap"));
        Edit(settings, "_NO_TRANSLATE_textBoxCommitValidationRegex", "^Issue");
        PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "checkBoxUseIndent")); PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "checkBoxSecondLineEmpty"));
        Capture(settings, "commit-template-validation-" + (dark ? "Dark" : "Light") + ".png");
        PushProcessTestDriver.Pointer(Field<Button>(settings, "buttonOk")); await Until(() => !settings.IsVisible);
        var saved = f.Store.Load(); Assert.That(saved.Templates[9], Is.EqualTo(new CommitMessageTemplate("duplicate name", "Issue {{(main)}}\n\nLiteral body λ", true)));
        Assert.That(saved.MaxFirstLineLength, Is.EqualTo(12)); Assert.That(saved.MaxLineLength, Is.EqualTo(18)); Assert.That(saved.AutoWrap, Is.False);
        Assert.That(saved.ValidationRegex, Is.EqualTo("^Issue")); Assert.That(saved.IndentAfterFirstLine, Is.False); Assert.That(saved.SecondLineMustBeEmpty, Is.True);
        settings = await OpenSettings(commit); await Choose(settings, Field<ComboBox>(settings, "_NO_TRANSLATE_comboBoxCommitTemplates"), 9);
        Assert.That(Field<TextBox>(settings, "_NO_TRANSLATE_textCommitTemplateText").Text, Is.EqualTo(saved.Templates[9].Text));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "buttonCancel")); Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task CancelAndEscapeDiscardDraftAndANewMissingProfileRemainsAbsent(bool escape)
    {
        await using Scope f = await Scope.CreateAsync(); CommitDialog commit = await f.OpenAsync(); string material = await f.MaterialAsync();
        var settings = await OpenSettings(commit); Edit(settings, "_NO_TRANSLATE_textBoxCommitTemplateName", "discard"); Edit(settings, "_NO_TRANSLATE_textCommitTemplateText", "draft body");
        if (escape) { Press(settings, PhysicalKey.Escape); } else { PushProcessTestDriver.Pointer(Field<Button>(settings, "buttonCancel")); }
        await Until(() => !settings.IsVisible); Assert.That(File.Exists(f.Store.FilePath), Is.False); Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
        settings = await OpenSettings(commit); Assert.That(Field<TextBox>(settings, "_NO_TRANSLATE_textCommitTemplateText").Text, Is.Empty.Or.Null);
        PushProcessTestDriver.Pointer(Field<Button>(settings, "buttonCancel"));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task EscapeClosesTheRealSlotsPopupBeforeCancellingTheOwnedDialogWithoutSavingItsDraft(bool dark)
    {
        await using Scope f = await Scope.CreateAsync();
        var baseline = f.Store.Load(); f.Store.SaveChanges(baseline, baseline.WithTemplate(9, new("saved name", "saved body")));
        byte[] profile = File.ReadAllBytes(f.Store.FilePath); DateTime written = File.GetLastWriteTimeUtc(f.Store.FilePath);
        CommitDialog commit = await f.OpenAsync(dark, owned: true); string material = await f.MaterialAsync();
        CommitMessageSettingsDialog settings = await OpenSettings(commit);
        ComboBox slots = Field<ComboBox>(settings, "_NO_TRANSLATE_comboBoxCommitTemplates");
        await Choose(settings, slots, 9);
        Edit(settings, "_NO_TRANSLATE_textBoxCommitTemplateName", "unsaved name");
        Edit(settings, "_NO_TRANSLATE_textCommitTemplateText", "unsaved body λ");
        CommitMessageTemplate draft = new("unsaved name", "unsaved body λ");
        Assert.That(settings.Draft.Templates[9], Is.EqualTo(draft));
        PushProcessTestDriver.Pointer(slots); Pump(settings);
        Popup popup = slots.GetVisualDescendants().OfType<Popup>().Single(item => item.Name == "PART_Popup");
        Assert.That(slots.IsDropDownOpen, Is.True); Assert.That(popup.IsOpen, Is.True);
        Assert.That(popup.Child, Is.Not.Null); Assert.That(popup.Child!.IsEffectivelyVisible, Is.True);
        Press(settings, PhysicalKey.Escape);
        Assert.That(settings.IsVisible, Is.True); Assert.That(settings.Owner, Is.SameAs(commit));
        Assert.That(slots.IsDropDownOpen, Is.False); Assert.That(popup.IsOpen, Is.False);
        Assert.That(slots.IsFocused, Is.True, "Dismissing the popup must retain keyboard focus in the settings dialog.");
        Assert.That(slots.SelectedIndex, Is.EqualTo(9)); Assert.That(settings.Draft.Templates[9], Is.EqualTo(draft));
        Assert.That(Field<TextBox>(settings, "_NO_TRANSLATE_textBoxCommitTemplateName").Text, Is.EqualTo(draft.Name));
        Assert.That(Field<TextBox>(settings, "_NO_TRANSLATE_textCommitTemplateText").Text, Is.EqualTo(draft.Text));
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(profile));
        Press(settings, PhysicalKey.Escape); await Until(() => !settings.IsVisible);
        Assert.That(commit.IsVisible, Is.True); Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(profile));
        Assert.That(File.GetLastWriteTimeUtc(f.Store.FilePath), Is.EqualTo(written));
        Assert.That(f.Store.Load().Templates[9], Is.EqualTo(new CommitMessageTemplate("saved name", "saved body")));
        Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    public async Task StoredHighLimitsArePreservedWithoutAllowingNewOutOfRangeValuesOrCrashingTheDialog()
    {
        await using Scope f = await Scope.CreateAsync(); var baseline = f.Store.Load(); f.Store.SaveChanges(baseline, baseline with { MaxLineLength = 200 });
        CommitDialog commit = await f.OpenAsync(); var settings = await OpenSettings(commit);
        Assert.That(Field<TextBlock>(settings, "CommitMessageSettingsNotice").Text, Does.Contain("0–100"));
        Edit(settings, "_NO_TRANSLATE_textBoxCommitTemplateName", "unrelated template edit"); PushProcessTestDriver.Pointer(Field<Button>(settings, "buttonOk"));
        await Until(() => !settings.IsVisible); Assert.That(f.Store.Load().MaxLineLength, Is.EqualTo(200));
        settings = await OpenSettings(commit); OpenTab(settings, "tabPage2");
        Assert.That(Field<NumericUpDown>(settings, "_NO_TRANSLATE_numericMaxFirstLineLength").Maximum, Is.EqualTo(100));
        Assert.That(Field<NumericUpDown>(settings, "_NO_TRANSLATE_numericMaxLineLength").Value, Is.EqualTo(200));
        byte[] before = File.ReadAllBytes(f.Store.FilePath); EditNumber(settings, "_NO_TRANSLATE_numericMaxLineLength", "151");
        PushProcessTestDriver.Pointer(Field<Button>(settings, "buttonOk")); Assert.That(settings.IsVisible, Is.True); Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(before));
        EditNumber(settings, "_NO_TRANSLATE_numericMaxLineLength", "100"); PushProcessTestDriver.Pointer(Field<Button>(settings, "buttonOk"));
        await Until(() => !settings.IsVisible); Assert.That(f.Store.Load().MaxLineLength, Is.EqualTo(100));
    }

    [AvaloniaTest]
    public async Task TwoOpenCommitWindowsConsumeSavedFormattingAndValidationWithoutOpeningTheSecondTemplatesMenu()
    {
        await using Scope f = await Scope.CreateAsync(); await File.AppendAllTextAsync(f.Git.File, "change\n"); await f.Git.Repository.StageAllAsync();
        CommitDialog first = await f.OpenAsync(), second = await f.OpenAsync(); string material = await f.MaterialAsync();
        var settings = await OpenSettings(first); OpenTab(settings, "tabPage2");
        EditNumber(settings, "_NO_TRANSLATE_numericMaxFirstLineLength", "4");
        Edit(settings, "_NO_TRANSLATE_textBoxCommitValidationRegex", "^Approved");
        PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "checkBoxUseIndent"));
        PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "checkBoxSecondLineEmpty"));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "buttonOk")); await Until(() => !settings.IsVisible);
        Edit(second, "Message", "Long subject\nbody"); Assert.That(Field<TextBox>(second, "Message").Text, Is.EqualTo("Long subject\n\nbody"));
        PushProcessTestDriver.Pointer(Field<Button>(second, "Commit")); await Until(() => second.OwnedWindows.Any());
        Window warning = second.OwnedWindows.Single(); Assert.That(Field<TextBlock>(warning, "CommitValidationMessage").Text, Does.Contain("too many"));
        PushProcessTestDriver.Pointer(Field<Button>(warning, "CommitValidationNo")); await Idle(second);
        settings = await OpenSettings(first); OpenTab(settings, "tabPage2"); EditNumber(settings, "_NO_TRANSLATE_numericMaxFirstLineLength", "0");
        PushProcessTestDriver.Pointer(Field<Button>(settings, "buttonOk")); await Until(() => !settings.IsVisible);
        PushProcessTestDriver.Pointer(Field<Button>(second, "Commit")); await Until(() => second.OwnedWindows.Any());
        warning = second.OwnedWindows.Single(); Assert.That(Field<TextBlock>(warning, "CommitValidationMessage").Text, Does.Contain("RegEx"));
        PushProcessTestDriver.Pointer(Field<Button>(warning, "CommitValidationNo")); await Idle(second);
        Assert.That(first.HasCommitted || second.HasCommitted, Is.False); Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    public async Task CorruptionAndARealIoFaultRemainVisibleWithoutLosingTheDraftOrReplacingTheProfile()
    {
        await using Scope f = await Scope.CreateAsync(); Directory.CreateDirectory(Path.GetDirectoryName(f.Store.FilePath)!); File.WriteAllText(f.Store.FilePath, "{keep exact corruption");
        byte[] corrupt = File.ReadAllBytes(f.Store.FilePath); CommitDialog commit = await f.OpenAsync();
        Assert.That(Field<TextBlock>(commit, "CommitTemplateNotice").Text, Is.Not.Empty);
        var settings = await OpenSettings(commit); Assert.That(Field<TextBlock>(settings, "CommitMessageSettingsNotice").Text, Is.Not.Empty);
        Edit(settings, "_NO_TRANSLATE_textBoxCommitTemplateName", "do not erase corruption"); PushProcessTestDriver.Pointer(Field<Button>(settings, "buttonOk"));
        Assert.That(settings.IsVisible, Is.True); Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(corrupt));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "buttonCancel")); File.Delete(f.Store.FilePath);
        settings = await OpenSettings(commit); Edit(settings, "_NO_TRANSLATE_textBoxCommitTemplateName", "retained draft");
        Directory.CreateDirectory(f.Store.FilePath); string sentinel = Path.Combine(f.Store.FilePath, "keep"); File.WriteAllText(sentinel, "keep λ");
        PushProcessTestDriver.Pointer(Field<Button>(settings, "buttonOk")); Assert.That(settings.IsVisible, Is.True);
        Assert.That(Field<TextBox>(settings, "_NO_TRANSLATE_textBoxCommitTemplateName").Text, Is.EqualTo("retained draft"));
        Assert.That(File.ReadAllText(sentinel), Is.EqualTo("keep λ")); Assert.That(Field<TextBlock>(settings, "CommitMessageSettingsNotice").Text, Is.Not.Empty);
        PushProcessTestDriver.Pointer(Field<Button>(settings, "buttonCancel"));
    }

    [AvaloniaTest]
    public async Task CorruptionDiscoveredAtCommitKeepsItsDiagnosticAndBytesWithoutIntroducingANewVeto()
    {
        await using Scope f = await Scope.CreateAsync(); await File.AppendAllTextAsync(f.Git.File, "change\n"); await f.Git.Repository.StageAllAsync();
        CommitDialog commit = await f.OpenAsync(); Edit(commit, "Message", "Valid subject");
        Directory.CreateDirectory(Path.GetDirectoryName(f.Store.FilePath)!); File.WriteAllText(f.Store.FilePath, "{preserve late corruption");
        byte[] before = File.ReadAllBytes(f.Store.FilePath); PushProcessTestDriver.Pointer(Field<Button>(commit, "Commit")); await Idle(commit);
        Assert.That(commit.HasCommitted, Is.True); Assert.That(commit.LastError, Is.Null);
        Assert.That(Field<TextBlock>(commit, "CommitTemplateNotice").IsVisible, Is.True); Assert.That(Field<TextBlock>(commit, "CommitTemplateNotice").Text, Is.Not.Empty);
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(before)); Assert.That((await f.Git.GitAsync("log", "-1", "--pretty=%s")).StandardOutput.TrimEnd(), Is.EqualTo("Valid subject"));
    }

    [AvaloniaTest]
    public async Task DuplicateNamesEmptyBodiesAndRegexTemplatesReplaceUndoablyWithoutBecomingTheGitFileTemplate()
    {
        await using Scope f = await Scope.CreateAsync(); var baseline = f.Store.Load();
        f.Store.SaveChanges(baseline, baseline.WithTemplate(0, new("duplicate", "#123 named body\n\nkeep issue"))
            .WithTemplate(1, new("duplicate", "")).WithTemplate(2, new("regex", "{{(main)}} then {{[}}", true)));
        CommitDialog commit = await f.OpenAsync(); Edit(commit, "Message", "previous draft"); string material = await f.MaterialAsync();
        MenuItem menu = OpenTemplates(commit); var entries = menu.Items.OfType<MenuItem>().Where(i => i.Name == "UserCommitTemplate").ToArray();
        Assert.That(entries, Has.Length.EqualTo(3)); Assert.That(entries.Take(2).Select(i => i.Header), Is.EqualTo(new[] { "duplicate", "duplicate" }));
        PushProcessTestDriver.Pointer(entries[0]); await Idle(commit); Assert.That(Field<TextBox>(commit, "Message").Text, Does.StartWith("#123"));
        Assert.That(Field<Button>(commit, "Commit").IsEnabled, Is.True, "A named application template is not the unchanged Git file template.");
        Assert.That(Field<TextBox>(commit, "Message").IsFocused, Is.True); Press(commit, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.That(Field<TextBox>(commit, "Message").Text, Is.EqualTo("previous draft"));
        menu = OpenTemplates(commit); PushProcessTestDriver.Pointer(menu.Items.OfType<MenuItem>().Where(i => i.Name == "UserCommitTemplate").ElementAt(1)); await Idle(commit);
        Assert.That(Field<TextBox>(commit, "Message").Text, Is.Empty);
        menu = OpenTemplates(commit); PushProcessTestDriver.Pointer(menu.Items.OfType<MenuItem>().Single(i => i.Name == "UserCommitTemplate" && ((CommitMessageTemplate)i.Tag!).Name == "regex")); await Idle(commit);
        Assert.That(Field<TextBox>(commit, "Message").Text, Is.EqualTo("main then {{[}}")); Assert.That(Field<TextBlock>(commit, "CommitTemplateNotice").Text, Is.Not.Empty);
        Assert.That(commit.LastError, Is.Null); Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    public async Task NamedIssueNumberTemplateCreatesARealCommitWithoutCommentStripping()
    {
        await using Scope f = await Scope.CreateAsync(); var baseline = f.Store.Load(); f.Store.SaveChanges(baseline, baseline.WithTemplate(0, new("issue", "#123 Track issue\nbody")));
        await File.AppendAllTextAsync(f.Git.File, "change\n"); await f.Git.Repository.StageAllAsync(); CommitDialog commit = await f.OpenAsync();
        var menu = OpenTemplates(commit); PushProcessTestDriver.Pointer(menu.Items.OfType<MenuItem>().Single(i => i.Name == "UserCommitTemplate")); await Idle(commit);
        PushProcessTestDriver.Pointer(Field<Button>(commit, "Commit")); await Idle(commit);
        Assert.That(commit.HasCommitted, Is.True); Assert.That((await f.Git.GitAsync("log", "-1", "--pretty=%B")).StandardOutput, Does.StartWith("#123 Track issue\n\nbody"));
        Assert.That(f.Store.Load().Templates[0].Text, Is.EqualTo("#123 Track issue\nbody"));
    }

    [AvaloniaTest]
    public async Task ConventionalHotkeysSelectFeatBeforeChangingTextAndPreserveBodyCaretAndBothHints()
    {
        await using Scope f = await Scope.CreateAsync(); CommitDialog commit = await f.OpenAsync(); Edit(commit, "Message", "fix: subject\n\nbody λ");
        Press(commit, PhysicalKey.Home, RawInputModifiers.Control); string material = await f.MaterialAsync();
        Press(commit, PhysicalKey.T, RawInputModifiers.Control);
        MenuItem menu = Templates(commit), conventional = menu.Items.OfType<MenuItem>().Single(i => i.Name == "ConventionalCommits");
        Assert.That(menu.IsSubMenuOpen && conventional.IsSubMenuOpen, Is.True); Assert.That(Field<TextBox>(commit, "Message").Text, Is.EqualTo("fix: subject\n\nbody λ"));
        MenuItem feat = conventional.Items.OfType<MenuItem>().Single(i => i.Name == "Conventional_feat");
        Assert.That(feat.IsSelected, Is.True); Assert.That(feat.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_InputGestureText").Text, Is.EqualTo("Ctrl+T, Ctrl+Shift+T"));
        Press(commit, PhysicalKey.Enter); Assert.That(Field<TextBox>(commit, "Message").Text, Is.EqualTo("feat: subject\n\nbody λ"));
        Press(commit, PhysicalKey.T, RawInputModifiers.Control | RawInputModifiers.Shift); Press(commit, PhysicalKey.Enter);
        TextBox message = Field<TextBox>(commit, "Message"); Assert.That(message.Text, Is.EqualTo("feat(): subject\n\nbody λ")); Assert.That(message.CaretIndex, Is.EqualTo(5));
        menu = OpenTemplates(commit); conventional = menu.Items.OfType<MenuItem>().Single(i => i.Name == "ConventionalCommits"); PushProcessTestDriver.Pointer(conventional);
        PushProcessTestDriver.Pointer(conventional.Items.OfType<MenuItem>().Single(i => i.Name == "ConventionalFooter_Reviewed-by"));
        Assert.That(message.Text, Does.EndWith("\nReviewed-by: ")); int caret = message.CaretIndex;
        menu = OpenTemplates(commit); conventional = menu.Items.OfType<MenuItem>().Single(i => i.Name == "ConventionalCommits"); PushProcessTestDriver.Pointer(conventional);
        PushProcessTestDriver.Pointer(conventional.Items.OfType<MenuItem>().Single(i => i.Name == "ConventionalSkipCi"));
        Assert.That(message.Text, Does.EndWith("\n[skip ci]")); Assert.That(message.CaretIndex, Is.EqualTo(caret));
        Assert.That(message.IsFocused, Is.True); Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ConventionalShortcutAfterPointingAtANativeTemplateAndUndoingItsTextSelectsTheFeatAncestorsAndEnter(bool withScope)
    {
        await using Scope f = await Scope.CreateAsync(); var baseline = f.Store.Load();
        f.Store.SaveChanges(baseline, baseline.WithTemplate(0, new("Native template", "Native template body")));
        CommitDialog commit = await f.OpenAsync(); Edit(commit, "Message", "unstaged title\n\nbody λ");
        TextBox message = Field<TextBox>(commit, "Message"); string material = await f.MaterialAsync();
        MenuItem menu = OpenTemplates(commit);
        MenuItem native = menu.Items.OfType<MenuItem>().Single(item => item.Name == "UserCommitTemplate");
        PushProcessTestDriver.Pointer(native); await Idle(commit);
        Assert.That(message.Text, Is.EqualTo("Native template body")); Assert.That(message.IsFocused, Is.True);
        // The real pointer stays at the selected parent-entry position: only native key input follows.
        Press(commit, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.That(message.Text, Is.EqualTo("unstaged title\n\nbody λ")); int caretBeforeShortcut = message.CaretIndex;
        Press(commit, PhysicalKey.T, RawInputModifiers.Control | (withScope ? RawInputModifiers.Shift : RawInputModifiers.None));
        Menu toolbar = Field<Menu>(commit, "CommitToolbar"); menu = Templates(commit);
        MenuItem conventional = menu.Items.OfType<MenuItem>().Single(item => item.Name == "ConventionalCommits");
        MenuItem feat = conventional.Items.OfType<MenuItem>().Single(item => item.Name == "Conventional_feat");
        MenuItem reopenedNative = menu.Items.OfType<MenuItem>().Single(item => item.Name == "UserCommitTemplate");
        Assert.That(toolbar.SelectedItem, Is.SameAs(menu));
        Assert.That(menu.IsSubMenuOpen && conventional.IsSubMenuOpen, Is.True);
        Assert.That(menu.SelectedIndex, Is.EqualTo(menu.Items.IndexOf(conventional)));
        Assert.That(menu.SelectedItem, Is.SameAs(conventional)); Assert.That(reopenedNative.IsSelected, Is.False);
        Assert.That(conventional.SelectedIndex, Is.EqualTo(conventional.Items.IndexOf(feat)));
        Assert.That(conventional.SelectedItem, Is.SameAs(feat)); Assert.That(feat.IsSelected && feat.IsFocused, Is.True);
        Assert.That(message.Text, Is.EqualTo("unstaged title\n\nbody λ"), "Opening the submenu must not reapply the hovered template or change the message.");
        Press(commit, PhysicalKey.Enter);
        Assert.That(message.Text, Is.EqualTo(withScope ? "feat(): unstaged title\n\nbody λ" : "feat: unstaged title\n\nbody λ"));
        Assert.That(message.CaretIndex, Is.EqualTo(withScope ? 5 : caretBeforeShortcut + 6)); Assert.That(message.IsFocused, Is.True);
        Assert.That(menu.IsSubMenuOpen || conventional.IsSubMenuOpen, Is.False);
        Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task TheFirstRealPointerMotionOrPressAfterTheConventionalShortcutRestoresNormalTemplateChoice(bool moveToAnotherRow)
    {
        await using Scope f = await Scope.CreateAsync(); var baseline = f.Store.Load();
        f.Store.SaveChanges(baseline, baseline.WithTemplate(0, new("Native template", "Native body")).WithTemplate(1, new("Other template", "Other body")));
        CommitDialog commit = await f.OpenAsync(); Edit(commit, "Message", "subject"); string material = await f.MaterialAsync();
        MenuItem menu = OpenTemplates(commit); MenuItem native = menu.Items.OfType<MenuItem>().First(item => item.Name == "UserCommitTemplate");
        PixelPoint stationary = native.PointToScreen(new Point(native.Bounds.Width / 2, native.Bounds.Height / 2));
        PushProcessTestDriver.Pointer(native); await Idle(commit); Press(commit, PhysicalKey.Z, RawInputModifiers.Control);
        Press(commit, PhysicalKey.T, RawInputModifiers.Control);
        MenuKeyboardSelection guard = KeyboardSelection(commit); Assert.That(guard.IsActive, Is.True);
        menu = Templates(commit); MenuItem chosen = menu.Items.OfType<MenuItem>().Single(item => item.Name == "UserCommitTemplate"
            && ((CommitMessageTemplate)item.Tag!).Name == (moveToAnotherRow ? "Other template" : "Native template"));
        if (moveToAnotherRow)
        {
            Assert.That(chosen.PointToScreen(new Point(chosen.Bounds.Width / 2, chosen.Bounds.Height / 2)), Is.Not.EqualTo(stationary));
            Hover(chosen); Assert.That(guard.IsActive, Is.False); Assert.That(menu.SelectedItem, Is.SameAs(chosen));
        }
        else { Assert.That(chosen.PointToScreen(new Point(chosen.Bounds.Width / 2, chosen.Bounds.Height / 2)), Is.EqualTo(stationary)); }
        PushProcessTestDriver.Pointer(chosen); await Idle(commit);
        Assert.That(guard.IsActive, Is.False); Assert.That(Field<TextBox>(commit, "Message").Text, Is.EqualTo(moveToAnotherRow ? "Other body" : "Native body"));
        Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    public async Task EscapeAndClosingTheCommitRetireItsKeyboardMenuSelectionGuard()
    {
        await using Scope f = await Scope.CreateAsync(); CommitDialog commit = await f.OpenAsync(); Edit(commit, "Message", "subject");
        string material = await f.MaterialAsync(); Press(commit, PhysicalKey.T, RawInputModifiers.Control);
        MenuKeyboardSelection guard = KeyboardSelection(commit); Assert.That(guard.IsActive, Is.True);
        Press(commit, PhysicalKey.Escape); Assert.That(guard.IsActive, Is.False); Assert.That(commit.IsVisible, Is.True);
        Press(commit, PhysicalKey.T, RawInputModifiers.Control); Assert.That(guard.IsActive, Is.True);
        commit.Close(); await Until(() => !commit.IsVisible && !commit.IsBusy);
        Assert.That(guard.IsActive, Is.False); Assert.That(commit.OwnedWindows, Is.Empty); Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    public async Task AFirstKeyboardOnlyConventionalMenuKeepsItsSelectionWithoutAnObservedPointerPosition()
    {
        await using Scope f = await Scope.CreateAsync(); CommitDialog commit = await f.OpenAsync();
        MenuKeyboardSelection guard = KeyboardSelection(commit);
        Assert.That(typeof(MenuKeyboardSelection).GetField("_lastPointerScreen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(guard),
            Is.Null, "This case must start without any Pointer/Edit helper or an observed pointer position.");
        TextBox message = Field<TextBox>(commit, "Message"); Assert.That(message.Text, Is.Empty.Or.Null);
        string material = await f.MaterialAsync(); Press(commit, PhysicalKey.T, RawInputModifiers.Control);
        MenuItem menu = Templates(commit), conventional = menu.Items.OfType<MenuItem>().Single(item => item.Name == "ConventionalCommits");
        MenuItem feat = conventional.Items.OfType<MenuItem>().Single(item => item.Name == "Conventional_feat");
        Assert.That(guard.IsActive, Is.True); Assert.That(menu.SelectedItem, Is.SameAs(conventional));
        Assert.That(conventional.SelectedItem, Is.SameAs(feat)); Assert.That(feat.IsFocused, Is.True);
        Press(commit, PhysicalKey.Enter); Assert.That(message.Text, Is.EqualTo("feat: "));
        Assert.That(message.IsFocused, Is.True); Assert.That(guard.IsActive, Is.False);
        Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    public async Task NativeUndoOfAutomaticBlankAndBulletDoesNotTriggerFormattingAgainAndLimitsRemainIndependent()
    {
        await using Scope f = await Scope.CreateAsync(); var baseline = f.Store.Load();
        f.Store.SaveChanges(baseline, baseline with { SecondLineMustBeEmpty = true, MaxFirstLineLength = 4 });
        CommitDialog commit = await f.OpenAsync(); Edit(commit, "Message", "subject\nbody");
        TextBox message = Field<TextBox>(commit, "Message"); Assert.That(message.Text, Is.EqualTo("subject\n\n - body"));
        Assert.That(message.CaretIndex, Is.EqualTo(message.Text!.Length));
        Press(commit, PhysicalKey.Z, RawInputModifiers.Control); Assert.That(message.Text, Is.EqualTo("subject\nbody"));
        Assert.That(Field<SpellingEditorView>(commit, "SpellingEditorView").ValidationRanges, Is.EqualTo(new[] { new SpellingRange(4, 3) }));
        Assert.That(SpellingEditorView.IllFormedRanges(new string('x', 51)).Single(), Is.EqualTo(new SpellingRange(50, 1)));
        Press(commit, PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift); Assert.That(message.Text, Is.EqualTo("subject\n\n - body"));
    }

    [AvaloniaTest]
    public async Task UndoBeforeTheQueuedAutoformatRunsInvalidatesThatWorkWithoutReinsertingItsText()
    {
        await using Scope f = await Scope.CreateAsync(); var baseline = f.Store.Load(); f.Store.SaveChanges(baseline, baseline with { SecondLineMustBeEmpty = true });
        CommitDialog commit = await f.OpenAsync(); TextBox message = Field<TextBox>(commit, "Message"); PushProcessTestDriver.Pointer(message);
        // The headless key helpers themselves run the dispatcher before/after input. Use the public native
        // editing boundary here so the queued-work precondition is real; the prior case covers actual Ctrl+Z/Redo.
        message.SelectedText = "Subject\nbody";
        Assert.That(message.Text, Is.EqualTo("Subject\nbody"), "Automatic formatting must still be pending before public Undo.");
        Assert.That(message.CanUndo, Is.True);
        message.Undo(); Assert.That(message.Text, Is.Empty); // Still no RunJobs between insertion and Undo.
        Pump(commit);
        Assert.That(message.Text, Is.Empty); Assert.That(message.CaretIndex, Is.Zero); Assert.That(message.IsFocused, Is.True);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ARealValidationWarningNoPreservesGitAndYesCommitsThroughTheNativeHook(bool accept)
    {
        await using Scope f = await Scope.CreateAsync(); var baseline = f.Store.Load(); f.Store.SaveChanges(baseline, baseline with { MaxFirstLineLength = 4 });
        string hooks = f.Git.Output("owned-hooks"), marker = f.Git.Output("hook-ran"); Directory.CreateDirectory(hooks);
        string hook = Path.Combine(hooks, "pre-commit"); File.WriteAllText(hook, "#!/bin/sh\nprintf ran > '" + marker.Replace("'", "'\\''", StringComparison.Ordinal) + "'\n");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        await f.Git.GitAsync("config", "core.hooksPath", hooks); await File.AppendAllTextAsync(f.Git.File, "change\n"); await f.Git.Repository.StageAllAsync();
        CommitDialog commit = await f.OpenAsync(); Edit(commit, "Message", "Long subject"); string material = await f.MaterialAsync();
        PushProcessTestDriver.Pointer(Field<Button>(commit, "Commit")); await Until(() => commit.OwnedWindows.Any(w => w.Name == "CommitValidation"));
        Window warning = commit.OwnedWindows.Single(w => w.Name == "CommitValidation");
        Assert.That(Field<TextBlock>(warning, "CommitValidationMessage").Text, Does.Contain("too many")); Assert.That(warning.Owner, Is.SameAs(commit));
        PushProcessTestDriver.Pointer(Field<Button>(warning, accept ? "CommitValidationYes" : "CommitValidationNo")); await Idle(commit);
        if (accept)
        { Assert.That(commit.HasCommitted, Is.True); Assert.That(File.ReadAllText(marker), Is.EqualTo("ran")); Assert.That((await f.Git.GitAsync("log", "-1", "--pretty=%s")).StandardOutput.TrimEnd(), Is.EqualTo("Long subject")); }
        else
        { Assert.That(commit.HasCommitted, Is.False); Assert.That(File.Exists(marker), Is.False); Assert.That(await f.MaterialAsync(), Is.EqualTo(material)); Assert.That(Field<TextBox>(commit, "Message").Text, Is.EqualTo("Long subject")); }
    }

    [AvaloniaTest]
    public async Task AClosingValidationQuestionStopsTheCommitAndASingleOwnerCloseDiscardsOwnedSettings()
    {
        await using Scope f = await Scope.CreateAsync(); var baseline = f.Store.Load(); f.Store.SaveChanges(baseline, baseline with { MaxFirstLineLength = 4 });
        await File.AppendAllTextAsync(f.Git.File, "change\n"); await f.Git.Repository.StageAllAsync(); CommitDialog commit = await f.OpenAsync(owned: true);
        Edit(commit, "Message", "Long subject"); string material = await f.MaterialAsync();
        PushProcessTestDriver.Pointer(Field<Button>(commit, "Commit")); await Until(() => commit.OwnedWindows.Count > 0);
        commit.OwnedWindows.Single().Close(); await Idle(commit); Assert.That(commit.HasCommitted, Is.False); Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
        byte[] profile = File.ReadAllBytes(f.Store.FilePath); var settings = await OpenSettings(commit); Edit(settings, "_NO_TRANSLATE_textBoxCommitTemplateName", "discard on owner close");
        Window owner = (Window)commit.Owner!; owner.Close(); await Until(() => !commit.IsVisible && !settings.IsVisible && !owner.IsVisible);
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(profile)); Assert.That(await f.MaterialAsync(), Is.EqualTo(material));
    }

    [AvaloniaTest]
    public async Task RegistryTemplatesStayBeforeUserTemplatesRefreshWithoutAccumulationAndUnregisterCleanly()
    {
        await using Scope f = await Scope.CreateAsync(); string name = "registered-" + Guid.NewGuid().ToString("N"); int calls = 0;
        var baseline = f.Store.Load(); f.Store.SaveChanges(baseline, baseline.WithTemplate(0, new("user", "body")));
        CommitTemplateRegistry.Register(name, () => { calls++; return "registered " + calls; });
        try
        {
            CommitDialog commit = await f.OpenAsync(); MenuItem menu = OpenTemplates(commit);
            Assert.That(menu.Items.OfType<MenuItem>().Where(i => i.Name is "RegisteredCommitTemplate" or "UserCommitTemplate").Select(i => ((CommitMessageTemplate)i.Tag!).Name), Is.EqualTo(new[] { name, "user" }));
            string expected = ((CommitMessageTemplate)menu.Items.OfType<MenuItem>().First(i => i.Name == "RegisteredCommitTemplate").Tag!).Text;
            PushProcessTestDriver.Pointer(menu.Items.OfType<MenuItem>().First(i => i.Name == "RegisteredCommitTemplate")); await Idle(commit);
            Assert.That(Field<TextBox>(commit, "Message").Text, Is.EqualTo(expected));
            CommitTemplateRegistry.Unregister(name); menu = OpenTemplates(commit); Assert.That(menu.Items.OfType<MenuItem>().Any(i => i.Name == "RegisteredCommitTemplate"), Is.False);
            Assert.That(menu.Items.OfType<Separator>().Count(), Is.EqualTo(2)); menu.Close();
        }
        finally { CommitTemplateRegistry.Unregister(name); }
    }

    private static async Task<CommitMessageSettingsDialog> OpenSettings(CommitDialog commit)
    {
        MenuItem menu = OpenTemplates(commit); PushProcessTestDriver.Pointer(menu.Items.OfType<MenuItem>().Single(i => i.Name == "CommitMessageSettings"));
        await Until(() => commit.OwnedWindows.OfType<CommitMessageSettingsDialog>().Any(w => w.IsVisible));
        var dialog = commit.OwnedWindows.OfType<CommitMessageSettingsDialog>().Single(w => w.IsVisible); Pump(dialog); return dialog;
    }
    private static MenuItem Templates(CommitDialog commit) => Field<Menu>(commit, "CommitToolbar").Items.OfType<MenuItem>().Single(i => i.Name == "commitTemplatesToolStripMenuItem");
    private static MenuItem OpenTemplates(CommitDialog commit) { MenuItem menu = Templates(commit); PushProcessTestDriver.Pointer(menu); Pump(commit); Assert.That(menu.IsSubMenuOpen, Is.True); return menu; }
    private static void OpenTab(Window window, string name) { PushProcessTestDriver.Pointer(Field<TabControl>(window, "tabControl1").Items.OfType<TabItem>().Single(t => t.Name == name)); Pump(window); }
    private static async Task Choose(Window window, ComboBox combo, int index)
    { PushProcessTestDriver.Pointer(combo); Press(window, PhysicalKey.Home); for (int i = 0; i < index; i++) { Press(window, PhysicalKey.ArrowDown); } Press(window, PhysicalKey.Enter); await Task.Yield(); Pump(window); Assert.That(combo.SelectedIndex, Is.EqualTo(index)); Assert.That(combo.IsDropDownOpen, Is.False); }
    private static void Edit(Window window, string name, string value) { var editor = Field<TextBox>(window, name); PushProcessTestDriver.Pointer(editor); Press(window, PhysicalKey.A, RawInputModifiers.Control); window.KeyTextInput(value); Pump(window); }
    private static void EditNumber(Window window, string name, string value)
    {
        var input = Field<NumericUpDown>(window, name); var editor = input.GetVisualDescendants().OfType<TextBox>().Single();
        PushProcessTestDriver.Pointer(editor); Press(window, PhysicalKey.A, RawInputModifiers.Control); window.KeyTextInput(value); Press(window, PhysicalKey.Tab);
        var presenter = editor.GetVisualDescendants().OfType<TextPresenter>().Single();
        Assert.That(editor.Text, Is.EqualTo(value)); Assert.That(presenter.IsEffectivelyVisible, Is.True);
        Assert.That(presenter.Bounds.Width, Is.GreaterThanOrEqualTo(presenter.TextLayout.Width), "The actual numeric text must fit beside the compact spinner, not be hidden by arrows.");
    }
    private static MenuKeyboardSelection KeyboardSelection(CommitDialog commit)
        => (MenuKeyboardSelection)typeof(CommitDialog).GetField("_templateKeyboardSelection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(commit)!;
    private static void Hover(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; Pump(top);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        Visual? hit = top.InputHitTest(point) as Visual;
        Assert.That(control.IsEffectivelyVisible && control.IsEffectivelyEnabled && new Rect(default, top.ClientSize).Contains(point)
            && hit?.GetSelfAndVisualAncestors().Contains(control) == true, Is.True, "The real hover target must pass the same visibility/viewport/hit-test prerequisites as Pointer.");
        top.MouseMove(point); Pump(top);
    }
    private static T Field<T>(TopLevel window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Press(Window window, PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None) { window.KeyPressQwerty(key, mods); if (window.IsVisible) { window.KeyReleaseQwerty(key, mods); Pump(window); } }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition) { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20)); while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); } }
    private static Task Idle(CommitDialog dialog) => Until(() => !dialog.IsBusy && dialog.OwnedWindows.Count == 0);
    private static void Capture(TopLevel window, string name) { Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string root = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(root); string path = Path.Combine(root, name); frame!.Save(path); TestContext.AddTestAttachment(path); }
    private sealed class Scope(GitFixture git) : IAsyncDisposable
    {
        private readonly List<Window> _windows = [];
        internal GitFixture Git => git;
        internal CommitMessagePreferencesStore Store { get; } = new();
        internal static async Task<Scope> CreateAsync()
        {
            GitFixture git = await GitFixture.CreateAsync(); git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
            new CommitPreferencesStore().Save(new() { CloseAfterEachCommit = false, CloseWhenAllChangesCommitted = false, SelectStagedOnMessageFocus = false });
            return new(git);
        }
        internal async Task<CommitDialog> OpenAsync(bool dark = false, bool owned = false)
        {
            CommitDialog dialog = new(git.Repository) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; _windows.Add(dialog);
            if (owned) { Window owner = new() { Width = 1000, Height = 800 }; _windows.Insert(0, owner); owner.Show(); _ = dialog.ShowDialog(owner); }
            else { dialog.Show(); }
            await dialog.InitializeAsync(); await Idle(dialog); Pump(dialog); Assert.That(dialog.LastError, Is.Null); return dialog;
        }
        internal async Task<string> MaterialAsync()
        {
            string refs = (await git.GitAsync("show-ref")).StandardOutput, head = await git.Repository.ResolveRevisionAsync("HEAD");
            string index = Convert.ToHexString(await File.ReadAllBytesAsync(git.PathInRepository(".git/index")));
            string files = string.Join('\n', Directory.EnumerateFiles(git.Repository.Path, "*", SearchOption.AllDirectories).Where(p => !Path.GetRelativePath(git.Repository.Path, p).StartsWith(".git/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(p => Path.GetRelativePath(git.Repository.Path, p) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
            return refs + head + index + files;
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window window in _windows.AsEnumerable().Reverse())
                { foreach (Window child in window.OwnedWindows.ToArray()) { child.Close(); } window.Close(); await Until(() => !window.IsVisible && (window is not CommitDialog c || !c.IsBusy)); }
            }
            finally { git.Dispose(); }
        }
    }
}

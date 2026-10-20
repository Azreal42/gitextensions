using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.DeleteUnusedBranchesUiFixture;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class DeleteUnusedBranchesDialogTests
{
    [AvaloniaTest]
    [TestCase("en", false), TestCase("fr", true)]
    public async Task OriginalFiveColumnsDefaultsAndCompactCheckboxRowsUsePluginTranslations(string language, bool dark)
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create();
        DeleteUnusedBranchesDialog dialog = await f.Show(language: language, dark: dark);
        TranslationCatalog translations = TranslationCatalog.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "Translation"), System.Globalization.CultureInfo.GetCultureInfo(language));
        string before = await f.Materials();
        Assert.That(dialog.Width, Is.EqualTo(760)); Assert.That(dialog.Height, Is.EqualTo(500));
        Assert.That(f.Field<NumericUpDown>("olderThanDays").Value, Is.EqualTo(30));
        Assert.That(f.Field<NumericUpDown>("olderThanDays").Maximum, Is.EqualTo(100000));
        Assert.That(f.Field<TextBox>("mergedIntoBranch").Text, Is.EqualTo("HEAD"));
        Assert.That(f.Field<TextBox>("_NO_TRANSLATE_Remote").Text, Is.EqualTo("origin"));
        Assert.That(f.Field<TextBox>("regexFilter").Text, Is.EqualTo("/(feature|develop)/"));
        foreach (string choice in new[] { "IncludeRemoteBranches", "useRegexFilter", "useRegexCaseInsensitive", "regexDoesNotMatch", "includeUnmergedBranches" })
            Assert.That(f.Field<CheckBox>(choice).IsChecked, Is.False, choice);
        Assert.That(f.Field<Button>("buttonSettings").IsVisible, Is.False, "No unrelated Settings introduction is used as a plugin configuration route.");
        Assert.That(dialog.GetVisualDescendants().OfType<Button>().Any(button => button.IsDefault), Is.False, "The Windows form has no AcceptButton.");
        Grid header = f.Field<Grid>("DeleteUnusedBranchesHeaders"); Assert.That(header.ColumnDefinitions, Has.Count.EqualTo(5));
        foreach ((string name, string text) in new[] { ("nameDataGridViewTextBoxColumn", "Name"), ("dateDataGridViewTextBoxColumn", "Last activity"), ("Author", "Last author") })
            Assert.That(f.Field<Button>(name).Content?.ToString(), Is.EqualTo(translations.Translate("DeleteUnusedBranchesForm", name, "HeaderText", text)));
        Assert.That(f.Field<TextBlock>("Message").Text, Is.EqualTo(translations.Translate("DeleteUnusedBranchesForm", "Message", "HeaderText", "Last message")));
        ListBox list = f.Field<ListBox>("BranchesGrid");
        Assert.That(dialog.VisibleBranches.Select(item => item.Reference.ShortName), Is.EquivalentTo(new[] { "old/one", "old/two", "recent/topic" }));
        Assert.That(dialog.SelectedBranches, Is.EquivalentTo(new[] { "refs/heads/old/one", "refs/heads/old/two" }));
        Assert.That(dialog.LastPreview!.Candidates.Single(item => item.Reference.ShortName == "main").Reason, Is.EqualTo("Current branch."));
        Assert.That(dialog.LastPreview.Candidates.Single(item => item.Reference.ShortName == "unmerged/topic").Eligible, Is.False);
        f.Pump();
        foreach (Control row in list.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Name == "DeleteUnusedBranchRow"))
        { Assert.That(row.Bounds.Height, Is.EqualTo(22)); Assert.That(((Grid)row).Children, Has.Count.EqualTo(5)); }
        foreach (string name in new[] { "olderThanDays", "mergedIntoBranch", "_NO_TRANSLATE_Remote", "regexFilter", "RefreshBtn", "Delete", "Cancel" })
        {
            Control control = f.Field<Control>(name); Rect bounds = new(control.TranslatePoint(default, dialog)!.Value, control.Bounds.Size);
            Assert.That(control.IsEffectivelyVisible && bounds.Width > 0 && bounds.Height > 0 && new Rect(default, dialog.ClientSize).Contains(bounds), Is.True, name);
            if (control is Button button)
            {
                TextBlock? caption = button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(label => label.Text == button.Content?.ToString());
                Assert.That(caption, Is.Not.Null, name); Rect textBounds = new(caption!.TranslatePoint(default, button)!.Value, caption!.Bounds.Size);
                Assert.That(new Rect(default, button.Bounds.Size).Contains(textBounds), Is.True, name + " translated caption");
                Assert.That(caption.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False, name);
                Assert.That(textBounds.X + caption.TextLayout.WidthIncludingTrailingWhitespace,
                    Is.LessThanOrEqualTo(button.Bounds.Width - button.BorderThickness.Right - button.Padding.Right + .5), name + " complete caption");
            }
        }
        f.Capture("delete-obsolete-branches-" + language + (dark ? "-dark.png" : "-light.png"));
        Assert.That(await f.Materials(), Is.EqualTo(before)); Assert.That(dialog.HasRepositoryChanges, Is.False);
    }

    [AvaloniaTest]
    public async Task AgeOnlyPreselectsAndHeaderAndSortingKeepExplicitRecentChoices()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(); DeleteUnusedBranchesDialog dialog = await f.Show();
        string before = await f.Materials();
        await f.Check("refs/heads/recent/topic"); Assert.That(dialog.SelectedBranches, Has.Count.EqualTo(3));
        Assert.That(f.Field<CheckBox>("checkBoxHeaderCell").IsChecked, Is.True);
        f.Click("dateDataGridViewTextBoxColumn"); Assert.That(dialog.VisibleBranches.Select(item => item.Date), Is.Ordered);
        Assert.That(dialog.SelectedBranches, Has.Count.EqualTo(3));
        f.Click("checkBoxHeaderCell"); Assert.That(dialog.SelectedBranches, Is.Empty);
        f.Click("checkBoxHeaderCell"); Assert.That(dialog.SelectedBranches, Has.Count.EqualTo(3));
        f.Click("Author"); Assert.That(dialog.SelectedBranches, Has.Count.EqualTo(3));
        Assert.That(f.Field<TextBlock>("OperationStatus").Text, Is.EqualTo("3/3 branches selected."));
        Assert.That(await f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task HeaderSelectsEveryEligibleBranchBeyondTheVisiblePageWithoutAnArbitraryLimit()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create();
        for (int index = 0; index < 130; index++) await f.Git.GitAsync("branch", "batch/" + index.ToString("D3"), f.Recent);
        DeleteUnusedBranchesDialog dialog = await f.Show(); string before = await f.Materials();
        Assert.That(dialog.VisibleBranches, Has.Count.EqualTo(133)); f.Click("checkBoxHeaderCell");
        Assert.That(dialog.SelectedBranches, Has.Count.EqualTo(133));
        Assert.That(f.Field<TextBlock>("OperationStatus").Text, Is.EqualTo("133/133 branches selected."));
        Assert.That(await f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(false), TestCase(true)]
    public async Task CountDecisionNoOrDismissNeverDeletesAndKeepsTheReviewedSelection(bool dismiss)
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(); DeleteUnusedBranchesDialog dialog = await f.Show();
        string before = await f.Materials(); string[] selected = dialog.SelectedBranches.ToArray(); f.Click("Delete");
        Window count = await f.Decision("2 selected branches"); Assert.That(count.Owner, Is.SameAs(dialog));
        Assert.That(count.GetVisualDescendants().OfType<Image>().Any(image => image.Tag as string == "Warning.png"), Is.True);
        if (dismiss) count.Close(); else Answer(count, false);
        await f.Idle(); Assert.That(dialog.LastDeletion, Is.Null); Assert.That(dialog.SelectedBranches, Is.EqualTo(selected));
        Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(await f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(false), TestCase(true)]
    public async Task NamedRemoteDecisionIsRequiredAfterCountAndNoOrDismissChangesNothing(bool dismiss)
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(remote: true); DeleteUnusedBranchesDialog dialog = await f.Show(remote: true);
        string before = await f.Materials(); IReadOnlyDictionary<string, string> remote = await f.RemoteRefs();
        f.Click("Delete"); Window count = await f.Decision("2 selected branches");
        Assert.That(count.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text?.Contains("DANGEROUS", StringComparison.Ordinal) == true), Is.False);
        Answer(count, true); Window danger = await f.Decision("remote 'origin'");
        Assert.That(danger.GetVisualDescendants().OfType<Image>().Any(image => image.Tag as string == "Warning.png"), Is.True);
        Assert.That(danger.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text?.Contains("This can not be undone.", StringComparison.Ordinal) == true), Is.True);
        Assert.That(await f.RemoteRefs(), Is.EquivalentTo(remote));
        if (dismiss) danger.Close(); else Answer(danger, false);
        await f.Idle(); Assert.That(dialog.LastDeletion, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.False);
        Assert.That(await f.Materials(), Is.EqualTo(before)); Assert.That(await f.RemoteRefs(), Is.EquivalentTo(remote));
    }

    [AvaloniaTest]
    public async Task TwoExplicitYesDecisionsDeleteOnlyCheckedRemoteBranchesThroughNativeGit()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(remote: true); DeleteUnusedBranchesDialog dialog = await f.Show(remote: true);
        Assert.That(dialog.VisibleBranches.All(item => item.Reference.IsRemote && item.Reference.SymbolicTarget is null), Is.True);
        await f.Check("refs/remotes/origin/old/two"); await f.Check("refs/remotes/origin/recent/topic");
        f.Click("Delete"); Answer(await f.Decision("2 selected branches"), true); Answer(await f.Decision("remote 'origin'"), true); await f.Idle();
        Assert.That(dialog.LastDeletion!.Success, Is.True); Assert.That(dialog.LastDeletion.DeletedCount, Is.EqualTo(2)); Assert.That(dialog.HasRepositoryChanges, Is.True);
        IReadOnlyDictionary<string, string> remote = await f.RemoteRefs();
        Assert.That(remote.ContainsKey("refs/heads/old/one"), Is.False); Assert.That(remote.ContainsKey("refs/heads/recent/topic"), Is.False);
        Assert.That(remote["refs/heads/old/two"], Is.EqualTo(f.Old)); Assert.That(remote["refs/heads/main"], Is.EqualTo(f.Recent));
        Assert.That(dialog.LastPreview!.Candidates.Any(item => item.Reference.Name == "refs/remotes/origin/old/one"), Is.False);
        Assert.That(f.Field<TextBox>("DeleteUnusedBranchesResults").Text, Does.Contain("origin/old/one").And.Contain("origin/recent/topic"));
    }

    [AvaloniaTest]
    public async Task ServerMovedAfterPreviewKeepsItsLeaseAndPartialResultsRemainVisible()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(remote: true); DeleteUnusedBranchesDialog dialog = await f.Show(remote: true);
        await f.AdvanceRemote("old/two");
        f.Click("Delete"); Answer(await f.Decision("2 selected branches"), true); Answer(await f.Decision("remote 'origin'"), true); await f.Idle();
        Assert.That(dialog.LastDeletion!.Success, Is.False); Assert.That(dialog.LastDeletion.DeletedCount, Is.EqualTo(1)); Assert.That(dialog.LastDeletion.FailureCount, Is.EqualTo(1));
        Assert.That(dialog.LastDeletion.Outcomes.Single(item => item.Branch.Reference.ShortName == "origin/old/two").Status, Is.EqualTo(DeleteUnusedBranchStatus.Failed));
        IReadOnlyDictionary<string, string> remote = await f.RemoteRefs();
        Assert.That(remote.ContainsKey("refs/heads/old/one"), Is.False); Assert.That(remote["refs/heads/old/two"], Is.EqualTo(f.Recent));
        Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(dialog.SelectedBranches, Has.Count.EqualTo(2));
        Assert.That(f.Field<TextBox>("DeleteUnusedBranchesResults").IsEffectivelyVisible, Is.True);
        Assert.That(f.Field<TextBox>("DeleteUnusedBranchesResults").Text, Does.Contain("Deleted").And.Contain("Failed").And.Contain("stale info"));
        Assert.That(f.Field<TextBlock>("OperationStatus").Text, Does.Contain("failed"));
        dialog.Height = dialog.MinHeight; f.Pump();
        foreach (string name in new[] { "DeleteUnusedBranchesResults", "Delete", "Cancel" })
        {
            Control control = f.Field<Control>(name); Rect bounds = new(control.TranslatePoint(default, dialog)!.Value, control.Bounds.Size);
            Assert.That(control.IsEffectivelyVisible && bounds.Height > 0 && new Rect(default, dialog.ClientSize).Contains(bounds), Is.True, name + " partial-result recovery at minimum height");
        }
    }

    [AvaloniaTest]
    public async Task ClosingDuringTheSecondNativePushRetainsTheFirstDeletionAndAnHonestInterruptedOutcome()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(remote: true); string gate = f.GateSecondPush();
        DeleteUnusedBranchesDialog dialog = await f.Show(remote: true, repository: new(f.Repository.Path, new GitRunner(gate)));
        await f.Check("refs/remotes/origin/recent/topic");
        f.Click("Delete"); Answer(await f.Decision("3 selected branches"), true); Answer(await f.Decision("remote 'origin'"), true);
        await f.Until(() => System.IO.File.Exists(gate + ".ready"));
        int pid = int.Parse(System.IO.File.ReadAllText(gate + ".ready"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.That(Running(pid), Is.True); IReadOnlyDictionary<string, string> during = await f.RemoteRefs();
        Assert.That(during.ContainsKey("refs/heads/old/one"), Is.False); Assert.That(during["refs/heads/old/two"], Is.EqualTo(f.Old));
        dialog.Close(); await f.Until(() => !dialog.IsVisible && !dialog.IsBusy); await dialog.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(Running(pid), Is.False, "Completion waits for the real native actor to stop.");
        Assert.That(dialog.LastDeletion!.Cancelled, Is.True); Assert.That(dialog.LastDeletion.Success, Is.False);
        Assert.That(dialog.LastDeletion.Outcomes.Select(item => item.Status), Is.EqualTo(new[] { DeleteUnusedBranchStatus.Deleted, DeleteUnusedBranchStatus.Interrupted, DeleteUnusedBranchStatus.NotAttempted }));
        Assert.That(dialog.LastDeletion.Outcomes[1].Message, Does.Contain("unknown")); Assert.That(dialog.HasRepositoryChanges, Is.True);
        IReadOnlyDictionary<string, string> after = await f.RemoteRefs();
        Assert.That(after["refs/heads/old/two"], Is.EqualTo(f.Old)); Assert.That(after["refs/heads/recent/topic"], Is.EqualTo(f.Recent));
    }

    [AvaloniaTest]
    public async Task ReferenceRevalidationRejectsTheWholeChangedSelectionBeforeTheFirstDeletion()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(); DeleteUnusedBranchesDialog dialog = await f.Show();
        await f.Git.GitAsync("update-ref", "refs/heads/old/two", f.Recent, f.Old); string before = await f.Materials();
        f.Click("Delete"); Answer(await f.Decision("2 selected branches"), true); await f.Idle();
        Assert.That(dialog.LastDeletion, Is.Null); Assert.That(dialog.LastError, Does.Contain("changed since the preview"));
        Assert.That(await f.Materials(), Is.EqualTo(before)); Assert.That(dialog.HasRepositoryChanges, Is.False);
    }

    [AvaloniaTest]
    public async Task IncludeUnmergedWarnsBeforeItsForcedLocalDeletionAndReferenceTargetRemainsExcluded()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(); DeleteUnusedBranchesDialog dialog = await f.Show();
        f.Click("includeUnmergedBranches"); Window notice = await f.Decision("dangling commits");
        Pointer(notice.GetVisualDescendants().OfType<Button>().Single()); await f.Idle();
        f.Edit("mergedIntoBranch", "old/one"); f.Click("RefreshBtn"); await f.Idle();
        Assert.That(dialog.VisibleBranches.Any(item => item.Reference.ShortName == "old/one"), Is.False);
        Assert.That(dialog.VisibleBranches.Any(item => item.Reference.ShortName == "unmerged/topic"), Is.True);
        if (dialog.SelectedBranches.Count > 0) f.Click("checkBoxHeaderCell");
        if (dialog.SelectedBranches.Count > 0) f.Click("checkBoxHeaderCell");
        Assert.That(dialog.SelectedBranches, Is.Empty); await f.Check("refs/heads/unmerged/topic");
        f.Click("Delete"); Answer(await f.Decision("1 selected branches"), true); await f.Idle();
        Assert.That(dialog.LastDeletion!.DeletedCount, Is.EqualTo(1)); Assert.That(await f.Revision("refs/heads/old/one"), Is.EqualTo(f.Old));
        Assert.That((await f.Repository.GetReferencesAsync()).Any(item => item.Name == "refs/heads/unmerged/topic"), Is.False);
    }

    [AvaloniaTest]
    public async Task ZeroCheckedBranchesShowsTheOriginalNoticeAndNeverInventsAConfirmationOrMutation()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(); DeleteUnusedBranchesDialog dialog = await f.Show();
        f.Click("checkBoxHeaderCell"); f.Click("checkBoxHeaderCell"); Assert.That(dialog.SelectedBranches, Is.Empty);
        string before = await f.Materials(); f.Click("Delete"); Window notice = await f.Decision("Select branches to delete using checkboxes");
        Assert.That(notice.GetVisualDescendants().OfType<Button>().Count(), Is.EqualTo(1));
        Pointer(notice.GetVisualDescendants().OfType<Button>().Single()); await f.Idle();
        Assert.That(dialog.LastDeletion, Is.Null); Assert.That(await f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(false), TestCase(true)]
    public async Task ClosingDuringEitherOwnedDecisionCancelsAndDrainsWithoutImplicitYes(bool remoteDecision)
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(remote: true); DeleteUnusedBranchesDialog dialog = await f.Show(remote: true);
        string before = await f.Materials(); IReadOnlyDictionary<string, string> remote = await f.RemoteRefs();
        f.Click("Delete"); Window count = await f.Decision("2 selected branches");
        if (remoteDecision) { Answer(count, true); await f.Decision("remote 'origin'"); }
        dialog.Close(); await f.Until(() => !dialog.IsVisible && !dialog.IsBusy);
        await dialog.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(dialog.LastDeletion, Is.Null); Assert.That(await f.Materials(), Is.EqualTo(before));
        Assert.That(await f.RemoteRefs(), Is.EquivalentTo(remote)); Assert.That(f.Owner.IsVisible, Is.True);
    }

    [AvaloniaTest]
    [TestCase(false), TestCase(true)]
    public async Task SearchCancelOrCloseDrainsTheRealReadAndNeverPublishesLateRows(bool close)
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(); string gate = f.Gate("for-each-ref");
        DeleteUnusedBranchesDialog dialog = await f.Show(repository: new(f.Repository.Path, new GitRunner(gate)), initialize: false);
        Task read = dialog.InitializeAsync(); await f.Until(() => System.IO.File.Exists(gate + ".ready"));
        Assert.That(f.Field<Button>("RefreshBtn").Content, Is.EqualTo("Cancel"));
        int pid = int.Parse(System.IO.File.ReadAllText(gate + ".ready"), System.Globalization.CultureInfo.InvariantCulture);
        if (close) dialog.Close(); else f.Click("RefreshBtn");
        await f.Until(() => read.IsCompleted); await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(dialog.IsBusy, Is.False); Assert.That(dialog.IsVisible, Is.EqualTo(!close)); Assert.That(Running(pid), Is.False);
        Assert.That(dialog.LastPreview, Is.Null); Assert.That(dialog.VisibleBranches, Is.Empty);
        Assert.That(dialog.LastError, Does.Contain("cancelled"));
        System.IO.File.WriteAllText(gate + ".release", "release"); f.Pump();
        Assert.That(dialog.LastPreview, Is.Null, "The cancelled read cannot publish after its process has drained.");
    }

    [AvaloniaTest]
    public async Task EditingCriteriaDuringSearchCancelsTheGenerationWithoutOverwritingTheFocusedDraft()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(); string gate = f.Gate("for-each-ref");
        DeleteUnusedBranchesDialog dialog = await f.Show(repository: new(f.Repository.Path, new GitRunner(gate)), initialize: false);
        Task read = dialog.InitializeAsync(); await f.Until(() => System.IO.File.Exists(gate + ".ready"));
        f.Edit("mergedIntoBranch", "old/one"); await read.WaitAsync(TimeSpan.FromSeconds(5)); f.Pump();
        Assert.That(dialog.LastPreview, Is.Null); Assert.That(f.Field<TextBox>("mergedIntoBranch").Text, Is.EqualTo("old/one"));
        Assert.That(f.Field<TextBox>("mergedIntoBranch").IsKeyboardFocusWithin, Is.True);
        System.IO.File.WriteAllText(gate + ".release", "release"); f.Click("RefreshBtn"); await f.Idle();
        Assert.That(dialog.VisibleBranches.Select(item => item.Reference.ShortName), Is.EquivalentTo(new[] { "old/two" }));
    }

    [AvaloniaTest]
    public async Task PreviewUsesOneTypedEngineWhileTheGenericTableKeepsItsOriginalCellsAndExplicitRecentDeletion()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(); DeleteUnusedBranchesPlugin plugin = new();
        Dictionary<string, string?> values = f.Values(); values["pattern"] = ""; values["operation"] = "preview";
        DeleteUnusedBranchesPreview preview = await plugin.PreviewAsync(new(f.Repository), values);
        PluginResult generic = await plugin.ExecuteAsync(new(f.Repository), values);
        PluginTable typed = preview.ToPluginResult().Table ?? throw new AssertionException("The typed preview must expose its generic table adapter.");
        PluginTable table = generic.Table ?? throw new AssertionException("The existing generic preview must retain its table.");
        Assert.That(typed.Columns, Is.EqualTo(table.Columns));
        Assert.That(typed.Rows.Select(row => row.Cells), Is.EqualTo(table.Rows.Select(row => row.Cells)));
        Assert.That(table.Rows.Single(row => row.Key == "refs/heads/recent/topic").Cells[5], Is.EqualTo("Eligible"));
        values["operation"] = "delete"; values["branches"] = "recent/topic";
        Assert.ThrowsAsync<InvalidOperationException>(() => plugin.ExecuteAsync(new(f.Repository), values));
        PluginResult deleted = await plugin.ExecuteAsync(new(f.Repository, Confirmed: true), values);
        Assert.That(deleted.Success, Is.True); Assert.That(deleted.RepositoryChanged, Is.True);
        Assert.That(deleted.Table!.Rows.Single(row => row.Key == "refs/heads/recent/topic").Cells[5], Is.EqualTo("Deleted"));
    }

    [AvaloniaTest]
    public async Task RegexEnableCaseInversionAndInvalidPatternKeepTheOriginalEligibilityRules()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(); DeleteUnusedBranchesDialog dialog = await f.Show();
        string before = await f.Materials(); f.Click("useRegexFilter"); f.Edit("regexFilter", "OLD/"); f.Click("useRegexCaseInsensitive");
        f.Click("RefreshBtn"); await f.Idle(); Assert.That(dialog.VisibleBranches.Select(item => item.Reference.ShortName), Is.EquivalentTo(new[] { "old/one", "old/two" }));
        f.Click("regexDoesNotMatch"); f.Click("RefreshBtn"); await f.Idle();
        Assert.That(dialog.VisibleBranches.Select(item => item.Reference.ShortName), Is.EquivalentTo(new[] { "recent/topic" }));
        f.Edit("regexFilter", "["); f.Click("RefreshBtn"); await f.Idle();
        Assert.That(dialog.LastError, Is.Not.Null); Assert.That(dialog.VisibleBranches, Is.Empty); Assert.That(await f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task TheFormerGenericSavedFilterRemainsEnabledUnlessAnExplicitEnableFlagSaysOtherwise()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(); Dictionary<string, string?> values = f.Values();
        values.Remove("useRegex"); values["pattern"] = "^old/";
        DeleteUnusedBranchesDialog dialog = await f.Show(values: values);
        Assert.That(f.Field<CheckBox>("useRegexFilter").IsChecked, Is.True);
        Assert.That(dialog.VisibleBranches.Select(item => item.Reference.ShortName), Is.EquivalentTo(new[] { "old/one", "old/two" }));
        f.Click("useRegexFilter"); f.Click("RefreshBtn"); await f.Idle();
        Assert.That(dialog.VisibleBranches, Has.Count.EqualTo(3)); Assert.That(f.Field<TextBox>("regexFilter").Text, Is.EqualTo("^old/"));
    }

    [AvaloniaTest]
    public async Task TheWindowsAgeMaximumIsAcceptedWithoutFilteringOutManuallySelectableRecentBranches()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(); Dictionary<string, string?> values = f.Values(); values["olderThanDays"] = "100000";
        DeleteUnusedBranchesDialog dialog = await f.Show(values: values);
        Assert.That(dialog.VisibleBranches, Has.Count.EqualTo(3)); Assert.That(dialog.SelectedBranches, Is.Empty);
        await f.Check("refs/heads/recent/topic"); Assert.That(dialog.SelectedBranches, Is.EqualTo(new[] { "refs/heads/recent/topic" }));
    }

    [AvaloniaTest]
    public async Task InitialPreviewAndUnsearchedDraftCloseNeverSaveButExplicitSearchUsesTheExistingSettingsCallback()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(); List<IReadOnlyDictionary<string, string?>> saved = [];
        Dictionary<string, string?> initial = f.Values(); initial["future.plugin.option"] = "raw <literal>";
        DeleteUnusedBranchesDialog dialog = await f.Show(values: initial, apply: (values, token) => { token.ThrowIfCancellationRequested(); saved.Add(new Dictionary<string, string?>(values)); return Task.CompletedTask; });
        Assert.That(saved, Is.Empty); f.Edit("regexFilter", "saved literal"); f.Click("RefreshBtn"); await f.Idle();
        Assert.That(saved, Has.Count.EqualTo(1)); Assert.That(saved[0]["pattern"], Is.EqualTo("saved literal")); Assert.That(saved[0]["useRegex"], Is.EqualTo("False")); Assert.That(saved[0]["future.plugin.option"], Is.EqualTo("raw <literal>"));
        Assert.That(saved[0]["branches"], Is.Empty); Assert.That(saved[0]["operation"], Is.EqualTo("preview"));
        f.Edit("mergedIntoBranch", "unsearched draft"); dialog.Close(); await f.Until(() => !dialog.IsVisible);
        Assert.That(saved, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task SettingsSaveFailureIsVisibleAndDoesNotDiscardTheComputedPreview()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create();
        DeleteUnusedBranchesDialog dialog = await f.Show(apply: (_, _) => Task.FromException(new IOException("fixture settings write failed")));
        f.Click("RefreshBtn"); await f.Idle(); Assert.That(dialog.LastError, Is.EqualTo("fixture settings write failed"));
        Assert.That(dialog.VisibleBranches, Has.Count.EqualTo(3)); Assert.That(dialog.SelectedBranches, Has.Count.EqualTo(2));
        Assert.That(dialog.HasRepositoryChanges, Is.False);
    }

    [AvaloniaTest]
    public async Task PhysicalSpaceTogglesTheFocusedCheckboxAndEnterInAnEditorCannotStartDeletion()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(); DeleteUnusedBranchesDialog dialog = await f.Show();
        string before = await f.Materials(); f.Click("checkBoxHeaderCell");
        Assert.That(f.Field<CheckBox>("checkBoxHeaderCell").IsKeyboardFocusWithin, Is.True);
        f.Press(PhysicalKey.Space); Assert.That(dialog.SelectedBranches, Is.Empty);
        f.Click("mergedIntoBranch"); f.Press(PhysicalKey.Enter); f.Press(PhysicalKey.Tab);
        Assert.That(dialog.OwnedWindows, Is.Empty); Assert.That(dialog.LastDeletion, Is.Null); Assert.That(await f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase("disabled")]
    [TestCase("enabled")]
    [TestCase("legacy")]
    public async Task RawPageAndGenericValuesShareTheRegexFlagWithoutLosingTheHistoricalAbsentFlag(string mode)
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create();
        Dictionary<string, string?> values = f.Values();
        values["pattern"] = mode == "disabled" ? "[" : "^old/one$";
        values["useRegex"] = (mode == "enabled").ToString();
        if (mode == "legacy") { values.Remove("useRegex"); }
        string before = await f.Materials(); DeleteUnusedBranchesPlugin plugin = new();
        // These are raw saved values, not a Desktop-only normalized copy.
        DeleteUnusedBranchesPreview historical = await plugin.PreviewAsync(new(f.Repository), values);
        DeleteUnusedBranchesDialog dialog = await f.Show(values: values);
        Assert.That(dialog.LastError, Is.Null);
        Assert.That(f.Field<TextBox>("regexFilter").Text, Is.EqualTo(values["pattern"]));
        Assert.That(f.Field<CheckBox>("useRegexFilter").IsChecked, Is.EqualTo(mode != "disabled"));
        Dictionary<string, string?> raw = new(dialog.CurrentValues);
        PluginResult generic = await plugin.ExecuteAsync(new(f.Repository), raw);
        string[] expected = mode == "disabled" ? new[] { "old/one", "old/two", "recent/topic" } : new[] { "old/one" };
        Assert.That(historical.Candidates.Where(item => item.Eligible).Select(item => item.Reference.ShortName), Is.EquivalentTo(expected));
        Assert.That(dialog.VisibleBranches.Select(item => item.Reference.ShortName), Is.EquivalentTo(expected));
        Assert.That(generic.Table!.Rows.Select(row => row.Cells), Is.EqualTo(dialog.LastPreview!.ToPluginResult().Table!.Rows.Select(row => row.Cells)));
        Assert.That(generic.RepositoryChanged, Is.False); Assert.That(generic.RepositoryMayHaveChanges, Is.False);
        Assert.That(await f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(false, false)] [TestCase(false, true)]
    [TestCase(true, false)] [TestCase(true, true)]
    public async Task NativeFailureKeepsAcquiredAndPossibleEffectsDistinctForGenericAndDialogReceipts(bool rejectedLease, bool genericApi)
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create(remote: rejectedLease);
        Dictionary<string, string?> values = f.Values(remote: rejectedLease);
        string name = rejectedLease ? "refs/remotes/origin/old/one" : "refs/heads/old/one";
        values["useRegex"] = "true"; values["pattern"] = rejectedLease ? "^origin/old/one$" : "^old/one$";
        GitRepository repository = rejectedLease ? f.Repository : new(f.Repository.Path, new GitRunner(f.DeleteThenFail()));
        DeleteUnusedBranchesDialog? dialog = genericApi ? null : await f.Show(remote: rejectedLease, values: values, repository: repository);
        if (rejectedLease) { await f.AdvanceRemote("old/one"); }
        PluginResult result;
        if (genericApi)
        {
            values["operation"] = "delete"; values["branches"] = name;
            result = await new DeleteUnusedBranchesPlugin().ExecuteAsync(new(repository, Confirmed: true), values);
        }
        else
        {
            DeleteUnusedBranchesDialog actual = dialog ?? throw new AssertionException("The real dialog must be open for this case.");
            Assert.That(actual.SelectedBranches, Is.EqualTo(new[] { name }));
            f.Click("Delete"); Answer(await f.Decision("1 selected branches"), true);
            if (rejectedLease) { Answer(await f.Decision("remote 'origin'"), true); }
            await f.Idle();
            Assert.That(actual.LastDeletion, Is.Not.Null); Assert.That(actual.LastDeletion!.DeletedCount, Is.Zero);
            Assert.That(actual.LastDeletion.MayHaveRepositoryChanges, Is.True);
            Assert.That(actual.HasRepositoryChanges, Is.False, "An attempted or rejected operation is not an acquired success.");
            Assert.That(actual.MayHaveRepositoryChanges, Is.True, "The owned Log adapter must still receive possible effects.");
            Assert.That(f.Field<TextBox>("DeleteUnusedBranchesResults").Text, Does.Contain("Failed"));
            result = actual.LastDeletion.ToPluginResult();
        }
        Assert.That(result.Success, Is.False); Assert.That(result.RepositoryChanged, Is.False);
        Assert.That(result.RepositoryMayHaveChanges, Is.True);
        PluginTable outcomes = result.Table ?? throw new AssertionException("The native result must retain its per-branch table.");
        Assert.That(outcomes.Rows.Single(row => row.Key == name).Cells[5], Is.EqualTo("Failed"));
        if (rejectedLease)
        {
            Assert.That((await f.RemoteRefs())["refs/heads/old/one"], Is.EqualTo(f.Recent));
            Assert.That(outcomes.Rows.Single(row => row.Key == name).Cells[6], Does.Contain("stale info"));
        }
        else
        {
            Assert.That((await f.Repository.GetReferencesAsync()).Any(item => item.Name == name), Is.False,
                "The private native command really deleted the branch before returning nonzero.");
            Assert.That(outcomes.Rows.Single(row => row.Key == name).Cells[6], Does.Contain("private failure after applied deletion"));
        }
        Assert.That(await f.Revision("refs/heads/old/two"), Is.EqualTo(f.Old));
    }

    [AvaloniaTest]
    public async Task InitializedPreviewSurvivesTextRenderingAndTheFirstRealCheckboxClickUntilAnActualCriteriaEdit()
    {
        await using DeleteUnusedBranchesUiFixture f = await DeleteUnusedBranchesUiFixture.Create();
        string before = await f.Materials(); List<IReadOnlyDictionary<string, string?>> saved = [];
        DeleteUnusedBranchesDialog dialog = await f.Show(apply: (values, token) =>
        { token.ThrowIfCancellationRequested(); saved.Add(new Dictionary<string, string?>(values)); return Task.CompletedTask; });
        DeleteUnusedBranchesPreview preview = dialog.LastPreview ?? throw new AssertionException("The initialized preview must survive its first rendered frame.");
        ListBox list = f.Field<ListBox>("BranchesGrid"); object source = list.ItemsSource!;
        Assert.That(dialog.VisibleBranches.Select(item => item.Reference.ShortName), Is.EquivalentTo(new[] { "old/one", "old/two", "recent/topic" }));
        for (int frame = 0; frame < 10; frame++) { f.Pump(); }
        Assert.That(dialog.LastPreview, Is.SameAs(preview)); Assert.That(list.ItemsSource, Is.SameAs(source));
        Assert.That(dialog.SelectedBranches, Has.Count.EqualTo(2)); Assert.That(saved, Is.Empty);
        await f.Check("refs/heads/recent/topic");
        Assert.That(dialog.LastPreview, Is.SameAs(preview)); Assert.That(dialog.SelectedBranches, Has.Count.EqualTo(3));
        Assert.That(f.Field<CheckBox>("checkBoxHeaderCell").IsChecked, Is.True);
        f.Edit("mergedIntoBranch", "old/one");
        Assert.That(dialog.LastPreview, Is.Null); Assert.That(dialog.VisibleBranches, Is.Empty);
        Assert.That(f.Field<TextBox>("mergedIntoBranch").IsKeyboardFocusWithin, Is.True);
        Assert.That(saved, Is.Empty); Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(dialog.MayHaveRepositoryChanges, Is.False);
        Assert.That(await f.Materials(), Is.EqualTo(before));
    }
}

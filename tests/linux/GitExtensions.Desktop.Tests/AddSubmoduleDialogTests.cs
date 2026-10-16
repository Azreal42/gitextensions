using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using RootFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AddSubmoduleDialogTests
{
    [AvaloniaTest]
    public async Task OriginalThreeRowsAndTwoButtonsStayCompactInBothThemesWithoutPreselectingHistoryOrADefaultAction()
    {
        await using Fixture f = await Fixture.CreateAsync(); Dictionary<string, byte[]?> histories = f.Histories();
        AddSubmoduleDialog dialog = await f.ShowAsync();
        Assert.That(dialog.Title, Is.EqualTo("Add submodule")); Assert.That(dialog.Name, Is.EqualTo("AddSubmoduleDialog"));
        Assert.That(dialog.MinWidth, Is.EqualTo(492)); Assert.That(dialog.MinHeight, Is.EqualTo(131));
        ComboBox source = Source(dialog), branch = Branch(dialog); TextBox local = Local(dialog);
        Assert.That(source.IsEditable && branch.IsEditable, Is.True);
        Assert.That(source.Text, Is.Null.Or.Empty); Assert.That(local.Text, Is.Null.Or.Empty); Assert.That(branch.Text, Is.Null.Or.Empty);
        Assert.That(source.SelectedIndex, Is.EqualTo(-1)); Assert.That(source.Items.Cast<string>(), Is.EqualTo(new CloneSourcesStore().Load()));
        Assert.That(branch.Items, Is.Empty); Assert.That(Force(dialog).IsChecked, Is.False);
        Assert.That(Add(dialog).IsDefault, Is.False); Assert.That(Add(dialog).IsEnabled, Is.False);
        Assert.That(source.IsKeyboardFocusWithin, Is.True);
        Assert.That(dialog.GetVisualDescendants().OfType<Button>().Where(button => button.Name is "AddSubmodule" or "BrowseSubmoduleSource").Count(), Is.EqualTo(2));
        Assert.That(dialog.GetVisualDescendants().OfType<Button>().Any(button => button.IsCancel), Is.False);
        Assert.That(dialog.GetVisualDescendants().OfType<Image>(), Is.Empty, "The Windows form has no command diagram or footer icon.");
        Press(dialog, PhysicalKey.Tab); Assert.That(Field<Button>(dialog, "BrowseSubmoduleSource").IsKeyboardFocusWithin, Is.True);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            dialog.RequestedThemeVariant = theme;
            foreach (double width in new[] { 492d, 760d })
            {
                dialog.Width = width; Pump(dialog);
                Assert.That(dialog.ClientSize.Height, Is.EqualTo(131).Within(1), "An empty status must not add another row to the Windows 492×131 form.");
                AssertInside(dialog, source, local, branch, Force(dialog), Add(dialog), Field<Button>(dialog, "BrowseSubmoduleSource"));
                double fieldWidth = width - 264;
                AssertBounds(dialog, source, new(144, 14, fieldWidth, 23)); AssertBounds(dialog, local, new(144, 41, fieldWidth, 23));
                AssertBounds(dialog, branch, new(144, 68, fieldWidth, 23)); AssertBounds(dialog, Field<Button>(dialog, "BrowseSubmoduleSource"), new(width - 114, 12, 102, 25));
                AssertBounds(dialog, Add(dialog), new(width - 114, 96, 102, 25));
                Assert.That(Bounds(dialog, Force(dialog)).X, Is.EqualTo(8).Within(2)); Assert.That(Bounds(dialog, Force(dialog)).Y, Is.EqualTo(100).Within(2));
                Capture(dialog, "add-submodule-empty-" + theme + "-" + width + ".png");
            }
        }
        Assert.That(dialog.LastProcess, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.False); f.SameHistories(histories);
    }

    [AvaloniaTest]
    public async Task SelectingSharedCloneHistoryAndTypingAnotherSourceAlwaysReproposesTheRepositoryName()
    {
        await using Fixture f = await Fixture.CreateAsync(); Dictionary<string, byte[]?> histories = f.Histories(); AddSubmoduleDialog dialog = await f.ShowAsync();
        string history = Source(dialog).Items.Cast<string>().First(); const string prefix = "ssh://private-history.invalid/rem";
        Source(dialog).Text = prefix; Pump(dialog); Assert.That(Source(dialog).Text, Is.EqualTo(prefix), "Programmatic loading must not trigger SuggestAppend.");
        Assert.That(HistoryPopup(dialog).IsOpen, Is.False);
        EditCombo(dialog, Source(dialog), prefix); await Until(() => Source(dialog).Text == history && HistoryPopup(dialog).IsOpen);
        Assert.That(Source(dialog).GetVisualDescendants().OfType<TextBox>().Single().SelectedText, Is.EqualTo(history[prefix.Length..]));
        Press(dialog, PhysicalKey.Enter); Assert.That(HistoryPopup(dialog).IsOpen, Is.False);
        Source(dialog).SelectedItem = history; Pump(dialog);
        Assert.That(Source(dialog).Text, Is.EqualTo(history)); Assert.That(Local(dialog).Text, Is.EqualTo("remembered"));
        Edit(dialog, Local(dialog), "custom/local/path"); Branch(dialog).Text = "typed-branch";
        EditCombo(dialog, Source(dialog), "ssh://private.example.invalid/team/first.git");
        await Until(() => Local(dialog).Text == "first"); Assert.That(Branch(dialog).Text, Is.EqualTo("typed-branch"));
        Edit(dialog, Local(dialog), "custom-again"); EditCombo(dialog, Source(dialog), "git@private.example.invalid:group/second.git");
        await Until(() => Local(dialog).Text == "second"); Assert.That(Add(dialog).IsEnabled, Is.True);
        Assert.That(dialog.IsBusy || dialog.IsBranchLoading, Is.False); Assert.That(dialog.LastProcess, Is.Null);
        Assert.That(File.ReadAllText(f.Trace), Does.Not.Contain("ls-remote"), "Source entry must not start a branch request until its dropdown opens.");
        f.SameHistories(histories);
    }

    [AvaloniaTest]
    public async Task HistoryPopupFiltersMatchingSourcesAndNativeDownEnterAcceptsTheSecondWithoutTakingEditorFocusInBothThemes()
    {
        await using Fixture f = await Fixture.CreateAsync(); Dictionary<string, byte[]?> histories = f.Histories();
        const string prefix = "ssh://private-history.invalid/rem";
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            AddSubmoduleDialog dialog = await f.ShowAsync(); dialog.RequestedThemeVariant = theme; Pump(dialog);
            ComboBox source = Source(dialog); string[] original = source.Items.Cast<string>().ToArray();
            EditCombo(dialog, source, prefix); Popup popup = HistoryPopup(dialog);
            await Until(() => popup.IsOpen && source.Text == Fixture.HistoryFirst);
            ListBox suggestions = HistorySuggestions(popup); TextBox editor = SourceEditor(dialog); Pump(TopLevel.GetTopLevel(suggestions)!);
            Assert.That(suggestions.Items.Cast<string>(), Is.EqualTo(new[] { Fixture.HistoryFirst, Fixture.HistorySecond }));
            Assert.That(editor.SelectedText, Is.EqualTo(Fixture.HistoryFirst[prefix.Length..])); Assert.That(editor.IsKeyboardFocusWithin, Is.True);
            Assert.That(source.IsDropDownOpen, Is.False, "SuggestAppend has its filtered popup while the native combo keeps the whole original MRU.");
            Capture(TopLevel.GetTopLevel(suggestions)!, "add-submodule-history-suggestions-" + theme + ".png");
            Capture(dialog, "add-submodule-history-input-" + theme + ".png");
            Press(dialog, PhysicalKey.ArrowDown);
            Assert.That(source.Text, Is.EqualTo(Fixture.HistorySecond)); Assert.That(editor.SelectedText, Is.EqualTo(Fixture.HistorySecond[prefix.Length..]));
            Assert.That(editor.IsKeyboardFocusWithin, Is.True); Assert.That(popup.IsOpen, Is.True);
            Press(dialog, PhysicalKey.Enter);
            Assert.That(popup.IsOpen, Is.False); Assert.That(source.Text, Is.EqualTo(Fixture.HistorySecond)); Assert.That(editor.SelectedText, Is.Empty);
            Assert.That(editor.IsKeyboardFocusWithin, Is.True); Assert.That(source.Items.Cast<string>(), Is.EqualTo(original));
            Assert.That(dialog.LastProcess, Is.Null); Assert.That(dialog.IsVisible && !dialog.IsBusy, Is.True); f.SameHistories(histories);
            dialog.Close(); await Until(() => !dialog.IsVisible);
        }
    }

    [AvaloniaTest]
    public async Task ContinuousHistoryTypingNarrowsThePopupAndEscapeRestoresTheTypedPrefixWhileProgrammaticAndUnmatchedValuesStayLiteral()
    {
        await using Fixture f = await Fixture.CreateAsync(); Dictionary<string, byte[]?> histories = f.Histories(); AddSubmoduleDialog dialog = await f.ShowAsync();
        ComboBox source = Source(dialog); Popup popup = HistoryPopup(dialog); const string prefix = "ssh://private-history.invalid/rem", narrow = "ssh://private-history.invalid/remembered-t";
        source.Text = prefix; Pump(dialog); Assert.That(popup.IsOpen, Is.False); Assert.That(source.Text, Is.EqualTo(prefix));
        EditCombo(dialog, source, prefix); await Until(() => popup.IsOpen && source.Text == Fixture.HistoryFirst);
        Assert.That(HistorySuggestions(popup).ItemCount, Is.EqualTo(2));
        dialog.KeyTextInput("embered-t"); Pump(dialog);
        await Until(() => popup.IsOpen && source.Text == Fixture.HistorySecond && HistorySuggestions(popup).ItemCount == 1);
        Assert.That(HistorySuggestions(popup).Items.Cast<string>(), Is.EqualTo(new[] { Fixture.HistorySecond }));
        Assert.That(SourceEditor(dialog).SelectedText, Is.EqualTo("wo.git")); Assert.That(SourceEditor(dialog).IsKeyboardFocusWithin, Is.True);
        Press(dialog, PhysicalKey.Escape);
        Assert.That(popup.IsOpen, Is.False); Assert.That(source.Text, Is.EqualTo(narrow)); Assert.That(SourceEditor(dialog).SelectedText, Is.Empty);
        Assert.That(SourceEditor(dialog).CaretIndex, Is.EqualTo(narrow.Length)); Assert.That(dialog.IsVisible, Is.True, "Escape dismisses the suggestion before closing its dialog.");
        const string unmatched = "ssh://private-history.invalid/no-match";
        EditCombo(dialog, source, unmatched); Assert.That(popup.IsOpen, Is.False); Assert.That(source.Text, Is.EqualTo(unmatched)); Assert.That(SourceEditor(dialog).SelectedText, Is.Empty);
        source.Text = prefix; Pump(dialog); Assert.That(popup.IsOpen, Is.False); Assert.That(source.Text, Is.EqualTo(prefix));
        Assert.That(source.Items.Cast<string>(), Is.EqualTo(new CloneSourcesStore().Load())); Assert.That(dialog.LastProcess, Is.Null); f.SameHistories(histories);
    }

    [AvaloniaTest]
    public async Task ExactPointerChoiceAndFocusDisableOrCloseDismissTheHistoryPopupWithoutChangingNativeItemsOrMru()
    {
        await using Fixture f = await Fixture.CreateAsync(); Dictionary<string, byte[]?> histories = f.Histories(); AddSubmoduleDialog dialog = await f.ShowAsync();
        ComboBox source = Source(dialog); string[] original = source.Items.Cast<string>().ToArray(); const string prefix = "ssh://private-history.invalid/rem";
        EditCombo(dialog, source, prefix); Popup popup = HistoryPopup(dialog); await Until(() => popup.IsOpen && source.Text == Fixture.HistoryFirst);
        ListBox suggestions = HistorySuggestions(popup); Pump(TopLevel.GetTopLevel(suggestions)!);
        ListBoxItem second = suggestions.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.Content as string == Fixture.HistorySecond || item.DataContext as string == Fixture.HistorySecond);
        Click(second); await Until(() => !popup.IsOpen && source.Text == Fixture.HistorySecond);
        Assert.That(SourceEditor(dialog).SelectedText, Is.Empty); Assert.That(SourceEditor(dialog).IsKeyboardFocusWithin, Is.True); Assert.That(dialog.LastProcess, Is.Null);
        EditCombo(dialog, source, prefix); await Until(() => popup.IsOpen); Local(dialog).Focus(); Pump(dialog); await Until(() => !popup.IsOpen);
        Assert.That(Local(dialog).IsKeyboardFocusWithin, Is.True);
        EditCombo(dialog, source, prefix); await Until(() => popup.IsOpen); source.IsEnabled = false; Pump(dialog); Assert.That(popup.IsOpen, Is.False);
        source.IsEnabled = true; EditCombo(dialog, source, prefix); await Until(() => popup.IsOpen);
        Assert.That(source.Items.Cast<string>(), Is.EqualTo(original)); dialog.Close(); await Until(() => !dialog.IsVisible && !popup.IsOpen);
        AddSubmoduleDialog next = await f.ShowAsync(); Assert.That(HistoryPopup(next).IsOpen, Is.False); Assert.That(Source(next).Text, Is.Null.Or.Empty);
        EditCombo(next, Source(next), prefix); await Until(() => HistoryPopup(next).IsOpen); Assert.That(popup.IsOpen, Is.False, "A disposed window cannot reopen its previous suggestion popup.");
        Assert.That(Source(next).Items.Cast<string>(), Is.EqualTo(original)); Assert.That(next.LastProcess, Is.Null); Assert.That(await f.Root.Repository.GetSubmodulesAsync(), Is.Empty); f.SameHistories(histories);
    }

    [AvaloniaTest]
    public async Task NativeBranchDropdownContainsOnlyHeadsAndPreservesTheManuallyTypedBranch()
    {
        await using Fixture f = await Fixture.CreateAsync(); Dictionary<string, byte[]?> histories = f.Histories();
        AddSubmoduleDialog dialog = await f.ShowAsync(); Fill(dialog, f.Source.Path, "modules/dropdown", "manual/choice");
        await GitAsync(f.Source.Path, "tag", "not-a-branch"); OpenDropdown(dialog, Branch(dialog));
        await Until(() => !dialog.IsBranchLoading && Branches(dialog).Contains(Fixture.Topic));
        Assert.That(Branches(dialog), Is.EqualTo(new[] { "main", Fixture.Topic })); Assert.That(Branch(dialog).Text, Is.EqualTo("manual/choice"));
        Assert.That(dialog.IsBusy, Is.False); Assert.That(Add(dialog).IsEnabled, Is.True); Assert.That(Source(dialog).IsEnabled && Local(dialog).IsEnabled, Is.True);
        Assert.That(dialog.BranchLoadError, Is.Null); Assert.That(dialog.BranchLoadFailures, Is.Empty);
        Branch(dialog).IsDropDownOpen = false; Branch(dialog).Focus(); Pump(dialog); Assert.That(Branch(dialog).IsKeyboardFocusWithin, Is.True);
        Assert.That(dialog.LastProcess, Is.Null); Assert.That(await f.Root.Repository.GetSubmodulesAsync(), Is.Empty); f.SameHistories(histories);
    }

    [AvaloniaTest]
    public async Task SourceChangesDrainTheOldBranchTransportWithoutPublishingItsHeadsOrReplacingANewerTypedChoice()
    {
        LinuxOnly(); await using Fixture f = await Fixture.CreateAsync(); Gate gate = await f.TransportAsync();
        Dictionary<string, byte[]?> histories = f.Histories(); AddSubmoduleDialog dialog = await f.ShowAsync(); Fill(dialog, Fixture.SshSource, "modules/stale", "first/manual");
        OpenDropdown(dialog, Branch(dialog)); await Until(() => gate.Started && dialog.IsBranchLoading); Actor[] actors = gate.Actors();
        Assert.That(actors.All(actor => actor.Alive()), Is.True); Branch(dialog).IsDropDownOpen = false;
        EditCombo(dialog, Source(dialog), f.Other.Path); EditCombo(dialog, Branch(dialog), "new/manual"); OpenDropdown(dialog, Branch(dialog));
        await Until(() => !dialog.IsBranchLoading && Branches(dialog).Contains("fresh-only") && actors.All(actor => !actor.Alive()));
        Assert.That(Branches(dialog), Is.EqualTo(new[] { "fresh-only", "main" })); Assert.That(Branches(dialog), Does.Not.Contain(Fixture.Topic));
        Assert.That(Branch(dialog).Text, Is.EqualTo("new/manual")); Assert.That(Source(dialog).Text, Is.EqualTo(f.Other.Path));
        Assert.That(dialog.BranchLoadError, Is.Null); Assert.That(dialog.BranchLoadFailures, Is.Empty); Assert.That(dialog.LastError, Is.Null);
        Assert.That(dialog.LastProcess, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.False); f.SameHistories(histories);
    }

    [AvaloniaTest]
    public async Task AFailedBranchReadKeepsItsNativeErrorAndEditableFieldsThenRecoversWithoutStartingAdd()
    {
        await using Fixture f = await Fixture.CreateAsync(); AddSubmoduleDialog dialog = await f.ShowAsync();
        string missing = f.Root.Output("missing source 'λ"); Fill(dialog, missing, "modules/unchanged", "manual/choice"); OpenDropdown(dialog, Branch(dialog));
        await Until(() => !dialog.IsBranchLoading && dialog.BranchLoadError is GitCommandException);
        GitCommandException failure = (GitCommandException)dialog.BranchLoadError!;
        Assert.That(failure.Result.Success, Is.False); Assert.That(failure.Result.StandardError, Is.Not.Empty);
        Assert.That(dialog.BranchLoadFailures.Last(), Is.SameAs(failure)); Assert.That(Branch(dialog).Text, Is.EqualTo("manual/choice"));
        Assert.That(Local(dialog).Text, Is.EqualTo("modules/unchanged")); Assert.That(dialog.IsVisible && !dialog.IsBusy, Is.True); Assert.That(Add(dialog).IsEnabled, Is.True);
        Branch(dialog).IsDropDownOpen = false;
        await f.Root.GitAsync("clone", "--bare", "--", f.Source.Path, missing);
        Branch(dialog).Text = "typed-after-error"; OpenDropdown(dialog, Branch(dialog));
        await Until(() => !dialog.IsBranchLoading && Branches(dialog).Contains(Fixture.Topic));
        Assert.That(dialog.BranchLoadError, Is.Null); Assert.That(dialog.BranchLoadFailures, Does.Contain(failure)); Assert.That(Branch(dialog).Text, Is.EqualTo("typed-after-error"));
        Assert.That(Source(dialog).Text, Is.EqualTo(missing)); Assert.That(Local(dialog).Text, Is.EqualTo("modules/unchanged"));
        Assert.That(Field<TextBlock>(dialog, "OperationStatus").Text, Is.Null.Or.Empty, "Retrying the same now-valid source must clear its completed native error.");
        Assert.That(dialog.LastProcess, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.False);
    }

    [AvaloniaTest]
    public async Task RealAdditionChecksOutTheChosenBranchAndClosesOnlyAfterTheOwnedProcessIsAcknowledgedWithoutVisitingMru()
    {
        await using Fixture f = await Fixture.CreateAsync(); Dictionary<string, byte[]?> histories = f.Histories(); string beforeSource = f.SourceMaterials();
        const string path = "modules/lib 'λ;$()"; AddSubmoduleDialog dialog = await f.ShowAsync(); Fill(dialog, f.Source.Path, path, Fixture.Topic);
        Click(Add(dialog)); GitProcessDialog process = await FinishedProcessAsync(dialog);
        Assert.That(process.Owner, Is.SameAs(dialog)); Assert.That(process.Outcome?.Success, Is.True, process.Error?.ToString());
        Assert.That(dialog.IsVisible && dialog.IsBusy, Is.True); Assert.That(dialog.AddedSubmodulePath, Is.Null);
        string fullPath = f.Root.PathInRepository(path); Assert.That(await GitTextAsync(fullPath, "symbolic-ref", "--short", "HEAD"), Is.EqualTo(Fixture.Topic));
        Assert.That(await new GitRepository(fullPath).ResolveRevisionAsync("HEAD"), Is.EqualTo(f.TopicHead));
        Assert.That(await GitTextAsync(f.Root.Repository.Path, "config", "--file", ".gitmodules", "--get", "submodule." + path + ".branch"), Is.EqualTo(Fixture.Topic));
        Assert.That((await f.Root.Repository.GetSubmodulesAsync()).Single().Path, Is.EqualTo(path)); f.SameHistories(histories);
        Capture(process, "add-submodule-process-success.png"); Click(Field<Button>(process, "Ok")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.AddedSubmodulePath, Is.EqualTo(path)); Assert.That(dialog.LastProcess?.Success, Is.True); Assert.That(dialog.HasRepositoryChanges, Is.True);
        Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.OwnedWindows, Is.Empty); f.SameHistories(histories); Assert.That(f.SourceMaterials(), Is.EqualTo(beforeSource));
    }

    [AvaloniaTest]
    public async Task NativeIgnoredPathFailureKeepsAllFieldsAndLocalFocusThenExplicitForceAllowsTheSameAddition()
    {
        await using Fixture f = await Fixture.CreateAsync(); await File.WriteAllTextAsync(f.Root.PathInRepository(".gitignore"), "ignored/\n"); await f.Root.GitAsync("add", ".gitignore"); await f.Root.GitAsync("commit", "-m", "Ignore test destination");
        Dictionary<string, byte[]?> histories = f.Histories(); string beforeIndex = await GitTextAsync(f.Root.Repository.Path, "write-tree");
        AddSubmoduleDialog dialog = await f.ShowAsync(); const string path = "ignored/module"; Fill(dialog, f.Source.Path, path, Fixture.Topic);
        Click(Add(dialog)); GitProcessDialog failed = await FinishedProcessAsync(dialog);
        Assert.That(failed.Outcome?.Success, Is.False); Assert.That(failed.Result?.ExitCode, Is.Not.Null.And.Not.Zero); Assert.That(failed.Result?.StandardError, Does.Contain("ignored"));
        GitResult nativeFailure = failed.Result!;
        Assert.That(failed.IsVisible && dialog.IsBusy, Is.True); Click(Field<Button>(failed, "Ok")); await Until(() => !dialog.IsBusy);
        AssertFields(dialog, f.Source.Path, path, Fixture.Topic, false); Assert.That(dialog.LastError, Is.Not.Null.And.Not.Empty); Assert.That(dialog.IsVisible, Is.True);
        Assert.That(dialog.LastException, Is.TypeOf<GitCommandException>()); Assert.That(((GitCommandException)dialog.LastException!).Result, Is.SameAs(nativeFailure));
        Assert.That(Local(dialog).IsKeyboardFocusWithin, Is.True); Assert.That(dialog.AddedSubmodulePath, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.True);
        Assert.That(await GitTextAsync(f.Root.Repository.Path, "write-tree"), Is.EqualTo(beforeIndex)); f.SameHistories(histories);
        Capture(dialog, "add-submodule-native-error.png"); Click(Force(dialog)); Assert.That(Force(dialog).IsChecked, Is.True); Click(Add(dialog));
        GitProcessDialog success = await FinishedProcessAsync(dialog); Assert.That(success.Outcome?.Success, Is.True, success.Error?.ToString());
        Click(Field<Button>(success, "Ok")); await Until(() => !dialog.IsBusy && !dialog.IsVisible);
        Assert.That(dialog.AddedSubmodulePath, Is.EqualTo(path)); Assert.That(await GitTextAsync(f.Root.Repository.Path, "ls-files", "--stage", "--", path), Does.StartWith("160000 "));
        Assert.That(await File.ReadAllTextAsync(f.Root.PathInRepository(".gitignore")), Is.EqualTo("ignored/\n")); f.SameHistories(histories);
    }

    [AvaloniaTest]
    public async Task AnExistingGitRepositoryAtTheDestinationIsAcceptedByNativeGitWithoutForceOrReplacingItsBytes()
    {
        await using Fixture f = await Fixture.CreateAsync(); const string path = "already exists"; string fullPath = f.Root.PathInRepository(path);
        await f.Root.GitAsync("clone", "--", f.Source.Path, fullPath); string beforeHead = await new GitRepository(fullPath).ResolveRevisionAsync("HEAD");
        await File.WriteAllBytesAsync(Path.Combine(fullPath, "untracked λ.bin"), [0, 255, 12]);
        AddSubmoduleDialog dialog = await f.ShowAsync(); Fill(dialog, f.Source.Path, path, ""); Assert.That(Add(dialog).IsEnabled, Is.True);
        Click(Add(dialog)); GitProcessDialog process = await FinishedProcessAsync(dialog); Assert.That(process.Outcome?.Success, Is.True, process.Error?.ToString());
        Click(Field<Button>(process, "Ok")); await Until(() => !dialog.IsBusy && !dialog.IsVisible);
        Assert.That(dialog.AddedSubmodulePath, Is.EqualTo(path)); Assert.That(await new GitRepository(fullPath).ResolveRevisionAsync("HEAD"), Is.EqualTo(beforeHead));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(fullPath, "untracked λ.bin")), Is.EqualTo(new byte[] { 0, 255, 12 }));
        Assert.That((await f.Root.Repository.GetSubmodulesAsync()).Single().Path, Is.EqualTo(path)); Assert.That(dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    public async Task LinkedWorktreeAdditionAndItsBranchLookupResolveRelativeSourceFromThatWorktree()
    {
        await using Fixture f = await Fixture.CreateAsync(); string linked = f.Root.Output("linked-add"); await f.Root.GitAsync("worktree", "add", "-b", "linked-add", "--", linked);
        GitRepository linkedRepository = await GitRepository.OpenAsync(linked); string source = "../" + Path.GetFileName(f.Source.Path);
        AddSubmoduleDialog dialog = await f.ShowAsync(linkedRepository); Fill(dialog, source, "deps/relative", Fixture.Topic); OpenDropdown(dialog, Branch(dialog));
        await Until(() => !dialog.IsBranchLoading && Branches(dialog).Contains(Fixture.Topic)); Branch(dialog).IsDropDownOpen = false;
        Click(Add(dialog)); GitProcessDialog process = await FinishedProcessAsync(dialog); Assert.That(process.Outcome?.Success, Is.True, process.Error?.ToString());
        Click(Field<Button>(process, "Ok")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.AddedSubmodulePath, Is.EqualTo("deps/relative")); Assert.That((await linkedRepository.GetSubmodulesAsync()).Single().Path, Is.EqualTo("deps/relative"));
        Assert.That(await new GitRepository(Path.Combine(linked, "deps/relative")).ResolveRevisionAsync("HEAD"), Is.EqualTo(f.TopicHead));
        Assert.That(await GitTextAsync(linked, "config", "--file", ".gitmodules", "--get", "submodule.deps/relative.url"), Is.EqualTo(source));
        Assert.That(File.Exists(f.Root.PathInRepository(".gitmodules")), Is.False); Assert.That(await f.Root.Repository.GetSubmodulesAsync(), Is.Empty);
        Assert.That(await GitTextAsync(f.Root.Repository.Path, "status", "--porcelain"), Is.Empty);
    }

    [AvaloniaTest]
    public async Task BareRepositoryCannotStartAddEvenWithOtherwiseValidSourceAndPath()
    {
        await using Fixture f = await Fixture.CreateAsync(); string bare = f.Root.Output("bare.git"); await f.Root.GitAsync("clone", "--bare", "--", f.Root.Repository.Path, bare);
        GitRepository repository = await GitRepository.OpenAsync(bare); AddSubmoduleDialog dialog = await f.ShowAsync(repository); Fill(dialog, f.Source.Path, "module", Fixture.Topic);
        Assert.That(Add(dialog).IsEnabled, Is.False); Assert.That(dialog.LastProcess, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.False);
        Assert.That(File.Exists(Path.Combine(bare, ".gitmodules")), Is.False);
    }

    [AvaloniaTest]
    public async Task AbortAndOwnerCloseDrainTheActualAddTransportAndNeverPublishSuccessOrRememberTheSource()
    {
        LinuxOnly();
        foreach (bool ownerClose in new[] { false, true })
        {
            await using Fixture f = await Fixture.CreateAsync(); Gate gate = await f.TransportAsync(); Window owner = f.Owner();
            AddSubmoduleDialog dialog = await f.ShowAsync(owner: owner); Dictionary<string, byte[]?> histories = f.Histories(); const string path = "modules/cancelled";
            Fill(dialog, Fixture.SshSource, path, "main", force: true); Click(Add(dialog)); GitProcessDialog process = await ProcessAsync(dialog);
            await Until(() => gate.Started && process.IsRunning); Actor[] actors = gate.Actors(); Assert.That(actors.All(actor => actor.Alive()), Is.True);
            if (ownerClose) { owner.Close(); }
            else { Click(Field<Button>(process, "Abort")); }
            await Until(() => !dialog.IsBusy && actors.All(actor => !actor.Alive()) && (!ownerClose || !dialog.IsVisible && !owner.IsVisible));
            Assert.That(process.IsRunning || process.IsVisible, Is.False); Assert.That(dialog.LastProcess?.WasCancelled, Is.True); Assert.That(dialog.LastProcess?.WorkflowStopRequested, Is.True);
            Assert.That(dialog.AddedSubmodulePath, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.True); f.SameHistories(histories);
            if (!ownerClose)
            {
                Assert.That(dialog.IsVisible && owner.IsVisible, Is.True); AssertFields(dialog, Fixture.SshSource, path, "main", true);
                Assert.That(Add(dialog).IsEnabled, Is.True); Assert.That(Local(dialog).IsKeyboardFocusWithin, Is.True);
            }
        }
    }

    [AvaloniaTest]
    public async Task ClosingDuringABranchReadDrainsItsTransportWithoutStartingAnAddOrChangingRepositoryState()
    {
        LinuxOnly(); await using Fixture f = await Fixture.CreateAsync(); Gate gate = await f.TransportAsync(); Window owner = f.Owner(); AddSubmoduleDialog dialog = await f.ShowAsync(owner: owner);
        string before = await GitTextAsync(f.Root.Repository.Path, "write-tree"); Fill(dialog, Fixture.SshSource, "modules/not-added", "manual");
        OpenDropdown(dialog, Branch(dialog)); await Until(() => gate.Started && dialog.IsBranchLoading); Actor[] actors = gate.Actors();
        owner.Close(); await Until(() => !dialog.IsVisible && !owner.IsVisible && !dialog.IsBranchLoading && actors.All(actor => !actor.Alive()));
        Assert.That(dialog.LastProcess, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(dialog.AddedSubmodulePath, Is.Null);
        Assert.That(await GitTextAsync(f.Root.Repository.Path, "write-tree"), Is.EqualTo(before)); Assert.That(await f.Root.Repository.GetSubmodulesAsync(), Is.Empty);
    }

    [AvaloniaTest]
    public async Task ClosingASuccessfulProcessBeforeOkKeepsTheActualAddedModuleButDoesNotCloseOrPublishTheForm()
    {
        await using Fixture f = await Fixture.CreateAsync(); Dictionary<string, byte[]?> histories = f.Histories(); AddSubmoduleDialog dialog = await f.ShowAsync();
        const string path = "modules/stopped-after-success"; Fill(dialog, f.Source.Path, path, Fixture.Topic); Click(Add(dialog)); GitProcessDialog process = await FinishedProcessAsync(dialog);
        Assert.That(process.Outcome?.Success, Is.True, process.Error?.ToString()); Assert.That(dialog.AddedSubmodulePath, Is.Null);
        process.Close(); await Until(() => !dialog.IsBusy && !process.IsVisible);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.LastProcess?.Success, Is.True); Assert.That(dialog.LastProcess?.WorkflowStopRequested, Is.True);
        Assert.That(Field<TextBlock>(dialog, "OperationStatus").Text, Is.Not.EqualTo("Add submodule…"), "A completed command must not leave a false running status after Process closes.");
        Assert.That(dialog.AddedSubmodulePath, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.True); AssertFields(dialog, f.Source.Path, path, Fixture.Topic, false);
        Assert.That((await f.Root.Repository.GetSubmodulesAsync()).Single().Path, Is.EqualTo(path)); Assert.That(await new GitRepository(f.Root.PathInRepository(path)).ResolveRevisionAsync("HEAD"), Is.EqualTo(f.TopicHead));
        f.SameHistories(histories);
    }

    [AvaloniaTest]
    public async Task MainWindowUsesTheSharedDialogAndRefreshesItsSubmoduleListAfterSuccessAndAnAttemptedFailure()
    {
        await using Fixture f = await Fixture.CreateAsync(); MainWindow main = new() { Width = 1440, Height = 900 }; f.Windows.Add(main); main.Show(); await main.OpenRepositoryAsync(f.Root.Repository.Path); await Until(() => !main.IsBusy);
        MenuItem repository = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => Header(item.Header) == "Repository");
        repository.Items.OfType<MenuItem>().Single(item => Header(item.Header) == "Submodules…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => main.OwnedWindows.Any(window => window.Title?.StartsWith("Submodules", StringComparison.Ordinal) == true));
        Window panel = main.OwnedWindows.Single(window => window.Title?.StartsWith("Submodules", StringComparison.Ordinal) == true);
        Button add = Field<Button>(panel, "AddSubmodule"); await Until(() => !((RepositoryDialog)panel).IsBusy && add.IsEnabled); Click(add);
        AddSubmoduleDialog dialog = await OwnedAsync<AddSubmoduleDialog>(main); Assert.That(dialog.Owner, Is.SameAs(panel)); Fill(dialog, f.Source.Path, "modules/from-main", Fixture.Topic);
        Click(Add(dialog)); GitProcessDialog process = await FinishedProcessAsync(dialog); Assert.That(process.Outcome?.Success, Is.True, process.Error?.ToString()); Click(Field<Button>(process, "Ok"));
        await Until(() => !((RepositoryDialog)panel).IsBusy && !dialog.IsVisible && Field<ListBox>(panel, "RemovalSubmodules").Items.OfType<GitSubmoduleSnapshot>().Any(module => module.Path == "modules/from-main"));
        Assert.That(Field<TextBox>(main, "RepositoryPath").Text, Is.EqualTo(f.Root.Repository.Path));
        Click(add); dialog = await OwnedAsync<AddSubmoduleDialog>(main); Fill(dialog, f.Root.Output("missing-native-source"), "modules/failure", "");
        Click(Add(dialog)); process = await FinishedProcessAsync(dialog); Assert.That(process.Outcome?.Success, Is.False);
        await f.Root.GitAsync("-c", "protocol.file.allow=always", "submodule", "add", "--", f.Other.Path, "modules/external-during-attempt");
        Click(Field<Button>(process, "Ok")); await Until(() => !dialog.IsBusy); Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.HasRepositoryChanges, Is.True);
        dialog.Close(); await Until(() => !((RepositoryDialog)panel).IsBusy && Field<ListBox>(panel, "RemovalSubmodules").Items.OfType<GitSubmoduleSnapshot>().Count() == 2);
        Assert.That(Field<ListBox>(panel, "RemovalSubmodules").Items.OfType<GitSubmoduleSnapshot>().Select(module => module.Path), Is.EquivalentTo(new[] { "modules/from-main", "modules/external-during-attempt" }));
        Assert.That(main.LastException, Is.Null);
    }

    private static ComboBox Source(AddSubmoduleDialog dialog) => Field<ComboBox>(dialog, "AddSubmoduleSource");
    private static ComboBox Branch(AddSubmoduleDialog dialog) => Field<ComboBox>(dialog, "AddSubmoduleBranch");
    private static TextBox Local(AddSubmoduleDialog dialog) => Field<TextBox>(dialog, "AddSubmoduleLocalPath");
    private static CheckBox Force(AddSubmoduleDialog dialog) => Field<CheckBox>(dialog, "AddSubmoduleForce");
    private static Button Add(AddSubmoduleDialog dialog) => Field<Button>(dialog, "AddSubmodule");
    private static TextBox SourceEditor(AddSubmoduleDialog dialog) => Source(dialog).GetVisualDescendants().OfType<TextBox>().Single();
    private static Popup HistoryPopup(AddSubmoduleDialog dialog) => Field<Popup>(dialog, "AddSubmoduleSourceHistoryCompletionPopup");
    private static ListBox HistorySuggestions(Popup popup) => popup.Child as ListBox ?? popup.Child!.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "AddSubmoduleSourceHistorySuggestions");
    private static string[] Branches(AddSubmoduleDialog dialog) => Branch(dialog).Items.Cast<string>().ToArray();
    private static void Fill(AddSubmoduleDialog dialog, string source, string path, string branch, bool force = false)
    { Source(dialog).Text = source; Local(dialog).Text = path; Branch(dialog).Text = branch; Force(dialog).IsChecked = force; Pump(dialog); }
    private static void AssertFields(AddSubmoduleDialog dialog, string source, string path, string branch, bool force)
    { Assert.That(Source(dialog).Text, Is.EqualTo(source)); Assert.That(Local(dialog).Text, Is.EqualTo(path)); Assert.That(Branch(dialog).Text, Is.EqualTo(branch)); Assert.That(Force(dialog).IsChecked, Is.EqualTo(force)); }
    private static void LinuxOnly() { if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private local transport actor identity requires Linux procfs."); } }
    private static string Header(object? value) => value is AccessText access ? (access.Text ?? "").Replace("_", "", StringComparison.Ordinal) : value?.ToString() ?? "";
    private static T Field<T>(TopLevel root, string name) where T : Control => root.GetVisualDescendants().OfType<Control>().Concat(root.GetLogicalDescendants().OfType<Control>()).Distinct().OfType<T>().Single(control => control.Name == name);
    private static void Click(Control control) => PushProcessTestDriver.Pointer(control);
    private static void OpenDropdown(Window window, ComboBox combo)
    {
        for (int frame = 0; frame < 10; frame++) { Pump(window); }
        Point point = combo.TranslatePoint(new Point(combo.Bounds.Width - 8, combo.Bounds.Height / 2), window)!.Value;
        Visual? hit = window.InputHitTest(point) as Visual;
        Assert.That(combo.IsEffectivelyVisible && combo.IsEffectivelyEnabled && hit?.GetSelfAndVisualAncestors().Contains(combo) == true, Is.True, "The actual editable combo arrow must be hit-testable.");
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump(window);
        Assert.That(combo.IsDropDownOpen, Is.True);
    }
    private static void EditCombo(Window window, ComboBox combo, string value)
    { Pump(window); Edit(window, combo.GetVisualDescendants().OfType<TextBox>().Single(), value); }
    private static void Edit(Window window, TextBox editor, string value)
    { Click(editor); Press(window, PhysicalKey.A, RawInputModifiers.Control); window.KeyTextInput(value); Pump(window); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static async Task<T> OwnedAsync<T>(Window root) where T : RepositoryDialog
    { await Until(() => PushProcessTestDriver.Windows(root).OfType<T>().Any(dialog => dialog.IsVisible && !dialog.IsBusy)); T dialog = PushProcessTestDriver.Windows(root).OfType<T>().Single(dialog => dialog.IsVisible); Pump(dialog); return dialog; }
    private static async Task<GitProcessDialog> ProcessAsync(Window owner)
    { await Until(() => PushProcessTestDriver.Windows(owner).OfType<GitProcessDialog>().Any(process => process.IsVisible)); return PushProcessTestDriver.Windows(owner).OfType<GitProcessDialog>().Single(process => process.IsVisible); }
    private static async Task<GitProcessDialog> FinishedProcessAsync(Window owner)
    { GitProcessDialog process = await ProcessAsync(owner); await Until(() => !process.IsRunning); return process; }
    private static async Task<GitResult> GitAsync(string path, params string[] arguments)
    { GitResult result = await new GitRunner().RunAsync(path, arguments); Assert.That(result.Success, Is.True, result.StandardError); return result; }
    private static async Task<string> GitTextAsync(string path, params string[] arguments) => (await GitAsync(path, arguments)).StandardOutput.TrimEnd('\r', '\n');
    private static async Task Until(Func<bool> condition)
    { Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(25)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True, "The real Add submodule workflow did not settle."); }
    private static void Pump(TopLevel root)
    { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static Rect Bounds(Window window, Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void AssertBounds(Window window, Control control, Rect expected)
    { Rect actual = Bounds(window, control); Assert.That(actual.X, Is.EqualTo(expected.X).Within(2), control.Name); Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(2), control.Name); Assert.That(actual.Width, Is.EqualTo(expected.Width).Within(2), control.Name); Assert.That(actual.Height, Is.EqualTo(expected.Height).Within(2), control.Name); }
    private static void AssertInside(Window window, params Control[] controls)
    { foreach (Control control in controls) { Rect bounds = Bounds(window, control); Assert.That(control.IsEffectivelyVisible, Is.True, control.Name); Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(0), control.Name); Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0), control.Name); Assert.That(bounds.Right, Is.LessThanOrEqualTo(window.ClientSize.Width), control.Name); Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(window.ClientSize.Height), control.Name); } }
    private static void Capture(TopLevel window, string name)
    { Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }

    private sealed record Actor(int Pid, long Start)
    {
        internal static Actor Read(int pid)
        { string stat = File.ReadAllText("/proc/" + pid + "/stat"); return new(pid, long.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19], CultureInfo.InvariantCulture)); }
        internal bool Alive()
        {
            try { string stat = File.ReadAllText("/proc/" + Pid + "/stat"); string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries); return fields[0] is not ("Z" or "X" or "x") && long.Parse(fields[19], CultureInfo.InvariantCulture) == Start; }
            catch (IOException) { return false; }
        }
    }
    private sealed record Gate(string Marker, string Release)
    {
        private Actor[]? _actors;
        internal bool Started => File.Exists(Marker);
        internal Actor[] Actors() => _actors ??= File.ReadAllText(Marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(value => Actor.Read(int.Parse(value, CultureInfo.InvariantCulture))).ToArray();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal const string Topic = "topic/λ+literal", SshSource = "ssh://private-add-submodule.invalid/owned-local.git";
        internal const string HistoryFirst = "ssh://private-history.invalid/remembered.git", HistorySecond = "ssh://private-history.invalid/remembered-two.git";
        internal RootFixture Root { get; }
        internal GitRepository Source { get; private set; } = null!;
        internal GitRepository Other { get; private set; } = null!;
        internal string TopicHead { get; private set; } = "";
        internal string Trace => Root.Output("native-add.trace");
        internal List<Window> Windows { get; } = [];
        private readonly List<Gate> _gates = [];
        private Fixture(RootFixture root) => Root = root;
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture f = new(await RootFixture.CreateAsync());
            try
            {
                string global = f.Root.Output("private-global.gitconfig"); await File.WriteAllTextAsync(global, "[protocol \"file\"]\n\tallow = always\n");
                foreach ((string key, string? value) in new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = global, ["GIT_ALLOW_PROTOCOL"] = "file:ssh", ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_SSH_COMMAND"] = null, ["GIT_SSH"] = null, ["GIT_TRACE"] = f.Trace }) { f.Root.SetEnvironment(key, value); }
                f.Source = await f.SeedAsync("source 'λ;$().git", Topic); f.TopicHead = await f.Source.ResolveRevisionAsync(Topic);
                await File.WriteAllTextAsync(Path.Combine(f.Source.Path, "file.txt"), "newer main contents\n"); await f.Source.StageAllAsync(); await f.Source.CommitAsync("Newer source main");
                f.Other = await f.SeedAsync("other.git", "fresh-only");
                CloneSourcesStore sources = new(); sources.Visit("ssh://other-history.invalid/unrelated.git", 30); sources.Visit(HistorySecond, 30); sources.Visit(HistoryFirst, 30);
                new RepositoryHistoryStore().Save(new() { Recent = [new(f.Root.Repository.Path)] });
                new ProcessDialogPreferencesStore().Save(new(false)); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        private async Task<GitRepository> SeedAsync(string name, string branch)
        {
            GitRepository repository = await GitRepository.InitAsync(Root.Output(name), new GitInitOptions("main"));
            await repository.SetConfigAsync("user.name", "Add Submodule UI"); await repository.SetConfigAsync("user.email", "add-submodule@example.invalid");
            await repository.SetConfigAsync("commit.gpgsign", "false"); await repository.SetConfigAsync("core.hooksPath", "/dev/null");
            await File.WriteAllTextAsync(Path.Combine(repository.Path, "file.txt"), name + "\n"); await repository.StageAllAsync(); await repository.CommitAsync("Source seed"); await repository.CreateBranchAsync(branch, checkout: false); return repository;
        }
        internal Window Owner() { Window window = new() { Width = 900, Height = 600 }; Windows.Add(window); window.Show(); Pump(window); return window; }
        internal async Task<AddSubmoduleDialog> ShowAsync(GitRepository? repository = null, Window? owner = null)
        {
            AddSubmoduleDialog dialog = new(repository ?? Root.Repository); Windows.Add(dialog);
            if (owner is null) { dialog.Show(); } else { _ = dialog.ShowDialog(owner); }
            await dialog.InitializeAsync(); Pump(dialog); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); return dialog;
        }
        internal Dictionary<string, byte[]?> Histories() => new[] { new CloneSourcesStore().FilePath, new RepositoryHistoryStore().FilePath }.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        internal void SameHistories(Dictionary<string, byte[]?> before)
        { foreach ((string path, byte[]? bytes) in before) { Assert.That(File.Exists(path), Is.EqualTo(bytes is not null), path); if (bytes is not null) { Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), path); } } }
        internal string SourceMaterials() => string.Join('\n', Directory.EnumerateFiles(Source.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(Source.Path, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        internal async Task<Gate> TransportAsync()
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Private local transport actor identity requires Linux procfs."); }
            string script = Root.Output("private local add ssh"), marker = script + ".actors", release = script + ".release";
            await File.WriteAllTextAsync(script, "#!/bin/sh\ncase \" $* \" in *private-add-submodule.invalid*) ;; *) exit 89 ;; esac\n"
                + "sleep 60 &\nchild=$!\ntrap 'kill \"$child\" 2>/dev/null || :; wait \"$child\" 2>/dev/null || :' 0\n"
                + "printf '%s %s %s\\n' \"$$\" \"$PPID\" \"$child\" > " + Quote(marker + ".tmp") + "\nmv " + Quote(marker + ".tmp") + " " + Quote(marker) + "\n"
                + "while test ! -f " + Quote(release) + "; do sleep 0.02; done\nkill \"$child\" 2>/dev/null || :\nwait \"$child\" 2>/dev/null || :\ntrap - 0\nexec /usr/bin/git upload-pack " + Quote(Source.Path) + "\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Root.SetEnvironment("GIT_SSH", script); Root.SetEnvironment("GIT_SSH_COMMAND", null); Root.SetEnvironment("GIT_SSH_VARIANT", "ssh"); Gate gate = new(marker, release); _gates.Add(gate); return gate;
        }
        private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window window in Windows.AsEnumerable().Reverse()) { CloseOwned(window); }
                await Until(() => Windows.All(window => !window.IsVisible && (window is not RepositoryDialog dialog || !dialog.IsBusy)));
            }
            finally
            {
                foreach (Gate gate in _gates.Where(gate => gate.Started))
                {
                    try
                    {
                        foreach (Actor actor in gate.Actors().Where(actor => actor.Alive())) { try { Process.GetProcessById(actor.Pid).Kill(entireProcessTree: true); } catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { } }
                    }
                    catch (IOException) { }
                }
                Root.Dispose();
            }
        }
        private static void CloseOwned(Window window) { foreach (Window child in window.OwnedWindows.ToArray()) { CloseOwned(child); } if (window.IsVisible) { window.Close(); } }
    }
}

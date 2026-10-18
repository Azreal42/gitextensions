using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class HotkeyHostDialogTests
{
    private const string Initial = "root\nend\n";
    private const string Changed = "root\nselected addition\nremaining addition\nend\n";
    private const string SelectedLine = "+selected addition";

    [AvaloniaTest]
    public async Task CommitUsesRemappedFocusFilterAndStageAllCommandsAndDoesNotKeepTheOldBindings()
    {
        await using Scope f = await Scope.CreateAsync(); await f.SeedChangesAsync();
        HotkeyBindings bindings = new();
        Set(bindings, "Commit", "FocusUnstagedFiles", Key.F6); Set(bindings, "Commit", "FocusSelectedDiff", Key.F7);
        Set(bindings, "Commit", "FocusStagedFiles", Key.F8); Set(bindings, "Commit", "FocusCommitMessage", Key.F9);
        Set(bindings, "Commit", "StageAll", Key.F10); Set(bindings, "Commit", "ToggleSelectionFilter", Key.F11);
        Save(bindings); CommitDialog dialog = await f.CommitAsync();
        DiffView diff = Field<DiffView>(dialog, "SelectedDiff"); TextBox message = Field<TextBox>(dialog, "Message");
        Press(dialog, PhysicalKey.F9); Assert.That(message.IsKeyboardFocusWithin, Is.True);
        dialog.KeyTextInput("typed into the actual message"); Assert.That(message.Text, Is.EqualTo("typed into the actual message"));
        Press(dialog, PhysicalKey.F6); Assert.That(Field<TreeView>(dialog, "Unstaged").IsKeyboardFocusWithin, Is.True);
        Press(dialog, PhysicalKey.Digit4, RawInputModifiers.Control);
        Assert.That(Field<TreeView>(dialog, "Unstaged").IsKeyboardFocusWithin, Is.True, "The old Ctrl+4 must no longer focus the message.");
        Press(dialog, PhysicalKey.F7); Assert.That(diff.Editor.TextArea.IsFocused, Is.True);
        Press(dialog, PhysicalKey.F8); Assert.That(Field<TreeView>(dialog, "Staged").IsKeyboardFocusWithin, Is.True);
        Press(dialog, PhysicalKey.F11); Assert.That(Field<Control>(dialog, "SelectionFilterToolbar").IsVisible, Is.True);
        Press(dialog, PhysicalKey.F11); Assert.That(Field<Control>(dialog, "SelectionFilterToolbar").IsVisible, Is.False);
        Press(dialog, PhysicalKey.F6); Press(dialog, PhysicalKey.S, RawInputModifiers.Control); await Idle(dialog);
        Assert.That((await f.Git.GitAsync("diff", "--cached")).StandardOutput, Is.Empty, "The former Ctrl+S must not stage anything after remapping.");
        Press(dialog, PhysicalKey.F10); await Idle(dialog);
        foreach (string path in new[] { "a.txt", "file.txt", "z.txt" })
        {
            Assert.That(await f.IndexAsync(path), Is.EqualTo(Changed));
            Assert.That(await File.ReadAllTextAsync(f.Git.PathInRepository(path)), Is.EqualTo(Changed));
        }
        Assert.That(ToolTip.GetTip(Field<Button>(dialog, "StageAll")), Does.Contain("F10"));
        Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(dialog.HasCommitted, Is.False);
    }

    [AvaloniaTest]
    [TestCase("SelectNext", false)]
    [TestCase("SelectNext_AlternativeHotkey1", false)]
    [TestCase("SelectNext_AlternativeHotkey2", false)]
    [TestCase("SelectPrevious", true)]
    [TestCase("SelectPrevious_AlternativeHotkey1", true)]
    [TestCase("SelectPrevious_AlternativeHotkey2", true)]
    public async Task EveryCommitNextPreviousAlternativeIsAnIndependentConfigurableCommand(string name, bool backwards)
    {
        await using Scope f = await Scope.CreateAsync(); await f.SeedChangesAsync();
        HotkeyBindings bindings = new();
        foreach (HotkeyCommand command in HotkeyCatalog.For("Commit").Where(command => command.Name.StartsWith("SelectNext", StringComparison.Ordinal)
            || command.Name.StartsWith("SelectPrevious", StringComparison.Ordinal))) { bindings.Set(command.Context, command.Code, null); }
        Set(bindings, "Commit", name, Key.F6); Save(bindings);
        CommitDialog dialog = await f.CommitAsync(); await SelectFile(dialog, false, "a.txt");
        string ready = await f.MaterialAsync(); TreeView tree = Field<TreeView>(dialog, "Unstaged");
        Press(dialog, PhysicalKey.N, RawInputModifiers.Control); await Idle(dialog);
        Assert.That(SelectedPath(tree), Is.EqualTo("a.txt"), "An unassigned primary command must not survive through a legacy switch.");
        Assert.That(tree.IsKeyboardFocusWithin, Is.True, "The initial real file click must retain its focus after the selected diff read.");
        Press(dialog, PhysicalKey.F6); await Idle(dialog);
        Assert.That(SelectedPath(tree), Is.EqualTo(backwards ? "z.txt" : "file.txt"));
        Assert.That(Field<DiffView>(dialog, "SelectedDiff").DocumentIdentity, Does.Contain(backwards ? "z.txt" : "file.txt"));
        Assert.That(tree.IsKeyboardFocusWithin, Is.True);
        Assert.That(await f.MaterialAsync(), Is.EqualTo(ready));
    }

    [AvaloniaTest]
    public async Task CommitAddsSelectedDiffTextAndOpensTheRealCreateBranchDialogThroughRemappedCommands()
    {
        await using Scope f = await Scope.CreateAsync(); await f.SeedChangesAsync();
        HotkeyBindings bindings = new(); Set(bindings, "Commit", "AddSelectionToCommitMessage", Key.F6); Set(bindings, "Commit", "CreateBranch", Key.F7); Save(bindings);
        CommitDialog dialog = await f.CommitAsync(); DiffView diff = Field<DiffView>(dialog, "SelectedDiff");
        await SelectFile(dialog, false, "file.txt"); PushProcessTestDriver.Pointer(diff.Editor.TextArea);
        diff.Select(diff.Text.IndexOf(SelectedLine, StringComparison.Ordinal), SelectedLine.Length);
        Press(dialog, PhysicalKey.F6);
        Assert.That(Field<TextBox>(dialog, "Message").Text, Is.EqualTo(SelectedLine + "\n"));
        string ready = await f.MaterialAsync(); Press(dialog, PhysicalKey.F7);
        await Until(() => dialog.OwnedWindows.OfType<CreateBranchDialog>().Any());
        CreateBranchDialog child = dialog.OwnedWindows.OfType<CreateBranchDialog>().Single(); await Idle(child);
        Assert.That(Field<TextBox>(child, "CreateBranchRevision").Text, Is.EqualTo("HEAD"));
        Press(child, PhysicalKey.Escape, releaseOn: dialog); await Until(() => !child.IsVisible); await Idle(dialog);
        Assert.That(await f.MaterialAsync(), Is.EqualTo(ready)); Assert.That(dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    public async Task CommitFileViewerStageAndUnstageUseTheirConfiguredKeysAndKeepWorkingBytes()
    {
        await using Scope f = await Scope.CreateAsync(); await f.SeedChangesAsync();
        HotkeyBindings bindings = new(); Set(bindings, "FileViewer", "StageLines", Key.F6); Set(bindings, "FileViewer", "UnstageLines", Key.F7); Save(bindings);
        CommitDialog dialog = await f.CommitAsync(); await SelectFile(dialog, false, "file.txt");
        DiffView diff = Field<DiffView>(dialog, "SelectedDiff"); SelectDiffLine(diff); Press(dialog, PhysicalKey.F6); await Idle(dialog);
        Assert.That(await f.IndexAsync("file.txt"), Does.Contain("selected addition").And.Not.Contain("remaining addition"));
        Assert.That(await f.IndexAsync("a.txt"), Is.EqualTo(Initial));
        Assert.That(await File.ReadAllTextAsync(f.Git.File), Is.EqualTo(Changed));
        await SelectFile(dialog, true, "file.txt"); SelectDiffLine(diff); Press(dialog, PhysicalKey.F7); await Idle(dialog);
        Assert.That(await f.IndexAsync("file.txt"), Is.EqualTo(Initial)); Assert.That(await File.ReadAllTextAsync(f.Git.File), Is.EqualTo(Changed));
        Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ExplicitEscapeBindingsRunBeforeTheNativeCommitCancelFallback(bool fileViewer)
    {
        await using Scope f = await Scope.CreateAsync(); await f.SeedChangesAsync(); HotkeyBindings bindings = new();
        string context = fileViewer ? "FileViewer" : "Commit", command = fileViewer ? "StageLines" : "FocusUnstagedFiles";
        Set(bindings, context, command, Key.Escape); Save(bindings);
        CommitDialog dialog = await f.CommitAsync();
        if (fileViewer) { await SelectFile(dialog, false, "file.txt"); SelectDiffLine(Field<DiffView>(dialog, "SelectedDiff")); }
        else { PushProcessTestDriver.Pointer(Field<TextBox>(dialog, "Message")); }
        Press(dialog, PhysicalKey.Escape); await Idle(dialog);
        Assert.That(dialog.IsVisible, Is.True, "An explicitly assigned command must be resolved before the native Escape cancel path.");
        if (fileViewer) { Assert.That(await f.IndexAsync("file.txt"), Does.Contain("selected addition").And.Not.Contain("remaining addition")); }
        else { Assert.That(Field<TreeView>(dialog, "Unstaged").IsKeyboardFocusWithin, Is.True); }
        bindings.Set(context, HotkeyCatalog.Find(context, command)!.Code, null); Save(bindings);
        if (fileViewer) { PushProcessTestDriver.Pointer(Field<TextBox>(dialog, "Message")); }
        Press(dialog, PhysicalKey.Escape); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task CommitResetLinesRequiresConfirmationAndUsesTheActualWorkingOrIndexAndWorkingPair(bool staged)
    {
        await using Scope f = await Scope.CreateAsync(); await f.SeedChangesAsync();
        if (staged) { await f.Git.Repository.StageAsync(["file.txt"]); }
        HotkeyBindings bindings = new(); Set(bindings, "FileViewer", "ResetLines", Key.F6); Save(bindings);
        CommitDialog dialog = await f.CommitAsync(); await SelectFile(dialog, staged, "file.txt");
        DiffView diff = Field<DiffView>(dialog, "SelectedDiff"); SelectDiffLine(diff); string ready = await f.MaterialAsync();
        Press(dialog, PhysicalKey.F6); Window question = await Question(dialog);
        Press(question, PhysicalKey.Escape, releaseOn: dialog); await Until(() => !question.IsVisible); await Idle(dialog);
        Assert.That(await f.MaterialAsync(), Is.EqualTo(ready), "Cancelling the real confirmation must not apply a patch.");
        SelectDiffLine(diff); Press(dialog, PhysicalKey.F6); question = await Question(dialog);
        PushProcessTestDriver.Pointer(question.GetVisualDescendants().OfType<Button>().Single(button => !button.IsCancel));
        await Until(() => !question.IsVisible); await Idle(dialog);
        const string remaining = "root\nremaining addition\nend\n";
        Assert.That(await File.ReadAllTextAsync(f.Git.File), Is.EqualTo(remaining));
        Assert.That(await f.IndexAsync("file.txt"), Is.EqualTo(staged ? remaining : Initial));
        Assert.That(await f.IndexAsync("a.txt"), Is.EqualTo(Initial));
        Assert.That(await File.ReadAllTextAsync(f.Git.PathInRepository("a.txt")), Is.EqualTo(Changed));
        Assert.That(dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    public async Task CommitUnassignedCommandsStayDisabledAndLiveSavedFocusBindingsUpdateAnOpenDialog()
    {
        await using Scope f = await Scope.CreateAsync(); await f.SeedChangesAsync(); HotkeyBindings bindings = new();
        bindings.Set("Commit", HotkeyCatalog.Find("Commit", "StageAll")!.Code, null); Save(bindings);
        CommitDialog dialog = await f.CommitAsync(); await SelectFile(dialog, false, "file.txt");
        Press(dialog, PhysicalKey.S, RawInputModifiers.Control); await Idle(dialog);
        Assert.That((await f.Git.GitAsync("diff", "--cached")).StandardOutput, Is.Empty);
        Set(bindings, "Commit", "FocusCommitMessage", Key.F6); Save(bindings); Pump(dialog);
        Press(dialog, PhysicalKey.F6); Assert.That(Field<TextBox>(dialog, "Message").IsFocused, Is.True);
        Assert.That(ToolTip.GetTip(Field<Button>(dialog, "StageAll")), Is.EqualTo("Stage all"));
    }

    [AvaloniaTest]
    public async Task CommitRefreshAndConventionalMenusUseTheirRemappedKeysWithoutChangingGit()
    {
        await using Scope f = await Scope.CreateAsync(); await f.SeedChangesAsync();
        HotkeyBindings bindings = new(); Set(bindings, "Commit", "Refresh", Key.F6);
        Set(bindings, "Commit", "ConventionalCommit_PrefixMessage", Key.F7); Set(bindings, "Commit", "ConventionalCommit_PrefixMessageWithScope", Key.F8); Save(bindings);
        CommitDialog dialog = await f.CommitAsync();
        await File.WriteAllTextAsync(f.Git.PathInRepository("new-after-open.txt"), "new literal content\n");
        string ready = await f.MaterialAsync(); Press(dialog, PhysicalKey.F5); await Idle(dialog);
        Assert.That(FileStatusTestHelpers.Changes(Field<TreeView>(dialog, "Unstaged")).Select(change => change.Path), Does.Not.Contain("new-after-open.txt"));
        Press(dialog, PhysicalKey.F6); await Idle(dialog);
        Assert.That(FileStatusTestHelpers.Changes(Field<TreeView>(dialog, "Unstaged")).Select(change => change.Path), Does.Contain("new-after-open.txt"));
        TextBox message = Field<TextBox>(dialog, "Message"); PushProcessTestDriver.Pointer(message); dialog.KeyTextInput("keep exact message");
        MenuItem templates = Field<Menu>(dialog, "CommitToolbar").Items.OfType<MenuItem>().Single(item => item.Name == "commitTemplatesToolStripMenuItem");
        foreach (PhysicalKey key in new[] { PhysicalKey.F7, PhysicalKey.F8 })
        {
            Press(dialog, key);
            MenuItem conventional = templates.Items.OfType<MenuItem>().Single(item => item.Name == "ConventionalCommits");
            Assert.That(templates.IsSubMenuOpen && conventional.IsSubMenuOpen, Is.True);
            Assert.That(conventional.Items.OfType<MenuItem>().Single(item => item.Name == "Conventional_feat").IsSelected, Is.True);
            Assert.That(message.Text, Is.EqualTo("keep exact message"));
            Press(dialog, PhysicalKey.Escape); Press(dialog, PhysicalKey.Escape);
            Assert.That(dialog.IsVisible, Is.True);
        }
        Assert.That(message.Text, Is.EqualTo("keep exact message")); Assert.That(dialog.OwnedWindows, Is.Empty);
        Assert.That(await f.MaterialAsync(), Is.EqualTo(ready));
    }

    [AvaloniaTest]
    public async Task CommitDiffToolKeyRunsTheExistingTypedWorkingComparisonAndLeavesRepositoryMaterialUntouched()
    {
        await using Scope f = await Scope.CreateAsync(); await f.SeedChangesAsync();
        string output = f.Git.Output("difftool-output");
        await f.Git.GitAsync("config", "diff.tool", "hotkeyfixture"); await f.Git.GitAsync("config", "diff.guitool", "hotkeyfixture");
        await f.Git.GitAsync("config", "difftool.hotkeyfixture.cmd", "cat \"$LOCAL\" > " + Quote(output) + "; printf '\\n---\\n' >> " + Quote(output) + "; cat \"$REMOTE\" >> " + Quote(output));
        HotkeyBindings bindings = new(); Set(bindings, "Commit", "OpenWithDifftool", Key.F6); Save(bindings);
        CommitDialog dialog = await f.CommitAsync(); await SelectFile(dialog, false, "file.txt"); string ready = await f.MaterialAsync();
        Press(dialog, PhysicalKey.F3); await Idle(dialog);
        Assert.That(File.Exists(output), Is.False, "The removed Commit binding is tested from its file list, outside the independent FileViewer F3 context.");
        Press(dialog, PhysicalKey.F6); await Idle(dialog);
        Assert.That(File.Exists(output), Is.True); Assert.That(await File.ReadAllTextAsync(output), Is.EqualTo(Initial + "\n---\n" + Changed));
        Assert.That(await f.MaterialAsync(), Is.EqualTo(ready)); Assert.That(dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    public async Task StashNextMeansPreviousIndexAndItsRemappedRefreshHasNoLegacyFallback()
    {
        await using Scope f = await Scope.CreateAsync();
        await File.WriteAllTextAsync(f.Git.File, "old stash\n"); await f.Git.Repository.StashAsync("old stash");
        await File.WriteAllTextAsync(f.Git.File, "new stash\n"); await f.Git.Repository.StashAsync("new stash");
        HotkeyBindings bindings = new(); Set(bindings, "Stash", "NextStash", Key.F6); Set(bindings, "Stash", "PreviousStash", Key.F7); Set(bindings, "Stash", "Refresh", Key.F8); Save(bindings);
        StashDialog dialog = f.Own(new StashDialog(f.Git.Repository)); dialog.Show(); await dialog.InitializeAsync(); await Idle(dialog);
        ComboBox selector = Field<ComboBox>(dialog, "StashSelection"); Assert.That(selector.SelectedIndex, Is.EqualTo(1));
        string ready = await f.MaterialAsync(); Press(dialog, PhysicalKey.F7); await Idle(dialog); Assert.That(selector.SelectedIndex, Is.EqualTo(2));
        Press(dialog, PhysicalKey.N, RawInputModifiers.Control); await Idle(dialog); Assert.That(selector.SelectedIndex, Is.EqualTo(2));
        Press(dialog, PhysicalKey.F6); await Idle(dialog); Assert.That(selector.SelectedIndex, Is.EqualTo(1));
        Press(dialog, PhysicalKey.F6); await Idle(dialog); Assert.That(selector.SelectedIndex, Is.Zero);
        Assert.That(await f.MaterialAsync(), Is.EqualTo(ready));
        await File.WriteAllTextAsync(f.Git.File, "third stash\n"); await f.Git.Repository.StashAsync("third stash");
        int count = selector.ItemCount; Press(dialog, PhysicalKey.F5); await Idle(dialog); Assert.That(selector.ItemCount, Is.EqualTo(count));
        Press(dialog, PhysicalKey.F8); await Idle(dialog); Assert.That(selector.ItemCount, Is.EqualTo(count + 1));
        Assert.That(ToolTip.GetTip(selector), Does.Contain("F6")); Assert.That(dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task StashConfiguredRefreshRunsBeforeNativeResetOrEscapeSelectionFallback(bool escape)
    {
        await using Scope f = await Scope.CreateAsync();
        await File.WriteAllTextAsync(f.Git.File, "first stash\n"); await f.Git.Repository.StashAsync("first stash");
        HotkeyBindings bindings = new(); Set(bindings, "Stash", "Refresh", escape ? Key.Escape : Key.R); Save(bindings);
        StashDialog dialog = f.Own(new StashDialog(f.Git.Repository)); dialog.Show(); await dialog.InitializeAsync(); await Idle(dialog);
        ComboBox selector = Field<ComboBox>(dialog, "StashSelection"); int count = selector.ItemCount;
        if (escape)
        {
            TextBox message = Field<TextBox>(dialog, "StashMessage"); PushProcessTestDriver.Pointer(message);
            Press(dialog, PhysicalKey.A, RawInputModifiers.Control);
            Assert.That(message.SelectionStart, Is.Not.EqualTo(message.SelectionEnd), "The native Escape deselection path must really be eligible.");
        }
        else
        {
            TreeView tree = Field<TreeView>(dialog, "StashFiles");
            TreeViewItem row = FileStatusTestHelpers.Rows(tree).Single(item => item.Items.Count == 0 && item.Tag is not null and not FileStatusListNodeTag);
            row.BringIntoView(); Pump(dialog);
            Control header = row.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header"
                && ReferenceEquals(control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), row));
            PushProcessTestDriver.Pointer(header); await Idle(dialog);
            Assert.That(tree.IsKeyboardFocusWithin, Is.True); Assert.That(selector.SelectedIndex, Is.GreaterThan(0));
        }
        await File.WriteAllTextAsync(f.Git.File, "second stash\n"); await f.Git.Repository.StashAsync("second stash");
        string ready = await f.MaterialAsync();
        Press(dialog, escape ? PhysicalKey.Escape : PhysicalKey.R); await Idle(dialog);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.OwnedWindows.Any(window => window.IsVisible), Is.False,
            "A configured Refresh must not open Reset or let native Escape close the window.");
        Assert.That(selector.ItemCount, Is.EqualTo(count + 1), "The configured command must perform a real stash refresh.");
        Assert.That(await f.MaterialAsync(), Is.EqualTo(ready)); Assert.That(dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    public async Task AFocusedFileViewerBindingWinsACommitBindingButTheMessageStillUsesTheCommitCommand()
    {
        await using Scope f = await Scope.CreateAsync(); await f.SeedChangesAsync(); HotkeyBindings bindings = new();
        Set(bindings, "FileViewer", "Find", Key.F6); Set(bindings, "Commit", "FocusUnstagedFiles", Key.F6); Save(bindings);
        CommitDialog dialog = await f.CommitAsync(); await SelectFile(dialog, false, "file.txt");
        DiffView diff = Field<DiffView>(dialog, "SelectedDiff"); PushProcessTestDriver.Pointer(diff.Editor.TextArea);
        string ready = await f.MaterialAsync(); Press(dialog, PhysicalKey.F6);
        await Until(() => dialog.OwnedWindows.Any(window => window.IsVisible && window.Name == "DiffViewerFindDialog"));
        Window find = dialog.OwnedWindows.Single(window => window.IsVisible && window.Name == "DiffViewerFindDialog");
        Assert.That(Field<TextBox>(find, "DiffViewerFindQuery").IsKeyboardFocusWithin, Is.True);
        Press(find, PhysicalKey.Escape, releaseOn: dialog); await Until(() => !find.IsVisible);
        PushProcessTestDriver.Pointer(Field<TextBox>(dialog, "Message")); Press(dialog, PhysicalKey.F6);
        Assert.That(Field<TreeView>(dialog, "Unstaged").IsKeyboardFocusWithin, Is.True);
        Assert.That(dialog.OwnedWindows.Any(window => window.IsVisible), Is.False); Assert.That(await f.MaterialAsync(), Is.EqualTo(ready));
    }

    [AvaloniaTest]
    public async Task AFocusedStashFileViewerBindingWinsItsFormRefreshBinding()
    {
        await using Scope f = await Scope.CreateAsync();
        await File.WriteAllTextAsync(f.Git.File, "first stash\n"); await f.Git.Repository.StashAsync("first stash");
        HotkeyBindings bindings = new(); Set(bindings, "FileViewer", "Find", Key.F6); Set(bindings, "Stash", "Refresh", Key.F6); Save(bindings);
        StashDialog dialog = f.Own(new StashDialog(f.Git.Repository)); dialog.Show(); await dialog.InitializeAsync(); await Idle(dialog);
        DiffView diff = Field<FileContentView>(dialog, "StashDiff").TextView; PushProcessTestDriver.Pointer(diff.Editor.TextArea);
        ComboBox selector = Field<ComboBox>(dialog, "StashSelection"); int count = selector.ItemCount;
        await File.WriteAllTextAsync(f.Git.File, "second stash\n"); await f.Git.Repository.StashAsync("second stash");
        string ready = await f.MaterialAsync(); Press(dialog, PhysicalKey.F6);
        await Until(() => dialog.OwnedWindows.Any(window => window.IsVisible && window.Name == "DiffViewerFindDialog"));
        Window find = dialog.OwnedWindows.Single(window => window.IsVisible && window.Name == "DiffViewerFindDialog");
        Assert.That(Field<TextBox>(find, "DiffViewerFindQuery").IsKeyboardFocusWithin, Is.True);
        Assert.That(selector.ItemCount, Is.EqualTo(count), "The focused viewer's Find must not invoke the form's Refresh.");
        Press(find, PhysicalKey.Escape, releaseOn: dialog); await Until(() => !find.IsVisible);
        PushProcessTestDriver.Pointer(Field<TextBox>(dialog, "StashMessage")); Press(dialog, PhysicalKey.F6); await Idle(dialog);
        Assert.That(selector.ItemCount, Is.EqualTo(count + 1)); Assert.That(dialog.OwnedWindows.Any(window => window.IsVisible), Is.False);
        Assert.That(await f.MaterialAsync(), Is.EqualTo(ready));
    }

    [AvaloniaTest]
    [TestCase("ChooseBase", "base\n")]
    [TestCase("ChooseLocal", "local\n")]
    [TestCase("ChooseRemote", "remote\n")]
    public async Task ConflictChoiceKeysUseTheRealConfirmationAndOnlyResolveTheSelectedPath(string command, string expected)
    {
        await using Scope f = await Scope.CreateAsync(); await f.CreateConflictAsync();
        HotkeyBindings bindings = new(); Set(bindings, "FormMergeConflicts", command, Key.F6); Save(bindings);
        ResolveConflictsDialog dialog = f.Own(new ResolveConflictsDialog(f.Git.Repository)); dialog.Show(); await dialog.InitializeAsync(); await Idle(dialog);
        PushProcessTestDriver.Pointer(Field<ListBox>(dialog, "ConflictFiles"));
        string ready = await f.MaterialAsync(); string head = await f.Git.Repository.ResolveRevisionAsync("HEAD");
        Press(dialog, PhysicalKey.F6); Window question = await Question(dialog);
        Press(question, PhysicalKey.Escape, releaseOn: dialog); await Until(() => !question.IsVisible); await Idle(dialog);
        Assert.That(await f.MaterialAsync(), Is.EqualTo(ready)); Assert.That(dialog.Conflicts.Select(path => path.Path), Is.EqualTo(new[] { "file.txt" }));
        Press(dialog, PhysicalKey.F6); question = await Question(dialog);
        PushProcessTestDriver.Pointer(question.GetVisualDescendants().OfType<Button>().Single(button => !button.IsCancel));
        await Until(() => !question.IsVisible); await Idle(dialog);
        Assert.That(dialog.Conflicts, Is.Empty); Assert.That(await File.ReadAllTextAsync(f.Git.File), Is.EqualTo(expected));
        Assert.That(await f.IndexAsync("file.txt"), Is.EqualTo(expected));
        Assert.That(await File.ReadAllTextAsync(f.Git.PathInRepository("keep.txt")), Is.EqualTo("keep\n"));
        Assert.That(await f.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    public async Task ConflictRefreshUsesItsConfiguredKeyAndUpdatesTheActualIndexWithoutRunningResolution()
    {
        await using Scope f = await Scope.CreateAsync(); await f.CreateConflictAsync();
        HotkeyBindings bindings = new(); Set(bindings, "FormMergeConflicts", "Rescan", Key.F6); Save(bindings);
        ResolveConflictsDialog dialog = f.Own(new ResolveConflictsDialog(f.Git.Repository)); dialog.Show(); await dialog.InitializeAsync(); await Idle(dialog);
        await File.WriteAllTextAsync(f.Git.File, "external resolution\n"); await f.Git.Repository.StageAsync(["file.txt"]);
        string ready = await f.MaterialAsync(); Press(dialog, PhysicalKey.F5); await Idle(dialog);
        Assert.That(dialog.Conflicts, Has.Count.EqualTo(1), "The old F5 binding must not rescan after it was changed.");
        Press(dialog, PhysicalKey.F6); await Idle(dialog); Assert.That(dialog.Conflicts, Is.Empty);
        Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(await f.MaterialAsync(), Is.EqualTo(ready));
        Assert.That(ToolTip.GetTip(Field<Button>(dialog, "RefreshConflicts")), Does.Contain("F6"));
    }

    [AvaloniaTest]
    public async Task ConflictMergeKeyCallsTheConfiguredRealMergeToolForTheSelectedPath()
    {
        await using Scope f = await Scope.CreateAsync(); await f.CreateConflictAsync();
        string marker = f.Git.Output("mergetool-invoked");
        await f.Git.GitAsync("config", "merge.tool", "hotkeyfixture");
        await f.Git.GitAsync("config", "mergetool.hotkeyfixture.cmd", "cp \"$LOCAL\" \"$MERGED\" && printf invoked > " + Quote(marker));
        await f.Git.GitAsync("config", "mergetool.hotkeyfixture.trustExitCode", "true"); await f.Git.GitAsync("config", "mergetool.keepBackup", "false");
        HotkeyBindings bindings = new(); Set(bindings, "FormMergeConflicts", "Merge", Key.F6); Save(bindings);
        ResolveConflictsDialog dialog = f.Own(new ResolveConflictsDialog(f.Git.Repository)); dialog.Show(); await dialog.InitializeAsync(); await Idle(dialog);
        PushProcessTestDriver.Pointer(Field<ListBox>(dialog, "ConflictFiles"));
        Press(dialog, PhysicalKey.M); await Idle(dialog);
        Assert.That(File.Exists(marker), Is.False); Assert.That(dialog.Conflicts, Has.Count.EqualTo(1));
        Press(dialog, PhysicalKey.F6); await Idle(dialog);
        Assert.That(await File.ReadAllTextAsync(marker), Is.EqualTo("invoked")); Assert.That(dialog.Conflicts, Is.Empty);
        Assert.That(await f.IndexAsync("file.txt"), Is.EqualTo("local\n"));
        Assert.That(await File.ReadAllTextAsync(f.Git.PathInRepository("keep.txt")), Is.EqualTo("keep\n"));
        Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(dialog.LastError, Is.Null);
    }

    private sealed class Scope(GitFixture git) : IAsyncDisposable
    {
        private readonly List<Window> _windows = [];
        internal GitFixture Git => git;
        internal static async Task<Scope> CreateAsync()
        {
            GitFixture fixture = await GitFixture.CreateAsync(); fixture.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
            return new(fixture);
        }
        internal T Own<T>(T window) where T : Window { _windows.Add(window); return window; }
        internal async Task SeedChangesAsync()
        {
            foreach (string path in new[] { "a.txt", "file.txt", "z.txt" }) { await File.WriteAllTextAsync(git.PathInRepository(path), Initial); }
            await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Multiple files");
            foreach (string path in new[] { "a.txt", "file.txt", "z.txt" }) { await File.WriteAllTextAsync(git.PathInRepository(path), Changed); }
        }
        internal async Task<CommitDialog> CommitAsync()
        {
            CommitDialog dialog = Own(new CommitDialog(git.Repository)); dialog.Show(); await dialog.InitializeAsync(); await Idle(dialog);
            return dialog;
        }
        internal async Task CreateConflictAsync()
        {
            await File.WriteAllTextAsync(git.File, "base\n"); await File.WriteAllTextAsync(git.PathInRepository("keep.txt"), "keep\n");
            await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Conflict base");
            await git.GitAsync("branch", "other");
            await File.WriteAllTextAsync(git.File, "local\n"); await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Local");
            await git.GitAsync("checkout", "other"); await File.WriteAllTextAsync(git.File, "remote\n"); await git.Repository.StageAllAsync(); await git.Repository.CommitAsync("Remote");
            await git.GitAsync("checkout", "main");
            GitResult merge = await new GitRunner().RunAsync(git.Repository.Path, ["merge", "--no-edit", "other"]);
            Assert.That(merge.ExitCode, Is.EqualTo(1)); Assert.That((await git.Repository.GetConflictsAsync()).Select(item => item.Path), Is.EqualTo(new[] { "file.txt" }));
        }
        internal async Task<string> IndexAsync(string path) => System.Text.Encoding.UTF8.GetString(await git.Repository.GetIndexFileBytesAsync(path));
        internal async Task<string> MaterialAsync()
        {
            string head = await git.Repository.ResolveRevisionAsync("HEAD"); string refs = (await git.GitAsync("show-ref")).StandardOutput;
            string index = Convert.ToHexString(await File.ReadAllBytesAsync(git.PathInRepository(".git/index")));
            string files = string.Join('\n', Directory.EnumerateFiles(git.Repository.Path, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(git.Repository.Path, path).StartsWith(".git/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
                .Select(path => Path.GetRelativePath(git.Repository.Path, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
            return head + "\n" + refs + "\n" + index + "\n" + files;
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window window in _windows.AsEnumerable().Reverse())
                {
                    foreach (Window child in window.OwnedWindows.ToArray()) { child.Close(); }
                    window.Close(); await Until(() => !window.IsVisible && !Busy(window));
                }
            }
            finally { git.Dispose(); }
        }
    }

    private static void Set(HotkeyBindings bindings, string context, string name, Key key)
        => bindings.Set(context, HotkeyCatalog.Find(context, name)!.Code, new(key));
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static void Save(HotkeyBindings bindings) { new HotkeyPreferencesStore().Save(bindings); Dispatcher.UIThread.RunJobs(); }
    private static async Task SelectFile(CommitDialog dialog, bool staged, string path)
    {
        TreeView tree = Field<TreeView>(dialog, staged ? "Staged" : "Unstaged");
        TreeViewItem item = FileStatusTestHelpers.Rows(tree).Single(item => item.Tag is GitChange change && change.Path == path);
        item.BringIntoView(); Pump(dialog);
        Control header = item.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header"
            && ReferenceEquals(control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), item));
        PushProcessTestDriver.Pointer(header); await Idle(dialog);
    }
    private static string? SelectedPath(TreeView tree) => (tree.SelectedItem as TreeViewItem)?.Tag is GitChange change ? change.Path : null;
    private static void SelectDiffLine(DiffView diff)
    {
        PushProcessTestDriver.Pointer(diff.Editor.TextArea);
        int start = diff.Text.IndexOf(SelectedLine, StringComparison.Ordinal); Assert.That(start, Is.GreaterThanOrEqualTo(0));
        diff.Select(start, SelectedLine.Length);
    }
    private static async Task<Window> Question(Window owner)
    { await Until(() => owner.OwnedWindows.Any(window => window.IsVisible)); Window question = owner.OwnedWindows.Single(window => window.IsVisible); Pump(question); return question; }
    private static bool Busy(Window window) => window is CommitDialog commit ? commit.IsBusy : window is RepositoryDialog repository && repository.IsBusy;
    private static async Task Idle(Window window)
    {
        await Until(() => !Busy(window)); Pump(window);
        if (window is CommitDialog commit) { Assert.That(commit.LastError, Is.Null, commit.LastError); }
        else if (window is RepositoryDialog repository) { Assert.That(repository.LastError, Is.Null, repository.LastError); }
    }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs(); Assert.That(condition(), Is.True, "The real host command, modal or close did not settle.");
    }
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None, Window? releaseOn = null)
    { window.KeyPressQwerty(key, modifiers); Window release = releaseOn ?? window; if (release.IsVisible) { release.KeyReleaseQwerty(key, modifiers); } Dispatcher.UIThread.RunJobs(); }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}

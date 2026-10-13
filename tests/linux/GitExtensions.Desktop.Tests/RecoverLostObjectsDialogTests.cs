using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class RecoverLostObjectsDialogTests
{
    [AvaloniaTest] public Task OriginalGridFiltersAndActionablePreviewRenderInLight() => OriginalGridFiltersAndActionablePreview(false);
    [AvaloniaTest] public Task OriginalGridFiltersAndActionablePreviewRenderInDark() => OriginalGridFiltersAndActionablePreview(true);
    private async Task OriginalGridFiltersAndActionablePreview(bool dark)
    {
        await using Fixture f = await Fixture.Create(); RecoverLostObjectsDialog dialog = await f.Show(dark); string before = await f.Witness();
        Assert.Multiple(() =>
        {
            Assert.That(dialog.Title, Is.EqualTo("Verify database")); Assert.That(dialog.Width, Is.EqualTo(859)); Assert.That(dialog.Height, Is.EqualTo(575));
            Assert.That(dialog.ReadOptions(), Is.EqualTo(new GitObjectRecoveryOptions(false, false, true)));
            Assert.That(Field<CheckBox>(dialog, "RecoveryShowCommits").IsChecked, Is.True); Assert.That(Field<CheckBox>(dialog, "RecoveryShowOther").IsChecked, Is.False);
            Assert.That(dialog.VisibleObjects.All(item => item.Type is GitLostObjectType.Commit or GitLostObjectType.Tag), Is.True);
            Assert.That(dialog.VisibleObjects.Select(item => item.Date), Is.Ordered.Descending);
            Assert.That(dialog.LastScan!.Objects.Select(item => item.Hash), Does.Contain(f.Blob));
            Assert.That(dialog.LastScan.Objects.Select(item => item.Hash), Does.Contain(f.Binary));
        });
        GitLostObject commit = dialog.VisibleObjects.Single(item => item.Hash == f.Lost);
        Assert.That(commit.Subject, Is.EqualTo("Café perdu €")); Assert.That(commit.Author, Is.EqualTo("Élodie λ")); Assert.That(commit.Parent, Is.EqualTo(f.Initial));
        Assert.That(commit.Date!.Value.ToUnixTimeSeconds(), Is.EqualTo(1577836800), "Date is committer time, not the deliberately different author time.");
        await Select(dialog, f.Lost); FileHistoryContentView viewer = Field<FileHistoryContentView>(dialog, "RecoveryPreview");
        Assert.That(dialog.Preview!.DiffDocument, Is.Not.Null); Assert.That(viewer.TextView.CanonicalText, Does.Contain("Café perdu €").And.Contain("Après €"));
        Assert.That(Field<ScrollViewer>(dialog, "RecoveryPreviewDiagnostics").IsVisible, Is.False, "An invisible error must not leave an input-blocking overlay over the viewer.");
        Rect instructions = Bounds(dialog, Field<Grid>(dialog, "RecoveryInstructions")), header = Bounds(dialog, Field<Grid>(dialog, "RecoveryHeader")), list = Bounds(dialog, Field<ListBox>(dialog, "RecoveryObjects")), actions = Bounds(dialog, Field<Grid>(dialog, "RecoveryActions"));
        Assert.That(instructions.Bottom, Is.LessThanOrEqualTo(header.Top)); Assert.That(header.Bottom, Is.LessThanOrEqualTo(list.Top + 1)); Assert.That(list.Bottom, Is.LessThanOrEqualTo(actions.Top));
        Assert.That(Bounds(dialog, Field<CheckBox>(dialog, "RecoveryCheckAll")).Right, Is.LessThanOrEqualTo(Bounds(dialog, Field<Button>(dialog, "RecoveryHeaderDate")).Left));
        Assert.That(Field<GridSplitter>(dialog, "RecoverySplitter").Bounds.Width, Is.EqualTo(4));
        Assert.That(Bounds(dialog, Field<GridSplitter>(dialog, "RecoverySplitter")).Left, Is.GreaterThanOrEqualTo(Bounds(dialog, Field<Border>(dialog, "RecoveryGrid")).Right));
        Assert.That(Bounds(dialog, Field<GridSplitter>(dialog, "RecoverySplitter")).Right, Is.LessThanOrEqualTo(Bounds(dialog, viewer).Left));
        Assert.That(viewer.Bounds.Width, Is.GreaterThan(100));
        await Pointer(Field<Button>(dialog, "RecoveryHeaderDate"), .94); Assert.That(dialog.VisibleObjects.Select(item => item.Date), Is.Ordered, "The whole column header remains a sort hit target beyond its caption.");
        await Pointer(Field<Button>(dialog, "RecoveryHeaderDate"), .94); Assert.That(dialog.VisibleObjects.Select(item => item.Date), Is.Ordered.Descending);
        await Pointer(Field<CheckBox>(dialog, "RecoveryShowOther")); await Until(() => dialog.VisibleObjects.Any(item => item.Hash == f.Blob)); Pump(dialog);
        await Select(dialog, f.Blob);
        await Until(() => Row(dialog).GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("seemingly: txt", StringComparison.Ordinal) is true));
        Assert.That(viewer.TextView.Text, Is.EqualTo("texte à sauver €\nseconde ligne\n"));
        Assert.That(dialog.Preview!.Object!.Bytes, Is.EqualTo(f.BlobBytes)); Assert.That(viewer.IsBinary, Is.False);
        TextEditor editor = viewer.GetVisualDescendants().OfType<TextEditor>().Single(); Assert.That(editor.IsReadOnly, Is.True);
        Point center = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, Math.Min(50, editor.Bounds.Height / 2)), dialog)!.Value; dialog.MouseMove(center); Pump(dialog);
        await Until(() => viewer.Toolbar.IsVisible); Pump(dialog);
        // At the original narrow preview width the overflow is the real, visible toolbar entry.
        Button reading = viewer.Toolbar.Overflow.IsEffectivelyVisible ? viewer.Toolbar.Overflow : viewer.Toolbar.NonPrinting;
        await Pointer(reading); if (reading == viewer.Toolbar.Overflow) { await Until(() => viewer.Toolbar.OverflowFlyout.IsOpen); viewer.Toolbar.OverflowFlyout.Hide(); }
        CheckBox selectedCheck = Check(dialog); int doubleTaps = 0; selectedCheck.DoubleTapped += (_, _) => doubleTaps++;
        await Pointer(selectedCheck); await Pointer(selectedCheck); await Until(() => doubleTaps != 0);
        Assert.That(dialog.OwnedWindows.Any(child => child.Name == "RecoveryQuickView"), Is.False, "A double click on the checkbox must not open the row's quick view.");
        await Pointer(Check(dialog)); Assert.That(Check(dialog).IsChecked, Is.True); Assert.That(Field<CheckBox>(dialog, "RecoveryCheckAll").IsChecked, Is.Null);
        await Pointer(Field<CheckBox>(dialog, "RecoveryCheckAll")); Pump(dialog);
        Assert.That(Field<ListBox>(dialog, "RecoveryObjects").GetVisualDescendants().OfType<CheckBox>().Where(box => box.Name == "RecoveryObjectChecked").Select(box => box.IsChecked), Is.All.True);
        await Pointer(Field<CheckBox>(dialog, "RecoveryCheckAll")); Assert.That(Field<CheckBox>(dialog, "RecoveryCheckAll").IsChecked, Is.False);
        await Pointer(Field<CheckBox>(dialog, "RecoveryShowCommits")); Assert.That(dialog.VisibleObjects.Select(item => item.Type), Does.Not.Contain(GitLostObjectType.Commit));
        await Pointer(Field<CheckBox>(dialog, "RecoveryShowOther")); Assert.That(Field<CheckBox>(dialog, "RecoveryShowCommits").IsChecked, Is.True, "At least one type filter stays enabled.");
        await Until(() => dialog.SelectedObject is { } selected && dialog.Preview?.Hash == selected.Hash); Pump(dialog);
        Assert.That(dialog.Preview!.ReadError, Is.Null); Assert.That(viewer.TextView.CanonicalText, Is.Not.Empty);
        Assert.That(viewer.TextView.DocumentIdentity, Is.EqualTo(f.Repository.Path + "\nobject:" + dialog.SelectedObject!.Hash));
        Capture(dialog, "recover-original-" + (dark ? "dark" : "light") + ".png"); Assert.That(await f.Witness(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task SelectionPreviewViewClipboardAndMissingObjectNeverRetainAStaleDocument()
    {
        await using Fixture f = await Fixture.Create(); RecoverLostObjectsDialog dialog = await f.Show(); await ShowOther(dialog);
        string before = await f.Witness(); ListBox objects = Field<ListBox>(dialog, "RecoveryObjects");
        foreach (string hash in new[] { f.Lost, f.Tree, f.Binary, f.Blob, f.Tree, f.Lost, f.Blob }) { objects.SelectedIndex = Index(dialog, hash); }
        await Until(() => dialog.Preview?.Hash == f.Blob); Pump(dialog); FileHistoryContentView viewer = Field<FileHistoryContentView>(dialog, "RecoveryPreview");
        Assert.That(viewer.TextView.DocumentIdentity, Is.EqualTo(f.Repository.Path + "\nobject:" + f.Blob)); Assert.That(viewer.TextView.Text, Does.Contain("texte à sauver €"));
        await Select(dialog, f.Tree); Assert.That(viewer.TextView.CanonicalText, Does.Contain("tree λ.txt"));
        Assert.That(Context(dialog, "RecoveryCreateBranch").IsEnabled || Context(dialog, "RecoveryCreateTag").IsEnabled || Context(dialog, "RecoveryCopyParent").IsEnabled || Context(dialog, "RecoverySaveBlob").IsEnabled, Is.False);
        await Select(dialog, f.Binary); Assert.That(dialog.Preview!.Object!.Bytes, Is.EqualTo(f.BinaryBytes)); Assert.That(Context(dialog, "RecoverySaveBlob").IsEnabled, Is.True);
        await Select(dialog, f.Lost); IClipboard clipboard = dialog.Clipboard!; string? old = await ClipboardExtensions.TryGetTextAsync(clipboard);
        try
        {
            ContextMenu menu = objects.ContextMenu!; menu.Open(objects); Pump(dialog);
            Assert.That(menu.Items.OfType<MenuItem>().Select(item => ((Image)item.Icon!).Tag), Is.EqualTo(new[] { "ViewFile.png", "TagCreate.png", "BranchCreate.png", "CopyToClipboard.png", "CopyToClipboard.png", "SaveAs.png" }));
            await Pointer(Context(dialog, "RecoveryCopyHash")); await Until(() => !dialog.IsBusy && !menu.IsOpen); Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo(f.Lost));
            Invoke(Context(dialog, "RecoveryCopyParent")); await Until(() => !dialog.IsBusy); Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo(f.Initial));
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, old ?? ""); }
        Pump(dialog); Assert.That(objects.ContextMenu!.IsOpen, Is.False);
        Assert.That(objects.Focus(), Is.True, "The lost-object grid must accept keyboard focus after the Copy menu closes."); Pump(dialog);
        Assert.That(objects.IsKeyboardFocusWithin, Is.True, "Enter must originate in the grid after Copy, not in a popup or default button.");
        Press(dialog, PhysicalKey.Enter); Window quick = await OwnedDialog(dialog, "RecoveryQuickView");
        FileHistoryContentView quickViewer = Field<FileHistoryContentView>(quick, "RecoveryQuickViewer"); Assert.That(quickViewer.TextView.DocumentIdentity, Does.EndWith(f.Lost));
        Assert.That(quickViewer.GetVisualDescendants().OfType<TextEditor>().Single().IsReadOnly, Is.True); Assert.That(quickViewer.TextView.CanonicalText, Does.Contain("Café perdu €"));
        Press(quick, PhysicalKey.Escape, dialog); await Until(() => !dialog.IsBusy); Assert.That(await f.Witness(), Is.EqualTo(before));
        System.IO.File.Delete(f.ObjectPath(f.Blob)); await Select(dialog, f.Blob);
        Assert.That(dialog.Preview!.ReadError, Is.Not.Null); Assert.That(Field<ScrollViewer>(dialog, "RecoveryPreviewDiagnostics").IsVisible, Is.True);
        Assert.That(viewer.TextView.CanonicalText, Is.Empty); Assert.That(viewer.TextView.DocumentIdentity, Is.Null);
        Assert.That(Field<TextBlock>(dialog, "RecoveryPreviewError").Text, Does.Contain(f.Blob));
        await Select(dialog, f.Lost); Assert.That(Field<ScrollViewer>(dialog, "RecoveryPreviewDiagnostics").IsVisible, Is.False); Assert.That(viewer.TextView.CanonicalText, Does.Contain("Café perdu €"));
    }

    [AvaloniaTest]
    public async Task ScanOptionsRescanWhileTypeFiltersKeepTheOriginalExclusiveMinimum()
    {
        await using Fixture f = await Fixture.Create(); RecoverLostObjectsDialog dialog = await f.Show(); string before = await f.Witness();
        await Pointer(Field<CheckBox>(dialog, "RecoveryNoReflogs")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.ReadOptions().NoReflogs, Is.False); Assert.That(dialog.LastScan!.Objects.Any(item => item.Hash == f.Lost), Is.False, "The lost commit really is reachable solely through its reflog.");
        await Pointer(Field<CheckBox>(dialog, "RecoveryNoReflogs")); await Until(() => !dialog.IsBusy); Assert.That(dialog.LastScan!.Objects.Any(item => item.Hash == f.Lost), Is.True);
        await Pointer(Field<CheckBox>(dialog, "RecoveryUnreachable")); await Until(() => !dialog.IsBusy); Assert.That(dialog.ReadOptions().IncludeUnreachable, Is.True);
        Assert.That(dialog.LastScan!.Objects.Select(item => item.Hash), Does.Contain(f.TreeBlob), "Unreachable includes the blob referenced by a dangling tree.");
        await Pointer(Field<CheckBox>(dialog, "RecoveryFullCheck")); await Until(() => !dialog.IsBusy); Assert.That(dialog.ReadOptions().FullCheck, Is.True);
        await Pointer(Field<Expander>(dialog, "RecoveryReportExpander"));
        await Until(() => dialog.GetVisualDescendants().OfType<TextBox>().Any(control => control.Name == "RecoveryReport")); Pump(dialog);
        Assert.That(Field<Expander>(dialog, "RecoveryReportExpander").IsExpanded, Is.True);
        Assert.That(Field<TextBox>(dialog, "RecoveryReport").IsEffectivelyVisible, Is.True);
        Assert.That(Field<TextBox>(dialog, "RecoveryReport").Text, Is.EqualTo(dialog.LastScan!.StandardOutput + dialog.LastScan.StandardError)); Assert.That(await f.Witness(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task StandardTagAndBranchDialogsReceiveTheInitialLostShaButKeepExplicitUserChoices()
    {
        await using Fixture f = await Fixture.Create(); RecoverLostObjectsDialog dialog = await f.Show(); await Select(dialog, f.Lost); string before = await f.Witness();
        Invoke(Context(dialog, "RecoveryCreateTag")); Field<ListBox>(dialog, "RecoveryObjects").SelectedIndex = Index(dialog, f.Tag); CreateTagDialog tag = await Child<CreateTagDialog>(dialog);
        Assert.That(tag.Owner, Is.SameAs(dialog)); Assert.That(tag.ReadOptions().Revision, Is.EqualTo(f.Lost)); Assert.That(Field<TextBox>(tag, "CreateTagRevision").IsReadOnly, Is.False);
        Assert.That(Field<Button>(tag, "ChooseTagRevision").IsEnabled, Is.True); await Pointer(Field<Button>(tag, "CancelCreateTag")); await Until(() => !dialog.IsBusy); Assert.That(await f.Witness(), Is.EqualTo(before));
        await Select(dialog, f.Lost); Invoke(Context(dialog, "RecoveryCreateTag")); tag = await Child<CreateTagDialog>(dialog);
        Field<TextBox>(tag, "CreateTagName").Text = "explicit-target"; Field<TextBox>(tag, "CreateTagRevision").Text = f.Initial;
        await Pointer(Field<Button>(tag, "CreateTag")); await Until(() => !tag.IsVisible && !dialog.IsBusy); Assert.That(await f.Text("rev-parse", "refs/tags/explicit-target"), Is.EqualTo(f.Initial));
        await Select(dialog, f.Lost);
        ContextMenu popup = Field<ListBox>(dialog, "RecoveryObjects").ContextMenu!; popup.Open(Field<ListBox>(dialog, "RecoveryObjects")); Pump(dialog);
        MenuItem branchItem = Context(dialog, "RecoveryCreateBranch"); Assert.That(branchItem.Focus(), Is.True); Assert.That(branchItem.IsKeyboardFocusWithin, Is.True);
        TopLevel popupHost = TopLevel.GetTopLevel(branchItem)!;
        popupHost.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.Control); popupHost.KeyReleaseQwerty(PhysicalKey.B, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs();
        Field<ListBox>(dialog, "RecoveryObjects").SelectedIndex = Index(dialog, f.Tag); CreateBranchDialog branch = await Child<CreateBranchDialog>(dialog);
        Assert.That(popup.IsOpen, Is.False, "The shortcut is delivered through the real open context menu before its child opens.");
        Assert.That(branch.Owner, Is.SameAs(dialog)); Assert.That(branch.ReadOptions().Revision, Is.EqualTo(f.Lost)); Assert.That(branch.ReadOptions().Checkout, Is.True);
        Assert.That(Field<Button>(branch, "ChooseBranchRevision").IsEnabled, Is.True); Assert.That(Field<TextBox>(branch, "CreateBranchRevision").IsReadOnly, Is.False);
        Assert.That(Field<CheckBox>(branch, "CreateOrphanBranch").IsChecked, Is.False); Assert.That(Field<CheckBox>(branch, "CreateOrphanBranch").IsEnabled, Is.True);
        Field<TextBox>(branch, "CreateBranchName").Text = "recovered-commit"; await Pointer(Field<Button>(branch, "CreateBranch")); await Until(() => !branch.IsVisible && !dialog.IsBusy);
        Assert.That(await f.Head(), Is.EqualTo(f.Lost)); Assert.That((await f.Repository.GetStatusAsync()).Branch, Is.EqualTo("recovered-commit")); Assert.That(dialog.HasRepositoryChanges, Is.True);
        Assert.That(dialog.LastScan!.Objects.Any(item => item.Hash == f.Lost), Is.False, "The mutation is followed by a real rescan.");
    }

    [AvaloniaTest] public Task ARealLostCommitEnablesStandardBranchAndTagCreationWithoutHead() => Unborn(false);
    [AvaloniaTest] public Task ABareUnbornRepositoryRecoversARealCommitWithoutCheckout() => Unborn(true);
    private async Task Unborn(bool bare)
    {
        foreach (bool createBranch in new[] { false, true })
        {
            await using Fixture f = await Fixture.Create(); await f.MakeUnborn(bare); Assert.That((await f.Repository.GetStatusAsync()).IsUnborn, Is.True);
            RecoverLostObjectsDialog dialog = await f.Show(); await Select(dialog, f.Lost);
            if (createBranch)
            {
                Invoke(Context(dialog, "RecoveryCreateBranch")); CreateBranchDialog branch = await Child<CreateBranchDialog>(dialog);
                Assert.That(branch.ReadOptions().Revision, Is.EqualTo(f.Lost)); Assert.That(branch.ReadOptions().Orphan, Is.False, "A real target never triggers the automatic orphan fallback.");
                Assert.That(branch.ReadOptions().Checkout, Is.EqualTo(!bare)); Assert.That(Field<CheckBox>(branch, "CreateOrphanBranch").IsEnabled, Is.EqualTo(!bare));
                Assert.That(Field<Button>(branch, "ChooseBranchRevision").IsEnabled, Is.True); Field<TextBox>(branch, "CreateBranchName").Text = "found";
                await Pointer(Field<Button>(branch, "CreateBranch")); await Until(() => !branch.IsVisible && !dialog.IsBusy);
                Assert.That(branch.LastError, Is.Null, branch.LastException?.ToString()); Assert.That(await f.Text("rev-parse", "refs/heads/found"), Is.EqualTo(f.Lost));
                Assert.That((await f.Repository.GetStatusAsync()).IsUnborn, Is.EqualTo(bare));
            }
            else
            {
                Invoke(Context(dialog, "RecoveryCreateTag")); CreateTagDialog tag = await Child<CreateTagDialog>(dialog); Assert.That(tag.ReadOptions().Revision, Is.EqualTo(f.Lost));
                Field<TextBox>(tag, "CreateTagName").Text = "found"; Assert.That(Field<Button>(tag, "CreateTag").IsEnabled, Is.True);
                await Pointer(Field<Button>(tag, "CreateTag")); await Until(() => !tag.IsVisible && !dialog.IsBusy);
                Assert.That(tag.LastError, Is.Null, tag.LastException?.ToString()); Assert.That(await f.Text("rev-parse", "refs/tags/found"), Is.EqualTo(f.Lost));
                Assert.That((await f.Repository.GetStatusAsync()).IsUnborn, Is.True);
            }
        }
    }

    [AvaloniaTest]
    public async Task RecoverWithNoCheckedRowsWarnsWithoutDeletingPreviousRecoveryTags()
    {
        await using Fixture f = await Fixture.Create(); await f.Git("tag", "LOST_FOUND_previous", f.Current); RecoverLostObjectsDialog dialog = await f.Show(); string before = await f.Witness();
        await Pointer(Field<Button>(dialog, "RecoveryRecoverSelected")); Window warning = await OwnedDialog(dialog, "RecoveryEmptySelection");
        Assert.That(warning.Title, Is.EqualTo("Restore lost objects")); Assert.That(Field<TextBlock>(warning, "RecoveryMessage").Text, Is.EqualTo("Select objects to restore."));
        await Pointer(Field<Button>(warning, "CloseRecoveryEmptySelection")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(dialog.LastRecovery, Is.Null); Assert.That(await f.Witness(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task RecoverUsesCheckedVisibleRowsAndKeepsExactAnnotatedTagAndBlobIdentities()
    {
        await using Fixture f = await Fixture.Create(); await f.Git("tag", "LOST_FOUND_previous", f.Current); await f.Git("tag", "keep", f.Current);
        RecoverLostObjectsDialog dialog = await f.Show(); await ShowOther(dialog);
        await Select(dialog, f.Lost); await Pointer(Check(dialog)); await Select(dialog, f.Tag); await Pointer(Check(dialog)); await Select(dialog, f.Blob); await Pointer(Check(dialog));
        string[] selected = dialog.VisibleObjects.Where(item => new[] { f.Lost, f.Tag, f.Blob }.Contains(item.Hash)).Select(item => item.Hash).ToArray();
        await Select(dialog, f.Binary); await Pointer(Field<Button>(dialog, "RecoveryRecoverSelected")); Window result = await OwnedDialog(dialog, "RecoveryTagsCreated");
        Assert.That(dialog.LastRecovery!.CreatedTags.Count, Is.EqualTo(3)); Assert.That(dialog.LastRecovery.Success, Is.True); Assert.That(dialog.LastRecovery.EffectsVerified, Is.True);
        Assert.That(Field<TextBlock>(result, "RecoveryMessage").Text, Does.StartWith("3 Tags created."));
        for (int index = 0; index < selected.Length; index++)
        { string name = "LOST_FOUND_" + (selected[index] == f.Tag ? "former-tag" : (index + 1).ToString(CultureInfo.InvariantCulture)); Assert.That(await f.Text("rev-parse", "refs/tags/" + name), Is.EqualTo(selected[index])); }
        Assert.That(await f.Text("rev-parse", "refs/tags/keep"), Is.EqualTo(f.Current)); Assert.That((await f.Repository.GetReferencesAsync()).Any(reference => reference.ShortName == "LOST_FOUND_previous"), Is.False);
        Assert.That(await f.Text("cat-file", "-t", "refs/tags/LOST_FOUND_former-tag"), Is.EqualTo("tag"), "Recovery must tag the annotated tag object itself, without peeling it.");
        await Pointer(Field<Button>(result, "CloseRecoveryTagsCreated")); await Until(() => !dialog.IsBusy); Assert.That(dialog.IsVisible, Is.True);
        Assert.That(dialog.LastScan!.Objects.Any(item => selected.Contains(item.Hash)), Is.False); Assert.That(dialog.LastScan.Objects.Any(item => item.Hash == f.Binary), Is.True, "Highlight is independent of the checked recovery set.");
    }

    [AvaloniaTest]
    public async Task RecoverReportsTheActualPartialCountAfterAnObjectDisappearsAndAllVisibleSuccessCloses()
    {
        await using Fixture f = await Fixture.Create(); RecoverLostObjectsDialog dialog = await f.Show(); await ShowOther(dialog);
        await Select(dialog, f.Lost); await Pointer(Check(dialog)); await Select(dialog, f.Blob); await Pointer(Check(dialog)); System.IO.File.Delete(f.ObjectPath(f.Blob));
        await Pointer(Field<Button>(dialog, "RecoveryRecoverSelected")); Window result = await OwnedDialog(dialog, "RecoveryTagsCreated");
        Assert.That(dialog.LastRecovery!.CreatedTags.Count, Is.EqualTo(1)); Assert.That(dialog.LastRecovery.Errors.Single().ObjectId, Is.EqualTo(f.Blob));
        Assert.That(Field<TextBlock>(result, "RecoveryMessage").Text, Does.StartWith("1 Tags created.").And.Contain(f.Blob));
        await Pointer(Field<Button>(result, "CloseRecoveryTagsCreated")); await Until(() => !dialog.IsBusy); Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.HasRepositoryChanges, Is.True);
        dialog.Close(); await f.Git("tag", "-d", dialog.LastRecovery.CreatedTags.Single().Name); dialog = await f.Show();
        await Pointer(Field<CheckBox>(dialog, "RecoveryCheckAll")); int count = dialog.VisibleObjects.Count; await Pointer(Field<Button>(dialog, "RecoveryRecoverSelected")); result = await OwnedDialog(dialog, "RecoveryTagsCreated");
        Assert.That(dialog.LastRecovery!.CreatedTags.Count, Is.EqualTo(count)); await Pointer(Field<Button>(result, "CloseRecoveryTagsCreated")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
    }

    [AvaloniaTest]
    public async Task SaveAsUsesChosenLocalFilesExactBinaryBytesAndTheOriginalAutocrlfRule()
    {
        await using Fixture f = await Fixture.Create(); RecoverLostObjectsDialog dialog = await f.Show(); await ShowOther(dialog); string before = await f.Witness();
        await Select(dialog, f.Binary); string destination = Path.Combine(f.Root, "chosen.png"); System.IO.File.WriteAllText(destination, "replace"); FilePickerSaveOptions? pickerOptions = null;
        dialog.SaveFilePicker = async options => { pickerOptions = options; return await StorageFile(dialog, destination); };
        Invoke(Context(dialog, "RecoverySaveBlob")); await Until(() => !dialog.IsBusy); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
        Assert.That(System.IO.File.ReadAllBytes(destination), Is.EqualTo(f.BinaryBytes)); Assert.That(pickerOptions!.SuggestedFileName, Is.EqualTo("LOST_FOUND_" + f.Binary + ".png"));
        Assert.That(pickerOptions.ShowOverwritePrompt, Is.True); Assert.That(pickerOptions.FileTypeChoices!.SelectMany(type => type.Patterns ?? []), Does.Contain("*.png"));
        Assert.That(await f.Witness(), Is.EqualTo(before)); Assert.That(dialog.HasRepositoryChanges, Is.False);
        foreach (string autocrlf in new[] { "false", "input", "true" })
        {
            await f.Git("config", "core.autocrlf", autocrlf); await Select(dialog, f.Blob); destination = Path.Combine(f.Root, "chosen-" + autocrlf + ".txt"); System.IO.File.WriteAllText(destination, "replace");
            Invoke(Context(dialog, "RecoverySaveBlob")); await Until(() => !dialog.IsBusy); Assert.That(dialog.LastError, Is.Null);
            byte[] expected = autocrlf == "true" ? Fixture.Cp.GetBytes("texte à sauver €\r\nseconde ligne\r\n") : f.BlobBytes; Assert.That(System.IO.File.ReadAllBytes(destination), Is.EqualTo(expected));
        }
        destination = f.File("seed.txt"); Invoke(Context(dialog, "RecoverySaveBlob")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.HasRepositoryChanges, Is.True, "Export over a chosen worktree file must refresh Browse after closing.");
        Assert.That(System.IO.File.ReadAllBytes(destination), Is.EqualTo(Fixture.Cp.GetBytes("texte à sauver €\r\nseconde ligne\r\n")));
    }

    [AvaloniaTest]
    public async Task SaveThroughAnOutsideDirectoryAliasRefreshesTheActualWorktreeEvenWhenItsOpenPathIsAliased()
    {
        foreach (bool openViaAlias in new[] { false, true })
        {
            await using Fixture f = await Fixture.Create(); string alias = Path.Combine(f.Root, "outside-alias"); Directory.CreateSymbolicLink(alias, f.Repository.Path);
            RecoverLostObjectsDialog dialog = await f.Show(repositoryPath: openViaAlias ? alias : null); await ShowOther(dialog); await Select(dialog, f.Blob);
            string chosenPath = Path.Combine(alias, "seed.txt"), actualPath = f.File("seed.txt"), refs = await f.Refs(), head = await f.Head();
            byte[] index = System.IO.File.ReadAllBytes(f.File(".git/index")), config = System.IO.File.ReadAllBytes(f.File(".git/config"));
            Assert.That(chosenPath.StartsWith(f.Repository.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal), Is.False, "The chosen path is lexically outside the worktree.");
            Assert.That(dialog.HasRepositoryChanges, Is.False);
            dialog.SaveFilePicker = async _ =>
            {
                IStorageFile file = await StorageFile(dialog, chosenPath);
                Assert.That(file.TryGetLocalPath(), Is.EqualTo(chosenPath), "The real storage file preserves the chosen alias until Core resolves the export."); return file;
            };
            Invoke(Context(dialog, "RecoverySaveBlob")); await Until(() => !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(System.IO.File.ReadAllBytes(actualPath), Is.EqualTo(f.BlobBytes));
            Assert.That(dialog.HasRepositoryChanges, Is.True, "Browse must refresh for the actual file written through the outside alias.");
            Assert.That(Field<TextBlock>(dialog, "OperationStatus").Text, Is.EqualTo(actualPath), "The notice shows Core's resolved destination, not the picker alias.");
            Assert.That(Field<ScrollViewer>(dialog, "RecoveryStatus").Content, Is.SameAs(Field<TextBlock>(dialog, "OperationStatus")));
            Assert.That(await f.Refs(), Is.EqualTo(refs)); Assert.That(await f.Head(), Is.EqualTo(head));
            Assert.That(System.IO.File.ReadAllBytes(f.File(".git/index")), Is.EqualTo(index)); Assert.That(System.IO.File.ReadAllBytes(f.File(".git/config")), Is.EqualTo(config));
        }
    }

    [AvaloniaTest]
    public async Task SavePickerCancelAndCloseWhileAwaitingSelectionNeverWriteADeferredFile()
    {
        await using Fixture f = await Fixture.Create(); RecoverLostObjectsDialog dialog = await f.Show(); await ShowOther(dialog); await Select(dialog, f.Blob); string before = await f.Witness();
        dialog.SaveFilePicker = _ => Task.FromResult<IStorageFile?>(null); Invoke(Context(dialog, "RecoverySaveBlob")); await Until(() => !dialog.IsBusy); Assert.That(await f.Witness(), Is.EqualTo(before));
        string destination = Path.Combine(f.Root, "closed-picker.txt"); System.IO.File.WriteAllText(destination, "unchanged");
        TaskCompletionSource<IStorageFile?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously); bool waiting = false;
        dialog.SaveFilePicker = _ => { waiting = true; return gate.Task; }; Invoke(Context(dialog, "RecoverySaveBlob")); await Until(() => waiting);
        IStorageFile chosen = await StorageFile(dialog, destination); dialog.Close(); gate.SetResult(chosen); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(System.IO.File.ReadAllText(destination), Is.EqualTo("unchanged")); Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(await f.Witness(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task SaveNativeLostFoundAndDeleteOnlyTheRealRecoveryPrefixRemainDistinct()
    {
        await using Fixture f = await Fixture.Create(); await f.Git("tag", "LOST_FOUND_previous", f.Current); await f.Git("tag", "LOST_AND_FOUND_keep", f.Current); await f.Git("tag", "keep", f.Current);
        RecoverLostObjectsDialog dialog = await f.Show(); byte[] index = System.IO.File.ReadAllBytes(f.File(".git/index")); string files = f.Worktree(), refs = await f.Refs();
        await Pointer(Field<Button>(dialog, "RecoverySaveLostFound")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.LastError, Is.Null); string directory = dialog.LastScan!.LostFoundDirectory;
        Assert.That(System.IO.File.ReadAllBytes(Path.Combine(directory, "other", f.Blob)), Is.EqualTo(f.BlobBytes));
        Assert.That(System.IO.File.ReadAllText(Path.Combine(directory, "commit", f.Lost)).TrimEnd(), Is.EqualTo(f.Lost));
        Assert.That(await f.Refs(), Is.EqualTo(refs)); Assert.That(f.Worktree(), Is.EqualTo(files)); Assert.That(System.IO.File.ReadAllBytes(f.File(".git/index")), Is.EqualTo(index));
        await Pointer(Field<Button>(dialog, "RecoveryDeleteTags")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.LastRecovery!.DeletedTags, Is.EqualTo(new[] { "LOST_FOUND_previous" })); Assert.That(await f.Text("rev-parse", "refs/tags/LOST_AND_FOUND_keep"), Is.EqualTo(f.Current));
        Assert.That(await f.Text("rev-parse", "refs/tags/keep"), Is.EqualTo(f.Current)); Assert.That(dialog.HasRepositoryChanges, Is.True);
    }

    [AvaloniaTest]
    public async Task PruneNoAndAcceptedPruneMatchPlainNativeGitWithoutInventedExpiryFlags()
    {
        await using Fixture f = await Fixture.Create(); await f.Git("reflog", "expire", "--expire=now", "--all"); f.AgeObjects();
        string twin = Path.Combine(f.Root, "native-twin"); Fixture.CopyDirectory(f.Repository.Path, twin);
        GitResult native = await new GitRunner().RunAsync(twin, ["prune"]); Assert.That(native.Success, Is.True, native.StandardError);
        RecoverLostObjectsDialog dialog = await f.Show(); string before = await f.Witness(); string objects = f.ObjectSet();
        await Pointer(Field<Button>(dialog, "RecoveryRemoveDangling")); Window warning = await OwnedDialog(dialog, "RecoveryPruneWarning");
        Assert.That(warning.Title, Is.EqualTo("Remove")); Assert.That(Field<TextBlock>(warning, "RecoveryMessage").Text, Is.EqualTo("Are you sure you want to delete all dangling objects?"));
        await Pointer(Field<Button>(warning, "CancelRecoveryPrune")); await Until(() => !dialog.IsBusy); Assert.That(await f.Witness(), Is.EqualTo(before)); Assert.That(f.ObjectSet(), Is.EqualTo(objects)); Assert.That(dialog.HasRepositoryChanges, Is.False);
        await Pointer(Field<Button>(dialog, "RecoveryRemoveDangling")); warning = await OwnedDialog(dialog, "RecoveryPruneWarning"); await Pointer(Field<Button>(warning, "ConfirmRecoveryPrune")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.LastError, Is.Null); Assert.That(f.ObjectSet(), Is.EqualTo(Fixture.ObjectSet(Path.Combine(twin, ".git/objects")))); Assert.That(dialog.LastScan!.Objects, Is.Empty);
        Assert.That(await f.Witness(), Is.EqualTo(before)); Assert.That(dialog.HasRepositoryChanges, Is.True);
    }

    [AvaloniaTest]
    public async Task ClosingThePruneConfirmationCancelsBeforeGitAndLeavesNoMutationMark()
    {
        await using Fixture f = await Fixture.Create(); RecoverLostObjectsDialog dialog = await f.Show(); string before = await f.Witness(); string objects = f.ObjectSet();
        await Pointer(Field<Button>(dialog, "RecoveryRemoveDangling")); _ = await OwnedDialog(dialog, "RecoveryPruneWarning"); dialog.Close();
        await Until(() => !dialog.IsVisible && !dialog.IsBusy); Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(await f.Witness(), Is.EqualTo(before)); Assert.That(f.ObjectSet(), Is.EqualTo(objects));
    }

    [AvaloniaTest]
    public async Task BrowseRecoveryAndVerifyToolsShareTheRealWindowWhileStatisticsAndFrenchLabelsRemainSeparate()
    {
        await using Fixture f = await Fixture.Create(); MainWindow main = new() { RequestedThemeVariant = ThemeVariant.Dark }; f.Windows.Add(main); main.Show(); await main.OpenRepositoryAsync(f.Repository.Path); string before = await f.Witness();
        MenuItem[] menu = Flatten(main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>()).ToArray();
        MenuItem recover = menu.Single(item => item.Name == "BrowseRecoverLostObjects"), group = menu.Single(item => item.Name == "BrowseGitMaintenance");
        Assert.That(group.Items.OfType<MenuItem>(), Does.Contain(recover)); Assert.That(((AccessText)group.Header!).Text, Is.EqualTo("_Git maintenance"));
        Assert.That(((AccessText)recover.Header!).Text, Is.EqualTo("_Recover lost objects…")); Assert.That(((Image)recover.Icon!).Tag, Is.EqualTo("RecoverLostObjects.png"));
        Invoke(recover); RecoverLostObjectsDialog dialog = await Child<RecoverLostObjectsDialog>(main); Assert.That(dialog.Owner, Is.SameAs(main)); Assert.That(dialog.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark)); dialog.Close(); await Until(() => !main.IsBusy);
        Invoke(menu.Single(item => item.Header as string == "Maintenance…")); Window tools = await VisibleOwned(main);
        await Pointer(tools.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Verify objects")); dialog = await Child<RecoverLostObjectsDialog>(tools);
        Assert.That(dialog.Owner, Is.SameAs(tools)); dialog.Close(); await Until(() => !main.IsBusy);
        await Pointer(tools.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Repository statistics")); MaintenanceDialog statistics = await Child<MaintenanceDialog>(tools);
        Assert.That(statistics.LastStatistics, Is.Not.Null); statistics.Close(); await Until(() => !main.IsBusy); tools.Close(); Assert.That(await f.Witness(), Is.EqualTo(before));
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr"); dialog = await f.Show();
        Assert.That(dialog.Title, Is.EqualTo("Vérifiez la base de données")); Assert.That(((AccessText)Field<Button>(dialog, "RecoveryRecoverSelected").Content!).Text, Is.EqualTo("Restaurer les objets sélectionnés"));
        Assert.That(((AccessText)Context(dialog, "RecoverySaveBlob").Header!).Text, Is.EqualTo("Enregistrer sous…"));
        await Pointer(Field<Button>(dialog, "RecoveryRecoverSelected")); Window warning = await OwnedDialog(dialog, "RecoveryEmptySelection"); Assert.That(warning.Title, Is.EqualTo("Restaurer les objets perdus"));
        Assert.That(Field<TextBlock>(warning, "RecoveryMessage").Text, Is.EqualTo("Sélectionner les objets à restaurer.")); Capture(dialog, "recover-original-french.png");
        await Pointer(Field<Button>(warning, "CloseRecoveryEmptySelection")); await Until(() => !dialog.IsBusy);
    }

    private static T Field<T>(Control owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static MenuItem Context(RecoverLostObjectsDialog dialog, string name) => Field<ListBox>(dialog, "RecoveryObjects").ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static IEnumerable<MenuItem> Flatten(IEnumerable<MenuItem> items) => items.SelectMany(item => new[] { item }.Concat(Flatten(item.Items.OfType<MenuItem>())));
    private static void Invoke(MenuItem item) { Assert.That(item.IsEnabled, Is.True, item.Name); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
    private static int Index(RecoverLostObjectsDialog dialog, string hash) => Enumerable.Range(0, dialog.VisibleObjects.Count).Single(index => dialog.VisibleObjects[index].Hash == hash);
    private static async Task Select(RecoverLostObjectsDialog dialog, string hash)
    { ListBox list = Field<ListBox>(dialog, "RecoveryObjects"); list.SelectedIndex = Index(dialog, hash); object? selected = list.SelectedItem; Assert.That(selected, Is.Not.Null); list.ScrollIntoView(selected!); Pump(dialog); await Until(() => dialog.Preview?.Hash == hash); Pump(dialog); }
    private static ListBoxItem Row(RecoverLostObjectsDialog dialog) => Field<ListBox>(dialog, "RecoveryObjects").GetVisualDescendants().OfType<ListBoxItem>().Single(item => Equals(item.DataContext, Field<ListBox>(dialog, "RecoveryObjects").SelectedItem));
    private static CheckBox Check(RecoverLostObjectsDialog dialog) => Row(dialog).GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "RecoveryObjectChecked");
    private static async Task ShowOther(RecoverLostObjectsDialog dialog) { await Pointer(Field<CheckBox>(dialog, "RecoveryShowOther")); await Until(() => dialog.VisibleObjects.Any(item => item.Type == GitLostObjectType.Blob)); }
    private static async Task<T> Child<T>(Window owner) where T : RepositoryDialog
    { await Until(() => owner.OwnedWindows.OfType<T>().Any(child => child.IsVisible && !child.IsBusy)); T dialog = owner.OwnedWindows.OfType<T>().Single(child => child.IsVisible); Pump(dialog); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); return dialog; }
    private static async Task<Window> OwnedDialog(Window owner, string name)
    {
        await Until(() => owner.OwnedWindows.Any(child => child.IsVisible && child.Name == name), () =>
            name + "; busy=" + (owner as RepositoryDialog)?.IsBusy + "; LastError=" + (owner as RepositoryDialog)?.LastError
            + "; LastException=" + (owner as RepositoryDialog)?.LastException + "; focused=" + owner.FocusManager?.GetFocusedElement()
            + "; owned=" + string.Join(", ", owner.OwnedWindows.Select(child => child.Name + ":visible=" + child.IsVisible)));
        Window dialog = owner.OwnedWindows.Single(child => child.IsVisible && child.Name == name); Pump(dialog); return dialog;
    }
    private static async Task<Window> VisibleOwned(Window owner) { await Until(() => owner.OwnedWindows.Any(child => child.IsVisible)); Window dialog = owner.OwnedWindows.Single(child => child.IsVisible); Pump(dialog); return dialog; }
    private static async Task<IStorageFile> StorageFile(Window owner, string path) { IStorageFile? file = await owner.StorageProvider.TryGetFileFromPathAsync(new Uri(path)); Assert.That(file, Is.Not.Null, "The picker seam must return a real local IStorageFile, not substitute the export."); return file!; }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static Rect Bounds(TopLevel window, Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void Press(TopLevel window, PhysicalKey key, TopLevel? releaseOn = null) { window.KeyPressQwerty(key, RawInputModifiers.None); (releaseOn ?? window).KeyReleaseQwerty(key, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); }
    private static async Task Pointer(Control control, double fraction = .5)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; Stopwatch timer = Stopwatch.StartNew(); bool ready = false; Point point = default; string hit = "";
        do
        {
            Pump(top); point = control.TranslatePoint(new Point(control.Bounds.Width * fraction, control.Bounds.Height / 2), top)!.Value; top.MouseMove(point);
            Visual? visual = top.InputHitTest(point) as Visual; hit = string.Join(" > ", visual?.GetSelfAndVisualAncestors().Select(item => item.GetType().Name + "#" + (item as Control)?.Name) ?? []);
            ready = control.IsEffectivelyEnabled && control.IsEffectivelyVisible && new Rect(default, top.ClientSize).Contains(point) && visual?.GetSelfAndVisualAncestors().Contains(control) == true;
            if (ready) { break; } await Task.Delay(10);
        } while (timer.Elapsed < TimeSpan.FromSeconds(8));
        Assert.That(ready, Is.True, control.Name + " must receive a real pointer; hit=" + hit); top.MouseDown(point, MouseButton.Left); top.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static async Task Until(Func<bool> condition, Func<string>? diagnostic = null) { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True, "Object recovery UI did not settle. " + diagnostic?.Invoke()); }
    private static void Capture(TopLevel window, string name) { Pump(window); using Bitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }

    private sealed class Fixture : IAsyncDisposable
    {
        public static Encoding Cp { get { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(1252); } }
        private readonly Dictionary<string, string?> _environment = [];
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly GitRunner _runner = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-recovery-ui-" + Guid.NewGuid().ToString("N"));
        private string _repositoryPath = "";
        public GitRepository Repository => new(_repositoryPath);
        public Window Owner { get; } = new() { Width = 1100, Height = 700 };
        public List<Window> Windows { get; } = [];
        public string Initial = "", Lost = "", Current = "", Blob = "", Binary = "", Tree = "", TreeBlob = "", Tag = "";
        public byte[] BlobBytes { get; } = Cp.GetBytes("texte à sauver €\nseconde ligne\n");
        public byte[] BinaryBytes { get; } = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0, 0, 0, 255];
        private Fixture()
        {
            _repositoryPath = Path.Combine(Root, "repo"); Directory.CreateDirectory(_repositoryPath);
            foreach ((string name, string value) in new[] { ("XDG_CONFIG_HOME", Path.Combine(Root, "config")), ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_OPTIONAL_LOCKS", "0") })
            { _environment[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); } CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        }
        public string File(string path) => Path.Combine(Repository.Path, path);
        public async Task<GitResult> Git(params string[] arguments) { GitResult result = await _runner.RunAsync(Repository.Path, arguments); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public async Task<string> Text(params string[] arguments) => (await Git(arguments)).StandardOutput.TrimEnd('\r', '\n');
        public Task<string> Head() => Text("rev-parse", "HEAD");
        public Task<string> Refs() => Text("for-each-ref", "--format=%(refname) %(objectname)");
        private async Task<string> BinaryGit(byte[] input, string[] arguments, IReadOnlyDictionary<string, string?>? environment = null)
        { GitBinaryResult result = await _runner.RunBinaryAsync(Repository.Path, arguments, standardInput: input, environment: environment); Assert.That(result.Success, Is.True, result.StandardError); return Encoding.UTF8.GetString(result.StandardOutput).TrimEnd('\r', '\n'); }
        public string ObjectPath(string oid) => Path.Combine(Repository.Path, (System.IO.File.Exists(File(".git/config")) ? ".git/" : "") + "objects", oid[..2], oid[2..]);
        public static async Task<Fixture> Create()
        {
            Fixture f = new();
            try
            {
                await f.Git("init", "-b", "main"); await f.Git("config", "user.name", "Élodie λ"); await f.Git("config", "user.email", "recovery@example.invalid");
                await f.Git("config", "commit.gpgsign", "false"); await f.Git("config", "core.hooksPath", "/dev/null"); await f.Git("config", "i18n.filesEncoding", "windows-1252");
                System.IO.File.WriteAllText(f.File("seed.txt"), "seed\n"); await f.Git("add", "seed.txt"); await f.Git("commit", "-m", "Initial"); f.Initial = await f.Head();
                await f.Git("config", "i18n.commitEncoding", "windows-1252"); System.IO.File.WriteAllBytes(f.File("lost.txt"), Cp.GetBytes("Après €\n")); await f.Git("add", "lost.txt");
                _ = await f.BinaryGit(Cp.GetBytes("Café perdu €\n"), ["commit", "--file=-"], new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = "2019-01-01T00:00:00Z", ["GIT_COMMITTER_DATE"] = "2020-01-01T00:00:00Z" }); f.Lost = await f.Head();
                await f.Git("reset", "--hard", f.Initial); System.IO.File.WriteAllText(f.File("current.txt"), "current\n"); await f.Git("add", "current.txt"); await f.Git("commit", "-m", "Current"); f.Current = await f.Head();
                f.Blob = await f.BinaryGit(f.BlobBytes, ["hash-object", "-w", "--stdin"]); f.Binary = await f.BinaryGit(f.BinaryBytes, ["hash-object", "-w", "--stdin"]);
                f.TreeBlob = await f.BinaryGit(Encoding.UTF8.GetBytes("tree content\n"), ["hash-object", "-w", "--stdin"]); f.Tree = await f.BinaryGit(Encoding.UTF8.GetBytes("100644 blob " + f.TreeBlob + "\ttree λ.txt\n"), ["mktree"]);
                await f.Git("tag", "--annotate", "--no-sign", "-m", "Ancienne étiquette", "former-tag", f.Initial); f.Tag = await f.Text("rev-parse", "refs/tags/former-tag"); await f.Git("tag", "-d", "former-tag");
                f.Owner.Show(); Pump(f.Owner); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async Task MakeUnborn(bool bare)
        {
            if (bare)
            { string destination = Path.Combine(Root, "bare.git"); await Git("clone", "--bare", Repository.Path, destination); _repositoryPath = destination; await Git("config", "i18n.filesEncoding", "windows-1252"); }
            await Git("symbolic-ref", "HEAD", "refs/heads/unborn"); await Git("update-ref", "-d", "refs/heads/main");
            if (!bare)
            { await Git("read-tree", "--empty"); foreach (string path in Directory.EnumerateFiles(Repository.Path).Where(path => Path.GetFileName(path) != ".git")) { System.IO.File.Delete(path); } }
        }
        public async Task<RecoverLostObjectsDialog> Show(bool dark = false, string? repositoryPath = null)
        { RecoverLostObjectsDialog dialog = new(repositoryPath is null ? Repository : new GitRepository(repositoryPath)) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; Windows.Add(dialog); await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); _ = dialog.ShowDialog(Owner); Pump(dialog); return dialog; }
        public string Worktree() => JsonSerializer.Serialize(Directory.EnumerateFiles(Repository.Path, "*", SearchOption.AllDirectories).Where(path => !path.StartsWith(File(".git") + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .ToDictionary(path => Path.GetRelativePath(Repository.Path, path), path => Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path)))));
        public async Task<string> Witness()
        {
            string refs = await Refs(); Dictionary<string, string> logs = Directory.Exists(File(".git/logs")) ? Directory.EnumerateFiles(File(".git/logs"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToDictionary(path => Path.GetRelativePath(Repository.Path, path), path => Convert.ToBase64String(System.IO.File.ReadAllBytes(path))) : [];
            return JsonSerializer.Serialize(new { refs, files = Worktree(), logs, head = Convert.ToBase64String(System.IO.File.ReadAllBytes(File(".git/HEAD"))), index = Convert.ToBase64String(System.IO.File.ReadAllBytes(File(".git/index"))), config = Convert.ToBase64String(System.IO.File.ReadAllBytes(File(".git/config"))) });
        }
        public string ObjectSet() => ObjectSet(File(".git/objects"));
        public static string ObjectSet(string directory) => string.Join('\n', Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(directory, path) + " " + Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path)))));
        public void AgeObjects() { foreach (string path in Directory.EnumerateFiles(File(".git/objects"), "*", SearchOption.AllDirectories)) { System.IO.File.SetLastWriteTimeUtc(path, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)); } }
        public static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination); foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) { Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory))); }
            foreach (string path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) { string target = Path.Combine(destination, Path.GetRelativePath(source, path)); System.IO.File.Copy(path, target); System.IO.File.SetLastWriteTimeUtc(target, System.IO.File.GetLastWriteTimeUtc(path)); }
        }
        public async ValueTask DisposeAsync()
        {
            static void CloseChildren(Window window) { foreach (Window child in window.OwnedWindows.ToArray()) { CloseChildren(child); child.Close(); } }
            foreach (Window window in Windows.AsEnumerable().Reverse()) { CloseChildren(window); window.Close(); } CloseChildren(Owner); Owner.Close(); Stopwatch timer = Stopwatch.StartNew();
            while (Windows.OfType<RepositoryDialog>().Any(dialog => dialog.IsBusy) && timer.Elapsed < TimeSpan.FromSeconds(8)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            foreach (Window window in Windows) { window.Close(); } Dispatcher.UIThread.RunJobs(); foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); } CultureInfo.CurrentUICulture = _culture;
            if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
        }
    }
}

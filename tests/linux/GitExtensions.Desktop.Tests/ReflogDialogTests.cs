using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class ReflogDialogTests
{
    [AvaloniaTest] public Task OriginalGridReferencesLinksMenuAndCopyRenderInLight() => OriginalGridReferencesLinksMenuAndCopy(false);
    [AvaloniaTest] public Task OriginalGridReferencesLinksMenuAndCopyRenderInDark() => OriginalGridReferencesLinksMenuAndCopy(true);
    private async Task OriginalGridReferencesLinksMenuAndCopy(bool dark)
    {
        await using Fixture f = await Fixture.Create();
        ReflogDialog dialog = await f.Show(dark);
        Capture(dialog, "reflog-open-" + (dark ? "dark" : "light") + ".png");
        string before = await f.Witness();
        ComboBox references = Field<ComboBox>(dialog, "ReflogReference"); ListBox entries = Field<ListBox>(dialog, "ReflogEntries");
        Assert.Multiple(() =>
        {
            Assert.That(dialog.Title, Is.EqualTo("Reflog")); Assert.That(dialog.Width, Is.EqualTo(782)); Assert.That(dialog.Height, Is.EqualTo(555));
            Assert.That(dialog.MinWidth, Is.EqualTo(400)); Assert.That(dialog.MinHeight, Is.EqualTo(200));
            Assert.That(references.Width, Is.EqualTo(272)); Assert.That(references.Bounds.Height, Is.EqualTo(24));
            Assert.That(references.Items.Cast<object>().Select(item => item.ToString()), Is.EqualTo(new[] { "HEAD", "main", "side", "origin/main" }));
            Assert.That(references.GetVisualDescendants().OfType<TextBox>().Where(editor => editor.IsEffectivelyVisible && editor.Bounds.Width > 0 && editor.Bounds.Height > 0),
                Is.Empty, "FormReflog's reference selector must expose no editable text input; the Fluent template may contain a hidden editor.");
            Assert.That(Field<TextBlock>(dialog, "ReflogReferenceLabel").Text, Is.EqualTo("Reference:"));
            Assert.That(Field<TextBlock>(dialog, "ReflogDisplayLabel").Text, Is.EqualTo("Display reflog for:"));
            Assert.That(((TextBlock)Field<Button>(dialog, "ReflogCurrentBranch").Content!).Text, Is.EqualTo("current branch (main)"));
            Assert.That(Field<TextBlock>(dialog, "ReflogDirtyWorkingDirectory").IsVisible, Is.False);
            Assert.That(dialog.Entries.Select(entry => entry.Hash), Does.Contain(f.Lost));
            Assert.That(entries.SelectionMode, Is.EqualTo(SelectionMode.Single));
        });
        await SelectReference(dialog, "origin/main");
        Assert.That(dialog.SelectedReference, Is.EqualTo("refs/remotes/origin/main"));
        Assert.That(dialog.Entries.All(entry => entry.Selector.StartsWith("origin/main@{", StringComparison.Ordinal)), Is.True);
        await Pointer(Field<Button>(dialog, "ReflogCurrentBranch")); await Until(() => !dialog.IsBusy && dialog.SelectedReference == "refs/heads/main");
        await Pointer(Field<Button>(dialog, "ReflogHead")); await Until(() => !dialog.IsBusy && dialog.SelectedReference == "HEAD");
        Button referenceHeader = Field<Button>(dialog, "ReflogHeaderRef");
        Point columnEdge = new(Field<Grid>(dialog, "ReflogHeader").ColumnDefinitions[1].ActualWidth - 5, referenceHeader.Bounds.Height / 2);
        await Pointer(referenceHeader, columnEdge);
        Assert.That(entries.Items.OfType<GitReflogEntry>().Select(entry => entry.Selector), Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal));
        await Pointer(referenceHeader, columnEdge);
        Assert.That(entries.Items.OfType<GitReflogEntry>().Select(entry => entry.Selector), Is.Ordered.Descending.Using((IComparer<string>)StringComparer.Ordinal));
        SelectLost(dialog, f); Pump(dialog);
        ListBoxItem row = entries.GetVisualDescendants().OfType<ListBoxItem>().Single(item => Equals(item.DataContext, entries.SelectedItem));
        TextBlock sha = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == f.Lost);
        Assert.That(sha.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(sha.Bounds.Width + .5), "The complete SHA must fit its actual text column.");
        Rect header = Bounds(dialog, Field<Grid>(dialog, "ReflogHeader")), list = Bounds(dialog, entries), selectors = Bounds(dialog, Field<WrapPanel>(dialog, "ReflogSelectors"));
        Assert.That(selectors.Bottom, Is.LessThanOrEqualTo(header.Top)); Assert.That(header.Bottom, Is.LessThanOrEqualTo(list.Top + 1));
        Assert.That(entries.GetVisualDescendants().OfType<ListBoxItem>().Select(item => item.Bounds.Height), Is.All.EqualTo(24));
        ContentPresenter selected = row.GetVisualDescendants().OfType<ContentPresenter>().Single(item => item.Name == "PART_ContentPresenter");
        Color highlight = ((ISolidColorBrush)selected.Background!).Color;
        Assert.That(highlight, Is.EqualTo(Color.Parse(dark ? "#2864b4" : "#0078d7")));
        Assert.That(((ISolidColorBrush)sha.Foreground!).Color, Is.EqualTo(Color.Parse(dark ? "#f0f0f0" : "#ffffff")), "The generated SHA text inherits the selected-row foreground.");
        Point sample = selected.TranslatePoint(new Point(1, selected.Bounds.Height / 2), dialog)!.Value;
        Assert.That(Pixel(dialog, sample), Is.EqualTo(highlight), "The selected row's actual pixels must use the original palette.");
        foreach (Button link in new[] { Field<Button>(dialog, "ReflogHead"), Field<Button>(dialog, "ReflogCurrentBranch") })
        { Assert.That(Contrast(((ISolidColorBrush)((TextBlock)link.Content!).Foreground!).Color, ((ISolidColorBrush)dialog.Background!).Color), Is.GreaterThanOrEqualTo(4.5)); }
        IClipboard clipboard = dialog.Clipboard!; string? oldClipboard = await ClipboardExtensions.TryGetTextAsync(clipboard);
        try
        {
            ContextMenu menu = entries.ContextMenu!;
            // FormReflog opens its recovery menu on an ordinary grid click, not only a right click.
            await Pointer(sha); await Until(() => menu.IsOpen); Pump(dialog);
            MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.That(items.Select(item => ((AccessText)item.Header!).Text), Is.EqualTo(new[] { "Copy SHA-1", "Create a branch on this commit…", "Reset current branch to this commit…" }));
            Assert.That(items.Select(item => ((Image)item.Icon!).Tag), Is.EqualTo(new[] { "CommitId.png", "BranchCreate.png", "ResetCurrentBranchToHere.png" }));
            Assert.That(items.Select(item => ((Image)item.Icon!).Width), Is.All.EqualTo(16));
            await Pointer(items[0]); await Until(() => !dialog.IsBusy && !menu.IsOpen);
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo(f.Lost));
            menu.Open(entries); Pump(dialog); Assert.That(menu.Items.OfType<MenuItem>().Select(item => ((Image)item.Icon!).Source), Is.All.Not.Null, "Reopening must retain its three original icons.");
            Capture(dialog, "reflog-original-" + (dark ? "dark" : "light") + ".png"); menu.Close();
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, oldClipboard ?? ""); }
        Assert.That(await f.Witness(), Is.EqualTo(before), "Selecting references, sorting and copying are read-only.");
    }

    [AvaloniaTest]
    public async Task CreatingAnUnreachableBranchUsesTheFrozenShaWithoutCheckoutOrOrphan()
    {
        await using Fixture f = await Fixture.Create(); ReflogDialog dialog = await f.Show();
        Assert.That((await f.Repository.GetLogAsync(100)).Select(commit => commit.Hash), Does.Not.Contain(f.Lost), "The selected commit really is outside the current graph.");
        SelectLost(dialog, f); Invoke(Context(dialog, "ReflogCreateBranch"));
        CreateBranchDialog child = await Child<CreateBranchDialog>(dialog);
        Assert.Multiple(() =>
        {
            Assert.That(child.ReadOptions().Revision, Is.EqualTo(f.Lost)); Assert.That(child.ReadOptions().Checkout, Is.False);
            Assert.That(Field<CheckBox>(child, "CheckoutAfterCreate").IsChecked, Is.False);
            Assert.That(Field<CheckBox>(child, "CheckoutAfterCreate").IsEnabled, Is.True, "Only its initial value is false, as in FormReflog.");
            Assert.That(Field<TextBox>(child, "CreateBranchRevision").IsReadOnly, Is.True);
            Assert.That(Field<TextBox>(child, "CreateBranchRevision").IsEnabled, Is.False); Assert.That(Field<Button>(child, "ChooseBranchRevision").IsEnabled, Is.False);
            Assert.That(Field<CheckBox>(child, "CreateOrphanBranch").IsEnabled, Is.False); Assert.That(Field<CheckBox>(child, "ClearOrphanWorkingTree").IsEnabled, Is.False);
            Assert.That(Field<TextBlock>(child, "CreateBranchSummary").Text, Does.Contain("Café perdu €").And.Contain(f.Lost));
        });
        // Move the ordinal HEAD@{n}, and alter the bound text/parent selection while the child is waiting.
        await f.Git("update-ref", "-m", "shift reflog", "HEAD", f.Initial); await f.Git("update-ref", "-m", "restore HEAD", "HEAD", f.Current);
        Field<ListBox>(dialog, "ReflogEntries").SelectedItem = dialog.Entries.First(entry => entry.Hash == f.Current);
        Field<TextBox>(child, "CreateBranchRevision").Text = f.Current;
        Field<CheckBox>(child, "CreateOrphanBranch").IsChecked = true; Field<CheckBox>(child, "ClearOrphanWorkingTree").IsChecked = true;
        Assert.That(child.ReadOptions().Revision, Is.EqualTo(f.Lost)); Assert.That(child.ReadOptions().Orphan, Is.False); Assert.That(child.ReadOptions().ClearWorkingTree, Is.False);
        byte[] index = System.IO.File.ReadAllBytes(f.File(".git/index")); string files = f.Worktree();
        Field<TextBox>(child, "CreateBranchName").Text = "recovered/λ";
        await Pointer(Field<Button>(child, "CreateBranch")); await Until(() => !child.IsVisible && !dialog.IsBusy);
        Assert.That(child.LastError, Is.Null, child.LastException?.ToString()); Assert.That(child.CreatedBranch, Is.EqualTo("recovered/λ"));
        Assert.That(await f.Text("rev-parse", "refs/heads/recovered/λ"), Is.EqualTo(f.Lost)); Assert.That(await f.Head(), Is.EqualTo(f.Current));
        Assert.That((await f.Repository.GetStatusAsync()).Branch, Is.EqualTo("main")); Assert.That(f.Worktree(), Is.EqualTo(files));
        Assert.That(System.IO.File.ReadAllBytes(f.File(".git/index")), Is.EqualTo(index)); Assert.That(dialog.HasRepositoryChanges, Is.True);
        Assert.That(Field<ComboBox>(dialog, "ReflogReference").Items.Cast<object>().Select(item => item.ToString()), Does.Contain("recovered/λ"), "Refs are reread after creation.");
    }

    [AvaloniaTest]
    public async Task CancellingCreationPreservesGitAndDefaultCreateBranchOptionsRemainUnchanged()
    {
        await using Fixture f = await Fixture.Create(); ReflogDialog dialog = await f.Show(); string before = await f.Witness();
        SelectLost(dialog, f); Invoke(Context(dialog, "ReflogCreateBranch")); CreateBranchDialog child = await Child<CreateBranchDialog>(dialog);
        Field<TextBox>(child, "CreateBranchName").Text = "must-not-exist"; await Pointer(Field<Button>(child, "CancelCreateBranch")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(await f.Witness(), Is.EqualTo(before));
        CreateBranchDialog regular = new(f.Repository); f.Windows.Add(regular); regular.Show(f.Owner); await regular.InitializeAsync(); Pump(regular);
        Assert.That(regular.ReadOptions().Checkout, Is.True); Assert.That(Field<TextBox>(regular, "CreateBranchRevision").IsReadOnly, Is.False);
        Assert.That(Field<Button>(regular, "ChooseBranchRevision").IsEnabled, Is.True); Assert.That(Field<CheckBox>(regular, "CreateOrphanBranch").IsEnabled, Is.True);
        regular.Close(); dialog.Close(); Assert.That(await f.Witness(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task DirtyResetNoAndEscapeAreTheOriginalDefaultAndLeaveEveryGitByteUnchanged()
    {
        await using Fixture f = await Fixture.Create(); await f.Dirty(); ReflogDialog dialog = await f.Show(); string before = await f.Witness();
        Assert.That(Field<TextBlock>(dialog, "ReflogDirtyWorkingDirectory").Text, Does.Contain("Stash them before"));
        Assert.That(Field<TextBlock>(dialog, "ReflogDirtyWorkingDirectory").IsVisible, Is.True);
        foreach (bool escape in new[] { false, true })
        {
            SelectLost(dialog, f); Invoke(Context(dialog, "ReflogResetBranch")); Window warning = await Warning(dialog, "ReflogDirtyResetWarning");
            Assert.That(warning.Title, Is.EqualTo("Changes not committed...")); Assert.That(Field<Button>(warning, "CancelReflogDirtyReset").IsDefault, Is.True);
            Assert.That(Field<TextBlock>(warning, "ReflogDirtyResetMessage").Text, Is.EqualTo("You have changes in your working directory that could be lost.\n\nDo you want to continue?"));
            Assert.That(warning.GetVisualDescendants().OfType<Image>().Single().Tag, Is.EqualTo("Warning.png"));
            if (escape) { Press(warning, PhysicalKey.Escape, dialog); } else { await Pointer(Field<Button>(warning, "CancelReflogDirtyReset")); }
            await Until(() => !dialog.IsBusy); Assert.That(dialog.OwnedWindows.OfType<ResetCurrentBranchDialog>(), Is.Empty);
            Assert.That(await f.Witness(), Is.EqualTo(before)); Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
    }

    [AvaloniaTest]
    public async Task DirtyResetFreezesTheHashBeforeTheWarningAndSoftPreservesIndexAndWorktree()
    {
        await using Fixture f = await Fixture.Create(); await f.Dirty(); ReflogDialog dialog = await f.Show(true); string before = await f.Witness();
        SelectLost(dialog, f); Invoke(Context(dialog, "ReflogResetBranch")); Window warning = await Warning(dialog, "ReflogDirtyResetWarning");
        Field<ListBox>(dialog, "ReflogEntries").SelectedItem = dialog.Entries.First(entry => entry.Hash == f.Current);
        await Pointer(Field<Button>(warning, "ConfirmReflogDirtyReset")); ResetCurrentBranchDialog reset = await Child<ResetCurrentBranchDialog>(dialog);
        Assert.That(reset.Mode, Is.EqualTo(GitResetMode.Soft)); Assert.That(reset.Revision, Is.EqualTo(f.Lost)); Assert.That(reset.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
        await Pointer(Field<Button>(reset, "CancelResetCurrentBranch")); await Until(() => !dialog.IsBusy); Assert.That(await f.Witness(), Is.EqualTo(before));
        byte[] index = System.IO.File.ReadAllBytes(f.File(".git/index")); string files = f.Worktree();
        SelectLost(dialog, f); Invoke(Context(dialog, "ReflogResetBranch")); warning = await Warning(dialog, "ReflogDirtyResetWarning");
        await Pointer(Field<Button>(warning, "ConfirmReflogDirtyReset")); reset = await Child<ResetCurrentBranchDialog>(dialog);
        await Pointer(Field<Button>(reset, "ExecuteResetCurrentBranch")); await Until(() => !reset.IsVisible && !dialog.IsBusy);
        Assert.That(reset.Applied, Is.True); Assert.That(await f.Head(), Is.EqualTo(f.Lost)); Assert.That((await f.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
        Assert.That(System.IO.File.ReadAllBytes(f.File(".git/index")), Is.EqualTo(index)); Assert.That(f.Worktree(), Is.EqualTo(files)); Assert.That(dialog.HasRepositoryChanges, Is.True);
        Assert.That(dialog.Entries.First().Hash, Is.EqualTo(f.Lost)); Assert.That(dialog.Entries.First().Description, Does.StartWith("reset:"));
        Assert.That(Field<TextBlock>(dialog, "ReflogDirtyWorkingDirectory").IsVisible, Is.True);
    }

    [AvaloniaTest]
    public async Task CleanResetStartsHardButCancelAndItsOwnHardWarningStillProtectGit()
    {
        await using Fixture f = await Fixture.Create(); ReflogDialog dialog = await f.Show(); string before = await f.Witness();
        SelectLost(dialog, f); Invoke(Context(dialog, "ReflogResetBranch")); ResetCurrentBranchDialog reset = await Child<ResetCurrentBranchDialog>(dialog);
        Assert.That(reset.Mode, Is.EqualTo(GitResetMode.Hard)); Assert.That(dialog.OwnedWindows.Any(window => window.Name == "ReflogDirtyResetWarning"), Is.False);
        await Pointer(Field<Button>(reset, "CancelResetCurrentBranch")); await Until(() => !dialog.IsBusy); Assert.That(await f.Witness(), Is.EqualTo(before));
        SelectLost(dialog, f); Invoke(Context(dialog, "ReflogResetBranch")); reset = await Child<ResetCurrentBranchDialog>(dialog);
        await Pointer(Field<Button>(reset, "ExecuteResetCurrentBranch")); Window warning = await Warning(reset, "ResetHardWarning");
        await Pointer(Field<Button>(warning, "CancelHardReset")); await Until(() => Field<Button>(reset, "ExecuteResetCurrentBranch").IsEnabled);
        Assert.That(await f.Witness(), Is.EqualTo(before)); Assert.That(reset.Applied, Is.False);
        await Pointer(Field<Button>(reset, "ExecuteResetCurrentBranch")); warning = await Warning(reset, "ResetHardWarning");
        await Pointer(Field<Button>(warning, "ConfirmHardReset")); await Until(() => !reset.IsVisible && !dialog.IsBusy);
        Assert.That(reset.Applied, Is.True); Assert.That(await f.Head(), Is.EqualTo(f.Lost)); Assert.That(System.IO.File.ReadAllText(f.File("lost.txt")), Is.EqualTo("lost\n"));
        Assert.That(System.IO.File.Exists(f.File("current.txt")), Is.False); Assert.That((await f.Repository.GetStatusAsync()).Changes, Is.Empty);
        Assert.That(dialog.Entries.First().Hash, Is.EqualTo(f.Lost)); Assert.That(Field<TextBlock>(dialog, "ReflogDirtyWorkingDirectory").IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task DetachedHeadHidesCurrentLinkAndRefusesResetWhileRecoveryAndCopyRemainAvailable()
    {
        await using Fixture f = await Fixture.Create(); await f.Git("checkout", "--detach", f.Current); ReflogDialog dialog = await f.Show(); string before = await f.Witness();
        SelectLost(dialog, f); Assert.That(Field<Button>(dialog, "ReflogCurrentBranch").IsVisible, Is.False);
        Assert.That(Context(dialog, "ReflogResetBranch").IsEnabled, Is.False); Assert.That(Context(dialog, "ReflogCopySha").IsEnabled, Is.True); Assert.That(Context(dialog, "ReflogCreateBranch").IsEnabled, Is.True);
        Context(dialog, "ReflogResetBranch").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Assert.That(dialog.OwnedWindows, Is.Empty); Assert.That(await f.Witness(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ChangingTheCheckedOutBranchDuringDirtyConfirmationRefusesADeferredReset()
    {
        await using Fixture f = await Fixture.Create(); await f.Dirty(); ReflogDialog dialog = await f.Show();
        SelectLost(dialog, f); Invoke(Context(dialog, "ReflogResetBranch")); Window warning = await Warning(dialog, "ReflogDirtyResetWarning");
        await f.Git("symbolic-ref", "HEAD", "refs/heads/side"); string before = await f.Witness();
        await Pointer(Field<Button>(warning, "ConfirmReflogDirtyReset")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.LastError, Does.Contain("current branch changed")); Assert.That(dialog.OwnedWindows.OfType<ResetCurrentBranchDialog>(), Is.Empty);
        Assert.That(await f.Witness(), Is.EqualTo(before)); Assert.That(dialog.HasRepositoryChanges, Is.False);
        Assert.That(((TextBlock)Field<Button>(dialog, "ReflogCurrentBranch").Content!).Text, Is.EqualTo("current branch (side)"));
    }

    [AvaloniaTest]
    public async Task AReferenceDeletedAfterOpeningShowsTheGitErrorWithoutStaleRowsAndHeadRecovers()
    {
        await using Fixture f = await Fixture.Create(); ReflogDialog dialog = await f.Show(); await f.Git("branch", "-D", "side"); string before = await f.Witness();
        await SelectReference(dialog, "side");
        Assert.That(dialog.LastException, Is.TypeOf<GitCommandException>()); Assert.That(dialog.Entries, Is.Empty);
        Assert.That(Field<ListBox>(dialog, "ReflogEntries").Items, Is.Empty); Assert.That(Context(dialog, "ReflogCopySha").IsEnabled || Context(dialog, "ReflogCreateBranch").IsEnabled || Context(dialog, "ReflogResetBranch").IsEnabled, Is.False);
        Assert.That(Field<TextBlock>(dialog, "OperationStatus").Text, Is.EqualTo(dialog.LastError));
        await Pointer(Field<Button>(dialog, "ReflogHead")); await Until(() => !dialog.IsBusy && dialog.SelectedReference == "HEAD");
        Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.Entries, Is.Not.Empty); Assert.That(await f.Witness(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ARealBranchWithoutAReflogDisplaysAnEmptyGridWithoutInventedEntries()
    {
        await using Fixture f = await Fixture.Create(); await f.Git("-c", "core.logAllRefUpdates=false", "branch", "without-log", f.Current);
        Assert.That(System.IO.File.Exists(f.File(".git/logs/refs/heads/without-log")), Is.False);
        ReflogDialog dialog = await f.Show(); string before = await f.Witness(); await SelectReference(dialog, "without-log");
        Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.SelectedReference, Is.EqualTo("refs/heads/without-log")); Assert.That(dialog.Entries, Is.Empty);
        Assert.That(Context(dialog, "ReflogCopySha").IsEnabled || Context(dialog, "ReflogCreateBranch").IsEnabled || Context(dialog, "ReflogResetBranch").IsEnabled, Is.False);
        Assert.That(await f.Witness(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task AnUnbornRepositoryKeepsHeadSelectableAndOffersNoFictitiousRecoveryTarget()
    {
        await using Fixture f = await Fixture.Create(unborn: true); ReflogDialog dialog = await f.Show(); string before = await f.Witness();
        Assert.That(dialog.SelectedReference, Is.EqualTo("HEAD")); Assert.That(dialog.Entries, Is.Empty); Assert.That(dialog.LastError, Is.Null);
        Assert.That(Field<ComboBox>(dialog, "ReflogReference").Items.Cast<object>().Select(item => item.ToString()), Is.EqualTo(new[] { "HEAD" }));
        Assert.That(Field<Button>(dialog, "ReflogHead").IsEnabled, Is.True); Assert.That(Field<Button>(dialog, "ReflogCurrentBranch").IsVisible, Is.False);
        Assert.That(Context(dialog, "ReflogCopySha").IsEnabled || Context(dialog, "ReflogCreateBranch").IsEnabled || Context(dialog, "ReflogResetBranch").IsEnabled, Is.False);
        Assert.That(await f.Witness(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task TheVisibleReflogIncludesRealEntriesOlderThanTheLegacyHundredEntryLimit()
    {
        await using Fixture f = await Fixture.Create();
        for (int number = 0; number < 104; number++) { await f.Git("update-ref", "-m", "entry " + number, "refs/heads/main", number % 2 == 0 ? f.Initial : f.Current); }
        IReadOnlyList<GitReflogEntry> all = await f.Repository.GetReflogAsync(count: null);
        Assert.That(all.Count, Is.GreaterThan(100)); Assert.That((await f.Repository.GetReflogAsync()).Count, Is.EqualTo(100));
        ReflogDialog dialog = await f.Show(); Assert.That(dialog.Entries, Is.EqualTo(all));
        Assert.That(Field<ListBox>(dialog, "ReflogEntries").Items.Count, Is.EqualTo(all.Count)); Assert.That(dialog.Entries[^1].Description, Does.Contain("Initial"));
    }

    [AvaloniaTest]
    public async Task OriginalFrenchCatalogueSuppliesTheSelectorsContextActionsAndDirtyConfirmation()
    {
        await using Fixture f = await Fixture.Create(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr"); await f.Dirty(); ReflogDialog dialog = await f.Show();
        Assert.That(Field<TextBlock>(dialog, "ReflogReferenceLabel").Text, Is.EqualTo("Référence :"));
        Assert.That(Field<TextBlock>(dialog, "ReflogDisplayLabel").Text, Is.EqualTo("Afficher le reflog pour :"));
        Assert.That(((AccessText)Context(dialog, "ReflogCreateBranch").Header!).Text, Is.EqualTo("Créer une branche sur ce commit"));
        Assert.That(((AccessText)Context(dialog, "ReflogCopySha").Header!).Text?.TrimEnd(), Is.EqualTo("Copier le SHA-1"));
        Assert.That(((AccessText)Context(dialog, "ReflogResetBranch").Header!).Text, Is.EqualTo("Réinitialiser la branche courante à ce commit…"));
        Assert.That(Field<TextBlock>(dialog, "ReflogDirtyWorkingDirectory").Text, Does.Contain("changements"));
        SelectLost(dialog, f); Invoke(Context(dialog, "ReflogResetBranch")); Window warning = await Warning(dialog, "ReflogDirtyResetWarning");
        Assert.That(warning.Title, Is.EqualTo("Modifications non commitées…")); Assert.That(Field<TextBlock>(warning, "ReflogDirtyResetMessage").Text, Does.Contain("Voulez-vous continuer?"));
        Assert.That(Field<Button>(warning, "CancelReflogDirtyReset").IsDefault, Is.True); Capture(warning, "reflog-dirty-french.png");
        await Pointer(Field<Button>(warning, "CancelReflogDirtyReset")); await Until(() => !dialog.IsBusy);
    }

    [AvaloniaTest]
    public async Task BrowseCommandsAndRepositoryToolsOpenTheRealReferenceDialogWithTheCorrectOwner()
    {
        await using Fixture f = await Fixture.Create(); MainWindow main = new() { RequestedThemeVariant = ThemeVariant.Dark }; f.Windows.Add(main); main.Show(); await main.OpenRepositoryAsync(f.Repository.Path);
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); string before = await f.Witness();
        MenuItem command = Flatten(main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>()).Single(item => item.Name == "BrowseShowReflog");
        Assert.That(((AccessText)command.Header!).Text, Is.EqualTo("Show reflo_g…")); Assert.That(((Image)command.Icon!).Tag, Is.EqualTo("Book.png"));
        Invoke(command); ReflogDialog dialog = await Child<ReflogDialog>(main); Assert.That(dialog.Owner, Is.SameAs(main)); Assert.That(dialog.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
        Assert.That(dialog.Entries.Any(entry => entry.Hash == f.Lost), Is.True); Assert.That(dialog.HasRepositoryChanges, Is.False); dialog.Close(); await Until(() => !main.IsBusy);
        MenuItem maintenance = Flatten(main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>()).Single(item => item.Header as string == "Maintenance…"); Invoke(maintenance);
        await Until(() => main.OwnedWindows.Any(window => window.IsVisible)); Window tools = main.OwnedWindows.Single(window => window.IsVisible); Pump(tools);
        await Pointer(Field<Button>(tools, "RepositoryShowReflog")); dialog = await Child<ReflogDialog>(tools);
        Assert.That(dialog.Owner, Is.SameAs(tools)); Assert.That(dialog.Entries.Any(entry => entry.Hash == f.Lost), Is.True); Assert.That(dialog.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
        dialog.Close(); await Until(() => !main.IsBusy); tools.Close(); Assert.That(await f.Witness(), Is.EqualTo(before));
    }

    private static T Field<T>(Control host, string name) where T : Control => host.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static MenuItem Context(ReflogDialog dialog, string name) => Field<ListBox>(dialog, "ReflogEntries").ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static IEnumerable<MenuItem> Flatten(IEnumerable<MenuItem> items) => items.SelectMany(item => new[] { item }.Concat(Flatten(item.Items.OfType<MenuItem>())));
    private static void Invoke(MenuItem item) { Assert.That(item.IsEnabled, Is.True, item.Name); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
    private static void SelectLost(ReflogDialog dialog, Fixture f) => Field<ListBox>(dialog, "ReflogEntries").SelectedItem = dialog.Entries.First(entry => entry.Hash == f.Lost && entry.Description.StartsWith("commit:", StringComparison.Ordinal));
    private static async Task SelectReference(ReflogDialog dialog, string name)
    {
        ComboBox combo = Field<ComboBox>(dialog, "ReflogReference"); combo.SelectedItem = combo.Items.Cast<object>().Single(item => item.ToString() == name);
        await Until(() => !dialog.IsBusy); Pump(dialog);
    }
    private static async Task<T> Child<T>(Window parent) where T : RepositoryDialog
    { await Until(() => parent.OwnedWindows.OfType<T>().Any(child => child.IsVisible && !child.IsBusy)); T child = parent.OwnedWindows.OfType<T>().Single(child => child.IsVisible); Pump(child); Assert.That(child.LastError, Is.Null, child.LastException?.ToString()); return child; }
    private static async Task<Window> Warning(Window parent, string name)
    { await Until(() => parent.OwnedWindows.Any(child => child.IsVisible && child.Name == name)); Window warning = parent.OwnedWindows.Single(child => child.IsVisible && child.Name == name); Pump(warning); return warning; }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static Rect Bounds(TopLevel window, Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void Press(TopLevel window, PhysicalKey key, TopLevel releaseOn)
    { window.KeyPressQwerty(key, RawInputModifiers.None); releaseOn.KeyReleaseQwerty(key, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); }
    private static async Task Pointer(Control control, Point? offset = null)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; Stopwatch timer = Stopwatch.StartNew(); bool ready = false; Point point = default; string hit = "";
        do
        {
            Pump(top); point = control.TranslatePoint(offset ?? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value; top.MouseMove(point);
            Visual? target = top.InputHitTest(point) as Visual; hit = string.Join(" > ", target?.GetSelfAndVisualAncestors().Select(visual => visual.GetType().Name + "#" + (visual as Control)?.Name) ?? []);
            ready = control.IsEffectivelyEnabled && control.IsEffectivelyVisible && new Rect(default, top.ClientSize).Contains(point) && target?.GetSelfAndVisualAncestors().Contains(control) == true;
            if (ready) { break; } await Task.Delay(10);
        } while (timer.Elapsed < TimeSpan.FromSeconds(8));
        Assert.That(ready, Is.True, control.Name + " must receive the real pointer; hit=" + hit);
        top.MouseDown(point, MouseButton.Left); top.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static async Task Until(Func<bool> condition)
    { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True, "Reflog UI did not settle."); }
    private static void Capture(TopLevel window, string name)
    { Pump(window); using Bitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }
    private static Color Pixel(TopLevel window, Point point)
    {
        Pump(window); using Bitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        using WriteableBitmap copy = new(frame!.PixelSize, frame.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul); using ILockedFramebuffer buffer = copy.Lock(); frame.CopyPixels(buffer, AlphaFormat.Unpremul);
        byte[] value = new byte[4]; Marshal.Copy(buffer.Address + (int)point.Y * buffer.RowBytes + (int)point.X * 4, value, 0, 4); return Color.FromArgb(value[3], value[2], value[1], value[0]);
    }
    private static double Contrast(Color a, Color b)
    {
        static double L(Color color) { static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); } return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B); }
        double first = L(a), second = L(b); return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Dictionary<string, string?> _environment = [];
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly GitRunner _runner = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-reflog-ui-" + Guid.NewGuid().ToString("N"));
        public GitRepository Repository => new(Path.Combine(Root, "repo"));
        public Window Owner { get; } = new() { Width = 1100, Height = 700 };
        public List<Window> Windows { get; } = [];
        public string Initial = "", Lost = "", Current = "";
        private Fixture()
        {
            foreach ((string name, string value) in new[] { ("XDG_CONFIG_HOME", Path.Combine(Root, "config")), ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_OPTIONAL_LOCKS", "0") })
            { _environment[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en"); Directory.CreateDirectory(Repository.Path);
        }
        public string File(string path) => Path.Combine(Repository.Path, path);
        public void Write(string path, string text) => System.IO.File.WriteAllText(File(path), text);
        public async Task<GitResult> Git(params string[] args)
        { GitResult result = await _runner.RunAsync(Repository.Path, args); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public async Task<string> Text(params string[] args) => (await Git(args)).StandardOutput.TrimEnd('\r', '\n');
        public Task<string> Head() => Text("rev-parse", "HEAD");
        private async Task<string> Commit(string path, string text, string message) { Write(path, text); await Git("add", "--", path); await Git("commit", "-m", message); return await Head(); }
        public static async Task<Fixture> Create(bool unborn = false)
        {
            Fixture f = new();
            try
            {
                await f.Git("init", "-b", "main"); await f.Git("config", "user.name", "Reflog UI"); await f.Git("config", "user.email", "reflog@example.invalid");
                await f.Git("config", "commit.gpgsign", "false"); await f.Git("config", "core.hooksPath", "/dev/null");
                if (!unborn)
                {
                    f.Initial = await f.Commit("seed.txt", "seed\n", "Initial"); f.Lost = await f.Commit("lost.txt", "lost\n", "Café perdu €");
                    await f.Git("reset", "--hard", f.Initial); f.Current = await f.Commit("current.txt", "current\n", "Current"); await f.Git("branch", "side", f.Initial);
                    await f.Git("update-ref", "--create-reflog", "-m", "remote initial", "refs/remotes/origin/main", f.Initial);
                    await f.Git("update-ref", "-m", "remote current", "refs/remotes/origin/main", f.Current); await f.Git("tag", "excluded-tag", f.Current);
                }
                f.Owner.Show(); Pump(f.Owner); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async Task Dirty() { Write("seed.txt", "staged\n"); await Git("add", "seed.txt"); Write("seed.txt", "worktree\n"); Write("untracked.txt", "keep\n"); }
        public async Task<ReflogDialog> Show(bool dark = false)
        {
            ReflogDialog dialog = new(Repository) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; Windows.Add(dialog);
            await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); _ = dialog.ShowDialog(Owner); Pump(dialog); return dialog;
        }
        public string Worktree() => JsonSerializer.Serialize(Directory.EnumerateFiles(Repository.Path, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(File(".git") + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .ToDictionary(path => Path.GetRelativePath(Repository.Path, path), path => Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path)))));
        public async Task<string> Witness()
        {
            string refs = await Text("for-each-ref", "--format=%(refname) %(objectname)");
            Dictionary<string, string> logs = Directory.Exists(File(".git/logs")) ? Directory.EnumerateFiles(File(".git/logs"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .ToDictionary(path => Path.GetRelativePath(Repository.Path, path), path => Convert.ToBase64String(System.IO.File.ReadAllBytes(path))) : [];
            return JsonSerializer.Serialize(new { refs, files = Worktree(), logs, head = Convert.ToBase64String(System.IO.File.ReadAllBytes(File(".git/HEAD"))),
                index = System.IO.File.Exists(File(".git/index")) ? Convert.ToBase64String(System.IO.File.ReadAllBytes(File(".git/index"))) : null,
                config = Convert.ToBase64String(System.IO.File.ReadAllBytes(File(".git/config"))) });
        }
        public async ValueTask DisposeAsync()
        {
            static void CloseChildren(Window window) { foreach (Window child in window.OwnedWindows.ToArray()) { CloseChildren(child); child.Close(); } }
            foreach (Window window in Windows.AsEnumerable().Reverse()) { CloseChildren(window); window.Close(); }
            CloseChildren(Owner); Owner.Close(); Stopwatch timer = Stopwatch.StartNew();
            while (Windows.OfType<RepositoryDialog>().Any(dialog => dialog.IsBusy) && timer.Elapsed < TimeSpan.FromSeconds(8)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            foreach (Window window in Windows) { window.Close(); } Dispatcher.UIThread.RunJobs();
            foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); } CultureInfo.CurrentUICulture = _culture;
            if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
        }
    }
}

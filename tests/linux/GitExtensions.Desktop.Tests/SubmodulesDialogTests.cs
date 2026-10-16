using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using RootFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class SubmodulesDialogTests
{
    [AvaloniaTest]
    public async Task OriginalCompactGridDetailsAndActionsStayUsableAtMinimumAndExpandedSizesInBothThemes()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.AddWitnessAsync();
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            SubmoduleRemovalDialog dialog = await f.ShowAsync(theme: theme);
            try
            {
                Assert.That(dialog.ClientSize, Is.EqualTo(new Size(782, 372)));
                Assert.That(new Size(dialog.MinWidth, dialog.MinHeight), Is.EqualTo(new Size(710, 350)));
                Assert.That(Field<Grid>(dialog, "SubmodulesHeaders").Bounds.Height, Is.EqualTo(24));
                Assert.That(Field<Grid>(dialog, "SubmodulesLayout").ColumnDefinitions[0].ActualWidth, Is.EqualTo(222).Within(1));
                Assert.That(Field<GridSplitter>(dialog, "SubmodulesSplitter").Bounds.Width, Is.EqualTo(5));
                Assert.That(Field<ListBox>(dialog, "RemovalSubmodules").SelectionMode, Is.EqualTo(SelectionMode.Single));
                Assert.That(dialog.GetVisualDescendants().OfType<Button>().Any(button => button.Name is "CloseSubmoduleRemoval" or "RefreshSubmodules"), Is.False);
                Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleName").Text, Is.EqualTo("selected"));
                Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleRemotePath").Text, Is.EqualTo(f.Source.Path));
                Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleLocalPath").Text, Is.EqualTo(Fixture.SelectedPath));
                Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleCommit").Text, Is.EqualTo(f.Initial));
                Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleStatus").Text, Is.EqualTo("Up-to-date"));
                foreach (TextBox detail in dialog.GetVisualDescendants().OfType<TextBox>().Where(box => box.Name?.StartsWith("RemovalSubmodule", StringComparison.Ordinal) == true))
                { Assert.That(detail.IsReadOnly, Is.True, detail.Name); }
                foreach (ListBoxItem row in Field<ListBox>(dialog, "RemovalSubmodules").GetVisualDescendants().OfType<ListBoxItem>())
                { Assert.That(row.Bounds.Height, Is.EqualTo(22)); }
                Assert.That(Field<Button>(dialog, "UpdateSubmodule").GetVisualDescendants().OfType<Image>(), Is.Not.Empty);
                Assert.That(Field<Button>(dialog, "SynchronizeSubmodule").GetVisualDescendants().OfType<Image>(), Is.Not.Empty);
                Assert.That(Field<Button>(dialog, "PullSubmodule").Bounds.Size, Is.EqualTo(new Size(24, 24)));
                Assert.That(Field<Button>(dialog, "AddSubmodule").Bounds.Size, Is.EqualTo(new Size(202, 27)));
                foreach ((double width, double height, string label) in new[] { (782d, 372d, "original"), (710d, 350d, "minimum"), (1100d, 500d, "expanded") })
                {
                    dialog.Width = width; dialog.Height = height; Pump(dialog);
                    foreach (string name in new[] { "RemovalSubmodules", "RemovalSubmoduleRemotePath", "RemovalSubmoduleLocalPath", "RemovalSubmoduleCommit", "PullSubmodule", "AddSubmodule", "SynchronizeSubmodule", "UpdateSubmodule", "RemoveSubmodule" })
                    { AssertInside(dialog, Field<Control>(dialog, name)); }
                    Rect sync = Bounds(dialog, Field<Button>(dialog, "SynchronizeSubmodule")), update = Bounds(dialog, Field<Button>(dialog, "UpdateSubmodule")), remove = Bounds(dialog, Field<Button>(dialog, "RemoveSubmodule"));
                    Assert.That(sync.Right, Is.LessThanOrEqualTo(update.Left)); Assert.That(update.Right, Is.LessThanOrEqualTo(remove.Left));
                    Rect branch = Bounds(dialog, Field<TextBox>(dialog, "RemovalSubmoduleBranch")), pull = Bounds(dialog, Field<Button>(dialog, "PullSubmodule"));
                    Assert.That(pull.Left, Is.GreaterThanOrEqualTo(branch.Right)); Assert.That(pull.Center.Y, Is.EqualTo(branch.Center.Y).Within(1));
                    Capture(dialog, "submodules-manager-" + theme + "-" + label + ".png");
                }
            }
            finally { dialog.Close(); }
        }
    }

    [AvaloniaTest]
    public async Task ActualSplitterDragPersistsTheAbsoluteLeftPanelWidthAcrossResizeAndReopening()
    {
        await using Fixture f = await Fixture.CreateAsync(); string preferences = f.Root.Output("isolated-splitter.json");
        SubmoduleRemovalDialog dialog = await f.ShowAsync(preferences: preferences);
        Grid layout = Field<Grid>(dialog, "SubmodulesLayout"); GridSplitter splitter = Field<GridSplitter>(dialog, "SubmodulesSplitter");
        Point start = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), dialog)!.Value;
        dialog.MouseMove(start); dialog.MouseDown(start, MouseButton.Left); dialog.MouseMove(start + new Vector(55, 0)); dialog.MouseUp(start + new Vector(55, 0), MouseButton.Left); Pump(dialog);
        double changed = layout.ColumnDefinitions[0].ActualWidth; Assert.That(changed, Is.GreaterThan(240));
        dialog.Width = 1000; Pump(dialog); Assert.That(layout.ColumnDefinitions[0].ActualWidth, Is.EqualTo(changed).Within(1));
        dialog.Close(); Assert.That(File.Exists(preferences), Is.True);
        SubmoduleRemovalDialog reopened = await f.ShowAsync(preferences: preferences);
        Assert.That(Field<Grid>(reopened, "SubmodulesLayout").ColumnDefinitions[0].ActualWidth, Is.EqualTo(changed).Within(1));
        reopened.Close(); await File.WriteAllTextAsync(preferences, "{invalid json");
        SubmoduleRemovalDialog recovered = await f.ShowAsync(preferences: preferences);
        Assert.That(Field<Grid>(recovered, "SubmodulesLayout").ColumnDefinitions[0].ActualWidth, Is.EqualTo(222).Within(1));
    }

    [AvaloniaTest]
    public async Task NativeColumnDraggingAndHorizontalScrollKeepCellsAlignedAndCtrlCCopiesExactNameAndStatus()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.AddWitnessAsync(); SubmoduleRemovalDialog dialog = await f.ShowAsync();
        var clipboard = dialog.Clipboard!; string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard);
        try
        {
            Grid header = Field<Grid>(dialog, "SubmodulesHeaders"); Thumb nameGrip = Field<Thumb>(dialog, "ResizeSubmoduleName");
            double before = header.ColumnDefinitions[0].ActualWidth; Drag(dialog, nameGrip, new Vector(70, 0));
            Assert.That(header.ColumnDefinitions[0].ActualWidth, Is.GreaterThan(before + 20));
            ScrollViewer scroll = Field<ScrollViewer>(dialog, "SubmodulesTableScroll");
            Thumb statusGrip = Field<Thumb>(dialog, "ResizeSubmoduleStatus"); double statusWidth = header.ColumnDefinitions[1].ActualWidth;
            Drag(dialog, statusGrip, new Vector(170, 0)); Assert.That(header.ColumnDefinitions[1].ActualWidth, Is.GreaterThan(statusWidth));
            scroll.Offset = new Vector(Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width), 0); Pump(dialog);
            Assert.That(scroll.Offset.X, Is.GreaterThan(0), "The Fill name column must retain a usable minimum when a resized Status column requires horizontal scrolling.");
            foreach (double width in new[] { 710d, 1000d })
            {
                dialog.Width = width; Pump(dialog); scroll.Offset = new Vector(Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width), 0); Pump(dialog);
                TextBlock name = Field<ListBox>(dialog, "RemovalSubmodules").GetVisualDescendants().OfType<TextBlock>().Single(cell => cell.Name == "RemovalNameCell" && cell.Text == "selected");
                TextBlock status = name.GetVisualAncestors().OfType<Grid>().First().GetVisualDescendants().OfType<TextBlock>().Single(cell => cell.Name == "RemovalStatusCell");
                Assert.That(Bounds(dialog, Field<TextBlock>(dialog, "RemovalNameHeader")).X, Is.EqualTo(Bounds(dialog, name).X).Within(2));
                Assert.That(Bounds(dialog, Field<TextBlock>(dialog, "RemovalStatusHeader")).X, Is.EqualTo(Bounds(dialog, status).X).Within(2));
                AssertInside(dialog, scroll);
            }
            ListBox list = Field<ListBox>(dialog, "RemovalSubmodules"); GitSubmoduleSnapshot selected = Selected(dialog); list.Focus();
            dialog.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control); dialog.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.Control);
            string expected = selected.Name + "\t" + selected.Status; string? copied = null; Stopwatch watch = Stopwatch.StartNew();
            while (copied != expected && watch.Elapsed < TimeSpan.FromSeconds(5))
            { Dispatcher.UIThread.RunJobs(); copied = await ClipboardExtensions.TryGetTextAsync(clipboard); if (copied != expected) { await Task.Delay(10); } }
            Assert.That(copied, Is.EqualTo(expected)); Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""); }
    }

    [AvaloniaTest]
    public async Task CopyingTheOnlyRowIncludesTranslatedHeadersAndPreservesLiteralQuotesAndCarriageReturns()
    {
        await using Fixture f = await Fixture.CreateAsync(); CultureInfo previousCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            SubmoduleRemovalDialog dialog = await f.ShowAsync(); var clipboard = dialog.Clipboard!; string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard);
            try
            {
                async Task AssertCopyAsync(string expected)
                {
                    Field<ListBox>(dialog, "RemovalSubmodules").Focus(); dialog.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control); dialog.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.Control);
                    string? copied = null; Stopwatch watch = Stopwatch.StartNew();
                    while (copied != expected && watch.Elapsed < TimeSpan.FromSeconds(5))
                    { Dispatcher.UIThread.RunJobs(); copied = await ClipboardExtensions.TryGetTextAsync(clipboard); if (copied != expected) { await Task.Delay(10); } }
                    Assert.That(copied, Is.EqualTo(expected));
                }
                Assert.That(Field<TextBlock>(dialog, "RemovalNameHeader").Text, Is.EqualTo("Nom"));
                Assert.That(Field<TextBlock>(dialog, "RemovalStatusHeader").Text, Is.EqualTo("Statut"));
                await AssertCopyAsync("Nom\tStatut\r\nselected\tUp-to-date");
                const string literalName = "selected\t\"λ\"\rvalue";
                await f.Root.GitAsync("config", "--file", ".gitmodules", "--rename-section", "submodule.selected", "submodule." + literalName);
                await f.Root.GitAsync("config", "--local", "--rename-section", "submodule.selected", "submodule." + literalName);
                await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
                ListBox list = Field<ListBox>(dialog, "RemovalSubmodules"); list.SelectedItem = list.Items.OfType<GitSubmoduleSnapshot>().Single(); Pump(dialog);
                Assert.That(Selected(dialog).Name, Is.EqualTo(literalName));
                await AssertCopyAsync("Nom\tStatut\r\nselected \"λ\"\rvalue\tUp-to-date");
                Assert.That(dialog.HasRepositoryChanges, Is.False);
            }
            finally { await ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""); }
        }
        finally { CultureInfo.CurrentUICulture = previousCulture; }
    }

    [AvaloniaTest]
    public async Task UpdateInitializesOnlyTheSelectedModuleRecursivelyAndCallsTheSuperprojectHookAfterAcknowledgment()
    {
        await using Fixture f = await Fixture.CreateAsync(nested: true); await f.AddWitnessAsync();
        await f.Root.GitAsync("--literal-pathspecs", "submodule", "deinit", "-f", "--", Fixture.SelectedPath, Fixture.WitnessPath);
        int hooks = 0; Window? hookOwner = null; GitRepository? hookRepository = null;
        SubmoduleRemovalDialog dialog = await f.ShowAsync(afterUpdate: (owner, repository, _) => { hooks++; hookOwner = owner; hookRepository = repository; return Task.CompletedTask; });
        Assert.That(Selected(dialog).IsInitialized, Is.False); Assert.That(Field<Button>(dialog, "PullSubmodule").IsEnabled, Is.False);
        Pointer(dialog, "UpdateSubmodule"); GitProcessDialog process = await FinishedProcessAsync(dialog);
        Assert.That(process.Owner, Is.SameAs(dialog)); Assert.That(process.Result?.Success, Is.True, process.Error?.ToString());
        Assert.That(dialog.IsBusy, Is.True); Assert.That(hooks, Is.Zero);
        Assert.That(await f.Module.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial));
        Assert.That(await new GitRepository(Path.Combine(f.Module.Path, Fixture.NestedPath)).ResolveRevisionAsync("HEAD"), Is.EqualTo(f.NestedHead));
        Assert.That(File.Exists(Path.Combine(f.Root.PathInRepository(Fixture.WitnessPath), ".git")), Is.False);
        Pointer(process, "Ok"); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Assert.That(hooks, Is.EqualTo(1));
        Assert.That(hookOwner, Is.SameAs(dialog)); Assert.That(hookRepository, Is.SameAs(f.Root.Repository));
        Assert.That(Selected(dialog).Path, Is.EqualTo(Fixture.SelectedPath)); Assert.That(Selected(dialog).IsUpToDate, Is.True);
        Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(Field<ListBox>(dialog, "RemovalSubmodules").IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    public async Task SynchronizeChangesOnlyTheSelectedModuleUrlAndDoesNotRecurseIntoItsChildren()
    {
        await using Fixture f = await Fixture.CreateAsync(nested: true); await f.AddWitnessAsync();
        await f.Root.GitAsync("--literal-pathspecs", "submodule", "update", "--init", "--recursive", "--", Fixture.SelectedPath);
        string newSource = f.Root.Output("replacement source.git"); await f.Root.GitAsync("clone", "--bare", "--", f.Source.Path, newSource);
        await f.Root.GitAsync("config", "--file", ".gitmodules", "submodule.selected.url", newSource);
        await f.Root.Repository.SetConfigAsync("submodule.witness.url", "witness-kept");
        await f.Module.SetConfigAsync("submodule.nested.url", "nested-kept");
        GitRepository nested = new(Path.Combine(f.Module.Path, Fixture.NestedPath)); await nested.SetConfigAsync("remote.origin.url", "nested-origin-kept");
        int hooks = 0; SubmoduleRemovalDialog dialog = await f.ShowAsync(afterUpdate: (_, _, _) => { hooks++; return Task.CompletedTask; });
        Pointer(dialog, "SynchronizeSubmodule"); GitProcessDialog process = await FinishedProcessAsync(dialog);
        Assert.That(process.Result?.Success, Is.True, process.Error?.ToString()); Pointer(process, "Ok"); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Assert.That(hooks, Is.Zero);
        Assert.That(await TextAsync(f.Root.Repository, "config", "--get", "submodule.selected.url"), Is.EqualTo(newSource));
        Assert.That(await TextAsync(f.Module, "config", "--get", "remote.origin.url"), Is.EqualTo(newSource));
        Assert.That(await TextAsync(f.Root.Repository, "config", "--get", "submodule.witness.url"), Is.EqualTo("witness-kept"));
        Assert.That(await TextAsync(f.Module, "config", "--get", "submodule.nested.url"), Is.EqualTo("nested-kept"));
        Assert.That(await TextAsync(nested, "config", "--get", "remote.origin.url"), Is.EqualTo("nested-origin-kept"));
        Assert.That(Selected(dialog).Path, Is.EqualTo(Fixture.SelectedPath)); Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleRemotePath").Text, Is.EqualTo(newSource));
        Assert.That(dialog.HasRepositoryChanges, Is.True);
    }

    [AvaloniaTest]
    public async Task PullUsesTheSelectedModuleRepositoryAndRefreshesItsModifiedStatusWithoutChangingTheSuperprojectIndex()
    {
        await using Fixture f = await Fixture.CreateAsync(nested: true); await f.AddWitnessAsync(); string superHead = await f.Root.Repository.ResolveRevisionAsync("HEAD");
        string next = await f.AdvanceSourceAsync("incoming module bytes\n"); new PullPreferencesStore().Save(new(DialogAction: GitPullAction.Merge, AutoStash: false));
        new SubmoduleCheckoutPreferencesStore().Remember(true);
        int hooks = 0; Window? hookOwner = null; GitRepository? hookRepository = null;
        SubmoduleRemovalDialog dialog = await f.ShowAsync(theme: ThemeVariant.Dark, afterUpdate: (owner, repository, _) =>
        { hooks++; hookOwner = owner; hookRepository = repository; return Task.CompletedTask; }); Pointer(dialog, "PullSubmodule");
        PullDialog pull = await OwnedAsync<PullDialog>(dialog); Assert.That(pull.Owner, Is.SameAs(dialog)); Assert.That(pull.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
        Assert.That(Field<TextBox>(pull, "PullLocalBranch").Text, Is.EqualTo("main")); Assert.That(Field<ComboBox>(pull, "PullRemote").Text, Is.EqualTo("origin"));
        Field<ComboBox>(pull, "PullBranch").Text = "main"; Pointer(pull, "ExecutePull"); GitProcessDialog process = await FinishedProcessAsync(pull);
        Assert.That(process.Result?.Success, Is.True, process.Error?.ToString()); Assert.That(process.Owner, Is.SameAs(pull)); Assert.That(hooks, Is.Zero); Pointer(process, "Ok");
        await Until(() => !pull.IsVisible && !dialog.IsBusy);
        Assert.That(pull.Succeeded, Is.True, pull.LastError); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
        Assert.That(await f.Module.ResolveRevisionAsync("HEAD"), Is.EqualTo(next)); Assert.That(await f.Root.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(superHead));
        Assert.That(Selected(dialog).IndexHead, Is.EqualTo(f.Initial)); Assert.That(Selected(dialog).Commit, Is.EqualTo(next));
        Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleStatus").Text, Is.EqualTo("Modified"));
        Assert.That(await new GitRepository(f.Root.PathInRepository(Fixture.WitnessPath)).ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial));
        Assert.That(hooks, Is.EqualTo(1)); Assert.That(hookOwner, Is.SameAs(pull)); Assert.That(hookRepository?.Path, Is.EqualTo(f.Module.Path));
        Assert.That(await new GitRepository(Path.Combine(f.Module.Path, Fixture.NestedPath)).ResolveRevisionAsync("HEAD"), Is.EqualTo(f.NestedHead));
        Assert.That(dialog.HasRepositoryChanges && dialog.IsVisible, Is.True);
    }

    [AvaloniaTest]
    public async Task ClosingPullWithoutExecutingStillReloadsExternalModuleChangesAndPreservesTheSelectedRow()
    {
        await using Fixture f = await Fixture.CreateAsync(); SubmoduleRemovalDialog dialog = await f.ShowAsync(); Pointer(dialog, "PullSubmodule");
        PullDialog pull = await OwnedAsync<PullDialog>(dialog); string next = await f.AdvanceSourceAsync("external module update\n");
        await Fixture.GitAsync(f.Module, "fetch", "origin"); await Fixture.GitAsync(f.Module, "checkout", "--detach", next);
        pull.Close(); await Idle(dialog);
        Assert.That(Selected(dialog).Path, Is.EqualTo(Fixture.SelectedPath)); Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleCommit").Text, Is.EqualTo(next));
        Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleStatus").Text, Is.EqualTo("Modified")); Assert.That(dialog.LastError, Is.Null);
        Assert.That(dialog.HasRepositoryChanges, Is.False, "An external change is refreshed without claiming that this untouched Pull command ran.");
    }

    [AvaloniaTest]
    public async Task DirtyUpdateFailureKeepsTheFileAndSelectionAndCanRetryAfterAnExplicitUserRepair()
    {
        await using Fixture f = await Fixture.CreateAsync(); string next = await f.AdvanceSourceAsync("remote replacement\n");
        await f.Root.GitAsync("update-index", "--cacheinfo", "160000," + next + "," + Fixture.SelectedPath);
        string file = Path.Combine(f.Module.Path, "file.txt"); await File.WriteAllTextAsync(file, "unsaved local work λ\n");
        int hooks = 0; SubmoduleRemovalDialog dialog = await f.ShowAsync(afterUpdate: (_, _, _) => { hooks++; return Task.CompletedTask; });
        Pointer(dialog, "UpdateSubmodule"); GitProcessDialog failed = await FinishedProcessAsync(dialog);
        Assert.That(failed.Result?.Success, Is.False); Pointer(failed, "Ok"); await Idle(dialog);
        Assert.That(dialog.LastException, Is.TypeOf<GitCommandException>()); Assert.That(hooks, Is.Zero);
        Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("unsaved local work λ\n")); Assert.That(await f.Module.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial));
        Assert.That(Selected(dialog).IndexHead, Is.EqualTo(next)); Assert.That(Selected(dialog).Path, Is.EqualTo(Fixture.SelectedPath));
        Assert.That(Field<Button>(dialog, "UpdateSubmodule").IsEnabled, Is.True); Assert.That(Field<ListBox>(dialog, "RemovalSubmodules").IsKeyboardFocusWithin, Is.True);
        await Fixture.GitAsync(f.Module, "restore", "--", "file.txt"); Pointer(dialog, "UpdateSubmodule"); GitProcessDialog repaired = await FinishedProcessAsync(dialog);
        Assert.That(repaired.Result?.Success, Is.True, repaired.Error?.ToString()); Pointer(repaired, "Ok"); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Assert.That(hooks, Is.EqualTo(1));
        Assert.That(await f.Module.ResolveRevisionAsync("HEAD"), Is.EqualTo(next)); Assert.That(Selected(dialog).IsUpToDate, Is.True);
    }

    [AvaloniaTest]
    public async Task AnUnmergedGitlinkRemainsVisibleWithExplicitConflictAndCannotBeRemovedOrGivenAFabricatedCommit()
    {
        await using Fixture f = await Fixture.CreateAsync(); string second = await f.AdvanceSourceAsync("second\n"), third = await f.AdvanceSourceAsync("third\n");
        string entries = "0 " + new string('0', 40) + "\t" + Fixture.SelectedPath + "\0"
            + "160000 " + f.Initial + " 1\t" + Fixture.SelectedPath + "\0"
            + "160000 " + second + " 2\t" + Fixture.SelectedPath + "\0"
            + "160000 " + third + " 3\t" + Fixture.SelectedPath + "\0";
        GitResult staged = await new GitRunner().RunAsync(f.Root.Repository.Path, ["update-index", "-z", "--index-info"], standardInput: entries); Assert.That(staged.Success, Is.True, staged.StandardError);
        SubmoduleRemovalDialog dialog = await f.ShowAsync(); GitSubmoduleSnapshot row = Selected(dialog);
        Assert.That(row.IsConflicted, Is.True); Assert.That(row.IsUpToDate, Is.False); Assert.That(row.Status, Is.EqualTo("Conflicted"));
        Assert.That(row.IndexHead, Is.Null); Assert.That(row.Commit, Is.Null); Assert.That(row.RemovalSelection, Is.Null);
        Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleCommit").Text, Is.Null.Or.Empty); Assert.That(Field<Button>(dialog, "RemoveSubmodule").IsEnabled, Is.False);
        Assert.That(Field<Button>(dialog, "UpdateSubmodule").IsEnabled, Is.True); Assert.That(Field<Button>(dialog, "SynchronizeSubmodule").IsEnabled, Is.True);
        string before = (await f.Root.GitAsync("ls-files", "--stage", "-z")).StandardOutput; byte[] contents = await File.ReadAllBytesAsync(Path.Combine(f.Module.Path, "file.txt"));
        Pointer(dialog, "UpdateSubmodule"); GitProcessDialog process = await FinishedProcessAsync(dialog);
        // Native Git decides whether an unmerged module is an error or a skipped update.
        Assert.That(process.Result, Is.Not.Null); Pointer(process, "Ok"); await Idle(dialog);
        Assert.That((await f.Root.GitAsync("ls-files", "--stage", "-z")).StandardOutput, Is.EqualTo(before));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(f.Module.Path, "file.txt")), Is.EqualTo(contents));
        Assert.That(Selected(dialog).IsConflicted, Is.True); Assert.That(Field<Button>(dialog, "RemoveSubmodule").IsEnabled, Is.False);
        Capture(dialog, "submodules-manager-conflicted.png");
    }

    [AvaloniaTest]
    public async Task ProcessCloseAfterSuccessfulUpdateRefreshesTheActualStateWithoutRunningTheSuccessHook()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.Root.GitAsync("--literal-pathspecs", "submodule", "deinit", "-f", "--", Fixture.SelectedPath);
        int hooks = 0; SubmoduleRemovalDialog dialog = await f.ShowAsync(afterUpdate: (_, _, _) => { hooks++; return Task.CompletedTask; });
        Pointer(dialog, "UpdateSubmodule"); GitProcessDialog process = await FinishedProcessAsync(dialog); Assert.That(process.Result?.Success, Is.True, process.Error?.ToString());
        process.Close(); await Idle(dialog);
        Assert.That(hooks, Is.Zero); Assert.That(dialog.LastProcess?.WorkflowStopRequested, Is.True); Assert.That(dialog.LastProcess?.Success, Is.True);
        Assert.That(Selected(dialog).IsInitialized, Is.True); Assert.That(Selected(dialog).IsUpToDate, Is.True); Assert.That(dialog.HasRepositoryChanges, Is.True);
        Assert.That(Field<TextBlock>(dialog, "OperationStatus").Text, Is.Null.Or.Empty);
    }

    [AvaloniaTest]
    public async Task AbortAndOwnerCloseDrainTheOwnedNativeUpdateActorsWithoutCallingTheSuccessHook()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private native actor identities require Linux procfs."); }
        foreach (bool ownerClose in new[] { false, true })
        {
            await using Fixture f = await Fixture.CreateAsync(); string wrapper = await f.BlockUpdateAsync();
            Window owner = new() { Width = 1000, Height = 600 }; f.Windows.Add(owner); owner.Show();
            int hooks = 0; SubmoduleRemovalDialog dialog = await f.ShowAsync(repository: new(f.Root.Repository.Path, new GitRunner(wrapper)), owner: owner,
                afterUpdate: (_, _, _) => { hooks++; return Task.CompletedTask; });
            string before = (await f.Root.GitAsync("ls-files", "--stage", "-z")).StandardOutput; Pointer(dialog, "UpdateSubmodule");
            GitProcessDialog process = await ProcessAsync(dialog); await Until(() => File.Exists(wrapper + ".actors") && process.IsRunning);
            Actor[] actors = File.ReadAllText(wrapper + ".actors").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(value => Actor.Read(int.Parse(value, CultureInfo.InvariantCulture))).ToArray();
            f.Actors.AddRange(actors); Assert.That(actors.All(actor => actor.Alive()), Is.True);
            if (ownerClose) { owner.Close(); } else { Pointer(process, "Abort"); }
            await Until(() => !dialog.IsBusy && !process.IsRunning && actors.All(actor => !actor.Alive()) && (!ownerClose || !dialog.IsVisible));
            Assert.That(dialog.LastProcess?.WasCancelled, Is.True); Assert.That(dialog.LastProcess?.WorkflowStopRequested, Is.True); Assert.That(hooks, Is.Zero);
            Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That((await f.Root.GitAsync("ls-files", "--stage", "-z")).StandardOutput, Is.EqualTo(before));
            Assert.That(await f.Module.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial));
            if (!ownerClose) { Assert.That(dialog.IsVisible, Is.True); Assert.That(Selected(dialog).Path, Is.EqualTo(Fixture.SelectedPath)); Assert.That(Field<Button>(dialog, "UpdateSubmodule").IsEnabled, Is.True); }
        }
    }

    [AvaloniaTest]
    public async Task ClosingDuringThePostProcessReloadCancelsItsNativeStatusReadAndRetainsTheCompletedUpdate()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private native actor identities require Linux procfs."); }
        await using Fixture f = await Fixture.CreateAsync(); await f.Root.GitAsync("--literal-pathspecs", "submodule", "deinit", "-f", "--", Fixture.SelectedPath);
        string wrapper = await f.BlockPostRefreshAsync(); SubmoduleRemovalDialog dialog = await f.ShowAsync(repository: new(f.Root.Repository.Path, new GitRunner(wrapper)));
        Pointer(dialog, "UpdateSubmodule"); GitProcessDialog process = await FinishedProcessAsync(dialog);
        Assert.That(process.Result?.Success, Is.True, process.Error?.ToString()); Pointer(process, "Ok");
        await Until(() => File.Exists(wrapper + ".actors"));
        Actor[] actors = File.ReadAllText(wrapper + ".actors").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(value => Actor.Read(int.Parse(value, CultureInfo.InvariantCulture))).ToArray();
        f.Actors.AddRange(actors); Assert.That(dialog.IsBusy, Is.True); Assert.That(actors.All(actor => actor.Alive()), Is.True);
        dialog.Close(); await Until(() => !dialog.IsVisible && !dialog.IsBusy && actors.All(actor => !actor.Alive()));
        Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(dialog.LastProcess?.Success, Is.True);
        Assert.That(await f.Module.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial));
        Assert.That((await f.Root.Repository.GetSubmoduleSnapshotsAsync()).Single().IsInitialized, Is.True);
    }

    private static GitSubmoduleSnapshot Selected(Window dialog) => (GitSubmoduleSnapshot)Field<ListBox>(dialog, "RemovalSubmodules").SelectedItem!;
    private static T Field<T>(TopLevel window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pointer(Window dialog, string name) => PushProcessTestDriver.Pointer(Field<Control>(dialog, name));
    private static void Drag(Window owner, Control control, Vector delta)
    { Pump(owner); Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), owner)!.Value; Assert.That(control.IsEffectivelyVisible && owner.InputHitTest(point) is Visual hit && hit.GetSelfAndVisualAncestors().Contains(control), Is.True, "The real table grip must receive pointer input."); owner.MouseMove(point); owner.MouseDown(point, MouseButton.Left); owner.MouseMove(point + delta); owner.MouseUp(point + delta, MouseButton.Left); Pump(owner); }
    private static void Pump(Window window) { for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } }
    private static async Task Until(Func<bool> condition)
    { Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(25)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True, "Submodule management did not settle."); }
    private static Task Idle(RepositoryDialog dialog) => Until(() => !dialog.IsBusy);
    private static async Task<T> OwnedAsync<T>(Window owner) where T : RepositoryDialog
    { await Until(() => PushProcessTestDriver.Windows(owner).OfType<T>().Any(window => window.IsVisible && !window.IsBusy)); T dialog = PushProcessTestDriver.Windows(owner).OfType<T>().Single(window => window.IsVisible); Pump(dialog); return dialog; }
    private static async Task<GitProcessDialog> ProcessAsync(Window owner)
    { await Until(() => PushProcessTestDriver.Windows(owner).OfType<GitProcessDialog>().Any(window => window.IsVisible)); return PushProcessTestDriver.Windows(owner).OfType<GitProcessDialog>().Single(window => window.IsVisible); }
    private static async Task<GitProcessDialog> FinishedProcessAsync(Window owner)
    { GitProcessDialog process = await ProcessAsync(owner); await Until(() => !process.IsRunning); Pump(process); return process; }
    private static async Task<string> TextAsync(GitRepository repository, params string[] args) => (await Fixture.GitAsync(repository, args)).StandardOutput.TrimEnd('\r', '\n');
    private static Rect Bounds(Window owner, Control control) => new(control.TranslatePoint(default, owner)!.Value, control.Bounds.Size);
    private static void AssertInside(Window owner, Control control)
    { Rect bounds = Bounds(owner, control); Assert.That(control.IsEffectivelyVisible, Is.True, control.Name); Assert.That(bounds.Width, Is.GreaterThan(0), control.Name); Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(-1), control.Name); Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(-1), control.Name); Assert.That(bounds.Right, Is.LessThanOrEqualTo(owner.ClientSize.Width + 1), control.Name); Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(owner.ClientSize.Height + 1), control.Name); }
    private static void Capture(Window window, string name)
    { Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string folder = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); frame!.Save(path); TestContext.AddTestAttachment(path); }

    private sealed record Actor(int Pid, string Start)
    {
        internal static Actor Read(int pid) => new(pid, Fields(pid)[19]);
        private static string[] Fields(int pid) { string text = File.ReadAllText("/proc/" + pid + "/stat"); return text[(text.LastIndexOf(')') + 2)..].Split(' '); }
        internal bool Alive() { try { string[] fields = Fields(Pid); return fields[0] != "Z" && fields[19] == Start; } catch (IOException) { return false; } }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string SelectedPath = "modules/selected [x]* λ\nline", WitnessPath = "modules/witness", NestedPath = "nested [x]* λ";
        internal RootFixture Root { get; }
        internal GitRepository Source { get; private set; } = null!;
        internal GitRepository Module => new(Root.PathInRepository(SelectedPath));
        internal string Initial { get; private set; } = "";
        internal string NestedHead { get; private set; } = "";
        internal List<Window> Windows { get; } = [];
        internal List<Actor> Actors { get; } = [];
        private Fixture(RootFixture root) => Root = root;
        internal static async Task<Fixture> CreateAsync(bool nested = false)
        {
            Fixture f = new(await RootFixture.CreateAsync());
            try
            {
                string global = f.Root.Output("private-submodules.gitconfig"); await File.WriteAllTextAsync(global, "[protocol \"file\"]\n\tallow = always\n");
                f.Root.SetEnvironment("GIT_CONFIG_GLOBAL", global); f.Root.SetEnvironment("GIT_ALLOW_PROTOCOL", "file"); f.Root.SetEnvironment("GIT_TERMINAL_PROMPT", "0");
                f.Source = await f.SeedAsync("source.git");
                if (nested)
                {
                    GitRepository leaf = await f.SeedAsync("nested-source.git"); f.NestedHead = await leaf.ResolveRevisionAsync("HEAD");
                    await GitAsync(f.Source, "submodule", "add", "--name", "nested", "--", leaf.Path, NestedPath); await f.Source.StageAllAsync(); await f.Source.CommitAsync("Nested module");
                }
                f.Initial = await f.Source.ResolveRevisionAsync("HEAD");
                await f.Root.GitAsync("submodule", "add", "--name", "selected", "--", f.Source.Path, SelectedPath); await f.Root.Repository.StageAllAsync(); await f.Root.Repository.CommitAsync("Selected module");
                new ProcessDialogPreferencesStore().Save(new(false)); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        private async Task<GitRepository> SeedAsync(string path)
        {
            GitRepository repository = await GitRepository.InitAsync(Root.Output(path), new GitInitOptions("main"));
            await repository.SetConfigAsync("user.name", "Submodules UI"); await repository.SetConfigAsync("user.email", "submodules@example.invalid"); await repository.SetConfigAsync("commit.gpgsign", "false"); await repository.SetConfigAsync("core.hooksPath", Root.Output("no-hooks"));
            await File.WriteAllTextAsync(Path.Combine(repository.Path, "file.txt"), "initial module bytes\n"); await repository.StageAllAsync(); await repository.CommitAsync("Initial module"); return repository;
        }
        internal async Task AddWitnessAsync()
        { await Root.GitAsync("submodule", "add", "--name", "witness", "--", Source.Path, WitnessPath); await Root.Repository.StageAllAsync(); await Root.Repository.CommitAsync("Witness module"); }
        internal async Task<string> AdvanceSourceAsync(string contents)
        { await File.WriteAllTextAsync(Path.Combine(Source.Path, "file.txt"), contents); await Source.StageAllAsync(); await Source.CommitAsync("Advance module " + Guid.NewGuid().ToString("N")); return await Source.ResolveRevisionAsync("HEAD"); }
        internal async Task<SubmoduleRemovalDialog> ShowAsync(ThemeVariant? theme = null, string? preferences = null, GitRepository? repository = null, Window? owner = null,
            Func<Window, GitRepository, CancellationToken, Task>? afterUpdate = null)
        {
            SubmoduleRemovalDialog dialog = new(repository ?? Root.Repository, afterSubmoduleUpdateAsync: afterUpdate, preferencesPath: preferences ?? Root.Output("manager-prefs.json")) { RequestedThemeVariant = theme ?? ThemeVariant.Light };
            Windows.Add(dialog); if (owner is null) { dialog.Show(); } else { _ = dialog.ShowDialog(owner); }
            await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            ListBox list = Field<ListBox>(dialog, "RemovalSubmodules"); list.SelectedItem = list.Items.OfType<GitSubmoduleSnapshot>().Single(item => item.Path == SelectedPath); Pump(dialog); return dialog;
        }
        internal async Task<string> BlockUpdateAsync()
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
            string path = Root.Output("private-update-git");
            await File.WriteAllTextAsync(path, "#!/usr/bin/python3\nimport os,sys,subprocess,time\na=sys.argv[1:]\nif 'submodule' in a and a[a.index('submodule')+1]=='update':\n p=subprocess.Popen(['/usr/bin/sleep','60'])\n with open(__file__+'.actors','w') as f: f.write(str(os.getpid())+' '+str(p.pid))\n time.sleep(60)\nos.execv('/usr/bin/git',['git']+a)\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
        }
        internal async Task<string> BlockPostRefreshAsync()
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
            string path = Root.Output("private-post-refresh-git");
            await File.WriteAllTextAsync(path, "#!/usr/bin/python3\nimport os,sys,subprocess,time\na=sys.argv[1:]\nc=a[a.index('submodule')+1] if 'submodule' in a else ''\nif c=='update':\n r=subprocess.run(['/usr/bin/git']+a)\n if r.returncode==0: open(__file__+'.updated','w').close()\n sys.exit(r.returncode)\nif c=='status' and os.path.exists(__file__+'.updated'):\n p=subprocess.Popen(['/usr/bin/sleep','60'])\n with open(__file__+'.actors','w') as f: f.write(str(os.getpid())+' '+str(p.pid))\n time.sleep(60)\nos.execv('/usr/bin/git',['git']+a)\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
        }
        internal static async Task<GitResult> GitAsync(GitRepository repository, params string[] arguments)
        { GitResult result = await repository.RunAsync(arguments); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window window in Windows.AsEnumerable().Reverse()) { Close(window); }
                await Until(() => Windows.All(window => !window.IsVisible && (window is not RepositoryDialog dialog || !dialog.IsBusy)));
            }
            finally
            {
                foreach (Actor actor in Actors.Where(actor => actor.Alive())) { try { Process.GetProcessById(actor.Pid).Kill(entireProcessTree: true); } catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { } }
                Root.Dispose();
            }
        }
        private static void Close(Window window) { foreach (Window child in window.OwnedWindows.ToArray()) { Close(child); } if (window.IsVisible) { window.Close(); } }
    }
}

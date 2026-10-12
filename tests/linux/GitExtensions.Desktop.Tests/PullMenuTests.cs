using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Extensibility.Git;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class PullMenuTests
{
    [AvaloniaTest]
    public async Task PullModesKeepTheirRenderedWindowsPaletteWithAnOrangePlatformAccent()
    {
        FluentTheme fluent = Application.Current!.Styles.OfType<FluentTheme>().Single();
        fluent.Palettes.TryGetValue(ThemeVariant.Light, out ColorPaletteResources? previousLight);
        fluent.Palettes.TryGetValue(ThemeVariant.Dark, out ColorPaletteResources? previousDark);
        Color orange = Color.Parse("#e95420");
        fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = orange };
        fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = orange };
        try
        {
            await using Fixture f = await Fixture.CreateAsync(); MainWindow main = await f.ShowAsync();
            PullDialog dialog = new(new GitRepository(f.Client), GitPullAction.Fetch);
            Task shown = dialog.ShowDialog(main); await dialog.InitializeAsync();
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light })
            {
                dialog.RequestedThemeVariant = theme; Pump(dialog);
                Assert.That(((IResourceNode)fluent).TryGetResource("SystemAccentColor", theme, out object? accent), Is.True);
                Assert.That(accent, Is.EqualTo(orange));
                Color expected = Color.Parse(theme == ThemeVariant.Dark ? "#2864b4" : "#0078d7");
                foreach (string name in new[] { "PullMerge", "PullRebase", "PullFetch" })
                {
                    RadioButton mode = Field<RadioButton>(dialog, name); mode.IsChecked = true; Pump(dialog); AssertChecked();
                    Point point = mode.TranslatePoint(new Point(10, 10), dialog)!.Value;
                    dialog.MouseMove(point); Pump(dialog); AssertChecked();
                    dialog.MouseDown(point, MouseButton.Left); Pump(dialog); AssertChecked();
                    dialog.MouseMove(new Point(dialog.Width - 2, dialog.Height - 2));
                    dialog.MouseUp(new Point(dialog.Width - 2, dialog.Height - 2), MouseButton.Left); Pump(dialog);
                    void AssertChecked()
                    {
                        Avalonia.Controls.Shapes.Ellipse circle = mode.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single(item => item.Name == "CheckOuterEllipse");
                        Assert.That(((ISolidColorBrush)circle.Fill!).Color, Is.EqualTo(expected), "The real checked/hover/pressed radio must retain the Windows palette.");
                    }
                }
                Capture(dialog, "pull-radio-palette-" + theme + ".png");
            }
            dialog.Close(); await shown;
        }
        finally
        {
            if (previousLight is null) { fluent.Palettes.Remove(ThemeVariant.Light); } else { fluent.Palettes[ThemeVariant.Light] = previousLight; }
            if (previousDark is null) { fluent.Palettes.Remove(ThemeVariant.Dark); } else { fluent.Palettes[ThemeVariant.Dark] = previousDark; }
        }
    }

    [AvaloniaTest]
    public async Task OriginalMenuAndDefaultSubmenuHaveTheirIconsAndChangingDefaultLeavesTheMenuOpenWithoutGit()
    {
        await using Fixture f = await Fixture.CreateAsync();
        MainWindow main = await f.ShowAsync();
        string before = await f.GitText("for-each-ref", "--format=%(refname) %(objectname)");
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme;
            Button dropdown = Field<Button>(main, "BrowsePullDropdown"); Click(dropdown);
            MenuFlyout menu = (MenuFlyout)dropdown.Flyout!;
            Assert.That(menu.IsOpen, Is.True);
            MenuFlyoutPresenter popup = Item(menu, "PullMenuNone").GetVisualAncestors().OfType<MenuFlyoutPresenter>().First();
            PixelPoint pullOrigin = Field<Button>(main, "BrowsePull").PointToScreen(default);
            Assert.That(popup.PointToScreen(default).X, Is.EqualTo(pullOrigin.X).Within(2), "The original dropdown aligns with the left edge of the whole split button.");
            Assert.That(menu.Items.OfType<MenuItem>().Select(item => item.Name), Is.EqualTo(new[]
            { "PullMenuNone", "PullMenuMerge", "PullMenuRebase", "PullMenuFetch", "PullMenuFetchAll", "PullMenuFetchPruneAll", "PullMenuSetDefault" }));
            Assert.That(menu.Items.OfType<Separator>().Count(), Is.EqualTo(2));
            Assert.That(Item(menu, "PullMenuNone").InputGesture, Is.EqualTo(new Avalonia.Input.KeyGesture(Avalonia.Input.Key.Down, Avalonia.Input.KeyModifiers.Control)));
            Assert.That(menu.Items.OfType<MenuItem>().Take(6).All(item => item.Icon is Image { Source: not null, Width: 16, Height: 16 }), Is.True);
            MenuItem defaults = Item(menu, "PullMenuSetDefault"); defaults.IsSubMenuOpen = true; Pump(main);
            MenuItem fetch = defaults.Items.OfType<MenuItem>().Single(item => item.Name == "PullMenuDefaultFetch");
            Click(fetch);
            Assert.That(menu.IsOpen && defaults.IsSubMenuOpen, Is.True, "Selecting a default must leave both menus open.");
            Assert.That(fetch.IsChecked, Is.True);
            Assert.That(defaults.Items.OfType<MenuItem>().Count(item => item.IsChecked), Is.EqualTo(1));
            Assert.That(await f.GitText("for-each-ref", "--format=%(refname) %(objectname)"), Is.EqualTo(before));
            Assert.That(main.IsBusy, Is.False);
            Capture(TopLevel.GetTopLevel(fetch)!, "pull-menu-default-" + theme + ".png");
            menu.Hide();
        }
        Assert.That(new PullPreferencesStore().Load().DefaultAction, Is.EqualTo(GitPullAction.Fetch));
        await f.CloseMainAsync();
        MainWindow reopened = await f.ShowAsync();
        Assert.That(ToolTip.GetTip(Field<Button>(reopened, "BrowsePull"))?.ToString(), Does.Contain("Fetch"));
    }

    [AvaloniaTest]
    public async Task DefaultFetchButtonAndF8UseTheCheckedOutBranchRemoteWhileFetchAllKeepsStaleRefs()
    {
        await using Fixture f = await Fixture.CreateAsync(); MainWindow main = await f.ShowAsync();
        string alpha = await f.AdvanceAsync("alpha", "alpha.txt", "alpha change\n");
        string zeta = await f.AdvanceAsync("zeta", "zeta.txt", "zeta change\n");
        ChooseDefault(main, GitPullAction.Fetch); Click(Field<Button>(main, "BrowsePull")); await Idle(main);
        Assert.That(await f.Ref("refs/remotes/zeta/main"), Is.EqualTo(zeta));
        Assert.That(await f.Ref("refs/remotes/alpha/main"), Is.EqualTo(f.Initial));
        string zeta2 = await f.AdvanceAsync("zeta", "zeta.txt", "zeta second change\n");
        Button dropdown = Field<Button>(main, "BrowsePullDropdown"); Click(dropdown);
        MenuItem defaults = Item((MenuFlyout)dropdown.Flyout!, "PullMenuSetDefault"); defaults.IsSubMenuOpen = true; Pump(main);
        Press(TopLevel.GetTopLevel(defaults.Items.OfType<MenuItem>().First())!, PhysicalKey.F8, RawInputModifiers.None); await Idle(main);
        Assert.That(((MenuFlyout)dropdown.Flyout!).IsOpen, Is.False, "F8 must also dispatch from the open default menu and close it before executing.");
        Assert.That(await f.Ref("refs/remotes/zeta/main"), Is.EqualTo(zeta2));
        Assert.That(await f.Ref("refs/remotes/alpha/main"), Is.EqualTo(f.Initial));
        ChooseDefault(main, GitPullAction.FetchAll); Press(main, PhysicalKey.F8, RawInputModifiers.None); await Idle(main);
        Assert.That(await f.Ref("refs/remotes/alpha/main"), Is.EqualTo(alpha));
        Assert.That(await f.Ref("refs/remotes/alpha/stale"), Is.EqualTo(f.Initial));
        Assert.That(await f.Ref("refs/remotes/zeta/stale"), Is.EqualTo(f.Initial));
        Assert.That(await f.Ref("HEAD"), Is.EqualTo(f.Initial));
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task PullMergeOnDivergentHistoryCreatesTheExpectedTwoParentsAndIgnoresTheGraphSelection()
    {
        await using Fixture f = await Fixture.CreateAsync();
        string local = await f.CommitAsync(f.Client, "local.txt", "local\n", "Local work");
        string remote = await f.AdvanceAsync("zeta", "remote.txt", "remote\n");
        await f.Git("config", "pull.rebase", "true");
        MainWindow main = await f.ShowAsync();
        Field<ListBox>(main, "RevisionHistory").SelectedItem = Field<ListBox>(main, "RevisionHistory").Items.OfType<GitCommit>().Last(); await Idle(main);
        ExecuteMenu(main, GitPullAction.Merge); await Idle(main);
        string[] parents = (await f.GitText("rev-list", "--parents", "-1", "HEAD")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(parents.Skip(1), Is.EqualTo(new[] { local, remote }));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Client, "local.txt")), Is.EqualTo("local\n"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Client, "remote.txt")), Is.EqualTo("remote\n"));
        Assert.That(Field<ListBox>(main, "RevisionHistory").Items.OfType<GitCommit>().Any(commit => commit.Hash == parents[0]), Is.True);
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task PullRebaseReplaysLocalCommitOnTheSelectedRemoteInsteadOfCreatingAMerge()
    {
        await using Fixture f = await Fixture.CreateAsync();
        string local = await f.CommitAsync(f.Client, "local.txt", "local\n", "Local work");
        string remote = await f.AdvanceAsync("zeta", "remote.txt", "remote\n");
        MainWindow main = await f.ShowAsync(); ExecuteMenu(main, GitPullAction.Rebase); await Idle(main);
        string[] row = (await f.GitText("rev-list", "--parents", "-1", "HEAD")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(row, Has.Length.EqualTo(2)); Assert.That(row[0], Is.Not.EqualTo(local)); Assert.That(row[1], Is.EqualTo(remote));
        Assert.That((await f.Repository.GetStatusAsync()).Changes, Is.Empty);
        Assert.That(new PullPreferencesStore().Load().DialogAction, Is.EqualTo(GitPullAction.Rebase));
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task PruneAllRequiresConfirmationAndDoesNotPruneTagsOrModifyLocalBranches()
    {
        await using Fixture f = await Fixture.CreateAsync(); MainWindow main = await f.ShowAsync();
        string before = await f.GitText("for-each-ref", "--format=%(refname) %(objectname)");
        ExecuteMenu(main, GitPullAction.FetchPruneAll);
        await Until(() => main.OwnedWindows.Any(window => window.Name == "ConfirmFetchPruneAll"));
        Window confirmation = main.OwnedWindows.Single(window => window.Name == "ConfirmFetchPruneAll");
        Click(Field<Button>(confirmation, "CancelFetchPruneAll")); await Idle(main);
        Assert.That(await f.GitText("for-each-ref", "--format=%(refname) %(objectname)"), Is.EqualTo(before));
        ExecuteMenu(main, GitPullAction.FetchPruneAll);
        await Until(() => main.OwnedWindows.Any(window => window.Name == "ConfirmFetchPruneAll"));
        confirmation = main.OwnedWindows.Single(window => window.Name == "ConfirmFetchPruneAll");
        Field<CheckBox>(confirmation, "DontConfirmFetchPruneAll").IsChecked = true;
        Click(Field<Button>(confirmation, "AcceptFetchPruneAll")); await Idle(main);
        Assert.That(await f.Ref("refs/remotes/alpha/stale"), Is.Null);
        Assert.That(await f.Ref("refs/remotes/zeta/stale"), Is.Null);
        Assert.That(await f.Ref("refs/tags/retained"), Is.EqualTo(f.Initial));
        Assert.That(await f.Ref("HEAD"), Is.EqualTo(f.Initial));
        Assert.That(new PullPreferencesStore().Load().DontConfirmFetchAndPruneAll, Is.True);
        ExecuteMenu(main, GitPullAction.FetchPruneAll); await Idle(main);
        Assert.That(main.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task CtrlDownUsesTheLastDialogModeEvenWhenTheButtonDefaultIsDifferentAndCancelDoesNotRunGit()
    {
        await using Fixture f = await Fixture.CreateAsync();
        new PullPreferencesStore().Save(new(DefaultAction: GitPullAction.Fetch, DialogAction: GitPullAction.Rebase));
        MainWindow main = await f.ShowAsync();
        string before = await f.GitText("for-each-ref", "--format=%(refname) %(objectname)");
        Press(main, PhysicalKey.ArrowDown, RawInputModifiers.Control);
        await Until(() => main.OwnedWindows.OfType<PullDialog>().Any());
        PullDialog dialog = main.OwnedWindows.OfType<PullDialog>().Single(); await Until(() => !dialog.IsBusy);
        Assert.That(Field<AutoCompleteBox>(dialog, "PullRemote").Text, Is.EqualTo("zeta"));
        Assert.That(Field<AutoCompleteBox>(dialog, "PullRemote").IsKeyboardFocusWithin, Is.True);
        Assert.That(Field<AutoCompleteBox>(dialog, "PullBranch").Text, Is.Empty);
        Assert.That(Field<RadioButton>(dialog, "PullRebase").IsChecked, Is.True);
        Assert.That(Field<CheckBox>(dialog, "PullPrune").IsEnabled, Is.False);
        Capture(dialog, "pull-dialog-last-mode.png");
        Click(Field<Button>(dialog, "ClosePull")); await Idle(main);
        Assert.That(await f.GitText("for-each-ref", "--format=%(refname) %(objectname)"), Is.EqualTo(before));
        Assert.That(new PullPreferencesStore().Load().DefaultAction, Is.EqualTo(GitPullAction.Fetch));
    }

    [AvaloniaTest]
    public async Task FailedPullShowsItsGitOutputAndCanAbortBothMergeAndRebaseConflicts()
    {
        foreach (GitPullAction action in new[] { GitPullAction.Merge, GitPullAction.Rebase })
        {
            await using Fixture f = await Fixture.CreateAsync();
            string local = await f.CommitAsync(f.Client, "seed.txt", "local edit\n", "Local conflict");
            await f.AdvanceAsync("zeta", "seed.txt", "remote edit\n");
            MainWindow main = await f.ShowAsync(); ExecuteMenu(main, action);
            await Until(() => main.OwnedWindows.OfType<PullDialog>().Any());
            PullDialog dialog = main.OwnedWindows.OfType<PullDialog>().Single(); await Until(() => !dialog.IsBusy);
            Assert.That(dialog.Succeeded, Is.False); Assert.That(dialog.LastError, Is.Not.Null);
            Assert.That(Field<TextBox>(dialog, "PullOutput").Text, Does.Contain("CONFLICT"));
            Assert.That((await f.Repository.GetStatusAsync()).Changes.Any(change => change.IsConflict), Is.True);
            Assert.That(Field<Button>(dialog, "ContinuePullOperation").IsEnabled, Is.False);
            Click(Field<Button>(dialog, "AbortPullOperation")); await Until(() => !dialog.IsBusy);
            Assert.That(await f.Ref("HEAD"), Is.EqualTo(local));
            Assert.That((await f.Repository.GetStatusAsync()).Changes, Is.Empty);
            Click(Field<Button>(dialog, "ClosePull")); await Idle(main);
            Assert.That(main.GetVisualDescendants().OfType<CommitGraph>().Single().CurrentHead, Is.EqualTo(local), "All-branches order may start with the remote commit; the checked-out HEAD marker must return to the local commit.");
        }
    }

    [AvaloniaTest]
    public async Task ConflictResolutionStagesTheChosenTextThenContinuesTheMerge()
    {
        await using Fixture f = await Fixture.CreateAsync();
        string local = await f.CommitAsync(f.Client, "seed.txt", "local edit\n", "Local conflict");
        string remote = await f.AdvanceAsync("zeta", "seed.txt", "remote edit\n");
        MainWindow main = await f.ShowAsync(); ExecuteMenu(main, GitPullAction.Merge);
        await Until(() => main.OwnedWindows.OfType<PullDialog>().Any());
        PullDialog dialog = main.OwnedWindows.OfType<PullDialog>().Single(); await Until(() => !dialog.IsBusy);
        Click(Field<Button>(dialog, "ResolvePullConflict"));
        await Until(() => dialog.OwnedWindows.Any());
        Window resolution = dialog.OwnedWindows.Single(); await Until(() => resolution is RepositoryDialog repo && !repo.IsBusy);
        Field<TextBox>(resolution, "PatchConflictText").Text = "resolved by user\n";
        Click(Field<Button>(resolution, "SavePatchResolution"));
        await Until(() => resolution is RepositoryDialog repo && !repo.IsBusy);
        resolution.Close(); await Until(() => Field<Button>(dialog, "ContinuePullOperation").IsEnabled);
        Click(Field<Button>(dialog, "ContinuePullOperation")); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.LastError, Is.Null);
        Assert.That((await f.GitText("rev-list", "--parents", "-1", "HEAD")).Split(' ').Skip(1), Is.EqualTo(new[] { local, remote }));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(f.Client, "seed.txt")), Is.EqualTo("resolved by user\n"));
        Click(Field<Button>(dialog, "ClosePull")); await Idle(main);
    }

    [AvaloniaTest]
    public async Task FetchAllVisibilityTracksRemoteCountWithoutReplacingAnAlreadySavedDefaultAndMenusUseFrenchXlf()
    {
        await using Fixture f = await Fixture.CreateAsync();
        new PullPreferencesStore().Save(new(DefaultAction: GitPullAction.FetchAll));
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        MainWindow main = await f.ShowAsync();
        Button dropdown = Field<Button>(main, "BrowsePullDropdown"); MenuFlyout menu = (MenuFlyout)dropdown.Flyout!;
        Click(dropdown); Assert.That(Item(menu, "PullMenuFetchAll").IsVisible, Is.True);
        Assert.That(((AccessText)Item(menu, "PullMenuSetDefault").Header!).Text, Does.Contain("défaut"));
        Assert.That(((AccessText)Item(menu, "PullMenuRebase").Header!).Text, Does.Contain("rebaser"));
        menu.Hide();
        await f.Git("remote", "remove", "alpha");
        Press(main, PhysicalKey.F5, RawInputModifiers.None); await Idle(main);
        Click(dropdown);
        Assert.That(Item(menu, "PullMenuFetchAll").IsVisible, Is.False);
        Assert.That(Item(menu, "PullMenuSetDefault").Items.OfType<MenuItem>().Single(item => item.Name == "PullMenuDefaultFetchAll").IsVisible, Is.False);
        Assert.That(Item(menu, "PullMenuFetchPruneAll").IsVisible, Is.True);
        Assert.That(new PullPreferencesStore().Load().DefaultAction, Is.EqualTo(GitPullAction.FetchAll));
        menu.Hide();
    }

    [AvaloniaTest]
    public async Task DialogRejectsWildcardPullAndCancellationBeforeExecutionPreservesGit()
    {
        await using Fixture f = await Fixture.CreateAsync();
        PullDialog dialog = new(f.Repository); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Field<AutoCompleteBox>(dialog, "PullBranch").Text = "*";
            Click(Field<Button>(dialog, "ExecutePull")); await Until(() => !dialog.IsBusy);
            Assert.That(dialog.LastError, Does.Contain("Wildcards")); Assert.That(dialog.Attempted, Is.False);
            Field<AutoCompleteBox>(dialog, "PullBranch").Text = "main";
            using CancellationTokenSource cancelled = new(); cancelled.Cancel();
            await dialog.ExecuteAsync(dialog, cancellationToken: cancelled.Token);
            Assert.That(dialog.Succeeded, Is.False); Assert.That(dialog.Attempted, Is.False);
            Assert.That(await f.Ref("HEAD"), Is.EqualTo(f.Initial));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task PullWithoutAnUpstreamUsesTheFirstRealRemoteAndQuickMergeShortcut()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.Git("config", "--unset", "branch.main.remote"); await f.Git("config", "--unset", "branch.main.merge");
        string alpha = await f.AdvanceAsync("alpha", "first.txt", "first remote\n");
        await f.AdvanceAsync("zeta", "second.txt", "second remote\n");
        MainWindow main = await f.ShowAsync(); Press(main, PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift);
        await Until(() => main.OwnedWindows.OfType<PullDialog>().Any());
        PullDialog dialog = main.OwnedWindows.OfType<PullDialog>().Single(); await Until(() => !dialog.IsBusy);
        Assert.That(Field<AutoCompleteBox>(dialog, "PullRemote").Text, Is.EqualTo("alpha"));
        Assert.That(dialog.LastError, Is.Not.Null, "Without an upstream Git requires a branch; expose that error and let the user choose.");
        Assert.That(await f.Ref("HEAD"), Is.EqualTo(f.Initial));
        EnterAutoComplete(dialog, "PullBranch", "main");
        Click(Field<Button>(dialog, "ExecutePull")); await Idle(main);
        Assert.That(main.OwnedWindows, Is.Empty);
        Assert.That(await f.Ref("HEAD"), Is.EqualTo(alpha));
        Assert.That(await f.Ref("refs/remotes/zeta/main"), Is.EqualTo(f.Initial));
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task DetachedPullCanBeDeclinedBeforeAnyFetchOrHeadChange()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.Git("checkout", "--detach", f.Initial); await f.AdvanceAsync("alpha", "new.txt", "remote change\n");
        MainWindow main = await f.ShowAsync();
        string refs = await f.GitText("for-each-ref", "--format=%(refname) %(objectname)");
        ExecuteMenu(main, GitPullAction.Merge);
        await Until(() => main.OwnedWindows.Any(window => window.Name == "ConfirmDetachedPull"));
        Click(Field<Button>(main.OwnedWindows.Single(window => window.Name == "ConfirmDetachedPull"), "CancelDetachedPull")); await Idle(main);
        Assert.That(await f.Ref("HEAD"), Is.EqualTo(f.Initial));
        Assert.That(await f.GitText("for-each-ref", "--format=%(refname) %(objectname)"), Is.EqualTo(refs));
        Assert.That((await f.Repository.GetStatusAsync()).IsDetached, Is.True);
        Assert.That(main.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task BareRepositoryCanFetchInTheDialogButQuickPullShortcutsCannotBecomeFetches()
    {
        await using Fixture f = await Fixture.CreateAsync();
        string remote = await f.AdvanceAsync("zeta", "new.txt", "remote change\n");
        GitRepository bare = new(f.Bare("alpha"));
        await bare.RemoveRemoteAsync("origin"); await bare.AddRemoteAsync("zeta", f.Bare("zeta"));
        PullDialog dialog = new(bare, GitPullAction.Fetch); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(Field<RadioButton>(dialog, "PullMerge").IsEnabled, Is.False);
            Assert.That(Field<RadioButton>(dialog, "PullFetch").IsChecked, Is.True);
            Click(Field<Button>(dialog, "ExecutePull")); await Until(() => !dialog.IsBusy);
            Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
            Assert.That((await bare.RunAsync(["rev-parse", "refs/remotes/zeta/main"])).StandardOutput.Trim(), Is.EqualTo(remote));
        }
        finally { dialog.Close(); }
        MainWindow main = await f.ShowAsync(f.Bare("alpha"));
        string before = (await bare.RunAsync(["for-each-ref", "--format=%(refname) %(objectname)"])).StandardOutput;
        ChooseDefault(main, GitPullAction.Merge);
        Assert.That(Field<Button>(main, "BrowsePull").IsEnabled, Is.False);
        Press(main, PhysicalKey.F8, RawInputModifiers.None); await Idle(main);
        Press(main, PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift); await Idle(main);
        Assert.That(main.OwnedWindows, Is.Empty);
        Assert.That((await bare.RunAsync(["for-each-ref", "--format=%(refname) %(objectname)"])).StandardOutput, Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ChoosingAllRemotesForcesFetchAndDisablesMergeAndRebaseWithoutRunningGit()
    {
        await using Fixture f = await Fixture.CreateAsync(); PullDialog dialog = new(f.Repository); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            EnterAutoComplete(dialog, "PullRemote", "[ All ]");
            Assert.That(Field<RadioButton>(dialog, "PullFetch").IsChecked, Is.True);
            Assert.That(Field<RadioButton>(dialog, "PullMerge").IsEnabled, Is.False);
            Assert.That(Field<RadioButton>(dialog, "PullRebase").IsEnabled, Is.False);
            Assert.That(Field<Button>(dialog, "ExecutePull").IsEnabled, Is.True);
            Assert.That(dialog.Attempted, Is.False);
            Assert.That(await f.Ref("HEAD"), Is.EqualTo(f.Initial));
            EnterAutoComplete(dialog, "PullRemote", "zeta");
            Assert.That(Field<RadioButton>(dialog, "PullMerge").IsEnabled, Is.True);
            Assert.That(Field<RadioButton>(dialog, "PullRebase").IsEnabled, Is.True);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task DefaultNoOnRebaseMergeConfirmationDoesNotExecuteAnAlternativeMerge()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.Git("checkout", "-b", "side"); await f.CommitAsync(f.Client, "side.txt", "side\n", "Side work");
        await f.Git("checkout", "main"); await f.CommitAsync(f.Client, "main-local.txt", "main\n", "Main work");
        await f.Git("merge", "--no-ff", "--no-edit", "side");
        string before = await f.Ref("HEAD") ?? "";
        await f.AdvanceAsync("zeta", "remote.txt", "remote\n");
        MainWindow main = await f.ShowAsync(); string refs = await f.GitText("for-each-ref", "--format=%(refname) %(objectname)");
        ExecuteMenu(main, GitPullAction.Rebase);
        await Until(() => main.OwnedWindows.Any(window => window.Name == "ConfirmRebasePull"));
        Window confirm = main.OwnedWindows.Single(window => window.Name == "ConfirmRebasePull");
        Assert.That(confirm.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault).Content, Is.EqualTo("No"));
        confirm.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        main.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); Pump(main);
        await Until(() => main.OwnedWindows.OfType<PullDialog>().Any());
        PullDialog dialog = main.OwnedWindows.OfType<PullDialog>().Single();
        Assert.That(dialog.Attempted, Is.False); Assert.That(Field<RadioButton>(dialog, "PullRebase").IsChecked, Is.True);
        Assert.That(await f.Ref("HEAD"), Is.EqualTo(before));
        Assert.That(await f.GitText("for-each-ref", "--format=%(refname) %(objectname)"), Is.EqualTo(refs));
        Click(Field<Button>(dialog, "ClosePull")); await Idle(main);
    }

    [AvaloniaTest]
    public async Task CancellingAQuickFetchStopsTheProcessAndDoesNotOpenAFallbackDialog()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private executable gate uses Linux process cancellation."); return; }
        await using Fixture f = await Fixture.CreateAsync(); MainWindow main = await f.ShowAsync();
        string gate = Path.Combine(f.Client, "slow-git"), marker = Path.Combine(f.Client, "network-started.pid");
        string literal = "'" + marker.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        await File.WriteAllTextAsync(gate, "#!/bin/sh\nfor task_arg do\n  case \"$task_arg\" in fetch|pull) printf '%s\\n' \"$$\" > " + literal + "; sleep 60 & wait ;; esac\ndone\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(gate, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        typeof(MainWindow).GetField("_repository", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(main, new GitRepository(f.Client, new GitRunner(gate)));
        ExecuteMenu(main, GitPullAction.Fetch);
        await Until(() => File.Exists(marker));
        Click(Field<Button>(main, "BrowseCancelOperation")); await Idle(main);
        Assert.That(main.OwnedWindows, Is.Empty, "Cancellation must terminate rather than opening a retry dialog.");
        Assert.That(await f.Ref("HEAD"), Is.EqualTo(f.Initial));
        Assert.That(await f.Ref("refs/remotes/zeta/main"), Is.EqualTo(f.Initial));
        Assert.That(main.LastException, Is.Null);
        File.Delete(gate); File.Delete(marker);
    }

    private static void ChooseDefault(MainWindow main, GitPullAction action)
    {
        Button dropdown = Field<Button>(main, "BrowsePullDropdown"); Click(dropdown);
        MenuFlyout menu = (MenuFlyout)dropdown.Flyout!;
        MenuItem defaults = Item(menu, "PullMenuSetDefault");
        defaults.Items.OfType<MenuItem>().Single(item => item.Name == "PullMenuDefault" + action).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Hide(); Pump(main);
    }
    private static void ExecuteMenu(MainWindow main, GitPullAction action)
    {
        Button dropdown = Field<Button>(main, "BrowsePullDropdown"); Click(dropdown);
        Item((MenuFlyout)dropdown.Flyout!, "PullMenu" + action).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(main);
    }
    private static MenuItem Item(MenuFlyout menu, string name) => menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static void EnterAutoComplete(Window dialog, string name, string value)
    {
        TextBox text = Field<AutoCompleteBox>(dialog, name).GetVisualDescendants().OfType<TextBox>().First();
        text.Focus(); text.SelectAll(); dialog.KeyTextInput(value); Pump(dialog);
        Press(dialog, PhysicalKey.Tab, RawInputModifiers.None);
        Assert.That(Field<AutoCompleteBox>(dialog, name).Text, Is.EqualTo(value));
    }
    private static void Pump(TopLevel root) { root.UpdateLayout(); Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); }
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers)
    { root.KeyPressQwerty(key, modifiers); root.KeyReleaseQwerty(key, modifiers); Pump(root); }
    private static void Click(Control item)
    {
        TopLevel root = TopLevel.GetTopLevel(item)!; Pump(root);
        Assert.That(item.IsEffectivelyVisible && item.IsEffectivelyEnabled, Is.True, item.Name);
        Point point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point); root.MouseDown(point, Avalonia.Input.MouseButton.Left); root.MouseUp(point, Avalonia.Input.MouseButton.Left); Pump(root);
    }
    private static async Task Idle(MainWindow main) { await Task.Delay(20); await Until(() => !main.IsBusy); Pump(main); }
    private static async Task Until(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!predicate() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        Assert.That(predicate(), Is.True, "Pull/Fetch UI did not reach its expected state.");
    }
    private static void Capture(TopLevel root, string filename)
    {
        Pump(root); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = root.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null); string file = Path.Combine(TestContext.CurrentContext.WorkDirectory, filename);
        frame!.Save(file); TestContext.AddTestAttachment(file);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-pull-ui-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> _environment = new();
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly GitRunner _git = new();
        private MainWindow? _main;
        internal string Client => Path.Combine(_root, "client");
        internal string Bare(string remote) => Path.Combine(_root, remote + ".git");
        internal GitRepository Repository => new(Client);
        internal string Initial { get; private set; } = "";
        private Fixture()
        {
            foreach ((string key, string value) in new Dictionary<string, string>
            {
                ["XDG_CONFIG_HOME"] = Path.Combine(_root, "config"), ["GIT_CONFIG_GLOBAL"] = "/dev/null", ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "protocol.file.allow", ["GIT_CONFIG_VALUE_0"] = "always"
            }) { _environment[key] = Environment.GetEnvironmentVariable(key); Environment.SetEnvironmentVariable(key, value); }
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US"); Directory.CreateDirectory(_root);
        }
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture f = new(); string seed = Path.Combine(f._root, "seed"); Directory.CreateDirectory(seed);
            await f.Run(seed, "init", "-b", "main"); await f.Configure(seed);
            f.Initial = await f.CommitAsync(seed, "seed.txt", "initial\n", "Initial commit");
            foreach (string remote in new[] { "alpha", "zeta" })
            {
                await f.Run(f._root, "clone", "--bare", seed, Path.Combine(f._root, remote + ".git"));
                await f.Run(f._root, "clone", Path.Combine(f._root, remote + ".git"), Path.Combine(f._root, remote + "-work"));
                await f.Configure(Path.Combine(f._root, remote + "-work"));
                await f.Run(Path.Combine(f._root, remote + ".git"), "update-ref", "refs/heads/stale", f.Initial);
                await f.Run(Path.Combine(f._root, remote + ".git"), "tag", "retained", f.Initial);
            }
            await f.Run(f._root, "clone", Path.Combine(f._root, "alpha.git"), f.Client); await f.Configure(f.Client);
            await f.Git("remote", "rename", "origin", "alpha"); await f.Git("remote", "add", "zeta", Path.Combine(f._root, "zeta.git"));
            await f.Git("fetch", "--all"); await f.Git("branch", "--set-upstream-to", "zeta/main", "main");
            await f.Git("config", "fetch.prune", "false"); await f.Git("config", "fetch.pruneTags", "false");
            foreach (string remote in new[] { "alpha", "zeta" })
            {
                await f.Run(Path.Combine(f._root, remote + ".git"), "update-ref", "-d", "refs/heads/stale");
                await f.Run(Path.Combine(f._root, remote + ".git"), "update-ref", "-d", "refs/tags/retained");
            }
            return f;
        }
        private async Task Configure(string path)
        {
            await Run(path, "config", "user.name", "Pull UI Tester"); await Run(path, "config", "user.email", "pull@example.invalid");
            await Run(path, "config", "commit.gpgsign", "false"); await Run(path, "config", "core.hooksPath", Path.Combine(_root, "no-hooks"));
        }
        internal async Task<string> CommitAsync(string path, string filename, string text, string subject)
        {
            await File.WriteAllTextAsync(Path.Combine(path, filename), text);
            await Run(path, "add", "--", filename); await Run(path, "commit", "-m", subject);
            return (await Run(path, "rev-parse", "HEAD")).StandardOutput.Trim();
        }
        internal async Task<string> AdvanceAsync(string remote, string filename, string text)
        {
            string work = Path.Combine(_root, remote + "-work");
            string head = await CommitAsync(work, filename, text, "Update " + remote);
            await Run(work, "push", "origin", "main"); return head;
        }
        internal async Task<MainWindow> ShowAsync(string? repository = null)
        {
            _main = new MainWindow { Width = 1280, Height = 752 }; _main.Show();
            await _main.OpenRepositoryAsync(repository ?? Client); await Idle(_main); return _main;
        }
        internal async Task CloseMainAsync()
        {
            if (_main is null) { return; }
            foreach (Window owned in _main.OwnedWindows.ToArray()) { owned.Close(); }
            await Idle(_main); _main.Close(); _main = null;
        }
        internal Task<GitResult> Git(params string[] args) => Run(Client, args);
        internal async Task<string> GitText(params string[] args) => (await Git(args)).StandardOutput.Trim();
        internal async Task<string?> Ref(string name)
        {
            GitResult result = await _git.RunAsync(Client, ["rev-parse", "--verify", "--quiet", name]);
            return result.Success ? result.StandardOutput.Trim() : null;
        }
        private async Task<GitResult> Run(string path, params string[] args)
        {
            GitResult result = await _git.RunAsync(path, args); Assert.That(result.Success, Is.True, result.StandardError); return result;
        }
        public async ValueTask DisposeAsync()
        {
            await CloseMainAsync();
            foreach ((string key, string? value) in _environment) { Environment.SetEnvironmentVariable(key, value); }
            CultureInfo.CurrentUICulture = _culture; Directory.Delete(_root, true);
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class PushDialogTests
{
    [AvaloniaTest]
    public async Task QuickPushShortcutAndShiftPointerPushTheCheckoutWithoutOpeningTheConfigurationWindow()
    {
        await using Fixture f = await Fixture.CreateAsync();
        MainWindow main = new() { Width = 1280, Height = 752 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Client);
            ListBox history = Field<ListBox>(main, "RevisionHistory");
            history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.Initial);
            await Until(() => !main.IsBusy);
            foreach (bool pointer in new[] { false, true })
            {
                await f.CommitAsync(pointer ? "pointer.txt" : "shortcut.txt", "latest\n", "Quick push checkout");
                string head = await f.Head();
                if (pointer)
                {
                    Control button = Field<Button>(main, "BrowsePush"); Pump(main);
                    Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), main)!.Value;
                    main.MouseMove(center); main.MouseDown(center, MouseButton.Left, RawInputModifiers.Shift);
                    main.MouseUp(center, MouseButton.Left, RawInputModifiers.Shift);
                }
                else { main.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Control | RawInputModifiers.Shift); main.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Control | RawInputModifiers.Shift); }
                await Until(() => !main.IsBusy);
                Assert.That(main.OwnedWindows.OfType<PushDialog>(), Is.Empty);
                Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(head));
                Assert.That(await f.Head(), Is.EqualTo(head));
                Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(f.Initial));
                Assert.That(await f.GitText("status", "--porcelain"), Is.Empty);
                Assert.That(main.LastException, Is.Null);
            }
            PointerClick(Field<Button>(main, "BrowsePush"));
            await Until(() => main.OwnedWindows.OfType<PushDialog>().Any(dialog => dialog.IsReady));
            PushDialog normal = main.OwnedWindows.OfType<PushDialog>().Single();
            Assert.That(normal.Attempted, Is.False, "A later plain click must remain the normal Push action.");
            normal.Close(); await Until(() => !main.IsBusy);
        }
        finally { main.Close(); Pump(main); }
    }

    [AvaloniaTest]
    public async Task QuickPushRetainsTheOriginalFormAfterHookRejectionAndCanRetryWithoutOfferingForce()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private executable hook requires Linux."); return; }
        await using Fixture f = await Fixture.CreateAsync();
        await f.CommitAsync("latest.txt", "latest\n", "Local change"); string head = await f.Head();
        string hooks = Path.Combine(f.Root, "quick-hooks"); Directory.CreateDirectory(hooks);
        string hook = Path.Combine(hooks, "pre-push");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nprintf 'private quick-push rejection\\n' >&2\nexit 1\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await f.Git("config", "core.hooksPath", hooks);
        MainWindow main = new() { Width = 1280, Height = 752 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Client); main.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Control | RawInputModifiers.Shift); main.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Control | RawInputModifiers.Shift);
            await Until(() => main.OwnedWindows.OfType<PushDialog>().Any(dialog => dialog.IsReady && !dialog.IsBusy));
            PushDialog fallback = main.OwnedWindows.OfType<PushDialog>().Single();
            Assert.That(fallback.Attempted, Is.True); Assert.That(fallback.LastError, Does.Contain("private quick-push rejection"));
            Assert.That(fallback.OwnedWindows, Is.Empty);
            Assert.That(Field<AutoCompleteBox>(fallback, "PushBranch").Text, Is.EqualTo("main"));
            Assert.That(Field<AutoCompleteBox>(fallback, "PushRemoteBranch").Text, Is.EqualTo("main"));
            Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(f.Initial));
            Assert.That(await f.Head(), Is.EqualTo(head));
            File.Delete(hook); PointerClick(Field<Button>(fallback, "ExecutePush"));
            await Until(() => !fallback.IsVisible && !main.IsBusy);
            Assert.That(fallback.Succeeded, Is.True, fallback.LastError);
            Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(head));
        }
        finally { main.Close(); Pump(main); }
    }

    [AvaloniaTest]
    public async Task QuickPushConfirmationDeclineShowsTheOriginalOptionsWithoutChangingRefsOrTracking()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.Git("checkout", "-b", "unpublished");
        string before = await f.GitText("config", "--local", "--list");
        MainWindow main = new() { Width = 1280, Height = 752 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Client); main.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Control | RawInputModifiers.Shift); main.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Control | RawInputModifiers.Shift);
            await Until(() => main.OwnedWindows.Any(window => window.Name == "PushNewBranchConfirmation"));
            Window confirmation = main.OwnedWindows.Single(window => window.Name == "PushNewBranchConfirmation");
            PointerClick(Field<Button>(confirmation, "PushChoice0"));
            await Until(() => main.OwnedWindows.OfType<PushDialog>().Any(dialog => dialog.IsReady && !dialog.IsBusy));
            PushDialog fallback = main.OwnedWindows.OfType<PushDialog>().Single();
            Assert.That(fallback.Attempted, Is.False); Assert.That(fallback.Cancelled, Is.True);
            Assert.That(Field<AutoCompleteBox>(fallback, "PushBranch").Text, Is.EqualTo("unpublished"));
            Assert.That(await f.RemoteRef("zeta", "unpublished"), Is.Null);
            Assert.That(await f.GitText("config", "--local", "--list"), Is.EqualTo(before));
            fallback.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); await Until(() => !main.IsBusy);
            Assert.That(await f.GitText("status", "--porcelain"), Is.Empty);
        }
        finally { main.Close(); Pump(main); }
    }

    [AvaloniaTest]
    public async Task BranchDialogUsesTheActualCheckoutAndItsConfiguredRemoteRatherThanTheSelectedGraphCommit()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.CommitAsync("latest.txt", "latest\n", "Latest commit");
        await f.Git("config", "branch.main.merge", "refs/heads/published");
        await f.Git("push", "zeta", "main:published");
        MainWindow main = new() { Width = 1280, Height = 752 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Client);
            ListBox history = Field<ListBox>(main, "RevisionHistory");
            history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.Initial);
            Pump(main); await Until(() => !main.IsBusy); Click(Field<Button>(main, "BrowsePush"));
            await Until(() => main.OwnedWindows.OfType<PushDialog>().Any(dialog => dialog.IsReady && !dialog.IsBusy));
            PushDialog dialog = main.OwnedWindows.OfType<PushDialog>().Single();
            Assert.That(Field<AutoCompleteBox>(dialog, "PushBranch").Text, Is.EqualTo("main"));
            Assert.That(Field<ComboBox>(dialog, "PushRemote").SelectedItem, Is.EqualTo("zeta"));
            Assert.That(Field<AutoCompleteBox>(dialog, "PushRemoteBranch").Text, Is.EqualTo("published"));
            string actual = await f.Head();
            await f.CommitAsync("last.txt", "last\n", "Last change before push"); actual = await f.Head();
            Click(Field<Button>(dialog, "ExecutePush"));
            await Until(() => !dialog.IsVisible && !main.IsBusy);
            Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
            Assert.That(await f.RemoteRef("zeta", "published"), Is.EqualTo(actual));
            Assert.That(await f.Head(), Is.EqualTo(actual));
            Assert.That(await f.GitText("status", "--porcelain"), Is.Empty);
            Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(f.Initial));
        }
        finally { main.Close(); Pump(main); }
    }

    [AvaloniaTest]
    public async Task NewBranchAndTrackingConfirmationsCanCancelThenPushLiteralNamesWithoutChangingTheCheckout()
    {
        await using Fixture f = await Fixture.CreateAsync();
        const string branch = "topic/λ;$()'quoted";
        await f.Git("branch", branch);
        PushDialog dialog = await f.ShowAsync();
        Field<AutoCompleteBox>(dialog, "PushBranch").Text = branch;
        Task first = dialog.ExecuteAsync(dialog);
        await Answer(dialog, "PushNewBranchConfirmation", 0); await first;
        Assert.That(dialog.Attempted, Is.False); Assert.That(dialog.Cancelled, Is.True);
        Assert.That(await f.RemoteRef("zeta", branch), Is.Null);
        Task second = dialog.ExecuteAsync(dialog);
        await Answer(dialog, "PushNewBranchConfirmation", 1, remember: true);
        await Answer(dialog, "PushTrackingConfirmation", 0); await second;
        Assert.That(dialog.Attempted, Is.False); Assert.That(await f.RemoteRef("zeta", branch), Is.Null);
        Task third = dialog.ExecuteAsync(dialog);
        await Answer(dialog, "PushTrackingConfirmation", 2); await third;
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(await f.RemoteRef("zeta", branch), Is.EqualTo(f.Initial));
        Assert.That(await f.GitText("config", "--get", "branch." + branch + ".remote", allowFailure: true), Is.Empty);
        Assert.That((await f.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
        Assert.That(new PushPreferencesStore().Load().DontConfirmNewBranch, Is.True);
    }

    [AvaloniaTest]
    public async Task TrackingConfirmationYesSetsTheSelectedDestinationAndRecursionPersistsWithoutPersistingForce()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.Git("branch", "feature");
        PushDialog dialog = await f.ShowAsync();
        Field<AutoCompleteBox>(dialog, "PushBranch").Text = "feature";
        Field<AutoCompleteBox>(dialog, "PushRemoteBranch").Text = "renamed-destination";
        Click(Field<Button>(dialog, "PushShowOptions"));
        Field<ComboBox>(dialog, "PushRecursiveSubmodules").SelectedIndex = 2;
        Task pushed = dialog.ExecuteAsync(dialog);
        await Answer(dialog, "PushNewBranchConfirmation", 1); await Answer(dialog, "PushTrackingConfirmation", 1); await pushed;
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(await f.GitText("config", "branch.feature.remote"), Is.EqualTo("zeta"));
        Assert.That(await f.GitText("config", "branch.feature.merge"), Is.EqualTo("refs/heads/renamed-destination"));
        Assert.That(await f.RemoteRef("zeta", "renamed-destination"), Is.EqualTo(f.Initial));
        dialog.Close();
        PushDialog reopened = await f.ShowAsync();
        Assert.That(Field<ComboBox>(reopened, "PushRecursiveSubmodules").SelectedIndex, Is.EqualTo(2));
        Assert.That(Field<CheckBox>(reopened, "PushForceWithLease").IsChecked, Is.Not.True);
        Assert.That(Field<CheckBox>(reopened, "ForcePushBranches").IsChecked, Is.Not.True);
        Assert.That(Field<StackPanel>(reopened, "PushOptionsPanel").IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task ConfiguredPushRefspecAndRemotePrefixSupplyDistinctEditableDestinations()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.Git("config", "remote.zeta.push", "refs/heads/main:refs/heads/review/main");
        await f.Git("config", "remote.zeta.prefix", "review/");
        await f.Git("branch", "feature");
        PushDialog dialog = await f.ShowAsync();
        Assert.That(Field<AutoCompleteBox>(dialog, "PushRemoteBranch").Text, Is.EqualTo("review/main"));
        Field<AutoCompleteBox>(dialog, "PushBranch").Text = "feature";
        Assert.That(Field<AutoCompleteBox>(dialog, "PushRemoteBranch").Text, Is.EqualTo("review/feature"));
        Field<RadioButton>(dialog, "PushToUrl").IsChecked = true;
        Assert.That(Field<AutoCompleteBox>(dialog, "PushRemoteBranch").Text, Is.EqualTo("feature"));
        Field<AutoCompleteBox>(dialog, "PushDestination").Text = f.Remote("alpha");
        await dialog.ExecuteAsync(dialog);
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(await f.RemoteRef("alpha", "feature"), Is.EqualTo(f.Initial));
        Assert.That(await f.RemoteRef("zeta", "feature"), Is.Null);
        Assert.That(new CloneSourcesStore().Load(), Does.Contain(f.Remote("alpha")), "The original clone/push destination history is shared.");
        dialog.Close(); PushDialog reopened = await f.ShowAsync();
        Field<RadioButton>(reopened, "PushToUrl").IsChecked = true;
        Assert.That(Field<AutoCompleteBox>(reopened, "PushDestination").ItemsSource!.Cast<string>(), Does.Contain(f.Remote("alpha")));
    }

    [AvaloniaTest]
    public async Task ManageRemotesReturnsToPushWithSeparateFetchAndPushUrlsAndItsRealNewDestination()
    {
        await using Fixture f = await Fixture.CreateAsync(); PushDialog dialog = await f.ShowAsync();
        PointerClick(Field<Button>(dialog, "PushManageRemotes"));
        await Until(() => dialog.OwnedWindows.OfType<PushRemoteDialog>().Any(child => !child.IsBusy));
        PushRemoteDialog remotes = dialog.OwnedWindows.OfType<PushRemoteDialog>().Single();
        Field<TextBox>(remotes, "PushManagedRemoteName").Text = "alternate";
        Field<TextBox>(remotes, "PushManagedFetchUrl").Text = f.Remote("zeta");
        Field<TextBox>(remotes, "PushManagedPushUrl").Text = f.Remote("alpha");
        PointerClick(Field<Button>(remotes, "PushAddRemote")); await Until(() => !remotes.IsBusy);
        Assert.That(remotes.LastError, Is.Null); Assert.That(remotes.HasRepositoryChanges, Is.True);
        remotes.Close(); await Until(() => !dialog.IsBusy);
        Assert.That(dialog.HasRepositoryChanges, Is.True);
        Field<ComboBox>(dialog, "PushRemote").SelectedItem = "alternate"; await Until(() => !dialog.IsBusy);
        Assert.That(Field<AutoCompleteBox>(dialog, "PushDestination").Text, Is.EqualTo(f.Remote("alpha")));
        Assert.That(await f.GitText("config", "remote.alternate.url"), Is.EqualTo(f.Remote("zeta")));
        Assert.That(await f.GitText("config", "remote.alternate.pushurl"), Is.EqualTo(f.Remote("alpha")));
        Task push = dialog.ExecuteAsync(dialog); await Answer(dialog, "PushNewBranchConfirmation", 1); await push;
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(await f.RemoteRef("alpha", "main"), Is.EqualTo(f.Initial));
        Assert.That(await f.GitText("config", "branch.main.remote"), Is.EqualTo("zeta"));
    }

    [AvaloniaTest]
    public async Task DetachedHeadRequiresAnExplicitDestinationAndBareRepositoriesCanPushTheirBranches()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.Git("checkout", "--detach", "HEAD");
        PushDialog detached = await f.ShowAsync();
        Field<ComboBox>(detached, "PushRemote").SelectedItem = "zeta"; await Until(() => !detached.IsBusy);
        Assert.That(Field<AutoCompleteBox>(detached, "PushBranch").Text, Is.EqualTo("HEAD"));
        Assert.That(Field<Button>(detached, "ExecutePush").IsEnabled, Is.False);
        Field<AutoCompleteBox>(detached, "PushRemoteBranch").Text = "detached-copy";
        Task push = detached.ExecuteAsync(detached); await Answer(detached, "PushNewBranchConfirmation", 1); await push;
        Assert.That(detached.Succeeded, Is.True, detached.LastError);
        Assert.That(await f.RemoteRef("zeta", "detached-copy"), Is.EqualTo(f.Initial));
        Assert.That((await f.Repository.GetStatusAsync()).IsDetached, Is.True);
        detached.Close();
        PushDialog bare = new(new GitRepository(f.Remote("zeta"))); f.Windows.Add(bare);
        await f.RemoteGit("zeta", "remote", "add", "target", f.Remote("alpha"));
        bare.Show(); await bare.InitializeAsync();
        Assert.That(Field<AutoCompleteBox>(bare, "PushBranch").Text, Is.EqualTo("main"));
        Assert.That(Field<Button>(bare, "PushPull").IsEnabled, Is.False);
        await bare.ExecuteAsync(bare);
        Assert.That(bare.Succeeded, Is.True, bare.LastError);
        Assert.That(await f.RemoteRef("alpha", "main"), Is.EqualTo(f.Initial));
    }

    [AvaloniaTest]
    public async Task TagsHaveASeparateAllChoiceAndForceLeaseCheckboxMeansForceForTagReplacement()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.Git("tag", "v1"); await f.Git("tag", "v2");
        PushDialog dialog = await f.ShowAsync(); Field<TabControl>(dialog, "PushTabs").SelectedIndex = 1; Pump(dialog);
        Assert.That(Field<AutoCompleteBox>(dialog, "PushTag").Text, Is.EqualTo("[ All ]"));
        await dialog.ExecuteAsync(dialog); Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(await f.RemoteRef("zeta", "v1", tag: true), Is.EqualTo(f.Initial)); Assert.That(await f.RemoteRef("zeta", "v2", tag: true), Is.EqualTo(f.Initial));
        await f.CommitAsync("tagged.txt", "tagged\n", "New tag target"); await f.Git("tag", "-f", "v1");
        Field<AutoCompleteBox>(dialog, "PushTag").Text = "v1";
        Field<CheckBox>(dialog, "ForcePushTags").IsChecked = true;
        Assert.That(dialog.ReadOptions().Force, Is.EqualTo(GitPushForce.Force));
        Field<TabControl>(dialog, "PushTabs").SelectedIndex = 0; Pump(dialog);
        Assert.That(Field<CheckBox>(dialog, "PushForceWithLease").IsChecked, Is.True);
        Field<TabControl>(dialog, "PushTabs").SelectedIndex = 1; Pump(dialog);
        await dialog.ExecuteAsync(dialog); Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(await f.RemoteRef("zeta", "v1", tag: true), Is.EqualTo(await f.Head()));
        Assert.That(await f.RemoteRef("zeta", "v2", tag: true), Is.EqualTo(f.Initial));
    }

    [AvaloniaTest]
    public async Task MultipleRowsOfferTrackedSelectionAndExclusivePushForceDeleteActionsWithDistinctDestinations()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.Git("branch", "feature"); await f.Git("push", "zeta", "main:stale");
        await f.Repository.FetchRemoteAsync("zeta");
        PushDialog dialog = await f.ShowAsync(); Field<TabControl>(dialog, "PushTabs").SelectedIndex = 2; Pump(dialog);
        Grid main = Row(dialog, "main"), feature = Row(dialog, "feature"), stale = Rows(dialog).Single(row => row.Tag is null);
        Button select = Field<Button>(dialog, "PushSelection"); PointerClick(select); Pump(dialog);
        MenuFlyout menu = (MenuFlyout)select.Flyout!; Assert.That(menu.IsOpen, Is.True);
        MenuItem tracked = menu.Items.OfType<MenuItem>().Single(item => item.Name == "PushselectTrackedToolStripMenuItem"); PointerClick(tracked); Pump(dialog);
        Assert.That(main.Children.OfType<CheckBox>().First().IsChecked, Is.True);
        Assert.That(feature.Children.OfType<CheckBox>().First().IsChecked, Is.Not.True);
        CheckBox[] actions = feature.Children.OfType<CheckBox>().ToArray();
        actions[0].IsChecked = true; actions[1].IsChecked = true;
        Assert.That(actions.Select(action => action.IsChecked == true), Is.EqualTo(new[] { false, true, false }));
        actions[2].IsChecked = true; Assert.That(actions.Select(action => action.IsChecked == true), Is.EqualTo(new[] { false, false, true }));
        actions[0].IsChecked = true;
        feature.Children.OfType<TextBox>().Single().Text = "mapped-feature";
        stale.Children.OfType<CheckBox>().Last().IsChecked = true;
        Assert.That(stale.Children.OfType<CheckBox>().Take(2).All(action => !action.IsVisible), Is.True);
        await dialog.ExecuteAsync(dialog); Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(await f.RemoteRef("zeta", "mapped-feature"), Is.EqualTo(f.Initial));
        Assert.That(await f.RemoteRef("zeta", "stale"), Is.Null);
        Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(f.Initial));
        Assert.That((await f.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
    }

    [AvaloniaTest]
    public async Task RejectedCurrentBranchCanPullRebaseAndRetryWhileKeepingUsefulGitOutput()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.AdvanceRemoteAsync(); await f.CommitAsync("local.txt", "local\n", "Local change");
        string before = await f.Head(); PushDialog dialog = await f.ShowAsync();
        Task push = dialog.ExecuteAsync(dialog);
        await Until(() => dialog.OwnedWindows.Any(window => window.Name == "PushRejectedConfirmation"));
        Window rejection = dialog.OwnedWindows.Single(window => window.Name == "PushRejectedConfirmation");
        Assert.That(rejection.GetVisualDescendants().OfType<AccessText>().Any(text => text.Text == "Pull with _rebase"), Is.True);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            rejection.RequestedThemeVariant = theme; Pump(rejection);
            foreach (int value in new[] { 1, 2, 3, 4 })
            {
                Button link = Field<Button>(rejection, "PushChoice" + value);
                Assert.That(link.Bounds.Width, Is.GreaterThan(500));
                Assert.That(link.GetVisualDescendants().OfType<PathIcon>().Single().Bounds.Size, Is.EqualTo(new Size(18, 18)));
            }
            Assert.That(Field<Button>(rejection, "PushChoice0").Bounds.Width, Is.EqualTo(75));
            Assert.That(Field<Button>(rejection, "PushChoice0").TranslatePoint(default, rejection)!.Value.Y, Is.GreaterThan(Field<Button>(rejection, "PushChoice4").TranslatePoint(default, rejection)!.Value.Y));
            Capture(rejection, "classic-push-rejected-" + theme + ".png");
        }
        await Answer(dialog, "PushRejectedConfirmation", 2);
        await push.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(await f.Head(), Is.Not.EqualTo(before));
        Assert.That((await f.Repository.GetLogAsync(1, all: false)).Single().Parents, Has.Count.EqualTo(1));
        Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(await f.Head()));
        Assert.That(Field<TextBox>(dialog, "PushOutput").Text, Does.Contain("[rejected]"));
        Assert.That(Field<TextBox>(dialog, "PushOutput").Text, Does.Contain("main -> main"));
        Assert.That(await f.GitText("status", "--porcelain"), Is.Empty);
    }

    [AvaloniaTest]
    public async Task StaleLeaseAndServerHookRejectionsKeepTheDialogAndDoNotOverwriteRemoteHistory()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("The private executable Git hooks require Linux."); return; }
        await using Fixture f = await Fixture.CreateAsync();
        await f.AdvanceRemoteAsync(); await f.CommitAsync("local.txt", "local\n", "Local change");
        string remote = (await f.RemoteRef("zeta", "main"))!; PushDialog dialog = await f.ShowAsync();
        Task push = dialog.ExecuteAsync(dialog); await Answer(dialog, "PushRejectedConfirmation", 4); await push;
        Assert.That(dialog.Succeeded, Is.False); Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.LastError, Does.Contain("stale info"));
        Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(remote));
        Assert.That(Field<Button>(dialog, "ExecutePush").IsEnabled, Is.True);
        await f.Repository.FetchRemoteAsync("zeta");
        string hook = Path.Combine(f.Remote("zeta"), "hooks", "pre-receive"); await File.WriteAllTextAsync(hook, "#!/bin/sh\necho private-server-rejected >&2\nexit 1\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Field<CheckBox>(dialog, "PushForceWithLease").IsChecked = true;
        await dialog.ExecuteAsync(dialog);
        Assert.That(dialog.Succeeded, Is.False); Assert.That(dialog.LastError, Does.Contain("private-server-rejected"));
        Assert.That(dialog.OwnedWindows, Is.Empty, "Server hook errors must not offer an automatic force or pull.");
        Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(remote));
        Assert.That(Field<TextBox>(dialog, "PushOutput").Text, Does.Contain("[remote rejected]"));
    }

    [AvaloniaTest]
    public async Task ForcePushOffersTheOriginalLeaseAlternativeBeforeRunningGit()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.CommitAsync("local.txt", "local\n", "Local change");
        PushDialog dialog = await f.ShowAsync(); Field<CheckBox>(dialog, "ForcePushBranches").IsChecked = true;
        Task cancel = dialog.ExecuteAsync(dialog); await Answer(dialog, "PushForceConfirmation", 0); await cancel;
        Assert.That(dialog.Attempted, Is.False); Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(f.Initial));
        Task lease = dialog.ExecuteAsync(dialog); await Answer(dialog, "PushForceConfirmation", 1); await lease;
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(Field<CheckBox>(dialog, "ForcePushBranches").IsChecked, Is.Not.True);
        Assert.That(Field<CheckBox>(dialog, "PushForceWithLease").IsChecked, Is.True);
        Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(await f.Head()));
    }

    [AvaloniaTest]
    public async Task ExplicitForceAndDeleteRefspecsRetainTheirConfirmationsAndTheirLiteralDestination()
    {
        await using Fixture f = await Fixture.CreateAsync();
        const string destination = "refs/heads/literal/λ;$()'quoted";
        PushDialog dialog = await f.ShowAsync();
        Field<AutoCompleteBox>(dialog, "PushBranch").Text = "+HEAD:" + destination;
        Assert.That(Field<AutoCompleteBox>(dialog, "PushRemoteBranch").IsEnabled, Is.False);
        Task cancelled = dialog.ExecuteAsync(dialog); await Answer(dialog, "PushForceConfirmation", 0); await cancelled;
        Assert.That(dialog.Attempted, Is.False); Assert.That(await f.RemoteRef("zeta", destination[11..]), Is.Null);
        Task push = dialog.ExecuteAsync(dialog); await Answer(dialog, "PushForceConfirmation", 1); await push;
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(Field<AutoCompleteBox>(dialog, "PushBranch").Text, Is.EqualTo("HEAD:" + destination));
        Assert.That(await f.RemoteRef("zeta", destination[11..]), Is.EqualTo(f.Initial));
        Field<CheckBox>(dialog, "PushForceWithLease").IsChecked = false;
        Field<AutoCompleteBox>(dialog, "PushBranch").Text = ":" + destination;
        Task deleteCancelled = dialog.ExecuteAsync(dialog); await Answer(dialog, "PushDeleteConfirmation", 0); await deleteCancelled;
        Assert.That(await f.RemoteRef("zeta", destination[11..]), Is.EqualTo(f.Initial));
        Task deleted = dialog.ExecuteAsync(dialog); await Answer(dialog, "PushDeleteConfirmation", 1); await deleted;
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError); Assert.That(await f.RemoteRef("zeta", destination[11..]), Is.Null);
        Assert.That(await f.Head(), Is.EqualTo(f.Initial)); Assert.That(await f.GitText("status", "--porcelain"), Is.Empty);
    }

    [AvaloniaTest]
    public async Task ClosingDuringAPrivateHookCancelsItsProcessAndPreservesBothRepositories()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("The private executable Git hook requires Linux."); return; }
        await using Fixture f = await Fixture.CreateAsync(); await f.CommitAsync("local.txt", "local\n", "Local change");
        string head = await f.Head(); string marker = Path.Combine(f.Root, "hook-started");
        string hooks = Path.Combine(f.Root, "client-hooks"); Directory.CreateDirectory(hooks);
        string hook = Path.Combine(hooks, "pre-push");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nprintf started > '" + marker.Replace("'", "'\\''", StringComparison.Ordinal) + "'\nwhile :; do sleep 1; done\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await f.Git("config", "core.hooksPath", hooks);
        PushDialog dialog = await f.ShowAsync(); Task push = dialog.ExecuteAsync(dialog);
        await Until(() => File.Exists(marker));
        Assert.That(dialog.IsBusy, Is.True); Assert.That(Field<TabControl>(dialog, "PushTabs").IsEnabled, Is.False);
        Assert.That(Field<Button>(dialog, "CancelPush").IsEnabled, Is.True);
        Click(Field<Button>(dialog, "CancelPush"));
        await push.WaitAsync(TimeSpan.FromSeconds(8)); await Until(() => !dialog.IsVisible);
        Assert.That(dialog.LastError, Is.EqualTo("Operation cancelled"));
        Assert.That(await f.Head(), Is.EqualTo(head)); Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(f.Initial));
        Assert.That(await f.GitText("status", "--porcelain"), Is.Empty);
    }

    [AvaloniaTest]
    public async Task OriginalFrenchTabsAdvancedControlsAndMultipleSelectionMenuRenderCompactlyInBothPalettes()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
        try
        {
            await using Fixture f = await Fixture.CreateAsync();
            await f.Git("branch", "feature"); await f.Git("push", "zeta", "main:remote-only"); await f.Repository.FetchRemoteAsync("zeta");
            PushDialog dialog = await f.ShowAsync();
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme; Field<TabControl>(dialog, "PushTabs").SelectedIndex = 0; Pump(dialog);
                Assert.That(Field<TabControl>(dialog, "PushTabs").Items.OfType<TabItem>().Select(tab => tab.Header), Is.EqualTo(new[] { "Pousser une branche", "Pousser les étiquettes", "Pousser plusieurs branches" }));
                if (Field<Button>(dialog, "PushShowOptions").IsVisible) { PointerClick(Field<Button>(dialog, "PushShowOptions")); }
                Pump(dialog);
                foreach (string name in new[] { "PushBranch", "PushRemoteBranch", "PushRecursiveSubmodules", "PushToRemote", "PushToUrl", "PushForceWithLease", "ForcePushBranches" })
                { Assert.That(Field<Control>(dialog, name).Bounds.Height, Is.EqualTo(24), name); }
                Assert.That(dialog.GetVisualDescendants().OfType<AccessText>().Any(text => text.Text == "R_emplacer la référence de suivi"), Is.True);
                Assert.That(dialog.GetVisualDescendants().OfType<AccessText>().Any(text => text.Text == "_Branche à pousser"), Is.True);
                Assert.That(dialog.GetVisualDescendants().OfType<AccessText>().Any(text => text.Text == "_vers/à"), Is.True);
                Assert.That(dialog.GetVisualDescendants().OfType<AccessText>().Any(text => text.Text == "_Sous-modules récursifs"), Is.True);
                Assert.That(Field<Button>(dialog, "ExecutePush").GetVisualDescendants().OfType<Image>().Single().Tag, Is.EqualTo("ArrowUp.png"));
                Capture(dialog, "classic-push-branches-fr-" + theme + ".png");
                Field<TabControl>(dialog, "PushTabs").SelectedIndex = 2; Pump(dialog);
                Assert.That(Rows(dialog).All(row => row.Bounds.Height == 24), Is.True);
                PointerClick(Field<Button>(dialog, "PushSelection")); Pump(dialog);
                MenuFlyout menu = (MenuFlyout)Field<Button>(dialog, "PushSelection").Flyout!;
                Assert.That(menu.IsOpen, Is.True); MenuItem item = menu.Items.OfType<MenuItem>().First();
                Assert.That(item.IsEffectivelyVisible, Is.True);
                Capture(TopLevel.GetTopLevel(item)!, "classic-push-multiple-menu-fr-" + theme + ".png");
                menu.Hide(); Pump(dialog); Capture(dialog, "classic-push-multiple-fr-" + theme + ".png");
            }
            Assert.That(dialog.Attempted, Is.False); Assert.That(await f.RemoteRef("zeta", "main"), Is.EqualTo(f.Initial));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private static IEnumerable<Grid> Rows(Window window) => window.GetVisualDescendants().OfType<Grid>().Where(row => row.Name?.StartsWith("PushBranchRow_", StringComparison.Ordinal) == true);
    private static Grid Row(Window window, string source) => Rows(window).Single(row => row.Tag as string == source);
    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void PointerClick(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; top.UpdateLayout();
        Point center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        top.MouseMove(center); top.MouseDown(center, MouseButton.Left); top.MouseUp(center, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static async Task Answer(PushDialog owner, string name, int value, bool remember = false)
    {
        await Until(() => owner.OwnedWindows.Any(window => window.Name == name && window.IsVisible));
        Window child = owner.OwnedWindows.Single(window => window.Name == name);
        if (remember) { Field<CheckBox>(child, "PushConfirmationRemember").IsChecked = true; }
        PointerClick(Field<Button>(child, "PushChoice" + value));
    }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "Timed out waiting for a real UI/Git operation.");
    }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Capture(TopLevel window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null, "The native push surface must actually render.");
        string directory = Path.GetFullPath("artifacts/linux/screenshots"); Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-push-ui-" + Guid.NewGuid().ToString("N"));
        public string Client => Path.Combine(Root, "client");
        public string Remote(string name) => Path.Combine(Root, name + ".git");
        public GitRepository Repository => new(Client);
        public string Initial { get; private set; } = "";
        public List<Window> Windows { get; } = [];
        private readonly GitRunner _runner = new();
        public static async Task<Fixture> CreateAsync()
        {
            Fixture f = new();
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(f.Root, "config")); Directory.CreateDirectory(f.Client);
                await f.Git("init", "-b", "main"); await f.Configure(f.Client); await f.CommitAsync("initial.txt", "initial\n", "Initial commit"); f.Initial = await f.Head();
                foreach (string remote in new[] { "alpha", "zeta" })
                {
                    Directory.CreateDirectory(f.Remote(remote)); await f.RemoteGit(remote, "init", "--bare", "-b", "main");
                    await f.Git("remote", "add", remote, f.Remote(remote));
                }
                await f.Git("push", "--set-upstream", "zeta", "main"); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        private async Task Configure(string path)
        {
            foreach (string[] arguments in new[] { new[] { "config", "user.name", "Push UI Tester" }, ["config", "user.email", "push-ui@example.com"], ["config", "commit.gpgsign", "false"], ["config", "core.hooksPath", Path.Combine(Root, "no-hooks")] })
            { GitResult result = await _runner.RunAsync(path, arguments); Assert.That(result.Success, Is.True, result.StandardError); }
        }
        public async Task<PushDialog> ShowAsync()
        {
            PushDialog dialog = new(Repository); Windows.Add(dialog); dialog.Show(); await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Pump(dialog); return dialog;
        }
        public async Task<GitResult> Git(params string[] arguments)
        { GitResult result = await _runner.RunAsync(Client, arguments); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public async Task<string> GitText(params string[] arguments) => (await Git(arguments)).StandardOutput.TrimEnd('\r', '\n');
        public async Task<string> GitText(string first, string second, string third, bool allowFailure)
        { GitResult result = await _runner.RunAsync(Client, [first, second, third]); if (!allowFailure) { Assert.That(result.Success, Is.True, result.StandardError); } return result.StandardOutput.TrimEnd('\r', '\n'); }
        public async Task<GitResult> RemoteGit(string name, params string[] arguments)
        { GitResult result = await _runner.RunAsync(Remote(name), arguments); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public async Task<string?> RemoteRef(string name, string reference, bool tag = false)
        { GitResult result = await _runner.RunAsync(Remote(name), ["rev-parse", "--verify", "--quiet", (tag ? "refs/tags/" : "refs/heads/") + reference]); return result.Success ? result.StandardOutput.Trim() : null; }
        public Task<string> Head() => GitText("rev-parse", "HEAD");
        public async Task CommitAsync(string path, string text, string message)
        { await File.WriteAllTextAsync(Path.Combine(Client, path), text); await Git("add", "--", path); await Git("commit", "-m", message); }
        public async Task AdvanceRemoteAsync()
        {
            string writer = Path.Combine(Root, "writer"); Directory.CreateDirectory(writer);
            GitResult clone = await _runner.RunAsync(writer, ["clone", "--", Remote("zeta"), "."]); Assert.That(clone.Success, Is.True, clone.StandardError); await Configure(writer);
            await File.WriteAllTextAsync(Path.Combine(writer, "remote.txt"), "remote\n");
            foreach (string[] args in new[] { new[] { "add", "--", "remote.txt" }, ["commit", "-m", "Remote change"], ["push", "origin", "main"] }) { GitResult result = await _runner.RunAsync(writer, args); Assert.That(result.Success, Is.True, result.StandardError); }
        }
        public async ValueTask DisposeAsync()
        {
            foreach (Window window in Windows.AsEnumerable().Reverse()) { foreach (Window child in window.OwnedWindows.ToArray()) { child.Close(); } window.Close(); }
            Stopwatch timer = Stopwatch.StartNew();
            while (Windows.OfType<RepositoryDialog>().Any(window => window.IsBusy) && timer.Elapsed < TimeSpan.FromSeconds(8))
            { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            foreach (Window window in Windows) { window.Close(); }
            Dispatcher.UIThread.RunJobs(); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
            if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
        }
    }
}

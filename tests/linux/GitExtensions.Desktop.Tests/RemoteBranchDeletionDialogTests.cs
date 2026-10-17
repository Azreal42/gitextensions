using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RemoteBranchDeletionDialogTests
{
    [AvaloniaTest]
    public async Task OriginalDefaultsExcludeSymbolicHeadAndRenderTheRealTrackingCandidatesInBothThemes()
    {
        using Fixture f = await Fixture.CreateAsync();
        const string remoteBranch = "feature/λ;$()'quoted";
        await f.Repository.CreateBranchAsync(remoteBranch, checkout: false);
        await f.CreateRemoteAsync();
        await f.GitAsync("remote", "set-head", "origin", "main");
        string[] locals = Enumerable.Range(0, 10).Select(index => $"tracking/{index:00}-λ").ToArray();
        foreach (string local in locals) { await TrackAsync(f, local, "origin", remoteBranch); }
        string head = await f.Repository.ResolveRevisionAsync("HEAD");

        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            DeleteRemoteBranchDialog dialog = await ShowAsync(f, "refs/remotes/origin/main", theme);
            try
            {
                BranchSelectionComboBox selection = Field<BranchSelectionComboBox>(dialog, "DeleteRemoteBranchSelection");
                CheckBox remote = Field<CheckBox>(dialog, "DeleteRemoteBranches"), tracking = Field<CheckBox>(dialog, "DeleteLocalTrackingBranches");
                Button delete = Field<Button>(dialog, "DeleteSelectedRemoteBranches");
                Assert.Multiple(() =>
                {
                    Assert.That(dialog.Title, Is.EqualTo("Delete branch"));
                    Assert.That(dialog.SelectionText, Is.EqualTo("origin/main"));
                    Assert.That(dialog.SelectedBranches.Select(branch => branch.Reference.Name), Is.EqualTo(new[] { "refs/remotes/origin/main" }));
                    Assert.That(selection.Choices.Any(reference => reference.SymbolicTarget is not null || reference.ShortName == "origin/HEAD"), Is.False);
                    Assert.That(remote.IsChecked, Is.Not.True);
                    Assert.That(tracking.IsChecked, Is.Not.True);
                    Assert.That(tracking.IsEnabled, Is.False);
                    Assert.That(delete.IsEnabled, Is.False);
                    Assert.That(delete.IsDefault, Is.True);
                    Assert.That(dialog.HasRepositoryChanges, Is.False);
                });
                dialog.SelectionText = "origin/" + remoteBranch;
                Assert.That(tracking.IsEnabled, Is.True);
                Assert.That(delete.IsEnabled, Is.False, "Tracking alone cannot authorize a remote deletion.");
                tracking.IsChecked = true;
                Assert.That(delete.IsEnabled, Is.False);
                remote.IsChecked = true;
                Assert.That(delete.IsEnabled, Is.True);
                string candidates = Field<TextBlock>(dialog, "LocalTrackingBranchCandidates").Text!;
                Assert.That(candidates, Does.StartWith("Local tracking branche(s) candidate to deletion:\n"));
                foreach (string local in locals.Take(8)) { Assert.That(candidates, Does.Contain(" - " + local)); }
                Assert.That(candidates, Does.Not.Contain(locals[8]).And.Not.Contain(locals[9]).And.EndWith("and 2 more..."));
                Capture(dialog, "delete-remote-tracking-" + theme + ".png");
                AssertInside(dialog, selection, remote, tracking, delete, Field<TextBlock>(dialog, "LocalTrackingBranchCandidates"));
                Assert.That(new[] { remote.Bounds.Height, tracking.Bounds.Height }, Is.All.EqualTo(24));
                Assert.That(Field<Button>(dialog, "SelectMultipleBranches").Content, Is.TypeOf<Image>());
                Assert.That(((Image)Field<Button>(dialog, "SelectMultipleBranches").Content!).Tag, Is.EqualTo("SelectBranch.png"));
                dialog.SelectionText = "origin/main";
                Assert.That(tracking.IsChecked, Is.Not.True);
                Assert.That(tracking.IsEnabled, Is.False);
                Assert.That(Field<TextBlock>(dialog, "LocalTrackingBranchCandidates").IsVisible, Is.False);
            }
            finally { dialog.Close(); }
        }
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That((await f.Repository.GetReferencesAsync()).Count(reference => reference.ShortName.StartsWith("tracking/", StringComparison.Ordinal)), Is.EqualTo(10));
    }

    [AvaloniaTest]
    public async Task EditableSpaceSelectionAndCheckedPickerPreserveLiteralNamesAndCloseWithTheirChecks()
    {
        using Fixture f = await Fixture.CreateAsync();
        const string first = "feature/λ;$()'quoted", second = "other/deep/path";
        await f.Repository.CreateBranchAsync(first, checkout: false);
        await f.Repository.CreateBranchAsync(second, checkout: false);
        await f.CreateRemoteAsync();
        DeleteRemoteBranchDialog dialog = await ShowAsync(f, "origin/" + first);
        try
        {
            BranchSelectionComboBox selection = Field<BranchSelectionComboBox>(dialog, "DeleteRemoteBranchSelection");
            dialog.SelectionText = " origin/" + first + "  origin/" + second + " origin/" + first + " ";
            Field<CheckBox>(dialog, "DeleteRemoteBranches").IsChecked = true;
            Assert.That(dialog.SelectedBranches.Select(branch => branch.BranchName), Is.EqualTo(new[] { first, second }));
            Assert.That(selection.UnknownNames, Is.Empty);
            dialog.SelectionText += "unknown/branch";
            Assert.That(selection.UnknownNames, Is.EqualTo(new[] { "unknown/branch" }));
            Assert.That(Field<Button>(dialog, "DeleteSelectedRemoteBranches").IsEnabled, Is.False);
            dialog.SelectionText = "origin/" + first + " origin/" + second;
            Pointer(dialog, "SelectMultipleBranches");
            await Until(() => dialog.OwnedWindows.OfType<SelectMultipleBranchesDialog>().Any());
            SelectMultipleBranchesDialog picker = dialog.OwnedWindows.OfType<SelectMultipleBranchesDialog>().Single();
            Assert.That(picker.SelectedBranches.Select(branch => branch.ShortName), Is.EqualTo(new[] { "origin/" + first, "origin/" + second }));
            CheckBox firstChoice = Choices(picker).Single(choice => choice.Tag as string == "refs/remotes/origin/" + first);
            PushProcessTestDriver.Pointer(firstChoice);
            CheckBox main = Choices(picker).Single(choice => choice.Tag as string == "refs/remotes/origin/main");
            PushProcessTestDriver.Pointer(main);
            Assert.That(Choices(picker).All(choice => choice.Bounds.Height == 20), Is.True);
            Capture(picker, "select-multiple-remote-branches-Light.png");
            AssertInside(picker, Field<Button>(picker, "AcceptBranchSelection"));
            Pointer(picker, "AcceptBranchSelection");
            await Until(() => !picker.IsVisible);
            Assert.That(dialog.SelectedBranches.Select(branch => branch.BranchName), Is.EqualTo(new[] { "main", second }));
            Pointer(dialog, "SelectMultipleBranches");
            await Until(() => dialog.OwnedWindows.OfType<SelectMultipleBranchesDialog>().Any());
            picker = dialog.OwnedWindows.OfType<SelectMultipleBranchesDialog>().Single();
            picker.RequestedThemeVariant = ThemeVariant.Dark;
            PushProcessTestDriver.Pointer(Choices(picker).Single(choice => choice.Tag as string == "refs/remotes/origin/" + first));
            Capture(picker, "select-multiple-remote-branches-Dark.png");
            picker.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); dialog.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await Until(() => !picker.IsVisible);
            Assert.That(dialog.SelectedBranches.Select(branch => branch.BranchName), Is.EquivalentTo(new[] { first, "main", second }),
                "The Windows picker retains checks when closed with Escape as well as OK.");
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task UnmergedConfirmationDefaultsToNoAndOnlyAnExplicitYesDeletesTheActualRemoteBranch()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Repository.CreateBranchAsync("unmerged");
        await f.ChangeAsync("unmerged\n", "Unmerged remote work");
        await f.Repository.CheckoutAsync("main");
        string server = await f.CreateRemoteAsync();
        new ProcessDialogPreferencesStore().Save(new(true));
        DeleteRemoteBranchDialog dialog = await ShowAsync(f, "origin/unmerged");
        try
        {
            Assert.That(dialog.SelectedBranches.Single().IsMerged, Is.False);
            Field<CheckBox>(dialog, "DeleteRemoteBranches").IsChecked = true;
            Pointer(dialog, "DeleteSelectedRemoteBranches");
            await Until(() => dialog.OwnedWindows.Any());
            Window question = dialog.OwnedWindows.Single();
            Assert.That(question.Title, Is.EqualTo("Delete remote branches"));
            Assert.That(CaptionButton(question, "No").IsDefault, Is.True);
            Assert.That(CaptionButton(question, "Yes").IsDefault, Is.False);
            Assert.That(question.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("At least one remote branch is unmerged.", StringComparison.Ordinal) == true), Is.True);
            PushProcessTestDriver.Pointer(CaptionButton(question, "No"));
            await Idle(dialog);
            Assert.That(dialog.LastDeletion, Is.Null);
            Assert.That(dialog.LastProcess, Is.Null);
            Assert.That(await RemoteRefAsync(server, "unmerged"), Is.Not.Null);
            Assert.That(dialog.HasRepositoryChanges, Is.False);
            Pointer(dialog, "DeleteSelectedRemoteBranches");
            await Until(() => dialog.OwnedWindows.Any());
            PushProcessTestDriver.Pointer(CaptionButton(dialog.OwnedWindows.Single(), "Yes"));
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.LastDeletion!.Success, Is.True);
            Assert.That(dialog.LastProcess!.Success, Is.True);
            Assert.That(dialog.IsVisible, Is.False);
            Assert.That(await RemoteRefAsync(server, "unmerged"), Is.Null);
            Assert.That((await f.Repository.GetReferencesAsync()).Any(reference => reference.Name == "refs/heads/unmerged"), Is.True);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingTheParentWhileUnmergedConfirmationIsOpenDismissesItWithoutStartingGit()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Repository.CreateBranchAsync("unmerged");
        await f.ChangeAsync("unmerged\n", "Unmerged work to retain");
        await f.Repository.CheckoutAsync("main");
        string server = await f.CreateRemoteAsync();
        DeleteRemoteBranchDialog dialog = await ShowAsync(f, "origin/unmerged");
        try
        {
            Field<CheckBox>(dialog, "DeleteRemoteBranches").IsChecked = true;
            Pointer(dialog, "DeleteSelectedRemoteBranches");
            await Until(() => dialog.OwnedWindows.Any());
            Window question = dialog.OwnedWindows.Single();
            Assert.That(CaptionButton(question, "No").IsDefault, Is.True);
            dialog.Close();
            await Until(() => !dialog.IsBusy && !dialog.IsVisible && !question.IsVisible);
            Assert.That(dialog.LastProcess, Is.Null);
            Assert.That(dialog.LastDeletion, Is.Null);
            Assert.That(dialog.HasRepositoryChanges, Is.False);
            Assert.That(dialog.OwnedWindows, Is.Empty);
            Assert.That(await RemoteRefAsync(server, "unmerged"), Is.Not.Null);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task SuccessfulMultipleRemotesOfferOneLocalDialogAndCancellingItPreservesAllLocalBranches()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Repository.CreateBranchAsync("topic", checkout: false);
        string origin = await f.CreateRemoteAsync(), zeta = await AddRemoteAsync(f, "zeta");
        await TrackAsync(f, "follow-origin", "origin", "topic");
        await TrackAsync(f, "follow-zeta", "zeta", "topic");
        new ProcessDialogPreferencesStore().Save(new(true));
        DeleteRemoteBranchDialog dialog = await ShowAsync(f);
        try
        {
            dialog.SelectionText = "origin/topic zeta/topic";
            Field<CheckBox>(dialog, "DeleteRemoteBranches").IsChecked = true;
            Field<CheckBox>(dialog, "DeleteLocalTrackingBranches").IsChecked = true;
            Pointer(dialog, "DeleteSelectedRemoteBranches");
            await UntilProcessAsync(dialog, () => dialog.OwnedWindows.OfType<DeleteBranchDialog>().Any(local => !local.IsBusy));
            DeleteBranchDialog local = dialog.OwnedWindows.OfType<DeleteBranchDialog>().Single();
            Assert.That(local.Owner, Is.SameAs(dialog));
            Assert.That(local.ReadOptions().Names, Is.EquivalentTo(new[] { "follow-origin", "follow-zeta" }));
            Assert.That(dialog.LastDeletion!.Batches.Select(batch => batch.Remote), Is.EqualTo(new[] { "origin", "zeta" }));
            Assert.That(dialog.LastDeletion.Success, Is.True);
            Pointer(local, "CancelDeleteBranches");
            await Idle(dialog);
            Assert.That(dialog.OwnedWindows, Is.Empty);
            Assert.That(dialog.IsVisible, Is.False);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(await RemoteRefAsync(origin, "topic"), Is.Null);
            Assert.That(await RemoteRefAsync(zeta, "topic"), Is.Null);
            Assert.That((await f.Repository.GetReferencesAsync()).Where(reference => reference.IsBranch).Select(reference => reference.ShortName),
                Is.SupersetOf(new[] { "main", "topic", "follow-origin", "follow-zeta" }));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task LocalCleanupRechecksTrackingAfterTheCompletedProcessInsteadOfDeletingRetargetedOrRecreatedNames()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Repository.CreateBranchAsync("topic", checkout: false);
        await f.Repository.CreateBranchAsync("other", checkout: false);
        string server = await f.CreateRemoteAsync();
        await TrackAsync(f, "still-tracking", "origin", "topic");
        await TrackAsync(f, "retargeted", "origin", "topic");
        await TrackAsync(f, "recreated", "origin", "topic");
        new ProcessDialogPreferencesStore().Save(new(false));
        DeleteRemoteBranchDialog dialog = await ShowAsync(f, "origin/topic");
        try
        {
            Field<CheckBox>(dialog, "DeleteRemoteBranches").IsChecked = true;
            Field<CheckBox>(dialog, "DeleteLocalTrackingBranches").IsChecked = true;
            Assert.That(dialog.SelectedBranches.Single().TrackingLocalBranches,
                Is.EquivalentTo(new[] { "still-tracking", "retargeted", "recreated" }));
            Pointer(dialog, "DeleteSelectedRemoteBranches");
            await Until(() => dialog.OwnedWindows.OfType<GitProcessDialog>().Any(process => !process.IsRunning));
            GitProcessDialog process = dialog.OwnedWindows.OfType<GitProcessDialog>().Single();
            Assert.That(process.Outcome!.Success, Is.True);
            Assert.That(Field<CheckBox>(process, "KeepDialogOpen").IsChecked, Is.True);
            Assert.That(dialog.IsBusy, Is.True);
            Assert.That(dialog.OwnedWindows.OfType<DeleteBranchDialog>(), Is.Empty);
            Assert.That(await RemoteRefAsync(server, "topic"), Is.Null);

            // This can be an external Git operation while the user reads the process output.
            await f.GitAsync("config", "branch.retargeted.merge", "refs/heads/other");
            await f.Repository.DeleteBranchesAsync(new(["recreated"]));
            await f.Repository.CreateBranchAsync("recreated", checkout: false);
            Assert.That((await f.Repository.GetReferencesAsync()).Single(reference => reference.Name == "refs/heads/recreated").Upstream,
                Is.Null.Or.Empty, "The replacement branch has no tracking relationship with the deleted remote branch.");

            Pointer(process, "Ok");
            await UntilProcessAsync(dialog, () => dialog.OwnedWindows.OfType<DeleteBranchDialog>().Any(local => !local.IsBusy));
            DeleteBranchDialog cleanup = dialog.OwnedWindows.OfType<DeleteBranchDialog>().Single();
            Assert.That(cleanup.ReadOptions().Names, Is.EqualTo(new[] { "still-tracking" }),
                "A successful remote deletion cannot authorize deletion of a name whose tracking was subsequently changed or removed.");
            Pointer(cleanup, "CancelDeleteBranches");
            await Idle(dialog);
            Assert.That(dialog.IsVisible, Is.False);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That((await f.Repository.GetReferencesAsync()).Where(reference => reference.IsBranch).Select(reference => reference.ShortName),
                Is.SupersetOf(new[] { "still-tracking", "retargeted", "recreated" }));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task AbortingTheSecondRemoteKeepsOnlyItsBranchSelectedAndRetryDoesNotRepeatTheSuccessfulDeletion()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("An executable private pre-push hook requires Linux."); return; }
        using Fixture f = await Fixture.CreateAsync();
        await f.Repository.CreateBranchAsync("topic", checkout: false);
        string origin = await f.CreateRemoteAsync(), zeta = await AddRemoteAsync(f, "zeta");
        await TrackAsync(f, "origin-local-to-keep", "origin", "topic");
        await TrackAsync(f, "zeta-local-to-keep", "zeta", "topic");
        string marker = f.Output("second-delete-started"), release = f.Output("second-delete-release");
        await WriteHookAsync(f, f.Repository.Path, "pre-push", "if test \"$1\" = zeta && test ! -f " + ShellQuote(release) + "; then\n"
            + " printf started > " + ShellQuote(marker) + "\n while test ! -f " + ShellQuote(release) + "; do sleep 0.02; done\nfi\n", "partial-cancel-hooks");
        new ProcessDialogPreferencesStore().Save(new(false));
        DeleteRemoteBranchDialog dialog = await ShowAsync(f);
        try
        {
            dialog.SelectionText = "origin/topic zeta/topic";
            Field<CheckBox>(dialog, "DeleteRemoteBranches").IsChecked = true;
            Field<CheckBox>(dialog, "DeleteLocalTrackingBranches").IsChecked = true;
            Pointer(dialog, "DeleteSelectedRemoteBranches");
            await Until(() => System.IO.File.Exists(marker));
            Assert.That(await RemoteRefAsync(origin, "topic"), Is.Null, "The first server transaction has already succeeded.");
            Assert.That(await RemoteRefAsync(zeta, "topic"), Is.Not.Null, "The second transaction is still held before its native push.");
            GitProcessDialog process = dialog.OwnedWindows.OfType<GitProcessDialog>().Single();
            Assert.That(process.IsRunning, Is.True);
            Pointer(process, "Abort");
            await Until(() => !dialog.IsBusy && !process.IsRunning && !process.IsVisible);
            Assert.That(dialog.IsVisible, Is.True, "Aborting the process keeps the branch selection window available for a retry.");
            Assert.That(dialog.LastProcess!.WasCancelled, Is.True);
            Assert.That(dialog.LastProcess.OwnerCloseRequested, Is.False);
            Assert.That(dialog.LastDeletion!.Cancelled, Is.True);
            Assert.That(dialog.LastDeletion.Batches.Select(batch => batch.Status), Is.EqualTo(new[]
                { GitRemoteBranchDeletionBatchStatus.Succeeded, GitRemoteBranchDeletionBatchStatus.Cancelled }));
            Assert.That(dialog.SelectionText, Is.EqualTo("zeta/topic"));
            Assert.That(dialog.SelectedBranches.Select(branch => branch.Reference.ShortName), Is.EqualTo(new[] { "zeta/topic" }));
            Assert.That(dialog.OwnedWindows, Is.Empty, "Cancellation must not offer local cleanup for an earlier successful batch.");
            Assert.That((await f.Repository.GetReferencesAsync()).Where(reference => reference.IsBranch).Select(reference => reference.ShortName),
                Is.SupersetOf(new[] { "origin-local-to-keep", "zeta-local-to-keep" }));

            await System.IO.File.WriteAllTextAsync(release, "release\n");
            System.IO.File.Delete(Path.Combine(f.Output("partial-cancel-hooks"), "pre-push"));
            new ProcessDialogPreferencesStore().Save(new(true));
            Field<CheckBox>(dialog, "DeleteLocalTrackingBranches").IsChecked = false;
            Assert.That(Field<Button>(dialog, "DeleteSelectedRemoteBranches").IsEnabled, Is.True);
            Pointer(dialog, "DeleteSelectedRemoteBranches");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.LastProcess!.Success, Is.True);
            Assert.That(dialog.LastDeletion!.Success, Is.True);
            Assert.That(dialog.LastDeletion.Batches.Select(batch => batch.Remote), Is.EqualTo(new[] { "zeta" }));
            Assert.That(dialog.IsVisible, Is.False);
            Assert.That(await RemoteRefAsync(origin, "topic"), Is.Null);
            Assert.That(await RemoteRefAsync(zeta, "topic"), Is.Null);
        }
        finally
        {
            await System.IO.File.WriteAllTextAsync(release, "release\n");
            await CloseAsync(dialog);
        }
    }

    [AvaloniaTest]
    public async Task PartiallyRejectedDeletionOffersCleanupOnlyForSuccessfulRemoteAndKeepsTheFailureSelected()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("An executable private server hook requires Linux."); return; }
        using Fixture f = await Fixture.CreateAsync();
        await f.Repository.CreateBranchAsync("topic", checkout: false);
        string origin = await f.CreateRemoteAsync(), zeta = await AddRemoteAsync(f, "zeta");
        await TrackAsync(f, "accepted-local", "origin", "topic");
        await TrackAsync(f, "rejected-local", "zeta", "topic");
        await WriteHookAsync(f, zeta, "pre-receive", "printf 'private deletion rejected\\n' >&2\nexit 1\n", "reject-hooks");
        new ProcessDialogPreferencesStore().Save(new(true));
        DeleteRemoteBranchDialog dialog = await ShowAsync(f);
        try
        {
            dialog.SelectionText = "origin/topic zeta/topic";
            Field<CheckBox>(dialog, "DeleteRemoteBranches").IsChecked = true;
            Field<CheckBox>(dialog, "DeleteLocalTrackingBranches").IsChecked = true;
            Pointer(dialog, "DeleteSelectedRemoteBranches");
            await UntilProcessAsync(dialog, () => dialog.OwnedWindows.OfType<DeleteBranchDialog>().Any(local => !local.IsBusy));
            DeleteBranchDialog local = dialog.OwnedWindows.OfType<DeleteBranchDialog>().Single();
            Assert.That(local.ReadOptions().Names, Is.EqualTo(new[] { "accepted-local" }));
            Assert.That(dialog.LastDeletion!.Batches.Select(batch => batch.Status), Is.EqualTo(new[]
                { GitRemoteBranchDeletionBatchStatus.Succeeded, GitRemoteBranchDeletionBatchStatus.Failed }));
            Pointer(local, "DeleteBranches");
            await Until(() => local.OwnedWindows.Any());
            PushProcessTestDriver.Pointer(local.OwnedWindows.Single().GetVisualDescendants().OfType<Button>().Single(button => button.Content as string != "Cancel"));
            await Idle(dialog);
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(dialog.HasRepositoryChanges, Is.True);
            Assert.That(dialog.LastError, Does.Contain("private deletion rejected"));
            Assert.That(dialog.SelectionText, Is.EqualTo("zeta/topic"));
            Assert.That(await RemoteRefAsync(origin, "topic"), Is.Null);
            Assert.That(await RemoteRefAsync(zeta, "topic"), Is.Not.Null);
            Assert.That((await f.Repository.GetReferencesAsync()).Any(reference => reference.Name == "refs/heads/accepted-local"), Is.False);
            Assert.That((await f.Repository.GetReferencesAsync()).Any(reference => reference.Name == "refs/heads/rejected-local"), Is.True);
            Assert.That(dialog.OwnedWindows, Is.Empty);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task ClearingRemoteAuthorizationAndTypingAnUnknownNameCannotStartAPush()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Repository.CreateBranchAsync("topic", checkout: false);
        string server = await f.CreateRemoteAsync();
        DeleteRemoteBranchDialog dialog = await ShowAsync(f, "origin/topic");
        try
        {
            Button delete = Field<Button>(dialog, "DeleteSelectedRemoteBranches");
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle(dialog);
            Assert.That(dialog.LastProcess, Is.Null);
            Field<CheckBox>(dialog, "DeleteRemoteBranches").IsChecked = true;
            dialog.SelectionText = "origin/topic unknown/branch";
            Assert.That(delete.IsEnabled, Is.False);
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle(dialog);
            Assert.That(dialog.LastProcess, Is.Null);
            Assert.That(dialog.LastDeletion, Is.Null);
            Assert.That(await RemoteRefAsync(server, "topic"), Is.Not.Null);
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task MultipleFallbackPushUrlsAreRefusedBeforeAnyPushAndCannotOfferLocalCleanup()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("A private executable push observer requires Linux."); return; }
        using Fixture f = await Fixture.CreateAsync();
        await f.Repository.CreateBranchAsync("topic", checkout: false);
        string first = await f.CreateRemoteAsync(), second = f.Output("second-fallback.git");
        await f.GitAsync("clone", "--bare", f.Repository.Path, second);
        await f.GitAsync("config", "--add", "remote.origin.url", second);
        await TrackAsync(f, "local-tracking-to-keep", "origin", "topic");
        string attempted = f.Output("fallback-push-attempted");
        await WriteHookAsync(f, f.Repository.Path, "pre-push", "printf invoked >> " + ShellQuote(attempted) + "\nexit 0\n", "fallback-observer-hooks");
        string? firstBefore = await RemoteRefAsync(first, "topic"), secondBefore = await RemoteRefAsync(second, "topic");
        string localBefore = await f.Repository.ResolveRevisionAsync("refs/heads/local-tracking-to-keep");
        Assert.That((await f.GitAsync("remote", "get-url", "--push", "--all", "origin")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            Is.EqualTo(new[] { first, second }), "Native Git will push to both remote.url values when no pushurl is set.");
        new ProcessDialogPreferencesStore().Save(new(false));
        DeleteRemoteBranchDialog dialog = await ShowAsync(f, "origin/topic");
        try
        {
            Assert.That(dialog.SelectedBranches.Single().ExpectedPushUrls, Is.EqualTo(new[] { first, second }));
            Field<CheckBox>(dialog, "DeleteRemoteBranches").IsChecked = true;
            CheckBox tracking = Field<CheckBox>(dialog, "DeleteLocalTrackingBranches");
            Assert.That(tracking.IsEnabled, Is.True); tracking.IsChecked = true;
            Pointer(dialog, "DeleteSelectedRemoteBranches");
            await Until(() => dialog.OwnedWindows.OfType<GitProcessDialog>().Any(process => !process.IsRunning));
            GitProcessDialog process = dialog.OwnedWindows.OfType<GitProcessDialog>().Single();
            Assert.That(process.Outcome!.Success, Is.False);
            Assert.That(process.Outcome.Error, Is.Not.Null);
            Assert.That(process.Outcome.Result, Is.Null, "Validation failed before starting any native push transaction.");
            await VisibleProcessNoticeAsync(process);
            Assert.That(Field<TextBlock>(process, "ProcessNotice").Text, Does.Contain("push").IgnoreCase.And.Contain("origin"));
            Assert.That(File.Exists(attempted), Is.False, "Neither server may be contacted by a push before the whole selection is validated.");
            Pointer(process, "Ok"); await Idle(dialog);
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(dialog.LastError, Does.Contain("push").IgnoreCase.And.Contain("origin"));
            Assert.That(dialog.LastDeletion, Is.Null);
            Assert.That(dialog.SelectionText, Is.EqualTo("origin/topic"));
            Assert.That(dialog.SelectedBranches.Select(branch => branch.Reference.ShortName), Is.EqualTo(new[] { "origin/topic" }));
            Assert.That(dialog.OwnedWindows, Is.Empty, "A rejected multi-destination push cannot authorize deletion of local tracking branches.");
            Assert.That(dialog.HasRepositoryChanges, Is.False);
            Assert.That(await RemoteRefAsync(first, "topic"), Is.EqualTo(firstBefore));
            Assert.That(await RemoteRefAsync(second, "topic"), Is.EqualTo(secondBefore));
            Assert.That(await f.Repository.ResolveRevisionAsync("refs/heads/local-tracking-to-keep"), Is.EqualTo(localBefore));
            Assert.That(File.Exists(attempted), Is.False);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task PushInsteadOfChangedAfterTheWindowOpenedRefusesTheNewDestinationWithoutAnyPushOrLocalCleanup()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("A private executable push observer requires Linux."); return; }
        using Fixture f = await Fixture.CreateAsync();
        await f.Repository.CreateBranchAsync("topic", checkout: false);
        string first = await f.CreateRemoteAsync(), second = f.Output("rewritten-push.git");
        await f.GitAsync("clone", "--bare", f.Repository.Path, second);
        await TrackAsync(f, "rewrite-tracking-to-keep", "origin", "topic");
        string attempted = f.Output("rewritten-push-attempted");
        await WriteHookAsync(f, f.Repository.Path, "pre-push", "printf invoked >> " + ShellQuote(attempted) + "\nexit 0\n", "rewrite-observer-hooks");
        string? firstBefore = await RemoteRefAsync(first, "topic"), secondBefore = await RemoteRefAsync(second, "topic");
        new ProcessDialogPreferencesStore().Save(new(false));
        DeleteRemoteBranchDialog dialog = await ShowAsync(f, "origin/topic");
        try
        {
            Assert.That(dialog.SelectedBranches.Single().ExpectedPushUrls, Is.EqualTo(new[] { first }));
            Field<CheckBox>(dialog, "DeleteRemoteBranches").IsChecked = true;
            Field<CheckBox>(dialog, "DeleteLocalTrackingBranches").IsChecked = true;
            await f.GitAsync("config", "url." + second + ".pushInsteadOf", first);
            Assert.That((await f.GitAsync("config", "--get", "remote.origin.url")).StandardOutput.TrimEnd('\r', '\n'), Is.EqualTo(first),
                "The raw URL is unchanged; the guard must re-read Git's effective push destination.");
            Assert.That((await f.GitAsync("remote", "get-url", "--push", "--all", "origin")).StandardOutput.TrimEnd('\r', '\n'), Is.EqualTo(second));
            Pointer(dialog, "DeleteSelectedRemoteBranches");
            await Until(() => dialog.OwnedWindows.OfType<GitProcessDialog>().Any(process => !process.IsRunning));
            GitProcessDialog process = dialog.OwnedWindows.OfType<GitProcessDialog>().Single();
            Assert.That(process.Outcome!.Error, Is.Not.Null);
            Assert.That(process.Outcome.Success, Is.False);
            Assert.That(process.Outcome.Result, Is.Null);
            await VisibleProcessNoticeAsync(process);
            Assert.That(Field<TextBlock>(process, "ProcessNotice").Text, Does.Contain("destination").IgnoreCase.And.Contain("changed").And.Contain("origin"));
            Pointer(process, "Ok"); await Idle(dialog);
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(dialog.LastError, Does.Contain("destination").IgnoreCase.And.Contain("changed"));
            Assert.That(dialog.LastDeletion, Is.Null);
            Assert.That(dialog.SelectionText, Is.EqualTo("origin/topic"));
            Assert.That(dialog.SelectedBranches.Single().ExpectedPushUrls, Is.EqualTo(new[] { second }), "The retained selection must be refreshed after reporting the changed destination.");
            Assert.That(dialog.OwnedWindows, Is.Empty);
            Assert.That(dialog.HasRepositoryChanges, Is.False);
            Assert.That(await RemoteRefAsync(first, "topic"), Is.EqualTo(firstBefore));
            Assert.That(await RemoteRefAsync(second, "topic"), Is.EqualTo(secondBefore));
            Assert.That((await f.Repository.GetReferencesAsync()).Any(reference => reference.Name == "refs/heads/rewrite-tracking-to-keep"), Is.True);
            Assert.That(File.Exists(attempted), Is.False, "A URL rewrite mismatch must be rejected before the client even executes pre-push.");
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public Task ClosingTheProcessOwnerDuringANativeHookDrainsItsActorsAndNeverOpensLocalCleanup()
        => ClosingDuringNativeHookAsync(closeOuterOwner: false);

    [AvaloniaTest]
    public Task ClosingTheOuterOwnerDuringANativeHookDrainsItsActorsAndNeverOpensLocalCleanup()
        => ClosingDuringNativeHookAsync(closeOuterOwner: true);

    private static async Task ClosingDuringNativeHookAsync(bool closeOuterOwner)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private hook actors and procfs require Linux."); return; }
        using Fixture f = await Fixture.CreateAsync();
        await f.Repository.CreateBranchAsync("topic", checkout: false);
        string server = await f.CreateRemoteAsync();
        await TrackAsync(f, "local-to-keep", "origin", "topic");
        string actors = f.Output("delete-hook-actors"), release = f.Output("delete-hook-release");
        await WriteHookAsync(f, f.Repository.Path, "pre-push", "sleep 60 &\nchild=$!\nprintf '%s %s\\n' \"$$\" \"$child\" > " + ShellQuote(actors)
            + "\nwhile test ! -f " + ShellQuote(release) + "; do sleep 0.02; done\nwait\n", "cancel-hooks");
        Window owner = new() { Width = 700, Height = 500 }; owner.Show();
        DeleteRemoteBranchDialog dialog = new(f.Repository, "origin/topic");
        Task shown = dialog.ShowDialog(owner);
        int[] pids = [];
        try
        {
            await dialog.InitializeAsync();
            Field<CheckBox>(dialog, "DeleteRemoteBranches").IsChecked = true;
            Field<CheckBox>(dialog, "DeleteLocalTrackingBranches").IsChecked = true;
            Pointer(dialog, "DeleteSelectedRemoteBranches");
            await Until(() => System.IO.File.Exists(actors) && System.IO.File.ReadAllText(actors).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length == 2);
            pids = (await System.IO.File.ReadAllTextAsync(actors)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            GitProcessDialog process = dialog.OwnedWindows.OfType<GitProcessDialog>().Single();
            Assert.That(process.IsRunning, Is.True);
            if (closeOuterOwner) { owner.Close(); } else { dialog.Close(); }
            await Until(() => !dialog.IsBusy && !process.IsRunning && !dialog.IsVisible);
            await shown.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.That(pids.Any(ActorAlive), Is.False, "The actual hook and child must have exited before the owner returns.");
            Assert.That(dialog.LastProcess, Is.Not.Null);
            Assert.That(dialog.LastProcess!.WasCancelled, Is.True);
            Assert.That(dialog.LastProcess.WorkflowStopRequested && dialog.LastProcess.OwnerCloseRequested, Is.True);
            Assert.That(PushProcessTestDriver.Windows(owner).OfType<DeleteBranchDialog>(), Is.Empty);
            Assert.That(dialog.OwnedWindows.OfType<DeleteBranchDialog>(), Is.Empty);
            Assert.That(await RemoteRefAsync(server, "topic"), Is.Not.Null);
            Assert.That((await f.Repository.GetReferencesAsync()).Any(reference => reference.Name == "refs/heads/local-to-keep"), Is.True);
            if (closeOuterOwner) { Assert.That(owner.IsVisible, Is.False); }
        }
        finally
        {
            await System.IO.File.WriteAllTextAsync(release, "release\n");
            await CloseAsync(dialog); owner.Close();
            foreach (int pid in pids.Where(ActorAlive))
            {
                try { using Process process = Process.GetProcessById(pid); process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
            }
        }
    }

    [AvaloniaTest]
    public async Task BrowserRevisionAndReferenceWorkflowOpenTheSameRemoteDialogWithTheSelectedBranch()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Repository.CreateBranchAsync("topic", checkout: false);
        await f.CreateRemoteAsync();
        MainWindow main = new() { Width = 1280, Height = 800 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Repository.Path);
            await Until(() => !main.IsBusy);
            ListBox history = Field<ListBox>(main, "RevisionHistory");
            history.SelectedIndex = 0;
            await Until(() => !main.IsBusy);
            ContextMenu context = history.ContextMenu!; context.Open(history); Pump(main);
            MenuItem delete = context.Items.OfType<MenuItem>().Single(item => item.Name == "RevisionDeleteBranch");
            MenuItem remote = delete.Items.OfType<MenuItem>().Single(item => item.Tag is GitReference reference && reference.ShortName == "origin/topic");
            remote.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); context.Close();
            await Until(() => main.OwnedWindows.OfType<DeleteRemoteBranchDialog>().Any(dialog => !dialog.IsBusy));
            DeleteRemoteBranchDialog fromRevision = main.OwnedWindows.OfType<DeleteRemoteBranchDialog>().Single();
            Assert.That(fromRevision.SelectionText, Is.EqualTo("origin/topic"));
            fromRevision.Close(); await Until(() => !main.IsBusy);
            MenuItem commands = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Commands");
            MenuItem openReferences = commands.Items.OfType<MenuItem>().Single(item => item.Header as string == "Branches and tags…");
            openReferences.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => main.OwnedWindows.Any());
            Window workflow = main.OwnedWindows.Single(); Pump(workflow);
            ListBox references = workflow.GetVisualDescendants().OfType<ListBox>().Single(list => list.Items.OfType<GitReference>().Any());
            references.SelectedItem = references.Items.OfType<GitReference>().Single(reference => reference.ShortName == "origin/topic");
            Button openDelete = workflow.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Delete…");
            openDelete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => workflow.OwnedWindows.OfType<DeleteRemoteBranchDialog>().Any(dialog => !dialog.IsBusy));
            DeleteRemoteBranchDialog fromReferences = workflow.OwnedWindows.OfType<DeleteRemoteBranchDialog>().Single();
            Assert.That(fromReferences.SelectionText, Is.EqualTo("origin/topic"));
            fromReferences.Close(); await Until(() => !main.IsBusy);
            workflow.Close();
            Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        }
        finally { await CloseAsync(main); }
    }

    private static async Task<DeleteRemoteBranchDialog> ShowAsync(Fixture fixture, string? initial = null, ThemeVariant? theme = null)
    {
        DeleteRemoteBranchDialog dialog = new(fixture.Repository, initial) { RequestedThemeVariant = theme ?? ThemeVariant.Light };
        dialog.Show(); await dialog.InitializeAsync(); Pump(dialog);
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
        return dialog;
    }

    private static async Task TrackAsync(Fixture f, string name, string remote, string branch)
    {
        await f.Repository.CreateBranchAsync(name, checkout: false);
        await f.GitAsync("config", "branch." + name + ".remote", remote);
        await f.GitAsync("config", "branch." + name + ".merge", "refs/heads/" + branch);
    }

    private static async Task<string> AddRemoteAsync(Fixture f, string name)
    {
        string path = f.Output(name + ".git");
        await f.GitAsync("clone", "--bare", f.Repository.Path, path);
        await f.Repository.AddRemoteAsync(name, path);
        await f.Repository.FetchAsync(name, prune: false);
        return path;
    }

    private static async Task<string?> RemoteRefAsync(string server, string branch)
    {
        GitResult result = await new GitRunner().RunAsync(server, ["show-ref", "--verify", "--hash", "refs/heads/" + branch]);
        return result.Success ? result.StandardOutput.Trim() : null;
    }

    private static async Task WriteHookAsync(Fixture f, string repository, string name, string body, string directory)
    {
        string hooks = f.Output(directory); Directory.CreateDirectory(hooks);
        string path = Path.Combine(hooks, name);
        await System.IO.File.WriteAllTextAsync(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows()) { System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        GitResult result = await new GitRunner().RunAsync(repository, ["config", "core.hooksPath", hooks]);
        Assert.That(result.Success, Is.True, result.StandardError);
    }

    private static bool ActorAlive(int pid)
    {
        try
        {
            string stat = System.IO.File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return fields[0] != "Z";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static IEnumerable<CheckBox> Choices(SelectMultipleBranchesDialog picker)
        => Field<StackPanel>(picker, "MultipleBranchChoices").Children.OfType<CheckBox>();
    private static T Field<T>(TopLevel root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static Button CaptionButton(Window root, string text)
        => root.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == text);
    private static void Pointer(Window root, string name) => PushProcessTestDriver.Pointer(Field<Button>(root, name));
    private static Task Idle(RepositoryDialog dialog) => UntilProcessAsync(dialog, () => !dialog.IsBusy);
    private static Task UntilProcessAsync(Window root, Func<bool> condition) => PushProcessTestDriver.UntilAsync(root, condition, "Remote branch workflow did not settle.");
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "Remote branch UI did not reach the expected state within 20 seconds.");
    }
    private static Task VisibleProcessNoticeAsync(GitProcessDialog process) => Until(() =>
    {
        // Execution can finish before newly visible ScrollViewer content is materialized.
        // Render it before observing the visual tree, without acknowledging the process.
        Pump(process);
        return process.GetVisualDescendants().OfType<TextBlock>()
            .Any(notice => notice.Name == "ProcessNotice" && notice.IsEffectivelyVisible);
    });
    private static void Pump(TopLevel window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }
    private static void AssertInside(Window window, params Control[] controls)
    {
        Pump(window);
        foreach (Control control in controls)
        {
            Point origin = control.TranslatePoint(default, window)!.Value;
            Assert.That(origin.X, Is.GreaterThanOrEqualTo(-.5), control.Name);
            Assert.That(origin.Y, Is.GreaterThanOrEqualTo(-.5), control.Name);
            Assert.That(origin.X + control.Bounds.Width, Is.LessThanOrEqualTo(window.ClientSize.Width + .5), control.Name);
            Assert.That(origin.Y + control.Bounds.Height, Is.LessThanOrEqualTo(window.ClientSize.Height + .5), control.Name);
        }
    }
    private static void Capture(Window window, string name)
    {
        Pump(window);
        using var frame = window.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private static async Task CloseAsync(Window window)
    {
        foreach (Window child in window.OwnedWindows.ToArray()) { await CloseAsync(child); }
        window.Close();
        await Until(() => !window.IsVisible);
    }
}

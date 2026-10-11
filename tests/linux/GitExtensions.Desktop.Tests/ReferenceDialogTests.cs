using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitCommands.Git;
using GitCommands.Git.Tag;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class ReferenceDialogTests
{
    [AvaloniaTest]
    public async Task CreatingBranchUsesTheChosenRevisionAndOptionalCheckoutWithLiteralValidNames()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string initial = await fixture.Repository.ResolveRevisionAsync("HEAD");
        await fixture.ChangeAsync("later\n", "Later commit");
        const string name = "topic/λ;$()'quoted";
        CreateBranchDialog dialog = new(fixture.Repository, initial);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Field<TextBox>(dialog, "CreateBranchName").Text = name;
            Field<CheckBox>(dialog, "CheckoutAfterCreate").IsChecked = false;
            Capture(dialog, "classic-create-branch.png");
            Click(dialog, "CreateBranch");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.CreatedBranch, Is.EqualTo(name));
            Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
            Assert.That(await fixture.Repository.ResolveRevisionAsync("refs/heads/" + name), Is.EqualTo(initial));
            Assert.That(dialog.HasRepositoryChanges, Is.True);
        }
        finally { dialog.Close(); }
        await fixture.Repository.CreateBranchAsync(new GitCreateBranchOptions("checked-out", initial));
        Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("checked-out"));
    }

    [AvaloniaTest]
    public async Task UnbornCreationPreservesTheIndexAndCreatesAParentlessFirstCommit()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync(unborn: true);
        await File.WriteAllTextAsync(fixture.File, "initial staged\n");
        await fixture.Repository.StageAllAsync();
        CreateBranchDialog dialog = new(fixture.Repository);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(Field<CheckBox>(dialog, "CreateOrphanBranch").IsChecked, Is.True);
            Assert.That(Field<CheckBox>(dialog, "CreateOrphanBranch").IsEnabled, Is.False);
            Assert.That(Field<CheckBox>(dialog, "CheckoutAfterCreate").IsEnabled, Is.False);
            Field<TextBox>(dialog, "CreateBranchName").Text = "new-root";
            Click(dialog, "CreateBranch");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("new-root"));
            Assert.That((await fixture.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("initial staged\n"));
            await fixture.GitAsync("commit", "-m", "First orphan commit");
            Assert.That((await fixture.Repository.GetLogAsync(1)).Single().Parents, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task OrphanClearRequiresConfirmationAndPreservesUntrackedFiles()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string original = await fixture.Repository.ResolveRevisionAsync("HEAD");
        string untracked = fixture.PathInRepository("untracked\nλ.txt");
        await File.WriteAllTextAsync(untracked, "keep me\n");
        CreateBranchDialog dialog = new(fixture.Repository);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Field<TextBox>(dialog, "CreateBranchName").Text = "empty-history";
            Field<CheckBox>(dialog, "CreateOrphanBranch").IsChecked = true;
            Field<CheckBox>(dialog, "ClearOrphanWorkingTree").IsChecked = true;
            Click(dialog, "CreateBranch");
            Confirm(dialog.OwnedWindows.Single(), false);
            await Task.Delay(20);
            Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(original));
            Click(dialog, "CreateBranch");
            Confirm(dialog.OwnedWindows.Single(), true);
            await Task.Delay(20);
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That((await fixture.Repository.GetStatusAsync()).IsUnborn, Is.True);
            Assert.That(File.Exists(fixture.File), Is.False);
            Assert.That(await File.ReadAllTextAsync(untracked), Is.EqualTo("keep me\n"));
            Assert.That((await fixture.GitAsync("ls-files", "-z")).StandardOutput, Is.Empty);
            Assert.That(await fixture.Repository.ResolveRevisionAsync("main"), Is.EqualTo(original));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task InvalidNamesAndCancelledOperationsDoNotCreateReferences()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        foreach (string name in new[] { "-option", "a:b", "a..b", "@{-1}", "line\nbreak", "HEAD" })
        {
            Assert.ThrowsAsync<ArgumentException>(() => fixture.Repository.CreateBranchAsync(new GitCreateBranchOptions(name, Checkout: false)));
        }
        Assert.ThrowsAsync<ArgumentException>(() => fixture.Repository.CreateTagAsync(new GitCreateTagOptions("-bad")));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(() => fixture.Repository.CreateBranchAsync(new GitCreateBranchOptions("cancelled", Checkout: false), cancelled.Token));
        Assert.That((await fixture.Repository.GetReferencesAsync()).Select(reference => reference.Name), Is.EqualTo(new[] { "refs/heads/main" }));
        CreateBranchDialog dialog = new(fixture.Repository, autoNormalize: false);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Field<TextBox>(dialog, "CreateBranchName").Text = "bad:name";
            Click(dialog, "CreateBranch");
            await Idle(dialog);
            Assert.That(dialog.LastError, Does.Contain("Invalid branch name"));
            Assert.That(dialog.IsVisible, Is.True);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task CheckoutKeepsWorkUntilExplicitlyConfirmedDiscard()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        await fixture.Repository.CreateBranchAsync("target");
        await fixture.ChangeAsync("target content\n", "Target commit");
        await fixture.Repository.CheckoutAsync("main");
        await File.WriteAllTextAsync(fixture.File, "local work\n");
        GitReference target = (await fixture.Repository.GetReferencesAsync()).Single(reference => reference.ShortName == "target");
        CheckoutBranchDialog dialog = new(fixture.Repository, target);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Click(dialog, "CheckoutBranch");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Not.Null);
            Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
            Assert.That(await File.ReadAllTextAsync(fixture.File), Is.EqualTo("local work\n"));
            Field<RadioButton>(dialog, "CheckoutDiscardChanges").IsChecked = true;
            Assert.That(Field<CheckBox>(dialog, "CheckoutChangesDefault").IsEnabled, Is.False);
            Click(dialog, "CheckoutBranch");
            Confirm(dialog.OwnedWindows.Single(), false);
            await Task.Delay(20);
            Assert.That(await File.ReadAllTextAsync(fixture.File), Is.EqualTo("local work\n"));
            Click(dialog, "CheckoutBranch");
            Confirm(dialog.OwnedWindows.Single(), true);
            await Task.Delay(20);
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("target"));
            Assert.That(await File.ReadAllTextAsync(fixture.File), Is.EqualTo("target content\n"));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task CheckoutStashRestoresItsExactIndexAndUntrackedNamesWithoutPoppingOlderStashes()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        await fixture.Repository.CreateBranchAsync("target", checkout: false);
        await File.WriteAllTextAsync(fixture.File, "older stash\n");
        await fixture.Repository.StashAsync("older");
        string older = (await fixture.Repository.GetStashesAsync()).Single().Hash;
        await File.WriteAllTextAsync(fixture.File, "staged work\n");
        await fixture.Repository.StageAllAsync();
        string untracked = fixture.PathInRepository("untracked\nλ[1].txt");
        await File.WriteAllTextAsync(untracked, "untracked work\n");
        GitReference target = (await fixture.Repository.GetReferencesAsync()).Single(reference => reference.ShortName == "target");
        CheckoutBranchDialog dialog = new(fixture.Repository, target);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Field<RadioButton>(dialog, "CheckoutStashChanges").IsChecked = true;
            Field<CheckBox>(dialog, "ReapplyCheckoutStash").IsChecked = true;
            Field<CheckBox>(dialog, "CheckoutChangesDefault").IsChecked = true;
            Capture(dialog, "classic-checkout-branch.png");
            Click(dialog, "CheckoutBranch");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.LastResult?.CreatedStash, Is.Not.Null);
            Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("target"));
            Assert.That((await fixture.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("staged work\n"));
            Assert.That(await File.ReadAllTextAsync(untracked), Is.EqualTo("untracked work\n"));
            Assert.That((await fixture.Repository.GetStashesAsync()).Select(stash => stash.Hash), Is.EqualTo(new[] { older }));
        }
        finally { dialog.Close(); }
        CheckoutBranchDialog next = new(fixture.Repository);
        next.Show();
        try { await next.InitializeAsync(); Assert.That(next.ReadOptions().Changes, Is.EqualTo(LocalChangesAction.Stash)); }
        finally { next.Close(); }
    }

    [AvaloniaTest]
    public async Task CheckoutCanMergeLocalWorkAndLeaveResolvableConflicts()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        await fixture.Repository.CreateBranchAsync("target");
        await fixture.ChangeAsync("target\n", "Target content");
        await fixture.Repository.CheckoutAsync("main");
        await File.WriteAllTextAsync(fixture.File, "local\n");
        GitCheckoutResult result = await fixture.Repository.CheckoutAsync(new GitCheckoutOptions("target", Changes: LocalChangesAction.Merge));
        Assert.That(result.Result.Success, Is.True);
        GitStatus status = await fixture.Repository.GetStatusAsync();
        Assert.That(status.Branch, Is.EqualTo("target"));
        Assert.That(status.Changes.Single().IsConflict, Is.True);
        Assert.That(await File.ReadAllTextAsync(fixture.File), Does.Contain("local").And.Contain("target"));
    }

    [AvaloniaTest]
    public async Task RemoteCheckoutCreatesTrackingBranchAndResetNeedsExplicitConfirmation()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        await fixture.CreateRemoteAsync();
        GitReference remote = (await fixture.Repository.GetReferencesAsync()).Single(reference => reference.Name == "refs/remotes/origin/main");
        CheckoutBranchDialog dialog = new(fixture.Repository, remote);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Field<TextBox>(dialog, "CheckoutLocalName").Text = "local-topic";
            Assert.That(dialog.ReadOptions().NewBranchMode, Is.EqualTo(CheckoutNewBranchMode.Create));
            Click(dialog, "CheckoutBranch");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            GitReference local = (await fixture.Repository.GetReferencesAsync()).Single(reference => reference.ShortName == "local-topic");
            Assert.That(local.Upstream, Is.EqualTo("origin/main"));
        }
        finally { dialog.Close(); }
        await fixture.ChangeAsync("local commit\n", "Local-only commit");
        string before = await fixture.Repository.ResolveRevisionAsync("HEAD");
        dialog = new(fixture.Repository, remote);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Field<ComboBox>(dialog, "RemoteCheckoutMode").SelectedIndex = 2;
            Field<TextBox>(dialog, "CheckoutLocalName").Text = "local-topic";
            Click(dialog, "CheckoutBranch");
            Confirm(dialog.OwnedWindows.Single(), false);
            await Task.Delay(20);
            Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(before));
            Click(dialog, "CheckoutBranch");
            Confirm(dialog.OwnedWindows.Single(), true);
            await Task.Delay(20);
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(remote.Hash));
        }
        finally { dialog.Close(); }
        await fixture.Repository.CheckoutAsync(new GitCheckoutOptions(remote.Name, GitCheckoutKind.RemoteBranch));
        Assert.That((await fixture.Repository.GetStatusAsync()).IsDetached, Is.True);
    }

    [AvaloniaTest]
    public async Task DeletingBranchesPreviewsMergedStateAndPreservesUnmergedBranchesUntilForced()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        await fixture.Repository.CreateBranchAsync("merged-a", checkout: false);
        await fixture.Repository.CreateBranchAsync("merged-b", checkout: false);
        await fixture.Repository.CreateBranchAsync("unmerged");
        await fixture.ChangeAsync("unmerged\n", "Unmerged work");
        await fixture.Repository.CheckoutAsync("main");
        Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.DeleteBranchesAsync(new(["unmerged"])));
        DeleteBranchDialog dialog = new(fixture.Repository, ["merged-a", "merged-b"]);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Capture(dialog, "classic-delete-branches.png");
            Click(dialog, "DeleteBranches");
            Confirm(dialog.OwnedWindows.Single(), false);
            await Task.Delay(20);
            Assert.That((await fixture.Repository.GetReferencesAsync()).Count(reference => reference.ShortName.StartsWith("merged-", StringComparison.Ordinal)), Is.EqualTo(2));
            Click(dialog, "DeleteBranches");
            Confirm(dialog.OwnedWindows.Single(), true);
            await Task.Delay(20);
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That((await fixture.Repository.GetReferencesAsync()).Any(reference => reference.ShortName.StartsWith("merged-", StringComparison.Ordinal)), Is.False);
        }
        finally { dialog.Close(); }
        await fixture.Repository.DeleteBranchesAsync(new(["unmerged"], Force: true));
        Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.DeleteBranchesAsync(new(["main"], Force: true)));
    }

    [AvaloniaTest]
    public async Task WorktreeBranchesRequireExplicitRemovalAndDirtyWorktreeForce()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string linked = fixture.Output("linked\nλ");
        await fixture.Repository.AddWorktreeAsync(linked, "HEAD", "linked");
        await File.WriteAllTextAsync(Path.Combine(linked, "file.txt"), "dirty linked work\n");
        Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.DeleteBranchesAsync(new(["linked"])));
        Assert.ThrowsAsync<GitCommandException>(() => fixture.Repository.DeleteBranchesAsync(new(["linked"], RemoveLinkedWorktrees: true)));
        Assert.That(File.Exists(Path.Combine(linked, "file.txt")), Is.True);
        DeleteBranchDialog dialog = new(fixture.Repository, ["linked"]);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Field<CheckBox>(dialog, "RemoveBranchWorktrees").IsChecked = true;
            Field<CheckBox>(dialog, "ForceRemoveBranchWorktrees").IsChecked = true;
            Click(dialog, "DeleteBranches");
            Window confirmation = dialog.OwnedWindows.Single();
            Assert.That(confirmation.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text?.Contains("Changes in these worktrees", StringComparison.Ordinal) == true).Text, Does.Contain("linked\\nλ"));
            Confirm(confirmation, true);
            await Task.Delay(20);
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(Directory.Exists(linked), Is.False);
            Assert.That((await fixture.Repository.GetReferencesAsync()).Any(reference => reference.ShortName == "linked"), Is.False);
            Assert.That(File.Exists(fixture.File), Is.True);
        }
        finally { dialog.Close(); }
        string missing = fixture.Output("missing");
        await fixture.Repository.AddWorktreeAsync(missing, "HEAD", "missing");
        Directory.Delete(missing, true);
        await fixture.Repository.DeleteBranchesAsync(new(["missing"], RemoveLinkedWorktrees: true));
        Assert.That((await fixture.Repository.GetWorktreesAsync()).Count, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task TagsKeepOriginalTypesAndMessagesAndForceReplacementIsConfirmed()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        await fixture.GitAsync("config", "tag.gpgSign", "true");
        CreateTagDialog light = new(fixture.Repository);
        light.Show();
        try
        {
            await light.InitializeAsync();
            Field<TextBox>(light, "CreateTagName").Text = "release/λ;$()'quoted";
            Assert.That(Field<TextBox>(light, "CreateTagMessage").IsEnabled, Is.False);
            Click(light, "CreateTag");
            await Idle(light);
            Assert.That(light.LastError, Is.Null, light.LastException?.ToString());
            Assert.That((await fixture.GitAsync("cat-file", "-t", "refs/tags/" + light.CreatedTag)).StandardOutput.Trim(), Is.EqualTo("commit"));
        }
        finally { light.Close(); }
        CreateTagDialog annotated = new(fixture.Repository);
        annotated.Show();
        const string message = "  Release λ\n\n# literal comment\nSecond paragraph\n";
        try
        {
            await annotated.InitializeAsync();
            Field<TextBox>(annotated, "CreateTagName").Text = "annotated";
            Field<ComboBox>(annotated, "CreateTagOperation").SelectedIndex = 1;
            Field<TextBox>(annotated, "CreateTagMessage").Text = message;
            Assert.That(Field<TextBox>(annotated, "TagSigningKey").IsEnabled, Is.False);
            Capture(annotated, "classic-create-tag.png");
            Click(annotated, "CreateTag");
            await Idle(annotated);
            Assert.That(annotated.LastError, Is.Null, annotated.LastException?.ToString());
            Assert.That((await fixture.GitAsync("cat-file", "-p", "refs/tags/annotated")).StandardOutput, Does.EndWith(message));
        }
        finally { annotated.Close(); }
        GitReference tag = (await fixture.Repository.GetReferencesAsync()).Single(reference => reference.Name == "refs/tags/annotated");
        CheckoutBranchDialog checkout = new(fixture.Repository, tag);
        checkout.Show();
        try { await checkout.InitializeAsync(); Click(checkout, "CheckoutBranch"); await Idle(checkout); Assert.That(checkout.LastError, Is.Null); Assert.That((await fixture.Repository.GetStatusAsync()).IsDetached, Is.True); }
        finally { checkout.Close(); }
        await fixture.Repository.CheckoutAsync("main");
        string original = await fixture.Repository.ResolveRevisionAsync("annotated");
        await fixture.ChangeAsync("new release\n", "Next release");
        CreateTagDialog replace = new(fixture.Repository);
        replace.Show();
        try
        {
            await replace.InitializeAsync();
            Field<TextBox>(replace, "CreateTagName").Text = "annotated";
            Field<CheckBox>(replace, "ForceTag").IsChecked = true;
            Click(replace, "CreateTag");
            await Until(() => replace.OwnedWindows.Any());
            Confirm(replace.OwnedWindows.Single(), false);
            await Task.Delay(20);
            Assert.That(await fixture.Repository.ResolveRevisionAsync("annotated"), Is.EqualTo(original));
            Click(replace, "CreateTag");
            await Until(() => replace.OwnedWindows.Any());
            Confirm(replace.OwnedWindows.Single(), true);
            await Task.Delay(20);
            await Idle(replace);
            Assert.That(replace.LastError, Is.Null, replace.LastException?.ToString());
            Assert.That(await fixture.Repository.ResolveRevisionAsync("annotated"), Is.EqualTo(await fixture.Repository.ResolveRevisionAsync("HEAD")));
        }
        finally { replace.Close(); }
    }

    [AvaloniaTest]
    public async Task CreatingAndDeletingRemoteTagUsesOnlyTheSelectedTagRefspec()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string remotePath = await fixture.CreateRemoteAsync();
        GitRepository server = await GitRepository.OpenAsync(remotePath);
        await fixture.Repository.CreateTagAsync(new GitCreateTagOptions("preserved", PushRemote: "origin"));
        CreateTagDialog create = new(fixture.Repository);
        create.Show();
        try
        {
            await create.InitializeAsync();
            Field<TextBox>(create, "CreateTagName").Text = "to-remove";
            Field<CheckBox>(create, "PushCreatedTag").IsChecked = true;
            Click(create, "CreateTag");
            await Idle(create);
            Assert.That(create.LastError, Is.Null, create.LastException?.ToString());
            Assert.That((await server.GetReferencesAsync()).Any(reference => reference.ShortName == "to-remove"), Is.True);
        }
        finally { create.Close(); }
        DeleteTagDialog delete = new(fixture.Repository, "to-remove");
        delete.Show();
        try
        {
            await delete.InitializeAsync();
            Field<CheckBox>(delete, "DeleteRemoteTag").IsChecked = true;
            Click(delete, "DeleteTag");
            Confirm(delete.OwnedWindows.Single(), false);
            await Task.Delay(20);
            Assert.That((await server.GetReferencesAsync()).Any(reference => reference.ShortName == "to-remove"), Is.True);
            Click(delete, "DeleteTag");
            Confirm(delete.OwnedWindows.Single(), true);
            await Task.Delay(20);
            await Idle(delete);
            Assert.That(delete.LastError, Is.Null, delete.LastException?.ToString());
            Assert.That((await fixture.Repository.GetReferencesAsync()).Where(reference => reference.IsTag).Select(reference => reference.ShortName), Is.EqualTo(new[] { "preserved" }));
            Assert.That((await server.GetReferencesAsync()).Where(reference => reference.IsTag).Select(reference => reference.ShortName), Is.EqualTo(new[] { "preserved" }));
        }
        finally { delete.Close(); }
    }

    [AvaloniaTest]
    public async Task TagsSignWithDefaultAndSpecificEphemeralGpgKeys()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string keyDirectory = fixture.Output("gnupg");
        Directory.CreateDirectory(keyDirectory);
        if (!OperatingSystem.IsWindows()) { System.IO.File.SetUnixFileMode(keyDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        fixture.SetEnvironment("GNUPGHOME", keyDirectory);
        try
        {
            await ExternalAsync(fixture.Repository.Path, "gpg", "--batch", "--pinentry-mode", "loopback", "--passphrase", "", "--quick-generate-key", "Reference Signing Test <signing@example.com>", "ed25519", "sign", "0");
        }
        catch (System.ComponentModel.Win32Exception) { Assert.Ignore("GPG is not installed; the signing integration scenario requires it."); }
        try
        {
            string keys = await ExternalAsync(fixture.Repository.Path, "gpg", "--batch", "--with-colons", "--list-secret-keys");
            string fingerprint = keys.Split('\n').First(line => line.StartsWith("fpr:", StringComparison.Ordinal)).Split(':')[9];
            await fixture.GitAsync("config", "user.signingkey", fingerprint);
            await fixture.Repository.CreateTagAsync(new GitCreateTagOptions("default-signed", Operation: TagOperation.SignWithDefaultKey, Message: "Default signing\n"));
            await fixture.GitAsync("verify-tag", "refs/tags/default-signed");
            CreateTagDialog specific = new(fixture.Repository);
            specific.Show();
            try
            {
                await specific.InitializeAsync();
                Field<TextBox>(specific, "CreateTagName").Text = "specific-signed";
                Field<ComboBox>(specific, "CreateTagOperation").SelectedIndex = 3;
                Assert.That(Field<TextBox>(specific, "TagSigningKey").IsEnabled, Is.True);
                Field<TextBox>(specific, "TagSigningKey").Text = fingerprint;
                Field<TextBox>(specific, "CreateTagMessage").Text = "Specific signing λ\n";
                Click(specific, "CreateTag");
                await Idle(specific);
                Assert.That(specific.LastError, Is.Null, specific.LastException?.ToString());
                await fixture.GitAsync("verify-tag", "refs/tags/specific-signed");
            }
            finally { specific.Close(); }
        }
        finally { await ExternalAsync(fixture.Repository.Path, "gpgconf", "--kill", "gpg-agent"); }
    }

    [AvaloniaTest]
    public async Task BareBranchesAndTagsCanBeCreatedWithoutCheckout()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string barePath = fixture.Output("bare.git");
        await fixture.GitAsync("clone", "--bare", fixture.Repository.Path, barePath);
        GitRepository bare = await GitRepository.OpenAsync(barePath);
        CreateBranchDialog branch = new(bare);
        branch.Show();
        try
        {
            await branch.InitializeAsync();
            Assert.That(Field<CheckBox>(branch, "CheckoutAfterCreate").IsChecked, Is.False);
            Assert.That(Field<CheckBox>(branch, "CheckoutAfterCreate").IsEnabled, Is.False);
            Field<TextBox>(branch, "CreateBranchName").Text = "bare-topic";
            Click(branch, "CreateBranch");
            await Idle(branch);
            Assert.That(branch.LastError, Is.Null, branch.LastException?.ToString());
            await bare.CreateTagAsync(new GitCreateTagOptions("bare-tag"));
            Assert.That((await bare.GetReferencesAsync()).Any(reference => reference.ShortName == "bare-topic"), Is.True);
            await bare.DeleteBranchesAsync(new(["bare-topic"]));
            await bare.DeleteTagsAsync(["bare-tag"]);
        }
        finally { branch.Close(); }
        CheckoutBranchDialog checkout = new(bare);
        checkout.Show();
        try { await checkout.InitializeAsync(); Assert.That(checkout.LastError, Is.Null); Assert.That(Field<Button>(checkout, "CheckoutBranch").IsEnabled, Is.False); }
        finally { checkout.Close(); }
    }

    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Window window, string name)
    {
        Button button = Field<Button>(window, name);
        Assert.That(button.IsEnabled && button.IsEffectivelyVisible, Is.True, name + " must be actionable.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Confirm(Window window, bool accept) => window.GetVisualDescendants().OfType<Button>().Single(button => (button.Content as string == "Cancel") != accept).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Task Idle(RepositoryDialog dialog) => Until(() => !dialog.IsBusy);
    private static async Task Until(Func<bool> complete)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!complete() && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        Assert.That(complete(), Is.True, "The native reference dialog did not complete within 15 seconds.");
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        Assert.That(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .Where(choice => choice.IsEffectivelyVisible && choice is CheckBox or RadioButton)
            .Select(choice => choice.Bounds.Height), Is.All.EqualTo(24), "Reference choices must retain the original compact height.");
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null);
        string file = Path.Combine(TestContext.CurrentContext.WorkDirectory, name);
        frame!.Save(file);
        TestContext.AddTestAttachment(file, "Classic native reference dialog with a real Git repository");
    }

    private static async Task<string> ExternalAsync(string directory, string executable, params string[] arguments)
    {
        ProcessStartInfo info = new(executable) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) { info.ArgumentList.Add(argument); }
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.That(process.ExitCode, Is.Zero, await errors);
        return await output;
    }

    internal sealed class ReferenceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-references-ui-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> _environment = new();
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly GitRunner _git = new();
        public GitRepository Repository { get; }
        internal string File => PathInRepository("file.txt");
        private ReferenceFixture()
        {
            Directory.CreateDirectory(_root);
            SetEnvironment("XDG_CONFIG_HOME", Path.Combine(_root, "config"));
            SetEnvironment("GIT_CONFIG_GLOBAL", "/dev/null");
            SetEnvironment("GIT_CONFIG_NOSYSTEM", "1");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            Repository = new GitRepository(Path.Combine(_root, "repo"));
            Directory.CreateDirectory(Repository.Path);
        }
        internal static async Task<ReferenceFixture> CreateAsync(bool unborn = false)
        {
            ReferenceFixture fixture = new();
            try
            {
                await fixture.GitAsync("init", "--initial-branch=main");
                await fixture.GitAsync("config", "user.name", "Reference Tester");
                await fixture.GitAsync("config", "user.email", "reference@example.com");
                await fixture.GitAsync("config", "commit.gpgsign", "false");
                await fixture.GitAsync("config", "core.hooksPath", fixture.Output("no-hooks"));
                if (!unborn) { await fixture.ChangeAsync("base\n", "Initial commit"); }
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        internal void SetEnvironment(string name, string? value)
        {
            if (!_environment.ContainsKey(name)) { _environment[name] = Environment.GetEnvironmentVariable(name); }
            Environment.SetEnvironmentVariable(name, value);
        }
        internal string Output(string path) => Path.Combine(_root, path);
        internal string PathInRepository(string path) => Path.Combine(Repository.Path, path);
        internal async Task<GitResult> GitAsync(params string[] args)
        {
            GitResult result = await _git.RunAsync(Repository.Path, args);
            Assert.That(result.Success, Is.True, result.StandardError);
            return result;
        }
        internal async Task ChangeAsync(string content, string subject)
        {
            await System.IO.File.WriteAllTextAsync(File, content);
            await Repository.StageAllAsync();
            await GitAsync("commit", "-m", subject);
        }
        internal async Task<string> CreateRemoteAsync()
        {
            string path = Output("remote.git");
            await GitAsync("clone", "--bare", Repository.Path, path);
            await Repository.AddRemoteAsync("origin", path);
            await Repository.FetchAsync("origin", prune: false);
            return path;
        }
        public void Dispose()
        {
            foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); }
            CultureInfo.CurrentUICulture = _culture;
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }
}

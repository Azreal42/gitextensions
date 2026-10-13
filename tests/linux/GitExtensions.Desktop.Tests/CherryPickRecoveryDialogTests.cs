using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public partial class CherryPickDialogTests
{
    [AvaloniaTest]
    public async Task ReopeningResolvedNoCommitRecoveryKeepsFrozenOptionsAndContinuesWithoutAnotherPick()
    {
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            await using Fixture f = await Fixture.CreateAsync(); string pick = await f.ConflictAsync();
            f.Write("prior.txt", "index before\n"); await f.Git("add", "prior.txt"); f.Write("prior.txt", "worktree before\n"); string head = await f.Head();
            CherryPickDialog first = await f.ShowAsync(pick); first.RequestedThemeVariant = theme;
            Pointer(Field<CheckBox>(first, "CherryPickAddReference")); Pointer(Field<Button>(first, "ExecuteCherryPick"));
            await Until(() => !first.IsBusy && Field<StackPanel>(first, "CherryPickRecovery").IsVisible, () => $"Pending {theme}: error={first.LastError}, exception={first.LastException}");
            Button cancel = Field<Button>(first, "CancelCherryPick"); int cancelClicks = 0;
            // Avalonia 11.3.22 registers Button.ClickEvent with Bubble only.
            cancel.AddHandler(Button.ClickEvent, (_, _) => cancelClicks++, RoutingStrategies.Bubble, handledEventsToo: true);
            TestContext.WriteLine($"Before recovery Cancel {theme}: {PointerTarget(cancel).Description}");
            await Until(() => { Pump(first); return PointerTarget(cancel).Ready; }, () => $"Recovery Cancel {theme}: {PointerTarget(cancel).Description}, busy={first.IsBusy}, error={first.LastError}");
            TestContext.WriteLine($"Ready recovery Cancel {theme}: {PointerTarget(cancel).Description}");
            byte[] interruptedIndex = File.ReadAllBytes(f.Metadata("index")), interruptedJournal = File.ReadAllBytes(f.Metadata("gitextensions-cherry-pick/recovery.json"));
            string interruptedContent = File.ReadAllText(f.File("base.txt"));
            Pointer(cancel); await Until(() => !first.IsVisible, () => $"Recovery Cancel {theme}: clicks={cancelClicks}, visible={first.IsVisible}, busy={first.IsBusy}, target={PointerTarget(cancel).Description}");
            Assert.That(cancelClicks, Is.EqualTo(1), "The ready button must receive the single actual pointer click.");
            Assert.That(File.ReadAllBytes(f.Metadata("index")), Is.EqualTo(interruptedIndex));
            Assert.That(File.ReadAllBytes(f.Metadata("gitextensions-cherry-pick/recovery.json")), Is.EqualTo(interruptedJournal));
            Assert.That(File.ReadAllText(f.File("base.txt")), Is.EqualTo(interruptedContent)); Assert.That(await f.Head(), Is.EqualTo(head));
            Assert.That(File.Exists(f.Metadata("CHERRY_PICK_HEAD")), Is.False, "Real single -n has no native marker; the journal must carry its intent.");
            f.Write("base.txt", "resolved outside the old dialogue\n"); await f.Git("add", "base.txt");
            new CherryPickPreferencesStore().Save(new(true, false));
            CherryPickDialog reopened = new(new GitRepository(f.Repository.Path), head) { RequestedThemeVariant = theme }; f.Windows.Add(reopened); reopened.Show(); await reopened.InitializeAsync(); Pump(reopened);
            Assert.That(reopened.Revision, Is.EqualTo(pick), "A new selection and changed preferences cannot retarget the interrupted operation.");
            Assert.That(reopened.ReadOptions(), Is.EqualTo(new GitCherryPickOptions(pick, AddCommitReference: true)));
            Assert.That(Field<Button>(reopened, "ExecuteCherryPick").IsEnabled, Is.False);
            Assert.That(Field<CheckBox>(reopened, "CherryPickAutoCommit").IsEnabled, Is.False);
            Assert.That(Field<Button>(reopened, "ContinueCherryPick").IsEnabled, Is.True);
            Button continuePick = Field<Button>(reopened, "ContinueCherryPick");
            await Until(() => { Pump(reopened); return PointerTarget(continuePick).Ready; }, () => $"Reopened Continue {theme}: {PointerTarget(continuePick).Description}, error={reopened.LastError}");
            Pointer(continuePick); await Until(() => !reopened.IsVisible && !reopened.IsBusy, () => $"Reopened Continue {theme}: error={reopened.LastError}, exception={reopened.LastException}");
            Assert.That(reopened.Applied && reopened.SequenceAccepted, Is.True, reopened.LastError);
            Assert.That(await f.Head(), Is.EqualTo(head)); Assert.That(await f.Text("show", ":base.txt"), Is.EqualTo("resolved outside the old dialogue"));
            Assert.That(await f.Text("show", ":prior.txt"), Is.EqualTo("index before")); Assert.That(File.ReadAllText(f.File("prior.txt")), Is.EqualTo("worktree before\n"));
            Assert.That((await new GitRepository(f.Repository.Path).GetCherryPickRecoveryAsync()).State, Is.EqualTo(GitCherryPickRecoveryState.Absent));
        }
    }

    [AvaloniaTest]
    public async Task ReopeningAutomaticRecoveryRetainsConflictsThenNativeContinueCreatesExactlyOneCommit()
    {
        await using Fixture f = await Fixture.CreateAsync(); string pick = await f.ConflictAsync(); string head = await f.Head();
        CherryPickDialog first = await f.ShowAsync(pick); Pointer(Field<CheckBox>(first, "CherryPickAutoCommit")); Pointer(Field<Button>(first, "ExecuteCherryPick"));
        await Until(() => !first.IsBusy && Field<StackPanel>(first, "CherryPickRecovery").IsVisible);
        first.Close(); await Until(() => !first.IsVisible);
        CherryPickDialog reopened = await f.ShowAsync(null);
        Assert.That(reopened.ReadOptions(), Is.EqualTo(new GitCherryPickOptions(pick, AutoCommit: true)));
        Assert.That(Field<ListBox>(reopened, "CherryPickConflictFiles").Items.Cast<string>(), Does.Contain("base.txt"));
        Assert.That(Field<Button>(reopened, "ContinueCherryPick").IsEnabled, Is.False);
        f.Write("base.txt", "resolved\n"); await f.Git("add", "base.txt"); Pointer(Field<Button>(reopened, "RefreshCherryPickConflicts"));
        await Until(() => !reopened.IsBusy && Field<Button>(reopened, "ContinueCherryPick").IsEnabled);
        Pointer(Field<Button>(reopened, "ContinueCherryPick")); await Until(() => !reopened.IsVisible && !reopened.IsBusy);
        Assert.That(reopened.Applied, Is.True, reopened.LastError); Assert.That(await f.Head(), Is.Not.EqualTo(head));
        Assert.That(await f.Text("rev-list", "--count", head + "..HEAD"), Is.EqualTo("1"));
    }

    [AvaloniaTest]
    public async Task ReopeningSequenceRecoversOnlyPendingPickAndNeverAppliesNewOrForgottenRemainder()
    {
        await using Fixture f = await Fixture.CreateAsync(); string pick = await f.ConflictAsync(); string head = await f.Head();
        CherryPickDialog first = await f.ShowAsync(pick); Pointer(Field<Button>(first, "ExecuteCherryPick")); await Until(() => !first.IsBusy && Field<StackPanel>(first, "CherryPickRecovery").IsVisible); first.Close();
        Window owner = new() { Width = 800, Height = 600, RequestedThemeVariant = ThemeVariant.Dark }; f.Windows.Add(owner); owner.Show();
        Task<bool> sequence = CherryPickSequence.ShowAsync(owner, new GitRepository(f.Repository.Path), [head, "missing-new-selection"]);
        await Until(() => owner.OwnedWindows.OfType<CherryPickDialog>().Any()); CherryPickDialog reopened = owner.OwnedWindows.OfType<CherryPickDialog>().Single();
        await Until(() => !reopened.IsBusy); Pump(reopened);
        Assert.That(reopened.Revision, Is.EqualTo(pick)); Assert.That(reopened.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
        Pointer(Field<Button>(reopened, "SkipCherryPick")); await Until(() => sequence.IsCompleted);
        Assert.That(await sequence, Is.True); Assert.That(await f.Head(), Is.EqualTo(head));
        Assert.That(owner.OwnedWindows.OfType<CherryPickDialog>(), Is.Empty, "A resumed pick must not restart a lost/new queue automatically.");
        Assert.That((await f.Repository.GetStatusAsync()).Changes, Is.Empty);
    }

    [AvaloniaTest]
    public async Task IncompleteJournalShowsUsefulErrorAndBlocksNewPickWhileClosureLeavesEverythingIntact()
    {
        await using Fixture f = await Fixture.CreateAsync(); string pick = await f.ConflictAsync(); await f.Repository.CherryPickAsync(new GitCherryPickOptions(pick));
        string manifest = f.Metadata("gitextensions-cherry-pick/recovery.json"); File.WriteAllText(manifest, "corrupt private preimages");
        byte[] index = File.ReadAllBytes(f.Metadata("index")); string content = File.ReadAllText(f.File("base.txt")), head = await f.Head();
        CherryPickDialog reopened = await f.ShowAsync(head);
        Assert.That(Field<TextBlock>(reopened, "OperationStatus").Text, Does.Contain("cannot be loaded safely"));
        Assert.That(Field<Button>(reopened, "ExecuteCherryPick").IsEnabled, Is.False);
        Assert.That(Field<Button>(reopened, "ChooseCherryPickRevision").IsEnabled, Is.False);
        Assert.That(Field<Button>(reopened, "ContinueCherryPick").IsEnabled, Is.False);
        Assert.That(Field<Button>(reopened, "SkipCherryPick").IsEnabled, Is.False);
        Pointer(Field<Button>(reopened, "CancelCherryPick")); await Until(() => !reopened.IsVisible);
        Assert.That(File.ReadAllBytes(f.Metadata("index")), Is.EqualTo(index)); Assert.That(File.ReadAllText(f.File("base.txt")), Is.EqualTo(content)); Assert.That(await f.Head(), Is.EqualTo(head));
        Assert.That(File.ReadAllText(manifest), Is.EqualTo("corrupt private preimages"));
    }
}

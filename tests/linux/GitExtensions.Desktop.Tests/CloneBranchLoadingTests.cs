using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Styling;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CloneBranchLoadingTests
{
    [AvaloniaTest]
    public async Task FileRemoteBranchLoadingKeepsTheFormEditableAndDoesNotChangeTheTypedBranchOrHistories()
    {
        await using CloneInteractionFixture f = await CloneInteractionFixture.Create();
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            CloneDialog clone = await f.Show(theme: theme);
            Dictionary<string, byte[]?> histories = f.Histories(); string materials = f.Materials();
            TextBox branch = CloneInteractionFixture.Field<TextBox>(clone, "CloneBranch");
            CloneInteractionFixture.Edit(clone, branch, "typed-before-loading");
            PushProcessTestDriver.Pointer(CloneInteractionFixture.Field<Button>(clone, "RefreshCloneBranches"));
            await CloneInteractionFixture.Until(() => !clone.IsBranchLoading && Branches(clone).Contains("topic-original"));
            Assert.That(clone.IsBusy, Is.False);
            Assert.That(branch.Text, Is.EqualTo("typed-before-loading"));
            Assert.That(Branches(clone).Skip(2), Is.EqualTo(new[] { "main", "topic-original" }));
            foreach (string name in new[] { "CloneSource", "CloneDestination", "CloneSubdirectory", "CloneBranch" })
            { Assert.That(CloneInteractionFixture.Field<TextBox>(clone, name).IsEnabled, Is.True, name); }
            Assert.That(CloneInteractionFixture.Field<Button>(clone, "CloneRepository").IsEnabled, Is.True);
            Assert.That(clone.BranchLoadError, Is.Null); Assert.That(clone.BranchLoadFailures, Is.Empty);
            Assert.That(clone.LastError, Is.Null); Assert.That(clone.LastProcess, Is.Null);
            Assert.That(File.ReadAllText(f.Trace), Does.Contain("ls-remote --heads --").And.Contain("file://"));
            f.SameHistories(histories); Assert.That(f.Materials(), Is.EqualTo(materials));
            clone.Close(); await CloneInteractionFixture.Until(() => !clone.IsVisible);
        }
    }

    [AvaloniaTest]
    public async Task ChangingTheSourceCancelsTheOldNativeLoadAndCannotOverwriteNewBranchesOrANewerTypedChoice()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private SSH transport and actor identity witnesses require Linux."); return; }
        await using CloneInteractionFixture f = await CloneInteractionFixture.Create();
        CloneBranchGate gate = await f.Gate(); CloneDialog clone = await f.Show(f.SshSource);
        Dictionary<string, byte[]?> histories = f.Histories(); string materials = f.Materials();
        PushProcessTestDriver.Pointer(CloneInteractionFixture.Field<Button>(clone, "RefreshCloneBranches"));
        await CloneInteractionFixture.Until(() => gate.Markers.Length == 1 && clone.IsBranchLoading);
        CloneBranchActor[] actors = gate.Actors(gate.Markers.Single());
        AssertEditable(clone);
        CloneInteractionFixture.Edit(clone, CloneInteractionFixture.Field<TextBox>(clone, "CloneSource"), f.OtherFileSource);
        CloneInteractionFixture.Edit(clone, CloneInteractionFixture.Field<TextBox>(clone, "CloneBranch"), "topic-new");
        PushProcessTestDriver.Pointer(CloneInteractionFixture.Field<Button>(clone, "RefreshCloneBranches"));
        await CloneInteractionFixture.Until(() => !clone.IsBranchLoading && Branches(clone).Contains("topic-new") && actors.All(actor => !actor.Alive()));
        Assert.That(CloneInteractionFixture.Field<TextBox>(clone, "CloneSource").Text, Is.EqualTo(f.OtherFileSource));
        Assert.That(CloneInteractionFixture.Field<TextBox>(clone, "CloneBranch").Text, Is.EqualTo("topic-new"));
        Assert.That(Branches(clone).Skip(2), Is.EqualTo(new[] { "main", "topic-new" }));
        Assert.That(Branches(clone), Does.Not.Contain("topic-original"));
        Assert.That(gate.Markers, Has.Length.EqualTo(1), "The replacement source uses the real private file:// repository.");
        Assert.That(clone.BranchLoadError, Is.Null); Assert.That(clone.BranchLoadFailures, Is.Empty);
        Assert.That(clone.LastError, Is.Null); Assert.That(clone.LastProcess, Is.Null);
        f.SameHistories(histories); Assert.That(f.Materials(), Is.EqualTo(materials));
    }

    [AvaloniaTest]
    public async Task OpeningTheBranchDropdownReplacesARefreshAndTheOldFinallyCannotClearTheNewLoadingState()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private SSH transport and actor identity witnesses require Linux."); return; }
        await using CloneInteractionFixture f = await CloneInteractionFixture.Create();
        CloneBranchGate gate = await f.Gate(); CloneDialog clone = await f.Show(f.SshSource);
        Dictionary<string, byte[]?> histories = f.Histories(); string materials = f.Materials();
        PushProcessTestDriver.Pointer(CloneInteractionFixture.Field<Button>(clone, "RefreshCloneBranches"));
        await CloneInteractionFixture.Until(() => gate.Markers.Length == 1 && clone.IsBranchLoading);
        string first = gate.Markers.Single(); CloneBranchActor[] previous = gate.Actors(first);
        ComboBox branches = CloneInteractionFixture.Field<ComboBox>(clone, "CloneBranches");
        PushProcessTestDriver.Pointer(branches);
        await CloneInteractionFixture.Until(() => gate.Markers.Length == 2 && previous.All(actor => !actor.Alive()));
        string second = gate.Markers.Single(marker => marker != first); CloneBranchActor[] current = gate.Actors(second);
        Assert.That(clone.IsBranchLoading, Is.True, "Only the latest request owns the loading indicator.");
        Assert.That(current.All(actor => actor.Alive()), Is.True);
        AssertEditable(clone);
        CloneInteractionFixture.Press(clone, PhysicalKey.Escape);
        Assert.That(clone.IsVisible, Is.True); Assert.That(branches.IsDropDownOpen, Is.False);
        CloneInteractionFixture.Edit(clone, CloneInteractionFixture.Field<TextBox>(clone, "CloneBranch"), "manual choice during native load");
        await gate.Release(second);
        await CloneInteractionFixture.Until(() => !clone.IsBranchLoading && Branches(clone).Contains("topic-original") && current.All(actor => !actor.Alive()));
        Assert.That(CloneInteractionFixture.Field<TextBox>(clone, "CloneBranch").Text, Is.EqualTo("manual choice during native load"));
        Assert.That(Branches(clone).Skip(2), Is.EqualTo(new[] { "main", "topic-original" }));
        Assert.That(clone.BranchLoadError, Is.Null); Assert.That(clone.BranchLoadFailures, Is.Empty);
        Assert.That(clone.LastError, Is.Null); Assert.That(clone.LastProcess, Is.Null);
        f.SameHistories(histories); Assert.That(f.Materials(), Is.EqualTo(materials));
    }

    [AvaloniaTest]
    public async Task EveryFailedBranchRequestKeepsItsActualGitFailureAndANewerSuccessfulRequestClearsOnlyTheCurrentError()
    {
        await using CloneInteractionFixture f = await CloneInteractionFixture.Create();
        CloneDialog clone = await f.Show();
        Dictionary<string, byte[]?> histories = f.Histories(); string materials = f.Materials();
        List<GitCommandException> errors = [];
        foreach (string name in new[] { "missing first '\";$(literal) 雪", "missing second" })
        {
            string source = new Uri(Path.Combine(f.Root, name)).AbsoluteUri;
            CloneInteractionFixture.Edit(clone, CloneInteractionFixture.Field<TextBox>(clone, "CloneSource"), source);
            CloneInteractionFixture.Edit(clone, CloneInteractionFixture.Field<TextBox>(clone, "CloneBranch"), name);
            PushProcessTestDriver.Pointer(CloneInteractionFixture.Field<Button>(clone, "RefreshCloneBranches"));
            await CloneInteractionFixture.Until(() => !clone.IsBranchLoading && clone.BranchLoadError is GitCommandException);
            GitCommandException error = (GitCommandException)clone.BranchLoadError!;
            Assert.That(error.Arguments, Is.EqualTo(new[] { "ls-remote", "--heads", "--", source }));
            Assert.That(error.Result.Success, Is.False); Assert.That(error.Result.StandardError, Is.Not.Empty);
            Assert.That(clone.BranchLoadFailures.Last(), Is.SameAs(error));
            Assert.That(CloneInteractionFixture.Field<TextBox>(clone, "CloneBranch").Text, Is.EqualTo(name));
            Assert.That(clone.IsVisible && !clone.IsBusy, Is.True); Assert.That(clone.LastError, Is.Null);
            Assert.That(clone.CreatedRepository, Is.Null); Assert.That(clone.LastProcess, Is.Null);
            errors.Add(error);
        }
        Assert.That(errors[0], Is.Not.SameAs(errors[1]));
        Assert.That(clone.BranchLoadFailures, Is.EqualTo(errors));
        CloneInteractionFixture.Edit(clone, CloneInteractionFixture.Field<TextBox>(clone, "CloneSource"), f.OtherFileSource);
        CloneInteractionFixture.Edit(clone, CloneInteractionFixture.Field<TextBox>(clone, "CloneBranch"), "topic-new");
        PushProcessTestDriver.Pointer(CloneInteractionFixture.Field<Button>(clone, "RefreshCloneBranches"));
        await CloneInteractionFixture.Until(() => !clone.IsBranchLoading && Branches(clone).Contains("topic-new"));
        Assert.That(clone.BranchLoadError, Is.Null);
        Assert.That(clone.BranchLoadFailures, Is.EqualTo(errors), "A later success must not erase either completed native failure.");
        Assert.That(CloneInteractionFixture.Field<TextBox>(clone, "CloneBranch").Text, Is.EqualTo("topic-new"));
        f.SameHistories(histories); Assert.That(f.Materials(), Is.EqualTo(materials));
    }

    [AvaloniaTest]
    public async Task CancelEscapeAndOwnerClosingDrainTheirActualBranchTransportBeforeClosingAndNeverStartClone()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private SSH transport and actor identity witnesses require Linux."); return; }
        foreach (string gesture in new[] { "cancel", "escape", "owner" })
        {
            await using CloneInteractionFixture f = await CloneInteractionFixture.Create();
            CloneBranchGate gate = await f.Gate(); Window? owner = gesture == "owner" ? f.Owner() : null;
            CloneDialog clone = await f.Show(f.SshSource, owner: owner);
            Dictionary<string, byte[]?> histories = f.Histories(); string materials = f.Materials();
            PushProcessTestDriver.Pointer(CloneInteractionFixture.Field<Button>(clone, "RefreshCloneBranches"));
            await CloneInteractionFixture.Until(() => gate.Markers.Length == 1 && clone.IsBranchLoading);
            CloneBranchActor[] actors = gate.Actors(gate.Markers.Single());
            bool? actorsStoppedWhenClosed = null;
            clone.Closed += (_, _) => actorsStoppedWhenClosed = actors.All(actor => !actor.Alive());
            AssertEditable(clone);
            if (gesture == "cancel") { PushProcessTestDriver.Pointer(CloneInteractionFixture.Field<Button>(clone, "CancelClone")); }
            else if (gesture == "escape") { CloneInteractionFixture.Press(clone, PhysicalKey.Escape); }
            else { owner!.Close(); }
            await CloneInteractionFixture.Until(() => !clone.IsVisible && !clone.IsBranchLoading && actors.All(actor => !actor.Alive()));
            Assert.That(actorsStoppedWhenClosed, Is.True, "The Clone Closed event must run after all owned branch-load actors stop.");
            if (owner is not null) { await CloneInteractionFixture.Until(() => !owner.IsVisible); }
            Assert.That(clone.IsBusy, Is.False); Assert.That(clone.LastProcess, Is.Null); Assert.That(clone.CreatedRepository, Is.Null);
            Assert.That(clone.BranchLoadError, Is.Null); Assert.That(clone.BranchLoadFailures, Is.Empty);
            Assert.That(gate.Markers, Has.Length.EqualTo(1));
            string trace = File.ReadAllText(f.Trace);
            Assert.That(trace, Does.Contain("ls-remote").And.Not.Contain("clone -v").And.Not.Contain("fetch-pack"));
            f.SameHistories(histories); Assert.That(f.Materials(), Is.EqualTo(materials));
        }
    }

    [AvaloniaTest]
    public async Task ActualCloneCancelsAndDrainsBranchDiscoveryBeforeStartingItsNativeTransportAndPublishesOnlyAfterOk()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private SSH transport and actor identity witnesses require Linux."); return; }
        await using CloneInteractionFixture f = await CloneInteractionFixture.Create();
        CloneBranchGate gate = await f.Gate(); CloneDialog clone = await f.Show(f.SshSource);
        Dictionary<string, byte[]?> histories = f.Histories(); string materials = f.Materials();
        PushProcessTestDriver.Pointer(CloneInteractionFixture.Field<Button>(clone, "RefreshCloneBranches"));
        await CloneInteractionFixture.Until(() => gate.Markers.Length == 1 && clone.IsBranchLoading);
        string discovery = gate.Markers.Single(); CloneBranchActor[] discoveryActors = gate.Actors(discovery);
        AssertEditable(clone);
        PushProcessTestDriver.Pointer(CloneInteractionFixture.Field<Button>(clone, "CloneRepository"));
        await CloneInteractionFixture.Until(() => gate.Markers.Length == 2 && clone.OwnedWindows.OfType<GitProcessDialog>().Any());
        string transfer = gate.Markers.Single(marker => marker != discovery); CloneBranchActor[] transferActors = gate.Actors(transfer);
        gate.PriorActorsStoppedAtEntry(transfer, discoveryActors);
        Assert.That(discoveryActors.All(actor => !actor.Alive()), Is.True, "Native clone may start only after its old ls-remote process group is drained.");
        GitProcessDialog process = clone.OwnedWindows.OfType<GitProcessDialog>().Single();
        Assert.That(process.Owner, Is.SameAs(clone)); Assert.That(process.IsRunning, Is.True);
        Assert.That(clone.IsBusy, Is.True); Assert.That(clone.IsBranchLoading, Is.False);
        Assert.That(clone.CreatedRepository, Is.Null); f.SameHistories(histories);
        await gate.Release(transfer);
        await CloneInteractionFixture.Until(() => !process.IsRunning && transferActors.All(actor => !actor.Alive()));
        Assert.That(process.Result!.Success, Is.True, process.Error?.ToString());
        GitResult native = process.Result!; Assert.That(clone.CreatedRepository, Is.Null);
        Assert.That(clone.IsBusy, Is.True); Assert.That(process.IsVisible, Is.True); f.SameHistories(histories);
        PushProcessTestDriver.Pointer(CloneInteractionFixture.Field<Button>(process, "Ok"));
        await CloneInteractionFixture.Until(() => !clone.IsBusy && !clone.IsVisible);
        Assert.That(clone.LastError, Is.Null); Assert.That(clone.BranchLoadError, Is.Null); Assert.That(clone.BranchLoadFailures, Is.Empty);
        Assert.That(clone.LastProcess!.Result, Is.SameAs(native));
        Assert.That(clone.LastProcess.WasCancelled || clone.LastProcess.WorkflowStopRequested, Is.False);
        Assert.That(clone.CreatedRepository, Is.Not.Null);
        Assert.That(await clone.CreatedRepository!.ResolveRevisionAsync("HEAD"), Is.EqualTo(await f.Source.ResolveRevisionAsync("HEAD")));
        Assert.That(new CloneSourcesStore().Load().First(), Is.EqualTo(f.SshSource));
        Assert.That(new RepositoryHistoryStore().Load().Recent.First().Path, Is.EqualTo(clone.CreatedRepository.Path));
        Assert.That(gate.Markers, Has.Length.EqualTo(2)); Assert.That(f.Materials(), Is.EqualTo(materials));
    }

    private static string[] Branches(CloneDialog clone)
        => CloneInteractionFixture.Field<ComboBox>(clone, "CloneBranches").Items.Cast<string>().ToArray();
    private static void AssertEditable(CloneDialog clone)
    {
        Assert.That(clone.IsBranchLoading, Is.True); Assert.That(clone.IsBusy, Is.False);
        foreach (string name in new[] { "CloneSource", "CloneDestination", "CloneSubdirectory", "CloneBranch" })
        { Assert.That(CloneInteractionFixture.Field<TextBox>(clone, name).IsEnabled, Is.True, name); }
        foreach (string name in new[] { "RefreshCloneBranches", "CloneRepository", "CancelClone" })
        { Assert.That(CloneInteractionFixture.Field<Button>(clone, name).IsEnabled, Is.True, name); }
    }
}

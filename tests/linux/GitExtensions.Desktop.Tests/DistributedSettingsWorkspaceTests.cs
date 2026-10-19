using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DistributedSettingsWorkspaceTests
{
    [TestCase(GitDistributedSettingsScope.Local)]
    [TestCase(GitDistributedSettingsScope.Distributed)]
    [TestCase(GitDistributedSettingsScope.Global)]
    public async Task DetailedAndCiPagesKeepTheirDraftsAcrossSequentialAndRepeatedApply(GitDistributedSettingsScope scope)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        DistributedSettingsWorkspace workspace = new(f.Repository);
        DetailedSettingsSession detailed = new(f.Repository, f.Desktop.Load(), workspace);
        GitHubActionsSettingsSession ci = new(f.Repository, workspace);
        await Task.WhenAll(detailed.InitializeAsync(default), ci.InitializeAsync(default));
        Dictionary<string, byte[]> initial = f.Material();
        detailed.Set(scope, DetailedRepositorySettings.RemoteBranches, "true");
        ci.Set(scope, GitHubActionsSettings.OwnerKey, "fixture-owner");
        ci.Set(scope, GitHubActionsSettings.RepositoryKey, "fixture-repository");
        f.AssertMaterial(initial);
        await detailed.SaveFilesAsync(default);
        await ci.SaveFilesAsync(default);
        GitXmlSettingsSnapshot saved = await f.ReadAsync(scope);
        Assert.That(saved.Values[DetailedRepositorySettings.RemoteBranches], Is.EqualTo("true"));
        Assert.That(saved.Values[GitHubActionsSettings.OwnerKey], Is.EqualTo("fixture-owner"));
        Assert.That(saved.Values["future.λ"], Is.EqualTo("keep <literal> & \nbody"));
        // A second Apply must use the same acknowledged baseline, without making
        // either page reload and discard the other page's pending draft.
        detailed.Set(scope, DetailedRepositorySettings.MergeLogCount, "31");
        ci.Set(scope, GitHubActionsSettings.RepositoryKey, "second-repository");
        await ci.SaveFilesAsync(default);
        await detailed.SaveFilesAsync(default);
        saved = await f.ReadAsync(scope);
        Assert.That(saved.Values[DetailedRepositorySettings.MergeLogCount], Is.EqualTo("31"));
        Assert.That(saved.Values[GitHubActionsSettings.RepositoryKey], Is.EqualTo("second-repository"));
        Assert.That(saved.Values[DetailedRepositorySettings.RemoteBranches], Is.EqualTo("true"));
    }

    [Test]
    public async Task ASharedBaselineNeverSilentlyRebasesAnExternalXmlEdit()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        DistributedSettingsWorkspace workspace = new(f.Repository);
        DetailedSettingsSession detailed = new(f.Repository, f.Desktop.Load(), workspace);
        GitHubActionsSettingsSession ci = new(f.Repository, workspace);
        await detailed.InitializeAsync(default); await ci.InitializeAsync(default);
        detailed.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.RemoteBranches, "true");
        ci.Set(GitDistributedSettingsScope.Local, GitHubActionsSettings.OwnerKey, "fixture-owner");
        GitDistributedSettingsSnapshot before = workspace.Snapshot!;
        GitXmlSettingsSnapshot peer = await f.ReadAsync(GitDistributedSettingsScope.Local);
        await GitXmlSettingsStore.SaveChangesAsync(peer, new Dictionary<string, string?> { ["peer.external"] = "must survive" });
        byte[] external = File.ReadAllBytes(f.Path(GitDistributedSettingsScope.Local));
        Assert.CatchAsync<IOException>(async () => await detailed.SaveFilesAsync(default));
        Assert.CatchAsync<IOException>(async () => await ci.SaveFilesAsync(default));
        Assert.That(workspace.Snapshot, Is.SameAs(before));
        Assert.That(File.ReadAllBytes(f.Path(GitDistributedSettingsScope.Local)), Is.EqualTo(external));
    }

    [Test]
    public async Task AcceptedSnapshotsCannotRetargetTheSettingsWindowToAnotherRepository()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        DistributedSettingsWorkspace workspace = new(f.Repository); await workspace.InitializeAsync(default);
        GitDistributedSettingsSnapshot before = workspace.Snapshot!;
        GitXmlSettingsSnapshot foreign = await GitXmlSettingsStore.LoadAsync(f.Git.Output("foreign.settings"));
        Assert.Throws<InvalidOperationException>(() => workspace.AcceptSaved(new(before.Global, foreign, before.Distributed)));
        Assert.That(workspace.Snapshot, Is.SameAs(before));
    }

    [Test]
    public async Task CancellingUnpublishedDetailedAndCiDraftsCreatesNoXmlFiles()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(seed: false);
        DistributedSettingsWorkspace workspace = new(f.Repository);
        DetailedSettingsSession detailed = new(f.Repository, f.Desktop.Load(), workspace);
        GitHubActionsSettingsSession ci = new(f.Repository, workspace);
        await detailed.InitializeAsync(default); await ci.InitializeAsync(default);
        detailed.Set(GitDistributedSettingsScope.Local, DetailedRepositorySettings.RemoteBranches, "true");
        ci.Set(GitDistributedSettingsScope.Global, GitHubActionsSettings.OwnerKey, "unsaved-owner");
        Assert.That(f.XmlPaths.Any(File.Exists), Is.False);
    }
}

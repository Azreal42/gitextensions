using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FileHistoryEmptyTreeMenuTests
{
    [AvaloniaTest]
    public async Task UnbornIndexMenuDescribesEmptyTreeAndRefusesResetWithoutSubstitutingTheWorkingDirectory()
    {
        using Fixture fixture = await CreateAsync(); GitRepository repository = fixture.Repository;
        GitComparison pair = await repository.GetArtificialRevisionComparisonAsync(new("", GitCompareRevisionKind.Index));
        GitComparisonFileMetadata metadata = (await repository.GetComparisonFileMetadataAsync(pair, pair.Base)).Single();
        FileMenuLeaf leaf = new(metadata.File, pair.Base, pair.Compare, pair, Stage: FileMenuStage.Index, FirstIsParent: true);
        FileMenuSelection selection = new(repository, [leaf], [leaf.File.Path]); FileMenuPolicy policy = new(selection);
        Assert.That(policy.CanResetFirst, Is.False); Assert.That(policy.CanResetSecond, Is.False); Assert.That(policy.CanReset, Is.False);
        Assert.That(policy.CanNativeDiff, Is.True);
        Assert.That(FileMenuRevisionDescription.Describe([pair.Base], value => value), Is.EqualTo("Empty tree"));
        Assert.That(FileMenuRevisionDescription.Describe([pair.Compare], value => value), Is.EqualTo("Index"));
        using FileStatusListView view = new(new TreeView(), preferencesStore: new(fixture.Output("empty-tree-preferences")));
        FileMenuHost host = new()
        {
            CaptureSelection = () => selection, IsCurrent = _ => true, IsBusy = () => false,
            ExecuteAsync = (_, _) => throw new AssertionException("Building an empty-tree menu must not execute an action."),
            CreateAction = (_, name, label, _, _, gesture) => new MenuItem { Name = name, Header = label, InputGesture = gesture }
        };
        FileStatusListContextMenuController controller = new(view, TranslationCatalog.LoadDefault(), host);
        MenuItem[] menu = controller.BuildItems(selection).OfType<MenuItem>().ToArray();
        Assert.That(menu.Single(item => item.Name == "tsmiResetFileTo").IsEnabled, Is.False);
        Assert.That(menu.Single(item => item.Name == "tsmiOpenWithDifftool").IsEnabled, Is.True);
        Assert.That(Encoding.UTF8.GetString(await repository.GetComparisonFileBytesAsync(pair.Base, leaf.File.Path)), Is.Empty);
        Assert.That(Encoding.UTF8.GetString(await repository.GetComparisonFileBytesAsync(pair.Compare, leaf.File.Path)), Is.EqualTo("indexed contents\n"));
        Assert.That(await File.ReadAllTextAsync(fixture.File), Is.EqualTo("working contents\n"));
    }

    [AvaloniaTest]
    public async Task DirectResetFirstOnEmptyTreeIsRejectedBeforeAConfirmationOrAnyGitMutation()
    {
        using Fixture fixture = await CreateAsync(); GitComparison pair = await fixture.Repository.GetArtificialRevisionComparisonAsync(new("", GitCompareRevisionKind.Index));
        FileMenuLeaf leaf = new(pair.Files.Single(), pair.Base, pair.Compare, pair, Stage: FileMenuStage.Index, FirstIsParent: true);
        FileMenuSelection selection = new(fixture.Repository, [leaf], [leaf.File.Path]); Window owner = new(); owner.Show();
        string before = Snapshot(fixture.Repository.Path); int enteredRun = 0;
        try
        {
            FileMenuOperations operations = new(owner, async (_, action, _) => { enteredRun++; await action(CancellationToken.None); },
                _ => true, _ => throw new AssertionException("Reset must not launch an application."), CancellationToken.None);
            InvalidOperationException? error = null;
            try { await operations.ExecuteAsync(FileMenuCommand.ResetToFirst, selection); }
            catch (InvalidOperationException caught) { error = caught; }
            Assert.That(error?.Message, Does.Contain("no reset source")); Assert.That(enteredRun, Is.Zero);
            Assert.That(owner.OwnedWindows, Is.Empty); Assert.That(Snapshot(fixture.Repository.Path), Is.EqualTo(before));
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    public async Task RealDiffToolReceivesEmptyAndIndexInputsRatherThanLiveWorkingContentsInAnUnbornRepository()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("The native difftool fixture uses the installed Linux shell."); }
        using Fixture fixture = await CreateAsync(); string first = fixture.Output("tool-first"), second = fixture.Output("tool-second");
        await fixture.GitAsync("config", "difftool.empty-tree-test.cmd", "cp -- \"$LOCAL\" " + Quote(first) + " && cp -- \"$REMOTE\" " + Quote(second));
        GitComparison pair = await fixture.Repository.GetArtificialRevisionComparisonAsync(new("", GitCompareRevisionKind.Index));
        FileMenuLeaf leaf = new(pair.Files.Single(), pair.Base, pair.Compare, pair, Stage: FileMenuStage.Index, FirstIsParent: true);
        FileMenuSelection selection = new(fixture.Repository, [leaf], [leaf.File.Path]) { CustomDiffTool = "empty-tree-test" };
        Window owner = new(); owner.Show(); string before = Snapshot(fixture.Repository.Path);
        try
        {
            FileMenuOperations operations = new(owner, async (_, action, _) => await action(CancellationToken.None), _ => true,
                _ => throw new AssertionException("The diff tool is launched through real Git, not desktop shell open."), CancellationToken.None);
            await operations.ExecuteAsync(FileMenuCommand.DiffFirstToSecond, selection);
            Assert.That(await File.ReadAllBytesAsync(first), Is.Empty);
            Assert.That(await File.ReadAllTextAsync(second), Is.EqualTo("indexed contents\n"));
            Assert.That(await File.ReadAllTextAsync(fixture.File), Is.EqualTo("working contents\n"));
            Assert.That(Snapshot(fixture.Repository.Path), Is.EqualTo(before));
        }
        finally { owner.Close(); }
    }

    private static async Task<Fixture> CreateAsync()
    {
        Fixture fixture = await Fixture.CreateAsync(unborn: true);
        try
        {
            await File.WriteAllTextAsync(fixture.File, "indexed contents\n"); await fixture.Repository.StageAllAsync();
            await File.WriteAllTextAsync(fixture.File, "working contents\n"); return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string Snapshot(string root) => string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));
}

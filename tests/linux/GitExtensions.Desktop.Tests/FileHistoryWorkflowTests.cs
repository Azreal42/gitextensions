using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FileHistoryWorkflowTests
{
    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.Commit, false)]
    [TestCase(GitCompareRevisionKind.Index, false)]
    [TestCase(GitCompareRevisionKind.WorkingTree, false)]
    [TestCase(GitCompareRevisionKind.Commit, true)]
    [TestCase(GitCompareRevisionKind.Index, true)]
    [TestCase(GitCompareRevisionKind.WorkingTree, true)]
    public async Task DefaultBrowseCapturesLiteralTypedSelectionWithoutAModalOrAHistoryBound(GitCompareRevisionKind kind, bool directory)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        byte[] index = await File.ReadAllBytesAsync(fixture.PathInRepository(".git/index"));
        const string path = "literal [*]= λ'\tline\nname";
        GitCompareRevision selected = kind == GitCompareRevisionKind.Commit ? new(head) : new("", kind);
        PreferencesStore preferences = new();
        Assert.That(File.Exists(preferences.FilePath), Is.False);
        Window owner = new(); owner.Show();
        ProcessStartInfo? captured = null;
        int launches = 0;
        try
        {
            await FileHistoryWorkflow.OpenAsync(owner, fixture.Repository, path, directory, selected, CancellationToken.None,
                launch: info => { captured = info; launches++; return new Process(); }, initialBlame: true);
            Assert.That(launches, Is.EqualTo(1));
            Assert.That(owner.OwnedWindows, Is.Empty, "The Windows Browse choice ignores initialBlame and opens an independent application.");
            Assert.That(captured!.UseShellExecute, Is.False);
            Assert.That(captured.WorkingDirectory, Is.EqualTo(fixture.Repository.Path));
            string[] argv = captured.ArgumentList.SkipWhile(value => value != "browse").ToArray();
            DesktopStartupArguments parsed = DesktopStartupArguments.Parse(argv);
            Assert.That(parsed.BrowseRequest, Is.EqualTo(new BrowseRequest(fixture.Repository.Path,
                new(path, directory ? BrowsePathKind.Directory : BrowsePathKind.File), selected)));
            Assert.That(parsed.BrowseRequest!.FirstRevision, Is.Null);
            Assert.That(File.Exists(preferences.FilePath), Is.False, "Temporary Browse state must not write ordinary preferences.");
            Assert.That(await File.ReadAllBytesAsync(fixture.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
            Assert.That(await File.ReadAllTextAsync(fixture.File), Is.EqualTo("base\n"));
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task PrecancelledTokenCannotLaunchEitherWorkflow(bool dedicated)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        if (dedicated) { PreferencesStore store = new(); store.Save(store.Load() with { UseBrowseForFileHistory = false }); }
        Window owner = new(); owner.Show(); int launches = 0;
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        try
        {
            Exception? failure = null;
            try
            {
                await FileHistoryWorkflow.OpenAsync(owner, fixture.Repository, "file.txt", false, new("HEAD"), cancellation.Token,
                    _ => { launches++; return new Process(); });
            }
            catch (Exception exception) { failure = exception; }
            Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            Assert.That(launches, Is.Zero); Assert.That(owner.OwnedWindows, Is.Empty);
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task TransportFailureRemainsObservableWithoutFallingBackToAnotherHistory(bool throws)
    {
        using Fixture fixture = await Fixture.CreateAsync(); Window owner = new(); owner.Show();
        IOException expected = new("Native Browse transport fixture failure."); int launches = 0;
        try
        {
            Exception? failure = null;
            try
            {
                await FileHistoryWorkflow.OpenAsync(owner, fixture.Repository, "file.txt", false, new("HEAD"), CancellationToken.None,
                    _ => { launches++; if (throws) { throw expected; } return null; });
            }
            catch (Exception exception) { failure = exception; }
            Assert.That(failure, Is.InstanceOf<IOException>());
            if (throws) { Assert.That(failure, Is.SameAs(expected)); }
            else { Assert.That(failure!.Message, Does.Contain("could not be started")); }
            Assert.That(launches, Is.EqualTo(1)); Assert.That(owner.OwnedWindows, Is.Empty);
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.Commit)]
    [TestCase(GitCompareRevisionKind.Index)]
    [TestCase(GitCompareRevisionKind.WorkingTree)]
    public async Task ExplicitDedicatedPreferenceKeepsTheRealHistoryDialogAndItsSelectedSource(GitCompareRevisionKind kind)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        PreferencesStore preferences = new(); preferences.Save(preferences.Load() with { UseBrowseForFileHistory = false });
        byte[] before = await File.ReadAllBytesAsync(preferences.FilePath);
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        GitCompareRevision selected = kind == GitCompareRevisionKind.Commit ? new(head) : new("", kind);
        Window owner = new(); owner.Show();
        int launches = 0;
        Task workflow = FileHistoryWorkflow.OpenAsync(owner, fixture.Repository, "file.txt", false, selected, CancellationToken.None,
            _ => { launches++; throw new AssertionException("The explicit dedicated preference must not launch Browse."); });
        FileHistoryDialog? dialog = null;
        try
        {
            await Until(() => owner.OwnedWindows.OfType<FileHistoryDialog>().Any());
            dialog = owner.OwnedWindows.OfType<FileHistoryDialog>().Single();
            await Until(() => !dialog.IsBusy && dialog.History.Count > 0);
            Assert.That(dialog.InitialSource, Is.EqualTo(selected));
            Assert.That(PushProcessTestDriver.Field<TextBox>(dialog, "FileHistoryPath").Text, Is.EqualTo("file.txt"));
            Assert.That(PushProcessTestDriver.Field<TextBox>(dialog, "FileHistoryRevision").Text, Is.EqualTo(kind == GitCompareRevisionKind.Commit ? head : "HEAD"));
            Assert.That(dialog.History.Select(entry => entry.Commit.Hash), Does.Contain(head));
            Assert.That(dialog.LastError, Is.Null);
            Assert.That(workflow.IsCompleted, Is.False, "The dedicated workflow still waits for its real modal.");
            Assert.That(launches, Is.Zero);
            Assert.That(await File.ReadAllBytesAsync(preferences.FilePath), Is.EqualTo(before));
        }
        finally
        {
            dialog ??= owner.OwnedWindows.OfType<FileHistoryDialog>().FirstOrDefault();
            if (dialog is not null)
            {
                dialog.Close();
                await Until(() => !dialog.IsVisible && dialog.InformationCompletion.IsCompleted);
            }
            await workflow.WaitAsync(TimeSpan.FromSeconds(15)); owner.Close();
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15))
        { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs();
        Assert.That(condition(), Is.True, "The real FileHistory modal did not reach the expected state.");
    }
}

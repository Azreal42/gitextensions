using System.Reflection;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Scope = GitExtensions.Desktop.Tests.ProgressiveBrowseTestScope;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
[SupportedOSPlatform("linux")]
public sealed class ProgressiveRevisionLogMutationTests
{
    [AvaloniaTest]
    public async Task ALogMutationPublishesBeforeItsHistoryTailDrainsAndRefreshesBrowseExactlyOnceAfterModalReturn()
    {
        await using Scope f = await Scope.CreateAsync();
        using var presentation = f.EnterPresentation();
        MainWindow browse = await f.OpenAsync(); await Scope.Complete(browse);
        object browseSource = Scope.Field<ListBox>(browse, "RevisionHistory").ItemsSource!;
        string[] browseHashes = Scope.Hashes(browse);
        byte[] index = await File.ReadAllBytesAsync(f.Root.PathInRepository(".git/index"));
        string working = await File.ReadAllTextAsync(f.Root.File);
        const string tagName = "progressive-log-mutation";
        string tagRef = "refs/tags/" + tagName;
        Assert.That(References(browse).Any(reference => reference.Name == tagRef), Is.False);

        MainWindow? log = null; Task mutation = Task.CompletedTask;
        Task modal = browse.ShowRevisionLogAsync();
        try
        {
            await Scope.Until(browse, () => browse.OwnedWindows.OfType<MainWindow>()
                .Any(window => window.WindowKind == MainWindowKind.RevisionLog));
            log = browse.OwnedWindows.OfType<MainWindow>().Single(window => window.WindowKind == MainWindowKind.RevisionLog);
            f.Track(log); await Scope.Complete(log);
            Assert.That(log.Owner, Is.SameAs(browse));
            Assert.That(Private<GitRepository>(log, "_repository"), Is.Not.SameAs(Private<GitRepository>(browse, "_repository")));
            Task previousPublication = Private<Task>(log, "_repositoryChangePublication");
            int previousGraphReads = f.Gate.Commands.Count(NativeHistoryGate.IsGraph);
            NativeHistoryGate.Slot tail = f.Gate.Arm();
            MainWindow source = log;
            mutation = RunMutation(log, async repository =>
            {
                CancellationToken token = PrivateProperty<CancellationToken>(source, "Token");
                GitResult tagged = await repository.CreateTagAsync(tagName, f.Head, cancellationToken: token);
                Assert.That(tagged.Success, Is.True, tagged.StandardError);
            });
            await Scope.Ready(log, tail);
            Task history = Scope.HistoryWork(log);
            Task mutationFrontier = PrivateProperty<Task>(log, "RepositoryMutationCompletion");
            Task publication = Private<Task>(log, "_repositoryChangePublication");
            Assert.That(publication, Is.Not.SameAs(previousPublication), "RunAsync(refresh:true) must admit a real standalone mutation notification.");

            // This deliberately does not await global idle: the actual producer remains retained.
            // The modal owner lock must still prevent delivery to Browse until Log returns.
            await Scope.Until(log, () => mutation.IsCompleted && mutationFrontier.IsCompleted && publication.IsCompleted);
            await mutation; await mutationFrontier; await publication;
            Assert.Multiple(() =>
            {
                Assert.That(mutation.IsCompletedSuccessfully, Is.True);
                Assert.That(mutationFrontier.IsCompletedSuccessfully, Is.True);
                Assert.That(publication.IsCompletedSuccessfully, Is.True);
                Assert.That(Private<bool>(source, "_repositoryChangePublicationPending"), Is.False);
                Assert.That(source.StandaloneMayHaveRepositoryChanges, Is.True);
                Assert.That(source.IsBusy, Is.False);
                Assert.That(source.LastException, Is.Null, source.LastException?.ToString());
                Assert.That(Scope.Loading(source), Is.True);
                Assert.That(history.IsCompleted, Is.False);
                Assert.That(Scope.HistoryWork(source).IsCompleted, Is.False);
                Assert.That(source.WindowWorkCompletion.IsCompleted, Is.False);
                Assert.That(tail.Paused(100), Is.True);
                Assert.That(tail.Identities.All(NativeHistoryGate.Present), Is.True);
                Assert.That(Scope.Hashes(source), Has.Length.EqualTo(100));
                Assert.That(References(source).Single(reference => reference.Name == tagRef).Hash, Is.EqualTo(f.Head));
                Assert.That(References(browse).Any(reference => reference.Name == tagRef), Is.False);
                Assert.That(Scope.Field<ListBox>(browse, "RevisionHistory").ItemsSource, Is.SameAs(browseSource));
                Assert.That(Scope.Hashes(browse), Is.EqualTo(browseHashes));
                Assert.That(browse.IsBusy, Is.True, "Browse still owns the modal Log operation.");
                Assert.That(modal.IsCompleted, Is.False);
                Assert.That(f.Gate.Commands.Count(NativeHistoryGate.IsGraph), Is.EqualTo(previousGraphReads + 1),
                    "Only the source Log has started a new history traversal; Browse has not refreshed while its modal is open.");
            });

            log.Close(); // No test release: the real close must cancel and drain both native actors.
            await Scope.Until(browse, () => !source.IsVisible && history.IsCompleted
                && source.WindowWorkCompletion.IsCompleted && modal.IsCompleted);
            await history; await source.WindowWorkCompletion; await modal; tail.AssertRetired();
            await Scope.Complete(browse);
            Assert.Multiple(() =>
            {
                Assert.That(browse.IsVisible, Is.True); Assert.That(browse.OwnedWindows, Is.Empty);
                Assert.That(browse.LastException, Is.Null, browse.LastException?.ToString());
                Assert.That(Scope.Field<ListBox>(browse, "RevisionHistory").ItemsSource, Is.SameAs(browseSource));
                Assert.That(Scope.Hashes(browse), Is.EqualTo(browseHashes));
                Assert.That(References(browse).Single(reference => reference.Name == tagRef).Hash, Is.EqualTo(f.Head));
                Assert.That(f.Gate.Commands.Count(NativeHistoryGate.IsGraph), Is.EqualTo(previousGraphReads + 2),
                    "Exactly one receiving Browse traversal follows the coalesced modal-return notification.");
            });
            Assert.That((await f.Git("rev-parse", "--verify", tagRef)).StandardOutput.Trim(), Is.EqualTo(f.Head));
            Assert.That(await File.ReadAllBytesAsync(f.Root.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(await File.ReadAllTextAsync(f.Root.File), Is.EqualTo(working));
        }
        finally
        {
            // A failed setup may not yet have assigned its already-mapped modal child.
            if (log is null)
            {
                log = browse.OwnedWindows.OfType<MainWindow>().FirstOrDefault(window => window.WindowKind == MainWindowKind.RevisionLog);
                if (log is not null) { f.Track(log); }
            }
            // Cleanup may release only after initiating close. The passing path has already proved retirement without release.
            if (log?.IsVisible == true) { log.Close(); }
            if (log is null && !modal.IsCompleted) { browse.Close(); }
            if (!modal.IsCompleted || !mutation.IsCompleted || log is not null
                && (!Scope.HistoryWork(log).IsCompleted || !log.WindowWorkCompletion.IsCompleted)) { f.Gate.ReleaseAll(); }
            await Scope.Until(browse, () => mutation.IsCompleted && modal.IsCompleted && (log is null
                || !log.IsVisible && Scope.HistoryWork(log).IsCompleted && log.WindowWorkCompletion.IsCompleted));
            await mutation;
            if (log is not null)
            {
                await Scope.HistoryWork(log); await log.WindowWorkCompletion;
            }
            await modal;
        }
    }

    private static IEnumerable<GitReference> References(MainWindow window)
        => Private<ListBox>(window, "_references").Items.OfType<GitReference>();
    private static T Private<T>(MainWindow window, string name) => (T)(typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
        ?? throw new AssertionException("Missing actual source field " + name));
    private static T PrivateProperty<T>(MainWindow window, string name) => (T)(typeof(MainWindow)
        .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
        ?? throw new AssertionException("Missing actual source property " + name));
    private static Task RunMutation(MainWindow window, Func<GitRepository, Task> action) => (Task)(typeof(MainWindow)
        .GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(window, ["Private real Log tag mutation", action, true, true])!);
}

using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ErrorReportRoutingTests
{
    [AvaloniaTest]
    public async Task ActualGitOpenFailureReturnsNormallyAndStatusOpensTheSameOriginalExceptionSnapshot()
    {
        await using Scope f = await Scope.CreateAsync(); MainWindow main = f.Main;
        string invalid = f.EmptyDirectory("not-a-repository"); await main.OpenRepositoryAsync(invalid);
        Assert.That(main.LastException, Is.TypeOf<GitCommandException>());
        Exception original = main.LastException!; ExceptionReportSnapshot snapshot = main.ErrorReportSnapshot!;
        Assert.That(snapshot.OriginalException, Is.SameAs(original)); Assert.That(main.OwnedWindows, Is.Empty);
        Assert.That(main.ErrorReportContextSnapshot!.WorkingDirectory, Is.EqualTo(invalid));
        string status = Field<TextBlock>(main, "RepositoryStatus").Text!;
        Assert.That(status, Does.Contain("Could not open repository"));
        Button access = Field<Button>(main, "ViewErrorDetails"); Assert.That(access.IsEffectivelyVisible && access.IsEnabled, Is.True);
        PushProcessTestDriver.Pointer(access); BugReportDialog report = await f.Report();
        Assert.That(report.Snapshot, Is.SameAs(snapshot)); Assert.That(report.Snapshot.OriginalException, Is.SameAs(original));
        Assert.That(report.Owner, Is.SameAs(main)); Assert.That(main.WindowWorkCompletion.IsCompleted, Is.False);
        Assert.That(Field<TabControl>(report, "mainTabs").SelectedIndex, Is.EqualTo(1));
        Assert.That(Field<Button>(report, "sendAndQuitButton").IsEnabled, Is.False);
        PushProcessTestDriver.Pointer(Field<Button>(report, "IgnoreButton")); await PushProcessTestDriver.CompleteAsync(main.ErrorReportCompletion, main);
        Assert.That(main.LastException, Is.SameAs(original)); Assert.That(Field<TextBlock>(main, "RepositoryStatus").Text, Is.EqualTo(status));
        Assert.That(access.IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(main, PhysicalKey.Enter);
        BugReportDialog keyboardReport = await f.Report(); Assert.That(keyboardReport.Snapshot, Is.SameAs(snapshot));
        PushProcessTestDriver.Pointer(Field<Button>(keyboardReport, "IgnoreButton")); await PushProcessTestDriver.CompleteAsync(main.ErrorReportCompletion, main);
        await main.OpenRepositoryAsync(f.Git.Repository.Path);
        Assert.That(main.LastException, Is.Null); Assert.That(main.ErrorReportSnapshot, Is.Null); Assert.That(access.IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task ASecondFailureReplacesTheButtonContextAndCopyCapturesOnlyTheNewImmutableReport()
    {
        await using Scope f = await Scope.CreateAsync(); List<string> copied = [];
        f.Main.ErrorReportCopyTextAsync = (text, _) => { copied.Add(text); return Task.CompletedTask; };
        await f.Main.OpenRepositoryAsync(f.Git.Repository.Path);
        string first = f.EmptyDirectory("first"), second = f.EmptyDirectory("second");
        await f.Main.OpenRepositoryAsync(first); ExceptionReportSnapshot old = f.Main.ErrorReportSnapshot!;
        await f.Main.OpenRepositoryAsync(second); ExceptionReportSnapshot current = f.Main.ErrorReportSnapshot!;
        Assert.That(current, Is.Not.SameAs(old)); Assert.That(f.Main.ErrorReportContextSnapshot!.WorkingDirectory, Is.EqualTo(second));
        PushProcessTestDriver.Pointer(Field<Button>(f.Main, "ViewErrorDetails")); BugReportDialog report = await f.Report();
        PushProcessTestDriver.Pointer(Field<Button>(report, "btnCopy")); await PushProcessTestDriver.UntilAsync(report, () => !report.IsBusy);
        Assert.That(report.Snapshot, Is.SameAs(current)); Assert.That(copied.Single(), Does.Contain(second).And.Not.Contain(first));
        Assert.That(f.Main.LastException, Is.SameAs(current.OriginalException));
        PushProcessTestDriver.Pointer(Field<Button>(report, "IgnoreButton"));
        await PushProcessTestDriver.CompleteAsync(f.Main.ErrorReportCompletion, f.Main);
        object readSequence = typeof(MainWindow).GetField("_treeFileRead", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Main)!;
        Func<GitRepository, CancellationToken, Task> readInvalidRevision = async (repository, token) =>
        { await repository.ResolveRevisionAsync("missing-revision-for-error-report", token); };
        Task read = (Task)typeof(MainWindow).GetMethod("ReadBrowserAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Main, [readSequence, "File tree", readInvalidRevision, CancellationToken.None])!;
        await read.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(f.Main.LastException, Is.TypeOf<GitCommandException>().And.Not.SameAs(current.OriginalException));
        Assert.That(Field<TextBlock>(f.Main, "RepositoryStatus").Text, Does.StartWith("File tree failed"));
        Assert.That(f.Main.ErrorReportSnapshot, Is.Null);
        Assert.That(Field<Button>(f.Main, "ViewErrorDetails").IsVisible, Is.False,
            "A new read failure outside RunAsync must not leave the old report attached to its status.");
    }

    [AvaloniaTest]
    public async Task CancellationDoesNotPublishAReportOrTurnIntoANewErrorButton()
    {
        await using Scope f = await Scope.CreateAsync(); await f.Main.OpenRepositoryAsync(f.EmptyDirectory("previous failure"));
        Task operation = (Task)typeof(MainWindow).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Main, ["Cancelled operation", (Func<GitRepository, Task>)(_ => Task.FromException(new OperationCanceledException())), false, false])!;
        await operation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(f.Main.LastException, Is.Null); Assert.That(f.Main.ErrorReportSnapshot, Is.Null);
        Assert.That(Field<Button>(f.Main, "ViewErrorDetails").IsVisible, Is.False); Assert.That(f.Main.OwnedWindows, Is.Empty);
        Assert.That(Field<TextBlock>(f.Main, "RepositoryStatus").Text, Is.EqualTo("Operation cancelled"));
    }

    [AvaloniaTest]
    public async Task OwnerCloseCancelsTheReportButWaitsForItsClipboardLeaseBeforeActuallyClosing()
    {
        await using Scope f = await Scope.CreateAsync();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Main.ErrorReportCopyTextAsync = async (_, _) => { started.SetResult(); await release.Task; };
        await f.Main.OpenRepositoryAsync(f.EmptyDirectory("failure"));
        PushProcessTestDriver.Pointer(Field<Button>(f.Main, "ViewErrorDetails")); BugReportDialog report = await f.Report();
        PushProcessTestDriver.Pointer(Field<Button>(report, "btnCopy")); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            f.Main.Close(); PortableHelperSettingsFixture.Pump(f.Main);
            Assert.That(f.Main.IsVisible, Is.True); Assert.That(f.Main.WindowWorkCompletion.IsCompleted, Is.False);
            Assert.That(f.Main.ErrorReportSnapshot, Is.Null); Assert.That(Field<Button>(f.Main, "ViewErrorDetails").IsVisible, Is.False);
        }
        finally { release.TrySetResult(); }
        await PushProcessTestDriver.UntilAsync(f.Main, () => !f.Main.IsVisible);
        await f.Main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(report.IsVisible, Is.False); Assert.That(f.Main.ErrorReportCompletion.IsCompletedSuccessfully, Is.True);
    }

    [AvaloniaTest]
    public async Task QuitClosesTheHostThroughItsNormalDrainedLifetimeWithoutRetryingGit()
    {
        await using Scope f = await Scope.CreateAsync(); await f.Main.OpenRepositoryAsync(f.Git.Repository.Path);
        string before = await f.Git.Repository.ResolveRevisionAsync("HEAD");
        await f.Main.OpenRepositoryAsync(f.EmptyDirectory("failure after valid repository"));
        PushProcessTestDriver.Pointer(Field<Button>(f.Main, "ViewErrorDetails")); BugReportDialog report = await f.Report();
        PushProcessTestDriver.Pointer(Field<Button>(report, "quitButton"));
        await PushProcessTestDriver.UntilAsync(f.Main, () => !f.Main.IsVisible);
        await f.Main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(await f.Git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(before));
        Assert.That(f.Main.ErrorReportCompletion.IsCompletedSuccessfully, Is.True);
    }

    [AvaloniaTest]
    public async Task ChangingRepositoryWhileAnOldReportActionDrainsCannotRepublishThatError()
    {
        await using Scope f = await Scope.CreateAsync();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Main.ErrorReportCopyTextAsync = async (_, _) => { started.SetResult(); await release.Task; };
        await f.Main.OpenRepositoryAsync(f.EmptyDirectory("old failure"));
        PushProcessTestDriver.Pointer(Field<Button>(f.Main, "ViewErrorDetails")); BugReportDialog report = await f.Report();
        PushProcessTestDriver.Pointer(Field<Button>(report, "btnCopy")); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await f.Main.OpenRepositoryAsync(f.Git.Repository.Path);
            Assert.That(f.Main.ErrorReportSnapshot, Is.Null); Assert.That(Field<Button>(f.Main, "ViewErrorDetails").IsVisible, Is.False);
        }
        finally { release.TrySetResult(); }
        await PushProcessTestDriver.CompleteAsync(f.Main.ErrorReportCompletion, f.Main);
        Assert.That(report.IsVisible, Is.False); Assert.That(f.Main.LastException, Is.Null);
        Assert.That(f.Main.ErrorReportSnapshot, Is.Null); Assert.That(Field<Button>(f.Main, "ViewErrorDetails").IsVisible, Is.False);
    }

    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private sealed class Scope(GitFixture git, MainWindow main) : IAsyncDisposable
    {
        internal GitFixture Git { get; } = git;
        internal MainWindow Main { get; } = main;
        internal static async Task<Scope> CreateAsync()
        { GitFixture git = await GitFixture.CreateAsync(); MainWindow main = new(); main.Show(); PortableHelperSettingsFixture.Pump(main); return new(git, main); }
        internal string EmptyDirectory(string name) { string path = Git.Output(name); Directory.CreateDirectory(path); return path; }
        internal async Task<BugReportDialog> Report()
        { await PushProcessTestDriver.UntilAsync(Main, () => Main.OwnedWindows.OfType<BugReportDialog>().Any()); return Main.OwnedWindows.OfType<BugReportDialog>().Single(); }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Main.IsVisible) { Main.Close(); }
                await PushProcessTestDriver.UntilAsync(Main, () => !Main.IsVisible);
                await Main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally { Git.Dispose(); }
        }
    }
}

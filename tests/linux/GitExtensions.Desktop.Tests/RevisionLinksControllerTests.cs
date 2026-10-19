using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands.ExternalLinks;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RevisionLinksControllerTests
{
    [AvaloniaTest]
    public async Task NativeConsumerShowsLiteralDistinctCaptionsUsesExactRefsAndDoesNotLaunchBeforeClick()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        await f.Git.GitAsync("commit", "--amend", "-m", "Subject #7", "-m", "Body #8");
        GitCommit selected = (await f.Repository!.GetLogAsync(1, all: false)).Single();
        await f.Git.GitAsync("notes", "add", "-m", "Notes #9", selected.Hash);
        await f.Git.GitAsync("branch", "fix/42", selected.Hash); await f.Git.GitAsync("tag", "issue/43", selected.Hash);
        await f.Git.ChangeAsync("later\n", "Later"); await f.Git.GitAsync("branch", "fix/99");
        ExternalLinkDefinition message = RevisionLinksSettingsSessionTests.Definition("message", "https://example.invalid/{0}"); message.LinkFormats[0].Caption = "Literal <a>& #{0}";
        message.LinkFormats.Add(new() { Caption = "duplicate ignored", Format = "https://example.invalid/{0}" });
        ExternalLinkDefinition attached = new()
        {
            Name = "attached", Enabled = true, SearchPattern = "(?:fix/|issue/)(\\d+)", SearchInParts = { ExternalLinkDefinition.RevisionPart.LocalBranches },
            LinkFormats = { new() { Caption = "ref {0}", Format = "https://example.invalid/ref/{0}" } }
        };
        f.Write(GitDistributedSettingsScope.Global, (GitRevisionLinkCodec.SettingName, GitRevisionLinkCodec.Encode([message, attached])!));
        using CommitInformationView view = new(); List<Uri> opened = []; view.RevisionLinkLauncher = uri => { opened.Add(uri); return Task.CompletedTask; };
        Window window = new() { Content = view, Width = 850, Height = 750 }; window.Show();
        try
        {
            view.SetRevision(f.Repository, selected); await view.LoadAsync(); Pump(window);
            Button[] links = Related(view); Assert.That(links.Length, Is.EqualTo(4)); Assert.That(opened, Is.Empty);
            Assert.That(links.Select(link => ((TextBlock)link.Content!).Text), Does.Contain("Literal <a>& #7").And.Contain("Literal <a>& #8"));
            Assert.That(links.Select(link => ((ValueTuple<CommitInformationCommand, string>)link.Tag!).Item2),
                Is.EquivalentTo(new[] { "https://example.invalid/7", "https://example.invalid/8", "https://example.invalid/ref/42", "https://example.invalid/ref/43" }));
            Assert.That(view.CopyText, Does.Not.Contain("Related links:")); Assert.That(view.Text, Does.Contain("Related links:"));
            PushProcessTestDriver.Pointer(links.Single(link => ((ValueTuple<CommitInformationCommand, string>)link.Tag!).Item2.EndsWith("/7", StringComparison.Ordinal))); Pump(window);
            Assert.That(opened.Select(uri => uri.AbsoluteUri), Is.EqualTo(new[] { "https://example.invalid/7" }));
        }
        finally { view.BeginClose(); await view.Completion; window.Close(); }
    }

    [AvaloniaTest]
    public async Task SavingMatchingSettingsReloadsOnlyRelatedLinksAndKeepsFocusedMessageAndSelection()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        await f.Git.GitAsync("commit", "--amend", "-m", "Subject #7"); GitCommit selected = (await f.Repository!.GetLogAsync(1, all: false)).Single();
        f.Write(GitDistributedSettingsScope.Global, (GitRevisionLinkCodec.SettingName, GitRevisionLinkCodec.Encode([RevisionLinksSettingsSessionTests.Definition("before", "https://before.invalid/{0}")])!));
        RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        using CommitInformationView view = new(); Window window = new() { Content = view, Width = 850, Height = 650 }; window.Show();
        try
        {
            view.SetRevision(f.Repository, selected); await view.LoadAsync(); Pump(window); view.MessageControl.Focus(); view.MessageControl.SelectionStart = 2; view.MessageControl.SelectionEnd = 6;
            Control header = view.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "CommitInformationCommit hash".Replace(" ", ""));
            string message = view.MessageControl.Text!;
            session.Set(GitDistributedSettingsScope.Global, [RevisionLinksSettingsSessionTests.Definition("after", "https://after.invalid/{0}")]); await session.SaveFilesAsync(default);
            await DetailedSettingsUiHost.Until(() => Related(view).Any(link => ((ValueTuple<CommitInformationCommand, string>)link.Tag!).Item2.StartsWith("https://after.invalid/", StringComparison.Ordinal))); Pump(window);
            Assert.That(view.MessageControl.IsKeyboardFocusWithin, Is.True); Assert.That(view.MessageControl.SelectionStart, Is.EqualTo(2)); Assert.That(view.MessageControl.SelectionEnd, Is.EqualTo(6));
            Assert.That(view.MessageControl.Text, Is.EqualTo(message)); Assert.That(view.GetVisualDescendants().Contains(header), Is.True);
            GitCommandLog.Clear(); RevisionLinksSettingsPublication.Publish(f.Git.Output("unrelated.settings")); Pump(window); await view.Completion;
            Assert.That(GitCommandLog.Snapshot(), Is.Empty, "Unrelated profiles must not reload this panel.");
        }
        finally { view.BeginClose(); await view.Completion; window.Close(); }
    }

    [AvaloniaTest]
    public async Task ArtificialMessageStateCannotBeRepopulatedByASettingsPublication()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        using CommitInformationView view = new(); Window window = new() { Content = view }; window.Show();
        try
        {
            view.SetRevision(f.Repository, (await f.Repository!.GetLogAsync(1, all: false)).Single()); await view.LoadAsync();
            view.SetMessage("Working directory"); GitCommandLog.Clear(); RevisionLinksSettingsPublication.Publish(DetailedRepositorySettings.GlobalPath); Pump(window); await view.Completion;
            Assert.That(view.MessageControl.Text, Is.EqualTo("Working directory")); Assert.That(Related(view), Is.Empty); Assert.That(GitCommandLog.Snapshot(), Is.Empty);
        }
        finally { view.BeginClose(); await view.Completion; window.Close(); }
    }

    [AvaloniaTest]
    public async Task CancelledStaleGenerationCannotPublishButCloseStillDrainsItsRealReader()
    {
        using Fixture f = await Fixture.CreateAsync(); string firstHash = await f.Repository.ResolveRevisionAsync("HEAD");
        TaskCompletionSource entered = new(), release = new(); CancellationToken oldToken = default;
        using RevisionLinksController controller = new(async (_, hash, token) =>
        {
            if (hash == firstHash) { oldToken = token; entered.SetResult(); await release.Task; return Result("stale"); }
            return Result("current");
        });
        List<string> values = []; controller.Updated += result => values.Add(result.Links.Single().Uri);
        controller.SetRevision(f.Repository, firstHash); Task first = controller.LoadAsync(); await entered.Task;
        controller.SetRevision(f.Repository, "different-generation"); await controller.LoadAsync(); Assert.That(oldToken.IsCancellationRequested, Is.True);
        Task close = controller.BeginClose(); Assert.That(close.IsCompleted, Is.False, "A cancelled worker is still owned until it actually finishes.");
        release.SetResult(); await first; await close; Assert.That(values, Is.EqualTo(new[] { "current" }));
    }

    [AvaloniaTest]
    public async Task QueuedPublicationFromOldAttachmentCannotReloadAfterDetachAndReattach()
    {
        using Fixture f = await Fixture.CreateAsync(); int reads = 0;
        using RevisionLinksController controller = new((_, _, _) => { reads++; return Task.FromResult(Result("value")); });
        controller.SetRevision(f.Repository, await f.Repository.ResolveRevisionAsync("HEAD")); controller.Attach(); await controller.LoadAsync();
        RevisionLinksSettingsPublication.Publish(DetailedRepositorySettings.GlobalPath); controller.Detach(); controller.Attach(); Dispatcher.UIThread.RunJobs();
        await controller.Completion; Assert.That(reads, Is.EqualTo(1), "The posted callback belongs to its captured attachment generation.");
        RevisionLinksSettingsPublication.Publish(DetailedRepositorySettings.GlobalPath); Dispatcher.UIThread.RunJobs(); await controller.Completion; Assert.That(reads, Is.EqualTo(2));
        await controller.BeginClose(); RevisionLinksSettingsPublication.Publish(DetailedRepositorySettings.GlobalPath); Dispatcher.UIThread.RunJobs(); Assert.That(reads, Is.EqualTo(2));
    }

    [AvaloniaTest]
    public async Task InvalidPatternDiagnosticDoesNotReplaceTheCommitMessageOrLaunchAnUrl()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); ExternalLinkDefinition invalid = RevisionLinksSettingsSessionTests.Definition("invalid", "https://invalid.invalid"); invalid.SearchPattern = "[";
        f.Write(GitDistributedSettingsScope.Global, (GitRevisionLinkCodec.SettingName, GitRevisionLinkCodec.Encode([invalid])!));
        using CommitInformationView view = new(); int launches = 0; view.RevisionLinkLauncher = _ => { launches++; return Task.CompletedTask; };
        Window window = new() { Content = view }; window.Show();
        try
        {
            GitCommit selected = (await f.Repository!.GetLogAsync(1, all: false)).Single(); view.SetRevision(f.Repository, selected); await view.LoadAsync(); Pump(window);
            Assert.That(view.MessageControl.Text, Does.Contain(selected.Subject)); Assert.That(Related(view), Is.Empty); Assert.That(launches, Is.Zero);
            Assert.That(view.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "RevisionLinksDiagnostic").Text, Is.Not.Empty);
        }
        finally { view.BeginClose(); await view.Completion; window.Close(); }
    }

    [AvaloniaTest]
    public async Task ExplicitInternalAndFragmentPrefixedLinksFollowWindowsLinkFactoryRules()
    {
        using CommitInformationView view = new(); List<(CommitInformationCommand, string?)> commands = []; List<string> launched = [];
        view.CommandRequested += (command, target) => commands.Add((command, target)); view.RevisionLinkLauncher = uri => { launched.Add(uri.AbsoluteUri); return Task.CompletedTask; };
        await view.ActivateRevisionLinkAsync("gitext://gotocommit/012345"); await view.ActivateRevisionLinkAsync("gitext://gotobranch/origin/main"); await view.ActivateRevisionLinkAsync("gitext://gototag/v1");
        Assert.That(commands, Is.EqualTo(new[] { (CommitInformationCommand.NavigateRevision, "012345"), (CommitInformationCommand.NavigateReference, "origin/main"), (CommitInformationCommand.NavigateReference, "refs/tags/v1") }));
        Assert.That(launched, Is.Empty); await view.ActivateRevisionLinkAsync("caption #https://example.invalid/issue/7"); Assert.That(launched, Is.EqualTo(new[] { "https://example.invalid/issue/7" }));
        Assert.ThrowsAsync<ArgumentException>(async () => await view.ActivateRevisionLinkAsync("invalid relative target")); Assert.That(launched.Count, Is.EqualTo(1));
    }

    private static RevisionLinksResult Result(string uri) => new([new ExternalLink(uri, uri)], []);
    private static Button[] Related(CommitInformationView view) => view.GetVisualDescendants().OfType<Button>().Where(button => button.Name == "CommitInformationRelatedLink").ToArray();
    private static void Pump(Window window) => DetailedSettingsUiHost.Pump(window);
}

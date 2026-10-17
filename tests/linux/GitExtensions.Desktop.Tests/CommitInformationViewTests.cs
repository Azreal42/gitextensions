using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommitInformationViewTests
{
    [AvaloniaTest]
    [TestCase("Light")]
    [TestCase("Dark")]
    public async Task SharedPanelDistinguishesAuthorCommitterDirectAnnotationsAndContainingDescendants(string theme)
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.GitAsync("commit", "--amend", "--author=Other Author <other@example.invalid>", "--date=2001-02-03T04:05:06+05:30", "-m", "Literal <subject> & text", "-m", "Body <a href='x'> remains literal.");
        GitCommit selected = (await f.Repository.GetLogAsync(1, all: false)).Single();
        await f.GitAsync("tag", "-a", "direct-tag", "-m", "Direct annotation <literal>");
        await f.GitAsync("notes", "add", "-m", "Mutable note", selected.Hash);
        await f.ChangeAsync("descendant\n", "Descendant"); await f.GitAsync("tag", "-a", "descendant-tag", "-m", "Descendant annotation must not be displayed");
        await f.GitAsync("branch", "topic/descendant");
        GitCommit[] graph = (await f.Repository.GetLogAsync(10, all: false)).ToArray();
        using CommitInformationView view = new() { ShowBranchesAsLinks = true };
        Window window = new() { Content = view, Width = 850, Height = 700, RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light }; window.Show();
        try
        {
            view.SetRevision(f.Repository, selected, graph, "literal [*] file:2"); await view.LoadAsync(); Pump(window);
            Assert.That(view.Text, Does.Contain("Other Author <other@example.invalid>").And.Contain("Reference Tester <reference@example.com>"));
            Assert.That(view.Text, Does.Contain("Author date:").And.Contain("Commit date:").And.Contain(selected.Hash));
            Assert.That(view.MessageControl.Text, Does.Contain("Body <a href='x'> remains literal.").And.Contain("Mutable note"));
            Assert.That(view.MessageControl.Text, Is.EqualTo("Literal <subject> & text\n\nBody <a href='x'> remains literal.\n\nNotes:\nMutable note"));
            Assert.That(view.Text, Does.Contain("Direct annotation <literal>").And.Not.Contain("Descendant annotation must not be displayed"));
            Assert.That(view.Text, Does.Contain("Contained in branches:").And.Contain("topic/descendant").And.Contain("descendant-tag"));
            Assert.That(view.CopyText, Does.Not.Contain("Contained in").And.Not.Contain("Direct annotation").And.Not.Contain("Children:"));
            Assert.That(view.CopyText, Does.Contain("Mutable note"));
            List<(CommitInformationCommand Command, string? Target)> commands = [];
            view.CommandRequested += (command, target) => commands.Add((command, target));
            Button child = view.GetVisualDescendants().OfType<Button>().Single(button => button.Tag is ValueTuple<CommitInformationCommand, string> target && target.Item1 == CommitInformationCommand.NavigateRevision && target.Item2 == graph[0].Hash);
            child.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(commands, Is.EqualTo(new[] { (CommitInformationCommand.NavigateRevision, graph[0].Hash) }));
            string head = (await f.GitAsync("rev-parse", "HEAD")).StandardOutput.Trim();
            Assert.That(head, Is.EqualTo(graph[0].Hash), "Passive display and link dispatch do not checkout a commit.");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task ShowAllReformatsTheCompleteSnapshotWithoutGitAndResetsAfterAnOptionReload()
    {
        using Fixture f = await Fixture.CreateAsync();
        for (int index = 0; index < 14; index++) { await f.GitAsync("branch", "topic/" + index.ToString("D2", CultureInfo.InvariantCulture)); }
        GitCommit selected = (await f.Repository.GetLogAsync(1, all: false)).Single();
        using CommitInformationView view = new() { ShowBranchesAsLinks = true };
        Window window = new() { Content = view, Width = 650, Height = 650 }; window.Show();
        try
        {
            view.SetRevision(f.Repository, selected); await view.LoadAsync(); Pump(window);
            Button all = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CommitInformationShowAllbranches");
            Assert.That(BranchLinks(view).Count(), Is.EqualTo(10));
            GitCommandLog.Clear(); PushProcessTestDriver.Pointer(all); Pump(window);
            Assert.That(BranchLinks(view).Count(), Is.EqualTo(15)); Assert.That(GitCommandLog.Snapshot(), Is.Empty);
            Assert.That(view.MessageControl.IsKeyboardFocusWithin, Is.True, "Expanding the list must leave a usable text focus after removing its link.");
            Assert.That(view.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "CommitInformationShowAllbranches"), Is.False);
            MenuItem remote = view.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "showContainedInBranchesRemoteToolStripMenuItem");
            remote.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await view.Completion; Pump(window);
            Assert.That(view.Options.Remote, Is.True); Assert.That(new CommitInformationPreferencesStore().Load().Remote, Is.True);
            Assert.That(BranchLinks(view).Count(), Is.EqualTo(10));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task SlowMetadataCancelsOldSelectionWithoutMovingTheUsersTextFocusOrStartingGlobalReaders()
    {
        using Fixture f = await Fixture.CreateAsync(); GitCommit first = (await f.Repository.GetLogAsync(1, all: false)).Single();
        await f.ChangeAsync("second\n", "Second subject"); GitCommit second = (await f.Repository.GetLogAsync(1, all: false)).Single();
        using CommitInformationView view = new(); TextBox editor = new() { Name = "UserEditor", Text = "keep editing" };
        StackPanel layout = new() { Children = { editor, view } }; Window window = new() { Content = layout, Width = 800, Height = 600 }; window.Show();
        using SlowMetadata gate = new(f.Output("gate"));
        try
        {
            editor.Focus(); GitCommandLog.Clear(); view.SetRevision(f.Repository, first); Task old = view.LoadAsync();
            await Until(() => gate.Started);
            view.SetRevision(f.Repository, second); Task latest = view.LoadAsync();
            gate.Release(); try { await old; } catch (OperationCanceledException) { }
            await latest; await view.Completion; Pump(window);
            Assert.That(view.Text, Does.Contain(second.Hash).And.Contain("Second subject"));
            Assert.That(view.MessageControl.Text, Does.Not.Contain("Initial commit"));
            Assert.That(window.FocusManager!.GetFocusedElement(), Is.SameAs(editor)); Assert.That(editor.Text, Is.EqualTo("keep editing"));
            Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status").And.Not.Contain("git ls-tree"));
            Assert.That(view.IsLoading, Is.False);
        }
        finally { gate.Release(); view.Cancel(); await view.Completion; window.Close(); }
    }

    [AvaloniaTest]
    public async Task ClearingAnUncommittedOrClosedHostCancelsAndDrainsWithoutAZeroHashLookup()
    {
        using Fixture f = await Fixture.CreateAsync(); GitCommit commit = (await f.Repository.GetLogAsync(1, all: false)).Single();
        using CommitInformationView view = new(); Window window = new() { Content = view }; window.Show();
        using SlowMetadata gate = new(f.Output("gate"));
        try
        {
            view.SetRevision(f.Repository, commit); Task old = view.LoadAsync(); await Until(() => gate.Started);
            view.SetMessage("Not committed yet"); view.BeginClose(); gate.Release(); try { await old; } catch (OperationCanceledException) { }
            await view.Completion; Assert.That(view.Text, Does.Contain("Not committed yet").And.Not.Contain(commit.Hash));
            GitCommandLog.Clear(); await view.LoadAsync(); Assert.That(GitCommandLog.Snapshot(), Is.Empty);
            MenuItem remote = view.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "showContainedInBranchesRemoteToolStripMenuItem");
            remote.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.That(new CommitInformationPreferencesStore().Load().Remote, Is.False, "Closing seals option callbacks before the host captures Completion.");
            Assert.That(view.IsLoading, Is.False);
        }
        finally { gate.Release(); view.Dispose(); await view.Completion; window.Close(); }
    }

    [AvaloniaTest]
    public async Task AnotherPanelsSavedOptionsInvalidateAnInFlightSnapshotBeforeTheContextMenuUsesThem()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.GitAsync("tag", "visible-tag");
        GitCommit commit = (await f.Repository.GetLogAsync(1, all: false)).Single();
        using CommitInformationView first = new(); using CommitInformationView other = new();
        Window window = new() { Content = first, Width = 700, Height = 600 }; window.Show();
        using SlowMetadata gate = new(f.Output("gate"));
        try
        {
            first.SetRevision(f.Repository, commit); Task old = first.LoadAsync(); await Until(() => gate.Started);
            MenuItem tags = other.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "showContainedInTagsToolStripMenuItem");
            tags.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await other.Completion;
            Assert.That(other.Options.Tags, Is.False);
            Pump(window);
            Point point = first.MessageControl.TranslatePoint(new Point(8, 8), window)!.Value;
            window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
            await Until(() => first.Options.Tags == false);
            gate.Release(); try { await old; } catch (OperationCanceledException) { }
            await first.Completion; Pump(window);
            Assert.That(first.Options.Tags, Is.False);
            Assert.That(first.Text, Does.Not.Contain("Contained in tags:"));
            Assert.That(first.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "showContainedInTagsToolStripMenuItem").IsChecked, Is.False);
            first.ContextMenu.Close();
        }
        finally { gate.Release(); first.Cancel(); try { await first.Completion; } catch (OperationCanceledException) { } window.Close(); }
    }

    [Test]
    public void IndependentDefaultsRoundTripWithoutOverwritingTheOtherDesktopPreferences()
    {
        string root = Path.Combine(Path.GetTempPath(), "commit-info-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            PreferencesStore desktop = new(root); desktop.Save(new() { Theme = "Dark", HistoryLimit = 42 }); byte[] original = File.ReadAllBytes(desktop.FilePath);
            CommitInformationPreferencesStore store = new(root); Assert.That(store.Load(), Is.EqualTo(new CommitInformationOptions()));
            CommitInformationOptions changed = new(false, true, true, false, false, false); store.Save(changed);
            Assert.That(store.Load(), Is.EqualTo(changed)); Assert.That(File.ReadAllBytes(desktop.FilePath), Is.EqualTo(original));
            File.WriteAllText(store.FilePath, "{}"); Assert.That(store.Load(), Is.EqualTo(new CommitInformationOptions()));
            File.WriteAllText(store.FilePath, "broken json"); Assert.That(store.Load(), Is.EqualTo(new CommitInformationOptions()));
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    private static IEnumerable<Button> BranchLinks(Control view) => view.GetVisualDescendants().OfType<Button>().Where(button => button.Tag is ValueTuple<CommitInformationCommand, string> target && target.Item2.StartsWith("refs/heads/", StringComparison.Ordinal));
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static async Task Until(Func<bool> predicate)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!predicate()) { if (timer.Elapsed > TimeSpan.FromSeconds(30)) { Assert.Fail("Commit information did not settle."); } Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
    }
    private sealed class SlowMetadata : IDisposable
    {
        private readonly string? _oldPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string _directory;
        private string Hold => Path.Combine(_directory, "hold");
        internal bool Started => File.Exists(Path.Combine(_directory, "started"));
        internal SlowMetadata(string directory)
        {
            _directory = directory; Directory.CreateDirectory(directory); File.WriteAllText(Hold, "");
            string executable = Path.Combine(directory, "git");
            File.WriteAllText(executable, "#!/bin/sh\nfor argument do\n case \"$argument\" in show)\n touch '" + directory + "/started'\n while [ -f '" + Hold + "' ]; do sleep 0.02; done\n break;;\n esac\ndone\nexec /usr/bin/git \"$@\"\n");
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + _oldPath);
        }
        internal void Release() => File.Delete(Hold);
        public void Dispose() { Release(); Environment.SetEnvironmentVariable("PATH", _oldPath); }
    }
}

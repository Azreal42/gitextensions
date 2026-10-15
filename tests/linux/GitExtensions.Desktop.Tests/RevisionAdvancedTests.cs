using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class RevisionAdvancedTests
{
    private const string Target = "1234567890abcdef1234567890abcdef12345678";

    [TestCase(GitRebaseAction.Edit, "edit")]
    [TestCase(GitRebaseAction.Reword, "reword")]
    public void TargetedPickChangesOnlyTheSelectedInstruction(GitRebaseAction action, string command)
    {
        string before = "# café λ\r\nlabel onto\r\nreset onto\r\n  pick\t1234567 Selected subject\r\n"
            + "pick aaaaaaa Other commit\r\nexec printf '%s' '$HOME; $(literal)'\r\nupdate-ref refs/heads/other\r\n";
        string after = GitRebaseEditor.RewriteTargetedCommitTodo(before, Target, action);
        Assert.That(after, Is.EqualTo(before.Replace("  pick\t1234567", "  " + command + "\t1234567", StringComparison.Ordinal)));
    }

    [Test]
    public void RewordMergeChangesItsMessageFlagAndEditMergeStopsImmediatelyAfterIt()
    {
        string before = "reset branch-point\nmerge -C 1234567 topic # Selected merge\npick aaaaaaa Later\n";
        Assert.That(GitRebaseEditor.RewriteTargetedCommitTodo(before, Target, GitRebaseAction.Reword),
            Is.EqualTo(before.Replace("merge -C", "merge -c", StringComparison.Ordinal)));
        Assert.That(GitRebaseEditor.RewriteTargetedCommitTodo(before, Target, GitRebaseAction.Edit),
            Is.EqualTo("reset branch-point\nmerge -C 1234567 topic # Selected merge\nbreak\npick aaaaaaa Later\n"));
        string crlf = "merge -C 1234567 topic # Merge\r\nlabel after-merge\r\n";
        Assert.That(GitRebaseEditor.RewriteTargetedCommitTodo(crlf, Target, GitRebaseAction.Edit),
            Is.EqualTo("merge -C 1234567 topic # Merge\r\nbreak\r\nlabel after-merge\r\n"));
    }

    [Test]
    public void MissingAmbiguousOrInvalidTargetsFailBeforeReturningATodo()
    {
        Assert.Throws<InvalidOperationException>(() => GitRebaseEditor.RewriteTargetedCommitTodo("pick aaaaaaa Other\n", Target, GitRebaseAction.Edit));
        Assert.Throws<InvalidOperationException>(() => GitRebaseEditor.RewriteTargetedCommitTodo("pick 1234567 One\nmerge -C 1234567 topic\n", Target, GitRebaseAction.Reword));
        Assert.Throws<ArgumentException>(() => GitRebaseEditor.RewriteTargetedCommitTodo("", Target + ";touch injected", GitRebaseAction.Edit));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitRebaseEditor.RewriteTargetedCommitTodo("", Target, GitRebaseAction.Drop));
    }

    [AvaloniaTest]
    public async Task TargetedCommitDialogKeepsTheSelectedOffBranchCommitAndReplacementMessage()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await Git(repository, "checkout", "-b", "other");
        await Git(repository, "commit", "--allow-empty", "-m", "Other branch target", "-m", "Existing body λ");
        GitCommit target = (await repository.GetLogAsync(1)).Single();
        await Git(repository, "checkout", "main");
        string before = await repository.ResolveRevisionAsync("HEAD");
        foreach (TargetedCommitKind kind in new[] { TargetedCommitKind.Fixup, TargetedCommitKind.Squash, TargetedCommitKind.Amend })
        {
            CommitDialog dialog = new(repository, initialKind: kind, targetCommit: target);
            dialog.Show();
            try
            {
                await dialog.InitializeAsync();
                Assert.That(dialog.LastError, Is.Null);
                Assert.That(OptionInput<ComboBox>(dialog, "CommitKind").SelectedIndex, Is.EqualTo((int)kind));
                Assert.That((OptionInput<ComboBox>(dialog, "TargetCommit").SelectedItem as GitCommit)?.Hash, Is.EqualTo(target.Hash));
                Assert.That(Field<CheckBox>(dialog, "Amend").IsChecked, Is.False, "An autosquash amend target must never select amend HEAD.");
                if (kind == TargetedCommitKind.Amend)
                { Assert.That(Field<TextBox>(dialog, "Message").Text, Does.StartWith("Other branch target\n\nExisting body λ")); }
                await dialog.RefreshAsync();
                Assert.That((OptionInput<ComboBox>(dialog, "TargetCommit").SelectedItem as GitCommit)?.Hash, Is.EqualTo(target.Hash));
            }
            finally { dialog.Close(); }
        }
        Assert.That(await repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(before), "Opening and cancelling the workflows must not create commits.");
    }

    [AvaloniaTest]
    public async Task AmendTargetCreatesAChildOfHeadAndAutosquashesItsEditedMessageIntoTheTarget()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string target = await repository.ResolveRevisionAsync("HEAD");
        await Git(repository, "commit", "--allow-empty", "-m", "Later HEAD");
        string previousHead = await repository.ResolveRevisionAsync("HEAD");
        const string replacement = "Edited original λ\n\nReplacement body";
        await repository.CommitAsync(new GitCommitOptions(Message: replacement, AllowEmpty: true, AmendRevision: target));
        GitCommit amend = (await repository.GetLogAsync(1)).Single();
        Assert.That(amend.Parents, Is.EqualTo(new[] { previousHead }));
        Assert.That(amend.Subject, Is.EqualTo("amend! Initial commit"));
        Assert.That(amend.Body.TrimEnd(), Is.EqualTo(replacement));
        Assert.That((await repository.GetLogAsync(1, previousHead)).Single().Subject, Is.EqualTo("Later HEAD"));
        await Git(repository, "-c", "sequence.editor=true", "rebase", "--interactive", "--autosquash", "--root");
        GitCommit[] rewritten = (await repository.GetLogAsync(10, all: false)).ToArray();
        Assert.That(rewritten.Select(commit => commit.Subject), Is.EqualTo(new[] { "Later HEAD", "Edited original λ" }));
        Assert.That(rewritten[^1].Body.TrimEnd(), Is.EqualTo("Replacement body"));
    }

    [AvaloniaTest]
    public async Task ConflictingTargetedKindsAreRejectedWithoutChangingHead()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string before = await repository.ResolveRevisionAsync("HEAD");
        GitCommitOptions[] invalid =
        [
            new(Message: "Invalid", Amend: true, AmendRevision: before),
            new(Message: "Invalid", FixupRevision: before, AmendRevision: before),
            new(Message: "Invalid", SquashRevision: before, AmendRevision: before),
            new(Message: "Invalid", FixupRevision: before, SquashRevision: before)
        ];
        foreach (GitCommitOptions options in invalid)
        { Assert.ThrowsAsync<ArgumentException>(async () => await repository.CommitAsync(options)); }
        Assert.That(await repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public Task NativeEditStopsAtTheSelectedRootAndAbortRestoresMainAndDirtyFiles()
        => VerifyNativeEditAndAbortAsync(root: true, merge: false);

    [AvaloniaTest]
    public Task NativeEditStopsAtTheSelectedMiddleCommitAndAbortRestoresMainAndDirtyFiles()
        => VerifyNativeEditAndAbortAsync(root: false, merge: false);

    [AvaloniaTest]
    public Task NativeEditStopsAfterTheSelectedMergeWithBothParentsAndAbortRestoresEveryRef()
        => VerifyNativeEditAndAbortAsync(root: false, merge: true);

    private static async Task VerifyNativeEditAndAbortAsync(bool root, bool merge)
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("The private SDK editor launcher uses a POSIX script."); return; }
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n", createMerge: merge);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        if (!root && !merge)
        {
            await File.WriteAllTextAsync(Path.Combine(repository.Path, "selected-middle.txt"), "Selected middle commit λ\n");
            await Git(repository, "add", "--", "selected-middle.txt");
            await Git(repository, "commit", "-m", "Selected middle commit");
        }
        GitCommit selected = (await repository.GetLogAsync(1, "HEAD")).Single();
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "later.txt"), "Later HEAD content\n");
        await Git(repository, "add", "--", "later.txt");
        await Git(repository, "commit", "-m", "Later HEAD must not be the edit target");
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "file.txt"), "Dirty tracked worktree λ\n");
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "untracked.txt"), "Untracked bytes survive the rebase\n");
        string originalHead = await repository.ResolveRevisionAsync("HEAD");
        string originalRefs = await Text(repository, "for-each-ref", "--format=%(refname)%00%(objectname)");
        string originalStatus = await Text(repository, "status", "--porcelain=v2", "-z");
        string originalIndex = await Text(repository, "diff", "--cached", "--binary");
        string originalFiles = await WorkingFilesAsync(repository);
        string selectedTree = await Text(repository, "rev-parse", selected.Hash + "^{tree}");
        using NativeEditorLauncher editor = NativeEditorLauncher.Create();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        try
        {
            GitResult started = await repository.RebaseCommitAsync(selected.Hash, GitRebaseAction.Edit, editor.Executable, deadline.Token);
            Assert.That(started.Success, Is.True, started.StandardError);
            GitRebaseState paused = await repository.GetRebaseStateAsync();
            GitCommit stopped = (await repository.GetLogAsync(1, "HEAD")).Single();
            GitRepositoryInfo info = await repository.GetInfoAsync();
            Assert.Multiple(() =>
            {
                Assert.That(paused.IsActive, Is.True, "The real child editor must leave Git paused, rather than completing an unchanged rebase.");
                Assert.That(paused.Backend, Is.EqualTo(GitRebaseBackend.Merge));
                Assert.That(paused.HeadName, Is.EqualTo("refs/heads/main"));
                Assert.That(paused.OriginalHead, Is.EqualTo(originalHead));
                Assert.That(paused.UsesLegacyPlan, Is.False);
                Assert.That(paused.Options?.Interactive, Is.True);
                Assert.That(paused.Options?.AutoStash, Is.True);
                Assert.That(paused.TodoEncodingName, Is.EqualTo("utf-8"));
                Assert.That(paused.EncodingError, Is.Null);
                Assert.That(paused.InstructionError, Is.Null);
                Assert.That(paused.JournalError, Is.Null);
                Assert.That(stopped.Subject, Is.EqualTo(selected.Subject));
                Assert.That(stopped.Parents.Count, Is.EqualTo(merge ? 2 : root ? 0 : 1));
                Assert.That(File.Exists(Path.Combine(info.GitDirectory, "gitextensions-rebase-native", "state.json")), Is.True);
                Assert.That(File.Exists(Path.Combine(repository.Path, "later.txt")), Is.False, "Git must stop before the later HEAD commit.");
            });
            Assert.That(await Text(repository, "rev-parse", "HEAD^{tree}"), Is.EqualTo(selectedTree));
            Assert.That(await repository.ResolveRevisionAsync("refs/heads/main"), Is.EqualTo(originalHead));
            Assert.That((await repository.GetStatusAsync()).IsDetached, Is.True);
            if (merge)
            {
                Assert.That(paused.Steps.Any(step => step.Action is "merge" or "m" && step.CommitHash == selected.Hash), Is.True);
                Assert.That(paused.Steps.Any(step => step.Action is "break" or "b" && step.Status == GitRebaseStepStatus.Applied), Is.True);
            }
            else
            { Assert.That(paused.CurrentCommit, Is.EqualTo(selected.Hash), "The edit instruction must target the requested original commit."); }
            GitResult aborted = await repository.AbortRebaseAsync();
            Assert.That(aborted.Success, Is.True, aborted.StandardError);
            Assert.That((await repository.GetRebaseStateAsync()).IsActive, Is.False);
            Assert.That((await repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
            Assert.That(await repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(originalHead));
            Assert.That(await Text(repository, "for-each-ref", "--format=%(refname)%00%(objectname)"), Is.EqualTo(originalRefs));
            Assert.That(await Text(repository, "status", "--porcelain=v2", "-z"), Is.EqualTo(originalStatus));
            Assert.That(await Text(repository, "diff", "--cached", "--binary"), Is.EqualTo(originalIndex));
            Assert.That(await WorkingFilesAsync(repository), Is.EqualTo(originalFiles));
            Assert.That(Directory.Exists(Path.Combine(info.GitDirectory, "gitextensions-rebase-native")), Is.False);
        }
        finally
        {
            if ((await repository.GetRebaseStateAsync()).IsActive) { await repository.AbortRebaseAsync(); }
        }
    }

    private static async Task<string> WorkingFilesAsync(GitRepository repository)
    {
        string paths = await Text(repository, "ls-files", "--cached", "--others", "--exclude-standard", "-z");
        List<string> files = [];
        foreach (string path in paths.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        { files.Add(path + "\0" + Convert.ToHexString(await File.ReadAllBytesAsync(Path.Combine(repository.Path, path)))); }
        return string.Join('\n', files);
    }

    private sealed class NativeEditorLauncher : IDisposable
    {
        private readonly string _directory;
        private NativeEditorLauncher(string directory) { _directory = directory; }
        public string Executable => Path.Combine(_directory, "editor");

        public static NativeEditorLauncher Create()
        {
            if (OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
            string application = typeof(MainWindow).Assembly.Location;
            Assert.That(File.Exists(application), Is.True);
            Assert.That(File.Exists(Path.ChangeExtension(application, "runtimeconfig.json")), Is.True,
                "Use the current test output's executable assembly and runtime configuration, never an installed payload.");
            DirectoryInfo? checkout = new(AppContext.BaseDirectory);
            while (checkout is not null && !File.Exists(Path.Combine(checkout.FullName, ".dotnet", "dotnet"))) { checkout = checkout.Parent; }
            string? host = checkout is null ? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") : Path.Combine(checkout.FullName, ".dotnet", "dotnet");
            Assert.That(host is not null && Path.IsPathFullyQualified(host) && File.Exists(host) && Path.GetFileName(host) == "dotnet", Is.True,
                "A verified existing SDK host is required for the native child editor.");
            string directory = Path.Combine("/tmp", "gitextensions-targeted-editor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            NativeEditorLauncher launcher = new(directory);
            try
            {
                static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
                File.WriteAllText(launcher.Executable, "#!/bin/sh\nexec " + Quote(host!) + " " + Quote(application) + " \"$@\"\n", new System.Text.UTF8Encoding(false));
                File.SetUnixFileMode(launcher.Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return launcher;
            }
            catch { launcher.Dispose(); throw; }
        }

        public void Dispose() { if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); } }
    }

    private static T Field<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static T OptionInput<T>(CommitDialog dialog, string name) where T : Control
        => Field<Menu>(dialog, "CommitToolbar").Items.OfType<MenuItem>().Single(item => item.Name == "Options")
            .Items.OfType<MenuItem>().Select(item => item.Header).OfType<StackPanel>()
            .SelectMany(panel => panel.Children).OfType<T>().Single(control => control.Name == name);

    private static async Task Git(GitRepository repository, params string[] arguments)
    {
        GitResult result = await repository.RunAsync(arguments);
        Assert.That(result.Success, Is.True, result.StandardError);
    }

    private static async Task<string> Text(GitRepository repository, params string[] arguments)
    {
        GitResult result = await repository.RunAsync(arguments);
        Assert.That(result.Success, Is.True, result.StandardError);
        return result.StandardOutput;
    }
}

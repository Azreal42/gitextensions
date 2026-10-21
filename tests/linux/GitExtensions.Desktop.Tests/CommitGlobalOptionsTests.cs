using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
[SupportedOSPlatform("linux")]
public sealed class CommitGlobalOptionsTests
{
    [AvaloniaTest]
    [TestCase(nameof(CommitDialogSettingsValues.ShowErrorsWhenStagingFiles))]
    [TestCase(nameof(CommitDialogSettingsValues.UseFormCommitMessage))]
    public void NewFieldsUseExistingFieldIntentionsAndPreserveAPeerAndUnknownValues(string name)
    {
        using PortableHelperSettingsFixture f = new(); CommitPreferencesStore store = new();
        store.Save(new() { LastCommitMessage = "preserved Ω" });
        JsonObject raw = JsonNode.Parse(File.ReadAllBytes(store.FilePath))!.AsObject();
        raw["future"] = JsonNode.Parse("{\"opaque\":[17,null,\"Ω\"]}"); File.WriteAllText(store.FilePath, raw.ToJsonString());
        CommitDialogSettingsSession first = new(), peer = new();
        string other = name == nameof(CommitDialogSettingsValues.ShowErrorsWhenStagingFiles)
            ? nameof(CommitDialogSettingsValues.UseFormCommitMessage) : nameof(CommitDialogSettingsValues.ShowErrorsWhenStagingFiles);
        first.Save(first.Initial.With(other, false)); peer.Save(peer.Initial.With(name, false));
        CommitDialogSettingsValues saved = new CommitDialogSettingsSession().Initial;
        Assert.That(saved.Get(name), Is.False); Assert.That(saved.Get(other), Is.False);
        Assert.That(store.Load().LastCommitMessage, Is.EqualTo("preserved Ω"));
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllBytes(store.FilePath))!["future"], raw["future"]), Is.True);
        byte[] before = File.ReadAllBytes(store.FilePath); peer.Save(peer.Initial);
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(true)] [TestCase(false)]
    public async Task MessageModeIsCapturedAtOpeningButTheNextWindowUsesTheActuallyAppliedValue(bool initial)
    {
        using GitFixture git = await GitFixture.CreateAsync(); using PortableHelperSettingsFixture f = new();
        CommitPreferencesStore store = Preferences(initial); CommitDialog old = await Open(git, store);
        CommitDialog? next = null;
        try
        {
            TextBox message = Field<TextBox>(old, "Message"); Assert.That(message.IsEnabled, Is.EqualTo(initial));
            (DesktopSettingsDialog settings, Task<bool> closed) = f.Open("Commit");
            AdvancedSettingsUiDriver.Set(settings, "chkWriteCommitMessageInCommitWindow", !initial);
            await AdvancedSettingsUiDriver.Apply(f, settings, 1);
            PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await closed;
            Assert.That(store.Load().UseFormCommitMessage, Is.EqualTo(!initial));
            Assert.That(Field<TextBox>(old, "Message"), Is.SameAs(message)); Assert.That(message.IsEnabled, Is.EqualTo(initial));
            await Close(old); next = await Open(git, store);
            Assert.That(Field<TextBox>(next, "Message").IsEnabled, Is.EqualTo(!initial));
            if (initial)
            {
                Assert.That(Private<ComboBox>(next, "_kind").IsEnabled, Is.False);
                Assert.That(Private<MenuItem>(next, "_historyMenu").IsEnabled, Is.False);
                Assert.That(Private<MenuItem>(next, "_templatesMenu").IsEnabled, Is.False);
                Assert.That(Field<Button>(next, "Commit").IsEnabled, Is.True, "Git, rather than the empty disabled form, supplies the message.");
            }
            Assert.That(old.HasCommitted || next.HasCommitted, Is.False);
        }
        finally { await Close(old); if (next is not null) { await Close(next); } }
    }

    [AvaloniaTest]
    [TestCase(true)] [TestCase(false)]
    public async Task ActualSafecrlfFailureIsReportedOrDisabledForOnlyThisNativeStage(bool showErrors)
    {
        using GitFixture git = await GitFixture.CreateAsync(); using PortableHelperSettingsFixture f = new();
        git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
        await git.GitAsync("config", "core.autocrlf", "true"); await git.GitAsync("config", "core.safecrlf", "true");
        const string name = "literal [line] Ω.txt"; string path = git.PathInRepository(name);
        await File.WriteAllTextAsync(path, "line one\nline two\n"); string head = await git.Repository.ResolveRevisionAsync("HEAD");
        CommitPreferencesStore store = Preferences(true); store.Save(store.Load() with { ShowErrorsWhenStagingFiles = showErrors });
        CommitDialog dialog = await Open(git, store);
        try
        {
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "StageAll"));
            if (showErrors)
            {
                CommitStagingReportDialog report = await Report(dialog);
                string output = Field<TextBox>(report, "StageDetailsOutput").Text!;
                Assert.That(output, Does.Contain("CRLF").And.Contain("literal [line]"));
                Assert.That(Field<TextBox>(report, "StageDetailsOutput").IsReadOnly, Is.True);
                PushProcessTestDriver.Pointer(Field<Button>(report, "StageDetailsClose"));
            }
            await Idle(dialog);
            Assert.That(dialog.LastStagingResult, Is.Not.Null); Assert.That(dialog.LastStagingResult!.Success, Is.EqualTo(!showErrors));
            Assert.That((await git.GitAsync("diff", "--cached", "--name-only")).StandardOutput.Length > 0, Is.EqualTo(!showErrors));
            Assert.That((await git.GitAsync("config", "--get", "core.safecrlf")).StandardOutput.Trim(), Is.EqualTo("true"));
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("line one\nline two\n"));
            Assert.That(await git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
            Assert.That(dialog.HasCommitted, Is.False); Assert.That(dialog.HasRepositoryChanges, Is.True);
            Assert.That(dialog.OwnedWindows.OfType<CommitStagingReportDialog>(), Is.Empty);
        }
        finally { await Close(dialog); }
    }

    [AvaloniaTest]
    public async Task TheStageGestureReadsTheLatestPersistedPolicyEvenWhenCommitWasAlreadyOpen()
    {
        using GitFixture git = await GitFixture.CreateAsync(); using PortableHelperSettingsFixture f = new();
        await git.GitAsync("config", "core.autocrlf", "true"); await git.GitAsync("config", "core.safecrlf", "true");
        await File.WriteAllTextAsync(git.PathInRepository("late LF.txt"), "native LF\n");
        CommitPreferencesStore store = Preferences(true); CommitDialog dialog = await Open(git, store);
        try
        {
            (DesktopSettingsDialog settings, Task<bool> closed) = f.Open("Commit");
            AdvancedSettingsUiDriver.Set(settings, "chkShowErrorsWhenStagingFiles", false); await AdvancedSettingsUiDriver.Apply(f, settings, 1);
            PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await closed;
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "StageAll")); await Idle(dialog);
            Assert.That(dialog.LastStagingResult!.Success, Is.True);
            Assert.That((await git.GitAsync("diff", "--cached", "--name-only")).StandardOutput, Does.Contain("late LF.txt"));
            Assert.That(dialog.OwnedWindows, Is.Empty); Assert.That(store.Load().ShowErrorsWhenStagingFiles, Is.False);
        }
        finally { await Close(dialog); }
    }

    [AvaloniaTest]
    [TestCase(true)] [TestCase(false)]
    public async Task ARealIndexLockRetainsTheFailureAndRefreshesWithoutAnImplicitRetry(bool showErrors)
    {
        using GitFixture git = await GitFixture.CreateAsync(); using PortableHelperSettingsFixture f = new();
        git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0"); await File.WriteAllTextAsync(git.File, "locked change\n");
        CommitPreferencesStore store = Preferences(true); store.Save(store.Load() with { ShowErrorsWhenStagingFiles = showErrors });
        CommitDialog dialog = await Open(git, store); string lockPath = git.PathInRepository(".git/index.lock");
        byte[] index = await File.ReadAllBytesAsync(git.PathInRepository(".git/index"));
        try
        {
            await File.WriteAllTextAsync(lockPath, "owned fixture lock\n"); PushProcessTestDriver.Pointer(Field<Button>(dialog, "StageAll"));
            if (showErrors)
            {
                CommitStagingReportDialog report = await Report(dialog);
                Assert.That(Field<TextBox>(report, "StageDetailsOutput").Text, Does.Contain("index.lock"));
                PushProcessTestDriver.Pointer(Field<Button>(report, "StageDetailsClose"));
            }
            await Idle(dialog);
            Assert.That(dialog.LastStagingResult!.Success, Is.False); Assert.That(dialog.LastError, Does.Contain("index.lock"));
            Assert.That(await File.ReadAllBytesAsync(git.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(await File.ReadAllTextAsync(lockPath), Is.EqualTo("owned fixture lock\n"));
            File.Delete(lockPath); await dialog.RefreshAsync(); await Idle(dialog);
            Assert.That((await git.GitAsync("diff", "--cached")).StandardOutput, Is.Empty, "Refresh must not replay the failed stage.");
            Assert.That(await File.ReadAllTextAsync(git.File), Is.EqualTo("locked change\n")); Assert.That(dialog.HasCommitted, Is.False);
        }
        finally { File.Delete(lockPath); await Close(dialog); }
    }

    [AvaloniaTest]
    public async Task AnUnreadablePolicyAfterOpeningNeverDefaultsAnOptOutBackOnOrRewritesTheProfile()
    {
        using GitFixture git = await GitFixture.CreateAsync(); using PortableHelperSettingsFixture f = new();
        await File.WriteAllTextAsync(git.File, "not staged\n"); CommitPreferencesStore store = Preferences(true);
        store.Save(store.Load() with { ShowErrorsWhenStagingFiles = false }); CommitDialog dialog = await Open(git, store);
        try
        {
            File.WriteAllText(store.FilePath, "{ corrupt policy Ω"); byte[] broken = File.ReadAllBytes(store.FilePath);
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "StageAll")); await Idle(dialog);
            Assert.That(dialog.LastStagingResult, Is.Null); Assert.That(dialog.LastError, Is.Not.Null);
            Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(broken)); Assert.That(dialog.OwnedWindows, Is.Empty);
            Assert.That((await git.GitAsync("diff", "--cached")).StandardOutput, Is.Empty); Assert.That(dialog.HasCommitted, Is.False);
        }
        finally { await Close(dialog); }
    }

    [AvaloniaTest]
    public async Task TheRealGitEditorCommitsAndTheMutationLeaseSurvivesUntilItsAuthenticatedHelperCloses()
    {
        using GitFixture git = await GitFixture.CreateAsync(); using PortableHelperSettingsFixture f = new();
        string editor = await Script(git, "editor with '; touch INJECTED #.sh", "printf 'External editor Ω\\n\\nreal body\\n' > \"$1\"\n");
        git.SetEnvironment("GIT_EDITOR", Quote(editor)); await File.WriteAllTextAsync(git.File, "external commit payload\n"); await git.Repository.StageAllAsync();
        string head = await git.Repository.ResolveRevisionAsync("HEAD"); GitCommit target = (await git.Repository.GetLogAsync(1)).Single();
        CommitPreferencesStore store = Preferences(false); store.Save(store.Load() with { LastCommitMessage = "unused previous message" });
        await File.WriteAllBytesAsync(git.PathInRepository(".git/COMMITMESSAGE"), [0xff, 0xfe, 0x00]);
        await using PtyTerminal terminal = new(); CommitDialog dialog = await Open(git, store, terminal, TargetedCommitKind.Squash, target);
        try
        {
            Assert.That(Field<TextBox>(dialog, "Message").IsEnabled, Is.False);
            Assert.That(Private<ComboBox>(dialog, "_kind").SelectedIndex, Is.Zero); Assert.That(Private<ComboBox>(dialog, "_kind").IsEnabled, Is.False);
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "Commit")); await terminal.WaitFor("Press Enter to close.");
            string committed = await git.Repository.ResolveRevisionAsync("HEAD"); Assert.That(committed, Is.Not.EqualTo(head));
            await File.WriteAllTextAsync(git.PathInRepository("after.txt"), "queued only after commit\n");
            Task stage = git.Repository.StageAsync(["after.txt"]);
            Assert.That(await Task.WhenAny(stage, Task.Delay(120)), Is.Not.SameAs(stage), "The live authenticated console still owns the Core mutation lease.");
            await terminal.Send("\n"); await Idle(dialog); await stage.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(dialog.HasCommitted, Is.True); Assert.That(dialog.PushCompleted, Is.True); Assert.That(dialog.LastError, Is.Null);
            Assert.That((await git.GitAsync("show", "-s", "--format=%s", "HEAD")).StandardOutput.Trim(), Is.EqualTo("External editor Ω"));
            Assert.That((await git.GitAsync("show", "-s", "--format=%P", "HEAD")).StandardOutput.Trim(), Is.EqualTo(head));
            Assert.That((await git.GitAsync("show", "HEAD:file.txt")).StandardOutput, Is.EqualTo("external commit payload\n"));
            Assert.That(store.Load().LastCommitMessage, Is.EqualTo("unused previous message"));
            Assert.That(File.Exists(git.PathInRepository(".git/COMMITMESSAGE")), Is.False, "A successful commit clears the unused UI draft without reading it as the native message.");
            Assert.That(terminal.Process!.HasExited, Is.True); Assert.That(terminal.Process.ExitCode, Is.Zero);
            Assert.That(File.Exists(git.PathInRepository("INJECTED")), Is.False);
        }
        finally { await Close(dialog); }
    }

    [AvaloniaTest]
    public async Task ARealEditorFailureKeepsTheStagedFilesAndNeverPretendsThatTheCommitOrPushSucceeded()
    {
        using GitFixture git = await GitFixture.CreateAsync(); using PortableHelperSettingsFixture f = new();
        git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
        string editor = await Script(git, "reject-editor.sh", "printf 'editor rejected witness\\n'\nexit 23\n");
        git.SetEnvironment("GIT_EDITOR", Quote(editor)); await File.WriteAllTextAsync(git.File, "editor rejection payload\n"); await git.Repository.StageAllAsync();
        string head = await git.Repository.ResolveRevisionAsync("HEAD");
        GitRunner runner = new();
        GitBinaryResult staged = await runner.RunBinaryAsync(git.Repository.Path, ["ls-files", "--stage", "-z"]);
        Assert.That(staged.Success, Is.True, staged.StandardError);
        CommitPreferencesStore store = Preferences(false); store.Save(store.Load() with { CloseAfterEachCommit = true });
        await using PtyTerminal terminal = new(); CommitDialog dialog = await Open(git, store, terminal);
        try
        {
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "CommitAndPush")); await terminal.WaitFor("Press Enter to close.");
            await terminal.Send("\n"); await Idle(dialog);
            Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.HasCommitted || dialog.PushCompleted, Is.False);
            Assert.That(dialog.LastError, Does.Contain("Git may have changed the repository"));
            Assert.That(await git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head)); Assert.That(dialog.HasRepositoryChanges, Is.True);
            // Native commit may refresh the TREE cache before the editor fails; the staged entries must remain exact.
            GitBinaryResult after = await runner.RunBinaryAsync(git.Repository.Path, ["ls-files", "--stage", "-z"]);
            Assert.That(after.Success, Is.True, after.StandardError);
            Assert.That(after.StandardOutput, Is.EqualTo(staged.StandardOutput));
            Assert.That((await git.GitAsync("diff", "--cached")).StandardOutput, Does.Contain("editor rejection payload"));
            Assert.That(dialog.OwnedWindows.OfType<PushDialog>(), Is.Empty); Assert.That(terminal.Process!.HasExited, Is.True);
        }
        finally { await Close(dialog); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task OwnerCloseDrainsTheRealEditorOrPostCommitHookAndPreservesAnyCommitAlreadyWritten(bool postCommit)
    {
        using GitFixture git = await GitFixture.CreateAsync(); using PortableHelperSettingsFixture f = new();
        git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0"); string witness = git.Output("native-blocked");
        string block = "printf '%s\\n' \"$$\" > " + Quote(witness) + "\nwhile :; do sleep 0.05; done\n";
        string editor = await Script(git, "commit-editor.sh", postCommit ? "printf 'Actually written before cancel\\n' > \"$1\"\n" : block);
        git.SetEnvironment("GIT_EDITOR", Quote(editor));
        if (postCommit)
        {
            string hooks = git.Output("native-hooks"); Directory.CreateDirectory(hooks);
            await File.WriteAllTextAsync(Path.Combine(hooks, "post-commit"), "#!/bin/sh\n" + block); Executable(Path.Combine(hooks, "post-commit"));
            await git.GitAsync("config", "core.hooksPath", hooks);
        }
        await File.WriteAllTextAsync(git.File, "cancel payload\n"); await git.Repository.StageAllAsync(); string head = await git.Repository.ResolveRevisionAsync("HEAD");
        CommitPreferencesStore store = Preferences(false); await using PtyTerminal terminal = new(); CommitDialog dialog = await Open(git, store, terminal);
        try
        {
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "CommitAndPush"));
            await PushProcessTestDriver.UntilAsync(dialog, () => File.Exists(witness));
            int pid = int.Parse(await File.ReadAllTextAsync(witness), System.Globalization.CultureInfo.InvariantCulture); long ticks = Ticks(pid)!.Value;
            dialog.Close(); Assert.That(dialog.IsVisible, Is.True, "Owner close waits for the authenticated helper and native group drain.");
            await PushProcessTestDriver.UntilAsync(dialog, () => !dialog.IsVisible && !dialog.IsBusy); await terminal.WaitExited();
            Assert.That(Ticks(pid), Is.Not.EqualTo(ticks)); Assert.That(dialog.HasCommitted || dialog.PushCompleted, Is.False);
            Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(dialog.OwnedWindows.OfType<PushDialog>(), Is.Empty);
            string after = await git.Repository.ResolveRevisionAsync("HEAD"); Assert.That(after == head, Is.EqualTo(!postCommit));
            if (postCommit)
            {
                Assert.That((await git.GitAsync("show", "-s", "--format=%s", "HEAD")).StandardOutput.Trim(), Is.EqualTo("Actually written before cancel"));
                Assert.That((await git.GitAsync("show", "HEAD:file.txt")).StandardOutput, Is.EqualTo("cancel payload\n"));
                Assert.That((await git.GitAsync("diff", "--cached")).StandardOutput, Is.Empty);
            }
            else { Assert.That((await git.GitAsync("diff", "--cached")).StandardOutput, Does.Contain("cancel payload")); }
        }
        finally { await Close(dialog); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task TheNativeEditorSharesTheRealCommitFlagsWithoutPassingAFormMessageOrNonHeadKind(bool amend)
    {
        using GitFixture git = await GitFixture.CreateAsync();
        string editor = await Script(git, "flags-editor.sh", """
            { printf 'Native flags message\n\n'; cat "$1"; } > "$1.next"
            mv -- "$1.next" "$1"
            """ + "\n");
        git.SetEnvironment("GIT_EDITOR", Quote(editor));
        string hooks = git.Output("rejecting-hooks"); Directory.CreateDirectory(hooks);
        string reject = Path.Combine(hooks, "pre-commit"); await File.WriteAllTextAsync(reject, "#!/bin/sh\nexit 19\n"); Executable(reject);
        await git.GitAsync("config", "core.hooksPath", hooks);
        await File.WriteAllTextAsync(git.File, "native flags payload\n"); await git.Repository.StageAllAsync();
        string before = await git.Repository.ResolveRevisionAsync("HEAD");
        await using PtyTerminal terminal = new(); Window owner = new(); owner.Show();
        Task<GitResult>? operation = null; IReadOnlyList<string>? seen = null;
        try
        {
            operation = git.Repository.CommitWithGitEditorAsync(new("FORM MESSAGE MUST NOT BE USED", Amend: amend, SignOff: true, Sign: false,
                FixupRevision: "not-a-real-target", SquashRevision: "also-ignored", TemplatePath: git.Output("missing-template"),
                Author: " External Author <external@example.invalid> ", NoVerify: true, AmendRevision: "ignored-too"),
                (arguments, token) =>
                {
                    seen = arguments.ToArray(); GitExecutionEnvironment environment = git.Repository.ExecutionEnvironment;
                    return InteractiveFileConsoleLauncher.RunWithTerminalAsync(owner, git.Repository.Path, environment.Executable,
                        arguments, token, terminal.Start, HelperExecutable(), executionEnvironment: environment);
                });
            await terminal.WaitFor("Press Enter to close."); await terminal.Send("\n");
            GitResult result = await operation.WaitAsync(TimeSpan.FromSeconds(20)); Assert.That(result.Success, Is.True);
            Assert.That(seen, Is.Not.Null); Assert.That(seen, Does.Contain("--no-verify").And.Contain("--signoff").And.Contain("--no-gpg-sign"));
            Assert.That(seen!.Contains("--amend"), Is.EqualTo(amend));
            Assert.That(seen, Does.Contain("--author=External Author <external@example.invalid>"));
            Assert.That(seen!.Any(value => value.StartsWith("--file", StringComparison.Ordinal) || value == "-F"
                || value.StartsWith("--fixup", StringComparison.Ordinal) || value.StartsWith("--squash", StringComparison.Ordinal)), Is.False);
            Assert.That(seen!.Any(value => value.Contains("FORM MESSAGE", StringComparison.Ordinal) || value.Contains("not-a-real-target", StringComparison.Ordinal)), Is.False);
            Assert.That((await git.GitAsync("show", "-s", "--format=%s%n%an%n%ae%n%B", "HEAD")).StandardOutput,
                Does.Contain("Native flags message").And.Contain("External Author").And.Contain("external@example.invalid").And.Contain("Signed-off-by:"));
            Assert.That((await git.GitAsync("rev-list", "--count", "HEAD")).StandardOutput.Trim(), Is.EqualTo(amend ? "1" : "2"));
            Assert.That(await git.Repository.ResolveRevisionAsync("HEAD"), Is.Not.EqualTo(before));
            Assert.That((await git.GitAsync("show", "HEAD:file.txt")).StandardOutput, Is.EqualTo("native flags payload\n"));
            Assert.That(terminal.Process!.HasExited, Is.True);
        }
        finally
        {
            owner.Close();
            if (operation is not null && !operation.IsCompleted)
            { try { await operation.WaitAsync(TimeSpan.FromSeconds(20)); } catch (OperationCanceledException) { } }
        }
    }

    private static T Private<T>(CommitDialog dialog, string name)
        => (T)typeof(CommitDialog).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog)!;

    private static CommitPreferencesStore Preferences(bool useForm)
    {
        CommitPreferencesStore store = new(); store.Save(new() { UseFormCommitMessage = useForm, CloseAfterEachCommit = false,
            CloseWhenAllChangesCommitted = false, SelectStagedOnMessageFocus = false, DontConfirmAmend = true }); return store;
    }
    private static async Task<CommitDialog> Open(GitFixture git, CommitPreferencesStore store, PtyTerminal? terminal = null,
        TargetedCommitKind kind = TargetedCommitKind.Normal, GitCommit? target = null)
    {
        CommitDialog dialog = new(git.Repository, store, null, kind, target, null, terminal is null ? null : terminal.Start, terminal is null ? null : HelperExecutable());
        dialog.Show(); await dialog.InitializeAsync(); await Idle(dialog); return dialog;
    }
    private static Task Idle(CommitDialog dialog) => PushProcessTestDriver.UntilAsync(dialog, () => !dialog.IsBusy);
    private static async Task<CommitStagingReportDialog> Report(CommitDialog dialog)
    { await PushProcessTestDriver.UntilAsync(dialog, () => dialog.OwnedWindows.OfType<CommitStagingReportDialog>().Any()); return dialog.OwnedWindows.OfType<CommitStagingReportDialog>().Single(); }
    private static async Task Close(CommitDialog dialog)
    { if (dialog.IsVisible) { dialog.Close(); } await PushProcessTestDriver.UntilAsync(dialog, () => !dialog.IsVisible && !dialog.IsBusy); }
    private static T Field<T>(Avalonia.Controls.TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static async Task<string> Script(GitFixture git, string name, string body)
    { string path = git.Output(name); await File.WriteAllTextAsync(path, "#!/bin/sh\n" + body, new UTF8Encoding(false)); Executable(path); return path; }
    private static void Executable(string path) => File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    private static long? Ticks(int pid)
    { try { return long.Parse(File.ReadAllText($"/proc/{pid}/stat").Split(')').Last().Split(' ', StringSplitOptions.RemoveEmptyEntries)[19], System.Globalization.CultureInfo.InvariantCulture); } catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; } }
    private static string HelperExecutable()
    {
        string direct = Path.Combine(AppContext.BaseDirectory, "GitExtensions"); if (File.Exists(direct)) { return direct; }
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src/linux/GitExtensions.Desktop"))) { directory = directory.Parent; }
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string source = Path.Combine(directory?.FullName ?? "", "src/linux/GitExtensions.Desktop/bin", configuration, "net10.0/GitExtensions");
        Assert.That(File.Exists(source), Is.True, "Build the exact Desktop apphost before its real interactive console tests."); return source;
    }
    private sealed class PtyTerminal : IAsyncDisposable
    {
        private readonly StringBuilder _output = new(); private Task _read = Task.CompletedTask;
        internal Process? Process { get; private set; }
        internal string Output { get { lock (_output) { return _output.ToString(); } } }
        internal Process Start(string executable, IReadOnlyList<string> args)
        {
            ProcessStartInfo info = new("/usr/bin/script") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string arg in new[] { "--quiet", "--return", "--flush", "--command", string.Join(" ", new[] { executable }.Concat(args).Select(Quote)), "/dev/null" }) { info.ArgumentList.Add(arg); }
            info.Environment["TERM"] = "xterm-256color"; info.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
            Process = System.Diagnostics.Process.Start(info)!; _read = Task.WhenAll(Read(Process.StandardOutput), Read(Process.StandardError));
            Process observation = System.Diagnostics.Process.GetProcessById(Process.Id); _ = observation.SafeHandle; return observation;
        }
        private async Task Read(StreamReader reader)
        { char[] buffer = new char[1024]; int count; while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0) { lock (_output) { _output.Append(buffer, 0, count); } } }
        internal async Task WaitFor(string text)
        {
            Stopwatch elapsed = Stopwatch.StartNew(); while (!Output.Contains(text, StringComparison.Ordinal) && elapsed.Elapsed < TimeSpan.FromSeconds(20))
            { if (Process?.HasExited == true) { break; } await Task.Delay(10); }
            Assert.That(Output, Does.Contain(text));
        }
        internal async Task Send(string value) { await Process!.StandardInput.WriteAsync(value); await Process.StandardInput.FlushAsync(); }
        internal async Task WaitExited() { if (Process is not null) { await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); await _read; } }
        public async ValueTask DisposeAsync()
        {
            if (Process is not null) { if (!Process.HasExited) { Process.StandardInput.Close(); } await WaitExited(); TestContext.Out.WriteLine(Output); Process.Dispose(); }
        }
    }
}

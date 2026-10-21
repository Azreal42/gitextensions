using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using GitCommands.Git.Gpg;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GpgStatusClassificationTests
{
    // The existing Windows controller oracles, executed against its shared pure classifier.
    [TestCase("G", CommitStatus.GoodSignature)]
    [TestCase("B", CommitStatus.SignatureError)] [TestCase("U", CommitStatus.SignatureError)]
    [TestCase("X", CommitStatus.SignatureError)] [TestCase("Y", CommitStatus.SignatureError)]
    [TestCase("R", CommitStatus.SignatureError)] [TestCase("E", CommitStatus.MissingPublicKey)]
    [TestCase("N", CommitStatus.NoSignature)] [TestCase("?", CommitStatus.NoSignature)]
    public void OriginalCommitClassificationsRemainShared(string value, CommitStatus expected)
        => Assert.That(GpgSignatureStatusParser.Commit(value), Is.EqualTo(expected));

    [TestCase(0, null, TagStatus.NoTag)] [TestCase(2, null, TagStatus.Many)]
    [TestCase(1, "GOODSIG ... VALIDSIG ...", TagStatus.OneGood)]
    [TestCase(1, "error: no signature found", TagStatus.TagNotSigned)]
    [TestCase(1, "[GNUPG:] NO_PUBKEY ABCD", TagStatus.NoPubKey)]
    [TestCase(1, "[GNUPG:] BADSIG ABCD", TagStatus.OneBad)]
    [TestCase(1, "error: verifier is unavailable", TagStatus.OneBad)]
    [TestCase(1, "NO_PUBKEY ... GOODSIG ... VALIDSIG ...", TagStatus.OneGood)]
    public void OriginalTagClassificationAndItsPrecedenceRemainShared(int count, string? raw, TagStatus expected)
        => Assert.That(GpgSignatureStatusParser.Tag(count, raw), Is.EqualTo(expected));
}

[NonParallelizable]
[SupportedOSPlatform("linux")]
public sealed class GpgVerificationTests
{
    [Test]
    public async Task RealRefsIncludePeeledAnnotatedTagsAndUnsignedTagsKeepTheNativeErrorWhileCommitOnlyStillWorks()
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        await using ControlledVerifier transport = new(profile);
        string old = await Head(profile); await Tag(profile, "other-commit", old);
        await profile.Git.ChangeAsync("new commit\n", "Second commit"); string selected = await Head(profile);
        GitGpgVerification empty = await profile.Repository!.ReadGpgVerificationAsync(selected);
        Assert.That(empty.Information.CommitStatus, Is.EqualTo(CommitStatus.NoSignature));
        Assert.That(empty.Information.TagStatus, Is.EqualTo(TagStatus.NoTag)); Assert.That(empty.Tags, Is.Empty);
        Assert.That(GitGpgPresentation.Format(empty, value => value), Is.EqualTo(empty.CommitDisplayText));
        Assert.That(empty.CommitDisplayText, Does.Contain("Signature status: N").And.Contain("Signer:").And.Contain("Fingerprint:"));
        await profile.Git.GitAsync("-c", "tag.gpgSign=false", "tag", "lightweight", selected);
        string direct = await Tag(profile, "direct", selected);
        // No real GPG/key/agent may be started by this unsigned fixture. Git rejects the absent signature itself.
        await profile.Git.GitAsync("config", "gpg.program", profile.Git.Output("must-not-launch-gpg"));
        GitGpgVerification read = await profile.Repository.ReadGpgVerificationAsync(selected);
        Assert.That(read.Tags.Select(tag => tag.Name), Is.EqualTo(new[] { "direct" }));
        Assert.That(read.Tags.Single().ObjectId, Is.EqualTo(direct));
        Assert.That(read.Information.TagStatus, Is.EqualTo(TagStatus.TagNotSigned));
        Assert.That(read.Tags.Single().RawExitCode, Is.Not.Zero);
        Assert.That(read.Information.TagVerificationMessage, Does.Contain("no signature found"));
        Assert.That(GitGpgPresentation.Format(read, value => value), Does.Contain("Tag is not signed").And.Not.Contain("other-commit").And.Not.Contain("nested"));
        Assert.That(transport.Calls.Select(call => call.ObjectId).Distinct(), Is.EqualTo(new[] { direct }));
        // The original GetRefs format peels tag-of-tag refs too. Verify both captured tag objects, not just the inner one.
        string nested = await Tag(profile, "nested", "refs/tags/direct");
        GitGpgVerification multiple = await profile.Repository.ReadGpgVerificationAsync(selected);
        Assert.That(multiple.Tags.Select(tag => tag.Name), Is.EqualTo(new[] { "direct", "nested" }));
        Assert.That(multiple.Tags.Select(tag => tag.ObjectId), Is.EqualTo(new[] { direct, nested }));
        Assert.That(multiple.Information.TagStatus, Is.EqualTo(TagStatus.Many));
        Assert.That(multiple.Tags.All(tag => tag.Status == TagStatus.TagNotSigned && tag.RawExitCode != 0), Is.True);
        Assert.That(GitGpgPresentation.Format(multiple, value => value), Does.Contain("Multiple tags").And.Contain("nested").And.Not.Contain("other-commit"));
        Assert.That(transport.Calls.Select(call => call.ObjectId).Distinct(), Is.EqualTo(new[] { direct, nested }));
    }

    [TestCase("[GNUPG:] GOODSIG ABCD Test\n[GNUPG:] VALIDSIG ABCD", 0, TagStatus.OneGood)]
    [TestCase("[GNUPG:] NO_PUBKEY ABCD", 1, TagStatus.NoPubKey)]
    [TestCase("error: verifier unavailable", 128, TagStatus.OneBad)]
    public async Task RealTagObjectsUseTheControlledVerificationProcessAndKeepMissingKeysOrErrorsHonest(string raw, int exit, TagStatus expected)
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        await using ControlledVerifier transport = new(profile);
        string commit = await Head(profile), tag = await Tag(profile, "verified", commit);
        transport.Set(tag, raw, "native verification diagnostic", exit);
        GitGpgVerification info = await profile.Repository!.ReadGpgVerificationAsync(commit);
        Assert.That(info.Information.TagStatus, Is.EqualTo(expected));
        Assert.That(info.Tags.Single().RawExitCode, Is.EqualTo(exit)); Assert.That(info.Tags.Single().MessageExitCode, Is.EqualTo(exit));
        Assert.That(info.Information.TagVerificationMessage, Is.EqualTo("native verification diagnostic\n"));
        Assert.That(transport.Calls.Select(call => (call.ObjectId, call.Raw)), Is.EqualTo(new[] { (tag, true), (tag, false) }));
        Assert.That(transport.Calls.All(call => call.Args.Last() == tag && call.Args.Contains("verify-tag")), Is.True);
        // This exercises real process/argv/exit transport and classification, not cryptographic validity of a real key.
        string rendered = GitGpgPresentation.Format(info, value => value);
        Assert.That(rendered, Does.Contain("verified").And.Contain("native verification diagnostic"));
        if (expected == TagStatus.NoPubKey) { Assert.That(rendered, Does.Contain("Public key unavailable").And.Not.Contain("Valid tag signature")); }
        if (expected == TagStatus.OneBad) { Assert.That(rendered, Does.Contain("Tag signature error").And.Not.Contain("Valid tag signature")); }
    }

    [Test]
    public async Task MultipleDirectTagsKeepEveryNativeMessageAndIndividualStatusWhileTheWindowsAggregateIsMany()
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        await using ControlledVerifier transport = new(profile); string commit = await Head(profile);
        string first = await Tag(profile, "first", commit), second = await Tag(profile, "second", commit);
        transport.Set(first, "GOODSIG ... VALIDSIG", "first diagnostic", 0);
        transport.Set(second, "NO_PUBKEY ABCD", "second diagnostic", 1);
        GitGpgVerification info = await profile.Repository!.ReadGpgVerificationAsync(commit);
        Assert.That(info.Information.TagStatus, Is.EqualTo(TagStatus.Many));
        Assert.That(info.Tags.Select(tag => tag.Status), Is.EquivalentTo(new[] { TagStatus.OneGood, TagStatus.NoPubKey }));
        Assert.That(info.Information.TagVerificationMessage, Does.Contain("first\nfirst diagnostic").And.Contain("second\nsecond diagnostic"));
        Assert.That(GitGpgPresentation.Format(info, value => value), Does.Contain("Multiple tags").And.Contain("first diagnostic").And.Contain("second diagnostic"));
        Assert.That(transport.Calls.Length, Is.EqualTo(4));
    }

    [Test]
    public async Task ReverificationOfTheSameCommitIsFreshWhenTheVerificationContextChanges()
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        await using ControlledVerifier transport = new(profile); string commit = await Head(profile), tag = await Tag(profile, "fresh", commit);
        Dictionary<string, byte[]> material = profile.Material();
        transport.Set(tag, "NO_PUBKEY ABCD", "not available", 1);
        Assert.That((await profile.Repository!.ReadGpgVerificationAsync(commit)).Information.TagStatus, Is.EqualTo(TagStatus.NoPubKey));
        transport.Set(tag, "GOODSIG ... VALIDSIG", "now verified", 0);
        Assert.That((await profile.Repository.ReadGpgVerificationAsync(commit)).Information.TagStatus, Is.EqualTo(TagStatus.OneGood));
        Assert.That(transport.Calls.Length, Is.EqualTo(4), "Signature verification must never use the immutable Git object cache.");
        profile.AssertMaterial(material);
    }

    [Test]
    public async Task PrecancelledReadDoesNotLaunchTheControlledVerifier()
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        await using ControlledVerifier transport = new(profile); string commit = await Head(profile), tag = await Tag(profile, "not-started", commit);
        transport.Set(tag, "NO_PUBKEY ABCD", "must not run", 1); using CancellationTokenSource token = new(); token.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await profile.Repository!.ReadGpgVerificationAsync(commit, token.Token));
        Assert.That(transport.Calls, Is.Empty);
    }

    [Test]
    public async Task CancellingTheReadDrainsTheActualVerifierAndItsDescendantBeforeReturning()
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        await using ControlledVerifier transport = new(profile); string commit = await Head(profile), tag = await Tag(profile, "blocked", commit);
        transport.Set(tag, "NO_PUBKEY ABCD", "stale result", 1, block: true); using CancellationTokenSource token = new();
        Task<GitGpgVerification> read = profile.Repository!.ReadGpgVerificationAsync(commit, token.Token);
        try
        {
            await transport.WaitBlockedAsync(); ControlledVerifier.ActorReceipt receipt = transport.Receipt;
            Assert.That(ControlledVerifier.Present(receipt.Pid, receipt.StartTicks), Is.True);
            Assert.That(ControlledVerifier.Present(receipt.ChildPid, receipt.ChildStartTicks), Is.True);
            token.Cancel(); Assert.CatchAsync<OperationCanceledException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(20)));
            transport.AssertRetired(receipt);
        }
        finally { token.Cancel(); transport.Release(); try { await read.WaitAsync(TimeSpan.FromSeconds(20)); } catch (OperationCanceledException) { } }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task SelectionOrOwnerCloseCancelsPendingTagVerificationWithoutReplacingRowsOrPublishingAStaleSignature(bool close)
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        await using ControlledVerifier transport = new(profile); string older = await Head(profile), tag = await Tag(profile, "old-selection-tag", older);
        transport.Set(tag, "NO_PUBKEY ABCD", "stale verification must not publish", 1, block: true);
        await profile.Git.ChangeAsync("newer\n", "Newer selection"); string newer = await Head(profile);
        profile.Desktop.Save(new() { Language = "English", StartWithRecentWorkingDir = false, CheckForUpdates = false });
        MainWindow main = new() { Width = 1200, Height = 760 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(profile.Repository!.Path); await DetailedSettingsUiHost.Until(() => !main.IsBusy);
            TabControl tabs = Find<TabControl>(main, "DetailsTabs"); TabItem tab = tabs.Items.OfType<TabItem>().Single(item => item.Name == "GpgTab");
            tabs.SelectedItem = tab; DetailedSettingsUiHost.Pump(main); TextBox panel = (TextBox)tab.Content!;
            await DetailedSettingsUiHost.Until(() => panel.Text?.Contains("Signature status: N", StringComparison.Ordinal) == true);
            ListBox history = Find<ListBox>(main, "RevisionHistory"); object? source = history.ItemsSource;
            main.UpdateLayout(); Control row = history.ContainerFromItem(history.Items.OfType<GitCommit>().Single(commit => commit.Hash == older))!;
            Point point = row.TranslatePoint(new Point(80, row.Bounds.Height / 2), main)!.Value;
            main.MouseDown(point, MouseButton.Left); main.MouseUp(point, MouseButton.Left);
            await transport.WaitBlockedAsync(); ControlledVerifier.ActorReceipt receipt = transport.Receipt;
            Assert.That(history.IsKeyboardFocusWithin, Is.True); Assert.That(history.ItemsSource, Is.SameAs(source));
            Assert.That(history.IsEnabled, Is.True); Assert.That(panel.Text, Does.Contain("Loading data"));
            if (close)
            { main.Close(); await DetailedSettingsUiHost.Until(() => !main.IsVisible); }
            else
            {
                main.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
                await DetailedSettingsUiHost.Until(() => (history.SelectedItem as GitCommit)?.Hash == newer && panel.Text?.Contains("Signature status: N", StringComparison.Ordinal) == true);
                Assert.That(panel.Text, Does.Not.Contain("old-selection-tag").And.Not.Contain("stale verification"));
                Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.IsKeyboardFocusWithin, Is.True);
            }
            await transport.WaitRetiredAsync(receipt); transport.AssertRetired(receipt); Assert.That(main.LastException, Is.Null);
        }
        finally { transport.Release(); main.Close(); await DetailedSettingsUiHost.Until(() => !main.IsVisible); }
    }

    private static async Task<string> Head(DetailedSettingsTestScope profile) => await profile.Repository!.ResolveRevisionAsync("HEAD");
    private static async Task<string> Tag(DetailedSettingsTestScope profile, string name, string revision)
    {
        await profile.Git.GitAsync("-c", "tag.gpgSign=false", "tag", "-a", name, "-m", name, revision);
        return (await profile.Git.GitAsync("rev-parse", "refs/tags/" + name)).StandardOutput.Trim();
    }
    private static T Find<T>(Control root, string name) where T : Control => root.GetLogicalDescendants().OfType<T>()
        .Concat(root.GetVisualDescendants().OfType<T>()).Distinct().Single(control => control.Name == name);

    // Real Git handles repository, commit and ref discovery. Only verify-tag is intercepted by a real private child process.
    // There are no keys, gpg/gpgconf calls, keyserver, agent, network or host-home probes in this fixture.
    private sealed class ControlledVerifier : IAsyncDisposable
    {
        private readonly string _root, _map, _gate, _receipt, _calls;
        private readonly GitEnvironmentOptions _previous = GitEnvironment.Options;
        private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
        internal ControlledVerifier(DetailedSettingsTestScope profile)
        {
            _root = profile.Git.Output("controlled-gpg-transport"); Directory.CreateDirectory(_root);
            _map = Path.Combine(_root, "responses.json"); _gate = Path.Combine(_root, "release"); _receipt = Path.Combine(_root, "blocked.json"); _calls = Path.Combine(_root, "calls.jsonl");
            File.WriteAllText(_map, "{}"); string home = Path.Combine(_root, "home"), gnupg = Path.Combine(_root, "gnupg"); Directory.CreateDirectory(home); Directory.CreateDirectory(gnupg);
            File.SetUnixFileMode(gnupg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            profile.Git.SetEnvironment("HOME", home); profile.Git.SetEnvironment("GNUPGHOME", gnupg); profile.Git.SetEnvironment("GPG_AGENT_INFO", null);
            string executable = Path.Combine(_root, "git");
            File.WriteAllText(executable, """
                #!/usr/bin/python3
                import json,os,pathlib,signal,subprocess,sys,time
                root=pathlib.Path(__file__).parent
                args=sys.argv[1:]
                if 'verify-tag' not in args: os.execv('/usr/bin/git',['/usr/bin/git',*args])
                oid=args[-1]; raw='--raw' in args
                with (root/'calls.jsonl').open('a') as stream: stream.write(json.dumps({'Args':args,'ObjectId':oid,'Raw':raw})+'\n')
                response=json.loads((root/'responses.json').read_text()).get(oid)
                if response is None: os.execv('/usr/bin/git',['/usr/bin/git',*args])
                child=None
                def cancelled(signum,frame): raise KeyboardInterrupt()
                signal.signal(signal.SIGINT,cancelled); signal.signal(signal.SIGTERM,cancelled)
                def ticks(pid): return int(pathlib.Path('/proc/'+str(pid)+'/stat').read_text().rsplit(')',1)[1].split()[19])
                try:
                    if response['block'] and raw:
                        child=subprocess.Popen(['/bin/sleep','60'])
                        receipt={'Pid':os.getpid(),'StartTicks':ticks(os.getpid()),'ChildPid':child.pid,'ChildStartTicks':ticks(child.pid)}
                        (root/'blocked.new').write_text(json.dumps(receipt)); (root/'blocked.new').replace(root/'blocked.json')
                        while not (root/'release').exists(): time.sleep(0.01)
                    print(response['raw'] if raw else response['message'],file=sys.stderr)
                    sys.exit(response['exit'])
                finally:
                    if child:
                        if child.poll() is None: child.terminate()
                        child.wait()
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            GitEnvironment.Configure(_previous with { Executable = executable, HomeDirectory = home });
        }
        internal void Set(string oid, string raw, string message, int exit, bool block = false)
        {
            _values[oid] = new Dictionary<string, object> { ["raw"] = raw, ["message"] = message, ["exit"] = exit, ["block"] = block };
            string temporary = _map + ".new"; File.WriteAllText(temporary, JsonSerializer.Serialize(_values)); File.Move(temporary, _map, overwrite: true);
        }
        internal sealed record Call(string[] Args, string ObjectId, bool Raw);
        internal Call[] Calls => File.Exists(_calls) ? File.ReadAllLines(_calls).Select(line => JsonSerializer.Deserialize<Call>(line)!).ToArray() : [];
        internal sealed record ActorReceipt(int Pid, long StartTicks, int ChildPid, long ChildStartTicks);
        internal ActorReceipt Receipt => JsonSerializer.Deserialize<ActorReceipt>(File.ReadAllText(_receipt))!;
        internal Task WaitBlockedAsync() => DetailedSettingsUiHost.Until(() => File.Exists(_receipt));
        internal void Release() => File.WriteAllText(_gate, "release");
        internal static bool Present(int pid, long ticks)
        {
            try { return long.Parse(File.ReadAllText($"/proc/{pid}/stat").Split(')')[^1].Trim().Split(' ')[19], CultureInfo.InvariantCulture) == ticks; }
            catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
        }
        internal Task WaitRetiredAsync(ActorReceipt receipt) => DetailedSettingsUiHost.Until(() => !Present(receipt.Pid, receipt.StartTicks) && !Present(receipt.ChildPid, receipt.ChildStartTicks));
        internal void AssertRetired(ActorReceipt receipt)
        {
            Assert.That(Present(receipt.Pid, receipt.StartTicks), Is.False, "Matching PID/start, including a zombie, is not retired.");
            Assert.That(Present(receipt.ChildPid, receipt.ChildStartTicks), Is.False);
        }
        public async ValueTask DisposeAsync()
        {
            Release();
            try
            {
                if (File.Exists(_receipt))
                {
                    ActorReceipt receipt = Receipt;
                    await DetailedSettingsUiHost.Until(() => !Present(receipt.Pid, receipt.StartTicks) && !Present(receipt.ChildPid, receipt.ChildStartTicks));
                }
            }
            finally { GitEnvironment.Configure(_previous); }
        }
    }
}

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using RootFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CompareBranchPickerLifecycleTests
{
    [AvaloniaTest]
    [TestCase("refs", false)] [TestCase("refs", true)]
    [TestCase("counts", false)] [TestCase("counts", true)]
    public async Task ClosingComparisonOrOwnerDrainsTheActualBranchReferenceOrCountRead(string phase, bool ownerClose)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private native gates require Linux shell/procfs."); return; }
        using RootFixture root = await RootFixture.CreateAsync();
        string first = await root.Repository.ResolveRevisionAsync("HEAD");
        await root.ChangeAsync("head\n", "Head");
        string head = await root.Repository.ResolveRevisionAsync("HEAD");
        await root.CreateRemoteAsync();
        const string selectedReference = "refs/remotes/origin/main";
        Gate gate = await Gate.CreateAsync(root, phase);
        string before = await Materials(root);
        await using ComparisonLease s = await ComparisonLease.OpenAsync(gate.Repository, new(first), new(selectedReference));
        GitComparison original = s.Dialog.Comparison!;
        string baseText = Field<TextBox>(s.Dialog, "ComparisonBaseRevision").Text!;
        string compareText = Field<TextBox>(s.Dialog, "ComparisonCompareRevision").Text!;
        WindowClosingBehavior previousClosingBehavior = s.Dialog.ClosingBehavior;
        Assert.That(previousClosingBehavior, Is.EqualTo(WindowClosingBehavior.OwnerAndChildWindows));
        await gate.ArmAsync();
        try
        {
            PushProcessTestDriver.Pointer(Field<Button>(s.Dialog, "ComparisonCompareBranch"));
            await Until(s.Owner, () => s.Dialog.OwnedWindows.OfType<CompareBranchPicker>().Any(window => window.IsVisible));
            CompareBranchPicker picker = s.Dialog.OwnedWindows.OfType<CompareBranchPicker>().Single(window => window.IsVisible);
            await Until(s.Owner, () => gate.Identity is not null);
            var identity = gate.Identity!.Value;
            Assert.That(picker.IsBusy && !picker.Completion.IsCompleted && s.Dialog.IsSelectionLoading, Is.True);
            Assert.That(s.Dialog.SelectionCompletion.IsCompleted, Is.False);
            Assert.That(s.Dialog.ClosingBehavior, Is.EqualTo(WindowClosingBehavior.OwnerWindowOnly));
            Assert.That(picker.RequestedThemeVariant, Is.EqualTo(s.Dialog.ActualThemeVariant));
            Assert.That(Field<RadioButton>(picker, "ComparePickRemote").IsChecked, Is.True);
            Assert.That(Field<Button>(picker, "ComparePickAccept").IsEnabled, Is.False);
            Assert.That(Field<TextBlock>(picker, "ComparePickCounts").Text ?? "", Is.Empty);
            if (phase == "refs")
            {
                Assert.That(gate.Arguments, Does.Contain("for-each-ref"));
                Assert.That(Field<ComboBox>(picker, "ComparePickBranch").Items, Is.Empty);
            }
            else
            {
                Assert.That(gate.Arguments, Does.Contain("rev-list").And.Contain("--left-right").And.Contain("--count").And.Contain(head + "..." + head));
                GitReference branch = (GitReference)Field<ComboBox>(picker, "ComparePickBranch").SelectedItem!;
                Assert.That(branch.Name, Is.EqualTo(selectedReference)); Assert.That(branch.Hash, Is.EqualTo(head));
            }
            if (ownerClose) { s.Owner.Close(); } else { s.Dialog.Close(); }
            await Until(s.Owner, () => !s.Dialog.IsVisible && !picker.IsVisible && s.Dialog.SelectionCompletion.IsCompleted
                && (!ownerClose || !s.Owner.IsVisible));
            await picker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await s.Dialog.SelectionCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(Alive(identity), Is.False, "The exact native PID/start identity must disappear, including zombies.");
            Assert.That(s.Dialog.ClosingBehavior, Is.EqualTo(previousClosingBehavior));
            Assert.That(s.Dialog.Comparison, Is.SameAs(original), "A canceled picker cannot publish a branch or reload the comparison.");
            Assert.That(Field<TextBox>(s.Dialog, "ComparisonBaseRevision").Text, Is.EqualTo(baseText));
            Assert.That(Field<TextBox>(s.Dialog, "ComparisonCompareRevision").Text, Is.EqualTo(compareText));
            Assert.That(Field<TextBlock>(picker, "ComparePickCounts").Text ?? "", Is.Empty, "A canceled count read cannot publish late counts.");
            Assert.That(s.Owner.IsVisible, Is.EqualTo(!ownerClose));
            Assert.That(await Materials(root), Is.EqualTo(before));
        }
        finally { await gate.ReleaseAsync(); }
    }

    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void PumpTree(Window root)
    {
        Dispatcher.UIThread.RunJobs();
        if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        foreach (Window child in root.OwnedWindows.ToArray()) { PumpTree(child); }
        Dispatcher.UIThread.RunJobs();
    }
    private static async Task Until(Window root, Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { PumpTree(root); await Task.Delay(10); }
        PumpTree(root);
        if (!condition() && root.IsVisible)
        {
            string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
            Directory.CreateDirectory(directory);
            using var frame = root.GetLastRenderedFrame();
            if (frame is not null)
            {
                string path = Path.Combine(directory, "branch-picker-timeout-" + Guid.NewGuid().ToString("N") + ".png");
                frame.Save(path); TestContext.AddTestAttachment(path);
            }
        }
        Assert.That(condition(), Is.True, "Actual branch picker timed out; visible=" + root.IsVisible
            + " children=" + string.Join(',', root.OwnedWindows.Select(window => window.Name + ":" + window.IsVisible)));
    }
    private static async Task<string> Materials(RootFixture root)
    {
        string profile = root.Output("config");
        return (await root.GitAsync("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput + "\n"
            + string.Join('\n', new[] { ".git/HEAD", ".git/index", ".git/config", "file.txt" }.Select(path => path + ":"
                + (File.Exists(root.PathInRepository(path)) ? Convert.ToHexString(File.ReadAllBytes(root.PathInRepository(path))) : "<absent>")))
            + "\nprofile:" + (Directory.Exists(profile) ? string.Join('\n', Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).Select(path => path + ":" + Convert.ToHexString(File.ReadAllBytes(path)))) : "");
    }
    private static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string? StartIdentity(int pid)
    {
        try { string stat = File.ReadAllText("/proc/" + pid + "/stat"); return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19]; }
        catch (IOException) { return null; }
    }
    private static bool Alive((int Pid, string Start) identity) => StartIdentity(identity.Pid) == identity.Start;

    private sealed class ComparisonLease : IAsyncDisposable
    {
        internal Window Owner { get; } = new() { Width = 1300, Height = 850 };
        internal CompareRevisionsDialog Dialog { get; }
        private readonly Task _modal;
        private ComparisonLease(GitRepository repository, GitCompareRevision first, GitCompareRevision second)
        { Owner.Show(); Dialog = new(repository, first, second); _modal = Dialog.ShowDialog(Owner); }
        internal static async Task<ComparisonLease> OpenAsync(GitRepository repository, GitCompareRevision first, GitCompareRevision second)
        {
            ComparisonLease s = new(repository, first, second);
            try
            {
                await s.Dialog.InitializeAsync();
                await Until(s.Owner, () => !s.Dialog.IsBusy && !s.Dialog.IsSelectionLoading);
                Assert.That(s.Dialog.LastError, Is.Null); return s;
            }
            catch { await s.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            foreach (Window child in Dialog.OwnedWindows.ToArray()) { child.Close(); }
            Dialog.Close();
            await Until(Owner, () => !Dialog.IsVisible && !Dialog.IsBusy && Dialog.SelectionCompletion.IsCompleted);
            await Dialog.SelectionCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            await _modal.WaitAsync(TimeSpan.FromSeconds(10)); Owner.Close(); PumpTree(Owner);
        }
    }

    private sealed class Gate(GitRepository repository, string directory)
    {
        internal GitRepository Repository { get; } = repository;
        internal string Arguments => File.ReadAllText(Path.Combine(directory, "native.args"));
        internal (int Pid, string Start)? Identity
        {
            get
            {
                string path = Path.Combine(directory, "native.pid");
                return File.Exists(path) && int.TryParse(File.ReadAllText(path), out int pid) && StartIdentity(pid) is string start ? (pid, start) : null;
            }
        }
        internal Task ReleaseAsync() => File.WriteAllTextAsync(Path.Combine(directory, "release"), "release\n");
        internal Task ArmAsync() => File.WriteAllTextAsync(Path.Combine(directory, "armed"), "armed\n");
        internal static async Task<Gate> CreateAsync(RootFixture root, string phase)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Native gates require Linux."); }
            string directory = root.Output("branch chooser gate " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "git"); string command = phase == "refs" ? "for-each-ref" : "rev-list";
            await File.WriteAllTextAsync(path, "#!/bin/sh\nblocked=no\nfor arg in \"$@\"; do [ \"$arg\" != " + Quote(command) + " ] || blocked=yes; done\n"
                + "if [ \"$blocked\" = yes ] && [ -f " + Quote(Path.Combine(directory, "armed")) + " ]; then printf '%s\\n' \"$@\" > "
                + Quote(Path.Combine(directory, "native.args")) + "; printf '%s' \"$$\" > " + Quote(Path.Combine(directory, "native.pid"))
                + "; while [ ! -f " + Quote(Path.Combine(directory, "release")) + " ]; do sleep .02; done; fi\nexec /usr/bin/git \"$@\"\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new(new GitRepository(root.Repository.Path, new GitRunner(path)), directory);
        }
    }
}

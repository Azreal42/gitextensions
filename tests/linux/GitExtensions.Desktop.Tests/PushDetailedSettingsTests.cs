using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class PushDetailedSettingsTests
{
    private const string Created = "server/été-$(literal);雪";

    [AvaloniaTest]
    public async Task EnabledMultipleReadsTheActualAdvancedServerWithoutFetchingOrSelectingAnAction()
    {
        await using F f = await F.CreateAsync(); f.Profile.Write(GitDistributedSettingsScope.Local, (DetailedRepositorySettings.RemoteBranches, "true"));
        string advanced = await f.AdvanceAsync(Created, deleteObsolete: true); string before = f.Materials(); GitCommandLog.Clear();
        PushDialog dialog = await f.ShowAsync(); Assert.That(Reads(), Is.Empty, "Single-branch initialization remains local.");
        PushProcessTestDriver.Pointer(Field<TabItem>(dialog, "PushMultipleTab"));
        await UiUntil(() => dialog.OwnedWindows.OfType<GitProcessDialog>().Any(process => !process.IsRunning));
        GitProcessDialog process = dialog.OwnedWindows.OfType<GitProcessDialog>().Single();
        Assert.That(process.IsVisible && process.UseDialogSettings, Is.True); Assert.That(process.Owner, Is.SameAs(dialog));
        Assert.That(process.CommandDescription, Does.Contain("ls-remote").And.Contain("origin"));
        Assert.That(process.Result!.StandardOutput, Does.Contain(advanced + "\trefs/heads/" + Created));
        Assert.That(dialog.MultipleRowsReady, Is.False); Assert.That(Rows(dialog), Is.Empty, "No partial rows become actionable while the process awaits acknowledgment.");
        PushProcessTestDriver.Pointer(Field<Button>(process, "Ok"));
        await PushProcessTestDriver.UntilAsync(dialog, () => !dialog.IsBusy && dialog.MultipleRowsReady);
        Assert.That(RemoteOnly(dialog), Does.Contain(Created).And.Not.Contain("obsolete"));
        Assert.That(Cell(LocalRow(dialog, "main"), 2), Is.EqualTo("="), "Windows retains the cached tracked ahead/behind rather than traversing an unfetched server object.");
        Assert.That(dialog.LastRemoteHeadsProcess?.Success, Is.True); AssertNoActions(dialog);
        Assert.That(Reads().Select(entry => entry.Directory), Is.EqualTo(new[] { f.Repository.Path }));
        Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Command is "git fetch" or "git push"), Is.False);
        Assert.That(await f.Text("rev-parse", "refs/remotes/origin/main"), Is.EqualTo(f.Initial));
        Assert.That(await f.Text("rev-parse", "refs/remotes/origin/obsolete"), Is.EqualTo(f.Initial));
        Assert.That((await new GitRunner().RunAsync(f.Repository.Path, ["cat-file", "-e", advanced + "^{commit}"])).Success, Is.False);
        Assert.That(f.Materials(), Is.EqualTo(before), "Reading preserves every local/server/writer ref, index, config, object, XML file and working file byte.");
    }

    [AvaloniaTest]
    public async Task AbsentAndFalseFlagsKeepTheOriginalLocalRemoteTrackingList()
    {
        foreach (bool explicitFalse in new[] { false, true })
        {
            await using F f = await F.CreateAsync();
            if (explicitFalse) { f.Profile.Write(GitDistributedSettingsScope.Local, (DetailedRepositorySettings.RemoteBranches, "false")); }
            await f.AdvanceAsync(Created, deleteObsolete: true); string before = f.Materials(); GitCommandLog.Clear();
            PushDialog dialog = await f.ShowAsync(); await f.MultipleAsync(dialog);
            Assert.That(RemoteOnly(dialog), Does.Contain("obsolete").And.Not.Contain(Created));
            Assert.That(Reads(), Is.Empty); Assert.That(dialog.LastRemoteHeadsProcess, Is.Null); AssertNoActions(dialog);
            Assert.That(f.Materials(), Is.EqualTo(before));
        }
    }

    [AvaloniaTest]
    public async Task UnknownOrEmptyHigherPriorityRawValuesMaskLowerTrueFlagsBeforeParsing()
    {
        foreach (string raw in new[] { "future-boolean", "" })
        {
            await using F f = await F.CreateAsync();
            f.Profile.Write(GitDistributedSettingsScope.Global, (DetailedRepositorySettings.RemoteBranches, "true"));
            f.Profile.Write(GitDistributedSettingsScope.Distributed, (DetailedRepositorySettings.RemoteBranches, "true"));
            f.Profile.Write(GitDistributedSettingsScope.Local, (DetailedRepositorySettings.RemoteBranches, raw), ("future.λ", "keep"));
            await f.AdvanceAsync(Created, deleteObsolete: true); string before = f.Materials(); GitCommandLog.Clear();
            PushDialog dialog = await f.ShowAsync(); await f.MultipleAsync(dialog);
            Assert.That(RemoteOnly(dialog), Does.Contain("obsolete").And.Not.Contain(Created)); Assert.That(Reads(), Is.Empty);
            Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Local)).Values[DetailedRepositorySettings.RemoteBranches], Is.EqualTo(raw));
            Assert.That(f.Materials(), Is.EqualTo(before)); AssertNoActions(dialog);
        }
    }

    [AvaloniaTest]
    public async Task EnabledSingleAndTagsAndTheirRemoteChangesNeverOpenATransport()
    {
        await using F f = await F.CreateAsync(); f.Profile.Write(GitDistributedSettingsScope.Global, (DetailedRepositorySettings.RemoteBranches, "true"));
        string before = f.Materials(); GitCommandLog.Clear(); PushDialog dialog = await f.ShowAsync();
        Assert.That(Field<AutoCompleteBox>(dialog, "PushRemoteBranch").ItemsSource!.Cast<string>(), Does.Contain("obsolete"));
        PushProcessTestDriver.Pointer(Field<TabItem>(dialog, "PushTagsTab"));
        Field<ComboBox>(dialog, "PushRemote").SelectedItem = "other"; await UiUntil(() => !dialog.IsBusy);
        Assert.That(Field<AutoCompleteBox>(dialog, "PushTag").ItemsSource!.Cast<string>(), Does.Contain("local-tag"));
        PushProcessTestDriver.Pointer(Field<TabItem>(dialog, "PushBranchesTab"));
        Field<ComboBox>(dialog, "PushRemote").SelectedItem = "origin"; await UiUntil(() => !dialog.IsBusy);
        Assert.That(Reads(), Is.Empty); Assert.That(dialog.LastRemoteHeadsProcess, Is.Null); Assert.That(dialog.OwnedWindows, Is.Empty);
        Assert.That(dialog.Attempted || dialog.Succeeded, Is.False); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task MultipleRefreshesOnTheSelectedRemoteAndOnReturningToTheTabWithoutRetainingActions()
    {
        await using F f = await F.CreateAsync(); f.Profile.Write(GitDistributedSettingsScope.Distributed, (DetailedRepositorySettings.RemoteBranches, "true"));
        await f.AdvanceAsync(Created, deleteObsolete: true); PushDialog dialog = await f.ShowAsync(); string before = f.Materials(); GitCommandLog.Clear();
        await f.MultipleAsync(dialog); Assert.That(RemoteOnly(dialog), Does.Contain(Created));
        LocalRow(dialog, "main").Children.OfType<CheckBox>().First().IsChecked = true;
        GitProcessDialogResult first = dialog.LastRemoteHeadsProcess!;
        Field<ComboBox>(dialog, "PushRemote").SelectedItem = "other";
        await PushProcessTestDriver.UntilAsync(dialog, () => !dialog.IsBusy && dialog.MultipleRowsReady && !ReferenceEquals(dialog.LastRemoteHeadsProcess, first));
        Assert.That(RemoteOnly(dialog), Does.Contain("other-only").And.Not.Contain(Created).And.Not.Contain("obsolete")); AssertNoActions(dialog);
        Assert.That(dialog.LastRemoteHeadsProcess!.Result!.StandardOutput, Does.Contain("refs/heads/other-only").And.Not.Contain(Created));
        Field<ComboBox>(dialog, "PushRemote").SelectedItem = "origin";
        await PushProcessTestDriver.UntilAsync(dialog, () => !dialog.IsBusy && dialog.MultipleRowsReady && RemoteOnly(dialog).Contains(Created));
        Assert.That(f.Materials(), Is.EqualTo(before));
        PushProcessTestDriver.Pointer(Field<TabItem>(dialog, "PushBranchesTab"));
        await f.AdvanceAsync("later/λ", deleteObsolete: false); before = f.Materials();
        int reads = Reads().Length; await f.MultipleAsync(dialog);
        Assert.That(Reads().Length, Is.EqualTo(reads + 1)); Assert.That(RemoteOnly(dialog), Does.Contain("later/λ").And.Contain(Created));
        AssertNoActions(dialog); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task AFailedServerReadLeavesAnEmptyUnreadyGridAndRetainsTheActualProcessError()
    {
        await using F f = await F.CreateAsync(); f.Profile.Write(GitDistributedSettingsScope.Local, (DetailedRepositorySettings.RemoteBranches, "true"));
        await f.Profile.Git.GitAsync("remote", "set-url", "origin", f.Profile.Git.Output("missing repository λ.git"));
        string before = f.Materials(); GitCommandLog.Clear(); PushDialog dialog = await f.ShowAsync();
        PushProcessTestDriver.Pointer(Field<TabItem>(dialog, "PushMultipleTab"));
        await UiUntil(() => dialog.OwnedWindows.OfType<GitProcessDialog>().Any(process => !process.IsRunning));
        GitProcessDialog process = dialog.OwnedWindows.OfType<GitProcessDialog>().Single();
        Assert.That(process.Result!.Success, Is.False); Assert.That(process.Result.StandardError, Is.Not.Empty);
        Assert.That(process.StandardErrorBytes.ToArray(), Is.EqualTo(process.Result.StandardErrorBytes));
        PushProcessTestDriver.Pointer(Field<Button>(process, "Ok")); await UiUntil(() => !dialog.IsBusy);
        Assert.That(dialog.LastRemoteHeadsProcess!.Result!.Success, Is.False); Assert.That(dialog.LastError, Is.Not.Empty);
        Assert.That(dialog.IsVisible, Is.True); AssertUnavailable(dialog); Assert.That(Reads().Length, Is.EqualTo(1));
        Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task CancellingTheRealReadProcessDrainsItsTransportAndCannotRestoreOldLocalRows()
    {
        await using F f = await F.CreateAsync(); f.Profile.Write(GitDistributedSettingsScope.Local, (DetailedRepositorySettings.RemoteBranches, "true"));
        string marker = await f.InstallTransportGateAsync(); string before = f.Materials(); GitCommandLog.Clear();
        PushDialog dialog = await f.ShowAsync(); PushProcessTestDriver.Pointer(Field<TabItem>(dialog, "PushMultipleTab"));
        await UiUntil(() => File.Exists(marker) && dialog.OwnedWindows.OfType<GitProcessDialog>().Any(process => process.IsRunning));
        GitProcessDialog process = dialog.OwnedWindows.OfType<GitProcessDialog>().Single();
        Actor[] actors = File.ReadAllText(marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => Actor.Read(int.Parse(value, CultureInfo.InvariantCulture))).ToArray();
        Assert.That(actors.Length, Is.EqualTo(3)); Assert.That(actors.All(actor => actor.Alive()), Is.True);
        Assert.That(actors.All(actor => actor.Group == actors[0].Group) && actors[0].Group != Actor.Read(Environment.ProcessId).Group, Is.True);
        Assert.That(Rows(dialog), Is.Empty); Assert.That(dialog.MultipleRowsReady, Is.False);
        PushProcessTestDriver.Pointer(Field<Button>(process, "Abort"));
        await UiUntil(() => !dialog.IsBusy && dialog.LastRemoteHeadsProcess is not null);
        Assert.That(dialog.LastRemoteHeadsProcess!.WasCancelled, Is.True); Assert.That(dialog.LastRemoteHeadsProcess.Result, Is.Null);
        Assert.That(actors.Any(actor => actor.Alive()), Is.False, "The user Abort must wait for the exact witnessed Git/helper/sleeper identities.");
        Assert.That(dialog.IsVisible, Is.True); AssertUnavailable(dialog); Assert.That(Reads().Length, Is.EqualTo(1));
        Assert.That(f.Materials(), Is.EqualTo(before));
    }

    private static T Field<T>(TopLevel host, string name) where T : Control => PushProcessTestDriver.Field<T>(host, name);
    private static Grid[] Rows(PushDialog dialog) => Field<StackPanel>(dialog, "PushBranchGrid").Children.OfType<Grid>().ToArray();
    private static string[] RemoteOnly(PushDialog dialog) => Rows(dialog).Where(row => row.Tag is null).Select(row => row.Children.OfType<TextBox>().Single().Text!).ToArray();
    private static Grid LocalRow(PushDialog dialog, string source) => Rows(dialog).Single(row => row.Tag as string == source);
    private static string? Cell(Grid row, int column) => row.Children.OfType<TextBlock>().Single(cell => Grid.GetColumn(cell) == column).Text;
    private static GitCommandLogEntry[] Reads() => GitCommandLog.Snapshot().Where(entry => entry.Command == "git ls-remote").ToArray();
    private static void AssertNoActions(PushDialog dialog)
    {
        Assert.That(Rows(dialog).SelectMany(row => row.Children.OfType<CheckBox>()).Any(box => box.IsChecked == true), Is.False);
        Assert.That(dialog.ReadOptions().Actions, Is.Empty); Assert.That(Field<Button>(dialog, "ExecutePush").IsEnabled, Is.False);
        Assert.That(dialog.Attempted || dialog.Succeeded, Is.False);
    }
    private static void AssertUnavailable(PushDialog dialog)
    { Assert.That(dialog.MultipleRowsReady, Is.False); Assert.That(Rows(dialog), Is.Empty); AssertNoActions(dialog); }
    private static async Task UiUntil(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "Detailed Push workflow did not settle.");
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string Tree(string root) => !Directory.Exists(root) ? "<absent>" : string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));

    private sealed record Actor(int Pid, long Start, int Group)
    {
        internal static Actor Read(int pid)
        {
            string stat = File.ReadAllText("/proc/" + pid + "/stat"); string[] parts = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new(pid, long.Parse(parts[19], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
        }
        internal bool Alive()
        {
            try { string stat = File.ReadAllText("/proc/" + Pid + "/stat"); return Read(Pid).Start == Start && stat[(stat.LastIndexOf(')') + 2)] is not ('Z' or 'X' or 'x'); }
            catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
        }
    }

    private sealed class F : IAsyncDisposable
    {
        internal DetailedSettingsTestScope Profile { get; }
        internal GitRepository Repository => Profile.Repository!;
        internal string Server { get; private set; } = "";
        internal string Other { get; private set; } = "";
        internal string Initial { get; private set; } = "";
        private string Writer => Profile.Git.Output("independent-writer");
        private readonly List<PushDialog> _dialogs = [];
        private F(DetailedSettingsTestScope profile) { Profile = profile; }
        internal static async Task<F> CreateAsync()
        {
            DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync(seed: false); F f = new(profile);
            try
            {
                profile.Git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0"); profile.Git.SetEnvironment("GIT_ALLOW_PROTOCOL", "file:ssh");
                f.Initial = await f.Text("rev-parse", "HEAD"); f.Server = await profile.Git.CreateRemoteAsync();
                await f.GitAt(f.Server, "update-ref", "refs/heads/obsolete", f.Initial); await profile.Git.GitAsync("fetch", "origin");
                await profile.Git.GitAsync("config", "branch.main.remote", "origin"); await profile.Git.GitAsync("config", "branch.main.merge", "refs/heads/main");
                f.Other = profile.Git.Output("other.git"); await profile.Git.GitAsync("clone", "--bare", f.Server, f.Other);
                await f.GitAt(f.Other, "update-ref", "refs/heads/other-only", f.Initial); await f.GitAt(f.Other, "update-ref", "-d", "refs/heads/obsolete");
                await profile.Git.GitAsync("remote", "add", "other", f.Other); await profile.Git.GitAsync("fetch", "other");
                await profile.Git.GitAsync("tag", "local-tag"); await profile.Git.GitAsync("branch", "local-only");
                string staged = profile.Git.PathInRepository("staged.txt"); await File.WriteAllTextAsync(staged, "staged\n"); await profile.Git.GitAsync("add", "--", "staged.txt");
                await File.AppendAllTextAsync(staged, "unstaged\n"); await File.AppendAllTextAsync(profile.Git.File, "unstaged tracked\n");
                await File.WriteAllTextAsync(profile.Git.PathInRepository("untracked λ '$()"), "keep\n");
                new ProcessDialogPreferencesStore().Save(new(false)); return f;
            }
            catch { profile.Dispose(); throw; }
        }
        internal async Task<PushDialog> ShowAsync()
        {
            PushDialog dialog = new(Repository); _dialogs.Add(dialog); dialog.Show(); await dialog.InitializeAsync();
            Assert.That(dialog.IsReady && !dialog.IsBusy, Is.True, dialog.LastError); return dialog;
        }
        internal async Task MultipleAsync(PushDialog dialog)
        {
            PushProcessTestDriver.Pointer(Field<TabItem>(dialog, "PushMultipleTab"));
            await PushProcessTestDriver.UntilAsync(dialog, () => !dialog.IsBusy && dialog.MultipleRowsReady);
        }
        internal async Task<string> AdvanceAsync(string branch, bool deleteObsolete)
        {
            if (!Directory.Exists(Writer))
            {
                await Profile.Git.GitAsync("clone", Server, Writer);
                await GitAt(Writer, "config", "user.name", "Independent server writer"); await GitAt(Writer, "config", "user.email", "writer@example.invalid");
                await GitAt(Writer, "config", "commit.gpgsign", "false"); await GitAt(Writer, "config", "core.hooksPath", "/dev/null");
            }
            await File.AppendAllTextAsync(Path.Combine(Writer, "server-change.txt"), branch + "\n"); await GitAt(Writer, "add", "--", "server-change.txt"); await GitAt(Writer, "commit", "-m", "Independent " + branch);
            List<string> arguments = ["push", "origin", "HEAD:refs/heads/main", "HEAD:refs/heads/" + branch]; if (deleteObsolete) { arguments.Add(":refs/heads/obsolete"); }
            await GitAt(Writer, arguments.ToArray()); return (await GitAt(Writer, "rev-parse", "HEAD")).StandardOutput.Trim();
        }
        internal async Task<string> InstallTransportGateAsync()
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Private transport/procfs regression requires Linux."); }
            string marker = Profile.Git.Output("heads-transport.actors"), helper = Profile.Git.Output("heads-ssh");
            await File.WriteAllTextAsync(helper, "#!/bin/sh\nset -eu\nsleep 60 &\nchild=$!\ntrap 'kill \"$child\" 2>/dev/null || :; wait \"$child\" 2>/dev/null || :' 0\n"
                + "printf 'private remote-head read barrier\\n' >&2\nprintf '%s %s %s\\n' \"$$\" \"$PPID\" \"$child\" > " + Quote(marker + ".tmp") + "\nmv " + Quote(marker + ".tmp") + " " + Quote(marker)
                + "\nwait \"$child\"\ntrap - 0\nexec /usr/bin/git upload-pack " + Quote(Server) + "\n");
            File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Profile.Git.SetEnvironment("GIT_SSH", helper); Profile.Git.SetEnvironment("GIT_SSH_COMMAND", null); Profile.Git.SetEnvironment("GIT_SSH_VARIANT", "ssh");
            await Profile.Git.GitAsync("remote", "set-url", "origin", "ssh://detailed-push-fixture.invalid/unused.git"); return marker;
        }
        internal Task<GitResult> GitAt(string path, params string[] args) => Native(path, args);
        private static async Task<GitResult> Native(string path, string[] args)
        { GitResult result = await new GitRunner().RunAsync(path, args); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        internal async Task<string> Text(params string[] args) => (await Profile.Git.GitAsync(args)).StandardOutput.TrimEnd('\r', '\n');
        internal string Materials() => Tree(Repository.Path) + "\nSERVER\n" + Tree(Server) + "\nOTHER\n" + Tree(Other) + "\nWRITER\n" + Tree(Writer)
            + "\nPROFILES\n" + string.Join('\n', Profile.Material().OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ":" + Convert.ToHexString(SHA256.HashData(pair.Value))));
        public async ValueTask DisposeAsync()
        {
            foreach (PushDialog dialog in _dialogs.AsEnumerable().Reverse()) { if (dialog.IsVisible) { dialog.Close(); } }
            await UiUntil(() => _dialogs.All(dialog => !dialog.IsBusy && !PushProcessTestDriver.Windows(dialog).OfType<GitProcessDialog>().Any(process => process.IsRunning)));
            foreach (PushDialog dialog in _dialogs.AsEnumerable().Reverse()) { if (dialog.IsVisible) { dialog.Close(); } }
            Profile.Dispose();
        }
    }
}

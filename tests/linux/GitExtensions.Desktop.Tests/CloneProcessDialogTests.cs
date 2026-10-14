using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CloneProcessDialogTests
{
    [AvaloniaTest]
    public async Task NormalShallowBareAndNoCheckoutClonesWaitForRealOkBeforePublishingHistoryInBothThemes()
    {
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            await using Fixture f = await Fixture.Create(); string before = f.Materials();
            foreach (string mode in new[] { "normal", "shallow", "bare", "no-checkout" })
            {
                Dictionary<string, byte[]?> histories = f.Histories(); CloneDialog clone = await f.Show(mode, theme);
                if (mode == "shallow") { Field<CheckBox>(clone, "CloneFullHistory").IsChecked = false; }
                if (mode == "bare") { Field<RadioButton>(clone, "CloneBare").IsChecked = true; }
                if (mode == "no-checkout") { Field<ComboBox>(clone, "CloneBranches").SelectedIndex = 1; }
                Pointer(Field<Button>(clone, "CloneRepository"));
                GitProcessDialog process = await Process(clone); await Until(() => !process.IsRunning);
                Assert.That(process.Owner, Is.SameAs(clone)); Assert.That(process.ActualThemeVariant, Is.EqualTo(theme));
                Assert.That(process.Result!.Success, Is.True); Assert.That(process.Outcome!.WorkflowStopRequested, Is.False);
                Assert.That(clone.IsBusy, Is.True); Assert.That(clone.CreatedRepository, Is.Null);
                Assert.That(Field<CheckBox>(process, "KeepDialogOpen").IsChecked, Is.True);
                Assert.That(Field<Button>(process, "Ok").IsEnabled, Is.True); Assert.That(Field<Button>(process, "Abort").IsEnabled, Is.False);
                Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Contain("Cloning into").And.EndsWith("Done"));
                f.SameHistories(histories); Capture(process, "clone-process-" + mode + "-" + theme);
                GitResult native = process.Result!; Pointer(Field<Button>(process, "Ok")); await Until(() => !clone.IsBusy && !clone.IsVisible);
                Assert.That(clone.LastError, Is.Null, clone.LastException?.ToString()); Assert.That(clone.LastProcess!.Result, Is.SameAs(native));
                Assert.That(clone.CreatedRepository, Is.Not.Null); GitRepository cloned = clone.CreatedRepository!;
                Assert.That(cloned.IsBare, Is.EqualTo(mode == "bare"));
                Assert.That(await cloned.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Head));
                if (mode == "shallow")
                {
                    Assert.That((await cloned.RunAsync(["rev-parse", "--is-shallow-repository"])).StandardOutput.Trim(), Is.EqualTo("true"));
                    Assert.That((await cloned.GetReferencesAsync()).Select(reference => reference.Name), Does.Contain("refs/remotes/origin/topic"));
                }
                Assert.That(File.Exists(Path.Combine(cloned.Path, "seed.txt")), Is.EqualTo(mode is not ("bare" or "no-checkout")));
                Assert.That(new CloneSourcesStore().Load().First(), Is.EqualTo(f.FileSource));
                Assert.That(new RepositoryHistoryStore().Load().Recent.First().Path, Is.EqualTo(cloned.Path));
                Assert.That(f.Materials(), Is.EqualTo(before));
            }
        }
    }

    [AvaloniaTest]
    public async Task LiveCloneShowsActualGitObjectBytesAndUncheckedKeepOnlyClosesAfterNativeCompletion()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private local SSH transport/procfs requires Linux."); return; }
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            await using Fixture f = await Fixture.Create(); byte[] bytes = [.. Encoding.UTF8.GetBytes("Native clone café λ\r\n"), 0xFF, .. Encoding.UTF8.GetBytes("\n")];
            Gate gate = await f.Transport(bytes); string before = f.Materials(); Dictionary<string, byte[]?> histories = f.Histories();
            CloneDialog clone = await f.Show("live", theme, f.SshSource); Pointer(Field<Button>(clone, "CloneRepository"));
            GitProcessDialog process = await Process(clone); await Until(() => File.Exists(gate.Marker) && process.StandardErrorBytes.Span.IndexOf(bytes) >= 0);
            Actor[] actors = gate.Actors(); Assert.That(process.IsRunning, Is.True); Assert.That(clone.CreatedRepository, Is.Null);
            Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Contain("café λ").And.Contain("\\xFF").And.Not.Contain("�"));
            Assert.That(process.StandardErrorBytes.Span.IndexOf(bytes), Is.GreaterThanOrEqualTo(0)); f.SameHistories(histories);
            Capture(process, "clone-process-live-" + theme); Pointer(Field<CheckBox>(process, "KeepDialogOpen"));
            Assert.That(process.IsRunning && process.IsVisible, Is.True); Assert.That(new ProcessDialogPreferencesStore().Load().CloseProcessDialog, Is.True);
            await File.WriteAllTextAsync(gate.Release, "release\n"); await Until(() => !clone.IsBusy && !clone.IsVisible);
            Assert.That(clone.LastError, Is.Null, clone.LastException?.ToString()); Assert.That(clone.CreatedRepository, Is.Not.Null);
            Assert.That(clone.LastProcess!.Success, Is.True); Assert.That(clone.LastProcess.WorkflowStopRequested, Is.False);
            Assert.That(clone.LastProcess.Result!.StandardErrorBytes.AsSpan().IndexOf(bytes), Is.GreaterThanOrEqualTo(0));
            Assert.That(clone.LastProcess.Attempts.Single().StandardErrorBytes.ToArray(), Is.EqualTo(clone.LastProcess.Result.StandardErrorBytes));
            Assert.That(actors.All(actor => !actor.Alive()), Is.True); Assert.That(f.Materials(), Is.EqualTo(before));
        }
    }

    [AvaloniaTest]
    public async Task NativeCloneFailureStaysOwnedAndVisibleDespiteAutoCloseAndDoesNotPublishHistory()
    {
        await using Fixture f = await Fixture.Create(); new ProcessDialogPreferencesStore().Save(new(true));
        string path = f.Destination("occupied"); Directory.CreateDirectory(path); byte[] keep = [0, 1, 0xFF]; await File.WriteAllBytesAsync(Path.Combine(path, "keep"), keep);
        Dictionary<string, byte[]?> histories = f.Histories(); string before = f.Materials(); CloneDialog clone = await f.Show("occupied");
        Pointer(Field<Button>(clone, "CloneRepository")); GitProcessDialog process = await Process(clone); await Until(() => !process.IsRunning);
        Assert.That(process.Result!.Success, Is.False); Assert.That(process.Error, Is.TypeOf<GitCommandException>());
        Assert.That(process.IsVisible, Is.True); Assert.That(process.OwnedWindows, Is.Empty); Assert.That(clone.CreatedRepository, Is.Null);
        Assert.That(clone.IsBusy, Is.True); Assert.That(process.Owner, Is.SameAs(clone));
        Assert.That(Field<TextBox>(process, "ProcessOutput").Text, Does.Contain(process.Result.StandardError.Trim()));
        GitResult native = process.Result!; Pointer(Field<Button>(process, "Ok")); await Until(() => !clone.IsBusy);
        Assert.That(clone.IsVisible, Is.True); Assert.That(clone.LastProcess!.Result, Is.SameAs(native));
        Assert.That(clone.LastError, Does.Contain("not an empty directory")); Assert.That(clone.CreatedRepository, Is.Null);
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(path, "keep")), Is.EqualTo(keep)); f.SameHistories(histories); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task AbortLiveCloneWaitsForTransportActorsAndReturnsToTheCloneOptionsWithoutPublishingHistory()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private local SSH transport/procfs requires Linux."); return; }
        await using Fixture f = await Fixture.Create(); Gate gate = await f.Transport(Encoding.UTF8.GetBytes("actual cancelled clone object\n"));
        string before = f.Materials(); Dictionary<string, byte[]?> histories = f.Histories(); CloneDialog clone = await f.Show("aborted", source: f.SshSource);
        Pointer(Field<Button>(clone, "CloneRepository")); GitProcessDialog process = await Process(clone); await Until(() => File.Exists(gate.Marker));
        Actor[] actors = gate.Actors(); Assert.That(actors.All(actor => actor.Alive()), Is.True);
        Pointer(Field<Button>(process, "Abort")); await Until(() => !clone.IsBusy);
        Assert.That(process.IsVisible || process.IsRunning, Is.False); Assert.That(clone.IsVisible, Is.True);
        Assert.That(clone.LastProcess!.WasCancelled && clone.LastProcess.WorkflowStopRequested, Is.True); Assert.That(clone.LastProcess.Result, Is.Null);
        Assert.That(clone.CreatedRepository, Is.Null); Assert.That(clone.LastError, Is.EqualTo("Operation cancelled"));
        Assert.That(Field<Button>(clone, "CloneRepository").IsEnabled, Is.True); Assert.That(actors.All(actor => !actor.Alive()), Is.True);
        f.SameHistories(histories); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ClosingCloneWhileItsTransportRunsWaitsForActorsAndKeepsItsVisibleOwnerOpen()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private local SSH transport/procfs requires Linux."); return; }
        await using Fixture f = await Fixture.Create(); Gate gate = await f.Transport(Encoding.UTF8.GetBytes("owner closure object\n"));
        Window owner = f.Owner(); CloneDialog clone = await f.Show("closed", source: f.SshSource, owner: owner);
        Dictionary<string, byte[]?> histories = f.Histories(); string before = f.Materials();
        Pointer(Field<Button>(clone, "CloneRepository")); GitProcessDialog process = await Process(clone); await Until(() => File.Exists(gate.Marker)); Actor[] actors = gate.Actors();
        clone.Close(); await Until(() => !clone.IsBusy && !clone.IsVisible);
        Assert.That(process.IsRunning || process.IsVisible, Is.False); Assert.That(actors.All(actor => !actor.Alive()), Is.True);
        Assert.That(owner.IsVisible, Is.True); Assert.That(owner.OwnedWindows, Is.Empty);
        Assert.That(clone.LastProcess!.WasCancelled && clone.LastProcess.WorkflowStopRequested, Is.True);
        Assert.That(clone.CreatedRepository, Is.Null); f.SameHistories(histories); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task AUniqueOwnerCloseDuringLiveCloneCancelsAndDrainsBeforeClosingAllWindowsWithoutAcknowledgment()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private local SSH transport/procfs requires Linux."); return; }
        await using Fixture f = await Fixture.Create(); Gate gate = await f.Transport(Encoding.UTF8.GetBytes("ancestor closure object\n"));
        Window owner = f.Owner(); CloneDialog clone = await f.Show("ancestor closed", source: f.SshSource, owner: owner);
        Dictionary<string, byte[]?> histories = f.Histories(); string before = f.Materials();
        Pointer(Field<Button>(clone, "CloneRepository")); GitProcessDialog process = await Process(clone); await Until(() => File.Exists(gate.Marker)); Actor[] actors = gate.Actors();
        owner.Close(); // Exactly one request; no Process OK or second close releases the workflow.
        await Until(() => !clone.IsBusy && !clone.IsVisible && !owner.IsVisible);
        Assert.That(process.IsRunning || process.IsVisible, Is.False); Assert.That(actors.All(actor => !actor.Alive()), Is.True);
        Assert.That(owner.OwnedWindows, Is.Empty); Assert.That(clone.OwnedWindows, Is.Empty);
        Assert.That(clone.CreatedRepository, Is.Null); Assert.That(clone.LastProcess!.WasCancelled, Is.True);
        f.SameHistories(histories); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ClosingOwnerAfterNativeCloneBeforeOkRetainsTheRealCloneButNeverPublishesItOrItsHistory()
    {
        await using Fixture f = await Fixture.Create(); Window owner = f.Owner(); CloneDialog clone = await f.Show("finished then closed", owner: owner);
        Dictionary<string, byte[]?> histories = f.Histories(); string before = f.Materials();
        Pointer(Field<Button>(clone, "CloneRepository")); GitProcessDialog process = await Process(clone); await Until(() => !process.IsRunning);
        GitResult native = process.Result!; Assert.That(native.Success, Is.True); Assert.That(clone.CreatedRepository, Is.Null);
        string path = f.Destination("finished then closed"); GitRepository actual = await GitRepository.OpenAsync(path);
        Assert.That(await actual.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Head)); string cloneMaterials = Tree(path);
        owner.Close(); await Until(() => !clone.IsBusy && !clone.IsVisible && !owner.IsVisible);
        Assert.That(clone.LastProcess!.Result, Is.SameAs(native)); Assert.That(clone.LastProcess.WasCancelled, Is.False);
        Assert.That(clone.LastProcess.WorkflowStopRequested, Is.True); Assert.That(clone.CreatedRepository, Is.Null);
        Assert.That(Tree(path), Is.EqualTo(cloneMaterials)); f.SameHistories(histories); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task PostCloneDiscoveryFailureNeverReplacesTheNativeResultOrVisitsHistory()
    {
        await using Fixture f = await Fixture.Create(); CloneDialog clone = await f.Show("discovery failed"); Dictionary<string, byte[]?> histories = f.Histories();
        Pointer(Field<Button>(clone, "CloneRepository")); GitProcessDialog process = await Process(clone); await Until(() => !process.IsRunning);
        GitResult native = process.Result!; Assert.That(native.Success, Is.True);
        string config = Path.Combine(f.Destination("discovery failed"), ".git/config"); await File.AppendAllTextAsync(config, "\n[invalid-private-section\n");
        Pointer(Field<Button>(process, "Ok")); await Until(() => !clone.IsBusy);
        Assert.That(clone.IsVisible, Is.True); Assert.That(clone.LastProcess!.Result, Is.SameAs(native)); Assert.That(clone.LastProcess.Error, Is.Null);
        Assert.That(clone.LastError, Does.Contain("bad config line")); Assert.That(clone.CreatedRepository, Is.Null); f.SameHistories(histories);
    }

    private static T Field<T>(TopLevel owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pointer(Control control) => PushProcessTestDriver.Pointer(control);
    private static async Task<GitProcessDialog> Process(CloneDialog clone)
    { await Until(() => clone.OwnedWindows.OfType<GitProcessDialog>().Any()); return clone.OwnedWindows.OfType<GitProcessDialog>().Single(); }
    private static async Task Until(Func<bool> condition)
    { Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True, "Actual clone/process did not settle within twenty seconds."); }
    private static string Tree(string path) => !Directory.Exists(path) ? "<absent>" : string.Join('\n', Directory.GetFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(file => Path.GetRelativePath(path, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static void Capture(TopLevel owner, string name)
    {
        Dispatcher.UIThread.RunJobs(); owner.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = owner.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name + ".png"); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private sealed record Actor(int Pid, long Start)
    {
        internal static Actor Read(int pid)
        { string text = File.ReadAllText("/proc/" + pid + "/stat"); return new(pid, long.Parse(text[(text.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19], CultureInfo.InvariantCulture)); }
        internal bool Alive()
        {
            try { string text = File.ReadAllText("/proc/" + Pid + "/stat"); string[] fields = text[(text.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries); return long.Parse(fields[19], CultureInfo.InvariantCulture) == Start && fields[0] is not ("Z" or "X" or "x"); }
            catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
        }
    }
    private sealed record Gate(string Marker, string Release)
    { internal Actor[] Actors() => File.ReadAllText(Marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(value => Actor.Read(int.Parse(value, CultureInfo.InvariantCulture))).ToArray(); }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Dictionary<string, string?> _environment = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-clone-process-" + Guid.NewGuid().ToString("N"));
        public GitRepository Source { get; private set; } = null!;
        public string Head { get; private set; } = "";
        public string FileSource => new Uri(Source.Path).AbsoluteUri;
        public string SshSource => "ssh://private-clone.invalid/local-only.git";
        public List<Window> Windows { get; } = [];
        public string Destination(string name) => Path.Combine(Root, "clones", name);
        private Fixture()
        {
            Directory.CreateDirectory(Root);
            foreach ((string name, string value) in new[] { ("XDG_CONFIG_HOME", Path.Combine(Root, "profile")), ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_OPTIONAL_LOCKS", "0"), ("GIT_ALLOW_PROTOCOL", "file:ssh"), ("GIT_TERMINAL_PROMPT", "0") }) { Set(name, value); }
        }
        private void Set(string name, string? value) { _environment.TryAdd(name, Environment.GetEnvironmentVariable(name)); Environment.SetEnvironmentVariable(name, value); }
        public static async Task<Fixture> Create()
        {
            Fixture f = new();
            try
            {
                f.Source = await GitRepository.InitAsync(Path.Combine(f.Root, "source '雪;$()"));
                await f.Source.SetConfigAsync("user.name", "Clone Process UI"); await f.Source.SetConfigAsync("user.email", "clone-process@example.invalid");
                await f.Source.SetConfigAsync("commit.gpgsign", "false"); await f.Source.SetConfigAsync("core.hooksPath", "/dev/null");
                for (int index = 0; index < 3; index++)
                {
                    await File.WriteAllTextAsync(Path.Combine(f.Source.Path, "seed.txt"), "clone source " + index + "\n"); await f.Source.StageAsync(["seed.txt"]); await f.Source.CommitAsync("Clone UI source " + index);
                    if (index == 0) { await f.Source.CreateBranchAsync("topic", checkout: false); }
                }
                f.Head = await f.Source.ResolveRevisionAsync("HEAD");
                new CloneSourcesStore().Visit(f.Source.Path, 30); new RepositoryHistoryStore().Save(new() { Recent = [new(f.Source.Path)] });
                new ClonePreferencesStore().Save(new(f.Root, false)); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public Window Owner() { Window window = new() { Width = 900, Height = 700 }; Windows.Add(window); window.Show(); return window; }
        public async Task<CloneDialog> Show(string name, ThemeVariant? theme = null, string? source = null, Window? owner = null)
        {
            CloneDialog clone = new(Path.Combine(Root, "clones"), source ?? FileSource) { RequestedThemeVariant = theme ?? ThemeVariant.Light }; Windows.Add(clone);
            if (owner is null) { clone.Show(); } else { _ = clone.ShowDialog(owner); }
            await clone.InitializeAsync(); Dispatcher.UIThread.RunJobs(); clone.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Field<TextBox>(clone, "CloneSubdirectory").Text = name; return clone;
        }
        public string Materials() => Tree(Source.Path);
        public Dictionary<string, byte[]?> Histories() => new[] { new CloneSourcesStore().FilePath, new RepositoryHistoryStore().FilePath,
            Path.Combine(Path.GetDirectoryName(new PreferencesStore().FilePath)!, "clone-preferences.json") }.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        public void SameHistories(Dictionary<string, byte[]?> before)
        { foreach ((string path, byte[]? bytes) in before) { Assert.That(File.Exists(path), Is.EqualTo(bytes is not null), path); if (bytes is not null) { Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), path); } } }
        public async Task<Gate> Transport(byte[] bytes)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
            GitBinaryResult saved = await new GitRunner().RunBinaryAsync(Source.Path, ["hash-object", "-w", "--stdin"], standardInput: bytes); Assert.That(saved.Success, Is.True);
            string blob = Encoding.UTF8.GetString(saved.StandardOutput).Trim(); string script = Path.Combine(Root, "private local transport"), marker = script + ".actors", release = script + ".release";
            await File.WriteAllTextAsync(script, "#!/bin/sh\nsleep 60 &\nchild=$!\ntrap 'kill \"$child\" 2>/dev/null || :; wait \"$child\" 2>/dev/null || :' 0\n/usr/bin/git -C " + Quote(Source.Path) + " cat-file blob " + Quote(blob) + " >&2\n"
                + "printf '%s %s %s\\n' \"$$\" \"$PPID\" \"$child\" > " + Quote(marker + ".tmp") + "\nmv " + Quote(marker + ".tmp") + " " + Quote(marker)
                + "\nwhile test ! -f " + Quote(release) + "; do sleep 0.02; done\nkill \"$child\" 2>/dev/null || :\nwait \"$child\" 2>/dev/null || :\ntrap - 0\nexec /usr/bin/git upload-pack " + Quote(Source.Path) + "\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Set("GIT_SSH", script); Set("GIT_SSH_COMMAND", null); Set("GIT_SSH_VARIANT", "ssh"); return new(marker, release);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (Window window in Windows.AsEnumerable().Reverse()) { window.Close(); }
            if (Windows.Count > 0) { await Until(() => Windows.All(window => !window.IsVisible && (window is not CloneDialog clone || !clone.IsBusy))); }
            Dispatcher.UIThread.RunJobs(); foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); }
            if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
        }
    }
}

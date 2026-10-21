using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using RootFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

// Shared by the eight progressive-host paths and subsequent notification-frontier regressions.
// All repository bytes come from real Git; the proxy delays its actual stdout, never a fixture transcript.
[SupportedOSPlatform("linux")]
internal sealed class ProgressiveBrowseTestScope : IAsyncDisposable
{
    internal const string OldName = "names/old [1] λ\n.txt", NewName = "names/new [2] λ\n.txt", DirectoryName = "sub [1] λ\n";
    internal RootFixture Root { get; }
    internal NativeHistoryGate Gate { get; }
    internal MainWindow Main { get; private set; } = null!;
    internal ListBox Rows => Field<ListBox>(Main, "RevisionHistory");
    internal DiffView Diff => Field<DiffView>(Main, "CommitDiff");
    internal TreeView Files => Field<TreeView>(Main, "RevisionFiles");
    internal string[] Native { get; private set; } = [];
    internal string Head { get; private set; } = "";
    internal string Deep { get; private set; } = "";
    internal string DeepFirst { get; private set; } = "";
    private readonly bool _frenchDark;
    private readonly GitEnvironmentOptions _previous;
    private readonly Dictionary<string, string?> _previousEnvironment;
    private readonly CultureInfo _previousCulture;
    private readonly List<MainWindow> _windows = [];

    private ProgressiveBrowseTestScope(RootFixture root, NativeHistoryGate gate, GitEnvironmentOptions previous, Dictionary<string, string?> previousEnvironment, CultureInfo previousCulture, bool frenchDark)
    { Root = root; Gate = gate; _previous = previous; _previousEnvironment = previousEnvironment; _previousCulture = previousCulture; _frenchDark = frenchDark; }

    internal static async Task<ProgressiveBrowseTestScope> CreateAsync(bool frenchDark = false)
    {
        GitEnvironmentOptions previous = GitEnvironment.Options;
        Dictionary<string, string?> previousEnvironment = new[] { "XDG_CONFIG_HOME", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "GIT_OPTIONAL_LOCKS" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        CultureInfo previousCulture = CultureInfo.CurrentUICulture;
        RootFixture root = await RootFixture.CreateAsync(unborn: true);
        try
        {
            root.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(frenchDark ? "fr" : "en");
            NativeHistoryGate gate = await NativeHistoryGate.CreateAsync(root);
            ProgressiveBrowseTestScope f = new(root, gate, previous, previousEnvironment, previousCulture, frenchDark);
            await f.ImportAsync(root.Repository.Path, "main", 160, "newer", 130);
            f.Head = (await f.Git("rev-parse", "HEAD")).StandardOutput.Trim();
            f.Deep = (await f.Git("rev-parse", "HEAD~109")).StandardOutput.Trim();
            f.DeepFirst = (await f.Git("rev-parse", "HEAD~110")).StandardOutput.Trim();
            f.Native = (await f.Git("log", "--format=%H", "--all", "--")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.That(Array.IndexOf(f.Native, f.Head), Is.GreaterThan(100), "The captured HEAD must genuinely arrive after the first batch.");
            await File.WriteAllTextAsync(root.File, "INDEX FILE\n"); await f.Git("add", "file.txt"); await File.WriteAllTextAsync(root.File, "WORKTREE FILE\n");
            new PreferencesStore().Save(new DesktopPreferences { MaxRevisionGraphCommits = 0, HistoryLimit = 100, StartWithRecentWorkingDir = false, Theme = frenchDark ? "Dark" : "Light" });
            new HistoryViewPreferencesStore().Save(new() { ShowArtificialCommits = true, ShowAvatar = false, ShowStashes = false, ShowSessionCheckpoints = false, Sort = GitLogSort.GitDefault });
            new BrowserLayoutPreferencesStore().Save(new(CommitInfoPosition.RightwardFromList));
            new FileHistoryPreferencesStore().Save(new());
            GitEnvironment.Configure(previous with { Executable = gate.Executable });
            return f;
        }
        catch { GitEnvironment.Configure(previous); root.Dispose(); throw; }
    }

    // This entry must run synchronously in the test caller's ExecutionContext, after await CreateAsync.
    internal IDisposable EnterPresentation()
    {
        Application application = Application.Current ?? throw new AssertionException("No real headless application for the presentation scope.");
        CultureInfo previousCulture = CultureInfo.CurrentUICulture;
        ThemeVariant? previousTheme = application.RequestedThemeVariant;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(_frenchDark ? "fr" : "en");
        application.RequestedThemeVariant = _frenchDark ? ThemeVariant.Dark : ThemeVariant.Light;
        return new PresentationLease(previousCulture, application, previousTheme);
    }
    private sealed class PresentationLease(CultureInfo previousCulture, Application application, ThemeVariant? previousTheme) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) { return; } _disposed = true;
            CultureInfo.CurrentUICulture = previousCulture;
            application.RequestedThemeVariant = previousTheme;
        }
    }
    internal void AssertPresentation(MainWindow window)
    {
        Assert.That(CultureInfo.CurrentUICulture.Name, Is.EqualTo(_frenchDark ? "fr" : "en"));
        Assert.That(window.ActualThemeVariant, Is.EqualTo(_frenchDark ? ThemeVariant.Dark : ThemeVariant.Light));
        Assert.That(Application.Current!.RequestedThemeVariant, Is.EqualTo(_frenchDark ? ThemeVariant.Dark : ThemeVariant.Light));
        TabItem diff = Field<TabItem>(window, "DiffTab");
        string? caption = diff.Header is string text ? text : (diff.Header as Panel)?.Children.OfType<TextBlock>().Single().Text;
        Assert.That(caption, Is.EqualTo(_frenchDark ? "Différenciation" : "Diff"), "The named shared Diff tab must be translated in the actual host, including the compact Log.");
    }

    internal async Task<MainWindow> OpenAsync(BrowseRequest? request = null, Window? owner = null)
    {
        Main = new MainWindow(request ?? new()) { Width = 1440, Height = 960,
            RequestedThemeVariant = _frenchDark ? ThemeVariant.Dark : ThemeVariant.Light };
        _windows.Add(Main);
        if (owner is null) { Main.Show(); } else { Main.Show(owner); }
        await Main.OpenRepositoryAsync(Root.Repository.Path);
        Pump(Main); AssertPresentation(Main);
        return Main;
    }

    internal void Track(MainWindow window) => _windows.Add(window);
    internal Task<GitResult> Git(params string[] argv) => NativeGit(Root.Repository.Path, argv);
    internal static async Task<GitResult> NativeGit(string path, params string[] argv)
    {
        GitResult result = await new GitRunner("/usr/bin/git").RunAsync(path, argv);
        Assert.That(result.Success, Is.True, result.StandardError); return result;
    }

    internal async Task ImportAsync(string repository, string mainBranch, int mainCount, string newerBranch, int newerCount)
    {
        StringBuilder input = new();
        static string Quoted(string path) => "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
        void Data(string text) => input.Append("data ").Append(Encoding.UTF8.GetByteCount(text)).Append('\n').Append(text);
        void File(string path, string text) { input.Append("M 100644 inline ").Append(Quoted(path)).Append('\n'); Data(text); }
        void Branch(string branch, int count, int mark, long date)
        {
            for (int i = 0; i < count; i++)
            {
                bool main = branch == mainBranch;
                string subject = (main && i % 4 == 0 ? "Outside " : "Keep ") + branch + " " + i;
                input.Append("commit refs/heads/").Append(branch).Append("\nmark :").Append(mark + i)
                    .Append("\nauthor Stream Tester <stream@example.invalid> ").Append(date + i).Append(" +0000\ncommitter Stream Tester <stream@example.invalid> ").Append(date + i).Append(" +0000\n");
                // Real output larger than pipe capacity keeps the native producer backpressured beyond each checkpoint.
                Data(subject + "\n\n" + new string('x', 2048) + "\n");
                if (i > 0) { input.Append("from :").Append(mark + i - 1).Append('\n'); }
                File("file.txt", (main ? "MAIN " : "NEWER ") + i + "\n");
                File("a-other.txt", "other " + i + "\n");
                if (main && i == 80)
                {
                    input.Append("D ").Append(Quoted(OldName)).Append('\n');
                    File(NewName, "followed main 79\n"); // An exact native rename, then ordinary edits resume.
                }
                else { File(main && i < 80 ? OldName : NewName, "followed " + branch + " " + i + "\n"); }
                if (!main || i % 4 != 0) { File(DirectoryName + "/inside.txt", "inside " + branch + " " + i + "\n"); }
                else { File("outside.txt", "outside " + i + "\n"); }
                input.Append('\n');
            }
        }
        Branch(mainBranch, mainCount, 1, 1700000000); Branch(newerBranch, newerCount, 10001, 1800000000); input.Append("done\n");
        GitResult imported = await new GitRunner("/usr/bin/git").RunAsync(repository, ["fast-import", "--quiet"], standardInput: input.ToString());
        Assert.That(imported.Success, Is.True, imported.StandardError); await NativeGit(repository, "reset", "--hard", "HEAD");
    }

    internal static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    internal static string Key(object item) => item switch { GitCommit c => c.Hash, ArtificialHistoryRevision a => a.GraphId, _ => throw new AssertionException("Unexpected real history item " + item.GetType()) };
    internal static string[] Hashes(MainWindow window) => Field<ListBox>(window, "RevisionHistory").Items.OfType<GitCommit>().Select(c => c.Hash).ToArray();
    internal static string[] Selected(MainWindow window) => Field<ListBox>(window, "RevisionHistory").SelectedItems!.Cast<object>().Select(Key).ToArray();
    internal static bool Loading(MainWindow window) => (bool)(typeof(MainWindow).GetProperty("IsHistoryLoading", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(window)
        ?? throw new AssertionException("The progressive history loading contract is missing."));
    internal static Task HistoryWork(MainWindow window) => (Task)(typeof(MainWindow).GetProperty("HistoryLoadingCompletion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(window)
        ?? throw new AssertionException("The progressive history drainage contract is missing."));
    internal static async Task Ready(MainWindow window, NativeHistoryGate.Slot gate, int count = 100)
    {
        await Until(window, () => gate.Paused(count) && Hashes(window).Length == count && !window.IsBusy);
        Assert.Multiple(() =>
        {
            Assert.That(Loading(window), Is.True); Assert.That(HistoryWork(window).IsCompleted, Is.False);
            Assert.That(window.WindowWorkCompletion.IsCompleted, Is.False);
            Assert.That(Field<ListBox>(window, "RevisionHistory").IsEffectivelyEnabled, Is.True);
            Assert.That(window.LastException, Is.Null, window.LastException?.ToString());
        });
        Assert.That(gate.Identities, Has.Length.EqualTo(2)); Assert.That(gate.Identities.All(NativeHistoryGate.Present), Is.True);
    }
    internal static async Task Complete(MainWindow window, bool error = false)
    {
        await Until(window, () => !Loading(window) && !window.IsBusy && HistoryWork(window).IsCompleted && window.WindowWorkCompletion.IsCompleted);
        await HistoryWork(window); await window.WindowWorkCompletion; Pump(window);
        if (!error) { Assert.That(window.LastException, Is.Null, window.LastException?.ToString()); }
    }
    internal static async Task Panel(MainWindow window) { await Until(window, () => !window.IsBusy); Pump(window); }
    internal static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs(); if (window.IsVisible) { window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        foreach (Window child in window.OwnedWindows.ToArray()) { Pump(child); } Dispatcher.UIThread.RunJobs();
    }
    internal static async Task Until(Window window, Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Pump(window); await Task.Delay(10); }
        Pump(window); if (!condition()) { Capture(window, "timeout-" + Guid.NewGuid().ToString("N")); }
        Assert.That(condition(), Is.True, $"Progressive host timeout: visible={window.IsVisible}; " + (window is MainWindow main ? $"busy={main.IsBusy}, loading={Loading(main)}, rows={Hashes(main).Length}, work={HistoryWork(main).Status}, error={main.LastException}" : "owner"));
    }
    internal static void Capture(Window window, string name)
    {
        if (!window.IsVisible) { return; }
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); Pump(window); using var frame = window.GetLastRenderedFrame();
        if (frame is not null) { string path = Path.Combine(directory, "progressive-browse-" + name + ".png"); frame.Save(path); TestContext.AddTestAttachment(path); }
    }
    internal static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible && window.PlatformImpl is not null) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    internal static void Edit(Window window, TextBox editor, string value)
    { PushProcessTestDriver.Pointer(editor); Press(window, PhysicalKey.A, RawInputModifiers.Control); window.KeyTextInput(value); Pump(window); Assert.That(editor.Text, Is.EqualTo(value)); }
    internal static async Task Click(MainWindow window, string key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        TimeSpan interval = window.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) ?? throw new AssertionException("No native double-tap contract.");
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(interval.TotalMilliseconds) + 1));
        ListBox rows = Field<ListBox>(window, "RevisionHistory"); object item = rows.Items.Cast<object>().Single(item => Key(item) == key);
        rows.ScrollIntoView(item); Pump(window); Control row = (Control)rows.ContainerFromItem(item)!; Assert.That(row, Is.Not.Null);
        Point point = row.TranslatePoint(new Point(Math.Min(180, row.Bounds.Width / 2), row.Bounds.Height / 2), window)!.Value;
        Assert.That(row.IsEffectivelyVisible && row.IsEffectivelyEnabled && new Rect(default, window.ClientSize).Contains(point), Is.True);
        Assert.That((window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True);
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left, modifiers); window.MouseUp(point, MouseButton.Left, modifiers); Pump(window);
        Assert.That(rows.IsKeyboardFocusWithin, Is.True, "A first physical row click must acquire native focus."); await Panel(window);
    }
    internal static ListBoxItem? Caret(MainWindow window) => (window.FocusManager?.GetFocusedElement() as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
    internal static ScrollViewer Scroller(MainWindow window) => Field<ListBox>(window, "RevisionHistory").GetVisualDescendants().OfType<ScrollViewer>().First();
    internal static GitRevisionDiffGroup[] Groups(MainWindow window) => FileStatusTestHelpers.Rows(Field<TreeView>(window, "RevisionFiles")).Select(item => item.Tag).OfType<GitRevisionDiffGroup>().ToArray();
    internal static async Task DiffTab(MainWindow window)
    { PushProcessTestDriver.Pointer(Field<TabItem>(window, "DiffTab")); await Panel(window); }
    internal static async Task FileRow(MainWindow window, string path)
    {
        TreeView tree = Field<TreeView>(window, "RevisionFiles");
        TreeViewItem item = FileStatusTestHelpers.Rows(tree).First(row => row.Tag is RevisionDiffFileSelection file && file.File.Path == path);
        foreach (TreeViewItem parent in FileStatusTestHelpers.Rows(tree).Where(parent => parent.Items.Contains(item))) { parent.IsExpanded = true; }
        Pump(window);
        Control header = item.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header" && ReferenceEquals(control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), item));
        PushProcessTestDriver.Pointer(header); await Panel(window);
    }
    internal static void ToggleArtificial(MainWindow window)
    {
        MenuItem view = Field<MenuItem>(window, "ViewMenu"); PushProcessTestDriver.Pointer(view); Pump(window);
        PushProcessTestDriver.Pointer(view.Items.OfType<MenuItem>().Single(item => item.Name == "ViewShowArtificialCommits")); Pump(window);
    }
    internal static async Task<HistoryFilterDialog> OpenFilter(MainWindow window)
    {
        PushProcessTestDriver.Pointer(Field<Button>(window, "BrowseAdvancedFilter"));
        await Until(window, () => window.OwnedWindows.OfType<HistoryFilterDialog>().Any(child => child.IsVisible));
        return window.OwnedWindows.OfType<HistoryFilterDialog>().Single(child => child.IsVisible);
    }
    internal static void FilterText(HistoryFilterDialog dialog, string name, string text)
    {
        CheckBox enabled = Field<CheckBox>(dialog, name + "Check"); if (enabled.IsChecked != true) { PushProcessTestDriver.Pointer(enabled); }
        Edit(dialog, Field<TextBox>(dialog, name), text);
    }
    internal static void AcceptFilter(HistoryFilterDialog dialog) => PushProcessTestDriver.Pointer(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault));
    internal async Task<string> Materials()
    {
        string refs = (await Git("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput;
        return refs + string.Join('\n', new[] { ".git/HEAD", ".git/index", ".git/config", "file.txt", "a-other.txt", OldName, NewName }
            .Select(Root.PathInRepository).Where(File.Exists).Select(path => Path.GetRelativePath(Root.Repository.Path, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    }
    internal string ProfileMaterial() => string.Join('\n', Directory.EnumerateFiles(Root.Output("config"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(Root.Output("config"), path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    public async ValueTask DisposeAsync()
    {
        // Closing itself must cancel/drain without a test release. Release is a cleanup fallback only after initiating the actual close.
        bool drained = false;
        try
        {
            foreach (MainWindow window in _windows.AsEnumerable().Reverse()) { if (window.IsVisible) { window.Close(); } }
            Gate.ReleaseAll();
            foreach (MainWindow window in _windows.AsEnumerable().Reverse())
            { await Until(window, () => !window.IsVisible && HistoryWork(window).IsCompleted && window.WindowWorkCompletion.IsCompleted); await HistoryWork(window); await window.WindowWorkCompletion; }
            Gate.AssertRetired(); drained = true;
        }
        finally
        {
            GitEnvironment.Configure(_previous);
            // Preserve evidence rather than delete a fixture which may still own a live native actor.
            if (drained) { Root.Dispose(); }
            else
            {
                foreach ((string name, string? value) in _previousEnvironment) { Environment.SetEnvironmentVariable(name, value); }
                CultureInfo.CurrentUICulture = _previousCulture;
            }
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class NativeHistoryGate(string directory, string executable)
{
    internal string Executable => executable;
    private readonly List<Slot> _slots = [];
    internal Slot Arm(bool fail = false, params int[] checkpoints)
    {
        Slot slot = new(Path.Combine(directory, "generation-" + (_slots.Count + 1).ToString(CultureInfo.InvariantCulture)));
        System.IO.Directory.CreateDirectory(slot.Path); _slots.Add(slot);
        string[] previous = _slots.Take(_slots.Count - 1).Select(s => s.Path).ToArray();
        File.WriteAllText(Path.Combine(directory, "arm.pending"), JsonSerializer.Serialize(new { path = slot.Path, checkpoints = checkpoints.Length == 0 ? new[] { 100 } : checkpoints, fail, previous }));
        File.Move(Path.Combine(directory, "arm.pending"), Path.Combine(directory, "arm"), overwrite: true); return slot;
    }
    internal string[][] Commands => File.Exists(Path.Combine(directory, "commands.jsonl")) ? File.ReadAllLines(Path.Combine(directory, "commands.jsonl")).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray() : [];
    internal static bool IsGraph(string[] argv) => argv.Contains("log") && argv.Any(arg => arg.StartsWith("--format=", StringComparison.Ordinal) && arg.Contains("%x00", StringComparison.Ordinal)) && !argv.Contains("--follow");
    internal void ReleaseAll() { foreach (Slot slot in _slots) { slot.ReleaseAll(); } }
    internal void AssertRetired() { foreach (Slot slot in _slots) { slot.AssertRetired(); } }
    internal sealed class Slot(string path)
    {
        internal string Path => path;
        internal bool Paused(int count = 100) => File.Exists(System.IO.Path.Combine(path, "paused-" + count));
        internal void Release(int count = 100) => File.WriteAllText(System.IO.Path.Combine(path, "release-" + count), "release");
        internal void ReleaseAll() => File.WriteAllText(System.IO.Path.Combine(path, "release-all"), "release");
        internal Identity[] Identities => File.Exists(System.IO.Path.Combine(path, "identities.json")) ? JsonSerializer.Deserialize<Identity[]>(File.ReadAllBytes(System.IO.Path.Combine(path, "identities.json")))! : [];
        internal bool PreviousRetiredAtAdmission => File.ReadAllText(System.IO.Path.Combine(path, "previous-retired")) == "yes";
        internal void AssertRetired() { foreach (Identity identity in Identities) { Assert.That(Retired(identity), Is.True, "Exact native actor still exists (same-start zombies count as present): " + identity); } }
    }
    internal sealed record Identity(int Pid, string Start);
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int Probe(int pid, int signal);
    internal static bool Present(Identity identity) => !Retired(identity);
    private static bool Retired(Identity identity)
    {
        if (Probe(identity.Pid, 0) != 0)
        { int error = Marshal.GetLastPInvokeError(); if (error == 3) { return true; } throw new Win32Exception(error, "Could not establish ESRCH for the native history actor."); }
        try
        {
            string stat = File.ReadAllText("/proc/" + identity.Pid.ToString(CultureInfo.InvariantCulture) + "/stat");
            return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19] != identity.Start;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException || error is IOException { HResult: 3 })
        {
            if (Probe(identity.Pid, 0) == 0) { return false; }
            int native = Marshal.GetLastPInvokeError(); if (native == 3) { return true; } throw new Win32Exception(native, "Missing procfs data did not establish ESRCH.");
        }
    }
    internal static async Task<NativeHistoryGate> CreateAsync(RootFixture root)
    {
        string directory = root.Output("progressive-history-gate"); System.IO.Directory.CreateDirectory(directory); string executable = Path.Combine(directory, "git");
        await File.WriteAllTextAsync(executable, $$"""
            #!/usr/bin/python3
            import os,sys,json,time,subprocess
            from pathlib import Path
            root=Path({{JsonSerializer.Serialize(directory)}})
            argv=sys.argv[1:]
            with (root/'commands.jsonl').open('a') as trace: trace.write(json.dumps(argv)+'\n')
            graph='log' in argv and '--follow' not in argv and any(a.startswith('--format=') and '%x00' in a for a in argv)
            if not graph: os.execv('/usr/bin/git',['git',*argv])
            try: os.rename(root/'arm',root/'claimed')
            except FileNotFoundError: os.execv('/usr/bin/git',['git',*argv])
            config=json.loads((root/'claimed').read_text()); folder=Path(config['path'])
            def identity(pid):
                fields=Path('/proc/'+str(pid)+'/stat').read_text().rsplit(') ',1)[1].split()
                return {'Pid':pid,'Start':fields[19]}
            def present(actor):
                try: os.kill(actor['Pid'],0)
                except ProcessLookupError: return False
                try: return identity(actor['Pid'])['Start']==actor['Start']
                except FileNotFoundError:
                    try: os.kill(actor['Pid'],0)
                    except ProcessLookupError: return False
                    return True
            previous=[]
            for old in config['previous']:
                previous.extend(json.loads((Path(old)/'identities.json').read_text()))
            (folder/'previous-retired').write_text('yes' if not any(present(a) for a in previous) else 'no')
            read_fd,write_fd=os.pipe()
            trampoline="import os,sys; fd=int(sys.argv[1]); ok=os.read(fd,1); os.close(fd); assert ok==b'G'; os.execv('/usr/bin/git',['git',*sys.argv[2:]])"
            child=subprocess.Popen([sys.executable,'-c',trampoline,str(read_fd),*argv],pass_fds=(read_fd,),stdout=subprocess.PIPE,bufsize=0,pipesize=4096)
            os.close(read_fd)
            try:
                (folder/'identities.pending').write_text(json.dumps([identity(os.getpid()),identity(child.pid)]))
                os.replace(folder/'identities.pending',folder/'identities.json')
                os.write(write_fd,b'G')
            except BaseException:
                os.close(write_fd); child.communicate(); raise
            else: os.close(write_fd)
            sent=0; separators=0; checkpoints=iter(config['checkpoints']); target=next(checkpoints,None)
            pending=bytearray(); scanned=0
            while True:
                chunk=child.stdout.read(8192)
                if not chunk: break
                pending.extend(chunk)
                while True:
                    nul=pending.find(b'\0',scanned)
                    if nul<0: scanned=len(pending); break
                    scanned=nul+1
                    separators+=1
                    if separators==8:
                        separators=0; sent+=1
                        if not config['fail'] or sent<=config['checkpoints'][0]:
                            sys.stdout.buffer.write(pending[:scanned]); sys.stdout.buffer.flush()
                        del pending[:scanned]; scanned=0
                        if target is not None and sent==target:
                            (folder/('paused-'+str(target))).write_text('paused')
                            for _ in range(6000):
                                if (folder/('release-'+str(target))).exists() or (folder/'release-all').exists(): break
                                time.sleep(.01)
                            else: raise RuntimeError('Unreleased native history checkpoint')
                            target=next(checkpoints,None)
            if pending and not config['fail']: sys.stdout.buffer.write(pending); sys.stdout.buffer.flush()
            code=child.wait(); (folder/'reaped').write_text(str(code))
            if config['fail']:
                sys.stderr.write('private late graph failure\n'+'e'*70000); sys.stderr.flush(); sys.exit(17)
            sys.exit(code)
            """);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new(directory, executable);
    }
}

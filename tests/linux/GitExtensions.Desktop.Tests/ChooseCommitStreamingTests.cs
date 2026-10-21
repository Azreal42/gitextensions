using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using RootFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable, Platform("Linux")]
[SupportedOSPlatform("linux")]
public sealed class ChooseCommitStreamingTests
{
    [AvaloniaTest]
    [TestCase("en", false)]
    public async Task NativeHistoryBeyondFiveHundredResolvesDeepPreselectionParentsAndGoTo(string language, bool dark)
    {
        using Fixture f = await Fixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        string before = await Materials(f.Root); await using Lease s = Lease.Start(f.Repository, new("HEAD~550"), dark: dark);
        await Complete(s); ListBox rows = Rows(s);
        Assert.That(rows.ItemCount, Is.EqualTo(620).And.GreaterThan(500));
        Assert.That(Hashes(rows), Is.EqualTo(f.Native), "The fixture is one linear chain; the native log supplies its actual order.");
        Assert.That(Selected(rows), Is.EqualTo(f.Native[550])); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
        Assert.That(Field<Button>(s.Dialog, "linkLabelParent").Tag, Is.EqualTo(f.Native[551]));
        Pointer(Field<Button>(s.Dialog, "linkLabelParent")); await Complete(s);
        Assert.That(Selected(rows), Is.EqualTo(f.Native[551])); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
        await StartGoTo(s, "HEAD~610"); await Complete(s);
        Assert.That(Selected(rows), Is.EqualTo(f.Native[610])); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
        Pointer(Field<Button>(s.Dialog, "ChooseAccept")); GitCommit chosen = (await s.Selection)!;
        Assert.That(chosen.Hash, Is.EqualTo(f.Native[610])); Assert.That(chosen.Parents, Is.EqualTo(new[] { f.Native[611] }));
        Assert.That(await Materials(f.Root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase("en", false, false)] [TestCase("fr", true, true)]
    public async Task FirstArrowOrClickOnAlreadySelectedRowCancelsDeferredPreselectionAndKeepsFocusDuringAppend(string language, bool dark, bool click)
    {
        using Fixture f = await Fixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        Gate gate = await Gate.CreateAsync(f.Root); string before = await Materials(f.Root);
        await using Lease s = Lease.Start(gate.Repository, new("HEAD~550"), dark: dark);
        try
        {
            await FirstHundred(s, gate, f.Native); ListBox rows = Rows(s); object source = rows.ItemsSource!;
            Assert.That(rows.IsKeyboardFocusWithin, Is.True);
            int index;
            if (click)
            {
                // Clicking the already-selected row is still a real user intent. There need not
                // be another SelectionChanged event to revoke the original deferred target.
                index = 0; rows.ScrollIntoView(rows.Items[0]!); Pump(s.Owner);
                Assert.That(rows.ContainerFromIndex(0), Is.InstanceOf<ListBoxItem>(), "The first native row must be rendered for a real pointer click.");
                Pointer((Control)rows.ContainerFromIndex(0)!);
            }
            else { index = 1; Press(s.Dialog, PhysicalKey.ArrowDown); }
            Assert.That(Selected(rows), Is.EqualTo(f.Native[index])); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
            object selection = rows.SelectedItem!;
            await gate.ReleaseAsync(); await Complete(s);
            Assert.That(Hashes(rows), Is.EqualTo(f.Native)); Assert.That(rows.ItemsSource, Is.SameAs(source));
            Assert.That(Selected(rows), Is.EqualTo(f.Native[index]).And.Not.EqualTo(f.Native[550]));
            Assert.That(rows.SelectedItem, Is.SameAs(selection)); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
            Press(s.Dialog, PhysicalKey.ArrowDown); Assert.That(Selected(rows), Is.EqualTo(f.Native[index + 1]));
            Assert.That(rows.IsKeyboardFocusWithin, Is.True, "Append must not require another click before keyboard navigation.");
            AssertRetired(gate.Identities); Assert.That(gate.Invocations, Is.EqualTo(1));
            Assert.That(await Materials(f.Root), Is.EqualTo(before));
        }
        finally { await gate.ReleaseAsync(); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ParentAndGoToOutsideTheFirstBatchWaitForThatSameTraversalInsteadOfReportingAnEarlyFilteredError(bool goTo)
    {
        using Fixture f = await Fixture.CreateAsync(); Gate gate = await Gate.CreateAsync(f.Root); string before = await Materials(f.Root);
        await using Lease s = Lease.Start(gate.Repository, new(f.Native[99]));
        try
        {
            await FirstHundred(s, gate, f.Native); ListBox rows = Rows(s);
            Assert.That(Selected(rows), Is.EqualTo(f.Native[99]));
            string target;
            if (goTo) { target = f.Native[550]; await StartGoTo(s, "HEAD~550"); }
            else { target = f.Native[100]; Assert.That(Field<Button>(s.Dialog, "linkLabelParent").Tag, Is.EqualTo(target)); Pointer(Field<Button>(s.Dialog, "linkLabelParent")); }
            Pump(s.Owner); Assert.That(s.Dialog.IsHistoryLoading, Is.True); Assert.That(rows.ItemCount, Is.EqualTo(100));
            Assert.That(s.Dialog.IsBusy, Is.True, "Only the explicitly requested navigation is pending now, not an unreadable/re-disabled first batch.");
            Assert.That(Selected(rows), Is.EqualTo(f.Native[99]));
            Assert.That(s.Dialog.OwnedWindows.Any(child => child.Name == "MessageDialog" && child.IsVisible), Is.False);
            Assert.That(s.Dialog.LastError, Is.Null); Assert.That(s.Dialog.Completion.IsCompleted, Is.False);
            await gate.ReleaseAsync(); await Complete(s);
            Assert.That(Selected(rows), Is.EqualTo(target)); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
            Assert.That(gate.Invocations, Is.EqualTo(1), "Navigation must wait on the native stream; it must not page an evolving --all query.");
            AssertRetired(gate.Identities); Assert.That(await Materials(f.Root), Is.EqualTo(before));
        }
        finally { await gate.ReleaseAsync(); }
    }

    [AvaloniaTest]
    [TestCase("owner")] [TestCase("token")] [TestCase("accept")]
    public async Task ClosingAnAlreadyUsablePartialHistoryDrainsExactProducerAndGitIdentitiesBeforeReturning(string close)
    {
        using Fixture f = await Fixture.CreateAsync(); Gate gate = await Gate.CreateAsync(f.Root); string before = await Materials(f.Root);
        using CancellationTokenSource token = new(); await using Lease s = Lease.Start(gate.Repository, new("HEAD~550"), token.Token);
        try
        {
            await FirstHundred(s, gate, f.Native); ListBox rows = Rows(s); var identities = gate.Identities;
            Assert.That(identities.Length, Is.EqualTo(2)); Assert.That(identities.All(Alive), Is.True);
            if (close == "owner") { s.Owner.Close(); }
            else if (close == "token") { token.Cancel(); }
            else { Pointer(Field<Button>(s.Dialog, "ChooseAccept")); }
            await Until(s.Owner, () => s.Selection.IsCompleted && !s.Dialog.IsVisible && (close != "owner" || !s.Owner.IsVisible));
            await s.Dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            if (close == "token") { Assert.CatchAsync<OperationCanceledException>(async () => await s.Selection); }
            else if (close == "accept") { Assert.That((await s.Selection)!.Hash, Is.EqualTo(f.Native[0]), "Accept must retain the usable current selection while canceling the remaining read."); }
            else { Assert.That(await s.Selection, Is.Null); }
            AssertRetired(identities); Assert.That(s.Owner.IsVisible, Is.EqualTo(close != "owner"));
            Assert.That(s.Dialog.IsHistoryLoading, Is.False); Assert.That(s.Dialog.HasRepositoryChanges, Is.False);
            int count = rows.ItemCount; string? selected = Selected(rows);
            await gate.ReleaseAsync(); Pump(s.Owner);
            Assert.That(rows.ItemCount, Is.EqualTo(count)); Assert.That(Selected(rows), Is.EqualTo(selected));
            Assert.That(await Materials(f.Root), Is.EqualTo(before));
        }
        finally { await gate.ReleaseAsync(); }
    }

    [AvaloniaTest]
    [TestCase("fr", true)]
    public async Task GlobalMaximumPrivateUnlimitedAndResetCheckboxHaveDistinctReadOnlyLifetimes(string language, bool dark)
    {
        using Fixture f = await Fixture.CreateAsync(maximum: 250); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        string before = await Materials(f.Root); await using Lease s = Lease.Start(f.Repository, new(), dark: dark); await Complete(s);
        Assert.That(Hashes(Rows(s)), Is.EqualTo(f.Native.Take(250)));
        HistoryFilterDialog filter = await OpenFilter(s);
        CheckBox enabled = Field<CheckBox>(filter, "CommitsLimitCheck"); NumericUpDown maximum = Field<NumericUpDown>(filter, "CommitsLimit");
        Assert.That(enabled.IsChecked, Is.False); Assert.That(maximum.IsEnabled, Is.False); Assert.That(maximum.Value, Is.EqualTo(250));
        Assert.That(maximum.Minimum, Is.Zero); Assert.That(maximum.Maximum, Is.EqualTo(int.MaxValue));
        Pointer(enabled); Edit(filter, maximum.GetVisualDescendants().OfType<TextBox>().Single(), "0"); Press(filter, PhysicalKey.Tab);
        Assert.That(maximum.Value, Is.Zero); Pointer(filter.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault));
        await FinishFilter(s, filter); Assert.That(Hashes(Rows(s)), Is.EqualTo(f.Native));
        object source = Rows(s).ItemsSource!;
        filter = await OpenFilter(s); Assert.That(Field<CheckBox>(filter, "CommitsLimitCheck").IsChecked, Is.True);
        maximum = Field<NumericUpDown>(filter, "CommitsLimit"); Assert.That(maximum.Value, Is.Zero);
        Edit(filter, maximum.GetVisualDescendants().OfType<TextBox>().Single(), "1"); Press(filter, PhysicalKey.Tab); Press(filter, PhysicalKey.Escape);
        await FinishFilter(s, filter); Assert.That(Rows(s).ItemsSource, Is.SameAs(source)); Assert.That(Hashes(Rows(s)), Is.EqualTo(f.Native));
        filter = await OpenFilter(s); Assert.That(Field<NumericUpDown>(filter, "CommitsLimit").Value, Is.Zero);
        Pointer(filter.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == TranslationCatalog.LoadDefault().Translate("Reset")));
        await FinishFilter(s, filter); Assert.That(Hashes(Rows(s)), Is.EqualTo(f.Native.Take(250)));
        filter = await OpenFilter(s);
        Assert.That(Field<CheckBox>(filter, "CommitsLimitCheck").IsChecked, Is.False);
        Assert.That(Field<NumericUpDown>(filter, "CommitsLimit").Value, Is.EqualTo(250));
        Assert.That(Field<NumericUpDown>(filter, "CommitsLimit").IsEnabled, Is.False);
        Press(filter, PhysicalKey.Escape); await FinishFilter(s, filter);
        Assert.That(await Materials(f.Root), Is.EqualTo(before), "A private maximum, including zero, is never saved as the global graph ceiling.");
    }

    private static async Task FirstHundred(Lease s, Gate gate, IReadOnlyList<string> native)
    {
        await Until(s.Owner, () => gate.IsPaused && Rows(s).ItemCount == 100 && !s.Dialog.IsBusy);
        Pump(s.Owner); Assert.That(s.Dialog.IsVisible && s.Dialog.IsHistoryLoading, Is.True);
        var identities = gate.Identities; Assert.That(identities, Has.Length.EqualTo(2)); Assert.That(identities.All(Alive), Is.True);
        Assert.That(Hashes(Rows(s)), Is.EqualTo(native.Take(100))); Assert.That(s.Dialog.Completion.IsCompleted || s.Selection.IsCompleted, Is.False);
        Assert.That(Rows(s).IsEffectivelyEnabled, Is.True);
        Assert.That(Field<Button>(s.Dialog, "ChooseAccept").IsEnabled && Field<Button>(s.Dialog, "buttonGotoCommit").IsEnabled, Is.True);
        Assert.That(s.Dialog.LastError, Is.Null, s.Dialog.LastException?.ToString());
    }
    private static async Task Complete(Lease s)
    {
        await Until(s.Owner, () => Rows(s).ItemCount > 0 && !s.Dialog.IsBusy && !s.Dialog.IsHistoryLoading && s.Dialog.Completion.IsCompleted);
        await s.Dialog.Completion; Pump(s.Owner); Assert.That(s.Dialog.LastError, Is.Null, s.Dialog.LastException?.ToString());
    }
    private static async Task StartGoTo(Lease s, string expression)
    {
        Pointer(Field<Button>(s.Dialog, "buttonGotoCommit"));
        await Until(s.Owner, () => s.Dialog.OwnedWindows.OfType<GoToCommitDialog>().Any(child => child.IsVisible));
        GoToCommitDialog go = s.Dialog.OwnedWindows.OfType<GoToCommitDialog>().Single(child => child.IsVisible);
        await go.InitializeAsync(); Pump(s.Owner); Edit(go, Field<TextBox>(go, "textboxCommitExpression"), expression); Press(go, PhysicalKey.Enter);
        await Until(s.Owner, () => !go.IsVisible); Assert.That(go.WasAccepted, Is.True); await go.Completion;
    }
    private static async Task<HistoryFilterDialog> OpenFilter(Lease s)
    {
        Pointer(Field<Button>(s.Dialog, "ChooseAdvancedFilter"));
        await Until(s.Owner, () => s.Dialog.OwnedWindows.OfType<HistoryFilterDialog>().Any(child => child.IsVisible));
        return s.Dialog.OwnedWindows.OfType<HistoryFilterDialog>().Single(child => child.IsVisible);
    }
    private static async Task FinishFilter(Lease s, HistoryFilterDialog filter)
    { await Until(s.Owner, () => !filter.IsVisible && !s.Dialog.IsBusy && !s.Dialog.IsHistoryLoading && s.Dialog.Completion.IsCompleted); await s.Dialog.Completion; Pump(s.Owner); Assert.That(s.Dialog.LastError, Is.Null); }
    private static ListBox Rows(Lease s) => Field<ListBox>(s.Dialog, "ChooseRows");
    private static string[] Hashes(ListBox rows) => rows.Items.OfType<GitCommit>().Select(commit => commit.Hash).ToArray();
    private static string? Selected(ListBox rows) => (rows.SelectedItem as GitCommit)?.Hash;
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Pointer(Control control) => PushProcessTestDriver.Pointer(control);
    private static void Edit(Window window, TextBox editor, string text)
    { Pointer(editor); Press(window, PhysicalKey.A, RawInputModifiers.Control); Press(window, PhysicalKey.Backspace); window.KeyTextInput(text); Pump(window); Assert.That(editor.Text, Is.EqualTo(text)); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Pump(Window root)
    {
        Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        foreach (Window child in root.OwnedWindows.ToArray()) { Pump(child); } Dispatcher.UIThread.RunJobs();
    }
    private static async Task Until(Window root, Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Pump(root); await Task.Delay(10); }
        Pump(root);
        if (!condition() && root.IsVisible)
        {
            string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory);
            using var frame = root.GetLastRenderedFrame(); if (frame is not null) { string path = Path.Combine(directory, "choose-stream-timeout-" + Guid.NewGuid().ToString("N") + ".png"); frame.Save(path); TestContext.AddTestAttachment(path); }
        }
        Assert.That(condition(), Is.True, "Streaming chooser did not settle: " + string.Join("; ", root.OwnedWindows.Select(child => child.Name + " visible=" + child.IsVisible
            + (child is ChooseCommitDialog chooser ? " busy=" + chooser.IsBusy + " history=" + chooser.IsHistoryLoading + " error=" + chooser.LastError : ""))));
    }
    private static string? Start(int pid)
    { try { string stat = File.ReadAllText("/proc/" + pid + "/stat"); return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19]; } catch (IOException) { return null; } }
    private static bool Alive((int Pid, string Start) identity) => Start(identity.Pid) == identity.Start;
    private static void AssertRetired(IEnumerable<(int Pid, string Start)> identities)
    { foreach (var identity in identities) { Assert.That(Alive(identity), Is.False, "Exact PID/start is still present, including zombie: " + identity); } }
    private static async Task<string> Materials(RootFixture f)
    {
        string profile = f.Output("config");
        return (await f.GitAsync("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput + "\n"
            + string.Join('\n', new[] { ".git/HEAD", ".git/index", ".git/config", "file.txt" }.Select(path => path + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.PathInRepository(path))))))
            + "\nprofile:" + (Directory.Exists(profile) ? string.Join('\n', Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(path => path + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))) : "");
    }

    private sealed class TestChooser : ChooseCommitDialog
    {
        private readonly CancellationToken _token;
        internal TestChooser(GitRepository repository, ChooseCommitRequest request, CancellationToken token)
            : base(repository, request, new("ChooseRows", "ChooseAccept", "ChooseCancel", "ChooseSearch", "ChooseSearchAction"), token) => _token = token;
        internal Task<GitCommit?> OpenAsync(Window owner) => ShowPickerAsync(this, owner, _token);
    }
    private sealed class Lease : IAsyncDisposable
    {
        internal Window Owner { get; }
        internal TestChooser Dialog { get; }
        internal Task<GitCommit?> Selection { get; }
        private readonly CancellationToken _token;
        private Lease(GitRepository repository, ChooseCommitRequest request, CancellationToken token, bool dark)
        {
            _token = token; Owner = new() { Width = 1200, Height = 900, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; Owner.Show();
            Dialog = new(repository, request, token); Selection = Dialog.OpenAsync(Owner); Pump(Owner);
        }
        internal static Lease Start(GitRepository repository, ChooseCommitRequest request, CancellationToken token = default, bool dark = false) => new(repository, request, token, dark);
        public async ValueTask DisposeAsync()
        {
            foreach (Window child in Dialog.OwnedWindows.ToArray()) { child.Close(); }
            Dialog.Close(); await Until(Owner, () => Selection.IsCompleted && !Dialog.IsVisible && !Dialog.IsHistoryLoading);
            await Dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            try { await Selection.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            Owner.Close(); Pump(Owner);
        }
    }

    private sealed record Fixture(RootFixture Root, string[] Native) : IDisposable
    {
        internal GitRepository Repository => Root.Repository;
        internal static async Task<Fixture> CreateAsync(int maximum = 0)
        {
            RootFixture root = await RootFixture.CreateAsync(unborn: true);
            try
            {
                root.SetEnvironment("GIT_OPTIONAL_LOCKS", "0"); StringBuilder input = new();
                for (int index = 0; index < 620; index++)
                {
                    string message = "Streaming commit " + index.ToString("D4", CultureInfo.InvariantCulture) + "\n";
                    input.Append("commit refs/heads/main\nmark :").Append(index + 1).Append("\ncommitter Streaming Tester <stream@example.invalid> ")
                        .Append(1700000000L + index).Append(" +0000\ndata ").Append(Encoding.UTF8.GetByteCount(message)).Append('\n').Append(message);
                    if (index > 0) { input.Append("from :").Append(index).Append('\n'); }
                    if (index == 0) { input.Append("M 100644 inline file.txt\ndata 5\nseed\n"); }
                    input.Append('\n');
                }
                input.Append("done\n");
                GitResult imported = await new GitRunner("/usr/bin/git").RunAsync(root.Repository.Path, ["fast-import", "--quiet"], standardInput: input.ToString());
                Assert.That(imported.Success, Is.True, imported.StandardError); await root.GitAsync("reset", "--hard", "HEAD");
                new PreferencesStore().Save(new DesktopPreferences { MaxRevisionGraphCommits = maximum, HistoryLimit = 128 });
                new HistoryViewPreferencesStore().Save(new HistoryViewPreferences { Sort = GitLogSort.GitDefault, ShowStashes = false, ShowSessionCheckpoints = false });
                new FileHistoryPreferencesStore().Save(new FileHistoryPreferences());
                string[] native = (await root.GitAsync("log", "--format=%H", "--all", "--")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.That(native, Has.Length.EqualTo(620)); return new(root, native);
            }
            catch { root.Dispose(); throw; }
        }
        public void Dispose() => Root.Dispose();
    }

    private sealed class Gate(string directory, GitRepository repository)
    {
        internal GitRepository Repository { get; } = repository;
        internal bool IsPaused => File.Exists(Path.Combine(directory, "paused"));
        internal int Invocations => File.Exists(Path.Combine(directory, "invocations")) ? File.ReadAllLines(Path.Combine(directory, "invocations")).Length : 0;
        internal (int Pid, string Start)[] Identities
        {
            get
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "identities.json")));
                    return document.RootElement.EnumerateArray().Select(value => (value.GetProperty("pid").GetInt32(), value.GetProperty("start").GetString()!)).ToArray();
                }
                catch (Exception error) when (error is IOException or JsonException) { return []; }
            }
        }
        internal Task ReleaseAsync() => File.WriteAllTextAsync(Path.Combine(directory, "release"), "release\n");
        internal static async Task<Gate> CreateAsync(RootFixture root)
        {
            string directory = root.Output("native-stream-gate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string executable = Path.Combine(directory, "git");
            await File.WriteAllTextAsync(executable, $$"""
                #!/usr/bin/python3
                import os, sys, json, time, subprocess, shutil
                from pathlib import Path
                args = sys.argv[1:]
                if 'log' not in args:
                    os.execv('/usr/bin/git', ['/usr/bin/git', *args])
                directory = Path({{JsonSerializer.Serialize(directory)}})
                with (directory/'invocations').open('a') as out:
                    out.write(str(os.getpid())+'\n')
                child = subprocess.Popen(['/usr/bin/git', *args], stdout=subprocess.PIPE, bufsize=0)
                def identity(pid):
                    fields = Path('/proc/'+str(pid)+'/stat').read_text().rsplit(')',1)[1].strip().split()
                    return dict(pid=pid,start=fields[19])
                (directory/'identities.json').write_text(json.dumps([identity(os.getpid()),identity(child.pid)]))
                prefix = bytearray()
                separators = 0
                while separators < 8*100:
                    value = child.stdout.read(1)
                    if not value:
                        sys.stderr.write('fixture log ended before exactly100 records\n')
                        child.wait()
                        sys.exit(79)
                    prefix.extend(value)
                    if value == b'\0':
                        separators += 1
                sys.stdout.buffer.write(prefix)
                sys.stdout.buffer.flush()
                (directory/'paused').write_text('100')
                while not (directory/'release').exists():
                    time.sleep(.01)
                shutil.copyfileobj(child.stdout,sys.stdout.buffer,length=32768)
                sys.stdout.buffer.flush()
                sys.exit(child.wait())
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new(directory, new GitRepository(root.Repository.Path, new GitRunner(executable)));
        }
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Linux;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using ReferenceFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ComparisonSelectionTests
{
    private const string A = "context-focus-compare-a.txt", B = "context-focus-compare-b.txt", C = "other-file.txt";

    [AvaloniaTest]
    public async Task ARealFilterBurstKeepsEveryCharacterAndDoesNotReadTheSameSelectedFileAgain()
    {
        await using Fixture f = await Fixture.CreateAsync(); CompareRevisionsDialog dialog = f.Dialog;
        TextBox filter = Field<TextBox>(dialog, "ComparisonFileFilter"); string selected = dialog.SelectedFile!.Path;
        int calls = f.Gate.Commands.Count; f.Gate.Arm(selected);
        PushProcessTestDriver.Pointer(filter); TypeBurst(dialog, "context-focus-compare");
        Assert.That(filter.Text, Is.EqualTo("context-focus-compare"));
        Assert.That(dialog.FocusManager!.GetFocusedElement(), Is.SameAs(filter));
        Assert.That(filter.IsEffectivelyEnabled, Is.True); Assert.That(dialog.IsBusy, Is.False);
        await Idle(dialog); Assert.That(dialog.SelectedFile!.Path, Is.EqualTo(selected));
        Assert.That(f.Gate.Commands.Count, Is.EqualTo(calls), "Retaining the same selected file is a local filter operation, not another Git read.");
        Assert.That(Diff(dialog), Does.Contain("new A")); Assert.That(dialog.LastError, Is.Null);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task AFilterBurstStaysEditableDuringTheActualSelectedFileRead(bool dark)
    {
        await using Fixture f = await Fixture.CreateAsync(dark); CompareRevisionsDialog dialog = f.Dialog;
        f.Gate.Arm(B); SelectFile(dialog, B); await Until(() => f.Gate.Started(B)); int pid = f.Gate.LatestPid(B);
        TextBox filter = Field<TextBox>(dialog, "ComparisonFileFilter");
        Assert.That(dialog.IsBusy, Is.False); Assert.That(filter.IsEffectivelyEnabled, Is.True);
        PushProcessTestDriver.Pointer(filter); TypeBurst(dialog, "context-focus-compare");
        Assert.That(filter.Text, Is.EqualTo("context-focus-compare")); Assert.That(dialog.FocusManager!.GetFocusedElement(), Is.SameAs(filter));
        Assert.That(f.Gate.BlockedCommands(B), Is.EqualTo(1), "Filtering must reuse the pending read rather than repeatedly cancelling and starting it.");
        f.Gate.Release(B); await Idle(dialog);
        Assert.That(Alive(pid), Is.False); Assert.That(dialog.SelectedFile!.Path, Is.EqualTo(B));
        Assert.That(dialog.FocusManager!.GetFocusedElement(), Is.SameAs(filter), "Publishing the file must keep the user's filter focus.");
        Assert.That(Diff(dialog), Does.Contain("new B").And.Not.Contain("new A"));
        Assert.That(Field<TextBlock>(dialog, "ComparisonSelectedPath").Text, Is.EqualTo(B));
        Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.False);
        Capture(dialog, "comparison-live-filter-" + (dark ? "dark" : "light") + ".png");
    }

    [AvaloniaTest]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task TheLatestFileWinsAcrossDiffBaseAndCompareReads(int tab)
    {
        await using Fixture f = await Fixture.CreateAsync(); CompareRevisionsDialog dialog = f.Dialog;
        Field<TabControl>(dialog, "ComparisonTabs").SelectedIndex = tab; await Idle(dialog);
        SelectFile(dialog, C); await Idle(dialog); f.Gate.Arm(A); SelectFile(dialog, A);
        await Until(() => f.Gate.Started(A)); int firstPid = f.Gate.LatestPid(A);
        f.Gate.Arm(B); SelectFile(dialog, B); await Until(() => f.Gate.Started(B));
        await Until(() => !Alive(firstPid)); f.Gate.Release(A); f.Gate.Release(B); await Idle(dialog);
        string text = Field<FileHistoryContentView>(dialog, tab == 0 ? "ComparisonDiff" : tab == 1 ? "ComparisonBaseView" : "ComparisonCompareView").TextView.Text;
        Assert.That(text, Does.Contain(tab == 1 ? "old B" : "new B").And.Not.Contain(tab == 1 ? "old A" : "new A"));
        Assert.That(dialog.SelectedFile!.Path, Is.EqualTo(B)); Assert.That(Field<TextBlock>(dialog, "ComparisonSelectedPath").Text, Is.EqualTo(B));
        Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.False);
    }

    [AvaloniaTest]
    public async Task AFilterWithNoMatchesCancelsAndRejectsTheFormerFileThenCanLoadAgain()
    {
        await using Fixture f = await Fixture.CreateAsync(); CompareRevisionsDialog dialog = f.Dialog;
        f.Gate.Arm(B); SelectFile(dialog, B); await Until(() => f.Gate.Started(B)); int pid = f.Gate.LatestPid(B);
        TextBox filter = Field<TextBox>(dialog, "ComparisonFileFilter"); PushProcessTestDriver.Pointer(filter); TypeBurst(dialog, "no-matching-file");
        await Idle(dialog); Assert.That(Alive(pid), Is.False); f.Gate.Release(B);
        Assert.That(dialog.SelectedFile, Is.Null); Assert.That(Diff(dialog), Is.EqualTo("No differences"));
        Assert.That(Field<TextBlock>(dialog, "ComparisonSelectedPath").Text, Is.EqualTo("No differences"));
        Press(dialog, PhysicalKey.A, RawInputModifiers.Control); Press(dialog, PhysicalKey.Backspace); await Idle(dialog);
        Assert.That(dialog.SelectedFile!.Path, Is.EqualTo(A)); Assert.That(Diff(dialog), Does.Contain("new A"));
        Assert.That(dialog.FocusManager!.GetFocusedElement(), Is.SameAs(filter));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ClosingTheDialogOrItsOwnerCancelsAndDrainsTheRealReadBeforeDisposal(bool owner)
    {
        await using Fixture f = await Fixture.CreateAsync(); CompareRevisionsDialog dialog = f.Dialog;
        f.Gate.Arm(B); SelectFile(dialog, B); await Until(() => f.Gate.Started(B)); int pid = f.Gate.LatestPid(B);
        if (owner) { f.Owner.Close(); } else { Press(dialog, PhysicalKey.Escape); }
        await Until(() => !dialog.IsVisible); await ReadCompletion(dialog);
        Assert.That(Alive(pid), Is.False); Assert.That(dialog.HasRepositoryChanges, Is.False);
        if (owner) { await Until(() => !f.Owner.IsVisible); }
        Assert.That(f.Shown.IsCompleted, Is.True); Assert.That(dialog.LastError, Is.Null, "Cancellation of a superseded/closed read is not a file error.");
        f.Gate.Release(B); Pump(f.Owner); Assert.That(dialog.IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task ACurrentReadErrorIsReportedAndTheNextSuccessfulSelectionClearsIt()
    {
        await using Fixture f = await Fixture.CreateAsync(); CompareRevisionsDialog dialog = f.Dialog;
        f.Gate.Arm(B, fail: true); SelectFile(dialog, B); await Idle(dialog);
        Assert.That(dialog.LastError, Does.Contain("fixture comparison file failure"));
        Assert.That(Field<TextBlock>(dialog, "OperationStatus").Text, Does.Contain("fixture comparison file failure"));
        f.Gate.Disarm(); SelectFile(dialog, C); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.LastException, Is.Null);
        Assert.That(Diff(dialog), Does.Contain("new C")); Assert.That(Field<TextBlock>(dialog, "OperationStatus").Text, Does.Not.Contain("fixture comparison file failure"));
    }

    [AvaloniaTest]
    public async Task AnOlderFileReadCannotEraseANewerComparisonFailure()
    {
        await using Fixture f = await Fixture.CreateAsync(); CompareRevisionsDialog dialog = f.Dialog;
        f.Gate.Arm(B); SelectFile(dialog, B); await Until(() => f.Gate.Started(B)); int pid = f.Gate.LatestPid(B);
        TextBox revision = Field<TextBox>(dialog, "ComparisonBaseRevision"); PushProcessTestDriver.Pointer(revision);
        Press(dialog, PhysicalKey.A, RawInputModifiers.Control); dialog.KeyTextInput("missing-comparison-revision");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "CompareRevisions")); await Idle(dialog);
        string? error = dialog.LastError; Assert.That(error, Is.Not.Null.And.Not.Empty); Assert.That(Alive(pid), Is.False);
        f.Gate.Release(B); await ReadCompletion(dialog); Pump(dialog);
        Assert.That(dialog.LastError, Is.EqualTo(error)); Assert.That(Field<TextBlock>(dialog, "OperationStatus").Text, Is.EqualTo(error));
        Assert.That(dialog.Comparison, Is.Null); Assert.That(Diff(dialog), Does.Not.Contain("new B"));
    }

    private static void SelectFile(CompareRevisionsDialog dialog, string path)
    {
        ListBox list = Field<ListBox>(dialog, "ComparisonFiles");
        GitRevisionFile file = list.Items.OfType<GitRevisionFile>().Single(item => item.Path == path); list.ScrollIntoView(file); Pump(dialog);
        PushProcessTestDriver.Pointer((Control)list.ContainerFromIndex(list.Items.IndexOf(file))!); Pump(dialog);
    }
    private static string Diff(CompareRevisionsDialog dialog) => Field<FileHistoryContentView>(dialog, "ComparisonDiff").TextView.Text;
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void TypeBurst(TopLevel root, string text) { foreach (char value in text) { root.KeyTextInput(value.ToString()); } Pump(root); }
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { root.KeyPressQwerty(key, modifiers); if (root.IsVisible) { root.KeyReleaseQwerty(key, modifiers); } Pump(root); }
    private static void Pump(TopLevel root) { Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    // The optional lookup also lets the burst regression run unchanged against
    // the old globally-busy implementation when preserving its red proof.
    private static Task ReadCompletion(CompareRevisionsDialog dialog) => (Task?)typeof(CompareRevisionsDialog)
        .GetProperty("SelectionCompletion", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dialog) ?? Task.CompletedTask;
    private static async Task Idle(CompareRevisionsDialog dialog) { await Until(() => !dialog.IsBusy && ReadCompletion(dialog).IsCompleted); Pump(dialog); }
    private static async Task Until(Func<bool> ready)
    { Stopwatch timer = Stopwatch.StartNew(); while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(ready(), Is.True); }
    private static bool Alive(int pid)
    { try { string stat = File.ReadAllText($"/proc/{pid}/stat"); return stat[(stat.LastIndexOf(')') + 2)..][0] is not ('Z' or 'X'); } catch (IOException) { return false; } }
    private static void Capture(TopLevel root, string name)
    { Pump(root); using var image = root.CaptureRenderedFrame(); Assert.That(image, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); image!.Save(path); TestContext.AddTestAttachment(path); }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ReferenceFixture _repository;
        internal Window Owner { get; } = new() { Width = 1300, Height = 850 };
        internal CompareRevisionsDialog Dialog { get; }
        internal Task Shown { get; }
        internal NativeGate Gate { get; }
        private Fixture(ReferenceFixture repository, NativeGate gate, string before, bool dark)
        {
            _repository = repository; Gate = gate; Owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; Owner.Show();
            Dialog = new(new(repository.Repository.Path, new GitRunner(gate.Executable)), before, "HEAD") { RequestedThemeVariant = Owner.ActualThemeVariant };
            Shown = Dialog.ShowDialog(Owner);
        }
        internal static async Task<Fixture> CreateAsync(bool dark = false)
        {
            if (!OperatingSystem.IsLinux()) { Assert.Ignore("The real delayed Git fixture uses Linux procfs."); }
            ReferenceFixture repository = await ReferenceFixture.CreateAsync(); Fixture? result = null;
            try
            {
                foreach ((string path, string label) in new[] { (A, "A"), (B, "B"), (C, "C") }) { await File.WriteAllTextAsync(repository.PathInRepository(path), "old " + label + "\n"); }
                await repository.GitAsync("add", "."); await repository.GitAsync("commit", "-m", "Comparison before"); string before = await repository.Repository.ResolveRevisionAsync("HEAD");
                foreach ((string path, string label) in new[] { (A, "A"), (B, "B"), (C, "C") }) { await File.WriteAllTextAsync(repository.PathInRepository(path), "new " + label + "\n"); }
                await repository.GitAsync("add", "."); await repository.GitAsync("commit", "-m", "Comparison after");
                NativeGate gate = new(repository); result = new(repository, gate, before, dark); await result.Dialog.InitializeAsync(); await Idle(result.Dialog);
                Assert.That(result.Dialog.LastError, Is.Null); Assert.That(result.Dialog.SelectedFile!.Path, Is.EqualTo(A)); return result;
            }
            catch { if (result is not null) { await result.DisposeAsync(); } else { repository.Dispose(); } throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Gate.Disarm(); Gate.Release(A); Gate.Release(B); Gate.Release(C); Dialog.Close(); await ReadCompletion(Dialog);
            await Until(() => !Dialog.IsVisible); await Shown; Owner.Close(); _repository.Dispose();
        }
    }

    private sealed class NativeGate
    {
        private readonly string _control, _log, _directory;
        internal string Executable { get; }
        internal IReadOnlyList<JsonElement> Commands
        {
            get
            {
                if (!File.Exists(_log)) { return []; }
                List<JsonElement> entries = [];
                foreach (string line in File.ReadAllLines(_log)) { try { using JsonDocument parsed = JsonDocument.Parse(line); entries.Add(parsed.RootElement.Clone()); } catch (JsonException) { } }
                return entries;
            }
        }
        internal NativeGate(ReferenceFixture fixture)
        {
            _directory = fixture.Output("read-gate"); Directory.CreateDirectory(_directory); _control = Path.Combine(_directory, "control.json"); _log = Path.Combine(_directory, "commands.jsonl"); Executable = Path.Combine(_directory, "git");
            File.WriteAllText(Executable, $$"""
                #!/usr/bin/python3
                import os,sys,json,time
                from pathlib import Path
                args=sys.argv[1:]
                path=next((p for p in {{JsonSerializer.Serialize(new[] { A, B, C })}} if p in args),None)
                selected=path is not None and (('diff' in args and '--name-status' not in args and '--name-only' not in args) or 'ls-tree' in args)
                control=json.loads(Path({{JsonSerializer.Serialize(_control)}}).read_text()) if Path({{JsonSerializer.Serialize(_control)}}).exists() else {}
                blocked=selected and control.get('path')==path
                with Path({{JsonSerializer.Serialize(_log)}}).open('a') as log: log.write(json.dumps(dict(pid=os.getpid(),path=path,args=args,blocked=blocked))+'\n')
                if blocked:
                    if control.get('fail'):
                        sys.stderr.write('fixture comparison file failure\n');sys.exit(77)
                    for _ in range(3000):
                        if Path({{JsonSerializer.Serialize(_directory)}}+'/release-'+path).exists():break
                        time.sleep(.01)
                    else:sys.exit(78)
                os.execv('/usr/bin/git',['/usr/bin/git',*args])
                """);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }
        internal void Arm(string path, bool fail = false) { File.Delete(Path.Combine(_directory, "release-" + path)); File.WriteAllText(_control, JsonSerializer.Serialize(new { path, fail })); }
        internal void Disarm() { File.Delete(_control); }
        internal void Release(string path) => File.WriteAllText(Path.Combine(_directory, "release-" + path), "release");
        internal bool Started(string path) => BlockedCommands(path) > 0;
        internal int BlockedCommands(string path) => Commands.Count(entry => entry.GetProperty("path").GetString() == path && entry.GetProperty("blocked").GetBoolean());
        internal int LatestPid(string path) => Commands.Last(entry => entry.GetProperty("path").GetString() == path && entry.GetProperty("blocked").GetBoolean()).GetProperty("pid").GetInt32();
    }
}

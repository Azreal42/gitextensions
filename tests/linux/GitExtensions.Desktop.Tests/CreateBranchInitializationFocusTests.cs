using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Threading;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CreateBranchInitializationFocusTests
{
    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task OpenedInitializationKeepsTheFirstNameKeystrokeWithoutRefocusing(bool blockNativeRead)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        using Gate gate = new(fixture);
        GitRepository repository = blockNativeRead ? new(fixture.Repository.Path, new GitRunner(gate.Executable)) : fixture.Repository;
        Window owner = new() { Width = 800, Height = 600, Content = new TextBox { Name = "OwnerInput" } };
        owner.Show();
        CreateBranchDialog dialog = new(repository);
        Task? initialize = null;
        dialog.Opened += (_, _) => { Log(dialog, "Opened before initialization"); initialize = dialog.InitializeAsync(); };
        Task shown = dialog.ShowDialog(owner);
        try
        {
            TextBox name = PushProcessTestDriver.Field<TextBox>(dialog, "CreateBranchName");
            if (blockNativeRead)
            {
                await Until(() => gate.HasStarted);
                Assert.That(dialog.IsBusy, Is.True);
                Assert.That(name.IsEnabled, Is.False, "The real native status read holds RunAsync's disabled-input interval.");
                Log(dialog, "Git status blocked");
                gate.Release();
            }
            await Until(() => initialize?.IsCompleted == true && !dialog.IsBusy);
            await initialize!; Pump(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Log(dialog, "Initialized on Opened");
            Assert.That(dialog.FocusManager!.GetFocusedElement(), Is.SameAs(name), "InitializeAsync on Opened must preserve the intended name field, without a second click.");
            Type(dialog, "m"); Assert.That(name.Text, Is.EqualTo("m"), "The very first raw text event must reach Branch name.");
            Type(dialog, "arker"); Assert.That(name.Text, Is.EqualTo("marker"));
            Assert.That((await fixture.GitAsync("branch", "--list", "marker")).StandardOutput, Is.Empty);
        }
        finally
        {
            gate.Release(); dialog.Close();
            if (initialize is not null) { await Until(() => initialize.IsCompleted); await initialize; }
            dialog.Close(); await Until(() => shown.IsCompleted); await shown; owner.Close(); Pump(owner);
        }
    }

    [AvaloniaTest]
    public async Task InitializationDoesNotStealAFieldChosenWhileTheNativeReadIsPending()
    {
        using Fixture fixture = await Fixture.CreateAsync(); using Gate gate = new(fixture);
        Window owner = new() { Width = 700, Height = 500 }; owner.Show();
        FocusProbeDialog dialog = new(new(fixture.Repository.Path, new GitRunner(gate.Executable)));
        Task? initialize = null; dialog.Opened += (_, _) => initialize = dialog.InitializeAsync(); Task shown = dialog.ShowDialog(owner);
        try
        {
            await Until(() => gate.HasStarted);
            PushProcessTestDriver.Pointer(dialog.Alternate); Type(dialog, "chosen");
            Assert.That(dialog.Alternate.Text, Is.EqualTo("chosen"));
            gate.Release(); await Until(() => initialize?.IsCompleted == true); await initialize!; Pump(dialog);
            Assert.That(dialog.FocusManager!.GetFocusedElement(), Is.SameAs(dialog.Alternate));
            Type(dialog, "-after"); Assert.That(dialog.Alternate.Text, Is.EqualTo("chosen-after")); Assert.That(dialog.Entry.Text, Is.Null.Or.Empty);
        }
        finally { await Finish(dialog, owner, gate, initialize, shown); }
    }

    [AvaloniaTest]
    public async Task InitializationDoesNotStealFocusFromAnOwnedBusinessDialog()
    {
        using Fixture fixture = await Fixture.CreateAsync(); using Gate gate = new(fixture);
        Window owner = new() { Width = 700, Height = 500 }; owner.Show();
        FocusProbeDialog dialog = new(new(fixture.Repository.Path, new GitRunner(gate.Executable)));
        Task? initialize = null; dialog.Opened += (_, _) => initialize = dialog.InitializeAsync(); Task shown = dialog.ShowDialog(owner);
        GoToCommitDialog? child = null; Task? childShown = null;
        try
        {
            await Until(() => gate.HasStarted);
            child = new(fixture.Repository, readClipboard: _ => Task.FromResult<string?>(null));
            childShown = child.ShowDialog(dialog); Pump(child);
            TextBox expression = PushProcessTestDriver.Field<TextBox>(child, "textboxCommitExpression");
            Assert.That(child.FocusManager!.GetFocusedElement(), Is.SameAs(expression));
            gate.Release(); await Until(() => initialize?.IsCompleted == true); await initialize!; Pump(child);
            Assert.That(child.FocusManager!.GetFocusedElement(), Is.SameAs(expression));
            Type(child, "marker"); Assert.That(expression.Text, Is.EqualTo("marker")); Assert.That(dialog.Entry.Text, Is.Null.Or.Empty);
        }
        finally
        {
            child?.Close(); if (childShown is not null) { await childShown; }
            await Finish(dialog, owner, gate, initialize, shown);
        }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task UpdateActionsCanDisableOrRemoveTheFormerInputWithoutRestoringItsFocus(bool remove)
    {
        using Fixture fixture = await Fixture.CreateAsync(); using Gate gate = new(fixture);
        Window owner = new() { Width = 700, Height = 500 }; owner.Show();
        FocusProbeDialog dialog = new(new(fixture.Repository.Path, new GitRunner(gate.Executable))) { RemoveAfterRead = remove, DisableAfterRead = !remove };
        Task? initialize = null; dialog.Opened += (_, _) => initialize = dialog.InitializeAsync(); Task shown = dialog.ShowDialog(owner);
        try
        {
            await Until(() => gate.HasStarted); gate.Release();
            await Until(() => initialize?.IsCompleted == true); await initialize!; Pump(dialog);
            Assert.That(dialog.FocusManager!.GetFocusedElement(), Is.Not.SameAs(dialog.Entry));
            Assert.That(remove ? TopLevel.GetTopLevel(dialog.Entry) is null : !dialog.Entry.IsEnabled, Is.True);
            Type(dialog, "marker"); Assert.That(dialog.Entry.Text, Is.Null.Or.Empty);
        }
        finally { await Finish(dialog, owner, gate, initialize, shown); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task CloseDuringInitializationCancelsAndDrainsWithoutRestoringTheClosedInput(bool escape)
    {
        using Fixture fixture = await Fixture.CreateAsync(); using Gate gate = new(fixture);
        Window owner = new() { Width = 700, Height = 500 }; owner.Show();
        CreateBranchDialog dialog = new(new(fixture.Repository.Path, new GitRunner(gate.Executable)));
        Task? initialize = null; dialog.Opened += (_, _) => initialize = dialog.InitializeAsync(); Task shown = dialog.ShowDialog(owner);
        try
        {
            await Until(() => gate.HasStarted); int pid = int.Parse(await System.IO.File.ReadAllTextAsync(gate.Started));
            if (escape) { dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); dialog.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); }
            else { dialog.Close(); }
            await Until(() => initialize?.IsCompleted == true && shown.IsCompleted); await initialize!; await shown; Pump(owner);
            Assert.That(dialog.IsVisible, Is.False); Assert.That(dialog.IsBusy, Is.False); Assert.That(Alive(pid), Is.False, "The actual blocked Git reader must be reaped.");
            Assert.That(owner.FocusManager!.GetFocusedElement(), Is.Not.SameAs(PushProcessTestDriver.Field<TextBox>(dialog, "CreateBranchName")));
        }
        finally { await Finish(dialog, owner, gate, initialize, shown); }
    }

    [AvaloniaTest]
    public async Task AnInitializationErrorStillReturnsToTheOriginalInputForRecovery()
    {
        using Fixture fixture = await Fixture.CreateAsync(); using Gate gate = new(fixture);
        Window owner = new() { Width = 700, Height = 500 }; owner.Show();
        FocusProbeDialog dialog = new(new(fixture.Repository.Path, new GitRunner(gate.Executable))) { FailAfterRead = true };
        Task? initialize = null; dialog.Opened += (_, _) => initialize = dialog.InitializeAsync(); Task shown = dialog.ShowDialog(owner);
        try
        {
            await Until(() => gate.HasStarted); gate.Release();
            await Until(() => initialize?.IsCompleted == true); await initialize!; Pump(dialog);
            Assert.That(dialog.LastError, Is.EqualTo("Fixture initialization failed"));
            Assert.That(dialog.FocusManager!.GetFocusedElement(), Is.SameAs(dialog.Entry));
            Type(dialog, "recover"); Assert.That(dialog.Entry.Text, Is.EqualTo("recover"));
        }
        finally { await Finish(dialog, owner, gate, initialize, shown); }
    }

    private static async Task Finish(RepositoryDialog dialog, Window owner, Gate gate, Task? initialize, Task shown)
    {
        gate.Release(); dialog.Close();
        if (initialize is not null) { await Until(() => initialize.IsCompleted); await initialize; }
        dialog.Close(); await Until(() => shown.IsCompleted); await shown; owner.Close(); Pump(owner);
    }

    [AvaloniaTest]
    public async Task ATrackedContainersActualEditableDescendantReceivesFocusAgain()
    {
        using Fixture fixture = await Fixture.CreateAsync(); using Gate gate = new(fixture);
        Window owner = new() { Width = 700, Height = 500 }; owner.Show();
        FocusProbeDialog dialog = new(new(fixture.Repository.Path, new GitRunner(gate.Executable)), nested: true);
        Task? initialize = null; dialog.Opened += (_, _) => initialize = dialog.InitializeAsync(); Task shown = dialog.ShowDialog(owner);
        try
        {
            await Until(() => gate.HasStarted); gate.Release();
            await Until(() => initialize?.IsCompleted == true); await initialize!; Pump(dialog);
            Assert.That(dialog.FocusManager!.GetFocusedElement(), Is.SameAs(dialog.Entry));
            Type(dialog, "marker"); Assert.That(dialog.Entry.Text, Is.EqualTo("marker"));
        }
        finally { await Finish(dialog, owner, gate, initialize, shown); }
    }

    [AvaloniaTest]
    public async Task AnAlreadyRequestedCloseDoesNotRestoreFocusWhenTheFinalCloseIsDeferred()
    {
        using Fixture fixture = await Fixture.CreateAsync(); using Gate gate = new(fixture);
        Window owner = new() { Width = 700, Height = 500 }; owner.Show();
        FocusProbeDialog dialog = new(new(fixture.Repository.Path, new GitRunner(gate.Executable)));
        int closeAttempts = 0;
        EventHandler<WindowClosingEventArgs> deferFinalClose = (_, args) => { if (++closeAttempts == 2) { args.Cancel = true; } };
        dialog.Closing += deferFinalClose;
        Task? initialize = null; dialog.Opened += (_, _) => initialize = dialog.InitializeAsync(); Task shown = dialog.ShowDialog(owner);
        try
        {
            await Until(() => gate.HasStarted); dialog.Close();
            await Until(() => initialize?.IsCompleted == true); await initialize!; Pump(dialog);
            Assert.That(closeAttempts, Is.EqualTo(2)); Assert.That(dialog.IsVisible, Is.True, "A recovery Closing handler deferred final closure.");
            Assert.That(dialog.FocusManager!.GetFocusedElement(), Is.Not.SameAs(dialog.Entry), "A requested close must never transiently restore the disabled input.");
        }
        finally { dialog.Closing -= deferFinalClose; await Finish(dialog, owner, gate, initialize, shown); }
    }

    private static bool Alive(int pid)
    { try { string stat = System.IO.File.ReadAllText($"/proc/{pid}/stat"); return stat[(stat.LastIndexOf(')') + 2)..][0] is not ('Z' or 'X'); } catch (IOException) { return false; } }

    private sealed class FocusProbeDialog : RepositoryDialog
    {
        public TextBox Entry { get; } = new() { Name = "OriginalInput" };
        public TextBox Alternate { get; } = new() { Name = "AlternateInput" };
        public bool DisableAfterRead { get; init; }
        public bool RemoveAfterRead { get; init; }
        public bool FailAfterRead { get; init; }
        private readonly StackPanel _fields;
        private readonly Control _tracked;
        private bool _completed;
        public FocusProbeDialog(GitRepository repository, bool nested = false) : base(repository, "Focus probe")
        {
            Width = 500; Height = 230;
            _tracked = nested ? new Border { Child = Entry } : Entry;
            _fields = new() { Margin = new Avalonia.Thickness(12), Spacing = 12, VerticalAlignment = VerticalAlignment.Top, Children = { _tracked, Alternate } };
            Root.Children.Add(_fields); Track(_tracked); Opened += (_, _) => Entry.Focus();
        }
        public override Task InitializeAsync() => RunAsync("Read native status", async token =>
        {
            await Repository.GetStatusAsync(token); _completed = true;
            if (FailAfterRead) { throw new InvalidOperationException("Fixture initialization failed"); }
        });
        protected override void UpdateActions()
        {
            if (!_completed) { return; }
            if (DisableAfterRead) { Entry.IsEnabled = false; }
            if (RemoveAfterRead) { _fields.Children.Remove(_tracked); }
        }
    }

    private static void Type(Window window, string text)
    { window.KeyTextInput(text); Pump(window); }
    private static void Log(Window window, string phase)
    { Control? focused = window.FocusManager?.GetFocusedElement() as Control; TestContext.WriteLine($"{phase}: focus={focused?.GetType().Name}/{focused?.Name}; nameEnabled={PushProcessTestDriver.Field<TextBox>(window, "CreateBranchName").IsEnabled}"); }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True); }

    private sealed class Gate : IDisposable
    {
        internal string Executable { get; }
        internal string Started { get; }
        internal bool HasStarted => System.IO.File.Exists(Started) && new FileInfo(Started).Length > 0;
        private readonly string _release;
        internal Gate(Fixture fixture)
        {
            if (!OperatingSystem.IsLinux()) { Assert.Ignore("The deterministic gate uses a private Linux Git wrapper."); }
            Executable = fixture.Output("gated-git"); Started = fixture.Output("status-started"); _release = fixture.Output("status-release");
            System.IO.File.WriteAllText(Executable, $$"""
                #!/usr/bin/python3
                import os,sys,time
                from pathlib import Path
                if 'status' in sys.argv[1:]:
                    Path({{JsonSerializer.Serialize(Started)}}).write_text(str(os.getpid()))
                    for _ in range(1500):
                        if Path({{JsonSerializer.Serialize(_release)}}).exists(): break
                        time.sleep(.01)
                    else: sys.exit(74)
                os.execv('/usr/bin/git',['/usr/bin/git',*sys.argv[1:]])
                """);
            if (!OperatingSystem.IsWindows()) { System.IO.File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }
        internal void Release() => System.IO.File.WriteAllText(_release, "release");
        public void Dispose() => Release();
    }
}

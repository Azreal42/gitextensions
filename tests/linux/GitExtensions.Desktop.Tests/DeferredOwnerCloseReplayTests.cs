using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable, SupportedOSPlatform("linux")]
public sealed class DeferredOwnerCloseReplayTests
{
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task InitCancelsAndDrainsItsRealPrivateGitWhileOnlyAnOwnerRequestClosesTheOwner(bool closeOwner)
    {
        using Fixture git = await Fixture.CreateAsync();
        GitEnvironmentOptions previous = GitEnvironment.Options;
        NativeGate gate = NativeGate.Create(git, "init", "");
        string destination = git.Output("blocked init destination");
        Window owner = new() { Width = 800, Height = 500 }; owner.Show();
        InitDialog dialog = new(destination); Task modal = dialog.ShowDialog(owner);
        try
        {
            GitEnvironment.Configure(previous with { Executable = gate.Executable });
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "CreateRepository"));
            await Until(dialog, () => File.Exists(gate.Ready), "The real Init operation must reach its native private Git barrier.");
            Actor actor = Actor.FromReady(gate.Ready);
            Assert.That(dialog.IsBusy, Is.True); Assert.That(actor.IsPresent, Is.True);
            if (closeOwner) { owner.Close(); }
            else { PushProcessTestDriver.Pointer(Field<Button>(dialog, "CancelInit")); }
            await Until(owner, () => !dialog.IsVisible && !dialog.IsBusy && modal.IsCompleted,
                "The first close must cancel and drain the native Init command without a second Close.");
            await modal;
            await Until(owner, () => !actor.IsPresent && (!closeOwner || !owner.IsVisible),
                "No live or zombie native identity may remain when the deferred owner-close completes.");
            Assert.That(File.Exists(gate.Cancelled), Is.True, "The private native process observed the real cancellation signal.");
            Assert.That(dialog.CreatedRepository, Is.Null); Assert.That(dialog.LastError, Is.EqualTo("Operation cancelled"));
            Assert.That(Directory.Exists(Path.Combine(destination, ".git")), Is.False);
            Assert.That(owner.IsVisible, Is.EqualTo(!closeOwner), "An ordinary child Cancel must never close its owner.");
        }
        finally
        {
            try
            {
                gate.Release();
                if (dialog.IsVisible) { dialog.Close(); }
                await Until(owner, () => !dialog.IsBusy && !dialog.IsVisible && modal.IsCompleted, "Cleanup must drain the actual Init operation.");
                await modal;
            }
            finally { GitEnvironment.Configure(previous); if (owner.IsVisible) { owner.Close(); } }
        }
    }

    [AvaloniaTest]
    public async Task RemotesDirtyTrackingCloseStartsOutsideBaseBusyThenDrainsItsRealWriteAndResumesTheOwner()
    {
        using Fixture git = await Fixture.CreateAsync();
        await git.GitAsync("config", "branch.main.remote", ".");
        await git.GitAsync("config", "branch.main.merge", "refs/heads/main");
        await git.GitAsync("config", "branch.main.description", "Preserved branch description.");
        string beforeRefs = (await git.GitAsync("show-ref", "--head")).StandardOutput;
        string beforeConfig = (await git.GitAsync("config", "--local", "--null", "--list")).StandardOutput;
        NativeGate gate = NativeGate.Create(git, "check-ref-format", "refs/heads/close-flush");
        GitRepository repository = new(git.Repository.Path, new GitRunner(gate.Executable));
        Window owner = new() { Width = 1100, Height = 700 }; owner.Show();
        RemotesDialog dialog = new(repository, preselectLocalBranch: "main"); Task modal = dialog.ShowDialog(owner);
        try
        {
            await dialog.InitializeAsync(); Pump(dialog);
            TabControl tabs = Field<TabControl>(dialog, "RemotesTabs");
            TabItem tracking = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "DefaultPullBehaviorTab");
            PushProcessTestDriver.Pointer(tracking);
            await Until(dialog, () => ReferenceEquals(tabs.SelectedItem, tracking)
                && dialog.GetVisualDescendants().OfType<ComboBox>().Any(box => box.Name == "TrackingMergeBranch"),
                "The real tracking tab must be selected and mounted before editing its controls.");
            Assert.That(tabs.SelectedItem, Is.SameAs(tracking));
            ComboBox merge = Field<ComboBox>(dialog, "TrackingMergeBranch");
            TextBox editor = merge.GetVisualDescendants().OfType<TextBox>().Single();
            PushProcessTestDriver.Pointer(editor);
            dialog.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
            dialog.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control);
            dialog.KeyTextInput("close-flush"); Pump(dialog);
            Assert.That(editor.Text, Is.EqualTo("close-flush")); Assert.That(editor.IsKeyboardFocusWithin, Is.True);
            Assert.That(dialog.IsBusy, Is.False); Assert.That(Writes(dialog).IsCompleted, Is.True);
            Assert.That(Read<bool>(dialog, "_mergeDirty"), Is.True, "Only real text input stages this unsaved tracking edit.");
            owner.Close(); // The derived Closing flush, not base IsBusy, owns this first veto.
            await Until(dialog, () => File.Exists(gate.Ready), "Owner-close must flush the dirty field through real native Git.");
            Actor actor = Actor.FromReady(gate.Ready);
            Assert.That(owner.IsVisible && dialog.IsVisible && dialog.IsBusy, Is.True); Assert.That(actor.IsPresent, Is.True);
            gate.Release();
            await Until(owner, () => modal.IsCompleted && Writes(dialog).IsCompleted && !dialog.IsBusy && !dialog.IsVisible && !owner.IsVisible,
                "The original owner-close must resume after the derived flush without a second Close.");
            await modal; await Writes(dialog);
            await Until(owner, () => !actor.IsPresent, "The real tracking command and all its native pipes must drain.");
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Assert.That(dialog.HasRepositoryChanges, Is.True);
            Assert.That((await git.GitAsync("config", "--get", "branch.main.merge")).StandardOutput.Trim(), Is.EqualTo("refs/heads/close-flush"));
            Assert.That((await git.GitAsync("show-ref", "--head")).StandardOutput, Is.EqualTo(beforeRefs));
            string afterConfig = (await git.GitAsync("config", "--local", "--null", "--list")).StandardOutput;
            Assert.That(OtherConfig(afterConfig), Is.EqualTo(OtherConfig(beforeConfig)), "The native close flush must preserve every unrelated config entry.");
        }
        finally
        {
            gate.Release();
            await Until(owner, () => !dialog.IsBusy && Writes(dialog).IsCompleted, "Cleanup must drain tracking writes.");
            if (dialog.IsVisible) { dialog.Close(); }
            await Until(owner, () => !dialog.IsVisible && modal.IsCompleted, "Cleanup must release real modal ownership.");
            await modal; if (owner.IsVisible) { owner.Close(); }
        }
    }

    private sealed record Actor(int Pid, string StartTicks)
    {
        internal static Actor FromReady(string path)
        {
            int pid = int.Parse(File.ReadAllText(path), CultureInfo.InvariantCulture);
            return new(pid, ReadStart(pid) ?? throw new AssertionException("The gate must still own a live native process."));
        }
        internal bool IsPresent => ReadStart(Pid) == StartTicks; // Z remains present and cannot satisfy a drain.
        private static string? ReadStart(int pid)
        {
            try
            {
                string stat = File.ReadAllText($"/proc/{pid}/stat");
                return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19];
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
    }

    private sealed record NativeGate(string Executable, string Ready, string ReleasePath, string Cancelled)
    {
        internal static NativeGate Create(Fixture git, string command, string argument)
        {
            string script = git.Output("owner-close-" + Guid.NewGuid().ToString("N") + ".py");
            NativeGate gate = new(script, script + ".ready", script + ".release", script + ".cancelled");
            File.WriteAllText(script, $$"""
                #!/usr/bin/python3
                import os,signal,sys,time
                from pathlib import Path
                ready={{JsonSerializer.Serialize(gate.Ready)}}
                release={{JsonSerializer.Serialize(gate.ReleasePath)}}
                cancelled={{JsonSerializer.Serialize(gate.Cancelled)}}
                command={{JsonSerializer.Serialize(command)}}
                argument={{JsonSerializer.Serialize(argument)}}
                if command in sys.argv[1:] and (not argument or argument in sys.argv[1:]):
                    def cancel(signum,frame):
                        Path(cancelled).write_text(str(signum))
                        sys.exit(130)
                    signal.signal(signal.SIGINT,cancel)
                    signal.signal(signal.SIGTERM,cancel)
                    Path(ready+'.tmp').write_text(str(os.getpid()))
                    os.replace(ready+'.tmp',ready)
                    deadline=time.monotonic()+30
                    while not Path(release).exists():
                        if time.monotonic()>deadline: sys.exit(124)
                        time.sleep(.01)
                os.execv('/usr/bin/git',['/usr/bin/git',*sys.argv[1:]])
                """, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
            { File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            return gate;
        }
        internal void Release() => File.WriteAllText(ReleasePath, "release private native command\n");
    }

    private static string[] OtherConfig(string config) => config.Split('\0', StringSplitOptions.RemoveEmptyEntries)
        .Where(record => !record.StartsWith("branch.main.merge\n", StringComparison.Ordinal)).ToArray();
    private static Task Writes(RemotesDialog dialog) => Read<Task>(dialog, "_trackingWrites");
    private static T Read<T>(RemotesDialog dialog, string name)
        => (T)typeof(RemotesDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
    private static T Field<T>(TopLevel root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static Task Until(Window owner, Func<bool> ready, string message) => PushProcessTestDriver.UntilAsync(owner, ready, message);
    private static void Pump(TopLevel root)
    { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}

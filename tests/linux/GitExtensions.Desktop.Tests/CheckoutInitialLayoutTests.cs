using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable, SupportedOSPlatform("linux")]
public sealed class CheckoutInitialLayoutTests
{
    [AvaloniaTest]
    [TestCase(true, false, false)]
    [TestCase(true, true, true)]
    [TestCase(false, false, true)]
    public async Task InitialMountKeepsUnknownLocalChangesHiddenUntilInitializationThenThePublishedCheckoutButtonAcceptsItsFirstClick(
        bool checkStatus, bool dirty, bool expectedLocalChanges)
    {
        using GitFixture git = await GitFixture.CreateAsync(); git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
        await git.GitAsync("branch", "other");
        if (dirty) { await File.WriteAllTextAsync(git.File, "Uncommitted local content Ω\n"); }
        byte[] worktree = await File.ReadAllBytesAsync(git.File);
        string head = await git.Repository.ResolveRevisionAsync("HEAD");
        GitReference target = (await git.Repository.GetReferencesAsync()).Single(reference => reference.Name == "refs/heads/other");
        new PreferencesStore().Save(new() { CheckForUncommittedChangesInCheckoutBranch = checkStatus });
        Gate gate = new(git);
        // A fresh repository object guarantees discovery reaches the native gate
        // even if setup already populated the fixture repository's Info cache.
        GitRepository repository = new(git.Repository.Path, new GitRunner(gate.Helper));
        Window owner = new() { Width = 800, Height = 600 }; owner.Show(); await PublishFrameAsync(owner);
        CheckoutBranchDialog dialog = new(repository, target);
        Task? initialization = null;
        dialog.Opened += (_, _) => initialization = dialog.InitializeAsync();
        Task modal = dialog.ShowDialog(owner);
        try
        {
            await Until(() => File.Exists(gate.Ready));
            Assert.That(initialization, Is.Not.Null); Assert.That(initialization!.IsCompleted, Is.False);
            Assert.That(dialog.IsBusy, Is.True);
            Assert.That(dialog.Owner, Is.SameAs(owner));
            Assert.That(owner.OwnedWindows, Does.Contain(dialog));
            Assert.That(modal.IsCompleted, Is.False);
            await PublishFrameAsync(dialog);
            RadioButton changes = Field<RadioButton>(dialog, "CheckoutKeepChanges");
            Button checkout = Field<Button>(dialog, "CheckoutBranch");
            Assert.That(changes.IsEffectivelyVisible, Is.False,
                "Unknown before initialization is not a known dirty worktree; the Local changes group must not be mounted yet.");
            Assert.That(checkout.IsEffectivelyEnabled, Is.False);
            Assert.That(Field<ComboBox>(dialog, "CheckoutBranchSelection").IsEffectivelyEnabled, Is.False);
            Size initialSize = dialog.ClientSize; Rect initialButton = Bounds(checkout, dialog);
            await PublishFrameAsync(dialog);
            Assert.That(dialog.ClientSize, Is.EqualTo(initialSize));
            Assert.That(Bounds(checkout, dialog), Is.EqualTo(initialButton),
                "The initial mapped geometry stays stable while discovery is still pending.");
            Assert.That(initialization.IsCompleted, Is.False);

            gate.Release();
            await initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.IsBusy, Is.False); Assert.That(checkout.IsEffectivelyEnabled, Is.True);
            Assert.That(changes.IsEffectivelyVisible, Is.EqualTo(expectedLocalChanges),
                "A disabled dirty-check keeps the final state unknown and offers explicit local-change choices.");
            Assert.That(gate.Commands().Any(arguments => arguments.Contains("status", StringComparer.Ordinal)), Is.EqualTo(checkStatus),
                "Only initialization commands are inspected here; the checkout workflow retains its own Git protections.");

            // Wait for the completed layout's actual render/hit-test publication.
            // Never poll the hit result, activate the window, move focus as a
            // repair or repeatedly click until the command finally works.
            await PublishFrameAsync(dialog);
            Point point = Center(checkout, dialog); Visual? hit = dialog.InputHitTest(point) as Visual;
            Assert.That(new Rect(default, dialog.ClientSize).Contains(Bounds(checkout, dialog)), Is.True);
            Assert.That(hit?.GetSelfAndVisualAncestors().Contains(checkout), Is.True,
                Diagnostic(dialog, checkout, hit));
            dialog.MouseMove(point); dialog.MouseDown(point, MouseButton.Left); dialog.MouseUp(point, MouseButton.Left);
            await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            await modal.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(dialog.Succeeded, Is.True, dialog.LastException?.ToString());
            Assert.That((await git.GitAsync("symbolic-ref", "--short", "HEAD")).StandardOutput.TrimEnd('\r', '\n'), Is.EqualTo("other"));
            Assert.That(await git.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
            Assert.That(await git.Repository.ResolveRevisionAsync("refs/heads/main"), Is.EqualTo(head));
            Assert.That(await git.Repository.ResolveRevisionAsync("refs/heads/other"), Is.EqualTo(head));
            Assert.That(await File.ReadAllBytesAsync(git.File), Is.EqualTo(worktree));
        }
        finally
        {
            gate.Release();
            if (dialog.IsVisible) { dialog.Close(); }
            if (initialization is not null) { await initialization.WaitAsync(TimeSpan.FromSeconds(15)); }
            await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            await modal.WaitAsync(TimeSpan.FromSeconds(10)); owner.Close();
        }
    }

    private sealed class Gate
    {
        private readonly string _hold, _log;
        internal string Helper { get; }
        internal string Ready { get; }
        internal Gate(GitFixture git)
        {
            _hold = git.Output("hold checkout discovery"); _log = git.Output("checkout initialization argv.jsonl");
            Ready = git.Output("checkout discovery ready.json"); Helper = git.Output("gated checkout git.py");
            File.WriteAllText(_hold, "hold\n");
            File.WriteAllText(Helper, "#!/usr/bin/python3\nimport json, os, pathlib, sys, time\n"
                + "with open(" + JsonSerializer.Serialize(_log) + ", 'a', encoding='utf-8') as log:\n    log.write(json.dumps(sys.argv[1:]) + '\\n')\n"
                + "if '--absolute-git-dir' in sys.argv and pathlib.Path(" + JsonSerializer.Serialize(_hold) + ").exists():\n"
                + "    ready = pathlib.Path(" + JsonSerializer.Serialize(Ready) + ")\n"
                + "    temporary = ready.with_name(ready.name + '.' + str(os.getpid()) + '.tmp')\n"
                + "    ticks = pathlib.Path('/proc/self/stat').read_text().rsplit(')', 1)[1].split()[19]\n"
                + "    temporary.write_text(json.dumps({'pid':os.getpid(),'start_ticks':ticks}))\n    temporary.replace(ready)\n"
                + "    while pathlib.Path(" + JsonSerializer.Serialize(_hold) + ").exists(): time.sleep(0.01)\n"
                + "os.execv('/usr/bin/git', ['/usr/bin/git'] + sys.argv[1:])\n");
            File.SetUnixFileMode(Helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        internal void Release() { if (File.Exists(_hold)) { File.Delete(_hold); } }
        internal string[][] Commands() => File.Exists(_log)
            ? File.ReadAllLines(_log).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray() : [];
    }

    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static Rect Bounds(Control control, Visual relative) => new(control.TranslatePoint(default, relative)!.Value, control.Bounds.Size);
    private static Point Center(Control control, Visual relative) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), relative)!.Value;
    private static async Task PublishFrameAsync(TopLevel window)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        window.UpdateLayout();
        // Headless resize notifications are posted at Send priority. Let them and
        // the preceding UI layout work finish before committing the current size.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background, timeout.Token);
        Size clientSize = window.ClientSize; double renderScaling = window.RenderScaling;
        var visual = ElementComposition.GetElementVisual(window);
        Assert.That(visual, Is.Not.Null, "The mapped window must have a composition visual.");
        var batch = visual!.Compositor.RequestCompositionBatchCommitAsync();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await batch.Rendered.WaitAsync(timeout.Token);
        // Rendered may complete inside the render loop; leave that stack before
        // inspecting the actual frame and performing the test's unique hit-test.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background, timeout.Token);
        using var frame = window.GetLastRenderedFrame();
        Assert.That(frame, Is.Not.Null, "The completed composition batch must leave an actual rendered frame.");
        Assert.That(window.ClientSize, Is.EqualTo(clientSize)); Assert.That(window.RenderScaling, Is.EqualTo(renderScaling));
        Assert.That(frame!.PixelSize, Is.EqualTo(PixelSize.FromSize(window.ClientSize, window.RenderScaling)));
        Assert.That(window.IsMeasureValid && window.IsArrangeValid, Is.True, "The published window layout must be valid.");
    }
    private static string Diagnostic(Window window, Control intended, Visual? hit)
    {
        string Describe(Visual visual) => visual.GetType().Name + "#" + (visual as Control)?.Name + " " + visual.Bounds;
        return "Checkout target=" + Bounds(intended, window) + "; client=" + window.ClientSize
            + "; actual hit=" + string.Join(" -> ", hit?.GetSelfAndVisualAncestors().Select(Describe) ?? []);
    }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "Timed out while waiting for the actual checkout initialization or closure.");
    }
}

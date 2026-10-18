using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Avatars;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable, SupportedOSPlatform("linux")]
public sealed class AvatarWindowLifetimeTests
{
    [AvaloniaTest]
    public async Task TheFirstGrandchildScopeInheritsThroughAMappedOwnerThatHasNeverRequestedItsScopeAndFamilyCloseDrainsIt()
    {
        using OfflineAvatarFixture f = new(); AvatarProviderGate gate = new(); f.Provider = gate.Read;
        await using AvatarService service = f.Create(); AvatarApplicationLifetime application = new(service);
        WindowRecord parent = new(new Window { Name = "AvatarParent", Width = 420, Height = 240, Content = new TextBlock { Text = "Parent" } });
        WindowRecord child = new(new Window { Name = "AvatarChild", Width = 360, Height = 200, Content = new TextBlock { Text = "No avatar or scope read here" } });
        WindowRecord grandchild = new(new Window { Name = "AvatarGrandchild", Width = 300, Height = 180, Content = new TextBlock { Text = "Grandchild" } });
        application.BindWindow(parent.Window);
        try
        {
            parent.Window.Show(); await PublishFrameAsync(parent.Window);
            child.Window.Show(parent.Window); await PublishFrameAsync(child.Window);
            grandchild.Window.Show(child.Window); await PublishFrameAsync(grandchild.Window);
            Assert.That(child.Window.Owner, Is.SameAs(parent.Window)); Assert.That(grandchild.Window.Owner, Is.SameAs(child.Window));
            Assert.That(parent.Opened && child.Opened && grandchild.Opened, Is.True);
            Assert.That(child.Lifetime.HasBinding, Is.True, "Reading HasBinding must not require a prior Scope read on C.");
            // The very first Scope access belongs to G; neither P nor C has an avatar presenter.
            AvatarOwnerScope g = grandchild.Lifetime.Scope ?? throw new AssertionException("G must inherit the application through C.");
            AvatarOwnerScope c = child.Lifetime.Scope ?? throw new AssertionException("G admission must create its real C owner scope.");
            AvatarOwnerScope p = parent.Lifetime.Scope ?? throw new AssertionException("C must belong to P.");
            Assert.That(g.Service, Is.SameAs(service)); Assert.That(c.Service, Is.SameAs(service)); Assert.That(p.Service, Is.SameAs(service));
            Assert.That(g.Parent, Is.SameAs(c)); Assert.That(c.Parent, Is.SameAs(p)); Assert.That(p.Parent, Is.Null);
            Task<AvatarReadResult> raw = g.ReadAsync(OfflineAvatars.Request()); await gate.Entered.Task.WaitAsync(OfflineAvatars.Limit);
            Task familyClose = parent.Lifetime.BeginClose();
            Assert.That(parent.Lifetime.IsClosing && child.Lifetime.IsClosing && grandchild.Lifetime.IsClosing, Is.True);
            Assert.CatchAsync<OperationCanceledException>(async () => await raw.WaitAsync(OfflineAvatars.Limit));
            await g.ConsumerCompletion.WaitAsync(OfflineAvatars.Limit);
            Assert.That(familyClose.IsCompleted, Is.False, "Closing the real window family still owns G's ignoring-token raw provider.");
            gate.Release.TrySetResult(); await familyClose.WaitAsync(OfflineAvatars.Limit);
            Assert.That(p.IsClosed && c.IsClosed && g.IsClosed, Is.True); Assert.That(f.Memory.Count, Is.Zero);
        }
        finally
        {
            gate.Release.TrySetResult(); await CloseWindowsAsync(grandchild, child, parent);
            await application.ShutdownAsync().WaitAsync(OfflineAvatars.Limit);
        }
    }

    [AvaloniaTest]
    public async Task BoundGourceStartBeforeMappingAndWhileMinimizedCannotFallBackToLegacyOrExportAvatars()
    {
        using GitFixture git = await GitFixture.CreateAsync(); using OfflineAvatarFixture f = new(); int sharedReads = 0;
        f.Provider = (_, _, _) => { Interlocked.Increment(ref sharedReads); return Task.FromResult<AvatarPng?>(OfflineAvatars.Image()); };
        await using AvatarService service = f.Create(); AvatarApplicationLifetime application = new(service);
        GitCommandLog commands = new(git); CountingBorrowedProvider injected = new();
        GourceDialog dialog = new(commands.Repository, new Dictionary<string, string?>
        { ["executable"] = "/usr/bin/true", ["arguments"] = "--user-image-dir '$(AVATARS)'" }, injected);
        WindowRecord window = new(dialog); application.BindWindow(dialog);
        try
        {
            await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.IsBusy, Is.False);
            Assert.That(window.Lifetime.HasBinding, Is.True); Assert.That(window.Lifetime.Scope, Is.Null);
            Assert.That(window.Lifetime.IsMapped, Is.False); Assert.That(window.Opened, Is.False);
            string before = commands.Content;
            RouteStartAndAssertNoWork("before mapping");
            dialog.Show(); await PublishFrameAsync(dialog);
            Assert.That(window.Opened, Is.True); Assert.That(window.Lifetime.IsMapped, Is.True);
            dialog.WindowState = WindowState.Minimized;
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.That(window.Lifetime.HasBinding, Is.True); Assert.That(window.Lifetime.IsMapped, Is.False); Assert.That(window.Lifetime.Scope, Is.Null);
            RouteStartAndAssertNoWork("while minimized");

            void RouteStartAndAssertNoWork(string phase)
            {
                Button start = dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "StartGource");
                Assert.That(start.IsEnabled, Is.True);
                // A hidden/minimized window has no pointer target. Use its real registered button route.
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.That(dialog.IsBusy, Is.False, phase); Assert.That(dialog.LastError, Is.Null, phase);
                Assert.That(dialog.LastLaunch, Is.Null, phase); Assert.That(injected.Calls, Is.Zero, phase); Assert.That(sharedReads, Is.Zero, phase);
                Assert.That(commands.Content, Is.EqualTo(before), "The export's real Git author-log read must not start " + phase + ".");
                Lazy<IGourceAvatarProvider> legacy = (Lazy<IGourceAvatarProvider>)(typeof(GourceDialog)
                    .GetField("_legacyAvatarProvider", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dialog)
                    ?? throw new AssertionException("The dormant legacy-provider boundary must be observable."));
                Assert.That(legacy.IsValueCreated, Is.False, phase);
                Assert.That(f.Disk.Reads, Is.Zero); Assert.That(f.Disk.Stages, Is.Zero); Assert.That(f.Handler.Requests, Is.Empty);
            }
        }
        finally
        {
            await CloseWindowsAsync(window); await application.ShutdownAsync().WaitAsync(OfflineAvatars.Limit);
            Assert.That(injected.Disposed, Is.False, "The injected legacy provider remains borrowed even when admission is refused.");
        }
    }

    [AvaloniaTest]
    public async Task ARealBoundBlameCloseWaitsForItsVisibleWidgetsIgnoringProviderAndCannotCommitItsLateImage()
    {
        using GitFixture git = await GitFixture.CreateAsync(); using OfflineAvatarFixture f = new(); AvatarProviderGate gate = new(); f.Provider = gate.Read;
        await using AvatarService service = f.Create(new() { Provider = AvatarProvider.Default, ShowCommitInfo = false });
        AvatarApplicationLifetime application = new(service); IGourceAvatarProvider previous = BlameView.DefaultAvatarProvider;
        BlameView.DefaultAvatarProvider = BlameView.OriginalDefaultAvatarProvider;
        BlameDialog dialog = new(git.Repository, "file.txt"); WindowRecord window = new(dialog); application.BindWindow(dialog);
        try
        {
            byte[] original = await File.ReadAllBytesAsync(git.File); dialog.Show(); await PublishFrameAsync(dialog);
            BlameView view = Field<BlameView>(dialog, "BlameView");
            TaskCompletionSource visibleRead = OfflineAvatars.Signal(); view.AvatarReadStarted += _ => visibleRead.TrySetResult();
            await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.IsBusy, Is.False); Assert.That(view.ShowAvatar, Is.True);
            await PublishFrameAsync(dialog);
            AvatarImageControl widget = view.GetVisualDescendants().OfType<AvatarImageControl>().Single(control => control.Name == "BlameAuthorAvatar");
            Assert.That(widget.Role, Is.EqualTo(AvatarImageRole.Blame)); Assert.That(widget.IsEffectivelyVisible, Is.True);
            Assert.That(widget.Bounds.Width > 0 && widget.Bounds.Height > 0, Is.True); Assert.That(widget.Focusable, Is.False);
            Assert.That(new Rect(default, dialog.ClientSize).Intersects(Bounds(widget, dialog)), Is.True);
            // Only the mapped, arranged widget may admit the provider; never force a scope read to make this pass.
            await visibleRead.Task.WaitAsync(OfflineAvatars.Limit); await gate.Entered.Task.WaitAsync(OfflineAvatars.Limit);
            AvatarOwnerScope scope = window.Lifetime.Scope ?? throw new AssertionException("The visible shared widget must have its real window owner.");
            Assert.That(scope.Service, Is.SameAs(service)); Assert.That(gate.Calls, Is.EqualTo(1)); Assert.That(f.Disk.Commits, Is.Zero);
            await PublishFrameAsync(dialog); PointerOnce(dialog, Field<Button>(dialog, "CancelBlame"));
            Assert.That(window.Lifetime.IsClosing, Is.True); Assert.That(scope.IsClosing, Is.True);
            await scope.ConsumerCompletion.WaitAsync(OfflineAvatars.Limit);
            Assert.That(gate.Token.IsCancellationRequested, Is.True); Assert.That(dialog.IsVisible, Is.True);
            Assert.That(window.Closed.Task.IsCompleted, Is.False, "Closed must not precede the actual ignoring-token provider drain.");
            Assert.That(window.Lifetime.BeginClose().IsCompleted, Is.False); Assert.That(f.Disk.Commits, Is.Zero);
            gate.Release.TrySetResult(); await window.Closed.Task.WaitAsync(OfflineAvatars.Limit);
            await window.Lifetime.BeginClose().WaitAsync(OfflineAvatars.Limit);
            Assert.That(dialog.IsVisible, Is.False); Assert.That(scope.IsClosed, Is.True); Assert.That(f.Disk.Commits, Is.Zero); Assert.That(f.Memory.Count, Is.Zero);
            Assert.That(await File.ReadAllBytesAsync(git.File), Is.EqualTo(original)); Assert.That((await git.Repository.GetStatusAsync()).Changes, Is.Empty);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
        }
        finally
        {
            gate.Release.TrySetResult();
            try { await CloseWindowsAsync(window); await application.ShutdownAsync().WaitAsync(OfflineAvatars.Limit); }
            finally { BlameView.DefaultAvatarProvider = previous; }
        }
    }

    private sealed class WindowRecord
    {
        internal Window Window { get; } internal AvatarWindowLifetime Lifetime { get; }
        internal bool Opened { get; private set; } internal TaskCompletionSource Closed { get; } = OfflineAvatars.Signal();
        internal WindowRecord(Window window)
        {
            Window = window; Lifetime = AvatarWindowLifetime.For(window);
            window.Opened += (_, _) => Opened = true; window.Closed += (_, _) => Closed.TrySetResult();
        }
    }

    private sealed class CountingBorrowedProvider : IGourceAvatarProvider, IDisposable
    {
        private int _calls; internal int Calls => Volatile.Read(ref _calls); internal bool Disposed { get; private set; }
        public Task<byte[]?> GetAvatarPngAsync(string email, string authorName, int imageSize, CancellationToken cancellationToken)
        { Interlocked.Increment(ref _calls); return Task.FromResult<byte[]?>(OfflineAvatars.Bytes); }
        public void Dispose() => Disposed = true;
    }

    private sealed class GitCommandLog
    {
        private readonly string _log; internal GitRepository Repository { get; }
        internal string Content => File.Exists(_log) ? File.ReadAllText(_log) : "";
        internal GitCommandLog(GitFixture git)
        {
            _log = git.Output("avatar window gource argv.jsonl"); string helper = git.Output("record avatar export git.py");
            File.WriteAllText(helper, "#!/usr/bin/python3\nimport json,os,sys\nwith open(" + JsonSerializer.Serialize(_log)
                + ", 'a', encoding='utf-8') as log: log.write(json.dumps(sys.argv[1:]) + '\\n')\n"
                + "os.execv('/usr/bin/git', ['/usr/bin/git'] + sys.argv[1:])\n");
            File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Repository = new(git.Repository.Path, new GitRunner(helper));
        }
    }

    private static async Task CloseWindowsAsync(params WindowRecord[] windows)
    {
        foreach (WindowRecord window in windows)
        {
            await window.Lifetime.BeginClose().WaitAsync(OfflineAvatars.Limit); window.Window.Close();
            if (window.Opened) { await window.Closed.Task.WaitAsync(OfflineAvatars.Limit); }
        }
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static Rect Bounds(Control control, Visual relative) => new(control.TranslatePoint(default, relative)!.Value, control.Bounds.Size);
    private static void PointerOnce(Window window, Button target)
    {
        Assert.That(target.IsEffectivelyVisible && target.IsEffectivelyEnabled, Is.True);
        Point point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        Visual? hit = window.InputHitTest(point) as Visual;
        Assert.That(hit?.GetSelfAndVisualAncestors().Contains(target), Is.True, "The first published hit must target Close without a retry or focus repair.");
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }
    private static async Task PublishFrameAsync(TopLevel window)
    {
        using CancellationTokenSource timeout = new(OfflineAvatars.Limit); window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background, timeout.Token);
        Size client = window.ClientSize; double scale = window.RenderScaling;
        var visual = ElementComposition.GetElementVisual(window); Assert.That(visual, Is.Not.Null);
        var batch = visual!.Compositor.RequestCompositionBatchCommitAsync(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await batch.Rendered.WaitAsync(timeout.Token);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background, timeout.Token);
        using var frame = window.GetLastRenderedFrame(); Assert.That(frame, Is.Not.Null);
        Assert.That(window.ClientSize, Is.EqualTo(client)); Assert.That(window.RenderScaling, Is.EqualTo(scale));
        Assert.That(frame!.PixelSize, Is.EqualTo(PixelSize.FromSize(client, scale)));
        Assert.That(window.IsMeasureValid && window.IsArrangeValid, Is.True);
    }
}

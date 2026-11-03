using System.Collections.Concurrent;
using System.Net;
using Avalonia.Media;
using GitCommands;
using GitExtensions.Linux.Desktop.Avatars;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[Category("CrossPlatform")]
public sealed class AvatarServiceTests
{
    [Test]
    public async Task ReadyReadsShareOneWorkerAndClosingOneOwnerKeepsTheOtherReadAlive()
    {
        using OfflineAvatarFixture f = new(); AvatarProviderGate gate = new(); f.Provider = gate.Read;
        await using AvatarService service = f.Create(); AvatarOwnerScope a = service.CreateOwnerScope("A"), b = service.CreateOwnerScope("B");
        Task<AvatarReadResult> first = a.ReadAsync(OfflineAvatars.Request()), second = b.ReadAsync(OfflineAvatars.Request());
        try
        {
            await gate.Entered.Task.WaitAsync(OfflineAvatars.Limit);
            AvatarOwnerClose close = a.BeginClose(); await close.Completion.WaitAsync(OfflineAvatars.Limit);
            Assert.CatchAsync<OperationCanceledException>(async () => await first.WaitAsync(OfflineAvatars.Limit));
            Assert.That(a.IsClosed, Is.True); Assert.That(b.IsActive, Is.True); Assert.That(second.IsCompleted, Is.False);
            Assert.That(gate.Token.IsCancellationRequested, Is.False); Assert.That(gate.Calls, Is.EqualTo(1));
            gate.Release.TrySetResult(); AvatarReadResult result = await second.WaitAsync(OfflineAvatars.Limit);
            Assert.That(result.Outcome, Is.EqualTo(AvatarReadOutcome.Ready)); Assert.That(result.Image!.CopyBytes(), Is.EqualTo(OfflineAvatars.Bytes));
            Assert.That((await b.ReadAsync(OfflineAvatars.Request())).Image!.CopyBytes(), Is.EqualTo(OfflineAvatars.Bytes));
            Assert.That(gate.Calls, Is.EqualTo(1));
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Test]
    public async Task CancelingAConsumerRetainsItsOwnersCacheFillEdgeAndItsNextReadJoinsTheSameWorker()
    {
        using OfflineAvatarFixture f = new(); AvatarProviderGate gate = new(); f.Provider = gate.Read;
        await using AvatarService service = f.Create(); AvatarOwnerScope owner = service.CreateOwnerScope("window");
        using CancellationTokenSource caller = new(); Task<AvatarReadResult> canceled = owner.ReadAsync(OfflineAvatars.Request(), caller.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(OfflineAvatars.Limit); caller.Cancel();
            OperationCanceledException error = Assert.CatchAsync<OperationCanceledException>(async () => await canceled.WaitAsync(OfflineAvatars.Limit))!;
            Assert.That(error.CancellationToken, Is.EqualTo(caller.Token)); await owner.ConsumerCompletion.WaitAsync(OfflineAvatars.Limit);
            Assert.That(gate.Token.IsCancellationRequested, Is.False); Assert.That(owner.IsActive, Is.True);
            Task<AvatarReadResult> next = owner.ReadAsync(OfflineAvatars.Request()); Assert.That(next.IsCompleted, Is.False);
            gate.Release.TrySetResult(); Assert.That((await next.WaitAsync(OfflineAvatars.Limit)).Outcome, Is.EqualTo(AvatarReadOutcome.Ready));
            Assert.That(gate.Calls, Is.EqualTo(1));
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Test]
    public async Task ConsumerCompletionIsNotAWorkerDrainWhenTheLastOwnerClosesAnIgnoringProvider()
    {
        using OfflineAvatarFixture f = new(); AvatarProviderGate gate = new(); f.Provider = gate.Read;
        await using AvatarService service = f.Create(); AvatarOwnerScope owner = service.CreateOwnerScope("window");
        Task<AvatarReadResult> read = owner.ReadAsync(OfflineAvatars.Request());
        try
        {
            await gate.Entered.Task.WaitAsync(OfflineAvatars.Limit); AvatarOwnerClose close = owner.BeginClose();
            Assert.That(owner.IsClosing, Is.True); Assert.That(owner.IsActive, Is.False);
            Assert.Throws<OperationCanceledException>(() => owner.ReadAsync(OfflineAvatars.Request("late@example.invalid")));
            Assert.Throws<OperationCanceledException>(() => service.CreateOwnerScope("late child", owner));
            Assert.CatchAsync<OperationCanceledException>(async () => await read.WaitAsync(OfflineAvatars.Limit));
            await owner.ConsumerCompletion.WaitAsync(OfflineAvatars.Limit);
            Assert.That(close.Completion.IsCompleted, Is.False, "The real provider still owns its gated raw work.");
            Assert.That(owner.CloseCompletion, Is.SameAs(close.Completion)); Assert.That(owner.BeginClose().Completion, Is.SameAs(close.Completion));
            gate.Release.TrySetResult(); await close.Completion.WaitAsync(OfflineAvatars.Limit);
            Assert.That(owner.IsClosed, Is.True); Assert.That(f.Disk.Commits, Is.Zero); Assert.That(f.Memory.Count, Is.Zero);
        }
        finally { gate.Release.TrySetResult(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AParentCloseStillDrainsAGrandchildAfterAnEarlierNonRecursiveChildClose(bool childAlreadyFinished)
    {
        using OfflineAvatarFixture f = new(); AvatarProviderGate childGate = new(), grandGate = new();
        f.Provider = (p, r, t) => r.Email.StartsWith("child", StringComparison.Ordinal) ? childGate.Read(p, r, t) : grandGate.Read(p, r, t);
        await using AvatarService service = f.Create(); AvatarOwnerScope parent = service.CreateOwnerScope("P");
        AvatarOwnerScope child = service.CreateOwnerScope("C", parent), grandchild = service.CreateOwnerScope("G", child);
        Task<AvatarReadResult> childRead = child.ReadAsync(OfflineAvatars.Request("child@example.invalid"));
        Task<AvatarReadResult> grandRead = grandchild.ReadAsync(OfflineAvatars.Request("grandchild@example.invalid"));
        try
        {
            await Task.WhenAll(childGate.Entered.Task, grandGate.Entered.Task).WaitAsync(OfflineAvatars.Limit);
            AvatarOwnerClose firstChildClose = child.BeginClose(false);
            if (childAlreadyFinished) { childGate.Release.TrySetResult(); await firstChildClose.Completion.WaitAsync(OfflineAvatars.Limit); }
            Assert.That(grandchild.IsActive, Is.True); AvatarOwnerClose family = parent.BeginClose(true);
            Assert.That(grandchild.IsActive, Is.False); Assert.That(family.Completion.IsCompleted, Is.False);
            Assert.That(child.BeginClose().Completion, Is.SameAs(firstChildClose.Completion));
            childGate.Release.TrySetResult(); await firstChildClose.Completion.WaitAsync(OfflineAvatars.Limit);
            Assert.That(family.Completion.IsCompleted, Is.False, "A completed C(false) must not hide G from P(true).");
            grandGate.Release.TrySetResult(); await family.Completion.WaitAsync(OfflineAvatars.Limit);
            Assert.CatchAsync<OperationCanceledException>(async () => await childRead.WaitAsync(OfflineAvatars.Limit));
            Assert.CatchAsync<OperationCanceledException>(async () => await grandRead.WaitAsync(OfflineAvatars.Limit));
            Assert.That(parent.IsClosed && child.IsClosed && grandchild.IsClosed, Is.True); Assert.That(f.Memory.Count, Is.Zero);
        }
        finally { childGate.Release.TrySetResult(); grandGate.Release.TrySetResult(); }
    }

    [Test]
    public async Task CloseReturnsWithoutRunningABlockingCancellationCallbackOnItsCallerButCompletionStillDrainsIt()
    {
        using OfflineAvatarFixture f = new(); TaskCompletionSource entered = OfflineAvatars.Signal(), callbackEntered = OfflineAvatars.Signal();
        TaskCompletionSource releaseRaw = OfflineAvatars.Signal(), releaseCallback = OfflineAvatars.Signal(); CancellationTokenRegistration registration = default;
        f.Provider = async (_, _, token) =>
        {
            registration = token.Register(() => { callbackEntered.TrySetResult(); releaseCallback.Task.GetAwaiter().GetResult(); });
            entered.TrySetResult(); await releaseRaw.Task; return OfflineAvatars.Image();
        };
        await using AvatarService service = f.Create(); AvatarOwnerScope owner = service.CreateOwnerScope("window");
        Task<AvatarReadResult> read = owner.ReadAsync(OfflineAvatars.Request());
        try
        {
            await entered.Task.WaitAsync(OfflineAvatars.Limit);
            Task<AvatarOwnerClose> invocation = Task.Run(() => owner.BeginClose());
            await callbackEntered.Task.WaitAsync(OfflineAvatars.Limit); AvatarOwnerClose close = await invocation.WaitAsync(OfflineAvatars.Limit);
            releaseRaw.TrySetResult(); Assert.CatchAsync<OperationCanceledException>(async () => await read.WaitAsync(OfflineAvatars.Limit));
            await owner.ConsumerCompletion.WaitAsync(OfflineAvatars.Limit);
            Assert.That(close.Completion.IsCompleted, Is.False, "A returned raw provider does not drain an unfinished cancellation callback.");
            releaseCallback.TrySetResult(); await close.Completion.WaitAsync(OfflineAvatars.Limit); Assert.That(owner.IsClosed, Is.True);
        }
        finally { releaseRaw.TrySetResult(); releaseCallback.TrySetResult(); registration.Dispose(); }
    }

    [Test]
    public async Task ClearRetiresAStagedWorkerBeforeCommitAndWaitsForItsRealPreparedWriteDisposal()
    {
        using OfflineAvatarFixture f = new(); f.Provider = (_, _, _) => Task.FromResult<AvatarPng?>(OfflineAvatars.Image());
        TaskCompletionSource staged = OfflineAvatars.Signal(), releaseStage = OfflineAvatars.Signal(); RecordingAvatarWrite? write = null;
        f.Disk.Stage = async (key, image, _) => { write = f.Disk.NewWrite(key, image); staged.TrySetResult(); await releaseStage.Task; return write; };
        await using AvatarService service = f.Create(new() { Provider = AvatarProvider.Default }); AvatarOwnerScope owner = service.CreateOwnerScope("window");
        Task<AvatarReadResult> old = owner.ReadAsync(OfflineAvatars.Request());
        try
        {
            await staged.Task.WaitAsync(OfflineAvatars.Limit); long epoch = service.Snapshot.Epoch; AvatarMutation clear = service.ClearCache();
            RecordingAvatarWrite prepared = write ?? throw new AssertionException("The staging gate must own a prepared write.");
            Assert.That(clear.Epoch, Is.EqualTo(epoch + 1)); Assert.That(service.Snapshot.IsReady, Is.False);
            Assert.That(clear.Completion.IsCompleted, Is.False); Assert.That(f.Disk.Clears, Is.Zero); Assert.That(prepared.Committed, Is.False);
            releaseStage.TrySetResult(); Assert.That((await old.WaitAsync(OfflineAvatars.Limit)).Outcome, Is.EqualTo(AvatarReadOutcome.GenerationRetired));
            await clear.Completion.WaitAsync(OfflineAvatars.Limit);
            Assert.That(prepared.Disposed, Is.True); Assert.That(prepared.Committed, Is.False); Assert.That(f.Disk.Commits, Is.Zero);
            Assert.That(f.Disk.Clears, Is.EqualTo(1)); Assert.That(f.Memory.Count, Is.Zero);
            AvatarReadResult fresh = await owner.ReadAsync(OfflineAvatars.Request()).WaitAsync(OfflineAvatars.Limit);
            Assert.That(fresh.Epoch, Is.EqualTo(clear.Epoch)); Assert.That(fresh.Outcome, Is.EqualTo(AvatarReadOutcome.Ready)); Assert.That(f.Disk.Commits, Is.EqualTo(1));
        }
        finally { releaseStage.TrySetResult(); }
    }

    [Test]
    public async Task CancelingAMutationWaitDoesNotAbandonApplicationOwnedDeletionOrReadiness()
    {
        using OfflineAvatarFixture f = new(); TaskCompletionSource clearing = OfflineAvatars.Signal(), release = OfflineAvatars.Signal();
        f.Disk.Clear = async _ => { clearing.TrySetResult(); await release.Task; };
        await using AvatarService service = f.Create(); service.Changed += _ => throw new InvalidOperationException("observer failure");
        try
        {
            AvatarMutation clear = service.ClearCache(); await clearing.Task.WaitAsync(OfflineAvatars.Limit);
            using CancellationTokenSource waiter = new(); Task wait = clear.WaitAsync(waiter.Token); waiter.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await wait.WaitAsync(OfflineAvatars.Limit));
            Assert.That(clear.Completion.IsCompleted, Is.False); Assert.That(service.Snapshot.IsReady, Is.False);
            release.TrySetResult(); await clear.Completion.WaitAsync(OfflineAvatars.Limit);
            Assert.That((await service.WaitForReadyAsync()).Epoch, Is.EqualTo(clear.Epoch)); Assert.That(f.Disk.Clears, Is.EqualTo(1));
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task FailedClearDisablesPersistentReuseUntilASuccessfulLaterClearWithoutResurrectingOldFiles()
    {
        using OfflineAvatarFixture f = new(); int providers = 0; f.Provider = (_, _, _) => { Interlocked.Increment(ref providers); return Task.FromResult<AvatarPng?>(OfflineAvatars.Image()); };
        f.Disk.Read = (_, _) => Task.FromResult<AvatarDiskEntry?>(new(OfflineAvatars.Image(), f.Clock.GetLocalNow().DateTime));
        f.Disk.Clear = _ => Task.FromException(new IOException("cannot delete old avatar"));
        await using AvatarService service = f.Create(new() { Provider = AvatarProvider.Default }); AvatarOwnerScope owner = service.CreateOwnerScope("window");
        AvatarMutation failed = service.ClearCache(); Assert.CatchAsync<IOException>(async () => await failed.Completion.WaitAsync(OfflineAvatars.Limit));
        Assert.That(service.Snapshot.IsReady, Is.True); Assert.That(service.Snapshot.MutationError, Is.Not.Null);
        Assert.That((await owner.ReadAsync(OfflineAvatars.Request()).WaitAsync(OfflineAvatars.Limit)).Outcome, Is.EqualTo(AvatarReadOutcome.Ready));
        Assert.That(f.Disk.Reads, Is.Zero); Assert.That(f.Disk.Stages, Is.Zero); Assert.That(providers, Is.EqualTo(1));
        f.Disk.Clear = null; await service.ClearCache().Completion.WaitAsync(OfflineAvatars.Limit);
        Assert.That(service.Snapshot.MutationError, Is.Null);
        await owner.ReadAsync(OfflineAvatars.Request("another@example.invalid")).WaitAsync(OfflineAvatars.Limit);
        Assert.That(f.Disk.Reads, Is.EqualTo(1)); Assert.That(providers, Is.EqualTo(1), "Only the successful new generation may reuse disk.");
    }

    [Test]
    public async Task User80RemainsAnUncachedNotFoundAndTheNextReadCanRetryTheRealProvider()
    {
        using OfflineAvatarFixture f = new(); int calls = 0; f.Provider = (_, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult<AvatarPng?>(null); };
        await using AvatarService service = f.Create(new() { Provider = AvatarProvider.Default }); AvatarOwnerScope owner = service.CreateOwnerScope("window");
        AvatarReadResult first = await owner.ReadAsync(OfflineAvatars.Request()).WaitAsync(OfflineAvatars.Limit);
        AvatarReadResult second = await owner.ReadAsync(OfflineAvatars.Request()).WaitAsync(OfflineAvatars.Limit);
        Assert.That(first.Outcome, Is.EqualTo(AvatarReadOutcome.NotFound)); Assert.That(second.Outcome, Is.EqualTo(AvatarReadOutcome.NotFound));
        Assert.That(first.Image, Is.Not.Null); Assert.That(second.Image, Is.Not.Null); Assert.That(calls, Is.EqualTo(2));
        Assert.That(f.UserFallbackCalls, Is.EqualTo(2)); Assert.That(f.Memory.Count, Is.Zero); Assert.That(f.Disk.Stages, Is.Zero);
    }

    [Test]
    public async Task DefaultFinalInitialsAreCachedInMemoryAndOnDiskAndReusedByANewRuntime()
    {
        using OfflineAvatarFixture f = new(); AvatarPreferences preferences = new() { Provider = AvatarProvider.Default };
        await using (AvatarService service = f.Create(preferences))
        {
            AvatarOwnerScope owner = service.CreateOwnerScope("first");
            Assert.That((await owner.ReadAsync(OfflineAvatars.Request())).Outcome, Is.EqualTo(AvatarReadOutcome.Ready));
            Assert.That((await owner.ReadAsync(OfflineAvatars.Request())).Outcome, Is.EqualTo(AvatarReadOutcome.Ready));
            Assert.That(f.Handler.Requests.Count, Is.EqualTo(2)); Assert.That(f.InitialsCalls, Is.EqualTo(1)); Assert.That(f.Disk.Commits, Is.EqualTo(1));
        }
        f.Memory = new(); await using AvatarService reopened = f.Create(preferences); AvatarOwnerScope next = reopened.CreateOwnerScope("second");
        Assert.That((await next.ReadAsync(OfflineAvatars.Request())).Outcome, Is.EqualTo(AvatarReadOutcome.Ready));
        Assert.That(f.Handler.Requests.Count, Is.EqualTo(2)); Assert.That(f.InitialsCalls, Is.EqualTo(1)); Assert.That(f.Disk.Commits, Is.EqualTo(1));
    }

    [Test]
    public async Task ProductionGourceBindingReturnsIndependentBytesAndBorrowedDependenciesSurviveShutdown()
    {
        using OfflineAvatarFixture f = new(); f.Provider = (_, _, _) => Task.FromResult<AvatarPng?>(OfflineAvatars.Image());
        await using AvatarService service = f.Create(); AvatarOwnerScope owner = service.CreateOwnerScope("gource");
        IGourceAvatarProvider binding = AvatarProviderBinding.ForProduction(owner, () => OfflineAvatars.Identity);
        byte[] first = (await binding.GetAvatarPngAsync(" Raw@Example.invalid ", "Literal Ω", 90, default))!;
        first[0] = 0; byte[] next = (await binding.GetAvatarPngAsync(" Raw@Example.invalid ", "Literal Ω", 90, default))!;
        Assert.That(next, Is.EqualTo(OfflineAvatars.Bytes)); Assert.That(first, Is.Not.SameAs(next));
        await service.ShutdownAsync().WaitAsync(OfflineAvatars.Limit); Assert.That(f.Disk.Disposed, Is.False); Assert.That(f.Codec.Disposed, Is.False);
        Assert.That(f.Handler.Disposed, Is.False); using HttpResponseMessage response = await f.Client.GetAsync("https://offline.invalid/still-borrowed");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task BorrowedGourceReceivesExactArgumentsAndTokenAndCloseWaitsItsIgnoringRawTaskWithoutDisposingIt()
    {
        using OfflineAvatarFixture f = new(); await using AvatarService service = f.Create(); AvatarOwnerScope owner = service.CreateOwnerScope("gource");
        BorrowedAvatarGate provider = new(); IGourceAvatarProvider binding = AvatarProviderBinding.ForBorrowed(owner, provider);
        using CancellationTokenSource caller = new(); Task<byte[]?> read = binding.GetAvatarPngAsync("  Raw@Example.invalid ", "[Literal] Ω", -7, caller.Token);
        try
        {
            await provider.Entered.Task.WaitAsync(OfflineAvatars.Limit);
            Assert.That(provider.Arguments, Is.EqualTo(("  Raw@Example.invalid ", "[Literal] Ω", -7, caller.Token)));
            caller.Cancel(); Assert.CatchAsync<OperationCanceledException>(async () => await read.WaitAsync(OfflineAvatars.Limit));
            await owner.ConsumerCompletion.WaitAsync(OfflineAvatars.Limit); AvatarOwnerClose close = owner.BeginClose();
            Assert.That(close.Completion.IsCompleted, Is.False); Assert.That(provider.Disposed, Is.False);
            provider.Release.TrySetResult(); await close.Completion.WaitAsync(OfflineAvatars.Limit); Assert.That(provider.Disposed, Is.False);
        }
        finally { provider.Release.TrySetResult(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ASynchronouslyCompletedBorrowedReadStillObservesCancellationBeforePublishingBytes(bool closeOwner)
    {
        using OfflineAvatarFixture f = new(); await using AvatarService service = f.Create();
        AvatarOwnerScope owner = service.CreateOwnerScope("gource"); using CancellationTokenSource caller = new();
        SynchronousBorrowedProvider provider = new(() => { if (closeOwner) { owner.BeginClose(); } else { caller.Cancel(); } });
        IGourceAvatarProvider binding = AvatarProviderBinding.ForBorrowed(owner, provider);
        try
        {
            Task<byte[]?> read = binding.GetAvatarPngAsync("literal@example.invalid", "Literal", 90, caller.Token);
            OperationCanceledException error = Assert.CatchAsync<OperationCanceledException>(async () => await read.WaitAsync(OfflineAvatars.Limit))!;
            Assert.That(error.CancellationToken, Is.EqualTo(closeOwner ? owner.LifetimeToken : caller.Token));
            await owner.ConsumerCompletion.WaitAsync(OfflineAvatars.Limit);
            Assert.That(provider.Calls, Is.EqualTo(1)); Assert.That(provider.Disposed, Is.False);
        }
        finally { await owner.BeginClose().Completion.WaitAsync(OfflineAvatars.Limit); }
        Assert.That(owner.IsClosed, Is.True); Assert.That(provider.Disposed, Is.False);
    }

    [Test]
    public async Task ClosingTheLastOwnerDrainsAnIgnoringHttpResponseStreamBeforeReportingClosed()
    {
        using OfflineAvatarFixture f = new(); GatedAvatarStream stream = new();
        f.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        await using AvatarService service = f.Create(new() { Provider = AvatarProvider.Default }); AvatarOwnerScope owner = service.CreateOwnerScope("window");
        Task<AvatarReadResult> read = owner.ReadAsync(OfflineAvatars.Request());
        try
        {
            await stream.Entered.Task.WaitAsync(OfflineAvatars.Limit); AvatarOwnerClose close = owner.BeginClose();
            Assert.CatchAsync<OperationCanceledException>(async () => await read.WaitAsync(OfflineAvatars.Limit));
            await owner.ConsumerCompletion.WaitAsync(OfflineAvatars.Limit);
            Assert.That(close.Completion.IsCompleted, Is.False); Assert.That(stream.Disposed, Is.False);
            stream.Release.TrySetResult(); await close.Completion.WaitAsync(OfflineAvatars.Limit);
            Assert.That(stream.Disposed, Is.True); Assert.That(owner.IsClosed, Is.True);
            Assert.That(f.Disk.Commits, Is.Zero); Assert.That(f.Memory.Count, Is.Zero);
        }
        finally { stream.Release.TrySetResult(); }
    }

    [Test]
    public async Task ShutdownSealsAdmissionAndWaitsRawWorkBeforeDisposingItsOwnedRuntime()
    {
        using OfflineAvatarFixture f = new(); AvatarProviderGate gate = new(); f.Provider = gate.Read;
        AvatarService service = f.Create(); AvatarOwnerScope owner = service.CreateOwnerScope("window");
        Task<AvatarReadResult> read = owner.ReadAsync(OfflineAvatars.Request());
        try
        {
            await gate.Entered.Task.WaitAsync(OfflineAvatars.Limit); Task shutdown = service.ShutdownAsync();
            Assert.That(service.Snapshot.IsStopping, Is.True); Assert.That(service.ShutdownAsync(), Is.SameAs(shutdown));
            Assert.Throws<ObjectDisposedException>(() => service.CreateOwnerScope("late")); Assert.Throws<ObjectDisposedException>(() => service.ClearCache());
            Assert.That(shutdown.IsCompleted, Is.False); Assert.CatchAsync<OperationCanceledException>(async () => await read.WaitAsync(OfflineAvatars.Limit));
            gate.Release.TrySetResult(); await shutdown.WaitAsync(OfflineAvatars.Limit); Assert.That(owner.IsClosed, Is.True);
        }
        finally { gate.Release.TrySetResult(); await service.ShutdownAsync().WaitAsync(OfflineAvatars.Limit); }
    }

    private sealed class SynchronousBorrowedProvider(Action beforeReturn) : IGourceAvatarProvider, IDisposable
    {
        internal int Calls { get; private set; } internal bool Disposed { get; private set; }
        public Task<byte[]?> GetAvatarPngAsync(string email, string name, int size, CancellationToken token)
        { Calls++; beforeReturn(); return Task.FromResult<byte[]?>(OfflineAvatars.Bytes); }
        public void Dispose() => Disposed = true;
    }

    private sealed class BorrowedAvatarGate : IGourceAvatarProvider, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = OfflineAvatars.Signal(); internal TaskCompletionSource Release { get; } = OfflineAvatars.Signal();
        internal (string, string, int, CancellationToken) Arguments { get; private set; }
        internal bool Disposed { get; private set; }
        public async Task<byte[]?> GetAvatarPngAsync(string email, string name, int size, CancellationToken token)
        { Arguments = (email, name, size, token); Entered.TrySetResult(); await Release.Task; return OfflineAvatars.Bytes; }
        public void Dispose() => Disposed = true;
    }

    private sealed class GatedAvatarStream : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = OfflineAvatars.Signal(); internal TaskCompletionSource Release { get; } = OfflineAvatars.Signal();
        internal bool Disposed { get; private set; }
        internal GatedAvatarStream() : base(OfflineAvatars.Bytes, writable: false) { }
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task; return await base.ReadAsync(buffer, CancellationToken.None); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}

internal static class OfflineAvatars
{
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    // Inline 1x1 PNG: no asset loader, font resolver, native decoder or application is needed.
    internal static byte[] Bytes => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    internal static AvatarRenderIdentity Identity => new("offline-font-never-resolved", "en-US", FontWeight.Normal, FontStyle.Normal);
    internal static AvatarRequest Request(string email = "test@example.com", string? name = "Test Person", int pixels = 90) => new(email, name, pixels, Identity);
    internal static AvatarPng Image() => new(Bytes);
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class AvatarProviderGate
{
    private int _calls;
    internal int Calls => Volatile.Read(ref _calls); internal CancellationToken Token { get; private set; }
    internal TaskCompletionSource Entered { get; } = OfflineAvatars.Signal(); internal TaskCompletionSource Release { get; } = OfflineAvatars.Signal();
    internal async Task<AvatarPng?> Read(AvatarPreferences preferences, AvatarRequest request, CancellationToken token)
    { Interlocked.Increment(ref _calls); Token = token; Entered.TrySetResult(); await Release.Task; return OfflineAvatars.Image(); }
}

internal sealed class OfflineAvatarFixture : IDisposable
{
    internal FixedAvatarTime Clock { get; } = new(); internal RecordingAvatarDisk Disk { get; }
    internal AvatarMemoryCache Memory { get; set; } = new(); internal OfflineAvatarCodec Codec { get; } = new();
    internal OfflineAvatarHttp Handler { get; } = new(); internal HttpClient Client { get; }
    internal Func<AvatarPreferences, AvatarRequest, CancellationToken, Task<AvatarPng?>>? Provider { get; set; }
    internal Func<AvatarRequest, CancellationToken, Task<AvatarPng?>>? Initials { get; set; }
    internal Func<AvatarRequest, CancellationToken, Task<AvatarPng?>>? UserFallback { get; set; }
    private int _initials, _userFallback;
    internal int InitialsCalls => Volatile.Read(ref _initials); internal int UserFallbackCalls => Volatile.Read(ref _userFallback);
    internal OfflineAvatarFixture() { Disk = new(Clock); Client = new(Handler); }
    internal AvatarService Create(AvatarPreferences? preferences = null) => new(preferences ?? new(), new()
    {
        HttpClient = Client, TimeProvider = Clock, DiskCache = Disk, MemoryCache = Memory, Codec = Codec, ReadProviderAsync = Provider,
        RenderInitialsAsync = (r, t) => { Interlocked.Increment(ref _initials); return Initials?.Invoke(r, t) ?? Task.FromResult<AvatarPng?>(OfflineAvatars.Image()); },
        RenderUserFallbackAsync = (r, t) => { Interlocked.Increment(ref _userFallback); return UserFallback?.Invoke(r, t) ?? Task.FromResult<AvatarPng?>(OfflineAvatars.Image()); }
    });
    public void Dispose() { Client.Dispose(); Disk.Dispose(); Codec.Dispose(); }
}

internal sealed class FixedAvatarTime : TimeProvider
{
    private long _ticks = new DateTime(2030, 5, 20, 12, 0, 0, DateTimeKind.Utc).Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
}

internal sealed class OfflineAvatarCodec : IAvatarImageCodec, IDisposable
{
    internal ConcurrentQueue<(byte[] Bytes, int Pixels, bool RejectIdenticon)> Calls { get; } = new();
    internal Func<byte[], int, bool, AvatarPng?>? DecodeImage { get; set; }
    internal bool Disposed { get; private set; }
    public AvatarPng? Decode(byte[] content, int pixels, bool rejectGitHubIdenticon, CancellationToken token)
    { token.ThrowIfCancellationRequested(); Calls.Enqueue((content.ToArray(), pixels, rejectGitHubIdenticon)); return DecodeImage is null ? OfflineAvatars.Image() : DecodeImage(content, pixels, rejectGitHubIdenticon); }
    public void Dispose() => Disposed = true;
}

internal sealed class OfflineAvatarHttp : HttpMessageHandler
{
    internal ConcurrentQueue<Uri> Requests { get; } = new(); internal bool Disposed { get; private set; }
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    { Requests.Enqueue(request.RequestUri!); return Respond?.Invoke(request, token) ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    internal static HttpResponseMessage Image(byte[]? bytes = null) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? OfflineAvatars.Bytes) };
}

internal sealed class RecordingAvatarDisk(FixedAvatarTime clock) : IAvatarDiskCache, IDisposable
{
    internal ConcurrentDictionary<string, AvatarDiskEntry> Entries { get; } = new(StringComparer.Ordinal);
    internal ConcurrentQueue<string> Events { get; } = new(); private int _reads, _stages, _commits, _clears, _expired;
    internal int Reads => Volatile.Read(ref _reads); internal int Stages => Volatile.Read(ref _stages);
    internal int Commits => Volatile.Read(ref _commits); internal int Clears => Volatile.Read(ref _clears); internal int Expired => Volatile.Read(ref _expired);
    internal bool Disposed { get; private set; }
    internal Func<string, CancellationToken, Task<AvatarDiskEntry?>>? Read { get; set; }
    internal Func<string, AvatarPng, CancellationToken, Task<IAvatarCacheWrite>>? Stage { get; set; }
    internal Func<CancellationToken, Task>? Clear { get; set; }
    public Task<AvatarDiskEntry?> ReadAsync(string key, CancellationToken token)
    { Interlocked.Increment(ref _reads); Events.Enqueue("read:" + key); return Read?.Invoke(key, token) ?? Task.FromResult<AvatarDiskEntry?>(Entries.GetValueOrDefault(key)); }
    public Task<IAvatarCacheWrite> StageAsync(string key, AvatarPng image, CancellationToken token)
    { Interlocked.Increment(ref _stages); Events.Enqueue("stage:" + key); return Stage?.Invoke(key, image, token) ?? Task.FromResult<IAvatarCacheWrite>(NewWrite(key, image)); }
    internal RecordingAvatarWrite NewWrite(string key, AvatarPng image) => new(() =>
    { Interlocked.Increment(ref _commits); Events.Enqueue("commit:" + key); Entries[key] = new(image, clock.GetLocalNow().DateTime); });
    public async Task ClearAsync(CancellationToken token)
    { Interlocked.Increment(ref _clears); Events.Enqueue("clear"); if (Clear is not null) { await Clear(token); } Entries.Clear(); }
    public void DeleteExpired(string key) { Interlocked.Increment(ref _expired); Events.Enqueue("expired:" + key); Entries.TryRemove(key, out _); }
    public void Dispose() => Disposed = true;
}

internal sealed class RecordingAvatarWrite(Action commit) : IAvatarCacheWrite
{
    internal bool Committed { get; private set; } internal bool Disposed { get; private set; }
    public void Commit() { commit(); Committed = true; }
    public void Dispose() => Disposed = true;
}

using GitCommands;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop.Avatars;

/// <summary>Owns real workers and mutations; a canceled consumer wait is never a worker-completion signal.</summary>
internal sealed class AvatarService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly AvatarImageTransport _transport;
    private readonly IAvatarImageCodec _codec;
    private readonly IAvatarDiskCache _disk;
    private readonly AvatarMemoryCache _memory;
    private readonly TimeProvider _clock;
    private readonly Func<AvatarRequest, CancellationToken, Task<AvatarPng?>> _initials;
    private readonly Func<AvatarRequest, CancellationToken, Task<AvatarPng?>> _userFallback;
    private readonly Func<AvatarPreferences, AvatarRequest, CancellationToken, Task<AvatarPng?>>? _provider;
    private readonly HashSet<AvatarOwnerScope> _scopes = [];
    private readonly HashSet<PendingAvatar> _active = [];
    private readonly Dictionary<(long Epoch, string Key), PendingAvatar> _pending = [];
    private readonly HashSet<Task> _mutations = [];
    private readonly int _capturedDiskDays;
    private Generation _generation;
    private Task _mutationTail = Task.CompletedTask;
    private TaskCompletionSource? _shutdown;
    private bool _stopping;

    internal event Action<AvatarPolicySnapshot>? Changed;
    internal AvatarPolicySnapshot Snapshot { get { lock (_gate) { return SnapshotLocked(); } } }

    internal AvatarService(AvatarPreferences firstSaved, AvatarServiceDependencies? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(firstSaved);
        dependencies ??= new();
        _capturedDiskDays = firstSaved.CacheDays < 1 ? 30 : firstSaved.CacheDays;
        _clock = dependencies.TimeProvider;
        _transport = new(dependencies.HttpClient);
        _codec = dependencies.Codec ?? new AvatarImageCodec();
        _disk = dependencies.DiskCache ?? new AvatarDiskCache();
        _memory = dependencies.MemoryCache ?? new AvatarMemoryCache();
        _initials = dependencies.RenderInitialsAsync ?? AvatarImageCodec.RenderInitialsAsync;
        _userFallback = dependencies.RenderUserFallbackAsync ?? ((request, token) => AvatarImageCodec.RenderUserFallbackAsync(request, _codec, token));
        _provider = dependencies.ReadProviderAsync;
        _generation = CreateGeneration(1, firstSaved);
        _generation.IsReady = true; _generation.Ready.TrySetResult();
    }

    internal sealed class Generation(long epoch, AvatarPreferences preferences, AvatarProviderChain chain)
    {
        internal readonly long Epoch = epoch;
        internal AvatarPreferences Preferences = preferences;
        internal readonly string PolicyKey = AvatarRequest.PolicyKey(preferences);
        internal readonly AvatarProviderChain Chain = chain;
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsReady, Retired;
        internal bool DiskAvailable = true;
        internal Exception? Error;
    }

    internal sealed class PendingAvatar
    {
        internal readonly Generation _generation;
        internal readonly string Key;
        internal readonly AvatarRequest Request;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly HashSet<AvatarOwnerScope> Owners = [];
        internal Task<AvatarReadResult> Task = null!;
        internal Task CancellationCompletion = System.Threading.Tasks.Task.CompletedTask;
        internal readonly TaskCompletionSource Terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Completion => Terminal.Task;
        internal long Epoch => _generation.Epoch;
        internal PendingAvatar(Generation generation, string key, AvatarRequest request)
        { _generation = generation; Key = key; Request = request; }
    }

    private Generation CreateGeneration(long epoch, AvatarPreferences preferences)
        => new(epoch, preferences, new(preferences, _transport, _codec, _initials));
    private AvatarPolicySnapshot SnapshotLocked()
        => new(_generation.Epoch, _generation.Preferences, _capturedDiskDays,
            _generation.IsReady && !_generation.Retired, _stopping, _generation.Error);
    private void CheckScopeLocked(AvatarOwnerScope scope)
    {
        if (!ReferenceEquals(scope.Service, this)) { throw new ArgumentException("Avatar scope belongs to another runtime.", nameof(scope)); }
        if (!scope.IsActive) { throw new OperationCanceledException(scope.LifetimeToken); }
        ObjectDisposedException.ThrowIf(_stopping, this);
    }
    private void CheckConsumerLocked(AvatarOwnerScope scope, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        if (_stopping || !scope.IsActive) { throw new OperationCanceledException(scope.LifetimeToken); }
        scope.LifetimeToken.ThrowIfCancellationRequested();
    }

    internal AvatarOwnerScope CreateOwnerScope(string debugName, AvatarOwnerScope? parent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(debugName);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (parent is not null) { CheckScopeLocked(parent); }
            AvatarOwnerScope scope = new(this, debugName) { ParentValue = parent };
            parent?.Children.Add(scope); _scopes.Add(scope); return scope;
        }
    }
    internal void SetParent(AvatarOwnerScope scope, AvatarOwnerScope parent)
    {
        lock (_gate)
        {
            CheckScopeLocked(scope); CheckScopeLocked(parent);
            if (scope.Started) { throw new InvalidOperationException("Attach the actual owner before the first avatar read."); }
            for (AvatarOwnerScope? ancestor = parent; ancestor is not null; ancestor = ancestor.ParentValue)
            { if (ReferenceEquals(ancestor, scope)) { throw new ArgumentException("Avatar owner cycle.", nameof(parent)); } }
            scope.ParentValue?.Children.Remove(scope); scope.ParentValue = parent; parent.Children.Add(scope);
        }
    }

    internal Task<AvatarReadResult> ReadAsync(AvatarOwnerScope scope, AvatarRequest request, CancellationToken consumerToken)
    {
        ArgumentNullException.ThrowIfNull(request); consumerToken.ThrowIfCancellationRequested();
        AvatarRequest normalized = request.Normalize();
        lock (_gate)
        {
            CheckScopeLocked(scope); consumerToken.ThrowIfCancellationRequested(); scope.Started = true;
            Generation generation = _generation;
            // A returned ready-generation read already owns its cache-fill edge. Starting only the
            // waiter asynchronously must not leave a scheduling gap in the owner-close contract.
            Task<AvatarReadResult>? admitted = generation.IsReady ? AttachReadLocked(scope, normalized, generation) : null;
            Task<AvatarReadResult> task = Task.Run(() => ReadConsumerAsync(scope, normalized, generation, admitted, consumerToken));
            TrackConsumerLocked(scope, task); return task;
        }
    }
    private void TrackConsumerLocked(AvatarOwnerScope scope, Task task)
    {
        scope.Consumers.Add(task);
        _ = task.ContinueWith(completed =>
        { _ = completed.Exception; lock (_gate) { scope.Consumers.Remove(completed); } },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private Task<AvatarReadResult> AttachReadLocked(AvatarOwnerScope scope, AvatarRequest request, Generation generation)
    {
        string key = request.CacheKey(generation.Preferences);
        if (_memory.TryGet(key, out AvatarPng? cached))
        { return Task.FromResult(new AvatarReadResult(generation.Epoch, AvatarReadOutcome.Ready, cached!.WithEpoch(generation.Epoch))); }
        _pending.TryGetValue((generation.Epoch, key), out PendingAvatar? pending);
        if (pending is not null && (pending.Task.IsCompleted || pending.Cancellation.IsCancellationRequested))
        { _pending.Remove((generation.Epoch, key)); pending = null; }
        if (pending is null)
        {
            pending = new(generation, key, request);
            _pending.Add((generation.Epoch, key), pending); _active.Add(pending);
            PendingAvatar owned = pending;
            owned.Task = Task.Run(() => ReadWorkerAsync(owned));
            _ = owned.Task.ContinueWith(_ => WorkerFinishedAsync(owned), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        pending.Owners.Add(scope); scope.Workers.Add(pending); return pending.Task;
    }
    private async Task<AvatarReadResult> ReadConsumerAsync(AvatarOwnerScope scope, AvatarRequest request,
        Generation generation, Task<AvatarReadResult>? work, CancellationToken caller)
    {
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(caller, scope.LifetimeToken);
        try
        {
            if (work is null)
            {
                await generation.Ready.Task.WaitAsync(wait.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    CheckConsumerLocked(scope, caller);
                    if (generation.Retired || !ReferenceEquals(_generation, generation)) { return Retired(generation.Epoch); }
                    work = AttachReadLocked(scope, request, generation);
                }
            }
            AvatarReadResult result = await work.WaitAsync(wait.Token).ConfigureAwait(false);
            // CancelAsync callbacks can reach the linked wait after the worker
            // completes. Publication must still observe the owner's close edge.
            lock (_gate) { CheckConsumerLocked(scope, caller); return result; }
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (OperationCanceledException) when (scope.LifetimeToken.IsCancellationRequested) { throw new OperationCanceledException(scope.LifetimeToken); }
    }

    private bool CanPublishLocked(PendingAvatar worker)
        => !_stopping && !worker._generation.Retired && ReferenceEquals(_generation, worker._generation)
            && !worker.Cancellation.IsCancellationRequested && worker.Owners.Count > 0
            && _pending.TryGetValue((worker.Epoch, worker.Key), out PendingAvatar? current) && ReferenceEquals(current, worker);

    private async Task<AvatarReadResult> ReadWorkerAsync(PendingAvatar worker)
    {
        CancellationToken token = worker.Cancellation.Token;
        Generation generation = worker._generation;
        bool diskHit = false; AvatarPng? image = null;
        try
        {
            lock (_gate) { if (!CanPublishLocked(worker)) { return Retired(worker.Epoch); } }
            token.ThrowIfCancellationRequested();
            if (generation.Chain.PerformsIo && generation.DiskAvailable)
            {
                try
                {
                    AvatarDiskEntry? disk = await _disk.ReadAsync(worker.Key, token).ConfigureAwait(false);
                    if (disk is not null)
                    {
                        if (generation.Preferences.Provider == AvatarProvider.None
                            || disk.LastWriteTime >= _clock.GetLocalNow().DateTime.AddDays(-_capturedDiskDays))
                        { image = _codec.Decode(disk.Image.CopyBytes(), worker.Request.PixelSize, false, token); diskHit = image is not null; }
                        else
                        {
                            lock (_gate)
                            {
                                if (!CanPublishLocked(worker)) { return Retired(worker.Epoch); }
                                _disk.DeleteExpired(worker.Key);
                            }
                        }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            image ??= _provider is null ? await generation.Chain.ReadAsync(worker.Request, token).ConfigureAwait(false)
                : await _provider(generation.Preferences, worker.Request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (image is null) { return await UserFallbackAsync(worker, token).ConfigureAwait(false); }
            if (!diskHit && generation.Chain.PerformsIo && generation.DiskAvailable)
            {
                try
                {
                    using IAvatarCacheWrite staged = await _disk.StageAsync(worker.Key, image, token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (!CanPublishLocked(worker)) { return Retired(worker.Epoch); }
                        // The check and final rename share the retirement gate; cancellation alone cannot fence a rename.
                        staged.Commit();
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            lock (_gate)
            {
                if (!CanPublishLocked(worker)) { return Retired(worker.Epoch); }
                AvatarPng published = image.WithEpoch(worker.Epoch); _memory.Add(worker.Key, published);
                return new(worker.Epoch, AvatarReadOutcome.Ready, published);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return Retired(worker.Epoch); }
        catch (Exception)
        {
            if (token.IsCancellationRequested) { return Retired(worker.Epoch); }
            try { return await UserFallbackAsync(worker, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return Retired(worker.Epoch); }
            catch (Exception)
            { lock (_gate) { return CanPublishLocked(worker) ? new(worker.Epoch, AvatarReadOutcome.NotFound, null) : Retired(worker.Epoch); } }
        }
    }
    private async Task<AvatarReadResult> UserFallbackAsync(PendingAvatar worker, CancellationToken token)
    {
        AvatarPng? image = await _userFallback(worker.Request, token).ConfigureAwait(false);
        lock (_gate)
        {
            if (!CanPublishLocked(worker)) { return Retired(worker.Epoch); }
            // Windows User80 is outside both caches, so absence remains retryable by another read.
            return new(worker.Epoch, AvatarReadOutcome.NotFound, image?.WithEpoch(worker.Epoch));
        }
    }
    private static AvatarReadResult Retired(long epoch) => new(epoch, AvatarReadOutcome.GenerationRetired, null);
    private async Task WorkerFinishedAsync(PendingAvatar worker)
    {
        _ = worker.Task.Exception;
        Task cancellation;
        lock (_gate) { cancellation = worker.CancellationCompletion; }
        await DrainAsync([cancellation]).ConfigureAwait(false);
        lock (_gate)
        {
            if (_pending.TryGetValue((worker.Epoch, worker.Key), out PendingAvatar? current) && ReferenceEquals(current, worker))
            { _pending.Remove((worker.Epoch, worker.Key)); }
            worker.Cancellation.Dispose();
            _active.Remove(worker);
            foreach (AvatarOwnerScope owner in worker.Owners) { owner.Workers.Remove(worker); }
            worker.Owners.Clear();
        }
        worker.Terminal.TrySetResult();
    }

    internal async Task<AvatarPolicySnapshot> WaitForReadyAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Generation generation;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopping, this); generation = _generation;
                if (generation.IsReady && !generation.Retired) { return SnapshotLocked(); }
            }
            await generation.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    internal AvatarMutation ApplySavedPolicy(AvatarPreferences preferences)
    { ArgumentNullException.ThrowIfNull(preferences); return RegisterMutation(preferences, false); }
    internal AvatarMutation ClearCache() => RegisterMutation(null, true);

    private AvatarMutation RegisterMutation(AvatarPreferences? preferences, bool forceClear)
    {
        Generation generation; PendingAvatar[] workers; Task previous, completion;
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AvatarPolicySnapshot snapshot; bool clear;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            AvatarPreferences saved = preferences ?? _generation.Preferences;
            clear = forceClear || AvatarRequest.PolicyKey(saved) != _generation.PolicyKey;
            if (clear)
            {
                _generation.Retired = true; _generation.Ready.TrySetResult();
                _generation = CreateGeneration(_generation.Epoch + 1, saved); _memory.Clear();
                workers = _active.ToArray(); _pending.Clear();
                foreach (PendingAvatar worker in workers) { CancelWorkerLocked(worker); }
            }
            else { _generation.Preferences = saved; workers = []; }
            generation = _generation; previous = _mutationTail;
            completion = Task.Run(() => MutateAsync(previous, start.Task, generation, workers, clear));
            _mutationTail = completion; _mutations.Add(completion);
            _ = completion.ContinueWith(task => { _ = task.Exception; lock (_gate) { _mutations.Remove(task); } },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            snapshot = SnapshotLocked();
        }
        Notify(snapshot); start.TrySetResult();
        return new(generation.Epoch, completion);
    }
    private async Task MutateAsync(Task previous, Task start, Generation generation, PendingAvatar[] workers, bool clear)
    {
        await start.ConfigureAwait(false); await DrainAsync([previous]).ConfigureAwait(false);
        await DrainAsync(workers.Select(worker => worker.Completion)).ConfigureAwait(false);
        Exception? error = null;
        if (clear)
        {
            try { await _disk.ClearAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception failure) { error = failure; }
        }
        AvatarPolicySnapshot? snapshot = null;
        lock (_gate)
        {
            if (clear) { generation.DiskAvailable = error is null; generation.Error = error; }
            generation.IsReady = true;
            generation.Ready.TrySetResult();
            if (!_stopping && ReferenceEquals(_generation, generation)) { snapshot = SnapshotLocked(); }
        }
        if (snapshot is not null) { Notify(snapshot); }
        if (error is not null) { throw new IOException("The shared avatar cache could not be cleared; persistent reuse is disabled until a successful clear.", error); }
    }
    private void Notify(AvatarPolicySnapshot snapshot)
    {
        if (Changed is not { } changed) { return; }
        foreach (Action<AvatarPolicySnapshot> listener in changed.GetInvocationList().Cast<Action<AvatarPolicySnapshot>>())
        { try { listener(snapshot); } catch (Exception) { /* An observer cannot abandon an Application-owned mutation. */ } }
    }

    internal Task ConsumerCompletion(AvatarOwnerScope scope)
    { lock (_gate) { return DrainAsync(scope.Consumers.ToArray()); } }
    internal Task CloseCompletion(AvatarOwnerScope scope)
    { lock (_gate) { return scope.CloseHandle?.Completion ?? Task.CompletedTask; } }
    internal AvatarOwnerClose BeginClose(AvatarOwnerScope scope, bool includeDescendants)
    {
        List<(AvatarOwnerScope Scope, Task[] Tasks)> drains = [];
        AvatarOwnerClose result;
        lock (_gate)
        {
            if (!ReferenceEquals(scope.Service, this)) { throw new ArgumentException("Foreign avatar scope.", nameof(scope)); }
            if (scope.CloseHandle is not null) { return scope.CloseHandle; }
            List<AvatarOwnerScope> selected = [];
            void Select(AvatarOwnerScope item)
            { selected.Add(item); if (includeDescendants) { foreach (AvatarOwnerScope child in item.Children.ToArray()) { Select(child); } } }
            Select(scope);
            AvatarOwnerScope[] fresh = selected.Where(item => item.CloseHandle is null).ToArray();
            foreach (AvatarOwnerScope item in fresh)
            {
                item.State = 1; item.CloseSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
                item.CloseHandle = new(item.CloseSource.Task);
            }
            Dictionary<AvatarOwnerScope, List<Task>> owned = fresh.ToDictionary(item => item, _ => new List<Task>());
            foreach (AvatarOwnerScope item in fresh)
            {
                foreach (PendingAvatar worker in item.Workers.ToArray())
                {
                    worker.Owners.Remove(item); item.Workers.Remove(worker);
                    if (worker.Owners.Count == 0)
                    {
                        if (_pending.TryGetValue((worker.Epoch, worker.Key), out PendingAvatar? current) && ReferenceEquals(current, worker))
                        { _pending.Remove((worker.Epoch, worker.Key)); }
                        CancelWorkerLocked(worker); owned[item].Add(worker.Completion);
                    }
                }
            }
            foreach (AvatarOwnerScope item in fresh)
            {
                bool DescendsFrom(AvatarOwnerScope child)
                {
                    for (AvatarOwnerScope? ancestor = child.ParentValue; ancestor is not null; ancestor = ancestor.ParentValue)
                    { if (ReferenceEquals(ancestor, item)) { return true; } }
                    return false;
                }
                // An intermediate scope may already have an intentionally non-recursive close handle.
                // The new family close must still own every selected descendant, not just that handle.
                IEnumerable<Task> children = includeDescendants
                    ? selected.Where(child => !ReferenceEquals(child, item) && child.CloseHandle is not null && DescendsFrom(child))
                        .Select(child => child.CloseHandle!.Completion) : [];
                drains.Add((item, item.Consumers.Concat(item.Borrowed).Concat(owned[item]).Concat(children).ToArray()));
            }
            result = scope.CloseHandle!;
        }
        foreach (var drain in drains)
        { _ = FinishCloseAsync(drain.Scope, drain.Tasks.Append(CancelAsync(drain.Scope.Cancellation)).ToArray()); }
        return result;
    }
    private async Task FinishCloseAsync(AvatarOwnerScope scope, Task[] pending)
    {
        await DrainAsync(pending).ConfigureAwait(false);
        lock (_gate)
        {
            scope.Cancellation.Dispose();
            scope.State = 2; _scopes.Remove(scope);
            // Keep a closed bridge in the tree while children excluded by close(false) remain alive.
            AvatarOwnerScope? prune = scope;
            while (prune is not null && prune.IsClosed && prune.Children.Count == 0)
            { AvatarOwnerScope? parent = prune.ParentValue; parent?.Children.Remove(prune); prune = parent; }
        }
        scope.CloseSource!.TrySetResult();
    }

    internal Task<byte[]?> ReadBorrowedAsync(AvatarOwnerScope scope, IGourceAvatarProvider provider,
        string email, string authorName, int imageSize, CancellationToken consumerToken)
    {
        consumerToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            CheckScopeLocked(scope); scope.Started = true;
            TaskCompletionSource terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            scope.Borrowed.Add(terminal.Task);
            Task<byte[]?> task = Task.Run(() => BorrowedConsumerAsync(scope, provider, email, authorName, imageSize, consumerToken, terminal));
            TrackConsumerLocked(scope, task); return task;
        }
    }
    private async Task<byte[]?> BorrowedConsumerAsync(AvatarOwnerScope scope, IGourceAvatarProvider provider,
        string email, string name, int pixels, CancellationToken caller, TaskCompletionSource terminal)
    {
        Task<byte[]?> raw;
        try
        {
            lock (_gate) { CheckConsumerLocked(scope, caller); }
            // The injected provider receives the original arguments/token, not a worker or scope token.
            raw = provider.GetAvatarPngAsync(email, name, pixels, caller);
        }
        catch { FinishBorrowed(scope, terminal); throw; }
        _ = ObserveBorrowedAsync(scope, raw, terminal);
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(caller, scope.LifetimeToken);
        try
        {
            byte[]? result = await raw.WaitAsync(wait.Token).ConfigureAwait(false);
            lock (_gate) { CheckConsumerLocked(scope, caller); return result; }
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (OperationCanceledException) when (scope.LifetimeToken.IsCancellationRequested) { throw new OperationCanceledException(scope.LifetimeToken); }
    }
    private async Task ObserveBorrowedAsync(AvatarOwnerScope scope, Task raw, TaskCompletionSource terminal)
    { await DrainAsync([raw]).ConfigureAwait(false); FinishBorrowed(scope, terminal); }
    private void FinishBorrowed(AvatarOwnerScope scope, TaskCompletionSource terminal)
    { lock (_gate) { scope.Borrowed.Remove(terminal.Task); } terminal.TrySetResult(); }

    internal Task ShutdownAsync()
    {
        AvatarOwnerScope[] scopes; PendingAvatar[] workers; Task mutations, completion;
        lock (_gate)
        {
            if (_shutdown is not null) { return _shutdown.Task; }
            _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously); _stopping = true;
            _generation.Retired = true; _generation.Ready.TrySetResult(); _pending.Clear(); _memory.Clear();
            scopes = _scopes.ToArray(); workers = _active.ToArray(); mutations = _mutationTail; completion = _shutdown.Task;
            foreach (PendingAvatar worker in workers) { CancelWorkerLocked(worker); }
        }
        Task[] closes = scopes.Select(scope => BeginClose(scope, true).Completion).ToArray();
        _ = Task.Run(() => FinishShutdownAsync(closes, workers, mutations)); return completion;
    }
    private async Task FinishShutdownAsync(Task[] closes, PendingAvatar[] workers, Task mutations)
    {
        try
        {
            await DrainAsync(closes.Concat(workers.Select(worker => worker.Completion)).Append(mutations)).ConfigureAwait(false);
            _transport.Dispose(); _shutdown!.TrySetResult();
        }
        catch (Exception error) { _shutdown!.TrySetException(error); }
    }
    private static Task CancelAsync(CancellationTokenSource cancellation)
    { try { return cancellation.CancelAsync(); } catch (ObjectDisposedException) { return Task.CompletedTask; } }
    private static void CancelWorkerLocked(PendingAvatar worker)
    {
        // CancelAsync marks cancellation synchronously but does not execute arbitrary provider callbacks on the UI caller.
        // Its actual callback completion is retained alongside the raw worker before its CTS may be disposed.
        if (!worker.Task.IsCompleted && !worker.Cancellation.IsCancellationRequested)
        { worker.CancellationCompletion = CancelAsync(worker.Cancellation); }
    }
    private static async Task DrainAsync(IEnumerable<Task> tasks)
    { try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception) { /* Drain terminal work, including faulted/canceled tasks. */ } }
    public ValueTask DisposeAsync() => new(ShutdownAsync());
}

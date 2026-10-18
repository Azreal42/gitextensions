using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop.Avatars;

internal sealed class AvatarOwnerScope
{
    internal AvatarService Service { get; }
    internal string DebugName { get; }
    internal AvatarOwnerScope? Parent => ParentValue;
    internal bool IsActive => Volatile.Read(ref State) == 0;
    internal bool IsClosing => Volatile.Read(ref State) == 1;
    internal bool IsClosed => Volatile.Read(ref State) == 2;
    internal Task ConsumerCompletion => Service.ConsumerCompletion(this);
    internal Task CloseCompletion => Service.CloseCompletion(this);
    internal readonly CancellationTokenSource Cancellation = new();
    internal readonly CancellationToken LifetimeToken;
    internal readonly HashSet<AvatarOwnerScope> Children = [];
    internal readonly HashSet<AvatarService.PendingAvatar> Workers = [];
    internal readonly HashSet<Task> Consumers = [];
    internal readonly HashSet<Task> Borrowed = [];
    internal AvatarOwnerScope? ParentValue;
    internal AvatarOwnerClose? CloseHandle;
    internal TaskCompletionSource? CloseSource;
    internal int State;
    internal bool Started;

    internal AvatarOwnerScope(AvatarService service, string debugName)
    { Service = service; DebugName = debugName; LifetimeToken = Cancellation.Token; }
    internal void SetParentBeforeFirstRead(AvatarOwnerScope parent) => Service.SetParent(this, parent);
    internal Task<AvatarReadResult> ReadAsync(AvatarRequest request, CancellationToken consumerToken = default)
        => Service.ReadAsync(this, request, consumerToken);
    internal AvatarOwnerClose BeginClose(bool includeDescendants = true) => Service.BeginClose(this, includeDescendants);
    internal Task<byte[]?> ReadBorrowedAsync(IGourceAvatarProvider provider, string email, string authorName,
        int imageSize, CancellationToken token) => Service.ReadBorrowedAsync(this, provider, email, authorName, imageSize, token);
}

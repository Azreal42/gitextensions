using GitCommands;

namespace GitExtensions.Linux.Desktop.Avatars;

internal sealed record AvatarPreferences
{
    public AvatarProvider Provider { get; init; } = AvatarProvider.None;
    public AvatarFallbackType Fallback { get; init; } = AvatarFallbackType.AuthorInitials;
    public string CustomTemplate { get; init; } = "";
    public int CacheDays { get; init; } = 13;
    public bool ShowCommitInfo { get; init; } = true;

    internal static AvatarPreferences From(DesktopPreferences saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return new()
        {
            Provider = saved.AvatarProvider, Fallback = saved.AvatarFallbackType,
            CustomTemplate = saved.CustomAvatarTemplate, CacheDays = saved.AvatarImageCacheDays,
            ShowCommitInfo = saved.ShowAuthorAvatarInCommitInfo
        };
    }
}

internal sealed record AvatarPolicySnapshot(long Epoch, AvatarPreferences Preferences,
    int CapturedDiskCacheDays, bool IsReady, bool IsStopping, Exception? MutationError);

internal sealed record AvatarServiceDependencies
{
    public HttpClient? HttpClient { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public IAvatarDiskCache? DiskCache { get; init; }
    public AvatarMemoryCache? MemoryCache { get; init; }
    public IAvatarImageCodec? Codec { get; init; }
    public Func<AvatarRequest, CancellationToken, Task<AvatarPng?>>? RenderInitialsAsync { get; init; }
    public Func<AvatarRequest, CancellationToken, Task<AvatarPng?>>? RenderUserFallbackAsync { get; init; }
    public Func<AvatarPreferences, AvatarRequest, CancellationToken, Task<AvatarPng?>>? ReadProviderAsync { get; init; }
}

internal enum AvatarReadOutcome { Ready, NotFound, GenerationRetired }
internal sealed record AvatarReadResult(long Epoch, AvatarReadOutcome Outcome, AvatarPng? Image);

internal sealed class AvatarMutation(long epoch, Task completion)
{
    internal long Epoch { get; } = epoch;
    internal Task Completion { get; } = completion;
    internal Task WaitAsync(CancellationToken cancellationToken = default) => Completion.WaitAsync(cancellationToken);
}

internal sealed class AvatarOwnerClose(Task completion)
{
    internal Task Completion { get; } = completion;
}

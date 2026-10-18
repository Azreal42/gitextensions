using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop.Avatars;

/// <summary>An explicitly supplied application runtime; constructing this coordinator reads no profile or cache.</summary>
internal sealed class AvatarApplicationLifetime(AvatarService service)
{
    internal AvatarService Service { get; } = service ?? throw new ArgumentNullException(nameof(service));

    internal AvatarWindowLifetime BindWindow(Window window)
    {
        AvatarWindowLifetime lifetime = AvatarWindowLifetime.For(window);
        lifetime.Bind(this);
        return lifetime;
    }

    // Only the real application exit owns this irreversible drain. The last avatar
    // window can close while other windows and this same captured cache TTL remain alive.
    internal Task ShutdownAsync() => Service.ShutdownAsync();
}

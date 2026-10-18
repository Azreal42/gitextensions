using GitExtensions.Linux.Desktop.Avatars;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private AvatarWindowLifetime? _avatarLifetime;

    // Binding only borrows the runtime created by the real App startup; tests stay unbound.
    private void InitializeAvatarLifetime()
    {
        _avatarLifetime = AvatarWindowLifetime.For(this, ownerManagesClose: true);
        App.BindExistingAvatarRuntime(this);
    }
    private Task CloseAvatarReadersAsync() => _avatarLifetime?.BeginClose() ?? Task.CompletedTask;
}

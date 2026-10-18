using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop.Avatars;

internal static class AvatarProviderBinding
{
    internal static IGourceAvatarProvider ForProduction(AvatarOwnerScope scope, Func<AvatarRenderIdentity>? renderIdentity = null)
    { ArgumentNullException.ThrowIfNull(scope); return new Production(scope, renderIdentity ?? (() => AvatarRenderIdentity.Capture())); }
    internal static IGourceAvatarProvider ForBorrowed(AvatarOwnerScope scope, IGourceAvatarProvider injected)
    { ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(injected); return new Borrowed(scope, injected); }

    private sealed class Production(AvatarOwnerScope scope, Func<AvatarRenderIdentity> identity) : IGourceAvatarProvider
    {
        public async Task<byte[]?> GetAvatarPngAsync(string email, string authorName, int imageSize, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!scope.IsActive) { throw new OperationCanceledException(scope.LifetimeToken); }
            ObjectDisposedException.ThrowIf(scope.Service.Snapshot.IsStopping, scope.Service);
            AvatarRequest request = new(email, authorName, imageSize, identity());
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.LifetimeToken);
            try
            {
                while (true)
                {
                    AvatarReadResult result = await scope.ReadAsync(request, cancellationToken).ConfigureAwait(false);
                    if (result.Outcome != AvatarReadOutcome.GenerationRetired) { return result.Image?.CopyBytes(); }
                    await scope.Service.WaitForReadyAsync(wait.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException(cancellationToken); }
        }
    }
    private sealed class Borrowed(AvatarOwnerScope scope, IGourceAvatarProvider provider) : IGourceAvatarProvider
    {
        public Task<byte[]?> GetAvatarPngAsync(string email, string authorName, int imageSize, CancellationToken cancellationToken)
            => scope.ReadBorrowedAsync(provider, email, authorName, imageSize, cancellationToken);
    }
}

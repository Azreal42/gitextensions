using GitCommands;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop.Avatars;

internal sealed class AvatarProviderChain
{
    private readonly AvatarPreferences _preferences;
    private readonly AvatarImageTransport _transport;
    private readonly IAvatarImageCodec _codec;
    private readonly Func<AvatarRequest, CancellationToken, Task<AvatarPng?>> _initials;
    private readonly List<(AvatarProvider? Tag, Func<AvatarProviderProtocol.TemplateData, string>? Template)> _segments = [];
    internal bool PerformsIo => _preferences.Provider is AvatarProvider.Default or AvatarProvider.Custom
        || AvatarProviderProtocol.FallbackName(_preferences.Fallback) is not null;
    internal AvatarProviderChain(AvatarPreferences preferences, AvatarImageTransport transport, IAvatarImageCodec codec,
        Func<AvatarRequest, CancellationToken, Task<AvatarPng?>> initials)
    {
        _preferences = preferences; _transport = transport; _codec = codec; _initials = initials;
        if (preferences.Provider != AvatarProvider.Custom) { return; }
        foreach (string part in (preferences.CustomTemplate ?? "").Split(';'))
        {
            string segment = part.Trim();
            if (segment.StartsWith('<') && segment.EndsWith('>'))
            {
                if (Enum.TryParse(segment[1..^1], true, out AvatarProvider tag) && tag != AvatarProvider.Custom)
                { _segments.Add((tag, null)); }
            }
            else { _segments.Add((null, AvatarProviderProtocol.CompileTemplate(segment))); }
        }
    }

    internal async Task<AvatarPng?> ReadAsync(AvatarRequest request, CancellationToken token)
    {
        AvatarPng? image = null;
        if (_preferences.Provider == AvatarProvider.Default)
        { image = await DefaultAsync(request, _preferences.Fallback, token).ConfigureAwait(false); }
        else if (_preferences.Provider == AvatarProvider.Custom)
        {
            AvatarProviderProtocol.TemplateData data = new(request);
            foreach (var part in _segments)
            {
                token.ThrowIfCancellationRequested();
                if (part.Tag is AvatarProvider tag)
                {
                    image = tag == AvatarProvider.Default ? await DefaultAsync(request, null, token).ConfigureAwait(false) : null;
                    image ??= await _initials(request, token).ConfigureAwait(false);
                }
                else
                {
                    string raw = part.Template!(data);
                    if (!string.IsNullOrWhiteSpace(raw))
                    { image = await DownloadAsync(new Uri(raw), request.PixelSize, false, token).ConfigureAwait(false); }
                }
                if (image is not null) { break; }
            }
        }
        if (image is not null) { return image; }
        if (AvatarProviderProtocol.FallbackName(_preferences.Fallback) is not null)
        { return await DownloadAsync(AvatarProviderProtocol.Gravatar(request.Email, request.PixelSize, _preferences.Fallback, true), request.PixelSize, false, token).ConfigureAwait(false); }
        return await _initials(request, token).ConfigureAwait(false);
    }

    private async Task<AvatarPng?> DefaultAsync(AvatarRequest request, AvatarFallbackType? fallback, CancellationToken token)
    {
        Uri? github = AvatarProviderProtocol.GitHubAvatar(request.Email, request.PixelSize);
        string? username = AvatarProviderProtocol.GitHubUsername(request.Email);
        if (username?.Contains('[') == true)
        {
            byte[]? profile = await _transport.DownloadAsync(GourceAvatarProtocol.GitHubProfile(username), 64 * 1024, token).ConfigureAwait(false);
            github = AvatarProviderProtocol.ProfileAvatar(profile, request.PixelSize);
        }
        return await DownloadAsync(github, request.PixelSize, true, token).ConfigureAwait(false)
            ?? await DownloadAsync(AvatarProviderProtocol.Gravatar(request.Email, request.PixelSize, fallback), request.PixelSize, false, token).ConfigureAwait(false);
    }
    private async Task<AvatarPng?> DownloadAsync(Uri? uri, int pixels, bool rejectIdenticon, CancellationToken token)
    {
        byte[]? bytes = await _transport.DownloadAsync(uri, AvatarImageTransport.MaximumResponseBytes, token).ConfigureAwait(false);
        return bytes is null ? null : _codec.Decode(bytes, pixels, rejectIdenticon, token);
    }
}

using System.Net.Http;

namespace GitExtensions.Linux.Desktop.Avatars;

internal sealed class AvatarImageTransport : IDisposable
{
    internal const int MaximumResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _downloads = new(10);
    private int _disposed;
    internal AvatarImageTransport(HttpClient? client = null)
    { _ownsClient = client is null; _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) }; }

    internal async Task<byte[]?> DownloadAsync(Uri? uri, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https")) { return null; }
        limit = Math.Min(limit, MaximumResponseBytes);
        await _downloads.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("GitExtensions-Linux/8.0");
                using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit) { return null; }
                using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using MemoryStream output = new(); byte[] buffer = new byte[8192]; int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + read > limit) { return null; }
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested(); return output.ToArray();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
            catch (Exception error) when (error is HttpRequestException or IOException) { return null; }
        }
        finally { _downloads.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        if (_ownsClient) { _client.Dispose(); }
        _downloads.Dispose();
    }
}

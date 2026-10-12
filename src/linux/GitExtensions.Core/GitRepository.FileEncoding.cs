namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public async Task<string> GetFileEncodingNameAsync(CancellationToken cancellationToken = default)
    {
        string? configured = (await GetConfigAsync("^i18n\\.filesencoding$", cancellationToken: cancellationToken).ConfigureAwait(false)).LastOrDefault().Value;
        return GitFileEncodingCodec.GetEncoding(configured).WebName;
    }
}

using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public async Task<string?> GetDefaultCloneSourceAsync(CancellationToken cancellationToken = default)
    {
        GitStatus status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        string? remote = null;
        if (status.Branch is not null and not "(detached)")
        {
            remote = (await GetConfigAsync("^branch\\." + Regex.Escape(status.Branch) + "\\.remote$", cancellationToken: cancellationToken).ConfigureAwait(false)).LastOrDefault().Value;
        }
        IReadOnlyList<GitRemote> remotes = await GetRemotesAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(remote))
        {
            remote = remotes.FirstOrDefault(item => item.Name.Equals("origin", StringComparison.OrdinalIgnoreCase))?.Name ?? remotes.FirstOrDefault()?.Name;
        }
        GitRemote? selected = remotes.FirstOrDefault(item => item.Name == remote);
        return selected?.PushUrls.LastOrDefault(url => !string.IsNullOrEmpty(url)) ?? selected?.FetchUrls.LastOrDefault();
    }
}

using System.Globalization;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Full HEAD message history for FormCommit, filtered by the effective identity when requested.</summary>
    public async Task<IReadOnlyList<string>> GetPreviousCommitMessagesAsync(int count = 6, bool onlyMyMessages = false, CancellationToken cancellationToken = default)
    {
        if (count < 1 || count > 1000) { throw new ArgumentOutOfRangeException(nameof(count)); }
        if (!await HasHeadAsync(cancellationToken).ConfigureAwait(false)) { return []; }
        List<string> arguments = ["log", "--encoding=UTF-8", "-z", "-n", count.ToString(CultureInfo.InvariantCulture), "HEAD", "--pretty=format:%B"];
        if (onlyMyMessages)
        {
            var configuration = await GetConfigAsync("^user\\.(name|email)$", cancellationToken: cancellationToken).ConfigureAwait(false);
            string name = configuration.LastOrDefault(item => item.Key == "user.name").Value ?? "";
            string email = configuration.LastOrDefault(item => item.Key == "user.email").Value ?? "";
            // Git's extended regexp syntax differs from .NET Regex.Escape. Quote its actual metacharacters.
            static string Literal(string value) => string.Concat(value.Select(character => "\\.^$|?*+()[]{}".Contains(character) ? "\\" + character : character.ToString()));
            arguments.Add("--extended-regexp");
            arguments.Add($"--author=^{Literal(name)} <{Literal(email)}>$");
        }
        GitResult result = await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(message => message.TrimEnd('\n'))
            .Where(message => !string.IsNullOrWhiteSpace(message)).ToArray();
    }
}

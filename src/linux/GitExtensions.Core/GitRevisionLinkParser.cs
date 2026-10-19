using System.Text.RegularExpressions;
using GitCommands.ExternalLinks;

namespace GitExtensions.Linux;

/// <summary>Windows revision-link extraction over captured data. No URL launch, IO or publication.</summary>
public sealed class GitRevisionLinkParser
{
    private readonly TimeSpan _regexTimeout;

    /// <param name="regexTimeout">Null preserves Windows' infinite timeout. A host may explicitly bound matches;
    /// timeout exceptions propagate rather than publishing a partial success.</param>
    public GitRevisionLinkParser(TimeSpan? regexTimeout = null)
    {
        _regexTimeout = regexTimeout ?? Regex.InfiniteMatchTimeout;
        if (_regexTimeout != Regex.InfiniteMatchTimeout
            && (_regexTimeout <= TimeSpan.Zero || _regexTimeout.TotalMilliseconds > int.MaxValue - 1))
        { throw new ArgumentOutOfRangeException(nameof(regexTimeout)); }
    }

    public GitRevisionLinkParseResult Parse(GitRevisionLinkContext context,
        IEnumerable<ExternalLinkDefinition> definitions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(definitions);
        cancellationToken.ThrowIfCancellationRequested();
        List<ExternalLinkDefinition> captured = [];
        foreach (ExternalLinkDefinition definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            captured.Add(GitRevisionLinkCodec.Clone(definition));
        }

        List<ExternalLink> links = [];
        List<GitRevisionLinkDiagnostic> diagnostics = [];
        foreach (ExternalLinkDefinition definition in captured.Where(definition => definition.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Regex? search = string.IsNullOrEmpty(definition.SearchPattern) ? null
                : Compile(definition.SearchPattern, nameof(definition.SearchPattern), definition);
            Regex? nested = string.IsNullOrEmpty(definition.NestedSearchPattern) ? null
                : Compile(definition.NestedSearchPattern, nameof(definition.NestedSearchPattern), definition);
            Regex? remote = string.IsNullOrWhiteSpace(definition.RemoteSearchPattern) ? null
                : Compile(definition.RemoteSearchPattern, nameof(definition.RemoteSearchPattern), definition);
            Regex? names = remote is null || string.IsNullOrWhiteSpace(definition.UseRemotesPattern) ? null
                : Compile(definition.UseRemotesPattern, nameof(definition.UseRemotesPattern), definition);

            foreach (Match? remoteMatch in RemoteMatches(definition, remote, names))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Windows uses !IsRemote, which also admits attached tags and other nonremote refs.
                if (definition.SearchInParts.Contains(ExternalLinkDefinition.RevisionPart.LocalBranches))
                {
                    foreach (GitRevisionLinkReference reference in context.AttachedRefs.Where(reference => !reference.IsRemote))
                    { ParsePart(reference.LocalName, remoteMatch); }
                }
                if (definition.SearchInParts.Contains(ExternalLinkDefinition.RevisionPart.RemoteBranches))
                {
                    foreach (GitRevisionLinkReference reference in context.AttachedRefs.Where(reference => reference.IsRemote))
                    { ParsePart(reference.LocalName, remoteMatch); }
                }
                if (definition.SearchInParts.Contains(ExternalLinkDefinition.RevisionPart.Message))
                { ParsePart(context.Message, remoteMatch); }
            }

            void ParsePart(string? part, Match? remoteMatch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (search is null || part is null) { return; }
                foreach (Match match in search.Matches(part))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!match.Success) { continue; }
                    if (string.IsNullOrEmpty(definition.NestedSearchPattern)) { AddFormats(match, remoteMatch); }
                    else if (nested is not null)
                    {
                        foreach (Match inner in nested.Matches(match.Value))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (inner.Success) { AddFormats(inner, remoteMatch); }
                        }
                    }
                }
            }

            void AddFormats(Match match, Match? remoteMatch)
            {
                foreach (ExternalLinkFormat format in definition.LinkFormats)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ExternalLink link = format.Apply(remoteMatch, match, context.Hash);
                    links.Add(link);
                    if (!format.IsValid)
                    { diagnostics.Add(new(GitRevisionLinkDiagnosticKind.InvalidFormat, definition.Name, nameof(definition.LinkFormats), link.Uri)); }
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(links, diagnostics);

        Regex? Compile(string pattern, string property, ExternalLinkDefinition definition)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new Regex(pattern, RegexOptions.Compiled, _regexTimeout); }
            catch (ArgumentException error)
            {
                diagnostics.Add(new(GitRevisionLinkDiagnosticKind.InvalidPattern, definition.Name, property, error.Message));
                return null;
            }
        }

        IEnumerable<Match?> RemoteMatches(ExternalLinkDefinition definition, Regex? remote, Regex? names)
        {
            // Missing OR invalid remote regex is an unconditional null match on Windows.
            if (remote is null) { return [null]; }
            IEnumerable<GitRevisionLinkRemote> matching = context.RawRemotes;
            if (names is not null)
            {
                matching = matching.Where(candidate => names.IsMatch(candidate.Name))
                    .OrderBy(candidate => definition.UseRemotesPattern!.IndexOf(candidate.Name, StringComparison.OrdinalIgnoreCase));
                if (definition.UseOnlyFirstRemote) { matching = matching.Take(1); }
            }
            // A blank/invalid UseRemotesPattern bypasses Take(1), matching Windows' early return.
            List<string> urls = [];
            foreach (GitRevisionLinkRemote candidate in matching)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (definition.RemoteSearchInParts.Contains(ExternalLinkDefinition.RemotePart.URL) && !string.IsNullOrWhiteSpace(candidate.Url))
                { urls.Add(candidate.Url); }
                if (definition.RemoteSearchInParts.Contains(ExternalLinkDefinition.RemotePart.PushURL) && !string.IsNullOrWhiteSpace(candidate.PushUrl))
                { urls.Add(candidate.PushUrl); }
            }
            List<Match?> matches = [];
            foreach (string url in urls.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (Match match in remote.Matches(url))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (match.Success) { matches.Add(match); }
                }
            }
            return matches;
        }
    }
}

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux.Desktop;

public sealed record CommitInformationOptions(
    bool Local = true,
    bool Remote = false,
    bool RemoteIfNoLocal = false,
    bool Tags = true,
    bool Annotated = true,
    bool Derives = true)
{
    [JsonIgnore]
    public bool WantsLocal => Local || RemoteIfNoLocal;
    [JsonIgnore]
    public bool WantsRemote => Remote || RemoteIfNoLocal;
}

/// <summary>The action identity is never reconstructed from the historical Windows display/sort name.</summary>
public sealed record CommitInformationReference<TIdentity>(TIdentity Identity, string LegacySortName);

public sealed record CommitInformationReferenceItem<TIdentity>(TIdentity Identity, string Text);

/// <param name="IsVisible">False for an option disabled or data not loaded; an empty loaded list remains visible.</param>
/// <param name="ShowAll">Whether the presentation needs a Show all link, rather than whether it is expanded.</param>
public sealed record CommitInformationReferenceList<TIdentity>(
    bool IsVisible,
    IReadOnlyList<CommitInformationReferenceItem<TIdentity>> Items,
    bool ShowAll);

/// <summary>Pure Windows CommitInfo ordering, filtering and limits; no Git, UI state or preference writes.</summary>
public static class CommitInformationPresentation
{
    private const string RemotePrefix = "remotes/";
    private const int UntruncatedReferenceLimit = 12;
    private const int TruncatedReferenceCount = 10;
    private static readonly TimeSpan PriorityMatchTimeout = TimeSpan.FromMilliseconds(500);

    /// <param name="currentBranch">The checkout name in the same legacy representation, not a selected reference.</param>
    /// <param name="isDetachedHead">Allows a typed detached-head identity to use a Windows pseudo-name without inventing a Git ref.</param>
    public static CommitInformationReferenceList<TIdentity> Branches<TIdentity>(
        IEnumerable<CommitInformationReference<TIdentity>>? references,
        CommitInformationOptions options,
        string currentBranch,
        bool isDetachedHead,
        string prioritizedBranchNames,
        string prioritizedRemoteNames,
        bool showAll = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(currentBranch);
        if (references is null || (!options.WantsLocal && !options.WantsRemote)) { return Hidden<TIdentity>(); }

        Regex[] localPatterns = Matchers(prioritizedBranchNames, pattern => $"^({pattern})$", nameof(prioritizedBranchNames));
        Regex[] remoteBranchPatterns = Matchers(prioritizedBranchNames, pattern => $"^{RemotePrefix}[^/]+/({pattern})$", nameof(prioritizedBranchNames));
        Regex[] remotePatterns = Matchers(prioritizedRemoteNames, pattern => $"^{RemotePrefix}({pattern})/", nameof(prioritizedRemoteNames));
        CommitInformationReference<TIdentity>[] sorted;
        try
        {
            sorted = references.Select(reference =>
                {
                    ArgumentNullException.ThrowIfNull(reference);
                    ArgumentNullException.ThrowIfNull(reference.LegacySortName);
                    return (Reference: reference, Order: Order(reference.LegacySortName));
                })
                .OrderBy(entry => entry.Order)
                .ThenBy(entry => entry.Reference.LegacySortName, StringComparer.Ordinal)
                .Select(entry => entry.Reference).ToArray();
        }
        catch (RegexMatchTimeoutException error)
        {
            throw new ArgumentException("A reference priority expression exceeded its matching time limit.", error);
        }

        List<CommitInformationReferenceItem<TIdentity>> visible = [];
        bool allowRemote = options.WantsRemote;
        foreach (CommitInformationReference<TIdentity> reference in sorted)
        {
            string name = reference.LegacySortName;
            bool local;
            if (options.WantsLocal && options.WantsRemote)
            {
                local = !name.StartsWith(RemotePrefix);
                if (!local) { name = name[RemotePrefix.Length..]; }
            }
            else { local = !options.WantsRemote; }

            if ((local && options.Local) || (!local && allowRemote))
            {
                visible.Add(new(reference.Identity, name));
            }

            // Windows cuts off subsequent remotes; it does not remove ones already emitted.
            // This also applies when Local is false or Remote is explicitly true.
            if (local && options.RemoteIfNoLocal) { allowRemote = false; }
        }

        return Limit(visible, showAll);

        (int Category, int BranchPriority, int RemotePriority) Order(string name)
        {
            if (isDetachedHead ? IsDetachedName(name) : name == currentBranch) { return (0, 0, 0); }
            if (!name.StartsWith(RemotePrefix))
            {
                int priority = Priority(name, localPatterns);
                return priority < localPatterns.Length ? (1, priority, 0) : (3, 0, 0);
            }

            int branchPriority = Priority(name, remoteBranchPatterns);
            int remotePriority = Priority(name, remotePatterns);
            return branchPriority < remoteBranchPatterns.Length ? (2, branchPriority, remotePriority) : (4, 0, remotePriority);
        }
    }

    /// <summary>Containing tags arrive in Core's native tagger-date order; no tree/name sort is applied here.</summary>
    public static CommitInformationReferenceList<TIdentity> Tags<TIdentity>(
        IEnumerable<CommitInformationReference<TIdentity>>? references,
        CommitInformationOptions options,
        bool showAll = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (references is null || !options.Tags) { return Hidden<TIdentity>(); }
        List<CommitInformationReferenceItem<TIdentity>> items = references.Select(reference =>
            {
                ArgumentNullException.ThrowIfNull(reference);
                ArgumentNullException.ThrowIfNull(reference.LegacySortName);
                return new CommitInformationReferenceItem<TIdentity>(reference.Identity, reference.LegacySortName);
            }).ToList();
        return Limit(items, showAll);
    }

    /// <summary>Windows TagsComparer puts a reference absent from its full-name cache at rank -1.</summary>
    public static int TagRank(string fullName, IReadOnlyDictionary<string, int> orderByFullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        ArgumentNullException.ThrowIfNull(orderByFullName);
        return orderByFullName.TryGetValue(fullName, out int rank) ? rank : -1;
    }

    private static CommitInformationReferenceList<TIdentity> Hidden<TIdentity>() => new(false, [], false);

    private static CommitInformationReferenceList<TIdentity> Limit<TIdentity>(List<CommitInformationReferenceItem<TIdentity>> items, bool showAll)
    {
        bool truncated = !showAll && items.Count > UntruncatedReferenceLimit;
        return new(true, Array.AsReadOnly(truncated ? items.Take(TruncatedReferenceCount).ToArray() : items.ToArray()), truncated);
    }

    private static bool IsDetachedName(string name) => name.StartsWith("(no branch", StringComparison.Ordinal)
        || name.StartsWith("(detached from ", StringComparison.Ordinal)
        || name.StartsWith("(HEAD detached at ", StringComparison.Ordinal);

    private static int Priority(string name, Regex[] patterns)
    {
        for (int index = 0; index < patterns.Length; index++)
        {
            if (patterns[index].IsMatch(name)) { return index; }
        }
        return patterns.Length;
    }

    private static Regex[] Matchers(string expressions, Func<string, string> expression, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(expressions, parameterName);
        try
        {
            // Like the existing Linux preference validator, validate all expressions and bound matching.
            return expressions.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(pattern => new Regex(expression(pattern), RegexOptions.ExplicitCapture, PriorityMatchTimeout)).ToArray();
        }
        catch (ArgumentException error)
        {
            throw new ArgumentException("Invalid reference priority expression: " + error.Message, parameterName, error);
        }
    }
}

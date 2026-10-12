namespace GitExtensions.Linux;

public enum GitPushMode { Branch, AllBranches, Tag, AllTags, Multiple }
public enum GitPushForce { None, Force, ForceWithLease }
public enum GitPushRecursion { None, Check, OnDemand }

/// <summary>A single branch mapping, including an explicitly selected remote deletion.</summary>
public sealed record GitPushAction(string? Source, string Destination, bool Force = false, bool Delete = false);

/// <summary>The push destination is a remote name or URL, independent of the source and destination refs.</summary>
public sealed record GitPushRequest(
    string Destination,
    GitPushMode Mode = GitPushMode.Branch,
    string? Source = null,
    string? DestinationRef = null,
    GitPushForce Force = GitPushForce.None,
    bool SetUpstream = false,
    GitPushRecursion Recursion = GitPushRecursion.None,
    IReadOnlyList<GitPushAction>? Actions = null);

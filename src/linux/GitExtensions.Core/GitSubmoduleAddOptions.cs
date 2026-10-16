namespace GitExtensions.Linux;

/// <summary>Options for Git's native submodule add. Force permits ignored paths and reuse of an existing module repository; it does not discard a checkout.</summary>
public sealed record GitSubmoduleAddOptions(string Source, string Path, string? Branch = null, bool Force = false);

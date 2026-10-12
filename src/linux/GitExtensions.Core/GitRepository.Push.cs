namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Pushes the selected refs without changing the checkout, Git configuration, or hook policy.</summary>
    public Task<GitResult> PushAsync(GitPushRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        string destination = ValidateValue(request.Destination);
        if (!Enum.IsDefined(request.Mode) || !Enum.IsDefined(request.Force) || !Enum.IsDefined(request.Recursion))
        { throw new ArgumentOutOfRangeException(nameof(request), "Choose a supported push mode and options."); }

        List<string> arguments = ["push", "--progress"];
        if (request.Force == GitPushForce.Force) { arguments.Add("--force"); }
        else if (request.Force == GitPushForce.ForceWithLease) { arguments.Add("--force-with-lease"); }
        if (request.SetUpstream) { arguments.Add("--set-upstream"); }
        if (request.Recursion == GitPushRecursion.Check) { arguments.Add("--recurse-submodules=check"); }
        else if (request.Recursion == GitPushRecursion.OnDemand) { arguments.Add("--recurse-submodules=on-demand"); }

        List<string> refspecs = [];
        switch (request.Mode)
        {
            case GitPushMode.Branch:
                RejectPushActions(request);
                if (request.DestinationRef is not null)
                {
                    string source = request.Source is null ? "HEAD" : ValidatePushSource(request.Source, allowRefspec: false);
                    refspecs.Add(source + ":" + PushBranchRef(request.DestinationRef));
                }
                else if (request.Source is not null) { refspecs.Add(ValidatePushSource(request.Source, allowRefspec: true)); }
                break;
            case GitPushMode.Tag:
                RejectPushActions(request);
                if (request.DestinationRef is not null) { throw new ArgumentException("A tag push uses the same tag name on both sides.", nameof(request)); }
                string tag = ValidatePushRef(request.Source ?? throw new ArgumentException("Select a tag to push.", nameof(request)));
                refspecs.Add(tag.StartsWith("refs/tags/", StringComparison.Ordinal) ? tag : "refs/tags/" + tag);
                break;
            case GitPushMode.AllBranches:
            case GitPushMode.AllTags:
                RejectPushActions(request);
                if (request.Source is not null || request.DestinationRef is not null)
                { throw new ArgumentException("Pushing all refs cannot also select a single mapping.", nameof(request)); }
                arguments.Add(request.Mode == GitPushMode.AllBranches ? "--all" : "--tags");
                break;
            case GitPushMode.Multiple:
                if (request.Source is not null || request.DestinationRef is not null)
                { throw new ArgumentException("Use individual mappings when pushing multiple branches.", nameof(request)); }
                GitPushAction[] actions = request.Actions?.ToArray() ?? [];
                if (actions.Length == 0) { throw new ArgumentException("Select at least one push or deletion action.", nameof(request)); }
                foreach (GitPushAction action in actions)
                {
                    ArgumentNullException.ThrowIfNull(action);
                    string remoteRef = PushBranchRef(action.Destination);
                    if (action.Delete)
                    {
                        if (action.Source is not null || action.Force)
                        { throw new ArgumentException("A deletion cannot also push or force a local source.", nameof(request)); }
                        refspecs.Add(":" + remoteRef);
                    }
                    else
                    {
                        string source = ValidatePushSource(action.Source ?? throw new ArgumentException("A push action requires a local source.", nameof(request)), allowRefspec: false);
                        // Multi-branch rows always identify branches. Fully qualified sources avoid
                        // ambiguity when a tag happens to have the same name as the local branch.
                        if (source != "HEAD" && !source.StartsWith("refs/", StringComparison.Ordinal)) { source = "refs/heads/" + source; }
                        refspecs.Add((action.Force ? "+" : "") + source + ":" + remoteRef);
                    }
                }
                break;
        }

        // Keep every user-supplied value after the option terminator and pass each
        // refspec as one argument. Git retains its normal push.default, leases,
        // credentials, remote.push rules, and local/server hooks.
        arguments.AddRange(["--", destination]);
        arguments.AddRange(refspecs);
        return WithMutationAsync(async token =>
        {
            // FormPush formats an existing local branch as refs/heads/name.
            // Do that lookup under the mutation lock, while retaining HEAD,
            // hashes, revision expressions and manually entered refspecs.
            string? source = request.Source;
            if (request.Mode == GitPushMode.Branch && source is not null && source != "HEAD"
                && !source.StartsWith("refs/", StringComparison.Ordinal) && !source.Contains(':') && !source.StartsWith('+')
                && !(source.Length is 40 or 64 && source.All(Uri.IsHexDigit))
                && (await GetReferencesAsync(token).ConfigureAwait(false)).Any(reference => reference.IsBranch && reference.ShortName == source))
            { arguments[^1] = "refs/heads/" + arguments[^1]; }
            return await ExecuteAsync(arguments, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static void RejectPushActions(GitPushRequest request)
    {
        if (request.Actions is { Count: > 0 })
        { throw new ArgumentException("Individual actions require multiple-branch mode.", nameof(request)); }
    }

    private static string ValidatePushSource(string source, bool allowRefspec)
    {
        ValidateValue(source);
        if (!allowRefspec && (source.Contains(':') || source.StartsWith('+')))
        { throw new ArgumentException("Enter one source ref when a destination mapping is selected.", nameof(source)); }
        return source;
    }

    private static string PushBranchRef(string reference)
    {
        reference = ValidatePushRef(reference);
        return reference.StartsWith("refs/", StringComparison.Ordinal) ? reference : "refs/heads/" + reference;
    }

    private static string ValidatePushRef(string reference)
    {
        ValidateValue(reference);
        if (reference.StartsWith('+') || reference.Contains(':') || reference.Contains('*'))
        { throw new ArgumentException("Enter a single reference name.", nameof(reference)); }
        return reference;
    }
}

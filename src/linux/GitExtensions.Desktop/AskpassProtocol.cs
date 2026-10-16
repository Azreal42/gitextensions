namespace GitExtensions.Linux.Desktop;

internal enum AskpassPromptKind { Secret, Username, YesNo, HostVerification, Permission, Notification }
internal enum AskpassDecision { Cancelled, Submit, Yes, No, Dismissed }

internal sealed record AskpassRequest(string Prompt, AskpassPromptKind Kind)
{
    internal static AskpassRequest Create(string prompt, string? hint = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        // OpenSSH's explicit modes take precedence over the wording of the prompt.
        if (hint == "none") { return new(prompt, AskpassPromptKind.Notification); }
        if (hint == "confirm") { return new(prompt, AskpassPromptKind.Permission); }
        string text = prompt.TrimStart();
        if (text.StartsWith("Password", StringComparison.OrdinalIgnoreCase)
            || text.TrimEnd().EndsWith("'s password:", StringComparison.OrdinalIgnoreCase)
            || text.TrimEnd().EndsWith("'s old password:", StringComparison.OrdinalIgnoreCase)
            || text.TrimEnd().EndsWith("'s new password:", StringComparison.OrdinalIgnoreCase)
            || (text.StartsWith("Enter ", StringComparison.OrdinalIgnoreCase) && text.Contains("passphrase", StringComparison.OrdinalIgnoreCase))
            || text.StartsWith("Bad passphrase, try again", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Current password:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("New password:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Retype new password:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Enter PIN", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("PIN for", StringComparison.OrdinalIgnoreCase))
        { return new(prompt, AskpassPromptKind.Secret); }
        if (text.StartsWith("Username for ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Username:", StringComparison.OrdinalIgnoreCase))
        { return new(prompt, AskpassPromptKind.Username); }
        if (prompt.Contains("(yes/no/[fingerprint])", StringComparison.OrdinalIgnoreCase)
            || text.TrimEnd().Equals("Please type 'yes', 'no' or the fingerprint:", StringComparison.OrdinalIgnoreCase))
        { return new(prompt, AskpassPromptKind.HostVerification); }
        if (prompt.Contains("(yes/no)", StringComparison.OrdinalIgnoreCase)
            || text.TrimEnd().Equals("Please type 'yes' or 'no':", StringComparison.OrdinalIgnoreCase))
        { return new(prompt, AskpassPromptKind.YesNo); }
        return new(prompt, AskpassPromptKind.Secret);
    }
}

internal readonly record struct AskpassResult(AskpassDecision Decision, string? Value = null);

internal static class AskpassProtocol
{
    internal static bool IsSingleLine(string response) => response.IndexOfAny(['\0', '\r', '\n']) < 0;
    // OpenSSH owns fingerprint validation and retries. Preserve its literal answer, including MD5 fingerprints.
    internal static bool IsValidFingerprintResponse(string? response) => !string.IsNullOrWhiteSpace(response) && IsSingleLine(response);

    internal static int GetExitCode(AskpassRequest request, AskpassResult result)
        => TryGetOutput(request, result, out _, out _) ? 0 : 1;

    internal static bool TryGetOutput(AskpassRequest request, AskpassResult result, out string response, out bool appendNewline)
    {
        response = ""; appendNewline = false;
        if (request is null || request.Prompt is null || (result.Decision != AskpassDecision.Submit && result.Value is not null)) { return false; }
        switch (request.Kind)
        {
            case AskpassPromptKind.Secret:
            case AskpassPromptKind.Username:
                if (result.Decision != AskpassDecision.Submit || result.Value is null || !IsSingleLine(result.Value)) { return false; }
                response = result.Value; appendNewline = true; return true;
            case AskpassPromptKind.YesNo:
            case AskpassPromptKind.HostVerification:
                if (result.Decision is AskpassDecision.Yes or AskpassDecision.No)
                { response = result.Decision == AskpassDecision.Yes ? "yes" : "no"; return true; }
                if (request.Kind == AskpassPromptKind.HostVerification && result.Decision == AskpassDecision.Submit && IsValidFingerprintResponse(result.Value))
                { response = result.Value!; return true; }
                return false;
            case AskpassPromptKind.Permission:
                // Empty stdout with exit 0 grants permission in OpenSSH; No and Cancel must fail.
                return result.Decision == AskpassDecision.Yes;
            case AskpassPromptKind.Notification:
                return result.Decision == AskpassDecision.Dismissed;
            default:
                return false;
        }
    }
}

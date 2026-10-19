using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed record CheckoutRevisionRequest(string Revision, bool Force = false)
{
    internal static bool IsActualCommit(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit) && value.Any(c => c != '0');
}

public sealed record CheckoutRevisionOutcome(GitResult? NativeResult, bool BeforeVetoed, string? HeadBefore, string? HeadAfter,
    bool SubmodulesUpdated, bool ScriptsMayHaveChangedRepository)
{
    public bool Succeeded => NativeResult?.Success == true && !BeforeVetoed;
}

public delegate Task<CheckoutRevisionOutcome> CheckoutRevisionExecutor(Window owner, CheckoutRevisionRequest request, CancellationToken cancellationToken);

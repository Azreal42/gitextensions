using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>
    /// Reverses a forward selected patch in the working tree, optionally updating the index in the same native apply.
    /// Patch headers and content must already be byte-equivalent to their source document; no file-codec conversion occurs here.
    /// </summary>
    /// <remarks>Line selection for reverse application uses the changed-side context (SelectLines staged:true).
    /// SelectResetLines already produces an inverse patch and must not be passed to this method.</remarks>
    public Task<GitResult> ResetSelectedPatchAsync(ReadOnlyMemory<byte> patch, bool indexAndWorkingTree, CancellationToken cancellationToken = default)
    {
        if (patch.IsEmpty) { throw new ArgumentException("A nonempty patch is required.", nameof(patch)); }
        byte[] snapshot = patch.ToArray();
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            List<string> arguments = ["apply", "--whitespace=nowarn", "--reverse"];
            if (indexAndWorkingTree) { arguments.Add("--index"); }
            arguments.Add("-");
            GitBinaryResult binary = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. arguments], token, snapshot).ConfigureAwait(false);
            GitResult result = new(binary.ExitCode, Encoding.UTF8.GetString(binary.StandardOutput), binary.StandardError)
            {
                StandardOutputBytes = binary.StandardOutput,
                StandardErrorBytes = binary.StandardErrorBytes,
                Warnings = binary.Warnings
            };
            if (!result.Success) { throw new GitCommandException(arguments, result); }
            return result;
        }, cancellationToken);
    }
}

using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private async Task<GitResult> ApplyFileEncodedPatchAsync(string patch, bool staged, bool reverse, CancellationToken token)
        => await ApplyPatchAsync(GitFileEncodingCodec.EncodePatch(patch, await GetFileEncodingNameAsync(token).ConfigureAwait(false)), staged, reverse, token).ConfigureAwait(false);

    /// <summary>Send Git's canonical patch bytes directly to stdin, without UTF-8 transcoding or a BOM.</summary>
    public Task<GitResult> ApplyPatchAsync(ReadOnlyMemory<byte> patch, bool staged = true, bool reverse = false, CancellationToken cancellationToken = default)
    {
        if (patch.IsEmpty) { throw new ArgumentException("A nonempty patch is required.", nameof(patch)); }
        byte[] snapshot = patch.ToArray();
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            List<string> arguments = ["apply", "--whitespace=nowarn"];
            if (staged) { arguments.Add("--cached"); }
            if (reverse) { arguments.Add("--reverse"); }
            arguments.Add("-");
            GitBinaryResult binary = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. arguments], token, snapshot).ConfigureAwait(false);
            GitResult result = new(binary.ExitCode, Encoding.UTF8.GetString(binary.StandardOutput), binary.StandardError);
            if (!result.Success) { throw new GitCommandException(arguments, result); }
            return result;
        }, cancellationToken);
    }
}

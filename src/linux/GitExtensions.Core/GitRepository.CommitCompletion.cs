namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    internal async Task<string?> ReadCommitCompletionPatchAsync(string path, string? oldPath, bool staged,
        bool useHistogram, CancellationToken token)
    {
        List<string> arguments = [.. GlobalOptions, "--no-optional-locks", "--literal-pathspecs", "diff", "--no-ext-diff",
            "--no-textconv", "--no-color", "--find-renames", "--find-copies", "--unified=5"];
        if (useHistogram) { arguments.Add("--histogram"); }
        if (staged) { arguments.Add("--cached"); }
        arguments.AddRange(["--", ValidatePath(path)]);
        if (staged && oldPath is not null) { arguments.Add(ValidatePath(oldPath)); }
        GitBinaryResult result = await _runner.RunBinaryAsync(Path, arguments, token).ConfigureAwait(false);
        if (!result.Success || result.StandardOutput.Length == 0) { return null; }
        string encodingName = await GetFileEncodingNameAsync(token).ConfigureAwait(false);
        return GitFileEncodingCodec.DecodePatch(result.StandardOutput, encodingName).Text;
    }
}

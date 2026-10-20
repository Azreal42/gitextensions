using System.Text;
using GitCommands.Patches;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Linux;

/// <summary>Read-only adapter to the original Windows patch metadata parser; never applies a patch.</summary>
public static class GitPatchPreview
{
    public static IReadOnlyList<Patch> Parse(string losslessText, string fileEncodingName = "utf-8", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(losslessText);
        cancellationToken.ThrowIfCancellationRequested();
        // Validate the supported name through the portable codec, but retain Windows'
        // replacement decoder fallback for preview text (this path never writes bytes).
        Lazy<Encoding> encoding = new(() => Encoding.GetEncoding(GitFileEncodingCodec.GetEncoding(fileEncodingName).CodePage));
        List<Patch> patches = [];
        foreach (Patch patch in PatchProcessor.CreatePatchesFromString(losslessText, encoding))
        {
            cancellationToken.ThrowIfCancellationRequested();
            patches.Add(patch);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Windows silently shows an empty list. An explicit diagnostic distinguishes an empty
        // patch from a nonempty file that its parser does not recognize, without discarding a preview.
        if (patches.Count == 0 && !string.IsNullOrWhiteSpace(losslessText))
        { throw new FormatException("The file contains no supported Git patch headers."); }
        return patches.AsReadOnly();
    }
}

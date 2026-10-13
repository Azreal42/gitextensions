using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private static readonly HashSet<string> ObjectBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".avi", ".bmp", ".dat", ".bin", ".dll", ".doc", ".docx", ".ppt", ".pps", ".pptx", ".ppsx", ".dwg", ".exe", ".gif", ".ico", ".jpg", ".jpeg", ".mpg", ".mpeg", ".msi", ".pdf", ".png", ".pdb", ".sc1", ".tif", ".tiff", ".vsd", ".vsdx", ".xls", ".xlsx", ".odt" };
    private static readonly HashSet<string> ObjectTextDiffAttributes = new(StringComparer.Ordinal)
    { "set", "astextplain", "ada", "bibtext", "cpp", "csharp", "css", "dts", "elixir", "fortran", "html", "java", "kotlin", "markdown", "matlab", "objc", "pascal", "perl", "php", "python", "ruby", "rust", "scheme", "tex" };

    /// <summary>SaveBlobAs semantics use Git attributes/name and GitConvert's byte heuristic, independently of preview decoding.</summary>
    public async Task<GitObjectExportResult> ExportBlobAsync(string objectId, string destinationPath, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        string oid = ValidateObjectId(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string destination = System.IO.Path.GetFullPath(destinationPath);
        string parent = System.IO.Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) { throw new DirectoryNotFoundException(parent); }
        if (Directory.Exists(destination) || new FileInfo(destination).LinkTarget is not null || !overwrite && File.Exists(destination))
        { throw new IOException("Select an available regular-file destination."); }
        parent = ResolveObjectExportDirectory(parent);
        destination = System.IO.Path.Combine(parent, System.IO.Path.GetFileName(destination));
        GitRepositoryInfo info = await GetInfoAsync(cancellationToken).ConfigureAwait(false);
        // Export is outside Git metadata. Do not overwrite the database or a live index through Save as.
        if (Inside(destination, ResolveObjectExportDirectory(info.GitDirectory)) || Inside(destination, ResolveObjectExportDirectory(info.CommonDirectory)))
        { throw new ArgumentException("A blob export destination must be outside Git metadata.", nameof(destinationPath)); }
        GitObjectData data = await ReadObjectAsync(oid, cancellationToken).ConfigureAwait(false);
        if (data.Type != GitLostObjectType.Blob) { throw new ArgumentException("Only blob objects can be saved as a file.", nameof(objectId)); }
        byte[] bytes = data.Bytes;
        string? autocrlf = (await GetConfigAsync("^core\\.autocrlf$", cancellationToken: cancellationToken).ConfigureAwait(false)).LastOrDefault().Value;
        if (string.Equals(autocrlf, "true", StringComparison.OrdinalIgnoreCase)
            && bytes.Count(value => value == 0) <= 5 && !await IsObjectExportBinaryNameAsync(destination, cancellationToken).ConfigureAwait(false))
        { bytes = ConvertObjectCrlfToWorktree(bytes); }
        bool converted = !bytes.AsSpan().SequenceEqual(data.Bytes);
        string temporary = System.IO.Path.Combine(parent, ".gitextensions-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        return new(destination, bytes.LongLength, converted);

        static bool Inside(string file, string directory) => file.Equals(directory, StringComparison.Ordinal)
            || file.StartsWith(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string ResolveObjectExportDirectory(string directory)
    {
        string full = System.IO.Path.GetFullPath(directory), resolved = System.IO.Path.GetPathRoot(full)!;
        foreach (string component in full[resolved.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = System.IO.Path.Combine(resolved, component);
            DirectoryInfo info = new(next);
            resolved = info.LinkTarget is null ? next : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new IOException("The export parent symbolic link cannot be resolved.");
        }
        return resolved;
    }

    private async Task<bool> IsObjectExportBinaryNameAsync(string destination, CancellationToken token)
    {
        GitBinaryResult attrs = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, "check-attr", "-z", "diff", "text", "crlf", "eol", "--", destination], token).ConfigureAwait(false);
        if (attrs.Success)
        {
            string[] fields = ObjectUtf8.GetString(attrs.StandardOutput).Split('\0');
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            for (int index = 0; index + 2 < fields.Length; index += 3) { values[fields[index + 1]] = fields[index + 2]; }
            if (values.TryGetValue("diff", out string? diff))
            {
                if (diff == "unset") { return true; }
                if (ObjectTextDiffAttributes.Contains(diff)) { return false; }
            }
            foreach (string key in new[] { "text", "crlf", "eol" })
            { if (values.TryGetValue(key, out string? value) && value is not ("unset" or "unspecified")) { return false; } }
        }
        return ObjectBinaryExtensions.Contains(System.IO.Path.GetExtension(destination));
    }

    // Byte-equivalent to upstream GitConvert.ConvertCrLfToWorktree; no codec roundtrip or platform newline substitution.
    private static byte[] ConvertObjectCrlfToWorktree(byte[] bytes)
    {
        int lf = 0, cr = 0, crlf = 0, printable = 0, nonPrintable = 0, nul = 0;
        for (int index = 0; index < bytes.Length; index++)
        {
            byte value = bytes[index];
            if (value == 13) { cr++; if (index + 1 < bytes.Length && bytes[index + 1] == 10) { crlf++; } continue; }
            if (value == 10) { lf++; continue; }
            if (value == 127) { nonPrintable++; }
            else if (value < 32 && value is not (8 or 9 or 27 or 12)) { nonPrintable++; if (value == 0) { nul++; } }
            else { printable++; }
        }
        if (bytes.Length > 0 && bytes[^1] == 26) { nonPrintable--; }
        if (lf == 0 || lf == crlf || cr != crlf || nul > 0 || printable / 128 < nonPrintable) { return bytes; }
        List<byte> converted = new(bytes.Length + lf - crlf);
        for (int index = 0; index < bytes.Length; index++)
        { if (bytes[index] == 10 && (index == 0 || bytes[index - 1] != 13)) { converted.Add(13); } converted.Add(bytes[index]); }
        return converted.ToArray();
    }

    private static string GuessLostObjectFileName(string oid, byte[] bytes, string? text)
    {
        // The original signature order is significant (e.g. JSON before generic '<' XML).
        (string Prefix, string Extension)[] signatures =
        [ ("{\\rtf", "rtf"), ("{", "json"), ("#include", "cpp"), ("import {", "js"), ("import * as", "js"), ("import \"", "js"), ("export ", "js"),
          ("import ", "java"), ("from", "py"), ("package", "go"), ("namespace ", "fs"), ("#!", "sh"), ("[", "ini"), ("using ", "cs"),
          ("# ", "md"), ("##", "md"), ("<!doctype html", "html"), ("<html", "html"), ("<?xml", "xml"), ("use ", "rs"), ("%PDF", "pdf"),
          ("PK", "zip"), ("MZ", "exe"), ("\\document", "tex"), ("\u0089PNG", "png"), ("ÿØÿQ", "jp2"), ("ÿØÿ", "jpg"), ("ÿ\n", "jxl"),
          ("RIFF", "webp"), ("<svg", "svg"), ("BM", "bmp"), ("7z", "7z"), ("GIF", "gif"), ("ÐÏ\u0011à¡±\u001aá", "doc"), ("qoif", "qoi"),
          ("Rar!", "rar"), ("%!PS", "ps"), ("OggS", "ogg"), ("8BPS", "psf"), ("ID3", "mp3"), ("CD001", "iso"), ("fLaC", "flac"),
          ("FLIF", "flif"), ("␚Eß£", "mkv"), ("<", "xml") ];
        string content = text ?? Encoding.Latin1.GetString(bytes);
        string extension = signatures.FirstOrDefault(signature => content.StartsWith(signature.Prefix, StringComparison.OrdinalIgnoreCase)).Extension ?? "txt";
        return LostFoundTagPrefix + oid + "." + extension;
    }
}

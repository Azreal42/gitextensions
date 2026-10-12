using GitCommands;
using GitCommands.Settings;

namespace GitExtensions.Linux;

public enum GitDiffCopyOperation { Patch, NewVersion, OldVersion }

/// <summary>A contiguous editor selection in UTF16. A zero length uses the whole document.</summary>
public readonly record struct GitDiffTextSelection(int Offset, int Length);

/// <summary>Pure FileViewer copy commands. Version copies filter the displayed patch;
/// they do not reconstruct content omitted from its hunks.</summary>
public static class GitDiffClipboard
{
    public static string GetText(string document, GitDiffCopyOperation operation,
        GitDiffTextSelection? selection = null, bool isDiff = true, AutoCRLFType? autoCrlf = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Enum.IsDefined(operation)) { throw new ArgumentOutOfRangeException(nameof(operation)); }
        if (selection is GitDiffTextSelection range
            && (range.Offset < 0 || range.Offset > document.Length || range.Length < 0 || range.Length > document.Length - range.Offset))
        { throw new ArgumentOutOfRangeException(nameof(selection), "Copy selections must refer to valid UTF16 document offsets."); }

        bool selected = selection is { Length: > 0 };
        int position = selected ? selection!.Value.Offset : 0;
        string text = selected ? document.Substring(position, selection!.Value.Length) : document;

        // FileViewer.CopyPatchToolStripMenuItemClick copies verbatim, including
        // its line endings. The autocrlf conversion belongs only to version copies.
        if (operation == GitDiffCopyOperation.Patch) { return text; }
        if (isDiff)
        {
            // FileViewerInternal.CopyNotStartingWith protects a selected fragment
            // beginning inside a line from being mistaken for a patch prefix.
            if (position > 0 && document[position - 1] != '\n') { text = " " + text; }
            char excluded = operation == GitDiffCopyOperation.NewVersion ? '-' : '+';
            IEnumerable<string> lines = text.Split('\n').Where(line => line.Length == 0 || line[0] != excluded
                || line.Length > 2 && line[1] == line[0] && line[2] == line[0]);
            int hunk = document.IndexOf("\n@@", StringComparison.Ordinal);
            // Match the original header rule, including its -1 sentinel when the
            // displayed document has no newline-prefixed hunk header.
            if (hunk <= position)
            {
                lines = lines.Select(line => line.Length > 0 && line[0] is ' ' or '-' or '+' ? line[1..] : line);
            }
            text = string.Join("\n", lines);
        }

        return text.AdjustLineEndings(autoCrlf);
    }
}

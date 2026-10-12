namespace GitExtensions.Linux;

internal static class GitPatchSelection
{
    // Avalonia/UTF-16 selections are half-open. The shared original Chunk parser
    // expects an inclusive end, and otherwise includes the next unselected row.
    internal static int InclusiveLength(int selectionLength) => Math.Max(0, selectionLength - 1);
}

namespace GitCommands
{
    // Small portability seam for the linked original normaliser; PathUtil's other methods depend on Windows services.
    internal static class PathUtil
    {
        internal static bool IsValidPathChar(char value)
            => value is (> ' ' and < '~' and not ('^' or ':' or '"' or '<' or '>')) && Array.IndexOf(System.IO.Path.GetInvalidPathChars(), value) < 0;
    }
}

namespace GitCommands.Git
{
    internal static class BranchNameStringExtensions
    {
        internal static string Join(this string[] values, string separator) => string.Join(separator, values);
    }
}

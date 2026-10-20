using System.Text;

// The upstream line-selection algorithm is compiled unchanged. These adapters supply only
// its encoding, settings and mail-footer dependencies; no Windows UI assembly is loaded.
namespace GitCommands.Patches
{
    internal static partial class GitModule
    {
        internal static Encoding SystemEncoding => Encoding.UTF8;
        internal const string NoNewLineAtTheEnd = "\\ No newline at end of file";
    }

    internal static class Application
    {
        internal const string ProductName = "Git Extensions Linux";
    }

    internal static class AppSettings
    {
        internal static string ProductVersion => typeof(GitExtensions.Linux.GitRepository).Assembly.GetName().Version?.ToString() ?? "";
    }

    internal static class PatchStringExtensions
    {
        internal static string? Combine(this string? left, string separator, string? right)
            => string.IsNullOrEmpty(left) ? right : string.IsNullOrEmpty(right) ? left : left + separator + right;
    }

    /// <summary>A Linux adapter for the sole settings read in the linked new-file patch algorithm.</summary>
    public interface IGitModule
    {
        string GetEffectiveSetting(string key);
    }
}

namespace JetBrains.Annotations
{
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class InstantHandleAttribute : Attribute;
}

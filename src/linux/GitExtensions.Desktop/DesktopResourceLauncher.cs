using System.Diagnostics;

namespace GitExtensions.Linux.Desktop;

internal enum DesktopPlatform { Linux, Windows, MacOS }

/// <summary>Open resources with the platform's associated application; the target is never shell command text.</summary>
internal static class DesktopResourceLauncher
{
    internal static DesktopPlatform CurrentPlatform => OperatingSystem.IsWindows() ? DesktopPlatform.Windows
        : OperatingSystem.IsMacOS() ? DesktopPlatform.MacOS : OperatingSystem.IsLinux() ? DesktopPlatform.Linux
        : throw new PlatformNotSupportedException("Supported desktop platforms are Windows, Linux and macOS.");

    internal static ProcessStartInfo CreateStartInfo(string target, string? workingDirectory = null)
        => CreateStartInfo(target, CurrentPlatform, workingDirectory);

    internal static ProcessStartInfo CreateStartInfo(string target, DesktopPlatform platform, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (target.Contains('\0') || workingDirectory?.Contains('\0') == true)
        { throw new ArgumentException("Resource launch values cannot contain NUL.", nameof(target)); }
        ProcessStartInfo start = new() { WorkingDirectory = workingDirectory ?? "" };
        switch (platform)
        {
            case DesktopPlatform.Windows:
                start.FileName = target; start.UseShellExecute = true; start.Verb = "open";
                break;
            case DesktopPlatform.MacOS:
                start.FileName = "/usr/bin/open"; start.UseShellExecute = false;
                start.ArgumentList.Add("--"); start.ArgumentList.Add(target);
                break;
            case DesktopPlatform.Linux:
                start.FileName = "xdg-open"; start.UseShellExecute = false; start.ArgumentList.Add(target);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(platform));
        }
        return start;
    }
}

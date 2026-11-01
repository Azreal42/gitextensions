using System.Diagnostics;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

internal static class TestDotnetHost
{
    internal static string Executable
    {
        get
        {
            string name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            string runtimeRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
            // setup-dotnet and a repository-local SDK both expose a real host.
            // Resolve the running installation without requiring a checkout .dotnet directory.
            foreach (string? candidate in new[] { Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), Path.Combine(runtimeRoot, name) })
            {
                if (candidate is not null && Path.IsPathFullyQualified(candidate) && File.Exists(candidate)
                    && Path.GetFileName(candidate) == name) { return candidate; }
            }
            throw new AssertionException("A verified existing .NET host is required for the fresh-process proof.");
        }
    }

    internal static ProcessStartInfo CreateStartInfo()
    {
        DirectoryInfo? checkout = new(AppContext.BaseDirectory);
        while (checkout is not null && !File.Exists(Path.Combine(checkout.FullName, "GitExtensions.Linux.slnx"))) { checkout = checkout.Parent; }
        if (checkout is null) { throw new AssertionException("The test output must belong to the Linux solution checkout."); }
        return new(Executable) { WorkingDirectory = checkout.FullName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    }
}

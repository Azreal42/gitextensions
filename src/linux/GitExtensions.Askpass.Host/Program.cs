using System.Diagnostics;

namespace GitExtensions.Askpass.Host;

/// <summary>A native apphost for Git and OpenSSH, which cannot execute a Windows batch file as SSH_ASKPASS.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) { return 1; }
        try
        {
            ProcessStartInfo start = new(Path.Combine(AppContext.BaseDirectory, "GitExtensions.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "--askpass", args[0] }
            };
            using Process child = Process.Start(start) ?? throw new IOException("Cannot start the askpass application.");
            // Relay protocol bytes without transcoding, logging, or evaluating the prompt/response.
            Task output = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
            Task error = child.StandardError.BaseStream.CopyToAsync(Stream.Null);
            child.WaitForExit(); Task.WhenAll(output, error).GetAwaiter().GetResult();
            return child.ExitCode;
        }
        catch (Exception) { return 1; }
    }
}

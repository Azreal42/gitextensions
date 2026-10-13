using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GitExtensions.Linux;

/// <summary>One Linux session per invocation, so an exited parent cannot leave an unkillable inherited output pipe.</summary>
[SupportedOSPlatform("linux")]
internal static class GitProcessGroup
{
    private const int SigKill = 9, NoSuchProcess = 3;

    internal static Exception? Stop(Process process)
    {
        Exception? failure = null;
        void Group()
        {
            int id = process.Id;
            if (id <= 1 || id == GetProcessGroup())
            { failure ??= new InvalidOperationException("Refusing to signal the application's process group."); return; }
            // A group's ID remains reserved while its orphaned members still exist, even after its leader exits.
            int result;
            do { result = Kill(-id, SigKill); } while (result != 0 && Marshal.GetLastPInvokeError() == 4);
            if (result != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != NoSuchProcess) { failure ??= new Win32Exception(error, "Cannot stop the private Git process group."); }
            }
        }
        // While the leader is alive, KillTree also finds children which started a separate group.
        // It stops the leader before enumerating children, covering cancellation before setsid().
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        catch (InvalidOperationException) { /* The child exited between the state check and Kill. */ }
        catch (Exception exception) { failure ??= exception; }
        // After exit, orphaned pipe holders still retain this invocation's reserved PGID/SID.
        try { Group(); }
        catch (Exception exception) { failure ??= exception; }
        return failure;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int process, int signal);
    [DllImport("libc", EntryPoint = "getpgrp")]
    private static extern int GetProcessGroup();
}

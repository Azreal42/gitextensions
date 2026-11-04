using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace GitExtensions.Linux.Desktop;

[SupportedOSPlatform("windows")]
internal static class WindowsInteractiveFileConsoleProtocol
{
    internal static void AuthenticateClient(SafePipeHandle pipe, int expected)
    {
        if (!GetNamedPipeClientProcessId(pipe, out uint actual))
        { throw new IOException("Cannot identify the console pipe client.", new Win32Exception(Marshal.GetLastPInvokeError())); }
        if (actual != (uint)expected) { throw new InvalidDataException("The console pipe client is not the process launched for this operation."); }
    }
    internal static void AuthenticateServer(SafePipeHandle pipe, int expected)
    {
        if (!GetNamedPipeServerProcessId(pipe, out uint actual))
        { throw new IOException("Cannot identify the console pipe server.", new Win32Exception(Marshal.GetLastPInvokeError())); }
        if (actual != (uint)expected) { throw new InvalidDataException("The console pipe server is not its launching application."); }
    }
    internal static bool SameNonce(string? left, string? right)
        => left is not null && right is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    internal static async Task WriteAsync<T>(StreamWriter writer, T value, CancellationToken token = default)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), token);
        await writer.FlushAsync(token);
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}

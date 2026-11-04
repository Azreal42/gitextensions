using System.Runtime.InteropServices;

namespace GitExtensions.Linux;

internal sealed class NativeUtf8Vector : IDisposable
{
    private readonly List<IntPtr> _strings = [];
    internal IntPtr Pointer { get; private set; }
    internal NativeUtf8Vector(IEnumerable<string> values)
    {
        try
        {
            foreach (string value in values)
            {
                ArgumentNullException.ThrowIfNull(value);
                if (value.Contains('\0')) { throw new ArgumentException("Native process values cannot contain NUL.", nameof(values)); }
                _strings.Add(Marshal.StringToCoTaskMemUTF8(value));
            }
            IntPtr[] vector = [.. _strings, IntPtr.Zero];
            Pointer = Marshal.AllocHGlobal(vector.Length * IntPtr.Size); Marshal.Copy(vector, 0, Pointer, vector.Length);
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { foreach (IntPtr value in _strings) { Marshal.FreeCoTaskMem(value); } Marshal.FreeHGlobal(Pointer); }
}

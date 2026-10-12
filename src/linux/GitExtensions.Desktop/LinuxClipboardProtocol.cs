using System.Runtime.InteropServices;

namespace GitExtensions.Linux.Desktop;

/// <summary>Provide clipboard protocol atoms before Avalonia 11.3.22 caches the X11 server's existing atoms.</summary>
internal sealed class LinuxClipboardProtocol : IDisposable
{
    private IntPtr _display;

    private LinuxClipboardProtocol(IntPtr display) => _display = display;

    /// <summary>Keep this connection alive during platform initialization so a first-client X server cannot reset the atoms.</summary>
    public static LinuxClipboardProtocol? TryOpen()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))) { return null; }
        LinuxClipboardProtocol? protocol = null;
        try
        {
            // Xlib requires thread initialization before any display connection, as Avalonia's platform also does.
            if (Native.XInitThreads() == 0) { return null; }
            IntPtr display = Native.XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) { return null; }
            protocol = new(display);
            string[] names = ["CLIPBOARD", "TARGETS", "UTF8_STRING", "UTF16_STRING", "MULTIPLE", "ATOM_PAIR", "INCR"];
            foreach (string name in names)
            {
                if (Native.XInternAtom(display, name, onlyIfExists: 0) == IntPtr.Zero)
                { protocol.Dispose(); return null; }
            }
            return protocol;
        }
        catch (DllNotFoundException) { protocol?.Dispose(); return null; }
        catch (EntryPointNotFoundException) { protocol?.Dispose(); return null; }
        catch (BadImageFormatException) { protocol?.Dispose(); return null; }
    }

    public void Dispose()
    {
        IntPtr display = Interlocked.Exchange(ref _display, IntPtr.Zero);
        if (display != IntPtr.Zero) { Native.XCloseDisplay(display); }
    }

    private static class Native
    {
        [DllImport("libX11.so.6", ExactSpelling = true)]
        internal static extern int XInitThreads();

        [DllImport("libX11.so.6", ExactSpelling = true)]
        internal static extern IntPtr XOpenDisplay(IntPtr displayName);

        [DllImport("libX11.so.6", CharSet = CharSet.Ansi, ExactSpelling = true)]
        internal static extern IntPtr XInternAtom(IntPtr display, string name, int onlyIfExists);

        [DllImport("libX11.so.6", ExactSpelling = true)]
        internal static extern int XCloseDisplay(IntPtr display);
    }
}

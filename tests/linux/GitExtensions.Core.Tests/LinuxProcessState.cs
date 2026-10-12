using System.Globalization;

internal static class LinuxProcessState
{
    internal static bool IsRunning(int pid)
    {
        string path = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat";
        if (!File.Exists(path)) { return false; }
        try { return IsRunningStat(File.ReadAllText(path)); }
        catch (IOException) when (!File.Exists(path)) { return false; }
    }

    internal static bool IsRunningStat(string stat)
    {
        // comm may contain spaces and parentheses. X/x are dead tasks, which
        // can appear briefly between a zombie observation and procfs removal.
        int end = stat.LastIndexOf(')');
        if (end < 0 || end + 2 >= stat.Length || stat[end + 1] != ' ')
        { throw new FormatException("Incomplete procfs process state."); }
        return stat[end + 2] is not ('Z' or 'X' or 'x');
    }

    internal static Task VerifyStatesAsync()
    {
        foreach (char state in "RSDTtWPKI")
        {
            if (!IsRunningStat("42 (name (with) )) " + state + " 1 2"))
            { throw new InvalidOperationException("Active or stopped task must remain alive: " + state); }
        }
        foreach (char state in "ZXx")
        {
            if (IsRunningStat("42 (sleep) " + state + " 1 2"))
            { throw new InvalidOperationException("Zombie/dead task cannot execute: " + state); }
        }
        foreach (string incomplete in new[] { "", "42 (sleep)", "42 (sleep) " })
        {
            try { IsRunningStat(incomplete); }
            catch (FormatException) { continue; }
            throw new InvalidOperationException("Malformed procfs data must not hide a live process.");
        }
        if (OperatingSystem.IsLinux() && !IsRunning(Environment.ProcessId))
        { throw new InvalidOperationException("The test's own process is alive."); }
        return Task.CompletedTask;
    }
}

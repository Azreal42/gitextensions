using System.Diagnostics;

namespace GitExtensions.Linux.Desktop;

/// <summary>Platform terminal choices preserve literal argv and the captured environment.</summary>
internal static class ConsoleStyleTerminal
{
    internal static bool IsAvailable(string name, string? path)
    {
        if (!ConsoleStylePreferences.Terminals.Contains(name, StringComparer.Ordinal)) { return false; }
        if (name == "Automatic" || OperatingSystem.IsWindows() && name == "Windows console") { return true; }
        if (OperatingSystem.IsMacOS() && name == "Terminal.app")
        { return Directory.Exists("/System/Applications/Utilities/Terminal.app") || Directory.Exists("/Applications/Utilities/Terminal.app"); }
        return Find(name, path) is not null;
    }

    internal static void ValidateNativeSelection(ConsoleStylePreferences? preferences = null)
    {
        ConsoleStylePreferences settings = preferences ?? new ConsoleStylePreferencesStore().Load();
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!IsAvailable(settings.Terminal, path) || settings.Terminal == "Automatic"
            && !ConsoleStylePreferences.Terminals.Skip(1).Any(name => IsAvailable(name, path)))
        { throw new FileNotFoundException("The saved terminal is unavailable on this platform. Choose an installed terminal in Console style settings.", settings.Terminal); }
    }

    internal static string? Find(string name, string? path)
        => (path ?? "").Split(Path.PathSeparator).Where(directory => !string.IsNullOrEmpty(directory))
            .Select(directory => OperatingSystem.IsWindows() && directory.Length >= 2 && directory[0] == '"' && directory[^1] == '"' ? directory[1..^1] : directory)
            .Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists);

    internal static ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments,
        GitExecutionEnvironment environment, bool waitForExit, string? workingDirectory = null, ConsoleStylePreferences? preferences = null)
    {
        ConsoleStylePreferences settings = preferences ?? new ConsoleStylePreferencesStore().Load();
        string? path = environment.GetVariable("PATH");
        string? terminal = settings.Terminal == "Automatic"
            ? ConsoleStylePreferences.Terminals.Skip(1).Select(name => Find(name, path)).FirstOrDefault(value => value is not null)
            : ConsoleStylePreferences.Terminals.Contains(settings.Terminal, StringComparer.Ordinal) ? Find(settings.Terminal, path) : null;
        if (terminal is null) { throw new FileNotFoundException("The selected Linux terminal emulator is unavailable. Choose an installed terminal in Console style settings.", settings.Terminal); }
        ProcessStartInfo info = new(terminal) { UseShellExecute = false };
        environment.ApplyTo(info);
        if (workingDirectory is not null) { info.WorkingDirectory = workingDirectory; }
        string name = Path.GetFileName(terminal);
        if (name == "gnome-terminal") { if (waitForExit) { info.ArgumentList.Add("--wait"); } info.ArgumentList.Add("--"); }
        else { if (name == "konsole" && waitForExit) { info.ArgumentList.Add("--nofork"); } info.ArgumentList.Add("-e"); }
        info.ArgumentList.Add(executable);
        foreach (string argument in arguments) { info.ArgumentList.Add(argument); }
        return info;
    }
}

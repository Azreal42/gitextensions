namespace GitExtensions.Desktop.Tests;

/// <summary>A readiness file becomes visible only after its complete native actor data is closed.</summary>
internal static class TestSignalFile
{
    // Arguments are fixture-generated Python expressions; paths are JSON string
    // literals or __file__ expressions, never user-supplied command text.
    internal static string Python(string pathExpression, string textExpression, int indent = 0)
    {
        string pad = new(' ', indent);
        return pad + "signal_path = " + pathExpression + "\n"
            + pad + "signal_temporary = signal_path + '.tmp-' + str(os.getpid())\n"
            + pad + "with open(signal_temporary, 'w', encoding='utf-8') as signal_output:\n"
            + pad + "    signal_output.write(" + textExpression + ")\n"
            + pad + "os.replace(signal_temporary, signal_path)\n";
    }

    internal static string ShellPids(string path, params string[] variables)
    {
        string temporary = Quote(path + ".tmp-") + "\"$$\"";
        string format = string.Join(" ", variables.Select(_ => "%s")) + "\\n";
        return "printf '" + format + "' " + string.Join(" ", variables.Select(value => "\"" + value + "\""))
            + " > " + temporary + "\nmv -- " + temporary + " " + Quote(path) + "\n";
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

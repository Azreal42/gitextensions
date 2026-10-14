using System.Xml;
using System.Xml.Serialization;

namespace GitExtensions.Linux.Scripts;

public static class ScriptConfiguration
{
    public const int MinimumUserScriptId = 9000;
    private const string ParameterSeparator = "<_PARAM_SEPARATOR_>", ScriptSeparator = "<_SCRIPT_SEPARATOR_>";
    private static readonly XmlSerializer Serializer = new(typeof(ScriptsXmlEnvelope));

    public static ScriptConfigurationResult Read(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return new(GetDefaults(), ScriptConfigurationSource.Defaults, text); }
        try
        {
            using StringReader input = new(text);
            using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            ScriptsXmlEnvelope envelope = (ScriptsXmlEnvelope)Serializer.Deserialize(reader)!;
            return new(RepairIdentifiers(envelope.Scripts), ScriptConfigurationSource.Xml, text);
        }
        catch (Exception xmlError) when (xmlError is InvalidOperationException or XmlException or ArgumentException or InvalidDataException or OverflowException)
        {
            if (!text.Contains(ParameterSeparator, StringComparison.Ordinal) && !text.Contains(ScriptSeparator, StringComparison.Ordinal))
            { return new(Array.Empty<ScriptDefinition>(), ScriptConfigurationSource.Invalid, text, xmlError); }
            try
            {
                List<ScriptDefinition> scripts = [];
                foreach (string item in text.Split([ScriptSeparator], StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] values = item.Split([ParameterSeparator], StringSplitOptions.None);
                    if (values.Length < 4) { throw new InvalidDataException("An old-format script requires four fields.", xmlError); }
                    scripts.Add(new() { Name = values[0], Command = values[1], Arguments = values[2],
                        AddToRevisionGridContextMenu = values[3] == "yes", Enabled = true, RunInBackground = false });
                }
                return new(RepairIdentifiers(scripts), ScriptConfigurationSource.Legacy, text);
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
            { return new(Array.Empty<ScriptDefinition>(), ScriptConfigurationSource.Invalid, text, error); }
        }
    }

    public static string WriteXml(IEnumerable<ScriptDefinition> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ScriptDefinition[] snapshot = scripts.ToArray();
        if (snapshot.Any(script => script is null || !Enum.IsDefined(script.OnEvent)))
        { throw new ArgumentException("Scripts must have a valid event.", nameof(scripts)); }
        using StringWriter output = new();
        using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings { Indent = true, NewLineHandling = NewLineHandling.Entitize }))
        { Serializer.Serialize(writer, new ScriptsXmlEnvelope { Scripts = snapshot.ToList() }); }
        return output.ToString();
    }

    public static int NextIdentifier(IEnumerable<ScriptDefinition> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        return checked(Math.Max(MinimumUserScriptId, scripts.Select(script => script.HotkeyCommandIdentifier).DefaultIfEmpty(0).Max()) + 1);
    }

    private static IReadOnlyList<ScriptDefinition> RepairIdentifiers(IEnumerable<ScriptDefinition> scripts)
    {
        ScriptDefinition[] snapshot = scripts.ToArray();
        if (snapshot.Any(script => script is null)) { throw new InvalidDataException("A script entry cannot be null."); }
        int maximum = snapshot.Select(script => script.HotkeyCommandIdentifier).DefaultIfEmpty(0).Max();
        HashSet<int> identifiers = [];
        for (int index = 0; index < snapshot.Length; index++)
        {
            if (identifiers.Add(snapshot[index].HotkeyCommandIdentifier)) { continue; }
            maximum = checked(maximum + 1);
            snapshot[index] = snapshot[index] with { HotkeyCommandIdentifier = maximum };
            identifiers.Add(maximum);
        }
        return Array.AsReadOnly(snapshot);
    }

    public static IReadOnlyList<ScriptDefinition> GetDefaults() => Array.AsReadOnly<ScriptDefinition>(
    [
        new() { HotkeyCommandIdentifier = 9000, Name = "Fetch changes after commit", Command = "git", Arguments = "fetch",
            AskConfirmation = true, OnEvent = ScriptEvent.AfterCommit, Enabled = false },
        new() { HotkeyCommandIdentifier = 9001, Name = "Update submodules after pull", Command = "git", Arguments = "submodule update --init --recursive",
            AskConfirmation = true, OnEvent = ScriptEvent.AfterPull, Enabled = false },
        new() { HotkeyCommandIdentifier = 9005, Icon = "EditFile", Name = "&Open in VS Code", Command = "bash",
            Arguments = "-c '" + "if [ -z \"{SelectedRelativePaths}\" ]; then code .; "
                + "elif [ -d \"{SelectedRelativePaths}\" ]; then code {{SelectedRelativePaths}}; "
                + "elif [ ! -f \"{SelectedRelativePaths}\" ]; then code . --goto {{SelectedRelativePaths}}; "
                + "else code . --goto {{SelectedRelativePaths}}:{LineNumber}:{ColumnNumber}; fi'",
            RunInBackground = true, OnEvent = ScriptEvent.ShowInUserMenuBar, Enabled = false },
        new() { HotkeyCommandIdentifier = 9002, Name = "&Example", Command = @"c:\windows\system32\calc.exe", Arguments = "",
            OnEvent = ScriptEvent.ShowInUserMenuBar, Enabled = false },
        new() { HotkeyCommandIdentifier = 9003, Name = "Open on GitHub", Command = "{openurl}",
            Arguments = "https://github.com{cDefaultRemotePathFromUrl}/commit/{sHash}", AddToRevisionGridContextMenu = true, Enabled = false },
        new() { HotkeyCommandIdentifier = 9004, Name = "Fetch All Submodules", Command = "git", Arguments = "submodule foreach --recursive git fetch --all",
            AddToRevisionGridContextMenu = true, Enabled = false },
        new() { HotkeyCommandIdentifier = 9006, Name = "Convert workspace file to LF", Command = "bash.exe",
            Arguments = "-c 'dos2unix.exe {{SelectedRelativePaths}}'", RunInBackground = true, Enabled = false },
        new() { HotkeyCommandIdentifier = 9007, Name = "Convert workspace file to CRLF", Command = "bash.exe",
            Arguments = "-c 'unix2dos.exe {{SelectedRelativePaths}}'", RunInBackground = true, Enabled = false }
    ]);
}

// Public for XmlSerializer; no UI/runtime dependency. Element names match the upstream binding-list XML.
[XmlRoot("ArrayOfScriptInfo")]
public sealed class ScriptsXmlEnvelope
{
    [XmlElement("ScriptInfo")]
    public List<ScriptDefinition> Scripts { get; set; } = [];
}

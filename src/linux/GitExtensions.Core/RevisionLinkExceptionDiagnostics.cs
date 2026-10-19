using System.Collections;
using System.Text;

namespace GitCommands;

// The portable diagnostic part of ExceptionUtils.cs:32-42. Its message-box helpers stay on Windows.
internal static class RevisionLinkExceptionDiagnostics
{
    internal static string ToStringWithData(this Exception error)
    {
        StringBuilder text = new();
        text.AppendLine(error.ToString());
        text.AppendLine();
        foreach (DictionaryEntry entry in error.Data)
        { text.AppendLine(entry.Key + " = " + entry.Value); }
        return text.ToString();
    }
}

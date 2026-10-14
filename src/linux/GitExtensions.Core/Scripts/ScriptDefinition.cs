using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace GitExtensions.Linux.Scripts;

// Names and ordinals belong to the Windows ownScripts XML contract.
public enum ScriptEvent
{
    None, BeforeCommit, AfterCommit, BeforePull, AfterPull, BeforePush, AfterPush,
    ShowInUserMenuBar, BeforeCheckout, AfterCheckout, BeforeMerge, AfterMerge,
    BeforeFetch, AfterFetch, ShowInFileList
}

[XmlType("ScriptInfo")]
public sealed record ScriptDefinition
{
    public bool Enabled { get; init; } = true;
    public string? Name { get; init; }
    public string? Command { get; init; }
    public string? Arguments { get; init; }
    public bool AddToRevisionGridContextMenu { get; init; }
    public ScriptEvent OnEvent { get; init; }
    public bool AskConfirmation { get; init; }
    public bool RunInBackground { get; init; }
    public bool IsPowerShell { get; init; }
    public int HotkeyCommandIdentifier { get; init; }
    public string? Icon { get; init; }
    public string? IconFilePath { get; init; }

    public string GetDisplayName() => Regex.Replace(Name ?? "", "&(?!&)", "");
}

public enum ScriptConfigurationSource { Defaults, Xml, Legacy, Invalid }

public sealed record ScriptConfigurationResult(
    IReadOnlyList<ScriptDefinition> Scripts, ScriptConfigurationSource Source,
    string? OriginalText = null, Exception? Error = null)
{
    public bool Success => Error is null;
}

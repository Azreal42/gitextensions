using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

/// <summary>UI infrastructure for FormQuickStringSelector; does not launch or register scripts.</summary>
internal sealed class QuickStringSelectorDialog : QuickItemSelectorDialog<string>
{
    internal QuickStringSelectorDialog(IReadOnlyList<string> values, TranslationCatalog? translations = null)
        : base(Define(values, translations ?? TranslationCatalog.LoadDefault())) { }

    internal string? SelectedString => SelectedChoice;
    internal ScriptReply Reply => WasAccepted ? new(ScriptReplyKind.Accepted, SelectedString) : new(ScriptReplyKind.Cancelled);

    private static QuickSelectorDefinition<string> Define(IReadOnlyList<string> values, TranslationCatalog translations)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new("QuickStringSelectorDialog", translations.Translate("FormQuickStringSelector", "_actionSelect", "Text", "Select"),
            "QuickStringsFrame", values.OrderBy(value => value).ToArray(), value => value, 0);
    }
}

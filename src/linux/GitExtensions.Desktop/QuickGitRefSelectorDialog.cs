using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal enum QuickGitRefAction { Rename, Delete, Select }
internal sealed record QuickGitReferenceItem(string Label, GitReference? Reference)
{
    public override string ToString() => Label;
}

/// <summary>The borderless FormQuickGitRefSelector used to disambiguate a commit's references.</summary>
internal sealed class QuickGitRefSelectorDialog : QuickItemSelectorDialog<QuickGitReferenceItem>
{
    internal QuickGitRefSelectorDialog(QuickGitRefAction action, IReadOnlyList<GitReference> references,
        TranslationCatalog? translations = null) : base(Define(action, references, translations ?? TranslationCatalog.LoadDefault())) { }

    internal GitReference? SelectedReference => SelectedChoice?.Reference;

    private static QuickSelectorDefinition<QuickGitReferenceItem> Define(QuickGitRefAction action, IReadOnlyList<GitReference> references, TranslationCatalog translations)
    {
        ArgumentNullException.ThrowIfNull(references);
        string Text(string key, string value) => translations.Translate("FormQuickGitRefSelector", key, "Text", value);
        string buttonText = action switch
        {
            QuickGitRefAction.Rename => Text("_actionRename", "Rename"),
            QuickGitRefAction.Delete => Text("_actionDelete", "Delete"),
            QuickGitRefAction.Select => Text("_actionSelect", "Select"),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        const string separator = "――――――――――――――――――";
        List<QuickGitReferenceItem> items = [];
        AddGroup(reference => reference.IsBranch, Text("_local", "local"));
        AddGroup(reference => reference.IsRemote, Text("_remote", "remote"));
        AddGroup(reference => reference.IsTag, Text("_tag", "tag"));
        return new("QuickGitRefSelectorDialog", buttonText, "QuickReferencesFrame", items, item => item.Label, 1);

        void AddGroup(Func<GitReference, bool> predicate, string caption)
        {
            GitReference[] group = references.Where(predicate).OrderBy(reference => reference.ShortName).ToArray();
            if (group.Length == 0) { return; }
            items.Add(new(($"{caption} {separator}")[..separator.Length], null));
            items.AddRange(group.Select(reference => new QuickGitReferenceItem(reference.ShortName, reference)));
        }
    }
}

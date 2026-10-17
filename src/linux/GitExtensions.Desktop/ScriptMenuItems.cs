using Avalonia.Controls;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

internal static class ScriptMenuItems
{
    internal static MenuItem Create(ScriptDefinition script, Func<Task> execute)
    {
        MenuItem item = new() { Name = "UserScript_" + script.HotkeyCommandIdentifier,
            Header = ScriptPresentation.Header(script), Icon = ScriptPresentation.Icon(script) };
        item.Click += async (_, args) => { if (args.Source == item && item.IsEnabled) { await execute(); } };
        DesktopHotkeys.BindScript(item, script.HotkeyCommandIdentifier);
        return item;
    }
    internal static object[] Build(IReadOnlyList<ScriptDefinition> scripts, Func<ScriptDefinition, bool> direct,
        Func<ScriptDefinition, Func<Task>> execute, bool enabled, TranslationCatalog translations, string category)
    {
        List<object> items = [];
        MenuItem run = OriginalMenuItems.Create(translations, category,
            category == "FileStatusList" ? "tsmiRunScript" : "runScriptToolStripMenuItem", "Run &script", "Console.png");
        foreach (ScriptDefinition script in scripts.Where(script => script.Enabled))
        {
            MenuItem item = Create(script, execute(script)); item.IsEnabled = enabled;
            if (direct(script)) { items.Add(item); } else { run.Items.Add(item); }
        }
        if (run.Items.Count > 0) { run.IsEnabled = enabled; items.Insert(0, run); }
        return items.Count == 0 ? [] : new object[] { new Separator() }.Concat(items).ToArray();
    }
}

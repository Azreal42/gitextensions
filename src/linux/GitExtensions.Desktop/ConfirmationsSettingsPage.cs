using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class ConfirmationsSettingsPage : UserControl
{
    private readonly ConfirmationsSettingsSession _session;
    private readonly TranslationCatalog _translations;
    private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.Ordinal);
    private readonly TextBlock _notice = new() { Name = "ConfirmationsNotice", TextWrapping = TextWrapping.Wrap };
    private ConfirmationsSettingsValues _baseline;

    internal ConfirmationsSettingsPage(ConfirmationsSettingsSession session, TranslationCatalog translations)
    {
        _session = session; _translations = translations; _baseline = session.Initial; Name = "ConfirmationsSettingsPage";
        string Text(string name, string fallback) => translations.Translate("ConfirmationsSettingsPage", name, "Text", fallback);
        StackPanel rows = new() { Name = "tlpnlConfirmations", Spacing = 1, Margin = new Thickness(5) };
        Dictionary<string, (string Name, string Text)> groups = new()
        {
            ["Commits"] = ("lblGroupCommits", "Commits:"), ["Branches"] = ("lblGroupBranches", "Branches:"),
            ["Stashes"] = ("lblGroupStashes", "Stash:"), ["Conflicts"] = ("lblGroupConflictResolution", "Rebase / conflict resolution:"),
            ["Submodules"] = ("lblGroupSubmodules", "Submodules:"), ["Worktrees"] = ("lblGroupWorktrees", "Worktrees:")
        };
        foreach (IGrouping<string, ConfirmationSetting> group in ConfirmationSettingsCatalog.Fields.GroupBy(field => field.Group))
        {
            var caption = groups[group.Key];
            rows.Children.Add(new TextBlock { Name = caption.Name, Text = Text(caption.Name, caption.Text), Margin = new Thickness(0, rows.Children.Count == 0 ? 0 : 12, 0, 2) });
            foreach (ConfirmationSetting field in group)
            {
                CheckBox choice = new()
                {
                    Name = field.Control, Content = new TextBlock { Text = Text(field.Control, field.Caption), TextWrapping = TextWrapping.Wrap },
                    IsThreeState = field.ThreeState, MinHeight = 24, Padding = default,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                bool? value = _baseline.Get(field.Name); choice.IsChecked = value.HasValue ? !value.Value : null;
                _choices.Add(field.Name, choice); rows.Children.Add(choice);
            }
        }
        RadioButton global = new() { Name = "ConfirmationsGlobalRB", Content = translations.Translate("Global"), IsChecked = true, MinHeight = 24 };
        Content = new StackPanel { Margin = new Thickness(8), Spacing = 6, Children =
        { global, ClassicChrome.Group("gbConfirmations", Text("gbConfirmations", "Confirm actions"), rows), _notice } };
        ShowErrors();
        AddHandler(KeyDownEvent, (_, args) =>
        { if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None && (args.Source is not Button || args.Source is CheckBox or RadioButton)) { args.Handled = true; } }, RoutingStrategies.Bubble);
    }

    internal ConfirmationsSettingsValues Draft
    {
        get
        {
            ConfirmationsSettingsValues values = _baseline;
            foreach (ConfirmationSetting setting in ConfirmationSettingsCatalog.Fields)
            {
                bool? check = _choices[setting.Name].IsChecked;
                // WinForms.Checked is true for both Checked and Indeterminate on bool fields.
                values = values.With(setting.Name, setting.Nullable ? check.HasValue ? !check.Value : null : check == false);
            }
            return values;
        }
    }
    internal void AcceptSaved(ConfirmationsSaveReceipt receipt)
    {
        foreach (ConfirmationSetting field in receipt.Fields)
        {
            bool? value = receipt.Values.Get(field.Name);
            _baseline = _baseline.With(field.Name, value);
            _choices[field.Name].IsChecked = value.HasValue ? !value.Value : null;
        }
        ShowErrors();
    }
    private void ShowErrors() => _notice.Text = _session.LoadErrors.Count == 0 ? null
        : _translations.Translate("Unable to read confirmation preferences.") + " " + string.Join(", ", _session.LoadErrors.Keys);
}

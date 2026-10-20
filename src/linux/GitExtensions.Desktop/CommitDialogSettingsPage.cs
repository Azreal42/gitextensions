using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The seven original global controls with real consumers in this port.</summary>
internal sealed class CommitDialogSettingsPage : UserControl
{
    private readonly CommitDialogSettingsSession _session;
    private readonly TranslationCatalog _translations;
    private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.Ordinal);
    private readonly NumericUpDown _previous = ClassicNumericInput.Create("_NO_TRANSLATE_CommitDialogNumberOfPreviousMessages", 1, 999,
        width: 94, clipValueToMinMax: false);
    private readonly TextBlock _notice = new() { Name = "CommitDialogSettingsNotice", TextWrapping = TextWrapping.Wrap };
    private CommitDialogSettingsValues _baseline;

    internal CommitDialogSettingsPage(CommitDialogSettingsSession session, TranslationCatalog translations)
    {
        _session = session; _translations = translations; _baseline = session.Initial; Name = "CommitDialogSettingsPage";
        string Text(string id, string fallback) => translations.Translate("CommitDialogSettingsPage", id, "Text", fallback)
            .Replace("&&", "&", StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal);
        StackPanel behaviour = new() { Name = "tableLayoutPanelBehaviour", Spacing = 2 };
        Add(behaviour, nameof(CommitDialogSettingsValues.ProvideAutocompletion));
        Add(behaviour, nameof(CommitDialogSettingsValues.EnsureCommitMessageSecondLineEmpty));
        _previous.Value = _baseline.NumberOfPreviousMessages;
        Grid number = new() { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 2) };
        number.Children.Add(new TextBlock { Name = "lblCommitDialogNumberOfPreviousMessages", Text = Text("lblCommitDialogNumberOfPreviousMessages", "Number of previous messages in commit dialog"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_previous, 1); number.Children.Add(_previous); behaviour.Children.Add(number);
        Add(behaviour, nameof(CommitDialogSettingsValues.RememberAmendCommitState));
        StackPanel buttons = new() { Name = "flowLayoutPanel1", Spacing = 2 };
        Add(buttons, nameof(CommitDialogSettingsValues.ShowCommitAndPush));
        Add(buttons, nameof(CommitDialogSettingsValues.ShowResetWorkTreeChanges));
        Add(buttons, nameof(CommitDialogSettingsValues.ShowResetAllChanges));
        behaviour.Children.Add(ClassicChrome.Group("grpAdditionalButtons", Text("grpAdditionalButtons", "Show additional buttons in commit button area"), buttons));
        Content = new StackPanel { Margin = new Thickness(8), Spacing = 6, Children =
        { ClassicChrome.Group("groupBoxBehaviour", Text("groupBoxBehaviour", "Behaviour"), behaviour), _notice } };
        ShowLoadErrors();
        AddHandler(KeyDownEvent, (_, args) =>
        { if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None && (args.Source is not Button || args.Source is CheckBox)) { args.Handled = true; } }, RoutingStrategies.Bubble);
        void Add(StackPanel panel, string name)
        {
            CommitDialogSetting choice = CommitDialogSettingsCatalog.Fields.Single(item => item.Name == name);
            CheckBox box = new() { Name = choice.Control, IsChecked = (bool)_baseline.Get(name), MinHeight = 24,
                Padding = default, HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock { Text = Text(choice.Control, choice.Caption), TextWrapping = TextWrapping.Wrap } };
            _choices.Add(name, box); panel.Children.Add(box);
        }
    }
    internal CommitDialogSettingsValues Draft
    {
        get
        {
            CommitDialogSettingsValues value = _baseline;
            foreach (CommitDialogSetting choice in CommitDialogSettingsCatalog.Fields.Where(item => !item.IsNumber))
            { value = value.With(choice.Name, _choices[choice.Name].IsChecked == true); }
            return value with { NumberOfPreviousMessages = _previous.Value is { } count && count >= int.MinValue && count <= int.MaxValue
                ? decimal.ToInt32(count) : _baseline.NumberOfPreviousMessages };
        }
    }
    internal bool Validate(out string? error)
    {
        error = null;
        if (_previous.Value is not { } count || count != decimal.Truncate(count)
            || (count != _baseline.NumberOfPreviousMessages && (count < 1 || count > 999)))
        {
            error = _translations.Translate("Choose between 1 and 999 previous commit messages.");
            _previous.Focus(); return false;
        }
        return true;
    }
    internal void AcceptSaved(CommitDialogSettingsSaveReceipt receipt)
    {
        foreach (CommitDialogSetting choice in receipt.Fields)
        {
            object value = receipt.Values.Get(choice.Name); _baseline = _baseline.With(choice.Name, value);
            if (choice.IsNumber) { _previous.Value = (int)value; }
            else { _choices[choice.Name].IsChecked = (bool)value; }
        }
        ShowLoadErrors();
    }
    private void ShowLoadErrors() => _notice.Text = _session.LoadErrors.Count == 0 ? null
        : _translations.Translate("Unable to read commit preferences.") + " " + string.Join("; ", _session.LoadErrors.Keys);
}

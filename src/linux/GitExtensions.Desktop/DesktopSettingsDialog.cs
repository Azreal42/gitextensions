using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class DesktopSettingsDialog : Window
{
    internal static readonly (GitRefsSortBy Value, string Label)[] ReferenceSortChoices =
    [
        (GitRefsSortBy.GitDefault, "Git default"), (GitRefsSortBy.AuthorDate, "Author date"),
        (GitRefsSortBy.CommitterDate, "Committer date"), (GitRefsSortBy.CreatorDate, "Creator date"),
        (GitRefsSortBy.TaggerDate, "Tagger date"), (GitRefsSortBy.RefName, "Alpha-numeric"),
        (GitRefsSortBy.VersionRefName, "Version"), (GitRefsSortBy.ObjectSize, "Object size"),
        (GitRefsSortBy.Upstream, "Originating remote")
    ];
    internal static string ReferenceOrderLabel(GitRefsSortOrder order) => order == GitRefsSortOrder.Ascending ? "A ↓ Z" : "Z ↑ A";
    public DesktopPreferences Result { get; private set; }
    public GitLogSort HistorySortResult { get; private set; }
    public bool OpenGitConfiguration { get; private set; }

    public DesktopSettingsDialog(DesktopPreferences initial, TranslationCatalog translations, bool canConfigureGit,
        GitLogSort historySort = GitLogSort.GitDefault)
    {
        Result = initial; HistorySortResult = historySort;
        Name = "DesktopSettings"; Title = translations.Translate("Settings");
        Width = 640; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ComboBox theme = new() { Name = "SettingsTheme", ItemsSource = new[] { "Light", "Dark", "System" }, SelectedItem = initial.Theme, MinWidth = 150 };
        NumericUpDown history = new() { Name = "SettingsHistoryLimit", Minimum = 1, Maximum = 5000, Value = Math.Clamp(initial.HistoryLimit, 1, 5000), FormatString = "0", Width = 150 };
        CheckBox normalise = new() { Name = "SettingsNormaliseBranches", Content = translations.Translate("Auto normalise branch name"), IsChecked = initial.AutoNormaliseBranchName };
        CheckBox advanced = new() { Name = "SettingsAdvancedOptions", Content = translations.Translate("Always show advanced options"), IsChecked = initial.AlwaysShowAdvancedOptions };
        TextBox symbol = new() { Name = "SettingsNormaliseSymbol", Text = initial.AutoNormaliseSymbol, Width = 150 };
        TextBlock notice = new() { Name = "SettingsNotice", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 6) };
        StackPanel fields = new() { Margin = new Thickness(12), Spacing = 10 };
        GeneralField("Theme", theme); GeneralField("Number of commits to load", history);
        fields.Children.Add(normalise); GeneralField("Replacement symbol", symbol); fields.Children.Add(advanced);
        fields.Children.Add(new TextBlock { Name = "TelemetryStatus", Text = translations.Translate("Telemetry is disabled. No usage data is sent."), TextWrapping = TextWrapping.Wrap });
        Button configuration = new() { Name = "OpenGitConfiguration", Content = translations.Translate("Git configuration") + "…", IsEnabled = canConfigureGit, HorizontalAlignment = HorizontalAlignment.Left };
        configuration.Click += (_, _) => { OpenGitConfiguration = true; Close(false); }; fields.Children.Add(configuration);

        ComboBox revisions = new() { Name = "_NO_TRANSLATE_cmbRevisionsSortBy", ItemsSource = historySort == GitLogSort.Date ? new[] { GitLogSort.GitDefault, GitLogSort.AuthorDate, GitLogSort.Topology, GitLogSort.Date } : new[] { GitLogSort.GitDefault, GitLogSort.AuthorDate, GitLogSort.Topology }, SelectedItem = historySort,
            ItemTemplate = new FuncDataTemplate<GitLogSort>((sort, _) => new TextBlock { Text = sort switch { GitLogSort.GitDefault => "Git default", GitLogSort.AuthorDate => "Author date", GitLogSort.Topology => "Topo (Ancestor)", _ => "Date order" } }) };
        ComboBox references = new() { Name = "_NO_TRANSLATE_cmbBranchesSortBy", ItemsSource = ReferenceSortChoices.Select(choice => choice.Value).ToArray(), SelectedItem = initial.ReferenceSorting.By,
            ItemTemplate = new FuncDataTemplate<GitRefsSortBy>((sort, _) => new TextBlock { Text = ReferenceSortChoices.Single(choice => choice.Value == sort).Label }) };
        ComboBox order = new() { Name = "_NO_TRANSLATE_cmbBranchesOrder", ItemsSource = Enum.GetValues<GitRefsSortOrder>(), SelectedItem = initial.ReferenceSorting.Order,
            ItemTemplate = new FuncDataTemplate<GitRefsSortOrder>((sort, _) => new TextBlock { Text = ReferenceOrderLabel(sort) }) };
        TextBox branches = new() { Name = "txtPrioBranchNames", Text = initial.PrioritizedBranchNames };
        TextBox remotes = new() { Name = "txtPrioRemoteNames", Text = initial.PrioritizedRemoteNames };
        Grid sorting = new() { Name = "SortingSettingsPage", Margin = new Thickness(12), ColumnDefinitions = new ColumnDefinitions("Auto,*,20"), RowDefinitions = new RowDefinitions("29,29,29,29,29") };
        SortingField(0, "Sort revisions by", revisions, "Sorting revisions may delay rendering of the revision graph.");
        SortingField(1, "Sort branches by", references);
        // Windows Settings leaves direction editable under Git default; only the tree menu disables it.
        SortingField(2, "Order branches", order);
        SortingField(3, "Prioritized branches", branches, "Regex to prioritize branch names in the left panel.\nThe branches matching the pattern will be shown before the others.\nSeparate the priorities with ';'.");
        SortingField(4, "Prioritized remotes", remotes, "Regex to prioritize remote names in the left panel.\nThe remotes matching the pattern will be shown before the others.\nSeparate the priorities with ';'.");
        TabItem generalTab = new() { Name = "SettingsGeneralTab", Header = translations.Translate("General"), Content = fields };
        TabItem sortingTab = new() { Name = "SettingsSortingTab", Header = translations.Translate("Sorting"), Content = sorting };
        TabControl tabs = new() { Name = "DesktopSettingsPages", MinHeight = 315, ItemsSource = new[] { generalTab, sortingTab }, SelectedItem = generalTab };
        Button save = new() { Name = "SaveDesktopSettings", Content = translations.Translate("OK"), MinWidth = 75, IsDefault = true };
        Button cancel = new() { Name = "CancelDesktopSettings", Content = translations.Translate("Cancel"), MinWidth = 75, IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        save.Click += (_, _) =>
        {
            if (history.Value is null || history.Value < 1 || history.Value > 5000 || (normalise.IsChecked == true && string.IsNullOrWhiteSpace(symbol.Text)))
            { tabs.SelectedItem = generalTab; notice.Text = translations.Translate("Enter a valid history limit and replacement symbol."); return; }
            foreach (TextBox input in new[] { branches, remotes })
            {
                try { GitReferencePriorities.Validate(input.Text ?? ""); }
                catch (ArgumentException error) { tabs.SelectedItem = sortingTab; notice.Text = error.Message; input.Focus(); return; }
            }
            Result = initial with
            {
                Theme = theme.SelectedItem as string ?? "Light", HistoryLimit = (int)history.Value,
                AutoNormaliseBranchName = normalise.IsChecked == true, AutoNormaliseSymbol = symbol.Text ?? "_", AlwaysShowAdvancedOptions = advanced.IsChecked == true,
                ReferenceSorting = new((GitRefsSortBy)references.SelectedItem!, (GitRefsSortOrder)order.SelectedItem!),
                PrioritizedBranchNames = branches.Text ?? "", PrioritizedRemoteNames = remotes.Text ?? ""
            };
            HistorySortResult = (GitLogSort)revisions.SelectedItem!;
            Close(true);
        };
        DockPanel root = new() { LastChildFill = true };
        StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(12, 6), Children = { save, cancel } };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); DockPanel.SetDock(notice, Dock.Bottom); root.Children.Add(notice); root.Children.Add(tabs); Content = root;
        Opened += (_, _) => { theme.Focus(); ModalWindowActivation.AfterMapping(this, () => IsVisible); };

        void GeneralField(string label, Control editor)
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = translations.Translate(label), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(editor, 1); row.Children.Add(editor); fields.Children.Add(row);
        }
        void SortingField(int row, string label, Control editor, string? help = null)
        {
            TextBlock caption = new() { Text = translations.Translate(label), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(caption, row); sorting.Children.Add(caption); editor.Margin = new Thickness(0, 2, 0, 2);
            editor.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(editor, row); Grid.SetColumn(editor, 1); sorting.Children.Add(editor);
            if (help is null) { return; }
            Control icon = GitExtensions.Linux.Desktop.Controls.OriginalMenuItems.Icon("information.png");
            icon.VerticalAlignment = VerticalAlignment.Center; icon.HorizontalAlignment = HorizontalAlignment.Center;
            ToolTip.SetTip(icon, translations.Translate(help)); ToolTip.SetTip(editor, translations.Translate(help));
            Grid.SetRow(icon, row); Grid.SetColumn(icon, 2); sorting.Children.Add(icon);
        }
    }
}

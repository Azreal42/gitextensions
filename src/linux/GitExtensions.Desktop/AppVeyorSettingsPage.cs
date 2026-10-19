using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>Original AppVeyorSettingsUserControl table over the shared scoped editor/draft machinery.</summary>
internal sealed class AppVeyorSettingsPage : BuildServerScopedSettingsPage
{
    private readonly TextBox _account, _token, _project;
    private readonly CheckBox _tests;
    private readonly HashSet<GitDistributedSettingsScope> _projectEdited = [];
    private readonly HashSet<GitDistributedSettingsScope> _activated = [];
    private readonly Dictionary<GitDistributedSettingsScope, string> _defaults = [];
    private bool _rendering;

    internal AppVeyorSettingsPage(GitHubActionsSettingsSession session, TranslationCatalog translations, bool embedded = false)
        : base(session, translations, "AppVeyorSettingsPage")
    {
        _account = Text(AppVeyorSettings.AccountNameKey, "AppVeyorAccountName", "Account name");
        _token = Text(AppVeyorSettings.AccountTokenKey, "AppVeyorAccountToken", "Api token", password: true); _token.RevealPassword = false;
        _project = Text(AppVeyorSettings.ProjectNameKey, "AppVeyorProjectName", "Project(s) Name(s)");
        const string testsCaption = "Display test results in build status summary for each build result (network intensive!)";
        Boolean(AppVeyorSettings.LoadTestsResultsKey, "cbLoadTestResults", testsCaption);
        _tests = this.GetLogicalDescendants().OfType<CheckBox>().Single(control => control.Name == "cbLoadTestResults");
        Grid table = new() { Name = "tableLayoutPanel1", ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto,Auto,Auto,Auto,Auto,Auto") };
        MoveEditor(_account, 0, "lblAccountName", "Account name", 1);
        MoveEditor(_token, 1, "lblApiToken", "Api token", 2);
        MoveEditor(_project, 3, "lblProjects", "Project(s) Name(s)", 3);
        Help(2, "label2", "Token used to be able to query AppVeyor rest Api. You will find out in the user account menu.");
        Help(4, "label1", "If you want to use the result of different projects, separate your different projects names by a '|'.");
        ((Panel)_tests.Parent!).Children.Remove(_tests);
        _tests.TabIndex = 4; _tests.Margin = new Thickness(3, 2); _tests.Height = double.NaN;
        _tests.MinHeight = DesktopFontMetrics.CompactHeight(DesktopFonts.For(DesktopFontRole.Application), 19, 2);
        _tests.HorizontalAlignment = HorizontalAlignment.Stretch;
        _tests.Content = new TextBlock { Text = T("cbLoadTestResults", testsCaption), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(_tests, 1); Grid.SetRow(_tests, 5); table.Children.Add(_tests);
        WrapPanel empty = new() { Name = "flowLayoutPanel1", Margin = default };
        Grid.SetColumn(empty, 1); Grid.SetRow(empty, 6); table.Children.Add(empty); Add(table);
        _project.PropertyChanged += (_, args) =>
        { if (!_rendering && args.Property == TextBox.TextProperty) { _projectEdited.Add(CurrentScope); } };
        if (embedded) { HideHeader(); }
        ShowLoadState();

        void MoveEditor(TextBox box, int row, string labelName, string caption, int tab)
        {
            Grid line = (Grid)box.Parent!; ((Panel)line.Parent!).Children.Remove(line);
            TextBlock label = line.Children.OfType<TextBlock>().Single(); line.Children.Remove(label); line.Children.Remove(box);
            label.Name = labelName; label.Text = T(labelName, caption); label.Margin = new Thickness(3, 0);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            box.Margin = new Thickness(3, 2); box.Height = box.MinHeight = DesktopFontMetrics.Height(DesktopFonts.For(DesktopFontRole.Application), 23, 6);
            box.TabIndex = tab; box.AcceptsReturn = box.AcceptsTab = false;
            // FormSettings has no AcceptButton; this single-line editor must not activate the Linux host's default OK.
            box.AddHandler(KeyDownEvent, (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; } }, RoutingStrategies.Tunnel);
            Grid.SetRow(label, row); Grid.SetColumn(box, 1); Grid.SetRow(box, row); table.Children.Add(label); table.Children.Add(box);
        }
        void Help(int row, string name, string caption)
        {
            TextBlock text = new() { Name = name, Text = T(name, caption), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(3, 0, 3, 10), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1); Grid.SetRow(text, row); table.Children.Add(text);
        }
    }

    internal void SetProviderActive(bool active, bool userInitiated = false)
    {
        if (active && userInitiated && Session.Loaded && CurrentScope != GitDistributedSettingsScope.Effective)
        { _activated.Add(CurrentScope); }
    }

    internal override void ShowLoadState()
    {
        _rendering = true;
        try
        {
            base.ShowLoadState();
            if (_project is not null && Session.Loaded && !_projectEdited.Contains(CurrentScope)
                && Session.Read(CurrentScope, AppVeyorSettings.ProjectNameKey) is null)
            {
                string suggestion = Session.DefaultProjectName; _defaults[CurrentScope] = suggestion;
                LoadText(_project, suggestion);
            }
        }
        finally { _rendering = false; }
    }

    internal override void PrepareSave()
    {
        foreach (GitDistributedSettingsScope scope in new[] { GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed, GitDistributedSettingsScope.Global })
        {
            Session.PrepareAppVeyorProject(scope, _defaults.GetValueOrDefault(scope, Session.DefaultProjectName),
                _projectEdited.Contains(scope), _activated.Contains(scope));
        }
    }

    private string T(string item, string source) => Translations.Translate("AppVeyorSettingsUserControl", item, "Text", source);
}

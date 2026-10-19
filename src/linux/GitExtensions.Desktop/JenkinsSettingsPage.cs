using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original JenkinsSettingsUserControl three-row table, using the shared scoped draft.</summary>
internal sealed class JenkinsSettingsPage : BuildServerScopedSettingsPage
{
    private readonly TextBox _server, _project, _ignore;
    private readonly HashSet<GitDistributedSettingsScope> _projectEdited = [];
    private readonly HashSet<GitDistributedSettingsScope> _activated = [];
    private readonly Dictionary<GitDistributedSettingsScope, string> _defaults = [];
    private bool _rendering;

    internal JenkinsSettingsPage(GitHubActionsSettingsSession session, TranslationCatalog translations, bool embedded = false)
        : base(session, translations, "JenkinsSettingsPage")
    {
        _server = Text(JenkinsSettings.ServerUrlKey, "JenkinsServerUrl", "Jenkins server URL");
        _project = Text(JenkinsSettings.ProjectNameKey, "JenkinsProjectName", "Project name");
        _ignore = Text(JenkinsSettings.IgnoreBuildBranchKey, "IgnoreBuildBranch", "Ignore build for branch");
        Grid table = new() { Name = "tableLayoutPanel1", ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto,Auto"), Margin = default };
        Move(_server, 0, "lblJenkinsServerUrl", "Jenkins server URL", 1);
        Move(_project, 1, "lblProjectName", "Project name", 3);
        Move(_ignore, 2, "lblIgnoreBuildBranch", "Ignore build for branch", 5);
        Add(table);
        _project.PropertyChanged += (_, args) =>
        { if (!_rendering && args.Property == TextBox.TextProperty) { _projectEdited.Add(CurrentScope); } };
        if (embedded) { HideHeader(); }
        ShowLoadState();

        void Move(TextBox box, int row, string labelName, string caption, int tab)
        {
            Grid line = (Grid)box.Parent!; ((Panel)line.Parent!).Children.Remove(line);
            TextBlock label = line.Children.OfType<TextBlock>().Single(); line.Children.Remove(label); line.Children.Remove(box);
            label.Name = labelName; label.Text = Translations.Translate("JenkinsSettingsUserControl", labelName, "Text", caption);
            label.Margin = new Thickness(3, 0); label.HorizontalAlignment = HorizontalAlignment.Left;
            box.Margin = new Thickness(3); box.Height = box.MinHeight = DesktopFontMetrics.Height(DesktopFonts.For(DesktopFontRole.Application), 23, 6);
            box.TabIndex = tab; box.AcceptsReturn = box.AcceptsTab = false;
            // Original FormSettings has no AcceptButton: Enter in a single-line field does not accept the host.
            box.AddHandler(KeyDownEvent, (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; } }, RoutingStrategies.Tunnel);
            Grid.SetRow(label, row); Grid.SetColumn(box, 1); Grid.SetRow(box, row); table.Children.Add(label); table.Children.Add(box);
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
                && Session.Read(CurrentScope, JenkinsSettings.ProjectNameKey) is null)
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
            Session.PrepareJenkinsProject(scope, _defaults.GetValueOrDefault(scope, Session.DefaultProjectName),
                _projectEdited.Contains(scope), _activated.Contains(scope));
        }
    }
}

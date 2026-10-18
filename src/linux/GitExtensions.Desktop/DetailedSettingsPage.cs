using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Desktop.Controls.Graph;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The Windows Detailed groups with a read-only Effective view and explicit editable scopes.</summary>
internal sealed class DetailedSettingsPage : UserControl
{
    private readonly DetailedSettingsSession _session;
    private readonly TranslationCatalog _translations;
    private readonly CheckBox _mergeLanes, _diagonals, _straighten, _remote, _log;
    private readonly TextBox _count;
    private readonly Control _graphGroup, _pushGroup, _mergeGroup;
    private readonly Dictionary<GitDistributedSettingsScope, RadioButton> _scopes = [];
    private readonly HashSet<string> _dirty = [];
    private readonly TextBlock _notice = new() { Name = "DetailedSettingsNotice", TextWrapping = TextWrapping.Wrap };
    private GitDistributedSettingsScope _scope;
    private bool _loading;
    internal DetailedSettingsSession Session => _session;

    internal DetailedSettingsPage(DetailedSettingsSession session, TranslationCatalog translations)
    {
        _session = session; _translations = translations; _scope = session.InitialScope; Name = "DetailedSettingsPage";
        WrapPanel header = new() { Name = "DetailedSettingsHeader", Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach ((GitDistributedSettingsScope scope, string item, string caption) in new[]
        {
            (GitDistributedSettingsScope.Effective, "EffectiveRB", "Effective"), (GitDistributedSettingsScope.Local, "LocalRB", "Local for current repository"),
            (GitDistributedSettingsScope.Distributed, "DistributedRB", "Distributed with current repository"), (GitDistributedSettingsScope.Global, "GlobalRB", "Global for all repositories")
        })
        {
            RadioButton choice = new() { Name = "Detailed" + item, GroupName = "DetailedSettingsScope", Margin = new Thickness(0, 0, 12, 0),
                Content = translations.Translate("SettingsPageHeader", item, "Text", caption) };
            ReferenceDialogControls.Compact(choice); _scopes.Add(scope, choice); header.Children.Add(choice);
            choice.IsCheckedChanged += (_, _) => { if (!_loading && choice.IsChecked == true) { Flush(); _scope = scope; LoadView(); } };
        }
        _mergeLanes = Check("chkMergeGraphLanesHavingCommonParent", "Merge graph lanes having common parent");
        _diagonals = Check("chkRenderGraphWithDiagonals", "Render graph with diagonals");
        _straighten = Check("chkStraightenGraphDiagonals", "Straighten graph diagonals");
        _remote = Check("chkRemotesFromServer", "Get remote branches directly from the remote");
        _log = Check("addLogMessages", "Add log messages");
        _count = new() { Name = "nbMessages", Width = 100, HorizontalAlignment = HorizontalAlignment.Left, Height = 24, MinHeight = 24,
            Watermark = translations.Translate("DistributedSettingsPage", "_numberSettingPlaceholder", "Text", "no value set") };
        foreach (CheckBox box in new[] { _mergeLanes, _diagonals, _straighten })
        { box.IsCheckedChanged += (_, _) => { if (!_loading) { _session.Graph = new(_mergeLanes.IsChecked == true, _diagonals.IsChecked == true, _straighten.IsChecked == true); } }; }
        _remote.IsCheckedChanged += (_, _) => { if (!_loading) { _dirty.Add(DetailedRepositorySettings.RemoteBranches); } };
        _log.IsCheckedChanged += (_, _) => { if (!_loading) { _dirty.Add(DetailedRepositorySettings.AddMergeLog); } };
        _count.PropertyChanged += (_, args) =>
        {
            if (!_loading && args.Property == TextBox.TextProperty) { _dirty.Add(DetailedRepositorySettings.MergeLogCount); MarkNumber(); }
        };
        _graphGroup = ClassicChrome.Group("gbRevisionGraph", Text("gbRevisionGraph", "Revision graph"),
            new StackPanel { Spacing = 3, Children = { _mergeLanes, _diagonals, _straighten } });
        _pushGroup = ClassicChrome.Group("PushWindowGB", Text("PushWindowGB", "Push window"), _remote);
        _mergeGroup = ClassicChrome.Group("mergeWindowGroup", Text("mergeWindowGroup", "Merge window"),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _log, _count } });
        Content = new StackPanel { Spacing = 5, Children = { header, _graphGroup, _pushGroup, _mergeGroup, _notice } };
        AddHandler(KeyDownEvent, (_, args) =>
        { if (args.Key == Key.Enter && args.Source is TextBox) { args.Handled = true; } }, RoutingStrategies.Bubble);
        LoadView();
    }
    private CheckBox Check(string item, string source)
    {
        CheckBox check = new() { Name = item, Content = _translations.TranslateAccessKeys("DetailedSettingsPage", item, "Text", source) };
        ReferenceDialogControls.Compact(check); return check;
    }
    private string Text(string item, string source) => _translations.Translate("DetailedSettingsPage", item, "Text", source);
    internal void PrepareSave() => Flush();
    internal void AcceptSaved() => LoadView();
    internal void ShowLoadState() => LoadView();
    private void Flush()
    {
        if (_loading || !_session.Loaded || _scope == GitDistributedSettingsScope.Effective) { return; }
        if (_dirty.Contains(DetailedRepositorySettings.RemoteBranches))
        { _session.Set(_scope, DetailedRepositorySettings.RemoteBranches, DetailedRepositorySettings.BooleanText(_remote.IsChecked)); }
        if (_dirty.Contains(DetailedRepositorySettings.AddMergeLog))
        { _session.Set(_scope, DetailedRepositorySettings.AddMergeLog, DetailedRepositorySettings.BooleanText(_log.IsChecked)); }
        if (_dirty.Contains(DetailedRepositorySettings.MergeLogCount))
        { _session.Set(_scope, DetailedRepositorySettings.MergeLogCount, DetailedRepositorySettings.NumberText(_count.Text)); }
        _dirty.Clear();
    }
    private void LoadView()
    {
        _loading = true;
        try
        {
            foreach ((GitDistributedSettingsScope scope, RadioButton choice) in _scopes)
            {
                choice.IsVisible = scope == GitDistributedSettingsScope.Global || _session.HasRepository;
                choice.IsEnabled = scope != GitDistributedSettingsScope.Distributed || _session.HasDistributed;
                choice.IsChecked = scope == _scope; ToolTip.SetTip(choice, _session.ScopePath(scope));
            }
            RevisionGraphOptions graph = _session.Graph;
            _mergeLanes.IsChecked = graph.MergeGraphLanesHavingCommonParent; _diagonals.IsChecked = graph.RenderGraphWithDiagonals;
            _straighten.IsChecked = graph.StraightenGraphDiagonals;
            DetailedScopeValues values = _session.Read(_scope);
            _remote.IsThreeState = _log.IsThreeState = _scope != GitDistributedSettingsScope.Effective;
            _remote.IsChecked = values.RemoteBranches; _log.IsChecked = values.AddMergeLog; _count.Text = values.LogCount;
            _graphGroup.IsEnabled = _scope == GitDistributedSettingsScope.Global;
            _pushGroup.IsEnabled = _mergeGroup.IsEnabled = _session.Loaded && _scope != GitDistributedSettingsScope.Effective;
            _notice.Text = _session.LoadError?.Message ?? (_scope == GitDistributedSettingsScope.Effective
                ? _translations.Translate("Effective settings are read-only. Select Local, Distributed or Global to edit.") : "");
            _notice.IsVisible = !string.IsNullOrEmpty(_notice.Text); _dirty.Clear(); MarkNumber();
        }
        finally { _loading = false; }
    }
    private void MarkNumber()
    {
        bool invalid = !string.IsNullOrWhiteSpace(_count.Text) && DetailedRepositorySettings.Number(_count.Text) is null;
        if (invalid) { _count.Foreground = Brushes.Red; }
        else { _count.ClearValue(TextBox.ForegroundProperty); }
    }
}

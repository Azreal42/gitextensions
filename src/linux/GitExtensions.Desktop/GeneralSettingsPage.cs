using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>Original General controls; publication and repository work belong to the Settings host.</summary>
internal sealed class GeneralSettingsPage : UserControl
{
    private static readonly GitPullAction[] PullActions =
    [GitPullAction.None, GitPullAction.Merge, GitPullAction.Rebase, GitPullAction.Fetch, GitPullAction.FetchAll, GitPullAction.FetchPruneAll];
    private readonly TranslationCatalog _translations;
    private readonly CheckBox _toolbar, _artificial, _submodules, _stashCount, _aheadBehind, _checkChanges, _limit;
    private readonly CheckBox _closeProcess, _histogram, _stashUntracked, _updateModules, _follow, _exact, _recent, _browseHistory;
    private readonly NumericUpDown _maxCommits, _quickSearch;
    private readonly ComboBox _clone, _pull;
    private readonly Button _browse;
    private readonly TextBlock _notice = new() { Name = "GeneralSettingsNotice", IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private GeneralSettingsValues _accepted;
    private Control? _invalidControl;
    private bool _loading, _picking;
    private long _pickerRevision;

    internal Func<Window, string, string, Task<string?>> BrowseFolderAsync { get; set; }
        = (owner, title, initialPath) => ExportDialogControls.FolderAsync(owner, title, initialPath);

    internal GeneralSettingsValues Draft => _accepted with
    {
        ShowGitStatusInBrowseToolbar = _toolbar.IsChecked == true,
        ShowGitStatusForArtificialCommits = _artificial.IsChecked == true,
        ShowSubmoduleStatus = _submodules.IsEnabled && _submodules.IsChecked == true,
        ShowStashCount = _stashCount.IsChecked == true,
        ShowAheadBehindData = _aheadBehind.IsChecked == true,
        CheckForUncommittedChangesInCheckoutBranch = _checkChanges.IsChecked == true,
        MaxRevisionGraphCommits = _limit.IsChecked == true ? (int)(_maxCommits.Value ?? _accepted.MaxRevisionGraphCommits) : 0,
        CloseProcessDialog = _closeProcess.IsChecked == true,
        UseHistogramDiffAlgorithm = _histogram.IsChecked == true,
        IncludeUntrackedFilesInAutoStash = _stashUntracked.IsChecked == true,
        UpdateSubmodulesOnCheckout = _updateModules.IsChecked,
        FollowRenamesInFileHistory = _follow.IsChecked == true,
        FollowRenamesInFileHistoryExact = _exact.IsChecked == true,
        StartWithRecentWorkingDir = _recent.IsChecked == true,
        DefaultCloneDestinationPath = _clone.Text ?? "",
        DefaultPullAction = _pull.SelectedItem is GitPullAction action ? action : _accepted.DefaultPullAction,
        RevisionGridQuickSearchTimeout = (int)(_quickSearch.Value ?? _accepted.RevisionGridQuickSearchTimeout),
        UseBrowseForFileHistory = _browseHistory.IsChecked == true
    };

    internal GeneralSettingsPage(GeneralSettingsValues initial, TranslationCatalog translations,
        IReadOnlyList<string>? cloneDestinations = null)
    {
        ArgumentNullException.ThrowIfNull(initial); ArgumentNullException.ThrowIfNull(translations);
        _translations = translations; _accepted = initial; Name = "GeneralSettingsPage";
        _toolbar = Check("chkShowGitStatusInToolbar", "Show number of changed files on commit button");
        _artificial = Check("chkShowGitStatusForArtificialCommits", "Show number of changed files for artificial commits");
        _submodules = Check("chkShowSubmoduleStatusInBrowse", "Show submodule status in browse menu");
        _stashCount = Check("chkShowStashCountInBrowseWindow", "Show stash count on status bar in browse window");
        _aheadBehind = Check("chkShowAheadBehindDataInBrowseWindow", "Show ahead and behind information on status bar in browse window");
        _checkChanges = Check("chkCheckForUncommittedChangesInCheckoutBranch", "Check for uncommitted changes in checkout branch dialog");
        _limit = Check("lblCommitsLimit", "Limit number of commits to be loaded");
        _maxCommits = Number("_NO_TRANSLATE_MaxCommits", 0, 10000);
        Grid performance = Rows("tlpnlPerformance", "*,100");
        foreach (CheckBox check in new[] { _toolbar, _artificial, _submodules, _stashCount, _aheadBehind, _checkChanges })
        { AddWide(performance, check, 2); }
        int limitRow = AddRow(performance); Put(performance, _limit, limitRow, 0); Put(performance, _maxCommits, limitRow, 1);
        _toolbar.IsCheckedChanged += (_, _) => UpdateSubmoduleStatus();
        _artificial.IsCheckedChanged += (_, _) => UpdateSubmoduleStatus();
        _limit.IsCheckedChanged += (_, _) => _maxCommits.IsEnabled = _limit.IsChecked == true;

        _closeProcess = Check("chkCloseProcessDialog", "Close Process dialog when process succeeds");
        CheckBox console = Unavailable("chkShowGitCommandLine", "Show console window when executing git process",
            "Git commands use the Linux process output dialog. A separate console window is not available.");
        _histogram = Check("chkUseHistogramDiffAlgorithm", "Use histogram diff algorithm");
        _stashUntracked = Check("chkStashUntrackedFiles", "Include untracked files in autostash");
        _updateModules = Check("chkUpdateModules", "Update submodules on checkout"); _updateModules.IsThreeState = true;
        _follow = Check("chkFollowRenamesInFileHistory", "Follow renames in file history");
        _exact = Check("chkFollowRenamesInFileHistoryExact", "Follow exact renames and copies only");
        _recent = Check("chkStartWithRecentWorkingDir", "Open last working directory on startup");
        _browseHistory = Check("SettingsUseBrowseForFileHistory", "Show file history in the main window", contextual: false);
        _clone = RepositorySourceControls.EditableChoice("cbDefaultCloneDestination");
        _clone.ItemsSource = (cloneDestinations ?? []).Where(path => !string.IsNullOrEmpty(path)).Distinct(StringComparer.Ordinal).ToArray();
        _clone.PropertyChanged += (_, args) => { if (!_loading && args.Property == ComboBox.TextProperty) { ++_pickerRevision; } };
        _browse = new() { Name = "btnDefaultDestinationBrowse", Content = Text("btnDefaultDestinationBrowse", "Browse"),
            MinWidth = 65, Height = 24, MinHeight = 24, Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
        _browse.Click += async (_, _) => await BrowseAsync();
        _pull = new() { Name = "cboDefaultPullAction", ItemsSource = PullActions, Height = 24, MinHeight = 24,
            HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160, Padding = new Thickness(4, 0),
            ItemTemplate = new FuncDataTemplate<GitPullAction>((action, _) => new TextBlock { Text = PullText(action) }) };
        _quickSearch = Number("RevisionGridQuickSearchTimeout", 100, 100);
        Grid behaviour = Rows("tlpnlBehaviour", "Auto,*,Auto"); behaviour.ColumnSpacing = 8;
        foreach (CheckBox check in new[] { _closeProcess, console, _histogram, _stashUntracked, _updateModules })
        { AddWide(behaviour, check, 3); }
        int followRow = AddRow(behaviour); Put(behaviour, _follow, followRow, 0); Put(behaviour, _exact, followRow, 1, 2);
        AddWide(behaviour, _recent, 3);
        int cloneRow = AddRow(behaviour);
        Put(behaviour, Caption("lblDefaultCloneDestination", "Default clone destination"), cloneRow, 0);
        Put(behaviour, _clone, cloneRow, 1); Put(behaviour, _browse, cloneRow, 2);
        int pullRow = AddRow(behaviour);
        Put(behaviour, Caption("lblDefaultPullAction", "Default pull action"), pullRow, 0); Put(behaviour, _pull, pullRow, 1, 2);
        int searchRow = AddRow(behaviour);
        Put(behaviour, Caption("lblQuickSearchTimeout", "Revision grid quick search timeout [ms]"), searchRow, 0);
        Put(behaviour, _quickSearch, searchRow, 1, 2);
        AddWide(behaviour, _browseHistory, 3);

        CheckBox telemetry = Unavailable("chkTelemetry", "Yes, I allow telemetry!", "Telemetry is disabled. No usage data is sent.");
        HyperlinkButton privacy = new() { Name = "llblTelemetryPrivacyLink", Content = Text("llblTelemetryPrivacyLink", "Why and what is captured?"),
            NavigateUri = new Uri("https://github.com/gitextensions/gitextensions/blob/master/setup/assets/PrivacyPolicy.md"),
            Padding = default, MinHeight = 24, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(privacy, translations.Translate("Privacy policy of the original Windows application. Telemetry is disabled in this Linux version."));
        Grid telemetryRows = Rows("tlpnlTelemetry", "*,*"); telemetryRows.ColumnSpacing = 8;
        int telemetryRow = AddRow(telemetryRows); Put(telemetryRows, telemetry, telemetryRow, 0); Put(telemetryRows, privacy, telemetryRow, 1);
        AddWide(telemetryRows, new TextBlock { Name = "TelemetryStatus", Text = translations.Translate("Telemetry is disabled. No usage data is sent."),
            TextWrapping = TextWrapping.Wrap }, 2);

        StackPanel body = new() { Name = "tlpnlMain", Spacing = 5, Children =
        {
            ClassicChrome.Group("groupBoxPerformance", Text("groupBoxPerformance", "Performance"), performance),
            ClassicChrome.Group("groupBoxBehaviour", Text("groupBoxBehaviour", "Behaviour"), behaviour),
            ClassicChrome.Group("groupBoxTelemetry", Text("groupBoxTelemetry", "Telemetry"), telemetryRows), _notice
        } };
        Content = body;
        _ = RepositorySourceControls.CompleteHistory(_clone, body);
        // Let the choice/editor consume Enter first. An accepted suggestion or
        // numeric edit must not also invoke the Settings window's default OK.
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None && args.Source is not Button)
            { args.Handled = true; }
        }, RoutingStrategies.Bubble);
        DetachedFromVisualTree += (_, _) => ++_pickerRevision;
        AcceptSaved(initial);
    }

    internal bool Validate(out string? error)
    {
        _invalidControl = null;
        if (_limit.IsChecked == true && !IsInteger(_maxCommits, 0, 1000000))
        { return Invalid(_maxCommits, "Enter a whole number of commits between 0 and 1,000,000.", out error); }
        if (!IsInteger(_quickSearch, 100, 1000000))
        { return Invalid(_quickSearch, "Enter a quick search timeout between 100 and 1,000,000 milliseconds.", out error); }
        if (_pull.SelectedItem is not GitPullAction action || !PullActions.Contains(action))
        { return Invalid(_pull, "Select a default pull action.", out error); }
        if ((_clone.Text ?? "").Contains('\0'))
        { return Invalid(_clone, "The clone destination must not contain a NUL character.", out error); }
        // A click on Apply can precede an editor's normal focus-loss commit.
        // Publish the validated text, never the numeric control's older value.
        if (_limit.IsChecked == true) { CommitNumber(_maxCommits); }
        CommitNumber(_quickSearch);
        ShowError(null); error = null; return true;
    }

    internal void AcceptSaved(GeneralSettingsValues published)
    {
        ArgumentNullException.ThrowIfNull(published);
        _loading = true; ++_pickerRevision;
        try
        {
            _accepted = published;
            _toolbar.IsChecked = published.ShowGitStatusInBrowseToolbar; _artificial.IsChecked = published.ShowGitStatusForArtificialCommits;
            _submodules.IsChecked = published.ShowSubmoduleStatus; _stashCount.IsChecked = published.ShowStashCount;
            _aheadBehind.IsChecked = published.ShowAheadBehindData; _checkChanges.IsChecked = published.CheckForUncommittedChangesInCheckoutBranch;
            _maxCommits.Value = Math.Clamp(published.MaxRevisionGraphCommits, 0, 1000000);
            _limit.IsChecked = published.MaxRevisionGraphCommits != 0; _maxCommits.IsEnabled = _limit.IsChecked == true;
            _closeProcess.IsChecked = published.CloseProcessDialog; _histogram.IsChecked = published.UseHistogramDiffAlgorithm;
            _stashUntracked.IsChecked = published.IncludeUntrackedFilesInAutoStash; _updateModules.IsChecked = published.UpdateSubmodulesOnCheckout;
            _follow.IsChecked = published.FollowRenamesInFileHistory; _exact.IsChecked = published.FollowRenamesInFileHistoryExact;
            _recent.IsChecked = published.StartWithRecentWorkingDir; _clone.SelectedIndex = -1; _clone.Text = published.DefaultCloneDestinationPath;
            _pull.SelectedItem = published.DefaultPullAction == GitPullAction.Default ? GitPullAction.None : published.DefaultPullAction;
            _quickSearch.Value = Math.Clamp(published.RevisionGridQuickSearchTimeout, 100, 1000000);
            _browseHistory.IsChecked = published.UseBrowseForFileHistory;
        }
        finally { _loading = false; }
        UpdateSubmoduleStatus(); ShowError(null);
    }

    private async Task BrowseAsync()
    {
        if (_picking || TopLevel.GetTopLevel(this) is not Window owner || !owner.IsVisible) { return; }
        _picking = true; _browse.IsEnabled = false;
        string before = _clone.Text ?? ""; long revision = _pickerRevision;
        try
        {
            string? path = await BrowseFolderAsync(owner, Text("lblDefaultCloneDestination", "Default clone destination"), before);
            if (path is not null && owner.IsVisible && TopLevel.GetTopLevel(this) == owner && _pickerRevision == revision && (_clone.Text ?? "") == before)
            { _clone.SelectedIndex = -1; _clone.Text = path; ShowError(null); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        { if (owner.IsVisible && TopLevel.GetTopLevel(this) == owner && _pickerRevision == revision) { ShowError(error.Message); } }
        finally { _picking = false; _browse.IsEnabled = true; }
    }

    private void UpdateSubmoduleStatus()
    {
        if (_loading) { return; }
        _submodules.IsEnabled = _toolbar.IsChecked == true || _artificial.IsChecked == true;
        if (!_submodules.IsEnabled) { _submodules.IsChecked = false; }
    }

    private CheckBox Check(string name, string source, bool contextual = true)
        => new() { Name = name, MinHeight = 23, VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = contextual ? Text(name, source) : _translations.Translate(source), TextWrapping = TextWrapping.Wrap } };
    private CheckBox Unavailable(string name, string source, string explanation)
    {
        CheckBox check = Check(name, source);
        ((TextBlock)check.Content!).Text += " (" + _translations.Translate("unavailable on Linux") + ")";
        check.IsEnabled = false; check.IsChecked = false;
        ToolTip.SetTip(check, _translations.Translate(explanation)); return check;
    }
    private TextBlock Caption(string name, string source)
        => new() { Name = name, Text = Text(name, source), TextWrapping = TextWrapping.Wrap,
            MaxWidth = 285, VerticalAlignment = VerticalAlignment.Center };
    private static NumericUpDown Number(string name, int minimum, int increment)
    {
        NumericUpDown number = ClassicNumericInput.Create(name, minimum, 1000000, increment, 100, "N0", clipValueToMinMax: false);
        number.HorizontalAlignment = HorizontalAlignment.Left; number.VerticalAlignment = VerticalAlignment.Center;
        number.TextAlignment = TextAlignment.Right;
        return number;
    }
    private static Grid Rows(string name, string columns) => new() { Name = name, ColumnDefinitions = new(columns), RowSpacing = 0 };
    private static int AddRow(Grid grid) { int row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); return row; }
    private static void AddWide(Grid grid, Control control, int span) => Put(grid, control, AddRow(grid), 0, span);
    private static void Put(Grid grid, Control control, int row, int column, int span = 1)
    { Grid.SetRow(control, row); Grid.SetColumn(control, column); Grid.SetColumnSpan(control, span); grid.Children.Add(control); }
    private string Text(string key, string source) => _translations.Translate("GeneralSettingsPage", key, "Text", source);
    private string PullText(GitPullAction action) => action switch
    {
        GitPullAction.None => Text("_openPullDialog", "Open pull dialog"), GitPullAction.Merge => Text("_pullMerge", "Pull - merge"),
        GitPullAction.Rebase => Text("_pullRebase", "Pull - rebase"), GitPullAction.Fetch => Text("_fetch", "Fetch"),
        GitPullAction.FetchAll => Text("_fetchAll", "Fetch all"), GitPullAction.FetchPruneAll => Text("_fetchAndPruneAll", "Fetch and prune all"), _ => ""
    };
    private static bool IsInteger(NumericUpDown input, int minimum, int maximum)
    {
        decimal? value = input.Value;
        if (input.Text is string text)
        {
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal parsed)) { return false; }
            value = parsed;
        }
        return value is decimal number && number >= minimum && number <= maximum && decimal.Truncate(number) == number;
    }
    private static void CommitNumber(NumericUpDown input)
    {
        if (input.Text is string text && decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal number))
        { input.Value = number; }
    }
    internal void FocusInvalidControl()
    {
        Control? target = _invalidControl is NumericUpDown number
            ? number.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() : _invalidControl;
        if (target?.IsEffectivelyVisible == true && target.IsEffectivelyEnabled)
        { target.BringIntoView(); target.Focus(); }
    }
    private bool Invalid(Control control, string message, out string? error)
    { _invalidControl = control; error = _translations.Translate(message); ShowError(error); FocusInvalidControl(); return false; }
    private void ShowError(string? error) { _notice.Text = error; _notice.IsVisible = !string.IsNullOrEmpty(error); }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Extensibility.Git;
using System.Runtime.ExceptionServices;

namespace GitExtensions.Linux.Desktop;

/// <summary>The branches, tags and multiple-branch workflows of the original FormPush.</summary>
public sealed class PushDialog : RepositoryDialog
{
    private const string AllRefs = "[ All ]";
    private readonly PushPreferencesStore _store = new();
    private readonly CloneSourcesStore _destinationHistory = new();
    private readonly Func<Window, CancellationToken, Task>? _afterSubmoduleUpdateAsync;
    private PushPreferences _preferences;
    private readonly RadioButton _toRemote = new() { Name = "PushToRemote", GroupName = "PushDestinationMode", IsChecked = true };
    private readonly RadioButton _toUrl = new() { Name = "PushToUrl", GroupName = "PushDestinationMode" };
    private readonly ComboBox _remote = ReferenceDialogControls.Choice("PushRemote");
    private readonly AutoCompleteBox _url = Input("PushDestination");
    private readonly AutoCompleteBox _source = Input("PushBranch");
    private readonly AutoCompleteBox _destination = Input("PushRemoteBranch");
    private readonly AutoCompleteBox _tag = Input("PushTag");
    private readonly ComboBox _recursive = ReferenceDialogControls.Choice("PushRecursiveSubmodules");
    private readonly CheckBox _tracking = new() { Name = "PushReplaceTracking" };
    private readonly CheckBox _force = new() { Name = "ForcePushBranches" };
    private readonly CheckBox _lease = new() { Name = "PushForceWithLease" };
    private readonly CheckBox _forceTags = new() { Name = "ForcePushTags" };
    private readonly TabControl _tabs = new() { Name = "PushTabs" };
    private readonly StackPanel _options = new() { Name = "PushOptionsPanel", Spacing = 4, IsVisible = false };
    private readonly StackPanel _multipleRows = new() { Name = "PushBranchGrid", Spacing = 0 };
    private readonly TextBox _output = new() { Name = "PushOutput", IsReadOnly = true, AcceptsReturn = true, MinHeight = 65, MaxHeight = 180, };
    private readonly Button _push, _pull, _manage, _browse, _showOptions;
    private readonly List<BranchRow> _rows = [];
    private IReadOnlyList<GitReference> _references = [];
    private IReadOnlyList<GitRemote> _remotes = [];
    private IReadOnlyList<KeyValuePair<string, string>> _configuration = [];
    private string? _currentBranch;
    private bool _bare, _updating, _forceUpdating, _workflowStopRequested;
    internal bool IsReady { get; private set; }
    public bool Succeeded { get; private set; }
    public bool Attempted { get; private set; }
    public bool Cancelled { get; private set; }
    public GitProcessDialogResult? LastProcess { get; private set; }

    public PushDialog(GitRepository repository, Func<Window, CancellationToken, Task>? afterSubmoduleUpdateAsync = null) : base(repository, "Push")
    {
        DesktopFonts.Apply(_output, DesktopFontRole.Monospace);
        _afterSubmoduleUpdateAsync = afterSubmoduleUpdateAsync;
        _preferences = _store.Load();
        _url.ItemsSource = _destinationHistory.Load();
        Width = 760; MinWidth = 620; SizeToContent = SizeToContent.Height;
        _toRemote.Content = Label("PushToRemote", "&Remote");
        _toUrl.Content = Label("PushToUrl", "U&rl");
        _tracking.Content = Label("ReplaceTrackingReference", "R&eplace tracking reference");
        _force.Content = Label("ForcePushBranches", "F&orce push");
        _lease.Content = Label("ckForceWithLease", "&Force with lease");
        _forceTags.Content = Label("ForcePushTags", "&Force push");
        ReferenceDialogControls.Compact(_toRemote, _toUrl, _tracking, _force, _lease, _forceTags);
        ToolTip.SetTip(_lease, C("_forceWithLeaseTooltips", "Force with lease is a safer way to force push. It ensures you only overwrite work that you have seen in your local repository"));
        _recursive.ItemsSource = new[] { C("RecursiveSubmodules", "None", "Item0"), C("RecursiveSubmodules", "Check", "Item1"), C("RecursiveSubmodules", "On-demand", "Item2") };
        _recursive.SelectedIndex = (int)_preferences.Recursion;
        _push = Action("ExecutePush", "Push", async () => { await ExecuteAsync(this); if (Succeeded) { Close(); } });
        _push.Content = IconLabel("ArrowUp.png", A("$this", "&Push")); _push.IsDefault = true;
        _pull = Action("PushPull", "Pull", OpenPullAsync); _pull.Content = Label("Pull", "P&ull");
        _manage = Action("PushManageRemotes", "Manage remotes", ManageRemotesAsync);
        _manage.Content = IconLabel("Remotes.png", A("AddRemote", "&Manage remotes"));
        _browse = Action("PushBrowse", "Browse…", async () => { string? folder = await ExportDialogControls.FolderAsync(this, C("_selectDestinationDirectory", "Please select a destination directory")); if (folder is not null) { _url.Text = folder; } });
        _browse.Content = Label("folderBrowserButton1", "Bro&wse...");
        _showOptions = new() { Name = "PushShowOptions", Content = C("ShowOptions", "Show options"), Height = 24, HorizontalAlignment = HorizontalAlignment.Left };
        _showOptions.Click += (_, _) => { _options.IsVisible = true; _showOptions.IsVisible = false; };
        Track(_showOptions);
        Button cancel = new() { Name = "CancelPush", Content = T("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close();
        DockPanel footer = new() { Margin = new Thickness(10, 6), LastChildFill = false };
        DockPanel.SetDock(_pull, Dock.Left); footer.Children.Add(_pull);
        StackPanel right = new() { Orientation = Orientation.Horizontal, Spacing = 6, Children = { cancel, _push } };
        DockPanel.SetDock(right, Dock.Right); footer.Children.Add(right);
        DockPanel.SetDock(footer, Dock.Bottom); Root.Children.Add(footer);

        Grid targets = new() { ColumnDefinitions = new ColumnDefinitions("100,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        AddTarget(0, _toRemote, _remote, _manage); AddTarget(1, _toUrl, _url, _browse);
        Grid branch = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*") };
        Control branchLabel = Label("labelFrom", "&Branch to push", _source); branchLabel.MinWidth = 120; branchLabel.Margin = new Thickness(0, 0, 8, 0);
        branch.Children.Add(branchLabel); Grid.SetColumn(_source, 1); branch.Children.Add(_source);
        Control to = Label("labelTo", "&to", _destination); to.Margin = new Thickness(6, 0); Grid.SetColumn(to, 2); branch.Children.Add(to);
        Grid.SetColumn(_destination, 3); branch.Children.Add(_destination);
        Grid recursion = new() { ColumnDefinitions = new ColumnDefinitions("Auto,150,*") };
        Control recursionLabel = Label("label2", "Recursive &submodules", _recursive); recursionLabel.MinWidth = 120; recursionLabel.Margin = new Thickness(0, 0, 8, 0);
        recursion.Children.Add(recursionLabel); Grid.SetColumn(_recursive, 1); recursion.Children.Add(_recursive);
        _options.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { _lease, _force } });
        _options.Children.Add(_tracking); _options.Children.Add(recursion);
        StackPanel branchBody = new() { Margin = new Thickness(8), Spacing = 6, Children = { branch, _showOptions, _options } };
        Grid tags = ReferenceDialogControls.Fields(); ReferenceDialogControls.Field(tags, Label("label1", "&Tag to push", _tag), _tag);
        _tabs.Items.Add(new TabItem { Name = "PushBranchesTab", Header = C("BranchTab", "Push branches"), Content = branchBody });
        _tabs.Items.Add(new TabItem { Name = "PushTagsTab", Header = C("TagTab", "Push tags"), Content = new StackPanel { Margin = new Thickness(8), Spacing = 6, Children = { tags, _forceTags } } });
        _tabs.Items.Add(new TabItem { Name = "PushMultipleTab", Header = C("MultipleBranchTab", "Push multiple branches"), Content = MultiplePanel() });
        _tabs.SelectedIndex = 0;
        Root.Children.Add(new StackPanel { Margin = new Thickness(10), Spacing = 8, Children = { ExportDialogControls.Group(C("groupBox2", "Push to"), targets), _tabs, _output } });
        Track(_tabs, _toRemote, _toUrl, _remote, _url, _source, _destination, _tag, _recursive, _tracking, _force, _lease, _forceTags, _multipleRows);
        _remote.SelectionChanged += async (_, _) => { if (!_updating && IsReady) { await ReloadRemoteAsync(); } };
        _source.PropertyChanged += (_, change) => { if (change.Property == AutoCompleteBox.TextProperty && !_updating) { UpdateDestination(); UpdateActions(); } };
        foreach (AutoCompleteBox box in new[] { _url, _destination, _tag }) { box.PropertyChanged += (_, change) => { if (change.Property == AutoCompleteBox.TextProperty) { UpdateActions(); } }; }
        foreach (RadioButton option in new[] { _toRemote, _toUrl }) { option.IsCheckedChanged += (_, _) => { if (!_updating) { if (_toUrl.IsChecked == true) { _url.ItemsSource = _destinationHistory.Load(); } UpdateDestination(); UpdateActions(); } }; }
        _tabs.SelectionChanged += (_, _) => UpdateActions();
        _force.IsCheckedChanged += (_, _) => SyncForce(_force);
        _lease.IsCheckedChanged += (_, _) => SyncForce(_lease);
        _forceTags.IsCheckedChanged += (_, _) => SyncForce(_forceTags);
        Opened += (_, _) => { if (IsReady) { _remote.Focus(); } };
        UpdateActions();

        void AddTarget(int row, Control label, Control input, Control action)
        {
            foreach (Control control in new[] { label, input, action }) { Grid.SetRow(control, row); control.Margin = new Thickness(0, 3, 6, 3); targets.Children.Add(control); }
            Grid.SetColumn(input, 1); Grid.SetColumn(action, 2);
        }
    }

    private static AutoCompleteBox Input(string name) => new() { Name = name, Height = 24, MinimumPrefixLength = 0, FilterMode = AutoCompleteFilterMode.ContainsOrdinal, IsTextCompletionEnabled = true };
    private string C(string item, string source, string property = "Text") => Translations.Translate("FormPush", item, property, source);
    private string A(string item, string source) => Translations.TranslateAccessKeys("FormPush", item, "Text", source);
    private Control Label(string item, string source, Control? target = null) => new Label
    { Content = new AccessText { Text = A(item, source) }, VerticalAlignment = VerticalAlignment.Center, Target = target, Padding = new Thickness(0) };
    private static Control IconLabel(string icon, string text)
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + icon));
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new Image { Width = 16, Height = 16, Source = new Bitmap(stream), Tag = icon }, new AccessText { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
    }
    private string? RemoteName => _remote.SelectedItem as string;
    private string Destination => _toUrl.IsChecked == true ? _url.Text ?? "" : RemoteName ?? "";
    private GitPushMode Mode => _tabs.SelectedIndex == 1 ? _tag.Text == AllRefs ? GitPushMode.AllTags : GitPushMode.Tag : _tabs.SelectedIndex == 2 ? GitPushMode.Multiple : _source.Text == AllRefs ? GitPushMode.AllBranches : GitPushMode.Branch;

    public override Task InitializeAsync() => InitializeAsync(default);

    internal async Task PushAndShowWhenFailedAsync(Window owner, CancellationToken cancellationToken = default, bool forceWithLease = false)
    {
        await InitializeAsync(cancellationToken);
        if (forceWithLease) { _lease.IsChecked = true; }
        if (IsReady) { await ExecuteAsync(owner, cancellationToken); }
        cancellationToken.ThrowIfCancellationRequested();
        if (!_workflowStopRequested && owner.IsVisible && !Succeeded) { await ShowDialog(owner); }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal Task InitializeAsync(CancellationToken cancellationToken)
    {
        return RunAsync("Push", async token =>
    {
        _updating = true;
        try
        {
            await ReadRepositoryAsync(token);
            _currentBranch = (await Repository.GetStatusAsync(token)).Branch;
            string current = _currentBranch is null or "(detached)" ? "HEAD" : _currentBranch;
            _source.ItemsSource = new[] { AllRefs, "HEAD" }.Concat(_references.Where(reference => reference.IsBranch).Select(reference => reference.ShortName)).ToArray();
            // Selecting the initial item updates both the editable text and
            // its template TextBox, even while modal activation is pending.
            _source.SelectedItem = current;
            _tag.ItemsSource = new[] { AllRefs }.Concat(_references.Where(reference => reference.IsTag).Select(reference => reference.ShortName)).ToArray(); _tag.SelectedItem = AllRefs;
            string? configured = Config("branch." + _currentBranch + ".remote");
            _remote.ItemsSource = _remotes.Select(remote => remote.Name).ToArray();
            _remote.SelectedItem = _remotes.FirstOrDefault(remote => remote.Name.Equals(configured, StringComparison.OrdinalIgnoreCase))?.Name
                ?? _remotes.FirstOrDefault(remote => remote.Name.Equals("origin", StringComparison.OrdinalIgnoreCase))?.Name ?? _remotes.FirstOrDefault()?.Name;
            _toRemote.IsChecked = true;
            UpdateDestination(); await RebuildRowsAsync(token); IsReady = true;
            Title = C("$this", "Push") + " (" + Repository.Path + ")";
            Status.Text = T("Current branch:") + " " + current;
        }
        finally { _updating = false; }
        }, cancellationToken: cancellationToken);
    }

    private async Task ReadRepositoryAsync(CancellationToken token)
    {
        _bare = (await Repository.GetInfoAsync(token)).IsBare;
        _references = await Repository.GetReferencesAsync(token);
        _remotes = await Repository.GetRemotesAsync(token);
        _configuration = await Repository.GetConfigAsync(cancellationToken: token);
    }
    private string? Config(string key) => _configuration.LastOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private Task ReloadRemoteAsync() => RunAsync("Push", async token =>
    {
        _updating = true;
        try { UpdateDestination(); await RebuildRowsAsync(token); }
        finally { _updating = false; }
    });

    private void UpdateDestination()
    {
        string source = _source.Text ?? "";
        string prefix = "refs/remotes/" + RemoteName + "/";
        _destination.ItemsSource = _references.Where(reference => reference.IsRemote && reference.SymbolicTarget is null && reference.Name.StartsWith(prefix, StringComparison.Ordinal)).Select(reference => reference.Name[prefix.Length..]).Prepend(source).Distinct(StringComparer.Ordinal).ToArray();
        if (source == AllRefs) { SetText(_destination, ""); return; }
        if (source == "HEAD" || source.Contains(':')) { return; }
        if (_toRemote.IsChecked == true && RemoteName is not null)
        {
            GitReference? local = _references.FirstOrDefault(reference => reference.IsBranch && reference.ShortName == source);
            string? mapping = _configuration.Where(pair => pair.Key == "remote." + RemoteName + ".push").Select(pair => PushMapping(pair.Value, source)).FirstOrDefault(value => value is not null);
            if (mapping is not null) { SetText(_destination, mapping); }
            else if (Config("branch." + source + ".remote") == RemoteName && Config("branch." + source + ".merge") is string merge) { SetText(_destination, StripHead(merge)); }
            else if (local?.Upstream?.StartsWith(RemoteName + "/", StringComparison.Ordinal) == true) { SetText(_destination, local.Upstream[(RemoteName.Length + 1)..]); }
            else { SetText(_destination, (Config("remote." + RemoteName + ".prefix") ?? "") + source); }
            GitRemote? remote = _remotes.FirstOrDefault(remote => remote.Name == RemoteName);
            string? pushUrl = Config("remote." + RemoteName + ".pushurl");
            SetText(_url, string.IsNullOrEmpty(pushUrl) ? remote?.FetchUrls.FirstOrDefault() ?? "" : pushUrl);
        }
        else { SetText(_destination, source); }
    }

    private static void SetText(AutoCompleteBox box, string value)
    {
        // AutoCompleteBox.SelectedItem updates its text and template part
        // synchronously; Text alone can be superseded by a queued empty edit
        // during the modal window's first activation.
        box.SelectedItem = value;
        box.Text = value;
    }

    private static string StripHead(string value) => value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value[11..] : value;
    private static string? PushMapping(string refspec, string branch)
    {
        string[] parts = refspec.TrimStart('+').Split(':');
        if (parts.Length != 2 || !parts[1].StartsWith("refs/heads/", StringComparison.Ordinal)) { return null; }
        string source = StripHead(parts[0]);
        return source.Equals(branch, StringComparison.OrdinalIgnoreCase) ? StripHead(parts[1]) : source == "*" ? StripHead(parts[1]).Replace("*", branch, StringComparison.Ordinal) : null;
    }

    private Control MultiplePanel()
    {
        Grid headings = RowGrid();
        string[] captions = [C("LocalColumn", "Local Branch", "HeaderText"), C("RemoteColumn", "Remote Branch", "HeaderText"), C("NewColumn", "Ahead/Behind", "HeaderText"), C("PushColumn", "Push", "HeaderText"), C("ForceColumn", "Force", "HeaderText"), C("DeleteColumn", "Delete Remote Branch", "HeaderText")];
        for (int column = 0; column < captions.Length; column++)
        {
            Control caption = column == 3 ? SelectionButton(captions[column]) : new TextBlock { Text = captions[column], TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 2) };
            Grid.SetColumn(caption, column); headings.Children.Add(caption);
        }
        return ClassicChrome.Border(new DockPanel { LastChildFill = true, Children = { Top(headings), new ScrollViewer { MaxHeight = 240, MinHeight = 100, Content = _multipleRows } } }, new Thickness(1), new Thickness(0));
        static Control Top(Control control) { DockPanel.SetDock(control, Dock.Top); return control; }
    }
    private static Grid RowGrid() => new() { ColumnDefinitions = new ColumnDefinitions("*,*,95,58,58,112"), MinHeight = 24 };
    private Button SelectionButton(string caption)
    {
        MenuFlyout menu = new();
        (string Id, string Text, Func<BranchRow, bool> Select)[] actions =
        [ ("unselectAllToolStripMenuItem", "Unselect all", _ => false), ("selectTrackedToolStripMenuItem", "Select tracked", row => row.Source is not null && !string.IsNullOrWhiteSpace(row.Destination.Text)), ("selectAllToolStripMenuItem", "Select all", row => row.Source is not null) ];
        foreach (var action in actions)
        {
            MenuItem item = new() { Name = "Push" + action.Id, Header = C(action.Id, action.Text) };
            item.Click += (_, _) => { foreach (BranchRow row in _rows) { row.Push.IsChecked = action.Select(row); } };
            menu.Items.Add(item);
        }
        Button button = new() { Name = "PushSelection", Content = caption, Flyout = menu, Height = 24, Padding = new Thickness(2) };
        Track(button); return button;
    }

    private async Task RebuildRowsAsync(CancellationToken token)
    {
        _rows.Clear(); _multipleRows.Children.Clear();
        string prefix = "refs/remotes/" + RemoteName + "/";
        GitReference[] locals = _references.Where(reference => reference.IsBranch).ToArray();
        GitReference[] remotes = _references.Where(reference => reference.IsRemote && reference.SymbolicTarget is null && reference.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        foreach (GitReference local in locals)
        {
            string tracked = Config("branch." + local.ShortName + ".remote") == RemoteName ? StripHead(Config("branch." + local.ShortName + ".merge") ?? local.ShortName) : "";
            GitReference? remote = remotes.FirstOrDefault(reference => reference.Name[prefix.Length..] == (tracked.Length > 0 ? tracked : local.ShortName));
            string ahead = remote is null ? "" : remote.Hash == local.Hash ? "=" : "<>";
            if (tracked.Length > 0 && remote is not null)
            {
                string[] counts = (await Repository.RunAsync(["rev-list", "--left-right", "--count", local.Name + "..." + remote.Name, "--"], token)).StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (counts.Length == 2) { ahead = counts[0] == "0" && counts[1] == "0" ? "=" : "+" + counts[0] + " / −" + counts[1]; }
            }
            AddRow(local.ShortName, tracked, ahead);
        }
        foreach (GitReference remote in remotes.Where(remote => locals.All(local => local.ShortName != remote.Name[prefix.Length..]))) { AddRow(null, remote.Name[prefix.Length..], ""); }
    }

    private void AddRow(string? source, string destination, string ahead)
    {
        int index = _rows.Count;
        BranchRow row = new(source, new TextBox { Name = "PushDestination_" + index, Text = destination, Height = 24, Margin = new Thickness(1, 0) },
            new CheckBox { Name = "PushRow_" + index }, new CheckBox { Name = "ForcePushRow_" + index }, new CheckBox { Name = "DeletePushRow_" + index });
        ReferenceDialogControls.Compact(row.Push, row.Force, row.Delete);
        row.Push.IsVisible = row.Force.IsVisible = source is not null;
        Grid grid = RowGrid(); grid.Name = "PushBranchRow_" + index; grid.Tag = source;
        Control[] cells = [new TextBlock { Text = source ?? "", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) }, row.Destination,
            new TextBlock { Text = ahead, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) }, row.Push, row.Force, row.Delete];
        for (int column = 0; column < cells.Length; column++) { Grid.SetColumn(cells[column], column); grid.Children.Add(cells[column]); }
        foreach (CheckBox action in new[] { row.Push, row.Force, row.Delete })
        {
            action.HorizontalAlignment = HorizontalAlignment.Center;
            action.IsCheckedChanged += (_, _) =>
            {
                if (action.IsChecked == true) { foreach (CheckBox other in new[] { row.Push, row.Force, row.Delete }.Where(other => other != action)) { other.IsChecked = false; } }
                UpdateActions();
            };
        }
        _rows.Add(row); _multipleRows.Children.Add(grid);
    }

    private void SyncForce(CheckBox sender)
    {
        if (_forceUpdating) { return; }
        _forceUpdating = true;
        try
        {
            if (sender == _force && _force.IsChecked == true) { _lease.IsChecked = _forceTags.IsChecked = false; }
            else if (sender == _lease) { _forceTags.IsChecked = _lease.IsChecked; if (_lease.IsChecked == true) { _force.IsChecked = false; } }
            else if (sender == _forceTags) { _lease.IsChecked = _forceTags.IsChecked; if (_forceTags.IsChecked == true) { _force.IsChecked = false; } }
        }
        finally { _forceUpdating = false; }
    }

    public GitPushRequest ReadOptions() => new(Destination, Mode,
        Mode == GitPushMode.Tag ? _tag.Text : Mode == GitPushMode.Branch ? _source.Text : null,
        Mode == GitPushMode.Branch && !(_source.Text?.Contains(':') == true) ? _destination.Text : null,
        Mode == GitPushMode.Multiple ? GitPushForce.None : Mode is GitPushMode.Tag or GitPushMode.AllTags ? _forceTags.IsChecked == true ? GitPushForce.Force : GitPushForce.None
            : _force.IsChecked == true ? GitPushForce.Force : _lease.IsChecked == true ? GitPushForce.ForceWithLease : GitPushForce.None,
        Mode is GitPushMode.Branch or GitPushMode.AllBranches && _tracking.IsChecked == true,
        Mode is GitPushMode.Branch or GitPushMode.AllBranches ? (GitPushRecursion)Math.Max(0, _recursive.SelectedIndex) : GitPushRecursion.None,
        Mode == GitPushMode.Multiple ? _rows.Where(row => row.Push.IsChecked == true || row.Force.IsChecked == true || row.Delete.IsChecked == true)
            .Select(row => new GitPushAction(row.Delete.IsChecked == true ? null : row.Source, string.IsNullOrWhiteSpace(row.Destination.Text) ? row.Source ?? "" : row.Destination.Text, row.Force.IsChecked == true, row.Delete.IsChecked == true)).ToArray() : null);

    internal Task ExecuteAsync(Window owner, CancellationToken cancellationToken = default) => RunAsync("Push", async token =>
    {
        Succeeded = Cancelled = false;
        _workflowStopRequested = false; LastProcess = null;
        GitPushRequest request = ReadOptions();
        if (!IsReady || string.IsNullOrWhiteSpace(request.Destination)) { throw new ArgumentException(C("_configureRemote", "Please configure a remote repository first.\nWould you like to do it now?")); }
        if (request.Mode == GitPushMode.Branch && (string.IsNullOrWhiteSpace(request.Source) || !request.Source.Contains(':') && string.IsNullOrWhiteSpace(request.DestinationRef))) { throw new ArgumentException(C("_noCurrentBranch", "No branch is selected, cannot push.")); }
        if (request.Mode == GitPushMode.Tag && string.IsNullOrWhiteSpace(request.Source)) { throw new ArgumentException(C("_selectTag", "You need to select a tag to push or select \"Push all tags\".")); }
        if (request.Mode == GitPushMode.Multiple && request.Actions?.Count == 0) { return; }
        if (request.Mode == GitPushMode.Branch && _toRemote.IsChecked == true && !_bare && !request.Source!.Contains(':'))
        {
            string remoteRef = "refs/remotes/" + RemoteName + "/" + request.DestinationRef;
            string? defaultPush = _configuration.Where(pair => pair.Key == "remote." + RemoteName + ".push").Select(pair => PushMapping(pair.Value, request.Source)).FirstOrDefault(value => value is not null);
            bool known = _references.Any(reference => reference.Name == remoteRef) || Config("branch." + request.DestinationRef + ".remote") == RemoteName;
            if (!known && defaultPush != request.DestinationRef && !_preferences.DontConfirmNewBranch)
            {
                var choice = await ChoiceAsync(owner, "PushNewBranchConfirmation", C("_pushCaption", "Push"), C("_branchNewForRemote", "The branch you are about to push seems to be a new branch for the remote.\nAre you sure you want to push this branch?"), [(1, T("Yes")), (0, T("No"))], true, token);
                if (choice.Remember) { _preferences = _preferences with { DontConfirmNewBranch = true }; _store.Save(_preferences); }
                if (choice.Value != 1) { Cancelled = true; return; }
            }
            GitReference? local = _references.FirstOrDefault(reference => reference.IsBranch && reference.ShortName == request.Source);
            if (!request.SetUpstream && local is not null && string.IsNullOrEmpty(local.Upstream) && !_remotes.Any(remote => request.Source.StartsWith(remote.Name, StringComparison.OrdinalIgnoreCase)) && Config("branch.autosetupmerge") != "false")
            {
                bool track = _preferences.DontConfirmAddTracking;
                if (!track)
                {
                    var choice = await ChoiceAsync(owner, "PushTrackingConfirmation", C("_pushCaption", "Push"), string.Format(C("_updateTrackingReference", "The branch {0} does not have a tracking reference. Do you want to add a tracking reference to {1}?"), request.Source, request.DestinationRef), [(1, T("Yes")), (2, T("No")), (0, T("Cancel"))], false, token);
                    if (choice.Value == 0) { Cancelled = true; return; }
                    track = choice.Value == 1;
                }
                request = request with { SetUpstream = track };
            }
        }
        if ((request.Force == GitPushForce.Force || request.Source?.StartsWith('+') == true) && request.Mode is GitPushMode.Branch or GitPushMode.AllBranches)
        {
            var choice = await ChoiceAsync(owner, "PushForceConfirmation", T("Question"), C("_useForceWithLeaseInstead", "Force push may overwrite changes since your last fetch. Do you want to use the safer force with lease instead?"), [(1, T("Yes")), (2, T("No")), (0, T("Cancel"))], false, token);
            if (choice.Value == 0) { Cancelled = true; return; }
            if (choice.Value == 1)
            {
                _force.IsChecked = false; _lease.IsChecked = true;
                request = request with { Force = GitPushForce.ForceWithLease, Source = request.Source?.TrimStart('+') };
                if (request.Mode == GitPushMode.Branch && request.Source is not null) { _source.Text = request.Source; }
            }
        }
        if (request.Mode == GitPushMode.Branch && request.Source?.StartsWith(':') == true)
        {
            var choice = await ChoiceAsync(owner, "PushDeleteConfirmation", T("Delete Confirmation"), request.Source[1..], [(1, T("Yes")), (0, T("Cancel"))], false, token);
            if (choice.Value != 1) { Cancelled = true; return; }
        }
        _preferences = _preferences with { Recursion = (GitPushRecursion)Math.Max(0, _recursive.SelectedIndex) }; _store.Save(_preferences);
        if (_toUrl.IsChecked == true)
        {
            int capacity = new RepositoryHistoryStore().Load(new PreferencesStore().Load().RecentRepositories).HistorySize;
            _destinationHistory.Visit(request.Destination, capacity);
        }
        Attempted = true; MarkRepositoryChanged();
        try { await PushAndRecoverAsync(owner, request, token); }
        finally { token.ThrowIfCancellationRequested(); }
    }, cancellationToken: cancellationToken);

    private async Task PushAndRecoverAsync(Window owner, GitPushRequest request, CancellationToken token)
    {
        GitPushRequest current = request;
        HashSet<int> appended = [];
        void AppendAttempt(GitProcessAttemptResult attempt)
        {
            if (!appended.Add(attempt.Number)) { return; }
            if (attempt.Result is GitResult result) { Append(result); }
            else
            {
                GitProcessTextDecoder stdout = new(), stderr = new();
                _output.Text = (_output.Text ?? "") + stdout.Append(attempt.StandardOutputBytes.Span, flush: true)
                    + stderr.Append(attempt.StandardErrorBytes.Span, flush: true);
            }
        }
        GitProcessCommand Command(GitPushRequest options) => new(
            "git push" + (options.Force == GitPushForce.ForceWithLease ? " --force-with-lease" : options.Force == GitPushForce.Force ? " --force" : "") + " — " + options.Destination,
            (ct, output) => Repository.PushAsync(options, ct, output));
        GitProcessCommand first = Command(current);
        LastProcess = await GitProcessDialog.ShowAsync(owner, Repository.Path, first.Description, true, first.Execute, token,
            handleOnExit: async (process, attempt, ct) =>
        {
            AppendAttempt(attempt);
            if (attempt.Error is not GitCommandException exception) { return null; }
            string output = exception.Result.StandardError + exception.Result.StandardOutput;
            // A hook/permission failure is not a non-fast-forward rejection. Never offer an automatic force for it.
            bool rejected = output.Contains("[rejected]", StringComparison.Ordinal) && !output.Contains("[remote rejected]", StringComparison.Ordinal);
            if (!rejected || current.Mode != GitPushMode.Branch || current.Source != _currentBranch || _toRemote.IsChecked != true || _bare) { return null; }
            GitPullAction? action = _preferences.RejectedAction;
            bool force = false;
            if (action is null)
            {
                GitPullAction defaultAction = new PullPreferencesStore().Load().DefaultAction;
                var choice = await ChoiceAsync(process, "PushRejectedConfirmation", string.Format(C("_pullRepositoryCaption", "Push was rejected from \"{0}\""), current.Destination),
                    C("_pullRepositoryMergeInstruction", "The push was rejected because the tip of your current branch is behind its remote counterpart. Merge the remote changes before pushing again."),
                    [(1, string.Format(A("_pullDefaultButton", "&Pull with the default pull action ({0})"), T(defaultAction.ToString().ToLowerInvariant()))),
                     (2, A("_pullRebaseButton", "Pull with &rebase")), (3, A("_pullMergeButton", "Pull with &merge")), (4, A("_pushForceButton", "&Force push with lease")), (0, T("Cancel"))], true, ct, commandLinks: true);
                action = choice.Value switch { 1 => GitPullAction.Default, 2 => GitPullAction.Rebase, 3 => GitPullAction.Merge, _ => GitPullAction.None };
                force = choice.Value == 4;
                if (choice.Remember && !force) { _preferences = _preferences with { RejectedAction = action }; _store.Save(_preferences); }
            }
            if (force)
            {
                ct.ThrowIfCancellationRequested();
                // A literal '+' is a force refspec too; choosing lease must not leave
                // it in place and accidentally bypass the safer requested mode.
                current = current with { Force = GitPushForce.ForceWithLease, Source = current.Source?.TrimStart('+') };
                _force.IsChecked = false; _lease.IsChecked = true;
                return Command(current);
            }
            if (action == GitPullAction.Default) { action = new PullPreferencesStore().Load().DefaultAction; }
            if (action == GitPullAction.None) { Cancelled = true; return null; }
            if (action is not (GitPullAction.Merge or GitPullAction.Rebase)) { throw new InvalidOperationException(T("Automatical pull can only be performed, when the default pull action is either set to Merge or Rebase.")); }
            if (action == GitPullAction.Rebase)
            {
                string remoteBranch = "refs/remotes/" + current.Destination + "/" + current.DestinationRef;
                if (_references.Any(reference => reference.Name == remoteBranch)
                    && (await Repository.RunAsync(["rev-list", "--merges", remoteBranch + "..HEAD", "--"], ct)).StandardOutput.Length > 0)
                { throw new InvalidOperationException(T("Can not perform automatical pull, when the pull action is set to Rebase and one of the commits that are about to be rebased is a merge commit.")); }
            }
            PullDialog? pull = null;
            pull = new(Repository, action.Value, current.Destination, current.DestinationRef,
                cancellation => _afterSubmoduleUpdateAsync?.Invoke(pull!, cancellation) ?? Task.CompletedTask) { RequestedThemeVariant = process.ActualThemeVariant };
            bool once = false;
            pull.Opened += async (_, _) => { if (once) { return; } once = true; await pull.InitializeAsync(ct); if (pull.IsReady) { await pull.ExecuteAsync(pull, cancellationToken: ct); if (pull.Succeeded) { pull.Close(); } } };
            await process.ShowCompletionDialogAsync<object?>(pull); ct.ThrowIfCancellationRequested();
            if (!pull.Succeeded) { if (pull.LastError is not null) { _output.Text += "\n" + pull.LastError; } return null; }
            return Command(current);
        });
        // Cancellation skips recovery, but its already observed native bytes still
        // belong to the cumulative Push log and must survive the stopped workflow.
        foreach (GitProcessAttemptResult attempt in LastProcess.Attempts) { AppendAttempt(attempt); }
        if (LastProcess.WorkflowStopRequested || LastProcess.OwnerCloseRequested || LastProcess.WasCancelled || token.IsCancellationRequested || !owner.IsVisible)
        { _workflowStopRequested = Cancelled = true; throw new OperationCanceledException(token); }
        if (LastProcess.CompletionError is Exception completionError) { ExceptionDispatchInfo.Capture(completionError).Throw(); }
        if (LastProcess.Error is Exception error) { ExceptionDispatchInfo.Capture(error).Throw(); }
        if (LastProcess.Result?.Success != true) { throw new InvalidOperationException(T("No native Git result was returned.")); }
        Succeeded = true; Status.Text = C("_pushToCaption", "Push to {0}").Replace("{0}", current.Destination, StringComparison.Ordinal);
    }
    private void Append(GitResult result) => _output.Text = (_output.Text ?? "") + result.StandardOutput + result.StandardError;

    private async Task OpenPullAsync()
    {
        if (IsBusy || _bare) { return; }
        await RunAsync("Pull", async token =>
        {
            PullDialog? pull = null;
            pull = new(Repository, GitPullAction.Merge, RemoteName, _destination.Text,
                cancellation => _afterSubmoduleUpdateAsync?.Invoke(pull!, cancellation) ?? Task.CompletedTask) { RequestedThemeVariant = ActualThemeVariant };
            pull.Opened += async (_, _) => await pull.InitializeAsync(token);
            using CancellationTokenRegistration registration = token.Register(() => Dispatcher.UIThread.Post(pull.Close));
            await pull.ShowDialog(this);
            if (pull.Attempted) { MarkRepositoryChanged(); }
            token.ThrowIfCancellationRequested();
            await ReadRepositoryAsync(token); UpdateDestination(); await RebuildRowsAsync(token);
        });
    }

    private Task ManageRemotesAsync() => RunAsync("Manage remotes", async token =>
    {
        RemotesDialog remote = new(Repository, preselectRemote: RemoteName) { RequestedThemeVariant = ActualThemeVariant };
        remote.Opened += async (_, _) => await remote.InitializeAsync(token);
        using CancellationTokenRegistration registration = token.Register(() => Dispatcher.UIThread.Post(remote.Close));
        await remote.ShowDialog(this);
        if (remote.HasRepositoryChanges) { MarkRepositoryChanged(); }
        token.ThrowIfCancellationRequested();
        if (remote.HasRepositoryChanges)
        {
            string? selected = RemoteName;
            _updating = true;
            try
            {
                await ReadRepositoryAsync(token); _remote.ItemsSource = _remotes.Select(item => item.Name).ToArray();
                _remote.SelectedItem = _remotes.FirstOrDefault(item => item.Name == selected)?.Name ?? _remotes.FirstOrDefault()?.Name;
                UpdateDestination(); await RebuildRowsAsync(token);
            }
            finally { _updating = false; }
        }
    });

    private async Task<(int Value, bool Remember)> ChoiceAsync(Window owner, string name, string title, string message,
        IReadOnlyList<(int Value, string Label)> choices, bool remember, CancellationToken token, bool commandLinks = false)
    {
        Window dialog = new() { Name = name, Title = title, Width = 580, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = owner.ActualThemeVariant };
        if (commandLinks) { ConfigureCommandLinks(dialog); }
        CheckBox dontAsk = new() { Name = "PushConfirmationRemember", Content = T("Don't show this message again"), IsVisible = remember };
        ReferenceDialogControls.Compact(dontAsk);
        StackPanel body = new() { Margin = new Thickness(12), Spacing = 8 };
        if (commandLinks)
        {
            body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children =
            {
                new Panel { Width = 32, Height = 32, Children =
                {
                    new Avalonia.Controls.Shapes.Ellipse { Fill = Brushes.Red, Width = 32, Height = 32 },
                    new PathIcon { Data = Geometry.Parse("M 1,3 L 3,1 L 9,7 L 15,1 L 17,3 L 11,9 L 17,15 L 15,17 L 9,11 L 3,17 L 1,15 L 7,9 Z"), Foreground = Brushes.White, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                } },
                new TextBlock { Text = C("_pullRepositoryMainMergeInstruction", "Pull latest changes from remote repository"), FontSize = DesktopFonts.For(DesktopFontRole.Application).SizeInDips + 3, VerticalAlignment = VerticalAlignment.Center }
            } });
        }
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        StackPanel buttons = new() { Orientation = commandLinks ? Orientation.Vertical : Orientation.Horizontal, Spacing = 2, HorizontalAlignment = commandLinks ? HorizontalAlignment.Stretch : HorizontalAlignment.Right };
        Button? cancel = null;
        foreach (var choice in choices)
        {
            Button button = new() { Name = "PushChoice" + choice.Value, Content = new AccessText { Text = choice.Label }, IsCancel = choice.Value == 0, IsDefault = choice.Value == (commandLinks ? 0 : choices[0].Value), MinHeight = commandLinks && choice.Value != 0 ? 48 : 24, HorizontalContentAlignment = commandLinks ? HorizontalAlignment.Left : HorizontalAlignment.Center };
            if (commandLinks && choice.Value != 0) { button.Classes.Add("push-command-link"); button.Template = CommandLinkTemplate(); }
            button.Click += (_, _) => dialog.Close((choice.Value, dontAsk.IsChecked == true));
            if (commandLinks && choice.Value == 0) { cancel = button; } else { buttons.Children.Add(button); }
        }
        body.Children.Add(buttons); body.Children.Add(dontAsk);
        if (cancel is not null)
        {
            cancel.MinWidth = 75; cancel.HorizontalAlignment = HorizontalAlignment.Right;
            Border footer = ClassicChrome.Border(cancel, new Thickness(0, 1, 0, 0), new Thickness(12));
            footer.Bind(Border.BackgroundProperty, Resource("Menu"));
            dialog.Content = new StackPanel { Children = { body, footer } };
        }
        else { dialog.Content = body; }
        dialog.KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; dialog.Close((0, false)); } };
        void Closing(object? sender, WindowClosingEventArgs args)
        {
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _workflowStopRequested = Cancelled = true; }
        }
        dialog.Closing += Closing;
        using CancellationTokenRegistration registration = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        try
        {
            var result = owner is GitProcessDialog process
                ? await process.ShowCompletionDialogAsync<(int Value, bool Remember)>(dialog)
                : await dialog.ShowDialog<(int Value, bool Remember)>(owner);
            token.ThrowIfCancellationRequested();
            if (_workflowStopRequested) { throw new OperationCanceledException(token); }
            return result;
        }
        finally { dialog.Closing -= Closing; }
    }

    private static DynamicResourceExtension Resource(string name) => new("Classic" + name + "Brush");

    private static void ConfigureCommandLinks(Window dialog)
    {
        dialog.Styles.Add(new Style(selector => selector.OfType<Button>().Class("push-command-link"))
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, Resource("Window")), new Setter(Button.ForegroundProperty, Resource("Text")),
                new Setter(Button.BorderBrushProperty, Brushes.Transparent), new Setter(Button.BorderThicknessProperty, new Thickness(1)),
                new Setter(Button.PaddingProperty, new Thickness(10)), new Setter(Button.FontSizeProperty, DesktopFonts.For(DesktopFontRole.Application).SizeInDips + 2),
                new Setter(Button.MinHeightProperty, 48d), new Setter(Button.HorizontalAlignmentProperty, HorizontalAlignment.Stretch)
            }
        });
        dialog.Styles.Add(new Style(selector => selector.OfType<Button>().Class("push-command-link").Class(":pointerover"))
        { Setters = { new Setter(Button.BackgroundProperty, Resource("Hover")), new Setter(Button.BorderBrushProperty, Resource("ButtonBorder")) } });
        dialog.Styles.Add(new Style(selector => selector.OfType<Button>().Class("push-command-link").Class(":pressed"))
        { Setters = { new Setter(Button.BackgroundProperty, Resource("Pressed")) } });
        dialog.Styles.Add(new Style(selector => selector.OfType<Button>().Class("push-command-link").Class(":focus-visible"))
        { Setters = { new Setter(Button.BorderBrushProperty, Resource("Highlight")) } });
        dialog.Styles.Add(new Style(selector => selector.OfType<Button>().Class("push-command-link").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent), new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent),
                new Setter(ContentPresenter.BorderThicknessProperty, new Thickness(0)), new Setter(ContentPresenter.PaddingProperty, new Thickness(0)) }
        });
    }

    private static FuncControlTemplate<Button> CommandLinkTemplate() => new((button, scope) =>
    {
        PathIcon arrow = new() { Data = Geometry.Parse("M 1,6 L 11,6 L 7,2 L 10,2 L 17,9 L 10,16 L 7,16 L 11,12 L 1,12 Z"), Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        arrow.Bind(PathIcon.ForegroundProperty, new TemplateBinding(Button.ForegroundProperty), BindingPriority.Template);
        ContentPresenter label = new() { Name = "PART_ContentPresenter", Content = button.Content, HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center };
        label.Bind(ContentPresenter.ForegroundProperty, new TemplateBinding(Button.ForegroundProperty), BindingPriority.Template); scope.Register(label.Name!, label);
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("18,12,*") }; content.Children.Add(arrow); Grid.SetColumn(label, 2); content.Children.Add(label);
        Border chrome = new() { Child = content, CornerRadius = new CornerRadius(3) };
        chrome.Bind(Border.BackgroundProperty, new TemplateBinding(Button.BackgroundProperty), BindingPriority.Template);
        chrome.Bind(Border.BorderBrushProperty, new TemplateBinding(Button.BorderBrushProperty), BindingPriority.Template);
        chrome.Bind(Border.BorderThicknessProperty, new TemplateBinding(Button.BorderThicknessProperty), BindingPriority.Template);
        chrome.Bind(Border.PaddingProperty, new TemplateBinding(Button.PaddingProperty), BindingPriority.Template); return chrome;
    });

    protected override void UpdateActions()
    {
        bool idle = !IsBusy && IsReady;
        _remote.IsEnabled = idle && _toRemote.IsChecked == true;
        _manage.IsEnabled = !IsBusy;
        _url.IsEnabled = _browse.IsEnabled = idle && _toUrl.IsChecked == true;
        _destination.IsEnabled = idle && _source.Text != AllRefs && !(_source.Text?.Contains(':') == true);
        _pull.IsEnabled = idle && !_bare;
        bool branchValid = Mode != GitPushMode.Branch || !string.IsNullOrWhiteSpace(_source.Text) && (_source.Text.Contains(':') || !string.IsNullOrWhiteSpace(_destination.Text));
        bool tagValid = Mode != GitPushMode.Tag || !string.IsNullOrWhiteSpace(_tag.Text);
        _push.IsEnabled = idle && !string.IsNullOrWhiteSpace(Destination) && branchValid && tagValid && (Mode != GitPushMode.Multiple || _rows.Any(row => row.Push.IsChecked == true || row.Force.IsChecked == true || row.Delete.IsChecked == true));
    }
    private sealed record BranchRow(string? Source, TextBox Destination, CheckBox Push, CheckBox Force, CheckBox Delete);
}

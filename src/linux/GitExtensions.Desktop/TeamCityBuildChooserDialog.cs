using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original TeamCity project/build picker. Its client is borrowed, never disposed here.</summary>
internal sealed class TeamCityBuildChooserDialog : Window
{
    private readonly IReadOnlyDictionary<string, string> _lightColors = ClassicStyles.OriginalColors(false), _darkColors = ClassicStyles.OriginalColors(true);
    private readonly ITeamCityClient _client;
    private readonly TeamCityBuildSelection? _initial;
    private readonly TranslationCatalog _translations;
    private readonly TreeView _projects = new() { Name = "treeViewTeamCityProjects", TabIndex = 14, Focusable = true };
    private readonly Button _ok, _cancel;
    private readonly TextBlock _notice = new()
    {
        Name = "TeamCityChooserNotice", IsVisible = false, TextWrapping = TextWrapping.Wrap,
        MaxHeight = 60, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(9, 2, 8, 0)
    };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _work = [];
    private readonly Dictionary<string, ProjectNode> _nodes = new(StringComparer.Ordinal);
    private Task? _initialize, _close;
    private string? _projectsNotice;
    private long _interactionVersion;
    private bool _closing, _closeReady, _decided;
    private Window? _closingOwner;

    internal TeamCityBuildSelection? Result { get; private set; }
    internal Exception? LastError { get; private set; }
    internal bool IsBusy => _work.Count != 0;
    internal Task Completion => Task.WhenAll(_work.ToArray());
    internal event Action? BusyChanged;

    internal TeamCityBuildChooserDialog(ITeamCityClient client, TeamCityBuildSelection? initial = null,
        TranslationCatalog? translations = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client)); _initial = initial;
        _translations = translations ?? TranslationCatalog.LoadDefault();
        Name = "TeamCityBuildChooserDialog"; Title = T("$this", "Choose the TeamCity build...");
        Width = 451; Height = 379; MinWidth = 360; MinHeight = 180;
        CanMinimize = CanMaximize = false; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _ok = Button("buttonOK", "OK", 15); _ok.IsEnabled = false;
        _cancel = Button("buttonCancel", "Cancel", 15); _cancel.IsCancel = true;
        _ok.Click += (_, _) => Accept(); _cancel.Click += (_, _) => Cancel();
        // The Windows Designer declares CancelButton, but no AcceptButton. Enter in the tree must not submit it.
        _projects.AddHandler(KeyDownEvent, (_, args) =>
        { if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None) { args.Handled = true; } }, RoutingStrategies.Tunnel);
        KeyboardNavigation.SetTabNavigation(_projects, KeyboardNavigationMode.Once);
        _projects.SelectionChanged += (_, _) => UpdateActions();
        _projects.AddHandler(DoubleTappedEvent, (_, args) =>
        {
            // A project double-click retains the native expand/collapse gesture.
            if (_projects.SelectedItem is TreeViewItem { Tag: TeamCityBuildType }) { args.Handled = true; Accept(); }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _projects.Bind(BackgroundProperty, new DynamicResourceExtension("ClassicWindowBrush"));
        Border frame = ClassicChrome.Border(ClassicTreeViewAppearance.Wrap(_projects), new Thickness(1));
        frame.Name = "TeamCityProjectsFrame"; frame.Margin = new Thickness(9, 10, 8, 0);
        Grid footer = new() { Name = "TeamCityChooserButtons", Height = 46 };
        _cancel.HorizontalAlignment = HorizontalAlignment.Left; _cancel.Margin = new Thickness(121, 13, 0, 0);
        _ok.HorizontalAlignment = HorizontalAlignment.Right; _ok.Margin = new Thickness(0, 13, 121, 0);
        footer.Children.Add(_cancel); footer.Children.Add(_ok);
        Grid layout = new() { RowDefinitions = new RowDefinitions("*,Auto,46") };
        layout.Children.Add(frame); Grid.SetRow(_notice, 1); layout.Children.Add(_notice);
        Grid.SetRow(footer, 2); layout.Children.Add(footer); Content = layout;
        AddHandler(PointerPressedEvent, (_, _) => _interactionVersion++, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, args) =>
        {
            _interactionVersion++;
            if (args.Key == Key.Escape) { args.Handled = true; Cancel(); }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Opened += (_, _) =>
        {
            _projects.Focus(); ModalWindowActivation.AfterMapping(this, () => !_closing && IsVisible);
            _ = InitializeAsync();
        };
        Closing += (_, args) =>
        {
            if (_closeReady) { return; }
            if (!_decided) { Result = null; }
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingOwner = Owner as Window; Result = null; }
            Task pending = BeginClose();
            if (!pending.IsCompleted) { args.Cancel = true; _ = CloseWhenDrainedAsync(pending); }
            else { _closeReady = true; }
        };
        Closed += (_, _) =>
        {
            _ = BeginClose(); _lifetime.Dispose();
            Window? owner = _closingOwner; _closingOwner = null;
            if (owner is not null) { Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        };
    }

    internal Task InitializeAsync(CancellationToken cancellationToken = default)
        => _initialize ??= _closing ? Task.CompletedTask : Track(() => ReadProjectsAsync(cancellationToken));

    internal Task BeginClose()
    {
        if (_close is not null) { return _close; }
        _closing = true; _lifetime.Cancel();
        foreach (ProjectNode node in _nodes.Values) { node.Request?.Cancel(); }
        UpdateActions();
        return _close = Completion;
    }

    private async Task ReadProjectsAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        long interaction = _interactionVersion;
        try
        {
            TeamCityPage<TeamCityProject> page = await _client.GetProjectsAsync(request.Token);
            if (_closing || request.IsCancellationRequested) { return; }
            BuildProjectTree(page.Items);
            _projectsNotice = PageNotice(page.Truncated, page.Diagnostics); UpdateNotice();
            foreach (TreeViewItem root in _projects.Items.OfType<TreeViewItem>()) { root.IsExpanded = true; }
            if (_initial is null || interaction != _interactionVersion || !_nodes.TryGetValue(_initial.ProjectId, out ProjectNode? target)) { return; }
            for (ProjectNode? ancestor = target; ancestor is not null; ancestor = ancestor.Parent) { ancestor.Item.IsExpanded = true; }
            _projects.SelectedItem = target.Item;
            await LoadBuildsAsync(target);
            if (_closing || request.IsCancellationRequested || interaction != _interactionVersion) { return; }
            TreeViewItem selected = target.Item.Items.OfType<TreeViewItem>().FirstOrDefault(item => item.Tag is TeamCityBuildType build
                && build.Id == _initial.BuildTypeId) ?? target.Item;
            _projects.SelectedItem = selected; selected.BringIntoView();
            if (_projects.IsKeyboardFocusWithin) { selected.Focus(); }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        { if (!_closing && !request.IsCancellationRequested) { LastError = error; _projectsNotice = error.Message; UpdateNotice(); } }
    }

    private void BuildProjectTree(IReadOnlyList<TeamCityProject> projects)
    {
        TeamCityProject[] active = projects.Where(project => !project.Archived).ToArray();
        Dictionary<string, TeamCityProject> byId = new(StringComparer.Ordinal);
        foreach (TeamCityProject project in active)
        {
            if (string.IsNullOrEmpty(project.Id) || !byId.TryAdd(project.Id, project))
            { throw new InvalidDataException(T("Invalid or duplicate TeamCity project ID.")); }
        }
        // Refuse malformed parentage before making controls: no silently omitted project or recursive cycle.
        foreach (TeamCityProject project in active)
        {
            HashSet<string> ancestors = new(StringComparer.Ordinal) { project.Id };
            for (string? parent = project.ParentProjectId; parent is not null; parent = byId[parent].ParentProjectId)
            {
                if (!byId.ContainsKey(parent) || !ancestors.Add(parent))
                { throw new InvalidDataException(T("Invalid TeamCity project hierarchy.")); }
            }
        }
        foreach (TeamCityProject project in active)
        {
            TreeViewItem item = Node(project.Name, project); ProjectNode state = new(project, item);
            _nodes.Add(project.Id, state);
            item.PropertyChanged += (_, args) =>
            {
                if (args.Property != TreeViewItem.IsExpandedProperty) { return; }
                if (item.IsExpanded) { _ = LoadBuildsAsync(state); }
                else if (state.Request is not null)
                { state.Generation++; CancellationTokenSource previous = state.Request; state.Request = null; state.CurrentRead = null; previous.Cancel(); }
            };
        }
        foreach (ProjectNode state in _nodes.Values)
        {
            if (state.Project.ParentProjectId is string parent) { state.Parent = _nodes[parent]; }
            ProjectNode[] children = _nodes.Values.Where(child => child.Project.ParentProjectId == state.Project.Id)
                .OrderBy(child => child.Project.Name, StringComparer.CurrentCulture).ToArray();
            foreach (ProjectNode child in children) { state.Item.Items.Add(child.Item); }
            if (children.Length == 0) { state.Item.Items.Add(Placeholder()); }
        }
        foreach (ProjectNode root in _nodes.Values.Where(state => state.Parent is null).OrderBy(state => state.Project.Name, StringComparer.CurrentCulture))
        { _projects.Items.Add(root.Item); }
    }

    private Task LoadBuildsAsync(ProjectNode state)
    {
        if (_closing || state.Loaded) { return Task.CompletedTask; }
        if (state.CurrentRead is Task existing) { return existing; }
        int generation = ++state.Generation;
        CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        state.Request = request;
        return state.CurrentRead = Track(async () =>
        {
            bool Current() => !_closing && !request.IsCancellationRequested && generation == state.Generation;
            try
            {
                TeamCityPage<TeamCityBuildType> page = await _client.GetProjectBuildTypesAsync(state.Project.Id, request.Token);
                if (!Current()) { return; }
                if (page.Items.Any(build => string.IsNullOrEmpty(build.Id) || build.ProjectId != state.Project.Id)
                    || page.Items.Select(build => build.Id).Distinct(StringComparer.Ordinal).Count() != page.Items.Count)
                { throw new InvalidDataException(T("Invalid TeamCity build configuration.")); }
                foreach (TreeViewItem placeholder in state.Item.Items.OfType<TreeViewItem>().Where(item => item.Tag is null).ToArray())
                { state.Item.Items.Remove(placeholder); }
                foreach (TeamCityBuildType build in page.Items.OrderBy(build => build.Id, StringComparer.CurrentCulture))
                { state.Item.Items.Add(Node(build.DisplayName, build)); }
                state.Loaded = true; state.Notice = PageNotice(page.Truncated, page.Diagnostics); UpdateNotice();
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (Current())
                {
                    LastError = error; state.Notice = error.Message; UpdateNotice();
                    if (!state.Item.Items.OfType<TreeViewItem>().Any()) { state.Item.Items.Add(Placeholder()); }
                }
            }
            finally
            {
                if (generation == state.Generation) { state.CurrentRead = null; state.Request = null; }
                request.Dispose();
            }
        });
    }

    private TreeViewItem Node(string caption, object model)
    {
        TextBlock label = new() { Text = caption, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        TreeViewItem item = new() { Header = label, Tag = model };
        label.Bind(TextBlock.ForegroundProperty, new Binding(nameof(Foreground)) { Source = item });
        void Palette()
        {
            bool dark = item.ActualThemeVariant == ThemeVariant.Dark;
            string color = item.IsSelected ? dark ? "WindowText" : "HighlightText" : model is TeamCityBuildType ? "Highlight" : "WindowText";
            item.Foreground = Brush.Parse((dark ? _darkColors : _lightColors)[color]);
        }
        item.AttachedToVisualTree += (_, _) => Palette();
        item.PropertyChanged += (_, args) => { if (args.Property == TreeViewItem.IsSelectedProperty || args.Property == ActualThemeVariantProperty) { Palette(); } };
        item.TemplateApplied += (_, args) =>
        { if (args.NameScope.Find<Border>("PART_LayoutRoot") is Border surface) { surface.Focusable = false; } };
        ToolTip.SetTip(item, caption); Palette(); return item;
    }

    private TreeViewItem Placeholder() => new() { Header = T("Loading..."), IsEnabled = false };
    private string? PageNotice(bool truncated, IReadOnlyList<string> diagnostics)
    {
        string[] messages = (truncated ? new[] { T("The TeamCity result is incomplete.") } : []).Concat(diagnostics).ToArray();
        return messages.Length == 0 ? null : string.Join("\n", messages);
    }
    private void UpdateNotice()
    {
        string text = string.Join("\n", new[] { _projectsNotice }.Concat(_nodes.Values.Select(node => node.Notice))
            .Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal));
        _notice.Text = text; _notice.IsVisible = text.Length != 0; ToolTip.SetTip(_notice, text);
    }
    private void UpdateActions() => _ok.IsEnabled = !_closing && _projects.SelectedItem is TreeViewItem { Tag: TeamCityBuildType };
    private void Accept()
    {
        if (_closing || _projects.SelectedItem is not TreeViewItem { Tag: TeamCityBuildType build }) { return; }
        _decided = true; Result = new(build.ProjectId, build.Id); if (IsVisible) { Close(Result); }
    }
    private void Cancel()
    { if (_closing) { return; } _decided = true; Result = null; if (IsVisible) { Close(null); } }
    private async Task CloseWhenDrainedAsync(Task pending)
    { await pending; _closeReady = true; if (IsVisible) { Close(Result); } }
    private Task Track(Func<Task> action)
    {
        Task task = RunAsync(); _work.Add(task); BusyChanged?.Invoke(); _ = RemoveAsync(task); return task;
        async Task RunAsync()
        {
            // Register before an immediate client response can publish controls or start another read.
            await Task.Yield();
            try { if (!_closing) { await action(); } }
            catch (Exception error) { if (!_closing) { LastError = error; _projectsNotice = error.Message; UpdateNotice(); } }
        }
    }
    private async Task RemoveAsync(Task task)
    { try { await task; } finally { _work.Remove(task); BusyChanged?.Invoke(); } }
    private Button Button(string name, string caption, int tabIndex) => new()
    {
        Name = name, Content = T(name, caption), TabIndex = tabIndex, Width = 56, Height = 19,
        MinWidth = 0, MinHeight = 0, Padding = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Top
    };
    private string T(string name, string source)
    {
        string contextual = _translations.Translate("TeamCityBuildChooser", name, "Text", source);
        return contextual == source ? _translations.Translate(source) : contextual;
    }
    private string T(string source) => _translations.Translate(source);
    private sealed class ProjectNode(TeamCityProject project, TreeViewItem item)
    {
        internal TeamCityProject Project { get; } = project;
        internal TreeViewItem Item { get; } = item;
        internal ProjectNode? Parent { get; set; }
        internal bool Loaded { get; set; }
        internal int Generation { get; set; }
        internal CancellationTokenSource? Request { get; set; }
        internal Task? CurrentRead { get; set; }
        internal string? Notice { get; set; }
    }
}

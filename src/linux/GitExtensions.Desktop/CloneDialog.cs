using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

public sealed class CloneDialog : StartupRepositoryDialog
{
    private readonly ClonePreferencesStore _preferences = new();
    private readonly CloneSourcesStore _sourcesStore = new();
    private readonly RepositoryHistoryStore _localHistoryStore = new();
    private readonly GitRepository? _context;
    private readonly bool _explicitSource;
    private readonly ComboBox _sourceHistory = new() { Name = "CloneSources", Width = 28, Height = 24 };
    private readonly ComboBox _destinationHistory = new() { Name = "CloneDestinations", Width = 28, Height = 24 };
    private Task? _initialization;
    private long _sourceVersion;
    private Window? _closingOwner;
    private readonly Action<Uri> _openHelp;
    private CloneDirectoryCompletion? _directoryCompletion;
    private readonly Dictionary<Task, CancellationTokenSource> _branchRequests = [];
    private readonly List<Exception> _branchFailures = [];
    private readonly TextBlock _branchNotice = new() { Name = "CloneBranchStatus", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private long _branchVersion;
    private bool _updatingBranches, _branchClosing, _branchClosePending;
    private readonly TextBox _source = new() { Name = "CloneSource", Height = 24 };
    private readonly TextBox _destination = new() { Name = "CloneDestination", Height = 24, MaxHeight = 80 };
    private readonly TextBox _subdirectory = new() { Name = "CloneSubdirectory", Height = 24 };
    private readonly TextBox _branch = new() { Name = "CloneBranch", Height = 24 };
    private readonly ComboBox _branches = new() { Name = "CloneBranches", MinWidth = 36, Width = 36, Height = 24 };
    private readonly TextBlock _location = new() { Name = "CloneLocation", TextWrapping = TextWrapping.Wrap };
    private readonly RadioButton _personal = new() { Name = "ClonePersonal", GroupName = "CloneType", IsChecked = true, Height = 24, Padding = new Thickness(0) };
    private readonly RadioButton _bare = new() { Name = "CloneBare", GroupName = "CloneType", Height = 24, Padding = new Thickness(0) };
    private readonly CheckBox _recursive = new() { Name = "CloneSubmodules", IsChecked = true, Height = 24, Padding = new Thickness(0) };
    private readonly CheckBox _fullHistory = new() { Name = "CloneFullHistory", IsChecked = true, Height = 24, Padding = new Thickness(0) };
    private readonly string _defaultBranch;
    private readonly string _noCheckout;

    public GitProcessDialogResult? LastProcess { get; private set; }
    public bool IsBranchLoading => _branchRequests.Keys.Any(task => !task.IsCompleted);
    public Exception? BranchLoadError { get; private set; }
    public IReadOnlyList<Exception> BranchLoadFailures => _branchFailures.ToArray();
    internal Task DirectoryCompletionIdle => _directoryCompletion?.Idle ?? Task.CompletedTask;
    internal bool IsDirectoryCompletionPending => _directoryCompletion?.IsPending == true;
    internal Func<string, IReadOnlyList<string>, Task>? DirectoryCompletionBeforeApply
    {
        get => _directoryCompletion?.BeforeApply;
        set { if (_directoryCompletion is not null) { _directoryCompletion.BeforeApply = value; } }
    }
    public static Uri HelpUri
    {
        get
        {
            Version? version = typeof(CloneDialog).Assembly.GetName().Version;
            string section = version is not null && !(version.Major == 33 && version.Minor == 33)
                ? "en/release-" + version.Major + "." + version.Minor + "/" : "en/main/";
            return new("https://git-extensions-documentation.readthedocs.io/" + section + "dashboard.html#clone-repository");
        }
    }

    public CloneDialog(string destination = "", string source = "", GitRepository? repository = null, Action<Uri>? openHelp = null) : base("Clone repository")
    {
        _context = repository;
        _openHelp = openHelp ?? (uri => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open")
            { UseShellExecute = false, ArgumentList = { uri.AbsoluteUri } }));
        _explicitSource = !string.IsNullOrEmpty(source);
        // Retain the owner's request before a busy Process can veto child closure.
        // StartupRepositoryDialog cancels our token and closes only after drainage.
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        Closing += (_, args) =>
        {
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingOwner = Owner as Window; }
            _branchClosing = true;
            if (IsBranchLoading)
            {
                args.Cancel = true; CancelBranchRequests();
                if (!_branchClosePending) { _branchClosePending = true; _ = CloseAfterBranchRequestsAsync(); }
            }
        };
        Closed += (_, _) =>
        {
            _directoryCompletion?.Dispose();
            Window? owner = _closingOwner; _closingOwner = null;
            if (owner is not null)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } });
            }
        };
        Width = 655;
        _defaultBranch = T("(default: remote HEAD)");
        _noCheckout = T("(none: don't checkout after clone)");
        _branch.Text = _defaultBranch;
        _personal.Content = AccessCaption("PersonalRepository", "&Personal repository");
        _bare.Content = AccessCaption("CentralRepository", "P&ublic repository, no working directory  (--bare)");
        _recursive.Content = T("Initialize all submodules");
        _fullHistory.Content = AccessCaption("cbDownloadFullHistory", "Download full &history");
        ToolTip.SetTip(_fullHistory, Translations.Translate("FormClone", "cbDownloadFullHistory", "ttHints",
            "The default Git behavior is to download all historical revisions.\nIf you turn this off, we'll only download the latest revision for all branches.\n\nActual command line (if unchecked): --depth 1 --no-single-branch"));
        _branches.ItemsSource = new[] { _defaultBranch, _noCheckout };
        ConfigureArrowOnly(_branches);
        _branches.SelectionChanged += (_, _) => { if (!_updatingBranches && _branches.SelectedItem is string branch) { _branch.Text = branch; } };
        _branches.DropDownOpened += async (_, _) => await LoadBranchesAsync();
        _source.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty) { return; }
            _sourceVersion++;
            _branchVersion++; CancelBranchRequests(); BranchLoadError = null; _branchNotice.IsVisible = false;
            string name = RepositoryName(_source.Text ?? "");
            if (name.Length > 0) { _subdirectory.Text = name; }
            _branch.Text = _defaultBranch;
            _updatingBranches = true;
            try { _branches.ItemsSource = new[] { _defaultBranch, _noCheckout }; }
            finally { _updatingBranches = false; }
            UpdateLocation();
        };
        _destination.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateLocation(); } };
        _subdirectory.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateLocation(); } };
        _bare.IsCheckedChanged += (_, _) => UpdateActions();
        _branch.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) { UpdateActions(); } };
        PropertyChanged += (_, change) => { if (change.Property == ActualThemeVariantProperty) { UpdateLocation(); } };
        ClonePreferences preferences = _preferences.Load();
        _recursive.IsChecked = preferences.InitializeSubmodules;
        _destination.Text = string.IsNullOrEmpty(destination) ? preferences.Destination : destination;
        if (string.IsNullOrWhiteSpace(_destination.Text) && repository is not null) { _destination.Text = Path.GetDirectoryName(repository.Path); }
        _source.Text = _explicitSource ? source : _sourcesStore.Load().FirstOrDefault() ?? "";
        Grid fields = ExportDialogControls.Fields();
        fields.ColumnDefinitions = new ColumnDefinitions("150,*,Auto");
        ExportDialogControls.Field(fields, AccessLabel("repositoryLabel", "Repository to &clone:", _source), HistoryInput(_source, _sourceHistory, _sourcesStore.Load()), BrowseButton("BrowseCloneSource", "FromBrowse", "&Browse", _source));
        string[] destinations = LoadLocalHistory().Recent.Select(item => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(item.Path))).Where(path => !string.IsNullOrEmpty(path)).Select(path => path!).Distinct(StringComparer.Ordinal).ToArray();
        Control destinationInput = HistoryInput(_destination, _destinationHistory, destinations);
        _directoryCompletion = new(_destination, (Panel)destinationInput);
        ExportDialogControls.Field(fields, AccessLabel("destinationLabel", "&Destination:", _destination), destinationInput, BrowseButton("BrowseCloneDestination", "ToBrowse", "B&rowse", _destination));
        ExportDialogControls.Field(fields, AccessLabel("subdirectoryLabel", "&Subdirectory to create:", _subdirectory), _subdirectory);
        Grid branch = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        branch.Children.Add(_branch);
        Grid.SetColumn(_branches, 1);
        branch.Children.Add(_branches);
        ExportDialogControls.Field(fields, AccessLabel("brachLabel", "Br&anch:", _branch), branch, Action("RefreshCloneBranches", "Refresh", LoadBranchesAsync));
        StackPanel body = new() { Margin = new Thickness(12), Spacing = 8 };
        body.Children.Add(fields);
        body.Children.Add(new ScrollViewer { Name = "CloneBranchNoticeScroll", Content = _branchNotice, MaxHeight = 64,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        body.Children.Add(_location);
        body.Children.Add(ExportDialogControls.Group(T("Repository type"), new StackPanel { Spacing = 4, Children = { _personal, _bare } }));
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Children = { _recursive, _fullHistory } });
        StackPanel buttons = ExportDialogControls.Buttons(Action("CancelClone", "Cancel", () => { Close(); return Task.CompletedTask; }), Action("CloneRepository", "Clone", CloneAsync));
        Button help = Action("CloneHelp", "?", OpenHelpAsync); help.MinWidth = help.Width = 25;
        ToolTip.SetTip(help, T("Help")); Avalonia.Automation.AutomationProperties.SetName(help, T("Help"));
        buttons.Margin = new Thickness(0);
        DockPanel footer = new() { LastChildFill = false, Margin = new Thickness(10, 6) };
        DockPanel.SetDock(help, Dock.Left); footer.Children.Add(help); DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); Root.Children.Add(footer);
        Root.Children.Add(body);
        Track(_source, _destination, _subdirectory, _branch, _branches, _sourceHistory, _destinationHistory, _personal, _bare, _recursive, _fullHistory);
        UpdateLocation();
        UpdateActions();
        AddHandler(InputElement.KeyDownEvent, async (_, args) =>
        {
            if (args.Key == Key.F1 && !IsBusy && !_branchClosing) { args.Handled = true; await OpenHelpAsync(); }
        }, RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            await InitializeAsync();
            if (IsVisible && !IsKeyboardFocusWithin) { _source.Focus(); }
        };
    }

    public Task InitializeAsync() => _initialization ??= _explicitSource ? Task.CompletedTask : RunAsync("Load repository", async token =>
    {
        long sourceVersion = _sourceVersion;
        string? proposed = null;
        try
        {
            if (Clipboard is not null && TryExtractSource(await ClipboardExtensions.TryGetTextAsync(Clipboard).WaitAsync(token) ?? "", out string clipboardSource)) { proposed = clipboardSource; }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Clipboard availability does not decide whether cloning is possible. */ }
        proposed ??= string.IsNullOrWhiteSpace(_source.Text) && _context is not null ? await _context.GetDefaultCloneSourceAsync(token) : null;
        token.ThrowIfCancellationRequested();
        if (proposed is not null && sourceVersion == _sourceVersion) { _source.Text = proposed; }
    });

    internal static bool TryExtractSource(string text, out string source)
    {
        source = text.Split(' ').FirstOrDefault(IsSource) ?? "";
        return source.Length > 0;
        static bool IsSource(string candidate) => !string.IsNullOrWhiteSpace(candidate)
            && (Uri.IsWellFormedUriString(candidate, UriKind.Absolute) && !candidate.Contains('?') && !candidate.Contains('#')
                || candidate.EndsWith(".git", StringComparison.CurrentCultureIgnoreCase) || candidate.EndsWith(".git/", StringComparison.CurrentCultureIgnoreCase)
                || Directory.Exists(Path.Combine(candidate, ".git")) || File.Exists(Path.Combine(candidate, ".git"))
                || File.Exists(Path.Combine(candidate, "HEAD")) && Directory.Exists(Path.Combine(candidate, "objects")));
    }

    private RepositoryHistoryState LoadLocalHistory()
    {
        try { return _localHistoryStore.Load(new PreferencesStore().Load().RecentRepositories); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return new(); }
    }

    private static Control HistoryInput(TextBox text, ComboBox history, IEnumerable<string> paths)
    {
        ConfigureArrowOnly(history);
        history.ItemsSource = paths.ToArray();
        history.ItemTemplate = new FuncDataTemplate<string>((path, _) => new TextBlock { Text = ExportDialogControls.DisplayPath(path ?? ""), MinWidth = 180, MaxWidth = 600, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 1) });
        history.SelectionChanged += (_, _) => { if (history.SelectedItem is string path) { text.Text = path; } };
        Grid input = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        input.Children.Add(text); Grid.SetColumn(history, 1); input.Children.Add(history);
        return input;
    }

    private static void ConfigureArrowOnly(ComboBox choice)
    {
        choice.Classes.Add("classic-arrow-only");
        choice.Resources["ComboBoxThemeMinWidth"] = 0d;
        choice.Padding = new Thickness(0);
        choice.SelectionBoxItemTemplate = new FuncDataTemplate<string>((_, _) => new TextBlock());
    }

    private AccessText AccessCaption(string item, string source)
        => new() { Text = Translations.TranslateAccessKeys("FormClone", item, "Text", source), TextWrapping = TextWrapping.Wrap };

    private Label AccessLabel(string item, string source, Control target)
        => new() { Name = "CloneAccess" + item, Content = AccessCaption(item, source), Target = target, Padding = new Thickness(0) };

    private Button BrowseButton(string name, string item, string source, TextBox target)
    {
        Button button = Action(name, "Browse", () => BrowseAsync(target));
        button.MinWidth = 94;
        Uri icon = new("avares://GitExtensions/Assets/Icons/BrowseFileExplorer.png");
        using Stream stream = AssetLoader.Open(icon);
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children =
        {
            new Image { Source = new Bitmap(stream), Width = 16, Height = 16 },
            AccessCaption(item, source)
        } };
        return button;
    }

    public GitCloneOptions ReadOptions()
    {
        string? branch = _branch.Text;
        bool noCheckout = branch == _noCheckout;
        if (noCheckout || branch == _defaultBranch || string.IsNullOrWhiteSpace(branch)) { branch = null; }
        return new(_bare.IsChecked == true, _recursive.IsChecked == true, branch, noCheckout, _fullHistory.IsChecked == true ? null : 1);
    }

    private async Task BrowseAsync(TextBox field)
    {
        string? path = await ExportDialogControls.FolderAsync(this, T("Choose directory"));
        if (path is not null) { field.Text = path; }
    }

    private Task OpenHelpAsync() => RunAsync("Help", _ => { _openHelp(HelpUri); return Task.CompletedTask; });

    private async Task LoadBranchesAsync()
    {
        if (IsBusy || _branchClosing || !IsVisible) { return; }
        CancelBranchRequests(); long version = ++_branchVersion, sourceVersion = _sourceVersion;
        string source = _source.Text ?? "";
        BranchLoadError = null; _branchNotice.IsVisible = false;
        CancellationTokenSource cancellation = new();
        Task operation = ReadBranchesAsync(source, version, sourceVersion, cancellation.Token);
        _branchRequests.Add(operation, cancellation);
        try { await operation; }
        finally { _branchRequests.Remove(operation); cancellation.Dispose(); }
    }

    private async Task ReadBranchesAsync(string source, long version, long sourceVersion, CancellationToken token)
    {
        using IDisposable execution = GitEnvironment.BeginOperation();
        GitRunner runner = GitEnvironment.Current.CreateRunner();
        try
        {
            IReadOnlyList<string> branches = await GitRepository.GetRemoteBranchesAsync(source, token, runner);
            token.ThrowIfCancellationRequested();
            if (_branchClosing || !IsVisible || version != _branchVersion || sourceVersion != _sourceVersion || source != _source.Text) { return; }
            // Preserve the latest manual edit/selection, not the text present when ls-remote began.
            string? selected = _branch.Text;
            _updatingBranches = true;
            try
            {
                _branches.ItemsSource = new[] { _defaultBranch, _noCheckout }.Concat(branches).ToArray();
                _branches.SelectedItem = _branches.ItemsSource.Cast<string>().FirstOrDefault(name => name == selected);
                _branch.Text = selected;
            }
            finally { _updatingBranches = false; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            _branchFailures.Add(error);
            if (!_branchClosing && IsVisible && version == _branchVersion && sourceVersion == _sourceVersion && source == _source.Text)
            { BranchLoadError = error; _branchNotice.Text = error.Message; _branchNotice.IsVisible = true; }
        }
    }

    private void CancelBranchRequests()
    { foreach (CancellationTokenSource request in _branchRequests.Values.ToArray()) { request.Cancel(); } }
    private Task DrainBranchRequestsAsync() => Task.WhenAll(_branchRequests.Keys.ToArray());
    private async Task CloseAfterBranchRequestsAsync()
    {
        await DrainBranchRequestsAsync();
        // RunAsync may still be draining its Process. Its existing deferred-close guard remains authoritative.
        if (IsVisible) { Close(); }
    }

    private Task CloneAsync() => RunAsync("Clone", async token =>
    {
        using IDisposable execution = GitEnvironment.BeginOperation();
        GitRunner runner = GitEnvironment.Current.CreateRunner();
        LastProcess = null;
        string directory = AbsoluteDirectory(_destination.Text);
        string subdirectory = _subdirectory.Text ?? "";
        if (Path.IsPathRooted(subdirectory) || subdirectory.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
        { throw new ArgumentException("Subdirectory must remain inside the destination folder."); }
        string path = Path.GetFullPath(Path.Combine(directory, subdirectory));
        string source = _source.Text ?? "";
        GitCloneOptions options = ReadOptions();
        _branchVersion++; CancelBranchRequests(); await DrainBranchRequestsAsync();
        token.ThrowIfCancellationRequested();
        if (_branchClosing || !IsVisible) { throw new OperationCanceledException(token); }
        string description = "git clone -v --progress" + (options.Bare ? " --bare" : "")
            + (options.Depth is int depth ? " --depth=" + depth.ToString(CultureInfo.InvariantCulture) + " --no-single-branch" : "")
            + " — " + source;
        LastProcess = await GitProcessDialog.ShowAsync(this, directory, description, true,
            (ct, output) => GitRepository.CloneCommandAsync(source, path, options, ct, output, runner), token);
        if (LastProcess.WasCancelled || LastProcess.OwnerCloseRequested || LastProcess.WorkflowStopRequested
            || token.IsCancellationRequested || !IsVisible)
        { throw new OperationCanceledException(token); }
        if (LastProcess.CompletionError is Exception completionError) { ExceptionDispatchInfo.Capture(completionError).Throw(); }
        if (LastProcess.Error is Exception error) { ExceptionDispatchInfo.Capture(error).Throw(); }
        if (LastProcess.Result?.Success != true) { throw new InvalidOperationException(T("No native Git result was returned.")); }
        // Keep discovery out of the clone observer: its output is neither transfer
        // progress nor the native clone result displayed by the process window.
        GitRepository cloned = await GitRepository.OpenAsync(path, token, runner);
        token.ThrowIfCancellationRequested();
        if (!IsVisible) { throw new OperationCanceledException(token); }
        _preferences.Save(new(directory, options.RecurseSubmodules));
        RepositoryHistoryState history = LoadLocalHistory();
        _sourcesStore.Visit(source, history.HistorySize);
        try { _localHistoryStore.Save(history.Visit(cloned.Path)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        Complete(cloned);
    });

    private void UpdateLocation()
    {
        string destination = _destination.Text ?? "";
        bool multiline = destination.Contains('\n') || destination.Contains('\r');
        // Linux directory names can contain line breaks; keep their literal text editable.
        _destination.Height = multiline ? double.NaN : 24;
        _destination.VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center;
        _destination.TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ScrollViewer.SetHorizontalScrollBarVisibility(_destination, multiline ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden);
        string subdirectory = _subdirectory.Text ?? "";
        try
        {
            string path = Path.Combine(destination, subdirectory);
            bool exists = Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
            _location.Text = string.Format(CultureInfo.CurrentCulture, T("The repository will be cloned to a new directory located here:\n{0}"), ExportDialogControls.DisplayPath(path)) + "\n" + T(exists ? "(Directory already exists)" : "(New directory)");
            if (exists) { _location.Foreground = new SolidColorBrush(ClassicTextContrast.AdaptForeground(this, Colors.Red)); }
            else { _location.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ClassicTextBrush")); }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        { _location.Text = exception.Message; }
    }

    protected override void UpdateActions() => _recursive.IsEnabled = !IsBusy && _bare.IsChecked != true && _branch.Text != _noCheckout;

    private static string RepositoryName(string source) => RepositorySourceControls.RepositoryName(source);
}

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop;

internal sealed record ChooseCommitRequest(string? Preselect = null, bool CurrentBranchOnly = false, string? LastRevisionToDisplayHash = null,
    bool ShowArtificial = false, GitCompareRevision? PreselectSelection = null);
internal sealed record ChooseCommitControlNames(string Rows, string Accept, string Cancel, string Search, string SearchAction);

/// <summary>The shared commit chooser, with typed artificial endpoints only for comparison callers.</summary>
internal partial class ChooseCommitDialog : RepositoryDialog
{
    private readonly ChooseCommitRequest _request;
    private readonly CancellationToken _token;
    private readonly ChooseCommitSelectionPresenter _revisions;
    private readonly bool _showArtificial;
    private readonly TextBox _search;
    private readonly Button _accept;
    private readonly StackPanel _parents = new() { Name = "flowLayoutPanelParents", Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button[] _parentLinks;
    private GitLogQuery _query;
    private string _rememberedBranchFilter = "";
    private bool _branchFilterEnabled;
    private bool _closing, _loaded;
    private Task _work = Task.CompletedTask;
    internal Task Completion => ObserveCompletion();

    protected ChooseCommitDialog(GitRepository repository, ChooseCommitRequest request,
        ChooseCommitControlNames controls, CancellationToken token) : base(repository, "Choose Commit")
    {
        _request = request; _token = token;
        _historyLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        DesktopPreferences general;
        try { general = new PreferencesStore().Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { general = new(); }
        _defaultMaximum = general.MaxRevisionGraphCommits;
        _batchSize = Math.Clamp(general.HistoryLimit, 1, 5000);
        HistoryViewPreferences view = new HistoryViewPreferencesStore().Load();
        _query = new FileHistoryPreferencesStore().Load().ConfigureQuery(new(Count: _batchSize,
            All: !request.CurrentBranchOnly, Revision: request.CurrentBranchOnly ? "HEAD" : null, IgnoreCase: true,
            Reflog: view.ShowReflog)
        {
            LastRevisionToDisplayHash = request.LastRevisionToDisplayHash, Sort = view.Sort,
            IncludeStashes = view.ShowStashes, IncludeLatestStashInBranchFilter = view.ShowStashes,
            IncludeSessionRefs = view.ShowSessionCheckpoints
        });
        _showArtificial = request.ShowArtificial && view.ShowArtificialCommits;
        Name = GetType().Name; Title = Original("$this", "Choose Commit");
        // FormChooseCommit is 1105x515 at 120 DPI; keep the existing Linux minimums for the shared five-column presenter.
        Width = 1105 * 96d / 120; Height = 515 * 96d / 120; MinWidth = 620; MinHeight = 300; CanMinimize = false; ShowInTaskbar = false;
        using (Stream icon = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/git-extensions-logo.ico"))) { Icon = new WindowIcon(icon); }
        CollapseEmptyStatus(); ConstrainStatusHeight("ChooseCommitError", 48);
        _revisions = new(controls.Rows, _showArtificial, T, view, general.ShowGitStatusForArtificialCommits) { Margin = new Thickness(4, 0, 4, 4) };
        _revisions.Rows.Focusable = true;
        _search = new() { Name = controls.Search, Height = DesktopFontMetrics.ToolbarHeight };
        _accept = new() { Name = controls.Accept, Content = Original("btnOK", "OK"), MinWidth = 110, IsDefault = true };
        _accept.Click += (_, _) => Accept();
        Button cancel = new() { Name = controls.Cancel, Content = T("Cancel"), IsCancel = true, MinWidth = 75 };
        cancel.Click += (_, _) => RequestClose(null);
        Button go = new() { Name = "buttonGotoCommit", Content = Original("buttonGotoCommit", "Go to commit..."), MinWidth = 130 };
        go.Click += (_, _) => { if (!IsBusy && !_closing) { _work = GoToAsync(); } };
        _parentLinks = [ParentLink("linkLabelParent"), ParentLink("linkLabelParent2")];
        _parents.Children.Add(new TextBlock { Name = "labelParents", Text = Original("labelParents", "Parent(s):"), VerticalAlignment = VerticalAlignment.Center });
        foreach (Button link in _parentLinks) { _parents.Children.Add(link); }
        StackPanel find = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Name = "label1", Text = Original("label1", "Find specific commit:"), VerticalAlignment = VerticalAlignment.Center }, go
        } };
        Grid footer = new() { Margin = new Thickness(4, 3), ColumnDefinitions = new("*,Auto") };
        find.Margin = new Thickness(0, 0, 12, 0);
        footer.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { find, _parents } });
        // Keep the historical Linux Cancel IDs/actions while sharing the original chooser footer.
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(6, 0, 0, 0), Children = { cancel, _accept } };
        Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); Root.Children.Add(footer);
        Grid search = ExportDialogControls.Fields();
        ExportDialogControls.Field(search, T("Message"), _search, Action(controls.SearchAction, "Search", InitializeAsync));
        Button filter = Action("ChooseAdvancedFilter", "Advanced filter...", OpenFilterAsync);
        filter.Content = Controls.OriginalMenuItems.Icon("EditFilter.png");
        ToolTip.SetTip(filter, Translations.Translate("RevisionGrid", "filterToolStripMenuItem", "Text", "Advanced filter...") + " (Ctrl+I)");
        filter.Width = DesktopFontMetrics.ToolbarHeight; filter.Padding = new Thickness(2, 1); filter.Margin = new Thickness(0, 3, 6, 3);
        DockPanel.SetDock(filter, Dock.Right);
        DockPanel filters = new() { Children = { filter, search } };
        search.Margin = new Thickness(6, 3); DockPanel.SetDock(filters, Dock.Top); Root.Children.Add(filters); Root.Children.Add(_revisions);
        Track(_search, _revisions, _accept, go, _parents);
        _revisions.SelectionChanged += (_, _) =>
        {
            if (!_applyingHistory) { RecordSelectionIntent(); }
            UpdateSelection();
        };
        _revisions.AddHandler(PointerPressedEvent, (_, _) => RecordSelectionIntent(), RoutingStrategies.Tunnel);
        _revisions.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Space)
            { RecordSelectionIntent(); }
        }, RoutingStrategies.Tunnel);
        _search.AddHandler(PointerPressedEvent, (_, _) => RecordSelectionIntent(), RoutingStrategies.Tunnel);
        _search.AddHandler(KeyDownEvent, (_, _) => RecordSelectionIntent(), RoutingStrategies.Tunnel);
        _revisions.AcceptRequested += (_, _) => Accept();
        _revisions.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None && !IsBusy)
            { args.Handled = true; Accept(); }
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (sender, args) =>
        {
            if (args.Key == Key.Escape)
            { args.Handled = true; RequestClose(null); return; }
            if (args.Key == Key.I && args.KeyModifiers == KeyModifiers.Control && !IsBusy && !_closing)
            { args.Handled = true; _ = OpenFilterAsync(); }
        }, RoutingStrategies.Tunnel);
        Closing += OnHistoryClosing;
        ActualThemeVariantChanged += (_, _) => UpdateSelection();
        Opened += (_, _) => { if (!IsBusy && !_closing) { _revisions.FocusSelected(); } };
        UpdateSelection();
    }

    private string Original(string item, string source) => Translations.Translate("FormChooseCommit", item, "Text", source);

    public override Task InitializeAsync()
    {
        if (_closing || IsBusy) { return _work; }
        return _work = LoadAsync();
    }

    private async Task LoadAsync()
    {
        await RunAsync("Load revisions", async token =>
        {
            _query = _query with { Message = string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text };
            HistoryRead read = BeginHistoryRead();
            await read.Ready.Task.WaitAsync(token);
            if (read.Error is not null) { throw read.Error; }
        }, cancellationToken: _token);
        if (!_closing && IsVisible && LastError is null) { Status.Text = ""; _revisions.FocusSelected(); }
    }

    private Task OpenFilterAsync()
    {
        if (_closing || IsBusy) { return _work; }
        return _work = FilterAsync();
    }

    private async Task FilterAsync()
    {
        GitLogQuery? accepted = null;
        int? acceptedMaximum = null;
        bool acceptedSuccessfully = false;
        await RunAsync("Advanced filter...", async token =>
        {
            GitLogQuery draft = _query with { Message = string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text };
            HistoryFilterDialog dialog = new(draft, _rememberedBranchFilter, _branchFilterEnabled,
                commitLimit: new(_defaultMaximum, _maximumOverride))
            { RequestedThemeVariant = ActualThemeVariant };
            using CancellationTokenRegistration close = token.Register(() => Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) { dialog.Close(null); } }));
            try { accepted = await dialog.ShowDialog<GitLogQuery?>(this); }
            finally { dialog.Close(); }
            token.ThrowIfCancellationRequested();
            if (_closing || accepted is null) { return; }
            _rememberedBranchFilter = dialog.RememberedBranchFilter; _branchFilterEnabled = dialog.IsBranchFilterEnabled;
            acceptedMaximum = dialog.CommitLimitOverride;
            acceptedSuccessfully = true;
        }, cancellationToken: _token);
        if (_closing || !IsVisible) { return; }
        if (acceptedSuccessfully && accepted is not null)
        {
            _maximumOverride = acceptedMaximum;
            _query = accepted with { LastRevisionToDisplayHash = _request.LastRevisionToDisplayHash };
            _search.Text = _query.Message;
            await LoadAsync();
        }
        else { if (LastError is null) { Status.Text = ""; } _revisions.FocusSelected(); }
    }

    private void Accept()
    {
        if (_closing || IsBusy || _revisions.Selection is not ChooseCommitSelection selected) { return; }
        RequestClose(selected);
    }

    private Button ParentLink(string name)
    {
        Button link = new() { Name = name, Padding = default, MinHeight = 20, Background = Brushes.Transparent, BorderThickness = default };
        link.Click += (_, _) =>
        {
            if (!IsBusy && !_closing && link.Tag is { } target)
            { _work = NavigateParentAsync(target is string hash ? new(hash) : (GitCompareRevision)target); }
        };
        return link;
    }

    private void UpdateSelection()
    {
        _accept.IsEnabled = !IsBusy && _revisions.Selection is not null;
        IReadOnlyList<GitCompareRevision> parents = _revisions.Parents;
        _parents.IsVisible = parents.Count > 0;
        for (int i = 0; i < _parentLinks.Length; i++)
        {
            GitCompareRevision? parent = parents.ElementAtOrDefault(i);
            Button link = _parentLinks[i]; link.Tag = parent?.Kind == GitCompareRevisionKind.Commit ? parent.Revision : parent; link.IsVisible = parent is not null;
            string text = parent is null ? "" : parent.Kind == GitCompareRevisionKind.Commit ? parent.Revision[..Math.Min(8, parent.Revision.Length)] : T("Commit index");
            link.Content = new TextBlock { Text = text, TextDecorations = TextDecorations.Underline };
            link.Foreground = ActualThemeVariant == ThemeVariant.Dark ? Brush.Parse("#78b7ff") : Brushes.Blue;
        }
    }

    protected override void UpdateActions()
    {
        UpdateSelection();
        if (!IsBusy && !_closing && _history?.Error is Exception error) { ReportError(error); }
    }

    private async Task NavigateParentAsync(GitCompareRevision revision)
    {
        bool focus = true;
        await RunAsync("Choose revision", async token => { focus = await SelectLoadedAsync(revision, token); }, cancellationToken: _token, disableInputs: false);
        if (!_closing && IsVisible && focus && !_search.IsKeyboardFocusWithin)
        { if (LastError is null) { Status.Text = ""; } _revisions.FocusSelected(); }
    }

    private async Task GoToAsync()
    {
        bool focus = true;
        await RunAsync("Go to commit", async token =>
        {
            GoToCommitDialog dialog = new(Repository) { RequestedThemeVariant = ActualThemeVariant };
            dialog.Opened += async (_, _) => await dialog.InitializeAsync(token);
            using CancellationTokenRegistration close = token.Register(() => Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) { dialog.Close(); } }));
            try { await dialog.ShowDialog(this); }
            finally { dialog.Close(); await dialog.Completion; }
            token.ThrowIfCancellationRequested(); if (_closing || !dialog.WasAccepted) { return; }
            if (dialog.SelectedCommitHash is string hash) { focus = await SelectLoadedAsync(new(hash), token); }
            else
            {
                string message = Translations.Translate("MessageBoxes", "_noRevisionFoundError", "Text", "No revision found.");
                await SelectionErrorAsync(message, token);
            }
        }, cancellationToken: _token, disableInputs: false);
        if (!_closing && IsVisible && focus && !_search.IsKeyboardFocusWithin)
        { if (LastError is null) { Status.Text = ""; } _revisions.FocusSelected(); }
    }

    private async Task<bool> SelectLoadedAsync(GitCompareRevision revision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        HistoryRead? read = _history;
        long intent = RecordSelectionIntent();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_closing || intent != _selectionVersion || read != _history) { return false; }
            if (ApplySelection(revision)) { return true; }
            if (read is null || read.Completed) { break; }
            Task progress = read.Progress.Task;
            await progress.WaitAsync(token);
        }
        if (read?.Error is Exception failure) { throw failure; }
        string hash = revision.Kind == GitCompareRevisionKind.Commit ? revision.Revision : T("Commit index");
        string format = Translations.Translate("MessageBoxes", "_cannotFindRevisionFilter", "Text",
            "Revision \"{0}\" is not visible in the revision grid. Remove the revision filter.");
        await SelectionErrorAsync(string.Format(CultureInfo.CurrentCulture, format, hash[..Math.Min(8, hash.Length)]), token);
        return true;
    }

    private Task SelectionErrorAsync(string message, CancellationToken token)
        => Dialogs.ShowMessageAsync(this, Translations.Translate("MessageBoxes", "_cannotFindRevisionCaption", "Text", "Cannot find revision"), message, false, token);

    protected static async Task<GitCommit?> ShowPickerAsync(ChooseCommitDialog picker, Window owner, CancellationToken token)
        => (await ShowSelectionAsync(picker, owner, token))?.Commit;

    protected static async Task<GitCompareRevision?> ShowComparisonPickerAsync(ChooseCommitDialog picker, Window owner, CancellationToken token)
        => (await ShowSelectionAsync(picker, owner, token))?.Revision;

    private static async Task<ChooseCommitSelection?> ShowSelectionAsync(ChooseCommitDialog picker, Window owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (!owner.IsVisible) { return null; }
        picker.RequestedThemeVariant = owner.ActualThemeVariant;
        picker.Opened += async (_, _) => await picker.InitializeAsync();
        using CancellationTokenRegistration close = token.Register(() => Dispatcher.UIThread.Post(() => { if (picker.IsVisible) { picker.Close(null); } }));
        try
        {
            ChooseCommitSelection? selected = await picker.ShowDialog<ChooseCommitSelection?>(owner);
            await picker.Completion; token.ThrowIfCancellationRequested(); return owner.IsVisible ? selected : null;
        }
        finally { picker.Close(); await picker.Completion; }
    }
}

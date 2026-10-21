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
internal class ChooseCommitDialog : RepositoryDialog
{
    private readonly ChooseCommitRequest _request;
    private readonly CancellationToken _token;
    private readonly ChooseCommitSelectionPresenter _revisions;
    private readonly bool _showArtificial;
    private readonly TextBox _search;
    private readonly Button _accept;
    private readonly StackPanel _parents = new() { Name = "flowLayoutPanelParents", Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button[] _parentLinks;
    private bool _closing, _loaded;
    private Task _work = Task.CompletedTask;
    internal Task Completion => _work;

    protected ChooseCommitDialog(GitRepository repository, ChooseCommitRequest request,
        ChooseCommitControlNames controls, CancellationToken token) : base(repository, "Choose Commit")
    {
        _request = request; _token = token;
        _showArtificial = request.ShowArtificial && new HistoryViewPreferencesStore().Load().ShowArtificialCommits;
        Name = GetType().Name; Title = Original("$this", "Choose Commit");
        // FormChooseCommit is 1105x515 at 120 DPI; keep the existing Linux minimums for the shared five-column presenter.
        Width = 1105 * 96d / 120; Height = 515 * 96d / 120; MinWidth = 620; MinHeight = 300; CanMinimize = false; ShowInTaskbar = false;
        using (Stream icon = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/git-extensions-logo.ico"))) { Icon = new WindowIcon(icon); }
        CollapseEmptyStatus(); ConstrainStatusHeight("ChooseCommitError", 48);
        _revisions = new(controls.Rows, _showArtificial, T) { Margin = new Thickness(4, 0, 4, 4) };
        _revisions.Rows.Focusable = true;
        _search = new() { Name = controls.Search, Height = DesktopFontMetrics.ToolbarHeight };
        _accept = new() { Name = controls.Accept, Content = Original("btnOK", "OK"), MinWidth = 110, IsDefault = true };
        _accept.Click += (_, _) => Accept();
        Button cancel = new() { Name = controls.Cancel, Content = T("Cancel"), IsCancel = true, MinWidth = 75 };
        cancel.Click += (_, _) => Close(null);
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
        search.Margin = new Thickness(6, 3); DockPanel.SetDock(search, Dock.Top); Root.Children.Add(search); Root.Children.Add(_revisions);
        Track(_search, _revisions, _accept, go, _parents);
        _revisions.SelectionChanged += (_, _) => UpdateSelection();
        _revisions.AcceptRequested += (_, _) => Accept();
        _revisions.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None && !IsBusy)
            { args.Handled = true; Accept(); }
        }, RoutingStrategies.Tunnel);
        Closing += (_, _) => _closing = true;
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
            using IDisposable environment = Repository.BeginOperation();
            GitCompareRevision? preserve = _revisions.Selection?.Revision;
            string? preselect = null;
            if (!_loaded && !string.IsNullOrWhiteSpace(_request.Preselect))
            {
                try { preselect = await Repository.ResolveRevisionAsync(_request.Preselect, token); }
                catch (Exception error) when (error is GitCommandException or ArgumentException) { /* Windows ignores an unresolved preselection. */ }
            }
            GitStatus status = await Repository.GetStatusAsync(token);
            IReadOnlyList<GitCommit> revisions = _request.CurrentBranchOnly && status.Head is null ? []
                : await Repository.GetLogAsync(new GitLogQuery(Count: 500, All: !_request.CurrentBranchOnly,
                    Revision: _request.CurrentBranchOnly ? status.Head : null,
                    Message: string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text)
                    { LastRevisionToDisplayHash = _request.LastRevisionToDisplayHash }, token);
            IReadOnlyList<GitReference> references = await Repository.GetReferencesAsync(token);
            token.ThrowIfCancellationRequested();
            if (_closing) { return; }
            _revisions.SetData(revisions, references, status.Head, _showArtificial && !Repository.IsBare);
            GitCompareRevision? target = !_loaded && _request.PreselectSelection?.Kind is (GitCompareRevisionKind.Index or GitCompareRevisionKind.WorkingTree)
                ? _request.PreselectSelection : preselect is not null ? new(preselect) : preserve;
            if (!_revisions.Select(target)) { _revisions.SelectFirst(); }
            _loaded = true; Status.Text = ""; UpdateSelection();
        }, cancellationToken: _token);
        if (!_closing && IsVisible && LastError is null) { _revisions.FocusSelected(); }
    }

    private void Accept()
    {
        if (_closing || IsBusy || _revisions.Selection is not ChooseCommitSelection selected) { return; }
        Close(selected);
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

    protected override void UpdateActions() => UpdateSelection();

    private async Task NavigateParentAsync(GitCompareRevision revision)
    {
        await RunAsync("Choose revision", token => SelectLoadedAsync(revision, token), cancellationToken: _token);
        if (!_closing && IsVisible) { if (LastError is null) { Status.Text = ""; } _revisions.FocusSelected(); }
    }

    private async Task GoToAsync()
    {
        await RunAsync("Go to commit", async token =>
        {
            GoToCommitDialog dialog = new(Repository) { RequestedThemeVariant = ActualThemeVariant };
            dialog.Opened += async (_, _) => await dialog.InitializeAsync(token);
            using CancellationTokenRegistration close = token.Register(() => Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) { dialog.Close(); } }));
            try { await dialog.ShowDialog(this); }
            finally { dialog.Close(); await dialog.Completion; }
            token.ThrowIfCancellationRequested(); if (_closing || !dialog.WasAccepted) { return; }
            if (dialog.SelectedCommitHash is string hash) { await SelectLoadedAsync(new(hash), token); }
            else
            {
                string message = Translations.Translate("MessageBoxes", "_noRevisionFoundError", "Text", "No revision found.");
                await SelectionErrorAsync(message, token);
            }
        }, cancellationToken: _token);
        if (!_closing && IsVisible) { if (LastError is null) { Status.Text = ""; } _revisions.FocusSelected(); }
    }

    private async Task SelectLoadedAsync(GitCompareRevision revision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_revisions.Select(revision)) { return; }
        string hash = revision.Kind == GitCompareRevisionKind.Commit ? revision.Revision : T("Commit index");
        string format = Translations.Translate("MessageBoxes", "_cannotFindRevisionFilter", "Text",
            "Revision \"{0}\" is not visible in the revision grid. Remove the revision filter.");
        await SelectionErrorAsync(string.Format(CultureInfo.CurrentCulture, format, hash[..Math.Min(8, hash.Length)]), token);
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

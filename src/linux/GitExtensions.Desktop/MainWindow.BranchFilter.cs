using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private enum HistoryBranchScope { All, Current, Filtered }
    private HistoryBranchScope _historyBranchScope;
    private string? _acceptedBranchFilter;
    private IReadOnlyList<GitReference> _branchFilterReferences = [];
    private BranchHistoryFilterComboBox? _branchFilterOverflow;
    private TextBox? _quickFilterOverflowInput;
    private bool _synchronizingBranchDraft, _branchLocalSuggestions = true, _branchRemoteSuggestions, _branchTagSuggestions;
    private bool _branchFilterClosingAccepted;
    private readonly List<MenuItem> _historyScopeChoices = [], _branchSuggestionChoices = [];

    private string NoBranchResults => _translations.Translate("TranslatedStrings", "_noResultsFound", "Text", "<No results found>");
    private bool IsBranchFilterOwnerClosing => _branchFilterClosingAccepted || !IsVisible;

    protected override void OnClosing(WindowClosingEventArgs args)
    {
        bool wasClosing = _branchFilterClosingAccepted;
        _branchFilterClosingAccepted = true;
        base.OnClosing(args);
        if (args.Cancel) { _branchFilterClosingAccepted = wasClosing; }
    }

    private void ConfigureBranchFilter(BranchHistoryFilterComboBox combo, string name)
    {
        combo.Name = name; combo.Suggestions = BranchSuggestions;
        ToolTip.SetTip(combo, T("Branch filter"));
        combo.Classes.Add("browse-toolbar-dropdown");
        combo.Styles.Add(new Style(selector => selector.OfType<ComboBox>().Class("browse-toolbar-dropdown").Template().OfType<PathIcon>().Name("DropDownGlyph"))
        {
            Setters = { new Setter(PathIcon.DataProperty, ToolbarDropdownGeometry), new Setter(WidthProperty, 6d),
                new Setter(HeightProperty, 3d), new Setter(MarginProperty, new Thickness(0)),
                new Setter(RenderTransformProperty, new TranslateTransform(0, -1)),
                new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Center), new Setter(VerticalAlignmentProperty, VerticalAlignment.Center) }
        });
        combo.DraftChanged += (_, _) =>
        {
            if (_synchronizingBranchDraft) { return; }
            _synchronizingBranchDraft = true;
            try
            {
                if (!ReferenceEquals(combo, _historyRevision)) { _historyRevision.SetDraft(combo.Text); }
                if (!ReferenceEquals(combo, _branchFilterOverflow)) { _branchFilterOverflow?.SetDraft(combo.Text); }
            }
            finally { _synchronizingBranchDraft = false; }
        };
        combo.AcceptRequested += async (_, _) => await ApplyBranchFilterAsync();
        if (name == "BranchHistoryFilterOverflow")
        { combo.AddHandler(KeyDownEvent, RelayBranchFilterFocusShortcut, Avalonia.Interactivity.RoutingStrategies.Tunnel); }
    }

    private IReadOnlyList<string> BranchSuggestions(string draft)
    {
        bool all = !_branchLocalSuggestions && !_branchRemoteSuggestions && !_branchTagSuggestions;
        string[] matches = _branchFilterReferences.Where(reference => all ||
                _branchLocalSuggestions && reference.IsBranch || _branchRemoteSuggestions && reference.IsRemote || _branchTagSuggestions && reference.IsTag)
            .Select(reference => reference.IsBranch || reference.IsRemote || reference.IsTag ? reference.ShortName
                : reference.Name.StartsWith("refs/", StringComparison.Ordinal) ? reference.Name[5..] : reference.Name)
            .Where(name => name.Contains(draft, StringComparison.InvariantCultureIgnoreCase)).ToArray();
        return matches.Length == 0 ? [NoBranchResults] : matches;
    }

    private void SetBranchFilterDraft(string? text)
    {
        _synchronizingBranchDraft = true;
        try { _historyRevision.SetDraft(text); _branchFilterOverflow?.SetDraft(text); }
        finally { _synchronizingBranchDraft = false; }
    }

    private void UpdateBranchFilterReferences(IReadOnlyList<GitReference> references, bool opening)
    {
        _branchFilterReferences = references;
        _historyRevision.InvalidateSuggestions(close: opening); _branchFilterOverflow?.InvalidateSuggestions(close: opening);
        if (opening) { _acceptedBranchFilter = null; _historyBranchScope = HistoryBranchScope.All; SetBranchFilterDraft(null); }
    }

    private void ResetBranchFilter()
    {
        UpdateBranchFilterReferences([], opening: true);
        _historyQuery = _historyQuery with { Revision = null, BranchFilter = null, All = true };
    }

    private void SetNavigationBranchFilter(string reference)
    {
        _navigationRevisionFilter = reference; _acceptedBranchFilter = reference; _historyBranchScope = HistoryBranchScope.Filtered;
        _historyQuery = _historyQuery with { BranchFilter = reference, Revision = null, All = true };
        SetBranchFilterDraft(reference);
    }

    private void RenameNavigationBranchFilter(string oldReference, string newReference)
    {
        if (_navigationRevisionFilter != oldReference) { return; }
        _navigationRevisionFilter = newReference; _acceptedBranchFilter = newReference;
        _historyQuery = _historyQuery with { BranchFilter = newReference, Revision = null };
        if (_historyRevision.Text == oldReference) { SetBranchFilterDraft(newReference); }
    }

    private async Task ApplyBranchFilterAsync(GitLogQuery? advanced = null, string? rememberedFilter = null, bool branchFilterEnabled = false)
    {
        if (_repository is null || _busy) { return; }
        GitRepository captured = _repository;
        BranchHistoryFilterComboBox? editing = _historyRevision.IsKeyboardFocusWithin ? _historyRevision
            : _branchFilterOverflow?.IsKeyboardFocusWithin == true ? _branchFilterOverflow : null;
        bool current = advanced is { All: false, Revision: "HEAD" };
        string? text = advanced is null ? _historyRevision.Text : rememberedFilter ?? advanced.BranchFilter;
        if (advanced is null && text == NoBranchResults) { text = ""; }
        _historyRevision.IsDropDownOpen = false;
        if (_branchFilterOverflow is not null) { _branchFilterOverflow.IsDropDownOpen = false; }
        _browserToolbar?.OverflowFlyout.Hide();
        await RunAsync(T("Filter revisions"), async repository =>
        {
            CancellationToken operationToken = Token;
            // FormRevisionFilter OK deliberately does not validate references.
            // Warnings/omission belong only to Enter in the toolbar combo.
            GitBranchFilterValidation validation = advanced is null
                ? await repository.ValidateBranchFilterAsync(text, operationToken)
                : new GitBranchFilterValidation(text?.Trim() ?? "", [], [], []);
            operationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_repository, captured) || IsBranchFilterOwnerClosing) { return; }
            foreach (GitBranchFilterInvalidToken invalid in validation.InvalidTokens)
            {
                await ShowInvalidBranchFilterAsync(invalid, operationToken);
                operationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_repository, captured) || IsBranchFilterOwnerClosing) { return; }
            }
            operationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_repository, captured) || IsBranchFilterOwnerClosing) { return; }
            _navigationRevisionFilter = null;
            if (!current || rememberedFilter is not null) { _acceptedBranchFilter = validation.Filter; }
            _historyBranchScope = current ? HistoryBranchScope.Current
                : advanced is not null ? branchFilterEnabled ? HistoryBranchScope.Filtered : HistoryBranchScope.All
                : string.IsNullOrWhiteSpace(validation.Filter) ? HistoryBranchScope.All : HistoryBranchScope.Filtered;
            _historyQuery = (advanced ?? _historyQuery) with
            { BranchFilter = current ? "HEAD" : _historyBranchScope == HistoryBranchScope.Filtered ? validation.Filter : null,
                Revision = null, All = !current };
            if (advanced is null || _historyBranchScope == HistoryBranchScope.Filtered) { SetBranchFilterDraft(validation.Filter); }
            UpdateHistoryScopePresentation();
            _historyWindow = _historyQuery.Count;
            await RefreshDataAsync(repository);
        }, refresh: false);
        if (!IsBranchFilterOwnerClosing && editing is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }) { editing.FocusEditor(); }
    }

    private async Task ShowInvalidBranchFilterAsync(GitBranchFilterInvalidToken invalid, CancellationToken cancellationToken)
    {
        string caption = _translations.Translate("TranslatedStrings", "_nonexistingGitRevision", "Text", "Git revision does not exist");
        string heading = string.Format(_translations.CurrentCulture,
            _translations.Translate("TranslatedStrings", "_ignoringReference", "Text", "\"{0}\" is not a Git revision and will be ignored."), invalid.Token);
        Button ok = new() { Name = "BranchFilterWarningOK", Content = T("OK"), MinWidth = 75, Height = 25, IsDefault = true, IsCancel = true };
        Window warning = new() { Name = "BranchFilterInvalidReference", Title = caption, Width = 440,
            SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = ActualThemeVariant };
        Grid body = new() { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(12), ColumnSpacing = 12 };
        body.Children.Add(OriginalIconImage("Warning.png"));
        TextBlock message = new() { Name = "BranchFilterWarningMessage", Text = heading, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(message, 1); body.Children.Add(message);
        warning.Content = new StackPanel { Children = { body, new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12), Children = { ok } } } };
        ok.Click += (_, _) => warning.Close();
        using CancellationTokenRegistration registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => warning.Close()));
        await warning.ShowDialog(this);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private MenuItem[] HistoryScopeChoices(string suffix)
    {
        return Enum.GetValues<HistoryBranchScope>().Select(scope =>
        {
            string label = scope switch { HistoryBranchScope.All => "All branches", HistoryBranchScope.Current => "Current branch", _ => "Filtered branches" };
            MenuItem item = MenuAction(label, () => SetHistoryBranchScopeAsync(scope));
            item.Name = "HistoryScope" + scope + suffix; item.Tag = scope;
            item.ToggleType = MenuItemToggleType.Radio; item.GroupName = "HistoryScope" + suffix;
            item.IsChecked = scope == _historyBranchScope; _historyScopeChoices.Add(item); return item;
        }).ToArray();
    }

    private Task SetHistoryBranchScopeAsync(HistoryBranchScope scope)
    {
        _navigationRevisionFilter = null; _historyBranchScope = scope;
        _historyQuery = _historyQuery with { Revision = null,
            BranchFilter = scope == HistoryBranchScope.Current ? "HEAD" : scope == HistoryBranchScope.Filtered ? _acceptedBranchFilter ?? "" : null,
            All = scope != HistoryBranchScope.Current };
        if (scope == HistoryBranchScope.Filtered) { SetBranchFilterDraft(_acceptedBranchFilter); }
        UpdateHistoryScopePresentation();
        return ReloadHistoryAsync();
    }

    private Button BranchSuggestionButton()
    {
        Button button = new() { Name = "BranchHistoryFilterType", Width = 29, Height = 23, MinHeight = 23, MinWidth = 0, Padding = new Thickness(2, 0) };
        Grid content = new() { ColumnDefinitions = new("Auto,*") };
        content.Children.Add(OriginalIconImage("EditFilter.png"));
        PathIcon arrow = ToolbarDropdownArrow(button, "BranchHistoryFilterTypeArrow"); Grid.SetColumn(arrow, 1); content.Children.Add(arrow);
        button.Content = content; ToolTip.SetTip(button, T("Branch type"));
        button.Flyout = new MenuFlyout { ItemsSource = BranchSuggestionChoices("") };
        ConfigureBrowserFlyout((MenuFlyout)button.Flyout, button); _actionButtons.Add(button); return button;
    }

    private MenuItem[] BranchSuggestionChoices(string suffix)
    {
        return new[] { "Local", "Remote", "Tag" }.Select(kind =>
        {
            MenuItem item = new() { Name = "BranchSuggestion" + kind + suffix,
                Header = _translations.TranslateAccessKeys("FilterToolBar", "tsmiBranch" + kind, "Text", "&" + kind),
                ToggleType = MenuItemToggleType.CheckBox, StaysOpenOnClick = true, Tag = kind,
                IsChecked = kind switch { "Local" => _branchLocalSuggestions, "Remote" => _branchRemoteSuggestions, _ => _branchTagSuggestions } };
            item.Click += (_, _) =>
            {
                if (kind == "Local") { _branchLocalSuggestions = item.IsChecked; }
                else if (kind == "Remote") { _branchRemoteSuggestions = item.IsChecked; }
                else { _branchTagSuggestions = item.IsChecked; }
                foreach (MenuItem choice in _branchSuggestionChoices)
                { choice.IsChecked = (choice.Tag as string) switch { "Local" => _branchLocalSuggestions, "Remote" => _branchRemoteSuggestions, _ => _branchTagSuggestions }; }
                _historyRevision.InvalidateSuggestions(close: true); _branchFilterOverflow?.InvalidateSuggestions(close: true);
            };
            _branchSuggestionChoices.Add(item); return item;
        }).ToArray();
    }

    private MenuItem BranchFilterOverflow()
    {
        _branchFilterOverflow = new BranchHistoryFilterComboBox();
        ConfigureBranchFilter(_branchFilterOverflow, "BranchHistoryFilterOverflow");
        MenuItem field = new() { Header = ToolbarField(T("Branches:"), _branchFilterOverflow), StaysOpenOnClick = true };
        field.AddHandler(MenuItem.PointerEnteredItemEvent, (_, args) =>
        {
            // Mapping the suggestions can re-enter the underlying menu on X11.
            // Its hover selection must not take focus from the embedded editor
            // while the pointer is still within that editable combo.
            if (_branchFilterOverflow.IsPointerOver && _branchFilterOverflow.IsKeyboardFocusWithin) { args.Handled = true; }
        });
        _actionMenus.Add(field); return field;
    }

    private MenuItem BranchSuggestionOverflow()
    {
        MenuItem type = new() { Name = "BranchHistoryFilterTypeOverflow", Header = T("Branch type"),
            Icon = OriginalIconImage("EditFilter.png"), ItemsSource = BranchSuggestionChoices("Overflow") };
        _actionMenus.Add(type); return type;
    }

    private void FocusHistoryFilter()
    {
        Control field = _quickHistoryFilter.IsFocused || _quickFilterOverflowInput?.IsFocused == true ? _historyRevision : _quickHistoryFilter;
        string name = ReferenceEquals(field, _historyRevision) ? "HistoryToolbarBranches" : "HistoryToolbarQuickFilter";
        if (!_historyView.ShowFilterToolbar || _historyView.HiddenToolbarItems.Contains(name))
        {
            _historyView = _historyView with { ShowFilterToolbar = true, HiddenToolbarItems = _historyView.HiddenToolbarItems.Where(key => key != name).ToArray() };
            ApplyHistoryToolbarVisibility(); RefreshHistoryMenuStates(); UpdateLayout();
        }
        Control revealed = _browserToolbar?.Reveal(field) ?? field;
        if (revealed is BranchHistoryFilterComboBox combo) { combo.FocusEditor(selectAll: true); }
        else if (revealed is TextBox editor) { editor.Focus(); editor.SelectAll(); }
        Dispatcher.UIThread.Post(() =>
        {
            if (revealed is BranchHistoryFilterComboBox delayed) { delayed.FocusEditor(selectAll: true); }
            else if (revealed is TextBox editor) { editor.Focus(); editor.SelectAll(); }
        });
    }

    private void RelayBranchFilterFocusShortcut(object? sender, Avalonia.Input.KeyEventArgs args)
    {
        // PopupRoot has its own input route; the main window's tunnel handler
        // cannot receive the configured focus command from either overflow editor.
        if (_busy || args.Handled || DesktopHotkeys.Match("Browse", args)?.Name != "FocusFilter") { return; }
        args.Handled = true; FocusHistoryFilter();
    }

    private void UpdateHistoryScopePresentation()
    {
        string scope = T(_historyBranchScope switch
        { HistoryBranchScope.Current => "Current branch", HistoryBranchScope.Filtered => "Filtered branches", _ => "All branches" });
        if (_scopeButton is not null) { _scopeButton.Content = IconLabel(scope, "BranchLocal.png"); }
        if (_scopeOverflow is not null) { _scopeOverflow.Header = scope; }
        foreach (MenuItem choice in _historyScopeChoices) { choice.IsChecked = choice.Tag is HistoryBranchScope value && value == _historyBranchScope; }
    }
}

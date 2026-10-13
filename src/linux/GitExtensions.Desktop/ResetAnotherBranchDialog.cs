using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

/// <summary>Move a non-current local branch, then optionally enter the normal checkout workflow.</summary>
public sealed class ResetAnotherBranchDialog : RepositoryDialog
{
    private readonly string _initialRevision;
    private readonly ResetBranchPreferencesStore _preferences;
    private readonly Func<Window, CancellationToken, Task>? _afterSubmoduleUpdate;
    private readonly CancellationTokenSource _lifetime;
    private readonly AutoCompleteBox _branches = new() { Name = "ResetOtherBranchSelection", Height = 24, MinHeight = 24, MinimumPrefixLength = 0,
        FilterMode = AutoCompleteFilterMode.ContainsOrdinal, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _checkout = new() { Name = "ResetOtherBranchCheckout" };
    private readonly CheckBox _force = new() { Name = "ResetOtherBranchForce" };
    private readonly TextBlock _summary = ResetDialogControls.Summary("ResetOtherRevisionSummary", 150);
    private readonly StackPanel _body;
    private readonly Button _reset;
    private GitResetOtherBranchPreview? _preview;
    private Popup? _branchPopup;
    private bool _initializing = true, _postProcessing;
    private int _suggestionRequest;
    public bool Applied { get; private set; }
    public string? ResetBranch { get; private set; }
    public bool CheckoutRequested { get; private set; }
    public bool CheckoutSucceeded { get; private set; }
    public bool SubmodulesUpdated { get; private set; }

    public ResetAnotherBranchDialog(GitRepository repository, string revision, ResetBranchPreferencesStore? preferences = null,
        Func<Window, CancellationToken, Task>? afterSubmoduleUpdate = null, CancellationToken cancellationToken = default) : base(repository, "Reset another branch")
    {
        _initialRevision = revision; _preferences = preferences ?? new(); _afterSubmoduleUpdate = afterSubmoduleUpdate;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Name = "ResetAnotherBranchDialog"; Title = Text("$this", "Reset branch"); Width = 550; MinWidth = 535; CanResize = false; SizeToContent = SizeToContent.Height;
        _checkout.Content = Caption("cbxCheckoutBranch", "Chec&kout branch after reset"); _checkout.IsChecked = _preferences.Load().CheckoutOtherBranchAfterReset;
        _force.Content = Caption("ForceReset", "&Force reset for a non-fast-forward reset");
        ReferenceDialogControls.Compact(_checkout, _force);
        _reset = Action("ExecuteResetOtherBranch", "OK", ExecuteAsync); _reset.Content = Caption("Ok", "&OK"); _reset.IsDefault = true; _reset.MinWidth = 91; _reset.Height = 25;
        Button cancel = new() { Name = "CancelResetOtherBranch", Content = Caption("Cancel", "&Cancel"), IsCancel = true, MinWidth = 91, Height = 25 };
        cancel.Click += (_, _) => Close(false);
        StackPanel buttons = ExportDialogControls.Buttons(_reset, cancel); DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons);
        Grid warning = new() { ColumnDefinitions = new("22,*") };
        Image icon = ResetDialogControls.Icon("Warning"); warning.Children.Add(icon);
        TextBlock warningText = new() { Text = Text("lblResetBranchWarning", "You can only reset a branch safely if there is a direct path from it to selected revision.\nForcing a branch to reset if it has not been merged might leave some commits unreachable."), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(warningText, 1); warning.Children.Add(warningText);
        _body = new() { Margin = new Thickness(8), Spacing = 6, Children = { warning,
            new Label { Content = Caption("BranchInfo", "Reset local &branch:"), Target = _branches, Padding = default }, _branches, _summary, _checkout, _force } };
        Root.Children.Add(_body); Track(_branches, _checkout, _force);
        _branches.PropertyChanged += (_, args) =>
        {
            if (args.Property == AutoCompleteBox.TextProperty || args.Property == AutoCompleteBox.SelectedItemProperty)
            { _suggestionRequest++; UpdateActions(); }
        };
        _branches.AddHandler(KeyDownEvent, (_, _) => _suggestionRequest++, RoutingStrategies.Tunnel);
        _branches.TemplateApplied += (_, args) => _branchPopup = args.NameScope.Find<Popup>("PART_Popup");
        _branches.DropDownOpening += (_, args) =>
        {
            // AutoCompleteBox posts TextBox changes as user-population requests.
            // A delayed request must not reopen a popup after focus has left,
            // while an operation is running, or after this window has closed.
            if (!IsVisible || IsBusy || _postProcessing || Applied || !_branches.IsEnabled
                || !_branches.IsKeyboardFocusWithin || _lifetime.IsCancellationRequested)
            {
                args.Cancel = true;
                // Avalonia's cancelled OpeningDropDown resets the property using
                // its ignore flag, without closing an already mapped PART_Popup.
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible || IsBusy || _postProcessing || Applied || !_branches.IsEnabled
                        || !_branches.IsKeyboardFocusWithin || _lifetime.IsCancellationRequested)
                    { CloseSuggestions(); }
                });
            }
        };
        _branches.GotFocus += (_, _) => UpdateActions();
        _branches.LostFocus += (_, _) => { if (!_branches.IsKeyboardFocusWithin) { CloseSuggestions(); } UpdateActions(); };
        _force.IsCheckedChanged += (_, _) => UpdateActions();
        AddHandler(GotFocusEvent, (_, _) => { if (!_branches.IsKeyboardFocusWithin) { CloseSuggestions(); } }, RoutingStrategies.Tunnel);
        _checkout.IsCheckedChanged += (_, _) =>
        {
            if (_initializing) { return; }
            try { _preferences.Save(new(_checkout.IsChecked == true)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Status.Text = error.Message; }
        };
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
        Opened += (_, _) => { if (!IsBusy) { _branches.Focus(); OpenEmptySuggestions(); } };
        UpdateActions();
    }
    private string Text(string item, string source) => Translations.Translate("FormResetAnotherBranch", item, "Text", source);
    private AccessText Caption(string item, string source) => new() { Text = Translations.TranslateAccessKeys("FormResetAnotherBranch", item, "Text", source) };
    private GitResetBranchCandidate? Selected => _preview?.Branches.FirstOrDefault(branch => branch.Reference.ShortName == (_branches.Text ?? ""));
    public override async Task InitializeAsync()
    {
        await RunAsync("Reset another branch", async token =>
        {
            _preview = await Repository.GetResetOtherBranchPreviewAsync(_initialRevision, token);
            ResetDialogControls.SetSummary(_summary, _preview.Commit);
            _branches.ItemsSource = _preview.Branches.Select(branch => branch.Reference.ShortName).ToArray();
            _branches.Text = _preview.SuggestedBranch ?? ""; Status.Text = "";
        }, cancellationToken: _lifetime.Token);
        _initializing = false; UpdateActions();
        if (IsVisible && !IsBusy) { _branches.Focus(); OpenEmptySuggestions(); }
    }
    private void OpenEmptySuggestions()
    {
        if (string.IsNullOrEmpty(_branches.Text) && _preview?.Branches.Count > 0)
        {
            int request = ++_suggestionRequest;
            Dispatcher.UIThread.Post(() =>
            {
                if (request == _suggestionRequest && IsVisible && !IsBusy && !_postProcessing && !Applied
                    && _branches.IsEnabled && _branches.IsKeyboardFocusWithin && !_lifetime.IsCancellationRequested
                    && string.IsNullOrEmpty(_branches.Text) && _branches.SelectedItem is null && _preview?.Branches.Count > 0)
                { _branches.IsDropDownOpen = true; }
            }, DispatcherPriority.Input);
        }
    }
    protected override void OnClosing(WindowClosingEventArgs args)
    {
        CloseSuggestions();
        base.OnClosing(args);
    }
    private void CloseSuggestions()
    {
        _suggestionRequest++;
        _branches.IsDropDownOpen = false;
        if (_branchPopup is { IsOpen: true }) { _branchPopup.IsOpen = false; }
    }
    private async Task ExecuteAsync()
    {
        GitResetBranchCandidate? selected = Selected;
        if (selected is null || _preview is null || IsBusy || Applied || _postProcessing || _lifetime.IsCancellationRequested) { return; }
        bool checkout = _checkout.IsChecked == true; CheckoutRequested = checkout;
        GitResetOtherBranchOptions options = new(selected.Reference.ShortName, _preview.Commit.Hash, _force.IsChecked == true);
        _postProcessing = true; UpdateActions();
        try
        {
            await RunAsync("Reset another branch", async token =>
            {
                try { await Repository.ResetOtherBranchAsync(options, token); Applied = true; ResetBranch = options.Branch; }
                finally { MarkRepositoryChanged(); }
            }, cancellationToken: _lifetime.Token);
            if (!Applied || !IsVisible || _lifetime.IsCancellationRequested) { return; }
            if (checkout)
            {
                GitReference current = (await Repository.GetReferencesAsync(_lifetime.Token)).Single(reference => reference.Name == selected.Reference.Name);
                if (!IsVisible || _lifetime.IsCancellationRequested) { return; }
                CheckoutBranchDialog dialog = new(Repository, current, afterSubmoduleUpdateAsync: async token =>
                { SubmodulesUpdated = true; if (_afterSubmoduleUpdate is not null) { await _afterSubmoduleUpdate(this, token); } })
                { RequestedThemeVariant = ActualThemeVariant };
                Task? initialization = null; dialog.Opened += (_, _) => initialization = dialog.InitializeAsync(_lifetime.Token);
                using CancellationTokenRegistration cancellation = _lifetime.Token.Register(() => Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) { dialog.Close(); } }));
                await dialog.ShowDialog(this); if (initialization is not null) { await initialization; }
                CheckoutSucceeded = dialog.LastResult?.Result.Success == true;
                if (dialog.HasRepositoryChanges) { MarkRepositoryChanged(); }
            }
            if (IsVisible && !_lifetime.IsCancellationRequested) { Close(true); }
        }
        finally { _postProcessing = false; UpdateActions(); }
    }
    protected override void UpdateActions()
    {
        if (_reset is null || _body is null) { return; }
        bool available = !IsBusy && !Applied && !_postProcessing && !_lifetime.IsCancellationRequested;
        _body.IsEnabled = available;
        GitResetBranchCandidate? selected = Selected;
        _reset.IsEnabled = available && selected is not null && (selected.IsFastForward || _force.IsChecked == true);
        bool invalid = !string.IsNullOrEmpty(_branches.Text) && selected is null && !_branches.IsKeyboardFocusWithin;
        _branches.Background = invalid ? new SolidColorBrush(ClassicTextContrast.AdaptForeground(this, Colors.LightCoral)) :
            this.TryFindResource("ClassicWindowBrush", ActualThemeVariant, out object? brush) ? brush as IBrush : null;
        if (selected is not null && !selected.IsFastForward && _force.IsChecked != true)
        { _reset.Background = new SolidColorBrush(Colors.LightCoral); }
        else { _reset.ClearValue(TemplatedControl.BackgroundProperty); }
    }
}

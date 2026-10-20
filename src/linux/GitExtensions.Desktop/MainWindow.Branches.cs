using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly MenuFlyout _branchFlyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private readonly ObservableCollection<Control> _branchItems = [];
    private readonly ObservableCollection<Control> _branchOverflowItems = [];
    private MenuItem? _branchOverflow;
    private CancellationTokenSource? _branchMenuRead;

    private Button BuildCurrentBranchButton()
    {
        // FormBrowse.branchSelect opens the same list from its label and arrow.
        Button button = new() { Name = "BrowseCurrentBranch", Width = 132, Height = DesktopFontMetrics.ToolbarHeight, MinHeight = DesktopFontMetrics.ToolbarHeight,
            Padding = new Thickness(2, 0), Content = IconLabel("Branch", "BranchLocal.png"), Flyout = _branchFlyout };
        _branchFlyout.ItemsSource = _branchItems;
        ConfigureBrowserFlyout(_branchFlyout, button);
        _branchFlyout.Opened += async (_, _) => await LoadBranchMenuAsync(_branchItems, "BranchMenu");
        _branchFlyout.Closed += (_, _) => CancelBranchMenuRead();
        button.PointerReleased += async (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right) { return; }
            e.Handled = true;
            await OpenBranchCheckoutAsync();
        };
        _branchOverflow = new MenuItem { Name = "BranchOverflow", Header = T("Branch"), Icon = OriginalIconImage("BranchLocal.png"), ItemsSource = _branchOverflowItems };
        _branchOverflow.PropertyChanged += async (_, e) =>
        {
            if (e.Property == MenuItem.IsSubMenuOpenProperty)
            {
                if (_branchOverflow.IsSubMenuOpen) { await LoadBranchMenuAsync(_branchOverflowItems, "BranchOverflow"); }
                else { CancelBranchMenuRead(); }
            }
        };
        _actionButtons.Add(button);
        ResetBranchMenu(_branchItems, "BranchMenu"); ResetBranchMenu(_branchOverflowItems, "BranchOverflow");
        return button;
    }

    private void CancelBranchMenuRead()
    {
        CancellationTokenSource? pending = _branchMenuRead;
        _branchMenuRead = null; pending?.Cancel();
    }

    private void ResetBranchMenu(ObservableCollection<Control> items, string prefix)
    {
        items.Clear();
        MenuItem checkout = new() { Name = prefix + "Checkout", Header = T("Checkout branch…"),
            Icon = OriginalIconImage("BranchCheckout.png"),
            IsEnabled = !_busy && _repository is { IsBare: false } };
        DesktopHotkeys.Bind(checkout, "Browse", "CheckoutBranch");
        checkout.AddHandler(KeyDownEvent, HandleShortcuts, RoutingStrategies.Tunnel);
        checkout.Click += async (_, _) => await OpenBranchCheckoutAsync();
        items.Add(checkout); items.Add(new Separator());
    }

    private async Task LoadBranchMenuAsync(ObservableCollection<Control> items, string prefix)
    {
        CancelBranchMenuRead(); ResetBranchMenu(items, prefix);
        if (_busy || _repository is not GitRepository repository) { return; }
        using CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _branchMenuRead = read;
        MenuItem loading = new() { Name = prefix + "Loading", Header = T("Loading…"), IsEnabled = false }; items.Add(loading);
        try
        {
            IReadOnlyList<GitReference> references = await repository.GetReferencesAsync(read.Token);
            if (read.IsCancellationRequested || _repository != repository) { return; }
            items.Remove(loading);
            HashSet<string> visible = _history.Select(commit => commit.Hash).ToHashSet(StringComparer.Ordinal);
            // Windows limits this shortcut list to the first 100 local heads.
            foreach (GitReference branch in references.Where(reference => reference.IsBranch).Take(100))
            {
                bool inGraph = visible.Contains(branch.Hash);
                MenuItem item = new() { Name = prefix + "Local", Header = new TextBlock { Text = branch.ShortName }, Tag = branch,
                    Icon = OriginalIconImage(inGraph ? "BranchLocal.png" : "EyeClosed.png"), IsEnabled = !repository.IsBare };
                if (!inGraph) { item.Foreground = Brush.Parse(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? ClassicStyles.OriginalColors(true)["GrayText"] : "#c0c0c0"); }
                item.AddHandler(KeyDownEvent, HandleShortcuts, RoutingStrategies.Tunnel);
                item.Click += async (_, _) => await CheckoutSelectedBranchAsync(repository, branch);
                items.Add(item);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!read.IsCancellationRequested && _repository == repository)
            { items.Remove(loading); items.Add(new MenuItem { Header = exception.Message, IsEnabled = false }); AppendOutput(exception.Message); }
        }
        finally { if (_branchMenuRead == read) { _branchMenuRead = null; } }
    }

    private void RefreshBranchMenu()
    {
        bool available = !_busy && _repository is not null;
        if (_currentBranchButton is not null) { _currentBranchButton.IsEnabled = available; }
        if (_branchOverflow is not null) { _branchOverflow.IsEnabled = available; }
        foreach (MenuItem item in _branchItems.Concat(_branchOverflowItems).OfType<MenuItem>())
        { item.IsEnabled = available && _repository?.IsBare == false && (item.Tag is GitReference || item.InputGesture is not null); }
        if (!available) { _branchFlyout.Hide(); CancelBranchMenuRead(); }
    }

    private Task OpenBranchCheckoutAsync()
    {
        _branchFlyout.Hide(); _browserToolbar?.OverflowFlyout.Hide();
        return _repository is { IsBare: false } ? CheckoutAsync() : Task.CompletedTask;
    }

    private async Task CheckoutSelectedBranchAsync(GitRepository repository, GitReference branch, bool fromLeftPanel = false)
    {
        _branchFlyout.Hide(); _browserToolbar?.OverflowFlyout.Hide();
        if (_busy || _repository != repository || repository.IsBare) { return; }
        CheckoutBranchDialog? dialog = null;
        dialog = new(repository, branch, _preferences.AutoNormaliseBranchName, _preferences.AutoNormaliseSymbol,
            token => AfterSubmoduleUpdateAsync(repository, dialog!.IsVisible ? dialog : WorkflowOwner, token));
        try
        {
            await RunAsync("Checkout branch", async _ =>
            {
                if (fromLeftPanel && new ConfirmationsSettingsSession().Initial.ConfirmBranchCheckout)
                {
                    bool? accepted = await Dialogs.ConfirmChoiceAsync(WorkflowOwner, T("Confirm checkout"),
                        string.Format(T("Are you sure you want to check out branch \"{0}\"?"), branch.ShortName),
                        T("Yes"), T("No"), acceptIsDefault: true, cancellationToken: Token);
                    if (accepted != true || Token.IsCancellationRequested || _repository != repository) { return; }
                }
                await dialog.InitializeAsync(Token);
                if (Token.IsCancellationRequested) { return; }
                if (dialog.CanExecuteDefault)
                {
                    await dialog.ExecuteDefaultAsync(WorkflowOwner, Token);
                    if (dialog.LastError is null || Token.IsCancellationRequested) { return; }
                }
                // A dirty tree or a failed quick checkout keeps the normal,
                // preselected workflow, including its errors and saved stash.
                await dialog.ShowDialog(WorkflowOwner);
            }, refresh: false);
            if (dialog.HasRepositoryChanges && _repository == repository && !_lifetime.IsCancellationRequested) { await RefreshAsync(); }
        }
        finally { if (dialog.MayHaveRepositoryChanges) { AcceptStandaloneRepositoryMutationAttempt(repository); } if (!dialog.IsVisible) { dialog.Close(); } }
    }
}

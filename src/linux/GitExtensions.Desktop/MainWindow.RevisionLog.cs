using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    internal MainWindowKind WindowKind { get; }
    private bool IsRevisionLog => WindowKind == MainWindowKind.RevisionLog;

    private Control BuildRevisionLogLayout()
    {
        // Construct these controls here once; every reader, selection, menu and lifetime stays in Main.
        DockPanel table = (DockPanel)BuildRevisionTable();
        // Keep the shared bounded history policy usable when the compact host has no Browse toolbar.
        _historyMore = ToolbarButton("RevisionLogLoadMore", "Load more", "ArrowDown.png", LoadMoreHistoryAsync);
        _historyMore.IsVisible = false; _historyMore.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(_historyMore, Dock.Bottom); table.Children.Insert(table.Children.Count - 1, _historyMore);
        _revisionTable = WithQuickSearchNotice(table);
        Grid filesAndDiff = (Grid)BuildCommitFilesView();
        filesAndDiff.ColumnDefinitions[0].Width = new GridLength(188);
        UseStandaloneDiffTab(filesAndDiff);
        Grid body = new() { Name = "RevisionLogLayout", RowDefinitions = new RowDefinitions("205,4,*") };
        body.Children.Add(_revisionTable);
        GridSplitter split = new() { Name = "RevisionLogDetailsSplitter", Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(split, 1); body.Children.Add(split); Grid.SetRow(_tabs, 2); body.Children.Add(_tabs);
        InitializeContinuousScroll();
        Opened += (_, _) =>
        {
            ModalWindowActivation.AfterMapping(this, () => !_lifetime.IsCancellationRequested);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible && IsEffectivelyEnabled && !_lifetime.IsCancellationRequested
                    && !OwnedWindows.Any(window => window.IsVisible)) { _commits.Focus(); }
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        };
        return WithStandaloneDiffStatus(body);
    }

    private void UseStandaloneDiffTab(Control filesAndDiff)
    {
        _tabs.Name = "DetailsTabs";
        _browserDetailTabs = [new TabItem { Name = "DiffTab", Header = T("Diff"), Content = filesAndDiff }];
        _tabs.ItemsSource = _browserDetailTabs; _tabs.SelectedIndex = 0;
        // Existing Main readers use DiffTab identity. The Log projects only its content, without a tab header.
        _tabs.Template = new FuncControlTemplate<TabControl>((control, scope) =>
        {
            // TabControl resolves SelectedContent through its generated container, even without a visible strip.
            ItemsPresenter headers = new()
            {
                Name = "PART_ItemsPresenter", IsVisible = false, IsHitTestVisible = false, Focusable = false,
                ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel())
            };
            ContentPresenter content = new()
            {
                Name = "PART_SelectedContentHost",
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                [!ContentPresenter.ContentProperty] = new Binding(nameof(TabControl.SelectedContent)) { Source = control },
                [!ContentPresenter.ContentTemplateProperty] = new Binding(nameof(TabControl.SelectedContentTemplate)) { Source = control }
            };
            // OnApplyTemplate must find the presenter and eagerly prepare its nonvirtual containers.
            scope.Register(headers.Name!, headers); scope.Register(content.Name!, content);
            return new Grid { Children = { headers, content } };
        });
    }

    private Control WithStandaloneDiffStatus(Control body)
    {
        DockPanel root = new();
        Border error = ClassicChrome.Border(_state, new Thickness(0, 1, 0, 0), new Thickness(6, 3));
        DockPanel.SetDock(error, Dock.Bottom); error.IsVisible = false;
        _state.PropertyChanged += (_, args) => { if (args.Property == TextBlock.TextProperty) { error.IsVisible = LastException is not null; } };
        root.Children.Add(error); root.Children.Add(body);
        return root;
    }

    internal async Task ShowRevisionLogAsync()
    {
        if (_repository is not { } repository || _busy || _lifetime.IsCancellationRequested) { return; }
        // Like explicit F5, a read-only Log can return to a caret which Ctrl+click
        // just deselected. Keep every normal focus/repository/owner guard below.
        OperationFocus? historyFocus = _commits.IsKeyboardFocusWithin ? CaptureOperationFocus() : null;
        if (historyFocus is not null) { historyFocus = historyFocus with { PreserveUnselectedRow = true }; }
        bool mayHaveChanges = false;
        Task publication = Task.CompletedTask;
        await RunAsync("Diff", async _ =>
        {
            RepositoryChangeContext? notifications = RepositoryChangesFor(repository);
            MainWindow log = RevisionLogWindow.Create(new(repository.Path), ModelessApplicationLifetime);
            log._repositoryChanges = notifications;
            Window owner = WorkflowOwner; log.RequestedThemeVariant = owner.ActualThemeVariant;
            CancellationToken command = Token;
            using CancellationTokenRegistration registration = command.Register(() =>
            {
                // Closing already runs on the UI thread: seal a busy descendant's
                // operation token before its continuation can overtake a queued close.
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) { log.Close(); }
                else { Avalonia.Threading.Dispatcher.UIThread.Post(log.Close); }
            });
            // This Main already owns the modal in its WindowWork lease. Let its
            // real Closing seal Token before a busy descendant can veto the cascade.
            bool leaseOwnsOwner = ReferenceEquals(owner, this);
            WindowClosingBehavior previousClosingBehavior = owner.ClosingBehavior;
            if (leaseOwnsOwner) { owner.ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly; }
            notifications?.Lock();
            try { command.ThrowIfCancellationRequested(); await log.ShowDialog(owner); command.ThrowIfCancellationRequested(); }
            finally
            {
                try
                {
                    log.Close(); await log.WindowWorkCompletion;
                    // This observer only waits Log work and Notify (still locked),
                    // never a Browse refresh or independent child's lifetime.
                    await log._repositoryChangePublication;
                    mayHaveChanges = log.StandaloneMayHaveRepositoryChanges || notifications?.HasPendingNotification == true;
                    if (mayHaveChanges) { AcceptStandaloneRepositoryMutationAttempt(repository); }
                }
                finally
                {
                    // Do not alter another host's policy or replace a policy changed meanwhile.
                    if (leaseOwnsOwner && owner.ClosingBehavior == WindowClosingBehavior.OwnerWindowOnly)
                    { owner.ClosingBehavior = previousClosingBehavior; }
                    notifications?.UnLock();
                    publication = notifications?.PublicationCompletion ?? Task.CompletedTask;
                }
            }
        }, refresh: false);
        // Only the receiving Browse can await its refresh after its own modal
        // RunAsync lease ends. A nested standalone host must not await Browse.
        if (!IsStandaloneDiff) { await publication; }
        // Mutating operations retain the ordinary selected-origin rule. The
        // shared restorer also refuses to steal a newer valid focus or outlive
        // this repository, owner, modal operation or window.
        if (!mayHaveChanges) { RestoreOperationFocus(historyFocus); }
    }
}

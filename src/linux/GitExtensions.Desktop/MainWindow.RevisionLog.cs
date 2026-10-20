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

    internal Task ShowRevisionLogAsync()
    {
        if (_repository is not { } repository || _busy || _lifetime.IsCancellationRequested) { return Task.CompletedTask; }
        return RunAsync("Diff", async _ =>
        {
            MainWindow log = RevisionLogWindow.Create(new(repository.Path));
            Window owner = WorkflowOwner; log.RequestedThemeVariant = owner.ActualThemeVariant;
            CancellationToken command = Token;
            using CancellationTokenRegistration registration = command.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(log.Close));
            try { command.ThrowIfCancellationRequested(); await log.ShowDialog(owner); command.ThrowIfCancellationRequested(); }
            finally { log.Close(); await log.WindowWorkCompletion; }
        }, refresh: true);
    }
}

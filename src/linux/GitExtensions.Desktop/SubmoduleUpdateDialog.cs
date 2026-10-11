using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

public sealed class SubmoduleUpdateDialog : RepositoryDialog
{
    private readonly TextBox _output = new()
    {
        Name = "SubmoduleUpdateOutput", IsReadOnly = true, AcceptsReturn = true,
        FontFamily = new FontFamily("monospace"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10)
    };
    private readonly ProgressBar _progress = new() { Name = "SubmoduleUpdateProgress", IsIndeterminate = true, Height = 8, Margin = new Thickness(10, 10, 10, 0) };
    private bool _cancelled;
    private Task? _update;
    public bool Succeeded { get; private set; }

    public SubmoduleUpdateDialog(GitRepository repository) : base(repository, "Update submodules")
    {
        Width = 620; Height = 290; MinWidth = 460; MinHeight = 220;
        Button close = new() { Name = "CancelSubmoduleUpdate", Content = T("Cancel"), IsCancel = true, Height = 24, MinHeight = 24, MinWidth = 75, Padding = new Thickness(8, 0) };
        close.Click += (_, _) => Close();
        StackPanel buttons = ExportDialogControls.Buttons(close);
        DockPanel.SetDock(buttons, Dock.Bottom); Root.Children.Add(buttons);
        DockPanel.SetDock(_progress, Dock.Top); Root.Children.Add(_progress);
        Root.Children.Add(_output);
        _output.Text = "git submodule update --init --recursive";
    }

    protected override void OnClosing(WindowClosingEventArgs args)
    {
        base.OnClosing(args);
        // Let an ancestor's close reach Checkout, whose cancellation and deferred close
        // cover the whole workflow. Manual cancellation still waits for this operation.
        if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { args.Cancel = false; }
    }

    public override Task InitializeAsync() => RunAsync("Update submodules", async token =>
    {
        try
        {
            GitResult result = await Repository.UpdateSubmodulesAsync(recursive: true, cancellationToken: token);
            _output.Text = result.StandardOutput + result.StandardError;
            Succeeded = true;
            MarkRepositoryChanged();
            Close();
        }
        catch (OperationCanceledException) { _cancelled = true; throw; }
        catch (GitCommandException exception) { _output.Text = exception.Result.StandardOutput + exception.Result.StandardError; MarkRepositoryChanged(); throw; }
        finally { _progress.IsIndeterminate = false; }
    });

    internal static async Task<bool> ShowAsync(Window owner, GitRepository repository, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        SubmoduleUpdateDialog dialog = new(repository);
        dialog.Opened += (_, _) => dialog._update = dialog.InitializeAsync();
        void OwnerClosed(object? sender, EventArgs args) => dialog.Close();
        owner.Closed += OwnerClosed;
        using CancellationTokenRegistration cancellation = token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close()));
        try
        {
            await dialog.ShowDialog(owner);
            if (dialog._update is not null) { await dialog._update; }
            token.ThrowIfCancellationRequested();
            if (dialog._cancelled) { throw new OperationCanceledException("Submodule update cancelled.", token); }
            if (dialog.LastException is Exception exception) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw(); }
            return dialog.Succeeded;
        }
        finally { owner.Closed -= OwnerClosed; }
    }
}

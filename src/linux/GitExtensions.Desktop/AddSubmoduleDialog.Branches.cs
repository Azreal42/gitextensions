using Avalonia.Controls;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop;

public sealed partial class AddSubmoduleDialog
{
    private readonly Dictionary<Task, CancellationTokenSource> _branchRequests = [];
    private readonly List<Exception> _branchFailures = [];
    private long _sourceVersion, _branchVersion;
    private bool _branchClosing, _branchClosePending;
    private Window? _closingOwner;
    public bool IsBranchLoading => _branchRequests.Keys.Any(task => !task.IsCompleted);
    public Exception? BranchLoadError { get; private set; }
    public IReadOnlyList<Exception> BranchLoadFailures => _branchFailures.ToArray();

    private void ConfigureBranchLifetime()
    {
        Closing += (_, args) =>
        {
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingOwner = Owner as Window; }
            _branchClosing = true; CancelBranchRequests();
            if (IsBranchLoading)
            {
                args.Cancel = true;
                if (!_branchClosePending) { _branchClosePending = true; _ = CloseAfterBranchRequestsAsync(); }
            }
        };
        Closed += (_, _) =>
        {
            Window? owner = _closingOwner; _closingOwner = null;
            if (owner is not null) { Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        };
    }

    private async Task LoadBranchesAsync()
    {
        if (!_ready || _bare || IsBusy || _branchClosing || !IsVisible || string.IsNullOrEmpty(_source.Text)) { return; }
        CancelBranchRequests(); long version = ++_branchVersion, sourceVersion = _sourceVersion;
        string source = _source.Text;
        string? oldError = BranchLoadError?.Message; BranchLoadError = null;
        if (Status.Text == oldError) { Status.Text = ""; }
        CancellationTokenSource cancellation = new();
        Task operation = ReadBranchesAsync(source, version, sourceVersion, cancellation.Token);
        _branchRequests.Add(operation, cancellation);
        try { await operation; }
        finally { _branchRequests.Remove(operation); cancellation.Dispose(); }
    }

    private async Task ReadBranchesAsync(string source, long version, long sourceVersion, CancellationToken token)
    {
        try
        {
            IReadOnlyList<string> branches = await Repository.GetSubmoduleRemoteBranchesAsync(source, token);
            token.ThrowIfCancellationRequested();
            if (_branchClosing || !IsVisible || version != _branchVersion || sourceVersion != _sourceVersion || source != _source.Text) { return; }
            string? latest = _branch.Text;
            _branch.ItemsSource = branches; _branch.SelectedItem = branches.FirstOrDefault(branch => branch == latest); _branch.Text = latest;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            _branchFailures.Add(error);
            if (!_branchClosing && IsVisible && version == _branchVersion && sourceVersion == _sourceVersion && source == _source.Text)
            { BranchLoadError = error; Status.Text = error.Message; }
        }
    }

    private void CancelBranchRequests() { foreach (CancellationTokenSource request in _branchRequests.Values.ToArray()) { request.Cancel(); } }
    private Task DrainBranchRequestsAsync() => Task.WhenAll(_branchRequests.Keys.ToArray());
    private async Task CloseAfterBranchRequestsAsync()
    {
        await DrainBranchRequestsAsync();
        if (IsVisible) { Close(); }
    }
}

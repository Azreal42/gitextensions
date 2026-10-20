namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly HashSet<Task> _windowWork = [];

    // Keep cancelled reads alive until Git and the reader have actually unwound.
    // The close event seals new work before it snapshots these leases.
    private WindowWork TrackWindowWork()
    {
        WindowWork work = new(_windowWork);
        _windowWork.Add(work.Completion);
        return work;
    }

    internal Task WindowWorkCompletion => Task.WhenAll(_windowWork.ToArray()
        .Append(_commitInfo.Completion).Append(FileHistoryInlineBlameCompletion).Append(_continuousScroll?.Completion ?? Task.CompletedTask));

    private sealed class WindowWork(HashSet<Task> pending) : IDisposable
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Completion => _completion.Task;
        public void Dispose()
        { pending.Remove(Completion); _completion.TrySetResult(); }
    }
}

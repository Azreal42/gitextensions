using System.Globalization;

namespace GitExtensions.Linux.Plugins;

public sealed record BackgroundFetchOptions(string Arguments = "fetch --all", int FetchEverySeconds = 0,
    bool AutoRefresh = false, bool FetchAllSubmodules = false, bool FetchImmediatelyOnRepositoryOpening = false)
{
    public static BackgroundFetchOptions FromValues(IReadOnlyDictionary<string, string?> values) => new(
        PluginValues.Text(BackgroundFetchService.Descriptor, values, "arguments"),
        ReadInterval(values),
        PluginValues.Boolean(BackgroundFetchService.Descriptor, values, "autoRefresh"),
        PluginValues.Boolean(BackgroundFetchService.Descriptor, values, "submodules"),
        PluginValues.Boolean(BackgroundFetchService.Descriptor, values, "immediate"));

    // One parser for the runtime and the scoped Settings page's explicit edits.
    public string[] GetCommandArguments()
        => GourceProcess.ParseArguments(string.IsNullOrWhiteSpace(Arguments) ? "fetch --all" : Arguments);

    private static int ReadInterval(IReadOnlyDictionary<string, string?> values)
        => int.TryParse(PluginValues.Text(BackgroundFetchService.Descriptor, values, "interval"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int seconds) ? seconds : throw new ArgumentException("Fetch interval must be an integer number of seconds.", "interval");
}

// The host supplies its foreground Git activity and marshals refreshes to its UI
// dispatcher. RunGitAsync is optional: production uses the repository's Git lock.
public sealed record BackgroundFetchContext(GitRepository Repository, Func<bool>? IsGitBusy = null,
    Func<CancellationToken, Task>? RequestRefreshAsync = null,
    Func<GitRepository, IReadOnlyList<string>, CancellationToken, Task<GitResult>>? RunGitAsync = null);

public sealed class BackgroundFetchService(TimeProvider? timeProvider = null) : IAsyncDisposable
{
    public static PluginDescriptor Descriptor { get; } = new(new Guid("D19A7905-8AAD-4271-ACA9-817669B94A1D"),
        "Periodic background fetch",
        "Fetch on repository opening or periodically. Refresh before a force push with lease when automatic refresh is disabled, to avoid losing remote commits.",
        "IconBackgroundFetch",
        [new("arguments", "Arguments of git command to run", PluginParameterKind.Text, "fetch --all"),
         new("interval", "Fetch every (seconds) - set to 0 to disable", PluginParameterKind.Integer, "0"),
         new("autoRefresh", "Refresh view after fetch", PluginParameterKind.Boolean, "false"),
         new("submodules", "Fetch all submodules", PluginParameterKind.Boolean, "false"),
         new("immediate", "Fetch immediately on repository opening", PluginParameterKind.Boolean, "false")]);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private BackgroundFetchContext? _context;
    private CancellationTokenSource? _cancellation;
    private Task _worker = Task.CompletedTask;
    private bool _disposed;
    public Task Completion => _worker;
    public Exception? LastError { get; private set; }
    public GitResult? LastResult { get; private set; }

    // Register at repository opening, and again after settings change. The token
    // belongs to the repository/window lifetime, not to an individual UI command.
    public async Task RegisterAsync(BackgroundFetchContext context, BackgroundFetchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        string[] command = options.GetCommandArguments();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopAsync().ConfigureAwait(false);
            _context = context;
            LastError = null;
            LastResult = null;
            if ((options.FetchEverySeconds <= 0 && !options.FetchImmediatelyOnRepositoryOpening)
                || !await context.Repository.IsRepositoryAsync(cancellationToken).ConfigureAwait(false)) { return; }
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = _cancellation.Token;
            _worker = Task.Run(() => RunLoopAsync(context, options, command, token), CancellationToken.None);
        }
        finally { _lifecycle.Release(); }
    }

    public Task ReconfigureAsync(BackgroundFetchOptions options, CancellationToken cancellationToken = default)
        => RegisterAsync(_context ?? throw new InvalidOperationException("Register a repository before reconfiguring background fetch."), options, cancellationToken);

    public async Task UnregisterAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopAsync().ConfigureAwait(false); _context = null; }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; await StopAsync().ConfigureAwait(false); _context = null; }
        finally { _lifecycle.Release(); }
    }

    private async Task StopAsync()
    {
        if (_cancellation is not null)
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
            await _worker.ConfigureAwait(false);
            _cancellation.Dispose();
            _cancellation = null;
        }
        _worker = Task.CompletedTask;
    }

    private async Task RunLoopAsync(BackgroundFetchContext context, BackgroundFetchOptions options, string[] command, CancellationToken token)
    {
        try
        {
            bool periodic = options.FetchEverySeconds > 0;
            TimeSpan interval = TimeSpan.FromSeconds(Math.Max(5, options.FetchEverySeconds));
            if (periodic)
            {
                await Task.Delay(options.FetchImmediatelyOnRepositoryOpening ? TimeSpan.FromMilliseconds(10) : interval, _clock, token).ConfigureAwait(false);
            }
            do
            {
                while (context.IsGitBusy?.Invoke() == true) { await Task.Delay(TimeSpan.FromSeconds(1), _clock, token).ConfigureAwait(false); }
                token.ThrowIfCancellationRequested();
                try
                {
                    if (options.FetchAllSubmodules)
                    { await FetchSubmodulesAsync(context, context.Repository, new(StringComparer.Ordinal) { context.Repository.Path }, token).ConfigureAwait(false); }
                    GitResult result = await RunGitAsync(context, context.Repository, command, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    LastResult = result;
                    if (result.Success && options.AutoRefresh && context.RequestRefreshAsync is not null
                        && (!command[0].Equals("fetch", StringComparison.OrdinalIgnoreCase) || result.StandardError.Contains("From", StringComparison.Ordinal)))
                    { await context.RequestRefreshAsync(token).ConfigureAwait(false); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) { LastError = exception; /* Background failures do not interrupt browsing. */ }
                if (!periodic) { break; }
                // Do not overlap or accumulate fetches when Git takes longer than
                // the interval. Restart the interval after this operation finishes.
                await Task.Delay(interval, _clock, token).ConfigureAwait(false);
            } while (true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { LastError = exception; }
    }

    private async Task FetchSubmodulesAsync(BackgroundFetchContext context, GitRepository parent, HashSet<string> visited, CancellationToken token)
    {
        IReadOnlyList<GitSubmodule> submodules;
        try { submodules = await parent.GetSubmodulesAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { LastError = exception; return; }
        foreach (GitSubmodule submodule in submodules)
        {
            if (!submodule.IsInitialized) { continue; }
            string path = Path.GetFullPath(Path.Combine(parent.Path, submodule.Path));
            if (!visited.Add(path)) { continue; }
            GitRepository child = new(path);
            try { await RunGitAsync(context, child, ["fetch", "--all"], token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { LastError = exception; }
            await FetchSubmodulesAsync(context, child, visited, token).ConfigureAwait(false);
        }
    }

    private static Task<GitResult> RunGitAsync(BackgroundFetchContext context, GitRepository repository, IReadOnlyList<string> arguments, CancellationToken token)
        => context.RunGitAsync is null ? repository.RunAsync(arguments, token) : context.RunGitAsync(repository, arguments, token);
}

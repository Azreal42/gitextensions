using Avalonia.Controls;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

/// <summary>One visible workflow owner. A close request seals continuations even when its busy window defers actual closure.</summary>
internal sealed class ScriptWorkflowScope : IDisposable
{
    private readonly Window _owner;
    private readonly GitRepository _repository;
    private readonly CancellationTokenSource _cancellation;
    private bool _closing;
    internal ScriptWorkflowScope(Window owner, GitRepository repository, CancellationToken token)
    {
        _owner = owner; _repository = repository;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        owner.Closing += Closing;
    }
    internal CancellationToken Token => _cancellation.Token;
    internal bool MayHaveRunScripts { get; private set; }
    internal void ThrowIfStopped()
    {
        Token.ThrowIfCancellationRequested();
        if (_closing || !_owner.IsVisible) { throw new OperationCanceledException(Token); }
    }
    internal static bool HasConfiguredEvents(params ScriptEvent[] events)
    {
        ScriptsPreferencesLoadResult settings = new ScriptsPreferencesStore().Load();
        if (settings.Error is not null) { throw new InvalidDataException("Unable to read scripts.", settings.Error); }
        return settings.Scripts.Any(script => script.Enabled && events.Contains(script.OnEvent));
    }
    internal static async Task<bool> IsGitOperationPendingAsync(GitRepository repository, CancellationToken token)
    {
        GitRepositoryInfo info = await repository.GetInfoAsync(token);
        token.ThrowIfCancellationRequested();
        return new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" }.Any(name => File.Exists(Path.Combine(info.GitDirectory, name)))
            || new[] { "rebase-merge", "rebase-apply", "sequencer" }.Any(name => Directory.Exists(Path.Combine(info.GitDirectory, name)));
    }
    internal async Task<bool> RunAsync(ScriptEvent scriptEvent)
    {
        ThrowIfStopped();
        // This is deliberately a conservative refresh flag, never evidence of Git or script success.
        ScriptsPreferencesLoadResult settings = new ScriptsPreferencesStore().Load();
        MayHaveRunScripts |= settings.Scripts.Any(script => script.Enabled && script.OnEvent == scriptEvent);
        bool completed = await DesktopScriptExecutor.RunEventAsync(_owner, _repository, scriptEvent, Token);
        ThrowIfStopped();
        return completed;
    }
    private void Closing(object? sender, WindowClosingEventArgs args) { _closing = true; _cancellation.Cancel(); }
    public void Dispose() { _owner.Closing -= Closing; _cancellation.Dispose(); }
}

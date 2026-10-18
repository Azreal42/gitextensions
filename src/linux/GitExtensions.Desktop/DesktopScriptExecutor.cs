using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Input;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

/// <summary>One launch path for explicit commands and workflow events. Background children remain owned until their real wait and pipe drainage.</summary>
internal static class DesktopScriptExecutor
{
    private static readonly ConditionalWeakTable<Window, Lifetime> Lifetimes = new();
    internal static MainWindow? BrowseOwner(Window owner)
    {
        for (Window? window = owner; window is not null; window = window.Owner as Window)
        { if (window is MainWindow browse) { return browse; } }
        return null;
    }

    internal static async Task<bool> RunEventAsync(Window owner, GitRepository repository, ScriptEvent scriptEvent, CancellationToken token = default)
    {
        using IDisposable execution = repository.BeginOperation();
        token.ThrowIfCancellationRequested();
        ScriptsPreferencesLoadResult settings = new ScriptsPreferencesStore().Load();
        if (settings.Error is not null) { throw new InvalidDataException("Unable to read scripts.", settings.Error); }
        ScriptDefinition[] scripts = settings.Scripts.Where(script => script.Enabled && script.OnEvent == scriptEvent).ToArray();
        foreach (ScriptDefinition script in scripts)
        {
            token.ThrowIfCancellationRequested();
            if (!owner.IsVisible || !await ExecuteAsync(owner, repository, script, token)) { return false; }
        }
        return true;
    }

    internal static async Task<bool> ExecuteAsync(Window owner, GitRepository repository, ScriptDefinition script,
        CancellationToken token = default, FileMenuSelection? files = null)
    {
        using IDisposable execution = repository.BeginOperation();
        Dispatcher.UIThread.VerifyAccess(); token.ThrowIfCancellationRequested();
        if (!owner.IsVisible) { return false; }
        Lifetime lifetime = Lifetimes.GetValue(owner, window => new Lifetime(window));
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        CancellationToken cancellation = operation.Token;
        MainWindow? browse = BrowseOwner(owner);
        files ??= lifetime.Files?.Invoke();
        ScriptHostSelection selection = browse?.CaptureScriptSelection(files) ?? new(Files: files);
        if (lifetime.Position is { } position)
        { var reading = position(); selection = selection with { LineNumber = reading.Line, ColumnNumber = reading.Column }; }
        CapturedScriptContext captured = await ScriptHostContext.CaptureAsync(repository, selection, cancellation);
        ScriptPreparationResult prepared = await new ScriptPreparationDispatcher(owner, captured.Context, captured.References).PrepareAsync(script, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (prepared.Expansion.Status != ScriptExpansionStatus.Completed)
        {
            if (prepared.Expansion.Status == ScriptExpansionStatus.Aborted)
            { throw new InvalidOperationException("Unable to prepare script '" + script.GetDisplayName() + "': " + prepared.Expansion.Error + " " + prepared.Expansion.Option + " " + prepared.Expansion.Diagnostic); }
            return false;
        }
        ScriptExecutionPlan plan = prepared.Expansion.ExecutionPlan ?? throw new InvalidOperationException("The script has no argument plan.");
        if (plan.Target == ScriptTargetKind.Plugin)
        { return browse is not null && await browse.ExecuteScriptPluginAsync(repository, plan.Command, owner, cancellation); }
        string executable = plan.Target == ScriptTargetKind.PowerShell ? "pwsh" : plan.Command;
        IReadOnlyList<string> arguments = plan.Target == ScriptTargetKind.PowerShell
            ? new[] { "-NoProfile", "-Command", plan.Command + " " + plan.ArgumentPlan.Preview } : plan.ArgumentPlan.Arguments;
        if (plan.Target == ScriptTargetKind.OpenUrl && arguments.Count != 1)
        { throw new InvalidOperationException("Open URL requires one URL argument. Use double braces for a single substituted value."); }
        if (plan.Target == ScriptTargetKind.NavigateTo)
        {
            if (executable.Length == 0) { return true; }
            ScriptProcessResult navigation = await new ScriptProcessRunner().RunAsync(executable, repository.Path, arguments, cancellation,
                executionEnvironment: captured.Context.ExecutionEnvironment);
            if (browse is null) { return false; }
            string revision = Encoding.UTF8.GetString(navigation.StandardOutputBytes).Split('\n')[0].TrimEnd('\r');
            // The Windows adapter uses stdout even after a nonzero native exit;
            // navigation failure is presented by Browse, not a veto of the script.
            if (revision.Length != 0) { await browse.NavigateScriptOutputAsync(repository, revision, cancellation); }
            cancellation.ThrowIfCancellationRequested(); return true;
        }
        if (plan.RunInBackground)
        {
            // Do not tie a detached command to a short-lived workflow operation token.
            // Its owner lifetime still cancels and drains it when the window closes.
            cancellation.ThrowIfCancellationRequested();
            return await lifetime.StartAsync(executable, repository.Path, arguments, browse, captured.Context.ExecutionEnvironment);
        }
        GitProcessDialogResult outcome = await GitProcessDialog.ShowAsync(owner, repository.Path,
            script.GetDisplayName() + ": " + executable + " " + plan.ArgumentPlan.Preview, true,
            async (processToken, output) => ToGitResult(await new ScriptProcessRunner().RunAsync(executable, repository.Path, arguments,
                processToken, outputReceived: output, executionEnvironment: captured.Context.ExecutionEnvironment)), cancellation, preserveStreamedOutput: true);
        return !outcome.OwnerCloseRequested && !outcome.WorkflowStopRequested && outcome.Success;
    }

    internal static void SetFileProvider(Window owner, Func<FileMenuSelection?> capture)
        => Lifetimes.GetValue(owner, window => new Lifetime(window)).Files = capture;

    internal static void SetReadingPositionProvider(Window owner, Func<(int Line, int Column)> capture)
        => Lifetimes.GetValue(owner, window => new Lifetime(window)).Position = capture;

    internal static void AttachHotkeys(Window owner, GitRepository repository, Func<ScriptDefinition, Task>? execute = null)
    {
        // Bubble preserves each host's own commands and text-editor handling.
        owner.AddHandler(Window.KeyDownEvent, async (_, args) =>
        {
            if (args.Handled || !owner.IsVisible || !owner.IsEffectivelyEnabled
                || owner is RepositoryDialog { IsBusy: true } || owner is CommitDialog { IsBusy: true }) { return; }
            HotkeyCommand? command = DesktopHotkeys.Match("Scripts", args);
            if (command is null) { return; }
            ScriptsPreferencesLoadResult settings = new ScriptsPreferencesStore().Load();
            ScriptDefinition? script = settings.Scripts.FirstOrDefault(script => script.HotkeyCommandIdentifier == command.Code);
            if (script is null) { return; }
            args.Handled = true;
            try
            {
                if (execute is not null) { await execute(script); }
                else { await ExecuteAsync(owner, repository, script); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            { if (owner.IsVisible && owner.IsEffectivelyEnabled) { await Dialogs.ShowErrorAsync(owner, error.Message); } }
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    internal static GitResult ToGitResult(ScriptProcessResult result) => new(result.ExitCode,
        Encoding.UTF8.GetString(result.StandardOutputBytes), Encoding.UTF8.GetString(result.StandardErrorBytes))
        { StandardOutputBytes = result.StandardOutputBytes, StandardErrorBytes = result.StandardErrorBytes };

    private sealed class Lifetime
    {
        private readonly Window _owner;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly HashSet<Task> _pending = [];
        private bool _closing, _ready;
        private Window? _closingOwner;
        internal CancellationToken Token => _lifetime.Token;
        internal Func<FileMenuSelection?>? Files { get; set; }
        internal Func<(int Line, int Column)>? Position { get; set; }
        internal Lifetime(Window owner)
        {
            _owner = owner;
            owner.Closing += (_, args) =>
            {
                if (_ready) { return; }
                _closing = true; _lifetime.Cancel();
                if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingOwner = owner.Owner as Window; }
                if (_pending.Count != 0)
                {
                    args.Cancel = true;
                    _ = CloseAfterDrainAsync();
                }
            };
            owner.Closed += (_, _) =>
            {
                _lifetime.Cancel();
                Window? parent = _closingOwner; _closingOwner = null;
                if (parent is not null) { Dispatcher.UIThread.Post(() => { if (parent.IsVisible) { parent.Close(); } }); }
            };
        }

        internal async Task<bool> StartAsync(string executable, string directory, IReadOnlyList<string> arguments, MainWindow? browse,
            GitExecutionEnvironment? executionEnvironment = null)
        {
            Token.ThrowIfCancellationRequested();
            executionEnvironment ??= GitEnvironment.Current;
            TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task process = ObserveAsync();
            _pending.Add(process);
            _ = RetireAsync(process);
            return await started.Task;

            async Task ObserveAsync()
            {
                try
                {
                    ScriptProcessResult result = await new ScriptProcessRunner().RunAsync(executable, directory, arguments, Token,
                        processStarted: _ => started.TrySetResult(true), executionEnvironment: executionEnvironment);
                    started.TrySetResult(false);
                    if (!result.Success) { browse?.ReportScriptOutput("Script exited with code " + result.ExitCode + ".\n" + Encoding.UTF8.GetString(result.StandardErrorBytes)); }
                }
                catch (OperationCanceledException) { started.TrySetResult(false); }
                catch (Exception error) { started.TrySetException(error); browse?.ReportScriptOutput(error.Message); }
            }
        }
        private async Task RetireAsync(Task process)
        { try { await process; } finally { _pending.Remove(process); } }
        private async Task CloseAfterDrainAsync()
        {
            try { await Task.WhenAll(_pending.ToArray()); }
            finally
            {
                if (!_ready) { _ready = true; Dispatcher.UIThread.Post(() => { if (_closing && _owner.IsVisible) { _owner.Close(); } }); }
            }
        }
    }
}

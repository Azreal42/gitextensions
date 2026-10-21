namespace GitExtensions.Linux.Desktop;

public sealed partial class CommitDialog
{
    private readonly bool _useFormCommitMessage;
    private readonly Func<string, IReadOnlyList<string>, System.Diagnostics.Process>? _startCommitTerminal;
    private readonly string? _commitHelperExecutable;
    private bool _commitPolicyRefreshPending;
    internal GitResult? LastStagingResult { get; private set; }

    private async Task<bool> StageUsingPolicyAsync(string[] paths)
    {
        LastStagingResult = null;
        bool showErrors = _preferencesStore.LoadForStagingPolicy().ShowErrorsWhenStagingFiles;
        try
        {
            GitResult result = await _repository.StageAsync(paths, showErrors, OperationToken);
            LastStagingResult = result;
            if (result.Success) { return true; }
            LastError = string.Concat(result.StandardOutput, result.StandardError);
            _state.Text = T("Stage") + ": " + T("Failed");
            if (showErrors)
            {
                await CommitStagingReportDialog.ShowAsync(this, paths.Length, result, _translations, OperationToken);
            }
            return false;
        }
        finally
        {
            // A failed native process can still have effects. Refresh and notify; never replay staging.
            HasRepositoryChanges = true;
            _commitPolicyRefreshPending = true;
        }
    }

    private async Task<GitResult> CommitUsingGitEditorAsync(GitCommitOptions options, CancellationToken token)
    {
        bool admitted = false;
        try
        {
            return await _repository.CommitWithGitEditorAsync(options, async (arguments, cancellation) =>
            {
                admitted = true;
                GitExecutionEnvironment environment = _repository.ExecutionEnvironment;
                return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    InteractiveFileConsoleLauncher.RunWithTerminalAsync(this, _repository.Path, environment.Executable,
                        arguments, cancellation, _startCommitTerminal, _commitHelperExecutable, executionEnvironment: environment)).ConfigureAwait(false);
            }, token);
        }
        catch (Exception error) when (admitted)
        {
            HasRepositoryChanges = true;
            _commitPolicyRefreshPending = true;
            // The helper may fail or be cancelled after Git committed but before its final acknowledgement.
            // Only an observed successful result permits Push/parent staging or automatic successful closure.
            throw new IOException(T("The Git editor did not complete normally. Git may have changed the repository. Review the current HEAD and staged files before committing again.")
                + Environment.NewLine + error.Message, error);
        }
    }
}

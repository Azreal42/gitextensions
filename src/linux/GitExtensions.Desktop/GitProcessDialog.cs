using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>Result remains null for an interrupted process: cancellation never invents an exit code or command output.</summary>
public sealed record GitProcessDialogResult(GitResult? Result, Exception? Error, bool WasCancelled, bool CommandStarted)
{
    /// <summary>The owner requested closure; a completed native command can still have succeeded.</summary>
    public bool OwnerCloseRequested { get; init; }
    public bool WorkflowStopRequested { get; init; }
    public Exception? CompletionError { get; init; }
    public IReadOnlyList<GitProcessAttemptResult> Attempts { get; init; } = [];
    public bool Success => !WasCancelled && Error is null && CompletionError is null && Result?.Success == true;
}

/// <summary>Owned FormProcess-style output window. The command delegate and its pipe observer are explicit and per invocation.</summary>
public sealed class GitProcessDialog : Window
{
    private readonly Func<CancellationToken, Action<GitProcessOutput>, Task<GitResult>> _command;
    private readonly Func<GitProcessDialog, GitProcessAttemptResult, CancellationToken, Task<GitProcessCommand?>>? _handleOnExit;
    private readonly List<GitProcessAttemptResult> _attempts = [];
    private readonly ProcessDialogPreferencesStore _store;
    private readonly bool _preserveStreamedOutput;
    private readonly CancellationTokenSource _operation;
    private readonly ConcurrentQueue<GitProcessOutput> _chunks = new();
    private GitProcessTextDecoder _stdoutDecoder = new(), _stderrDecoder = new();
    private readonly MemoryStream _stdout = new(), _stderr = new();
    private readonly StringBuilder _display = new();
    private readonly PipeLine _stdoutLine = new(), _stderrLine = new();
    private readonly TextBox _output = new() { Name = "ProcessOutput", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock _notice = new() { Name = "ProcessNotice", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly ScrollViewer _noticeScroll;
    private readonly ProgressBar _progress = new() { Name = "ProgressBar", Height = 3, Minimum = 0, Maximum = 100, IsIndeterminate = true };
    private readonly CheckBox _keep = new() { Name = "KeepDialogOpen", MinHeight = 24 };
    private readonly Button _ok = new() { Name = "Ok", MinWidth = 75, Height = 25, IsDefault = true, IsEnabled = false };
    private readonly Button _abort = new() { Name = "Abort", MinWidth = 75, Height = 25 };
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private Task? _completion;
    private GitProcessDialogResult? _outcome;
    private int _drainScheduled, _resourcesDisposed;
    private bool _closeRequested, _ownerCloseRequested, _ownerCloseObserved, _closed, _starting = true, _preferenceFailed,
        _workflowStopRequested, _inCompletionHook, _acknowledgingClose;
    private string? _preferenceError;

    public GitProcessDialog(string workingDirectory, string commandDescription, bool useDialogSettings,
        Func<CancellationToken, Action<GitProcessOutput>, Task<GitResult>> command, CancellationToken cancellationToken = default,
        ProcessDialogPreferencesStore? preferencesStore = null, bool preserveStreamedOutput = false,
        Func<GitProcessDialog, GitProcessAttemptResult, CancellationToken, Task<GitProcessCommand?>>? handleOnExit = null)
    {
        ArgumentNullException.ThrowIfNull(command); ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandDescription);
        _command = command; _store = preferencesStore ?? new(); _preserveStreamedOutput = preserveStreamedOutput;
        _handleOnExit = handleOnExit;
        // Recovery children can cancel Closing while their native command drains.
        // Observe our own closure first, cancel their shared token, then await them.
        // The default, single-command windows retain their existing close behavior.
        if (handleOnExit is not null) { ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly; }
        _operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CommandDescription = commandDescription; UseDialogSettings = useDialogSettings;
        _display.Append(commandDescription).Append('\n');
        Name = "GitProcessDialog"; Title = Text("$this", "Process") + " (" + workingDirectory + ")";
        Width = 549; Height = 327; MinWidth = 500; MinHeight = 200; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _keep.Content = new AccessText { Text = _translations.TranslateAccessKeys("FormStatus", "KeepDialogOpen", "Text", "&Keep dialog open") };
        _keep.IsVisible = useDialogSettings; if (useDialogSettings) { _keep.IsChecked = !_store.Load().CloseProcessDialog; }
        _ok.Content = Text("Ok", "OK"); _abort.Content = new AccessText { Text = _translations.TranslateAccessKeys("FormStatus", "Abort", "Text", "&Abort") };
        _keep.IsCheckedChanged += (_, _) => KeepChanged();
        _ok.Click += (_, _) => { if (!IsRunning) { CloseOutcome(); } };
        _abort.Click += (_, _) => RequestClose();
        ToolTip.SetTip(_output, commandDescription);
        _noticeScroll = new() { Name = "ProcessNoticeScroll", Content = _notice, MaxHeight = 64, IsVisible = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid body = new() { RowDefinitions = new("Auto,*"), Margin = new Thickness(12) }; body.Children.Add(_noticeScroll); Grid.SetRow(_output, 1); body.Children.Add(_output);
        StackPanel controls = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6,
            Margin = new Thickness(12, 8, 12, 8), Children = { _keep, _ok, _abort } };
        Grid root = new() { RowDefinitions = new("*,3,Auto") }; root.Children.Add(body); Grid.SetRow(_progress, 1); root.Children.Add(_progress); Grid.SetRow(controls, 2); root.Children.Add(controls); Content = root;
        UpdateOutput();
        SetBadge("StatusBadgeWaiting");
        Opened += (_, _) => { _starting = false; _completion ??= ExecuteAsync(); };
        Closing += (_, args) =>
        {
            if (_closed) { return; }
            if (!_acknowledgingClose)
            {
                _workflowStopRequested = true;
                if (_outcome is not null) { _outcome = _outcome with { WorkflowStopRequested = true }; }
            }
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing)
            {
                // Child continuations can resume before the owner's own Closing event or visibility change.
                _ownerCloseObserved = true;
                _workflowStopRequested = true;
                if (_outcome is not null) { _outcome = _outcome with { OwnerCloseRequested = true, WorkflowStopRequested = true }; }
            }
            if (IsRunning)
            {
                // Avalonia asks children before raising the owner's Closing event. Retain the request we defer.
                if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _ownerCloseRequested = true; }
                args.Cancel = true; RequestClose();
            }
        };
        Closed += (_, _) => { _closed = true; if (IsRunning) { _operation.Cancel(); } else { DisposeResources(); } };
        AddHandler(KeyDownEvent, (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; RequestClose(); } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _starting = false;
    }

    public string CommandDescription { get; private set; }
    public bool UseDialogSettings { get; }
    public bool IsRunning { get; private set; } = true;
    public GitProcessDialogResult? Outcome => _outcome;
    public GitResult? Result => _outcome?.Result;
    public Exception? Error => _outcome?.Error;
    public bool WorkflowStopRequested => _workflowStopRequested;
    public ReadOnlyMemory<byte> StandardOutputBytes => _stdout.ToArray();
    public ReadOnlyMemory<byte> StandardErrorBytes => _stderr.ToArray();
    private string Text(string item, string source) => _translations.Translate("FormStatus", item, "Text", source);

    public static async Task<GitProcessDialogResult> ShowAsync(Window owner, string workingDirectory, string commandDescription, bool useDialogSettings,
        Func<CancellationToken, Action<GitProcessOutput>, Task<GitResult>> command, CancellationToken cancellationToken = default,
        ProcessDialogPreferencesStore? preferencesStore = null, bool preserveStreamedOutput = false,
        Func<GitProcessDialog, GitProcessAttemptResult, CancellationToken, Task<GitProcessCommand?>>? handleOnExit = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsVisible) { return new(null, null, true, false); }
        GitProcessDialog dialog = new(workingDirectory, commandDescription, useDialogSettings, command, cancellationToken, preferencesStore, preserveStreamedOutput, handleOnExit)
            { RequestedThemeVariant = owner.ActualThemeVariant };
        void OwnerClosed(object? sender, EventArgs args) => dialog.RequestClose();
        owner.Closed += OwnerClosed;
        using CancellationTokenRegistration registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(dialog.RequestClose));
        try { await dialog.ShowDialog(owner); }
        finally
        {
            owner.Closed -= OwnerClosed;
            if (dialog.IsRunning) { dialog.RequestClose(); }
            if (dialog._completion is Task completion) { await completion; }
            if (!dialog._closed) { dialog.Close(); }
            dialog.DisposeResources();
            // The native command and its pipe drainage have completed and the child has released its owner.
            // A busy RepositoryDialog can now observe Closing and finish its own deferred close.
            if (dialog._ownerCloseRequested && owner.IsVisible) { owner.Close(); }
        }
        return dialog._outcome ?? new(null, null, true, false);
    }

    /// <summary>Only completion hooks can show children here. Owner-close is observed before modal continuations resume.</summary>
    public async Task<T> ShowCompletionDialogAsync<T>(Window dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (!_inCompletionHook) { throw new InvalidOperationException("Completion children must be shown by the active completion hook."); }
        _operation.Token.ThrowIfCancellationRequested();
        if (_workflowStopRequested || !IsVisible) { throw new OperationCanceledException(_operation.Token); }
        dialog.RequestedThemeVariant = ActualThemeVariant;
        void ChildClosing(object? sender, WindowClosingEventArgs args)
        {
            // Avalonia closes owned children before raising this window's Closing.
            // Stop now; a child result must never race that event into another Git command.
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { RequestClose(); }
        }
        dialog.Closing += ChildClosing;
        using CancellationTokenRegistration registration = _operation.Token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        try
        {
            T result = await dialog.ShowDialog<T>(this);
            _operation.Token.ThrowIfCancellationRequested();
            if (_workflowStopRequested) { throw new OperationCanceledException(_operation.Token); }
            return result;
        }
        finally { dialog.Closing -= ChildClosing; }
    }

    private async Task ExecuteAsync()
    {
        GitProcessCommand current = new(CommandDescription, _command);
        Exception? completionError = null;
        while (true)
        {
            GitResult? result = null; Exception? error = null; bool started = false;
            try
            {
                _operation.Token.ThrowIfCancellationRequested(); started = true;
                result = await current.Execute(_operation.Token, Receive);
            }
            catch (OperationCanceledException exception) { error = exception; }
            catch (GitCommandException exception) { error = exception; result = exception.Result; }
            catch (Exception exception) { error = exception; }
            Drain();
            AppendText(_stdoutDecoder.Append([], flush: true), _stdoutLine); AppendText(_stderrDecoder.Append([], flush: true), _stderrLine);
            FinishLine(_stdoutLine); FinishLine(_stderrLine);
            // Freeze native cancellation before any recovery UI. Closing that UI later
            // stops the workflow, but cannot change an already returned native result.
            GitProcessDialogResult native = new(result, error, _operation.IsCancellationRequested || error is OperationCanceledException, started);
            GitProcessAttemptResult attempt = new(_attempts.Count + 1, CommandDescription, native, _stdout.ToArray(), _stderr.ToArray());
            _attempts.Add(attempt);
            _outcome = native with { OwnerCloseRequested = _ownerCloseObserved, WorkflowStopRequested = _workflowStopRequested,
                Attempts = Array.AsReadOnly(_attempts.ToArray()) };
            ProjectResult(result);
            UpdateOutput(); RefreshNotice();

            GitProcessCommand? next = null;
            if (_handleOnExit is not null && !native.WasCancelled && !_workflowStopRequested && !_closeRequested && !_closed)
            {
                _inCompletionHook = true;
                try
                {
                    next = await _handleOnExit(this, attempt, _operation.Token);
                    if (next is not null)
                    {
                        ArgumentException.ThrowIfNullOrWhiteSpace(next.Description);
                        ArgumentNullException.ThrowIfNull(next.Execute);
                    }
                }
                catch (OperationCanceledException) when (_operation.IsCancellationRequested || _workflowStopRequested)
                { _workflowStopRequested = true; }
                catch (Exception exception) { completionError = exception; }
                finally { _inCompletionHook = false; }
            }
            if (_operation.IsCancellationRequested) { _workflowStopRequested = true; }
            if (next is null || completionError is not null || _workflowStopRequested || _closeRequested || _closed || !IsVisible) { break; }
            current = next;
            ResetAttempt(current.Description);
        }
        _outcome = _outcome! with { CompletionError = completionError, WorkflowStopRequested = _workflowStopRequested,
            OwnerCloseRequested = _ownerCloseObserved, Attempts = Array.AsReadOnly(_attempts.ToArray()) };
        IsRunning = false;
        if (!_closed)
        {
            if (_outcome.Result is not null) { _display.Append("Done"); }
            UpdateOutput();
            RefreshNotice();
            _progress.IsVisible = false; _ok.IsEnabled = true; _abort.IsEnabled = false; _ok.Focus();
            SetBadge(_outcome.Success ? "StatusBadgeSuccess" : "StatusBadgeError");
            if (_closeRequested || (!_workflowStopRequested && _outcome.Success && UseDialogSettings && !_preferenceFailed && _store.Load().CloseProcessDialog)) { CloseOutcome(); }
        }
        else { DisposeResources(); }
    }

    private void ProjectResult(GitResult? result)
    {
        if (result is null) { return; }
        // A failed readonly probe may have no observed push bytes. Show its actual
        // result without fabricating observer events, including before a completion hook.
        if ((!_preserveStreamedOutput || _stdout.Length == 0 && _stderr.Length == 0)
            && (!_stdout.ToArray().AsSpan().SequenceEqual(result.StandardOutputBytes)
            || !_stderr.ToArray().AsSpan().SequenceEqual(result.StandardErrorBytes)
            || _stdout.Length == 0 && _stderr.Length == 0 && (result.StandardOutput.Length > 0 || result.StandardError.Length > 0)))
        {
            _display.Clear(); _display.Append(CommandDescription).Append('\n');
            AppendText(result.StandardOutput, _stdoutLine); AppendText(result.StandardError, _stderrLine);
            FinishLine(_stdoutLine); FinishLine(_stderrLine);
        }
    }

    private void ResetAttempt(string description)
    {
        _stdoutLine.ProgressTimer?.Dispose(); _stderrLine.ProgressTimer?.Dispose();
        _stdoutLine.ProgressTimer = _stderrLine.ProgressTimer = null;
        _stdoutLine.Text.Clear(); _stderrLine.Text.Clear(); _stdoutLine.CarriageReturn = _stderrLine.CarriageReturn = null;
        _stdoutDecoder = new(); _stderrDecoder = new(); _stdout.SetLength(0); _stderr.SetLength(0);
        _outcome = null;
        _display.Clear(); CommandDescription = description; _display.Append(description).Append('\n');
        ToolTip.SetTip(_output, description); _progress.IsVisible = true; _progress.IsIndeterminate = true; _progress.Value = 0;
        _ok.IsEnabled = false; _abort.IsEnabled = true; SetBadge("StatusBadgeWaiting"); RefreshNotice(); UpdateOutput();
        // Keep, preference diagnostics and all stop flags are deliberately not reset.
    }

    private void Receive(GitProcessOutput chunk)
    {
        _chunks.Enqueue(chunk);
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0) { Dispatcher.UIThread.Post(Drain); }
    }
    private void Drain()
    {
        while (_chunks.TryDequeue(out GitProcessOutput? chunk))
        {
            bool stdout = chunk.Stream == GitProcessOutputStream.StandardOutput;
            (stdout ? _stdout : _stderr).Write(chunk.Bytes.Span);
            AppendText((stdout ? _stdoutDecoder : _stderrDecoder).Append(chunk.Bytes.Span), stdout ? _stdoutLine : _stderrLine);
        }
        Interlocked.Exchange(ref _drainScheduled, 0);
        if (!_chunks.IsEmpty && Interlocked.Exchange(ref _drainScheduled, 1) == 0) { Dispatcher.UIThread.Post(Drain); }
        if (!_closed && IsRunning) { UpdateOutput(); }
    }
    private sealed class PipeLine
    { internal readonly StringBuilder Text = new(); internal string? CarriageReturn; internal IDisposable? ProgressTimer; }
    private void AppendText(string text, PipeLine line)
    {
        foreach (char character in text)
        {
            if (line.CarriageReturn is string previous)
            {
                line.ProgressTimer?.Dispose(); line.ProgressTimer = null;
                line.CarriageReturn = null;
                if (character == '\n') { _display.Append(ProjectLine(previous)).Append('\n'); continue; }
                UpdateProgress(previous);
            }
            if (character == '\r')
            {
                line.CarriageReturn = line.Text.ToString(); line.Text.Clear();
                // The Windows reader waits 300 ms for LF before treating a trailing CR as transient progress.
                line.ProgressTimer = DispatcherTimer.RunOnce(() =>
                {
                    line.ProgressTimer = null;
                    if (!_closed && IsRunning && line.CarriageReturn is string progress) { UpdateProgress(progress); }
                }, TimeSpan.FromMilliseconds(300));
            }
            else if (character == '\n') { _display.Append(ProjectLine(line.Text.ToString())).Append('\n'); line.Text.Clear(); }
            else { line.Text.Append(character); }
        }
    }
    private static string ProjectLine(string text) => text.Replace("\u001B[K", "", StringComparison.Ordinal);
    private void FinishLine(PipeLine line)
    {
        line.ProgressTimer?.Dispose(); line.ProgressTimer = null;
        if (line.CarriageReturn is string progress) { UpdateProgress(progress); }
        line.CarriageReturn = null;
        _display.Append(ProjectLine(line.Text.ToString())); line.Text.Clear();
    }
    private void UpdateOutput()
    {
        string text = _display.ToString() + ProjectLine(_stdoutLine.Text.ToString()) + ProjectLine(_stderrLine.Text.ToString());
        _output.Text = text; _output.CaretIndex = text.Length;
    }
    private void UpdateProgress(string text)
    {
        if (_closed) { return; }
        int index = text.LastIndexOf('%');
        if (index > 4 && int.TryParse(text.AsSpan(index - 3, 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out int progress) && progress >= 0)
        { _progress.IsIndeterminate = false; _progress.Value = Math.Min(100, progress); }
        if (text.Length > 0) { Title = text.TrimEnd(); }
    }
    private void KeepChanged()
    {
        if (_starting || !UseDialogSettings) { return; }
        try { _store.Save(new(_keep.IsChecked != true)); _preferenceFailed = false; _preferenceError = null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _preferenceError = error.Message; _preferenceFailed = true; RefreshNotice(); return; }
        RefreshNotice();
        if (_keep.IsChecked != true && !IsRunning && !_workflowStopRequested && _outcome?.Success == true) { CloseOutcome(); }
    }
    private void RefreshNotice()
    {
        List<string> notices = [];
        if (!string.IsNullOrWhiteSpace(_preferenceError)) { notices.Add(_preferenceError); }
        if (_outcome?.Result is GitResult result) { notices.AddRange(result.Warnings); }
        if (_outcome?.Error is Exception error && error is not GitCommandException)
        { notices.Add(_outcome.WasCancelled ? _translations.Translate("Operation cancelled") : error.Message); }
        if (_outcome?.CompletionError is Exception completionError) { notices.Add(completionError.Message); }
        _notice.Text = string.Join(Environment.NewLine, notices.Distinct()); _noticeScroll.IsVisible = _notice.IsVisible = notices.Count > 0;
    }
    private void RequestClose()
    {
        if (_closed) { return; }
        _closeRequested = _workflowStopRequested = true;
        if (_outcome is not null) { _outcome = _outcome with { WorkflowStopRequested = true }; }
        if (IsRunning) { _operation.Cancel(); }
        else { CloseOutcome(); }
    }
    private void CloseOutcome()
    {
        if (_closed || _outcome is null) { return; }
        _acknowledgingClose = true;
        try { Close(_outcome); }
        finally { _acknowledgingClose = false; }
    }
    private void SetBadge(string name)
    { using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + name + ".png")); Icon = new WindowIcon(stream); }
    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0)
        { _stdoutLine.ProgressTimer?.Dispose(); _stderrLine.ProgressTimer?.Dispose(); _operation.Dispose(); _stdout.Dispose(); _stderr.Dispose(); }
    }
}

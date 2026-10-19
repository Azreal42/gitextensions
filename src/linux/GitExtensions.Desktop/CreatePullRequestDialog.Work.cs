using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed record PullRequestHostedRemote(string Name, GitHubHostedRepository Repository, bool IsOwnedByMe)
{
    internal string DisplayData => Repository.Owner + "/" + Repository.Name;
    public override string ToString() => DisplayData;
}

/// <summary>Both hosted dialogs own their asynchronous work, including stale cancelled reads.</summary>
internal abstract class PullRequestDialog : Window
{
    protected readonly TranslationCatalog Translations = TranslationCatalog.LoadDefault();
    protected readonly TextBlock Notice = new() { Name = "PullRequestNotice", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new(4, 1) };
    protected readonly CancellationTokenSource Lifetime = new();
    private readonly HashSet<Task> _work = [];
    private bool _closeReady, _closeRequested, _result;
    private Window? _closingOwner;
    private Task? _close;
    protected bool ClosingRequested => _closeRequested;
    internal bool IsBusy => _work.Count != 0;
    protected bool Mutating { get; private set; }
    internal virtual Task Completion => Task.WhenAll(_work.ToArray());
    protected PullRequestDialog()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner; CanMinimize = false; ShowInTaskbar = false;
        Opened += (_, _) => ModalWindowActivation.AfterMapping(this, () => !ClosingRequested);
        Closing += (_, args) =>
        {
            if (_closeReady) { return; }
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingOwner = Owner as Window; }
            Task pending = BeginClose();
            if (!pending.IsCompleted) { args.Cancel = true; _ = CloseWhenDrainedAsync(pending); }
            else { _closeReady = true; }
        };
        Closed += (_, _) =>
        {
            _ = BeginClose(); Lifetime.Dispose(); DisposeControls();
            Window? owner = _closingOwner; _closingOwner = null;
            if (owner is not null) { Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        };
    }
    protected virtual void DisposeControls() { }
    protected virtual Task CloseChildrenAsync() => Task.CompletedTask;
    protected virtual void StateChanged() { }
    protected Task Run(Func<CancellationToken, Task> action, bool mutation = false, Func<bool>? current = null)
    {
        if (_closeRequested || (mutation && Mutating)) { return Task.CompletedTask; }
        if (mutation) { Mutating = true; }
        Task task = ExecuteAsync(action, mutation, current); _work.Add(task); StateChanged(); _ = RemoveAsync(task); return task;
    }
    private async Task ExecuteAsync(Func<CancellationToken, Task> action, bool mutation, Func<bool>? current)
    {
        try { await action(Lifetime.Token); }
        catch (OperationCanceledException) when (_closeRequested || Lifetime.IsCancellationRequested || current?.Invoke() == false) { }
        catch (Exception error)
        { if (!_closeRequested && (current?.Invoke() ?? true)) { Notice.Text = error.Message; } }
        finally { if (mutation) { Mutating = false; } }
    }
    private async Task RemoveAsync(Task task)
    { try { await task; } finally { _work.Remove(task); StateChanged(); } }
    internal Task BeginClose()
    {
        if (_close is not null) { return _close; }
        _closeRequested = true; Lifetime.Cancel(); StateChanged();
        return _close = Task.WhenAll(Completion, CloseChildrenAsync());
    }
    protected void Accept(bool result)
    { _result = result; if (IsVisible) { Close(result); } }
    private async Task CloseWhenDrainedAsync(Task pending)
    { await pending; _closeReady = true; if (IsVisible) { Close(_result); } }
    protected string T(string category, string name, string source) => Translations.Translate(category, name, "Text", source);
    protected string T(string source) => Translations.Translate(source);
    protected Button ActionButton(string category, string name, string caption, Func<Task> action)
    {
        Button button = new() { Name = name, Content = T(category, name, caption), MinHeight = 25, Padding = new(7, 2) };
        button.Click += async (_, _) => { if (!_closeRequested) { await action(); } }; return button;
    }
}

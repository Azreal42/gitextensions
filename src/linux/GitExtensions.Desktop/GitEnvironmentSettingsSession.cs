using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>Owns a Settings scan and its publications; checkboxes do not publish pending path drafts.</summary>
internal sealed class GitEnvironmentSettingsSession : IDisposable
{
    private readonly GitEnvironmentPreferencesStore _store;
    private readonly TranslationCatalog _translations;
    private readonly Func<GitRepository?> _repository;
    private readonly CancellationTokenSource _lifetime;
    private GitEnvironmentPreferencesLoadResult _baseline;
    private bool _disposed;
    private TaskCompletionSource? _completion;
    internal GitEnvironmentSettingsPage Page { get; }
    internal ChecklistSettingsPage Checklist { get; }
    internal bool IsBusy { get; private set; }
    internal bool HasProblems { get; private set; } = true;
    internal CancellationToken CancellationToken => _lifetime.Token;
    internal Task OperationCompletion => _completion?.Task ?? Task.CompletedTask;
    internal event Action? BusyChanged;
    internal event Action? Settled;
    internal Func<Task<bool>>? SaveAllRequested { get; set; }
    internal Func<GitEnvironmentCheckId, Task>? RepairRequested { get; set; }
    internal GitEnvironmentChecklistResult? LastScan { get; private set; }

    internal GitEnvironmentSettingsSession(GitEnvironmentPreferencesStore store, TranslationCatalog translations,
        Func<GitRepository?> repository, CancellationToken ownerToken = default)
    {
        _store = store; _translations = translations; _repository = repository;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ownerToken);
        _baseline = store.Load(); Page = new(_baseline.Preferences, translations); Checklist = new(translations);
        Checklist.CheckAtStartupChanged = SetCheckAtStartup;
        Checklist.SaveAndRescanRequested = async () =>
        {
            if (SaveAllRequested is not { } save || !await save()) { return; }
            await ScanAsync();
        };
        Checklist.RepairRequested = async id =>
        {
            if (RepairRequested is { } repair) { await repair(id); }
        };
        Checklist.Render([], _baseline.Preferences.CheckAtStartup);
        ShowError(_baseline.Error?.Message);
    }

    internal static GitEnvironmentOptions Options(GitEnvironmentPreferences preferences) => new()
    {
        Executable = preferences.GitExecutable.Length == 0 ? "git" : preferences.GitExecutable,
        ToolsDirectory = preferences.ToolsDirectory.Length == 0 ? null : preferences.ToolsDirectory,
        HomeDirectory = preferences.CustomHomeDirectory
    };

    internal bool TrySave()
    {
        if (_disposed || _lifetime.IsCancellationRequested || IsBusy) { return false; }
        if (_baseline.Error is not null && !Page.IsDirty) { return true; }
        if (!Page.Validate(out string? validation)) { ShowError(validation); return false; }
        try
        {
            GitEnvironmentPreferences draft = Page.Draft;
            _ = GitExecutionEnvironment.Capture(Options(draft));
            GitEnvironmentPreferencesLoadResult saved = _store.SaveChanges(_baseline, draft);
            GitEnvironment.Configure(Options(saved.Preferences));
            if (Options(_baseline.Preferences) != Options(saved.Preferences)) { LastScan = null; HasProblems = true; }
            _baseline = saved; Page.AcceptSaved(saved.Preferences); ShowError(null);
            Checklist.Render(LastScan?.Checks ?? [], saved.Preferences.CheckAtStartup);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { ShowError(error.Message); return false; }
    }

    internal void Reload()
    {
        if (_disposed || IsBusy) { return; }
        GitEnvironmentPreferencesLoadResult latest = _store.Load();
        if (latest.Error is not null) { ShowError(latest.Error.Message); return; }
        if (Options(_baseline.Preferences) != Options(latest.Preferences)) { LastScan = null; HasProblems = true; }
        GitEnvironment.Configure(Options(latest.Preferences));
        _baseline = latest; Page.RebasePublished(latest.Preferences);
        Checklist.Render(LastScan?.Checks ?? [], Page.Draft.CheckAtStartup);
        ShowError(null);
    }

    private void SetCheckAtStartup(bool value)
    {
        if (_disposed || IsBusy || _lifetime.IsCancellationRequested) { return; }
        SaveStartupFlag(value);
    }

    private bool SaveStartupFlag(bool value)
    {
        try
        {
            GitEnvironmentPreferencesLoadResult saved = _store.SaveChanges(_baseline, _baseline.Preferences with { CheckAtStartup = value });
            _baseline = saved; Page.RebasePublished(saved.Preferences);
            Checklist.Render(LastScan?.Checks ?? [], saved.Preferences.CheckAtStartup);
            ShowError(null); return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Checklist.Render(LastScan?.Checks ?? [], _baseline.Preferences.CheckAtStartup);
            ShowError(error.Message); return false;
        }
    }

    internal async Task ScanAsync()
    {
        if (_disposed || IsBusy || _lifetime.IsCancellationRequested) { return; }
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _completion = completion;
        IsBusy = true;
        try
        {
            Page.SetBusy(true); Checklist.SetBusy(true); BusyChanged?.Invoke();
            // Scans describe published settings. Draft edits are published only by Save all / OK.
            GitEnvironmentPreferencesLoadResult latest = _store.Load();
            if (latest.Error is null)
            {
                GitEnvironment.Configure(Options(latest.Preferences));
                Page.RebasePublished(latest.Preferences);
            }
            _baseline = latest;
            GitExecutionEnvironment environment = _baseline.Error is null
                ? GitExecutionEnvironment.Capture(Options(_baseline.Preferences)) : GitEnvironment.Current;
            string? language = new PreferencesStore().Load().Language;
            GitEnvironmentChecklistResult result = await GitEnvironmentChecklist.ScanAsync(_repository(), environment, _translations, language, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            LastScan = result; HasProblems = !result.AllGood || _baseline.Error is not null;
            Page.SetEnvironmentInformation(result.EnvironmentInformation);
            bool publicationFailed = false;
            if (result.AllGood && _baseline.Error is null && _baseline.Preferences.CheckAtStartup)
            { publicationFailed = !SaveStartupFlag(false); HasProblems |= publicationFailed; }
            Checklist.Render(result.Checks, _baseline.Preferences.CheckAtStartup);
            if (!publicationFailed) { ShowError(_baseline.Error?.Message); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        { HasProblems = true; if (!_disposed) { ShowError(error.Message); } }
        finally
        {
            IsBusy = false;
            try { if (!_disposed) { Page.SetBusy(false); Checklist.SetBusy(false); BusyChanged?.Invoke(); } }
            finally { completion.TrySetResult(); Settled?.Invoke(); }
        }
    }

    internal void Cancel() => _lifetime.Cancel();
    internal void ShowError(string? message) { Page.ShowError(message); Checklist.ShowError(message); }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true; _lifetime.Cancel(); Page.Invalidate(); Checklist.Invalidate(); _lifetime.Dispose();
    }
}

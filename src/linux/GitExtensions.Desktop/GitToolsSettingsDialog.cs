using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>Git Config, with independent local/global drafts and explicit publication.</summary>
internal sealed class GitToolsSettingsDialog : Window
{
    private readonly GitRepository _repository;
    private readonly TranslationCatalog _translations;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _ownerCancellation;
    private readonly GitToolsSettingsPage _page;
    private readonly GitConfigAdvancedSettingsPage _advancedPage;
    private readonly TabControl _tabs;
    private readonly AvailableEncodingsPreferencesStore _encodingPreferences;
    private readonly TextBox _error = new()
    {
        Name = "GitToolsError", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        MaxHeight = 140, IsVisible = false
    };
    private readonly Button _ok, _cancel, _apply, _reload;
    private CancellationTokenSource? _operationCancellation;
    private TaskCompletionSource? _completion;
    private Task? _initialization;
    private bool _closed, _closeRequested, _acceptWhenIdle, _requiresReload, _closeOwnerWhenIdle, _ownerCloseRequested;
    private Window? _observedOwner;
    private GitToolsDraftSession.ValidationTarget? _validationTarget;
    internal bool IsBusy { get; private set; }
    internal bool IsReady => _page.Session is { } session && session.Snapshot.Scopes.Any(scope => scope.IsAvailable);
    internal bool MayHaveChangedConfiguration { get; private set; }
    internal bool OwnerCloseRequested => _ownerCloseRequested;
    internal string? LastError { get; private set; }
    internal GitToolConfigurationSaveResult? LastSaveResult { get; private set; }
    internal Task OperationCompletion => _completion?.Task ?? Task.CompletedTask;
    internal GitToolsSettingsPage Page => _page;
    internal GitConfigAdvancedSettingsPage AdvancedPage => _advancedPage;
    internal Func<GitToolRole, string, CancellationToken, Task<string?>>? FilePicker { get; set; }
    internal Func<string, CancellationToken, Task<string?>>? TemplateFilePicker { get; set; }

    internal GitToolsSettingsDialog(GitRepository repository, TranslationCatalog? translations = null,
        GitToolRole? focus = null, CancellationToken ownerToken = default, AvailableEncodingsPreferencesStore? encodingPreferences = null,
        GitToolConfigurationView? initialView = null, GitConfigurationField? focusField = null, bool advanced = false)
    {
        _repository = repository; _translations = translations ?? TranslationCatalog.LoadDefault();
        _encodingPreferences = encodingPreferences ?? new();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ownerToken);
        Name = "GitToolsSettingsDialog";
        Title = _translations.Translate("GitConfigSettingsPage", "$this", "Text", "Config");
        Width = 820; MinWidth = 660; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Observe our own owner-close request before an owned child's close can veto it.
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        _page = new(_translations); _page.DraftChanged += UpdateActions; _page.BrowseRequested = BrowseAsync;
        _page.TemplateBrowseRequested = BrowseTemplateAsync; _page.ConfigureEncodingRequested = ConfigureEncodingAsync;
        _advancedPage = new(_translations); _advancedPage.DraftChanged += UpdateActions;
        _tabs = new() { Name = "GitConfigPages", ItemsSource = new[]
        {
            new TabItem { Name = "GitConfigTab", Header = Title, Content = _page },
            new TabItem { Name = "GitAdvancedTab", Header = _translations.Translate("GitConfigAdvancedSettingsPage", "$this", "Text", "Advanced"), Content = _advancedPage }
        }, SelectedIndex = advanced ? 1 : 0 };
        _tabs.SelectionChanged += (_, args) =>
        {
            if (args.Source != _tabs || _page.Session is not { } session) { return; }
            if (_tabs.SelectedIndex == 1) { _advancedPage.SelectScope(_page.View); _advancedPage.SetSession(session); }
            else { _page.SelectScope(_advancedPage.View); _page.SetSession(session); }
        };
        ClassicChoiceAppearance.Apply(this, "git-config-advanced");
        ReloadEncodingChoices(); AvailableEncodingsPreferencesStore.Saved += EncodingPreferencesSaved;
        _ok = Footer("GitToolsAccept", "OK"); _ok.IsDefault = true;
        _cancel = Footer("GitToolsCancel", "Cancel"); _cancel.IsCancel = true;
        _apply = Footer("GitToolsApply", "Apply"); _reload = Footer("GitToolsReload", "Reload");
        _ok.Click += async (_, _) => await PublishAsync(closeOnSuccess: true);
        _apply.Click += async (_, _) => await PublishAsync(closeOnSuccess: false);
        _cancel.Click += (_, _) => RequestClose();
        _reload.Click += async (_, _) => await RunOwnedAsync(LoadAsync);
        StackPanel body = new() { Margin = new Thickness(12), Spacing = 10, Children =
        {
            _tabs,
            new TextBlock { Name = "GitToolsNotice", Text = _translations.Translate("Apply saves Git configuration. Cancel discards only unsaved changes."), TextWrapping = TextWrapping.Wrap },
            _error, _reload
        } };
        DockPanel root = new() { LastChildFill = true };
        StackPanel footer = new()
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 6), Spacing = 6, Children = { _ok, _cancel, _apply }
        };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Name = "GitToolsBodyScroll", Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Opened += async (_, _) =>
        {
            if (Owner is Window owner)
            {
                _observedOwner = owner; RequestedThemeVariant = owner.ActualThemeVariant;
                owner.Closing += OwnerClosing;
            }
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                MaxHeight = Math.Max(240, screen.WorkingArea.Height / screen.Scaling - 40);
                MaxWidth = Math.Max(320, screen.WorkingArea.Width / screen.Scaling - 40);
                MinWidth = Math.Min(MinWidth, MaxWidth); Width = Math.Min(Width, MaxWidth);
            }
            await InitializeAsync();
            if (!_closeRequested && IsVisible)
            {
                if (initialView is { } view) { _page.SelectScope(view); _advancedPage.SelectScope(view); }
                if (focus is { } role) { _tabs.SelectedIndex = 0; _page.FocusRole(role); }
                if (focusField is { } field) { FocusValidation(new(_page.View, Field: field)); }
                ModalWindowActivation.AfterMapping(this, () => !_closeRequested && !_closed);
            }
        };
        Closing += (_, args) =>
        {
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing)
            {
                _ownerCloseRequested = true;
                // Avalonia asks children first. A busy veto can prevent Owner.Closing entirely.
                if (IsBusy) { _closeOwnerWhenIdle = true; }
            }
            if (IsBusy)
            {
                args.Cancel = true; _closeRequested = true; _page.Invalidate(); _advancedPage.Invalidate();
                _lifetime.Cancel(); _operationCancellation?.Cancel();
            }
        };
        Closed += (_, _) =>
        {
            _closed = true; _page.Invalidate(); _advancedPage.Invalidate(); _lifetime.Cancel();
            AvailableEncodingsPreferencesStore.Saved -= EncodingPreferencesSaved;
            _ownerCancellation.Dispose();
            if (_observedOwner is { } owner)
            {
                owner.Closing -= OwnerClosing;
                if (_closeOwnerWhenIdle) { Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
            }
            _lifetime.Dispose();
        };
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; RequestClose(); } };
        _ownerCancellation = ownerToken.Register(() => Dispatcher.UIThread.Post(RequestClose));
        UpdateActions();
    }

    internal Task InitializeAsync() => _initialization ??= RunOwnedAsync(LoadAsync);

    private async Task LoadAsync(CancellationToken token)
    {
        GitToolConfigurationSnapshot snapshot = await _repository.GetToolConfigurationAsync(token);
        token.ThrowIfCancellationRequested();
        SetSession(_page.Session?.Rebase(snapshot) ?? new(snapshot));
        _requiresReload = !snapshot.Scopes.Any(scope => scope.IsAvailable);
        if (_requiresReload) { ShowError(string.Join("\n", snapshot.Scopes.Select(scope => scope.Diagnostic).Where(text => !string.IsNullOrEmpty(text)).Distinct())); }
        else { ShowError(null); }
    }

    internal Task PublishAsync(bool closeOnSuccess) => RunOwnedAsync(async token =>
    {
        GitToolsDraftSession? session = _page.Session;
        if (session is null || _requiresReload) { return; }
        if (!session.Validate(out string? validation, out GitToolsDraftSession.ValidationTarget target))
        { _validationTarget = target; SelectValidationPage(target); ShowError(validation ?? _translations.Translate("Unable to save settings.")); return; }
        IReadOnlyList<GitToolConfigurationScopeEdit> edits = session.BuildEdits();
        if (edits.Count == 0) { _acceptWhenIdle = closeOnSuccess; return; }
        GitToolConfigurationSaveResult result;
        try { result = await _repository.SaveToolConfigurationAsync(session.Snapshot, edits, token); }
        catch (GitToolConfigurationSaveCanceledException error) { result = error.Result; }
        catch (GitToolConfigurationSaveException error) { result = error.Result; }
        catch { MayHaveChangedConfiguration = true; _requiresReload = true; throw; }
        LastSaveResult = result; MayHaveChangedConfiguration |= result.MayHaveChanged;
        if (result.After is { } after) { SetSession(session.Rebase(after)); }
        _requiresReload = !result.Success || result.After is null;
        if (_requiresReload)
        {
            string diagnostic = result.Diagnostic ?? _translations.Translate(result.Cancelled ? "Operation cancelled" : "Unable to save settings.");
            string receipts = string.Join("\n", result.Writes.Select(write =>
                $"{write.Scope}: {write.Key} — {write.Outcome}" + (write.NativeResult is { } native ? $" ({native.ExitCode})" : "")));
            ShowError(diagnostic + (receipts.Length > 0 ? "\n" + receipts : "") + "\n" + _translations.Translate("Reload published configuration before saving again. Unsaved values are retained."));
        }
        else
        {
            List<string> warnings = [];
            if (result.Diagnostic is { Length: > 0 } diagnostic) { warnings.Add(diagnostic); }
            warnings.AddRange(result.Writes.Where(write => !string.IsNullOrEmpty(write.Diagnostic))
                .Select(write => $"{write.Scope}: {write.Key} — {write.Diagnostic}"));
            bool pending = _page.Session?.IsDirty == true;
            if (warnings.Count > 0 || pending)
            {
                string observed = string.Join("\n", result.Writes.Select(write =>
                {
                    GitToolConfigurationView scope = write.Scope == GitConfigScope.Local ? GitToolConfigurationView.Local : GitToolConfigurationView.Global;
                    GitToolConfigurationScopeSnapshot snapshot = result.After!.GetScope(scope);
                    GitConfigurationField? field = Enum.GetValues<GitConfigurationField>().Cast<GitConfigurationField?>().FirstOrDefault(candidate => GitConfigurationFields.Key(candidate!.Value) == write.Key);
                    GitToolConfigurationOccurrence? entry = field is { } setting ? snapshot.GetSetting(setting).Occurrences.LastOrDefault()
                        : snapshot.Occurrences.LastOrDefault(occurrence => occurrence.Key == write.Key);
                    string published = entry is null ? _translations.Translate("Not set") : entry.Value ?? _translations.Translate("No value");
                    return $"{write.Scope}: {write.Key} = {published}";
                }));
                ShowError(string.Join("\n", warnings) + (pending ? "\n" + _translations.Translate("Some requested values still differ from the published configuration.") : "")
                    + "\n" + _translations.Translate("Published values") + ":\n" + observed);
                _acceptWhenIdle = false;
            }
            else { ShowError(null); _acceptWhenIdle = closeOnSuccess; }
        }
    });

    private Task BrowseAsync(GitToolRole role)
    {
        GitToolsSettingsPage.PickerStamp? stamp = _page.CapturePicker(role);
        if (stamp is null) { return Task.CompletedTask; }
        return RunOwnedAsync(async token =>
        {
            string? path = FilePicker is { } picker ? await picker(role, stamp.Path, token) : await PickNativeAsync(role, stamp.Name, stamp.Path, token);
            token.ThrowIfCancellationRequested();
            if (!_closed && !_closeRequested && IsVisible && path is not null) { _page.AcceptPicker(stamp, path); }
        });
    }

    private Task BrowseTemplateAsync()
    {
        GitToolsSettingsPage.SettingPickerStamp? stamp = _page.CaptureTemplatePicker();
        if (stamp is null) { return Task.CompletedTask; }
        return RunOwnedAsync(async token =>
        {
            string? path = TemplateFilePicker is { } picker ? await picker(stamp.Path, token)
                : await PickFileAsync(stamp.Path, null, token, template: true);
            token.ThrowIfCancellationRequested();
            if (!_closed && !_closeRequested && IsVisible && path is not null) { _page.AcceptTemplatePicker(stamp, path); }
        });
    }

    private Task ConfigureEncodingAsync() => RunOwnedAsync(async token =>
    {
        AvailableEncodingsDialog dialog = new(_encodingPreferences, translations: _translations);
        using CancellationTokenRegistration close = token.Register(() => Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) { dialog.Close(false); } }));
        await dialog.ShowDialog<bool>(this);
        token.ThrowIfCancellationRequested();
        if (!_closed && !_closeRequested) { ReloadEncodingChoices(); }
    });

    private void ReloadEncodingChoices()
    {
        AvailableEncodingsPreferencesLoadResult preferences = _encodingPreferences.Load();
        _page.SetEncodingChoices(_encodingPreferences.Catalog.Choices(preferences.Names), preferences.Error?.Message);
    }

    private void EncodingPreferencesSaved(string path)
    {
        if (string.Equals(path, _encodingPreferences.FilePath, StringComparison.Ordinal))
        { Dispatcher.UIThread.Post(() => { if (!_closed && !_closeRequested) { ReloadEncodingChoices(); } }); }
    }

    private async Task<string?> PickNativeAsync(GitToolRole role, string name, string path, CancellationToken token)
        => await PickFileAsync(path, GitToolsCatalog.PreferredExecutable(role, name), token);

    private async Task<string?> PickFileAsync(string path, string? preferred, CancellationToken token, bool template = false)
    {
        string? startPath = ExistingDirectory(path) ?? ExistingDirectory(preferred);
        using IStorageFolder? start = startPath is null ? null : await StorageProvider.TryGetFolderFromPathAsync(new Uri(startPath));
        token.ThrowIfCancellationRequested();
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _translations.Translate("Select file"), AllowMultiple = false, SuggestedStartLocation = start,
            FileTypeFilter = template
                ? [new FilePickerFileType(_translations.Translate("Text files")) { Patterns = ["*.txt"] }, new FilePickerFileType(_translations.Translate("All files")) { Patterns = ["*"] }]
                : [new FilePickerFileType(_translations.Translate("All files")) { Patterns = ["*"] }]
        });
        try
        {
            token.ThrowIfCancellationRequested();
            return files.Count == 0 ? null : files[0].TryGetLocalPath() ?? throw new IOException(_translations.Translate("Select a local file."));
        }
        finally { foreach (IStorageFile file in files) { file.Dispose(); } }
    }

    private async Task RunOwnedAsync(Func<CancellationToken, Task> action)
    {
        if (_closed || _closeRequested || IsBusy) { return; }
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _completion = completion;
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationCancellation = operation; IsBusy = true; _page.SetBusy(true); _advancedPage.SetBusy(true); _tabs.IsEnabled = false; UpdateActions();
        try { await action(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { if (!_closeRequested) { ShowError(_translations.Translate("Operation cancelled")); } }
        catch (Exception error)
        { _requiresReload = true; if (!_closed) { ShowError(error.Message); } }
        finally
        {
            _operationCancellation = null; IsBusy = false;
            if (!_closed) { _page.SetBusy(false); _advancedPage.SetBusy(false); _tabs.IsEnabled = true; UpdateActions(); }
            if (!_closed && !_closeRequested && _validationTarget is { } target)
            { _validationTarget = null; FocusValidation(target); }
            completion.TrySetResult();
            if (!_closed && (_closeRequested || _acceptWhenIdle)) { Close(_acceptWhenIdle && !_closeRequested); }
        }
    }

    internal void RequestClose()
    {
        if (_closed) { return; }
        _closeRequested = true; _page.Invalidate(); _advancedPage.Invalidate(); _lifetime.Cancel(); _operationCancellation?.Cancel();
        if (!IsBusy) { Close(false); }
    }

    private void OwnerClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_closed) { return; }
        _ownerCloseRequested = true;
        if (IsBusy) { args.Cancel = true; _closeOwnerWhenIdle = true; }
        RequestClose();
    }

    private void SetSession(GitToolsDraftSession session)
    { _page.SetSession(session); _advancedPage.SetSession(session); }

    internal void SelectAdvancedPage(bool advanced) => _tabs.SelectedIndex = advanced ? 1 : 0;

    private void SelectValidationPage(GitToolsDraftSession.ValidationTarget target)
    {
        _tabs.SelectedIndex = target.Field is { } field && GitConfigurationFields.IsAdvancedBoolean(field) ? 1 : 0;
        _page.SelectScope(target.View); _advancedPage.SelectScope(target.View);
    }

    private void FocusValidation(GitToolsDraftSession.ValidationTarget target)
    {
        SelectValidationPage(target);
        if (_tabs.SelectedIndex == 1) { _advancedPage.FocusValidation(target); } else { _page.FocusValidation(target); }
    }

    private void UpdateActions()
    {
        bool live = !_closed && !_closeRequested && !IsBusy;
        _ok.IsEnabled = live && IsReady && !_requiresReload;
        _apply.IsEnabled = live && IsReady && !_requiresReload && _page.Session?.IsDirty == true;
        _reload.IsVisible = _requiresReload; _reload.IsEnabled = live;
    }
    private void ShowError(string? text) { LastError = text; _error.Text = text; _error.IsVisible = !string.IsNullOrEmpty(text); }
    private Button Footer(string name, string source) => new() { Name = name, Content = _translations.Translate(source), MinWidth = 75, HorizontalAlignment = HorizontalAlignment.Right };
    private static string? ExistingDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path)) { return null; }
        try
        {
            string full = Path.GetFullPath(path);
            if (Directory.Exists(full)) { return full; }
            string? parent = Path.GetDirectoryName(full); return parent is not null && Directory.Exists(parent) ? parent : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }
}

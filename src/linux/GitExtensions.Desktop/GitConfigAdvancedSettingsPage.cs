using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original eight advanced Git booleans, using the native Config session and writer.</summary>
internal sealed class GitConfigAdvancedSettingsPage : UserControl
{
    private sealed record ScopeChoice(GitToolConfigurationView View, string Label);
    private readonly TranslationCatalog _translations;
    private readonly ComboBox _scope = new() { Name = "GitAdvancedScope", MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _notice = new() { Name = "GitAdvancedScopeNotice", TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<GitConfigurationField, CheckBox> _fields = [];
    private readonly ScopeChoice[] _choices;
    private bool _loading, _busy, _closed;
    internal GitToolConfigurationView View { get; private set; } = GitToolConfigurationView.Effective;
    internal GitToolsDraftSession? Session { get; private set; }
    internal event Action? DraftChanged;

    internal GitConfigAdvancedSettingsPage(TranslationCatalog translations)
    {
        _translations = translations; Name = "GitConfigAdvancedSettingsPage";
        _choices =
        [
            new(GitToolConfigurationView.Effective, ScopeText("EffectiveRB", "Effective")),
            new(GitToolConfigurationView.Local, ScopeText("LocalRB", "Local for current repository")),
            new(GitToolConfigurationView.Global, ScopeText("GlobalRB", "Global for all repositories")),
            new(GitToolConfigurationView.System, ScopeText("SystemRB", "System"))
        ];
        _scope.ItemsSource = _choices;
        _scope.ItemTemplate = new FuncDataTemplate<ScopeChoice>((choice, _) => new TextBlock { Text = choice?.Label });
        _scope.SelectedItem = _choices[0];
        _scope.SelectionChanged += (_, _) =>
        {
            if (_loading || _busy || _closed || _scope.SelectedItem is not ScopeChoice next || next.View == View) { return; }
            View = next.View; Refresh(); DraftChanged?.Invoke();
        };
        StackPanel options = new() { Name = "GitAdvancedOptions", Spacing = 1, Margin = new Thickness(7, 0, 0, 0) };
        Add(options, GitConfigurationField.PullRebase, "checkBoxPullRebase", "Rebase local branch when pulling (instead of merge)");
        Add(options, GitConfigurationField.FetchPrune, "checkBoxFetchPrune", "Prune remote branches during fetch");
        Add(options, GitConfigurationField.MergeAutoStash, "checkboxMergeAutoStash", "Automatically stash before doing a merge");
        Add(options, GitConfigurationField.RebaseAutoStash, "checkBoxRebaseAutostash", "Automatically stash before doing a rebase");
        Add(options, GitConfigurationField.RebaseAutoSquash, "checkBoxRebaseAutosquash", "Automatically squash commits when doing an interactive rebase");
        Add(options, GitConfigurationField.RebaseUpdateRefs, "checkBoxUpdateRefs", "Rebase also dependent branches");
        Add(options, GitConfigurationField.RerereEnabled, "checkBoxReReReEnabled", "Reuse recorded resolution of conflicted merges");
        Add(options, GitConfigurationField.RerereAutoUpdate, "checkBoxReReReAutoUpdate", "Automatically apply recorded resolution of conflicted merges");
        Content = new StackPanel { Spacing = 8, Children = { _scope, _notice, options } };
        Refresh();
    }

    private void Add(StackPanel options, GitConfigurationField field, string name, string source)
    {
        CheckBox box = new() { Name = name, IsThreeState = true,
            Content = new TextBlock { Text = _translations.Translate("GitConfigAdvancedSettingsPage", name, "Text", source)
                + " [" + GitConfigurationFields.Key(field) + "]", TextWrapping = TextWrapping.Wrap } };
        ReferenceDialogControls.Compact(box); box.Height = double.NaN;
        _fields.Add(field, box); options.Children.Add(box);
        box.IsCheckedChanged += (_, _) =>
        {
            if (!CanAct(field)) { return; }
            Session!.Scope(View).Setting(field).SetValue(box.IsChecked switch { true => "true", false => "false", null => null });
            Refresh(); DraftChanged?.Invoke();
        };
        MenuItem unset = new() { Name = name + "Unset", Header = _translations.Translate("Unset in this scope") };
        MenuItem restore = new() { Name = name + "RestorePublished", Header = _translations.Translate("Restore published value") };
        unset.Click += (_, _) => Change(field, unset: true);
        restore.Click += (_, _) => Change(field, unset: false);
        box.ContextMenu = new() { ItemsSource = new[] { unset, restore } };
        box.ContextMenu.Opened += (_, _) => { unset.IsEnabled = restore.IsEnabled = CanAct(field); };
    }

    private void Change(GitConfigurationField field, bool unset)
    {
        if (!CanAct(field)) { return; }
        GitToolsDraftSession.SettingDraft draft = Session!.Scope(View).Setting(field);
        if (unset) { draft.SetValue(null); } else { draft.RestorePublished(); }
        Refresh(); DraftChanged?.Invoke();
    }

    private bool CanAct(GitConfigurationField field) => !_loading && !_busy && !_closed && Session?.Scope(View).CanEdit(field) == true;
    internal void SetSession(GitToolsDraftSession session) { Session = session; Refresh(); }
    internal void SetBusy(bool busy) { _busy = busy; Refresh(); }
    internal void Invalidate() { _closed = true; Refresh(); }
    internal void SelectScope(GitToolConfigurationView view) { View = view; Refresh(); }
    internal void FocusValidation(GitToolsDraftSession.ValidationTarget target)
    {
        SelectScope(target.View);
        if (target.Field is { } field && _fields.TryGetValue(field, out CheckBox? box)) { box.BringIntoView(); box.Focus(); }
    }

    private void Refresh()
    {
        _loading = true;
        try
        {
            _scope.SelectedItem = _choices.Single(choice => choice.View == View);
            _scope.IsEnabled = !_busy && !_closed && Session is not null;
            List<string> notices = [];
            GitToolsDraftSession.ScopeDraft? scope = Session?.Scope(View);
            if (scope is null) { notices.Add(_translations.Translate("Loading Git configuration…")); }
            else
            {
                if (!scope.Snapshot.CanWrite) { notices.Add(_translations.Translate("This scope is read-only.")); }
                if (scope.Snapshot.Diagnostic is { Length: > 0 } diagnostic) { notices.Add(diagnostic); }
                if (Session!.IsDirty) { notices.Add(_translations.Translate("Local or global configuration has unsaved changes. Effective shows published values.")); }
            }
            foreach ((GitConfigurationField field, CheckBox box) in _fields)
            {
                GitToolsDraftSession.SettingDraft? setting = scope?.Setting(field);
                box.IsVisible = field != GitConfigurationField.RebaseUpdateRefs || Session?.Snapshot.SupportsRebaseUpdateRefs == true;
                box.IsEnabled = !_busy && !_closed && scope?.CanEdit(field) == true;
                box.IsChecked = GitConfigurationFields.AdvancedBooleanState(setting?.Value);
                if (setting is null) { continue; }
                List<string> provenance = [GitConfigurationFields.Key(field)];
                if (setting.Published.Restriction is { } restriction) { provenance.Add(restriction); }
                if (setting.Published.Occurrences.Count == 0) { provenance.Add(_translations.Translate("Not set")); }
                foreach (GitToolConfigurationOccurrence entry in setting.Published.Occurrences)
                { provenance.Add(entry.Scope + " — " + entry.Origin + ": " + (entry.Value is null ? "(valueless)" : "\"" + entry.Value + "\"")); }
                if (setting.IsDirty) { provenance.Add(_translations.Translate("Unsaved value") + ": " + (setting.Value ?? _translations.Translate("Not set"))); }
                ToolTip.SetTip(box, string.Join("\n", provenance));
                if (!setting.IsDirty && setting.IsPresent && GitConfigurationFields.AdvancedBooleanState(setting.Value) is null)
                { notices.Add(GitConfigurationFields.Key(field) + ": " + _translations.Translate("The raw value is preserved until you choose true, false or unset.")); }
            }
            if (Session is not null && !Session.Snapshot.SupportsRebaseUpdateRefs)
            { notices.Add(_translations.Translate("Rebase updateRefs requires Git 2.38 or newer.")); }
            _notice.Text = string.Join("\n", notices);
        }
        finally { _loading = false; }
    }

    private string ScopeText(string id, string source) => _translations.Translate("SettingsPageHeader", id, "Text", source);
}

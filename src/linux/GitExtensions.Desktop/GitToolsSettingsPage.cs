using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal sealed class GitToolsSettingsPage : UserControl
{
    private sealed record ScopeChoice(GitToolConfigurationView View, string Label);
    private sealed record RoleControls(ComboBox Name, TextBox ReadOnlyName, TextBox Path, TextBox Command, Button Browse, Button Suggest);
    private sealed record SettingControls(Control Input, TextBlock Caption, TextBox? ReadOnlyInput = null);
    internal sealed record EncodingChoice(string? Value, string Label, bool RestorePublished = false);
    internal sealed record PickerStamp(GitToolConfigurationView View, GitToolRole Role, string Name, string Path, long Revision);
    internal sealed record SettingPickerStamp(GitToolConfigurationView View, GitConfigurationField Field, string Path, long Revision);
    private readonly TranslationCatalog _translations;
    private readonly ComboBox _scope = new() { Name = "GitToolsScope", MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<GitToolRole, RoleControls> _roles = [];
    private readonly Dictionary<GitConfigurationField, SettingControls> _settings = [];
    private readonly Dictionary<string, RadioButton> _lineEndings = [];
    private readonly TextBlock _lineEndingsNotice = new() { Name = "GitAutoCrlfNotice", TextWrapping = TextWrapping.Wrap };
    private readonly Button _templateBrowse, _configureEncoding;
    private readonly ComboBox _encoding = new() { Name = "Global_FilesEncoding", HorizontalAlignment = HorizontalAlignment.Stretch };
    private IReadOnlyList<GitFileEncodingChoice> _encodingChoices = [];
    private string? _encodingError;
    private readonly TextBlock _scopeNotice = new() { Name = "GitToolsScopeNotice", TextWrapping = TextWrapping.Wrap };
    private readonly ScopeChoice[] _choices;
    private bool _loading, _busy, _closed;
    private long _revision;
    private GitToolRole? _activeRole;
    private GitConfigurationField? _activeField;
    internal GitToolConfigurationView View { get; private set; } = GitToolConfigurationView.Effective;
    internal GitToolsDraftSession? Session { get; private set; }
    internal event Action? DraftChanged;
    internal Func<GitToolRole, Task>? BrowseRequested { get; set; }
    internal Func<Task>? TemplateBrowseRequested { get; set; }
    internal Func<Task>? ConfigureEncodingRequested { get; set; }

    internal GitToolsSettingsPage(TranslationCatalog translations)
    {
        _translations = translations; Name = "GitToolsSettingsPage";
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
            View = next.View; ++_revision; Refresh(); DraftChanged?.Invoke();
        };
        Grid identity = Rows(13); identity.Name = "tableLayoutPanelGitConfig";
        AddTextSetting(identity, 0, GitConfigurationField.UserName, "GlobalUserName", "label3", "User name");
        AddTextSetting(identity, 1, GitConfigurationField.UserEmail, "GlobalUserEmail", "label4", "User email");
        AddComboSetting(identity, 2, GitConfigurationField.CredentialHelper, "cbxCredentialHelper", "lblCredentialHelper", "Credential helper", ["oauth", "store", "cache"]);
        AddComboSetting(identity, 3, GitConfigurationField.Editor, "GlobalEditor", "label6", "Editor", EditorChoices());
        BuildRole(GitToolRole.Merge, identity, 4); BuildRole(GitToolRole.Diff, identity, 7);
        AddTextSetting(identity, 10, GitConfigurationField.CommitTemplate, "txtCommitTemplatePath", "lblCommitTemplatePath", "Path to commit template");
        _templateBrowse = new() { Name = "btnCommitTemplateBrowse", Content = Text("btnCommitTemplateBrowse", "Browse"), MinWidth = 75 };
        _templateBrowse.Click += async (_, _) => { if (CanAct(GitConfigurationField.CommitTemplate) && TemplateBrowseRequested is { } browse) { await browse(); } };
        _templateBrowse.GotFocus += (_, _) => Activate(GitConfigurationField.CommitTemplate);
        Put(identity, 10, 2, _templateBrowse);
        StackPanel endings = new() { Name = "flowLayoutPanelLineEndings", Spacing = 4 };
        AddLineEnding(endings, "true", "globalAutoCrlfTrue", "Checkout Windows-style, commit Unix-style line endings (\"core.autocrlf\"  is set to \"true\")");
        AddLineEnding(endings, "input", "globalAutoCrlfInput", "Checkout as-is, commit Unix-style line endings (\"core.autocrlf\"  is set to \"input\")");
        AddLineEnding(endings, "false", "globalAutoCrlfFalse", "Checkout as-is, commit as-is (\"core.autocrlf\"  is set to \"false\")");
        AddLineEnding(endings, "", "globalAutoCrlfNotSet", "Not set");
        endings.Children.Add(_lineEndingsNotice);
        Grid endingsGroup = ClassicChrome.Group("groupBoxLineEndings", Text("groupBoxLineEndings", "Line endings"), endings);
        Grid.SetColumnSpan(endingsGroup, 3); Put(identity, 11, 0, endingsGroup);
        TextBlock encodingCaption = Caption("label60", "Files content encoding");
        _settings.Add(GitConfigurationField.FilesEncoding, new(_encoding, encodingCaption));
        AddSettingMenu(_encoding, GitConfigurationField.FilesEncoding);
        _encoding.ItemTemplate = new FuncDataTemplate<EncodingChoice>((choice, _) => new TextBlock { Text = choice?.Label });
        _encoding.SelectionChanged += (_, _) =>
        {
            if (!CanAct(GitConfigurationField.FilesEncoding) || _encoding.SelectedItem is not EncodingChoice choice) { return; }
            GitToolsDraftSession.SettingDraft setting = Session!.Scope(View).Setting(GitConfigurationField.FilesEncoding);
            if (choice.RestorePublished) { setting.RestorePublished(); } else { setting.SetValue(choice.Value); }
            ++_revision; Changed();
        };
        _encoding.GotFocus += (_, _) => Activate(GitConfigurationField.FilesEncoding);
        _configureEncoding = new() { Name = "ConfigureEncoding", Content = Text("ConfigureEncoding", "Configure"), MinWidth = 75 };
        _configureEncoding.Click += async (_, _) => { if (!_busy && !_closed && ConfigureEncodingRequested is { } configure) { await configure(); } };
        Put(identity, 12, 0, encodingCaption); Put(identity, 12, 1, _encoding); Put(identity, 12, 2, _configureEncoding);
        StackPanel configuration = new() { Spacing = 8, Children =
        {
            _scope, _scopeNotice, identity
        } };
        Content = configuration;
        Refresh();
    }

    private void AddTextSetting(Grid rows, int row, GitConfigurationField field, string name, string label, string source)
    {
        TextBox input = new() { Name = name };
        TextBlock caption = Caption(label, source); _settings.Add(field, new(input, caption));
        AddSettingMenu(input, field);
        input.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty || !CanAct(field)) { return; }
            SetText(field, input.Text ?? "");
        };
        input.GotFocus += (_, _) => Activate(field);
        Put(rows, row, 0, caption); Put(rows, row, 1, input);
    }

    private void AddComboSetting(Grid rows, int row, GitConfigurationField field, string name, string label, string source, IEnumerable<string> choices)
    {
        ComboBox input = new() { Name = name, IsEditable = true, IsTextSearchEnabled = false, ItemsSource = choices.ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
        TextBox readOnly = new() { Name = "ReadOnly" + name, IsReadOnly = true };
        TextBlock caption = Caption(label, source); _settings.Add(field, new(input, caption, readOnly));
        AddSettingMenu(input, field); AddSettingMenu(readOnly, field);
        input.PropertyChanged += (_, change) =>
        {
            if (change.Property != ComboBox.TextProperty || !CanAct(field)) { return; }
            SetText(field, input.Text ?? "");
        };
        input.GotFocus += (_, _) => Activate(field); readOnly.GotFocus += (_, _) => Activate(field);
        Put(rows, row, 0, caption); Put(rows, row, 1, new Grid { Children = { input, readOnly } });
    }

    private void SetText(GitConfigurationField field, string text)
    {
        GitToolsDraftSession.SettingDraft setting = Session!.Scope(View).Setting(field);
        if (setting.DisplayValue == text) { return; }
        setting.SetValue(text.Length == 0 ? null : text); ++_revision; Changed();
    }

    private void AddLineEnding(StackPanel panel, string value, string name, string source)
    {
        RadioButton radio = new()
        {
            Name = name, GroupName = "GitAutoCrlf", Content = new TextBlock { Text = Text(name, source), TextWrapping = TextWrapping.Wrap },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _lineEndings.Add(value, radio); panel.Children.Add(radio);
        radio.IsCheckedChanged += (_, _) =>
        {
            if (radio.IsChecked != true || !CanAct(GitConfigurationField.AutoCrlf)) { return; }
            Session!.Scope(View).Setting(GitConfigurationField.AutoCrlf).SetValue(value.Length == 0 ? null : value);
            ++_revision; RefreshLineEndings(); Changed();
        };
        radio.GotFocus += (_, _) => Activate(GitConfigurationField.AutoCrlf);
    }

    private void Activate(GitConfigurationField field)
    { if (_activeField != field || _activeRole is not null) { _activeField = field; _activeRole = null; ++_revision; } }

    private void AddSettingMenu(Control input, GitConfigurationField field)
    {
        MenuItem unset = new() { Name = "Unset" + field, Header = _translations.Translate("Unset this value") };
        unset.Click += (_, _) => SetExplicitValue(field, null);
        List<MenuItem> items = [unset];
        if (field == GitConfigurationField.CredentialHelper)
        {
            MenuItem reset = new() { Name = "ResetCredentialHelpers", Header = _translations.Translate("Use empty value (reset helper list)") };
            reset.Click += (_, _) => SetExplicitValue(field, ""); items.Add(reset);
        }
        input.ContextMenu = new ContextMenu { ItemsSource = items };
        input.ContextMenu.Opened += (_, _) => { foreach (MenuItem item in items) { item.IsEnabled = CanAct(field); } };
    }

    private void SetExplicitValue(GitConfigurationField field, string? value)
    {
        if (!CanAct(field)) { return; }
        Session!.Scope(View).Setting(field).SetValue(value); ++_revision; Refresh(); Changed();
    }

    internal void SetEncodingChoices(IReadOnlyList<GitFileEncodingChoice> choices, string? error)
    { _encodingChoices = choices; _encodingError = error; Refresh(); }

    private void BuildRole(GitToolRole role, Grid rows, int firstRow)
    {
        string suffix = role == GitToolRole.Merge ? "Merge" : "Diff", noun = role == GitToolRole.Merge ? "Mergetool" : "Difftool";
        ComboBox name = new()
        {
            Name = "_NO_TRANSLATE_cbo" + suffix + "Tool", IsEditable = true, IsTextSearchEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        TextBox readOnlyName = new() { Name = "ReadOnly" + suffix + "Tool", IsReadOnly = true };
        TextBox path = new() { Name = "txt" + suffix + "ToolPath" }, command = new() { Name = "txt" + suffix + "ToolCommand" };
        Button browse = new() { Name = "btn" + suffix + "ToolBrowse", Content = Text("btn" + suffix + "ToolBrowse", "Browse"), MinWidth = 75 };
        Button suggest = new() { Name = "btn" + suffix + "ToolCommandSuggest", Content = Text("btn" + suffix + "ToolCommandSuggest", "Suggest"), MinWidth = 75 };
        RoleControls controls = new(name, readOnlyName, path, command, browse, suggest); _roles.Add(role, controls);
        name.PropertyChanged += (_, change) =>
        {
            if (change.Property != ComboBox.TextProperty || !CanAct(role)) { return; }
            GitToolsDraftSession.RoleDraft draft = Session!.Scope(View).Role(role);
            if (!draft.Selection.CanChange || name.Text == draft.Name) { return; }
            draft.Name = name.Text ?? ""; ++_revision; RefreshRole(role, preserveNameInput: true); Changed();
        };
        path.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty || !CanAct(role)) { return; }
            GitToolsDraftSession.Fields fields = Session!.Scope(View).Role(role).Current;
            if (!fields.CanChangePath || fields.Path == (path.Text ?? "")) { return; }
            fields.Path = path.Text ?? ""; ++_revision;
            if (path.IsFocused && fields.AutoSuggest && fields.CanChangeCommand) { Suggest(role, explicitChoice: false); }
            Changed();
        };
        command.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty || !CanAct(role)) { return; }
            GitToolsDraftSession.Fields fields = Session!.Scope(View).Role(role).Current;
            if (!fields.CanChangeCommand || fields.Command == (command.Text ?? "")) { return; }
            fields.SetCommand(command.Text ?? "", generated: false); ++_revision; Changed();
        };
        browse.Click += async (_, _) => { if (CanAct(role) && BrowseRequested is { } action) { await action(role); } };
        suggest.Click += (_, _) => { if (CanAct(role)) { Suggest(role, explicitChoice: true); } };
        foreach (Control control in new Control[] { name, readOnlyName, path, command, browse, suggest })
        { control.GotFocus += (_, _) => { if (_activeRole != role || _activeField is not null) { _activeRole = role; _activeField = null; ++_revision; } }; }
        Put(rows, firstRow, 0, Caption("lbl" + suffix + "Tool", noun));
        Put(rows, firstRow, 1, new Grid { Children = { name, readOnlyName } });
        Put(rows, firstRow + 1, 0, Caption("lbl" + suffix + "ToolPath", "Path to " + noun.ToLowerInvariant()));
        Put(rows, firstRow + 1, 1, path); Put(rows, firstRow + 1, 2, browse);
        Put(rows, firstRow + 2, 0, Caption("lbl" + suffix + "ToolCommand", noun + " command"));
        Put(rows, firstRow + 2, 1, command); Put(rows, firstRow + 2, 2, suggest);
    }

    internal void SetSession(GitToolsDraftSession session) { Session = session; ++_revision; Refresh(); }
    internal void SetBusy(bool busy) { _busy = busy; Refresh(); }
    internal void Invalidate() { _closed = true; ++_revision; }
    internal void SelectScope(GitToolConfigurationView view)
    { if (View != view) { View = view; ++_revision; Refresh(); } }
    internal void FocusRole(GitToolRole role)
    {
        RoleControls controls = _roles[role];
        (controls.Name.IsVisible ? (Control)controls.Name : controls.ReadOnlyName).Focus();
    }

    internal void FocusValidation(GitToolsDraftSession.ValidationTarget target)
    {
        SelectScope(target.View);
        Control? control = null;
        if (target.Field is GitConfigurationField.AutoCrlf)
        { control = _lineEndings.Values.FirstOrDefault(radio => radio.IsChecked == true) ?? _lineEndings[""]; }
        else if (target.Field is { } field)
        {
            SettingControls setting = _settings[field];
            control = setting.Input.IsVisible ? setting.Input : setting.ReadOnlyInput;
        }
        else if (target.Role is { } role)
        {
            RoleControls tools = _roles[role];
            control = target.ToolPart switch { "path" => tools.Path, "command" => tools.Command, _ => tools.Name.IsVisible ? tools.Name : tools.ReadOnlyName };
        }
        if (control is not null) { control.BringIntoView(); control.Focus(); }
    }

    internal SettingPickerStamp? CaptureTemplatePicker()
    {
        const GitConfigurationField field = GitConfigurationField.CommitTemplate;
        return CanAct(field) ? new(View, field, Session!.Scope(View).Setting(field).Value ?? "", _revision) : null;
    }

    internal bool AcceptTemplatePicker(SettingPickerStamp stamp, string path)
    {
        if (_closed || Session is null || View != stamp.View || _revision != stamp.Revision || !Session.Scope(View).CanEdit(stamp.Field)) { return false; }
        Session.Scope(View).Setting(stamp.Field).SetValue(path); ++_revision; RefreshSettings(); Changed(); return true;
    }

    internal PickerStamp? CapturePicker(GitToolRole role)
    {
        if (!CanAct(role)) { return null; }
        GitToolsDraftSession.RoleDraft draft = Session!.Scope(View).Role(role);
        if (!draft.Current.CanChangePath || string.IsNullOrWhiteSpace(draft.Name)) { return null; }
        return new(View, role, draft.Name, draft.Current.Path, _revision);
    }

    internal bool AcceptPicker(PickerStamp stamp, string path)
    {
        if (_closed || Session is null || View != stamp.View || _revision != stamp.Revision) { return false; }
        GitToolsDraftSession.RoleDraft draft = Session.Scope(View).Role(stamp.Role);
        if (!Session.Scope(View).CanEdit(stamp.Role) || draft.Name != stamp.Name || !draft.Current.CanChangePath) { return false; }
        draft.Current.Path = path; ++_revision;
        if (draft.Current.AutoSuggest && draft.Current.CanChangeCommand) { Suggest(stamp.Role, explicitChoice: false); }
        RefreshRole(stamp.Role); Changed(); return true;
    }

    private void Suggest(GitToolRole role, bool explicitChoice)
    {
        if (Session is null) { return; }
        GitToolsDraftSession.RoleDraft draft = Session.Scope(View).Role(role);
        if (!draft.Current.CanChangeCommand || (!explicitChoice && !draft.Current.AutoSuggest)) { return; }
        string? suggestion = GitToolsCatalog.Suggest(role, draft.Name, draft.Current.Path);
        if (suggestion is null) { return; }
        draft.Current.SetCommand(suggestion, generated: true); ++_revision; RefreshRole(role); Changed();
    }

    private bool CanAct(GitToolRole role) => !_loading && !_busy && !_closed && Session?.Scope(View).CanEdit(role) == true;
    private bool CanAct(GitConfigurationField field) => !_loading && !_busy && !_closed && Session?.Scope(View).CanEdit(field) == true;
    private void Changed() { UpdateScopeNotice(); DraftChanged?.Invoke(); }
    private void Refresh()
    {
        _loading = true;
        try
        {
            _scope.IsEnabled = Session is not null && !_busy && !_closed;
            _scope.SelectedItem = _choices.Single(choice => choice.View == View);
            RefreshSettings();
            foreach (GitToolRole role in Enum.GetValues<GitToolRole>()) { RefreshRole(role); }
            UpdateScopeNotice();
        }
        finally { _loading = false; }
    }

    private void RefreshSettings()
    {
        bool loading = _loading; _loading = true;
        try
        {
            GitToolsDraftSession.ScopeDraft? scope = Session?.Scope(View);
            foreach ((GitConfigurationField field, SettingControls controls) in _settings)
            {
                GitToolsDraftSession.SettingDraft? draft = scope?.Setting(field);
                bool editable = scope?.CanEdit(field) == true && !_busy && !_closed;
                bool visible = field != GitConfigurationField.CredentialHelper || View != GitToolConfigurationView.Effective;
                controls.Caption.IsVisible = visible;
                controls.Input.IsVisible = visible && (controls.ReadOnlyInput is null || editable);
                if (controls.ReadOnlyInput is { } readOnly)
                { readOnly.IsVisible = visible && !editable; readOnly.Text = draft?.DisplayValue ?? ""; ToolTip.SetTip(readOnly, SettingTip(draft)); }
                if (controls.Input is TextBox text)
                { text.Text = draft?.DisplayValue ?? ""; text.IsReadOnly = !editable; }
                else if (controls.Input is ComboBox combo && field != GitConfigurationField.FilesEncoding)
                { combo.SelectedItem = null; combo.Text = draft?.DisplayValue ?? ""; combo.IsEnabled = editable; }
                else if (field == GitConfigurationField.FilesEncoding)
                {
                    List<EncodingChoice> choices = [new(null, _translations.Translate("Not set"))];
                    choices.AddRange(_encodingChoices.Select(choice => new EncodingChoice(choice.Name, choice.DisplayName)));
                    EncodingChoice? valueless = draft?.Published.Occurrences.LastOrDefault() is { Value: null }
                        ? new(null, _translations.Translate("No value"), RestorePublished: true) : null;
                    if (valueless is not null) { choices.Add(valueless); }
                    EncodingChoice selected = choices[0];
                    if (draft?.IsPresent == true)
                    {
                        selected = draft.Value is null && valueless is not null ? valueless : choices.FirstOrDefault(choice => choice.Value == draft.Value && choice.Value is not null)
                            ?? new(draft.Value, draft.Value is null ? _translations.Translate("No value") : draft.Value.Length == 0 ? _translations.Translate("Empty value") : draft.Value);
                        if (!choices.Contains(selected)) { choices.Add(selected); }
                    }
                    _encoding.ItemsSource = choices; _encoding.SelectedItem = selected; _encoding.IsEnabled = editable;
                }
                ToolTip.SetTip(controls.Input, SettingTip(draft));
            }
            _templateBrowse.IsEnabled = scope?.CanEdit(GitConfigurationField.CommitTemplate) == true && !_busy && !_closed;
            _configureEncoding.IsEnabled = !_busy && !_closed;
            RefreshLineEndings();
        }
        finally { _loading = loading; }
    }

    private void RefreshLineEndings()
    {
        bool loading = _loading; _loading = true;
        try
        {
            GitToolsDraftSession.ScopeDraft? scope = Session?.Scope(View);
            GitToolsDraftSession.SettingDraft? draft = scope?.Setting(GitConfigurationField.AutoCrlf);
            string? value = draft?.IsPresent != true ? "" : draft.Value?.ToLowerInvariant() switch
            { null or "true" or "yes" or "on" or "1" => "true", "false" or "no" or "off" or "0" or "" => "false", "input" => "input", _ => null };
            foreach ((string key, RadioButton radio) in _lineEndings)
            {
                radio.IsChecked = key == value; radio.IsEnabled = scope?.CanEdit(GitConfigurationField.AutoCrlf) == true && !_busy && !_closed;
                ToolTip.SetTip(radio, SettingTip(draft));
            }
            _lineEndingsNotice.Text = value is null ? _translations.Translate("Unrecognized configured value") + ": " + draft?.Value : null;
            _lineEndingsNotice.IsVisible = value is null;
        }
        finally { _loading = loading; }
    }

    private string SettingTip(GitToolsDraftSession.SettingDraft? draft)
    {
        if (draft is null) { return ""; }
        IEnumerable<string> entries = draft.Published.Occurrences.Select(entry => entry.Origin + ": " + (entry.Value ?? _translations.Translate("No value")));
        string published = draft.Published.Occurrences.Count == 0 ? _translations.Translate("Not set") : string.Join("\n", entries);
        return string.Join("\n", new[] { draft.Published.Restriction, _translations.Translate("Published value") + ": " + published }.Where(value => !string.IsNullOrEmpty(value)));
    }

    private void RefreshRole(GitToolRole role, bool preserveNameInput = false)
    {
        bool loading = _loading; _loading = true;
        try
        {
            RoleControls controls = _roles[role];
            GitToolsDraftSession.ScopeDraft? scope = Session?.Scope(View);
            GitToolsDraftSession.RoleDraft? draft = scope?.Role(role);
            GitToolsDraftSession.Fields? fields = draft?.Current;
            bool editable = scope?.CanEdit(role) == true && !_busy && !_closed;
            bool nameEditable = editable && draft!.Selection.CanChange;
            controls.Name.IsVisible = nameEditable; controls.ReadOnlyName.IsVisible = !nameEditable;
            controls.Name.IsEnabled = nameEditable;
            if (!preserveNameInput)
            {
                controls.Name.ItemsSource = draft?.Names().ToArray() ?? [];
                controls.Name.SelectedItem = null; controls.Name.Text = draft?.Name ?? "";
            }
            controls.ReadOnlyName.Text = draft?.Name ?? "";
            controls.Path.Text = fields?.Path ?? ""; controls.Command.Text = fields?.Command ?? "";
            bool hasName = !string.IsNullOrWhiteSpace(draft?.Name);
            controls.Path.IsReadOnly = !editable || fields?.CanChangePath != true;
            controls.Command.IsReadOnly = !editable || fields?.CanChangeCommand != true;
            controls.Path.IsEnabled = controls.Command.IsEnabled = hasName;
            controls.Browse.IsEnabled = hasName && editable && fields?.CanChangePath == true;
            controls.Suggest.IsEnabled = hasName && editable && fields?.CanChangeCommand == true && GitToolsCatalog.IsKnown(role, draft!.Name);
            string? suggestion = draft is null ? null : GitToolsCatalog.Suggest(role, draft.Name, fields?.Path ?? "");
            controls.Command.Watermark = suggestion;
            ToolTip.SetTip(controls.Name, draft?.Selection.Restriction);
            ToolTip.SetTip(controls.ReadOnlyName, draft?.Selection.Restriction);
            ToolTip.SetTip(controls.Path, FieldTip(fields?.PublishedPath, fields?.PathRestriction));
            ToolTip.SetTip(controls.Command, FieldTip(fields?.PublishedCommand, fields?.CommandRestriction));
        }
        finally { _loading = loading; }
    }

    private void UpdateScopeNotice()
    {
        if (Session is null) { _scopeNotice.Text = _translations.Translate("Loading Git configuration…"); return; }
        GitToolsDraftSession.ScopeDraft scope = Session.Scope(View);
        List<string> notice = [];
        if (!scope.Snapshot.IsAvailable) { notice.Add(scope.Snapshot.Diagnostic ?? _translations.Translate("Git configuration is unavailable.")); }
        else
        {
            if (!scope.Snapshot.CanWrite || View is GitToolConfigurationView.Effective or GitToolConfigurationView.System)
            { notice.Add(_translations.Translate("Read only")); }
            if (!Session.Snapshot.SupportsGuiMergeTool)
            { notice.Add(_translations.Translate("This Git version does not support GUI merge tool configuration.")); }
            if (scope.Role(GitToolRole.Merge).UsesMergeFallback)
            { notice.Add(_translations.Translate("Mergetool is inherited from merge.tool; this fallback is not changed implicitly.")); }
            if (scope.Snapshot.Diagnostic is { Length: > 0 } diagnostic) { notice.Add(diagnostic); }
        }
        if (Session.IsDirty) { notice.Add(_translations.Translate("Local or global configuration has unsaved changes. Effective shows published values.")); }
        if (_encodingError is { Length: > 0 }) { notice.Add(_encodingError); }
        _scopeNotice.Text = string.Join("\n", notice);
    }

    private string FieldTip(string? published, string? restriction)
        => string.Join("\n", new[] { restriction, _translations.Translate("Published value") + ": " + (published ?? "") }.Where(text => !string.IsNullOrEmpty(text)));
    private TextBlock Caption(string id, string source) => new() { Name = id, Text = Text(id, source), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 190 };
    private string Text(string id, string source) => _translations.Translate("GitConfigSettingsPage", id, "Text", source);
    private string ScopeText(string id, string source) => _translations.Translate("SettingsPageHeader", id, "Text", source);
    private static void Put(Grid grid, int row, int column, Control control)
    { Grid.SetRow(control, row); Grid.SetColumn(control, column); grid.Children.Add(control); }
    private static Grid Rows(int count)
    {
        Grid rows = new() { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new(string.Join(",", Enumerable.Repeat("Auto", count))), ColumnSpacing = 6, RowSpacing = 6 };
        return rows;
    }
    private static IEnumerable<string> EditorChoices()
        => new[] { Environment.GetEnvironmentVariable("VISUAL"), Environment.GetEnvironmentVariable("EDITOR"), "code --wait", "gedit --wait", "kate --block", "emacs", "nano", "vim" }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.Ordinal);
}

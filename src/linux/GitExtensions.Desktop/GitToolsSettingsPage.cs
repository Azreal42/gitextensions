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
    internal sealed record PickerStamp(GitToolConfigurationView View, GitToolRole Role, string Name, string Path, long Revision);
    private readonly TranslationCatalog _translations;
    private readonly ComboBox _scope = new() { Name = "GitToolsScope", MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<GitToolRole, RoleControls> _roles = [];
    private readonly TextBlock _scopeNotice = new() { Name = "GitToolsScopeNotice", TextWrapping = TextWrapping.Wrap };
    private readonly ScopeChoice[] _choices;
    private bool _loading, _busy, _closed;
    private long _revision;
    private GitToolRole? _activeRole;
    internal GitToolConfigurationView View { get; private set; } = GitToolConfigurationView.Effective;
    internal GitToolsDraftSession? Session { get; private set; }
    internal event Action? DraftChanged;
    internal Func<GitToolRole, Task>? BrowseRequested { get; set; }

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
        Content = new StackPanel { Spacing = 8, Children = { _scope, _scopeNotice, BuildRole(GitToolRole.Merge), BuildRole(GitToolRole.Diff) } };
        Refresh();
    }

    private Control BuildRole(GitToolRole role)
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
        { control.GotFocus += (_, _) => { if (_activeRole != role) { _activeRole = role; ++_revision; } }; }
        Grid rows = new() { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 6, RowSpacing = 6 };
        Put(rows, 0, 0, Caption("lbl" + suffix + "Tool", noun));
        Put(rows, 0, 1, new Grid { Children = { name, readOnlyName } });
        Put(rows, 1, 0, Caption("lbl" + suffix + "ToolPath", "Path to " + noun.ToLowerInvariant()));
        Put(rows, 1, 1, path); Put(rows, 1, 2, browse);
        Put(rows, 2, 0, Caption("lbl" + suffix + "ToolCommand", noun + " command"));
        Put(rows, 2, 1, command); Put(rows, 2, 2, suggest);
        return ClassicChrome.Group("Config" + suffix + "Tool", Text("lbl" + suffix + "Tool", noun), rows);
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
    private void Changed() { UpdateScopeNotice(); DraftChanged?.Invoke(); }
    private void Refresh()
    {
        _loading = true;
        try
        {
            _scope.IsEnabled = Session is not null && !_busy && !_closed;
            _scope.SelectedItem = _choices.Single(choice => choice.View == View);
            foreach (GitToolRole role in Enum.GetValues<GitToolRole>()) { RefreshRole(role); }
            UpdateScopeNotice();
        }
        finally { _loading = false; }
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
        _scopeNotice.Text = string.Join("\n", notice);
    }

    private string FieldTip(string? published, string? restriction)
        => string.Join("\n", new[] { restriction, _translations.Translate("Published value") + ": " + (published ?? "") }.Where(text => !string.IsNullOrEmpty(text)));
    private TextBlock Caption(string id, string source) => new() { Text = Text(id, source), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 190 };
    private string Text(string id, string source) => _translations.Translate("GitConfigSettingsPage", id, "Text", source);
    private string ScopeText(string id, string source) => _translations.Translate("SettingsPageHeader", id, "Text", source);
    private static void Put(Grid grid, int row, int column, Control control)
    { Grid.SetRow(control, row); Grid.SetColumn(control, column); grid.Children.Add(control); }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitCommands.ExternalLinks;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original categories, remote/revision patterns and Caption/URI table, with explicit scoped drafts.</summary>
internal sealed class RevisionLinksSettingsPage : UserControl
{
    private readonly TranslationCatalog _translations;
    private readonly Func<CancellationToken, Task<IReadOnlyList<GitRevisionLinkRemote>>> _readTemplateRemotes;
    private readonly Dictionary<GitDistributedSettingsScope, RadioButton> _scopes = [];
    private readonly ListBox _categories = new() { Name = "_NO_TRANSLATE_Categories", MinHeight = 150 };
    private readonly TextBox _name, _useRemotes, _remotePattern, _search, _nested;
    private readonly CheckBox _enabled, _first, _url, _pushUrl, _message, _local, _remote;
    private readonly ListBox _formats = new() { Name = "LinksGrid", MinHeight = 100, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _add = new() { Name = "Add", MinHeight = 24 }, _remove = new() { Name = "Remove", MinHeight = 24 };
    private readonly Button _addArrow = new() { Name = "AddTemplateDropDown", Content = "▾", MinWidth = 22, MinHeight = 24, Padding = new(2, 0) };
    private readonly TextBlock _notice = new() { Name = "RevisionLinksNotice", TextWrapping = TextWrapping.Wrap };
    private readonly Control _editor;
    private readonly HashSet<string> _dirty = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<ExternalLinkDefinition> _definitions = [];
    private GitDistributedSettingsScope _scope;
    private ExternalLinkDefinition? _selected;
    private bool _loading, _changed, _closing;
    private long _draftRevision;
    private Task _pending = Task.CompletedTask;
    private Task? _closeCompletion;
    internal RevisionLinksSettingsSession Session { get; }
    internal bool IsBusy { get; private set; }
    internal event Action? BusyChanged;
    internal Task Completion => _pending;

    internal RevisionLinksSettingsPage(RevisionLinksSettingsSession session, TranslationCatalog translations,
        Func<CancellationToken, Task<IReadOnlyList<GitRevisionLinkRemote>>>? readTemplateRemotes = null)
    {
        Session = session; _translations = translations;
        _readTemplateRemotes = readTemplateRemotes ?? (token => Session.Repository is null
            ? Task.FromResult<IReadOnlyList<GitRevisionLinkRemote>>([]) : Session.Repository.GetRevisionLinkRemotesAsync(token));
        _scope = session.InitialScope; Name = "RevisionLinksSettingsPage";
        WrapPanel header = new() { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 7) };
        foreach ((GitDistributedSettingsScope scope, string item, string caption) in new[]
        {
            (GitDistributedSettingsScope.Effective, "EffectiveRB", "Effective"),
            (GitDistributedSettingsScope.Local, "LocalRB", "Local for current repository"),
            (GitDistributedSettingsScope.Distributed, "DistributedRB", "Distributed with current repository"),
            (GitDistributedSettingsScope.Global, "GlobalRB", "Global for all repositories")
        })
        {
            RadioButton choice = new() { Name = "RevisionLinks" + item, GroupName = "RevisionLinksScope", Margin = new(0, 0, 10, 0),
                Content = translations.Translate("SettingsPageHeader", item, "Text", caption) };
            ReferenceDialogControls.Compact(choice); header.Children.Add(choice); _scopes.Add(scope, choice);
            choice.IsCheckedChanged += (_, _) =>
            { if (!_loading && choice.IsChecked == true && scope != _scope) { Flush(); _draftRevision++; _scope = scope; LoadScope(); } };
        }
        _name = Edit("_NO_TRANSLATE_Name"); _useRemotes = Edit("_NO_TRANSLATE_UseRemotes");
        _remotePattern = Edit("_NO_TRANSLATE_RemotePatern"); _search = Edit("_NO_TRANSLATE_SearchPatternEdit"); _nested = Edit("_NO_TRANSLATE_NestedPatternEdit");
        _enabled = Check("EnabledChx", "Enabled"); _first = Check("chkOnlyFirstRemote", "Only use the first match");
        _url = Check("chxURL", "URL"); _pushUrl = Check("chxPushURL", "Push URL");
        _message = Check("MessageChx", "Message"); _local = Check("LocalBranchChx", "Local branch name"); _remote = Check("RemoteBranchChx", "Remote branch name");
        Grid nameRow = new() { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 7 };
        nameRow.Children.Add(Label("label1", "Name")); Grid.SetColumn(_name, 1); nameRow.Children.Add(_name);
        Grid.SetColumn(_enabled, 2); nameRow.Children.Add(_enabled);
        Grid remoteData = new() { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 7, RowSpacing = 4 };
        Grid useGrid = new() { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 7 };
        useGrid.Children.Add(_useRemotes); Grid.SetColumn(_first, 1); useGrid.Children.Add(_first);
        Row(remoteData, 0, Label("lblUseRemotes", "Use remotes"), useGrid);
        Row(remoteData, 1, Label("lblRemoteSearchIn", "Search in"), new WrapPanel { Orientation = Orientation.Horizontal, Children = { _url, _pushUrl } });
        Row(remoteData, 2, Label("lblSearchRemotePattern", "Search pattern"), _remotePattern);
        Grid revisionData = new() { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 7, RowSpacing = 4 };
        Row(revisionData, 0, Label("label5", "Search in"), new WrapPanel { Orientation = Orientation.Horizontal, Children = { _message, _local, _remote } });
        Row(revisionData, 1, Label("label2", "Search pattern"), _search);
        Row(revisionData, 2, Label("nestedPatternLab", "Nested pattern"), _nested);
        Grid formatHeader = new() { ColumnDefinitions = new("*,2*"), ColumnSpacing = 1 };
        TextBlock formatCaption = Label("CaptionCol", "Caption"), uri = Label("URICol", "URI");
        formatHeader.Children.Add(formatCaption); Grid.SetColumn(uri, 1); formatHeader.Children.Add(uri);
        _formats.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Delete && args.Source is not TextBox && _selected is not null
                && _formats.SelectedItem is Grid row && row.Tag is ExternalLinkFormat format && Editable)
            { _selected.LinkFormats.Remove(format); Changed(); LoadFormats(); args.Handled = true; }
        }, RoutingStrategies.Bubble);
        _editor = new StackPanel { Spacing = 7, Children =
        {
            nameRow, ClassicChrome.Group("remoteGrp", T("remoteGrp", "Remote data"), remoteData),
            ClassicChrome.Group("revisionDataGrp", T("revisionDataGrp", "Revision data"), revisionData),
            Label("label6", "Links"), formatHeader, _formats
        } };
        // The original category ToolStrip measures its translated buttons. Keep the
        // compact 170px minimum, but allow the list column to fit the complete footer.
        Grid columns = new() { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        _add.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { OriginalMenuItems.Icon("FileStatusAdded.png"), new TextBlock { Text = T("Add", "Add") } } };
        _remove.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { OriginalMenuItems.Icon("FileStatusRemoved.png"), new TextBlock { Text = T("Remove", "Remove") } } };
        _add.Padding = _remove.Padding = new(3, 0);
        _add.Click += (_, _) => { if (Editable) { AddDefinitions([GitRevisionLinkTemplates.CreateManual()]); } };
        _remove.Click += (_, _) =>
        {
            if (!Editable || _selected is null) { return; }
            int index = _definitions.IndexOf(_selected); _definitions.RemoveAt(index); _selected = null; _changed = true; Flush();
            ReloadCategories(Math.Min(index, _definitions.Count - 1));
        };
        MenuItem manual = new() { Name = "AddManualRevisionLink", Header = T("Add", "Add") };
        manual.Click += (_, _) => { if (Editable) { AddDefinitions([GitRevisionLinkTemplates.CreateManual()]); } };
        ContextMenu menu = new() { Name = "RevisionLinksAddMenu" }; menu.Items.Add(manual); menu.Items.Add(new Separator());
        foreach ((GitRevisionLinkTemplate template, string service) in new[] { (GitRevisionLinkTemplate.GitHub, "GitHub"), (GitRevisionLinkTemplate.AzureDevOps, "Azure DevOps") })
        {
            MenuItem item = new() { Name = "Add" + template + "Templates", Header = string.Format(translations.Translate("Add {0} templates"), service),
                Icon = OriginalMenuItems.Icon(template == GitRevisionLinkTemplate.GitHub ? "github.png" : "VisualStudioTeamServices.png") };
            item.Click += (_, _) => { if (Editable) { _pending = AddTemplateAsync(template); } }; menu.Items.Add(item);
        }
        _addArrow.ContextMenu = menu; _addArrow.Click += (_, _) => { if (Editable) { menu.Open(_addArrow); } };
        Grid addRow = new() { ColumnDefinitions = new("*,Auto"), Margin = new(0, 4, 0, 0) };
        addRow.Children.Add(_add); Grid.SetColumn(_addArrow, 1); addRow.Children.Add(_addArrow);
        Grid categories = new() { RowDefinitions = new("Auto,*,Auto"), MinWidth = 170, MaxWidth = 280 };
        categories.Children.Add(Label("CategoriesLabel", "Categories"));
        Grid.SetRow(_categories, 1); categories.Children.Add(_categories);
        Grid manage = new() { ColumnDefinitions = new("Auto,Auto"), ColumnSpacing = 3, HorizontalAlignment = HorizontalAlignment.Left };
        manage.Children.Add(addRow); Grid.SetColumn(_remove, 1); manage.Children.Add(_remove); _remove.Margin = new(0, 4, 0, 0);
        Grid.SetRow(manage, 2); categories.Children.Add(manage);
        columns.Children.Add(categories); Grid.SetColumn(_editor, 1); columns.Children.Add(_editor);
        _categories.SelectionChanged += (_, _) =>
        { if (!_loading) { Flush(); _selected = (_categories.SelectedItem as ListBoxItem)?.Tag as ExternalLinkDefinition; LoadSelected(); } };
        Grid body = new() { RowDefinitions = new("Auto,*,Auto") };
        body.Children.Add(header); Grid.SetRow(columns, 1); body.Children.Add(columns); Grid.SetRow(_notice, 2); body.Children.Add(_notice);
        Content = body; LoadScope();
    }
    private bool Editable => !_loading && !_closing && !IsBusy && Session.Loaded && _scope != GitDistributedSettingsScope.Effective;
    private string T(string item, string source) => _translations.Translate("RevisionLinksSettingsPage", item, "Text", source);
    private TextBlock Label(string name, string text) => new() { Name = name, Text = T(name, text), VerticalAlignment = VerticalAlignment.Center };
    private static void Row(Grid grid, int row, Control label, Control editor)
    { Grid.SetRow(label, row); Grid.SetRow(editor, row); Grid.SetColumn(editor, 1); grid.Children.Add(label); grid.Children.Add(editor); }
    private TextBox Edit(string name)
    {
        TextBox box = new() { Name = name, MinHeight = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.PropertyChanged += (_, args) => { if (!_loading && args.Property == TextBox.TextProperty && Editable) { _dirty.Add(name); _changed = true; _draftRevision++; } };
        box.LostFocus += (_, _) => { if (!_loading) { Flush(); } }; return box;
    }
    private CheckBox Check(string name, string source)
    {
        CheckBox box = new() { Name = name, Content = T(name, source), Margin = new(0, 0, 9, 0) };
        ReferenceDialogControls.Compact(box); box.IsCheckedChanged += (_, _) =>
        { if (!_loading && Editable) { _dirty.Add(name); _changed = true; _draftRevision++; Flush(); } }; return box;
    }
    private void Changed() { _changed = true; _draftRevision++; }
    private void Flush()
    {
        if (_loading || !Session.Loaded || _scope == GitDistributedSettingsScope.Effective) { return; }
        if (_selected is { } selected)
        {
            if (_dirty.Contains(_name.Name!)) { selected.Name = _name.Text ?? ""; }
            if (_dirty.Contains(_useRemotes.Name!)) { selected.UseRemotesPattern = _useRemotes.Text?.Trim(); }
            if (_dirty.Contains(_remotePattern.Name!)) { selected.RemoteSearchPattern = _remotePattern.Text?.Trim(); }
            if (_dirty.Contains(_search.Name!)) { selected.SearchPattern = _search.Text?.Trim(); }
            if (_dirty.Contains(_nested.Name!)) { selected.NestedSearchPattern = _nested.Text?.Trim(); }
            if (_dirty.Contains(_enabled.Name!)) { selected.Enabled = _enabled.IsChecked == true; }
            if (_dirty.Contains(_first.Name!)) { selected.UseOnlyFirstRemote = _first.IsChecked == true; }
            void Part<TPart>(HashSet<TPart> parts, CheckBox box, TPart part) where TPart : notnull
            { if (_dirty.Contains(box.Name!)) { if (box.IsChecked == true) { parts.Add(part); } else { parts.Remove(part); } } }
            Part(selected.RemoteSearchInParts, _url, ExternalLinkDefinition.RemotePart.URL); Part(selected.RemoteSearchInParts, _pushUrl, ExternalLinkDefinition.RemotePart.PushURL);
            Part(selected.SearchInParts, _message, ExternalLinkDefinition.RevisionPart.Message); Part(selected.SearchInParts, _local, ExternalLinkDefinition.RevisionPart.LocalBranches);
            Part(selected.SearchInParts, _remote, ExternalLinkDefinition.RevisionPart.RemoteBranches);
            if (_categories.SelectedItem is ListBoxItem item) { item.Content = selected.Name ?? ""; }
        }
        _dirty.Clear();
        if (_changed) { Session.Set(_scope, _definitions); _changed = false; }
    }
    private void LoadScope()
    {
        _loading = true;
        try
        {
            foreach ((GitDistributedSettingsScope scope, RadioButton choice) in _scopes)
            {
                choice.IsVisible = scope == GitDistributedSettingsScope.Global || Session.HasRepository;
                choice.IsEnabled = !IsBusy && (scope != GitDistributedSettingsScope.Distributed || Session.HasDistributed);
                choice.IsChecked = scope == _scope; ToolTip.SetTip(choice, Session.ScopePath(scope));
            }
            _definitions.Clear(); _definitions.AddRange(Session.Read(_scope)); _selected = null; _dirty.Clear(); _changed = false;
        }
        finally { _loading = false; }
        ReloadCategories(_definitions.Count == 0 ? -1 : 0);
        _notice.Text = Session.LoadError?.Message ?? string.Join("\n", Session.Diagnostics(_scope).Select(d => d.Message));
        if (string.IsNullOrEmpty(_notice.Text) && _scope == GitDistributedSettingsScope.Effective)
        { _notice.Text = _translations.Translate("Effective settings are read-only. Select Local, Distributed or Global to edit."); }
        UpdateEnabled();
    }
    private void ReloadCategories(int index)
    {
        _loading = true;
        try
        {
            _categories.ItemsSource = _definitions.Select(definition => new ListBoxItem { Content = definition.Name ?? "", Tag = definition, Padding = new(3, 1), MinHeight = 20 }).ToArray();
            _categories.SelectedIndex = index; _selected = index >= 0 && index < _definitions.Count ? _definitions[index] : null;
        }
        finally { _loading = false; }
        LoadSelected();
    }
    private void LoadSelected()
    {
        _loading = true;
        try
        {
            _dirty.Clear(); ExternalLinkDefinition? selected = _selected;
            _name.Text = selected?.Name ?? ""; _useRemotes.Text = selected?.UseRemotesPattern ?? ""; _remotePattern.Text = selected?.RemoteSearchPattern ?? "";
            _search.Text = selected?.SearchPattern ?? ""; _nested.Text = selected?.NestedSearchPattern ?? "";
            _enabled.IsChecked = selected?.Enabled == true; _first.IsChecked = selected?.UseOnlyFirstRemote == true;
            _url.IsChecked = selected?.RemoteSearchInParts.Contains(ExternalLinkDefinition.RemotePart.URL) == true;
            _pushUrl.IsChecked = selected?.RemoteSearchInParts.Contains(ExternalLinkDefinition.RemotePart.PushURL) == true;
            _message.IsChecked = selected?.SearchInParts.Contains(ExternalLinkDefinition.RevisionPart.Message) == true;
            _local.IsChecked = selected?.SearchInParts.Contains(ExternalLinkDefinition.RevisionPart.LocalBranches) == true;
            _remote.IsChecked = selected?.SearchInParts.Contains(ExternalLinkDefinition.RevisionPart.RemoteBranches) == true;
            LoadFormats();
        }
        finally { _loading = false; }
        UpdateEnabled();
    }
    private void LoadFormats()
    {
        _formats.Items.Clear();
        if (_selected is null) { return; }
        foreach (ExternalLinkFormat format in _selected.LinkFormats) { FormatRow(format); }
        if (_scope != GitDistributedSettingsScope.Effective) { FormatRow(null); }
    }
    private void FormatRow(ExternalLinkFormat? model)
    {
        Grid row = new() { ColumnDefinitions = new("*,2*"), ColumnSpacing = 1, Tag = model };
        TextBox caption = new() { Name = "RevisionLinkCaption", Text = model?.Caption ?? "", MinHeight = 24, Padding = new(3, 1) };
        TextBox uri = new() { Name = "RevisionLinkUri", Text = model?.Format ?? "", MinHeight = 24, Padding = new(3, 1) };
        row.Children.Add(caption); Grid.SetColumn(uri, 1); row.Children.Add(uri);
        void EditFormat(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property != TextBox.TextProperty || !Editable || _selected is null) { return; }
            if (model is null)
            {
                if (string.IsNullOrEmpty(caption.Text) && string.IsNullOrEmpty(uri.Text)) { return; }
                model = new(); row.Tag = model; _selected.LinkFormats.Add(model); FormatRow(null);
            }
            model.Caption = caption.Text ?? ""; model.Format = uri.Text ?? ""; Changed();
        }
        caption.PropertyChanged += EditFormat; uri.PropertyChanged += EditFormat;
        _formats.Items.Add(row);
    }
    private void UpdateEnabled()
    {
        bool editable = Session.Loaded && _scope != GitDistributedSettingsScope.Effective && !IsBusy && !_closing;
        _editor.IsEnabled = editable && _selected is not null; _add.IsEnabled = _addArrow.IsEnabled = editable;
        _remove.IsEnabled = editable && _selected is not null;
        foreach ((GitDistributedSettingsScope scope, RadioButton choice) in _scopes)
        { choice.IsEnabled = !IsBusy && !_closing && (scope != GitDistributedSettingsScope.Distributed || Session.HasDistributed); }
    }
    private void AddDefinitions(IReadOnlyList<ExternalLinkDefinition> definitions)
    {
        Flush(); int first = _definitions.Count; _definitions.AddRange(definitions.Select(GitRevisionLinkCodec.Clone)); Changed(); Flush(); ReloadCategories(first);
    }
    private async Task AddTemplateAsync(GitRevisionLinkTemplate template)
    {
        Flush(); GitDistributedSettingsScope scope = _scope; long revision = _draftRevision;
        IsBusy = true; UpdateEnabled(); BusyChanged?.Invoke();
        try
        {
            IReadOnlyList<GitRevisionLinkRemote> remotes = await _readTemplateRemotes(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!_closing && scope == _scope && revision == _draftRevision)
            { AddDefinitions(GitRevisionLinkTemplates.Create(template, remotes, source => _translations.Translate(source))); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or GitCommandException or InvalidOperationException or UnauthorizedAccessException)
        { if (!_closing) { _notice.Text = error.Message; } }
        finally { IsBusy = false; UpdateEnabled(); BusyChanged?.Invoke(); }
    }
    internal void PrepareSave() => Flush();
    internal async Task PrepareSaveAsync(CancellationToken token)
    { await Completion.WaitAsync(token); token.ThrowIfCancellationRequested(); Flush(); }
    internal void AcceptSaved() => LoadScope();
    internal void ShowLoadState() => LoadScope();
    internal Task BeginClose()
    {
        if (_closeCompletion is not null) { return _closeCompletion; }
        _closing = true; _lifetime.Cancel(); UpdateEnabled(); return _closeCompletion = CloseCoreAsync();
    }
    private async Task CloseCoreAsync()
    { try { await Completion; } finally { _lifetime.Dispose(); } }
}

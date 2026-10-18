using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original two-list chooser. Only an accepted child publishes the independent profile preference.</summary>
internal sealed class AvailableEncodingsDialog : Window
{
    private readonly AvailableEncodingsPreferencesStore _store;
    private readonly AvailableEncodingsCatalog _catalog;
    private readonly AvailableEncodingsPreferencesLoadResult _baseline;
    private readonly List<string> _draft;
    private readonly ListBox _included = new() { Name = "ListIncludedEncodings", SelectionMode = SelectionMode.Single };
    private readonly ListBox _available = new() { Name = "ListAvailableEncodings", SelectionMode = SelectionMode.Single };
    private readonly Button _add, _remove, _accept;
    private readonly TextBlock _notice = new() { Name = "AvailableEncodingsError", TextWrapping = TextWrapping.Wrap, IsVisible = false, MaxHeight = 100 };
    private bool _closed;
    internal AvailableEncodingsPreferencesLoadResult Result { get; private set; }
    internal string? LastError { get; private set; }

    internal AvailableEncodingsDialog(AvailableEncodingsPreferencesStore? store = null, AvailableEncodingsCatalog? catalog = null,
        TranslationCatalog? translations = null)
    {
        _catalog = store?.Catalog ?? catalog ?? new(); _store = store ?? new(catalog: _catalog);
        _baseline = _store.Load(); Result = _baseline; _draft = _baseline.Names.ToList();
        TranslationCatalog text = translations ?? TranslationCatalog.LoadDefault();
        string T(string control, string source) => text.Translate("FormAvailableEncodings", control, "Text", source);
        Name = "FormAvailableEncodings"; Title = T("$this", "Configure available encodings");
        Width = 605; Height = 315; MinWidth = 500; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _included.ItemTemplate = ChoiceTemplate(); _available.ItemTemplate = ChoiceTemplate();
        _remove = new() { Name = "ToRight", Content = ">", MinWidth = 28, Padding = new Thickness(2), IsEnabled = false };
        _add = new() { Name = "ToLeft", Content = "<", MinWidth = 28, Padding = new Thickness(2), IsEnabled = false };
        ToolTip.SetTip(_remove, text.Translate("Remove")); ToolTip.SetTip(_add, text.Translate("Add"));
        _included.SelectionChanged += (_, _) => UpdateActions(); _available.SelectionChanged += (_, _) => UpdateActions();
        _add.Click += (_, _) =>
        {
            if (_baseline.Error is not null || _available.SelectedItem is not GitFileEncodingChoice choice) { return; }
            _draft.Add(choice.Name); RefreshLists(included: choice.Name);
        };
        _remove.Click += (_, _) =>
        {
            if (_baseline.Error is not null || _included.SelectedItem is not GitFileEncodingChoice choice || _catalog.IsProtected(choice.Name)) { return; }
            _draft.Remove(choice.Name); RefreshLists(available: choice.Name);
        };
        _accept = new() { Name = "ButtonOk", Content = T("ButtonOk", "OK"), MinWidth = 75, IsDefault = true };
        Button cancel = new() { Name = "ButtonCancel", Content = T("ButtonCancel", "Cancel"), MinWidth = 75, IsCancel = true };
        _accept.Click += (_, _) => Accept(); cancel.Click += (_, _) => Close(false);
        Grid lists = new() { Margin = new Thickness(3), ColumnDefinitions = new("*,34,*"), RowDefinitions = new("20,*"), ColumnSpacing = 3 };
        TextBlock selected = new() { Name = "lSelectedEncodings", Text = T("lSelectedEncodings", "Selected:"), VerticalAlignment = VerticalAlignment.Center };
        TextBlock available = new() { Name = "lAvaolableEncodings", Text = T("lAvaolableEncodings", "Available:"), VerticalAlignment = VerticalAlignment.Center };
        lists.Children.Add(selected); Grid.SetColumn(available, 2); lists.Children.Add(available);
        Control includedList = ClassicChrome.Border(_included, new Thickness(1)), availableList = ClassicChrome.Border(_available, new Thickness(1));
        Grid.SetRow(includedList, 1); lists.Children.Add(includedList);
        Grid.SetRow(availableList, 1); Grid.SetColumn(availableList, 2); lists.Children.Add(availableList);
        StackPanel arrows = new() { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { _remove, _add } };
        Grid.SetRow(arrows, 1); Grid.SetColumn(arrows, 1); lists.Children.Add(arrows);
        StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(6), Spacing = 6, Children = { cancel, _accept } };
        DockPanel body = new(); DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        DockPanel.SetDock(_notice, Dock.Bottom); _notice.Margin = new Thickness(6, 0, 6, 3); body.Children.Add(_notice); body.Children.Add(lists); Content = body;
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(false); } };
        Opened += (_, _) =>
        {
            if (Owner is Window owner) { RequestedThemeVariant = owner.ActualThemeVariant; }
            _included.Focus(); ModalWindowActivation.AfterMapping(this, () => !_closed && IsVisible);
        };
        Closed += (_, _) => _closed = true;
        RefreshLists();
        if (_baseline.Error is not null) { ShowError(text.Translate("Unable to read preferred encodings.") + " " + _baseline.Error.Message); }
    }

    private void Accept()
    {
        if (_closed || _baseline.Error is not null) { return; }
        try { Result = _store.SaveChanges(_baseline, _draft); Close(true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        { ShowError(error.Message); }
    }

    private void RefreshLists(string? included = null, string? available = null)
    {
        included ??= (_included.SelectedItem as GitFileEncodingChoice)?.Name;
        available ??= (_available.SelectedItem as GitFileEncodingChoice)?.Name;
        _included.ItemsSource = _catalog.Choices(_draft);
        _available.ItemsSource = _catalog.All.Where(choice => !_draft.Contains(choice.Name, StringComparer.Ordinal)).ToArray();
        _included.SelectedItem = _included.Items.OfType<GitFileEncodingChoice>().FirstOrDefault(choice => choice.Name == included);
        _available.SelectedItem = _available.Items.OfType<GitFileEncodingChoice>().FirstOrDefault(choice => choice.Name == available);
        UpdateActions();
    }

    private void UpdateActions()
    {
        _add.IsEnabled = _baseline.Error is null && _available.SelectedItem is GitFileEncodingChoice;
        _remove.IsEnabled = _baseline.Error is null && _included.SelectedItem is GitFileEncodingChoice choice && !_catalog.IsProtected(choice.Name);
        _accept.IsEnabled = _baseline.Error is null;
    }
    private void ShowError(string error) { LastError = error; _notice.Text = error; _notice.IsVisible = true; }
    private static FuncDataTemplate<GitFileEncodingChoice> ChoiceTemplate() => new((choice, _) =>
    {
        TextBlock label = new() { Text = choice?.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis };
        if (choice is not null) { ToolTip.SetTip(label, choice.DisplayName + " (" + choice.Name + ")"); }
        return label;
    });
}

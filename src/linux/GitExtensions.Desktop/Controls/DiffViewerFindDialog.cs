using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Desktop.Controls;

/// <summary>The cached, modeless read-only FindAndReplaceForm.</summary>
internal sealed class DiffViewerFindDialog : Window
{
    private readonly DiffViewerSearch _search;
    private readonly TranslationCatalog _translations;
    private readonly TextBox _query;
    private readonly CheckBox _case;
    private readonly CheckBox _whole;
    private bool _refreshing;
    private bool _release;

    internal DiffViewerFindDialog(DiffViewerSearch search, TranslationCatalog translations)
    {
        _search = search; _translations = translations;
        Name = "DiffViewerFindDialog";
        Width = 419; Height = 169; CanResize = false; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _query = new TextBox { Name = "DiffViewerFindQuery", Height = 24, MinHeight = 24, Padding = new Thickness(3, 1) };
        _case = Check("DiffViewerFindMatchCase", "chkMatchCase", "Match &case");
        _whole = Check("DiffViewerFindMatchWholeWord", "chkMatchWholeWord", "Match &whole word");
        Button previous = Button("DiffViewerFindPrevious", "btnFindPrevious", "Find pre&vious", () => _search.FindFromDialog(true));
        Button next = Button("DiffViewerFindNext", "btnFindNext", "&Find next", () => _search.FindFromDialog(false)); next.IsDefault = true;
        Button highlight = Button("DiffViewerFindHighlightAll", "btnHighlightAll", "Find && highlight &all", () =>
        {
            int count = _search.HighlightAll();
            if (count > 0) { HideForReuse(); }
            else if (_search.Query.Length > 0) { _search.ShowNotFound(this); }
        });
        highlight.Width = 215;
        Button cancel = Button("DiffViewerFindCancel", "btnCancel", "Cancel", HideForReuse); cancel.IsCancel = true;
        Grid input = new() { ColumnDefinitions = new ColumnDefinitions("78,*"), Height = 24 };
        Label label = new() { Name = "DiffViewerFindLabel", Content = new AccessText { Text = Access("label1", "Fi&nd what:") },
            Target = _query, VerticalContentAlignment = VerticalAlignment.Center, Padding = default };
        input.Children.Add(label); Grid.SetColumn(_query, 1); input.Children.Add(_query);
        Grid options = new() { ColumnDefinitions = new ColumnDefinitions("78,*"), RowDefinitions = new RowDefinitions("24,24") };
        Grid.SetColumn(_case, 1); Grid.SetColumn(_whole, 1); Grid.SetRow(_whole, 1); options.Children.Add(_case); options.Children.Add(_whole);
        Content = new StackPanel { Margin = new Thickness(12, 6, 12, 10), Spacing = 3, Children =
        {
            input, new Border { Height = 17 }, options,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { previous, next } },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { highlight, cancel } }
        } };
        _query.TextChanged += (_, _) => { if (!_refreshing) { _search.Query = _query.Text ?? ""; } };
        _case.IsCheckedChanged += (_, _) => { if (!_refreshing) { _search.MatchCase = _case.IsChecked == true; } };
        _whole.IsCheckedChanged += (_, _) => { if (!_refreshing) { _search.MatchWholeWordOnly = _whole.IsChecked == true; } };
        _search.Changed += SearchChanged;
        Opened += (_, _) => { if (Owner is Window owner) { Position = new PixelPoint(owner.Position.X + 100, owner.Position.Y + 100); } FocusQuery(); };
        Closing += (_, args) => { if (!_release) { args.Cancel = true; HideForReuse(); } };
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; HideForReuse(); }
            else if (args.Key == Key.F3 && args.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
            { args.Handled = true; _search.FindFromDialog(args.KeyModifiers == KeyModifiers.Shift); }
        };
        Refresh();
    }

    private void SearchChanged(object? sender, EventArgs args) => Refresh();
    internal void Refresh()
    {
        _refreshing = true;
        try
        {
            if (_query.Text != _search.Query) { _query.Text = _search.Query; }
            _case.IsChecked = _search.MatchCase; _whole.IsChecked = _search.MatchWholeWordOnly;
            Title = T("_findString", "Find") + (_search.ScanRegion is not null ? " (" + T("_selectionOnlyString", "selection only") + ")" : "");
        }
        finally { _refreshing = false; }
    }
    internal void FocusQuery() { _query.Focus(); _query.SelectAll(); }
    private void HideForReuse()
    {
        // Avalonia clears Owner during Hide, unlike the cached WinForms dialog.
        Window? owner = Owner as Window;
        Hide(); _search.ClearScanRegion();
        if (owner is { IsVisible: true }) { owner.Activate(); _search.FocusViewer(); }
    }
    internal void Release()
    {
        _release = true; _search.Changed -= SearchChanged; Close();
    }
    private CheckBox Check(string name, string key, string source) => new()
    {
        Name = name, Content = new AccessText { Text = Access(key, source) }, Height = 24, MinHeight = 24,
        Padding = default, VerticalContentAlignment = VerticalAlignment.Center, IsChecked = false
    };
    private Button Button(string name, string key, string source, Action action)
    {
        Button result = new() { Name = name, Content = new AccessText { Text = Access(key, source) }, Width = 100, Height = 24, MinHeight = 24 };
        result.Click += (_, _) => action(); return result;
    }
    private string T(string key, string source) => _translations.Translate("FindAndReplaceForm", key, "Text", source);
    private string Access(string key, string source) => _translations.TranslateAccessKeys("FindAndReplaceForm", key, "Text", source);
}

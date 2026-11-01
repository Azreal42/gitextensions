using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Desktop.Controls;

/// <summary>The cached, modeless FileViewer FindAndReplaceForm.</summary>
internal sealed class DiffViewerFindDialog : Window
{
    private readonly DiffViewerSearch _search;
    private readonly TranslationCatalog _translations;
    private readonly TextBox _query;
    private readonly TextBox _replacement;
    private readonly Control _replaceInput;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _highlight;
    private readonly Button _replace;
    private readonly Button _replaceAll;
    private readonly CheckBox _case;
    private readonly CheckBox _whole;
    private bool _refreshing;
    private bool _release;
    private bool _replaceMode;
    internal bool ReplaceMode
    {
        get => _replaceMode;
        set { _replaceMode = value && _search.CanReplace; Refresh(); }
    }

    internal DiffViewerFindDialog(DiffViewerSearch search, TranslationCatalog translations)
    {
        _search = search; _translations = translations;
        Name = "DiffViewerFindDialog";
        Width = MinWidth = 419; Height = 169; SizeToContent = SizeToContent.Width; CanResize = false; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _query = new TextBox { Name = "DiffViewerFindQuery", Height = 24, MinHeight = 24, Padding = new Thickness(3, 1) };
        _replacement = new TextBox { Name = "DiffViewerReplaceWith", Height = 24, MinHeight = 24, Padding = new Thickness(3, 1) };
        _case = Check("DiffViewerFindMatchCase", "chkMatchCase", "Match &case");
        _whole = Check("DiffViewerFindMatchWholeWord", "chkMatchWholeWord", "Match &whole word");
        _previous = Button("DiffViewerFindPrevious", "btnFindPrevious", "Find pre&vious", () => _search.FindFromDialog(true));
        _next = Button("DiffViewerFindNext", "btnFindNext", "&Find next", () => _search.FindFromDialog(false));
        _replace = Button("DiffViewerReplace", "btnReplace", "&Replace", _search.ReplaceFromDialog);
        _replaceAll = Button("DiffViewerReplaceAll", "btnReplaceAll", "Replace &All", _search.ReplaceAllFromDialog);
        _highlight = Button("DiffViewerFindHighlightAll", "btnHighlightAll", "Find && highlight &all", () =>
        {
            int count = _search.HighlightAll();
            if (count > 0) { HideForReuse(); }
            else if (_search.Query.Length > 0) { _search.ShowNotFound(this); }
        });
        _highlight.Width = 215;
        Button cancel = Button("DiffViewerFindCancel", "btnCancel", "Cancel", HideForReuse); cancel.IsCancel = true;
        // Preserve the original 317px editor while allowing natural caption
        // metrics to exceed the original 78px gutter on Linux and in translations.
        Grid input = new() { ColumnDefinitions = CaptionColumns("317"), Height = 24 };
        Label label = new() { Name = "DiffViewerFindLabel", Content = new AccessText { Text = Access("label1", "Fi&nd what:") },
            Target = _query, VerticalContentAlignment = VerticalAlignment.Center, Padding = default };
        input.Children.Add(label); Grid.SetColumn(_query, 1); input.Children.Add(_query);
        Grid replacement = new() { Name = "DiffViewerReplaceInput", ColumnDefinitions = CaptionColumns("317"), Height = 24 };
        replacement.Children.Add(new Label { Content = new AccessText { Text = Access("lblReplaceWith", "Re&place with:") },
            Target = _replacement, VerticalContentAlignment = VerticalAlignment.Center, Padding = default });
        Grid.SetColumn(_replacement, 1); replacement.Children.Add(_replacement); _replaceInput = replacement;
        Grid options = new() { ColumnDefinitions = CaptionColumns("*"), RowDefinitions = new RowDefinitions("23,23") };
        Grid.SetColumn(_case, 1); Grid.SetColumn(_whole, 1); Grid.SetRow(_whole, 1); options.Children.Add(_case); options.Children.Add(_whole);
        Grid body = new() { Margin = new Thickness(12, 6, 12, 10), RowDefinitions = new RowDefinitions("26,26,48,28,25") };
        Grid.SetIsSharedSizeScope(body, true);
        Add(input, 0); Add(replacement, 1); Add(options, 2);
        Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { _previous, _next } }, 3);
        Grid bottom = new() { ColumnDefinitions = new ColumnDefinitions("*,100"), ColumnSpacing = 6 };
        StackPanel replacements = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 15, Children = { _replace, _replaceAll } };
        bottom.Children.Add(replacements); bottom.Children.Add(_highlight); _highlight.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(cancel, 1); bottom.Children.Add(cancel); Add(bottom, 4);
        Content = body;
        void Add(Control control, int row) { Grid.SetRow(control, row); body.Children.Add(control); }
        _query.TextChanged += (_, _) => { if (!_refreshing) { _search.Query = _query.Text ?? ""; } };
        _replacement.TextChanged += (_, _) => { if (!_refreshing) { _search.Replacement = _replacement.Text ?? ""; } };
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

    private static ColumnDefinitions CaptionColumns(string editor)
    {
        ColumnDefinitions columns = new("Auto," + editor);
        columns[0].MinWidth = 78; columns[0].SharedSizeGroup = "FindCaption";
        return columns;
    }

    private void SearchChanged(object? sender, EventArgs args) => Refresh();
    internal void Refresh()
    {
        _refreshing = true;
        try
        {
            if (_query.Text != _search.Query) { _query.Text = _search.Query; }
            if (_replacement.Text != _search.Replacement) { _replacement.Text = _search.Replacement; }
            _case.IsChecked = _search.MatchCase; _whole.IsChecked = _search.MatchWholeWordOnly;
            bool replace = ReplaceMode && _search.CanReplace;
            _replaceInput.IsVisible = _replace.IsVisible = _replaceAll.IsVisible = replace;
            _replace.IsEnabled = _replaceAll.IsEnabled = _search.CanReplace;
            _highlight.IsVisible = !replace;
            _replace.IsDefault = replace; _next.IsDefault = !replace;
            Title = (replace ? T("_findAndReplaceString", "Find & replace") : T("_findString", "Find"))
                + (_search.ScanRegion is not null ? " (" + T("_selectionOnlyString", "selection only") + ")" : "");
        }
        finally { _refreshing = false; }
    }
    internal void FocusQuery() { _query.Focus(); _query.SelectAll(); }
    internal void HideForReuse()
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

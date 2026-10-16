using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Presenters;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Desktop.Controls;

public sealed record DiffViewerEncodingChoice(string? Name, string DisplayName);

/// <summary>The original read-only FileViewer commands. Git queries and repository identity belong to the host.</summary>
public sealed class DiffViewerReadingMenu : IDisposable
{
    private readonly DiffView _view;
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly List<(MenuItem Item, GitDiffCopyOperation? Copy)> _items = [];
    private readonly List<ComboBox> _encodingSelectors = [];
    private readonly List<(Image Image, Bitmap Bitmap)> _icons = [];
    private bool _updatingEncoding;
    private Window? _goToDialog;
    private bool _disposed;
    public Task PendingOperation { get; private set; } = Task.CompletedTask;

    internal DiffViewerReadingMenu(DiffView view) => _view = view;

    public IReadOnlyList<object> CreateContextMenuItems()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MenuItem copy = Item("Copy", "copyToolStripMenuItem", "&Copy");
        copy.InputGesture = new(Key.C, KeyModifiers.Control);
        copy.Click += (_, _) => _view.Editor.Copy();
        MenuItem patch = CopyItem("CopyPatch", "copyPatchToolStripMenuItem", "Copy &patch", GitDiffCopyOperation.Patch);
        MenuItem current = CopyItem("CopyNewVersion", "copyNewVersionToolStripMenuItem", "Copy &new version", GitDiffCopyOperation.NewVersion);
        MenuItem previous = CopyItem("CopyOldVersion", "copyOldVersionToolStripMenuItem", "Copy &old version", GitDiffCopyOperation.OldVersion);
        MenuItem find = Item("Find", "findToolStripMenuItem", "&Find...");
        find.InputGesture = new(Key.F, KeyModifiers.Control);
        using (Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/Preview.png")))
        {
            Bitmap bitmap = new(stream); Image icon = new() { Source = bitmap, Width = 16, Height = 16 };
            find.Icon = icon; _icons.Add((icon, bitmap));
        }
        find.Click += (_, _) => _view.Search.ShowFind();
        MenuItem goTo = Item("GoToLine", "goToLineToolStripMenuItem", "&Go to line");
        goTo.InputGesture = new(Key.G, KeyModifiers.Control);
        goTo.Click += (_, _) => Start(ShowGoToLineAsync);
        MenuItem replace = Item("Replace", "replaceToolStripMenuItem", "&Replace...");
        replace.InputGesture = new(Key.H, KeyModifiers.Control);
        replace.Click += (_, _) => _view.Search.ShowFind(replaceMode: true);
        _items.Add((copy, null)); _items.Add((find, null)); _items.Add((replace, null)); _items.Add((goTo, null)); Refresh();
        return [copy, patch, current, previous, new Separator(), find, replace, goTo];
    }

    public ComboBox CreateEncodingSelector()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ComboBox selector = new()
        {
            Name = "DiffViewerEncoding", Width = 140, Height = 24, MinHeight = 24, Padding = new Thickness(3, 0),
            ItemTemplate = EncodingTemplate(216), SelectionBoxItemTemplate = EncodingTemplate(104),
            Resources = new ResourceDictionary
            {
                ThemeDictionaries =
                {
                    [ThemeVariant.Light] = EncodingResources(dark: false),
                    [ThemeVariant.Dark] = EncodingResources(dark: true)
                }
            }
        };
        // The original FileViewer clamps the encoding dropdown to 250 px. Fluent
        // only binds its minimum width, so constrain the actual popup host as well
        // as the labels, including any scrollbar and template padding.
        selector.TemplateApplied += (_, args) =>
        {
            if (args.NameScope.Find<Popup>("PART_Popup") is Popup popup) { popup.MaxWidth = 250; }
        };
        selector.Styles.Add(new Style(style => style.OfType<ComboBoxItem>())
        {
            Setters =
            {
                new Setter(Layoutable.HeightProperty, 24d), new Setter(Layoutable.MinHeightProperty, 24d),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 2)),
                new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                new Setter(TemplatedControl.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ClassicTextBrush"))
            }
        });
        // Style the presenter that actually paints the row. Setting only the
        // item's Foreground does not override the inner Fluent template states.
        selector.Styles.Add(new Style(style => style.OfType<ComboBoxItem>().Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Setter(ContentPresenter.ForegroundProperty, EncodingBrush("Text")) }
        });
        foreach (string state in new[] { ":selected", ":pointerover", ":pressed" })
        {
            selector.Styles.Add(new Style(style => style.OfType<ComboBoxItem>().Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, EncodingBrush("Highlight")),
                    new Setter(ContentPresenter.ForegroundProperty, EncodingBrush("HighlightText"))
                }
            });
        }
        ToolTip.SetTip(selector, _translations.Translate("Encoding"));
        _encodingSelectors.Add(selector);
        selector.SelectionChanged += (_, _) =>
        {
            if (!_updatingEncoding && selector.SelectedItem is DiffViewerEncodingChoice choice) { _view.FileEncodingName = choice.Name; }
        };
        Refresh(); return selector;
    }

    private static Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension EncodingBrush(string color)
        => new("Classic" + color + "Brush");

    private static FuncDataTemplate<DiffViewerEncodingChoice> EncodingTemplate(double maximumWidth)
        => new((choice, _) =>
        {
            TextBlock label = new() { Name = "DiffViewerEncodingLabel", Text = choice?.DisplayName, MaxWidth = maximumWidth,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            // A data-template child's logical parent is the ComboBoxItem, whose
            // ordinary text color need not match Fluent's selected presenter.
            // Follow the actual visual presenter so keyboard selection, hover and
            // theme changes use the original highlight/foreground pair together.
            IDisposable? foregroundBinding = null;
            label.AttachedToVisualTree += (_, _) =>
            {
                foregroundBinding?.Dispose();
                if (label.GetVisualAncestors().OfType<ContentPresenter>().FirstOrDefault() is ContentPresenter presenter)
                { foregroundBinding = label.Bind(TextBlock.ForegroundProperty, presenter.GetObservable(ContentPresenter.ForegroundProperty)); }
            };
            label.DetachedFromVisualTree += (_, _) => { foregroundBinding?.Dispose(); foregroundBinding = null; };
            if (choice is not null) { ToolTip.SetTip(label, choice.DisplayName); }
            return label;
        });

    private static ResourceDictionary EncodingResources(bool dark)
    {
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(dark);
        ResourceDictionary resources = new() { ["OverlayCornerRadius"] = new CornerRadius(0) };
        foreach ((string resource, string color) in new Dictionary<string, string>
        {
            ["ComboBoxDropDownBackground"] = "EditorBackground", ["ComboBoxDropDownBorderBrush"] = "ActiveBorder",
            ["SystemControlHighlightListLowBrush"] = "Highlight", ["SystemControlHighlightListMediumBrush"] = "Highlight",
            ["SystemControlHighlightListAccentLowBrush"] = "Highlight", ["SystemControlHighlightListAccentMediumBrush"] = "Highlight",
            ["SystemControlHighlightListAccentHighBrush"] = "Highlight", ["SystemControlHighlightAltBaseHighBrush"] = dark ? "WindowText" : "HighlightText",
            ["ComboBoxForegroundFocused"] = "WindowText", ["ComboBoxForegroundFocusedPressed"] = "WindowText",
            ["ComboBoxDropDownGlyphForeground"] = "WindowText", ["ComboBoxDropDownGlyphForegroundFocused"] = "WindowText",
            ["ComboBoxDropDownGlyphForegroundFocusedPressed"] = "WindowText"
        }) { resources[resource] = Brush.Parse(colors[color]); }
        return resources;
    }

    public bool HandleKeyDown(KeyEventArgs args)
    {
        if (_disposed || args.Handled || !_view.IsEffectivelyEnabled || args.KeyModifiers != KeyModifiers.Control || args.Key != Key.G) { return false; }
        if (_view.MaxFileLine < 1 || TopLevel.GetTopLevel(_view) is not Window) { return false; }
        args.Handled = true;
        Start(ShowGoToLineAsync);
        return true;
    }

    public async Task CopyAsync(GitDiffCopyOperation operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_view.CanCopyPatch || string.IsNullOrEmpty(_view.Text)) { return; }
        IClipboard? clipboard = TopLevel.GetTopLevel(_view)?.Clipboard;
        if (clipboard is null) { return; }
        GitDiffTextSelection? selection = _view.SelectionLength > 0 ? new(_view.SelectionStart, _view.SelectionLength) : null;
        string text = GitDiffClipboard.GetText(_view.Text, operation, selection, isDiff: true, _view.CopyAutoCrlf);
        await ClipboardExtensions.SetTextAsync(clipboard, text);
    }

    public async Task ShowGoToLineAsync()
    {
        if (_disposed || !_view.IsEffectivelyEnabled || TopLevel.GetTopLevel(_view) is not Window { IsVisible: true } owner) { return; }
        if (_goToDialog is { IsVisible: true }) { _goToDialog.Activate(); return; }
        int maximum = _view.MaxFileLine;
        if (maximum < 1) { return; }
        NumericUpDown line = new() { Name = "DiffViewerLineNumber", Minimum = 1, Maximum = maximum, Value = 1,
            Width = 210, Height = 24, MinHeight = 24, FormatString = "0", ClipValueToMinMax = true,
            Template = LineEditorTemplate() };
        Button accept = new() { Name = "DiffViewerGoToAccept", Content = T("FormGoToLine", "okBtn", "OK"), IsDefault = true, MinWidth = 75, Height = 24 };
        Button cancel = new() { Name = "DiffViewerGoToCancel", Content = T("FormGoToLine", "cancelBtn", "Cancel"), IsCancel = true, MinWidth = 75, Height = 24 };
        Window dialog = new() { Name = "DiffViewerGoToLineDialog", Title = T("FormGoToLine", "$this", "Go to line"), Width = 237,
            SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = owner.ActualThemeVariant };
        dialog.Content = new StackPanel { Margin = new Thickness(12, 9, 12, 10), Spacing = 4, Children =
        {
            new TextBlock { Name = "DiffViewerLineLabel", Text = T("FormGoToLine", "lineLabel", "Line number") + " (1 - " + maximum.ToString(CultureInfo.CurrentCulture) + "):" },
            line,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 6, Margin = new Thickness(0, 4, 0, 0), Children = { accept, cancel } }
        } };
        accept.Click += (_, _) => { if (line.Value is decimal number && number >= 1 && number <= maximum) { dialog.Close((int)number); } };
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; dialog.Close(null); } };
        dialog.Opened += (_, _) =>
        {
            TextBox? input = line.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (input is not null) { input.Focus(); input.SelectAll(); } else { line.Focus(); }
        };
        _goToDialog = dialog;
        try
        {
            int? number = await dialog.ShowDialog<int?>(owner);
            if (!_disposed && number is int value && owner.IsVisible) { _view.GoToFileLine(value); }
        }
        finally
        {
            if (ReferenceEquals(_goToDialog, dialog)) { _goToDialog = null; }
            if (!_disposed && owner.IsVisible) { _view.Editor.TextArea.Focus(); }
        }
    }

    internal void Refresh()
    {
        foreach ((MenuItem item, GitDiffCopyOperation? copy) in _items)
        {
            if (copy is not null) { item.IsVisible = _view.CanCopyPatch; item.IsEnabled = _view.Text.Length > 0; }
            else
            {
                if (item.Name == "DiffViewerReplace") { item.IsVisible = !_view.IsReadOnly; }
                item.IsEnabled = item.Name switch { "DiffViewerCopy" => _view.SelectionLength > 0, "DiffViewerFind" => _view.Search.IsAvailable,
                    "DiffViewerReplace" => _view.Search.CanReplace, _ => _view.MaxFileLine > 0 };
            }
        }
        _updatingEncoding = true;
        try
        {
            string defaultName = _view.DefaultFileEncodingName;
            string defaultDisplay = GitFileEncodingCodec.AvailableEncodings.FirstOrDefault(item => item.Name == defaultName)?.DisplayName ?? defaultName;
            DiffViewerEncodingChoice[] choices = [new(null, defaultDisplay + " (" + _translations.Translate("Default") + ")"),
                .. GitFileEncodingCodec.AvailableEncodings.Select(item => new DiffViewerEncodingChoice(item.Name, item.DisplayName))];
            foreach (ComboBox selector in _encodingSelectors)
            {
                if (selector.ItemsSource is not DiffViewerEncodingChoice[] previous || previous[0] != choices[0]) { selector.ItemsSource = choices; }
                selector.SelectedItem = selector.Items.OfType<DiffViewerEncodingChoice>().FirstOrDefault(item => item.Name == _view.FileEncodingName);
            }
        }
        finally { _updatingEncoding = false; }
    }

    private MenuItem CopyItem(string name, string original, string text, GitDiffCopyOperation operation)
    {
        MenuItem item = Item(name, original, text);
        item.Click += (_, _) => Start(() => CopyAsync(operation)); _items.Add((item, operation)); return item;
    }

    private MenuItem Item(string name, string original, string source)
        => new() { Name = "DiffViewer" + name, Header = new AccessText { Text = _translations.TranslateAccessKeys("FileViewer", original, "Text", source) } };
    private string T(string category, string item, string source) => _translations.Translate(category, item, "Text", source);

    private static FuncControlTemplate<NumericUpDown> LineEditorTemplate() => new((owner, scope) =>
    {
        TextBox text = new() { Name = "PART_TextBox", Height = 22, MinHeight = 0, MinWidth = 0, Padding = new Thickness(2, 0), BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center };
        ButtonSpinner spinner = new() { Name = "PART_Spinner", Content = text, AllowSpin = true, MinWidth = 0, MinHeight = 0, Padding = default,
            Template = new FuncControlTemplate<ButtonSpinner>((host, spinnerScope) =>
            {
                RepeatButton increase = Arrow("PART_IncreaseButton", "▴"), decrease = Arrow("PART_DecreaseButton", "▾");
                spinnerScope.Register(increase.Name!, increase); spinnerScope.Register(decrease.Name!, decrease);
                StackPanel arrows = new() { Width = 14, Children = { increase, decrease } };
                Grid panel = new() { ColumnDefinitions = new ColumnDefinitions("*,14") };
                panel.Children.Add(new ContentPresenter { Content = host.Content, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                Grid.SetColumn(arrows, 1); panel.Children.Add(arrows); return panel;
            }) };
        scope.Register(text.Name!, text); scope.Register(spinner.Name!, spinner);
        return ClassicChrome.Border(spinner, new Thickness(1));
        static RepeatButton Arrow(string name, string glyph) => new() { Name = name, Content = glyph, Height = 11, MinHeight = 0, MinWidth = 0, FontSize = 8, Padding = default,
            Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    });

    private void Start(Func<Task> operation) => PendingOperation = ExecuteAsync(operation);
    private async Task ExecuteAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!_disposed && TopLevel.GetTopLevel(_view) is Window { IsVisible: true } owner) { await Dialogs.ShowErrorAsync(owner, exception.Message); }
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; } _disposed = true;
        _goToDialog?.Close(null); _goToDialog = null; _items.Clear(); _encodingSelectors.Clear();
        foreach ((Image icon, Bitmap bitmap) in _icons) { icon.Source = null; bitmap.Dispose(); } _icons.Clear();
    }

}

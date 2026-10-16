using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

internal enum QuickGitRefAction { Rename, Delete, Select }

/// <summary>The borderless FormQuickGitRefSelector used to disambiguate a commit's references.</summary>
internal sealed class QuickGitRefSelectorDialog : Window
{
    private const int MaximumVisibleItems = 8;
    private const int MaximumMeasuredCharacters = 100;
    private const string Separator = "――――――――――――――――――";
    private readonly ListBox _references = new()
    {
        Name = "lbxRefs", SelectionMode = SelectionMode.Single, Focusable = true, TabIndex = 0, IsTextSearchEnabled = true,
        Padding = default, BorderThickness = default, ClipToBounds = true
    };
    private readonly IReadOnlyList<ReferenceItem> _items;
    private readonly Border _frame;
    private Style? _rowStyle;
    private bool _closed;

    internal QuickGitRefSelectorDialog(QuickGitRefAction action, IReadOnlyList<GitReference> references,
        TranslationCatalog? translations = null)
    {
        ArgumentNullException.ThrowIfNull(references);
        translations ??= TranslationCatalog.LoadDefault();
        string Text(string key, string value) => translations.Translate("FormQuickGitRefSelector", key, "Text", value);
        string buttonText = action switch
        {
            QuickGitRefAction.Rename => Text("_actionRename", "Rename"),
            QuickGitRefAction.Delete => Text("_actionDelete", "Delete"),
            QuickGitRefAction.Select => Text("_actionSelect", "Select"),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        List<ReferenceItem> items = [];
        AddGroup(reference => reference.IsBranch, Text("_local", "local"));
        AddGroup(reference => reference.IsRemote, Text("_remote", "remote"));
        AddGroup(reference => reference.IsTag, Text("_tag", "tag"));
        _items = items;

        Name = "QuickGitRefSelectorDialog"; Title = buttonText;
        SystemDecorations = SystemDecorations.None; CanResize = CanMinimize = CanMaximize = false;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.Manual;
        MinWidth = 154;
        _references.ItemTemplate = new FuncDataTemplate<ReferenceItem>((item, _) => new TextBlock
        {
            Text = item?.Label ?? "", TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None, VerticalAlignment = VerticalAlignment.Center
        });
        _references.ItemsSource = items;
        _references.SelectedIndex = items.Count == 0 ? -1 : 1;
        ScrollViewer.SetHorizontalScrollBarVisibility(_references, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_references, ScrollBarVisibility.Auto);
        _references.DoubleTapped += (_, args) => { args.Handled = true; Accept(); };

        Button accept = new()
        {
            Name = "btnAction", Content = buttonText, IsDefault = true, TabIndex = 1,
            MinWidth = 75, Height = 23, MinHeight = 23, Margin = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            IsEnabled = items.Count > 0
        };
        accept.Click += (_, _) => Accept();
        Grid footer = new() { Name = "flowLayoutPanel1", Height = 29, Children = { accept } };
        _frame = ClassicChrome.Border(_references, new Thickness(1)); _frame.Name = "QuickReferencesFrame";
        Grid root = new() { RowDefinitions = new RowDefinitions("Auto,29"), Margin = new Thickness(2, 2, 2, 0) };
        root.Children.Add(_frame); Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        UpdateMetrics();
        Opened += (_, _) =>
        {
            if (_items.Count == 0)
            {
                Dispatcher.UIThread.Post(() => { if (IsVisible) { Close(); } }, DispatcherPriority.Loaded);
                return;
            }
            UpdateMetrics(); _references.Focus();
            // The logical focus is already set. Activation after mapping must not reset a later choice or focus.
            ModalWindowActivation.AfterMapping(this, () => !_closed);
        };
        Closed += (_, _) => _closed = true;
        KeyDown += (_, args) =>
        {
            if (!args.Handled && args.Key == Key.Escape) { args.Handled = true; Close(); }
        };

        void AddGroup(Func<GitReference, bool> predicate, string caption)
        {
            GitReference[] group = references.Where(predicate).OrderBy(reference => reference.ShortName).ToArray();
            if (group.Length == 0) { return; }
            items.Add(new(($"{caption} {Separator}")[..Separator.Length], null));
            items.AddRange(group.Select(reference => new ReferenceItem(reference.ShortName, reference)));
        }
    }

    internal GitReference? SelectedReference => (_references.SelectedItem as ReferenceItem)?.Reference;
    internal bool WasAccepted { get; private set; }

    private void Accept()
    {
        if (_closed || _items.Count == 0) { return; }
        WasAccepted = true; Close();
    }

    private void UpdateMetrics()
    {
        Typeface face = new(_references.FontFamily, _references.FontStyle, _references.FontWeight, _references.FontStretch);
        FormattedText Measure(string value) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, _references.FontSize, Brushes.Black);
        double itemHeight = Math.Ceiling(Measure("Ag").Height);
        double textWidth = _items.Select(item => Measure(item.Label[..Math.Min(item.Label.Length, MaximumMeasuredCharacters)]).WidthIncludingTrailingWhitespace)
            .DefaultIfEmpty(0).Max();
        // GDI MeasureString includes overhang padding; retain room for the list frame and the native text inset.
        _frame.Width = Math.Floor(Math.Max(150, textWidth + 4));
        _frame.Height = Math.Min(_items.Count, MaximumVisibleItems) * itemHeight + 2;
        Width = _frame.Width + 4; Height = _frame.Height + 2 + 29;
        if (_rowStyle is not null) { _references.Styles.Remove(_rowStyle); }
        _rowStyle = new Style(selector => selector.OfType<ListBoxItem>()) { Setters =
        {
            new Setter(Layoutable.HeightProperty, itemHeight), new Setter(Layoutable.MinHeightProperty, itemHeight),
            new Setter(TemplatedControl.PaddingProperty, new Thickness(1, 0)), new Setter(TemplatedControl.BorderThicknessProperty, default(Thickness)),
            new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
        } };
        _references.Styles.Add(_rowStyle);
    }

    private sealed record ReferenceItem(string Label, GitReference? Reference)
    {
        public override string ToString() => Label;
    }
}

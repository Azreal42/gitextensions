using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitCommands;

namespace GitExtensions.Linux.Desktop.Controls;

/// <summary>Measured file-leaf text; the containing row and its raw identity never change.</summary>
internal sealed class FilePathHeader : Grid
{
    private readonly string _name;
    private readonly string? _oldName;
    private readonly Run _file = new();
    private TruncatePathMethod _method;
    private (string? Prefix, string? Text, string? Suffix)? _formatted;
    private double _lastWidth = double.NaN;

    internal TextBlock Label { get; } = new()
    {
        Name = "FileStatusPathText", VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(2, 0), TextTrimming = TextTrimming.None
    };
    internal Run DirectoryRun { get; } = new();
    internal Run PreviousRun { get; } = new();

    internal FilePathHeader(Control icon, string name, string? oldName, TruncatePathMethod method)
    {
        _name = name; _oldName = oldName; _method = method;
        Name = "FileStatusPathHeader";
        ColumnDefinitions = new("16,*"); Height = ClassicTreeViewAppearance.RowHeight;
        Children.Add(icon); Grid.SetColumn(Label, 1); Children.Add(Label);
        Label.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBlock.FontFamilyProperty || change.Property == TextBlock.FontSizeProperty
                || change.Property == TextBlock.FontStyleProperty || change.Property == TextBlock.FontWeightProperty
                || change.Property == TextBlock.FontStretchProperty || change.Property == FlowDirectionProperty)
            { InvalidateMeasure(); }
        };
        LayoutUpdated += (_, _) =>
        {
            double width = AvailableTextWidth(double.PositiveInfinity);
            if (!SameWidth(width, _lastWidth)) { InvalidateMeasure(); }
        };
        UpdateText(double.PositiveInfinity);
    }

    internal void SetMethod(TruncatePathMethod method)
    {
        if (_method == method) { return; }
        _method = method;
        UpdateText(AvailableTextWidth(double.PositiveInfinity));
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _lastWidth = AvailableTextWidth(availableSize.Width);
        UpdateText(_lastWidth);
        return base.MeasureOverride(availableSize);
    }

    private double AvailableTextWidth(double fallback)
    {
        // The TreeView permits horizontal scrolling and therefore measures a
        // header with infinity. Its real scroll presenter, rather than that
        // natural desired width, is the Windows client-width equivalent.
        ScrollContentPresenter? viewport = this.GetVisualAncestors().OfType<ScrollContentPresenter>().FirstOrDefault();
        if (viewport is not null && viewport.Bounds.Width > 0 && this.TranslatePoint(default, viewport) is Point point)
        { return Math.Max(0, viewport.Bounds.Width - point.X - 16 - Label.Margin.Left - Label.Margin.Right); }
        if (double.IsFinite(fallback)) { return Math.Max(0, fallback - 16 - Label.Margin.Left - Label.Margin.Right); }
        return double.PositiveInfinity;
    }

    private void UpdateText(double width)
    {
        var next = FilePathPresentation.Format(_method, width, _name, _oldName, Measure);
        if (_formatted is { } old && old == next) { return; }
        _formatted = next;
        DirectoryRun.Text = next.prefix; _file.Text = next.text; PreviousRun.Text = next.suffix;
        Label.Inlines!.Clear();
        if (!string.IsNullOrEmpty(next.prefix)) { Label.Inlines.Add(DirectoryRun); }
        if (next.text is not null) { Label.Inlines.Add(_file); }
        if (!string.IsNullOrEmpty(next.suffix)) { Label.Inlines.Add(PreviousRun); }
    }

    private double Measure(string text) => new FormattedText(text, CultureInfo.CurrentCulture, Label.FlowDirection,
        new Typeface(Label.FontFamily, Label.FontStyle, Label.FontWeight, Label.FontStretch),
        Label.FontSize, Brushes.Black).WidthIncludingTrailingWhitespace;

    private static bool SameWidth(double first, double second)
        => first == second || double.IsFinite(first) && double.IsFinite(second) && Math.Abs(first - second) < .25;
}

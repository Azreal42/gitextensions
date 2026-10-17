using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace GitExtensions.Linux.Desktop.Controls;

// QuickSearchProvider searches the displayed grid, independently of its Git filter.
internal sealed class RevisionQuickSearch : IDisposable
{
    private readonly Func<int> _count, _current;
    private readonly Func<int, string, bool> _matches;
    private readonly string _prefix;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(4000) };
    private readonly TextBlock _text = new() { Name = "RevisionQuickSearchText", FontSize = 44d / 3, FontWeight = FontWeight.Bold };
    private string _active = "", _last = "";
    private bool _found = true;
    private IDisposable? _foregroundBinding;

    internal RevisionQuickSearch(Func<int> count, Func<int> current, Func<int, string, bool> matches, string prefix)
    {
        _count = count; _current = current; _matches = matches; _prefix = prefix;
        Notice = new Border
        {
            Name = "RevisionQuickSearchNotice", Child = _text, Padding = new Thickness(7, 5, 5, 5),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, IsVisible = false
        };
        Notice.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ClassicTooltipBrush"));
        Notice.ActualThemeVariantChanged += ThemeChanged;
        Notice.PropertyChanged += BackgroundChanged;
        _timer.Tick += Timeout;
    }

    internal Border Notice { get; }
    internal string ActiveText => _active;

    internal int? Type(string text)
        => Update(_active + string.Concat(text.Where(character => !char.IsControl(character)).Select(char.ToLower)));

    internal int? Paste(string text) => Update(_active + text);

    internal int? Backspace(out bool handled)
    {
        handled = _active.Length > 1;
        if (handled) { return Update(_active[..^1]); }
        Hide(); return null;
    }

    internal int? Next(bool down)
    {
        _active = _last;
        int current = _current();
        return Find(current < 0 ? 0 : current + (down ? 1 : -1), reverse: !down);
    }

    internal void Hide()
    {
        _active = ""; Notice.IsVisible = false; _timer.Stop();
    }

    private int? Update(string text)
    {
        _active = _last = text;
        return Find(Math.Max(0, _current()), reverse: false);
    }

    private int? Find(int start, bool reverse)
    {
        int count = _count();
        if (start < 0 || start >= count) { start = reverse ? count - 1 : 0; }
        int? found = null;
        for (int step = 0; step < count; step++)
        {
            int index = ((start + (reverse ? -step : step)) % count + count) % count;
            if (_matches(index, _active)) { found = index; break; }
        }
        if (count > 0) { _found = found.HasValue; }
        _text.Text = _prefix + _active;
        UpdateColor(); Notice.IsVisible = true;
        _timer.Stop(); _timer.Start();
        return found;
    }

    private void ThemeChanged(object? sender, EventArgs args) => UpdateColor();
    private void BackgroundChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    { if (args.Property == Border.BackgroundProperty) { UpdateColor(); } }
    private void UpdateColor()
    {
        _foregroundBinding?.Dispose(); _foregroundBinding = null;
        if (_found) { _foregroundBinding = _text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ClassicTooltipTextBrush")); }
        else
        {
            Color background = Notice.Background is ISolidColorBrush brush ? brush.Color : Colors.LightYellow;
            _text.Foreground = new SolidColorBrush(ClassicTextContrast.AdaptForeground(background, Colors.DarkRed));
        }
    }
    private void Timeout(object? sender, EventArgs args) => Hide();

    internal static bool Matches(string id, string? author, string? message, IEnumerable<string> references, string criteria)
    {
        if (string.IsNullOrWhiteSpace(criteria)) { return false; }
        return references.Any(name => !string.IsNullOrWhiteSpace(name) && name.Contains(criteria, StringComparison.OrdinalIgnoreCase))
            || criteria.Length > 2 && id.StartsWith(criteria, StringComparison.CurrentCultureIgnoreCase)
            || author?.Contains(criteria, StringComparison.CurrentCultureIgnoreCase) == true
            || message?.Contains(criteria, StringComparison.OrdinalIgnoreCase) == true;
    }

    public void Dispose()
    {
        Hide(); _timer.Tick -= Timeout; Notice.ActualThemeVariantChanged -= ThemeChanged;
        Notice.PropertyChanged -= BackgroundChanged; _foregroundBinding?.Dispose();
    }
}

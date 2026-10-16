using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Document;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Desktop.Controls;

public sealed record DiffViewerSearchMatch(int Offset, int Length, bool LoopedAround = false);
internal enum DiffViewerSearchMarkerKind { Region, Occurrence, Highlight }
internal sealed record DiffViewerSearchMarker(int Offset, int Length, DiffViewerSearchMarkerKind Kind);

/// <summary>Shared FileViewer find/replace. All positions refer to displayed UTF-16 text.</summary>
public sealed class DiffViewerSearch : IDisposable
{
    private readonly DiffView _view;
    private readonly IDisposable _caretOutsideSelection;
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly List<DiffViewerSearchMatch> _occurrences = [];
    private readonly List<DiffViewerSearchMatch> _highlights = [];
    private DiffViewerFindDialog? _dialog;
    private Window? _owner;
    private Window? _notification;
    private string _query = "";
    private string _selectedText = "";
    private string _replacement = "";
    private bool _lastSearchBackward;
    private bool _matchCase;
    private bool _matchWholeWord;
    private bool _suspended;
    private bool _disposed;
    private readonly GitDiffColor[] _backgrounds = new GitDiffColor[3];
    private readonly GitDiffColor[] _foregrounds = new GitDiffColor[3];
    public event EventHandler? Changed;
    public Func<Task>? OpenDiffToolRequested { get; set; }
    public Task PendingOperation { get; private set; } = Task.CompletedTask;
    public string Query { get => _query; set { value ??= ""; if (_query == value) { return; } _query = value; Notify(); } }
    public string Replacement { get => _replacement; set { value ??= ""; if (_replacement == value) { return; } _replacement = value; Notify(); } }
    public bool CanReplace => IsAvailable && !_view.IsReadOnly;
    public bool MatchCase { get => _matchCase; set { if (_matchCase == value) { return; } _matchCase = value; Notify(); } }
    public bool MatchWholeWordOnly { get => _matchWholeWord; set { if (_matchWholeWord == value) { return; } _matchWholeWord = value; Notify(); } }
    public DiffViewerSearchMatch? ScanRegion { get; private set; }
    public IReadOnlyList<DiffViewerSearchMatch> Occurrences => _occurrences.AsReadOnly();
    public IReadOnlyList<DiffViewerSearchMatch> Highlights => _highlights.AsReadOnly();
    public bool IsAvailable => !_disposed && !_suspended && _view.IsDocumentReady && _view.IsEffectivelyEnabled;
    internal DiffViewerFindDialog? Dialog => _dialog;

    internal DiffViewerSearch(DiffView view)
    {
        _view = view;
        // The original read-only FileViewer allows an independent caret and
        // selection. AvaloniaEdit otherwise clears that selection in a deferred
        // validation, including after occurrence navigation or a display reload.
        _caretOutsideSelection = _view.Editor.TextArea.AllowCaretOutsideSelection();
        UpdateColors();
        _view.Editor.TextArea.SelectionChanged += SelectionChanged;
        _view.Editor.Document.Changed += DocumentChanged;
        _view.DetachedFromVisualTree += Detached;
        _view.AttachedToVisualTree += Attached;
    }

    public void ShowFind(bool replaceMode = false)
    {
        if (!IsAvailable || TopLevel.GetTopLevel(_view) is not Window { IsVisible: true } owner) { return; }
        ClearScanRegion();
        string text = _view.Text;
        int start = _view.SelectionStart, length = _view.SelectionLength;
        if (length > 0)
        {
            if (_view.Editor.Document.GetLineByOffset(start).LineNumber == _view.Editor.Document.GetLineByOffset(start + length).LineNumber)
            { Query = text.Substring(start, length); }
            else { ScanRegion = new(start, length); Notify(); }
        }
        else
        {
            int first = _view.CaretOffset, last = first;
            while (first > 0 && IsWord(text[first - 1])) { first--; }
            while (last < text.Length && IsWord(text[last])) { last++; }
            Query = text[first..last];
        }
        if (!ReferenceEquals(owner, _owner)) { ReleaseDialog(); _owner = owner; _owner.Closed += OwnerClosed; }
        _dialog ??= new(this, _translations);
        _dialog.ReplaceMode = replaceMode && CanReplace;
        _dialog.RequestedThemeVariant = _view.ActualThemeVariant;
        _dialog.Refresh();
        if (!_dialog.IsVisible) { _dialog.Show(owner); }
        _dialog.Activate(); _dialog.FocusQuery();
    }

    public void ClearScanRegion()
    {
        if (ScanRegion is null) { return; }
        ScanRegion = null; Notify();
    }

    public DiffViewerSearchMatch? FindNext(bool searchBackward = false, bool viaF3 = true)
    {
        if (!IsAvailable || Query.Length == 0) { return null; }
        _lastSearchBackward = searchBackward;
        string text = _view.Text;
        int caret = _view.Editor.CaretOffset;
        if (viaF3 && ScanRegion is { } scan && (caret < scan.Offset || caret > scan.Offset + scan.Length)) { ClearScanRegion(); }
        int begin = ScanRegion?.Offset ?? 0;
        // Native selections are half-open. The legacy searcher accidentally fed
        // TextMarker's inclusive EndOffset into its exclusive search interval.
        int end = ScanRegion is { } region ? region.Offset + region.Length : text.Length;
        string selected = _view.Editor.SelectedText;
        bool selectedResult = _view.SelectionLength == Query.Length && _view.SelectionStart + _view.SelectionLength == caret
            && (MatchCase ? selected == Query : selected.ToUpperInvariant() == Query.ToUpperInvariant());
        // Exclude a currently selected result when stepping backwards, while an
        // initial multiline region still includes a match ending at its boundary.
        int start = caret - (searchBackward && selectedResult ? 1 : 0);
        if (start == -1) { start = end; }
        start = Math.Clamp(start, begin, end);
        int offset = searchBackward ? FindIn(text, begin, start, true) : FindIn(text, start, end, false);
        bool looped = offset < 0;
        if (looped) { offset = searchBackward ? FindIn(text, start, end, true) : FindIn(text, begin, start, false); }
        if (offset < 0) { return null; }
        DiffViewerSearchMatch result = new(offset, Query.Length, looped);
        _view.Select(result.Offset, result.Length);
        _view.Editor.CaretOffset = result.Offset + result.Length;
        _view.Editor.ScrollToLine(_view.Editor.Document.GetLineByOffset(result.Offset).LineNumber);
        return result;
    }

    public DiffViewerSearchMatch? ReplaceNext()
    {
        if (!CanReplace || Query.Length == 0) { return null; }
        int start = _view.SelectionStart;
        if (_view.SelectionLength == Query.Length && FindIn(_view.Text, start, start + Query.Length, false) == start
            && (ScanRegion is not { } region || start >= region.Offset && start + Query.Length <= region.Offset + region.Length))
        {
            _view.Editor.Document.Replace(start, Query.Length, Replacement);
            _view.Select(start + Replacement.Length, 0);
        }
        return FindNext(_lastSearchBackward, viaF3: false);
    }

    public int ReplaceAll()
    {
        if (!CanReplace || Query.Length == 0) { return 0; }
        List<int> matches = [];
        int offset = ScanRegion?.Offset ?? 0;
        int end = ScanRegion is { } region ? region.Offset + region.Length : _view.Text.Length;
        while (offset <= end)
        {
            int match = FindIn(_view.Text, offset, end, false);
            if (match < 0) { break; }
            matches.Add(match);
            offset = match + Query.Length;
        }
        if (matches.Count == 0) { return 0; }
        // Match the original undo group, and search the old snapshot only so a
        // replacement containing the query cannot replace itself indefinitely.
        TextDocument document = _view.Editor.Document;
        document.BeginUpdate();
        try
        {
            for (int index = matches.Count - 1; index >= 0; index--) { document.Replace(matches[index], Query.Length, Replacement); }
        }
        finally { document.EndUpdate(); }
        _view.Select(matches[^1] + (matches.Count - 1) * (Replacement.Length - Query.Length) + Replacement.Length, 0);
        return matches.Count;
    }

    private int FindIn(string text, int begin, int end, bool backwards)
    {
        end -= Query.Length;
        string expected = MatchCase ? Query : Query.ToUpperInvariant();
        for (int offset = backwards ? end : begin; backwards ? offset >= begin : offset <= end; offset += backwards ? -1 : 1)
        {
            if (text[offset] != expected[0] && (MatchCase || char.ToUpperInvariant(text[offset]) != expected[0])) { continue; }
            if (MatchWholeWordOnly && (!Boundary(text, offset) || !Boundary(text, offset + Query.Length))) { continue; }
            // The original searcher compares invariant uppercase UTF-16 strings;
            // selection occurrences deliberately use OrdinalIgnoreCase instead.
            if (MatchCase ? text.AsSpan(offset, Query.Length).SequenceEqual(expected) : text.Substring(offset, Query.Length).ToUpperInvariant() == expected) { return offset; }
        }
        return -1;
    }
    private static bool IsWord(char character) => char.IsLetterOrDigit(character) || character == '_';
    private static bool Boundary(string text, int offset)
        => offset <= 0 || offset >= text.Length || !IsWord(text[offset - 1]) || !IsWord(text[offset]);

    public int HighlightAll()
    {
        _highlights.Clear();
        if (IsAvailable && Query.Length > 0)
        {
            int offset = ScanRegion?.Offset ?? 0;
            int end = ScanRegion is { } region ? region.Offset + region.Length : _view.Text.Length;
            while (offset <= end)
            {
                int match = FindIn(_view.Text, offset, end, false);
                if (match < 0) { break; }
                _highlights.Add(new(match, Query.Length)); offset = match + Query.Length;
            }
        }
        Notify(); return _highlights.Count;
    }

    public bool GoToNextOccurrence() => NavigateOccurrence(backwards: false);
    public bool GoToPreviousOccurrence() => NavigateOccurrence(backwards: true);
    private bool NavigateOccurrence(bool backwards)
    {
        if (!IsAvailable) { return false; }
        int caret = _view.Editor.CaretOffset;
        DiffViewerSearchMatch? marker = backwards ? _occurrences.LastOrDefault(item => item.Offset < caret) : _occurrences.FirstOrDefault(item => item.Offset > caret);
        if (marker is null) { return false; }
        // Changing the caret directly leaves AvaloniaEdit's actual Selection intact.
        _view.Editor.CaretOffset = marker.Offset;
        _view.Editor.ScrollToLine(_view.Editor.Document.GetLineByOffset(marker.Offset).LineNumber);
        return true;
    }

    public bool HandleKeyDown(KeyEventArgs args)
    {
        if (args.Handled || !IsAvailable) { return false; }
        if (args.Key == Key.F && args.KeyModifiers == KeyModifiers.Control) { args.Handled = true; ShowFind(); return true; }
        if (args.Key == Key.H && args.KeyModifiers == KeyModifiers.Control && CanReplace) { args.Handled = true; ShowFind(replaceMode: true); return true; }
        if (args.Key == Key.F3 && args.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            args.Handled = true;
            bool backwards = args.KeyModifiers == KeyModifiers.Shift;
            PendingOperation = FindFromCommandAsync(backwards, viaF3: true); return true;
        }
        if (args.KeyModifiers == KeyModifiers.Alt && args.Key is Key.Left or Key.Right)
        { args.Handled = true; if (args.Key == Key.Left) { GoToPreviousOccurrence(); } else { GoToNextOccurrence(); } return true; }
        return false;
    }

    internal void FindFromDialog(bool backwards) => PendingOperation = FindFromCommandAsync(backwards, viaF3: false);
    internal void ReplaceFromDialog() => PendingOperation = ReplaceFromCommandAsync();
    private async Task ReplaceFromCommandAsync()
    {
        DiffViewerSearchMatch? match = ReplaceNext();
        if (match is null && CanReplace && _dialog is { IsVisible: true } dialog)
        { await ShowMessageAsync(dialog, Query.Length == 0 ? T("_noSearchString", "No string specified to look for!") : T("_textNotFoundString", "Text not found"), " "); }
    }
    internal void ReplaceAllFromDialog() => PendingOperation = ReplaceAllFromCommandAsync();
    private async Task ReplaceAllFromCommandAsync()
    {
        if (!CanReplace || _dialog is not { IsVisible: true } dialog) { return; }
        int count = ReplaceAll();
        string message = count == 0 ? T("_noOccurrencesFoundString", "No occurrences found.")
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, T("_replacedOccurrencesString", "Replaced {0} occurrences."), count);
        await ShowMessageAsync(dialog, message, _translations.Translate("Information"));
        if (count > 0 && dialog.IsVisible) { dialog.HideForReuse(); }
    }
    internal void ShowNotFound(Window owner) => PendingOperation = ShowMessageAsync(owner, T("_textNotFoundString2", "Search text not found."), T("_notFoundString", "Not found"));
    private async Task FindFromCommandAsync(bool backwards, bool viaF3)
    {
        if (viaF3 && !backwards && Query.Length == 0 && OpenDiffToolRequested is { } open)
        {
            try { await open(); }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            { if (IsAvailable && TopLevel.GetTopLevel(_view) is Window { IsVisible: true } owner) { await Dialogs.ShowErrorAsync(owner, exception.Message); } }
            return;
        }
        DiffViewerSearchMatch? match = FindNext(backwards, viaF3);
        if (match is null && IsAvailable && TopLevel.GetTopLevel(_view) is Window { IsVisible: true } parent)
        {
            string message = Query.Length == 0 ? T("_noSearchString", "No string specified to look for!") : T("_textNotFoundString", "Text not found");
            await ShowMessageAsync(_dialog is { IsVisible: true } dialog ? dialog : parent, message, Query.Length == 0 ? T("_findString", "Find") : " ");
        }
    }
    private async Task ShowMessageAsync(Window owner, string message, string title)
    {
        if (_notification is { IsVisible: true } active) { active.Activate(); return; }
        Window dialog = new() { Name = "DiffViewerSearchInformation", Title = title, Width = 360, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = _view.ActualThemeVariant };
        Button ok = new() { Content = _translations.Translate("OK"), Width = 75, Height = 24, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 10, Children =
        { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok } } } };
        _notification = dialog;
        try { await dialog.ShowDialog(owner); }
        finally { if (ReferenceEquals(_notification, dialog)) { _notification = null; } }
    }

    private void SelectionChanged(object? sender, EventArgs args)
    {
        if (_view.IsUpdatingText || !IsAvailable) { return; }
        if (_highlights.Count > 0) { _highlights.Clear(); Notify(); }
        RebuildOccurrences();
    }
    private void RebuildOccurrences()
    {
        string selected = _view.Editor.SelectedText;
        if (selected == _selectedText) { return; }
        _selectedText = selected; _occurrences.Clear(); _highlights.Clear();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            int offset = -1;
            while ((offset = _view.Text.IndexOf(selected, offset + 1, StringComparison.OrdinalIgnoreCase)) >= 0)
            { _occurrences.Add(new(offset, selected.Length)); }
        }
        Notify();
    }

    internal void Suspend()
    {
        if (_disposed) { return; }
        _suspended = true; _selectedText = ""; ScanRegion = null;
        _occurrences.Clear(); _highlights.Clear(); _notification?.Close(); _notification = null; ReleaseDialog(); Notify();
    }
    internal void DocumentUpdated(bool changed)
    {
        if (_disposed) { return; }
        if (changed) { ScanRegion = null; _selectedText = ""; _occurrences.Clear(); _highlights.Clear(); }
        _suspended = false; RebuildOccurrences(); Notify();
    }
    private void DocumentChanged(object? sender, DocumentChangeEventArgs args)
    {
        if (_view.IsReadOnly || _view.IsUpdatingText || ScanRegion is not { } region) { return; }
        int start = MapOffset(region.Offset, afterInsertion: false);
        int end = MapOffset(region.Offset + region.Length, afterInsertion: true);
        ScanRegion = new(start, Math.Max(0, end - start));
        int MapOffset(int position, bool afterInsertion)
        {
            if (position < args.Offset || position == args.Offset && !afterInsertion) { return position; }
            if (position >= args.Offset + args.RemovalLength) { return position + args.InsertionLength - args.RemovalLength; }
            return args.Offset + (afterInsertion ? args.InsertionLength : 0);
        }
    }
    internal void DocumentEdited()
    {
        if (_disposed) { return; }
        _selectedText = ""; _occurrences.Clear(); _highlights.Clear();
        RebuildOccurrences(); Notify();
    }
    internal void EditabilityChanged() => Notify();
    internal void ThemeChanged()
    {
        UpdateColors();
        if (_dialog is not null) { _dialog.RequestedThemeVariant = _view.ActualThemeVariant; }
        Notify();
    }
    internal IEnumerable<DiffViewerSearchMarker> Markers(int begin = 0, int end = int.MaxValue)
    {
        if (!IsAvailable) { yield break; }
        if (ScanRegion is { } region && region.Offset < end && region.Offset + region.Length > begin) { yield return new(region.Offset, region.Length, DiffViewerSearchMarkerKind.Region); }
        foreach (DiffViewerSearchMatch item in Within(_occurrences, begin, end)) { yield return new(item.Offset, item.Length, DiffViewerSearchMarkerKind.Occurrence); }
        foreach (DiffViewerSearchMatch item in Within(_highlights, begin, end)) { yield return new(item.Offset, item.Length, DiffViewerSearchMarkerKind.Highlight); }
    }
    private static IEnumerable<DiffViewerSearchMatch> Within(List<DiffViewerSearchMatch> matches, int begin, int end)
    {
        if (matches.Count == 0) { yield break; }
        int minimum = Math.Max(0, begin - matches[0].Length + 1), low = 0, high = matches.Count;
        while (low < high) { int middle = (low + high) / 2; if (matches[middle].Offset < minimum) { low = middle + 1; } else { high = middle; } }
        for (int i = low; i < matches.Count && matches[i].Offset < end; i++) { yield return matches[i]; }
    }
    internal GitDiffColor Background(DiffViewerSearchMarkerKind kind) => _backgrounds[(int)kind];
    internal GitDiffColor Foreground(DiffViewerSearchMarkerKind kind) => _foregrounds[(int)kind];
    private void UpdateColors()
    {
        IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(_view.ActualThemeVariant == ThemeVariant.Dark);
        foreach (DiffViewerSearchMarkerKind kind in Enum.GetValues<DiffViewerSearchMarkerKind>())
        {
            _backgrounds[(int)kind] = kind switch
            {
                DiffViewerSearchMarkerKind.Occurrence => Read(colors["HighlightAllOccurences"]),
                DiffViewerSearchMarkerKind.Region => GitDiffColorMath.Dim(DiffViewerSearchColors.Adapt(new(160, 160, 160), colors), Read(colors["EditorBackground"])),
                _ => DiffViewerSearchColors.Adapt(new(255, 255, 0), colors)
            };
            _foregrounds[(int)kind] = GitDiffColorMath.TextColor(_backgrounds[(int)kind], Read(colors["WindowText"]));
        }
    }
    private static GitDiffColor Read(string color) { Color value = Color.Parse(color); return new(value.R, value.G, value.B); }
    private void Notify() { _view.Editor.TextArea.TextView.Redraw(); Changed?.Invoke(this, EventArgs.Empty); }
    private string T(string key, string source) => _translations.Translate("FindAndReplaceForm", key, "Text", source);
    private void Detached(object? sender, Avalonia.VisualTreeAttachmentEventArgs args) => Suspend();
    private void Attached(object? sender, Avalonia.VisualTreeAttachmentEventArgs args)
    {
        if (_disposed || !_view.IsDocumentReady) { return; }
        _suspended = false; RebuildOccurrences(); Notify();
    }
    internal void FocusViewer() => _view.Editor.TextArea.Focus();
    private void OwnerClosed(object? sender, EventArgs args) => Suspend();
    private void ReleaseDialog()
    {
        if (_owner is not null) { _owner.Closed -= OwnerClosed; _owner = null; }
        DiffViewerFindDialog? dialog = _dialog; _dialog = null; dialog?.Release();
    }
    public void Dispose()
    {
        if (_disposed) { return; }
        Suspend(); _disposed = true; OpenDiffToolRequested = null;
        _view.Editor.TextArea.SelectionChanged -= SelectionChanged;
        _view.Editor.Document.Changed -= DocumentChanged;
        _view.DetachedFromVisualTree -= Detached;
        _view.AttachedToVisualTree -= Attached;
        _caretOutsideSelection.Dispose();
    }
}

// RGB/HSL portion of ColorHelper.AdaptBackColor: use the original CSS/system
// pairs rather than choosing an unrelated dark search highlight.
internal static class DiffViewerSearchColors
{
    private readonly record struct Hsl(double H, double S, double L);
    public static GitDiffColor Adapt(GitDiffColor color, IReadOnlyDictionary<string, string> colors)
    {
        IReadOnlyDictionary<string, string> original = ClassicStyles.OriginalColors(false);
        string[] backgrounds = ["Window", "Control", "Info", "Highlight", "MenuHighlight"];
        string[] foregrounds = ["WindowText", "ControlText", "InfoText", "HighlightText", "HighlightText"];
        Hsl hsl = ToHsl(color);
        int index = Enumerable.Range(0, backgrounds.Length).MinBy(i => Math.Abs(Perceived(color, hsl.L) - Perceived(Read(original[backgrounds[i]]), ToHsl(Read(original[backgrounds[i]])).L))
            + .25 * Math.Abs(hsl.S - ToHsl(Read(original[backgrounds[i]])).S));
        GitDiffColor backOriginal = Read(original[backgrounds[index]]), foreOriginal = Read(original[foregrounds[index]]);
        GitDiffColor back = Read(colors[backgrounds[index]]), fore = Read(colors[foregrounds[index]]);
        double light = Transform(Perceived(color, hsl.L), Perceived(backOriginal, ToHsl(backOriginal).L), Perceived(foreOriginal, ToHsl(foreOriginal).L),
            Perceived(back, ToHsl(back).L), Perceived(fore, ToHsl(fore).L));
        return FromHsl(hsl with { L = GammaTransform(light, 1 / Gamma(color)) });
    }
    private static GitDiffColor Read(string value) { Color color = Color.Parse(value); return new(color.R, color.G, color.B); }
    private static double Gamma(GitDiffColor color) => color.R == color.G && color.G == color.B ? 1 : (color.R + color.G + color.B) / (color.R * .8 + color.G * 1.75 + color.B * .45);
    private static double Perceived(GitDiffColor color, double light) => GammaTransform(light, Gamma(color));
    private static double GammaTransform(double light, double gamma) => light < gamma / (gamma + 1) ? light / gamma : 1 + gamma * (light - 1);
    private static double Transform(double light, double backOriginal, double foreOriginal, double back, double fore)
    {
        if (foreOriginal < backOriginal && backOriginal < light) { foreOriginal = 1; fore = fore < back ? 1 : 0; }
        else if (light < backOriginal && backOriginal < foreOriginal) { foreOriginal = 0; fore = back < fore ? 0 : 1; }
        if (Math.Abs(backOriginal - foreOriginal) < .01) { foreOriginal = Math.Clamp(foreOriginal - .2, 0, 1); backOriginal = Math.Clamp(backOriginal + .2, 0, 1); }
        return Math.Clamp(fore + (light - foreOriginal) / (backOriginal - foreOriginal) * (back - fore), 0, 1);
    }
    private static Hsl ToHsl(GitDiffColor color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), light = (max + min) / 2;
        if (max == min) { return new(0, 0, light); }
        double delta = max - min, saturation = light <= .5 ? delta / (max + min) : delta / (2 - max - min);
        double hue = max == r ? (g - b) / delta + (g < b ? 6 : 0) : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        return new(hue / 6, saturation, light);
    }
    private static GitDiffColor FromHsl(Hsl color)
    {
        if (color.S == 0) { byte value = Channel(color.L); return new(value, value, value); }
        double q = color.L <= .5 ? color.L * (1 + color.S) : color.L + color.S - color.L * color.S, p = 2 * color.L - q;
        return new(Channel(Hue(color.H + 1 / 3d)), Channel(Hue(color.H)), Channel(Hue(color.H - 1 / 3d)));
        double Hue(double value)
        {
            if (value < 0) { value++; } if (value > 1) { value--; }
            return value < 1 / 6d ? p + (q - p) * 6 * value : value < .5 ? q : value < 2 / 3d ? p + (q - p) * (2 / 3d - value) * 6 : p;
        }
        static byte Channel(double value) => (byte)Math.Clamp((int)Math.Floor(value * 256), 0, 255);
    }
}

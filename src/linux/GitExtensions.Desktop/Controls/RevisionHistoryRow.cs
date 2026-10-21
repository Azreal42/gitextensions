using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Plugins;
using SmartFormat;
using GitExtensions.Linux.Desktop;

namespace GitExtensions.Desktop.Controls;

/// <summary>The compact original revision-grid columns: inline references and subject, author, date and hash.</summary>
public static class RevisionHistoryRow
{
    internal sealed record Presentation(bool ShowAvatar, bool ShowAuthor, bool ShowDate, bool ShowId, bool RelativeDate,
        bool ShowNotesColumn, DateTimeOffset Date, string Details, string Notes, bool Dim, IReadOnlyList<GitReference> References, bool FillRefLabels = false);

    internal static void ApplyPresentation(Control row, Presentation presentation)
    {
        if (row is HistoryRow historyRow) { historyRow.ApplyPresentation(presentation); }
    }
    internal static string? CommitHash(Control row) => (row as HistoryRow)?.CommitHash;
    internal static void ApplyProviderBuildStatus(Control row, BuildServerCommitStatus? status, bool showIcon, bool showText)
    { if (row is HistoryRow historyRow) { historyRow.ApplyBuildStatus(status, showIcon, showText); } }
    internal static void ApplyBuildStatus(Control row, GitHubActionsCommitStatus? status, bool showIcon, bool showText)
        => ApplyProviderBuildStatus(row, status is null ? null : BuildServerCommitStatus.FromGitHubActions(status), showIcon, showText);
    private static TranslationCatalog? _translations;
    private static readonly Dictionary<string, Bitmap> BuildImages = [];
    private static Bitmap BuildImage(string name)
    {
        if (!BuildImages.TryGetValue(name, out Bitmap? image))
        { using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/" + name)); BuildImages.Add(name, image = new Bitmap(stream)); }
        return image;
    }

    public static Control Create(GitCommit? commit, IReadOnlyList<GitReference> references, bool relativeDates = true)
    {
        ArgumentNullException.ThrowIfNull(references);
        HistoryRow row = new(commit);
        if (commit is null) { return row; }
        GitReference[] refs = references.Where(reference => reference.RevisionHash == commit.Hash).DistinctBy(reference => reference.Name, StringComparer.Ordinal)
            .OrderBy(reference => reference.IsCurrent ? 0 : reference.IsBranch ? 1 : reference.IsRemote ? 2 : 3)
            .ThenBy(reference => reference.Name, StringComparer.Ordinal).ToArray();
        MessageCell message = new(commit.Subject, commit.Body, refs) { Name = "RevisionMessage", Margin = new Thickness(4, 0) };
        if (refs.Any(reference => reference.IsCurrent)) { message.Subject.FontWeight = FontWeight.Bold; }
        row.Children.Add(message);
        Grid author = new() { Name = "RevisionAuthor", ColumnDefinitions = new ColumnDefinitions("20,*") };
        AvatarImageControl avatar = new(commit.AuthorEmail, commit.AuthorName)
        { Name = "RevisionAuthorAvatar", Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        author.Children.Add(avatar);
        TextBlock authorName = Cell("RevisionAuthorName", commit.AuthorName);
        Grid.SetColumn(authorName, 1); author.Children.Add(authorName);
        ToolTip.SetTip(author, commit.AuthorName + " <" + commit.AuthorEmail + ">\n" + commit.AuthoredAt.LocalDateTime.ToString("G", CultureInfo.CurrentCulture));
        Grid.SetColumn(author, 1); row.Children.Add(author);
        TextBlock date = Cell("RevisionDate", relativeDates ? FormatRelativeDate(commit.AuthoredAt, DateTimeOffset.Now)
            : commit.AuthoredAt.LocalDateTime.ToString("G", CultureInfo.CurrentCulture));
        ToolTip.SetTip(date, commit.AuthoredAt.LocalDateTime.ToString("G", CultureInfo.CurrentCulture));
        Grid.SetColumn(date, 2); row.Children.Add(date);
        TextBlock hash = Cell("RevisionHash", commit.Hash[..Math.Min(9, commit.Hash.Length)]);
        DesktopFonts.Apply(hash, DesktopFontRole.Monospace);
        ToolTip.SetTip(hash, commit.Hash); Grid.SetColumn(hash, 3); row.Children.Add(hash);
        row.TextCells = [message.Subject, authorName, date, hash];
        row.AuthorName = authorName;
        return row;
    }

    public static void UpdateState(Control row, bool selected, bool selectionFocused, string? authoredEmail, int rowIndex, bool alternateBackground = true)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row is not HistoryRow historyRow) { throw new ArgumentException("The control must be created by RevisionHistoryRow.Create.", nameof(row)); }
        historyRow.UpdateState(selected, selectionFocused, authoredEmail, rowIndex, alternateBackground);
    }

    private static readonly IBrush[] LightBackgrounds =
    [Brush.Parse("#ffffff"), Brush.Parse("#f9f9f9"), Brush.Parse("#eaf1ff"), Brush.Parse("#e6e6e6"), Brush.Parse("#0078d7")];
    private static readonly IBrush[] DarkBackgrounds =
    [Brush.Parse("#323232"), Brush.Parse("#363636"), Brush.Parse("#393920"), Brush.Parse("#436689"), Brush.Parse("#2864b4")];
    private static readonly IBrush DarkText = Brush.Parse("#f0f0f0");

    internal static IBrush BackgroundFor(GitCommit? commit, bool selected, bool selectionFocused, string? authoredEmail,
        int rowIndex, bool alternateBackground, bool dark)
    {
        // RevisionDataGridView.GetBackground. Alternating colors use the original MakeDarkerBy / HslColor floor*256 conversion.
        int index = selected ? selectionFocused ? 4 : 3 : IsAuthored(commit, authoredEmail) ? 2 : alternateBackground && rowIndex % 2 == 0 ? 1 : 0;
        return (dark ? DarkBackgrounds : LightBackgrounds)[index];
    }

    // RevisionDataGridView.GetForeground and GetCommitBodyForeground use separate state tables.
    // Classic ControlText/HighlightText/GrayText map to the existing Light/Dark text palette.
    private static readonly IBrush NonRelativeDarkSubject = Brush.Parse("#c0c0c0");
    private static readonly IBrush NonRelativeSelectedLightSubject = Brush.Parse("#bcbcbc");
    private static readonly IBrush NonRelativeSelectedDarkSubject = Brush.Parse("#ebebd7");
    private static readonly IBrush RelativeSelectedLightBody = Brush.Parse("#bcbcbc");
    private static readonly IBrush SelectedDarkBody = Brush.Parse("#aaaa96");
    private static readonly IBrush NonRelativeLightBody = Brush.Parse("#989898");
    private static readonly IBrush NonRelativeDarkBody = Brush.Parse("#828282");
    private static readonly IBrush NonRelativeSelectedLightBody = Brush.Parse("#a1a1a1");

    internal static IBrush ForegroundFor(bool selected, bool selectionFocused, bool nonRelative, bool dark)
        => (nonRelative, selected && selectionFocused) switch
        {
            (false, false) => dark ? DarkText : Brushes.Black,
            (false, true) => dark ? DarkText : Brushes.White,
            (true, false) => dark ? NonRelativeDarkSubject : Brushes.Gray,
            (true, true) => dark ? NonRelativeSelectedDarkSubject : NonRelativeSelectedLightSubject
        };

    internal static IBrush CommitBodyForegroundFor(bool selected, bool nonRelative, bool dark)
        => (nonRelative, selected) switch
        {
            (false, false) => Brushes.Gray,
            (false, true) => dark ? SelectedDarkBody : RelativeSelectedLightBody,
            (true, false) => dark ? NonRelativeDarkBody : NonRelativeLightBody,
            (true, true) => dark ? SelectedDarkBody : NonRelativeSelectedLightBody
        };

    private static bool IsAuthored(GitCommit? commit, string? authoredEmail) => commit is not null && !string.IsNullOrWhiteSpace(commit.AuthorEmail)
        && string.Equals(commit.AuthorEmail, authoredEmail, StringComparison.OrdinalIgnoreCase);

    private sealed class HistoryRow : Grid
    {
        private readonly GitCommit? _commit;
        private readonly ResolvedDesktopFont _application = DesktopFonts.For(DesktopFontRole.Application);
        private bool _hasState, _selected, _selectionFocused, _alternateBackground;
        private string? _authoredEmail;
        private int _rowIndex;
        public TextBlock[] TextCells { get; set; } = [];
        public TextBlock? AuthorName { get; set; }
        private bool _dim;
        private TextBlock? _notes;
        private TextBlock? _build;
        private Image? _buildIcon;
        private StackPanel? _buildCell;
        private (BuildServerBuildStatus? Status, string Text, string Tooltip, bool Icon, bool Label)? _buildPresentation;
        private BuildServerCommitStatus? _buildStatus;
        internal string? CommitHash => _commit?.Hash;

        internal void ApplyBuildStatus(BuildServerCommitStatus? status, bool showIcon, bool showText)
        {
            _buildStatus = status;
            if (_build is null && !showIcon && !showText) { return; }
            (BuildServerBuildStatus? Status, string Text, string Tooltip, bool Icon, bool Label) presentation = (status?.Status, status?.Text ?? "", status?.Tooltip ?? "", showIcon, showText);
            if (_buildPresentation == presentation) { return; } _buildPresentation = presentation;
            if (_build is null)
            {
                // Keep notes at column4, even if CI is activated before Notes. Existing rows remain the same objects.
                while (ColumnDefinitions.Count < 6) { ColumnDefinitions.Add(new ColumnDefinition(0, GridUnitType.Pixel)); }
                _build = Cell("RevisionBuildStatus", "");
                _buildIcon = new() { Name = "RevisionBuildStatusIcon", Width = 16, Height = 16, Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center };
                _buildCell = new() { Name = "RevisionBuildStatusCell", Orientation = Orientation.Horizontal, Spacing = 2, Children = { _buildIcon, _build } };
                Grid.SetColumn(_buildCell, 5); Children.Add(_buildCell);
            }
            string icon = status?.Status switch
            { BuildServerBuildStatus.Success => "StatusBadgeSuccess.png", BuildServerBuildStatus.Failure => "StatusBadgeError.png",
              BuildServerBuildStatus.InProgress => "StatusBadgeWaiting.png", BuildServerBuildStatus.Stopped => "BisectStop.png", _ => "Warning.png" };
            if (status is not null) { _buildIcon!.Source = BuildImage(icon); _buildIcon.Tag = icon; }
            _buildIcon!.IsVisible = showIcon && status is not null;
            _build.Text = showText ? status?.Text ?? "" : ""; _build.IsVisible = showText;
            _buildCell!.IsVisible = showIcon || showText; ToolTip.SetTip(_buildCell, status?.Tooltip);
            ColumnDefinitions[5].Width = new GridLength(showText ? 150 : showIcon ? 16 : 0);
            ApplyBuildColor();
        }
        private void ApplyBuildColor()
        {
            if (_build is null) { return; }
            bool bright = _selected || ActualThemeVariant == ThemeVariant.Dark;
            _build.Foreground = _buildStatus?.Status switch
            {
                BuildServerBuildStatus.Success => bright ? Brushes.LightGreen : Brushes.DarkGreen,
                BuildServerBuildStatus.Failure => bright ? Brushes.OrangeRed : Brushes.DarkRed,
                BuildServerBuildStatus.InProgress => bright ? Brushes.LightBlue : Brushes.Blue,
                BuildServerBuildStatus.Unstable => Brushes.OrangeRed,
                _ => bright ? Brushes.LightGray : Brushes.Gray
            };
        }

        public void ApplyPresentation(Presentation value)
        {
            if (_commit is null) { return; }
            new RevisionHistoryColumns(value.ShowAvatar, value.ShowAuthor, value.ShowDate, value.ShowId, value.ShowNotesColumn).Apply(ColumnDefinitions);
            Grid author = Children.OfType<Grid>().Single();
            author.Children[0].IsVisible = value.ShowAvatar;
            author.Children[1].IsVisible = value.ShowAuthor;
            author.ColumnDefinitions[0].Width = new GridLength(value.ShowAvatar ? 20 : 0);
            TextBlock date = Children.OfType<TextBlock>().Single(c => c.Name == "RevisionDate");
            date.IsVisible = value.ShowDate;
            date.Text = value.RelativeDate ? FormatRelativeDate(value.Date, DateTimeOffset.Now) : value.Date.LocalDateTime.ToString("G", CultureInfo.CurrentCulture);
            ToolTip.SetTip(date, value.Date.LocalDateTime.ToString("G", CultureInfo.CurrentCulture));
            Children.OfType<TextBlock>().Single(c => c.Name == "RevisionHash").IsVisible = value.ShowId;
            if (value.ShowNotesColumn && _notes is null)
            {
                _notes = Cell("RevisionNotes", ""); if (ColumnDefinitions.Count < 5) { ColumnDefinitions.Add(new ColumnDefinition(220, GridUnitType.Pixel)); }
                Grid.SetColumn(_notes, 4); Children.Add(_notes); TextCells = [.. TextCells, _notes];
            }
            if (_notes is not null)
            {
                _notes.Text = string.Join(' ', value.Notes.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
                ToolTip.SetTip(_notes, value.Notes); _notes.IsVisible = value.ShowNotesColumn;
            }
            MessageCell message = Children.OfType<MessageCell>().Single();
            message.SetPresentation(value.Details, value.References.Where(r => r.RevisionHash == _commit.Hash).DistinctBy(r => r.Name, StringComparer.Ordinal)
                .OrderBy(r => r.IsCurrent ? 0 : r.IsBranch ? 1 : r.IsRemote ? 2 : 3).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray(), value.FillRefLabels);
            _dim = value.Dim; ApplyState();
        }

        public HistoryRow(GitCommit? commit)
        {
            _commit = commit; Name = "RevisionHistoryRow";
            ColumnDefinitions = new RevisionHistoryColumns().CreateDefinitions();
            Height = DesktopFontMetrics.HistoryContentHeight(DesktopFonts.Active); ClipToBounds = true;
            ActualThemeVariantChanged += (_, _) => ApplyState();
            AttachedToVisualTree += (_, _) => ApplyState();
        }

        public void UpdateState(bool selected, bool selectionFocused, string? authoredEmail, int rowIndex, bool alternateBackground)
        {
            _hasState = true; _selected = selected; _selectionFocused = selectionFocused; _authoredEmail = authoredEmail;
            _rowIndex = rowIndex; _alternateBackground = alternateBackground; ApplyState();
        }

        private void ApplyState()
        {
            if (!_hasState) { return; }
            bool dark = ActualThemeVariant == ThemeVariant.Dark;
            Background = BackgroundFor(_commit, _selected, _selectionFocused, _authoredEmail, _rowIndex, _alternateBackground, dark);
            // The host row is 20px, while its content retains the original compact 18px. Fill both consistently.
            if (this.FindAncestorOfType<ListBoxItem>() is { } item) { item.Background = Background; }
            IBrush foreground = ForegroundFor(_selected, _selectionFocused, _dim, dark);
            foreach (TextBlock text in TextCells) { text.Foreground = foreground; }
            foreach (MessageCell message in Children.OfType<MessageCell>())
            { message.Details.Foreground = CommitBodyForegroundFor(_selected, _dim, dark); }
            if (AuthorName is not null) { AuthorName.FontWeight = IsAuthored(_commit, _authoredEmail) ? FontWeight.Bold : _application.Weight; }
            ApplyBuildColor();
        }
    }

    private static TextBlock Cell(string name, string value)
    {
        TextBlock text = new()
        {
            Name = name, Text = value, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0)
        };
        DesktopFonts.Apply(text, DesktopFontRole.Application);
        return text;
    }

    internal static string FormatRelativeDate(DateTimeOffset date, DateTimeOffset now, TranslationCatalog? translations = null)
    {
        if (date == DateTimeOffset.MinValue || date == DateTimeOffset.MaxValue) { return ""; }
        if (translations is null)
        {
            if (_translations?.CurrentCulture.Name != CultureInfo.CurrentUICulture.Name) { _translations = TranslationCatalog.LoadDefault(); }
            translations = _translations;
        }
        // LocalizationHelpers.GetRelativeDateString, with displayWeeks:false as DateColumnProvider uses.
        static DateTime Round(DateTime value) => new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second);
        TimeSpan elapsed = Round(now.LocalDateTime) - Round(date.LocalDateTime);
        double seconds = Math.Abs(elapsed.TotalSeconds);
        (string Item, string Unit, int Count) value = seconds < 60 ? ("_secondsAgo", "second|seconds", elapsed.Seconds)
            : seconds < 45 * 60 ? ("_minutesAgo", "minute|minutes", elapsed.Minutes)
            : seconds < 24 * 60 * 60 ? ("_hoursAgo", "hour|hours", seconds < 60 * 60 ? Math.Sign(elapsed.Minutes) : elapsed.Hours)
            : seconds < 30 * 24 * 60 * 60 ? ("_daysAgo", "day|days", elapsed.Days)
            : seconds < 365 * 24 * 60 * 60 ? ("_monthsAgo", "month|months", Convert.ToInt32(elapsed.Days / 30.0))
            : ("_yearsAgo", "year|years", Convert.ToInt32(elapsed.Days / 365.0));
        string source = "{0} {1:" + value.Unit + "} ago";
        string template = translations!.Translate("TranslatedStrings", value.Item, "Text", source);
        return Smart.Format(translations.CurrentCulture, template, value.Count, Math.Abs(value.Count));
    }

    private sealed class MessageCell : Panel
    {
        private readonly string _body;
        private readonly double _height = DesktopFontMetrics.HistoryContentHeight(DesktopFonts.Active);
        private ReferenceBadge[] _badges;
        private GitReference[] _references;
        private double[] _widths = [];
        private double _subjectWidth;
        public TextBlock Subject { get; }
        public TextBlock Details { get; }
        public MessageCell(string subject, string body, GitReference[] refs)
        {
            ClipToBounds = true;
            _body = body;
            _references = refs; _badges = refs.Select(reference => new ReferenceBadge(reference)).ToArray();
            foreach (ReferenceBadge badge in _badges) { Children.Add(badge); }
            Subject = Cell("RevisionSubject", subject); Subject.Margin = default; Children.Add(Subject);
            Details = Cell("RevisionMessageDetails", ""); Details.Margin = default; Details.Foreground = Brushes.Gray; Children.Add(Details);
            SetPresentation("", refs);
        }
        public void SetPresentation(string details, GitReference[] references, bool fillRefLabels = false)
        {
            Details.Text = details.Length == 0 ? "" : "  " + details;
            ToolTip.SetTip(this, string.Join('\n', references.Select(reference => reference.ShortName)
                .Append(Subject.Text!).Append(_body).Where(text => text.Length > 0)));
            if (_references.SequenceEqual(references))
            { foreach (ReferenceBadge badge in _badges) { badge.SetFilled(fillRefLabels); } return; }
            foreach (ReferenceBadge badge in _badges) { Children.Remove(badge); }
            _references = references; _badges = references.Select(r => new ReferenceBadge(r)).ToArray();
            foreach (ReferenceBadge badge in _badges) { badge.SetFilled(fillRefLabels); }
            foreach (ReferenceBadge badge in _badges) { Children.Add(badge); }
            InvalidateMeasure();
        }
        protected override Size MeasureOverride(Size availableSize)
        {
            _widths = new double[_badges.Length];
            for (int index = 0; index < _badges.Length; index++)
            { _badges[index].Measure(new Size(double.PositiveInfinity, _height)); _widths[index] = _badges[index].DesiredSize.Width; }
            Subject.Measure(new Size(double.PositiveInfinity, _height)); _subjectWidth = Subject.DesiredSize.Width;
            Details.Measure(new Size(double.PositiveInfinity, _height));
            double natural = _widths.Sum() + _badges.Length * 5 + _subjectWidth + Details.DesiredSize.Width;
            double width = double.IsFinite(availableSize.Width) ? availableSize.Width : natural;
            ArrangeChildren(width, measureOnly: true);
            return new(Math.Min(natural, width), _height);
        }
        protected override Size ArrangeOverride(Size finalSize)
        { ArrangeChildren(finalSize.Width, measureOnly: false); return finalSize; }
        private void ArrangeChildren(double width, bool measureOnly)
        {
            double gaps = Math.Min(width, _badges.Length * 5);
            double reserveSubject = Math.Min(_subjectWidth, Math.Min(160, width * .3));
            double budget = Math.Max(0, width - gaps - reserveSubject);
            double total = _widths.Sum();
            double ratio = total > 0 ? Math.Min(1, budget / total) : 0;
            double x = 0;
            for (int index = 0; index < _badges.Length; index++)
            {
                double badgeWidth = _widths[index] * ratio;
                if (measureOnly) { _badges[index].Measure(new Size(badgeWidth, _height)); }
                else { _badges[index].Arrange(new Rect(x, 0, badgeWidth, _height)); }
                x += badgeWidth + (_badges.Length == 0 ? 0 : gaps / _badges.Length);
            }
            double subjectWidth = Math.Max(0, width - x);
            double textWidth = Math.Min(subjectWidth, _subjectWidth);
            if (measureOnly) { Subject.Measure(new Size(subjectWidth, _height)); }
            else
            {
                Subject.Arrange(new Rect(x, 0, textWidth, _height));
                Details.Arrange(new Rect(x + textWidth, 0, Math.Max(0, subjectWidth - textWidth), _height));
            }
        }
    }

    private sealed class ReferenceBadge : Border
    {
        private readonly GitReference _reference;
        private readonly TextBlock _label;
        private readonly CurrentBranchArrow? _arrow;
        private ListBoxItem? _selectionOwner;
        private bool _filled;
        internal void SetFilled(bool filled)
        { if (_filled == filled) { return; } _filled = filled; UpdatePalette(); }
        public ReferenceBadge(GitReference reference)
        {
            _reference = reference; Name = "RevisionReference"; Tag = reference;
            Height = DesktopFontMetrics.HistoryContentHeight(DesktopFonts.Active); BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(5);
            Padding = new Thickness(3, 0); ClipToBounds = true;
            _label = Cell("RevisionReferenceLabel", reference.ShortName); _label.Margin = default;
            _label.FontWeight = reference.IsCurrent ? FontWeight.Bold : DesktopFonts.For(DesktopFontRole.Application).Weight;
            Grid content = new() { ColumnDefinitions = new ColumnDefinitions(reference.IsCurrent ? "9,*" : "*") };
            if (reference.IsCurrent)
            {
                _arrow = new() { Name = "RevisionCurrentBranchArrow", Width = 9, Height = 16, VerticalAlignment = VerticalAlignment.Center };
                content.Children.Add(_arrow); Grid.SetColumn(_label, 1);
            }
            content.Children.Add(_label); Child = content; ToolTip.SetTip(this, reference.ShortName);
            ActualThemeVariantChanged += (_, _) => UpdatePalette();
            AttachedToVisualTree += (_, _) =>
            {
                _selectionOwner = this.FindAncestorOfType<ListBoxItem>();
                if (_selectionOwner is not null) { _selectionOwner.PropertyChanged += SelectionChanged; }
                UpdatePalette();
            };
            DetachedFromVisualTree += (_, _) =>
            {
                if (_selectionOwner is not null) { _selectionOwner.PropertyChanged -= SelectionChanged; }
                _selectionOwner = null;
            };
            UpdatePalette();
        }
        private void SelectionChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        { if (args.Property == ListBoxItem.IsSelectedProperty) { UpdatePalette(); } }
        private void UpdatePalette()
        {
            bool dark = ActualThemeVariant == ThemeVariant.Dark;
            Color color = CommitGraph.ReferenceColor(_reference, dark);
            Color window = Color.Parse(dark ? "#323232" : "#ffffff");
            // RevisionGridRefRenderer.DrawRefBackground: same fill gradient and frame interpolation.
            _label.Foreground = new SolidColorBrush(_filled ? color : Lerp(color, Colors.Black, .25f));
            BorderBrush = new SolidColorBrush(Lerp(color, window, _filled ? .83f : .5f));
            if (_filled)
            {
                Color top = Lerp(color, window, .92f), bottom = Lerp(top, window, .9f);
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = new GradientStops { new GradientStop(top, 0), new GradientStop(bottom, 1) }
                };
            }
            else { Background = _selectionOwner?.IsSelected == true ? new SolidColorBrush(window) : Brushes.Transparent; }
            if (_arrow is not null) { _arrow.Brush = new SolidColorBrush(color); }
        }
        private static Color Lerp(Color from, Color to, float amount) => Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * amount), (byte)(from.G + (to.G - from.G) * amount), (byte)(from.B + (to.B - from.B) * amount));
    }

    private sealed class CurrentBranchArrow : Control
    {
        private IBrush _brush = Brushes.Green;
        public IBrush Brush { get => _brush; set { _brush = value; InvalidateVisual(); } }
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            // The original current-ref icon is DrawArrow's filled polygon, not a PNG.
            double height = Math.Max(0, Bounds.Height - 6), width = height / 2;
            StreamGeometry geometry = new();
            using (StreamGeometryContext path = geometry.Open())
            { path.BeginFigure(new Point(1, 3), true); path.LineTo(new Point(1 + width, 3 + height / 2)); path.LineTo(new Point(1, 3 + height)); path.EndFigure(true); }
            context.DrawGeometry(_brush, null, geometry);
        }
    }

}

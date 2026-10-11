using System.Globalization;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Desktop.Controls;

public sealed class BlameView : UserControl, IDisposable
{
    private readonly FileHistoryContentView _content = new() { Name = "BlameCode" };
    private readonly AuthorMargin _authors;
    private readonly Bitmap? _avatar;
    private readonly IGourceAvatarProvider _avatarProvider;
    private readonly Dictionary<string, Bitmap> _avatars = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _avatarOperation;
    private bool _disposed;
    private bool _showAvatar = true;
    private GitFileBlame? _blame;
    private IBrush[] _ages = [];
    private int _selectedLine;
    public event EventHandler? SelectedLineChanged;
    public GitFileBlameLine? SelectedLine => _blame is not null && _selectedLine > 0 && _selectedLine <= _blame.Lines.Count ? _blame.Lines[_selectedLine - 1] : null;
    public IReadOnlyList<GitFileBlameLine> Lines => _blame?.Lines ?? [];
    public DiffView TextView => _content.TextView;
    public bool ShowAuthor { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowOriginalPath { get; set; } = true;
    public static IGourceAvatarProvider DefaultAvatarProvider { get; set; } = new GourceAvatarProvider();
    public Task AvatarLoading { get; private set; } = Task.CompletedTask;
    public bool ShowAvatar
    {
        get => _showAvatar;
        set
        {
            if (_showAvatar == value) { return; }
            _showAvatar = value;
            StartAvatarLoading();
            RefreshDisplay();
        }
    }

    public BlameView(IGourceAvatarProvider? avatarProvider = null)
    {
        _avatarProvider = avatarProvider ?? DefaultAvatarProvider;
        Uri image = new("avares://GitExtensions/Assets/Icons/User80.png");
        if (AssetLoader.Exists(image)) { using Stream stream = AssetLoader.Open(image); _avatar = new Bitmap(stream); }
        _authors = new(this) { Name = "BlameAuthorMargin", Width = 290 };
        TextView.Editor.TextArea.LeftMargins.Insert(0, _authors);
        TextView.Editor.TextArea.TextView.BackgroundRenderers.Add(new CommitBackground(this));
        TextView.Editor.TextArea.Caret.PositionChanged += (_, _) => SelectLine(TextView.Editor.TextArea.Caret.Line, moveCaret: false);
        ActualThemeVariantChanged += (_, _) => RefreshDisplay();
        AttachedToVisualTree += (_, _) => StartAvatarLoading();
        DetachedFromVisualTree += (_, _) => CancelAvatarLoading();
        Content = _content;
    }
    public void SetBlame(GitFileBlame blame, int initialLine = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelAvatarLoading();
        _blame = blame;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long oldest = Math.Min(now.AddYears(-3).Ticks, blame.Lines.Count == 0 ? now.AddYears(-3).Ticks : blame.Lines.Min(item => item.Line.AuthoredAt.Ticks));
        long interval = Math.Max(1, (now.Ticks - oldest + 1) / Ages.Length);
        _ages = blame.Lines.Select(line => Ages[(int)Math.Clamp((line.Line.AuthoredAt.Ticks - oldest) / interval, 0, Ages.Length - 1)]).ToArray();
        _content.ShowFile(blame.Path, blame.Bytes);
        SelectLine(Math.Clamp(initialLine, 1, Math.Max(1, blame.Lines.Count)));
        RefreshDisplay();
        StartAvatarLoading();
    }
    public void SelectLine(int number, bool moveCaret = true)
    {
        if (_blame is null || number < 1 || number > _blame.Lines.Count) { return; }
        bool changed = number != _selectedLine;
        _selectedLine = number;
        if (moveCaret)
        {
            TextView.CaretOffset = TextView.Editor.Document.GetLineByNumber(number).Offset;
            TextView.Editor.ScrollToLine(number);
        }
        RefreshDisplay();
        if (changed) { SelectedLineChanged?.Invoke(this, EventArgs.Empty); }
    }
    public void RefreshDisplay()
    {
        _authors.InvalidateVisual();
        TextView.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }
    private IBrush GroupBrush => Brush.Parse(ActualThemeVariant == ThemeVariant.Dark ? "#414141" : "#e3e3e3");
    private bool IsSelected(GitFileBlameLine line) => SelectedLine?.Line.Hash == line.Line.Hash;
    private static readonly IBrush[] Ages = new[] { "#f7fcf5", "#c7e9c0", "#a1d99b", "#74c476", "#41ab5d", "#238b45", "#00441b" }.Select(Brush.Parse).ToArray();
    public void CancelAvatarLoading() { _avatarOperation?.Cancel(); _avatarOperation = null; }
    private void StartAvatarLoading()
    {
        CancelAvatarLoading();
        if (_disposed || !_showAvatar || _blame is null || this.GetVisualRoot() is null) { return; }
        CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _avatarOperation = operation;
        int size = Math.Max(1, (int)Math.Ceiling(TextView.Editor.TextArea.TextView.DefaultLineHeight) + 1);
        GitBlameLine[] authors = _blame.Lines.Select(line => line.Line)
            .Where(line => !line.Hash.All(character => character == '0') && !string.IsNullOrWhiteSpace(line.AuthorEmail))
            .DistinctBy(line => line.AuthorEmail.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(line => !_avatars.ContainsKey(line.AuthorEmail.Trim())).ToArray();
        AvatarLoading = LoadAvatarsAsync(authors, size, operation);
    }
    private async Task LoadAvatarsAsync(GitBlameLine[] authors, int size, CancellationTokenSource operation)
    {
        CancellationToken token = operation.Token;
        try { await Task.WhenAll(authors.Select(LoadAsync)); }
        finally
        {
            if (_avatarOperation == operation) { _avatarOperation = null; }
            operation.Dispose();
        }
        async Task LoadAsync(GitBlameLine author)
        {
            try
            {
                byte[]? bytes = await _avatarProvider.GetAvatarPngAsync(author.AuthorEmail, author.AuthorName, size, token);
                token.ThrowIfCancellationRequested();
                if (bytes is null || _disposed) { return; }
                using MemoryStream stream = new(bytes, writable: false);
                Bitmap bitmap = new(stream);
                if (_avatars.TryGetValue(author.AuthorEmail.Trim(), out Bitmap? previous)) { previous.Dispose(); }
                _avatars[author.AuthorEmail.Trim()] = bitmap;
                _authors.InvalidateVisual();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or HttpRequestException)
            { /* An unavailable avatar must keep User80 without breaking the Git view. */ }
        }
    }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true; _lifetime.Cancel(); CancelAvatarLoading();
        _content.Dispose(); _avatar?.Dispose();
        foreach (Bitmap avatar in _avatars.Values) { avatar.Dispose(); }
        _avatars.Clear(); _lifetime.Dispose();
    }

    private sealed class AuthorMargin(BlameView owner) : AbstractMargin
    {
        protected override Size MeasureOverride(Size availableSize) => new(Width, 0);
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.DrawRectangle(owner.TextView.Editor.Background, null, new Rect(Bounds.Size));
            if (TextView?.VisualLinesValid != true) { return; }
            using (context.PushClip(new Rect(Bounds.Size)))
            foreach (VisualLine visual in TextView.VisualLines)
            {
                int index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= owner.Lines.Count) { continue; }
                GitFileBlameLine line = owner.Lines[index];
                double top = visual.VisualTop - TextView.VerticalOffset;
                if (owner.IsSelected(line)) { context.DrawRectangle(owner.GroupBrush, null, new Rect(0, top, Bounds.Width, visual.Height)); }
                context.DrawRectangle(owner._ages[index], null, new Rect(0, top, 4, visual.Height));
                bool first = index == 0 || owner.Lines[index - 1].Line.Hash != line.Line.Hash;
                if (!first) { continue; }
                double textY = visual.GetTextLineVisualYPosition(visual.TextLines[0], VisualYPosition.TextTop) - TextView.VerticalOffset;
                double avatarWidth = owner.ShowAvatar ? Math.Min(visual.Height, 20) : 0;
                Bitmap? avatar = owner._avatars.GetValueOrDefault(line.Line.AuthorEmail.Trim()) ?? owner._avatar;
                if (owner.ShowAvatar && avatar is not null)
                { context.DrawImage(avatar, new Rect(avatar.Size), new Rect(6, top, avatarWidth, avatarWidth)); }
                string text = string.Join(" - ", new[]
                {
                    owner.ShowDate ? line.Line.AuthoredAt.ToString("d", CultureInfo.CurrentCulture) : null,
                    owner.ShowAuthor ? line.Line.AuthorName : null,
                    owner.ShowOriginalPath && line.OriginalPath != owner._blame!.Path ? line.OriginalPath : null
                }.Where(value => value is not null));
                FormattedText author = new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(owner.TextView.Editor.FontFamily), owner.TextView.Editor.FontSize, owner.TextView.Editor.Foreground);
                context.DrawText(author, new Point(8 + avatarWidth, textY));
            }
        }
        protected override void OnPointerPressed(PointerPressedEventArgs args)
        {
            base.OnPointerPressed(args);
            if (TextView?.VisualLinesValid != true) { return; }
            double y = args.GetPosition(this).Y + TextView.VerticalOffset;
            VisualLine? line = TextView.VisualLines.FirstOrDefault(line => y >= line.VisualTop && y < line.VisualTop + line.Height);
            if (line is not null) { owner.SelectLine(line.FirstDocumentLine.LineNumber); args.Handled = true; }
        }
    }
    private sealed class CommitBackground(BlameView owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;
        public void Draw(TextView view, DrawingContext context)
        {
            if (!view.VisualLinesValid) { return; }
            foreach (VisualLine visual in view.VisualLines)
            {
                int index = visual.FirstDocumentLine.LineNumber - 1;
                if (index < owner.Lines.Count && owner.IsSelected(owner.Lines[index]))
                { context.DrawRectangle(owner.GroupBrush, null, new Rect(0, visual.VisualTop - view.VerticalOffset, view.Bounds.Width, visual.Height)); }
            }
        }
    }
}

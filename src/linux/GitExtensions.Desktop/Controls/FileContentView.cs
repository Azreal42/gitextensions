using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GitExtensions.Linux;

namespace GitExtensions.Desktop.Controls;

/// <summary>Original FileViewer behavior: first image frame, centered without enlargement, otherwise fit to the panel.</summary>
public sealed class FileContentView : UserControl, IDisposable
{
    private readonly TextBox _source;
    private readonly Image _image = new() { Name = "FileImage", Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, UseLayoutRounding = true };
    private Bitmap? _bitmap;
    private bool _settingImage;
    private bool _settingFile;
    private byte[]? _fileBytes;
    private string? _filePath;
    private string? _defaultEncodingName;
    private string _viewerDefaultEncodingName = "utf-8";
    private bool _truncated;
    public DiffView TextView { get; }
    public bool IsImage => _bitmap is not null;
    internal Bitmap? Bitmap => _bitmap;

    public FileContentView(TextBox source, DiffView? textView = null)
    {
        _source = source;
        TextView = textView ?? new DiffView(source);
        Content = TextView;
        _source.PropertyChanged += SourceChanged;
        TextView.FileEncodingChanged += FileEncodingChanged;
        TextView.DocumentLoading += DocumentLoading;
        SizeChanged += (_, _) => SizeImage();
    }

    private void SourceChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == TextBox.TextProperty && !_settingImage)
        {
            if (!_settingFile) { _fileBytes = null; _filePath = null; }
            ClearImage();
        }
    }

    public void ShowFile(string path, byte[] bytes, string? defaultEncodingName = null, bool truncated = false)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _filePath = path; _fileBytes = bytes.ToArray();
        _viewerDefaultEncodingName = TextView.DefaultFileEncodingName;
        _defaultEncodingName = defaultEncodingName ?? _viewerDefaultEncodingName; _truncated = truncated;
        RenderFile();
    }

    private void FileEncodingChanged(object? sender, EventArgs args)
    {
        if (_viewerDefaultEncodingName != TextView.DefaultFileEncodingName)
        { _viewerDefaultEncodingName = TextView.DefaultFileEncodingName; _defaultEncodingName = _viewerDefaultEncodingName; }
        if (_fileBytes is not null) { RenderFile(); }
    }

    private void DocumentLoading(object? sender, EventArgs args) { _fileBytes = null; _filePath = null; }

    private void RenderFile()
    {
        if (_fileBytes is null || _filePath is null) { return; }
        _settingFile = true;
        try
        {
            TextView.Mode = DiffViewMode.PlainText;
            if (TryShowImage(_filePath, _fileBytes)) { return; }
            GitDecodedFile decoded = GitFileEncodingCodec.DecodeFile(_fileBytes, TextView.FileEncodingName ?? _defaultEncodingName, _truncated);
            _source.Text = decoded.Text + (_truncated ? "\n[Preview limited to 2 MB]" : "");
            TextView.SyntaxPath = decoded.IsBinary ? null : _filePath;
            TextView.CompletePlainTextDocumentLoad();
        }
        finally { _settingFile = false; }
    }

    public static bool IsImagePath(string path)
        => new[] { ".bmp", ".gif", ".ico", ".jpeg", ".jpg", ".png", ".tif", ".tiff" }
            .Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    public bool TryShowImage(string path, byte[] bytes)
    {
        if (!_settingFile) { _fileBytes = null; _filePath = null; }
        ClearImage();
        if (!IsImagePath(path)) { return false; }
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            _bitmap = new Bitmap(stream);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or InvalidOperationException)
        { return false; }
        _settingImage = true;
        try { _source.Text = ""; }
        finally { _settingImage = false; }
        _image.Source = _bitmap;
        TextView.SyntaxPath = null;
        SizeImage();
        Content = _image;
        return true;
    }

    private void SizeImage()
    {
        if (_bitmap is null) { return; }
        PixelSize pixels = _bitmap.PixelSize;
        double scale = Bounds.Width > 0 && Bounds.Height > 0
            ? Math.Min(1, Math.Min(Bounds.Width / pixels.Width, Bounds.Height / pixels.Height)) : 1;
        // Give layout the destination size so its center lands on whole pixels,
        // as PictureBox.CenterImage did, rather than blurring a small odd-sized image.
        _image.Width = pixels.Width * scale;
        _image.Height = pixels.Height * scale;
    }

    public void ShowImageOrHex(string path, byte[] bytes)
    {
        if (!TryShowImage(path, bytes))
        {
            _source.Text = $"Cannot view image: {path}\n{Convert.ToHexString(bytes.AsSpan(0, Math.Min(4096, bytes.Length)))}";
        }
    }

    public async Task<bool> ShowChangedImageAsync(GitRepository repository, GitChange change, bool staged, CancellationToken token)
    {
        ClearImage();
        // An unmerged index has stages 1/2/3 rather than stage 0. Keep its conflict diff visible.
        if (change.IsSubmodule || change.IsConflict || !IsImagePath(change.Path)) { return false; }
        byte[] bytes;
        if (staged)
        {
            bytes = change.IndexStatus == 'D'
                ? await repository.GetFileBytesAsync(change.OriginalPath ?? change.Path, "HEAD", token)
                : await repository.GetIndexFileBytesAsync(change.Path, token);
        }
        else
        {
            string path = Path.Combine(repository.Path, change.Path);
            bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : await repository.GetIndexFileBytesAsync(change.Path, token);
        }
        ShowImageOrHex(change.Path, bytes);
        return true;
    }

    public void ClearImage()
    {
        Content = TextView;
        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    public void Dispose()
    {
        _source.PropertyChanged -= SourceChanged;
        TextView.FileEncodingChanged -= FileEncodingChanged;
        TextView.DocumentLoading -= DocumentLoading;
        ClearImage();
        TextView.Dispose();
    }
}

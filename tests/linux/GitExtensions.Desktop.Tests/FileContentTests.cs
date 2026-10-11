using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class FileContentTests
{
    [AvaloniaTest]
    public void ImageViewerKeepsOriginalSizingAndRestoresTextForCorruptImages()
    {
        TextBox source = new();
        using FileContentView view = new(source);
        Window window = new() { Content = view, Width = 400, Height = 300 };
        window.Show();
        try
        {
            Assert.That(FileContentView.IsImagePath("PHOTO.JPEG"), Is.True);
            Assert.That(FileContentView.IsImagePath("drawing.svg"), Is.False);
            Assert.That(view.TryShowImage("small.png", Png(2, 3)), Is.True);
            window.UpdateLayout();
            Image image = view.GetVisualDescendants().OfType<Image>().Single();
            Assert.Multiple(() =>
            {
                Assert.That(image.Stretch, Is.EqualTo(Stretch.Uniform));
                Assert.That(image.StretchDirection, Is.EqualTo(Avalonia.Media.StretchDirection.DownOnly));
                Assert.That(view.Bitmap!.PixelSize, Is.EqualTo(new PixelSize(2, 3)));
            });
            Rect small = RenderedImageBounds(window);
            Assert.That(small.Size, Is.EqualTo(new Size(2, 3)), "Small images render at native size rather than filling the available pane.");
            Assert.That(small.Center.X, Is.EqualTo(view.Bounds.Width / 2).Within(1));
            Assert.That(small.Center.Y, Is.EqualTo(view.Bounds.Height / 2).Within(1));
            Assert.That(view.TryShowImage("large.png", Png(800, 400)), Is.True);
            Rect fitted = RenderedImageBounds(window);
            Assert.That(fitted.Width, Is.EqualTo(view.Bounds.Width).Within(1));
            Assert.That(fitted.Height, Is.EqualTo(fitted.Width / 2).Within(1), "Large images fit while preserving their proportions.");
            source.Text = "text after image\n";
            Assert.That(view.IsImage, Is.False);
            Assert.That(view.Content, Is.SameAs(view.TextView));
            Assert.That(view.TextView.Text, Is.EqualTo(source.Text));
            Assert.That(view.TryShowImage("frame.gif", Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7")), Is.True);
            view.ShowImageOrHex("corrupt.png", [65, 66, 255, 0]);
            Assert.That(view.IsImage, Is.False);
            Assert.That(view.TextView.Text, Does.Contain("Cannot view image: corrupt.png").And.Contain("4142FF00"));
            view.ClearImage();
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task HistoryFileListAndMergeParentSelectionShowTheSelectedFile()
    {
        using MainWindowClassicTests.RepositoryFixture fixture = await MainWindowClassicTests.RepositoryFixture.CreateAsync("anchor\n", "anchor\n", createMerge: true);
        MainWindow window = fixture.Window;
        ListBox history = Find<ListBox>(window, "RevisionHistory");
        history.SelectedItem = history.ItemsSource!.OfType<GitCommit>().Single(commit => commit.Parents.Count == 2);
        ListBox files = Find<ListBox>(window, "RevisionFiles");
        await Until(() => !window.IsBusy && files.Items.OfType<GitRevisionFile>().Any(file => file.Path == "feature.txt"));
        ComboBox parent = Find<ComboBox>(window, "RevisionDiffParent");
        Assert.That(parent.IsEffectivelyVisible, Is.True);
        parent.SelectedIndex = 1;
        await Until(() => !window.IsBusy && files.Items.OfType<GitRevisionFile>().Any(file => file.Path == "README.md"));
        FileContentView content = VisibleContent(window);
        Assert.That(content.TextView.Text, Does.Contain("+Main branch content").And.Not.Contain("+Feature branch content"));
        window.UpdateLayout();
        Assert.That(files.TranslatePoint(default, window)!.Value.X + files.Bounds.Width,
            Is.LessThan(content.TranslatePoint(default, window)!.Value.X), "The original file list stays left of its viewer.");
        Assert.That(window.LastException, Is.Null, window.LastException?.ToString());
    }

    [AvaloniaTest]
    public async Task HistoricalAndWorkingImagePreviewsReadTheCorrectGitVersion()
    {
        using MainWindowClassicTests.RepositoryFixture fixture = await MainWindowClassicTests.RepositoryFixture.CreateAsync("anchor\n", "anchor\n");
        const string path = "a λ[1].png";
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Repository, path), Png(2, 3));
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "note.txt"), "note\n");
        await repository.StageAllAsync();
        await repository.CommitAsync("Image and note");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Repository, path), Png(4, 5));
        await repository.StageAsync([path]);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Repository, path), Png(6, 7));
        await fixture.Window.OpenRepositoryAsync(fixture.Repository);
        ListBox history = Find<ListBox>(fixture.Window, "RevisionHistory");
        history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Image and note");
        ListBox files = Find<ListBox>(fixture.Window, "RevisionFiles");
        await Until(() => !fixture.Window.IsBusy && files.Items.OfType<GitRevisionFile>().Any(file => file.Path == path));
        files.SelectedItem = files.Items.OfType<GitRevisionFile>().Single(file => file.Path == path);
        await Until(() => !fixture.Window.IsBusy && VisibleContent(fixture.Window).IsImage);
        Assert.That(VisibleContent(fixture.Window).Bitmap!.PixelSize, Is.EqualTo(new PixelSize(2, 3)));
        files.SelectedItem = files.Items.OfType<GitRevisionFile>().Single(file => file.Path == "note.txt");
        await Until(() => !fixture.Window.IsBusy && !VisibleContent(fixture.Window).IsImage);
        Assert.That(VisibleContent(fixture.Window).TextView.Text, Does.Contain("+note"));

        TabControl tabs = Find<TabControl>(fixture.Window, "DetailsTabs");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "ChangesTab");
        fixture.Window.UpdateLayout();
        ListBox changes = fixture.Window.GetVisualDescendants().OfType<ListBox>().Single(list => list.Items.OfType<GitChange>().Any());
        changes.SelectedItem = changes.Items.OfType<GitChange>().Single(change => change.Path == path);
        await Until(() => !fixture.Window.IsBusy && VisibleContent(fixture.Window).IsImage);
        Assert.That(VisibleContent(fixture.Window).Bitmap!.PixelSize, Is.EqualTo(new PixelSize(6, 7)));
        CheckBox staged = fixture.Window.GetVisualDescendants().OfType<CheckBox>().Single(check => check.Content as string == "Show staged changes");
        staged.IsChecked = true;
        await Until(() => !fixture.Window.IsBusy && VisibleContent(fixture.Window).Bitmap?.PixelSize == new PixelSize(4, 5));
        Assert.That(fixture.Window.LastException, Is.Null, fixture.Window.LastException?.ToString());

        string empty = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "empty");
        await GitRepository.InitAsync(empty);
        await fixture.Window.OpenRepositoryAsync(empty);
        Assert.That(VisibleContent(fixture.Window).IsImage, Is.False, "Repository changes clear stale image previews.");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "DiffTab");
        fixture.Window.UpdateLayout();
        Assert.That(VisibleContent(fixture.Window).IsImage, Is.False);
        Assert.That(Find<ComboBox>(fixture.Window, "RevisionDiffParent").IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task CommitPreviewShowsStagedUnstagedAndDeletedImageBytesWithoutChangingThem()
    {
        using MainWindowClassicTests.RepositoryFixture fixture = await MainWindowClassicTests.RepositoryFixture.CreateAsync("anchor\n", "anchor\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        const string path = "image λ.png";
        await File.WriteAllBytesAsync(Path.Combine(repository.Path, path), Png(2, 3));
        await repository.StageAsync([path]);
        await repository.CommitAsync("Image");
        await File.WriteAllBytesAsync(Path.Combine(repository.Path, path), Png(4, 5));
        await repository.StageAsync([path]);
        byte[] working = Png(6, 7);
        await File.WriteAllBytesAsync(Path.Combine(repository.Path, path), working);
        CommitDialog dialog = new(repository);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            ListBox unstaged = Find<ListBox>(dialog, "Unstaged");
            unstaged.SelectedItem = unstaged.Items.OfType<GitChange>().Single(change => change.Path == path);
            await Until(() => !dialog.IsBusy && VisibleContent(dialog).IsImage);
            Assert.That(VisibleContent(dialog).Bitmap!.PixelSize, Is.EqualTo(new PixelSize(6, 7)));
            ListBox staged = Find<ListBox>(dialog, "Staged");
            staged.SelectedItem = staged.Items.OfType<GitChange>().Single(change => change.Path == path);
            await Until(() => !dialog.IsBusy && VisibleContent(dialog).Bitmap?.PixelSize == new PixelSize(4, 5));
            Assert.That((await File.ReadAllBytesAsync(Path.Combine(repository.Path, path))).SequenceEqual(working), Is.True);
            File.Delete(Path.Combine(repository.Path, path));
            await repository.StageAsync([path]);
            await dialog.RefreshAsync();
            staged.SelectedItem = staged.Items.OfType<GitChange>().Single(change => change.Path == path);
            await Until(() => !dialog.IsBusy && VisibleContent(dialog).IsImage);
            Assert.That(VisibleContent(dialog).Bitmap!.PixelSize, Is.EqualTo(new PixelSize(2, 3)), "A staged deletion previews its parent blob.");
            Assert.That(dialog.LastError, Is.Null);
            Assert.That(File.Exists(Path.Combine(repository.Path, path)), Is.False);
        }
        finally { dialog.Close(); }
    }

    internal static byte[] Png(int width, int height)
    {
        using WriteableBitmap bitmap = new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
        using (ILockedFramebuffer pixels = bitmap.Lock())
        {
            byte[] row = Enumerable.Range(0, width).SelectMany(_ => new byte[] { 20, 100, 200, 255 }).ToArray();
            for (int y = 0; y < height; y++) { Marshal.Copy(row, 0, pixels.Address + y * pixels.RowBytes, row.Length); }
        }
        using MemoryStream stream = new();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static FileContentView VisibleContent(Window window) => window.GetVisualDescendants().OfType<FileContentView>().Single(view => view.IsEffectivelyVisible);
    private static async Task Until(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate() && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        Assert.That(predicate(), Is.True, "Image workflow did not finish within 15 seconds.");
    }

    private static Rect RenderedImageBounds(Window window)
    {
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new AssertionException("No image frame rendered.");
        using ILockedFramebuffer pixels = frame.Lock();
        byte[] data = new byte[pixels.RowBytes * pixels.Size.Height];
        Marshal.Copy(pixels.Address, data, 0, data.Length);
        bool bgra = pixels.Format == PixelFormat.Bgra8888;
        int left = pixels.Size.Width, top = pixels.Size.Height, right = -1, bottom = -1;
        for (int y = 0; y < pixels.Size.Height; y++)
        for (int x = 0; x < pixels.Size.Width; x++)
        {
            int index = y * pixels.RowBytes + x * 4;
            if (data[index + (bgra ? 2 : 0)] != 20 || data[index + 1] != 100 || data[index + (bgra ? 0 : 2)] != 200) { continue; }
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        Assert.That(right, Is.GreaterThanOrEqualTo(left), "Image pixels must appear in the rendered frame.");
        return new Rect(left, top, right - left + 1, bottom - top + 1);
    }
}

using System.Net;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;
using SkiaSharp;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;
using Helpers = GitExtensions.Desktop.Tests.FileHistoryDialogTests;

namespace GitExtensions.Desktop.Tests;

[SetUpFixture]
public sealed class BlameTestAvatarIsolation
{
    private IGourceAvatarProvider? _previous;
    [OneTimeSetUp]
    public void PreventExternalAvatarHttp()
    { _previous = BlameView.DefaultAvatarProvider; BlameView.DefaultAvatarProvider = new NullBlameAvatarProvider(); }
    [OneTimeTearDown]
    public void RestoreAvatarProvider() => BlameView.DefaultAvatarProvider = _previous!;
}

internal sealed class NullBlameAvatarProvider : IGourceAvatarProvider
{
    public Task<byte[]?> GetAvatarPngAsync(string email, string name, int size, CancellationToken token)
        => Task.FromResult<byte[]?>(null);
}

[NonParallelizable]
public class BlameAvatarTests
{
    [AvaloniaTest]
    public async Task InjectedHttpReplacesUser80WithNativeAvatarsWithoutBlockingGitAndRendersInBothThemes()
    {
        using Fixture fixture = await Fixture.CreateAsync(Helpers.Initial, Helpers.Initial);
        await Helpers.MakeHistoryAsync(fixture);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using AvatarHttpHandler handler = new(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return Image(SKColors.DarkGreen);
        });
        using HttpClient http = new(handler);
        GourceAvatarProvider provider = new(http, Cache(fixture));
        BlameDialog dialog = new(repository, Helpers.LiteralPath, avatarProvider: provider); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            await Helpers.Until(() => handler.Requests.Count == 2);
            BlameView view = Helpers.Field<BlameView>(dialog, "BlameView");
            Assert.That(view.AvatarLoading.IsCompleted, Is.False);
            Assert.That(Helpers.Field<TextBox>(dialog, "BlameFilePath").IsEnabled, Is.True, "An HTTP avatar wait must not hold the Git-operation UI lock.");
            using (WriteableBitmap placeholder = Helpers.Capture(dialog, "classic-blame-avatar-pending.png"))
            {
                Assert.That(AvatarPixels(placeholder, dialog, 0, SKColors.DarkGreen), Is.Zero);
                Assert.That(AvatarPixels(placeholder, dialog, 0, SKColors.Black), Is.GreaterThan(3), "The original User80 silhouette stays visible until its avatar is ready.");
            }
            release.TrySetResult();
            await view.AvatarLoading.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(handler.Requests.Count, Is.EqualTo(2), "Repeated author groups share one request per email.");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme;
                using WriteableBitmap frame = Helpers.Capture(dialog, "classic-blame-avatar-" + theme + ".png");
                Assert.That(AvatarPixels(frame, dialog, 0, SKColors.DarkGreen), Is.GreaterThan(40));
                Assert.That(AvatarPixels(frame, dialog, 1, SKColors.DarkGreen), Is.GreaterThan(40));
                Assert.That(AvatarPixels(frame, dialog, 2, SKColors.DarkGreen), Is.GreaterThan(40));
                Color text = ((ISolidColorBrush)view.TextView.Editor.Foreground!).Color;
                Color background = ((ISolidColorBrush)view.TextView.Editor.Background!).Color;
                Assert.That(FileHistorySyntax.Ratio(text, background), Is.GreaterThanOrEqualTo(4.5));
            }
            view.SelectLine(2); await Helpers.Idle(dialog);
            Helpers.Click(dialog, "BlameShowChanges");
            await Helpers.Until(() => dialog.OwnedWindows.OfType<MainWindow>().Any(window => window.WindowKind == MainWindowKind.CommitDiff));
            MainWindow changes = dialog.OwnedWindows.OfType<MainWindow>().Single();
            await Helpers.Until(() => !changes.IsBusy && changes.WindowWorkCompletion.IsCompleted);
            Assert.That(changes.LastException, Is.Null);
            Assert.That(Helpers.Field<GitExtensions.Desktop.Controls.DiffView>(changes, "CommitDiff").Text, Does.Contain("Value = 2"));
            changes.Close(); await Helpers.Until(() => !changes.IsVisible && changes.WindowWorkCompletion.IsCompleted); await Helpers.Idle(dialog);
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { release.TrySetResult(); dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingTheBlameCancelsInjectedHttpAndLeavesNoPartialAvatarCache()
    {
        using Fixture fixture = await Fixture.CreateAsync("committed\n", "committed\n");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using AvatarHttpHandler handler = new(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return Image(SKColors.DarkGreen); }
            finally { if (token.IsCancellationRequested) { cancelled.TrySetResult(); } }
        });
        using HttpClient http = new(handler);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        BlameDialog dialog = new(repository, "file.txt", avatarProvider: new GourceAvatarProvider(http, Cache(fixture))); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            BlameView view = Helpers.Field<BlameView>(dialog, "BlameView");
            Assert.That(dialog.IsBusy, Is.False);
            Task loading = view.AvatarLoading;
            Helpers.Click(dialog, "CancelBlame");
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await loading.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(dialog.IsVisible, Is.False);
            Assert.That(Directory.Exists(Cache(fixture)), Is.False);
            Assert.That(dialog.LastError, Is.Null, "Cancelling a detached avatar must not report a failed Git operation.");
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ReloadCancelsOldHttpAndItsLateImageCannotReplaceTheNewRevisionAvatar()
    {
        using Fixture fixture = await Fixture.CreateAsync("old committed line\n", "old committed line\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string oldHash = await repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "new committed line\n");
        await Helpers.Git(fixture, "commit", "-am", "New author", "--author=New Author <new@example.invalid>");
        TaskCompletionSource oldEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource oldCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource oldRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using AvatarHttpHandler handler = new(async (uri, token) =>
        {
            if (uri.Query.Contains("desktop", StringComparison.Ordinal))
            {
                using CancellationTokenRegistration registration = token.Register(() => oldCancelled.TrySetResult());
                oldEntered.TrySetResult();
                await oldRelease.Task; // Simulate an HTTP handler that delivers after cancellation.
                return Image(SKColors.Red);
            }
            return Image(SKColors.DarkGreen);
        });
        using HttpClient http = new(handler);
        BlameDialog dialog = new(repository, "file.txt", oldHash, avatarProvider: new GourceAvatarProvider(http, Cache(fixture))); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            BlameView view = Helpers.Field<BlameView>(dialog, "BlameView"); Task oldLoad = view.AvatarLoading;
            Helpers.Field<TextBox>(dialog, "BlameRevision").Text = "HEAD";
            Helpers.Click(dialog, "LoadBlame"); await Helpers.Idle(dialog);
            await oldCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await view.AvatarLoading.WaitAsync(TimeSpan.FromSeconds(5));
            oldRelease.TrySetResult(); await oldLoad.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(view.TextView.Text, Is.EqualTo("new committed line\n"));
            Assert.That(view.SelectedLine!.Line.AuthorName, Is.EqualTo("New Author"));
            using WriteableBitmap frame = Helpers.Capture(dialog, "classic-blame-avatar-reloaded.png");
            Assert.That(AvatarPixels(frame, dialog, 0, SKColors.DarkGreen), Is.GreaterThan(40));
            Assert.That(AvatarPixels(frame, dialog, 0, SKColors.Red), Is.Zero);
            Assert.That(Directory.GetFiles(Cache(fixture), "*.png").Length, Is.EqualTo(1), "The cancelled author's late response must not write the cache.");
        }
        finally { oldRelease.TrySetResult(); dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task SharedProviderWorksInFileHistoryAndUncommittedLinesRetainUser80()
    {
        using Fixture fixture = await Fixture.CreateAsync("committed\n", "committed\nnot committed\n");
        using AvatarHttpHandler handler = new(_ => Image(SKColors.DarkGreen)); using HttpClient http = new(handler);
        GourceAvatarProvider provider = new(http, Cache(fixture));
        IGourceAvatarProvider previous = BlameView.DefaultAvatarProvider; BlameView.DefaultAvatarProvider = provider;
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        BlameDialog blame = new(repository, "file.txt", revision: null); FileHistoryDialog history = new(repository, "file.txt");
        blame.Show();
        try
        {
            await blame.InitializeAsync(); BlameView view = Helpers.Field<BlameView>(blame, "BlameView");
            await view.AvatarLoading.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(handler.Requests.Count, Is.EqualTo(1), "An uncommitted pseudo-author must not be queried remotely.");
            using (WriteableBitmap frame = Helpers.Capture(blame, "classic-blame-avatar-uncommitted.png"))
            {
                Assert.That(AvatarPixels(frame, blame, 0, SKColors.DarkGreen), Is.GreaterThan(40));
                Assert.That(AvatarPixels(frame, blame, 1, SKColors.DarkGreen), Is.Zero);
                Assert.That(AvatarPixels(frame, blame, 1, SKColors.Black), Is.GreaterThan(3));
            }
            blame.Close(); history.Show(); await history.InitializeAsync();
            Helpers.Field<TabControl>(history, "FileHistoryTabs").SelectedIndex = 3; await Helpers.Idle(history);
            BlameView embedded = Helpers.Field<BlameView>(history, "FileHistoryBlame");
            await embedded.AvatarLoading.WaitAsync(TimeSpan.FromSeconds(5));
            using WriteableBitmap embeddedFrame = Helpers.Capture(history, "classic-file-history-avatar.png");
            Assert.That(AvatarPixels(embeddedFrame, history, 0, SKColors.DarkGreen), Is.GreaterThan(40));
            Assert.That(handler.Requests.Count, Is.EqualTo(1), "The native provider cache is shared across the two blame surfaces.");
        }
        finally { blame.Close(); history.Close(); BlameView.DefaultAvatarProvider = previous; }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task DisposedBlameUnpublishesItsImagesBeforeTheStillMappedOwnerRenders(bool fetchedAvatar)
    {
        using Fixture fixture = await Fixture.CreateAsync(Helpers.Initial, Helpers.Initial);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        using AvatarHttpHandler handler = new((_, _) => Task.FromResult(Image(SKColors.DarkGreen)));
        using HttpClient http = new(handler);
        IGourceAvatarProvider provider = fetchedAvatar ? new GourceAvatarProvider(http, Cache(fixture)) : new NullBlameAvatarProvider();
        BlameDialog dialog = new(repository, "file.txt", avatarProvider: provider); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            BlameView view = Helpers.Field<BlameView>(dialog, "BlameView");
            await view.AvatarLoading.WaitAsync(TimeSpan.FromSeconds(5));
            string text = view.TextView.Text;
            using (WriteableBitmap before = Helpers.Capture(dialog, "blame-before-dispose-" + fetchedAvatar + ".png"))
            {
                Assert.That(AvatarPixels(before, dialog, 0, fetchedAvatar ? SKColors.DarkGreen : SKColors.Black), Is.GreaterThan(3));
            }
            Control margin = Helpers.Field<Control>(dialog, "BlameAuthorMargin");
            Point clicked = margin.TranslatePoint(new Point(100, view.TextView.Editor.TextArea.TextView.DefaultLineHeight * .5), dialog)!.Value;
            dialog.MouseMove(clicked); dialog.MouseDown(clicked, MouseButton.Right); dialog.MouseUp(clicked, MouseButton.Right);
            ContextMenu menu = margin.ContextMenu!; await Helpers.Until(() => menu.IsOpen);
            Avalonia.Controls.Image[] icons = menu.Items.OfType<MenuItem>().Select(item => item.Icon).OfType<Avalonia.Controls.Image>()
                .Append((Avalonia.Controls.Image)Helpers.Field<Button>(dialog, "BlameOptions").Content!).ToArray();
            Assert.That(icons.Length, Is.GreaterThan(1)); Assert.That(icons.All(icon => icon.Source is not null), Is.True);
            view.Dispose();
            Assert.That(dialog.IsVisible, Is.True); Assert.That(TopLevel.GetTopLevel(view), Is.SameAs(dialog));
            Assert.That(icons.All(icon => icon.Source is null), Is.True, "Popup and toolbar presenters must not retain disposed images.");
            using WriteableBitmap after = Helpers.Capture(dialog, "blame-disposed-still-mapped-" + fetchedAvatar + ".png");
            Assert.That(after.PixelSize.Width, Is.GreaterThan(0)); Assert.That(view.TextView.Text, Is.EqualTo(text));
            menu.Close();
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    private static string Cache(Fixture fixture) => Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "blame-avatars");
    private static HttpResponseMessage Image(SKColor color)
    {
        using SKBitmap bitmap = new(30, 30); bitmap.Erase(color);
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data.ToArray()) };
    }
    private static int AvatarPixels(WriteableBitmap frame, Window owner, int row, SKColor color)
    {
        Control margin = Helpers.Field<Control>(owner, "BlameAuthorMargin");
        BlameView view = owner is BlameDialog ? Helpers.Field<BlameView>(owner, "BlameView") : Helpers.Field<BlameView>(owner, "FileHistoryBlame");
        double lineHeight = view.TextView.Editor.TextArea.TextView.DefaultLineHeight;
        Point start = margin.TranslatePoint(new Point(6, row * lineHeight), owner)!.Value;
        using ILockedFramebuffer buffer = frame.Lock(); byte[] bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length); bool bgra = buffer.Format == PixelFormat.Bgra8888;
        int count = 0;
        for (int y = Math.Max(0, (int)start.Y); y < Math.Min(buffer.Size.Height, start.Y + lineHeight); y++)
        for (int x = Math.Max(0, (int)start.X); x < Math.Min(buffer.Size.Width, start.X + Math.Min(20, lineHeight)); x++)
        {
            int offset = y * buffer.RowBytes + x * 4;
            if (bytes[offset + (bgra ? 2 : 0)] == color.Red && bytes[offset + 1] == color.Green && bytes[offset + (bgra ? 0 : 2)] == color.Blue) { count++; }
        }
        return count;
    }
}

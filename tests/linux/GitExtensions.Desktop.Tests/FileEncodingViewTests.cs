using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class FileEncodingViewTests
{
    [AvaloniaTest]
    public async Task RealLegacyBlobSelectorRedecodesRememberedBytesInBothThemesWithoutGitMutation()
    {
        using FileFixture fixture = await FileFixture.CreateAsync("iso-8859-1");
        byte[] bytes = GitFileEncodingCodec.GetEncoding("windows-1252").GetBytes("été € ‘quotes’\r\n42\n");
        File.WriteAllBytes(fixture.File, bytes);
        await fixture.Repository.StageAsync([FileFixture.Literal]); await fixture.Repository.CommitAsync("Historical legacy bytes");
        byte[] config = File.ReadAllBytes(Path.Combine(fixture.Repository.Path, ".git/config"));
        byte[] index = File.ReadAllBytes(Path.Combine(fixture.Repository.Path, ".git/index"));
        using FileHistoryContentView view = new();
        await view.TextView.ConfigureRepositoryAsync(fixture.Repository);
        byte[] blob = await fixture.Repository.GetFileBytesAsync(FileFixture.Literal);
        view.ShowFile(FileFixture.Literal, blob);
        Window window = new() { Width = 640, Height = 280, Content = view };
        window.Show();
        try
        {
            Assert.That(view.CurrentFileEncodingName, Is.EqualTo("iso-8859-1"));
            Assert.That(view.TextView.Text, Does.Contain("été \u0080"));
            int events = 0; view.EncodingChanged += (_, _) => events++;
            object choice = view.EncodingSelector.Items.Cast<object>().Single(item => Name(item) == "windows-1252");
            view.EncodingSelector.SelectedItem = choice;
            Assert.That(view.FileEncodingName, Is.EqualTo("windows-1252"));
            Assert.That(view.TextView.Text, Is.EqualTo("été € ‘quotes’\r\n42\n"));
            Assert.That(view.TextView.Editor.IsReadOnly, Is.True);
            Assert.That(view.TextView.CanStagePatch, Is.False);
            Assert.That(events, Is.EqualTo(1));
            blob[0] = 0;
            File.Delete(fixture.File);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme; window.UpdateLayout();
                DiffViewerInputTestHelpers.RevealToolbar(window, view.EncodingSelector);
                Assert.That(view.EncodingSelector.Bounds.Height, Is.EqualTo(24));
                Assert.That(view.EncodingSelector.Bounds.Width, Is.EqualTo(140));
                Assert.That(Name(view.EncodingSelector.SelectedItem!), Is.EqualTo("windows-1252"));
                using WriteableBitmap frame = window.CaptureRenderedFrame()!;
                Assert.That(frame, Is.Not.Null);
                Capture(frame, "file-encoding-selector-" + theme + ".png");
            }
            view.SetFileEncoding(null);
            Assert.That(view.CurrentFileEncodingName, Is.EqualTo("iso-8859-1"));
            Assert.That(view.TextView.Text, Does.Contain("été \u0080"));
            Assert.That(view.FileEncodingName, Is.Null);
            Assert.That(events, Is.EqualTo(2), "Reset reads only remembered blob bytes, even after its worktree file is deleted.");
            Assert.That(File.ReadAllBytes(Path.Combine(fixture.Repository.Path, ".git/config")), Is.EqualTo(config));
            Assert.That(File.ReadAllBytes(Path.Combine(fixture.Repository.Path, ".git/index")), Is.EqualTo(index));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void ExplicitDefaultSurvivesSessionResetAndDirectDiffViewChoiceUsesTheSameCache()
    {
        using FileHistoryContentView view = new();
        byte[] bytes = GitFileEncodingCodec.GetEncoding("windows-1252").GetBytes("été €\n");
        view.ShowFile("legacy.txt", bytes, "iso-8859-1");
        Assert.That(view.TextView.Text, Is.EqualTo("été \u0080\n"));
        view.SetFileEncoding("windows-1252");
        Assert.That(view.TextView.Text, Is.EqualTo("été €\n"));
        view.TextView.FileEncodingName = null;
        Assert.That(view.CurrentFileEncodingName, Is.EqualTo("iso-8859-1"));
        Assert.That(view.TextView.Text, Is.EqualTo("été \u0080\n"));
        view.TextView.FileEncodingName = "windows-1252";
        Assert.That(view.FileEncodingName, Is.EqualTo("windows-1252"));
        Assert.That(view.TextView.Text, Is.EqualTo("été €\n"), "Wrapper and DiffView share one session override.");
    }

    [AvaloniaTest]
    public void BomWinsOverSessionChoicesAndUnicodePreviewRemainsStrictAcrossReloads()
    {
        using FileHistoryContentView view = new();
        Window window = new() { Width = 560, Height = 260, Content = view };
        window.Show();
        try
        {
            view.SetFileEncoding("windows-1252");
            foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true),
                new UnicodeEncoding(true, true, true), new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) })
            {
                const string expected = "été λ 🦊\r\n42\n";
                view.ShowFile("literal é.cs", [.. encoding.GetPreamble(), .. encoding.GetBytes(expected)], "iso-8859-1");
                Assert.Multiple(() =>
                {
                    Assert.That(view.TextView.Text, Is.EqualTo(expected));
                    Assert.That(view.HasBom, Is.True);
                    Assert.That(view.IsBinary, Is.False);
                    Assert.That(view.CurrentFileEncodingName, Is.EqualTo(encoding.WebName));
                    Assert.That(view.FileEncodingName, Is.EqualTo("windows-1252"), "BOM detection does not mutate session choice.");
                });
                view.SetFileEncoding("iso-8859-1");
                Assert.That(view.TextView.Text, Is.EqualTo(expected));
                view.SetFileEncoding("windows-1252");
            }
            view.ShowFile("corrupt.cs", [0xff, 0xfe, 0x3d, 0xd8]);
            Assert.That(view.IsBinary, Is.True);
            Assert.That(view.TextView.Text, Does.Contain("FFFE3DD8").And.Not.Contain("\ufffd"));
            Assert.That(view.TextView.Editor.SyntaxHighlighting, Is.Null);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void ImageBytesRemainAnImageAndNulBinaryRemainsHexAcrossEverySingleByteChoice()
    {
        using FileHistoryContentView view = new();
        byte[] png = FileHistoryDialogTests.ImageBytes(3, 2, 0xa1);
        view.ShowFile("image.png", png, "windows-1252");
        Assert.That(view.IsImage, Is.True);
        Assert.That(view.EncodingSelector.IsEnabled, Is.False);
        view.SetFileEncoding("iso-8859-1");
        Assert.That(view.IsImage, Is.True);
        Assert.That(view.TextView.Text, Is.Empty);
        view.ShowFile("binary.cs", [0, 1, 0xff, 0x80], "windows-1252");
        Assert.That(view.IsImage, Is.False);
        Assert.That(view.EncodingSelector.IsEnabled, Is.True);
        foreach (string name in new[] { "utf-8", "iso-8859-1", "windows-1252" })
        {
            view.SetFileEncoding(name);
            Assert.That(view.IsBinary, Is.True);
            Assert.That(view.TextView.Text, Does.Contain("0001FF80").And.Not.Contain("\ufffd"));
            Assert.That(view.TextView.Editor.SyntaxHighlighting, Is.Null);
        }
    }

    [AvaloniaTest]
    public async Task ChangingRepositoriesClearsSessionAndUpdatesTheRememberedDefaultWithoutConfigWrites()
    {
        using FileFixture latin = await FileFixture.CreateAsync("iso-8859-1");
        using FileFixture cp = await FileFixture.CreateAsync("windows-1252");
        using FileHistoryContentView view = new();
        await view.TextView.ConfigureRepositoryAsync(latin.Repository);
        view.ShowFile("file.txt", [0xe9, 0x80, 0x0a]);
        view.SetFileEncoding("us-ascii");
        Assert.That(view.IsBinary, Is.True);
        byte[] config = File.ReadAllBytes(Path.Combine(cp.Repository.Path, ".git/config"));
        await view.TextView.ConfigureRepositoryAsync(cp.Repository);
        Assert.That(view.FileEncodingName, Is.Null);
        Assert.That(view.CurrentFileEncodingName, Is.EqualTo("windows-1252"));
        Assert.That(view.TextView.Text, Is.EqualTo("é€\n"));
        Assert.That(view.IsBinary, Is.False);
        Assert.That(File.ReadAllBytes(Path.Combine(cp.Repository.Path, ".git/config")), Is.EqualTo(config));
    }

    [AvaloniaTest]
    public void UnchangedBlobReloadReleasesSearchAndKeepsPositionWhileAnotherIdentityStartsAtOne()
    {
        const string text = "first\r\nshared second\r\nthird\n";
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        TextBox source = new();
        using FileContentView content = new(source);
        DiffView view = content.TextView;
        Window window = new() { Width = 640, Height = 300, Content = content };
        window.Show();
        try
        {
            view.DocumentIdentity = "repository\0first.cs\0revision\0blob";
            view.BeginDocumentLoad(); content.ShowFile("first.cs", bytes);
            int selected = text.IndexOf("second", StringComparison.Ordinal);
            view.Select(selected, "second".Length);
            int caret = view.CaretOffset;
            int notifications = 0;
            source.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) { notifications++; } };
            view.Search.Query = "third";
            view.BeginDocumentLoad();
            Assert.That(view.Search.FindNext(), Is.Null, "The old display is unavailable during loading.");
            content.ShowFile("first.cs", bytes);
            Assert.Multiple(() =>
            {
                Assert.That(notifications, Is.Zero, "Identical source text emits no property change.");
                Assert.That(view.Search.IsAvailable, Is.True);
                Assert.That(view.SelectionStart, Is.EqualTo(selected));
                Assert.That(view.SelectionLength, Is.EqualTo("second".Length));
                Assert.That(view.CaretOffset, Is.EqualTo(caret));
                Assert.That(view.CurrentFileLine, Is.EqualTo(2));
                Assert.That(view.Text, Is.EqualTo(text));
                Assert.That(view.CanStagePatch, Is.False);
            });
            Assert.That(view.Search.FindNext()?.Offset, Is.EqualTo(text.IndexOf("third", StringComparison.Ordinal)));
            view.DocumentIdentity = "repository\0other.cs\0revision\0blob";
            view.BeginDocumentLoad(); content.ShowFile("other.cs", bytes);
            Assert.Multiple(() =>
            {
                Assert.That(notifications, Is.Zero);
                Assert.That(view.CurrentFileLine, Is.EqualTo(1));
                Assert.That(view.SelectionLength, Is.Zero);
                Assert.That(view.Search.IsAvailable, Is.True);
            });
            view.CaretOffset = selected;
            view.BeginDocumentLoad(); content.ShowFile("other.cs", bytes);
            Assert.That(view.CurrentFileLine, Is.EqualTo(2), "The successfully loaded identity also owns the next refresh.");
        }
        finally { window.Close(); }
    }

    private static string? Name(object choice) => (string?)choice.GetType().GetProperty("Name")!.GetValue(choice);
    private static void Capture(WriteableBitmap frame, string name)
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts/linux/screenshots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name); frame.Save(path); TestContext.AddTestAttachment(path);
    }

    private sealed class FileFixture : IDisposable
    {
        internal const string Literal = "été\t\n[*]'\".cs";
        private FileFixture(string root, GitRepository repository) { Root = root; Repository = repository; }
        internal string Root { get; }
        internal GitRepository Repository { get; }
        internal string File => Path.Combine(Repository.Path, Literal);
        internal static async Task<FileFixture> CreateAsync(string encoding)
        {
            string root = Path.Combine(Path.GetTempPath(), "gitextensions-file-codec-" + Guid.NewGuid().ToString("N"));
            GitRepository repository = await GitRepository.InitAsync(Path.Combine(root, "repo"));
            await repository.SetConfigAsync("user.name", "Native file codec"); await repository.SetConfigAsync("user.email", "codec@example.invalid");
            await repository.SetConfigAsync("commit.gpgsign", "false"); await repository.SetConfigAsync("core.hooksPath", "/dev/null");
            await repository.SetConfigAsync("i18n.filesencoding", encoding);
            return new(root, repository);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

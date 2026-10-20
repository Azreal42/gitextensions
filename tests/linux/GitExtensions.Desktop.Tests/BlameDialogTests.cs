using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;
using Helpers = GitExtensions.Desktop.Tests.FileHistoryDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class BlameDialogTests
{
    [AvaloniaTest]
    public async Task BlameRendersOriginalAttributionBesideCodeAndLoadsSelectedAuthorsAndChangesInBothThemes()
    {
        using Fixture fixture = await Fixture.CreateAsync(Helpers.Initial, Helpers.Initial);
        await Helpers.MakeHistoryAsync(fixture);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        BlameDialog dialog = new(repository, Helpers.LiteralPath);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.LastBlame!.Lines.Count, Is.EqualTo(4));
            Assert.That(dialog.LastBlame.Lines[0].OriginalPath, Is.EqualTo("file.txt"));
            Assert.That(dialog.LastBlame.Lines[1].OriginalPath, Is.EqualTo(Helpers.LiteralPath), "Git's escaped original filename must be decoded literally.");
            Assert.That(dialog.LastBlame.Lines[1].PreviousPath, Is.EqualTo(Helpers.LiteralPath));
            BlameView blame = Helpers.Field<BlameView>(dialog, "BlameView");
            Assert.That(blame.TextView.Text, Is.EqualTo(Helpers.Initial.Replace("Value = 1", "Value = 2", StringComparison.Ordinal)));
            Assert.That(blame.Lines[0].Line.AuthorName, Is.EqualTo("Desktop Tester"));
            Assert.That(blame.Lines[1].Line.AuthorName, Is.EqualTo("Béatrice"));
            dialog.UpdateLayout();
            Control margin = Helpers.Field<Control>(dialog, "BlameAuthorMargin");
            Point point = margin.TranslatePoint(new Point(120, blame.TextView.Editor.TextArea.TextView.DefaultLineHeight * 1.5), dialog)!.Value;
            dialog.MouseMove(point); dialog.MouseDown(point, MouseButton.Left); dialog.MouseUp(point, MouseButton.Left);
            await Helpers.Idle(dialog);
            Assert.That(Helpers.Field<CommitInformationView>(dialog, "BlameCommitInfo").Text, Does.Contain("Béatrice <beatrice@example.invalid>").And.Contain("Actual author commit body."));
            Assert.That(blame.SelectedLine!.Line.LineNumber, Is.EqualTo(2));
            Assert.That(margin.TranslatePoint(default, dialog)!.Value.X + margin.Bounds.Width,
                Is.LessThan(blame.TextView.Editor.TextArea.TextView.TranslatePoint(default, dialog)!.Value.X));
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme; dialog.UpdateLayout();
                Assert.That(((ISolidColorBrush)blame.TextView.Editor.Foreground!).Color, Is.EqualTo(Color.Parse(theme == ThemeVariant.Dark ? "#f0f0f0" : "#000000")));
                using var frame = Helpers.Capture(dialog, "classic-blame-" + theme + ".png");
                Assert.That(frame.PixelSize.Width, Is.GreaterThan(700));
            }
            Helpers.Click(dialog, "BlameShowChanges");
            await Helpers.Until(() => dialog.OwnedWindows.OfType<MainWindow>().Any(window => window.WindowKind == MainWindowKind.CommitDiff));
            MainWindow changes = dialog.OwnedWindows.OfType<MainWindow>().Single();
            await Helpers.Until(() => !changes.IsBusy && changes.WindowWorkCompletion.IsCompleted);
            Assert.That(changes.LastException, Is.Null);
            Assert.That(Helpers.Field<GitExtensions.Desktop.Controls.DiffView>(changes, "CommitDiff").Text, Does.Contain("+    public int Value = 2;").And.Contain("-    public int Value = 1;"));
            changes.Close(); await Helpers.Until(() => !changes.IsVisible && changes.WindowWorkCompletion.IsCompleted); await Helpers.Idle(dialog);
            Helpers.Click(dialog, "BlamePreviousRevision"); await Helpers.Idle(dialog);
            Assert.That(blame.TextView.Text, Is.EqualTo(Helpers.Initial), "Previous-revision navigation must read the actual earlier blob.");
            Assert.That(Helpers.Field<TextBox>(dialog, "BlameFilePath").Text, Is.EqualTo(Helpers.LiteralPath));
            Assert.That(dialog.LastBlame!.Lines.All(line => line.Line.AuthorName == "Desktop Tester"), Is.True);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task BlameThisRevisionUsesOriginalPathAndWhitespaceOptionChangesActualAttribution()
    {
        const string original = "one\nhello world\nlast without newline";
        using Fixture fixture = await Fixture.CreateAsync(original, original);
        await Helpers.Git(fixture, "mv", "--", "file.txt", Helpers.LiteralPath);
        await Helpers.Git(fixture, "commit", "-m", "Rename");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, Helpers.LiteralPath), original.Replace("hello world", "hello    world", StringComparison.Ordinal));
        await Helpers.Git(fixture, "add", "--", Helpers.LiteralPath);
        await Helpers.Git(fixture, "commit", "--author=Whitespace Author <white@example.invalid>", "-m", "Whitespace edit");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        BlameDialog dialog = new(repository, Helpers.LiteralPath);
        dialog.Show();
        try
        {
            Helpers.Field<CheckBox>(dialog, "BlameIgnoreWhitespace").IsChecked = false;
            await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.LastBlame!.Lines[1].Line.AuthorName, Is.EqualTo("Whitespace Author"));
            BlameView view = Helpers.Field<BlameView>(dialog, "BlameView");
            Assert.That(view.TextView.Text, Does.EndWith("last without newline"), "The viewer must preserve a blob with no final newline.");
            Helpers.Field<CheckBox>(dialog, "BlameIgnoreWhitespace").IsChecked = true;
            Helpers.Click(dialog, "LoadBlame"); await Helpers.Idle(dialog);
            Assert.That(dialog.LastBlame!.Lines[1].Line.AuthorName, Is.EqualTo("Desktop Tester"));
            Helpers.Click(dialog, "BlameThisRevision"); await Helpers.Idle(dialog);
            Assert.That(Helpers.Field<TextBox>(dialog, "BlameFilePath").Text, Is.EqualTo("file.txt"));
            Assert.That(view.TextView.Text, Is.EqualTo(original));
            Assert.That(dialog.LastBlame.Lines.All(line => line.IsBoundary), Is.True);
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task WorkingDirectoryBlameShowsUncommittedLinesAndCloseCancelsAnActualGitLoad()
    {
        using Fixture fixture = await Fixture.CreateAsync("committed\n", "committed\nnew working line\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        GitFileBlame working = await repository.GetFileBlameAsync("file.txt", revision: null);
        Assert.That(working.Lines.Count, Is.EqualTo(2));
        Assert.That(working.Lines[1].Line.Hash, Is.All.EqualTo('0'));
        Assert.That(Encoding.UTF8.GetString(working.Bytes), Is.EqualTo("committed\nnew working line\n"));
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Linux process cancellation scenario."); return; }
        (GitRepository slow, string started) = Helpers.SlowRepository(fixture, "blame");
        BlameDialog dialog = new(slow, "file.txt");
        dialog.Show(); Task load = dialog.InitializeAsync();
        await Helpers.Until(() => File.Exists(started));
        Assert.That(Helpers.Field<TextBox>(dialog, "BlameFilePath").IsEnabled, Is.False);
        Helpers.Click(dialog, "CancelBlame");
        await load.WaitAsync(TimeSpan.FromSeconds(10));
        await Helpers.Until(() => !dialog.IsVisible);
        Assert.That(dialog.LastError, Is.EqualTo("Operation cancelled"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "file.txt")), Is.EqualTo("committed\nnew working line\n"));
    }
}

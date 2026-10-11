using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Styling;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;
using Helpers = GitExtensions.Desktop.Tests.FileHistoryDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class BlameNavigationTests
{
    [AvaloniaTest]
    public async Task PreviousRevisionMapsAcrossMultipleRealHunksAndRenameToItsLiteralOldPath()
    {
        string original = Source();
        using Fixture fixture = await Fixture.CreateAsync(original, original);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string parent = await repository.ResolveRevisionAsync("HEAD");
        List<string> lines = original.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        lines.InsertRange(0, ["// inserted first", "// inserted second"]);
        lines.RemoveAt(6); // Remove original line five, before the selected line.
        lines[9] = "public int Code09 = 999; // changed value in the original code line nine";
        string changed = string.Join('\n', lines) + "\n";
        await Helpers.Git(fixture, "mv", "--", "file.txt", Helpers.LiteralPath);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, Helpers.LiteralPath), changed);
        await Helpers.Git(fixture, "add", "--", Helpers.LiteralPath);
        await Helpers.Git(fixture, "commit", "--author=Hunk Author <hunk@example.invalid>", "-m", "Rename with inserted deleted and edited lines");
        BlameDialog dialog = new(repository, Helpers.LiteralPath, initialLine: 10); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            BlameView view = Helpers.Field<BlameView>(dialog, "BlameView");
            Assert.That(view.SelectedLine!.Line.Text, Does.Contain("Code09 = 999"));
            Assert.That(view.SelectedLine.Line.OriginalLine, Is.EqualTo(10));
            Assert.That(view.SelectedLine.PreviousPath, Is.EqualTo("file.txt"));
            Helpers.Field<CheckBox>(dialog, "BlameDetectMoves").IsChecked = true;
            Helpers.Field<CheckBox>(dialog, "BlameDetectCopies").IsChecked = true;
            Helpers.Click(dialog, "BlamePreviousRevision"); await Helpers.Idle(dialog);
            Assert.That(dialog.LastBlame!.Revision, Is.EqualTo(parent));
            Assert.That(Helpers.Field<TextBox>(dialog, "BlameFilePath").Text, Is.EqualTo("file.txt"));
            Assert.That(view.TextView.Text, Is.EqualTo(original));
            Assert.That(view.SelectedLine!.Line.LineNumber, Is.EqualTo(9), "Two inserted lines and one deleted line must not leave the old file selected at line ten.");
            Assert.That(view.SelectedLine.Line.Text, Does.Contain("Code09 = 9"));
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme;
                using var frame = Helpers.Capture(dialog, "classic-blame-previous-" + theme + ".png");
                Assert.That(frame.PixelSize.Height, Is.GreaterThan(400));
            }
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository, Helpers.LiteralPath)), Is.EqualTo(changed));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task InsertedLinesNavigateToTheOriginalHunkStartAndRootLinesHaveNoPreviousRevision()
    {
        using Fixture fixture = await Fixture.CreateAsync("one\ntwo\nthree\n", "one\ntwo\nthree\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await repository.SetConfigAsync("diff.algorithm", "patience");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "insert first\ninsert second\none\ntwo\nthree\n");
        await Helpers.Git(fixture, "commit", "-am", "Insert first two lines");
        GitFileBlame blame = await repository.GetFileBlameAsync("file.txt");
        GitFileBlameLine inserted = blame.Lines[1];
        Assert.That(inserted.Line.Text, Is.EqualTo("insert second"));
        foreach (bool histogram in new[] { false, true })
        {
            GitBlamePreviousRevision previous = (await repository.GetBlamePreviousRevisionAsync(inserted, useHistogramDiffAlgorithm: histogram))!;
            Assert.That(previous.Line, Is.EqualTo(1), "A new line before the original file must clamp to its first line.");
            Assert.That(previous.Path, Is.EqualTo("file.txt"));
            Assert.That((await repository.GetFileBlameAsync(previous.Path, previous.Revision)).Lines[previous.Line - 1].Line.Text, Is.EqualTo("one"));
        }
        Assert.That(await repository.GetBlamePreviousRevisionAsync(blame.Lines[2]), Is.Null, "The root commit's attributed lines have no earlier commit.");
        BlameDialog dialog = new(repository, "file.txt", initialLine: 2); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Helpers.Click(dialog, "BlamePreviousRevision"); await Helpers.Idle(dialog);
            Assert.That(Helpers.Field<BlameView>(dialog, "BlameView").SelectedLine!.Line.Text, Is.EqualTo("one"));
            Assert.That(Helpers.Field<BlameView>(dialog, "BlameView").TextView.Editor.TextArea.Caret.Line, Is.EqualTo(1));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task CopiedLineNavigatesUsingItsActualAttributedCommitAndOriginalFilename()
    {
        string original = Source();
        using Fixture fixture = await Fixture.CreateAsync(original, original);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string parent = await repository.ResolveRevisionAsync("HEAD");
        string changed = original.Replace("Code09 = 9", "Code09 = 999", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), changed);
        await Helpers.Git(fixture, "commit", "-am", "Edit original", "--author=Original Author <origin@example.invalid>");
        string attributed = await repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, Helpers.LiteralPath), changed);
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "// source also participated in this copy commit\n");
        await Helpers.Git(fixture, "add", "--", "file.txt", Helpers.LiteralPath); await Helpers.Git(fixture, "commit", "-m", "Copy to another literal filename");
        BlameDialog dialog = new(repository, Helpers.LiteralPath, initialLine: 9); dialog.Show();
        try
        {
            Helpers.Field<CheckBox>(dialog, "BlameDetectMoves").IsChecked = true;
            Helpers.Field<CheckBox>(dialog, "BlameDetectCopies").IsChecked = true;
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            BlameView view = Helpers.Field<BlameView>(dialog, "BlameView");
            Assert.That(view.SelectedLine!.Line.Hash, Is.EqualTo(attributed));
            Assert.That(view.SelectedLine.OriginalPath, Is.EqualTo("file.txt"));
            Helpers.Click(dialog, "BlamePreviousRevision"); await Helpers.Idle(dialog);
            Assert.That(dialog.LastBlame!.Revision, Is.EqualTo(parent));
            Assert.That(dialog.LastBlame.Path, Is.EqualTo("file.txt"));
            Assert.That(view.SelectedLine!.Line.LineNumber, Is.EqualTo(9));
            Assert.That(view.TextView.Text, Is.EqualTo(original));
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingPreviousRevisionMappingCancelsItsActualGitDiffProcess()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Linux process cancellation scenario."); return; }
        using Fixture fixture = await Fixture.CreateAsync(Helpers.Initial, Helpers.Initial); await Helpers.MakeHistoryAsync(fixture);
        (GitRepository slow, string started) = Helpers.SlowRepository(fixture, "diff");
        BlameDialog dialog = new(slow, Helpers.LiteralPath, initialLine: 2); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Helpers.Idle(dialog);
            Helpers.Click(dialog, "BlamePreviousRevision"); await Helpers.Until(() => File.Exists(started));
            Assert.That(dialog.IsBusy, Is.True);
            Assert.That(Helpers.Field<TextBox>(dialog, "BlameRevision").IsEnabled, Is.False);
            Helpers.Click(dialog, "CancelBlame");
            await Helpers.Until(() => !dialog.IsBusy && !dialog.IsVisible);
            Assert.That(dialog.LastError, Is.EqualTo("Operation cancelled"));
            Assert.That((await fixture.Git.RunAsync(fixture.Repository, ["status", "--porcelain"])).StandardOutput, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    private static string Source() => string.Join('\n', Enumerable.Range(1, 12).Select(index => $"public int Code{index:00} = {index}; // unique original code and description number {index:00}")) + "\n";
}

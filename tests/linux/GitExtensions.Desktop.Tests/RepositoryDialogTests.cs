using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class RepositoryDialogTests
{
    [AvaloniaTest]
    public async Task NoteEditorLoadsTheExistingNoteAndSavesMultilineTextWithoutChangingHead()
    {
        using Fixture fixture = await Fixture.CreateAsync("anchor\n", "anchor\n");
        GitRepository repository = new(fixture.Repository);
        string hash = await repository.ResolveRevisionAsync("HEAD");
        await repository.AddNoteAsync(hash, "Existing note\n");
        NoteDialog dialog = new(repository, hash);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            TextBox note = Field<TextBox>(dialog, "NoteText");
            Assert.That(note.Text, Is.EqualTo("Existing note\n"));
            const string newNote = "  Detailed note λ🧪\n\nSecond paragraph\n";
            note.Text = newNote;
            Click(dialog, "SaveNote");
            await Idle(dialog);
            Assert.Multiple(() =>
            {
                Assert.That(dialog.LastError, Is.Null);
                Assert.That(dialog.HasRepositoryChanges, Is.True);
            });
            Assert.That(await repository.GetNoteAsync(hash), Is.EqualTo(newNote));
            Assert.That(await repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(hash));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task SparseEditorAppliesDirectoriesAndReloadsExactNonConeRules()
    {
        using Fixture fixture = await Fixture.CreateAsync("anchor\n", "anchor\n");
        foreach (string directory in new[] { "selected", "excluded" })
        {
            Directory.CreateDirectory(Path.Combine(fixture.Repository, directory));
            await File.WriteAllTextAsync(Path.Combine(fixture.Repository, directory, "file.txt"), directory + "\n");
        }
        await Git(fixture, "add", "--all");
        await Git(fixture, "commit", "-m", "Add sparse directories");
        GitRepository repository = new(fixture.Repository);
        SparseCheckoutDialog dialog = new(repository);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            CheckBox enabled = Field<CheckBox>(dialog, "SparseEnabled");
            TextBox rules = Field<TextBox>(dialog, "SparseRules");
            Assert.That(rules.IsEnabled, Is.False);
            enabled.IsChecked = true;
            Assert.That(rules.IsEnabled, Is.True);
            rules.Text = "selected\n";
            Click(dialog, "SaveSparse");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null);
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(fixture.Repository, "selected", "file.txt")), Is.True);
                Assert.That(File.Exists(Path.Combine(fixture.Repository, "excluded", "file.txt")), Is.False);
                Assert.That(File.Exists(Path.Combine(fixture.Repository, "file.txt")), Is.True, "Cone mode retains root-level files.");
            });
            Field<CheckBox>(dialog, "SparseCone").IsChecked = false;
            rules.Text = "/*\n!/excluded/\n";
            Click(dialog, "SaveSparse");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null);
            await dialog.InitializeAsync();
            Assert.That(rules.Text, Is.EqualTo("/*\n!/excluded/"));
            Assert.That(Field<CheckBox>(dialog, "SparseCone").IsChecked, Is.False);
            Assert.That(Field<CheckBox>(dialog, "SparseIndex").IsEnabled, Is.False);
            Capture(dialog, "classic-sparse.png");
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ClassicBisectButtonsFindTheCulpritAndRestoreTheOriginalBranch()
    {
        using Fixture fixture = await Fixture.CreateAsync("state 0\n", "state 0\n");
        GitRepository repository = new(fixture.Repository);
        string first = await repository.ResolveRevisionAsync("HEAD");
        string culprit = "";
        for (int index = 1; index <= 4; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), $"state {index}\n");
            await Git(fixture, "add", "--", "file.txt");
            await Git(fixture, "commit", "-m", $"Change {index}");
            if (index == 2) { culprit = await repository.ResolveRevisionAsync("HEAD"); }
        }
        string original = await repository.ResolveRevisionAsync("HEAD");
        await repository.StartBisectAsync(original, [first]);
        BisectDialog dialog = new(repository);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(Field<Button>(dialog, "Start").IsEnabled, Is.False);
            Assert.That(Field<Button>(dialog, "Stop").IsEnabled, Is.True);
            for (int step = 0; step < 8; step++)
            {
                GitBisectState state = await repository.GetBisectStateAsync();
                if (state.FirstBadRevision is not null) { break; }
                string content = await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "file.txt"));
                int number = int.Parse(content.AsSpan(6).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                Click(dialog, number >= 2 ? "Bad" : "Good");
                DateTime resultDeadline = DateTime.UtcNow.AddSeconds(10);
                while (!dialog.OwnedWindows.OfType<GitProcessDialog>().Any(window => !window.IsRunning) && DateTime.UtcNow < resultDeadline)
                { await Task.Delay(10); }
                GitProcessDialog output = dialog.OwnedWindows.OfType<GitProcessDialog>().Single();
                Assert.That(output.IsRunning, Is.False); Assert.That(output.Outcome?.Success, Is.True, output.Error?.ToString());
                GitResult native = output.Result!;
                Assert.That(output.GetVisualDescendants().OfType<TextBox>().Single(text => text.Name == "ProcessOutput").Text,
                    Is.EqualTo(output.CommandDescription + "\n" + native.StandardOutput + native.StandardError + "Done"));
                if ((await repository.GetBisectStateAsync()).FirstBadRevision is not null)
                {
                    Assert.That(native.StandardOutput + native.StandardError, Does.Contain(culprit));
                }
                output.UpdateLayout(); Button ok = output.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Ok");
                Point center = ok.TranslatePoint(new Point(ok.Bounds.Width / 2, ok.Bounds.Height / 2), output)!.Value;
                Assert.That((output.InputHitTest(center) as Visual)?.GetSelfAndVisualAncestors().Contains(ok), Is.True);
                output.MouseMove(center); output.MouseDown(center, MouseButton.Left); output.MouseUp(center, MouseButton.Left);
                await Idle(dialog);
                Assert.That(dialog.LastError, Is.Null);
                DateTime closeDeadline = DateTime.UtcNow.AddSeconds(10);
                while (dialog.IsVisible && DateTime.UtcNow < closeDeadline) { await Task.Delay(10); }
                Assert.That(dialog.IsVisible, Is.False, "Good/Bad classify the current checkout and close the original five-button window.");
                dialog = new BisectDialog(repository); dialog.Show(); await dialog.InitializeAsync();
            }
            Assert.That((await repository.GetBisectStateAsync()).FirstBadRevision, Is.EqualTo(culprit));
            Assert.That(Field<Button>(dialog, "Bad").IsEnabled, Is.True, "Windows enables classification while bisect remains active, including its terminal result.");
            Assert.That(dialog.GetVisualDescendants().OfType<Button>().Count(), Is.EqualTo(5));
            Assert.That(dialog.GetVisualDescendants().OfType<TextBox>(), Is.Empty, "The source window has no extra revision field or log viewer.");
            Capture(dialog, "classic-bisect.png");
            Click(dialog, "Stop");
            DateTime resetDeadline = DateTime.UtcNow.AddSeconds(10);
            while (!dialog.OwnedWindows.OfType<GitProcessDialog>().Any(window => !window.IsRunning) && DateTime.UtcNow < resetDeadline) { await Task.Delay(10); }
            GitProcessDialog resetOutput = dialog.OwnedWindows.OfType<GitProcessDialog>().Single();
            Assert.That(resetOutput.Outcome?.Success, Is.True, resetOutput.Error?.ToString());
            resetOutput.UpdateLayout(); Button resetOk = resetOutput.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Ok");
            Point resetCenter = resetOk.TranslatePoint(new Point(resetOk.Bounds.Width / 2, resetOk.Bounds.Height / 2), resetOutput)!.Value;
            Assert.That((resetOutput.InputHitTest(resetCenter) as Visual)?.GetSelfAndVisualAncestors().Contains(resetOk), Is.True);
            resetOutput.MouseMove(resetCenter); resetOutput.MouseDown(resetCenter, MouseButton.Left); resetOutput.MouseUp(resetCenter, MouseButton.Left);
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null);
            DateTime stopDeadline = DateTime.UtcNow.AddSeconds(10);
            while (dialog.IsVisible && DateTime.UtcNow < stopDeadline) { await Task.Delay(10); }
            Assert.That(dialog.IsVisible, Is.False, "Stop restores the original checkout and closes.");
            Assert.That((await repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
            Assert.That(await repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(original));
            dialog = new BisectDialog(repository); dialog.Show(); await dialog.InitializeAsync();
            Assert.That(Field<Button>(dialog, "Start").IsEnabled, Is.True);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task PluginPreviewRequiresExplicitSelectionAndConfirmationBeforeDeletingBranches()
    {
        using Fixture fixture = await Fixture.CreateAsync("anchor\n", "anchor\n");
        GitRepository repository = new(fixture.Repository);
        await repository.CreateBranchAsync("obsolete-topic", checkout: false);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "latest.txt"), "Main advanced\n");
        await Git(fixture, "add", "--", "latest.txt");
        await Git(fixture, "commit", "-m", "Advance main beyond merged topic");
        PluginDialog dialog = new(repository, new DeleteUnusedBranchesPlugin());
        dialog.Show();
        try
        {
            Click(dialog, "RunPlugin");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null);
            Assert.That(dialog.LastResult?.RepositoryChanged, Is.False);
            ListBox rows = Field<ListBox>(dialog, "PluginRows");
            PluginRow branch = rows.ItemsSource!.OfType<PluginRow>().Single(row => row.Key == "refs/heads/obsolete-topic");
            Assert.That(branch.Cells[5], Is.EqualTo("Eligible"));
            Assert.That(rows.ItemsSource!.OfType<PluginRow>().Single(row => row.Key == "refs/heads/main").Cells[5], Is.EqualTo("Excluded"));
            rows.SelectedItem = branch;
            Click(dialog, "SelectPluginRows");
            Assert.That(Field<TextBox>(dialog, "branches").Text, Is.EqualTo("refs/heads/obsolete-topic"));
            Assert.That((await repository.GetReferencesAsync()).Any(reference => reference.ShortName == "obsolete-topic"), Is.True);
            Capture(dialog, "classic-plugin-branches.png");

            Field<ComboBox>(dialog, "operation").SelectedItem = "delete";
            Click(dialog, "RunPlugin");
            Window confirmation = dialog.OwnedWindows.Single();
            Assert.That(confirmation.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("refs/heads/obsolete-topic", StringComparison.Ordinal) is true), Is.True);
            confirmation.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(20);
            Assert.That((await repository.GetReferencesAsync()).Any(reference => reference.ShortName == "obsolete-topic"), Is.True);

            Click(dialog, "RunPlugin");
            confirmation = dialog.OwnedWindows.Single();
            confirmation.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Delete obsolete branches").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(20);
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.HasRepositoryChanges, Is.True);
            Assert.That((await repository.GetReferencesAsync()).Any(reference => reference.ShortName == "obsolete-topic"), Is.False);
            Assert.That((await repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public void HistoryFilterUsesClassicRowsAndReturnsOnlyEnabledFilters()
    {
        GitLogQuery original = new(Count: 42, Revision: "HEAD", All: false, Author: "Name[1]", Message: "release λ", Path: "dir/file.txt", FirstParent: true, HideMergeCommits: true,
            Since: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), Until: new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
        HistoryFilterDialog dialog = new(original);
        dialog.Show();
        try
        {
            GitLogQuery query = dialog.ReadQuery();
            Assert.Multiple(() =>
            {
                Assert.That(query.Author, Is.EqualTo("Name[1]"));
                Assert.That(query.Message, Is.EqualTo("release λ"));
                Assert.That(query.Path, Is.EqualTo("dir/file.txt"));
                Assert.That(query.Count, Is.EqualTo(42));
                Assert.That(query.FirstParent && query.HideMergeCommits, Is.True);
                Assert.That(query.All, Is.False);
                Assert.That(query.Revision, Is.EqualTo("HEAD"));
                Assert.That(query.Since?.Date, Is.EqualTo(original.Since?.Date));
                Assert.That(query.Until?.Date, Is.EqualTo(original.Until?.Date));
                Assert.That(query.Until?.TimeOfDay, Is.EqualTo(TimeSpan.FromDays(1) - TimeSpan.FromTicks(1)));
                Assert.That(Field<TextBox>(dialog, "Author").IsEnabled, Is.True);
                Assert.That(Field<TextBox>(dialog, "Committer").IsEnabled, Is.False);
                Assert.That(Field<TextBox>(dialog, "BranchFilter").IsEnabled, Is.False);
            });
            Field<CheckBox>(dialog, "AuthorCheck").IsChecked = false;
            Assert.That(dialog.ReadQuery().Author, Is.Null);
            Field<CheckBox>(dialog, "CurrentBranchOnly").IsChecked = false;
            Field<CheckBox>(dialog, "BranchFilterCheck").IsChecked = true;
            Field<TextBox>(dialog, "BranchFilter").Text = "feature";
            Assert.That(dialog.ReadQuery().BranchFilter, Is.EqualTo("feature"));
            Assert.That(Field<TextBox>(dialog, "BranchFilter").IsEnabled, Is.True);
            Capture(dialog, "classic-history-filter.png");
        }
        finally { dialog.Close(); }
    }

    private static T Field<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Click(Window window, string name)
    {
        Button button = Field<Button>(window, name);
        Assert.That(button.IsEffectivelyVisible && button.IsEnabled, Is.True, name + " must be actionable.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task Idle(RepositoryDialog dialog)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (dialog.IsBusy && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        Assert.That(dialog.IsBusy, Is.False, "Repository dialog did not complete within 15 seconds.");
    }

    private static async Task Git(Fixture fixture, params string[] arguments)
    {
        GitResult result = await fixture.Git.RunAsync(fixture.Repository, arguments);
        Assert.That(result.Success, Is.True, result.StandardError);
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        using Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name);
        frame!.Save(path);
        TestContext.AddTestAttachment(path);
    }
}

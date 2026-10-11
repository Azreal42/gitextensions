using System.Globalization;
using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class ExportDialogTests
{
    [AvaloniaTest]
    public async Task BrowserCommandsOpenNativePatchDialogsWithTheSelectedRevision()
    {
        using MainWindowClassicTests.RepositoryFixture fixture = await MainWindowClassicTests.RepositoryFixture.CreateAsync("base\n", "base\n");
        MainWindow main = fixture.Window;
        ListBox history = main.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "RevisionHistory");
        GitCommit selected = history.ItemsSource!.Cast<GitCommit>().Single();
        history.SelectedItem = selected;
        await Until(() => !main.IsBusy);
        Menu menu = main.GetVisualDescendants().OfType<Menu>().Single();
        MenuItem commands = menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Commands");
        commands.Items.OfType<MenuItem>().Single(item => item.Header as string == "Create patches…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => main.OwnedWindows.OfType<FormatPatchDialog>().Any());
        FormatPatchDialog format = main.OwnedWindows.OfType<FormatPatchDialog>().Single();
        await Idle(format);
        try
        {
            Assert.That(format.ReadOptions().SelectedRevisions, Is.EqualTo(new[] { selected.Hash }));
            string output = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "browser-patches");
            Field<TextBox>(format, "PatchOutputDirectory").Text = output;
            Click(format, "CreatePatches");
            await Idle(format);
            Assert.That(format.LastError, Is.Null, format.LastException?.ToString());
            Assert.That(format.LastResult?.Files.Count, Is.EqualTo(1));
        }
        finally { format.Close(); }
        await Until(() => !main.IsBusy);
        commands.Items.OfType<MenuItem>().Single(item => item.Header as string == "Apply patch…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => main.OwnedWindows.OfType<ApplyPatchDialog>().Any());
        ApplyPatchDialog apply = main.OwnedWindows.OfType<ApplyPatchDialog>().Single();
        await Idle(apply);
        Assert.That(apply.AmState?.IsActive, Is.False);
        apply.Close();
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }

    [AvaloniaTest]
    public async Task ArchivePreservesLiteralSelectedNamesAndRefusesToOverwriteTheDestination()
    {
        using ExportFixture fixture = await ExportFixture.CreateAsync();
        const string unusual = "literal[1]\nλ.txt";
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, unusual), "literal content\n");
        await fixture.CommitAsync("Unusual file");
        ArchiveDialog dialog = new(fixture.Repository);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Click(dialog, "SelectArchivePaths");
            await Idle(dialog);
            Field<ListBox>(dialog, "ArchiveExactPaths").SelectedItem = unusual;
            Field<TextBox>(dialog, "ArchiveOutput").Text = fixture.Output("selected.zip");
            Field<TextBox>(dialog, "ArchivePrefix").Text = "release";
            Click(dialog, "SaveArchive");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.LastResult?.Paths, Is.EqualTo(new[] { unusual }));
            Assert.That(dialog.HasRepositoryChanges, Is.False);
            using (ZipArchive zip = ZipFile.OpenRead(fixture.Output("selected.zip")))
            {
                ZipArchiveEntry entry = zip.Entries.Single(entry => !entry.FullName.EndsWith('/'));
                Assert.That(entry.FullName, Is.EqualTo("release/" + unusual));
                using StreamReader reader = new(entry.Open());
                Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("literal content\n"));
            }
            byte[] original = await File.ReadAllBytesAsync(fixture.Output("selected.zip"));
            Click(dialog, "SaveArchive");
            await Idle(dialog);
            Assert.That(dialog.LastError, Does.Contain("exists"));
            Assert.That(await File.ReadAllBytesAsync(fixture.Output("selected.zip")), Is.EqualTo(original));
            Assert.That(Field<TextBox>(dialog, "ArchiveRevision").Bounds.Height, Is.EqualTo(24));
            Capture(dialog, "classic-archive.png");
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ArchiveRevalidatesTypedRevisionAndKeepsChangedFilesFilterExclusive()
    {
        using ExportFixture fixture = await ExportFixture.CreateAsync();
        string initial = await fixture.Repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "changed\n");
        await fixture.CommitAsync("Later revision");
        ArchiveDialog dialog = new(fixture.Repository);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Field<CheckBox>(dialog, "ArchivePathsEnabled").IsChecked = true;
            Field<CheckBox>(dialog, "ArchiveComparisonEnabled").IsChecked = true;
            Assert.That(Field<CheckBox>(dialog, "ArchivePathsEnabled").IsChecked, Is.False);
            Assert.That(Field<TextBox>(dialog, "ArchiveComparison").IsEnabled, Is.True);
            Field<CheckBox>(dialog, "ArchiveComparisonEnabled").IsChecked = false;
            Field<TextBox>(dialog, "ArchiveRevision").Text = initial;
            Field<TextBox>(dialog, "ArchiveOutput").Text = fixture.Output("initial.zip");
            Click(dialog, "SaveArchive");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(Field<TextBlock>(dialog, "ArchiveSummary").Text, Does.StartWith("Initial commit"));
            Assert.That(dialog.LastResult?.Revision, Is.EqualTo(initial));
            using ZipArchive zip = ZipFile.OpenRead(fixture.Output("initial.zip"));
            using StreamReader reader = new(zip.GetEntry("file.txt")!.Open());
            Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("base\n"));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task FormatPatchKeepsOriginalTwoRevisionRangeAndMultipleIndividualSelection()
    {
        using ExportFixture fixture = await ExportFixture.CreateAsync();
        for (int index = 1; index <= 3; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), $"state {index}\n");
            await fixture.CommitAsync($"Change {index}");
        }
        FormatPatchDialog dialog = new(fixture.Repository);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            ListBox revisions = Field<ListBox>(dialog, "FormatRevisions");
            GitCommit[] commits = revisions.ItemsSource!.Cast<GitCommit>().ToArray();
            revisions.SelectedItems!.Clear();
            revisions.SelectedItems.Add(commits[0]);
            revisions.SelectedItems.Add(commits[2]);
            Field<TextBox>(dialog, "PatchOutputDirectory").Text = fixture.Output("range");
            Expand(dialog);
            Field<NumericUpDown>(dialog, "PatchStartNumber").Value = 7;
            Field<CheckBox>(dialog, "PatchSignOff").IsChecked = true;
            Field<TextBox>(dialog, "PatchSubjectPrefix").Text = "REVIEW";
            Field<ComboBox>(dialog, "PatchNumbered").SelectedIndex = 2;
            Assert.That(dialog.ReadOptions().SelectedRevisions, Is.EqualTo(new[] { commits[2].Hash, commits[0].Hash }));
            Click(dialog, "CreatePatches");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.LastResult?.Files.Count, Is.EqualTo(3), "Two selected endpoints include the intervening commit.");
            string first = await File.ReadAllTextAsync(dialog.LastResult!.Files[0]);
            Assert.That(Path.GetFileName(dialog.LastResult.Files[0]), Does.StartWith("0007-"));
            Assert.That(first, Does.Contain("[REVIEW]").And.Contain("Signed-off-by:"));
            revisions.SelectedItems.Clear();
            foreach (GitCommit commit in new[] { commits[0], commits[2], commits[3] }) { revisions.SelectedItems.Add(commit); }
            Assert.That(Field<CheckBox>(dialog, "PatchCoverLetter").IsEnabled, Is.False);
            Assert.That(dialog.ReadOptions().SelectedRevisions, Is.EqualTo(new[] { commits[3].Hash, commits[2].Hash, commits[0].Hash }));
            Field<TextBox>(dialog, "PatchOutputDirectory").Text = fixture.Output("individual");
            Click(dialog, "CreatePatches");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            string[] subjects = await Task.WhenAll(dialog.LastResult!.Files.Select(path => File.ReadAllTextAsync(path)));
            Assert.That(subjects.Count(text => text.Contains("Subject: [REVIEW]", StringComparison.Ordinal)), Is.EqualTo(3));
            Assert.That(subjects.Any(text => text.Contains("Subject: [REVIEW] Change 2", StringComparison.Ordinal)), Is.False);
            Assert.That(revisions.GetVisualDescendants().OfType<ListBoxItem>().Select(item => item.Bounds.Height), Is.All.EqualTo(24));
            Capture(dialog, "classic-format-patch.png");
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task FormatPatchPreselectsTheRequestedRevisionEvenOutsideCurrentReferences()
    {
        using ExportFixture fixture = await ExportFixture.CreateAsync();
        await fixture.GitAsync("checkout", "-b", "temporary");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "unreferenced\n");
        await fixture.CommitAsync("Earlier selected revision");
        string selected = await fixture.Repository.ResolveRevisionAsync("HEAD");
        await fixture.GitAsync("checkout", "main");
        await fixture.GitAsync("branch", "-D", "temporary");
        FormatPatchDialog dialog = new(fixture.Repository, selected);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.ReadOptions().SelectedRevisions, Is.EqualTo(new[] { selected }));
            Field<TextBox>(dialog, "PatchOutputDirectory").Text = fixture.Output("selected-history");
            Click(dialog, "CreatePatches");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(await File.ReadAllTextAsync(dialog.LastResult!.Files.Single()), Does.Contain("Earlier selected revision"));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task OriginalPatchChoicesPersistInXdgWithoutOverwritingGeneralPreferences()
    {
        using ExportFixture fixture = await ExportFixture.CreateAsync();
        PreferencesStore general = new();
        general.Save(new DesktopPreferences { Theme = "Dark", HistoryLimit = 42 });
        FormatPatchDialog format = new(fixture.Repository);
        format.Show();
        Field<TextBox>(format, "PatchOutputDirectory").Text = fixture.Output("existing");
        Directory.CreateDirectory(fixture.Output("existing"));
        Field<TextBox>(format, "PatchOutputDirectory").Text = fixture.Output("existing") + Path.DirectorySeparatorChar;
        format.Close();
        ApplyPatchDialog apply = new(fixture.Repository);
        apply.Show();
        Field<ComboBox>(apply, "PatchInputKind").SelectedIndex = 2;
        Field<CheckBox>(apply, "ApplyIgnoreWhitespace").IsChecked = true;
        Field<CheckBox>(apply, "ApplySignOff").IsChecked = true;
        apply.Close();
        format = new(fixture.Repository);
        apply = new(fixture.Repository);
        format.Show();
        apply.Show();
        try
        {
            Assert.That(Field<TextBox>(format, "PatchOutputDirectory").Text, Is.EqualTo(fixture.Output("existing") + Path.DirectorySeparatorChar));
            Assert.That(Field<CheckBox>(apply, "ApplyIgnoreWhitespace").IsChecked, Is.True);
            Assert.That(Field<CheckBox>(apply, "ApplySignOff").IsChecked, Is.True);
            Assert.That(general.Load().Theme, Is.EqualTo("Dark"));
            Assert.That(general.Load().HistoryLimit, Is.EqualTo(42));
        }
        finally { apply.Close(); format.Close(); }
    }

    [AvaloniaTest]
    public async Task RawPatchCheckDoesNotChangeFilesAndApplyingItUsesTheChosenTarget()
    {
        using ExportFixture fixture = await ExportFixture.CreateAsync();
        string patch = await fixture.CreateDiffAsync("incoming\n");
        ApplyPatchDialog dialog = new(fixture.Repository, patch);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Expand(dialog);
            Assert.That(Field<DiffView>(dialog, "PatchPreview").Text, Does.Contain("+incoming"));
            Field<CheckBox>(dialog, "ApplyCheckOnly").IsChecked = true;
            Click(dialog, "ApplyPatch");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.HasRepositoryChanges, Is.False);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt")), Is.EqualTo("base\n"));
            Field<CheckBox>(dialog, "ApplyCheckOnly").IsChecked = false;
            Field<ComboBox>(dialog, "ApplyTarget").SelectedIndex = 1;
            Click(dialog, "ApplyPatch");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.HasRepositoryChanges, Is.True);
            Assert.That((await fixture.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("incoming\n"));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt")), Is.EqualTo("base\n"));
            Assert.That(Field<Button>(dialog, "ContinuePatch").IsEnabled, Is.False);
            Capture(dialog, "classic-apply-patch.png");
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task MailboxConflictCanBeEditedStagedAndContinuedThroughNativeControls()
    {
        using ExportFixture fixture = await ExportFixture.CreateAsync();
        string patch = await fixture.CreateConflictingMailboxAsync();
        ApplyPatchDialog dialog = new(fixture.Repository, patch);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Click(dialog, "ApplyPatch");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Not.Null);
            Assert.That(dialog.AmState?.IsActive, Is.True);
            Assert.That(dialog.AmState?.ConflictedPaths, Is.EqualTo(new[] { "file.txt" }));
            Assert.That(Field<Button>(dialog, "ContinuePatch").IsEnabled, Is.False);
            Capture(dialog, "classic-apply-conflict.png");
            Click(dialog, "ResolvePatchConflicts");
            await Until(() => dialog.OwnedWindows.OfType<RepositoryDialog>().Any());
            RepositoryDialog resolution = dialog.OwnedWindows.OfType<RepositoryDialog>().Single();
            await Idle(resolution);
            Field<TextBox>(resolution, "PatchConflictText").Text = "resolved content\n";
            Click(resolution, "SavePatchResolution");
            await Until(() => !dialog.OwnedWindows.Any());
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(Field<Button>(dialog, "ContinuePatch").IsEnabled, Is.True);
            Click(dialog, "ContinuePatch");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.AmState?.IsActive, Is.False);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt")), Is.EqualTo("resolved content\n"));
            Assert.That((await fixture.Repository.GetLogAsync(1)).Single().Subject, Is.EqualTo("Incoming change"));
        }
        finally { foreach (Window child in dialog.OwnedWindows.ToArray()) { child.Close(); } dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task InterruptedMailboxOffersRealSkipAndConfirmedAbortRestoringTheOriginalHead()
    {
        foreach (bool abort in new[] { false, true })
        {
            using ExportFixture fixture = await ExportFixture.CreateAsync();
            string patch = await fixture.CreateConflictingMailboxAsync();
            string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
            ApplyPatchDialog dialog = new(fixture.Repository, patch);
            dialog.Show();
            try
            {
                await dialog.InitializeAsync();
                Click(dialog, "ApplyPatch");
                await Idle(dialog);
                Assert.That(dialog.AmState?.IsActive, Is.True);
                Click(dialog, abort ? "AbortPatch" : "SkipPatch");
                if (abort)
                {
                    Window confirmation = dialog.OwnedWindows.Single();
                    Confirm(confirmation, false);
                    await Task.Delay(20);
                    Assert.That((await fixture.Repository.GetAmStateAsync()).IsActive, Is.True);
                    Click(dialog, "AbortPatch");
                    Confirm(dialog.OwnedWindows.Single(), true);
                }
                await Task.Delay(20);
                await Idle(dialog);
                Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
                Assert.That(dialog.AmState?.IsActive, Is.False);
                Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
                Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt")), Is.EqualTo("target\n"));
            }
            finally { dialog.Close(); }
        }
    }

    [AvaloniaTest]
    public async Task MailboxDirectoryLoadsItsOrderedSeriesAndAppliesEveryCommitWithSignOff()
    {
        using ExportFixture fixture = await ExportFixture.CreateAsync();
        string initial = await fixture.Repository.ResolveRevisionAsync("HEAD");
        for (int index = 1; index <= 2; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), $"incoming {index}\n");
            await fixture.CommitAsync($"Incoming {index}");
        }
        GitFormatPatchResult patches = await fixture.Repository.FormatPatchAsync(new(fixture.Output("series"), SelectedRevisions: ["HEAD~1", "HEAD"]));
        await fixture.GitAsync("checkout", "-b", "apply-target", initial);
        ApplyPatchDialog dialog = new(fixture.Repository, patches.OutputDirectory);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(Field<ListBox>(dialog, "PatchGrid").ItemsSource!.Cast<object>().Count(), Is.EqualTo(2));
            Assert.That(Field<CheckBox>(dialog, "ApplySignOff").IsEnabled, Is.True);
            Field<CheckBox>(dialog, "ApplySignOff").IsChecked = true;
            Click(dialog, "ApplyPatch");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.AmState?.IsActive, Is.False);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt")), Is.EqualTo("incoming 2\n"));
            string log = (await fixture.GitAsync("log", "-2", "--format=%B")).StandardOutput;
            Assert.That(log, Does.Contain("Incoming 1").And.Contain("Incoming 2"));
            Assert.That(log.Split("Signed-off-by:", StringSplitOptions.None).Length - 1, Is.EqualTo(2));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task SkippingIntoAnotherConflictKeepsTheCorrectPatchProgressAndRecoveryButtons()
    {
        using ExportFixture fixture = await ExportFixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, "second.txt"), "base\n");
        await fixture.CommitAsync("Second base file");
        string initial = await fixture.Repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "incoming first\n");
        await fixture.CommitAsync("Incoming first");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, "second.txt"), "incoming second\n");
        await fixture.CommitAsync("Incoming second");
        GitFormatPatchResult patches = await fixture.Repository.FormatPatchAsync(new(fixture.Output("series"), SelectedRevisions: ["HEAD~1", "HEAD"]));
        await fixture.GitAsync("checkout", "-b", "target", initial);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "target first\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, "second.txt"), "target second\n");
        await fixture.CommitAsync("Target versions");
        ApplyPatchDialog dialog = new(fixture.Repository, patches.OutputDirectory);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Click(dialog, "ApplyPatch");
            await Idle(dialog);
            Assert.That(dialog.AmState?.CurrentPatch, Is.EqualTo(1));
            Click(dialog, "SkipPatch");
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Not.Null, "Git reports the second patch's conflict after skipping the first.");
            Assert.That(dialog.AmState?.CurrentPatch, Is.EqualTo(2));
            Assert.That(dialog.AmState?.ConflictedPaths, Is.EqualTo(new[] { "second.txt" }));
            PatchApplicationRow[] rows = Field<ListBox>(dialog, "PatchGrid").ItemsSource!.Cast<PatchApplicationRow>().ToArray();
            Assert.That(rows.Select(row => row.Status), Is.EqualTo(new[] { "Skipped", "Current" }));
            Assert.That(Field<Button>(dialog, "ContinuePatch").IsEnabled, Is.False);
            Assert.That(Field<Button>(dialog, "ResolvePatchConflicts").IsEnabled, Is.True);
            Click(dialog, "AbortPatch");
            Confirm(dialog.OwnedWindows.Single(), true);
            await Task.Delay(20);
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.AmState?.IsActive, Is.False);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, "second.txt")), Is.EqualTo("target second\n"));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task BareRepositoriesAllowArchivePatchesAndMaintenanceButDisablePatchApplication()
    {
        using ExportFixture fixture = await ExportFixture.CreateAsync();
        string barePath = fixture.Output("bare.git");
        await fixture.GitAsync("clone", "--bare", fixture.Repository.Path, barePath);
        GitRepository bare = await GitRepository.OpenAsync(barePath);
        ArchiveDialog archive = new(bare);
        FormatPatchDialog format = new(bare);
        ApplyPatchDialog apply = new(bare);
        MaintenanceDialog maintenance = new(bare, MaintenanceView.Statistics);
        archive.Show();
        format.Show();
        apply.Show();
        maintenance.Show();
        try
        {
            await archive.InitializeAsync();
            Field<TextBox>(archive, "ArchiveOutput").Text = fixture.Output("bare.zip");
            Click(archive, "SaveArchive");
            await Idle(archive);
            Assert.That(archive.LastError, Is.Null, archive.LastException?.ToString());
            using (ZipArchive zip = ZipFile.OpenRead(fixture.Output("bare.zip"))) { Assert.That(zip.GetEntry("file.txt"), Is.Not.Null); }
            await format.InitializeAsync();
            Field<TextBox>(format, "PatchOutputDirectory").Text = fixture.Output("bare-patches");
            Click(format, "CreatePatches");
            await Idle(format);
            Assert.That(format.LastError, Is.Null, format.LastException?.ToString());
            Assert.That(format.LastResult?.Files.Count, Is.EqualTo(1));
            await apply.InitializeAsync();
            Assert.That(apply.LastError, Is.Null, apply.LastException?.ToString());
            Assert.That(Field<Button>(apply, "ApplyPatch").IsEnabled, Is.False);
            await maintenance.InitializeAsync();
            Assert.That(maintenance.LastError, Is.Null, maintenance.LastException?.ToString());
            Assert.That(maintenance.LastStatistics, Is.Not.Null);
        }
        finally { maintenance.Close(); apply.Close(); format.Close(); archive.Close(); }
    }

    [AvaloniaTest]
    public async Task MaintenanceDisplaysRealObjectStatisticsAndRequiresConfirmationForNormalGc()
    {
        using ExportFixture fixture = await ExportFixture.CreateAsync();
        string looseFile = fixture.Output("dangling.txt");
        await File.WriteAllTextAsync(looseFile, "unreferenced content\n");
        string dangling = (await fixture.GitAsync("hash-object", "-w", "--", looseFile)).StandardOutput.Trim();
        MaintenanceDialog dialog = new(fixture.Repository);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.LastVerification?.IsValid, Is.True);
            Assert.That(dialog.LastVerification?.Issues.Any(issue => issue.Hash == dangling && issue.Kind == GitFsckIssueKind.Dangling), Is.True);
            Click(dialog, "ReadStatistics");
            await Idle(dialog);
            Assert.That(dialog.LastStatistics?.LooseObjects, Is.GreaterThan(0));
            Assert.That(Field<TextBox>(dialog, "RepositoryStatistics").Text, Does.Contain("Loose objects:"));
            Capture(dialog, "classic-maintenance.png");
            Click(dialog, "CollectGarbage");
            Confirm(dialog.OwnedWindows.Single(), false);
            await Task.Delay(20);
            Assert.That(dialog.HasRepositoryChanges, Is.False);
            Click(dialog, "CollectGarbage");
            Confirm(dialog.OwnedWindows.Single(), true);
            await Task.Delay(20);
            await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.HasRepositoryChanges, Is.True);
            Assert.That(dialog.LastStatistics?.PackFiles, Is.GreaterThan(0));
            Assert.That((await fixture.Repository.VerifyObjectsAsync()).IsValid, Is.True);
        }
        finally { dialog.Close(); }
    }

    private static T Field<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Expand(Window window)
    {
        foreach (Expander expander in window.GetVisualDescendants().OfType<Expander>().ToArray()) { expander.IsExpanded = true; }
        window.UpdateLayout();
    }

    private static void Click(Window window, string name)
    {
        Button button = Field<Button>(window, name);
        Assert.That(button.IsEffectivelyVisible && button.IsEnabled, Is.True, name + " must be actionable.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Confirm(Window window, bool accept)
        => window.GetVisualDescendants().OfType<Button>().Single(button => (button.Content as string == "Cancel") != accept).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Task Idle(RepositoryDialog dialog) => Until(() => !dialog.IsBusy);

    private static async Task Until(Func<bool> complete)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!complete() && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        Assert.That(complete(), Is.True, "The native dialog did not complete within 15 seconds.");
    }

    private static void Capture(Window dialog, string name)
    {
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using Avalonia.Media.Imaging.WriteableBitmap? frame = dialog.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name);
        frame!.Save(path);
        TestContext.AddTestAttachment(path, "Classic native dialog operating on a real Git repository");
    }

    private sealed class ExportFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-export-ui-" + Guid.NewGuid().ToString("N"));
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly GitRunner _git = new();
        public GitRepository Repository { get; }
        private ExportFixture()
        {
            Directory.CreateDirectory(_root);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_root, "config"));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            Repository = new GitRepository(Path.Combine(_root, "repo"));
            Directory.CreateDirectory(Repository.Path);
        }
        internal static async Task<ExportFixture> CreateAsync()
        {
            ExportFixture fixture = new();
            try
            {
                await fixture.GitAsync("init", "--initial-branch=main");
                await fixture.GitAsync("config", "user.name", "Export Tester");
                await fixture.GitAsync("config", "user.email", "export@example.com");
                await fixture.GitAsync("config", "commit.gpgsign", "false");
                await fixture.GitAsync("config", "core.hooksPath", fixture.Output("no-hooks"));
                await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "base\n");
                await fixture.CommitAsync("Initial commit");
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        internal string Output(string name) => Path.Combine(_root, name);
        internal async Task<GitResult> GitAsync(params string[] args)
        {
            GitResult result = await _git.RunAsync(Repository.Path, args);
            Assert.That(result.Success, Is.True, result.StandardError);
            return result;
        }
        internal async Task CommitAsync(string subject)
        {
            await Repository.StageAllAsync();
            await GitAsync("commit", "-m", subject);
        }
        internal async Task<string> CreateDiffAsync(string content)
        {
            await File.WriteAllTextAsync(Path.Combine(Repository.Path, "file.txt"), content);
            string path = Output("raw.diff");
            await File.WriteAllTextAsync(path, (await GitAsync("diff", "--binary", "--")).StandardOutput);
            await GitAsync("restore", "--", "file.txt");
            return path;
        }
        internal async Task<string> CreateConflictingMailboxAsync()
        {
            string initial = await Repository.ResolveRevisionAsync("HEAD");
            await File.WriteAllTextAsync(Path.Combine(Repository.Path, "file.txt"), "incoming\n");
            await CommitAsync("Incoming change");
            GitFormatPatchResult patches = await Repository.FormatPatchAsync(new(Output("mailbox"), SelectedRevisions: ["HEAD"]));
            await GitAsync("checkout", "-b", "target", initial);
            await File.WriteAllTextAsync(Path.Combine(Repository.Path, "file.txt"), "target\n");
            await CommitAsync("Target change");
            return patches.Files.Single();
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg);
            CultureInfo.CurrentUICulture = _culture;
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }
}

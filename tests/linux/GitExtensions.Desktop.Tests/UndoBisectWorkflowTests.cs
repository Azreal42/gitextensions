using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class UndoBisectWorkflowTests
{
    [AvaloniaTest]
    public async Task CommandsKeepCommitThenUndoAndUseTheOriginalSelectionAndWorktreeGuards()
    {
        await using Fixture f = await Fixture.Create(); MainWindow main = await f.Main();
        MenuItem commands = Commands(main); commands.IsSubMenuOpen = true; Pump(main);
        MenuItem[] items = commands.Items.OfType<MenuItem>().ToArray();
        Assert.That(items.Take(2).Select(item => item.Name), Is.EqualTo(new[] { "BrowseCommit", "BrowseUndoLastCommit" }));
        Assert.That(((AccessText)items[0].Header!).Text, Is.EqualTo("_Commit…"));
        Assert.That(((Image)items[0].Icon!).Tag, Is.EqualTo("RepoStateClean.png"));
        Assert.That(((AccessText)items[1].Header!).Text, Is.EqualTo("_Undo last commit…"));
        Assert.That(((Image)items[1].Icon!).Tag, Is.EqualTo("ResetFileTo.png"));
        Assert.That(items[1].InputGesture, Is.Null, "No Undo hotkey is declared in the original.");
        commands.IsSubMenuOpen = false; ListBox history = Field<ListBox>(main, "RevisionHistory");
        foreach (int count in new[] { 0, 1, 2 })
        {
            history.SelectedItems!.Clear(); await Idle(main);
            foreach (GitCommit commit in history.Items.OfType<GitCommit>().Take(count)) { history.SelectedItems.Add(commit); await Idle(main); }
            commands.IsSubMenuOpen = true; Pump(main);
            Assert.That(Menu(commands, "BrowseUndoLastCommit").IsEnabled, Is.True, "Undo acts on HEAD, independently of the grid selection.");
            Assert.That(Menu(commands, "BrowseBisect").IsEnabled, Is.EqualTo(count == 1));
            commands.IsSubMenuOpen = false;
        }
        // The original Commit entry must actually open the existing commit workflow.
        items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => main.OwnedWindows.OfType<CommitDialog>().Any());
        CommitDialog commitDialog = main.OwnedWindows.OfType<CommitDialog>().Single();
        await Until(() => !commitDialog.IsBusy); commitDialog.Close(); await Until(() => !commitDialog.IsVisible);
    }

    [AvaloniaTest]
    public async Task UndoUsesHeadRatherThanTheSelectedRevisionAndPreservesTheRawIndexAndDirtyFiles()
    {
        foreach (bool detached in new[] { false, true })
        {
        await using Fixture f = await Fixture.Create(); MainWindow main = await f.Main();
        if (detached) { await f.Git("checkout", "--detach", f.Hashes[^1]); await Invoke(main, "RefreshAsync"); await Idle(main); }
        ListBox history = Field<ListBox>(main, "RevisionHistory"); history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == f.Hashes[1]); await Idle(main);
        f.Write("staged λ.txt", "staged content\n"); await f.Git("add", "--", "staged λ.txt"); f.Write("tracked.txt", "different unstaged content\n");
        f.Write("untracked.txt", "untouched untracked\n"); f.Write("ignored.ignored", "untouched ignored\n"); await Invoke(main, "RefreshAsync"); await Idle(main);
        byte[] index = File.ReadAllBytes(f.File(".git/index")); string worktree = f.Materials(worktreeOnly: true); string head = await f.Head();
        Task action = Invoke(main, "UndoLastCommitAsync"); Window question = await Owned(main, "UndoLastCommitConfirmation");
        if (detached) { Press(question, PhysicalKey.Enter, releaseOn: main); }
        else { Pointer(Field<Button>(question, "ConfirmUndoLastCommit")); }
        await action.WaitAsync(TimeSpan.FromSeconds(20)); await Idle(main);
        Assert.That(main.LastException, Is.Null); Assert.That(await f.Head(), Is.EqualTo(f.Hashes[^2]));
        Assert.That(await f.Text("rev-parse", "HEAD@{1}"), Is.EqualTo(head));
        Assert.That(await f.Text("reflog", "-1", "--format=%gs"), Is.EqualTo("reset: moving to HEAD~1"));
        Assert.That(await f.Text("cat-file", "-t", head), Is.EqualTo("commit"));
        Assert.That(File.ReadAllBytes(f.File(".git/index")), Is.EqualTo(index)); Assert.That(f.Materials(worktreeOnly: true), Is.EqualTo(worktree));
        Assert.That(await f.Text("diff", "--cached", "--name-only", "-z"), Does.Contain("tracked.txt\0").And.Contain("staged λ.txt\0"));
        Assert.That(await f.Text("rev-parse", "main"), Is.EqualTo(detached ? f.Hashes[^1] : f.Hashes[^2]));
        }
    }

    [AvaloniaTest]
    public async Task UndoNoEscapeAndWindowCloseLeaveEveryRepositoryMaterialUnchanged()
    {
        await using Fixture f = await Fixture.Create(); MainWindow main = await f.Main(); string before = f.Materials();
        foreach (string choice in new[] { "No", "Escape", "Close" })
        {
            Task action = Invoke(main, "UndoLastCommitAsync"); Window question = await Owned(main, "UndoLastCommitConfirmation");
            Assert.That(Field<Button>(question, "ConfirmUndoLastCommit").IsDefault, Is.True);
            await Dismiss(question, main, choice, "CancelUndoLastCommit"); await action.WaitAsync(TimeSpan.FromSeconds(15)); await Idle(main);
            Assert.That(f.Materials(), Is.EqualTo(before), choice); Assert.That(new BrowseCommandPreferencesStore().Load().DontConfirmUndoLastCommit, Is.False);
        }
    }

    [AvaloniaTest]
    public async Task UndoSuppressionIsRecordedAfterYesNoEscapeOrCloseAndAppliesToTheNextInvocation()
    {
        foreach (string choice in new[] { "Yes", "No", "Escape", "Close" })
        {
            await using Fixture f = await Fixture.Create(); MainWindow main = await f.Main(); string head = await f.Head();
            Task action = Invoke(main, "UndoLastCommitAsync"); Window question = await Owned(main, "UndoLastCommitConfirmation");
            Pointer(Field<CheckBox>(question, "UndoDontShowAgain"));
            if (choice == "Yes") { Pointer(Field<Button>(question, "ConfirmUndoLastCommit")); }
            else { await Dismiss(question, main, choice, "CancelUndoLastCommit"); }
            await action.WaitAsync(TimeSpan.FromSeconds(20)); await Idle(main);
            Assert.That(new BrowseCommandPreferencesStore().Load().DontConfirmUndoLastCommit, Is.True, choice);
            Assert.That(await f.Head(), Is.EqualTo(choice == "Yes" ? f.Hashes[^2] : head));
            await Invoke(main, "UndoLastCommitAsync").WaitAsync(TimeSpan.FromSeconds(20)); await Idle(main);
            Assert.That(main.OwnedWindows.Any(window => window.Name == "UndoLastCommitConfirmation"), Is.False);
            Assert.That(await f.Head(), Is.EqualTo(choice == "Yes" ? f.Hashes[^3] : f.Hashes[^2]), choice);
            Assert.That(main.LastException, Is.Null);
        }
    }

    [AvaloniaTest]
    public async Task UndoWarningAndBisectQuestionUseTheOriginalFrenchLabelsAndTheOwnerPalette()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = new("fr-FR");
        try
        {
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                await using Fixture f = await Fixture.Create(); MainWindow main = await f.Main(theme); string before = f.Materials();
                MenuItem commands = Commands(main); commands.IsSubMenuOpen = true; Pump(main);
                Assert.That(((AccessText)Menu(commands, "BrowseUndoLastCommit").Header!).Text, Is.EqualTo("Ann_uler le dernier commit…"));
                commands.IsSubMenuOpen = false;
                Task undo = Invoke(main, "UndoLastCommitAsync"); Window warning = await Owned(main, "UndoLastCommitConfirmation");
                Assert.That(warning.Title, Is.EqualTo("Annuler le dernier commit")); Assert.That(warning.ActualThemeVariant, Is.EqualTo(theme));
                Assert.That(Pixel(warning, new Point(3, 3)), Is.EqualTo(theme == ThemeVariant.Dark ? Color.Parse("#323232") : Colors.White));
                Assert.That(Field<Image>(warning, "UndoLastCommitWarning").Tag, Is.EqualTo("Warning.png"));
                Assert.That(((AccessText)Field<CheckBox>(warning, "UndoDontShowAgain").Content!).Text, Is.EqualTo("Ne plus _m'afficher ce message"));
                Assert.That(Field<TextBlock>(warning, "UndoLastCommitMessage").Text, Does.Contain("zone de staging"));
                Assert.That(Field<Button>(warning, "ConfirmUndoLastCommit").IsDefault, Is.True); Capture(warning, "undo-warning-fr-" + theme + ".png");
                Pointer(Field<Button>(warning, "CancelUndoLastCommit")); await undo.WaitAsync(TimeSpan.FromSeconds(15)); Assert.That(f.Materials(), Is.EqualTo(before));
                BisectDialog bisect = await f.Show(new BisectDialog(f.Repository, [f.Hashes[0], f.Hashes[^1]]) { RequestedThemeVariant = theme });
                Pointer(Field<Button>(bisect, "Start")); Window range = await Owned(bisect, "BisectRangeConfirmation");
                Assert.That(range.ActualThemeVariant, Is.EqualTo(theme)); Assert.That(Field<TextBlock>(range, "BisectRangeQuestion").Text,
                    Is.EqualTo("Marquer la révision sélectionnée comme départ pour la bissection ?"));
                Panel icon = Field<Panel>(range, "BisectQuestionIcon"); Assert.That(icon.Bounds.Size, Is.EqualTo(new Size(32, 32)));
                Assert.That(icon.GetVisualDescendants().OfType<TextBlock>().Single().Text, Is.EqualTo("?"));
                Assert.That(icon.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single().Fill, Is.EqualTo(Brushes.RoyalBlue));
                Assert.That(Pixel(range, icon.TranslatePoint(new Point(5, 16), range)!.Value), Is.EqualTo(Colors.RoyalBlue));
                Capture(range, "bisect-question-fr-" + theme + ".png"); Pointer(Field<Button>(range, "CancelBisectRange"));
                await Until(() => Field<Button>(bisect, "Stop").IsEnabled); Pointer(Field<Button>(bisect, "Stop")); await Closed(bisect);
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [AvaloniaTest]
    public async Task UndoAndBisectRefuseBareAndMissingRepositoryWithoutAWindowOrAMutation()
    {
        await using Fixture f = await Fixture.Create(bare: true); MainWindow main = await f.Main(); string before = f.Materials();
        MenuItem commands = Commands(main); commands.IsSubMenuOpen = true; Pump(main);
        Assert.That(Menu(commands, "BrowseUndoLastCommit").IsEnabled, Is.False); Assert.That(Menu(commands, "BrowseBisect").IsEnabled, Is.False);
        commands.IsSubMenuOpen = false; await Invoke(main, "UndoLastCommitAsync"); await Invoke(main, "BisectAsync");
        Assert.That(main.OwnedWindows, Is.Empty); Assert.That(f.Materials(), Is.EqualTo(before));
        MainWindow empty = new(); f.Windows.Add(empty); empty.Show();
        await Invoke(empty, "UndoLastCommitAsync"); await Invoke(empty, "BisectAsync"); Assert.That(empty.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task UndoOfUnbornOrSingleCommitReportsTheNativeMissingParentAndPreservesTheRepository()
    {
        foreach (int commits in new[] { 0, 1 })
        {
            await using Fixture f = await Fixture.Create(commits: commits); MainWindow main = await f.Main(); string before = f.Materials();
            Task action = Invoke(main, "UndoLastCommitAsync"); Window question = await Owned(main, "UndoLastCommitConfirmation");
            Pointer(Field<Button>(question, "ConfirmUndoLastCommit")); await action.WaitAsync(TimeSpan.FromSeconds(20)); await Idle(main);
            Assert.That(main.LastException, Is.TypeOf<GitCommandException>()); GitCommandException error = (GitCommandException)main.LastException!;
            Assert.That(error.Result.Success, Is.False); Assert.That(error.Result.StandardError, Is.Not.Empty);
            Assert.That(error.Arguments, Is.EqualTo(new[] { "reset", "--soft", "--quiet", "HEAD~1", "--" }));
            Assert.That(f.Materials(), Is.EqualTo(before));
        }
    }

    [AvaloniaTest]
    public async Task ClosingTheUndoOwnerWhileConfirmationIsOpenNeverStartsAReset()
    {
        await using Fixture f = await Fixture.Create(); MainWindow main = await f.Main(); string before = f.Materials();
        Task action = Invoke(main, "UndoLastCommitAsync"); await Owned(main, "UndoLastCommitConfirmation"); main.Close();
        await action.WaitAsync(TimeSpan.FromSeconds(15)); await Until(() => !main.IsBusy);
        Assert.That(main.IsVisible, Is.False); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task BisectHasOnlyTheFiveOriginalButtonsAndOpeningItIsReadOnlyInBothThemes()
    {
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            await using Fixture f = await Fixture.Create(); string before = f.Materials();
            BisectDialog dialog = await f.Show(new BisectDialog(f.Repository) { RequestedThemeVariant = theme });
            Button[] buttons = dialog.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.That(buttons.Select(button => button.Name), Is.EqualTo(new[] { "Start", "Bad", "Good", "btnSkip", "Stop" }));
            Assert.That(buttons.Select(button => ((AccessText)button.Content!).Text), Is.EqualTo(new[] { "Start bisect", "Mark current revision _bad", "Mark current revision _good", "_Skip current revision", "Stop bisect" }));
            Assert.That(buttons.All(button => button.Bounds.Width >= 224 && button.Bounds.Height == 25), Is.True);
            Assert.That(dialog.CanResize, Is.False); Assert.That(dialog.WindowStartupLocation, Is.EqualTo(WindowStartupLocation.CenterOwner));
            Assert.That(dialog.GetVisualDescendants().OfType<TextBox>(), Is.Empty); Assert.That(dialog.GetVisualDescendants().OfType<Expander>(), Is.Empty);
            Assert.That(buttons.Select(button => button.IsEnabled), Is.EqualTo(new[] { true, false, false, false, false }));
            Capture(dialog, "bisect-original-buttons-" + theme + ".png"); Assert.That(f.Materials(), Is.EqualTo(before)); dialog.Close();
        }
    }

    [AvaloniaTest]
    public async Task BisectStartWithZeroOrOneSelectionLeavesAnActiveUnboundedSessionAndStopRestoresHead()
    {
        await using Fixture f = await Fixture.Create(); string initialHead = await f.Head();
        foreach (string[] selected in new[] { Array.Empty<string>(), new[] { f.Hashes[1] } })
        {
            BisectDialog dialog = await f.Show(new BisectDialog(f.Repository, selected)); Pointer(Field<Button>(dialog, "Start"));
            await AcknowledgePhase(dialog);
            await Until(() => !dialog.IsBusy && Field<Button>(dialog, "Stop").IsEnabled);
            Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.OwnedWindows, Is.Empty);
            GitBisectState state = await f.Repository.GetBisectStateAsync(); Assert.That(state.IsActive, Is.True);
            Assert.That(state.GoodRevisions, Is.Empty); Assert.That(state.BadRevision, Is.Null); Assert.That(await f.Head(), Is.EqualTo(initialHead));
            Pointer(Field<Button>(dialog, "Stop")); await Closed(dialog); Assert.That((await f.Repository.GetBisectStateAsync()).IsActive, Is.False);
            Assert.That(await f.Head(), Is.EqualTo(initialHead)); Assert.That(await f.Text("symbolic-ref", "--short", "HEAD"), Is.EqualTo("main"));
        }
    }

    [AvaloniaTest]
    public async Task BisectSnapshotsTheChosenFirstAndLastBoundsAndMatchesSequentialNativeGit()
    {
        await using Fixture f = await Fixture.Create(); string oracle = await f.CopyRepository("oracle");
        List<string> selected = [f.Hashes[0], f.Hashes[3], f.Hashes[^1]];
        BisectDialog dialog = new(f.Repository, selected); selected.Reverse(); selected.Add("not-a-ref"); await f.Show(dialog);
        Assert.That(dialog.SelectedRevisionHashes, Is.EqualTo(new[] { f.Hashes[0], f.Hashes[3], f.Hashes[^1] }));
        Assert.That(() => ((IList<string>)dialog.SelectedRevisionHashes).Add("mutation"), Throws.TypeOf<NotSupportedException>());
        Pointer(Field<Button>(dialog, "Start")); Window range = await Owned(dialog, "BisectRangeConfirmation");
        Assert.That(File.Exists(f.File(".git/BISECT_START")), Is.True); Assert.That((await f.Repository.GetBisectStateAsync()).BadRevision, Is.Null);
        Field<Button>(range, "ConfirmBisectRange").Focus(); Press(range, PhysicalKey.Enter, releaseOn: dialog); await Closed(dialog);
        await f.GitAt(oracle, "bisect", "start"); await f.GitAt(oracle, "bisect", "good", f.Hashes[0]); await f.GitAt(oracle, "bisect", "bad", f.Hashes[^1]);
        GitBisectState actual = await f.Repository.GetBisectStateAsync(); GitBisectState expected = await new GitRepository(oracle).GetBisectStateAsync();
        Assert.Multiple(() =>
        {
            Assert.That(actual.IsActive, Is.EqualTo(expected.IsActive)); Assert.That(actual.CurrentRevision, Is.EqualTo(expected.CurrentRevision));
            Assert.That(actual.BadRevision, Is.EqualTo(expected.BadRevision)); Assert.That(actual.StartingRevision, Is.EqualTo(expected.StartingRevision));
            Assert.That(actual.GoodRevisions, Is.EqualTo(expected.GoodRevisions)); Assert.That(actual.Log, Is.EqualTo(expected.Log));
        });
        Assert.That(actual.GoodRevisions, Is.EqualTo(new[] { f.Hashes[0] })); Assert.That(actual.Log, Does.Not.Contain("git bisect good " + f.Hashes[3]));
        Assert.That((await f.GitAt(oracle, "ls-files", "--stage", "-z")).StandardOutput, Is.EqualTo((await f.Git("ls-files", "--stage", "-z")).StandardOutput));
        Assert.That(File.ReadAllBytes(Path.Combine(oracle, "tracked.txt")), Is.EqualTo(File.ReadAllBytes(f.File("tracked.txt"))));
    }

    [AvaloniaTest]
    public async Task MainBisectPassesTheActualSelectionCollectionOrderRatherThanTheHistorySortOrder()
    {
        await using Fixture f = await Fixture.Create(); MainWindow main = await f.Main(); ListBox history = Field<ListBox>(main, "RevisionHistory");
        history.SelectedItems!.Clear(); await Idle(main);
        foreach (string hash in new[] { f.Hashes[0], f.Hashes[3], f.Hashes[^1] }) { history.SelectedItems.Add(history.Items.OfType<GitCommit>().Single(commit => commit.Hash == hash)); await Idle(main); }
        string[] chosen = history.SelectedItems.Cast<GitCommit>().Select(commit => commit.Hash).ToArray(); Assert.That(chosen, Is.EqualTo(new[] { f.Hashes[0], f.Hashes[3], f.Hashes[^1] }));
        Task action = Invoke(main, "BisectAsync"); await Until(() => main.OwnedWindows.OfType<BisectDialog>().Any());
        BisectDialog dialog = main.OwnedWindows.OfType<BisectDialog>().Single(); await Until(() => !dialog.IsBusy && Field<Button>(dialog, "Start").IsEnabled);
        Assert.That(dialog.SelectedRevisionHashes, Is.EqualTo(chosen)); Pointer(Field<Button>(dialog, "Start")); Window range = await Owned(dialog, "BisectRangeConfirmation");
        Pointer(Field<Button>(range, "ConfirmBisectRange")); await Closed(dialog); await action.WaitAsync(TimeSpan.FromSeconds(20)); await Idle(main);
        GitBisectState state = await f.Repository.GetBisectStateAsync(); Assert.That(state.GoodRevisions, Is.EqualTo(new[] { chosen[0] })); Assert.That(state.BadRevision, Is.EqualTo(chosen[^1]));
    }

    [AvaloniaTest]
    public async Task BisectRangeNoEscapeOrCloseKeepsTheAlreadyStartedSessionWithoutClassifyingACommit()
    {
        await using Fixture f = await Fixture.Create();
        foreach (string choice in new[] { "No", "Escape", "Close" })
        {
            BisectDialog dialog = await f.Show(new BisectDialog(f.Repository, [f.Hashes[0], f.Hashes[^1]])); Pointer(Field<Button>(dialog, "Start")); Window range = await Owned(dialog, "BisectRangeConfirmation");
            await Dismiss(range, dialog, choice, "CancelBisectRange"); await Until(() => Field<Button>(dialog, "Stop").IsEnabled);
            Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.HasRepositoryChanges, Is.True);
            GitBisectState state = await f.Repository.GetBisectStateAsync(); Assert.That(state.IsActive, Is.True); Assert.That(state.GoodRevisions, Is.Empty); Assert.That(state.BadRevision, Is.Null);
            Pointer(Field<Button>(dialog, "Stop")); await Closed(dialog);
        }
    }

    [AvaloniaTest]
    public async Task BisectFailuresShowNativeOutputAndKeepRealPartialEffectsWithoutRunningTheNextMark()
    {
        foreach (bool goodFails in new[] { true, false })
        {
            await using Fixture f = await Fixture.Create(); string[] bounds = goodFails ? ["missing-good", f.Hashes[^1]] : [f.Hashes[0], "missing-bad"];
            BisectDialog dialog = await f.Show(new BisectDialog(f.Repository, bounds)); Pointer(Field<Button>(dialog, "Start")); Window range = await Owned(dialog, "BisectRangeConfirmation");
            Pointer(Field<Button>(range, "ConfirmBisectRange")); GitProcessDialog error = (GitProcessDialog)await Owned(dialog, "BisectNativeError");
            Assert.That(((GitProcessDialog)error).Error, Is.TypeOf<GitCommandException>()); GitCommandException failure = (GitCommandException)((GitProcessDialog)error).Error!;
            Assert.That(Field<TextBox>(error, "ProcessOutput").Text, Is.EqualTo(error.CommandDescription + "\n" + failure.Result.StandardOutput + failure.Result.StandardError + "Done"));
            Assert.That(failure.Result.StandardError, Is.Not.Empty); Assert.That(failure.Arguments, Does.Contain((goodFails ? "missing-good" : "missing-bad") + "^{commit}"));
            GitBisectState state = await f.Repository.GetBisectStateAsync(); Assert.That(state.IsActive, Is.True); Assert.That(state.BadRevision, Is.Null);
            Assert.That(state.GoodRevisions, Is.EqualTo(goodFails ? Array.Empty<string>() : new[] { f.Hashes[0] }));
            Assert.That(dialog.HasRepositoryChanges, Is.True); Capture(error, "bisect-native-error-" + (goodFails ? "good" : "bad") + ".png");
            Pointer(Field<Button>(error, "Ok")); await Closed(dialog); Assert.That(dialog.LastException, Is.SameAs(failure));
            Assert.That((await f.Repository.GetBisectStateAsync()).BadRevision, Is.Null); Assert.That(await f.Head(), Is.EqualTo(f.Hashes[^1]));
        }
    }

    [AvaloniaTest]
    public async Task ClosingBisectWithTheRangeQuestionOpenStopsBeforeEitherClassification()
    {
        await using Fixture f = await Fixture.Create(); BisectDialog dialog = await f.Show(new BisectDialog(f.Repository, [f.Hashes[0], f.Hashes[^1]]));
        Pointer(Field<Button>(dialog, "Start")); Window question = await Owned(dialog, "BisectRangeConfirmation"); dialog.Close(); await Closed(dialog);
        await Until(() => !question.IsVisible); GitBisectState state = await f.Repository.GetBisectStateAsync(); Assert.That(state.IsActive, Is.True);
        Assert.That(state.GoodRevisions, Is.Empty); Assert.That(state.BadRevision, Is.Null); Assert.That(await f.Head(), Is.EqualTo(f.Hashes[^1]));
    }

    [AvaloniaTest]
    public async Task ARealBadCommandFailureKeepsExactlyTheEffectsOfNativeGitAndClosesAfterTheErrorIsAcknowledged()
    {
        await using Fixture f = await Fixture.Create(); string oracle = await f.CopyRepository("native-bad-failure-oracle");
        // Both commits exist. This intentionally reversed range is refused by Git, after any ref writes Git actually performs.
        BisectDialog dialog = await f.Show(new BisectDialog(f.Repository, [f.Hashes[^1], f.Hashes[0]]));
        Pointer(Field<Button>(dialog, "Start")); Window question = await Owned(dialog, "BisectRangeConfirmation");
        Pointer(Field<Button>(question, "ConfirmBisectRange")); GitProcessDialog error = (GitProcessDialog)await Owned(dialog, "BisectNativeError");
        Assert.That(((GitProcessDialog)error).Error, Is.TypeOf<GitCommandException>()); GitCommandException failure = (GitCommandException)((GitProcessDialog)error).Error!;
        Assert.That(failure.Arguments.Take(2), Is.EqualTo(new[] { "bisect", "bad" }));
        Assert.That(Field<TextBox>(error, "ProcessOutput").Text, Is.EqualTo(error.CommandDescription + "\n" + failure.Result.StandardOutput + failure.Result.StandardError + "Done"));
        await f.GitAt(oracle, "bisect", "start"); await f.GitAt(oracle, "bisect", "good", f.Hashes[^1]);
        GitResult expectedFailure = await new GitRunner().RunAsync(oracle, ["bisect", "bad", f.Hashes[0]]);
        Assert.That(expectedFailure.Success, Is.False); Assert.That(failure.Result.ExitCode, Is.EqualTo(expectedFailure.ExitCode));
        GitBisectState expected = await new GitRepository(oracle).GetBisectStateAsync(); GitBisectState actual = await f.Repository.GetBisectStateAsync();
        Assert.That(actual.Log, Is.EqualTo(expected.Log)); Assert.That(actual.GoodRevisions, Is.EqualTo(expected.GoodRevisions));
        Assert.That(actual.BadRevision, Is.EqualTo(expected.BadRevision)); Assert.That(actual.CurrentRevision, Is.EqualTo(expected.CurrentRevision));
        Assert.That((await f.GitAt(oracle, "show-ref")).StandardOutput, Is.EqualTo((await f.Git("show-ref")).StandardOutput));
        Assert.That((await f.GitAt(oracle, "ls-files", "--stage", "-z")).StandardOutput, Is.EqualTo((await f.Git("ls-files", "--stage", "-z")).StandardOutput));
        Assert.That(File.ReadAllBytes(Path.Combine(oracle, "tracked.txt")), Is.EqualTo(File.ReadAllBytes(f.File("tracked.txt"))));
        Assert.That(dialog.HasRepositoryChanges, Is.True); Pointer(Field<Button>(error, "Ok")); await Closed(dialog); Assert.That(dialog.LastException, Is.SameAs(failure));
        Assert.That((await f.Repository.GetBisectStateAsync()).Log, Is.EqualTo(expected.Log));
    }

    [AvaloniaTest]
    public async Task ClosingWhileGoodRunsPreservesItsRealEffectStopsBeforeBadAndTerminatesTheChild()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("The private executable/process fixture exercises Linux process cancellation."); return; }
        await using Fixture f = await Fixture.Create(); string git = (await f.Git("--exec-path")).StandardOutput.Trim();
        // /usr/bin/env resolves Git from the same PATH as the ordinary runner; only this private wrapper adds a gate after a real Good.
        string wrapper = Path.Combine(f.Root, "git-good-gate"), marker = Path.Combine(f.Root, "good-child-pids");
        static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        File.WriteAllText(wrapper, "#!/bin/sh\ncase \" $* \" in *' bisect good '*)\n /usr/bin/env git \"$@\" || exit $?\n sleep 60 &\n child=$!\n printf '%s %s\\n' \"$$\" \"$child\" > " + Quote(marker) + "\n wait \"$child\";;\n *) exec /usr/bin/env git \"$@\";;\nesac\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.That(git, Is.Not.Empty); GitRepository gated = new(f.Repository.Path, new GitRunner(wrapper));
        BisectDialog dialog = await f.Show(new BisectDialog(gated, [f.Hashes[0], f.Hashes[^1]])); Pointer(Field<Button>(dialog, "Start")); Window question = await Owned(dialog, "BisectRangeConfirmation");
        Pointer(Field<Button>(question, "ConfirmBisectRange")); await Until(() => File.Exists(marker) && File.ReadAllText(marker).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 2 && dialog.IsBusy);
        int[] pids = File.ReadAllText(marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray(); dialog.Close();
        await Closed(dialog); await Until(() => pids.All(pid => !Running(pid))); GitBisectState state = await f.Repository.GetBisectStateAsync();
        Assert.That(state.GoodRevisions, Is.EqualTo(new[] { f.Hashes[0] })); Assert.That(state.BadRevision, Is.Null);
        Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(await f.Head(), Is.EqualTo(f.Hashes[^1]));
    }

    [AvaloniaTest]
    public async Task BisectGoodBadAndSkipClassifyCurrentCheckoutAndCloseIncludingTerminalSessions()
    {
        foreach (string name in new[] { "Good", "Bad", "btnSkip" })
        {
            await using Fixture f = await Fixture.Create(); await f.Repository.StartBisectAsync(f.Hashes[^1], [f.Hashes[0]]);
            GitBisectState before = await f.Repository.GetBisectStateAsync(); string current = before.CurrentRevision!;
            BisectDialog dialog = await f.Show(new BisectDialog(f.Repository, [f.Hashes[^1], f.Hashes[0]])); Pointer(Field<Button>(dialog, name)); await Closed(dialog);
            GitBisectState after = await f.Repository.GetBisectStateAsync(); Assert.That(dialog.HasRepositoryChanges, Is.True);
            if (name == "Good") { Assert.That(after.GoodRevisions, Does.Contain(current)); }
            else if (name == "Bad") { Assert.That(after.BadRevision, Is.EqualTo(current)); }
            else { Assert.That(after.SkippedRevisions, Does.Contain(current)); }
        }
        await using Fixture terminal = await Fixture.Create(); await terminal.Repository.StartBisectAsync(terminal.Hashes[1], [terminal.Hashes[0]]);
        GitBisectState found = await terminal.Repository.GetBisectStateAsync(); Assert.That(found.FirstBadRevision, Is.EqualTo(terminal.Hashes[1]));
        BisectDialog foundDialog = await terminal.Show(new BisectDialog(terminal.Repository));
        Assert.That(new[] { "Good", "Bad", "btnSkip", "Stop" }.All(name => Field<Button>(foundDialog, name).IsEnabled), Is.True);
        Pointer(Field<Button>(foundDialog, "Stop")); await Closed(foundDialog); Assert.That(await terminal.Head(), Is.EqualTo(terminal.Hashes[^1]));
        await terminal.Repository.StartBisectAsync(terminal.Hashes[2], [terminal.Hashes[0]]);
        await terminal.Repository.MarkBisectAsync(GitCommands.Git.GitBisectOption.Skip);
        Assert.That((await terminal.Repository.GetBisectStateAsync()).IsInconclusive, Is.True);
        BisectDialog inconclusive = await terminal.Show(new BisectDialog(terminal.Repository));
        Assert.That(new[] { "Good", "Bad", "btnSkip", "Stop" }.All(name => Field<Button>(inconclusive, name).IsEnabled), Is.True);
        Pointer(Field<Button>(inconclusive, "Stop")); await Closed(inconclusive); Assert.That(await terminal.Head(), Is.EqualTo(terminal.Hashes[^1]));
    }

    [AvaloniaTest]
    public async Task TheActualTerminalOrInconclusiveGitOutputIsReadableBeforeHistoricalCloseInBothThemes()
    {
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        foreach (bool skip in new[] { false, true })
        {
            await using Fixture f = await Fixture.Create(); BisectDialog dialog;
            if (skip)
            {
                await f.Repository.StartBisectAsync(f.Hashes[2], [f.Hashes[0]]);
                dialog = await f.Show(new BisectDialog(f.Repository) { RequestedThemeVariant = theme }); Pointer(Field<Button>(dialog, "btnSkip"));
            }
            else
            {
                dialog = await f.Show(new BisectDialog(f.Repository, [f.Hashes[0], f.Hashes[1]]) { RequestedThemeVariant = theme });
                Pointer(Field<Button>(dialog, "Start")); Window question = await Owned(dialog, "BisectRangeConfirmation"); Pointer(Field<Button>(question, "ConfirmBisectRange"));
            }
            GitProcessDialog output = (GitProcessDialog)await Owned(dialog, "BisectNativeOutput"); GitResult result = output.Result!;
            Assert.That(Field<TextBox>(output, "ProcessOutput").Text, Is.EqualTo(output.CommandDescription + "\n" + result.StandardOutput + result.StandardError + "Done"));
            Assert.That(result.StandardOutput + result.StandardError, Is.Not.Empty); Assert.That(output.ActualThemeVariant, Is.EqualTo(theme));
            Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.HasRepositoryChanges, Is.True);
            GitBisectState state = await f.Repository.GetBisectStateAsync();
            if (skip) { Assert.That(state.IsInconclusive, Is.True); Assert.That(result.ExitCode, Is.EqualTo(2)); }
            else { Assert.That(state.FirstBadRevision, Is.EqualTo(f.Hashes[1])); Assert.That(result.StandardOutput + result.StandardError, Does.Contain(f.Hashes[1])); }
            Capture(output, "bisect-native-" + (skip ? "inconclusive" : "culprit") + "-" + theme + ".png"); string material = f.Materials();
            if (skip) { Field<Button>(output, "Ok").Focus(); Press(output, PhysicalKey.Escape, releaseOn: dialog); }
            else { Pointer(Field<Button>(output, "Ok")); }
            await Closed(dialog); Assert.That(dialog.LastResult, Is.SameAs(result)); if (!skip) { Assert.That(dialog.CommandResults.Count, Is.EqualTo(3)); }
            Assert.That(f.Materials(), Is.EqualTo(material), "Acknowledging the result cannot undo or fabricate Git's terminal state.");
        }
    }

    private static MenuItem Commands(MainWindow main) => main.GetVisualDescendants().OfType<Menu>().First().Items.OfType<MenuItem>()
        .Single(item => item.Items.OfType<MenuItem>().Any(child => child.Name == "BrowseCommit"));
    private static MenuItem Menu(MenuItem parent, string name) => parent.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static T Field<T>(TopLevel window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static Task Invoke(MainWindow main, string name) => (Task)typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null)!;
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Pointer(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; Pump(top); Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        Visual? hit = top.InputHitTest(point) as Visual;
        Assert.That(control.IsEffectivelyEnabled && control.IsEffectivelyVisible && new Rect(default, top.ClientSize).Contains(point) && hit?.GetSelfAndVisualAncestors().Contains(control) == true,
            Is.True, control.Name + " center=" + point + " hit=" + string.Join(" > ", hit?.GetSelfAndVisualAncestors().Select(visual => visual.GetType().Name + "#" + (visual as Control)?.Name) ?? []));
        top.MouseMove(point); top.MouseDown(point, MouseButton.Left); top.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static void Press(TopLevel window, PhysicalKey key, TopLevel? releaseOn = null)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        TopLevel recipient = releaseOn ?? window;
        if (recipient is not Window native || native.IsVisible) { recipient.KeyReleaseQwerty(key, RawInputModifiers.None); }
        Dispatcher.UIThread.RunJobs();
    }
    private static async Task Dismiss(Window question, Window owner, string choice, string noName)
    {
        if (choice == "No") { Pointer(Field<Button>(question, noName)); }
        else if (choice == "Escape") { Field<Button>(question, noName).Focus(); Press(question, PhysicalKey.Escape, releaseOn: owner); }
        else { question.Close(); }
        await Until(() => !question.IsVisible);
    }
    private static async Task<Window> Owned(Window owner, string name)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            Dispatcher.UIThread.RunJobs();
            Window? target = owner.OwnedWindows.FirstOrDefault(window => window.Name == name && window.IsVisible);
            if (target is not null) { Pump(target); return target; }
            GitProcessDialog? phase = owner.OwnedWindows.OfType<GitProcessDialog>().FirstOrDefault(window => window.IsVisible && !window.IsRunning);
            if (phase is not null)
            {
                bool error = phase.Error is not null || phase.Result?.Success == false;
                bool resultPhase = phase.CommandDescription.StartsWith("git bisect bad", StringComparison.Ordinal) || phase.CommandDescription == "git bisect skip";
                if ((name == "BisectNativeError" && error) || (name == "BisectNativeOutput" && resultPhase)) { Pump(phase); return phase; }
                Assert.That(phase.Outcome?.Success, Is.True, phase.Error?.ToString()); Pointer(Field<Button>(phase, "Ok"));
            }
            await Task.Delay(10);
        }
        throw new AssertionException("Expected owned " + name + " was not opened.");
    }
    private static async Task Idle(MainWindow main) { await Task.Delay(20); await Until(() => !main.IsBusy); }
    private static async Task Closed(RepositoryDialog dialog)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while ((dialog.IsVisible || dialog.IsBusy) && timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            Dispatcher.UIThread.RunJobs();
            GitProcessDialog? phase = dialog.OwnedWindows.OfType<GitProcessDialog>().FirstOrDefault(window => window.IsVisible && !window.IsRunning);
            if (phase is not null) { Pointer(Field<Button>(phase, "Ok")); }
            await Task.Delay(10);
        }
        Assert.That(!dialog.IsVisible && !dialog.IsBusy, Is.True, "Bisect did not close after its actual process phases.");
    }
    private static async Task AcknowledgePhase(Window owner)
    { await Until(() => owner.OwnedWindows.OfType<GitProcessDialog>().Any(window => window.IsVisible && !window.IsRunning)); GitProcessDialog phase = owner.OwnedWindows.OfType<GitProcessDialog>().Single(); Assert.That(phase.Outcome?.Success, Is.True, phase.Error?.ToString()); Pointer(Field<Button>(phase, "Ok")); await Until(() => !phase.IsVisible); }
    private static async Task Until(Func<bool> condition)
    { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True, "Undo/Bisect UI did not settle within 15 seconds."); }
    private static void Capture(TopLevel window, string name)
    { Pump(window); using Bitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }
    private static Color Pixel(TopLevel window, Point point)
    {
        Pump(window); using Bitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        using WriteableBitmap copy = new(frame!.PixelSize, frame.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using ILockedFramebuffer buffer = copy.Lock(); frame.CopyPixels(buffer, AlphaFormat.Unpremul);
        byte[] bytes = new byte[4]; Marshal.Copy(buffer.Address + (int)point.Y * buffer.RowBytes + (int)point.X * 4, bytes, 0, 4);
        return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }
    private static bool Running(int pid)
    {
        string stat = "/proc/" + pid + "/stat"; if (!File.Exists(stat)) { return false; }
        try { string value = File.ReadAllText(stat); int index = value.LastIndexOf(')'); return index < 0 || index + 2 >= value.Length || value[index + 2] is not ('Z' or 'X' or 'x'); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Dictionary<string, string?> _environment = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-undo-bisect-" + Guid.NewGuid().ToString("N"));
        public GitRepository Repository { get; private set; } = null!;
        public List<string> Hashes { get; } = []; public List<Window> Windows { get; } = [];
        private Fixture()
        {
            Directory.CreateDirectory(Root);
            foreach ((string name, string value) in new[] { ("XDG_CONFIG_HOME", Path.Combine(Root, "profile")), ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_OPTIONAL_LOCKS", "0") })
            { _environment[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
            new PreferencesStore().Save(new() { Theme = "Light" });
        }
        public string File(string relative) => Path.Combine(Repository.Path, relative);
        public void Write(string relative, string text) { string path = File(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, text); }
        public static async Task<Fixture> Create(int commits = 6, bool bare = false)
        {
            Fixture f = new();
            try
            {
                f.Repository = await GitRepository.InitAsync(Path.Combine(f.Root, "repo"), bare: bare);
                await f.Git("config", "user.name", "Undo Bisect Test"); await f.Git("config", "user.email", "undo-bisect@example.invalid");
                await f.Git("config", "commit.gpgsign", "false"); await f.Git("config", "tag.gpgsign", "false"); await f.Git("config", "core.hooksPath", "/dev/null");
                if (!bare)
                {
                    for (int index = 0; index < commits; index++)
                    {
                        f.Write("tracked.txt", "version " + index + "\n"); f.Write(".gitignore", "*.ignored\n");
                        await f.Git("add", "--", "tracked.txt", ".gitignore"); await f.Git("commit", "-m", index == 0 ? "Initial" : "Commit " + index); f.Hashes.Add(await f.Head());
                    }
                }
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async Task<GitResult> GitAt(string path, params string[] arguments)
        { GitResult result = await new GitRunner().RunAsync(path, arguments); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public Task<GitResult> Git(params string[] arguments) => GitAt(Repository.Path, arguments);
        public async Task<string> Text(params string[] arguments) => (await Git(arguments)).StandardOutput.TrimEnd('\r', '\n');
        public Task<string> Head() => Text("rev-parse", "HEAD");
        public async Task<MainWindow> Main(ThemeVariant? theme = null)
        { MainWindow main = new() { Width = 1280, Height = 752, RequestedThemeVariant = theme ?? ThemeVariant.Light }; Windows.Add(main); main.Show(); await main.OpenRepositoryAsync(Repository.Path); await Idle(main); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); Pump(main); return main; }
        public async Task<BisectDialog> Show(BisectDialog dialog)
        { Windows.Add(dialog); dialog.Show(); await dialog.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Pump(dialog); return dialog; }
        public string Materials(bool worktreeOnly = false)
        {
            SortedDictionary<string, string> entries = new(StringComparer.Ordinal);
            void Walk(string directory)
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (worktreeOnly && path == File(".git")) { continue; }
                    FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                    string relative = Path.GetRelativePath(Repository.Path, path); if (info.LinkTarget is string link) { entries[relative] = "link:" + link; continue; }
                    string mode = OperatingSystem.IsWindows() ? "" : ((int)System.IO.File.GetUnixFileMode(path)).ToString(CultureInfo.InvariantCulture);
                    if (info is DirectoryInfo) { entries[relative] = "dir:" + mode; Walk(path); }
                    else { entries[relative] = mode + ":" + Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path))); }
                }
            }
            Walk(Repository.Path); return JsonSerializer.Serialize(entries);
        }
        public Task<string> CopyRepository(string name)
        {
            string destination = Path.Combine(Root, name);
            void Copy(string source, string target)
            { Directory.CreateDirectory(target); foreach (string path in Directory.EnumerateFileSystemEntries(source)) { string output = Path.Combine(target, Path.GetFileName(path)); if (Directory.Exists(path)) { Copy(path, output); } else { System.IO.File.Copy(path, output); } } }
            Copy(Repository.Path, destination); return Task.FromResult(destination);
        }
        public async ValueTask DisposeAsync()
        {
            static void CloseOwned(Window owner) { foreach (Window child in owner.OwnedWindows.ToArray()) { CloseOwned(child); child.Close(); } }
            foreach (Window window in Windows.AsEnumerable().Reverse()) { CloseOwned(window); window.Close(); }
            if (Windows.Count != 0) { await Until(() => Windows.All(window => !window.IsVisible && (window is not RepositoryDialog dialog || !dialog.IsBusy) && (window is not MainWindow main || !main.IsBusy))); }
            Dispatcher.UIThread.RunJobs(); foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); }
            if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
        }
    }
}

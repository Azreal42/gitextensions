using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class CommitMessageWorkflowTests
{
    [AvaloniaTest]
    public async Task ClosingAndReopeningRestoresRawDraftAndAmendThenSuccessClearsBothFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitRepository repository = fixture.Repository;
        CommitDialog dialog = await fixture.OpenAsync();
        const string draft = "Draft λ🧪\nbody without a blank second line\n#issue number\n";
        Message(dialog).Text = draft;
        Field<CheckBox>(dialog, "Amend").IsChecked = true;
        dialog.Close();
        Assert.That(await File.ReadAllTextAsync(await GitPath(repository, "COMMITMESSAGE")), Is.EqualTo(draft));
        Assert.That(await File.ReadAllTextAsync(await GitPath(repository, "GitExtensions.amend")), Is.EqualTo("True"));
        CommitDialog reopened = await fixture.OpenAsync();
        Assert.That(Message(reopened).Text, Is.EqualTo(draft));
        Assert.That(Field<CheckBox>(reopened, "Amend").IsChecked, Is.True);
        string before = await repository.ResolveRevisionAsync("HEAD");
        Click(reopened, "Commit");
        Window confirmation = await Confirmation(reopened);
        Assert.That(confirmation.Title, Is.EqualTo("Amend commit"));
        AcceptConfirmation(confirmation);
        await Idle(reopened);
        Assert.That(await repository.ResolveRevisionAsync("HEAD"), Is.Not.EqualTo(before));
        string committed = (await repository.RunAsync(["log", "-1", "--pretty=%B"])).StandardOutput;
        Assert.That(committed, Does.StartWith("Draft λ🧪\n\nbody without a blank second line\n#issue number"));
        Assert.That(File.Exists(await GitPath(repository, "COMMITMESSAGE")), Is.False);
        Assert.That(File.Exists(await GitPath(repository, "GitExtensions.amend")), Is.False);
        reopened.Close();
        Assert.That(File.Exists(await GitPath(repository, "COMMITMESSAGE")), Is.False, "Closing a successful commit must not resurrect its cleared draft.");
        Assert.That(fixture.Store.Load().LastCommitMessage, Is.EqualTo(draft));
    }

    [AvaloniaTest]
    public async Task LinkedWorktreesKeepIndependentDraftsAndMergeMessageWinsWithoutFixupOverwritingIt()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        CommitDialog main = await fixture.OpenAsync(); Message(main).Text = "main draft\n\nMain body"; main.Close();
        string worktreePath = Path.Combine(fixture.Temporary, "linked [worktree]");
        await fixture.Git("worktree", "add", "-b", "linked", "--", worktreePath);
        GitRepository linked = await GitRepository.OpenAsync(worktreePath);
        CommitDialog worktree = await fixture.OpenAsync(linked); Message(worktree).Text = "linked draft"; worktree.Close();
        string mainPath = await GitPath(fixture.Repository, "COMMITMESSAGE"), linkedPath = await GitPath(linked, "COMMITMESSAGE");
        Assert.That(linkedPath, Is.Not.EqualTo(mainPath));
        Assert.That(await File.ReadAllTextAsync(mainPath), Is.EqualTo("main draft\n\nMain body"));
        Assert.That(await File.ReadAllTextAsync(linkedPath), Is.EqualTo("linked draft"));
        string mergePath = await GitPath(linked, "MERGE_MSG");
        await File.WriteAllTextAsync(mergePath, "Merge draft from Git\n\nMerge body");
        worktree = await fixture.OpenAsync(linked);
        Assert.That(Message(worktree).Text, Is.EqualTo("Merge draft from Git\n\nMerge body"));
        Message(worktree).Text += "\nEdited";
        worktree.Close();
        Assert.That(await File.ReadAllTextAsync(mergePath), Does.EndWith("\nEdited"));
        Assert.That(await File.ReadAllTextAsync(linkedPath), Is.EqualTo("linked draft"));
        worktree = await fixture.OpenAsync(linked);
        Input<ComboBox>(worktree, "CommitKind").SelectedIndex = 1;
        Input<ComboBox>(worktree, "TargetCommit").SelectedIndex = 0;
        worktree.Close();
        Assert.That(await File.ReadAllTextAsync(mergePath), Does.EndWith("\nEdited"), "Closing fixup must preserve the ordinary draft.");
        await File.WriteAllTextAsync(await GitPath(linked, "GitExtensions.amend"), "True");
        fixture.Store.Save(fixture.Store.Load() with { RememberAmendCommitState = false });
        worktree = await fixture.OpenAsync(linked);
        Assert.That(Field<CheckBox>(worktree, "Amend").IsChecked, Is.False);
        worktree.Close();
        Assert.That(File.Exists(await GitPath(linked, "GitExtensions.amend")), Is.False);
    }

    [AvaloniaTest]
    public async Task MessageHistoryUsesFullBodiesLatestFailedMessageCountAndLiteralAuthorIdentity()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        const string identity = "My.Name [literal]+ (tester)";
        await fixture.Git("config", "user.name", identity);
        await fixture.Git("config", "user.email", "my+name@example.invalid");
        await fixture.Git("commit", "--allow-empty", "-m", "Own message\n\nEntire multiline body λ\nlast line");
        await fixture.Git("commit", "--allow-empty", "-m", "Other author message", "--author=Other Author <other@example.invalid>");
        await fixture.Git("commit", "--allow-empty", "-m", "Same name other mail", "--author=" + identity + " <other@example.invalid>");
        fixture.Store.Save(fixture.Store.Load() with { NumberOfPreviousMessages = 2 });
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "changed\n");
        CommitDialog dialog = await fixture.OpenAsync();
        Click(dialog, "StageAll"); await Idle(dialog);
        string candidate = new string('L', 85) + "\n\nFailed candidate body λ";
        Message(dialog).Text = candidate;
        fixture.WriteHook("pre-commit", "printf 'rejected candidate\\n' >&2\nexit 1\n");
        Click(dialog, "Commit"); await Idle(dialog, error: true);
        Assert.That(fixture.Store.Load().LastCommitMessage, Is.EqualTo(candidate));
        dialog.Close();

        // The last attempted message is shared even in another repository, rather than stored in its .git directory.
        string secondPath = Path.Combine(fixture.Temporary, "second repository");
        GitRepository second = await fixture.CreateRepositoryAsync(secondPath);
        CommitDialog other = await fixture.OpenAsync(second);
        MenuItem history = await OpenHistory(other);
        MenuItem[] messages = HistoryMessages(history);
        Assert.That(messages.Length, Is.EqualTo(2));
        Assert.That(messages[0].Tag, Is.EqualTo(candidate));
        Assert.That(messages[0].Header, Is.EqualTo(new string('L', 69) + "..."));
        Message(other).Text = "Original text to undo";
        Invoke(messages[0]);
        Assert.That(Message(other).Text, Is.EqualTo(candidate));
        history.Close();
        Message(other).Focus();
        other.KeyPressQwerty(PhysicalKey.Z, Avalonia.Input.RawInputModifiers.Control);
        other.KeyReleaseQwerty(PhysicalKey.Z, Avalonia.Input.RawInputModifiers.Control);
        Assert.That(Message(other).Text, Is.EqualTo("Original text to undo"), "History replacement must preserve the editor's undo.");
        other.Close();

        dialog = await fixture.OpenAsync();
        history = await OpenHistory(dialog);
        MenuItem mine = HistoryItem(history, "ShowOnlyMyMessagesToolStripMenuItem");
        mine.IsChecked = true; Invoke(mine);
        await Until(() => !history.Items.OfType<MenuItem>().Any(item => item.Name == "HistoryLoading"));
        messages = HistoryMessages(history);
        Assert.That(messages.Select(item => (string)item.Tag!), Is.EqualTo(new[] { candidate, "Own message\n\nEntire multiline body λ\nlast line" }));
        Assert.That(messages.Select(item => item.Header), Does.Not.Contain("Other author message").And.Not.Contain("Same name other mail"));
        Assert.That(fixture.Store.Load().ShowOnlyMyMessages, Is.True);
        history.Close();
        Assert.That(await fixture.Repository.GetPreviousCommitMessagesAsync(2, onlyMyMessages: true), Is.EqualTo(new[] { "Own message\n\nEntire multiline body λ\nlast line" }));
    }

    [AvaloniaTest]
    public async Task PersistentOriginalOptionsAndTranslatedMenusKeepCompactLightAndDarkLayout()
    {
        using Fixture fixture = await Fixture.CreateAsync("fr-FR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        CommitDialog dialog = await fixture.OpenAsync(keepOpen: false);
        Assert.That(fixture.Store.Load(), Is.EqualTo(new CommitPreferences()));
        MenuItem options = Options(dialog);
        Assert.That(Option(dialog, "closeDialogAfterEachCommitToolStripMenuItem").IsChecked, Is.True);
        Assert.That(Option(dialog, "refreshDialogOnFormFocusToolStripMenuItem").IsChecked, Is.False);
        foreach (string name in new[] { "closeDialogAfterEachCommitToolStripMenuItem", "closeDialogAfterAllFilesCommittedToolStripMenuItem", "tsmiSelectStagedOnEnterMessage", "refreshDialogOnFormFocusToolStripMenuItem" })
        {
            MenuItem item = Option(dialog, name); item.IsChecked = !item.IsChecked; Invoke(item);
        }
        Option(dialog, "noVerifyToolStripMenuItem").IsChecked = true;
        Option(dialog, "signOffToolStripMenuItem").IsChecked = true;
        Input<TextBox>(dialog, "AuthorOverride").Text = "Session Author <session@example.invalid>";
        ComboBox signing = Input<ComboBox>(dialog, "Signing");
        Assert.That(signing.ItemCount, Is.EqualTo(4));
        signing.SelectedIndex = 3; Assert.That(Input<TextBox>(dialog, "SigningKey").IsVisible, Is.True);
        signing.SelectedIndex = 2; Assert.That(Input<TextBox>(dialog, "SigningKey").IsVisible, Is.False);
        dialog.Close();
        dialog = await fixture.OpenAsync(keepOpen: false);
        Assert.Multiple(() =>
        {
            Assert.That(Option(dialog, "closeDialogAfterEachCommitToolStripMenuItem").IsChecked, Is.False);
            Assert.That(Option(dialog, "closeDialogAfterAllFilesCommittedToolStripMenuItem").IsChecked, Is.False);
            Assert.That(Option(dialog, "tsmiSelectStagedOnEnterMessage").IsChecked, Is.False);
            Assert.That(Option(dialog, "refreshDialogOnFormFocusToolStripMenuItem").IsChecked, Is.True);
            Assert.That(Option(dialog, "noVerifyToolStripMenuItem").IsChecked, Is.False);
            Assert.That(Option(dialog, "signOffToolStripMenuItem").IsChecked, Is.False);
            Assert.That(Input<TextBox>(dialog, "AuthorOverride").Text, Is.Null.Or.Empty);
            Assert.That(Input<ComboBox>(dialog, "Signing").SelectedIndex, Is.Zero);
        });
        Message(dialog).Text = "Brouillon conservé\n\nCorps du message";
        Menu toolbar = Field<Menu>(dialog, "CommitToolbar");
        options = Options(dialog);
        dialog.UpdateLayout();
        var top = toolbar.Items.OfType<MenuItem>().ToArray();
        CommitToolbarTestDriver.AssertLayout(dialog);
        CompactMenuToolbar overflow = CommitToolbarTestDriver.Controller(dialog);
        Assert.That(Bounds(dialog, options).Right, Is.EqualTo(Bounds(dialog, toolbar).Right).Within(1));
        if (overflow.IsOverflowed(top[2]))
        {
            Assert.That(top[2].IsVisible, Is.False);
            Assert.That(Bounds(dialog, top[0]).Right, Is.LessThan(Bounds(dialog, overflow.Overflow).X + 1));
            Assert.That(Bounds(dialog, overflow.Overflow).Right, Is.EqualTo(Bounds(dialog, options).X).Within(.5));
        }
        else
        {
            Assert.That(Bounds(dialog, top[0]).Right, Is.LessThan(Bounds(dialog, top[2]).X + 1));
            Assert.That(Bounds(dialog, top[2]).Right, Is.LessThan(Bounds(dialog, options).X));
        }
        Assert.That(toolbar.Bounds.Height, Is.EqualTo(28));
        Assert.That(top[0].GetVisualDescendants().OfType<Image>().Count(image => image.Bounds.Size == new Size(16, 16)), Is.EqualTo(1), "The toolbar must draw its original message icon.");
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            dialog.RequestedThemeVariant = theme;
            await Idle(dialog);
            CommitToolbarTestDriver.AssertLayout(dialog);
            MenuItem templates = top[2];
            MenuItem templatePresentation = CommitToolbarTestDriver.Presentation(dialog, templates, morePopup =>
            { using var moreFrame = Capture(morePopup, "classic-commit-toolbar-more-fr-" + theme + ".png"); });
            Assert.That(templatePresentation.GetVisualDescendants().OfType<Image>().Count(image => image.Bounds.Size == new Size(16, 16)), Is.EqualTo(1), "The visible template command must draw its original template icon, inline or in More.");
            CommitToolbarTestDriver.OpenMenu(dialog, templates);
            TopLevel templatePopup = (TopLevel)templates.Items.OfType<MenuItem>().First(item => item.IsVisible && item.IsEnabled).GetVisualRoot()!;
            using (var templateFrame = Capture(templatePopup, "classic-commit-templates-popup-fr-" + theme + ".png")) { }
            toolbar.Close(); CommitToolbarTestDriver.Pump(dialog);
            CommitToolbarTestDriver.OpenMenu(dialog, options);
            TopLevel popup = (TopLevel)Option(dialog, "noVerifyToolStripMenuItem").GetVisualRoot()!;
            Assert.That(options.IsSubMenuOpen, Is.True);
            Assert.That(Option(dialog, "noVerifyToolStripMenuItem").Bounds.Height, Is.GreaterThan(0));
            using var popupFrame = Capture(popup, "classic-commit-options-popup-fr-" + theme + ".png");
            TestContext.WriteLine("Rendered options host: " + popup.GetType().FullName);
            Assert.That(Option(dialog, "noVerifyToolStripMenuItem").Header, Is.EqualTo("Pas de vérification"));
            using var frame = Capture(dialog, "classic-commit-options-fr-" + theme + ".png");
            Assert.That(frame.PixelSize.Width, Is.EqualTo(1100));
            Color background = ((ISolidColorBrush)dialog.Background!).Color;
            Assert.That(background, Is.EqualTo(Color.Parse(theme == ThemeVariant.Dark ? "#323232" : "#ffffff")));
            options.Close();
            await Idle(dialog);
            Click(dialog, "Commit");
            CommitEmptyDialog empty = await EmptyDialog(dialog);
            Assert.That(empty.Title, Is.EqualTo("Confirmer le commit"));
            empty.RequestedThemeVariant = theme;
            using var prompt = Capture(empty, "classic-commit-empty-fr-" + theme + ".png");
            Assert.That(Field<Button>(empty, "StageUnstaged").IsEnabled, Is.False);
            Click(empty, "Cancel"); await Idle(dialog);
        }
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(fixture.Store.FilePath)!, "*.tmp"), Is.Empty);
    }

    [AvaloniaTest]
    public async Task NoVerifySkipsPreCommitAndCommitMessageHooksButRunsPrepareAndPostAndResetsAfterSuccess()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "changed\n");
        CommitDialog dialog = await fixture.OpenAsync();
        Click(dialog, "StageAll"); await Idle(dialog);
        string hookLog = Path.Combine(fixture.Temporary, "hooks.log");
        fixture.WriteHook("pre-commit", "printf 'pre rejected\\n' >&2\nexit 1\n");
        fixture.WriteHook("commit-msg", "printf 'message rejected\\n' >&2\nexit 1\n");
        fixture.WriteHook("prepare-commit-msg", $"printf 'prepare\\n' >> '{hookLog}'\nprintf '\\nPrepared by hook\\n' >> \"$1\"\n");
        fixture.WriteHook("post-commit", $"printf 'post\\n' >> '{hookLog}'\n");
        Message(dialog).Text = "Verified hook semantics\nbody";
        Option(dialog, "noVerifyToolStripMenuItem").IsChecked = true;
        Click(dialog, "Commit"); await Idle(dialog);
        Assert.That(await File.ReadAllLinesAsync(hookLog), Is.EqualTo(new[] { "prepare", "post" }));
        Assert.That((await fixture.Git("log", "-1", "--pretty=%B")).StandardOutput, Does.Contain("Verified hook semantics\n\nbody").And.Contain("Prepared by hook"));
        Assert.That(Option(dialog, "noVerifyToolStripMenuItem").IsChecked, Is.False);
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "second change\n");
        await dialog.RefreshAsync(); Click(dialog, "StageAll"); await Idle(dialog);
        Message(dialog).Text = "Must be verified again";
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        Click(dialog, "Commit"); await Idle(dialog, error: true);
        Assert.That(dialog.LastError, Does.Contain("pre rejected"));
        Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That(Message(dialog).Text, Is.EqualTo("Must be verified again"));
        Assert.That((await fixture.Repository.GetStatusAsync()).Changes.Single().IsStaged, Is.True);
        Option(dialog, "noVerifyToolStripMenuItem").IsChecked = true;
        fixture.WriteHook("prepare-commit-msg", "printf 'prepare rejected\\n' >&2\nexit 1\n");
        Click(dialog, "Commit"); await Idle(dialog, error: true);
        Assert.That(dialog.LastError, Does.Contain("prepare rejected"));
        Assert.That(Option(dialog, "noVerifyToolStripMenuItem").IsChecked, Is.True, "Failure keeps the user's session choice.");
    }

    [AvaloniaTest]
    public async Task EmptyConfirmationCancelAndExplicitEmptyPreserveAllUnstagedContent()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "unstaged\n");
        CommitDialog dialog = await fixture.OpenAsync(); Message(dialog).Text = "Empty by explicit choice";
        string before = await fixture.Repository.ResolveRevisionAsync("HEAD");
        Assert.That(Field<Button>(dialog, "Commit").IsEnabled, Is.True);
        Click(dialog, "Commit"); CommitEmptyDialog prompt = await EmptyDialog(dialog);
        Assert.That(Field<Button>(prompt, "StageUnstaged").IsEnabled, Is.True);
        prompt.KeyPressQwerty(PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);
        if (prompt.IsVisible) { prompt.KeyReleaseQwerty(PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None); }
        await Until(() => !dialog.OwnedWindows.Contains(prompt));
        Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(before));
        Assert.That(Message(dialog).Text, Is.EqualTo("Empty by explicit choice"));
        Assert.That((await fixture.Git("diff", "--cached")).StandardOutput, Is.Empty);
        Click(dialog, "Commit"); prompt = await EmptyDialog(dialog); Click(prompt, "MakeEmpty"); await Idle(dialog);
        Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD^"), Is.EqualTo(before));
        Assert.That((await fixture.Git("diff", "HEAD^", "HEAD")).StandardOutput, Is.Empty);
        Assert.That((await fixture.Repository.GetStatusAsync()).Changes.Single().IsModified, Is.True);
        Assert.That((await fixture.Git("diff", "--cached")).StandardOutput, Is.Empty);
    }

    [AvaloniaTest]
    public async Task StageAndCommitUsesAllDisplayedLiteralPathsAndAStageFailureNeverCreatesAnEmptyCommit()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        const string visible = "visible [literal] λ.txt", hidden = "hidden.txt";
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, visible), "visible contents\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, hidden), "hidden contents\n");
        CommitDialog dialog = await fixture.OpenAsync();
        dialog.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control); dialog.KeyReleaseQwerty(PhysicalKey.F, RawInputModifiers.Control);
        ComboBox filter = Field<ComboBox>(dialog, "SelectionFilter"); filter.Text = "visible";
        await Until(() => filter.Items.OfType<string>().Contains("visible")); await Idle(dialog);
        Assert.That(FileStatusTestHelpers.Changes(Field<TreeView>(dialog, "Unstaged")).Count(), Is.EqualTo(2));
        Assert.That(Field<TreeView>(dialog, "Unstaged").SelectedItems!.OfType<TreeViewItem>().Select(item => item.Tag).OfType<GitChange>().Single().Path, Is.EqualTo(visible));
        Message(dialog).Text = "Only visible change";
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        string indexLock = await GitPath(fixture.Repository, "index.lock"); await File.WriteAllTextAsync(indexLock, "locked");
        try
        {
            Click(dialog, "Commit"); CommitEmptyDialog prompt = await EmptyDialog(dialog);
            Assert.That(ButtonText(Field<Button>(prompt, "StageUnstaged")), Is.EqualTo("Stage and commit all unstaged files"));
            Click(prompt, "StageUnstaged"); await Idle(dialog, error: true);
            Assert.That(dialog.LastError, Does.Contain("index.lock"));
            Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
            Assert.That(Message(dialog).Text, Is.EqualTo("Only visible change"));
            Assert.That((await fixture.Git("diff", "--cached")).StandardOutput, Is.Empty);
        }
        finally { File.Delete(indexLock); }
        Click(dialog, "Commit"); CommitEmptyDialog successful = await EmptyDialog(dialog); Click(successful, "StageUnstaged"); await Idle(dialog);
        Assert.That((await fixture.Git("diff-tree", "--no-commit-id", "--name-only", "-z", "-r", "HEAD")).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries), Is.EquivalentTo(new[] { visible, hidden }));
        Assert.That((await fixture.Repository.GetStatusAsync()).Changes, Is.Empty);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, hidden)), Is.EqualTo("hidden contents\n"));
    }

    [AvaloniaTest]
    public async Task MergeWithNoIndexChangesKeepsTheOriginalSeparateConfirmationAndTwoParents()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        CommitDialog dialog = await fixture.OpenAsync();
        await fixture.Git("checkout", "-b", "merge-source");
        await fixture.Git("commit", "--allow-empty", "-m", "Different history same tree");
        await fixture.Git("checkout", "main");
        await fixture.Git("merge", "--no-ff", "--no-commit", "merge-source");
        Message(dialog).Text = "Edited external merge message";
        string before = await fixture.Repository.ResolveRevisionAsync("HEAD");
        Click(dialog, "Commit"); Window prompt = await Confirmation(dialog);
        Assert.That(prompt, Is.Not.InstanceOf<CommitEmptyDialog>());
        Assert.That(prompt.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "There are no files staged for this commit.\nAre you sure you want to commit?"), Is.True);
        CancelConfirmation(prompt); await Until(() => !dialog.OwnedWindows.Contains(prompt));
        Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(before));
        Click(dialog, "Commit"); prompt = await Confirmation(dialog); AcceptConfirmation(prompt); await Idle(dialog);
        Assert.That((await fixture.Git("rev-list", "--parents", "-1", "HEAD")).StandardOutput.Trim().Split(' ').Length, Is.EqualTo(3));
        Assert.That((await fixture.Repository.GetLogAsync(1, all: false)).Single().Subject, Is.EqualTo("Edited external merge message"));
        Assert.That(File.Exists(await GitPath(fixture.Repository, "MERGE_MSG")), Is.False);
        Assert.That(File.Exists(await GitPath(fixture.Repository, "COMMITMESSAGE")), Is.False);
    }

    [AvaloniaTest]
    public async Task ClosingWhileAHookRunsCancelsGitThenSavesTheDraftBeforeTheWindowDisappears()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "changed\n");
        CommitDialog dialog = await fixture.OpenAsync(); Click(dialog, "StageAll"); await Idle(dialog);
        string before = await fixture.Repository.ResolveRevisionAsync("HEAD");
        string marker = Path.Combine(fixture.Temporary, "hook.started");
        fixture.WriteHook("pre-commit", $"printf '%s' \"$$\" > '{marker}'\nsleep 60\n");
        Message(dialog).Text = "Cancelled hook draft λ\nBody";
        Click(dialog, "Commit"); await Until(() => File.Exists(marker) && new FileInfo(marker).Length > 0);
        Assert.That(dialog.IsBusy, Is.True); Assert.That(Field<Button>(dialog, "Cancel").IsEnabled, Is.True);
        Click(dialog, "Cancel");
        await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(before));
        Assert.That((await fixture.Repository.GetStatusAsync()).Changes.Single().IsStaged, Is.True);
        Assert.That(await File.ReadAllTextAsync(await GitPath(fixture.Repository, "COMMITMESSAGE")), Is.EqualTo("Cancelled hook draft λ\nBody"));
        CommitDialog reopened = await fixture.OpenAsync();
        Assert.That(Message(reopened).Text, Is.EqualTo("Cancelled hook draft λ\nBody"));
        int pid = int.Parse(await File.ReadAllTextAsync(marker), CultureInfo.InvariantCulture);
        Assert.That(ProcessAlive(pid), Is.False);
    }

    [AvaloniaTest]
    public async Task ConfiguredTemplateCommentsAreRemovedButIssueNumbersAndFormattingPreferenceArePreserved()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string template = Path.Combine(fixture.Temporary, "template");
        await File.WriteAllTextAsync(template, "Template subject\n# template guidance\nTemplate body\n");
        await fixture.Git("config", "commit.template", template);
        fixture.Store.Save(fixture.Store.Load() with { EnsureCommitMessageSecondLineEmpty = false });
        CommitDialog dialog = await fixture.OpenAsync();
        Assert.That(Message(dialog).Text, Does.Contain("# template guidance"));
        Assert.That(Field<Button>(dialog, "Commit").IsEnabled, Is.False);
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "changed\n");
        await dialog.RefreshAsync(); Click(dialog, "StageAll"); await Idle(dialog);
        Message(dialog).Text += "Edited body\n";
        Click(dialog, "Commit"); await Idle(dialog);
        Assert.That((await fixture.Git("log", "-1", "--pretty=%B")).StandardOutput, Does.StartWith("Template subject\nTemplate body\n").And.Not.Contain("# template guidance"));
        dialog.Close();
        await fixture.Git("config", "--unset", "commit.template");
        dialog = await fixture.OpenAsync();
        Message(dialog).Text = "#123 Fix issue\nKeep second line";
        Click(dialog, "Commit"); CommitEmptyDialog prompt = await EmptyDialog(dialog); Click(prompt, "MakeEmpty"); await Idle(dialog);
        Assert.That((await fixture.Git("log", "-1", "--pretty=%B")).StandardOutput, Does.StartWith("#123 Fix issue\nKeep second line"));
    }

    [AvaloniaTest]
    public async Task LegacyEncodingKeepsExactTemplateDraftCommitBytesAndDisplayedHistoryWhileRejectingIncompatibleGlyphs()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Git("config", "i18n.commitEncoding", "ISO-8859-1");
        await fixture.Git("config", "i18n.logOutputEncoding", "ISO-8859-1");
        System.Text.Encoding latin = await fixture.Repository.GetCommitMessageEncodingAsync();
        string template = Path.Combine(fixture.Temporary, "legacy-template");
        const string templateText = "Café modèle\n# Commentaire\nDétail naïf\n";
        await File.WriteAllBytesAsync(template, latin.GetBytes(templateText));
        await fixture.Git("config", "commit.template", template);
        CommitDialog dialog = await fixture.OpenAsync();
        Assert.That(Message(dialog).Text, Is.EqualTo(templateText));
        Message(dialog).Text = "# Commentaire modifié\n# Seulement des instructions\n";
        Assert.That(Field<Button>(dialog, "Commit").IsEnabled, Is.False, "Edited comments cannot produce a valid message or start staging.");
        const string raw = "Café résumé\nDétail naïf\n# Retiré du template\n";
        Message(dialog).Text = raw; dialog.Close();
        string path = await GitPath(fixture.Repository, "COMMITMESSAGE");
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(latin.GetBytes(raw)));
        dialog = await fixture.OpenAsync(); Assert.That(Message(dialog).Text, Is.EqualTo(raw));
        MenuItem load = Field<Menu>(dialog, "CommitToolbar").Items.OfType<MenuItem>().SelectMany(item => item.Items.OfType<MenuItem>()).Single(item => item.Name == "LoadTemplate");
        Invoke(load); await Idle(dialog); Message(dialog).Text = raw;
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "legacy change\n");
        await dialog.RefreshAsync();
        Click(dialog, "StageAll"); await Idle(dialog);
        Click(dialog, "Commit"); await Idle(dialog);
        GitBinaryResult binary = await new GitRunner().RunBinaryAsync(fixture.Repository.Path, ["cat-file", "commit", "HEAD"]);
        Assert.That(binary.Success, Is.True, binary.StandardError);
        string data = latin.GetString(binary.StandardOutput);
        Assert.That(data, Does.Contain("\nencoding ISO-8859-1\n"));
        string committed = data[(data.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        Assert.That(committed, Does.StartWith("Café résumé\n\nDétail naïf"));
        Assert.That(binary.StandardOutput, Does.Contain((byte)0xe9));
        Assert.That(committed, Does.Not.Contain("# Retiré du template"));
        Assert.That((await fixture.Repository.GetLogAsync(1, all: false)).Single().Subject, Is.EqualTo("Café résumé"));
        Assert.That((await fixture.Repository.GetReflogAsync(1)).Single().Description, Does.EndWith("Café résumé"));
        Assert.That((await fixture.Repository.GetPreviousCommitMessagesAsync(1)).Single(), Is.EqualTo(committed.TrimEnd('\n')));
        Assert.That(await fixture.Repository.GetCommitDiffAsync("HEAD"), Does.Contain("Café résumé").And.Contain("Détail naïf"));
        Assert.That((await fixture.Repository.GetFileHistoryAsync("file.txt")).First().Commit.Subject, Is.EqualTo("Café résumé"));
        Assert.That((await fixture.Repository.GetFileBlameAsync("file.txt")).Lines.Last().Line.Summary, Is.EqualTo("Café résumé"));
        Assert.That(Input<ComboBox>(dialog, "TargetCommit").Items.OfType<GitCommit>().First().Subject, Is.EqualTo("Café résumé"));
        MenuItem history = await OpenHistory(dialog); Assert.That(HistoryMessages(history).Any(item => (item.Header as string) == "Café résumé"), Is.True); history.Close();
        dialog.Close();

        // A failed encoder cannot truncate a previous draft or stage an unrelated change.
        await fixture.Git("config", "--unset", "commit.template");
        dialog = await fixture.OpenAsync(); Message(dialog).Text = "Brouillon valide résumé"; dialog.Close();
        byte[] oldDraft = await File.ReadAllBytesAsync(path);
        dialog = await fixture.OpenAsync();
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "remaining change\n"); await dialog.RefreshAsync();
        Message(dialog).Text = "Incompatible 🧪";
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        Click(dialog, "Commit"); CommitEmptyDialog prompt = await EmptyDialog(dialog); Click(prompt, "StageUnstaged"); await Idle(dialog, error: true);
        Assert.That(dialog.LastError, Does.Contain("translate Unicode character"));
        Assert.That((await fixture.Git("diff", "--cached")).StandardOutput, Is.Empty);
        Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(oldDraft));
        Assert.That(Message(dialog).Text, Is.EqualTo("Incompatible 🧪"));
        dialog.Close();
        Assert.That(dialog.IsVisible, Is.True, "A draft that cannot be saved must remain available to correct or copy.");
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(oldDraft));
        try { await fixture.Repository.CommitAsync(new GitCommitOptions("Direct incompatible 🧪", AllowEmpty: true)); Assert.Fail("The backend must also use a strict encoder."); }
        catch (System.Text.EncoderFallbackException) { }
        try { await fixture.Repository.CommitAsync("Simple incompatible 🧪"); Assert.Fail("The string overload must use the same strict encoder."); }
        catch (System.Text.EncoderFallbackException) { }
        Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Message(dialog).Text = "Brouillon valide résumé"; dialog.Close();
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "COMMITMESSAGE.*.tmp"), Is.Empty);
        string? previousAction = Environment.GetEnvironmentVariable("GIT_REFLOG_ACTION");
        try
        {
            Environment.SetEnvironmentVariable("GIT_REFLOG_ACTION", "Étape 🧪");
            await fixture.Repository.CommitAsync(new GitCommitOptions("Â©  Café\trésumé\nDeuxième ligne du paragraphe\n", AllowEmpty: true));
            await fixture.Git("config", "i18n.commitEncoding", "UTF-8");
            Assert.That((await fixture.Repository.GetReflogAsync(1)).Single().Description, Is.EqualTo("Étape 🧪: Â© Café résumé"));
        }
        finally { Environment.SetEnvironmentVariable("GIT_REFLOG_ACTION", previousAction); }
    }

    [AvaloniaTest]
    public async Task UnknownAndNonGitTextCodecsFailExplicitlyWithoutChangingHistoryIndexOrDraft()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        CommitDialog dialog = await fixture.OpenAsync(); Message(dialog).Text = "Durable UTF-8 λ"; dialog.Close();
        string draft = await GitPath(fixture.Repository, "COMMITMESSAGE"); byte[] before = await File.ReadAllBytesAsync(draft);
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        foreach (string encoding in new[] { "no-such-git-encoding", "utf-16", "utf-32", "shift_jis", "IBM037" })
        {
            await fixture.Git("config", "i18n.commitEncoding", encoding);
            try { await fixture.Repository.CommitAsync(new GitCommitOptions("Cannot use this codec", AllowEmpty: true)); Assert.Fail("Unsupported codec accepted: " + encoding); }
            catch (NotSupportedException exception) { Assert.That(exception.Message, Does.Contain(encoding)); }
            Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
            Assert.That((await fixture.Git("diff", "--cached")).StandardOutput, Is.Empty);
            Assert.That(await File.ReadAllBytesAsync(draft), Is.EqualTo(before));
        }
        await fixture.Git("config", "--unset", "i18n.commitEncoding");
        var utf8 = await fixture.Repository.GetCommitMessageEncodingAsync();
        Assert.That(utf8.GetPreamble(), Is.Empty);
        Assert.That(utf8.GetString(await File.ReadAllBytesAsync(draft)), Is.EqualTo("Durable UTF-8 λ"));
    }

    [AvaloniaTest]
    public async Task UntouchedTemplatePreservesANewGitMergeMessageAndRemovedMetadataKeepsTheEditedDraftAvailable()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string template = Path.Combine(fixture.Temporary, "initial-template");
        const string templateText = "Untouched template subject\n\n# Instructions\n";
        await File.WriteAllTextAsync(template, templateText);
        await fixture.Git("config", "commit.template", template);
        CommitDialog dialog = await fixture.OpenAsync();
        Assert.That(Message(dialog).Text, Is.EqualTo(templateText));
        await fixture.Git("checkout", "-b", "external-merge");
        await fixture.Git("commit", "--allow-empty", "-m", "Same tree different history");
        await fixture.Git("checkout", "main");
        await fixture.Git("merge", "--no-ff", "--no-commit", "external-merge");
        string mergePath = await GitPath(fixture.Repository, "MERGE_MSG");
        string mergeMessage = await File.ReadAllTextAsync(mergePath);
        TaskCompletionSource firstClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, _) => firstClosed.TrySetResult();
        dialog.Close();
        await firstClosed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(dialog.IsVisible, Is.False);
        Assert.That(await File.ReadAllTextAsync(mergePath), Is.EqualTo(mergeMessage), "Closing an untouched, automatically displayed template must preserve the message just created by Git.");
        dialog = await fixture.OpenAsync();
        Assert.That(Message(dialog).Text, Is.EqualTo(mergeMessage));
        Click(dialog, "Commit"); Window prompt = await Confirmation(dialog); AcceptConfirmation(prompt); await Idle(dialog);
        Assert.That((await fixture.Git("rev-list", "--parents", "-1", "HEAD")).StandardOutput.Trim().Split(' ').Length, Is.EqualTo(3));
        Message(dialog).Text = "Keep text when repository metadata disappears";
        string gitDirectory = Path.GetDirectoryName(await GitPath(fixture.Repository, "COMMITMESSAGE"))!;
        string removed = Path.Combine(fixture.Temporary, "temporarily-removed-git-metadata");
        Directory.Move(gitDirectory, removed);
        try
        {
            dialog.Close();
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(dialog.LastError, Is.Not.Null);
            Assert.That(Message(dialog).Text, Is.EqualTo("Keep text when repository metadata disappears"));
        }
        finally { Directory.Move(removed, gitDirectory); }
        TaskCompletionSource finalClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, _) => finalClosed.TrySetResult();
        dialog.Close();
        await finalClosed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(dialog.IsVisible, Is.False);
        Assert.That(await File.ReadAllTextAsync(await GitPath(fixture.Repository, "COMMITMESSAGE")), Is.EqualTo("Keep text when repository metadata disappears"));
    }

    private static bool ProcessAlive(int pid)
    {
        string path = $"/proc/{pid}/status";
        try { return File.Exists(path) && !File.ReadLines(path).Any(line => line.StartsWith("State:", StringComparison.Ordinal) && line.Contains('Z')); }
        catch (IOException) when (!File.Exists(path)) { return false; }
    }
    private static Avalonia.Media.Imaging.WriteableBitmap Capture(TopLevel owner, string name)
    {
        owner.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = owner.CaptureRenderedFrame() ?? throw new AssertionException("No rendered commit frame.");
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "src/linux/GitExtensions.Desktop/GitExtensions.Desktop.csproj"))) { repository = repository.Parent; }
        string directory = Path.Combine(repository?.FullName ?? TestContext.CurrentContext.WorkDirectory, "artifacts/linux/screenshots"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name); frame.Save(path); TestContext.AddTestAttachment(path); return frame;
    }
    private static TextBox Message(CommitDialog dialog) => Field<TextBox>(dialog, "Message");
    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static MenuItem Options(CommitDialog dialog) => Field<Menu>(dialog, "CommitToolbar").Items.OfType<MenuItem>().Single(item => item.Name == "Options");
    private static MenuItem Option(CommitDialog dialog, string name) => Options(dialog).Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static T Input<T>(CommitDialog dialog, string name) where T : Control => Options(dialog).Items.OfType<MenuItem>().Select(item => item.Header).OfType<Control>()
        .SelectMany(control => control.GetVisualDescendants().Prepend(control)).OfType<T>().Single(control => control.Name == name);
    private static void Invoke(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void Click(Window window, string name) { Button button = Field<Button>(window, name); Assert.That(button.IsEnabled, Is.True, name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static string ButtonText(Button button) => button.Content is TextBlock text ? text.Text! : button.Content?.ToString() ?? "";
    private static Rect Bounds(Window window, Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static async Task Idle(CommitDialog dialog, bool error = false)
    {
        await Task.Yield();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Until(() => !dialog.IsBusy && dialog.OwnedWindows.Count == 0);
        if (error) { Assert.That(dialog.LastError, Is.Not.Null); } else { Assert.That(dialog.LastError, Is.Null); }
        dialog.UpdateLayout();
    }
    private static async Task Until(Func<bool> predicate)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); while (!predicate()) { await Task.Delay(20, timeout.Token); } }
    private static async Task<Window> Confirmation(CommitDialog owner)
    { await Until(() => owner.OwnedWindows.Any()); return owner.OwnedWindows.Single(); }
    private static async Task<CommitEmptyDialog> EmptyDialog(CommitDialog owner) => (CommitEmptyDialog)await Confirmation(owner);
    private static void AcceptConfirmation(Window prompt) => prompt.GetVisualDescendants().OfType<Button>().Single(button => !button.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void CancelConfirmation(Window prompt) => prompt.GetVisualDescendants().OfType<Button>().Single(button => button.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static MenuItem[] HistoryMessages(MenuItem menu) => menu.Items.OfType<MenuItem>().Where(item => item.Name == "PreviousMessage").ToArray();
    private static MenuItem HistoryItem(MenuItem menu, string name) => menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static async Task<MenuItem> OpenHistory(CommitDialog dialog)
    {
        MenuItem menu = Field<Menu>(dialog, "CommitToolbar").Items.OfType<MenuItem>().Single(item => item.Name == "CommitMessageHistory");
        menu.Open();
        await Until(() => !menu.Items.OfType<MenuItem>().Any(item => item.Name == "HistoryLoading"));
        Assert.That(dialog.LastError, Is.Null);
        return menu;
    }
    private static async Task<string> GitPath(GitRepository repository, string name) => Path.GetFullPath((await repository.RunAsync(["rev-parse", "--git-path", name])).StandardOutput.TrimEnd('\r', '\n'), repository.Path);

    private sealed class Fixture : IDisposable
    {
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly List<CommitDialog> _dialogs = [];
        public string Temporary { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-commit-messages-" + Guid.NewGuid().ToString("N"));
        public GitRepository Repository { get; private set; } = null!;
        public CommitPreferencesStore Store { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync(string culture = "en")
        {
            Fixture fixture = new();
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(fixture.Temporary, "config"));
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                fixture.Store = new();
                fixture.Repository = await fixture.CreateRepositoryAsync(Path.Combine(fixture.Temporary, "repository"));
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        public async Task<GitRepository> CreateRepositoryAsync(string path)
        {
            Directory.CreateDirectory(path);
            GitResult init = await new GitRunner().RunAsync(path, ["init", "-b", "main"]); Assert.That(init.Success, Is.True, init.StandardError);
            GitRepository repository = new(path);
            foreach (var (key, value) in new[] { ("user.name", "Message Tester"), ("user.email", "message@example.invalid"), ("commit.gpgsign", "false"), ("core.autocrlf", "false"), ("core.hooksPath", Path.Combine(path, ".git", "hooks")) })
            { await repository.SetConfigAsync(key, value); }
            await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), "initial\n");
            await repository.StageAsync(["file.txt"]); await repository.CommitAsync(new GitCommitOptions("Initial commit"));
            return repository;
        }
        public Task<GitResult> Git(params string[] args) => Repository.RunAsync(args);
        public async Task<CommitDialog> OpenAsync(GitRepository? repository = null, bool keepOpen = true)
        {
            CommitDialog dialog = new(repository ?? Repository, Store); _dialogs.Add(dialog); dialog.Show(); await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Null);
            // Session overrides leave the loaded preference defaults available for assertions and explicit menu gestures.
            if (keepOpen)
            {
                Option(dialog, "closeDialogAfterEachCommitToolStripMenuItem").IsChecked = false;
                Option(dialog, "closeDialogAfterAllFilesCommittedToolStripMenuItem").IsChecked = false;
            }
            dialog.UpdateLayout(); return dialog;
        }
        public void WriteHook(string name, string script)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Real Git Unix-hook scenarios require Linux."); }
            string path = Path.Combine(Repository.Path, ".git", "hooks", name);
            File.WriteAllText(path, "#!/bin/sh\n" + script); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose()
        {
            foreach (CommitDialog dialog in _dialogs) { foreach (Window child in dialog.OwnedWindows.ToArray()) { child.Close(); } dialog.Close(); }
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); CultureInfo.CurrentUICulture = _culture;
            if (Directory.Exists(Temporary)) { Directory.Delete(Temporary, true); }
        }
    }
}

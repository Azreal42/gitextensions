using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class CommitDialogTests
{
    [AvaloniaTest]
    public async Task ClassicFrenchDialogKeepsTwoCompactFileListsBesideDiffAndMessage()
    {
        using Fixture fixture = await Fixture.CreateAsync(PartialInitial, PartialChanged, "fr-FR");
        CommitDialog dialog = fixture.Dialog;
        DiffView diff = Find<DiffView>(dialog, "SelectedDiff");
        diff.Select(diff.Text.IndexOf("+selected addition", StringComparison.Ordinal), 1);
        Click(dialog, "StageHunk");
        await Idle(dialog);
        Find<TextBox>(dialog, "Message").Text = "Préserver la disposition originale";
        dialog.UpdateLayout();
        ListBox unstaged = Find<ListBox>(dialog, "Unstaged");
        ListBox staged = Find<ListBox>(dialog, "Staged");
        TextBox message = Find<TextBox>(dialog, "Message");
        Button commit = Find<Button>(dialog, "Commit");
        Assert.That(dialog.Title, Is.EqualTo("Commiter"));
        Assert.That(ButtonText(commit), Is.EqualTo("Commiter"));
        Assert.That(ButtonText(Find<Button>(dialog, "Stage")), Is.EqualTo("Indexer"));
        Assert.That(Find<CheckBox>(dialog, "Amend").Content, Is.EqualTo("Rectifier le commit"));
        Assert.That(unstaged.Items.OfType<GitChange>().Single().Path, Is.EqualTo("file.txt"));
        Assert.That(staged.Items.OfType<GitChange>().Single().Path, Is.EqualTo("file.txt"));
        Assert.That(unstaged.GetVisualDescendants().OfType<ListBoxItem>().Single().Bounds.Height, Is.EqualTo(24));
        Assert.That(staged.GetVisualDescendants().OfType<ListBoxItem>().Single().Bounds.Height, Is.EqualTo(24));
        Rect unstagedBounds = Bounds(dialog, unstaged);
        Rect stagedBounds = Bounds(dialog, staged);
        Rect diffBounds = Bounds(dialog, diff);
        Rect messageBounds = Bounds(dialog, message);
        Rect commitBounds = Bounds(dialog, commit);
        Assert.Multiple(() =>
        {
            Assert.That(unstagedBounds.Bottom, Is.LessThan(stagedBounds.Y));
            Assert.That(stagedBounds.X, Is.EqualTo(unstagedBounds.X).Within(1));
            Assert.That(unstagedBounds.Right, Is.LessThan(diffBounds.X));
            Assert.That(diffBounds.Bottom, Is.LessThan(messageBounds.Y));
            Assert.That(commitBounds.X, Is.GreaterThan(stagedBounds.Right));
            Assert.That(commitBounds.Right, Is.LessThan(messageBounds.X));
            Assert.That(diffBounds.Right, Is.EqualTo(messageBounds.Right).Within(1));
            Assert.That(commit.GetVisualDescendants().OfType<Image>().Single().Bounds.Size, Is.EqualTo(new Size(16, 16)));
        });
        using Avalonia.Media.Imaging.WriteableBitmap? frame = dialog.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null);
        string screenshot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "classic-commit-fr.png");
        frame!.Save(screenshot);
        TestContext.AddTestAttachment(screenshot, "Historical commit layout with staged and unstaged parts of the same file");
        dialog.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        dialog.UpdateLayout();
        Assert.That(((Avalonia.Media.ISolidColorBrush)dialog.Background!).Color, Is.EqualTo(Avalonia.Media.Color.Parse("#323232")));
        Assert.That(((Avalonia.Media.ISolidColorBrush)dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "Indexé").Background!).Color,
            Is.EqualTo(Avalonia.Media.Color.Parse("#373737")));
        using Avalonia.Media.Imaging.WriteableBitmap? dark = dialog.CaptureRenderedFrame();
        Assert.That(dark, Is.Not.Null);
        dark!.Save(Path.Combine(TestContext.CurrentContext.WorkDirectory, "classic-commit-fr-dark.png"));
    }

    [AvaloniaTest]
    public async Task PartialHunksAndUnicodeLinesMoveBetweenTheTwoListsWithoutChangingTheWorktree()
    {
        using Fixture fixture = await Fixture.CreateAsync(PartialInitial, PartialChanged);
        CommitDialog dialog = fixture.Dialog;
        DiffView diff = Find<DiffView>(dialog, "SelectedDiff");
        ListBox unstaged = Find<ListBox>(dialog, "Unstaged");
        ListBox staged = Find<ListBox>(dialog, "Staged");
        diff.Select(diff.Text.IndexOf("+selected addition", StringComparison.Ordinal), 1);
        Click(dialog, "StageHunk");
        await Idle(dialog);
        await AssertIndex(fixture, "selected addition", "remaining addition");
        Assert.That(unstaged.ItemCount, Is.EqualTo(1));
        Assert.That(staged.ItemCount, Is.EqualTo(1));
        staged.SelectedIndex = 0;
        await Idle(dialog);
        const string selected = "+selected addition λ🧪";
        diff.Select(diff.Text.IndexOf(selected, StringComparison.Ordinal), selected.Length);
        Click(dialog, "UnstageLines");
        await Idle(dialog);
        Assert.That((await fixture.Run("diff", "--cached")).StandardOutput, Is.Empty);

        unstaged.SelectedIndex = 0;
        await Idle(dialog);
        diff.Select(diff.Text.IndexOf(selected, StringComparison.Ordinal), selected.Length);
        Click(dialog, "StageLines");
        await Idle(dialog);
        await AssertIndex(fixture, "selected addition", "remaining addition");
        staged.SelectedIndex = 0;
        await Idle(dialog);
        diff.Select(diff.Text.IndexOf(selected, StringComparison.Ordinal), 0);
        Click(dialog, "UnstageHunk");
        await Idle(dialog);
        Assert.That((await fixture.Run("diff", "--cached")).StandardOutput, Is.Empty);
        Assert.That(File.ReadAllText(Path.Combine(fixture.Repository.Path, "file.txt")), Is.EqualTo(PartialChanged));
        Assert.That(dialog.HasRepositoryChanges, Is.True);
        Assert.That(dialog.HasCommitted, Is.False);
    }

    [AvaloniaTest]
    public async Task HookFailureRetainsMessageAndIndexThenHooksRunWithAuthorSignoffAndTemplate()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\nchanged\n");
        CommitDialog dialog = fixture.Dialog;
        string template = Path.Combine(fixture.Temporary, "commit-template");
        await File.WriteAllTextAsync(template, "Configured template subject\n\nEdited body\n");
        await fixture.Run("config", "commit.template", template);
        MenuItem loadTemplate = Toolbar(dialog).Items.OfType<MenuItem>().SelectMany(item => item.Items.OfType<MenuItem>()).Single(item => item.Name == "LoadTemplate");
        loadTemplate.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Idle(dialog);
        TextBox message = Find<TextBox>(dialog, "Message");
        Assert.That(message.Text, Does.StartWith("Configured template subject"));
        Click(dialog, "StageAll");
        await Idle(dialog);
        Assert.That(Find<Button>(dialog, "Commit").IsEnabled, Is.False, "An unchanged template must be edited before committing.");
        message.Text += "\nReady for review\n";
        Input<TextBox>(dialog, "AuthorOverride").Text = "Different Author <author@example.com>";
        Option(dialog, "signOffToolStripMenuItem").IsChecked = true;
        Input<ComboBox>(dialog, "Signing").SelectedIndex = 1;
        string draft = message.Text!;
        string originalHead = (await fixture.Run("rev-parse", "HEAD")).StandardOutput.Trim();
        string index = (await fixture.Run("diff", "--cached")).StandardOutput;
        fixture.WriteHook("pre-commit", "printf 'hook rejected this commit\\n' >&2\nexit 1\n");
        Click(dialog, "Commit");
        Assert.That(Find<ListBox>(dialog, "Unstaged").IsEnabled, Is.False);
        Assert.That(Find<ListBox>(dialog, "Staged").IsEnabled, Is.False);
        Assert.That(message.IsEnabled, Is.False);
        Assert.That(Find<Button>(dialog, "Cancel").IsEnabled, Is.True);
        await Idle(dialog, expectError: true);
        Assert.Multiple(() =>
        {
            Assert.That(dialog.LastError, Does.Contain("hook rejected this commit"));
            Assert.That(Find<TextBox>(dialog, "CommitOutput").IsVisible, Is.True);
            Assert.That(message.Text, Is.EqualTo(draft));
            Assert.That(dialog.HasCommitted, Is.False);
            Assert.That(dialog.IsVisible, Is.True);
        });
        Assert.That((await fixture.Run("rev-parse", "HEAD")).StandardOutput.Trim(), Is.EqualTo(originalHead));
        Assert.That((await fixture.Run("diff", "--cached")).StandardOutput, Is.EqualTo(index));

        string hookLog = Path.Combine(fixture.Temporary, "hooks.log");
        fixture.WriteHook("pre-commit", $"printf 'pre-commit\\n' >> '{hookLog}'\n");
        fixture.WriteHook("prepare-commit-msg", $"printf 'prepare-commit-msg\\n' >> '{hookLog}'\nprintf '\\nPrepared by hook\\n' >> \"$1\"\n");
        fixture.WriteHook("post-commit", $"printf 'post-commit\\n' >> '{hookLog}'\n");
        int repositoryChanges = 0;
        dialog.RepositoryChanged += (_, _) => repositoryChanges++;
        Click(dialog, "Commit");
        await Idle(dialog);
        GitResult committed = await fixture.Run("log", "-1", "--format=%an <%ae>%n%B");
        Assert.That(committed.StandardOutput, Does.StartWith("Different Author <author@example.com>\nConfigured template subject"));
        Assert.That(committed.StandardOutput, Does.Contain("Signed-off-by: Commit Tester <commit@example.com>"));
        Assert.That(committed.StandardOutput, Does.Contain("Prepared by hook"));
        Assert.That(File.ReadAllLines(hookLog), Is.EqualTo(new[] { "pre-commit", "prepare-commit-msg", "post-commit" }));
        Assert.That(message.Text, Is.Empty);
        Assert.That(dialog.HasCommitted, Is.True);
        Assert.That(repositoryChanges, Is.EqualTo(1));
        Assert.That(Find<ListBox>(dialog, "Staged").ItemCount, Is.Zero);
    }

    [AvaloniaTest]
    public async Task AmendFixupAndSquashUseTheSelectedCommitAndPreserveGitHistorySemantics()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\nchanged\n");
        CommitDialog dialog = fixture.Dialog;
        string initial = (await fixture.Run("rev-parse", "HEAD")).StandardOutput.Trim();
        Find<CheckBox>(dialog, "Amend").IsChecked = true;
        await Idle(dialog);
        TextBox message = Find<TextBox>(dialog, "Message");
        Assert.That(message.Text, Is.EqualTo("Initial commit"));
        message.Text = "Amended commit";
        Click(dialog, "StageAll");
        await Idle(dialog);
        Click(dialog, "Commit");
        Window confirmation = await Confirmation(dialog);
        confirmation.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Amend commit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle(dialog);
        Assert.That((await fixture.Run("rev-list", "--count", "HEAD")).StandardOutput.Trim(), Is.EqualTo("1"));
        string amended = (await fixture.Run("rev-parse", "HEAD")).StandardOutput.Trim();
        Assert.That(amended, Is.Not.EqualTo(initial));

        ComboBox kind = Input<ComboBox>(dialog, "CommitKind");
        ComboBox target = Input<ComboBox>(dialog, "TargetCommit");
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "fixup change\n");
        await dialog.RefreshAsync();
        kind.SelectedIndex = 1;
        target.SelectedItem = target.Items.OfType<GitCommit>().Single(commit => commit.Hash == amended);
        Assert.That(message.IsEnabled, Is.False, "Fixup uses its target's subject, so the message editor must show that it is unavailable.");
        Click(dialog, "StageAll");
        await Idle(dialog);
        Click(dialog, "Commit");
        await Idle(dialog);
        Assert.That((await fixture.Run("log", "-1", "--format=%s")).StandardOutput.Trim(), Is.EqualTo("fixup! Amended commit"));

        await File.AppendAllTextAsync(Path.Combine(fixture.Repository.Path, "file.txt"), "squash change\n");
        await dialog.RefreshAsync();
        kind.SelectedIndex = 2;
        target.SelectedItem = target.Items.OfType<GitCommit>().Single(commit => commit.Hash == amended);
        message.Text = "Squash details";
        Click(dialog, "StageAll");
        await Idle(dialog);
        Click(dialog, "Commit");
        await Idle(dialog);
        GitResult squash = await fixture.Run("log", "-1", "--format=%s%n%B");
        Assert.That(squash.StandardOutput, Does.StartWith("squash! Amended commit\n").And.Contain("Squash details"));
        Assert.That((await fixture.Run("rev-list", "--count", "HEAD")).StandardOutput.Trim(), Is.EqualTo("3"));
    }

    [AvaloniaTest]
    public async Task OriginalKeyboardShortcutsFocusTheFourPanesStageAllCommitRefreshAndClose()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\nchanged\n");
        CommitDialog dialog = fixture.Dialog;
        Press(dialog, PhysicalKey.Digit1, RawInputModifiers.Control);
        Assert.That(Find<ListBox>(dialog, "Unstaged").IsKeyboardFocusWithin, Is.True);
        Press(dialog, PhysicalKey.Digit2, RawInputModifiers.Control);
        Assert.That(Find<DiffView>(dialog, "SelectedDiff").IsKeyboardFocusWithin, Is.True);
        Press(dialog, PhysicalKey.Digit3, RawInputModifiers.Control);
        Assert.That(Find<ListBox>(dialog, "Staged").IsKeyboardFocusWithin, Is.True);
        Press(dialog, PhysicalKey.Digit4, RawInputModifiers.Control);
        Assert.That(Find<TextBox>(dialog, "Message").IsKeyboardFocusWithin, Is.True);
        Press(dialog, PhysicalKey.S, RawInputModifiers.Control);
        await Idle(dialog);
        Assert.That(Find<ListBox>(dialog, "Unstaged").ItemCount, Is.Zero);
        Assert.That(Find<ListBox>(dialog, "Staged").ItemCount, Is.EqualTo(1));
        Click(dialog, "UnstageAll");
        await Idle(dialog);
        const string literalPath = "new λ; $(touch should-not-exist).txt";
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, literalPath), "untracked content\n");
        Press(dialog, PhysicalKey.F5);
        await Idle(dialog);
        ListBox unstaged = Find<ListBox>(dialog, "Unstaged");
        unstaged.SelectedItem = unstaged.Items.OfType<GitChange>().Single(change => change.Path == literalPath);
        await Idle(dialog);
        Assert.That(Find<DiffView>(dialog, "SelectedDiff").Text, Is.EqualTo("untracked content\n"));
        Click(dialog, "Stage");
        await Idle(dialog);
        Assert.That(Find<ListBox>(dialog, "Staged").Items.OfType<GitChange>().Single().Path, Is.EqualTo(literalPath));
        Assert.That(File.Exists(Path.Combine(fixture.Repository.Path, "should-not-exist")), Is.False);
        Find<TextBox>(dialog, "Message").Text = "Commit with keyboard";
        Find<TextBox>(dialog, "Message").Focus();
        Press(dialog, PhysicalKey.Enter, RawInputModifiers.Control);
        await Idle(dialog);
        Assert.That((await fixture.Run("log", "-1", "--format=%s")).StandardOutput.Trim(), Is.EqualTo("Commit with keyboard"));
        Press(dialog, PhysicalKey.Escape);
        Assert.That(dialog.IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task SelectionRegexKeepsBothListsAndStageAllButtonsAndShortcutStageAllLiteralPaths()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        CommitDialog dialog = fixture.Dialog;
        const string selectedPath = "visible λ[1]; $(touch should-not-exist).txt";
        const string otherPath = "visible λ1; $(touch should-not-exist).txt";
        const string initial = "initial\n";
        const string selectedChanged = "initial\nselected change\n";
        const string otherStaged = "initial\nstaged change\n";
        const string otherChanged = "initial\nstaged change\nunstaged change\n";
        await fixture.Run("mv", "file.txt", otherPath);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, selectedPath), initial);
        await fixture.Run("add", "--", selectedPath);
        await fixture.Run("commit", "-m", "Add literal file path");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, otherPath), otherStaged);
        await fixture.Run("add", "--", otherPath);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, otherPath), otherChanged);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository.Path, selectedPath), selectedChanged);
        await dialog.RefreshAsync();
        Press(dialog, PhysicalKey.F, RawInputModifiers.Control);
        ComboBox filter = Find<ComboBox>(dialog, "SelectionFilter");
        string expression = "^" + Regex.Escape(selectedPath.ToUpperInvariant()) + "$";
        filter.Text = expression;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (!filter.Items.OfType<string>().Contains(expression)) { await Task.Delay(20, timeout.Token); }
        await Idle(dialog);
        ListBox unstaged = Find<ListBox>(dialog, "Unstaged"), staged = Find<ListBox>(dialog, "Staged");
        Assert.That(unstaged.ItemCount, Is.EqualTo(2));
        Assert.That(unstaged.SelectedItems!.OfType<GitChange>().Single().Path, Is.EqualTo(selectedPath));
        Assert.That(staged.Items.OfType<GitChange>().Single().Path, Is.EqualTo(otherPath), "Selection Filter does not hide indexed files.");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt == 0) { Click(dialog, "StageAll"); }
            else { Press(dialog, PhysicalKey.S, RawInputModifiers.Control); }
            await Idle(dialog);
            Assert.That((await fixture.Run("show", ":" + selectedPath)).StandardOutput, Is.EqualTo(selectedChanged));
            Assert.That((await fixture.Run("show", ":" + otherPath)).StandardOutput, Is.EqualTo(otherChanged));
            Assert.That(unstaged.ItemCount, Is.Zero);
            Assert.That(staged.ItemCount, Is.EqualTo(2));
            Click(dialog, "UnstageAll"); await Idle(dialog);
            Assert.That((await fixture.Run("show", ":" + selectedPath)).StandardOutput, Is.EqualTo(initial));
            Assert.That((await fixture.Run("show", ":" + otherPath)).StandardOutput, Is.EqualTo(initial));
            Assert.That(unstaged.ItemCount, Is.EqualTo(2));
            Assert.That(staged.ItemCount, Is.Zero);
        }
        Assert.That(File.ReadAllText(Path.Combine(fixture.Repository.Path, selectedPath)), Is.EqualTo(selectedChanged));
        Assert.That(File.ReadAllText(Path.Combine(fixture.Repository.Path, otherPath)), Is.EqualTo(otherChanged));
        Assert.That(File.Exists(Path.Combine(fixture.Repository.Path, "should-not-exist")), Is.False);
    }

    [AvaloniaTest]
    public async Task MergeWithIdenticalTreesCanBeCommittedAndDefaultCloseOccursOnlyAfterSuccess()
    {
        using Fixture fixture = await Fixture.CreateAsync("same content\n", "same content\n");
        await fixture.Run("checkout", "-b", "feature");
        await fixture.Run("commit", "--allow-empty", "-m", "Feature checkpoint");
        await fixture.Run("checkout", "main");
        await fixture.Run("commit", "--allow-empty", "-m", "Main checkpoint");
        await fixture.Run("merge", "--no-ff", "--no-commit", "feature");
        fixture.Dialog.Close();
        CommitDialog dialog = new(fixture.Repository);
        try
        {
            dialog.Show();
            await dialog.InitializeAsync();
            Assert.That(Find<ListBox>(dialog, "Staged").ItemCount, Is.Zero);
            Assert.That(Find<TextBox>(dialog, "Message").Text, Does.Contain("Merge branch 'feature'"));
            Assert.That(Find<Button>(dialog, "Commit").IsEnabled, Is.True);
            Click(dialog, "Commit");
            Window confirmation = await Confirmation(dialog);
            confirmation.GetVisualDescendants().OfType<Button>().Single(button => !button.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle(dialog);
            Assert.That(dialog.HasCommitted, Is.True);
            Assert.That(dialog.IsVisible, Is.False);
            string parents = (await fixture.Run("log", "-1", "--format=%P")).StandardOutput.Trim();
            Assert.That(parents.Split(' ').Length, Is.EqualTo(2));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task RenameMetadataAndUnbornBranchUnstagingUseBothPathsAndPreserveTheWorktree()
    {
        using Fixture fixture = await Fixture.CreateAsync(PartialInitial, PartialInitial);
        CommitDialog dialog = fixture.Dialog;
        const string renamed = "renamed λ.txt";
        await fixture.Run("mv", "file.txt", renamed);
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository.Path, renamed), "renamed change\n");
        await dialog.RefreshAsync();
        Press(dialog, PhysicalKey.F, RawInputModifiers.Control);
        ComboBox filter = Find<ComboBox>(dialog, "SelectionFilter");
        filter.Text = "RENAMED λ";
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (!filter.Items.OfType<string>().Contains("RENAMED λ")) { await Task.Delay(20, timeout.Token); }
        await Idle(dialog);
        Assert.That(Find<ListBox>(dialog, "Unstaged").Items.OfType<GitChange>().Single().Path, Is.EqualTo(renamed),
            "Selection matches the current name without changing Git's case-sensitive path identity.");
        Click(dialog, "StageAll");
        await Idle(dialog);
        DiffView diff = Find<DiffView>(dialog, "SelectedDiff");
        Assert.That(diff.Text, Does.Contain("rename from file.txt").And.Contain("rename to renamed λ.txt"));
        Click(dialog, "UnstageAll");
        await Idle(dialog);
        Assert.That((await fixture.Run("diff", "--cached")).StandardOutput, Is.Empty);
        Assert.That(File.Exists(Path.Combine(fixture.Repository.Path, "file.txt")), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(fixture.Repository.Path, renamed)), Is.EqualTo(PartialInitial + "renamed change\n"));
        filter.Text = "";
        await Idle(dialog);

        await fixture.Run("checkout", "--orphan", "unborn");
        await fixture.Run("rm", "--cached", "--", "file.txt");
        await dialog.RefreshAsync();
        Assert.That(Find<CheckBox>(dialog, "Amend").IsEnabled, Is.False);
        Click(dialog, "StageAll");
        await Idle(dialog);
        Assert.That(Find<ListBox>(dialog, "Staged").Items.OfType<GitChange>().Single().IsStaged, Is.True);
        Click(dialog, "UnstageAll");
        await Idle(dialog);
        Assert.That(Find<ListBox>(dialog, "Staged").ItemCount, Is.Zero);
        Assert.That(Find<ListBox>(dialog, "Unstaged").Items.OfType<GitChange>().Single().Path, Is.EqualTo(renamed));
        Assert.That(File.Exists(Path.Combine(fixture.Repository.Path, renamed)), Is.True);
    }

    private static readonly string PartialInitial = string.Join('\n', Enumerable.Range(1, 30).Select(index => $"original line {index:00}")) + "\n";
    private static readonly string PartialChanged = PartialInitial.Replace("original line 02", "original line 02\nselected addition λ🧪", StringComparison.Ordinal)
        .Replace("original line 26", "original line 26\nremaining addition", StringComparison.Ordinal);
    private static T Find<T>(Window dialog, string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static Menu Toolbar(CommitDialog dialog) => Find<Menu>(dialog, "CommitToolbar");
    private static MenuItem Options(CommitDialog dialog) => Toolbar(dialog).Items.OfType<MenuItem>().Single(item => item.Name == "Options");
    private static MenuItem Option(CommitDialog dialog, string name) => Options(dialog).Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static T Input<T>(CommitDialog dialog, string name) where T : Control => Options(dialog).Items.OfType<MenuItem>().Select(item => item.Header).OfType<StackPanel>().SelectMany(panel => panel.Children).OfType<T>().Single(control => control.Name == name);
    private static string ButtonText(Button button) => button.GetVisualDescendants().OfType<TextBlock>().Single().Text!;
    private static Rect Bounds(Window window, Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void Click(CommitDialog dialog, string name)
    {
        Button button = Find<Button>(dialog, name);
        Assert.That(button.IsEffectivelyVisible && button.IsEnabled, Is.True, name + " must be available.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); }
    }
    private static async Task Idle(CommitDialog dialog, bool expectError = false)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (dialog.IsBusy && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        Assert.That(dialog.IsBusy, Is.False, "Commit workflow did not complete within 15 seconds.");
        if (!expectError) { Assert.That(dialog.LastError, Is.Null); }
        dialog.UpdateLayout();
    }
    private static async Task AssertIndex(Fixture fixture, string selected, string remaining)
    {
        Assert.That((await fixture.Run("diff", "--cached")).StandardOutput, Does.Contain("+" + selected).And.Not.Contain("+" + remaining));
        Assert.That((await fixture.Run("diff")).StandardOutput, Does.Contain("+" + remaining).And.Not.Contain("+" + selected));
    }

    private static async Task<Window> Confirmation(CommitDialog dialog)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (!dialog.OwnedWindows.Any()) { await Task.Delay(20, timeout.Token); }
        return dialog.OwnedWindows.Single();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        public string Temporary { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-commit-tests-" + Guid.NewGuid().ToString("N"));
        public GitRepository Repository { get; private set; } = null!;
        public CommitDialog Dialog { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync(string initial, string changed, string language = "en")
        {
            Fixture fixture = new();
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(fixture.Temporary, "config"));
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                string path = Path.Combine(fixture.Temporary, "repository");
                Directory.CreateDirectory(path);
                fixture.Repository = new GitRepository(path);
                GitResult init = await new GitRunner().RunAsync(path, ["init", "-b", "main"]);
                Assert.That(init.Success, Is.True, init.StandardError);
                await fixture.Run("config", "user.name", "Commit Tester");
                await fixture.Run("config", "user.email", "commit@example.com");
                await fixture.Run("config", "commit.gpgsign", "false");
                await fixture.Run("config", "core.autocrlf", "false");
                await fixture.Run("config", "core.hooksPath", Path.Combine(path, ".git", "hooks"));
                await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), initial);
                await fixture.Run("add", "--", "file.txt");
                await fixture.Run("commit", "-m", "Initial commit");
                await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), changed);
                fixture.Dialog = new CommitDialog(fixture.Repository);
                fixture.Dialog.Show();
                await fixture.Dialog.InitializeAsync();
                Option(fixture.Dialog, "closeDialogAfterEachCommitToolStripMenuItem").IsChecked = false;
                Option(fixture.Dialog, "closeDialogAfterAllFilesCommittedToolStripMenuItem").IsChecked = false;
                Assert.That(fixture.Dialog.LastError, Is.Null);
                fixture.Dialog.UpdateLayout();
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        public Task<GitResult> Run(params string[] arguments) => Repository.RunAsync(arguments);
        public void WriteHook(string name, string script)
        {
            string path = Path.Combine(Repository.Path, ".git", "hooks", name);
            File.WriteAllText(path, "#!/bin/sh\n" + script);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }
        public void Dispose()
        {
            Dialog?.Close();
            CultureInfo.CurrentUICulture = _culture;
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg);
            if (Directory.Exists(Temporary)) { Directory.Delete(Temporary, true); }
        }
    }
}

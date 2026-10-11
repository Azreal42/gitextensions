using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class CommitSelectionFilterTests
{
    [AvaloniaTest]
    public async Task OriginalHiddenEditableComboAndShortcutKeepCompactTranslatedLightAndDarkLayout()
    {
        using Fixture fixture = await Fixture.CreateAsync("first.txt", "second.txt");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        CommitDialog dialog = await fixture.OpenAsync();
        ComboBox filter = Field<ComboBox>(dialog, "SelectionFilter");
        Control toolbar = Field<Control>(dialog, "SelectionFilterToolbar");
        Assert.That(toolbar.IsVisible, Is.False);
        Assert.That(filter.IsEditable, Is.True);
        Press(dialog, PhysicalKey.F, RawInputModifiers.Control);
        Assert.That(toolbar.IsVisible, Is.True);
        TextBox editor = filter.GetVisualDescendants().OfType<TextBox>().Single();
        editor.Focus(); dialog.KeyTextInput("^FIRST\\.txt$");
        await Applied(dialog, "^FIRST\\.txt$");
        Assert.That(Selected(dialog), Is.EqualTo(new[] { "first.txt" }));
        Assert.That(ToolTip.GetTip(filter), Is.EqualTo("Entrer une expression régulière pour sélectionner des fichiers non-indexés."));
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            dialog.RequestedThemeVariant = theme; dialog.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            using var frame = dialog.CaptureRenderedFrame();
            Assert.That(frame, Is.Not.Null);
            Assert.That(filter.Bounds.Size, Is.EqualTo(new Size(121, 24)));
            Assert.That(editor.Bounds.Height, Is.LessThanOrEqualTo(24));
            Assert.That(editor.GetVisualDescendants().OfType<TextPresenter>().Single().Bounds.Height, Is.GreaterThan(0));
            string directory = Screenshots(); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "classic-commit-selection-filter-fr-" + theme + ".png");
            frame!.Save(path); TestContext.AddTestAttachment(path);
            filter.IsDropDownOpen = true; dialog.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Popup popup = filter.GetVisualDescendants().OfType<Popup>().Single();
            Assert.That(popup.IsOpen, Is.True);
            TextBlock caption = popup.Child!.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "^FIRST\\.txt$");
            Assert.That(caption.IsEffectivelyVisible, Is.True);
            Assert.That(caption.Bounds.Height, Is.GreaterThan(0));
            TopLevel host = (TopLevel)caption.GetVisualRoot()!;
            using var popupFrame = host.CaptureRenderedFrame();
            Assert.That(popupFrame, Is.Not.Null);
            string popupPath = Path.Combine(directory, "classic-commit-selection-filter-popup-fr-" + theme + ".png");
            popupFrame!.Save(popupPath); TestContext.AddTestAttachment(popupPath);
            filter.IsDropDownOpen = false; Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        Press(dialog, PhysicalKey.F, RawInputModifiers.Control);
        Assert.That(toolbar.IsVisible, Is.False);
        Assert.That(Field<ListBox>(dialog, "Unstaged").IsFocused, Is.True);
        dialog.Close();
        CommitDialog reopened = await fixture.OpenAsync();
        Assert.That(Field<Control>(reopened, "SelectionFilterToolbar").IsVisible, Is.False, "The original toggle is local to the dialog.");
        Assert.That(Field<ComboBox>(reopened, "SelectionFilter").Items, Is.Empty, "The original selection history is local to the dialog.");
    }

    [AvaloniaTest]
    public async Task RegexSelectsMultipleCurrentPathsWithoutHidingFilesAndSelectedStagePreservesOtherIndexEntries()
    {
        using Fixture fixture = await Fixture.CreateAsync("src/one.cs", "src/TWO.cs", "src/other.txt", "indexed.txt");
        await fixture.Repository.StageAsync(["indexed.txt"]);
        string indexedBefore = (await fixture.Git("diff", "--cached")).StandardOutput;
        CommitDialog dialog = await fixture.OpenAsync();
        await SetRegex(dialog, @"^SRC/.*\.cs$");
        Assert.That(Selected(dialog), Is.EquivalentTo(new[] { "src/one.cs", "src/TWO.cs" }));
        Assert.That(Field<ListBox>(dialog, "Unstaged").ItemCount, Is.EqualTo(3));
        Assert.That(Field<ListBox>(dialog, "Staged").ItemCount, Is.EqualTo(1));
        Click(dialog, "Stage"); await Idle(dialog);
        Assert.That((await fixture.Git("diff", "--cached", "--", "indexed.txt")).StandardOutput, Is.EqualTo(indexedBefore));
        Assert.That((await fixture.Git("show", ":src/other.txt")).StandardOutput, Is.EqualTo(Fixture.Initial));
        Assert.That(Field<ListBox>(dialog, "Staged").ItemCount, Is.EqualTo(3));
        ListBox staged = Field<ListBox>(dialog, "Staged");
        foreach (GitChange change in staged.Items.OfType<GitChange>().Where(change => change.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))) { staged.SelectedItems!.Add(change); }
        await Idle(dialog); Click(dialog, "Unstage"); await Idle(dialog);
        Assert.That((await fixture.Git("diff", "--cached")).StandardOutput, Is.EqualTo(indexedBefore));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, "src/one.cs")), Is.EqualTo(Fixture.Changed));
    }

    [AvaloniaTest]
    public async Task InvalidRegexKeepsSelectionIndexDraftAndHistoryAndReportsTranslatedError()
    {
        using Fixture fixture = await Fixture.CreateAsync("first.txt", "second.txt");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        CommitDialog dialog = await fixture.OpenAsync();
        await SetRegex(dialog, "first");
        Field<TextBox>(dialog, "Message").Text = "Brouillon λ🧪";
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        ComboBox filter = Field<ComboBox>(dialog, "SelectionFilter"); filter.Text = "[";
        await Until(() => ToolTip.GetTip(filter) is string text && text.StartsWith("Erreur ", StringComparison.Ordinal));
        Assert.That(Selected(dialog), Is.EqualTo(new[] { "first.txt" }));
        Assert.That(filter.Items.OfType<string>(), Is.EqualTo(new[] { "first" }));
        Assert.That(Field<ListBox>(dialog, "Unstaged").ItemCount, Is.EqualTo(2));
        Assert.That((await fixture.Git("diff", "--cached")).StandardOutput, Is.Empty);
        Assert.That(await fixture.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That(Field<TextBox>(dialog, "Message").Text, Is.EqualTo("Brouillon λ🧪"));
        Assert.That(dialog.LastError, Is.Null);
        filter.Text = "second"; await Applied(dialog, "second");
        Assert.That(Selected(dialog), Is.EqualTo(new[] { "second.txt" }));
        Assert.That(ToolTip.GetTip(filter), Is.EqualTo("Entrer une expression régulière pour sélectionner des fichiers non-indexés."));
    }

    [AvaloniaTest]
    public async Task SessionHistoryKeepsTenSuccessfulExpressionsWithoutPromotingDuplicatesAndSupportsDropdownSelection()
    {
        string[] names = Enumerable.Range(0, 12).Select(index => $"file{index:D2}.txt").ToArray();
        using Fixture fixture = await Fixture.CreateAsync(names);
        CommitDialog dialog = await fixture.OpenAsync();
        for (int index = 0; index < names.Length; index++) { await SetRegex(dialog, "^" + Regex.Escape(names[index]) + "$"); }
        ComboBox filter = Field<ComboBox>(dialog, "SelectionFilter");
        string[] before = filter.Items.OfType<string>().ToArray();
        Assert.That(before, Is.EqualTo(names.Skip(2).Reverse().Select(name => "^" + Regex.Escape(name) + "$")));
        filter.SelectedItem = before[4]; await Until(() => Selected(dialog).SequenceEqual(new[] { names[7] }) && !dialog.IsBusy);
        Assert.That(filter.Text, Is.EqualTo(before[4]));
        Assert.That(filter.Items.OfType<string>(), Is.EqualTo(before), "The original history does not promote existing expressions.");
        filter.Text = "unmatched"; await Until(() => Selected(dialog).Length == 0);
        Assert.That(filter.Items.OfType<string>(), Is.EqualTo(before));
        filter.Text = ""; await Applied(dialog, "");
        Assert.That(Selected(dialog), Is.EquivalentTo(names));
        Assert.That(filter.Items.OfType<string>().First(), Is.Empty, "Clearing the expression selects all and is retained when it matches files.");
        Assert.That(filter.ItemCount, Is.EqualTo(10));
    }

    [AvaloniaTest]
    public async Task RegexEscapeStagesOnlyTheSelectedLiteralPathIncludingMetacharactersAndNewline()
    {
        const string literal = "nested/λ[1]; $(touch nope)\n?.txt", other = "nested/λ1; $(touch nope)X.txt";
        using Fixture fixture = await Fixture.CreateAsync(literal, other);
        CommitDialog dialog = await fixture.OpenAsync();
        await SetRegex(dialog, "^" + Regex.Escape(literal) + "$" );
        Assert.That(Selected(dialog), Is.EqualTo(new[] { literal }));
        Click(dialog, "Stage"); await Idle(dialog);
        Assert.That((await fixture.Git("diff", "--cached", "--name-only", "-z")).StandardOutput, Is.EqualTo(literal + '\0'));
        Assert.That((await fixture.Git("show", ":" + other)).StandardOutput, Is.EqualTo(Fixture.Initial));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository.Path, other)), Is.EqualTo(Fixture.Changed));
        Assert.That(File.Exists(Path.Combine(fixture.Repository.Path, "nope")), Is.False);
    }

    [AvaloniaTest]
    public async Task NoMatchesDoNotDisableStageAllOrTheOriginalStageAndCommitAllChoice()
    {
        using Fixture fixture = await Fixture.CreateAsync("first.txt", "second.txt");
        CommitDialog dialog = await fixture.OpenAsync();
        ComboBox filter = Field<ComboBox>(dialog, "SelectionFilter"); filter.Text = "not-present";
        await Until(() => Selected(dialog).Length == 0);
        Assert.That(Field<Button>(dialog, "Stage").IsEnabled, Is.False);
        Assert.That(Field<Button>(dialog, "StageAll").IsEnabled, Is.True);
        Assert.That(Field<ListBox>(dialog, "Unstaged").ItemCount, Is.EqualTo(2));
        Field<TextBox>(dialog, "Message").Text = "Both displayed files";
        Click(dialog, "Commit"); await Until(() => dialog.OwnedWindows.OfType<CommitEmptyDialog>().Any());
        CommitEmptyDialog prompt = dialog.OwnedWindows.OfType<CommitEmptyDialog>().Single();
        Button stage = Field<Button>(prompt, "StageUnstaged");
        Assert.That(stage.IsEnabled, Is.True);
        Assert.That(((TextBlock)stage.Content!).Text, Is.EqualTo("Stage and commit all unstaged files"));
        stage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(dialog);
        Assert.That((await fixture.Git("diff-tree", "--no-commit-id", "--name-only", "-z", "-r", "HEAD")).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries), Is.EquivalentTo(new[] { "first.txt", "second.txt" }));
        Assert.That((await fixture.Repository.GetStatusAsync()).Changes, Is.Empty);
    }

    private static string[] Selected(CommitDialog dialog) => Field<ListBox>(dialog, "Unstaged").SelectedItems!.OfType<GitChange>().Select(change => change.Path).ToArray();
    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(CommitDialog window, string name) { Button button = Field<Button>(window, name); Assert.That(button.IsEnabled, Is.True); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers) { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } }
    private static async Task SetRegex(CommitDialog dialog, string expression)
    {
        if (!Field<Control>(dialog, "SelectionFilterToolbar").IsVisible) { Press(dialog, PhysicalKey.F, RawInputModifiers.Control); }
        Field<ComboBox>(dialog, "SelectionFilter").Text = expression; await Applied(dialog, expression);
    }
    private static async Task Applied(CommitDialog dialog, string expression)
    { await Until(() => Field<ComboBox>(dialog, "SelectionFilter").Items.OfType<string>().Contains(expression)); await Idle(dialog); }
    private static async Task Idle(CommitDialog dialog)
    { await Task.Yield(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); await Until(() => !dialog.IsBusy && dialog.OwnedWindows.Count == 0); Assert.That(dialog.LastError, Is.Null); dialog.UpdateLayout(); }
    private static async Task Until(Func<bool> predicate)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); while (!predicate()) { await Task.Delay(20, timeout.Token); } }
    private static string Screenshots()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src/linux/GitExtensions.Desktop/GitExtensions.Desktop.csproj"))) { directory = directory.Parent; }
        return Path.Combine(directory?.FullName ?? TestContext.CurrentContext.WorkDirectory, "artifacts/linux/screenshots");
    }

    private sealed class Fixture : IDisposable
    {
        public const string Initial = "initial\n", Changed = "initial\nchanged λ🧪\n";
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly List<CommitDialog> _dialogs = [];
        public string Temporary { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-selection-filter-" + Guid.NewGuid().ToString("N"));
        public GitRepository Repository { get; private set; } = null!;
        public CommitPreferencesStore Store { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync(params string[] names)
        {
            Fixture fixture = new();
            try
            {
                string path = Path.Combine(fixture.Temporary, "repository"); Directory.CreateDirectory(path);
                GitResult init = await new GitRunner().RunAsync(path, ["init", "-b", "main"]); Assert.That(init.Success, Is.True, init.StandardError);
                fixture.Repository = new(path); fixture.Store = new(Path.Combine(fixture.Temporary, "config"));
                foreach (var (key, value) in new[] { ("user.name", "Filter Tester"), ("user.email", "filter@example.invalid"), ("commit.gpgsign", "false"), ("core.autocrlf", "false"), ("core.hooksPath", Path.Combine(path, ".git/hooks")) }) { await fixture.Repository.SetConfigAsync(key, value); }
                foreach (string name in names) { string file = Path.Combine(path, name); Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllTextAsync(file, Initial); }
                await fixture.Repository.StageAsync(names); await fixture.Repository.CommitAsync(new GitCommitOptions("Initial commit"));
                foreach (string name in names) { await File.WriteAllTextAsync(Path.Combine(path, name), Changed); }
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        public Task<GitResult> Git(params string[] args) => Repository.RunAsync(args);
        public async Task<CommitDialog> OpenAsync()
        {
            CommitDialog dialog = new(Repository, Store); _dialogs.Add(dialog); dialog.Show(); await dialog.InitializeAsync();
            Menu options = Field<Menu>(dialog, "CommitToolbar");
            foreach (MenuItem item in options.Items.OfType<MenuItem>().Single(item => item.Name == "Options").Items.OfType<MenuItem>().Where(item => item.Name is "closeDialogAfterEachCommitToolStripMenuItem" or "closeDialogAfterAllFilesCommittedToolStripMenuItem")) { item.IsChecked = false; }
            await Idle(dialog); return dialog;
        }
        public void Dispose()
        {
            foreach (CommitDialog dialog in _dialogs) { foreach (Window child in dialog.OwnedWindows.ToArray()) { child.Close(); } dialog.Close(); }
            CultureInfo.CurrentUICulture = _culture;
            if (Directory.Exists(Temporary)) { Directory.Delete(Temporary, true); }
        }
    }
}

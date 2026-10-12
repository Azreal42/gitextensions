using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class ViewerEscapeHostTests
{
    private const string Draft = "Préserver ce brouillon λ🧪\n\nMême après Échap.\n";

    [AvaloniaTest]
    public async Task EscapeInDiffWithoutSelectionClosesCommitAndPreservesDraftDespiteRememberedSearchHighlights()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        CommitDialog commit = fixture.Dialog; DiffView diff = fixture.Diff;
        fixture.Message.Text = Draft;
        diff.Select(0, 0); FocusDiff(commit, diff);
        diff.Search.Query = "shared"; Assert.That(diff.Search.HighlightAll(), Is.GreaterThanOrEqualTo(3));
        Assert.That(diff.SelectionLength, Is.Zero); Assert.That(diff.Editor.TextArea.Selection.IsEmpty, Is.True);
        Assert.That(diff.Search.Highlights, Is.Not.Empty, "A stored query and highlighted occurrences do not create another Escape priority in the original viewer.");
        Assert.That(File.Exists(fixture.DraftPath), Is.False);

        Press(commit, PhysicalKey.Escape);
        await Until(() => !commit.IsVisible);
        Assert.That(await File.ReadAllTextAsync(fixture.DraftPath), Is.EqualTo(Draft));
        await fixture.AssertGitUnchangedAsync();

        CommitDialog reopened = await fixture.OpenCommitAsync();
        Assert.That(Find<TextBox>(reopened, "Message").Text, Is.EqualTo(Draft), "The raw message must survive a real close and reopen.");
    }

    [AvaloniaTest]
    public async Task EscapeInDiffSelectionClearsOnlySelectionThenTheSecondEscapeClosesAndPreservesDraft()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        CommitDialog commit = fixture.Dialog; DiffView diff = fixture.Diff;
        fixture.Message.Text = Draft;
        int start = diff.Text.IndexOf("shared", StringComparison.Ordinal); Assert.That(start, Is.GreaterThanOrEqualTo(0));
        diff.Select(start, "shared".Length); FocusDiff(commit, diff);
        Assert.That(diff.SelectionLength, Is.EqualTo("shared".Length)); Assert.That(diff.Search.Occurrences.Count, Is.GreaterThanOrEqualTo(3));
        int caret = diff.CaretOffset;

        Press(commit, PhysicalKey.Escape); Pump(commit);
        Assert.That(commit.IsVisible, Is.True, "Original Escape clears an actual diff selection before dismissing FormCommit.");
        Assert.That(diff.SelectionLength, Is.Zero); Assert.That(diff.Editor.TextArea.Selection.IsEmpty, Is.True);
        Assert.That(diff.CaretOffset, Is.EqualTo(caret), "Clearing selection must not move the reader's caret.");
        Assert.That(diff.Editor.TextArea.IsFocused, Is.True); Assert.That(fixture.Message.Text, Is.EqualTo(Draft));
        Assert.That(File.Exists(fixture.DraftPath), Is.False, "The first Escape has not closed or saved the dialog.");
        await fixture.AssertGitUnchangedAsync();

        Press(commit, PhysicalKey.Escape); await Until(() => !commit.IsVisible);
        Assert.That(await File.ReadAllTextAsync(fixture.DraftPath), Is.EqualTo(Draft)); await fixture.AssertGitUnchangedAsync();
    }

    [AvaloniaTest]
    public async Task EscapeInFindClosesOnlyFindClearsItsRegionAndReturnsToTheStillSelectedCommitDiff()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        CommitDialog commit = fixture.Dialog; DiffView diff = fixture.Diff;
        fixture.Message.Text = Draft;
        int start = diff.Text.IndexOf(" shared alpha", StringComparison.Ordinal), end = diff.Text.IndexOf(" shared gamma", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0)); Assert.That(end, Is.GreaterThan(start));
        int length = end + " shared gamma".Length - start;
        diff.Search.Query = "shared"; diff.Select(start, length); FocusDiff(commit, diff);
        Press(commit, PhysicalKey.F, RawInputModifiers.Control);
        await Until(() => commit.OwnedWindows.Any(window => window.Name == "DiffViewerFindDialog" && window.IsVisible));
        Window find = commit.OwnedWindows.Single(window => window.Name == "DiffViewerFindDialog" && window.IsVisible);
        TextBox query = Find<TextBox>(find, "DiffViewerFindQuery");
        Assert.That(query.IsKeyboardFocusWithin, Is.True); Assert.That(diff.Search.ScanRegion, Is.Not.Null);
        Assert.That(diff.Search.ScanRegion!.Offset, Is.EqualTo(start)); Assert.That(diff.Search.ScanRegion.Length, Is.EqualTo(length));

        find.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        commit.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await Until(() => !find.IsVisible); Pump(commit);
        Assert.That(commit.IsVisible, Is.True); Assert.That(diff.Search.ScanRegion, Is.Null);
        Assert.That(diff.Search.Query, Is.EqualTo("shared")); Assert.That(diff.SelectionLength, Is.EqualTo(length));
        Assert.That(diff.Editor.TextArea.IsFocused, Is.True); Assert.That(fixture.Message.Text, Is.EqualTo(Draft));
        Assert.That(File.Exists(fixture.DraftPath), Is.False); await fixture.AssertGitUnchangedAsync();

        // Find's Escape must not also reach the owner. The owner still follows
        // the original selection-first sequence when the next key is pressed.
        Press(commit, PhysicalKey.Escape); Assert.That(commit.IsVisible, Is.True); Assert.That(diff.SelectionLength, Is.Zero);
        Press(commit, PhysicalKey.Escape); await Until(() => !commit.IsVisible);
        Assert.That(await File.ReadAllTextAsync(fixture.DraftPath), Is.EqualTo(Draft)); await fixture.AssertGitUnchangedAsync();
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(TopLevel root) { root.UpdateLayout(); Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); }
    private static void FocusDiff(CommitDialog commit, DiffView diff)
    {
        Assert.That(diff.Editor.TextArea.Focus(), Is.True); Pump(commit);
        Assert.That(diff.Editor.TextArea.IsFocused, Is.True, "The actual editor TextArea must receive the tested Escape.");
    }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); }
    }
    private static async Task Until(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); }
        Dispatcher.UIThread.RunJobs();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _oldXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly List<CommitDialog> _dialogs = [];
        private readonly string _temporary = Path.Combine(Path.GetTempPath(), "gitextensions-viewer-escape-" + Guid.NewGuid().ToString("N"));
        private const string PathName = "shared [λ].txt";
        private const string Initial = "shared alpha\nshared beta\nshared gamma\n";
        private const string Changed = "shared alpha\nshared beta changed λ\nshared gamma\n";
        private string _head = "", _index = "";
        internal GitRepository Repository { get; private set; } = null!;
        internal CommitDialog Dialog { get; private set; } = null!;
        internal DiffView Diff => Find<DiffView>(Dialog, "SelectedDiff");
        internal TextBox Message => Find<TextBox>(Dialog, "Message");
        internal string DraftPath { get; private set; } = "";

        internal static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(fixture._temporary, "config"));
                string repository = Path.Combine(fixture._temporary, "repository"); Directory.CreateDirectory(repository);
                GitResult init = await new GitRunner().RunAsync(repository, ["init", "-b", "main"]); Assert.That(init.Success, Is.True, init.StandardError);
                fixture.Repository = new(repository);
                await fixture.GitAsync("config", "user.name", "Escape Reader"); await fixture.GitAsync("config", "user.email", "escape@example.org");
                await fixture.GitAsync("config", "commit.gpgsign", "false"); await fixture.GitAsync("config", "core.autocrlf", "false");
                await fixture.GitAsync("config", "i18n.commitEncoding", "UTF-8");
                await fixture.GitAsync("config", "core.hooksPath", Path.Combine(fixture._temporary, "no-hooks"));
                await File.WriteAllTextAsync(Path.Combine(repository, PathName), Initial);
                await fixture.GitAsync("add", "--", PathName); await fixture.GitAsync("commit", "-m", "Initial reader content");
                fixture._head = await fixture.GitAsync("rev-parse", "HEAD"); fixture._index = await fixture.GitAsync("ls-files", "--stage");
                fixture.DraftPath = Path.GetFullPath((await fixture.GitAsync("rev-parse", "--git-path", "COMMITMESSAGE")).TrimEnd('\r', '\n'), repository);
                await File.WriteAllTextAsync(Path.Combine(repository, PathName), Changed);
                fixture.Dialog = await fixture.OpenCommitAsync();
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        internal async Task<CommitDialog> OpenCommitAsync()
        {
            CommitDialog dialog = new(Repository); _dialogs.Add(dialog); dialog.Show(); await dialog.InitializeAsync();
            await Until(() => !dialog.IsBusy); Assert.That(dialog.LastError, Is.Null);
            FileStatusTestHelpers.Select(Find<TreeView>(dialog, "Unstaged"), PathName);
            await Until(() => !dialog.IsBusy); Pump(dialog);
            DiffView diff = Find<DiffView>(dialog, "SelectedDiff");
            Assert.That(diff.Text, Does.Contain("+shared beta changed λ")); Assert.That(diff.IsDocumentReady, Is.True);
            return dialog;
        }
        internal async Task AssertGitUnchangedAsync()
        {
            Assert.That(await GitAsync("rev-parse", "HEAD"), Is.EqualTo(_head)); Assert.That(await GitAsync("ls-files", "--stage"), Is.EqualTo(_index));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(Repository.Path, PathName)), Is.EqualTo(Changed));
        }
        private async Task<string> GitAsync(params string[] arguments)
        { GitResult result = await Repository.RunAsync(arguments); Assert.That(result.Success, Is.True, result.StandardError); return result.StandardOutput; }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (CommitDialog dialog in _dialogs.AsEnumerable().Reverse()) { dialog.Close(); }
                await Until(() => _dialogs.All(dialog => !dialog.IsVisible));
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldXdg);
                if (Directory.Exists(_temporary)) { Directory.Delete(_temporary, true); }
            }
        }
    }
}

using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class ViewerHostLoadFailureTests
{
    private const string FirstPath = ":first[1]* λ.txt";
    private const string SecondPath = ":second[2]* λ.txt";
    private const string Added = "selected first-file insertion";
    private const string Omitted = "omitted first-file insertion";
    private const string InvalidEncoding = "gitextensions-no-such-encoding-5529";

    [AvaloniaTest]
    public void DirectDocumentTransitionClearsEqualTextBlobCacheBeforeCodecChange()
    {
        const string patch = "diff --git a/file.txt b/file.txt\nindex 7898192..6178079 100644\n--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-before\n+after\n";
        TextBox source = new();
        using FileContentView content = new(source);
        DiffView view = content.TextView;
        content.ShowFile(":untracked[1]* λ.txt", Encoding.UTF8.GetBytes(patch), "utf-8");
        Assert.That(view.CanonicalText, Is.EqualTo(patch));
        Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.PlainText));
        GitDiffDocument document = new(patch, patch, GitDiffAppearance.Patch, true)
        {
            FileEncodingName = "utf-8", PatchBytes = Encoding.UTF8.GetBytes(patch), IsLosslessPatch = true
        };
        view.Mode = DiffViewMode.Auto;
        view.ShowDocument(document);
        AssertReady(view);
        int reloads = 0;
        view.DisplayOptionsChanged += (_, _) => reloads++;
        view.FileEncodingName = "windows-1252";
        Assert.That(reloads, Is.EqualTo(1), "The codec choice must request a tracked-document reload instead of rendering the old blob cache.");
        Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.Unified));
        Assert.That(view.Document, Is.SameAs(document));
        Assert.That(view.CanonicalText, Is.EqualTo(patch));
        AssertBlocked(view);
        view.ShowDocument(document with { FileEncodingName = "windows-1252" });
        AssertReady(view);
        Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.Unified));
        Assert.That(view.CanonicalText, Is.EqualTo(patch));
    }

    [AvaloniaTest]
    public async Task MainFailedOtherFileLoadCannotStagePreviousDocumentAndSuccessfulReturnStagesOnlySelectedBytes()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        MainWindow main = await fixture.MainAsync();
        TreeView files = Find<TreeView>(main, "WorkingFiles");
        DiffView view = Find<DiffView>(main, "WorktreeDiff");
        await SelectMainFileAsync(main, files, view, FirstPath);
        SelectAdded(view);
        AssertReady(view);
        GitDiffDocument previous = view.Document!;

        await fixture.Repository.SetConfigAsync("i18n.filesencoding", InvalidEncoding);
        FileStatusTestHelpers.Select(files, SecondPath);
        await Until(() => !main.IsBusy && main.LastException is not null);
        Assert.That(view.DocumentIdentity, Does.Contain(SecondPath));
        Assert.That(main.LastException!.Message, Does.Contain(InvalidEncoding));
        AssertBlocked(view);
        AssertMainStageButtons(main, enabled: false);
        Assert.That(view.Document, Is.SameAs(previous), "A failed read may retain the previous display, but must not authorize its patch.");
        await fixture.AssertUnchangedAsync();

        await fixture.Repository.SetConfigAsync("i18n.filesencoding", "utf-8");
        await SelectMainFileAsync(main, files, view, FirstPath);
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        AssertReady(view);
        SelectAdded(view);
        AssertMainStageButtons(main, enabled: true);
        Click(MainStageButton(main, "Stage selected lines"));
        await Until(() => !main.IsBusy && FileStatusTestHelpers.Changes(files).Single(change => change.Path == FirstPath).IsStaged);
        await fixture.AssertOnlyFirstInsertionStagedAsync();
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }

    [AvaloniaTest]
    public async Task CommitFailedOtherFileLoadDisablesPartialStageThenSuccessfulReturnUsesTheActualNewDocument()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        CommitDialog dialog = fixture.Track(new CommitDialog(fixture.Repository));
        dialog.Show();
        await dialog.InitializeAsync();
        TreeView unstaged = Find<TreeView>(dialog, "Unstaged");
        DiffView view = Find<DiffView>(dialog, "SelectedDiff");
        FileStatusTestHelpers.Select(unstaged, FirstPath);
        await Until(() => !dialog.IsBusy && view.DocumentIdentity?.Contains(FirstPath, StringComparison.Ordinal) == true && view.Document is not null);
        SelectAdded(view);
        AssertReady(view);
        GitDiffDocument previous = view.Document!;

        await fixture.Repository.SetConfigAsync("i18n.filesencoding", InvalidEncoding);
        FileStatusTestHelpers.Select(unstaged, SecondPath);
        await Until(() => !dialog.IsBusy && dialog.LastError is not null);
        Assert.That(view.DocumentIdentity, Does.Contain(SecondPath));
        Assert.That(dialog.LastError, Does.Contain(InvalidEncoding));
        AssertBlocked(view);
        AssertCommitStageButtons(dialog, enabled: false);
        Assert.That(view.Document, Is.SameAs(previous));
        await fixture.AssertUnchangedAsync();

        await fixture.Repository.SetConfigAsync("i18n.filesencoding", "utf-8");
        FileStatusTestHelpers.Select(unstaged, FirstPath);
        await Until(() => !dialog.IsBusy && view.DocumentIdentity?.Contains(FirstPath, StringComparison.Ordinal) == true && view.CanStagePatch);
        Assert.That(dialog.LastError, Is.Null, dialog.LastError);
        AssertReady(view);
        SelectAdded(view);
        AssertCommitStageButtons(dialog, enabled: true);
        Click(Find<Button>(dialog, "StageLines"));
        TreeView staged = Find<TreeView>(dialog, "Staged");
        await Until(() => !dialog.IsBusy && FileStatusTestHelpers.Changes(staged).Any(change => change.Path == FirstPath));
        await fixture.AssertOnlyFirstInsertionStagedAsync();
        Assert.That(dialog.LastError, Is.Null, dialog.LastError);
        Assert.That(dialog.HasCommitted, Is.False);
    }

    [AvaloniaTest]
    public async Task MainSameIdentityFailedCodecReloadKeepsReadingPositionAndSuccessfulRetryReauthorizesDocument()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        MainWindow main = await fixture.MainAsync();
        DiffView view = Find<DiffView>(main, "WorktreeDiff");
        await SelectMainFileAsync(main, Find<TreeView>(main, "WorkingFiles"), view, FirstPath);
        SelectAdded(view);
        string identity = view.DocumentIdentity!;
        string selected = Selection(view);
        int fileLine = view.CurrentFileLine;
        Assert.That(fileLine, Is.GreaterThan(150), "This position must be a file line rather than a small diff row.");
        AssertReady(view);
        GitDiffDocument previous = view.Document!;

        await fixture.Repository.SetConfigAsync("i18n.filesencoding", InvalidEncoding);
        ChooseEncoding(VisibleEncoding(main), "utf-8");
        await Until(() => !main.IsBusy && main.LastException is not null);
        AssertBlocked(view);
        AssertMainStageButtons(main, enabled: false);
        Assert.That(view.DocumentIdentity, Is.EqualTo(identity));
        Assert.That(Selection(view), Is.EqualTo(selected));
        Assert.That(view.CurrentFileLine, Is.EqualTo(fileLine));
        await fixture.AssertUnchangedAsync();

        await fixture.Repository.SetConfigAsync("i18n.filesencoding", "utf-8");
        ChooseEncoding(VisibleEncoding(main), "windows-1252");
        await Until(() => !main.IsBusy && !ReferenceEquals(view.Document, previous) && view.CanStagePatch);
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        AssertReady(view);
        AssertMainStageButtons(main, enabled: true);
        Assert.That(view.DocumentIdentity, Is.EqualTo(identity));
        Assert.That(Selection(view), Is.EqualTo(selected));
        Assert.That(view.CurrentFileLine, Is.EqualTo(fileLine));
        Assert.That(view.Document!.FileEncodingName, Is.EqualTo("windows-1252"));
        await fixture.AssertUnchangedAsync();
    }

    [AvaloniaTest]
    public async Task MainIdenticalUntrackedPreviewAndTrackedPatchDoNotKeepThePreviousFilesCodecCache()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        const string cachedPath = ":cached-patch[9]* λ.txt";
        string patch = (await fixture.Repository.GetDiffDocumentAsync(FirstPath)).Patch;
        byte[] cachedBytes = new UTF8Encoding(false, true).GetBytes(patch);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Repository.Path, cachedPath), cachedBytes);
        MainWindow main = await fixture.MainAsync();
        TreeView files = Find<TreeView>(main, "WorkingFiles");
        DiffView view = Find<DiffView>(main, "WorktreeDiff");
        FileStatusTestHelpers.Select(files, cachedPath);
        await Until(() => !main.IsBusy && view.DocumentIdentity?.Contains(cachedPath, StringComparison.Ordinal) == true && view.Text == patch);
        Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.PlainText));
        Assert.That(view.Document, Is.Null);
        Assert.That(view.CanStagePatch, Is.False);

        await SelectMainFileAsync(main, files, view, FirstPath);
        Assert.That(view.CanonicalText, Is.EqualTo(patch), "The transition deliberately assigns the same TextBox string.");
        AssertReady(view);
        GitDiffDocument previous = view.Document!;
        ChooseEncoding(VisibleEncoding(main), "windows-1252");
        await Until(() => !main.IsBusy && !ReferenceEquals(view.Document, previous) && view.Document is not null);
        Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.Unified));
        AssertReady(view);
        Assert.That(view.Document!.FileEncodingName, Is.EqualTo("windows-1252"));
        Assert.That(view.CanonicalText, Is.EqualTo(patch), "A codec change must reload the tracked diff, not reinterpret the old untracked bytes.");
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(fixture.Repository.Path, cachedPath)), Is.EqualTo(cachedBytes));
        Assert.That((await fixture.Repository.GetStatusAsync()).Changes.Single(change => change.Path == cachedPath).IsUntracked, Is.True);
        await fixture.AssertUnchangedAsync();
    }

    private static void AssertReady(DiffView view)
    {
        Assert.That(view.Document, Is.Not.Null);
        Assert.That(view.Document!.IsLosslessPatch, Is.True);
        Assert.That(view.CanStagePatch, Is.True);
        Assert.That(view.CanCopyPatch, Is.True);
    }

    private static void AssertBlocked(DiffView view)
    {
        Assert.That(view.CanStagePatch, Is.False, "The previously displayed patch is not a successfully loaded document for the current request.");
        Assert.That(view.CanCopyPatch, Is.False, "Version/patch copies must not describe the previous file as the newly selected one.");
    }

    private static async Task SelectMainFileAsync(MainWindow main, TreeView files, DiffView view, string path)
    {
        FileStatusTestHelpers.Select(files, path);
        await Until(() => !main.IsBusy && view.DocumentIdentity?.Contains(path, StringComparison.Ordinal) == true && view.CanStagePatch);
    }

    private static void SelectAdded(DiffView view)
    {
        string token = "+" + Added;
        int start = view.Text.IndexOf(token, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        view.Select(start, token.Length);
    }

    private static string Selection(DiffView view) => view.Text.Substring(view.SelectionStart, view.SelectionLength);
    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static ComboBox VisibleEncoding(Window window)
        => DiffViewerInputTestHelpers.VisibleEncoding(window);
    private static Button MainStageButton(Window window, string text)
        => window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == text);
    private static void AssertMainStageButtons(Window window, bool enabled)
    {
        Assert.That(MainStageButton(window, "Stage selected lines").IsEnabled, Is.EqualTo(enabled));
        Assert.That(MainStageButton(window, "Stage selected hunks").IsEnabled, Is.EqualTo(enabled));
    }
    private static void AssertCommitStageButtons(Window window, bool enabled)
    {
        Assert.That(Find<Button>(window, "StageLines").IsEnabled, Is.EqualTo(enabled));
        Assert.That(Find<Button>(window, "StageHunk").IsEnabled, Is.EqualTo(enabled));
    }
    private static void ChooseEncoding(ComboBox selector, string name)
    {
        int index = GitFileEncodingCodec.AvailableEncodings.ToList().FindIndex(choice => choice.Name == name);
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        Assert.That(selector.Items.OfType<DiffViewerEncodingChoice>().Any(choice => choice.Name == name), Is.True,
            "The isolated legacy profile must include the requested encoding in the real selector: " + name);
        Assert.That(selector.IsEffectivelyVisible && selector.IsEnabled, Is.True);
        selector.SelectedIndex = index + 1;
    }
    private static void Click(Button button)
    {
        Assert.That(button.IsEnabled && button.IsEffectivelyVisible, Is.True, button.Name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static async Task Until(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (!predicate()) { await Task.Delay(20, timeout.Token); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly List<Window> _windows = [];
        private readonly Dictionary<string, byte[]> _initial = [];
        private readonly Dictionary<string, byte[]> _changed = [];
        private readonly string _temporary = Path.Combine(Path.GetTempPath(), "gitextensions-viewer-load-failure-" + Guid.NewGuid().ToString("N"));
        public GitRepository Repository { get; private set; } = null!;
        public T Track<T>(T window) where T : Window { _windows.Add(window); return window; }

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(fixture._temporary, "configuration"));
                LegacyViewerEncodingPreferences.Seed(Path.Combine(fixture._temporary, "configuration"));
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
                fixture.Repository = await GitRepository.InitAsync(Path.Combine(fixture._temporary, "repository"), new GitInitOptions("main"));
                await fixture.Repository.SetConfigAsync("user.name", "Viewer Load Tester");
                await fixture.Repository.SetConfigAsync("user.email", "viewer-load@example.invalid");
                await fixture.Repository.SetConfigAsync("commit.gpgsign", "false");
                await fixture.Repository.SetConfigAsync("core.autocrlf", "false");
                await fixture.Repository.SetConfigAsync("core.hooksPath", Path.Combine(fixture._temporary, "no-hooks"));
                await fixture.Repository.SetConfigAsync("i18n.filesencoding", "utf-8");
                string initial = string.Concat(Enumerable.Range(1, 240).Select(line => $"line {line:000}\n"));
                fixture._initial[FirstPath] = Encoding.UTF8.GetBytes(initial);
                fixture._changed[FirstPath] = Encoding.UTF8.GetBytes(WithFirst(initial).Replace("line 230\n", Omitted + "\nline 230\n", StringComparison.Ordinal));
                fixture._initial[SecondPath] = Encoding.UTF8.GetBytes("original second-file content\n");
                fixture._changed[SecondPath] = Encoding.UTF8.GetBytes("original second-file content\nsecond-file insertion\n");
                foreach ((string path, byte[] bytes) in fixture._initial) { await File.WriteAllBytesAsync(Path.Combine(fixture.Repository.Path, path), bytes); }
                await fixture.Repository.StageAllAsync();
                await fixture.Repository.CommitAsync("Initial load-failure content");
                foreach ((string path, byte[] bytes) in fixture._changed) { await File.WriteAllBytesAsync(Path.Combine(fixture.Repository.Path, path), bytes); }
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async Task<MainWindow> MainAsync()
        {
            MainWindow main = Track(new MainWindow { Width = 1440, Height = 900 });
            main.Show();
            await main.OpenRepositoryAsync(Repository.Path);
            await Until(() => !main.IsBusy);
            Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
            TabControl tabs = Find<TabControl>(main, "DetailsTabs");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "ChangesTab");
            main.UpdateLayout();
            return main;
        }

        public async Task AssertUnchangedAsync()
        {
            foreach ((string path, byte[] bytes) in _initial) { Assert.That(await IndexAsync(path), Is.EqualTo(bytes), "The failed read must not touch the index: " + path); }
            await AssertWorktreeAsync();
        }

        public async Task AssertOnlyFirstInsertionStagedAsync()
        {
            Assert.That(await IndexAsync(FirstPath), Is.EqualTo(Encoding.UTF8.GetBytes(WithFirst(Encoding.UTF8.GetString(_initial[FirstPath])))), "The omitted insertion must remain out of the index.");
            Assert.That(await IndexAsync(SecondPath), Is.EqualTo(_initial[SecondPath]), "The other selected-before-failure file must remain unstaged.");
            await AssertWorktreeAsync();
        }

        private async Task AssertWorktreeAsync()
        {
            foreach ((string path, byte[] bytes) in _changed) { Assert.That(await File.ReadAllBytesAsync(Path.Combine(Repository.Path, path)), Is.EqualTo(bytes), "Reading/partial staging must preserve worktree bytes: " + path); }
        }

        private async Task<byte[]> IndexAsync(string path)
        {
            GitBinaryResult result = await new GitRunner().RunBinaryAsync(Repository.Path, ["show", ":" + path]);
            Assert.That(result.Success, Is.True, result.StandardError);
            return result.StandardOutput;
        }

        private static string WithFirst(string text) => text.Replace("line 180\n", Added + "\nline 180\n", StringComparison.Ordinal);

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window window in _windows.AsEnumerable().Reverse())
                {
                    foreach (Window child in window.OwnedWindows.ToArray()) { child.Close(); }
                    window.Close();
                }
                await Until(() => _windows.All(window => !window.IsVisible));
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg);
                CultureInfo.CurrentUICulture = _culture;
                if (Directory.Exists(_temporary)) { Directory.Delete(_temporary, recursive: true); }
            }
        }
    }
}

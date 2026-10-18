using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands.Settings;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
[SupportedOSPlatform("linux")]
public class GeneralDiffAlgorithmTests
{
    private const string Literal = "[algorithm] café.txt";
    private const string Untracked = "[untracked] café.txt";
    private static readonly GitDiffDisplayOptions Plain = new(UseGitColoring: false);

    [AvaloniaTest]
    public async Task GeneralPublicationChangesAnOpenReaderInBothDirectionsWithoutReplacingItsLocalOptions()
    {
        await using Fixture f = await Fixture.CreateAsync();
        using DiffView reader = new(new TextBox(), new DiffViewerPreferencesStore(f.Configuration));
        Window host = f.Track(new Window { Content = reader, Width = 800, Height = 500 }); host.Show();
        GitRevisionChanges changes = await f.Repository.GetRevisionChangesAsync(f.Head);
        GitRevisionFile file = changes.Files.Single(item => item.Path == Literal);
        reader.ShowDocument(await f.Repository.GetRevisionFileDiffDocumentAsync(changes, file, new(), Plain));
        Assert.That(reader.ComparisonMenu.SetOptions(new(IgnoreWhitespaceKind.Eol, 2, TreatAllFilesAsText: true)), Is.True);
        Assert.That(reader.ComparisonMenu.SetRememberContextLines(true), Is.True);
        Assert.That(reader.ComparisonMenu.SetRememberWholeFile(true), Is.True);
        GitComparisonDiffOptions local = reader.ComparisonMenu.Options;
        foreach (bool histogram in new[] { false, true, false })
        {
            f.PublishHistogram(histogram);
            GitDiffDocument document = await f.ReadDocumentAsync(() => f.Repository.GetRevisionFileDiffDocumentAsync(
                changes, file, reader.ComparisonMenu.Options, Plain), histogram, "diff", "+committed edit");
            reader.ShowDocument(document);
            Assert.Multiple(() =>
            {
                Assert.That(reader.Text, Does.Contain("+committed edit"));
                Assert.That(reader.ComparisonMenu.Options, Is.EqualTo(local with { UseHistogram = histogram }));
                Assert.That(reader.ComparisonMenu.RememberContextLines && reader.ComparisonMenu.RememberWholeFile, Is.True);
                Assert.That(reader.CanStagePatch, Is.True);
            });
            string[] command = f.DiffCommands().First();
            Assert.That(command, Does.Contain("--ignore-space-at-eol").And.Contain("--unified=2").And.Contain("--text"));
            Assert.That(command[^1], Is.EqualTo(Literal));
        }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task RealRootParentWorkingIndexPairAndUntrackedCommandsUseThePublishedAlgorithm(bool histogram)
    {
        await using Fixture f = await Fixture.CreateAsync(); f.PublishHistogram(histogram);
        using DiffViewerComparisonMenu reader = new(new DiffViewerPreferencesStore(f.Configuration));
        GitComparisonDiffOptions options = reader.Options;
        GitRevisionChanges root = await f.Repository.GetRevisionChangesAsync(f.Root);
        await f.ReadDocumentAsync(() => f.Repository.GetRevisionFileDiffDocumentAsync(root,
            root.Files.Single(file => file.Path == Literal), options, Plain), histogram, "diff-tree", "+line 001");
        GitRevisionChanges parent = await f.Repository.GetRevisionChangesAsync(f.Head);
        await f.ReadDocumentAsync(() => f.Repository.GetRevisionFileDiffDocumentAsync(parent,
            parent.Files.Single(file => file.Path == Literal), options, Plain), histogram, "diff", "+committed edit");

        await File.WriteAllTextAsync(f.File, Fixture.Content("index edit")); await f.NativeAsync("add", "--", Literal);
        await File.WriteAllTextAsync(f.File, Fixture.Content("working edit"));
        await f.ReadDocumentAsync(() => f.Repository.GetDiffDocumentAsync(Literal, options: reader.Options, displayOptions: Plain),
            histogram, "diff", "+working edit");
        Assert.That(f.DiffCommands().First(), Does.Not.Contain("--cached"));
        await f.ReadDocumentAsync(() => f.Repository.GetDiffDocumentAsync(Literal, staged: true, options: reader.Options, displayOptions: Plain),
            histogram, "diff", "+index edit");
        Assert.That(f.DiffCommands().First(), Does.Contain("--cached"));

        GitComparison pair = await f.Repository.GetComparisonAsync(new(f.Root), new(f.Head));
        await f.ReadDocumentAsync(() => f.Repository.GetComparisonFileDiffDocumentAsync(pair,
            pair.Files.Single(file => file.Path == Literal), reader.Options, Plain), histogram, "diff", "+committed edit");
        await File.WriteAllTextAsync(Path.Combine(f.Path, Untracked), "untracked literal content\n");
        GitComparison untracked = await f.Repository.GetComparisonAsync(new(f.Head), new("", GitCompareRevisionKind.WorkingTree));
        await f.ReadDocumentAsync(() => f.Repository.GetComparisonFileDiffDocumentAsync(untracked,
            untracked.Files.Single(file => file.Path == Untracked), reader.Options, Plain), histogram, "diff", "+untracked literal content");
        Assert.That(f.DiffCommands().First(), Does.Contain("--no-index").And.Contain("/dev/null"));

        f.ClearLog(); string summary = await f.Repository.GetCommitDiffAsync(f.Head, reader.Options);
        string[] show = f.AssertDiffCommands(histogram, "show").Single();
        Assert.That(summary, Is.EqualTo((await f.RawAsync(show)).StandardOutput));
        Assert.That(summary, Does.Contain("Changed algorithm content").And.Contain("+committed edit"));
    }

    [AvaloniaTest]
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task RealRangeAndCombinedCommandsRetainExplicitOptionsAndNeverDuplicateHistogram(bool global, bool explicitCombined)
    {
        await using Fixture f = await Fixture.CreateAsync();
        string common = f.Head;
        await f.NativeAsync("checkout", "-b", "old-series");
        await File.WriteAllTextAsync(f.File, Fixture.Content("old series option")); await f.CommitAsync("Implement series change");
        string first = (await f.NativeAsync("rev-parse", "HEAD")).StandardOutput.Trim();
        await f.NativeAsync("checkout", "-b", "new-series", common);
        await File.WriteAllTextAsync(f.File, Fixture.Content("new series option")); await f.CommitAsync("Implement series change");
        string second = (await f.NativeAsync("rev-parse", "HEAD")).StandardOutput.Trim();
        f.PublishHistogram(global);
        using DiffViewerComparisonMenu reader = new(new DiffViewerPreferencesStore(f.Configuration));
        GitRangeDiffComparison range = await f.Repository.CreateRangeDiffComparisonAsync(first, second, common, common);
        await f.ReadDocumentAsync(() => f.Repository.GetRangeDiffDocumentAsync(range,
            new(ContextLines: reader.Options.ContextLines, UseHistogram: reader.Options.UseHistogram), Plain),
            global, "range-diff", "Implement series change");

        GitResult merge = await f.RawAsync(["merge", "--no-ff", "--no-edit", "old-series"]);
        Assert.That(merge.ExitCode, Is.EqualTo(1), "The fixture requires a real content conflict: " + merge.StandardError);
        await File.WriteAllTextAsync(f.File, Fixture.Content("resolved merge option")); await f.CommitAsync("Resolve algorithm merge");
        GitCombinedDiff combined = await f.Repository.GetCombinedDiffAsync("HEAD");
        GitDiffDocument document = await f.ReadDocumentAsync(() => f.Repository.GetCombinedFileDiffDocumentAsync(combined,
            combined.Files.Single(file => file.Path == Literal), new(DiffOptions: reader.Options, UseHistogram: explicitCombined), Plain),
            global || explicitCombined, "diff-tree", "++resolved merge option");
        Assert.That(document.Patch, Does.Contain("@@@")); Assert.That(document.CanStageEquivalent, Is.False);
        Assert.That(f.DiffCommands().Select(command => command.Count(argument => argument == "--histogram")),
            Is.All.EqualTo(global || explicitCombined ? 1 : 0));
    }

    [AvaloniaTest]
    public async Task CompareReloadsTheSameSelectedFileAfterGeneralChangesInsteadOfReusingItsOldCache()
    {
        await using Fixture f = await Fixture.CreateAsync();
        CompareRevisionsDialog dialog = f.Track(new CompareRevisionsDialog(f.Repository, f.Root, f.Head));
        dialog.Show(); await dialog.InitializeAsync(); await dialog.SelectionCompletion;
        DiffView view = Find<FileHistoryContentView>(dialog, "ComparisonDiff").TextView;
        await Until(() => !dialog.IsBusy && !dialog.IsSelectionLoading && view.Document is not null);
        GitRevisionFile selected = dialog.SelectedFile!;
        Assert.That(selected.Path, Is.EqualTo(Literal));
        GitComparisonGroup group = dialog.SelectedGroup!;
        Assert.That(view.ComparisonMenu.SetOptions(view.ComparisonMenu.Options with { ContextLines = 2, IgnoreWhitespace = IgnoreWhitespaceKind.Eol }), Is.True);
        await Until(() => !dialog.IsBusy && !dialog.IsSelectionLoading);
        GitComparisonDiffOptions local = dialog.ReadDiffOptions();
        foreach (bool histogram in new[] { true, false })
        {
            GitDiffDocument before = view.Document!;
            f.PublishHistogram(histogram); f.ClearLog();
            await view.Toolbar.NextChangeRequested!();
            await dialog.SelectionCompletion;
            Assert.Multiple(() =>
            {
                Assert.That(dialog.SelectedGroup, Is.SameAs(group));
                Assert.That(dialog.SelectedFile, Is.EqualTo(selected));
                Assert.That(view.Document, Is.Not.SameAs(before));
                Assert.That(dialog.ReadDiffOptions(), Is.EqualTo(local with { UseHistogram = histogram }));
                Assert.That(view.Text, Does.Contain("+committed edit"));
                Assert.That(dialog.LastException, Is.Null, dialog.LastError);
            });
            string[] canonical = f.AssertDiffCommands(histogram, "diff").First(command => command.Contains("--no-color"));
            Assert.That(view.Document!.PatchBytes.ToArray(), Is.EqualTo((await f.RawBinaryAsync(canonical)).StandardOutput));
            Assert.That(f.Commands().Any(command => command.Contains("--name-status")), Is.False, "An unchanged comparison keeps its file groups.");
        }
    }

    [AvaloniaTest]
    public async Task MainAlgorithmReloadReplacesOnlyDiffDocumentsAndPreservesHistoryFilesSelectionAndIndex()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.File, Fixture.Content("working reload edit"));
        MainWindow main = f.Track(new MainWindow { Width = 1280, Height = 840 });
        main.Show(); await main.OpenRepositoryAsync(f.Path); await Until(() => !main.IsBusy);
        SelectTab(main, "DiffTab"); await Until(() => !main.IsBusy);
        TreeView revisionFiles = Find<TreeView>(main, "RevisionFiles");
        TreeViewItem revisionRow = FileStatusTestHelpers.Rows(revisionFiles).Single(row => row.Tag is RevisionDiffFileSelection selection && selection.File.Path == Literal);
        revisionFiles.SelectedItem = revisionRow; await Until(() => !main.IsBusy);
        DiffView commitView = Find<DiffView>(main, "CommitDiff");
        SelectTab(main, "ChangesTab");
        TreeView working = Find<TreeView>(main, "WorkingFiles"); FileStatusTestHelpers.Select(working, Literal);
        DiffView workingView = Find<DiffView>(main, "WorktreeDiff");
        await Until(() => !main.IsBusy && workingView.Document is not null);
        ListBox history = Find<ListBox>(main, "RevisionHistory"); history.Focus();
        object historyItems = history.ItemsSource!, revisionItems = revisionFiles.ItemsSource!, workingItems = working.ItemsSource!;
        object historySelection = history.SelectedItem!, workingSelection = working.SelectedItem!;
        byte[] index = (await f.RawBinaryAsync(["ls-files", "--stage"])).StandardOutput;
        byte[] worktree = await File.ReadAllBytesAsync(f.File);
        foreach (bool histogram in new[] { true, false })
        {
            GitDiffDocument oldCommit = commitView.Document!, oldWorking = workingView.Document!;
            f.PublishHistogram(histogram); f.ClearLog();
            await main.ReloadGeneralDiffAlgorithmAsync(); await Until(() => !main.IsBusy);
            Assert.Multiple(() =>
            {
                Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
                Assert.That(commitView.Document, Is.Not.SameAs(oldCommit)); Assert.That(workingView.Document, Is.Not.SameAs(oldWorking));
                Assert.That(commitView.Text, Does.Contain("+committed edit")); Assert.That(workingView.Text, Does.Contain("+working reload edit"));
                Assert.That(history.ItemsSource, Is.SameAs(historyItems)); Assert.That(revisionFiles.ItemsSource, Is.SameAs(revisionItems));
                Assert.That(working.ItemsSource, Is.SameAs(workingItems)); Assert.That(history.SelectedItem, Is.SameAs(historySelection));
                Assert.That(revisionFiles.SelectedItem, Is.SameAs(revisionRow)); Assert.That(working.SelectedItem, Is.SameAs(workingSelection));
                Assert.That(history.IsKeyboardFocusWithin, Is.True);
            });
            Assert.That(f.AssertDiffCommands(histogram, "diff").Length, Is.GreaterThanOrEqualTo(2));
            Assert.That(f.Commands().Any(command => command.Any(argument => argument is "log" or "status" or "for-each-ref" or "ls-tree")
                || command.Contains("--name-status")), Is.False, "Changing the diff algorithm must not reload Browse or its file list.");
            Assert.That((await f.RawBinaryAsync(["ls-files", "--stage"])).StandardOutput, Is.EqualTo(index));
            Assert.That(await File.ReadAllBytesAsync(f.File), Is.EqualTo(worktree));
        }
    }

    [AvaloniaTest]
    public async Task AnAlreadyOpenCommitDialogUsesTheNewAlgorithmOnItsNextRepositoryRead()
    {
        await using Fixture f = await Fixture.CreateAsync(); await File.WriteAllTextAsync(f.File, Fixture.Content("commit dialog edit"));
        CommitDialog dialog = f.Track(new CommitDialog(f.Repository)); dialog.Show(); await dialog.InitializeAsync();
        FileStatusTestHelpers.Select(Find<TreeView>(dialog, "Unstaged"), Literal);
        DiffView view = Find<DiffView>(dialog, "SelectedDiff");
        await Until(() => !dialog.IsBusy && view.Document is not null);
        foreach (bool histogram in new[] { true, false })
        {
            GitDiffDocument before = view.Document!; f.PublishHistogram(histogram); f.ClearLog();
            await dialog.RefreshAsync(); await Until(() => !dialog.IsBusy && view.Document is not null);
            Assert.That(dialog.LastError, Is.Null); Assert.That(view.Document, Is.Not.SameAs(before));
            Assert.That(view.Text, Does.Contain("+commit dialog edit"));
            f.AssertDiffCommands(histogram, "diff");
        }
    }

    private static T Find<T>(Control root, string name) where T : Control
        => root.GetLogicalDescendants().OfType<T>().Concat(root.GetVisualDescendants().OfType<T>()).Distinct().Single(control => control.Name == name);

    private static void SelectTab(MainWindow main, string name)
    {
        TabControl tabs = Find<TabControl>(main, "DetailsTabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == name);
        main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
    }

    private static async Task Until(Func<bool> ready)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)), "The actual reader did not finish.");
            Dispatcher.UIThread.RunJobs(); await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly string? _global = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        private readonly string? _system = Environment.GetEnvironmentVariable("GIT_CONFIG_SYSTEM");
        private readonly GitEnvironmentOptions _options = GitEnvironment.Options;
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        private readonly List<Window> _windows = [];
        private readonly GitRunner _native = new("/usr/bin/git");
        private string Temporary { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-general-diff-" + Guid.NewGuid().ToString("N"));
        internal string Configuration => System.IO.Path.Combine(Temporary, "configuration");
        internal string Path => System.IO.Path.Combine(Temporary, "repository");
        internal string File => System.IO.Path.Combine(Path, Literal);
        private string Log => System.IO.Path.Combine(Temporary, "argv.jsonl");
        internal GitRepository Repository { get; private set; } = null!;
        internal string Root { get; private set; } = "";
        internal string Head { get; private set; } = "";

        internal static string Content(string edit) => string.Concat(Enumerable.Range(1, 40).Select(number => number == 20 ? edit + "\n" : $"line {number:000}\n"));
        internal T Track<T>(T window) where T : Window { _windows.Add(window); return window; }
        internal static async Task<Fixture> CreateAsync()
        {
            if (!OperatingSystem.IsLinux()) { Assert.Ignore("The argument witness executes native Linux Git."); }
            Fixture f = new();
            try
            {
                Directory.CreateDirectory(f.Temporary); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", f.Configuration);
                Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", "/dev/null"); Environment.SetEnvironmentVariable("GIT_CONFIG_SYSTEM", "/dev/null");
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
                string wrapper = System.IO.Path.Combine(f.Temporary, "git-witness");
                string script = "#!/usr/bin/python3\nimport os,sys,json,fcntl\nwith open(" + JsonSerializer.Serialize(f.Log) + ", 'a') as log:\n"
                    + " fcntl.flock(log, fcntl.LOCK_EX)\n log.write(json.dumps(sys.argv[1:]) + '\\n')\n log.flush()\n"
                    + "os.execv('/usr/bin/git', ['git', *sys.argv[1:]])\n";
                await System.IO.File.WriteAllTextAsync(wrapper, script);
                System.IO.File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                GitEnvironment.Configure(new() { Executable = wrapper });
                new PreferencesStore().Save(new() { Theme = "Light" });
                GitRepository direct = await GitRepository.InitAsync(f.Path, new("main"), runner: f._native);
                foreach ((string key, string value) in new[] { ("user.name", "General Diff Tester"), ("user.email", "general-diff@example.invalid"),
                    ("commit.gpgsign", "false"), ("core.autocrlf", "false"), ("core.hooksPath", System.IO.Path.Combine(f.Temporary, "no-hooks")), ("diff.algorithm", "myers") })
                { await direct.SetConfigAsync(key, value); }
                await System.IO.File.WriteAllTextAsync(f.File, Content("original line")); await f.CommitAsync("Initial algorithm content");
                f.Root = (await f.NativeAsync("rev-parse", "HEAD")).StandardOutput.Trim();
                await System.IO.File.WriteAllTextAsync(f.File, Content("committed edit")); await f.CommitAsync("Changed algorithm content");
                f.Head = (await f.NativeAsync("rev-parse", "HEAD")).StandardOutput.Trim();
                f.Repository = await GitRepository.OpenAsync(f.Path, runner: new GitRunner(wrapper));
                f.ClearLog(); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        internal void PublishHistogram(bool value)
        {
            DesktopPreferences desktop = new PreferencesStore().Load(); GeneralSettingsSession session = new(desktop);
            session.Save(session.Initial with { UseHistogramDiffAlgorithm = value }, desktop);
        }
        internal void ClearLog() => System.IO.File.WriteAllText(Log, "");
        internal string[][] Commands() => System.IO.File.Exists(Log)
            ? System.IO.File.ReadAllLines(Log).Where(line => line.Length != 0).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray() : [];
        internal string[][] DiffCommands() => Commands().Where(command => Verb(command) is not null).ToArray();
        private static string? Verb(IEnumerable<string> command) => command.TakeWhile(argument => argument != "--")
            .FirstOrDefault(argument => argument is "diff" or "diff-tree" or "range-diff" or "show");
        internal string[][] AssertDiffCommands(bool histogram, string verb)
        {
            string[][] commands = DiffCommands(); Assert.That(commands, Is.Not.Empty, "A real diff command must execute.");
            Assert.That(commands.Select(Verb), Is.All.EqualTo(verb));
            foreach (string[] command in commands)
            {
                Assert.That(command.Count(argument => argument == "--histogram"), Is.EqualTo(histogram ? 1 : 0), JsonSerializer.Serialize(command));
                Assert.That(command, Does.Contain("--no-ext-diff").And.Contain("--no-textconv"));
            }
            return commands;
        }
        internal async Task<GitDiffDocument> ReadDocumentAsync(Func<Task<GitDiffDocument>> read, bool histogram, string verb, string fragment)
        {
            ClearLog(); GitDiffDocument document = await read();
            string[] canonical = AssertDiffCommands(histogram, verb).First(command => command.Contains("--no-color"));
            GitBinaryResult oracle = await RawBinaryAsync(canonical);
            Assert.That(oracle.Success || canonical.Contains("--no-index") && oracle.ExitCode == 1, Is.True, oracle.StandardError);
            Assert.That(document.PatchBytes.ToArray(), Is.EqualTo(oracle.StandardOutput), "Compare the actual patch bytes with native Git using the witnessed argv.");
            Assert.That(document.Patch, Does.Contain(fragment)); Assert.That(document.IsLosslessPatch, Is.True);
            return document;
        }
        internal Task<GitResult> RawAsync(IReadOnlyList<string> arguments) => _native.RunAsync(Path, arguments);
        internal Task<GitBinaryResult> RawBinaryAsync(IReadOnlyList<string> arguments) => _native.RunBinaryAsync(Path, arguments);
        internal async Task<GitResult> NativeAsync(params string[] arguments)
        {
            GitResult result = await RawAsync(arguments); Assert.That(result.Success, Is.True, result.StandardError); return result;
        }
        internal async Task CommitAsync(string message)
        { await NativeAsync("add", "--all"); await NativeAsync("commit", "-m", message); }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window window in _windows.AsEnumerable().Reverse()) { window.Close(); }
                await Until(() => _windows.All(window => !window.IsVisible));
            }
            finally
            {
                GitEnvironment.Configure(_options); CultureInfo.CurrentUICulture = _culture;
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", _global);
                Environment.SetEnvironmentVariable("GIT_CONFIG_SYSTEM", _system);
                if (Directory.Exists(Temporary)) { Directory.Delete(Temporary, recursive: true); }
            }
        }
    }
}

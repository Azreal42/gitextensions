using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class CleanupCommandLineTests
{
    [Test]
    public void CleanupTakesOnlyItsLiteralSecondArgumentAndNeverTurnsItIntoAFilterOrGlobalFlag()
    {
        foreach (string? argument in new string?[] { null, "", " ", "-", "--version", "--help", "--smoke-test", "folder with spaces", "file\"" })
        {
            string[] argv = argument is null ? ["cleanup"] : ["cleanup", argument, "unused filter", "--version", "--help"];
            DesktopStartupArguments parsed = DesktopStartupArguments.Parse(argv);
            Assert.Multiple(() =>
            {
                Assert.That(parsed.Mode, Is.EqualTo(DesktopStartupMode.Cleanup));
                Assert.That(parsed.RepositoryArgument, Is.EqualTo(argument));
                Assert.That(parsed.SmokeTest, Is.False);
            });
        }
        Assert.That(DesktopStartupArguments.IsCleanupInvocation(["Cleanup"]), Is.False, "The original command is case-sensitive.");
        Assert.That(DesktopStartupArguments.Parse(["./cleanup"]).Mode, Is.EqualTo(DesktopStartupMode.Browse));
    }

    [Test]
    public void ExistingBrowseAndSmokeArgumentsRemainSeparateFromTheCleanupCommand()
    {
        Assert.That(DesktopStartupArguments.Parse([]), Is.EqualTo(new DesktopStartupArguments(DesktopStartupMode.Browse)));
        Assert.That(DesktopStartupArguments.Parse(["repo", "ignored"]), Is.EqualTo(new DesktopStartupArguments(DesktopStartupMode.Browse, "repo")));
        Assert.That(DesktopStartupArguments.Parse(["--smoke-test", "repo"]), Is.EqualTo(new DesktopStartupArguments(DesktopStartupMode.Browse, "repo", true)));
        Assert.That(DesktopStartupArguments.Parse(["repo", "--smoke-test"]), Is.EqualTo(new DesktopStartupArguments(DesktopStartupMode.Browse, "repo", true)));
        Assert.That(DesktopStartupArguments.Parse(["--smoke-test"]), Is.EqualTo(new DesktopStartupArguments(DesktopStartupMode.Browse, SmokeTest: true)));
        Assert.Throws<ArgumentException>(() => DesktopStartupArguments.Parse(["-"]));
        Assert.Throws<ArgumentException>(() => DesktopStartupArguments.Parse(["--unknown"]));
    }

    [Test]
    public async Task ExplicitRepositoryAndNestedWorkingDirectoryResolveTheActualRootWithoutReadingRecentRepositories()
    {
        await using Fixture fixture = await Fixture.Create();
        Directory.CreateDirectory(fixture.File("deep/nested"));
        string before = fixture.Materials();
        DesktopStartupRequest explicitRoot = await fixture.Prepare(fixture.Repository.Path, fixture.Root);
        DesktopStartupRequest explicitNested = await fixture.Prepare(fixture.File("deep/nested"), fixture.Root);
        DesktopStartupRequest cwd = await fixture.Prepare(null, fixture.File("deep/nested"));
        foreach (DesktopStartupRequest request in new[] { explicitRoot, explicitNested, cwd })
        {
            Assert.That(request.Repository!.Path, Is.EqualTo(fixture.Repository.Path));
            Assert.That(request.Repository.Info!.RootPath, Is.EqualTo(fixture.Repository.Path));
        }
        Assert.That(fixture.Materials(), Is.EqualTo(before), "Discovery neither loads Browse nor writes MRU, Git index/config/refs or worktree bytes.");
    }

    [Test]
    public async Task ExistingAndMissingFilesUseTheirParentRepositoryAndNeverBecomeCleanupPathFilters()
    {
        await using Fixture fixture = await Fixture.Create();
        Directory.CreateDirectory(fixture.File("deep")); fixture.Write("deep/file with spaces.txt", "file\n");
        string before = fixture.Materials();
        foreach (string path in new[] { fixture.File("deep/file with spaces.txt"), fixture.File("deep/missing file.txt"), "deep/file with spaces.txt" })
        {
            DesktopStartupRequest request = await fixture.Prepare(path, fixture.Repository.Path);
            Assert.That(request.Repository!.Path, Is.EqualTo(fixture.Repository.Path));
            Assert.That(request.Arguments.RepositoryArgument, Is.EqualTo(path));
        }
        Assert.That(fixture.Materials(), Is.EqualTo(before));
        GitRepository other = await GitRepository.InitAsync(Path.Combine(fixture.Root, "other repository"));
        before = fixture.Materials();
        DesktopStartupRequest missingParents = await fixture.Prepare(Path.Combine(other.Path, "missing/directory/file.txt"), fixture.Repository.Path);
        Assert.That(missingParents.Repository!.Path, Is.EqualTo(other.Path), "Nonexistent parents are climbed before falling back to a different cwd repository.");
        Assert.That(fixture.Materials(), Is.EqualTo(before));
    }

    [Test]
    public async Task EmptyWhitespaceNonRepositoryAndFlagLookingPathsFallBackToTheInvokingCwd()
    {
        await using Fixture fixture = await Fixture.Create();
        string outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        string before = fixture.Materials();
        foreach (string? path in new string?[] { null, "", " \t ", "-", "--help", "--version", outside, Path.Combine(outside, "missing.txt") })
        {
            DesktopStartupRequest request = await fixture.Prepare(path, fixture.Repository.Path);
            Assert.That(request.Repository!.Path, Is.EqualTo(fixture.Repository.Path));
        }
        Assert.That(fixture.Materials(), Is.EqualTo(before));
    }

    [Test]
    public async Task NativeLiteralSpaceDashAndTrailingQuoteRepositoryNamesAreNotTrimmedOrReparsed()
    {
        await using Fixture fixture = await Fixture.Create(repositoryName: "- repository with spaces \"");
        string before = fixture.Materials();
        foreach (string path in new[] { fixture.Repository.Path, Path.GetFileName(fixture.Repository.Path) })
        {
            DesktopStartupRequest request = await fixture.Prepare(path, fixture.Root);
            Assert.That(request.Repository!.Path, Is.EqualTo(fixture.Repository.Path));
        }
        Assert.That(fixture.Materials(), Is.EqualTo(before), "Linux has no Windows command-line trailing-quote workaround.");
    }

    [Test]
    public async Task ActualDeniedDirectorySearchNeverFallsBackToAParentOrTheDifferentCwdRepository()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("This permission oracle requires Linux directory search permissions."); }
        await using Fixture fixture = await Fixture.Create();
        GitRepository target = await GitRepository.InitAsync(Path.Combine(fixture.Root, "target repository"));
        string barrier = Path.Combine(target.Path, "denied parent"), directory = Path.Combine(barrier, "selected directory");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "selected file.txt"); await File.WriteAllTextAsync(file, "target bytes\n");
        UnixFileMode originalMode = File.GetUnixFileMode(barrier);
        string before = fixture.Materials();
        try
        {
            File.SetUnixFileMode(barrier, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.That(Directory.Exists(directory), Is.False, "The old existence probe hides this real directory search denial.");
            Assert.Throws<UnauthorizedAccessException>(() => File.GetAttributes(directory), "Require a genuine access-denied oracle, not a privileged chmod bypass.");
            foreach (string path in new[] { directory, file, Path.Combine(directory, "missing/child.txt") })
            {
                Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await fixture.Prepare(path, fixture.Repository.Path),
                    "An inaccessible explicit path must not resolve to the target's readable parent or the different, valid cwd repository.");
            }
            Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await fixture.Prepare(null, directory),
                "An inaccessible invoking cwd also cannot select its readable ancestor repository.");
        }
        finally { File.SetUnixFileMode(barrier, originalMode); }
        Assert.That(fixture.Materials(), Is.EqualTo(before), "After restoring only the fixture permission barrier, every raw index/ref/config/object/worktree/profile byte and mode is unchanged.");
    }

    [Test]
    public async Task InvalidDiscoveryMalformedPathsAndPreCancellationCreateNoWindowOrGitEffects()
    {
        await using Fixture fixture = await Fixture.Create();
        string outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        string before = fixture.Materials();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Prepare(null, outside));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Prepare(Path.Combine(outside, "missing.txt"), outside));
        Assert.ThrowsAsync<ArgumentException>(async () => await fixture.Prepare("bad\0path", fixture.Repository.Path));
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await DesktopStartupDispatcher.PrepareAsync(
            DesktopStartupArguments.Parse(["cleanup", fixture.Repository.Path]), fixture.Root, cancellation.Token));
        Assert.Throws<InvalidOperationException>(() => App.CreateStartupWindow(new(new(DesktopStartupMode.Cleanup))));
        Assert.That(fixture.Materials(), Is.EqualTo(before));
    }

    [Test]
    public async Task ActualProgramHelpAndVersionOnlyDispatchGloballyOutsideCleanup()
    {
        await using Fixture fixture = await Fixture.Create();
        string outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        string before = fixture.Materials(), previousDirectory = Environment.CurrentDirectory;
        TextWriter previousOutput = Console.Out, previousError = Console.Error;
        try
        {
            Environment.CurrentDirectory = outside;
            string[][] invocations = [["cleanup", "--version"], ["cleanup", "--help"], ["cleanup", "bad\0path", "--version"], ["cleanup", "bad\0path", "--help"]];
            foreach (string[] arguments in invocations)
            {
                using StringWriter output = new(), error = new(); Console.SetOut(output); Console.SetError(error);
                Assert.That(GitExtensions.Linux.Desktop.Program.Main(arguments), Is.EqualTo(1));
                Assert.That(output.ToString(), Is.Empty, "Cleanup target/extra argv cannot print the global version or help and silently skip the dialog.");
                Assert.That(error.ToString(), Is.Not.Empty);
            }
            using StringWriter help = new(); Console.SetOut(help); Console.SetError(previousError);
            Assert.That(GitExtensions.Linux.Desktop.Program.Main(["--help"]), Is.Zero);
            Assert.That(help.ToString(), Does.Contain("cleanup [repository-directory-or-file]"));
            using StringWriter version = new(); Console.SetOut(version);
            Assert.That(GitExtensions.Linux.Desktop.Program.Main(["--version"]), Is.Zero);
            Assert.That(version.ToString(), Does.StartWith("Git Extensions "));
        }
        finally { Environment.CurrentDirectory = previousDirectory; Console.SetOut(previousOutput); Console.SetError(previousError); }
        Assert.That(fixture.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task AppOpensTheOriginalCleanupDirectlyInBothThemesWithoutBrowsePreviewOrAnyMutation()
    {
        await using Fixture fixture = await Fixture.Create();
        foreach ((string theme, ThemeVariant expected) in new[] { ("Light", ThemeVariant.Light), ("Dark", ThemeVariant.Dark) })
        {
            fixture.Theme(theme); string before = fixture.Materials();
            CleanupRepositoryDialog dialog = await fixture.Show(await fixture.Prepare(fixture.File("loose.txt"), fixture.Root));
            Assert.Multiple(() =>
            {
                Assert.That(dialog.Owner, Is.Null);
                Assert.That(dialog.WindowStartupLocation, Is.EqualTo(WindowStartupLocation.CenterScreen));
                Assert.That(dialog.ActualThemeVariant, Is.EqualTo(expected));
                Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
                Assert.That(dialog.LastResult, Is.Null, "Only initialization runs at startup, never Preview/Cleanup.");
                Assert.That(dialog.HasRepositoryChanges, Is.False);
                Assert.That(Field<TextBox>(dialog, "CleanupLog").Text ?? "", Is.Empty);
                Assert.That(Field<TextBox>(dialog, "CleanupIncludePaths").Text, Is.Empty);
                Assert.That(Field<TextBox>(dialog, "CleanupExcludePaths").Text, Is.Empty);
                Assert.That(Field<CheckBox>(dialog, "CleanupIncludeFilter").IsChecked, Is.False);
                Assert.That(Field<CheckBox>(dialog, "CleanupExcludeFilter").IsChecked, Is.False);
                Assert.That(dialog.ReadOptions().Mode, Is.EqualTo(CleanMode.All));
                Assert.That(dialog.ReadOptions().RemoveDirectories, Is.True);
                Assert.That(dialog.ReadOptions().CleanSubmodules, Is.False);
                Assert.That(dialog.ReadOptions().IncludePaths, Is.Empty);
                Assert.That(dialog.OwnedWindows, Is.Empty);
            });
            await Pointer(Field<Button>(dialog, "CloseCleanup"));
            await Until(() => !dialog.IsVisible);
            Assert.That(fixture.Materials(), Is.EqualTo(before));
        }
    }

    [AvaloniaTest]
    public async Task StandaloneEscapeClosesFromTheEditablePathFieldWithoutCleaningOrSavingPreferences()
    {
        await using Fixture fixture = await Fixture.Create(); string before = fixture.Materials();
        CleanupRepositoryDialog dialog = await fixture.Show(await fixture.Prepare(fixture.Repository.Path, fixture.Root));
        CheckBox filter = Field<CheckBox>(dialog, "CleanupIncludeFilter"); await Pointer(filter);
        TextBox paths = Field<TextBox>(dialog, "CleanupIncludePaths"); paths.Text = "*.tmp"; Assert.That(paths.Focus(), Is.True); Pump(dialog);
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        // The window closes on KeyDown. Headless cannot deliver KeyUp to a detached native root.
        await Until(() => !dialog.IsVisible);
        Assert.That(dialog.LastResult, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.False);
        Assert.That(fixture.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task StandaloneCleanupQuestionNoAndEscapeKeepTheActualRepositoryAndEmptyLogIntact()
    {
        await using Fixture fixture = await Fixture.Create(); string before = fixture.Materials();
        CleanupRepositoryDialog dialog = await fixture.Show(await fixture.Prepare(fixture.Repository.Path, fixture.Root));
        foreach (bool escape in new[] { false, true })
        {
            await Pointer(Field<Button>(dialog, "ExecuteCleanup"));
            await Until(() => dialog.OwnedWindows.Any(window => window.Name == "CleanupConfirmation" && window.IsVisible));
            Window question = dialog.OwnedWindows.Single(window => window.Name == "CleanupConfirmation");
            Assert.That(Field<Button>(question, "ConfirmCleanup").IsDefault, Is.True);
            if (escape) { question.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); dialog.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); }
            else { await Pointer(Field<Button>(question, "CancelCleanup")); }
            await Until(() => !dialog.IsBusy && dialog.IsEnabled && !question.IsVisible);
            Assert.That(dialog.LastResult, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.False);
            Assert.That(Field<TextBox>(dialog, "CleanupLog").Text ?? "", Is.Empty);
            Assert.That(fixture.Materials(), Is.EqualTo(before));
        }
        await Pointer(Field<Button>(dialog, "CloseCleanup"));
    }

    [AvaloniaTest]
    public async Task BareStartupShowsTheExistingWorktreeGuardAndRemainsClosableWithoutAnyCommands()
    {
        await using Fixture fixture = await Fixture.Create();
        GitRepository bare = await GitRepository.InitAsync(Path.Combine(fixture.Root, "bare.git"), bare: true);
        string before = fixture.Materials();
        CleanupRepositoryDialog dialog = await fixture.Show(await fixture.Prepare(bare.Path, fixture.Root));
        Assert.That(dialog.LastError, Is.Not.Empty); Assert.That(dialog.LastException, Is.TypeOf<InvalidOperationException>());
        Assert.That(Field<Button>(dialog, "PreviewCleanup").IsEnabled, Is.False);
        Assert.That(Field<Button>(dialog, "ExecuteCleanup").IsEnabled, Is.False);
        Assert.That(dialog.LastResult, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.False);
        await Pointer(Field<Button>(dialog, "CloseCleanup")); await Until(() => !dialog.IsVisible);
        Assert.That(fixture.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task UnbornStartupUsesTheRealWorktreeWithoutRequiringOrCreatingHeadOrAnIndex()
    {
        await using Fixture fixture = await Fixture.Create(seed: false); string before = fixture.Materials();
        Assert.That(File.Exists(fixture.File(".git/index")), Is.False);
        CleanupRepositoryDialog dialog = await fixture.Show(await fixture.Prepare(null, fixture.Repository.Path));
        Assert.That(dialog.LastError, Is.Null); Assert.That(Field<Button>(dialog, "PreviewCleanup").IsEnabled, Is.True);
        Assert.That(Field<Button>(dialog, "ExecuteCleanup").IsEnabled, Is.True); Assert.That(dialog.LastResult, Is.Null);
        await Pointer(Field<Button>(dialog, "CloseCleanup")); await Until(() => !dialog.IsVisible);
        Assert.That(File.Exists(fixture.File(".git/index")), Is.False);
        Assert.That(fixture.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ClosingStandaloneDuringInitializationCancelsDiscoveryAndItsDescendantWithoutCleanup()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The private process fixture uses Linux permissions and procfs."); }
        await using Fixture fixture = await Fixture.Create();
        string script = Path.Combine(fixture.Root, "delayed-git"), marker = Path.Combine(fixture.Root, "discovery-pids");
        string realGit = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(path => Path.Combine(path, "git")).First(File.Exists);
        static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
        await File.WriteAllTextAsync(script, "#!/bin/sh\ncase \"$*\" in\n*rev-parse*) sleep 60 & echo \"$$ $!\" > " + Quote(marker)
            + "; wait;;\n*) exec " + Quote(realGit) + " \"$@\";;\nesac\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string before = fixture.Materials(omitPath: marker);
        CleanupRepositoryDialog dialog = (CleanupRepositoryDialog)App.CreateStartupWindow(new(new(DesktopStartupMode.Cleanup, fixture.Repository.Path),
            new GitRepository(fixture.Repository.Path, new GitRunner(script))));
        fixture.Windows.Add(dialog); dialog.Show();
        await Until(() => File.Exists(marker) && File.ReadAllText(marker).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 2 && dialog.IsBusy);
        int[] pids = (await File.ReadAllTextAsync(marker)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        dialog.Close();
        await Until(() => !dialog.IsBusy && !dialog.IsVisible && pids.All(pid => !Running(pid)));
        Assert.That(dialog.LastResult, Is.Null); Assert.That(dialog.HasRepositoryChanges, Is.False);
        Assert.That(fixture.Materials(omitPath: marker), Is.EqualTo(before), "Only the fixture's external pid witness is new; all Git/worktree/profile materials remain exact.");
    }

    private static bool Running(int pid)
    {
        string stat = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat";
        try { string text = File.ReadAllText(stat); int end = text.LastIndexOf(')'); return end < 0 || end + 2 >= text.Length || text[end + 2] is not ('Z' or 'X' or 'x'); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static T Field<T>(Window dialog, string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(12)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "Standalone cleanup did not reach its bounded state.");
    }
    private static async Task Pointer(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; Stopwatch timer = Stopwatch.StartNew(); Point point = default; bool ready = false; string? hit = null;
        do
        {
            Pump(top); point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
            top.MouseMove(point); Visual? target = top.InputHitTest(point) as Visual;
            hit = string.Join(" > ", target?.GetSelfAndVisualAncestors().Select(visual => visual.GetType().Name + "#" + (visual as Control)?.Name) ?? []);
            ready = control.IsEffectivelyEnabled && control.IsEffectivelyVisible && target?.GetSelfAndVisualAncestors().Contains(control) == true;
            if (!ready) { await Task.Delay(10); }
        } while (!ready && timer.Elapsed < TimeSpan.FromSeconds(8));
        Assert.That(ready, Is.True, control.Name + ": " + hit);
        top.MouseDown(point, MouseButton.Left); top.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Dictionary<string, string?> _environment = [];
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-cleanup-cli-" + Guid.NewGuid().ToString("N"));
        internal GitRepository Repository { get; private set; } = null!;
        internal List<Window> Windows { get; } = [];
        private Fixture()
        {
            Directory.CreateDirectory(Root);
            foreach ((string name, string value) in new[] { ("XDG_CONFIG_HOME", Path.Combine(Root, "profile")), ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_OPTIONAL_LOCKS", "0") })
            { _environment[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
            Theme("Light");
        }
        internal string File(string relative) => Path.Combine(Repository.Path, relative);
        internal void Write(string relative, string text) { string path = File(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, text); }
        internal void Theme(string theme) => new PreferencesStore().Save(new() { Theme = theme, RecentRepositories = [Path.Combine(Root, "recent-must-not-be-opened")] });
        internal static async Task<Fixture> Create(bool seed = true, string repositoryName = "repository")
        {
            Fixture fixture = new();
            try
            {
                fixture.Repository = await GitRepository.InitAsync(Path.Combine(fixture.Root, repositoryName));
                await fixture.Git("config", "user.name", "Cleanup CLI Test"); await fixture.Git("config", "user.email", "cleanup-cli@example.invalid");
                await fixture.Git("config", "commit.gpgsign", "false"); await fixture.Git("config", "tag.gpgsign", "false"); await fixture.Git("config", "core.hooksPath", "/dev/null");
                if (seed)
                {
                    fixture.Write("tracked.txt", "tracked\n"); fixture.Write(".gitignore", "*.ignored\n"); await fixture.Git("add", "."); await fixture.Git("commit", "-m", "Initial");
                    fixture.Write("staged.txt", "staged\n"); await fixture.Git("add", "staged.txt"); fixture.Write("tracked.txt", "dirty tracked\n");
                }
                fixture.Write("loose.txt", "untracked\n"); fixture.Write("ignored.ignored", "ignored\n"); return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        internal async Task Git(params string[] arguments)
        { GitResult result = await new GitRunner().RunAsync(Repository.Path, arguments); Assert.That(result.Success, Is.True, result.StandardError); }
        internal Task<DesktopStartupRequest> Prepare(string? target, string cwd)
            => DesktopStartupDispatcher.PrepareAsync(DesktopStartupArguments.Parse(target is null ? ["cleanup"] : ["cleanup", target]), cwd);
        internal async Task<CleanupRepositoryDialog> Show(DesktopStartupRequest request)
        {
            Window window = App.CreateStartupWindow(request); Assert.That(window, Is.TypeOf<CleanupRepositoryDialog>());
            CleanupRepositoryDialog dialog = (CleanupRepositoryDialog)window; Windows.Add(dialog); dialog.Show(); Pump(dialog);
            await Until(() => !dialog.IsBusy && (Field<Button>(dialog, "PreviewCleanup").IsEnabled || dialog.LastError is not null));
            return dialog;
        }
        internal string Materials(string? omitPath = null)
        {
            SortedDictionary<string, string> entries = new(StringComparer.Ordinal);
            void Walk(string directory)
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (path == omitPath) { continue; }
                    FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                    string relative = Path.GetRelativePath(Root, path);
                    if (info.LinkTarget is string target) { entries[relative] = "link:" + target; continue; }
                    string mode = OperatingSystem.IsWindows() ? "" : ((int)System.IO.File.GetUnixFileMode(path)).ToString(CultureInfo.InvariantCulture);
                    if (info is DirectoryInfo) { entries[relative] = "dir:" + mode; Walk(path); }
                    else { entries[relative] = mode + ":" + Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path))); }
                }
            }
            Walk(Root); return JsonSerializer.Serialize(entries);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (Window window in Windows.AsEnumerable().Reverse())
            {
                foreach (Window owned in window.OwnedWindows.ToArray()) { owned.Close(); }
                window.Close();
            }
            if (Windows.Count != 0)
            {
                await Until(() => Windows.All(window => !window.IsVisible && (window is not RepositoryDialog dialog || !dialog.IsBusy)));
                Dispatcher.UIThread.RunJobs();
            }
            foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); }
            Directory.Delete(Root, recursive: true);
        }
    }
}

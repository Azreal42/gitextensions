using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands.Git;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed partial class GitProcessDialogTests
{
    [AvaloniaTest]
    public async Task DefaultSettingsKeepActualGitOutputOpenUntilOkWithoutChangingTheRepository()
    {
        await using Fixture f = await Fixture.Create(); string before = f.Materials(); Window owner = f.Owner();
        Assert.That(new ProcessDialogPreferencesStore().Load().CloseProcessDialog, Is.False);
        Task<GitProcessDialogResult> task = f.Process(owner, true, ["log", "-1", "--format=%H"]); GitProcessDialog phase = await Ready(owner);
        Assert.That(task.IsCompleted, Is.False); Assert.That(Field<CheckBox>(phase, "KeepDialogOpen").IsChecked, Is.True);
        Assert.That(Field<CheckBox>(phase, "KeepDialogOpen").IsVisible, Is.True); Assert.That(Field<Button>(phase, "Abort").IsEnabled, Is.False);
        Assert.That(Field<Button>(phase, "Ok").IsEnabled, Is.True); Assert.That(Field<Button>(phase, "Ok").IsFocused, Is.True);
        Assert.That(Field<ProgressBar>(phase, "ProgressBar").IsVisible, Is.False); Assert.That(phase.Icon, Is.Not.Null);
        GitResult native = phase.Result!;
        Assert.That(Field<TextBox>(phase, "ProcessOutput").Text, Is.EqualTo(phase.CommandDescription + "\n" + native.StandardOutput + native.StandardError + "Done"));
        Pointer(Field<Button>(phase, "Ok")); GitProcessDialogResult outcome = await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(outcome.Success, Is.True); Assert.That(outcome.Result!.StandardOutput.Trim(), Is.EqualTo(f.Hashes[^1])); Assert.That(f.Materials(), Is.EqualTo(before));
        string file = new ProcessDialogPreferencesStore().FilePath; File.WriteAllText(file, "null"); Assert.That(new ProcessDialogPreferencesStore().Load().CloseProcessDialog, Is.False);
        File.WriteAllText(file, "{bad json"); Assert.That(new ProcessDialogPreferencesStore().Load().CloseProcessDialog, Is.False);
    }

    [AvaloniaTest]
    public async Task SuccessAutoCloseIsDurableButUseDialogSettingsFalseAlwaysWaitsForAcknowledgment()
    {
        await using Fixture f = await Fixture.Create(); Window owner = f.Owner(); new ProcessDialogPreferencesStore().Save(new(true));
        GitProcessDialogResult automatic = await f.Process(owner, true, ["log", "-1", "--format=%H"]).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(automatic.Success, Is.True); Assert.That(owner.OwnedWindows, Is.Empty);
        Task<GitProcessDialogResult> manual = f.Process(owner, false, ["log", "-1", "--format=%H"]); GitProcessDialog phase = await Ready(owner);
        Assert.That(phase.UseDialogSettings, Is.False); Assert.That(Field<CheckBox>(phase, "KeepDialogOpen").IsVisible, Is.False);
        Assert.That(manual.IsCompleted, Is.False); Assert.That(new ProcessDialogPreferencesStore().Load().CloseProcessDialog, Is.True);
        Press(phase, PhysicalKey.Enter, owner); Assert.That((await manual.WaitAsync(TimeSpan.FromSeconds(15))).Success, Is.True);
    }

    [AvaloniaTest]
    public async Task UncheckingKeepAfterSuccessClosesImmediatelyAndControlsTheNextProcess()
    {
        await using Fixture f = await Fixture.Create(); Window owner = f.Owner();
        Task<GitProcessDialogResult> task = f.Process(owner, true, ["log", "-1", "--format=%s"]); GitProcessDialog phase = await Ready(owner);
        Pointer(Field<CheckBox>(phase, "KeepDialogOpen")); GitProcessDialogResult outcome = await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(outcome.Success, Is.True); Assert.That(phase.IsVisible, Is.False); Assert.That(new ProcessDialogPreferencesStore().Load().CloseProcessDialog, Is.True);
        Assert.That((await f.Process(owner, true, ["log", "-1", "--format=%s"]).WaitAsync(TimeSpan.FromSeconds(15))).Success, Is.True);
    }

    [AvaloniaTest]
    public async Task ARealNativeErrorNeverAutoClosesOrOpensADuplicateErrorWindow()
    {
        await using Fixture f = await Fixture.Create(); Window owner = f.Owner(); await f.Repository.StartBisectAsync(); string before = f.Materials();
        new ProcessDialogPreferencesStore().Save(new(true));
        Task<GitProcessDialogResult> task = GitProcessDialog.ShowAsync(owner, f.Repository.Path, "git bisect good missing-ref", true,
            (token, output) => f.Repository.MarkBisectAsync(GitBisectOption.Good, "missing-ref", token, output));
        GitProcessDialog phase = await Ready(owner); Assert.That(phase.Error, Is.TypeOf<GitCommandException>()); GitResult native = phase.Result!; Assert.That(native.Success, Is.False);
        Assert.That(task.IsCompleted, Is.False); Assert.That(owner.OwnedWindows.Count(), Is.EqualTo(1)); Assert.That(phase.OwnedWindows, Is.Empty);
        Assert.That(Field<TextBox>(phase, "ProcessOutput").Text, Is.EqualTo(phase.CommandDescription + "\n" + native.StandardOutput + native.StandardError + "Done"));
        Assert.That(native.StandardError, Is.Not.Empty); Assert.That(f.Materials(), Is.EqualTo(before));
        Pointer(Field<Button>(phase, "Ok")); GitProcessDialogResult result = await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(result.Error, Is.SameAs(phase.Error)); Assert.That(result.Success, Is.False); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ActualGitBytesStreamWhileRunningWithUnicodeCarriageReturnProgressAndNoReplacement()
    {
        await using Fixture f = await Fixture.Create(); Window owner = f.Owner();
        byte[] bytes = [.. System.Text.Encoding.UTF8.GetBytes(new string('x', 4095) + "éλ😀\r\nNative Git blob progress:  12%\rNative Git blob progress:  42%\r"), 0xFF, .. System.Text.Encoding.UTF8.GetBytes("\u001B[K end\n")];
        GitBinaryResult saved = await new GitRunner().RunBinaryAsync(f.Repository.Path, ["hash-object", "-w", "--stdin"], standardInput: bytes);
        Assert.That(saved.Success, Is.True); string hash = System.Text.Encoding.UTF8.GetString(saved.StandardOutput).Trim(); string wrapper = f.Gate("cat-file");
        Task<GitProcessDialogResult> task = GitProcessDialog.ShowAsync(owner, f.Repository.Path, "git cat-file blob " + hash, true,
            (token, output) => new GitRunner(wrapper).RunAsync(f.Repository.Path, ["cat-file", "blob", hash], token, outputReceived: output));
        await Until(() => File.Exists(wrapper + ".pids")); GitProcessDialog phase = owner.OwnedWindows.OfType<GitProcessDialog>().Single();
        await Until(() => phase.StandardOutputBytes.Span.SequenceEqual(bytes));
        Assert.That(phase.IsRunning, Is.True); Assert.That(task.IsCompleted, Is.False); Assert.That(Field<Button>(phase, "Ok").IsEnabled, Is.False); Assert.That(Field<Button>(phase, "Abort").IsEnabled, Is.True);
        Assert.That(Field<TextBox>(phase, "ProcessOutput").Text, Does.StartWith(phase.CommandDescription + "\n").And.Contain("éλ😀\n").And.Contain("\\xFF").And.Not.Contain("�").And.Not.Contain("\r").And.Not.Contain("\u001B[K").And.Not.Contain("Native Git blob progress:"));
        Assert.That(phase.Title, Is.EqualTo("Native Git blob progress:  42%")); Assert.That(Field<ProgressBar>(phase, "ProgressBar").Value, Is.EqualTo(42));
        Assert.That(Field<ProgressBar>(phase, "ProgressBar").IsIndeterminate, Is.False); Capture(phase, "bisect-process-live-native-bytes.png");
        Pointer(Field<CheckBox>(phase, "KeepDialogOpen")); Assert.That(task.IsCompleted, Is.False, "Changing auto-close cannot pretend the live process has ended.");
        Pointer(Field<CheckBox>(phase, "KeepDialogOpen"));
        File.WriteAllText(wrapper + ".release", "release\n"); Assert.That(await Ready(owner), Is.SameAs(phase));
        GitResult native = phase.Result!;
        Assert.That(task.IsCompleted, Is.False); Assert.That(phase.IsVisible, Is.True);
        Assert.That(Field<TextBox>(phase, "ProcessOutput").Text, Is.EqualTo(phase.CommandDescription + "\n" + new string('x', 4095) + "éλ😀\n\\xFF end\nDone"));
        Assert.That(native.StandardOutputBytes, Is.EqualTo(bytes)); Assert.That(native.StandardOutput, Does.Contain("éλ😀").And.Contain("\\xFF").And.Contain("\r").And.Contain("\u001B[K").And.Not.Contain("�"));
        Assert.That(native.Warnings, Is.Not.Empty);
        Assert.That(Field<TextBlock>(phase, "ProcessNotice").IsVisible, Is.True);
        Assert.That(Field<TextBlock>(phase, "ProcessNotice").Text, Is.EqualTo(string.Join(Environment.NewLine, native.Warnings)));
        Capture(phase, "bisect-process-finished-native-bytes.png");
        Pointer(Field<CheckBox>(phase, "KeepDialogOpen")); GitProcessDialogResult outcome = await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(outcome.Success, Is.True); Assert.That(outcome.Result, Is.SameAs(native));
    }

    [AvaloniaTest]
    public async Task PreferenceWriteFailureAfterCompletionRetainsGitWarningsAndSuccessfulRetryClearsTheStaleNotice()
    {
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            await using Fixture f = await Fixture.Create(); Window owner = f.Owner(theme);
            byte[] bytes = [.. System.Text.Encoding.UTF8.GetBytes("native warning bytes\n"), 0xFF];
            GitBinaryResult saved = await new GitRunner().RunBinaryAsync(f.Repository.Path, ["hash-object", "-w", "--stdin"], standardInput: bytes);
            Assert.That(saved.Success, Is.True); string hash = System.Text.Encoding.UTF8.GetString(saved.StandardOutput).Trim();
            string directory = f.Root;
            for (int segment = 0; segment < 6; segment++) { directory = Path.Combine(directory, "private preferences " + segment + " " + new string('x', 100)); }
            string displaced = directory + ".saved";
            ProcessDialogPreferencesStore store = new(Path.Combine(directory, "process.json")); store.Save(new(false));
            string materials = f.Materials();
            Task<GitProcessDialogResult> task = GitProcessDialog.ShowAsync(owner, f.Repository.Path, "git cat-file blob " + hash, true,
                (token, output) => new GitRunner().RunAsync(f.Repository.Path, ["cat-file", "blob", hash], token, outputReceived: output), preferencesStore: store);
            GitProcessDialog phase = await Ready(owner); GitResult native = phase.Result!; phase.Width = 500; phase.Height = 200; Pump(phase);
            Assert.That(native.Success, Is.True); Assert.That(native.StandardOutputBytes, Is.EqualTo(bytes)); Assert.That(native.Warnings, Is.Not.Empty);
            string warnings = string.Join(Environment.NewLine, native.Warnings); TextBlock notice = Field<TextBlock>(phase, "ProcessNotice");
            Assert.That(notice.Text, Is.EqualTo(warnings));
            // A real file obstructs the private directory, producing an IO error even for a privileged test runner.
            Directory.Move(directory, displaced); File.WriteAllText(directory, "private preference write barrier\n");
            try
            {
                Pointer(Field<CheckBox>(phase, "KeepDialogOpen")); Pump(phase);
                Assert.That(task.IsCompleted, Is.False); Assert.That(phase.IsVisible, Is.True); Assert.That(notice.IsVisible, Is.True);
                Assert.That(notice.Text, Does.Contain(warnings)); Assert.That(notice.Text, Is.Not.EqualTo(warnings));
                Assert.That(phase.Result, Is.SameAs(native)); Assert.That(phase.Outcome!.Success, Is.True);
                ScrollViewer scroll = Field<ScrollViewer>(phase, "ProcessNoticeScroll"); TextBox output = Field<TextBox>(phase, "ProcessOutput");
                Rect noticeBounds = new(scroll.TranslatePoint(default, phase)!.Value, scroll.Bounds.Size);
                Rect outputBounds = new(output.TranslatePoint(default, phase)!.Value, output.Bounds.Size);
                Assert.That(noticeBounds.Height, Is.GreaterThan(0)); Assert.That(outputBounds.Height, Is.GreaterThan(0));
                Assert.That(noticeBounds.Bottom, Is.LessThanOrEqualTo(outputBounds.Top));
                foreach (string action in new[] { "KeepDialogOpen", "Ok", "Abort" })
                {
                    Control control = Field<Control>(phase, action); Rect bounds = new(control.TranslatePoint(default, phase)!.Value, control.Bounds.Size);
                    Assert.That(control.IsEffectivelyVisible, Is.True); Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(outputBounds.Bottom));
                    Assert.That(new Rect(default, phase.ClientSize).Contains(bounds), Is.True);
                }
                Assert.That(scroll.Extent.Height, Is.GreaterThan(scroll.Viewport.Height)); scroll.ScrollToEnd(); Pump(phase);
                Assert.That(scroll.Offset.Y, Is.GreaterThan(0)); Capture(phase, "bisect-process-preference-error-minimum-" + theme + ".png");
            }
            finally { File.Delete(directory); Directory.Move(displaced, directory); }
            Pointer(Field<CheckBox>(phase, "KeepDialogOpen")); Pump(phase);
            Assert.That(store.Load().CloseProcessDialog, Is.False); Assert.That(task.IsCompleted, Is.False);
            Assert.That(notice.IsVisible, Is.True); Assert.That(notice.Text, Is.EqualTo(warnings));
            Assert.That(Field<TextBox>(phase, "ProcessOutput").Text, Is.EqualTo(phase.CommandDescription + "\n" + "native warning bytes\n\\xFFDone"));
            Assert.That(phase.StandardOutputBytes.ToArray(), Is.EqualTo(bytes)); Assert.That(f.Materials(), Is.EqualTo(materials));
            Capture(phase, "bisect-process-preference-recovered-minimum-" + theme + ".png");
            Pointer(Field<Button>(phase, "Ok")); GitProcessDialogResult outcome = await task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.That(outcome.Success, Is.True); Assert.That(outcome.Result, Is.SameAs(native));
        }
    }

    [AvaloniaTest]
    public async Task AbortAndEscapeStopAfterActualGoodWithoutStartingBadOrLeakingDescendants()
    {
        foreach (bool escape in new[] { false, true })
        {
            await using Fixture f = await Fixture.Create(); string wrapper = f.Gate("bisect good"); GitRepository gated = new(f.Repository.Path, new GitRunner(wrapper));
            BisectDialog dialog = await f.Show(new BisectDialog(gated, [f.Hashes[0], f.Hashes[^1]])); Pointer(Field<Button>(dialog, "Start"));
            GitProcessDialog start = await Ready(dialog); Assert.That(start.UseDialogSettings, Is.True); Pointer(Field<Button>(start, "Ok"));
            await Until(() => dialog.OwnedWindows.Any(window => window.Name == "BisectRangeConfirmation")); Window range = dialog.OwnedWindows.Single(); Pointer(Field<Button>(range, "ConfirmBisectRange"));
            await Until(() => File.Exists(wrapper + ".pids")); GitProcessDialog good = dialog.OwnedWindows.OfType<GitProcessDialog>().Single();
            await Until(() => good.StandardOutputBytes.Length > 0); int[] pids = File.ReadAllText(wrapper + ".pids").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            Assert.That(good.IsRunning, Is.True); Assert.That(dialog.HasRepositoryChanges, Is.True);
            if (escape) { Field<TextBox>(good, "ProcessOutput").Focus(); Press(good, PhysicalKey.Escape, dialog); }
            else { Pointer(Field<Button>(good, "Abort")); }
            await Until(() => !dialog.IsVisible && !dialog.IsBusy && pids.All(pid => !Running(pid)));
            Assert.That(good.Outcome!.WasCancelled, Is.True); Assert.That(good.Result, Is.Null, "An interrupted wrapper has no observed exit code.");
            Assert.That(good.StandardOutputBytes.Length, Is.GreaterThan(0)); GitBisectState state = await f.Repository.GetBisectStateAsync();
            Assert.That(state.GoodRevisions, Is.EqualTo(new[] { f.Hashes[0] })); Assert.That(state.BadRevision, Is.Null); Assert.That(await f.Head(), Is.EqualTo(f.Hashes[^1]));
        }
    }

    [AvaloniaTest]
    public async Task OwnerClosureWaitsForItsProcessAndKeepsTheRealPartialGitEffects()
    {
        await using Fixture f = await Fixture.Create(); Window owner = f.Owner(); await f.Repository.StartBisectAsync(); string wrapper = f.Gate("bisect good");
        Task<GitProcessDialogResult> task = GitProcessDialog.ShowAsync(owner, f.Repository.Path, "git bisect good " + f.Hashes[0], true,
            (token, output) => new GitRepository(f.Repository.Path, new GitRunner(wrapper)).MarkBisectAsync(GitBisectOption.Good, f.Hashes[0], token, output));
        await Until(() => File.Exists(wrapper + ".pids")); int[] pids = File.ReadAllText(wrapper + ".pids").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        owner.Close(); GitProcessDialogResult result = await task.WaitAsync(TimeSpan.FromSeconds(15)); await Until(() => pids.All(pid => !Running(pid)));
        Assert.That(result.WasCancelled, Is.True); Assert.That(result.CommandStarted, Is.True); Assert.That(result.Result, Is.Null);
        Assert.That((await f.Repository.GetBisectStateAsync()).GoodRevisions, Is.EqualTo(new[] { f.Hashes[0] })); Assert.That((await f.Repository.GetBisectStateAsync()).BadRevision, Is.Null);
    }

    [AvaloniaTest]
    public async Task EveryRangePhaseUsesThePreferenceAndClassificationAndStopIgnoreIt()
    {
        await using Fixture f = await Fixture.Create(); BisectDialog dialog = await f.Show(new BisectDialog(f.Repository, [f.Hashes[0], f.Hashes[^1]]));
        Pointer(Field<Button>(dialog, "Start")); GitProcessDialog start = await Ready(dialog); Assert.That(start.CommandDescription, Is.EqualTo("git bisect start")); Assert.That(start.UseDialogSettings, Is.True); Pointer(Field<Button>(start, "Ok"));
        await Until(() => dialog.OwnedWindows.Any(window => window.Name == "BisectRangeConfirmation")); Window range = dialog.OwnedWindows.Single(); Pointer(Field<Button>(range, "ConfirmBisectRange"));
        GitProcessDialog good = await Ready(dialog); Assert.That(good.CommandDescription, Is.EqualTo("git bisect good " + f.Hashes[0])); Assert.That(good.UseDialogSettings, Is.True); Pointer(Field<Button>(good, "Ok"));
        GitProcessDialog bad = await Ready(dialog); Assert.That(bad.CommandDescription, Is.EqualTo("git bisect bad " + f.Hashes[^1])); Assert.That(bad.UseDialogSettings, Is.True); Pointer(Field<Button>(bad, "Ok"));
        await Until(() => !dialog.IsVisible); Assert.That(dialog.CommandResults.Count, Is.EqualTo(3)); GitBisectState state = await f.Repository.GetBisectStateAsync();
        Assert.That(state.GoodRevisions, Is.EqualTo(new[] { f.Hashes[0] })); Assert.That(state.BadRevision, Is.EqualTo(f.Hashes[^1]));
        new ProcessDialogPreferencesStore().Save(new(true));
        foreach (string action in new[] { "Good", "Stop" })
        {
            dialog = await f.Show(new BisectDialog(f.Repository)); Pointer(Field<Button>(dialog, action)); GitProcessDialog phase = await Ready(dialog);
            Assert.That(phase.UseDialogSettings, Is.False); Assert.That(Field<CheckBox>(phase, "KeepDialogOpen").IsVisible, Is.False); Assert.That(dialog.IsVisible, Is.True);
            Pointer(Field<Button>(phase, "Ok")); await Until(() => !dialog.IsVisible); Assert.That(new ProcessDialogPreferencesStore().Load().CloseProcessDialog, Is.True);
        }
        Assert.That((await f.Repository.GetBisectStateAsync()).IsActive, Is.False); Assert.That(await f.Head(), Is.EqualTo(f.Hashes[^1]));
    }

    [AvaloniaTest]
    public async Task OriginalProcessLabelsAndTheRealConsolePaletteRenderInFrenchInBothThemes()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = new("fr-FR");
        try
        {
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                await using Fixture f = await Fixture.Create(); Window owner = f.Owner(theme); Task<GitProcessDialogResult> task = f.Process(owner, true, ["log", "-1", "--format=%H%n%s"]); GitProcessDialog phase = await Ready(owner);
                Assert.That(phase.Title, Does.StartWith("Processus (")); Assert.That(phase.ActualThemeVariant, Is.EqualTo(theme));
                Assert.That(((AccessText)Field<CheckBox>(phase, "KeepDialogOpen").Content!).Text, Is.EqualTo("_Garder la boîte de dialogue ouverte"));
                Assert.That(((AccessText)Field<Button>(phase, "Abort").Content!).Text, Is.EqualTo("_Abandonner"));
                Assert.That(Field<Button>(phase, "Abort").IsEnabled, Is.False); Assert.That(Field<Button>(phase, "Ok").IsEnabled, Is.True);
                Assert.That(Pixel(phase, new Point(3, 3)), Is.EqualTo(theme == ThemeVariant.Dark ? Color.Parse("#323232") : Colors.White));
                Assert.That(((ISolidColorBrush)Field<TextBox>(phase, "ProcessOutput").Foreground!).Color, Is.EqualTo(theme == ThemeVariant.Dark ? Color.Parse("#f0f0f0") : Colors.Black));
                Assert.That(Field<TextBox>(phase, "ProcessOutput").Text, Does.Contain(f.Hashes[^1])); Capture(phase, "original-bisect-process-fr-" + theme + ".png");
                Pointer(Field<Button>(phase, "Ok")); Assert.That((await task.WaitAsync(TimeSpan.FromSeconds(15))).Success, Is.True);
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private static T Field<T>(TopLevel window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static async Task Until(Func<bool> condition)
    { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True, "Native process/dialog did not settle within fifteen seconds."); }
    private static async Task<GitProcessDialog> Ready(Window owner)
    { await Until(() => owner.OwnedWindows.OfType<GitProcessDialog>().Any(window => window.IsVisible && window.Outcome is not null)); GitProcessDialog phase = owner.OwnedWindows.OfType<GitProcessDialog>().Single(window => window.IsVisible); Pump(phase); return phase; }
    private static void Pointer(Control control) => PushProcessTestDriver.Pointer(control);
    private static void Press(TopLevel window, PhysicalKey key, Window releaseOn)
    { window.KeyPressQwerty(key, RawInputModifiers.None); if (releaseOn.IsVisible) { releaseOn.KeyReleaseQwerty(key, RawInputModifiers.None); } Dispatcher.UIThread.RunJobs(); }
    private static bool Running(int pid)
    { try { string stat = File.ReadAllText("/proc/" + pid + "/stat"); int end = stat.LastIndexOf(')'); return end < 0 || end + 2 >= stat.Length || stat[end + 2] is not ('Z' or 'X' or 'x'); } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } }
    private static void Capture(TopLevel window, string name)
    { Pump(window); using Bitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }
    private static Color Pixel(TopLevel window, Point point)
    { Pump(window); using Bitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); using WriteableBitmap copy = new(frame!.PixelSize, frame.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul); using ILockedFramebuffer buffer = copy.Lock(); frame.CopyPixels(buffer, AlphaFormat.Unpremul); byte[] bytes = new byte[4]; Marshal.Copy(buffer.Address + (int)point.Y * buffer.RowBytes + (int)point.X * 4, bytes, 0, 4); return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]); }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Dictionary<string, string?> _environment = []; public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-process-ui-" + Guid.NewGuid().ToString("N"));
        public GitRepository Repository { get; private set; } = null!; public List<string> Hashes { get; } = []; public List<Window> Windows { get; } = [];
        private Fixture()
        {
            Directory.CreateDirectory(Root); foreach ((string name, string value) in new[] { ("XDG_CONFIG_HOME", Path.Combine(Root, "profile")), ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_OPTIONAL_LOCKS", "0") })
            { _environment[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
            new PreferencesStore().Save(new() { Theme = "Light" });
        }
        public static async Task<Fixture> Create()
        {
            Fixture f = new();
            try
            {
                f.Repository = await GitRepository.InitAsync(Path.Combine(f.Root, "repo"));
                await f.Git("config", "user.name", "Process UI Test"); await f.Git("config", "user.email", "process-ui@example.invalid"); await f.Git("config", "commit.gpgsign", "false"); await f.Git("config", "core.hooksPath", "/dev/null");
                for (int index = 0; index < 6; index++) { File.WriteAllText(Path.Combine(f.Repository.Path, "tracked.txt"), "version " + index + "\n"); await f.Git("add", "tracked.txt"); await f.Git("commit", "-m", "Native " + index); f.Hashes.Add(await f.Head()); }
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async Task<GitResult> Git(params string[] arguments) { GitResult result = await new GitRunner().RunAsync(Repository.Path, arguments); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public async Task<string> Head() => (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        public Window Owner(ThemeVariant? theme = null) { Window owner = new() { Width = 800, Height = 600, RequestedThemeVariant = theme ?? ThemeVariant.Light }; Windows.Add(owner); owner.Show(); Pump(owner); return owner; }
        public async Task<BisectDialog> Show(BisectDialog dialog) { Windows.Add(dialog); dialog.Show(); await dialog.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.That(dialog.LastError, Is.Null); Pump(dialog); return dialog; }
        public Task<GitProcessDialogResult> Process(Window owner, bool settings, string[] args) => GitProcessDialog.ShowAsync(owner, Repository.Path, "git " + string.Join(' ', args), settings,
            (token, output) => new GitRunner().RunAsync(Repository.Path, args, token, outputReceived: output));
        public string Gate(string command)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The private descendant fixture requires Linux."); }
            string wrapper = Path.Combine(Root, "gated-git-" + Guid.NewGuid().ToString("N")); static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
            File.WriteAllText(wrapper, "#!/bin/sh\ncase \" $* \" in *" + Quote(" " + command + " ") + "*)\n /usr/bin/env git \"$@\"\n status=$?\n sleep 60 &\n child=$!\n"
                + TestSignalFile.ShellPids(wrapper + ".pids", "$$", "$child")
                + " while test ! -f " + Quote(wrapper + ".release") + "; do sleep 0.02; done\n kill \"$child\" 2>/dev/null || :\n wait \"$child\" 2>/dev/null || :\n exit \"$status\";;\n *) exec /usr/bin/env git \"$@\";;\nesac\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return wrapper;
        }
        public string Materials() => string.Join('\n', Directory.GetFiles(Repository.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Repository.Path, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        public async ValueTask DisposeAsync()
        {
            static void CloseOwned(Window window) { foreach (Window child in window.OwnedWindows.ToArray()) { CloseOwned(child); child.Close(); } }
            foreach (Window window in Windows.AsEnumerable().Reverse()) { CloseOwned(window); window.Close(); }
            if (Windows.Count > 0) { await Until(() => Windows.All(window => !window.IsVisible && (window is not RepositoryDialog dialog || !dialog.IsBusy))); }
            Dispatcher.UIThread.RunJobs(); foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); } if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
        }
    }
}

using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using BrowserFixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DeleteUnusedBranchesSettingsScriptRouteTests
{
    private const int ScriptId = 9082;
    private static ScriptDefinition Script => new() { Name = "Owned obsolete branch settings", Command = "{plugin.Delete obsolete branches}", HotkeyCommandIdentifier = ScriptId };

    [AvaloniaTest]
    public async Task APhysicalMainScriptHotkeyCanReachItsSettingsWhileMainIsBusyAndCancelKeepsItsOwnerOpen()
    {
        using ReadOnlyGit environment = new(); using BrowserFixture f = await BrowserFixture.CreateAsync("base\n", "base\n");
        ConfigurePhysicalScript(); Pump(f.Window);
        GitRepository repository = CurrentRepository(f.Window);
        Material before = await Material.Capture(repository); Profiles profiles = await Profiles.Capture(repository);
        PressScript(f.Window);
        DeleteUnusedBranchesDialog preview = await Preview(f.Window, f.Window);
        Assert.That(f.Window.IsBusy, Is.True, "This route must exercise the real Main _busy/fromScript exception, not a direct dialog constructor.");
        DesktopSettingsDialog settings = await OpenSettings(f.Window, f.Window, preview);
        Assert.That(settings.Owner, Is.SameAs(f.Window)); Assert.That(f.Window.IsBusy, Is.True);
        EditPluginDraft(settings); Pointer(Field<Button>(settings, "CancelDesktopSettings"));
        await Until(f.Window, () => !settings.IsVisible && !f.Window.IsBusy && !f.Window.OwnedWindows.Any(window => window.IsVisible));
        await f.Window.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(f.Window.IsVisible && f.Window.IsEffectivelyEnabled, Is.True);
        Assert.That(f.Window.LastException, Is.Null);
        await profiles.AssertSame(repository); await before.AssertSame(repository);
    }

    [AvaloniaTest]
    public async Task CancellingTheActualScriptTokenClosesSettingsAndPreservesTheCommitOwnerWithoutPublishingItsDraft()
    {
        using ReadOnlyGit environment = new(); using BrowserFixture f = await BrowserFixture.CreateAsync("base\n", "working change Ω\n");
        GitRepository repository = CurrentRepository(f.Window);
        CommitDialog commit = new(repository); Task closed = commit.ShowDialog(f.Window);
        using CancellationTokenSource cancellation = new(); Task<bool>? execution = null;
        try
        {
            await commit.InitializeAsync(); await Until(f.Window, () => !commit.IsBusy);
            Material before = await Material.Capture(repository); Profiles profiles = await Profiles.Capture(repository);
            execution = DesktopScriptExecutor.ExecuteAsync(commit, repository, Script, cancellation.Token);
            DeleteUnusedBranchesDialog preview = await Preview(f.Window, commit);
            DesktopSettingsDialog settings = await OpenSettings(f.Window, commit, preview); EditPluginDraft(settings);
            CancellationToken ownedToken = settings.GitEnvironmentSession!.CancellationToken;
            cancellation.Cancel();
            await Until(f.Window, () => execution.IsCompleted && !settings.IsVisible && !commit.OwnedWindows.Any(window => window.IsVisible));
            await MustBeCancelled(execution);
            Assert.That(ownedToken.IsCancellationRequested, Is.True);
            Assert.That(commit.IsVisible && commit.IsEffectivelyEnabled, Is.True); Assert.That(closed.IsCompleted, Is.False);
            Assert.That(f.Window.OwnedWindows, Is.EquivalentTo(new[] { commit }));
            await profiles.AssertSame(repository); await before.AssertSame(repository);
        }
        finally
        {
            cancellation.Cancel(); CloseSettingsChildren(commit);
            if (execution is not null) { await DrainExecution(f.Window, execution); }
            if (commit.IsVisible) { commit.Close(); }
            await Until(f.Window, () => closed.IsCompleted && !commit.IsBusy); await closed;
        }
    }

    [AvaloniaTest]
    public async Task OneCommitCloseSealsTheSettingsTokenDuringItsOwnClosingBeforeTheScriptCanReportSuccess()
    {
        using ReadOnlyGit environment = new(); using BrowserFixture f = await BrowserFixture.CreateAsync("base\n", "working change Ω\n");
        GitRepository repository = CurrentRepository(f.Window);
        CommitDialog commit = new(repository); Task closed = commit.ShowDialog(f.Window); Task<bool>? execution = null;
        try
        {
            await commit.InitializeAsync(); await Until(f.Window, () => !commit.IsBusy);
            Material before = await Material.Capture(repository); Profiles profiles = await Profiles.Capture(repository);
            execution = DesktopScriptExecutor.ExecuteAsync(commit, repository, Script);
            DeleteUnusedBranchesDialog preview = await Preview(f.Window, commit);
            DesktopSettingsDialog settings = await OpenSettings(f.Window, commit, preview); EditPluginDraft(settings);
            CancellationToken ownedToken = settings.GitEnvironmentSession!.CancellationToken;
            bool sawOwnerClose = false, cancelledInClosing = false;
            settings.Closing += (_, args) =>
            {
                if (args.CloseReason != WindowCloseReason.OwnerWindowClosing) { return; }
                sawOwnerClose = true; cancelledInClosing |= ownedToken.IsCancellationRequested;
            };
            commit.Close(); // One explicit owner close; no Settings Cancel/ACK or second test Close.
            await Until(f.Window, () => execution.IsCompleted && closed.IsCompleted && !settings.IsVisible && !commit.IsVisible);
            await MustBeCancelled(execution); await closed;
            Assert.That(sawOwnerClose, Is.True);
            Assert.That(cancelledInClosing, Is.True,
                "Capture must precede child completion. Disposal on Closed or owner.Closing afterwards is too late for this oracle.");
            Assert.That(f.Window.IsVisible, Is.True); Assert.That(f.Window.OwnedWindows, Is.Empty);
            await profiles.AssertSame(repository); await before.AssertSame(repository);
        }
        finally
        {
            CloseSettingsChildren(commit);
            if (execution is not null) { await DrainExecution(f.Window, execution); }
            if (commit.IsVisible) { commit.Close(); }
            await Until(f.Window, () => closed.IsCompleted && !commit.IsBusy); await closed;
        }
    }

    [AvaloniaTest]
    public async Task OneCloseOfTheBusyCommitWaitsForTheRealSettingsBrowseAndRetainsItsOwnerCloseIntentAfterDrain()
    {
        using ReadOnlyGit environment = new(); using BrowserFixture f = await BrowserFixture.CreateAsync("base\n", "working change Ω\n");
        ConfigurePhysicalScript(); GitRepository repository = CurrentRepository(f.Window);
        CommitDialog commit = new(repository); Task closed = commit.ShowDialog(f.Window);
        TaskCompletionSource<string?> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await commit.InitializeAsync(); await Until(f.Window, () => !commit.IsBusy);
            Material before = await Material.Capture(repository); Profiles profiles = await Profiles.Capture(repository);
            PressScript(commit); DeleteUnusedBranchesDialog preview = await Preview(f.Window, commit);
            Assert.That(commit.IsBusy, Is.True, "The real Commit script hotkey must own its running operation.");
            CancellationToken operation = (CancellationToken)typeof(CommitDialog).GetProperty("OperationToken", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(commit)!;
            DesktopSettingsDialog settings = await OpenSettings(f.Window, commit, preview); EditPluginDraft(settings);
            SettingsNavigationTestDriver.Select(settings, "GitSsh");
            SshSettingsPage ssh = settings.GitEnvironmentSession!.SshPage;
            CancellationToken pickerToken = default; bool pickerEntered = false;
            ssh.PickExecutable = token => { pickerToken = token; pickerEntered = true; return reply.Task; };
            Pointer(Field<RadioButton>(settings, "Other")); Pointer(Field<Button>(settings, "OtherSshBrowse"));
            await Until(f.Window, () => pickerEntered && ssh.IsBusy);
            Task browse = ssh.OperationCompletion;
            Assert.That(settings.Owner, Is.SameAs(commit)); Assert.That(browse.IsCompleted, Is.False);
            commit.Close(); // Only explicit owner close. Late picker does not cooperate with cancellation.
            Pump(f.Window);
            Assert.That(pickerToken.IsCancellationRequested, Is.True);
            Assert.That(settings.IsVisible && commit.IsVisible && commit.IsBusy, Is.True);
            Assert.That(closed.IsCompleted, Is.False); Assert.That(browse.IsCompleted, Is.False);
            await profiles.AssertSame(repository);
            reply.SetResult("/late/ssh-result-must-not-be-published");
            await Until(f.Window, () => browse.IsCompleted && closed.IsCompleted && !settings.IsVisible && !commit.IsVisible && !commit.IsBusy);
            await browse.WaitAsync(TimeSpan.FromSeconds(5)); await closed;
            Assert.That(operation.IsCancellationRequested, Is.True, "The deferred Close must return to the true owner instead of merely dismissing Settings.");
            Assert.That(ssh.Draft, Is.Not.EqualTo("/late/ssh-result-must-not-be-published"));
            Assert.That(commit.LastError, Is.Null); Assert.That(f.Window.IsVisible, Is.True); Assert.That(f.Window.OwnedWindows, Is.Empty);
            await profiles.AssertSame(repository); await before.AssertSame(repository);
        }
        finally
        {
            reply.TrySetResult(null); CloseSettingsChildren(commit);
            if (commit.IsVisible) { commit.Close(); }
            await Until(f.Window, () => closed.IsCompleted && !commit.IsBusy); await closed;
        }
    }

    private static void ConfigurePhysicalScript()
    {
        new ScriptsPreferencesStore().Save([Script]); // Register the real script identity before loading its hotkey catalog.
        HotkeyPreferencesStore store = new(); HotkeyBindings bindings = store.Load();
        bindings.Set("Scripts", ScriptId, new(Key.F12, KeyModifiers.Control | KeyModifiers.Alt)); store.Save(bindings);
    }
    private static void PressScript(Window owner)
    { owner.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.Control | RawInputModifiers.Alt); owner.KeyReleaseQwerty(PhysicalKey.F12, RawInputModifiers.Control | RawInputModifiers.Alt); Pump(owner); }
    private static async Task<DeleteUnusedBranchesDialog> Preview(MainWindow main, Window owner)
    {
        await Until(main, () => owner.OwnedWindows.OfType<DeleteUnusedBranchesDialog>().Any(window => window.IsVisible));
        DeleteUnusedBranchesDialog preview = owner.OwnedWindows.OfType<DeleteUnusedBranchesDialog>().Single(window => window.IsVisible);
        await Until(main, () => !preview.IsBusy && preview.LastPreview is not null);
        Assert.That(preview.Owner, Is.SameAs(owner)); return preview;
    }
    private static async Task<DesktopSettingsDialog> OpenSettings(MainWindow main, Window owner, DeleteUnusedBranchesDialog preview)
    {
        Pointer(Field<Button>(preview, "buttonSettings"));
        await Until(main, () => !preview.IsVisible && owner.OwnedWindows.OfType<DesktopSettingsDialog>().Any(window => window.IsVisible));
        DesktopSettingsDialog settings = owner.OwnedWindows.OfType<DesktopSettingsDialog>().Single(window => window.IsVisible);
        await Until(main, () => settings.DeleteUnusedBranchesSettingsSession.Loaded && Field<Button>(settings, "ApplyDesktopSettings").IsEnabled);
        Assert.That(settings.Owner, Is.SameAs(owner)); Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo("PluginDeleteUnusedBranches"));
        Assert.That(preview.IsBusy, Is.False); Assert.That(preview.Completion.IsCompleted, Is.True);
        Assert.That(owner.OwnedWindows.OfType<DeleteUnusedBranchesDialog>().Any(window => window.IsVisible), Is.False);
        return settings;
    }
    private static void EditPluginDraft(DesktopSettingsDialog settings)
    {
        Pointer(Field<RadioButton>(settings, "DeleteUnusedBranchesGlobalRB"));
        TextBox box = Field<TextBox>(settings, "_remoteName"); Pointer(box);
        settings.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control); settings.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control);
        settings.KeyTextInput("script-route-unsaved"); Pump(settings);
        Assert.That(box.Text, Is.EqualTo("script-route-unsaved"));
    }
    private static GitRepository CurrentRepository(MainWindow main)
        => (GitRepository)typeof(MainWindow).GetField("_repository", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
    private static T Field<T>(Window owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pointer(Control control) { control.BringIntoView(); PushProcessTestDriver.Pointer(control); }
    private static void Pump(Window owner)
    {
        Dispatcher.UIThread.RunJobs();
        foreach (Window window in Windows(owner).Prepend(owner).Where(window => window.IsVisible)) { window.UpdateLayout(); }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }
    private static IEnumerable<Window> Windows(Window owner)
    { foreach (Window child in owner.OwnedWindows.ToArray()) { yield return child; foreach (Window nested in Windows(child)) { yield return nested; } } }
    private static async Task Until(Window owner, Func<bool> condition)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(15)) { Pump(owner); await Task.Delay(10); }
        Pump(owner); Assert.That(condition(), Is.True, "The real script/plugin/Settings owner chain did not settle within fifteen seconds; no automatic modal acknowledgments are used.");
    }
    private static async Task MustBeCancelled(Task<bool> execution)
    {
        try { _ = await execution.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Fail("Owner close/cancellation must not become successful plugin/script completion."); }
        catch (OperationCanceledException) { }
    }
    private static async Task DrainExecution(MainWindow owner, Task<bool> execution)
    { await Until(owner, () => execution.IsCompleted); try { await execution; } catch (OperationCanceledException) { } }
    private static void CloseSettingsChildren(Window owner)
    {
        foreach (Window child in owner.OwnedWindows.ToArray())
        { CloseSettingsChildren(child); if (child is DesktopSettingsDialog or DeleteUnusedBranchesDialog) { child.Close(); } }
    }
    private sealed class ReadOnlyGit : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("GIT_OPTIONAL_LOCKS");
        internal ReadOnlyGit() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        public void Dispose() => Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", _previous);
    }
    private sealed record Profiles(Dictionary<string, byte[]?> Files, string ConfigurationRoot)
    {
        internal static async Task<Profiles> Capture(GitRepository repository)
        {
            GitRepositoryInfo info = await repository.GetInfoAsync();
            string root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? throw new AssertionException("The existing browser fixture must provide its private configuration root.");
            string[] xml = [DetailedRepositorySettings.GlobalPath, Path.Combine(info.CommonDirectory, GitDistributedSettingsSnapshot.FileName), Path.Combine(info.RootPath, GitDistributedSettingsSnapshot.FileName)];
            Dictionary<string, byte[]?> files = (Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>())
                .Concat(xml).Distinct(StringComparer.Ordinal).ToDictionary(path => path, ReadOrMissing, StringComparer.Ordinal);
            return new(files, root);
        }
        internal async Task AssertSame(GitRepository repository)
        {
            Profiles after = await Capture(repository);
            Assert.That(after.ConfigurationRoot, Is.EqualTo(ConfigurationRoot)); Assert.That(after.Files, Is.EqualTo(Files),
                "No Settings XML, JSON, credentials or neighbouring profile may be created, removed or modified by Cancel/owner stop.");
        }
        private static byte[]? ReadOrMissing(string path)
        { try { return File.ReadAllBytes(path); } catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; } }
    }
    private sealed record Material(Dictionary<string, byte[]> Bytes)
    {
        internal static async Task<Material> Capture(GitRepository repository)
        {
            GitRunner reader = new(); Dictionary<string, byte[]> bytes = [];
            foreach ((string key, string[] arguments) in new[]
            {
                ("HEAD", new[] { "rev-parse", "HEAD" }), ("refs", new[] { "show-ref", "--head" }),
                ("entries", new[] { "ls-files", "--stage", "-z" }), ("config", new[] { "config", "--local", "--null", "--list" }),
                ("status", new[] { "status", "--porcelain=v2", "-z" })
            })
            {
                GitBinaryResult result = await reader.RunBinaryAsync(repository.Path, arguments);
                Assert.That(result.Success, Is.True, result.StandardError); bytes[key] = result.StandardOutput;
            }
            foreach (string name in new[] { ".git/index", ".git/logs/HEAD", "file.txt" }) { bytes[name] = File.ReadAllBytes(Path.Combine(repository.Path, name)); }
            return new(bytes);
        }
        internal async Task AssertSame(GitRepository repository)
        { Material after = await Capture(repository); Assert.That(after.Bytes, Is.EqualTo(Bytes)); }
    }
}

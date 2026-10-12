using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using ReferenceFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class PullAdvancedDialogTests
{
    [AvaloniaTest]
    public async Task OriginalTagAndPruneCouplingsWorkWithThePointerAndNoTagsWorksWithItsAccessKey()
    {
        await using Fixture f = await Fixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        await f.Git(f.Writer, "tag", "reachable"); await f.Git(f.Writer, "push", "origin", "--tags");
        await f.Repository.SetConfigAsync("remote.origin.tagopt", "--no-tags");
        PullDialog dialog = await f.OpenAsync(GitPullAction.Fetch);
        Pointer(dialog, "PullTagsAll"); Assert.That(Field<RadioButton>(dialog, "PullTagsAll").IsChecked, Is.True);
        Pointer(dialog, "PullPruneTags");
        Assert.That(Field<CheckBox>(dialog, "PullPrune").IsChecked, Is.True);
        Assert.That(Field<RadioButton>(dialog, "PullTagsAll").IsChecked, Is.True);
        Alt(dialog, PhysicalKey.N);
        Assert.That(Field<RadioButton>(dialog, "PullTagsNone").IsChecked, Is.True);
        Assert.That(Field<CheckBox>(dialog, "PullPruneTags").IsChecked, Is.True, "The original lets the user choose NoTags after checking PruneTags.");
        await dialog.ExecuteAsync(dialog); Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That((await f.Repository.GetReferencesAsync()).Any(reference => reference.Name == "refs/tags/reachable"), Is.True, "--prune-tags adds an explicit tag refspec even alongside --no-tags, following native Git.");
        await f.Git(f.Repository, "tag", "--delete", "reachable");
        Pointer(dialog, "PullTagsAll");
        Pointer(dialog, "PullPrune");
        Assert.That(Field<CheckBox>(dialog, "PullPruneTags").IsChecked, Is.False);
        Assert.That(Field<RadioButton>(dialog, "PullTagsAll").IsChecked, Is.True);
        Pointer(dialog, "PullRebase");
        Assert.That(Field<RadioButton>(dialog, "PullTagsAll").IsEnabled, Is.False);
        Assert.That(Field<RadioButton>(dialog, "PullTagsFollow").IsChecked, Is.True, "Merge/Rebase return an All selection to the original Follow default.");
        Assert.That(Field<CheckBox>(dialog, "PullPruneTags").IsEnabled, Is.False);
        await dialog.ExecuteAsync(dialog);
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That((await f.Repository.GetReferencesAsync()).Any(reference => reference.Name == "refs/tags/reachable"), Is.False, "Resetting All to Follow must preserve the remote's no-tags config during a real pull.");
        Pointer(dialog, "PullFetch"); Alt(dialog, PhysicalKey.N);
        Assert.That(Field<RadioButton>(dialog, "PullTagsNone").IsChecked, Is.True);
        Pointer(dialog, "ExecutePull"); await Closed(dialog);
        Assert.That((await f.Repository.GetReferencesAsync()).Any(reference => reference.Name == "refs/tags/reachable"), Is.False);
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial));
    }

    [AvaloniaTest]
    public async Task FrenchOriginalAdvancedGroupsAndPaletteRenderWithoutClippingInBothThemes()
    {
        await using Fixture f = await Fixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
        PullDialog dialog = await f.OpenAsync(GitPullAction.Fetch);
        Assert.That(((AccessText)Field<RadioButton>(dialog, "PullTagsFollow").Content!).Text, Does.StartWith("Suivre _tagopt"));
        Assert.That(((AccessText)Field<CheckBox>(dialog, "PullPruneTags").Content!).Text, Does.Contain("étiquettes"));
        Assert.That(((AccessText)Field<CheckBox>(dialog, "PullAutoStash").Content!).Text, Is.EqualTo("_Remiser automatiquement"));
        Assert.That(Field<TextBlock>(dialog, "PullRemoteLabel").Text, Is.EqualTo("Distant"));
        Assert.That(Field<TextBlock>(dialog, "PullRemoteBranchLabel").Text, Is.EqualTo("Branche distante"));
        Assert.That(Field<CheckBox>(dialog, "PullUnshallow").IsVisible, Is.False, "The historical option is hidden for a full repository.");
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            dialog.RequestedThemeVariant = theme; Pointer(dialog, "PullTagsAll"); Pump(dialog);
            foreach (string name in new[] { "PullRebase", "PullTagsFollow", "PullTagsAll", "PullAutoStash", "ExecutePull" })
            {
                Control control = Field<Control>(dialog, name); Point location = control.TranslatePoint(default, dialog)!.Value;
                Assert.That(location.Y, Is.GreaterThanOrEqualTo(0)); Assert.That(location.Y + control.Bounds.Height, Is.LessThanOrEqualTo(dialog.ClientSize.Height));
                if (control is ContentControl { Content: AccessText label })
                { Assert.That(label.Bounds.Height, Is.LessThanOrEqualTo(control.Bounds.Height), "Wrapped French labels must fit their actual control."); }
            }
            Color expected = Color.Parse(theme == ThemeVariant.Dark ? "#2864b4" : "#0078d7");
            Avalonia.Controls.Shapes.Ellipse glyph = Field<RadioButton>(dialog, "PullTagsAll").GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single(item => item.Name == "CheckOuterEllipse");
            Assert.That(((ISolidColorBrush)glyph.Fill!).Color, Is.EqualTo(expected));
            Capture(dialog, "pull-advanced-fr-" + theme + ".png");
        }
        dialog.Close();
    }

    [AvaloniaTest]
    public async Task ShallowDownloadCheckboxFetchesCompleteHistoryAndDoesNotPersistItsSessionValue()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.AdvanceAsync("one.txt"); await f.AdvanceAsync("two.txt");
        GitRepository shallow = await f.ShallowAsync(); PullDialog dialog = await f.OpenAsync(GitPullAction.Fetch, shallow);
        Assert.That(Field<CheckBox>(dialog, "PullUnshallow").IsVisible, Is.True);
        Assert.That(Field<CheckBox>(dialog, "PullUnshallow").IsChecked, Is.False);
        Pointer(dialog, "PullUnshallow"); Pointer(dialog, "ExecutePull"); await Closed(dialog);
        Assert.That(await shallow.IsShallowRepositoryAsync(), Is.False);
        Assert.That((await f.Git(shallow, "rev-list", "--count", "HEAD")).StandardOutput.Trim(), Is.EqualTo("3"));
        PullDialog reopened = await f.OpenAsync(GitPullAction.Fetch, shallow);
        Assert.That(Field<CheckBox>(reopened, "PullUnshallow").IsVisible, Is.False);
        Assert.That(Field<CheckBox>(reopened, "PullUnshallow").IsChecked, Is.False); reopened.Close();
    }

    [AvaloniaTest]
    public async Task AutoStashNoKeepsTheSavedWorkAndRememberedNoSuppressesOnlyTheNextPopQuestion()
    {
        await using Fixture f = await Fixture.CreateAsync(); string remote = await f.AdvanceAsync("remote.txt");
        await File.WriteAllTextAsync(f.Root.File, "saved tracked\n"); await File.WriteAllTextAsync(f.Root.PathInRepository("untracked.txt"), "not included\n");
        PullDialog dialog = await f.OpenAsync(); Pointer(dialog, "PullAutoStash");
        Task execution = dialog.ExecuteAsync(dialog); Window prompt = await Owned(dialog, "ConfirmPopPullAutoStash");
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(remote), "The question must follow the successful pull.");
        Assert.That(await File.ReadAllTextAsync(f.Root.File), Is.EqualTo("base\n"));
        Assert.That(await File.ReadAllTextAsync(f.Root.PathInRepository("untracked.txt")), Is.EqualTo("not included\n"));
        Pointer(prompt, "RememberPullAutoStash"); Pointer(prompt, "KeepPullAutoStash"); await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError); Assert.That((await f.Repository.GetStashesAsync()).Count, Is.EqualTo(1));
        Assert.That(new PullPreferencesStore().Load().AutoStash, Is.True);
        Assert.That(new PullPreferencesStore().Load().AutoPopStashAfterPull, Is.False); dialog.Close();
        await File.WriteAllTextAsync(f.Root.File, "another saved change\n");
        PullDialog next = await f.OpenAsync(); Assert.That(Field<CheckBox>(next, "PullAutoStash").IsChecked, Is.True);
        await next.ExecuteAsync(next).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(next.Succeeded, Is.True, next.LastError); Assert.That(next.OwnedWindows, Is.Empty);
        Assert.That((await f.Repository.GetStashesAsync()).Count, Is.EqualTo(2)); next.Close();
    }

    [AvaloniaTest]
    public async Task AutoStashYesRestoresLiteralUntrackedBytesAfterApprovalAndRememberPersistsTheAnswer()
    {
        await using Fixture f = await Fixture.CreateAsync(); new PullPreferencesStore().Save(new(AutoStash: true, IncludeUntrackedFilesInAutoStash: true));
        const string literal = "[untracked]* Ω.txt"; await File.WriteAllTextAsync(f.Root.File, "saved staged\n"); await f.Repository.StageAsync(["file.txt"]);
        await File.WriteAllTextAsync(f.Root.PathInRepository(literal), "literal bytes\n");
        PullDialog dialog = await f.OpenAsync(); dialog.RequestedThemeVariant = ThemeVariant.Dark;
        Task execution = dialog.ExecuteAsync(dialog); Window prompt = await Owned(dialog, "ConfirmPopPullAutoStash");
        Assert.That(prompt.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
        Assert.That(File.Exists(f.Root.PathInRepository(literal)), Is.False, "Untracked inclusion is applied to the saved stash, not to its trigger.");
        Capture(prompt, "pull-autostash-question-Dark.png");
        Pointer(prompt, "RememberPullAutoStash"); Pointer(prompt, "ApplyPullAutoStash"); await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(await File.ReadAllTextAsync(f.Root.File), Is.EqualTo("saved staged\n")); Assert.That(await File.ReadAllTextAsync(f.Root.PathInRepository(literal)), Is.EqualTo("literal bytes\n"));
        Assert.That((await f.Repository.GetStashesAsync()).Count, Is.Zero);
        Assert.That((await f.Repository.GetStatusAsync()).Changes.Single(change => change.Path == "file.txt").IsStaged, Is.False, "Original StashPop does not restore the index with --index.");
        Assert.That(new PullPreferencesStore().Load().AutoPopStashAfterPull, Is.True); dialog.Close();
    }

    [AvaloniaTest]
    public async Task EscapeDismissesTheAutoPopQuestionWithoutRememberingItAndNeverLosesTheSavedWork()
    {
        await using Fixture f = await Fixture.CreateAsync(); await File.WriteAllTextAsync(f.Root.File, "saved on dismissal\n");
        new PullPreferencesStore().Save(new(AutoStash: true)); PullDialog dialog = await f.OpenAsync();
        Task execution = dialog.ExecuteAsync(dialog); Window prompt = await Owned(dialog, "ConfirmPopPullAutoStash");
        Pointer(prompt, "RememberPullAutoStash"); prompt.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        dialog.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(new PullPreferencesStore().Load().AutoPopStashAfterPull, Is.Null);
        Assert.That((await f.Repository.GetStashesAsync()).Count, Is.EqualTo(1)); Assert.That(dialog.Succeeded, Is.True, dialog.LastError); dialog.Close();
    }

    [AvaloniaTest]
    public async Task FailedPullExposesGitOutputKeepsAutoStashAndDoesNotOfferToPop()
    {
        await using Fixture f = await Fixture.CreateAsync(); await File.WriteAllTextAsync(f.Root.File, "saved before failure\n"); new PullPreferencesStore().Save(new(AutoStash: true));
        PullDialog dialog = await f.OpenAsync(branch: "missing-private-branch"); await dialog.ExecuteAsync(dialog).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(dialog.Succeeded, Is.False); Assert.That(dialog.LastException, Is.TypeOf<GitCommandException>());
        Assert.That(Field<TextBox>(dialog, "PullOutput").Text, Does.Contain("missing-private-branch")); Assert.That(dialog.OwnedWindows, Is.Empty);
        Assert.That((await f.Repository.GetStashesAsync()).Count, Is.EqualTo(1)); Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial));
        Assert.That(await File.ReadAllTextAsync(f.Root.File), Is.EqualTo("base\n")); dialog.Close();
    }

    [AvaloniaTest]
    public async Task StaleUnshallowRequestIsRejectedBeforeAutoStashOrAnyRefIndexConfigChange()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.AdvanceAsync("one.txt"); GitRepository shallow = await f.ShallowAsync();
        PullDialog dialog = await f.OpenAsync(repository: shallow, branch: "main"); Pointer(dialog, "PullUnshallow"); Pointer(dialog, "PullAutoStash");
        await shallow.FetchRemoteAsync("origin", new GitFetchOptions(Unshallow: true));
        string file = Path.Combine(shallow.Path, "file.txt"); await File.WriteAllTextAsync(file, "must remain dirty\n");
        string refs = (await f.Git(shallow, "for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput;
        string index = (await f.Git(shallow, "ls-files", "--stage", "-z")).StandardOutput; byte[] config = File.ReadAllBytes(Path.Combine(shallow.Path, ".git/config"));
        await dialog.ExecuteAsync(dialog); Assert.That(dialog.LastException, Is.TypeOf<InvalidOperationException>());
        Assert.That(dialog.Attempted, Is.False); Assert.That((await shallow.GetStashesAsync()).Count, Is.Zero);
        Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("must remain dirty\n")); Assert.That((await f.Git(shallow, "ls-files", "--stage", "-z")).StandardOutput, Is.EqualTo(index));
        Assert.That((await f.Git(shallow, "for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput, Is.EqualTo(refs)); Assert.That(File.ReadAllBytes(Path.Combine(shallow.Path, ".git/config")), Is.EqualTo(config));
        Assert.That(File.Exists(new PullPreferencesStore().FilePath), Is.False); dialog.Close();
    }

    [AvaloniaTest]
    public async Task CancelDuringPullStopsTheProcessAndRetainsTheSavedStashWhileAllOptionsAreDisabled()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private Linux executable cancellation scenario."); return; }
        await using Fixture f = await Fixture.CreateAsync(); await File.WriteAllTextAsync(f.Root.File, "recover cancellation\n");
        new PullPreferencesStore().Save(new(AutoStash: true));
        string marker = f.Root.Output("pull-started"), helper = marker + ".py";
        await File.WriteAllTextAsync(helper, "#!/usr/bin/python3\nimport os,pathlib,sys,time\nif 'pull' in sys.argv[1:]:\n pathlib.Path(" + JsonSerializer.Serialize(marker) + ").write_text(str(os.getpid()))\n time.sleep(30)\nos.execvp('git',['git']+sys.argv[1:])\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        PullDialog dialog = await f.OpenAsync(repository: new(f.Repository.Path, new GitRunner(helper)));
        Task execution = dialog.ExecuteAsync(dialog); await Until(() => File.Exists(marker));
        int pid = int.Parse(await File.ReadAllTextAsync(marker), CultureInfo.InvariantCulture);
        foreach (string name in new[] { "PullAutoStash", "PullTagsFollow", "PullPruneTags", "PullRemote", "PullStashChanges" }) { Assert.That(Field<Control>(dialog, name).IsEnabled, Is.False); }
        Pointer(dialog, "ClosePull"); await execution.WaitAsync(TimeSpan.FromSeconds(10)); await Until(() => !dialog.IsVisible);
        Assert.That(dialog.LastError, Is.EqualTo("Operation cancelled")); Assert.That((await f.Repository.GetStashesAsync()).Count, Is.EqualTo(1));
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial));
        try { using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid); Assert.That(process.HasExited, Is.True); } catch (ArgumentException) { }
    }

    [AvaloniaTest]
    public async Task SuccessfulPullReusesTheDurableCheckoutDecisionAndUpdatesLiteralNestedSubmodulesBeforeItsCallback()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.AddModulesAsync();
        string helper = f.Root.Output("slow-submodule-update.py");
        await File.WriteAllTextAsync(helper, "#!/usr/bin/python3\nimport os,sys,time\nif 'submodule' in sys.argv[1:] and 'update' in sys.argv[1:]:\n time.sleep(0.5)\nos.execvp('git',['git']+sys.argv[1:])\n");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        new SubmoduleCheckoutPreferencesStore().Remember(true); int callbacks = 0;
        PullDialog dialog = await f.OpenAsync(repository: new(f.Repository.Path, new GitRunner(helper)), afterUpdate: async token =>
        {
            GitRepository module = new(f.Root.PathInRepository(Fixture.ModulePath));
            Assert.That(await module.ResolveRevisionAsync("HEAD", token), Is.EqualTo(f.NewModuleHead));
            GitSubmodule child = (await module.GetSubmodulesAsync(token)).Single(); Assert.That(child.Path, Is.EqualTo(Fixture.NestedPath)); Assert.That(child.Head, Is.EqualTo(child.ExpectedHead)); callbacks++;
        });
        dialog.RequestedThemeVariant = ThemeVariant.Dark;
        Task execution = dialog.ExecuteAsync(dialog);
        await Until(() => dialog.OwnedWindows.OfType<SubmoduleUpdateDialog>().Any());
        Assert.That(dialog.OwnedWindows.OfType<SubmoduleUpdateDialog>().Single().ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark),
            "The progress window must inherit the Pull owner's theme even when the application theme differs.");
        await execution.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(dialog.Succeeded, Is.True, dialog.LastException?.ToString()); Assert.That(callbacks, Is.EqualTo(1)); Assert.That(dialog.OwnedWindows, Is.Empty); dialog.Close();
    }

    [AvaloniaTest]
    public async Task UninitializedPullAsksTheOriginalQuestionAndNoDoesNotPersistOrUpdateButTheNextYesInitializesRecursively()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.AddModulesAsync();
        await f.Git(f.Repository, "submodule", "deinit", "--force", "--", Fixture.ModulePath); int callbacks = 0;
        PullDialog first = await f.OpenAsync(afterUpdate: _ => { callbacks++; return Task.CompletedTask; }); Task firstExecution = first.ExecuteAsync(first);
        Window question = await Owned(first, "ConfirmInitializePullSubmodules"); Assert.That(question.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text?.Contains("initialize", StringComparison.Ordinal) == true), Is.True);
        Pointer(question, "SkipInitializePullSubmodules"); await firstExecution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That((await f.Repository.GetSubmodulesAsync()).Single().IsInitialized, Is.False); Assert.That(callbacks, Is.Zero); Assert.That(new SubmoduleCheckoutPreferencesStore().Load().Decision, Is.Null); first.Close();
        PullDialog second = await f.OpenAsync(afterUpdate: _ => { callbacks++; return Task.CompletedTask; }); Task secondExecution = second.ExecuteAsync(second);
        question = await Owned(second, "ConfirmInitializePullSubmodules"); Pointer(question, "InitializePullSubmodules"); await secondExecution.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(second.Succeeded, Is.True, second.LastException?.ToString()); Assert.That(callbacks, Is.EqualTo(1));
        GitRepository module = new(f.Root.PathInRepository(Fixture.ModulePath)); Assert.That(await module.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.NewModuleHead));
        Assert.That((await module.GetSubmodulesAsync()).Single().IsInitialized, Is.True); Assert.That(new SubmoduleCheckoutPreferencesStore().Load().Decision, Is.Null); second.Close();
    }

    [AvaloniaTest]
    public async Task CancellingTheInitializedSubmoduleQuestionKeepsItsOldCheckoutAndNeverRecordsNoOrCallsTheHook()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.AddModulesAsync(); int callbacks = 0;
        PullDialog dialog = await f.OpenAsync(afterUpdate: _ => { callbacks++; return Task.CompletedTask; }); dialog.RequestedThemeVariant = ThemeVariant.Dark;
        using CancellationTokenSource cancellation = new(); Task execution = dialog.ExecuteAsync(dialog, cancellationToken: cancellation.Token);
        Window prompt = await Owned(dialog, ""); Assert.That(prompt, Is.TypeOf<CheckoutSubmodulePrompt>()); Assert.That(prompt.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
        Pointer(prompt, "RememberSubmoduleCheckout"); cancellation.Cancel(); await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(dialog.LastError, Is.EqualTo("Operation cancelled")); Assert.That(new SubmoduleCheckoutPreferencesStore().Load().Decision, Is.Null); Assert.That(callbacks, Is.Zero);
        Assert.That(await new GitRepository(f.Root.PathInRepository(Fixture.ModulePath)).ResolveRevisionAsync("HEAD"), Is.EqualTo(f.OldModuleHead)); Assert.That(dialog.OwnedWindows, Is.Empty); dialog.Close();
    }

    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Pointer(Window window, string name)
    {
        Pump(window); Control target = Field<Control>(window, name); Assert.That(target.IsEnabled && target.IsEffectivelyVisible, Is.True, name);
        Point point = target.TranslatePoint(new Point(Math.Min(12, target.Bounds.Width / 2), target.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static void Alt(Window window, PhysicalKey key)
    { Pump(window); window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.Alt); window.KeyPressQwerty(key, RawInputModifiers.Alt); window.KeyReleaseQwerty(key, RawInputModifiers.Alt); window.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> predicate) { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); while (!predicate()) { await Task.Delay(20, timeout.Token); } }
    private static async Task<Window> Owned(Window owner, string name) { await Until(() => owner.OwnedWindows.Any(window => name.Length == 0 || window.Name == name)); Window result = owner.OwnedWindows.Single(window => name.Length == 0 || window.Name == name); Pump(result); return result; }
    private static async Task Closed(PullDialog dialog) { await Until(() => !dialog.IsVisible && !dialog.IsBusy); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Assert.That(dialog.Succeeded, Is.True); }
    private static void Capture(Window window, string name) { Pump(window); using WriteableBitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<(PullDialog Dialog, Task Shown)> _dialogs = [];
        private readonly Window _owner = new() { Width = 1000, Height = 850 };
        internal const string ModulePath = "modules/[literal] Ω";
        internal const string NestedPath = "nested/[literal] 雪";
        internal ReferenceFixture Root { get; }
        internal GitRepository Repository => Root.Repository;
        internal GitRepository Server { get; }
        internal GitRepository Writer { get; }
        internal string Initial { get; }
        internal string OldModuleHead { get; private set; } = "";
        internal string NewModuleHead { get; private set; } = "";
        private Fixture(ReferenceFixture root, GitRepository server, GitRepository writer, string initial) { Root = root; Server = server; Writer = writer; Initial = initial; _owner.Show(); }
        internal static async Task<Fixture> CreateAsync()
        {
            ReferenceFixture root = await ReferenceFixture.CreateAsync();
            try
            {
                GitRepository server = new(await root.CreateRemoteAsync()); await root.GitAsync("branch", "--set-upstream-to", "origin/main");
                GitRepository writer = await GitRepository.CloneAsync(server.Path, root.Output("private writer"));
                await Configure(writer, root); return new(root, server, writer, await root.Repository.ResolveRevisionAsync("HEAD"));
            }
            catch { root.Dispose(); throw; }
        }
        internal async Task<PullDialog> OpenAsync(GitPullAction action = GitPullAction.Merge, GitRepository? repository = null, string? branch = null, Func<CancellationToken, Task>? afterUpdate = null)
        {
            PullDialog dialog = new(repository ?? Repository, action, "origin", branch, afterUpdate);
            Task shown = dialog.ShowDialog(_owner); _dialogs.Add((dialog, shown)); await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Pump(dialog); return dialog;
        }
        internal async Task<GitResult> Git(GitRepository repository, params string[] arguments)
        { GitResult result = await repository.RunAsync(arguments); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        internal async Task<string> AdvanceAsync(string file)
        { await Commit(Writer, file, "remote " + file + "\n", "Advance " + file); await Writer.PushAsync("origin", "main"); return await Writer.ResolveRevisionAsync("HEAD"); }
        internal async Task<GitRepository> ShallowAsync()
        {
            string path = Root.Output("shallow " + Guid.NewGuid().ToString("N")); await Git(Repository, "clone", "--depth=1", new Uri(Server.Path).AbsoluteUri, path);
            GitRepository repository = new(path); await Configure(repository, Root); return repository;
        }
        internal async Task AddModulesAsync()
        {
            Root.SetEnvironment("GIT_ALLOW_PROTOCOL", "file");
            GitRepository child = await GitRepository.InitAsync(Root.Output("private nested source")); await Configure(child, Root); await Commit(child, "file.txt", "nested base\n", "Nested base");
            GitRepository module = await GitRepository.InitAsync(Root.Output("private module source")); await Configure(module, Root); await Commit(module, "file.txt", "module base\n", "Module base");
            await Git(module, "submodule", "add", "--name", "nested", "--", child.Path, NestedPath); await module.StageAllAsync(); await module.CommitAsync("Add nested child"); OldModuleHead = await module.ResolveRevisionAsync("HEAD");
            await Git(Repository, "submodule", "add", "--name", "literal-module", "--", module.Path, ModulePath); await Repository.StageAllAsync(); await Repository.CommitAsync("Add module"); await Repository.PushAsync("origin", "main");
            await Writer.PullMergeAsync("origin", "main");
            await Commit(module, "file.txt", "module next\n", "Module next"); NewModuleHead = await module.ResolveRevisionAsync("HEAD");
            await Git(Writer, "update-index", "--cacheinfo", "160000," + NewModuleHead + "," + ModulePath); await Writer.CommitAsync("Advance module pointer"); await Writer.PushAsync("origin", "main");
        }
        private static async Task Configure(GitRepository repository, ReferenceFixture root)
        { await repository.SetConfigAsync("user.name", "Pull Tester"); await repository.SetConfigAsync("user.email", "pull@example.invalid"); await repository.SetConfigAsync("commit.gpgsign", "false"); await repository.SetConfigAsync("core.hooksPath", root.Output("no-hooks")); }
        private static async Task Commit(GitRepository repository, string file, string contents, string subject)
        { string path = Path.Combine(repository.Path, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, contents); await repository.StageAllAsync(); await repository.CommitAsync(subject); }
        public async ValueTask DisposeAsync()
        {
            foreach ((PullDialog dialog, _) in _dialogs) { if (dialog.IsVisible) { dialog.Close(); } }
            foreach ((PullDialog _, Task shown) in _dialogs) { await shown.WaitAsync(TimeSpan.FromSeconds(10)); }
            _owner.Close(); Dispatcher.UIThread.RunJobs(); Root.Dispose();
        }
    }
}

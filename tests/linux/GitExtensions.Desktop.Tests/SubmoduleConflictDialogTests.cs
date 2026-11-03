using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using RootFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class SubmoduleConflictDialogTests
{
    [AvaloniaTest]
    public async Task OriginalFixedGeometryFourReadonlyHashesAndIconsRenderInBothThemesAndThePathCopiesLiterally()
    {
        await using Fixture f = await Fixture.CreateAsync();
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            SubmoduleConflictDialog dialog = await f.ShowAsync(theme: theme);
            Assert.That(dialog.ClientSize, Is.EqualTo(new Size(595, 254))); Assert.That(dialog.CanResize, Is.False);
            Assert.That(Field<Button>(dialog, "btOpenSubmodule").IsDefault, Is.True);
            Assert.That(dialog.GetVisualDescendants().OfType<Button>().Count(button => button.Name?.StartsWith("bt", StringComparison.Ordinal) == true), Is.EqualTo(4));
            foreach ((string name, Rect expected) in new[] { ("tbBase", new Rect(106, 49, 438, 27)), ("tbLocal", new Rect(106, 82, 438, 27)),
                ("tbRemote", new Rect(106, 119, 438, 27)), ("tbCurrent", new Rect(106, 155, 438, 27)), ("btOpenSubmodule", new Rect(19, 212, 176, 31)),
                ("btCheckoutBranch", new Rect(203, 212, 181, 31)), ("btStageCurrent", new Rect(392, 212, 190, 31)), ("btRefresh", new Rect(552, 152, 34, 31)) })
            { Assert.That(Bounds(dialog, Field<Control>(dialog, name)), Is.EqualTo(expected), name); }
            foreach (string name in new[] { "tbBase", "tbLocal", "tbRemote", "tbCurrent" }) { Assert.That(Field<TextBox>(dialog, name).IsReadOnly, Is.True); }
            AssertHashes(dialog, f.Base, f.Local, f.Remote, f.Base);
            Assert.That(Field<Button>(dialog, "btOpenSubmodule").GetVisualDescendants().OfType<Image>().Single().Tag, Is.EqualTo("FolderSubmodule.png"));
            Assert.That(Field<Button>(dialog, "btRefresh").GetVisualDescendants().OfType<Image>().Single().Tag, Is.EqualTo("ReloadRevisions.png"));
            var clipboard = dialog.Clipboard!; string? before = await ClipboardExtensions.TryGetTextAsync(clipboard);
            try
            {
                Field<TextBox>(dialog, "lbSubmodule").Focus(); Press(dialog, PhysicalKey.A, RawInputModifiers.Control); Press(dialog, PhysicalKey.C, RawInputModifiers.Control);
                await Until(async () => await ClipboardExtensions.TryGetTextAsync(clipboard) == Fixture.ModulePath);
                Assert.That(Field<TextBox>(dialog, "lbSubmodule").Text, Is.EqualTo(Fixture.ModulePath));
            }
            finally { await ClipboardExtensions.SetTextAsync(clipboard, before ?? ""); }
            Capture(dialog, "submodule-conflict-" + theme + ".png"); dialog.Close();
        }
    }

    [AvaloniaTest]
    public async Task EnterOpensAnIndependentBrowserAtTheExactChildRootAndKeepsTheAssistantAndIndexUntouched()
    {
        await using Fixture f = await Fixture.CreateAsync(); List<string> opened = [];
        SubmoduleConflictDialog dialog = await f.ShowAsync(open: (path, _) => { opened.Add(path); return Task.CompletedTask; }); string before = await f.IndexAsync();
        Field<TextBox>(dialog, "lbSubmodule").Focus(); Press(dialog, PhysicalKey.Enter); await Until(() => opened.Count == 1 && !dialog.IsBusy);
        Assert.That(opened.Single(), Is.EqualTo(f.Module.Path)); Assert.That(Path.IsPathFullyQualified(opened[0]), Is.True);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.Resolved || dialog.HasRepositoryChanges, Is.False); Assert.That(await f.IndexAsync(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task BrowseLauncherUsesTheInstalledApphostOrDotnetWithLiteralArgumentListAndNeverTheTesthost()
    {
        using RootFixture f = await RootFixture.CreateAsync(); string app = f.Output("private apphost directory"); Directory.CreateDirectory(app);
        string repository = f.PathInRepository("literal ' λ\tline\nend"); string host = Path.Combine(app, OperatingSystem.IsWindows() ? "GitExtensions.exe" : "GitExtensions");
        await File.WriteAllTextAsync(host, "private fixture placeholder"); ProcessStartInfo installed = BrowseApplicationLauncher.CreateStartInfo(repository, app);
        Assert.That(installed.FileName, Is.EqualTo(host)); Assert.That(installed.UseShellExecute, Is.False); Assert.That(installed.ArgumentList, Is.EqualTo(new[] { repository }));
        Assert.That(installed.WorkingDirectory, Is.EqualTo(repository)); File.Delete(host);
        ProcessStartInfo development = BrowseApplicationLauncher.CreateStartInfo(repository, app);
        Assert.That(Path.GetFileNameWithoutExtension(development.FileName), Is.EqualTo("dotnet")); Assert.That(development.UseShellExecute, Is.False);
        Assert.That(development.ArgumentList, Has.Count.EqualTo(2)); Assert.That(development.ArgumentList[0], Does.EndWith("GitExtensions.dll")); Assert.That(development.ArgumentList[1], Is.EqualTo(repository));
        Assert.Throws<ArgumentException>(() => BrowseApplicationLauncher.CreateStartInfo("relative"));
    }

    [AvaloniaTest]
    public async Task ActualBrowseProcessReceivesLiteralCwdAndArgvAndSurvivesClosingTheAssistant()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("The private executable launcher fixture uses a Linux shebang and procfs."); return; }
        await using Fixture f = await Fixture.CreateAsync(); string app = f.Root.Output("independent browse app"); Directory.CreateDirectory(app);
        string host = Path.Combine(app, "GitExtensions"), marker = host + ".started", release = host + ".release";
        await File.WriteAllTextAsync(host, "#!/usr/bin/python3\nimport os,sys,json,time\nwith open(__file__+'.started.tmp','w') as f: json.dump({'pid':os.getpid(),'cwd':os.getcwd(),'argv':sys.argv[1:]},f)\nos.replace(__file__+'.started.tmp',__file__+'.started')\nwhile not os.path.exists(__file__+'.release'): time.sleep(.02)\n");
        File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        SubmoduleConflictDialog dialog = await f.ShowAsync(open: (path, token) => BrowseApplicationLauncher.OpenAsync(path, token, app));
        Pointer(dialog, "btOpenSubmodule"); await Until(() => File.Exists(marker) && !dialog.IsBusy);
        using JsonDocument data = JsonDocument.Parse(await File.ReadAllTextAsync(marker)); Actor actor = Actor.Read(data.RootElement.GetProperty("pid").GetInt32()); f.Actors.Add(actor);
        Assert.That(data.RootElement.GetProperty("cwd").GetString(), Is.EqualTo(f.Module.Path));
        Assert.That(data.RootElement.GetProperty("argv").EnumerateArray().Select(value => value.GetString()), Is.EqualTo(new[] { f.Module.Path }));
        Assert.That(actor.Alive(), Is.True); Assert.That(dialog.IsVisible, Is.True);
        dialog.Close(); Pump(dialog); Assert.That(actor.Alive(), Is.True, "The independent Browse must not be awaited or killed by the assistant.");
        await File.WriteAllTextAsync(release, "exit"); await Until(() => !actor.Alive());
    }

    [AvaloniaTest]
    public async Task RefreshChangesOnlyCurrentAndRefusesAReplacementOfTheCapturedStages()
    {
        await using Fixture f = await Fixture.CreateAsync(); SubmoduleConflictDialog dialog = await f.ShowAsync();
        await Fixture.GitAsync(f.Module, "checkout", "--detach", f.Local); Pointer(dialog, "btRefresh"); await Idle(dialog);
        AssertHashes(dialog, f.Base, f.Local, f.Remote, f.Local);
        await f.SetStagesAsync(f.Base, f.Remote, f.Local); string changed = await f.IndexAsync(); Pointer(dialog, "btRefresh"); await Idle(dialog);
        Assert.That(dialog.LastException, Is.TypeOf<InvalidOperationException>()); AssertHashes(dialog, f.Base, f.Local, f.Remote, f.Local);
        foreach (string name in new[] { "btOpenSubmodule", "btCheckoutBranch", "btStageCurrent" }) { Assert.That(Field<Button>(dialog, name).IsEnabled, Is.False); }
        Assert.That(dialog.IsVisible, Is.True); Assert.That(await f.IndexAsync(), Is.EqualTo(changed));
    }

    [AvaloniaTest]
    public async Task StageCurrentAcceptsAnUnrelatedRealChildCommitClearsOnlyItsUnmergedStagesAndNeverCommitsTheParent()
    {
        await using Fixture f = await Fixture.CreateAsync(); await Fixture.GitAsync(f.Module, "checkout", "--detach", f.Unrelated);
        SubmoduleConflictDialog dialog = await f.ShowAsync(); string parent = await f.Root.Repository.ResolveRevisionAsync("HEAD");
        Pointer(dialog, "btStageCurrent"); await Until(() => !dialog.IsBusy && !dialog.IsVisible);
        Assert.That(dialog.Resolved, Is.True); Assert.That(dialog.LastStageResult?.IsResolved, Is.True); Assert.That(dialog.LastStageResult?.StagedCommit, Is.EqualTo(f.Unrelated));
        Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(await f.IndexAsync(), Is.EqualTo("160000 " + f.Unrelated + " 0\t" + Fixture.ModulePath + "\0"));
        Assert.That(await f.Root.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(parent)); Assert.That(await f.Module.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Unrelated));
    }

    [AvaloniaTest]
    public async Task MissingStagesUseTheOriginalFrenchDeletedTextAndMissingChildNeverFallsBackToTheParentHead()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.SetStagesAsync(null, null, f.Remote); CultureInfo before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            SubmoduleConflictDialog dialog = await f.ShowAsync(); Assert.That(dialog.Title, Is.EqualTo("Conflit de sous module"));
            Assert.That(Field<TextBox>(dialog, "tbBase").Text, Is.EqualTo("supprimé")); Assert.That(Field<TextBox>(dialog, "tbLocal").Text, Is.EqualTo("supprimé"));
            Assert.That(Field<Button>(dialog, "btCheckoutBranch").IsEnabled, Is.False); Assert.That(Field<Button>(dialog, "btStageCurrent").IsEnabled, Is.True);
            dialog.Close(); File.Delete(Path.Combine(f.Module.Path, ".git"));
            SubmoduleConflictDialog missing = await f.ShowAsync(); Assert.That(Field<TextBox>(missing, "tbCurrent").Text, Is.Null.Or.Empty);
            Assert.That(missing.Snapshot?.Current, Is.Null); Assert.That(missing.Snapshot?.CanOpen, Is.False);
            Assert.That(Field<Button>(missing, "btOpenSubmodule").IsEnabled || Field<Button>(missing, "btStageCurrent").IsEnabled, Is.False);
            Assert.That(await f.Root.Repository.ResolveRevisionAsync("HEAD"), Is.Not.Null.And.Not.Empty); Capture(missing, "submodule-conflict-French-missing-child.png");
        }
        finally { CultureInfo.CurrentUICulture = before; }
    }

    [AvaloniaTest]
    public async Task SharedCheckoutFiltersToTheIntersectionOfBothSidesAndStagesOnlyAfterItsCompleteSuccess()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.LocalCandidatesAsync(2); SubmoduleConflictDialog dialog = await f.ShowAsync();
        string parent = await f.Root.Repository.ResolveRevisionAsync("HEAD"); Pointer(dialog, "btCheckoutBranch"); CheckoutBranchDialog checkout = await CheckoutAsync(dialog);
        ComboBox choices = Field<ComboBox>(checkout, "CheckoutBranchSelection"); GitReference[] refs = choices.Items.OfType<GitReference>().ToArray();
        Assert.That(refs.Select(reference => reference.ShortName), Is.EquivalentTo(new[] { "resolved-one", "resolved-two" })); Assert.That(choices.SelectedItem, Is.Null);
        Assert.That(Field<Button>(checkout, "CheckoutBranch").IsEnabled, Is.False); Assert.That(Field<RadioButton>(checkout, "CheckoutRevisionMode").IsVisible, Is.False);
        choices.SelectedItem = refs.Single(reference => reference.ShortName == "resolved-one"); Pointer(checkout, "CheckoutBranch"); await Until(() => !checkout.IsBusy && !dialog.IsBusy && !dialog.IsVisible);
        Assert.That(checkout.Succeeded, Is.True); Assert.That(checkout.LastResult?.Result.Success, Is.True); Assert.That(dialog.Resolved, Is.True);
        Assert.That(await f.Module.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Merge)); Assert.That(await f.IndexAsync(), Is.EqualTo("160000 " + f.Merge + " 0\t" + Fixture.ModulePath + "\0"));
        Assert.That(await f.Root.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(parent));
    }

    [AvaloniaTest]
    public async Task UniqueLocalAndRemoteOnlyChoicesArePreselectedAndCancellationNeverStages()
    {
        foreach (bool remoteOnly in new[] { false, true })
        {
            await using Fixture f = await Fixture.CreateAsync(); if (!remoteOnly) { await f.LocalCandidatesAsync(1); }
            else { await Fixture.GitAsync(f.Module, "update-ref", "-d", "refs/remotes/origin/merged-two"); }
            SubmoduleConflictDialog dialog = await f.ShowAsync(); string before = await f.IndexAsync(); Pointer(dialog, "btCheckoutBranch"); CheckoutBranchDialog checkout = await CheckoutAsync(dialog);
            ComboBox choices = Field<ComboBox>(checkout, "CheckoutBranchSelection"); Assert.That(choices.Items.Count, Is.EqualTo(1)); Assert.That(choices.SelectedItem, Is.TypeOf<GitReference>());
            Assert.That(((GitReference)choices.SelectedItem!).IsRemote, Is.EqualTo(remoteOnly)); Assert.That(Field<Button>(checkout, "CheckoutBranch").IsEnabled, Is.True);
            Pointer(checkout, "CancelCheckoutBranch"); await Idle(dialog); Assert.That(checkout.Succeeded || dialog.Resolved, Is.False); Assert.That(await f.IndexAsync(), Is.EqualTo(before));
        }
    }

    [AvaloniaTest]
    public async Task CancelCheckoutRefreshesCurrentWithoutReplacingStagesOrClaimingAnExternalChange()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.LocalCandidatesAsync(1); SubmoduleConflictDialog dialog = await f.ShowAsync(); string before = await f.IndexAsync();
        Pointer(dialog, "btCheckoutBranch"); CheckoutBranchDialog checkout = await CheckoutAsync(dialog);
        await Fixture.GitAsync(f.Module, "checkout", "--detach", f.Local); Pointer(checkout, "CancelCheckoutBranch"); await Idle(dialog);
        AssertHashes(dialog, f.Base, f.Local, f.Remote, f.Local); Assert.That(dialog.Resolved || dialog.HasRepositoryChanges, Is.False); Assert.That(await f.IndexAsync(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task MovingAnAdvertisedBranchBeforeCheckoutFailsWithoutStagingAndKeepsTheAssistantRecoverable()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.LocalCandidatesAsync(1); SubmoduleConflictDialog dialog = await f.ShowAsync(); string before = await f.IndexAsync();
        Pointer(dialog, "btCheckoutBranch"); CheckoutBranchDialog checkout = await CheckoutAsync(dialog);
        GitReference advertised = (GitReference)Field<ComboBox>(checkout, "CheckoutBranchSelection").SelectedItem!;
        await Fixture.GitAsync(f.Module, "update-ref", advertised.Name, f.Local); Pointer(checkout, "CheckoutBranch"); await Until(() => !checkout.IsBusy && checkout.LastError is not null);
        Assert.That(checkout.Succeeded, Is.False); Assert.That(checkout.IsVisible, Is.True); Assert.That(await f.Module.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Base));
        Pointer(checkout, "CancelCheckoutBranch"); await Idle(dialog);
        Assert.That(dialog.Resolved, Is.False); Assert.That(dialog.IsVisible, Is.True); Assert.That(await f.IndexAsync(), Is.EqualTo(before)); AssertHashes(dialog, f.Base, f.Local, f.Remote, f.Base);
    }

    [AvaloniaTest]
    public async Task CancellingTheNestedModuleQuestionAfterNativeCheckoutNeverStagesDespiteASuccessfulLastResult()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.AddNestedCandidateAsync(); await f.LocalCandidatesAsync(1);
        SubmoduleConflictDialog dialog = await f.ShowAsync(); string before = await f.IndexAsync(); Pointer(dialog, "btCheckoutBranch"); CheckoutBranchDialog checkout = await CheckoutAsync(dialog);
        Pointer(checkout, "CheckoutBranch"); await Until(() => checkout.OwnedWindows.Any(window => window is CheckoutSubmodulePrompt));
        Window prompt = checkout.OwnedWindows.Single(window => window is CheckoutSubmodulePrompt); prompt.Close();
        await Until(() => !checkout.IsBusy); Assert.That(checkout.LastResult?.Result.Success, Is.True); Assert.That(checkout.Succeeded, Is.False);
        Assert.That(checkout.IsVisible, Is.True); Assert.That(await f.Module.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Merge)); Assert.That(await f.IndexAsync(), Is.EqualTo(before));
        Pointer(checkout, "CancelCheckoutBranch"); await Idle(dialog);
        Assert.That(dialog.Resolved, Is.False); Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(dialog.IsVisible, Is.True);
        AssertHashes(dialog, f.Base, f.Local, f.Remote, f.Merge); Assert.That(await f.IndexAsync(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task NativeStageFailureRetainsTheAssistantAndCanRetryAfterTheUserRemovesTheIndexLock()
    {
        await using Fixture f = await Fixture.CreateAsync(); SubmoduleConflictDialog dialog = await f.ShowAsync(); string before = await f.IndexAsync();
        string indexLock = f.Root.PathInRepository(".git/index.lock"); await File.WriteAllTextAsync(indexLock, "private user-owned lock\n");
        Pointer(dialog, "btStageCurrent"); await Idle(dialog); Assert.That(dialog.LastException, Is.TypeOf<GitCommandException>()); Assert.That(dialog.Resolved, Is.False);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.LastStageResult?.Result.Success, Is.False); Assert.That(await f.IndexAsync(), Is.EqualTo(before));
        Assert.That(await File.ReadAllTextAsync(indexLock), Is.EqualTo("private user-owned lock\n")); Assert.That(Field<Button>(dialog, "btStageCurrent").IsEnabled, Is.True);
        File.Delete(indexLock); Pointer(dialog, "btStageCurrent"); await Until(() => !dialog.IsBusy && !dialog.IsVisible); Assert.That(dialog.Resolved, Is.True);
    }

    [AvaloniaTest]
    public async Task ClosingTheAssistantOrItsOwnerDrainsTheNativeStageProcessAndNeverPublishesResolution()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Native actor identities require Linux procfs."); }
        foreach (bool ownerClose in new[] { false, true })
        {
            await using Fixture f = await Fixture.CreateAsync(); string wrapper = await f.BlockStageAsync(); Window owner = new() { Width = 900, Height = 500 }; owner.Show(); f.Windows.Add(owner);
            SubmoduleConflictDialog dialog = await f.ShowAsync(repository: new(f.Root.Repository.Path, new GitRunner(wrapper)), owner: owner); string before = await f.IndexAsync();
            Pointer(dialog, "btStageCurrent"); await Until(() => File.Exists(wrapper + ".actors")); Actor[] actors = File.ReadAllText(wrapper + ".actors").Split(' ').Select(value => Actor.Read(int.Parse(value, CultureInfo.InvariantCulture))).ToArray(); f.Actors.AddRange(actors);
            Assert.That(dialog.IsBusy, Is.True); Assert.That(actors.All(actor => actor.Alive()), Is.True); if (ownerClose) { owner.Close(); } else { dialog.Close(); }
            await Until(() => !dialog.IsVisible && !dialog.IsBusy && actors.All(actor => !actor.Alive()) && (!ownerClose || !owner.IsVisible));
            Assert.That(dialog.Resolved, Is.False); Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(await f.IndexAsync(), Is.EqualTo(before)); Assert.That(await f.Module.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Base));
        }
    }

    private static T Field<T>(TopLevel root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pointer(Window root, string name) => PushProcessTestDriver.Pointer(Field<Control>(root, name));
    private static void Press(Window root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) { root.KeyPressQwerty(key, modifiers); if (root.IsVisible) { root.KeyReleaseQwerty(key, modifiers); } Pump(root); }
    private static void Pump(Window root) { for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } }
    private static Rect Bounds(Window root, Control control) => new(control.TranslatePoint(default, root)!.Value, control.Bounds.Size);
    private static void AssertHashes(Window root, string? @base, string? local, string? remote, string? current)
    { Assert.That(Field<TextBox>(root, "tbBase").Text, Is.EqualTo(@base)); Assert.That(Field<TextBox>(root, "tbLocal").Text, Is.EqualTo(local)); Assert.That(Field<TextBox>(root, "tbRemote").Text, Is.EqualTo(remote)); Assert.That(Field<TextBox>(root, "tbCurrent").Text, Is.EqualTo(current)); }
    private static async Task Until(Func<bool> condition) { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(25)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True, "Submodule conflict workflow did not settle."); }
    private static async Task Until(Func<Task<bool>> condition) { Stopwatch timer = Stopwatch.StartNew(); bool ready = await condition(); while (!ready && timer.Elapsed < TimeSpan.FromSeconds(5)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); ready = await condition(); } Assert.That(ready, Is.True); }
    private static Task Idle(RepositoryDialog dialog) => Until(() => !dialog.IsBusy);
    private static async Task<CheckoutBranchDialog> CheckoutAsync(Window root) { await Until(() => root.OwnedWindows.OfType<CheckoutBranchDialog>().Any(dialog => !dialog.IsBusy)); CheckoutBranchDialog dialog = root.OwnedWindows.OfType<CheckoutBranchDialog>().Single(); Pump(dialog); return dialog; }
    private static void Capture(Window root, string name) { Pump(root); using var frame = root.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string folder = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); frame!.Save(path); TestContext.AddTestAttachment(path); }

    private sealed record Actor(int Pid, string Start)
    {
        private static string[] Fields(int pid) { string text = File.ReadAllText("/proc/" + pid + "/stat"); return text[(text.LastIndexOf(')') + 2)..].Split(' '); }
        internal static Actor Read(int pid) => new(pid, Fields(pid)[19]);
        internal bool Alive() { try { string[] fields = Fields(Pid); return fields[0] != "Z" && fields[19] == Start; } catch (IOException) { return false; } }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string ModulePath = "module [x]* λ\tline\nend";
        internal RootFixture Root { get; }
        internal GitRepository Source { get; private set; } = null!;
        internal GitRepository Module => new(Root.PathInRepository(ModulePath));
        internal string Base { get; private set; } = "";
        internal string Local { get; private set; } = "";
        internal string Remote { get; private set; } = "";
        internal string Merge { get; private set; } = "";
        internal string Unrelated { get; private set; } = "";
        internal List<Window> Windows { get; } = [];
        internal List<Actor> Actors { get; } = [];
        private Fixture(RootFixture root) => Root = root;
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture f = new(await RootFixture.CreateAsync());
            try
            {
                f.Root.SetEnvironment("GIT_ALLOW_PROTOCOL", "file"); SourceSetup(f);
                f.Source = await GitRepository.InitAsync(f.Root.Output("source.git"), new GitInitOptions("main"));
                await f.Source.SetConfigAsync("user.name", "Submodule Conflict UI"); await f.Source.SetConfigAsync("user.email", "submodule-conflict@example.invalid"); await f.Source.SetConfigAsync("commit.gpgsign", "false"); await f.Source.SetConfigAsync("core.hooksPath", f.Root.Output("no-hooks"));
                await File.WriteAllTextAsync(Path.Combine(f.Source.Path, "file.txt"), "base bytes\n"); await f.Source.StageAllAsync(); await f.Source.CommitAsync("Base"); f.Base = await f.Source.ResolveRevisionAsync("HEAD");
                string tree = await TextAsync(f.Source, "rev-parse", "HEAD^{tree}");
                f.Local = await TextAsync(f.Source, "commit-tree", tree, "-p", f.Base, "-m", "Local"); f.Remote = await TextAsync(f.Source, "commit-tree", tree, "-p", f.Base, "-m", "Remote");
                f.Merge = await TextAsync(f.Source, "commit-tree", tree, "-p", f.Local, "-p", f.Remote, "-m", "Merge both sides"); f.Unrelated = await TextAsync(f.Source, "commit-tree", tree, "-m", "Independent root");
                foreach ((string branch, string hash) in new[] { ("local", f.Local), ("remote", f.Remote), ("merged-one", f.Merge), ("merged-two", f.Merge), ("unrelated", f.Unrelated) }) { await GitAsync(f.Source, "update-ref", "refs/heads/" + branch, hash); }
                await f.Root.GitAsync("-c", "protocol.file.allow=always", "submodule", "add", "--name", "literal-module", "--", f.Source.Path, ModulePath); await f.Root.Repository.StageAllAsync(); await f.Root.Repository.CommitAsync("Module");
                await f.SetStagesAsync(f.Base, f.Local, f.Remote); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        private static void SourceSetup(Fixture f) { f.Root.SetEnvironment("GIT_TERMINAL_PROMPT", "0"); }
        internal async Task LocalCandidatesAsync(int count) { if (count > 0) { await GitAsync(Module, "branch", "resolved-one", "origin/merged-one"); } if (count > 1) { await GitAsync(Module, "branch", "resolved-two", "origin/merged-two"); } }
        internal async Task AddNestedCandidateAsync()
        {
            GitRepository leaf = await GitRepository.InitAsync(Root.Output("nested-source.git"), new GitInitOptions("main"));
            await leaf.SetConfigAsync("user.name", "Nested UI"); await leaf.SetConfigAsync("user.email", "nested@example.invalid"); await leaf.SetConfigAsync("commit.gpgsign", "false"); await leaf.SetConfigAsync("core.hooksPath", Root.Output("no-hooks"));
            await File.WriteAllTextAsync(Path.Combine(leaf.Path, "file.txt"), "nested bytes\n"); await leaf.StageAllAsync(); await leaf.CommitAsync("Nested source");
            await GitAsync(Source, "-c", "protocol.file.allow=always", "submodule", "add", "--name", "nested", "--", leaf.Path, "nested");
            string tree = await TextAsync(Source, "write-tree"); Merge = await TextAsync(Source, "commit-tree", tree, "-p", Merge, "-m", "Nested module after merge");
            await GitAsync(Source, "update-ref", "refs/heads/merged-one", Merge); await GitAsync(Source, "update-ref", "refs/heads/merged-two", Merge); await GitAsync(Module, "fetch", "origin");
        }
        internal async Task SetStagesAsync(string? @base, string? local, string? remote)
        {
            string input = "0 " + new string('0', 40) + "\t" + ModulePath + "\0";
            foreach ((string? hash, int stage) in new[] { (@base, 1), (local, 2), (remote, 3) }) { if (hash is not null) { input += "160000 " + hash + " " + stage + "\t" + ModulePath + "\0"; } }
            GitResult result = await new GitRunner().RunAsync(Root.Repository.Path, ["update-index", "-z", "--index-info"], standardInput: input); Assert.That(result.Success, Is.True, result.StandardError);
        }
        internal async Task<string> IndexAsync() => (await Root.GitAsync("--literal-pathspecs", "ls-files", "--stage", "-z", "--", ModulePath)).StandardOutput;
        internal async Task<SubmoduleConflictDialog> ShowAsync(ThemeVariant? theme = null, Func<string, CancellationToken, Task>? open = null, GitRepository? repository = null, Window? owner = null)
        {
            SubmoduleConflictDialog dialog = new(repository ?? Root.Repository, ModulePath, open ?? ((_, _) => Task.CompletedTask)) { RequestedThemeVariant = theme ?? ThemeVariant.Light };
            Windows.Add(dialog); if (owner is null) { dialog.Show(); } else { _ = dialog.ShowDialog(owner); } await dialog.InitializeAsync(); Pump(dialog); return dialog;
        }
        internal async Task<string> BlockStageAsync()
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
            string path = Root.Output("private-stage-git"); await File.WriteAllTextAsync(path, "#!/usr/bin/python3\nimport sys,os,subprocess,time\na=sys.argv[1:]\nif 'add' in a:\n p=subprocess.Popen(['/usr/bin/sleep','60'])\n"
                + TestSignalFile.Python("__file__+'.actors'", "str(os.getpid())+' '+str(p.pid)", 1)
                + " time.sleep(60)\nos.execv('/usr/bin/git',['git']+a)\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
        }
        internal static async Task<GitResult> GitAsync(GitRepository repository, params string[] args) { GitResult result = await repository.RunAsync(args); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        private static async Task<string> TextAsync(GitRepository repository, params string[] args) => (await GitAsync(repository, args)).StandardOutput.TrimEnd('\r', '\n');
        public async ValueTask DisposeAsync()
        {
            try { foreach (Window window in Windows.AsEnumerable().Reverse()) { Close(window); } await Until(() => Windows.All(window => !window.IsVisible && (window is not RepositoryDialog dialog || !dialog.IsBusy))); }
            finally { foreach (Actor actor in Actors.Where(actor => actor.Alive())) { try { Process.GetProcessById(actor.Pid).Kill(entireProcessTree: true); } catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { } } Root.Dispose(); }
        }
        private static void Close(Window window) { foreach (Window child in window.OwnedWindows.ToArray()) { Close(child); } if (window.IsVisible) { window.Close(); } }
    }
}

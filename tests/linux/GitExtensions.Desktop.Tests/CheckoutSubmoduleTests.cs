using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class CheckoutSubmoduleTests
{
    [AvaloniaTest]
    public async Task NoLeavesSubmodulesAtTheirOldCommitAndAnUnrememberedChoiceIsAskedAgain()
    {
        using SubmoduleFixture fixture = await SubmoduleFixture.CreateAsync();
        int callbacks = 0;
        CheckoutBranchDialog dialog = await fixture.OpenAsync("target", afterUpdate: _ => { callbacks++; return Task.CompletedTask; });
        try
        {
            Click(dialog, "CheckoutBranch");
            CheckoutSubmodulePrompt prompt = await OwnedAsync<CheckoutSubmodulePrompt>(dialog);
            Assert.That(Field<TextBlock>(prompt, "CheckoutSubmoduleHeading").Text, Is.EqualTo("Update submodules on checkout?"));
            Assert.That(Field<CheckBox>(prompt, "RememberSubmoduleCheckout").Bounds.Height, Is.EqualTo(24));
            Capture(prompt, "classic-checkout-submodules.png");
            Click(prompt, "SkipCheckoutSubmodules"); await ClosedAsync(dialog);
            Assert.That(await fixture.ModuleHeadAsync(), Is.EqualTo(fixture.OldModuleHead));
            Assert.That(callbacks, Is.Zero);
            Assert.That(new SubmoduleCheckoutPreferencesStore().Load().Decision, Is.Null);
        }
        finally { dialog.Close(); }
        dialog = await fixture.OpenAsync("main");
        try
        {
            Click(dialog, "CheckoutBranch");
            CheckoutSubmodulePrompt prompt = await OwnedAsync<CheckoutSubmodulePrompt>(dialog);
            Click(prompt, "SkipCheckoutSubmodules"); await ClosedAsync(dialog);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task YesInitializesLiteralModulesRecursivelyAndCallsTheHookAfterTheSuccessfulUpdate()
    {
        using SubmoduleFixture fixture = await SubmoduleFixture.CreateAsync(nested: true);
        await fixture.Root.GitAsync("--literal-pathspecs", "submodule", "deinit", "--force", "--", fixture.ModulePath);
        Assert.That((await fixture.Repository.GetSubmodulesAsync()).Single().IsInitialized, Is.False);
        int callbacks = 0;
        CheckoutBranchDialog dialog = await fixture.OpenAsync("target", afterUpdate: async token =>
        {
            Assert.That(await fixture.ModuleHeadAsync(token), Is.EqualTo(fixture.NewModuleHead));
            GitSubmodule child = (await new GitRepository(fixture.ModuleDirectory).GetSubmodulesAsync(token)).Single();
            Assert.That(child.Path, Is.EqualTo(SubmoduleFixture.NestedPath));
            Assert.That(child.IsInitialized, Is.True);
            Assert.That(child.Head, Is.EqualTo(child.ExpectedHead));
            callbacks++;
        });
        try
        {
            Click(dialog, "CheckoutBranch");
            CheckoutSubmodulePrompt prompt = await OwnedAsync<CheckoutSubmodulePrompt>(dialog);
            Click(prompt, "UpdateCheckoutSubmodules"); await ClosedAsync(dialog);
            Assert.That(callbacks, Is.EqualTo(1));
            GitSubmodule module = (await fixture.Repository.GetSubmodulesAsync()).Single();
            Assert.That(module.Path, Is.EqualTo(fixture.ModulePath));
            Assert.That(module.Head, Is.EqualTo(module.ExpectedHead));
            Assert.That(new SubmoduleCheckoutPreferencesStore().Load().Decision, Is.Null);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task RememberNoSkipsLaterQuestionsAndNeverUpdatesOrCallsTheHook()
    {
        using SubmoduleFixture fixture = await SubmoduleFixture.CreateAsync();
        int callbacks = 0;
        CheckoutBranchDialog dialog = await fixture.OpenAsync("target", afterUpdate: _ => { callbacks++; return Task.CompletedTask; });
        try
        {
            Click(dialog, "CheckoutBranch");
            CheckoutSubmodulePrompt prompt = await OwnedAsync<CheckoutSubmodulePrompt>(dialog);
            Field<CheckBox>(prompt, "RememberSubmoduleCheckout").IsChecked = true;
            Click(prompt, "SkipCheckoutSubmodules"); await ClosedAsync(dialog);
            Assert.That(new SubmoduleCheckoutPreferencesStore().Load(), Is.EqualTo(new SubmoduleCheckoutPreferences(false, false)));
        }
        finally { dialog.Close(); }
        dialog = await fixture.OpenAsync("main", afterUpdate: _ => { callbacks++; return Task.CompletedTask; });
        try { Click(dialog, "CheckoutBranch"); await ClosedAsync(dialog); }
        finally { dialog.Close(); }
        dialog = await fixture.OpenAsync("target", afterUpdate: _ => { callbacks++; return Task.CompletedTask; });
        try { Click(dialog, "CheckoutBranch"); await ClosedAsync(dialog); }
        finally { dialog.Close(); }
        Assert.That(callbacks, Is.Zero);
        Assert.That(await fixture.ModuleHeadAsync(), Is.EqualTo(fixture.OldModuleHead));
    }

    [AvaloniaTest]
    public async Task RememberYesUsesVisibleCancellableProgressOnLaterCheckoutsAndPreservesBothPreferenceAliases()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Linux executable integration scenario."); return; }
        using SubmoduleFixture fixture = await SubmoduleFixture.CreateAsync();
        (GitRepository slow, _) = fixture.SlowRepository(delaySeconds: 0.4);
        int callbacks = 0;
        CheckoutBranchDialog dialog = await fixture.OpenAsync("target", slow, _ => { callbacks++; return Task.CompletedTask; });
        try
        {
            Click(dialog, "CheckoutBranch");
            CheckoutSubmodulePrompt prompt = await OwnedAsync<CheckoutSubmodulePrompt>(dialog);
            Field<CheckBox>(prompt, "RememberSubmoduleCheckout").IsChecked = true;
            Click(prompt, "UpdateCheckoutSubmodules");
            SubmoduleUpdateDialog progress = await OwnedAsync<SubmoduleUpdateDialog>(dialog);
            Assert.That(progress.IsVisible && progress.IsBusy, Is.True);
            Assert.That(Field<Button>(progress, "CancelSubmoduleUpdate").IsEnabled, Is.True);
            Capture(progress, "classic-checkout-submodule-update.png");
            await ClosedAsync(dialog);
            Assert.That(new SubmoduleCheckoutPreferencesStore().Load(), Is.EqualTo(new SubmoduleCheckoutPreferences(true, true)));
            Assert.That(await fixture.ModuleHeadAsync(), Is.EqualTo(fixture.NewModuleHead));
        }
        finally { dialog.Close(); }
        dialog = await fixture.OpenAsync("main", slow, _ => { callbacks++; return Task.CompletedTask; });
        try
        {
            Click(dialog, "CheckoutBranch");
            SubmoduleUpdateDialog progress = await OwnedAsync<SubmoduleUpdateDialog>(dialog);
            Assert.That(dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>(), Is.Empty);
            Assert.That(progress.IsVisible && progress.IsBusy, Is.True, "Remembered consent must still show the update operation.");
            progress.RequestedThemeVariant = ThemeVariant.Dark;
            Capture(progress, "classic-checkout-submodule-update-dark.png");
            await ClosedAsync(dialog);
            Assert.That(await fixture.ModuleHeadAsync(), Is.EqualTo(fixture.OldModuleHead));
            Assert.That(callbacks, Is.EqualTo(2));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task SwitchingBranchesAtTheSameHeadDoesNotProposeOrUpdateSubmodules()
    {
        using SubmoduleFixture fixture = await SubmoduleFixture.CreateAsync();
        await fixture.Repository.CreateBranchAsync(new GitCreateBranchOptions("same-head", Checkout: false));
        int callbacks = 0;
        CheckoutBranchDialog dialog = await fixture.OpenAsync("same-head", afterUpdate: _ => { callbacks++; return Task.CompletedTask; });
        try { Click(dialog, "CheckoutBranch"); await ClosedAsync(dialog); }
        finally { dialog.Close(); }
        Assert.That(callbacks, Is.Zero);
        Assert.That(new SubmoduleCheckoutPreferencesStore().Load().Decision, Is.Null);
        Assert.That(await fixture.ModuleHeadAsync(), Is.EqualTo(fixture.OldModuleHead));
    }

    [AvaloniaTest]
    public async Task ReapplyingAConflictingCheckoutStashDoesNotProposeSubmodulesOrCallTheHook()
    {
        using SubmoduleFixture fixture = await SubmoduleFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.Root.File, "local conflict\n");
        int callbacks = 0;
        CheckoutBranchDialog dialog = await fixture.OpenAsync("target", afterUpdate: _ => { callbacks++; return Task.CompletedTask; });
        try
        {
            Field<RadioButton>(dialog, "CheckoutStashChanges").IsChecked = true;
            Field<CheckBox>(dialog, "ReapplyCheckoutStash").IsChecked = true;
            Click(dialog, "CheckoutBranch"); await IdleAsync(dialog, success: false);
            Assert.That(dialog.LastError, Is.Not.Null);
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(dialog.OwnedWindows, Is.Empty);
            Assert.That((await fixture.Repository.GetStatusAsync()).Changes.Any(change => change.IsConflict), Is.True);
            Assert.That(await fixture.ModuleHeadAsync(), Is.EqualTo(fixture.OldModuleHead));
            Assert.That((await fixture.Repository.GetStashesAsync()), Has.Count.EqualTo(1));
            Assert.That(callbacks, Is.Zero);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task FailedUpdateDoesNotForceOverwriteDirtyModuleFilesOrCallTheHook()
    {
        using SubmoduleFixture fixture = await SubmoduleFixture.CreateAsync();
        string file = Path.Combine(fixture.ModuleDirectory, "file.txt");
        await File.WriteAllTextAsync(file, "dirty module content\n");
        int callbacks = 0;
        CheckoutBranchDialog dialog = await fixture.OpenAsync("target", afterUpdate: _ => { callbacks++; return Task.CompletedTask; });
        try
        {
            Click(dialog, "CheckoutBranch");
            CheckoutSubmodulePrompt prompt = await OwnedAsync<CheckoutSubmodulePrompt>(dialog);
            Click(prompt, "UpdateCheckoutSubmodules");
            SubmoduleUpdateDialog progress = await OwnedAsync<SubmoduleUpdateDialog>(dialog);
            await IdleAsync(progress, success: false);
            Assert.That(progress.Succeeded, Is.False);
            Assert.That(progress.LastException, Is.TypeOf<GitCommandException>());
            Assert.That(Field<TextBox>(progress, "SubmoduleUpdateOutput").Text, Does.Contain("overwritten"));
            Click(progress, "CancelSubmoduleUpdate"); await IdleAsync(dialog, success: false);
            Assert.That(dialog.LastException, Is.TypeOf<GitCommandException>());
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("dirty module content\n"));
            Assert.That(await fixture.ModuleHeadAsync(), Is.EqualTo(fixture.OldModuleHead));
            Assert.That(callbacks, Is.Zero);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task CancellingAnUpdateKillsItsGitProcessAndKeepsTheAlreadyCheckedOutBranch()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Linux process cancellation scenario."); return; }
        using SubmoduleFixture fixture = await SubmoduleFixture.CreateAsync();
        (GitRepository slow, string marker) = fixture.SlowRepository(delaySeconds: 30);
        int callbacks = 0;
        CheckoutBranchDialog dialog = await fixture.OpenAsync("target", slow, _ => { callbacks++; return Task.CompletedTask; });
        try
        {
            Click(dialog, "CheckoutBranch");
            CheckoutSubmodulePrompt prompt = await OwnedAsync<CheckoutSubmodulePrompt>(dialog);
            Click(prompt, "UpdateCheckoutSubmodules");
            SubmoduleUpdateDialog progress = await OwnedAsync<SubmoduleUpdateDialog>(dialog);
            int pid = await StartedAsync(marker);
            Click(progress, "CancelSubmoduleUpdate"); await IdleAsync(dialog, success: false);
            Assert.That(dialog.LastError, Is.EqualTo("Operation cancelled"));
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("target"));
            Assert.That(await fixture.ModuleHeadAsync(), Is.EqualTo(fixture.OldModuleHead));
            AssertExited(pid);
            Assert.That(callbacks, Is.Zero);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingTheOwnerDuringAnUpdateCancelsItsProcessAndNeverCallsTheHook()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Linux process cancellation scenario."); return; }
        using SubmoduleFixture fixture = await SubmoduleFixture.CreateAsync();
        (GitRepository slow, string marker) = fixture.SlowRepository(delaySeconds: 30);
        Window owner = new() { Width = 600, Height = 400 }; owner.Show();
        int callbacks = 0;
        GitReference reference = (await fixture.Repository.GetReferencesAsync()).Single(reference => reference.ShortName == "target");
        CheckoutBranchDialog dialog = new(slow, reference, afterSubmoduleUpdateAsync: _ => { callbacks++; return Task.CompletedTask; });
        dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        Task shown = dialog.ShowDialog(owner);
        try
        {
            await IdleAsync(dialog);
            Click(dialog, "CheckoutBranch");
            CheckoutSubmodulePrompt prompt = await OwnedAsync<CheckoutSubmodulePrompt>(dialog);
            Click(prompt, "UpdateCheckoutSubmodules");
            await OwnedAsync<SubmoduleUpdateDialog>(dialog);
            int pid = await StartedAsync(marker);
            owner.Close(); await shown.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(dialog.IsVisible, Is.False);
            Assert.That(owner.IsVisible, Is.False);
            AssertExited(pid);
            Assert.That(callbacks, Is.Zero);
            Assert.That(await fixture.ModuleHeadAsync(), Is.EqualTo(fixture.OldModuleHead));
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingTheOwnerWithRememberCheckedDoesNotSaveADismissalAsNo()
    {
        using SubmoduleFixture fixture = await SubmoduleFixture.CreateAsync();
        Window owner = new() { Width = 600, Height = 400 }; owner.Show();
        GitReference reference = (await fixture.Repository.GetReferencesAsync()).Single(reference => reference.ShortName == "target");
        CheckoutBranchDialog dialog = new(fixture.Repository, reference);
        dialog.Opened += async (_, _) => await dialog.InitializeAsync();
        Task shown = dialog.ShowDialog(owner);
        try
        {
            await IdleAsync(dialog);
            Click(dialog, "CheckoutBranch");
            CheckoutSubmodulePrompt prompt = await OwnedAsync<CheckoutSubmodulePrompt>(dialog);
            Field<CheckBox>(prompt, "RememberSubmoduleCheckout").IsChecked = true;
            owner.Close(); await shown.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(new SubmoduleCheckoutPreferencesStore().Load().Decision, Is.Null);
            Assert.That(await fixture.ModuleHeadAsync(), Is.EqualTo(fixture.OldModuleHead));
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    public async Task OriginalPreferencePrecedenceAndFrenchDarkCompactPromptArePreserved()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        SubmoduleCheckoutPreferencesStore store = new();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new SubmoduleCheckoutPreferences(false, true)));
        Assert.That(store.Load().Decision, Is.False);
        await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new SubmoduleCheckoutPreferences(null, true)));
        Assert.That(store.Load().Decision, Is.True);
        await File.WriteAllTextAsync(store.FilePath, "invalid json");
        Assert.That(store.Load().Decision, Is.Null);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
        CheckoutSubmodulePrompt prompt = new() { RequestedThemeVariant = ThemeVariant.Dark }; prompt.Show();
        try
        {
            prompt.UpdateLayout();
            Assert.That(prompt.Title, Is.EqualTo("Mettre à jour les sous-modules"));
            Assert.That(Field<TextBlock>(prompt, "CheckoutSubmoduleHeading").Text, Does.Contain("Mettre à jour les sous-modules"));
            Assert.That(Field<TextBlock>(prompt, "CheckoutSubmoduleDescription").Text, Does.Contain("Ce dépôt"));
            Assert.That(Field<CheckBox>(prompt, "RememberSubmoduleCheckout").Bounds.Height, Is.EqualTo(24));
            Assert.That(Field<Button>(prompt, "UpdateCheckoutSubmodules").Bounds.Height, Is.EqualTo(24));
            Capture(prompt, "classic-checkout-submodules-fr-dark.png");
        }
        finally { prompt.Close(); }
    }

    [AvaloniaTest]
    public async Task MainUsesConfiguredAutoCompileAfterCheckoutUpdateAndOwnsEachSolutionConfirmation()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Linux native build helper integration scenario."); return; }
        using SubmoduleFixture fixture = await SubmoduleFixture.CreateAsync(solutions: true);
        string helper = fixture.Root.Output("fake-submodule-builder.py");
        string marker = helper + ".json";
        await File.WriteAllTextAsync(helper, "#!/usr/bin/python3\nimport json, pathlib, sys\n" +
            "p=pathlib.Path(" + JsonSerializer.Serialize(marker) + ")\n" +
            "runs=json.loads(p.read_text()) if p.exists() else []\n" +
            "solution=pathlib.Path(sys.argv[1])\n" +
            "runs.append({'solution': str(solution), 'content': solution.parent.joinpath('file.txt').read_text()})\n" +
            "p.write_text(json.dumps(runs))\nprint('local build fixture completed')\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        new PreferencesStore().Save(new DesktopPreferences
        {
            PluginSettings = new() { [AutoCompileSubmodulesService.Descriptor.Id.ToString()] = new()
            { ["enabled"] = "true", ["executable"] = helper, ["arguments"] = "" } }
        });
        MainWindow main = new() { Width = 1440, Height = 900 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(fixture.Repository.Path);
            TreeView navigation = Field<TreeView>(main, "RepositoryNavigation");
            navigation.SelectedItem = BrowseTestHelpers.NavigationNodes(navigation)
                .Single(node => node.Tag is GitReference reference && reference.Name == "refs/heads/target");
            await Until(() => !main.IsBusy);
            MenuItem commands = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(menu => menu.Header as string == "Commands");
            commands.Items.OfType<MenuItem>().Single(menu => menu.Header as string == "Checkout branch…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            CheckoutBranchDialog dialog = await OwnedAsync<CheckoutBranchDialog>(main); await IdleAsync(dialog);
            Click(dialog, "CheckoutBranch");
            CheckoutSubmodulePrompt prompt = await OwnedAsync<CheckoutSubmodulePrompt>(dialog);
            Click(prompt, "UpdateCheckoutSubmodules");
            Window? previous = null;
            for (int index = 0; index < 2; index++)
            {
                await Until(() => dialog.OwnedWindows.Any(window => window.Title == "Auto compile submodules" && window != previous) || !dialog.IsBusy);
                Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
                Window confirmation = dialog.OwnedWindows.Single(window => window.Title == "Auto compile submodules" && window != previous);
                Assert.That(confirmation.Owner, Is.EqualTo(dialog));
                Assert.That(main.IsBusy, Is.True);
                Assert.That(await fixture.ModuleHeadAsync(), Is.EqualTo(fixture.NewModuleHead), "Compilation must follow the completed update.");
                confirmation.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Build").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                previous = confirmation;
            }
            await Until(() => !dialog.IsVisible && !main.IsBusy);
            Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
            using JsonDocument runs = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
            Assert.That(runs.RootElement.EnumerateArray().Select(run => run.GetProperty("solution").GetString()), Is.EquivalentTo(SubmoduleFixture.SolutionNames.Select(name => Path.Combine(fixture.ModuleDirectory, name))));
            Assert.That(runs.RootElement.EnumerateArray().Select(run => run.GetProperty("content").GetString()), Is.All.EqualTo("module new\n"));
            Assert.That(Field<TextBlock>(main, "RepositoryStatus").Text, Does.StartWith("target"));
        }
        finally { main.Close(); }
    }

    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Window window, string name)
    { Button button = Field<Button>(window, name); Assert.That(button.IsEnabled && button.IsEffectivelyVisible, Is.True, name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static async Task<T> OwnedAsync<T>(Window window) where T : Window
    { await Until(() => window.OwnedWindows.OfType<T>().Any()); T owned = window.OwnedWindows.OfType<T>().Single(); owned.UpdateLayout(); return owned; }
    private static async Task IdleAsync(RepositoryDialog dialog, bool success = true)
    { await Until(() => !dialog.IsBusy); if (success) { Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); } }
    private static async Task ClosedAsync(RepositoryDialog dialog)
    { await Until(() => !dialog.IsVisible && !dialog.IsBusy); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); }
    private static async Task Until(Func<bool> predicate)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); while (!predicate()) { await Task.Delay(20, timeout.Token); } }
    private static async Task<int> StartedAsync(string marker)
    { await Until(() => File.Exists(marker) && new FileInfo(marker).Length > 0); return int.Parse(await File.ReadAllTextAsync(marker), CultureInfo.InvariantCulture); }
    private static void AssertExited(int pid)
    { try { using Process process = Process.GetProcessById(pid); Assert.That(process.HasExited, Is.True); } catch (ArgumentException) { } }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using WriteableBitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }

    private sealed class SubmoduleFixture : IDisposable
    {
        internal const string NestedPath = "nested/[one]\nΩ";
        internal static readonly string[] SolutionNames = ["build [one]\nλ.sln", "build two Ω.sln"];
        internal Fixture Root { get; }
        internal GitRepository Repository => Root.Repository;
        internal string ModulePath { get; } = "modules/[child]\nλ";
        internal string ModuleDirectory => Root.PathInRepository(ModulePath);
        internal string OldModuleHead { get; private set; } = "";
        internal string NewModuleHead { get; private set; } = "";

        private SubmoduleFixture(Fixture root) { Root = root; }
        internal static async Task<SubmoduleFixture> CreateAsync(bool nested = false, bool solutions = false)
        {
            Fixture root = await Fixture.CreateAsync();
            SubmoduleFixture fixture = new(root);
            try
            {
                root.SetEnvironment("GIT_ALLOW_PROTOCOL", "file");
                string source = root.Output("module source λ");
                GitRepository module = await CreateRepositoryAsync(source, "module old\n");
                if (nested)
                {
                    string child = root.Output("nested source Ω");
                    await CreateRepositoryAsync(child, "nested content\n");
                    await module.RunAsync(["--literal-pathspecs", "submodule", "add", "--name", "nested-checkout", "--", child, NestedPath]);
                    await module.StageAllAsync(); await module.CommitAsync("Nested literal module");
                }
                fixture.OldModuleHead = await module.ResolveRevisionAsync("HEAD");
                await root.Repository.RunAsync(["--literal-pathspecs", "submodule", "add", "--name", "checkout-child", "--", source, fixture.ModulePath]);
                await root.Repository.StageAllAsync(); await root.Repository.CommitAsync("Superproject module base");
                await root.Repository.CreateBranchAsync(new GitCreateBranchOptions("target"));
                await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "module new\n");
                if (solutions) { foreach (string solution in SolutionNames) { await File.WriteAllTextAsync(Path.Combine(source, solution), "local solution fixture\n"); } }
                await module.StageAllAsync(); await module.CommitAsync("Module target");
                fixture.NewModuleHead = await module.ResolveRevisionAsync("HEAD");
                GitRepository checkedOutModule = new(fixture.ModuleDirectory);
                await checkedOutModule.FetchAsync("origin", prune: false);
                await checkedOutModule.CheckoutAsync(fixture.NewModuleHead, detached: true);
                await File.WriteAllTextAsync(root.File, "target parent\n");
                await root.Repository.StageAllAsync(); await root.Repository.CommitAsync("Superproject target");
                await root.Repository.CheckoutAsync("main");
                await checkedOutModule.CheckoutAsync(fixture.OldModuleHead, detached: true);
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        private static async Task<GitRepository> CreateRepositoryAsync(string path, string content)
        {
            GitRepository repository = await GitRepository.InitAsync(path);
            await repository.SetConfigAsync("user.name", "Submodule Tester");
            await repository.SetConfigAsync("user.email", "submodule@example.com");
            await repository.SetConfigAsync("commit.gpgsign", "false");
            await repository.SetConfigAsync("core.hooksPath", Path.Combine(Path.GetDirectoryName(path)!, "no-hooks"));
            await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), content);
            await repository.StageAllAsync(); await repository.CommitAsync("Initial module");
            return repository;
        }
        internal async Task<CheckoutBranchDialog> OpenAsync(string branch, GitRepository? repository = null, Func<CancellationToken, Task>? afterUpdate = null)
        {
            GitReference reference = (await Repository.GetReferencesAsync()).Single(reference => reference.Name == "refs/heads/" + branch);
            CheckoutBranchDialog dialog = new(repository ?? Repository, reference, afterSubmoduleUpdateAsync: afterUpdate); dialog.Show();
            await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); return dialog;
        }
        internal Task<string> ModuleHeadAsync(CancellationToken token = default) => new GitRepository(ModuleDirectory).ResolveRevisionAsync("HEAD", token);
        internal (GitRepository, string) SlowRepository(double delaySeconds)
        {
            if (OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Linux Git helper fixture."); }
            string helper = Root.Output("slow-submodule-git.py"); string marker = helper + ".started";
            File.WriteAllText(helper, "#!/usr/bin/python3\nimport os, pathlib, sys, time\n" +
                "if any(sys.argv[i:i+2] == ['submodule', 'update'] for i in range(1, len(sys.argv)-1)):\n    pathlib.Path(" + JsonSerializer.Serialize(marker) + ").write_text(str(os.getpid()))\n    time.sleep(" + delaySeconds.ToString(CultureInfo.InvariantCulture) + ")\n" +
                "os.execvp('git', ['git'] + sys.argv[1:])\n");
            File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return (new GitRepository(Repository.Path, new GitRunner(helper)), marker);
        }
        public void Dispose() => Root.Dispose();
    }
}

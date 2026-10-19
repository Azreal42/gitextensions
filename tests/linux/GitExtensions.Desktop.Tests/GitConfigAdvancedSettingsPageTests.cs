using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using ReferenceFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GitConfigAdvancedSettingsPageTests
{
    private static readonly string[] Names = ["checkBoxPullRebase", "checkBoxFetchPrune", "checkboxMergeAutoStash", "checkBoxRebaseAutostash", "checkBoxRebaseAutosquash", "checkBoxUpdateRefs", "checkBoxReReReEnabled", "checkBoxReReReAutoUpdate"];
    private static readonly string[] Keys = ["pull.rebase", "fetch.prune", "merge.autostash", "rebase.autostash", "rebase.autosquash", "rebase.updaterefs", "rerere.enabled", "rerere.autoupdate"];
    private static readonly string[] Captions = ["Rebase local branch when pulling (instead of merge)", "Prune remote branches during fetch", "Automatically stash before doing a merge", "Automatically stash before doing a rebase", "Automatically squash commits when doing an interactive rebase", "Rebase also dependent branches", "Reuse recorded resolution of conflicted merges", "Automatically apply recorded resolution of conflicted merges"];

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("en", true)] [TestCase("fr", false)] [TestCase("fr", true)]
    public async Task OriginalEightRowsAreTranslatedReadableAndEffectiveAndSystemStayReadOnly(string language, bool dark)
    {
        await using Scope f = await Scope.CreateAsync();
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(language, dark);
        string before = f.Bytes(); Assert.That(dialog.AdvancedPage.Session, Is.SameAs(dialog.Page.Session));
        Assert.That(dialog.ActualThemeVariant, Is.EqualTo(dark ? ThemeVariant.Dark : ThemeVariant.Light));
        double previousBottom = 0;
        for (int i = 0; i < Names.Length; i++)
        {
            CheckBox box = Field<CheckBox>(dialog, Names[i]); box.BringIntoView(); Pump(dialog);
            Assert.That(box.IsThreeState, Is.True); Assert.That(box.IsEnabled, Is.False);
            Assert.That(((TextBlock)box.Content!).Text, Is.EqualTo(f.Catalog!.Translate("GitConfigAdvancedSettingsPage", Names[i], "Text", Captions[i]) + " [" + Keys[i] + "]"));
            Assert.That(box.Bounds.Height, Is.GreaterThanOrEqualTo(24));
            Assert.That(box.Bounds.Y, Is.GreaterThanOrEqualTo(previousBottom)); previousBottom = box.Bounds.Bottom;
            Assert.That(((TextBlock)box.Content!).Bounds.Width, Is.GreaterThan(0));
        }
        await ScopeChoice(dialog, "GitAdvancedScope", 3);
        foreach (string name in Names) { Assert.That(Field<CheckBox>(dialog, name).IsEnabled, Is.False); }
        Assert.That(Field<TextBlock>(dialog, "GitAdvancedScopeNotice").Text, Does.Contain(f.Catalog!.Translate("This scope is read-only.")));
        Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False);
        Capture(dialog, "git-advanced-" + language + (dark ? "-dark.png" : "-light.png"));
        Press(dialog, PhysicalKey.Escape); await Until(() => done.IsCompleted); Assert.That(await done, Is.False); Assert.That(f.Bytes(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task RawAliasesEmptyUnknownAndValuelessAreNotNormalizedByOpeningSwitchingOrCancel()
    {
        await using Scope f = await Scope.CreateAsync();
        string[] raw = ["yes", "no", "on", "off", "1", "0", "", "TRUE"];
        for (int i = 0; i < Keys.Length; i++) { await f.Set("local", Keys[i], raw[i]); }
        string before = f.Bytes(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(); await ScopeChoice(dialog, "GitAdvancedScope", 1);
        bool?[] expected = [true, false, true, false, true, false, false, null];
        for (int i = 0; i < Names.Length; i++) { Assert.That(Field<CheckBox>(dialog, Names[i]).IsChecked, Is.EqualTo(expected[i])); }
        Assert.That(ToolTip.GetTip(Field<CheckBox>(dialog, Names[7]))?.ToString(), Does.Contain("TRUE").And.Contain("file:"));
        Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False);
        await ScopeChoice(dialog, "GitAdvancedScope", 2); await ScopeChoice(dialog, "GitAdvancedScope", 1);
        Click(dialog, "GitToolsCancel"); await Until(() => done.IsCompleted); Assert.That(f.Bytes(), Is.EqualTo(before));
        await f.Git.GitAsync("config", "--unset", "rerere.autoupdate"); File.AppendAllText(f.Git.PathInRepository(".git/config"), "\n[rerere]\n autoupdate\n");
        before = f.Bytes(); (dialog, done) = await f.Open(); await ScopeChoice(dialog, "GitAdvancedScope", 1);
        Assert.That(Field<CheckBox>(dialog, Names[7]).IsChecked, Is.Null); Assert.That(ToolTip.GetTip(Field<CheckBox>(dialog, Names[7]))?.ToString(), Does.Contain("valueless"));
        Click(dialog, "GitToolsCancel"); await Until(() => done.IsCompleted); Assert.That(f.Bytes(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase("checkBoxPullRebase", "pull.rebase")]
    [TestCase("checkBoxFetchPrune", "fetch.prune")]
    [TestCase("checkboxMergeAutoStash", "merge.autostash")]
    [TestCase("checkBoxRebaseAutostash", "rebase.autostash")]
    [TestCase("checkBoxRebaseAutosquash", "rebase.autosquash")]
    [TestCase("checkBoxUpdateRefs", "rebase.updaterefs")]
    [TestCase("checkBoxReReReEnabled", "rerere.enabled")]
    [TestCase("checkBoxReReReAutoUpdate", "rerere.autoupdate")]
    public async Task EachRealCheckboxCyclesTrueFalseUnsetAndApplyUsesItsOriginalNativeKey(string name, string key)
    {
        await using Scope f = await Scope.CreateAsync(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(); await ScopeChoice(dialog, "GitAdvancedScope", 1);
        CheckBox box = Field<CheckBox>(dialog, name); Pointer(box); State(dialog, box, true);
        await Apply(dialog); Assert.That(await f.Read("local", key), Is.EqualTo("true"));
        State(dialog, box, false);
        await Apply(dialog); Assert.That(await f.Read("local", key), Is.EqualTo("false"));
        State(dialog, box, null);
        await Apply(dialog); Assert.That(await f.Read("local", key), Is.Null);
        Assert.That(dialog.LastSaveResult!.Writes.Single().Arguments, Does.Contain("--unset"));
        Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False);
        Click(dialog, "GitToolsCancel"); await Until(() => done.IsCompleted); Assert.That(await done, Is.False);
    }

    [AvaloniaTest]
    public async Task UnknownRawValueCanBeRestoredWithoutWritingOrExplicitlyUnsetThroughItsMenu()
    {
        await using Scope f = await Scope.CreateAsync(); await f.Set("local", "pull.rebase", "merges"); string before = f.Bytes();
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(); await ScopeChoice(dialog, "GitAdvancedScope", 1);
        CheckBox box = Field<CheckBox>(dialog, "checkBoxPullRebase"); Assert.That(box.IsChecked, Is.Null);
        Pointer(box); State(dialog, box, true); await Action(dialog, box, "checkBoxPullRebaseRestorePublished");
        Assert.That(box.IsChecked, Is.Null); Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False); Assert.That(f.Bytes(), Is.EqualTo(before));
        await Action(dialog, box, "checkBoxPullRebaseUnset"); Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.True);
        await Apply(dialog); Assert.That(await f.Read("local", "pull.rebase"), Is.Null);
        Click(dialog, "GitToolsCancel"); await Until(() => done.IsCompleted);
    }

    [AvaloniaTest]
    public async Task LocalGlobalAndConfigDraftsShareOneApplyWhileEffectiveShowsPublishedValuesAndCancelDropsOnlyPendingEdits()
    {
        await using Scope f = await Scope.CreateAsync(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open();
        await ScopeChoice(dialog, "GitAdvancedScope", 1); State(dialog, Field<CheckBox>(dialog, "checkBoxPullRebase"), true);
        Pointer(Field<TabItem>(dialog, "GitConfigTab")); Pump(dialog); Assert.That(dialog.Page.View, Is.EqualTo(GitToolConfigurationView.Local));
        TextBox email = Field<TextBox>(dialog, "GlobalUserEmail"); Pointer(email); Press(dialog, PhysicalKey.A, RawInputModifiers.Control); dialog.KeyTextInput("advanced-joint@example.invalid");
        await ScopeChoice(dialog, "GitToolsScope", 2); Pointer(Field<TabItem>(dialog, "GitAdvancedTab")); Pump(dialog);
        Assert.That(dialog.AdvancedPage.View, Is.EqualTo(GitToolConfigurationView.Global)); State(dialog, Field<CheckBox>(dialog, "checkBoxFetchPrune"), true);
        await ScopeChoice(dialog, "GitAdvancedScope", 0); Assert.That(Field<CheckBox>(dialog, "checkBoxPullRebase").IsChecked, Is.Null);
        await ScopeChoice(dialog, "GitAdvancedScope", 1); Assert.That(Field<CheckBox>(dialog, "checkBoxPullRebase").IsChecked, Is.True);
        await Apply(dialog); Assert.That(dialog.LastSaveResult!.Writes.Count, Is.EqualTo(3)); Assert.That(dialog.AdvancedPage.Session, Is.SameAs(dialog.Page.Session));
        string published = f.Bytes(); State(dialog, Field<CheckBox>(dialog, "checkBoxPullRebase"), false);
        Click(dialog, "GitToolsCancel"); await Until(() => done.IsCompleted); Assert.That(f.Bytes(), Is.EqualTo(published));
        Assert.That(await f.Read("local", "user.email"), Is.EqualTo("advanced-joint@example.invalid")); Assert.That(await f.Read("global", "fetch.prune"), Is.EqualTo("true"));
    }

    [AvaloniaTest]
    [TestCase("2.37.3", false)] [TestCase("2.38.0", true)]
    public async Task UpdateRefsVisibilityUsesTheActualGitVersionAndKeepsAnUnsupportedExistingValue(string version, bool supported)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("The version-only native wrapper is Linux-specific."); }
        await using Scope f = await Scope.CreateAsync(); await f.Set("local", "rebase.updaterefs", "yes");
        string wrapper = f.Wrapper("if [ \"$1\" = --version ]; then printf 'git version " + version + "\\n'; exit 0; fi\nexec /usr/bin/git \"$@\"");
        GitRepository repository = new(f.Git.Repository.Path, new GitRunner(wrapper)); string before = f.Bytes();
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(repository: repository); await ScopeChoice(dialog, "GitAdvancedScope", 1);
        Assert.That(dialog.AdvancedPage.Session!.Snapshot.SupportsRebaseUpdateRefs, Is.EqualTo(supported));
        CheckBox update = Field<CheckBox>(dialog, "checkBoxUpdateRefs"); Assert.That(update.IsVisible, Is.EqualTo(supported)); Assert.That(update.IsEnabled, Is.EqualTo(supported));
        Click(dialog, "GitToolsCancel"); await Until(() => done.IsCompleted); Assert.That(f.Bytes(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task NativePartialFailureRebasesBothPagesRetainsPendingIntentAndRequiresReloadBeforeRetry()
    {
        await using Scope f = await Scope.CreateAsync(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open();
        await ScopeChoice(dialog, "GitAdvancedScope", 1); State(dialog, Field<CheckBox>(dialog, "checkBoxPullRebase"), true);
        await ScopeChoice(dialog, "GitAdvancedScope", 2); State(dialog, Field<CheckBox>(dialog, "checkBoxFetchPrune"), true);
        File.WriteAllText(f.Git.Output("global.config") + ".lock", "foreign-lock\n"); Click(dialog, "GitToolsApply"); await Idle(dialog);
        Assert.That(dialog.LastSaveResult!.Success, Is.False); Assert.That(dialog.LastSaveResult.Writes.Select(write => write.Outcome), Is.EqualTo(new[] { GitToolConfigurationWriteOutcome.NativeCompleted, GitToolConfigurationWriteOutcome.NativeFailed }));
        Assert.That(await f.Read("local", "pull.rebase"), Is.EqualTo("true")); Assert.That(await f.Read("global", "fetch.prune"), Is.Null);
        Assert.That(dialog.AdvancedPage.Session, Is.SameAs(dialog.Page.Session)); Assert.That(dialog.AdvancedPage.Session!.IsDirty, Is.True);
        Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False); Assert.That(Field<Button>(dialog, "GitToolsReload").IsVisible, Is.True); Assert.That(done.IsCompleted, Is.False);
        File.Delete(f.Git.Output("global.config") + ".lock"); Click(dialog, "GitToolsReload"); await Idle(dialog);
        Assert.That(Field<CheckBox>(dialog, "checkBoxFetchPrune").IsChecked, Is.True); await Apply(dialog);
        Assert.That(await f.Read("global", "fetch.prune"), Is.EqualTo("true")); Click(dialog, "GitToolsCancel"); await Until(() => done.IsCompleted);
    }

    [AvaloniaTest]
    public async Task ConcurrentRawFileEditRejectsApplyAndRetainsAdvancedDraftWithoutClosingTheModal()
    {
        await using Scope f = await Scope.CreateAsync(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open();
        await ScopeChoice(dialog, "GitAdvancedScope", 1); State(dialog, Field<CheckBox>(dialog, "checkBoxPullRebase"), true);
        File.AppendAllText(f.Git.PathInRepository(".git/config"), "\n# concurrent outside editor\n"); string concurrent = f.Bytes();
        Click(dialog, "GitToolsApply"); await Idle(dialog);
        Assert.That(dialog.LastSaveResult!.MutationStarted, Is.False); Assert.That(f.Bytes(), Is.EqualTo(concurrent)); Assert.That(done.IsCompleted, Is.False);
        Assert.That(Field<CheckBox>(dialog, "checkBoxPullRebase").IsChecked, Is.True); Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False);
        Click(dialog, "GitToolsCancel"); await Until(() => done.IsCompleted); Assert.That(f.Bytes(), Is.EqualTo(concurrent));
    }
    private sealed class Scope(ReferenceFixture git) : IAsyncDisposable
    {
        private readonly List<Window> _windows = [];
        internal ReferenceFixture Git => git;
        internal AvailableEncodingsPreferencesStore EncodingPreferences { get; } = new(git.Output("encoding-preferences"));
        internal static async Task<Scope> CreateAsync()
        {
            ReferenceFixture git = await ReferenceFixture.CreateAsync();
            try
            {
                Directory.CreateDirectory(git.Output("home")); git.SetEnvironment("HOME", git.Output("home"));
                git.SetEnvironment("GIT_CONFIG_GLOBAL", git.Output("global.config")); File.WriteAllText(git.Output("global.config"), "");
                git.SetEnvironment("GIT_CONFIG_SYSTEM", git.Output("system.config")); File.WriteAllText(git.Output("system.config"), "");
                git.SetEnvironment("GIT_CONFIG_NOSYSTEM", null); git.SetEnvironment("GIT_CONFIG_COUNT", null);
                git.SetEnvironment("GIT_CONFIG_PARAMETERS", null); git.SetEnvironment("GIT_CONFIG", null);
                foreach (string name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name => name.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal) || name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal)).ToArray())
                { git.SetEnvironment(name, null); }
                return new(git);
            }
            catch { git.Dispose(); throw; }
        }
        internal void Own(Window window) => _windows.Add(window);
        internal Window Owner(bool dark = false)
        {
            Window window = new() { Width = 1100, Height = 800, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Content = new TextBox { Text = "Config owner" } };
            Own(window); window.Show(); return window;
        }
        internal TranslationCatalog? Catalog { get; private set; }
        internal async Task<(GitToolsSettingsDialog Dialog, Task<bool> Done)> Open(string language = "en", bool dark = false, GitRepository? repository = null, Window? owner = null)
        {
            owner ??= Owner(dark); Catalog = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
            GitToolsSettingsDialog dialog = new(repository ?? git.Repository, Catalog, encodingPreferences: EncodingPreferences, advanced: true); Own(dialog);
            Task<bool> done = dialog.ShowDialog<bool>(owner); await dialog.InitializeAsync(); await Idle(dialog);
            Assert.That(dialog.IsReady, Is.True, dialog.LastError); Assert.That(dialog.LastError, Is.Null);
            return (dialog, done);
        }
        internal Task<GitResult> Set(string scope, string key, string value) => git.GitAsync("config", "--" + scope, key, value);
        internal async Task<string?> Read(string scope, string key)
        {
            GitResult result = await new GitRunner().RunAsync(git.Repository.Path, ["config", "--" + scope, "--get", key]);
            Assert.That(result.ExitCode, Is.AnyOf(0, 1), result.StandardError); return result.ExitCode == 1 ? null : result.StandardOutput[..^1];
        }
        internal string Bytes() => string.Join("|", new[] { git.PathInRepository(".git/config"), git.Output("global.config"), git.Output("system.config") }.Select(path => Convert.ToHexString(File.ReadAllBytes(path))));
        internal string Wrapper(string body)
        {
            string file = git.Output("git-wrapper-" + Guid.NewGuid().ToString("N")); File.WriteAllText(file, "#!/bin/sh\nset -eu\n" + body + "\n");
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } return file;
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window window in _windows.AsEnumerable().Reverse())
                {
                    foreach (Window child in PushProcessTestDriver.Windows(window).Reverse().ToArray()) { if (child.IsVisible) { child.Close(); } }
                    if (window.IsVisible) { window.Close(); }
                    if (window is GitToolsSettingsDialog config) { await config.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(20)); }
                    if (window is MainWindow main) { await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(20)); }
                    await Until(() => !window.IsVisible);
                }
            }
            finally { git.Dispose(); }
        }
    }

    private static async Task Action(Window window, CheckBox box, string name)
    {
        box.BringIntoView(); Pump(window); box.ContextMenu!.Open(box); await Task.Yield(); Pump(window);
        MenuItem item = box.ContextMenu.Items.OfType<MenuItem>().Single(value => value.Name == name); Pointer(item);
        Assert.That(box.ContextMenu.IsOpen, Is.False);
    }
    private static async Task ScopeChoice(GitToolsSettingsDialog dialog, string name, int index)
    {
        ComboBox combo = Field<ComboBox>(dialog, name); Pointer(combo); Assert.That(combo.IsDropDownOpen, Is.True);
        Press(dialog, PhysicalKey.Home); for (int i = 0; i < index; i++) { Press(dialog, PhysicalKey.ArrowDown); }
        Press(dialog, PhysicalKey.Enter); await Task.Yield(); Pump(dialog); Assert.That(combo.SelectedIndex, Is.EqualTo(index));
    }
    private static async Task Apply(GitToolsSettingsDialog dialog)
    { Click(dialog, "GitToolsApply"); await Idle(dialog); Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.LastSaveResult?.Success, Is.True); }
    private static void State(Window window, CheckBox box, bool? value)
    {
        box.BringIntoView(); box.Focus(); Pump(window);
        for (int i = 0; i < 3 && box.IsChecked != value; i++) { Press(window, PhysicalKey.Space); }
        Assert.That(box.IsChecked, Is.EqualTo(value), "The real three-state checkbox must reach the requested value within one cycle.");
    }
    private static void Click(Window window, string name) => Pointer(Field<Button>(window, name));
    private static void Pointer(Control control) { control.BringIntoView(); PushProcessTestDriver.Pointer(control); }
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static Task Idle(GitToolsSettingsDialog dialog) => Until(() => !dialog.IsBusy);
    private static async Task Until(Func<bool> complete)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!complete() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs(); Assert.That(complete(), Is.True, "The real config modal or operation did not settle.");
    }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Capture(Window window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
}

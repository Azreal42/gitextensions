using System.Diagnostics;
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
public sealed class GitToolsSettingsDialogTests
{
    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task RealReadOnlyScopesAndUntouchedOkDoNotPublishFallbackOrSuggestedCommands(bool dark)
    {
        await using Scope f = await Scope.CreateAsync();
        await f.Set("local", "diff.guitool", "meld"); await f.Set("local", "merge.tool", "legacy merge");
        await f.Set("system", "difftool.system.path", "/system/literal");
        string oracle = f.Git.Output("must-not-run");
        await f.Set("local", "mergetool.legacy merge.cmd", "printf ran > " + Quote(oracle));
        string before = f.ConfigurationBytes();
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(dark: dark);
        Assert.That(dialog.ActualThemeVariant, Is.EqualTo(dark ? ThemeVariant.Dark : ThemeVariant.Light));
        Assert.That(dialog.Page.ActualThemeVariant, Is.EqualTo(dialog.ActualThemeVariant));
        Assert.That(Field<TextBox>(dialog, "ReadOnlyDiffTool").Text, Is.EqualTo("meld"));
        Assert.That(Field<TextBox>(dialog, "ReadOnlyMergeTool").Text, Is.EqualTo("legacy merge"));
        Assert.That(Field<TextBox>(dialog, "txtMergeToolCommand").IsReadOnly, Is.True);
        Assert.That(Field<Button>(dialog, "btnDiffToolBrowse").IsEnabled, Is.False);
        await ChooseScope(dialog, 3); // System
        Assert.That(Field<TextBox>(dialog, "txtDiffToolPath").IsReadOnly, Is.True);
        Assert.That(Field<Button>(dialog, "btnMergeToolCommandSuggest").IsEnabled, Is.False);
        await ChooseScope(dialog, 1); // Local
        Assert.That(Field<TextBox>(dialog, "txtDiffToolCommand").Text, Is.Empty);
        Assert.That(Field<TextBox>(dialog, "txtDiffToolCommand").Watermark, Does.Contain("$LOCAL"));
        Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False);
        Capture(dialog, "git-tools-settings-" + (dark ? "dark" : "light") + ".png");
        Click(dialog, "GitToolsAccept"); await Until(() => done.IsCompleted); Assert.That(await done, Is.True);
        Assert.That(f.ConfigurationBytes(), Is.EqualTo(before)); Assert.That(File.Exists(oracle), Is.False);
        Assert.That(await f.Read("local", "merge.guitool"), Is.Null);
        Assert.That(await f.Read("local", "difftool.meld.cmd"), Is.Null);
        Assert.That(dialog.MayHaveChangedConfiguration, Is.False);
    }

    [AvaloniaTest]
    public async Task LocalGlobalDraftsSurviveScopeChangesWhileEffectiveStaysPublishedAndCancelWritesNothing()
    {
        await using Scope f = await Scope.CreateAsync();
        await f.Set("local", "diff.guitool", "local original"); await f.Set("global", "diff.guitool", "global original");
        string before = f.ConfigurationBytes(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open();
        await ChooseScope(dialog, 1); EditName(dialog, "Diff", "local new"); Edit(dialog, "txtDiffToolCommand", "local custom \"$LOCAL\"");
        await ChooseScope(dialog, 2); EditName(dialog, "Diff", "global new"); Edit(dialog, "txtDiffToolPath", "/literal global path");
        await ChooseScope(dialog, 0);
        Assert.That(Field<TextBox>(dialog, "ReadOnlyDiffTool").Text, Is.EqualTo("local original"));
        await ChooseScope(dialog, 1);
        Assert.That(Field<ComboBox>(dialog, "_NO_TRANSLATE_cboDiffTool").Text, Is.EqualTo("local new"));
        Assert.That(Field<TextBox>(dialog, "txtDiffToolCommand").Text, Is.EqualTo("local custom \"$LOCAL\""));
        await ChooseScope(dialog, 2);
        Assert.That(Field<ComboBox>(dialog, "_NO_TRANSLATE_cboDiffTool").Text, Is.EqualTo("global new"));
        Assert.That(Field<TextBox>(dialog, "txtDiffToolPath").Text, Is.EqualTo("/literal global path"));
        Assert.That(f.ConfigurationBytes(), Is.EqualTo(before));
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
        Assert.That(f.ConfigurationBytes(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ApplyPersistsBothScopesAndExactUnknownNamesWithoutChangingLegacyOrVisitedTools()
    {
        await using Scope f = await Scope.CreateAsync();
        await f.Set("local", "diff.guitool", "old"); await f.Set("local", "difftool.old.cmd", "keep old");
        await f.Set("local", "diff.tool", "legacy diff"); await f.Set("local", "merge.tool", "legacy merge");
        string name = "Exact.Name ' Ω\\tail", path = "/literal path/'quote\\backslash", command = "custom --arg='$LOCAL' \"$REMOTE\"";
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open();
        await ChooseScope(dialog, 1); EditName(dialog, "Diff", "visited only"); Edit(dialog, "txtDiffToolCommand", "do not publish");
        EditName(dialog, "Diff", name); Edit(dialog, "txtDiffToolPath", path); Edit(dialog, "txtDiffToolCommand", command);
        await ChooseScope(dialog, 2); EditName(dialog, "Merge", "global custom"); Edit(dialog, "txtMergeToolCommand", "merge \"$BASE\" \"$MERGED\"");
        Click(dialog, "GitToolsApply"); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null); Assert.That(dialog.IsVisible, Is.True);
        Assert.That(dialog.LastSaveResult?.Success, Is.True); Assert.That(dialog.MayHaveChangedConfiguration, Is.True);
        Assert.That(await f.Read("local", "diff.guitool"), Is.EqualTo(name));
        Assert.That(await f.Read("local", "difftool." + name + ".path"), Is.EqualTo(path));
        Assert.That(await f.Read("local", "difftool." + name + ".cmd"), Is.EqualTo(command));
        Assert.That(await f.Read("local", "difftool.visited only.cmd"), Is.Null);
        Assert.That(await f.Read("local", "difftool.old.cmd"), Is.EqualTo("keep old"));
        Assert.That(await f.Read("local", "diff.tool"), Is.EqualTo("legacy diff"));
        Assert.That(await f.Read("local", "merge.tool"), Is.EqualTo("legacy merge"));
        Assert.That(await f.Read("global", "merge.guitool"), Is.EqualTo("global custom"));
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
        (GitToolsSettingsDialog reopened, Task<bool> reopenedDone) = await f.Open(); await ChooseScope(reopened, 1);
        Assert.That(Field<ComboBox>(reopened, "_NO_TRANSLATE_cboDiffTool").Text, Is.EqualTo(name));
        Assert.That(Field<TextBox>(reopened, "txtDiffToolCommand").Text, Is.EqualTo(command));
        EditName(reopened, "Diff", "");
        ComboBox cleared = Field<ComboBox>(reopened, "_NO_TRANSLATE_cboDiffTool");
        Press(reopened, PhysicalKey.Tab); Pump(reopened);
        Assert.That(cleared.Text, Is.Empty, $"Tab must keep the explicit empty choice: selected={cleared.SelectedItem}, popup={cleared.IsDropDownOpen}.");
        Assert.That(cleared.IsDropDownOpen, Is.False, "The actual field-exit gesture must dismiss suggestions before the strict Apply pointer.");
        Click(reopened, "GitToolsApply"); await Idle(reopened);
        Assert.That(reopened.LastError, Is.Null); Assert.That(await f.Read("local", "diff.guitool"), Is.Null);
        Assert.That(await f.Read("local", "difftool." + name + ".cmd"), Is.EqualTo(command));
        Click(reopened, "GitToolsCancel"); Assert.That(await reopenedDone, Is.False);
    }

    [AvaloniaTest]
    public async Task BrowseCancelPreservesCustomCommandAndAcceptedPathNeedsExplicitSuggestToReplaceIt()
    {
        await using Scope f = await Scope.CreateAsync();
        await f.Set("local", "diff.guitool", "meld"); await f.Set("local", "difftool.meld.path", "/old path");
        await f.Set("local", "difftool.meld.cmd", "custom --literal \"$LOCAL\"");
        string before = f.ConfigurationBytes(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(); await ChooseScope(dialog, 1);
        dialog.FilePicker = (_, _, _) => Task.FromResult<string?>(null);
        Click(dialog, "btnDiffToolBrowse"); await Idle(dialog);
        Assert.That(Field<TextBox>(dialog, "txtDiffToolPath").Text, Is.EqualTo("/old path"));
        Assert.That(Field<TextBox>(dialog, "txtDiffToolCommand").Text, Is.EqualTo("custom --literal \"$LOCAL\""));
        Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False); Assert.That(f.ConfigurationBytes(), Is.EqualTo(before));
        Edit(dialog, "txtDiffToolPath", "/focused typed path");
        Assert.That(Field<TextBox>(dialog, "txtDiffToolCommand").Text, Is.EqualTo("custom --literal \"$LOCAL\""));
        string selected = "/literal ' quote\\name";
        dialog.FilePicker = (_, _, _) => Task.FromResult<string?>(selected);
        Click(dialog, "btnDiffToolBrowse"); await Idle(dialog);
        Assert.That(Field<TextBox>(dialog, "txtDiffToolPath").Text, Is.EqualTo(selected));
        Assert.That(Field<TextBox>(dialog, "txtDiffToolCommand").Text, Is.EqualTo("custom --literal \"$LOCAL\""));
        Click(dialog, "btnDiffToolCommandSuggest");
        string suggested = Quote(selected) + " \"$LOCAL\" \"$REMOTE\"";
        Assert.That(Field<TextBox>(dialog, "txtDiffToolCommand").Text, Is.EqualTo(suggested));
        Click(dialog, "GitToolsApply"); await Idle(dialog); Assert.That(dialog.LastError, Is.Null);
        Assert.That(await f.Read("local", "difftool.meld.path"), Is.EqualTo(selected));
        Assert.That(await f.Read("local", "difftool.meld.cmd"), Is.EqualTo(suggested));
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
    }

    [AvaloniaTest]
    public async Task ClosingWithAnOutstandingPickerWaitsForItsReturnAndIgnoresTheLateSelection()
    {
        await using Scope f = await Scope.CreateAsync(); await f.Set("local", "diff.guitool", "meld");
        string before = f.ConfigurationBytes(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(); await ChooseScope(dialog, 1);
        TaskCompletionSource<string?> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.FilePicker = (_, _, _) => response.Task;
        try
        {
            Click(dialog, "btnDiffToolBrowse"); Assert.That(dialog.IsBusy, Is.True); Task operation = dialog.OperationCompletion;
            dialog.RequestClose(); Assert.That(done.IsCompleted, Is.False); Assert.That(operation.IsCompleted, Is.False);
            response.SetResult("/late ignored"); await Until(() => done.IsCompleted); await operation;
            Assert.That(await done, Is.False); Assert.That(dialog.MayHaveChangedConfiguration, Is.False);
            Assert.That(f.ConfigurationBytes(), Is.EqualTo(before));
        }
        finally { response.TrySetResult(null); }
    }

    [AvaloniaTest]
    public async Task ARealSecondScopeWriteFailureShowsReceiptsAndKeepsTheFirstPublishedScopeAfterCancel()
    {
        await using Scope f = await Scope.CreateAsync();
        await f.Set("local", "diff.guitool", "local old"); await f.Set("global", "diff.guitool", "global old");
        string wrapper = f.Wrapper("""
            case " $* " in
              *" config --global -- diff.guitool "*) printf 'controlled second-scope failure\n' >&2; exit 73;;
            esac
            exec git "$@"
            """);
        GitRepository repository = new(f.Git.Repository.Path, new GitRunner(wrapper));
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(repository);
        await ChooseScope(dialog, 1); EditName(dialog, "Diff", "local published");
        await ChooseScope(dialog, 2); EditName(dialog, "Diff", "global unsaved");
        Click(dialog, "GitToolsApply"); await Idle(dialog);
        Assert.That(dialog.LastSaveResult?.Success, Is.False); Assert.That(dialog.MayHaveChangedConfiguration, Is.True);
        Assert.That(dialog.LastSaveResult!.Writes.Any(write => write.Scope == GitConfigScope.Local && write.NativeResult?.Success == true), Is.True);
        Assert.That(dialog.LastSaveResult.Writes.Last().NativeResult?.ExitCode, Is.EqualTo(73));
        Assert.That(Field<TextBox>(dialog, "GitToolsError").Text, Does.Contain("controlled second-scope failure"));
        Assert.That(Field<ComboBox>(dialog, "_NO_TRANSLATE_cboDiffTool").Text, Is.EqualTo("global unsaved"));
        Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False, "A failed save requires an explicit readback review before another attempt.");
        Assert.That(await f.Read("local", "diff.guitool"), Is.EqualTo("local published"));
        Assert.That(await f.Read("global", "diff.guitool"), Is.EqualTo("global old"));
        await ChooseScope(dialog, 0); Assert.That(Field<TextBox>(dialog, "ReadOnlyDiffTool").Text, Is.EqualTo("local published"));
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
        Assert.That(await f.Read("local", "diff.guitool"), Is.EqualTo("local published"));
        Assert.That(await f.Read("global", "diff.guitool"), Is.EqualTo("global old"));
    }

    [AvaloniaTest]
    public async Task ACompletedNativeWriteMaskedByALaterIncludeKeepsOkOpenAndShowsTheActualWinningValue()
    {
        await using Scope f = await Scope.CreateAsync();
        // Git inserts the new key into the existing earlier [diff] section.
        // The later include still wins even though the native write itself succeeds.
        await f.Set("local", "diff.context", "3");
        string include = f.Git.Output("later-tool.config"); File.WriteAllText(include, "[diff]\n\tguitool = included winner\n");
        await f.Set("local", "include.path", include);
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(); await ChooseScope(dialog, 1);
        EditName(dialog, "Diff", "native request"); Click(dialog, "GitToolsAccept"); await Idle(dialog);
        Assert.That(done.IsCompleted, Is.False); Assert.That(dialog.IsVisible, Is.True);
        Assert.That(dialog.LastSaveResult?.Success, Is.True); Assert.That(dialog.MayHaveChangedConfiguration, Is.True);
        Assert.That(dialog.LastSaveResult!.Writes.Any(write => write.NativeResult?.Success == true && !string.IsNullOrEmpty(write.Diagnostic)), Is.True);
        Assert.That(Field<TextBox>(dialog, "GitToolsError").Text, Does.Contain("included winner"));
        Assert.That(Field<TextBox>(dialog, "GitToolsError").Text, Does.Contain("Published values"));
        Assert.That(Field<Button>(dialog, "GitToolsReload").IsVisible, Is.False, "A known After snapshot does not require an invented read failure.");
        Assert.That((await f.Git.GitAsync("config", "--local", "--includes", "--get", "diff.guitool")).StandardOutput, Is.EqualTo("included winner\n"));
        Assert.That(await f.Read("local", "diff.guitool"), Is.EqualTo("native request"));
        await ChooseScope(dialog, 0); Assert.That(Field<TextBox>(dialog, "ReadOnlyDiffTool").Text, Is.EqualTo("included winner"));
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
        Assert.That(await f.Read("local", "diff.guitool"), Is.EqualTo("native request"));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task OwnerCloseWaitsForTheActualCancelledGitProcessAndPreservesItsCancellationReceipt(bool nestedSettings)
    {
        await using Scope f = await Scope.CreateAsync();
        string pidFile = f.Git.Output("slow-write.pid"), drained = f.Git.Output("slow-write.drained");
        string wrapper = f.Wrapper("""
            case " $* " in
              *" config --local -- diff.guitool "*)
                printf '%s\n' "$$" > @PID@
                trap @TRAP@ INT TERM
                while :; do sleep 1; done;;
            esac
            exec git "$@"
            """.Replace("@PID@", Quote(pidFile), StringComparison.Ordinal)
            .Replace("@TRAP@", Quote("printf drained > " + Quote(drained) + "; exit 130"), StringComparison.Ordinal));
        Window outerOwner = f.Owner(), owner = outerOwner;
        if (nestedSettings)
        {
            DesktopSettingsDialog settings = new(new(), TranslationCatalog.LoadDefault(), true, configureGitTools: _ => Task.CompletedTask);
            f.Own(settings); _ = settings.ShowDialog<bool>(outerOwner); owner = settings;
        }
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(new(f.Git.Repository.Path, new GitRunner(wrapper)), owner);
        await ChooseScope(dialog, 1); EditName(dialog, "Diff", "cancel actual write"); Click(dialog, "GitToolsApply");
        await Until(() => File.Exists(pidFile)); int pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
        Assert.That(Alive(pid), Is.True); Task operation = dialog.OperationCompletion;
        outerOwner.Close(); // exactly one close request; the deferred relay must finish the whole chain
        await Until(() => done.IsCompleted && !owner.IsVisible && !outerOwner.IsVisible); await operation;
        Assert.That(await done, Is.False); Assert.That(dialog.LastSaveResult?.Cancelled, Is.True);
        Assert.That(dialog.LastSaveResult?.Writes.Last().NativeResult, Is.Null);
        Assert.That(dialog.MayHaveChangedConfiguration, Is.True);
        Assert.That(Alive(pid), Is.False, "The owner cannot disappear while its actual Git child is still alive.");
        Assert.That(File.Exists(drained), Is.True); Assert.That(await f.Read("local", "diff.guitool"), Is.Null);
    }

    [AvaloniaTest]
    public async Task MultiValueFieldsStayReadOnlyWhileAnUnrelatedRoleCanBeSavedWithoutFlatteningThem()
    {
        await using Scope f = await Scope.CreateAsync();
        await f.Set("local", "diff.guitool", "custom"); await f.Set("local", "difftool.custom.cmd", "first");
        await f.Git.GitAsync("config", "--local", "--add", "difftool.custom.cmd", "second");
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(); await ChooseScope(dialog, 1);
        Assert.That(Field<TextBox>(dialog, "txtDiffToolCommand").IsReadOnly, Is.True);
        Assert.That(ToolTip.GetTip(Field<TextBox>(dialog, "txtDiffToolCommand"))?.ToString(), Does.Contain("multiple"));
        EditName(dialog, "Merge", "separate merge"); Edit(dialog, "txtMergeToolCommand", "preserved exact \"$MERGED\"");
        Click(dialog, "GitToolsApply"); await Idle(dialog); Assert.That(dialog.LastError, Is.Null);
        Assert.That((await f.Git.GitAsync("config", "--local", "--get-all", "difftool.custom.cmd")).StandardOutput, Is.EqualTo("first\nsecond\n"));
        Assert.That(await f.Read("local", "merge.guitool"), Is.EqualTo("separate merge"));
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
    }

    [AvaloniaTest]
    public async Task OldGitShowsUnavailableDiagnosticRatherThanAnEditableEmptyConfiguration()
    {
        await using Scope f = await Scope.CreateAsync();
        string before = f.ConfigurationBytes();
        string wrapper = f.Wrapper("""
            if test "$1" = --version; then printf 'git version 2.25.9\n'; exit 0; fi
            exec git "$@"
            """);
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(new(f.Git.Repository.Path, new GitRunner(wrapper)), allowUnavailable: true);
        Assert.That(dialog.IsReady, Is.False); Assert.That(Field<TextBox>(dialog, "GitToolsError").Text, Does.Contain("2.26"));
        Assert.That(Field<Button>(dialog, "GitToolsAccept").IsEnabled, Is.False);
        await ChooseScope(dialog, 1); Assert.That(Field<TextBox>(dialog, "ReadOnlyDiffTool").IsVisible, Is.True);
        Assert.That(Field<Button>(dialog, "btnDiffToolBrowse").IsEnabled, Is.False);
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False); Assert.That(f.ConfigurationBytes(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task NestedSettingsKeepsScriptsAndFontsDraftsAndReturnsToSettingsAfterChildApplyThenCancel()
    {
        await using Scope f = await Scope.CreateAsync();
        ScriptsPreferencesStore scripts = new(); scripts.Save([new() { Name = "original script", Command = "/bin/true", HotkeyCommandIdentifier = 9001 }]);
        FontPreferencesStore fonts = new(); fonts.SaveChanges(new(), new() { ShowEolMarkerAsGlyph = true });
        byte[] scriptBytes = File.ReadAllBytes(scripts.FilePath), fontBytes = File.ReadAllBytes(fonts.FilePath);
        MainWindow main = new() { Width = 1280, Height = 900 }; f.Own(main); main.Show(); await main.OpenRepositoryAsync(f.Git.Repository.Path);
        await Until(() => !main.IsBusy); Press(main, PhysicalKey.Comma, RawInputModifiers.Control);
        await Until(() => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any());
        DesktopSettingsDialog settings = main.OwnedWindows.OfType<DesktopSettingsDialog>().Single(); Pump(settings);
        ClickControl(Field<TabItem>(settings, "SettingsScriptsTab")); Edit(settings, "ScriptName", "parent unsaved script");
        ClickControl(Field<TabItem>(settings, "SettingsFontsTab")); ClickControl(Field<CheckBox>(settings, "ShowEolMarkerAsGlyph"));
        bool eolDraft = Field<AppearanceFontsSettingsPage>(settings, "AppearanceFontsSettingsPage").Draft.ShowEolMarkerAsGlyph;
        ClickControl(Field<TabItem>(settings, "SettingsGeneralTab")); Click(settings, "OpenGitToolsConfiguration");
        await Until(() => settings.OwnedWindows.OfType<GitToolsSettingsDialog>().Any());
        GitToolsSettingsDialog child = settings.OwnedWindows.OfType<GitToolsSettingsDialog>().Single(); await child.InitializeAsync(); await Idle(child);
        Assert.That(child.Owner, Is.SameAs(settings)); Click(child, "GitToolsCancel");
        await Until(() => !child.IsVisible && !main.IsBusy && Field<Button>(settings, "OpenGitToolsConfiguration").IsEnabled); Pump(settings);
        Assert.That(settings.IsVisible && settings.IsEffectivelyEnabled, Is.True);
        Control? focused = settings.FocusManager?.GetFocusedElement() as Control;
        Assert.That(focused is not null && TopLevel.GetTopLevel(focused) == settings, Is.True,
            "Settings must regain its own focus; Main must not reinstall its history-grid focus.");
        ClickControl(Field<TabItem>(settings, "SettingsScriptsTab"));
        Assert.That(Field<ScriptsSettingsPage>(settings, "ScriptsSettingsPage").Draft[0].Name, Is.EqualTo("parent unsaved script"));
        ClickControl(Field<TabItem>(settings, "SettingsFontsTab"));
        Assert.That(Field<AppearanceFontsSettingsPage>(settings, "AppearanceFontsSettingsPage").Draft.ShowEolMarkerAsGlyph, Is.EqualTo(eolDraft));
        ClickControl(Field<TabItem>(settings, "SettingsGeneralTab"));
        Click(settings, "OpenGitToolsConfiguration"); await Until(() => settings.OwnedWindows.OfType<GitToolsSettingsDialog>().Any());
        child = settings.OwnedWindows.OfType<GitToolsSettingsDialog>().Single(); await child.InitializeAsync(); await Idle(child);
        await ChooseScope(child, 1); EditName(child, "Diff", "child published"); Click(child, "GitToolsApply"); await Idle(child);
        Assert.That(child.LastError, Is.Null); Click(child, "GitToolsCancel");
        await Until(() => !child.IsVisible && !main.IsBusy && Field<Button>(settings, "OpenGitToolsConfiguration").IsEnabled);
        Assert.That(File.ReadAllBytes(scripts.FilePath), Is.EqualTo(scriptBytes)); Assert.That(File.ReadAllBytes(fonts.FilePath), Is.EqualTo(fontBytes));
        Click(settings, "CancelDesktopSettings"); await Until(() => !settings.IsVisible && !main.IsBusy);
        Assert.That(await f.Read("local", "diff.guitool"), Is.EqualTo("child published"));
        Assert.That(File.ReadAllBytes(scripts.FilePath), Is.EqualTo(scriptBytes)); Assert.That(File.ReadAllBytes(fonts.FilePath), Is.EqualTo(fontBytes));
    }

    private sealed class Scope(ReferenceFixture git) : IAsyncDisposable
    {
        private readonly List<Window> _windows = [];
        internal ReferenceFixture Git => git;
        internal static async Task<Scope> CreateAsync()
        {
            ReferenceFixture git = await ReferenceFixture.CreateAsync();
            try
            {
                Directory.CreateDirectory(git.Output("home")); git.SetEnvironment("HOME", git.Output("home"));
                git.SetEnvironment("GIT_CONFIG_GLOBAL", git.Output("global.config")); File.WriteAllText(git.Output("global.config"), "");
                git.SetEnvironment("GIT_CONFIG_SYSTEM", git.Output("system.config")); File.WriteAllText(git.Output("system.config"), "");
                git.SetEnvironment("GIT_CONFIG_NOSYSTEM", null);
                git.SetEnvironment("GIT_CONFIG_COUNT", null); git.SetEnvironment("GIT_CONFIG_PARAMETERS", null);
                git.SetEnvironment("GIT_CONFIG", null);
                foreach (string name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
                    name.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal) || name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal)).ToArray())
                { git.SetEnvironment(name, null); }
                return new(git);
            }
            catch { git.Dispose(); throw; }
        }
        internal void Own(Window window) => _windows.Add(window);
        internal Window Owner(bool dark = false)
        {
            Window owner = new() { Width = 1100, Height = 800, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Content = new TextBox { Text = "Config owner" } };
            Own(owner); owner.Show(); return owner;
        }
        internal async Task<(GitToolsSettingsDialog Dialog, Task<bool> Done)> Open(GitRepository? repository = null, Window? owner = null, bool dark = false, bool allowUnavailable = false)
        {
            owner ??= Owner(dark); GitToolsSettingsDialog dialog = new(repository ?? git.Repository); Own(dialog);
            Task<bool> done = dialog.ShowDialog<bool>(owner); await dialog.InitializeAsync(); await Idle(dialog);
            if (!allowUnavailable) { Assert.That(dialog.IsReady, Is.True, dialog.LastError); Assert.That(dialog.LastError, Is.Null); }
            return (dialog, done);
        }
        internal Task<GitResult> Set(string scope, string key, string value) => git.GitAsync("config", "--" + scope, key, value);
        internal async Task<string?> Read(string scope, string key)
        {
            GitResult result = await new GitRunner().RunAsync(git.Repository.Path, ["config", "--" + scope, "--get", key]);
            Assert.That(result.ExitCode, Is.AnyOf(0, 1), result.StandardError);
            return result.ExitCode == 1 ? null : result.StandardOutput[..^1];
        }
        internal string ConfigurationBytes() => string.Join("|", new[] { git.PathInRepository(".git/config"), git.Output("global.config"), git.Output("system.config") }
            .Select(path => Convert.ToHexString(File.ReadAllBytes(path))));
        internal string Wrapper(string body)
        {
            string file = git.Output("git-wrapper-" + Guid.NewGuid().ToString("N")); File.WriteAllText(file, "#!/bin/sh\nset -eu\n" + body + "\n");
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            return file;
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window window in _windows.AsEnumerable().Reverse())
                {
                    foreach (Window child in Descendants(window).Reverse().ToArray()) { if (child.IsVisible) { child.Close(); } }
                    if (window.IsVisible) { window.Close(); }
                    if (window is GitToolsSettingsDialog tools) { await tools.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(20)); }
                    if (window is MainWindow main) { await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(20)); }
                    await Until(() => !window.IsVisible);
                }
            }
            finally { git.Dispose(); }
        }
    }

    private static async Task ChooseScope(GitToolsSettingsDialog dialog, int index)
    {
        ComboBox combo = Field<ComboBox>(dialog, "GitToolsScope"); ClickControl(combo); Assert.That(combo.IsDropDownOpen, Is.True);
        Press(dialog, PhysicalKey.Home); for (int i = 0; i < index; i++) { Press(dialog, PhysicalKey.ArrowDown); }
        Press(dialog, PhysicalKey.Enter); await Task.Yield(); Pump(dialog); Assert.That(combo.SelectedIndex, Is.EqualTo(index));
    }
    private static void EditName(Window window, string role, string value)
    {
        ComboBox combo = Field<ComboBox>(window, "_NO_TRANSLATE_cbo" + role + "Tool"); Pump(window);
        TextBox text = combo.GetVisualDescendants().OfType<TextBox>().First(); EditControl(window, text, value);
        Assert.That(combo.Text, Is.EqualTo(value));
    }
    private static void Edit(Window window, string name, string value) => EditControl(window, Field<TextBox>(window, name), value);
    private static void EditControl(Window window, TextBox text, string value)
    {
        text.BringIntoView(); Pump(window); ClickControl(text); Press(window, PhysicalKey.A, RawInputModifiers.Control);
        if (value.Length == 0) { Press(window, PhysicalKey.Backspace); }
        else { window.KeyTextInput(value); }
        Pump(window); Assert.That(text.Text, Is.EqualTo(value));
    }
    private static void Click(Window window, string name) => ClickControl(Field<Button>(window, name));
    private static void ClickControl(Control control) { control.BringIntoView(); PushProcessTestDriver.Pointer(control); }
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static Task Idle(GitToolsSettingsDialog dialog) => Until(() => !dialog.IsBusy);
    private static async Task Until(Func<bool> complete)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!complete() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs(); Assert.That(complete(), Is.True, "The real Config operation, owner close or native wait did not settle.");
    }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Dispatcher.UIThread.RunJobs(); }
    private static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static bool Alive(int pid)
    { try { using Process process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static IEnumerable<Window> Descendants(Window root)
    { foreach (Window child in root.OwnedWindows.ToArray()) { yield return child; foreach (Window nested in Descendants(child)) { yield return nested; } } }
    private static void Capture(Window dialog, string name)
    {
        Pump(dialog); using var frame = dialog.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string file = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame!.Save(file); TestContext.AddTestAttachment(file);
    }
}

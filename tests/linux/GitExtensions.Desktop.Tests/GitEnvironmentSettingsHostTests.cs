using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GitEnvironmentSettingsHostTests
{
    [AvaloniaTest]
    public async Task SaveAndRescanPublishesEveryEditedPageBeforeTheFirstProbeAndCancelKeepsThosePublications()
    {
        await using Host f = new(); f.Open();
        Click(f.Dialog, "SettingsNormaliseBranches");
        Click(f.Dialog, "SettingsFontsTab"); Click(f.Dialog, "ShowEolMarkerAsGlyph");
        Click(f.Dialog, "SettingsScriptsTab"); Click(f.Dialog, "btnAdd");
        Edit(f.Dialog, "ScriptName", "Host publication script"); Edit(f.Dialog, "ScriptCommand", "/bin/true");
        Click(f.Dialog, "SettingsHotkeysTab");
        ListBox contexts = Field<ListBox>(f.Dialog, "cmbSettings"); contexts.ScrollIntoView("Browse"); Pump(f.Dialog);
        PushProcessTestDriver.Pointer((Control)contexts.ContainerFromItem("Browse")!); Pump(f.Dialog);
        ListBox mappings = Field<ListBox>(f.Dialog, "listMappings"); HotkeyCommand command = mappings.Items.OfType<HotkeyCommand>().Single(item => item.Name == "CreateBranch");
        mappings.ScrollIntoView(command); Pump(f.Dialog); PushProcessTestDriver.Pointer((Control)mappings.ContainerFromItem(command)!); Click(f.Dialog, "bClear");
        Click(f.Dialog, "SettingsGitTab"); Edit(f.Dialog, "GitPath", f.SelectedGit);
        await f.ShowChecklistAndWaitAsync(); f.ClearProbeLog();
        Assert.That(f.EnvironmentStore.Load().Preferences.GitExecutable, Is.EqualTo(f.InitialGit));
        f.BeforePublish = settings =>
        {
            Assert.That(f.EnvironmentStore.Load().Preferences.GitExecutable, Is.EqualTo(f.SelectedGit));
            Assert.That(f.FontStore.Load().ShowEolMarkerAsGlyph, Is.True);
            Assert.That(f.ScriptsStore.Load().Scripts.Any(script => script.Name == "Host publication script" && script.Command == "/bin/true"), Is.True);
            Assert.That(f.HotkeyStore.Load().Get("Browse", command.Code), Is.Null);
            Assert.That(settings.Result.AutoNormaliseBranchName, Is.False);
            Assert.That(File.ReadAllText(f.ProbeLog), Is.Empty, "Native scan must wait for the entire host publication.");
        };
        Click(f.Dialog, "Rescan"); await f.WaitForScanAsync();
        Assert.That(f.Publications, Is.EqualTo(1)); Assert.That(f.Session.LastScan, Is.Not.Null);
        Assert.That(File.ReadAllText(f.ProbeLog), Does.Contain("selected:published").And.Not.Contain("selected:unpublished"));
        Assert.That(f.GeneralStore.Load().AutoNormaliseBranchName, Is.False); Assert.That(f.Dialog.IsVisible, Is.True);
        Assert.That(f.Session.Page.IsDirty, Is.False);
        Click(f.Dialog, "CancelDesktopSettings"); Assert.That(await f.Completion, Is.False);
        Assert.That(f.EnvironmentStore.Load().Preferences.GitExecutable, Is.EqualTo(f.SelectedGit));
        Assert.That(f.GeneralStore.Load().AutoNormaliseBranchName, Is.False); Assert.That(f.FontStore.Load().ShowEolMarkerAsGlyph, Is.True);
        Assert.That(f.ScriptsStore.Load().Scripts.Any(script => script.Name == "Host publication script"), Is.True);
        Assert.That(f.HotkeyStore.Load().Get("Browse", command.Code), Is.Null);
    }

    [AvaloniaTest]
    public async Task CancelAfterAnInstantStartupToggleKeepsOnlyTheFlagAndDiscardsThePendingGitPath()
    {
        await using Host f = new(); f.Open(); Click(f.Dialog, "SettingsGitTab"); Edit(f.Dialog, "GitPath", f.SelectedGit);
        await f.ShowChecklistAndWaitAsync(); Assert.That(f.Session.Checklist.CheckAtStartup, Is.True); f.ClearProbeLog();
        Click(f.Dialog, "CheckAtStartup");
        Assert.That(f.EnvironmentStore.Load().Preferences.CheckAtStartup, Is.False);
        Assert.That(f.EnvironmentStore.Load().Preferences.GitExecutable, Is.EqualTo(f.InitialGit));
        Assert.That(f.Session.Page.Draft.GitExecutable, Is.EqualTo(f.SelectedGit)); Assert.That(f.Session.Page.IsDirty, Is.True);
        Assert.That(GitEnvironment.Options.Executable, Is.EqualTo(f.InitialGit));
        Click(f.Dialog, "CancelDesktopSettings"); Assert.That(await f.Completion, Is.False);
        Assert.That(f.EnvironmentStore.Load().Preferences.GitExecutable, Is.EqualTo(f.InitialGit));
        Assert.That(f.EnvironmentStore.Load().Preferences.CheckAtStartup, Is.False);
        Assert.That(File.ReadAllText(f.ProbeLog), Is.Empty); Assert.That(f.Publications, Is.Zero);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ClosingSettingsOrItsOwnerDuringANativeScanCancelsDrainsAndIgnoresTheAbortedResult(bool closeOwner)
    {
        await using Host f = new(); File.WriteAllText(f.Gate, "block version"); f.Open(showChecklist: true);
        await Until(() => File.Exists(f.Ready) && int.TryParse(File.ReadAllText(f.Ready), CultureInfo.InvariantCulture, out _)); Assert.That(f.Session.IsBusy, Is.True);
        int pid = int.Parse(File.ReadAllText(f.Ready), CultureInfo.InvariantCulture);
        if (closeOwner) { f.Owner.Close(); } else { Click(f.Dialog, "CancelDesktopSettings"); }
        Assert.That(f.Dialog.IsVisible || f.Session.OperationCompletion.IsCompleted, Is.True, "The host may close only after the native operation has drained.");
        await Until(() => f.Completion.IsCompleted); Assert.That(await f.Completion, Is.False); await f.Session.OperationCompletion;
        Assert.That(IsAlive(pid), Is.False); Assert.That(f.Session.LastScan, Is.Null);
        Assert.That(f.EnvironmentStore.Load().Preferences.CheckAtStartup, Is.True);
        Assert.That(f.Publications, Is.Zero); Assert.That(f.Session.Page.IsInvalidated, Is.True); Assert.That(f.Session.Checklist.IsInvalidated, Is.True);
        await Task.Yield(); Dispatcher.UIThread.RunJobs(); Assert.That(f.Dialog.IsVisible, Is.False);
        if (closeOwner) { await Until(() => !f.Owner.IsVisible); }
    }

    [AvaloniaTest]
    public async Task AHostPublicationFailureAfterTheEnvironmentSaveLeavesTheActualPublicationVisibleAndNeverStartsTheScan()
    {
        await using Host f = new(); f.Open(); Click(f.Dialog, "SettingsGitTab"); Edit(f.Dialog, "GitPath", f.SelectedGit);
        await f.ShowChecklistAndWaitAsync(); f.ClearProbeLog();
        f.PublishFailure = new IOException("desktop profile locked <literal>"); Click(f.Dialog, "Rescan");
        await Until(() => Field<TextBlock>(f.Dialog, "SettingsNotice").Text?.Contains("desktop profile locked <literal>", StringComparison.Ordinal) == true);
        Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Session.Page.IsDirty, Is.False);
        Assert.That(f.EnvironmentStore.Load().Preferences.GitExecutable, Is.EqualTo(f.SelectedGit));
        Assert.That(GitEnvironment.Options.Executable, Is.EqualTo(f.SelectedGit));
        Assert.That(File.ReadAllText(f.ProbeLog), Is.Empty); Assert.That(f.Session.LastScan, Is.Null);
        Click(f.Dialog, "CancelDesktopSettings"); Assert.That(await f.Completion, Is.False);
        Assert.That(f.EnvironmentStore.Load().Preferences.GitExecutable, Is.EqualTo(f.SelectedGit));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ValidationOrAnEarlierSaveFailurePreventsGitPublicationAndRescanning(bool failHotkeys)
    {
        await using Host f = new(); f.Open();
        if (!failHotkeys) { Edit(f.Dialog, "SettingsNormaliseSymbol", ""); }
        Click(f.Dialog, "SettingsGitTab"); Edit(f.Dialog, "GitPath", f.SelectedGit);
        await f.ShowChecklistAndWaitAsync(); f.ClearProbeLog();
        if (failHotkeys) { f.HotkeyFailure = new IOException("hotkey profile locked <literal>"); }
        Click(f.Dialog, "Rescan"); Pump(f.Dialog);
        string notice = Field<TextBlock>(f.Dialog, "SettingsNotice").Text ?? "";
        Assert.That(notice, Does.Contain(failHotkeys ? "hotkey profile locked <literal>" : "Enter a valid history limit and replacement symbol."));
        Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Session.Page.IsDirty, Is.True);
        Assert.That(f.EnvironmentStore.Load().Preferences.GitExecutable, Is.EqualTo(f.InitialGit));
        Assert.That(GitEnvironment.Options.Executable, Is.EqualTo(f.InitialGit));
        Assert.That(File.ReadAllText(f.ProbeLog), Is.Empty); Assert.That(f.Publications, Is.Zero);
        Click(f.Dialog, "CancelDesktopSettings"); Assert.That(await f.Completion, Is.False);
    }

    private static bool IsAlive(int pid)
    { try { using Process process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static void Click(Window window, string name) { Pump(window); PushProcessTestDriver.Pointer(Field<Control>(window, name)); Pump(window); }
    private static void Edit(Window window, string name, string text)
    {
        TextBox editor = Field<TextBox>(window, name); editor.BringIntoView(); Pump(window); PushProcessTestDriver.Pointer(editor);
        Press(window, PhysicalKey.A, RawInputModifiers.Control); Press(window, PhysicalKey.Backspace);
        if (text.Length > 0) { window.KeyTextInput(text); } Pump(window); Assert.That(editor.Text, Is.EqualTo(text));
    }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Dispatcher.UIThread.RunJobs(); }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
        Assert.That(condition(), Is.True);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly Dictionary<string, string?> _savedEnvironment;
        private readonly GitEnvironmentOptions _savedOptions = GitEnvironment.Options;
        private readonly CultureInfo _savedCulture = CultureInfo.CurrentUICulture;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-settings-host-" + Guid.NewGuid().ToString("N"));
        internal string ProbeLog => Path.Combine(Root, "probes.log");
        internal string Published => Path.Combine(Root, "host-published");
        internal string Gate => Path.Combine(Root, "block-version");
        internal string Ready => Path.Combine(Root, "version-ready");
        internal string InitialGit { get; }
        internal string SelectedGit { get; }
        internal GitEnvironmentPreferencesStore EnvironmentStore { get; }
        internal PreferencesStore GeneralStore { get; }
        internal FontPreferencesStore FontStore { get; }
        internal ScriptsPreferencesStore ScriptsStore { get; }
        internal HotkeyPreferencesStore HotkeyStore { get; }
        internal GitEnvironmentSettingsSession Session { get; }
        internal Window Owner { get; }
        internal DesktopSettingsDialog Dialog { get; private set; } = null!;
        internal Task<bool> Completion { get; private set; } = Task.FromResult(false);
        internal Action<DesktopSettingsDialog>? BeforePublish { get; set; }
        internal Exception? PublishFailure { get; set; }
        internal Exception? HotkeyFailure { get; set; }
        internal int Publications { get; private set; }

        internal Host()
        {
            string[] names = ["HOME", "XDG_CONFIG_HOME", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_COUNT", "GITEXTENSIONS_UI_CULTURE", "GIT_EDITOR", "VISUAL", "EDITOR"];
            _savedEnvironment = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            Directory.CreateDirectory(Root); string home = Path.Combine(Root, "home"); Directory.CreateDirectory(home);
            string profile = Path.Combine(Root, "profile"), global = Path.Combine(Root, "global.config");
            File.WriteAllText(global, "[user]\n name = Host User\n email = host@example.invalid\n[core]\n editor = /bin/true\n");
            foreach ((string name, string? value) in new Dictionary<string, string?>
            {
                ["HOME"] = home, ["XDG_CONFIG_HOME"] = profile, ["GIT_CONFIG_GLOBAL"] = global,
                ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_COUNT"] = "0", ["GITEXTENSIONS_UI_CULTURE"] = null,
                ["GIT_EDITOR"] = null, ["VISUAL"] = null, ["EDITOR"] = null
            }) { Environment.SetEnvironmentVariable(name, value); }
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            InitialGit = Proxy("initial"); SelectedGit = Proxy("selected"); ClearProbeLog();
            EnvironmentStore = new(); GeneralStore = new(); FontStore = new(); ScriptsStore = new(); HotkeyStore = new();
            GeneralStore.Save(new() { Language = "English" });
            GitEnvironmentPreferences preferences = new() { GitExecutable = InitialGit };
            EnvironmentStore.SaveChanges(EnvironmentStore.Load(), preferences); GitEnvironment.Configure(GitEnvironmentSettingsSession.Options(preferences));
            TranslationCatalog translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo("en"));
            Session = new(EnvironmentStore, translations, () => null);
            Owner = new Window { Width = 1000, Height = 800 }; Owner.Show(); Pump(Owner);
        }

        internal void Open(bool showChecklist = false)
        {
            Dialog = new(GeneralStore.Load(), TranslationCatalog.LoadDefault(), false,
                hotkeys: HotkeyStore.Load(), saveHotkeys: bindings => { if (HotkeyFailure is { } error) { throw error; } HotkeyStore.Save(bindings); },
                fonts: FontStore.Load(), saveFonts: FontStore.SaveChanges,
                scripts: ScriptsStore.Load(), saveScripts: ScriptsStore.SaveChanges,
                gitEnvironment: Session, publishSettings: settings =>
                {
                    BeforePublish?.Invoke(settings); if (PublishFailure is { } error) { throw error; }
                    GeneralStore.Save(settings.Result); Publications++; File.WriteAllText(Published, "published"); return Task.CompletedTask;
                }, showChecklist: showChecklist);
            Completion = Dialog.ShowDialog<bool>(Owner); Pump(Dialog);
        }
        internal async Task ShowChecklistAndWaitAsync() { Click(Dialog, "SettingsChecklistTab"); await WaitForScanAsync(); }
        internal async Task WaitForScanAsync() { await Session.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(15)); await Until(() => !Session.IsBusy); Pump(Dialog); }
        internal void ClearProbeLog() { File.WriteAllText(ProbeLog, ""); if (File.Exists(Published)) { File.Delete(Published); } }
        private string Proxy(string label)
        {
            string path = Path.Combine(Root, label + " git Ω");
            File.WriteAllText(path, "#!/bin/sh\nif test \"$1\" = --version; then\n"
                + " if test -f " + Quote(Published) + "; then printf '%s\\n' " + Quote(label + ":published") + "; else printf '%s\\n' " + Quote(label + ":unpublished") + "; fi >> " + Quote(ProbeLog) + "\n"
                + " if test -f " + Quote(Gate) + "; then printf '%s' \"$$\" > " + Quote(Ready) + "; /bin/sleep 60; fi\n"
                + " printf '%s\\n' 'git version 2.53.0'\n exit 0\nfi\nexec /usr/bin/git \"$@\"\n", new UTF8Encoding(false));
            if (OperatingSystem.IsLinux()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            return path;
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Session.Page.IsInvalidated) { Session.Cancel(); }
                if (Dialog is not null && Dialog.IsVisible) { Dialog.Close(false); }
                await Session.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(15)); Dispatcher.UIThread.RunJobs();
                if (Dialog is not null && Dialog.IsVisible) { Dialog.Close(false); }
                Owner.Close(); Session.Dispose(); Dispatcher.UIThread.RunJobs();
            }
            finally
            {
                foreach ((string name, string? value) in _savedEnvironment) { Environment.SetEnvironmentVariable(name, value); }
                CultureInfo.CurrentUICulture = _savedCulture; GitEnvironment.Configure(_savedOptions);
                if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
            }
        }
        private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}

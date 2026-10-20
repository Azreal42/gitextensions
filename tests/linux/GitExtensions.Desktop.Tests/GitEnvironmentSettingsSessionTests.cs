using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable, Platform("Linux")]
public sealed class GitEnvironmentSettingsSessionTests
{
    [Test]
    public async Task GlobalContextCannotDiscoverOrWriteTheInvokingRepositoryEvenWithInheritedRepositoryOverrides()
    {
        using Fixture f = await Fixture.CreateAsync();
        Dictionary<string, string?> values = new(f.Environment.EnvironmentVariables, StringComparer.Ordinal)
        {
            ["GIT_DIR"] = Path.Combine(f.Repository.Path, ".git"), ["GIT_WORK_TREE"] = f.Repository.Path,
            ["GIT_INDEX_FILE"] = Path.Combine(f.Repository.Path, ".git", "index"),
            ["GIT_OBJECT_DIRECTORY"] = Path.Combine(f.Repository.Path, ".git", "objects")
        };
        GitExecutionEnvironment inherited = GitExecutionEnvironment.Capture(new() { Executable = "/usr/bin/git" }, values);
        string before = f.RepositoryData(includeGlobal: false), temporary;
        using (GitConfigurationContext context = GitConfigurationContext.Create(null, inherited, f.Repository.Path))
        {
            temporary = context.Repository.Path;
            Assert.That(context.GlobalOnly, Is.True);
            Assert.That(context.Environment.GetVariable("GIT_CEILING_DIRECTORIES"), Is.EqualTo(temporary));
            foreach (string key in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY" })
            { Assert.That(context.Environment.GetVariable(key), Is.Null); }
            GitToolConfigurationSnapshot snapshot = await context.Repository.GetToolConfigurationAsync();
            Assert.That(snapshot.GetScope(GitToolConfigurationView.Local).IsAvailable, Is.False);
            Assert.That(snapshot.GetScope(GitToolConfigurationView.Global).CanWrite, Is.True);
            GitToolConfigurationSaveResult saved = await context.Repository.SaveToolConfigurationAsync(snapshot,
                [new(GitConfigScope.Global, [], [new(GitConfigurationField.UserName, "Written only to global Ω")])]);
            Assert.That(saved.Success, Is.True);
            Assert.That(saved.After!.GetScope(GitToolConfigurationView.Global).GetSetting(GitConfigurationField.UserName).Occurrences.Last().Value,
                Is.EqualTo("Written only to global Ω"));
            GitResult discovery = await context.Environment.CreateRunner().RunAsync(temporary, ["rev-parse", "--show-toplevel"]);
            Assert.That(discovery.Success, Is.False);
            Assert.That(f.RepositoryData(includeGlobal: false), Is.EqualTo(before));
        }
        Assert.That(Directory.Exists(temporary), Is.False);
        Assert.That(inherited.GetVariable("GIT_DIR"), Is.EqualTo(Path.Combine(f.Repository.Path, ".git")));
        Assert.That(f.RepositoryData(includeGlobal: false), Is.EqualTo(before));
    }

    [Test]
    public async Task GlobalContextPreservesTheInvokingMeaningOfRelativeLocationsAndPathEntries()
    {
        using Fixture f = await Fixture.CreateAsync();
        string tools = Path.Combine(f.Root, "relative tools");
        Executable(tools, "git proxy", "exec /usr/bin/git \"$@\"\n");
        File.WriteAllText(Path.Combine(f.Root, "system.config"), "[user]\nname = system must lose\n");
        GitExecutionEnvironment inherited = GitExecutionEnvironment.Capture(new() { Executable = "./relative tools/git proxy" },
            new Dictionary<string, string?>
            {
                ["HOME"] = "home", ["XDG_CONFIG_HOME"] = "xdg", ["GIT_CONFIG_GLOBAL"] = "global.config",
                ["GIT_CONFIG_SYSTEM"] = "system.config", ["PATH"] = "relative tools::/usr/bin:/bin"
            });
        string before = f.RepositoryData();
        using GitConfigurationContext context = GitConfigurationContext.Create(null, inherited, f.Root);
        Assert.That(context.Environment.Executable, Is.EqualTo(Path.Combine(tools, "git proxy")));
        Assert.That(context.Environment.HomeDirectory, Is.EqualTo(f.Home));
        Assert.That(context.Environment.GetVariable("XDG_CONFIG_HOME"), Is.EqualTo(Path.Combine(f.Root, "xdg")));
        Assert.That(context.Environment.GetVariable("GIT_CONFIG_GLOBAL"), Is.EqualTo(f.Global));
        Assert.That(context.Environment.GetVariable("GIT_CONFIG_SYSTEM"), Is.EqualTo(Path.Combine(f.Root, "system.config")));
        Assert.That(context.Environment.GetVariable("PATH")!.Split(Path.PathSeparator),
            Is.EqualTo(new[] { tools, f.Root, "/usr/bin", "/bin" }));
        GitToolConfigurationSnapshot configuration = await context.Repository.GetToolConfigurationAsync();
        Assert.That(configuration.GetScope(GitToolConfigurationView.Global).GetSetting(GitConfigurationField.UserName).Occurrences.Last().Value,
            Is.EqualTo("Global Ω"));
        Assert.That(configuration.GetScope(GitToolConfigurationView.Local).IsAvailable, Is.False);
        Assert.That(inherited.HomeDirectory, Is.EqualTo("home"));
        Assert.That(inherited.GetVariable("GIT_CONFIG_GLOBAL"), Is.EqualTo("global.config"));
        Assert.That(f.RepositoryData(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task StartupCheckboxPublishesImmediatelyWithoutPublishingAnyPendingPaths()
    {
        using Fixture f = await Fixture.CreateAsync();
        using SessionHost host = new(f);
        string draftGit = f.VersionProxy("pending git Ω", "git version 2.53.0");
        string draftTools = Path.Combine(f.Root, "pending tools");
        Field<TextBox>(host.Window, "GitPath").Text = draftGit;
        Field<TextBox>(host.Window, "LinuxToolsDir").Text = draftTools;
        string before = f.RepositoryData();
        GitEnvironmentOptions options = GitEnvironment.Options;

        Field<CheckBox>(host.Window, "CheckAtStartup").IsChecked = false;
        GitEnvironmentPreferences published = f.Store.Load().Preferences;
        Assert.That(published, Is.EqualTo(f.Initial with { CheckAtStartup = false }));
        Assert.That(host.Session.Page.Draft.GitExecutable, Is.EqualTo(draftGit));
        Assert.That(host.Session.Page.Draft.ToolsDirectory, Is.EqualTo(draftTools));
        Assert.That(host.Session.Page.Draft.CheckAtStartup, Is.False);
        Assert.That(host.Session.Page.IsDirty, Is.True);
        Assert.That(GitEnvironment.Options, Is.EqualTo(options));
        Assert.That(f.RepositoryData(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task AnOlderWindowRefusesStalePublicationsAndReloadRebasesItsExplicitDraftBeforeSaving()
    {
        using Fixture f = await Fixture.CreateAsync();
        using SessionHost older = new(f), newer = new(f);
        string draftGit = f.VersionProxy("older draft git", "git version 2.53.0");
        string newerTools = Path.Combine(f.Root, "newer published tools");
        Field<TextBox>(older.Window, "GitPath").Text = draftGit;
        Field<TextBox>(newer.Window, "LinuxToolsDir").Text = newerTools;
        Assert.That(newer.Session.TrySave(), Is.True);
        byte[] newerProfile = File.ReadAllBytes(f.Store.FilePath);
        string before = f.RepositoryData();

        Assert.That(older.Session.TrySave(), Is.False);
        Assert.That(Field<TextBlock>(older.Window, "GitEnvironmentError").Text, Does.Contain("changed"));
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(newerProfile));
        Field<CheckBox>(older.Window, "CheckAtStartup").IsChecked = false;
        Assert.That(older.Session.Checklist.CheckAtStartup, Is.True, "A stale immediate flag publication must be visibly refused too.");
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(newerProfile));

        older.Session.Reload();
        Assert.That(older.Session.Page.Draft.GitExecutable, Is.EqualTo(draftGit));
        Assert.That(older.Session.Page.Draft.ToolsDirectory, Is.EqualTo(newerTools));
        Assert.That(older.Session.TrySave(), Is.True);
        Assert.That(f.Store.Load().Preferences, Is.EqualTo(f.Initial with { GitExecutable = draftGit, ToolsDirectory = newerTools }));
        Assert.That(older.Session.Page.IsDirty, Is.False);
        Assert.That(GitEnvironment.Options.Executable, Is.EqualTo(draftGit));
        Assert.That(f.RepositoryData(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task RescanAdoptsAnotherWindowsPublishedContextWhileKeepingUnpublishedPathsAsDrafts()
    {
        using Fixture f = await Fixture.CreateAsync();
        using SessionHost host = new(f);
        string draftMarker = Path.Combine(f.Root, "draft-executable-was-used");
        string draftGit = Executable(f.Root, "pending executable", ": > " + Quote(draftMarker) + "\nexec /usr/bin/git \"$@\"\n");
        Field<TextBox>(host.Window, "GitPath").Text = draftGit;
        string publishedGit = f.VersionProxy("other published git", "git version 2.43.0");
        GitEnvironmentPreferences published = f.Initial with { GitExecutable = publishedGit, ToolsDirectory = Path.Combine(f.Root, "published tools") };
        f.Store.SaveChanges(f.Store.Load(), published);
        byte[] profile = File.ReadAllBytes(f.Store.FilePath);
        string before = f.RepositoryData();

        await host.Session.ScanAsync();
        Assert.That(host.Session.LastScan!.Diagnostic.Git.Executable, Is.EqualTo(publishedGit));
        Assert.That(host.Session.LastScan.Diagnostic.Version, Is.EqualTo(new Version(2, 43, 0)));
        Assert.That(host.Session.Page.Draft.GitExecutable, Is.EqualTo(draftGit));
        Assert.That(host.Session.Page.Draft.ToolsDirectory, Is.EqualTo(published.ToolsDirectory));
        Assert.That(host.Session.Page.IsDirty, Is.True);
        Assert.That(File.Exists(draftMarker), Is.False);
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(profile));
        Assert.That(GitEnvironment.Options.Executable, Is.EqualTo(publishedGit));
        Assert.That(f.RepositoryData(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(true)]
    [TestCase(false)]
    public async Task OnlyACompleteAllGoodScanAutomaticallyDisablesTheSavedStartupCheck(bool good)
    {
        using Fixture f = await Fixture.CreateAsync();
        if (!good)
        {
            GitEnvironmentPreferences changed = f.Initial with { GitExecutable = f.VersionProxy("unsupported git", "git version 2.42.9") };
            f.Store.SaveChanges(f.Store.Load(), changed);
        }
        using SessionHost host = new(f);
        string before = f.RepositoryData();
        byte[] desktopPreferences = File.ReadAllBytes(new PreferencesStore().FilePath);
        GitEnvironmentPreferences published = f.Store.Load().Preferences;

        await host.Session.ScanAsync();
        Assert.That(host.Session.LastScan, Is.Not.Null);
        Assert.That(host.Session.LastScan!.AllGood, Is.EqualTo(good), string.Join("\n", host.Session.LastScan.Checks));
        Assert.That(host.Session.HasProblems, Is.EqualTo(!good));
        Assert.That(host.Session.IsBusy, Is.False);
        Assert.That(host.Session.OperationCompletion.IsCompleted, Is.True);
        Assert.That(f.Store.Load().Preferences, Is.EqualTo(published with { CheckAtStartup = !good }));
        Assert.That(host.Session.Checklist.CheckAtStartup, Is.EqualTo(!good));
        Assert.That(File.ReadAllBytes(new PreferencesStore().FilePath), Is.EqualTo(desktopPreferences));
        Assert.That(f.RepositoryData(), Is.EqualTo(before));
        host.Window.Content = host.Session.SshPage; Pump(host.Window);
        Assert.That(Field<TextBlock>(host.Window, "SshEnvironmentInformation").Text,
            Does.Contain("SSH executable version check:").And.Contain("SSH authentication has not been tested."));
    }

    [AvaloniaTest]
    public async Task AnUnreadableApplicationProfileIsNeverAutoReplacedByOtherwiseGoodObservations()
    {
        using Fixture f = await Fixture.CreateAsync();
        using SessionHost host = new(f);
        File.WriteAllText(f.Store.FilePath, "{corrupt application profile\r\n");
        byte[] profile = File.ReadAllBytes(f.Store.FilePath);
        string before = f.RepositoryData();

        await host.Session.ScanAsync();
        Assert.That(host.Session.LastScan!.AllGood, Is.True, string.Join("\n", host.Session.LastScan.Checks));
        Assert.That(host.Session.HasProblems, Is.True);
        Assert.That(Field<TextBlock>(host.Window, "GitEnvironmentError").IsVisible, Is.True);
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(profile));
        Assert.That(f.RepositoryData(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationOrDisposalDuringANativeProbeDrainsBeforeCompletionWithoutAnyLatePublication(bool dispose)
    {
        using Fixture f = await Fixture.CreateAsync();
        string ready = Path.Combine(f.Root, "native-ready"), pidFile = Path.Combine(f.Root, "native-pid");
        string git = Executable(f.Root, "gated version git", "if test \"$1\" = --version; then\nprintf '%s' \"$$\" > " + Quote(pidFile)
            + "\n: > " + Quote(ready) + "\nwhile :; do /bin/sleep 0.01; done\nfi\nexec /usr/bin/git \"$@\"\n");
        f.Store.SaveChanges(f.Store.Load(), f.Initial with { GitExecutable = git });
        using SessionHost host = new(f);
        byte[] profile = File.ReadAllBytes(f.Store.FilePath);
        string before = f.RepositoryData();
        Task scan = host.Session.ScanAsync();
        bool disposed = false;
        try
        {
            await Until(() => File.Exists(ready));
            Assert.That(host.Session.IsBusy, Is.True);
            Assert.That(host.Session.OperationCompletion.IsCompleted, Is.False);
            Assert.That(host.Session.TrySave(), Is.False);
            int pid = int.Parse(File.ReadAllText(pidFile), CultureInfo.InvariantCulture);
            if (dispose) { host.Session.Dispose(); disposed = true; }
            else { host.Session.Cancel(); }
            await scan.WaitAsync(TimeSpan.FromSeconds(10));
            await host.Session.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(ProcessExited(pid), Is.True, "The session completion must follow actual native process drainage.");
            Assert.That(host.Session.IsBusy, Is.False);
            Assert.That(host.Session.LastScan, Is.Null);
            Assert.That(host.Session.HasProblems, Is.True);
            Assert.That(host.Session.TrySave(), Is.False);
            Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(profile));
            Assert.That(f.RepositoryData(), Is.EqualTo(before));
        }
        finally
        {
            if (!disposed) { host.Session.Cancel(); }
            await scan.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static bool ProcessExited(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(Window window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True);
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static string Executable(string directory, string name, string body)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body, new UTF8Encoding(false));
        if (OperatingSystem.IsLinux()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        return path;
    }

    private sealed class SessionHost : IDisposable
    {
        internal GitEnvironmentSettingsSession Session { get; }
        internal Window Window { get; }
        internal SessionHost(Fixture f)
        {
            Session = new(f.Store, TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo("en")), () => f.Repository);
            Window = new() { Width = 980, Height = 900, Content = new StackPanel { Children = { Session.Page, Session.Checklist } } };
            Window.Show(); Pump(Window);
        }
        public void Dispose() { Session.Dispose(); Window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-environment-session-" + Guid.NewGuid().ToString("N"));
        internal string Home => Path.Combine(Root, "home");
        internal string Global => Path.Combine(Root, "global.config");
        internal GitEnvironmentPreferencesStore Store { get; }
        internal GitExecutionEnvironment Environment { get; }
        internal GitRepository Repository { get; }
        internal GitEnvironmentPreferences Initial { get; }
        private readonly GitEnvironmentOptions _options = GitEnvironment.Options;
        private readonly string? _askpass = GitRunner.AskpassFallbackPath;
        private readonly Dictionary<string, string?> _variables = [];
        private Fixture()
        {
            Directory.CreateDirectory(Home);
            foreach ((string key, string? value) in new Dictionary<string, string?>
            {
                ["HOME"] = Home, ["PATH"] = "/usr/bin:/bin", ["XDG_CONFIG_HOME"] = Path.Combine(Root, "xdg"),
                ["GIT_CONFIG_GLOBAL"] = Global, ["GIT_CONFIG_SYSTEM"] = "/dev/null", ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_COUNT"] = "0", ["GIT_CONFIG"] = null, ["GIT_EDITOR"] = null, ["VISUAL"] = null, ["EDITOR"] = null,
                ["GIT_DIR"] = null, ["GIT_COMMON_DIR"] = null, ["GIT_WORK_TREE"] = null, ["GIT_INDEX_FILE"] = null,
                ["GIT_OBJECT_DIRECTORY"] = null, ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = null, ["GIT_PREFIX"] = null,
                ["GIT_CEILING_DIRECTORIES"] = null
            })
            { _variables[key] = System.Environment.GetEnvironmentVariable(key); System.Environment.SetEnvironmentVariable(key, value); }
            GitRunner.AskpassFallbackPath = null;
            File.WriteAllText(Global, "");
            Store = new();
            string git = VersionProxy("published git Ω", "git version 2.53.0");
            Initial = new() { GitExecutable = git, CustomHomeDirectory = Home };
            Store.SaveChanges(Store.Load(), Initial);
            GitEnvironment.Configure(new() { Executable = git, HomeDirectory = Home });
            new PreferencesStore().Save(new() { Language = "English", HistoryLimit = 617 });
            Environment = GitExecutionEnvironment.Capture(new() { Executable = "/usr/bin/git" });
            Repository = new(Path.Combine(Root, "repository"), Environment.CreateRunner());
            Directory.CreateDirectory(Repository.Path);
        }
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture f = new();
            try
            {
                await f.Git("init", "--initial-branch=main");
                await f.Git("config", "--global", "user.name", "Global Ω");
                await f.Git("config", "--global", "user.email", "global@example.invalid");
                await f.Git("config", "--global", "core.editor", "configured editor --wait '$LITERAL'");
                await f.Git("config", "--local", "user.name", "Local Ω");
                await f.Git("config", "--local", "merge.guitool", "custom merge");
                await f.Git("config", "--local", "mergetool.custom merge.cmd", "literal merge command");
                await f.Git("config", "--local", "diff.guitool", "custom diff");
                await f.Git("config", "--local", "difftool.custom diff.cmd", "literal diff command");
                File.WriteAllText(Path.Combine(f.Repository.Path, "staged.txt"), "index witness Ω\n");
                await f.Git("add", "--", "staged.txt");
                File.WriteAllText(Path.Combine(f.Repository.Path, "untracked.txt"), "worktree witness Ω\r\n");
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        internal string VersionProxy(string name, string version)
            => Executable(Root, name, "if test \"$1\" = --version; then printf '%s\\n' " + Quote(version) + "; exit 0; fi\nexec /usr/bin/git \"$@\"\n");
        private async Task Git(params string[] arguments)
        {
            GitResult result = await Environment.CreateRunner().RunAsync(Repository.Path, arguments);
            Assert.That(result.Success, Is.True, result.StandardError);
        }
        internal string RepositoryData(bool includeGlobal = true)
        {
            IEnumerable<string> files = Directory.GetFiles(Repository.Path, "*", SearchOption.AllDirectories)
                .Concat(includeGlobal ? new[] { Global } : []);
            return JsonSerializer.Serialize(files.Order(StringComparer.Ordinal).ToDictionary(path => Path.GetRelativePath(Root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        }
        public void Dispose()
        {
            GitEnvironment.Configure(_options); GitRunner.AskpassFallbackPath = _askpass;
            foreach ((string key, string? value) in _variables) { System.Environment.SetEnvironmentVariable(key, value); }
            if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
        }
    }
}

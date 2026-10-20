using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.SshSettingsPageTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable, Platform("Linux")]
public sealed class SshSettingsSessionTests
{
    [Test]
    public void OlderProfilesRemainReadableAndUnknownFieldsSurviveSshPublicationAndNativeReset()
    {
        using Fixture f = new();
        byte[] previous = File.ReadAllBytes(f.Store.FilePath);
        Assert.That(f.Store.Load().Preferences.SshExecutable, Is.Empty);
        f.Store.SaveChanges(f.Store.Load(), f.Store.Load().Preferences);
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(previous));
        GitEnvironmentPreferencesLoadResult old = f.Store.Load();
        GitEnvironmentPreferencesLoadResult saved = f.Store.SaveChanges(old, old.Preferences with { SshExecutable = "/ssh λ '$()" });
        Assert.That(saved.Preferences.SshExecutable, Is.EqualTo("/ssh λ '$()"));
        Assert.Throws<InvalidOperationException>(() => f.Store.SaveChanges(old, old.Preferences with { SshExecutable = "stale" }));
        f.Store.SaveChanges(saved, saved.Preferences with { SshExecutable = "" });
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(f.Store.FilePath));
        Assert.That(json.RootElement.GetProperty("future").GetProperty("value").GetInt32(), Is.EqualTo(42));
        Assert.That(json.RootElement.GetProperty("SshExecutable").GetString(), Is.Empty);
    }

    [Test]
    [TestCase("null")]
    [TestCase("42")]
    public void InvalidSshProfileIsPreservedRatherThanSilentlyReset(string invalid)
    {
        using Fixture f = new(); string previous = File.ReadAllText(f.Store.FilePath);
        string json = previous[..previous.LastIndexOf('}')] + ",\"SshExecutable\":" + invalid + "}";
        File.WriteAllText(f.Store.FilePath, json); GitEnvironmentPreferencesLoadResult loaded = f.Store.Load();
        Assert.That(loaded.Error, Is.TypeOf<JsonException>()); Assert.That(loaded.Error!.Message, Is.EqualTo("SshExecutable must be a string."));
        Assert.Throws<InvalidDataException>(() => f.Store.SaveChanges(loaded, new()));
        Assert.That(File.ReadAllText(f.Store.FilePath), Is.EqualTo(json));
    }

    [AvaloniaTest]
    public void InstantChecklistPublicationAndReloadKeepPendingSshAndPathsPrivateUntilApply()
    {
        using Fixture f = new(); using GitEnvironmentSettingsSession session = new(f.Store, Catalog(), () => null);
        Window window = new() { Width = 850, Height = 900, Content = new StackPanel { Children = { session.Page, session.SshPage, session.Checklist } } }; window.Show(); Pump(window);
        try
        {
            Field<TextBox>(window, "GitPath").Text = "/pending/git";
            Field<RadioButton>(window, "Other").IsChecked = true;
            Field<TextBox>(window, "OtherSsh").Text = "/pending/ssh";
            Field<CheckBox>(window, "CheckAtStartup").IsChecked = false;
            Assert.That(f.Store.Load().Preferences, Is.EqualTo(new GitEnvironmentPreferences { CheckAtStartup = false }));
            Assert.That(GitEnvironment.Options.SshExecutable, Is.Empty);
            Assert.That(session.SshPage.Draft, Is.EqualTo("/pending/ssh"));
            GitEnvironmentPreferencesLoadResult external = f.Store.Load();
            f.Store.SaveChanges(external, external.Preferences with { ToolsDirectory = "/published/tools", SshExecutable = "/published/ssh" });
            Assert.That(session.TrySave(), Is.False, "A stale SSH draft must conflict with another window's publication.");
            session.Reload(); Assert.That(session.SshPage.Draft, Is.EqualTo("/pending/ssh"));
            Assert.That(session.Page.Draft.ToolsDirectory, Is.EqualTo("/published/tools"));
            Assert.That(session.TrySave(), Is.True);
            Assert.That(f.Store.Load().Preferences.SshExecutable, Is.EqualTo("/pending/ssh"));
            Assert.That(f.Store.Load().Preferences.GitExecutable, Is.EqualTo("/pending/git"));
            Assert.That(GitEnvironment.Options.SshExecutable, Is.EqualTo("/pending/ssh"));
            Assert.That(session.SshPage.IsDirty, Is.False);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task RealSettingsHostValidatesTheSshPageAppliesItAndCancelKeepsOnlyTheAppliedChoice()
    {
        using Fixture f = new(); using GitEnvironmentSettingsSession session = new(f.Store, Catalog(), () => null);
        Window owner = new() { Width = 1200, Height = 800 }; owner.Show();
        DesktopSettingsDialog dialog = new(new(), Catalog(), false, gitEnvironment: session, initialPage: "GitSsh");
        Task<bool> result = dialog.ShowDialog<bool>(owner);
        try
        {
            await Until(() => Field<Button>(dialog, "ApplyDesktopSettings").IsEnabled);
            Assert.That(dialog.Navigation.SelectedPage.Id, Is.EqualTo("GitSsh"));
            Field<RadioButton>(dialog, "Other").IsChecked = true;
            Click(dialog, "ApplyDesktopSettings"); Assert.That(dialog.IsVisible, Is.True);
            Assert.That(dialog.Navigation.SelectedPage.Id, Is.EqualTo("GitSsh")); Assert.That(File.ReadAllText(f.Store.FilePath), Does.Not.Contain("SshExecutable"));
            Field<TextBox>(dialog, "OtherSsh").Text = "/applied ssh λ"; Click(dialog, "ApplyDesktopSettings");
            await Until(() => f.Store.Load().Preferences.SshExecutable == "/applied ssh λ");
            Field<TextBox>(dialog, "OtherSsh").Text = "/cancelled"; Click(dialog, "CancelDesktopSettings");
            Assert.That(await result, Is.True); Assert.That(f.Store.Load().Preferences.SshExecutable, Is.EqualTo("/applied ssh λ"));
            Assert.That(GitEnvironment.Options.SshExecutable, Is.EqualTo("/applied ssh λ"));
        }
        finally { if (dialog.IsVisible) { dialog.Close(); } owner.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingSettingsCancelsPickerOwnershipButWaitsForItsActualRetirement()
    {
        using Fixture f = new(); using GitEnvironmentSettingsSession session = new(f.Store, Catalog(), () => null);
        TaskCompletionSource<string?> picker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default; session.SshPage.PickExecutable = token => { observed = token; return picker.Task; };
        Window owner = new() { Width = 1200, Height = 800 }; owner.Show();
        DesktopSettingsDialog dialog = new(new(), Catalog(), false, gitEnvironment: session, initialPage: "GitSsh"); Task<bool> result = dialog.ShowDialog<bool>(owner);
        try
        {
            await Until(() => Field<Button>(dialog, "ApplyDesktopSettings").IsEnabled);
            Field<RadioButton>(dialog, "Other").IsChecked = true; Click(dialog, "OtherSshBrowse");
            Assert.That(session.IsBusy, Is.True); Assert.That(session.OperationCompletion.IsCompleted, Is.False);
            dialog.Close(); Pump(dialog);
            Assert.That(observed.IsCancellationRequested, Is.True); Assert.That(result.IsCompleted, Is.False);
            picker.SetResult("late picker path"); await Until(() => result.IsCompleted);
            Assert.That(await result, Is.False); Assert.That(f.Store.Load().Preferences.SshExecutable, Is.Empty);
            Assert.That(session.OperationCompletion.IsCompleted, Is.True);
        }
        finally { picker.TrySetResult(null); if (dialog.IsVisible) { await session.OperationCompletion; dialog.Close(); } owner.Close(); }
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task GlobalOnlyDiagnosticsRetainRelativeSshPathsFromTheInvokingDirectory(bool applicationChoice)
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("Unix executable fixture"); return; }
        using Fixture f = new(); string root = Path.GetDirectoryName(Path.GetDirectoryName(f.Store.FilePath))!;
        string ssh = Path.Combine(root, "relative ssh"); File.WriteAllText(ssh, "#!/bin/sh\ntest \"$1\" = -V || exit 7\nprintf 'fixture version\\n' >&2\n");
        File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitEnvironmentOptions options = new() { Executable = "/usr/bin/git", SshExecutable = applicationChoice ? "./relative ssh" : "" };
        Dictionary<string, string?> values = new() { ["PATH"] = "/bin:/usr/bin", ["HOME"] = root, ["GIT_CONFIG_GLOBAL"] = "/dev/null", ["GIT_CONFIG_NOSYSTEM"] = "1" };
        if (!applicationChoice) { values["GIT_SSH"] = "./relative ssh"; }
        GitExecutionEnvironment inherited = GitExecutionEnvironment.Capture(options, values);
        using GitConfigurationContext context = GitConfigurationContext.Create(null, inherited, root);
        GitEnvironmentDiagnostic diagnostic = await GitEnvironmentDiagnostics.ProbeAsync(context.Environment, context.Repository.Path);
        Assert.That(diagnostic.Ssh.Success, Is.True, diagnostic.Ssh.Error);
        Assert.That(diagnostic.Ssh.Executable, Is.EqualTo(ssh));
        Assert.That(diagnostic.Ssh.Result!.StandardError, Is.EqualTo("fixture version\n"));
        Assert.That(inherited.GetVariable("GIT_SSH"), Is.EqualTo("./relative ssh"));
    }

    [Test]
    public void ConsoleRoundTripKeepsTheCapturedSshChoiceAfterANewPublication()
    {
        using Fixture f = new(); GitEnvironment.Configure(new() { SshExecutable = "captured ssh" });
        InteractiveFileConsoleEnvironment snapshot = InteractiveFileConsoleEnvironment.Capture(GitEnvironment.Current);
        string request = JsonSerializer.Serialize(snapshot); GitEnvironment.Configure(new() { SshExecutable = "new ssh" });
        GitExecutionEnvironment restored = JsonSerializer.Deserialize<InteractiveFileConsoleEnvironment>(request)!.Restore();
        Assert.That(restored.GetVariable("GIT_SSH"), Is.EqualTo("captured ssh"));
    }

    private static void Click(Window window, string name) { Pump(window); PushProcessTestDriver.Pointer(Field<Control>(window, name)); Pump(window); }
    private static async Task Until(Func<bool> condition)
    {
        for (int n = 0; n < 1000 && !condition(); n++) { await Task.Delay(5); Dispatcher.UIThread.RunJobs(); }
        Assert.That(condition(), Is.True);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-ssh-settings-" + Guid.NewGuid().ToString("N"));
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly GitEnvironmentOptions _options = GitEnvironment.Options;
        internal GitEnvironmentPreferencesStore Store { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root); GitEnvironment.Configure(new());
            Store = new(_root); Directory.CreateDirectory(Path.GetDirectoryName(Store.FilePath)!);
            File.WriteAllText(Store.FilePath, "{\"Version\":1,\"GitExecutable\":\"\",\"ToolsDirectory\":\"\",\"CustomHomeDirectory\":null,\"CheckAtStartup\":true,\"future\":{\"value\":42}}");
        }
        public void Dispose() { GitEnvironment.Configure(_options); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); Directory.Delete(_root, true); }
    }
}

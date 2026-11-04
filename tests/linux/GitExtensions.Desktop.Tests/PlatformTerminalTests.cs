using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[Category("CrossPlatform"), NonParallelizable]
public sealed class PlatformTerminalTests
{
    [AvaloniaTest]
    public void ActualSettingsPageOffersOnlyHostProvidersAndDoesNotReplaceAForeignSavedTerminal()
    {
        using Fixture fixture = new(); ConsoleStylePreferencesStore store = new(fixture.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        string raw = "{\"Terminal\":\"foreign-terminal\",\"Future\":true}"; File.WriteAllText(store.FilePath, raw);
        ConsoleStyleSettingsPage page = new(new(store), TranslationCatalog.LoadDefault()); Window host = new() { Content = page };
        try
        {
            host.Show(); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
            ComboBox terminal = host.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "cboConsoleEmulator");
            Assert.That((terminal.SelectedItem as ComboBoxItem)?.Tag, Is.EqualTo("foreign-terminal"));
            Assert.That((terminal.SelectedItem as ComboBoxItem)?.IsEnabled, Is.False);
            foreach (ComboBoxItem item in terminal.Items.OfType<ComboBoxItem>().Where(item => item.IsEnabled))
            { Assert.That(ConsoleStylePreferences.Terminals, Does.Contain(item.Tag)); }
            if (!OperatingSystem.IsLinux())
            { Assert.That(terminal.Items.OfType<ComboBoxItem>().Any(item => item.IsEnabled && (string?)item.Tag == ConsoleStylePreferences.Terminals[1]), Is.True); }
            Assert.That(File.ReadAllText(store.FilePath), Is.EqualTo(raw));
        }
        finally { host.Close(); }
    }

    [TestCase("linux", "gnome-terminal")]
    [TestCase("macos", "Terminal.app")]
    [TestCase("windows", "Windows console")]
    public void NativeTerminalCataloguesIncludeTheirProviderWithoutOfferingOtherPlatforms(string platform, string provider)
    {
        IReadOnlyList<string> names = ConsoleStylePreferences.TerminalsFor(platform);
        Assert.That(names[0], Is.EqualTo("Automatic")); Assert.That(names, Does.Contain(provider));
        foreach ((string other, string foreign) in new[] { ("linux", "gnome-terminal"), ("macos", "Terminal.app"), ("windows", "Windows console") })
        { if (other != platform) { Assert.That(names, Does.Not.Contain(foreign)); } }
    }

    [TestCase("cmd")]
    [TestCase("bin")]
    [TestCase("mingw64/bin")]
    public void WindowsShellBelongsToCapturedGitAndKeepsLiteralDirectoryAndEnvironment(string gitDirectory)
    {
        using Fixture fixture = new(); string root = fixture.Directory("Git ' λ"), unrelated = fixture.Directory("other");
        string git = fixture.File(Path.Combine(root, gitDirectory.Replace('/', Path.DirectorySeparatorChar), "git.exe"));
        string expected = fixture.File(Path.Combine(root, "git-bash.exe")); fixture.File(Path.Combine(unrelated, "git-bash.exe"));
        string repository = fixture.Directory("repository [x] ' λ");
        GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new() { Executable = git },
            new Dictionary<string, string?> { ["PATH"] = unrelated, ["HOME"] = fixture.Root, ["CAPTURED_SECRET"] = "literal ' $() λ" });
        ProcessStartInfo start = WindowsGitShell.CreateStartInfo(repository, environment);
        Assert.That(start.FileName, Is.EqualTo(expected)); Assert.That(start.WorkingDirectory, Is.EqualTo(repository));
        Assert.That(start.ArgumentList, Is.EqualTo(new[] { "--cd=" + repository })); Assert.That(start.UseShellExecute, Is.False);
        Assert.That(start.Environment["CAPTURED_SECRET"], Is.EqualTo("literal ' $() λ")); Assert.That(start.Environment["PATH"], Is.EqualTo(unrelated));
    }

    [Test]
    public void WindowsShellHonorsExplicitToolsAndPrefersRealUsrShellBeforeCompatibilityWrapper()
    {
        using Fixture fixture = new(); string root = fixture.Directory("Git"), tools = fixture.Directory("tools");
        string git = fixture.File(Path.Combine(root, "cmd", "git.exe"));
        string actual = fixture.File(Path.Combine(root, "usr", "bin", "bash.exe")); fixture.File(Path.Combine(root, "bin", "bash.exe"));
        GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new() { Executable = git }, new Dictionary<string, string?>());
        Assert.That(WindowsGitShell.CreateStartInfo(fixture.Root, environment).FileName, Is.EqualTo(actual));
        string explicitShell = fixture.File(Path.Combine(tools, "sh.exe")); fixture.File(Path.Combine(root, "git-bash.exe"));
        Assert.That(WindowsGitShell.CreateStartInfo(fixture.Root, environment, nativeConsole: true).FileName, Is.EqualTo(actual));
        environment = GitExecutionEnvironment.Capture(new() { Executable = git, ToolsDirectory = tools }, new Dictionary<string, string?>());
        ProcessStartInfo start = WindowsGitShell.CreateStartInfo(fixture.Root, environment);
        Assert.That(start.FileName, Is.EqualTo(explicitShell)); Assert.That(start.ArgumentList, Is.EqualTo(new[] { "--login", "-i" }));
        Assert.That(start.Environment["CHERE_INVOKING"], Is.EqualTo("1"));
    }

    [Test]
    public void WindowsBareGitUsesCapturedPathAndNeverChoosesUnrelatedBash()
    {
        using Fixture fixture = new(); string root = fixture.Directory("Git"), unrelated = fixture.Directory("WSL");
        fixture.File(Path.Combine(root, "cmd", "git.exe")); string expected = fixture.File(Path.Combine(root, "git-bash.exe"));
        fixture.File(Path.Combine(unrelated, "bash.exe"));
        GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new(), new Dictionary<string, string?> { ["PATH"] = Path.Combine(root, "cmd") + Path.PathSeparator + unrelated });
        Assert.That(WindowsGitShell.CreateStartInfo(fixture.Root, environment).FileName, Is.EqualTo(expected));
        File.Delete(expected);
        Assert.Throws<FileNotFoundException>(() => WindowsGitShell.CreateStartInfo(fixture.Root, environment));
    }

    [TestCase("gnome-terminal")]
    [TestCase("Terminal.app")]
    [TestCase("Windows console")]
    public void AProfileFromAnotherPlatformIsNeverRewrittenByAnUnrelatedStyleEdit(string terminal)
    {
        using Fixture fixture = new(); ConsoleStylePreferencesStore store = new(fixture.Root); Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, JsonSerializer.Serialize(new { Terminal = terminal, Future = new { retain = "literal λ" } }));
        ConsoleStylePreferences before = store.Load(); store.SaveChanges(before, before with { Style = "Dark" });
        Assert.That(store.Load().Terminal, Is.EqualTo(terminal));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Assert.That(json.RootElement.GetProperty("Future").GetProperty("retain").GetString(), Is.EqualTo("literal λ"));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "platform-terminal-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => System.IO.Directory.CreateDirectory(Root);
        internal string Directory(string name) { string path = Path.Combine(Root, name); System.IO.Directory.CreateDirectory(path); return path; }
        internal string File(string path) { System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, "lookup only"); return path; }
        public void Dispose() => System.IO.Directory.Delete(Root, true);
    }
}

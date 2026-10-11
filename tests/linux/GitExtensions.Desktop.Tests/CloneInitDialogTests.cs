using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class CloneInitDialogTests
{
    [AvaloniaTest]
    public async Task BrowserFileMenuOpensClassicCreateAndCloneWindows()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        MenuItem file = fixture.Window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "File");
        file.Items.OfType<MenuItem>().Single(item => item.Header as string == "Create new repository…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => fixture.Window.OwnedWindows.OfType<InitDialog>().Any());
        InitDialog init = fixture.Window.OwnedWindows.OfType<InitDialog>().Single();
        Assert.That(Field<TextBox>(init, "InitDirectory").Text, Is.EqualTo(fixture.Repository));
        Capture(init, "classic-create-light.png");
        init.RequestedThemeVariant = ThemeVariant.Dark;
        Capture(init, "classic-create-dark.png");
        init.Close();
        await Task.Delay(20);
        file.Items.OfType<MenuItem>().Single(item => item.Header as string == "Clone repository…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => fixture.Window.OwnedWindows.OfType<CloneDialog>().Any());
        CloneDialog clone = fixture.Window.OwnedWindows.OfType<CloneDialog>().Single();
        await clone.InitializeAsync();
        Capture(clone, "classic-clone-light.png");
        clone.RequestedThemeVariant = ThemeVariant.Dark;
        Capture(clone, "classic-clone-dark.png");
        clone.Close();
        Assert.That(fixture.Window.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task CreatePersonalAndSharedCentralRepositoriesHonorsGitDefaultBranchAndLiteralDirectory()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        string root = Path.GetDirectoryName(fixture.Repository)!;
        string config = Path.Combine(root, "init-default.gitconfig");
        await File.WriteAllTextAsync(config, "[init]\n\tdefaultBranch = preferred\n");
        string? previous = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", config);
        try
        {
            foreach (bool central in new[] { false, true })
            {
                string path = Path.Combine(root, central ? "central [shared]\nλ.git" : "personal 'literal' λ");
                InitDialog init = new(path);
                init.Show();
                Field<RadioButton>(init, central ? "InitCentral" : "InitPersonal").IsChecked = true;
                Click(init, "CreateRepository");
                await Until(() => !init.IsBusy);
                Assert.That(init.LastError, Is.Null, init.LastException?.ToString());
                Assert.That(init.IsVisible, Is.False);
                GitRepository repository = init.CreatedRepository!;
                Assert.That(repository.Path, Is.EqualTo(path));
                Assert.That(repository.IsBare, Is.EqualTo(central));
                Assert.That((await repository.GetStatusAsync()).Branch, Is.EqualTo("preferred"));
                if (central)
                {
                    Assert.That((await repository.GetConfigAsync("^core\\.sharedRepository$")).Single().Value, Is.EqualTo("2"));
                    Assert.That((await repository.GetConfigAsync("^receive\\.denyNonFastforwards$")).Single().Value, Is.EqualTo("true"));
                }
            }
        }
        finally { Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previous); }
    }

    [AvaloniaTest]
    public async Task ShallowCloneKeepsOtherBranchesAndSavedSubmodulePreference()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        await Git(fixture, "branch", "other");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "second\n");
        await Git(fixture, "commit", "-am", "Second");
        CloneDialog dialog = NewClone(fixture, "shallow λ", new Uri(fixture.Repository).AbsoluteUri);
        Field<CheckBox>(dialog, "CloneFullHistory").IsChecked = false;
        Field<CheckBox>(dialog, "CloneSubmodules").IsChecked = false;
        Field<TextBox>(dialog, "CloneBranch").Text = "main";
        Click(dialog, "RefreshCloneBranches");
        await Until(() => !dialog.IsBusy);
        Assert.That(Field<ComboBox>(dialog, "CloneBranches").ItemsSource!.Cast<string>(), Does.Contain("other"));
        Assert.That(Field<TextBox>(dialog, "CloneBranch").Text, Is.EqualTo("main"));
        Click(dialog, "CloneRepository");
        await Until(() => !dialog.IsBusy);
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
        GitRepository cloned = dialog.CreatedRepository!;
        Assert.That((await cloned.RunAsync(["rev-parse", "--is-shallow-repository"])).StandardOutput.Trim(), Is.EqualTo("true"));
        Assert.That((await cloned.GetReferencesAsync()).Select(reference => reference.Name), Does.Contain("refs/remotes/origin/other"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(cloned.Path, "file.txt")), Is.EqualTo("second\n"));
        CloneDialog reopened = new();
        reopened.Show();
        try
        {
            Assert.That(Field<TextBox>(reopened, "CloneDestination").Text, Is.EqualTo(Path.GetDirectoryName(fixture.Repository)));
            Assert.That(Field<CheckBox>(reopened, "CloneSubmodules").IsChecked, Is.False);
        }
        finally { reopened.Close(); }
    }

    [AvaloniaTest]
    public async Task CloneSupportsSelectedBranchNoCheckoutAndBareRepositories()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        await Git(fixture, "switch", "-c", "topic");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "topic\n");
        await Git(fixture, "commit", "-am", "Topic");
        await Git(fixture, "switch", "main");
        foreach (string mode in new[] { "topic", "none", "bare" })
        {
            CloneDialog dialog = NewClone(fixture, mode, fixture.Repository);
            if (mode == "topic") { Field<TextBox>(dialog, "CloneBranch").Text = "topic"; }
            if (mode == "none") { Field<ComboBox>(dialog, "CloneBranches").SelectedIndex = 1; }
            if (mode == "bare") { Field<RadioButton>(dialog, "CloneBare").IsChecked = true; }
            if (mode != "topic") { Assert.That(Field<CheckBox>(dialog, "CloneSubmodules").IsEnabled, Is.False); }
            Click(dialog, "CloneRepository");
            await Until(() => !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.CreatedRepository!.IsBare, Is.EqualTo(mode == "bare"));
            string file = Path.Combine(dialog.CreatedRepository.Path, "file.txt");
            if (mode == "topic") { Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("topic\n")); }
            else { Assert.That(File.Exists(file), Is.False); }
        }
    }

    [AvaloniaTest]
    public async Task InvalidCreateAndCloneKeepTheDialogOpenAndPreserveExistingFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        InitDialog init = new(Path.Combine(fixture.Repository, "file.txt"));
        init.Show();
        try
        {
            Click(init, "CreateRepository");
            await Until(() => !init.IsBusy);
            Assert.That(init.LastError, Does.Contain("file"));
            Assert.That(init.CreatedRepository, Is.Null);
            Assert.That(init.IsVisible, Is.True);
        }
        finally { init.Close(); }
        CloneDialog clone = NewClone(fixture, "..", fixture.Repository);
        try
        {
            Click(clone, "CloneRepository");
            await Until(() => !clone.IsBusy);
            Assert.That(clone.LastError, Does.Contain("inside"));
            Field<TextBox>(clone, "CloneSubdirectory").Text = Path.GetFileName(fixture.Repository);
            Click(clone, "CloneRepository");
            await Until(() => !clone.IsBusy);
            Assert.That(clone.LastError, Does.Contain("not an empty directory"));
            Assert.That(clone.CreatedRepository, Is.Null);
            Assert.That(clone.IsVisible, Is.True);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "file.txt")), Is.EqualTo("base\n"));
        }
        finally { clone.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingCloneCancelsGitAndItsSshChildBeforeTheWindowCloses()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("This test exercises Linux process cancellation."); }
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        string root = Path.GetDirectoryName(fixture.Repository)!;
        string script = Path.Combine(root, "slow-ssh.py");
        string pidFile = Path.Combine(root, "ssh.pid");
        await File.WriteAllTextAsync(script, "#!/usr/bin/python3\nimport os,time\nwith open(" + System.Text.Json.JsonSerializer.Serialize(pidFile) + ", 'w') as output: output.write(str(os.getpid()))\ntime.sleep(60)\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string? previousSsh = Environment.GetEnvironmentVariable("GIT_SSH");
        string? previousCommand = Environment.GetEnvironmentVariable("GIT_SSH_COMMAND");
        string? previousVariant = Environment.GetEnvironmentVariable("GIT_SSH_VARIANT");
        Environment.SetEnvironmentVariable("GIT_SSH", script);
        Environment.SetEnvironmentVariable("GIT_SSH_COMMAND", null);
        Environment.SetEnvironmentVariable("GIT_SSH_VARIANT", "ssh");
        CloneDialog clone = NewClone(fixture, "cancelled", "ssh://example.invalid/repo.git");
        try
        {
            Click(clone, "CloneRepository");
            await Until(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0);
            int pid = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
            Assert.That(clone.IsBusy, Is.True);
            clone.Close();
            await Until(() => !clone.IsBusy && !clone.IsVisible);
            Assert.That(clone.CreatedRepository, Is.Null);
            Assert.That(clone.LastError, Is.EqualTo("Operation cancelled"));
            await Until(() => Terminated(pid));
        }
        finally
        {
            clone.Close();
            Environment.SetEnvironmentVariable("GIT_SSH", previousSsh);
            Environment.SetEnvironmentVariable("GIT_SSH_COMMAND", previousCommand);
            Environment.SetEnvironmentVariable("GIT_SSH_VARIANT", previousVariant);
        }
    }

    private static bool Terminated(int pid)
    {
        string status = $"/proc/{pid}/status";
        try { return !File.Exists(status) || File.ReadLines(status).Any(line => line.StartsWith("State:", StringComparison.Ordinal) && line.Contains('Z')); }
        catch (IOException) when (!File.Exists(status)) { return true; }
    }

    [AvaloniaTest]
    public async Task SuggestedFolderDecodesUrlsPreservesLocalColonsAndShowsTranslatedReadableWarnings()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        string root = Path.GetDirectoryName(fixture.Repository)!;
        foreach ((string source, string expected) in new[]
        {
            ("https://example.invalid/mon%20d%C3%A9p%C3%B4t.git", "mon dépôt"),
            (Path.Combine(root, "a:b.git"), "a:b"),
            ("git@example.invalid:mon%20d%C3%A9p%C3%B4t.git", "mon dépôt"),
            (new Uri(Path.Combine(root, "local dépôt.git")).AbsoluteUri, "local dépôt")
        })
        {
            CloneDialog dialog = new(root, source);
            dialog.Show();
            try { Assert.That(Field<TextBox>(dialog, "CloneSubdirectory").Text, Is.EqualTo(expected)); }
            finally { dialog.Close(); }
        }
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        CloneDialog french = new(root);
        french.Show();
        try
        {
            TextBlock location = Field<TextBlock>(french, "CloneLocation");
            Assert.That(location.Text, Does.StartWith("Le dépôt sera cloné").And.Contain("Dossier déjà existant"));
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                french.RequestedThemeVariant = theme;
                string path = Capture(french, "classic-clone-warning-" + theme + ".png");
                using SkiaSharp.SKBitmap bitmap = SkiaSharp.SKBitmap.Decode(path);
                SkiaSharp.SKColor background = bitmap.GetPixel(0, 0);
                Color foreground = ((ISolidColorBrush)location.Foreground!).Color;
                double a = Luminance(foreground.R, foreground.G, foreground.B), b = Luminance(background.Red, background.Green, background.Blue);
                Assert.That((Math.Max(a, b) + .05) / (Math.Min(a, b) + .05), Is.GreaterThanOrEqualTo(4.5), "Original warning contrast threshold against rendered " + theme + " background");
            }
        }
        finally { french.Close(); System.Globalization.CultureInfo.CurrentUICulture = previous; }

        static double Luminance(byte r, byte g, byte b)
        {
            static double Linear(byte channel) => channel / 255d <= .04045 ? channel / 255d / 12.92 : Math.Pow((channel / 255d + .055) / 1.055, 2.4);
            return .2126 * Linear(r) + .7152 * Linear(g) + .0722 * Linear(b);
        }
    }

    private static CloneDialog NewClone(Fixture fixture, string name, string source)
    {
        CloneDialog dialog = new(Path.GetDirectoryName(fixture.Repository)!, source);
        dialog.Show();
        Field<TextBox>(dialog, "CloneSubdirectory").Text = name;
        return dialog;
    }
    private static async Task Git(Fixture fixture, params string[] arguments)
    {
        GitResult result = await fixture.Git.RunAsync(fixture.Repository, arguments);
        Assert.That(result.Success, Is.True, result.StandardError);
    }
    private static TControl Field<TControl>(Window window, string name) where TControl : Control
        => window.GetVisualDescendants().OfType<TControl>().Single(control => control.Name == name);
    private static void Click(Window window, string name)
    {
        Button button = Field<Button>(window, name);
        Assert.That(button.IsEnabled && button.IsEffectivelyVisible, Is.True);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static async Task Until(Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        Assert.That(done(), Is.True, "Native startup dialog did not finish.");
    }
    private static string Capture(Window dialog, string filename)
    {
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using Avalonia.Media.Imaging.WriteableBitmap? frame = dialog.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, filename);
        frame!.Save(path);
        TestContext.AddTestAttachment(path);
        return path;
    }
}

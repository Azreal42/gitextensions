using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class ProxyGourceDialogTests
{
    [AvaloniaTest]
    public async Task ProxyShowsOriginalScopeRowsAndSetsUnsetsRealGitWithoutSavingPassword()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        const string secret = "secret:$(touch SHOULD_NOT_EXIST)@été";
        bool requested = false;
        ProxySwitcherDialog dialog = new(fixture.Repository, new Dictionary<string, string?>
        { ["host"] = "proxy.example.invalid", ["username"] = "alice", ["password"] = secret }, requestSettings: () => requested = true);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(Field<CheckBox>(dialog, "ApplyGlobally_CheckBox").IsChecked, Is.True);
            Click(dialog, "SetProxy_Button"); await Idle(dialog);
            Assert.That(dialog.LastError, Is.Null);
            Assert.That(dialog.HasRepositoryChanges, Is.True);
            Assert.That((await fixture.Repository.GetConfigAsync("^http\\.proxy$", GitConfigScope.Global)).Single().Value,
                Is.EqualTo("alice:" + secret + "@proxy.example.invalid:8080"));
            Assert.That(Field<TextBox>(dialog, "LocalHttpProxy_TextBox").Text, Is.EqualTo("alice:****@proxy.example.invalid:8080"));
            Assert.That(Field<TextBox>(dialog, "GlobalHttpProxy_TextBox").IsReadOnly, Is.True);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme;
                Capture(dialog, $"classic-proxy-{theme}.png");
                Assert.That(Field<TextBox>(dialog, "LocalHttpProxy_TextBox").Bounds.Height, Is.EqualTo(24));
                Assert.That(Field<Button>(dialog, "SetProxy_Button").Bounds.Width, Is.EqualTo(117));
            }
            Click(dialog, "ProxySettings");
            Assert.That(requested && dialog.SettingsRequested, Is.True); Assert.That(dialog.IsVisible, Is.False);
            await ProxySettingsTestDriver.WithSettings(fixture.Repository, async settings =>
            {
                Assert.That(ProxySettingsTestDriver.Field<TextBox>(settings, "Password").PasswordChar, Is.EqualTo('●'));
                ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<RadioButton>(settings, "ProxyLocalRB"));
                ProxySettingsTestDriver.Edit(settings, "HttpProxy", "local.example.invalid");
                ProxySettingsTestDriver.Edit(settings, "HttpProxyPort", "<empty string>");
                ProxySettingsTestDriver.Edit(settings, "Username", "<empty string>");
                ProxySettingsTestDriver.Edit(settings, "Password", "<empty string>");
                ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<Button>(settings, "ApplyDesktopSettings")); await ProxySettingsTestDriver.Saved(settings);
            });
            ProxySwitcherSettingsSession defaults = new(fixture.Repository); await defaults.InitializeAsync(default);
            dialog = new(fixture.Repository, defaults.RuntimeValues()); dialog.Show(); await dialog.InitializeAsync();
            Field<CheckBox>(dialog, "ApplyGlobally_CheckBox").IsChecked = false;
            Click(dialog, "SetProxy_Button"); await Idle(dialog);
            Assert.That(Field<TextBox>(dialog, "LocalHttpProxy_TextBox").Text, Is.EqualTo("local.example.invalid"));
            Assert.That(Field<CheckBox>(dialog, "ApplyGlobally_CheckBox").IsChecked, Is.False);
            Click(dialog, "UnsetProxy_Button"); await Idle(dialog);
            Assert.That(Field<TextBox>(dialog, "LocalHttpProxy_TextBox").Text, Is.EqualTo("alice:****@proxy.example.invalid:8080"));
            Click(dialog, "UnsetProxy_Button"); await Idle(dialog);
            Assert.That((await fixture.Repository.GetConfigAsync("^http\\.proxy$", GitConfigScope.Global)), Is.Empty);
            Assert.That(File.Exists(Path.Combine(fixture.Repository.Path, "SHOULD_NOT_EXIST")), Is.False);
            PreferencesStore store = new();
            store.Save(new() { PluginSettings = new() { [new ProxySwitcherPlugin().Descriptor.Id.ToString()] = new(dialog.CurrentValues) } });
            Assert.That(File.ReadAllText(store.FilePath), Does.Not.Contain(secret).And.Not.Contain("password"));
            dialog.Close();
            Assert.That(dialog.CurrentOptions.Password, Is.Empty);
        }
        finally { if (dialog.IsVisible) dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ProxySettingsValidationAndCancelLeaveGitAndPreviousSettingsUntouched()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        bool requested = false; ProxySwitcherDialog dialog = new(fixture.Repository, requestSettings: () => requested = true);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(Field<Button>(dialog, "SetProxy_Button").IsEnabled, Is.False);
            ProxySettingsTestDriver.ProfileBytes[] before = await ProxySettingsTestDriver.Profiles(fixture.Repository);
            Click(dialog, "ProxySettings"); Assert.That(requested && dialog.SettingsRequested, Is.True);
            await ProxySettingsTestDriver.WithSettings(fixture.Repository, async settings =>
            {
                ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<RadioButton>(settings, "ProxyGlobalRB"));
                ProxySettingsTestDriver.Edit(settings, "HttpProxy", "configured.invalid");
                ProxySettingsTestDriver.Edit(settings, "HttpProxyPort", "65536");
                ProxySettingsTestDriver.Edit(settings, "Password", "discarded password");
                TextBox password = ProxySettingsTestDriver.Field<TextBox>(settings, "Password");
                ProxySettingsTestDriver.Pointer(ProxySettingsTestDriver.Field<Button>(settings, "CancelDesktopSettings"));
                await ProxySettingsTestDriver.Until(settings, () => !settings.IsVisible); Assert.That(password.Text, Is.Null.Or.Empty);
            });
            foreach (ProxySettingsTestDriver.ProfileBytes file in before) { file.AssertUnchanged(); }
            Assert.That(dialog.CurrentOptions.Host, Is.Empty); Assert.That(dialog.HasRepositoryChanges, Is.False);
            // Windows' four StringSettings do not validate ports on Apply. The existing Linux runtime still validates Set.
            ProxySwitcherDialog invalid = new(fixture.Repository, new Dictionary<string, string?> { ["host"] = "configured.invalid", ["port"] = "65536", ["password"] = "discarded password" });
            try
            {
                invalid.Show(); await invalid.InitializeAsync(); Click(invalid, "SetProxy_Button"); await Idle(invalid);
                Assert.That(invalid.IsVisible, Is.True); Assert.That(invalid.LastError, Does.Contain("65535"));
                Assert.That(await fixture.Repository.GetConfigAsync("^http\\.proxy$"), Is.Empty);
            }
            finally { invalid.Close(); }
            Assert.That(await fixture.Repository.GetConfigAsync("^http\\.proxy$"), Is.Empty);
        }
        finally { if (dialog.IsVisible) { dialog.Close(); } }
    }

    [AvaloniaTest]
    public async Task GourceOriginalThreeRowsBrowseLinksAndSettingsRenderInBothThemes()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        GourceDialog dialog = new(fixture.Repository, avatarProvider: new BlockingAvatarProvider());
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Null);
            Assert.That(Field<TextBox>(dialog, "WorkingDir").Text, Is.EqualTo(fixture.Repository.Path));
            Assert.That(Field<TextBox>(dialog, "Arguments").Text, Is.EqualTo(new GourceOptions().Arguments));
            Assert.That(Field<Button>(dialog, "GourceBrowse").Content, Is.EqualTo("Browse"));
            Assert.That(Field<Button>(dialog, "WorkingDirBrowse").Content, Is.EqualTo("Browse"));
            Assert.That(((TextBlock)Field<Button>(dialog, "GourceProject").Content!).Text, Is.EqualTo("Gource project"));
            Assert.That(((TextBlock)Field<Button>(dialog, "GourceCommandLine").Content!).Text, Is.EqualTo("Gource command line"));
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme;
                Capture(dialog, $"classic-gource-{theme}.png");
                Assert.That(Field<TextBox>(dialog, "Arguments").Bounds.Height, Is.EqualTo(24));
                Assert.That(Field<Button>(dialog, "StartGource").Bounds.Width, Is.EqualTo(75));
                Assert.That(((Avalonia.Media.SolidColorBrush)Field<Button>(dialog, "GourceProject").Foreground!).Color,
                    Is.EqualTo(Avalonia.Media.Color.Parse(theme == ThemeVariant.Dark ? "#9393ff" : "#0000ff")),
                    "Original ThemeFix adapts blue links to 4.5 contrast; selection accents are not link colors.");
            }
            Field<TextBox>(dialog, "GourcePath").Text = "/literal path/gource";
            Field<TextBox>(dialog, "Arguments").Text = "--title 'été'";
            dialog.Close();
            PreferencesStore store = new();
            store.Save(new() { PluginSettings = new() { [new GourcePlugin().Descriptor.Id.ToString()] = new(dialog.CurrentValues) } });
            GourceDialog restored = new(fixture.Repository, store.Load().PluginSettings[new GourcePlugin().Descriptor.Id.ToString()]);
            Assert.That(restored.CurrentOptions.Executable, Is.EqualTo("/literal path/gource"));
            Assert.That(restored.CurrentOptions.Arguments, Is.EqualTo("--title 'été'"));
            restored.Close();
        }
        finally { if (dialog.IsVisible) dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task GourceStartsActualLocalProcessWithNativeDownloadedPngAndLiteralArguments()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        using FakeGource executable = new(fixture.Root);
        using AvatarHttpHandler handler = new(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(AvatarHttpHandler.Image(40, 60, jpeg: true)) });
        using HttpClient client = new(handler);
        GourceAvatarProvider provider = new(client, Path.Combine(fixture.Root, "avatar cache"));
        const string literal = "$(touch SENTINEL);`echo x`|cat";
        GourceDialog dialog = new(fixture.Repository, new Dictionary<string, string?>
        { ["executable"] = executable.Path, ["arguments"] = "--title 'Repository été' --literal '" + literal + "' --user-image-dir '$(AVATARS)'" }, provider);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Click(dialog, "StartGource"); await Until(() => !dialog.IsVisible);
            Assert.That(dialog.LastError, Is.Null);
            await Until(() => File.Exists(executable.Log));
            using JsonDocument invocation = JsonDocument.Parse(File.ReadAllText(executable.Log));
            string avatars = dialog.LastLaunch!.AvatarDirectory!;
            Assert.That(invocation.RootElement.GetProperty("cwd").GetString(), Is.EqualTo(fixture.Repository.Path));
            Assert.That(invocation.RootElement.GetProperty("args").EnumerateArray().Select(value => value.GetString()),
                Is.EqualTo(new[] { "--title", "Repository été", "--literal", literal, "--user-image-dir", avatars }));
            Assert.That(handler.Requests.Count, Is.EqualTo(1));
            using Avalonia.Media.Imaging.Bitmap png = new(Path.Combine(avatars, "Avatar Tester.png"));
            Assert.That(png.PixelSize, Is.EqualTo(new Avalonia.PixelSize(90, 90)));
            Assert.That(File.Exists(Path.Combine(fixture.Repository.Path, "SENTINEL")), Is.False);
            executable.Release(); await Until(() => !Directory.Exists(avatars));
        }
        finally { if (dialog.IsVisible) dialog.Close(); executable.Release(); }
    }

    [AvaloniaTest]
    public async Task GourceExportsAuthorsConcurrentlyWithTheOriginalTenDownloadLimit()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        for (int index = 0; index < 11; index++)
            await fixture.Repository.RunAsync(["-c", "user.name=Avatar " + index, "-c", "user.email=avatar" + index + "@example.invalid", "commit", "--allow-empty", "-m", "Author " + index]);
        using FakeGource executable = new(fixture.Root);
        TaskCompletionSource tenEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, maximum = 0, started = 0;
        byte[] image = AvatarHttpHandler.Image(20, 20);
        using AvatarHttpHandler handler = new(async (_, token) =>
        {
            int count = Interlocked.Increment(ref active);
            int previous;
            while (count > (previous = Volatile.Read(ref maximum)) && Interlocked.CompareExchange(ref maximum, count, previous) != previous) { }
            if (Interlocked.Increment(ref started) == 10) tenEntered.TrySetResult();
            try { await release.Task.WaitAsync(token); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(image) }; }
            finally { Interlocked.Decrement(ref active); }
        });
        using HttpClient client = new(handler);
        GourceDialog dialog = new(fixture.Repository, new Dictionary<string, string?> { ["executable"] = executable.Path }, new GourceAvatarProvider(client, Path.Combine(fixture.Root, "avatars")));
        dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Click(dialog, "StartGource");
            await tenEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(Volatile.Read(ref maximum), Is.EqualTo(10));
            release.TrySetResult(); await Until(() => !dialog.IsVisible);
            Assert.That(dialog.LastError, Is.Null);
            Assert.That(handler.Requests.Count, Is.EqualTo(12));
            Assert.That(Volatile.Read(ref maximum), Is.EqualTo(10));
            Assert.That(Directory.GetFiles(dialog.LastLaunch!.AvatarDirectory!), Has.Length.EqualTo(12));
            executable.Release(); await Until(() => !Directory.Exists(dialog.LastLaunch.AvatarDirectory));
        }
        finally { release.TrySetResult(); if (dialog.IsVisible) dialog.Close(); executable.Release(); await Idle(dialog); }
    }

    [AvaloniaTest]
    public async Task MainGourceMenuOpensNativeDialogLaunchesAndRestoresItsXdgPreferences()
    {
        using var fixture = await MainWindowClassicTests.RepositoryFixture.CreateAsync("anchor\n", "anchor\n");
        using FakeGource executable = new(Path.GetDirectoryName(fixture.Repository)!);
        GourceDialog dialog = await OpenAsync();
        const string arguments = "--title 'From main menu' --literal '$(touch MAIN_SENTINEL)'";
        try
        {
            Field<TextBox>(dialog, "GourcePath").Text = executable.Path;
            Field<TextBox>(dialog, "Arguments").Text = arguments; // No avatar placeholder: no external HTTP request.
            Click(dialog, "StartGource");
            await Until(() => !dialog.IsVisible && File.Exists(executable.Log));
            Assert.That(dialog.LastError, Is.Null);
            Assert.That(dialog.LastLaunch!.AvatarDirectory, Is.Null);
            using JsonDocument invocation = JsonDocument.Parse(File.ReadAllText(executable.Log));
            Assert.That(invocation.RootElement.GetProperty("args").EnumerateArray().Select(value => value.GetString()),
                Is.EqualTo(new[] { "--title", "From main menu", "--literal", "$(touch MAIN_SENTINEL)" }));
            await Until(() => !fixture.Window.IsBusy);
            GitXmlSettingsSnapshot saved = await GitXmlSettingsStore.LoadAsync(DetailedRepositorySettings.GlobalPath);
            PluginScopedSettingsDefinition definition = SimplePluginSettingsCatalog.Gource.Definition;
            Assert.That(saved.Values[definition.Key(definition.Find("_gourcePath"))], Is.EqualTo(executable.Path));
            Assert.That(saved.Values[definition.Key(definition.Find("_gourceArguments"))], Is.EqualTo(arguments));
            Assert.That(File.Exists(Path.Combine(fixture.Repository, "MAIN_SENTINEL")), Is.False);
            executable.Release();
            GourceDialog restored = await OpenAsync();
            Assert.That(restored.CurrentOptions.Executable, Is.EqualTo(executable.Path));
            Assert.That(restored.CurrentOptions.Arguments, Is.EqualTo(arguments));
            restored.Close(); await Until(() => !fixture.Window.OwnedWindows.Contains(restored) && !fixture.Window.IsBusy);
            Assert.That(fixture.Window.LastException, Is.Null, fixture.Window.LastException?.ToString());
        }
        finally { if (dialog.IsVisible) dialog.Close(); executable.Release(); }

        async Task<GourceDialog> OpenAsync()
        {
            MenuItem plugins = fixture.Window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Plugins");
            MenuItem item = plugins.Items.OfType<MenuItem>().Single(item => item.Header as string == new GourcePlugin().Descriptor.Name);
            Assert.That(item.IsEnabled, Is.True); Assert.That(item.Icon, Is.Not.Null);
            plugins.IsSubMenuOpen = true; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); plugins.IsSubMenuOpen = false;
            await Until(() => fixture.Window.OwnedWindows.OfType<GourceDialog>().Any(candidate => !candidate.IsBusy));
            return fixture.Window.OwnedWindows.OfType<GourceDialog>().Single();
        }
    }

    [AvaloniaTest]
    public async Task MissingGourceKeepsDialogOpenAndDoesNotTryDownloadingWindowsBinaries()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        BlockingAvatarProvider provider = new();
        GourceDialog dialog = new(fixture.Repository, new Dictionary<string, string?> { ["executable"] = Path.Combine(fixture.Root, "not installed") }, provider);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Click(dialog, "StartGource");
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(Field<TextBlock>(dialog, "OperationStatus").Text, Does.Contain("Cannot find Gource"));
            Assert.That(provider.Entered.Task.IsCompleted, Is.False);
            Assert.That(dialog.LastLaunch, Is.Null);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingGourceCancelsAvatarExportBeforeStartingTheDetachedProcess()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        using FakeGource executable = new(fixture.Root);
        BlockingAvatarProvider provider = new();
        string root = Path.Combine(Path.GetTempPath(), "GitExtensions-Gource");
        string[] before = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        GourceDialog dialog = new(fixture.Repository, new Dictionary<string, string?> { ["executable"] = executable.Path }, provider);
        dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Click(dialog, "StartGource");
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(dialog.IsBusy, Is.True);
            dialog.Close(); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(provider.Cancelled, Is.True);
            Assert.That(File.Exists(executable.Log), Is.False);
            Assert.That(dialog.LastLaunch, Is.Null);
            Assert.That(Directory.GetDirectories(root), Is.EquivalentTo(before));
        }
        finally { if (dialog.IsVisible) dialog.Close(); }
    }

    private sealed class BlockingAvatarProvider : IGourceAvatarProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public async Task<byte[]?> GetAvatarPngAsync(string email, string authorName, int imageSize, CancellationToken token)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return null;
        }
    }

    internal static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    internal static void Click(Window window, string name) => Field<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal static async Task Until(Func<bool> predicate)
    {
        for (int attempt = 0; attempt < 500 && !predicate(); attempt++) await Task.Delay(10);
        Assert.That(predicate(), Is.True, "Native operation did not reach the expected state.");
    }
    private static Task Idle(RepositoryDialog dialog) => Until(() => !dialog.IsBusy);
    private static void Capture(Window dialog, string name)
    {
        dialog.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = dialog.CaptureRenderedFrame() ?? throw new AssertionException("Missing actual rendered frame.");
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts/linux/screenshots"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name); frame.Save(path); TestContext.AddTestAttachment(path);
        if (name.StartsWith("classic-gource", StringComparison.Ordinal))
        {
            using SkiaSharp.SKBitmap pixels = SkiaSharp.SKBitmap.Decode(path);
            SkiaSharp.SKColor expected = name.Contains("Dark", StringComparison.Ordinal) ? new(147, 147, 255) : SkiaSharp.SKColors.Blue;
            Assert.That(pixels.Pixels.Count(pixel => pixel == expected), Is.GreaterThan(20), "Original adapted link glyph color must occur in the real frame.");
        }
    }

    private sealed class FakeGource : IDisposable
    {
        public string Path { get; }
        public string Log { get; }
        private readonly string _release;
        public FakeGource(string root)
        {
            Path = System.IO.Path.Combine(root, "gource été [*] $(literal)");
            Log = System.IO.Path.Combine(root, "gource invocation.json"); _release = System.IO.Path.Combine(root, "release gource");
            File.WriteAllText(Path, "#!/usr/bin/python3\nimport os,sys,json,time,pathlib\n" +
                $"pathlib.Path({JsonSerializer.Serialize(Log)}).write_text(json.dumps({{'cwd':os.getcwd(),'args':sys.argv[1:],'pid':os.getpid()}}))\n" +
                $"while not pathlib.Path({JsonSerializer.Serialize(_release)}).exists(): time.sleep(.01)\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Release() { File.WriteAllText(_release, "released"); }
        public void Dispose()
        {
            Release();
            if (File.Exists(Log))
            {
                using JsonDocument log = JsonDocument.Parse(File.ReadAllText(Log));
                try { using Process process = Process.GetProcessById(log.RootElement.GetProperty("pid").GetInt32()); process.WaitForExit(5000); }
                catch (ArgumentException) { }
            }
        }
    }
}

internal sealed class ProxyGourceFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-proxy-gource-" + Guid.NewGuid().ToString("N"));
    public GitRepository Repository { get; private set; } = null!;
    private readonly Dictionary<string, string?> _previous = new()
    {
        ["GIT_CONFIG_GLOBAL"] = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL"),
        ["GIT_CONFIG_NOSYSTEM"] = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM"),
        ["XDG_CONFIG_HOME"] = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        ["XDG_CACHE_HOME"] = Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
    };
    private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
    public static async Task<ProxyGourceFixture> CreateAsync()
    {
        ProxyGourceFixture fixture = new();
        try
        {
            Directory.CreateDirectory(fixture.Root);
            string global = Path.Combine(fixture.Root, "global git config"); File.WriteAllText(global, "");
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global); Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(fixture.Root, "config"));
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(fixture.Root, "cache")); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            fixture.Repository = await GitRepository.InitAsync(Path.Combine(fixture.Root, "repository été"));
            await fixture.Repository.SetConfigAsync("user.name", "Avatar Tester");
            await fixture.Repository.SetConfigAsync("user.email", "avatar@example.invalid");
            await fixture.Repository.SetConfigAsync("core.hooksPath", Path.Combine(fixture.Root, "no-hooks"));
            await fixture.Repository.SetConfigAsync("commit.gpgsign", "false");
            await fixture.Repository.RunAsync(["commit", "--allow-empty", "-m", "Avatar author"]);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }
    public void Dispose()
    {
        foreach (var variable in _previous) Environment.SetEnvironmentVariable(variable.Key, variable.Value);
        CultureInfo.CurrentUICulture = _culture; Directory.Delete(Root, recursive: true);
    }
}

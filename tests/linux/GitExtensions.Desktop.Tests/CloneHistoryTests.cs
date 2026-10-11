using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class CloneHistoryTests
{
    [Test]
    public async Task DefaultSourceUsesTheCurrentLiteralBranchRemoteAndItsLastPushUrlBeforeFetchOrOrigin()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        string origin = (await SeedAsync(fixture, "origin dépôt")).Path;
        string fetch = (await SeedAsync(fixture, "fetch dépôt")).Path;
        string firstPush = (await SeedAsync(fixture, "push one")).Path;
        string lastPush = (await SeedAsync(fixture, "push two\n雪")).Path;
        const string remote = "deploy.with+literal", branch = "topic.with+literal";
        await GitAsync(fixture.Repository, "remote", "add", "origin", origin);
        await GitAsync(fixture.Repository, "remote", "add", remote, fetch);
        await GitAsync(fixture.Repository, "switch", "-c", branch);
        await fixture.Repository.SetConfigAsync($"branch.{branch}.remote", remote);
        await fixture.Repository.SetConfigAsync($"branch.{branch}.merge", "refs/heads/main");
        await GitAsync(fixture.Repository, "config", "--add", $"remote.{remote}.pushurl", firstPush);
        await GitAsync(fixture.Repository, "config", "--add", $"remote.{remote}.pushurl", lastPush);
        using EnvironmentScope transport = NoTransport(fixture.Root);
        Assert.That(await fixture.Repository.GetDefaultCloneSourceAsync(), Is.EqualTo(lastPush));
        await GitAsync(fixture.Repository, "config", "--unset-all", $"remote.{remote}.pushurl");
        Assert.That(await fixture.Repository.GetDefaultCloneSourceAsync(), Is.EqualTo(fetch));
        await GitAsync(fixture.Repository, "config", "--unset", $"branch.{branch}.remote");
        Assert.That(await fixture.Repository.GetDefaultCloneSourceAsync(), Is.EqualTo(origin));
        await GitAsync(fixture.Repository, "switch", "--detach", "HEAD");
        Assert.That(await fixture.Repository.GetDefaultCloneSourceAsync(), Is.EqualTo(origin));
        await GitAsync(fixture.Repository, "remote", "remove", "origin");
        Assert.That(await fixture.Repository.GetDefaultCloneSourceAsync(), Is.EqualTo(fetch));
        AssertNoTransport(fixture.Root);
    }

    [AvaloniaTest]
    public async Task SuggestionsPreferExplicitThenClipboardThenMruThenBranchRemoteWithoutRequestingRemoteBranches()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        Window owner = new(); owner.Show();
        await using ClipboardScope clipboard = await ClipboardScope.CreateAsync(owner, "");
        using EnvironmentScope transport = NoTransport(fixture.Root);
        const string clip = "ssh://clipboard.example.invalid/literal.git";
        const string mru = "ssh://history.example.invalid/previous.git";
        const string remote = "ssh://branch.example.invalid/push.git";
        string explicitSource = (await SeedAsync(fixture, " explicit source\n雪 ")).Path;
        await GitAsync(fixture.Repository, "remote", "add", "origin", "ssh://origin.example.invalid/fetch.git");
        await GitAsync(fixture.Repository, "remote", "add", "publish", "ssh://branch.example.invalid/fetch.git");
        string branch = (await fixture.Repository.GetStatusAsync()).Branch!;
        await fixture.Repository.SetConfigAsync($"branch.{branch}.remote", "publish");
        await fixture.Repository.SetConfigAsync("remote.publish.pushurl", remote);
        CloneSourcesStore sources = new();
        try
        {
            foreach ((string supplied, string clipboardText, bool remember, string expected) in new[]
            {
                (explicitSource, "please clone " + clip, true, explicitSource),
                ("", "please clone " + clip, true, clip),
                ("", "ordinary clipboard prose", true, mru),
                ("", "ordinary clipboard prose", false, remote)
            })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sources.FilePath)!);
                File.WriteAllText(sources.FilePath, remember ? JsonSerializer.Serialize(new[] { mru }) : "[]");
                string stored = File.ReadAllText(sources.FilePath);
                await clipboard.SetAsync(clipboardText);
                CloneDialog dialog = new(fixture.Root, supplied, fixture.Repository); dialog.Show();
                try
                {
                    await dialog.InitializeAsync();
                    Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
                    Assert.That(Field<TextBox>(dialog, "CloneSource").Text, Is.EqualTo(expected));
                    Assert.That(File.ReadAllText(sources.FilePath), Is.EqualTo(stored), "Suggestions must not visit a source.");
                    Assert.That(Field<ComboBox>(dialog, "CloneBranches").Items.Count, Is.EqualTo(2), "Suggestions must not fetch remote branch names.");
                }
                finally { dialog.Close(); }
            }
            AssertNoTransport(fixture.Root);
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    public async Task SuccessfulClonesRememberLiteralSourcesAndLocalDestinationsWithMruDeduplicationAndCapacity()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        GitRepository a = await SeedAsync(fixture, " source\n\t\"$(literal);雪 ");
        GitRepository b = await SeedAsync(fixture, "source B");
        GitRepository c = await SeedAsync(fixture, "source C");
        RepositoryHistoryStore local = new(); local.Save(new() { HistorySize = 2 });
        CloneSourcesStore sources = new();
        List<string> destinations = [];
        foreach (GitRepository source in new[] { a, b, a, c })
        {
            string subdirectory = " cloned " + destinations.Count.ToString(CultureInfo.InvariantCulture) + "\n\t\"$(literal);雪 ";
            string expected = Path.Combine(fixture.Root, subdirectory);
            CloneDialog dialog = NewClone(fixture.Root, source.Path, subdirectory);
            try
            {
                Click(dialog, "CloneRepository"); await Until(() => !dialog.IsBusy);
                Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
                Assert.That(dialog.CreatedRepository!.Path, Is.EqualTo(expected));
                Assert.That(dialog.IsVisible, Is.False);
                Assert.That((await dialog.CreatedRepository!.RunAsync(["rev-parse", "HEAD"])).StandardOutput,
                    Is.EqualTo((await source.RunAsync(["rev-parse", "HEAD"])).StandardOutput));
                destinations.Add(expected);
                Assert.That(local.Load().Recent.First().Path, Is.EqualTo(expected));
                Assert.That(sources.Load().First(), Is.EqualTo(source.Path));
            }
            finally { dialog.Close(); }
        }
        Assert.That(sources.Load(), Is.EqualTo(new[] { c.Path, a.Path }));
        Assert.That(local.Load().Recent.Select(entry => entry.Path), Is.EqualTo(destinations.TakeLast(2).Reverse()));
        Assert.That(File.ReadAllText(sources.FilePath), Does.Not.Contain(b.Path));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(sources.FilePath)!, "*.tmp"), Is.Empty);
        Assert.That((await fixture.Repository.GetStatusAsync()).Changes, Is.Empty);
    }

    [AvaloniaTest]
    public async Task CloneFailureDoesNotVisitEitherHistoryOrOverwriteSavedClonePreferences()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        CloneSourcesStore sources = new(); sources.Visit(fixture.Repository.Path, 30);
        RepositoryHistoryStore local = new(); local.Save(new() { Recent = [new(fixture.Repository.Path)] });
        new ClonePreferencesStore().Save(new(fixture.Root, false));
        Dictionary<string, byte[]> before = HistoryFiles(sources, local);
        CloneDialog dialog = NewClone(fixture.Root, Path.Combine(fixture.Root, "absent literal source\n雪"), "failed clone");
        try
        {
            Click(dialog, "CloneRepository"); await Until(() => !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Not.Null.And.Not.Empty);
            Assert.That(dialog.CreatedRepository, Is.Null); Assert.That(dialog.IsVisible, Is.True);
            AssertHistoryFilesUnchanged(before);
            Assert.That((await fixture.Repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingAnActualSshCloneCancelsItsProcessWithoutVisitingSourceOrDestination()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("The cancellation fixture uses a Unix SSH executable."); return; }
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        CloneSourcesStore sources = new(); sources.Visit(fixture.Repository.Path, 30);
        RepositoryHistoryStore local = new(); local.Save(new() { Recent = [new(fixture.Repository.Path)] });
        new ClonePreferencesStore().Save(new(fixture.Root, false));
        Dictionary<string, byte[]> before = HistoryFiles(sources, local);
        string script = Path.Combine(fixture.Root, "blocked ssh.py"), pidFile = Path.Combine(fixture.Root, "ssh.pid");
        await File.WriteAllTextAsync(script, "#!/usr/bin/python3\nimport os,time\nfrom pathlib import Path\nPath(" + JsonSerializer.Serialize(pidFile) + ").write_text(str(os.getpid()))\ntime.sleep(60)\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using EnvironmentScope ssh = new(new() { ["GIT_SSH"] = script, ["GIT_SSH_COMMAND"] = null, ["GIT_SSH_VARIANT"] = "ssh" });
        CloneDialog dialog = NewClone(fixture.Root, "ssh://example.invalid/no-real-network.git", "cancelled clone");
        try
        {
            Click(dialog, "CloneRepository");
            await Until(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0);
            int pid = int.Parse(await File.ReadAllTextAsync(pidFile), CultureInfo.InvariantCulture);
            Assert.That(dialog.IsBusy, Is.True);
            dialog.Close(); await Until(() => !dialog.IsBusy && !dialog.IsVisible);
            await Until(() => Terminated(pid));
            Assert.That(dialog.CreatedRepository, Is.Null);
            Assert.That(dialog.LastError, Is.EqualTo("Operation cancelled"));
            AssertHistoryFilesUnchanged(before);
            Assert.That((await fixture.Repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); await Until(() => !dialog.IsBusy); }
    }

    [AvaloniaTest]
    public async Task MainCloneThenNoKeepsTheActiveRepositoryButTheRecentMenuCanOpenTheLiteralClone()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        MainWindow main = new() { Width = 1440, Height = 900 }; main.Show();
        await using ClipboardScope clipboard = await ClipboardScope.CreateAsync(main, "");
        try
        {
            await main.OpenRepositoryAsync(fixture.Repository.Path);
            new CloneSourcesStore().Visit(fixture.Repository.Path, 30);
            FileAction(main, "Clone repository…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => main.OwnedWindows.OfType<CloneDialog>().Any());
            CloneDialog clone = main.OwnedWindows.OfType<CloneDialog>().Single(); await clone.InitializeAsync();
            Assert.That(Field<TextBox>(clone, "CloneSource").Text, Is.EqualTo(fixture.Repository.Path));
            string parent = Path.Combine(fixture.Root, "destination\n\t$(literal);雪"), child = " cloned repo ";
            Field<TextBox>(clone, "CloneDestination").Text = parent; Field<TextBox>(clone, "CloneSubdirectory").Text = child;
            string expected = Path.Combine(parent, child);
            Click(clone, "CloneRepository");
            await Until(() => main.OwnedWindows.Any(window => window is not CloneDialog && window.Title == "Open"));
            Window question = main.OwnedWindows.Single(window => window.Title == "Open");
            Assert.That(question.GetVisualDescendants().OfType<Button>().Select(button => button.Content as string), Is.EquivalentTo(new[] { "Yes", "No" }));
            Assert.That(new RepositoryHistoryStore().Load().Recent.First().Path, Is.EqualTo(expected), "Success visits the clone before the Open answer.");
            question.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "No").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !question.IsVisible && !main.IsBusy);
            Assert.That(main.Title, Is.EqualTo("Git Extensions — " + fixture.Repository.Path));
            Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
            MenuItem recent = FileAction(main, "Recent repositories");
            MenuItem remembered = recent.Items.OfType<MenuItem>().Single(item => ToolTip.GetTip(item) as string == expected);
            remembered.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => !main.IsBusy && main.Title == "Git Extensions — " + expected);
            Assert.That((await clone.CreatedRepository!.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { foreach (Window child in main.OwnedWindows.ToArray()) child.Close(); main.Close(); }
    }

    [Test]
    public async Task InvalidSourceJsonRecoversAndCredentialUrlsAreExcludedWithoutTrimmingLiteralPaths()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        string literal = (await SeedAsync(fixture, " literal\n\t?token=filename#雪 ")).Path;
        CloneSourcesStore sources = new(); Directory.CreateDirectory(Path.GetDirectoryName(sources.FilePath)!);
        File.WriteAllText(sources.FilePath, "{broken json"); Assert.That(sources.Load(), Is.Empty);
        sources.Visit(literal, 2); string before = File.ReadAllText(sources.FilePath);
        foreach (string credentials in new[]
        {
            "https://alice:SECRET@example.invalid/repo.git", "https://alice@example.invalid/repo.git",
            "https://example.invalid/repo.git?token=SECRET", "ssh://alice:SECRET@example.invalid/repo.git"
        })
        { sources.Visit(credentials, 2); Assert.That(File.ReadAllText(sources.FilePath), Is.EqualTo(before)); }
        Assert.That(sources.Load(), Is.EqualTo(new[] { literal }));
        string ssh = "git@example.invalid:literal.git";
        File.WriteAllText(sources.FilePath, JsonSerializer.Serialize(new string?[] { literal, literal, "https://alice:SECRET@example.invalid/repo.git", null, " ", ssh }));
        Assert.That(sources.Load(), Is.EqualTo(new[] { literal, ssh }));
        sources.Visit(literal, 2);
        Assert.That(File.ReadAllText(sources.FilePath), Does.Not.Contain("SECRET"));
        Assert.That(sources.Load(), Is.EqualTo(new[] { literal, ssh }));
    }

    [AvaloniaTest]
    public async Task GitUrlRewriteClonesACredentialSourceLocallyWithoutPersistingItsSecretInXdgMru()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        const string source = "https://clone-user:DUMMY-CLONE-SECRET@example.invalid/project.git";
        CloneSourcesStore sources = new(); sources.Visit(fixture.Repository.Path, 30); string before = File.ReadAllText(sources.FilePath);
        using EnvironmentScope rewrite = new(new()
        {
            ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "url." + new Uri(fixture.Repository.Path).AbsoluteUri + ".insteadOf",
            ["GIT_CONFIG_VALUE_0"] = source, ["GIT_ALLOW_PROTOCOL"] = "file"
        });
        CloneDialog dialog = NewClone(fixture.Root, source, "credential clone");
        try
        {
            Click(dialog, "CloneRepository"); await Until(() => !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.CreatedRepository, Is.Not.Null);
            Assert.That(File.ReadAllText(sources.FilePath), Is.EqualTo(before));
            Assert.That(new RepositoryHistoryStore().Load().Recent.First().Path, Is.EqualTo(dialog.CreatedRepository!.Path));
            foreach (string file in Directory.GetFiles(Path.GetDirectoryName(sources.FilePath)!, "*.json"))
            { Assert.That(File.ReadAllText(file), Does.Not.Contain("DUMMY-CLONE-SECRET")); }
            Assert.That((await dialog.CreatedRepository!.RunAsync(["rev-parse", "HEAD"])).StandardOutput,
                Is.EqualTo((await fixture.Repository.RunAsync(["rev-parse", "HEAD"])).StandardOutput));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task MalformedCloneAndLocalHistoryJsonFallsBackToTheRemoteAndParentWithoutSavingSuggestions()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        await GitAsync(fixture.Repository, "remote", "add", "origin", fixture.Repository.Path);
        CloneSourcesStore sources = new(); RepositoryHistoryStore local = new();
        Directory.CreateDirectory(Path.GetDirectoryName(sources.FilePath)!);
        string[] files = [sources.FilePath, local.FilePath, Path.Combine(Path.GetDirectoryName(sources.FilePath)!, "clone-preferences.json")];
        foreach (string file in files) File.WriteAllText(file, "{malformed");
        Window owner = new(); owner.Show();
        await using ClipboardScope clipboard = await ClipboardScope.CreateAsync(owner, "ordinary text");
        using EnvironmentScope transport = NoTransport(fixture.Root);
        CloneDialog dialog = new(repository: fixture.Repository); dialog.Show();
        try
        {
            await dialog.InitializeAsync();
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(Field<TextBox>(dialog, "CloneSource").Text, Is.EqualTo(fixture.Repository.Path));
            Assert.That(Field<TextBox>(dialog, "CloneDestination").Text, Is.EqualTo(fixture.Root));
            Assert.That(Field<ComboBox>(dialog, "CloneSources").Items, Is.Empty);
            Assert.That(Field<ComboBox>(dialog, "CloneDestinations").Items, Is.Empty);
            foreach (string file in files) Assert.That(File.ReadAllText(file), Is.EqualTo("{malformed"));
            AssertNoTransport(fixture.Root);
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    public async Task SourceAndDestinationPopupsRenderCompactInputsAndEscapedCaptionsInBothThemesWithoutChangingPaths()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        GitRepository literal = await SeedAsync(fixture, "source\n\t\"$(literal);雪");
        string otherParent = Path.Combine(fixture.Root, "destination\n\t\"$(literal);雪");
        GitRepository other = await SeedAsync(fixture, Path.Combine(otherParent, "nested repo"));
        CloneSourcesStore sources = new(); sources.Visit(fixture.Repository.Path, 30); sources.Visit(literal.Path, 30);
        RepositoryHistoryStore local = new(); local.Save(new() { Recent = [new(other.Path), new(fixture.Repository.Path), new(literal.Path)] });
        using EnvironmentScope transport = NoTransport(fixture.Root);
        CloneDialog dialog = new(fixture.Root, fixture.Repository.Path); dialog.Show(); await dialog.InitializeAsync();
        try
        {
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme;
                Render(dialog);
                foreach (string name in new[] { "ClonePersonal", "CloneBare" })
                {
                    RadioButton radio = Field<RadioButton>(dialog, name);
                    AssertGlyphInside(radio, radio.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single(shape => shape.Name == "OuterEllipse"));
                }
                foreach (string name in new[] { "CloneSubmodules", "CloneFullHistory" })
                {
                    CheckBox check = Field<CheckBox>(dialog, name);
                    AssertGlyphInside(check, check.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "NormalRectangle"));
                }
                ComboBox branches = Field<ComboBox>(dialog, "CloneBranches");
                AssertGlyphInside(branches, branches.GetVisualDescendants().OfType<PathIcon>().Single(icon => icon.Name == "DropDownGlyph"));
                foreach ((string comboName, string textName, string selected, string kind) in new[]
                {
                    ("CloneSources", "CloneSource", literal.Path, "sources"),
                    ("CloneDestinations", "CloneDestination", otherParent, "destinations")
                })
                {
                    Render(dialog);
                    ComboBox combo = Field<ComboBox>(dialog, comboName); TextBox field = Field<TextBox>(dialog, textName);
                    Assert.That(combo.Bounds.Height, Is.EqualTo(24)); Assert.That(field.Bounds.Height, Is.EqualTo(24));
                    AssertGlyphInside(combo, combo.GetVisualDescendants().OfType<PathIcon>().Single(icon => icon.Name == "DropDownGlyph"));
                    combo.IsDropDownOpen = true; Render(dialog);
                    Popup popup = combo.GetVisualDescendants().OfType<Popup>().Single();
                    Assert.That(popup.IsOpen, Is.True); Assert.That(popup.Child, Is.Not.Null);
                    string caption = ExportDialogControls.DisplayPath(selected);
                    TextBlock visible = popup.Child!.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == caption);
                    Assert.That(visible.IsEffectivelyVisible, Is.True); Assert.That(visible.Bounds.Width, Is.GreaterThan(0));
                    Capture(dialog, $"classic-clone-history-{kind}-{theme}.png");
                    if (visible.GetVisualRoot() is TopLevel popupRoot && popupRoot != dialog)
                    { Capture(popupRoot, $"classic-clone-history-{kind}-popup-{theme}.png"); }
                    combo.SelectedItem = selected; combo.IsDropDownOpen = false; Render(dialog);
                    Assert.That(field.Text, Is.EqualTo(selected), "The escaped display caption must never replace the literal path used by Git.");
                }
            }
            AssertNoTransport(fixture.Root);
            Assert.That(sources.Load(), Is.EqualTo(new[] { literal.Path, fixture.Repository.Path }));
            Assert.That(local.Load().Recent.Select(entry => entry.Path), Is.EqualTo(new[] { other.Path, fixture.Repository.Path, literal.Path }));
        }
        finally { dialog.Close(); }
    }

    private static void AssertGlyphInside(Control owner, Control glyph)
    {
        var point = glyph.TranslatePoint(default, owner);
        Assert.That(point, Is.Not.Null);
        Assert.That(new Avalonia.Rect(owner.Bounds.Size).Contains(new Avalonia.Rect(point!.Value, glyph.Bounds.Size)), Is.True,
            $"The rendered {glyph.Name} must fit inside {owner.Name} without clipping.");
    }

    private static async Task<GitRepository> SeedAsync(ProxyGourceFixture fixture, string name)
    {
        GitRepository repository = await GitRepository.InitAsync(Path.Combine(fixture.Root, name));
        await repository.SetConfigAsync("user.name", "Clone History Tester"); await repository.SetConfigAsync("user.email", "clone@example.invalid");
        await repository.SetConfigAsync("commit.gpgsign", "false"); await repository.SetConfigAsync("core.hooksPath", Path.Combine(fixture.Root, "no-hooks"));
        await GitAsync(repository, "commit", "--allow-empty", "-m", "Source commit"); return repository;
    }
    private static async Task GitAsync(GitRepository repository, params string[] arguments)
    { GitResult result = await repository.RunAsync(arguments); Assert.That(result.Success, Is.True, result.StandardError); }
    private static CloneDialog NewClone(string parent, string source, string child)
    { CloneDialog dialog = new(parent, source); dialog.Show(); Field<TextBox>(dialog, "CloneSubdirectory").Text = child; return dialog; }
    private static T Field<T>(TopLevel owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Window owner, string name)
    { Button button = Field<Button>(owner, name); Assert.That(button.IsEnabled, Is.True); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static MenuItem FileAction(MainWindow main, string action)
        => main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(menu => menu.Header as string == "File").Items.OfType<MenuItem>().Single(item => item.Header as string == action);
    private static Dictionary<string, byte[]> HistoryFiles(CloneSourcesStore sources, RepositoryHistoryStore local)
        => new[] { sources.FilePath, local.FilePath, Path.Combine(Path.GetDirectoryName(sources.FilePath)!, "clone-preferences.json") }.ToDictionary(file => file, File.ReadAllBytes);
    private static void AssertHistoryFilesUnchanged(Dictionary<string, byte[]> files)
    { foreach ((string file, byte[] before) in files) Assert.That(File.ReadAllBytes(file), Is.EqualTo(before)); }
    private static EnvironmentScope NoTransport(string root) => new(new() { ["GIT_ALLOW_PROTOCOL"] = "file", ["GIT_TRACE"] = Path.Combine(root, "suggestions.trace") });
    private static void AssertNoTransport(string root)
    {
        string file = Path.Combine(root, "suggestions.trace"); string trace = File.Exists(file) ? File.ReadAllText(file) : "";
        Assert.That(trace, Does.Not.Match(@"(?:built-in: git |exec: git-)(?:ls-remote|fetch|clone|fetch-pack|upload-pack|remote-https)\b"));
        Assert.That(trace, Does.Not.Contain("git-upload-pack").And.Not.Contain("git-remote-https"));
    }
    private static bool Terminated(int pid)
    {
        string path = $"/proc/{pid}/status";
        try { return !File.Exists(path) || File.ReadLines(path).Any(line => line.StartsWith("State:", StringComparison.Ordinal) && line.Contains('Z')); }
        catch (IOException) when (!File.Exists(path)) { return true; }
    }
    private static async Task Until(Func<bool> predicate)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)); while (!predicate()) await Task.Delay(20, timeout.Token); }
    private static void Render(TopLevel owner) { owner.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Capture(TopLevel owner, string name)
    {
        Render(owner); using var frame = owner.CaptureRenderedFrame() ?? throw new AssertionException("No popup/dialog frame.");
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts/linux/screenshots"); Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, name); frame.Save(file); TestContext.AddTestAttachment(file);
    }
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous;
        public EnvironmentScope(Dictionary<string, string?> values)
        { _previous = values.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable); foreach ((string key, string? value) in values) Environment.SetEnvironmentVariable(key, value); }
        public void Dispose() { foreach ((string key, string? value) in _previous) Environment.SetEnvironmentVariable(key, value); }
    }
    private sealed class ClipboardScope(IClipboard clipboard, string? previous) : IAsyncDisposable
    {
        public static async Task<ClipboardScope> CreateAsync(TopLevel owner, string value)
        {
            IClipboard clipboard = owner.Clipboard ?? throw new AssertionException("Headless clipboard unavailable.");
            string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard);
            await ClipboardExtensions.SetTextAsync(clipboard, value); return new(clipboard, previous);
        }
        public Task SetAsync(string value) => ClipboardExtensions.SetTextAsync(clipboard, value);
        public ValueTask DisposeAsync() => new(ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""));
    }
}

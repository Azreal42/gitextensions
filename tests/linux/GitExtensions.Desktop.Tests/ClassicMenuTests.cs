using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ClassicMenuTests
{
    [AvaloniaTest]
    public async Task ToolsAndHelpMatchOriginalOrderIconsGesturesAndCompactPopupMetricsInBothThemes()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        Menu menu = f.Window.GetVisualDescendants().OfType<Menu>().Single();
        string[] tools = ["Git bash", "Git GUI", "GitK", "|", "Git command log", "|", "Settings…"];
        string[] help = ["User manual", "Changelog", "|", "Translate", "|", "Donate", "Yes, I allow telemetry", "Report an issue", "Check for updates", "About"];
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            f.Window.RequestedThemeVariant = theme;
            foreach ((string name, string[] expected) in new[] { ("ToolsMenu", tools), ("HelpMenu", help) })
            {
                MenuItem root = menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
                root.IsSubMenuOpen = true; Pump(f.Window);
                Assert.That(root.Items.Cast<object>().Select(item => item is Separator ? "|" : Caption((MenuItem)item)), Is.EqualTo(expected));
                Assert.That(root.Bounds.Width, Is.LessThan(65), "Top-level menu buttons must not reserve a popup icon gutter.");
                MenuItem[] rows = root.Items.OfType<MenuItem>().ToArray();
                Assert.That(rows.Select(item => item.Bounds.Height), Is.All.EqualTo(22));
                Assert.That(root.Items.OfType<Separator>().Select(item => item.Bounds.Height), Is.All.EqualTo(6));
                Assert.That(rows.Where(item => item.Name != "tsmiTelemetryEnabled").All(item => item.Icon is Image { Source: not null, Width: 16, Height: 16 }), Is.True);
                double[] labels = rows.Select(item => item.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_HeaderPresenter")
                    .TranslatePoint(default, TopLevel.GetTopLevel(item)!)!.Value.X).ToArray();
                Assert.That(labels.Distinct().Count(), Is.EqualTo(1), "Iconless entries must use the same label column.");
                Assert.That(((ISolidColorBrush)rows[0].Foreground!).Color, Is.EqualTo(Color.Parse(theme == ThemeVariant.Dark ? "#F0F0F0" : "#000000")));
                using var bitmap = TopLevel.GetTopLevel(rows[0])!.CaptureRenderedFrame();
                Assert.That(bitmap, Is.Not.Null);
                string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"classic-{name}-{theme}.png");
                bitmap!.Save(path); TestContext.AddTestAttachment(path);
                root.IsSubMenuOpen = false; Pump(f.Window);
            }
        }
        Assert.That(Find(f.Window, "gitBashToolStripMenuItem").InputGesture, Is.EqualTo(new KeyGesture(Key.G, KeyModifiers.Control)));
        Assert.That(Find(f.Window, "gitcommandLogToolStripMenuItem").InputGesture, Is.EqualTo(new KeyGesture(Key.F12)));
        Assert.That(Find(f.Window, "settingsToolStripMenuItem").InputGesture, Is.EqualTo(new KeyGesture(Key.OemComma, KeyModifiers.Control)));
        Assert.That(Find(f.Window, "tsmiTelemetryEnabled").IsChecked, Is.False);
    }

    [AvaloniaTest]
    public async Task ToolbarFlyoutsAndLateNestedItemsShareTheSameMetricsAndAlignedChevrons()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        Button anchor = f.Window.GetVisualDescendants().OfType<Button>().First(button => button.Name == "BrowsePullDropdown");
        MenuFlyout flyout = (MenuFlyout)anchor.Flyout!;
        flyout.ShowAt(anchor); Pump(f.Window);
        MenuItem defaults = flyout.Items.OfType<MenuItem>().Single(item => item.Name == "PullMenuSetDefault");
        defaults.IsSubMenuOpen = true; Pump(f.Window);
        MenuItem[] nested = defaults.Items.OfType<MenuItem>().ToArray();
        Assert.That(flyout.Items.OfType<MenuItem>().Concat(nested).Where(item => item.IsVisible).Select(item => item.Bounds.Height), Is.All.EqualTo(22));
        Assert.That(nested.Where(item => item.IsChecked).Count(), Is.EqualTo(1));
        flyout.Hide(); Pump(f.Window);
        using (var appearance = new GitExtensions.Desktop.Controls.DiffViewerAppearanceMenu())
        {
            appearance.SetContext(true, true);
            MenuFlyout viewer = appearance.CreateFlyout(); viewer.ShowAt(anchor); Pump(f.Window);
            Assert.That(viewer.Items.OfType<MenuItem>().Where(item => item.IsVisible).Select(item => item.Bounds.Height), Is.All.EqualTo(22));
            viewer.Hide();
        }
        ListBox history = f.Window.GetVisualDescendants().OfType<ListBox>().Single(item => item.Name == "RevisionHistory");
        ContextMenu context = history.ContextMenu!;
        MenuItem newGroup = new() { Header = "New", ItemsSource = new[] { new MenuItem { Header = new TextBlock { Text = "literal_branch" } } } };
        context.ItemsSource = context.Items.Cast<object>().Append(newGroup).ToArray();
        context.Open(history); newGroup.IsSubMenuOpen = true; Pump(f.Window);
        MenuItem late = newGroup.Items.OfType<MenuItem>().Single();
        Assert.That(late.Bounds.Height, Is.EqualTo(22));
        Assert.That(((TextBlock)late.Header!).Text, Is.EqualTo("literal_branch"));
        context.Close();
    }

    [AvaloniaTest]
    public async Task HelpLinksTargetForkWhereAppropriateAndDialogsShowRealBundledInformation()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        List<ProcessStartInfo> launches = [];
        f.Window.ExternalLauncher = info => { launches.Add(info); return null; };
        foreach (string name in new[] { "userManualToolStripMenuItem", "translateToolStripMenuItem", "donateToolStripMenuItem", "checkForUpdatesToolStripMenuItem", "reportAnIssueToolStripMenuItem" })
        { Click(Find(f.Window, name)); }
        await Until(() => launches.Count == 5);
        Assert.That(launches.Select(info => info.FileName), Is.All.EqualTo("xdg-open"));
        Assert.That(launches.Select(info => info.ArgumentList.Single()), Is.EqualTo(new[] { "https://git-extensions-documentation.readthedocs.io/", "https://github.com/gitextensions/gitextensions/wiki/Translations", "https://opencollective.com/gitextensions", "https://github.com/Azreal42/gitextensions/releases", "https://github.com/Azreal42/gitextensions/issues" }));
        Assert.That(await ClipboardExtensions.TryGetTextAsync(f.Window.Clipboard!), Does.Contain("Git Extensions").And.Not.Contain(f.Repository));
        foreach ((string name, string text) in new[] { ("aboutToolStripMenuItem", "Linux / Avalonia"), ("changelogToolStripMenuItem", "Git Extensions") })
        {
            Click(Find(f.Window, name)); await Until(() => f.Window.OwnedWindows.OfType<DesktopInformationWindow>().Any());
            DesktopInformationWindow dialog = f.Window.OwnedWindows.OfType<DesktopInformationWindow>().Single();
            Assert.That(dialog.GetVisualDescendants().OfType<TextBox>().Single().Text, Does.Contain(text)); dialog.Close(); Pump(f.Window);
        }
    }

    [AvaloniaTest]
    public async Task ToolsUseRepositoryDirectoryAndGitGuiIsGuardedInBareAndDashboardContexts()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        List<ProcessStartInfo> launches = [];
        f.Window.ExternalLauncher = info => { launches.Add(info); return null; };
        Click(Find(f.Window, "gitGUIToolStripMenuItem")); Click(Find(f.Window, "kGitToolStripMenuItem"));
        Assert.That(launches.Select(info => info.FileName), Is.EqualTo(new[] { "git", "gitk" }));
        Assert.That(launches[0].ArgumentList, Is.EqualTo(new[] { "gui" }));
        Assert.That(launches.Select(info => info.WorkingDirectory), Is.All.EqualTo(f.Repository));
        string bare = Path.Combine(Path.GetDirectoryName(f.Repository)!, "bare with space.git");
        Assert.That((await f.Git.RunAsync(f.Repository, ["clone", "--bare", f.Repository, bare])).Success, Is.True);
        await f.Window.OpenRepositoryAsync(bare);
        Assert.That(Find(f.Window, "gitGUIToolStripMenuItem").IsEnabled, Is.False);
        Assert.That(Find(f.Window, "kGitToolStripMenuItem").IsEnabled, Is.True);
        Click(Find(f.Window, "kGitToolStripMenuItem")); Assert.That(launches.Last().WorkingDirectory, Is.EqualTo(bare));
    }

    [AvaloniaTest]
    public async Task MissingExternalGitToolShowsAnErrorAndClosingOwnerCancelsOnlyTheWait()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        f.Window.ExternalLauncher = _ => Process.Start(new ProcessStartInfo("/bin/false") { UseShellExecute = false });
        Click(Find(f.Window, "gitGUIToolStripMenuItem"));
        await Until(() => f.Window.OwnedWindows.Any(window => window.Title == "Error"));
        Window error = f.Window.OwnedWindows.Single(window => window.Title == "Error");
        Assert.That(error.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("git-gui", StringComparison.Ordinal) == true), Is.True);
        error.Close();
        int pid = 0;
        f.Window.ExternalLauncher = _ =>
        {
            Process process = Process.Start(new ProcessStartInfo("/bin/sleep") { UseShellExecute = false, ArgumentList = { "30" } })!;
            pid = process.Id; return process;
        };
        Click(Find(f.Window, "kGitToolStripMenuItem"));
        using Process ownedTestTool = Process.GetProcessById(pid);
        try
        {
            f.Window.Close(); await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            Assert.That(f.Window.OwnedWindows, Is.Empty);
            Assert.That(ownedTestTool.HasExited, Is.False, "Closing the browser leaves separately launched user tools running.");
        }
        finally { if (!ownedTestTool.HasExited) { ownedTestTool.Kill(); await ownedTestTool.WaitForExitAsync(); } }
    }

    [AvaloniaTest]
    public async Task F12ReusesLiveCommandLogAndClearDoesNotAffectRepository()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        f.Window.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.None); Pump(f.Window);
        GitCommandLogWindow log = f.Window.OwnedWindows.OfType<GitCommandLogWindow>().Single();
        GitCommandLog.Clear(); await f.Git.RunAsync(f.Repository, ["status", "--porcelain"]); log.Refresh();
        TextBox box = log.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.That(box.Text, Does.Contain("git status").And.Contain(f.Repository));
        Click(Find(f.Window, "gitcommandLogToolStripMenuItem"));
        Assert.That(f.Window.OwnedWindows.OfType<GitCommandLogWindow>().Count(), Is.EqualTo(1));
        log.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ClearGitCommandLog").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(box.Text, Is.Empty); Assert.That(GitCommandLog.Snapshot(), Is.Empty);
        log.Close();
    }

    [AvaloniaTest]
    public async Task SettingsSaveRealPreferencesAndCancelPreservesThem()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        PreferencesStore store = new(); DesktopPreferences initial = store.Load();
        Click(Find(f.Window, "settingsToolStripMenuItem")); await Until(() => f.Window.OwnedWindows.OfType<DesktopSettingsDialog>().Any());
        DesktopSettingsDialog dialog = f.Window.OwnedWindows.OfType<DesktopSettingsDialog>().Single();
        dialog.GetVisualDescendants().OfType<NumericUpDown>().Single().Value = 150;
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveDesktopSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !dialog.IsVisible && !f.Window.IsBusy && store.Load().HistoryLimit == 150);
        Assert.That(store.Load().AutoNormaliseSymbol, Is.EqualTo(initial.AutoNormaliseSymbol));
        Click(Find(f.Window, "tsmiTelemetryEnabled")); await Until(() => f.Window.OwnedWindows.OfType<DesktopSettingsDialog>().Any());
        dialog = f.Window.OwnedWindows.OfType<DesktopSettingsDialog>().Single();
        Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "TelemetryStatus").Text, Does.Contain("disabled"));
        dialog.GetVisualDescendants().OfType<NumericUpDown>().Single().Value = 350;
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CancelDesktopSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(store.Load().HistoryLimit, Is.EqualTo(150));
        Click(Find(f.Window, "settingsToolStripMenuItem")); await Until(() => f.Window.OwnedWindows.OfType<DesktopSettingsDialog>().Any());
        dialog = f.Window.OwnedWindows.OfType<DesktopSettingsDialog>().Single();
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OpenGitConfiguration").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => f.Window.OwnedWindows.Any(window => window.Title == "Configuration — Git Extensions"));
        Window configuration = f.Window.OwnedWindows.Single(window => window.Title == "Configuration — Git Extensions");
        Assert.That(configuration.GetVisualDescendants().OfType<ListBox>().Any(), Is.True);
        configuration.Close();
        Assert.That(store.Load().HistoryLimit, Is.EqualTo(150));
    }

    [AvaloniaTest]
    public async Task FrenchUsesTheOriginalToolsAndHelpCatalogueWithAccessKeys()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
        MainWindow french = new() { Width = 1440, Height = 900 }; french.Show();
        try
        {
            MenuItem help = french.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Name == "HelpMenu");
            MenuItem tools = french.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Name == "ToolsMenu");
            foreach (MenuItem root in new[] { tools, help })
            {
                root.IsSubMenuOpen = true; Pump(french);
                Assert.That(root.Items.OfType<MenuItem>().Select(item => item.Bounds.Height), Is.All.EqualTo(22));
                using var bitmap = TopLevel.GetTopLevel(root.Items.OfType<MenuItem>().First())!.CaptureRenderedFrame();
                string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"classic-{root.Name}-fr.png"); bitmap!.Save(path); TestContext.AddTestAttachment(path);
                root.IsSubMenuOpen = false;
            }
            Assert.That(Caption(Find(french, "userManualToolStripMenuItem")), Does.Contain("Utilisateur"));
            Assert.That(((AccessText)Find(french, "settingsToolStripMenuItem").Header!).Text, Does.Contain('_'));
        }
        finally { french.Close(); }
    }

    [AvaloniaTest]
    public async Task CommandJournalKeepsRealExitStatusWithoutRetainingSecretsAndBoundsItsMemory()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n");
        GitCommandLog.Clear();
        const string secret = "private-token-sentinel";
        GitResult good = await f.Git.RunAsync(f.Repository, ["-c", "http.extraHeader=Authorization: " + secret, "status", "--porcelain"]);
        GitResult bad = await f.Git.RunAsync(f.Repository, ["rev-parse", "--verify", "missing-ref"]);
        GitCommandLogEntry[] entries = GitCommandLog.Snapshot().ToArray();
        Assert.That(entries.Select(entry => entry.Command), Is.EqualTo(new[] { "git status", "git rev-parse" }));
        Assert.That(entries.Select(entry => entry.ExitCode), Is.EqualTo(new int?[] { good.ExitCode, bad.ExitCode }));
        Assert.That(entries.All(entry => entry.Duration >= TimeSpan.Zero), Is.True);
        Assert.That(System.Text.Json.JsonSerializer.Serialize(entries), Does.Not.Contain(secret).And.Not.Contain("extraHeader").And.Not.Contain("missing-ref"));
        for (int i = 0; i < GitCommandLog.Capacity; i++) { await f.Git.RunAsync(f.Repository, ["--version"]); }
        Assert.That(GitCommandLog.Snapshot().Count, Is.EqualTo(GitCommandLog.Capacity));
        Assert.That(GitCommandLog.Snapshot().All(entry => entry.Command == "git"), Is.True);
        GitCommandLog.Clear();
    }

    private static MenuItem Find(MainWindow window, string name) => Descendants(window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>()).Single(item => item.Name == name);
    private static IEnumerable<MenuItem> Descendants(IEnumerable<MenuItem> roots) { foreach (MenuItem root in roots) { yield return root; foreach (MenuItem child in Descendants(root.Items.OfType<MenuItem>())) { yield return child; } } }
    private static string Caption(MenuItem item) => item.Header is AccessText text ? (text.Text ?? "").Replace("_", "", StringComparison.Ordinal) : item.Header?.ToString() ?? "";
    private static void Click(MenuItem item) { item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); window.CaptureRenderedFrame()?.Dispose(); }
    private static async Task Until(Func<bool> predicate) { for (int i = 0; i < 500 && !predicate(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); } Assert.That(predicate(), Is.True); }
}

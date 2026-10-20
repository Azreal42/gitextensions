using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommandLogCaptureTests
{
    [Test]
    public async Task MissingFalseAndAnUnchangedChoiceDoNotCreateOrRewriteTheWindowsXmlAuthority()
    {
        using XmlScope f = new(); CommandLogCapturePreferences store = new();
        CommandLogCaptureState missing = await store.LoadAsync();
        Assert.That(missing.Error, Is.Null); Assert.That(missing.Enabled, Is.False); Assert.That(File.Exists(store.FilePath), Is.False);
        await store.SetAsync(missing, false); Assert.That(Directory.Exists(f.Root), Is.False);
        CommandLogCaptureState on = await store.SetAsync(missing, true);
        GitXmlSettingsSnapshot xml = await GitXmlSettingsStore.LoadAsync(store.FilePath);
        Assert.That(xml.Values[CommandLogCapturePreferences.Key], Is.EqualTo("true")); Assert.That(GitCommandLog.CapturePolicy.CaptureCallStacks, Is.True);
        byte[] bytes = File.ReadAllBytes(store.FilePath); DateTime stamp = File.GetLastWriteTimeUtc(store.FilePath);
        await store.SetAsync(on, true); Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(bytes)); Assert.That(File.GetLastWriteTimeUtc(store.FilePath), Is.EqualTo(stamp));
        CommandLogCaptureState reopened = await new CommandLogCapturePreferences().LoadAsync(); Assert.That(reopened.Enabled, Is.True);
        await store.SetAsync(reopened, false); Assert.That((await new CommandLogCapturePreferences().LoadAsync()).Enabled, Is.False);
        Assert.That(File.Exists(new PreferencesStore().FilePath), Is.False, "The Windows XML key must not gain a JSON authority.");
    }

    [Test]
    public async Task CompetingSnapshotsUseStrictCasAndAnExplicitReopenPreservesUnknownNilAndUnicodeValues()
    {
        using XmlScope f = new(); CommandLogCapturePreferences store = new();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "<dictionary xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><item><key><string>future.λ</string></key><value><string>Ω &amp; literal</string></value></item><item><key><string>future.nil</string></key><value><string xsi:nil=\"true\" /></value></item></dictionary>");
        CommandLogCaptureState left = await store.LoadAsync(), right = await store.LoadAsync();
        async Task<Exception?> Attempt(CommandLogCaptureState value)
        { try { await store.SetAsync(value, true); return null; } catch (IOException error) { return error; } }
        Exception?[] results = await Task.WhenAll(Attempt(left), Attempt(right));
        Assert.That(results.Count(error => error is null), Is.EqualTo(1)); Assert.That(results.Count(error => error is IOException), Is.EqualTo(1));
        Assert.That(GitCommandLog.CapturePolicy.CaptureCallStacks, Is.True);
        byte[] accepted = File.ReadAllBytes(store.FilePath);
        Assert.CatchAsync<IOException>(async () => await store.SetAsync(left, false));
        Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(accepted)); Assert.That(GitCommandLog.CapturePolicy.CaptureCallStacks, Is.True);
        CommandLogCaptureState current = await store.LoadAsync(); await store.SetAsync(current, false);
        GitXmlSettingsSnapshot xml = await GitXmlSettingsStore.LoadAsync(store.FilePath);
        Assert.That(xml.Values["future.λ"], Is.EqualTo("Ω & literal")); Assert.That(xml.Values["future.nil"], Is.Null);
        Assert.That(xml.Values[CommandLogCapturePreferences.Key], Is.EqualTo("false")); Assert.That(GitCommandLog.CapturePolicy.CaptureCallStacks, Is.False);
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task TheRealCheckboxPersistsImmediatelyAndOldEntriesAndFirstArrowsRemainStable(string language, bool dark)
    {
        GitCommandLogCapturePolicy policy = GitCommandLog.CapturePolicy;
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        GitCommandLogWindow? log = null;
        try
        {
            byte[] json = File.ReadAllBytes(f.Desktop.FilePath); GitCommandLog.Clear(); log = await Open(language, dark);
            CheckBox choice = Field<CheckBox>(log, "chkCaptureCallStacks");
            Assert.That(choice.Content, Is.EqualTo(language == "fr" ? "Capturer les piles d'appels" : "Capture call stacks"));
            Assert.That(choice.IsChecked, Is.False); Assert.That(choice.IsEnabled, Is.True);
            WrapPanel footer = Field<WrapPanel>(log, "CommandLogOptions"); CheckBox[] options = footer.Children.OfType<CheckBox>().ToArray();
            Assert.That(options.Select(option => option.Bounds.Top).Distinct().Count(), Is.EqualTo(1), "The natural 659-pixel window keeps the Windows options on one line.");
            log.Width = log.MinWidth; Pump(log);
            foreach (CheckBox option in options)
            {
                Point position = option.TranslatePoint(default, log)!.Value;
                Assert.That(position.X, Is.GreaterThanOrEqualTo(0)); Assert.That(position.X + option.Bounds.Width, Is.LessThanOrEqualTo(log.ClientSize.Width + .1));
                TextBlock caption = option.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == option.Content as string);
                Assert.That(caption.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False);
                Assert.That(caption.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(caption.Bounds.Width + .5));
            }
            if (options.Sum(option => option.DesiredSize.Width) + footer.ItemSpacing * 2 > footer.Bounds.Width)
            { Assert.That(options.Select(option => option.Bounds.Top).Distinct().Count(), Is.GreaterThan(1), "The footer must reflow rather than clip an option at 400 pixels."); }
            log.Width = 659; Pump(log);
            Add("status"); Add("log"); await Until(() => Field<ListBox>(log, "LogItems").Items.Count == 2); Pump(log);
            ListBox list = Field<ListBox>(log, "LogItems"); object? source = list.ItemsSource; object first = list.Items[0]!, second = list.Items[1]!;
            PushProcessTestDriver.Pointer((ListBoxItem)list.ContainerFromIndex(0)!); string? detail = Field<TextBox>(log, "GitCommandLogText").Text;
            PushProcessTestDriver.Pointer(choice); await log.Work; Pump(log);
            Assert.That(choice.IsChecked, Is.True); Assert.That(GitCommandLog.CapturePolicy.CaptureCallStacks, Is.True);
            Assert.That((await GitXmlSettingsStore.LoadAsync(DetailedRepositorySettings.GlobalPath)).Values[CommandLogCapturePreferences.Key], Is.EqualTo("true"));
            Assert.That(list.ItemsSource, Is.SameAs(source)); Assert.That(list.SelectedItem, Is.SameAs(first)); Assert.That(Field<TextBox>(log, "GitCommandLogText").Text, Is.EqualTo(detail));
            Assert.That(GitCommandLog.Snapshot().All(entry => entry.CallStack is null), Is.True, "Toggling never rewrites existing entries.");
            PushProcessTestDriver.Pointer((ListBoxItem)list.ContainerFromIndex(0)!);
            await Task.Run(() => Add("diff")); await Until(() => list.Items.Count == 3); Pump(log);
            log.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); log.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); Pump(log);
            Assert.That(list.SelectedItem, Is.SameAs(second)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
            await Close(log); log = await Open(language, dark); choice = Field<CheckBox>(log, "chkCaptureCallStacks"); Assert.That(choice.IsChecked, Is.True);
            PushProcessTestDriver.Pointer(choice); await log.Work; Pump(log); Assert.That(GitCommandLog.CapturePolicy.CaptureCallStacks, Is.False);
            Assert.That(File.ReadAllBytes(f.Desktop.FilePath), Is.EqualTo(json));
        }
        finally { if (log is not null) { await Close(log); } GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(policy); }
    }

    [AvaloniaTest]
    [TestCase("<dictionary><broken>")] [TestCase("<dictionary><item><key><string>Log.CaptureCallStacks</string></key><value><string>secret-invalid-bool</string></value></item></dictionary>")]
    public async Task AnUnreadableAuthorityDisablesTheCheckboxWithAVisibleDiagnosticAndNeverRepairsIt(string contents)
    {
        using XmlScope f = new(); string path = DetailedRepositorySettings.GlobalPath; Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, contents);
        byte[] bytes = File.ReadAllBytes(path); GitCommandLogWindow log = await Open();
        try
        {
            Assert.That(Field<CheckBox>(log, "chkCaptureCallStacks").IsChecked, Is.False); Assert.That(Field<CheckBox>(log, "chkCaptureCallStacks").IsEnabled, Is.False);
            Assert.That(Field<TextBlock>(log, "CommandLogCaptureNotice").IsVisible, Is.True);
            Assert.That(Field<TextBlock>(log, "CommandLogCaptureNotice").Text, Is.Not.Empty);
            Assert.That(GitCommandLog.CapturePolicy.CaptureCallStacks, Is.False); Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        }
        finally { await Close(log); }
    }

    [AvaloniaTest]
    public async Task AStaleOrIoFailedToggleKeepsItsAcceptedChoiceAndRequiresAnExplicitReopen()
    {
        using XmlScope f = new(); CommandLogCapturePreferences store = new();
        GitCommandLogWindow log = await Open();
        try
        {
            GitXmlSettingsSnapshot peer = await GitXmlSettingsStore.LoadAsync(store.FilePath);
            await GitXmlSettingsStore.SaveChangesAsync(peer, new Dictionary<string, string?> { ["peer.Ω"] = "keep" });
            byte[] bytes = File.ReadAllBytes(store.FilePath); CheckBox choice = Field<CheckBox>(log, "chkCaptureCallStacks");
            PushProcessTestDriver.Pointer(choice); await log.Work; Pump(log);
            Assert.That(choice.IsChecked, Is.False); Assert.That(choice.IsEnabled, Is.False); Assert.That(GitCommandLog.CapturePolicy.CaptureCallStacks, Is.False);
            Assert.That(Field<TextBlock>(log, "CommandLogCaptureNotice").IsVisible, Is.True); Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(bytes));
            await Close(log); log = await Open(); choice = Field<CheckBox>(log, "chkCaptureCallStacks"); Assert.That(choice.IsEnabled, Is.True);
            using (FileStream exclusive = new(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            { PushProcessTestDriver.Pointer(choice); await log.Work; Pump(log); Assert.That(choice.IsChecked, Is.False); Assert.That(choice.IsEnabled, Is.False); }
            Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(bytes)); Assert.That(GitCommandLog.CapturePolicy.CaptureCallStacks, Is.False);
        }
        finally { await Close(log); }
    }

    [AvaloniaTest]
    public async Task NotificationsDuringTheOwnedInitialReadKeepFollowLastFocusAndTheFirstArrow()
    {
        using XmlScope f = new(); Add("status"); Add("log"); Add("show");
        SemaphoreSlim gate = (SemaphoreSlim)typeof(CommandLogCapturePreferences).GetField("PublicationGate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        await gate.WaitAsync(); GitCommandLogWindow log = new(TranslationCatalog.Load("", CultureInfo.GetCultureInfo("en")));
        bool held = true;
        try
        {
            log.Show(); Pump(log); Assert.That(log.IsBusy, Is.True);
            ListBox list = Field<ListBox>(log, "LogItems"); object? source = list.ItemsSource;
            PushProcessTestDriver.Pointer((ListBoxItem)list.ContainerFromIndex(2)!);
            await Task.Run(() => { for (int count = 0; count < 40; count++) { Add("diff"); } });
            await Until(() => list.Items.Count == 43); Pump(log);
            Assert.That(list.ItemsSource, Is.SameAs(source)); Assert.That(list.SelectedIndex, Is.EqualTo(42)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
            object previous = list.Items[41]!;
            log.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None); log.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None); Pump(log);
            Assert.That(list.SelectedItem, Is.SameAs(previous)); Assert.That(list.IsKeyboardFocusWithin, Is.True);
            gate.Release(); held = false; await log.Work; Pump(log); Assert.That(list.IsKeyboardFocusWithin, Is.True); Assert.That(list.SelectedItem, Is.SameAs(previous));
        }
        finally { if (held) { gate.Release(); } await Close(log); }
    }

    [AvaloniaTest]
    public async Task ClosingBeforeAcquisitionCancelsButAnAlreadyPublishedWriteSurvivesCloseAndReopen()
    {
        using XmlScope f = new(); GitCommandLogWindow log = await Open();
        SemaphoreSlim gate = (SemaphoreSlim)typeof(CommandLogCapturePreferences).GetField("PublicationGate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        await gate.WaitAsync();
        try
        { PushProcessTestDriver.Pointer(Field<CheckBox>(log, "chkCaptureCallStacks")); log.Close(); }
        finally { gate.Release(); }
        await log.Work; Assert.That(File.Exists(DetailedRepositorySettings.GlobalPath), Is.False); Assert.That(GitCommandLog.CapturePolicy.CaptureCallStacks, Is.False);
        log = await Open();
        try
        {
            PushProcessTestDriver.Pointer(Field<CheckBox>(log, "chkCaptureCallStacks")); Task work = log.Work;
            // Hold the actual UI turn until durable IO + runtime publication finish on
            // the worker; its UI continuation cannot acknowledge the closed control.
            Assert.That(Task.Run(async () =>
            { for (int count = 0; count < 1000 && !GitCommandLog.CapturePolicy.CaptureCallStacks; count++) { await Task.Delay(5); } return GitCommandLog.CapturePolicy.CaptureCallStacks; })
                .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(), Is.True);
            log.Close(); await work; Assert.That(GitCommandLog.CapturePolicy.CaptureCallStacks, Is.True);
            Assert.That((await GitXmlSettingsStore.LoadAsync(DetailedRepositorySettings.GlobalPath)).Values[CommandLogCapturePreferences.Key], Is.EqualTo("true"));
            log = await Open(); Assert.That(Field<CheckBox>(log, "chkCaptureCallStacks").IsChecked, Is.True);
        }
        finally { await Close(log); }
    }

    [AvaloniaTest]
    public async Task TheExitDrainWaitsForTheRealXmlWriterAndItsRuntimePublicationWithoutDispatchingUiWork()
    {
        using XmlScope f = new(); CommandLogCapturePreferences store = new(); CommandLogCaptureState initial = await store.LoadAsync();
        ConcurrentDictionary<string, SemaphoreSlim> gates = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(GitXmlSettingsStore)
            .GetField("PathLocks", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        SemaphoreSlim ioGate = gates[store.FilePath]; await ioGate.WaitAsync();
        Task<CommandLogCaptureState>? write = null; Task? drain = null; bool held = true;
        try
        {
            write = store.SetAsync(initial, true); drain = CommandLogCapturePreferences.DrainAsync();
            Assert.That(write.IsCompleted, Is.False); Assert.That(drain.IsCompleted, Is.False); Assert.That(File.Exists(store.FilePath), Is.False);
            ioGate.Release(); held = false;
            // Keep this actual UI turn occupied: neither the raw writer/publication nor
            // Program's same drain may require a Dispatcher continuation to complete.
            drain.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            CommandLogCaptureState accepted = await write;
            Assert.That(accepted.Enabled, Is.True); Assert.That(GitCommandLog.CapturePolicy.CaptureCallStacks, Is.True);
            Assert.That((await GitXmlSettingsStore.LoadAsync(store.FilePath)).Values[CommandLogCapturePreferences.Key], Is.EqualTo("true"));
        }
        finally { if (held) { ioGate.Release(); } if (write is not null) { await write; } if (drain is not null) { await drain; } }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task RealSettingsGeneralAndAdvancedDraftsCannotOverwriteCaptureAndStaleXmlFailsWithoutRebase(bool xmlIntention)
    {
        GitCommandLogCapturePolicy policy = GitCommandLog.CapturePolicy;
        await using DetailedSettingsUiHost host = await DetailedSettingsUiHost.CreateAsync(); GitCommandLogWindow? log = null;
        try
        {
            SettingsNavigationTestDriver.Select(host.Dialog, "General"); host.Click("chkCheckForUncommittedChangesInCheckoutBranch");
            bool general = host.Field<CheckBox>("chkCheckForUncommittedChangesInCheckoutBranch").IsChecked == true;
            SettingsNavigationTestDriver.Select(host.Dialog, "Advanced"); host.Click("SettingsAdvancedOptions");
            bool advanced = host.Field<CheckBox>("SettingsAdvancedOptions").IsChecked == true;
            if (xmlIntention) { host.Session.Set(GitDistributedSettingsScope.Global, DetailedRepositorySettings.MergeLogCount, "41"); }
            log = await Open(owner: host.Dialog); PushProcessTestDriver.Pointer(Field<CheckBox>(log, "chkCaptureCallStacks")); await log.Work; await Close(log); log = null;
            byte[] accepted = File.ReadAllBytes(DetailedRepositorySettings.GlobalPath);
            host.Click("ApplyDesktopSettings"); await DetailedSettingsUiHost.Until(() => host.Publications == 1 && host.Dialog.Navigation.IsEnabled && host.Field<Button>("ApplyDesktopSettings").IsEnabled); Pump(host.Dialog);
            Assert.That(host.Profile.Desktop.Load().CheckForUncommittedChangesInCheckoutBranch, Is.EqualTo(general));
            Assert.That(host.Profile.Desktop.Load().AlwaysShowAdvancedOptions, Is.EqualTo(advanced));
            Assert.That(File.ReadAllBytes(DetailedRepositorySettings.GlobalPath), Is.EqualTo(accepted)); Assert.That(GitCommandLog.CapturePolicy.CaptureCallStacks, Is.True);
            if (xmlIntention)
            {
                Assert.That(host.Field<TextBlock>("SettingsNotice").Text, Is.Not.Empty); Assert.That(host.Session.Read(GitDistributedSettingsScope.Global).LogCount, Is.EqualTo("41"));
                DetailedSettingsSession reopened = new(host.Profile.Repository, host.Profile.Desktop.Load()); await reopened.InitializeAsync(default);
                reopened.Set(GitDistributedSettingsScope.Global, DetailedRepositorySettings.MergeLogCount, "41"); await reopened.SaveFilesAsync(default);
                Assert.That((await GitXmlSettingsStore.LoadAsync(DetailedRepositorySettings.GlobalPath)).Values[CommandLogCapturePreferences.Key], Is.EqualTo("true"));
            }
            else { Assert.That(host.Field<TextBlock>("SettingsNotice").Text, Is.Null.Or.Empty); }
            host.Click("CancelDesktopSettings"); await host.Completion;
            Assert.That((await new CommandLogCapturePreferences().LoadAsync()).Enabled, Is.True);
            Assert.That(File.ReadAllText(host.Profile.Desktop.FilePath), Does.Not.Contain("CaptureCallStacks"));
        }
        finally { if (log is not null) { await Close(log); } GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(policy); }
    }

    [AvaloniaTest]
    public async Task TheProgramCallbackUsesTheActualDispatcherAndSafeLaunchSnapshotsReachDetailAndExplicitExports()
    {
        GitCommandLogCapturePolicy policy = GitCommandLog.CapturePolicy;
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        try
        {
            CommandLogCaptureState initial = await CommandLogCapturePreferences.InitializeRuntimeAsync(); Assert.That(GitCommandLog.CapturePolicy.UiThreadProbe, Is.Null);
            AppBuilder builder = Program.BuildAvaloniaApp(); builder.AfterSetupCallback(builder); // Existing real headless application/Dispatcher, no second lifetime.
            CommandLogCapturePreferences store = new(); CommandLogCaptureState enabled = await store.SetAsync(initial, true);
            Assert.That(GitCommandLog.CapturePolicy.ApprovedHostAssembly, Is.SameAs(typeof(Program).Assembly));
            GitCommandLog.Clear(); GitRunner runner = new(); GitRepository repository = f.Repository!;
            await runner.RunAsync(repository.Path, ["-c", "http.extraHeader=argument-capture-secret", "status", "--porcelain"], standardInput: "stdin-capture-secret", environment: new Dictionary<string, string?> { ["CAPTURE_TEST"] = "environment-capture-secret" });
            await Task.Run(async () =>
            {
                SynchronizationContext? before = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new());
                try { await runner.RunAsync(repository.Path, ["status", "--porcelain"]); }
                finally { SynchronizationContext.SetSynchronizationContext(before); }
            });
            GitCommandLogEntry[] rows = GitCommandLog.Snapshot().Where(entry => entry.Command == "git status").ToArray();
            Assert.That(rows.Length, Is.EqualTo(2)); Assert.That(rows[0].IsOnMainThread, Is.True); Assert.That(rows[1].IsOnMainThread, Is.False);
            Assert.That(rows.All(entry => entry.CallStack is { Length: > 0 } stack && stack.Length <= GitCommandLogCapturePolicy.MaxCallStackLength), Is.True);
            TranslationCatalog en = TranslationCatalog.Load("", CultureInfo.GetCultureInfo("en")); string detail = GitCommandLogPresentation.Detail(rows[0], en);
            Assert.That(GitCommandLogPresentation.Line(rows[0], en), Does.Contain(" UI ")); Assert.That(GitCommandLogPresentation.Line(rows[1], en), Does.Not.Contain(" UI "));
            Assert.That(detail, Does.Contain("UI Thread?:  True").And.Contain(rows[0].CallStack!));
            string txt = GitCommandLogPresentation.Export(rows, false, CultureInfo.InvariantCulture), csv = GitCommandLogPresentation.Export(rows, true, CultureInfo.GetCultureInfo("fr-FR"));
            Assert.That(txt.Count(character => character == '\n'), Is.EqualTo(2), "Multiline stacks remain one physical TXT row per launch.");
            Assert.That(csv, Does.Contain(";true;").And.Contain(";false;").And.Contain(rows[0].CallStack!.Split('\n')[0]));
            string all = string.Join('\n', GitCommandLog.Snapshot().Select(entry => entry.ToString())) + detail + txt + csv;
            foreach (string secret in new[] { "argument-capture-secret", "stdin-capture-secret", "environment-capture-secret", "CommandLogCaptureTests.cs", "http.extraHeader" })
            { Assert.That(all, Does.Not.Contain(secret)); }
            string? oldStack = rows[0].CallStack; await store.SetAsync(enabled, false); await runner.RunAsync(repository.Path, ["status", "--porcelain"]);
            GitCommandLogEntry last = GitCommandLog.Snapshot().Last(entry => entry.Command == "git status"); Assert.That(last.CallStack, Is.Null); Assert.That(last.IsOnMainThread, Is.True);
            Assert.That(rows[0].CallStack, Is.SameAs(oldStack)); Assert.That(GitCommandLogPresentation.Detail(last, en), Does.Contain("not captured"));
            Assert.That(GitCommandLogPresentation.Detail(last with { IsOnMainThread = null }, en), Does.Contain("UI Thread?:  unknown"));
        }
        finally { GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(policy); }
    }

    [TestCase("en-US", ",")] [TestCase("fr-FR", ";")]
    public void CsvQuotesTheSafeMultilineStackAndPreservesAllEightExistingFields(string culture, string separator)
    {
        GitCommandLogEntry row = new("git status", "Ω,;\"dir\"\r\nnext", DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(2), 0, false)
            { Id = 1, IsOnMainThread = true, CallStack = "at Approved.Type.λ\n[truncated]" };
        string csv = GitCommandLogPresentation.Export([row], true, CultureInfo.GetCultureInfo(culture));
        string[] fields = ["1970-01-01T00:00:00.0000000+00:00", "2", "", "0", "false", "false", "git status", "\"Ω,;\"\"dir\"\"\r\nnext\""];
        string prefix = string.Join(separator, fields);
        Assert.That(csv, Is.EqualTo(prefix + separator + "true" + separator + "\"at Approved.Type.λ\n[truncated]\"\n"));
        string txt = GitCommandLogPresentation.Export([row], false, CultureInfo.InvariantCulture);
        Assert.That(txt, Does.EndWith("\ttrue\tat Approved.Type.λ\\n[truncated]\n")); Assert.That(txt.Count(character => character == '\n'), Is.EqualTo(1));
    }

    private static async Task<GitCommandLogWindow> Open(string language = "en", bool dark = false, Window? owner = null)
    {
        GitCommandLogWindow log = new(TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language)))
            { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try { if (owner is null) { log.Show(); } else { log.Show(owner); } await Until(() => !log.IsBusy); Pump(log); return log; }
        catch { log.Close(); await log.Work; throw; }
    }
    private static async Task Close(GitCommandLogWindow log) { if (log.IsVisible) { log.Close(); } await log.Work; Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
    private static void Add(string verb)
    {
        long id = (long)typeof(GitCommandLog).GetMethod("Begin", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { new[] { verb }, "/literal-journal-directory", DateTimeOffset.UnixEpoch })!;
        typeof(GitCommandLog).GetMethod("Complete", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { id, TimeSpan.FromMilliseconds(2), 0, false });
    }
    private static T Field<T>(Window root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(TopLevel root) => DetailedSettingsUiHost.Pump(root);
    private static Task Until(Func<bool> condition) => DetailedSettingsUiHost.Until(condition);
    private sealed class XmlScope : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly GitCommandLogCapturePolicy _policy = GitCommandLog.CapturePolicy;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "command-log-capture-" + Guid.NewGuid().ToString("N"));
        internal XmlScope() { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Root); GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(new(false)); }
        public void Dispose() { GitCommandLog.Clear(); GitCommandLog.ConfigureCapture(_policy); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous); if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }
}

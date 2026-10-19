using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ConsoleStyleSettingsTests
{
    [Test]
    public void OpeningOrSavingAnUnchangedDraftDoesNotCreateAProfile()
    {
        using Scope f = new(); ConsoleStyleSettingsSession session = new(f.Store);
        Assert.That(session.Initial, Is.EqualTo(new ConsoleStylePreferences()));
        Assert.That(session.Save(session.Initial), Is.EqualTo(session.Initial));
        Assert.That(File.Exists(f.Store.FilePath), Is.False);
    }

    [Test]
    public void NoOpPreservesUnknownBytesAndTimestampAndChangedIntentsMergeWithAPeer()
    {
        using Scope f = new(); Directory.CreateDirectory(Path.GetDirectoryName(f.Store.FilePath)!);
        string raw = "{ \"Style\":\"Default\", \"Future\": {\"keep\":[1,2,3]} }\n"; File.WriteAllText(f.Store.FilePath, raw);
        DateTime stamp = File.GetLastWriteTimeUtc(f.Store.FilePath); ConsoleStylePreferences initial = f.Store.Load();
        f.Store.SaveChanges(initial, initial); Assert.That(File.ReadAllText(f.Store.FilePath), Is.EqualTo(raw)); Assert.That(File.GetLastWriteTimeUtc(f.Store.FilePath), Is.EqualTo(stamp));
        f.Store.SaveChanges(initial, initial with { Terminal = "xterm" });
        ConsoleStylePreferences accepted = f.Store.SaveChanges(initial, initial with { Style = "Dark" });
        Assert.That(accepted.Terminal, Is.EqualTo("xterm")); Assert.That(accepted.Style, Is.EqualTo("Dark"));
        using JsonDocument stored = JsonDocument.Parse(File.ReadAllText(f.Store.FilePath)); Assert.That(stored.RootElement.GetProperty("Future").GetProperty("keep").GetArrayLength(), Is.EqualTo(3));
    }

    [TestCase("{broken")]
    [TestCase("{\"Style\":\"Dark\",\"Style\":\"Light\"}")]
    [TestCase("{\"Font\":{\"Family\":\"DejaVu Sans Mono\",\"SizeInPoints\":0}}")]
    public void InvalidProfilesAreReportedAndNeverRepairedByAnEdit(string raw)
    {
        using Scope f = new(); Directory.CreateDirectory(Path.GetDirectoryName(f.Store.FilePath)!); File.WriteAllText(f.Store.FilePath, raw);
        ConsoleStyleSettingsSession session = new(f.Store); Assert.That(session.LoadError, Is.Not.Null);
        Assert.Throws<InvalidOperationException>(() => session.Save(session.Initial with { Style = "Dark" }));
        Assert.That(File.ReadAllText(f.Store.FilePath), Is.EqualTo(raw));
    }

    [Test]
    public void UnknownIdentitiesSurviveAnUnrelatedEditAndFontResetUsesTheSharedRoleWithoutRewritingFonts()
    {
        using Scope f = new(); Directory.CreateDirectory(Path.GetDirectoryName(f.Store.FilePath)!);
        File.WriteAllText(f.Store.FilePath, "{\"Terminal\":\"future-terminal\",\"Font\":{\"Family\":\"Unavailable literal family\",\"SizeInPoints\":13.375,\"FutureFace\":true}}");
        ConsoleStylePreferences before = f.Store.Load();
        ConsoleStylePreferences saved = f.Store.SaveChanges(before, before with { Style = "Dark" });
        Assert.That(saved.Terminal, Is.EqualTo("future-terminal")); Assert.That(saved.Font, Is.EqualTo(before.Font));
        f.Store.SaveChanges(saved, saved with { Font = null }); Assert.That(f.Store.Load().Font, Is.Null);
        Assert.That(File.Exists(new FontPreferencesStore().FilePath), Is.False);
    }

    [TestCase("gnome-terminal", "--wait", "--")]
    [TestCase("konsole", "--nofork", "-e")]
    [TestCase("xterm", "-e", null)]
    [TestCase("x-terminal-emulator", "-e", null)]
    public void RealTerminalPreferenceBuildsLiteralArgvWithoutStartingAProcess(string name, string first, string? second)
    {
        using Scope f = new(); string bin = f.Path("bin"); Directory.CreateDirectory(bin); File.WriteAllText(Path.Combine(bin, name), "fixture only, never executed");
        Environment.SetEnvironmentVariable("PATH", bin);
        new ConsoleStyleSettingsSession(f.Store).Save(new(Terminal: name));
        GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new() { Executable = "/usr/bin/git" });
        var info = ConsoleStyleTerminal.CreateStartInfo("helper λ", ["--interactive", "a [x]* λ", "line\nbreak"], environment, waitForExit: true);
        List<string> expected = [first]; if (second is not null) { expected.Add(second); } expected.AddRange(["helper λ", "--interactive", "a [x]* λ", "line\nbreak"]);
        Assert.That(info.FileName, Is.EqualTo(Path.Combine(bin, name))); Assert.That(info.ArgumentList, Is.EqualTo(expected)); Assert.That(info.UseShellExecute, Is.False);
    }

    [Test]
    public void AnUnavailableExplicitTerminalCannotSilentlyBecomeAnotherInstalledTerminal()
    {
        using Scope f = new(); string bin = f.Path("bin"); Directory.CreateDirectory(bin); File.WriteAllText(Path.Combine(bin, "xterm"), "not executed"); Environment.SetEnvironmentVariable("PATH", bin);
        GitExecutionEnvironment environment = GitExecutionEnvironment.Capture(new() { Executable = "/usr/bin/git" });
        Assert.Throws<FileNotFoundException>(() => ConsoleStyleTerminal.CreateStartInfo("helper", [], environment, true, preferences: new(Terminal: "konsole")));
        var info = ConsoleStyleTerminal.CreateStartInfo("helper", [], environment, true, preferences: new()); Assert.That(Path.GetFileName(info.FileName), Is.EqualTo("xterm"));
    }

    [AvaloniaTest]
    public void MissingTerminalsAreExplainedWithoutOfferingUnavailableEmulatorsOrWritingTheProfile()
    {
        using Scope f = new(); Environment.SetEnvironmentVariable("PATH", "");
        ConsoleStyleSettingsPage page = new(new(f.Store), TranslationCatalog.LoadDefault()); Window host = new() { Content = page, Width = 640, Height = 430 };
        try
        {
            host.Show(); Pump(host); ComboBox terminal = Field<ComboBox>(host, "cboConsoleEmulator");
            Assert.That(terminal.Items.OfType<ComboBoxItem>().Count(item => item.IsEnabled), Is.EqualTo(1));
            Assert.That((terminal.SelectedItem as ComboBoxItem)?.Tag, Is.EqualTo("Automatic"));
            Assert.That(Field<TextBlock>(host, "ConsoleStyleNotice").Text, Does.Contain("Install"));
            Assert.That(File.Exists(f.Store.FilePath), Is.False);
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public void SavedStyleAndFontAffectOnlyTheCapturedProfileWithoutChangingTextSelectionCaretOrFocus()
    {
        using Scope f = new(); ConsoleStylePreferencesStore other = new(f.Path("other"));
        TextBox output = new() { Text = "literal output λ\nkeep this selection", AcceptsReturn = true, IsReadOnly = true }, peer = new() { Text = "other profile", IsReadOnly = true };
        ConsoleStylePresentation.Bind(output, f.Store); ConsoleStylePresentation.Bind(peer, other);
        Window one = new() { Content = output }, two = new() { Content = peer };
        try
        {
            one.Show(); two.Show(); Pump(one); Pump(two); output.Focus(); output.SelectionStart = 2; output.SelectionEnd = 9; output.CaretIndex = 9;
            string? text = output.Text; string? selected = output.SelectedText; int caret = output.CaretIndex; IBrush? peerBackground = peer.Background;
            FontSpec font = new("DejaVu Sans Mono", 13.375, true, false);
            new ConsoleStyleSettingsSession(f.Store).Save(new(Style: "Dark", Font: font)); Pump(one); Pump(two);
            Assert.That(output.Text, Is.EqualTo(text)); Assert.That(output.SelectedText, Is.EqualTo(selected)); Assert.That(output.CaretIndex, Is.EqualTo(caret)); Assert.That(output.IsFocused, Is.True);
            Assert.That(output.FontSize, Is.EqualTo(font.SizeInDips)); Assert.That((output.Background as ISolidColorBrush)?.Color, Is.EqualTo(Color.Parse("#0c0c0c"))); Assert.That(peer.Background, Is.SameAs(peerBackground));
            one.Content = null; IBrush? detached = output.Background;
            new ConsoleStyleSettingsSession(f.Store).Save(f.Store.Load() with { Style = "Light" }); Pump(one); Assert.That(output.Background, Is.SameAs(detached));
            one.Content = output; Pump(one); Assert.That(output.Background, Is.EqualTo(Brushes.White)); Assert.That(output.Text, Is.EqualTo(text));
        }
        finally { one.Close(); two.Close(); }
    }

    [AvaloniaTest]
    public void PublicationQueuedForAnOldAttachmentCannotReplaceTheNewlyReadSavedProfile()
    {
        using Scope f = new(); TextBox output = new() { Text = "stable" }; ConsoleStylePresentation.Bind(output, f.Store); Window host = new() { Content = output };
        try
        {
            host.Show(); Pump(host);
            Task.Run(() => ConsoleStylePresentation.PublishSaved(f.Store.FilePath, new(Style: "Dark"))).GetAwaiter().GetResult();
            host.Content = null; ConsoleStylePreferences initial = f.Store.Load(); f.Store.SaveChanges(initial, initial with { Style = "Light" }); host.Content = output;
            Pump(host); Assert.That(output.Background, Is.EqualTo(Brushes.White)); Assert.That(output.Text, Is.EqualTo("stable"));
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    [TestCase("en-US", false)]
    [TestCase("fr-FR", true)]
    public void ActualPageDraftPreviewAndCancelAreSeparateFromSavedOutputAndApplyUpdatesAnOpenConsumer(string language, bool dark)
    {
        using Scope f = new(); TranslationCatalog translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
        ConsoleStyleSettingsSession session = new(f.Store); ConsoleStyleSettingsPage page = new(session, translations);
        Window settings = new() { Width = 640, Height = 430, Content = page, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        TextBox output = new() { Text = "real consumer state", IsReadOnly = true }; ConsoleStylePresentation.Bind(output, f.Store); Window consumer = new() { Content = output };
        try
        {
            consumer.Show(); settings.Show(); Pump(settings); IBrush? before = output.Background;
            ComboBox style = Field<ComboBox>(settings, "_NO_TRANSLATE_cboStyle"); style.SelectedItem = style.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag! == "Dark"); Pump(settings);
            Assert.That(page.Draft.Style, Is.EqualTo("Dark")); Assert.That(File.Exists(f.Store.FilePath), Is.False); Assert.That(output.Background, Is.SameAs(before));
            Capture(settings, $"console-style-{language}-{(dark ? "Dark" : "Light")}.png");
            Assert.That(page.Validate(out _), Is.True); page.AcceptSaved(session.Save(page.Draft)); Pump(consumer); Assert.That(f.Store.Load().Style, Is.EqualTo("Dark"));
            style.SelectedItem = style.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag! == "Light"); settings.Close();
            Assert.That(f.Store.Load().Style, Is.EqualTo("Dark")); Assert.That((output.Background as ISolidColorBrush)?.Color, Is.EqualTo(Color.Parse("#0c0c0c")));
        }
        finally { if (settings.IsVisible) { settings.Close(); } consumer.Close(); }
    }

    [AvaloniaTest]
    public async Task ExistingProcessOutputUsesTheSavedPresentationWhileTheCommandIsRunning()
    {
        using Scope f = new(); TaskCompletionSource<GitResult> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GitProcessDialog process = new(f.Path("repository"), "git status", true, (_, _) => done.Task);
        try
        {
            process.Show(); Pump(process); TextBox output = Field<TextBox>(process, "ProcessOutput");
            Assert.That(output.Text, Does.Contain("git status")); output.Focus(); output.SelectionStart = 0; output.SelectionEnd = 3;
            string? text = output.Text, selected = output.SelectedText;
            new ConsoleStyleSettingsSession(f.Store).Save(new(Style: "Dark", Font: new("DejaVu Sans Mono", 12.375)));
            Pump(process); Assert.That(process.IsRunning, Is.True);
            Assert.That((output.Background as ISolidColorBrush)?.Color, Is.EqualTo(Color.Parse("#0c0c0c")));
            Assert.That(output.FontSize, Is.EqualTo(12.375 * (96d / 72d)));
            Assert.That(output.Text, Is.EqualTo(text)); Assert.That(output.SelectedText, Is.EqualTo(selected)); Assert.That(output.IsFocused, Is.True);
            Capture(process, "console-style-process-output-Dark.png");
        }
        finally
        {
            done.TrySetResult(new(0, "completed\n", ""));
            DateTime end = DateTime.UtcNow.AddSeconds(10);
            while (process.IsRunning && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            Assert.That(process.IsRunning, Is.False); if (process.IsVisible) { process.Close(); } Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public void ConsoleStyleDefaultToDarkSurvivesDetachAttach(bool dark)
    {
        using Scope f = new(); ConsoleStyleSettingsSession session = new(f.Store);
        ConsoleStyleSettingsPage page = new(session, TranslationCatalog.LoadDefault());
        ContentControl pageHost = new() { Content = page, Height = 400 };
        TextBox output = new() { Text = "actual saved output\nselection remains available", IsReadOnly = true, AcceptsReturn = true, Height = 100 };
        ConsoleStylePresentation.Bind(output, f.Store);
        ContentControl outputHost = new() { Content = output };
        Button elsewhere = new() { Content = "Focus elsewhere" };
        Window host = new() { Width = 800, Height = 630, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
            Content = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children = { pageHost, outputHost, elsewhere } } };
        try
        {
            host.Show(); Pump(host);
            TextBox preview = Field<TextBox>(host, "ConsoleStylePreview");
            ComboBox style = Field<ComboBox>(host, "_NO_TRANSLATE_cboStyle");
            foreach (string choice in new[] { "Dark", "Light", "Default" })
            {
                style.SelectedItem = style.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag! == choice); Pump(host);
                Color background = Color.Parse(choice == "Dark" ? "#0c0c0c" : choice == "Light" ? "#ffffff" : ClassicStyles.OriginalColors(dark)["EditorBackground"]);
                Color foreground = Color.Parse(choice == "Dark" ? "#cccccc" : choice == "Light" ? "#000000" : ClassicStyles.OriginalColors(dark)["WindowText"]);
                AssertStates(preview, background, foreground);
                // Settings navigation removes and reattaches the SAME page without reapplying its draft.
                pageHost.Content = new TextBlock { Text = "A different Settings page" }; Pump(host);
                pageHost.Content = page; Pump(host); AssertStates(preview, background, foreground);
                page.AcceptSaved(session.Save(page.Draft)); Pump(host);
                AssertStates(output, background, foreground);
                outputHost.Content = null; Pump(host); outputHost.Content = output; Pump(host);
                AssertStates(output, background, foreground);
            }
            host.RequestedThemeVariant = dark ? ThemeVariant.Light : ThemeVariant.Dark; Pump(host);
            AssertStates(preview, Color.Parse(ClassicStyles.OriginalColors(!dark)["EditorBackground"]), Color.Parse(ClassicStyles.OriginalColors(!dark)["WindowText"]));
            AssertStates(output, Color.Parse(ClassicStyles.OriginalColors(!dark)["EditorBackground"]), Color.Parse(ClassicStyles.OriginalColors(!dark)["WindowText"]));
        }
        finally { host.Close(); }

        void AssertStates(TextBox box, Color background, Color foreground)
        {
            string? text = box.Text;
            elsewhere.Focus(); host.MouseMove(new Point(host.Bounds.Width - 3, host.Bounds.Height - 3)); Pump(host); AssertPainted();
            Point inside = box.TranslatePoint(new Point(12, 12), host)!.Value;
            host.MouseMove(inside); Pump(host); Assert.That(box.IsPointerOver, Is.True); AssertPainted();
            box.Focus(); box.CaretIndex = 8; box.SelectionStart = 2; box.SelectionEnd = 8; Pump(host);
            Assert.That(box.IsFocused, Is.True); AssertPainted();
            host.MouseMove(new Point(host.Bounds.Width - 3, host.Bounds.Height - 3)); Pump(host); AssertPainted();
            Assert.That(box.Text, Is.EqualTo(text)); Assert.That(box.SelectedText, Is.EqualTo(text![2..8])); Assert.That(box.CaretIndex, Is.EqualTo(8));

            void AssertPainted()
            {
                Border border = box.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_BorderElement");
                TextPresenter presenter = box.GetVisualDescendants().OfType<TextPresenter>().Single(control => control.Name == "PART_TextPresenter");
                Assert.That((border.Background as ISolidColorBrush)?.Color, Is.EqualTo(background), "The rendered template border must follow the console palette in every state.");
                Assert.That((presenter.Foreground as ISolidColorBrush)?.Color, Is.EqualTo(foreground));
                using WriteableBitmap? frame = host.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
                Point sample = border.TranslatePoint(new Point(border.Bounds.Width / 2, border.Bounds.Height - 8), host)!.Value;
                using ILockedFramebuffer pixels = frame!.Lock(); byte[] pixel = new byte[4];
                int x = (int)(sample.X * host.RenderScaling), y = (int)(sample.Y * host.RenderScaling);
                Marshal.Copy(pixels.Address + y * pixels.RowBytes + x * 4, pixel, 0, pixel.Length);
                Color painted = pixels.Format == PixelFormat.Bgra8888 ? Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]) : Color.FromArgb(pixel[3], pixel[0], pixel[1], pixel[2]);
                Assert.That(painted, Is.EqualTo(background), "Check the painted surface, not just TextBox.Background.");
            }
        }
    }

    private static T Field<T>(Window host, string name) where T : Control => host.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(TopLevel window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private sealed class Scope : IDisposable
    {
        private readonly string? _profile = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), _path = Environment.GetEnvironmentVariable("PATH");
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-console-style-" + Guid.NewGuid().ToString("N"));
        internal ConsoleStylePreferencesStore Store { get; }
        internal Scope() { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root); Store = new(); }
        internal string Path(string name) => System.IO.Path.Combine(_root, name);
        public void Dispose()
        { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _profile); Environment.SetEnvironmentVariable("PATH", _path); if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
    }
}

using System.Globalization;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using DesktopProgram = GitExtensions.Linux.Desktop.Program;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class CommandlineHelpDialogTests
{
    private const string ExistingUsage = "Usage: gitextensions [repository-directory] | browse [repository-directory] [--pathFilter=path] [-commit=selected[,first-selected]] | cleanup [repository-directory-or-file] | --version | --smoke-test [repository-directory]";

    [Test]
    public void ExplicitHelpUsesOnlyTheGuiLauncherAndPropagatesItsActualExitWithoutTouchingProfilesOrTheCwd()
    {
        using Scope f = new(); f.WriteMalformedProfiles(); string before = f.Materials(); int calls = 0;
        GitEnvironmentOptions environment = GitEnvironment.Options; DesktopStartupRequest request = DesktopProgram.StartupRequest;
        string? repository = DesktopProgram.RepositoryPath; bool smoke = DesktopProgram.SmokeTest;
        TextWriter oldOut = Console.Out, oldError = Console.Error; using StringWriter output = new(), error = new();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            foreach (int actualExit in new[] { 0, 7 })
            { Assert.That(DesktopProgram.Run(["help"], () => { calls++; return actualExit; }), Is.EqualTo(actualExit)); }
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        Assert.That(calls, Is.EqualTo(2)); Assert.That(output.ToString(), Is.Empty); Assert.That(error.ToString(), Is.Empty);
        Assert.That(GitEnvironment.Options, Is.SameAs(environment)); Assert.That(DesktopProgram.StartupRequest, Is.SameAs(request));
        Assert.That(DesktopProgram.RepositoryPath, Is.EqualTo(repository)); Assert.That(DesktopProgram.SmokeTest, Is.EqualTo(smoke));
        Assert.That(f.Materials(), Is.EqualTo(before), "Informational dispatch must precede reading preferences, Git configuration, startup or MRU.");
    }

    [Test]
    public void ARealLauncherExceptionReturnsFailureAndDoesNotWriteArbitraryExceptionData()
    {
        using Scope f = new(); string before = f.Materials(); TextWriter old = Console.Error; using StringWriter error = new();
        try
        {
            Console.SetError(error);
            Assert.That(DesktopProgram.Run(["help"], () => throw new InvalidOperationException("private launch detail")), Is.EqualTo(1));
        }
        finally { Console.SetError(old); }
        Assert.That(error.ToString(), Is.EqualTo("Unable to open commandline help." + Environment.NewLine));
        Assert.That(error.ToString(), Does.Not.Contain("private launch detail")); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [Test]
    public void ActualMainKeepsStdoutHelpAndTheVersionPriorityWithoutOpeningAChooserOrCreatingProfiles()
    {
        using Scope f = new(); string before = f.Materials(); TextWriter oldOut = Console.Out, oldError = Console.Error; int gui = 0;
        try
        {
            foreach (string[] args in new string[][] { ["--help"], ["help", "--help"], ["browse", "--help"], ["repo", "--help"] })
            {
                using StringWriter output = new(), error = new(); Console.SetOut(output); Console.SetError(error);
                Assert.That(DesktopProgram.Run(args, () => { gui++; return 9; }), Is.Zero);
                Assert.That(output.ToString(), Is.EqualTo(ExistingUsage + Environment.NewLine)); Assert.That(error.ToString(), Is.Empty);
            }
            using StringWriter actual = new(); Console.SetOut(actual);
            Assert.That(DesktopProgram.Main(["--help"]), Is.Zero); Assert.That(actual.ToString(), Is.EqualTo(ExistingUsage + Environment.NewLine));
            using StringWriter version = new(); Console.SetOut(version);
            Assert.That(DesktopProgram.Run(["help", "--help", "--version"], () => { gui++; return 9; }), Is.Zero);
            Assert.That(version.ToString(), Does.StartWith("Git Extensions ").And.Not.Contain("Usage:"));
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        Assert.That(gui, Is.Zero); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [Test]
    public void OnlyTheSoleCaseSensitiveHelpVerbIsGuiAndLiteralCleanupBrowseAndPrivateProtocolsKeepTheirScope()
    {
        Assert.That(DesktopProgram.IsCommandlineHelpInvocation(["help"]), Is.True);
        foreach (string[] args in new string[][]
        {
            [], ["Help"], ["./help"], ["help", "extra"], ["--help"], ["--askpass", "help"],
            ["cleanup", "help"], ["cleanup", "--help"], ["browse", "help"], ["--smoke-test", "help"],
            ["--write-rebase-plan", "help"], ["--gitextensions-native-editor", "help"]
        }) { Assert.That(DesktopProgram.IsCommandlineHelpInvocation(args), Is.False, string.Join(' ', args)); }
        DesktopStartupArguments cleanup = DesktopStartupArguments.Parse(["cleanup", "--help", "help"]);
        Assert.That(cleanup.Mode, Is.EqualTo(DesktopStartupMode.Cleanup)); Assert.That(cleanup.RepositoryArgument, Is.EqualTo("--help"));
        DesktopStartupArguments browse = DesktopStartupArguments.Parse(["browse", "help"]);
        Assert.That(browse.Mode, Is.EqualTo(DesktopStartupMode.Browse)); Assert.That(browse.RepositoryArgument, Is.EqualTo("help"));
        Assert.That(DesktopStartupArguments.Parse(["./help"]).RepositoryArgument, Is.EqualTo("./help"));
    }

    [Test]
    public void UnknownOptionsKeepTheErrorExitAndDoNotFallBackToTheGuiHelp()
    {
        using Scope f = new(); TextWriter oldOut = Console.Out, oldError = Console.Error; using StringWriter output = new(), error = new();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            Assert.That(DesktopProgram.Run(["--not-a-linux-command"], () => throw new AssertionException("Unexpected GUI help")), Is.EqualTo(2));
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        Assert.That(output.ToString(), Is.Empty); Assert.That(error.ToString(), Does.Contain("Unknown option:"));
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalCompactInformationChromeUsesTheSharedLanguageFontsAndIconWithOnlyRealLinuxCommands(string culture, bool dark)
    {
        using Scope f = new(); TranslationCatalog translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(culture));
        string before = f.Materials(); Window owner = f.Own(new Window { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }); owner.Show();
        CommandlineHelpDialog dialog = f.Own(new CommandlineHelpDialog(translations) { RequestedThemeVariant = owner.ActualThemeVariant, ShowInTaskbar = false });
        Task completion = dialog.ShowDialog(owner); Pump(dialog);
        TextBox commands = Field<TextBox>(dialog, "CommandlineHelpCommands"); TextBlock header = Field<TextBlock>(dialog, "CommandlineHelpHeader");
        Assert.Multiple(() =>
        {
            Assert.That(dialog.Owner, Is.SameAs(owner)); Assert.That(dialog.ShowInTaskbar, Is.False); Assert.That(dialog.Icon, Is.Not.Null);
            Assert.That(dialog.Title, Is.EqualTo(culture == "fr" ? "Utilisation de la ligne de commande" : "Commandline usage"));
            Assert.That(dialog.ClientSize.Width, Is.EqualTo(394).Within(1)); Assert.That(dialog.ClientSize.Height, Is.EqualTo(662).Within(1));
            Assert.That(header.Text, Is.EqualTo(translations.Translate("Supported commandline arguments for") + "\ngitextensions:"));
            Assert.That(header.Text, Does.Not.Contain("gitex.cmd").And.Not.Contain("GitExtensions.exe"));
            Assert.That(commands.FontFamily, Is.EqualTo(dialog.FontFamily)); Assert.That(commands.IsReadOnly, Is.True); Assert.That(commands.AcceptsReturn, Is.True);
            Assert.That(commands.TextWrapping, Is.EqualTo(Avalonia.Media.TextWrapping.Wrap)); Assert.That(commands.IsKeyboardFocusWithin, Is.True);
            Assert.That(commands.SelectionStart, Is.Zero); Assert.That(commands.SelectionEnd, Is.Zero);
            Assert.That(commands.Text!.Split('\n'), Is.EqualTo(DesktopProgram.PublicCommandlineForms));
            foreach (string unavailable in new[] { "checkoutrevision", "commit [", "clone [", "settings", "gitex", "--askpass", "--write-rebase-plan", "--gitextensions-native-editor" })
            { Assert.That(commands.Text, Does.Not.Contain(unavailable)); }
        });
        Rect headerBounds = new(header.TranslatePoint(default, dialog)!.Value, header.Bounds.Size);
        Rect commandsBounds = new(commands.TranslatePoint(default, dialog)!.Value, commands.Bounds.Size);
        Assert.That(headerBounds.Bottom, Is.LessThanOrEqualTo(commandsBounds.Top)); Assert.That(new Rect(default, dialog.ClientSize).Contains(commandsBounds), Is.True);
        Capture(dialog, "commandline-help-" + culture + "-" + (dark ? "Dark" : "Light") + ".png");
        Press(dialog, PhysicalKey.Escape); Pump(owner); await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(dialog.IsVisible, Is.False); Assert.That(owner.IsVisible, Is.True); Assert.That(f.Materials(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public void TheStandaloneWindowIsSelectableReadonlyAndTheFirstEscapeClosesItWithoutAResultOrFurtherWindow()
    {
        using Scope f = new(); CommandlineHelpDialog dialog = f.Own(new CommandlineHelpDialog()); dialog.Show(); Pump(dialog);
        TextBox commands = Field<TextBox>(dialog, "CommandlineHelpCommands"); string original = commands.Text!;
        Press(dialog, PhysicalKey.A, RawInputModifiers.Control); dialog.KeyTextInput("cannot replace usage"); Pump(dialog);
        Assert.That(commands.Text, Is.EqualTo(original)); Assert.That(commands.SelectedText, Is.EqualTo(original));
        Press(dialog, PhysicalKey.Enter); Pump(dialog); Assert.That(dialog.IsVisible, Is.True); Assert.That(commands.Text, Is.EqualTo(original));
        Assert.That(dialog.OwnedWindows, Is.Empty); Press(dialog, PhysicalKey.Escape); Pump(dialog); Assert.That(dialog.IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task OwnedHelpCancellationClosesAndDrainsItsOnlyWindowBeforeTheTaskCompletes()
    {
        using Scope f = new(); Window owner = f.Own(new Window()); owner.Show(); using CancellationTokenSource cancellation = new();
        Task help = CommandlineHelpDialog.ShowAsync(owner, cancellation.Token); Pump(owner);
        CommandlineHelpDialog child = owner.OwnedWindows.OfType<CommandlineHelpDialog>().Single();
        Assert.That(child.Owner, Is.SameAs(owner)); Assert.That(child.IsVisible, Is.True); cancellation.Cancel(); Pump(child); Pump(owner);
        try { await help.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Fail("Cancellation must remain observable after closing the child."); }
        catch (OperationCanceledException) { }
        Assert.That(child.IsVisible, Is.False); Assert.That(owner.IsVisible, Is.True);
    }

    [AvaloniaTest]
    public async Task ClosingTheOwnerCompletesOwnedHelpWithoutAContinuationOrNewWindow()
    {
        using Scope f = new(); Window owner = f.Own(new Window()); owner.Show(); Task help = CommandlineHelpDialog.ShowAsync(owner); Pump(owner);
        CommandlineHelpDialog child = owner.OwnedWindows.OfType<CommandlineHelpDialog>().Single(); owner.Close(); Pump(child); Pump(owner);
        await help.WaitAsync(TimeSpan.FromSeconds(5)); Assert.That(child.IsVisible, Is.False); Assert.That(owner.IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task PreCancellationOrAnInvisibleOwnerDoesNotCreateAWindow()
    {
        using Scope f = new(); Window owner = f.Own(new Window()); using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await CommandlineHelpDialog.ShowAsync(owner, cancellation.Token));
        await CommandlineHelpDialog.ShowAsync(owner); Assert.That(owner.OwnedWindows, Is.Empty);
    }

    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); Pump(window); }
    private static void Capture(Window window, string name)
    {
        Pump(window); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using WriteableBitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private sealed class Scope : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-commandline-help-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly string _oldDirectory = Environment.CurrentDirectory;
        private readonly List<Window> _windows = [];
        internal Scope() { Directory.CreateDirectory(_root); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_root, "config")); Environment.CurrentDirectory = _root; }
        internal T Own<T>(T window) where T : Window { _windows.Add(window); return window; }
        internal void WriteMalformedProfiles()
        {
            string file = new PreferencesStore().FilePath; Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "not JSON");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(file)!, "git-environment.json"), "not JSON");
        }
        internal string Materials() => string.Join('\n', Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(_root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        public void Dispose()
        {
            foreach (Window window in _windows.AsEnumerable().Reverse()) { foreach (Window child in window.OwnedWindows.ToArray()) { child.Close(); } window.Close(); Pump(window); }
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig); Environment.CurrentDirectory = _oldDirectory; Directory.Delete(_root, recursive: true);
        }
    }
}

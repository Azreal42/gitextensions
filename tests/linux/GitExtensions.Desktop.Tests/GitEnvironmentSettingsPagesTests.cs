using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GitEnvironmentSettingsPagesTests
{
    [AvaloniaTest]
    public void PathsAreDraftOnlyAndAcceptSavedRefreshesTheBaselineWithoutTouchingTheLoginEnvironment()
    {
        string? home = Environment.GetEnvironmentVariable("HOME");
        GitEnvironmentPreferences initial = new(); GitEnvironmentSettingsPage page = new(initial, Catalog());
        using WindowScope scope = new(page); int changes = 0; page.DraftChanged += () => changes++;
        Field<TextBox>(scope.Window, "GitPath").Text = "/opt/a git/bin/git";
        Field<TextBox>(scope.Window, "LinuxToolsDir").Text = "/opt/tools with spaces";
        Assert.That(initial.GitExecutable, Is.Empty); Assert.That(page.IsDirty, Is.True);
        Assert.That(page.Draft.GitExecutable, Is.EqualTo("/opt/a git/bin/git")); Assert.That(changes, Is.EqualTo(2));
        page.SetCheckAtStartup(false); Assert.That(page.Draft.CheckAtStartup, Is.False);
        GitEnvironmentPreferences saved = page.Draft; page.AcceptSaved(saved);
        Assert.That(page.IsDirty, Is.False); Assert.That(Environment.GetEnvironmentVariable("HOME"), Is.EqualTo(home));
        Field<TextBox>(scope.Window, "GitPath").Text = "bad\0path";
        Assert.That(page.Validate(out string? error), Is.False); Assert.That(error, Does.Contain("NUL"));
    }

    [AvaloniaTest]
    public async Task BrowseAndHomeCallbacksChangeOnlyTheDraftAndFailuresRemainLiteralAndVisible()
    {
        GitEnvironmentPreferences initial = new(); GitEnvironmentSettingsPage page = new(initial, Catalog())
        {
            PickGitExecutable = () => Task.FromResult<string?>("/chosen git"),
            PickToolsDirectory = () => throw new IOException("literal <tools> failure"),
            ChangeHome = draft => Task.FromResult<GitEnvironmentPreferences?>(draft with { CustomHomeDirectory = Path.GetTempPath() })
        };
        using WindowScope scope = new(page);
        Click(scope.Window, "BrowseGitPath"); await Until(() => page.Draft.GitExecutable == "/chosen git");
        Click(scope.Window, "BrowseLinuxToolsDir");
        Assert.That(Field<TextBlock>(scope.Window, "GitEnvironmentError").Text, Is.EqualTo("literal <tools> failure"));
        Assert.That(Field<TextBlock>(scope.Window, "GitEnvironmentError").IsVisible, Is.True); Assert.That(page.Draft.ToolsDirectory, Is.Empty);
        Click(scope.Window, "ChangeHomeButton"); await Until(() => page.Draft.CustomHomeDirectory == Path.GetTempPath());
        Assert.That(initial.CustomHomeDirectory, Is.Null); Assert.That(page.IsDirty, Is.True);
        Assert.That(Field<TextBlock>(scope.Window, "homeIsSetToLabel").Text, Does.Contain("child processes only"));
    }

    [AvaloniaTest]
    public void RebaseKeepsEachEditedFieldWhileAdoptingPublishedUntouchedValuesAndTheExplicitEnvironmentSnapshot()
    {
        GitEnvironmentPreferences initial = new() { GitExecutable = "git-old", ToolsDirectory = "/old/tools", CheckAtStartup = true };
        GitEnvironmentSettingsPage page = new(initial, Catalog()); using WindowScope scope = new(page);
        Field<TextBox>(scope.Window, "GitPath").Text = "/draft/git";
        GitEnvironmentPreferences published = initial with { GitExecutable = "/other/git", ToolsDirectory = "/published/tools", CheckAtStartup = false };
        page.RebasePublished(published);
        Assert.That(page.Draft.GitExecutable, Is.EqualTo("/draft/git")); Assert.That(page.Draft.ToolsDirectory, Is.EqualTo("/published/tools"));
        Assert.That(page.Draft.CheckAtStartup, Is.False); Assert.That(page.IsDirty, Is.True);
        page.SetEnvironmentInformation("captured HOME = /saved/home\nGIT_CONFIG_GLOBAL = /explicit/config");
        Field<TextBox>(scope.Window, "GitPath").Text = "git";
        Assert.That(Field<TextBlock>(scope.Window, "homeIsSetToLabel").Text, Is.EqualTo("captured HOME = /saved/home\nGIT_CONFIG_GLOBAL = /explicit/config"));
        page.AcceptSaved(page.Draft); Assert.That(page.IsDirty, Is.False);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ASlowPickerCannotOverwriteASavedBaselineOrAnInvalidatedPage(bool invalidate)
    {
        TaskCompletionSource<string?> choice = new(); GitEnvironmentSettingsPage page = new(new(), Catalog()) { PickGitExecutable = () => choice.Task };
        using WindowScope scope = new(page); Click(scope.Window, "BrowseGitPath");
        Assert.That(Field<Button>(scope.Window, "BrowseGitPath").IsEffectivelyEnabled, Is.False);
        if (invalidate) { page.Invalidate(); } else { page.AcceptSaved(new() { GitExecutable = "/published/git" }); }
        choice.SetResult("/late/git"); await Until(() => choice.Task.IsCompleted); await Task.Yield(); Pump(scope.Window);
        Assert.That(page.Draft.GitExecutable, Is.EqualTo(invalidate ? "" : "/published/git"));
    }

    [AvaloniaTest]
    public async Task HomeSelectionAndCancellationReturnDraftsWithoutRequiringAnExistingGitconfig()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gitextensions-home-page-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string? inherited = Environment.GetEnvironmentVariable("HOME");
        try
        {
            using WindowScope owner = new(new TextBlock());
            GitEnvironmentPreferences initial = new(); GitHomeDirectoryDialog dialog = new(initial, Catalog(), directory);
            Task<bool> completion = dialog.ShowDialog<bool>(owner.Window); Pump(dialog);
            Assert.That(Field<RadioButton>(dialog, "defaultHome").IsChecked, Is.True);
            Click(dialog, "otherHome"); Field<TextBox>(dialog, "otherHomeDir").Text = directory; Click(dialog, "ok");
            Assert.That(await completion, Is.True); Assert.That(dialog.Result.CustomHomeDirectory, Is.EqualTo(directory));
            Assert.That(File.Exists(Path.Combine(directory, ".gitconfig")), Is.False);
            Assert.That(Environment.GetEnvironmentVariable("HOME"), Is.EqualTo(inherited));
            GitHomeDirectoryDialog canceled = new(initial, Catalog(), directory); Task<bool> cancel = canceled.ShowDialog<bool>(owner.Window); Pump(canceled);
            Click(canceled, "otherHome"); Field<TextBox>(canceled, "otherHomeDir").Text = directory; Click(canceled, "CancelHome");
            Assert.That(await cancel, Is.False); Assert.That(canceled.Result, Is.EqualTo(initial));
        }
        finally { Directory.Delete(directory); }
    }

    [AvaloniaTest]
    [TestCase("")]
    [TestCase("relative/home")]
    [TestCase("/missing/home/gitextensions-ui-01")]
    public async Task InvalidHomeKeepsTheDialogOpenAndDisplaysTheActualValidationError(string path)
    {
        using WindowScope owner = new(new TextBlock()); GitHomeDirectoryDialog dialog = new(new(), Catalog());
        Task<bool> completion = dialog.ShowDialog<bool>(owner.Window); Pump(dialog);
        Click(dialog, "otherHome"); Field<TextBox>(dialog, "otherHomeDir").Text = path; Click(dialog, "ok");
        Assert.That(completion.IsCompleted, Is.False); Assert.That(dialog.IsVisible, Is.True);
        Assert.That(Field<TextBlock>(dialog, "HomeError").Text, Is.EqualTo(GitHomeDirectoryDialog.ValidateDirectory(path)));
        Assert.That(Field<TextBlock>(dialog, "HomeError").IsVisible, Is.True);
        Click(dialog, "CancelHome"); Assert.That(await completion, Is.False);
    }

    [AvaloniaTest]
    public async Task HomePickerCompletionAfterCancelCannotChangeTheResult()
    {
        using WindowScope owner = new(new TextBlock()); TaskCompletionSource<string?> picker = new();
        GitEnvironmentPreferences initial = new(); GitHomeDirectoryDialog dialog = new(initial, Catalog()) { PickHomeDirectory = () => picker.Task };
        Task<bool> completion = dialog.ShowDialog<bool>(owner.Window); Pump(dialog); Click(dialog, "otherHomeBrowse");
        Click(dialog, "CancelHome"); Assert.That(await completion, Is.False);
        picker.SetResult(Path.GetTempPath()); await Task.Yield(); Dispatcher.UIThread.RunJobs();
        Assert.That(dialog.Result, Is.EqualTo(initial));
    }

    [AvaloniaTest]
    public void ChecklistPreservesTheOriginalApplicableOrderAndNeverHidesAnUnobservedCheck()
    {
        ChecklistSettingsPage page = new(Catalog()); using WindowScope scope = new(page);
        string[] names = ["GitFound", "UserNameSet", "Editor", "MergeTool", "DiffTool", "GitBinFound", "SshConfig", "translationConfig", "HomeDirectory"];
        Assert.That(names.Select(name => Grid.GetRow(Field<Button>(scope.Window, name))), Is.EqualTo(Enumerable.Range(1, 9)));
        foreach (string name in names) { Assert.That(((TextBlock)Field<Button>(scope.Window, name).Content!).Text, Is.EqualTo("Not checked.")); }
        Assert.That(scope.Window.GetVisualDescendants().Any(control => control.Name is "GitExtensionsInstall" or "ShellExtensionsRegistered" or "GcmDetected"), Is.False);
        int changes = 0; page.CheckAtStartupChanged = _ => changes++;
        page.Render(Checks(), false); page.Render(Checks(), true); Assert.That(changes, Is.Zero);
        Assert.That(page.CheckAtStartup, Is.True); Click(scope.Window, "CheckAtStartup"); Assert.That(changes, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task StatusAndRepairShareTheSameActionAndBusyAndInvalidatedPagesRejectRepeatedRequests()
    {
        ChecklistSettingsPage page = new(Catalog()); using WindowScope scope = new(page); TaskCompletionSource repair = new(); int calls = 0;
        page.Render(Checks(), true); page.RepairRequested = id => { Assert.That(id, Is.EqualTo(GitEnvironmentCheckId.GitExecutable)); calls++; return repair.Task; };
        Click(scope.Window, "GitFound"); Assert.That(calls, Is.EqualTo(1));
        Assert.That(Field<Button>(scope.Window, "GitFound_Fix").IsEffectivelyEnabled, Is.False);
        Button disabledRepair = Field<Button>(scope.Window, "GitFound_Fix");
        Point disabledPoint = disabledRepair.TranslatePoint(new Point(disabledRepair.Bounds.Width / 2, disabledRepair.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseMove(disabledPoint); scope.Window.MouseDown(disabledPoint, MouseButton.Left); scope.Window.MouseUp(disabledPoint, MouseButton.Left);
        Pump(scope.Window); Assert.That(calls, Is.EqualTo(1));
        repair.SetResult(); await Task.Yield(); Pump(scope.Window);
        Click(scope.Window, "GitFound_Fix"); Assert.That(calls, Is.EqualTo(2));
        page.SetBusy(true); Assert.That(Field<Button>(scope.Window, "GitFound").IsEffectivelyEnabled, Is.False);
        page.SetBusy(false); page.Invalidate(); page.SetBusy(false); Assert.That(Field<Button>(scope.Window, "GitFound").IsEffectivelyEnabled, Is.False);
    }

    [AvaloniaTest]
    public void SaveAndRescanFailureIsVisibleAndCannotTurnAFailedCheckGreenOrChangeTheStartupFlag()
    {
        ChecklistSettingsPage page = new(Catalog()); using WindowScope scope = new(page); page.Render(Checks(), true);
        page.SaveAndRescanRequested = () => throw new IOException("profile locked <literal>");
        Click(scope.Window, "Rescan");
        Assert.That(Field<TextBlock>(scope.Window, "ChecklistError").Text, Is.EqualTo("profile locked <literal>"));
        Assert.That(Field<TextBlock>(scope.Window, "ChecklistError").IsVisible, Is.True);
        Assert.That(((ISolidColorBrush)Field<Button>(scope.Window, "GitFound").Background!).Color, Is.EqualTo(Color.FromRgb(255, 128, 128)));
        Assert.That(page.CheckAtStartup, Is.True);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public void OriginalLabelsAndChecklistColorsRemainReadableInBothThemesAndScreenshots(bool dark)
    {
        TranslationCatalog translations = Catalog("fr"); GitEnvironmentSettingsPage git = new(new(), translations);
        using WindowScope gitScope = new(git, dark);
        Assert.That(Field<Button>(gitScope.Window, "BrowseGitPath").Content, Is.EqualTo("Parcourir"));
        Assert.That(Field<Button>(gitScope.Window, "ChangeHomeButton").Content, Is.EqualTo("Changer HOME"));
        Capture(gitScope.Window, "targeted-ui-01-paths-" + (dark ? "dark" : "light") + ".png");
        ChecklistSettingsPage checklist = new(translations); using WindowScope checkScope = new(checklist, dark);
        checklist.RepairRequested = _ => Task.CompletedTask; checklist.SaveAndRescanRequested = () => Task.CompletedTask; checklist.Render(Checks(), true); Pump(checkScope.Window);
        Assert.That(Field<Button>(checkScope.Window, "Rescan").Content, Is.EqualTo("Enregistrer et re-scanner"));
        Assert.That(Field<Button>(checkScope.Window, "GitFound_Fix").Content, Is.EqualTo("Réparer"));
        Assert.That(Field<Button>(checkScope.Window, "UserNameSet_Fix").IsVisible, Is.False);
        foreach (string name in new[] { "GitFound", "UserNameSet", "Editor", "MergeTool" })
        {
            Button button = Field<Button>(checkScope.Window, name); Color back = ((ISolidColorBrush)button.Background!).Color;
            Color fore = ((ISolidColorBrush)button.Foreground!).Color;
            Assert.That(Contrast(back, fore), Is.GreaterThanOrEqualTo(4.5));
        }
        Capture(checkScope.Window, "targeted-ui-01-checklist-" + (dark ? "dark" : "light") + ".png");
    }

    private static GitEnvironmentCheck[] Checks() => Enum.GetValues<GitEnvironmentCheckId>().Select(id => new GitEnvironmentCheck(id,
        id == GitEnvironmentCheckId.GitExecutable ? GitEnvironmentCheckStatus.Error : id == GitEnvironmentCheckId.Editor ? GitEnvironmentCheckStatus.Warning : id == GitEnvironmentCheckId.MergeTool ? GitEnvironmentCheckStatus.Unavailable : GitEnvironmentCheckStatus.Good,
        id + ": diagnostic literal", true)).ToArray();
    private static TranslationCatalog Catalog(string language = "en") => TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Window window, string name) { Pump(window); PushProcessTestDriver.Pointer(Field<Control>(window, name)); Pump(window); }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    {
        for (int count = 0; count < 100 && !condition(); count++) { await Task.Delay(1); Dispatcher.UIThread.RunJobs(); }
        Assert.That(condition(), Is.True);
    }
    private static void Capture(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS"); if (directory is null) { return; }
        Pump(window); using var image = window.CaptureRenderedFrame(); Assert.That(image, Is.Not.Null);
        Directory.CreateDirectory(directory); string file = Path.Combine(directory, name); image!.Save(file); TestContext.AddTestAttachment(file);
    }
    private static double Contrast(Color a, Color b)
    {
        static double L(Color color)
        { static double C(byte channel) { double x = channel / 255d; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); } return .2126 * C(color.R) + .7152 * C(color.G) + .0722 * C(color.B); }
        return (Math.Max(L(a), L(b)) + .05) / (Math.Min(L(a), L(b)) + .05);
    }
    private sealed class WindowScope : IDisposable
    {
        internal Window Window { get; }
        internal WindowScope(Control content, bool dark = false)
        { Window = new() { Width = 920, Height = 660, Content = new Border { Padding = new Thickness(12), Child = content }, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; Window.Show(); Pump(Window); }
        public void Dispose() { foreach (Window child in Window.OwnedWindows.ToArray()) { child.Close(); } Window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}

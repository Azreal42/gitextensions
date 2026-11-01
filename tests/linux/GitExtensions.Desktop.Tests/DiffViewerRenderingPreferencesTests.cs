using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using GitCommands.Settings;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class DiffViewerRenderingPreferencesTests
{
    [Test]
    public void OriginalRenderingDefaultsAlsoApplyToExistingJsonWithoutTheNewFields()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        Assert.That(store.BeginRenderingViewerSession(), Is.EqualTo(new DiffViewerRenderingOptions(false, true)));
        Assert.That(store.Load().RememberNonPrintingChars, Is.False); Assert.That(store.Load().RememberSyntaxHighlightingInDiff, Is.True);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{\"UseGitColoring\":false,\"Appearance\":1,\"RememberAppearance\":true,\"ContextLines\":9,\"RememberContextLines\":true}");
        DiffViewerPreferences legacy = store.Load();
        Assert.That(legacy.UseGitColoring, Is.False); Assert.That(legacy.Appearance, Is.EqualTo(DiffViewerAppearance.GitWordDiff)); Assert.That(legacy.ContextLines, Is.EqualTo(9));
        Assert.That(store.BeginRenderingViewerSession(), Is.EqualTo(new DiffViewerRenderingOptions()));
        Assert.That(legacy.RememberSyntaxHighlightingInDiff && !legacy.RememberNonPrintingChars, Is.True);
    }

    [TestCase("{\"ShowNonPrintingChars\":null}")]
    [TestCase("{\"ShowSyntaxHighlightingInDiff\":null}")]
    [TestCase("{\"RememberSyntaxHighlightingInDiff\":null}")]
    public void InvalidRenderingJsonRecoversWithoutKeepingAnOldSession(string content)
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        Assert.That(store.SaveRenderingOptions(new(), new(true, false)), Is.True);
        File.WriteAllText(store.FilePath, content);
        Assert.That(store.Load(), Is.EqualTo(new DiffViewerPreferences()));
        Assert.That(store.BeginRenderingViewerSession(), Is.EqualTo(new DiffViewerRenderingOptions()));
    }

    [AvaloniaTest]
    public void NewReadersReloadUnrememberedBaselinesWithoutChangingReadersAlreadyOpen()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        using DiffViewerRenderingMenu first = new(store), alreadyOpen = new(new DiffViewerPreferencesStore(directory.Path + "/."));
        int otherChanges = 0; alreadyOpen.OptionsChanged += (_, _) => otherChanges++;
        Assert.That(first.ToggleNonPrinting(), Is.True); Assert.That(first.ToggleSyntaxHighlighting(), Is.True);
        Assert.That(first.Options, Is.EqualTo(new DiffViewerRenderingOptions(true, false)));
        Assert.That(alreadyOpen.Options, Is.EqualTo(new DiffViewerRenderingOptions())); Assert.That(otherChanges, Is.Zero);
        using DiffViewerRenderingMenu later = new(store);
        Assert.That(later.Options, Is.EqualTo(new DiffViewerRenderingOptions(false, false)), "Nonprinting reloads its saved baseline; remembered syntax uses the runtime value.");
        Assert.That(first.Options, Is.EqualTo(new DiffViewerRenderingOptions(true, false)), "Constructing another reader must not reset the first one's local display.");
        Assert.That(store.SaveSettings(store.Load() with { RememberNonPrintingChars = true, RememberSyntaxHighlightingInDiff = false }), Is.True);
        Dispatcher.UIThread.RunJobs();
        Assert.That(first.RememberNonPrintingChars && !first.RememberSyntaxHighlightingInDiff, Is.True);
        Assert.That(first.Options, Is.EqualTo(new DiffViewerRenderingOptions(true, false))); Assert.That(otherChanges, Is.Zero);
        Assert.That(later.ToggleNonPrinting(), Is.True);
        using DiffViewerRenderingMenu final = new(store);
        Assert.That(final.Options, Is.EqualTo(new DiffViewerRenderingOptions(true, true)), "Remembered nonprinting uses the latest runtime; unremembered syntax reloads true.");
        Assert.That(Read(store, "ShowNonPrintingChars").GetBoolean(), Is.False);
        Assert.That(Read(store, "ShowSyntaxHighlightingInDiff").GetBoolean(), Is.True, "Remember flags never automatically save rendering baselines.");
    }

    [AvaloniaTest]
    public void OldControllersPatchOnlyTheirChangedFieldsAndSaveDefaultUsesTheLatestSharedRenderingValues()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        DiffViewerPreferences oldAppearance = store.Load();
        Assert.That(store.SaveSettings(store.Load() with { RememberNonPrintingChars = true }), Is.True);
        using DiffViewerRenderingMenu first = new(store), second = new(store);
        using DiffViewerComparisonMenu comparison = new(store);
        Assert.That(first.ToggleSyntaxHighlighting(), Is.True); Assert.That(second.ToggleNonPrinting(), Is.True);
        Assert.That(first.Options, Is.EqualTo(new DiffViewerRenderingOptions(false, false)));
        Assert.That(second.Options, Is.EqualTo(new DiffViewerRenderingOptions(true, true)));
        Assert.That(store.Load().ShowNonPrintingChars, Is.True); Assert.That(store.Load().ShowSyntaxHighlightingInDiff, Is.False, "A stale reader's nonprinting click must not restore its old syntax value.");
        Assert.That(comparison.SelectWhitespace(IgnoreWhitespaceKind.AllSpace), Is.True);
        Assert.That(comparison.ToggleWholeFile(), Is.True);
        Assert.That(store.Save(oldAppearance with { Appearance = DiffViewerAppearance.GitWordDiff, ReverseGitColoring = false }), Is.True);
        Assert.That(store.Load().ShowNonPrintingChars && !store.Load().ShowSyntaxHighlightingInDiff, Is.True);
        Assert.That(store.SaveSettings(oldAppearance with { RememberNonPrintingChars = true, RememberSyntaxHighlightingInDiff = false }), Is.True);
        Assert.That(store.Load().ShowNonPrintingChars && !store.Load().ShowSyntaxHighlightingInDiff, Is.True, "An old settings page changes flags, never the runtime rendering values.");
        Assert.That(store.SaveCurrentViewSettingsAsDefault(), Is.True);
        Assert.That(Read(store, "ShowNonPrintingChars").GetBoolean(), Is.True); Assert.That(Read(store, "ShowSyntaxHighlightingInDiff").GetBoolean(), Is.False);
        Assert.That(Read(store, "IgnoreWhitespace").GetInt32(), Is.EqualTo((int)IgnoreWhitespaceKind.AllSpace)); Assert.That(Read(store, "ShowEntireFile").GetBoolean(), Is.True);
        Assert.That(Read(store, "Appearance").GetInt32(), Is.EqualTo((int)DiffViewerAppearance.GitWordDiff));
        Assert.That(first.Options, Is.EqualTo(new DiffViewerRenderingOptions(false, false)));
        Assert.That(second.Options, Is.EqualTo(new DiffViewerRenderingOptions(true, true)));
    }

    [AvaloniaTest]
    public void FailedAtomicRenderingSaveKeepsLocalAndSharedValuesAndEmitsNoChange()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        using DiffViewerRenderingMenu reader = new(store); Directory.CreateDirectory(store.FilePath);
        int local = 0, shared = 0; reader.OptionsChanged += (_, _) => local++; store.Changed += (_, _) => shared++;
        Assert.That(reader.SetOptions(new(true, false)), Is.False);
        Assert.That(reader.Options, Is.EqualTo(new DiffViewerRenderingOptions())); Assert.That(store.BeginRenderingViewerSession(), Is.EqualTo(new DiffViewerRenderingOptions()));
        Assert.That(local + shared, Is.Zero); Assert.That(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"), Is.Empty);
    }

    [Test]
    public async Task ARealNewProcessReadsTheSavedRenderingBaselineNotRememberedSessionValues()
    {
        const string directoryVariable = "GITEXTENSIONS_RENDERING_PREFS_PROBE_DIRECTORY", expectedVariable = "GITEXTENSIONS_RENDERING_PREFS_PROBE_DEFAULT";
        string? probe = Environment.GetEnvironmentVariable(directoryVariable);
        if (probe is not null)
        {
            bool saved = Environment.GetEnvironmentVariable(expectedVariable) == "saved";
            Assert.That(new DiffViewerPreferencesStore(probe).BeginRenderingViewerSession(), Is.EqualTo(saved ? new DiffViewerRenderingOptions(true, false) : new DiffViewerRenderingOptions()));
            return;
        }
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        Assert.That(store.SaveSettings(store.Load() with { RememberNonPrintingChars = true }), Is.True);
        Assert.That(store.SaveRenderingOptions(new(), new(true, false)), Is.True); await Probe("original");
        Assert.That(store.SaveCurrentViewSettingsAsDefault(), Is.True); await Probe("saved");
        Assert.That(store.SaveRenderingOptions(new(true, false), new()), Is.True); await Probe("saved");
        async Task Probe(string expected)
        {
            ProcessStartInfo start = TestDotnetHost.CreateStartInfo();
            foreach (string argument in new[] { "test", "tests/linux/GitExtensions.Desktop.Tests/GitExtensions.Desktop.Tests.csproj", "-c", "Release", "--no-build", "--no-restore", "--filter",
                "FullyQualifiedName~GitExtensions.Desktop.Tests.DiffViewerRenderingPreferencesTests.ARealNewProcessReadsTheSavedRenderingBaselineNotRememberedSessionValues", "--", "NUnit.NumberOfTestWorkers=0" }) { start.ArgumentList.Add(argument); }
            start.Environment[directoryVariable] = directory.Path; start.Environment[expectedVariable] = expected;
            using Process process = Process.Start(start)!; Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); } catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); } throw; }
            Assert.That(process.ExitCode, Is.Zero, await output + await error);
        }
    }

    private static JsonElement Read(DiffViewerPreferencesStore store, string property)
    { using JsonDocument json = JsonDocument.Parse(File.ReadAllText(store.FilePath)); return json.RootElement.GetProperty(property).Clone(); }
}

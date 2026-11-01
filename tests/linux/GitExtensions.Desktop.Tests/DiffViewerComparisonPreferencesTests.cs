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
public class DiffViewerComparisonPreferencesTests
{
    [Test]
    public void RememberedAppearanceRemainsRuntimeUntilExplicitDefaultSaveAndOldJsonRemainsTheBaseline()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new();
        DiffViewerPreferencesStore store = new(directory.Path);
        Assert.That(store.Save(new(false, false, DiffViewerAppearance.GitWordDiff, true)), Is.True);
        Assert.That(Read(store, "Appearance").GetInt32(), Is.Zero);
        Assert.That(new DiffViewerPreferencesStore(directory.Path).BeginViewerSession().Appearance, Is.EqualTo(DiffViewerAppearance.GitWordDiff));
        Assert.That(store.SaveCurrentViewSettingsAsDefault(), Is.True);
        Assert.That(Read(store, "Appearance").GetInt32(), Is.EqualTo((int)DiffViewerAppearance.GitWordDiff));
        Assert.That(store.Save(store.Load() with { Appearance = DiffViewerAppearance.Difftastic, RememberAppearance = false }), Is.True);
        Assert.That(store.BeginViewerSession().Appearance, Is.EqualTo(DiffViewerAppearance.GitWordDiff), "A new reader reloads the explicitly saved baseline, not an unconditional Patch value.");
        Assert.That(store.Load().UseGitColoring || store.Load().ReverseGitColoring, Is.False);

        using DiffViewerPreferencesTests.TemporaryDirectory legacyDirectory = new(); DiffViewerPreferencesStore legacy = new(legacyDirectory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy.FilePath)!);
        File.WriteAllText(legacy.FilePath, "{\"Appearance\":2,\"RememberAppearance\":false,\"UseGitColoring\":false}");
        Assert.That(legacy.BeginViewerSession().Appearance, Is.EqualTo(DiffViewerAppearance.Difftastic));
        Assert.That(legacy.Load().RememberWhitespace, Is.True);
        Assert.That(legacy.Save(legacy.Load() with { Appearance = DiffViewerAppearance.Patch }), Is.True);
        Assert.That(Read(legacy, "Appearance").GetInt32(), Is.EqualTo(2), "Existing saved JSON values retain their meaning as durable defaults.");
    }

    [AvaloniaTest]
    public void ComparisonSnapshotsStayLocalAndOldAppearanceSnapshotsCannotEraseTheLatestSession()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        DiffViewerPreferences oldAppearance = store.Load();
        using DiffViewerComparisonMenu first = new(store), alreadyOpen = new(new DiffViewerPreferencesStore(directory.Path + "/."));
        int otherChanges = 0; alreadyOpen.OptionsChanged += (_, _) => otherChanges++;
        Assert.That(first.SelectWhitespace(IgnoreWhitespaceKind.AllSpace), Is.True);
        Assert.That(first.SetRememberWholeFile(true), Is.True); Assert.That(first.ToggleWholeFile(), Is.True);
        Assert.That(first.SetRememberContextLines(true), Is.True);
        Assert.That(store.Save(oldAppearance with { ReverseGitColoring = false }), Is.True); Dispatcher.UIThread.RunJobs();
        Assert.That(store.Load().IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.AllSpace));
        Assert.That(store.Load().ShowEntireFile && store.Load().RememberWholeFile && store.Load().RememberContextLines, Is.True);
        Assert.That(alreadyOpen.Options, Is.EqualTo(new GitExtensions.Linux.GitComparisonDiffOptions())); Assert.That(otherChanges, Is.Zero);
        Assert.That(alreadyOpen.RememberWholeFile && alreadyOpen.RememberContextLines, Is.True);
        using DiffViewerComparisonMenu later = new(new DiffViewerPreferencesStore(directory.Path));
        Assert.That(later.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.AllSpace)); Assert.That(later.Options.ShowEntireFile, Is.True);
        Assert.That(later.SelectWhitespace(IgnoreWhitespaceKind.Eol), Is.True);
        Assert.That(first.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.AllSpace));
        Assert.That(first.SaveCurrentViewSettingsAsDefault(), Is.True);
        Assert.That(Read(store, "IgnoreWhitespace").GetInt32(), Is.EqualTo((int)IgnoreWhitespaceKind.Eol), "An old reader's save-default command must save the latest shared session, not its old local snapshot.");
    }

    [AvaloniaTest]
    public void ContextPersistsOnlyWhenRememberedAndChangingRememberDoesNotOverwriteTheSavedCount()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        using DiffViewerComparisonMenu first = new(store);
        Assert.That(first.IncreaseContext(), Is.True); Assert.That(first.IncreaseContext(), Is.True);
        Assert.That(first.Options.ContextLines, Is.EqualTo(5)); Assert.That(Read(store, "ContextLines").GetInt32(), Is.EqualTo(3));
        using DiffViewerComparisonMenu second = new(store); Assert.That(second.Options.ContextLines, Is.EqualTo(3));
        Assert.That(first.SetRememberContextLines(true), Is.True); Assert.That(first.IncreaseContext(), Is.True);
        Assert.That(Read(store, "ContextLines").GetInt32(), Is.EqualTo(6)); Assert.That(second.Options.ContextLines, Is.EqualTo(3));
        using DiffViewerComparisonMenu third = new(store); Assert.That(third.Options.ContextLines, Is.EqualTo(6));
        Assert.That(first.SetRememberContextLines(false), Is.True);
        using DiffViewerComparisonMenu fourth = new(store); Assert.That(fourth.Options.ContextLines, Is.EqualTo(3));
        Assert.That(first.SetRememberContextLines(true), Is.True);
        using DiffViewerComparisonMenu fifth = new(store); Assert.That(fifth.Options.ContextLines, Is.EqualTo(6));
        Assert.That(store.SaveCurrentViewSettingsAsDefault(), Is.True); Assert.That(Read(store, "ContextLines").GetInt32(), Is.EqualTo(6));
    }

    [AvaloniaTest]
    public void RememberWhitespaceAndWholeFileChooseSessionOrSavedBaselineWithoutChangingExistingReaders()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        using DiffViewerComparisonMenu first = new(store);
        Assert.That(first.SelectWhitespace(IgnoreWhitespaceKind.Change), Is.True); Assert.That(first.SetRememberWholeFile(true), Is.True);
        Assert.That(first.ToggleWholeFile(), Is.True);
        Assert.That(Read(store, "IgnoreWhitespace").GetInt32(), Is.Zero); Assert.That(Read(store, "ShowEntireFile").GetBoolean(), Is.False);
        Assert.That(first.SaveCurrentViewSettingsAsDefault(), Is.True);
        Assert.That(first.SelectWhitespace(IgnoreWhitespaceKind.AllSpace), Is.True); Assert.That(first.ToggleWholeFile(), Is.True);
        Assert.That(first.SetRememberWhitespace(false), Is.True); Assert.That(first.SetRememberWholeFile(false), Is.True);
        using DiffViewerComparisonMenu second = new(store);
        Assert.That(second.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.Change)); Assert.That(second.Options.ShowEntireFile, Is.True);
        Assert.That(first.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.AllSpace)); Assert.That(first.Options.ShowEntireFile, Is.False);
        Assert.That(first.SetRememberWhitespace(true), Is.True); Assert.That(first.SelectWhitespace(IgnoreWhitespaceKind.Eol), Is.True);
        using DiffViewerComparisonMenu third = new(store); Assert.That(third.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.Eol));
        Assert.That(second.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.Change));
    }

    [TestCase("{broken")]
    [TestCase("null")]
    [TestCase("{\"RememberWhitespace\":null}")]
    public void CorruptNewOptionsRecoverDefaults(string text)
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!); File.WriteAllText(store.FilePath, text);
        Assert.That(store.Load(), Is.EqualTo(new DiffViewerPreferences()));
        Assert.That(store.Save(new()), Is.True); Assert.That(Read(store, "RememberWhitespace").GetBoolean(), Is.True);
    }

    [Test]
    public void InvalidEnumsAndContextRecoverIndividuallyAndInvalidCountsAreRejected()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{\"UseGitColoring\":false,\"Appearance\":87,\"IgnoreWhitespace\":88,\"ContextLines\":-1,\"RememberContextLines\":true}");
        DiffViewerPreferences loaded = store.Load(); Assert.That(loaded.UseGitColoring, Is.False);
        Assert.That(loaded.Appearance, Is.EqualTo(DiffViewerAppearance.Patch)); Assert.That(loaded.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.None)); Assert.That(loaded.ContextLines, Is.EqualTo(3));
        Assert.That(store.Save(loaded), Is.True);
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"), Is.Empty);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveContextLines(-1));
    }

    [AvaloniaTest]
    public void AtomicSaveFailurePreservesTheReaderAndSharedRuntimeWithoutSendingOptionsChanged()
    {
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        using DiffViewerComparisonMenu reader = new(store);
        Directory.CreateDirectory(store.FilePath); int local = 0, shared = 0;
        reader.OptionsChanged += (_, _) => local++; store.Changed += (_, _) => shared++;
        Assert.That(reader.SelectWhitespace(IgnoreWhitespaceKind.AllSpace), Is.False);
        Assert.That(reader.Options.IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.None)); Assert.That(store.Load().IgnoreWhitespace, Is.EqualTo(IgnoreWhitespaceKind.None));
        Assert.That(local + shared, Is.Zero); Assert.That(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"), Is.Empty);
    }

    [Test]
    public async Task ARealNewProcessReadsOnlyTheExplicitlySavedAppearanceDefaultRegardlessOfRemember()
    {
        const string directoryVariable = "GITEXTENSIONS_COMPARISON_PREFS_PROBE_DIRECTORY", expectedVariable = "GITEXTENSIONS_COMPARISON_PREFS_PROBE_APPEARANCE";
        string? probe = Environment.GetEnvironmentVariable(directoryVariable);
        if (probe is not null)
        {
            DiffViewerPreferencesStore fresh = new(probe);
            Assert.That(fresh.BeginViewerSession().Appearance, Is.EqualTo(Enum.Parse<DiffViewerAppearance>(Environment.GetEnvironmentVariable(expectedVariable)!)));
            return;
        }
        using DiffViewerPreferencesTests.TemporaryDirectory directory = new(); DiffViewerPreferencesStore store = new(directory.Path);
        Assert.That(store.Save(new(Appearance: DiffViewerAppearance.GitWordDiff, RememberAppearance: true)), Is.True);
        await Probe(DiffViewerAppearance.Patch);
        Assert.That(store.SaveCurrentViewSettingsAsDefault(), Is.True); await Probe(DiffViewerAppearance.GitWordDiff);
        Assert.That(store.Save(store.Load() with { Appearance = DiffViewerAppearance.Difftastic, RememberAppearance = false }), Is.True); await Probe(DiffViewerAppearance.GitWordDiff);
        async Task Probe(DiffViewerAppearance expected)
        {
            ProcessStartInfo start = TestDotnetHost.CreateStartInfo();
            foreach (string argument in new[] { "test", "tests/linux/GitExtensions.Desktop.Tests/GitExtensions.Desktop.Tests.csproj", "-c", "Release", "--no-build", "--no-restore", "--filter",
                "FullyQualifiedName~GitExtensions.Desktop.Tests.DiffViewerComparisonPreferencesTests.ARealNewProcessReadsOnlyTheExplicitlySavedAppearanceDefaultRegardlessOfRemember", "--", "NUnit.NumberOfTestWorkers=0" }) { start.ArgumentList.Add(argument); }
            start.Environment[directoryVariable] = directory.Path; start.Environment[expectedVariable] = expected.ToString();
            using Process process = Process.Start(start)!; Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); } catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); } throw; }
            Assert.That(process.ExitCode, Is.Zero, await output + await error);
        }
    }

    private static JsonElement Read(DiffViewerPreferencesStore store, string property)
    { using JsonDocument json = JsonDocument.Parse(File.ReadAllText(store.FilePath)); return json.RootElement.GetProperty(property).Clone(); }
}

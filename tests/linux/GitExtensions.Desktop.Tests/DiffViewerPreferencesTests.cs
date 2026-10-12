using System.Diagnostics;
using System.Text.Json;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class DiffViewerPreferencesTests
{
    [Test]
    public void SharedSessionOptionsStayLiveAndNewViewersReloadPatchUnlessRemembered()
    {
        using TemporaryDirectory temporary = new();
        DiffViewerPreferencesStore first = new(temporary.Path), second = new(temporary.Path + "/.");
        Assert.That(first.Load(), Is.EqualTo(new DiffViewerPreferences()));
        int firstChanges = 0, secondChanges = 0;
        first.Changed += (_, _) => firstChanges++; second.Changed += (_, _) => secondChanges++;
        DiffViewerPreferences word = new(false, false, DiffViewerAppearance.GitWordDiff);
        Assert.That(first.Save(word), Is.True);
        Assert.That(second.Load(), Is.EqualTo(word), "Ordinary reads and additional stores must retain the live appearance.");
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(first.FilePath));
        Assert.That(json.RootElement.GetProperty("Appearance").GetInt32(), Is.Zero, "Non-remembered appearance must not become a future session default.");
        Assert.That(second.BeginViewerSession(), Is.EqualTo(word with { Appearance = DiffViewerAppearance.Patch }));
        Assert.That(first.Load().Appearance, Is.EqualTo(DiffViewerAppearance.Patch));
        Assert.That(firstChanges, Is.EqualTo(2)); Assert.That(secondChanges, Is.EqualTo(2));
        Assert.That(second.Save(word with { RememberAppearance = true }), Is.True);
        Assert.That(first.BeginViewerSession().Appearance, Is.EqualTo(DiffViewerAppearance.GitWordDiff));
        Assert.That(firstChanges, Is.EqualTo(3)); Assert.That(secondChanges, Is.EqualTo(3));
        Assert.That(Directory.GetFiles(System.IO.Path.GetDirectoryName(first.FilePath)!, "*.tmp"), Is.Empty);
    }

    [TestCase("{broken")]
    [TestCase("null")]
    [TestCase("{\"UseGitColoring\":null}")]
    [TestCase("{\"Appearance\":null}")]
    public void CorruptOrNullSettingsRecoverOriginalDefaultsWithoutPreservingASessionChoice(string content)
    {
        using TemporaryDirectory temporary = new(); DiffViewerPreferencesStore store = new(temporary.Path);
        Assert.That(store.Save(new(Appearance: DiffViewerAppearance.Difftastic)), Is.True);
        File.WriteAllText(store.FilePath, content);
        Assert.That(store.Load(), Is.EqualTo(new DiffViewerPreferences()));
        Assert.That(store.Save(new()), Is.True);
        using JsonDocument recovered = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Assert.That(recovered.RootElement.GetProperty("UseGitColoring").GetBoolean(), Is.True);
    }

    [Test]
    public void UnknownAppearancePreservesValidColorOptionsAndFailedSaveDoesNotPublishOrChangeRuntime()
    {
        using TemporaryDirectory temporary = new(); DiffViewerPreferencesStore store = new(temporary.Path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{\"UseGitColoring\":false,\"ReverseGitColoring\":false,\"Appearance\":83,\"RememberAppearance\":true}");
        Assert.That(store.Load(), Is.EqualTo(new DiffViewerPreferences(false, false, RememberAppearance: true)));
        using TemporaryDirectory blocked = new(); Directory.CreateDirectory(blocked.Path);
        File.WriteAllText(System.IO.Path.Combine(blocked.Path, "gitextensions"), "not a directory");
        DiffViewerPreferencesStore failure = new(blocked.Path); int events = 0; failure.Changed += (_, _) => events++;
        Assert.That(failure.Save(new(false, false, DiffViewerAppearance.GitWordDiff)), Is.False);
        Assert.That(events, Is.Zero); Assert.That(failure.Load(), Is.EqualTo(new DiffViewerPreferences()));
    }

    [Test]
    public async Task NewProcessReadsOnlyPersistedAppearance()
    {
        const string probeDirectoryVariable = "GITEXTENSIONS_DIFF_VIEWER_PROBE_DIRECTORY";
        string? probeDirectory = Environment.GetEnvironmentVariable(probeDirectoryVariable);
        if (probeDirectory is not null)
        {
            DiffViewerPreferences loaded = new DiffViewerPreferencesStore(probeDirectory).Load();
            Assert.That(loaded.UseGitColoring || loaded.ReverseGitColoring, Is.False);
            Assert.That(loaded.Appearance, Is.EqualTo(loaded.RememberAppearance ? DiffViewerAppearance.GitWordDiff : DiffViewerAppearance.Patch));
            return;
        }

        using TemporaryDirectory temporary = new(); DiffViewerPreferencesStore store = new(temporary.Path);
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(System.IO.Path.Combine(root.FullName, ".dotnet", "dotnet"))) { root = root.Parent; }
        DirectoryInfo repositoryRoot = root ?? throw new AssertionException("A fresh process must execute with the repository's actual .NET runtime.");
        foreach (bool remember in new[] { false, true })
        {
            Assert.That(store.Save(new(false, false, DiffViewerAppearance.GitWordDiff, remember)), Is.True);
            Assert.That(store.Load().Appearance, Is.EqualTo(DiffViewerAppearance.GitWordDiff));
            ProcessStartInfo start = new(System.IO.Path.Combine(repositoryRoot.FullName, ".dotnet", "dotnet"))
            { WorkingDirectory = repositoryRoot.FullName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "test", "tests/linux/GitExtensions.Desktop.Tests/GitExtensions.Desktop.Tests.csproj", "-c", "Release", "--no-build", "--no-restore",
                "--filter", "FullyQualifiedName~GitExtensions.Desktop.Tests.DiffViewerPreferencesTests.NewProcessReadsOnlyPersistedAppearance", "--", "NUnit.NumberOfTestWorkers=0" }) { start.ArgumentList.Add(argument); }
            start.Environment[probeDirectoryVariable] = temporary.Path;
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); } throw; }
            Assert.That(process.ExitCode, Is.Zero, await output + await error);
        }
    }

    internal sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-diff-viewer-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); } }
    }
}

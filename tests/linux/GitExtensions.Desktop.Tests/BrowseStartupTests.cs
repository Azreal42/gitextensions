using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using static GitExtensions.Desktop.Tests.ReferenceDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class BrowseStartupTests
{
    private const string WorkingTreeId = "1111111111111111111111111111111111111111";
    private const string IndexId = "2222222222222222222222222222222222222222";

    [TestCase("HEAD~1", GitCompareRevisionKind.Commit)]
    [TestCase(WorkingTreeId, GitCompareRevisionKind.WorkingTree)]
    [TestCase(IndexId, GitCompareRevisionKind.Index)]
    public void WindowsBrowseArgumentsKeepPathAndTypedSelectionSeparate(string revision, GitCompareRevisionKind kind)
    {
        const string literal = "dir/= [*] λ'\tline\nfile.txt";
        DesktopStartupArguments parsed = DesktopStartupArguments.Parse(["browse", "repo", "--pathFilter=" + literal, "-commit=" + revision]);
        Assert.That(parsed.RepositoryArgument, Is.EqualTo("repo"));
        Assert.That(parsed.BrowseRequest!.PathFilter, Is.EqualTo(new BrowsePathFilter(literal)));
        Assert.That(parsed.BrowseRequest.SelectedRevision!.Kind, Is.EqualTo(kind));
        Assert.That(parsed.BrowseRequest.SelectedRevision.Revision, Is.EqualTo(kind == GitCompareRevisionKind.Commit ? revision : ""));
        Assert.That(parsed.BrowseRequest.FirstRevision, Is.Null, "Opening file history must not add another selection or a history bound.");
    }

    [Test]
    public void WindowsFirstAndSelectedAreSelectionsAndTheFirstMatchingOptionsWin()
    {
        DesktopStartupArguments parsed = DesktopStartupArguments.Parse(["browse", "-commit=HEAD,HEAD~1,ignored", "-commit=other", "-pathfilter=a=b", "-pathfilter=other"]);
        Assert.That(parsed.BrowseRequest!.RepositoryPath, Is.Null);
        Assert.That(parsed.BrowseRequest.SelectedRevision, Is.EqualTo(new GitCompareRevision("HEAD")));
        Assert.That(parsed.BrowseRequest.FirstRevision, Is.EqualTo(new GitCompareRevision("HEAD~1")));
        Assert.That(parsed.BrowseRequest.PathFilter!.Path, Is.EqualTo("a=b"));
        Assert.That(DesktopStartupArguments.Parse(["browse", "-commit="]).BrowseRequest!.SelectedRevision, Is.Null);
        Assert.Throws<ArgumentException>(() => DesktopStartupArguments.Parse(["browse", "-commit=HEAD,"]));
        Assert.Throws<ArgumentException>(() => DesktopStartupArguments.Parse(["browse", "-unknown=anything"]));
        Assert.Throws<ArgumentException>(() => DesktopStartupArguments.Parse(["browse", "-pathfilter-kind=directory"]));
    }

    [Test]
    public void SimpleBrowseSmokeAndCleanupKeepTheirExistingArgumentContract()
    {
        Assert.That(DesktopStartupArguments.Parse(["repo", "ignored"]), Is.EqualTo(new DesktopStartupArguments(DesktopStartupMode.Browse, "repo")));
        Assert.That(DesktopStartupArguments.Parse(["--smoke-test", "repo"]), Is.EqualTo(new DesktopStartupArguments(DesktopStartupMode.Browse, "repo", true)));
        Assert.That(DesktopStartupArguments.Parse(["cleanup", "--version", "-pathfilter=ignored", "-commit=HEAD"]),
            Is.EqualTo(new DesktopStartupArguments(DesktopStartupMode.Cleanup, "--version")));
        Assert.That(DesktopStartupArguments.Parse(["browse", "repo", "--smoke-test"]).SmokeTest, Is.True);
    }

    [Test]
    public async Task PreparationDiscoversTheRepositoryResolvesTheCommitAndKeepsAnExplicitMissingDirectory()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        Directory.CreateDirectory(fixture.PathInRepository("nested"));
        await fixture.GitAsync("tag", "-a", "point", "-m", "Annotated selection");
        string head = await fixture.Repository.ResolveRevisionAsync("HEAD");
        byte[] index = await File.ReadAllBytesAsync(fixture.PathInRepository(".git/index"));
        string refs = (await fixture.GitAsync("show-ref")).StandardOutput;
        DesktopStartupRequest prepared = await DesktopStartupDispatcher.PrepareAsync(
            DesktopStartupArguments.Parse(["browse", "nested", "-pathfilter=gone literal[*]/", "-pathfilter-kind=directory", "-commit=point"]), fixture.Repository.Path);
        Assert.That(prepared.Repository!.Path, Is.EqualTo(fixture.Repository.Path));
        Assert.That(prepared.BrowseRequest!.RepositoryPath, Is.EqualTo(fixture.Repository.Path));
        Assert.That(prepared.BrowseRequest.PathFilter, Is.EqualTo(new BrowsePathFilter("gone literal[*]/", BrowsePathKind.Directory)));
        Assert.That(prepared.BrowseRequest.SelectedRevision, Is.EqualTo(new GitCompareRevision(head)));
        Assert.That(prepared.BrowseRequest.FirstRevision, Is.Null);
        Assert.That(await File.ReadAllBytesAsync(fixture.PathInRepository(".git/index")), Is.EqualTo(index));
        Assert.That((await fixture.GitAsync("show-ref")).StandardOutput, Is.EqualTo(refs));
    }

    [TestCase(WorkingTreeId, GitCompareRevisionKind.WorkingTree)]
    [TestCase(IndexId, GitCompareRevisionKind.Index)]
    public async Task PreparationUsesTheInvokingRepositoryAndPreservesArtificialSelection(string revision, GitCompareRevisionKind kind)
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        Directory.CreateDirectory(fixture.PathInRepository("folder"));
        DesktopStartupRequest directory = await DesktopStartupDispatcher.PrepareAsync(
            DesktopStartupArguments.Parse(["browse", "-pathfilter=folder", "-commit=" + revision]), fixture.Repository.Path);
        Assert.That(directory.BrowseRequest!.PathFilter, Is.EqualTo(new BrowsePathFilter("folder", BrowsePathKind.Directory)));
        Assert.That(directory.BrowseRequest.SelectedRevision, Is.EqualTo(new GitCompareRevision("", kind)));
        DesktopStartupRequest file = await DesktopStartupDispatcher.PrepareAsync(
            DesktopStartupArguments.Parse(["browse", "-pathfilter=" + fixture.File]), fixture.Repository.Path);
        Assert.That(file.BrowseRequest!.PathFilter, Is.EqualTo(new BrowsePathFilter("file.txt", BrowsePathKind.File)));
        Assert.That(file.BrowseRequest.SelectedRevision, Is.Null);
    }

    [Test]
    public async Task InvalidOrCancelledPreparationCannotTurnIntoAnotherRepositoryOrSelection()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        Assert.ThrowsAsync<GitCommandException>(() => DesktopStartupDispatcher.PrepareAsync(
            DesktopStartupArguments.Parse(["browse", fixture.Repository.Path, "-commit=missing-selected"]), fixture.Repository.Path));
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(() => DesktopStartupDispatcher.PrepareAsync(
            DesktopStartupArguments.Parse(["browse", fixture.Repository.Path, "-commit=HEAD"]), fixture.Repository.Path, cancellation.Token));
        Assert.ThrowsAsync<InvalidOperationException>(() => DesktopStartupDispatcher.PrepareAsync(
            DesktopStartupArguments.Parse(["browse", fixture.Output("not-a-repository")]), fixture.Repository.Path));
    }

    [Test]
    public async Task LauncherPreservesLiteralArgvBothForApphostAndDotnetAndNeverAddsAFileHistoryBound()
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string application = fixture.Output("browse application"); Directory.CreateDirectory(application);
        string apphost = Path.Combine(application, OperatingSystem.IsWindows() ? "GitExtensions.exe" : "GitExtensions");
        await File.WriteAllTextAsync(apphost, "only inspected, never executed");
        const string path = "literal [*]= λ'\tline\nfile";
        BrowseRequest request = new(fixture.Repository.Path, new(path, BrowsePathKind.File), new("", GitCompareRevisionKind.Index));
        string[] expected = ["browse", fixture.Repository.Path, "--pathFilter=" + path, "-pathfilter-kind=file", "-commit=" + IndexId];
        ProcessStartInfo installed = BrowseApplicationLauncher.CreateStartInfo(request, application);
        Assert.That(installed.FileName, Is.EqualTo(apphost)); Assert.That(installed.ArgumentList, Is.EqualTo(expected));
        Assert.That(installed.UseShellExecute, Is.False); Assert.That(installed.WorkingDirectory, Is.EqualTo(fixture.Repository.Path));
        DesktopStartupArguments roundTrip = DesktopStartupArguments.Parse(installed.ArgumentList.ToArray());
        Assert.That(roundTrip.BrowseRequest, Is.EqualTo(request));
        Assert.That(BrowseApplicationLauncher.CreateStartInfo(fixture.Repository.Path, application).ArgumentList, Is.EqualTo(new[] { fixture.Repository.Path }));
        File.Delete(apphost);
        ProcessStartInfo development = BrowseApplicationLauncher.CreateStartInfo(request, application);
        Assert.That(Path.GetFileNameWithoutExtension(development.FileName), Is.EqualTo("dotnet"));
        Assert.That(development.ArgumentList.First(), Does.EndWith("GitExtensions.dll"));
        Assert.That(development.ArgumentList.Skip(1), Is.EqualTo(expected));
        Assert.Throws<ArgumentException>(() => BrowseApplicationLauncher.CreateStartInfo(request with { RepositoryPath = "relative" }, application));
        Assert.Throws<ArgumentException>(() => BrowseApplicationLauncher.CreateStartInfo(request with { SelectedRevision = null, FirstRevision = new("HEAD") }, application));
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public async Task IndependentProcessReceivesTheTypedRequestAndOutlivesTheLaunchingCall()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("This executable fixture uses a Linux shebang."); return; }
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        string application = fixture.Output("literal app"); Directory.CreateDirectory(application);
        string apphost = Path.Combine(application, "GitExtensions"), marker = apphost + ".started", release = apphost + ".release";
        await File.WriteAllTextAsync(apphost, "#!/usr/bin/python3\nimport os,sys,json,time\nwith open(__file__+'.started.tmp','w') as f: json.dump({'pid':os.getpid(),'cwd':os.getcwd(),'argv':sys.argv[1:]},f)\nos.replace(__file__+'.started.tmp',__file__+'.started')\nwhile not os.path.exists(__file__+'.release'): time.sleep(.01)\n");
        File.SetUnixFileMode(apphost, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        BrowseRequest request = new(fixture.Repository.Path, new("literal = [*]'\tline\nfile", BrowsePathKind.File), new("", GitCompareRevisionKind.WorkingTree));
        Process? child = null;
        try
        {
            using CancellationTokenSource cancelled = new(); cancelled.Cancel();
            Assert.CatchAsync<OperationCanceledException>(() => BrowseApplicationLauncher.OpenAsync(request, cancelled.Token, application));
            Assert.That(File.Exists(marker), Is.False);
            await BrowseApplicationLauncher.OpenAsync(request, CancellationToken.None, application);
            await Until(() => File.Exists(marker));
            using JsonDocument data = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
            child = Process.GetProcessById(data.RootElement.GetProperty("pid").GetInt32());
            Assert.That(data.RootElement.GetProperty("cwd").GetString(), Is.EqualTo(fixture.Repository.Path));
            Assert.That(data.RootElement.GetProperty("argv").EnumerateArray().Select(value => value.GetString()),
                Is.EqualTo(BrowseApplicationLauncher.CreateStartInfo(request, application).ArgumentList));
            Assert.That(child.HasExited, Is.False, "Browse is an independent process, not awaited or killed after the launch call.");
        }
        finally
        {
            await File.WriteAllTextAsync(release, "normal exit");
            if (child is null && File.Exists(marker))
            { using JsonDocument data = JsonDocument.Parse(await File.ReadAllTextAsync(marker)); child = Process.GetProcessById(data.RootElement.GetProperty("pid").GetInt32()); }
            if (child is not null)
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.That(child.HasExited, Is.True); TestContext.Out.WriteLine("independent Browse fixture terminated, pid=" + child.Id); child.Dispose();
            }
        }
    }

    [Test]
    public void FileHistoryPreferenceDefaultsToBrowseAndRoundTripsWithoutChangingOtherPreferences()
    {
        string root = Path.Combine(Path.GetTempPath(), "ge-browse-preference-" + Guid.NewGuid().ToString("N"));
        try
        {
            PreferencesStore store = new(root); Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, "{\"Theme\":\"Dark\",\"HistoryLimit\":173,\"RecentRepositories\":[\"literal [*] path\"]}");
            DesktopPreferences original = store.Load(); Assert.That(original.UseBrowseForFileHistory, Is.True);
            store.Save(original with { UseBrowseForFileHistory = false });
            DesktopPreferences loaded = store.Load(); Assert.That(loaded.UseBrowseForFileHistory, Is.False);
            Assert.That(loaded.Theme, Is.EqualTo(original.Theme)); Assert.That(loaded.HistoryLimit, Is.EqualTo(original.HistoryLimit));
            Assert.That(loaded.RecentRepositories, Is.EqualTo(original.RecentRepositories));
            Assert.That(loaded.ReferenceSorting, Is.EqualTo(original.ReferenceSorting));
            using JsonDocument data = JsonDocument.Parse(File.ReadAllText(store.FilePath));
            Assert.That(data.RootElement.TryGetProperty("BrowseRequest", out _), Is.False, "Temporary file-history panel state must not be saved with ordinary settings.");
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task SettingsReturnsTheOrdinaryPreferenceOnlyOnOk(bool accept)
    {
        using ReferenceFixture fixture = await ReferenceFixture.CreateAsync();
        DesktopPreferences initial = new() { HistoryLimit = 173 };
        PreferencesStore store = new(); store.Save(initial); byte[] before = await File.ReadAllBytesAsync(store.FilePath);
        Window owner = new(); owner.Show();
        DesktopSettingsDialog dialog = new(initial, TranslationCatalog.LoadDefault(), false);
        Task<bool> result = dialog.ShowDialog<bool>(owner);
        try
        {
            CheckBox choice = Field<CheckBox>(dialog, "SettingsUseBrowseForFileHistory");
            Assert.That(choice.IsChecked, Is.True);
            PushProcessTestDriver.Pointer(choice);
            Assert.That(choice.IsChecked, Is.False);
            PushProcessTestDriver.Pointer(Field<Button>(dialog, accept ? "SaveDesktopSettings" : "CancelDesktopSettings"));
            Assert.That(await result, Is.EqualTo(accept));
            Assert.That(dialog.Result.UseBrowseForFileHistory, Is.EqualTo(!accept));
            Assert.That(dialog.Result.HistoryLimit, Is.EqualTo(173));
            Assert.That(await File.ReadAllBytesAsync(store.FilePath), Is.EqualTo(before), "The owner persists after acceptance, not the settings window itself.");
        }
        finally { if (dialog.IsVisible) { dialog.Close(false); } owner.Close(); }
    }

    private static T Field<T>(Window window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>()
        .Concat(window.GetVisualDescendants().OfType<T>()).Distinct().Single(control => control.Name == name);

    private static async Task Until(Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(10); }
        Assert.That(condition(), Is.True, "The real independent process did not publish its startup marker.");
    }
}

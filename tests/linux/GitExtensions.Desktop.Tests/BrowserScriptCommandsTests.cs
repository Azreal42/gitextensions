using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class BrowserScriptCommandsTests
{
    [AvaloniaTest]
    public async Task ToolbarPointerAndRenamedDisabledScriptHotkeyUseTheSameCapturedRevisionContext()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", true);
        MainWindow main = f.Window;
        await PushProcessTestDriver.UntilAsync(main, () => !main.IsBusy);
        ListBox history = main.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "RevisionHistory");
        GitCommit selected = history.Items.OfType<GitCommit>().Last(); history.SelectedItem = selected;
        await PushProcessTestDriver.UntilAsync(main, () => !main.IsBusy);
        string scriptPath = Path.Combine(Path.GetDirectoryName(f.Repository)!, "capture.py");
        string output = Path.Combine(Path.GetDirectoryName(f.Repository)!, "captured.txt");
        await File.WriteAllTextAsync(scriptPath, "import sys\nopen(sys.argv[1],'w').write('\\n'.join(sys.argv[2:]))\n");
        ScriptDefinition script = new() { Name = "&Capture", Command = "/usr/bin/python3", Arguments = Quote(scriptPath) + " " + Quote(output) + " {{sHash}} {{cHash}}",
            OnEvent = ScriptEvent.ShowInUserMenuBar, AddToRevisionGridContextMenu = true, Icon = "Console", HotkeyCommandIdentifier = 9017 };
        ScriptsPreferencesStore scripts = new(); scripts.Save([script]);
        HotkeyPreferencesStore hotkeys = new(); HotkeyBindings bindings = hotkeys.Load();
        bindings.Set("Scripts", 9017, new(Key.F10, KeyModifiers.Control)); hotkeys.Save(bindings);
        Button toolbar = main.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ScriptToolbar_9017");
        Assert.That(history.ContextMenu!.Items.OfType<MenuItem>().Count(item => item.Name == "UserScript_9017"), Is.EqualTo(1));
        PushProcessTestDriver.Pointer(toolbar);
        await PushProcessTestDriver.UntilAsync(main, () => File.Exists(output) && !main.IsBusy);
        string[] expected = [selected.Hash, history.Items.OfType<GitCommit>().First().Hash];
        Assert.That(File.ReadAllLines(output), Is.EqualTo(expected));
        Assert.That(main.LastException, Is.Null);
        File.Delete(output);
        scripts.Save([script with { Name = "&Renamed", Enabled = false }]);
        Assert.That(history.ContextMenu.Items.OfType<MenuItem>().Any(item => item.Name == "UserScript_9017"), Is.False);
        history.Focus();
        main.KeyPressQwerty(PhysicalKey.F10, RawInputModifiers.Control);
        main.KeyReleaseQwerty(PhysicalKey.F10, RawInputModifiers.Control);
        await PushProcessTestDriver.UntilAsync(main, () => File.Exists(output) && !main.IsBusy);
        Assert.That(File.ReadAllLines(output), Is.EqualTo(expected));
        Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(selected.Hash));
        Assert.That(main.LastException, Is.Null);
    }
    [AvaloniaTest]
    public async Task NavigateToUsesStdoutAfterANonzeroExitAndPresentsMissingOrFilteredRevisionsWithoutVetoingTheScript()
    {
        using Fixture f = await Fixture.CreateAsync("base\n", "base\n", true);
        MainWindow main = f.Window;
        await PushProcessTestDriver.UntilAsync(main, () => !main.IsBusy);
        ListBox history = main.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "RevisionHistory");
        GitRepository repository = (GitRepository)typeof(MainWindow).GetField("_repository",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        string target = history.Items.OfType<GitCommit>().Last().Hash;
        GitResult hidden = await f.Git.RunAsync(f.Repository, ["commit-tree", "HEAD^{tree}", "-p", "HEAD", "-m", "Not in the loaded grid"]);
        Assert.That(hidden.Success, Is.True, hidden.StandardError);
        foreach ((string revision, string? errorText) in new[]
        {
            (target, (string?)null), ("refs/heads/does-not-exist", "No revision found."),
            (hidden.StandardOutput.Trim(), "not visible in the revision grid"), ("", (string?)null)
        })
        {
            ScriptDefinition script = new() { Name = "Navigate", Command = "navigateTo:/usr/bin/python3",
                Arguments = "-c " + Quote("import sys; print(sys.argv[1]); sys.exit(17)") + " " + Quote(revision) };
            Task<bool> execution = DesktopScriptExecutor.ExecuteAsync(main, repository, script);
            if (errorText is not null)
            {
                await PushProcessTestDriver.UntilAsync(main, () => execution.IsCompleted
                    || PushProcessTestDriver.Windows(main).Any(window => window.Name == "GoToCommitError" && window.IsVisible));
                Window error = PushProcessTestDriver.Windows(main).Single(window => window.Name == "GoToCommitError" && window.IsVisible);
                Assert.That(PushProcessTestDriver.Field<TextBlock>(error, "GoToCommitErrorMessage").Text, Does.Contain(errorText));
                PushProcessTestDriver.Pointer(PushProcessTestDriver.Field<Button>(error, "CloseGoToCommitError"));
            }
            await PushProcessTestDriver.CompleteAsync(execution, main);
            Assert.That(await execution, Is.True, "The Windows adapter reports navigation errors in Browse without vetoing the script.");
            Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(target));
            Assert.That(main.LastException, Is.Null);
        }
    }
    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

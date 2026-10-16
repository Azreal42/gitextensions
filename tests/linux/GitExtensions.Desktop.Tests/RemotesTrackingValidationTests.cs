using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RemotesTrackingValidationTests
{
    [AvaloniaTest]
    public async Task SaveReloadMustNotDropASecondValidatedMergeQueuedWhileFirstWriteIsRunning()
    {
        if (OperatingSystem.IsWindows()) { Assert.Ignore("This Linux UI scenario uses a POSIX wrapper around the real Git executable."); }
        string evidence = Environment.GetEnvironmentVariable("GITEXTENSIONS_RACE_ARTIFACTS")
            ?? Path.Combine(TestContext.CurrentContext.WorkDirectory, "remotes-tracking-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        using Fixture f = await Fixture.CreateAsync();
        await f.CreateRemoteAsync();
        await f.GitAsync("config", "branch.main.remote", ".");
        await f.GitAsync("config", "branch.main.merge", "refs/heads/main");
        await f.GitAsync("config", "branch.main.description", "Preserve this description\nand its second line.");
        await f.GitAsync("branch", "other");
        await f.GitAsync("tag", "preserved-tag");
        await f.GitAsync("config", "branch.other.remote", "origin");
        await f.GitAsync("config", "branch.other.merge", "refs/heads/main");
        await f.GitAsync("config", "remote.origin.pushurl", "ssh://example.invalid/team/push.git");
        await f.GitAsync("config", "remote.origin.prefix", "team/");
        await f.GitAsync("config", "remote.origin.color", "#123456");
        await f.GitAsync("config", "--add", "remote.origin.review", "first opaque value");
        await f.GitAsync("config", "--add", "remote.origin.review", "second opaque value");
        string beforeConfig = (await f.GitAsync("config", "--local", "--null", "--list")).StandardOutput;
        string beforeRefs = (await f.GitAsync("show-ref", "--head")).StandardOutput;
        string description = await File.ReadAllTextAsync(f.PathInRepository(".git/description"));
        string gate = Path.Combine(evidence, "first-entered");
        string release = Path.Combine(evidence, "first-release");
        string calls = Path.Combine(evidence, "git-calls.log");
        string timeline = Path.Combine(evidence, "ui-timeline.log");
        foreach (string file in new[] { gate, release, calls, timeline }) { File.Delete(file); }
        string wrapper = f.Output("git-wrapper");
        await File.WriteAllTextAsync(wrapper, $$"""
            #!/bin/sh
            printf '%s\t' "$@" >> '{{calls}}'
            printf '\n' >> '{{calls}}'
            case "$*" in
              *'check-ref-format refs/heads/first-write'*)
                : > '{{gate}}'
                while [ ! -f '{{release}}' ]; do sleep 0.01; done
                ;;
            esac
            exec /usr/bin/git "$@"
            """);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        GitRepository repository = new(f.Repository.Path, new GitRunner(wrapper));
        RemotesDialog dialog = new(repository, preselectLocalBranch: "main");
        dialog.Show(); await dialog.InitializeAsync(); Pump(dialog);
        ComboBox merge = Field<ComboBox>(dialog, "TrackingMergeBranch");
        TextBox editor = merge.GetVisualDescendants().OfType<TextBox>().Single();
        void Record(string stage) => File.AppendAllText(timeline, stage + " busy=" + dialog.IsBusy
            + " writes=" + Writes(dialog).Status + " merge=" + merge.Text + " editor=" + editor.Text
            + " dirty=" + Private(dialog, "_mergeDirty") + " error=" + dialog.LastError + Environment.NewLine);
        try
        {
            Assert.That(dialog.LastError, Is.Null);
            Type(dialog, editor, "first-write"); Record("typed-A");
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveBranchTracking"));
            await Until(() => File.Exists(gate), "A did not reach its real native check-ref-format barrier");
            Record("A-blocked-after-first-save");
            Assert.That(dialog.IsBusy, Is.True);
            Assert.That(merge.IsEffectivelyEnabled, Is.True);
            Type(dialog, editor, "second-write"); Record("typed-B");
            PushProcessTestDriver.Pointer(Field<TextBox>(dialog, "TrackingLocalBranch"));
            Record("B-validated-by-real-focus-loss");
            Assert.That((bool)Private(dialog, "_mergeDirty")!, Is.False, "B was captured for validation");
            File.WriteAllText(release, "release real native Git A\n");
            await Until(() => !dialog.IsBusy && Writes(dialog).IsCompleted, "All tracking writes and save reload must drain");
            Pump(dialog);
            Record("drained-without-third-click");
            GitResult actual = await f.GitAsync("config", "--get", "branch.main.merge");
            string afterConfig = (await f.GitAsync("config", "--local", "--null", "--list")).StandardOutput;
            string afterRefs = (await f.GitAsync("show-ref", "--head")).StandardOutput;
            string config = await File.ReadAllTextAsync(f.PathInRepository(".git/config"));
            File.WriteAllText(Path.Combine(evidence, "final-config"), config);
            File.WriteAllText(Path.Combine(evidence, "before-config-records"), beforeConfig);
            File.WriteAllText(Path.Combine(evidence, "after-config-records"), afterConfig);
            File.WriteAllText(Path.Combine(evidence, "before-refs"), beforeRefs);
            File.WriteAllText(Path.Combine(evidence, "after-refs"), afterRefs);
            File.WriteAllText(Path.Combine(evidence, "result.txt"), "actual=" + actual.StandardOutput.Trim()
                + "\nexpected=refs/heads/second-write\nLastError=" + dialog.LastError + "\nUI=" + merge.Text + "\n");
            foreach (string file in Directory.GetFiles(evidence)) { TestContext.AddTestAttachment(file); }
            Assert.That(dialog.LastError, Is.Null);
            Assert.That(OtherConfig(afterConfig), Is.EqualTo(OtherConfig(beforeConfig)),
                "Tracking Save must preserve every other key, including descriptions, URL overrides, opaque multivalues and the other branch.");
            Assert.That(afterRefs, Is.EqualTo(beforeRefs), "All local, remote and tag references must remain unchanged.");
            Assert.That(await File.ReadAllTextAsync(f.PathInRepository(".git/description")), Is.EqualTo(description));
            Assert.That(actual.StandardOutput.Trim(), Is.EqualTo("refs/heads/second-write"),
                "The second validated value must persist without requiring a third edit or Save click. Evidence: " + evidence);
            Assert.That(merge.Text, Is.EqualTo("second-write"));
            GitBranchTrackingSnapshot row = Field<ListBox>(dialog, "BranchTrackingList").Items.OfType<GitBranchTrackingSnapshot>().Single(item => item.LocalBranch == "main");
            Assert.That(row.MergeRef, Is.EqualTo("refs/heads/second-write"));
            string nativeCalls = await File.ReadAllTextAsync(calls);
            Assert.That(nativeCalls, Does.Contain("\tcheck-ref-format\trefs/heads/second-write\t"), "B must reach the real Git validation.");
            Assert.That(nativeCalls, Does.Contain("\t--add\t--\tbranch.main.merge\trefs/heads/second-write\t"), "B must reach the real Git configuration edit.");
        }
        finally
        {
            File.WriteAllText(release, "cleanup\n");
            await Until(() => !dialog.IsBusy && Writes(dialog).IsCompleted, "cleanup must drain");
            if (dialog.IsVisible) { dialog.Close(); }
            await Until(() => !dialog.IsVisible && !dialog.IsBusy, "cleanup close");
        }
    }

    [AvaloniaTest]
    public async Task SaveWithoutDirtyFieldsRefreshesExternalTrackingAndRebasesTheNextValidatedWrite()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.CreateRemoteAsync();
        await f.GitAsync("config", "branch.main.remote", ".");
        await f.GitAsync("config", "branch.main.merge", "refs/heads/main");
        await f.GitAsync("config", "branch.main.description", "Preserve tracking description.");
        string beforeRefs = (await f.GitAsync("show-ref", "--head")).StandardOutput;
        RemotesDialog dialog = new(f.Repository, preselectLocalBranch: "main");
        dialog.Show(); await dialog.InitializeAsync(); Pump(dialog);
        ComboBox merge = Field<ComboBox>(dialog, "TrackingMergeBranch");
        TextBox editor = merge.GetVisualDescendants().OfType<TextBox>().Single();
        try
        {
            Assert.That(dialog.LastError, Is.Null);
            Assert.That((bool)Private(dialog, "_mergeDirty")!, Is.False);
            await f.GitAsync("config", "branch.main.merge", "refs/heads/external-update");
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "SaveBranchTracking"));
            await Until(() => !dialog.IsBusy && Writes(dialog).IsCompleted
                && merge.Text == "external-update", "Save with no dirty fields must reload the externally updated configuration");
            Assert.That(dialog.LastError, Is.Null);
            GitBranchTrackingSnapshot row = Field<ListBox>(dialog, "BranchTrackingList").Items.OfType<GitBranchTrackingSnapshot>().Single(item => item.LocalBranch == "main");
            Assert.That(row.MergeRef, Is.EqualTo("refs/heads/external-update"));
            Type(dialog, editor, "after-external-refresh");
            PushProcessTestDriver.Pointer(Field<TextBox>(dialog, "TrackingLocalBranch"));
            await Until(() => !dialog.IsBusy && Writes(dialog).IsCompleted, "The next validation must use the refreshed snapshot");
            Assert.That(dialog.LastError, Is.Null);
            Assert.That((await f.GitAsync("config", "--get", "branch.main.merge")).StandardOutput.Trim(), Is.EqualTo("refs/heads/after-external-refresh"));
            Assert.That((await f.GitAsync("config", "--get", "branch.main.description")).StandardOutput.Trim(), Is.EqualTo("Preserve tracking description."));
            Assert.That((await f.GitAsync("config", "--get", "branch.main.remote")).StandardOutput.Trim(), Is.EqualTo("."));
            Assert.That((await f.GitAsync("show-ref", "--head")).StandardOutput, Is.EqualTo(beforeRefs));
        }
        finally
        {
            await Until(() => !dialog.IsBusy && Writes(dialog).IsCompleted, "cleanup tracking refresh");
            if (dialog.IsVisible) { dialog.Close(); }
            await Until(() => !dialog.IsVisible && !dialog.IsBusy, "cleanup refresh close");
        }
    }

    private static string[] OtherConfig(string config) => config.Split('\0', StringSplitOptions.RemoveEmptyEntries)
        .Where(record => !record.StartsWith("branch.main.merge\n", StringComparison.Ordinal)).ToArray();

    private static object? Private(RemotesDialog dialog, string name) => typeof(RemotesDialog).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog);
    private static Task Writes(RemotesDialog dialog) => (Task)Private(dialog, "_trackingWrites")!;
    private static T Field<T>(TopLevel owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Type(Window owner, TextBox editor, string text)
    {
        PushProcessTestDriver.Pointer(editor);
        owner.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        owner.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control);
        owner.KeyTextInput(text); Pump(owner);
        Assert.That(editor.Text, Is.EqualTo(text));
    }
    private static void Pump(TopLevel owner)
    { Dispatcher.UIThread.RunJobs(); owner.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition, string message)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, message);
    }
}

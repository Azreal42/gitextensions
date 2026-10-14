using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Plugins;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class FindLargeFilesDialogTests
{
    [AvaloniaTest]
    public async Task SevenOriginalColumnsRenderNativeBlobDataAndSortingPreservesCheckedObjectsInBothThemes()
    {
        await using Fixture f = await Fixture.Create(); await f.Git("repack", "-ad");
        FindLargeFilesDialog dialog = await f.Show(); string before = f.Materials();
        Assert.That(dialog.VisibleObjects, Has.Count.EqualTo(5));
        Assert.That(Field<Button>(dialog, "DeleteLargeFiles").IsEnabled, Is.False);
        PluginRow large = dialog.VisibleObjects.Single(row => row.Cells[1] == Fixture.LargePath);
        Assert.That(large.Cells[2], Is.EqualTo((12 * 1048576).ToString(CultureInfo.InvariantCulture))); Assert.That(large.Cells[4], Is.EqualTo("2"));
        Assert.That(DateTimeOffset.Parse(large.Cells[5], CultureInfo.InvariantCulture), Is.EqualTo(Fixture.LastDate));
        Assert.That(long.Parse(large.Cells[3], CultureInfo.InvariantCulture), Is.GreaterThan(0).And.LessThan(12 * 1048576));
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            dialog.RequestedThemeVariant = theme; dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight; Pump(dialog);
            Assert.That(Field<Grid>(dialog, "FindLargeFilesHeaders").Children, Has.Count.EqualTo(7));
            Grid row = Row(dialog, large.Key); Assert.That(row.Children, Has.Count.EqualTo(7)); Assert.That(row.Bounds.Height, Is.EqualTo(24));
            TextBlock sha = row.Children.OfType<TextBlock>().Single(text => text.Name == "FindLargeFilesCell0");
            Assert.That(sha.Text, Is.EqualTo(large.Key[..7])); Assert.That(ToolTip.GetTip(sha), Is.EqualTo(large.Key));
            Assert.That(row.Children.OfType<TextBlock>().Single(text => text.Name == "FindLargeFilesCell2").Text, Is.EqualTo("12.00 Mb"));
            Assert.That(row.Children[1].Bounds.Width, Is.GreaterThan(row.Children[0].Bounds.Width * 2));
            foreach (string name in new[] { "FindLargeFilesMinimum", "FindLargeFilesRevision", "AnalyzeLargeFiles", "DeleteLargeFiles", "CloseLargeFiles" })
            {
                Control control = Field<Control>(dialog, name); Rect bounds = new(control.TranslatePoint(default, dialog)!.Value, control.Bounds.Size);
                Assert.That(control.IsEffectivelyVisible && bounds.Width > 0 && bounds.Height > 0 && new Rect(default, dialog.ClientSize).Contains(bounds), Is.True, name);
            }
            Assert.That(Field<ProgressBar>(dialog, "FindLargeFilesProgress").IsVisible, Is.False);
            Control delete = Field<Button>(dialog, "DeleteLargeFiles"), close = Field<Button>(dialog, "CloseLargeFiles");
            Assert.That(delete.TranslatePoint(default, dialog)!.Value.Y, Is.EqualTo(close.TranslatePoint(default, dialog)!.Value.Y));
            Assert.That(Check(dialog, large.Key).TranslatePoint(default, row)!.Value.X, Is.GreaterThan(row.Children[5].Bounds.Right));
            Capture(dialog, "find-large-files-grid-" + theme + ".png");
        }
        Pointer(Check(dialog, large.Key)); Assert.That(dialog.SelectedPaths, Is.EqualTo(new[] { Fixture.LargePath }));
        Pointer(Field<Button>(dialog, "FindLargeFilesSort1"));
        Assert.That(dialog.VisibleObjects.Select(row => row.Cells[1]), Is.Ordered.Using<string>(StringComparer.Ordinal));
        Pointer(Field<Button>(dialog, "FindLargeFilesSort2"));
        Assert.That(dialog.VisibleObjects.Select(row => long.Parse(row.Cells[2], CultureInfo.InvariantCulture)), Is.Ordered);
        await RevealObject(dialog, large.Key);
        Assert.That(Check(dialog, large.Key).IsChecked, Is.True); Assert.That(Field<Button>(dialog, "DeleteLargeFiles").IsEnabled, Is.True);
        Assert.That(dialog.SelectedPaths, Is.EqualTo(new[] { Fixture.LargePath }));
        await dialog.InitializeAsync(); await RevealObject(dialog, large.Key); Assert.That(Check(dialog, large.Key).IsChecked, Is.True);
        Assert.That(dialog.SelectedPaths, Is.EqualTo(new[] { Fixture.LargePath }));
        Assert.That(f.Materials(), Is.EqualTo(before)); Assert.That(dialog.HasRepositoryChanges, Is.False);
    }

    [AvaloniaTest]
    public async Task EmptySelectionAndOwnedNoConfirmationLeaveRepositoryAndCheckedRowsUntouched()
    {
        await using Fixture f = await Fixture.Create(); FindLargeFilesDialog dialog = await f.Show(); string before = f.Materials();
        Assert.That(Field<Button>(dialog, "DeleteLargeFiles").IsEnabled, Is.False); Assert.That(dialog.OwnedWindows, Is.Empty);
        PluginRow large = dialog.VisibleObjects.Single(row => row.Cells[1] == Fixture.LargePath); Pointer(Check(dialog, large.Key));
        Pointer(Field<Button>(dialog, "DeleteLargeFiles")); Window confirmation = await Confirm(dialog);
        Assert.That(confirmation.Owner, Is.SameAs(dialog)); Assert.That(confirmation.RequestedThemeVariant, Is.EqualTo(dialog.ActualThemeVariant));
        Assert.That(confirmation.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Fixture.LargePath), Is.True);
        Pointer(Field<Button>(confirmation, "CancelLargeFilesDelete")); await Idle(dialog);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.LastRemoval, Is.Null); Assert.That(dialog.LastProcess, Is.Null);
        Assert.That(dialog.SelectedPaths, Is.EqualTo(new[] { Fixture.LargePath })); Assert.That(Check(dialog, large.Key).IsChecked, Is.True);
        Assert.That(dialog.HasRepositoryChanges, Is.False); Assert.That(f.Materials(), Is.EqualTo(before));
        Pointer(Field<Button>(dialog, "DeleteLargeFiles")); await Confirm(dialog); dialog.Close(); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.LastRemoval, Is.Null); Assert.That(f.Materials(), Is.EqualTo(before), "Closing during confirmation never hands a mutation to Git.");
    }

    [AvaloniaTest]
    public async Task ConfirmedUnionUsesNativeProcessKeepsAllPhasesAndReanalysesOnlyAfterFullSuccess()
    {
        await using Fixture f = await Fixture.Create(); await f.Git("tag", "-a", "annotated-native", "-m", "Keep native annotated tag");
        string tagged = (await f.Git("rev-parse", "annotated-native^{}")).StandardOutput.Trim();
        FindLargeFilesDialog dialog = await f.Show(); Select(dialog, Fixture.LargePath, Fixture.LiteralPath);
        Point deletePosition = FooterPosition(dialog, "DeleteLargeFiles");
        Point closePosition = FooterPosition(dialog, "CloseLargeFiles");
        string[] selected = dialog.SelectedPaths.ToArray(); Pointer(Field<Button>(dialog, "DeleteLargeFiles"));
        Pointer(Field<Button>(await Confirm(dialog), "ConfirmLargeFilesDelete")); GitProcessDialog phase = await Ready(dialog);
        GitHistoryRemovalResult removal = dialog.LastRemoval!;
        Assert.That(phase.Owner, Is.SameAs(dialog)); Assert.That(phase.UseDialogSettings, Is.True);
        ProgressBar progress = Field<ProgressBar>(dialog, "FindLargeFilesProgress"); Pump(dialog);
        Assert.That(progress.IsVisible, Is.True); Rect progressBounds = new(progress.TranslatePoint(default, dialog)!.Value, progress.Bounds.Size);
        Assert.That(progressBounds.Width > 0 && progressBounds.Height > 0 && new Rect(default, dialog.ClientSize).Contains(progressBounds), Is.True);
        Assert.That(FooterPosition(dialog, "DeleteLargeFiles"), Is.EqualTo(deletePosition));
        Assert.That(FooterPosition(dialog, "CloseLargeFiles"), Is.EqualTo(closePosition));
        Assert.That(Field<CheckBox>(phase, "KeepDialogOpen").IsChecked, Is.True); Assert.That(removal.Success, Is.True);
        Assert.That(removal.Paths, Is.EqualTo(selected)); Assert.That(removal.Steps.Count(step => step.Step == GitHistoryRemovalStep.RewriteHistory), Is.EqualTo(1));
        Assert.That(removal.Steps.Last().Step, Is.EqualTo(GitHistoryRemovalStep.GarbageCollect));
        Assert.That(phase.Result, Is.SameAs(removal.Steps.Last().Result), "Expose the last actual native result, never a composite exit result.");
        Assert.That(phase.StandardOutputBytes.ToArray(), Is.EqualTo(removal.Steps.SelectMany(step => step.StandardOutputBytes).ToArray()));
        Assert.That(phase.StandardErrorBytes.ToArray(), Is.EqualTo(removal.Steps.SelectMany(step => step.StandardErrorBytes).ToArray()));
        Assert.That(Field<TextBox>(phase, "ProcessOutput").Text, Does.Contain("was rewritten").And.Contain("annotated-native"));
        Assert.That(dialog.IsBusy, Is.True); Assert.That(dialog.VisibleObjects.Any(row => selected.Contains(row.Cells[1], StringComparer.Ordinal)), Is.True,
            "Rows stay available until the owned process is acknowledged.");
        Pointer(Field<Button>(phase, "Ok")); await Idle(dialog);
        Assert.That(Field<ProgressBar>(dialog, "FindLargeFilesProgress").IsVisible, Is.False);
        Pump(dialog); Assert.That(FooterPosition(dialog, "DeleteLargeFiles"), Is.EqualTo(deletePosition));
        Assert.That(FooterPosition(dialog, "CloseLargeFiles"), Is.EqualTo(closePosition));
        Assert.That(dialog.LastProcess!.Success, Is.True); Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(dialog.IsVisible, Is.True);
        Assert.That(dialog.VisibleObjects.Any(row => selected.Contains(row.Cells[1], StringComparer.Ordinal)), Is.False);
        Assert.That(Field<TextBlock>(dialog, "OperationStatus").Text, Does.Contain("Clean up repository objects").And.Contain("annotated-native"));
        Assert.That((await f.Git("rev-parse", "annotated-native^{}")).StandardOutput.Trim(), Is.EqualTo(tagged));
        Assert.That((await f.Git("ls-tree", "-r", "--name-only", "HEAD")).StandardOutput, Does.Not.Contain(Fixture.LargePath).And.Not.Contain(Fixture.LiteralPath));
    }

    [AvaloniaTest]
    public async Task NativeFailureAfterRewriteRetainsBackupsCheckedRowsAndRealDiagnosticsWithoutPretendingSuccess()
    {
        await using Fixture f = await Fixture.Create(); string before = await f.Head();
        FindLargeFilesDialog dialog = await f.Show(new(f.Repository.Path, new GitRunner(f.FailureWrapper()))); Select(dialog, Fixture.LargePath);
        Pointer(Field<Button>(dialog, "DeleteLargeFiles")); Pointer(Field<Button>(await Confirm(dialog), "ConfirmLargeFilesDelete")); GitProcessDialog phase = await Ready(dialog);
        GitHistoryRemovalResult removal = dialog.LastRemoval!;
        Assert.That(removal.Success, Is.False); Assert.That(removal.MutationStarted, Is.True); Assert.That(removal.Steps[0].Success, Is.True);
        Assert.That(removal.Steps.Last().Step, Is.EqualTo(GitHistoryRemovalStep.EnumerateBackupReferences));
        Assert.That(phase.Error, Is.TypeOf<GitCommandException>()); Assert.That(phase.Result, Is.SameAs(removal.Steps.Last().Result));
        Assert.That(phase.Result!.ExitCode, Is.Not.Zero); Assert.That(Field<TextBox>(phase, "ProcessOutput").Text, Does.Contain("was rewritten").And.Contain("core.abbrev"));
        Pointer(Field<Button>(phase, "Ok")); await Idle(dialog);
        Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(dialog.LastProcess!.Success, Is.False);
        Assert.That(dialog.SelectedPaths, Is.EqualTo(new[] { Fixture.LargePath })); Assert.That(dialog.VisibleObjects.Any(row => row.Cells[1] == Fixture.LargePath), Is.True);
        Assert.That(Field<TextBlock>(dialog, "OperationStatus").Text, Does.Contain("partially changed").And.Contain("core.abbrev"));
        Assert.That(await f.Head(), Is.Not.EqualTo(before)); Assert.That((await f.Git("for-each-ref", "--format=%(refname)", "refs/original/")).StandardOutput, Is.Not.Empty);
    }

    [AvaloniaTest]
    public async Task AbortAndBusyOwnerCloseRetainActualPartialRewriteAndNeverStartCleanupOrInventAnExit()
    {
        foreach (bool closeOwner in new[] { false, true })
        {
            await using Fixture f = await Fixture.Create(); string before = await f.Head(); string wrapper = f.GatedWrapper();
            FindLargeFilesDialog dialog = await f.Show(new(f.Repository.Path, new GitRunner(wrapper))); Select(dialog, Fixture.LargePath);
            Pointer(Field<Button>(dialog, "DeleteLargeFiles")); Pointer(Field<Button>(await Confirm(dialog), "ConfirmLargeFilesDelete"));
            await Until(() => File.Exists(wrapper + ".ready")); GitProcessDialog phase = dialog.OwnedWindows.OfType<GitProcessDialog>().Single();
            await Until(() => Encoding.UTF8.GetString(phase.StandardOutputBytes.Span).Contains("fixture after real rewrite", StringComparison.Ordinal));
            Assert.That(await f.Head(), Is.Not.EqualTo(before)); Assert.That(phase.IsRunning, Is.True); Assert.That(Field<Button>(dialog, "CloseLargeFiles").IsEnabled, Is.True);
            int pid = int.Parse(File.ReadAllText(wrapper + ".ready"), CultureInfo.InvariantCulture); TextBlock status = Field<TextBlock>(dialog, "OperationStatus");
            if (closeOwner) { dialog.Close(); } else { Pointer(Field<Button>(phase, "Abort")); }
            await Until(() => !dialog.IsBusy && (!closeOwner || !dialog.IsVisible));
            Assert.That(dialog.LastRemoval!.Cancelled, Is.True); Assert.That(dialog.LastRemoval.MutationStarted, Is.True);
            Assert.That(dialog.LastRemoval.Steps, Has.Count.EqualTo(1)); Assert.That(dialog.LastRemoval.Steps[0].Result, Is.Null);
            Assert.That(dialog.LastProcess!.WasCancelled, Is.True); Assert.That(dialog.LastProcess.Result, Is.Null);
            Assert.That(dialog.HasRepositoryChanges, Is.True); Assert.That(dialog.IsVisible, Is.EqualTo(!closeOwner));
            Assert.That(Running(pid), Is.False, "Cancellation returns after the captured actor has stopped.");
            Assert.That((await f.Git("for-each-ref", "--format=%(refname)", "refs/original/")).StandardOutput, Is.Not.Empty);
            Assert.That(dialog.SelectedPaths, Is.EqualTo(new[] { Fixture.LargePath }));
            Assert.That(status.Text, Does.Contain("partially changed").And.Contain("Operation cancelled").And.Not.Contain("Exit code:"));
        }
    }

    [AvaloniaTest]
    public async Task BusyProcessDefersAnActualOwnerCloseUntilNativeCompletionWhileDirectAbortKeepsTheOwnerOpen()
    {
        foreach (bool closeOwner in new[] { false, true })
        {
            await using Fixture f = await Fixture.Create(); string before = f.Materials(); string wrapper = f.ReadOnlyGate();
            Window owner = f.Track(new Window { Width = 800, Height = 600 }); owner.Show();
            Task<GitProcessDialogResult> task = GitProcessDialog.ShowAsync(owner, f.Repository.Path, "native readonly owner-close regression", true,
                (token, output) => new GitRunner(wrapper).RunAsync(f.Repository.Path, ["rev-parse", "HEAD"], token, outputReceived: output));
            await Until(() => File.Exists(wrapper + ".ready")); GitProcessDialog phase = owner.OwnedWindows.OfType<GitProcessDialog>().Single();
            await Until(() => Encoding.UTF8.GetString(phase.StandardOutputBytes.Span).Contains("fixture after real native command", StringComparison.Ordinal));
            int pid = int.Parse(File.ReadAllText(wrapper + ".ready"), CultureInfo.InvariantCulture);
            Assert.That(phase.Owner, Is.SameAs(owner)); Assert.That(phase.IsRunning, Is.True); Assert.That(Running(pid), Is.True);
            List<WindowCloseReason> closeReasons = []; phase.Closing += (_, args) => closeReasons.Add(args.CloseReason);
            bool ownerClosed = false, nativeFinishedBeforeOwnerClose = false;
            owner.Closed += (_, _) => { ownerClosed = true; nativeFinishedBeforeOwnerClose = !phase.IsRunning && !Running(pid); };
            if (closeOwner) { owner.Close(); } else { Pointer(Field<Button>(phase, "Abort")); }
            await Until(() => task.IsCompleted && !phase.IsVisible && (!closeOwner || !owner.IsVisible));
            GitProcessDialogResult result = await task;
            Assert.That(closeReasons.Contains(WindowCloseReason.OwnerWindowClosing), Is.EqualTo(closeOwner));
            Assert.That(owner.IsVisible, Is.EqualTo(!closeOwner)); Assert.That(ownerClosed, Is.EqualTo(closeOwner));
            if (closeOwner) { Assert.That(nativeFinishedBeforeOwnerClose, Is.True); }
            Assert.That(result.CommandStarted, Is.True); Assert.That(result.WasCancelled, Is.True); Assert.That(result.Result, Is.Null);
            Assert.That(result.Error, Is.InstanceOf<OperationCanceledException>()); Assert.That(Running(pid), Is.False);
            Assert.That(Encoding.UTF8.GetString(phase.StandardOutputBytes.Span), Does.Contain("fixture after real native command"));
            Assert.That(f.Materials(), Is.EqualTo(before), "This direct owner-close regression uses a readonly native command.");
        }
    }

    [AvaloniaTest]
    public async Task MainPluginMenuAutomaticallyScansAndRestoresThresholdAndRevisionThroughTheExistingStore()
    {
        await using Fixture f = await Fixture.Create(); MainWindow main = f.Track(new MainWindow()); main.Show(); await main.OpenRepositoryAsync(f.Repository.Path); await Until(() => !main.IsBusy);
        FindLargeFilesDialog dialog = await Open(); Assert.That(dialog.LastAnalysis, Is.Not.Null); Assert.That(dialog.VisibleObjects, Has.Count.EqualTo(3));
        Field<NumericUpDown>(dialog, "FindLargeFilesMinimum").Value = 1.1m; Field<TextBox>(dialog, "FindLargeFilesRevision").Text = "HEAD~1";
        Pointer(Field<Button>(dialog, "AnalyzeLargeFiles")); await Idle(dialog); Assert.That(dialog.VisibleObjects, Has.Count.EqualTo(2));
        Pointer(Field<Button>(dialog, "CloseLargeFiles")); string id = new FindLargeFilesPlugin().Descriptor.Id.ToString();
        await Until(() => !main.OwnedWindows.Contains(dialog) && !main.IsBusy && new PreferencesStore().Load().PluginSettings.ContainsKey(id));
        Assert.That(new PreferencesStore().Load().PluginSettings[id]["minimumMegabytes"], Is.EqualTo("1.1"));
        Assert.That(new PreferencesStore().Load().PluginSettings[id]["revision"], Is.EqualTo("HEAD~1"));
        FindLargeFilesDialog restored = await Open(); Assert.That(restored.CurrentValues, Is.EquivalentTo(dialog.CurrentValues));
        Assert.That(restored.VisibleObjects, Has.Count.EqualTo(2)); Assert.That(restored.HasRepositoryChanges, Is.False); restored.Close();

        async Task<FindLargeFilesDialog> Open()
        {
            MenuItem plugins = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Plugins");
            MenuItem entry = plugins.Items.OfType<MenuItem>().Single(item => item.Header as string == "Find large files");
            plugins.IsSubMenuOpen = true; entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); plugins.IsSubMenuOpen = false;
            await Until(() => main.OwnedWindows.OfType<FindLargeFilesDialog>().Any(candidate => candidate.LastAnalysis is not null && !candidate.IsBusy));
            return main.OwnedWindows.OfType<FindLargeFilesDialog>().Single();
        }
    }

    [AvaloniaTest]
    public async Task CompositeProcessPreservesEarlierNativeBytesCopyAndLastRealResultWithoutChangingDefaultFallback()
    {
        await using Fixture f = await Fixture.Create(); Window owner = f.Track(new Window { Width = 800, Height = 600 }); owner.Show();
        byte[] bytes = Encoding.UTF8.GetBytes("phase one λ😀\n"); GitBinaryResult blob = await new GitRunner().RunBinaryAsync(f.Repository.Path, ["hash-object", "-w", "--stdin"], standardInput: bytes);
        Assert.That(blob.Success, Is.True); string hash = Encoding.UTF8.GetString(blob.StandardOutput).Trim(); GitResult? first = null, last = null;
        Task<GitProcessDialogResult> task = GitProcessDialog.ShowAsync(owner, f.Repository.Path, "native multi-phase", true, async (token, output) =>
        {
            first = await new GitRunner().RunAsync(f.Repository.Path, ["cat-file", "blob", hash], token, outputReceived: output);
            last = await new GitRunner().RunAsync(f.Repository.Path, ["rev-parse", "HEAD"], token, outputReceived: output); return last;
        }, preserveStreamedOutput: true);
        GitProcessDialog phase = await Ready(owner); byte[] expected = [.. first!.StandardOutputBytes, .. last!.StandardOutputBytes];
        Assert.That(phase.Result, Is.SameAs(last)); Assert.That(phase.StandardOutputBytes.ToArray(), Is.EqualTo(expected));
        TextBox text = Field<TextBox>(phase, "ProcessOutput"); string displayed = "native multi-phase\n" + Encoding.UTF8.GetString(expected) + "Done";
        Assert.That(text.Text, Is.EqualTo(displayed)); string? previous = await ClipboardExtensions.TryGetTextAsync(phase.Clipboard!);
        try
        {
            text.Focus(); text.SelectAll(); phase.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control); phase.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.Control); Pump(phase);
            Assert.That(await ClipboardExtensions.TryGetTextAsync(phase.Clipboard!), Is.EqualTo(displayed));
        }
        finally { await ClipboardExtensions.SetTextAsync(phase.Clipboard!, previous ?? ""); }
        Pointer(Field<Button>(phase, "Ok")); Assert.That((await task).Result, Is.SameAs(last));
        // No-stream readonly failures still use the actual native result, even in the new mode.
        Task<GitProcessDialogResult> fallback = GitProcessDialog.ShowAsync(owner, f.Repository.Path, "readonly fallback", true, async (token, _) =>
        {
            string[] args = ["rev-parse", "--verify", "refs/heads/definitely-missing"];
            GitResult result = await new GitRunner().RunAsync(f.Repository.Path, args, token); throw new GitCommandException(args, result);
        }, preserveStreamedOutput: true);
        GitProcessDialog failed = await Ready(owner); Assert.That(failed.Result!.Success, Is.False);
        Assert.That(Field<TextBox>(failed, "ProcessOutput").Text, Does.Contain(failed.Result.StandardError.Trim()));
        Pointer(Field<Button>(failed, "Ok")); Assert.That((await fallback).Success, Is.False);
    }

    [AvaloniaTest]
    public async Task CompositeObserverFailureKeepsActualEarlierStreamsAndTheOriginalErrorWithoutAnyNativeExitResult()
    {
        await using Fixture f = await Fixture.Create(); Window owner = f.Track(new Window { Width = 800, Height = 600 }); owner.Show();
        InvalidOperationException original = new("observer stopped the native composite"); byte[] observed = [];
        Task<GitProcessDialogResult> task = GitProcessDialog.ShowAsync(owner, f.Repository.Path, "observer failure", true, async (token, output) =>
            await new GitRunner().RunAsync(f.Repository.Path, ["rev-parse", "HEAD"], token, outputReceived: chunk =>
            { output(chunk); observed = chunk.Bytes.ToArray(); throw original; }), preserveStreamedOutput: true);
        GitProcessDialog phase = await Ready(owner); Assert.That(phase.Error, Is.SameAs(original)); Assert.That(phase.Result, Is.Null);
        Assert.That(observed, Is.Not.Empty); Assert.That(phase.StandardOutputBytes.ToArray(), Is.EqualTo(observed));
        Assert.That(Field<TextBox>(phase, "ProcessOutput").Text, Does.Contain(Encoding.UTF8.GetString(observed)).And.Not.EndWith("Done"));
        Pointer(Field<Button>(phase, "Ok")); GitProcessDialogResult result = await task; Assert.That(result.Error, Is.SameAs(original)); Assert.That(result.Result, Is.Null);
    }

    private static T Field<T>(TopLevel window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static Point FooterPosition(FindLargeFilesDialog dialog, string name)
    {
        Button button = Field<Button>(dialog, name);
        Assert.That(button.Parent, Is.TypeOf<Grid>());
        return button.TranslatePoint(default, (Grid)button.Parent!)!.Value;
    }
    private static Grid Row(FindLargeFilesDialog dialog, string hash) => dialog.GetVisualDescendants().OfType<Grid>().Single(row => row.Name == "FindLargeFilesRow" && row.Tag as string == hash);
    private static CheckBox Check(FindLargeFilesDialog dialog, string hash) => Row(dialog, hash).Children.OfType<CheckBox>().Single();
    private static async Task RevealObject(FindLargeFilesDialog dialog, string hash)
    {
        ListBox list = Field<ListBox>(dialog, "FindLargeFilesObjects");
        int index = dialog.VisibleObjects.ToList().FindIndex(row => row.Key == hash);
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        object? item = list.Items[index]; Assert.That(item, Is.Not.Null);
        list.ScrollIntoView(item!); Pump(dialog);
        await Until(() =>
        {
            Pump(dialog);
            return list.ContainerFromIndex(index) is Control container && container.GetVisualDescendants().OfType<CheckBox>()
                .Any(check => check.Name == "FindLargeFilesDeleteChecked" && check.Tag as string == hash && check.IsEffectivelyVisible);
        });
    }
    private static void Select(FindLargeFilesDialog dialog, params string[] paths)
    { foreach (string path in paths) { PluginRow row = dialog.VisibleObjects.First(row => row.Cells[1] == path); Pointer(Check(dialog, row.Key)); } }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Pointer(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; Pump(top); Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        Assert.That(control.IsEffectivelyEnabled && control.IsEffectivelyVisible && new Rect(default, top.ClientSize).Contains(point)
            && (top.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(control) == true, Is.True, control.Name);
        top.MouseMove(point); top.MouseDown(point, MouseButton.Left); top.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static async Task Until(Func<bool> condition)
    { Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(30)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True, "The native UI workflow did not reach the expected state."); }
    private static Task Idle(RepositoryDialog dialog) => Until(() => !dialog.IsBusy);
    private static async Task<Window> Confirm(FindLargeFilesDialog dialog)
    { await Until(() => dialog.OwnedWindows.Any(window => window.Name == "FindLargeFilesDeleteConfirmation")); return dialog.OwnedWindows.Single(window => window.Name == "FindLargeFilesDeleteConfirmation"); }
    private static async Task<GitProcessDialog> Ready(Window owner)
    { await Until(() => owner.OwnedWindows.OfType<GitProcessDialog>().Any(window => !window.IsRunning)); return owner.OwnedWindows.OfType<GitProcessDialog>().Single(); }
    private static void Capture(Window window, string name)
    { Pump(window); using Bitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }
    private static bool Running(int pid)
    { try { string stat = File.ReadAllText("/proc/" + pid + "/stat"); return stat[stat.LastIndexOf(')') + 2] is not ('Z' or 'X' or 'x'); } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string LargePath = "folder ü/large 'a'.bin", LiteralPath = "literal [glob].txt";
        internal static DateTimeOffset LastDate => DateTimeOffset.Parse("2026-10-02T12:00:00+00:00", CultureInfo.InvariantCulture);
        private readonly Dictionary<string, string?> _environment = [];
        private readonly CultureInfo _oldCulture = CultureInfo.CurrentCulture, _oldUiCulture = CultureInfo.CurrentUICulture;
        private readonly List<Window> _windows = [];
        private readonly List<string> _gates = [];
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "find-large-files-ui-" + Guid.NewGuid().ToString("N"));
        internal GitRepository Repository { get; private set; } = null!;
        private Fixture()
        {
            Directory.CreateDirectory(Root); CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US"); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            foreach ((string name, string value) in new[] { ("XDG_CONFIG_HOME", Path.Combine(Root, "profile")), ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_OPTIONAL_LOCKS", "0"), ("FILTER_BRANCH_SQUELCH_WARNING", "1") })
            { _environment[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
            new PreferencesStore().Save(new() { Theme = "Light" });
        }
        internal static async Task<Fixture> Create()
        {
            Fixture f = new();
            try
            {
                f.Repository = await GitRepository.InitAsync(Path.Combine(f.Root, "repo"));
                await f.Git("config", "user.name", "Large Files UI"); await f.Git("config", "user.email", "large-files-ui@example.invalid");
                await f.Git("config", "commit.gpgsign", "false"); await f.Git("config", "tag.gpgsign", "false"); await f.Git("config", "core.hooksPath", "/dev/null");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(f.Repository.Path, LargePath))!);
                File.WriteAllBytes(Path.Combine(f.Repository.Path, LargePath), Enumerable.Repeat((byte)'L', 12 * 1048576).ToArray());
                File.WriteAllBytes(Path.Combine(f.Repository.Path, "small.bin"), Enumerable.Repeat((byte)'S', 3 * 1048576).ToArray());
                File.WriteAllBytes(Path.Combine(f.Repository.Path, LiteralPath), Enumerable.Repeat((byte)'G', 1048576).ToArray());
                File.WriteAllText(Path.Combine(f.Repository.Path, "keep.txt"), "keep version one\n"); await f.Commit("First", "2026-10-01T12:00:00+00:00");
                File.WriteAllText(Path.Combine(f.Repository.Path, "keep.txt"), "keep version two\n"); await f.Commit("Second", "2026-10-02T12:00:00+00:00");
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        private async Task Commit(string title, string date)
        {
            await Repository.StageAllAsync(); GitResult result = await new GitRunner().RunAsync(Repository.Path, ["commit", "-m", title], environment: new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date });
            Assert.That(result.Success, Is.True, result.StandardError);
        }
        internal async Task<GitResult> Git(params string[] args) { GitResult result = await new GitRunner().RunAsync(Repository.Path, args); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        internal async Task<string> Head() => (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        internal T Track<T>(T window) where T : Window { _windows.Add(window); return window; }
        internal async Task<FindLargeFilesDialog> Show(GitRepository? repository = null)
        { FindLargeFilesDialog dialog = Track(new FindLargeFilesDialog(repository ?? Repository, new Dictionary<string, string?> { ["minimumMegabytes"] = "0" })); dialog.Show(); await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Pump(dialog); return dialog; }
        internal string FailureWrapper()
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("This fixture requires native Linux process semantics."); }
            string path = Path.Combine(Root, "failure-git");
            // Rewrite genuinely succeeds first. The next phase invokes native Git with a deliberately invalid numeric config.
            File.WriteAllText(path, """
                #!/usr/bin/python3
                import os, sys
                args = sys.argv[1:]
                i = 0
                while i < len(args):
                    if args[i] == '-c': i += 2
                    elif args[i] == '--no-pager': i += 1
                    else: break
                if i < len(args) and args[i] == 'for-each-ref':
                    os.execv('/usr/bin/git', ['/usr/bin/git', '-c', 'core.abbrev=invalid', *args])
                os.execv('/usr/bin/git', ['/usr/bin/git', *args])
                """ + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
        }
        internal string GatedWrapper()
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("This fixture requires native Linux process semantics."); }
            string path = Path.Combine(Root, "gated-git"); _gates.Add(path);
            File.WriteAllText(path, """
                #!/usr/bin/python3
                import os, pathlib, subprocess, sys, time
                args = sys.argv[1:]
                i = 0
                while i < len(args):
                    if args[i] == '-c': i += 2
                    elif args[i] == '--no-pager': i += 1
                    else: break
                if i < len(args) and args[i] == 'filter-branch':
                    status = subprocess.run(['/usr/bin/git', *args]).returncode
                    if status != 0: sys.exit(status)
                    print('fixture after real rewrite', flush=True)
                    pathlib.Path(__file__ + '.ready').write_text(str(os.getpid()))
                    try:
                        while not pathlib.Path(__file__ + '.release').exists(): time.sleep(.02)
                    except KeyboardInterrupt:
                        print('fixture interrupted after real rewrite', file=sys.stderr, flush=True)
                        sys.exit(130)
                    sys.exit(status)
                os.execv('/usr/bin/git', ['/usr/bin/git', *args])
                """ + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
        }
        internal string ReadOnlyGate()
        {
            string path = GatedWrapper();
            File.WriteAllText(path, File.ReadAllText(path).Replace("'filter-branch'", "'rev-parse'", StringComparison.Ordinal)
                .Replace("after real rewrite", "after real native command", StringComparison.Ordinal));
            return path;
        }
        internal string Materials() => string.Join('\n', Directory.GetFiles(Repository.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Repository.Path, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        public async ValueTask DisposeAsync()
        {
            foreach (string gate in _gates) { File.WriteAllText(gate + ".release", "release\n"); }
            static void CloseOwned(Window window) { foreach (Window child in window.OwnedWindows.ToArray()) { CloseOwned(child); child.Close(); } }
            foreach (Window window in _windows.AsEnumerable().Reverse()) { CloseOwned(window); window.Close(); }
            if (_windows.Count > 0) { await Until(() => _windows.All(window => !window.IsVisible && (window is not RepositoryDialog dialog || !dialog.IsBusy) && (window is not MainWindow main || !main.IsBusy))); }
            Dispatcher.UIThread.RunJobs(); foreach ((string name, string? value) in _environment) { Environment.SetEnvironmentVariable(name, value); }
            CultureInfo.CurrentCulture = _oldCulture; CultureInfo.CurrentUICulture = _oldUiCulture; if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, NonParallelizable]
public sealed class ResetRevertDialogTests
{
    [AvaloniaTest]
    public async Task ResetDefaultSoftKeyboardKeepsTheActualIndexAndWorktree()
    {
        await using Fixture f = await Fixture.CreateAsync(); string tip = await f.Commit("second.txt", "second\n", "Second");
        f.Write("seed.txt", "staged\n"); await f.Git("add", "seed.txt"); f.Write("seed.txt", "separate worktree\n"); byte[] index = File.ReadAllBytes(f.File(".git/index"));
        ResetCurrentBranchDialog dialog = await f.Show(new ResetCurrentBranchDialog(f.Repository, f.Initial));
        Assert.That(dialog.Mode, Is.EqualTo(GitResetMode.Soft)); Assert.That(Field<TextBlock>(dialog, "ResetCurrentRevisionSummary").Text, Does.Contain(f.Initial).And.Contain("Initial"));
        Assert.That(dialog.GetVisualDescendants().OfType<RadioButton>().Select(item => item.Name), Is.EqualTo(new[] { "ResetModeSoft", "ResetModeMixed", "ResetModeKeep", "ResetModeMerge", "ResetModeHard" }));
        Field<Button>(dialog, "ExecuteResetCurrentBranch").Focus(); Press(dialog, PhysicalKey.Enter);
        await Until(() => !dialog.IsVisible && !dialog.IsBusy, () => dialog.LastError);
        Assert.That(dialog.Applied, Is.True); Assert.That(await f.Head(), Is.EqualTo(f.Initial)); Assert.That(tip, Is.Not.EqualTo(f.Initial));
        Assert.That(File.ReadAllBytes(f.File(".git/index")), Is.EqualTo(index)); Assert.That(File.ReadAllText(f.File("seed.txt")), Is.EqualTo("separate worktree\n"));
    }

    [AvaloniaTest]
    public async Task OriginalResetColorsLabelsAndSummaryRenderInFrenchInBothThemes()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = new("fr-FR");
        try
        {
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                await using Fixture f = await Fixture.CreateAsync(); await f.Commit("second.txt", "second\n", "Second");
                ResetCurrentBranchDialog dialog = await f.Show(new ResetCurrentBranchDialog(f.Repository, f.Initial) { RequestedThemeVariant = theme });
                Assert.That(dialog.Title, Is.EqualTo("Réinitialiser la branche courante"));
                Assert.That(Field<RadioButton>(dialog, "ResetModeHard").Content, Is.TypeOf<AccessText>());
                Assert.That(((AccessText)Field<RadioButton>(dialog, "ResetModeHard").Content!).Text, Does.Contain("TOUTES"));
                string[] modes = ["Soft", "Mixed", "Keep", "Merge", "Hard"];
                Color[] expected = theme == ThemeVariant.Light
                    ? [Color.Parse("#80ff80"), Color.Parse("#ffff80"), Color.Parse("#ffff80"), Color.Parse("#ffff80"), Color.Parse("#ff8080")]
                    : [Color.Parse("#63be63"), Color.Parse("#bebe63"), Color.Parse("#bebe63"), Color.Parse("#bebe63"), Color.Parse("#be6363")];
                dialog.MouseMove(new Point(2, 2)); Pump(dialog);
                for (int index = 0; index < modes.Length; index++)
                {
                    RadioButton radio = Field<RadioButton>(dialog, "ResetMode" + modes[index]);
                    Assert.That(((ISolidColorBrush)radio.Background!).Color, Is.EqualTo(expected[index]));
                    Assert.That(Contrast(((ISolidColorBrush)radio.Foreground!).Color, expected[index]), Is.GreaterThanOrEqualTo(4.5));
                    Point point = radio.TranslatePoint(new Point(radio.Bounds.Width - 9, radio.Bounds.Height / 2), dialog)!.Value;
                    Assert.That(Pixel(dialog, point), Is.EqualTo(expected[index]), "The actual radio template must preserve the original reset-type background.");
                }
                Capture(dialog, "original-reset-current-fr-" + theme + ".png");
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [AvaloniaTest]
    public async Task HardWarningNoAndEscapePreserveChangesThenYesExecutesTheReset()
    {
        await using Fixture f = await Fixture.CreateAsync(); string tip = await f.Commit("seed.txt", "committed\n", "Second"); f.Write("seed.txt", "discardable\n"); byte[] index = File.ReadAllBytes(f.File(".git/index"));
        ResetCurrentBranchDialog dialog = await f.Show(new ResetCurrentBranchDialog(f.Repository, f.Initial) { RequestedThemeVariant = ThemeVariant.Dark });
        Pointer(Field<RadioButton>(dialog, "ResetModeHard")); Pointer(Field<Button>(dialog, "ExecuteResetCurrentBranch"));
        await Until(() => dialog.OwnedWindows.Any(window => window.Name == "ResetHardWarning")); Window warning = dialog.OwnedWindows.Single(window => window.Name == "ResetHardWarning");
        Assert.That(warning.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark)); Capture(warning, "original-reset-hard-warning-dark.png");
        Pointer(Field<Button>(warning, "CancelHardReset")); await Until(() => !warning.IsVisible && Field<Button>(dialog, "ExecuteResetCurrentBranch").IsEnabled);
        Assert.That(await f.Head(), Is.EqualTo(tip)); Assert.That(File.ReadAllBytes(f.File(".git/index")), Is.EqualTo(index)); Assert.That(File.ReadAllText(f.File("seed.txt")), Is.EqualTo("discardable\n"));
        Pointer(Field<Button>(dialog, "ExecuteResetCurrentBranch")); await Until(() => dialog.OwnedWindows.Any(window => window.Name == "ResetHardWarning")); warning = dialog.OwnedWindows.Single(window => window.Name == "ResetHardWarning"); Press(warning, PhysicalKey.Escape, releaseOn: dialog);
        await Until(() => !warning.IsVisible && Field<Button>(dialog, "ExecuteResetCurrentBranch").IsEnabled); Assert.That(await f.Head(), Is.EqualTo(tip));
        Pointer(Field<Button>(dialog, "ExecuteResetCurrentBranch")); await Until(() => dialog.OwnedWindows.Any(window => window.Name == "ResetHardWarning")); warning = dialog.OwnedWindows.Single(window => window.Name == "ResetHardWarning"); Pointer(Field<Button>(warning, "ConfirmHardReset"));
        await Until(() => !dialog.IsVisible && !dialog.IsBusy); Assert.That(dialog.Applied, Is.True); Assert.That(await f.Head(), Is.EqualTo(f.Initial)); Assert.That(File.ReadAllText(f.File("seed.txt")), Is.EqualTo("seed\n"));
    }

    [AvaloniaTest]
    public async Task AnotherBranchFastForwardCheckoutPreferenceAndForceGateFollowTheOriginal()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.Git("branch", "other", f.Initial); string tip = await f.Commit("second.txt", "second\n", "Second");
        ResetAnotherBranchDialog dialog = await f.Show(new ResetAnotherBranchDialog(f.Repository, tip));
        Assert.That(Field<CheckBox>(dialog, "ResetOtherBranchCheckout").IsChecked, Is.True); Assert.That(Field<CheckBox>(dialog, "ResetOtherBranchForce").IsChecked, Is.False);
        await SelectResetBranch(dialog, "other");
        Assert.That(Field<Button>(dialog, "ExecuteResetOtherBranch").IsEnabled, Is.True);
        await PointerAfterPopupClosed(Field<CheckBox>(dialog, "ResetOtherBranchCheckout")); await PointerAfterPopupClosed(Field<Button>(dialog, "ExecuteResetOtherBranch")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(await f.Text("rev-parse", "other"), Is.EqualTo(tip)); Assert.That(await f.Text("symbolic-ref", "--short", "HEAD"), Is.EqualTo("main")); Assert.That(dialog.CheckoutRequested, Is.False);
        dialog = await f.Show(new ResetAnotherBranchDialog(f.Repository, f.Initial)); Assert.That(Field<CheckBox>(dialog, "ResetOtherBranchCheckout").IsChecked, Is.False, "Checkout is a durable option, independent of Force reset.");
        await SelectResetBranch(dialog, "other");
        Assert.That(Field<Button>(dialog, "ExecuteResetOtherBranch").IsEnabled, Is.False); await PointerAfterPopupClosed(Field<CheckBox>(dialog, "ResetOtherBranchForce"));
        Assert.That(Field<Button>(dialog, "ExecuteResetOtherBranch").IsEnabled, Is.True); await PointerAfterPopupClosed(Field<Button>(dialog, "ExecuteResetOtherBranch")); await Until(() => !dialog.IsVisible && !dialog.IsBusy); Assert.That(await f.Text("rev-parse", "other"), Is.EqualTo(f.Initial));
    }

    [AvaloniaTest]
    public async Task AnotherBranchUsesTheRealCheckoutDialogAfterMovingTheBranch()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.Git("branch", "other"); string tip = await f.Commit("second.txt", "second\n", "Second");
        ResetAnotherBranchDialog dialog = await f.Show(new ResetAnotherBranchDialog(f.Repository, tip)); await SelectResetBranch(dialog, "other");
        await PointerAfterPopupClosed(Field<Button>(dialog, "ExecuteResetOtherBranch")); await Until(() => dialog.OwnedWindows.OfType<CheckoutBranchDialog>().Any());
        CheckoutBranchDialog checkout = dialog.OwnedWindows.OfType<CheckoutBranchDialog>().Single(); await Until(() => !checkout.IsBusy && Field<Button>(checkout, "CheckoutBranch").IsEnabled);
        Assert.That(await f.Text("symbolic-ref", "--short", "HEAD"), Is.EqualTo("main")); Pointer(Field<Button>(checkout, "CheckoutBranch")); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
        Assert.That(dialog.CheckoutSucceeded, Is.True); Assert.That(await f.Text("symbolic-ref", "--short", "HEAD"), Is.EqualTo("other")); Assert.That(await f.Head(), Is.EqualTo(tip));
    }

    [AvaloniaTest]
    public async Task AnotherBranchTypingUnknownOrCurrentBranchNeverEnablesMutation()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.Git("branch", "other"); string tip = await f.Commit("second.txt", "second\n", "Second");
        ResetAnotherBranchDialog dialog = await f.Show(new ResetAnotherBranchDialog(f.Repository, tip)); AutoCompleteBox branch = Field<AutoCompleteBox>(dialog, "ResetOtherBranchSelection");
        TextBox editor = branch.GetVisualDescendants().OfType<TextBox>().Single(); Assert.That(editor.Focus(), Is.True);
        await Until(() => branch.IsDropDownOpen, () => ResetBranchState(dialog));
        Press(dialog, PhysicalKey.Escape); await Until(() => !branch.IsDropDownOpen, () => ResetBranchState(dialog));
        Assert.That(dialog.IsVisible, Is.True, "Escape closes the original branch suggestions before the dialog.");
        Press(dialog, PhysicalKey.A, RawInputModifiers.Control); dialog.KeyTextInput("missing");
        await Until(() => branch.Text == "missing" && !branch.IsDropDownOpen, () => ResetBranchState(dialog));
        Pointer(Field<CheckBox>(dialog, "ResetOtherBranchForce")); Assert.That(Field<Button>(dialog, "ExecuteResetOtherBranch").IsEnabled, Is.False);
        Assert.That(editor.Focus(), Is.True); Press(dialog, PhysicalKey.A, RawInputModifiers.Control); dialog.KeyTextInput("main");
        await Until(() => branch.Text == "main" && !branch.IsDropDownOpen, () => ResetBranchState(dialog)); Assert.That(Field<Button>(dialog, "ExecuteResetOtherBranch").IsEnabled, Is.False);
        Press(dialog, PhysicalKey.Escape); await Until(() => !dialog.IsVisible, () => ResetBranchState(dialog));
        Assert.That(await f.Head(), Is.EqualTo(tip)); Assert.That(await f.Text("rev-parse", "other"), Is.EqualTo(f.Initial));
    }

    [AvaloniaTest]
    public async Task HardResetOffersSubmodulesAndRememberedMixedResetRunsTheSharedUpdateAndCallback()
    {
        foreach (bool hard in new[] { true, false })
        {
            await using Fixture f = await Fixture.CreateAsync(); string source = Path.Combine(f.Root, "module-source"); Directory.CreateDirectory(source); GitRepository module = new(source);
            await f.GitAt(module, "init", "-b", "main"); await f.GitAt(module, "config", "user.name", "Module"); await f.GitAt(module, "config", "user.email", "module@example.invalid"); await f.GitAt(module, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(source, "module.txt"), "one\n"); await f.GitAt(module, "add", "module.txt"); await f.GitAt(module, "commit", "-m", "Module one");
            await f.Git("-c", "protocol.file.allow=always", "submodule", "add", source, "module"); await f.Git("commit", "-am", "Module one"); string target = await f.Head();
            GitRepository worktree = new(f.File("module")); string one = (await f.GitAt(worktree, "rev-parse", "HEAD")).StandardOutput.Trim();
            await f.GitAt(worktree, "config", "user.name", "Module"); await f.GitAt(worktree, "config", "user.email", "module@example.invalid"); await f.GitAt(worktree, "config", "commit.gpgsign", "false"); File.WriteAllText(f.File("module/module.txt"), "two\n"); await f.GitAt(worktree, "commit", "-am", "Module two"); await f.Git("commit", "-am", "Module two");
            if (!hard) { new SubmoduleCheckoutPreferencesStore().Remember(true); }
            int callbacks = 0; ResetCurrentBranchDialog dialog = await f.Show(new ResetCurrentBranchDialog(f.Repository, target, hard ? GitResetMode.Hard : GitResetMode.Mixed,
                (owner, token) => { Assert.That(owner.IsVisible && !token.IsCancellationRequested, Is.True); callbacks++; return Task.CompletedTask; }));
            Pointer(Field<Button>(dialog, "ExecuteResetCurrentBranch"));
            if (hard)
            {
                await Until(() => dialog.OwnedWindows.Any(window => window.Name == "ResetHardWarning")); Pointer(Field<Button>(dialog.OwnedWindows.Single(window => window.Name == "ResetHardWarning"), "ConfirmHardReset"));
                await Until(() => dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>().Any()); CheckoutSubmodulePrompt prompt = dialog.OwnedWindows.OfType<CheckoutSubmodulePrompt>().Single();
                Button yes = prompt.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "UpdateCheckoutSubmodules"); Pointer(yes);
            }
            await Until(() => !dialog.IsVisible && !dialog.IsBusy, () => dialog.LastError);
            Assert.That(dialog.SubmodulesUpdated, Is.True); Assert.That(callbacks, Is.EqualTo(1)); Assert.That((await f.GitAt(worktree, "rev-parse", "HEAD")).StandardOutput.Trim(), Is.EqualTo(one));
        }
    }

    [AvaloniaTest]
    public async Task RevertDefaultNoCommitAndAutomaticKeyboardChoiceHaveDistinctNativeEffects()
    {
        foreach (bool automatic in new[] { false, true })
        {
            await using Fixture f = await Fixture.CreateAsync(); string tip = await f.Commit("added λ [*].txt", "added\n", "Added");
            RevertDialog dialog = await f.Show(new RevertDialog(f.Repository, tip)); Assert.That(dialog.AutoCommit, Is.False); Assert.That(Field<StackPanel>(dialog, "RevertParentPanel").IsVisible, Is.False);
            if (automatic) { Field<CheckBox>(dialog, "RevertAutoCommit").Focus(); Press(dialog, PhysicalKey.Space); Assert.That(dialog.AutoCommit, Is.True); }
            Field<Button>(dialog, "ExecuteRevert").Focus(); Press(dialog, PhysicalKey.Enter); await Until(() => !dialog.IsVisible && !dialog.IsBusy, () => dialog.LastError);
            Assert.That(dialog.Applied, Is.True); Assert.That(File.Exists(f.File("added λ [*].txt")), Is.False);
            Assert.That(await f.Head(), automatic ? Is.Not.EqualTo(tip) : Is.EqualTo(tip));
            if (!automatic) { Assert.That((await f.Repository.GetStatusAsync()).Changes.Any(change => change.IndexStatus == 'D'), Is.True); }
        }
    }

    [AvaloniaTest]
    public async Task RevertMergeParentsAndOriginalFrenchLayoutRenderInBothThemes()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = new("fr-FR");
        try
        {
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                await using Fixture f = await Fixture.CreateAsync(); string merge = await f.Merge(); RevertDialog dialog = await f.Show(new RevertDialog(f.Repository, merge) { RequestedThemeVariant = theme });
                Assert.That(dialog.Title, Is.EqualTo("Annuler le commit")); ListBox parents = Field<ListBox>(dialog, "RevertParents"); Assert.That(parents.ItemCount, Is.EqualTo(2)); Assert.That(parents.SelectedIndex, Is.Zero);
                Assert.That(Field<StackPanel>(dialog, "RevertParentPanel").IsVisible, Is.True); Assert.That(dialog.AutoCommit, Is.False);
                ListBoxItem[] rows = parents.GetVisualDescendants().OfType<ListBoxItem>().ToArray(); Assert.That(rows, Has.Length.EqualTo(2)); Assert.That(rows.All(row => row.Bounds.Height == 18), Is.True);
                Capture(dialog, "original-revert-merge-fr-" + theme + ".png"); parents.SelectedIndex = -1; Pump(dialog); Assert.That(Field<Button>(dialog, "ExecuteRevert").IsEnabled, Is.False);
                parents.Focus(); Press(dialog, PhysicalKey.Home); Press(dialog, PhysicalKey.ArrowDown); Assert.That(parents.SelectedIndex, Is.EqualTo(1));
                Pointer(Field<Button>(dialog, "ExecuteRevert")); await Until(() => !dialog.IsVisible && !dialog.IsBusy); Assert.That(dialog.Applied, Is.True);
                Assert.That(File.Exists(f.File("right.txt")), Is.True); Assert.That(File.Exists(f.File("left.txt")), Is.False);
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [AvaloniaTest]
    public async Task RevertConflictCallbackStopsBeforeNextCommitWhenTheConflictIsStillUnresolved()
    {
        await using Fixture f = await Fixture.CreateAsync(); string first = await f.Commit("seed.txt", "first\n", "First"); string second = await f.Commit("seed.txt", "second\n", "Second"); Window owner = f.Owner(); int resolutions = 0;
        Task<bool> sequence = RevertSequence.ShowAsync(owner, f.Repository, [first, second], resolveConflictsAsync: (window, repository, offerCommit, token) =>
        { Assert.That(window, Is.SameAs(owner)); Assert.That(repository.Path, Is.EqualTo(f.Repository.Path)); Assert.That(offerCommit, Is.False); Assert.That(token.IsCancellationRequested, Is.False); resolutions++; return Task.CompletedTask; });
        await Until(() => owner.OwnedWindows.OfType<RevertDialog>().Any()); RevertDialog dialog = owner.OwnedWindows.OfType<RevertDialog>().Single(); Pointer(Field<Button>(dialog, "ExecuteRevert"));
        Assert.That(await sequence.WaitAsync(TimeSpan.FromSeconds(12)), Is.True); Assert.That(resolutions, Is.EqualTo(1)); Assert.That(dialog.Conflicts, Is.EqualTo(new[] { "seed.txt" }));
        Assert.That(await f.Head(), Is.EqualTo(second)); Assert.That((await f.Repository.GetStatusAsync()).Changes.Single().IsConflict, Is.True); Assert.That(owner.OwnedWindows.OfType<RevertDialog>().Any(), Is.False);
    }

    [AvaloniaTest]
    public async Task RevertSequenceCancelSkipsOneDialogAndDoesNotCopyAutomaticOption()
    {
        await using Fixture f = await Fixture.CreateAsync(); string first = await f.Commit("first.txt", "first\n", "First"); string second = await f.Commit("second.txt", "second\n", "Second"); Window owner = f.Owner();
        Task<bool> sequence = RevertSequence.ShowAsync(owner, f.Repository, [first, second]); await Until(() => owner.OwnedWindows.OfType<RevertDialog>().Any()); RevertDialog dialog = owner.OwnedWindows.OfType<RevertDialog>().Single(); Assert.That(dialog.Revision, Is.EqualTo(first));
        Pointer(Field<CheckBox>(dialog, "RevertAutoCommit")); Pointer(Field<Button>(dialog, "CancelRevert"));
        await Until(() => owner.OwnedWindows.OfType<RevertDialog>().Any(next => next.Revision == second)); dialog = owner.OwnedWindows.OfType<RevertDialog>().Single(); Assert.That(dialog.AutoCommit, Is.False, "Each original Revert window starts with its Designer default.");
        Pointer(Field<Button>(dialog, "ExecuteRevert")); Assert.That(await sequence.WaitAsync(TimeSpan.FromSeconds(12)), Is.True);
        Assert.That(await f.Head(), Is.EqualTo(second)); Assert.That(File.Exists(f.File("first.txt")), Is.True); Assert.That(File.Exists(f.File("second.txt")), Is.False);
    }

    [AvaloniaTest]
    public async Task ClosingSequenceOwnerCancelsWithoutApplyingAnyRevertAndInvalidSelectionNeverOpensADialog()
    {
        await using Fixture f = await Fixture.CreateAsync(); string tip = await f.Commit("added.txt", "added\n", "Added"); Window owner = f.Owner(); byte[] index = File.ReadAllBytes(f.File(".git/index"));
        TestContext.WriteLine("Revert owner lifecycle: validating the complete invalid selection asynchronously.");
        GitCommandException? invalid = null;
        try { await RevertSequence.ShowAsync(owner, f.Repository, [tip, "missing"]).WaitAsync(TimeSpan.FromSeconds(12)); }
        catch (GitCommandException error) { invalid = error; }
        Assert.That(invalid, Is.Not.Null); Assert.That(owner.OwnedWindows, Is.Empty);
        TestContext.WriteLine("Revert owner lifecycle: validated without a dialog; starting the valid modal.");
        Task<bool> sequence = RevertSequence.ShowAsync(owner, f.Repository, [tip]);
        await Until(() => owner.OwnedWindows.OfType<RevertDialog>().Any(), () => $"ownerVisible={owner.IsVisible}, sequence={sequence.Status}, windows={string.Join(',', owner.OwnedWindows.Select(window => window.Name))}");
        RevertDialog pending = owner.OwnedWindows.OfType<RevertDialog>().Single();
        TestContext.WriteLine($"Revert owner lifecycle: closing owner; dialogVisible={pending.IsVisible}, busy={pending.IsBusy}, sequence={sequence.Status}.");
        owner.Close();
        try { await sequence.WaitAsync(TimeSpan.FromSeconds(8)); Assert.Fail("Owner close must cancel the sequence."); } catch (OperationCanceledException) { }
        await Until(() => !owner.IsVisible && !pending.IsVisible && !pending.IsBusy,
            () => $"ownerVisible={owner.IsVisible}, dialogVisible={pending.IsVisible}, busy={pending.IsBusy}, sequence={sequence.Status}, error={pending.LastError}");
        TestContext.WriteLine("Revert owner lifecycle: owner and modal closed; cancelled sequence completed.");
        Assert.That(await f.Head(), Is.EqualTo(tip)); Assert.That(File.ReadAllBytes(f.File(".git/index")), Is.EqualTo(index)); Assert.That(File.ReadAllText(f.File("added.txt")), Is.EqualTo("added\n"));
    }

    private static T Field<T>(Control control, string name) where T : Control => control.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static string ResetBranchState(ResetAnotherBranchDialog dialog)
    {
        AutoCompleteBox? branch = dialog.GetVisualDescendants().OfType<AutoCompleteBox>().SingleOrDefault(item => item.Name == "ResetOtherBranchSelection");
        IEnumerable<Popup> popups = branch is null ? [] : branch.GetLogicalDescendants().OfType<Popup>().Concat(branch.GetVisualDescendants().OfType<Popup>()).Distinct();
        return $"visible={dialog.IsVisible}, busy={dialog.IsBusy}, text={branch?.Text}, selected={branch?.SelectedItem}, popup={branch?.IsDropDownOpen}, templatePopups={string.Join(';', popups.Select(popup => popup.Name + ':' + popup.IsOpen))}, editorFocus={branch?.IsKeyboardFocusWithin}, error={dialog.LastError}";
    }
    private static async Task SelectResetBranch(ResetAnotherBranchDialog dialog, string name)
    {
        AutoCompleteBox branch = Field<AutoCompleteBox>(dialog, "ResetOtherBranchSelection"); TextBox editor = branch.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.That(editor.Focus(), Is.True); await Until(() => branch.IsDropDownOpen, () => ResetBranchState(dialog));
        // Commit a real suggestion using the native adapter. Setting SelectedItem
        // directly does not commit/close an AutoCompleteBox popup.
        Press(dialog, PhysicalKey.ArrowDown); Press(dialog, PhysicalKey.Enter);
        await Until(() => branch.Text == name && Equals(branch.SelectedItem, name) && !branch.IsDropDownOpen, () => ResetBranchState(dialog));
        Assert.That(dialog.IsVisible && !dialog.Applied, Is.True, "Choosing the branch must not execute the default reset button."); Pump(dialog);
        TestContext.WriteLine("After committing the suggestion: " + ResetBranchState(dialog)
            + $", Applied={dialog.Applied}, enabled={dialog.IsEnabled}/{dialog.IsEffectivelyEnabled}, owned={string.Join(';', dialog.OwnedWindows.Select(WindowState))}");
    }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static async Task PointerAfterPopupClosed(Button control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; Pump(top);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        top.MouseMove(point);
        Stopwatch timer = Stopwatch.StartNew(); string diagnostic = ""; bool ready = false;
        do
        {
            Pump(top);
            Visual? hit = top.InputHitTest(point) as Visual;
            Point? currentCenter = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top);
            ready = control.IsEnabled && control.IsEffectivelyVisible && currentCenter == point
                && new Rect(default, top.ClientSize).Contains(point) && hit?.GetSelfAndVisualAncestors().Contains(control) == true;
            diagnostic = $"name={control.Name}, point={point}, currentCenter={currentCenter}, bounds={control.Bounds}, client={top.ClientSize}, ready={ready}, hit="
                + string.Join(" -> ", hit?.GetSelfAndVisualAncestors().Select(visual => visual.GetType().Name + "#" + (visual as Control)?.Name + "(" + visual.Bounds + ")") ?? []);
            if (ready) { break; }
            await Task.Delay(10); // Let the compositing renderer commit the popup removal.
        }
        while (timer.Elapsed < TimeSpan.FromSeconds(8));
        TestContext.WriteLine("Pointer after real popup closure: " + diagnostic);
        if (!ready)
        {
            DumpPointerState(top, control, point);
            Capture(top, "pointer-not-ready-" + control.Name + "-" + Guid.NewGuid().ToString("N") + ".png");
        }
        Assert.That(ready, Is.True, diagnostic);
        int clicks = 0; EventHandler<RoutedEventArgs> count = (_, _) => clicks++; control.Click += count;
        try
        {
            // One press and one release only, after the actual renderer hit is ready.
            top.MouseDown(point, MouseButton.Left); top.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.That(clicks, Is.EqualTo(1), "The single real pointer click must reach " + control.Name);
        }
        finally { control.Click -= count; }
    }
    private static string WindowState(Window window) => $"{window.GetType().Name}#{window.Name}(visible={window.IsVisible}, enabled={window.IsEnabled}/{window.IsEffectivelyEnabled}, bounds={window.Bounds}, owner={window.Owner?.GetType().Name}#{window.Owner?.Name})";
    private static void DumpPointerState(TopLevel top, Button control, Point point)
    {
        static string Attached(Visual visual)
        {
            return string.Join(';', AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(visual.GetType())
                .Where(property => property.IsAttached && visual.IsSet(property))
                .Select(property => property.OwnerType.Name + "." + property.Name + "=" + visual.GetValue(property)));
        }
        string Describe(Visual visual)
        {
            Control? item = visual as Control;
            object? background = visual switch { Panel panel => panel.Background, Border border => border.Background,
                ContentPresenter presenter => presenter.Background, TemplatedControl templated => templated.Background, _ => null };
            return $"{visual.GetType().Name}#{item?.Name}(bounds={visual.Bounds}, client={visual.TranslatePoint(default, top)}, visible={visual.IsVisible}, enabled={item?.IsEnabled}/{item?.IsEffectivelyEnabled}, hitTest={item?.IsHitTestVisible}, background={background ?? "null"}, opacity={visual.Opacity}, z={visual.ZIndex}, clip={visual.Clip}, clipToBounds={visual.ClipToBounds}, visualParent={visual.GetVisualParent()?.GetType().Name}#{(visual.GetVisualParent() as Control)?.Name}, logicalParent={(visual as ILogical)?.GetLogicalParent()?.GetType().Name}, attached=[{Attached(visual)}])";
        }
        TestContext.WriteLine("Target control state: " + Describe(control)
            + $", backgroundLocal={control.IsSet(TemplatedControl.BackgroundProperty)}, border={control.BorderBrush}/{control.BorderThickness}, focused={control.IsFocused}/{control.IsKeyboardFocusWithin}");
        if (top is Window owner)
        {
            TestContext.WriteLine("Owner state: " + WindowState(owner));
            TestContext.WriteLine("Owned windows: " + string.Join(';', owner.OwnedWindows.Select(WindowState)));
        }
        if (top is ResetAnotherBranchDialog reset)
        {
            TestContext.WriteLine("Reset state at timeout: " + ResetBranchState(reset)
                + $", Applied={reset.Applied}, ResetBranch={reset.ResetBranch}, CheckoutRequested={reset.CheckoutRequested}, CheckoutSucceeded={reset.CheckoutSucceeded}");
        }
        IClassicDesktopStyleApplicationLifetime? lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        TestContext.WriteLine("Application lifetime windows: " + (lifetime is null ? "no classic desktop lifetime" : string.Join(';', lifetime.Windows.Select(WindowState))));
        foreach (Visual visual in top.GetVisualDescendants().OfType<Panel>())
        {
            Point? origin = visual.TranslatePoint(default, top);
            if (origin.HasValue && new Rect(origin.Value, visual.Bounds.Size).Contains(point))
            { TestContext.WriteLine("Panel covering the pointer: " + Describe(visual)); }
        }
        foreach (Visual visual in control.GetSelfAndVisualDescendants()) { TestContext.WriteLine("Button template: " + Describe(visual)); }
        IEnumerable<Point> probes = control.GetVisualDescendants().OfType<AccessText>()
            .Select(text => text.TranslatePoint(new Point(text.Bounds.Width / 2, text.Bounds.Height / 2), top))
            .Where(candidate => candidate.HasValue).Select(candidate => candidate!.Value)
            .Prepend(control.TranslatePoint(new Point(.5, control.Bounds.Height / 2), top)!.Value);
        foreach (Point probe in probes)
        {
            Visual? hit = top.InputHitTest(probe) as Visual;
            TestContext.WriteLine($"Read-only border/text hit probe {probe}: " + string.Join(" -> ", hit?.GetSelfAndVisualAncestors().Select(Describe) ?? []));
        }
    }
    private static void Pointer(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; Pump(top); Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        Assert.That(control.IsEnabled && control.IsEffectivelyVisible && new Rect(default, top.ClientSize).Contains(point), Is.True, control.Name);
        Visual? hit = top.InputHitTest(point) as Visual;
        string Describe(Visual visual)
        {
            Control? item = visual as Control; Point? position = visual.TranslatePoint(default, top);
            return $"{visual.GetType().Name}#{item?.Name}(bounds={visual.Bounds}, client={position}, visible={visual.IsVisible}, enabled={item?.IsEnabled}, hitTest={item?.IsHitTestVisible})";
        }
        string diagnostic = $"Real pointer target: {control.Name}, center={point}, client={top.ClientSize}, intended={Describe(control)}, hit={string.Join(" -> ", hit?.GetSelfAndVisualAncestors().Select(Describe) ?? [])}";
        bool correct = hit?.GetSelfAndVisualAncestors().Contains(control) == true;
        if (!correct)
        {
            TestContext.WriteLine(diagnostic);
            if (top is ResetAnotherBranchDialog reset) { TestContext.WriteLine(ResetBranchState(reset)); }
            Capture(top, "pointer-mask-" + control.Name + "-" + Guid.NewGuid().ToString("N") + ".png");
        }
        Assert.That(correct, Is.True, diagnostic);
        top.MouseMove(point); top.MouseDown(point, MouseButton.Left); top.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static void Press(TopLevel window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None, TopLevel? releaseOn = null)
    { window.KeyPressQwerty(key, modifiers); if (releaseOn is not null) { releaseOn.KeyReleaseQwerty(key, modifiers); } else if (window is not Window native || native.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition, Func<string?>? diagnostic = null)
    { Stopwatch timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True, "Timed out waiting for Reset/Revert UI: " + diagnostic?.Invoke()); }
    private static void Capture(TopLevel window, string name)
    { Pump(window); using Bitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }
    private static Color Pixel(TopLevel window, Point point)
    {
        Pump(window); using Bitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        using WriteableBitmap copy = new(frame!.PixelSize, frame.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul); using ILockedFramebuffer buffer = copy.Lock(); frame.CopyPixels(buffer, AlphaFormat.Unpremul);
        byte[] bytes = new byte[4]; Marshal.Copy(buffer.Address + (int)point.Y * buffer.RowBytes + (int)point.X * 4, bytes, 0, 4); return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }
    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color) { static double Linear(byte x) { double v = x / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); } return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B); }
        double a = Luminance(first), b = Luminance(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), _locks = Environment.GetEnvironmentVariable("GIT_OPTIONAL_LOCKS");
        private readonly GitRunner _runner = new(); public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-reset-revert-ui-" + Guid.NewGuid().ToString("N"));
        public GitRepository Repository => new(Path.Combine(Root, "repo")); public List<Window> Windows { get; } = []; public string Initial { get; private set; } = "";
        public string File(string path) => Path.Combine(Repository.Path, path);
        public void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(File(path))!); System.IO.File.WriteAllText(File(path), text); }
        public static async Task<Fixture> CreateAsync()
        {
            Fixture f = new(); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(f.Root, "config")); Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0"); Directory.CreateDirectory(f.Repository.Path);
            await f.Git("init", "-b", "main"); await f.Git("config", "user.name", "Reset Revert UI"); await f.Git("config", "user.email", "reset-revert@example.invalid"); await f.Git("config", "commit.gpgsign", "false"); await f.Git("config", "core.hooksPath", "/dev/null"); f.Initial = await f.Commit("seed.txt", "seed\n", "Initial"); return f;
        }
        public async Task<GitResult> GitAt(GitRepository repository, params string[] arguments)
        { GitResult result = await _runner.RunAsync(repository.Path, arguments, environment: new Dictionary<string, string?> { ["GIT_OPTIONAL_LOCKS"] = "0" }); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public Task<GitResult> Git(params string[] arguments) => GitAt(Repository, arguments);
        public async Task<string> Text(params string[] arguments) => (await Git(arguments)).StandardOutput.TrimEnd('\r', '\n'); public Task<string> Head() => Text("rev-parse", "HEAD");
        public async Task<string> Commit(string path, string text, string message) { Write(path, text); await Git("add", "--", path); await Git("commit", "-m", message); return await Head(); }
        public async Task<string> Merge()
        { await Git("checkout", "-b", "left"); await Commit("left.txt", "left\n", "Left parent"); await Git("checkout", "-b", "right", Initial); await Commit("right.txt", "right\n", "Right parent"); await Git("checkout", "left"); await Git("merge", "--no-ff", "right", "-m", "Merge"); return await Head(); }
        public Window Owner() { Window owner = new() { Width = 800, Height = 600 }; Windows.Add(owner); owner.Show(); Pump(owner); return owner; }
        public async Task<T> Show<T>(T dialog) where T : RepositoryDialog { Windows.Add(dialog); dialog.Show(); await dialog.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Pump(dialog); return dialog; }
        public async ValueTask DisposeAsync()
        {
            static void CloseChildren(Window window) { foreach (Window child in window.OwnedWindows.ToArray()) { CloseChildren(child); child.Close(); } }
            foreach (Window window in Windows.AsEnumerable().Reverse()) { CloseChildren(window); window.Close(); }
            Stopwatch timer = Stopwatch.StartNew(); while (Windows.OfType<RepositoryDialog>().Any(window => window.IsBusy) && timer.Elapsed < TimeSpan.FromSeconds(8)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            foreach (Window window in Windows) { window.Close(); } Dispatcher.UIThread.RunJobs(); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", _locks); if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
        }
    }
}

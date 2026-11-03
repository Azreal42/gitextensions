using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using RootFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ChooseCommitDialogTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task SourceFooterFitsAtTheMinimumWithTwoParentsAndTheFirstArrowWorksWithoutAnotherClick(string language, bool dark)
    {
        using Fixture f = await Fixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        TranslationCatalog translations = TranslationCatalog.LoadDefault(); string before = await Materials(f.Root);
        await using Lease s = await Lease.OpenAsync(f.Repository, new(f.Merge), dark: dark);
        TestChooser dialog = s.Dialog; ListBox rows = Field<ListBox>(dialog, "ChooseRows");
        Assert.That(dialog.Title, Is.EqualTo(translations.Translate("FormChooseCommit", "$this", "Text", "Choose Commit")));
        Assert.That(Field<Button>(dialog, "ChooseAccept").Content, Is.EqualTo(translations.Translate("FormChooseCommit", "btnOK", "Text", "OK")));
        Assert.That(dialog.Width, Is.EqualTo(1105 * 96d / 120)); Assert.That(dialog.Height, Is.EqualTo(515 * 96d / 120));
        Assert.That(dialog.CanMinimize || dialog.ShowInTaskbar, Is.False); Assert.That(dialog.Icon, Is.Not.Null);
        Assert.That(dialog.ActualThemeVariant, Is.EqualTo(s.Owner.ActualThemeVariant));
        dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight; PumpTree(s.Owner);
        foreach (string name in new[] { "label1", "buttonGotoCommit", "labelParents", "linkLabelParent", "linkLabelParent2", "ChooseAccept", "ChooseCancel", "ChooseSearch", "ChooseSearchAction" })
        {
            Control control = Field<Control>(dialog, name); Rect bounds = Bounds(dialog, control);
            Assert.That(control.IsEffectivelyVisible && bounds.Width > 0 && bounds.Height > 0
                && new Rect(default, dialog.ClientSize).Contains(bounds), Is.True, name + ": " + bounds + " client=" + dialog.ClientSize);
        }
        Assert.That(Field<Button>(dialog, "linkLabelParent").Tag, Is.EqualTo(f.Main));
        Assert.That(Field<Button>(dialog, "linkLabelParent2").Tag, Is.EqualTo(f.Side));
        Capture(dialog, "choose-commit-minimum-" + language + (dark ? "-dark.png" : "-light.png"));
        int original = rows.SelectedIndex; Assert.That(rows.IsKeyboardFocusWithin, Is.True);
        Assert.That(original + 1, Is.LessThan(rows.ItemCount));
        Press(dialog, PhysicalKey.ArrowDown); Assert.That(rows.SelectedIndex, Is.EqualTo(original + 1));
        Assert.That(rows.IsKeyboardFocusWithin, Is.True); Press(dialog, PhysicalKey.ArrowUp);
        Assert.That(((GitCommit)rows.SelectedItem!).Hash, Is.EqualTo(f.Merge)); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
        Pointer(Field<Button>(dialog, "ChooseCancel")); Assert.That(await s.Selection, Is.Null);
        Assert.That(await Materials(f.Root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase("HEAD~1", true)] [TestCase("Tag/Été", true)] [TestCase("not-a-revision", false)]
    public async Task PreselectionResolvesExpressionsAndAnnotatedTagsAndIgnoresInvalidExpressions(string expression, bool valid)
    {
        using Fixture f = await Fixture.CreateAsync(); string before = await Materials(f.Root);
        await using Lease s = await Lease.OpenAsync(f.Repository, new(expression));
        ListBox rows = Field<ListBox>(s.Dialog, "ChooseRows"); string expected = valid
            ? await f.Repository.ResolveRevisionAsync(expression) : ((GitCommit)rows.Items[0]!).Hash;
        Assert.That(((GitCommit)rows.SelectedItem!).Hash, Is.EqualTo(expected));
        Assert.That(s.Dialog.LastError, Is.Null); Press(s.Dialog, PhysicalKey.Enter);
        Assert.That((await s.Selection)!.Hash, Is.EqualTo(expected)); Assert.That(await Materials(f.Root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task NativeMergeBaseBoundaryIsExclusiveAndCurrentBranchModeNeverIncludesTheOppositeSide()
    {
        using Fixture f = await Fixture.CreateAsync(merge: false); string before = await Materials(f.Root);
        string? boundary = await f.Repository.GetComparisonMergeBaseAsync(new(f.Main), new(f.Side));
        Assert.That(boundary, Is.EqualTo(f.First));
        GitLogQuery query = new(Count: 100, Revision: "HEAD", All: false) { LastRevisionToDisplayHash = boundary };
        Assert.That(query.HasRevisionFilter, Is.False, "The Windows boundary is independent of the parent-rewriting filters.");
        string[] native = (await f.Root.GitAsync("log", "--format=%H", "HEAD", "..." + boundary, "--")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(native, Is.EqualTo(new[] { f.Main }));
        Assert.That((await f.Repository.GetLogAsync(query)).Select(commit => commit.Hash), Is.EqualTo(native));
        Assert.That((await f.Repository.GetRevisionGraphAsync(query)).Select(commit => commit.Hash), Is.EqualTo(native));
        await using Lease s = await Lease.OpenAsync(f.Repository, new("HEAD^", true, boundary));
        Assert.That(Field<ListBox>(s.Dialog, "ChooseRows").Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(native));
        Assert.That(Field<ListBox>(s.Dialog, "ChooseRows").Items.OfType<GitCommit>().Any(commit => commit.Hash == f.First || commit.Hash == f.Side), Is.False);
        Pointer(Field<Button>(s.Dialog, "ChooseCancel")); await s.Selection; Assert.That(await Materials(f.Root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task EqualHeadAndBoundaryLeaveNoSelectableRevisionRatherThanIncludingTheBoundary()
    {
        using Fixture f = await Fixture.CreateAsync(merge: false);
        await using Lease s = await Lease.OpenAsync(f.Repository, new("HEAD", true, f.Main));
        Assert.That(Field<ListBox>(s.Dialog, "ChooseRows").Items, Is.Empty);
        Assert.That(Field<Button>(s.Dialog, "ChooseAccept").IsEnabled, Is.False); Assert.That(s.Dialog.LastError, Is.Null);
        Press(s.Dialog, PhysicalKey.Enter); Assert.That(s.Dialog.IsVisible, Is.True); Assert.That(s.Selection.IsCompleted, Is.False);
        Press(s.Dialog, PhysicalKey.Escape); Assert.That(await s.Selection, Is.Null);
    }

    [AvaloniaTest]
    public async Task RebaseLauncherComputesTheRealMergeBaseWithoutChangingOptionsOrRepository()
    {
        using Fixture f = await Fixture.CreateAsync(merge: false); string before = await Materials(f.Root);
        Window owner = new() { Width = 1200, Height = 800 }; owner.Show();
        RebaseDialog rebase = new(f.Repository, initialOnto: "topic"); Task modal = rebase.ShowDialog(owner);
        try
        {
            await rebase.InitializeAsync(); PumpTree(owner);
            Pointer(Field<CheckBox>(rebase, "RebaseSpecificRange"));
            Assert.That(Field<TextBox>(rebase, "RebaseRangeFrom").Text, Is.Null.Or.Empty);
            Pointer(Field<Button>(rebase, "ChooseRebaseFrom"));
            RebaseRevisionPicker chooser = await Child<RebaseRevisionPicker>(rebase);
            await Until(owner, () => !chooser.IsBusy && Field<ListBox>(chooser, "RebaseChooseRevision").ItemCount == 1);
            Assert.That(Field<ListBox>(chooser, "RebaseChooseRevision").Items.OfType<GitCommit>().Single().Hash, Is.EqualTo(f.Main));
            Pointer(Field<Button>(chooser, "ChooseRebaseCommit")); await Until(owner, () => !chooser.IsVisible && !rebase.IsBusy);
            Assert.That(Field<TextBox>(rebase, "RebaseRangeFrom").Text, Is.EqualTo(f.Main));
            Assert.That(Field<ComboBox>(rebase, "RebaseOn").Text, Is.EqualTo("topic"));
            Press(rebase, PhysicalKey.Escape); await modal.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(rebase.HasRepositoryChanges, Is.False); Assert.That(await Materials(f.Root), Is.EqualTo(before));
        }
        finally { foreach (Window child in rebase.OwnedWindows.ToArray()) { child.Close(); } rebase.Close(); owner.Close(); await Until(owner, () => !rebase.IsVisible && !rebase.IsBusy); }
    }

    [AvaloniaTest]
    public async Task BothParentsAndTheRootUseTheExistingRowsWithoutReloadingOrAccepting()
    {
        using Fixture f = await Fixture.CreateAsync(); string before = await Materials(f.Root);
        await using Lease s = await Lease.OpenAsync(f.Repository, new(f.Merge)); ListBox rows = Field<ListBox>(s.Dialog, "ChooseRows"); object source = rows.ItemsSource!;
        Pointer(Field<Button>(s.Dialog, "linkLabelParent2")); await s.Dialog.Completion;
        Assert.That(((GitCommit)rows.SelectedItem!).Hash, Is.EqualTo(f.Side)); Assert.That(rows.ItemsSource, Is.SameAs(source));
        Assert.That(Field<TextBlock>(s.Dialog, "OperationStatus").IsEffectivelyVisible, Is.False, "An idle navigation must not leave its loading band visible.");
        await GoTo(s, f.Merge); Assert.That(((GitCommit)rows.SelectedItem!).Hash, Is.EqualTo(f.Merge));
        Pointer(Field<Button>(s.Dialog, "linkLabelParent")); await s.Dialog.Completion;
        Assert.That(((GitCommit)rows.SelectedItem!).Hash, Is.EqualTo(f.Main)); Assert.That(Field<Button>(s.Dialog, "linkLabelParent2").IsVisible, Is.False);
        Pointer(Field<Button>(s.Dialog, "linkLabelParent")); await s.Dialog.Completion;
        Assert.That(((GitCommit)rows.SelectedItem!).Hash, Is.EqualTo(f.First));
        Assert.That(Field<StackPanel>(s.Dialog, "flowLayoutPanelParents").IsVisible, Is.False);
        Assert.That(rows.ItemsSource, Is.SameAs(source)); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
        Assert.That(Field<TextBlock>(s.Dialog, "OperationStatus").IsEffectivelyVisible, Is.False);
        Assert.That(s.Selection.IsCompleted, Is.False); Assert.That(await Materials(f.Root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task FilteredParentsAndGoToShowOwnedErrorsAndKeepRowsSelectionAndFilterForRetry()
    {
        using Fixture f = await Fixture.CreateAsync(); string before = await Materials(f.Root);
        await using Lease s = await Lease.OpenAsync(f.Repository, new(f.Merge));
        Edit(s.Dialog, Field<TextBox>(s.Dialog, "ChooseSearch"), "Merge selected"); Pointer(Field<Button>(s.Dialog, "ChooseSearchAction")); await s.Dialog.Completion;
        ListBox rows = Field<ListBox>(s.Dialog, "ChooseRows"); object source = rows.ItemsSource!;
        Assert.That(rows.ItemCount, Is.EqualTo(1)); Assert.That(((GitCommit)rows.SelectedItem!).Hash, Is.EqualTo(f.Merge));
        Pointer(Field<Button>(s.Dialog, "linkLabelParent")); Window parentError = await Message(s.Dialog);
        Assert.That(parentError.Owner, Is.SameAs(s.Dialog)); Assert.That(Field<TextBlock>(parentError, "MessageDialogText").Text, Does.Contain(f.Main[..8]).And.Contain("not visible"));
        Pointer(Field<Button>(parentError, "MessageDialogAccept")); await s.Dialog.Completion;
        foreach ((string expression, string error) in new[] { ("Tag/Été", "not visible"), ("not-a-revision", "No revision found.") })
        {
            Pointer(Field<Button>(s.Dialog, "buttonGotoCommit")); GoToCommitDialog go = await Child<GoToCommitDialog>(s.Dialog);
            await go.InitializeAsync(); Edit(go, Field<TextBox>(go, "textboxCommitExpression"), expression); Press(go, PhysicalKey.Enter);
            Window message = await Message(s.Dialog); Assert.That(message.Owner, Is.SameAs(s.Dialog));
            Assert.That(Field<TextBlock>(message, "MessageDialogText").Text, Does.Contain(error));
            Pointer(Field<Button>(message, "MessageDialogAccept")); await s.Dialog.Completion;
            Assert.That(rows.ItemsSource, Is.SameAs(source)); Assert.That(((GitCommit)rows.SelectedItem!).Hash, Is.EqualTo(f.Merge));
            Assert.That(Field<TextBox>(s.Dialog, "ChooseSearch").Text, Is.EqualTo("Merge selected"));
            Assert.That(rows.IsKeyboardFocusWithin, Is.True); Assert.That(s.Dialog.IsVisible, Is.True);
            Assert.That(Field<TextBlock>(s.Dialog, "OperationStatus").IsEffectivelyVisible, Is.False, "The owned message is already acknowledged; no loading band remains at idle.");
        }
        Edit(s.Dialog, Field<TextBox>(s.Dialog, "ChooseSearch"), ""); Pointer(Field<Button>(s.Dialog, "ChooseSearchAction")); await s.Dialog.Completion;
        Assert.That(((GitCommit)rows.SelectedItem!).Hash, Is.EqualTo(f.Merge), "A still-visible selection survives changing the filter.");
        await GoTo(s, "Tag/Été"); Assert.That(((GitCommit)rows.SelectedItem!).Hash, Is.EqualTo(f.Main));
        Assert.That(await Materials(f.Root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task CancelingTheRealGoToReturnsFocusAndTheFirstArrowNavigates(bool windowClose)
    {
        using Fixture f = await Fixture.CreateAsync(); await using Lease s = await Lease.OpenAsync(f.Repository, new(f.Merge));
        ListBox rows = Field<ListBox>(s.Dialog, "ChooseRows"); object source = rows.ItemsSource!; int selected = rows.SelectedIndex;
        Pointer(Field<Button>(s.Dialog, "buttonGotoCommit")); GoToCommitDialog go = await Child<GoToCommitDialog>(s.Dialog); await go.InitializeAsync();
        Edit(go, Field<TextBox>(go, "textboxCommitExpression"), "HEAD^");
        if (windowClose) { go.Close(); } else { Press(go, PhysicalKey.Escape); }
        await s.Dialog.Completion; Assert.That(go.WasAccepted, Is.False); Assert.That(rows.ItemsSource, Is.SameAs(source));
        Assert.That(rows.SelectedIndex, Is.EqualTo(selected)); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
        Assert.That(Field<TextBlock>(s.Dialog, "OperationStatus").IsEffectivelyVisible, Is.False);
        Press(s.Dialog, PhysicalKey.ArrowDown); Assert.That(rows.SelectedIndex, Is.EqualTo(selected + 1)); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    [TestCase("Export")] [TestCase("CherryPick")] [TestCase("Revert")] [TestCase("Rebase")]
    public async Task HistoricalAdapterClassesIdsAndActualCommitResultsArePreserved(string adapter)
    {
        using Fixture f = await Fixture.CreateAsync(); string before = await Materials(f.Root);
        Window owner = new() { Width = 1000, Height = 700 }; owner.Show();
        Task<GitCommit?> selection = adapter switch
        {
            "Export" => ExportRevisionPicker.ShowAsync(owner, f.Repository, "Tag/Été"),
            "CherryPick" => CherryPickRevisionPicker.ShowAsync(owner, f.Repository, "Tag/Été"),
            "Revert" => RevertRevisionPicker.ShowAsync(owner, f.Repository, "Tag/Été", default),
            _ => RebaseRevisionPicker.ShowAsync(owner, f.Repository, "Tag/Été", default)
        };
        string rowsId = adapter switch { "Export" => "ChooseRevision", "CherryPick" => "CherryPickChooseRevision", "Revert" => "RevertChooseRevision", _ => "RebaseChooseRevision" };
        string acceptId = adapter switch { "Export" => "Choose", "CherryPick" => "ChooseCherryPickCommit", "Revert" => "ChooseRevertCommit", _ => "ChooseRebaseCommit" };
        try
        {
            await Until(owner, () => owner.OwnedWindows.OfType<ChooseCommitDialog>().Any(child => child.IsVisible && !child.IsBusy
                && (Field<ListBox>(child, rowsId).SelectedItem as GitCommit)?.Hash == f.Main
                && Field<Button>(child, acceptId).IsEffectivelyEnabled));
            ChooseCommitDialog dialog = owner.OwnedWindows.OfType<ChooseCommitDialog>().Single();
            Assert.That(dialog.GetType().Name, Is.EqualTo(adapter + "RevisionPicker"));
            Assert.That(((GitCommit)Field<ListBox>(dialog, rowsId).SelectedItem!).Hash, Is.EqualTo(f.Main));
            Pointer(Field<Button>(dialog, acceptId)); GitCommit result = (await selection)!;
            Assert.That(result.Hash, Is.EqualTo(f.Main)); Assert.That(result.Parents, Is.EqualTo(new[] { f.First }));
            Assert.That(await Materials(f.Root), Is.EqualTo(before)); Assert.That(owner.IsVisible, Is.True);
        }
        finally { foreach (Window child in owner.OwnedWindows.ToArray()) { child.Close(); } owner.Close(); await selection; }
    }

    [AvaloniaTest]
    public async Task CancellationAtTheRealAcceptClickCannotPublishTheAlreadySelectedCommit()
    {
        using Fixture f = await Fixture.CreateAsync(); string before = await Materials(f.Root);
        using CancellationTokenSource token = new(); await using Lease s = await Lease.OpenAsync(f.Repository, new(f.Merge), token: token.Token);
        Assert.That(((GitCommit)Field<ListBox>(s.Dialog, "ChooseRows").SelectedItem!).Hash, Is.EqualTo(f.Merge));
        bool closing = false;
        s.Dialog.Closing += (_, _) => { if (!closing) { closing = true; token.Cancel(); } };
        Pointer(Field<Button>(s.Dialog, "ChooseAccept"));
        Assert.CatchAsync<OperationCanceledException>(async () => await s.Selection);
        await s.Dialog.Completion; Assert.That(s.Dialog.IsVisible, Is.False); Assert.That(s.Owner.IsVisible, Is.True);
        Assert.That(await Materials(f.Root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase("escape")] [TestCase("owner")] [TestCase("token")]
    public async Task ClosingDuringAnActualLogReadDrainsItsPidAndNeverPublishesASelection(string close)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private native gates require Linux shell/procfs."); return; }
        using Fixture f = await Fixture.CreateAsync(); Gate gate = await Gate.CreateAsync(f.Root, "log"); string before = await Materials(f.Root);
        using CancellationTokenSource token = new(); await using Lease s = Lease.Begin(gate.Repository, new(), token.Token);
        try
        {
            await Until(s.Owner, () => gate.Identity is not null); var identity = gate.Identity!.Value;
            Assert.That(s.Dialog.IsBusy && s.Dialog.IsVisible, Is.True); Assert.That(s.Selection.IsCompleted || s.Dialog.Completion.IsCompleted, Is.False);
            if (close == "token") { token.Cancel(); } else if (close == "owner") { s.Owner.Close(); } else { Press(s.Dialog, PhysicalKey.Escape); }
            await Until(s.Owner, () => s.Selection.IsCompleted && !s.Dialog.IsVisible && (close != "owner" || !s.Owner.IsVisible)); await s.Dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            if (close == "token") { Assert.CatchAsync<OperationCanceledException>(async () => await s.Selection); }
            else { Assert.That(await s.Selection, Is.Null); }
            Assert.That(Alive(identity), Is.False, "Completion must drain the exact native PID/start identity.");
            Assert.That(s.Owner.IsVisible, Is.EqualTo(close != "owner")); Assert.That(await Materials(f.Root), Is.EqualTo(before));
        }
        finally { await gate.ReleaseAsync(); }
    }

    [AvaloniaTest]
    [TestCase("owner")] [TestCase("token")]
    public async Task ClosingWhileTheSharedGoToReadsReferencesDrainsBothWindowsAndItsActualPid(string close)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private native gates require Linux shell/procfs."); return; }
        using Fixture f = await Fixture.CreateAsync(); Gate gate = await Gate.CreateAsync(f.Root, "goto"); string before = await Materials(f.Root);
        using CancellationTokenSource token = new(); await using Lease s = await Lease.OpenAsync(gate.Repository, new(f.Merge), token: token.Token);
        try
        {
            Pointer(Field<Button>(s.Dialog, "buttonGotoCommit")); GoToCommitDialog go = await Child<GoToCommitDialog>(s.Dialog);
            await Until(s.Owner, () => gate.Identity is not null); var identity = gate.Identity!.Value;
            Assert.That(go.IsLoadingTags && s.Dialog.IsBusy, Is.True); Assert.That(s.Dialog.Completion.IsCompleted, Is.False);
            if (close == "token") { token.Cancel(); } else { s.Owner.Close(); }
            await Until(s.Owner, () => s.Selection.IsCompleted && !s.Dialog.IsVisible && !go.IsVisible && (close != "owner" || !s.Owner.IsVisible));
            await s.Dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10)); await go.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            if (close == "token") { Assert.CatchAsync<OperationCanceledException>(async () => await s.Selection); } else { Assert.That(await s.Selection, Is.Null); }
            Assert.That(Alive(identity), Is.False); Assert.That(s.Owner.OwnedWindows.Any(child => child.IsVisible), Is.False);
            Assert.That(s.Owner.IsVisible, Is.EqualTo(close != "owner"));
            Assert.That(await Materials(f.Root), Is.EqualTo(before));
        }
        finally { await gate.ReleaseAsync(); }
    }

    [AvaloniaTest]
    [TestCase(true)] [TestCase(false)]
    public async Task CurrentBranchReadsHandleUnbornAndDetachedHeadWithoutArtificialCommits(bool unborn)
    {
        using RootFixture root = await RootFixture.CreateAsync(unborn);
        if (!unborn) { await root.ChangeAsync("later\n", "Later"); await root.GitAsync("checkout", "--detach", "HEAD^"); }
        string? head = (await root.Repository.GetStatusAsync()).Head;
        await using Lease s = await Lease.OpenAsync(root.Repository, new(CurrentBranchOnly: true));
        ListBox rows = Field<ListBox>(s.Dialog, "ChooseRows"); Assert.That(s.Dialog.LastError, Is.Null);
        if (unborn) { Assert.That(rows.Items, Is.Empty); Assert.That(Field<Button>(s.Dialog, "ChooseAccept").IsEnabled, Is.False); }
        else { Assert.That(rows.Items.OfType<GitCommit>().Single().Hash, Is.EqualTo(head)); }
        Press(s.Dialog, PhysicalKey.Escape); Assert.That(await s.Selection, Is.Null);
    }

    private static async Task GoTo(Lease s, string expression)
    {
        Pointer(Field<Button>(s.Dialog, "buttonGotoCommit")); GoToCommitDialog go = await Child<GoToCommitDialog>(s.Dialog); await go.InitializeAsync();
        Edit(go, Field<TextBox>(go, "textboxCommitExpression"), expression); Press(go, PhysicalKey.Enter);
        await s.Dialog.Completion; Assert.That(go.WasAccepted, Is.True); Assert.That(go.SelectedCommitHash, Is.Not.Null);
        Assert.That(s.Dialog.OwnedWindows.Any(child => child.IsVisible), Is.False); Assert.That(Field<ListBox>(s.Dialog, "ChooseRows").IsKeyboardFocusWithin, Is.True);
        Assert.That(Field<TextBlock>(s.Dialog, "OperationStatus").IsEffectivelyVisible, Is.False);
    }

    private static async Task<string> Materials(RootFixture f)
    {
        string config = f.Output("config");
        return (await f.GitAsync("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput + "\n"
            + string.Join('\n', new[] { ".git/HEAD", ".git/index", ".git/config", "file.txt" }.Select(path => path + ":" + Convert.ToHexString(File.ReadAllBytes(f.PathInRepository(path)))))
            + "\nprofile:" + (Directory.Exists(config) ? string.Join('\n', Directory.EnumerateFiles(config, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => path + ":" + Convert.ToHexString(File.ReadAllBytes(path)))) : "");
    }
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static Rect Bounds(Window window, Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void Pointer(Control control) => PushProcessTestDriver.Pointer(control);
    private static void Edit(Window window, TextBox editor, string text)
    { Pointer(editor); Press(window, PhysicalKey.A, RawInputModifiers.Control); Press(window, PhysicalKey.Backspace); if (text.Length > 0) { window.KeyTextInput(text); } PumpTree(window); Assert.That(editor.Text ?? "", Is.EqualTo(text)); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } PumpTree(window); }
    private static void PumpTree(Window root)
    {
        Dispatcher.UIThread.RunJobs();
        if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        foreach (Window child in root.OwnedWindows.ToArray()) { PumpTree(child); }
        Dispatcher.UIThread.RunJobs();
    }
    private static async Task Until(Window owner, Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { PumpTree(owner); await Task.Delay(10); }
        PumpTree(owner); if (!condition() && owner.IsVisible) { Capture(owner, "choose-commit-timeout-" + Guid.NewGuid().ToString("N") + ".png"); }
        Assert.That(condition(), Is.True, "Actual chooser workflow timed out: " + string.Join("; ", owner.OwnedWindows.Select(window => window.Name + " visible=" + window.IsVisible + (window is RepositoryDialog dialog ? " busy=" + dialog.IsBusy + " error=" + dialog.LastError : ""))));
    }
    private static async Task<T> Child<T>(Window owner) where T : Window
    { await Until(owner, () => owner.OwnedWindows.OfType<T>().Any(window => window.IsVisible)); return owner.OwnedWindows.OfType<T>().Single(window => window.IsVisible); }
    private static async Task<Window> Message(Window owner)
    { await Until(owner, () => owner.OwnedWindows.Any(window => window.Name == "MessageDialog" && window.IsVisible)); return owner.OwnedWindows.Single(window => window.Name == "MessageDialog" && window.IsVisible); }
    private static void Capture(Window window, string name)
    {
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); using var frame = window.GetLastRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string? StartIdentity(int pid)
    { try { string stat = File.ReadAllText("/proc/" + pid + "/stat"); return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19]; } catch (IOException) { return null; } }
    private static bool Alive((int Pid, string Start) identity)
    {
        try { string stat = File.ReadAllText("/proc/" + identity.Pid + "/stat"); string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' '); return fields[19] == identity.Start; }
        catch (IOException) { return false; }
    }

    private sealed class TestChooser : ChooseCommitDialog
    {
        private readonly CancellationToken _testToken;
        internal TestChooser(GitRepository repository, ChooseCommitRequest request, CancellationToken token)
            : base(repository, request, new("ChooseRows", "ChooseAccept", "ChooseCancel", "ChooseSearch", "ChooseSearchAction"), token) => _testToken = token;
        internal Task<GitCommit?> OpenAsync(Window owner) => ShowPickerAsync(this, owner, _testToken);
    }

    private sealed class Lease : IAsyncDisposable
    {
        internal Window Owner { get; }
        internal TestChooser Dialog { get; }
        internal Task<GitCommit?> Selection { get; }
        private readonly CancellationToken _token;
        private Lease(GitRepository repository, ChooseCommitRequest request, CancellationToken token, bool dark)
        {
            _token = token; Owner = new() { Width = 1200, Height = 800, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; Owner.Show();
            Dialog = new(repository, request, token); Selection = Dialog.OpenAsync(Owner); PumpTree(Owner);
        }
        internal static Lease Begin(GitRepository repository, ChooseCommitRequest request, CancellationToken token = default, bool dark = false) => new(repository, request, token, dark);
        internal static async Task<Lease> OpenAsync(GitRepository repository, ChooseCommitRequest request, bool dark = false, CancellationToken token = default)
        {
            Lease s = new(repository, request, token, dark);
            try { await Until(s.Owner, () => !s.Dialog.IsBusy && s.Dialog.Completion.IsCompleted); await s.Dialog.Completion; PumpTree(s.Owner); return s; }
            catch { await s.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            foreach (Window child in Dialog.OwnedWindows.ToArray()) { child.Close(); }
            Dialog.Close(); await Until(Owner, () => Selection.IsCompleted && !Dialog.IsBusy && !Dialog.IsVisible);
            await Dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            try { await Selection.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            Owner.Close(); PumpTree(Owner);
        }
    }

    private sealed class Gate(GitRepository repository, string directory)
    {
        internal GitRepository Repository { get; } = repository;
        internal (int Pid, string Start)? Identity
        {
            get
            {
                string path = Path.Combine(directory, "native.pid");
                if (!File.Exists(path) || !int.TryParse(File.ReadAllText(path), out int pid)) { return null; }
                return StartIdentity(pid) is string start ? (pid, start) : null;
            }
        }
        internal Task ReleaseAsync() => File.WriteAllTextAsync(Path.Combine(directory, "release"), "release\n");
        internal static async Task<Gate> CreateAsync(RootFixture f, string kind)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Native PID gates require Linux."); }
            string directory = f.Output("choose gate " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); string script = Path.Combine(directory, "git");
            string match = kind == "log" ? "for arg in \"$@\"; do [ \"$arg\" != log ] || blocked=yes; done\n"
                : "for arg in \"$@\"; do [ \"$arg\" != refs/tags/ ] || blocked=yes; done\n";
            await File.WriteAllTextAsync(script, "#!/bin/sh\nblocked=no\n" + match + "if [ \"$blocked\" = yes ]; then\n"
                + TestSignalFile.ShellPids(Path.Combine(directory, "native.pid"), "$$")
                + "while [ ! -f " + Quote(Path.Combine(directory, "release")) + " ]; do sleep .02; done; fi\nexec /usr/bin/git \"$@\"\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new(new GitRepository(f.Repository.Path, new GitRunner(script)), directory);
        }
    }

    private sealed class Fixture(RootFixture root, string first, string main, string side, string merge) : IDisposable
    {
        internal RootFixture Root { get; } = root;
        internal GitRepository Repository => Root.Repository;
        internal string First { get; } = first;
        internal string Main { get; } = main;
        internal string Side { get; } = side;
        internal string Merge { get; } = merge;
        internal static async Task<Fixture> CreateAsync(bool merge = true)
        {
            RootFixture root = await RootFixture.CreateAsync();
            try
            {
                string first = await root.Repository.ResolveRevisionAsync("HEAD"); await root.GitAsync("branch", "topic");
                await root.ChangeAsync("main\n", "Main selected"); string main = await root.Repository.ResolveRevisionAsync("HEAD");
                await root.GitAsync("config", "tag.gpgsign", "false"); await root.GitAsync("tag", "-a", "Tag/Été", "-m", "Annotated target", main);
                await root.GitAsync("checkout", "topic"); await File.WriteAllTextAsync(root.PathInRepository("topic.txt"), "side\n");
                await root.Repository.StageAllAsync(); await root.GitAsync("commit", "-m", "Side selected"); string side = await root.Repository.ResolveRevisionAsync("HEAD");
                await root.GitAsync("checkout", "main"); string mergeHash = main;
                if (merge)
                {
                    await root.GitAsync("merge", "--no-ff", "topic", "-m", "Merge selected"); mergeHash = await root.Repository.ResolveRevisionAsync("HEAD");
                    await root.ChangeAsync("newest\n", "Newest selected");
                }
                return new(root, first, main, side, mergeHash);
            }
            catch { root.Dispose(); throw; }
        }
        public void Dispose() => Root.Dispose();
    }
}

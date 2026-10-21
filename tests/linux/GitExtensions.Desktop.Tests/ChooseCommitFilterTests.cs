using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
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
public sealed class ChooseCommitFilterTests
{
    [AvaloniaTest]
    [TestCase("en", false, false)] [TestCase("en", false, true)]
    [TestCase("fr", true, false)] [TestCase("fr", true, true)]
    public async Task CancelingAdvancedDraftKeepsRowsSelectionMessageAndProfileAndFirstArrow(string language, bool dark, bool escape)
    {
        using Fixture f = await Fixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        string before = await Materials(f.Root); await using Lease s = await Lease.OpenAsync(f.Repository, new(f.Last), dark);
        ListBox rows = Rows(s); object source = rows.ItemsSource!; string[] hashes = Hashes(rows); int selected = rows.SelectedIndex;
        HistoryFilterDialog filter = await OpenFilter(s, keyboard: escape);
        Assert.That(filter.Owner, Is.SameAs(s.Dialog)); Assert.That(filter.ActualThemeVariant, Is.EqualTo(s.Dialog.ActualThemeVariant));
        decimal? limit = Field<NumericUpDown>(filter, "CommitsLimit").Value;
        SetTextFilter(filter, "Message", "Unaccepted message"); SetTextFilter(filter, "Author", "Unaccepted author");
        SetCheck(filter, "OnlyFirstParent", true);
        if (escape) { Press(filter, PhysicalKey.Escape); } else { Pointer(filter.GetVisualDescendants().OfType<Button>().Single(button => button.IsCancel)); }
        await FinishFilter(s, filter);
        Assert.That(rows.ItemsSource, Is.SameAs(source)); Assert.That(Hashes(rows), Is.EqualTo(hashes));
        Assert.That(rows.SelectedIndex, Is.EqualTo(selected)); Assert.That(Field<TextBox>(s.Dialog, "ChooseSearch").Text, Is.Null.Or.Empty);
        Assert.That(rows.IsKeyboardFocusWithin, Is.True); Assert.That(s.Selection.IsCompleted, Is.False);
        Assert.That(Field<TextBlock>(s.Dialog, "OperationStatus").IsEffectivelyVisible, Is.False);
        Press(s.Dialog, PhysicalKey.ArrowDown); Assert.That(rows.SelectedIndex, Is.EqualTo(selected + 1)); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
        filter = await OpenFilter(s, keyboard: !escape);
        Assert.That(Field<CheckBox>(filter, "MessageCheck").IsChecked, Is.False);
        Assert.That(Field<CheckBox>(filter, "AuthorCheck").IsChecked, Is.False);
        Assert.That(Field<CheckBox>(filter, "OnlyFirstParent").IsChecked, Is.False);
        Assert.That(Field<NumericUpDown>(filter, "CommitsLimit").Value, Is.EqualTo(limit));
        Press(filter, PhysicalKey.Escape); await FinishFilter(s, filter);
        Assert.That(await Materials(f.Root), Is.EqualTo(before), "Opening and canceling a private filter must not write the shared profile or repository.");
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task AcceptedMultifieldFilterUsesNativeRewrittenParentsAndReopensTheAcceptedDraft(string language, bool dark)
    {
        using Fixture f = await Fixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        string before = await Materials(f.Root); await using Lease s = await Lease.OpenAsync(f.Repository, new(f.Last), dark);
        HistoryFilterDialog filter = await OpenFilter(s);
        SetTextFilter(filter, "Message", "Keep"); SetTextFilter(filter, "Author", "Reference Tester");
        SetTextFilter(filter, "Committer", "Reference Tester"); SetTextFilter(filter, "PathFilter", "file.txt");
        SetCheck(filter, "IgnoreCase", true); Pointer(filter.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault));
        await FinishFilter(s, filter);
        ListBox rows = Rows(s); GitCommit[] shown = rows.Items.OfType<GitCommit>().ToArray();
        string native = (await f.Root.GitAsync("log", "--parents", "--format=%H %P", "--fixed-strings", "--regexp-ignore-case",
            "--author=Reference Tester", "--committer=Reference Tester", "--grep=Keep", "--all", "--", "file.txt")).StandardOutput;
        Dictionary<string, string[]> expected = NativeParents(native);
        Assert.That(expected.Keys, Is.EquivalentTo(new[] { f.Last, f.FirstKept }));
        Assert.That(expected[f.Last], Is.EqualTo(new[] { f.FirstKept }), "The real path-filtered log must skip the middle commit's unrelated file change.");
        Assert.That((await f.Repository.GetLogAsync(new GitLogQuery(Revision: f.Last, All: false, Count: 1))).Single().Parents,
            Is.EqualTo(new[] { f.Hidden }), "The fixture must distinguish raw parents from graph parents.");
        Assert.That(shown.Select(commit => commit.Hash), Is.EquivalentTo(expected.Keys));
        foreach (GitCommit commit in shown) { Assert.That(commit.Parents, Is.EqualTo(expected[commit.Hash]), commit.Hash); }
        Assert.That(((GitCommit)rows.SelectedItem!).Hash, Is.EqualTo(f.Last));
        Assert.That(Field<Button>(s.Dialog, "linkLabelParent").Tag, Is.EqualTo(f.FirstKept));
        Assert.That(Field<Button>(s.Dialog, "linkLabelParent2").IsVisible, Is.False);
        object acceptedSource = rows.ItemsSource!;
        Pointer(Field<Button>(s.Dialog, "linkLabelParent")); await s.Dialog.Completion;
        Assert.That(((GitCommit)rows.SelectedItem!).Hash, Is.EqualTo(f.FirstKept)); Assert.That(rows.ItemsSource, Is.SameAs(acceptedSource));
        Assert.That(s.Selection.IsCompleted, Is.False); Assert.That(rows.IsKeyboardFocusWithin, Is.True);
        filter = await OpenFilter(s);
        foreach ((string name, string value) in new[] { ("Message", "Keep"), ("Author", "Reference Tester"), ("Committer", "Reference Tester"), ("PathFilter", "file.txt") })
        {
            Assert.That(Field<CheckBox>(filter, name + "Check").IsChecked, Is.True, name);
            Assert.That(Field<TextBox>(filter, name).Text, Is.EqualTo(value), name);
        }
        Press(filter, PhysicalKey.Escape); await FinishFilter(s, filter);
        Assert.That(rows.ItemsSource, Is.SameAs(acceptedSource)); Assert.That(((GitCommit)rows.SelectedItem!).Hash, Is.EqualTo(f.FirstKept));
        Pointer(Field<Button>(s.Dialog, "ChooseAccept")); GitCommit result = (await s.Selection)!;
        Assert.That(result.Hash, Is.EqualTo(f.FirstKept)); Assert.That(result.Subject, Is.EqualTo("Keep alpha"));
        Assert.That(await Materials(f.Root), Is.EqualTo(before), "Accepted query state belongs to this chooser, not a global preference or repository write.");
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task CurrentBranchIsAnEditableInitialFilterWhileTheExclusiveLauncherBoundarySurvivesAcceptAndReset(string language, bool dark)
    {
        using Fixture f = await Fixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        string before = await Materials(f.Root); await using Lease s = await Lease.OpenAsync(f.Repository, new(f.Last, true, f.FirstKept), dark);
        string[] current = NativeHashes((await f.Root.GitAsync("log", "--format=%H", "HEAD", "..." + f.FirstKept, "--")).StandardOutput);
        Assert.That(Hashes(Rows(s)), Is.EquivalentTo(current)); Assert.That(current, Does.Not.Contain(f.Side));
        HistoryFilterDialog filter = await OpenFilter(s);
        Assert.That(Field<CheckBox>(filter, "CurrentBranchOnly").IsChecked, Is.True);
        SetCheck(filter, "CurrentBranchOnly", false); Pointer(filter.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault));
        await FinishFilter(s, filter);
        string[] allBounded = NativeHashes((await f.Root.GitAsync("log", "--format=%H", "--all", "..." + f.FirstKept, "--")).StandardOutput);
        Assert.That(allBounded, Does.Contain(f.Side).And.Not.Contain(f.FirstKept));
        Assert.That(Hashes(Rows(s)), Is.EquivalentTo(allBounded), "Changing the initial Current mode must be allowed without dropping the launcher's exclusive boundary.");
        filter = await OpenFilter(s); SetTextFilter(filter, "Message", "Keep gamma");
        Pointer(filter.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault)); await FinishFilter(s, filter);
        Assert.That(Hashes(Rows(s)), Is.EqualTo(new[] { f.Last }));
        filter = await OpenFilter(s);
        Assert.That(Field<CheckBox>(filter, "CurrentBranchOnly").IsChecked, Is.False);
        Assert.That(Field<TextBox>(filter, "Message").Text, Is.EqualTo("Keep gamma"));
        Pointer(Reset(filter)); await FinishFilter(s, filter);
        Assert.That(Hashes(Rows(s)), Is.EquivalentTo(allBounded));
        Assert.That(Hashes(Rows(s)), Does.Not.Contain(f.FirstKept).And.Not.Contain(f.RootCommit));
        Assert.That(Field<TextBox>(s.Dialog, "ChooseSearch").Text, Is.Null.Or.Empty);
        filter = await OpenFilter(s);
        Assert.That(Field<CheckBox>(filter, "CurrentBranchOnly").IsChecked, Is.False);
        Assert.That(Field<CheckBox>(filter, "MessageCheck").IsChecked, Is.False);
        Press(filter, PhysicalKey.Escape); await FinishFilter(s, filter);
        Assert.That(await Materials(f.Root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ClosingWithAnOpenAdvancedFilterDrainsTheChildAndPublishesNoSelection(bool ownerClose)
    {
        using Fixture f = await Fixture.CreateAsync(); string before = await Materials(f.Root);
        await using Lease s = await Lease.OpenAsync(f.Repository, new(f.Last), false);
        HistoryFilterDialog filter = await OpenFilter(s);
        Task work = s.Dialog.Completion;
        Assert.That(work.IsCompleted, Is.False); Assert.That(s.Dialog.IsBusy, Is.True);
        if (ownerClose) { s.Owner.Close(); } else { s.Dialog.Close(); }
        await Until(s.Owner, () => !filter.IsVisible && !s.Dialog.IsVisible && !s.Dialog.IsBusy && s.Selection.IsCompleted
            && (!ownerClose || !s.Owner.IsVisible));
        await work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(s.Dialog.Completion, Is.SameAs(work)); Assert.That(await s.Selection, Is.Null);
        Assert.That(s.Owner.IsVisible, Is.EqualTo(!ownerClose));
        Assert.That(s.Dialog.HasRepositoryChanges, Is.False);
        Assert.That(await Materials(f.Root), Is.EqualTo(before));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task SharedFilterEditorKeepsHostSortRefInclusionAndBoundaryOnOkAndResetButRestartsPaging(bool reset)
    {
        using Fixture f = await Fixture.CreateAsync(); string before = await Materials(f.Root);
        GitLogQuery original = new(Count: 23, Skip: 7, Author: "old author", Message: "old message", Path: "old.txt")
        { Sort = GitLogSort.AuthorDate, IncludeStashes = false, IncludeSessionRefs = false,
            IncludeLatestStashInBranchFilter = false, LastRevisionToDisplayHash = f.FirstKept };
        Window owner = new() { Width = 1000, Height = 800 }; owner.Show();
        HistoryFilterDialog dialog = new(original); Task<GitLogQuery?> response = dialog.ShowDialog<GitLogQuery?>(owner); Pump(owner);
        try
        {
            SetTextFilter(dialog, "Message", "edited message");
            if (reset) { Pointer(Reset(dialog)); } else { Pointer(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault)); }
            await Until(owner, () => response.IsCompleted); GitLogQuery accepted = (await response)!;
            Assert.That(accepted, Is.Not.Null); Assert.That(accepted.Skip, Is.Zero);
            Assert.That(accepted.Sort, Is.EqualTo(GitLogSort.AuthorDate));
            Assert.That(accepted.IncludeStashes || accepted.IncludeSessionRefs || accepted.IncludeLatestStashInBranchFilter, Is.False);
            Assert.That(accepted.LastRevisionToDisplayHash, Is.EqualTo(f.FirstKept)); Assert.That(accepted.Count, Is.EqualTo(23));
            Assert.That(accepted.Message, reset ? Is.Null : Is.EqualTo("edited message"));
            Assert.That(accepted.Author, reset ? Is.Null : Is.EqualTo("old author"));
            Assert.That(accepted.Path, reset ? Is.Null : Is.EqualTo("old.txt"));
            Assert.That(original.Skip, Is.EqualTo(7)); Assert.That(original.Message, Is.EqualTo("old message"));
            Assert.That(await Materials(f.Root), Is.EqualTo(before));
        }
        finally { dialog.Close(); owner.Close(); await response; }
    }

    private static async Task<HistoryFilterDialog> OpenFilter(Lease s, bool keyboard = false)
    {
        if (keyboard) { Press(s.Dialog, PhysicalKey.I, RawInputModifiers.Control); }
        else { Pointer(Field<Button>(s.Dialog, "ChooseAdvancedFilter")); }
        await Until(s.Owner, () => s.Dialog.OwnedWindows.OfType<HistoryFilterDialog>().Any(dialog => dialog.IsVisible));
        HistoryFilterDialog result = s.Dialog.OwnedWindows.OfType<HistoryFilterDialog>().Single(dialog => dialog.IsVisible);
        Pump(s.Owner); Assert.That(s.Selection.IsCompleted, Is.False); return result;
    }

    private static async Task FinishFilter(Lease s, HistoryFilterDialog filter)
    {
        await Until(s.Owner, () => !filter.IsVisible && !s.Dialog.IsBusy && s.Dialog.Completion.IsCompleted);
        await s.Dialog.Completion; Pump(s.Owner); Assert.That(s.Dialog.LastError, Is.Null, s.Dialog.LastError);
        Assert.That(s.Dialog.IsVisible, Is.True); Assert.That(s.Dialog.OwnedWindows.Any(window => window.IsVisible), Is.False);
    }

    private static void SetTextFilter(HistoryFilterDialog dialog, string name, string text)
    { SetCheck(dialog, name + "Check", true); Edit(dialog, Field<TextBox>(dialog, name), text); }
    private static void SetCheck(Window dialog, string name, bool check)
    {
        CheckBox control = Field<CheckBox>(dialog, name);
        if ((control.IsChecked == true) != check) { Pointer(control); Pump(dialog); }
        Assert.That(control.IsChecked, Is.EqualTo(check), name);
    }
    private static Button Reset(HistoryFilterDialog dialog) => dialog.GetVisualDescendants().OfType<Button>()
        .Single(button => button.Content as string == TranslationCatalog.LoadDefault().Translate("Reset"));
    private static ListBox Rows(Lease s) => Field<ListBox>(s.Dialog, "ChooseRows");
    private static string[] Hashes(ListBox rows) => rows.Items.OfType<GitCommit>().Select(commit => commit.Hash).ToArray();
    private static string[] NativeHashes(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    private static Dictionary<string, string[]> NativeParents(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToDictionary(fields => fields[0], fields => fields.Skip(1).ToArray(), StringComparer.Ordinal);
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Pointer(Control control) => PushProcessTestDriver.Pointer(control);
    private static void Edit(Window window, TextBox editor, string text)
    { Pointer(editor); Press(window, PhysicalKey.A, RawInputModifiers.Control); Press(window, PhysicalKey.Backspace); window.KeyTextInput(text); Pump(window); Assert.That(editor.Text, Is.EqualTo(text)); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Pump(Window root)
    {
        Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        foreach (Window child in root.OwnedWindows.ToArray()) { Pump(child); } Dispatcher.UIThread.RunJobs();
    }
    private static async Task Until(Window root, Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Pump(root); await Task.Delay(10); }
        Pump(root); Assert.That(condition(), Is.True, "Chooser/filter did not finish: " + string.Join("; ", root.OwnedWindows.Select(child => child.Name + " visible=" + child.IsVisible)));
    }
    private static async Task<string> Materials(RootFixture f)
    {
        string config = f.Output("config");
        return (await f.GitAsync("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput + "\n"
            + string.Join('\n', new[] { ".git/HEAD", ".git/index", ".git/config", "file.txt", "aux.txt", "side.txt" }
                .Select(path => path + ":" + (File.Exists(f.PathInRepository(path)) ? Convert.ToHexString(File.ReadAllBytes(f.PathInRepository(path))) : "<missing>")))
            + "\nprofile:" + (Directory.Exists(config) ? string.Join('\n', Directory.EnumerateFiles(config, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(path => path + ":" + Convert.ToHexString(File.ReadAllBytes(path)))) : "");
    }

    private sealed class TestChooser(GitRepository repository, ChooseCommitRequest request) : ChooseCommitDialog(repository, request,
        new("ChooseRows", "ChooseAccept", "ChooseCancel", "ChooseSearch", "ChooseSearchAction"), default)
    {
        internal Task<GitCommit?> OpenAsync(Window owner) => ShowPickerAsync(this, owner, default);
    }
    private sealed class Lease : IAsyncDisposable
    {
        internal Window Owner { get; }
        internal TestChooser Dialog { get; }
        internal Task<GitCommit?> Selection { get; }
        private Lease(GitRepository repository, ChooseCommitRequest request, bool dark)
        {
            Owner = new() { Width = 1200, Height = 800, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; Owner.Show();
            Dialog = new(repository, request); Selection = Dialog.OpenAsync(Owner); Pump(Owner);
        }
        internal static async Task<Lease> OpenAsync(GitRepository repository, ChooseCommitRequest request, bool dark)
        {
            Lease result = new(repository, request, dark);
            try { await Until(result.Owner, () => !result.Dialog.IsBusy && result.Dialog.Completion.IsCompleted); await result.Dialog.Completion; Pump(result.Owner); return result; }
            catch { await result.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            foreach (Window child in Dialog.OwnedWindows.ToArray()) { child.Close(); }
            Dialog.Close(); await Until(Owner, () => Selection.IsCompleted && !Dialog.IsBusy && !Dialog.IsVisible);
            await Dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10)); await Selection.WaitAsync(TimeSpan.FromSeconds(10)); Owner.Close(); Pump(Owner);
        }
    }

    private sealed class Fixture(RootFixture root, string rootCommit, string firstKept, string hidden, string last, string side) : IDisposable
    {
        internal RootFixture Root { get; } = root;
        internal GitRepository Repository => Root.Repository;
        internal string RootCommit { get; } = rootCommit;
        internal string FirstKept { get; } = firstKept;
        internal string Hidden { get; } = hidden;
        internal string Last { get; } = last;
        internal string Side { get; } = side;
        internal static async Task<Fixture> CreateAsync()
        {
            RootFixture root = await RootFixture.CreateAsync();
            try
            {
                root.SetEnvironment("GIT_OPTIONAL_LOCKS", "0"); string first = await root.Repository.ResolveRevisionAsync("HEAD");
                await root.GitAsync("branch", "side"); await root.ChangeAsync("kept alpha\n", "Keep alpha"); string a = await root.Repository.ResolveRevisionAsync("HEAD");
                await File.WriteAllTextAsync(root.PathInRepository("aux.txt"), "hidden\n"); await root.Repository.StageAllAsync(); await root.GitAsync("commit", "-m", "Hidden middle");
                string b = await root.Repository.ResolveRevisionAsync("HEAD"); await root.ChangeAsync("kept gamma\n", "Keep gamma"); string c = await root.Repository.ResolveRevisionAsync("HEAD");
                await root.GitAsync("checkout", "side"); await File.WriteAllTextAsync(root.PathInRepository("side.txt"), "side\n");
                await root.Repository.StageAllAsync(); await root.GitAsync("commit", "-m", "Keep side"); string side = await root.Repository.ResolveRevisionAsync("HEAD"); await root.GitAsync("checkout", "main");
                new PreferencesStore().Save(new DesktopPreferences());
                new HistoryViewPreferencesStore().Save(new HistoryViewPreferences { ShowStashes = false, ShowSessionCheckpoints = false });
                new FileHistoryPreferencesStore().Save(new FileHistoryPreferences());
                return new(root, first, a, b, c, side);
            }
            catch { root.Dispose(); throw; }
        }
        public void Dispose() => Root.Dispose();
    }
}

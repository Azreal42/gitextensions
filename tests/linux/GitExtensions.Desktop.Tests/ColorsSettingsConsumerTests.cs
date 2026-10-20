using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ColorsSettingsConsumerTests
{
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task RealApplyChangesTheExistingRowsGraphAndReferencesWithoutReloadingOrLosingCiAndSelection(bool dark)
    {
        using GitFixture git = await GitFixture.CreateAsync();
        await git.Repository.CreateBranchAsync("side"); await git.ChangeAsync("side content\n", "Side colors commit");
        await git.Repository.CheckoutAsync("main"); await git.ChangeAsync("main content\n", "Main colors commit");
        new PreferencesStore().Save(new() { Theme = dark ? "Dark" : "Light", Language = "English" });
        new HistoryViewPreferencesStore().Save(new());
        MainWindow main = new() { Width = 1440, Height = 900 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(git.Repository.Path); await Idle(main);
            ListBox history = Field<ListBox>(main, "RevisionHistory"); object source = history.ItemsSource!;
            GitCommit selected = history.ItemsSource!.OfType<GitCommit>().Single(commit => commit.Subject == "Main colors commit");
            GitCommit unrelated = history.ItemsSource!.OfType<GitCommit>().Single(commit => commit.Subject == "Side colors commit");
            history.SelectedItem = selected; history.Focus(); PortableHelperSettingsFixture.Pump(main);
            Control row = Row(history, selected), other = Row(history, unrelated);
            Border badge = row.GetVisualDescendants().OfType<Border>().First(control => control.Name == "RevisionReference");
            CommitGraph graph = main.GetVisualDescendants().OfType<CommitGraph>().Single();
            BuildServerRun run = new("synthetic", "colors", "Synthetic CI retained", BuildServerBuildStatus.Success,
                [selected.Hash], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);
            RevisionHistoryRow.ApplyProviderBuildStatus(row, new(selected.Hash, BuildServerBuildStatus.Success, [run]), true, true);
            Control ci = Field<StackPanel>(row, "RevisionBuildStatusCell");
            TextBlock ciText = Field<TextBlock>(row, "RevisionBuildStatus"); string ciTooltip = (string)ToolTip.GetTip(ci)!;
            DesktopSettingsDialog settings = await Settings(main);
            foreach (string control in new[] { "chkDrawAlternateBackColor", "DrawNonRelativesGray", "DrawNonRelativesTextGray", "chkHighlightAuthored", "chkFillRefLabels" })
            { PushProcessTestDriver.Pointer(Field<CheckBox>(settings, control)); }
            GitCommandLog.Clear(); await Apply(settings, () => new HistoryViewPreferencesStore().Load().FillRefLabels);
            Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected));
            Assert.That(Row(history, selected), Is.SameAs(row)); Assert.That(Row(history, unrelated), Is.SameAs(other));
            Assert.That(Field<StackPanel>(row, "RevisionBuildStatusCell"), Is.SameAs(ci)); Assert.That(ciText.Text, Is.EqualTo("Synthetic CI retained"));
            Assert.That(ToolTip.GetTip(ci), Is.EqualTo(ciTooltip));
            Assert.That(row.GetVisualDescendants().OfType<Border>().First(control => control.Name == "RevisionReference"), Is.SameAs(badge));
            Assert.That(badge.Background, Is.InstanceOf<LinearGradientBrush>());
            Assert.That(graph.DrawAlternateBackground, Is.False); Assert.That(graph.AuthoredEmail, Is.Null); Assert.That(graph.RelatedHashes, Is.Null);
            Assert.That(((ISolidColorBrush)Field<TextBlock>(other, "RevisionSubject").Foreground!).Color, Is.EqualTo(Colors.Gray), "Text dimming remains independently enabled.");
            Assert.That(((ISolidColorBrush)((Grid)other).Background!).Color, Is.EqualTo(Color.Parse(dark ? "#323232" : "#ffffff")));
            Assert.That(Field<TextBlock>(other, "RevisionAuthorName").FontWeight, Is.EqualTo(DesktopFonts.For(DesktopFontRole.Application).Weight));
            Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Command == "git log"), Is.False);
            PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await Idle(main);
            PushProcessTestDriver.Pointer(row); PortableHelperSettingsFixture.Press(main, PhysicalKey.ArrowDown);
            PortableHelperSettingsFixture.Press(main, PhysicalKey.ArrowUp);
            Assert.That(history.SelectedItem, Is.SameAs(selected)); Assert.That(history.IsKeyboardFocusWithin, Is.True);
            DesktopSettingsDialog reopened = await Settings(main);
            Assert.That(Field<ColorsSettingsPage>(reopened, "ColorsSettingsPage").Draft, Is.EqualTo(new ColorsSettingsValues
            { DrawAlternateBackground = false, DrawNonRelativesGray = false, DrawNonRelativesTextGray = true, HighlightAuthoredRevisions = false, FillRefLabels = true }));
            PushProcessTestDriver.Pointer(Field<CheckBox>(reopened, "chkFillRefLabels"));
            PushProcessTestDriver.Pointer(Field<Button>(reopened, "CancelDesktopSettings")); await Idle(main);
            Assert.That(new HistoryViewPreferencesStore().Load().FillRefLabels, Is.True); Assert.That(badge.Background, Is.InstanceOf<LinearGradientBrush>());
        }
        finally { await Close(main); }
        MainWindow restarted = new() { Width = 1440, Height = 900 }; restarted.Show();
        try
        {
            await restarted.OpenRepositoryAsync(git.Repository.Path); await Idle(restarted);
            CommitGraph graph = restarted.GetVisualDescendants().OfType<CommitGraph>().Single();
            Assert.That(graph.DrawAlternateBackground, Is.False); Assert.That(graph.AuthoredEmail, Is.Null);
            ListBox history = Field<ListBox>(restarted, "RevisionHistory");
            GitCommit selected = history.ItemsSource!.OfType<GitCommit>().Single(commit => commit.Subject == "Main colors commit");
            Assert.That(Row(history, selected).GetVisualDescendants().OfType<Border>().First(control => control.Name == "RevisionReference").Background,
                Is.InstanceOf<LinearGradientBrush>());
        }
        finally { await Close(restarted); }
    }

    [AvaloniaTest]
    public async Task TheActualViewMenuAndColorsShareOneActiveGraphGrayChoiceWithoutSavingItOnAnUnrelatedApply()
    {
        using GitFixture git = await GitFixture.CreateAsync(); new HistoryViewPreferencesStore().Save(new());
        MainWindow main = new() { Width = 1440, Height = 900 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(git.Repository.Path); await Idle(main);
            MenuItem view = Field<MenuItem>(main, "ViewMenu"); PushProcessTestDriver.Pointer(view); PortableHelperSettingsFixture.Pump(main);
            MenuItem gray = view.Items.OfType<MenuItem>().Single(item => item.Name == "ViewdrawNonrelativesGrayToolStripMenuItem");
            Assert.That(gray.IsChecked, Is.True); PushProcessTestDriver.Pointer(gray); await Idle(main); Assert.That(gray.IsChecked, Is.False);
            Assert.That(new HistoryViewPreferencesStore().Load().DrawNonRelativesGray, Is.True);
            DesktopSettingsDialog settings = await Settings(main);
            Assert.That(Field<CheckBox>(settings, "DrawNonRelativesGray").IsChecked, Is.False);
            PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "chkFillRefLabels")); await Apply(settings, () => new HistoryViewPreferencesStore().Load().FillRefLabels);
            Assert.That(gray.IsChecked, Is.False); Assert.That(new HistoryViewPreferencesStore().Load().DrawNonRelativesGray, Is.True);
            PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "DrawNonRelativesGray")); await Apply(settings, () => gray.IsChecked);
            Assert.That(gray.IsChecked, Is.True); Assert.That(new HistoryViewPreferencesStore().Load().DrawNonRelativesGray, Is.True);
            PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "DrawNonRelativesGray")); await Apply(settings, () => !gray.IsChecked);
            Assert.That(new HistoryViewPreferencesStore().Load().DrawNonRelativesGray, Is.False, "A real change to the same field publishes its default.");
            PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await Idle(main);
            ColorsSettingsSession peer = new(new HistoryViewPreferencesStore().Load());
            peer.Save(peer.Initial with { DrawAlternateBackground = false, DrawNonRelativesGray = true,
                DrawNonRelativesTextGray = true, HighlightAuthoredRevisions = false, FillRefLabels = false });
            PushProcessTestDriver.Pointer(view); PortableHelperSettingsFixture.Pump(main);
            PushProcessTestDriver.Pointer(view.Items.OfType<MenuItem>().Single(item => item.Name == "ViewSaveAsDefault")); await Idle(main);
            HistoryViewPreferences file = new HistoryViewPreferencesStore().Load();
            Assert.That(ColorsSettingsValues.From(file), Is.EqualTo(new ColorsSettingsValues
            { DrawAlternateBackground = false, DrawNonRelativesGray = true, DrawNonRelativesTextGray = true, HighlightAuthoredRevisions = false, FillRefLabels = false }));
            Assert.That(gray.IsChecked, Is.True, "Save View adopts the durable Colors receipt instead of publishing its stale graph snapshot.");
        }
        finally { await Close(main); }
    }

    [AvaloniaTest]
    public async Task TheRealSaveViewRejectsAnExplicitSharedGraphCollisionAndKeepsThePeerBytes()
    {
        using GitFixture git = await GitFixture.CreateAsync(); new HistoryViewPreferencesStore().Save(new() { DrawNonRelativesGray = false });
        MainWindow main = new() { Width = 1440, Height = 900 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(git.Repository.Path); await Idle(main);
            MenuItem view = Field<MenuItem>(main, "ViewMenu");
            MenuItem gray = view.Items.OfType<MenuItem>().Single(item => item.Name == "ViewdrawNonrelativesGrayToolStripMenuItem");
            for (int index = 0; index < 2; index++)
            { PushProcessTestDriver.Pointer(view); PortableHelperSettingsFixture.Pump(main); PushProcessTestDriver.Pointer(gray); await Idle(main); }
            Assert.That(gray.IsChecked, Is.False);
            ColorsSettingsSession peer = new(new HistoryViewPreferencesStore().Load()); peer.Save(peer.Initial with { DrawNonRelativesGray = true });
            byte[] saved = File.ReadAllBytes(new HistoryViewPreferencesStore().FilePath);
            PushProcessTestDriver.Pointer(view); PortableHelperSettingsFixture.Pump(main);
            PushProcessTestDriver.Pointer(view.Items.OfType<MenuItem>().Single(item => item.Name == "ViewSaveAsDefault")); await Idle(main);
            Assert.That(File.ReadAllBytes(new HistoryViewPreferencesStore().FilePath), Is.EqualTo(saved)); Assert.That(gray.IsChecked, Is.False);
            TextBlock state = (TextBlock)typeof(MainWindow).GetField("_state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
            Assert.That(state.Text, Does.Contain("changed").And.Contain("Reopen Settings"));
        }
        finally { await Close(main); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public void FilledReferencesChangeRenderedPixelsOnTheSameBadgeAndKeepCiControls(bool dark)
    {
        GitCommit commit = new("M", [], "Colors Author", "colors@example.invalid", DateTimeOffset.UnixEpoch, "Reference fill", "");
        GitReference[] references = [new("refs/heads/main", "M", null, true)];
        ListBox history = RevisionHistoryRowTests.History([commit], references);
        Window owner = new() { Width = 1000, Height = 140, Content = history, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; owner.Show();
        try
        {
            PortableHelperSettingsFixture.Pump(owner); Control row = Row(history, commit);
            RevisionHistoryRow.UpdateState(row, false, true, null, 0);
            Border badge = Field<Border>(row, "RevisionReference");
            TextBlock label = Field<TextBlock>(badge, "RevisionReferenceLabel");
            BuildServerRun run = new("1", "test", "Keep CI", BuildServerBuildStatus.Success, [commit.Hash], null, null, null);
            RevisionHistoryRow.ApplyProviderBuildStatus(row, new(commit.Hash, BuildServerBuildStatus.Success, [run]), true, true);
            Control ci = Field<StackPanel>(row, "RevisionBuildStatusCell");
            PortableHelperSettingsFixture.Pump(owner); byte[] before = Region(owner, badge);
            RevisionHistoryRow.ApplyPresentation(row, new(true, true, true, true, true, false, commit.AuthoredAt, "", "", false, references, true));
            PortableHelperSettingsFixture.Pump(owner);
            Assert.That(Field<Border>(row, "RevisionReference"), Is.SameAs(badge)); Assert.That(Field<TextBlock>(badge, "RevisionReferenceLabel"), Is.SameAs(label));
            Assert.That(Field<StackPanel>(row, "RevisionBuildStatusCell"), Is.SameAs(ci));
            Assert.That(badge.Background, Is.InstanceOf<LinearGradientBrush>()); Assert.That(Region(owner, badge), Is.Not.EqualTo(before));
            Color color = CommitGraph.ReferenceColor(references[0], dark); Assert.That(((ISolidColorBrush)label.Foreground!).Color, Is.EqualTo(color));
            PortableHelperSettingsFixture.Capture(owner, "colors-filled-refs-" + (dark ? "dark.png" : "light.png"));
            RevisionHistoryRow.ApplyPresentation(row, new(true, true, true, true, true, false, commit.AuthoredAt, "", "", false, references, false));
            PortableHelperSettingsFixture.Pump(owner); Assert.That(Field<Border>(row, "RevisionReference"), Is.SameAs(badge));
            Assert.That(Region(owner, badge), Is.EqualTo(before));
        }
        finally { owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public void SelectionStillHasPriorityOverAuthorAndAlternationAndTextGrayDoesNotDimCi(bool dark)
    {
        GitCommit commit = new("M", [], "Colors Author", "colors@example.invalid", DateTimeOffset.UnixEpoch, "Color precedence", "");
        Control row = RevisionHistoryRow.Create(commit, []);
        Window owner = new() { Width = 1000, Height = 140, Content = row, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; owner.Show();
        try
        {
            RevisionHistoryRow.UpdateState(row, false, true, commit.AuthorEmail, 0, true); PortableHelperSettingsFixture.Pump(owner);
            Assert.That(((ISolidColorBrush)((Grid)row).Background!).Color, Is.EqualTo(Color.Parse(dark ? "#393920" : "#eaf1ff")));
            RevisionHistoryRow.UpdateState(row, true, true, commit.AuthorEmail, 0, true); PortableHelperSettingsFixture.Pump(owner);
            Assert.That(((ISolidColorBrush)((Grid)row).Background!).Color, Is.EqualTo(Color.Parse(dark ? "#2864b4" : "#0078d7")));
            RevisionHistoryRow.UpdateState(row, false, true, null, 0, true); PortableHelperSettingsFixture.Pump(owner);
            Assert.That(((ISolidColorBrush)((Grid)row).Background!).Color, Is.EqualTo(Color.Parse(dark ? "#363636" : "#f9f9f9")));
            RevisionHistoryRow.UpdateState(row, false, true, null, 0, false); PortableHelperSettingsFixture.Pump(owner);
            Assert.That(((ISolidColorBrush)((Grid)row).Background!).Color, Is.EqualTo(Color.Parse(dark ? "#323232" : "#ffffff")));
            BuildServerRun run = new("1", "test", "CI success", BuildServerBuildStatus.Success, [commit.Hash], null, null, null);
            RevisionHistoryRow.ApplyProviderBuildStatus(row, new(commit.Hash, BuildServerBuildStatus.Success, [run]), true, true);
            Color ci = ((ISolidColorBrush)Field<TextBlock>(row, "RevisionBuildStatus").Foreground!).Color;
            RevisionHistoryRow.ApplyPresentation(row, new(true, true, true, true, true, false, commit.AuthoredAt, "", "", true, []));
            Assert.That(((ISolidColorBrush)Field<TextBlock>(row, "RevisionSubject").Foreground!).Color, Is.EqualTo(Colors.Gray));
            Assert.That(((ISolidColorBrush)Field<TextBlock>(row, "RevisionBuildStatus").Foreground!).Color, Is.EqualTo(ci));
        }
        finally { owner.Close(); }
    }

    private static byte[] Region(Window owner, Control control)
    {
        using WriteableBitmap frame = owner.CaptureRenderedFrame() ?? throw new AssertionException("No rendered frame.");
        using var buffer = frame.Lock(); byte[] bytes = new byte[buffer.RowBytes * buffer.Size.Height]; Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        Point point = control.TranslatePoint(default, owner)!.Value; PixelPoint first = new((int)Math.Ceiling(point.X + 2), (int)Math.Ceiling(point.Y + 2));
        int width = Math.Max(1, (int)Math.Floor(control.Bounds.Width - 4)), height = Math.Max(1, (int)Math.Floor(control.Bounds.Height - 4));
        List<byte> region = [];
        for (int y = first.Y; y < Math.Min(buffer.Size.Height, first.Y + height); y++)
        { int x = Math.Max(0, first.X), count = Math.Max(0, Math.Min(width, buffer.Size.Width - x)); region.AddRange(bytes.AsSpan(y * buffer.RowBytes + x * 4, count * 4).ToArray()); }
        Assert.That(region, Is.Not.Empty); return region.ToArray();
    }
    private static T Field<T>(Control owner, string name) where T : Control
        => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static Control Row(ListBox history, GitCommit commit)
        => ((Control)history.ContainerFromItem(commit)!).GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionHistoryRow");
    private static async Task<DesktopSettingsDialog> Settings(MainWindow main)
    {
        PortableHelperSettingsFixture.Press(main, PhysicalKey.Comma, Avalonia.Input.Raw.RawInputModifiers.Control);
        await PushProcessTestDriver.UntilAsync(main, () => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any(window => window.IsVisible));
        DesktopSettingsDialog settings = main.OwnedWindows.OfType<DesktopSettingsDialog>().Single(window => window.IsVisible);
        SettingsNavigationTestDriver.Select(settings, "Colors");
        if (settings.GitEnvironmentSession is { } environment) { await environment.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(20)); }
        await PushProcessTestDriver.UntilAsync(settings, () => Field<Button>(settings, "ApplyDesktopSettings").IsEffectivelyEnabled);
        return settings;
    }
    private static async Task Apply(DesktopSettingsDialog settings, Func<bool> accepted)
    {
        PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => settings.Navigation.IsEnabled && accepted() && Field<TextBlock>(settings, "SettingsNotice").Text is null or "");
    }
    private static Task Idle(MainWindow main)
        => PushProcessTestDriver.UntilAsync(main, () => !main.IsBusy && main.WindowWorkCompletion.IsCompleted && main.RepositoryBranches.Completion.IsCompleted);
    private static async Task Close(MainWindow main)
    {
        if (main.IsVisible) { main.Close(); }
        await PushProcessTestDriver.UntilAsync(main, () => !main.IsVisible && main.WindowWorkCompletion.IsCompleted && main.RepositoryBranches.Completion.IsCompleted);
        await main.WindowWorkCompletion; await main.RepositoryBranches.Completion;
    }
}

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
internal sealed class QuickGitRefSelectorDialogTests
{
    [AvaloniaTest]
    public void ReferencesAreGroupedLocalRemoteTagAndSortedByTheOriginalCulturalShortNameWithoutChangingIdentity()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                GitReference[] refs =
                [
                    Ref("refs/tags/zulu"), Ref("refs/remotes/origin/Zeta"), Ref("refs/heads/zeta"),
                    Ref("refs/tags/Alpha"), Ref("refs/heads/Bravo"), Ref("refs/remotes/origin/alpha"),
                    Ref("refs/heads/alpha"), Ref("refs/stash")
                ];
                QuickGitRefSelectorDialog dialog = Show(QuickGitRefAction.Select, refs, language);
                try
                {
                    string[] headers = language == "fr-FR" ? [Header("local"), Header("distant"), Header("étiquette")]
                        : [Header("local"), Header("remote"), Header("tag")];
                    Assert.That(Labels(dialog), Is.EqualTo(new[]
                    { headers[0], "alpha", "Bravo", "zeta", headers[1], "origin/alpha", "origin/Zeta", headers[2], "Alpha", "zulu" }));
                    Assert.That(List(dialog).SelectedIndex, Is.EqualTo(1)); Assert.That(dialog.SelectedReference, Is.SameAs(refs[6]));
                    Assert.That(List(dialog).IsKeyboardFocusWithin, Is.True); Assert.That(dialog.WasAccepted, Is.False);
                    Assert.That(refs.Select(reference => reference.Name), Is.EqualTo(new[]
                    { "refs/tags/zulu", "refs/remotes/origin/Zeta", "refs/heads/zeta", "refs/tags/Alpha", "refs/heads/Bravo", "refs/remotes/origin/alpha", "refs/heads/alpha", "refs/stash" }));
                }
                finally { dialog.Close(); }
            }
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [AvaloniaTest]
    [TestCase(QuickGitRefAction.Rename, "Rename")]
    [TestCase(QuickGitRefAction.Delete, "Delete")]
    [TestCase(QuickGitRefAction.Select, "Select")]
    public void TheOriginalActionButtonAcceptsTheReferenceChosenByOneRealRowClick(QuickGitRefAction action, string caption)
    {
        GitReference[] refs = [Ref("refs/heads/alpha"), Ref("refs/heads/beta")];
        QuickGitRefSelectorDialog dialog = Show(action, refs);
        try
        {
            Assert.That(Button(dialog).Content, Is.EqualTo(caption));
            PushProcessTestDriver.Pointer(Row(dialog, 2));
            Assert.That(dialog.IsVisible && !dialog.WasAccepted, Is.True); Assert.That(dialog.SelectedReference, Is.SameAs(refs[1]));
            PushProcessTestDriver.Pointer(Button(dialog));
            Assert.That(dialog.IsVisible, Is.False); Assert.That(dialog.WasAccepted, Is.True); Assert.That(dialog.SelectedReference, Is.SameAs(refs[1]));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public void KeyboardArrowsAndEnterUseTheSelectedReferenceAndTabOnlyMovesBetweenTheListAndAction()
    {
        GitReference[] refs = [Ref("refs/heads/alpha"), Ref("refs/heads/beta")];
        QuickGitRefSelectorDialog dialog = Show(QuickGitRefAction.Rename, refs);
        try
        {
            Press(dialog, PhysicalKey.ArrowDown); Assert.That(dialog.SelectedReference, Is.SameAs(refs[1]));
            Press(dialog, PhysicalKey.Tab); Assert.That(Button(dialog).IsKeyboardFocusWithin, Is.True);
            Assert.That(dialog.IsVisible && !dialog.WasAccepted, Is.True);
            Press(dialog, PhysicalKey.Tab, RawInputModifiers.Shift); Assert.That(List(dialog).IsKeyboardFocusWithin, Is.True);
            Press(dialog, PhysicalKey.Enter);
            Assert.That(dialog.IsVisible, Is.False); Assert.That(dialog.WasAccepted, Is.True); Assert.That(dialog.SelectedReference, Is.SameAs(refs[1]));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public void TwoActualPointerClicksOnTheSameReferenceValidateWithoutAnExtraButtonClick()
    {
        GitReference[] refs = [Ref("refs/heads/alpha"), Ref("refs/heads/β;$()'quoted")];
        QuickGitRefSelectorDialog dialog = Show(QuickGitRefAction.Select, refs);
        try
        {
            ListBoxItem row = Row(dialog, 2);
            PushProcessTestDriver.Pointer(row); Assert.That(dialog.IsVisible && !dialog.WasAccepted, Is.True);
            Point point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), dialog)!.Value;
            PointerMayClose(dialog, point, row);
            Assert.That(dialog.IsVisible, Is.False); Assert.That(dialog.WasAccepted, Is.True); Assert.That(dialog.SelectedReference, Is.SameAs(refs[1]));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public void TwoActualClicksOnEmptyListSpaceAcceptTheExistingSelectionAsInWindows()
    {
        GitReference[] refs = [Ref("refs/heads/alpha"), Ref("refs/heads/beta")];
        QuickGitRefSelectorDialog dialog = Show(QuickGitRefAction.Select, refs);
        try
        {
            Press(dialog, PhysicalKey.ArrowDown); Assert.That(dialog.SelectedReference, Is.SameAs(refs[1]));
            double itemHeight = Row(dialog, 1).Bounds.Height;
            // Give the existing list one empty row of viewport to exercise its native background, not a synthetic event.
            Field<Border>(dialog, "QuickReferencesFrame").Height += itemHeight; dialog.Height += itemHeight;
            for (int frame = 0; frame < 10; frame++) { Pump(dialog); }
            Rect last = Bounds(dialog, Row(dialog, 2)); Point point = new(last.Left + 12, last.Bottom + itemHeight / 2);
            Visual? hit = dialog.InputHitTest(point) as Visual;
            Assert.That(hit is Visual actual && actual.GetSelfAndVisualAncestors().Contains(List(dialog))
                && !actual.GetSelfAndVisualAncestors().OfType<ListBoxItem>().Any(), Is.True, "The real pointer must hit the empty list viewport.");
            dialog.MouseMove(point); dialog.MouseDown(point, MouseButton.Left); dialog.MouseUp(point, MouseButton.Left); Pump(dialog);
            Assert.That(dialog.IsVisible && !dialog.WasAccepted, Is.True); Assert.That(dialog.SelectedReference, Is.SameAs(refs[1]));
            PointerMayClose(dialog, point, List(dialog), requireEmptySpace: true);
            Assert.That(dialog.IsVisible, Is.False); Assert.That(dialog.WasAccepted, Is.True); Assert.That(dialog.SelectedReference, Is.SameAs(refs[1]));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    [TestCase("b", "bravo")]
    [TestCase("R", null)]
    public void TheFirstTypedLetterAndNativePrefixSearchNavigateTheActiveListWithoutFilteringOrAccepting(string firstKey, string? expected)
    {
        GitReference[] refs = [Ref("refs/heads/alpha"), Ref("refs/heads/bravo"), Ref("refs/remotes/origin/main"), Ref("refs/tags/v1")];
        QuickGitRefSelectorDialog dialog = Show(QuickGitRefAction.Select, refs);
        try
        {
            Assert.That(List(dialog).IsTextSearchEnabled && List(dialog).IsKeyboardFocusWithin, Is.True);
            dialog.KeyTextInput(firstKey); Pump(dialog);
            if (expected is null) { Assert.That(List(dialog).SelectedIndex, Is.EqualTo(3)); Assert.That(dialog.SelectedReference, Is.Null); }
            else
            {
                Assert.That(dialog.SelectedReference, Is.SameAs(refs[1]));
                dialog.KeyTextInput("r"); Pump(dialog); Assert.That(dialog.SelectedReference, Is.SameAs(refs[1]), "The consecutive prefix 'br' still selects bravo.");
            }
            Assert.That(Labels(dialog).Length, Is.EqualTo(7)); Assert.That(dialog.IsVisible && !dialog.WasAccepted, Is.True);
            Assert.That(List(dialog).IsKeyboardFocusWithin, Is.True); Assert.That(dialog.GetVisualDescendants().OfType<TextBox>(), Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    [TestCase(0)]
    [TestCase(3)]
    [TestCase(6)]
    public void GroupHeadersRemainSelectableAndEnterAcceptsWithNoReferenceAsInWindows(int headerIndex)
    {
        GitReference[] refs =
        [
            Ref("refs/heads/alpha"), Ref("refs/heads/beta"), Ref("refs/remotes/origin/alpha"),
            Ref("refs/remotes/origin/beta"), Ref("refs/tags/alpha"), Ref("refs/tags/beta")
        ];
        QuickGitRefSelectorDialog dialog = Show(QuickGitRefAction.Delete, refs);
        try
        {
            PushProcessTestDriver.Pointer(Row(dialog, headerIndex));
            Assert.That(List(dialog).SelectedIndex, Is.EqualTo(headerIndex)); Assert.That(dialog.SelectedReference, Is.Null);
            Assert.That(Button(dialog).IsEnabled && dialog.IsVisible && !dialog.WasAccepted, Is.True);
            Press(dialog, PhysicalKey.Enter); Assert.That(dialog.WasAccepted, Is.True);
            Assert.That(dialog.IsVisible, Is.False); Assert.That(dialog.SelectedReference, Is.Null);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    [TestCase("refs/remotes/origin/topic", "remote")]
    [TestCase("refs/tags/v1.2.3", "tag")]
    public void WhenEarlierGroupsAreAbsentTheFirstRealReferenceIsStillInitiallySelected(string name, string group)
    {
        GitReference reference = Ref(name); QuickGitRefSelectorDialog dialog = Show(QuickGitRefAction.Select, [reference]);
        try
        {
            Assert.That(Labels(dialog), Is.EqualTo(new[] { Header(group), reference.ShortName }));
            Assert.That(List(dialog).SelectedIndex, Is.EqualTo(1)); Assert.That(dialog.SelectedReference, Is.SameAs(reference));
            Assert.That(List(dialog).IsKeyboardFocusWithin, Is.True);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task EmptyOrUnsupportedOnlyListsCancelAModalWithoutAnExceptionOrAcceptedResult(bool unsupportedOnly)
    {
        Window owner = new() { Width = 400, Height = 250 }; owner.Show();
        QuickGitRefSelectorDialog dialog = new(QuickGitRefAction.Select, unsupportedOnly ? [Ref("refs/stash")] : [], Catalog("en"));
        try
        {
            Task modal = dialog.ShowDialog(owner); Pump(owner); await modal;
            Assert.That(dialog.IsVisible || dialog.WasAccepted, Is.False); Assert.That(dialog.SelectedReference, Is.Null);
            Assert.That(owner.IsEffectivelyEnabled, Is.True);
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task EscapeAndClosingTheModalOwnerCancelWithoutReplacingTheUsersSelection(bool closeOwner)
    {
        GitReference[] refs = [Ref("refs/heads/alpha"), Ref("refs/heads/beta")];
        Window owner = new() { Width = 400, Height = 250 }; owner.Show();
        QuickGitRefSelectorDialog dialog = new(QuickGitRefAction.Delete, refs, Catalog("en"));
        try
        {
            Task modal = dialog.ShowDialog(owner); Pump(dialog);
            Assert.That(modal.IsCompleted, Is.False); Assert.That(dialog.Owner, Is.SameAs(owner));
            Assert.That(owner.OwnedWindows, Does.Contain(dialog)); Assert.That(List(dialog).IsKeyboardFocusWithin, Is.True);
            Press(dialog, PhysicalKey.ArrowDown); Assert.That(dialog.SelectedReference, Is.SameAs(refs[1]));
            if (closeOwner) { owner.Close(); } else { Press(dialog, PhysicalKey.Escape); }
            await modal; Assert.That(dialog.IsVisible || dialog.WasAccepted, Is.False); Assert.That(dialog.SelectedReference, Is.SameAs(refs[1]));
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    public void ActivationAfterMappingDoesNotResetAChoiceOrFocusMadeAfterTheOpenedEvent()
    {
        GitReference[] refs = [Ref("refs/heads/alpha"), Ref("refs/heads/beta")];
        QuickGitRefSelectorDialog dialog = new(QuickGitRefAction.Rename, refs, Catalog("en"));
        dialog.Opened += (_, _) => { List(dialog).SelectedIndex = 2; Button(dialog).Focus(); };
        dialog.Show();
        try
        {
            Pump(dialog); Assert.That(List(dialog).SelectedIndex, Is.EqualTo(2)); Assert.That(dialog.SelectedReference, Is.SameAs(refs[1]));
            Assert.That(Button(dialog).IsKeyboardFocusWithin, Is.True, "The Loaded activation must not re-run initial logical focus.");
            Press(dialog, PhysicalKey.Enter); Assert.That(dialog.WasAccepted, Is.True); Assert.That(dialog.SelectedReference, Is.SameAs(refs[1]));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public void OriginalBorderlessCompactLayoutAndTranslatedActionsRenderWithoutAdditionalInputsInBothThemes()
    {
        foreach (string language in new[] { "en", "fr-FR" })
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        foreach (QuickGitRefAction action in Enum.GetValues<QuickGitRefAction>())
        {
            QuickGitRefSelectorDialog dialog = Show(action,
                [Ref("refs/heads/main"), Ref("refs/remotes/origin/main"), Ref("refs/tags/v1.2.3")], language, theme);
            try
            {
                Assert.That(dialog.SystemDecorations, Is.EqualTo(SystemDecorations.None));
                Assert.That(dialog.CanResize || dialog.CanMinimize || dialog.CanMaximize || dialog.ShowInTaskbar, Is.False);
                Assert.That(dialog.WindowStartupLocation, Is.EqualTo(WindowStartupLocation.Manual));
                Assert.That(dialog.GetVisualDescendants().OfType<TextBox>(), Is.Empty); Assert.That(dialog.GetVisualDescendants().OfType<Image>(), Is.Empty);
                Assert.That(dialog.GetVisualDescendants().OfType<Button>().Count(button => button.IsEffectivelyVisible), Is.EqualTo(1));
                Border frame = Field<Border>(dialog, "QuickReferencesFrame"); Grid footer = Field<Grid>(dialog, "flowLayoutPanel1");
                Assert.That(Bounds(dialog, frame).Left, Is.EqualTo(2)); Assert.That(Bounds(dialog, frame).Top, Is.EqualTo(2)); Assert.That(frame.Bounds.Width, Is.GreaterThanOrEqualTo(150));
                Assert.That(dialog.ClientSize.Width, Is.EqualTo(frame.Bounds.Width + 4));
                Assert.That(footer.Bounds.Height, Is.EqualTo(29)); Assert.That(dialog.ClientSize.Height, Is.EqualTo(frame.Bounds.Height + 31));
                Assert.That(Bounds(dialog, footer).Top, Is.EqualTo(Bounds(dialog, frame).Bottom));
                Assert.That(Bounds(dialog, Button(dialog)).Height, Is.EqualTo(23));
                Assert.That(Bounds(dialog, Button(dialog)).Right, Is.EqualTo(dialog.ClientSize.Width - 5));
                Assert.That(Button(dialog).IsDefault, Is.True); AssertInside(dialog, frame, footer, Button(dialog));
                Assert.That(List(dialog).IsKeyboardFocusWithin, Is.True); Assert.That(Row(dialog, 1).Bounds.Height, Is.InRange(12, 22));
                if (language == "fr-FR")
                {
                    Assert.That(Button(dialog).Content, Is.EqualTo(action switch
                    { QuickGitRefAction.Rename => "Renommer", QuickGitRefAction.Delete => "Supprimer", _ => "Sélectionner" }));
                    Assert.That(Labels(dialog).Where((_, index) => index % 2 == 0), Is.EqualTo(new[] { Header("local"), Header("distant"), Header("étiquette") }));
                }
                Capture(dialog, $"quick-reference-{action}-{language}-{theme}.png");
            }
            finally { dialog.Close(); }
        }
    }

    [AvaloniaTest]
    public void WidthMeasuresAtMostOneHundredCharactersWithoutTruncatingTheUnderlyingLiteralLabel()
    {
        List<double> widths = [];
        foreach (string name in new[] { new string('W', 100), new string('W', 180), new string('i', 100) })
        {
            GitReference reference = Ref("refs/heads/" + name); QuickGitRefSelectorDialog dialog = Show(QuickGitRefAction.Select, [reference]);
            try
            {
                widths.Add(dialog.ClientSize.Width); Assert.That(Labels(dialog)[1], Is.EqualTo(name));
                Assert.That(dialog.SelectedReference, Is.SameAs(reference));
                Assert.That(Row(dialog, 1).GetVisualDescendants().OfType<TextBlock>().Single().Text, Is.EqualTo(name));
                if (name.Length == 180) { Capture(dialog, "quick-reference-long-label.png"); }
            }
            finally { dialog.Close(); }
        }
        Assert.That(widths[1], Is.EqualTo(widths[0]), "Only the measured prefix is capped; the literal reference stays intact.");
        Assert.That(widths[0], Is.GreaterThan(widths[2]), "Width is measured from glyphs rather than a fixed character count.");
    }

    [AvaloniaTest]
    public void MoreThanEightRowsUseVerticalScrollingAndKeyboardEndKeepsTheFooterVisible()
    {
        GitReference[] refs = Enumerable.Range(0, 30).Select(index => Ref($"refs/heads/topic-{index:00}")).ToArray();
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            QuickGitRefSelectorDialog dialog = Show(QuickGitRefAction.Select, refs, theme: theme);
            try
            {
                Border frame = Field<Border>(dialog, "QuickReferencesFrame"); double itemHeight = Row(dialog, 1).Bounds.Height;
                Assert.That(frame.Bounds.Height, Is.EqualTo(itemHeight * 8 + 2)); Assert.That(Labels(dialog).Length, Is.EqualTo(31));
                ScrollViewer scroll = List(dialog).GetVisualDescendants().OfType<ScrollViewer>().Single();
                Assert.That(scroll.Extent.Height, Is.GreaterThan(scroll.Viewport.Height)); Assert.That(scroll.HorizontalScrollBarVisibility, Is.EqualTo(ScrollBarVisibility.Disabled));
                Press(dialog, PhysicalKey.End); Assert.That(dialog.SelectedReference, Is.SameAs(refs[^1]));
                Assert.That(scroll.Offset.Y, Is.GreaterThan(0)); AssertInside(dialog, frame, Button(dialog), Field<Grid>(dialog, "flowLayoutPanel1"));
                Capture(dialog, "quick-reference-scroll-" + theme + ".png");
                Press(dialog, PhysicalKey.ArrowUp); Assert.That(dialog.SelectedReference, Is.SameAs(refs[^2]));
                Assert.That(dialog.IsVisible && !dialog.WasAccepted, Is.True);
            }
            finally { dialog.Close(); }
        }
    }

    [AvaloniaTest]
    public async Task ActualGitReferenceSnapshotsCanBeSelectedWithoutChangingTheRepository()
    {
        using RootFixture fixture = await RootFixture.CreateAsync();
        await fixture.GitAsync("branch", "topic"); await fixture.GitAsync("tag", "v1");
        IReadOnlyList<GitReference> refs = await fixture.Repository.GetReferencesAsync();
        string before = (await fixture.GitAsync("show-ref")).StandardOutput;
        byte[] index = await File.ReadAllBytesAsync(fixture.PathInRepository(".git/index"));
        QuickGitRefSelectorDialog dialog = Show(QuickGitRefAction.Select, refs);
        try
        {
            GitReference expected = refs.Single(reference => reference.Name == "refs/heads/main");
            Assert.That(dialog.SelectedReference, Is.SameAs(expected)); Press(dialog, PhysicalKey.Enter);
            Assert.That(dialog.WasAccepted, Is.True); Assert.That(dialog.SelectedReference, Is.SameAs(expected));
            Assert.That((await fixture.GitAsync("show-ref")).StandardOutput, Is.EqualTo(before));
            Assert.That(await File.ReadAllBytesAsync(fixture.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That((await fixture.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
        }
        finally { dialog.Close(); }
    }

    private static GitReference Ref(string name) => new(name, new string('a', 40), null, false);
    private static string Header(string name) => (name + " ――――――――――――――――――")[..18];
    private static TranslationCatalog Catalog(string language) => TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
    private static QuickGitRefSelectorDialog Show(QuickGitRefAction action, IReadOnlyList<GitReference> refs, string language = "en", ThemeVariant? theme = null)
    {
        QuickGitRefSelectorDialog dialog = new(action, refs, Catalog(language)) { RequestedThemeVariant = theme ?? ThemeVariant.Light };
        dialog.Show(); Pump(dialog); return dialog;
    }
    private static T Field<T>(Window dialog, string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static ListBox List(QuickGitRefSelectorDialog dialog) => Field<ListBox>(dialog, "lbxRefs");
    private static Button Button(QuickGitRefSelectorDialog dialog) => Field<Button>(dialog, "btnAction");
    private static string[] Labels(QuickGitRefSelectorDialog dialog) => List(dialog).Items.Cast<object>().Select(item => item.ToString()!).ToArray();
    private static ListBoxItem Row(QuickGitRefSelectorDialog dialog, int index)
    { Pump(dialog); return (ListBoxItem)List(dialog).ContainerFromIndex(index)!; }
    private static void Press(Window dialog, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { dialog.KeyPressQwerty(key, modifiers); if (dialog.IsVisible) { dialog.KeyReleaseQwerty(key, modifiers); } Pump(dialog); }
    private static void PointerMayClose(Window dialog, Point point, Control target, bool requireEmptySpace = false)
    {
        for (int frame = 0; frame < 10; frame++) { Pump(dialog); }
        Visual? hit = dialog.InputHitTest(point) as Visual;
        Assert.That(target.IsEffectivelyVisible && target.IsEffectivelyEnabled && new Rect(default, dialog.ClientSize).Contains(point)
            && hit is Visual actual && actual.GetSelfAndVisualAncestors().Contains(target)
            && (!requireEmptySpace || !actual.GetSelfAndVisualAncestors().OfType<ListBoxItem>().Any()), Is.True,
            "Real closing pointer target: " + target.Name + " at " + point + "; hit=" + hit?.GetType().Name);
        dialog.MouseMove(point); dialog.MouseDown(point, MouseButton.Left);
        // DoubleTapped accepts on this press; the closed TopLevel no longer has a headless input backend.
        if (dialog.IsVisible) { dialog.MouseUp(point, MouseButton.Left); }
        Dispatcher.UIThread.RunJobs();
    }
    private static void Pump(TopLevel dialog)
    { Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static Rect Bounds(Window dialog, Control control) => new(control.TranslatePoint(default, dialog)!.Value, control.Bounds.Size);
    private static void AssertInside(Window dialog, params Control[] controls)
    {
        foreach (Control control in controls)
        {
            Rect rect = Bounds(dialog, control); Assert.That(control.IsEffectivelyVisible, Is.True, control.Name);
            Assert.That(rect.Left, Is.GreaterThanOrEqualTo(0), control.Name); Assert.That(rect.Top, Is.GreaterThanOrEqualTo(0), control.Name);
            Assert.That(rect.Right, Is.LessThanOrEqualTo(dialog.ClientSize.Width), control.Name); Assert.That(rect.Bottom, Is.LessThanOrEqualTo(dialog.ClientSize.Height), control.Name);
        }
    }
    private static void Capture(Window dialog, string name)
    {
        Pump(dialog); using var frame = dialog.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
}

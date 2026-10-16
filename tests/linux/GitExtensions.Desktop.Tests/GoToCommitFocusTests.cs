using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using RootFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GoToCommitFocusTests
{
    // This is the exact sequence asserted by the original FormGoToCommitTests.
    private static readonly string[] TabStops =
        ["textboxCommitExpression", "comboBoxTags", "comboBoxBranches", "goButton", "linkGitRevParse"];

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task NativeTabAndShiftTabVisitEveryOriginalStopAndWrapWithoutAccepting(bool backwards)
    {
        await using Fixture f = await Fixture.CreateAsync();
        foreach (string culture in new[] { "en", "fr-FR" })
        {
            GoToCommitDialog dialog = await f.ShowAsync(culture: culture);
            Assert.That(FocusedStop(dialog), Is.EqualTo(TabStops[0]));
            string[] expected = backwards ? TabStops.Skip(1).Reverse().Append(TabStops[0]).ToArray()
                : TabStops.Skip(1).Append(TabStops[0]).ToArray();
            List<string> actual = [];
            foreach (string stop in expected)
            {
                Press(dialog, PhysicalKey.Tab, backwards ? RawInputModifiers.Shift : RawInputModifiers.None);
                actual.Add(FocusedStop(dialog));
                Assert.That(dialog.IsVisible && !dialog.WasAccepted, Is.True, stop);
                Assert.That(Popup(dialog, Tags(dialog)).IsOpen || Popup(dialog, Branches(dialog)).IsOpen, Is.False);
            }

            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(f.OpenedLinks, Is.Empty, "Tabbing through the Help link must not launch a browser.");
            await CloseAsync(dialog);
        }
    }

    [AvaloniaTest]
    public async Task OriginalFieldsLocalListsAndHelpStayVisibleWhenResizedInBothThemesAndFrench()
    {
        await using Fixture f = await Fixture.CreateAsync();
        foreach (string culture in new[] { "en", "fr-FR" })
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            GoToCommitDialog dialog = await f.ShowAsync(culture, theme);
            TextBox expression = Expression(dialog); ComboBox tags = Tags(dialog), branches = Branches(dialog);
            Button go = Field<Button>(dialog, "goButton"), help = Field<Button>(dialog, "linkGitRevParse");
            Assert.That(dialog.ClientSize.Width, Is.EqualTo(604)); Assert.That(dialog.ClientSize.Height, Is.GreaterThanOrEqualTo(302));
            Assert.That(dialog.MinWidth, Is.EqualTo(604)); Assert.That(dialog.MinHeight, Is.EqualTo(302));
            Assert.That(dialog.CanResize, Is.True); Assert.That(dialog.CanMinimize || dialog.CanMaximize || dialog.ShowInTaskbar, Is.False);
            Assert.That(go.IsDefault, Is.True); Assert.That(tags.IsEditable && branches.IsEditable, Is.True);
            Assert.That(tags.SelectedIndex, Is.EqualTo(-1)); Assert.That(branches.SelectedIndex, Is.EqualTo(-1));
            Assert.That(tags.Items.Cast<string>(), Is.EqualTo(new[] { Fixture.FirstTag, Fixture.SecondTag }));
            Assert.That(branches.Items.Cast<string>(), Is.EqualTo(new[] { Fixture.FirstBranch, Fixture.SecondBranch, Fixture.LiteralBranch, "main" }));
            Assert.That(branches.Items.Cast<string>(), Does.Not.Contain("origin/OnlyRemote"));
            Assert.That(tags.Items.Cast<string>(), Does.Not.Contain("origin/OnlyRemote"));
            Assert.That(expression.Text ?? "", Is.Empty); Assert.That(tags.Text ?? "", Is.Empty); Assert.That(branches.Text ?? "", Is.Empty);
            string[] labels = dialog.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").ToArray();
            if (culture == "fr-FR")
            {
                Assert.That(dialog.Title, Is.EqualTo("Allez au commit")); Assert.That(go.Content, Is.EqualTo("Allez à"));
                Assert.That(labels, Does.Contain("Expression de commit :").And.Contain("Allez à l'étiquette :").And.Contain("Allez à la branche :"));
                Assert.That(((TextBlock)help.Content!).Text, Is.EqualTo("Plus, voir git-rev-parse"));
            }
            else
            {
                Assert.That(dialog.Title, Is.EqualTo("Go to commit")); Assert.That(go.Content, Is.EqualTo("Go"));
                Assert.That(labels, Does.Contain("Commit expression:").And.Contain("Go to tag:").And.Contain("Go to branch:"));
            }

            foreach (double delta in new[] { 0d, 200d })
            {
                dialog.Width = 604 + delta; Pump(dialog);
                Rect group = Bounds(dialog, Field<Grid>(dialog, "groupBox1"));
                Assert.That(group.X, Is.EqualTo(45)); Assert.That(group.Y, Is.EqualTo(43));
                Assert.That(group.Width, Is.EqualTo(470 + delta)); Assert.That(group.Height, Is.GreaterThanOrEqualTo(141));
                double translatedHelpGrowth = group.Height - 141;
                AssertBounds(dialog, expression, new Rect(155, 13, 360 + delta, 23));
                AssertBounds(dialog, tags, new Rect(155, 216 + translatedHelpGrowth, 287 + delta, 23));
                AssertBounds(dialog, branches, new Rect(155, 258 + translatedHelpGrowth, 287 + delta, 23));
                AssertBounds(dialog, go, new Rect(521 + delta, 10, 75, 28));
                AssertInside(dialog, expression, tags, branches, go, help);
                Rect examples = Bounds(dialog, Field<TextBlock>(dialog, "label2"));
                Assert.That(examples.Bottom, Is.LessThanOrEqualTo(Bounds(dialog, tags).Top), "The translated examples must remain above the tag field.");
                Assert.That(Bounds(dialog, help).Bottom, Is.LessThanOrEqualTo(Bounds(dialog, tags).Top));
                Capture(dialog, $"goto-commit-focus-{culture}-{theme}-{604 + delta}.png");
            }

            PushProcessTestDriver.Pointer(help);
            Assert.That(dialog.IsVisible && !dialog.WasAccepted, Is.True);
            Assert.That(f.OpenedLinks.Last().AbsoluteUri, Is.EqualTo("https://git-scm.com/docs/git-rev-parse#_specifying_revisions"));
            await CloseAsync(dialog);
        }
    }

    [AvaloniaTest]
    [TestCase("comboBoxTags")]
    [TestCase("comboBoxBranches")]
    public async Task SuggestAppendPreviewsCaseInsensitiveLocalNamesAndOnlyEnterCommitsTheChosenRow(string name)
    {
        await using Fixture f = await Fixture.CreateAsync();
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            GoToCommitDialog dialog = await f.ShowAsync(theme: theme); ComboBox choice = Field<ComboBox>(dialog, name);
            string[] original = choice.Items.Cast<string>().ToArray(); string prefix = Prefix(name), first = First(name), second = Second(name);
            Edit(dialog, Editor(choice), prefix); Popup popup = Popup(dialog, choice);
            await Until(() => popup.IsOpen && choice.Text == first);
            ListBox suggestions = Suggestions(popup); Pump(TopLevel.GetTopLevel(suggestions)!);
            Assert.That(suggestions.Items.Cast<string>(), Is.EqualTo(new[] { first, second }));
            Assert.That(Editor(choice).SelectedText, Is.EqualTo(first[prefix.Length..]));
            Assert.That(Editor(choice).IsKeyboardFocusWithin, Is.True); AssertUnaccepted(dialog);
            Capture(TopLevel.GetTopLevel(suggestions)!, $"goto-commit-focus-suggest-{name}-{theme}.png");
            Capture(dialog, $"goto-commit-focus-suffix-{name}-{theme}.png");
            Press(dialog, PhysicalKey.ArrowDown);
            Assert.That(choice.Text, Is.EqualTo(second)); Assert.That(Editor(choice).SelectedText, Is.EqualTo(second[prefix.Length..]));
            Assert.That(Editor(choice).IsKeyboardFocusWithin && popup.IsOpen, Is.True); AssertUnaccepted(dialog);
            Press(dialog, PhysicalKey.Enter); await AcceptedAsync(dialog, f.Last);
            Assert.That(choice.Text, Is.EqualTo(second)); Assert.That(choice.Items.Cast<string>(), Is.EqualTo(original));
        }
    }

    [AvaloniaTest]
    [TestCase("comboBoxTags", false, "comboBoxBranches")]
    [TestCase("comboBoxTags", true, "textboxCommitExpression")]
    [TestCase("comboBoxBranches", false, "goButton")]
    [TestCase("comboBoxBranches", true, "comboBoxTags")]
    public async Task LeavingTheSuggestionByTabOrShiftTabAcceptsTheTextAndMovesFocusWithoutAcceptingTheDialog(string name, bool backwards, string next)
    {
        await using Fixture f = await Fixture.CreateAsync(); GoToCommitDialog dialog = await f.ShowAsync(); ComboBox choice = Field<ComboBox>(dialog, name);
        Edit(dialog, Editor(choice), Prefix(name)); Popup popup = Popup(dialog, choice); await Until(() => popup.IsOpen);
        Press(dialog, PhysicalKey.ArrowDown); string acceptedText = Second(name);
        Assert.That(choice.Text, Is.EqualTo(acceptedText));
        Press(dialog, PhysicalKey.Tab, backwards ? RawInputModifiers.Shift : RawInputModifiers.None);
        Assert.That(popup.IsOpen, Is.False); Assert.That(choice.Text, Is.EqualTo(acceptedText)); Assert.That(FocusedStop(dialog), Is.EqualTo(next));
        Assert.That(Editor(choice).SelectedText, Is.Empty); AssertUnaccepted(dialog);
        Press(dialog, PhysicalKey.Tab, backwards ? RawInputModifiers.None : RawInputModifiers.Shift);
        Assert.That(FocusedStop(dialog), Is.EqualTo(name)); AssertUnaccepted(dialog);
        Assert.That(popup.IsOpen, Is.False, "Returning to the field cannot commit or reopen an old completion.");
    }

    [AvaloniaTest]
    [TestCase("comboBoxTags")]
    [TestCase("comboBoxBranches")]
    public async Task EscapeRestoresTheTypedPrefixKeepsEditorFocusAndOnlyTheNextEscapeCloses(string name)
    {
        await using Fixture f = await Fixture.CreateAsync(); GoToCommitDialog dialog = await f.ShowAsync(); ComboBox choice = Field<ComboBox>(dialog, name);
        string prefix = Prefix(name); Edit(dialog, Editor(choice), prefix); Popup popup = Popup(dialog, choice); await Until(() => popup.IsOpen);
        Press(dialog, PhysicalKey.ArrowDown); Press(dialog, PhysicalKey.Escape);
        Assert.That(popup.IsOpen, Is.False); Assert.That(choice.Text, Is.EqualTo(prefix)); Assert.That(Editor(choice).SelectedText, Is.Empty);
        Assert.That(Editor(choice).CaretIndex, Is.EqualTo(prefix.Length)); Assert.That(Editor(choice).IsKeyboardFocusWithin, Is.True); AssertUnaccepted(dialog);
        Press(dialog, PhysicalKey.Escape); await dialog.Completion;
        Assert.That(dialog.IsVisible || popup.IsOpen || dialog.WasAccepted, Is.False); Assert.That(dialog.SelectedCommitHash, Is.Null);
    }

    [AvaloniaTest]
    [TestCase("comboBoxTags")]
    [TestCase("comboBoxBranches")]
    public async Task ARealArrowClickWithSelectedEditorTextThenARealRowClickCommitsTheExactLiteralName(string name)
    {
        await using Fixture f = await Fixture.CreateAsync();
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        foreach (double width in new[] { 604d, 1000d })
        {
            GoToCommitDialog dialog = await f.ShowAsync(theme: theme); ComboBox choice = Field<ComboBox>(dialog, name);
            dialog.Width = width; Pump(dialog);
            const string draft = "Unmatched/literal;$()'λ";
            Edit(dialog, Editor(choice), draft); Press(dialog, PhysicalKey.A, RawInputModifiers.Control);
            Assert.That(Editor(choice).SelectedText, Is.EqualTo(draft));
            OpenArrow(dialog, choice);
            Popup popup = Popup(dialog, choice); await Until(() => popup.IsOpen);
            Assert.That(popup.Width, Is.EqualTo(choice.Bounds.Width), "The dropdown must follow the complete editable combo width.");
            if (width == 1000) { Assert.That(choice.Bounds.Width, Is.EqualTo(683)); Assert.That(popup.Width, Is.GreaterThan(600)); }
            AssertUnaccepted(dialog); string selected = name == "comboBoxTags" ? Second(name) : Fixture.LiteralBranch;
            ListBox list = Suggestions(popup); Pump(TopLevel.GetTopLevel(list)!);
            Assert.That(popup.Child!.Bounds.Width, Is.EqualTo(choice.Bounds.Width), "The rendered popup border must also escape the old 600px maximum.");
            int index = list.Items.Cast<string>().ToList().IndexOf(selected); Assert.That(index, Is.GreaterThanOrEqualTo(0));
            ListBoxItem row = (ListBoxItem)list.ContainerFromIndex(index)!;
            Assert.That(row, Is.Not.Null); Capture(TopLevel.GetTopLevel(list)!, $"goto-commit-focus-arrow-{name}-{theme}-{width}.png");
            PushProcessTestDriver.Pointer(row); await AcceptedAsync(dialog, f.Last);
            Assert.That(choice.Text, Is.EqualTo(selected)); Assert.That(popup.IsOpen, Is.False);
        }
    }

    [AvaloniaTest]
    [TestCase("comboBoxTags")]
    [TestCase("comboBoxBranches")]
    public async Task ArrowOpeningAndKeyboardPreviewNeverCommitUntilTheRealEnterKey(string name)
    {
        await using Fixture f = await Fixture.CreateAsync(); GoToCommitDialog dialog = await f.ShowAsync(); ComboBox choice = Field<ComboBox>(dialog, name);
        Edit(dialog, Editor(choice), "unmatched"); Press(dialog, PhysicalKey.A, RawInputModifiers.Control);
        OpenArrow(dialog, choice);
        Popup popup = Popup(dialog, choice); await Until(() => popup.IsOpen); AssertUnaccepted(dialog);
        Press(dialog, PhysicalKey.ArrowDown);
        Assert.That(choice.Text, Is.EqualTo(First(name))); Assert.That(Editor(choice).IsKeyboardFocusWithin, Is.True); AssertUnaccepted(dialog);
        Press(dialog, PhysicalKey.Enter); await AcceptedAsync(dialog, f.First);
    }

    [AvaloniaTest]
    public async Task ProgrammaticSelectionAndTextNeverCommitAndFocusChangesDismissOnlyTheCurrentPopup()
    {
        await using Fixture f = await Fixture.CreateAsync(); GoToCommitDialog dialog = await f.ShowAsync();
        foreach (ComboBox choice in new[] { Tags(dialog), Branches(dialog) })
        {
            choice.SelectedItem = Second(choice.Name!); choice.Text = First(choice.Name!); Pump(dialog);
            AssertUnaccepted(dialog); Assert.That(Popup(dialog, choice).IsOpen, Is.False); Assert.That(Expression(dialog).IsKeyboardFocusWithin, Is.True);
            Edit(dialog, Editor(choice), Prefix(choice.Name!)); Popup popup = Popup(dialog, choice); await Until(() => popup.IsOpen);
            DismissAtExpression(dialog, popup); await Until(() => !popup.IsOpen);
            Assert.That(Expression(dialog).IsKeyboardFocusWithin, Is.True); AssertUnaccepted(dialog);
            Edit(dialog, Editor(choice), Prefix(choice.Name!)); await Until(() => popup.IsOpen);
            choice.Text = "Programmatic/λ;$()'literal"; Pump(dialog);
            Assert.That(popup.IsOpen, Is.False); Assert.That(choice.Text, Is.EqualTo("Programmatic/λ;$()'literal")); AssertUnaccepted(dialog);
            PushProcessTestDriver.Pointer(Expression(dialog)); Assert.That(Expression(dialog).IsKeyboardFocusWithin, Is.True);
        }

        ComboBox tags = Tags(dialog); Edit(dialog, Editor(tags), Prefix(tags.Name!)); Popup oldPopup = Popup(dialog, tags); await Until(() => oldPopup.IsOpen);
        await CloseAsync(dialog); Assert.That(oldPopup.IsOpen, Is.False);
        GoToCommitDialog next = await f.ShowAsync(); Assert.That(Expression(next).IsKeyboardFocusWithin, Is.True);
        Assert.That(Popup(next, Tags(next)).IsOpen, Is.False); Assert.That(oldPopup.IsOpen, Is.False); AssertUnaccepted(next);
    }

    private static string Prefix(string name) => name == "comboBoxTags" ? "ta" : "to";
    private static string First(string name) => name == "comboBoxTags" ? Fixture.FirstTag : Fixture.FirstBranch;
    private static string Second(string name) => name == "comboBoxTags" ? Fixture.SecondTag : Fixture.SecondBranch;
    private static TextBox Expression(GoToCommitDialog dialog) => Field<TextBox>(dialog, "textboxCommitExpression");
    private static ComboBox Tags(GoToCommitDialog dialog) => Field<ComboBox>(dialog, "comboBoxTags");
    private static ComboBox Branches(GoToCommitDialog dialog) => Field<ComboBox>(dialog, "comboBoxBranches");
    private static TextBox Editor(ComboBox choice) => choice.GetVisualDescendants().OfType<TextBox>().Single();
    private static T Field<T>(TopLevel root, string name) where T : Control => root.GetVisualDescendants().OfType<Control>()
        .Concat(root.GetLogicalDescendants().OfType<Control>()).Distinct().OfType<T>().Single(control => control.Name == name);
    private static Popup Popup(GoToCommitDialog dialog, ComboBox choice) => Field<Popup>(dialog, choice.Name + "HistoryCompletionPopup");
    private static ListBox Suggestions(Popup popup) => popup.Child!.GetVisualDescendants().OfType<ListBox>().Single();
    private static string FocusedStop(GoToCommitDialog dialog) => (dialog.FocusManager?.GetFocusedElement() as Control)?
        .GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(control => TabStops.Contains(control.Name, StringComparer.Ordinal))?.Name ?? "<none>";
    private static void AssertUnaccepted(GoToCommitDialog dialog)
    { Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.WasAccepted || dialog.IsValidating, Is.False); Assert.That(dialog.SelectedCommitHash, Is.Null); }
    private static async Task AcceptedAsync(GoToCommitDialog dialog, string expected)
    {
        await Until(() => !dialog.IsVisible); await dialog.Completion;
        Assert.That(dialog.WasAccepted, Is.True); Assert.That(dialog.ResolutionError, Is.Null); Assert.That(dialog.SelectedCommitHash, Is.EqualTo(expected));
    }
    private static async Task CloseAsync(GoToCommitDialog dialog) { dialog.Close(); await dialog.Completion; Pump(dialog); }
    private static void Edit(Window window, TextBox editor, string value)
    { PushProcessTestDriver.Pointer(editor); Press(window, PhysicalKey.A, RawInputModifiers.Control); window.KeyTextInput(value); Pump(window); }
    private static void OpenArrow(GoToCommitDialog dialog, ComboBox choice)
    {
        for (int frame = 0; frame < 10; frame++) { Pump(dialog); }
        Point point = choice.TranslatePoint(new Point(choice.Bounds.Width - 8, choice.Bounds.Height / 2), dialog)!.Value;
        Visual? hit = dialog.InputHitTest(point) as Visual;
        Assert.That(choice.IsEffectivelyVisible && choice.IsEffectivelyEnabled && new Rect(default, dialog.ClientSize).Contains(point)
            && hit is Visual arrowHit && arrowHit.GetSelfAndVisualAncestors().Contains(choice)
            && !arrowHit.GetSelfAndVisualAncestors().Contains(Editor(choice)), Is.True, "The real right-hand arrow must be hit-testable outside the editable field.");
        dialog.MouseMove(point); dialog.MouseDown(point, MouseButton.Left); dialog.MouseUp(point, MouseButton.Left); Pump(dialog);
    }
    private static void DismissAtExpression(GoToCommitDialog dialog, Popup popup)
    {
        for (int frame = 0; frame < 10; frame++) { Pump(dialog); }
        TextBox expression = Expression(dialog);
        Point point = expression.TranslatePoint(new Point(expression.Bounds.Width / 2, expression.Bounds.Height / 2), dialog)!.Value;
        Visual? hit = dialog.InputHitTest(point) as Visual;
        Assert.That(popup.IsOpen && expression.IsEffectivelyVisible && expression.IsEffectivelyEnabled
            && new Rect(default, dialog.ClientSize).Contains(point), Is.True);
        Assert.That(hit?.GetType().Name, Is.EqualTo("LightDismissOverlayLayer"), "The first native click belongs to the popup's dismissal layer.");
        dialog.MouseMove(point); dialog.MouseDown(point, MouseButton.Left); dialog.MouseUp(point, MouseButton.Left); Pump(dialog);
        Assert.That(popup.IsOpen, Is.False); Assert.That(expression.IsKeyboardFocusWithin, Is.True, "One real click must dismiss the popup and pass through to the expression.");
    }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "The real Go to commit input did not settle.");
    }
    private static Rect Bounds(Window window, Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void AssertBounds(Window window, Control control, Rect expected)
    {
        Rect actual = Bounds(window, control);
        Assert.That(actual.X, Is.EqualTo(expected.X).Within(1), control.Name); Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(1), control.Name);
        Assert.That(actual.Width, Is.EqualTo(expected.Width).Within(1), control.Name); Assert.That(actual.Height, Is.EqualTo(expected.Height).Within(1), control.Name);
    }
    private static void AssertInside(Window window, params Control[] controls)
    {
        foreach (Control control in controls)
        {
            Rect bounds = Bounds(window, control); Assert.That(control.IsEffectivelyVisible, Is.True, control.Name);
            Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(0), control.Name); Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0), control.Name);
            Assert.That(bounds.Right, Is.LessThanOrEqualTo(window.ClientSize.Width), control.Name); Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(window.ClientSize.Height), control.Name);
        }
    }
    private static void Capture(TopLevel window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }

    private sealed class Fixture(RootFixture root, string first, string last) : IAsyncDisposable
    {
        internal const string FirstTag = "Tag/Alpha";
        internal const string SecondTag = "Tag/Alpine";
        internal const string FirstBranch = "Topic/Alpha";
        internal const string SecondBranch = "Topic/Alpine";
        internal const string LiteralBranch = "ZLiteral/λ;$()'quoted";
        private readonly List<GoToCommitDialog> _windows = [];
        internal List<Uri> OpenedLinks { get; } = [];
        internal string First { get; } = first;
        internal string Last { get; } = last;
        internal static async Task<Fixture> CreateAsync()
        {
            RootFixture root = await RootFixture.CreateAsync();
            try
            {
                string first = await root.Repository.ResolveRevisionAsync("HEAD");
                await root.ChangeAsync("second\n", "Second focus fixture commit"); string last = await root.Repository.ResolveRevisionAsync("HEAD");
                await root.GitAsync("tag", FirstTag, first); await root.GitAsync("tag", SecondTag, last);
                await root.GitAsync("branch", FirstBranch, first); await root.GitAsync("branch", SecondBranch, last);
                await root.GitAsync("branch", LiteralBranch, last); await root.GitAsync("update-ref", "refs/remotes/origin/OnlyRemote", first);
                return new(root, first, last);
            }
            catch { root.Dispose(); throw; }
        }
        internal async Task<GoToCommitDialog> ShowAsync(string culture = "en", ThemeVariant? theme = null)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            GoToCommitDialog dialog = new(root.Repository, OpenedLinks.Add, _ => Task.FromResult<string?>(null))
                { RequestedThemeVariant = theme ?? ThemeVariant.Light };
            _windows.Add(dialog); dialog.Show(); await dialog.InitializeAsync(); Pump(dialog); return dialog;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (GoToCommitDialog dialog in _windows) { if (dialog.IsVisible) { dialog.Close(); } await dialog.Completion; }
            root.Dispose();
        }
    }
}

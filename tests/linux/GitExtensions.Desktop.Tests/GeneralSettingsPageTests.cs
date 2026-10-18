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
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GeneralSettingsPageTests
{
    [AvaloniaTest]
    public void DisablingBothStatusCountersClearsSubmoduleStatusAndDoesNotRestoreItLater()
    {
        GeneralSettingsValues initial = new() { ShowSubmoduleStatus = true };
        using Scope f = new(initial);
        Assert.That(f.Check("chkShowSubmoduleStatusInBrowse").IsChecked, Is.True);
        f.Click("chkShowGitStatusInToolbar");
        Assert.That(f.Check("chkShowSubmoduleStatusInBrowse").IsEffectivelyEnabled, Is.True,
            "The artificial-commit counter still needs submodule status.");
        Assert.That(f.Page.Draft.ShowSubmoduleStatus, Is.True);
        f.Click("chkShowGitStatusForArtificialCommits");
        Assert.That(f.Check("chkShowSubmoduleStatusInBrowse").IsEffectivelyEnabled, Is.False);
        Assert.That(f.Page.Draft.ShowSubmoduleStatus, Is.False);
        f.Click("chkShowGitStatusInToolbar");
        Assert.That(f.Check("chkShowSubmoduleStatusInBrowse").IsEffectivelyEnabled, Is.True);
        Assert.That(f.Page.Draft.ShowSubmoduleStatus, Is.False,
            "Windows clears the dependent choice when its last consumer is switched off.");
        f.Click("chkShowSubmoduleStatusInBrowse");
        Assert.That(f.Page.Draft.ShowSubmoduleStatus, Is.True);
        Assert.That(initial, Is.EqualTo(new GeneralSettingsValues { ShowSubmoduleStatus = true }),
            "The page owns a draft rather than mutating the input object.");
    }

    [AvaloniaTest]
    public void AnInconsistentInitialSubmoduleChoiceIsClearedWhenNeitherCounterConsumesIt()
    {
        using Scope f = new(new() { ShowGitStatusInBrowseToolbar = false, ShowGitStatusForArtificialCommits = false, ShowSubmoduleStatus = true });
        Assert.That(f.Check("chkShowSubmoduleStatusInBrowse").IsEffectivelyEnabled, Is.False);
        Assert.That(f.Check("chkShowSubmoduleStatusInBrowse").IsChecked, Is.False);
        Assert.That(f.Page.Draft.ShowSubmoduleStatus, Is.False);
    }

    [AvaloniaTest]
    public void FollowRenamesAndExactRenamesAreIndependentChoices()
    {
        using Scope f = new(new() { FollowRenamesInFileHistory = false, FollowRenamesInFileHistoryExact = true });
        Assert.That(f.Check("chkFollowRenamesInFileHistoryExact").IsEffectivelyEnabled, Is.True);
        f.Click("chkFollowRenamesInFileHistoryExact");
        Assert.That(f.Page.Draft.FollowRenamesInFileHistory, Is.False);
        Assert.That(f.Page.Draft.FollowRenamesInFileHistoryExact, Is.False);
        f.Click("chkFollowRenamesInFileHistory"); f.Click("chkFollowRenamesInFileHistoryExact");
        f.Click("chkFollowRenamesInFileHistory");
        Assert.That(f.Page.Draft.FollowRenamesInFileHistory, Is.False);
        Assert.That(f.Page.Draft.FollowRenamesInFileHistoryExact, Is.True);
    }

    [AvaloniaTest]
    [TestCase(null)]
    [TestCase(false)]
    [TestCase(true)]
    public void SubmoduleCheckoutCanCycleThroughAskNoAndYesWithoutLosingItsThirdState(bool? initial)
    {
        using Scope f = new(new() { UpdateSubmodulesOnCheckout = initial });
        CheckBox check = f.Check("chkUpdateModules");
        Assert.That(check.IsThreeState, Is.True); Assert.That(check.IsChecked, Is.EqualTo(initial));
        HashSet<bool?> choices = [initial];
        for (int click = 0; click < 3; click++)
        {
            f.Click(check); choices.Add(check.IsChecked);
            Assert.That(f.Page.Draft.UpdateSubmodulesOnCheckout, Is.EqualTo(check.IsChecked));
        }
        Assert.That(choices, Is.EquivalentTo(new bool?[] { null, false, true }));
        Assert.That(f.Page.Draft.UpdateSubmodulesOnCheckout, Is.EqualTo(initial));
    }

    [AvaloniaTest]
    public void UnlimitedHistoryDisablesItsNumberAndReenablingRetainsTheEditedLimit()
    {
        using Scope f = new(new());
        NumericUpDown limit = f.Field<NumericUpDown>("_NO_TRANSLATE_MaxCommits");
        f.Number(limit, "234567");
        Assert.That(f.Page.Draft.MaxRevisionGraphCommits, Is.EqualTo(234567));
        f.Click("lblCommitsLimit");
        Assert.That(limit.IsEffectivelyEnabled, Is.False);
        Assert.That(f.Page.Draft.MaxRevisionGraphCommits, Is.Zero);
        f.Click("lblCommitsLimit");
        Assert.That(limit.IsEffectivelyEnabled, Is.True);
        Assert.That(f.Page.Draft.MaxRevisionGraphCommits, Is.EqualTo(234567));
    }

    [AvaloniaTest]
    public void AnUnlimitedInitialHistoryRemainsUnlimitedUntilTheUserEnablesTheLimit()
    {
        using Scope f = new(new() { MaxRevisionGraphCommits = 0 });
        NumericUpDown limit = f.Field<NumericUpDown>("_NO_TRANSLATE_MaxCommits");
        Assert.That(limit.IsEffectivelyEnabled, Is.False); Assert.That(f.Page.Draft.MaxRevisionGraphCommits, Is.Zero);
        f.Click("lblCommitsLimit"); f.Number(limit, "1000000");
        Assert.That(f.Page.Draft.MaxRevisionGraphCommits, Is.EqualTo(1_000_000));
        Assert.That(f.Page.Validate(out string? error), Is.True, error);
    }

    [AvaloniaTest]
    public void NumericEditorsAcceptTheWindowsRangeAndQuickSearchStepThroughActualKeyboardInput()
    {
        using Scope f = new(new());
        NumericUpDown timeout = f.Field<NumericUpDown>("RevisionGridQuickSearchTimeout");
        Assert.That(timeout.Minimum, Is.EqualTo(100)); Assert.That(timeout.Maximum, Is.EqualTo(1_000_000));
        f.Number(timeout, "100");
        Assert.That(f.Page.Draft.RevisionGridQuickSearchTimeout, Is.EqualTo(100));
        f.Show(timeout); TextBox editor = timeout.GetVisualDescendants().OfType<TextBox>().Single();
        PushProcessTestDriver.Pointer(editor); Press(f.Window, PhysicalKey.ArrowUp);
        Assert.That(f.Page.Draft.RevisionGridQuickSearchTimeout, Is.EqualTo(200));
        Assert.That(editor.IsFocused, Is.True);
        Press(f.Window, PhysicalKey.ArrowDown);
        Assert.That(f.Page.Draft.RevisionGridQuickSearchTimeout, Is.EqualTo(100));
        Press(f.Window, PhysicalKey.ArrowDown);
        Assert.That(f.Page.Draft.RevisionGridQuickSearchTimeout, Is.EqualTo(100));
        f.Number(timeout, "1000000");
        Assert.That(f.Page.Draft.RevisionGridQuickSearchTimeout, Is.EqualTo(1_000_000));
        Assert.That(f.Page.Validate(out string? error), Is.True, error);
    }

    [AvaloniaTest]
    public void AllSixOriginalPullActionsAreSelectableAndOnlyChangeTheDraft()
    {
        GeneralSettingsValues initial = new(); using Scope f = new(initial);
        ComboBox actions = f.Field<ComboBox>("cboDefaultPullAction");
        GitPullAction[] expected = [GitPullAction.None, GitPullAction.Merge, GitPullAction.Rebase, GitPullAction.Fetch, GitPullAction.FetchAll, GitPullAction.FetchPruneAll];
        Assert.That(actions.Items.Count, Is.EqualTo(expected.Length));
        for (int index = 0; index < expected.Length; index++)
        {
            f.Show(actions); PushProcessTestDriver.Pointer(actions); Pump(f.Window);
            Assert.That(actions.IsDropDownOpen, Is.True);
            PushProcessTestDriver.Pointer(actions.ContainerFromIndex(index)!); Pump(f.Window);
            Assert.That(f.Page.Draft.DefaultPullAction, Is.EqualTo(expected[index]));
        }
        Assert.That(initial.DefaultPullAction, Is.EqualTo(GitPullAction.Merge));
    }

    [AvaloniaTest]
    public void CloneDestinationOffersHistoryButAcceptsALiteralLinuxDirectory()
    {
        string[] history = ["/tmp/Settings Case", "/tmp/settings case", "/tmp/recent destination"];
        using Scope f = new(new(), history);
        ComboBox destination = f.Field<ComboBox>("cbDefaultCloneDestination");
        Assert.That(destination.IsEditable, Is.True);
        Assert.That(destination.Items.Cast<string>(), Is.EqualTo(history), "Linux paths differing only by case remain distinct.");
        string literal = "/tmp/new 'quoted';$(literal) destination Ω";
        f.EditChoice(destination, literal); f.Click("chkStartWithRecentWorkingDir");
        Assert.That(f.Page.Draft.DefaultCloneDestinationPath, Is.EqualTo(literal));
        Assert.That(destination.Items.Cast<string>(), Is.EqualTo(history), "Editing the draft must not publish a history entry.");
        Assert.That(f.Page.Validate(out string? error), Is.True, error);
    }

    [AvaloniaTest]
    public void CancelingTheFolderPickerKeepsThePendingTextAndAcceptingItOnlyChangesTheDraft()
    {
        string initial = "/tmp/initial clone parent", pending = "/tmp/pending clone parent", chosen = "/tmp/chosen clone parent Ω";
        using Scope f = new(new() { DefaultCloneDestinationPath = initial });
        ComboBox destination = f.Field<ComboBox>("cbDefaultCloneDestination"); f.EditChoice(destination, pending);
        List<(Window Owner, string Initial)> calls = []; string? next = null;
        f.Page.BrowseFolderAsync = (owner, _, path) => { calls.Add((owner, path)); return Task.FromResult(next); };
        f.Click("btnDefaultDestinationBrowse");
        Assert.That(calls, Has.Count.EqualTo(1)); Assert.That(calls[0].Owner, Is.SameAs(f.Window));
        Assert.That(calls[0].Initial, Is.EqualTo(pending)); Assert.That(f.Page.Draft.DefaultCloneDestinationPath, Is.EqualTo(pending));
        next = chosen; f.Click("btnDefaultDestinationBrowse");
        Assert.That(calls, Has.Count.EqualTo(2)); Assert.That(calls[1].Initial, Is.EqualTo(pending));
        Assert.That(f.Page.Draft.DefaultCloneDestinationPath, Is.EqualTo(chosen));
    }

    [AvaloniaTest]
    [TestCase("edit")]
    [TestCase("saved")]
    [TestCase("detach")]
    public void ADelayedFolderChoiceCannotReplaceNewerTextAnAcceptedPublicationOrADetachedPage(string change)
    {
        using Scope f = new(new() { DefaultCloneDestinationPath = "/tmp/before picker" });
        TaskCompletionSource<string?> completion = new(); int calls = 0;
        f.Page.BrowseFolderAsync = (_, _, _) => { calls++; return completion.Task; };
        f.Click("btnDefaultDestinationBrowse");
        Assert.That(calls, Is.EqualTo(1)); Assert.That(f.Field<Button>("btnDefaultDestinationBrowse").IsEffectivelyEnabled, Is.False);
        string expected = "/tmp/before picker";
        if (change == "edit") { expected = "/tmp/new keyboard draft Ω"; f.EditChoice(f.Field<ComboBox>("cbDefaultCloneDestination"), expected); }
        else if (change == "saved") { expected = "/tmp/new accepted publication Ω"; f.Page.AcceptSaved(f.Page.Draft with { DefaultCloneDestinationPath = expected }); }
        else { f.Window.Content = new TextBlock { Text = "The user left this page." }; Pump(f.Window); }
        completion.SetResult("/tmp/late folder selection"); Pump(f.Window);
        Assert.That(f.Page.Draft.DefaultCloneDestinationPath, Is.EqualTo(expected));
        Assert.That(calls, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public void AcceptSavedAdoptsMergedValuesWithoutTurningFutureEditsIntoAReversion()
    {
        using Scope f = new(new()); f.Click("chkShowStashCountInBrowseWindow");
        GeneralSettingsValues merged = f.Page.Draft with
        {
            DefaultCloneDestinationPath = "/tmp/concurrently published Ω", DefaultPullAction = GitPullAction.FetchAll,
            FollowRenamesInFileHistory = false, FollowRenamesInFileHistoryExact = true, UpdateSubmodulesOnCheckout = false,
            MaxRevisionGraphCommits = 0, RevisionGridQuickSearchTimeout = 1200
        };
        f.Page.AcceptSaved(merged); Pump(f.Window);
        Assert.That(f.Page.Draft, Is.EqualTo(merged));
        Assert.That(f.Field<ComboBox>("cbDefaultCloneDestination").Text, Is.EqualTo(merged.DefaultCloneDestinationPath));
        Assert.That(f.Field<NumericUpDown>("_NO_TRANSLATE_MaxCommits").IsEffectivelyEnabled, Is.False);
        f.Click("chkStartWithRecentWorkingDir");
        Assert.That(f.Page.Draft, Is.EqualTo(merged with { StartWithRecentWorkingDir = true }));
        Assert.That(f.Page.Validate(out string? error), Is.True, error);
    }

    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("en", true)]
    [TestCase("fr-FR", false)]
    [TestCase("fr-FR", true)]
    public void OriginalGroupsAndControlsRemainReadableAndReachableInBothLanguagesAndThemes(string culture, bool dark)
    {
        using Scope f = new(new(), culture: culture, dark: dark);
        string[] groups = ["groupBoxPerformance", "groupBoxBehaviour", "groupBoxTelemetry"];
        double previous = double.NegativeInfinity;
        foreach (string name in groups)
        {
            Grid group = f.Field<Grid>(name); double y = group.TranslatePoint(default, f.Page)!.Value.Y;
            Assert.That(y, Is.GreaterThan(previous)); previous = y;
        }
        CheckBox unsupportedConsole = f.Check("chkShowGitCommandLine"), telemetry = f.Check("chkTelemetry");
        Assert.That(unsupportedConsole.IsEffectivelyVisible, Is.True); Assert.That(unsupportedConsole.IsEffectivelyEnabled, Is.False);
        Assert.That(telemetry.IsEffectivelyVisible, Is.True); Assert.That(telemetry.IsEffectivelyEnabled, Is.False);
        string[] reachable = ["chkShowGitStatusInToolbar", "chkShowGitStatusForArtificialCommits", "chkShowSubmoduleStatusInBrowse",
            "chkShowStashCountInBrowseWindow", "chkShowAheadBehindDataInBrowseWindow", "chkCheckForUncommittedChangesInCheckoutBranch", "lblCommitsLimit",
            "_NO_TRANSLATE_MaxCommits", "chkCloseProcessDialog", "chkUseHistogramDiffAlgorithm", "chkStashUntrackedFiles", "chkUpdateModules",
            "chkFollowRenamesInFileHistory", "chkFollowRenamesInFileHistoryExact", "chkStartWithRecentWorkingDir", "cbDefaultCloneDestination",
            "btnDefaultDestinationBrowse", "cboDefaultPullAction", "RevisionGridQuickSearchTimeout", "SettingsUseBrowseForFileHistory"];
        foreach (string name in reachable)
        {
            Control control = f.Field<Control>(name); f.Show(control);
            Point center = control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), f.Window)!.Value;
            Assert.That(control.IsEffectivelyVisible && control.IsEffectivelyEnabled, Is.True, name);
            Assert.That(new Rect(default, f.Window.ClientSize).Contains(center), Is.True, name + " must scroll into the visible host.");
            Assert.That(control.Bounds.Width, Is.GreaterThan(0), name);
        }
        TextBlock behaviour = f.Field<Border>("groupBoxBehaviourCaption").GetVisualDescendants().OfType<TextBlock>().Single();
        Assert.That(behaviour.Text, Is.EqualTo(culture == "en" ? "Behaviour" : "Comportement"));
        f.Show(f.Field<Grid>("groupBoxPerformance")); Capture(f.Window, $"general-settings-{culture}-{(dark ? "dark" : "light")}-top.png");
        f.Show(telemetry); Capture(f.Window, $"general-settings-{culture}-{(dark ? "dark" : "light")}-bottom.png");
    }

    private sealed class Scope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
        internal Window Window { get; }
        internal GeneralSettingsPage Page { get; }
        internal Scope(GeneralSettingsValues initial, IReadOnlyList<string>? history = null, string culture = "en", bool dark = false)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Page = new(initial, TranslationCatalog.LoadDefault(), history);
            Window = new() { Width = 740, Height = 590, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
                Content = new ScrollViewer { Content = Page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
            Window.Show(); Pump(Window);
        }
        internal T Field<T>(string name) where T : Control => PushProcessTestDriver.Field<T>(Window, name);
        internal CheckBox Check(string name) => Field<CheckBox>(name);
        internal void Show(Control control) { control.BringIntoView(); Pump(Window); }
        internal void Click(string name) => Click(Field<Control>(name));
        internal void Click(Control control) { Show(control); PushProcessTestDriver.Pointer(control); Pump(Window); }
        internal void Number(NumericUpDown number, string text)
        {
            Show(number); TextBox editor = number.GetVisualDescendants().OfType<TextBox>().Single(); Edit(editor, text);
            Press(Window, PhysicalKey.Tab); Pump(Window);
        }
        internal void EditChoice(ComboBox choice, string text)
        {
            Show(choice); Edit(choice.GetVisualDescendants().OfType<TextBox>().Single(), text); Pump(Window);
        }
        private void Edit(TextBox editor, string text)
        {
            PushProcessTestDriver.Pointer(editor); Press(Window, PhysicalKey.A, RawInputModifiers.Control); Press(Window, PhysicalKey.Backspace);
            if (text.Length > 0) { Window.KeyTextInput(text); } Pump(Window);
        }
        public void Dispose() { try { Window.Close(); } finally { CultureInfo.CurrentUICulture = _culture; } }
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); Dispatcher.UIThread.RunJobs(); }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(Window window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string folder = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
}

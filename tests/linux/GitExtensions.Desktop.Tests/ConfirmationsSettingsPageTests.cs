using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ConfirmationsSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalGroupsAndThirteenFunctionalChoicesUseGlobalScopeAndReadableLabels(string language, bool dark)
    {
        using PortableHelperSettingsFixture f = new(language, dark);
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("General");
        SettingsNavigationTestDriver.Select(settings, "Confirmations");
        Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo("Confirmations"));
        ConfirmationsSettingsPage page = Field<ConfirmationsSettingsPage>(settings, "ConfirmationsSettingsPage");
        Assert.That(page.GetVisualDescendants().OfType<CheckBox>().Count(), Is.EqualTo(17));
        Assert.That(page.GetVisualDescendants().OfType<RadioButton>().Select(radio => radio.Name), Is.EqualTo(new[] { "ConfirmationsGlobalRB" }));
        Assert.That(Field<RadioButton>(settings, "ConfirmationsGlobalRB").IsChecked, Is.True);
        foreach (string newlyPorted in new[] { "chkBranchDeleteUnmerged", "chkBranchCheckoutConfirmation", "chkAutoPopStashAfterCheckout", "chkSecondAbortConfirmation" })
        { Assert.That(page.GetVisualDescendants().OfType<Control>().Any(control => control.Name == newlyPorted), Is.True); }
        foreach (ConfirmationSetting choice in ConfirmationSettingsCatalog.Fields)
        {
            CheckBox box = Field<CheckBox>(settings, choice.Control); box.BringIntoView(); PortableHelperSettingsFixture.Pump(settings);
            TextBlock label = (TextBlock)box.Content!;
            Assert.That(label.Text, Is.EqualTo(f.Original("ConfirmationsSettingsPage", choice.Control + ".Text")));
            Assert.That(box.IsThreeState, Is.EqualTo(choice.ThreeState));
            Assert.That(box.IsChecked, Is.EqualTo(choice.Nullable ? null : (bool?)choice.Inverted));
            Point origin = label.TranslatePoint(default, box)!.Value;
            Assert.That(origin.X, Is.GreaterThanOrEqualTo(0));
            Assert.That(origin.X + label.Bounds.Width, Is.LessThanOrEqualTo(box.Bounds.Width + .5));
            Assert.That(origin.Y + label.TextLayout.Height, Is.LessThanOrEqualTo(box.Bounds.Height + .5), "Translated label must fit its actual check box: " + choice.Control);
        }
        CheckBox first = Field<CheckBox>(settings, "chkAmend"); first.BringIntoView(); PortableHelperSettingsFixture.Pump(settings);
        PortableHelperSettingsFixture.Capture(settings, "confirmations-" + language + (dark ? "-dark.png" : "-light.png"));
        PushProcessTestDriver.Pointer(first); Assert.That(first.IsKeyboardFocusWithin, Is.True);
        Assert.That(page.Draft.DontConfirmAmend, Is.True, "First click changes the choice immediately.");
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.Space);
        Assert.That(page.Draft.DontConfirmAmend, Is.False);
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.Tab);
        Assert.That(Field<CheckBox>(settings, "chkUndoLastCommitConfirmation").IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(settings, PhysicalKey.Enter);
        Assert.That(settings.IsVisible && !completion.IsCompleted, Is.True, "Enter within the page must not invoke Settings OK.");
        Assert.That(f.Publications, Is.Zero);
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings"));
        Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.False);
        Assert.That(File.Exists(new CommitPreferencesStore().FilePath), Is.False);
    }

    [AvaloniaTest]
    [TestCase("AutoPopStashAfterCheckoutBranch", "chkAutoPopStashAfterCheckout", null)]
    [TestCase("AutoPopStashAfterCheckoutBranch", "chkAutoPopStashAfterCheckout", false)]
    [TestCase("AutoPopStashAfterCheckoutBranch", "chkAutoPopStashAfterCheckout", true)]
    [TestCase("AutoPopStashAfterPull", "chkAutoPopStashAfterPull", null)]
    [TestCase("AutoPopStashAfterPull", "chkAutoPopStashAfterPull", false)]
    [TestCase("AutoPopStashAfterPull", "chkAutoPopStashAfterPull", true)]
    [TestCase("DontConfirmUpdateSubmodulesOnCheckout", "chkUpdateModules", null)]
    [TestCase("DontConfirmUpdateSubmodulesOnCheckout", "chkUpdateModules", false)]
    [TestCase("DontConfirmUpdateSubmodulesOnCheckout", "chkUpdateModules", true)]
    public async Task NullableChoicesCycleThroughAllThreeOriginalInvertedStatesWithoutWriting(string name, string control, bool? initial)
    {
        using PortableHelperSettingsFixture f = new();
        ConfirmationsSettingsSession session = new(); session.Save(session.Initial.With(name, initial));
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Confirmations");
        ConfirmationsSettingsPage page = Field<ConfirmationsSettingsPage>(settings, "ConfirmationsSettingsPage");
        CheckBox choice = Field<CheckBox>(settings, control); choice.BringIntoView(); PortableHelperSettingsFixture.Pump(settings);
        Assert.That(choice.IsChecked, Is.EqualTo(initial.HasValue ? (bool?)!initial.Value : null));
        Dictionary<string, byte[]> before = Profiles(); HashSet<bool?> states = [page.Draft.Get(name)];
        PushProcessTestDriver.Pointer(choice); states.Add(page.Draft.Get(name));
        for (int step = 0; step < 2; step++) { PortableHelperSettingsFixture.Press(settings, PhysicalKey.Space); states.Add(page.Draft.Get(name)); }
        Assert.That(states, Is.EquivalentTo(new bool?[] { null, false, true }));
        Assert.That(page.Draft.Get(name), Is.EqualTo(initial));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await completion.WaitAsync(TimeSpan.FromSeconds(10));
        SameProfiles(before);
    }

    [AvaloniaTest]
    [TestCase("chkSecondAbortConfirmation", "DontConfirmSecondAbortConfirmation")]
    [TestCase("chkUndoLastCommitConfirmation", "DontConfirmUndoLastCommit")]
    [TestCase("chkRebaseOnTopOfSelectedCommit", "DontConfirmRebase")]
    [TestCase("chkResolveConflicts", "DontConfirmResolveConflicts")]
    [TestCase("chkCommitAfterConflictsResolved", "DontConfirmCommitAfterConflictsResolved")]
    public async Task OriginalThreeStateBoolUsesCheckedSemanticsRatherThanInventingNullablePersistence(string control, string name)
    {
        using PortableHelperSettingsFixture f = new();
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Confirmations");
        ConfirmationsSettingsPage page = Field<ConfirmationsSettingsPage>(settings, "ConfirmationsSettingsPage");
        CheckBox box = Field<CheckBox>(settings, control); box.BringIntoView(); PortableHelperSettingsFixture.Pump(settings);
        PushProcessTestDriver.Pointer(box);
        for (int step = 0; step < 3 && box.IsChecked is not null; step++) { PortableHelperSettingsFixture.Press(settings, PhysicalKey.Space); }
        Assert.That(box.IsChecked, Is.Null);
        Assert.That(page.Draft.Get(name), Is.False, "WinForms.Checked includes the Indeterminate state for these bool settings.");
        PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => settings.Navigation.IsEnabled && f.Publications == 1);
        Assert.That(new ConfirmationsSettingsSession().Initial.Get(name), Is.False);
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings")); await completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [AvaloniaTest]
    public async Task ApplyReopenAndCancelKeepOnlyAcceptedChoicesAndPreserveAWorkflowPeerEdit()
    {
        using PortableHelperSettingsFixture f = new();
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Confirmations");
        PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "chkAmend"));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => f.Publications == 1 && settings.Navigation.IsEnabled);
        Assert.That(new CommitPreferencesStore().Load().DontConfirmAmend, Is.True);
        new CommitPreferencesStore().Save(new CommitPreferencesStore().Load() with { LastCommitMessage = "peer workflow draft Ω" });
        Dictionary<string, byte[]> accepted = Profiles();
        PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "chkAmend"));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings"));
        Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
        SameProfiles(accepted);
        (DesktopSettingsDialog reopened, Task<bool> reopenedCompletion) = f.Open("Confirmations");
        Assert.That(Field<CheckBox>(reopened, "chkAmend").IsChecked, Is.False);
        Assert.That(new CommitPreferencesStore().Load().LastCommitMessage, Is.EqualTo("peer workflow draft Ω"));
        PushProcessTestDriver.Pointer(Field<Button>(reopened, "CancelDesktopSettings")); await reopenedCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        SameProfiles(accepted);
    }

    [AvaloniaTest]
    public async Task PartialApplyAccusesTheFirstFileBeforeSelectingTheFailedPageAndRetryDoesNotReplayIt()
    {
        using PortableHelperSettingsFixture f = new();
        (DesktopSettingsDialog settings, Task<bool> completion) = f.Open("Confirmations");
        PushProcessTestDriver.Pointer(Field<CheckBox>(settings, "chkAmend"));
        CheckBox fetch = Field<CheckBox>(settings, "chkFetchAndPruneAllConfirmation"); fetch.BringIntoView(); PortableHelperSettingsFixture.Pump(settings); PushProcessTestDriver.Pointer(fetch);
        string blocked = new PullPreferencesStore().FilePath; Directory.CreateDirectory(blocked);
        PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => f.Publications == 1 && settings.Navigation.IsEnabled);
        Assert.That(settings.Navigation.SelectedPage.Id, Is.EqualTo("Confirmations"));
        Assert.That(Field<TextBlock>(settings, "SettingsNotice").Text, Does.Contain("Some settings may already have been saved"));
        Assert.That(new CommitPreferencesStore().Load().DontConfirmAmend, Is.True);
        Assert.That(Field<CheckBox>(settings, "chkAmend").IsChecked, Is.False);
        Assert.That(fetch.IsChecked, Is.False, "The failed file's draft must remain available.");
        new CommitPreferencesStore().Save(new CommitPreferencesStore().Load() with { DontConfirmAmend = false, LastCommitMessage = "new peer" });
        byte[] peer = File.ReadAllBytes(new CommitPreferencesStore().FilePath); Directory.Delete(blocked);
        PushProcessTestDriver.Pointer(Field<Button>(settings, "ApplyDesktopSettings"));
        await PushProcessTestDriver.UntilAsync(settings, () => f.Publications == 2 && settings.Navigation.IsEnabled);
        Assert.That(new PullPreferencesStore().Load().DontConfirmFetchAndPruneAll, Is.True);
        Assert.That(File.ReadAllBytes(new CommitPreferencesStore().FilePath), Is.EqualTo(peer));
        PushProcessTestDriver.Pointer(Field<Button>(settings, "CancelDesktopSettings"));
        Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(File.ReadAllBytes(new CommitPreferencesStore().FilePath), Is.EqualTo(peer));
    }

    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static Dictionary<string, byte[]> Profiles()
    {
        string directory = Path.GetDirectoryName(new PreferencesStore().FilePath)!;
        return Directory.Exists(directory) ? Directory.GetFiles(directory).ToDictionary(path => path, File.ReadAllBytes) : new();
    }
    private static void SameProfiles(Dictionary<string, byte[]> expected)
    {
        Dictionary<string, byte[]> actual = Profiles(); Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
        foreach ((string path, byte[] bytes) in expected) { Assert.That(actual[path], Is.EqualTo(bytes), path); }
    }
}

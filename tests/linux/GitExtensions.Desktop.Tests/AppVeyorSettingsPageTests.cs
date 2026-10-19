using System.Runtime.InteropServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AppVeyorSettingsPageTests
{
    private const string ProjectsHelp = "If you want to use the result of different projects, separate your different projects names by a '|'.";
    private const string TokenHelp = "Token used to be able to query AppVeyor rest Api. You will find out in the user account menu.";
    private const string TestsCaption = "Display test results in build status summary for each build result (network intensive!)";

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task WindowsSevenRowTableHasOriginalFieldsHelpAndReadableTranslatedThreeStateChoice(string language, bool dark)
    {
        await using AppVeyorSettingsUiFixture f = await AppVeyorSettingsUiFixture.CreateAsync(language, dark); await f.ShowAsync();
        f.Select(GitDistributedSettingsScope.Global); Dictionary<string, byte[]> before = f.Profile.Material();
        Grid table = f.Field<Grid>("tableLayoutPanel1"); Assert.That(table.ColumnDefinitions.Count, Is.EqualTo(2)); Assert.That(table.RowDefinitions.Count, Is.EqualTo(7));
        Assert.That(f.Page.GetVisualDescendants().OfType<TextBox>().Count(), Is.EqualTo(3));
        Assert.That(f.Page.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible && button is not ToggleButton), Is.Empty, "This page has no invented buttons or links.");
        Assert.That(f.Field<WrapPanel>("flowLayoutPanel1").Children, Is.Empty);
        Assert.That(Grid.GetRow(f.Field<TextBox>("AppVeyorAccountName")), Is.EqualTo(0));
        Assert.That(Grid.GetRow(f.Field<TextBox>("AppVeyorAccountToken")), Is.EqualTo(1));
        Assert.That(Grid.GetRow(f.Field<TextBox>("AppVeyorProjectName")), Is.EqualTo(3));
        Assert.That(Grid.GetRow(f.Field<CheckBox>("cbLoadTestResults")), Is.EqualTo(5));
        TranslationCatalog text = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), System.Globalization.CultureInfo.GetCultureInfo(language));
        Assert.That(f.Field<TextBlock>("label1").Text, Is.EqualTo(text.Translate("AppVeyorSettingsUserControl", "label1", "Text", ProjectsHelp)));
        Assert.That(f.Field<TextBlock>("label2").Text, Is.EqualTo(text.Translate("AppVeyorSettingsUserControl", "label2", "Text", TokenHelp)));
        Assert.That(((TextBlock)f.Field<CheckBox>("cbLoadTestResults").Content!).Text,
            Is.EqualTo(text.Translate("AppVeyorSettingsUserControl", "cbLoadTestResults", "Text", TestsCaption)));
        Assert.That(f.Field<TextBlock>("label1").Margin.Bottom, Is.EqualTo(10)); Assert.That(f.Field<TextBlock>("label2").Margin.Bottom, Is.EqualTo(10));
        Assert.That(f.Page.GetVisualDescendants().OfType<RadioButton>().Count(control => control.IsEffectivelyVisible), Is.Zero, "The parent owns the shared scope header.");
        foreach (TextBlock label in f.Page.GetVisualDescendants().OfType<TextBlock>().Where(label => label.IsEffectivelyVisible && !string.IsNullOrEmpty(label.Text)))
        {
            Assert.That(label.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False, label.Text);
            Assert.That(label.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(label.Bounds.Width + .5), label.Text);
            Assert.That(new Rect(default, f.Dialog.ClientSize).Contains(new Rect(label.TranslatePoint(default, f.Dialog)!.Value, label.Bounds.Size)), Is.True, label.Text);
        }
        TextBox account = f.Field<TextBox>("AppVeyorAccountName"), token = f.Field<TextBox>("AppVeyorAccountToken"), project = f.Field<TextBox>("AppVeyorProjectName");
        Assert.That(account.TranslatePoint(default, table)!.Value.X, Is.EqualTo(token.TranslatePoint(default, table)!.Value.X));
        Assert.That(account.TranslatePoint(default, table)!.Value.X, Is.EqualTo(project.TranslatePoint(default, table)!.Value.X));
        Assert.That(table.Bounds.Height, Is.LessThan(270), "Wrapped original French text must remain compact and readable.");
        Assert.That(token.PasswordChar, Is.Not.EqualTo('\0')); Assert.That(token.RevealPassword, Is.False);
        using WriteableBitmap image = await AvatarUiTestDriver.Frame(f.Dialog);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "appveyor-settings-" + language + (dark ? "-dark.png" : "-light.png")); image.Save(path); TestContext.AddTestAttachment(path);
        f.Profile.AssertMaterial(before); Assert.That(f.OpenedUrls, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ActualTypedTokensPaintOnlyMaskGlyphsAndDifferentSecretsOfEqualLengthProduceIdenticalPixels(bool dark)
    {
        await using AppVeyorSettingsUiFixture f = await AppVeyorSettingsUiFixture.CreateAsync(dark: dark); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); TextBox token = f.Field<TextBox>("AppVeyorAccountToken");
        f.Edit("AppVeyorAccountToken", "AAAA1111"); f.Click("AppVeyorAccountName");
        using WriteableBitmap first = await AvatarUiTestDriver.Frame(f.Dialog); byte[] firstPixels = Crop(first, f.Dialog, token);
        f.Edit("AppVeyorAccountToken", "zzzz9999"); f.Click("AppVeyorAccountName");
        TextPresenter presenter = token.GetVisualDescendants().OfType<TextPresenter>().Single(control => control.Name == "PART_TextPresenter");
        string painted = string.Concat(presenter.TextLayout.TextLines.SelectMany(line => line.TextRuns).Select(run => run.Text.ToString()));
        Assert.That(painted, Is.EqualTo(new string(token.PasswordChar, 8)));
        using WriteableBitmap second = await AvatarUiTestDriver.Frame(f.Dialog); Assert.That(Crop(second, f.Dialog, token), Is.EqualTo(firstPixels));
        f.Edit("AppVeyorAccountToken", ""); f.Click("AppVeyorAccountName");
        using WriteableBitmap empty = await AvatarUiTestDriver.Frame(f.Dialog); Assert.That(Crop(empty, f.Dialog, token), Is.Not.EqualTo(firstPixels), "The matching frames must contain real mask ink, not two blank editors.");
        f.Profile.AssertMaterial(before); Assert.That(f.OpenedUrls, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase("none")] [TestCase("bare")]
    public async Task RepositoryAvailabilityUsesTheSharedScopeChoicesAndEffectiveFieldsStayReadOnly(string kind)
    {
        await using AppVeyorSettingsUiFixture f = await AppVeyorSettingsUiFixture.CreateAsync(kind: kind); await f.ShowAsync();
        Assert.That(f.Field<RadioButton>("BuildServerIntegrationSettingsPageGlobal").IsVisible, Is.True);
        if (kind == "none")
        {
            Assert.That(f.Field<RadioButton>("BuildServerIntegrationSettingsPageGlobal").IsChecked, Is.True);
            foreach (string scope in new[] { "Effective", "Local", "Distributed" })
            { Assert.That(f.Field<RadioButton>("BuildServerIntegrationSettingsPage" + scope).IsVisible, Is.False); }
            Assert.That(f.Field<TextBox>("AppVeyorAccountName").IsEffectivelyEnabled, Is.True);
        }
        else
        {
            Assert.That(f.Field<TextBox>("AppVeyorAccountName").IsEffectivelyEnabled, Is.False);
            Assert.That(f.Field<CheckBox>("cbLoadTestResults").IsEffectivelyEnabled, Is.False);
            Assert.That(f.Field<RadioButton>("BuildServerIntegrationSettingsPageDistributed").IsEnabled, Is.False);
            f.Select(GitDistributedSettingsScope.Global); Assert.That(f.Field<CheckBox>("cbLoadTestResults").IsEffectivelyEnabled, Is.True);
        }
    }

    [AvaloniaTest]
    [TestCase(null)] [TestCase("future-bool")] [TestCase("true")] [TestCase("false")]
    public async Task UntouchedNullableAndUnknownBooleanRawValuesAreNotRewrittenByScopeSwitchOrNoOpApply(string? raw)
    {
        await using AppVeyorSettingsUiFixture f = await AppVeyorSettingsUiFixture.CreateAsync(prepare: profile =>
        { foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { AppVeyorSettingsUiFixture.Seed(profile, scope, loadTests: raw); } });
        await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material();
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes)
        {
            f.Select(scope); CheckBox check = f.Field<CheckBox>("cbLoadTestResults"); Assert.That(check.IsThreeState, Is.True);
            Assert.That(check.IsChecked, Is.EqualTo(raw == "true" ? true : raw == "false" ? false : (bool?)null));
        }
        Assert.That(f.Session.HasChanges, Is.False); f.Click("ApplyDesktopSettings"); await f.SavedAsync(); AssertXmlUnchanged(f, before);
    }

    [AvaloniaTest]
    [TestCase("pointer")] [TestCase("space")]
    public async Task ActualThreeStateGesturesCanStoreTrueFalseAndUnsetWithoutSavingUntilApply(string gesture)
    {
        await using AppVeyorSettingsUiFixture f = await AppVeyorSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); CheckBox check = f.Field<CheckBox>("cbLoadTestResults"); HashSet<bool?> seen = [check.IsChecked];
        for (int index = 0; index < 3; index++)
        {
            if (gesture == "pointer") { f.Click("cbLoadTestResults"); }
            else { if (!check.IsKeyboardFocusWithin) { check.Focus(); } f.Press(PhysicalKey.Space); }
            seen.Add(check.IsChecked);
            Assert.That(f.Session.Read(GitDistributedSettingsScope.Global, AppVeyorSettings.LoadTestsResultsKey),
                Is.EqualTo(check.IsChecked is bool value ? value ? "true" : "false" : null));
        }
        Assert.That(seen, Is.EquivalentTo(new bool?[] { null, true, false })); Assert.That(check.IsChecked, Is.Null);
        f.Profile.AssertMaterial(before); f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values.ContainsKey(AppVeyorSettings.LoadTestsResultsKey), Is.False);
    }

    [AvaloniaTest]
    public async Task FolderSuggestionAndNoOpApplyOrCancelNeverCreateAProjectIntention()
    {
        await using AppVeyorSettingsUiFixture f = await AppVeyorSettingsUiFixture.CreateAsync(prepare: profile =>
        { foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { AppVeyorSettingsUiFixture.Seed(profile, scope, project: null); } });
        await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material(); f.Select(GitDistributedSettingsScope.Global);
        Assert.That(f.Field<TextBox>("AppVeyorProjectName").Text, Is.EqualTo(f.Session.DefaultProjectName));
        Assert.That(f.Session.Read(GitDistributedSettingsScope.Global, AppVeyorSettings.ProjectNameKey), Is.Null);
        Assert.That(f.Session.AppVeyor(GitDistributedSettingsScope.Global).ProjectName, Is.Empty, "The runtime continues account discovery until a suggestion is explicitly saved.");
        Assert.That(f.Session.HasChanges, Is.False); f.Click("ApplyDesktopSettings"); await f.SavedAsync(); AssertXmlUnchanged(f, before);
        f.Edit("AppVeyorAccountName", "discarded-account"); f.Click("CancelDesktopSettings"); await f.UntilAsync(() => f.Modal.IsCompleted); AssertXmlUnchanged(f, before);
    }

    [AvaloniaTest]
    public async Task RealPageEditsSaveTheVisibleDefaultForEveryEditedScopeEvenAfterLeavingThatScope()
    {
        await using AppVeyorSettingsUiFixture f = await AppVeyorSettingsUiFixture.CreateAsync(prepare: profile =>
        { foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { AppVeyorSettingsUiFixture.Seed(profile, scope, project: null); } });
        await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material();
        f.Select(GitDistributedSettingsScope.Global); f.Edit("AppVeyorAccountName", "global-account");
        f.Select(GitDistributedSettingsScope.Local); f.Edit("AppVeyorAccountToken", "local-token");
        f.Select(GitDistributedSettingsScope.Distributed); Assert.That(f.Session.Read(GitDistributedSettingsScope.Local, AppVeyorSettings.ProjectNameKey), Is.Null);
        f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        foreach (GitDistributedSettingsScope scope in new[] { GitDistributedSettingsScope.Global, GitDistributedSettingsScope.Local })
        {
            GitXmlSettingsSnapshot saved = await f.Profile.ReadAsync(scope); Assert.That(saved.Values[AppVeyorSettings.ProjectNameKey], Is.EqualTo(f.Session.DefaultProjectName));
            Assert.That(saved.Values["future.λ"], Is.EqualTo("keep <literal> & \nbody")); Assert.That(saved.Values[AppVeyorSettings.LoadTestsResultsKey], Is.EqualTo("future-bool"));
            AssertOtherProviderValues(saved);
        }
        Assert.That(File.ReadAllBytes(f.Profile.Path(GitDistributedSettingsScope.Distributed)), Is.EqualTo(before[f.Profile.Path(GitDistributedSettingsScope.Distributed)]));
    }

    [AvaloniaTest]
    public async Task ExplicitProviderActivationAcceptsTheVisibleDefaultOnlyWhenApplyIsPressed()
    {
        await using AppVeyorSettingsUiFixture f = await AppVeyorSettingsUiFixture.CreateAsync(prepare: profile =>
            AppVeyorSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Global, project: null, provider: "Future CI"));
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global); Dictionary<string, byte[]> before = f.Profile.Material();
        Assert.That(f.Field<ComboBox>("BuildServerProvider").SelectedItem, Is.EqualTo("Future CI")); Assert.That(f.Page.IsVisible, Is.False);
        await f.ProviderAsync(AppVeyorSettings.ProviderName); Assert.That(f.Page.IsVisible, Is.True);
        Assert.That(f.Session.Read(GitDistributedSettingsScope.Global, AppVeyorSettings.ProjectNameKey), Is.Null); f.Profile.AssertMaterial(before);
        f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values[AppVeyorSettings.ProjectNameKey], Is.EqualTo(f.Session.DefaultProjectName));
    }

    [AvaloniaTest]
    public async Task ReturningToAnUnknownProviderBeforeApplyDoesNotPersistTheInactivePageSuggestion()
    {
        await using AppVeyorSettingsUiFixture f = await AppVeyorSettingsUiFixture.CreateAsync(prepare: profile =>
            AppVeyorSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Global, project: null, provider: "Future CI"));
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global); Dictionary<string, byte[]> before = f.Profile.Material();
        await f.ProviderAsync(AppVeyorSettings.ProviderName); await f.ProviderAsync("Future CI");
        f.Click("ApplyDesktopSettings"); await f.SavedAsync(); AssertXmlUnchanged(f, before);
        Assert.That(f.Session.Read(GitDistributedSettingsScope.Global, AppVeyorSettings.ProviderKey), Is.EqualTo("Future CI"));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ExplicitlyClearedProjectRemainsEmptyAcrossApplyAndScopeChangesWhileRuntimeKeepsAccountDiscovery(bool existingProject)
    {
        await using AppVeyorSettingsUiFixture f = await AppVeyorSettingsUiFixture.CreateAsync(prepare: profile =>
            AppVeyorSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Global, project: existingProject ? "sample-account/old-project" : null));
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global); f.Edit("AppVeyorProjectName", "");
        f.Edit("AppVeyorAccountName", "discovery-account"); f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        Assert.That(f.Field<TextBox>("AppVeyorProjectName").Text, Is.Empty);
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values.ContainsKey(AppVeyorSettings.ProjectNameKey), Is.False);
        f.Select(GitDistributedSettingsScope.Local); f.Select(GitDistributedSettingsScope.Global); Assert.That(f.Field<TextBox>("AppVeyorProjectName").Text, Is.Empty);
        Assert.That(f.Session.AppVeyor(GitDistributedSettingsScope.Global).ProjectName, Is.Empty);
        Assert.That(AppVeyorProjects.Resolve(f.Session.AppVeyor(GitDistributedSettingsScope.Global).ProjectName, "discovery-account"), Is.Empty);
        await f.ReopenAsync(); f.Select(GitDistributedSettingsScope.Global);
        Assert.That(f.Field<TextBox>("AppVeyorProjectName").Text, Is.EqualTo(f.Session.DefaultProjectName), "A new Windows-style load may suggest the folder again, without writing it.");
        Assert.That(f.Session.HasChanges, Is.False); Assert.That(f.Session.AppVeyor(GitDistributedSettingsScope.Global).ProjectName, Is.Empty);
    }

    [AvaloniaTest]
    public async Task InheritanceUsesLocalDistributedGlobalWhileActualApplyOnlyChangesTheChosenScopeAndCancelDiscardsLaterDraft()
    {
        await using AppVeyorSettingsUiFixture f = await AppVeyorSettingsUiFixture.CreateAsync(prepare: profile =>
        {
            AppVeyorSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Global, loadTests: "true");
            AppVeyorSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Distributed, loadTests: "false");
            AppVeyorSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Local, loadTests: null);
        });
        await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material(); Assert.That(f.Field<CheckBox>("cbLoadTestResults").IsChecked, Is.False);
        f.Select(GitDistributedSettingsScope.Local); CheckBox check = f.Field<CheckBox>("cbLoadTestResults"); Assert.That(check.IsChecked, Is.Null);
        for (int count = 0; count < 3 && check.IsChecked != true; count++) { f.Click("cbLoadTestResults"); }
        Assert.That(check.IsChecked, Is.True); f.Select(GitDistributedSettingsScope.Effective); Assert.That(check.IsChecked, Is.True); Assert.That(check.IsEffectivelyEnabled, Is.False);
        f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Local)).Values[AppVeyorSettings.LoadTestsResultsKey], Is.EqualTo("true"));
        foreach (GitDistributedSettingsScope scope in new[] { GitDistributedSettingsScope.Global, GitDistributedSettingsScope.Distributed })
        { Assert.That(File.ReadAllBytes(f.Profile.Path(scope)), Is.EqualTo(before[f.Profile.Path(scope)])); }
        Dictionary<string, byte[]> applied = f.Profile.Material(); f.Select(GitDistributedSettingsScope.Global); f.Edit("AppVeyorProjectName", "discarded/project");
        f.Click("CancelDesktopSettings"); await f.UntilAsync(() => f.Modal.IsCompleted); Assert.That(await f.Modal.WaitAsync(TimeSpan.FromSeconds(5)), Is.True); AssertXmlUnchanged(f, applied);
    }

    [AvaloniaTest]
    public async Task PartialCasFailureAndRetryNeverReplayAcknowledgedScopeOrOverwritePeerDataAndExplicitReopenCanSave()
    {
        await using AppVeyorSettingsUiFixture f = await AppVeyorSettingsUiFixture.CreateAsync(prepare: profile =>
        { foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { AppVeyorSettingsUiFixture.Seed(profile, scope, project: null); } });
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Local); f.Edit("AppVeyorAccountName", "saved-local");
        f.Select(GitDistributedSettingsScope.Global); f.Edit("AppVeyorAccountName", "retry-global");
        string globalPath = f.Profile.Path(GitDistributedSettingsScope.Global); XDocument peer = XDocument.Load(globalPath);
        peer.Root!.Add(new XElement("item", new XElement("key", new XElement("string", "peer.edit")), new XElement("value", new XElement("string", "preserve peer")))); peer.Save(globalPath);
        byte[] peerBytes = File.ReadAllBytes(globalPath); f.Click("ApplyDesktopSettings"); await f.SaveFailedAsync();
        GitXmlSettingsSnapshot local = await f.Profile.ReadAsync(GitDistributedSettingsScope.Local);
        Assert.That(local.Values[AppVeyorSettings.AccountNameKey], Is.EqualTo("saved-local")); Assert.That(local.Values[AppVeyorSettings.ProjectNameKey], Is.EqualTo(f.Session.DefaultProjectName));
        byte[] localSaved = File.ReadAllBytes(local.Path); Assert.That(File.ReadAllBytes(globalPath), Is.EqualTo(peerBytes));
        f.Click("ApplyDesktopSettings"); await f.SaveFailedAsync(); Assert.That(File.ReadAllBytes(local.Path), Is.EqualTo(localSaved)); Assert.That(File.ReadAllBytes(globalPath), Is.EqualTo(peerBytes));
        await f.ReopenAsync(); f.Select(GitDistributedSettingsScope.Global); f.Edit("AppVeyorAccountName", "retry-global"); f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        GitXmlSettingsSnapshot global = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global);
        Assert.That(global.Values["peer.edit"], Is.EqualTo("preserve peer")); Assert.That(global.Values[AppVeyorSettings.AccountNameKey], Is.EqualTo("retry-global"));
        Assert.That(File.ReadAllBytes(local.Path), Is.EqualTo(localSaved));
    }

    [AvaloniaTest]
    public async Task FirstClicksTabAndEnterKeepNativeEditorFocusWithoutInvokingDefaultSettingsOk()
    {
        await using AppVeyorSettingsUiFixture f = await AppVeyorSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); f.Click("AppVeyorAccountName"); Assert.That(f.Field<TextBox>("AppVeyorAccountName").IsKeyboardFocusWithin, Is.True);
        foreach (string name in new[] { "AppVeyorAccountName", "AppVeyorAccountToken", "AppVeyorProjectName" })
        {
            Assert.That(f.Field<TextBox>(name).IsKeyboardFocusWithin, Is.True, name); f.Press(PhysicalKey.Enter);
            Assert.That(f.Dialog.IsVisible, Is.True, "Enter inside a single-line editor must not accept Settings."); Assert.That(f.Field<TextBox>(name).IsKeyboardFocusWithin, Is.True); f.Press(PhysicalKey.Tab);
        }
        Assert.That(f.Field<CheckBox>("cbLoadTestResults").IsKeyboardFocusWithin, Is.True); f.Press(PhysicalKey.Enter);
        Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Field<CheckBox>("cbLoadTestResults").IsKeyboardFocusWithin, Is.True);
        f.Profile.AssertMaterial(before); Assert.That(f.OpenedUrls, Is.Empty);
    }

    private static void AssertXmlUnchanged(AppVeyorSettingsUiFixture f, IReadOnlyDictionary<string, byte[]> before)
    { foreach (string path in f.Profile.XmlPaths) { Assert.That(File.ReadAllBytes(path), Is.EqualTo(before[path]), path); } }
    private static void AssertOtherProviderValues(GitXmlSettingsSnapshot saved)
    {
        Assert.That(saved.Values[GitHubActionsSettings.OwnerKey], Is.EqualTo("untouched-owner")); Assert.That(saved.Values[GitHubActionsSettings.TokenKey], Is.EqualTo("untouched-gha-token"));
        Assert.That(saved.Values[TeamCitySettings.ProjectNameKey], Is.EqualTo("untouched-project")); Assert.That(saved.Values[TeamCitySettings.BuildIdFilterKey], Is.EqualTo("untouched.*"));
        Assert.That(saved.Values[GitLabSettings.ProjectIdKey], Is.EqualTo("71")); Assert.That(saved.Values[GitLabSettings.ApiTokenKey], Is.EqualTo("untouched-gitlab-token"));
    }
    private static byte[] Crop(WriteableBitmap frame, TopLevel window, Control control)
    {
        Point origin = control.TranslatePoint(default, window)!.Value; double scale = window.RenderScaling;
        int x = (int)Math.Ceiling((origin.X + 3) * scale), y = (int)Math.Ceiling((origin.Y + 3) * scale);
        int width = (int)Math.Floor((control.Bounds.Width - 6) * scale), height = (int)Math.Floor((control.Bounds.Height - 6) * scale);
        Assert.That(width, Is.GreaterThan(0)); Assert.That(height, Is.GreaterThan(0)); byte[] bytes = new byte[width * height * 4];
        using var buffer = frame.Lock();
        for (int row = 0; row < height; row++) { Marshal.Copy(IntPtr.Add(buffer.Address, (y + row) * buffer.RowBytes + x * 4), bytes, row * width * 4, width * 4); }
        return bytes;
    }
}

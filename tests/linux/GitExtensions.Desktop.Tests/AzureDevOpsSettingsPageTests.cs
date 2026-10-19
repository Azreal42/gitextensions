using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AzureDevOpsSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalElevenRowTableHasOnlyTheThreeWindowsFieldsTranslatedLinksAndReadablePatHelp(string language, bool dark)
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(language, dark); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); Grid table = f.Field<Grid>("tableLayoutPanel1");
        Assert.That(table.ColumnDefinitions.Count, Is.EqualTo(2)); Assert.That(table.ColumnDefinitions[0].Width, Is.EqualTo(GridLength.Auto));
        Assert.That(table.ColumnDefinitions[1].Width.IsStar, Is.True); Assert.That(table.RowDefinitions.Count, Is.EqualTo(11));
        Assert.That(table.RowDefinitions[3].Height.Value, Is.EqualTo(20)); Assert.That(table.RowDefinitions[7].Height.Value, Is.EqualTo(20));
        TextBox[] inputs = f.Page.GetVisualDescendants().OfType<TextBox>().ToArray(); Assert.That(inputs.Select(box => box.Name), Is.EquivalentTo(new[] { "TfsServer", "TfsBuildDefinitionNameFilter", "RestApiToken" }));
        foreach ((string name, int row) in new[] { ("TfsServer", 1), ("TfsBuildDefinitionNameFilter", 4), ("RestApiToken", 8) })
        {
            TextBox box = f.Field<TextBox>(name); Assert.That(Grid.GetRow(box), Is.EqualTo(row)); Assert.That(Grid.GetColumn(box), Is.EqualTo(1));
            Assert.That(box.Bounds.Height, Is.EqualTo(DesktopFontMetrics.Height(DesktopFonts.For(DesktopFontRole.Application), 23, 6)).Within(.1));
        }
        Assert.That(Grid.GetRow(f.Field<Button>("ExtractLink")), Is.Zero); Assert.That(Grid.GetRow(f.Field<Button>("TokenManagementLink")), Is.EqualTo(9));
        foreach ((string name, string source) in new[]
        {
            ("ExtractLink", "Extract data from a build result url copied in the clipboard"), ("TokenManagementLink", "Go to token management page")
        })
        { Assert.That(f.Field<Button>(name).Content, Is.EqualTo(f.Catalog.Translate("SettingsUserControl", name, "Text", source))); }
        foreach (TextBlock text in f.Page.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text)))
        {
            Assert.That(text.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False, text.Text);
            Assert.That(text.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(text.Bounds.Width + .5), text.Text);
            Rect bounds = new(text.TranslatePoint(default, f.Dialog)!.Value, text.Bounds.Size);
            Assert.That(new Rect(default, f.Dialog.ClientSize).Contains(bounds), Is.True, text.Text);
            foreach (Control ancestor in text.GetVisualAncestors().OfType<Control>().Where(control => control.ClipToBounds))
            { Assert.That(new Rect(default, ancestor.Bounds.Size).Contains(new Rect(text.TranslatePoint(default, ancestor)!.Value, text.Bounds.Size)), Is.True, text.Text); }
        }
        TextBox token = f.Field<TextBox>("RestApiToken"); Assert.That(token.PasswordChar, Is.Not.EqualTo('\0')); Assert.That(token.RevealPassword, Is.False);
        Assert.That(f.Page.GetVisualDescendants().OfType<RadioButton>().Count(control => control.IsEffectivelyVisible), Is.Zero);
        using WriteableBitmap image = await AvatarUiTestDriver.Frame(f.Dialog);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "azuredevops-settings-" + language + (dark ? "-dark.png" : "-light.png")); image.Save(path); TestContext.AddTestAttachment(path);
        Assert.That(f.Clients, Is.Empty); Assert.That(f.OpenedUrls, Is.Empty); f.Profile.AssertMaterial(before);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task RealPatTypingPaintsOnlyMaskGlyphsAndEqualLengthSecretsHaveIdenticalPixels(bool dark)
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(dark: dark); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        TextBox token = f.Field<TextBox>("RestApiToken"); f.Edit("RestApiToken", "AAAA1111"); f.Click("TfsServer");
        using WriteableBitmap first = await AvatarUiTestDriver.Frame(f.Dialog); byte[] pixels = Crop(first, f.Dialog, token);
        f.Edit("RestApiToken", "zzzz9999"); f.Click("TfsServer");
        TextPresenter presenter = token.GetVisualDescendants().OfType<TextPresenter>().Single(control => control.Name == "PART_TextPresenter");
        string painted = string.Concat(presenter.TextLayout.TextLines.SelectMany(line => line.TextRuns).Select(run => run.Text.ToString()));
        Assert.That(painted, Is.EqualTo(new string(token.PasswordChar, 8)));
        using WriteableBitmap second = await AvatarUiTestDriver.Frame(f.Dialog); Assert.That(Crop(second, f.Dialog, token), Is.EqualTo(pixels));
        f.Edit("RestApiToken", ""); f.Click("TfsServer"); using WriteableBitmap empty = await AvatarUiTestDriver.Frame(f.Dialog);
        Assert.That(Crop(empty, f.Dialog, token), Is.Not.EqualTo(pixels), "Equal images must contain mask ink rather than two empty editors.");
        Assert.That(f.Clients, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase("none")] [TestCase("bare")]
    public async Task ScopeAvailabilityAndEffectiveReadonlyUseTheCommonSettingsHost(string kind)
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(kind: kind); await f.ShowAsync();
        if (kind == "none")
        {
            Assert.That(f.Field<RadioButton>("BuildServerIntegrationSettingsPageGlobal").IsChecked, Is.True);
            foreach (string scope in new[] { "Effective", "Local", "Distributed" }) { Assert.That(f.Field<RadioButton>("BuildServerIntegrationSettingsPage" + scope).IsVisible, Is.False); }
            Assert.That(f.Field<TextBox>("TfsServer").IsEffectivelyEnabled, Is.True);
        }
        else
        {
            foreach (string field in new[] { "TfsServer", "TfsBuildDefinitionNameFilter", "RestApiToken" }) { Assert.That(f.Field<TextBox>(field).IsEffectivelyEnabled, Is.False); }
            Assert.That(f.Field<Button>("ExtractLink").IsEnabled, Is.False); Assert.That(f.Field<RadioButton>("BuildServerIntegrationSettingsPageDistributed").IsEnabled, Is.False);
        }
        Assert.That(f.Clients, Is.Empty);
    }

    [AvaloniaTest]
    public async Task ScopeSwitchAndNoOpApplyPreserveRawHiddenAndOtherProviderKeysByteForByte()
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(prepare: profile =>
            AzureDevOpsSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Global, filter: "[future malformed", provider: "Future CI"));
        await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material();
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { f.Select(scope); }
        Assert.That(f.Field<ComboBox>("BuildServerProvider").SelectedItem, Is.EqualTo("Future CI")); Assert.That(f.Page.IsVisible, Is.False);
        Assert.That(f.Session.HasChanges, Is.False); f.Click("ApplyDesktopSettings"); await f.SavedAsync(); AssertXmlUnchanged(f, before); Assert.That(f.Clients, Is.Empty);
    }

    [AvaloniaTest]
    public async Task RemoteSuggestionDoesNotStageOnLoadNoOpApplyOrCancel()
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(prepare: profile =>
        { foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { AzureDevOpsSettingsUiFixture.Seed(profile, scope, project: null, token: null); } });
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global); Dictionary<string, byte[]> before = f.Profile.Material();
        Assert.That(f.Field<TextBox>("TfsServer").Text, Is.EqualTo(AzureDevOpsSettingsUiFixture.Project));
        Assert.That(f.Session.Read(GitDistributedSettingsScope.Global, AzureDevOpsSettings.ProjectUrlKey), Is.Null); Assert.That(f.Session.HasChanges, Is.False);
        f.Click("ApplyDesktopSettings"); await f.SavedAsync(); AssertXmlUnchanged(f, before);
        f.Edit("TfsBuildDefinitionNameFilter", "discarded"); f.Click("CancelDesktopSettings"); await f.UntilAsync(() => f.Modal.IsCompleted); AssertXmlUnchanged(f, before);
    }

    [AvaloniaTest]
    public async Task RealEditsAndProviderActivationSaveTheVisibleDefaultOnlyInEditedScopes()
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(prepare: profile =>
        { foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { AzureDevOpsSettingsUiFixture.Seed(profile, scope, project: null, token: null, provider: scope == GitDistributedSettingsScope.Global ? "Future CI" : AzureDevOpsSettings.ProviderName); } });
        await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material(); f.Select(GitDistributedSettingsScope.Global);
        await f.ProviderAsync(AzureDevOpsSettings.ProviderName); f.Select(GitDistributedSettingsScope.Local); f.Edit("TfsBuildDefinitionNameFilter", "local-edited");
        f.Select(GitDistributedSettingsScope.Distributed); f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        foreach (GitDistributedSettingsScope scope in new[] { GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Global })
        { GitXmlSettingsSnapshot saved = await f.Profile.ReadAsync(scope); Assert.That(saved.Values[AzureDevOpsSettings.ProjectUrlKey], Is.EqualTo(AzureDevOpsSettingsUiFixture.Project)); AssertUntouched(saved); }
        Assert.That(File.ReadAllBytes(f.Profile.Path(GitDistributedSettingsScope.Distributed)), Is.EqualTo(before[f.Profile.Path(GitDistributedSettingsScope.Distributed)]));
    }

    [AvaloniaTest]
    public async Task ActualApplyReopenAndCancelRetainScopedDraftsWithoutRewritingHiddenRepositoryName()
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        f.Edit("TfsBuildDefinitionNameFilter", "global-kept"); f.Select(GitDistributedSettingsScope.Local); f.Edit("RestApiToken", "new-local-token");
        f.Select(GitDistributedSettingsScope.Distributed); f.Edit("TfsBuildDefinitionNameFilter", "distributed-kept"); f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { AssertUntouched(await f.Profile.ReadAsync(scope)); }
        await f.ReopenAsync(); f.Select(GitDistributedSettingsScope.Global); Assert.That(f.Field<TextBox>("TfsBuildDefinitionNameFilter").Text, Is.EqualTo("global-kept"));
        Dictionary<string, byte[]> before = f.Profile.Material(); f.Edit("TfsBuildDefinitionNameFilter", "discarded"); f.Click("CancelDesktopSettings"); await f.UntilAsync(() => f.Modal.IsCompleted); AssertXmlUnchanged(f, before);
        await f.ReopenAsync(); f.Select(GitDistributedSettingsScope.Local); Assert.That(f.Field<TextBox>("RestApiToken").Text, Is.EqualTo("new-local-token"));
    }

    [AvaloniaTest]
    public async Task PartialSaveRetryDoesNotReplayAnAcknowledgedScopeOrSilentlyRebaseAConflictingFile()
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync();
        f.Select(GitDistributedSettingsScope.Local); f.Edit("TfsBuildDefinitionNameFilter", "saved-local");
        f.Select(GitDistributedSettingsScope.Global); f.Edit("TfsBuildDefinitionNameFilter", "pending-global");
        GitXmlSettingsSnapshot global = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global);
        await GitXmlSettingsStore.SaveChangesAsync(global, new Dictionary<string, string?> { ["peer.edit"] = "preserve peer" }, default);
        byte[] peer = File.ReadAllBytes(global.Path); f.Click("ApplyDesktopSettings"); await f.SaveFailedAsync();
        GitXmlSettingsSnapshot local = await f.Profile.ReadAsync(GitDistributedSettingsScope.Local);
        Assert.That(local.Values[AzureDevOpsSettings.BuildDefinitionFilterKey], Is.EqualTo("saved-local"));
        await GitXmlSettingsStore.SaveChangesAsync(local, new Dictionary<string, string?> { [AzureDevOpsSettings.BuildDefinitionFilterKey] = "new-peer-local" }, default);
        byte[] peerLocal = File.ReadAllBytes(local.Path); f.Click("ApplyDesktopSettings"); await f.SaveFailedAsync();
        Assert.That(File.ReadAllBytes(local.Path), Is.EqualTo(peerLocal)); Assert.That(File.ReadAllBytes(global.Path), Is.EqualTo(peer));
        await f.ReopenAsync(); f.Select(GitDistributedSettingsScope.Global); f.Edit("TfsBuildDefinitionNameFilter", "pending-global"); f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values["peer.edit"], Is.EqualTo("preserve peer"));
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Local)).Values[AzureDevOpsSettings.BuildDefinitionFilterKey], Is.EqualTo("new-peer-local"));
    }

    [AvaloniaTest]
    [TestCase("https://other.example.invalid/acme/ProjectA")]
    [TestCase("https://dev.azure.com/acme/ProjectB")]
    [TestCase("https://dev.azure.com/other-collection/ProjectA")]
    public async Task SameScopeEndpointChangeCannotSaveAnOldPatUntilTheUserActuallyReentersIt(string target)
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); f.Edit("TfsServer", target); f.Click("ApplyDesktopSettings"); await f.SaveFailedAsync();
        Assert.That(f.Field<TextBlock>("SettingsNotice").Text, Does.Contain("PAT")); AssertXmlUnchanged(f, before);
        f.Edit("RestApiToken", "fake-token"); f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        GitXmlSettingsSnapshot saved = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global); Assert.That(saved.Values[AzureDevOpsSettings.ProjectUrlKey], Is.EqualTo(target));
        Assert.That(saved.Values[AzureDevOpsSettings.ApiTokenKey], Is.EqualTo("fake-token")); AssertUntouched(saved); Assert.That(f.Clients, Is.Empty);
    }

    [AvaloniaTest]
    public async Task InheritedPatCannotBeRetargetedByALocalUrlOverride()
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(prepare: profile =>
        {
            AzureDevOpsSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Local, token: null, enabled: "true");
            AzureDevOpsSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Distributed, token: null);
        });
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Local); Dictionary<string, byte[]> before = f.Profile.Material();
        f.Edit("TfsServer", "https://dev.azure.com/acme/Other"); f.Click("ApplyDesktopSettings"); await f.SaveFailedAsync(); AssertXmlUnchanged(f, before);
        f.Edit("RestApiToken", "local-new-token"); f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        GitXmlSettingsSnapshot saved = await f.Profile.ReadAsync(GitDistributedSettingsScope.Local); Assert.That(saved.Values[AzureDevOpsSettings.ApiTokenKey], Is.EqualTo("local-new-token"));
    }

    [AvaloniaTest]
    [TestCase("*")] [TestCase("[")]
    public async Task RealRegexErrorIsVisibleAndRejectedEvenAfterLeavingTheEditedScope(string invalid)
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); f.Edit("TfsBuildDefinitionNameFilter", invalid); Assert.That(f.Field<TextBlock>("labelRegexError").IsVisible, Is.True);
        f.Select(GitDistributedSettingsScope.Local); f.Click("ApplyDesktopSettings"); await f.SaveFailedAsync(); AssertXmlUnchanged(f, before);
        f.Select(GitDistributedSettingsScope.Global); f.Edit("TfsBuildDefinitionNameFilter", "Build*"); Assert.That(f.Field<TextBlock>("labelRegexError").IsVisible, Is.False);
        f.Click("ApplyDesktopSettings"); await f.SavedAsync(); Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values[AzureDevOpsSettings.BuildDefinitionFilterKey], Is.EqualTo("Build*"));
    }

    [AvaloniaTest]
    public async Task FirstClickTypingTabAndEnterStayInTheEditorsWithoutAcceptingSettings()
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); f.Edit("TfsServer", AzureDevOpsSettingsUiFixture.Project + "-typed");
        Assert.That(f.Field<TextBox>("TfsServer").IsKeyboardFocusWithin, Is.True); f.Press(PhysicalKey.Enter); Assert.That(f.Dialog.IsVisible, Is.True);
        f.Press(PhysicalKey.Tab); Assert.That(f.Field<TextBox>("TfsBuildDefinitionNameFilter").IsKeyboardFocusWithin, Is.True);
        f.Press(PhysicalKey.Enter); f.Press(PhysicalKey.Tab); Assert.That(f.Field<TextBox>("RestApiToken").IsKeyboardFocusWithin, Is.True);
        f.Press(PhysicalKey.Enter); Assert.That(f.Dialog.IsVisible, Is.True); AssertXmlUnchanged(f, before); Assert.That(f.Clients, Is.Empty); Assert.That(f.OpenedUrls, Is.Empty);
    }

    [AvaloniaTest]
    public async Task TokenManagementIsAnExplicitKeyboardActionAndInvalidProjectDisablesIt()
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Assert.That(f.OpenedUrls, Is.Empty); f.Click("RestApiToken"); f.Press(PhysicalKey.Tab);
        Assert.That(f.Field<Button>("TokenManagementLink").IsKeyboardFocusWithin, Is.True); f.Press(PhysicalKey.Enter);
        await f.IdleAsync();
        Assert.That(f.OpenedUrls.Single(), Is.EqualTo("https://dev.azure.com/acme/_details/security/tokens"));
        f.Edit("TfsServer", "https://user:secret@example.invalid/project"); Assert.That(f.Field<Button>("TokenManagementLink").IsEnabled, Is.False);
        Assert.That(f.Clients, Is.Empty); Assert.That(f.OpenedUrls.Count, Is.EqualTo(1));
    }

    [AvaloniaTest]
    [TestCase("https://dev.azure.com/acme/ProjectA", true)]
    [TestCase("https://dev.azure.com/acme/project", false)]
    public async Task ClipboardAuthorizationExpandsTheActualFetchRepositoryVariablesInsteadOfTheSyntaxValidatorPlaceholder(string target, bool permitted)
    {
        const string template = "https://dev.azure.com/acme/{cRepoProject}";
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(
            remote: "https://host/ProjectA/repository.git", prepare: profile =>
                AzureDevOpsSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Global, project: template));
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global); Dictionary<string, byte[]> before = f.Profile.Material();
        Assert.That(f.Session.ExpandBuildServerVariables("{cRepoProject}"), Is.EqualTo("ProjectA"));
        Assert.That(f.Field<TextBox>("TfsServer").Text, Is.EqualTo(template)); f.ClipboardText = target + "/_build/results?buildId=42";
        f.Click("ExtractLink");
        if (permitted)
        {
            await f.IdleAsync(); Assert.That(f.Clients.Single().Settings.ProjectUrl, Is.EqualTo(target));
            Assert.That(f.Clients.Single().Settings.ApiToken, Is.EqualTo("fake-token"));
            Assert.That(f.Clients.Single().Requests.Single().Id, Is.EqualTo(42));
            Assert.That(f.Field<TextBox>("TfsBuildDefinitionNameFilter").Text, Is.EqualTo("resolved-pipeline"));
        }
        else
        {
            await f.AcknowledgeAsync(AzureDevOpsSettingsPage.TokenProjectMismatch); await f.IdleAsync();
            Assert.That(f.Clients, Is.Empty, "A literal project must never receive the PAT bound to repository ProjectA.");
            Assert.That(f.Field<TextBox>("TfsServer").Text, Is.EqualTo(target)); Assert.That(f.Field<TextBox>("TfsBuildDefinitionNameFilter").Text, Is.Empty);
            f.Click("ApplyDesktopSettings"); await f.SaveFailedAsync();
        }
        AssertXmlUnchanged(f, before); Assert.That(f.OpenedUrls, Is.Empty);
    }

    [AvaloniaTest]
    public async Task UnresolvedProjectTemplateWithoutARepositoryNeverAuthorizesTheLiteralProjectOrConstructsAClient()
    {
        const string template = "https://dev.azure.com/acme/{cRepoProject}";
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(kind: "none", prepare: profile =>
            AzureDevOpsSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Global, project: template));
        await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material();
        Assert.That(f.Session.ExpandBuildServerVariables(template), Is.EqualTo(template));
        f.ClipboardText = "https://dev.azure.com/acme/project/_build/results?buildId=42"; f.Click("ExtractLink");
        await f.AcknowledgeAsync(AzureDevOpsSettingsPage.TokenProjectMismatch); await f.IdleAsync();
        Assert.That(f.Clients, Is.Empty); Assert.That(f.OpenedUrls, Is.Empty); f.Click("ApplyDesktopSettings"); await f.SaveFailedAsync(); AssertXmlUnchanged(f, before);
    }

    [AvaloniaTest]
    [TestCase("io")] [TestCase("win32")] [TestCase("operation")]
    public async Task ActualTokenLinkKeyboardActivationOwnsBrowserFailuresAndOneFixedNoticeWithoutChangingXml(string failure)
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); f.LaunchBrowser = _ =>
        {
            Exception exception = failure switch
            { "io" => new IOException("secret-file-path"), "win32" => new System.ComponentModel.Win32Exception("secret-command-line"), _ => new InvalidOperationException("secret-payload") };
            throw exception;
        };
        f.Click("RestApiToken"); f.Press(PhysicalKey.Tab); Assert.That(f.Field<Button>("TokenManagementLink").IsKeyboardFocusWithin, Is.True); f.Press(PhysicalKey.Enter);
        await f.UntilAsync(() => f.Dialog.OwnedWindows.Any(window => window.IsVisible));
        Assert.That(f.Page.IsBusy, Is.True); Assert.That(f.Field<Button>("ApplyDesktopSettings").IsEnabled, Is.False);
        Assert.That(f.Dialog.OwnedWindows.Count(window => window.IsVisible), Is.EqualTo(1));
        await f.AcknowledgeAsync(AzureDevOpsSettingsPage.BrowserFailure, f.Catalog.Translate("Error")); await f.IdleAsync();
        Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Field<Button>("ApplyDesktopSettings").IsEnabled, Is.True);
        Assert.That(f.OpenedUrls.Count, Is.EqualTo(1)); Assert.That(f.Clients, Is.Empty); AssertXmlUnchanged(f, before);
    }

    [AvaloniaTest]
    [TestCase("scope")] [TestCase("provider")] [TestCase("close")]
    public async Task BrowserErrorNoticeIsCancelledAndDrainedWhenItsCapturedPageContextChanges(string change)
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); f.LaunchBrowser = _ => throw new IOException("not displayed");
        f.Click("TokenManagementLink"); await f.UntilAsync(() => f.Dialog.OwnedWindows.Any(window => window.IsVisible));
        Window notice = f.Dialog.OwnedWindows.Single(window => window.IsVisible);
        if (change == "scope") { f.ProviderPage.SetScope(GitDistributedSettingsScope.Local); }
        else if (change == "provider")
        {
            // The modal owns input. Drive the real host's model-refresh lifecycle rather than fake a pointer through a modal.
            f.Session.Set(GitDistributedSettingsScope.Global, AzureDevOpsSettings.ProviderKey, "GitHub Actions"); f.ProviderPage.ShowLoadState();
        }
        else { f.Dialog.Close(); }
        await f.UntilAsync(() => !notice.IsVisible); await f.Page.Completion.WaitAsync(TimeSpan.FromSeconds(5)); await f.IdleAsync();
        Assert.That(f.Dialog.OwnedWindows.Any(window => window.IsVisible), Is.False); Assert.That(f.OpenedUrls.Count, Is.EqualTo(1));
        if (change == "close") { await f.UntilAsync(() => !f.Dialog.IsVisible); }
        AssertXmlUnchanged(f, before);
    }

    [AvaloniaTest]
    [TestCase("https://dev.azure.com/acme/ProjectA/_build/results")]
    [TestCase("not a build url")]
    public async Task InvalidClipboardShowsTheOriginalErrorWithoutHttpOrDraftMutation(string clipboard)
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); f.ClipboardText = clipboard; f.Click("ExtractLink");
        await f.AcknowledgeAsync(AzureDevOpsSettingsPage.ClipboardFailure); await f.IdleAsync();
        Assert.That(f.Session.HasChanges, Is.False); Assert.That(f.Clients, Is.Empty); AssertXmlUnchanged(f, before);
    }

    [AvaloniaTest]
    public async Task ClipboardWithoutPatFillsProjectAndEmptyFilterAfterTheOriginalWarningWithoutAnyHttp()
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(prepare: profile =>
            AzureDevOpsSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Global, token: null));
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global); f.ClipboardText = "https://dev.azure.com/other/project/_build/results?buildId=77";
        f.Click("ExtractLink"); await f.AcknowledgeAsync(AzureDevOpsSettingsPage.NoTokenInformation); await f.IdleAsync();
        Assert.That(f.Clients, Is.Empty); Assert.That(f.Field<TextBox>("TfsServer").Text, Is.EqualTo("https://dev.azure.com/other/project"));
        Assert.That(f.Field<TextBox>("TfsBuildDefinitionNameFilter").Text, Is.Empty); Assert.That(f.Field<TextBox>("RestApiToken").Text, Is.Empty);
        f.Click("ApplyDesktopSettings"); await f.SavedAsync(); AssertUntouched(await f.Profile.ReadAsync(GitDistributedSettingsScope.Global));
    }

    [AvaloniaTest]
    public async Task ExplicitSameProjectExtractionGetsTheExactBuildIdAndPreservesPatAndHiddenRepository()
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Assert.That(f.Clients, Is.Empty); f.Click("ExtractLink"); await f.IdleAsync();
        AzureDevOpsPageTestClient client = f.Clients.Single(); Assert.That(client.Requests.Single().Id, Is.EqualTo(42));
        Assert.That(client.Settings.ProjectUrl, Is.EqualTo(AzureDevOpsSettingsUiFixture.Project)); Assert.That(client.Settings.ApiToken, Is.EqualTo("fake-token"));
        Assert.That(client.RuntimeCalls, Is.Zero); Assert.That(client.Disposed, Is.True); Assert.That(f.Field<TextBox>("TfsBuildDefinitionNameFilter").Text, Is.EqualTo("resolved-pipeline"));
        f.Click("ApplyDesktopSettings"); await f.SavedAsync(); AssertUntouched(await f.Profile.ReadAsync(GitDistributedSettingsScope.Global));
    }

    [AvaloniaTest]
    public async Task LookupFailureShowsOnlyTheOriginalFixedErrorAndRetainsTheDraft()
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); f.Lookup = (_, _, _) => throw new AzureDevOpsException("do not display secret-payload");
        f.Click("ExtractLink"); await f.AcknowledgeAsync(AzureDevOpsSettingsPage.LookupFailure); await f.IdleAsync();
        Assert.That(f.Session.HasChanges, Is.False); Assert.That(f.Clients.Single().Disposed, Is.True); Assert.That(f.Field<TextBox>("TfsBuildDefinitionNameFilter").Text, Is.EqualTo("pipeline.*")); AssertXmlUnchanged(f, before);
    }

    [AvaloniaTest]
    [TestCase("https://other.example.invalid/acme/ProjectA")]
    [TestCase("https://dev.azure.com/acme/Other")]
    [TestCase("https://dev.azure.com/other-base/ProjectA")]
    public async Task ClipboardRetargetFillsOnlyUrlAndFilterButCannotSendOrSaveTheOldPat(string target)
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); f.ClipboardText = target + "/_build/results?buildId=99";
        f.Click("ExtractLink"); await f.AcknowledgeAsync(AzureDevOpsSettingsPage.TokenProjectMismatch); await f.IdleAsync();
        Assert.That(f.Clients, Is.Empty); Assert.That(f.Field<TextBox>("TfsServer").Text, Is.EqualTo(target)); Assert.That(f.Field<TextBox>("TfsBuildDefinitionNameFilter").Text, Is.Empty);
        Assert.That(f.Field<TextBox>("RestApiToken").Text, Is.EqualTo("fake-token")); f.Click("ApplyDesktopSettings"); await f.SaveFailedAsync(); AssertXmlUnchanged(f, before);
        f.Edit("RestApiToken", "fake-token"); f.Click("ExtractLink"); await f.IdleAsync(); Assert.That(f.Clients.Single().Requests.Single().Id, Is.EqualTo(99));
        f.Click("ApplyDesktopSettings"); await f.SavedAsync(); Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values[AzureDevOpsSettings.ProjectUrlKey], Is.EqualTo(target));
    }

    [AvaloniaTest]
    [TestCase("edit")] [TestCase("scope")] [TestCase("provider")]
    public async Task LateHttpReplyAfterEditScopeOrProviderChangeCannotOverwriteAnyDraft(string change)
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        TaskCompletionSource<string?> read = f.Gate<string?>("cleanup"); f.Lookup = (_, _, _) => read.Task;
        f.Click("ExtractLink"); await f.UntilAsync(() => f.Clients.Count == 1); AzureDevOpsPageTestClient client = f.Clients.Single();
        if (change == "edit") { f.Edit("TfsBuildDefinitionNameFilter", "keep-typed"); }
        else if (change == "scope") { f.Select(GitDistributedSettingsScope.Local); }
        else { await f.ProviderAsync("GitHub Actions"); }
        Assert.That(client.Requests.Single().Token.IsCancellationRequested, Is.True); Assert.That(f.Field<Button>("ApplyDesktopSettings").IsEnabled, Is.False);
        read.SetResult("late-must-not-publish"); await f.IdleAsync(); Assert.That(client.Disposed, Is.True);
        Assert.That(f.Session.Read(GitDistributedSettingsScope.Global, AzureDevOpsSettings.BuildDefinitionFilterKey), Is.EqualTo(change == "edit" ? "keep-typed" : "pipeline.*"));
        Assert.That(f.Dialog.OwnedWindows.Any(window => window.IsVisible), Is.False); Assert.That(f.OpenedUrls, Is.Empty);
    }

    [AvaloniaTest]
    public async Task SecondExplicitRequestWinsWhileTheFirstUncooperativeRequestStillDrains()
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        TaskCompletionSource<string?> first = f.Gate<string?>("cleanup-one"), second = f.Gate<string?>("cleanup-two"); int call = 0;
        f.Lookup = (_, _, _) => ++call == 1 ? first.Task : second.Task;
        f.Click("ExtractLink"); await f.UntilAsync(() => f.Clients.Count == 1); f.Click("ExtractLink"); await f.UntilAsync(() => f.Clients.Count == 2);
        second.SetResult("second-result"); await f.UntilAsync(() => f.Clients[1].Disposed); Assert.That(f.Field<TextBox>("TfsBuildDefinitionNameFilter").Text, Is.EqualTo("second-result"));
        Assert.That(f.Page.IsBusy, Is.True); Assert.That(f.Field<Button>("ApplyDesktopSettings").IsEnabled, Is.False);
        first.SetResult("obsolete-first-result"); await f.IdleAsync(); Assert.That(f.Field<TextBox>("TfsBuildDefinitionNameFilter").Text, Is.EqualTo("second-result"));
    }

    [AvaloniaTest]
    public async Task OwnerCloseWaitsForTheRawLookupAndAsyncClientDisposalWithoutLatePublication()
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); TaskCompletionSource<string?> read = f.Gate<string?>("cleanup"); TaskCompletionSource<bool> disposal = f.Gate(true);
        f.Lookup = (_, _, _) => read.Task; f.DisposeClient = async () => { await disposal.Task; };
        f.Click("ExtractLink"); await f.UntilAsync(() => f.Clients.Count == 1); f.Owner.Close();
        await f.UntilAsync(() => f.Clients.Single().Requests.Single().Token.IsCancellationRequested); Assert.That(f.Dialog.IsVisible, Is.True);
        read.SetResult("late"); await f.UntilAsync(() => read.Task.IsCompleted); f.Pump(); Assert.That(f.Page.IsBusy, Is.True); Assert.That(f.Clients.Single().Disposed, Is.False);
        disposal.SetResult(true); await f.UntilAsync(() => !f.Dialog.IsVisible && !f.Owner.IsVisible); await f.Page.Completion.WaitAsync(TimeSpan.FromSeconds(5)); AssertXmlUnchanged(f, before);
    }

    [AvaloniaTest]
    public async Task ClosingDuringAnUncooperativeClipboardReadDrainsItAndNeverCreatesAClient()
    {
        await using AzureDevOpsSettingsUiFixture f = await AzureDevOpsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); TaskCompletionSource<string?> clipboard = f.Gate<string?>(AzureDevOpsSettingsUiFixture.Build); CancellationToken captured = default;
        f.ReadClipboard = token => { captured = token; return clipboard.Task; }; f.Click("ExtractLink"); await f.UntilAsync(() => f.Page.IsBusy);
        f.Dialog.Close(); await f.UntilAsync(() => captured.IsCancellationRequested); Assert.That(f.Dialog.IsVisible, Is.True);
        clipboard.SetResult(AzureDevOpsSettingsUiFixture.Build); await f.UntilAsync(() => !f.Dialog.IsVisible); Assert.That(f.Clients, Is.Empty); AssertXmlUnchanged(f, before);
    }

    private static void AssertXmlUnchanged(AzureDevOpsSettingsUiFixture f, IReadOnlyDictionary<string, byte[]> before)
    { foreach (string path in f.Profile.XmlPaths) { Assert.That(File.ReadAllBytes(path), Is.EqualTo(before[path]), path); } }
    private static void AssertUntouched(GitXmlSettingsSnapshot saved)
    {
        Assert.That(saved.Values[AzureDevOpsSettings.RepositoryNameKey], Is.EqualTo("hidden-repository-name"));
        Assert.That(saved.Values["future.λ"], Is.EqualTo("keep <literal> & \nbody")); Assert.That(saved.Values[AppVeyorSettings.AccountNameKey], Is.EqualTo("untouched-account"));
        Assert.That(saved.Values[GitLabSettings.ApiTokenKey], Is.EqualTo("untouched-gitlab-token"));
    }
    private static byte[] Crop(WriteableBitmap image, Window window, Control control)
    {
        using var buffer = image.Lock(); Point p = control.TranslatePoint(default, window)!.Value; double scale = image.PixelSize.Width / window.ClientSize.Width;
        int x = (int)Math.Floor(p.X * scale), y = (int)Math.Floor(p.Y * scale), width = (int)Math.Ceiling(control.Bounds.Width * scale), height = (int)Math.Ceiling(control.Bounds.Height * scale);
        byte[] pixels = new byte[width * height * 4]; for (int row = 0; row < height; row++) { Marshal.Copy(buffer.Address + (y + row) * buffer.RowBytes + x * 4, pixels, row * width * 4, width * 4); }
        return pixels;
    }
}

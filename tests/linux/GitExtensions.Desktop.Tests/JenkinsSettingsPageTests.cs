using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using GitUIPluginInterfaces.BuildServerIntegration;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class JenkinsSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task ExactWindowsThreeRowTableHasTranslatedLabelsAlignedEditorsAndNoInventedControls(string language, bool dark)
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(language, dark); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); Grid table = f.Page.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "tableLayoutPanel1");
        Assert.That(table.ColumnDefinitions.Count, Is.EqualTo(2)); Assert.That(table.ColumnDefinitions[0].Width.IsAuto, Is.True);
        Assert.That(table.RowDefinitions.Count, Is.EqualTo(3)); Assert.That(table.RowDefinitions.All(row => row.Height.IsAuto), Is.True);
        Assert.That(table.Children.Count, Is.EqualTo(6)); Assert.That(f.Page.GetVisualDescendants().OfType<TextBox>().Count(), Is.EqualTo(3));
        Assert.That(f.Page.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible && button is not ToggleButton), Is.Empty);
        Assert.That(f.Page.GetVisualDescendants().OfType<RadioButton>().Any(button => button.IsEffectivelyVisible), Is.False, "The host owns the shared scope header.");
        TranslationCatalog text = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
        double? editorX = null;
        foreach ((string name, string labelName, string caption, int row, int tab) in new[]
        {
            ("JenkinsServerUrl", "lblJenkinsServerUrl", "Jenkins server URL", 0, 1),
            ("JenkinsProjectName", "lblProjectName", "Project name", 1, 3),
            ("IgnoreBuildBranch", "lblIgnoreBuildBranch", "Ignore build for branch", 2, 5)
        })
        {
            TextBox editor = f.Field<TextBox>(name); TextBlock label = f.Field<TextBlock>(labelName);
            Assert.That(label.Text, Is.EqualTo(text.Translate("JenkinsSettingsUserControl", labelName, "Text", caption)));
            Assert.That(Grid.GetRow(editor), Is.EqualTo(row)); Assert.That(Grid.GetColumn(editor), Is.EqualTo(1)); Assert.That(editor.TabIndex, Is.EqualTo(tab));
            Assert.That(editor.AcceptsReturn || editor.AcceptsTab, Is.False); Assert.That(editor.PasswordChar, Is.EqualTo('\0'));
            double x = editor.TranslatePoint(default, table)!.Value.X; editorX ??= x; Assert.That(x, Is.EqualTo(editorX.Value));
            Assert.That(label.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False);
            Assert.That(label.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(label.Bounds.Width + .5));
            foreach (Control control in new Control[] { editor, label })
            { Assert.That(new Rect(default, f.Dialog.ClientSize).Contains(new Rect(control.TranslatePoint(default, f.Dialog)!.Value, control.Bounds.Size)), Is.True, name); }
        }
        Assert.That(table.Bounds.Height, Is.LessThanOrEqualTo(110), "Three compact Windows rows should not become a form with oversized spacing.");
        using WriteableBitmap image = await AvatarUiTestDriver.Frame(f.Dialog);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "jenkins-settings-" + language + (dark ? "-dark.png" : "-light.png")); image.Save(path); TestContext.AddTestAttachment(path);
        f.Profile.AssertMaterial(before); Assert.That(f.OpenedUrls, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase("none")] [TestCase("bare")] [TestCase("worktree")]
    public async Task ScopeAvailabilityAndEffectiveReadOnlyReuseTheCommonControls(string kind)
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(kind: kind); await f.ShowAsync();
        if (kind == "none")
        {
            Assert.That(f.Field<RadioButton>("BuildServerIntegrationSettingsPageGlobal").IsChecked, Is.True);
            foreach (string scope in new[] { "Effective", "Local", "Distributed" }) { Assert.That(f.Field<RadioButton>("BuildServerIntegrationSettingsPage" + scope).IsVisible, Is.False); }
            Assert.That(f.Field<TextBox>("JenkinsServerUrl").IsEffectivelyEnabled, Is.True);
        }
        else
        {
            foreach (string name in Editors) { Assert.That(f.Field<TextBox>(name).IsEffectivelyEnabled, Is.False); }
            Assert.That(f.Field<RadioButton>("BuildServerIntegrationSettingsPageDistributed").IsEnabled, Is.EqualTo(kind != "bare"));
            f.Select(GitDistributedSettingsScope.Global); foreach (string name in Editors) { Assert.That(f.Field<TextBox>(name).IsEffectivelyEnabled, Is.True); }
        }
    }

    [AvaloniaTest]
    [TestCase(null)] [TestCase("future-bool")] [TestCase("true")] [TestCase("false")]
    public async Task NoOpScopeSwitchAndApplyPreserveUnknownRawBooleansAndProviderExtensions(string? enabled)
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(prepare: p =>
        { foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { JenkinsSettingsUiFixture.Seed(p, scope, enabled: enabled); } });
        await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material();
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { f.Select(scope); }
        Assert.That(f.Session.HasChanges, Is.False); f.Click("ApplyDesktopSettings"); await f.SavedAsync(); AssertXmlUnchanged(f, before);
        // A no-op must retain future booleans and every provider's unknown schema byte for byte.
        Assert.That(f.Session.Read(GitDistributedSettingsScope.Global, JenkinsSettings.EnableKey), Is.EqualTo(enabled));
    }

    [AvaloniaTest]
    public async Task SuggestionIsNotRuntimeConfigurationAndNoOpApplyOrCancelNeverWritesIt()
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(prepare: p =>
        { foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { JenkinsSettingsUiFixture.Seed(p, scope, project: null); } });
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global); Dictionary<string, byte[]> before = f.Profile.Material();
        Assert.That(f.Field<TextBox>("JenkinsProjectName").Text, Is.EqualTo(f.Session.DefaultProjectName)); Assert.That(f.Session.Jenkins().ProjectName, Is.Empty);
        Assert.That(f.Session.HasChanges, Is.False); f.Click("ApplyDesktopSettings"); await f.SavedAsync(); AssertXmlUnchanged(f, before);
        f.Edit("JenkinsServerUrl", "host-without-scheme"); f.Click("CancelDesktopSettings"); await f.UntilAsync(() => f.Modal.IsCompleted); AssertXmlUnchanged(f, before);
    }

    [AvaloniaTest]
    public async Task RealEditsAcceptFolderDefaultForEveryEditedScopeButLeaveUneditedScopeBytesUntouched()
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(prepare: p =>
        { foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { JenkinsSettingsUiFixture.Seed(p, scope, project: null); } });
        await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material();
        f.Select(GitDistributedSettingsScope.Global); f.Edit("JenkinsServerUrl", "server-without-scheme/base");
        f.Select(GitDistributedSettingsScope.Local); f.Edit("IgnoreBuildBranch", "^release/"); f.Select(GitDistributedSettingsScope.Distributed);
        f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        foreach (GitDistributedSettingsScope scope in new[] { GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Global })
        {
            GitXmlSettingsSnapshot saved = await f.Profile.ReadAsync(scope); Assert.That(saved.Values[JenkinsSettings.ProjectNameKey], Is.EqualTo(f.Session.DefaultProjectName)); AssertOtherValues(saved);
        }
        Assert.That(File.ReadAllBytes(f.Profile.Path(GitDistributedSettingsScope.Distributed)), Is.EqualTo(before[f.Profile.Path(GitDistributedSettingsScope.Distributed)]));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ActivationAcceptsSuggestionOnlyForTheProviderStillSelectedAtApply(bool leaveProvider)
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(prepare: p => JenkinsSettingsUiFixture.Seed(p, GitDistributedSettingsScope.Global, project: null, provider: "Future CI"));
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global); Dictionary<string, byte[]> before = f.Profile.Material();
        Assert.That(f.Page.IsVisible, Is.False); await f.ProviderAsync(JenkinsSettings.ProviderName); Assert.That(f.Page.IsVisible, Is.True);
        Assert.That(f.Session.Read(GitDistributedSettingsScope.Global, JenkinsSettings.ProjectNameKey), Is.Null); f.Profile.AssertMaterial(before);
        if (leaveProvider) { await f.ProviderAsync("Future CI"); }
        f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        if (leaveProvider) { AssertXmlUnchanged(f, before); }
        else { Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values[JenkinsSettings.ProjectNameKey], Is.EqualTo(f.Session.DefaultProjectName)); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ExplicitlyClearedProjectAndIgnoreStayUnsetAcrossApplyAndScopeChanges(bool existingProject)
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(prepare: p => JenkinsSettingsUiFixture.Seed(p, GitDistributedSettingsScope.Global, project: existingProject ? "old-job" : null));
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global); f.Edit("JenkinsProjectName", ""); f.Edit("IgnoreBuildBranch", ""); f.Edit("JenkinsServerUrl", "new-host");
        f.Click("ApplyDesktopSettings"); await f.SavedAsync(); GitXmlSettingsSnapshot saved = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global);
        Assert.That(saved.Values.ContainsKey(JenkinsSettings.ProjectNameKey), Is.False); Assert.That(saved.Values.ContainsKey(JenkinsSettings.IgnoreBuildBranchKey), Is.False);
        f.Select(GitDistributedSettingsScope.Local); f.Select(GitDistributedSettingsScope.Global); Assert.That(f.Field<TextBox>("JenkinsProjectName").Text, Is.Empty); Assert.That(f.Field<TextBox>("IgnoreBuildBranch").Text, Is.Empty);
        await f.ReopenAsync(); f.Select(GitDistributedSettingsScope.Global); Assert.That(f.Field<TextBox>("JenkinsProjectName").Text, Is.EqualTo(f.Session.DefaultProjectName)); Assert.That(f.Session.HasChanges, Is.False);
    }

    [AvaloniaTest]
    public async Task EffectiveInheritanceAndChosenScopeApplyLeaveEveryOtherProviderAndFileIntact()
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(prepare: p =>
        {
            JenkinsSettingsUiFixture.Seed(p, GitDistributedSettingsScope.Global, project: "global-job", ignore: "global");
            JenkinsSettingsUiFixture.Seed(p, GitDistributedSettingsScope.Distributed, project: "distributed-job", ignore: "distributed");
            JenkinsSettingsUiFixture.Seed(p, GitDistributedSettingsScope.Local, project: null, ignore: null);
        });
        await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material(); Assert.That(f.Field<TextBox>("JenkinsProjectName").Text, Is.EqualTo("distributed-job"));
        f.Select(GitDistributedSettingsScope.Local); f.Edit("JenkinsProjectName", "folder/new-job|other-job?builds[number]"); f.Edit("IgnoreBuildBranch", "^(release|archive)/");
        f.Select(GitDistributedSettingsScope.Effective); Assert.That(f.Field<TextBox>("JenkinsProjectName").Text, Is.EqualTo("folder/new-job|other-job?builds[number]"));
        f.Click("ApplyDesktopSettings"); await f.SavedAsync(); GitXmlSettingsSnapshot local = await f.Profile.ReadAsync(GitDistributedSettingsScope.Local); AssertOtherValues(local);
        Assert.That(local.Values[JenkinsSettings.ProjectNameKey], Is.EqualTo("folder/new-job|other-job?builds[number]"));
        foreach (GitDistributedSettingsScope scope in new[] { GitDistributedSettingsScope.Global, GitDistributedSettingsScope.Distributed })
        { Assert.That(File.ReadAllBytes(f.Profile.Path(scope)), Is.EqualTo(before[f.Profile.Path(scope)])); }
        Dictionary<string, byte[]> applied = f.Profile.Material(); f.Select(GitDistributedSettingsScope.Global); f.Edit("JenkinsProjectName", "discard-job"); f.Click("CancelDesktopSettings");
        await f.UntilAsync(() => f.Modal.IsCompleted); AssertXmlUnchanged(f, applied);
    }

    [AvaloniaTest]
    [TestCase("Local")] [TestCase("Distributed")] [TestCase("Global")]
    public async Task InvalidRegexInEveryEditedScopeBlocksApplyWithoutTouchingFilesEvenIfMaskedByAnotherScope(string name)
    {
        GitDistributedSettingsScope scope = Enum.Parse<GitDistributedSettingsScope>(name);
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material();
        f.Select(scope); f.Edit("IgnoreBuildBranch", "["); f.Select(GitDistributedSettingsScope.Effective); f.Click("ApplyDesktopSettings"); await f.SaveFailedAsync();
        AssertXmlUnchanged(f, before); Assert.That(f.Session.HasChanges, Is.True); f.Select(scope); Assert.That(f.Field<TextBox>("IgnoreBuildBranch").Text, Is.EqualTo("["));
        f.Edit("IgnoreBuildBranch", "^release/"); f.Click("ApplyDesktopSettings"); await f.SavedAsync(); Assert.That((await f.Profile.ReadAsync(scope)).Values[JenkinsSettings.IgnoreBuildBranchKey], Is.EqualTo("^release/"));
    }

    [AvaloniaTest]
    public async Task UntouchedFutureInvalidRegexIsNotRewrittenOrValidatedByUnrelatedDisabledProviderEdit()
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(prepare: p => JenkinsSettingsUiFixture.Seed(p, GitDistributedSettingsScope.Global, ignore: "[", provider: "Future CI"));
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Local); await f.ProviderAsync(GitHubActionsSettings.ProviderName); f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values[JenkinsSettings.IgnoreBuildBranchKey], Is.EqualTo("["));
    }

    [AvaloniaTest]
    public async Task CasFailureAndRetryDoNotReplayAcceptedScopeOrOverwritePeerAndExplicitReopenCanSave()
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(); await f.ShowAsync();
        f.Select(GitDistributedSettingsScope.Local); f.Edit("JenkinsProjectName", "saved-local"); f.Select(GitDistributedSettingsScope.Global); f.Edit("JenkinsProjectName", "retry-global");
        string path = f.Profile.Path(GitDistributedSettingsScope.Global); XDocument peer = XDocument.Load(path);
        peer.Root!.Add(new XElement("item", new XElement("key", new XElement("string", "peer.edit")), new XElement("value", new XElement("string", "preserve peer")))); peer.Save(path); byte[] peerBytes = File.ReadAllBytes(path);
        f.Click("ApplyDesktopSettings"); await f.SaveFailedAsync(); byte[] accepted = File.ReadAllBytes(f.Profile.Path(GitDistributedSettingsScope.Local));
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Local)).Values[JenkinsSettings.ProjectNameKey], Is.EqualTo("saved-local")); Assert.That(File.ReadAllBytes(path), Is.EqualTo(peerBytes));
        f.Click("ApplyDesktopSettings"); await f.SaveFailedAsync(); Assert.That(File.ReadAllBytes(f.Profile.Path(GitDistributedSettingsScope.Local)), Is.EqualTo(accepted)); Assert.That(File.ReadAllBytes(path), Is.EqualTo(peerBytes));
        await f.ReopenAsync(); f.Select(GitDistributedSettingsScope.Global); f.Edit("JenkinsProjectName", "retry-global"); f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        GitXmlSettingsSnapshot saved = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global); Assert.That(saved.Values["peer.edit"], Is.EqualTo("preserve peer")); Assert.That(saved.Values[JenkinsSettings.ProjectNameKey], Is.EqualTo("retry-global"));
    }

    [AvaloniaTest]
    public async Task FirstPointerClickTabAndEnterPreserveFieldFocusWithoutAcceptingSettings()
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); f.Click(Editors[0]);
        foreach (string name in Editors)
        {
            Assert.That(f.Field<TextBox>(name).IsKeyboardFocusWithin, Is.True, name); f.Press(PhysicalKey.Enter);
            Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Field<TextBox>(name).IsKeyboardFocusWithin, Is.True); f.Press(PhysicalKey.Tab);
        }
        f.Profile.AssertMaterial(before); Assert.That(f.OpenedUrls, Is.Empty);
    }

    [AvaloniaTest]
    public async Task ProjectVariablesRemainLiteralInXmlAndEditorsButSessionExpandsRealFetchRepositoryValues()
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(prepare: p => JenkinsSettingsUiFixture.Seed(p, GitDistributedSettingsScope.Global,
            project: "{cRepoProject}/{cRepoShortName}|second-job", server: null));
        await f.ShowAsync();
        await f.Profile.Git.GitAsync("config", "remote.origin.url", "https://git.example.invalid/ProjectA/repository.git");
        await f.Profile.Git.GitAsync("config", "remote.origin.pushurl", "https://push.example.invalid/WrongProject/wrong.git");
        await f.ReopenAsync(); f.Select(GitDistributedSettingsScope.Global);
        Assert.That(f.Field<TextBox>("JenkinsServerUrl").Text, Is.Empty, "The Jenkins page has no remote-based server discovery.");
        Assert.That(f.Field<TextBox>("JenkinsProjectName").Text, Is.EqualTo("{cRepoProject}/{cRepoShortName}|second-job"));
        Assert.That(f.Session.Jenkins(GitDistributedSettingsScope.Global).ProjectName, Is.EqualTo("ProjectA/repository|second-job"));
        f.Edit("IgnoreBuildBranch", "^ignore/"); f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values[JenkinsSettings.ProjectNameKey], Is.EqualTo("{cRepoProject}/{cRepoShortName}|second-job"));
    }

    [AvaloniaTest]
    [TestCase("dialog-cancel")] [TestCase("host-cancel")]
    public async Task ActualCredentialsCancelNeverWritesAUserSecretOrXmlAndBearerIsUnavailable(string cancel)
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); BuildServerCredentialsDialog child = await CredentialsAsync(f);
        RadioButton bearer = Child<RadioButton>(child, "radioButtonBearerToken"); Assert.That(bearer.IsVisible && bearer.IsEnabled, Is.False);
        PushProcessTestDriver.Pointer(Child<RadioButton>(child, "radioButtonAuthenticatedUser")); f.Pump();
        Edit(child, "textBoxUserName", "explicit-user"); Edit(child, "textBoxPassword", "private-password");
        Assert.That(Child<TextBox>(child, "textBoxPassword").PasswordChar, Is.Not.EqualTo('\0')); Assert.That(Child<TextBox>(child, "textBoxPassword").RevealPassword, Is.False);
        PushProcessTestDriver.Pointer(Child<Button>(child, cancel == "dialog-cancel" ? "buttonCancel" : "buttonOK"));
        await f.UntilAsync(() => f.ProviderPage.CredentialsCompletion.IsCompleted); await f.ProviderPage.CredentialsCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(f.Session.HasChanges, Is.EqualTo(cancel == "host-cancel")); Assert.That(File.Exists(f.Session.CredentialsStore.FilePath), Is.False); f.Profile.AssertMaterial(before);
        f.Click("CancelDesktopSettings"); await f.UntilAsync(() => f.Modal.IsCompleted); Assert.That(File.Exists(f.Session.CredentialsStore.FilePath), Is.False); AssertXmlUnchanged(f, before);
    }

    [AvaloniaTest]
    [TestCase("guest")] [TestCase("basic")]
    public async Task ExplicitCredentialsOkThenApplyPersistsOnlyTheEndpointBoundPrivateCredentialAndReopensIt(string mode)
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); BuildServerCredentialsDialog child = await CredentialsAsync(f);
        PushProcessTestDriver.Pointer(Child<RadioButton>(child, mode == "basic" ? "radioButtonAuthenticatedUser" : "radioButtonGuestAccess")); f.Pump();
        if (mode == "basic") { Edit(child, "textBoxUserName", "explicit-user"); Edit(child, "textBoxPassword", "private-password"); }
        PushProcessTestDriver.Pointer(Child<Button>(child, "buttonOK")); await f.UntilAsync(() => f.ProviderPage.CredentialsCompletion.IsCompleted); await f.ProviderPage.CredentialsCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(f.Session.HasChanges, Is.True); Assert.That(File.Exists(f.Session.CredentialsStore.FilePath), Is.False); f.Profile.AssertMaterial(before);
        f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        BuildServerCredentialsSnapshot saved = f.Session.CredentialsStore.Load(JenkinsSettings.ProviderName, new Uri("https://jenkins.example.invalid/base/")); Assert.That(saved.Exists, Is.True);
        Assert.That(saved.Credentials.BuildServerCredentialsType, Is.EqualTo(mode == "basic" ? BuildServerCredentialsType.UsernameAndPassword : BuildServerCredentialsType.Guest));
        if (mode == "basic") { Assert.That(saved.Credentials.Username, Is.EqualTo("explicit-user")); Assert.That(saved.Credentials.Password, Is.EqualTo("private-password")); }
        Assert.That(f.Session.CredentialsStore.Load(TeamCitySettings.ProviderName, new Uri("https://jenkins.example.invalid/base/")).Exists, Is.False);
        Assert.That(f.Session.CredentialsStore.Load(JenkinsSettings.ProviderName, new Uri("https://jenkins.example.invalid/another/")).Exists, Is.False);
        AssertXmlUnchanged(f, before); await f.ReopenAsync(); f.Select(GitDistributedSettingsScope.Global); child = await CredentialsAsync(f);
        Assert.That(Child<RadioButton>(child, mode == "basic" ? "radioButtonAuthenticatedUser" : "radioButtonGuestAccess").IsChecked, Is.True);
        if (mode == "basic") { Assert.That(Child<TextBox>(child, "textBoxUserName").Text, Is.EqualTo("explicit-user")); Assert.That(Child<TextBox>(child, "textBoxPassword").Text, Is.EqualTo("private-password")); }
        PushProcessTestDriver.Pointer(Child<Button>(child, "buttonCancel")); await f.UntilAsync(() => f.ProviderPage.CredentialsCompletion.IsCompleted);
    }

    [AvaloniaTest]
    [TestCase("scope")] [TestCase("provider")] [TestCase("endpoint")] [TestCase("owner-close")]
    public async Task CredentialsChildIsCanceledAndDrainedWhenItsCapturedOwnerContextChanges(string change)
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material();
        string? baselinePassword = f.Session.CredentialDraft(JenkinsSettings.ProviderName, new Uri("https://jenkins.example.invalid/base/")).Password;
        BuildServerCredentialsDialog child = await CredentialsAsync(f);
        PushProcessTestDriver.Pointer(Child<RadioButton>(child, "radioButtonAuthenticatedUser")); f.Pump(); Edit(child, "textBoxPassword", "discarded-private-password");
        Task completion = f.ProviderPage.CredentialsCompletion; Assert.That(completion.IsCompleted, Is.False);
        // Simulate a queued host-context change while the modal owns input; no click through a disabled owner.
        if (change == "scope") { f.ProviderPage.SetScope(GitDistributedSettingsScope.Local); }
        else if (change == "provider") { f.Field<ComboBox>("BuildServerProvider").SelectedItem = TeamCitySettings.ProviderName; }
        else if (change == "endpoint") { f.Field<TextBox>("JenkinsServerUrl").Text = "https://jenkins.example.invalid/another/"; }
        else { f.Dialog.Close(); }
        await f.UntilAsync(() => completion.IsCompleted && !child.IsVisible); await completion.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "owner-close") { await f.UntilAsync(() => f.Modal.IsCompleted); }
        Assert.That(File.Exists(f.Session.CredentialsStore.FilePath), Is.False); f.Profile.AssertMaterial(before);
        Assert.That(f.Session.CredentialDraft(JenkinsSettings.ProviderName, new Uri("https://jenkins.example.invalid/base/")).Password, Is.EqualTo(baselinePassword));
    }

    [AvaloniaTest]
    public async Task MalformedPrivateCredentialJsonShowsOnlyFixedNoticeAndCompletesSuccessfullyWithoutWriting()
    {
        await using JenkinsSettingsUiFixture f = await JenkinsSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        string path = f.Session.CredentialsStore.FilePath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ malformed-private-payload"); byte[] broken = File.ReadAllBytes(path); Dictionary<string, byte[]> before = f.Profile.Material();
        f.Click("JenkinsCredentials"); await f.UntilAsync(() => f.ProviderPage.CredentialsCompletion.IsCompleted);
        Assert.That(f.ProviderPage.CredentialsCompletion.IsCompletedSuccessfully, Is.True); await f.ProviderPage.CredentialsCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(f.Field<TextBlock>("JenkinsCredentialsNotice").Text, Is.EqualTo("Could not read Jenkins credentials. Check the server URL and profile access."));
        Assert.That(f.Dialog.OwnedWindows, Is.Empty); Assert.That(f.ProviderPage.IsBusy, Is.False); Assert.That(f.Session.HasChanges, Is.False);
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(broken)); f.Profile.AssertMaterial(before); Assert.That(f.OpenedUrls, Is.Empty);
    }

    private static async Task<BuildServerCredentialsDialog> CredentialsAsync(JenkinsSettingsUiFixture f)
    {
        f.Click("JenkinsCredentials"); await f.UntilAsync(() => f.Dialog.OwnedWindows.OfType<BuildServerCredentialsDialog>().Any(child => child.IsVisible));
        return f.Dialog.OwnedWindows.OfType<BuildServerCredentialsDialog>().Single(child => child.IsVisible);
    }
    private static T Child<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Edit(Window window, string name, string text)
    {
        TextBox box = Child<TextBox>(window, name); PushProcessTestDriver.Pointer(box);
        window.KeyPressQwerty(PhysicalKey.A, Avalonia.Input.Raw.RawInputModifiers.Control); window.KeyReleaseQwerty(PhysicalKey.A, Avalonia.Input.Raw.RawInputModifiers.Control);
        window.KeyPressQwerty(PhysicalKey.Backspace); window.KeyReleaseQwerty(PhysicalKey.Backspace); window.KeyTextInput(text); DetailedSettingsUiHost.Pump(window);
        Assert.That(box.Text, Is.EqualTo(text));
    }

    private static readonly string[] Editors = ["JenkinsServerUrl", "JenkinsProjectName", "IgnoreBuildBranch"];
    private static void AssertXmlUnchanged(JenkinsSettingsUiFixture f, IReadOnlyDictionary<string, byte[]> before)
    { foreach (string path in f.Profile.XmlPaths) { Assert.That(File.ReadAllBytes(path), Is.EqualTo(before[path]), path); } }
    private static void AssertOtherValues(GitXmlSettingsSnapshot xml)
    {
        foreach ((string key, string value) in new[]
        {
            ("future.λ", "keep <literal> & \nbody"), ("BuildServer.Jenkins.FutureSetting", "keep Jenkins extension"),
            (GitHubActionsSettings.OwnerKey, "untouched-owner"), (GitHubActionsSettings.TokenKey, "untouched-gha-token"),
            (TeamCitySettings.ProjectNameKey, "untouched-project"), (TeamCitySettings.BuildIdFilterKey, "untouched.*"),
            (GitLabSettings.ProjectIdKey, "71"), (GitLabSettings.ApiTokenKey, "untouched-gitlab-token"),
            (AppVeyorSettings.ProjectNameKey, "account/untouched"), (AzureDevOpsSettings.RepositoryNameKey, "hidden-repo")
        }) { Assert.That(xml.Values[key], Is.EqualTo(value)); }
    }
}

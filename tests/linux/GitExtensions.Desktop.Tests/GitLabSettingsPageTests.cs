using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GitLabSettingsPageTests
{
    private const string Failure = "Failed to obtain project from server. Try to specify valid API token or check instance URL";

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalTwoColumnPageKeepsThreeFieldsCompactTranslatedLinksAndActuallyMaskedToken(string language, bool dark)
    {
        await using GitLabSettingsUiFixture f = await GitLabSettingsUiFixture.CreateAsync(language, dark); await f.ShowAsync();
        f.Select(GitDistributedSettingsScope.Global); Dictionary<string, byte[]> before = f.Profile.Material();
        Grid table = f.Field<Grid>("tableLayoutPanel1"); Assert.That(table.ColumnDefinitions.Count, Is.EqualTo(2));
        Assert.That(table.RowDefinitions.Count, Is.EqualTo(6)); Assert.That(table.RowDefinitions[3].MinHeight, Is.EqualTo(20));
        Assert.That(f.Page.GetVisualDescendants().OfType<TextBox>().Count(), Is.EqualTo(3), "There is no new PagesLimit field.");
        TextBox instance = f.Field<TextBox>("InstanceUrlTextBox"), project = f.Field<TextBox>("ProjectIdTextBox"), token = f.Field<TextBox>("ApiTokenTextBox");
        Assert.That(instance.TranslatePoint(default, table)!.Value.X, Is.EqualTo(project.TranslatePoint(default, table)!.Value.X));
        Assert.That(instance.TranslatePoint(default, table)!.Value.X, Is.EqualTo(token.TranslatePoint(default, table)!.Value.X));
        Assert.That(token.PasswordChar, Is.Not.EqualTo('\0')); Assert.That(token.RevealPassword, Is.False);
        TextPresenter presenter = token.GetVisualDescendants().OfType<TextPresenter>().Single(control => control.Name == "PART_TextPresenter");
        string painted = string.Concat(presenter.TextLayout.TextLines.SelectMany(line => line.TextRuns).Select(run => run.Text.ToString()));
        Assert.That(painted, Is.Not.Empty); Assert.That(painted.All(character => character == token.PasswordChar), Is.True);
        TranslationCatalog catalog = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
        foreach ((string name, string source) in new[] { ("GetProjectIdLink", "Get Project ID from server"), ("TokenManagementLink", "Go to token management page") })
        { Assert.That(f.Field<Button>(name).Content, Is.EqualTo(catalog.Translate("GitlabSettingsUserControl", name, "Text", source))); }
        Assert.That(f.Field<TextBlock>("GetProjectIdStatusText").Text, Is.EqualTo(catalog.Translate("GitlabSettingsUserControl", "GetProjectIdStatusText", "Text", Failure)));
        Assert.That(f.Page.GetVisualDescendants().OfType<RadioButton>().Count(choice => choice.IsEffectivelyVisible), Is.Zero, "The shared provider page owns the only scope header.");
        foreach (TextBlock label in f.Page.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text)))
        {
            Assert.That(label.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False, label.Text);
            Assert.That(label.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(label.Bounds.Width + .5), label.Text);
            Rect actual = new(label.TranslatePoint(default, f.Dialog)!.Value, label.Bounds.Size);
            Assert.That(new Rect(default, f.Dialog.ClientSize).Contains(actual), Is.True, label.Text);
        }
        Assert.That(table.Bounds.Height, Is.LessThanOrEqualTo(155), "The English/French normal page retains its compact six-row form.");
        f.Pump(); using var image = f.Dialog.CaptureRenderedFrame(); Assert.That(image, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "gitlab-settings-" + language + (dark ? "-dark.png" : "-light.png")); image!.Save(path); TestContext.AddTestAttachment(path);
        Assert.That(f.Clients, Is.Empty); Assert.That(f.OpenedUrls, Is.Empty); f.Profile.AssertMaterial(before);
    }

    [AvaloniaTest]
    public async Task ActualApplySavesOnlyTheEditedScopeWithWindowsPagesLimitAndCancelDiscardsLaterScopes()
    {
        await using GitLabSettingsUiFixture f = await GitLabSettingsUiFixture.CreateAsync(); await f.ShowAsync();
        Dictionary<string, byte[]> before = f.Profile.Material();
        Assert.That(f.Field<TextBox>("InstanceUrlTextBox").IsEffectivelyEnabled, Is.False, "Effective is read-only.");
        f.Select(GitDistributedSettingsScope.Global); f.Edit("ProjectIdTextBox", "2048"); f.Edit("ApiTokenTextBox", "explicit-new-token");
        f.Profile.AssertMaterial(before); f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        GitXmlSettingsSnapshot global = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global);
        Assert.That(global.Values[GitLabSettings.ProjectIdKey], Is.EqualTo("2048")); Assert.That(global.Values[GitLabSettings.ApiTokenKey], Is.EqualTo("explicit-new-token"));
        Assert.That(global.Values[GitLabSettings.PagesLimitKey], Is.EqualTo("0")); Assert.That(global.Values["future.λ"], Is.EqualTo("keep <literal> & \nbody"));
        foreach (GitDistributedSettingsScope scope in new[] { GitDistributedSettingsScope.Local, GitDistributedSettingsScope.Distributed })
        { Assert.That(File.ReadAllBytes(f.Profile.Path(scope)), Is.EqualTo(before[f.Profile.Path(scope)])); }
        Dictionary<string, byte[]> applied = f.Profile.Material();
        f.Select(GitDistributedSettingsScope.Local); f.Edit("ProjectIdTextBox", "4096");
        f.Select(GitDistributedSettingsScope.Distributed); f.Edit("ApiTokenTextBox", "discarded-token");
        f.Click("CancelDesktopSettings"); await f.UntilAsync(() => f.Modal.IsCompleted);
        Assert.That(await f.Modal.WaitAsync(TimeSpan.FromSeconds(5)), Is.True, "Original Settings keeps OK after Apply, despite final Cancel.");
        f.Profile.AssertMaterial(applied); Assert.That(f.Clients, Is.Empty);
    }

    [AvaloniaTest]
    public async Task UnknownUntouchedValuesAndScopeSwitchesDoNotRewriteOrStartLookup()
    {
        await using GitLabSettingsUiFixture f = await GitLabSettingsUiFixture.CreateAsync(prepare: profile =>
        { foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { GitLabSettingsUiFixture.Seed(profile, scope, "future-id"); } });
        await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material();
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes.Append(GitDistributedSettingsScope.Effective))
        { f.Select(scope); Assert.That(f.Field<TextBox>("ProjectIdTextBox").Text, Is.EqualTo("future-id")); }
        Assert.That(f.Session.HasChanges, Is.False); Assert.That(f.Clients, Is.Empty);
        f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes)
        { Assert.That(File.ReadAllBytes(f.Profile.Path(scope)), Is.EqualTo(before[f.Profile.Path(scope)]), "Untouched raw Gitlab values stay exact: " + scope); }
    }

    [AvaloniaTest]
    public async Task AutomaticLookupRunsOnlyWhenTheEditableGitlabProviderIsActuallyActivatedAndOnlyStagesItsResult()
    {
        await using GitLabSettingsUiFixture f = await GitLabSettingsUiFixture.CreateAsync(prepare: profile =>
            GitLabSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Global, id: null, instance: null, token: null));
        await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material(); Assert.That(f.Clients, Is.Empty, "Effective may not auto-load.");
        f.Select(GitDistributedSettingsScope.Global); await f.IdleAsync();
        Assert.That(f.Clients.Count, Is.EqualTo(1)); Assert.That(f.Clients[0].Settings.InstanceUrl, Is.EqualTo("https://gitlab.example"));
        Assert.That(f.Clients[0].Requests.Select(request => (request.Namespace, request.Name)), Is.EqualTo(new[] { ("group/subgroup", "repository") }));
        Assert.That(f.Field<TextBox>("ProjectIdTextBox").Text, Is.EqualTo("107"));
        Assert.That(f.Session.Read(GitDistributedSettingsScope.Global, GitLabSettings.InstanceUrlKey), Is.EqualTo("https://gitlab.example"));
        Assert.That(f.Session.Read(GitDistributedSettingsScope.Global, GitLabSettings.ProjectIdKey), Is.EqualTo("107"));
        f.Page.ShowLoadState(); f.Page.SetProviderActive(true); f.Pump(); Assert.That(f.Clients.Count, Is.EqualTo(1));
        Assert.That(f.Clients[0].Disposed, Is.True); Assert.That(f.Clients[0].RuntimeCalls, Is.Zero); f.Profile.AssertMaterial(before);
        f.Click("CancelDesktopSettings"); await f.UntilAsync(() => f.Modal.IsCompleted); Assert.That(await f.Modal, Is.False); f.Profile.AssertMaterial(before);
    }

    [AvaloniaTest]
    public async Task InactiveProviderAndTypingNeverStartAutomaticNetworkReads()
    {
        await using GitLabSettingsUiFixture f = await GitLabSettingsUiFixture.CreateAsync(prepare: profile =>
            GitLabSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Global, id: null, provider: "TeamCity"));
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global); f.Pump(); Assert.That(f.Clients, Is.Empty); Assert.That(f.Page.IsVisible, Is.False);
        f.Select(GitDistributedSettingsScope.Local); Assert.That(f.Page.IsVisible, Is.True);
        f.Edit("ProjectIdTextBox", ""); f.Edit("InstanceUrlTextBox", "https://gitlab.example"); f.Edit("ApiTokenTextBox", "typed-token");
        f.Pump(); Assert.That(f.Clients, Is.Empty); Assert.That(f.Field<TextBox>("ProjectIdTextBox").Text, Is.Empty);
        f.Edit("InstanceUrlTextBox", ""); f.Page.ShowLoadState(); f.Pump();
        Assert.That(f.Field<TextBox>("InstanceUrlTextBox").Text, Is.Empty, "A user-cleared/malformed endpoint must not be replaced with remote detection."); Assert.That(f.Clients, Is.Empty);
    }

    [AvaloniaTest]
    public async Task FirstClickAndTabEnterLookupKeepKeyboardFocusAndDoNotInvokeTheSettingsDefaultOk()
    {
        await using GitLabSettingsUiFixture f = await GitLabSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        Dictionary<string, byte[]> before = f.Profile.Material(); f.Click("InstanceUrlTextBox"); Assert.That(f.Field<TextBox>("InstanceUrlTextBox").IsKeyboardFocusWithin, Is.True);
        f.Press(PhysicalKey.Tab); Assert.That(f.Field<TextBox>("ProjectIdTextBox").IsKeyboardFocusWithin, Is.True);
        f.Press(PhysicalKey.Tab); Assert.That(f.Field<Button>("GetProjectIdLink").IsKeyboardFocusWithin, Is.True);
        f.Press(PhysicalKey.Enter); await f.IdleAsync();
        Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Field<Button>("GetProjectIdLink").IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Field<TextBox>("ProjectIdTextBox").Text, Is.EqualTo("107")); Assert.That(f.Clients.Count, Is.EqualTo(1));
        f.Profile.AssertMaterial(before); Assert.That(f.OpenedUrls, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase("https://gitlab.example/base/", "https://gitlab.example/base/-/profile/personal_access_tokens?name=GitExtensionsIntegration&scopes=api")]
    [TestCase("http://gitlab.example:8080", "http://gitlab.example:8080/-/profile/personal_access_tokens?name=GitExtensionsIntegration&scopes=api")]
    public async Task TokenManagementIsAnExplicitActionUsingTheOriginalTailAndConfiguredBasePath(string instance, string expected)
    {
        await using GitLabSettingsUiFixture f = await GitLabSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        f.Edit("InstanceUrlTextBox", instance); Assert.That(f.OpenedUrls, Is.Empty); Assert.That(f.Clients, Is.Empty);
        f.Click("TokenManagementLink"); Assert.That(f.OpenedUrls, Is.EqualTo(new[] { expected })); Assert.That(f.Clients, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase("")] [TestCase("relative")] [TestCase("file:///tmp/example")]
    [TestCase("https://user:password@gitlab.example")] [TestCase("https://gitlab.example/?token=literal")]
    public async Task InvalidInstanceDisablesBothLinksWithoutCreatingAClientOrOpeningAnything(string invalid)
    {
        await using GitLabSettingsUiFixture f = await GitLabSettingsUiFixture.CreateAsync(); await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        f.Edit("InstanceUrlTextBox", invalid);
        Assert.That(f.Field<Button>("GetProjectIdLink").IsEnabled, Is.False); Assert.That(f.Field<Button>("TokenManagementLink").IsEnabled, Is.False);
        Assert.That(f.Clients, Is.Empty); Assert.That(f.OpenedUrls, Is.Empty); Assert.That(f.Field<TextBox>("InstanceUrlTextBox").Text, Is.EqualTo(invalid));
    }

    [AvaloniaTest]
    public async Task ExplicitLookupFailureShowsOnlyTheOriginalFixedErrorWhileAutomaticFailureStaysSilent()
    {
        await using GitLabSettingsUiFixture f = await GitLabSettingsUiFixture.CreateAsync(prepare: profile =>
            GitLabSettingsUiFixture.Seed(profile, GitDistributedSettingsScope.Global, id: null));
        f.Lookup = (_, _, _, _) => Task.FromException<GitLabProject>(new IOException("secret fake-token upstream response body"));
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global); await f.IdleAsync();
        Assert.That(f.Clients.Count, Is.EqualTo(1)); Assert.That(f.Field<TextBlock>("GetProjectIdStatusText").IsVisible, Is.False);
        f.Click("GetProjectIdLink"); await f.IdleAsync();
        Assert.That(f.Field<TextBlock>("GetProjectIdStatusText").IsVisible, Is.True);
        Assert.That(f.Field<TextBlock>("GetProjectIdStatusText").Text, Is.EqualTo(Failure));
        Assert.That(f.Field<TextBox>("ProjectIdTextBox").Text, Is.Empty); Assert.That(f.Clients.All(client => client.Disposed), Is.True);
    }

    [AvaloniaTest]
    [TestCase("InstanceUrlTextBox", "https://other.gitlab.example")]
    [TestCase("ApiTokenTextBox", "replacement-token")]
    [TestCase("ProjectIdTextBox", "900")]
    public async Task EditingAnyCapturedFieldCancelsLookupAndLateResultNeverOverwritesTheNewDraftOrSelection(string field, string value)
    {
        await using GitLabSettingsUiFixture f = await GitLabSettingsUiFixture.CreateAsync(); var gate = f.Gate<GitLabProject>();
        f.Lookup = (_, _, _, _) => gate.Task; await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        f.Click("GetProjectIdLink"); await f.UntilAsync(() => f.Clients.Count == 1 && f.Clients[0].Requests.Count == 1);
        Assert.That(f.Field<Button>("ApplyDesktopSettings").IsEnabled || f.Field<Button>("SaveDesktopSettings").IsEnabled, Is.False);
        Assert.That(f.Field<TextBox>(field).IsEffectivelyEnabled, Is.True, "A running lookup must leave editors usable."); f.Edit(field, value);
        TextBox editor = f.Field<TextBox>(field); editor.CaretIndex = value.Length; editor.SelectionStart = 1; editor.SelectionEnd = Math.Min(3, value.Length);
        var selection = (editor.CaretIndex, editor.SelectionStart, editor.SelectionEnd); Assert.That(editor.IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Clients[0].Requests[0].Token.IsCancellationRequested, Is.True);
        gate.SetResult(new(501)); await f.IdleAsync();
        Assert.That(editor.Text, Is.EqualTo(value)); Assert.That(editor.IsKeyboardFocusWithin, Is.True);
        Assert.That((editor.CaretIndex, editor.SelectionStart, editor.SelectionEnd), Is.EqualTo(selection));
        Assert.That(f.Field<TextBox>("ProjectIdTextBox").Text, Is.EqualTo(field == "ProjectIdTextBox" ? "900" : "71"));
        Assert.That(f.Field<TextBlock>("GetProjectIdStatusText").IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task ANewInstanceRequestCanFinishFirstAndOldInstanceCannotPublishAfterward()
    {
        await using GitLabSettingsUiFixture f = await GitLabSettingsUiFixture.CreateAsync(); var first = f.Gate<GitLabProject>(); var second = f.Gate<GitLabProject>();
        f.Lookup = (settings, _, _, _) => settings.InstanceUrl.Contains("other.gitlab", StringComparison.Ordinal) ? second.Task : first.Task;
        await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global); f.Click("GetProjectIdLink"); await f.UntilAsync(() => f.Clients.Count == 1);
        f.Edit("InstanceUrlTextBox", "https://other.gitlab.example"); f.Click("GetProjectIdLink"); await f.UntilAsync(() => f.Clients.Count == 2 && f.Clients[1].Requests.Count == 1);
        Assert.That(f.Clients[0].Requests[0].Token.IsCancellationRequested, Is.True);
        Assert.That(f.Clients[1].Requests[0].Namespace, Is.EqualTo("other")); Assert.That(f.Clients[1].Requests[0].Name, Is.EqualTo("project"));
        second.SetResult(new(202)); await f.UntilAsync(() => f.Field<TextBox>("ProjectIdTextBox").Text == "202");
        Assert.That(f.Page.IsBusy, Is.True, "Obsolete reads remain part of the drain."); first.SetResult(new(101)); await f.IdleAsync();
        Assert.That(f.Field<TextBox>("ProjectIdTextBox").Text, Is.EqualTo("202"));
        Assert.That(f.Session.Read(GitDistributedSettingsScope.Global, GitLabSettings.InstanceUrlKey), Is.EqualTo("https://other.gitlab.example"));
        Assert.That(f.Clients.All(client => client.Disposed), Is.True);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ScopeOrProviderChangesCancelAndHideTheOldRequestWithoutUpdatingEitherScope(bool changeProvider)
    {
        await using GitLabSettingsUiFixture f = await GitLabSettingsUiFixture.CreateAsync(); var gate = f.Gate<GitLabProject>();
        f.Lookup = (_, _, _, _) => gate.Task; await f.ShowAsync(); f.Select(GitDistributedSettingsScope.Global);
        f.Click("GetProjectIdLink"); await f.UntilAsync(() => f.Clients.Count == 1 && f.Clients[0].Requests.Count == 1);
        if (changeProvider) { f.Provider(""); Assert.That(f.Page.IsVisible, Is.False); } else { f.Select(GitDistributedSettingsScope.Local); }
        Assert.That(f.Clients[0].Requests[0].Token.IsCancellationRequested, Is.True); gate.SetResult(new(999)); await f.IdleAsync();
        Assert.That(f.Session.Read(GitDistributedSettingsScope.Global, GitLabSettings.ProjectIdKey), Is.EqualTo("71"));
        Assert.That(f.Session.Read(GitDistributedSettingsScope.Local, GitLabSettings.ProjectIdKey), Is.EqualTo("71"));
        Assert.That(f.Clients.Count, Is.EqualTo(1)); if (!changeProvider) { Assert.That(f.Field<TextBox>("ProjectIdTextBox").Text, Is.EqualTo("71")); }
    }

    [AvaloniaTest]
    [TestCase("Cancel")] [TestCase("OwnerClose")]
    public async Task ClosingCancelsButDrainsAnUncooperativeLookupBeforeTheSettingsOwnerCanFinish(string close)
    {
        await using GitLabSettingsUiFixture f = await GitLabSettingsUiFixture.CreateAsync(); var gate = f.Gate<GitLabProject>();
        f.Lookup = (_, _, _, _) => gate.Task; await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material(); f.Select(GitDistributedSettingsScope.Global);
        f.Click("GetProjectIdLink"); await f.UntilAsync(() => f.Clients.Count == 1 && f.Clients[0].Requests.Count == 1);
        if (close == "OwnerClose") { f.Owner.Close(); } else { f.Click("CancelDesktopSettings"); }
        f.Pump(); Assert.That(f.Clients[0].Requests[0].Token.IsCancellationRequested, Is.True);
        Assert.That(f.Modal.IsCompleted, Is.False); Assert.That(f.Page.Completion.IsCompleted, Is.False); Assert.That(f.Clients[0].Disposed, Is.False);
        gate.SetResult(new(999)); await f.UntilAsync(() => f.Modal.IsCompleted); Assert.That(await f.Modal.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        Assert.That(f.Clients[0].Disposed, Is.True); Assert.That(f.Session.Read(GitDistributedSettingsScope.Global, GitLabSettings.ProjectIdKey), Is.EqualTo("71")); f.Profile.AssertMaterial(before);
    }

    [AvaloniaTest]
    public async Task CompletionAlsoIncludesAsynchronousClientDisposalRatherThanOnlyTheLookupResponse()
    {
        await using GitLabSettingsUiFixture f = await GitLabSettingsUiFixture.CreateAsync(); var gate = f.Gate<bool>(); f.DisposeClient = async () => { await gate.Task; };
        await f.ShowAsync(); Dictionary<string, byte[]> before = f.Profile.Material(); f.Select(GitDistributedSettingsScope.Global); f.Click("GetProjectIdLink");
        await f.UntilAsync(() => f.Field<TextBox>("ProjectIdTextBox").Text == "107");
        Assert.That(f.Page.IsBusy, Is.True); Assert.That(f.Clients[0].Disposed, Is.False);
        f.Click("CancelDesktopSettings"); Assert.That(f.Modal.IsCompleted, Is.False); gate.SetResult(true);
        await f.UntilAsync(() => f.Modal.IsCompleted); Assert.That(f.Clients[0].Disposed, Is.True); f.Profile.AssertMaterial(before);
    }
}

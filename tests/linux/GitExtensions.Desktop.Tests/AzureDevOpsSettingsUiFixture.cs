using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

internal sealed class AzureDevOpsPageTestClient(AzureDevOpsSettings settings,
    Func<AzureDevOpsSettings, long, CancellationToken, Task<string?>> lookup, Func<Task>? dispose = null) : IAzureDevOpsClient
{
    internal AzureDevOpsSettings Settings { get; } = settings;
    internal List<(long Id, CancellationToken Token)> Requests { get; } = [];
    internal bool Disposed { get; private set; }
    internal int RuntimeCalls { get; private set; }
    public Task<string?> GetBuildDefinitionNameFromIdAsync(long buildId, CancellationToken cancellationToken = default)
    { Requests.Add((buildId, cancellationToken)); return lookup(Settings, buildId, cancellationToken); }
    public Task<AzureDevOpsSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default)
    { RuntimeCalls++; throw new InvalidOperationException("A Settings action must not poll builds."); }
    public Uri? BuildPageUri(AzureDevOpsBuild build)
    { RuntimeCalls++; throw new InvalidOperationException("A Settings action must not open build pages."); }
    public async ValueTask DisposeAsync() { if (dispose is not null) { await dispose(); } Disposed = true; }
}

internal sealed class AzureDevOpsSettingsUiFixture : IAsyncDisposable
{
    internal const string Project = "https://dev.azure.com/acme/ProjectA";
    internal const string Build = Project + "/_build/results?buildId=42&view=results";
    internal DetailedSettingsTestScope Profile { get; }
    internal GitHubActionsSettingsSession Session { get; private set; } = null!;
    internal DesktopSettingsDialog Dialog { get; private set; } = null!;
    internal BuildServerIntegrationSettingsPage ProviderPage { get; private set; } = null!;
    internal AzureDevOpsSettingsPage Page => ProviderPage.AzureDevOpsPage;
    internal Window Owner { get; } = new() { Width = 1040, Height = 740 };
    internal List<AzureDevOpsPageTestClient> Clients { get; } = [];
    internal List<string> OpenedUrls { get; } = [];
    internal List<Action> ReleaseOnDispose { get; } = [];
    internal Func<AzureDevOpsSettings, long, CancellationToken, Task<string?>> Lookup { get; set; } = (_, _, _) => Task.FromResult<string?>("resolved-pipeline");
    internal Func<CancellationToken, Task<string?>>? ReadClipboard { get; set; }
    internal Func<Task>? DisposeClient { get; set; }
    internal Action<string>? LaunchBrowser { get; set; }
    internal string? ClipboardText { get; set; } = Build;
    internal Task<bool> Modal { get; private set; } = null!;
    internal TranslationCatalog Catalog { get; }
    private readonly CultureInfo _culture, _uiCulture;
    private readonly string _language;
    private readonly bool _dark;

    private AzureDevOpsSettingsUiFixture(DetailedSettingsTestScope profile, string language, bool dark)
    {
        Profile = profile; _language = language; _dark = dark;
        _culture = CultureInfo.CurrentCulture; _uiCulture = CultureInfo.CurrentUICulture;
        Catalog = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
    }
    internal static async Task<AzureDevOpsSettingsUiFixture> CreateAsync(string language = "en", bool dark = false, string kind = "worktree",
        Action<DetailedSettingsTestScope>? prepare = null, string remote = Project + "/_git/repository")
    {
        DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync(kind);
        try
        {
            foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes)
            {
                try { _ = profile.Path(scope); Seed(profile, scope); } catch (KeyNotFoundException) { }
            }
            if (profile.Repository is not null)
            {
                bool existing = (await profile.Repository.GetRemotesAsync()).Any(remote => remote.Name == "origin");
                await profile.Git.GitAsync("-C", profile.Repository.Path, "remote", existing ? "set-url" : "add", "origin", remote);
            }
            prepare?.Invoke(profile);
            AzureDevOpsSettingsUiFixture fixture = new(profile, language, dark); await fixture.CreateDialogAsync(); return fixture;
        }
        catch { profile.Dispose(); throw; }
    }
    internal static void Seed(DetailedSettingsTestScope profile, GitDistributedSettingsScope scope, string? project = Project,
        string? token = "fake-token", string? filter = "pipeline.*", string? provider = AzureDevOpsSettings.ProviderName, string enabled = "false")
    {
        List<(string Key, string Value)> entries =
        [
            (AzureDevOpsSettings.EnableKey, enabled), (AzureDevOpsSettings.ShowPageKey, "true"),
            (AzureDevOpsSettings.RepositoryNameKey, "hidden-repository-name"),
            (AppVeyorSettings.AccountNameKey, "untouched-account"), (AppVeyorSettings.AccountTokenKey, "untouched-av-token"),
            (GitHubActionsSettings.OwnerKey, "untouched-owner"), (TeamCitySettings.ServerUrlKey, "https://ci.example.invalid/base"),
            (GitLabSettings.InstanceUrlKey, "https://gitlab.example.invalid"), (GitLabSettings.ApiTokenKey, "untouched-gitlab-token"),
            ("future.λ", "keep <literal> & \nbody")
        ];
        if (project is not null) { entries.Add((AzureDevOpsSettings.ProjectUrlKey, project)); }
        if (token is not null) { entries.Add((AzureDevOpsSettings.ApiTokenKey, token)); }
        if (filter is not null) { entries.Add((AzureDevOpsSettings.BuildDefinitionFilterKey, filter)); }
        if (provider is not null) { entries.Add((AzureDevOpsSettings.ProviderKey, provider)); }
        profile.Write(scope, entries.ToArray());
    }
    private async Task CreateDialogAsync()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(_language);
        DetailedSettingsSession detailed = await Profile.SessionAsync();
        Session = new(Profile.Repository, detailed.Workspace); await Session.InitializeAsync(default);
        Dialog = new(Profile.Desktop.Load(), Catalog, Profile.Repository is not null, initialPage: "BuildServerIntegration",
            detailedSettings: detailed, gitHubActionsSettings: Session,
            openSettingsUrlAsync: url => { OpenedUrls.Add(url); LaunchBrowser?.Invoke(url); return Task.CompletedTask; })
            { RequestedThemeVariant = _dark ? ThemeVariant.Dark : ThemeVariant.Light };
        ProviderPage = (BuildServerIntegrationSettingsPage)typeof(DesktopSettingsDialog)
            .GetField("_buildServerSettingsPage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Dialog)!;
        Page.ReadClipboardAsync = token => ReadClipboard is { } read ? read(token) : Task.FromResult(ClipboardText);
        Page.ClientFactory = settings =>
        {
            AzureDevOpsPageTestClient client = new(settings, (s, id, token) => Lookup(s, id, token), DisposeClient);
            Clients.Add(client); return client;
        };
    }
    internal async Task ShowAsync()
    { Owner.Show(); Modal = Dialog.ShowDialog<bool>(Owner); await UntilAsync(() => Session.Loaded && Field<Button>("ApplyDesktopSettings").IsEnabled); Pump(); }
    internal async Task ReopenAsync()
    {
        if (Dialog.IsVisible) { Dialog.Close(); await UntilAsync(() => !Dialog.IsVisible); }
        await CreateDialogAsync(); Modal = Dialog.ShowDialog<bool>(Owner); await UntilAsync(() => Field<Button>("ApplyDesktopSettings").IsEnabled); Pump();
    }
    internal T Field<T>(string name) where T : Control => Dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    internal void Select(GitDistributedSettingsScope scope) => Click("BuildServerIntegrationSettingsPage" + scope);
    internal void Click(string name) { Pump(); PushProcessTestDriver.Pointer(Field<Control>(name)); Pump(); }
    internal void Edit(string name, string value)
    {
        Click(name); Press(PhysicalKey.A, RawInputModifiers.Control); Press(PhysicalKey.Backspace);
        if (value.Length > 0) { Dialog.KeyTextInput(value); } Pump(); Assert.That(Field<TextBox>(name).Text, Is.EqualTo(value));
    }
    internal async Task ProviderAsync(string provider)
    { await AvatarUiTestDriver.Choose(Dialog, Field<ComboBox>("BuildServerProvider"), provider); Pump(); }
    internal void Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { Dialog.KeyPressQwerty(key, modifiers); if (Dialog.IsVisible) { Dialog.KeyReleaseQwerty(key, modifiers); Pump(); } }
    internal void Pump()
    { Dispatcher.UIThread.RunJobs(); if (Dialog.IsVisible) { Dialog.UpdateLayout(); } AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    internal async Task UntilAsync(Func<bool> condition, string? reason = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); await Task.Delay(5); }
        Pump(); Assert.That(condition(), Is.True, reason ?? "The real Azure DevOps Settings page did not reach the expected state in ten seconds.");
    }
    internal Task SavedAsync() => UntilAsync(() => !Session.HasChanges && Field<Button>("ApplyDesktopSettings").IsEnabled && string.IsNullOrEmpty(Field<TextBlock>("SettingsNotice").Text));
    internal Task SaveFailedAsync() => UntilAsync(() => Field<Button>("ApplyDesktopSettings").IsEnabled && !string.IsNullOrEmpty(Field<TextBlock>("SettingsNotice").Text));
    internal async Task IdleAsync() { await UntilAsync(() => !Page.IsBusy); await Page.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Pump(); }
    internal async Task AcknowledgeAsync(string expected, string? title = null)
    {
        await UntilAsync(() => Dialog.OwnedWindows.Any(window => window.IsVisible));
        Window notice = Dialog.OwnedWindows.Single(window => window.IsVisible);
        Assert.That(notice.Title, Is.EqualTo(title ?? Catalog.Translate("SettingsUserControl", "_failToExtractDataFromClipboardCaption", "Text", "Could not extract data")));
        Assert.That(notice.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == expected), Is.True);
        Button ok = notice.GetVisualDescendants().OfType<Button>().Single(); PushProcessTestDriver.Pointer(ok);
        await UntilAsync(() => !notice.IsVisible);
    }
    internal TaskCompletionSource<T> Gate<T>(T cleanupValue)
    { TaskCompletionSource<T> gate = new(TaskCreationOptions.RunContinuationsAsynchronously); ReleaseOnDispose.Add(() => gate.TrySetResult(cleanupValue)); return gate; }
    public async ValueTask DisposeAsync()
    {
        foreach (Action release in ReleaseOnDispose) { release(); }
        try
        {
            Task drain = Page.BeginClose(); if (Dialog.IsVisible) { Dialog.Close(); }
            await UntilAsync(() => drain.IsCompleted); await drain.WaitAsync(TimeSpan.FromSeconds(5));
            if (Dialog.IsVisible) { await UntilAsync(() => !Dialog.IsVisible); }
            Owner.Close();
        }
        finally { Profile.Dispose(); CultureInfo.CurrentCulture = _culture; CultureInfo.CurrentUICulture = _uiCulture; }
    }
}

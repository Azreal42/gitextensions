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

internal sealed class GitLabPageTestClient(GitLabSettings settings,
    Func<GitLabSettings, string, string, CancellationToken, Task<GitLabProject>> read,
    Func<Task>? dispose = null) : IGitLabClient
{
    internal GitLabSettings Settings { get; } = settings;
    internal List<(string Namespace, string Name, CancellationToken Token)> Requests { get; } = [];
    internal bool Disposed { get; private set; }
    internal int RuntimeCalls { get; private set; }
    public Task<GitLabProject> GetProjectAsync(string projectNamespace, string projectName, CancellationToken cancellationToken = default)
    { Requests.Add((projectNamespace, projectName, cancellationToken)); return read(Settings, projectNamespace, projectName, cancellationToken); }
    public Task<GitLabSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default)
    { RuntimeCalls++; throw new InvalidOperationException("The Settings page must not poll CI."); }
    public Uri? BuildPageUri(GitLabPipeline pipeline)
    { RuntimeCalls++; throw new InvalidOperationException("The Settings page must not open pipeline pages."); }
    public void Dispose() => Disposed = true;
    public async ValueTask DisposeAsync() { if (dispose is not null) { await dispose(); } Disposed = true; }
}

internal sealed class GitLabSettingsUiFixture : IAsyncDisposable
{
    internal DetailedSettingsTestScope Profile { get; }
    internal GitHubActionsSettingsSession Session { get; }
    internal DesktopSettingsDialog Dialog { get; }
    internal BuildServerIntegrationSettingsPage ProviderPage { get; }
    internal GitLabSettingsPage Page => ProviderPage.GitLabPage;
    internal Window Owner { get; } = new() { Width = 1040, Height = 740 };
    internal List<GitLabPageTestClient> Clients { get; } = [];
    internal List<string> OpenedUrls { get; } = [];
    internal List<Action> ReleaseOnDispose { get; } = [];
    internal Func<GitLabSettings, string, string, CancellationToken, Task<GitLabProject>> Lookup { get; set; }
        = (_, _, _, _) => Task.FromResult(new GitLabProject(107));
    internal Func<Task>? DisposeClient { get; set; }
    internal Task<bool> Modal { get; private set; } = null!;
    private readonly CultureInfo _culture, _uiCulture;
    private GitLabSettingsUiFixture(DetailedSettingsTestScope profile, GitHubActionsSettingsSession session,
        DetailedSettingsSession detailed, string language, bool dark)
    {
        Profile = profile; Session = session; _culture = CultureInfo.CurrentCulture; _uiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        Dialog = new(profile.Desktop.Load(), TranslationCatalog.LoadDefault(), true, initialPage: "BuildServerIntegration",
            detailedSettings: detailed, gitHubActionsSettings: session,
            openSettingsUrlAsync: url => { OpenedUrls.Add(url); return Task.CompletedTask; })
            { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        ProviderPage = (BuildServerIntegrationSettingsPage)typeof(DesktopSettingsDialog)
            .GetField("_buildServerSettingsPage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Dialog)!;
        Page.ClientFactory = settings =>
        {
            GitLabPageTestClient client = new(settings, (s, ns, name, token) => Lookup(s, ns, name, token), DisposeClient);
            Clients.Add(client); return client;
        };
    }
    internal static async Task<GitLabSettingsUiFixture> CreateAsync(string language = "en", bool dark = false,
        Action<DetailedSettingsTestScope>? prepare = null)
    {
        DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        try
        {
            foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes) { Seed(profile, scope); }
            await profile.Git.GitAsync("remote", "add", "origin", "git@gitlab.example:group/subgroup/repository.git");
            await profile.Git.GitAsync("remote", "add", "other", "https://other.gitlab.example/other/project.git");
            prepare?.Invoke(profile);
            DetailedSettingsSession detailed = await profile.SessionAsync();
            GitHubActionsSettingsSession session = new(profile.Repository, detailed.Workspace); await session.InitializeAsync(default);
            return new(profile, session, detailed, language, dark);
        }
        catch { profile.Dispose(); throw; }
    }
    internal static void Seed(DetailedSettingsTestScope f, GitDistributedSettingsScope scope, string? id = "71", string? instance = "https://gitlab.example", string? token = "fake-token", string? provider = "Gitlab")
    {
        List<(string Key, string Value)> values = [(GitLabSettings.EnableKey, "false"), (GitLabSettings.ShowPageKey, "true"),
            (GitLabSettings.PagesLimitKey, "future-raw-limit"), ("future.λ", "keep <literal> & \nbody")];
        if (provider is not null) { values.Add((GitLabSettings.ProviderKey, provider)); }
        if (instance is not null) { values.Add((GitLabSettings.InstanceUrlKey, instance)); }
        if (id is not null) { values.Add((GitLabSettings.ProjectIdKey, id)); }
        if (token is not null) { values.Add((GitLabSettings.ApiTokenKey, token)); }
        f.Write(scope, values.ToArray());
    }
    internal async Task ShowAsync()
    {
        Owner.Show(); Modal = Dialog.ShowDialog<bool>(Owner);
        await UntilAsync(() => Session.Loaded && Field<Button>("ApplyDesktopSettings").IsEnabled); Pump();
    }
    internal T Field<T>(string name) where T : Control => Dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    internal void Select(GitDistributedSettingsScope scope) => Click("BuildServerIntegrationSettingsPage" + scope);
    internal void Click(string name) { Pump(); PushProcessTestDriver.Pointer(Field<Control>(name)); Pump(); }
    internal void Edit(string name, string value)
    {
        Click(name); Press(PhysicalKey.A, RawInputModifiers.Control); Press(PhysicalKey.Backspace);
        if (value.Length > 0) { Dialog.KeyTextInput(value); } Pump(); Assert.That(Field<TextBox>(name).Text, Is.EqualTo(value));
    }
    internal void Provider(string name)
    {
        Click("BuildServerProvider"); Press(PhysicalKey.Home);
        int offset = name switch { "Gitlab" => 3, "TeamCity" => 2, "GitHub Actions" => 1, _ => 0 };
        for (int index = 0; index < offset; index++) { Press(PhysicalKey.ArrowDown); }
        Press(PhysicalKey.Enter); Assert.That(Field<ComboBox>("BuildServerProvider").SelectedItem, Is.EqualTo(name)); Pump();
    }
    internal void Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { Dialog.KeyPressQwerty(key, modifiers); if (Dialog.IsVisible) { Dialog.KeyReleaseQwerty(key, modifiers); Pump(); } }
    internal void Pump()
    {
        Dispatcher.UIThread.RunJobs(); if (Dialog.IsVisible) { Dialog.UpdateLayout(); }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }
    internal async Task UntilAsync(Func<bool> condition, string? reason = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); await Task.Delay(5); }
        Pump(); Assert.That(condition(), Is.True, reason ?? "The real Settings window did not reach its expected state within ten seconds.");
    }
    internal async Task IdleAsync() { await UntilAsync(() => !Page.IsBusy); await Page.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Pump(); }
    internal async Task SavedAsync() => await UntilAsync(() => !Session.HasChanges && Field<Button>("ApplyDesktopSettings").IsEnabled
        && string.IsNullOrEmpty(Field<TextBlock>("SettingsNotice").Text));
    internal TaskCompletionSource<T> Gate<T>()
    {
        TaskCompletionSource<T> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ReleaseOnDispose.Add(() => gate.TrySetCanceled()); return gate;
    }
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

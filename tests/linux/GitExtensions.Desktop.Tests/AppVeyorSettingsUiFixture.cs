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

internal sealed class AppVeyorSettingsUiFixture : IAsyncDisposable
{
    internal DetailedSettingsTestScope Profile { get; }
    internal GitHubActionsSettingsSession Session { get; private set; } = null!;
    internal DesktopSettingsDialog Dialog { get; private set; } = null!;
    internal BuildServerIntegrationSettingsPage ProviderPage { get; private set; } = null!;
    internal AppVeyorSettingsPage Page => ProviderPage.AppVeyorPage;
    internal Window Owner { get; } = new() { Width = 1040, Height = 740 };
    internal List<string> OpenedUrls { get; } = [];
    internal Task<bool> Modal { get; private set; } = null!;
    private readonly CultureInfo _culture, _uiCulture;
    private readonly string _language;
    private readonly bool _dark;
    private AppVeyorSettingsUiFixture(DetailedSettingsTestScope profile, string language, bool dark)
    {
        Profile = profile; _language = language; _dark = dark;
        _culture = CultureInfo.CurrentCulture; _uiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
    }
    internal static async Task<AppVeyorSettingsUiFixture> CreateAsync(string language = "en", bool dark = false, string kind = "worktree",
        Action<DetailedSettingsTestScope>? prepare = null)
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture, originalUiCulture = CultureInfo.CurrentUICulture;
        DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync(kind);
        try
        {
            foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes.Where(scope => profile.XmlPaths.Contains(profilePath(scope))))
            { Seed(profile, scope); }
            prepare?.Invoke(profile); AppVeyorSettingsUiFixture result = new(profile, language, dark); await result.CreateDialogAsync(); return result;
            string profilePath(GitDistributedSettingsScope scope)
            { try { return profile.Path(scope); } catch (KeyNotFoundException) { return ""; } }
        }
        catch { profile.Dispose(); CultureInfo.CurrentCulture = originalCulture; CultureInfo.CurrentUICulture = originalUiCulture; throw; }
    }
    internal static void Seed(DetailedSettingsTestScope profile, GitDistributedSettingsScope scope, string? project = "sample-account/project-one|other/project-two",
        string? loadTests = "future-bool", string? provider = "AppVeyor")
    {
        List<(string Key, string Value)> entries =
        [
            (AppVeyorSettings.EnableKey, "false"), (AppVeyorSettings.ShowPageKey, "true"),
            (AppVeyorSettings.AccountNameKey, "sample-account"), (AppVeyorSettings.AccountTokenKey, "fake-token"),
            (GitHubActionsSettings.ApiUrlKey, "https://api.github.com"), (GitHubActionsSettings.OwnerKey, "untouched-owner"),
            (GitHubActionsSettings.RepositoryKey, "untouched-repository"), (GitHubActionsSettings.TokenKey, "untouched-gha-token"),
            (TeamCitySettings.ServerUrlKey, "https://ci.example.invalid/base"), (TeamCitySettings.ProjectNameKey, "untouched-project"),
            (TeamCitySettings.BuildIdFilterKey, "untouched.*"), (GitLabSettings.InstanceUrlKey, "https://gitlab.example.invalid"),
            (GitLabSettings.ProjectIdKey, "71"), (GitLabSettings.ApiTokenKey, "untouched-gitlab-token"),
            ("future.λ", "keep <literal> & \nbody")
        ];
        if (project is not null) { entries.Add((AppVeyorSettings.ProjectNameKey, project)); }
        if (loadTests is not null) { entries.Add((AppVeyorSettings.LoadTestsResultsKey, loadTests)); }
        if (provider is not null) { entries.Add((AppVeyorSettings.ProviderKey, provider)); }
        profile.Write(scope, entries.ToArray());
    }
    private async Task CreateDialogAsync()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(_language);
        DetailedSettingsSession detailed = await Profile.SessionAsync();
        Session = new(Profile.Repository, detailed.Workspace); await Session.InitializeAsync(default);
        Dialog = new(Profile.Desktop.Load(), TranslationCatalog.LoadDefault(), Profile.Repository is not null,
            initialPage: "BuildServerIntegration", detailedSettings: detailed, gitHubActionsSettings: Session,
            openSettingsUrlAsync: url => { OpenedUrls.Add(url); return Task.CompletedTask; })
            { RequestedThemeVariant = _dark ? ThemeVariant.Dark : ThemeVariant.Light };
        ProviderPage = (BuildServerIntegrationSettingsPage)typeof(DesktopSettingsDialog)
            .GetField("_buildServerSettingsPage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Dialog)!;
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
    internal async Task ProviderAsync(string name)
    { await AvatarUiTestDriver.Choose(Dialog, Field<ComboBox>("BuildServerProvider"), name); Pump(); }
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
        Pump(); Assert.That(condition(), Is.True, reason ?? "The Settings window did not reach the expected state in ten seconds.");
    }
    internal Task SavedAsync() => UntilAsync(() => !Session.HasChanges && Field<Button>("ApplyDesktopSettings").IsEnabled
        && string.IsNullOrEmpty(Field<TextBlock>("SettingsNotice").Text));
    internal async Task SaveFailedAsync() => await UntilAsync(() => Field<Button>("ApplyDesktopSettings").IsEnabled && !string.IsNullOrEmpty(Field<TextBlock>("SettingsNotice").Text));
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Dialog.IsVisible) { Dialog.Close(); await UntilAsync(() => !Dialog.IsVisible); }
            Owner.Close(); await UntilAsync(() => !Owner.IsVisible);
        }
        finally { Profile.Dispose(); CultureInfo.CurrentCulture = _culture; CultureInfo.CurrentUICulture = _uiCulture; }
    }
}

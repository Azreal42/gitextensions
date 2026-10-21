using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitUIPluginInterfaces.BuildServerIntegration;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GitHubHostingSettingsDialogTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task AuthenticationIsOwnedMaskedAndBufferedUntilExplicitParentSave(string language, bool dark)
    {
        using Profile profile = new(); CultureInfo previous = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        Window owner = new() { Width = 740, Height = 440 }; owner.Show();
        GitHubHostingSettingsDialog dialog = new(profile.Store) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        Task<bool> done = dialog.ShowDialog<bool>(owner);
        try
        {
            Pump(dialog); PushProcessTestDriver.Pointer(Field<Button>(dialog, "GitHubAuthentication")); Pump(dialog);
            BuildServerCredentialsDialog credentials = dialog.OwnedWindows.OfType<BuildServerCredentialsDialog>().Single();
            RadioButton bearer = credentials.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "radioButtonBearerToken");
            PushProcessTestDriver.Pointer(bearer); Pump(credentials);
            TextBox token = credentials.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "textBoxBearerToken");
            Assert.That(token.PasswordChar, Is.Not.EqualTo('\0')); token.Text = "explicit-test-hosting-token";
            PushProcessTestDriver.Pointer(credentials.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "buttonOK")); Pump(dialog);
            Assert.That(File.Exists(profile.Store.FilePath), Is.False, "Accepting the child is only a parent draft.");
            Assert.That(Field<TextBlock>(dialog, "GitHubAuthenticationMode").Text, Is.Not.Empty);
            Capture(dialog, "github-hosting-settings-" + language + (dark ? "-dark.png" : "-light.png"));
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "GitHubSaveSettings")); Pump(owner);
            Assert.That(await done.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            IReadOnlyDictionary<string, string?> saved = profile.Store.Load();
            Assert.That(GitHubHostingSettings.FromPluginSettings(saved).CredentialsFromPluginSettings(saved).BearerToken, Is.EqualTo("explicit-test-hosting-token"));
            Assert.That(saved[GitHubHostingSettings.TokenKey], Is.EqualTo("explicit-test-hosting-token"));
        }
        finally { dialog.Close(); owner.Close(); CultureInfo.CurrentUICulture = previous; }
    }

    [AvaloniaTest]
    public async Task ChangedApiCannotSilentlyReuseThePreviousHostingTokenAndCancelPreservesProfileBytes()
    {
        using Profile profile = new(); GitHubHostingSettings settings = new();
        profile.Store.SaveChanges(profile.Store.Load(), settings.WithPersonalAccessToken(new Dictionary<string, string?>(), "original-hosting-token"));
        byte[] before = File.ReadAllBytes(profile.Store.FilePath); Window owner = new() { Width = 740, Height = 440 }; owner.Show();
        GitHubHostingSettingsDialog dialog = new(profile.Store); Task<bool> done = dialog.ShowDialog<bool>(owner);
        try
        {
            Pump(dialog); Field<TextBox>(dialog, "GitHubApiUrl").Text = "https://enterprise.example/api/v3";
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "GitHubSaveSettings")); Pump(dialog);
            Assert.That(dialog.IsVisible, Is.True); Assert.That(done.IsCompleted, Is.False);
            Assert.That(Field<TextBlock>(dialog, "GitHubHostingSettingsError").Text, Does.Contain("authentication"));
            Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(before));
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "GitHubCancelSettings")); Pump(owner); Assert.That(await done.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
            Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(before));
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    public async Task TokenLinksRequireAnExplicitClickAndUseTheConfiguredWebHost()
    {
        using Profile profile = new(); List<string> opened = []; Window owner = new() { Width = 740, Height = 440 }; owner.Show();
        GitHubHostingSettingsDialog dialog = new(profile.Store, openUrl: url => { opened.Add(url); return Task.CompletedTask; });
        Task<bool> done = dialog.ShowDialog<bool>(owner);
        try
        {
            Pump(dialog); Assert.That(opened, Is.Empty); Field<TextBox>(dialog, "GitHubWebUrl").Text = "https://enterprise.example";
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "GitHubManageTokens")); Pump(dialog);
            Assert.That(opened, Is.EqualTo(new[] { "https://enterprise.example/settings/tokens" }));
            Assert.That(File.Exists(profile.Store.FilePath), Is.False); dialog.Close(false); Assert.That(await done.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        }
        finally { dialog.Close(); owner.Close(); }
    }
    private static T Field<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") is not { Length: > 0 } directory) { return; }
        Directory.CreateDirectory(directory); window.CaptureRenderedFrame()!.Save(Path.Combine(directory, name));
    }
    private sealed class Profile : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "github-hosting-settings-" + Guid.NewGuid().ToString("N"));
        internal GitHubHostingPreferencesStore Store { get; }
        internal Profile() => Store = new(Root);
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }
}

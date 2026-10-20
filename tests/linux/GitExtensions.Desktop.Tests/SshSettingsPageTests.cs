using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class SshSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("fr", true)]
    public void OriginalClientControlsUseCompactSharedChromeAndCaptureLiteralPathsSynchronously(string culture, bool dark)
    {
        SshSettingsPage page = new("", Catalog(culture)); using Host host = new(page, dark);
        Assert.That(Field<RadioButton>(host.Window, "OpenSSH").IsChecked, Is.True);
        Assert.That(Field<TextBox>(host.Window, "OtherSsh").IsEffectivelyEnabled, Is.False);
        PushProcessTestDriver.Pointer(Field<RadioButton>(host.Window, "Other")); Pump(host.Window);
        Assert.That(page.Validate(out _), Is.False);
        string literal = "/some ssh λ ' $(literal)";
        Field<TextBox>(host.Window, "OtherSsh").Text = literal;
        Assert.That(page.Draft, Is.EqualTo(literal)); Assert.That(page.Validate(out _), Is.True);
        Assert.That(Field<Button>(host.Window, "OtherSshBrowse").IsEffectivelyEnabled, Is.True);
        Assert.That(Field<Button>(host.Window, "OtherSshBrowse").Content, Is.EqualTo(culture == "fr" ? "Parcourir" : "Browse"));
        Assert.That(Field<RadioButton>(host.Window, "Other").Content, Is.EqualTo(culture == "fr" ? "Autre client ssh" : "Other ssh client"));
        Field<TextBox>(host.Window, "OtherSsh").Text = "invalid\0ssh"; Assert.That(page.Validate(out _), Is.False);
        Field<RadioButton>(host.Window, "OpenSSH").IsChecked = true;
        Assert.That(page.Draft, Is.Empty); Assert.That(page.Validate(out _), Is.True);
        Assert.That(Field<TextBox>(host.Window, "OtherSsh").IsEffectivelyEnabled, Is.False);
        page.AcceptSaved(literal); Assert.That(page.Draft, Is.EqualTo(literal)); Assert.That(page.IsDirty, Is.False);
        string? directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS");
        if (directory is not null)
        {
            Pump(host.Window); using var bitmap = host.Window.CaptureRenderedFrame(); Assert.That(bitmap, Is.Not.Null);
            bitmap!.Save(Path.Combine(directory, "ssh-settings-" + culture + ".png"));
        }
    }

    [AvaloniaTest]
    public void ReloadAdoptsUntouchedPublicationAndRetainsAnExplicitNativeOrCustomDraft()
    {
        SshSettingsPage page = new("old", Catalog()); using Host host = new(page);
        page.RebasePublished("external"); Assert.That(page.Draft, Is.EqualTo("external")); Assert.That(page.IsDirty, Is.False);
        Field<TextBox>(host.Window, "OtherSsh").Text = "pending";
        page.RebasePublished("new published"); Assert.That(page.Draft, Is.EqualTo("pending"));
        Field<RadioButton>(host.Window, "OpenSSH").IsChecked = true;
        page.RebasePublished("another external"); Assert.That(page.Draft, Is.Empty); Assert.That(page.IsDirty, Is.True);
    }

    [AvaloniaTest]
    public void EditingApplyingOrReloadingCannotDisplayAnotherExecutablesVersionCheck()
    {
        SshSettingsPage page = new("ssh A", Catalog()); using Host host = new(page);
        GitExecutableDiagnostic probe = new("ssh A", new GitResult(0, "version", ""), null);
        GitEnvironmentDiagnostic diagnostic = new(probe, probe, probe, null, null, null, [])
        { SshConfiguration = new(GitSshCommandSource.GitSsh) };
        page.SetDiagnostic(diagnostic);
        Assert.That(Field<TextBlock>(host.Window, "SshEnvironmentInformation").Text, Does.Contain("ssh A"));
        page.AcceptSaved("ssh B"); Assert.That(Field<TextBlock>(host.Window, "SshEnvironmentInformation").Text, Is.Empty);
        page.SetDiagnostic(diagnostic); Field<TextBox>(host.Window, "OtherSsh").Text = "ssh C";
        Assert.That(Field<TextBlock>(host.Window, "SshEnvironmentInformation").Text, Is.Empty);
        page.SetDiagnostic(diagnostic); page.RebasePublished("ssh D");
        Assert.That(Field<TextBlock>(host.Window, "SshEnvironmentInformation").Text, Is.Empty);
        Assert.That(page.Draft, Is.EqualTo("ssh C"));
    }

    [AvaloniaTest]
    public async Task PickerIsOwnedUntilItsActualReturnAndCannotPublishAfterInvalidation()
    {
        TaskCompletionSource<string?> chosen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SshSettingsPage page = new("initial", Catalog()) { PickExecutable = _ => chosen.Task }; using Host host = new(page);
        Task picking = page.BrowseAsync(); Assert.That(page.IsBusy, Is.True);
        Assert.That(page.OperationCompletion.IsCompleted, Is.False);
        Assert.That(Field<Button>(host.Window, "OtherSshBrowse").IsEffectivelyEnabled, Is.False);
        page.Invalidate(); chosen.SetResult("late executable"); await picking;
        Assert.That(page.IsBusy, Is.False); Assert.That(page.OperationCompletion.IsCompleted, Is.True);
        Assert.That(page.Draft, Is.EqualTo("initial")); Assert.That(page.IsDirty, Is.False);
    }

    [AvaloniaTest]
    public async Task FailedPickerKeepsTheDraftAndAllowsARealRetry()
    {
        SshSettingsPage page = new("initial", Catalog()) { PickExecutable = _ => throw new IOException("picker failure") }; using Host host = new(page);
        await page.BrowseAsync(); Assert.That(Field<TextBlock>(host.Window, "SshSettingsError").Text, Is.EqualTo("picker failure"));
        page.PickExecutable = _ => Task.FromResult<string?>("chosen ssh"); await page.BrowseAsync();
        Assert.That(page.Draft, Is.EqualTo("chosen ssh")); Assert.That(page.IsBusy, Is.False);
    }

    internal static TranslationCatalog Catalog(string culture = "en") => TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(culture));
    internal static T Field<T>(Control control, string name) where T : Control => control.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    internal static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private sealed class Host : IDisposable
    {
        internal Window Window { get; }
        internal Host(SshSettingsPage page, bool dark = false)
        { Window = new() { Width = 800, Height = 430, Content = page, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; Window.Show(); Pump(Window); }
        public void Dispose() => Window.Close();
    }
}

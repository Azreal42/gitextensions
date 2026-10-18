using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Desktop.Controls.Graph;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DetailedSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalGroupsAreFullyReadableAtSettingsWidthAndGraphIsEditableOnlyInGlobal(string culture, bool dark)
    {
        await using DetailedSettingsUiHost f = await DetailedSettingsUiHost.CreateAsync(culture: culture, dark: dark);
        Dictionary<string, byte[]> before = f.Profile.Material();
        Assert.That(f.Dialog.Width, Is.EqualTo(958)); Assert.That(f.Field<RadioButton>("DetailedEffectiveRB").IsChecked, Is.True);
        foreach (string name in DetailedSettingsUiHost.GraphNames.Concat(new[] { "chkRemotesFromServer", "addLogMessages", "nbMessages" }))
        { Assert.That(f.Field<Control>(name).IsEffectivelyEnabled, Is.False, "The initial Effective view is read-only: " + name); }
        foreach (string scope in new[] { "Local", "Distributed" })
        {
            f.Select(scope);
            foreach (string name in DetailedSettingsUiHost.GraphNames) { Assert.That(f.Field<CheckBox>(name).IsEffectivelyEnabled, Is.False); }
            Assert.That(f.Field<CheckBox>("chkRemotesFromServer").IsEffectivelyEnabled, Is.True);
            Assert.That(f.Field<TextBox>("nbMessages").IsEffectivelyEnabled, Is.True);
        }
        f.Select("Global");
        foreach (string name in DetailedSettingsUiHost.GraphNames) { Assert.That(f.Field<CheckBox>(name).IsEffectivelyEnabled, Is.True); }
        f.AssertFullLabels(); f.Capture("detailed-settings-" + culture + (dark ? "-dark.png" : "-light.png"));
        Assert.That(f.Dialog.ActualThemeVariant, Is.EqualTo(dark ? ThemeVariant.Dark : ThemeVariant.Light));
        f.Profile.AssertMaterial(before); Assert.That(f.Publications, Is.Zero);
    }

    [AvaloniaTest]
    [TestCase("none")] [TestCase("bare")]
    public async Task NoRepositoryShowsOnlyGlobalWhileBareRepositoriesDisableDistributed(string kind)
    {
        await using DetailedSettingsUiHost f = await DetailedSettingsUiHost.CreateAsync(kind);
        Assert.That(f.Field<RadioButton>("DetailedGlobalRB").IsVisible, Is.True);
        if (kind == "none")
        {
            foreach (string scope in new[] { "Effective", "Local", "Distributed" }) { Assert.That(f.Field<RadioButton>("Detailed" + scope + "RB").IsVisible, Is.False); }
            Assert.That(f.Field<RadioButton>("DetailedGlobalRB").IsChecked, Is.True);
            Assert.That(f.Field<CheckBox>(DetailedSettingsUiHost.GraphNames[0]).IsEffectivelyEnabled, Is.True);
        }
        else
        {
            Assert.That(f.Field<RadioButton>("DetailedEffectiveRB").IsChecked, Is.True);
            Assert.That(f.Field<RadioButton>("DetailedLocalRB").IsEffectivelyEnabled, Is.True);
            Assert.That(f.Field<RadioButton>("DetailedDistributedRB").IsVisible, Is.True);
            Assert.That(f.Field<RadioButton>("DetailedDistributedRB").IsEffectivelyEnabled, Is.False);
            f.Select("Local"); Assert.That(f.Field<CheckBox>("chkRemotesFromServer").IsEffectivelyEnabled, Is.True);
        }
    }

    [AvaloniaTest]
    public async Task NativeThreeStateClicksRepresentTrueFalseAndUnsetRatherThanForcingInheritedValues()
    {
        await using DetailedSettingsUiHost f = await DetailedSettingsUiHost.CreateAsync(); f.Select("Global");
        CheckBox remote = f.Field<CheckBox>("chkRemotesFromServer"); Assert.That(remote.IsThreeState, Is.True); Assert.That(remote.IsChecked, Is.False);
        f.Click("chkRemotesFromServer"); Assert.That(remote.IsChecked, Is.True);
        f.Click("chkRemotesFromServer"); Assert.That(remote.IsChecked, Is.Null);
        f.Page.PrepareSave(); Assert.That(f.Session.Read(GitDistributedSettingsScope.Global).RemoteBranches, Is.Null);
        f.Click("chkRemotesFromServer"); Assert.That(remote.IsChecked, Is.False);
        f.Page.PrepareSave(); Assert.That(f.Session.Read(GitDistributedSettingsScope.Global).RemoteBranches, Is.False);
        f.Select("Effective"); Assert.That(remote.IsThreeState, Is.False); Assert.That(remote.IsEffectivelyEnabled, Is.False);
    }

    [AvaloniaTest]
    [TestCase("-2147483648", "-2147483648", false)]
    [TestCase("-1", "-1", false)] [TestCase("0", "0", false)]
    [TestCase("2147483647", "2147483647", false)]
    [TestCase("2147483648", null, true)] [TestCase("-2147483649", null, true)]
    [TestCase("not a number", null, true)] [TestCase("", null, false)] [TestCase("  +42 ", "42", false)]
    public async Task NativeNumberEditingPersistsInt32WithoutClampingAndInvalidOrEmptyTextUnsets(string typed, string? expected, bool invalid)
    {
        await using DetailedSettingsUiHost f = await DetailedSettingsUiHost.CreateAsync(); f.Select("Global"); f.Edit("nbMessages", typed);
        TextBox count = f.Field<TextBox>("nbMessages");
        if (invalid) { Assert.That((count.Foreground as ISolidColorBrush)?.Color, Is.EqualTo(Colors.Red)); }
        f.Press(PhysicalKey.Enter); Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Publications, Is.Zero, "Enter inside the numeric text field must not accept the Settings dialog.");
        f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        GitXmlSettingsSnapshot saved = await f.Profile.ReadAsync(GitDistributedSettingsScope.Global);
        Assert.That(saved.Values.GetValueOrDefault(DetailedRepositorySettings.MergeLogCount), Is.EqualTo(expected));
        Assert.That(count.Text, Is.EqualTo(expected ?? ""));
        Assert.That(saved.Values["future.λ"], Is.EqualTo("keep <literal> & \nbody"));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ScopeSwitchingAndApplyPreserveUnknownRawValuesUnlessTheSpecificControlWasEdited(bool editCount)
    {
        await using DetailedSettingsUiHost f = await DetailedSettingsUiHost.CreateAsync(prepareProfile: profile =>
        {
            foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes)
            { profile.Write(scope, (DetailedRepositorySettings.RemoteBranches, "future-boolean"), (DetailedRepositorySettings.AddMergeLog, "future-log"), (DetailedRepositorySettings.MergeLogCount, "2147483648"), ("future.λ", "literal <unknown> & body")); }
        });
        Dictionary<string, byte[]> before = f.Profile.XmlPaths.ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
        foreach (string scope in new[] { "Local", "Distributed", "Global" })
        {
            f.Select(scope); Assert.That(f.Field<CheckBox>("chkRemotesFromServer").IsChecked, Is.Null);
            Assert.That(f.Field<CheckBox>("addLogMessages").IsChecked, Is.Null); Assert.That(f.Field<TextBox>("nbMessages").Text, Is.Empty);
        }
        if (editCount) { f.Edit("nbMessages", "37"); }
        f.Click("ApplyDesktopSettings"); await f.SavedAsync();
        foreach (GitDistributedSettingsScope scope in DetailedSettingsTestScope.EditableScopes)
        {
            GitXmlSettingsSnapshot saved = await f.Profile.ReadAsync(scope);
            Assert.That(saved.Values[DetailedRepositorySettings.RemoteBranches], Is.EqualTo("future-boolean"));
            Assert.That(saved.Values[DetailedRepositorySettings.AddMergeLog], Is.EqualTo("future-log")); Assert.That(saved.Values["future.λ"], Is.EqualTo("literal <unknown> & body"));
            Assert.That(saved.Values[DetailedRepositorySettings.MergeLogCount], Is.EqualTo(editCount && scope == GitDistributedSettingsScope.Global ? "37" : "2147483648"));
            if (!editCount || scope != GitDistributedSettingsScope.Global) { Assert.That(File.ReadAllBytes(saved.Path), Is.EqualTo(before[saved.Path]), "An untouched scope must preserve even its original XML formatting."); }
        }
    }

    [AvaloniaTest]
    public async Task SwitchingScopesKeepsIndependentDraftsAndCancelWritesNothing()
    {
        await using DetailedSettingsUiHost f = await DetailedSettingsUiHost.CreateAsync(); Dictionary<string, byte[]> before = f.Profile.Material();
        f.Select("Local"); f.Click("chkRemotesFromServer"); f.Edit("nbMessages", "31");
        f.Select("Distributed"); f.Click("addLogMessages"); f.Edit("nbMessages", "32");
        f.Select("Global"); f.Click(DetailedSettingsUiHost.GraphNames[0]); f.Click("chkRemotesFromServer"); f.Edit("nbMessages", "33");
        f.Select("Local"); Assert.That(f.Field<CheckBox>("chkRemotesFromServer").IsChecked, Is.True); Assert.That(f.Field<TextBox>("nbMessages").Text, Is.EqualTo("31"));
        f.Select("Distributed"); Assert.That(f.Field<CheckBox>("addLogMessages").IsChecked, Is.True); Assert.That(f.Field<TextBox>("nbMessages").Text, Is.EqualTo("32"));
        f.Select("Global"); Assert.That(f.Field<CheckBox>(DetailedSettingsUiHost.GraphNames[0]).IsChecked, Is.False); Assert.That(f.Field<TextBox>("nbMessages").Text, Is.EqualTo("33"));
        f.Profile.AssertMaterial(before); f.Click("CancelDesktopSettings"); Assert.That(await f.Completion.WaitAsync(TimeSpan.FromSeconds(20)), Is.False);
        f.Profile.AssertMaterial(before); Assert.That(f.Publications, Is.Zero); Assert.That(f.OwnerGraph.Options, Is.EqualTo(new RevisionGraphOptions()));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ApplyAndOkPublishOnlySavedGraphAndExplicitScopeIntentions(bool close)
    {
        await using DetailedSettingsUiHost f = await DetailedSettingsUiHost.CreateAsync(); byte[] distributed = File.ReadAllBytes(f.Profile.Path(GitDistributedSettingsScope.Distributed));
        f.Select("Global"); f.Click(DetailedSettingsUiHost.GraphNames[0]); f.Edit("nbMessages", "42");
        f.Select("Local"); f.Click("chkRemotesFromServer"); f.Click(close ? "SaveDesktopSettings" : "ApplyDesktopSettings");
        if (close) { Assert.That(await f.Completion.WaitAsync(TimeSpan.FromSeconds(20)), Is.True); }
        else { await f.SavedAsync(); Assert.That(f.Dialog.IsVisible, Is.True); }
        Assert.That(f.Publications, Is.EqualTo(1)); Assert.That(f.Profile.Desktop.Load().MergeGraphLanesHavingCommonParent, Is.False);
        Assert.That(f.OwnerGraph.Options.MergeGraphLanesHavingCommonParent, Is.False);
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values[DetailedRepositorySettings.MergeLogCount], Is.EqualTo("42"));
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Local)).Values[DetailedRepositorySettings.RemoteBranches], Is.EqualTo("true"));
        Assert.That(File.ReadAllBytes(f.Profile.Path(GitDistributedSettingsScope.Distributed)), Is.EqualTo(distributed));
        if (!close) { f.Click("CancelDesktopSettings"); Assert.That(await f.Completion, Is.True, "Apply accepts the actual saved state before a later Cancel closes the host."); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ARealPartialHostSaveKeepsPublishedGraphAndLocalFileThenCancelOrRetryPreservesPeers(bool retry)
    {
        await using DetailedSettingsUiHost f = await DetailedSettingsUiHost.CreateAsync();
        f.Select("Local"); f.Click("chkRemotesFromServer");
        f.Select("Distributed"); f.Click("addLogMessages");
        f.Select("Global"); f.Click(DetailedSettingsUiHost.GraphNames[0]); f.Edit("nbMessages", "33");
        using DetailedSettingsTestScope.BlockedFile block = new(f.Profile.Path(GitDistributedSettingsScope.Distributed));
        f.Click("ApplyDesktopSettings");
        await DetailedSettingsUiHost.Until(() => f.Field<Button>("ApplyDesktopSettings").IsEnabled && f.Field<TextBlock>("SettingsNotice").Text?.Contains("Some settings may already have been saved", StringComparison.Ordinal) == true);
        Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Publications, Is.EqualTo(1));
        Assert.That(f.OwnerGraph.Options.MergeGraphLanesHavingCommonParent, Is.False);
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Local)).Values[DetailedRepositorySettings.RemoteBranches], Is.EqualTo("true"));
        if (!retry)
        {
            f.Click("CancelDesktopSettings"); Assert.That(await f.Completion, Is.False);
            Assert.That(f.Profile.Desktop.Load().MergeGraphLanesHavingCommonParent, Is.False);
            Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values[DetailedRepositorySettings.MergeLogCount], Is.EqualTo("20"));
            return;
        }
        GitXmlSettingsSnapshot local = await f.Profile.ReadAsync(GitDistributedSettingsScope.Local);
        await GitXmlSettingsStore.SaveChangesAsync(local, new Dictionary<string, string?> { [DetailedRepositorySettings.RemoteBranches] = "false", ["peer.after-partial"] = "retained" });
        byte[] localPeer = File.ReadAllBytes(local.Path); DesktopPreferences peer = f.Profile.Desktop.Load() with { MergeGraphLanesHavingCommonParent = true };
        f.Profile.Desktop.Save(peer); DetailedGraphSettings.PublishSaved(f.Profile.Desktop.FilePath, peer);
        block.Restore(); f.Click("ApplyDesktopSettings"); await f.SavedAsync(expectedPublications: 2);
        Assert.That(File.ReadAllBytes(local.Path), Is.EqualTo(localPeer));
        Assert.That(f.Profile.Desktop.Load().MergeGraphLanesHavingCommonParent, Is.True);
        Assert.That(f.OwnerGraph.Options.MergeGraphLanesHavingCommonParent, Is.True);
        f.Select("Global"); Assert.That(f.Field<CheckBox>(DetailedSettingsUiHost.GraphNames[0]).IsChecked, Is.True);
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Distributed)).Values[DetailedRepositorySettings.AddMergeLog], Is.EqualTo("true"));
        Assert.That((await f.Profile.ReadAsync(GitDistributedSettingsScope.Global)).Values[DetailedRepositorySettings.MergeLogCount], Is.EqualTo("33"));
    }

    [AvaloniaTest]
    public async Task StandaloneHostInitializesItsGlobalScopeAsynchronouslyBeforeEditingOrSaving()
    {
        await using DetailedSettingsUiHost f = await DetailedSettingsUiHost.CreateAsync("none", standalone: true);
        Assert.That(f.Dialog.DetailedSettingsSession.Loaded, Is.True); Assert.That(f.Session.HasRepository, Is.False);
        Assert.That(f.Field<RadioButton>("DetailedGlobalRB").IsChecked, Is.True);
        Assert.That(f.Field<CheckBox>("chkRemotesFromServer").IsEffectivelyEnabled, Is.True);
        f.Click("CancelDesktopSettings"); Assert.That(await f.Completion, Is.False); Assert.That(f.Publications, Is.Zero);
    }

    [AvaloniaTest]
    public async Task AnUnreadableXmlScopeShowsItsErrorWithoutEnablingRepositoryEditsOrChangingTheFile()
    {
        using DetailedSettingsTestScope profile = await DetailedSettingsTestScope.CreateAsync();
        File.WriteAllText(profile.Path(GitDistributedSettingsScope.Local), "<dictionary><broken>");
        byte[] before = File.ReadAllBytes(profile.Path(GitDistributedSettingsScope.Local));
        DetailedSettingsSession session = new(profile.Repository, profile.Desktop.Load()); await session.InitializeAsync(default);
        Assert.That(session.LoadError, Is.Not.Null); Assert.That(session.Loaded, Is.False);
        DetailedSettingsPage page = new(session, TranslationCatalog.LoadDefault()); Window window = new() { Content = page, Width = 958, Height = 646 };
        try
        {
            window.Show(); DetailedSettingsUiHost.Pump(window);
            Assert.That(PushProcessTestDriver.Field<TextBlock>(window, "DetailedSettingsNotice").Text, Is.Not.Empty);
            Assert.That(PushProcessTestDriver.Field<CheckBox>(window, "chkRemotesFromServer").IsEffectivelyEnabled, Is.False);
            Assert.That(PushProcessTestDriver.Field<TextBox>(window, "nbMessages").IsEffectivelyEnabled, Is.False);
            Assert.That(File.ReadAllBytes(profile.Path(GitDistributedSettingsScope.Local)), Is.EqualTo(before));
        }
        finally { window.Close(); }
    }
}

internal sealed class DetailedSettingsUiHost : IAsyncDisposable
{
    internal static readonly string[] GraphNames = ["chkMergeGraphLanesHavingCommonParent", "chkRenderGraphWithDiagonals", "chkStraightenGraphDiagonals"];
    internal DetailedSettingsTestScope Profile { get; }
    internal DesktopSettingsDialog Dialog { get; private set; } = null!;
    internal DetailedSettingsSession Session => Dialog.DetailedSettingsSession;
    internal DetailedSettingsPage Page => Dialog.GetVisualDescendants().OfType<DetailedSettingsPage>().Single();
    internal Window Owner { get; } = new() { Width = 1000, Height = 760 };
    internal CommitGraph OwnerGraph { get; } = new();
    internal Task<bool> Completion { get; private set; } = null!;
    internal int Publications { get; private set; }
    private DetailedSettingsUiHost(DetailedSettingsTestScope profile) => Profile = profile;
    internal static async Task<DetailedSettingsUiHost> CreateAsync(string repositoryKind = "worktree", string culture = "en", bool dark = false, bool standalone = false, Action<DetailedSettingsTestScope>? prepareProfile = null)
    {
        DetailedSettingsUiHost f = new(await DetailedSettingsTestScope.CreateAsync(repositoryKind));
        try
        {
            prepareProfile?.Invoke(f.Profile);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture); DesktopPreferences initial = f.Profile.Desktop.Load();
            GeneralSettingsSession general = new(initial); AppearanceSettingsSession appearance = new(initial, new HistoryViewPreferencesStore().Load());
            DetailedSettingsSession? detailed = standalone ? null : await f.Profile.SessionAsync();
            DetailedGraphSettings.Bind(f.OwnerGraph); f.Owner.Content = f.OwnerGraph; f.Owner.Show();
            f.Dialog = new(initial, TranslationCatalog.LoadDefault(), f.Profile.Repository is not null,
                detailedSettings: detailed, publishSettings: settings =>
                {
                    DesktopPreferences merged = appearance.MergeDesktop(settings.AppearanceSettingsResult, settings.Result);
                    merged = settings.DetailedSettingsSession.MergeDesktop(merged);
                    GeneralSettingsPublication saved = general.Save(settings.GeneralSettingsResult, merged, accepted =>
                    {
                        appearance.DesktopSaved(settings.AppearanceSettingsResult); settings.DetailedSettingsSession.DesktopSaved(accepted);
                        DetailedGraphSettings.PublishSaved(f.Profile.Desktop.FilePath, accepted); f.Publications++;
                    });
                    settings.AcceptGeneralSaved(saved);
                    AppearanceSettingsPublication published = appearance.Save(settings.AppearanceSettingsResult, saved.Desktop, new HistoryViewPreferencesStore().Load());
                    settings.AcceptAppearanceSaved(published); return Task.CompletedTask;
                }) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            f.Completion = f.Dialog.ShowDialog<bool>(f.Owner);
            await Until(() => f.Session.Loaded && f.Field<Button>("ApplyDesktopSettings").IsEnabled);
            SettingsNavigationTestDriver.Select(f.Dialog, "Detailed"); Pump(f.Dialog); return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    internal T Field<T>(string name) where T : Control => PushProcessTestDriver.Field<T>(Dialog, name);
    internal void Select(string scope) => Click("Detailed" + scope + "RB");
    internal void Click(string name) { Pump(Dialog); PushProcessTestDriver.Pointer(Field<Control>(name)); Pump(Dialog); }
    internal void Edit(string name, string text)
    { Click(name); Press(PhysicalKey.A, RawInputModifiers.Control); Press(PhysicalKey.Backspace); if (text.Length > 0) { Dialog.KeyTextInput(text); } Pump(Dialog); Assert.That(Field<TextBox>(name).Text, Is.EqualTo(text)); }
    internal void Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { Dialog.KeyPressQwerty(key, modifiers); if (Dialog.IsVisible) { Dialog.KeyReleaseQwerty(key, modifiers); Pump(Dialog); } }
    internal Task SavedAsync(int expectedPublications = 1) => Until(() => Publications == expectedPublications && Field<Button>("ApplyDesktopSettings").IsEnabled && string.IsNullOrEmpty(Field<TextBlock>("SettingsNotice").Text));
    internal void AssertFullLabels()
    {
        Pump(Dialog); Rect viewport = new(default, Dialog.ClientSize);
        foreach (TextBlock text in Page.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text)))
        {
            Assert.That(text.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False, text.Text);
            Assert.That(text.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(text.Bounds.Width + .5), text.Text);
            Rect rect = new(text.TranslatePoint(default, Dialog)!.Value, text.Bounds.Size);
            Assert.That(rect.Left, Is.GreaterThanOrEqualTo(viewport.Left - .5), text.Text); Assert.That(rect.Right, Is.LessThanOrEqualTo(viewport.Right + .5), text.Text);
            Assert.That(rect.Top, Is.GreaterThanOrEqualTo(viewport.Top - .5), text.Text); Assert.That(rect.Bottom, Is.LessThanOrEqualTo(viewport.Bottom + .5), text.Text);
        }
        foreach (string name in GraphNames.Concat(new[] { "chkRemotesFromServer", "addLogMessages", "nbMessages" }))
        { Control control = Field<Control>(name); Assert.That(control.IsEffectivelyVisible, Is.True, name); Assert.That(control.Bounds.Height, Is.GreaterThan(0), name); }
    }
    internal void Capture(string name)
    {
        Pump(Dialog); using var frame = Dialog.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); Assert.That(frame!.PixelSize.Width, Is.EqualTo(958));
        string root = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(root);
        string path = System.IO.Path.Combine(root, name); frame.Save(path); TestContext.AddTestAttachment(path);
    }
    internal static void Pump(TopLevel root)
    { for (int frame = 0; frame < 6; frame++) { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    internal static async Task Until(Func<bool> condition)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20)); while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); } }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Dialog is not null && Dialog.IsVisible) { Dialog.Close(); await Until(() => !Dialog.IsVisible); }
            Owner.Close(); await Until(() => !Owner.IsVisible);
        }
        finally { Profile.Dispose(); }
    }
}

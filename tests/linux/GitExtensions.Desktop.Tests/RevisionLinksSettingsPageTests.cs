using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
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

[NonParallelizable]
public sealed class RevisionLinksSettingsPageTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalFieldsIconsAndCaptionsAreReadableAtSettingsWidth(string culture, bool dark)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        f.Write(GitDistributedSettingsScope.Global, (GitRevisionLinkCodec.SettingName, GitRevisionLinkCodec.Encode(GitRevisionLinkTemplates.Create(GitRevisionLinkTemplate.GitHub, []))!));
        RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        RevisionLinksSettingsPage page = new(session, TranslationCatalog.LoadDefault());
        // The actual Settings content width includes its original 190px navigation column.
        Grid host = new() { ColumnDefinitions = new("190,*"), Margin = new(8) }; Grid.SetColumn(page, 1); host.Children.Add(page);
        Window window = new() { Width = 958, Height = 720, Content = host, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; window.Show();
        try
        {
            Pump(window); Assert.That(Field<RadioButton>(page, "RevisionLinksEffectiveRB").IsChecked, Is.True);
            Assert.That(Field<TextBox>(page, "_NO_TRANSLATE_Name").IsEffectivelyEnabled, Is.False);
            Click(window, Field<RadioButton>(page, "RevisionLinksGlobalRB")); Assert.That(Field<TextBox>(page, "_NO_TRANSLATE_Name").IsEffectivelyEnabled, Is.True);
            foreach (string name in new[] { "_NO_TRANSLATE_Name", "_NO_TRANSLATE_UseRemotes", "_NO_TRANSLATE_RemotePatern", "_NO_TRANSLATE_SearchPatternEdit", "_NO_TRANSLATE_NestedPatternEdit", "LinksGrid", "Add", "Remove" })
            {
                Control control = Field<Control>(page, name); Assert.That(control.IsEffectivelyVisible, Is.True, name);
                Point position = control.TranslatePoint(default, window)!.Value;
                Assert.That(position.X, Is.GreaterThanOrEqualTo(0)); Assert.That(position.X + control.Bounds.Width, Is.LessThanOrEqualTo(window.ClientSize.Width + .5), name);
            }
            foreach (TextBlock text in page.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text)))
            {
                Assert.That(text.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False, text.Text);
                Assert.That(text.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(text.Bounds.Width + .5), text.Text);
            }
            Assert.That(Field<Button>(page, "Add").GetVisualDescendants().OfType<Image>().Single().Tag, Is.EqualTo("FileStatusAdded.png"));
            Assert.That(Field<Button>(page, "Remove").GetVisualDescendants().OfType<Image>().Single().Tag, Is.EqualTo("FileStatusRemoved.png"));
            foreach (string name in new[] { "Add", "Remove" })
            {
                Button button = Field<Button>(page, name);
                double contentRight = button.Bounds.Width - button.BorderThickness.Right - button.Padding.Right;
                foreach (Control child in button.GetVisualDescendants().OfType<Control>().Where(control => control is TextBlock or Image))
                {
                    Point position = child.TranslatePoint(default, button)!.Value;
                    double fullWidth = child is TextBlock label ? label.TextLayout.WidthIncludingTrailingWhitespace : child.Bounds.Width;
                    Assert.That(position.X, Is.GreaterThanOrEqualTo(button.BorderThickness.Left + button.Padding.Left - .5), name + " content starts inside the button");
                    Assert.That(position.X + fullWidth, Is.LessThanOrEqualTo(contentRight + .5), name + " complete translated caption/icon must fit within button content bounds");
                    Assert.That(position.X + child.Bounds.Width, Is.LessThanOrEqualTo(contentRight + .5), name + " actual child bounds must not be clipped by the button");
                    Point inPage = child.TranslatePoint(default, page)!.Value;
                    Assert.That(inPage.X + Math.Max(fullWidth, child.Bounds.Width), Is.LessThanOrEqualTo(page.Bounds.Width + .5), name + " child must also fit within the page");
                }
            }
            string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory);
            using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
            string path = Path.Combine(directory, "revision-links-" + culture + (dark ? "-dark.png" : "-light.png")); frame!.Save(path); TestContext.AddTestAttachment(path);
        }
        finally { await page.BeginClose(); window.Close(); }
    }

    [AvaloniaTest]
    public async Task NativeManualAddFieldsAndFormatRowPersistOnlyAfterExplicitSaveAndRegexTrimDoesNotTrimName()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        RevisionLinksSettingsPage page = new(session, TranslationCatalog.LoadDefault()); Window window = new() { Content = page, Width = 958, Height = 700 }; window.Show();
        Dictionary<string, byte[]> before = f.Material();
        try
        {
            Click(window, Field<RadioButton>(page, "RevisionLinksGlobalRB")); Click(window, Field<Button>(page, "Add"));
            Assert.That(Field<TextBox>(page, "_NO_TRANSLATE_Name").Text, Is.EqualTo("<new>"));
            Assert.That(Field<TextBox>(page, "_NO_TRANSLATE_UseRemotes").Text, Is.EqualTo("upstream|origin"));
            Assert.That(Field<CheckBox>(page, "chkOnlyFirstRemote").IsChecked, Is.True);
            Edit(window, Field<TextBox>(page, "_NO_TRANSLATE_Name"), "  name with spaces  ");
            Edit(window, Field<TextBox>(page, "_NO_TRANSLATE_SearchPatternEdit"), "  #(\\d+)  ");
            TextBox caption = page.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "RevisionLinkCaption");
            Edit(window, caption, "Literal <a> #{0}");
            TextBox uri = page.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "RevisionLinkUri"); Edit(window, uri, "https://example.invalid/{0}?a=<b>&q=été");
            await page.PrepareSaveAsync(default); f.AssertMaterial(before);
            var draft = session.Read(GitDistributedSettingsScope.Global).Single(); Assert.That(draft.Name, Is.EqualTo("  name with spaces  ")); Assert.That(draft.SearchPattern, Is.EqualTo("#(\\d+)"));
            Assert.That(draft.LinkFormats.Single().Format, Is.EqualTo("https://example.invalid/{0}?a=<b>&q=été"));
            await session.SaveFilesAsync(default); page.AcceptSaved();
            var saved = GitRevisionLinkCodec.Decode((await f.ReadAsync(GitDistributedSettingsScope.Global)).Values[GitRevisionLinkCodec.SettingName]).Definitions.Single();
            Assert.That(saved.Name, Is.EqualTo(draft.Name)); Assert.That(saved.LinkFormats.Single().Caption, Is.EqualTo("Literal <a> #{0}"));
            Assert.That((await f.ReadAsync(GitDistributedSettingsScope.Global)).Values["future.λ"], Is.EqualTo("keep <literal> & \nbody"));
        }
        finally { await page.BeginClose(); window.Close(); }
    }

    [AvaloniaTest]
    public async Task SwitchingScopesPreservesIndependentDraftsAndClosingWithoutSaveWritesNothing()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        RevisionLinksSettingsPage page = new(session, TranslationCatalog.LoadDefault()); Window window = new() { Content = page, Width = 958, Height = 700 }; window.Show(); Dictionary<string, byte[]> before = f.Material();
        try
        {
            foreach (string scope in new[] { "Local", "Distributed", "Global" })
            { Click(window, Field<RadioButton>(page, "RevisionLinks" + scope + "RB")); Click(window, Field<Button>(page, "Add")); Edit(window, Field<TextBox>(page, "_NO_TRANSLATE_Name"), scope); }
            Click(window, Field<RadioButton>(page, "RevisionLinksEffectiveRB"));
            Assert.That(Field<ListBox>(page, "_NO_TRANSLATE_Categories").ItemCount, Is.EqualTo(3)); Assert.That(Field<Button>(page, "Add").IsEffectivelyEnabled, Is.False);
            Click(window, Field<RadioButton>(page, "RevisionLinksLocalRB")); Assert.That(Field<TextBox>(page, "_NO_TRANSLATE_Name").Text, Is.EqualTo("Local"));
            await page.BeginClose(); f.AssertMaterial(before);
        }
        finally { await page.BeginClose(); window.Close(); }
    }

    [AvaloniaTest]
    public async Task TemplateMenuUsesTheCurrentRemoteAndRemoveSelectsTheAdjacentCategory()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        await f.Git.GitAsync("remote", "add", "origin", "https://github.com/new-owner/live-repo.git");
        RevisionLinksSettingsPage page = new(session, TranslationCatalog.LoadDefault()); Window window = new() { Content = page, Width = 958, Height = 700 }; window.Show();
        try
        {
            Click(window, Field<RadioButton>(page, "RevisionLinksGlobalRB")); Button arrow = Field<Button>(page, "AddTemplateDropDown"); Click(window, arrow);
            MenuItem github = arrow.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "AddGitHubTemplates");
            await DetailedSettingsUiHost.Until(() => github.IsEffectivelyVisible); PushProcessTestDriver.Pointer(github); await page.Completion; Pump(window);
            Assert.That(session.Read(GitDistributedSettingsScope.Global).Count, Is.EqualTo(3));
            Assert.That(session.Read(GitDistributedSettingsScope.Global)[0].LinkFormats[0].Format, Is.EqualTo("https://github.com/new-owner/live-repo/commit/%COMMIT_HASH%"));
            Click(window, Field<Button>(page, "Remove")); Assert.That(Field<TextBox>(page, "_NO_TRANSLATE_Name").Text, Is.EqualTo("GitHub - Issues"));
            Assert.That(session.Read(GitDistributedSettingsScope.Global).Count, Is.EqualTo(2));
        }
        finally { await page.BeginClose(); window.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task LateTemplateRequestCannotEditAnotherScopeAndCloseCancelsThenDrains(bool close)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        TaskCompletionSource entered = new(), release = new(); CancellationToken requestToken = default;
        RevisionLinksSettingsPage page = new(session, TranslationCatalog.LoadDefault(), async token =>
        { requestToken = token; entered.SetResult(); await release.Task; return new GitRevisionLinkRemote[] { new("origin", "https://github.com/live/repo", null) }; });
        List<bool> busy = []; page.BusyChanged += () => busy.Add(page.IsBusy);
        Window window = new() { Content = page, Width = 958, Height = 700 }; window.Show();
        try
        {
            Click(window, Field<RadioButton>(page, "RevisionLinksGlobalRB")); Button arrow = Field<Button>(page, "AddTemplateDropDown"); Click(window, arrow);
            MenuItem github = arrow.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "AddGitHubTemplates");
            await DetailedSettingsUiHost.Until(() => github.IsEffectivelyVisible); PushProcessTestDriver.Pointer(github); await entered.Task;
            Assert.That(page.IsBusy, Is.True); Assert.That(Field<Button>(page, "Add").IsEffectivelyEnabled, Is.False);
            Task? closing = null;
            if (close)
            {
                closing = page.BeginClose(); Assert.That(requestToken.IsCancellationRequested, Is.True);
                Assert.That(closing.IsCompleted, Is.False, "A request that has not stopped is still owned by the closing page.");
            }
            else
            {
                // Exercise a late scope change from the host; a disabled human control cannot initiate this.
                Field<RadioButton>(page, "RevisionLinksLocalRB").IsChecked = true;
            }
            release.SetResult(); await page.Completion; if (closing is not null) { await closing; }
            Assert.That(session.Read(GitDistributedSettingsScope.Global), Is.Empty); Assert.That(session.Read(GitDistributedSettingsScope.Local), Is.Empty);
            Assert.That(busy, Is.EqualTo(new[] { true, false })); Assert.That(page.IsBusy, Is.False);
        }
        finally { release.TrySetResult(); await page.BeginClose(); window.Close(); }
    }

    [AvaloniaTest]
    [TestCase("none")] [TestCase("bare")]
    public async Task TopologyDisablesOnlyUnavailableScopes(string kind)
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(kind); RevisionLinksSettingsSession session = new(f.Repository); await session.InitializeAsync(default);
        RevisionLinksSettingsPage page = new(session, TranslationCatalog.LoadDefault()); Window window = new() { Content = page, Width = 958, Height = 700 }; window.Show();
        try
        {
            Pump(window); Assert.That(Field<RadioButton>(page, "RevisionLinksGlobalRB").IsVisible, Is.True);
            if (kind == "none")
            { Assert.That(Field<RadioButton>(page, "RevisionLinksLocalRB").IsVisible, Is.False); Assert.That(Field<Button>(page, "Add").IsEffectivelyEnabled, Is.True); }
            else
            { Assert.That(Field<RadioButton>(page, "RevisionLinksDistributedRB").IsVisible, Is.True); Assert.That(Field<RadioButton>(page, "RevisionLinksDistributedRB").IsEffectivelyEnabled, Is.False); }
        }
        finally { await page.BeginClose(); window.Close(); }
    }

    private static T Field<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(Window window) => DetailedSettingsUiHost.Pump(window);
    private static void Click(Window window, Control control) { Pump(window); PushProcessTestDriver.Pointer(control); Pump(window); }
    private static void Edit(Window window, TextBox box, string text)
    {
        Click(window, box); window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control); window.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control);
        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        window.KeyTextInput(text); Pump(window); Assert.That(box.Text, Is.EqualTo(text));
    }
}

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using GitUIPluginInterfaces.BuildServerIntegration;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class BuildServerCredentialsDialogTests
{
    [AvaloniaTest]
    [TestCase(BuildServerCredentialsType.Guest)]
    [TestCase(BuildServerCredentialsType.UsernameAndPassword)]
    [TestCase(BuildServerCredentialsType.BearerToken)]
    public async Task ThreeRealRadioModesKeepInactiveValuesAndOkReturnsANewLiteralDraft(BuildServerCredentialsType mode)
    {
        BuildServerCredentials input = Credentials(mode); var before = Snapshot(input);
        Window owner = new(); BuildServerCredentialsDialog dialog = new("https://auth.example.invalid/team/repo", input);
        try
        {
            owner.Show(); Task<BuildServerCredentials?> completion = dialog.ShowDialog<BuildServerCredentials?>(owner); Pump(dialog);
            AssertMode(dialog, mode); Assert.That(dialog.Result, Is.Null);
            Click(Field<RadioButton>(dialog, "radioButtonAuthenticatedUser"));
            Edit(dialog, "textBoxUserName", "  fake user λ  "); Edit(dialog, "textBoxPassword", " fake PAT $' λ ");
            Click(Field<RadioButton>(dialog, "radioButtonBearerToken"));
            Edit(dialog, "textBoxBearerToken", " fake bearer +/=_ λ ");
            Click(Field<RadioButton>(dialog, "radioButtonGuestAccess")); AssertMode(dialog, BuildServerCredentialsType.Guest);
            Assert.That(Field<TextBox>(dialog, "textBoxUserName").Text, Is.EqualTo("  fake user λ  "));
            Assert.That(Field<TextBox>(dialog, "textBoxPassword").Text, Is.EqualTo(" fake PAT $' λ "));
            Assert.That(Field<TextBox>(dialog, "textBoxBearerToken").Text, Is.EqualTo(" fake bearer +/=_ λ "));
            Assert.That(Snapshot(input), Is.EqualTo(before), "No edit or mode switch may mutate the supplied model.");
            string selected = mode switch
            {
                BuildServerCredentialsType.UsernameAndPassword => "radioButtonAuthenticatedUser",
                BuildServerCredentialsType.BearerToken => "radioButtonBearerToken", _ => "radioButtonGuestAccess"
            };
            Click(Field<RadioButton>(dialog, selected)); AssertMode(dialog, mode);
            Click(Field<Button>(dialog, "buttonOK")); Pump(owner);
            BuildServerCredentials? result = await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(result, Is.Not.Null); Assert.That(result, Is.SameAs(dialog.Result)); Assert.That(result, Is.Not.SameAs(input));
            Assert.That(result!.BuildServerCredentialsType, Is.EqualTo(mode));
            Assert.That(result.Username, Is.EqualTo("  fake user λ  ")); Assert.That(result.Password, Is.EqualTo(" fake PAT $' λ ")); Assert.That(result.BearerToken, Is.EqualTo(" fake bearer +/=_ λ "));
            Assert.That(Snapshot(input), Is.EqualTo(before));
            result.Password = "changed accepted clone"; Assert.That(Snapshot(input), Is.EqualTo(before));
            Assert.That(FieldValueAfterClose(dialog, "textBoxPassword"), Is.Empty); Assert.That(FieldValueAfterClose(dialog, "textBoxBearerToken"), Is.Empty);
        }
        finally { if (dialog.IsVisible) { dialog.Close(); } owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase("Cancel")]
    [TestCase("Escape")]
    [TestCase("WindowClose")]
    public async Task CancelEscapeAndChromeReturnNullAfterRealSecretEditsWithoutMutatingInput(string close)
    {
        BuildServerCredentials input = Credentials(BuildServerCredentialsType.UsernameAndPassword); var before = Snapshot(input);
        Window owner = new(); BuildServerCredentialsDialog dialog = new("fake endpoint", input);
        try
        {
            owner.Show(); Task<BuildServerCredentials?> completion = dialog.ShowDialog<BuildServerCredentials?>(owner); Pump(dialog);
            Edit(dialog, "textBoxPassword", "discard this fake secret");
            Click(Field<RadioButton>(dialog, "radioButtonBearerToken")); Edit(dialog, "textBoxBearerToken", "discard this fake token");
            if (close == "Cancel") { Click(Field<Button>(dialog, "buttonCancel")); }
            else if (close == "Escape") { Press(dialog, PhysicalKey.Escape); }
            else { dialog.Close(); }
            Pump(owner); Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.Null); Assert.That(dialog.Result, Is.Null); Assert.That(Snapshot(input), Is.EqualTo(before));
            Assert.That(owner.IsVisible, Is.True); Assert.That(dialog.IsVisible, Is.False);
        }
        finally { if (dialog.IsVisible) { dialog.Close(); } owner.Close(); }
    }

    [AvaloniaTest]
    public async Task ConstructorClonesImmediatelyAndDefaultEnterAcceptsTheCapturedValues()
    {
        BuildServerCredentials input = Credentials(BuildServerCredentialsType.BearerToken); var captured = Snapshot(input);
        BuildServerCredentialsDialog dialog = new("unique {literal} λ", input);
        input.BuildServerCredentialsType = BuildServerCredentialsType.Guest; input.Username = "external user"; input.Password = "external password"; input.BearerToken = "external token";
        var external = Snapshot(input); Window owner = new();
        try
        {
            owner.Show(); Task<BuildServerCredentials?> completion = dialog.ShowDialog<BuildServerCredentials?>(owner); Pump(dialog);
            AssertMode(dialog, BuildServerCredentialsType.BearerToken);
            Assert.That(Field<TextBox>(dialog, "textBoxBearerToken").IsKeyboardFocusWithin, Is.True);
            Press(dialog, PhysicalKey.Enter); Pump(owner);
            BuildServerCredentials? accepted = await completion.WaitAsync(TimeSpan.FromSeconds(10)); Assert.That(accepted, Is.Not.Null);
            Assert.That(Snapshot(accepted!), Is.EqualTo(captured)); Assert.That(Snapshot(input), Is.EqualTo(external));
            Assert.That(dialog.Result, Is.SameAs(accepted));
        }
        finally { if (dialog.IsVisible) { dialog.Close(); } owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase(BuildServerCredentialsType.Guest)]
    [TestCase(BuildServerCredentialsType.UsernameAndPassword)]
    [TestCase(BuildServerCredentialsType.BearerToken)]
    public async Task EnterFromEachFocusedRadioAcceptsTheSelectedModeAndKeepsInactiveValues(BuildServerCredentialsType mode)
    {
        BuildServerCredentials input = Credentials(BuildServerCredentialsType.UsernameAndPassword); var before = Snapshot(input);
        Window owner = new(); BuildServerCredentialsDialog dialog = new("fake endpoint", input);
        try
        {
            owner.Show(); Task<BuildServerCredentials?> completion = dialog.ShowDialog<BuildServerCredentials?>(owner); Pump(dialog);
            string name = mode switch
            {
                BuildServerCredentialsType.UsernameAndPassword => "radioButtonAuthenticatedUser",
                BuildServerCredentialsType.BearerToken => "radioButtonBearerToken", _ => "radioButtonGuestAccess"
            };
            RadioButton radio = Field<RadioButton>(dialog, name); Click(radio); AssertMode(dialog, mode);
            Assert.That(radio.IsKeyboardFocusWithin, Is.True, "The real pointer gesture must leave Enter on the selected radio.");
            Press(dialog, PhysicalKey.Enter); Pump(owner);
            BuildServerCredentials? accepted = await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(accepted, Is.Not.Null); Assert.That(accepted, Is.SameAs(dialog.Result)); Assert.That(accepted, Is.Not.SameAs(input));
            Assert.That(Snapshot(accepted!), Is.EqualTo((mode, before.Item2, before.Item3, before.Item4)));
            Assert.That(Snapshot(input), Is.EqualTo(before)); Assert.That(dialog.IsVisible, Is.False);
        }
        finally { if (dialog.IsVisible) { dialog.Close(); } owner.Close(); }
    }

    [AvaloniaTest]
    public async Task EnterOnTheCancelButtonReachedByTabStillCancelsWithoutAcceptingCredentials()
    {
        BuildServerCredentials input = Credentials(BuildServerCredentialsType.Guest); var before = Snapshot(input);
        Window owner = new(); BuildServerCredentialsDialog dialog = new("fake endpoint", input);
        try
        {
            owner.Show(); Task<BuildServerCredentials?> completion = dialog.ShowDialog<BuildServerCredentials?>(owner); Pump(dialog);
            Button cancel = Field<Button>(dialog, "buttonCancel");
            for (int index = 0; index < 12 && !cancel.IsKeyboardFocusWithin; index++) { Press(dialog, PhysicalKey.Tab); }
            Assert.That(cancel.IsKeyboardFocusWithin, Is.True, "Real Tab navigation must reach Cancel before Enter.");
            Press(dialog, PhysicalKey.Enter); Pump(owner);
            Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.Null);
            Assert.That(dialog.Result, Is.Null); Assert.That(Snapshot(input), Is.EqualTo(before)); Assert.That(dialog.IsVisible, Is.False);
        }
        finally { if (dialog.IsVisible) { dialog.Close(); } owner.Close(); }
    }

    [AvaloniaTest]
    public async Task MissingInputStartsAsGuestAndOkCreatesTheOriginalModelWithoutPersistence()
    {
        Window owner = new(); BuildServerCredentialsDialog dialog = new("public.example.invalid");
        try
        {
            owner.Show(); Task<BuildServerCredentials?> completion = dialog.ShowDialog<BuildServerCredentials?>(owner); Pump(dialog);
            AssertMode(dialog, BuildServerCredentialsType.Guest); Assert.That(dialog.Result, Is.Null);
            Press(dialog, PhysicalKey.Enter); Pump(owner);
            BuildServerCredentials? accepted = await completion.WaitAsync(TimeSpan.FromSeconds(10)); Assert.That(accepted, Is.TypeOf<BuildServerCredentials>());
            Assert.That(Snapshot(accepted!), Is.EqualTo((BuildServerCredentialsType.Guest, "", "", "")));
        }
        finally { if (dialog.IsVisible) { dialog.Close(); } owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase("en-US", false)]
    [TestCase("en-US", true)]
    [TestCase("fr-FR", false)]
    [TestCase("fr-FR", true)]
    public async Task OriginalCompactOwnedWindowUsesSharedTranslationsAndMasksActualPasswordAndTokenGlyphs(string culture, bool dark)
    {
        TranslationCatalog translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(culture));
        const string unique = "https://auth.example.invalid/team/{literal}/λ";
        Window owner = new() { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        BuildServerCredentialsDialog dialog = new(unique, Credentials(BuildServerCredentialsType.UsernameAndPassword), translations)
            { RequestedThemeVariant = owner.RequestedThemeVariant };
        try
        {
            owner.Show(); Task<BuildServerCredentials?> completion = dialog.ShowDialog<BuildServerCredentials?>(owner); Pump(dialog);
            Assert.That(dialog.Owner, Is.SameAs(owner)); Assert.That(dialog.ShowInTaskbar, Is.False); Assert.That(dialog.WindowStartupLocation, Is.EqualTo(WindowStartupLocation.CenterOwner));
            Assert.That(dialog.Width, Is.EqualTo(580)); Assert.That(dialog.Height, Is.EqualTo(251)); Assert.That(dialog.MinWidth, Is.EqualTo(373)); Assert.That(dialog.MinHeight, Is.EqualTo(210));
            Assert.That(dialog.CanMinimize || dialog.CanMaximize, Is.False);
            string T(string item, string source)
            {
                string contextual = translations.Translate("FormBuildServerCredentials", item, "Text", source);
                return contextual == source ? translations.Translate(source) : contextual;
            }
            Assert.That(dialog.Title, Is.EqualTo(T("$this", "Enter credentials")));
            Assert.That(Field<TextBlock>(dialog, "labelHeader").Text, Is.EqualTo(string.Format(translations.CurrentCulture, T("labelHeader", "Please enter the credentials for the build server at {0}."), unique)));
            Assert.That(Field<Button>(dialog, "buttonCancel").Content, Is.EqualTo(T("buttonCancel", "Cancel")));
            if (culture == "fr-FR") { Assert.That(Field<Button>(dialog, "buttonCancel").Content, Is.EqualTo("Annuler")); }
            foreach (string name in new[] { "textBoxPassword", "textBoxBearerToken" })
            {
                TextBox input = Field<TextBox>(dialog, name); Assert.That(input.PasswordChar, Is.Not.EqualTo('\0')); Assert.That(input.RevealPassword, Is.False);
                TextPresenter presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single(control => control.Name == "PART_TextPresenter");
                string painted = string.Concat(presenter.TextLayout.TextLines.SelectMany(line => line.TextRuns).Select(run => run.Text.ToString()));
                Assert.That(painted, Is.Not.Empty); Assert.That(painted.All(character => character == input.PasswordChar), Is.True, "The actual shaped text must contain mask glyphs only.");
                Assert.That(input.AcceptsReturn || input.AcceptsTab, Is.False);
            }
            Assert.That(Field<TextBox>(dialog, "textBoxUserName").PasswordChar, Is.EqualTo('\0'));
            foreach (Control control in new Control[] { Field<Button>(dialog, "buttonOK"), Field<Button>(dialog, "buttonCancel"), Field<TextBox>(dialog, "textBoxBearerToken") })
            {
                Point origin = control.TranslatePoint(default, dialog)!.Value;
                Assert.That(new Rect(default, dialog.ClientSize).Contains(new Rect(origin, control.Bounds.Size)), Is.True, control.Name);
            }
            Capture(dialog, $"build-server-credentials-{culture}-{(dark ? "Dark" : "Light")}.png");
            Press(dialog, PhysicalKey.Escape); Pump(owner); Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.Null);
        }
        finally { if (dialog.IsVisible) { dialog.Close(); } owner.Close(); }
    }

    [AvaloniaTest]
    public void UnknownCredentialModeIsRejectedInsteadOfBecomingAnAnonymousLogin()
    {
        BuildServerCredentials input = Credentials((BuildServerCredentialsType)999);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BuildServerCredentialsDialog("fake endpoint", input));
        Assert.That(input.BuildServerCredentialsType, Is.EqualTo((BuildServerCredentialsType)999));
    }

    private static BuildServerCredentials Credentials(BuildServerCredentialsType mode) => new()
    { BuildServerCredentialsType = mode, Username = "fake username λ", Password = "fake password 0123", BearerToken = "fake bearer 9876" };
    private static (BuildServerCredentialsType, string?, string?, string?) Snapshot(IBuildServerCredentials value)
        => (value.BuildServerCredentialsType, value.Username, value.Password, value.BearerToken);
    private static void AssertMode(Window dialog, BuildServerCredentialsType mode)
    {
        Assert.That(Field<RadioButton>(dialog, "radioButtonGuestAccess").IsChecked, Is.EqualTo(mode == BuildServerCredentialsType.Guest));
        Assert.That(Field<RadioButton>(dialog, "radioButtonAuthenticatedUser").IsChecked, Is.EqualTo(mode == BuildServerCredentialsType.UsernameAndPassword));
        Assert.That(Field<RadioButton>(dialog, "radioButtonBearerToken").IsChecked, Is.EqualTo(mode == BuildServerCredentialsType.BearerToken));
        Assert.That(Field<TextBox>(dialog, "textBoxUserName").IsEffectivelyEnabled, Is.EqualTo(mode == BuildServerCredentialsType.UsernameAndPassword));
        Assert.That(Field<TextBox>(dialog, "textBoxPassword").IsEffectivelyEnabled, Is.EqualTo(mode == BuildServerCredentialsType.UsernameAndPassword));
        Assert.That(Field<TextBox>(dialog, "textBoxBearerToken").IsEffectivelyEnabled, Is.EqualTo(mode == BuildServerCredentialsType.BearerToken));
    }
    private static T Field<T>(Window host, string name) where T : Control => host.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static string FieldValueAfterClose(Window dialog, string name)
    {
        // Closed windows can retain their content for callers; the input is cleared independently of Result.
        TextBox? field = dialog.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(item => item.Name == name);
        return field?.Text ?? "";
    }
    private static void Click(Control control) => PushProcessTestDriver.Pointer(control);
    private static void Edit(Window dialog, string name, string value)
    {
        TextBox field = Field<TextBox>(dialog, name); Click(field); Press(dialog, PhysicalKey.A, RawInputModifiers.Control);
        dialog.KeyTextInput(value); Pump(dialog); Assert.That(field.Text, Is.EqualTo(value));
    }
    private static void Press(Window dialog, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { dialog.KeyPressQwerty(key, modifiers); if (dialog.IsVisible) { dialog.KeyReleaseQwerty(key, modifiers); Pump(dialog); } }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(Window dialog, string name)
    {
        Pump(dialog); using var frame = dialog.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
}

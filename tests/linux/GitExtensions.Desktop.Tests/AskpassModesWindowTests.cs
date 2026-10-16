using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AskpassModesWindowTests
{
    [AvaloniaTest]
    public void PointerYesAndNoAreTypedDecisionsWithTheExactProtocolForEachQuestionKind()
    {
        foreach (AskpassPromptKind kind in new[] { AskpassPromptKind.YesNo, AskpassPromptKind.HostVerification, AskpassPromptKind.Permission })
        foreach (bool yes in new[] { true, false })
        {
            AskpassWindow window = Show(kind); SelectableTextBlock prompt = Field<SelectableTextBlock>(window, "AskpassPrompt");
            TextBox? fingerprint = Optional<TextBox>(window, "AskpassFingerprint");
            try
            {
                Assert.That(Optional<TextBox>(window, "AskpassInput"), Is.Null);
                Assert.That(Field<Button>(window, "YesAskpass").IsDefault, Is.True);
                Assert.That(Field<Button>(window, "NoAskpass").IsCancel, Is.False, "Escape must cancel instead of answering No.");
                if (fingerprint is not null) { fingerprint.Text = "unsubmitted private fingerprint"; }
                Pointer(window, yes ? "YesAskpass" : "NoAskpass");
                Assert.That(window.IsVisible, Is.False); Assert.That(window.Result.Decision, Is.EqualTo(yes ? AskpassDecision.Yes : AskpassDecision.No));
                Assert.That(window.Response, Is.EqualTo(yes ? "yes" : "no")); Assert.That(window.Result.Value, Is.Null);
                Assert.That(prompt.Text, Is.Empty); if (fingerprint is not null) { Assert.That(fingerprint.Text, Is.Empty); }
                int code = kind == AskpassPromptKind.Permission && !yes ? 1 : 0;
                AssertOutput(window, code, kind == AskpassPromptKind.Permission ? "" : yes ? "yes" : "no");
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTest]
    public void EnterOnTheQuestionUsesTheOriginalDefaultYesAndNoLateClickCanReplaceIt()
    {
        foreach (AskpassPromptKind kind in new[] { AskpassPromptKind.YesNo, AskpassPromptKind.HostVerification, AskpassPromptKind.Permission })
        {
            AskpassWindow window = Show(kind); Button no = Field<Button>(window, "NoAskpass");
            try
            {
                Assert.That(Field<Button>(window, "YesAskpass").IsKeyboardFocusWithin, Is.True);
                Press(window, PhysicalKey.Enter); Assert.That(window.IsVisible, Is.False); Assert.That(window.Result.Decision, Is.EqualTo(AskpassDecision.Yes));
                no.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.That(window.Result.Decision, Is.EqualTo(AskpassDecision.Yes));
                AssertOutput(window, 0, kind == AskpassPromptKind.Permission ? "" : "yes");
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTest]
    public void EscapeAndWindowCloseCancelEveryInputOrQuestionWithoutReturningNoOrAnyTypedValue()
    {
        foreach (AskpassPromptKind kind in new[] { AskpassPromptKind.Secret, AskpassPromptKind.Username, AskpassPromptKind.YesNo, AskpassPromptKind.HostVerification, AskpassPromptKind.Permission })
        foreach (bool escape in new[] { false, true })
        {
            AskpassWindow window = Show(kind); TextBox? input = Optional<TextBox>(window, "AskpassInput"), fingerprint = Optional<TextBox>(window, "AskpassFingerprint");
            try
            {
                if (input is not null) { input.Text = "private cancelled λ"; }
                if (fingerprint is not null) { fingerprint.Text = "SHA256:private-cancelled"; fingerprint.Focus(); }
                if (escape) { Press(window, PhysicalKey.Escape); } else { window.Close(); }
                Assert.That(window.IsVisible, Is.False); Assert.That(window.Result.Decision, Is.EqualTo(AskpassDecision.Cancelled));
                Assert.That(window.Result.Value, Is.Null); Assert.That(window.Response, Is.Null); AssertOutput(window, 1, "");
                if (input is not null) { Assert.That(input.Text, Is.Empty); } if (fingerprint is not null) { Assert.That(fingerprint.Text, Is.Empty); }
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTest]
    public void FingerprintEnterNeverFallsThroughToYesForEmptyWhitespaceOrProtocolBreakingValues()
    {
        foreach (string invalid in new[] { "", " \t ", "line\nbreak", "line\rbreak", "nul\0byte" })
        {
            AskpassWindow window = Show(AskpassPromptKind.HostVerification);
            try
            {
                TextBox input = Field<TextBox>(window, "AskpassFingerprint"); input.Text = invalid; Assert.That(input.Focus(), Is.True);
                Assert.That(Field<Button>(window, "UseAskpassFingerprint").IsEnabled, Is.False); Press(window, PhysicalKey.Enter);
                Assert.That(window.IsVisible, Is.True, "Invalid fingerprint: " + Convert.ToHexString(Encoding.UTF8.GetBytes(invalid)));
                Assert.That(window.Result.Decision, Is.EqualTo(AskpassDecision.Cancelled)); Assert.That(window.Response, Is.Null); AssertOutput(window, 1, "");
                Field<Button>(window, "UseAskpassFingerprint").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.That(window.IsVisible, Is.True);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTest]
    public void FingerprintEnterAndItsExplicitButtonSubmitLiteralUtf8WithoutANewlineAndClearTheField()
    {
        foreach (bool enter in new[] { true, false })
        {
            AskpassWindow window = Show(AskpassPromptKind.HostVerification);
            try
            {
                const string value = " SHA256:fictitious+λ/MD5:aa:bb\t'quoted' "; TextBox input = Field<TextBox>(window, "AskpassFingerprint");
                input.Text = value; input.Focus(); Assert.That(input.PasswordChar, Is.EqualTo('\0'));
                Assert.That(Field<Button>(window, "UseAskpassFingerprint").IsEnabled, Is.True);
                if (enter) { Press(window, PhysicalKey.Enter); } else { Pointer(window, "UseAskpassFingerprint"); }
                Assert.That(window.IsVisible, Is.False); Assert.That(window.Result.Decision, Is.EqualTo(AskpassDecision.Submit));
                Assert.That(window.Result.Value, Is.EqualTo(value)); Assert.That(window.Response, Is.EqualTo(value)); Assert.That(input.Text, Is.Empty);
                AssertOutput(window, 0, value);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTest]
    public void AnExplicitYesClickRemainsAQuestionAnswerEvenWhileTheFingerprintFieldIsInvalid()
    {
        AskpassWindow window = Show(AskpassPromptKind.HostVerification);
        try
        {
            TextBox input = Field<TextBox>(window, "AskpassFingerprint"); input.Text = " \n "; input.Focus(); Pointer(window, "YesAskpass");
            Assert.That(window.Result.Decision, Is.EqualTo(AskpassDecision.Yes)); Assert.That(input.Text, Is.Empty); AssertOutput(window, 0, "yes");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void NotificationHasNoAuthenticationInputAndEveryClosingRouteOnlyDismissesIt()
    {
        foreach (string route in new[] { "close", "enter", "escape", "window" })
        {
            AskpassWindow window = Show(AskpassPromptKind.Notification, "Password (yes/no)? This message must only be displayed.");
            try
            {
                Assert.That(window.GetVisualDescendants().OfType<TextBox>(), Is.Empty);
                foreach (string name in new[] { "YesAskpass", "NoAskpass", "AcceptAskpass", "CancelAskpass", "UseAskpassFingerprint" }) { Assert.That(Optional<Button>(window, name), Is.Null); }
                if (route == "close") { Pointer(window, "CloseAskpass"); }
                else if (route == "enter") { Press(window, PhysicalKey.Enter); }
                else if (route == "escape") { Press(window, PhysicalKey.Escape); }
                else { window.Close(); }
                Assert.That(window.IsVisible, Is.False); Assert.That(window.Result.Decision, Is.EqualTo(AskpassDecision.Dismissed));
                Assert.That(window.Result.Value, Is.Null); Assert.That(window.Response, Is.Null); AssertOutput(window, 0, "");
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTest]
    public void CredentialsAcceptActualTextInputAndEnterWithOneLiteralLfAndClearTheirUiBuffers()
    {
        foreach (AskpassPromptKind kind in new[] { AskpassPromptKind.Secret, AskpassPromptKind.Username })
        {
            AskpassWindow window = Show(kind); TextBox input = Field<TextBox>(window, "AskpassInput");
            try
            {
                Assert.That(input.IsKeyboardFocusWithin, Is.True); Assert.That(input.PasswordChar != '\0', Is.EqualTo(kind == AskpassPromptKind.Secret)); Assert.That(input.RevealPassword, Is.False);
                const string value = " fictitious λ '$() & response "; window.KeyTextInput(value); Pump(window); Assert.That(input.Text, Is.EqualTo(value));
                Press(window, PhysicalKey.Enter); Assert.That(window.Result.Decision, Is.EqualTo(AskpassDecision.Submit)); Assert.That(window.Result.Value, Is.EqualTo(value));
                Assert.That(input.Text, Is.Empty); AssertOutput(window, 0, value + "\n");
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTest]
    public void InvalidCredentialInputCannotBeSubmittedByEnterOrByRaisingTheDisabledButton()
    {
        foreach (string invalid in new[] { "line\nbreak", "line\rbreak", "nul\0byte" })
        {
            AskpassWindow window = Show(AskpassPromptKind.Secret);
            try
            {
                TextBox input = Field<TextBox>(window, "AskpassInput"); input.Text = invalid;
                Button accept = Field<Button>(window, "AcceptAskpass"); Assert.That(accept.IsEnabled, Is.False);
                Press(window, PhysicalKey.Enter); accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.That(window.IsVisible, Is.True);
                Assert.That(window.Result.Decision, Is.EqualTo(AskpassDecision.Cancelled)); Assert.That(window.Response, Is.Null); AssertOutput(window, 1, "");
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTest]
    public async Task PromptIsLiteralSelectableAndCopyableAndTheStringConstructorNeverReadsAnAmbientModeHint()
    {
        string? previousHint = Environment.GetEnvironmentVariable("SSH_ASKPASS_PROMPT");
        const string literal = "Unknown request & <tag> '$()'\tλ\nSecond line: (confirm)";
        Environment.SetEnvironmentVariable("SSH_ASKPASS_PROMPT", "confirm"); AskpassWindow window = new(literal); window.Show(); Pump(window);
        try
        {
            Assert.That(window.Request.Kind, Is.EqualTo(AskpassPromptKind.Secret)); Assert.That(Field<TextBox>(window, "AskpassInput").PasswordChar, Is.Not.EqualTo('\0'));
            SelectableTextBlock prompt = Field<SelectableTextBlock>(window, "AskpassPrompt"); Assert.That(prompt.Text, Is.EqualTo(literal)); Assert.That(window.Request.Prompt, Is.EqualTo(literal));
            var clipboard = window.Clipboard!; string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard);
            try
            {
                Assert.That(prompt.Focus(), Is.True); Press(window, PhysicalKey.A, RawInputModifiers.Control); Press(window, PhysicalKey.C, RawInputModifiers.Control);
                Stopwatch timer = Stopwatch.StartNew(); string? copied = null;
                while (copied != literal && timer.Elapsed < TimeSpan.FromSeconds(5)) { Dispatcher.UIThread.RunJobs(); copied = await ClipboardExtensions.TryGetTextAsync(clipboard); if (copied != literal) { await Task.Delay(10); } }
                Assert.That(copied, Is.EqualTo(literal)); window.Close(); Assert.That(prompt.Text, Is.Empty);
            }
            finally { await ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""); }
        }
        finally { window.Close(); Environment.SetEnvironmentVariable("SSH_ASKPASS_PROMPT", previousHint); }
    }

    [AvaloniaTest]
    public void CompactOriginalControlsAndTranslatedDecisionsStayVisibleInBothThemesForEveryMode()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (AskpassPromptKind kind in Enum.GetValues<AskpassPromptKind>())
            {
                AskpassWindow window = Show(kind, "Literal fake authentication prompt\nSecond line: λ 'quoted' $()", theme);
                try
                {
                    Assert.That(window.Title, Is.EqualTo("Git Extensions - git CLI stdin wrapper")); Assert.That(window.CanResize, Is.False);
                    bool credentials = kind is AskpassPromptKind.Secret or AskpassPromptKind.Username;
                    Assert.That(window.ClientSize.Width, Is.EqualTo(credentials ? 360 : kind == AskpassPromptKind.Notification ? 450 : 480));
                    Assert.That(window.ClientSize.Height, Is.InRange(80, 210));
                    Assert.That(window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Send input"), Is.False);
                    foreach (Control control in window.GetVisualDescendants().OfType<Control>().Where(control => control.Name is "AskpassInput" or "AskpassFingerprint" or "AcceptAskpass" or "CancelAskpass" or "YesAskpass" or "NoAskpass" or "UseAskpassFingerprint" or "CloseAskpass"))
                    { AssertInside(window, control); Assert.That(control.Bounds.Height, Is.EqualTo(25), control.Name); }
                    if (credentials) { Assert.That(Field<Button>(window, "CancelAskpass").Content, Is.EqualTo("Annuler")); Assert.That(window.ClientSize.Height, Is.InRange(110, 135)); }
                    else if (kind == AskpassPromptKind.Notification) { Assert.That(Field<Button>(window, "CloseAskpass").Content, Is.EqualTo("Fermer")); }
                    else { Assert.That(Field<Button>(window, "YesAskpass").Content, Is.EqualTo("Oui")); Assert.That(Field<Button>(window, "NoAskpass").Content, Is.EqualTo("Non")); }
                    Capture(window, "askpass-" + kind + "-" + theme + ".png");
                }
                finally { window.Close(); }
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [AvaloniaTest]
    public void LongHostVerificationPromptHasABoundedScrollableSurfaceAndKeepsEveryDecisionReachable()
    {
        string prompt = string.Join('\n', Enumerable.Repeat("Literal host verification detail ' λ $() & <not-markup>", 70));
        AskpassWindow window = Show(AskpassPromptKind.HostVerification, prompt, ThemeVariant.Dark);
        try
        {
            ScrollViewer scroll = Field<ScrollViewer>(window, "AskpassPromptScroll"); Assert.That(scroll.Bounds.Height, Is.LessThanOrEqualTo(240));
            Assert.That(scroll.Extent.Height, Is.GreaterThan(scroll.Viewport.Height)); Assert.That(window.ClientSize.Height, Is.LessThan(430));
            foreach (string name in new[] { "AskpassFingerprint", "UseAskpassFingerprint", "YesAskpass", "NoAskpass" }) { AssertInside(window, Field<Control>(window, name)); }
            scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height); Pump(window); Assert.That(scroll.Offset.Y, Is.GreaterThan(0));
            Assert.That(Field<SelectableTextBlock>(window, "AskpassPrompt").Text, Is.EqualTo(prompt)); Capture(window, "askpass-HostVerification-long-Dark.png");
            Pointer(window, "NoAskpass"); AssertOutput(window, 0, "no");
        }
        finally { window.Close(); }
    }

    private static AskpassWindow Show(AskpassPromptKind kind, string prompt = "Literal fake authentication request?", ThemeVariant? theme = null)
    { AskpassWindow window = new(new AskpassRequest(prompt, kind)) { RequestedThemeVariant = theme ?? ThemeVariant.Light }; window.Show(); Pump(window); return window; }
    private static T Field<T>(TopLevel root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static T? Optional<T>(TopLevel root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name);
    private static void Pointer(TopLevel root, string name) => PushProcessTestDriver.Pointer(Field<Control>(root, name));
    private static void Pump(Window root) { for (int frame = 0; frame < 3; frame++) { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } }
    private static void Press(Window root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) { root.KeyPressQwerty(key, modifiers); if (root.IsVisible) { root.KeyReleaseQwerty(key, modifiers); } Pump(root); }
    private static void AssertOutput(AskpassWindow window, int code, string expected)
    {
        using MemoryStream bytes = new(); using StreamWriter output = new(bytes, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n" };
        Assert.That(AskpassApplication.WriteResult(window.Request, window.Result, output), Is.EqualTo(code)); output.Flush();
        Assert.That(bytes.ToArray(), Is.EqualTo(Encoding.UTF8.GetBytes(expected)), "The helper must emit only exact protocol bytes, without a BOM or implicit CRLF.");
    }
    private static void AssertInside(Window root, Control control)
    { Rect bounds = new(control.TranslatePoint(default, root)!.Value, control.Bounds.Size); Assert.That(control.IsEffectivelyVisible, Is.True, control.Name); Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(0), control.Name); Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0), control.Name); Assert.That(bounds.Right, Is.LessThanOrEqualTo(root.ClientSize.Width), control.Name); Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(root.ClientSize.Height), control.Name); }
    private static void Capture(Window root, string name)
    { Pump(root); using var frame = root.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string folder = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); frame!.Save(path); TestContext.AddTestAttachment(path); }
}

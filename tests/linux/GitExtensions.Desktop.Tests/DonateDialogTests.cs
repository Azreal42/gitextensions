using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DonateDialogTests
{
    [AvaloniaTest]
    public void BadgeIsTheLinkedOriginalWindowsImageWithoutAnIndependentCopy()
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/Donate.png"));
        Assert.That(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            Is.EqualTo("7168a3f5a5a4b474b99b1ce5b945cbfa79a12f7d4aff2f6a8c9c52a07dec43b5"));
    }

    [AvaloniaTest]
    [TestCase("en-US", false)] [TestCase("en-US", true)]
    [TestCase("fr-FR", false)] [TestCase("fr-FR", true)]
    public async Task OriginalTextBadgeAndCompactLayoutAreTranslatedAndOnlyItsRealClickOpensTheFixedSite(string language, bool dark)
    {
        List<Uri> opened = [];
        TranslationCatalog text = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
        Window owner = new() { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        DonateDialog dialog = new(text, opened.Add) { RequestedThemeVariant = owner.RequestedThemeVariant };
        try
        {
            Assert.That(opened, Is.Empty); owner.Show(); Task completion = dialog.ShowDialog(owner); Pump(dialog);
            Button badge = Field<Button>(dialog, "pbxDonate"); TextBlock explanation = Field<TextBlock>(dialog, "lblText");
            Image image = Field<Image>(dialog, "DonateBadge");
            Assert.Multiple(() =>
            {
                Assert.That(dialog.Owner, Is.SameAs(owner)); Assert.That(dialog.ShowInTaskbar, Is.False);
                Assert.That(dialog.Width, Is.EqualTo(508)); Assert.That(dialog.ClientSize.Height, Is.GreaterThanOrEqualTo(266));
                Assert.That(dialog.Title, Is.EqualTo(language.StartsWith("fr", StringComparison.Ordinal) ? "Faire un don" : "Donate"));
                Assert.That(explanation.Text, Does.StartWith(language.StartsWith("fr", StringComparison.Ordinal) ? "Nous disposons" : "We have a dedicated team"));
                Assert.That(explanation.Text!.Replace("\r\n", "\n", StringComparison.Ordinal), Does.Contain("\n\n")); Assert.That(explanation.FontFamily, Is.EqualTo(dialog.FontFamily));
                Assert.That(explanation.TextLayout.Height, Is.LessThanOrEqualTo(explanation.Bounds.Height + 1), "Translated text must not be clipped.");
                Assert.That(((Bitmap)image.Source!).PixelSize, Is.EqualTo(new PixelSize(460, 96)));
                Assert.That(AutomationProperties.GetName(badge), Is.EqualTo(dialog.Title));
                Assert.That(dialog.GetVisualDescendants().OfType<Button>().Count(), Is.EqualTo(1), "Windows exposes just the donation badge, not invented payment/copy actions.");
                Assert.That(opened, Is.Empty, "Construction, layout and focus cannot navigate.");
            });
            Rect bodyBounds = new(explanation.TranslatePoint(default, dialog)!.Value, explanation.Bounds.Size);
            Rect badgeBounds = new(badge.TranslatePoint(default, dialog)!.Value, badge.Bounds.Size);
            Assert.That(badgeBounds.Top, Is.GreaterThanOrEqualTo(bodyBounds.Bottom));
            Assert.That(new Rect(default, dialog.ClientSize).Contains(badgeBounds), Is.True);
            Assert.That(badgeBounds.Height, Is.GreaterThanOrEqualTo(35));
            PushProcessTestDriver.Pointer(explanation); Assert.That(opened, Is.Empty, "Clicking the explanation cannot navigate.");
            PushProcessTestDriver.Pointer(badge); Pump(dialog);
            Assert.That(opened.Select(uri => uri.AbsoluteUri), Is.EqualTo(new[] { "https://opencollective.com/gitextensions" }));
            Assert.That(dialog.IsVisible, Is.True, "Opening information in the browser is not a donation or a dialog result.");
            Capture(dialog, $"donate-{language}-{(dark ? "Dark" : "Light")}.png");
            Press(dialog, PhysicalKey.Escape); Pump(owner); await completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(owner.IsVisible, Is.True); Assert.That(opened.Count, Is.EqualTo(1)); Assert.That(image.Source, Is.Null);
        }
        finally { if (dialog.IsVisible) { dialog.Close(); } owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task RealPointerHoverAndKeyboardFocusRenderTheSharedHistoricalBorderColors(bool dark)
    {
        Window owner = new() { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        List<Uri> opened = []; DonateDialog dialog = new(openUrl: opened.Add) { RequestedThemeVariant = owner.RequestedThemeVariant };
        try
        {
            owner.Show(); Task completion = dialog.ShowDialog(owner); Pump(dialog);
            Button badge = Field<Button>(dialog, "pbxDonate"); Border border = Field<Border>(dialog, "DonateBadgeBorder");
            Point point = badge.TranslatePoint(new Point(badge.Bounds.Width / 2, badge.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseMove(point); Pump(dialog);
            Color link = Color.Parse(ClassicStyles.OriginalColors(dark)["HotTrack"]);
            Assert.That(((ISolidColorBrush)border.BorderBrush!).Color, Is.EqualTo(link)); AssertBorderPixel(dialog, border, link);
            dialog.MouseMove(new Point(2, 2)); Press(dialog, PhysicalKey.Tab); Pump(dialog);
            Assert.That(badge.IsKeyboardFocusWithin, Is.True);
            Color focus = Color.Parse(ClassicStyles.OriginalColors(dark)["Highlight"]);
            Assert.That(((ISolidColorBrush)border.BorderBrush!).Color, Is.EqualTo(focus)); AssertBorderPixel(dialog, border, focus);
            Assert.That(opened, Is.Empty); Capture(dialog, $"donate-focus-{(dark ? "Dark" : "Light")}.png");
            Press(dialog, PhysicalKey.Escape); Pump(owner); await completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { if (dialog.IsVisible) { dialog.Close(); } owner.Close(); }
    }

    [AvaloniaTest]
    [TestCase(PhysicalKey.Enter)] [TestCase(PhysicalKey.Space)]
    public async Task KeyboardActivationOnTheFocusedBadgeOpensOnlyTheCapturedOriginalDestination(PhysicalKey key)
    {
        List<Uri> opened = []; Window owner = new(); DonateDialog dialog = new(openUrl: opened.Add);
        try
        {
            owner.Show(); Task completion = dialog.ShowDialog(owner); Pump(dialog);
            Assert.That(Field<Button>(dialog, "pbxDonate").IsKeyboardFocusWithin, Is.True); Assert.That(opened, Is.Empty);
            Press(dialog, key); Pump(dialog);
            Assert.That(opened.Select(uri => uri.AbsoluteUri), Is.EqualTo(new[] { "https://opencollective.com/gitextensions" }));
            Assert.That(dialog.IsVisible, Is.True); dialog.Close(); Pump(owner); await completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(owner.IsVisible, Is.True); Assert.That(opened.Count, Is.EqualTo(1));
        }
        finally { if (dialog.IsVisible) { dialog.Close(); } owner.Close(); }
    }

    [AvaloniaTest]
    public async Task WindowCloseBeforeActivationNeverNavigatesAndReturnsToTheOwner()
    {
        List<Uri> opened = []; Window owner = new(); DonateDialog dialog = new(openUrl: opened.Add);
        try
        {
            owner.Show(); Task completion = dialog.ShowDialog(owner); Pump(dialog); dialog.Close(); Pump(owner);
            await completion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.That(opened, Is.Empty); Assert.That(owner.IsVisible, Is.True);
        }
        finally { if (dialog.IsVisible) { dialog.Close(); } owner.Close(); }
    }

    private static T Field<T>(Window root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static void Press(Window window, PhysicalKey key)
    { window.KeyPressQwerty(key, RawInputModifiers.None); if (window.IsVisible) { window.KeyReleaseQwerty(key, RawInputModifiers.None); } Pump(window); }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void AssertBorderPixel(Window window, Border border, Color expected)
    {
        using WriteableBitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        Point point = border.TranslatePoint(new Point(border.Bounds.Width / 2, 0), window)!.Value;
        using ILockedFramebuffer pixels = frame!.Lock(); byte[] data = new byte[4];
        int x = (int)Math.Round(point.X * window.RenderScaling), y = (int)Math.Round(point.Y * window.RenderScaling);
        Marshal.Copy(pixels.Address + y * pixels.RowBytes + x * 4, data, 0, data.Length);
        Color actual = pixels.Format == PixelFormat.Bgra8888 ? Color.FromArgb(data[3], data[2], data[1], data[0]) : Color.FromArgb(data[3], data[0], data[1], data[2]);
        Assert.That(actual, Is.EqualTo(expected), "The template must actually draw the shared border color.");
    }
    private static void Capture(TopLevel window, string name)
    {
        Pump(window); using WriteableBitmap? image = window.CaptureRenderedFrame(); Assert.That(image, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); image!.Save(path); TestContext.AddTestAttachment(path);
    }
}

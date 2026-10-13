using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class OriginalCommandHelpTests
{
    [AvaloniaTest]
    public void RealPointerHoverShowsTheOriginalFastForwardDiagramOnlyWhileEnabledAndInside()
    {
        using Fixture f = new(); using OriginalCommandHelp help = new("HelpCommandMerge", "HelpCommandMergeFastForward", "merge", "Hover to see scenario when fast forward is possible.") { UseAlternateOnHover = true };
        Window window = f.Show(help); Image image = Field<Image>(help, "OriginalCommandHelpImage");
        Assert.That(help.IsExpanded, Is.True); Assert.That(image.Bounds.Size, Is.EqualTo(new Size(289, 373)));
        Assert.That(image.Tag, Is.EqualTo("HelpCommandMerge.png"));
        Assert.That(Field<TextBlock>(help, "OriginalCommandHelpNotice").IsVisible, Is.True);
        window.MouseMove(Center(image)); Pump(window);
        Assert.That(image.Tag, Is.EqualTo("HelpCommandMergeFastForward.png")); Capture(window, "command-help-merge-hover-light.png");
        help.UseAlternateOnHover = false; Pump(window);
        Assert.That(image.Tag, Is.EqualTo("HelpCommandMerge.png")); Assert.That(Field<TextBlock>(help, "OriginalCommandHelpNotice").IsVisible, Is.False);
        help.UseAlternateOnHover = true; window.MouseMove(new Point(window.ClientSize.Width - 2, window.ClientSize.Height - 2)); Pump(window);
        Assert.That(image.Tag, Is.EqualTo("HelpCommandMerge.png"));
        window.RequestedThemeVariant = ThemeVariant.Dark; Pump(window); window.MouseMove(Center(image)); Pump(window);
        Assert.That(image.Tag, Is.EqualTo("HelpCommandMergeFastForward.png")); Capture(window, "command-help-merge-hover-dark.png");
    }

    [AvaloniaTest]
    public void PointerHideAndKeyboardShowPersistIndependentSettingsIdsWithoutChangingOtherHelp()
    {
        using Fixture f = new(); using OriginalCommandHelp merge = new("HelpCommandMerge.png", settingsId: "merge");
        Window window = f.Show(merge); int changes = 0; merge.ExpandedChanged += (_, _) => changes++;
        Button toggle = Field<Button>(merge, "OriginalCommandHelpToggle"); PointerClick(toggle); Pump(window);
        Assert.That(merge.IsExpanded, Is.False); Assert.That(Field<Image>(merge, "OriginalCommandHelpImage").IsVisible, Is.False);
        Assert.That(Field<Image>(merge, "OriginalCommandHelpImage").Source, Is.Null); Assert.That(changes, Is.EqualTo(1));
        using OriginalCommandHelp reopened = new("HelpCommandMerge", settingsId: "merge");
        using OriginalCommandHelp rebase = new("HelpCommandRebase", settingsId: "rebase");
        Assert.That(reopened.IsExpanded, Is.False); Assert.That(rebase.IsExpanded, Is.True);
        toggle.Focus(); window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None); Pump(window);
        Assert.That(merge.IsExpanded, Is.True); Assert.That(changes, Is.EqualTo(2));
        using OriginalCommandHelp afterKeyboard = new("HelpCommandMerge", settingsId: "merge"); Assert.That(afterKeyboard.IsExpanded, Is.True);
        Window rebaseWindow = f.Show(rebase); PointerClick(Field<Button>(rebase, "OriginalCommandHelpToggle")); Pump(rebaseWindow);
        using OriginalCommandHelp afterRebase = new("HelpCommandRebase", settingsId: "rebase"); Assert.That(afterRebase.IsExpanded, Is.False);
        using OriginalCommandHelp finalMerge = new("HelpCommandMerge", settingsId: "merge"); Assert.That(finalMerge.IsExpanded, Is.True);
        using OriginalCommandHelp transient = new("HelpCommandRebase"); Window transientWindow = f.Show(transient); PointerClick(Field<Button>(transient, "OriginalCommandHelpToggle"));
        using OriginalCommandHelp freshTransient = new("HelpCommandRebase"); Assert.That(freshTransient.IsExpanded, Is.True);
    }

    [AvaloniaTest]
    public void RealColoredAssetsKeepLightPixelsAndAlphaAcrossOwnerThemesAndDetachReattach()
    {
        using Fixture f = new(); using OriginalCommandHelp help = new("HelpCommandRebase", settingsId: "rebase");
        Window window = f.Show(help); Image image = Field<Image>(help, "OriginalCommandHelpImage");
        using Stream stream = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Help/HelpCommandRebase.png")); using Bitmap original = new(stream);
        byte[] source = Pixels(original); Assert.That(Pixels((Bitmap)image.Source!), Is.EqualTo(source));
        Capture(window, "command-help-rebase-light.png");
        byte[]? dark = null;
        for (int pass = 0; pass < 2; pass++)
        {
            window.RequestedThemeVariant = ThemeVariant.Dark; Pump(window);
            Assert.That(help.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
            byte[] adapted = Pixels((Bitmap)image.Source!); Assert.That(adapted, Is.Not.EqualTo(source));
            Assert.That(adapted.Where((_, index) => index % 4 == 3), Is.EqualTo(source.Where((_, index) => index % 4 == 3)), "Preserve all source alpha bytes.");
            if (dark is not null) { Assert.That(adapted, Is.EqualTo(dark)); } dark = adapted;
            Capture(window, "command-help-rebase-dark.png");
            window.RequestedThemeVariant = ThemeVariant.Light; Pump(window); Assert.That(Pixels((Bitmap)image.Source!), Is.EqualTo(source));
        }
        Bitmap first = (Bitmap)image.Source!; Grid previousHost = (Grid)window.Content!;
        window.Content = null; Pump(window); Assert.That(image.Source, Is.Null);
        Assert.That(previousHost.Children.Remove(help), Is.True); Assert.That(help.Parent, Is.Null);
        window.RequestedThemeVariant = ThemeVariant.Dark; window.Content = f.Host(help); Pump(window);
        Assert.That(image.Source, Is.Not.SameAs(first)); Assert.That(Pixels((Bitmap)image.Source!), Is.EqualTo(dark));
        Assert.That(help.IsExpanded, Is.True); Assert.That(image.Bounds.Size, Is.EqualTo(new Size(289, 373)));
    }

    [AvaloniaTest]
    public void OriginalPerceivedLightnessGoldenVectorsPreserveColoredHueAndFractionalAlpha()
    {
        // Derived from Windows ColorHelper.Gamma + LightnessCorrection + HslColor.ToColor,
        // with WindowText white and Window black. This is not a gray inversion.
        using WriteableBitmap source = new(new PixelSize(5, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        byte[] input = [0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 255, 128, 0, 255, 0, 255, 255, 0, 0, 255];
        using (ILockedFramebuffer buffer = source.Lock()) { Marshal.Copy(input, 0, buffer.Address, input.Length); }
        using Bitmap adapted = OriginalCommandHelpBitmap.Adapt(source, Colors.White, Colors.Black);
        Assert.That(Pixels(adapted), Is.EqualTo(new byte[] { 255, 255, 255, 255, 0, 0, 0, 255, 92, 92, 255, 128, 0, 83, 0, 255, 255, 204, 204, 255 }));
        Assert.That(Pixels(source), Is.EqualTo(input), "Transform never changes the original bitmap.");
    }

    [AvaloniaTest]
    public void FailedPreferenceWriteIsVisibleAndDoesNotClaimTheCollapsedChoiceWasSaved()
    {
        using Fixture f = new(); using OriginalCommandHelp help = new("HelpCommandMerge", settingsId: "merge"); Window window = f.Show(help);
        Directory.CreateDirectory(Path.Combine(f.Root, "gitextensions", "command-help-preferences.json"));
        PointerClick(Field<Button>(help, "OriginalCommandHelpToggle")); Pump(window);
        Assert.That(help.IsExpanded, Is.False); Assert.That(help.LastError, Is.Not.Null.And.Not.Empty);
        Assert.That(Field<TextBlock>(help, "OriginalCommandHelpNotice").IsVisible, Is.True);
        Assert.That(Field<TextBlock>(help, "OriginalCommandHelpNotice").Text, Does.Contain(help.LastError));
        Directory.Delete(Path.Combine(f.Root, "gitextensions", "command-help-preferences.json"));
        using OriginalCommandHelp next = new("HelpCommandMerge", settingsId: "merge"); Assert.That(next.IsExpanded, Is.True);
    }

    private static T Field<T>(Control control, string name) where T : Control => control.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static Point Center(Control control)
    { TopLevel top = TopLevel.GetTopLevel(control)!; Pump(top); return control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value; }
    private static void PointerClick(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; Point center = Center(control);
        Assert.That(control.IsEffectivelyVisible && control.IsEnabled && new Rect(default, top.ClientSize).Contains(center), Is.True);
        Assert.That((top.InputHitTest(center) as Visual)?.GetSelfAndVisualAncestors().Contains(control), Is.True);
        top.MouseMove(center); top.MouseDown(center, MouseButton.Left); top.MouseUp(center, MouseButton.Left); Pump(top);
    }
    private static byte[] Pixels(Bitmap bitmap)
    {
        using WriteableBitmap copy = new(bitmap.PixelSize, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using ILockedFramebuffer buffer = copy.Lock(); bitmap.CopyPixels(buffer, AlphaFormat.Unpremul);
        byte[] raw = new byte[buffer.RowBytes * buffer.Size.Height]; Marshal.Copy(buffer.Address, raw, 0, raw.Length);
        byte[] packed = new byte[buffer.Size.Width * buffer.Size.Height * 4];
        for (int y = 0; y < buffer.Size.Height; y++) { Array.Copy(raw, y * buffer.RowBytes, packed, y * buffer.Size.Width * 4, buffer.Size.Width * 4); }
        return packed;
    }
    private static void Capture(TopLevel window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly List<Window> _windows = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-command-help-" + Guid.NewGuid().ToString("N"));
        public Fixture() { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Root); Directory.CreateDirectory(Root); }
        public Control Host(Control help) => new Grid { Margin = new Thickness(12), Children = { help } };
        public Window Show(Control help)
        { help.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left; Window window = new() { Width = 520, Height = 500, RequestedThemeVariant = ThemeVariant.Light, Content = Host(help) }; _windows.Add(window); window.Show(); Pump(window); return window; }
        public void Dispose()
        { foreach (Window window in _windows) { window.Close(); } Dispatcher.UIThread.RunJobs(); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdg); if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }
}
